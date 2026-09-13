namespace Diligence.Infrastructure

open System
open System.Collections.Generic
open System.Globalization
open System.Net
open System.Net.Http
open System.Text.Json
open System.Text.RegularExpressions
open System.Threading
open System.Threading.Tasks
open Diligence.Core

type private LruCache<'key, 'value when 'key: equality>(capacity: int) =
    let capacity = max capacity 1
    let items = Dictionary<'key, LinkedListNode<KeyValuePair<'key, 'value>>>()
    let recency = LinkedList<KeyValuePair<'key, 'value>>()
    let gate = obj ()

    member _.TryGet(key: 'key) =
        lock gate (fun () ->
            match items.TryGetValue key with
            | true, node ->
                recency.Remove node
                recency.AddLast node
                Some node.Value.Value
            | _ -> None)

    member _.Set(key: 'key, value: 'value) =
        lock gate (fun () ->
            match items.TryGetValue key with
            | true, node -> recency.Remove node
            | _ -> ()

            let node = recency.AddLast(KeyValuePair(key, value))
            items[key] <- node

            if items.Count > capacity then
                let oldest = recency.First
                recency.RemoveFirst()
                items.Remove oldest.Value.Key |> ignore)

type private FilingDocument = {
    PrimaryDocument: string
    Text: string
}

type private EdgarClient(config: AppConfig, httpClient: HttpClient, cacheSize: int) =
    let dataBaseUrl = "https://data.sec.gov"
    let wwwBaseUrl = "https://www.sec.gov"
    let jsonCache = LruCache<string, string>(cacheSize)
    let textCache = LruCache<string, string>(cacheSize)
    let mutable tickerEntries: JsonElement array option = None

    let userAgent =
        config.SecUserAgent
        |> Option.filter (String.IsNullOrWhiteSpace >> not)
        |> Option.defaultWith (fun () -> invalidOp "SEC_USER_AGENT is required for SEC EDGAR tools")

    let requestWithRetry (url: string) (cancellationToken: CancellationToken) =
        let rec send attempt = task {
            use request = new HttpRequestMessage(HttpMethod.Get, url)
            request.Headers.TryAddWithoutValidation("User-Agent", userAgent) |> ignore
            use! response = httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)

            if (response.StatusCode = HttpStatusCode.TooManyRequests || response.StatusCode = HttpStatusCode.ServiceUnavailable) && attempt < 5 then
                let ceiling = min (pown 2.0 attempt) 30.0
                let delay = ceiling + Random.Shared.NextDouble() * ceiling
                do! Task.Delay(TimeSpan.FromSeconds delay, cancellationToken)
                return! send (attempt + 1)
            else
                response.EnsureSuccessStatusCode() |> ignore
                let contentLength = response.Content.Headers.ContentLength
                if contentLength.HasValue && contentLength.Value > int64 config.Limits.MaximumFetchedBytes then
                    invalidOp "Response exceeds the configured fetched-byte limit."

                let! body = response.Content.ReadAsStringAsync(cancellationToken)
                if Text.Encoding.UTF8.GetByteCount(body) > config.Limits.MaximumFetchedBytes then
                    invalidOp "Response exceeds the configured fetched-byte limit."
                return body
        }
        send 0

    let getCached (cache: LruCache<string, string>) (url: string) (cancellationToken: CancellationToken) = task {
        match cache.TryGet url with
        | Some body -> return body
        | None ->
            let! body = requestWithRetry url cancellationToken
            cache.Set(url, body)
            return body
    }

    let getJson (url: string) (cancellationToken: CancellationToken) = task {
        let! body = getCached jsonCache url cancellationToken
        use document = JsonDocument.Parse(body)
        return document.RootElement.Clone()
    }

    let tryProperty (name: string) (element: JsonElement) =
        match element.TryGetProperty name with
        | true, value -> Some value
        | _ -> None

    let stringProperty (name: string) (element: JsonElement) =
        tryProperty name element
        |> Option.map (fun value ->
            if value.ValueKind = JsonValueKind.String then value.GetString()
            else value.ToString())
        |> Option.defaultValue ""

    let formatCik (value: string) =
        match Int64.TryParse(value.Trim(), NumberStyles.None, CultureInfo.InvariantCulture) with
        | true, cik when cik >= 0L -> cik.ToString("D10", CultureInfo.InvariantCulture)
        | _ -> invalidArg "value" $"Invalid SEC CIK: '{value}'"

    let normalizeAccession (value: string) =
        let compact = value.Replace("-", "").Trim()
        if not (Regex.IsMatch(compact, "^\\d{18}$")) then
            invalidArg "accessionNumber" $"Invalid SEC accession number: '{value}'"
        $"{compact[..9]}-{compact[10..11]}-{compact[12..]}"

    let loadTickerEntries cancellationToken = task {
        match tickerEntries with
        | Some entries -> return entries
        | None ->
            let! payload = getJson $"{wwwBaseUrl}/files/company_tickers.json" cancellationToken
            let entries =
                match payload.ValueKind with
                | JsonValueKind.Object ->
                    payload.EnumerateObject()
                    |> Seq.map (fun property -> property.Value.Clone())
                    |> Seq.filter (fun item -> item.ValueKind = JsonValueKind.Object)
                    |> Seq.toArray
                | JsonValueKind.Array ->
                    payload.EnumerateArray()
                    |> Seq.filter (fun item -> item.ValueKind = JsonValueKind.Object)
                    |> Seq.map (fun item -> item.Clone())
                    |> Seq.toArray
                | _ -> invalidOp "SEC ticker map returned an unexpected payload"
            tickerEntries <- Some entries
            return entries
    }

    let indexDocumentNames (payload: JsonElement) =
        let item =
            tryProperty "directory" payload
            |> Option.bind (tryProperty "item")

        match item with
        | Some value when value.ValueKind = JsonValueKind.Array ->
            value.EnumerateArray()
            |> Seq.choose (fun entry ->
                let name = stringProperty "name" entry
                if String.IsNullOrWhiteSpace name then None else Some name)
            |> Seq.toList
        | Some value when value.ValueKind = JsonValueKind.Object ->
            let name = stringProperty "name" value
            if String.IsNullOrWhiteSpace name then [] else [ name ]
        | _ -> []

    member _.ResolveCik(query: string, cancellationToken: CancellationToken) = task {
        let cleaned = query.Trim()
        if String.IsNullOrWhiteSpace cleaned then invalidArg "query" "Company query is required"

        let cikMatch = Regex.Match(cleaned, "^(?:CIK)?\\s*0*(\\d{1,10})$", RegexOptions.IgnoreCase)
        if cikMatch.Success then
            return formatCik cikMatch.Groups[1].Value
        else
            let! entries = loadTickerEntries cancellationToken
            let tickerMatch =
                entries
                |> Array.tryFind (fun entry -> String.Equals(stringProperty "ticker" entry, cleaned, StringComparison.OrdinalIgnoreCase))

            let companyMatch =
                tickerMatch
                |> Option.orElseWith (fun () ->
                    entries
                    |> Array.tryFind (fun entry ->
                        let title = stringProperty "title" entry
                        not (String.IsNullOrWhiteSpace title)
                        && (title.Contains(cleaned, StringComparison.OrdinalIgnoreCase)
                            || cleaned.Contains(title, StringComparison.OrdinalIgnoreCase))))

            match companyMatch with
            | Some entry -> return formatCik (stringProperty "cik_str" entry)
            | None -> return invalidOp $"No company found for '{query}'. Try a ticker symbol or CIK number."
    }

    member _.FetchSubmissions(cik: string, cancellationToken: CancellationToken) =
        getJson $"{dataBaseUrl}/submissions/CIK{formatCik cik}.json" cancellationToken

    member _.FetchCompanyFacts(cik: string, cancellationToken: CancellationToken) =
        getJson $"{dataBaseUrl}/api/xbrl/companyfacts/CIK{formatCik cik}.json" cancellationToken

    member this.FetchFilingDocument(cik: string, accessionNumber: string, cancellationToken: CancellationToken) = task {
        let cik = formatCik cik
        let accession = normalizeAccession accessionNumber
        let compactAccession = accession.Replace("-", "")
        let archiveCik = Int64.Parse(cik, CultureInfo.InvariantCulture)
        let! index = getJson $"{wwwBaseUrl}/Archives/edgar/data/{archiveCik}/{compactAccession}/index.json" cancellationToken
        let! submissions = this.FetchSubmissions(cik, cancellationToken)

        let primaryFromSubmissions =
            tryProperty "filings" submissions
            |> Option.bind (tryProperty "recent")
            |> Option.bind (fun recent ->
                match tryProperty "accessionNumber" recent, tryProperty "primaryDocument" recent with
                | Some accessions, Some documents when accessions.ValueKind = JsonValueKind.Array && documents.ValueKind = JsonValueKind.Array ->
                    accessions.EnumerateArray()
                    |> Seq.mapi (fun index (value: JsonElement) -> index, value.GetString())
                    |> Seq.tryPick (fun (index, value: string) ->
                        if value = accession && index < documents.GetArrayLength() then
                            let primary = documents[index].GetString()
                            if String.IsNullOrWhiteSpace primary then None else Some primary
                        else None)
                | _ -> None)

        let candidates = indexDocumentNames index
        let primaryDocument =
            primaryFromSubmissions
            |> Option.orElseWith (fun () ->
                candidates
                |> List.tryFind (fun (name: string) ->
                    let lower = name.ToLowerInvariant()
                    (lower.EndsWith(".htm") || lower.EndsWith(".html")) && not (lower.StartsWith("ex"))))
            |> Option.orElseWith (fun () ->
                candidates
                |> List.tryFind (fun (name: string) ->
                    let lower = name.ToLowerInvariant()
                    lower.EndsWith(".txt") || lower.EndsWith(".htm") || lower.EndsWith(".html") || lower.EndsWith(".xml")))
            |> Option.defaultWith (fun () -> invalidOp $"No primary document found for accession '{accessionNumber}'")

        let url = $"{wwwBaseUrl}/Archives/edgar/data/{archiveCik}/{compactAccession}/{primaryDocument}"
        let! text = getCached textCache url cancellationToken
        return { PrimaryDocument = primaryDocument; Text = text }
    }

