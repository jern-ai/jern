namespace Jern.Host

open System
open System.IO
open System.Text
open System.Text.Json
open System.Text.Json.Nodes

/// The run receipt — the flight recorder, made visible.
///
/// A receipt is a **pure function of a trace**: nothing accumulates it during
/// the run, so any trace, however old, can produce one (`jern receipt
/// <trace.jsonl>`). Everything it reports is something the run actually
/// recorded at the choke point that enforces policy; where the trace does not
/// say (an older, pre-envelope recording, or a run cut short), the receipt
/// says so rather than inventing a value.
module Receipt =

    type Summary =
        { tracePath: string
          schemaVersion: int option
          /// False when the trace has no run-started envelope: an older
          /// recording, summarized on a best-effort basis.
          hasEnvelope: bool
          /// False when the run never wrote run-finished — interrupted,
          /// killed, or still going.
          finished: bool
          /// Non-zero when lines could not be parsed (a truncated tail).
          unreadableLines: int
          runId: string option
          jernVersion: string option
          command: string option
          task: string option
          model: string option
          agent: string option
          status: string option
          statusReason: string option
          duration: TimeSpan option
          llmCalls: int
          inputTokens: int64
          outputTokens: int64
          budgetLlmCalls: int option
          budgetTokens: int option
          budgetExtended: int
          budgetDenied: bool
          /// Tool name → invocations, most-used first.
          tools: (string * int) list
          filesTouched: string list
          commits: int
          policyAllowed: int
          policyAsked: int
          policyDeniedByRule: int
          approvalsDenied: int
          denialReasons: string list
          spawns: int
          programs: int
          /// source, digest, grants-trusted, protected
          policyLayers: (string * string * bool * bool) list
          memoryReads: int
          memoryWrites: int }

    let private empty path =
        { tracePath = path; schemaVersion = None; hasEnvelope = false; finished = false
          unreadableLines = 0; runId = None; jernVersion = None; command = None; task = None
          model = None; agent = None; status = None; statusReason = None; duration = None
          llmCalls = 0; inputTokens = 0L; outputTokens = 0L
          budgetLlmCalls = None; budgetTokens = None; budgetExtended = 0; budgetDenied = false
          tools = []; filesTouched = []; commits = 0
          policyAllowed = 0; policyAsked = 0; policyDeniedByRule = 0; approvalsDenied = 0
          denialReasons = []; spawns = 0; programs = 0; policyLayers = []
          memoryReads = 0; memoryWrites = 0 }

    // -- small readers over JsonObject, all total ---------------------------

    let private field (o: JsonObject) (key: string) : JsonNode option =
        match o.[key] with
        | null -> None
        | node -> Some node

    let private str (o: JsonObject) (key: string) =
        field o key |> Option.bind (fun v -> try Some(v.GetValue<string>()) with _ -> None)

    let private int64Of (o: JsonObject) (key: string) =
        field o key |> Option.bind (fun v -> try Some(v.GetValue<int64>()) with _ -> None)

    let private intOf (o: JsonObject) (key: string) =
        int64Of o key |> Option.map int

    let private child (o: JsonObject) (key: string) =
        match field o key with
        | Some (:? JsonObject as c) -> Some c
        | _ -> None

    let private boolOf (o: JsonObject) (key: string) =
        field o key |> Option.bind (fun v -> try Some(v.GetValue<bool>()) with _ -> None)

    /// Read a trace into a summary. Unknown event names are ignored — a
    /// newer jern may write events this build has never heard of — but an
    /// unknown *major* schema version is refused with guidance, because
    /// then the events we do recognize may not mean what we think.
    let ofTrace (path: string) : Result<Summary, string> =
        if not (File.Exists path) then Error(sprintf "no trace at '%s'" path)
        else

        let mutable s = empty path
        let toolCounts = Collections.Generic.Dictionary<string, int>()
        let files = ResizeArray<string>()
        let reasons = ResizeArray<string>()
        let layers = ResizeArray<string * string * bool * bool>()
        // Tool calls nest (a kernel_eval program's inner calls are traced
        // between the outer call and its result), so pairing a result with
        // its call is a stack, not a queue.
        let pending = Collections.Generic.Stack<string * string option>()
        let mutable firstTs = None
        let mutable lastTs = None
        let mutable versionError = None

        for line in File.ReadLines path do
            if line.Trim() <> "" then
                let parsed =
                    try
                        match JsonNode.Parse(line: string) with
                        | :? JsonObject as o -> Some o
                        | _ -> None
                    with _ -> None
                match parsed with
                | None -> s <- { s with unreadableLines = s.unreadableLines + 1 }
                | Some doc ->
                    (match str doc "ts" with
                     | Some ts ->
                         if firstTs.IsNone then firstTs <- Some ts
                         lastTs <- Some ts
                     | None -> ())
                    match str doc "event" |> Option.defaultValue "" with
                    | "run-started" ->
                        let version = intOf doc "schema_version"
                        (match version with
                         | Some v when v > Trace.schemaVersion ->
                             versionError <-
                                 Some(sprintf
                                          "this trace uses schema version %d; this jern (%s) reads version %d — upgrade jern to summarize it"
                                          v AgentEnv.version Trace.schemaVersion)
                         | _ -> ())
                        let budget = child doc "budget"
                        s <-
                            { s with
                                hasEnvelope = true
                                schemaVersion = version
                                runId = str doc "run_id"
                                jernVersion = str doc "jern_version"
                                command = str doc "command"
                                task = str doc "task"
                                model = str doc "model"
                                agent = str doc "agent"
                                budgetLlmCalls = budget |> Option.bind (fun b -> intOf b "llm_calls")
                                budgetTokens = budget |> Option.bind (fun b -> intOf b "tokens") }
                        match field doc "policy" with
                        | Some (:? JsonArray as array) ->
                            for item in array do
                                match item with
                                | :? JsonObject as l ->
                                    layers.Add(
                                        defaultArg (str l "source") "?",
                                        defaultArg (str l "digest") "",
                                        true,
                                        defaultArg (boolOf l "protected") false)
                                | _ -> ()
                        | _ -> ()
                    | "run-finished" ->
                        s <-
                            { s with
                                finished = true
                                status = str doc "status"
                                statusReason = str doc "reason"
                                duration =
                                    intOf doc "duration_ms"
                                    |> Option.map (fun ms -> TimeSpan.FromMilliseconds(float ms)) }
                    | "policy-layer" ->
                        // The resolved layer (whether its grants survived
                        // trust) supersedes what run-started announced.
                        let source = defaultArg (str doc "source") "?"
                        let entry =
                            source,
                            defaultArg (str doc "digest") "",
                            defaultArg (boolOf doc "grants") true,
                            defaultArg (boolOf doc "protected") false
                        let existing = layers.FindIndex(fun (name, _, _, _) -> name = source)
                        if existing >= 0 then layers.[existing] <- entry else layers.Add entry
                    | "llm-call" -> s <- { s with llmCalls = s.llmCalls + 1 }
                    | "llm-response" ->
                        match child doc "response" |> Option.bind (fun r -> child r "usage") with
                        | Some usage ->
                            s <-
                                { s with
                                    inputTokens = s.inputTokens + defaultArg (int64Of usage "input_tokens") 0L
                                    outputTokens = s.outputTokens + defaultArg (int64Of usage "output_tokens") 0L }
                        | None -> ()
                    | "tool-call" ->
                        match child doc "call" with
                        | Some call ->
                            let name = defaultArg (str call "name") "?"
                            toolCounts.[name] <- (match toolCounts.TryGetValue name with
                                                  | true, n -> n + 1
                                                  | _ -> 1)
                            if name = "kernel_eval" then s <- { s with programs = s.programs + 1 }
                            let path = child call "input" |> Option.bind (fun i -> str i "path")
                            pending.Push(name, path)
                        | None -> pending.Push("?", None)
                    | "tool-result" ->
                        if pending.Count > 0 then
                            let name, path = pending.Pop()
                            let failed =
                                child doc "result"
                                |> Option.bind (fun r -> boolOf r "is_error")
                                |> Option.defaultValue false
                            // A file counts as touched only if the write
                            // actually succeeded.
                            if not failed && (name = "edit_file" || name = "write_file") then
                                match path with
                                | Some p when not (files.Contains p) -> files.Add p
                                | _ -> ()
                    | "policy-decision" ->
                        match str doc "decision" with
                        | Some "allow" -> s <- { s with policyAllowed = s.policyAllowed + 1 }
                        | Some "ask" -> s <- { s with policyAsked = s.policyAsked + 1 }
                        | Some reason ->
                            s <- { s with policyDeniedByRule = s.policyDeniedByRule + 1 }
                            if not (reasons.Contains reason) then reasons.Add reason
                        | None -> ()
                    | "approval-denied" -> s <- { s with approvalsDenied = s.approvalsDenied + 1 }
                    | "git-commit" -> s <- { s with commits = s.commits + 1 }
                    | "spawn" -> s <- { s with spawns = s.spawns + 1 }
                    | "budget-extended" -> s <- { s with budgetExtended = s.budgetExtended + 1 }
                    | "budget-denied" -> s <- { s with budgetDenied = true }
                    | "memory-recall" -> s <- { s with memoryReads = s.memoryReads + 1 }
                    | "memory-remember" -> s <- { s with memoryWrites = s.memoryWrites + 1 }
                    | "log" ->
                        // Pre-envelope traces carry the task in the agent's
                        // own opening log event; use it when nothing better.
                        match child doc "data" with
                        | Some data when str data "event" = Some "agent-started" ->
                            if s.task.IsNone then s <- { s with task = str data "task" }
                        | _ -> ()
                    | _ -> ()   // an event this build does not know: ignore

        match versionError with
        | Some message -> Error message
        | None ->
            // Without an envelope, fall back to wall-clock between the first
            // and last timestamps — explicitly weaker than a recorded duration.
            let duration =
                match s.duration, firstTs, lastTs with
                | Some d, _, _ -> Some d
                | None, Some a, Some b ->
                    (try Some(DateTime.Parse(b, Globalization.CultureInfo.InvariantCulture)
                              - DateTime.Parse(a, Globalization.CultureInfo.InvariantCulture))
                     with _ -> None)
                | _ -> None
            Ok
                { s with
                    duration = duration
                    tools =
                        toolCounts
                        |> Seq.map (fun kv -> kv.Key, kv.Value)
                        |> Seq.sortBy (fun (name, count) -> -count, name)
                        |> List.ofSeq
                    filesTouched = List.ofSeq files
                    denialReasons = List.ofSeq reasons
                    policyLayers = List.ofSeq layers }

    /// The newest `.jern/trace-*.jsonl` in a workspace.
    let latestTrace (root: string) : string option =
        let dir = Path.Combine(root, ".jern")
        if not (Directory.Exists dir) then None
        else
            Directory.EnumerateFiles(dir, "trace-*.jsonl")
            |> Seq.sortByDescending (fun f -> File.GetLastWriteTimeUtc f)
            |> Seq.tryHead

    // -- rendering ----------------------------------------------------------

    /// Colors are the front-end's business; the host renders structure.
    type Palette =
        { title: string -> string
          label: string -> string
          dim: string -> string
          good: string -> string
          bad: string -> string }

    let plain =
        { title = id; label = id; dim = id; good = id; bad = id }

    let private compactCount (n: int64) =
        if n >= 1_000L then sprintf "%.1fk" (float n / 1000.0) else string n

    let private plural n singular =
        if n = 1 then sprintf "1 %s" singular else sprintf "%d %ss" n singular

    /// Paths read better relative to where the reader is standing.
    let private shortPath (path: string) =
        let cwd = Directory.GetCurrentDirectory()
        let full = try Path.GetFullPath path with _ -> path
        if full.StartsWith(cwd + string Path.DirectorySeparatorChar, StringComparison.Ordinal) then
            full.Substring(cwd.Length + 1)
        else path

    let private describeDuration (d: TimeSpan) =
        if d.TotalMinutes >= 1.0 then sprintf "%dm %02ds" (int d.TotalMinutes) d.Seconds
        else sprintf "%.1fs" d.TotalSeconds

    /// The one-line facts a reader wants, in a fixed order. Shared by every
    /// rendering, so the terminal, Markdown, and the UI cannot drift apart.
    let rows (s: Summary) : (string * string) list =
        [ let model = defaultArg s.model "model unknown"
          let budget =
              match s.budgetLlmCalls with
              | Some limit -> sprintf " · budget %d/%d" s.llmCalls limit
              | None -> ""
          yield "model calls",
                sprintf "%d (%s) · %s in / %s out%s"
                    s.llmCalls model (compactCount s.inputTokens) (compactCount s.outputTokens) budget

          if not s.tools.IsEmpty then
              yield "tools",
                    s.tools |> List.map (fun (name, n) -> sprintf "%s ×%d" name n) |> String.concat " · "

          if not s.filesTouched.IsEmpty then
              let commits =
                  if s.commits > 0 then sprintf "   (%s, undo with `jern undo`)" (plural s.commits "jern commit")
                  else ""
              yield "files touched", String.concat " · " s.filesTouched + commits

          let approved = max 0 (s.policyAsked - s.approvalsDenied)
          let denied = s.policyDeniedByRule + s.approvalsDenied
          if s.policyAllowed + s.policyAsked + denied > 0 then
              let reason =
                  if s.denialReasons.IsEmpty then ""
                  else
                      // The reasons the model saw already say "policy:";
                      // the row is labelled policy, so drop the prefix and
                      // any trailing "(source)" attribution — the layer is
                      // named in the policy-layers row.
                      let first = s.denialReasons.Head.Split('\n').[0]
                      let body = if first.StartsWith "policy: " then first.Substring 8 else first
                      let body =
                          match body.LastIndexOf " (" with
                          | i when i > 0 && body.EndsWith ")" -> body.Substring(0, i)
                          | _ -> body
                      sprintf " (%s)" (if body.Length > 60 then body.Substring(0, 59) + "…" else body)
              yield "policy",
                    sprintf "%d allowed · %d approved by you · %d denied%s"
                        s.policyAllowed approved denied reason

          if s.spawns > 0 || s.programs > 0 then
              yield "delegation",
                    [ if s.spawns > 0 then yield plural s.spawns "subagent"
                      if s.programs > 0 then yield plural s.programs "kernel_eval program" ]
                    |> String.concat " · "

          if s.memoryReads > 0 || s.memoryWrites > 0 then
              yield "memory", sprintf "%d read · %d written" s.memoryReads s.memoryWrites

          if not s.policyLayers.IsEmpty then
              yield "policy layers",
                    s.policyLayers
                    |> List.map (fun (source, digest, grants, isProtected) ->
                        sprintf "%s %s%s%s"
                            source
                            (if digest.Length >= 12 then digest.Substring(0, 12) else digest)
                            (if isProtected then " protected" else "")
                            (if grants then "" else " (grants dropped)"))
                    |> String.concat " · "

          if s.budgetExtended > 0 then
              yield "budget", sprintf "extended %s by you" (plural s.budgetExtended "time")

          yield "trace", shortPath s.tracePath ]

    let private headline (s: Summary) =
        let run = match s.runId with Some id -> " · run " + id | None -> ""
        let duration =
            match s.duration with
            | Some d -> " · " + describeDuration d
            | None -> ""
        let outcome =
            if not s.finished then "unfinished"
            else
                match s.status with
                | Some "ok" -> "ok"
                | Some other -> other
                | None -> "unknown"
        run, duration, outcome

    let render (palette: Palette) (s: Summary) : string =
        let run, duration, outcome = headline s
        let mark = if s.finished && s.status = Some "ok" then palette.good else palette.bad
        let b = StringBuilder()
        b.AppendLine(palette.title "receipt" + palette.dim (run + duration + " · ") + mark outcome) |> ignore
        match s.statusReason with
        | Some reason -> b.AppendLine("  " + palette.bad reason) |> ignore
        | None -> ()
        for label, value in rows s do
            // Pad on the label's *visible* width: a styled label carries ANSI
            // escapes that %-14s would count, so a colored terminal would get
            // no padding at all and the columns would collapse.
            let padding = String(' ', max 1 (14 - label.Length))
            b.AppendLine("  " + palette.label label + padding + value) |> ignore
        if not s.hasEnvelope then
            b.AppendLine(palette.dim "  (older trace: no run envelope, so model, budget, and outcome are unknown)")
            |> ignore
        if s.unreadableLines > 0 then
            b.AppendLine(palette.dim (sprintf "  (%s — the trace looks truncated)"
                                          (plural s.unreadableLines "unreadable line")))
            |> ignore
        b.ToString()

    /// Markdown for a PR comment or a job summary.
    let renderMarkdown (s: Summary) : string =
        let run, duration, outcome = headline s
        let mark = if s.finished && s.status = Some "ok" then "✅" else "⚠️"
        let b = StringBuilder()
        b.AppendLine(sprintf "**jern receipt**%s%s · %s %s" run duration mark outcome) |> ignore
        (match s.task with
         | Some task -> b.AppendLine().AppendLine(sprintf "> %s" (task.Split('\n').[0])) |> ignore
         | None -> ())
        b.AppendLine() |> ignore
        b.AppendLine("| | |") |> ignore
        b.AppendLine("|---|---|") |> ignore
        for label, value in rows s do
            b.AppendLine(sprintf "| %s | %s |" label (value.Replace("|", "\\|"))) |> ignore
        if not s.hasEnvelope then
            b.AppendLine().AppendLine("_Older trace: no run envelope, so model, budget, and outcome are unknown._")
            |> ignore
        if s.unreadableLines > 0 then
            b.AppendLine().AppendLine(sprintf "_%s — the trace looks truncated._"
                                          (plural s.unreadableLines "unreadable line"))
            |> ignore
        b.ToString()

    /// The same facts as data, for tooling that wants to gate on them.
    let renderJson (s: Summary) : string =
        let doc = JsonObject()
        doc.["trace"] <- JsonValue.Create s.tracePath
        doc.["schema_version"] <-
            (match s.schemaVersion with Some v -> JsonValue.Create v :> JsonNode | None -> null)
        doc.["complete"] <- JsonValue.Create s.finished
        doc.["has_envelope"] <- JsonValue.Create s.hasEnvelope
        let optional (value: string option) : JsonNode =
            match value with Some v -> JsonValue.Create v :> JsonNode | None -> null
        doc.["run_id"] <- optional s.runId
        doc.["jern_version"] <- optional s.jernVersion
        doc.["command"] <- optional s.command
        doc.["task"] <- optional s.task
        doc.["model"] <- optional s.model
        doc.["agent"] <- optional s.agent
        doc.["status"] <- optional s.status
        doc.["status_reason"] <- optional s.statusReason
        doc.["duration_ms"] <-
            (match s.duration with
             | Some d -> JsonValue.Create(int d.TotalMilliseconds) :> JsonNode
             | None -> null)
        doc.["llm_calls"] <- JsonValue.Create s.llmCalls
        doc.["input_tokens"] <- JsonValue.Create s.inputTokens
        doc.["output_tokens"] <- JsonValue.Create s.outputTokens
        doc.["budget_llm_calls"] <-
            (match s.budgetLlmCalls with Some n -> JsonValue.Create n :> JsonNode | None -> null)
        doc.["budget_tokens"] <-
            (match s.budgetTokens with Some n -> JsonValue.Create n :> JsonNode | None -> null)
        doc.["budget_extended"] <- JsonValue.Create s.budgetExtended
        doc.["budget_denied"] <- JsonValue.Create s.budgetDenied
        let tools = JsonObject()
        for name, count in s.tools do tools.[name] <- JsonValue.Create count
        doc.["tools"] <- tools
        let files = JsonArray()
        for f in s.filesTouched do files.Add(JsonValue.Create f)
        doc.["files_touched"] <- files
        doc.["commits"] <- JsonValue.Create s.commits
        let policy = JsonObject()
        policy.["allowed"] <- JsonValue.Create s.policyAllowed
        policy.["asked"] <- JsonValue.Create s.policyAsked
        policy.["denied_by_rule"] <- JsonValue.Create s.policyDeniedByRule
        policy.["approvals_denied"] <- JsonValue.Create s.approvalsDenied
        let denials = JsonArray()
        for r in s.denialReasons do denials.Add(JsonValue.Create r)
        policy.["denial_reasons"] <- denials
        let layers = JsonArray()
        for source, digest, grants, isProtected in s.policyLayers do
            let l = JsonObject()
            l.["source"] <- JsonValue.Create source
            l.["digest"] <- JsonValue.Create digest
            l.["grants_trusted"] <- JsonValue.Create grants
            l.["protected"] <- JsonValue.Create isProtected
            layers.Add l
        policy.["layers"] <- layers
        doc.["policy"] <- policy
        doc.["spawns"] <- JsonValue.Create s.spawns
        doc.["programs"] <- JsonValue.Create s.programs
        doc.["memory_reads"] <- JsonValue.Create s.memoryReads
        doc.["memory_writes"] <- JsonValue.Create s.memoryWrites
        doc.["unreadable_lines"] <- JsonValue.Create s.unreadableLines
        doc.ToJsonString(JsonSerializerOptions(WriteIndented = true))
