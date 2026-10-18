module Diligence.Infrastructure

open System
open System.Collections.Generic
open System.Diagnostics
open System.Globalization
open System.IO
open System.Net.Http
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.RegularExpressions
open System.Threading
open System.Xml.Linq

// ---------------------------------------------------------------------------
// Configuration
// ---------------------------------------------------------------------------

let SEC_USER_AGENT =
        config.SecUserAgent
        |> Option.filter (String.IsNullOrWhiteSpace >> not)
        |> Option.defaultWith (fun () -> invalidOp "SEC_USER_AGENT is required for SEC EDGAR tools")

let SEC_BASE = "https://data.sec.gov"

let SEC_ARCHIVES =
    "https://www.sec.gov/Archives/edgar/data"

let COMPANY_TICKERS_URL =
    "https://www.sec.gov/files/company_tickers.json"

let FUND_TICKERS_URL =
    "https://www.sec.gov/files/company_tickers_mf.json"

let EFTS_SEARCH_URL =
    "https://efts.sec.gov/LATEST/search-index"

let COMPANY_TICKERS_MAX_AGE = TimeSpan.FromHours(24.0)

let NPORT_XML_MAX_AGE = TimeSpan.FromDays(30.0)

type EdgarError(message: string, innerException: exn) =
    inherit Exception(message, innerException)

    new(message: string) = EdgarError(message, null)

let private httpClient =
    let client = new HttpClient()
    client.Timeout <- TimeSpan.FromSeconds(30.0)

    client.DefaultRequestHeaders.TryAddWithoutValidation(
        "User-Agent",
        SEC_USER_AGENT
    )
    |> ignore

    client.DefaultRequestHeaders.TryAddWithoutValidation(
        "Accept",
        "application/json, application/xml, text/xml, */*"
    )
    |> ignore

    client

let private warn (message: string) =
    Trace.TraceWarning(message)

let private info (message: string) =
    Trace.TraceInformation(message)

// ---------------------------------------------------------------------------
// Rate limiter
// ---------------------------------------------------------------------------

// A lock and monotonic clock keep request starts separated even when
// multiple callers use the module concurrently.
let private requestLock = obj ()
let private requestClock = Stopwatch.StartNew()
let private minimumDelay = TimeSpan.FromSeconds(0.15)
let mutable private lastRequest: TimeSpan option = None

let private rateLimit () =
    lock requestLock (fun () ->
        match lastRequest with
        | Some previous ->
            let remaining =
                minimumDelay - (requestClock.Elapsed - previous)

            if remaining > TimeSpan.Zero then
                Thread.Sleep(remaining)
        | None -> ()

        lastRequest <- Some requestClock.Elapsed)

let private getBytes (url: string) =
    rateLimit ()

    use response =
        httpClient.GetAsync(url).GetAwaiter().GetResult()

    response.EnsureSuccessStatusCode() |> ignore

    response.Content
        .ReadAsByteArrayAsync()
        .GetAwaiter()
        .GetResult()

// ---------------------------------------------------------------------------
// On-disk cache
// ---------------------------------------------------------------------------

let private cacheDir () =
    let configured =
        Environment.GetEnvironmentVariable("PEQUOD_CACHE_DIR")

    if String.IsNullOrWhiteSpace(configured) then
        Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.UserProfile
            ),
            ".cache",
            "pequod-mcp"
        )
    else
        configured.Trim()

let private cachePath (url: string) =
    let digest =
        url
        |> Encoding.UTF8.GetBytes
        |> SHA256.HashData
        |> Convert.ToHexString
        |> fun value -> value.ToLowerInvariant().Substring(0, 24)

    let lastSegment =
        url.Substring(url.LastIndexOf('/') + 1)

    let sanitized =
        Regex.Replace(lastSegment, "[^A-Za-z0-9._-]", "-")

    let stem =
        if String.IsNullOrEmpty(sanitized) then
            "response"
        else
            sanitized.Substring(0, min 60 sanitized.Length)

    Path.Combine(cacheDir (), $"{stem}-{digest}")

