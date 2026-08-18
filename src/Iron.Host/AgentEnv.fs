namespace Iron.Host

open System
open System.Reflection
open IronKernel
open IronKernel.Ast
open IronKernel.Eval
open IronKernel.Errors
open IronKernel.SymbolTable

/// Builds the restricted environment the agent runs in.
///
/// The agent holds no authority: its environment is bootstrapped from the
/// `safe` capability profile, so it cannot reach raw CLR interop, host I/O
/// files, source loading, or CLR tasks. Everything it may do beyond pure
/// computation arrives as explicit host-injected bindings — applicatives the
/// host implements, and unforgeable effect tags the host creates. Authority
/// lives in the handlers the host installs around those tags, not in agent
/// code.
module AgentEnv =

    /// Product version from MSBuild `<Version>` (root `version` file).
    let version =
        let asm = Assembly.GetExecutingAssembly()
        match asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>() with
        | null ->
            match asm.GetName().Version with
            | null -> "0.0.0"
            | v -> sprintf "%d.%d.%d" v.Major v.Minor v.Build
        | attr ->
            let raw = attr.InformationalVersion
            match raw.IndexOf('+') with
            | -1 -> raw
            | i -> raw.Substring(0, i)

    /// Host-injected applicative: evaluated arguments in, one value out.
    let applicative invoke =
        Applicative(PrimitiveOperative { identity = None; invoke = invoke })

    /// iron/host-version : () -> string
    let hostVersion env cont = function
        | [] -> bounceContinue env cont (Obj(version :> obj))
        | bad -> signal cont (NumArgs(0, bad))

    /// Effect tags are created by the host per session. They are `PromptTag`
    /// values behind fresh GUIDs, so agent code can perform against them but
    /// can never forge one that a host handler answers to.
    type EffectTags =
        { llmCall: LispVal
          toolCall: LispVal
          askUser: LispVal
          approve: LispVal
          log: LispVal }

    let newEffectTags () =
        { llmCall = PromptTag(Guid.NewGuid())
          toolCall = PromptTag(Guid.NewGuid())
          askUser = PromptTag(Guid.NewGuid())
          approve = PromptTag(Guid.NewGuid())
          log = PromptTag(Guid.NewGuid()) }

    let effectBindings tags =
        [ "iron/llm-call", tags.llmCall
          "iron/tool-call", tags.toolCall
          "iron/ask-user", tags.askUser
          "iron/approve", tags.approve
          "iron/log", tags.log ]

    /// iron/show : value -> string (the printer, for messages and tests).
    let show env cont = function
        | [value] -> bounceContinue env cont (Obj(showVal value :> obj))
        | bad -> signal cont (NumArgs(1, bad))

    /// Everything the agent environment gets from the host.
    let baseBindings tags =
        ("iron/host-version", applicative hostVersion)
        :: ("iron/show", applicative show)
        :: effectBindings tags

    /// Bootstrap the safe-profile standard environment and inject the host
    /// surface. Returns the agent environment plus the session's effect tags
    /// (the host keeps the tags to install handlers around agent code).
    let create () : Choice<LispError, LispVal * EffectTags> =
        match Emit.bootstrapEnvForProfile Safe with
        | Choice1Of2 error -> Choice1Of2 error
        | Choice2Of2 std ->
            let tags = newEffectTags ()
            let env = bindVars std (baseBindings tags)
            Choice2Of2(env, tags)
