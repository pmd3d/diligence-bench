namespace Diligence.Core

open System
open System.Threading
open System.Threading.Tasks

type IResearchTool =
    abstract Definition: ToolDefinition
    abstract Execute: string * CancellationToken -> Task<string>

module ToolLoop =
    let runWithObserver (model: IResearchConversation) (tools: IResearchTool list) request prompt limits observer cancellationToken = task {
        let byName = tools |> Seq.map (fun tool -> tool.Definition.Name, tool) |> Map.ofSeq
        let started = DateTimeOffset.UtcNow
        let mutable turns = 0
        let mutable calls = 0
        let mutable toolCalls = []
        let mutable inputTokens = 0
        let mutable outputTokens = 0
        let mutable hasInputTokens = false
        let mutable hasOutputTokens = false
        let mutable answer = None
        let mutable lastModelText = None
        let mutable stopReason = MaximumTurnsReached
        let mutable next = Some (model.Start(request, prompt, tools |> List.map (fun tool -> tool.Definition), cancellationToken))
        while answer.IsNone && next.IsSome do
            cancellationToken.ThrowIfCancellationRequested()
            if DateTimeOffset.UtcNow - started > limits.ElapsedTimeLimit then
                stopReason <- ElapsedTimeLimitReached
                next <- None
            elif turns >= limits.MaximumTurns then
                stopReason <- MaximumTurnsReached
                next <- None
            else
                turns <- turns + 1
                do! observer (TurnStarted turns)
                let! turn = next.Value
                if turn.Text |> Option.exists (String.IsNullOrWhiteSpace >> not) then lastModelText <- turn.Text
                match turn.InputTokens with | Some value -> inputTokens <- inputTokens + value; hasInputTokens <- true | None -> ()
                match turn.OutputTokens with | Some value -> outputTokens <- outputTokens + value; hasOutputTokens <- true | None -> ()
                match turn.ToolRequests with
                | [] ->
                    answer <- turn.Text
                    next <- None
                    stopReason <- if turn.Text |> Option.exists (String.IsNullOrWhiteSpace >> not) then AnswerReturned else ModelStoppedWithoutAnswer
                | requests when calls + requests.Length > limits.MaximumToolCalls ->
                    stopReason <- MaximumToolCallsReached
                    next <- None
                | requests ->
                    calls <- calls + requests.Length
                    let execute call = task {
                        let startedAt = DateTimeOffset.UtcNow
                        let pending = { Id = call.Id; Name = call.Name; Arguments = call.Arguments; StartedAt = startedAt; CompletedAt = None; Result = None; Error = None }
                        do! observer (ToolStarted pending)
                        match Map.tryFind call.Name byName with
                        | None ->
                            let message = $"Unknown tool: {call.Name}"
                            let completed = { pending with CompletedAt = Some DateTimeOffset.UtcNow; Error = Some message }
                            toolCalls <- completed :: toolCalls
                            do! observer (ToolFinished completed)
                            return { ToolUseId = call.Id; Content = message; IsError = true }
                        | Some tool ->
                            try
                                let! output = tool.Execute(call.Arguments, cancellationToken)
                                let completed = { pending with CompletedAt = Some DateTimeOffset.UtcNow; Result = Some output }
                                toolCalls <- completed :: toolCalls
                                do! observer (ToolFinished completed)
                                return { ToolUseId = call.Id; Content = output; IsError = false }
                            with
                            | :? OperationCanceledException -> return raise (OperationCanceledException(cancellationToken))
                            | error ->
                                let completed = { pending with CompletedAt = Some DateTimeOffset.UtcNow; Error = Some error.Message }
                                toolCalls <- completed :: toolCalls
                                do! observer (ToolFinished completed)
                                return { ToolUseId = call.Id; Content = error.Message; IsError = true }
                    }
                    let results = ResizeArray<ToolResult>()
                    for request in requests do
                        let! result = execute request
                        results.Add(result)
                    next <- Some (model.Continue(results |> Seq.toList, cancellationToken))
        return {
            Answer = answer
            LastModelText = lastModelText
            Turns = turns
            ToolCalls = List.rev toolCalls
            InputTokens = if hasInputTokens then Some inputTokens else None
            OutputTokens = if hasOutputTokens then Some outputTokens else None
            StopReason = stopReason
            Elapsed = DateTimeOffset.UtcNow - started
        }
    }

    let run model tools request prompt limits cancellationToken = task {
        let! outcome = runWithObserver model tools request prompt limits (fun _ -> task { return () }) cancellationToken
        return outcome.Answer, outcome.Turns, outcome.ToolCalls.Length
    }
