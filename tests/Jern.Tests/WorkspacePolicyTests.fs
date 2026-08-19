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
