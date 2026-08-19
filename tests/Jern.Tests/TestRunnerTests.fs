module Jern.Tests.TestRunnerTests

open System.IO
open Xunit
open IronKernel.Ast
open Jern.Host

let private repoAgentDir () =
    Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", "..", "agents", "default"))

/// A model that fixes the typo: read → edit → done, keyed off request content
/// rather than call order so it can serve as a recording upstream.
let private scriptedUpstream: AnthropicBridge.LlmBridge =
    fun request ->
        let json = Json.serialize request
        let reply =
            if json.Contains "edited \\u0027greeting.txt" then
                """{"role":"assistant","stop_reason":"end_turn","content":[{"type":"text","text":"Fixed the typo."}]}"""
            elif json.Contains "helo world" then
                """{"role":"assistant","stop_reason":"tool_use","content":[{"type":"tool_use","id":"t2","name":"edit_file","input":{"path":"greeting.txt","old_string":"helo","new_string":"hello"}}]}"""
            else
                """{"role":"assistant","stop_reason":"tool_use","content":[{"type":"tool_use","id":"t1","name":"read_file","input":{"path":"greeting.txt"}}]}"""
        Choice2Of2 (Json.deserialize reply)

/// One-time generator for the committed fixture. Un-skip, run, re-skip after
/// a deliberate behavior change (or use `jern test --record` with a live key).
[<Fact(Skip = "fixture generator — run manually after deliberate agent changes")>]
let ``record default agent fixtures`` () =
    match TestRunner.run (repoAgentDir ()) (Fixtures.Record scriptedUpstream) with
    | Error message -> failwith message
    | Ok summary ->
        for o in summary.Failed do
            failwithf "%s: %s" o.name o.error.Value

[<Fact>]
let ``default agent suite passes on replay`` () =
    match TestRunner.run (repoAgentDir ()) Fixtures.Replay with
    | Error message -> failwith message
    | Ok summary ->
        for o in summary.Failed do
            failwithf "%s: %s" o.name o.error.Value
        Assert.Equal(3, summary.Passed.Length)

/// The flagship claim: a prompt regression is caught. Tamper with the default
/// agent's system prompt in a copy; replay must fail with a divergence error.
[<Fact>]
let ``a prompt regression is caught by replay`` () =
    let tampered = Path.Combine(Path.GetTempPath(), "jern-tampered-" + System.Guid.NewGuid().ToString("N"))
    try
        for file in Directory.EnumerateFiles(repoAgentDir (), "*", SearchOption.AllDirectories) do
            let target = Path.Combine(tampered, Path.GetRelativePath(repoAgentDir (), file))
            Directory.CreateDirectory(Path.GetDirectoryName target) |> ignore
            File.Copy(file, target)
        let main = Path.Combine(tampered, "src", "main.ikr")
        File.WriteAllText(main, (File.ReadAllText main).Replace("You are jern", "You are chatty"))
        match TestRunner.run tampered Fixtures.Replay with
        | Error message -> failwith message
        | Ok summary ->
            let failed = summary.Failed
            Assert.Single(failed) |> ignore
            Assert.Equal("fixes a typo end to end", failed.Head.name)
            Assert.Contains("diverged", failed.Head.error.Value)
    finally
        if Directory.Exists tampered then Directory.Delete(tampered, true)

[<Fact>]
let ``an agent that makes fewer llm calls than recorded fails`` () =
    // An exhausted-or-unused fixture is a behavior change too.
    let dir = Path.Combine(Path.GetTempPath(), "jern-short-" + System.Guid.NewGuid().ToString("N"))
    try
        Directory.CreateDirectory(Path.Combine(dir, "src")) |> ignore
        Directory.CreateDirectory(Path.Combine(dir, "test", "fixtures")) |> ignore
        File.WriteAllText(Path.Combine(dir, "src", "main.ikr"), "(define run-agent (lambda (task) \"did nothing\"))\n")
        File.Copy(Path.Combine(repoAgentDir (), "test", "fixtures", "fix-typo.json"),
                  Path.Combine(dir, "test", "fixtures", "fix-typo.json"))
        File.WriteAllText(Path.Combine(dir, "test", "t.ikr"),
            """(deftest "lazy agent" (with-fixtures "fixtures/fix-typo.json" (run-agent "x")))""" + "\n")
        match TestRunner.run dir Fixtures.Replay with
        | Error message -> failwith message
        | Ok summary ->
            Assert.Single(summary.Failed) |> ignore
            Assert.Contains("unused exchange", summary.Failed.Head.error.Value)
    finally
        if Directory.Exists dir then Directory.Delete(dir, true)

let private docsAgentDir () =
    Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", "..", "agents", "docs"))

/// A model that fixes the README heading: read → edit → done.
let private docsUpstream: AnthropicBridge.LlmBridge =
    fun request ->
        let json = Json.serialize request
        let reply =
            if json.Contains "edited \\u0027README.md" then
                """{"role":"assistant","stop_reason":"end_turn","content":[{"type":"text","text":"Fixed the heading typo."}]}"""
            elif json.Contains "Sampel Project" then
                """{"role":"assistant","stop_reason":"tool_use","content":[{"type":"tool_use","id":"d2","name":"edit_file","input":{"path":"README.md","old_string":"Sampel","new_string":"Sample"}}]}"""
            else
                """{"role":"assistant","stop_reason":"tool_use","content":[{"type":"tool_use","id":"d1","name":"read_file","input":{"path":"README.md"}}]}"""
        Choice2Of2 (Json.deserialize reply)