let private cachedGet (url: string) (maxAge: TimeSpan) =
    let path = cachePath url

    let cached =
        try
            if File.Exists(path) then
                let age =
                    DateTime.UtcNow - File.GetLastWriteTimeUtc(path)

                if age < maxAge then
                    Some(File.ReadAllBytes(path))
                else
                    None
            else
                None
        with
        | :? IOException as ex ->
            warn $"Cache read failed for {path}: {ex.Message}"
            None
        | :? UnauthorizedAccessException as ex ->
            warn $"Cache read failed for {path}: {ex.Message}"
            None

    match cached with
    | Some content -> content
    | None ->
        let content = getBytes url

        // A unique temporary name avoids collisions between writers.
        let temporaryPath =
            path + "." + Guid.NewGuid().ToString("N") + ".tmp"

        try
            Directory.CreateDirectory(cacheDir ()) |> ignore
            File.WriteAllBytes(temporaryPath, content)
            File.Move(temporaryPath, path, true)
        with
        | :? IOException as ex ->
            warn $"Cache write failed for {path}: {ex.Message}"
        | :? UnauthorizedAccessException as ex ->
            warn $"Cache write failed for {path}: {ex.Message}"

        try
            if File.Exists(temporaryPath) then
                File.Delete(temporaryPath)
        with _ ->
            ()

        content

// ---------------------------------------------------------------------------
// Data types
// ---------------------------------------------------------------------------

type Holding =
    {
        Name: string
        Title: string
        Ticker: string
        Lei: string
        Figi: string
        Isin: string
        Balance: float
        Units: string
        ValueUsd: float
        PctVal: float
        Currency: string
        AssetType: string
        IssuerType: string
        Country: string
        PayoffProfile: string
    }

    member this.ToDict() : IDictionary<string, obj> =
        // CUSIP is intentionally excluded.
        // IssuerType is also omitted to match the Python serializer.
        dict [
            "name", box this.Name
            "title", box this.Title
            "ticker", box this.Ticker
            "lei", box this.Lei
            "figi", box this.Figi
            "isin", box this.Isin
            "balance", box this.Balance
            "units", box this.Units
            "value_usd", box this.ValueUsd
            "pct_val", box this.PctVal
            "currency", box this.Currency
            "asset_type", box this.AssetType
            "country", box this.Country
            "payoff_profile", box this.PayoffProfile
        ]

type HoldingsResult =
    {
        EtfTicker: string
        Cik: string
        FilingDate: string
        ReportDate: string
        AccessionNumber: string
        SourceUrl: string
        FundName: string
        TotalAssets: float
        NetAssets: float
        Holdings: Holding list
        TotalHoldings: int
        Method: string
    }

    member this.ToDict() : IDictionary<string, obj> =
        let totalHoldings =
            if this.TotalHoldings = 0 then
                this.Holdings.Length
            else
                this.TotalHoldings

        dict [
            "etf_ticker", box this.EtfTicker
            "cik", box this.Cik
            "fund_name", box this.FundName
            "filing_date", box this.FilingDate
            "report_date", box this.ReportDate
            "accession_number", box this.AccessionNumber
            "source_url", box this.SourceUrl
            "total_assets", box this.TotalAssets
            "net_assets", box this.NetAssets
            "total_holdings", box totalHoldings
            "method", box this.Method
            "holdings",
            box (
                this.Holdings
                |> List.map (fun holding -> holding.ToDict())
                |> List.toArray
            )
        ]

type SecEntry =
    {
        Cik: string
        Title: string
    }

type FundEntry =
    {
        Cik: string
        SeriesId: string
    }

type private NportMetadata =
    {
        AccessionNumber: string
        FilingDate: string
        ReportDate: string
        PrimaryDocument: string
        EntityName: string
    }

