module Jern.Tests.MemoryTests

open System
open System.IO
open Xunit
open IronKernel.Ast
open IronKernel.Errors
open Jern.Host

/// A bridge for sessions whose tests never reach the model.
let private noLlm: AnthropicBridge.LlmBridge =
    fun _ -> Choice1Of2 (Default "no llm expected in this test")

let private makeRoot () =
    let root = Path.Combine(Path.GetTempPath(), "jern-memory-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory root |> ignore
    root

let private newSession (root: string) (trace: ResizeArray<string> option) =
    let config =
        { Session.configIn root noLlm with
            traceSink = trace |> Option.map (fun t -> t.Add) }
    match Session.createWith config with
    | Choice1Of2 error -> failwith (showError error)
    | Choice2Of2 session -> session

let private run session source =
    match Session.runSource session "test" source with
    | Choice1Of2 error -> failwith (showError error)
    | Choice2Of2 value -> value

let private expectBool expected value =
    match value with
    | Bool b -> Assert.Equal((expected: bool), b)
    | other -> failwith ("expected a boolean, got " + showVal other)

let private expectNull value =
    match value with
    | Keyword "null" -> ()
    | other -> failwith ("expected :null, got " + showVal other)

[<Fact>]
let ``remember and recall round-trip and persist across sessions`` () =
    let root = makeRoot ()
    try
        let trace = ResizeArray<string>()
        let first = newSession root (Some trace)
        expectBool true (run first """(remember "build" "dotnet test, 124 green")""")
        match run first """(recall "build")""" with
        | Obj (:? string as value) -> Assert.Equal("dotnet test, 124 green", value)
        | other -> failwith ("unexpected recall result: " + showVal other)
        // Unknown keys answer :null.
        expectNull (run first """(recall "nothing")""")
        // The store is ordinary workspace data…
        Assert.True(File.Exists(Memory.storePath root))
        // …and a *fresh* session sees it: memory is cross-session.
        let second = newSession root None
        match run second """(recall "build")""" with
        | Obj (:? string as value) -> Assert.Equal("dotnet test, 124 green", value)
        | other -> failwith ("unexpected recall result: " + showVal other)
        // Every access crossed the trace choke point.
        let count (marker: string) =
            trace |> Seq.filter (fun l -> l.Contains marker) |> Seq.length
        Assert.Equal(1, count "\"event\":\"memory-remember\"")
        Assert.Equal(2, count "\"event\":\"memory-recall\"")
    finally
        Directory.Delete(root, true)

[<Fact>]
let ``a workspace policy can deny remembering`` () =
    let root = makeRoot ()
    try
        Directory.CreateDirectory(Path.Combine(root, ".jern")) |> ignore
        File.WriteAllText(
            Path.Combine(root, ".jern", "policy.ikr"),
            "(define memory-policy\n  (lambda (op key) (if (equal? op \"remember\") \"memory is read-only here\" :allow)))\n")
        let session = newSession root None
        expectBool false (run session """(remember "k" "v")""")
        expectNull (run session """(recall "k")""")
        Assert.True(Memory.get (Memory.storePath root) "k" |> Option.isNone)
    finally
        Directory.Delete(root, true)

[<Fact>]
let ``an ask decision routes memory through the approver`` () =
    let root = makeRoot ()
    try
        Directory.CreateDirectory(Path.Combine(root, ".jern")) |> ignore
        File.WriteAllText(
            Path.Combine(root, ".jern", "policy.ikr"),
            "(define memory-policy\n  (lambda (op key) (if (equal? op \"remember\") :ask :allow)))\n")
        let asked = ResizeArray<string>()
        let config =
            { Session.configIn root noLlm with
                approver = Some(fun description ->
                    asked.Add description
                    false) }
        let session =
            match Session.createWith config with
            | Choice1Of2 error -> failwith (showError error)
            | Choice2Of2 s -> s
        expectBool false (run session """(remember "secret" "v")""")
        Assert.Equal("remember: secret", Assert.Single asked)
        Assert.True(Memory.get (Memory.storePath root) "secret" |> Option.isNone)
    finally
        Directory.Delete(root, true)
