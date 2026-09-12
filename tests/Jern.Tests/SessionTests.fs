module Jern.Tests.SessionTests

open Xunit
open IronKernel
open IronKernel.Ast
open IronKernel.Errors
open Jern.Host

/// A canned provider: replies with one text block echoing that it was called.
let private mockBridge (reply: string) : AnthropicBridge.LlmBridge =
    fun _request ->
        Json.deserialize (sprintf """{"role":"assistant","content":[{"type":"text","text":"%s"}],"stop_reason":"end_turn"}""" reply)
        |> Choice2Of2

let private newSession bridge =
    match Session.create bridge with
    | Choice1Of2 error -> failwith (showError error)
    | Choice2Of2 session -> session

let private run session source =
    match Session.runSource session "test.ikr" source with
    | Choice1Of2 error -> failwith (showError error)
    | Choice2Of2 value -> value

[<Fact>]
let ``performing jern/llm-call reaches the bridge and returns its reply`` () =
    let session = newSession (mockBridge "mock says hi")
    let result =
        run session
            """(response-text (perform jern/llm-call (list :messages (vector))))"""
    match result with
    | Obj o -> Assert.Equal("mock says hi", o :?> string)
    | other -> failwith ("unexpected result: " + showVal other)

[<Fact>]
let ``bridge receives the request the agent authored`` () =
    let mutable seen = ""
    let bridge: AnthropicBridge.LlmBridge =
        fun request ->
            seen <- Json.serialize request
            (mockBridge "ok") request
    let session = newSession bridge
    run session """(perform jern/llm-call (list :model "claude-opus-5" :max_tokens 7))"""
    |> ignore
    Assert.Equal("""{"model":"claude-opus-5","max_tokens":7}""", seen)

[<Fact>]
let ``bridge errors surface as kernel errors`` () =
    let bridge: AnthropicBridge.LlmBridge =
        fun _ -> Choice1Of2 (Default "provider unavailable")
    let session = newSession bridge
    match Session.runSource session "test.ikr" "(perform jern/llm-call (list))" with
    | Choice1Of2 error -> Assert.Contains("provider unavailable", showError error)
    | Choice2Of2 value -> failwith ("expected an error, got " + showVal value)

[<Fact>]
let ``agent code cannot reach the host bridge directly`` () =
    let session = newSession (mockBridge "nope")
    match Session.runSource session "test.ikr" "(jern/host-llm-call (list))" with
    | Choice1Of2 error ->
        Assert.Contains("unbound", (showError error).ToLowerInvariant())
    | Choice2Of2 value -> failwith ("expected unbound-variable error, got " + showVal value)

[<Fact>]
let ``prelude plist-get works in the agent environment`` () =
    let session = newSession (mockBridge "unused")
    match run session """(plist-get (list :a 1 :b 2) :b)""" with
    | Obj o -> Assert.Equal(2, o :?> int)
    | other -> failwith ("unexpected: " + showVal other)

/// The runtime source — prelude, tools, policy, handlers — runs privileged,
/// so it comes from the install beside the binary and never from the
/// current directory: a workspace that ships its own `kernel/` cannot
/// replace the handler stack. A developer names another copy explicitly.
[<Fact>]
let ``kernel source is read from the install, never from the working directory`` () =
    let installed = System.IO.Path.Combine(System.AppContext.BaseDirectory, "kernel", "policy.ikr")
    Assert.Equal(installed, Session.kernelFile "policy.ikr")
    // A `kernel/` in the working directory changes nothing.
    let cwdKernel = System.IO.Path.Combine(System.Environment.CurrentDirectory, "kernel")
    let created = not (System.IO.Directory.Exists cwdKernel)
    if created then System.IO.Directory.CreateDirectory cwdKernel |> ignore
    let planted = System.IO.Path.Combine(cwdKernel, "policy.ikr")
    let plantedBefore = System.IO.File.Exists planted
    try
        if not plantedBefore then System.IO.File.WriteAllText(planted, "(define tool-policy (lambda (call) :allow))\n")
        Assert.Equal(installed, Session.kernelFile "policy.ikr")
    finally
        if not plantedBefore then System.IO.File.Delete planted
        if created then System.IO.Directory.Delete(cwdKernel, true)
    // Only an explicit JERN_KERNEL_DIR points elsewhere.
    let previous = System.Environment.GetEnvironmentVariable "JERN_KERNEL_DIR"
    try
        System.Environment.SetEnvironmentVariable("JERN_KERNEL_DIR", "/elsewhere/kernel")
        Assert.Equal(System.IO.Path.Combine("/elsewhere/kernel", "policy.ikr"), Session.kernelFile "policy.ikr")
    finally
        System.Environment.SetEnvironmentVariable("JERN_KERNEL_DIR", previous)