type private FundInfo =
    {
        FundName: string
        ReportDate: string
        FilingDate: string
        TotalAssets: float
        NetAssets: float
    }

// ---------------------------------------------------------------------------
// JSON helpers
// ---------------------------------------------------------------------------

let private property (name: string) (element: JsonElement) =
    if element.ValueKind = JsonValueKind.Object then
        match element.TryGetProperty(name) with
        | true, value -> value
        | false, _ -> Unchecked.defaultof<JsonElement>
    else
        Unchecked.defaultof<JsonElement>

let private jsonText (element: JsonElement) =
    match element.ValueKind with
    | JsonValueKind.String -> element.GetString()
    | JsonValueKind.Number
    | JsonValueKind.True
    | JsonValueKind.False -> element.GetRawText()
    | _ -> ""

let private textProperty name element =
    element |> property name |> jsonText

let private jsonArray (element: JsonElement) =
    if element.ValueKind = JsonValueKind.Array then
        element.EnumerateArray() |> Seq.toArray
    else
        [||]

let private arrayProperty name element =
    element |> property name |> jsonArray

let private arrayTextAt name index element =
    let items = arrayProperty name element

    if index >= 0 && index < items.Length then
        jsonText items[index]
    else
        ""

let private getJson url =
    use document = JsonDocument.Parse(getBytes url)
    document.RootElement.Clone()

let private cachedJson url maxAge =
    use document = JsonDocument.Parse(cachedGet url maxAge)
    document.RootElement.Clone()

let private jsonObjectOrEmpty (element: JsonElement) : obj =
    if element.ValueKind = JsonValueKind.Object then
        box (element.Clone())
    else
        box (dict [] : IDictionary<string, obj>)

let private stringArrayProperty name element =
    element
    |> arrayProperty name
    |> Array.map jsonText

// ---------------------------------------------------------------------------
// Ticker / CIK lookup
// ---------------------------------------------------------------------------

let private tickerMapLock = obj ()
let private fundMapLock = obj ()

let mutable private tickerMap: Map<string, SecEntry> option = None
let mutable private fundMap: Map<string, FundEntry> option = None

let private normalizeTicker (ticker: string) =
    ticker
        .ToUpperInvariant()
        .Trim()
        .Replace("/", "-")
        .Replace(".", "-")

let private loadTickerMap () =
    lock tickerMapLock (fun () ->
        match tickerMap with
        | Some entries -> entries
        | None ->
            try
                let data =
                    cachedJson
                        COMPANY_TICKERS_URL
                        COMPANY_TICKERS_MAX_AGE

                let entries =
                    data.EnumerateObject()
                    |> Seq.choose (fun item ->
                        let ticker =
                            textProperty "ticker" item.Value
                            |> fun value -> value.ToUpperInvariant()

                        let cik =
                            textProperty "cik_str" item.Value

                        if ticker = "" || cik = "" then
                            None
                        else
                            Some(
                                ticker,
                                {
                                    Cik = cik.PadLeft(10, '0')
                                    Title =
                                        textProperty "title" item.Value
                                }
                            ))
                    |> Map.ofSeq

                tickerMap <- Some entries
                info $"Loaded {entries.Count} ticker-to-CIK mappings"
                entries
            with ex ->
                raise (
                    EdgarError(
                        "Failed to download or parse "
                        + "SEC company_tickers.json.",
                        ex
                    )
                ))

