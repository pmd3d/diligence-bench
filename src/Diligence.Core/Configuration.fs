namespace Diligence.Core

open System
open System.IO
open System.Text.Json

type ResearchLimits = { MaximumTurns: int; MaximumToolCalls: int; MaximumFetchedBytes: int; ElapsedTimeLimit: TimeSpan }
type AppConfig = { Region: string; ModelId: string option; RunsDirectory: string; Temperature: decimal option; Limits: ResearchLimits; SecUserAgent: string option; ExaApiKey: string option }

module AppConfig =
    let defaults = {
        Region = "us-east-1"; ModelId = None
        RunsDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "diligence-runs")
        Temperature = None
        Limits = { MaximumTurns = 24; MaximumToolCalls = 24; MaximumFetchedBytes = 8_000_000; ElapsedTimeLimit = TimeSpan.FromMinutes 15.0 }
        SecUserAgent = Environment.GetEnvironmentVariable("SEC_USER_AGENT") |> Option.ofObj
        ExaApiKey = Environment.GetEnvironmentVariable("EXA_API_KEY") |> Option.ofObj
    }

    let load path =
        if String.IsNullOrWhiteSpace path then defaults else
        use document = JsonDocument.Parse(File.ReadAllText path)
        let root = document.RootElement
        let stringValue (name: string) fallback = match root.TryGetProperty name with | true, value when value.ValueKind = JsonValueKind.String -> value.GetString() | _ -> fallback
        { defaults with Region = stringValue "region" defaults.Region; ModelId = stringValue "modelId" null |> Option.ofObj; RunsDirectory = stringValue "runsDirectory" defaults.RunsDirectory
                        SecUserAgent = stringValue "secUserAgent" null |> Option.ofObj |> Option.orElse defaults.SecUserAgent }
