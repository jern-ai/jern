/// The whole CLI surface, parsed in one place. Pure — no printing, no
/// exit — so the grammar is unit-testable. Global flags are pulled from
/// anywhere on the line, and each command's own flags may come before or
/// after its positional arguments.
module Jern.Cli.Args

open System

/// Flags valid on every command.
type Globals =
    { model: string option
      budget: int option
      auto: bool
      think: int option
      effort: string option
      /// --policy-baseline <file>: a policy that this checkout may tighten
      /// but not weaken. CI supplies it from the base branch or from
      /// workflow-owned data, never from the pull request's own tree.
      policyBaseline: string option
      /// --policy-trust <sha256>: policy digests whose grants apply without
      /// an interactive prompt. The way a workflow blesses grants headlessly.
      policyTrust: string list }

let defaultGlobals =
    { model = None; budget = None; auto = false; think = None; effort = None
      policyBaseline = None; policyTrust = [] }

/// How `jern receipt` prints: for a human, a PR comment, or a tool.
type ReceiptFormat =
    | Text
    | Markdown
    | Json

type Command =
    /// Bare `jern` — chat on a terminal, usage text otherwise.
    | NoArgs
    | Version
    | Resume of id: string option
    | Run of yes: bool * agent: string option * task: string
    | Ui of port: int * agent: string option
    | Undo
    | Eject
    | Repl
    | Script of path: string
    | Test of dir: string option * record: bool
    | Replay of trace: string * policy: string option * agent: string option
    | Receipt of trace: string option * format: ReceiptFormat
    | Mcp
    | Policy of init: bool * showCompiled: bool

type ParseError =
    /// A flag's value is missing or invalid; printed as "jern: <message>".
    | BadValue of message: string
    /// A command's arguments don't fit its shape; printed verbatim.
    | SubUsage of line: string
    /// Arguments that match no command; printed with the full usage text.
    | UnknownArgs of args: string list

let runUsage = "usage: jern run [--yes] [--agent <dir>] [--model <spec>] [--budget <n>] \"task\""
let uiUsage = "usage: jern ui [--port <n>] [--agent <dir>] [--model <spec>] [--budget <n>]"
let testUsage = "usage: jern test [<agent-dir>] [--record]"
let replayUsage = "usage: jern replay <trace.jsonl> [--policy <file>] [--agent <dir>]"
let receiptUsage = "usage: jern receipt [<trace.jsonl>] [--md | --json]"

let private positiveInt (flag: string) (what: string) (raw: string) =
    match Int32.TryParse raw with
    | true, n when n > 0 -> Ok n
    | _ -> Error(BadValue(sprintf "%s needs a positive %s, got '%s'" flag what raw))

/// Strip the global flags, wherever they appear, leaving the command and
/// its own arguments in order.
let private extractGlobals (args: string list) =
    let rec go acc g = function
        | [] -> Ok(List.rev acc, g)
        | "--model" :: spec :: rest -> go acc { g with model = Some spec } rest
        | ["--model"] -> Error(BadValue "--model needs a value (provider/model)")
        | "--budget" :: n :: rest ->
            positiveInt "--budget" "integer" n
            |> Result.bind (fun calls -> go acc { g with budget = Some calls } rest)
        | ["--budget"] -> Error(BadValue "--budget needs a positive integer")
        | "--auto" :: rest -> go acc { g with auto = true } rest
        | "--think" :: n :: rest ->
            positiveInt "--think" "token budget" n
            |> Result.bind (fun tokens -> go acc { g with think = Some tokens } rest)
        | ["--think"] -> Error(BadValue "--think needs a positive token budget")
        | "--effort" :: level :: rest -> go acc { g with effort = Some level } rest
        | ["--effort"] -> Error(BadValue "--effort needs a level (low|medium|high)")
        | "--policy-baseline" :: file :: rest -> go acc { g with policyBaseline = Some file } rest
        | ["--policy-baseline"] -> Error(BadValue "--policy-baseline needs a file path")
        | "--policy-trust" :: digest :: rest ->
            go acc { g with policyTrust = g.policyTrust @ [ digest ] } rest
        | ["--policy-trust"] -> Error(BadValue "--policy-trust needs a sha256 policy digest")
        | arg :: rest -> go (arg :: acc) g rest
    go [] defaultGlobals args

