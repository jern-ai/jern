module Iron.Tests.JsonTests

open Xunit
open IronKernel.Ast
open Iron.Host

let private roundTrip json =
    Json.serialize (Json.deserialize json)

[<Fact>]
let ``objects round-trip as plists`` () =
    let json = """{"model":"claude-opus-5","max_tokens":256,"stream":false}"""
    Assert.Equal(json, roundTrip json)

[<Fact>]
let ``nested arrays and objects round-trip`` () =
    let json = """{"messages":[{"role":"user","content":"hi"}],"tools":[],"empty":{}}"""
    Assert.Equal(json, roundTrip json)

[<Fact>]
let ``scalars round-trip`` () =
    let json = """{"s":"x","i":42,"f":1.5,"t":true,"nil":null}"""
    Assert.Equal(json, roundTrip json)

[<Fact>]
let ``json maps to the documented kernel shapes`` () =
    match Json.deserialize """{"a":1}""" with
    | Pair cell ->
        match cell.car with
        | Keyword "a" -> ()
        | other -> failwith ("expected :a key, got " + showVal other)
    | other -> failwith ("expected plist, got " + showVal other)
    match Json.deserialize """[1,2]""" with
    | Vector items -> Assert.Equal(2, items.Length)
    | other -> failwith ("expected vector, got " + showVal other)
    match Json.deserialize "null" with
    | Keyword "null" -> ()
    | other -> failwith ("expected :null, got " + showVal other)

[<Fact>]
let ``integers become kernel-native int32`` () =
    match Json.deserialize "7" with
    | Obj o -> Assert.IsType<int>(o) |> ignore
    | other -> failwith ("expected Obj, got " + showVal other)
