namespace Diligence.Infrastructure

open System
open System.IO
open System.Net
open System.Text
open System.Text.Encodings.Web
open System.Text.Json

module Report =
    let private readableJson = JsonSerializerOptions(WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping)
    let private encode (value: string) = WebUtility.HtmlEncode(defaultArg (Option.ofObj value) "")

    let private property (name: string) (element: JsonElement) =
        element.EnumerateObject()
        |> Seq.tryFind (fun item -> String.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase))
        |> Option.map (fun item -> item.Value)

    let private textProperty name element =
        property name element
        |> Option.bind (fun value -> if value.ValueKind = JsonValueKind.String then Option.ofObj (value.GetString()) else Some(value.ToString()))
        |> Option.defaultValue ""

    let private readJson path =
        if File.Exists path then
            try
                use document = JsonDocument.Parse(File.ReadAllText path)
                Some(document.RootElement.Clone())
            with :? JsonException -> None
        else None

    let private appendDocumentText (builder: StringBuilder) heading cssClass path fallback =
        builder.Append($"<section><h2>{encode heading}</h2><div class=\"{cssClass}\"><pre>") |> ignore
        builder.Append(encode (if File.Exists path then File.ReadAllText path else fallback)) |> ignore
        builder.Append("</pre></div></section>") |> ignore

    let generate (run: RunDirectory) = task {
        let request = readJson (Path.Combine(run.Path, "request.json"))
        let manifest = readJson (Path.Combine(run.Path, "manifest.json"))
        let status = manifest |> Option.map (textProperty "status") |> Option.defaultValue "unknown"
        let ticker = request |> Option.map (textProperty "ticker") |> Option.defaultValue "Unknown ticker"
        let builder = StringBuilder()
        builder.Append("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">") |> ignore
        builder.Append($"<title>Diligence report — {encode ticker}</title>") |> ignore
        builder.Append("<style>body{font-family:system-ui,sans-serif;max-width:1040px;margin:2rem auto;padding:0 1rem;color:#202124;background:#fafafa}header,section{background:white;border:1px solid #ddd;border-radius:10px;padding:1rem 1.25rem;margin:1rem 0}h1,h2{margin-top:0}.status{display:inline-block;padding:.25rem .6rem;border-radius:999px;background:#e8eefc}pre{white-space:pre-wrap;overflow-wrap:anywhere;margin:0}.question{border-left:4px solid #5271ff;padding-left:1rem}.memo{border-left:4px solid #198754;padding-left:1rem}.warning{border-left:4px solid #d97706;padding-left:1rem}details{border-top:1px solid #eee;padding:.7rem 0}summary{cursor:pointer;font-weight:600}.meta{color:#666;font-size:.9rem}a{color:#174ea6}</style></head><body>") |> ignore
        builder.Append($"<header><h1>{encode ticker} research report</h1><span class=\"status\">{encode status}</span><p class=\"meta\">Run {encode run.Id}</p></header>") |> ignore
        appendDocumentText builder "Question" "question" (Path.Combine(run.Path, "prompt.md")) "Question was not saved."
        let answerPath = Path.Combine(run.Path, "answer.md")
        let partialPath = Path.Combine(run.Path, "partial-answer.md")
        if File.Exists answerPath then appendDocumentText builder "Research memo" "memo" answerPath ""
        elif File.Exists partialPath then appendDocumentText builder "Partial model output" "warning" partialPath ""
        else appendDocumentText builder "Research memo" "warning" answerPath "No memo was produced. Review the run status and activity below."

        builder.Append("<section><h2>Sources and saved evidence</h2>") |> ignore
        match readJson (Path.Combine(run.Path, "sources.json")) with
        | Some sources when sources.ValueKind = JsonValueKind.Array && sources.GetArrayLength() > 0 ->
            builder.Append("<ol>") |> ignore
            for source in sources.EnumerateArray() do
                let title = textProperty "title" source
                let url = textProperty "url" source
                let evidencePath = textProperty "path" source
                let safeUrl =
                    match Uri.TryCreate(url, UriKind.Absolute) with
                    | true, uri when uri.Scheme = Uri.UriSchemeHttp || uri.Scheme = Uri.UriSchemeHttps -> Some uri.AbsoluteUri
                    | _ -> None
                builder.Append("<li>") |> ignore
                match safeUrl with
                | Some href -> builder.Append($"<a href=\"{encode href}\">{encode (if String.IsNullOrWhiteSpace title then href else title)}</a>") |> ignore
                | None -> builder.Append(encode title) |> ignore
                if not (String.IsNullOrWhiteSpace evidencePath) then builder.Append($" — <a href=\"{encode evidencePath}\">saved excerpt</a>") |> ignore
                let retrievedAt = textProperty "retrievedAt" source |> encode
                builder.Append($" <span class=\"meta\">{retrievedAt}</span></li>") |> ignore
            builder.Append("</ol>") |> ignore
        | _ -> builder.Append("<p class=\"warning\">No saved evidence is available for this run.</p>") |> ignore
        builder.Append("</section>") |> ignore

        builder.Append("<section><h2>Tool and run activity</h2>") |> ignore
        let eventsPath = Path.Combine(run.Path, "events.jsonl")
        if File.Exists eventsPath then
            for line in File.ReadLines eventsPath do
                if not (String.IsNullOrWhiteSpace line) then
                    try
                        use document = JsonDocument.Parse(line)
                        let event = document.RootElement
                        let eventName = textProperty "name" event |> encode
                        let eventDetails = textProperty "details" event |> encode
                        let eventAt = textProperty "at" event |> encode
                        builder.Append($"<details><summary>{eventName} — {eventDetails}</summary><p class=\"meta\">{eventAt}</p>") |> ignore
                        match property "data" event with
                        | Some data -> builder.Append($"<pre>{encode (JsonSerializer.Serialize(data, readableJson))}</pre>") |> ignore
                        | None -> ()
                        builder.Append("</details>") |> ignore
                    with :? JsonException -> builder.Append($"<details><summary>Unreadable event</summary><pre>{encode line}</pre></details>") |> ignore
        else builder.Append("<p>No activity was recorded.</p>") |> ignore
        builder.Append("</section>") |> ignore

        builder.Append("<section><h2>Run configuration and outcome</h2><pre>") |> ignore
        builder.Append(encode (if File.Exists(Path.Combine(run.Path, "manifest.json")) then File.ReadAllText(Path.Combine(run.Path, "manifest.json")) else "Manifest is unavailable.")) |> ignore
        builder.Append("</pre></section></body></html>") |> ignore
        let reportPath = Path.Combine(run.Path, "report.html")
        let temporaryPath = reportPath + ".tmp"
        do! File.WriteAllTextAsync(temporaryPath, builder.ToString())
        File.Move(temporaryPath, reportPath, true)
        return reportPath
    }
