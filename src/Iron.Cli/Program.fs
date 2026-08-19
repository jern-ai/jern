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
                      (persisted to .iron/sessions/; needs ANTHROPIC_API_KEY)
  iron --resume [id]  Continue the latest (or the given) chat session
  iron run [--yes] [--agent <dir>] "task"
                      One-shot agentic task in the current workspace
                      (needs ANTHROPIC_API_KEY; trace in .iron/)
                      --yes approves policy-gated actions (writes, shell)
                      --agent runs a different agent package (the headline)
  iron eject          Copy the default agent's source into ./agents/default
  iron repl           Kernel REPL inside the agent's restricted environment
  iron script <file>  Run a .ikr script as agent code under the handler stack
  iron --version      Print version

  iron test [<agent-dir>] [--record]
                      Run an agent package's test suite. Replay is
                      deterministic and network-free; --record captures new
                      fixtures from the live provider (needs ANTHROPIC_API_KEY)

Coming (see docs/implementation-plan.md):
  iron             Interactive chat session
"""

let private runTests (dirArg: string option) (record: bool) =
    let agentDir =
        match dirArg with
        | Some dir -> dir
        | None ->
            let local = IO.Path.Combine(Environment.CurrentDirectory, "agents", "default")
            if IO.Directory.Exists local then local else Session.defaultAgentDir ()
    let mode =
        if record then Fixtures.Record AnthropicBridge.call else Fixtures.Replay
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
let private runChat (resumeId: string option) =
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
    let writer, _ = newTraceSink root
    let config =
        { Session.configIn root AnthropicBridge.call with
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
            | line ->
                match Session.runChatTurn session messages line with
                | Choice1Of2 error -> eprintfn "error : %s" (showError error)
                | Choice2Of2 updated ->
                    messages <- updated
                    SessionStore.save root id messages
        writer.Dispose()
        match messages with
        | Nil -> ()
        | _ -> printfn "session saved: %s (resume with: iron --resume %s)" id id
        0

let private runTask (autoApprove: bool) (agentDir: string option) (task: string) =
    let root = Environment.CurrentDirectory
    let writer, tracePath = newTraceSink root
    let agent =
        match agentDir with
        | Some dir -> dir
        | None -> Session.defaultAgentDir ()
    let config =
        { Session.configIn root AnthropicBridge.call with
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
            printfn "Trace: %s" tracePath
            0

let private banner () =
    printfn ""
    printfn " iron v%s — agent session REPL (safe profile)" AgentEnv.version
    printfn " Effects handled: iron/llm-call (Anthropic), iron/tool-call (workspace tools)"
    printfn " Try: (response-text (perform iron/llm-call (list :messages (vector (list :role \"user\" :content \"hi\")))))"
    printfn ""

let private runRepl () =
    Console.OutputEncoding <- Encoding.UTF8
    banner ()
    match Session.create AnthropicBridge.call with
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

[<EntryPoint>]
let main argv =
    match Array.toList argv with
    | ["--version"] | ["version"] ->
        printfn "%s" AgentEnv.version
        0
    | [] when not Console.IsInputRedirected ->
        runChat None
    | ["--resume"] -> runChat (Some "")
    | ["--resume"; id] -> runChat (Some id)
    | ["repl"] ->
        runRepl ()
    | "run" :: rest ->
        // iron run [--yes] [--agent <dir>] "task"
        let rec parse yes agent = function
            | "--yes" :: more -> parse true agent more
            | "--agent" :: dir :: more -> parse yes (Some dir) more
            | [task] -> Some(yes, agent, task)
            | _ -> None
        match parse false None rest with
        | Some(yes, agent, task) -> runTask yes agent task
        | None ->
            eprintfn "usage: iron run [--yes] [--agent <dir>] \"task\""
            2
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
    | ["test"] -> runTests None false
    | ["test"; "--record"] -> runTests None true
    | ["test"; dir] -> runTests (Some dir) false
    | ["test"; dir; "--record"] | ["test"; "--record"; dir] -> runTests (Some dir) true
    | ["script"; path] ->
        match Session.create AnthropicBridge.call with
        | Choice1Of2 error ->
            eprintfn "Startup error: %s" (showError error)
            1
        | Choice2Of2 session ->
            match Session.runScriptFile session path with
            | Choice1Of2 error ->
                eprintfn "Script error: %s" (showError error)
                1
            | Choice2Of2 _ -> 0
    | [] ->
        printf "%s" usage
        0
    | other ->
        eprintfn "Unknown arguments: %s" (String.Join(" ", other))
        eprintfn ""
        eprintf "%s" usage
        2
