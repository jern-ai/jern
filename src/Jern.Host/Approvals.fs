namespace Jern.Host

open System.Collections.Generic

/// Approval ergonomics shared by the CLI prompt and the web UI.
///
/// Policy still decides first: :allow never asks, an explicit denial never
/// reaches an approver at all. These helpers only govern the :ask middle —
/// auto-approve mode ("approve everything not explicitly denied", opencode's
/// --auto semantics) and per-tool "always allow" memory for one session.
module Approvals =

    /// The stable part of an approval description: the tool name before the
    /// first ':' of the first line ("edit_file: path\n - …" -> "edit_file"),
    /// or the whole first line when there is none (budget questions never
    /// repeat verbatim, so they are effectively never remembered).
    let key (description: string) =
        let firstLine =
            match description.IndexOf '\n' with
            | -1 -> description
            | i -> description.Substring(0, i)
        match firstLine.IndexOf ':' with
        | -1 -> firstLine.Trim()
        | i -> firstLine.Substring(0, i).Trim()

    /// Session-scoped memory of "always allow" answers, plus the auto mode.
    type Memory(auto: bool) =
        let always = HashSet<string>()
        let sync = obj ()
        member val Auto = auto with get, set

        /// True when this description needs no human: auto mode is on, or its
        /// tool was answered "always" earlier in the session.
        member this.Covers(description: string) =
            this.Auto || lock sync (fun () -> always.Contains(key description))

        member _.RememberAlways(description: string) =
            lock sync (fun () -> always.Add(key description) |> ignore)
