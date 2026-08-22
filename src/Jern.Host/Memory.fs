namespace Jern.Host

open System.IO
open System.Text.Json
open System.Text.Json.Nodes

/// Persistent, cross-session agent memory: a flat string→string store at
/// `<workspace>/.jern/memory.json`. The agent reaches it only through the
/// `jern/recall` and `jern/remember` effects, so every access crosses the
/// handler stack — traced, and gated by `memory-policy` in policy.ikr.
module Memory =

    let storePath (workspaceRoot: string) =
        Path.Combine(workspaceRoot, ".jern", "memory.json")

    let load (path: string) : Map<string, string> =
        if not (File.Exists path) then Map.empty
        else
            try
                let doc = JsonNode.Parse(File.ReadAllText path).AsObject()
                doc
                |> Seq.choose (fun kv ->
                    match kv.Value with
                    | :? JsonValue as v ->
                        match v.TryGetValue<string>() with
                        | true, s -> Some(kv.Key, s)
                        | _ -> None
                    | _ -> None)
                |> Map.ofSeq
            with _ -> Map.empty

    let get (path: string) (key: string) : string option =
        (load path).TryFind key

    let set (path: string) (key: string) (value: string) =
        let dir = Path.GetDirectoryName path
        if dir <> "" then Directory.CreateDirectory dir |> ignore
        let doc = JsonObject()
        load path
        |> Map.add key value
        |> Map.iter (fun k v -> doc.[k] <- JsonValue.Create(v: string))
        File.WriteAllText(path, doc.ToJsonString(JsonSerializerOptions(WriteIndented = true)) + "\n")

    let keys (path: string) : string list =
        load path |> Map.toList |> List.map fst
