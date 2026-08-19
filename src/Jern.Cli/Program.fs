module Jern.Cli.Program

open System
open System.Text
open IronKernel
open IronKernel.Ast
open IronKernel.Errors
open Jern.Host

let private usage = """jern — a terminal coding agent whose brain is IronKernel source

Usage:
  jern                Interactive chat session in the current workspace
                      (persisted to .jern/sessions/)
  jern --resume [id]  Continue the latest (or the given) chat session
  jern run [--yes] [--agent <dir>] "task"
                      One-shot agentic task in the current workspace
                      (trace in .jern/)
                      --yes approves policy-gated actions (writes, shell)
                      --agent runs a different agent package (the headline)
  jern ui [--port n]  The chat session as a local web app (127.0.0.1):
                      streaming replies, tool activity, and approval cards
                      with diffs. The page is ui/index.html beside the
                      binary — edit it like everything else
  jern undo           Revert the last jern-authored commit (also /undo in chat)
  jern eject          Copy the default agent's source into ./agents/default
  jern repl           Kernel REPL inside the agent's restricted environment
  jern script <file>  Run a .ikr script as agent code under the handler stack
  jern test [<agent-dir>] [--record]
                      Run an agent package's test suite. Replay is
                      deterministic and network-free; --record captures new
                      fixtures from the live provider
  jern mcp            Connect the configured MCP servers and list their tools
  jern policy [init]  Show the active tool policy; `init` writes a workspace
                      policy (.jern/policy.ikr) that overrides the built-in
                      rules for this repo — enforced, testable Kernel source
  jern --version      Print version

Models & providers:
  --model provider/model on any command (e.g. --model openai/gpt-5.2,
  ollama/qwen3, anthropic/claude-opus-5). Default and aliases come from
  jern.json / ~/.config/jern/config.json. Keys via provider env vars
  (ANTHROPIC_API_KEY, OPENAI_API_KEY, …); ollama and lmstudio need none.

Budgets:
  --budget <n> caps the run at n model calls (or set jern.json
  "budget": { "llm_calls": n, "tokens": m }). Enforced in the handler
  stack: on exhaustion the agent must ask you before continuing.

MCP:
  Add servers in jern.json — their tools join the agent's toolset as
  mcp__<server>__<tool>, ask-gated by the default policy:
    "mcp_servers": { "github": { "command": "npx",
                                 "args": ["-y", "@modelcontextprotocol/server-github"],
                                 "env": { "GITHUB_TOKEN": "…" } } }
"""

/// ANSI styling for the terminal, in the brand's palette (rust and steel on
/// iron). Colors turn off when stdout is redirected or NO_COLOR is set.
module private Style =
    let enabled =
        lazy (not Console.IsOutputRedirected
              && isNull (Environment.GetEnvironmentVariable "NO_COLOR"))
    let private wrap (code: string) (s: string) =
        if enabled.Value then sprintf "\x1b[%sm%s\x1b[0m" code s else s
    let rust s = wrap "1;38;5;173" s      // the brand accent, bold
    let steel s = wrap "38;5;110" s
    let dim s = wrap "2" s
    let bold s = wrap "1" s
    let red s = wrap "31" s
    let green s = wrap "32" s
    let yellow s = wrap "33" s

    /// Color a tool-call description: edit_file previews carry
    /// "  - old" / "  + new" lines.
    let describe (description: string) =
        description.Split('\n')
        |> Array.map (fun line ->
            if line.StartsWith "  - " then red line
            elif line.StartsWith "  + " then green line
            else line)
        |> String.concat "\n"

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

let private loadProviders () =
    match Providers.load Environment.CurrentDirectory with
    | Error message ->
        eprintfn "jern: %s" message
        exit 1
    | Ok config -> config

/// Pull a global `--budget <n>` (model-call cap) out of the argument list.
let private extractBudget (args: string list) =
    let rec go acc budget = function
        | "--budget" :: n :: rest ->
            (match Int32.TryParse(n: string) with
             | true, calls when calls > 0 -> go acc (Some calls) rest
             | _ ->
                 eprintfn "jern: --budget needs a positive integer, got '%s'" n
                 exit 2)
        | arg :: rest -> go (arg :: acc) budget rest
        | [] -> List.rev acc, budget
    go [] None args

