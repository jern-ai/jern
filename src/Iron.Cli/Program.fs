module Iron.Cli.Program

open System
open System.Text
open IronKernel
open IronKernel.Ast
open IronKernel.Errors
open Iron.Host

let private usage = """iron — a terminal coding agent whose brain is IronKernel source

Usage:
  iron                Interactive chat session in the current workspace
                      (persisted to .iron/sessions/)
  iron --resume [id]  Continue the latest (or the given) chat session
  iron run [--yes] [--agent <dir>] "task"
                      One-shot agentic task in the current workspace
                      (trace in .iron/)
                      --yes approves policy-gated actions (writes, shell)
                      --agent runs a different agent package (the headline)
  iron undo           Revert the last iron-authored commit (also /undo in chat)
  iron eject          Copy the default agent's source into ./agents/default
  iron repl           Kernel REPL inside the agent's restricted environment
  iron script <file>  Run a .ikr script as agent code under the handler stack
  iron test [<agent-dir>] [--record]
                      Run an agent package's test suite. Replay is
                      deterministic and network-free; --record captures new
                      fixtures from the live provider
  iron --version      Print version

Models & providers:
  --model provider/model on any command (e.g. --model openai/gpt-5.2,
  ollama/qwen3, anthropic/claude-opus-5). Default and aliases come from
  iron.json / ~/.config/iron/config.json. Keys via provider env vars
  (ANTHROPIC_API_KEY, OPENAI_API_KEY, …); ollama and lmstudio need none.
"""

/// Streams model text to the terminal, remembering whether the turn's last
/// character needs a closing newline.
type private ConsoleStream() =
    let mutable needsNewline = false
    member _.Write(piece: string) =
        Console.Write piece
        if piece <> "" then needsNewline <- not (piece.EndsWith "\n")
    member _.FinishTurn() =
        if needsNewline then
            Console.WriteLine()
            needsNewline <- false

/// Running token totals across a command, fed from response :usage.
type private UsageMeter() =
    let mutable input = 0L
    let mutable output = 0L
    member _.Wrap (stream: ConsoleStream option) (bridge: AnthropicBridge.LlmBridge) : AnthropicBridge.LlmBridge =
        fun request ->
            let result = bridge request
            stream |> Option.iter (fun s -> s.FinishTurn())
            match result with
            | Choice2Of2 response ->
                match Tools.plistTryGet "usage" response with
                | Some usage ->
                    let grab key =
                        match Tools.plistTryGet key usage with
                        | Some (Obj v) -> (try Convert.ToInt64 v with _ -> 0L)
                        | _ -> 0L
                    input <- input + grab "input_tokens"
                    output <- output + grab "output_tokens"
                | None -> ()
            | Choice1Of2 _ -> ()
            result
    member _.Line =
        let fmt (n: int64) =
            if n >= 10_000L then sprintf "%.1fk" (float n / 1000.0) else string n
        sprintf "tokens: %s in, %s out" (fmt input) (fmt output)
    member _.SawUsage = input > 0L || output > 0L

/// The provider-routing bridge for live commands.
let private newBridge (model: string option) (stream: ConsoleStream option) (meter: UsageMeter) =
    match Providers.load Environment.CurrentDirectory with
    | Error message -> Error message
    | Ok config ->
        Providers.createBridge config model (stream |> Option.map (fun s -> s.Write))
        |> meter.Wrap stream
        |> Ok

let private runTests (dirArg: string option) (record: bool) (model: string option) =
    let agentDir =
        match dirArg with
        | Some dir -> dir
        | None ->
            let local = IO.Path.Combine(Environment.CurrentDirectory, "agents", "default")
            if IO.Directory.Exists local then local else Session.defaultAgentDir ()
    let mode =
        if record then
            match newBridge model None (UsageMeter()) with
            | Error message ->
                eprintfn "iron test: %s" message
                exit 2
            | Ok bridge -> Fixtures.Record bridge
        else Fixtures.Replay
    match TestRunner.run agentDir mode with
    | Error message ->
        eprintfn "iron test: %s" message
        2
    | Ok summary ->
        for outcome in summary.outcomes do
            match outcome.error with
            | None -> printfn "ok   - %s" outcome.name
            | Some error ->
                printfn "FAIL - %s" outcome.name
                printfn "       %s" (error.Replace("\n", "\n       "))
        printfn ""
        printfn "%d passed, %d failed" summary.Passed.Length summary.Failed.Length
        if summary.Failed.IsEmpty then 0 else 1

