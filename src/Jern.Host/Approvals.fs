namespace Jern.Host

open System
open System.Collections.Generic

/// Approval ergonomics shared by the CLI prompt and the web UI.
///
/// Policy still decides first: :allow never asks, an explicit denial never
/// reaches an approver at all. These helpers only govern the :ask middle —
/// auto-approve mode ("approve everything not explicitly denied", opencode's
/// --auto semantics) and per-tool "always allow" memory for one session.
module Approvals =

    /// The stable part of an approval description — the unit an "always"
    /// answer whitelists for the rest of the session. Shell commands key on
    /// the tool plus the command word ("shell: git status" -> "shell: git"),
    /// never the whole tool: whitelisting `ls` must not whitelist every
    /// command the model invents later. Other tools key on the name before
    /// the first ':' of the first line ("edit_file: p\n - …" -> "edit_file"),
    /// or the whole first line when there is none (budget questions never
    /// repeat verbatim, so they are effectively never remembered). Approval
    /// prompts show this key, so the user sees exactly what is remembered.
    let key (description: string) =
        let firstLine =
            match description.IndexOf '\n' with
            | -1 -> description
            | i -> description.Substring(0, i)
        match firstLine.IndexOf ':' with
        | -1 -> firstLine.Trim()
        | i ->
            let tool = firstLine.Substring(0, i).Trim()
            if tool = "shell" then
                let word =
                    firstLine.Substring(i + 1).Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    |> Array.tryHead
                match word with
                | Some w -> tool + ": " + w
                | None -> tool
            else tool

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
