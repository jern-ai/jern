module Iron.Tests.GitTests

open System
open System.Diagnostics
open System.IO
open Xunit
open IronKernel
open IronKernel.Ast
open IronKernel.Errors
open Iron.Host

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
    let root = Path.Combine(Path.GetTempPath(), "iron-git-" + Guid.NewGuid().ToString("N"))
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
        Assert.Equal("iron: edit a.txt", subject)
        let body = sh root "git log -1 --format=%b"
        Assert.Contains("Change one to two in a.txt", body)
        Assert.Equal("iron@localhost", sh root "git log -1 --format=%ae")
        // Working tree is clean afterwards: killing iron here loses nothing.
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
            [| "iron: edit a.txt"
               "iron: save your uncommitted changes to a.txt"
               "files"; "root" |], subjects)
        // Undo pops only iron's edit; the saved user changes survive.
        match Git.undoLast root with
        | Error e -> failwith e
        | Ok subject -> Assert.Equal("iron: edit a.txt", subject)
        Assert.Equal("one\n", File.ReadAllText(Path.Combine(root, "a.txt"))))

[<Fact>]
let ``undo refuses when head is not an iron commit`` () =
    withRepo (fun root ->
        match Git.undoLast root with
        | Ok _ -> failwith "expected refusal"
        | Error message -> Assert.Contains("not an iron commit", message))

[<Fact>]
let ``outside a repository the git layer is silent`` () =
    let root = Path.Combine(Path.GetTempPath(), "iron-nogit-" + Guid.NewGuid().ToString("N"))
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
