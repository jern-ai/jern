namespace Jern.Host

open System
open System.Text.Json.Nodes
open IronKernel.Ast

/// The trace, as a versioned *run record*.
///
/// Every effect already lands in `.jern/trace-*.jsonl` at the one choke point
/// that enforces policy. This module adds the envelope that turns a stream of
/// effects into a run you can summarize later without guessing: a
/// `run-started` event carrying the schema version and everything the run was
/// configured with, and exactly one `run-finished` event carrying how it
/// ended. A trace that stops mid-run is still valid — it simply has no
/// `run-finished`, which a reader reports as incomplete rather than
/// inventing an outcome (see Receipt).
module Trace =

    /// Bumped only for a *breaking* change to event shapes. Readers ignore
    /// unknown event names (forward compatibility is the normal case) but
    /// refuse a major version they do not know.
    let schemaVersion = 1

    let private stamp (payload: string) =
        let ts = DateTime.UtcNow.ToString("o")
        if payload.StartsWith "{" then sprintf """{"ts":"%s",%s""" ts (payload.Substring 1)
        else sprintf """{"ts":"%s","data":%s}""" ts payload

    /// Emit a Kernel-shaped event — the path the handler stack itself takes.
    let event (sink: string -> unit) (payload: LispVal) =
        sink (stamp (Json.serialize payload))

    /// How a run ended. `run-finished` says this outright so a reader never
    /// has to infer success from the absence of an error event.
    type Status =
        | Completed
        | Failed of reason: string
        | Interrupted
        | BudgetDenied

    let statusName = function
        | Completed -> "ok"
        | Failed _ -> "error"
        | Interrupted -> "interrupted"
        | BudgetDenied -> "budget-denied"

    /// A policy layer's identity, as configured for this run. The resolved
    /// trust of each layer is recorded separately by the session's own
    /// `policy-layer` events; this is what the run was *asked* to enforce.
    type PolicyLayer =
        { source: string
          digest: string
          isProtected: bool }

    type RunStart =
        { runId: string
          /// run | chat | ui | script | repl | golden — what invoked this.
          command: string
          task: string option
          model: string
          /// The agent package directory, or "default".
          agent: string
          budgetLlmCalls: int option
          budgetTokens: int option
          policy: PolicyLayer list }

    /// Write `run-started` and return the function that closes the run with
    /// `run-finished`. Closing twice writes once — the CLI can call it on
    /// both the success and the error path without checking.
    let openRun (sink: string -> unit) (start: RunStart) : Status -> unit =
        let doc = JsonObject()
        doc.["event"] <- JsonValue.Create "run-started"
        doc.["schema_version"] <- JsonValue.Create schemaVersion
        doc.["run_id"] <- JsonValue.Create start.runId
        doc.["jern_version"] <- JsonValue.Create AgentEnv.version
        doc.["command"] <- JsonValue.Create start.command
        (match start.task with
         | Some task -> doc.["task"] <- JsonValue.Create task
         | None -> ())
        doc.["model"] <- JsonValue.Create start.model
        doc.["agent"] <- JsonValue.Create start.agent
        let budget = JsonObject()
        budget.["llm_calls"] <-
            (match start.budgetLlmCalls with Some n -> JsonValue.Create n :> JsonNode | None -> null)
        budget.["tokens"] <-
            (match start.budgetTokens with Some n -> JsonValue.Create n :> JsonNode | None -> null)
        doc.["budget"] <- budget
        let layers = JsonArray()
        for layer in start.policy do
            let l = JsonObject()
            l.["source"] <- JsonValue.Create layer.source
            l.["digest"] <- JsonValue.Create layer.digest
            l.["protected"] <- JsonValue.Create layer.isProtected
            layers.Add l
        doc.["policy"] <- layers
        sink (stamp (doc.ToJsonString()))

        let started = DateTime.UtcNow
        let mutable closed = false
        fun status ->
            if not closed then
                closed <- true
                let finish = JsonObject()
                finish.["event"] <- JsonValue.Create "run-finished"
                finish.["status"] <- JsonValue.Create(statusName status)
                (match status with
                 | Failed reason -> finish.["reason"] <- JsonValue.Create reason
                 | _ -> ())
                finish.["duration_ms"] <- JsonValue.Create(int (DateTime.UtcNow - started).TotalMilliseconds)
                sink (stamp (finish.ToJsonString()))
