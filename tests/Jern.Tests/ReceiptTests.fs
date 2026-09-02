module Jern.Tests.ReceiptTests

open System
open System.IO
open Xunit
open IronKernel.Ast
open IronKernel.Errors
open Jern.Host

let private write (lines: string list) =
    let path = Path.Combine(Path.GetTempPath(), "jern-receipt-" + Guid.NewGuid().ToString("N") + ".jsonl")
    File.WriteAllText(path, String.Join("\n", lines) + "\n")
    path

let private summarize lines =
    let path = write lines
    try
        match Receipt.ofTrace path with
        | Ok summary -> summary
        | Error message -> failwith message
    finally
        File.Delete path

let private ts (event: string) = sprintf """{"ts":"2026-08-24T10:15:%02dZ",%s""" 30 event

/// A small but complete run: envelope, a model call with usage, a policed
/// read, an approved edit, a denied write, and a commit.
let private canned =
    [ ts """"event":"run-started","schema_version":1,"run_id":"20260824-101530","jern_version":"0.12.0","command":"run","task":"fix the parser","model":"anthropic/claude-opus-5","agent":"default","budget":{"llm_calls":20,"tokens":null},"sandbox":"external","cloud":{"run_id":"20260824-101530","token_cap":50000},"policy":[{"source":"jern.json","digest":"abc123def4567890","protected":false}]}"""
      ts """"event":"policy-layer","source":"jern.json","digest":"abc123def4567890","grants":true,"protected":false}"""
      ts """"event":"llm-call","request":{"model":"anthropic/claude-opus-5"}}"""
      ts """"event":"llm-response","response":{"usage":{"input_tokens":18200,"output_tokens":2100}}}"""
      ts """"event":"policy-decision","call":"read_file: src/parser.py","decision":"allow","by":"tool-policy"}"""
      ts """"event":"tool-call","call":{"name":"read_file","input":{"path":"src/parser.py"}}}"""
      ts """"event":"tool-result","result":{"content":"…","is_error":false}}"""
      ts """"event":"policy-decision","call":"edit_file: src/parser.py","decision":"ask","by":"tool-policy"}"""
      ts """"event":"tool-call","call":{"name":"edit_file","input":{"path":"src/parser.py"}}}"""
      ts """"event":"tool-result","result":{"content":"edited","is_error":false}}"""
      ts """"event":"git-commit","hash":"abc1234","path":"src/parser.py"}"""
      ts """"event":"policy-decision","call":"write_file: /etc/passwd","decision":"policy: edits are limited to src/ (jern.json edits_within)","by":"jern.json edits_within"}"""
      ts """"event":"run-finished","status":"ok","duration_ms":134000}""" ]

[<Fact>]
let ``a receipt reports what the run actually did`` () =
    let s = summarize canned
    Assert.True s.hasEnvelope
    Assert.True s.finished
    Assert.Equal(Some "20260824-101530", s.runId)
    Assert.Equal(Some "run", s.command)
    Assert.Equal(Some "fix the parser", s.task)
    Assert.Equal(Some "ok", s.status)
    Assert.Equal(1, s.llmCalls)
    Assert.Equal(18200L, s.inputTokens)
    Assert.Equal(2100L, s.outputTokens)
    Assert.Equal(Some 20, s.budgetLlmCalls)
    Assert.Equal(Some 50000L, s.cloudTokenCap)
    Assert.Equal(Some "external", s.sandbox)
    Assert.Equal<(string * int) list>([ "edit_file", 1; "read_file", 1 ], s.tools)
    Assert.Equal<string list>([ "src/parser.py" ], s.filesTouched)
    Assert.Equal(1, s.commits)
    // One allowed, one asked (and approved, since no approval-denied), one
    // denied by a rule.
    Assert.Equal(1, s.policyAllowed)
    Assert.Equal(1, s.policyAsked)
    Assert.Equal(1, s.policyDeniedByRule)
    Assert.Equal(0, s.approvalsDenied)
    Assert.Equal<string list>([ "policy: edits are limited to src/ (jern.json edits_within)" ], s.denialReasons)
    Assert.Equal(0, s.unreadableLines)