/// The session budget plist: the CLI --budget (model calls) wins over
/// jern.json's "budget" object.
let private sessionBudget (providers: Providers.Config) (cliBudget: int option) =
    match cliBudget with
    | Some calls -> Providers.budget { providers with budgetLlmCalls = Some calls }
    | None -> Providers.budget providers

/// The provider-routing bridge for live commands. `interrupted` aborts a
/// streaming turn from inside the text callback.
let private routedBridge (config: Providers.Config) (model: string option)
                         (stream: ConsoleStream option) (meter: UsageMeter)
                         (interrupted: (unit -> bool) option) =
    let onText =
        stream
        |> Option.map (fun s ->
            fun piece ->
                match interrupted with
                | Some check when check () -> raise Interrupted
                | _ -> s.Write piece)
    Providers.createBridge config model onText |> meter.Wrap stream

let private newBridge (model: string option) (stream: ConsoleStream option) (meter: UsageMeter) =
    Ok(routedBridge (loadProviders ()) model stream meter None)

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
                eprintfn "jern test: %s" message
                exit 2
            | Ok bridge -> Fixtures.Record bridge
        else Fixtures.Replay
    match TestRunner.run agentDir mode with
    | Error message ->
        eprintfn "jern test: %s" message
        2
    | Ok summary ->
        for outcome in summary.outcomes do
            match outcome.error with
            | None -> printfn "%s - %s" (Style.green "ok  ") outcome.name
            | Some error ->
                printfn "%s - %s" (Style.red "FAIL") outcome.name
                printfn "       %s" (error.Replace("\n", "\n       "))
        printfn ""
        let verdict = sprintf "%d passed, %d failed" summary.Passed.Length summary.Failed.Length
        printfn "%s" (if summary.Failed.IsEmpty then Style.green verdict else Style.red verdict)
        if summary.Failed.IsEmpty then 0 else 1

/// A JSONL trace sink under <workspace>/.jern/.
let private newTraceSink (root: string) =
    let dir = IO.Path.Combine(root, ".jern")
    IO.Directory.CreateDirectory dir |> ignore
    let path = IO.Path.Combine(dir, sprintf "trace-%s.jsonl" (DateTime.Now.ToString("yyyyMMdd-HHmmss")))
    let writer = new IO.StreamWriter(path, append = true, AutoFlush = true)
    writer, path

/// Ask on the terminal; deny when there is no terminal to ask.
let private ttyApprover (description: string) =
    if Console.IsInputRedirected then
        eprintfn "jern: denied (no terminal to ask on; use --yes): %s" description
        false
    else
        printf "%s %s%s %s " (Style.yellow "approve") (Style.describe description)
            (Style.yellow "?") (Style.bold "[y/N]")
        match Console.ReadLine() with
        | null -> false
        | answer -> answer.Trim().ToLowerInvariant() = "y"

