namespace Iron.Host

open System.Text.Json
open System.Text.Json.Nodes
open IronKernel
open IronKernel.Ast

/// JSON ⇄ Kernel data, the frozen boundary convention (implementation plan §4.2).
///
/// | JSON            | Kernel                                            |
/// |-----------------|---------------------------------------------------|
/// | object          | plist: `(:key value :key2 value2 …)`, `{}` ↔ `()` |
/// | array           | vector                                            |
/// | string          | CLR string (`Obj`)                                |
/// | integer         | exact integer (`Obj` int64)                       |
/// | float           | inexact real (`Obj` double)                       |
/// | true / false    | boolean                                           |
/// | null            | `:null`                                           |
///
/// Keywords carry the exact JSON key name (`:max_tokens`, `:stop_reason`), so
/// the conversion is a pure structural bijection: agent source and traces read
/// exactly like the Messages API wire format. A keyword in value position other
/// than `:null` serializes as its name string.
module Json =

    let rec toLispVal (node: JsonNode) : LispVal =
        match node with
        | null -> Keyword "null"
        | :? JsonObject as o ->
            o
            |> Seq.collect (fun kv -> [ Keyword kv.Key; toLispVal kv.Value ])
            |> List.ofSeq
            |> ofList
        | :? JsonArray as a ->
            Vector(a |> Seq.map toLispVal |> Array.ofSeq)
        | :? JsonValue as v ->
            match v.GetValueKind() with
            | JsonValueKind.String -> Obj(v.GetValue<string>() :> obj)
            | JsonValueKind.True -> Bool true
            | JsonValueKind.False -> Bool false
            | JsonValueKind.Null -> Keyword "null"
            | JsonValueKind.Number ->
                // Kernel's native exact integers are int32 (vector-ref and
                // friends expect them); wider values stay int64.
                let mutable small = 0
                let mutable big = 0L
                if v.TryGetValue<int>(&small) then Obj(small :> obj)
                elif v.TryGetValue<int64>(&big) then Obj(big :> obj)
                else Obj(v.GetValue<double>() :> obj)
            | other -> failwithf "Unsupported JSON value kind: %A" other
        | other -> failwithf "Unsupported JSON node: %A" other

    /// Kernel plists must have keyword keys in even positions; anything else
    /// here is a malformed boundary value and surfaces as an error.
    let rec fromLispVal (value: LispVal) : JsonNode =
        match value with
        | Bool b -> JsonValue.Create(b) :> JsonNode
        | Keyword "null" -> null
        | Keyword name -> JsonValue.Create(name) :> JsonNode
        | Obj o ->
            match o with
            | :? string as s -> JsonValue.Create(s) :> JsonNode
            | :? int64 as i -> JsonValue.Create(i) :> JsonNode
            | :? int32 as i -> JsonValue.Create(i) :> JsonNode
            | :? byte as b -> JsonValue.Create(int b) :> JsonNode
            | :? System.Numerics.BigInteger as b -> JsonValue.Create(int64 b) :> JsonNode
            | :? double as d -> JsonValue.Create(d) :> JsonNode
            | :? single as s -> JsonValue.Create(double s) :> JsonNode
            | other -> failwithf "Value does not cross the JSON boundary: %A" other
        | Vector items ->
            let array = JsonArray()
            items |> Array.iter (fun item -> array.Add(fromLispVal item))
            array :> JsonNode
        | Nil -> JsonObject() :> JsonNode
        | Pair _ ->
            let o = JsonObject()
            let rec walk v =
                match v with
                | Nil -> ()
                | Pair cell ->
                    match cell.car, cell.cdr with
                    | Keyword key, Pair valueCell ->
                        o.[key] <- fromLispVal valueCell.car
                        walk valueCell.cdr
                    | Keyword key, bad ->
                        failwithf "Plist key :%s has no value (found %s)" key (showVal bad)
                    | badKey, _ ->
                        failwithf "Plist keys must be keywords, found %s" (showVal badKey)
                | bad -> failwithf "Improper plist tail: %s" (showVal bad)
            walk value
            o :> JsonNode
        | other -> failwithf "Value does not cross the JSON boundary: %s" (showVal other)

    let serialize (value: LispVal) : string =
        match fromLispVal value with
        | null -> "null"
        | node -> node.ToJsonString(JsonSerializerOptions(WriteIndented = false))

    let deserialize (json: string) : LispVal =
        JsonNode.Parse(json) |> toLispVal
