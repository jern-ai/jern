module Jern.Tests.PolicyConfigTests

open System
open System.IO
open System.Text.Json.Nodes
open Xunit
open IronKernel.Ast
open IronKernel.Errors
open Jern.Host

let private noLlm: AnthropicBridge.LlmBridge =
    fun _ -> Choice1Of2 (Default "no llm expected in this test")

let private makeRoot () =
    let root = Path.Combine(Path.GetTempPath(), "jern-polcfg-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory root |> ignore
    root

let private parsePolicy (json: string) =
    match PolicyConfig.parse (JsonNode.Parse(json: string)) with
    | Ok policy -> policy
    | Error message -> failwith message

/// A session with the given policy sources; `asked` records every approval
/// question, which is how a test tells :allow from :ask.
let private sessionWith root sources grantTrust (asked: ResizeArray<string>) =
    let config =
        { Session.configIn root noLlm with
            policySources = sources
            policyGrantTrust = grantTrust
            approver = Some(fun description -> asked.Add description; true) }
    match Session.createWith config with
    | Choice1Of2 error -> failwith (showError error)
    | Choice2Of2 session -> session

let private call session source =
    match Session.runSource session "test" source with
    | Choice1Of2 error -> failwith (showError error)
    | Choice2Of2 value -> value

let private contentOf result =
    match Tools.plistTryGet "content" result with
    | Some (Obj (:? string as s)) -> s
    | other -> failwithf "no :content in tool result: %A" other

let private isErrorOf result =
    match Tools.plistTryGet "is_error" result with
    | Some (Bool b) -> b
    | other -> failwithf "no :is_error in tool result: %A" other

let private workspaceSource path policy =
    { PolicyConfig.origin = PolicyConfig.Workspace path; PolicyConfig.policy = policy }

let private baselineSource label policy =
    { PolicyConfig.origin = PolicyConfig.Baseline label; PolicyConfig.policy = policy }

// ---------------------------------------------------------------------------
// Parsing, canonical form, and compilation

[<Fact>]
let ``a policy object parses into restrictions and grants`` () =
    let policy =
        parsePolicy """{"edits_within":["src/"],"shell_allow":["pytest"],"deny":["mcp__*"],"memory":"ask"}"""
    Assert.Equal<string list>([ "src/" ], policy.editsWithin)
    Assert.Equal<string list>([ "pytest" ], policy.shellAllow)
    Assert.Equal<string list>([ "mcp__*" ], policy.deny)
    Assert.Equal(Some "ask", policy.memory)
    Assert.True(PolicyConfig.hasGrants policy)
    Assert.True(PolicyConfig.hasRestrictions policy)
    // The tightening half is what survives a declined trust prompt.
    let restrictions = PolicyConfig.restrictionsOnly policy
    Assert.False(PolicyConfig.hasGrants restrictions)
    Assert.Equal<string list>([ "src/" ], restrictions.editsWithin)

[<Fact>]
let ``a malformed policy is a startup error, never a silent no-op`` () =
    let fails (json: string) =
        match PolicyConfig.parse (JsonNode.Parse json) with
        | Ok _ -> failwithf "expected '%s' to be rejected" json
        | Error message -> message
    Assert.Contains("unknown policy key", fails """{"edits_withn":["src/"]}""")
    Assert.Contains("must be an array of strings", fails """{"deny":"shell"}""")
    Assert.Contains("must be an array of strings", fails """{"allow":[1,2]}""")
    Assert.Contains("policy.memory", fails """{"memory":"maybe"}""")
    Assert.Contains("must be a JSON object", fails """["src/"]""")

[<Fact>]
let ``canonical JSON and the compiled source are byte-stable`` () =
    // Same policy, different key order and whitespace.
    let a = parsePolicy """{"deny":["mcp__*"],  "edits_within":["src/"]}"""
    let b = parsePolicy """{"edits_within" : ["src/"], "deny" : ["mcp__*"]}"""
    Assert.Equal(PolicyConfig.canonicalJson a, PolicyConfig.canonicalJson b)
    Assert.Equal("""{"deny":["mcp__*"],"edits_within":["src/"]}""", PolicyConfig.canonicalJson a)
    Assert.Equal(PolicyConfig.digest a, PolicyConfig.digest b)
    Assert.Equal(PolicyConfig.compile "jern.json" true a, PolicyConfig.compile "jern.json" true b)
    // Array order is meaningful and preserved; the digest follows it.
    let ordered = parsePolicy """{"edits_within":["a/","b/"]}"""
    let swapped = parsePolicy """{"edits_within":["b/","a/"]}"""
    Assert.NotEqual<string>(PolicyConfig.digest ordered, PolicyConfig.digest swapped)
    // Dropping untrusted grants changes the compiled source, not the digest:
    // the digest identifies what the file asked for.
    let mixed = parsePolicy """{"deny":["shell"],"allow":["grep"]}"""
    Assert.Contains("add-policy-grant!", PolicyConfig.compile "jern.json" true mixed)
    Assert.DoesNotContain("add-policy-grant!", PolicyConfig.compile "jern.json" false mixed)
    Assert.Contains("add-policy-restriction!", PolicyConfig.compile "jern.json" false mixed)

// ---------------------------------------------------------------------------
// Enforcement

[<Fact>]
let ``edits_within denies writes outside the prefixes with a reason the model sees`` () =
    let root = makeRoot ()
    try
        Directory.CreateDirectory(Path.Combine(root, "src")) |> ignore
        File.WriteAllText(Path.Combine(root, "outside.txt"), "x\n")
        File.WriteAllText(Path.Combine(root, "src", "inside.txt"), "x\n")
        let asked = ResizeArray<string>()
        let session =
            sessionWith root [ workspaceSource "/w/jern.json" (parsePolicy """{"edits_within":["src/"]}""") ]
                        (fun _ _ -> true) asked
        let denied =
            call session """(call-tool "edit_file" (list :path "outside.txt" :old_string "x" :new_string "y"))"""
        Assert.True(isErrorOf denied)
        Assert.Contains("policy: edits are limited to src/", contentOf denied)
        Assert.Empty asked                                  // a denial never reaches the user
        Assert.Equal("x\n", File.ReadAllText(Path.Combine(root, "outside.txt")))
        // Inside the prefix the ordinary rules still apply: edit_file asks.
        let allowed =
            call session """(call-tool "edit_file" (list :path "src/inside.txt" :old_string "x" :new_string "y"))"""
        Assert.False(isErrorOf allowed)
        Assert.Single asked |> ignore
        // Reads are untouched by a write restriction.
        Assert.False(isErrorOf (call session """(call-tool "read_file" (list :path "outside.txt"))"""))
    finally
        Directory.Delete(root, true)

[<Fact>]
let ``blast radius keys parse, canonicalise, and describe as restrictions`` () =
    let policy = parsePolicy """{"max_files_edited": 2, "protected_paths": ["migrations/"], "max_lines_changed": 40, "edits_within": ["src/"]}"""
    Assert.Equal(Some 2, policy.maxFilesEdited)
    Assert.Equal(Some 40, policy.maxLinesChanged)
    Assert.Equal<string list>([ "migrations/" ], policy.protectedPaths)
    Assert.True(PolicyConfig.hasRestrictions policy)
    Assert.False(PolicyConfig.hasGrants policy)
    Assert.Equal("""{"edits_within":["src/"],"max_files_edited":2,"max_lines_changed":40,"protected_paths":["migrations/"]}""", PolicyConfig.canonicalJson policy)
    Assert.Equal(policy, PolicyConfig.restrictionsOnly policy)
    Assert.Contains("max_files_edited: 2", PolicyConfig.describeRestrictions policy)
    Assert.Contains("protected_paths: migrations/", PolicyConfig.describeRestrictions policy)
    Assert.Contains("(jern/host-blast-radius call 2 40 \"jern.json\")", PolicyConfig.compile "jern.json" true policy)
    for bad in [ """{"max_files_edited": 0}"""; """{"max_lines_changed": -1}"""; """{"max_files_edited": "two"}""" ] do
        match PolicyConfig.parse (JsonNode.Parse(bad: string)) with
        | Ok _ -> failwithf "accepted %s" bad
        | Error message -> Assert.Contains("must be a positive integer", message)
    let tight, loose = parsePolicy """{"max_files_edited": 1, "protected_paths": ["a/"]}""", parsePolicy """{"max_files_edited": 5, "max_lines_changed": 9, "protected_paths": ["b/"]}"""
    Assert.Equal((Some 1, Some 9, [ "a/"; "b/" ]), PolicyConfig.effectiveLimits [ loose; tight ])

[<Fact>]
let ``a blast radius denies the edit that would cross it and counts only edits that landed`` () =
    let root = makeRoot ()
    try
        Directory.CreateDirectory(Path.Combine(root, "src")) |> ignore
        Directory.CreateDirectory(Path.Combine(root, "migrations")) |> ignore
        File.WriteAllText(Path.Combine(root, "src", "a.txt"), "one\ntwo\n")
        File.WriteAllText(Path.Combine(root, "src", "b.txt"), "one\n")
        File.WriteAllText(Path.Combine(root, "migrations", "001.sql"), "select 1;\n")
        let asked = ResizeArray<string>()
        let session =
            sessionWith root
                [ baselineSource "base" (parsePolicy """{"max_files_edited": 2, "max_lines_changed": 6, "protected_paths": ["migrations/"]}""")
                  workspaceSource "/w/jern.json" (parsePolicy """{"max_files_edited": 9}""") ]
                (fun _ _ -> true) asked
        // A protected path is denied whatever else allows it.
        let protectedDenied =
            call session """(call-tool "edit_file" (list :path "migrations/001.sql" :old_string "1" :new_string "2"))"""
        Assert.True(isErrorOf protectedDenied)
        Assert.Contains("policy: edits under migrations/ are denied (protected baseline: base protected_paths)", contentOf protectedDenied)
        // Two lines out, two lines in: four of the six allowed.
        let first = call session """(call-tool "edit_file" (list :path "src/a.txt" :old_string "one\ntwo" :new_string "uno\ndos"))"""
        Assert.False(isErrorOf first)
        // A failed edit changes nothing and counts nothing.
        let missing = call session """(call-tool "edit_file" (list :path "src/b.txt" :old_string "absent" :new_string "x"))"""
        Assert.True(isErrorOf missing)
        // Three more lines would make seven: denied with the total named.
        let tooMany = call session """(call-tool "write_file" (list :path "src/b.txt" :content "a\nb\nc\nd\n"))"""
        Assert.True(isErrorOf tooMany)
        Assert.Contains("at most 6 lines may change in this run (protected baseline: base max_lines_changed); this edit brings the total to 9", contentOf tooMany)
        // A second file within both limits is fine; a third file is not, and
        // the tighter of the two sources is the one that speaks.
        let second = call session """(call-tool "edit_file" (list :path "src/b.txt" :old_string "one" :new_string "two"))"""
        Assert.False(isErrorOf second)
        let third = call session """(call-tool "write_file" (list :path "src/c.txt" :content "x"))"""
        Assert.True(isErrorOf third)
        Assert.Contains("at most 2 files may be edited in this run (protected baseline: base max_files_edited); src/c.txt would be file 3", contentOf third)
        Assert.Equal("uno\ndos\n", File.ReadAllText(Path.Combine(root, "src", "a.txt")))
        Assert.Equal("two\n", File.ReadAllText(Path.Combine(root, "src", "b.txt")))
        Assert.False(File.Exists(Path.Combine(root, "src", "c.txt")))
    finally
        Directory.Delete(root, true)

[<Fact>]
let ``the receipt reports the blast radius against its limits`` () =
    let root = makeRoot ()
    try
        Directory.CreateDirectory(Path.Combine(root, "src")) |> ignore
        File.WriteAllText(Path.Combine(root, "src", "a.txt"), "one\n")
        let trace = ResizeArray<string>()
        let config =
            { Session.configIn root noLlm with
                policySources = [ baselineSource "base" (parsePolicy """{"max_files_edited": 3, "max_lines_changed": 50, "protected_paths": ["migrations/"]}""") ]
                policyGrantTrust = (fun _ _ -> true)
                approver = Some(fun _ -> true)
                traceSink = Some trace.Add }
        let session =
            match Session.createWith config with
            | Choice1Of2 error -> failwith (showError error)
            | Choice2Of2 session -> session
        call session """(call-tool "edit_file" (list :path "src/a.txt" :old_string "one" :new_string "uno\ndos"))""" |> ignore
        let tracePath = Path.Combine(root, "trace.jsonl")
        File.WriteAllLines(tracePath, trace)
        match Receipt.ofTrace tracePath with
        | Error message -> failwith message
        | Ok receipt ->
            Assert.Equal<string list>([ "src/a.txt" ], receipt.filesTouched)
            Assert.Equal(3L, receipt.linesChanged)
            Assert.Equal(Some 3, receipt.maxFilesEdited)
            Assert.Equal(Some 50, receipt.maxLinesChanged)
            Assert.Equal<string list>([ "migrations/" ], receipt.protectedPaths)
            Assert.Contains("1 files of at most 3 · 3 lines of at most 50 · protected: migrations/", Receipt.renderMarkdown receipt)
            Assert.Contains("\"blast_radius\"", Receipt.renderJson receipt)
    finally
        Directory.Delete(root, true)

/// "." looks like "anywhere" and would otherwise match nothing, because no
/// workspace-relative path begins with a dot.
[<Fact>]
let ``edits_within "." means the whole workspace, not nothing`` () =
    let root = makeRoot ()
    try
        File.WriteAllText(Path.Combine(root, "anywhere.txt"), "x\n")
        let asked = ResizeArray<string>()
        let session =
            sessionWith root [ workspaceSource "/w/jern.json" (parsePolicy """{"edits_within":["."]}""") ]
                        (fun _ _ -> true) asked
        let result =
            call session """(call-tool "edit_file" (list :path "anywhere.txt" :old_string "x" :new_string "y"))"""
        Assert.False(isErrorOf result)
        Assert.Equal("y\n", File.ReadAllText(Path.Combine(root, "anywhere.txt")))
    finally
        Directory.Delete(root, true)

[<Fact>]
let ``shell_allow auto-allows exactly its commands`` () =
    let root = makeRoot ()
    try
        let asked = ResizeArray<string>()
        let session =
            sessionWith root [ workspaceSource "/w/jern.json" (parsePolicy """{"shell_allow":["echo"]}""") ]
                        (fun _ _ -> true) asked
        call session """(call-tool "shell" (list :command "echo hi"))""" |> ignore
        Assert.Empty asked                                  // granted: no question
        call session """(call-tool "shell" (list :command "true"))""" |> ignore
        Assert.Single asked |> ignore                       // not granted: asked
        // A metacharacter cannot smuggle a second command past the grant.
        call session """(call-tool "shell" (list :command "echo hi; echo bye"))""" |> ignore
        Assert.Equal(2, asked.Count)
    finally
        Directory.Delete(root, true)

[<Fact>]
let ``deny beats allow, and wildcards match by prefix`` () =
    let root = makeRoot ()
    try
        let asked = ResizeArray<string>()
        let policy = parsePolicy """{"deny":["mcp__*"],"allow":["mcp__github__get_issue","grep"]}"""
        let session = sessionWith root [ workspaceSource "/w/jern.json" policy ] (fun _ _ -> true) asked
        let denied = call session """(call-tool "mcp__github__get_issue" (list :number 1))"""
        Assert.True(isErrorOf denied)
        Assert.Contains("mcp__github__get_issue is denied by", contentOf denied)
        Assert.Empty asked
        // A grant that no restriction covers still applies.
        Assert.False(isErrorOf (call session """(call-tool "grep" (list :pattern "x"))"""))
        Assert.Empty asked
    finally
        Directory.Delete(root, true)

[<Fact>]
let ``memory restrictions from config outrank the base memory policy`` () =
    let root = makeRoot ()
    try
        let asked = ResizeArray<string>()
        let session =
            sessionWith root [ workspaceSource "/w/jern.json" (parsePolicy """{"memory":"deny"}""") ]
                        (fun _ _ -> true) asked
        match call session """(remember "k" "v")""" with
        | Bool false -> ()
        | other -> failwith ("expected the write to be refused, got " + showVal other)
        Assert.True(Memory.get (Memory.storePath root) "k" |> Option.isNone)
    finally
        Directory.Delete(root, true)

// ---------------------------------------------------------------------------
// The trust split

[<Fact>]
let ``declining trust drops the grants and keeps the restrictions`` () =
    let root = makeRoot ()
    try
        File.WriteAllText(Path.Combine(root, "outside.txt"), "x\n")
        let asked = ResizeArray<string>()
        let consulted = ResizeArray<string * string>()
        let policy = parsePolicy """{"edits_within":["src/"],"shell_allow":["echo"]}"""
        let session =
            sessionWith root [ workspaceSource "/w/jern.json" policy ]
                        (fun identity canonical -> consulted.Add(identity, canonical); false) asked
        // The grant half is gone: the command asks like any other.
        call session """(call-tool "shell" (list :command "echo hi"))""" |> ignore
        Assert.Single asked |> ignore
        // The restriction half stands.
        let denied =
            call session """(call-tool "edit_file" (list :path "outside.txt" :old_string "x" :new_string "y"))"""
        Assert.True(isErrorOf denied)
        Assert.Contains("policy: edits are limited to", contentOf denied)
        // Trust was asked once, keyed by identity + canonical JSON.
        let identity, canonical = Assert.Single consulted
        Assert.Equal("/w/jern.json#policy", identity)
        Assert.Equal(PolicyConfig.canonicalJson policy, canonical)
    finally
        Directory.Delete(root, true)

[<Fact>]
let ``a restriction-only policy never asks for trust`` () =
    let root = makeRoot ()
    try
        let asked = ResizeArray<string>()
        let consulted = ResizeArray<string>()
        let session =
            sessionWith root [ workspaceSource "/w/jern.json" (parsePolicy """{"deny":["shell"]}""") ]
                        (fun identity _ -> consulted.Add identity; false) asked
        Assert.True(isErrorOf (call session """(call-tool "shell" (list :command "true"))"""))
        Assert.Empty consulted   // tightening is free
    finally
        Directory.Delete(root, true)

// ---------------------------------------------------------------------------
// The properties the CI story rests on

[<Fact>]
let ``a workspace policy file cannot erase a config restriction`` () =
    let root = makeRoot ()
    try
        File.WriteAllText(Path.Combine(root, "outside.txt"), "x\n")
        Directory.CreateDirectory(Path.Combine(root, ".jern")) |> ignore
        // The most permissive workspace policy imaginable.
        File.WriteAllText(
            Path.Combine(root, ".jern", "policy.ikr"),
            "(define tool-policy (lambda (call) :allow))\n")
        let asked = ResizeArray<string>()
        let session =
            sessionWith root [ workspaceSource "/w/jern.json" (parsePolicy """{"edits_within":["src/"]}""") ]
                        (fun _ _ -> true) asked
        // It does relax what it may: reads and shell stop asking…
        call session """(call-tool "shell" (list :command "true"))""" |> ignore
        Assert.Empty asked
        // …but it cannot turn a restriction's denial into an approval.
        let denied =
            call session """(call-tool "edit_file" (list :path "outside.txt" :old_string "x" :new_string "y"))"""
        Assert.True(isErrorOf denied)
        Assert.Contains("policy: edits are limited to src/", contentOf denied)
        Assert.Equal("x\n", File.ReadAllText(Path.Combine(root, "outside.txt")))
    finally
        Directory.Delete(root, true)

[<Fact>]
let ``a protected baseline outranks head-branch config and policy`` () =
    let root = makeRoot ()
    try
        // The "pull request" carries both a permissive policy file and a
        // jern.json that grants the very tool the baseline denies.
        Directory.CreateDirectory(Path.Combine(root, ".jern")) |> ignore
        File.WriteAllText(
            Path.Combine(root, ".jern", "policy.ikr"),
            "(define tool-policy (lambda (call) :allow))\n")
        let asked = ResizeArray<string>()
        let sources =
            [ baselineSource "base branch" (parsePolicy """{"deny":["shell"],"edits_within":["src/"]}""")
              workspaceSource "/w/jern.json" (parsePolicy """{"allow":["shell"]}""") ]
        let session = sessionWith root sources (fun _ _ -> true) asked
        let denied = call session """(call-tool "shell" (list :command "true"))"""
        Assert.True(isErrorOf denied)
        Assert.Contains("shell is denied by protected baseline: base branch", contentOf denied)
        Assert.Empty asked
    finally
        Directory.Delete(root, true)

[<Fact>]
let ``policy layers announce their provenance in the trace`` () =
    let root = makeRoot ()
    try
        File.WriteAllText(Path.Combine(root, "outside.txt"), "x\n")
        let trace = ResizeArray<string>()
        let policy = parsePolicy """{"edits_within":["src/"]}"""
        let config =
            { Session.configIn root noLlm with
                policySources = [ workspaceSource "/w/jern.json" policy ]
                traceSink = Some trace.Add }
        let session =
            match Session.createWith config with
            | Choice1Of2 error -> failwith (showError error)
            | Choice2Of2 s -> s
        // The layer is announced with its digest when the session is built…
        let layerLine = trace |> Seq.find (fun l -> l.Contains "\"event\":\"policy-layer\"")
        Assert.Contains(PolicyConfig.digest policy, layerLine)
        Assert.Contains("\"source\":\"jern.json\"", layerLine)
        // …and every decision records which layer made it.
        call session """(call-tool "edit_file" (list :path "outside.txt" :old_string "x" :new_string "y"))"""
        |> ignore
        let decision = trace |> Seq.find (fun l -> l.Contains "\"event\":\"policy-decision\"")
        Assert.Contains("jern.json edits_within", decision)
    finally
        Directory.Delete(root, true)

[<Fact>]
let ``an environment object is recognised and validated but never applied here`` () =
    match PolicyConfig.parseEnvironment (JsonNode.Parse("""{"services":["postgres:16","redis:7"]}""": string)) with
    | Ok environment -> Assert.Equal<string list>([ "postgres:16"; "redis:7" ], environment.services)
    | Error message -> failwith message
    match PolicyConfig.parseEnvironment (JsonNode.Parse("""{}""": string)) with
    | Ok environment -> Assert.True(PolicyConfig.environmentIsEmpty environment)
    | Error message -> failwith message
    let rejected (json: string) =
        match PolicyConfig.parseEnvironment (JsonNode.Parse(json: string)) with
        | Error _ -> true
        | Ok _ -> false
    // A key this runtime does not know is kept and named, never refused: a
    // host ahead of the runtime must not fail every session.
    match PolicyConfig.parseEnvironment (JsonNode.Parse("""{"servcies":["postgres:16"],"network_allow":["docs.python.org"]}""": string)) with
    | Ok environment ->
        Assert.Equal<string list>([ "servcies" ], environment.unknown)
        Assert.Equal<string list>([ "docs.python.org" ], environment.networkAllow)
        Assert.False(PolicyConfig.environmentIsEmpty environment)
    | Error message -> failwith message
    Assert.True(rejected """{"services":"postgres:16"}""")
    Assert.True(rejected """{"services":["Postgres 16"]}""")
    Assert.True(rejected """{"network_allow":["Docs.Python.Org"]}""")
    Assert.True(rejected """{"network_allow":["localhost"]}""")
    Assert.True(rejected """["postgres:16"]""")
    Assert.Equal("services postgres:16; network docs.python.org; keys this runtime does not know: servcies",
                 PolicyConfig.describeEnvironment { PolicyConfig.services = [ "postgres:16" ]; PolicyConfig.networkAllow = [ "docs.python.org" ]; PolicyConfig.unknown = [ "servcies" ] })

[<Fact>]
let ``a tool pack in allow or deny expands when the policy compiles`` () =
    let policy = parsePolicy """{"deny":["pack:git","mcp__*"],"allow":["pack:verify"]}"""
    // The JSON keeps the pack name; the compiled layer names every tool.
    Assert.Equal("""{"allow":["pack:verify"],"deny":["pack:git","mcp__*"]}""", PolicyConfig.canonicalJson policy)
    let compiled = PolicyConfig.compile "jern.json" true policy
    Assert.Contains("\"git_status\" \"git_diff\" \"git_log\" \"git_blame\" \"changed_set\"", compiled)
    Assert.Contains("\"run_tests\"", compiled)
    Assert.Contains("\"mcp__\"", compiled)
    Assert.Contains("deny: pack:git, mcp__*", PolicyConfig.describeRestrictions policy)
    match PolicyConfig.parse (JsonNode.Parse """{"deny":["pack:nope"]}""") with
    | Ok _ -> failwith "expected the unknown pack to be rejected"
    | Error message -> Assert.Contains("unknown tool pack pack:nope", message)

/// The policy judges the canonical path — the one the tool will act on —
/// so a prefix rule cannot be satisfied by traversal, by a near-miss name,
/// or by a link under the prefix that points elsewhere.
[<Fact>]
let ``edits_within judges the canonical path at a directory boundary`` () =
    let root = makeRoot ()
    try
        Directory.CreateDirectory(Path.Combine(root, "src")) |> ignore
        Directory.CreateDirectory(Path.Combine(root, "srcs")) |> ignore
        Directory.CreateDirectory(Path.Combine(root, "tests")) |> ignore
        let asked = ResizeArray<string>()
        let session =
            sessionWith root [ workspaceSource "/w/jern.json" (parsePolicy """{"edits_within":["src/"]}""") ]
                        (fun _ _ -> true) asked
        let write path =
            call session (sprintf """(call-tool "write_file" (list :path "%s" :content "y\n"))""" path)
        // Traversal out of the prefix is denied by the policy, not merely
        // by the tool: the file is never written.
        let traversal = write "src/../outside.txt"
        Assert.True(isErrorOf traversal)
        Assert.Contains("policy: edits are limited to src/", contentOf traversal)
        Assert.False(File.Exists(Path.Combine(root, "outside.txt")))
        Assert.True(isErrorOf (write "./src/../srcs/near.txt"))
        Assert.True(isErrorOf (write "srcs/near.txt"))          // a name that merely starts with src
        Assert.Empty asked
        // Inside, in any spelling, the ordinary rules apply.
        Assert.False(isErrorOf (write "src/./deep/../a.txt"))
        Assert.True(File.Exists(Path.Combine(root, "src", "a.txt")))
        if not (OperatingSystem.IsWindows()) then
            // A link under src/ that points at tests/ is an edit under tests/.
            Directory.CreateSymbolicLink(Path.Combine(root, "src", "link"), Path.Combine(root, "tests")) |> ignore
            let viaLink = write "src/link/b.txt"
            Assert.True(isErrorOf viaLink)
            Assert.Contains("policy: edits are limited to src/", contentOf viaLink)
            Assert.False(File.Exists(Path.Combine(root, "tests", "b.txt")))
        // policy_check answers the same way, before any attempt.
        let probe =
            call session """(call-tool "policy_check" (list :name "write_file" :input (list :path "src/../outside.txt" :content "y")))"""
        Assert.StartsWith("deny: policy: edits are limited to src/", contentOf probe)
    finally
        Directory.Delete(root, true)

[<Fact>]
let ``pathWithin folds traversal, resolves links, and stops at directory boundaries`` () =
    let root = makeRoot ()
    try
        Directory.CreateDirectory(Path.Combine(root, "src")) |> ignore
        Assert.True(Tools.pathWithin root "src/a.txt" "src/")
        Assert.True(Tools.pathWithin root "src/a.txt" "src")
        Assert.True(Tools.pathWithin root "./src/x/../a.txt" "./src/")
        Assert.True(Tools.pathWithin root "src" "src/")
        Assert.True(Tools.pathWithin root "anything/at/all.txt" ".")
        Assert.False(Tools.pathWithin root "src/../a.txt" "src/")
        Assert.False(Tools.pathWithin root "srcs/a.txt" "src/")
        Assert.False(Tools.pathWithin root "../escape.txt" "src/")
        Assert.False(Tools.pathWithin root "../escape.txt" ".")
        Assert.False(Tools.pathWithin root "/etc/passwd" ".")
        Assert.Equal(Some "src/a.txt", Tools.workspaceRelativePath root "src/./a.txt")
        Assert.Equal(Some ".", Tools.workspaceRelativePath root ".")
        Assert.Equal(None, Tools.workspaceRelativePath root "../x")
    finally
        Directory.Delete(root, true)
