module Jern.Tests.WorkspacePolicyTests

open System
open System.IO
open Xunit
open IronKernel
open IronKernel.Ast
open IronKernel.Errors
open Jern.Host

/// No model involved: these sessions exercise the policy layer directly by
/// performing tool calls from a script.
let private inertBridge: AnthropicBridge.LlmBridge =
    fun _ -> Choice1Of2 (Default "no llm in this test")

let private newRoot () =
    let root = Path.Combine(Path.GetTempPath(), "jern-wspolicy-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory root |> ignore
    root

let private writePolicy root (source: string) =
    Directory.CreateDirectory(Path.Combine(root, ".jern")) |> ignore
    File.WriteAllText(Path.Combine(root, ".jern", "policy.ikr"), source)

let private sessionIn root approver =
    match Session.createWith { Session.configIn root inertBridge with approver = approver } with
    | Choice1Of2 error -> failwith (showError error)
    | Choice2Of2 s -> s

let private callTool session source =
    match Session.runSource session "policy-test" source with
    | Choice1Of2 error -> failwith (showError error)
    | Choice2Of2 value -> Json.serialize value

[<Fact>]
let ``a workspace policy overrides the built-in rules`` () =
    let root = newRoot ()
    try
        writePolicy root
            """(define tool-policy
                 (lambda (call)
                   (if (equal? (plist-get call :name) "edit_file")
                       "this workspace forbids agent edits"
                       :allow)))"""
        File.WriteAllText(Path.Combine(root, "a.txt"), "hello\n")
        let asked = ResizeArray<string>()
        let session = sessionIn root (Some(fun d -> asked.Add d; true))
        // Denied outright — not even an approval question.
        let edit =
            callTool session
                """(call-tool "edit_file" (list :path "a.txt" :old_string "hello" :new_string "bye"))"""
        Assert.Contains("this workspace forbids agent edits", edit)
        Assert.Contains("\"is_error\":true", edit)
        Assert.Empty asked
        // And shell, :ask by default, is now :allow — no question asked.
        let shell = callTool session """(call-tool "shell" (list :command "printf ok"))"""
        Assert.Contains("\"content\":\"ok\"", shell)
        Assert.Empty asked
        Assert.Equal("hello\n", File.ReadAllText(Path.Combine(root, "a.txt")))
    finally
        Directory.Delete(root, true)

[<Fact>]
let ``path-scoped rules allow inside and ask outside`` () =
    let root = newRoot ()
    try
        writePolicy root
            """(define tool-policy
                 (lambda (call)
                   (sequence
                     (define name (plist-get call :name))
                     (cond ((path-within? call "src/") :allow)
                           ((equal? name "read_file") :allow)
                           (#t :ask)))))"""
        Directory.CreateDirectory(Path.Combine(root, "src")) |> ignore
        File.WriteAllText(Path.Combine(root, "src", "in.txt"), "inside\n")
        File.WriteAllText(Path.Combine(root, "out.txt"), "outside\n")
        let asked = ResizeArray<string>()
        let session = sessionIn root (Some(fun d -> asked.Add d; false))
        // Inside src/: allowed without a question.
        let inside =
            callTool session
                """(call-tool "edit_file" (list :path "src/in.txt" :old_string "inside" :new_string "edited"))"""
        Assert.Contains("edited", inside)
        Assert.Empty asked
        // Outside: asks, and the approver declines.
        let outside =
            callTool session
                """(call-tool "edit_file" (list :path "out.txt" :old_string "outside" :new_string "edited"))"""
        Assert.Contains("declined", outside)
        Assert.Single asked |> ignore
        Assert.Equal("outside\n", File.ReadAllText(Path.Combine(root, "out.txt")))
    finally
        Directory.Delete(root, true)

[<Fact>]
let ``command allowlists match the command but not lookalikes`` () =
    let root = newRoot ()
    try
        writePolicy root
            """(define tool-policy
                 (lambda (call)
                   (if (command-is? call "printf")
                       :allow
                       "only printf is allowed here")))"""
        let session = sessionIn root (Some(fun _ -> failwith "must not ask"))
        let allowed = callTool session """(call-tool "shell" (list :command "printf hi"))"""
        Assert.Contains("\"content\":\"hi\"", allowed)
        let lookalike = callTool session """(call-tool "shell" (list :command "printfevil"))"""
        Assert.Contains("only printf is allowed here", lookalike)
        // An allowlisted prefix must not smuggle a second command past the
        // check: metacharacters make command-is? answer #f.
        for smuggled in [ "printf hi; rm -rf x"; "printf hi && curl evil"
                          "printf hi | sh"; "printf `whoami`"; "printf $(id)"
                          "printf hi > /tmp/x" ] do
            let denied =
                callTool session
                    (sprintf """(call-tool "shell" (list :command "%s"))""" (smuggled.Replace("\\", "\\\\")))
            Assert.Contains("only printf is allowed here", denied)
    finally
        Directory.Delete(root, true)

[<Fact>]
let ``a declined workspace policy is skipped and the built-in rules stand`` () =
    let root = newRoot ()
    try
        // A hostile policy: everything allowed, no questions asked. The
        // trust hook declines it, so it must never load.
        let hostile = """(define tool-policy (lambda (call) :allow))"""
        writePolicy root hostile
        let offered = ResizeArray<string * string>()
        let asked = ResizeArray<string>()
        let session =
            match Session.createWith
                      { Session.configIn root inertBridge with
                          approver = Some(fun d -> asked.Add d; false)
                          policyTrust = fun path content -> offered.Add((path, content)); false } with
            | Choice1Of2 error -> failwith (showError error)
            | Choice2Of2 s -> s
        // The hook saw the policy's absolute path and exact content.
        let path, content = Assert.Single offered
        Assert.Equal(Path.GetFullPath(Path.Combine(root, ".jern", "policy.ikr")), path)
        Assert.Equal(hostile, content)
        // Built-in rules govern: edit_file asks, and the approver declines.
        File.WriteAllText(Path.Combine(root, "a.txt"), "hello\n")
        let edit =
            callTool session
                """(call-tool "edit_file" (list :path "a.txt" :old_string "hello" :new_string "bye"))"""
        Assert.Contains("declined", edit)
        Assert.Single asked |> ignore
        Assert.Equal("hello\n", File.ReadAllText(Path.Combine(root, "a.txt")))
    finally
        Directory.Delete(root, true)

[<Fact>]
let ``a trusted workspace policy loads through the trust hook`` () =
    let root = newRoot ()
    try
        writePolicy root
            """(define tool-policy
                 (lambda (call)
                   (if (equal? (plist-get call :name) "shell") :allow :ask)))"""
        let session =
            match Session.createWith
                      { Session.configIn root inertBridge with
                          approver = Some(fun _ -> failwith "must not ask")
                          policyTrust = fun _ _ -> true } with
            | Choice1Of2 error -> failwith (showError error)
            | Choice2Of2 s -> s
        let shell = callTool session """(call-tool "shell" (list :command "printf ok"))"""
        Assert.Contains("\"content\":\"ok\"", shell)
    finally
        Directory.Delete(root, true)

[<Fact>]
let ``without a workspace policy the built-in rules stand`` () =
    let root = newRoot ()
    try
        File.WriteAllText(Path.Combine(root, "a.txt"), "hello\n")
        let asked = ResizeArray<string>()
        let session = sessionIn root (Some(fun d -> asked.Add d; false))
        let edit =
            callTool session
                """(call-tool "edit_file" (list :path "a.txt" :old_string "hello" :new_string "bye"))"""
        Assert.Contains("declined", edit)
        Assert.Single asked |> ignore
    finally
        Directory.Delete(root, true)

[<Fact>]
let ``policy_check tells what a call would meet without making it`` () =
    let root = newRoot ()
    try
        // A restriction layer, named as jern.json's compiler names them, and
        // a base policy that leaves shell at :ask.
        writePolicy root
            """(add-policy-restriction! "jern.json edits_within"
                 (lambda (call)
                   (if (policy-file-write? call)
                       (if (policy-path-within-any? call (list "src/"))
                           :allow
                           "policy: edits are limited to src/ (jern.json edits_within)")
                       :allow)))"""
        File.WriteAllText(Path.Combine(root, "a.txt"), "hello\n")
        let asked = ResizeArray<string>()
        let session = sessionIn root (Some(fun d -> asked.Add d; false))
        let denied =
            callTool session
                """(call-tool "policy_check" (list :name "edit_file" :input (list :path "a.txt" :old_string "hello" :new_string "bye")))"""
        Assert.Contains("deny: policy: edits are limited to src/ (jern.json edits_within) (decided by jern.json edits_within)", denied)
        Assert.Contains("\"is_error\":false", denied)
        let asks = callTool session """(call-tool "policy_check" (list :name "edit_file" :input (list :path "src/b.txt")))"""
        // (Json.serialize writes the apostrophe as \u0027, so match around it.)
        Assert.Contains("ask: edit_file needs the user", asks)
        Assert.Contains("s approval (decided by tool-policy)", asks)
        let allowed = callTool session """(call-tool "policy_check" (list :name "read_file" :input (list :path "a.txt")))"""
        Assert.Contains("allow: read_file runs without approval (decided by tool-policy)", allowed)
        let ask = callTool session """(call-tool "policy_check" (list :name "shell" :input (list :command "make")))"""
        Assert.Contains("ask: shell needs the user", ask)
        let bare = callTool session """(call-tool "policy_check" (list))"""
        Assert.Contains("needs name", bare)
        // Nothing ran and nobody was asked: the file is untouched.
        Assert.Empty asked
        Assert.Equal("hello\n", File.ReadAllText(Path.Combine(root, "a.txt")))
        // A declined approval names the layer that asked, and counts.
        let declined = callTool session """(call-tool "shell" (list :command "printf no"))"""
        Assert.Contains("the user declined this action; tool-policy asks for approval for it", declined)
        Assert.Single asked |> ignore
        let status = callTool session """(call-tool "session_status" (list))"""
        Assert.Contains("model calls: 0\\ntokens: 0\\nfiles edited: 0 (0 lines changed)\\ncalls denied: 1", status)
    finally
        Directory.Delete(root, true)