[<Fact>]
let ``every rendering carries the same facts`` () =
    let s = summarize canned
    let text = Receipt.render Receipt.plain s
    Assert.Contains("receipt", text)
    Assert.Contains("external (the host confines the whole process)", text)
    Assert.Contains("run 20260824-101530", text)
    Assert.Contains("2m 14s", text)
    Assert.Contains("1 (anthropic/claude-opus-5)", text)
    Assert.Contains("18.2k in / 2.1k out", text)
    Assert.Contains("budget 1/20", text)
    Assert.Contains("cloud cap 20.3k/50.0k", text)
    Assert.Contains("read_file ×1", text)
    Assert.Contains("src/parser.py", text)
    Assert.Contains("1 allowed · 1 approved by you · 1 denied", text)

    let md = Receipt.renderMarkdown s
    Assert.Contains("**jern receipt**", md)
    Assert.Contains("| model calls |", md)
    Assert.Contains("✅ ok", md)
    Assert.Contains("> fix the parser", md)
    // Pipes in a value would break the table.
    Assert.DoesNotContain("edits are limited to src/ (jern.json edits_within) |", md.Replace("\\|", ""))

    let json = Receipt.renderJson s
    let doc = Text.Json.Nodes.JsonNode.Parse(json).AsObject()
    Assert.Equal(1, doc.["llm_calls"].GetValue<int>())
    Assert.Equal(18200L, doc.["input_tokens"].GetValue<int64>())
    Assert.Equal(50000L, doc.["cloud_token_cap"].GetValue<int64>())
    Assert.False(doc.["hard_token_budget_denied"].GetValue<bool>())
    Assert.Equal("ok", doc.["status"].GetValue<string>())
    Assert.Equal("src/parser.py", doc.["files_touched"].AsArray().[0].GetValue<string>())
    Assert.Equal(1, doc.["policy"].AsObject().["denied_by_rule"].GetValue<int>())

/// Styling must not move the columns: a palette that wraps labels in ANSI
/// escapes has to produce the same visible layout as the plain one.
[<Fact>]
let ``colored rendering keeps the same column layout`` () =
    let s = summarize canned
    let styled: Receipt.Palette =
        { title = (fun t -> "\u001b[1m" + t + "\u001b[0m")
          label = (fun t -> "\u001b[36m" + t + "\u001b[0m")
          dim = id; good = id; bad = id }
    let strip (text: string) =
        Text.RegularExpressions.Regex.Replace(text, "\u001b\[[0-9;]*m", "")
    Assert.Equal(Receipt.render Receipt.plain s, strip (Receipt.render styled s))

[<Fact>]
let ``a denied approval is not counted as an approval`` () =
    let s =
        summarize
            [ ts """"event":"run-started","schema_version":1,"run_id":"r","command":"run","model":"m","agent":"a","budget":{},"policy":[]}"""
              ts """"event":"policy-decision","call":"shell: rm -rf /","decision":"ask","by":"tool-policy"}"""
              ts """"event":"approval-denied","call":"shell: rm -rf /"}"""
              ts """"event":"run-finished","status":"ok","duration_ms":10}""" ]
    Assert.Equal(1, s.policyAsked)
    Assert.Equal(1, s.approvalsDenied)
    Assert.Contains("0 approved by you · 1 denied", Receipt.render Receipt.plain s)

[<Fact>]
let ``spawns and programs are reported, and nested calls still pair`` () =
    let s =
        summarize
            [ ts """"event":"run-started","schema_version":1,"run_id":"r","command":"run","model":"m","agent":"a","budget":{},"policy":[]}"""
              ts """"event":"tool-call","call":{"name":"kernel_eval","input":{"code":"…"}}}"""
              // The program's inner calls are traced *inside* the outer pair.
              ts """"event":"tool-call","call":{"name":"write_file","input":{"path":"a.txt"}}}"""
              ts """"event":"tool-result","result":{"content":"wrote","is_error":false}}"""
              ts """"event":"tool-call","call":{"name":"write_file","input":{"path":"b.txt"}}}"""
              ts """"event":"tool-result","result":{"content":"denied","is_error":true}}"""
              ts """"event":"tool-result","result":{"content":"done","is_error":false}}"""
              ts """"event":"spawn","spec":{"task":"docs"}}"""
              ts """"event":"spawn-result","result":{"content":"ok","is_error":false}}"""
              ts """"event":"run-finished","status":"ok","duration_ms":10}""" ]
    Assert.Equal(1, s.programs)
    Assert.Equal(1, s.spawns)
    // b.txt failed, so only a.txt was actually touched.
    Assert.Equal<string list>([ "a.txt" ], s.filesTouched)
    Assert.Contains("1 subagent · 1 kernel_eval program", Receipt.render Receipt.plain s)

