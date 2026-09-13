namespace Diligence.Infrastructure

open System
open System.IO
open System.Text.Json
open System.Security.Cryptography
open Diligence.Core

type RunDirectory = { Id: string; Path: string }

module RunStorage =
    let create root =
        let timestamp = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH-mm-ssZ")
        let suffix = Guid.NewGuid().ToString("N")[..7]
        let id = $"{timestamp}-{suffix}"
        let path = Path.Combine(root, id)
        Directory.CreateDirectory(path) |> ignore
        Directory.CreateDirectory(Path.Combine(path, "evidence")) |> ignore
        { Id = id; Path = path }
    let private writeAtomically (path: string) (content: string) = task {
        let temporaryPath = path + ".tmp"
        do! File.WriteAllTextAsync(temporaryPath, content)
        File.Move(temporaryPath, path, true)
    }
    let writeRequest run request = JsonSerializer.Serialize(request, JsonSerializerOptions(WriteIndented = true)) |> writeAtomically (Path.Combine(run.Path, "request.json"))
    let writePrompt run prompt = writeAtomically (Path.Combine(run.Path, "prompt.md")) prompt
    let writeAnswer run answer = writeAtomically (Path.Combine(run.Path, "answer.md")) answer
    let writePartialAnswer run answer = writeAtomically (Path.Combine(run.Path, "partial-answer.md")) answer
    let saveEvidence run (title: string) (url: string) (content: string) = task {
        let bytes: byte array = Text.Encoding.UTF8.GetBytes(content)
        let hash: string = SHA256.HashData(bytes) |> Convert.ToHexString |> fun value -> value.ToLowerInvariant()
        let id = hash[..15]
        let relativePath = Path.Combine("evidence", id + ".txt")
        do! writeAtomically (Path.Combine(run.Path, relativePath)) content
        let source = {| id = id; title = title; url = url; retrievedAt = DateTimeOffset.UtcNow; contentHash = hash; path = relativePath |}
        let sourcesPath = Path.Combine(run.Path, "sources.json")
        let prior = if File.Exists sourcesPath then JsonSerializer.Deserialize<JsonElement>(File.ReadAllText sourcesPath).EnumerateArray() |> Seq.map (fun value -> value.GetRawText()) |> Seq.toList else []
        let json = "[" + String.Join(",", prior @ [ JsonSerializer.Serialize(source) ]) + "]"
        do! writeAtomically sourcesPath json
        return source
    }
    let writeManifest run config status =
        let document = {| runId = run.Id; status = status; region = config.Region; modelId = config.ModelId; createdAt = DateTimeOffset.UtcNow |}
        JsonSerializer.Serialize(document, JsonSerializerOptions(WriteIndented = true)) |> writeAtomically (Path.Combine(run.Path, "manifest.json"))
    let writeDetailedManifest run config status (startedAt: DateTimeOffset) (completedAt: DateTimeOffset option) turns toolCalls inputTokens outputTokens message =
        let document =
            {| runId = run.Id
               status = status
               region = config.Region
               modelId = config.ModelId
               startedAt = startedAt
               completedAt = completedAt
               elapsedMilliseconds = completedAt |> Option.map (fun finished -> (finished - startedAt).TotalMilliseconds)
               turns = turns
               toolCalls = toolCalls
               inputTokens = inputTokens
               outputTokens = outputTokens
               message = message |}
        JsonSerializer.Serialize(document, JsonSerializerOptions(WriteIndented = true)) |> writeAtomically (Path.Combine(run.Path, "manifest.json"))
    let appendEvent run eventName details = task {
        let event = {| at = DateTimeOffset.UtcNow; name = eventName; details = details |}
        do! File.AppendAllTextAsync(Path.Combine(run.Path, "events.jsonl"), JsonSerializer.Serialize(event) + Environment.NewLine)
    }
    let appendToolEvent run eventName (call: ToolCall) = task {
        let event = {| at = DateTimeOffset.UtcNow; name = eventName; details = $"{call.Name} ({call.Id})"; data = call |}
        do! File.AppendAllTextAsync(Path.Combine(run.Path, "events.jsonl"), JsonSerializer.Serialize(event) + Environment.NewLine)
    }
    let find root runId =
        let rootPath = Path.GetFullPath(root)
        let path = Path.GetFullPath(Path.Combine(rootPath, runId))
        let expectedParent = Directory.GetParent(path)
        if
            not (String.IsNullOrWhiteSpace runId)
            && not (Path.IsPathRooted runId)
            && expectedParent <> null
            && String.Equals(expectedParent.FullName, rootPath, StringComparison.Ordinal)
            && Directory.Exists path
        then
            Some { Id = runId; Path = path }
        else
            None
