namespace Diligence.Core

open System
open System.IO

module Prompts =
    let load inlinePrompt promptFile =
        match inlinePrompt, promptFile with
        | Some prompt, None when not (String.IsNullOrWhiteSpace prompt) -> Ok prompt
        | None, Some path when File.Exists path -> Ok(File.ReadAllText path)
        | Some _, Some _ -> Error "Specify either --prompt or --prompt-file, not both."
        | None, Some path -> Error $"Prompt file does not exist: {path}"
        | _ -> Error "A question is required via --prompt or --prompt-file."
