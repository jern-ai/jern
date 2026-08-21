module Jern.Tests.ArgsTests

open Xunit
open Jern.Cli
open Jern.Cli.Args

let private parsed args =
    match Args.parse args with
    | Ok result -> result
    | Error e -> failwithf "expected a parse, got %A" e

let private command args = snd (parsed args)
let private globals args = fst (parsed args)

let private failure args =
    match Args.parse args with
    | Error e -> e
    | Ok r -> failwithf "expected a parse error, got %A" r

// --- run: flags may come before or after the task ---

[<Fact>]
let ``run accepts --agent after the task`` () =
    Assert.Equal(Run(false, Some "agents/reviewer", "fix the tests"),
                 command ["run"; "fix the tests"; "--agent"; "agents/reviewer"])

[<Fact>]
let ``run accepts --yes after the task`` () =
    Assert.Equal(Run(true, None, "fix"), command ["run"; "fix"; "--yes"])

[<Fact>]
let ``run accepts flags interleaved around the task`` () =
    Assert.Equal(Run(true, Some "a", "fix"),
                 command ["run"; "--agent"; "a"; "fix"; "--yes"])

[<Fact>]
let ``run still accepts the flags-first ordering`` () =
    Assert.Equal(Run(true, Some "a", "fix"),
                 command ["run"; "--yes"; "--agent"; "a"; "fix"])

[<Fact>]
let ``run without a task is a usage error`` () =
    Assert.Equal(SubUsage runUsage, failure ["run"; "--yes"])

[<Fact>]
let ``run with two tasks is a usage error`` () =
    Assert.Equal(SubUsage runUsage, failure ["run"; "one"; "two"])

[<Fact>]
let ``run with a valueless --agent is a usage error`` () =
    Assert.Equal(SubUsage runUsage, failure ["run"; "fix"; "--agent"])

// --- global flags: anywhere on the line ---

[<Fact>]
let ``global flags may follow the subcommand's arguments`` () =
    let g, c = parsed ["run"; "fix"; "--model"; "openai/gpt-5.2"; "--budget"; "3"; "--auto"]
    Assert.Equal(Run(false, None, "fix"), c)
    Assert.Equal(Some "openai/gpt-5.2", g.model)
    Assert.Equal(Some 3, g.budget)
    Assert.True g.auto

[<Fact>]
let ``global flags may precede the subcommand`` () =
    let g, c = parsed ["--think"; "2048"; "--effort"; "high"; "repl"]
    Assert.Equal(Repl, c)
    Assert.Equal(Some 2048, g.think)
    Assert.Equal(Some "high", g.effort)

[<Fact>]
let ``global flags after a bare command parse too`` () =
    let g, c = parsed ["repl"; "--effort"; "high"]
    Assert.Equal(Repl, c)
    Assert.Equal(Some "high", g.effort)

[<Fact>]
let ``no arguments parses as NoArgs with default globals`` () =
    Assert.Equal((defaultGlobals, NoArgs), parsed [])

// --- global flag validation ---

[<Fact>]
let ``budget rejects a non-integer`` () =
    Assert.Equal(BadValue "--budget needs a positive integer, got 'nope'",
                 failure ["--budget"; "nope"; "repl"])

[<Fact>]
let ``budget rejects zero`` () =
    Assert.Equal(BadValue "--budget needs a positive integer, got '0'",
                 failure ["--budget"; "0"])

[<Fact>]
let ``think rejects a negative budget`` () =
    Assert.Equal(BadValue "--think needs a positive token budget, got '-5'",
                 failure ["--think"; "-5"])

[<Fact>]
let ``a trailing --model without a value is an error`` () =
    Assert.Equal(BadValue "--model needs a value (provider/model)",
                 failure ["run"; "fix"; "--model"])

// --- ui ---

[<Fact>]
let ``ui accepts its flags in either order`` () =
    Assert.Equal(Ui(8080, Some "d"), command ["ui"; "--port"; "8080"; "--agent"; "d"])
    Assert.Equal(Ui(8080, Some "d"), command ["ui"; "--agent"; "d"; "--port"; "8080"])

[<Fact>]
let ``ui rejects a non-numeric port`` () =
    Assert.Equal(SubUsage uiUsage, failure ["ui"; "--port"; "abc"])

[<Fact>]
let ``ui rejects stray positionals`` () =
    Assert.Equal(SubUsage uiUsage, failure ["ui"; "stray"])

// --- test ---

[<Fact>]
let ``test accepts --record before or after the dir`` () =
    Assert.Equal(Test(Some "dir", true), command ["test"; "dir"; "--record"])
    Assert.Equal(Test(Some "dir", true), command ["test"; "--record"; "dir"])

[<Fact>]
let ``test parses its bare forms`` () =
    Assert.Equal(Test(None, false), command ["test"])
    Assert.Equal(Test(None, true), command ["test"; "--record"])
    Assert.Equal(Test(Some "dir", false), command ["test"; "dir"])

[<Fact>]
let ``test with two dirs is a usage error`` () =
    Assert.Equal(SubUsage testUsage, failure ["test"; "a"; "b"])

// --- resume, version, and the simple commands ---

[<Fact>]
let ``resume parses with and without an id`` () =
    Assert.Equal(Resume None, command ["--resume"])
    Assert.Equal(Resume(Some "abc"), command ["--resume"; "abc"])

[<Fact>]
let ``resume mixes with global flags`` () =
    let g, c = parsed ["--auto"; "--resume"; "abc"]
    Assert.Equal(Resume(Some "abc"), c)
    Assert.True g.auto

[<Fact>]
let ``version parses in both spellings`` () =
    Assert.Equal(Version, command ["--version"])
    Assert.Equal(Version, command ["version"])

[<Fact>]
let ``the simple commands parse`` () =
    Assert.Equal(Undo, command ["undo"])
    Assert.Equal(Eject, command ["eject"])
    Assert.Equal(Repl, command ["repl"])
    Assert.Equal(Mcp, command ["mcp"])
    Assert.Equal(Policy false, command ["policy"])
    Assert.Equal(Policy true, command ["policy"; "init"])
    Assert.Equal(Script "f.ikr", command ["script"; "f.ikr"])

[<Fact>]
let ``unknown arguments are reported as such`` () =
    Assert.Equal(UnknownArgs ["frobnicate"], failure ["frobnicate"])
    Assert.Equal(UnknownArgs ["policy"; "reset"], failure ["policy"; "reset"])
