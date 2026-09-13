namespace Jern.Host

open System
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json.Nodes

/// `jern doctor` — deployment readiness, made inspectable *before* a run.
///
/// A receipt says what a run did; doctor says what the next run would be
/// allowed to do, and with what code. It answers five questions without
/// starting a session, calling a provider, prompting for anything, or
/// touching the workspace:
///
/// 1. **Runtime source hashes** — the SHA-256 of every Kernel file that
///    carries authority (the handler stack, the built-in policy, the
///    prelude, the tools, and the agent package that will be loaded), plus
///    one digest over the set. That digest is what a deployment pins: two
///    machines that report the same one are running the same brain.
/// 2. **Startup trust decisions** — what the session would do with each
///    thing that needs a yes (a workspace policy file, a config policy's
///    grants, each workspace-declared MCP server), evaluated exactly as the
///    session evaluates it and *without asking*: doctor never writes to the
///    trust store.
/// 3. **Effective permissions** — every policy layer with its digest and
///    provenance, which grants are actually in force, and the blast radius
///    the restrictions compose to.
/// 4. **Sandbox availability** — what would confine a shell command here.
/// 5. **Unsafe configuration** — findings, each with a stable code so a
///    control plane can gate on them.
///
/// Everything is derived from inputs the caller supplies, so the report is
/// reproducible and testable; the only ambient state it reads is the
/// filesystem (source files, the trust store) and the environment (the
/// sandbox probe, provider key variables).
module Doctor =

    /// How much a finding should worry the reader. `Risk` is what makes
    /// `jern doctor` exit non-zero.
    type Severity =
        | Note
        | Risk

    let severityName =
        function
        | Note -> "note"
        | Risk -> "risk"

    /// One thing a reader might act on. `code` is stable across releases —
    /// tooling gates on it, humans read `message`.
    type Finding =
        { severity: Severity
          code: string
          message: string
          remedy: string option }

    /// One runtime source entry of the trusted computing base, hashed as it
    /// sits on disk or marked unreadable with a stable sentinel.
    type SourceHash =
        { /// Install-relative, so the same deployment reports the same names
          /// whatever the absolute paths are ("kernel/policy.ikr").
          name: string
          path: string
          sha256: string
          bytes: int64 }

    /// A yes-or-no the session would reach at startup, without asking.
    type TrustDecision =
        { /// What is being trusted, as the reader knows it.
          subject: string
          /// "workspace-policy" | "config-grants" | "mcp-server"
          kind: string
          /// The identity the trust store keys this decision by.
          identity: string
          /// SHA-256 of the exact content that was (or would be) approved.
          digest: string
          trusted: bool
          /// What the session does with it, given the decision.
          effect: string }

    /// A policy layer as it would compose into this run.
    type PolicyLayer =
        { source: string
          digest: string
          isProtected: bool
          /// Whether this layer's grant half applies. Restrictions always do.
          grantsTrusted: bool
          restrictions: string list
          grants: string list }

    type Sandbox =
        { /// As the run envelope records it: sandbox-exec, bubblewrap,
          /// external, or none.
          mode: string
          platform: string
          detail: string }

    type Report =
        { version: string
          workspace: string
          agentDir: string
          kernelDir: string
          /// True when JERN_KERNEL_DIR points the handler stack somewhere
          /// other than the copy installed beside the binary.
          kernelDirOverridden: bool
          runtime: SourceHash list
          /// One digest over every runtime source: the deployment's pin.
          runtimeDigest: string
          defaultModel: string
          provider: string option
          providerError: string option
          apiKeyEnv: string option
          apiKeyPresent: bool
          testCommand: string option
          testCommandSource: string option
          budgetLlmCalls: int option
          budgetTokens: int option
          limits: Tools.Limits
          /// False when there is no terminal to answer a prompt on: untrusted
          /// grants drop and ask-gated calls deny unless --auto.
          interactive: bool
          trustStore: string
          trust: TrustDecision list
          layers: PolicyLayer list
          maxFilesEdited: int option
          maxLinesChanged: int option
          protectedPaths: string list
          /// The grant lines that actually apply, labelled by their source.
          grantsInForce: string list
          sandbox: Sandbox
          findings: Finding list }

    let hasRisk (report: Report) =
        report.findings |> List.exists (fun f -> f.severity = Risk)

    /// 0 when nothing risky was found, 1 when something was.
    let exitCode (report: Report) = if hasRisk report then 1 else 0

    // -- runtime source hashes ---------------------------------------------

    let private hashFile (path: string) =
        use stream = File.OpenRead path
        Convert.ToHexString(SHA256.HashData stream).ToLowerInvariant()

    let private unreadableHash = "unreadable"

    let private unreadableSource (prefix: string) (dir: string) (path: string) (isDirectory: bool) =
        let relative = Path.GetRelativePath(dir, path).Replace('\\', '/')
        let name =
            if relative = "." then prefix + "**"
            elif isDirectory then prefix + relative + "/**"
            else prefix + relative
        { name = name
          path = path
          sha256 = unreadableHash
          bytes = -1L }

    let private isUnreadableDirectory (path: string) =
        try
            use _ = Directory.EnumerateFileSystemEntries(path).GetEnumerator()
            false
        with
        | :? UnauthorizedAccessException -> true
        | :? IOException -> false
        | :? ArgumentException -> false

    let private enumerateIkrFiles (prefix: string) (root: string) (startDir: string) (recursive: bool) =
        let files = ResizeArray<string>()
        let unreadable = ResizeArray<SourceHash>()
        let rec loop (dir: string) =
            try
                for path in Directory.EnumerateFileSystemEntries(dir) do
                    try
                        let attrs = File.GetAttributes path
                        if attrs.HasFlag FileAttributes.Directory then
                            if recursive then loop path
                        elif String.Equals(Path.GetExtension path, ".ikr", StringComparison.OrdinalIgnoreCase) then
                            files.Add path
                    with _ ->
                        unreadable.Add(unreadableSource prefix root path (isUnreadableDirectory path))
            with _ ->
                unreadable.Add(unreadableSource prefix root dir true)
        loop startDir
        List.ofSeq files, List.ofSeq unreadable

    /// Hash a set of files under `dir`, naming each one `prefix` + its path
    /// relative to `dir`. A file that cannot be read stays in the list with
    /// a stable sentinel hash, so the runtime digest still covers the full
    /// declared source set.
    let private hashUnder (prefix: string) (dir: string) (files: string list) =
        files
        |> List.map (fun file ->
            let name = prefix + Path.GetRelativePath(dir, file).Replace('\\', '/')
            let bytes =
                try FileInfo(file).Length
                with _ -> -1L
            let sha256 =
                try hashFile file
                with _ -> unreadableHash
            { name = name
              path = file
              sha256 = sha256
              bytes = bytes })

    /// The digest of a source set: names and hashes only, so it is
    /// independent of where the install lives.
    let digestOf (sources: SourceHash list) =
        sources
        |> List.sortBy (fun s -> s.name)
        |> List.map (fun s -> s.name + " " + s.sha256)
        |> String.concat "\n"
        |> Trust.contentHash

    // -- sandbox ------------------------------------------------------------

    let private platformName () =
        if OperatingSystem.IsLinux() then "linux"
        elif OperatingSystem.IsMacOS() then "macos"
        elif OperatingSystem.IsWindows() then "windows"
        else "unknown"

    let private describeSandbox (mode: string) =
        match mode with
        | "sandbox-exec" ->
            "shell writes confined to the workspace, /tmp and /dev; reads and network open"
        | "bubblewrap" ->
            "the filesystem mounts read-only with the workspace and /tmp writable; reads and network open"
        | "external" ->
            "JERN_SANDBOX=external: the host says it already confines this process; jern adds none"
        | _ ->
            "shell commands run unconfined; approval is the only gate"

    let sandboxNow () =
        let mode = Tools.sandboxMode ()
        { mode = mode; platform = platformName (); detail = describeSandbox mode }

    // -- inputs -------------------------------------------------------------

    /// Everything the report is derived from. The caller supplies the
    /// trust predicate because only it knows the run's `--policy-trust`
    /// pins; doctor asks nothing and remembers nothing.
    type Inputs =
        { root: string
          version: string
          kernelDir: string
          kernelDirOverridden: bool
          agentDir: string
          config: Providers.Config
          policySources: PolicyConfig.Source list
          /// identity -> canonical content -> already blessed?
          grantsTrusted: string -> string -> bool
          trustStorePath: string
          interactive: bool }

    /// A value that looks like a secret someone pasted into a config file,
    /// rather than a reference to one the environment supplies.
    let private looksLikeSecret (name: string) (value: string) =
        let n = name.ToLowerInvariant()
        [ "token"; "key"; "secret"; "password"; "passwd"; "credential" ]
        |> List.exists n.Contains
        && value <> ""
        && not (value.StartsWith "$")
        && not (value.Contains "${")

    /// A config file that only the user can read, as jern writes them.
    let private isPrivateFile (path: string) =
        if OperatingSystem.IsWindows() || not (File.Exists path) then true
        else
            try
                let mode = File.GetUnixFileMode path
                let shared =
                    UnixFileMode.GroupRead ||| UnixFileMode.GroupWrite
                    ||| UnixFileMode.OtherRead ||| UnixFileMode.OtherWrite
                mode &&& shared = UnixFileMode.None
            with _ -> true

    // -- the report ---------------------------------------------------------

    let inspect (input: Inputs) : Report =
        let findings = ResizeArray<Finding>()
        let add severity code message remedy =
            findings.Add { severity = severity; code = code; message = message; remedy = remedy }

        // 1. Runtime source hashes: the handler stack and the agent that
        //    would be loaded, in that order.
        let kernelFiles, unreadableKernelEntries =
            if Directory.Exists input.kernelDir then
                enumerateIkrFiles "kernel/" input.kernelDir input.kernelDir true
            else [], []
        let agentFiles, unreadableAgentEntries =
            if Directory.Exists input.agentDir then
                enumerateIkrFiles "agent/" input.agentDir input.agentDir true
            else [], []
        let agentFiles =
            agentFiles |> List.sortBy (fun file -> Path.GetRelativePath(input.agentDir, file).Replace('\\', '/'))
        let hashedKernel =
            (hashUnder "kernel/" input.kernelDir kernelFiles @ unreadableKernelEntries)
            |> List.sortBy (fun source -> source.name)
        let hashedAgent =
            (hashUnder "agent/" input.agentDir agentFiles @ unreadableAgentEntries)
            |> List.sortBy (fun source -> source.name)
        let unreadableKernel =
            hashedKernel |> List.filter (fun source -> source.sha256 = unreadableHash) |> List.length
        let unreadableAgent =
            hashedAgent |> List.filter (fun source -> source.sha256 = unreadableHash) |> List.length
        let runtime = hashedKernel @ hashedAgent
        if kernelFiles.IsEmpty && unreadableKernelEntries.IsEmpty then
            add Risk "runtime.kernel-missing"
                (sprintf "no handler stack at %s — the policy and prelude jern runs cannot be read" input.kernelDir)
                (Some "reinstall jern, or point JERN_KERNEL_DIR at a good copy")
        elif unreadableKernel > 0 then
            add Risk "runtime.kernel-unreadable"
                (sprintf "%d handler stack file(s) under %s could not be read and use a sentinel in the runtime fingerprint"
                    unreadableKernel input.kernelDir)
                (Some "fix file permissions or reinstall jern so every Kernel source can be hashed")
        if agentFiles.IsEmpty && unreadableAgentEntries.IsEmpty then
            add Risk "runtime.agent-missing"
                (sprintf "no agent source at %s — there is no loop to run" input.agentDir)
                (Some "reinstall jern, or pass --agent <dir>")
        elif unreadableAgent > 0 then
            add Risk "runtime.agent-unreadable"
                (sprintf "%d agent source file(s) under %s could not be read and use a sentinel in the runtime fingerprint"
                    unreadableAgent input.agentDir)
                (Some "fix file permissions or pass --agent <dir> pointing at a readable package")
        if input.kernelDirOverridden then
            add Risk "runtime.kernel-overridden"
                (sprintf "JERN_KERNEL_DIR replaces the trusted computing base with %s" input.kernelDir)
                (Some "unset JERN_KERNEL_DIR to run the handler stack installed beside the binary")

        // 2. Startup trust decisions, decided exactly as a session decides
        //    them — and never asked.
        let trust = ResizeArray<TrustDecision>()

        let workspacePolicyPath = Path.Combine(input.root, ".jern", "policy.ikr")
        if File.Exists workspacePolicyPath then
            match try Some(File.ReadAllText workspacePolicyPath) with _ -> None with
            | Some content ->
                let identity = Path.GetFullPath workspacePolicyPath
                let trusted = input.grantsTrusted identity content
                trust.Add
                    { subject = ".jern/policy.ikr"
                      kind = "workspace-policy"
                      identity = identity
                      digest = Trust.contentHash content
                      trusted = trusted
                      effect = if trusted then "loaded" else "skipped; the built-in rules stand" }
                if trusted then
                    add Note "policy.workspace-kernel"
                        ".jern/policy.ikr is trusted: arbitrary Kernel runs with jern's authority (restrictions from config still win)"
                        (Some "review it with: jern policy --show-compiled")
                else
                    add Note "policy.workspace-untrusted"
                        ".jern/policy.ikr is not trusted yet, so it is skipped and the built-in rules stand"
                        (Some "run jern interactively once to review it")
            | None ->
                let identity = Path.GetFullPath workspacePolicyPath
                trust.Add
                    { subject = ".jern/policy.ikr"
                      kind = "workspace-policy"
                      identity = identity
                      digest = unreadableHash
                      trusted = false
                      effect = "unreadable; skipped" }
                add Risk "policy.workspace-unreadable"
                    ".jern/policy.ikr exists but could not be read, so it is skipped and the built-in rules stand"
                    (Some "fix file permissions or remove it")

        for source in input.policySources do
            if PolicyConfig.hasGrants source.policy then
                let canonical = PolicyConfig.canonicalJson source.policy
                let identity = PolicyConfig.trustIdentity source.origin
                let trusted =
                    match source.origin with
                    | PolicyConfig.Workspace _ -> input.grantsTrusted identity canonical
                    | _ -> true
                let digest = PolicyConfig.digest source.policy
                trust.Add
                    { subject = PolicyConfig.originLabel source.origin + " grants"
                      kind = "config-grants"
                      identity = identity
                      digest = digest
                      trusted = trusted
                      effect =
                        if trusted then "grants apply"
                        else "grants dropped; the restrictions still apply" }
                if not trusted then
                    add Note "policy.grants-dropped"
                        (sprintf "%s grants extra permissions that are not trusted here, so they are dropped"
                             (PolicyConfig.originLabel source.origin))
                        (Some(sprintf "bless them in an unattended run with: --policy-trust %s" digest))

        for spec in input.config.mcpServers do
            match Mcp.trustIdentity spec with
            | None ->
                trust.Add
                    { subject = "mcp server " + spec.name
                      kind = "mcp-server"
                      identity = spec.name
                      digest = Trust.contentHash(Mcp.canonicalJson spec)
                      trusted = true
                      effect = "starts (declared by your own config)" }
            | Some identity ->
                let canonical = Mcp.canonicalJson spec
                let digest = Trust.contentHash canonical
                let trusted = input.grantsTrusted identity canonical
                trust.Add
                    { subject = "mcp server " + spec.name
                      kind = "mcp-server"
                      identity = identity
                      digest = digest
                      trusted = trusted
                      effect = if trusted then "starts" else "not started; its tools never register" }
                if not trusted then
                    add Note "mcp.untrusted"
                        (sprintf "MCP server '%s' is declared by the workspace and is not trusted, so it will not start"
                             spec.name)
                        (Some(sprintf "pin it in an unattended run with: --policy-trust %s" digest))
            for name, value in spec.env do
                if spec.workspaceConfig.IsSome && looksLikeSecret name value then
                    add Risk "mcp.inline-secret"
                        (sprintf "MCP server '%s' carries a literal %s in %s"
                             spec.name name (defaultArg spec.workspaceConfig "the workspace config"))
                        (Some "keep the value in the environment and reference it there")

        // 3. Effective permissions: every layer, and what the grants and
        //    restrictions compose to.
        let layers =
            input.policySources
            |> List.map (fun source ->
                let grantsTrusted =
                    if not (PolicyConfig.hasGrants source.policy) then true
                    else
                        match source.origin with
                        | PolicyConfig.Workspace _ ->
                            input.grantsTrusted
                                (PolicyConfig.trustIdentity source.origin)
                                (PolicyConfig.canonicalJson source.policy)
                        | _ -> true
                { source = PolicyConfig.originLabel source.origin
                  digest = PolicyConfig.digest source.policy
                  isProtected = (match source.origin with PolicyConfig.Baseline _ -> true | _ -> false)
                  grantsTrusted = grantsTrusted
                  restrictions = PolicyConfig.describeRestrictions source.policy
                  grants = PolicyConfig.describeGrants source.policy })

        let allPolicies = input.policySources |> List.map (fun s -> s.policy)
        let maxFilesEdited, maxLinesChanged, protectedPaths = PolicyConfig.effectiveLimits allPolicies

        // Only a layer whose grants survived trust loosens anything.
        let inForce =
            List.zip layers allPolicies
            |> List.filter (fun (layer, _) -> layer.grantsTrusted)
            |> List.map snd
        let grantsInForce =
            List.zip layers allPolicies
            |> List.collect (fun (layer, policy) ->
                if layer.grantsTrusted then
                    PolicyConfig.describeGrants policy |> List.map (fun line -> layer.source + ": " + line)
                else [])

        let allowInForce = inForce |> List.collect (fun p -> p.allow)
        if allowInForce |> List.contains "*" then
            add Risk "policy.allow-all"
                "a trusted grant allows every tool without approval (allow: *)"
                (Some "name the tools the agent needs, or a pack such as pack:read")
        if allowInForce |> List.exists (fun a -> a = "shell") then
            add Risk "policy.allow-shell"
                "a trusted grant allows every shell command without approval (allow: shell)"
                (Some "use shell_allow with the command words the agent needs")
        for pattern in allowInForce do
            if pattern <> "*" && pattern.EndsWith "*" then
                add Note "policy.allow-wildcard"
                    (sprintf "a trusted grant auto-allows every tool matching '%s'" pattern)
                    None
        if inForce |> List.exists (fun p -> p.memory = Some "allow") then
            add Note "policy.memory-allow"
                "a trusted grant lets the agent write workspace memory without approval"
                None
        if input.policySources.IsEmpty then
            add Note "policy.none"
                "no \"policy\" in configuration: only the built-in rules apply (reads allow; writes, shell, and MCP tools ask)"
                (Some "add a \"policy\" object to jern.json, or run: jern policy init")
        elif maxFilesEdited.IsNone && maxLinesChanged.IsNone && protectedPaths.IsEmpty
             && allPolicies |> List.forall (fun p -> p.editsWithin.IsEmpty) then
            add Note "policy.no-blast-radius"
                "no edits_within, protected_paths, max_files_edited or max_lines_changed: an approved edit may touch any file in the workspace"
                (Some "bound the run with \"edits_within\" or \"max_files_edited\"")

        // 4. Sandbox availability.
        let sandbox = sandboxNow ()
        match sandbox.mode with
        | "none" ->
            add Risk "sandbox.none"
                (sprintf "no OS sandbox on %s: an approved shell command may write anywhere the user can"
                     sandbox.platform)
                (Some(
                    if sandbox.platform = "linux" then "install bubblewrap (bwrap), or set JERN_SANDBOX=external where the host confines the process"
                    else "run jern where an OS sandbox exists, or set JERN_SANDBOX=external where the host confines the process"))
        | "external" ->
            add Note "sandbox.external"
                "JERN_SANDBOX=external is set: jern adds no sandbox and trusts the host to confine this process"
                (Some "set it only where something outside jern really does")
        | _ -> ()

        // 5. The rest of the readiness surface: model reachability, budgets,
        //    the acceptance command, and the files that hold secrets.
        let provider, providerError, apiKeyEnv, apiKeyPresent =
            match Providers.resolve input.config input.config.defaultModel with
            | Error message ->
                add Risk "model.unresolved"
                    (sprintf "the default model cannot be resolved: %s" message)
                    (Some "fix \"model\" in jern.json, or pass --model provider/model")
                None, Some message, None, false
            | Ok(provider, _) ->
                let present =
                    match provider.apiKeyEnv with
                    | None -> true
                    | Some name -> not (String.IsNullOrEmpty(Environment.GetEnvironmentVariable name))
                if not present then
                    add Risk "model.no-key"
                        (sprintf "provider '%s' needs %s and it is not set — no model call can be made"
                             provider.name (defaultArg provider.apiKeyEnv "an API key"))
                        (Some "export the variable, or save a key with jern ui → settings")
                Some provider.name, None, provider.apiKeyEnv, present

        if input.config.testCommand.IsNone then
            add Note "verify.no-test-command"
                "no \"test_command\": run_tests and jern verify have nothing to run, so a run's work cannot be acceptance-checked"
                (Some "add \"test_command\" to jern.json")
        if input.config.budgetLlmCalls.IsNone && input.config.budgetTokens.IsNone then
            add Note "budget.unbounded"
                "no budget: a run may make model calls until it decides to stop"
                (Some "set \"budget\": { \"llm_calls\": n, \"tokens\": m } in jern.json, or pass --budget")
        if not input.interactive then
            add Note "approvals.headless"
                "no terminal to ask on: ask-gated calls are denied unless --auto, and untrusted grants are dropped"
                None
        if not (isPrivateFile input.trustStorePath) then
            add Risk "trust.permissions"
                (sprintf "the trust store %s is readable or writable beyond its owner" input.trustStorePath)
                (Some "chmod 600 it: anyone who can write it can bless a policy for you")
        let credentials = Providers.credentialsPath ()
        if File.Exists credentials && not (isPrivateFile credentials) then
            add Risk "credentials.permissions"
                (sprintf "the credentials file %s is readable or writable beyond its owner" credentials)
                (Some "chmod 600 it")

        { version = input.version
          workspace = input.root
          agentDir = input.agentDir
          kernelDir = input.kernelDir
          kernelDirOverridden = input.kernelDirOverridden
          runtime = runtime
          runtimeDigest = digestOf runtime
          defaultModel = input.config.defaultModel
          provider = provider
          providerError = providerError
          apiKeyEnv = apiKeyEnv
          apiKeyPresent = apiKeyPresent
          testCommand = input.config.testCommand
          testCommandSource = input.config.testCommandSource
          budgetLlmCalls = input.config.budgetLlmCalls
          budgetTokens = input.config.budgetTokens
          limits = input.config.limits
          interactive = input.interactive
          trustStore = input.trustStorePath
          trust = List.ofSeq trust
          layers = layers
          maxFilesEdited = maxFilesEdited
          maxLinesChanged = maxLinesChanged
          protectedPaths = protectedPaths
          grantsInForce = grantsInForce
          sandbox = sandbox
          // Risks first, notes after, each group in the order it was found.
          findings =
            findings
            |> List.ofSeq
            |> List.sortBy (fun f -> match f.severity with Risk -> 0 | Note -> 1) }

    /// The inputs a live command uses: the installed handler stack, the
    /// agent that would run, and this machine's trust store.
    let inputsFor (root: string) (config: Providers.Config)
                  (policySources: PolicyConfig.Source list)
                  (agentDir: string option)
                  (grantsTrusted: string -> string -> bool)
                  (interactive: bool) : Inputs =
        { root = root
          version = AgentEnv.version
          kernelDir = Session.kernelDir ()
          kernelDirOverridden =
            not (String.IsNullOrEmpty(Environment.GetEnvironmentVariable "JERN_KERNEL_DIR"))
          agentDir = defaultArg agentDir (Session.defaultAgentDir ())
          config = config
          policySources = policySources
          grantsTrusted = grantsTrusted
          trustStorePath = Trust.defaultStorePath ()
          interactive = interactive }

    // -- rendering ----------------------------------------------------------

    /// Colors are the front-end's business; the host renders structure.
    type Palette =
        { title: string -> string
          label: string -> string
          dim: string -> string
          good: string -> string
          warn: string -> string
          bad: string -> string }

    let plain =
        { title = id; label = id; dim = id; good = id; warn = id; bad = id }

    let private shortDigest (digest: string) =
        if digest.Length >= 12 then digest.Substring(0, 12) else digest

    let private bytesText (n: int64) =
        if n < 0L then "?"
        elif n >= 1024L then sprintf "%.1fkB" (float n / 1024.0) else sprintf "%dB" n

    let private plural n singular =
        if n = 1 then sprintf "1 %s" singular else sprintf "%d %ss" n singular

    /// The permissions block as label/value rows, shared by every rendering
    /// so the terminal and JSON cannot drift apart in what they claim.
    let permissionRows (r: Report) : (string * string) list =
        [ yield "built-in", "reads allow; writes, shell, and MCP tools ask"
          for layer in r.layers do
              let marks =
                  [ if layer.isProtected then yield "protected"
                    if not layer.grants.IsEmpty && not layer.grantsTrusted then yield "grants dropped" ]
              let suffix = if marks.IsEmpty then "" else " (" + String.concat ", " marks + ")"
              let rules = layer.restrictions @ layer.grants
              yield layer.source + " " + shortDigest layer.digest + suffix,
                    (if rules.IsEmpty then "(no rules)" else String.concat " · " rules)
          let blast =
              [ (match r.maxFilesEdited with
                 | Some n -> sprintf "at most %d files" n
                 | None -> "files unbounded")
                (match r.maxLinesChanged with
                 | Some n -> sprintf "at most %d lines" n
                 | None -> "lines unbounded")
                (if r.protectedPaths.IsEmpty then "nothing protected"
                 else "protected: " + String.concat ", " r.protectedPaths) ]
          yield "blast radius", String.concat " · " blast
          yield "grants in force",
                (if r.grantsInForce.IsEmpty then "none" else String.concat " · " r.grantsInForce)
          yield "test command",
                (match r.testCommand with
                 | Some command -> sprintf "%s (%s)" command (defaultArg r.testCommandSource "jern.json")
                 | None -> "none")
          yield "budget",
                ([ match r.budgetLlmCalls with Some n -> yield sprintf "%d model calls" n | None -> ()
                   match r.budgetTokens with Some n -> yield sprintf "%d tokens" n | None -> () ]
                 |> function [] -> "unbounded" | parts -> String.concat " · " parts)
          yield "limits",
                sprintf "shell %.0fs · tests %.0fs · eval %.0fs · file %s · result %d chars"
                    r.limits.shellTimeoutSeconds r.limits.testTimeoutSeconds r.limits.evalTimeoutSeconds
                    (bytesText r.limits.maxFileBytes) r.limits.maxToolResultChars ]

    let render (palette: Palette) (r: Report) : string =
        let b = StringBuilder()
        let line (s: string) = b.AppendLine s |> ignore
        let row label value =
            let padding = String(' ', max 1 (16 - String.length label))
            line ("    " + palette.label label + padding + value)

        line ""
        line (" " + palette.title "jern doctor" + "  " + palette.dim r.workspace)
        line ("  " + palette.dim (sprintf "jern %s · agent %s" r.version r.agentDir))

        line ""
        line ("  " + palette.label "runtime"
              + "  " + shortDigest r.runtimeDigest
              + palette.dim (sprintf "  (%s)" (plural r.runtime.Length "source")))
        for source in r.runtime do
            let padding = String(' ', max 1 (26 - String.length source.name))
            line ("    " + source.name + padding + palette.dim (shortDigest source.sha256)
                  + palette.dim ("  " + bytesText source.bytes))
        line ("    " + palette.dim (sprintf "kernel dir: %s%s" r.kernelDir
                                        (if r.kernelDirOverridden then "  (JERN_KERNEL_DIR)" else "")))

        line ""
        line ("  " + palette.label "trust" + "  " + palette.dim r.trustStore)
        if r.trust.IsEmpty then
            line ("    " + palette.dim "nothing needs a first-use decision in this workspace")
        else
            for decision in r.trust do
                let mark =
                    if decision.trusted then palette.good "trusted" else palette.warn "not trusted"
                let padding = String(' ', max 1 (26 - String.length decision.subject))
                line ("    " + decision.subject + padding + palette.dim (shortDigest decision.digest)
                      + "  " + mark + palette.dim ("  " + decision.effect))

        line ""
        line ("  " + palette.label "permissions")
        for label, value in permissionRows r do
            row label value

        line ""
        let sandboxMark = if r.sandbox.mode = "none" then palette.bad else palette.good
        line ("  " + palette.label "sandbox" + "  " + sandboxMark r.sandbox.mode
              + palette.dim (sprintf " on %s — %s" r.sandbox.platform r.sandbox.detail))

        line ""
        line ("  " + palette.label "model" + "  " + r.defaultModel
              + palette.dim (
                  match r.provider, r.apiKeyEnv with
                  | Some name, Some env ->
                      sprintf "  (%s, %s %s)" name env (if r.apiKeyPresent then "set" else "MISSING")
                  | Some name, None -> sprintf "  (%s, no key needed)" name
                  | None, _ -> "  (" + defaultArg r.providerError "unresolved" + ")"))

        line ""
        if r.findings.IsEmpty then
            line ("  " + palette.good "no findings — nothing unsafe or unset that doctor can see")
        else
            line ("  " + palette.label "findings")
            let mutable risks = 0
            let mutable notes = 0
            for finding in r.findings do
                let tag =
                    match finding.severity with
                    | Risk ->
                        risks <- risks + 1
                        palette.bad "risk"
                    | Note ->
                        notes <- notes + 1
                        palette.warn "note"
                line ("    " + tag + "  " + palette.dim finding.code)
                line ("          " + finding.message)
                match finding.remedy with
                | Some remedy -> line ("          " + palette.dim ("→ " + remedy))
                | None -> ()
            line ""
            let verdict = sprintf "%s, %s" (plural risks "risk") (plural notes "note")
            line ("  " + (if risks > 0 then palette.bad verdict else palette.warn verdict))
        b.ToString()

    /// The same facts as data, for a control plane that gates on them.
    let renderJson (r: Report) : string =
        let optional (value: string option) : JsonNode =
            match value with
            | Some v -> JsonValue.Create v :> JsonNode
            | None -> null
        let optionalInt (value: int option) : JsonNode =
            match value with
            | Some v -> JsonValue.Create v :> JsonNode
            | None -> null
        let array (values: string list) =
            let a = JsonArray()
            for v in values do a.Add(JsonValue.Create v)
            a

        let doc = JsonObject()
        doc.["status"] <- JsonValue.Create(if hasRisk r then "risk" else "ok")
        doc.["version"] <- JsonValue.Create r.version
        doc.["workspace"] <- JsonValue.Create r.workspace
        doc.["agent"] <- JsonValue.Create r.agentDir

        let runtime = JsonObject()
        runtime.["digest"] <- JsonValue.Create r.runtimeDigest
        runtime.["kernel_dir"] <- JsonValue.Create r.kernelDir
        runtime.["kernel_dir_overridden"] <- JsonValue.Create r.kernelDirOverridden
        let sources = JsonArray()
        for source in r.runtime do
            let s = JsonObject()
            s.["name"] <- JsonValue.Create source.name
            s.["path"] <- JsonValue.Create source.path
            s.["sha256"] <- JsonValue.Create source.sha256
            s.["bytes"] <- JsonValue.Create source.bytes
            sources.Add s
        runtime.["sources"] <- sources
        doc.["runtime"] <- runtime

        let model = JsonObject()
        model.["default"] <- JsonValue.Create r.defaultModel
        model.["provider"] <- optional r.provider
        model.["error"] <- optional r.providerError
        model.["api_key_env"] <- optional r.apiKeyEnv
        model.["api_key_present"] <- JsonValue.Create r.apiKeyPresent
        doc.["model"] <- model

        let trust = JsonObject()
        trust.["store"] <- JsonValue.Create r.trustStore
        trust.["interactive"] <- JsonValue.Create r.interactive
        let decisions = JsonArray()
        for decision in r.trust do
            let d = JsonObject()
            d.["subject"] <- JsonValue.Create decision.subject
            d.["kind"] <- JsonValue.Create decision.kind
            d.["identity"] <- JsonValue.Create decision.identity
            d.["digest"] <- JsonValue.Create decision.digest
            d.["trusted"] <- JsonValue.Create decision.trusted
            d.["effect"] <- JsonValue.Create decision.effect
            decisions.Add d
        trust.["decisions"] <- decisions
        doc.["trust"] <- trust

        let permissions = JsonObject()
        let layers = JsonArray()
        for layer in r.layers do
            let l = JsonObject()
            l.["source"] <- JsonValue.Create layer.source
            l.["digest"] <- JsonValue.Create layer.digest
            l.["protected"] <- JsonValue.Create layer.isProtected
            l.["grants_trusted"] <- JsonValue.Create layer.grantsTrusted
            l.["restrictions"] <- array layer.restrictions
            l.["grants"] <- array layer.grants
            layers.Add l
        permissions.["layers"] <- layers
        permissions.["max_files_edited"] <- optionalInt r.maxFilesEdited
        permissions.["max_lines_changed"] <- optionalInt r.maxLinesChanged
        permissions.["protected_paths"] <- array r.protectedPaths
        permissions.["grants_in_force"] <- array r.grantsInForce
        permissions.["test_command"] <- optional r.testCommand
        permissions.["test_command_source"] <- optional r.testCommandSource
        let budget = JsonObject()
        budget.["llm_calls"] <- optionalInt r.budgetLlmCalls
        budget.["tokens"] <- optionalInt r.budgetTokens
        permissions.["budget"] <- budget
        let limits = JsonObject()
        limits.["max_file_bytes"] <- JsonValue.Create r.limits.maxFileBytes
        limits.["max_tool_result_chars"] <- JsonValue.Create r.limits.maxToolResultChars
        limits.["shell_timeout_seconds"] <- JsonValue.Create r.limits.shellTimeoutSeconds
        limits.["test_timeout_seconds"] <- JsonValue.Create r.limits.testTimeoutSeconds
        limits.["eval_timeout_seconds"] <- JsonValue.Create r.limits.evalTimeoutSeconds
        permissions.["limits"] <- limits
        doc.["permissions"] <- permissions

        let sandbox = JsonObject()
        sandbox.["mode"] <- JsonValue.Create r.sandbox.mode
        sandbox.["platform"] <- JsonValue.Create r.sandbox.platform
        sandbox.["detail"] <- JsonValue.Create r.sandbox.detail
        doc.["sandbox"] <- sandbox

        let findings = JsonArray()
        for finding in r.findings do
            let f = JsonObject()
            f.["severity"] <- JsonValue.Create(severityName finding.severity)
            f.["code"] <- JsonValue.Create finding.code
            f.["message"] <- JsonValue.Create finding.message
            f.["remedy"] <- optional finding.remedy
            findings.Add f
        doc.["findings"] <- findings
        doc.ToJsonString(System.Text.Json.JsonSerializerOptions(WriteIndented = true))
