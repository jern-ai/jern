namespace Jern.Host

open System
open System.Diagnostics
open System.IO
open System.Text
open System.Text.Json.Nodes

/// Exact definitions and references from `jern-symbols`, the tree-sitter
/// helper shipped beside the binary (native/symbols). The helper is a plain
/// process: it reads file paths, parses them with the grammar their
/// extension names, and prints one JSON object per line. When it is not
/// there, or a file's language is not among its grammars, the callers in
/// Tools fall back to the line-anchored patterns they always had.
module Symbols =

    type Definition =
        { kind: string
          name: string
          /// 1-based, inclusive.
          start: int
          finish: int
          file: string
          signature: string }

    type Reference =
        { file: string
          line: int
          column: int
          /// "definition", "call", "class", "type", "implementation", …, or
          /// "identifier" when the grammar's tags say nothing more.
          kind: string
          /// The innermost enclosing definition's name, "" at top level.
          scope: string
          text: string }

    let private exeName = if OperatingSystem.IsWindows() then "jern-symbols.exe" else "jern-symbols"

    /// The helper's path: `JERN_SYMBOLS` first, then beside the binary.
    let helperPath () =
        match Environment.GetEnvironmentVariable "JERN_SYMBOLS" with
        | path when not (String.IsNullOrWhiteSpace path) -> path
        | _ -> Path.Combine(AppContext.BaseDirectory, exeName)

    /// The tags queries: `JERN_SYMBOLS_QUERIES` first, then beside the binary.
    let queriesPath () =
        match Environment.GetEnvironmentVariable "JERN_SYMBOLS_QUERIES" with
        | path when not (String.IsNullOrWhiteSpace path) -> path
        | _ -> Path.Combine(AppContext.BaseDirectory, "symbols", "queries")

    /// Whether the helper and its queries are present. Checked on every
    /// call: a missing helper must never take the runtime down, only its
    /// exactness.
    let available () =
        try File.Exists(helperPath ()) && Directory.Exists(queriesPath ())
        with _ -> false

    let private timeout = TimeSpan.FromSeconds 60.0

    /// Run the helper with `arguments`, feeding `stdinLines`; the stdout
    /// lines, or None when the helper is absent or fails.
    let private run (arguments: string list) (stdinLines: string seq) : string list option =
        if not (available ()) then None
        else
            try
                let info = ProcessStartInfo(helperPath ())
                info.UseShellExecute <- false
                info.RedirectStandardInput <- true
                info.RedirectStandardOutput <- true
                info.RedirectStandardError <- true
                info.StandardOutputEncoding <- UTF8Encoding false
                info.StandardInputEncoding <- UTF8Encoding false
                info.CreateNoWindow <- true
                info.ArgumentList.Add "--queries"
                info.ArgumentList.Add(queriesPath ())
                for a in arguments do info.ArgumentList.Add a
                use proc = Process.Start info
                let output = proc.StandardOutput.ReadToEndAsync()
                let errors = proc.StandardError.ReadToEndAsync()
                for line in stdinLines do proc.StandardInput.WriteLine line
                proc.StandardInput.Close()
                if proc.WaitForExit(int timeout.TotalMilliseconds) then
                    let text = output.Result
                    errors.Result |> ignore
                    if proc.ExitCode = 0 then
                        text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                        |> Array.map (fun l -> l.TrimEnd '\r')
                        |> Array.filter (fun l -> l <> "")
                        |> List.ofArray
                        |> Some
                    else None
                else
                    try proc.Kill true with _ -> ()
                    None
            with _ -> None

    let private str (node: JsonNode) (key: string) =
        match node.[key] with
        | null -> ""
        | v -> v.GetValue<string>()

    let private int' (node: JsonNode) (key: string) =
        match node.[key] with
        | null -> 0
        | v -> v.GetValue<int>()

    let private parseDefinition (fallbackFile: string) (line: string) =
        try
            let node = JsonNode.Parse line
            Some
                { kind = str node "kind"
                  name = str node "name"
                  start = int' node "start"
                  finish = int' node "end"
                  file = (match str node "file" with "" -> fallbackFile | f -> f)
                  signature = str node "signature" }
        with _ -> None

    let private parseReference (line: string) =
        try
            let node = JsonNode.Parse line
            Some
                { file = str node "file"
                  line = int' node "line"
                  column = int' node "column"
                  kind = str node "kind"
                  scope = str node "scope"
                  text = str node "text" }
        with _ -> None

    /// The extensions the helper parses, e.g. ".py"; empty when absent.
    let languages : Lazy<Set<string>> =
        lazy
            (match run [ "languages" ] Seq.empty with
             | Some lines ->
                 lines
                 |> List.choose (fun l ->
                     match l.Split(' ', StringSplitOptions.RemoveEmptyEntries) with
                     | [| ext; _ |] -> Some ext
                     | _ -> None)
                 |> Set.ofList
             | None -> Set.empty)

    /// Whether `file` is one the helper parses exactly.
    let supports (file: string) =
        available () && languages.Value.Contains(Path.GetExtension(file).ToLowerInvariant())

    /// Every definition in one file, in source order; None when the helper
    /// cannot answer (absent, unsupported language, parse failure).
    let outline (file: string) : Definition list option =
        if not (supports file) then None
        else run [ "outline"; file ] Seq.empty |> Option.map (List.choose (parseDefinition file))

    /// Definitions across `files` (only the supported ones are read),
    /// optionally those named `name`, exactly or ignoring case.
    let definitions (files: string seq) (name: string option) (ignoreCase: bool) : Definition list option =
        let supported = files |> Seq.filter supports |> List.ofSeq
        if supported.IsEmpty then Some []
        else
            let arguments =
                match name with
                | Some n -> [ "defs"; (if ignoreCase then "--iname" else "--name"); n ]
                | None -> [ "defs" ]
            run arguments supported |> Option.map (List.choose (parseDefinition ""))

    /// Every mention of `name` across `files` outside strings and comments,
    /// each with what the grammar says it is and the definition it sits in.
    let references (files: string seq) (name: string) : Reference list option =
        let supported = files |> Seq.filter supports |> List.ofSeq
        if supported.IsEmpty then Some []
        else run [ "refs"; name ] supported |> Option.map (List.choose parseReference)
