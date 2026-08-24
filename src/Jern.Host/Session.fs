namespace Jern.Host

open System
open System.IO
open IronKernel
open IronKernel.Ast
open IronKernel.Errors
open IronKernel.Eval
open IronKernel.SymbolTable

/// A session wires together the two environments of the architecture:
///
/// - the **agent environment** (safe profile): effect tags, the prelude, and
///   agent code. No authority — everything reaches the world via `perform`.
/// - the **handler environment** (safe profile + host primitives): the handler
///   stack from kernel/handlers.ikr, `jern/host-llm-call` (the provider
///   bridge), and the agent environment as a first-class value.
///
/// Agent code never sees the handler environment; the handler environment
/// deliberately holds the agent environment, which is the safe direction of
/// delegation (docs/capabilities.md).
module Session =

    type Config =
        { workspaceRoot: string
          bridge: AnthropicBridge.LlmBridge
          /// Receives one JSON line per traced effect; None discards.
          traceSink: (string -> unit) option
          /// Agent package sources evaluated into the agent environment,
          /// after the host prelude and tool definitions.
          agentSources: string list
          /// Answers jern/approve questions (TTY prompt, --yes, test stub).
          /// None denies everything with a warning to stderr.
          approver: (string -> bool) option
          /// Extra bindings injected into the agent environment (test hooks).
          agentBindings: (string * LispVal) list
          /// Workspace configuration exposed to agent source as the
          /// `jern/workspace-config` plist (e.g. :test_command). Data only.
          agentConfig: LispVal
          /// MCP servers to connect; their tools register in the agent's
          /// registry as mcp__<server>__<tool> and dispatch through the
          /// ordinary jern/tool-call effect (policy applies unchanged).
          mcpServers: Mcp.ServerSpec list
          /// Run budget for the budget handler, as a plist
          /// (:llm_calls N :tokens M); Nil = unlimited. Exhaustion becomes
          /// a jern/approve question — approving grants another round.
          budget: LispVal
          /// Polled before every llm/tool dispatch; true aborts the turn
          /// with a Kernel error (Ctrl-C).
          interrupted: unit -> bool
          /// Consulted before <root>/.jern/policy.ikr is evaluated (it runs
          /// privileged): receives the file's absolute path and content;
          /// false skips it and the built-in policy stands. The default
          /// trusts everything — the CLI front-ends install a first-use
          /// prompt backed by Trust (docs/security-model.md).
          policyTrust: string -> string -> bool
          /// Policy sources composed into this session (config files, a
          /// protected CI baseline). Their *restrictions* always apply;
          /// their *grants* apply only when policyGrantTrust agrees.
          policySources: PolicyConfig.Source list
          /// Consulted for the grant half of a repository-supplied policy:
          /// (trust identity, canonical JSON) -> trusted? Restrictions never
          /// consult it. The default trusts everything — the CLI front-ends
          /// install a first-use prompt (and a headless digest check).
          policyGrantTrust: string -> string -> bool
          /// Replaces the executor for every jern/tool-call that reaches the
          /// host (built-in and MCP tools alike). None = the real tools.
          /// `jern replay` substitutes a bridge that answers from a recorded
          /// trace, making a whole run side-effect-free.
          toolDispatch: (LispVal -> ThrowsError<LispVal>) option
          /// How many jern/spawn ancestors this session has: 0 for a session
          /// the user started, incremented for each child. Capped host-side
          /// so agents cannot fork without bound.
          spawnDepth: int }

    type Session =
        { agentEnv: LispVal
          handlerEnv: LispVal
          tags: AgentEnv.EffectTags
          workspaceRoot: string }

    /// Starting point for a workspace policy (`jern policy init`, and the
    /// UI's policy editor when no workspace policy exists yet).
    let policyTemplate = """; Workspace tool policy — loaded after the built-in kernel/policy.ikr and
; overrides what it redefines. Decisions: :allow, :ask, or a reason string
; (a denial the model sees). Helpers in scope: call-path, call-command,
; (path-within? call "src/"), (command-is? call "pytest"),
; string-prefix?/string-suffix?/string-contains?. command-is? never
; auto-allows a command containing shell metacharacters (; & | ` $( > <):
; those fall through to your next clause.
;
; This file runs privileged, so jern asks you to trust it the first time it
; loads — and again whenever its content changes.

(define tool-policy
  (lambda (call)
    (sequence
      (define name (plist-get call :name))
      (cond ; ((path-within? call "src/") :allow)      ; scope edits to src/
            ; ((command-is? call "pytest") :allow)     ; allowlist a command
            ; ((equal? name "mcp__github__get_issue") :allow)
            ((equal? name "shell") :ask)
            ((equal? name "edit_file") :ask)
            ((equal? name "write_file") :ask)
            ((equal? name "read_file") :allow)
            ((equal? name "list_dir") :allow)
            ((equal? name "file_tree") :allow)
            ((equal? name "grep") :allow)
            ((equal? name "symbols") :allow)
            ((equal? name "kernel_eval") :allow) ; inner calls are still policed
            (#t :ask)))))
"""

    let kernelFile name =
        let local = Path.Combine("kernel", name)
        let installed = Path.Combine(AppContext.BaseDirectory, "kernel", name)
        if File.Exists local then local else installed

    /// Parse and evaluate every form of Kernel source in `env`; `path` is
    /// for error reporting only — the given source is what runs.
    let private loadSource (env: LispVal) (path: string) (source: string) : ThrowsError<unit> =
        try
            match Parser.readExprListFromSource path source with
            | Choice1Of2 error -> Choice1Of2 error
            | Choice2Of2 forms ->
                let rec run = function
                    | [] -> Choice2Of2 ()
                    | form :: rest ->
                        match eval env (newContinuation env) form with
                        | Choice1Of2 error -> Choice1Of2 error
                        | Choice2Of2 _ -> run rest
                run forms
        with ex ->
            Choice1Of2 (Default(sprintf "failed to load '%s': %s" path ex.Message))

    /// Parse and evaluate every form of a Kernel source file in `env`.
    let private loadFile (env: LispVal) (path: string) : ThrowsError<unit> =
        try
            loadSource env path (File.ReadAllText path)
        with ex ->
            Choice1Of2 (Default(sprintf "failed to load '%s': %s" path ex.Message))

    /// The sources of an agent package directory: its src/*.ikr in path order
    /// (or the directory's own *.ikr when there is no src/).
    let agentPackageSources (dir: string) : string list =
        let sourceDir =
            let src = Path.Combine(dir, "src")
            if Directory.Exists src then src else dir
        if Directory.Exists sourceDir then
            Directory.EnumerateFiles(sourceDir, "*.ikr") |> Seq.sort |> List.ofSeq
        else
            []

    /// The default agent installed alongside the binary.
    let defaultAgentDir () =
        Path.Combine(AppContext.BaseDirectory, "agents", "default")

    /// Evaluate parsed forms as agent code under the installed handler stack.
    let runForms (session: Session) (forms: LispVal list) : ThrowsError<LispVal> =
        let program =
            ofList [ Atom "run-in-session"; ofList (Atom "sequence" :: forms) ]
        eval session.handlerEnv (newContinuation session.handlerEnv) program

    /// Invoke the loaded agent's entry point: (run-agent "task").
    let runAgent (session: Session) (task: string) : ThrowsError<LispVal> =
        runForms session [ ofList [ Atom "run-agent"; Obj(task :> obj) ] ]

    /// Build a session from a full configuration. Recursive because the
    /// jern/host-spawn primitive builds a child session with `createWith`.
    let rec createWith (config: Config) : ThrowsError<Session> =
        match Emit.bootstrapEnvForProfile Safe with
        | Choice1Of2 error -> Choice1Of2 error
        | Choice2Of2 std ->
            let tags = AgentEnv.newEffectTags ()

            // Connect configured MCP servers. A server that fails to start is
            // skipped with a warning — a dead integration must not brick the
            // session — and its tools simply don't register.
            let mcpServers =
                config.mcpServers
                |> List.choose (fun spec ->
                    match Mcp.connect spec with
                    | Ok server -> Some server
                    | Error reason ->
                        eprintfn "jern: %s (skipping this MCP server)" reason
                        None)
            let mcpByName =
                mcpServers |> List.map (fun s -> s.spec.name, s) |> Map.ofList

            let agentEnv =
                bindVars (newEnv [std])
                    (AgentEnv.baseBindings tags
                     @ [ "jern/workspace-config", config.agentConfig ]
                     @ config.agentBindings)

            // Where kernel_eval programs run: a child of the agent
            // environment, so model-authored code sees the same bindings as
            // agent source (tools, prelude, skills) and nothing more — and
            // one env for the whole session, so a program's definitions
            // persist for later programs.
            let programEnv = newEnv [agentEnv]

            // A kernel_eval program that outlives its wall-clock cap is
            // abandoned: its worker thread is marked here, and the host
            // primitives below refuse it, so a runaway program can burn a
            // core but can no longer act — no effects, no trace writes, no
            // approval prompts — while the session moves on.
            let abandonedThreads = System.Collections.Concurrent.ConcurrentDictionary<int, byte>()
            let threadAbandoned () =
                abandonedThreads.ContainsKey System.Threading.Thread.CurrentThread.ManagedThreadId

            let hostLlmCall env cont = function
                | [request] ->
                    if config.interrupted () || threadAbandoned () then
                        signal cont (Default "interrupted by user")
                    else
                        match config.bridge request with
                        | Choice1Of2 error -> signal cont error
                        | Choice2Of2 reply -> bounceContinue env cont reply
                | bad -> signal cont (NumArgs(1, bad))

            // kernel_eval is answered here, before the replay override:
            // replay must *re-execute* programs (their inner effects are
            // what the trace recorded), not answer them from the recording.
            let evalProgramRef: (LispVal -> ThrowsError<LispVal>) ref =
                ref (fun _ -> Choice1Of2 (Default "kernel_eval is not available yet"))

            let hostToolCall env cont = function
                | [call] ->
                    if config.interrupted () || threadAbandoned () then
                        signal cont (Default "interrupted by user")
                    else
                        let dispatch =
                            match Tools.plistTryGet "name" call with
                            | Some (Obj (:? string as name)) when name = "kernel_eval" ->
                                evalProgramRef.Value
                            | nameVal ->
                                match config.toolDispatch with
                                | Some substitute -> substitute
                                | None ->
                                    match nameVal with
                                    | Some (Obj (:? string as name)) when name.StartsWith "mcp__" ->
                                        Mcp.dispatch mcpByName
                                    | _ -> Tools.dispatch config.workspaceRoot
                        match dispatch call with
                        | Choice1Of2 error -> signal cont error
                        | Choice2Of2 reply -> bounceContinue env cont reply
                | bad -> signal cont (NumArgs(1, bad))

            /// One timestamped JSONL line per event. Used by the jern/trace
            /// primitive below and by the host itself, for events that belong
            /// to the run rather than to any effect (policy provenance).
            let emitTrace (event: LispVal) =
                match config.traceSink with
                | None -> ()
                | Some sink ->
                    let ts = System.DateTime.UtcNow.ToString("o")
                    let payload = Json.serialize event
                    let line =
                        if payload.StartsWith "{" then
                            sprintf """{"ts":"%s",%s""" ts (payload.Substring 1)
                        else
                            sprintf """{"ts":"%s","data":%s}""" ts payload
                    sink line

            let hostTrace env cont = function
                | [event] ->
                    if threadAbandoned () then bounceContinue env cont Inert
                    else
                        emitTrace event
                        bounceContinue env cont Inert
                | bad -> signal cont (NumArgs(1, bad))

            let gitEnabled = lazy (Git.isRepo config.workspaceRoot)

            /// Commit a file's uncommitted user changes before jern edits it.
            let hostGitSaveDirty env cont = function
                | [Obj (:? string as path)] ->
                    let result =
                        if gitEnabled.Value && Git.isFileDirty config.workspaceRoot path then
                            match Git.commitPath config.workspaceRoot path
                                      (sprintf "jern: save your uncommitted changes to %s" path) with
                            | Some hash -> Obj(hash :> obj)
                            | None -> Keyword "null"
                        else Keyword "null"
                    bounceContinue env cont result
                | bad -> signal cont (NumArgs(1, bad))

            let hostGitCommit env cont = function
                | [Obj (:? string as path); Obj (:? string as message)] ->
                    let result =
                        if gitEnabled.Value then
                            match Git.commitPath config.workspaceRoot path message with
                            | Some hash -> Obj(hash :> obj)
                            | None -> Keyword "null"
                        else Keyword "null"
                    bounceContinue env cont result
                | bad -> signal cont (NumArgs(2, bad))

            // Persistent memory (.jern/memory.json), reached only via the
            // jern/recall and jern/remember effects — see kernel/handlers.ikr.
            let memoryPath = Memory.storePath config.workspaceRoot

            let hostMemoryGet env cont = function
                | [Obj (:? string as key)] ->
                    let result =
                        match Memory.get memoryPath key with
                        | Some value -> Obj(value :> obj)
                        | None -> Keyword "null"
                    bounceContinue env cont result
                | [bad] -> signal cont (TypeMismatch("string", bad))
                | bad -> signal cont (NumArgs(1, bad))

            let hostMemorySet env cont = function
                | [Obj (:? string as key); Obj (:? string as value)] ->
                    Memory.set memoryPath key value
                    bounceContinue env cont Inert
                | bad -> signal cont (NumArgs(2, bad))

            // jern/spawn — fork a child agent session. The same handler
            // stack (policy, approval, budget, memory, trace) composes
            // recursively onto the child via createWith; the child shares
            // this session's bridge, approver, and workspace, and its trace
            // lines are tagged {"spawn":N,…} so the log shows the nesting.
            // Depth is capped so children cannot fork without bound.
            let spawnCounter = ref 0

            let hostSpawn env cont = function
                | [spec] ->
                    let answer (isError: bool) (content: string) =
                        bounceContinue env cont
                            (ofList [ Keyword "content"; Obj(content :> obj)
                                      Keyword "is_error"; Bool isError ])
                    if config.interrupted () then
                        signal cont (Default "interrupted by user")
                    elif config.spawnDepth >= 2 then
                        answer true "spawn depth limit (2) reached — this agent may not fork further"
                    else
                        match Tools.plistTryGet "task" spec with
                        | Some (Obj (:? string as task)) ->
                            let sources =
                                match Tools.plistTryGet "agent" spec with
                                | Some (Obj (:? string as name)) ->
                                    // A workspace-relative package directory
                                    // (confined to the workspace), or the
                                    // name of an installed agent.
                                    let rootFull = Path.GetFullPath config.workspaceRoot
                                    let workspaceCandidate = Path.GetFullPath(Path.Combine(rootFull, name))
                                    let installedCandidate = Path.Combine(AppContext.BaseDirectory, "agents", name)
                                    let confined =
                                        workspaceCandidate.StartsWith(rootFull + string Path.DirectorySeparatorChar, StringComparison.Ordinal)
                                    if confined && Directory.Exists workspaceCandidate then
                                        Ok(agentPackageSources workspaceCandidate)
                                    elif Directory.Exists installedCandidate then
                                        Ok(agentPackageSources installedCandidate)
                                    else
                                        Error(sprintf "agent '%s' is neither a workspace directory nor an installed agent" name)
                                | Some other -> Error(sprintf ":agent must be a string, got %s" (showVal other))
                                | None -> Ok config.agentSources
                            match sources with
                            | Error reason -> answer true reason
                            | Ok agentSources ->
                                spawnCounter.Value <- spawnCounter.Value + 1
                                let spawnId = spawnCounter.Value
                                let childSink =
                                    config.traceSink
                                    |> Option.map (fun sink ->
                                        fun (line: string) ->
                                            if line.StartsWith "{" then
                                                sink (sprintf "{\"spawn\":%d,%s" spawnId (line.Substring 1))
                                            else sink line)
                                let childConfig =
                                    { config with
                                        agentSources = agentSources
                                        traceSink = childSink
                                        // Each spawn would reconnect stdio
                                        // servers, so children run without
                                        // MCP tools for now.
                                        mcpServers = []
                                        spawnDepth = config.spawnDepth + 1 }
                                match createWith childConfig with
                                | Choice1Of2 error -> answer true (sprintf "spawn failed: %s" (showError error))
                                | Choice2Of2 child ->
                                    match runAgent child task with
                                    | Choice1Of2 error -> answer true (sprintf "subagent error: %s" (showError error))
                                    | Choice2Of2 (Obj (:? string as text)) -> answer false text
                                    | Choice2Of2 _ -> answer false ""
                        | _ -> answer true "jern/spawn needs a :task string"
                | bad -> signal cont (NumArgs(1, bad))

            let hostApprove env cont = function
                | [Obj (:? string as _)] when threadAbandoned () ->
                    signal cont (Default "abandoned program may not ask for approval")
                | [Obj (:? string as description)] ->
                    match config.approver with
                    | Some approve -> bounceContinue env cont (Bool(approve description))
                    | None ->
                        eprintfn "jern: denied (no approver configured): %s" description
                        bounceContinue env cont (Bool false)
                | [bad] -> signal cont (TypeMismatch("string", bad))
                | bad -> signal cont (NumArgs(1, bad))

            let handlerEnv =
                bindVars (newEnv [std])
                    (("jern/host-llm-call", AgentEnv.applicative hostLlmCall)
                     :: ("jern/host-tool-call", AgentEnv.applicative hostToolCall)
                     :: ("jern/host-trace", AgentEnv.applicative hostTrace)
                     :: ("jern/host-approve", AgentEnv.applicative hostApprove)
                     :: ("jern/host-git-save-dirty", AgentEnv.applicative hostGitSaveDirty)
                     :: ("jern/host-git-commit", AgentEnv.applicative hostGitCommit)
                     :: ("jern/host-memory-get", AgentEnv.applicative hostMemoryGet)
                     :: ("jern/host-memory-set", AgentEnv.applicative hostMemorySet)
                     :: ("jern/host-spawn", AgentEnv.applicative hostSpawn)
                     :: ("jern/show", AgentEnv.applicative AgentEnv.show)
                     :: ("jern/budget", config.budget)
                     :: ("agent-env", agentEnv)
                     :: ("program-env", programEnv)
                     :: AgentEnv.stringBindings
                     @ AgentEnv.effectBindings tags)

            // Policy from configuration (jern.json, a protected CI baseline)
            // compiles to Kernel layers installed on top of the built-in
            // policy. Restrictions always load. Grants can loosen approvals,
            // so a repository-supplied one is the same attack surface as a
            // workspace policy file and needs the same first-use trust; when
            // that is declined, the grants are dropped and the restrictions
            // stay (docs/roadmap-governance.md §2).
            let policyConfigLayers =
                config.policySources
                |> List.filter (fun source -> not (PolicyConfig.isEmpty source.policy))
                |> List.map (fun source ->
                    let label = PolicyConfig.originLabel source.origin
                    let canonical = PolicyConfig.canonicalJson source.policy
                    let grantsTrusted =
                        if not (PolicyConfig.hasGrants source.policy) then true
                        else
                            match source.origin with
                            // The user's own machine config and a baseline
                            // supplied by a protected workflow are, by
                            // construction, not the repository's to write.
                            | PolicyConfig.UserConfig _ | PolicyConfig.Baseline _ -> true
                            | PolicyConfig.Workspace _ ->
                                config.policyGrantTrust
                                    (PolicyConfig.trustIdentity source.origin) canonical
                    if not grantsTrusted then
                        eprintfn "jern: grants in %s are not trusted — dropping them; its restrictions still apply" label
                    let compiled = PolicyConfig.compile label grantsTrusted source.policy
                    emitTrace
                        (ofList
                            [ Keyword "event"; Obj("policy-layer" :> obj)
                              Keyword "source"; Obj(label :> obj)
                              Keyword "digest"; Obj(PolicyConfig.digest source.policy :> obj)
                              Keyword "grants"; Bool grantsTrusted
                              Keyword "protected"
                              Bool (match source.origin with PolicyConfig.Baseline _ -> true | _ -> false) ])
                    handlerEnv, sprintf "<policy from %s>" label, Some compiled)

            // A workspace may carry its own policy: <root>/.jern/policy.ikr
            // loads after the built-in one and rebinds what it redefines
            // (usually tool-policy). It runs privileged, so policyTrust
            // decides whether it loads at all, and what runs is pinned to
            // the exact content that decision saw (docs/security-model.md).
            let workspacePolicy =
                let path = Path.GetFullPath(Path.Combine(config.workspaceRoot, ".jern", "policy.ikr"))
                if File.Exists path then
                    let content = File.ReadAllText path
                    if config.policyTrust path content then
                        eprintfn "jern: using workspace policy %s" path
                        [ handlerEnv, path, Some content ]
                    else
                        eprintfn "jern: workspace policy %s is not trusted — using the built-in policy" path
                        []
                else []

            // A workspace can carry a skills library: .jern/skills.ikr loads
            // into the *agent* environment, so agent source and kernel_eval
            // programs alike can call what it defines. Unprivileged — unlike
            // the workspace policy it needs no trust prompt, because skills
            // code can only reach the world through the same policed effects
            // as any other agent code. A broken skills file fails the
            // session loudly, like a broken agent source.
            let workspaceSkills =
                let path = Path.GetFullPath(Path.Combine(config.workspaceRoot, ".jern", "skills.ikr"))
                if File.Exists path then
                    eprintfn "jern: loading workspace skills %s (unprivileged)" path
                    [ agentEnv, path, None ]
                else []

            let files =
                [ agentEnv, kernelFile "prelude.ikr", None
                  agentEnv, kernelFile "tools.ikr", None ]
                @ workspaceSkills
                @ [ // The prelude's helpers (plist-get & co.) serve both sides.
                    handlerEnv, kernelFile "prelude.ikr", None
                    handlerEnv, kernelFile "policy.ikr", None ]
                @ policyConfigLayers
                @ workspacePolicy
                @ [ handlerEnv, kernelFile "handlers.ikr", None ]
                @ (config.agentSources |> List.map (fun path -> agentEnv, path, None))

            // kernel_eval — programmatic tool calling. The model's program
            // is parsed here and evaluated *in Kernel*, in program-env,
            // wrapped in run-in-program-session (kernel/handlers.ikr) so a
            // fresh copy of the whole handler stack is installed on the
            // nested continuation: every effect the program performs crosses
            // policy, approval, budget, git, memory, and trace exactly like
            // a hand-made tool call — and the same budget counters apply.
            // Program errors surface as this tool's error result, so the
            // model can read them and try again; a program that exceeds the
            // wall-clock cap is abandoned (see abandonedThreads above).
            let evalNesting = ref 0
            evalProgramRef.Value <-
                fun call ->
                    let toolResult (isError: bool) (content: string) =
                        ofList [ Keyword "content"; Obj(content :> obj)
                                 Keyword "is_error"; Bool isError ]
                    let code =
                        match Tools.plistTryGet "input" call with
                        | Some input ->
                            match Tools.plistTryGet "code" input with
                            | Some (Obj (:? string as c)) -> Some c
                            | _ -> None
                        | None -> None
                    match code with
                    | None -> Choice2Of2 (toolResult true "kernel_eval needs a 'code' string argument")
                    | Some code ->
                        if evalNesting.Value >= 2 then
                            Choice2Of2 (toolResult true "kernel_eval nesting limit (2) reached")
                        else
                            match Parser.readExprListFromSource "kernel_eval" code with
                            | Choice1Of2 error ->
                                Choice2Of2 (toolResult true ("syntax error: " + showError error))
                            | Choice2Of2 [] -> Choice2Of2 (toolResult true "empty program")
                            | Choice2Of2 forms ->
                                let program =
                                    ofList [ Atom "run-in-program-session"
                                             ofList (Atom "sequence" :: forms) ]
                                evalNesting.Value <- evalNesting.Value + 1
                                try
                                    try
                                        let timeout = (Tools.currentLimits ()).evalTimeoutSeconds
                                        let workerThread = ref -1
                                        let worker =
                                            System.Threading.Tasks.Task.Run(fun () ->
                                                workerThread.Value <- System.Threading.Thread.CurrentThread.ManagedThreadId
                                                eval handlerEnv (newContinuation handlerEnv) program)
                                        let started = DateTime.UtcNow
                                        let mutable outcome = None
                                        let mutable waiting = true
                                        while waiting do
                                            if worker.Wait 100 then
                                                outcome <- Some worker.Result
                                                waiting <- false
                                            elif config.interrupted ()
                                                 || (DateTime.UtcNow - started).TotalSeconds > timeout then
                                                waiting <- false
                                        match outcome with
                                        | Some (Choice2Of2 value) -> Choice2Of2 (toolResult false (showVal value))
                                        | Some (Choice1Of2 error) -> Choice2Of2 (toolResult true (showError error))
                                        | None ->
                                            // Mark the worker's thread refused; if the
                                            // program does eventually finish, unmark it —
                                            // the pool will reuse the thread.
                                            if workerThread.Value >= 0 then
                                                abandonedThreads.TryAdd(workerThread.Value, 0uy) |> ignore
                                                worker.ContinueWith(fun (_: System.Threading.Tasks.Task<_>) ->
                                                    abandonedThreads.TryRemove workerThread.Value |> ignore)
                                                |> ignore
                                            let reason =
                                                if config.interrupted () then "was interrupted"
                                                else sprintf "timed out after %.0f seconds" timeout
                                            Choice2Of2 (toolResult true
                                                            (sprintf "program %s — any effects it completed are in the trace" reason))
                                    with ex ->
                                        Choice2Of2 (toolResult true (sprintf "program failed: %s" ex.Message))
                                finally
                                    evalNesting.Value <- evalNesting.Value - 1

            let loaded =
                files
                |> List.fold
                    (fun acc (env, file, pinned) ->
                        match acc with
                        | Choice1Of2 _ -> acc
                        | Choice2Of2 () ->
                            match pinned with
                            | Some source -> loadSource env file source
                            | None -> loadFile env file)
                    (Choice2Of2 ())

            // Register each connected server's tools in the agent's registry
            // by evaluating ordinary (register-tool! …) forms — the same path
            // tools.ikr takes, so tools-for-llm and call-tool see no
            // difference between built-in and MCP tools.
            let registerMcpTools () =
                let registerDescriptor (descriptor: LispVal) =
                    match Tools.plistTryGet "name" descriptor,
                          Tools.plistTryGet "description" descriptor,
                          Tools.plistTryGet "input_schema" descriptor with
                    | Some name, Some description, Some schema ->
                        let form =
                            ofList
                                [ Atom "register-tool!"; name; description
                                  ofList [ Atom "quote"; schema ] ]
                        match eval agentEnv (newContinuation agentEnv) form with
                        | Choice1Of2 error -> Choice1Of2 error
                        | Choice2Of2 _ -> Choice2Of2 ()
                    | _ -> Choice2Of2 ()
                mcpServers
                |> List.fold
                    (fun acc server ->
                        match acc with
                        | Choice1Of2 _ -> acc
                        | Choice2Of2 () ->
                            match Mcp.listTools server with
                            | Error reason ->
                                eprintfn "jern: %s (its tools are unavailable)" reason
                                Choice2Of2 ()
                            | Ok descriptors ->
                                descriptors
                                |> List.fold
                                    (fun acc2 d ->
                                        match acc2 with
                                        | Choice1Of2 _ -> acc2
                                        | Choice2Of2 () -> registerDescriptor d)
                                    (Choice2Of2 ()))
                    (Choice2Of2 ())

            match loaded with
            | Choice1Of2 error -> Choice1Of2 error
            | Choice2Of2 () ->
                match registerMcpTools () with
                | Choice1Of2 error -> Choice1Of2 error
                | Choice2Of2 () ->
                    Choice2Of2
                        { agentEnv = agentEnv
                          handlerEnv = handlerEnv
                          tags = tags
                          workspaceRoot = config.workspaceRoot }

    /// Configuration with no trace, no agent package, tools in `root`, and
    /// everything approval-gated approved (scripts and the REPL are the user
    /// acting directly; `jern run` overrides this with a real approver).
    let configIn root bridge =
        { workspaceRoot = root
          bridge = bridge
          traceSink = None
          agentSources = []
          approver = Some(fun _ -> true)
          agentBindings = []
          agentConfig = Nil
          mcpServers = []
          budget = Nil
          interrupted = fun () -> false
          policyTrust = fun _ _ -> true
          policySources = []
          policyGrantTrust = fun _ _ -> true
          toolDispatch = None
          spawnDepth = 0 }

    /// Build a session around an LLM bridge with tools scoped to `root`.
    let createIn (root: string) (bridge: AnthropicBridge.LlmBridge) : ThrowsError<Session> =
        createWith (configIn root bridge)

    /// Session rooted in the current directory.
    let create bridge = createIn (System.Environment.CurrentDirectory) bridge

    /// Evaluate a single source string as agent code under the handler stack.
    let runSource (session: Session) (name: string) (source: string) : ThrowsError<LispVal> =
        match Parser.readExprListFromSource name source with
        | Choice1Of2 error -> Choice1Of2 error
        | Choice2Of2 forms -> runForms session forms

    /// Evaluate a script file as agent code under the handler stack.
    let runScriptFile (session: Session) (path: string) : ThrowsError<LispVal> =
        try
            runSource session path (File.ReadAllText path)
        with ex ->
            Choice1Of2 (Default(sprintf "failed to read '%s': %s" path ex.Message))

    /// One chat turn: (chat-turn '<messages> "text"). The conversation is
    /// passed and returned as a Kernel list, newest-first.
    let runChatTurn (session: Session) (messages: LispVal) (userText: string) : ThrowsError<LispVal> =
        runForms session
            [ ofList
                [ Atom "chat-turn"
                  ofList [ Atom "quote"; messages ]
                  Obj(userText :> obj) ] ]
