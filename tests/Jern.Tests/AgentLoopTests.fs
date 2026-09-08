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
        // The trace saw every effect: agent log, 3 llm calls, and 7 tool
        // calls (read + edit, one first-turn file_tree, one .jern/skills
        // listing per run, and one CONVENTIONS.md probe per model turn).
        let count (marker: string) =
            trace |> Seq.filter (fun (l: string) -> l.Contains marker) |> Seq.length
        Assert.Equal(1, count "\"event\":\"log\"")
        Assert.Equal(3, count "\"event\":\"llm-call\"")
        Assert.Equal(3, count "\"event\":\"llm-response\"")
        Assert.Equal(7, count "\"event\":\"tool-call\"")
        Assert.Equal(7, count "\"event\":\"tool-result\"")
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

/// The reasoning knobs materialize on the wire from agent source: with
/// :thinking_tokens configured, every request carries the Anthropic
/// extended-thinking block and a grown max_tokens.
[<Fact>]
let ``configured thinking rides every request from agent source`` () =
    let root = Path.Combine(Path.GetTempPath(), "jern-think-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory root |> ignore
    try
        let mutable sawThinking = false
        let bridge: AnthropicBridge.LlmBridge =
            fun request ->
                let json = Json.serialize request
                Assert.Contains("\"thinking\":{\"type\":\"enabled\",\"budget_tokens\":2048}", json)
                Assert.Contains("\"max_tokens\":10240", json)
                sawThinking <- true
                Choice2Of2(Json.deserialize """{"role":"assistant","stop_reason":"end_turn","content":[{"type":"text","text":"ok"}]}""")
        let repoAgentDir = Path.Combine(__SOURCE_DIRECTORY__, "..", "..", "agents", "default")
        let config =
            { Session.configIn root bridge with
                agentSources = Session.agentPackageSources repoAgentDir
                agentConfig = ofList [ Keyword "thinking_tokens"; Obj(2048 :> obj) ] }
        match Session.createWith config with
        | Choice1Of2 error -> failwith (showError error)
        | Choice2Of2 session ->
            match Session.runAgent session "say ok" with
            | Choice1Of2 error -> failwith (showError error)
            | Choice2Of2 _ -> Assert.True sawThinking
    finally
        Directory.Delete(root, true)

/// A tool result longer than the limit reaches the model as its head and
/// tail plus a handle; read_output pages the whole text back by line.
[<Fact>]
let ``a long tool result is offloaded and pages back through read_output`` () =
    let root = Path.Combine(Path.GetTempPath(), "jern-offload-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory root |> ignore
    let lines = [| for i in 1 .. 3000 -> sprintf "line %04d: %s" i (String.replicate 3 "lorem ipsum ") |]
    File.WriteAllText(Path.Combine(root, "big.txt"), String.Join("\n", lines) + "\n")
    let trace = ResizeArray<string>()
    try
        let bridge: AnthropicBridge.LlmBridge = fun _ -> failwith "no model call expected"
        let config = { Session.configIn root bridge with traceSink = Some trace.Add }
        let session =
            match Session.createWith config with
            | Choice1Of2 error -> failwith (showError error)
            | Choice2Of2 s -> s
        let run source =
            match Session.runSource session "test.ikr" source with
            | Choice1Of2 error -> failwith (showError error)
            | Choice2Of2 value -> value
        let content value =
            match Tools.plistTryGet "content" value with
            | Some (Obj (:? string as text)) -> text
            | other -> failwithf "no content in %A" other
        let clipped = content (run """(call-tool "read_file" (list :path "big.txt"))""")
        Assert.True(clipped.Length <= Tools.defaultLimits.maxToolResultChars + 400, sprintf "clipped result is %d chars" clipped.Length)
        Assert.StartsWith("line 0001:", clipped)
        Assert.Contains("[… output truncated:", clipped)
        Assert.Contains("kept as out_1", clipped)
        Assert.EndsWith("line 3000: lorem ipsum lorem ipsum lorem ipsum \n", clipped)
        // The whole text is in the trace, once, as its own event.
        Assert.Equal(1, trace |> Seq.filter (fun line -> line.Contains "\"event\":\"output-offloaded\"") |> Seq.length)
        let page = content (run """(call-tool "read_output" (list :id "out_1" :from_line 1500 :lines 3))""")
        Assert.StartsWith("out_1 lines 1500-1502 of 3000:\n", page)
        Assert.Contains("line 1500:", page)
        Assert.Contains("line 1502:", page)
        Assert.DoesNotContain("line 1503:", page)
        let tail = content (run """(call-tool "read_output" (list :id "out_1" :from_line 2999))""")
        Assert.StartsWith("out_1 lines 2999-3000 of 3000:\n", tail)
        let missing = run """(call-tool "read_output" (list :id "out_9"))"""
        Assert.Contains("no kept output 'out_9'", content missing)
        // A short result is untouched.
        File.WriteAllText(Path.Combine(root, "small.txt"), "hello\n")
        Assert.Equal("hello\n", content (run """(call-tool "read_file" (list :path "small.txt"))"""))
    finally
        Directory.Delete(root, true)

/// Once the provider reports a context past the threshold, the default
/// agent summarizes its older turns with one model call and continues on
/// the task, the notes, and the newest turns.
[<Fact>]
let ``default agent compacts its context past the threshold`` () =
    let root = Path.Combine(Path.GetTempPath(), "jern-compact-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory root |> ignore
    File.WriteAllText(Path.Combine(root, "f.txt"), "x\n")
    let trace = ResizeArray<string>()
    try
        let mutable turn = 0
        let messageCount (json: string) =
            use document = System.Text.Json.JsonDocument.Parse json
            document.RootElement.GetProperty("messages").GetArrayLength()
        let bridge: AnthropicBridge.LlmBridge =
            fun request ->
                turn <- turn + 1
                let json = Json.serialize request
                match turn with
                | 1 | 2 | 3 | 4 ->
                    // Four tool turns, each reported as a context of 150k tokens.
                    Assert.Equal(turn * 2 - 1, messageCount json)
                    response (sprintf """{"role":"assistant","stop_reason":"tool_use","content":[{"type":"text","text":"reading %d"},{"type":"tool_use","id":"t%d","name":"read_file","input":{"path":"f.txt"}}],"usage":{"input_tokens":150000,"output_tokens":10}}""" turn turn)
                | 5 ->
                    // The summary call: no tools, one user message with the transcript.
                    Assert.DoesNotContain("\"tools\":", json)
                    Assert.Contains("compacting the working memory", json)
                    Assert.Contains("Transcript to compact:", json)
                    Assert.Contains("[tool_use read_file", json)
                    Assert.Equal(1, messageCount json)
                    response """{"role":"assistant","stop_reason":"end_turn","content":[{"type":"text","text":"NOTES: f.txt read three times; nothing changed yet."}],"usage":{"input_tokens":3000,"output_tokens":20}}"""
                | _ ->
                    // The task stays verbatim, the notes ride with it, and the six newest turns are kept.
                    Assert.Equal(7, messageCount json)
                    Assert.Contains("Keep reading f.txt", json)
                    Assert.Contains("NOTES: f.txt read three times", json)
                    Assert.Contains("Notes from the work so far", json)
                    Assert.Contains("\"tools\":", json)
                    response """{"role":"assistant","stop_reason":"end_turn","content":[{"type":"text","text":"Done."}],"usage":{"input_tokens":9000,"output_tokens":5}}"""
        let repoAgentDir = Path.Combine(__SOURCE_DIRECTORY__, "..", "..", "agents", "default")
        let config =
            { Session.configIn root bridge with
                traceSink = Some trace.Add
                agentSources = Session.agentPackageSources repoAgentDir }
        let session =
            match Session.createWith config with
            | Choice1Of2 error -> failwith (showError error)
            | Choice2Of2 s -> s
        match Session.runAgent session "Keep reading f.txt" with
        | Choice1Of2 error -> failwith (showError error)
        | Choice2Of2 (Obj (:? string as text)) -> Assert.Equal("Done.", text)
        | Choice2Of2 other -> failwith ("unexpected final value: " + showVal other)
        Assert.Equal(6, turn)
        let compacted = trace |> Seq.filter (fun line -> line.Contains "\"context-compacted\"") |> List.ofSeq
        Assert.Single compacted |> ignore
        Assert.Contains("\"context_tokens\":150000", compacted.Head)
        Assert.Contains("\"summarized_messages\":2", compacted.Head)
        Assert.Contains("\"kept_messages\":6", compacted.Head)
    finally
        Directory.Delete(root, true)
