namespace Iron.Host

open System
open System.IO
open System.Text.Json
open System.Text.Json.Nodes
open IronKernel.Ast

/// Chat-session persistence for `iron` / `iron --resume`.
///
/// A session file is `.iron/sessions/<id>.json`:
/// `{"id": …, "workspace": …, "updated": …, "messages": [...]}` with the
/// conversation oldest-first in Messages API wire shape — the same JSON the
/// model sees, so a session file is also a readable transcript. Environments
/// are ephemeral by design (implementation plan M6): resuming rebuilds the
/// session and replays nothing; only the message history survives.
module SessionStore =

    let private dir root = Path.Combine(root, ".iron", "sessions")

    let private pathFor root (id: string) = Path.Combine(dir root, id + ".json")

    let newId () = DateTime.Now.ToString("yyyyMMdd-HHmmss")

    /// Kernel list (newest-first) -> F# list.
    let rec private toItems value =
        match value with
        | Pair cell -> cell.car :: toItems cell.cdr
        | _ -> []

    /// Persist a conversation (Kernel list, newest-first).
    let save (root: string) (id: string) (messages: LispVal) =
        Directory.CreateDirectory(dir root) |> ignore
        let array = Vector(toItems messages |> List.rev |> Array.ofList)
        let doc = JsonObject()
        doc.["id"] <- JsonValue.Create id
        doc.["workspace"] <- JsonValue.Create(Path.GetFullPath root)
        doc.["updated"] <- JsonValue.Create(DateTime.UtcNow.ToString "o")
        doc.["messages"] <- Json.fromLispVal array
        File.WriteAllText(pathFor root id, doc.ToJsonString(JsonSerializerOptions(WriteIndented = true)) + "\n")

    /// Load a conversation as a Kernel list (newest-first).
    let load (root: string) (id: string) : Result<LispVal, string> =
        let path = pathFor root id
        if not (File.Exists path) then
            Error(sprintf "no session '%s' (looked at %s)" id path)
        else
            try
                let doc = JsonNode.Parse(File.ReadAllText path).AsObject()
                match Json.toLispVal doc.["messages"] with
                | Vector items ->
                    Ok(items |> Array.toList |> List.rev |> ofList)
                | other -> Error(sprintf "session '%s' has malformed messages: %s" id (showVal other))
            with ex ->
                Error(sprintf "session '%s' is unreadable: %s" id ex.Message)

    /// The most recently updated session id in this workspace, if any.
    let latest (root: string) : string option =
        if not (Directory.Exists(dir root)) then None
        else
            Directory.EnumerateFiles(dir root, "*.json")
            |> Seq.sortByDescending File.GetLastWriteTimeUtc
            |> Seq.tryHead
            |> Option.map Path.GetFileNameWithoutExtension
