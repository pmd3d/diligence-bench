namespace Diligence.Infrastructure

open System
open System.IO
open System.Threading
open System.Threading.Tasks
open System.Collections.Generic
open Amazon.BedrockRuntime
open Amazon
open Amazon.BedrockRuntime.Model
open Diligence.Core

type ModelReply = { Memo: string; InputTokens: int option; OutputTokens: int option }
type IResearchModel = abstract Complete: ResearchRequest * string * CancellationToken -> Task<ModelReply>

module Bedrock =
    let clientConfig region =
        let config = AmazonBedrockRuntimeConfig(RegionEndpoint = RegionEndpoint.GetBySystemName(region))
        config.AuthSchemePreference <- List<string>([ "sigv4" ])
        config

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
    interface IResearchModel with
        member _.Complete(_, _, _) = Task.FromException<ModelReply>(NotSupportedException($"Live Bedrock Converse is not enabled yet for '{modelId}'. Use --replay-response for recorded-response runs."))
    interface IResearchConversation with
        member _.Start(request, systemPrompt, _, cancellationToken) = task {
            let conversation = new ConverseRequest(ModelId = modelId)
            conversation.System <- List<SystemContentBlock>([ SystemContentBlock(Text = systemPrompt) ])
            conversation.Messages <- List<Message>([ Message(Role = ConversationRole.User, Content = List<ContentBlock>([ ContentBlock(Text = $"Ticker: {request.Ticker}\\nQuestion: {request.Question}") ])) ])
            let! response = client.ConverseAsync(conversation, cancellationToken)
            let text = response.Output.Message.Content |> Seq.choose (fun content -> if String.IsNullOrWhiteSpace(content.Text) then None else Some content.Text) |> String.concat "\\n"
            return { Text = Some text; ToolRequests = []; InputTokens = None; OutputTokens = None }
        }
        member _.Continue(_, _) = Task.FromException<ModelTurn>(InvalidOperationException("The Bedrock conversation ended without a tool result request."))
