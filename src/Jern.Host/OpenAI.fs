namespace Jern.Host

open System
open System.Collections.Generic
open System.IO
open System.Net.Http
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open IronKernel.Ast
open IronKernel.Errors

/// The OpenAI-compatible bridge (Chat Completions wire format).
///
/// One translation layer covers OpenAI, Ollama, OpenRouter, DeepSeek, Groq,
/// Mistral, xAI, LM Studio, and Gemini's compatibility endpoint. jern's
/// canonical conversation format stays the Anthropic-shaped Kernel data
/// frozen in M1 — sessions, traces, and fixtures are provider-independent —
/// and this module converts at the boundary:
///
///   canonical                      openai
///   :system "…"                 →  messages[0] = {role:"system"}
///   assistant tool_use blocks   →  assistant.tool_calls (arguments as JSON string)
///   user tool_result blocks     →  one {role:"tool", tool_call_id} each
///   :tools [{input_schema}]     →  tools [{type:"function", function:{parameters}}]
///   finish_reason stop/tool_calls/length ← stop_reason end_turn/tool_use/max_tokens
module OpenAIBridge =

    let private str (s: string) : JsonNode = JsonValue.Create s

    let private contentText (content: JsonNode) =
        // Canonical content is a string or an array of blocks; collect text.
        match content with
        | :? JsonValue as v when v.GetValueKind() = JsonValueKind.String -> v.GetValue<string>()
        | :? JsonArray as blocks ->
            blocks
            |> Seq.choose (fun b ->
                match b with
                | :? JsonObject as o when
                    not (isNull o.["type"]) && o.["type"].GetValue<string>() = "text" ->
                    Some(o.["text"].GetValue<string>())
                | _ -> None)
            |> String.concat "\n"
        | _ -> ""

    /// Canonical request (Anthropic shape) -> Chat Completions request.
    let translateRequest (model: string) (canonical: JsonObject) : JsonObject =
        let out = JsonObject()
        out.["model"] <- str model
        match canonical.["max_tokens"] with
        | null -> ()
        | tokens -> out.["max_tokens"] <- tokens.DeepClone()

        // Reasoning models: canonical :reasoning_effort passes through to
        // Chat Completions; the Anthropic-shaped :thinking block does not
        // exist here and is dropped. Models that take reasoning_effort
        // (o-series and friends) reject max_tokens in favor of
        // max_completion_tokens, so the token cap moves with it.
        match canonical.["reasoning_effort"] with
        | null -> ()
        | effort ->
            out.["reasoning_effort"] <- effort.DeepClone()
            match out.["max_tokens"] with
            | null -> ()
            | tokens ->
                out.["max_completion_tokens"] <- tokens.DeepClone()
                out.Remove "max_tokens" |> ignore

        let messages = JsonArray()
        match canonical.["system"] with
        | null -> ()
        | system ->
            let text = contentText system
            if text <> "" then
                let m = JsonObject()
                m.["role"] <- str "system"
                m.["content"] <- str text
                messages.Add m

        match canonical.["messages"] with
        | :? JsonArray as canonicalMessages ->
            for message in canonicalMessages do
                let message = message.AsObject()
                let role = message.["role"].GetValue<string>()
                match message.["content"] with
                | :? JsonValue as v when v.GetValueKind() = JsonValueKind.String ->
                    let m = JsonObject()
                    m.["role"] <- str role
                    m.["content"] <- str (v.GetValue<string>())
                    messages.Add m
                | :? JsonArray as blocks when role = "assistant" ->
                    let m = JsonObject()
                    m.["role"] <- str "assistant"
                    let text = contentText blocks
                    m.["content"] <- (if text = "" then null else str text)
                    let toolCalls = JsonArray()
                    for block in blocks do
                        let block = block.AsObject()
                        if block.["type"].GetValue<string>() = "tool_use" then
                            let call = JsonObject()
                            call.["id"] <- block.["id"].DeepClone()
                            call.["type"] <- str "function"
                            let f = JsonObject()
                            f.["name"] <- block.["name"].DeepClone()
                            f.["arguments"] <- str (block.["input"].ToJsonString())
                            call.["function"] <- f
                            toolCalls.Add call
                    if toolCalls.Count > 0 then m.["tool_calls"] <- toolCalls
                    messages.Add m
                | :? JsonArray as blocks ->
                    // User turn: tool_result blocks become role:"tool" messages;
                    // any text blocks stay a user message.
                    for block in blocks do
                        let block = block.AsObject()
                        if block.["type"].GetValue<string>() = "tool_result" then
                            let m = JsonObject()
                            m.["role"] <- str "tool"
                            m.["tool_call_id"] <- block.["tool_use_id"].DeepClone()
                            m.["content"] <- str (contentText block.["content"])
                            messages.Add m
                    let text =
                        blocks
                        |> Seq.map (fun b -> b.AsObject())
                        |> Seq.filter (fun b -> b.["type"].GetValue<string>() = "text")
                        |> Seq.map (fun b -> b.["text"].GetValue<string>())
                        |> String.concat "\n"
                    if text <> "" then
                        let m = JsonObject()
                        m.["role"] <- str role
                        m.["content"] <- str text
                        messages.Add m
                | _ -> ()
        | _ -> ()
        out.["messages"] <- messages

        match canonical.["tools"] with
        | :? JsonArray as tools when tools.Count > 0 ->
            let outTools = JsonArray()
            for tool in tools do
                let tool = tool.AsObject()
                let f = JsonObject()
                f.["name"] <- tool.["name"].DeepClone()
                match tool.["description"] with
                | null -> ()
                | d -> f.["description"] <- d.DeepClone()
                match tool.["input_schema"] with
                | null -> ()
                | s -> f.["parameters"] <- s.DeepClone()
                let entry = JsonObject()
                entry.["type"] <- str "function"
                entry.["function"] <- f
                outTools.Add entry
            out.["tools"] <- outTools
        | _ -> ()
        out

    /// Build a canonical response from the pieces a completion (streamed or
    /// not) yields.
    let private buildCanonical (model: string) (reasoning: string) (text: string)
                               (toolCalls: (string * string * string) list)
                               (finishReason: string option) (usage: JsonObject option) : JsonObject =
        let response = JsonObject()
        response.["role"] <- str "assistant"
        response.["model"] <- str model
        let content = JsonArray()
        // DeepSeek-style reasoning_content becomes a canonical thinking
        // block, first, mirroring the Anthropic shape.
        if reasoning <> "" then
            let block = JsonObject()
            block.["type"] <- str "thinking"
            block.["thinking"] <- str reasoning
            content.Add block
        if text <> "" then
            let block = JsonObject()
            block.["type"] <- str "text"
            block.["text"] <- str text
            content.Add block
        for (id, name, arguments) in toolCalls do
            let block = JsonObject()
            block.["type"] <- str "tool_use"
            block.["id"] <- str id
            block.["name"] <- str name
            block.["input"] <-
                (try JsonNode.Parse(if arguments.Trim() = "" then "{}" else arguments)
                 with _ -> JsonNode.Parse "{}")
            content.Add block
        response.["content"] <- content
        response.["stop_reason"] <-
            str (match finishReason with
                 | Some "tool_calls" -> "tool_use"
                 | Some "length" -> "max_tokens"
                 | _ when not toolCalls.IsEmpty -> "tool_use"
                 | _ -> "end_turn")
        match usage with
        | Some u ->
            let usageOut = JsonObject()
            match u.["prompt_tokens"] with
            | null -> ()
            | t -> usageOut.["input_tokens"] <- t.DeepClone()
            match u.["completion_tokens"] with
            | null -> ()
            | t -> usageOut.["output_tokens"] <- t.DeepClone()
            response.["usage"] <- usageOut
        | None -> ()
        response

    /// Chat Completions response -> canonical.
    let translateResponse (openai: JsonObject) : Result<JsonObject, string> =
        match openai.["choices"] with
        | :? JsonArray as choices when choices.Count > 0 ->
            let choice = choices.[0].AsObject()
            let message = choice.["message"].AsObject()
            let text =
                match message.["content"] with
                | null -> ""
                | c when c.GetValueKind() = JsonValueKind.String -> c.GetValue<string>()
                | _ -> ""
            let reasoning =
                match message.["reasoning_content"], message.["reasoning"] with
                | (:? JsonValue as r), _ when r.GetValueKind() = JsonValueKind.String -> r.GetValue<string>()
                | _, (:? JsonValue as r) when r.GetValueKind() = JsonValueKind.String -> r.GetValue<string>()
                | _ -> ""
            let toolCalls =
                match message.["tool_calls"] with
                | :? JsonArray as calls ->
                    [ for call in calls do
                        let call = call.AsObject()
                        let f = call.["function"].AsObject()
                        yield call.["id"].GetValue<string>(),
                              f.["name"].GetValue<string>(),
                              (match f.["arguments"] with null -> "{}" | a -> a.GetValue<string>()) ]
                | _ -> []
            let finish =
                match choice.["finish_reason"] with
                | null -> None
                | f -> Some(f.GetValue<string>())
            let usage =
                match openai.["usage"] with
                | :? JsonObject as u -> Some u
                | _ -> None
            let model =
                match openai.["model"] with
                | null -> ""
                | m -> m.GetValue<string>()
            Ok(buildCanonical model reasoning text toolCalls finish usage)
        | _ ->
            Error("provider returned no choices: " + openai.ToJsonString())

    /// Accumulates Chat Completions stream chunks into the same pieces.
    type StreamAccumulator(onText: string -> unit) =
        let text = StringBuilder()
        let reasoning = StringBuilder()
        let calls = SortedDictionary<int, string * string * StringBuilder>()
        let mutable finishReason: string option = None
        let mutable usage: JsonObject option = None
        let mutable model = ""

        member _.Apply(chunk: JsonObject) =
            match chunk.["model"] with
            | null -> ()
            | m -> model <- m.GetValue<string>()
            match chunk.["usage"] with
            | :? JsonObject as u -> usage <- Some(u.DeepClone().AsObject())
            | _ -> ()
            match chunk.["choices"] with
            | :? JsonArray as choices when choices.Count > 0 ->
                let choice = choices.[0].AsObject()
                match choice.["finish_reason"] with
                | null -> ()
                | f when f.GetValueKind() = JsonValueKind.String ->
                    finishReason <- Some(f.GetValue<string>())
                | _ -> ()
                match choice.["delta"] with
                | :? JsonObject as delta ->
                    match delta.["content"] with
                    | null -> ()
                    | c when c.GetValueKind() = JsonValueKind.String ->
                        let piece = c.GetValue<string>()
                        text.Append piece |> ignore
                        onText piece
                    | _ -> ()
                    match delta.["reasoning_content"], delta.["reasoning"] with
                    | (:? JsonValue as r), _ when r.GetValueKind() = JsonValueKind.String ->
                        reasoning.Append(r.GetValue<string>()) |> ignore
                    | _, (:? JsonValue as r) when r.GetValueKind() = JsonValueKind.String ->
                        reasoning.Append(r.GetValue<string>()) |> ignore
                    | _ -> ()
                    match delta.["tool_calls"] with
                    | :? JsonArray as deltaCalls ->
                        for deltaCall in deltaCalls do
                            let deltaCall = deltaCall.AsObject()
                            let index =
                                match deltaCall.["index"] with
                                | null -> 0
                                | i -> i.GetValue<int>()
                            let id0, name0, arguments =
                                match calls.TryGetValue index with
                                | true, entry -> entry
                                | _ ->
                                    let entry = "", "", StringBuilder()
                                    calls.[index] <- entry
                                    entry
                            let id =
                                match deltaCall.["id"] with
                                | null -> id0
                                | i -> i.GetValue<string>()
                            let name =
                                match deltaCall.["function"] with
                                | :? JsonObject as f ->
                                    (match f.["name"] with
                                     | null -> name0
                                     | n -> name0 + n.GetValue<string>())
                                | _ -> name0
                            match deltaCall.["function"] with
                            | :? JsonObject as f ->
                                match f.["arguments"] with
                                | null -> ()
                                | a -> arguments.Append(a.GetValue<string>()) |> ignore
                            | _ -> ()
                            calls.[index] <- (id, name, arguments)
                    | _ -> ()
                | _ -> ()
            | _ -> ()

        member _.Final() : JsonObject =
            let toolCalls =
                [ for kv in calls -> let (id, name, arguments) = kv.Value in id, name, arguments.ToString() ]
            buildCanonical model (reasoning.ToString()) (text.ToString()) toolCalls finishReason usage

    // ── HTTP ────────────────────────────────────────────────────────────────

    let private http = lazy (new HttpClient(Timeout = TimeSpan.FromMinutes 10.0))

    let private newRequest (baseUrl: string) (apiKey: string option) (body: JsonObject) =
        let request = new HttpRequestMessage(HttpMethod.Post, baseUrl.TrimEnd('/') + "/chat/completions")
        match apiKey with
        | Some key -> request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + key) |> ignore
        | None -> ()
        request.Content <- new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
        request

    let private completeOnce (baseUrl: string) (apiKey: string option) (body: JsonObject) : Result<JsonObject, string> =
        use request = newRequest baseUrl apiKey body
        use response = http.Value.Send(request)
        let payload = response.Content.ReadAsStringAsync().Result
        if not response.IsSuccessStatusCode then
            Error(sprintf "%s: %s" (string response.StatusCode) payload)
        else
            try Ok(JsonNode.Parse(payload).AsObject())
            with ex -> Error("unparseable provider response: " + ex.Message)

    let private streamOnce (baseUrl: string) (apiKey: string option) (body: JsonObject)
                           (onText: string -> unit) : Result<JsonObject, string> =
        let body = body.DeepClone().AsObject()
        body.["stream"] <- JsonValue.Create true
        let streamOptions = JsonObject()
        streamOptions.["include_usage"] <- JsonValue.Create true
        body.["stream_options"] <- streamOptions
        use request = newRequest baseUrl apiKey body
        use response = http.Value.Send(request, HttpCompletionOption.ResponseHeadersRead)
        if not response.IsSuccessStatusCode then
            let payload = response.Content.ReadAsStringAsync().Result
            Error(sprintf "%s: %s" (string response.StatusCode) payload)
        else
            let accumulator = StreamAccumulator(onText)
            use reader = new StreamReader(response.Content.ReadAsStream())
            let mutable running = true
            while running do
                match reader.ReadLine() with
                | null -> running <- false
                | line when line.StartsWith "data: " ->
                    let payload = line.Substring(6).Trim()
                    if payload = "[DONE]" then running <- false
                    elif payload <> "" then
                        try accumulator.Apply(JsonNode.Parse(payload).AsObject())
                        with
                        | e when AnthropicBridge.isInterrupt e -> raise Interrupted
                        | _ -> ()
                | _ -> ()
            Ok(accumulator.Final())

    /// The bridge for one provider endpoint. With a callback the response
    /// streams; a provider that rejects the streaming request is retried
    /// once without it (some compatible servers lack stream_options).
    let call (baseUrl: string) (apiKeyEnv: string option) (model: string)
             (onText: (string -> unit) option) : AnthropicBridge.LlmBridge =
        fun request ->
            try
                let apiKey =
                    match apiKeyEnv with
                    | None -> None
                    | Some env ->
                        match Environment.GetEnvironmentVariable env with
                        | null | "" ->
                            failwithf "environment variable %s is not set (needed for %s)" env baseUrl
                        | key -> Some key
                let body = translateRequest model (JsonNode.Parse(Json.serialize request).AsObject())
                let outcome =
                    match onText with
                    | None -> completeOnce baseUrl apiKey body |> Result.bind translateResponse
                    | Some emit ->
                        match streamOnce baseUrl apiKey body emit with
                        | Ok canonical -> Ok canonical
                        | Error _ ->
                            completeOnce baseUrl apiKey body
                            |> Result.bind translateResponse
                            |> Result.map (fun canonical ->
                                match canonical.["content"] with
                                | :? JsonArray as blocks ->
                                    for block in blocks do
                                        let block = block.AsObject()
                                        if block.["type"].GetValue<string>() = "text" then
                                            emit (block.["text"].GetValue<string>())
                                | _ -> ()
                                canonical)
                match outcome with
                | Ok canonical -> Choice2Of2 (Json.toLispVal canonical)
                | Error message -> Choice1Of2 (Default("llm-call failed: " + message))
            with
            | e when AnthropicBridge.isInterrupt e ->
                Choice1Of2 (Default "llm-call interrupted by user")
            | ex ->
                Choice1Of2 (Default("llm-call failed: " + ex.Message))
