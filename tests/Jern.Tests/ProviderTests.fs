module Jern.Tests.ProviderTests

open System
open System.IO
open System.Text
open System.Threading
open System.Text.Json.Nodes
open Xunit
open IronKernel.Ast
open Jern.Host

// ── OpenAI translation ──────────────────────────────────────────────────────

let private canonicalRequest =
    """{
      "model": "ignored-by-translation",
      "max_tokens": 512,
      "system": "be terse",
      "tools": [
        {"name": "read_file", "description": "Read a file",
         "input_schema": {"type": "object", "properties": {"path": {"type": "string"}}, "required": ["path"]}}
      ],
      "messages": [
        {"role": "user", "content": "fix the bug"},
        {"role": "assistant", "content": [
          {"type": "text", "text": "Reading."},
          {"type": "tool_use", "id": "call_1", "name": "read_file", "input": {"path": "a.txt"}}]},
        {"role": "user", "content": [
          {"type": "tool_result", "tool_use_id": "call_1", "content": "helo world", "is_error": false}]}
      ]
    }"""

[<Fact>]
let ``requests translate to chat completions shape`` () =
    let out = OpenAIBridge.translateRequest "gpt-test" (JsonNode.Parse(canonicalRequest).AsObject())
    Assert.Equal("gpt-test", out.["model"].GetValue<string>())
    Assert.Equal(512, out.["max_tokens"].GetValue<int>())
    let messages = out.["messages"].AsArray()
    // system, user, assistant(with tool_calls), tool
    Assert.Equal(4, messages.Count)
    Assert.Equal("system", messages.[0].["role"].GetValue<string>())
    Assert.Equal("be terse", messages.[0].["content"].GetValue<string>())
    Assert.Equal("user", messages.[1].["role"].GetValue<string>())
    let assistant = messages.[2].AsObject()
    Assert.Equal("Reading.", assistant.["content"].GetValue<string>())
    let call = assistant.["tool_calls"].AsArray().[0].AsObject()
    Assert.Equal("call_1", call.["id"].GetValue<string>())
    Assert.Equal("function", call.["type"].GetValue<string>())
    Assert.Equal("read_file", call.["function"].["name"].GetValue<string>())
    Assert.Contains("\"path\"", call.["function"].["arguments"].GetValue<string>())
    let tool = messages.[3].AsObject()
    Assert.Equal("tool", tool.["role"].GetValue<string>())
    Assert.Equal("call_1", tool.["tool_call_id"].GetValue<string>())
    Assert.Equal("helo world", tool.["content"].GetValue<string>())
    let f = out.["tools"].AsArray().[0].["function"].AsObject()
    Assert.Equal("read_file", f.["name"].GetValue<string>())
    Assert.Equal("object", f.["parameters"].["type"].GetValue<string>())

[<Fact>]
let ``responses translate back to the canonical shape`` () =
    let openai =
        """{
          "model": "gpt-test",
          "choices": [{"finish_reason": "tool_calls", "message": {
            "role": "assistant", "content": "On it.",
            "tool_calls": [{"id": "call_9", "type": "function",
                            "function": {"name": "edit_file", "arguments": "{\"path\":\"a.txt\"}"}}]}}],
          "usage": {"prompt_tokens": 100, "completion_tokens": 20}
        }"""
    match OpenAIBridge.translateResponse (JsonNode.Parse(openai).AsObject()) with
    | Error e -> failwith e
    | Ok canonical ->
        Assert.Equal("assistant", canonical.["role"].GetValue<string>())
        Assert.Equal("tool_use", canonical.["stop_reason"].GetValue<string>())
        let content = canonical.["content"].AsArray()
        Assert.Equal("text", content.[0].["type"].GetValue<string>())
        Assert.Equal("On it.", content.[0].["text"].GetValue<string>())
        Assert.Equal("tool_use", content.[1].["type"].GetValue<string>())
        Assert.Equal("call_9", content.[1].["id"].GetValue<string>())
        Assert.Equal("a.txt", content.[1].["input"].["path"].GetValue<string>())
        Assert.Equal(100, canonical.["usage"].["input_tokens"].GetValue<int>())
        Assert.Equal(20, canonical.["usage"].["output_tokens"].GetValue<int>())

