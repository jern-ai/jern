namespace Jern.Host

open System
open System.IO
open IronKernel
open IronKernel.Ast
open IronKernel.Errors
open IronKernel.Eval

/// `jern test` — run an agent package's test suite.
///
/// Test files live in `<agent>/test/*.ikr`. Top-level `(deftest "name" body…)`
/// forms are the tests; any other top-level form is shared setup, evaluated
/// before each test. Every test runs in a fresh session and a fresh temporary
/// workspace, with `jern/llm-call` answered by the fixture bridge: replay by
/// default (deterministic, network-free), record with `--record`.
module TestRunner =

    type Outcome =
        { name: string
          file: string
          error: string option }

    type Summary =
        { outcomes: Outcome list }
        member this.Failed = this.outcomes |> List.filter (fun o -> o.error.IsSome)
        member this.Passed = this.outcomes |> List.filter (fun o -> o.error.IsNone)

    let private parseFile path : ThrowsError<LispVal list> =
        try
            Parser.readExprListFromSource path (File.ReadAllText path)
        with ex ->
            Choice1Of2 (Default(sprintf "failed to read '%s': %s" path ex.Message))

    let private (|Deftest|_|) (form: LispVal) =
        match form with
        | Pair { car = Atom "deftest"; cdr = Pair { car = Obj (:? string as name); cdr = body } } ->
            let rec toList = function
                | Nil -> Some []
                | Pair cell -> toList cell.cdr |> Option.map (fun rest -> cell.car :: rest)
                | _ -> None
            toList body |> Option.map (fun forms -> name, forms)
        | _ -> None

    let private stringContains env cont = function
        | [Obj (:? string as haystack); Obj (:? string as needle)] ->
            bounceContinue env cont (Bool(haystack.Contains(needle: string)))
        | bad -> signal cont (NumArgs(2, bad))

    let private runOne (agentDir: string) (mode: Fixtures.Mode) (file: string)
                       (setup: LispVal list) (name: string) (body: LispVal list) : Outcome =
        let workspace = Path.Combine(Path.GetTempPath(), "jern-test-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory workspace |> ignore
        try
            let fixtures = Fixtures.FixtureBridge(mode, Path.Combine(agentDir, "test"))
            let useFixtures env cont = function
                | [Obj (:? string as path)] ->
                    match fixtures.Use path with
                    | Choice1Of2 error -> signal cont error
                    | Choice2Of2 () -> bounceContinue env cont Inert
                | bad -> signal cont (NumArgs(1, bad))
            let setupFile env cont = function
                | [Obj (:? string as path); Obj (:? string as content)] ->
                    let full = Path.GetFullPath(Path.Combine(workspace, path))
                    if not (full.StartsWith(Path.GetFullPath workspace, StringComparison.Ordinal)) then
                        signal cont (Default(sprintf "setup-file: '%s' is outside the test workspace" path))
                    else
                        Directory.CreateDirectory(Path.GetDirectoryName full) |> ignore
                        File.WriteAllText(full, content)
                        bounceContinue env cont Inert
                | bad -> signal cont (NumArgs(2, bad))
            let config =
                { Session.configIn workspace fixtures.Bridge with
                    agentSources =
                        Session.kernelFile "test-prelude.ikr"
                        :: Session.agentPackageSources agentDir
                    agentBindings =
                        [ "jern/use-fixtures", AgentEnv.applicative useFixtures
                          "jern/setup-file", AgentEnv.applicative setupFile
                          "jern/string-contains?", AgentEnv.applicative stringContains ] }
            match Session.createWith config with
            | Choice1Of2 error ->
                { name = name; file = file; error = Some(showError error) }
            | Choice2Of2 session ->
                let result = Session.runForms session (setup @ body)
                let succeeded = match result with Choice2Of2 _ -> true | _ -> false
                let finishError =
                    match fixtures.Finish succeeded with
                    | Ok () -> None
                    | Error message -> Some message
                match result, finishError with
                | Choice1Of2 error, _ -> { name = name; file = file; error = Some(showError error) }
                | _, Some message -> { name = name; file = file; error = Some message }
                | _ -> { name = name; file = file; error = None }
        finally
            try Directory.Delete(workspace, true) with _ -> ()

    /// Run every test of the agent package at `agentDir`.
    let run (agentDir: string) (mode: Fixtures.Mode) : Result<Summary, string> =
        let testDir = Path.Combine(agentDir, "test")
        if not (Directory.Exists testDir) then
            Error(sprintf "no test directory at '%s'" testDir)
        else
            let files = Directory.EnumerateFiles(testDir, "*.ikr") |> Seq.sort |> List.ofSeq
            if files.IsEmpty then
                Error(sprintf "no .ikr test files in '%s'" testDir)
            else
                let outcomes =
                    files
                    |> List.collect (fun file ->
                        match parseFile file with
                        | Choice1Of2 error ->
                            [ { name = "(parse)"; file = file; error = Some(showError error) } ]
                        | Choice2Of2 forms ->
                            let setup =
                                forms |> List.filter (fun f -> (|Deftest|_|) f |> Option.isNone)
                            forms
                            |> List.choose (|Deftest|_|)
                            |> List.map (fun (name, body) ->
                                runOne agentDir mode file setup name body))
                Ok { outcomes = outcomes }
