namespace Diligence.Core

open System

type ResearchRequest = { Ticker: string; Question: string; Thesis: string option; AsOfDate: DateOnly option }
type EvidenceItem = { Id: string; Url: string; RetrievedAt: DateTimeOffset; ContentHash: string; Title: string option }
type ToolCall = { Id: string; Name: string; Arguments: string; StartedAt: DateTimeOffset; CompletedAt: DateTimeOffset option; Result: string option; Error: string option }
type RunStatus = | Running | Completed | Incomplete of string | Failed of string
type ResearchError = | InvalidRequest of string | ProviderFailure of string | StorageFailure of string

type ToolDefinition = { Name: string; Description: string; InputSchema: string }
type ToolRequest = { Id: string; Name: string; Arguments: string }
type ModelTurn = { Text: string option; ToolRequests: ToolRequest list; InputTokens: int option; OutputTokens: int option }
type ToolResult = { ToolUseId: string; Content: string; IsError: bool }

type ToolLoopStopReason =
    | AnswerReturned
    | ModelStoppedWithoutAnswer
    | MaximumTurnsReached
    | MaximumToolCallsReached
    | ElapsedTimeLimitReached

type ToolLoopEvent =
    | TurnStarted of turn: int
    | ToolStarted of ToolCall
    | ToolFinished of ToolCall

type ToolLoopOutcome = {
    Answer: string option
    LastModelText: string option
    Turns: int
    ToolCalls: ToolCall list
    InputTokens: int option
    OutputTokens: int option
    StopReason: ToolLoopStopReason
    Elapsed: TimeSpan
}

type IResearchConversation =
    abstract Start: ResearchRequest * string * ToolDefinition list * Threading.CancellationToken -> Threading.Tasks.Task<ModelTurn>
    abstract Continue: ToolResult list * Threading.CancellationToken -> Threading.Tasks.Task<ModelTurn>
