module Jern.Tests.AgentLoopTests

open System
open System.IO
open Xunit
open IronKernel
open IronKernel.Ast
open IronKernel.Errors
open Jern.Host

let private response json : ThrowsError<LispVal> =
    Choice2Of2 (Json.deserialize json)

/// Drive the real default agent (agents/default/src) against a scripted
/// model: read the file, fix the typo, then report done.
[<Fact>]
let ``default agent loop reads, edits, and finishes a task`` () =
    let root = Path.Combine(Path.GetTempPath(), "jern-loop-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory root |> ignore
    File.WriteAllText(Path.Combine(root, "greeting.txt"), "helo world\n")
    let trace = ResizeArray<string>()
    try
        let mutable turn = 0
        let scriptedBridge: AnthropicBridge.LlmBridge =
            fun request ->
                turn <- turn + 1
                let json = Json.serialize request
                // Every turn carries the system prompt and the tool schemas.
                Assert.Contains("\"system\":", json)
                Assert.Contains("\"name\":\"edit_file\"", json)
                match turn with
                | 1 ->
                    response """{"role":"assistant","stop_reason":"tool_use","content":[{"type":"text","text":"Reading the file."},{"type":"tool_use","id":"t1","name":"read_file","input":{"path":"greeting.txt"}}]}"""
                | 2 ->
                    // The loop must have appended the read result.
                    Assert.Contains("helo world", json)
                    response """{"role":"assistant","stop_reason":"tool_use","content":[{"type":"tool_use","id":"t2","name":"edit_file","input":{"path":"greeting.txt","old_string":"helo","new_string":"hello"}}]}"""
                | _ ->
                    // ' is JSON-escaped as ', so match around it.
                    Assert.Contains("edited \\u0027greeting.txt", json)
                    response """{"role":"assistant","stop_reason":"end_turn","content":[{"type":"text","text":"Fixed the typo."}]}"""
        let repoAgentDir = Path.Combine(__SOURCE_DIRECTORY__, "..", "..", "agents", "default")
        let config =
            { Session.configIn root scriptedBridge with
                traceSink = Some trace.Add
                agentSources = Session.agentPackageSources repoAgentDir }
        let session =
            match Session.createWith config with
            | Choice1Of2 error -> failwith (showError error)
            | Choice2Of2 s -> s
        match Session.runAgent session "Fix the typo in greeting.txt" with
        | Choice1Of2 error -> failwith (showError error)
        | Choice2Of2 result ->
            // The loop's final value is the closing text.
            match result with
            | Obj (:? string as text) -> Assert.Equal("Fixed the typo.", text)
            | other -> failwith ("unexpected final value: " + showVal other)
        Assert.Equal(3, turn)
        Assert.Equal("hello world\n", File.ReadAllText(Path.Combine(root, "greeting.txt")))
        // The trace saw every effect: agent log, 3 llm calls, and 6 tool
        // calls (read + edit, one first-turn file_tree, and one
        // CONVENTIONS.md probe per model turn).
        let count (marker: string) =
            trace |> Seq.filter (fun (l: string) -> l.Contains marker) |> Seq.length
        Assert.Equal(1, count "\"event\":\"log\"")
        Assert.Equal(3, count "\"event\":\"llm-call\"")
        Assert.Equal(3, count "\"event\":\"llm-response\"")
        Assert.Equal(6, count "\"event\":\"tool-call\"")
        Assert.Equal(6, count "\"event\":\"tool-result\"")
        // Trace lines are timestamped JSON.
        Assert.All(trace, fun line -> Assert.StartsWith("{\"ts\":\"", line))
    finally
        Directory.Delete(root, true)

[<Fact>]
let ``agent loop stops at max turns`` () =
    let root = Path.Combine(Path.GetTempPath(), "jern-loop-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory root |> ignore
    File.WriteAllText(Path.Combine(root, "f.txt"), "x\n")
    try
        let mutable calls = 0
        let loopingBridge: AnthropicBridge.LlmBridge =
            fun _ ->
                calls <- calls + 1
                response """{"role":"assistant","stop_reason":"tool_use","content":[{"type":"tool_use","id":"t","name":"read_file","input":{"path":"f.txt"}}]}"""
        let repoAgentDir = Path.Combine(__SOURCE_DIRECTORY__, "..", "..", "agents", "default")
        let config =
            { Session.configIn root loopingBridge with
                agentSources = Session.agentPackageSources repoAgentDir }
        let session =
            match Session.createWith config with
            | Choice1Of2 error -> failwith (showError error)
            | Choice2Of2 s -> s
        match Session.runAgent session "loop forever" with
        | Choice1Of2 error -> failwith (showError error)
        | Choice2Of2 result ->
            match result with
            | Keyword "null" -> ()
            | other -> failwith ("expected :null at max turns, got " + showVal other)
        Assert.Equal(50, calls)
    finally
        Directory.Delete(root, true)
