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
          /// Polled before every llm/tool dispatch; true aborts the turn
          /// with a Kernel error (Ctrl-C).
          interrupted: unit -> bool }

    type Session =
        { agentEnv: LispVal
          handlerEnv: LispVal
          tags: AgentEnv.EffectTags
          workspaceRoot: string }

    let kernelFile name =
        let local = Path.Combine("kernel", name)
        let installed = Path.Combine(AppContext.BaseDirectory, "kernel", name)
        if File.Exists local then local else installed

    /// Parse and evaluate every form of a Kernel source file in `env`.
    let private loadFile (env: LispVal) (path: string) : ThrowsError<unit> =
        try
            let source = File.ReadAllText path
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

    /// Build a session from a full configuration.
    let createWith (config: Config) : ThrowsError<Session> =
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

            let hostLlmCall env cont = function
                | [request] ->
                    if config.interrupted () then
                        signal cont (Default "interrupted by user")
                    else
                        match config.bridge request with
                        | Choice1Of2 error -> signal cont error
                        | Choice2Of2 reply -> bounceContinue env cont reply
                | bad -> signal cont (NumArgs(1, bad))

            let hostToolCall env cont = function
                | [call] ->
                    if config.interrupted () then
                        signal cont (Default "interrupted by user")
                    else
                        let dispatch =
                            match Tools.plistTryGet "name" call with
                            | Some (Obj (:? string as name)) when name.StartsWith "mcp__" ->
                                Mcp.dispatch mcpByName
                            | _ -> Tools.dispatch config.workspaceRoot
                        match dispatch call with
                        | Choice1Of2 error -> signal cont error
                        | Choice2Of2 reply -> bounceContinue env cont reply
                | bad -> signal cont (NumArgs(1, bad))

            let hostTrace env cont = function
                | [event] ->
                    match config.traceSink with
                    | None -> bounceContinue env cont Inert
                    | Some sink ->
                        let ts = System.DateTime.UtcNow.ToString("o")
                        let payload = Json.serialize event
                        let line =
                            if payload.StartsWith "{" then
                                sprintf """{"ts":"%s",%s""" ts (payload.Substring 1)
                            else
                                sprintf """{"ts":"%s","data":%s}""" ts payload
                        sink line
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

            let hostApprove env cont = function
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
                     :: ("agent-env", agentEnv)
                     :: AgentEnv.effectBindings tags)

            let files =
                [ agentEnv, kernelFile "prelude.ikr"
                  agentEnv, kernelFile "tools.ikr"
                  // The prelude's helpers (plist-get & co.) serve both sides.
                  handlerEnv, kernelFile "prelude.ikr"
                  handlerEnv, kernelFile "policy.ikr"
                  handlerEnv, kernelFile "handlers.ikr" ]
                @ (config.agentSources |> List.map (fun path -> agentEnv, path))

            let loaded =
                files
                |> List.fold
                    (fun acc (env, file) ->
                        match acc with
                        | Choice1Of2 _ -> acc
                        | Choice2Of2 () -> loadFile env file)
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
          interrupted = fun () -> false }

    /// Build a session around an LLM bridge with tools scoped to `root`.
    let createIn (root: string) (bridge: AnthropicBridge.LlmBridge) : ThrowsError<Session> =
        createWith (configIn root bridge)

    /// Session rooted in the current directory.
    let create bridge = createIn (System.Environment.CurrentDirectory) bridge

    /// Evaluate parsed forms as agent code under the installed handler stack.
    let runForms (session: Session) (forms: LispVal list) : ThrowsError<LispVal> =
        let program =
            ofList [ Atom "run-in-session"; ofList (Atom "sequence" :: forms) ]
        eval session.handlerEnv (newContinuation session.handlerEnv) program

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

    /// Invoke the loaded agent's entry point: (run-agent "task").
    let runAgent (session: Session) (task: string) : ThrowsError<LispVal> =
        runForms session [ ofList [ Atom "run-agent"; Obj(task :> obj) ] ]

    /// One chat turn: (chat-turn '<messages> "text"). The conversation is
    /// passed and returned as a Kernel list, newest-first.
    let runChatTurn (session: Session) (messages: LispVal) (userText: string) : ThrowsError<LispVal> =
        runForms session
            [ ofList
                [ Atom "chat-turn"
                  ofList [ Atom "quote"; messages ]
                  Obj(userText :> obj) ] ]