[<Fact>]
let ``an interrupted or failed run says so`` () =
    let interrupted =
        summarize
            [ ts """"event":"run-started","schema_version":1,"run_id":"r","command":"chat","model":"m","agent":"a","budget":{},"policy":[]}"""
              ts """"event":"run-finished","status":"interrupted","duration_ms":900}""" ]
    Assert.Equal(Some "interrupted", interrupted.status)
    Assert.Contains("interrupted", Receipt.render Receipt.plain interrupted)

    let failed =
        summarize
            [ ts """"event":"run-started","schema_version":1,"run_id":"r","command":"run","model":"m","agent":"a","budget":{},"policy":[]}"""
              ts """"event":"run-finished","status":"error","reason":"budget exhausted","duration_ms":900}""" ]
    Assert.Equal(Some "error", failed.status)
    Assert.Contains("budget exhausted", Receipt.render Receipt.plain failed)

[<Fact>]
let ``a truncated run is marked incomplete rather than guessed`` () =
    let path = write [ ts """"event":"run-started","schema_version":1,"run_id":"r","command":"run","model":"m","agent":"a","budget":{},"policy":[]}"""
                       ts """"event":"llm-call","request":{}}""" ]
    try
        // A process killed mid-write leaves a partial line.
        File.AppendAllText(path, """{"ts":"2026-08-24T10:16:00Z","event":"llm-resp""")
        match Receipt.ofTrace path with
        | Error message -> failwith message
        | Ok s ->
            Assert.True s.hasEnvelope
            Assert.False s.finished
            Assert.Equal(1, s.unreadableLines)
            let text = Receipt.render Receipt.plain s
            Assert.Contains("unfinished", text)
            Assert.Contains("truncated", text)
    finally
        File.Delete path

[<Fact>]
let ``an older pre-envelope trace produces an explicitly partial receipt`` () =
    let s =
        summarize
            [ ts """"event":"log","data":{"event":"agent-started","task":"fix the typo"}}"""
              ts """"event":"llm-call","request":{}}"""
              ts """"event":"tool-call","call":{"name":"read_file","input":{"path":"a.txt"}}}"""
              ts """"event":"tool-result","result":{"content":"x","is_error":false}}""" ]
    Assert.False s.hasEnvelope
    Assert.False s.finished
    Assert.Equal(None, s.model)
    // The task is still recoverable from the agent's own opening log event.
    Assert.Equal(Some "fix the typo", s.task)
    Assert.Equal(1, s.llmCalls)
    let text = Receipt.render Receipt.plain s
    Assert.Contains("older trace: no run envelope", text)
    Assert.Contains("model unknown", text)

[<Fact>]
let ``unknown events are ignored but an unknown schema version is refused`` () =
    // Forward compatibility: a newer jern may write events we never heard of.
    let s =
        summarize
            [ ts """"event":"run-started","schema_version":1,"run_id":"r","command":"run","model":"m","agent":"a","budget":{},"policy":[]}"""
              ts """"event":"quantum-entanglement","spooky":true}"""
              ts """"event":"run-finished","status":"ok","duration_ms":5}""" ]
    Assert.True s.finished
    Assert.Equal(0, s.unreadableLines)

    let path =
        write [ ts """"event":"run-started","schema_version":99,"run_id":"r","command":"run","model":"m","agent":"a","budget":{},"policy":[]}""" ]
    try
        match Receipt.ofTrace path with
        | Ok _ -> failwith "expected a future schema version to be refused"
        | Error message ->
            Assert.Contains("schema version 99", message)
            Assert.Contains("upgrade jern", message)
    finally
        File.Delete path