/// A JSONL trace sink under <workspace>/.iron/.
let private newTraceSink (root: string) =
    let dir = IO.Path.Combine(root, ".iron")
    IO.Directory.CreateDirectory dir |> ignore
    let path = IO.Path.Combine(dir, sprintf "trace-%s.jsonl" (DateTime.Now.ToString("yyyyMMdd-HHmmss")))
    let writer = new IO.StreamWriter(path, append = true, AutoFlush = true)
    writer, path

/// Ask on the terminal; deny when there is no terminal to ask.
let private ttyApprover (description: string) =
    if Console.IsInputRedirected then
        eprintfn "iron: denied (no terminal to ask on; use --yes): %s" description
        false
    else
        printf "approve %s? [y/N] " description
        match Console.ReadLine() with
        | null -> false
        | answer -> answer.Trim().ToLowerInvariant() = "y"

/// Interactive chat: one agent turn per user message, history persisted
/// after every turn so a session survives interruption and `--resume`.
let private runChat (resumeId: string option) (model: string option) =
    Console.OutputEncoding <- Encoding.UTF8
    let root = Environment.CurrentDirectory
    let id, initial =
        match resumeId with
        | Some requested ->
            (match requested, SessionStore.latest root with
             | "", None -> None
             | "", Some latest -> Some latest
             | given, _ -> Some given)
            |> function
               | None -> Error "no sessions to resume in this workspace"
               | Some id -> SessionStore.load root id |> Result.map (fun m -> id, m)
        | None -> Ok(SessionStore.newId (), Nil)
        |> function
           | Error message ->
               eprintfn "iron: %s" message
               exit 1
           | Ok pair -> pair
    let stream = ConsoleStream()
    let meter = UsageMeter()
    let bridge =
        match newBridge model (Some stream) meter with
        | Error message ->
            eprintfn "iron: %s" message
            exit 1
        | Ok bridge -> bridge
    let writer, _ = newTraceSink root
    let config =
        { Session.configIn root bridge with
            traceSink = Some writer.WriteLine
            agentSources = Session.agentPackageSources (Session.defaultAgentDir ())
            approver = Some ttyApprover }
    match Session.createWith config with
    | Choice1Of2 error ->
        eprintfn "Startup error: %s" (showError error)
        1
    | Choice2Of2 session ->
        printfn ""
        printfn " iron v%s — chat (session %s%s)" AgentEnv.version id
            (match initial with Nil -> "" | _ -> ", resumed")
        printfn " The agent works in %s; quit with 'exit' or ctrl-d." root
        printfn ""
        let mutable messages = initial
        let mutable running = true
        while running do
            printf "you> "
            match Console.ReadLine() with
            | null -> running <- false
            | line when line.Trim() = "" -> ()
            | line when [ "exit"; "quit" ] |> List.contains (line.Trim().ToLowerInvariant()) ->
                running <- false
            | line when line.Trim() = "/undo" ->
                match Git.undoLast root with
                | Ok subject -> printfn "undone: %s" subject
                | Error message -> eprintfn "iron: %s" message
            | line ->
                match Session.runChatTurn session messages line with
                | Choice1Of2 error -> eprintfn "error : %s" (showError error)
                | Choice2Of2 updated ->
                    messages <- updated
                    SessionStore.save root id messages
                    if meter.SawUsage then printfn "%s[%s]%s" "" meter.Line ""
        writer.Dispose()
        match messages with
        | Nil -> ()
        | _ -> printfn "session saved: %s (resume with: iron --resume %s)" id id
        0

let private runTask (autoApprove: bool) (agentDir: string option) (model: string option) (task: string) =
    let root = Environment.CurrentDirectory
    let stream = ConsoleStream()
    let meter = UsageMeter()
    let bridge =
        match newBridge model (Some stream) meter with
        | Error message ->
            eprintfn "iron: %s" message
            exit 1
        | Ok bridge -> bridge
    let writer, tracePath = newTraceSink root
    let agent =
        match agentDir with
        | Some dir -> dir
        | None -> Session.defaultAgentDir ()
    let config =
        { Session.configIn root bridge with
            traceSink = Some writer.WriteLine
            agentSources = Session.agentPackageSources agent
            approver = Some(if autoApprove then (fun _ -> true) else ttyApprover) }
    match Session.createWith config with
    | Choice1Of2 error ->
        eprintfn "Startup error: %s" (showError error)
        1
    | Choice2Of2 session ->
        let outcome = Session.runAgent session task
        writer.Dispose()
        match outcome with
        | Choice1Of2 error ->
            eprintfn "Agent error: %s" (showError error)
            eprintfn "Trace: %s" tracePath
            1
        | Choice2Of2 _ ->
            printfn ""
            if meter.SawUsage then printfn "%s" meter.Line
            printfn "Trace: %s" tracePath
            0

