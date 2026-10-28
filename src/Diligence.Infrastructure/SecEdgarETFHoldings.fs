namespace Diligence.Infrastructure

open System
open System.Collections.Generic
open System.Globalization
open System.Net.Http
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open System.Xml.Linq
open Diligence.Core

/// SEC NPORT-P holdings support. CIK and fund-series resolution lives in
/// EdgarClient so every SEC tool uses the same authoritative lookup.
type SecEdgarEtfHoldingsTools(run: RunDirectory, config: AppConfig, httpClient: HttpClient) =
    let dataBaseUrl = "https://data.sec.gov"
    let archivesBaseUrl = "https://www.sec.gov/Archives/edgar/data"
    let client = lazy (EdgarClient(config, httpClient, 1000))

    let definition name description schema = { Name = name; Description = description; InputSchema = schema }

    let property (name: string) (arguments: string) =
        use document = JsonDocument.Parse(arguments)
        match document.RootElement.TryGetProperty name with
        | true, value when value.ValueKind = JsonValueKind.String && not (String.IsNullOrWhiteSpace(value.GetString())) -> value.GetString()
        | _ -> invalidArg "arguments" $"{name} is required"

    let integer (name: string) fallback (arguments: string) =
        use document = JsonDocument.Parse(arguments)
        match document.RootElement.TryGetProperty name with
        | true, value when value.TryGetInt32() |> fst -> value.GetInt32()
        | _ -> fallback

    let tryProperty (name: string) (element: JsonElement) =
        match element.TryGetProperty name with
        | true, value -> Some value
        | _ -> None

    let stringProperty (name: string) (element: JsonElement) =
        tryProperty name element
        |> Option.map (fun value -> if value.ValueKind = JsonValueKind.String then value.GetString() else value.ToString())
        |> Option.defaultValue ""

    let arrayProperty (name: string) (element: JsonElement) =
        tryProperty name element
        |> Option.filter (fun value -> value.ValueKind = JsonValueKind.Array)
        |> Option.map (fun value -> value.EnumerateArray() |> Seq.toArray)
        |> Option.defaultValue [||]

    let at index (values: JsonElement array) = if index >= 0 && index < values.Length then values[index].ToString() else ""

    let isNport (form: string) =
        match form.Trim().ToUpperInvariant() with
        | "NPORT-P" | "NPORT-P/A" | "N-PORT" | "N-PORT/A" -> true
        | _ -> false

    let nportsFromFilings entityName (filings: JsonElement) =
        let forms = arrayProperty "form" filings
        forms
        |> Array.mapi (fun index form -> index, form)
        |> Array.choose (fun (index, form) ->
            if not (isNport (form.ToString())) then None
            else Some (
            at index (arrayProperty "accessionNumber" filings),
            at index (arrayProperty "filingDate" filings),
            at index (arrayProperty "reportDate" filings),
            at index (arrayProperty "primaryDocument" filings),
            entityName))
        |> Array.sortByDescending (fun (_, filingDate, _, _, _) -> filingDate)

    let xmlDocumentUrl (cik: string) (accession: string) (primaryDocument: string) (cancellationToken: CancellationToken) = task {
        let archiveCik = cik.TrimStart('0') |> fun value -> if value = "" then "0" else value
        let accession = accession.Replace("-", "")
        let baseUrl = $"{archivesBaseUrl}/{archiveCik}/{accession}/"
        let usable name = not (String.IsNullOrWhiteSpace name) && name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)
        if usable primaryDocument then return baseUrl + (primaryDocument.Split('/') |> Array.last)
        else
            let! index = client.Value.FetchSecJson(baseUrl + "index.json", cancellationToken)
            let names =
                tryProperty "directory" index
                |> Option.map (arrayProperty "item")
                |> Option.defaultValue [||]
                |> Array.map (stringProperty "name")
            let name =
                names
                |> Array.tryFind (fun value -> usable value && value.Contains("nport", StringComparison.OrdinalIgnoreCase))
                |> Option.orElseWith (fun () -> names |> Array.tryFind usable)
                |> Option.defaultWith (fun () -> invalidOp "The NPORT-P filing does not contain an XML document.")
            return baseUrl + name
    }

    let child (name: string) (element: XElement) = element.Elements() |> Seq.tryFind (fun value -> value.Name.LocalName = name)
    let children (name: string) (element: XElement) = element.Elements() |> Seq.filter (fun value -> value.Name.LocalName = name)
    let childText (name: string) (element: XElement) = child name element |> Option.map (fun value -> value.Value.Trim()) |> Option.defaultValue ""
    let attribute (name: string) (element: XElement) = match element.Attribute(XName.Get name) with | null -> "" | value -> value.Value.Trim()
    let number (value: string) = match Double.TryParse(value.Replace(",", "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture) with | true, parsed -> parsed | _ -> 0.0

    let filingSeriesId (xml: string) =
        let document = XDocument.Parse(xml)
        let root = document.Root
        if isNull root then ""
        else
            let formData = child "formData" root |> Option.defaultValue root
            child "genInfo" formData |> Option.map (childText "seriesId") |> Option.defaultValue ""

    let latestNport cik seriesId cancellationToken = task {
        let! submissions = client.Value.FetchSubmissions(cik, cancellationToken)
        let entityName = stringProperty "name" submissions
        let filings = tryProperty "filings" submissions |> Option.defaultValue (Unchecked.defaultof<JsonElement>)
        let recent = tryProperty "recent" filings |> Option.defaultValue (Unchecked.defaultof<JsonElement>)
        let mutable candidates = nportsFromFilings entityName recent |> Array.toList
        let mutable found = None

        let findSeriesInCandidates candidates = task {
            let mutable matched = None
            for accession, filingDate, reportDate, primary, name in candidates do
                if matched.IsNone then
                    try
                        let! url = xmlDocumentUrl cik accession primary cancellationToken
                        let! xml = client.Value.FetchSecText(url, cancellationToken)
                        if String.Equals(filingSeriesId xml, seriesId, StringComparison.OrdinalIgnoreCase) then
                            matched <- Some (accession, filingDate, reportDate, primary, name, xml)
                    with
                    | :? OperationCanceledException as error -> raise error
                    | _ -> ()
            return matched
        }

        if String.IsNullOrWhiteSpace seriesId then
            match candidates with
            | (accession, filingDate, reportDate, primary, company) :: _ ->
                let! url = xmlDocumentUrl cik accession primary cancellationToken
                let! xml = client.Value.FetchSecText(url, cancellationToken)
                found <- Some (accession, filingDate, reportDate, primary, company, xml)
            | [] -> ()
        else
            let! recentMatch = findSeriesInCandidates candidates
            found <- recentMatch

        if found.IsNone then
            let historicFiles = arrayProperty "files" filings
            for file in historicFiles do
                if found.IsNone then
                    let name = stringProperty "name" file
                    if not (String.IsNullOrWhiteSpace name) then
                        try
                            let! historic = client.Value.FetchSecJson($"{dataBaseUrl}/submissions/{name}", cancellationToken)
                            candidates <- nportsFromFilings entityName historic |> Array.toList
                            if String.IsNullOrWhiteSpace seriesId then
                                match candidates with
                                | (accession, filingDate, reportDate, primary, company) :: _ ->
                                    let! url = xmlDocumentUrl cik accession primary cancellationToken
                                    let! xml = client.Value.FetchSecText(url, cancellationToken)
                                    found <- Some (accession, filingDate, reportDate, primary, company, xml)
                                | [] -> ()
                            else
                                let! historicMatch = findSeriesInCandidates candidates
                                found <- historicMatch
                        with
                        | :? OperationCanceledException as error -> raise error
                        | _ -> ()
        return found
    }

    let parseHoldings (xml: string) =
        let document = XDocument.Parse(xml)
        let root = document.Root
        if isNull root then invalidOp "The NPORT-P filing has no XML root."
        let formData = child "formData" root |> Option.defaultValue root
        let general = child "genInfo" formData
        let fund = child "fundInfo" formData
        let generalText name = general |> Option.map (childText name) |> Option.defaultValue ""
        let fundNumber name = fund |> Option.map (childText name >> number) |> Option.defaultValue 0.0
        let securities = child "invstOrSecs" formData |> Option.defaultValue formData
        let holding security =
            let identifiers = child "identifiers" security
            let identifier name = identifiers |> Option.map (children name >> Seq.tryHead >> Option.map (attribute "value") >> Option.defaultValue "") |> Option.defaultValue ""
            let figi =
                identifiers
                |> Option.map (fun values ->
                    values.Elements()
                    |> Seq.tryFind (fun value -> value.Name.LocalName = "other" && (attribute "otherDesc" value).Contains("FIGI", StringComparison.OrdinalIgnoreCase))
                    |> Option.map (attribute "value")
                    |> Option.defaultValue "")
                |> Option.defaultValue ""
            let text = childText
            let currency =
                let direct = text "curCd" security
                if direct <> "" then direct
                else
                    child "currencyConditional" security
                    |> Option.map (fun value ->
                        let fromAttribute = attribute "curCd" value
                        if fromAttribute = "" then text "curCd" value else fromAttribute)
                    |> Option.defaultValue ""
            dict [
                "name", box (text "name" security); "title", box (text "title" security)
                "ticker", box (identifier "ticker"); "lei", box (text "lei" security)
                "figi", box figi; "isin", box (identifier "isin"); "balance", box (number (text "balance" security))
                "units", box (text "units" security); "value_usd", box (number (text "valUSD" security))
                "pct_val", box (number (text "pctVal" security)); "asset_type", box (text "assetCat" security)
                "currency", box currency; "country", box (text "invCountry" security)
                "payoff_profile", box (text "payoffProfile" security)
            ]
        let holdings = children "invstOrSec" securities |> Seq.map holding |> Seq.toList
        let fundName = let series = generalText "seriesName" in if series = "" then generalText "regName" else series
        fundName, generalText "repPdDate", fundNumber "totAssets", fundNumber "netAssets", holdings

    let execute arguments cancellationToken = task {
        try
            let ticker = property "ticker" arguments
            let maxHoldings = integer "max_holdings" 5000 arguments |> max 1 |> min 10000
            let! cik, seriesId = client.Value.ResolveEtf(ticker, cancellationToken)
            let! filing = latestNport cik seriesId cancellationToken
            match filing with
            | None -> return SecEdgarFormatting.serialize {| error = $"No NPORT-P filing found for {ticker}." |}
            | Some (accession, filingDate, reportDate, primary, entityName, xml) ->
                let! url = xmlDocumentUrl cik accession primary cancellationToken
                let! source = RunStorage.saveEvidence run $"SEC NPORT-P holdings for {ticker.Trim().ToUpperInvariant()} ({accession})" url xml
                let fundName, xmlReportDate, totalAssets, netAssets, holdings = parseHoldings xml
                let returned = holdings |> List.truncate maxHoldings
                return SecEdgarFormatting.serialize {| ticker = ticker.Trim().ToUpperInvariant(); cik = cik; series_id = seriesId; fund_name = (if fundName = "" then entityName else fundName); filing_date = filingDate; report_date = (if xmlReportDate = "" then reportDate else xmlReportDate); accession_number = accession; source_url = url; source_id = source.id; source_path = source.path; total_assets = totalAssets; net_assets = netAssets; total_holdings = holdings.Length; holdings_returned = returned.Length; holdings = returned |}
        with
        | :? OperationCanceledException as error -> return raise error
        | error -> return SecEdgarFormatting.error error.Message
    }

    member _.All: IResearchTool list =
        [ { new IResearchTool with
                member _.Definition = definition "sec_etf_holdings" "Retrieve the latest SEC NPORT-P filing and holdings for an ETF or mutual-fund ticker. Selects the filing whose XML matches the ticker's SEC series ID. Returns all holdings by default; use max_holdings only when a smaller result is sufficient." "{\"type\":\"object\",\"properties\":{\"ticker\":{\"type\":\"string\"},\"max_holdings\":{\"type\":\"integer\",\"default\":5000}},\"required\":[\"ticker\"]}"
                member _.Execute(arguments, ct) = execute arguments ct } ]