module private SecEdgarFormatting =
    let private jsonOptions = JsonSerializerOptions(PropertyNamingPolicy = null)
    let serialize value = JsonSerializer.Serialize(value, jsonOptions)
    let error message = serialize {| error = message |}

    let tryProperty (name: string) (element: JsonElement) =
        match element.TryGetProperty name with
        | true, value -> Some value
        | _ -> None

    let stringProperty (name: string) (element: JsonElement) =
        tryProperty name element
        |> Option.map (fun value -> if value.ValueKind = JsonValueKind.String then value.GetString() else value.ToString())
        |> Option.defaultValue ""

    let arrayProperty (name: string) (element: JsonElement) =
        match tryProperty name element with
        | Some value when value.ValueKind = JsonValueKind.Array -> value.EnumerateArray() |> Seq.map (fun item -> item.Clone()) |> Seq.toArray
        | _ -> [||]

    let truncate (budget: int) (text: string) =
        if text.Length <= budget then text
        else text[..budget - 1] + "... [truncated; use bash with grep -A/-B /path to inspect]"

    let htmlToText (raw: string) =
        raw
        |> fun text -> Regex.Replace(text, "(?i)<(br|p|div|tr|table|h[1-6])\\b[^>]*>", "\n")
        |> fun text -> Regex.Replace(text, "<[^>]+>", " ")
        |> WebUtility.HtmlDecode
        |> fun text -> text.Replace('\u00a0', ' ')
        |> fun text -> Regex.Replace(text, "[ \\t]+", " ")
        |> fun text -> Regex.Replace(text, "\n\\s+", "\n")
        |> fun text -> Regex.Replace(text, "\n{3,}", "\n\n")
        |> fun text -> text.Trim()

    let private extractHeadingSection (text: string) (headingPattern: Regex) (nextHeadingPattern: Regex) =
        headingPattern.Matches(text)
        |> Seq.cast<Match>
        |> Seq.map (fun heading ->
            let next = nextHeadingPattern.Match(text, heading.Index + heading.Length)
            let finish = if next.Success then next.Index else text.Length
            text.Substring(heading.Index, finish - heading.Index).Trim())
        |> Seq.sortByDescending _.Length
        |> Seq.tryHead
        |> Option.filter (String.IsNullOrWhiteSpace >> not)

    let extractItemSection (text: string) (item: string) =
        let note = Regex.Match(item, "(?i)(?:notes?|footnotes?)\\s*(\\d+)")
        if note.Success then
            let number = Regex.Escape(note.Groups[1].Value)
            extractHeadingSection text (Regex($"(?im)^\\s*(?:notes?|footnotes?)\\s+{number}\\b[^\n]*")) (Regex("(?im)^\\s*(?:notes?|footnotes?)\\s+\\d+\\b[^\n]*"))
        else
            let itemMatch = Regex.Match(item, "(?i)(?:item\\s*)?(\\d{1,2}[A-Z]?)")
            if not itemMatch.Success then None
            else
                let target = Regex.Escape(itemMatch.Groups[1].Value.ToUpperInvariant())
                extractHeadingSection text (Regex($"(?im)^\\s*item\\s+{target}\\.?\\b[^\n]*")) (Regex("(?im)^\\s*item\\s+\\d{1,2}[A-Z]?\\.?\\b[^\n]*"))

    let availableSections (text: string) =
        [ Regex("(?im)^\\s*(item\\s+\\d{1,2}[A-Z]?\\.?[^\n]{0,120})")
          Regex("(?im)^\\s*((?:notes?|footnotes?)\\s+\\d+[^\n]{0,120})") ]
        |> Seq.collect (fun pattern -> pattern.Matches(text) |> Seq.cast<Match>)
        |> Seq.map (fun found -> Regex.Replace(found.Groups[1].Value, "\\s+", " ").Trim())
        |> Seq.distinctBy _.ToLowerInvariant()
        |> Seq.truncate 30
        |> Seq.toList

    let searchText (text: string) (query: string) contextChars =
        let contextChars = max contextChars 0
        let seen = ResizeArray<int * int>()
        let results = ResizeArray<Dictionary<string, obj>>()
        for found in Regex.Matches(text, Regex.Escape(query), RegexOptions.IgnoreCase) |> Seq.cast<Match> do
            if results.Count < 10 then
                let start = max 0 (found.Index - contextChars)
                let finish = min text.Length (found.Index + found.Length + contextChars)
                let overlaps =
                    seen
                    |> Seq.exists (fun (previousStart, previousFinish) -> min finish previousFinish - max start previousStart > contextChars)
                if not overlaps then
                    seen.Add(start, finish)
                    results.Add(Dictionary<string, obj>(dict [ "position", box found.Index; "passage", box (text.Substring(start, finish - start)) ]))
        results

