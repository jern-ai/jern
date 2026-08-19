module Iron.Tests.TestRunnerTests

open System.IO
open Xunit
open IronKernel.Ast
open Iron.Host

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
/// a deliberate behavior change (or use `iron test --record` with a live key).
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
    let tampered = Path.Combine(Path.GetTempPath(), "iron-tampered-" + System.Guid.NewGuid().ToString("N"))
    try
        for file in Directory.EnumerateFiles(repoAgentDir (), "*", SearchOption.AllDirectories) do
            let target = Path.Combine(tampered, Path.GetRelativePath(repoAgentDir (), file))
            Directory.CreateDirectory(Path.GetDirectoryName target) |> ignore
            File.Copy(file, target)
        let main = Path.Combine(tampered, "src", "main.ikr")
        File.WriteAllText(main, (File.ReadAllText main).Replace("You are iron", "You are chatty"))
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
    let dir = Path.Combine(Path.GetTempPath(), "iron-short-" + System.Guid.NewGuid().ToString("N"))
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
