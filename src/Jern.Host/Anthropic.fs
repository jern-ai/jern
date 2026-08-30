namespace Jern.Host

open System
open System.Collections.Generic
open System.IO
open System.Net.ServerSentEvents
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open IronKernel
open IronKernel.Ast
open IronKernel.Errors
open Anthropic
open Anthropic.Models.Messages

/// The LLM provider bridge: Anthropic Messages API, first provider.
///
/// The bridge is a pure passthrough. The agent authors the request as Kernel
/// data in the exact wire shape of the Messages API (`:model`, `:max_tokens`,
/// `:messages`, `:tools`, …); we convert it to JSON, hand it to the official
/// SDK as a raw body, and convert the raw wire response back without generated
/// response models narrowing its shape. Any field the API grows is immediately
/// expressible from agent source with no host change — the handler seam is the abstraction,
/// not a request-shape mapping layer.
module AnthropicBridge =

    /// The signature every provider (real, mock, or fixture) implements:
    /// request plist in, response plist or error out.
    type LlmBridge = LispVal -> ThrowsError<LispVal>

    let defaultModel = "claude-opus-5"
    let defaultMaxTokens = 16000L

    /// The SDK client reads ANTHROPIC_API_KEY once, in its constructor — so
    /// it is rebuilt whenever the variable changes. Keys can arrive
    /// mid-process (the jern ui settings panel, persisted credentials), and
    /// a client constructed before the key existed would otherwise stay
    /// unauthorized for the life of the process.
    let mutable private cachedClient : (string * AnthropicClient) option = None
    let private clientSync = obj ()

    let internal client () =
        let key =
            match Environment.GetEnvironmentVariable "ANTHROPIC_API_KEY" with
            | null -> ""
            | k -> k
        lock clientSync (fun () ->
            match cachedClient with
            | Some (cachedKey, c) when cachedKey = key -> c
            | _ ->
                let c = new AnthropicClient()
                cachedClient <- Some(key, c)
                c)

    /// Ctrl-C surfaces as Interrupted (possibly wrapped by the task machinery).
    let internal isInterrupt (e: exn) =
        match e with
        | Interrupted -> true
        | :? AggregateException as a when not (isNull a.InnerException) ->
            match a.InnerException with
            | Interrupted -> true
            | _ -> false
        | _ -> false

    /// A cancelled HTTP call (possibly wrapped by the task machinery) — how
    /// an interrupt lands when it cancels an in-flight provider request.
    let internal isCanceled (e: exn) =
        match e with
        | :? OperationCanceledException -> true
        | :? AggregateException as a -> (a.InnerException :? OperationCanceledException)
        | _ -> false

    /// A token that cancels when `interrupted` flips true (polled every
    /// 100 ms), so /interrupt and Ctrl-C land even on a non-streaming call
    /// or before the first streamed token, instead of only inside the text
    /// callback. Dispose the returned watcher when the call finishes.
    let internal interruptTokenSource (interrupted: unit -> bool) =
        let cts = new System.Threading.CancellationTokenSource()
        let timer =
            new System.Threading.Timer(
                (fun _ -> if interrupted () then (try cts.Cancel() with _ -> ())),
                null, 100, 100)
        cts, (timer :> IDisposable)

    /// Accumulates Messages API stream events (wire JSON) into the final
    /// message, emitting text deltas as they arrive. Pure JSON-to-JSON, so
    /// it is unit-testable without the SDK or the network.
    type StreamAccumulator(onText: string -> unit) =
        let mutable message: JsonObject = null
        let partialJson = Dictionary<int, StringBuilder>()

        member private _.Content = message.["content"].AsArray()

        member this.Apply(event: JsonObject) =
            match event.["type"] |> Option.ofObj |> Option.map (fun t -> t.GetValue<string>()) with
            | Some "message_start" ->
                message <- event.["message"].DeepClone().AsObject()
                if isNull message.["content"] then message.["content"] <- JsonArray()
            | Some "content_block_start" ->
                let index = event.["index"].GetValue<int>()
                let block = event.["content_block"].DeepClone()
                let content = this.Content
                while content.Count <= index do content.Add(null: JsonNode)
                content.[index] <- block
            | Some "content_block_delta" ->
                let index = event.["index"].GetValue<int>()
                let block = this.Content.[index].AsObject()
                let delta = event.["delta"].AsObject()
                let append (field: string) (piece: string) =
                    let existing =
                        match block.[field] with
                        | null -> ""
                        | node -> node.GetValue<string>()
                    block.[field] <- JsonValue.Create(existing + piece)
                match delta.["type"].GetValue<string>() with
                | "text_delta" ->
                    let piece = delta.["text"].GetValue<string>()
                    append "text" piece
                    onText piece
                | "input_json_delta" ->
                    let buffer =
                        match partialJson.TryGetValue index with
                        | true, b -> b
                        | _ ->
                            let b = StringBuilder()
                            partialJson.[index] <- b
                            b
                    buffer.Append(delta.["partial_json"].GetValue<string>()) |> ignore
                | "thinking_delta" -> append "thinking" (delta.["thinking"].GetValue<string>())
                | "signature_delta" -> append "signature" (delta.["signature"].GetValue<string>())
                | _ -> ()
            | Some "content_block_stop" ->
                let index = event.["index"].GetValue<int>()
                match partialJson.TryGetValue index with
                | true, buffer ->
                    let text = buffer.ToString()
                    let input = if text.Trim() = "" then "{}" else text
                    this.Content.[index].AsObject().["input"] <- JsonNode.Parse input
                    partialJson.Remove index |> ignore
                | _ -> ()
            | Some "message_delta" ->
                match event.["delta"] with
                | :? JsonObject as delta ->
                    for kv in delta do
                        message.[kv.Key] <- (if isNull kv.Value then null else kv.Value.DeepClone())
                | _ -> ()
                match event.["usage"] with
                | :? JsonObject as usage ->
                    let target =
                        match message.["usage"] with
                        | :? JsonObject as u -> u
                        | _ ->
                            let u = JsonObject()
                            message.["usage"] <- u
                            u
                    for kv in usage do
                        target.[kv.Key] <- (if isNull kv.Value then null else kv.Value.DeepClone())
                | _ -> ()
            | _ -> ()

        member _.Final() : Result<JsonObject, string> =
            if isNull message then Error "stream produced no message_start event"
            else Ok message

    /// Fill :model and :max_tokens when the agent omitted them; an explicit
    /// `model` argument (from provider routing) overrides the request's.
    let internal prepareBody (model: string option) (request: LispVal) =
        let bodyJson = Json.serialize request
        let body = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(bodyJson)
        match model with
        | Some m -> body.["model"] <- JsonSerializer.SerializeToElement(m)
        | None ->
            if not (body.ContainsKey "model") then
                body.["model"] <- JsonSerializer.SerializeToElement(defaultModel)
        if not (body.ContainsKey "max_tokens") then
            body.["max_tokens"] <- JsonSerializer.SerializeToElement(defaultMaxTokens)
        // :reasoning_effort belongs to the OpenAI-compatible bridge; the
        // Messages API rejects unknown top-level fields. (:thinking is
        // native here and passes through untouched.)
        body.Remove "reasoning_effort" |> ignore
        body

    let private toParams (body: Dictionary<string, JsonElement>) =
        let empty = Dictionary<string, JsonElement>()
        MessageCreateParams.FromRawUnchecked(empty, empty, body)

    let private complete (parameters: MessageCreateParams) (ct: System.Threading.CancellationToken) =
        let response =
            (client ()).Messages.WithRawResponse.Create(parameters, ct)
            |> Async.AwaitTask
            |> Async.RunSynchronously
        use response = response
        response.ReadAsString(ct)
        |> Async.AwaitTask
        |> Async.RunSynchronously
        |> JsonNode.Parse

    let internal accumulateRawStream (stream: Stream) (ct: System.Threading.CancellationToken)
                                     (onText: string -> unit) =
        let accumulator = StreamAccumulator(onText)
        let consume =
            task {
                let events = SseParser.Create(stream).EnumerateAsync(ct)
                let enumerator = events.GetAsyncEnumerator(ct)
                try
                    let mutable go = true
                    while go do
                        let! hasNext = enumerator.MoveNextAsync().AsTask()
                        if hasNext then
                            let event = JsonNode.Parse(enumerator.Current.Data).AsObject()
                            match event.["type"] |> Option.ofObj |> Option.map _.GetValue<string>() with
                            | Some "error" ->
                                let providerError =
                                    match event.["error"] with
                                    | :? JsonObject as error ->
                                        [ "type"; "message" ]
                                        |> List.choose (fun field ->
                                            match error.[field] with
                                            | :? JsonValue as value when value.GetValueKind() = JsonValueKind.String ->
                                                Some(value.GetValue<string>())
                                            | _ -> None)
                                        |> String.concat ": "
                                    | _ -> ""
                                failwith
                                    (if providerError = "" then "Anthropic stream returned an error event"
                                     else "Anthropic stream error: " + providerError)
                            | _ -> accumulator.Apply event
                        else
                            go <- false
                finally
                    enumerator.DisposeAsync().AsTask().GetAwaiter().GetResult()
            }
        consume.GetAwaiter().GetResult()
        match accumulator.Final() with
        | Ok message -> message
        | Error error -> failwith error

    let private streamCompletion (parameters: MessageCreateParams)
                                 (ct: System.Threading.CancellationToken) (onText: string -> unit) =
        let response =
            (client ()).Messages.WithRawResponse.CreateStreaming(parameters, ct)
            |> Async.AwaitTask
            |> Async.RunSynchronously
        use response = response
        use stream = response.ReadAsStream(ct) |> Async.AwaitTask |> Async.RunSynchronously
        accumulateRawStream stream ct onText :> JsonNode

    /// The bridge, parameterized by provider routing (`model` override), an
    /// optional live-text callback, and an interrupt probe. With a callback
    /// the response streams; if streaming fails, we fall back to a plain call
    /// and deliver the full text through the callback once. `interrupted`
    /// cancels the in-flight HTTP call, so Ctrl-C and /interrupt land even
    /// on non-streaming turns and before the first token. Transient provider
    /// failures are retried inside the SDK itself (AnthropicClient.MaxRetries).
    let callWithInterrupt (model: string option) (onText: (string -> unit) option)
                          (interrupted: unit -> bool) : LlmBridge =
        fun request ->
            let cts, watcher = interruptTokenSource interrupted
            use _cts = cts
            use _watcher = watcher
            try
                let parameters = toParams (prepareBody model request)
                let responseNode =
                    match onText with
                    | None -> complete parameters cts.Token
                    | Some emit ->
                        try
                            streamCompletion parameters cts.Token emit
                        with
                        | e when isInterrupt e -> raise Interrupted
                        | e when isCanceled e && interrupted () -> raise Interrupted
                        | _ ->
                            let node = complete (toParams (prepareBody model request)) cts.Token
                            match node.["content"] with
                            | :? JsonArray as blocks ->
                                for block in blocks do
                                    match block with
                                    | :? JsonObject as b when
                                        not (isNull b.["type"])
                                        && b.["type"].GetValue<string>() = "text" ->
                                        emit (b.["text"].GetValue<string>())
                                    | _ -> ()
                            | _ -> ()
                            node
                Choice2Of2 (Json.toLispVal responseNode)
            with
            | e when isInterrupt e ->
                Choice1Of2 (Default "llm-call interrupted by user")
            | e when isCanceled e && interrupted () ->
                Choice1Of2 (Default "llm-call interrupted by user")
            | :? AggregateException as ex when ex.InnerException <> null ->
                Choice1Of2 (Default("llm-call failed: " + ex.InnerException.Message))
            | ex ->
                Choice1Of2 (Default("llm-call failed: " + ex.Message))

    /// Bridge without an interrupt probe (scripts, tests).
    let callWith (model: string option) (onText: (string -> unit) option) : LlmBridge =
        callWithInterrupt model onText (fun () -> false)

    /// Plain non-streaming bridge with the built-in default model.
    let call: LlmBridge = callWith None None