[<Fact>]
let ``openai stream chunks accumulate text, tool calls, and usage`` () =
    let collected = Text.StringBuilder()
    let accumulator = OpenAIBridge.StreamAccumulator(fun piece -> collected.Append piece |> ignore)
    let chunks =
        [ """{"model":"gpt-test","choices":[{"delta":{"role":"assistant","content":"Hel"}}]}"""
          """{"choices":[{"delta":{"content":"lo."}}]}"""
          """{"choices":[{"delta":{"tool_calls":[{"index":0,"id":"call_2","function":{"name":"read_file","arguments":"{\"pa"}}]}}]}"""
          """{"choices":[{"delta":{"tool_calls":[{"index":0,"function":{"arguments":"th\":\"b.txt\"}"}}]}}]}"""
          """{"choices":[{"delta":{},"finish_reason":"tool_calls"}]}"""
          """{"choices":[],"usage":{"prompt_tokens":9,"completion_tokens":7}}""" ]
    for chunk in chunks do
        accumulator.Apply(JsonNode.Parse(chunk).AsObject())
    let final = accumulator.Final()
    Assert.Equal("Hello.", collected.ToString())
    let content = final.["content"].AsArray()
    Assert.Equal("Hello.", content.[0].["text"].GetValue<string>())
    Assert.Equal("b.txt", content.[1].["input"].["path"].GetValue<string>())
    Assert.Equal("tool_use", final.["stop_reason"].GetValue<string>())
    Assert.Equal(9, final.["usage"].["input_tokens"].GetValue<int>())
    Assert.Equal(7, final.["usage"].["output_tokens"].GetValue<int>())

[<Fact>]
let ``anthropic stream events accumulate into the final message`` () =
    let collected = Text.StringBuilder()
    let accumulator = AnthropicBridge.StreamAccumulator(fun piece -> collected.Append piece |> ignore)
    let events =
        [ """{"type":"message_start","message":{"id":"msg_1","type":"message","role":"assistant","model":"claude-opus-5","content":[],"stop_reason":null,"usage":{"input_tokens":11,"output_tokens":1}}}"""
          """{"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}"""
          """{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"Hi "}}"""
          """{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"there"}}"""
          """{"type":"content_block_stop","index":0}"""
          """{"type":"content_block_start","index":1,"content_block":{"type":"tool_use","id":"toolu_1","name":"grep","input":{}}}"""
          """{"type":"content_block_delta","index":1,"delta":{"type":"input_json_delta","partial_json":"{\"pattern\":"}}"""
          """{"type":"content_block_delta","index":1,"delta":{"type":"input_json_delta","partial_json":"\"beta\"}"}}"""
          """{"type":"content_block_stop","index":1}"""
          """{"type":"message_delta","delta":{"stop_reason":"tool_use"},"usage":{"output_tokens":42}}"""
          """{"type":"message_stop"}""" ]
    for event in events do
        accumulator.Apply(JsonNode.Parse(event).AsObject())
    match accumulator.Final() with
    | Error e -> failwith e
    | Ok message ->
        Assert.Equal("Hi there", collected.ToString())
        let content = message.["content"].AsArray()
        Assert.Equal("Hi there", content.[0].["text"].GetValue<string>())
        Assert.Equal("beta", content.[1].["input"].["pattern"].GetValue<string>())
        Assert.Equal("tool_use", message.["stop_reason"].GetValue<string>())
        Assert.Equal(11, message.["usage"].["input_tokens"].GetValue<int>())
        Assert.Equal(42, message.["usage"].["output_tokens"].GetValue<int>())

