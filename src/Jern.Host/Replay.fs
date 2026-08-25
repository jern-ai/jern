namespace Jern.Host

open System
open System.Collections.Generic
open System.IO
open System.Text.Json.Nodes
open IronKernel.Ast
open IronKernel.Errors

/// `jern replay` — time-travel debugging on a recorded session.
///
/// The JSONL trace is byte-exact and captures every effect a run performed:
/// each llm request with its response, each tool call with its result. That
/// makes a whole session re-runnable *offline*: the agent source executes
/// again for real, but both `jern/llm-call` and `jern/tool-call` are
/// answered from the recording, so nothing touches the model, the network,
/// or the workspace.
///
/// A faithful replay completes silently. The interesting use is the fork:
/// swap one handler — a stricter workspace policy via `--policy`, or an
/// edited agent via `--agent` — and replay. The first effect that no longer
/// matches its recording is reported with the exact recorded-vs-actual
/// difference: what this rule (or edit) *would have changed* about a run
/// that already happened.
module Replay =

    type Outcome =
        /// The run re-executed exactly as recorded.
        | Completed of llmCalls: int * toolCalls: int
        /// The run left the recording; the report pinpoints where and how.
        | Diverged of report: string

    type Options =
        { tracePath: string
          /// The agent package to run — the same one as the recording for a
          /// faithful replay, or an edited copy to probe a change.
          agentDir: string
          /// A policy file substituted for the workspace's own (it is copied
          /// into the scratch workspace as .jern/policy.ikr).
          policyFile: string option
          /// jern/workspace-config for the replayed session — pass the same
          /// configuration the recording ran with (test_command, thinking…)
          /// or the requests will differ for that reason alone.
          agentConfig: LispVal
          /// MCP servers to connect for toolset parity in the requests;
          /// their tools are advertised but never invoked — every call is
          /// answered from the recording.
          mcpServers: Mcp.ServerSpec list
          /// Policy from configuration, so a replay is judged by the rules
          /// in force *now*. Grants are taken as given: a replay performs no
          /// real effects (every call answers from the trace), so there is
          /// nothing for a trust prompt to protect here.
          policySources: PolicyConfig.Source list }

    type private Recorded =
        { task: string
          llm: Queue<JsonNode * JsonNode>      // request, response
          tools: Queue<JsonNode * JsonNode> }  // call, result

    let private parseTrace (path: string) : Result<Recorded, string> =
        try
            let mutable task = None
            let llm = Queue<JsonNode * JsonNode>()
            let tools = Queue<JsonNode * JsonNode>()
            let mutable pendingLlm: JsonNode option = None
            // Tool events can nest: a kernel_eval program's inner calls are
            // traced between the outer call and its result, so pairing is a
            // stack. The outer kernel_eval pair itself is *dropped* — replay
            // re-executes programs, and it is their inner calls that must
            // line up with the recording.
            let pendingTools = Stack<JsonNode>()
            let isKernelEval (call: JsonNode) =
                match call with
                | :? JsonObject as o ->
                    match o.["name"] with
                    | null -> false
                    | n -> (try n.GetValue<string>() = "kernel_eval" with _ -> false)
                | _ -> false
            for line in File.ReadLines path do
                if line.Trim() <> "" then
                    let doc = JsonNode.Parse(line).AsObject()
                    let field (name: string) : JsonNode option =
                        match doc.[name] with
                        | null -> None
                        | node -> Some node
                    let event =
                        match field "event" with
                        | Some node -> (try node.GetValue<string>() with _ -> "")
                        | None -> ""
                    match event with
                    | "log" ->
                        match field "data" with
                        | Some (:? JsonObject as data) ->
                            let inner (name: string) : JsonNode option =
                                match data.[name] with
                                | null -> None
                                | node -> Some node
                            let isStart =
                                match inner "event" with
                                | Some e -> (try e.GetValue<string>() = "agent-started" with _ -> false)
                                | None -> false
                            if isStart then
                                match inner "task" with
                                | Some t -> task <- (try Some(t.GetValue<string>()) with _ -> None)
                                | None -> ()
                        | _ -> ()
                    | "llm-call" -> pendingLlm <- field "request"
                    | "llm-response" ->
                        match pendingLlm, field "response" with
                        | Some request, Some response ->
                            llm.Enqueue(request, response)
                            pendingLlm <- None
                        | _ -> ()
                    | "tool-call" ->
                        match field "call" with
                        | Some call -> pendingTools.Push call
                        | None -> ()
                    | "tool-result" ->
                        match (if pendingTools.Count > 0 then Some(pendingTools.Pop()) else None),
                              field "result" with
                        | Some call, Some result when not (isKernelEval call) ->
                            tools.Enqueue(call, result)
                        | _ -> ()
                    | _ -> ()
            match task with
            | None ->
                Error "this trace has no agent-started event — only `jern run` traces can be replayed"
            | Some task -> Ok { task = task; llm = llm; tools = tools }
        with ex ->
            Error(sprintf "cannot read trace '%s': %s" path ex.Message)

    /// Canonical JSON for comparison: both sides go through JsonNode's
    /// serializer, so escaping and number formatting are normalized.
    let private canonical (value: LispVal) =
        JsonNode.Parse(Json.serialize value).ToJsonString()

    let private diffReport (ordinal: int) (kind: string) (recorded: string) (actual: string) =
        let firstDiff =
            Seq.zip recorded actual
            |> Seq.tryFindIndex (fun (a, b) -> a <> b)
            |> Option.defaultValue (min recorded.Length actual.Length)
        let excerpt (s: string) =
            let from = max 0 (firstDiff - 60)
            let piece = s.Substring(from, min 160 (s.Length - from))
            (if from > 0 then "…" else "") + piece
            + (if from + 160 < s.Length then "…" else "")
        sprintf "diverged from the recording at %s #%d.\n  recorded: %s\n  actual:   %s"
            kind ordinal (excerpt recorded) (excerpt actual)

    /// Console.Out is process-global, so two replays running at once could
    /// restore it in the wrong order and leave it silenced. Replays are
    /// sequential in the CLI; this keeps them sequential everywhere else too.
    ///
    /// (Not unit-tested on purpose: asserting it means swapping the global
    /// Console.Out inside a test, which swallows the runner's own output and
    /// makes the suite flaky. The invariant is verified end to end instead —
    /// `jern golden check --md` must emit Markdown and nothing else.)
    let private consoleLock = obj ()

    /// Re-run the recorded session. Side-effect-free: the scratch workspace
    /// is a throwaway temp directory and every effect answers from the trace.
    let run (options: Options) : Result<Outcome, string> =
        match parseTrace options.tracePath with
        | Error message -> Error message
        | Ok recorded ->
            let llmTotal = recorded.llm.Count
            let toolTotal = recorded.tools.Count
            let scratch = Path.Combine(Path.GetTempPath(), "jern-replay-" + Guid.NewGuid().ToString("N"))
            Directory.CreateDirectory scratch |> ignore
            try
                match options.policyFile with
                | Some file ->
                    if not (File.Exists file) then Error(sprintf "policy file '%s' does not exist" file)
                    else
                        Directory.CreateDirectory(Path.Combine(scratch, ".jern")) |> ignore
                        File.Copy(file, Path.Combine(scratch, ".jern", "policy.ikr"))
                        Ok ()
                | None -> Ok ()
                |> Result.bind (fun () ->
                    let mutable divergence: string option = None
                    let diverge report =
                        divergence <- Some report
                        Choice1Of2 (Default "replay diverged")
                    let bridge: AnthropicBridge.LlmBridge =
                        fun request ->
                            if divergence.IsSome then Choice1Of2 (Default "replay diverged")
                            elif recorded.llm.Count = 0 then
                                diverge (sprintf "diverged from the recording: the replayed run performs more than the %d recorded model calls" llmTotal)
                            else
                                let ordinal = llmTotal - recorded.llm.Count + 1
                                let recordedRequest, response = recorded.llm.Dequeue()
                                let expected = recordedRequest.ToJsonString()
                                let actual = canonical request
                                if expected <> actual then
                                    diverge (diffReport ordinal "llm request" expected actual)
                                else
                                    Choice2Of2 (Json.toLispVal response)
                    let toolDispatch (call: LispVal) : ThrowsError<LispVal> =
                        if divergence.IsSome then Choice1Of2 (Default "replay diverged")
                        elif recorded.tools.Count = 0 then
                            diverge (sprintf "diverged from the recording: the replayed run performs more than the %d recorded tool calls" toolTotal)
                        else
                            let ordinal = toolTotal - recorded.tools.Count + 1
                            let recordedCall, result = recorded.tools.Dequeue()
                            let expected = recordedCall.ToJsonString()
                            let actual = canonical call
                            if expected <> actual then
                                diverge (diffReport ordinal "tool call" expected actual)
                            else
                                Choice2Of2 (Json.toLispVal result)
                    let config =
                        { Session.configIn scratch bridge with
                            agentSources = Session.agentPackageSources options.agentDir
                            agentConfig = options.agentConfig
                            mcpServers = options.mcpServers
                            policySources = options.policySources
                            toolDispatch = Some toolDispatch
                            // The recording already answers everything, so
                            // approval questions (from a swapped-in stricter
                            // policy, or budget checks) auto-approve: denials
                            // are exactly the divergences we want to observe.
                            approver = Some(fun _ -> true)
                            policyTrust = fun _ _ -> true }
                    match Session.createWith config with
                    | Choice1Of2 error -> Error(sprintf "cannot build the replay session: %s" (showError error))
                    | Choice2Of2 session ->
                        // The replayed agent prints its own progress ("→
                        // read_file") as it re-executes. That is the recorded
                        // run narrating itself, not progress of the replay,
                        // and it would contaminate machine-read output —
                        // `jern golden check --md` writes a PR comment to the
                        // same stdout. Silence it for the duration.
                        let outcome =
                            lock consoleLock (fun () ->
                                let realOut = Console.Out
                                try
                                    Console.SetOut TextWriter.Null
                                    Session.runAgent session recorded.task
                                finally
                                    Console.SetOut realOut)
                        match divergence with
                        | Some report -> Ok(Diverged report)
                        | None ->
                            match outcome with
                            | Choice1Of2 error -> Error(sprintf "replay failed: %s" (showError error))
                            | Choice2Of2 _ ->
                                if recorded.llm.Count > 0 || recorded.tools.Count > 0 then
                                    Ok(Diverged(sprintf
                                                    "diverged from the recording: the replayed run ended early, leaving %d model call(s) and %d tool call(s) unused"
                                                    recorded.llm.Count recorded.tools.Count))
                                else
                                    Ok(Completed(llmTotal, toolTotal)))
            finally
                try Directory.Delete(scratch, true) with _ -> ()