/// jern run [--yes] [--agent <dir>] "task" — flags and task in any order.
let private parseRun rest =
    let rec go yes agent positionals = function
        | "--yes" :: more -> go true agent positionals more
        | "--agent" :: dir :: more -> go yes (Some dir) positionals more
        | ["--agent"] -> Error(SubUsage runUsage)
        | arg :: more -> go yes agent (arg :: positionals) more
        | [] ->
            match List.rev positionals with
            | [task] -> Ok(Run(yes, agent, task))
            | _ -> Error(SubUsage runUsage)
    go false None [] rest

/// jern ui [--port <n>] [--agent <dir>] — flags in any order, no positionals.
let private parseUi rest =
    let rec go port agent = function
        | "--port" :: p :: more ->
            (match Int32.TryParse(p: string) with
             | true, n -> go n agent more
             | _ -> Error(SubUsage uiUsage))
        | "--agent" :: dir :: more -> go port (Some dir) more
        | [] -> Ok(Ui(port, agent))
        | _ -> Error(SubUsage uiUsage)
    go 0 None rest

/// jern test [<agent-dir>] [--record] — flag and dir in any order.
let private parseTest rest =
    let rec go dir record = function
        | "--record" :: more -> go dir true more
        | arg :: more when Option.isNone dir -> go (Some arg) record more
        | [] -> Ok(Test(dir, record))
        | _ -> Error(SubUsage testUsage)
    go None false rest

/// jern replay <trace> [--policy <file>] [--agent <dir>] — any order.
let private parseReplay rest =
    let rec go policy agent positionals = function
        | "--policy" :: file :: more -> go (Some file) agent positionals more
        | ["--policy"] -> Error(SubUsage replayUsage)
        | "--agent" :: dir :: more -> go policy (Some dir) positionals more
        | ["--agent"] -> Error(SubUsage replayUsage)
        | arg :: more -> go policy agent (arg :: positionals) more
        | [] ->
            match List.rev positionals with
            | [trace] -> Ok(Replay(trace, policy, agent))
            | _ -> Error(SubUsage replayUsage)
    go None None [] rest

/// jern receipt [<trace>] [--md|--json] — flag and path in any order.
let private parseReceipt rest =
    let rec go trace format = function
        | "--md" :: more -> go trace Markdown more
        | "--markdown" :: more -> go trace Markdown more
        | "--json" :: more -> go trace Json more
        | arg :: more when Option.isNone trace -> go (Some arg) format more
        | [] -> Ok(Receipt(trace, format))
        | _ -> Error(SubUsage receiptUsage)
    go None Text rest

let parse (argv: string list) : Result<Globals * Command, ParseError> =
    match extractGlobals argv with
    | Error e -> Error e
    | Ok(rest, globals) ->
        let command =
            match rest with
            | [] -> Ok NoArgs
            | ["--version"] | ["version"] -> Ok Version
            | ["--resume"] -> Ok(Resume None)
            | ["--resume"; id] -> Ok(Resume(Some id))
            | "run" :: more -> parseRun more
            | "ui" :: more -> parseUi more
            | ["undo"] -> Ok Undo
            | ["eject"] -> Ok Eject
            | ["repl"] -> Ok Repl
            | ["script"; path] -> Ok(Script path)
            | "test" :: more -> parseTest more
            | "replay" :: more -> parseReplay more
            | "receipt" :: more -> parseReceipt more
            | ["mcp"] -> Ok Mcp
            | ["policy"] -> Ok(Policy(false, false))
            | ["policy"; "--show-compiled"] -> Ok(Policy(false, true))
            | ["policy"; "init"] -> Ok(Policy(true, false))
            | other -> Error(UnknownArgs other)
        command |> Result.map (fun c -> globals, c)
