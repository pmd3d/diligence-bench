open System
open System.IO
open System.Text.Json
open System.Threading
open Diligence.Core
open Diligence.Infrastructure

let arguments = Environment.GetCommandLineArgs() |> Array.skip 1 |> Array.toList
let value name values = values |> List.tryFindIndex ((=) name) |> Option.bind (fun index -> values |> List.tryItem (index + 1))
let configPath values = value "--config" values |> Option.orElseWith (fun () -> Environment.GetEnvironmentVariable("DILIGENCE_CONFIG") |> Option.ofObj)
let usage () = Console.Error.WriteLine("Usage: diligence research --ticker <symbol> (--prompt <text> | --prompt-file <path>) [--config <path>] [--replay-response <path>] [--runs-dir <path>] [--region <region>]\n       diligence restart --run <run-id> [--config <path>] [--replay-response <path>] [--runs-dir <path>] [--region <region>]\n       diligence show --run <run-id> [--config <path>] [--runs-dir <path>] [--answer]\n       diligence tools")

let stopMessage reason =
    match reason with
    | AnswerReturned -> "The model returned a memo."
    | ModelStoppedWithoutAnswer -> "The model stopped without returning a memo."
    | MaximumTurnsReached -> "The maximum turn limit was reached before a memo was returned."
    | MaximumToolCallsReached -> "The maximum tool-call limit was reached before a memo was returned."
    | ElapsedTimeLimitReached -> "The elapsed-time limit was reached before a memo was returned."

let runResearch values = task {
    let prompt = Prompts.load (value "--prompt" values) (value "--prompt-file" values)
    match value "--ticker" values, prompt with
    | Some ticker, Ok question ->
        let loadedConfig = AppConfig.load (defaultArg (configPath values) "")
        let config = { loadedConfig with RunsDirectory = defaultArg (value "--runs-dir" values) loadedConfig.RunsDirectory; Region = defaultArg (value "--region" values) loadedConfig.Region }
        let request = { Ticker = ticker.ToUpperInvariant(); Question = question; Thesis = None; AsOfDate = None }
        let run = RunStorage.create config.RunsDirectory
        let startedAt = DateTimeOffset.UtcNow
        do! RunStorage.writeRequest run request
        do! RunStorage.writePrompt run question
        do! RunStorage.writeDetailedManifest run config "running" startedAt None 0 0 None None None
        do! RunStorage.appendEvent run "run_started" "Research run started."
        use cancellation = new CancellationTokenSource()
        let mutable observedTurns = 0
        let mutable observedToolCalls = 0
        let cancelHandler = ConsoleCancelEventHandler(fun _ event -> event.Cancel <- true; cancellation.Cancel(); Console.Error.WriteLine("Cancellation requested; preserving the partial run…"))
        Console.CancelKeyPress.AddHandler(cancelHandler)
        try
            use client = new Net.Http.HttpClient(Timeout = TimeSpan.FromSeconds 30.0)
            let tools = ResearchTools(run, config, client).All
            let model : IResearchConversation =
                match value "--replay-response" values with
                | Some path when File.Exists path -> ReplayModel(path) :> IResearchConversation
                | Some path -> invalidArg "--replay-response" $"Replay response does not exist: {path}"
                | None -> match config.ModelId with | Some modelId -> BedrockModel(config.Region, modelId) :> IResearchConversation | None -> invalidArg "--config" "modelId is required for live Bedrock research or supply --replay-response."
            let observe event = task {
                match event with
                | TurnStarted turn ->
                    observedTurns <- turn
                    Console.Error.WriteLine($"Turn {turn}: waiting for the model…")
                    do! RunStorage.appendEvent run "turn_started" $"Model turn {turn} started."
                | ToolStarted call ->
                    observedToolCalls <- observedToolCalls + 1
                    Console.Error.WriteLine($"Tool {call.Name}: started")
                    do! RunStorage.appendToolEvent run "tool_started" call
                | ToolFinished call ->
                    let outcome = if call.Error.IsSome then "failed" else "completed"
                    Console.Error.WriteLine($"Tool {call.Name}: {outcome}")
                    do! RunStorage.appendToolEvent run $"tool_{outcome}" call
            }
            let! outcome = ToolLoop.runWithObserver model tools request question config.Limits observe cancellation.Token
            let completedAt = DateTimeOffset.UtcNow
            match outcome.Answer with
            | Some memo when not (String.IsNullOrWhiteSpace memo) ->
                do! RunStorage.writeAnswer run memo
                do! RunStorage.appendEvent run "memo_saved" $"Memo saved after {outcome.Turns} turns and {outcome.ToolCalls.Length} tool calls."
                do! RunStorage.writeDetailedManifest run config "completed" startedAt (Some completedAt) outcome.Turns outcome.ToolCalls.Length outcome.InputTokens outcome.OutputTokens (Some(stopMessage outcome.StopReason))
                let! reportPath = Report.generate run
                Console.WriteLine($"Run: {run.Id}")
                Console.WriteLine($"Report: {reportPath}")
                return 0
            | _ ->
                match outcome.LastModelText with
                | Some partial when not (String.IsNullOrWhiteSpace partial) -> do! RunStorage.writePartialAnswer run partial
                | _ -> ()
                let message = stopMessage outcome.StopReason
                do! RunStorage.appendEvent run "run_incomplete" message
                do! RunStorage.writeDetailedManifest run config "incomplete" startedAt (Some completedAt) outcome.Turns outcome.ToolCalls.Length outcome.InputTokens outcome.OutputTokens (Some message)
                let! reportPath = Report.generate run
                Console.Error.WriteLine($"Run {run.Id} is incomplete. {message}")
                Console.Error.WriteLine($"Report: {reportPath}")
                return 1
        with
        | :? OperationCanceledException ->
            let message = "The run was cancelled. Completed activity and evidence were preserved."
            do! RunStorage.appendEvent run "run_cancelled" message
            do! RunStorage.writeDetailedManifest run config "incomplete" startedAt (Some DateTimeOffset.UtcNow) observedTurns observedToolCalls None None (Some message)
            let! reportPath = Report.generate run
            Console.Error.WriteLine($"Run {run.Id} was cancelled. Report: {reportPath}")
            return 130
        | error ->
            let message = $"{error.GetType().Name}: {error.Message}"
            do! RunStorage.appendEvent run "run_failed" message
            do! RunStorage.writeDetailedManifest run config "failed" startedAt (Some DateTimeOffset.UtcNow) observedTurns observedToolCalls None None (Some message)
            let! reportPath = Report.generate run
            Console.Error.WriteLine($"Run {run.Id} failed: {error.Message}")
            Console.Error.WriteLine($"Report: {reportPath}")
            return 1
    | _, Error message -> Console.Error.WriteLine(message); return 2
    | None, _ -> Console.Error.WriteLine("--ticker is required."); return 2
}

