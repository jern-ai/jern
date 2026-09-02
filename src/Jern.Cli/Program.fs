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
  jern ui [--port n] [--agent <dir>]
                      The chat session as a local web app (127.0.0.1),
                      guarded by a startup token in the printed URL:
                      streaming replies, tool activity, approval cards with
                      diffs, a brain editor with one-click test runs, and
                      settings for models and API keys
  jern undo           Revert the last jern-authored commit (also /undo in chat)
  jern eject          Copy the default agent's source into ./agents/default
  jern repl           Kernel REPL inside the agent's restricted environment
  jern script <file>  Run a .ikr script as agent code under the handler stack
  jern test [<agent-dir>] [--record]
                      Run an agent package's test suite. Replay is
                      deterministic and network-free; --record captures new
                      fixtures from the live provider
  jern replay <trace.jsonl> [--policy <file>] [--agent <dir>]
                      Re-run a recorded session offline — model and tool
                      effects answer from the trace, nothing touches the
                      network or the workspace. Swap in a policy file or an
                      edited agent to see exactly where and how the run
                      would have diverged
  jern receipt [<trace.jsonl>] [--md | --json]
                      What a run did: model calls and tokens against budget,
                      tools used, files touched, policy decisions, and the
                      trace it came from. Printed after every `jern run`;
                      --md is ready to paste into a pull request
  jern golden record "task" [--slug <name>]
  jern golden check [--filter <slug>] [--md]
  jern golden list
                      Golden sessions: record a real task once, then check
                      it forever offline. `check` replays every recording
                      against the current agent and policy — a behavior
                      change fails with the exact divergence — and enforces
                      the declarative assertions in each recording's sidecar
                      (edits_within, no_tools, max_files_edited,
                      max_llm_calls, max_tokens), which survive re-recording
  jern mcp            Connect the configured MCP servers and list their tools
  jern policy [init | --show-compiled]
                      Show the effective policy and where each rule came
                      from; `init` writes a workspace policy
                      (.jern/policy.ikr); `--show-compiled` prints the Kernel
                      source that jern.json's "policy" object compiles to
  jern --version      Print version

Flag order is free: global flags (--model, --budget, --auto, --think,
--effort, --policy-baseline, --policy-trust) and each command's own flags
may appear anywhere on the line
(e.g. jern run "fix the tests" --agent agents/reviewer).

Models & providers:
  --model provider/model on any command (e.g. --model openai/gpt-5.2,
  ollama/qwen3, anthropic/claude-opus-5). Default and aliases come from
  jern.json / ~/.config/jern/config.json. Keys via provider env vars
  (ANTHROPIC_API_KEY, OPENAI_API_KEY, …); ollama and lmstudio need none.

Reasoning:
  --think <tokens> turns on Anthropic extended thinking with that budget;
  --effort low|medium|high sets reasoning effort for OpenAI-style
  reasoning models (o-series, R1, …). Or set jern.json
  "thinking_tokens" / "reasoning_effort". Thinking rides the request
  from agent source; each provider bridge consumes its own knob.

Approvals:
  --auto on any command auto-approves what the policy would ask about
  (explicit denials still deny). At the interactive prompt, answer `a`
  to approve and stop asking for the session — about that command word
  for shell (`a` on `shell: git status` covers later git invocations,
  nothing else), about that tool otherwise. The prompt shows exactly
  what is remembered.

Budgets:
  --budget <n> caps the run at n model calls (or set jern.json
  "budget": { "llm_calls": n, "tokens": m }). Enforced in the handler
  stack: on exhaustion the agent must ask you before continuing.