let private runUndo () =
    match Git.undoLast Environment.CurrentDirectory with
    | Ok subject ->
        printfn "undone: %s" subject
        0
    | Error message ->
        eprintfn "iron undo: %s" message
        1

let private banner () =
    printfn ""
    printfn " iron v%s — agent session REPL (safe profile)" AgentEnv.version
    printfn " Effects handled: iron/llm-call (configured provider), iron/tool-call (workspace tools)"
    printfn " Try: (response-text (perform iron/llm-call (list :messages (vector (list :role \"user\" :content \"hi\")))))"
    printfn ""

let private runRepl (model: string option) =
    Console.OutputEncoding <- Encoding.UTF8
    banner ()
    let bridge =
        match newBridge model None (UsageMeter()) with
        | Error message ->
            eprintfn "iron: %s" message
            exit 1
        | Ok bridge -> bridge
    match Session.create bridge with
    | Choice1Of2 error ->
        eprintfn "Startup error: %s" (showError error)
        1
    | Choice2Of2 session ->
        let run (line: string) =
            match Session.runSource session "repl" line with
            | Choice1Of2 error -> printfn "error : %s" (showError error)
            | Choice2Of2 value -> printfn "%s" (showVal value)
        Repl.until
            (fun line -> line.ToLowerInvariant().Equals("quit"))
            (Repl.readPrompt "iron> ")
            run
        0

let private runScript (path: string) (model: string option) =
    let stream = ConsoleStream()
    let meter = UsageMeter()
    let bridge =
        match newBridge model (Some stream) meter with
        | Error message ->
            eprintfn "iron: %s" message
            exit 1
        | Ok bridge -> bridge
    match Session.create bridge with
    | Choice1Of2 error ->
        eprintfn "Startup error: %s" (showError error)
        1
    | Choice2Of2 session ->
        match Session.runScriptFile session path with
        | Choice1Of2 error ->
            eprintfn "Script error: %s" (showError error)
            1
        | Choice2Of2 _ -> 0

/// Pull a global `--model <spec>` out of the argument list.
let private extractModel (args: string list) =
    let rec go acc model = function
        | "--model" :: spec :: rest -> go acc (Some spec) rest
        | arg :: rest -> go (arg :: acc) model rest
        | [] -> List.rev acc, model
    go [] None args

[<EntryPoint>]
let main argv =
    let args, model = extractModel (Array.toList argv)
    match args with
    | ["--version"] | ["version"] ->
        printfn "%s" AgentEnv.version
        0
    | [] when not Console.IsInputRedirected ->
        runChat None model
    | ["--resume"] -> runChat (Some "") model
    | ["--resume"; id] -> runChat (Some id) model
    | ["repl"] ->
        runRepl model
    | "run" :: rest ->
        // iron run [--yes] [--agent <dir>] "task"
        let rec parse yes agent = function
            | "--yes" :: more -> parse true agent more
            | "--agent" :: dir :: more -> parse yes (Some dir) more
            | [task] -> Some(yes, agent, task)
            | _ -> None
        match parse false None rest with
        | Some(yes, agent, task) -> runTask yes agent model task
        | None ->
            eprintfn "usage: iron run [--yes] [--agent <dir>] [--model <spec>] \"task\""
            2
    | ["undo"] -> runUndo ()
    | ["eject"] ->
        // Copy the installed default agent into the workspace for editing.
        let source = Session.defaultAgentDir ()
        let target = IO.Path.Combine(Environment.CurrentDirectory, "agents", "default")
        if IO.Directory.Exists target then
            eprintfn "iron eject: '%s' already exists" target
            1
        elif not (IO.Directory.Exists source) then
            eprintfn "iron eject: no installed default agent at '%s'" source
            1
        else
            for file in IO.Directory.EnumerateFiles(source, "*", IO.SearchOption.AllDirectories) do
                let destination = IO.Path.Combine(target, IO.Path.GetRelativePath(source, file))
                IO.Directory.CreateDirectory(IO.Path.GetDirectoryName destination) |> ignore
                IO.File.Copy(file, destination)
            printfn "ejected the default agent to %s" target
            printfn "run it with: iron run --agent %s \"task\"" (IO.Path.GetRelativePath(Environment.CurrentDirectory, target))
            0
    | ["test"] -> runTests None false model
    | ["test"; "--record"] -> runTests None true model
    | ["test"; dir] -> runTests (Some dir) false model
    | ["test"; dir; "--record"] | ["test"; "--record"; dir] -> runTests (Some dir) true model
    | ["script"; path] ->
        runScript path model
    | [] ->
        printf "%s" usage
        0
    | other ->
        eprintfn "Unknown arguments: %s" (String.Join(" ", other))
        eprintfn ""
        eprintf "%s" usage
        2