[<Fact>]
let ``anthropic raw stream preserves content shapes outside typed SDK models`` () =
    let body =
        """event: message_start
data: {"type":"message_start","message":{"id":"msg_future","type":"message","role":"assistant","model":"claude-opus-5","content":[],"stop_reason":null,"usage":{"input_tokens":3,"output_tokens":1}}}

event: content_block_start
data: {"type":"content_block_start","index":0,"content_block":{"type":"tool_use","id":"toolu_future","name":"future_tool","input":"opaque"}}

event: content_block_stop
data: {"type":"content_block_stop","index":0}

event: ping
data: "ping"

event: message_delta
data: {"type":"message_delta","delta":{"stop_reason":"tool_use"},"usage":{"output_tokens":7}}

event: message_stop
data: {"type":"message_stop"}

"""
    use stream = new MemoryStream(Encoding.UTF8.GetBytes body)
    let message = AnthropicBridge.accumulateRawStream stream CancellationToken.None ignore
    let block = message.["content"].AsArray().[0]
    Assert.Equal("opaque", block.["input"].GetValue<string>())
    Assert.Equal("tool_use", message.["stop_reason"].GetValue<string>())

[<Fact>]
let ``anthropic raw stream surfaces provider error events`` () =
    let body = "event: error\ndata: {\"type\":\"error\",\"error\":{\"type\":\"overloaded_error\",\"message\":\"busy\"}}\n\n"
    use stream = new MemoryStream(Encoding.UTF8.GetBytes body)
    let error = Assert.ThrowsAny<Exception>(fun () -> AnthropicBridge.accumulateRawStream stream CancellationToken.None ignore |> ignore)
    Assert.Contains("overloaded_error: busy", error.Message)

// ── Routing and configuration ──────────────────────────────────────────────

[<Fact>]
let ``model specs route to providers`` () =
    let config = Providers.defaultConfig
    match Providers.resolve config "openai/gpt-5.2" with
    | Ok (p, bare) -> Assert.Equal("openai", p.name); Assert.Equal("gpt-5.2", bare)
    | Error e -> failwith e
    match Providers.resolve config "ollama/qwen3:8b" with
    | Ok (p, bare) -> Assert.Equal("ollama", p.name); Assert.Equal("qwen3:8b", bare)
    | Error e -> failwith e
    // Bare claude models default to anthropic.
    match Providers.resolve config "claude-opus-5" with
    | Ok (p, bare) -> Assert.Equal("anthropic", p.name); Assert.Equal("claude-opus-5", bare)
    | Error e -> failwith e
    // Anything else without a prefix is an error that names the fix.
    match Providers.resolve config "gpt-5.2" with
    | Ok _ -> failwith "expected an error"
    | Error message -> Assert.Contains("provider prefix", message)
    match Providers.resolve config "nosuch/model" with
    | Ok _ -> failwith "expected an error"
    | Error message -> Assert.Contains("unknown provider", message)

