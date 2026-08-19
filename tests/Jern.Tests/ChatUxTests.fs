module Jern.Tests.ChatUxTests

open System
open System.IO
open Xunit
open IronKernel
open IronKernel.Ast
open IronKernel.Errors
open Jern.Host

let private repoAgentDir () =
    Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", "..", "agents", "default"))

let private withWorkspace (body: string -> unit) =
    let root = Path.Combine(Path.GetTempPath(), "jern-ux-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory root |> ignore
    try body root
    finally Directory.Delete(root, true)

let private textReply text : ThrowsError<LispVal> =
    Choice2Of2 (Json.deserialize (sprintf """{"role":"assistant","stop_reason":"end_turn","content":[{"type":"text","text":"%s"}]}""" text))

[<Fact>]
let ``file_tree lists nested entries and skips build dirs`` () =
    withWorkspace (fun root ->
        Directory.CreateDirectory(Path.Combine(root, "src", "deep")) |> ignore
        Directory.CreateDirectory(Path.Combine(root, "obj")) |> ignore
        File.WriteAllText(Path.Combine(root, "src", "a.txt"), "")
        File.WriteAllText(Path.Combine(root, "src", "deep", "b.txt"), "")
        File.WriteAllText(Path.Combine(root, "obj", "junk.o"), "")
        let session =
            match Session.createIn root (fun _ -> textReply "unused") with
            | Choice1Of2 e -> failwith (showError e)
            | Choice2Of2 s -> s
        match Session.runSource session "t.ikr" """(plist-get (call-tool "file_tree" (list)) :content)""" with
        | Choice1Of2 e -> failwith (showError e)
        | Choice2Of2 (Obj (:? string as tree)) ->
            Assert.Contains("src/", tree)
            Assert.Contains("  deep/", tree)
            Assert.Contains("    b.txt", tree)
            Assert.DoesNotContain("obj", tree)
        | Choice2Of2 other -> failwith (showVal other))

[<Fact>]
let ``the first turn carries the workspace layout, later turns do not`` () =
    withWorkspace (fun root ->
        File.WriteAllText(Path.Combine(root, "notable.txt"), "")
        let requests = ResizeArray<string>()
        let bridge: AnthropicBridge.LlmBridge =
            fun request ->
                requests.Add(Json.serialize request)
                textReply (sprintf "r%d" requests.Count)
        let config =
            { Session.configIn root bridge with
                agentSources = Session.agentPackageSources (repoAgentDir ()) }
        let session =
            match Session.createWith config with
            | Choice1Of2 e -> failwith (showError e)
            | Choice2Of2 s -> s
        let after1 =
            match Session.runChatTurn session Nil "first" with
            | Choice2Of2 m -> m
            | Choice1Of2 e -> failwith (showError e)
        Session.runChatTurn session after1 "second" |> ignore
        Assert.Contains("Workspace layout:", requests.[0])
        Assert.Contains("notable.txt", requests.[0])
        // The second user message is bare; the layout appears exactly once,
        // in the first message of the history.
        let secondTurnText = requests.[1]
        Assert.Equal(1, Text.RegularExpressions.Regex.Matches(secondTurnText, "Workspace layout").Count)
        Assert.Contains("\"content\":\"second\"", secondTurnText))

[<Fact>]
let ``test_command output rides the edit tool result`` () =
    withWorkspace (fun root ->
        File.WriteAllText(Path.Combine(root, "a.txt"), "one\n")
        let requests = ResizeArray<string>()
        let mutable turn = 0
        let bridge: AnthropicBridge.LlmBridge =
            fun request ->
                requests.Add(Json.serialize request)
                turn <- turn + 1
                if turn = 1 then
                    Choice2Of2 (Json.deserialize """{"role":"assistant","stop_reason":"tool_use","content":[{"type":"tool_use","id":"u1","name":"edit_file","input":{"path":"a.txt","old_string":"one","new_string":"two"}}]}""")
                else textReply "done"
        let config =
            { Session.configIn root bridge with
                agentSources = Session.agentPackageSources (repoAgentDir ())
                agentConfig = Json.deserialize """{"test_command":"echo TESTS-RAN && cat a.txt"}""" }
        let session =
            match Session.createWith config with
            | Choice1Of2 e -> failwith (showError e)
            | Choice2Of2 s -> s
        match Session.runAgent session "edit it" with
        | Choice1Of2 e -> failwith (showError e)
        | Choice2Of2 _ -> ()
        // The second request's tool_result carries the test run's output,
        // which itself proves the tests ran after the edit landed.
        Assert.Contains("[test_command]", requests.[1])
        Assert.Contains("TESTS-RAN", requests.[1])
        Assert.Contains("two", requests.[1]))

[<Fact>]
let ``an interrupt aborts the turn before the next dispatch`` () =
    withWorkspace (fun root ->
        let mutable calls = 0
        let flag = ref false
        let bridge: AnthropicBridge.LlmBridge =
            fun _ ->
                calls <- calls + 1
                // Simulate ctrl-c arriving while the model asks for a tool.
                flag.Value <- true
                Choice2Of2 (Json.deserialize """{"role":"assistant","stop_reason":"tool_use","content":[{"type":"tool_use","id":"i1","name":"read_file","input":{"path":"a.txt"}}]}""")
        let config =
            { Session.configIn root bridge with
                agentSources = Session.agentPackageSources (repoAgentDir ())
                interrupted = fun () -> flag.Value }
        let session =
            match Session.createWith config with
            | Choice1Of2 e -> failwith (showError e)
            | Choice2Of2 s -> s
        match Session.runChatTurn session Nil "go" with
        | Choice2Of2 v -> failwith ("expected an interrupt error, got " + showVal v)
        | Choice1Of2 error ->
            Assert.Contains("interrupted", showError error)
        Assert.Equal(1, calls))