/// Interactive chat: one agent turn per user message, history persisted
/// after every turn so a session survives interruption and `--resume`.
let private runChat (resumeId: string option) (model: string option) (cliBudget: int option) =
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
               eprintfn "jern: %s" message
               exit 1
           | Ok pair -> pair
    let providers = loadProviders ()
    let stream = ConsoleStream()
    let meter = UsageMeter()
    let interrupted = ref false
    Console.CancelKeyPress.Add(fun e ->
        e.Cancel <- true
        interrupted.Value <- true)
    let writer, _ = newTraceSink root
    let mutable currentModel = model

    let makeSession () =
        let bridge =
            routedBridge providers currentModel (Some stream) meter (Some(fun () -> interrupted.Value))
        Session.createWith
            { Session.configIn root bridge with
                traceSink = Some writer.WriteLine
                agentSources = Session.agentPackageSources (Session.defaultAgentDir ())
                approver = Some ttyApprover
                agentConfig = Providers.agentConfig providers
                mcpServers = providers.mcpServers
                budget = sessionBudget providers cliBudget
                interrupted = fun () -> interrupted.Value }

    let effectiveModel () =
        match currentModel with
        | Some m -> m
        | None -> providers.defaultModel

    match makeSession () with
    | Choice1Of2 error ->
        eprintfn "Startup error: %s" (showError error)
        1
    | Choice2Of2 first ->
        let mutable session = first
        printfn ""
        printfn " %s %s — chat (%s%s, %s)" (Style.rust "jern") (Style.steel ("v" + AgentEnv.version))
            (Style.dim ("session " + id))
            (match initial with Nil -> "" | _ -> Style.dim ", resumed")
            (Style.steel (effectiveModel ()))
        printfn " %s" (Style.dim (root + " · /help for commands · 'exit' or ctrl-d to quit"))
        printfn ""
        let mutable messages = initial
        let mutable running = true
        while running do
            printf "%s " (Style.rust "you>")
            match Console.ReadLine() with
            | null -> running <- false
            | line when line.Trim() = "" -> interrupted.Value <- false
            | line when [ "exit"; "quit" ] |> List.contains (line.Trim().ToLowerInvariant()) ->
                running <- false
            | line when line.Trim().StartsWith "/" ->
                interrupted.Value <- false
                match line.Trim().Split(' ', 2) |> Array.toList with
                | ["/help"] | ["/help"; _] ->
                    printfn "/model [provider/model]  show or switch the model"
                    printfn "/undo                    revert the last jern commit"
                    printfn "/clear                   forget this conversation's history"
                    printfn "/cost                    token totals for this session"
                    printfn "/help                    this list"
                | ["/undo"] ->
                    match Git.undoLast root with
                    | Ok subject -> printfn "undone: %s" subject
                    | Error message -> eprintfn "jern: %s" message
                | ["/model"] ->
                    printfn "model: %s" (effectiveModel ())
                | ["/model"; spec] ->
                    match Providers.resolve providers (spec.Trim()) with
                    | Error message -> eprintfn "jern: %s" message
                    | Ok _ ->
                        currentModel <- Some(spec.Trim())
                        match makeSession () with
                        | Choice1Of2 error -> eprintfn "jern: %s" (showError error)
                        | Choice2Of2 fresh ->
                            session <- fresh
                            printfn "model: %s" (effectiveModel ())
                | ["/clear"] ->
                    messages <- Nil
                    SessionStore.save root id messages
                    printfn "history cleared"
                | ["/cost"] ->
                    printfn "%s" meter.Line
                | command :: _ ->
                    eprintfn "jern: unknown command %s (try /help)" command
                | [] -> ()
            | line ->
                interrupted.Value <- false
                match Session.runChatTurn session messages line with
                | Choice1Of2 error when interrupted.Value ->
                    printfn "%s" (Style.yellow (sprintf "interrupted — turn discarded (%s)" (showError error)))
                | Choice1Of2 error -> eprintfn "%s" (Style.red (sprintf "error : %s" (showError error)))
                | Choice2Of2 updated ->
                    messages <- updated
                    SessionStore.save root id messages
                    printfn "%s" (Style.dim (sprintf "[%s · %s · session %s]" (effectiveModel ()) meter.Line id))
        writer.Dispose()
        match messages with
        | Nil -> ()
        | _ -> printfn "%s" (Style.dim (sprintf "session saved: %s (resume with: jern --resume %s)" id id))
        0

let private runTask (autoApprove: bool) (agentDir: string option) (model: string option) (cliBudget: int option) (task: string) =
    let root = Environment.CurrentDirectory
    let providers = loadProviders ()
    let stream = ConsoleStream()
    let meter = UsageMeter()
    let bridge = routedBridge providers model (Some stream) meter None
    let writer, tracePath = newTraceSink root
    let agent =
        match agentDir with
        | Some dir -> dir
        | None -> Session.defaultAgentDir ()
    let config =
        { Session.configIn root bridge with
            traceSink = Some writer.WriteLine
            agentSources = Session.agentPackageSources agent
            approver = Some(if autoApprove then (fun _ -> true) else ttyApprover)
            agentConfig = Providers.agentConfig providers
            mcpServers = providers.mcpServers
            budget = sessionBudget providers cliBudget }
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
            if meter.SawUsage then printfn "%s" (Style.dim meter.Line)
            printfn "%s" (Style.dim ("Trace: " + tracePath))
            0

