module Jern.Tests.PolicyTests

open System
open System.IO
open Xunit
open IronKernel
open IronKernel.Ast
open IronKernel.Errors
open Jern.Host

let private mockBridge: AnthropicBridge.LlmBridge =
    fun _ -> Choice2Of2 (Json.deserialize """{"role":"assistant","content":[],"stop_reason":"end_turn"}""")

let private withWorkspace (body: string -> unit) =
    let root = Path.Combine(Path.GetTempPath(), "jern-policy-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory root |> ignore
    File.WriteAllText(Path.Combine(root, "a.txt"), "one\n")
    try body root
    finally Directory.Delete(root, true)

let private sessionWith root approver trace =
    let config =
        { Session.configIn root mockBridge with
            approver = approver
            traceSink = trace }
    match Session.createWith config with
    | Choice1Of2 error -> failwith (showError error)
    | Choice2Of2 s -> s

let private run session source =
    match Session.runSource session "test.ikr" source with
    | Choice1Of2 error -> failwith (showError error)
    | Choice2Of2 value -> value

let private field key result =
    match Tools.plistTryGet key result with
    | Some v -> v
    | None -> failwithf "missing :%s in %s" key (showVal result)

[<Fact>]
let ``reads are allowed without approval`` () =
    withWorkspace (fun root ->
        let mutable asked = 0
        let session = sessionWith root (Some(fun _ -> asked <- asked + 1; true)) None
        let result = run session """(call-tool "read_file" (list :path "a.txt"))"""
        Assert.Equal(0, asked)
        match field "is_error" result with
        | Bool false -> ()
        | other -> failwith (showVal other))

[<Fact>]
let ``writes ask and proceed when approved`` () =
    withWorkspace (fun root ->
        let mutable questions = []
        let session =
            sessionWith root (Some(fun q -> questions <- q :: questions; true)) None
        let result =
            run session """(call-tool "edit_file" (list :path "a.txt" :old_string "one" :new_string "two"))"""
        match field "is_error" result with
        | Bool false -> ()
        | other -> failwith (showVal other)
        // The approval question is a minimal diff preview.
        Assert.Equal<string list>([ "edit_file: a.txt\n  - one\n  + two" ], questions)
        Assert.Equal("two\n", File.ReadAllText(Path.Combine(root, "a.txt"))))

[<Fact>]
let ``denied writes become error results and change nothing`` () =
    withWorkspace (fun root ->
        let trace = ResizeArray<string>()
        let session = sessionWith root (Some(fun _ -> false)) (Some trace.Add)
        let result =
            run session """(call-tool "edit_file" (list :path "a.txt" :old_string "one" :new_string "two"))"""
        match field "is_error" result with
        | Bool true -> ()
        | other -> failwith (showVal other)
        match field "content" result with
        | Obj (:? string as s) -> Assert.Contains("declined", s)
        | other -> failwith (showVal other)
        Assert.Equal("one\n", File.ReadAllText(Path.Combine(root, "a.txt")))
        Assert.Contains(trace, fun l -> l.Contains "\"event\":\"approval-denied\"")
        // No tool-call event: the denied call never reached the executor.
        Assert.DoesNotContain(trace, fun l -> l.Contains "\"event\":\"tool-call\""))

[<Fact>]
let ``shell asks with the command in the question`` () =
    withWorkspace (fun root ->
        let mutable question = ""
        let session = sessionWith root (Some(fun q -> question <- q; true)) None
        run session """(call-tool "shell" (list :command "echo ok"))""" |> ignore
        Assert.Equal("shell: echo ok", question))

[<Fact>]
let ``every decision is traced`` () =
    withWorkspace (fun root ->
        let trace = ResizeArray<string>()
        let session = sessionWith root (Some(fun _ -> true)) (Some trace.Add)
        run session """(call-tool "read_file" (list :path "a.txt"))""" |> ignore
        run session """(call-tool "shell" (list :command "true"))""" |> ignore
        let decisions = trace |> Seq.filter (fun l -> l.Contains "\"event\":\"policy-decision\"") |> List.ofSeq
        Assert.Equal(2, decisions.Length)
        Assert.Contains(decisions, fun l -> l.Contains "\"decision\":\"allow\"")
        Assert.Contains(decisions, fun l -> l.Contains "\"decision\":\"ask\""))

[<Fact>]
let ``macOS sandbox blocks shell writes outside the workspace`` () =
    if OperatingSystem.IsMacOS() && File.Exists "/usr/bin/sandbox-exec" then
        withWorkspace (fun root ->
            let outside = Path.Combine(Path.GetTempPath(), "jern-escape-" + Guid.NewGuid().ToString("N"))
            let session = sessionWith root (Some(fun _ -> true)) None
            // Home is outside the write profile; /tmp and the workspace are not.
            let result =
                run session
                    (sprintf """(call-tool "shell" (list :command "touch %s/x 2>&1"))""" (Environment.GetFolderPath Environment.SpecialFolder.UserProfile))
            match field "is_error" result with
            | Bool true -> ()
            | other -> failwith ("expected sandbox denial, got " + showVal other)
            // Writing inside the workspace still works.
            let inside = run session """(call-tool "shell" (list :command "touch inner && ls inner"))"""
            match field "is_error" inside with
            | Bool false -> ()
            | other -> failwith ("expected success, got " + showVal other)
            if Directory.Exists outside then Directory.Delete(outside, true))
