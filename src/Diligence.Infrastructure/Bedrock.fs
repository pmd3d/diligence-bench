namespace Diligence.Infrastructure

open System
open System.IO
open System.Text.Json
open System.Text.RegularExpressions
open System.Threading
open System.Threading.Tasks
open System.Collections.Generic
open Amazon.BedrockRuntime
open Amazon
open Amazon.Runtime.Documents
open Amazon.BedrockRuntime.Model
open Diligence.Core

type ModelReply = { Memo: string; InputTokens: int option; OutputTokens: int option }
type IResearchModel = abstract Complete: ResearchRequest * string * CancellationToken -> Task<ModelReply>

module Bedrock =
    let clientConfig region =
        let config = AmazonBedrockRuntimeConfig(RegionEndpoint = RegionEndpoint.GetBySystemName(region))
        config.AuthSchemePreference <- List<string>([ "sigv4" ])
        config

    let private documentFromJson (json: string) =
        use parsed = JsonDocument.Parse(json)
        Document.FromObject(parsed.RootElement)

    let rec private documentValue (document: Document) : obj =
        if document.IsNull() then null
        elif document.IsBool() then box (document.AsBool())
        elif document.IsInt() then box (document.AsInt())
        elif document.IsLong() then box (document.AsLong())
        elif document.IsDouble() then box (document.AsDouble())
        elif document.IsString() then box (document.AsString())
        elif document.IsList() then
            document.AsList()
            |> Seq.map documentValue
            |> ResizeArray<obj>
            |> box
        elif document.IsDictionary() then
            let values = Dictionary<string, obj>()
            for KeyValue(key, value) in document.AsDictionary() do
                values[key] <- documentValue value
            box values
        else
            invalidOp $"Unsupported Bedrock document type: {document.Type}"

    let private documentToJson document =
        JsonSerializer.Serialize(documentValue document)

    let toolConfiguration (definitions: ToolDefinition list) =
        let tools =
            definitions
            |> List.map (fun definition ->
                Tool(ToolSpec = ToolSpecification(
                    Name = definition.Name,
                    Description = definition.Description,
                    InputSchema = ToolInputSchema(Json = documentFromJson definition.InputSchema))))
        ToolConfiguration(Tools = List<Tool>(tools))

    let usesTextToolProtocol (modelId: string) =
        modelId.StartsWith("deepseek.", StringComparison.OrdinalIgnoreCase)
        || modelId.Contains("deepseek", StringComparison.OrdinalIgnoreCase)

    let textToolInstructions (definitions: ToolDefinition list) =
        let descriptions =
            definitions
            |> List.map (fun definition -> $"- {definition.Name}: {definition.Description}\n  Input schema: {definition.InputSchema}")
            |> String.concat "\n"
        $"""

## Tool calling protocol

The following client-executed tools are available:
{descriptions}

When you need a tool, do not emit Python or describe the intended call. Respond with only this XML-shaped envelope, using valid JSON inside `params`:

<tool_calls>
<tool_call>
<id>unique-call-id</id>
<function>tool_name</function>
<params>{{"argument":"value"}}</params>
</tool_call>
</tool_calls>

You may include multiple `tool_call` elements. After the application returns tool results, either request another tool using the same protocol or provide the final answer. Never present a tool-call envelope as the final answer.
"""

    let textToolRequests (text: string) =
        let pattern = @"<(?<call>sec_call|tool_call)>\s*(?:<id>(?<id>.*?)</id>\s*)?<(?:function|name)>(?<name>.*?)</(?:function|name)>\s*<(?:params|arguments)>(?<arguments>.*?)</(?:params|arguments)>\s*</\k<call>>"
        Regex.Matches(text, pattern, RegexOptions.IgnoreCase ||| RegexOptions.Singleline)
        |> Seq.cast<Match>
        |> Seq.mapi (fun index found ->
            let arguments = found.Groups["arguments"].Value.Trim()
            let suppliedId = found.Groups["id"].Value.Trim()
            {
                Id = if String.IsNullOrWhiteSpace(suppliedId) then $"text-call-{index + 1}-{Guid.NewGuid():N}" else suppliedId
                Name = found.Groups["name"].Value.Trim()
                Arguments = arguments
            })
        |> Seq.toList

    let modelTurn (response: ConverseResponse) =
        let content = response.Output.Message.Content
        let text =
            content
            |> Seq.choose (fun block -> if String.IsNullOrWhiteSpace(block.Text) then None else Some block.Text)
            |> String.concat "\n"
            |> fun value -> if String.IsNullOrWhiteSpace(value) then None else Some value
        let requests =
            content
            |> Seq.choose (fun block ->
                if isNull block.ToolUse then None
                else
                    Some {
                        Id = block.ToolUse.ToolUseId
                        Name = block.ToolUse.Name
                        Arguments = documentToJson block.ToolUse.Input
                    })
            |> Seq.toList
        let inputTokens =
            if isNull response.Usage || not response.Usage.InputTokens.HasValue then None
            else Some response.Usage.InputTokens.Value
        let outputTokens =
            if isNull response.Usage || not response.Usage.OutputTokens.HasValue then None
            else Some response.Usage.OutputTokens.Value
        { Text = text; ToolRequests = requests; InputTokens = inputTokens; OutputTokens = outputTokens }

    let toolResultMessage (results: ToolResult list) =
        let content =
            results
            |> List.map (fun result ->
                ContentBlock(ToolResult = ToolResultBlock(
                    ToolUseId = result.ToolUseId,
                    Content = List<ToolResultContentBlock>([ ToolResultContentBlock(Text = result.Content) ]))))
        Message(Role = ConversationRole.User, Content = List<ContentBlock>(content))

    let textToolResultMessage (results: ToolResult list) =
        let payload =
            results
            |> List.map (fun result -> {| tool_use_id = result.ToolUseId; is_error = result.IsError; content = result.Content |})
            |> JsonSerializer.Serialize
        Message(Role = ConversationRole.User, Content = List<ContentBlock>([
            ContentBlock(Text = $"<tool_results>{payload}</tool_results>\nUse these results to continue. Request another tool if needed; otherwise provide the final answer.")
        ]))

