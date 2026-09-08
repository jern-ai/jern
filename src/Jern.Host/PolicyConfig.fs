namespace Jern.Host

open System
open System.Text
open System.Text.Json.Nodes

/// Policy from configuration — "branch protection for agents".
///
/// A `"policy"` object in jern.json (or a protected CI baseline) compiles to
/// ordinary Kernel source that installs *layers* on the policy handler. The
/// layering is the load-bearing part (docs/roadmap-governance.md §2):
///
/// - **Restrictions** (`edits_within`, `deny`, `memory: ask|deny`) only ever
///   tighten. They compose by severity with everything else, so no later
///   layer — not a trusted grant, not a hand-written `.jern/policy.ikr`
///   rebinding `tool-policy` — can turn a restriction's denial into an
///   approval.
/// - **Grants** (`shell_allow`, `allow`, `memory: allow`) relax the *base*
///   policy only. They can loosen approvals, so a repository-supplied grant
///   is the same attack surface as a workspace policy file and goes through
///   the same first-use trust flow, keyed by the source's identity and the
///   SHA-256 of its canonical JSON.
///
/// Declining trust (or having no terminal to ask on) discards the grants and
/// keeps the restrictions: tightening is free, loosening needs a yes.
module PolicyConfig =

    /// The parsed `"policy"` object. Every field is optional; absent fields
    /// contribute no layer at all.
    type Policy =
        { /// edit_file/write_file confined to these workspace-relative
          /// prefixes; outside them the call is denied. Restriction.
          editsWithin: string list
          /// Shell command words auto-allowed (via `command-is?`, which
          /// refuses shell metacharacters). Grant.
          shellAllow: string list
          /// Tool names (with `*` suffix wildcards) to auto-allow. Grant.
          allow: string list
          /// Tool names (with `*` suffix wildcards) to deny. Restriction.
          deny: string list
          /// "allow" (grant) | "ask" | "deny" (restrictions). None = silent.
          memory: string option
          /// At most this many distinct files may be edited in a run. Restriction.
          maxFilesEdited: int option
          /// At most this many lines may change in a run, counting a replaced
          /// region as its old lines plus its new ones. Restriction.
          maxLinesChanged: int option
          /// Workspace-relative prefixes no edit may touch, whatever
          /// edits_within allows. Restriction.
          protectedPaths: string list }

    let empty =
        { editsWithin = []; shellAllow = []; allow = []; deny = []; memory = None
          maxFilesEdited = None; maxLinesChanged = None; protectedPaths = [] }

    /// Tool packs: one name in `allow` or `deny` that stands for a family
    /// of built-in tools, so a policy reads "pack:git" rather than five
    /// names, and a tool added to a family later is covered. Packs are
    /// expanded when the policy compiles; the JSON keeps the pack name.
    let packs : Map<string, string list> =
        Map.ofList
            [ "read", [ "read_file"; "read_output"; "list_dir"; "file_tree"; "grep"; "symbols"; "outline"; "read_symbol"; "references" ]
              "edit", [ "edit_file"; "edit_symbol"; "apply_patch"; "write_file" ]
              "verify", [ "run_tests" ]
              "git", [ "git_status"; "git_diff"; "git_log"; "git_blame"; "changed_set" ]
              "session", [ "policy_check"; "session_status" ]
              "memory", [ "memory_read"; "memory_write" ] ]

    let isEmpty (policy: Policy) = policy = empty

    /// Does this policy loosen anything? Only these halves need trust.
    let hasGrants (policy: Policy) =
        not policy.shellAllow.IsEmpty
        || not policy.allow.IsEmpty
        || policy.memory = Some "allow"

    let hasRestrictions (policy: Policy) =
        not policy.editsWithin.IsEmpty
        || not policy.deny.IsEmpty
        || (match policy.memory with Some ("ask" | "deny") -> true | _ -> false)
        || policy.maxFilesEdited.IsSome
        || policy.maxLinesChanged.IsSome
        || not policy.protectedPaths.IsEmpty

    /// The tightening half alone — what survives a declined trust prompt.
    let restrictionsOnly (policy: Policy) =
        { empty with
            editsWithin = policy.editsWithin
            deny = policy.deny
            memory = (match policy.memory with Some ("ask" | "deny" as m) -> Some m | _ -> None)
            maxFilesEdited = policy.maxFilesEdited
            maxLinesChanged = policy.maxLinesChanged
            protectedPaths = policy.protectedPaths }

    /// The blast radius every source agrees on: the smallest limits and every
    /// protected prefix. Restrictions need no trust, so all sources count.
    let effectiveLimits (policies: Policy list) =
        let smallest select =
            policies |> List.choose select |> function [] -> None | values -> Some(List.min values)
        smallest (fun p -> p.maxFilesEdited),
        smallest (fun p -> p.maxLinesChanged),
        policies |> List.collect (fun p -> p.protectedPaths) |> List.distinct |> List.sort

    // ---------------------------------------------------------------------
    // Parsing

    let private stringArray (field: string) (node: JsonNode) : Result<string list, string> =
        match node with
        | :? JsonArray as array ->
            array
            |> Seq.fold
                (fun acc item ->
                    match acc with
                    | Error e -> Error e
                    | Ok values ->
                        match item with
                        | :? JsonValue as v ->
                            match v.TryGetValue<string>() with
                            | true, s when s <> "" -> Ok(s :: values)
                            | true, _ -> Error(sprintf "policy.%s must not contain empty strings" field)
                            | _ -> Error(sprintf "policy.%s must be an array of strings" field)
                        | _ -> Error(sprintf "policy.%s must be an array of strings" field))
                (Ok [])
            |> Result.map List.rev
        | _ -> Error(sprintf "policy.%s must be an array of strings" field)

    /// Parse a `"policy"` object. Unknown keys are an error, not a silent
    /// no-op: a typo in a rule that was meant to restrict must never look
    /// like it applied.
    let parse (node: JsonNode) : Result<Policy, string> =
        match node with
        | :? JsonObject as o ->
            let known = set [ "edits_within"; "shell_allow"; "allow"; "deny"; "memory"; "max_files_edited"; "max_lines_changed"; "protected_paths" ]
            let unknown = o |> Seq.map (fun kv -> kv.Key) |> Seq.filter (known.Contains >> not) |> List.ofSeq
            if not unknown.IsEmpty then
                Error(sprintf "unknown policy key(s): %s (known: %s)"
                          (String.Join(", ", unknown)) (String.Join(", ", known)))
            else
                let field (name: string) =
                    match o.[name] with
                    | null -> Ok []
                    | node -> stringArray name node
                let positive (name: string) =
                    match o.[name] with
                    | null -> Ok None
                    | :? JsonValue as v ->
                        match (try Some(v.GetValue<int>()) with _ -> None) with
                        | Some n when n >= 1 -> Ok(Some n)
                        | _ -> Error(sprintf "policy.%s must be a positive integer" name)
                    | _ -> Error(sprintf "policy.%s must be a positive integer" name)
                let unknownPack (patterns: string list) =
                    patterns |> List.tryFind (fun p -> p.StartsWith "pack:" && not (Map.containsKey (p.Substring 5) packs))
                match field "edits_within", field "shell_allow", field "allow", field "deny", field "protected_paths" with
                | Error e, _, _, _, _ | _, Error e, _, _, _ | _, _, Error e, _, _ | _, _, _, Error e, _ | _, _, _, _, Error e -> Error e
                | _, _, Ok allow, Ok deny, _ when (unknownPack (allow @ deny)).IsSome ->
                    Error(sprintf "unknown tool pack %s (known: %s)" (unknownPack (allow @ deny)).Value
                              (String.Join(", ", packs |> Map.toList |> List.map (fun (k, _) -> "pack:" + k))))
                | Ok editsWithin, Ok shellAllow, Ok allow, Ok deny, Ok protectedPaths ->
                    let limits =
                        match positive "max_files_edited", positive "max_lines_changed" with
                        | Error e, _ | _, Error e -> Error e
                        | Ok files, Ok lines -> Ok(files, lines)
                    let memory =
                        match o.["memory"] with
                        | null -> Ok None
                        | node ->
                            match node with
                            | :? JsonValue as v ->
                                match v.TryGetValue<string>() with
                                | true, ("allow" | "ask" | "deny" as m) -> Ok(Some m)
                                | true, other ->
                                    Error(sprintf "policy.memory must be \"allow\", \"ask\", or \"deny\", got \"%s\"" other)
                                | _ -> Error "policy.memory must be a string"
                            | _ -> Error "policy.memory must be a string"
                    match memory, limits with
                    | Error e, _ | _, Error e -> Error e
                    | Ok memory, Ok(maxFiles, maxLines) ->
                        Ok { editsWithin = editsWithin
                             shellAllow = shellAllow
                             allow = allow
                             deny = deny
                             memory = memory
                             maxFilesEdited = maxFiles
                             maxLinesChanged = maxLines
                             protectedPaths = protectedPaths }
        | _ -> Error "policy must be a JSON object"

    // ---------------------------------------------------------------------
    // Canonical form and identity

    /// The canonical JSON of a policy: UTF-8, object keys sorted ordinally,
    /// arrays order-preserving, empty fields omitted, no insignificant
    /// whitespace. These exact bytes feed the trust hash, the compiled
    /// source's identity, and the trace — so they must not drift.
    // ---------------------------------------------------------------------
    // The "environment" object

    /// The parsed `"environment"` object: what a hosting runner must provide
    /// around the agent, as opposed to `"policy"`, which this runtime enforces
    /// on the agent itself. jern recognises the object so a misspelt key
    /// cannot pass silently, and applies none of it; a host such as Jern
    /// Cloud grants it from a catalog. Every field is optional.
    /// What the baseline asks a host to provide around the agent. The runtime
    /// recognises it and applies none of it. Keys a newer host may know and
    /// this runtime does not are kept, not refused: a host validates its own
    /// declarations, and a session must not fail because the runtime is
    /// older than the baseline.
    type Environment =
        { services: string list
          networkAllow: string list
          unknown: string list }

    let emptyEnvironment = { services = []; networkAllow = []; unknown = [] }

    let environmentIsEmpty (environment: Environment) = environment = emptyEnvironment

    /// Parse an `"environment"` object. Known keys are validated; a key this
    /// runtime does not know is kept under `unknown` and named in the notice,
    /// so a typo is visible without a session failing on a host that is
    /// ahead of the runtime.
    let parseEnvironment (node: JsonNode) : Result<Environment, string> =
        match node with
        | :? JsonObject as o ->
            let known = set [ "services"; "network_allow" ]
            let unknown = o |> Seq.map (fun kv -> kv.Key) |> Seq.filter (known.Contains >> not) |> List.ofSeq
            let services =
                match o.["services"] with
                | null -> Ok []
                | servicesNode ->
                    match stringArray "services" servicesNode with
                    | Error e -> Error(e.Replace("policy.services", "environment.services"))
                    | Ok services ->
                        let declaration = Text.RegularExpressions.Regex("^[a-z][a-z0-9]*:[a-z0-9.]+$")
                        match services |> List.tryFind (declaration.IsMatch >> not) with
                        | Some bad -> Error(sprintf "environment.services entry \"%s\" is not a name:version declaration" bad)
                        | None -> Ok services
            let networkAllow =
                match o.["network_allow"] with
                | null -> Ok []
                | hostsNode ->
                    match stringArray "network_allow" hostsNode with
                    | Error e -> Error(e.Replace("policy.network_allow", "environment.network_allow"))
                    | Ok hosts ->
                        let hostName = Text.RegularExpressions.Regex("^[a-z0-9]([a-z0-9.-]*[a-z0-9])?$")
                        match hosts |> List.tryFind (fun host -> not (hostName.IsMatch host) || not (host.Contains ".")) with
                        | Some bad -> Error(sprintf "environment.network_allow entry \"%s\" is not a lowercase host name" bad)
                        | None -> Ok hosts
            match services, networkAllow with
            | Error e, _ | _, Error e -> Error e
            | Ok services, Ok hosts -> Ok { services = services; networkAllow = hosts; unknown = unknown }
        | _ -> Error "environment must be an object"

    /// One phrase for a notice: what a host would have to provide.
    let describeEnvironment (environment: Environment) =
        [ if not environment.services.IsEmpty then "services " + String.Join(", ", environment.services)
          if not environment.networkAllow.IsEmpty then "network " + String.Join(", ", environment.networkAllow)
          if not environment.unknown.IsEmpty then "keys this runtime does not know: " + String.Join(", ", environment.unknown) ]
        |> fun parts -> String.Join("; ", parts)

    /// True when a host has said it confines and provisions this process,
    /// in which case its environment declarations are its business.
    let hostProvidesEnvironment () =
        String.Equals(Environment.GetEnvironmentVariable "JERN_SANDBOX", "external", StringComparison.OrdinalIgnoreCase)

    let canonicalJson (policy: Policy) : string =
        let escape (s: string) =
            let b = StringBuilder()
            for c in s do
                match c with
                | '"' -> b.Append "\\\"" |> ignore
                | '\\' -> b.Append "\\\\" |> ignore
                | '\n' -> b.Append "\\n" |> ignore
                | '\r' -> b.Append "\\r" |> ignore
                | '\t' -> b.Append "\\t" |> ignore
                | c when c < ' ' -> b.AppendFormat("\\u{0:x4}", int c) |> ignore
                | c -> b.Append c |> ignore
            b.ToString()
        let array (values: string list) =
            "[" + String.Join(",", values |> List.map (fun v -> "\"" + escape v + "\"")) + "]"
        // Keys in ordinal sort order: allow, deny, edits_within, max_files_edited,
        // max_lines_changed, memory, protected_paths, shell_allow.
        let fields =
            [ if not policy.allow.IsEmpty then yield "\"allow\":" + array policy.allow
              if not policy.deny.IsEmpty then yield "\"deny\":" + array policy.deny
              if not policy.editsWithin.IsEmpty then yield "\"edits_within\":" + array policy.editsWithin
              match policy.maxFilesEdited with
              | Some n -> yield sprintf "\"max_files_edited\":%d" n
              | None -> ()
              match policy.maxLinesChanged with
              | Some n -> yield sprintf "\"max_lines_changed\":%d" n
              | None -> ()
              match policy.memory with
              | Some m -> yield "\"memory\":\"" + escape m + "\""
              | None -> ()
              if not policy.protectedPaths.IsEmpty then yield "\"protected_paths\":" + array policy.protectedPaths
              if not policy.shellAllow.IsEmpty then yield "\"shell_allow\":" + array policy.shellAllow ]
        "{" + String.Join(",", fields) + "}"

    /// SHA-256 of the canonical JSON — the policy's identity in the trust
    /// store, in `jern policy` output, and in the trace.
    let digest (policy: Policy) = Trust.contentHash (canonicalJson policy)

    // ---------------------------------------------------------------------
    // Compilation to Kernel source

    let private kernelString (s: string) =
        let b = StringBuilder()
        b.Append '"' |> ignore
        for c in s do
            match c with
            | '"' -> b.Append "\\\"" |> ignore
            | '\\' -> b.Append "\\\\" |> ignore
            | '\n' -> b.Append "\\n" |> ignore
            | c -> b.Append c |> ignore
        b.Append '"' |> ignore
        b.ToString()

    let private kernelList (values: string list) =
        match values with
        | [] -> "(list)"
        | _ -> "(list " + String.Join(" ", values |> List.map kernelString) + ")"

    /// Split tool-name patterns into exact names and `*`-suffix prefixes.
    /// Wildcards are expanded here rather than in Kernel, so the generated
    /// rule stays a plain two-list membership test.
    let private expandPacks (patterns: string list) =
        patterns
        |> List.collect (fun pattern ->
            if pattern.StartsWith "pack:" then
                match Map.tryFind (pattern.Substring 5) packs with
                | Some tools -> tools
                | None -> [ pattern ]
            else [ pattern ])

    let private splitPatterns (patterns: string list) =
        expandPacks patterns
        |> List.fold
            (fun (exacts, prefixes) (pattern: string) ->
                if pattern.EndsWith "*" then exacts, pattern.Substring(0, pattern.Length - 1) :: prefixes
                else pattern :: exacts, prefixes)
            ([], [])
        |> fun (exacts, prefixes) -> List.rev exacts, List.rev prefixes

    /// Compile a policy to the Kernel source that installs its layers.
    /// `label` names the source in denials, `jern policy`, and the trace;
    /// `includeGrants` is false when the source's grants were not trusted.
    /// The output is byte-stable for a given input — it ends up in traces.
    let compile (label: string) (includeGrants: bool) (policy: Policy) : string =
        let lines = ResizeArray<string>()
        let add (line: string) = lines.Add line
        add (sprintf "; policy layers from %s (sha256 %s)" label (digest policy))
        if not policy.editsWithin.IsEmpty then
            // "." and "./" read as "the whole workspace", but as literal
            // prefixes they match nothing — every workspace-relative path
            // starts with a directory name. Normalizing them to the empty
            // prefix makes the rule mean what it looks like it means, rather
            // than silently denying every write.
            let normalized =
                policy.editsWithin
                |> List.map (fun prefix -> if prefix = "." || prefix = "./" then "" else prefix)
            let policy = { policy with editsWithin = normalized }
            let prefixes = String.Join(", ", policy.editsWithin)
            add (sprintf "(add-policy-restriction! %s" (kernelString (label + " edits_within")))
            add  "  (lambda (call)"
            add  "    (if (policy-file-write? call)"
            add (sprintf "        (if (policy-path-within-any? call %s)" (kernelList policy.editsWithin))
            add  "            :allow"
            add (sprintf "            %s)" (kernelString (sprintf "policy: edits are limited to %s (%s edits_within)" prefixes label)))
            add  "        :allow)))"
        if not policy.protectedPaths.IsEmpty then
            let prefixes = String.Join(", ", policy.protectedPaths)
            add (sprintf "(add-policy-restriction! %s" (kernelString (label + " protected_paths")))
            add  "  (lambda (call)"
            add  "    (if (policy-file-write? call)"
            add (sprintf "        (if (policy-path-within-any? call %s)" (kernelList policy.protectedPaths))
            add (sprintf "            %s" (kernelString (sprintf "policy: edits under %s are denied (%s protected_paths)" prefixes label)))
            add  "            :allow)"
            add  "        :allow)))"
        if policy.maxFilesEdited.IsSome || policy.maxLinesChanged.IsSome then
            // The counts live in the host, which sees every edit succeed or
            // fail; the layer asks it whether this write would cross a limit.
            add (sprintf "(add-policy-restriction! %s" (kernelString (label + " blast_radius")))
            add  "  (lambda (call)"
            add  "    (if (policy-file-write? call)"
            add (sprintf "        (jern/host-blast-radius call %d %d %s)"
                     (defaultArg policy.maxFilesEdited 0) (defaultArg policy.maxLinesChanged 0) (kernelString label))
            add  "        :allow)))"
        if not policy.deny.IsEmpty then
            let exacts, prefixes = splitPatterns policy.deny
            add (sprintf "(add-policy-restriction! %s" (kernelString (label + " deny")))
            add  "  (lambda (call)"
            add (sprintf "    (if (policy-name-matches-any? call %s %s)" (kernelList exacts) (kernelList prefixes))
            add (sprintf "        (String.concat (String.concat \"policy: \" (plist-get call :name)) %s)"
                     (kernelString (sprintf " is denied by %s deny" label)))
            add  "        :allow)))"
        if includeGrants && not policy.shellAllow.IsEmpty then
            add (sprintf "(add-policy-grant! %s" (kernelString (label + " shell_allow")))
            add  "  (lambda (call)"
            add (sprintf "    (if (policy-command-is-any? call %s) :allow :none)))" (kernelList policy.shellAllow))
        if includeGrants && not policy.allow.IsEmpty then
            let exacts, prefixes = splitPatterns policy.allow
            add (sprintf "(add-policy-grant! %s" (kernelString (label + " allow")))
            add  "  (lambda (call)"
            add (sprintf "    (if (policy-name-matches-any? call %s %s) :allow :none)))"
                     (kernelList exacts) (kernelList prefixes))
        match policy.memory with
        | Some "allow" when includeGrants ->
            add (sprintf "(add-memory-grant! %s :allow)" (kernelString (label + " memory")))
        | Some ("ask" | "deny" as decision) ->
            let value =
                if decision = "ask" then ":ask"
                else kernelString (sprintf "policy: memory is denied by %s memory" label)
            add (sprintf "(add-memory-restriction! %s %s)" (kernelString (label + " memory")) value)
        | _ -> ()
        String.Join("\n", lines) + "\n"

    /// Human-readable summaries of a policy's two halves, for `jern policy`
    /// and the trust prompt. Restrictions always apply; grants are the part
    /// that needs a yes.
    let describeRestrictions (policy: Policy) : string list =
        [ if not policy.editsWithin.IsEmpty then
            yield "edits_within: " + String.Join(", ", policy.editsWithin)
          if not policy.protectedPaths.IsEmpty then
            yield "protected_paths: " + String.Join(", ", policy.protectedPaths)
          match policy.maxFilesEdited with
          | Some n -> yield sprintf "max_files_edited: %d" n
          | None -> ()
          match policy.maxLinesChanged with
          | Some n -> yield sprintf "max_lines_changed: %d" n
          | None -> ()
          if not policy.deny.IsEmpty then
            yield "deny: " + String.Join(", ", policy.deny)
          match policy.memory with
          | Some ("ask" | "deny" as m) -> yield "memory: " + m
          | _ -> () ]

    let describeGrants (policy: Policy) : string list =
        [ if not policy.shellAllow.IsEmpty then
            yield "shell_allow: " + String.Join(", ", policy.shellAllow)
          if not policy.allow.IsEmpty then
            yield "allow: " + String.Join(", ", policy.allow)
          if policy.memory = Some "allow" then yield "memory: allow" ]

    // ---------------------------------------------------------------------
    // Sources

    /// Where a policy came from. Origin decides whether its *grants* need a
    /// trust answer; restrictions from every origin always apply.
    type Origin =
        /// ~/.config/jern/config.json — the user's own machine config.
        | UserConfig of path: string
        /// <workspace>/jern.json — arrives with the repository, so its
        /// grants are untrusted until the user says otherwise.
        | Workspace of path: string
        /// A protected baseline supplied by a CI workflow from outside the
        /// pull request (base branch or workflow-owned input). Its
        /// restrictions cannot be weakened by anything in the checkout.
        | Baseline of label: string

    type Source =
        { origin: Origin
          policy: Policy }

    let originLabel = function
        | UserConfig _ -> "user config"
        | Workspace _ -> "jern.json"
        | Baseline label -> "protected baseline: " + label

    /// The key this source's grants are trusted under, in the trust store.
    let trustIdentity = function
        | UserConfig path -> path + "#policy"
        | Workspace path -> path + "#policy"
        | Baseline label -> "baseline:" + label + "#policy"
