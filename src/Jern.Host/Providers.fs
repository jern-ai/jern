namespace Jern.Host

open System
open System.IO
open System.Text.Json.Nodes
open IronKernel.Ast
open IronKernel.Errors

/// Provider routing and configuration.
///
/// Models are named aider-style, `provider/model` (`anthropic/claude-opus-5`,
/// `openai/gpt-5.2`, `ollama/qwen3`). The provider prefix picks the bridge and
/// endpoint; the bare model id goes on the wire. Per-request `:model` in agent
/// source wins over the CLI `--model`, which wins over the config default —
/// so agent code *may* choose models per task, and usually doesn't have to.
///
/// Configuration is JSON, merged from `~/.config/jern/config.json` then
/// `<workspace>/jern.json` (workspace wins per key):
///
///   {
///     "default_model": "anthropic/claude-opus-5",
///     "aliases": { "fast": "groq/llama-3.3-70b-versatile" },
///     "providers": {
///       "myproxy": { "base_url": "https://llm.corp/v1", "api_key_env": "MYPROXY_KEY" }
///     }
///   }
module Providers =

    type Kind =
        | Anthropic
        | OpenAICompat

    type Provider =
        { name: string
          kind: Kind
          baseUrl: string
          apiKeyEnv: string option }

    /// Built-in providers. One OpenAI-compatible translation layer covers
    /// everything but Anthropic (which is native wire-shape passthrough).
    let builtIns =
        [ { name = "anthropic"; kind = Anthropic; baseUrl = ""; apiKeyEnv = Some "ANTHROPIC_API_KEY" }
          { name = "openai"; kind = OpenAICompat; baseUrl = "https://api.openai.com/v1"; apiKeyEnv = Some "OPENAI_API_KEY" }
          { name = "ollama"; kind = OpenAICompat; baseUrl = "http://localhost:11434/v1"; apiKeyEnv = None }
          { name = "openrouter"; kind = OpenAICompat; baseUrl = "https://openrouter.ai/api/v1"; apiKeyEnv = Some "OPENROUTER_API_KEY" }
          { name = "deepseek"; kind = OpenAICompat; baseUrl = "https://api.deepseek.com/v1"; apiKeyEnv = Some "DEEPSEEK_API_KEY" }
          { name = "groq"; kind = OpenAICompat; baseUrl = "https://api.groq.com/openai/v1"; apiKeyEnv = Some "GROQ_API_KEY" }
          { name = "mistral"; kind = OpenAICompat; baseUrl = "https://api.mistral.ai/v1"; apiKeyEnv = Some "MISTRAL_API_KEY" }
          { name = "xai"; kind = OpenAICompat; baseUrl = "https://api.x.ai/v1"; apiKeyEnv = Some "XAI_API_KEY" }
          { name = "gemini"; kind = OpenAICompat; baseUrl = "https://generativelanguage.googleapis.com/v1beta/openai"; apiKeyEnv = Some "GEMINI_API_KEY" }
          { name = "lmstudio"; kind = OpenAICompat; baseUrl = "http://localhost:1234/v1"; apiKeyEnv = None } ]

    type Config =
        { defaultModel: string
          aliases: Map<string, string>
          providers: Map<string, Provider>
          /// Shell command the default agent runs after every edit
          /// (jern.json "test_command"); exposed to agent source.
          testCommand: string option
          /// MCP servers (jern.json "mcp_servers"); their tools register as
          /// mcp__<server>__<tool> and go through the normal policy stack.
          mcpServers: Mcp.ServerSpec list
          /// Hard run budgets (jern.json "budget": {"llm_calls": N, "tokens": M});
          /// enforced by the budget handler — exhaustion asks for approval.
          budgetLlmCalls: int option
          budgetTokens: int option
          /// Reasoning knobs (jern.json "thinking_tokens"/"reasoning_effort"
          /// or --think/--effort): Anthropic extended-thinking budget and
          /// OpenAI-style reasoning effort. Each bridge consumes its own.
          thinkingTokens: int option
          reasoningEffort: string option
          /// Tool limits (jern.json "limits": {"max_file_bytes", …,
          /// "shell_timeout_seconds"}); applied via Tools.configureLimits.
          limits: Tools.Limits
          /// Policy sources, one per config file that carried a "policy"
          /// object, in load order. Not merged: each keeps its own origin,
          /// because origin decides whether its grants need trust
          /// (PolicyConfig).
          policySources: PolicyConfig.Source list }

    let defaultConfig =
        { defaultModel = "anthropic/" + AnthropicBridge.defaultModel
          aliases = Map.empty
          providers = builtIns |> List.map (fun p -> p.name, p) |> Map.ofList
          testCommand = None
          mcpServers = []
          budgetLlmCalls = None
          budgetTokens = None
          thinkingTokens = None
          reasoningEffort = None
          limits = Tools.defaultLimits
          policySources = [] }

    let private applyFile (config: Config) (path: string, origin: string -> PolicyConfig.Origin) : Config =
        if not (File.Exists path) then config
        else
            let doc = JsonNode.Parse(File.ReadAllText path).AsObject()
            let defaultModel =
                match doc.["default_model"] with
                | null -> config.defaultModel
                | m -> m.GetValue<string>()
            let aliases =
                match doc.["aliases"] with
                | :? JsonObject as o ->
                    o |> Seq.fold (fun acc kv -> Map.add kv.Key (kv.Value.GetValue<string>()) acc) config.aliases
                | _ -> config.aliases
            let providers =
                match doc.["providers"] with
                | :? JsonObject as o ->
                    o
                    |> Seq.fold
                        (fun acc kv ->
                            let spec = kv.Value.AsObject()
                            let existing = Map.tryFind kv.Key acc
                            let provider =
                                { name = kv.Key
                                  kind =
                                    match spec.["kind"] with
                                    | null ->
                                        existing |> Option.map (fun p -> p.kind) |> Option.defaultValue OpenAICompat
                                    | k when k.GetValue<string>() = "anthropic" -> Anthropic
                                    | _ -> OpenAICompat
                                  baseUrl =
                                    match spec.["base_url"] with
                                    | null -> existing |> Option.map (fun p -> p.baseUrl) |> Option.defaultValue ""
                                    | u -> u.GetValue<string>()
                                  apiKeyEnv =
                                    match spec.["api_key_env"] with
                                    | null -> existing |> Option.bind (fun p -> p.apiKeyEnv)
                                    | e -> Some(e.GetValue<string>()) }
                            Map.add kv.Key provider acc)
                        config.providers
                | _ -> config.providers
            let testCommand =
                match doc.["test_command"] with
                | null -> config.testCommand
                | t -> Some(t.GetValue<string>())
            let mcpServers =
                match doc.["mcp_servers"] with
                | :? JsonObject as o ->
                    let parsed =
                        o
                        |> Seq.map (fun kv ->
                            let spec = kv.Value.AsObject()
                            { Mcp.name = kv.Key
                              Mcp.command =
                                match spec.["command"] with
                                | null -> failwithf "mcp_servers.%s needs a \"command\"" kv.Key
                                | c -> c.GetValue<string>()
                              Mcp.args =
                                match spec.["args"] with
                                | :? JsonArray as a ->
                                    a |> Seq.map (fun v -> v.GetValue<string>()) |> List.ofSeq
                                | _ -> []
                              Mcp.env =
                                match spec.["env"] with
                                | :? JsonObject as e ->
                                    e |> Seq.map (fun ev -> ev.Key, ev.Value.GetValue<string>()) |> List.ofSeq
                                | _ -> [] })
                        |> List.ofSeq
                    // Later files override same-named servers from earlier ones.
                    let names = parsed |> List.map (fun s -> s.name) |> Set.ofList
                    (config.mcpServers |> List.filter (fun s -> not (names.Contains s.name))) @ parsed
                | _ -> config.mcpServers
            let budgetLlmCalls, budgetTokens =
                match doc.["budget"] with
                | :? JsonObject as b ->
                    let field (key: string) fallback =
                        match b.[key] with
                        | null -> fallback
                        | v -> Some(v.GetValue<int>())
                    field "llm_calls" config.budgetLlmCalls, field "tokens" config.budgetTokens
                | _ -> config.budgetLlmCalls, config.budgetTokens
            let thinkingTokens =
                match doc.["thinking_tokens"] with
                | null -> config.thinkingTokens
                | t -> Some(t.GetValue<int>())
            let reasoningEffort =
                match doc.["reasoning_effort"] with
                | null -> config.reasoningEffort
                | e -> Some(e.GetValue<string>())
            let limits =
                match doc.["limits"] with
                | :? JsonObject as l ->
                    let intField (key: string) fallback =
                        match l.[key] with null -> fallback | v -> v.GetValue<int>()
                    { Tools.maxFileBytes =
                        (match l.["max_file_bytes"] with
                         | null -> config.limits.maxFileBytes
                         | v -> v.GetValue<int64>())
                      Tools.maxGrepMatches = intField "max_grep_matches" config.limits.maxGrepMatches
                      Tools.maxTreeEntries = intField "max_tree_entries" config.limits.maxTreeEntries
                      Tools.shellTimeoutSeconds =
                        (match l.["shell_timeout_seconds"] with
                         | null -> config.limits.shellTimeoutSeconds
                         | v -> v.GetValue<float>())
                      Tools.evalTimeoutSeconds =
                        (match l.["eval_timeout_seconds"] with
                         | null -> config.limits.evalTimeoutSeconds
                         | v -> v.GetValue<float>()) }
                | _ -> config.limits
            // A "policy" object contributes a source tagged with this file's
            // origin. A malformed one is a startup error, never a silent
            // no-op: a typo in a rule meant to restrict must not look like
            // it applied.
            let policySources =
                match doc.["policy"] with
                | null -> config.policySources
                | node ->
                    match PolicyConfig.parse node with
                    | Error message -> failwithf "%s: %s" path message
                    | Ok policy ->
                        if PolicyConfig.isEmpty policy then config.policySources
                        else config.policySources @ [ { PolicyConfig.origin = origin path
                                                        PolicyConfig.policy = policy } ]
            // An "environment" object is validated like "policy" and applied
            // by the host, never here; outside a host it earns one notice.
            match doc.["environment"] with
            | null -> ()
            | node ->
                match PolicyConfig.parseEnvironment node with
                | Error message -> failwithf "%s: %s" path message
                | Ok environment ->
                    if not (PolicyConfig.environmentIsEmpty environment) && not (PolicyConfig.hostProvidesEnvironment ()) then
                        eprintfn "jern: %s declares an environment (%s) that a hosting runner provides; not applied here"
                            path (PolicyConfig.describeEnvironment environment)
            { defaultModel = defaultModel
              aliases = aliases
              providers = providers
              testCommand = testCommand
              mcpServers = mcpServers
              budgetLlmCalls = budgetLlmCalls
              budgetTokens = budgetTokens
              thinkingTokens = thinkingTokens
              reasoningEffort = reasoningEffort
              limits = limits
              policySources = policySources }

    /// Optional persisted API keys: ~/.config/jern/credentials.json holds
    /// { "<ENV_NAME>": "<key>", … } with 0600 permissions. Environment
    /// variables always win; this only fills in the gaps at startup.
    let credentialsPath () =
        Path.Combine(Environment.GetFolderPath Environment.SpecialFolder.UserProfile,
                     ".config", "jern", "credentials.json")

    /// Set stored credentials as process-local environment variables so the
    /// provider bridges (which read env at call time) pick them up. Called
    /// once at CLI startup; never overrides a variable that is already set.
    let applyCredentials () =
        let path = credentialsPath ()
        if File.Exists path then
            try
                match JsonNode.Parse(File.ReadAllText path) with
                | :? JsonObject as o ->
                    for kv in o do
                        if String.IsNullOrEmpty(Environment.GetEnvironmentVariable kv.Key) then
                            Environment.SetEnvironmentVariable(kv.Key, kv.Value.GetValue<string>())
                | _ -> ()
            with _ -> ()

    /// Persist one credential (merging with the file) and apply it now.
    let saveCredential (envName: string) (key: string) =
        let path = credentialsPath ()
        Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
        let existing =
            if File.Exists path then
                try
                    match JsonNode.Parse(File.ReadAllText path) with
                    | :? JsonObject as o -> o
                    | _ -> JsonObject()
                with _ -> JsonObject()
            else JsonObject()
        existing.[envName] <- JsonValue.Create(key: string)
        File.WriteAllText(path, existing.ToJsonString())
        if not (OperatingSystem.IsWindows()) then
            File.SetUnixFileMode(path, UnixFileMode.UserRead ||| UnixFileMode.UserWrite)
        Environment.SetEnvironmentVariable(envName, key)

    /// Global config, then the workspace's jern.json on top.
    let load (workspaceRoot: string) : Result<Config, string> =
        let files =
            [ Path.Combine(Environment.GetFolderPath Environment.SpecialFolder.UserProfile,
                           ".config", "jern", "config.json"), PolicyConfig.UserConfig
              Path.Combine(workspaceRoot, "jern.json"), PolicyConfig.Workspace ]
        try
            Ok(files |> List.fold applyFile defaultConfig)
        with ex ->
            Error("bad jern config: " + ex.Message)

    /// `alias | provider/model | claude-*` -> provider + bare model id.
    let resolve (config: Config) (modelSpec: string) : Result<Provider * string, string> =
        let spec =
            match Map.tryFind modelSpec config.aliases with
            | Some target -> target
            | None -> modelSpec
        match spec.IndexOf '/' with
        | -1 when spec.StartsWith "claude" ->
            Ok(config.providers.["anthropic"], spec)
        | -1 ->
            Error(sprintf "model '%s' has no provider prefix — use provider/model, e.g. openai/%s (providers: %s)"
                      spec spec (config.providers |> Map.toList |> List.map fst |> String.concat ", "))
        | i ->
            let providerName = spec.Substring(0, i)
            let bare = spec.Substring(i + 1)
            match Map.tryFind providerName config.providers with
            | Some provider when bare <> "" -> Ok(provider, bare)
            | Some _ -> Error(sprintf "model spec '%s' names no model after the provider" spec)
            | None ->
                Error(sprintf "unknown provider '%s' (known: %s; add custom ones in jern.json)"
                          providerName (config.providers |> Map.toList |> List.map fst |> String.concat ", "))

    /// The workspace-config plist handed to agent source (data, no authority).
    let agentConfig (config: Config) : LispVal =
        (match config.testCommand with
         | Some command -> [ Keyword "test_command"; Obj(command :> obj) ]
         | None -> [])
        @ (match config.thinkingTokens with
           | Some tokens -> [ Keyword "thinking_tokens"; Obj(tokens :> obj) ]
           | None -> [])
        @ (match config.reasoningEffort with
           | Some effort -> [ Keyword "reasoning_effort"; Obj(effort :> obj) ]
           | None -> [])
        |> ofList

    /// The budget plist handed to the budget handler (handler environment):
    /// (:llm_calls N :tokens M), omitting unset limits; Nil = unlimited.
    let budget (config: Config) : LispVal =
        (match config.budgetLlmCalls with
         | Some n -> [ Keyword "llm_calls"; Obj(n :> obj) ]
         | None -> [])
        @ (match config.budgetTokens with
           | Some n -> [ Keyword "tokens"; Obj(n :> obj) ]
           | None -> [])
        |> ofList

    /// The per-request model spec inside the canonical request, if any.
    let private requestModel (request: LispVal) =
        match Tools.plistTryGet "model" request with
        | Some (Obj (:? string as m)) -> Some m
        | _ -> None

    /// Build the routing bridge. Precedence for the model of each request:
    /// the request's own :model, then the CLI --model, then the config.
    /// `interrupted` is polled while a call is in flight so Ctrl-C and
    /// /interrupt land even on non-streaming turns.
    let createBridgeWith (config: Config) (cliModel: string option)
                         (onText: (string -> unit) option) (interrupted: unit -> bool)
                         : AnthropicBridge.LlmBridge =
        fun request ->
            let spec =
                match requestModel request, cliModel with
                | Some m, _ -> m
                | None, Some m -> m
                | None, None -> config.defaultModel
            match resolve config spec with
            | Error message -> Choice1Of2 (IronKernel.Ast.Default message)
            | Ok (provider, bareModel) ->
                let bridge =
                    match provider.kind with
                    | Anthropic ->
                        AnthropicBridge.callWithInterrupt (Some bareModel) onText interrupted
                    | OpenAICompat ->
                        OpenAIBridge.callWith provider.baseUrl provider.apiKeyEnv bareModel onText interrupted
                bridge request

    /// Routing bridge without an interrupt probe.
    let createBridge (config: Config) (cliModel: string option) (onText: (string -> unit) option)
                     : AnthropicBridge.LlmBridge =
        createBridgeWith config cliModel onText (fun () -> false)
