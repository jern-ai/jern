module Jern.Tests.GitTests

open System
open System.Diagnostics
open System.IO
open Xunit
open IronKernel
open IronKernel.Ast
open IronKernel.Errors
open Jern.Host

let private sh (root: string) (cmd: string) =
    use p = new Process()
    p.StartInfo.FileName <- "/bin/sh"
    p.StartInfo.ArgumentList.Add "-c"
    p.StartInfo.ArgumentList.Add cmd
    p.StartInfo.WorkingDirectory <- root
    p.StartInfo.RedirectStandardOutput <- true
    p.StartInfo.RedirectStandardError <- true
    p.StartInfo.UseShellExecute <- false
    p.Start() |> ignore
    let out = p.StandardOutput.ReadToEnd()
    p.WaitForExit()
    out.Trim()

let private withRepo (body: string -> unit) =
    let root = Path.Combine(Path.GetTempPath(), "jern-git-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory root |> ignore
    sh root "git init -q -b main && git -c user.name=u -c user.email=u@x commit -q --allow-empty -m root" |> ignore
    try body root
    finally Directory.Delete(root, true)

let private repoAgentDir () =
    Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", "..", "agents", "default"))

let private newSession root bridge =
    let config =
        { Session.configIn root bridge with
            agentSources = Session.agentPackageSources (repoAgentDir ()) }
    match Session.createWith config with
    | Choice1Of2 e -> failwith (showError e)
    | Choice2Of2 s -> s

let private editBridge () : AnthropicBridge.LlmBridge =
    let mutable turn = 0
    fun _ ->
        turn <- turn + 1
        let reply =
            if turn = 1 then
                """{"role":"assistant","stop_reason":"tool_use","content":[{"type":"tool_use","id":"g1","name":"edit_file","input":{"path":"a.txt","old_string":"one","new_string":"two"}}]}"""
            else
                """{"role":"assistant","stop_reason":"end_turn","content":[{"type":"text","text":"done"}]}"""
        Choice2Of2 (Json.deserialize reply)

[<Fact>]
let ``a successful edit is committed with the task in the message`` () =
    withRepo (fun root ->
        File.WriteAllText(Path.Combine(root, "a.txt"), "one\n")
        sh root "git add a.txt && git -c user.name=u -c user.email=u@x commit -qm files" |> ignore
        let session = newSession root (editBridge ())
        match Session.runAgent session "Change one to two in a.txt" with
        | Choice1Of2 e -> failwith (showError e)
        | Choice2Of2 _ -> ()
        Assert.Equal("two\n", File.ReadAllText(Path.Combine(root, "a.txt")))
        let subject = sh root "git log -1 --format=%s"
        Assert.Equal("jern: edit a.txt", subject)
        let body = sh root "git log -1 --format=%b"
        Assert.Contains("Change one to two in a.txt", body)
        Assert.Equal("jern@localhost", sh root "git log -1 --format=%ae")
        // Working tree is clean afterwards: killing jern here loses nothing.
        Assert.Equal("", sh root "git status --porcelain"))

[<Fact>]
let ``dirty user changes are saved on their own commit first`` () =
    withRepo (fun root ->
        File.WriteAllText(Path.Combine(root, "a.txt"), "zero\n")
        sh root "git add a.txt && git -c user.name=u -c user.email=u@x commit -qm files" |> ignore
        // The user edited a.txt but did not commit.
        File.WriteAllText(Path.Combine(root, "a.txt"), "one\n")
        let session = newSession root (editBridge ())
        Session.runAgent session "task" |> ignore
        let subjects = sh root "git log --format=%s" |> fun s -> s.Split '\n'
        Assert.Equal<string[]>(
            [| "jern: edit a.txt"
               "jern: save your uncommitted changes to a.txt"
               "files"; "root" |], subjects)
        // Undo pops only jern's edit; the saved user changes survive.
        match Git.undoLast root with
        | Error e -> failwith e
        | Ok subject -> Assert.Equal("jern: edit a.txt", subject)
        Assert.Equal("one\n", File.ReadAllText(Path.Combine(root, "a.txt"))))

[<Fact>]
let ``undo refuses when head is not a jern commit`` () =
    withRepo (fun root ->
        match Git.undoLast root with
        | Ok _ -> failwith "expected refusal"
        | Error message -> Assert.Contains("not a jern commit", message))

/// `reset --hard` discards tracked working-tree changes: undo must refuse
/// rather than silently destroy edits the user made after jern's commit.
[<Fact>]
let ``undo refuses when the working tree has uncommitted changes`` () =
    withRepo (fun root ->
        File.WriteAllText(Path.Combine(root, "a.txt"), "one\n")
        sh root "git add a.txt && git -c user.name=u -c user.email=u@x commit -qm files" |> ignore
        let session = newSession root (editBridge ())
        Session.runAgent session "task" |> ignore
        Assert.Equal("two\n", File.ReadAllText(Path.Combine(root, "a.txt")))
        // The user edits by hand after jern's commit…
        File.WriteAllText(Path.Combine(root, "a.txt"), "hand edit\n")
        match Git.undoLast root with
        | Ok _ -> failwith "expected refusal"
        | Error message -> Assert.Contains("uncommitted changes", message)
        Assert.Equal("hand edit\n", File.ReadAllText(Path.Combine(root, "a.txt")))
        // …but a merely untracked file does not block undo (reset keeps it).
        File.Delete(Path.Combine(root, "a.txt"))
        sh root "git checkout -- a.txt" |> ignore
        File.WriteAllText(Path.Combine(root, "new.txt"), "untracked\n")
        match Git.undoLast root with
        | Error e -> failwith e
        | Ok subject -> Assert.Equal("jern: edit a.txt", subject)
        Assert.Equal("untracked\n", File.ReadAllText(Path.Combine(root, "new.txt"))))

[<Fact>]
let ``outside a repository the git layer is silent`` () =
    let root = Path.Combine(Path.GetTempPath(), "jern-nogit-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory root |> ignore
    try
        File.WriteAllText(Path.Combine(root, "a.txt"), "one\n")
        let session = newSession root (editBridge ())
        match Session.runAgent session "task" with
        | Choice1Of2 e -> failwith (showError e)
        | Choice2Of2 _ -> ()
        Assert.Equal("two\n", File.ReadAllText(Path.Combine(root, "a.txt")))
    finally
        Directory.Delete(root, true)

[<Fact>]
let ``conventions ride the system prompt when the file exists`` () =
    withRepo (fun root ->
        File.WriteAllText(Path.Combine(root, "CONVENTIONS.md"), "Always use tabs.\n")
        File.WriteAllText(Path.Combine(root, "a.txt"), "one\n")
        let mutable sawConventions = false
        let mutable sawCacheControl = false
        let bridge: AnthropicBridge.LlmBridge =
            fun request ->
                let json = Json.serialize request
                sawConventions <- json.Contains "Always use tabs."
                sawCacheControl <- json.Contains "\"cache_control\":{\"type\":\"ephemeral\"}"
                Choice2Of2 (Json.deserialize """{"role":"assistant","stop_reason":"end_turn","content":[{"type":"text","text":"ok"}]}""")
        let session = newSession root bridge
        Session.runAgent session "hello" |> ignore
        Assert.True(sawConventions, "CONVENTIONS.md must be in the system prompt")
        Assert.True(sawCacheControl, "the request must carry the cache breakpoint"))

let private run session source =
    match Session.runSource session "test.ikr" source with
    | Choice1Of2 error -> failwith (showError error)
    | Choice2Of2 value -> value

let private contentOf (result: LispVal) =
    match Tools.plistTryGet "content" result with
    | Some (Obj (:? string as s)) -> s
    | other -> failwithf "no :content in tool result: %A" other

let private isErrorOf (result: LispVal) =
    match Tools.plistTryGet "is_error" result with
    | Some (Bool b) -> b
    | _ -> false

let private quietBridge: AnthropicBridge.LlmBridge =
    fun _ -> Choice2Of2 (Json.deserialize """{"role":"assistant","stop_reason":"end_turn","content":[{"type":"text","text":"done"}]}""")

[<Fact>]
let ``git_status, git_diff, and git_log read the repository as data`` () =
    withRepo (fun root ->
        File.WriteAllText(Path.Combine(root, "a.txt"), "one\n")
        Directory.CreateDirectory(Path.Combine(root, "src")) |> ignore
        File.WriteAllText(Path.Combine(root, "src", "b.txt"), "b\n")
        File.WriteAllText(Path.Combine(root, "c.txt"), "c\n")
        sh root "git add . && git -c user.name=u -c user.email=u@x commit -qm 'add files'" |> ignore
        File.WriteAllText(Path.Combine(root, "a.txt"), "two\n")
        File.WriteAllText(Path.Combine(root, "src", "b.txt"), "bb\n")
        sh root "git add src/b.txt" |> ignore
        File.WriteAllText(Path.Combine(root, "new.txt"), "n\n")
        let session = newSession root quietBridge
        let status = contentOf (run session """(call-tool "git_status" (list))""")
        Assert.StartsWith("On main", status)
        Assert.Contains("Staged:\n  modified src/b.txt", status)
        Assert.Contains("Unstaged:\n  modified a.txt", status)
        Assert.Contains("Untracked:\n  new.txt", status)
        // Uncommitted change against HEAD by default, staged on request,
        // a ref when named, and per-file counts with stat.
        let diff = contentOf (run session """(call-tool "git_diff" (list))""")
        Assert.Contains("-one\n+two", diff)
        Assert.Contains("-b\n+bb", diff)
        let staged = contentOf (run session """(call-tool "git_diff" (list :staged #t))""")
        Assert.Contains("+bb", staged)
        Assert.DoesNotContain("+two", staged)
        let scoped = contentOf (run session """(call-tool "git_diff" (list :path "src"))""")
        Assert.DoesNotContain("+two", scoped)
        let stat = contentOf (run session """(call-tool "git_diff" (list :stat #t))""")
        Assert.Contains("a.txt", stat)
        Assert.DoesNotContain("+two", stat)
        let bad = run session """(call-tool "git_diff" (list :ref "--output=/tmp/x"))"""
        Assert.True(isErrorOf bad)
        Assert.Contains("not a plain ref", contentOf bad)
        let log = contentOf (run session """(call-tool "git_log" (list :count 5))""")
        let first = log.Split('\n').[0]
        Assert.Contains("add files (u)", first)
        Assert.Contains("\n    a.txt, c.txt, src/b.txt", log)
        Assert.Contains("root (u)", log)
        let scopedLog = contentOf (run session """(call-tool "git_log" (list :path "src/b.txt"))""")
        Assert.DoesNotContain("root", scopedLog)
        // Blame reads the working tree, so an unmodified file names the commit.
        let blame = contentOf (run session """(call-tool "git_blame" (list :path "c.txt" :start 1 :end 1))""")
        Assert.Contains("(u ", blame)
        Assert.EndsWith(" 1) c", blame.TrimEnd()))

[<Fact>]
let ``changed_set reports what this session edited`` () =
    withRepo (fun root ->
        File.WriteAllText(Path.Combine(root, "a.txt"), "one\n")
        sh root "git add a.txt && git -c user.name=u -c user.email=u@x commit -qm files" |> ignore
        let session = newSession root (editBridge ())
        let before = contentOf (run session """(call-tool "changed_set" (list))""")
        Assert.Equal("no files edited in this session yet", before)
        match Session.runAgent session "Change one to two in a.txt" with
        | Choice1Of2 e -> failwith (showError e)
        | Choice2Of2 _ -> ()
        let after = contentOf (run session """(call-tool "changed_set" (list))""")
        Assert.Equal("1 files edited in this session, 2 lines changed:\n  a.txt (2 lines changed)", after))

[<Fact>]
let ``outside a repository the git tools say so`` () =
    let root = Path.Combine(Path.GetTempPath(), "jern-nogit-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory root |> ignore
    try
        let session = newSession root quietBridge
        let status = run session """(call-tool "git_status" (list))"""
        Assert.True(isErrorOf status)
        Assert.Contains("not a git repository", contentOf status)
    finally Directory.Delete(root, true)
