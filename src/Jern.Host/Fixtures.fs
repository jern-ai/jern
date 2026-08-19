namespace Jern.Host

open System.Collections.Generic
open System.IO
open System.Text.Json
open System.Text.Json.Nodes
open IronKernel
open IronKernel.Ast
open IronKernel.Errors

/// Deterministic LLM fixtures — the machinery behind `jern test`.
///
/// A fixture file is the recorded transcript of a test's LLM traffic:
/// `{"exchanges": [{"request": …, "response": …}, …]}` in wire format.
///
/// - **Record**: the bridge delegates to an upstream provider and captures
///   every exchange; the file is written when the test finishes.
/// - **Replay**: the bridge answers from the file, in order, and *fails on
///   any divergence* — a request that no longer matches its recording byte
///   for byte (canonical JSON) is a behavior change, which is exactly what
///   the test is there to catch. Re-record deliberate changes.
module Fixtures =

    type Mode =
        | Record of upstream: AnthropicBridge.LlmBridge
        | Replay

    type private Active =
        | Recording of path: string * captured: ResizeArray<JsonObject>
        | Replaying of path: string * queue: Queue<JsonObject>

    type FixtureBridge(mode: Mode, baseDir: string) =
        let mutable active: Active option = None

        let canonical (value: LispVal) = Json.serialize value

        member _.Use(relativePath: string) : ThrowsError<unit> =
            let path = Path.Combine(baseDir, relativePath)
            match mode with
            | Record _ ->
                Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
                active <- Some(Recording(path, ResizeArray()))
                Choice2Of2 ()
            | Replay ->
                if not (File.Exists path) then
                    Choice1Of2 (Default(sprintf "fixture '%s' not found — run `jern test --record` to create it" relativePath))
                else
                    try
                        let doc = JsonNode.Parse(File.ReadAllText path).AsObject()
                        let queue = Queue<JsonObject>()
                        for exchange in doc.["exchanges"].AsArray() do
                            queue.Enqueue(exchange.AsObject())
                        active <- Some(Replaying(path, queue))
                        Choice2Of2 ()
                    with ex ->
                        Choice1Of2 (Default(sprintf "fixture '%s' is unreadable: %s" relativePath ex.Message))

        /// The LLM bridge for the test session.
        member _.Bridge: AnthropicBridge.LlmBridge =
            fun request ->
                match active, mode with
                | Some (Recording (_, captured)), Record upstream ->
                    match upstream request with
                    | Choice1Of2 error -> Choice1Of2 error
                    | Choice2Of2 response ->
                        let exchange = JsonObject()
                        exchange.["request"] <- JsonNode.Parse(canonical request)
                        exchange.["response"] <- JsonNode.Parse(canonical response)
                        captured.Add exchange
                        Choice2Of2 response
                | Some (Replaying (path, queue)), _ ->
                    if queue.Count = 0 then
                        Choice1Of2 (Default(sprintf "fixture '%s' is exhausted: the agent made more llm calls than were recorded" path))
                    else
                        let exchange = queue.Dequeue()
                        let recorded = exchange.["request"].ToJsonString()
                        let actual = JsonNode.Parse(canonical request).ToJsonString()
                        if recorded <> actual then
                            // Point at the first divergence with a little
                            // context, rather than dumping both requests.
                            let firstDiff =
                                Seq.zip recorded actual
                                |> Seq.tryFindIndex (fun (a, b) -> a <> b)
                                |> Option.defaultValue (min recorded.Length actual.Length)
                            let excerpt (s: string) =
                                let from = max 0 (firstDiff - 60)
                                let piece = s.Substring(from, min 160 (s.Length - from))
                                (if from > 0 then "…" else "") + piece
                                + (if from + 160 < s.Length then "…" else "")
                            Choice1Of2 (Default(
                                "llm request diverged from its recording — behavior changed; "
                                + "re-record if intended. First difference:\n  recorded: "
                                + excerpt recorded + "\n  actual:   " + excerpt actual))
                        else
                            Choice2Of2 (Json.toLispVal exchange.["response"])
                | _ ->
                    Choice1Of2 (Default "llm-call outside with-fixtures: tests must wrap llm traffic in (with-fixtures \"file.json\" …)")

        /// Called by the runner when the test body finished; writes the
        /// recording (only on success) and reports leftover replay entries.
        member _.Finish(succeeded: bool) : Result<unit, string> =
            let outcome =
                match active with
                | Some (Recording (path, captured)) when succeeded ->
                    let doc = JsonObject()
                    let array = JsonArray()
                    captured |> Seq.iter (fun e -> array.Add(e))
                    doc.["exchanges"] <- array
                    File.WriteAllText(path, doc.ToJsonString(JsonSerializerOptions(WriteIndented = true)) + "\n")
                    Ok ()
                | Some (Replaying (path, queue)) when succeeded && queue.Count > 0 ->
                    Error(sprintf "fixture '%s' has %d unused exchange(s): the agent made fewer llm calls than were recorded" path queue.Count)
                | _ -> Ok ()
            active <- None
            outcome