[<Fact>]
let ``workspace config adds aliases, providers, and a default`` () =
    let root = Path.Combine(Path.GetTempPath(), "jern-config-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory root |> ignore
    try
        File.WriteAllText(Path.Combine(root, "jern.json"),
            """{
              "default_model": "ollama/qwen3",
              "aliases": {"fast": "groq/llama-3.3-70b-versatile"},
              "providers": {"corp": {"base_url": "https://llm.corp/v1", "api_key_env": "CORP_KEY"}}
            }""")
        match Providers.load root with
        | Error e -> failwith e
        | Ok config ->
            Assert.Equal("ollama/qwen3", config.defaultModel)
            match Providers.resolve config "fast" with
            | Ok (p, bare) -> Assert.Equal("groq", p.name); Assert.Equal("llama-3.3-70b-versatile", bare)
            | Error e -> failwith e
            match Providers.resolve config "corp/internal-model" with
            | Ok (p, bare) ->
                Assert.Equal("https://llm.corp/v1", p.baseUrl)
                Assert.Equal(Some "CORP_KEY", p.apiKeyEnv)
                Assert.Equal("internal-model", bare)
            | Error e -> failwith e
    finally
        Directory.Delete(root, true)

[<Fact>]
let ``per-request model beats the cli model`` () =
    // Route to a bogus provider name so resolution itself is the observable.
    let mutable resolvedError = ""
    let bridge = Providers.createBridge Providers.defaultConfig (Some "nosuch/frommcli") None
    match bridge (Json.deserialize """{"model":"alsonosuch/fromrequest","messages":[]}""") with
    | Choice1Of2 (Default message) -> resolvedError <- message
    | _ -> failwith "expected a routing error"
    Assert.Contains("alsonosuch", resolvedError)

/// Regression for the first live run: the SDK client reads
/// ANTHROPIC_API_KEY in its constructor, so a client cached before the key
/// existed stayed unauthorized forever (keys arrive mid-process via the ui
/// settings panel). The bridge must rebuild the client when the key changes
/// and reuse it when it does not.
[<Fact>]
let ``the anthropic client is rebuilt when the key changes`` () =
    let previous = System.Environment.GetEnvironmentVariable "ANTHROPIC_API_KEY"
    try
        System.Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", "key-one")
        let first = AnthropicBridge.client ()
        System.Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", "key-two")
        let second = AnthropicBridge.client ()
        Assert.NotSame(first, second)
        Assert.Same(second, AnthropicBridge.client ())
    finally
        System.Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", previous)

[<Fact>]
let ``approval keys and memory behave`` () =
    Assert.Equal("edit_file", Approvals.key "edit_file: greeting.txt\n  - a\n  + b")
    // Shell keys on the command word: "always" for one command must not
    // whitelist every command the model invents later in the session.
    Assert.Equal("shell: pytest", Approvals.key "shell: pytest -q")
    Assert.Equal("shell: git", Approvals.key "shell: git status")
    Assert.Equal("shell", Approvals.key "shell: ")
    Assert.Equal("read_file", Approvals.key "read_file")
    let memory = Approvals.Memory(false)
    Assert.False(memory.Covers "shell: rm -rf /tmp/x")
    memory.RememberAlways "shell: ls"
    Assert.True(memory.Covers "shell: ls -la src")
    Assert.False(memory.Covers "shell: rm -rf /")
    Assert.False(memory.Covers "edit_file: f")
    memory.RememberAlways "edit_file: a.txt\n  - x\n  + y"
    Assert.True(memory.Covers "edit_file: b.txt\n  - p\n  + q")
    let auto = Approvals.Memory(true)
    Assert.True(auto.Covers "edit_file: whatever")
    auto.Auto <- false
    Assert.False(auto.Covers "edit_file: whatever")

[<Fact>]
let ``reasoning effort maps to chat completions and moves the token cap`` () =
    let canonical = JsonNode.Parse("""{"max_tokens":8192,"reasoning_effort":"high","messages":[]}""").AsObject()
    let out = OpenAIBridge.translateRequest "o4-mini" canonical
    Assert.Equal("high", out.["reasoning_effort"].GetValue<string>())
    Assert.Equal(8192, out.["max_completion_tokens"].GetValue<int>())
    Assert.Null(out.["max_tokens"])
    // Without effort, max_tokens stays put.
    let plain = JsonNode.Parse("""{"max_tokens":8192,"messages":[]}""").AsObject()
    let out2 = OpenAIBridge.translateRequest "gpt-4.1" plain
    Assert.Equal(8192, out2.["max_tokens"].GetValue<int>())
    Assert.Null(out2.["reasoning_effort"])

[<Fact>]
let ``reasoning content becomes a canonical thinking block`` () =
    let response = JsonNode.Parse("""{"model":"deepseek-reasoner","choices":[{"message":{"content":"answer","reasoning_content":"chain of thought"},"finish_reason":"stop"}]}""").AsObject()
    match OpenAIBridge.translateResponse response with
    | Error e -> failwith e
    | Ok canonical ->
        let content = canonical.["content"].AsArray()
        Assert.Equal("thinking", content.[0].AsObject().["type"].GetValue<string>())
        Assert.Equal("chain of thought", content.[0].AsObject().["thinking"].GetValue<string>())
        Assert.Equal("text", content.[1].AsObject().["type"].GetValue<string>())

[<Fact>]
let ``the anthropic body drops reasoning_effort and keeps thinking`` () =
    let request =
        Json.deserialize """{"max_tokens":9000,"reasoning_effort":"high","thinking":{"type":"enabled","budget_tokens":2048},"messages":[]}"""
    let body = AnthropicBridge.prepareBody None request
    Assert.False(body.ContainsKey "reasoning_effort")
    Assert.True(body.ContainsKey "thinking")
