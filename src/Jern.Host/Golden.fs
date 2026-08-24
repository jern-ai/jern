namespace Jern.Host

open System
open System.IO
open System.Text
open System.Text.Json
open System.Text.Json.Nodes

/// Golden sessions — `jern test` for people who will never write a `deftest`.
///
/// Record a real task once; the trace is committed under `.jern/golden/`.
/// From then on `jern golden check` re-runs it offline against the *current*
/// agent source and the *current* effective policy, with model and tool
/// results answered from the recording. Two different things are protected,
/// deliberately:
///
/// - **the byte-exact effect sequence**, by replay: any change in what the
///   agent would do shows up as a divergence, with the recorded-vs-actual
///   difference. Re-recording blesses such a change.
/// - **the meaning**, by declarative assertions in a sidecar file: edits
///   stayed under `src/`, `shell` was never used, at most N files changed.
///   These are evaluated against the recording itself, so they keep their
///   force *across* a re-record — blessing new bytes cannot quietly bless
///   an agent that now shells out.
///
/// Boundaries worth keeping straight: `jern replay` is ad-hoc forensics over
/// any past run; `jern golden check` is a committed snapshot in CI; `jern
/// test` is an authored suite with semantic trajectory assertions in Kernel.
module Golden =

    type Assertions =
        { editsWithin: string list
          noTools: string list
          maxFilesEdited: int option
          maxLlmCalls: int option
          maxTokens: int option }

    let noAssertions =
        { editsWithin = []; noTools = []; maxFilesEdited = None; maxLlmCalls = None; maxTokens = None }

    type Metadata =
        { task: string
          recordedWith: string
          assertions: Assertions }

    /// A golden session on disk: its recording and its sidecar.
    type Entry =
        { slug: string
          tracePath: string
          metadataPath: string
          metadata: Metadata }

    type Verdict =
        { entry: Entry
          /// The recorded effect sequence no longer matches what the agent
          /// would do now.
          divergence: string option
          /// Declarative assertions the *recording* violates.
          failures: string list }
        member this.Passed = this.divergence.IsNone && this.failures.IsEmpty

    // -----------------------------------------------------------------------

    let directory (root: string) = Path.Combine(root, ".jern", "golden")

    /// A stable file name from a task sentence.
    let slugify (task: string) =
        let b = StringBuilder()
        let mutable lastDash = true
        for c in task.Trim().ToLowerInvariant() do
            if Char.IsLetterOrDigit c then
                b.Append c |> ignore
                lastDash <- false
            elif not lastDash then
                b.Append '-' |> ignore
                lastDash <- true
        let slug = b.ToString().Trim('-')
        let slug = if slug.Length > 48 then slug.Substring(0, 48).Trim('-') else slug
        if slug = "" then "golden" else slug

    let private stringArray (name: string) (node: JsonNode) : Result<string list, string> =
        match node with
        | :? JsonArray as array ->
            array
            |> Seq.fold
                (fun acc item ->
                    match acc, item with
                    | Error e, _ -> Error e
                    | Ok values, (:? JsonValue as v) ->
                        (match v.TryGetValue<string>() with
                         | true, s -> Ok(s :: values)
                         | _ -> Error(sprintf "assert.%s must be an array of strings" name))
                    | Ok _, _ -> Error(sprintf "assert.%s must be an array of strings" name))
                (Ok [])
            |> Result.map List.rev
        | _ -> Error(sprintf "assert.%s must be an array of strings" name)

    /// Parse a sidecar. Unknown assertion keys are an error: a typo in a rule
    /// meant to protect meaning must never look like it passed.
    let parseMetadata (json: string) : Result<Metadata, string> =
        try
            match JsonNode.Parse(json: string) with
            | :? JsonObject as doc ->
                let task =
                    match doc.["task"] with
                    | null -> ""
                    | t -> (try t.GetValue<string>() with _ -> "")
                let recordedWith =
                    match doc.["recorded_with"] with
                    | null -> ""
                    | v -> (try v.GetValue<string>() with _ -> "")
                match doc.["assert"] with
                | null -> Ok { task = task; recordedWith = recordedWith; assertions = noAssertions }
                | :? JsonObject as a ->
                    let known = set [ "edits_within"; "no_tools"; "max_files_edited"; "max_llm_calls"; "max_tokens" ]
                    let unknown =
                        a |> Seq.map (fun kv -> kv.Key) |> Seq.filter (known.Contains >> not) |> List.ofSeq
                    if not unknown.IsEmpty then
                        Error(sprintf "unknown assert key(s): %s (known: %s)"
                                  (String.Join(", ", unknown)) (String.Join(", ", known)))
                    else
                        let list name =
                            match a.[name: string] with
                            | null -> Ok []
                            | node -> stringArray name node
                        let number (name: string) =
                            match a.[name] with
                            | null -> Ok None
                            | v ->
                                match (try Some(v.GetValue<int>()) with _ -> None) with
                                | Some n when n >= 0 -> Ok(Some n)
                                | _ -> Error(sprintf "assert.%s must be a non-negative integer" name)
                        match list "edits_within", list "no_tools" with
                        | Error e, _ | _, Error e -> Error e
                        | Ok editsWithin, Ok noTools ->
                            match number "max_files_edited", number "max_llm_calls", number "max_tokens" with
                            | Error e, _, _ | _, Error e, _ | _, _, Error e -> Error e
                            | Ok maxFiles, Ok maxCalls, Ok maxTokens ->
                                Ok { task = task
                                     recordedWith = recordedWith
                                     assertions =
                                       { editsWithin = editsWithin
                                         noTools = noTools
                                         maxFilesEdited = maxFiles
                                         maxLlmCalls = maxCalls
                                         maxTokens = maxTokens } }
                | _ -> Error "assert must be a JSON object"
            | _ -> Error "a golden sidecar must be a JSON object"
        with ex -> Error("unreadable golden sidecar: " + ex.Message)

    let metadataJson (metadata: Metadata) =
        let doc = JsonObject()
        doc.["task"] <- JsonValue.Create metadata.task
        doc.["recorded_with"] <- JsonValue.Create metadata.recordedWith
        let a = JsonObject()
        let strings (values: string list) =
            let array = JsonArray()
            for v in values do array.Add(JsonValue.Create v)
            array
        if not metadata.assertions.editsWithin.IsEmpty then
            a.["edits_within"] <- strings metadata.assertions.editsWithin
        if not metadata.assertions.noTools.IsEmpty then
            a.["no_tools"] <- strings metadata.assertions.noTools
        (match metadata.assertions.maxFilesEdited with
         | Some n -> a.["max_files_edited"] <- JsonValue.Create n
         | None -> ())
        (match metadata.assertions.maxLlmCalls with
         | Some n -> a.["max_llm_calls"] <- JsonValue.Create n
         | None -> ())
        (match metadata.assertions.maxTokens with
         | Some n -> a.["max_tokens"] <- JsonValue.Create n
         | None -> ())
        doc.["assert"] <- a
        doc.ToJsonString(JsonSerializerOptions(WriteIndented = true)) + "\n"

    /// Every golden session in a workspace, in slug order.
    let list (root: string) : Result<Entry list, string> =
        let dir = directory root
        if not (Directory.Exists dir) then Ok []
        else
            Directory.EnumerateFiles(dir, "*.jsonl")
            |> Seq.sortBy Path.GetFileName
            |> Seq.fold
                (fun acc trace ->
                    match acc with
                    | Error e -> Error e
                    | Ok entries ->
                        let slug = Path.GetFileNameWithoutExtension trace
                        let metadataPath = Path.Combine(dir, slug + ".json")
                        let parsed =
                            if File.Exists metadataPath then
                                parseMetadata (File.ReadAllText metadataPath)
                                |> Result.mapError (fun e -> sprintf "%s: %s" metadataPath e)
                            else
                                Ok { task = ""; recordedWith = ""; assertions = noAssertions }
                        parsed
                        |> Result.map (fun metadata ->
                            entries
                            @ [ { slug = slug
                                  tracePath = trace
                                  metadataPath = metadataPath
                                  metadata = metadata } ]))
                (Ok [])

    /// Evaluate the declarative assertions against a recording's own summary.
    /// Deliberately not against the replay: these must keep their force when
    /// a deliberate re-record changes the bytes.
    let evaluate (assertions: Assertions) (summary: Receipt.Summary) : string list =
        [ if not assertions.editsWithin.IsEmpty then
            let escaped =
                summary.filesTouched
                |> List.filter (fun path ->
                    assertions.editsWithin
                    |> List.forall (fun prefix -> not (path.StartsWith(prefix, StringComparison.Ordinal))))
            if not escaped.IsEmpty then
                yield sprintf "edited outside %s: %s"
                          (String.Join(", ", assertions.editsWithin)) (String.Join(", ", escaped))

          for tool in assertions.noTools do
              match summary.tools |> List.tryFind (fun (name, _) -> name = tool) with
              | Some (_, count) -> yield sprintf "used %s %d time(s); it is forbidden here" tool count
              | None -> ()

          match assertions.maxFilesEdited with
          | Some limit when summary.filesTouched.Length > limit ->
              yield sprintf "changed %d files (limit %d)" summary.filesTouched.Length limit
          | _ -> ()

          match assertions.maxLlmCalls with
          | Some limit when summary.llmCalls > limit ->
              yield sprintf "made %d model calls (limit %d)" summary.llmCalls limit
          | _ -> ()

          match assertions.maxTokens with
          | Some limit when int (summary.inputTokens + summary.outputTokens) > limit ->
              yield sprintf "spent %d tokens (limit %d)"
                        (int (summary.inputTokens + summary.outputTokens)) limit
          | _ -> () ]

    /// Check one golden session: replay it against the current agent and
    /// policy, then evaluate its assertions. Offline; no API key.
    let check (replay: string -> Result<Replay.Outcome, string>) (entry: Entry) : Verdict =
        let divergence =
            match replay entry.tracePath with
            | Error message -> Some message
            | Ok (Replay.Diverged report) -> Some report
            | Ok (Replay.Completed _) -> None
        let failures =
            match Receipt.ofTrace entry.tracePath with
            | Error message -> [ "cannot summarize the recording: " + message ]
            | Ok summary -> evaluate entry.metadata.assertions summary
        { entry = entry; divergence = divergence; failures = failures }

    /// Markdown for a PR comment or job summary.
    let renderMarkdown (verdicts: Verdict list) : string =
        let b = StringBuilder()
        let failed = verdicts |> List.filter (fun v -> not v.Passed)
        b.AppendLine(
            sprintf "**jern golden** · %d checked · %s"
                verdicts.Length
                (if failed.IsEmpty then "✅ all match"
                 else sprintf "⚠️ %d changed" failed.Length))
        |> ignore
        b.AppendLine() |> ignore
        for v in verdicts do
            let mark = if v.Passed then "✅" else "⚠️"
            b.AppendLine(sprintf "- %s `%s`%s"
                             mark v.entry.slug
                             (if v.entry.metadata.task = "" then ""
                              else " — " + v.entry.metadata.task.Split('\n').[0]))
            |> ignore
            match v.divergence with
            | Some report ->
                b.AppendLine() |> ignore
                b.AppendLine("  <details><summary>behavior diverged from the recording</summary>") |> ignore
                b.AppendLine() |> ignore
                b.AppendLine("  ```") |> ignore
                for line in report.Split('\n') do b.AppendLine("  " + line) |> ignore
                b.AppendLine("  ```") |> ignore
                b.AppendLine("  </details>") |> ignore
                b.AppendLine() |> ignore
            | None -> ()
            for failure in v.failures do
                b.AppendLine(sprintf "  - assertion failed: %s" failure) |> ignore
        b.ToString()
