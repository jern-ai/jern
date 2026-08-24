module Jern.Tests.EvalProgramTests

open System
open System.IO
open Xunit
open IronKernel.Ast
open IronKernel.Errors
open Jern.Host

let private noLlm: AnthropicBridge.LlmBridge =
    fun _ -> Choice1Of2 (Default "no llm expected in this test")

let private makeRoot () =
    let root = Path.Combine(Path.GetTempPath(), "jern-eval-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory root |> ignore
    root

let private newSession root bridge trace =
    let config =
        { Session.configIn root bridge with
            traceSink = trace }
    match Session.createWith config with
    | Choice1Of2 error -> failwith (showError error)
    | Choice2Of2 session -> session

let private run session source =
    match Session.runSource session "test" source with
    | Choice1Of2 error -> failwith (showError error)
    | Choice2Of2 value -> value

let private contentOf (result: LispVal) =
    match Tools.plistTryGet "content" result with
    | Some (Obj (:? string as s)) -> s
    | other -> failwithf "no :content in tool result: %A" other

let private isErrorOf (result: LispVal) =
    match Tools.plistTryGet "is_error" result with
    | Some (Bool b) -> b
    | other -> failwithf "no :is_error in tool result: %A" other

/// The lisptc idea, jern-style: the model submits one program composing
/// several tool calls; the program executes under a nested copy of the
/// handler stack, so its inner calls are individually policed and traced.
[<Fact>]
let ``a model program composes tool calls in one step`` () =
    let root = makeRoot ()
    try
        File.WriteAllText(Path.Combine(root, "greeting.txt"), "helo world\n")
        let trace = ResizeArray<string>()
        let mutable turn = 0
        let scripted: AnthropicBridge.LlmBridge =
            fun request ->
                turn <- turn + 1
                let json = Json.serialize request
                Assert.Contains("\"name\":\"kernel_eval\"", json)
                match turn with
                | 1 ->
                    Choice2Of2 (Json.deserialize """{"role":"assistant","stop_reason":"tool_use","content":[{"type":"tool_use","id":"p1","name":"kernel_eval","input":{"code":"(define content (plist-get (call-tool \"read_file\" (list :path \"greeting.txt\")) :content)) (if (string-contains? content \"helo\") (plist-get (call-tool \"edit_file\" (list :path \"greeting.txt\" :old_string \"helo\" :new_string \"hello\")) :content) \"nothing to fix\")"}}]}""")
                | _ ->
                    // The program's last value (the edit result) came back.
                    Assert.Contains("edited", json)
                    Choice2Of2 (Json.deserialize """{"role":"assistant","stop_reason":"end_turn","content":[{"type":"text","text":"Fixed in one program."}]}""")
        let repoAgentDir = Path.Combine(__SOURCE_DIRECTORY__, "..", "..", "agents", "default")
        let session =
            match Session.createWith
                      { Session.configIn root scripted with
                          traceSink = Some trace.Add
                          agentSources = Session.agentPackageSources repoAgentDir } with
            | Choice1Of2 error -> failwith (showError error)
            | Choice2Of2 s -> s
        match Session.runAgent session "Fix the typo in greeting.txt with one program" with
        | Choice1Of2 error -> failwith (showError error)
        | Choice2Of2 (Obj (:? string as text)) -> Assert.Equal("Fixed in one program.", text)
        | Choice2Of2 other -> failwith (showVal other)
        Assert.Equal(2, turn)
        Assert.Equal("hello world\n", File.ReadAllText(Path.Combine(root, "greeting.txt")))
        // The trace shows the program AND its inner effects, each policed.
        let count (marker: string) =
            trace |> Seq.filter (fun (l: string) -> l.Contains marker) |> Seq.length
        Assert.True(count "\"name\":\"kernel_eval\"" >= 1)
        Assert.True(count "\"name\":\"read_file\"" >= 1)
        Assert.True(count "\"name\":\"edit_file\"" >= 1)
        Assert.Contains(trace, fun (l: string) ->
            l.Contains "\"event\":\"policy-decision\"" && l.Contains "edit_file")
    finally
        Directory.Delete(root, true)

/// Definitions persist across programs: the model can build up a toolkit
/// over the session, lisptc's REPL-with-state.
[<Fact>]
let ``program definitions persist across kernel_eval calls`` () =
    let root = makeRoot ()
    try
        let session = newSession root noLlm None
        let first = run session """(call-tool "kernel_eval" (list :code "(define twice (lambda (n) (+ n n)))"))"""
        Assert.False(isErrorOf first)
        let second = run session """(call-tool "kernel_eval" (list :code "(twice 21)"))"""
        Assert.False(isErrorOf second)
        Assert.Contains("42", contentOf second)
    finally
        Directory.Delete(root, true)

/// A broken program is a tool error the model can read — the session (and
/// the program environment) survive it.
[<Fact>]
let ``a program error comes back as a tool error and the session continues`` () =
    let root = makeRoot ()
    try
        let session = newSession root noLlm None
        let broken = run session """(call-tool "kernel_eval" (list :code "(no-such-function 1)"))"""
        Assert.True(isErrorOf broken)
        Assert.Contains("no-such-function", contentOf broken)
        let after = run session """(call-tool "kernel_eval" (list :code "(+ 40 2)"))"""
        Assert.False(isErrorOf after)
        Assert.Contains("42", contentOf after)
    finally
        Directory.Delete(root, true)

/// Authority lives at the effects: a workspace policy that denies edits
/// denies them *inside* model programs too.
[<Fact>]
let ``inner tool calls are still policed inside a program`` () =
    let root = makeRoot ()
    try
        File.WriteAllText(Path.Combine(root, "a.txt"), "x\n")
        Directory.CreateDirectory(Path.Combine(root, ".jern")) |> ignore
        File.WriteAllText(
            Path.Combine(root, ".jern", "policy.ikr"),
            "(define tool-policy\n  (lambda (call)\n    (if (equal? (plist-get call :name) \"edit_file\")\n        \"edits are forbidden here\"\n        :allow)))\n")
        let session = newSession root noLlm None
        let result =
            run session
                """(call-tool "kernel_eval" (list :code "(plist-get (call-tool \"edit_file\" (list :path \"a.txt\" :old_string \"x\" :new_string \"y\")) :content)"))"""
        // The program ran fine; what it got back from the edit is the denial.
        Assert.False(isErrorOf result)
        Assert.Contains("edits are forbidden here", contentOf result)
        Assert.Equal("x\n", File.ReadAllText(Path.Combine(root, "a.txt")))
    finally
        Directory.Delete(root, true)

/// A pure loop has no effect for the interrupt check to catch, so the
/// wall-clock cap is the stop: the program is abandoned, the session moves on.
[<Fact>]
let ``a runaway program times out and the session stays usable`` () =
    let root = makeRoot ()
    let saved = Tools.currentLimits ()
    try
        Tools.configureLimits { saved with evalTimeoutSeconds = 1.0 }
        let session = newSession root noLlm None
        let runaway = run session """(call-tool "kernel_eval" (list :code "(define spin (lambda () (spin))) (spin)"))"""
        Assert.True(isErrorOf runaway)
        Assert.Contains("timed out", contentOf runaway)
        // The abandoned program is still spinning — that is the documented
        // trade (it can burn a core, it just cannot act) — so restore the
        // normal cap before asking whether the session still works. Leaving
        // the one-second cap in place would race the runaway thread for CPU
        // on a small CI machine and time out an addition.
        Tools.configureLimits saved
        let after = run session """(call-tool "kernel_eval" (list :code "(+ 40 2)"))"""
        Assert.False(isErrorOf after)
        Assert.Contains("42", contentOf after)
    finally
        Tools.configureLimits saved
        Directory.Delete(root, true)

// ---------------------------------------------------------------------------
// Workspace skills: .jern/skills.ikr loads into the agent environment.

[<Fact>]
let ``workspace skills serve agent code and model programs alike`` () =
    let root = makeRoot ()
    try
        Directory.CreateDirectory(Path.Combine(root, ".jern")) |> ignore
        File.WriteAllText(
            Path.Combine(root, ".jern", "skills.ikr"),
            "(define greet (lambda (name) (String.concat \"hei \" name)))\n")
        let session = newSession root noLlm None
        // Agent source sees the skill…
        match run session """(greet "jern")""" with
        | Obj (:? string as s) -> Assert.Equal("hei jern", s)
        | other -> failwith (showVal other)
        // …and so does a model-authored program.
        let program = run session """(call-tool "kernel_eval" (list :code "(greet \"program\")"))"""
        Assert.False(isErrorOf program)
        Assert.Contains("hei program", contentOf program)
    finally
        Directory.Delete(root, true)

[<Fact>]
let ``skills are unprivileged: host primitives are out of reach`` () =
    let root = makeRoot ()
    try
        Directory.CreateDirectory(Path.Combine(root, ".jern")) |> ignore
        // The handler environment's primitives do not exist where skills
        // load; referencing one fails the session loudly at startup.
        File.WriteAllText(
            Path.Combine(root, ".jern", "skills.ikr"),
            "(define leak jern/host-llm-call)\n")
        match Session.createWith (Session.configIn root noLlm) with
        | Choice1Of2 _ -> ()
        | Choice2Of2 _ -> failwith "expected the privileged reference to fail the load"
    finally
        Directory.Delete(root, true)

/// Replay handles programs: the outer kernel_eval pair is re-executed, its
/// inner calls answered from the recording.
[<Fact>]
let ``a run that used kernel_eval replays faithfully`` () =
    let root = makeRoot ()
    let tracePath = Path.Combine(Path.GetTempPath(), "jern-eval-trace-" + Guid.NewGuid().ToString("N") + ".jsonl")
    try
        File.WriteAllText(Path.Combine(root, "greeting.txt"), "helo world\n")
        let mutable turn = 0
        let scripted: AnthropicBridge.LlmBridge =
            fun _ ->
                turn <- turn + 1
                match turn with
                | 1 ->
                    Choice2Of2 (Json.deserialize """{"role":"assistant","stop_reason":"tool_use","content":[{"type":"tool_use","id":"p1","name":"kernel_eval","input":{"code":"(plist-get (call-tool \"read_file\" (list :path \"greeting.txt\")) :content)"}}]}""")
                | _ ->
                    Choice2Of2 (Json.deserialize """{"role":"assistant","stop_reason":"end_turn","content":[{"type":"text","text":"Read it."}]}""")
        let repoAgentDir = Path.Combine(__SOURCE_DIRECTORY__, "..", "..", "agents", "default")
        (use writer = new StreamWriter(tracePath, append = false, AutoFlush = true)
         let session =
             match Session.createWith
                       { Session.configIn root scripted with
                           traceSink = Some writer.WriteLine
                           agentSources = Session.agentPackageSources repoAgentDir } with
             | Choice1Of2 error -> failwith (showError error)
             | Choice2Of2 s -> s
         match Session.runAgent session "read the greeting" with
         | Choice1Of2 error -> failwith (showError error)
         | Choice2Of2 _ -> ())
        match Replay.run
                  { tracePath = tracePath
                    agentDir = repoAgentDir
                    policyFile = None
                    agentConfig = Nil
                    mcpServers = []
                    policySources = [] } with
        | Error message -> failwith message
        | Ok (Replay.Diverged report) -> failwith ("unexpected divergence: " + report)
        | Ok (Replay.Completed _) -> ()
    finally
        File.Delete tracePath
        Directory.Delete(root, true)
