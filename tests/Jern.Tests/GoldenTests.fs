module Jern.Tests.GoldenTests

open System
open System.IO
open Xunit
open IronKernel.Ast
open IronKernel.Errors
open Jern.Host

let private repoAgentDir () =
    Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", "..", "agents", "default"))

let private makeRoot () =
    let root = Path.Combine(Path.GetTempPath(), "jern-golden-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory root |> ignore
    root

/// A model that fixes the typo: read → edit → done.
let private fixTypo: AnthropicBridge.LlmBridge =
    fun request ->
        let json = Json.serialize request
        Choice2Of2 (Json.deserialize (
            if json.Contains "edited \\u0027greeting.txt" then
                """{"role":"assistant","stop_reason":"end_turn","content":[{"type":"text","text":"Fixed."}],"usage":{"input_tokens":900,"output_tokens":30}}"""
            elif json.Contains "helo world" then
                """{"role":"assistant","stop_reason":"tool_use","content":[{"type":"tool_use","id":"t2","name":"edit_file","input":{"path":"greeting.txt","old_string":"helo","new_string":"hello"}}],"usage":{"input_tokens":800,"output_tokens":40}}"""
            else
                """{"role":"assistant","stop_reason":"tool_use","content":[{"type":"tool_use","id":"t1","name":"read_file","input":{"path":"greeting.txt"}}],"usage":{"input_tokens":700,"output_tokens":20}}"""))

/// Record a golden session the way `jern golden record` does.
let private recordGolden root slug task (bridge: AnthropicBridge.LlmBridge) assertions =
    let dir = Golden.directory root
    Directory.CreateDirectory dir |> ignore
    let tracePath = Path.Combine(dir, slug + ".jsonl")
    (use writer = new StreamWriter(tracePath, append = false, AutoFlush = true)
     let finish =
         Trace.openRun writer.WriteLine
             { runId = slug; command = "golden"; task = Some task
               model = "test/model"; agent = "default"
               budgetLlmCalls = None; budgetTokens = None; policy = [] }
     let session =
         match Session.createWith
                   { Session.configIn root bridge with
                       traceSink = Some writer.WriteLine
                       agentSources = Session.agentPackageSources (repoAgentDir ()) } with
         | Choice1Of2 error -> failwith (showError error)
         | Choice2Of2 s -> s
     match Session.runAgent session task with
     | Choice1Of2 error -> failwith (showError error)
     | Choice2Of2 _ -> ()
     finish Trace.Completed)
    File.WriteAllText(
        Path.Combine(dir, slug + ".json"),
        Golden.metadataJson { task = task; recordedWith = "test"; assertions = assertions })
    tracePath

/// The check as the CLI runs it: replay against a given agent and policy.
let private checkAll root (agentDir: string) policySources =
    match Golden.list root with
    | Error message -> failwith message
    | Ok entries ->
        entries
        |> List.map (
            Golden.check (fun tracePath ->
                Replay.run
                    { tracePath = tracePath
                      agentDir = agentDir
                      policyFile = None
                      agentConfig = Nil
                      mcpServers = []
                      policySources = policySources }))

[<Fact>]
let ``a recorded session checks clean against the agent that made it`` () =
    let root = makeRoot ()
    try
        File.WriteAllText(Path.Combine(root, "greeting.txt"), "helo world\n")
        recordGolden root "fix-the-typo" "fix the typo in greeting.txt" fixTypo
            { Golden.noAssertions with
                editsWithin = [ "greeting" ]
                noTools = [ "shell" ]
                maxFilesEdited = Some 1
                maxLlmCalls = Some 3 }
        |> ignore
        let verdicts = checkAll root (repoAgentDir ()) []
        let verdict = Assert.Single verdicts
        Assert.True(verdict.Passed, sprintf "%A %A" verdict.divergence verdict.failures)
        Assert.Equal("fix-the-typo", verdict.entry.slug)
        Assert.Equal("fix the typo in greeting.txt", verdict.entry.metadata.task)
    finally
        Directory.Delete(root, true)

/// The headline: change how the agent behaves and the committed snapshot
/// says so, offline, with the exact difference.
[<Fact>]
let ``an edited agent fails the check with the divergence`` () =
    let root = makeRoot ()
    let tampered = Path.Combine(Path.GetTempPath(), "jern-golden-agent-" + Guid.NewGuid().ToString("N"))
    try
        File.WriteAllText(Path.Combine(root, "greeting.txt"), "helo world\n")
        recordGolden root "fix-the-typo" "fix the typo in greeting.txt" fixTypo Golden.noAssertions |> ignore
        // Someone tweaks the system prompt in a pull request.
        for file in Directory.EnumerateFiles(repoAgentDir (), "*", SearchOption.AllDirectories) do
            let target = Path.Combine(tampered, Path.GetRelativePath(repoAgentDir (), file))
            Directory.CreateDirectory(Path.GetDirectoryName target) |> ignore
            File.Copy(file, target)
        let main = Path.Combine(tampered, "src", "main.ikr")
        File.WriteAllText(main, (File.ReadAllText main).Replace("You are jern", "You are chatty"))

        let verdict = Assert.Single(checkAll root tampered [])
        Assert.False verdict.Passed
        Assert.True verdict.divergence.IsSome
        Assert.Contains("diverged", verdict.divergence.Value)
        Assert.Contains("recorded:", verdict.divergence.Value)
        // And it reads as a change, not a crash, in the PR comment.
        let md = Golden.renderMarkdown [ verdict ]
        Assert.Contains("⚠️ 1 changed", md)
        Assert.Contains("behavior diverged from the recording", md)
    finally
        Directory.Delete(root, true)
        if Directory.Exists tampered then Directory.Delete(tampered, true)

/// Assertions protect *meaning*, so they must keep their force when a
/// deliberate re-record blesses new bytes.
[<Fact>]
let ``assertions catch a regression that re-recording would otherwise bless`` () =
    let root = makeRoot ()
    try
        File.WriteAllText(Path.Combine(root, "greeting.txt"), "helo world\n")
        // The new recording is internally consistent — replay is clean —
        // but the agent now shells out and touches a second file.
        let shellsOut: AnthropicBridge.LlmBridge =
            fun request ->
                let json = Json.serialize request
                Choice2Of2 (Json.deserialize (
                    if json.Contains "(no output)" || json.Contains "wrote \\u0027notes.txt" then
                        """{"role":"assistant","stop_reason":"end_turn","content":[{"type":"text","text":"Done."}]}"""
                    elif json.Contains "edited \\u0027greeting.txt" then
                        """{"role":"assistant","stop_reason":"tool_use","content":[{"type":"tool_use","id":"s1","name":"write_file","input":{"path":"notes.txt","content":"note"}}]}"""
                    else
                        """{"role":"assistant","stop_reason":"tool_use","content":[{"type":"tool_use","id":"e1","name":"edit_file","input":{"path":"greeting.txt","old_string":"helo","new_string":"hello"}}]}"""))
        recordGolden root "fix-the-typo" "fix the typo in greeting.txt" shellsOut
            { Golden.noAssertions with
                noTools = [ "write_file" ]
                maxFilesEdited = Some 1 }
        |> ignore
        let verdict = Assert.Single(checkAll root (repoAgentDir ()) [])
        // The bytes are self-consistent…
        Assert.True(verdict.divergence.IsNone, sprintf "%A" verdict.divergence)
        // …and the meaning is still caught.
        Assert.False verdict.Passed
        Assert.Contains(verdict.failures, fun f -> f.Contains "used write_file")
        Assert.Contains(verdict.failures, fun f -> f.Contains "changed 2 files (limit 1)")
    finally
        Directory.Delete(root, true)

/// The CI property: rules in force now judge a recording made before them.
[<Fact>]
let ``a policy that now forbids the edit fails the check`` () =
    let root = makeRoot ()
    try
        File.WriteAllText(Path.Combine(root, "greeting.txt"), "helo world\n")
        recordGolden root "fix-the-typo" "fix the typo in greeting.txt" fixTypo Golden.noAssertions |> ignore
        let policy =
            match PolicyConfig.parse (Text.Json.Nodes.JsonNode.Parse("""{"edits_within":["src/"]}""": string)) with
            | Ok p -> p
            | Error message -> failwith message
        let sources =
            [ { PolicyConfig.origin = PolicyConfig.Baseline "base branch"; PolicyConfig.policy = policy } ]
        let verdict = Assert.Single(checkAll root (repoAgentDir ()) sources)
        Assert.False verdict.Passed
        Assert.Contains("diverged", verdict.divergence.Value)
    finally
        Directory.Delete(root, true)

[<Fact>]
let ``slugs are stable and sidecars reject unknown assertions`` () =
    Assert.Equal("fix-the-failing-test", Golden.slugify "Fix the failing test!")
    Assert.Equal("fix-the-failing-test", Golden.slugify "  fix   the failing   test  ")
    Assert.Equal("golden", Golden.slugify "!!!")
    Assert.True((Golden.slugify (String.replicate 40 "long ")).Length <= 48)

    match Golden.parseMetadata """{"task":"t","assert":{"edits_withn":["src/"]}}""" with
    | Ok _ -> failwith "expected an unknown assert key to be rejected"
    | Error message -> Assert.Contains("unknown assert key", message)

    match Golden.parseMetadata """{"task":"t","assert":{"max_llm_calls":"lots"}}""" with
    | Ok _ -> failwith "expected a non-integer limit to be rejected"
    | Error message -> Assert.Contains("max_llm_calls", message)

    match Golden.parseMetadata """{"task":"t","assert":{"no_tools":["shell"],"max_tokens":50000}}""" with
    | Error message -> failwith message
    | Ok metadata ->
        Assert.Equal<string list>([ "shell" ], metadata.assertions.noTools)
        Assert.Equal(Some 50000, metadata.assertions.maxTokens)
        // Round-trips through the writer.
        match Golden.parseMetadata (Golden.metadataJson metadata) with
        | Error message -> failwith message
        | Ok again -> Assert.Equal(metadata.assertions, again.assertions)