[<Fact>]
let ``an empty trace and a missing trace both fail gracefully`` () =
    let path = write []
    try
        match Receipt.ofTrace path with
        | Error message -> failwith message
        | Ok s ->
            Assert.False s.hasEnvelope
            Assert.Equal(0, s.llmCalls)
            // Renders without throwing, and says nothing it does not know.
            Assert.Contains("unfinished", Receipt.render Receipt.plain s)
    finally
        File.Delete path
    match Receipt.ofTrace (Path.Combine(Path.GetTempPath(), "jern-nope.jsonl")) with
    | Ok _ -> failwith "expected a missing trace to be an error"
    | Error message -> Assert.Contains("no trace at", message)

/// The reader must not drift from what a session actually writes: drive the
/// real agent and summarize the trace it produced.
[<Fact>]
let ``a receipt of a real run matches the run`` () =
    let root = Path.Combine(Path.GetTempPath(), "jern-receipt-run-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory root |> ignore
    File.WriteAllText(Path.Combine(root, "greeting.txt"), "helo world\n")
    let tracePath = Path.Combine(root, "trace.jsonl")
    try
        let mutable turn = 0
        let scripted: AnthropicBridge.LlmBridge =
            fun _ ->
                turn <- turn + 1
                match turn with
                | 1 ->
                    Choice2Of2 (Json.deserialize """{"role":"assistant","stop_reason":"tool_use","content":[{"type":"tool_use","id":"t","name":"edit_file","input":{"path":"greeting.txt","old_string":"helo","new_string":"hello"}}],"usage":{"input_tokens":1200,"output_tokens":90}}""")
                | _ ->
                    Choice2Of2 (Json.deserialize """{"role":"assistant","stop_reason":"end_turn","content":[{"type":"text","text":"Fixed."}],"usage":{"input_tokens":1300,"output_tokens":20}}""")
        let repoAgentDir = Path.Combine(__SOURCE_DIRECTORY__, "..", "..", "agents", "default")
        (use writer = new StreamWriter(tracePath, append = false, AutoFlush = true)
         let finish =
             Trace.openRun writer.WriteLine
                 { runId = "test-run"
                   command = "run"
                   task = Some "fix the greeting"
                   model = "anthropic/claude-opus-5"
                   agent = "default"
                   budgetLlmCalls = Some 20
                   budgetTokens = None
                   cloudTokenCap = None
                   sandbox = "none"
                   policy = [] }
         let session =
             match Session.createWith
                       { Session.configIn root scripted with
                           traceSink = Some writer.WriteLine
                           agentSources = Session.agentPackageSources repoAgentDir } with
             | Choice1Of2 error -> failwith (showError error)
             | Choice2Of2 s -> s
         match Session.runAgent session "fix the greeting" with
         | Choice1Of2 error -> failwith (showError error)
         | Choice2Of2 _ -> ()
         finish Trace.Completed)

        match Receipt.ofTrace tracePath with
        | Error message -> failwith message
        | Ok s ->
            Assert.True s.hasEnvelope
            Assert.True s.finished
            Assert.Equal(Some "ok", s.status)
            Assert.Equal(2, s.llmCalls)
            Assert.Equal(2500L, s.inputTokens)
            Assert.Equal(110L, s.outputTokens)
            // The edit landed, so the file is reported as touched…
            Assert.Equal<string list>([ "greeting.txt" ], s.filesTouched)
            // …and the tools the loop really used are counted.
            let count name = s.tools |> List.tryFind (fun (n, _) -> n = name) |> Option.map snd
            Assert.Equal(Some 1, count "edit_file")
            Assert.True((count "read_file").IsSome)   // CONVENTIONS.md probe
            Assert.True(s.policyAllowed > 0)
            Assert.Equal(1, s.policyAsked)            // the edit
            Assert.Contains("budget 2/20", Receipt.render Receipt.plain s)
    finally
        Directory.Delete(root, true)