[<Fact(Skip = "fixture generator — run manually after deliberate agent changes")>]
let ``record docs agent fixtures`` () =
    match TestRunner.run (docsAgentDir ()) (Fixtures.Record docsUpstream) with
    | Error message -> failwith message
    | Ok summary ->
        for o in summary.Failed do
            failwithf "%s: %s" o.name o.error.Value

[<Fact>]
let ``docs agent suite passes on replay`` () =
    match TestRunner.run (docsAgentDir ()) Fixtures.Replay with
    | Error message -> failwith message
    | Ok summary ->
        for o in summary.Failed do
            failwithf "%s: %s" o.name o.error.Value
        Assert.Equal(3, summary.Passed.Length)

let private tddAgentDir () =
    Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", "..", "agents", "tdd"))

/// A model that misbehaves first: it tries to implement immediately, gets
/// the gate's refusal, then does it right — failing test (red), then the
/// implementation (green). Keyed off conversation content so it can serve
/// as a recording upstream; the recorded exchange therefore *contains* the
/// refusal, and replay proves the gate fired.
let private tddUpstream: AnthropicBridge.LlmBridge =
    fun request ->
        let json = Json.serialize request
        let reply =
            if json.Contains "edited \\u0027lib.sh" then
                // The implementation landed and the run after it was green.
                """{"role":"assistant","stop_reason":"end_turn","content":[{"type":"text","text":"Done: red first, then green."}]}"""
            elif json.Contains "FAIL subtract" then
                // Red observed — now the implementation edit is allowed.
                """{"role":"assistant","stop_reason":"tool_use","content":[{"type":"tool_use","id":"g3","name":"edit_file","input":{"path":"lib.sh","old_string":"add() { echo $(( $1 + $2 )); }","new_string":"add() { echo $(( $1 + $2 )); }\nsubtract() { echo $(( $1 - $2 )); }"}}]}"""
            elif json.Contains "TDD gate" then
                // Refused — write the failing test instead.
                """{"role":"assistant","stop_reason":"tool_use","content":[{"type":"tool_use","id":"g2","name":"edit_file","input":{"path":"test.sh","old_string":"echo OK","new_string":"test x$(subtract 5 3) = x2 || { echo FAIL subtract; exit 1; }\necho OK"}}]}"""
            else
                // First move: jump straight to the implementation (wrong).
                """{"role":"assistant","stop_reason":"tool_use","content":[{"type":"tool_use","id":"g1","name":"edit_file","input":{"path":"lib.sh","old_string":"add() { echo $(( $1 + $2 )); }","new_string":"add() { echo $(( $1 + $2 )); }\nsubtract() { echo $(( $1 - $2 )); }"}}]}"""
        Choice2Of2 (Json.deserialize reply)

[<Fact(Skip = "fixture generator — run manually after deliberate agent changes")>]
let ``record tdd agent fixtures`` () =
    match TestRunner.run (tddAgentDir ()) (Fixtures.Record tddUpstream) with
    | Error message -> failwith message
    | Ok summary ->
        for o in summary.Failed do
            failwithf "%s: %s" o.name o.error.Value

[<Fact>]
let ``tdd agent suite passes on replay`` () =
    match TestRunner.run (tddAgentDir ()) Fixtures.Replay with
    | Error message -> failwith message
    | Ok summary ->
        for o in summary.Failed do
            failwithf "%s: %s" o.name o.error.Value
        Assert.Equal(3, summary.Passed.Length)

/// The differentiating claim, tested: weaken the gate in a copy of the
/// agent (allow every edit) and the recorded conversation diverges — the
/// premature edit now succeeds, so the second request no longer carries
/// the refusal. A prompt regression suite built on vibes can't do this.
[<Fact>]
let ``a weakened tdd gate is caught by replay`` () =
    let weakened = Path.Combine(Path.GetTempPath(), "jern-nogate-" + System.Guid.NewGuid().ToString("N"))
    try
        for file in Directory.EnumerateFiles(tddAgentDir (), "*", SearchOption.AllDirectories) do
            let target = Path.Combine(weakened, Path.GetRelativePath(tddAgentDir (), file))
            Directory.CreateDirectory(Path.GetDirectoryName target) |> ignore
            File.Copy(file, target)
        let main = Path.Combine(weakened, "src", "main.ikr")
        File.WriteAllText(
            main,
            (File.ReadAllText main)
                .Replace("(if (test-file? path) #t (eqv? (tdd-phase) :implement))",
                         "#t"))
        match TestRunner.run weakened Fixtures.Replay with
        | Error message -> failwith message
        | Ok summary ->
            // Both the gate's unit test and the end-to-end replay fail: the
            // recorded conversation carries the refusal the gate no longer
            // produces, so the fixture diverges.
            let failedNames = summary.Failed |> List.map (fun o -> o.name)
            Assert.Contains("red before green, end to end", failedNames)
            Assert.Contains("the gate refuses implementation edits until red is observed", failedNames)
    finally
        if Directory.Exists weakened then Directory.Delete(weakened, true)
