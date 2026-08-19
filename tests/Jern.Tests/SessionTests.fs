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