/// Connect every configured MCP server and print its tools — the setup
/// debugging loop for jern.json "mcp_servers".
let private runMcp () =
    let providers = loadProviders ()
    match providers.mcpServers with
    | [] ->
        printfn "no MCP servers configured (add \"mcp_servers\" to jern.json)"
        0
    | specs ->
        let mutable failures = 0
        for spec in specs do
            let argsSummary =
                let flat = String.Join(" ", spec.args).Replace("\n", " ")
                if flat.Length > 60 then flat.Substring(0, 57) + "…" else flat
            printfn "%s: %s %s" spec.name spec.command argsSummary
            match Mcp.connect spec with
            | Error reason ->
                failures <- failures + 1
                printfn "  FAILED: %s" reason
            | Ok server ->
                match Mcp.listTools server with
                | Error reason ->
                    failures <- failures + 1
                    printfn "  connected, but tools/list failed: %s" reason
                | Ok descriptors ->
                    if descriptors.IsEmpty then printfn "  (no tools)"
                    for d in descriptors do
                        let get key =
                            match Tools.plistTryGet key d with
                            | Some (Obj (:? string as s)) -> s
                            | _ -> ""
                        let description = get "description"
                        let summary =
                            let line = description.Split('\n').[0]
                            if line.Length > 80 then line.Substring(0, 77) + "…" else line
                        printfn "  %s — %s" (get "name") summary
                Mcp.shutdown server
        if failures = 0 then 0 else 1

let private policyTemplate = """; Workspace tool policy — loaded after the built-in kernel/policy.ikr and
; overrides what it redefines. Decisions: :allow, :ask, or a reason string
; (a denial the model sees). Helpers in scope: call-path, call-command,
; (path-within? call "src/"), (command-is? call "pytest"),
; string-prefix?/string-suffix?/string-contains?.
;
; This file runs privileged. Review it in repositories you did not author,
; the same way you review a repo's test_command.

(define tool-policy
  (lambda (call)
    (sequence
      (define name (plist-get call :name))
      (cond ; ((path-within? call "src/") :allow)      ; scope edits to src/
            ; ((command-is? call "pytest") :allow)     ; allowlist a command
            ; ((equal? name "mcp__github__get_issue") :allow)
            ((equal? name "shell") :ask)
            ((equal? name "edit_file") :ask)
            ((equal? name "read_file") :allow)
            ((equal? name "list_dir") :allow)
            ((equal? name "file_tree") :allow)
            ((equal? name "grep") :allow)
            (#t :ask)))))
"""

/// `jern policy` — show which policy governs this workspace;
/// `jern policy init` — write the workspace override template.
let private runPolicy (init: bool) =
    let root = Environment.CurrentDirectory
    let workspacePath = IO.Path.Combine(root, ".jern", "policy.ikr")
    if init then
        if IO.File.Exists workspacePath then
            eprintfn "jern policy init: '%s' already exists" workspacePath
            1
        else
            IO.Directory.CreateDirectory(IO.Path.GetDirectoryName workspacePath) |> ignore
            IO.File.WriteAllText(workspacePath, policyTemplate)
            printfn "wrote %s" workspacePath
            printfn "it now governs every jern session in this workspace; edit and re-run"
            0
    else
        if IO.File.Exists workspacePath then
            printfn "workspace policy: %s" workspacePath
            printfn ""
            printf "%s" (IO.File.ReadAllText workspacePath)
        else
            let builtin = Session.kernelFile "policy.ikr"
            printfn "built-in policy: %s (override with: jern policy init)" builtin
            printfn ""
            printf "%s" (IO.File.ReadAllText builtin)
        0