let runShow values = task {
    let loadedConfig = AppConfig.load (defaultArg (configPath values) "")
    let root = defaultArg (value "--runs-dir" values) loadedConfig.RunsDirectory
    match value "--run" values |> Option.bind (RunStorage.find root) with
    | Some run when List.contains "--answer" values ->
        let answer = Path.Combine(run.Path, "answer.md")
        if File.Exists answer then Console.Write(File.ReadAllText answer); return 0
        else Console.Error.WriteLine("This run has no completed memo. Use show without --answer to inspect its report."); return 1
    | Some run ->
        let! reportPath = Report.generate run
        Console.WriteLine(reportPath)
        return 0
    | None -> Console.Error.WriteLine("Run not found. Supply --run and, if needed, --runs-dir."); return 2
}

let runRestart values = task {
    let loadedConfig = AppConfig.load (defaultArg (configPath values) "")
    let root = defaultArg (value "--runs-dir" values) loadedConfig.RunsDirectory
    match value "--run" values |> Option.bind (RunStorage.find root) with
    | Some priorRun ->
        let requestPath = Path.Combine(priorRun.Path, "request.json")
        let promptPath = Path.Combine(priorRun.Path, "prompt.md")
        if not (File.Exists requestPath && File.Exists promptPath) then
            Console.Error.WriteLine("The saved run is missing request.json or prompt.md and cannot be restarted.")
            return 1
        else
            use document = JsonDocument.Parse(File.ReadAllText requestPath)
            let ticker = document.RootElement.GetProperty("Ticker").GetString()
            if String.IsNullOrWhiteSpace ticker then
                Console.Error.WriteLine("The saved run has no ticker and cannot be restarted.")
                return 1
            else
                Console.Error.WriteLine($"Restarting run {priorRun.Id} as a new run; the original remains unchanged.")
                return! runResearch ([ "--ticker"; ticker; "--prompt-file"; promptPath ] @ values)
    | None ->
        Console.Error.WriteLine("Run not found. Supply --run and, if needed, --runs-dir.")
        return 2
}

[<EntryPoint>]
let main _ =
    match arguments with
    | "research" :: rest -> runResearch rest |> Async.AwaitTask |> Async.RunSynchronously
    | "restart" :: rest -> runRestart rest |> Async.AwaitTask |> Async.RunSynchronously
    | "show" :: rest -> runShow rest |> Async.AwaitTask |> Async.RunSynchronously
    | [ "tools" ] -> Console.WriteLine("sec_filings\nsec_filing_content\nsec_financials\nsec_resolve_company\nsec_filing_search\nweb_search\nweb_fetch"); 0
    | _ -> usage (); 2