type SecEdgarTools(config: AppConfig, httpClient: HttpClient) =
    let defaultCacheSize = 1000
    let defaultOutputBudget = 16000
    let definition name description schema = { Name = name; Description = description; InputSchema = schema }

    let positiveEnvironmentInteger name fallback =
        match Int32.TryParse(Environment.GetEnvironmentVariable(name)) with
        | true, value when value > 0 -> value
        | _ -> fallback

    let client = lazy (EdgarClient(config, httpClient, positiveEnvironmentInteger "SEC_EDGAR_CACHE_SIZE" defaultCacheSize))
    let outputBudget = positiveEnvironmentInteger "TOOL_OUTPUT_CHAR_BUDGET" defaultOutputBudget

    let property (name: string) (arguments: string) =
        use document = JsonDocument.Parse(arguments)
        match document.RootElement.TryGetProperty name with
        | true, value when value.ValueKind = JsonValueKind.String && not (String.IsNullOrWhiteSpace(value.GetString())) -> value.GetString()
        | _ -> invalidArg "arguments" $"{name} is required"

    let optional (name: string) (fallback: string) (arguments: string) =
        use document = JsonDocument.Parse(arguments)
        match document.RootElement.TryGetProperty name with
        | true, value when value.ValueKind = JsonValueKind.String -> value.GetString()
        | _ -> fallback

    let integer (name: string) fallback (arguments: string) =
        use document = JsonDocument.Parse(arguments)
        match document.RootElement.TryGetProperty name with
        | true, value when value.TryGetInt32() |> fst -> value.GetInt32()
        | _ -> fallback

    let protect (operation: unit -> Task<string>) = task {
        try return! operation ()
        with
        | :? OperationCanceledException as error -> return raise error
        | error -> return SecEdgarFormatting.error error.Message
    }

    let filings arguments cancellationToken = protect (fun () -> task {
        let query = property "query" arguments
        let formType = optional "form_type" "10-Q" arguments
        let limit = max (integer "num_results" 5 arguments) 0
        let! cik = client.Value.ResolveCik(query, cancellationToken)
        let! submissions = client.Value.FetchSubmissions(cik, cancellationToken)
        let company = SecEdgarFormatting.stringProperty "name" submissions
        let recent =
            SecEdgarFormatting.tryProperty "filings" submissions
            |> Option.bind (SecEdgarFormatting.tryProperty "recent")
        let rows = ResizeArray<Dictionary<string, string>>()

        match recent with
        | Some recent ->
            let forms = SecEdgarFormatting.arrayProperty "form" recent
            let dates = SecEdgarFormatting.arrayProperty "filingDate" recent
            let accessions = SecEdgarFormatting.arrayProperty "accessionNumber" recent
            let documents = SecEdgarFormatting.arrayProperty "primaryDocument" recent
            let valueAt (values: JsonElement array) index = if index < values.Length then values[index].ToString() else ""
            let mutable index = 0
            while index < forms.Length && rows.Count < limit do
                let form = forms[index].ToString()
                if String.IsNullOrWhiteSpace(formType) || String.Equals(form, formType.Trim(), StringComparison.OrdinalIgnoreCase) then
                    rows.Add(Dictionary<string, string>(dict [
                        "company", company
                        "form", form
                        "filing_date", valueAt dates index
                        "accession_number", valueAt accessions index
                        "primary_document", valueAt documents index
                    ]))
                index <- index + 1
        | None -> ()

        return SecEdgarFormatting.serialize rows |> SecEdgarFormatting.truncate outputBudget
    })

    let filingContent arguments cancellationToken = protect (fun () -> task {
        let identifier = property "identifier" arguments
        let accession = property "accession_number" arguments
        let item = optional "item" "" arguments
        let! cik = client.Value.ResolveCik(identifier, cancellationToken)
        let! document = client.Value.FetchFilingDocument(cik, accession, cancellationToken)
        let text = SecEdgarFormatting.htmlToText document.Text
        if String.IsNullOrWhiteSpace item then
            return SecEdgarFormatting.truncate outputBudget text
        else
            match SecEdgarFormatting.extractItemSection text item with
            | Some section -> return SecEdgarFormatting.truncate outputBudget section
            | None -> return SecEdgarFormatting.serialize {| error = $"Section '{item}' not found"; available_sections = SecEdgarFormatting.availableSections text |}
    })

    let financials arguments cancellationToken = protect (fun () -> task {
        let identifier = property "identifier" arguments
        let statement = optional "statement" "balance_sheet" arguments
        let statementNames = [ "balance_sheet"; "income_statement"; "cash_flow_statement" ]
        let concepts =
            Map [
                "balance_sheet", [ "Assets", [ "Assets" ]; "Liabilities", [ "Liabilities" ]; "StockholdersEquity", [ "StockholdersEquity" ] ]
                "income_statement", [ "Revenues", [ "Revenues"; "RevenueFromContractWithCustomerExcludingAssessedTax"; "SalesRevenueNet" ]; "NetIncomeLoss", [ "NetIncomeLoss" ] ]
                "cash_flow_statement", [ "CashAndCashEquivalents", [ "CashAndCashEquivalentsAtCarryingValue"; "CashCashEquivalentsRestrictedCashAndRestrictedCashEquivalents" ]; "CashFlowFromOperations", [ "NetCashProvidedByUsedInOperatingActivities" ] ]
            ]

        match concepts |> Map.tryFind statement with
        | None -> return SecEdgarFormatting.serialize {| error = $"Statement '{statement}' not found"; available = statementNames |}
        | Some statementConcepts ->
            let! cik = client.Value.ResolveCik(identifier, cancellationToken)
            let! companyFacts = client.Value.FetchCompanyFacts(cik, cancellationToken)
            let usGaap =
                SecEdgarFormatting.tryProperty "facts" companyFacts
                |> Option.bind (SecEdgarFormatting.tryProperty "us-gaap")
            let outputFacts = Dictionary<string, obj>()

            let recentValues (concept: string) =
                usGaap
                |> Option.bind (SecEdgarFormatting.tryProperty concept)
                |> Option.bind (SecEdgarFormatting.tryProperty "units")
                |> Option.bind (fun units ->
                    SecEdgarFormatting.tryProperty "USD" units
                    |> Option.orElseWith (fun () -> SecEdgarFormatting.tryProperty "shares" units))
                |> Option.filter (fun values -> values.ValueKind = JsonValueKind.Array)
                |> Option.map (fun values ->
                    values.EnumerateArray()
                    |> Seq.filter (fun value -> SecEdgarFormatting.tryProperty "val" value |> Option.isSome)
                    |> Seq.sortByDescending (fun value -> SecEdgarFormatting.stringProperty "end" value, SecEdgarFormatting.stringProperty "filed" value)
                    |> Seq.truncate 8
                    |> Seq.map (fun value ->
                        let row = Dictionary<string, obj>()
                        for name in [ "end"; "filed"; "form"; "fy"; "fp"; "val"; "accn" ] do
                            row[name] <-
                                match SecEdgarFormatting.tryProperty name value with
                                | Some property -> box (property.Clone())
                                | None -> null
                        row)
                    |> Seq.toList)
                |> Option.defaultValue []

            for outputName, candidates in statementConcepts do
                let values = candidates |> List.map recentValues |> List.tryFind (List.isEmpty >> not) |> Option.defaultValue []
                outputFacts[outputName] <- box values

            let payload = Dictionary<string, obj>()
            payload["cik"] <- box cik
            payload["entity_name"] <- box (SecEdgarFormatting.stringProperty "entityName" companyFacts)
            payload["statement"] <- box statement
            payload["facts"] <- box outputFacts
            return SecEdgarFormatting.serialize payload |> SecEdgarFormatting.truncate outputBudget
    })

    let resolveCompany arguments cancellationToken = protect (fun () -> task {
        let query = property "query" arguments
        let! cik = client.Value.ResolveCik(query, cancellationToken)
        let! submissions = client.Value.FetchSubmissions(cik, cancellationToken)
        let output = Dictionary<string, obj>()
        output["cik"] <- box cik
        output["name"] <- box (SecEdgarFormatting.stringProperty "name" submissions)
        output["tickers"] <- box (SecEdgarFormatting.arrayProperty "tickers" submissions)
        output["exchanges"] <- box (SecEdgarFormatting.arrayProperty "exchanges" submissions)
        output["sic"] <- box (SecEdgarFormatting.stringProperty "sic" submissions)
        output["sic_description"] <- box (SecEdgarFormatting.stringProperty "sicDescription" submissions)
        return SecEdgarFormatting.serialize output
    })

    let filingSearch arguments cancellationToken = protect (fun () -> task {
        let identifier = property "identifier" arguments
        let accession = property "accession_number" arguments
        let query = property "query" arguments
        let contextChars = integer "context_chars" 500 arguments |> max 0 |> min 10000
        let! cik = client.Value.ResolveCik(identifier, cancellationToken)
        let! document = client.Value.FetchFilingDocument(cik, accession, cancellationToken)
        let matches = SecEdgarFormatting.htmlToText document.Text |> fun text -> SecEdgarFormatting.searchText text query contextChars
        if matches.Count = 0 then
            return SecEdgarFormatting.serialize {| matches = matches; note = $"No matches for '{query}' in this filing" |}
        else
            return SecEdgarFormatting.serialize {| matches = matches |} |> SecEdgarFormatting.truncate outputBudget
    })

    member _.All: IResearchTool list =
        [ { new IResearchTool with
                member _.Definition = definition "sec_filings" "Search SEC EDGAR for company filings by ticker, CIK, or company name." "{\"type\":\"object\",\"properties\":{\"query\":{\"type\":\"string\"},\"form_type\":{\"type\":\"string\",\"default\":\"10-Q\"},\"num_results\":{\"type\":\"integer\",\"default\":5}},\"required\":[\"query\"]}"
                member _.Execute(arguments, ct) = filings arguments ct }
          { new IResearchTool with
                member _.Definition = definition "sec_filing_content" "Fetch SEC filing content. Use item for sections such as 'Item 7', 'Note 12', or 'Footnote 5'." "{\"type\":\"object\",\"properties\":{\"identifier\":{\"type\":\"string\"},\"accession_number\":{\"type\":\"string\"},\"item\":{\"type\":\"string\",\"default\":\"\"}},\"required\":[\"identifier\",\"accession_number\"]}"
                member _.Execute(arguments, ct) = filingContent arguments ct }
          { new IResearchTool with
                member _.Definition = definition "sec_financials" "Get parsed financial statements from SEC company facts." "{\"type\":\"object\",\"properties\":{\"identifier\":{\"type\":\"string\"},\"statement\":{\"type\":\"string\",\"enum\":[\"balance_sheet\",\"income_statement\",\"cash_flow_statement\"],\"default\":\"balance_sheet\"}},\"required\":[\"identifier\"]}"
                member _.Execute(arguments, ct) = financials arguments ct }
          { new IResearchTool with
                member _.Definition = definition "sec_resolve_company" "Resolve a company name, ticker, or CIK to its SEC identifiers." "{\"type\":\"object\",\"properties\":{\"query\":{\"type\":\"string\"}},\"required\":[\"query\"]}"
                member _.Execute(arguments, ct) = resolveCompany arguments ct }
          { new IResearchTool with
                member _.Definition = definition "sec_filing_search" "Search within a specific SEC filing for a keyword or phrase and return matching passages with context." "{\"type\":\"object\",\"properties\":{\"identifier\":{\"type\":\"string\"},\"accession_number\":{\"type\":\"string\"},\"query\":{\"type\":\"string\"},\"context_chars\":{\"type\":\"integer\",\"default\":500}},\"required\":[\"identifier\",\"accession_number\",\"query\"]}"
                member _.Execute(arguments, ct) = filingSearch arguments ct } ]