let private loadFundMap () =
    lock fundMapLock (fun () ->
        match fundMap with
        | Some entries -> entries
        | None ->
            try
                let data =
                    cachedJson
                        FUND_TICKERS_URL
                        COMPANY_TICKERS_MAX_AGE

                let indices =
                    arrayProperty "fields" data
                    |> Array.mapi (fun index value ->
                        jsonText value, index)
                    |> Map.ofArray

                let symbolIndex = indices["symbol"]
                let cikIndex = indices["cik"]
                let seriesIndex = indices["seriesId"]

                let entries =
                    arrayProperty "data" data
                    |> Array.fold
                        (fun (state: Map<string, FundEntry>) row ->
                            let values = jsonArray row

                            let symbol =
                                jsonText values[symbolIndex]
                                |> fun value ->
                                    value.ToUpperInvariant().Trim()

                            if symbol = "" || state.ContainsKey(symbol) then
                                state
                            else
                                let entry =
                                    {
                                        Cik =
                                            (jsonText values[cikIndex])
                                                .PadLeft(10, '0')
                                        SeriesId =
                                            jsonText values[seriesIndex]
                                    }

                                state.Add(symbol, entry))
                        Map.empty

                fundMap <- Some entries

                info (
                    $"Loaded {entries.Count} "
                    + "fund-symbol-to-series mappings"
                )

                entries
            with ex ->
                raise (
                    EdgarError(
                        "Failed to download or parse "
                        + "SEC company_tickers_mf.json.",
                        ex
                    )
                ))

let secEntry (ticker: string) : SecEntry option =
    let entries = loadTickerMap ()
    let upper = ticker.ToUpperInvariant().Trim()

    entries
    |> Map.tryFind upper
    |> Option.orElseWith (fun () ->
        entries |> Map.tryFind (normalizeTicker ticker))

let secFundEntry (ticker: string) : FundEntry option =
    let entries = loadFundMap ()
    let upper = ticker.ToUpperInvariant().Trim()

    entries
    |> Map.tryFind upper
    |> Option.orElseWith (fun () ->
        entries |> Map.tryFind (normalizeTicker ticker))

let private getCik ticker =
    secEntry ticker |> Option.map (fun entry -> entry.Cik)

let lookupCik (ticker: string) : IDictionary<string, obj> =
    let upper = ticker.ToUpperInvariant().Trim()

    let cik, seriesId =
        match getCik upper with
        | Some cik -> cik, ""
        | None ->
            match secFundEntry upper with
            | Some fund -> fund.Cik, fund.SeriesId
            | None -> "", ""

    if cik = "" then
        dict [
            "ticker", box upper
            "cik", box ""
            "found", box false
            "error",
            box (
                $"Ticker {upper} not found in SEC "
                + "company_tickers.json or company_tickers_mf.json"
            )
        ]
    else
        let url = $"{SEC_BASE}/submissions/CIK{cik}.json"

        let data =
            try
                getJson url
            with ex ->
                raise (
                    EdgarError(
                        $"CIK {cik} found for {upper}, but the "
                        + "EDGAR submissions lookup failed.",
                        ex
                    )
                )

        let addresses = property "addresses" data

        dict [
            "ticker", box upper
            "cik", box cik
            "found", box true
            "series_id", box seriesId
            "kind",
            box (
                if seriesId = "" then
                    "registrant"
                else
                    "fund_series"
            )
            "entity_name", box (textProperty "name" data)
            "entity_type", box (textProperty "entityType" data)
            "sic", box (textProperty "sic" data)
            "sic_description", box (textProperty "sicDescription" data)
            "tickers", box (stringArrayProperty "tickers" data)
            "exchanges", box (stringArrayProperty "exchanges" data)
            "ein", box (textProperty "ein" data)
            "state_of_incorporation",
            box (textProperty "stateOfIncorporation" data)
            "fiscal_year_end", box (textProperty "fiscalYearEnd" data)
            "mailing_address",
            jsonObjectOrEmpty (property "mailing" addresses)
            "business_address",
            jsonObjectOrEmpty (property "business" addresses)
            "phone", box (textProperty "phone" data)
            "website", box (textProperty "website" data)
        ]

// ---------------------------------------------------------------------------
// Submissions lookup
// ---------------------------------------------------------------------------

