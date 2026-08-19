module Iron.Tests.ChatTests

open System
open System.IO
open Xunit
open IronKernel
open IronKernel.Ast
open IronKernel.Errors
open Iron.Host

let private repoAgentDir () =
    Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", "..", "agents", "default"))

let private withWorkspace (body: string -> unit) =
    let root = Path.Combine(Path.GetTempPath(), "iron-chat-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory root |> ignore
    try body root
    finally Directory.Delete(root, true)

let private textReply (text: string) : ThrowsError<LispVal> =
    Choice2Of2 (Json.deserialize (sprintf """{"role":"assistant","stop_reason":"end_turn","content":[{"type":"text","text":"%s"}]}""" text))

[<Fact>]
let ``chat turns accumulate history across calls`` () =
    withWorkspace (fun root ->
        let requests = ResizeArray<string>()
        let bridge: AnthropicBridge.LlmBridge =
            fun request ->
                requests.Add(Json.serialize request)
                textReply (sprintf "reply %d" requests.Count)
        let config =
            { Session.configIn root bridge with
                agentSources = Session.agentPackageSources (repoAgentDir ()) }
        let session =
            match Session.createWith config with
            | Choice1Of2 e -> failwith (showError e)
            | Choice2Of2 s -> s
        let afterFirst =
            match Session.runChatTurn session Nil "first question" with
            | Choice1Of2 e -> failwith (showError e)
            | Choice2Of2 m -> m
        let afterSecond =
            match Session.runChatTurn session afterFirst "second question" with
            | Choice1Of2 e -> failwith (showError e)
            | Choice2Of2 m -> m
        Assert.Equal(2, requests.Count)
        // The second request carries the whole first exchange, in order.
        let second = requests.[1]
        Assert.Contains("first question", second)
        Assert.Contains("reply 1", second)
        Assert.Contains("second question", second)
        Assert.True(second.IndexOf "first question" < second.IndexOf "reply 1")
        Assert.True(second.IndexOf "reply 1" < second.IndexOf "second question")
        // Four messages, newest first.
        match afterSecond with
        | Pair { car = newest } ->
            match Tools.plistTryGet "role" newest with
            | Some (Obj (:? string as role)) -> Assert.Equal("assistant", role)
            | other -> failwithf "unexpected newest role: %A" other
        | other -> failwith ("expected message list, got " + showVal other))

[<Fact>]
let ``session store round-trips a conversation`` () =
    withWorkspace (fun root ->
        let messages =
            Json.deserialize """[{"role":"user","content":"hi"},{"role":"assistant","content":[{"type":"text","text":"hello"}]}]"""
            |> function
               | Vector items -> items |> Array.toList |> List.rev |> ofList
               | other -> failwith (showVal other)
        SessionStore.save root "test-session" messages
        match SessionStore.load root "test-session" with
        | Error e -> failwith e
        | Ok loaded ->
            // Same canonical JSON both ways proves a lossless round trip.
            let canonical (m: LispVal) =
                let rec items v = match v with Pair c -> c.car :: items c.cdr | _ -> []
                Json.serialize (Vector(items m |> List.rev |> Array.ofList))
            Assert.Equal(canonical messages, canonical loaded)
        Assert.Equal(Some "test-session", SessionStore.latest root))

[<Fact>]
let ``latest returns none for a fresh workspace`` () =
    withWorkspace (fun root -> Assert.Equal(None, SessionStore.latest root))