/// `jern ui` — serve the chat session as a local web app and open it.
let private runUi (model: string option) (cliBudget: int option) (port: int) =
    let root = Environment.CurrentDirectory
    let providers = loadProviders ()
    let modelLabel =
        match model with
        | Some m -> m
        | None -> providers.defaultModel
    let server =
        Ui.start
            { root = root
              // Streaming goes to the browser, not the console; the Ui
              // server does its own usage metering for the header.
              makeBridge = fun onText -> Providers.createBridge providers model (Some onText)
              agentSources = Session.agentPackageSources (Session.defaultAgentDir ())
              agentConfig = Providers.agentConfig providers
              mcpServers = providers.mcpServers
              budget = sessionBudget providers cliBudget
              modelLabel = modelLabel
              port = port }
    printfn " %s %s — ui at %s" (Style.rust "jern") (Style.steel ("v" + AgentEnv.version)) (Style.bold server.url)
    printfn " %s" (Style.dim (root + " · ctrl-c to stop"))
    try
        Diagnostics.Process.Start(Diagnostics.ProcessStartInfo(server.url, UseShellExecute = true)) |> ignore
    with _ -> ()
    server.run ()
    0

let private runUndo () =
    match Git.undoLast Environment.CurrentDirectory with
    | Ok subject ->
        printfn "undone: %s" subject
        0
    | Error message ->
        eprintfn "jern undo: %s" message
        1

let private banner () =
    printfn ""
    printfn " jern v%s — agent session REPL (safe profile)" AgentEnv.version
    printfn " Effects handled: jern/llm-call (configured provider), jern/tool-call (workspace tools)"
    printfn " Try: (response-text (perform jern/llm-call (list :messages (vector (list :role \"user\" :content \"hi\")))))"
    printfn ""

let private runRepl (model: string option) =
    Console.OutputEncoding <- Encoding.UTF8
    banner ()
    let bridge =
        match newBridge model None (UsageMeter()) with
        | Error message ->
            eprintfn "jern: %s" message
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
            (Repl.readPrompt "jern> ")
            run
        0

let private runScript (path: string) (model: string option) =
    let stream = ConsoleStream()
    let meter = UsageMeter()
    let bridge =
        match newBridge model (Some stream) meter with
        | Error message ->
            eprintfn "jern: %s" message
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
    let args, cliBudget = extractBudget args
    match args with
    | ["--version"] | ["version"] ->
        printfn "%s" AgentEnv.version
        0
    | [] when not Console.IsInputRedirected ->
        runChat None model cliBudget
    | ["--resume"] -> runChat (Some "") model cliBudget
    | ["--resume"; id] -> runChat (Some id) model cliBudget
    | ["repl"] ->
        runRepl model
    | "run" :: rest ->
        // jern run [--yes] [--agent <dir>] "task"
        let rec parse yes agent = function
            | "--yes" :: more -> parse true agent more
            | "--agent" :: dir :: more -> parse yes (Some dir) more
            | [task] -> Some(yes, agent, task)
            | _ -> None
        match parse false None rest with
        | Some(yes, agent, task) -> runTask yes agent model cliBudget task
        | None ->
            eprintfn "usage: jern run [--yes] [--agent <dir>] [--model <spec>] [--budget <n>] \"task\""
            2
    | ["undo"] -> runUndo ()
    | ["ui"] -> runUi model cliBudget 0
    | ["ui"; "--port"; p] ->
        (match Int32.TryParse p with
         | true, port -> runUi model cliBudget port
         | _ ->
             eprintfn "jern ui: --port needs a number, got '%s'" p
             2)
    | ["mcp"] -> runMcp ()
    | ["policy"] -> runPolicy false
    | ["policy"; "init"] -> runPolicy true
    | ["eject"] ->
        // Copy the installed default agent into the workspace for editing.
        let source = Session.defaultAgentDir ()
        let target = IO.Path.Combine(Environment.CurrentDirectory, "agents", "default")
        if IO.Directory.Exists target then
            eprintfn "jern eject: '%s' already exists" target
            1
        elif not (IO.Directory.Exists source) then
            eprintfn "jern eject: no installed default agent at '%s'" source
            1
        else
            for file in IO.Directory.EnumerateFiles(source, "*", IO.SearchOption.AllDirectories) do
                let destination = IO.Path.Combine(target, IO.Path.GetRelativePath(source, file))
                IO.Directory.CreateDirectory(IO.Path.GetDirectoryName destination) |> ignore
                IO.File.Copy(file, destination)
            printfn "ejected the default agent to %s" target
            printfn "run it with: jern run --agent %s \"task\"" (IO.Path.GetRelativePath(Environment.CurrentDirectory, target))
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
