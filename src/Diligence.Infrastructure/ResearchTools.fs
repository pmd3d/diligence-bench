namespace Diligence.Infrastructure

open System
open System.IO
open System.Net
open System.Net.Http
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Diligence.Core

type ResearchTools(run: RunDirectory, config: AppConfig, client: HttpClient) =
    let jsonOptions = JsonSerializerOptions(PropertyNameCaseInsensitive = true)
    let definition name description schema = { Name = name; Description = description; InputSchema = schema }
    let result value = JsonSerializer.Serialize(value)
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
    let bounded (name: string) (fallback: int) (maximum: int) (arguments: string) =
        use document = JsonDocument.Parse(arguments)
        match document.RootElement.TryGetProperty name with
        | true, value when value.TryGetInt32() |> fst -> value.GetInt32() |> max 1 |> min maximum
        | _ -> fallback
    let getText (url: string) (cancellationToken: CancellationToken) = task {
        use request = new HttpRequestMessage(HttpMethod.Get, url)
        request.Headers.TryAddWithoutValidation("User-Agent", defaultArg config.SecUserAgent "DiligenceResearch research@example.com") |> ignore
        use! response = client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
        response.EnsureSuccessStatusCode() |> ignore
        let! stream = response.Content.ReadAsStreamAsync(cancellationToken)
        use reader = new StreamReader(stream)
        let! text = reader.ReadToEndAsync(cancellationToken)
        if Text.Encoding.UTF8.GetByteCount(text) > config.Limits.MaximumFetchedBytes then invalidOp "Response exceeds the configured fetched-byte limit."
        return text
    }
    let fetch (arguments: string) (cancellationToken: CancellationToken) = task {
        let url = property "url" arguments
        let uri = Uri(url)
        if uri.Scheme <> Uri.UriSchemeHttp && uri.Scheme <> Uri.UriSchemeHttps then invalidArg "url" "Only HTTP/HTTPS URLs are allowed."
        let! addresses = Dns.GetHostAddressesAsync(uri.DnsSafeHost, cancellationToken)
        if addresses |> Array.exists (fun address -> IPAddress.IsLoopback address || address.IsIPv6LinkLocal || address.GetAddressBytes()[0] = 10uy || address.GetAddressBytes()[0] = 127uy || (address.GetAddressBytes()[0] = 192uy && address.GetAddressBytes()[1] = 168uy) || (address.GetAddressBytes()[0] = 169uy && address.GetAddressBytes()[1] = 254uy)) then invalidArg "url" "Private, loopback, and link-local addresses are blocked."
        let! content = getText url cancellationToken
        let! source = RunStorage.saveEvidence run (optional "title" url arguments) url content
        return result {| source_id = source.id; path = source.path; size = content.Length; first_chars = content[..min (content.Length - 1) 8000] |}
    }
    let search (arguments: string) (cancellationToken: CancellationToken) = task {
        let key = config.ExaApiKey |> Option.defaultWith (fun () -> invalidOp "EXA_API_KEY is required for web_search.")
        let query = property "query" arguments
        let count = bounded "k" 5 10 arguments
        use request = new HttpRequestMessage(HttpMethod.Post, "https://api.exa.ai/search")
        request.Headers.Add("x-api-key", key)
        request.Content <- new StringContent($"{{\"query\":{JsonSerializer.Serialize(query)},\"numResults\":{count}}}", Text.Encoding.UTF8, "application/json")
        use! response = client.SendAsync(request, cancellationToken)
        response.EnsureSuccessStatusCode() |> ignore
        let! body = response.Content.ReadAsStringAsync(cancellationToken)
        return body
    }
    member _.All: IResearchTool list =
        SecEdgarTools(config, client).All @
        [ { new IResearchTool with
                member _.Definition = definition "web_search" "Search public web sources with Exa." "{\"type\":\"object\",\"properties\":{\"query\":{\"type\":\"string\"},\"k\":{\"type\":\"integer\"}},\"required\":[\"query\"]}"
                member _.Execute(arguments, ct) = search arguments ct }
          { new IResearchTool with
                member _.Definition = definition "web_fetch" "Fetch a public URL and save it as run evidence." "{\"type\":\"object\",\"properties\":{\"url\":{\"type\":\"string\"},\"title\":{\"type\":\"string\"}},\"required\":[\"url\"]}"
                member _.Execute(arguments, ct) = fetch arguments ct } ]
