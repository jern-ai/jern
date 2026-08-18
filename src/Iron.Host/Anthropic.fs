namespace Iron.Host

open System
open System.Collections.Generic
open System.Text.Json
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

    let private clientLazy = lazy (new AnthropicClient())

    /// Fill :model and :max_tokens when the agent omitted them.
    let private withDefaults (body: Dictionary<string, JsonElement>) =
        if not (body.ContainsKey "model") then
            body.["model"] <- JsonSerializer.SerializeToElement(defaultModel)
        if not (body.ContainsKey "max_tokens") then
            body.["max_tokens"] <- JsonSerializer.SerializeToElement(defaultMaxTokens)
        body

    let call: LlmBridge =
        fun request ->
            try
                let bodyJson = Json.serialize request
                let body =
                    JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(bodyJson)
                    |> withDefaults
                let empty = Dictionary<string, JsonElement>()
                let parameters = MessageCreateParams.FromRawUnchecked(empty, empty, body)
                let message =
                    clientLazy.Value.Messages.Create(parameters)
                    |> Async.AwaitTask
                    |> Async.RunSynchronously
                Choice2Of2 (Json.deserialize (JsonSerializer.Serialize(message)))
            with
            | :? AggregateException as ex when ex.InnerException <> null ->
                Choice1Of2 (Default("llm-call failed: " + ex.InnerException.Message))
            | ex ->
                Choice1Of2 (Default("llm-call failed: " + ex.Message))
