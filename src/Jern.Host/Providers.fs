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
          testCommand: string option }

    let defaultConfig =
        { defaultModel = "anthropic/" + AnthropicBridge.defaultModel
          aliases = Map.empty
          providers = builtIns |> List.map (fun p -> p.name, p) |> Map.ofList
          testCommand = None }

    let private applyFile (config: Config) (path: string) : Config =
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
            { defaultModel = defaultModel
              aliases = aliases
              providers = providers
              testCommand = testCommand }

    /// Global config, then the workspace's jern.json on top.
    let load (workspaceRoot: string) : Result<Config, string> =
        let files =
            [ Path.Combine(Environment.GetFolderPath Environment.SpecialFolder.UserProfile,
                           ".config", "jern", "config.json")
              Path.Combine(workspaceRoot, "jern.json") ]
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
        match config.testCommand with
        | Some command ->
            ofList [ Keyword "test_command"; Obj(command :> obj) ]
        | None -> Nil

    /// The per-request model spec inside the canonical request, if any.
    let private requestModel (request: LispVal) =
        match Tools.plistTryGet "model" request with
        | Some (Obj (:? string as m)) -> Some m
        | _ -> None

    /// Build the routing bridge. Precedence for the model of each request:
    /// the request's own :model, then the CLI --model, then the config.
    let createBridge (config: Config) (cliModel: string option) (onText: (string -> unit) option)
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
                    | Anthropic -> AnthropicBridge.callWith (Some bareModel) onText
                    | OpenAICompat -> OpenAIBridge.call provider.baseUrl provider.apiKeyEnv bareModel onText
                bridge request