Policy:
  A "policy" object in jern.json enforces repository rules without any
  Kernel — restrictions apply on sight, grants are confirmed once:
    "policy": { "edits_within": ["src/"], "shell_allow": ["pytest"],
                "deny": ["mcp__*"], "memory": "ask" }
  Restrictions compose by severity, so nothing loaded later (a grant, a
  hand-written .jern/policy.ikr) can turn a denial into an approval.
  --policy-baseline <file> supplies rules the checkout may tighten but not
  weaken (CI points this at base-branch data, never at the PR's own tree);
  --policy-trust <sha256> blesses a policy's grants in an unattended run,
  where jern never prompts. See jern policy.

MCP:
  Add servers in jern.json — their tools join the agent's toolset as
  mcp__<server>__<tool>, ask-gated by the default policy:
    "mcp_servers": { "github": { "command": "npx",
                                 "args": ["-y", "@modelcontextprotocol/server-github"],
                                 "env": { "GITHUB_TOKEN": "…" } } }

Docs: https://jern.ai/docs/
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

/// The receipt's colors, in the terminal's palette.
let private receiptPalette : Receipt.Palette =
    { title = Style.rust
      label = Style.steel
      dim = Style.dim
      good = Style.green
      bad = Style.red }

let mutable private cliThink : int option = None
let mutable private cliEffort : string option = None
let mutable private cliPolicyBaseline : string option = None
let mutable private cliPolicyTrust : string list = []

let private loadProviders () =
    match Providers.load Environment.CurrentDirectory with
    | Error message ->
        eprintfn "jern: %s" message
        exit 1
    | Ok config ->
        Tools.configureLimits config.limits
        { config with
            thinkingTokens = (match cliThink with Some t -> Some t | None -> config.thinkingTokens)
            reasoningEffort = (match cliEffort with Some e -> Some e | None -> config.reasoningEffort) }

/// The session budget plist: the CLI --budget (model calls) wins over
/// jern.json's "budget" object.
let private sessionBudget (providers: Providers.Config) (cliBudget: int option) =
    match cliBudget with
    | Some calls -> Providers.budget { providers with budgetLlmCalls = Some calls }
    | None -> Providers.budget providers

type private CloudRunContext =
    { runId: string
      tokenBudget: Session.HardTokenBudget }

let private cloudRunContext () =
    let variable name =
        match Environment.GetEnvironmentVariable name with
        | null | "" -> None
        | value -> Some value
    match variable "JERN_CLOUD_RUN_ID", variable "JERN_CLOUD_TOKEN_CAP" with
    | None, None -> Ok None
    | Some _, None | None, Some _ ->
        Error "JERN_CLOUD_RUN_ID and JERN_CLOUD_TOKEN_CAP must be set together"
    | Some runId, Some rawCap ->
        if not (Text.RegularExpressions.Regex.IsMatch(runId, "^run_[0-9a-f]{32}$")) then
            Error "JERN_CLOUD_RUN_ID must have the form run_<32 lowercase hex characters>"
        else
            match Int64.TryParse rawCap with
            | true, cap when cap > 0L ->
                Ok(Some { runId = runId; tokenBudget = Session.HardTokenBudget cap })
            | _ -> Error "JERN_CLOUD_TOKEN_CAP must be a positive 64-bit integer"

/// The protected policy baseline (`--policy-baseline <file>`): rules this
/// checkout may tighten but never weaken. CI points it at base-branch or
/// workflow-owned data — never at the pull request's own tree, which is the
/// whole point. The file may be a bare policy object or a jern.json-shaped
/// file with a "policy" key, so a workflow can reuse a checked-in config.
let private baselineSource () =
    match cliPolicyBaseline with
    | None -> None
    | Some file ->
        if not (IO.File.Exists file) then
            eprintfn "jern: --policy-baseline '%s' does not exist" file
            exit 2
        let node =
            try
                let doc = Text.Json.Nodes.JsonNode.Parse(IO.File.ReadAllText file: string)
                match doc with
                | :? Text.Json.Nodes.JsonObject as o when not (isNull o.["policy"]) -> o.["policy"]
                | other -> other
            with ex ->
                eprintfn "jern: --policy-baseline '%s' is not readable JSON: %s" file ex.Message
                exit 2
        match PolicyConfig.parse node with
        | Error message ->
            eprintfn "jern: --policy-baseline '%s': %s" file message
            exit 2
        | Ok policy ->
            Some { PolicyConfig.origin = PolicyConfig.Baseline(IO.Path.GetFileName file)
                   PolicyConfig.policy = policy }

/// Every policy source for this run. The baseline goes first: it is the
/// layer nothing in the checkout may weaken.
let private policySources (providers: Providers.Config) =
    (baselineSource () |> Option.toList) @ providers.policySources

/// Has this source's grant half already been blessed — by a `--policy-trust`
/// pin from the workflow, or by an earlier yes in the trust store? Asks
/// nothing; `jern policy` and headless runs use it as-is.
let private grantsAlreadyTrusted (identity: string) (canonical: string) =
    let digest = Trust.contentHash canonical
    cliPolicyTrust |> List.exists (fun pin -> pin.Trim().ToLowerInvariant() = digest)
    || Trust.isTrusted (Trust.defaultStorePath ()) identity canonical

/// First-use trust for the *grant* half of a repository-supplied policy.
/// Restrictions never come here — tightening is free. Loosening is not: a
/// cloned repo's jern.json can grant permissions exactly like its
/// policy.ikr can, so it is shown and confirmed once. Declining (or having
/// no terminal) drops the grants and keeps the restrictions.
let private ttyPolicyGrantTrust (identity: string) (canonical: string) =
    if grantsAlreadyTrusted identity canonical then true
    else
        let digest = Trust.contentHash canonical
        if Console.IsInputRedirected then
            // Session names the source it dropped; add only the remedy.
            eprintfn "jern: to allow that policy's grants in an unattended run: --policy-trust %s" digest
            false
        else
            let rule = Style.dim (String.replicate 60 "─")
            printfn ""
            printfn "%s" (Style.yellow (sprintf "This workspace's policy grants extra permissions: %s" identity))
            printfn "%s" (Style.dim "Its restrictions apply either way; only the relaxations need your yes.")
            printfn "%s" rule
            printfn "%s" canonical
            printfn "%s" rule
            printf "%s %s " (Style.yellow "trust these policy grants?") (Style.bold "[y/N]")
            match Console.ReadLine() with
            | null -> false
            | answer when answer.Trim().ToLowerInvariant() = "y" ->
                Trust.remember (Trust.defaultStorePath ()) identity canonical
                true
            | _ -> false

/// Open the run envelope on a trace: the header a receipt is read from.
/// Everything here is what the run was *configured* with, so a summary never
/// has to infer it from the effects that followed.
let private openRun (sink: string -> unit) (providers: Providers.Config) (runId: string)
                    (command: string) (task: string option) (model: string option)
                    (agent: string) (cliBudget: int option) (cloudTokenCap: int64 option) =
    Trace.openRun sink
        { runId = runId
          command = command
          task = task
          model = (match model with Some m -> m | None -> providers.defaultModel)
          agent = agent
          budgetLlmCalls = (match cliBudget with Some n -> Some n | None -> providers.budgetLlmCalls)
          budgetTokens = providers.budgetTokens
          cloudTokenCap = cloudTokenCap
          sandbox = Tools.sandboxMode ()
          policy =
            policySources providers
            |> List.map (fun source ->
                { Trace.source = PolicyConfig.originLabel source.origin
                  Trace.digest = PolicyConfig.digest source.policy
                  Trace.isProtected =
                    match source.origin with PolicyConfig.Baseline _ -> true | _ -> false }) }

/// Print the receipt for a finished (or in-progress) trace.
let private showReceipt (tracePath: string) =
    match Receipt.ofTrace tracePath with
    | Error message -> eprintfn "jern: %s" message
    | Ok summary ->
        printfn ""
        printf "%s" (Receipt.render receiptPalette summary)

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
    let check = defaultArg interrupted (fun () -> false)
    Providers.createBridgeWith config model onText check |> meter.Wrap stream

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
let private newTraceSink (root: string) (requestedRunId: string option) =
    let dir = IO.Path.Combine(root, ".jern")
    IO.Directory.CreateDirectory dir |> ignore
    let runId = defaultArg requestedRunId (DateTime.Now.ToString("yyyyMMdd-HHmmss"))
    let path = IO.Path.Combine(dir, sprintf "trace-%s.jsonl" runId)
    let writer = new IO.StreamWriter(path, append = true, AutoFlush = true)
    writer, path, runId

/// Ask on the terminal; deny when there is no terminal to ask. `a` answers
/// yes and stops asking about that tool for the rest of the session.
let private makeTtyApprover (auto: bool) =
    let memory = Approvals.Memory(auto)
    fun (description: string) ->
        if memory.Covers description then
            printfn "%s %s" (Style.dim "auto-approved:") (Style.dim (Approvals.key description))
            true
        elif Console.IsInputRedirected then
            eprintfn "jern: denied (no terminal to ask on; use --auto): %s" description
            false
        else
            // The 'a' answer whitelists Approvals.key's unit (the command
            // word for shell, the tool otherwise) — show it, so the user
            // knows exactly what stops being asked about.
            printf "%s %s%s %s " (Style.yellow "approve") (Style.describe description)
                (Style.yellow "?") (Style.bold (sprintf "[y/N/a=always '%s']" (Approvals.key description)))
            match Console.ReadLine() with
            | null -> false
            | answer ->
                match answer.Trim().ToLowerInvariant() with
                | "y" -> true
                | "a" ->
                    memory.RememberAlways description
                    true
                | _ -> false

/// First-use trust for the workspace policy (.jern/policy.ikr). It runs
/// privileged, so an unseen (or changed) policy is shown and confirmed on
/// the terminal before a session loads it. A yes persists in
/// ~/.config/jern/trusted.json keyed by absolute path + content hash;
/// declining — or having no terminal to ask on — skips the workspace policy
/// for this session and the built-in rules stand.
let private ttyPolicyTrust (path: string) (content: string) =
    let store = Trust.defaultStorePath ()
    if Trust.isTrusted store path content then true
    elif Console.IsInputRedirected then
        eprintfn "jern: workspace policy %s is not trusted yet — run jern interactively once to review it" path
        false
    else
        let rule = Style.dim (String.replicate 60 "─")
        printfn ""
        printfn "%s" (Style.yellow (sprintf "This workspace provides its own tool policy: %s" path))
        printfn "%s" (Style.dim "It runs with jern's full authority and can loosen approval rules.")
        printfn "%s" rule
        printf "%s" content
        if not (content.EndsWith "\n") then printfn ""
        printfn "%s" rule
        printf "%s %s " (Style.yellow "trust this workspace policy?") (Style.bold "[y/N]")
        match Console.ReadLine() with
        | null -> false
        | answer when answer.Trim().ToLowerInvariant() = "y" ->
            Trust.remember store path content
            true
        | _ -> false

/// Interactive chat: one agent turn per user message, history persisted
/// after every turn so a session survives interruption and `--resume`.
let private runChat (resumeId: string option) (model: string option) (cliBudget: int option) (auto: bool) =
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
    let writer, tracePath, runId = newTraceSink root None
    let mutable currentModel = model
    let finishRun =
        openRun writer.WriteLine providers runId "chat" None model
            (Session.defaultAgentDir ()) cliBudget None

    let makeSession () =
        let bridge =
            routedBridge providers currentModel (Some stream) meter (Some(fun () -> interrupted.Value))
        Session.createWith
            { Session.configIn root bridge with
                traceSink = Some writer.WriteLine
                agentSources = Session.agentPackageSources (Session.defaultAgentDir ())
                approver = Some(makeTtyApprover auto)
                agentConfig = Providers.agentConfig providers
                mcpServers = providers.mcpServers
                budget = sessionBudget providers cliBudget
                interrupted = (fun () -> interrupted.Value)
                policyTrust = ttyPolicyTrust
                policySources = policySources providers
                policyGrantTrust = ttyPolicyGrantTrust }

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
        printfn " %s" (Style.dim (root + " · /help for commands · 'exit' or ctrl-d to quit"
                                + (if auto then " · auto-approve on" else "")))
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
                    printfn "/receipt                 what this session has done so far"
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
                | ["/receipt"] ->
                    // The trace is flushed per line, so a mid-session
                    // receipt reads the run so far.
                    showReceipt tracePath
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
        finishRun (if interrupted.Value then Trace.Interrupted else Trace.Completed)
        writer.Dispose()
        match messages with
        | Nil -> ()
        | _ -> printfn "%s" (Style.dim (sprintf "session saved: %s (resume with: jern --resume %s)" id id))
        0

/// Run one task to completion, writing its trace wherever the caller says.
/// Shared by `jern run` and `jern golden record`: a golden recording is an
/// ordinary run whose trace is kept and committed.
let private runTaskTo (writer: IO.StreamWriter) (tracePath: string) (runId: string) (command: string)
                      (autoApprove: bool) (agentDir: string option) (model: string option)
                      (cliBudget: int option) (cloudTokenBudget: Session.HardTokenBudget option)
                      (task: string) =
    let root = Environment.CurrentDirectory
    let providers = loadProviders ()
    let stream = ConsoleStream()
    let meter = UsageMeter()
    let bridge = routedBridge providers model (Some stream) meter None
    let agent =
        match agentDir with
        | Some dir -> dir
        | None -> Session.defaultAgentDir ()
    let finishRun =
        openRun writer.WriteLine providers runId command (Some task) model agent cliBudget
            (cloudTokenBudget |> Option.map _.Limit)
    let config =
        { Session.configIn root bridge with
            traceSink = Some writer.WriteLine
            agentSources = Session.agentPackageSources agent
            approver = Some(if autoApprove then (fun _ -> true) else makeTtyApprover false)
            agentConfig = Providers.agentConfig providers
            mcpServers = providers.mcpServers
            budget = sessionBudget providers cliBudget
            hardTokenBudget = cloudTokenBudget
            policyTrust = ttyPolicyTrust
            policySources = policySources providers
            policyGrantTrust = ttyPolicyGrantTrust }
    match Session.createWith config with
    | Choice1Of2 error ->
        finishRun (Trace.Failed(showError error))
        writer.Dispose()
        eprintfn "Startup error: %s" (showError error)
        1
    | Choice2Of2 session ->
        let outcome = Session.runAgent session task
        (match outcome with
         | Choice1Of2 error -> finishRun (Trace.Failed(showError error))
         | Choice2Of2 _ -> finishRun Trace.Completed)
        writer.Dispose()
        match outcome with
        | Choice1Of2 error ->
            eprintfn "Agent error: %s" (showError error)
            showReceipt tracePath
            1
        | Choice2Of2 _ ->
            // The receipt carries the tokens, the files, the policy tally,
            // and the trace path — the evidence, not just the outcome.
            showReceipt tracePath
            0

let private runTask (autoApprove: bool) (agentDir: string option) (model: string option)
                    (cliBudget: int option) (task: string) =
    match cloudRunContext () with
    | Error message ->
        eprintfn "jern: %s" message
        2
    | Ok context ->
        let writer, tracePath, runId =
            newTraceSink Environment.CurrentDirectory (context |> Option.map _.runId)
        runTaskTo writer tracePath runId "run" autoApprove agentDir model cliBudget
            (context |> Option.map _.tokenBudget) task

/// `jern replay` — re-run a recorded trace offline, optionally with a
/// swapped policy or agent, and report the first divergence.
let private runReplay (tracePath: string) (policyFile: string option) (agentDir: string option) =
    if not (IO.File.Exists tracePath) then
        eprintfn "jern replay: trace '%s' does not exist" tracePath
        2
    else
        let providers = loadProviders ()
        let agent =
            match agentDir with
            | Some dir -> dir
            | None -> Session.defaultAgentDir ()
        printfn "%s %s" (Style.dim "replaying") tracePath
        match policyFile with
        | Some file -> printfn "%s %s" (Style.dim "with policy") file
        | None -> ()
        match agentDir with
        | Some dir -> printfn "%s %s" (Style.dim "with agent ") dir
        | None -> ()
        match Replay.run
                  { tracePath = tracePath
                    agentDir = agent
                    policyFile = policyFile
                    agentConfig = Providers.agentConfig providers
                    mcpServers = providers.mcpServers
                    policySources = policySources providers } with
        | Error message ->
            eprintfn "jern replay: %s" message
            2
        | Ok (Replay.Completed (llmCalls, toolCalls)) ->
            printfn "%s — %d model calls and %d tool calls re-ran exactly as recorded"
                (Style.green "no divergence") llmCalls toolCalls
            0
        | Ok (Replay.Diverged report) ->
            printfn "%s %s" (Style.red "✗") (Style.describe report)
            1

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


/// `jern receipt [<trace>]` — the evidence for a run, re-derived from its
/// trace. With no argument, the newest trace in this workspace.
let private runReceipt (tracePath: string option) (format: Args.ReceiptFormat) =
    let resolved =
        match tracePath with
        | Some path -> Some path
        | None -> Receipt.latestTrace Environment.CurrentDirectory
    match resolved with
    | None ->
        eprintfn "jern receipt: no traces in %s/.jern (run jern first)" Environment.CurrentDirectory
        2
    | Some path ->
        match Receipt.ofTrace path with
        | Error message ->
            eprintfn "jern receipt: %s" message
            2
        | Ok summary ->
            match format with
            | Args.Markdown -> printf "%s" (Receipt.renderMarkdown summary)
            | Args.Json -> printfn "%s" (Receipt.renderJson summary)
            | Args.Text -> printf "%s" (Receipt.render receiptPalette summary)
            0

// --- golden sessions -------------------------------------------------------

/// Replay one recording against the agent and policy in force *now*.
let private replayGolden (providers: Providers.Config) (agentDir: string option) (tracePath: string) =
    Replay.run
        { tracePath = tracePath
          agentDir = (match agentDir with Some dir -> dir | None -> Session.defaultAgentDir ())
          policyFile = None
          agentConfig = Providers.agentConfig providers
          mcpServers = providers.mcpServers
          policySources = policySources providers }

/// `jern golden record "task"` — run the task for real once and keep the
/// trace as a committed snapshot of how the agent handles it.
let private runGoldenRecord (task: string) (slug: string option) (autoApprove: bool)
                            (agentDir: string option) (model: string option) (cliBudget: int option) =
    let root = Environment.CurrentDirectory
    let slug = match slug with Some s -> Golden.slugify s | None -> Golden.slugify task
    let dir = Golden.directory root
    IO.Directory.CreateDirectory dir |> ignore
    let tracePath = IO.Path.Combine(dir, slug + ".jsonl")
    let metadataPath = IO.Path.Combine(dir, slug + ".json")
    let rerecording = IO.File.Exists tracePath
    // Keep any assertions the sidecar already carries: re-recording blesses
    // new bytes, never the loss of a rule.
    let existing =
        if IO.File.Exists metadataPath then
            match Golden.parseMetadata (IO.File.ReadAllText metadataPath) with
            | Ok metadata -> metadata.assertions
            | Error message ->
                eprintfn "jern golden: %s (keeping no assertions)" message
                Golden.noAssertions
        else Golden.noAssertions
    if rerecording then IO.File.Delete tracePath
    let writer = new IO.StreamWriter(tracePath, append = false, AutoFlush = true)
    let code = runTaskTo writer tracePath slug "golden" autoApprove agentDir model cliBudget None task
    if code = 0 then
        IO.File.WriteAllText(
            metadataPath,
            Golden.metadataJson { task = task; recordedWith = AgentEnv.version; assertions = existing })
        printfn ""
        printfn "%s %s" (Style.green(if rerecording then "re-recorded" else "recorded")) (Style.bold slug)
        printfn "%s" (Style.dim (sprintf "  %s" (IO.Path.GetRelativePath(root, tracePath))))
        printfn "%s" (Style.dim (sprintf "  %s — commit both; add assertions under \"assert\" to protect meaning"
                                     (IO.Path.GetRelativePath(root, metadataPath))))
        printfn "%s" (Style.dim "  check it any time with: jern golden check")
    else
        eprintfn "jern golden: the run failed; nothing was recorded"
        (try IO.File.Delete tracePath with _ -> ())
    code

/// `jern golden check` — replay every recording offline against the current
/// agent and policy, then evaluate each one's declarative assertions.
let private runGoldenCheck (filter: string option) (markdown: bool) (agentDir: string option) =
    let root = Environment.CurrentDirectory
    match Golden.list root with
    | Error message ->
        eprintfn "jern golden: %s" message
        2
    | Ok [] ->
        eprintfn "jern golden: no recordings in %s (make one with: jern golden record \"task\")"
            (IO.Path.GetRelativePath(root, Golden.directory root))
        2
    | Ok entries ->
        let providers = loadProviders ()
        let selected =
            match filter with
            | Some slug -> entries |> List.filter (fun e -> e.slug.Contains(slug: string))
            | None -> entries
        if selected.IsEmpty then
            eprintfn "jern golden: no recording matches '%s'" (defaultArg filter "")
            2
        else
            let verdicts =
                selected |> List.map (Golden.check (replayGolden providers agentDir))
            if markdown then
                printf "%s" (Golden.renderMarkdown verdicts)
            else
                for v in verdicts do
                    if v.Passed then
                        printfn "%s - %s" (Style.green "ok  ") v.entry.slug
                    else
                        printfn "%s - %s" (Style.red "FAIL") v.entry.slug
                        match v.divergence with
                        | Some report ->
                            printfn "       %s" (Style.describe (report.Replace("\n", "\n       ")))
                        | None -> ()
                        for failure in v.failures do
                            printfn "       %s %s" (Style.red "assertion failed:") failure
                printfn ""
                let failed = verdicts |> List.filter (fun v -> not v.Passed)
                let verdict = sprintf "%d matched, %d changed" (verdicts.Length - failed.Length) failed.Length
                printfn "%s" (if failed.IsEmpty then Style.green verdict else Style.red verdict)
                if not failed.IsEmpty then
                    printfn "%s" (Style.dim "re-record a deliberate change with: jern golden record \"<task>\" --slug <slug>")
            if verdicts |> List.forall (fun v -> v.Passed) then 0 else 1

let private runGoldenList () =
    let root = Environment.CurrentDirectory
    match Golden.list root with
    | Error message ->
        eprintfn "jern golden: %s" message
        2
    | Ok [] ->
        printfn "%s" (Style.dim "no golden sessions yet — record one with: jern golden record \"task\"")
        0
    | Ok entries ->
        for entry in entries do
            printfn "%s  %s" (Style.bold entry.slug) (Style.dim entry.metadata.task)
            let assertions = entry.metadata.assertions
            let described =
                [ if not assertions.editsWithin.IsEmpty then
                    yield "edits within " + String.Join(", ", assertions.editsWithin)
                  if not assertions.noTools.IsEmpty then
                    yield "never " + String.Join(", ", assertions.noTools)
                  match assertions.maxFilesEdited with
                  | Some n -> yield sprintf "≤%d files" n
                  | None -> ()
                  match assertions.maxLlmCalls with
                  | Some n -> yield sprintf "≤%d model calls" n
                  | None -> ()
                  match assertions.maxTokens with
                  | Some n -> yield sprintf "≤%d tokens" n
                  | None -> () ]
            if described.IsEmpty then
                printfn "    %s" (Style.dim "no assertions — bytes only")
            else
                printfn "    %s %s" (Style.steel "asserts") (String.Join(" · ", described))
        0

/// `jern policy` — show the effective policy with provenance;
/// `jern policy --show-compiled` — the Kernel source config compiles to;
/// `jern policy init` — write the workspace override template.
let private runPolicy (init: bool) (showCompiled: bool) =
    let root = Environment.CurrentDirectory
    let workspacePath = IO.Path.Combine(root, ".jern", "policy.ikr")
    if init then
        if IO.File.Exists workspacePath then
            eprintfn "jern policy init: '%s' already exists" workspacePath
            1
        else
            IO.Directory.CreateDirectory(IO.Path.GetDirectoryName workspacePath) |> ignore
            IO.File.WriteAllText(workspacePath, Session.policyTemplate)
            // The user just authored this file, so it needs no first-use
            // prompt; edits to it will ask again.
            Trust.remember (Trust.defaultStorePath ()) workspacePath Session.policyTemplate
            printfn "wrote %s" workspacePath
            printfn "it now governs every jern session in this workspace; edit and re-run"
            0
    elif showCompiled then
        // Exactly what the config layers evaluate to — the escape hatch stays
        // honest: paste this into .jern/policy.ikr and edit it by hand.
        let sources = policySources (loadProviders ())
        if sources.IsEmpty then
            printfn "%s" (Style.dim "; no \"policy\" in configuration — only the built-in rules below apply")
            printf "%s" (IO.File.ReadAllText(Session.kernelFile "policy.ikr"))
        else
            for source in sources do
                let label = PolicyConfig.originLabel source.origin
                let grants =
                    match source.origin with
                    | PolicyConfig.Workspace _ ->
                        grantsAlreadyTrusted (PolicyConfig.trustIdentity source.origin)
                                             (PolicyConfig.canonicalJson source.policy)
                    | _ -> true
                printf "%s" (PolicyConfig.compile label grants source.policy)
        0
    else
        let providers = loadProviders ()
        let sources = policySources providers
        printfn ""
        printfn " %s %s" (Style.rust "effective policy") (Style.dim root)
        printfn ""
        printfn "  %s" (Style.bold "built-in")
        printfn "    %s" (Style.dim (Session.kernelFile "policy.ikr"))
        printfn "    %s" (Style.dim "reads allow; writes, shell, and MCP tools ask")
        for source in sources do
            let label = PolicyConfig.originLabel source.origin
            let canonical = PolicyConfig.canonicalJson source.policy
            let isProtected = match source.origin with PolicyConfig.Baseline _ -> true | _ -> false
            let grantsTrusted =
                if not (PolicyConfig.hasGrants source.policy) then true
                else
                    match source.origin with
                    | PolicyConfig.Workspace _ ->
                        grantsAlreadyTrusted (PolicyConfig.trustIdentity source.origin) canonical
                    | _ -> true
            printfn ""
            printfn "  %s %s%s" (Style.bold label)
                (Style.dim ("sha256 " + (PolicyConfig.digest source.policy).Substring(0, 12) + "…"))
                (if isProtected then "  " + Style.steel "[protected]" else "")
            for line in PolicyConfig.describeRestrictions source.policy do
                printfn "    %s %s" (Style.steel "restrict") line
            for line in PolicyConfig.describeGrants source.policy do
                printfn "    %s    %s   %s" (Style.steel "grant") line
                    (if grantsTrusted then Style.green "[trusted]" else Style.yellow "[not trusted — dropped]")
            // Pinning grants for an unattended run needs the *whole* digest,
            // so print it where someone wiring up CI will look for it.
            if PolicyConfig.hasGrants source.policy then
                printfn "    %s --policy-trust %s"
                    (Style.dim "pin these grants in CI with:") (PolicyConfig.digest source.policy)
        if IO.File.Exists workspacePath then
            let content = IO.File.ReadAllText workspacePath
            let trusted = Trust.isTrusted (Trust.defaultStorePath ()) workspacePath content
            printfn ""
            printfn "  %s %s" (Style.bold ".jern/policy.ikr")
                (if trusted then Style.green "[trusted]" else Style.yellow "[not trusted — skipped]")
            printfn "    %s" (Style.dim "arbitrary Kernel; may relax the base, but restrictions above still win")
        printfn ""
        printfn " %s" (Style.dim "restrictions compose by severity — a denial beats ask beats allow;")
        printfn " %s" (Style.dim "nothing loaded later can turn a restriction's denial into an approval.")
        printfn " %s" (Style.dim "compiled source: jern policy --show-compiled")
        printfn ""
        0

/// `jern ui` — serve the chat session as a local web app and open it.
let private runUi (model: string option) (cliBudget: int option) (auto: bool) (port: int) (agentDir: string option) =
    let root = Environment.CurrentDirectory
    let providers = loadProviders ()
    let agent =
        match agentDir with
        | Some dir -> dir
        | None -> Session.defaultAgentDir ()
    // First-use trust happens on this terminal before the server starts;
    // the running UI only consults the store (and trusts the brain
    // editor's own saves of the policy, which are the user authoring it).
    let policyPath = IO.Path.GetFullPath(IO.Path.Combine(root, ".jern", "policy.ikr"))
    if IO.File.Exists policyPath then
        ttyPolicyTrust policyPath (IO.File.ReadAllText policyPath) |> ignore
    let sources = policySources providers
    for source in sources do
        if PolicyConfig.hasGrants source.policy then
            match source.origin with
            | PolicyConfig.Workspace _ ->
                ttyPolicyGrantTrust (PolicyConfig.trustIdentity source.origin)
                                    (PolicyConfig.canonicalJson source.policy) |> ignore
            | _ -> ()
    let server =
        Ui.start
            { root = root
              // Streaming goes to the browser, not the console; the Ui
              // server does its own usage metering for the header.
              makeBridge = fun currentModel onText interrupted ->
                               Providers.createBridgeWith providers currentModel (Some onText) interrupted
              providers = providers
              model = model
              agentDir = Some agent
              agentSources = Session.agentPackageSources agent
              agentConfig = Providers.agentConfig providers
              mcpServers = providers.mcpServers
              budget = sessionBudget providers cliBudget
              auto = auto
              port = port
              policyTrust = fun path content -> Trust.isTrusted (Trust.defaultStorePath ()) path content
              rememberPolicy = fun path content -> Trust.remember (Trust.defaultStorePath ()) path content
              policySources = sources
              // The server never prompts: the terminal answered above, and
              // rebuilds mid-session only consult what was decided there.
              policyGrantTrust = grantsAlreadyTrusted }
    // Ctrl-C (or a supervisor's SIGTERM) closes the run record before the
    // process goes away, so a UI session's trace ends like a terminal run's.
    // PosixSignalRegistration is the reliable path here — ProcessExit does
    // not run for a signalled console app. The registrations are held for
    // the process's lifetime on purpose: collecting them unhooks the signal.
    let signalHooks =
        [ Runtime.InteropServices.PosixSignal.SIGINT
          Runtime.InteropServices.PosixSignal.SIGTERM ]
        |> List.map (fun signal ->
            Runtime.InteropServices.PosixSignalRegistration.Create(
                signal, fun _ -> server.stop ()))
    printfn " %s %s — ui at %s" (Style.rust "jern") (Style.steel ("v" + AgentEnv.version)) (Style.bold server.url)
    printfn " %s" (Style.dim (root + " · ctrl-c to stop"))
    try
        Diagnostics.Process.Start(Diagnostics.ProcessStartInfo(server.url, UseShellExecute = true)) |> ignore
    with _ -> ()
    server.run ()
    // Held until the listener returns: a collected registration unhooks its
    // signal, and this loop is the whole lifetime of the server.
    GC.KeepAlive signalHooks
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
    match Session.createWith
              { Session.configIn Environment.CurrentDirectory bridge with
                  policyTrust = ttyPolicyTrust
                  policySources = policySources (loadProviders ())
                  policyGrantTrust = ttyPolicyGrantTrust } with
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
    match Session.createWith
              { Session.configIn Environment.CurrentDirectory bridge with
                  policyTrust = ttyPolicyTrust
                  policySources = policySources (loadProviders ())
                  policyGrantTrust = ttyPolicyGrantTrust } with
    | Choice1Of2 error ->
        eprintfn "Startup error: %s" (showError error)
        1
    | Choice2Of2 session ->
        match Session.runScriptFile session path with
        | Choice1Of2 error ->
            eprintfn "Script error: %s" (showError error)
            1
        | Choice2Of2 _ -> 0

/// Copy the installed default agent into the workspace for editing.
let private runEject () =
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

[<EntryPoint>]
let main argv =
    Providers.applyCredentials ()
    match Args.parse (Array.toList argv) with
    | Error(Args.BadValue message) ->
        eprintfn "jern: %s" message
        2
    | Error(Args.SubUsage line) ->
        eprintfn "%s" line
        2
    | Error(Args.UnknownArgs other) ->
        eprintfn "Unknown arguments: %s" (String.Join(" ", other))
        eprintfn ""
        eprintf "%s" usage
        2
    | Ok(globals, command) ->
        cliThink <- globals.think
        cliEffort <- globals.effort
        cliPolicyBaseline <- globals.policyBaseline
        cliPolicyTrust <- globals.policyTrust
        let model = globals.model
        let cliBudget = globals.budget
        let auto = globals.auto
        match command with
        | Args.Version ->
            printfn "%s" AgentEnv.version
            0
        | Args.NoArgs when not Console.IsInputRedirected ->
            runChat None model cliBudget auto
        | Args.NoArgs ->
            printf "%s" usage
            0
        | Args.Resume id -> runChat (Some(defaultArg id "")) model cliBudget auto
        | Args.Repl -> runRepl model
        | Args.Run(yes, agent, task) -> runTask (yes || auto) agent model cliBudget task
        | Args.Undo -> runUndo ()
        | Args.Ui(port, agent) -> runUi model cliBudget auto port agent
        | Args.Mcp -> runMcp ()
        | Args.Policy(init, showCompiled) -> runPolicy init showCompiled
        | Args.Eject -> runEject ()
        | Args.Test(dir, record) -> runTests dir record model
        | Args.Replay(trace, policy, agent) -> runReplay trace policy agent
        | Args.Receipt(trace, format) -> runReceipt trace format
        | Args.Golden(Args.GoldenRecord(task, slug)) ->
            runGoldenRecord task slug auto None model cliBudget
        | Args.Golden(Args.GoldenCheck(filter, markdown)) -> runGoldenCheck filter markdown None
        | Args.Golden Args.GoldenList -> runGoldenList ()
        | Args.Script path -> runScript path model
