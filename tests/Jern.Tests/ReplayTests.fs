module Jern.Tests.ReplayTests

open System
open System.IO
open Xunit
open IronKernel.Ast
open IronKernel.Errors
open Jern.Host

let private repoAgentDir () =
    Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", "..", "agents", "default"))

let private response json : ThrowsError<LispVal> =
    Choice2Of2 (Json.deserialize json)

/// Run the real default agent against a scripted model in a scratch
/// workspace, capturing the JSONL trace to a file — the recording that the
/// replay tests fork from.
let private recordTrace () =
    let root = Path.Combine(Path.GetTempPath(), "jern-rec-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory root |> ignore
    File.WriteAllText(Path.Combine(root, "greeting.txt"), "helo world\n")
    let tracePath = Path.Combine(Path.GetTempPath(), "jern-rec-trace-" + Guid.NewGuid().ToString("N") + ".jsonl")
    let scripted: AnthropicBridge.LlmBridge =
        fun request ->
            let json = Json.serialize request
            if json.Contains "edited \\u0027greeting.txt" then
                response """{"role":"assistant","stop_reason":"end_turn","content":[{"type":"text","text":"Fixed the typo."}]}"""
            elif json.Contains "helo world" then
                response """{"role":"assistant","stop_reason":"tool_use","content":[{"type":"tool_use","id":"t2","name":"edit_file","input":{"path":"greeting.txt","old_string":"helo","new_string":"hello"}}]}"""
            else
                response """{"role":"assistant","stop_reason":"tool_use","content":[{"type":"tool_use","id":"t1","name":"read_file","input":{"path":"greeting.txt"}}]}"""
    use writer = new StreamWriter(tracePath, append = false, AutoFlush = true)
    let config =
        { Session.configIn root scripted with
            traceSink = Some writer.WriteLine
            agentSources = Session.agentPackageSources (repoAgentDir ()) }
    match Session.createWith config with
    | Choice1Of2 error -> failwith (showError error)
    | Choice2Of2 session ->
        match Session.runAgent session "Fix the typo in greeting.txt" with
        | Choice1Of2 error -> failwith (showError error)
        | Choice2Of2 _ -> ()
    Directory.Delete(root, true)
    tracePath

let private replay tracePath policyFile =
    Replay.run
        { tracePath = tracePath
          agentDir = repoAgentDir ()
          policyFile = policyFile
          agentConfig = Nil
          mcpServers = [] }

[<Fact>]
let ``a faithful replay re-runs the whole recording offline`` () =
    let tracePath = recordTrace ()
    try
        // The recording workspace is gone by now: everything must come from
        // the trace, nothing from disk, model, or network.
        match replay tracePath None with
        | Error message -> failwith message
        | Ok (Replay.Diverged report) -> failwith ("unexpected divergence: " + report)
        | Ok (Replay.Completed (llmCalls, toolCalls)) ->
            Assert.Equal(3, llmCalls)
            Assert.Equal(6, toolCalls)
    finally
        File.Delete tracePath

[<Fact>]
let ``forking with a stricter policy pinpoints the divergence`` () =
    let tracePath = recordTrace ()
    let policyFile = Path.Combine(Path.GetTempPath(), "jern-strict-" + Guid.NewGuid().ToString("N") + ".ikr")
    try
        // What if edits had been forbidden? Fork the recorded session with
        // the stricter rule and watch where behavior leaves the recording.
        File.WriteAllText(
            policyFile,
            "(define tool-policy\n  (lambda (call)\n    (if (equal? (plist-get call :name) \"edit_file\")\n        \"edits are forbidden in this fork\"\n        :allow)))\n")
        match replay tracePath (Some policyFile) with
        | Error message -> failwith message
        | Ok (Replay.Completed _) -> failwith "expected the stricter policy to diverge"
        | Ok (Replay.Diverged report) ->
            // The denied edit changes the next model request: the recorded
            // one carries the edit result, the actual one the denial.
            Assert.Contains("diverged", report)
            Assert.Contains("recorded:", report)
            Assert.Contains("actual:", report)
    finally
        File.Delete tracePath
        if File.Exists policyFile then File.Delete policyFile

[<Fact>]
let ``a trace without a task is rejected with guidance`` () =
    let tracePath = Path.Combine(Path.GetTempPath(), "jern-notask-" + Guid.NewGuid().ToString("N") + ".jsonl")
    try
        File.WriteAllText(tracePath, """{"ts":"2026-01-01T00:00:00Z","event":"log","data":{"event":"chat-turn"}}""" + "\n")
        match replay tracePath None with
        | Error message -> Assert.Contains("agent-started", message)
        | Ok _ -> failwith "expected an error for a taskless trace"
    finally
        File.Delete tracePath