let private getLatestNportForSeries (seriesId: string) =
    let query = Uri.EscapeDataString($"\"{seriesId}\"")

    let url =
        $"{EFTS_SEARCH_URL}?q={query}&forms=NPORT-P"

    let data = getJson url

    let hits =
        data
        |> property "hits"
        |> arrayProperty "hits"

    if Array.isEmpty hits then
        None
    else
        let best =
            hits
            |> Array.maxBy (fun hit ->
                hit
                |> property "_source"
                |> textProperty "file_date")

        let documentId = textProperty "_id" best
        let separator = documentId.IndexOf(':')

        let accession, primaryDocument =
            if separator >= 0 then
                documentId.Substring(0, separator),
                documentId.Substring(separator + 1)
            else
                documentId, ""

        let source = property "_source" best

        let displayName =
            arrayTextAt "display_names" 0 source

        let cikMarker =
            displayName.IndexOf("(CIK", StringComparison.Ordinal)

        let entityName =
            if cikMarker >= 0 then
                displayName.Substring(0, cikMarker).Trim()
            else
                displayName.Trim()

        Some
            {
                AccessionNumber = accession
                FilingDate = textProperty "file_date" source
                ReportDate = ""
                PrimaryDocument =
                    if primaryDocument = "" then
                        "primary_doc.xml"
                    else
                        primaryDocument
                EntityName = entityName
            }

let private isNportForm form =
    match form with
    | "NPORT-P"
    | "NPORT-P/A"
    | "N-PORT"
    | "N-PORT/A" -> true
    | _ -> false

let private findNport
    (entityName: string)
    (filings: JsonElement)
    =
    arrayProperty "form" filings
    |> Array.tryFindIndex (jsonText >> isNportForm)
    |> Option.map (fun index ->
        {
            AccessionNumber =
                arrayTextAt "accessionNumber" index filings
            FilingDate =
                arrayTextAt "filingDate" index filings
            ReportDate =
                arrayTextAt "reportDate" index filings
            PrimaryDocument =
                arrayTextAt "primaryDocument" index filings
            EntityName = entityName
        })

let private getLatestNport (cik: string) =
    let url = $"{SEC_BASE}/submissions/CIK{cik}.json"
    let data = getJson url
    let entityName = textProperty "name" data
    let filings = property "filings" data
    let recent = property "recent" filings

    match findNport entityName recent with
    | Some metadata -> Some metadata
    | None ->
        arrayProperty "files" filings
        |> Array.tryPick (fun fileReference ->
            let name = textProperty "name" fileReference

            let fileUrl =
                $"{SEC_BASE}/submissions/{name}"

            try
                getJson fileUrl |> findNport entityName
            with _ ->
                // Matches the Python behavior for older files.
                None)

// ---------------------------------------------------------------------------
// Filing document discovery
// ---------------------------------------------------------------------------

let private getNportXmlUrl
    (cik: string)
    (accession: string)
    (primaryDocument: string)
    =
    let accessionWithoutDashes = accession.Replace("-", "")

    let plainCik =
        let value = cik.TrimStart('0')
        if value = "" then "0" else value

    let baseUrl =
        $"{SEC_ARCHIVES}/{plainCik}/{accessionWithoutDashes}/"

    if primaryDocument <> "" then
        let document =
            if
                primaryDocument.StartsWith(
                    "xsl",
                    StringComparison.Ordinal
                )
                && primaryDocument.Contains("/")
            then
                primaryDocument.Substring(
                    primaryDocument.LastIndexOf('/') + 1
                )
            else
                primaryDocument

        baseUrl + document
    else
        let discovered =
            try
                let index = getJson (baseUrl + "index.json")

                let names =
                    index
                    |> property "directory"
                    |> arrayProperty "item"
                    |> Array.map (textProperty "name")

                let preferred =
                    names
                    |> Array.tryFind (fun name ->
                        name.EndsWith(".xml", StringComparison.Ordinal)
                        && (
                            name.Contains(
                                "nport",
                                StringComparison.OrdinalIgnoreCase
                            )
                            || name.Contains(
                                "primary_doc",
                                StringComparison.OrdinalIgnoreCase
                            )
                        ))

                preferred
                |> Option.orElseWith (fun () ->
                    names
                    |> Array.tryFind (fun name ->
                        name.EndsWith(
                            ".xml",
                            StringComparison.Ordinal
                        )))
            with ex ->
                warn (
                    $"Could not fetch filing index at {baseUrl}: "
                    + ex.Message
                )

                None

        baseUrl + Option.defaultValue "primary_doc.xml" discovered

