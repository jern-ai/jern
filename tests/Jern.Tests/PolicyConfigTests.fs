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
    // A misspelt key must not pass silently, exactly as in "policy".
    Assert.True(rejected """{"servcies":["postgres:16"]}""")
    Assert.True(rejected """{"services":"postgres:16"}""")
    Assert.True(rejected """{"services":["Postgres 16"]}""")
    Assert.True(rejected """["postgres:16"]""")
    Assert.Equal("services postgres:16", PolicyConfig.describeEnvironment { PolicyConfig.services = [ "postgres:16" ] })