type ReplayModel(responsePath: string) =
    interface IResearchModel with
        member _.Complete(_, _, cancellationToken) = task {
            cancellationToken.ThrowIfCancellationRequested()
            let! memo = File.ReadAllTextAsync(responsePath, cancellationToken)
            return { Memo = memo; InputTokens = None; OutputTokens = None }
        }
    interface IResearchConversation with
        member _.Start(_, _, _, cancellationToken) = task {
            cancellationToken.ThrowIfCancellationRequested()
            let! response = File.ReadAllTextAsync(responsePath, cancellationToken)
            try
                use document = Text.Json.JsonDocument.Parse(response)
                let turns = document.RootElement.GetProperty("turns").EnumerateArray() |> Seq.toArray
                let value = turns[0]
                let text = if value.TryGetProperty("text") |> fst then Some(value.GetProperty("text").GetString()) else None
                let calls = if value.TryGetProperty("tool_requests") |> fst then value.GetProperty("tool_requests").EnumerateArray() |> Seq.map (fun call -> { Id = call.GetProperty("id").GetString(); Name = call.GetProperty("name").GetString(); Arguments = call.GetProperty("arguments").GetRawText() }) |> Seq.toList else []
                return { Text = text; ToolRequests = calls; InputTokens = None; OutputTokens = None }
            with :? Text.Json.JsonException -> return { Text = Some response; ToolRequests = []; InputTokens = None; OutputTokens = None }
        }
        member _.Continue(_, cancellationToken) = task {
            cancellationToken.ThrowIfCancellationRequested()
            let! response = File.ReadAllTextAsync(responsePath, cancellationToken)
            try
                use document = Text.Json.JsonDocument.Parse(response)
                let turns = document.RootElement.GetProperty("turns").EnumerateArray() |> Seq.toArray
                let value = turns[1]
                let text = if value.TryGetProperty("text") |> fst then Some(value.GetProperty("text").GetString()) else None
                return { Text = text; ToolRequests = []; InputTokens = None; OutputTokens = None }
            with :? Text.Json.JsonException -> return { Text = Some response; ToolRequests = []; InputTokens = None; OutputTokens = None }
        }
type BedrockModel(region: string, modelId: string) =
    let client = new AmazonBedrockRuntimeClient(Bedrock.clientConfig region)
    let messages = ResizeArray<Message>()
    let mutable system = List<SystemContentBlock>()
    let mutable toolConfig: ToolConfiguration = null
    let mutable started = false
    let textTools = Bedrock.usesTextToolProtocol modelId

    let converse cancellationToken = task {
        let conversation = new ConverseRequest(ModelId = modelId)
        conversation.System <- system
        conversation.Messages <- List<Message>(messages)
        conversation.ToolConfig <- toolConfig
        let! response = client.ConverseAsync(conversation, cancellationToken)
        messages.Add(response.Output.Message)
        let turn = Bedrock.modelTurn response
        if textTools && turn.ToolRequests.IsEmpty then
            return { turn with ToolRequests = turn.Text |> Option.map Bedrock.textToolRequests |> Option.defaultValue [] }
        else
            return turn
    }

    interface IResearchModel with
        member _.Complete(_, _, _) = Task.FromException<ModelReply>(NotSupportedException($"Live Bedrock Converse is not enabled yet for '{modelId}'. Use --replay-response for recorded-response runs."))
    interface IResearchConversation with
        member _.Start(request, systemPrompt, definitions, cancellationToken) = task {
            messages.Clear()
            let effectivePrompt = if textTools then systemPrompt + Bedrock.textToolInstructions definitions else systemPrompt
            system <- List<SystemContentBlock>([ SystemContentBlock(Text = effectivePrompt) ])
            toolConfig <- if textTools then null else Bedrock.toolConfiguration definitions
            messages.Add(Message(Role = ConversationRole.User, Content = List<ContentBlock>([ ContentBlock(Text = $"Ticker: {request.Ticker}\nQuestion: {request.Question}") ])))
            started <- true
            return! converse cancellationToken
        }
        member _.Continue(results, cancellationToken) = task {
            if not started then invalidOp "The Bedrock conversation has not been started."
            messages.Add(if textTools then Bedrock.textToolResultMessage results else Bedrock.toolResultMessage results)
            return! converse cancellationToken
        }