// ---------------------------------------------------------------------------
// N-PORT XML parsing
// ---------------------------------------------------------------------------

let private findChild (name: string) (element: XElement) =
    element.Elements()
    |> Seq.tryFind (fun child -> child.Name.LocalName = name)

let private findChildren (name: string) (element: XElement) =
    element.Elements()
    |> Seq.filter (fun child -> child.Name.LocalName = name)

let private childText (name: string) (element: XElement) =
    element
    |> findChild name
    |> Option.map (fun child -> child.Value.Trim())
    |> Option.defaultValue ""

let private attributeText (name: string) (element: XElement) =
    match element.Attribute(XName.Get(name)) with
    | null -> ""
    | attribute -> attribute.Value.Trim()

let private firstNonEmpty first second =
    if String.IsNullOrEmpty(first) then second else first

let private parseFloat (value: string) =
    let normalized = Regex.Replace(value, @"[,\s]", "")

    match
        Double.TryParse(
            normalized,
            NumberStyles.Float,
            CultureInfo.InvariantCulture
        )
    with
    | true, number -> number
    | false, _ -> 0.0

let private parseIdentifiers (element: XElement option) =
    match element with
    | None -> Map.empty
    | Some identifiers ->
        identifiers.Elements()
        |> Seq.fold
            (fun (state: Map<string, string>) child ->
                let name = child.Name.LocalName.ToLowerInvariant()
                let value = attributeText "value" child

                if value = "" then
                    state
                else
                    match name with
                    | "ticker" -> state.Add("ticker", value)
                    | "isin" -> state.Add("isin", value)
                    | "other" ->
                        let description =
                            attributeText "otherDesc" child
                            |> fun text -> text.ToUpperInvariant()

                        if description.Contains("FIGI") then
                            state.Add("figi", value)
                        elif state.ContainsKey("other") then
                            state
                        else
                            state.Add("other", value)
                    | _ -> state)
            Map.empty

let private parseNportXml (xmlBytes: byte array) =
    use stream = new MemoryStream(xmlBytes)
    let document = XDocument.Load(stream)
    let root = document.Root

    if isNull root then
        invalidOp "The N-PORT XML document has no root element."

    let formData =
        root
        |> findChild "formData"
        |> Option.defaultValue root

    let generalInfo = findChild "genInfo" formData
    let fundInfoElement = findChild "fundInfo" formData

    let generalText name =
        generalInfo
        |> Option.map (childText name)
        |> Option.defaultValue ""

    let fundNumber name =
        fundInfoElement
        |> Option.map (childText name >> parseFloat)
        |> Option.defaultValue 0.0

    let fundInfo =
        {
            FundName =
                firstNonEmpty
                    (generalText "seriesName")
                    (generalText "regName")
            ReportDate =
                firstNonEmpty
                    (generalText "repPdDate")
                    (generalText "repPdEnd")
            FilingDate = generalText "regFileDate"
            TotalAssets = fundNumber "totAssets"
            NetAssets = fundNumber "netAssets"
        }

    let securities =
        formData
        |> findChild "invstOrSecs"
        |> Option.defaultValue formData

    let holdings =
        securities
        |> findChildren "invstOrSec"
        |> Seq.map (fun security ->
            let text name = childText name security
            let number name = text name |> parseFloat

            let identifiers =
                security
                |> findChild "identifiers"
                |> parseIdentifiers

            let identifier name =
                identifiers
                |> Map.tryFind name
                |> Option.defaultValue ""

            let currency =
                let direct = text "curCd"

                if direct <> "" then
                    direct
                else
                    security
                    |> findChild "currencyConditional"
                    |> Option.map (fun conditional ->
                        firstNonEmpty
                            (attributeText "curCd" conditional)
                            (childText "curCd" conditional))
                    |> Option.defaultValue ""

            {
                Name = text "name"
                Title = text "title"
                Ticker = identifier "ticker"
                Lei = text "lei"
                Figi = identifier "figi"
                Isin = identifier "isin"
                Balance = number "balance"
                Units = text "units"
                ValueUsd = number "valUSD"
                PctVal = number "pctVal"
                Currency = currency
                AssetType = text "assetCat"
                IssuerType = text "issuerCat"
                Country = text "invCountry"
                PayoffProfile = text "payoffProfile"
            })
        |> Seq.toList

    fundInfo, holdings

