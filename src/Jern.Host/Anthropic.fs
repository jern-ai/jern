namespace Jern.Host

open System
open System.Collections.Generic
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
/// SDK as a raw body (typed views still validate it), and convert the wire
/// response back. Any field the API grows is immediately expressible from
/// agent source with no host change — the handler seam is the abstraction,
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
    let private prepareBody (model: string option) (request: LispVal) =
        let bodyJson = Json.serialize request
        let body = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(bodyJson)
        match model with
        | Some m -> body.["model"] <- JsonSerializer.SerializeToElement(m)
        | None ->
            if not (body.ContainsKey "model") then
                body.["model"] <- JsonSerializer.SerializeToElement(defaultModel)
        if not (body.ContainsKey "max_tokens") then
            body.["max_tokens"] <- JsonSerializer.SerializeToElement(defaultMaxTokens)
        body

    let private toParams (body: Dictionary<string, JsonElement>) =
        let empty = Dictionary<string, JsonElement>()
        MessageCreateParams.FromRawUnchecked(empty, empty, body)

    let private complete (parameters: MessageCreateParams) =
        (client ()).Messages.Create(parameters)
        |> Async.AwaitTask
        |> Async.RunSynchronously
        |> fun message -> JsonNode.Parse(JsonSerializer.Serialize(message))

    let private streamCompletion (parameters: MessageCreateParams) (onText: string -> unit) =
        let accumulator = StreamAccumulator(onText)
        let events = (client ()).Messages.CreateStreaming(parameters)
        let consume =
            task {
                let enumerator = events.GetAsyncEnumerator()
                try
                    let mutable go = true
                    while go do
                        let! hasNext = enumerator.MoveNextAsync().AsTask()
                        if hasNext then
                            accumulator.Apply(JsonNode.Parse(JsonSerializer.Serialize(enumerator.Current)).AsObject())
                        else
                            go <- false
                finally
                    enumerator.DisposeAsync().AsTask().Wait()
            }
        consume.GetAwaiter().GetResult()
        match accumulator.Final() with
        | Ok message -> message :> JsonNode
        | Error e -> failwith e

    /// The bridge, parameterized by provider routing (`model` override) and
    /// an optional live-text callback. With a callback the response streams;
    /// if streaming fails, we fall back to a plain call and deliver the full
    /// text through the callback once.
    let callWith (model: string option) (onText: (string -> unit) option) : LlmBridge =
        fun request ->
            try
                let parameters = toParams (prepareBody model request)
                let responseNode =
                    match onText with
                    | None -> complete parameters
                    | Some emit ->
                        try
                            streamCompletion parameters emit
                        with
                        | e when isInterrupt e -> raise Interrupted
                        | _ ->
                            let node = complete (toParams (prepareBody model request))
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
            | :? AggregateException as ex when ex.InnerException <> null ->
                Choice1Of2 (Default("llm-call failed: " + ex.InnerException.Message))
            | ex ->
                Choice1Of2 (Default("llm-call failed: " + ex.Message))

    /// Plain non-streaming bridge with the built-in default model.
    let call: LlmBridge = callWith None None
