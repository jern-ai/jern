module Jern.Tests.SpawnTests

open System
open System.IO
open Xunit
open IronKernel.Ast
open IronKernel.Errors
open Jern.Host

let private noLlm: AnthropicBridge.LlmBridge =
    fun _ -> Choice1Of2 (Default "no llm expected in this test")

let private makeRoot () =
    let root = Path.Combine(Path.GetTempPath(), "jern-spawn-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory root |> ignore
    root

let private writeAgent (root: string) (dir: string) (source: string) =
    let src = Path.Combine(root, dir, "src")
    Directory.CreateDirectory src |> ignore
    File.WriteAllText(Path.Combine(src, "main.ikr"), source)
    Path.Combine(root, dir)

let private newSession root agentDir trace =
    let config =
        { Session.configIn root noLlm with
            agentSources = Session.agentPackageSources agentDir
            traceSink = trace }
    match Session.createWith config with
    | Choice1Of2 error -> failwith (showError error)
    | Choice2Of2 session -> session

let private finalText result =
    match result with
    | Choice1Of2 error -> failwith (showError error)
    | Choice2Of2 (Obj (:? string as text)) -> text
    | Choice2Of2 other -> failwith ("expected a string, got " + showVal other)

/// A parent that forks its own brain for a subtask: the child runs the same
/// agent source in a fresh session, under its own copy of the handler stack.
[<Fact>]
let ``an agent can spawn its own brain on a subtask`` () =
    let root = makeRoot ()
    try
        let agentDir =
            writeAgent root "parent"
                (String.concat "\n"
                    [ "(define run-agent"
                      "  (lambda (task)"
                      "    (if (equal? task \"child-task\")"
                      "        (sequence"
                      "          (perform jern/log (list :event \"child-working\"))"
                      "          \"child-done\")"
                      "        (plist-get (spawn-agent \"child-task\") :content))))"
                      "" ])
        let trace = ResizeArray<string>()
        let session = newSession root agentDir (Some trace.Add)
        Assert.Equal("child-done", finalText (Session.runAgent session "go"))
        // The spawn crossed the trace choke point…
        Assert.Contains(trace, fun (l: string) -> l.Contains "\"event\":\"spawn\"")
        Assert.Contains(trace, fun (l: string) -> l.Contains "\"event\":\"spawn-result\"")
        // …and the child's own effects are tagged with the spawn id.
        Assert.Contains(trace, fun (l: string) -> l.Contains "\"spawn\":1")
    finally
        Directory.Delete(root, true)

/// :agent selects a different brain — here a workspace-relative package.
[<Fact>]
let ``an agent can spawn a different agent by name`` () =
    let root = makeRoot ()
    try
        writeAgent root "sub" "(define run-agent (lambda (task) (String.concat \"sub saw: \" task)))\n" |> ignore
        let parentDir =
            writeAgent root "parent"
                "(define run-agent (lambda (task) (plist-get (spawn-agent-named \"sub\" task) :content)))\n"
        let session = newSession root parentDir None
        Assert.Equal("sub saw: delegated", finalText (Session.runAgent session "delegated"))
    finally
        Directory.Delete(root, true)

/// A brain that forks unconditionally hits the host's depth cap instead of
/// forking forever; the refusal comes back as an ordinary error result.
[<Fact>]
let ``spawn depth is capped`` () =
    let root = makeRoot ()
    try
        let agentDir =
            writeAgent root "forker"
                "(define run-agent (lambda (task) (plist-get (spawn-agent task) :content)))\n"
        let session = newSession root agentDir None
        Assert.Contains("spawn depth limit", finalText (Session.runAgent session "go"))
    finally
        Directory.Delete(root, true)

/// A child that cannot be built (unknown agent) is an error result the
/// parent can react to, not a crashed session.
[<Fact>]
let ``an unknown agent name is an error result`` () =
    let root = makeRoot ()
    try
        let agentDir =
            writeAgent root "parent"
                (String.concat "\n"
                    [ "(define run-agent"
                      "  (lambda (task)"
                      "    (sequence"
                      "      (define result (spawn-agent-named \"no-such-agent\" task))"
                      "      (if (eqv? (plist-get result :is_error) #t)"
                      "          (String.concat \"refused: \" (plist-get result :content))"
                      "          \"unexpectedly succeeded\"))))"
                      "" ])
        let session = newSession root agentDir None
        Assert.StartsWith("refused: agent 'no-such-agent'", finalText (Session.runAgent session "go"))
    finally
        Directory.Delete(root, true)

[<Fact>]
let ``spawned agents share the hard token budget`` () =
    let root = makeRoot ()
    try
        let calls = ref 0
        let bridge: AnthropicBridge.LlmBridge =
            fun _ ->
                calls.Value <- calls.Value + 1
                Choice2Of2(
                    Jern.Host.Json.deserialize
                        """{"role":"assistant","stop_reason":"end_turn","content":[],"usage":{"input_tokens":20,"output_tokens":10}}""")
        let agentDir =
            writeAgent root "parent"
                (String.concat "\n"
                    [ "(define run-agent"
                      "  (lambda (task)"
                      "    (if (equal? task \"child\")"
                      "        (sequence (perform jern/llm-call (list :messages (vector))) \"child-done\")"
                      "        (sequence"
                      "          (perform jern/llm-call (list :messages (vector)))"
                      "          (plist-get (spawn-agent \"child\") :content)))))"
                      "" ])
        let hardBudget = Session.HardTokenBudget 30L
        let config =
            { Session.configIn root bridge with
                agentSources = Session.agentPackageSources agentDir
                hardTokenBudget = Some hardBudget }
        let session =
            match Session.createWith config with
            | Choice1Of2 error -> failwith (showError error)
            | Choice2Of2 created -> created
        let result = finalText (Session.runAgent session "parent")
        Assert.Contains("hard token budget of 30 exhausted", result)
        Assert.Equal(1, calls.Value)
        Assert.Equal(30L, hardBudget.Spent)
    finally
        Directory.Delete(root, true)