// ---------------------------------------------------------------------------
// Public holdings API
// ---------------------------------------------------------------------------

/// Fetch holdings using a caller-supplied CIK.
/// Pass an empty CIK to resolve the ticker through the SEC indices.
let scrapeEtfHoldingsWithCik
    (ticker: string)
    (cikOverride: string)
    : HoldingsResult
    =
    let upper = ticker.ToUpperInvariant().Trim()

    let cik, seriesId =
        if not (String.IsNullOrEmpty(cikOverride)) then
            cikOverride.Trim().PadLeft(10, '0'), ""
        else
            match secFundEntry upper with
            | Some fund -> fund.Cik, fund.SeriesId
            | None ->
                getCik upper |> Option.defaultValue "", ""

    if cik = "" then
        raise (
            EdgarError(
                $"Ticker {upper} not found in SEC "
                + "company_tickers.json or company_tickers_mf.json. "
                + "If you have the fund's CIK, pass it explicitly."
            )
        )

    let metadata =
        try
            if seriesId <> "" then
                getLatestNportForSeries seriesId
            else
                getLatestNport cik
        with
        | :? EdgarError -> reraise ()
        | ex ->
            raise (
                EdgarError(
                    $"EDGAR submissions lookup failed for {upper} "
                    + $"(CIK {cik}).",
                    ex
                )
            )

    let metadata =
        match metadata with
        | Some value -> value
        | None ->
            raise (
                EdgarError(
                    $"No N-PORT filing found for {upper} "
                    + $"(CIK {cik}) in EDGAR. It may not be a "
                    + "registered investment company that files N-PORT."
                )
            )

    let xmlUrl =
        getNportXmlUrl
            cik
            metadata.AccessionNumber
            metadata.PrimaryDocument

    let xmlBytes =
        try
            cachedGet xmlUrl NPORT_XML_MAX_AGE
        with ex ->
            raise (
                EdgarError(
                    $"Failed to download N-PORT XML from {xmlUrl}.",
                    ex
                )
            )

    let fundInfo, holdings =
        try
            parseNportXml xmlBytes
        with ex ->
            raise (
                EdgarError(
                    $"Failed to parse N-PORT XML for {upper} "
                    + $"({xmlUrl}).",
                    ex
                )
            )

    {
        EtfTicker = upper
        Cik = cik
        FundName =
            firstNonEmpty fundInfo.FundName metadata.EntityName
        FilingDate = metadata.FilingDate
        ReportDate =
            firstNonEmpty fundInfo.ReportDate metadata.ReportDate
        AccessionNumber = metadata.AccessionNumber
        SourceUrl = xmlUrl
        TotalAssets = fundInfo.TotalAssets
        NetAssets = fundInfo.NetAssets
        Holdings = holdings
        TotalHoldings = holdings.Length
        Method = "edgar_nport"
    }

/// Resolve the ticker through the SEC indices and fetch its holdings.
let scrapeEtfHoldings (ticker: string) =
    scrapeEtfHoldingsWithCik ticker ""
