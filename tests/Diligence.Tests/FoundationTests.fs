namespace Diligence.Tests

open System
open System.IO
open System.Net
open System.Net.Http
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Xunit
open Diligence.Core
open Diligence.Infrastructure

module FoundationTests =
    type FixtureHttpHandler(responses: Map<string, string>) =
        inherit HttpMessageHandler()

        override _.SendAsync(request, _) =
            let path = request.RequestUri.AbsolutePath
            match responses |> Map.tryFind path with
            | Some body ->
                let response = new HttpResponseMessage(HttpStatusCode.OK)
                response.Content <- new StringContent(body, Encoding.UTF8, "application/json")
                Task.FromResult response
            | None -> Task.FromException<HttpResponseMessage>(InvalidOperationException($"Unexpected fixture request: {path}"))

    type ToolUsingModel() =
        interface IResearchConversation with
            member _.Start(_, _, _, _) =
                Task.FromResult { Text = Some "Looking up evidence"; ToolRequests = [ { Id = "call-1"; Name = "fixture_tool"; Arguments = "{\"query\":\"ACME\"}" } ]; InputTokens = Some 10; OutputTokens = Some 4 }
            member _.Continue(results, _) =
                Assert.Single(results) |> ignore
                Task.FromResult { Text = Some "# Final memo"; ToolRequests = []; InputTokens = Some 6; OutputTokens = Some 8 }

    type EmptyModel() =
        interface IResearchConversation with
            member _.Start(_, _, _, _) = Task.FromResult { Text = None; ToolRequests = []; InputTokens = None; OutputTokens = None }
            member _.Continue(_, _) = Task.FromException<ModelTurn>(InvalidOperationException("No continuation expected"))

    let fixtureTool =
        { new IResearchTool with
            member _.Definition = { Name = "fixture_tool"; Description = "Recorded fixture tool"; InputSchema = "{}" }
            member _.Execute(_, _) = Task.FromResult("{\"source_id\":\"source-1\"}") }

    [<Fact>]
    let ``defaults target the selected local Bedrock region`` () = Assert.Equal("us-east-1", AppConfig.defaults.Region)

    [<Fact>]
    let ``F sharp registry exposes the complete SEC EDGAR tool set`` () =
        use client = new HttpClient(new FixtureHttpHandler(Map.empty))
        let names = SecEdgarTools(AppConfig.defaults, client).All |> List.map (fun tool -> tool.Definition.Name) |> Set.ofList
        let expected = Set.ofList [ "sec_filings"; "sec_filing_content"; "sec_financials"; "sec_resolve_company"; "sec_filing_search" ]
        Assert.True((expected = names), $"Unexpected SEC EDGAR tools: {names}")
        SecEdgarTools(AppConfig.defaults, client).All
        |> List.iter (fun tool ->
            use schema = JsonDocument.Parse(tool.Definition.InputSchema)
            Assert.Equal(JsonValueKind.Object, schema.RootElement.ValueKind))

    [<Fact>]
    let ``SEC EDGAR tools preserve the Python lookup and extraction contracts`` () = task {
        let submissions = """{"name":"Apple Inc.","tickers":["AAPL"],"exchanges":["Nasdaq"],"sic":"3571","sicDescription":"Electronic Computers","filings":{"recent":{"form":["10-K"],"filingDate":["2024-11-01"],"accessionNumber":["0000320193-24-000123"],"primaryDocument":["aapl-20240928.htm"]}}}"""
        let index = """{"directory":{"item":[{"name":"aapl-20240928.htm"}]}}"""
        let filing = """<html><h1>Item 7. Management's Discussion</h1><p>Revenue grew and liquidity remained strong.</p><h1>Item 8. Financial Statements</h1><p>Statements follow.</p></html>"""
        let facts = """{"entityName":"Apple Inc.","facts":{"us-gaap":{"Assets":{"units":{"USD":[{"end":"2024-09-28","filed":"2024-11-01","form":"10-K","fy":2024,"fp":"FY","val":364980000000,"accn":"0000320193-24-000123"}]}}}}}"""
        let responses = Map [
            "/submissions/CIK0000320193.json", submissions
            "/Archives/edgar/data/320193/000032019324000123/index.json", index
            "/Archives/edgar/data/320193/000032019324000123/aapl-20240928.htm", filing
            "/api/xbrl/companyfacts/CIK0000320193.json", facts
        ]
        use client = new HttpClient(new FixtureHttpHandler(responses))
        let config = { AppConfig.defaults with SecUserAgent = Some "Diligence Tests tests@example.com" }
        let tools = SecEdgarTools(config, client).All |> Seq.map (fun tool -> tool.Definition.Name, tool) |> Map.ofSeq
        let execute name arguments = tools[name].Execute(arguments, CancellationToken.None)

        let! filings = execute "sec_filings" "{\"query\":\"CIK 320193\",\"form_type\":\"10-K\"}"
        Assert.Contains("0000320193-24-000123", filings)
        let! company = execute "sec_resolve_company" "{\"query\":\"320193\"}"
        Assert.Contains("Apple Inc.", company)
        Assert.Contains("AAPL", company)
        let! content = execute "sec_filing_content" "{\"identifier\":\"320193\",\"accession_number\":\"000032019324000123\",\"item\":\"Item 7\"}"
        Assert.Contains("liquidity remained strong", content)
        Assert.DoesNotContain("Statements follow", content)
        let! search = execute "sec_filing_search" "{\"identifier\":\"320193\",\"accession_number\":\"0000320193-24-000123\",\"query\":\"liquidity\",\"context_chars\":30}"
        Assert.Contains("liquidity", search)
        let! financials = execute "sec_financials" "{\"identifier\":\"320193\",\"statement\":\"balance_sheet\"}"
        Assert.Contains("364980000000", financials)
        Assert.Contains("Assets", financials)
    }

    [<Fact>]
    let ``Bedrock client uses profile-compatible SigV4 authentication`` () =
        let config = Bedrock.clientConfig "us-west-2"
        Assert.Equal("us-west-2", config.RegionEndpoint.SystemName)
        Assert.Equal<string list>([ "sigv4" ], config.AuthSchemePreference |> Seq.toList)

    [<Fact>]
    let ``Bedrock maps tool definitions to Converse tool configuration`` () =
        let definition = {
            Name = "sec_filings"
            Description = "List SEC filings."
            InputSchema = "{\"type\":\"object\",\"properties\":{\"ticker\":{\"type\":\"string\"}},\"required\":[\"ticker\"]}"
        }
        let configuration = Bedrock.toolConfiguration [ definition ]
        let tool = Assert.Single(configuration.Tools).ToolSpec
        Assert.Equal("sec_filings", tool.Name)
        Assert.Equal("List SEC filings.", tool.Description)
        Assert.True(tool.InputSchema.Json.IsDictionary())
        let schema = tool.InputSchema.Json.AsDictionary()
        Assert.Equal("object", schema["type"].AsString())

    [<Fact>]
    let ``Bedrock parses native tool use and token usage`` () =
        let input = Amazon.Runtime.Documents.Document.FromObject(dict [ "ticker", box "MSFT" ])
        let response = Amazon.BedrockRuntime.Model.ConverseResponse(
            Output = Amazon.BedrockRuntime.Model.ConverseOutput(
                Message = Amazon.BedrockRuntime.Model.Message(
                    Role = Amazon.BedrockRuntime.ConversationRole.Assistant,
                    Content = Collections.Generic.List<Amazon.BedrockRuntime.Model.ContentBlock>([
                        Amazon.BedrockRuntime.Model.ContentBlock(Text = "Checking filings")
                        Amazon.BedrockRuntime.Model.ContentBlock(ToolUse = Amazon.BedrockRuntime.Model.ToolUseBlock(ToolUseId = "call-1", Name = "sec_filings", Input = input))
                    ]))),
            Usage = Amazon.BedrockRuntime.Model.TokenUsage(InputTokens = Nullable 11, OutputTokens = Nullable 7))
        let turn = Bedrock.modelTurn response
        Assert.Equal(Some "Checking filings", turn.Text)
        let call = Assert.Single(turn.ToolRequests)
        Assert.Equal("call-1", call.Id)
        Assert.Equal("sec_filings", call.Name)
        Assert.Equal("{\"ticker\":\"MSFT\"}", call.Arguments)
        Assert.Equal(Some 11, turn.InputTokens)
        Assert.Equal(Some 7, turn.OutputTokens)

    [<Fact>]
    let ``Bedrock maps tool outputs to user tool-result blocks`` () =
        let message = Bedrock.toolResultMessage [ { ToolUseId = "call-1"; Content = "{\"filings\":[]}"; IsError = false } ]
        Assert.Equal(Amazon.BedrockRuntime.ConversationRole.User, message.Role)
        let result = Assert.Single(message.Content).ToolResult
        Assert.Equal("call-1", result.ToolUseId)
        Assert.Equal("{\"filings\":[]}", Assert.Single(result.Content).Text)

    [<Fact>]
    let ``Bedrock parses DeepSeek text tool calls instead of saving them as an answer`` () =
        let text = """Let me check.\n<sec_calls><sec_call><function>sec_filings</function><params>{"ticker":"MSFT"}</params></sec_call></sec_calls>"""
        let call = Assert.Single(Bedrock.textToolRequests text)
        Assert.StartsWith("text-call-1-", call.Id)
        Assert.Equal("sec_filings", call.Name)
        Assert.Equal("{\"ticker\":\"MSFT\"}", call.Arguments)

    [<Fact>]
    let ``Bedrock forwards malformed text-tool arguments so the tool loop can report the error`` () =
        let text = "<tool_calls><tool_call><id>bad-1</id><function>sec_filings</function><params>{bad json}</params></tool_call></tool_calls>"
        let call = Assert.Single(Bedrock.textToolRequests text)
        Assert.Equal("bad-1", call.Id)
        Assert.Equal("{bad json}", call.Arguments)

    [<Fact>]
    let ``Bedrock selects the text protocol for DeepSeek runtime models`` () =
        Assert.True(Bedrock.usesTextToolProtocol "deepseek.v3.2")
        Assert.True(Bedrock.usesTextToolProtocol "us.deepseek.r1-v1:0")
        Assert.False(Bedrock.usesTextToolProtocol "cohere.command-r-v1:0")

    [<Fact>]
    let ``Bedrock text tool results preserve ids errors and content`` () =
        let message = Bedrock.textToolResultMessage [ { ToolUseId = "call-7"; Content = "failed <unsafe>"; IsError = true } ]
        let text = Assert.Single(message.Content).Text
        Assert.Contains("<tool_results>", text)
        Assert.Contains("\"tool_use_id\":\"call-7\"", text)
        Assert.Contains("\"is_error\":true", text)
        Assert.Contains("\\u003Cunsafe\\u003E", text)

    [<Fact>]
    let ``replay response is returned as a memo`` () = task {
        let root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString())
        let response = Path.Combine(root, "response.md")
        Directory.CreateDirectory(root) |> ignore
        do! File.WriteAllTextAsync(response, "# Recorded memo")
        let model = ReplayModel(response) :> IResearchModel
        let request = { Ticker = "ACME"; Question = "What changed?"; Thesis = None; AsOfDate = None }
        let! reply = model.Complete(request, request.Question, CancellationToken.None)
        Assert.Equal("# Recorded memo", reply.Memo)
    }

    [<Fact>]
    let ``tool loop exposes progress history and token totals`` () = task {
        let request = { Ticker = "ACME"; Question = "What changed?"; Thesis = None; AsOfDate = None }
        let events = ResizeArray<ToolLoopEvent>()
        let! outcome = ToolLoop.runWithObserver (ToolUsingModel()) [ fixtureTool ] request request.Question AppConfig.defaults.Limits (fun event -> events.Add event; task { return () }) CancellationToken.None
        Assert.Equal(Some "# Final memo", outcome.Answer)
        Assert.Equal(Some 16, outcome.InputTokens)
        Assert.Equal(Some 12, outcome.OutputTokens)
        Assert.Equal(1, outcome.ToolCalls.Length)
        Assert.True(outcome.ToolCalls.Head.CompletedAt.IsSome, "Completed tool activity should have an end time")
        Assert.Contains(events, fun event -> match event with | ToolStarted call -> call.Name = "fixture_tool" | _ -> false)
        Assert.Contains(events, fun event -> match event with | ToolFinished call -> call.Result.IsSome | _ -> false)
    }

    [<Fact>]
    let ``tool loop identifies a model that stops without a memo`` () = task {
        let request = { Ticker = "ACME"; Question = "What changed?"; Thesis = None; AsOfDate = None }
        let! outcome = ToolLoop.runWithObserver (EmptyModel()) [] request request.Question AppConfig.defaults.Limits (fun _ -> task { return () }) CancellationToken.None
        Assert.Equal(ModelStoppedWithoutAnswer, outcome.StopReason)
        Assert.Equal(1, outcome.Turns)
        Assert.True(outcome.Answer.IsNone, "An empty provider turn must not be treated as a memo")
    }

    [<Fact>]
    let ``report escapes untrusted content and links saved evidence`` () = task {
        let root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString())
        let run = RunStorage.create root
        let request = { Ticker = "ACME"; Question = "<script>question()</script>"; Thesis = None; AsOfDate = None }
        do! RunStorage.writeRequest run request
        do! RunStorage.writePrompt run request.Question
        do! RunStorage.writeAnswer run "# Memo\n<img src=x onerror=alert(1)>"
        let! _ = RunStorage.saveEvidence run "Annual <report>" "https://example.com/filing?a=1&b=2" "saved filing"
        let call = { Id = "call-1"; Name = "web_fetch"; Arguments = "{\"url\":\"https://example.com\"}"; StartedAt = DateTimeOffset.UtcNow; CompletedAt = Some DateTimeOffset.UtcNow; Result = Some "<unsafe>"; Error = None }
        do! RunStorage.appendToolEvent run "tool_completed" call
        do! RunStorage.writeDetailedManifest run AppConfig.defaults "completed" DateTimeOffset.UtcNow (Some DateTimeOffset.UtcNow) 2 1 (Some 10) (Some 5) None
        let! reportPath = Report.generate run
        let html = File.ReadAllText reportPath
        Assert.Contains("&lt;script&gt;question()&lt;/script&gt;", html)
        Assert.Contains("&lt;img src=x onerror=alert(1)&gt;", html)
        Assert.DoesNotContain("<script>question()</script>", html)
        Assert.Contains("saved excerpt", html)
        Assert.Contains("tool_completed", html)
        Assert.Contains("&lt;unsafe&gt;", html)
    }

    [<Fact>]
    let ``run lookup cannot escape the configured storage directory`` () =
        let root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString())
        let run = RunStorage.create root
        Assert.True((RunStorage.find root run.Id).IsSome, "A run directly beneath the storage root should be found")
        Assert.True((RunStorage.find root "../").IsNone, "Parent traversal must not resolve as a run")
        Assert.True((RunStorage.find root (Path.GetTempPath())).IsNone, "An absolute path must not resolve as a run")
