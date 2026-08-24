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
          memory: string option }

    let empty =
        { editsWithin = []; shellAllow = []; allow = []; deny = []; memory = None }

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

    /// The tightening half alone — what survives a declined trust prompt.
    let restrictionsOnly (policy: Policy) =
        { empty with
            editsWithin = policy.editsWithin
            deny = policy.deny
            memory = (match policy.memory with Some ("ask" | "deny" as m) -> Some m | _ -> None) }

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
            let known = set [ "edits_within"; "shell_allow"; "allow"; "deny"; "memory" ]
            let unknown = o |> Seq.map (fun kv -> kv.Key) |> Seq.filter (known.Contains >> not) |> List.ofSeq
            if not unknown.IsEmpty then
                Error(sprintf "unknown policy key(s): %s (known: %s)"
                          (String.Join(", ", unknown)) (String.Join(", ", known)))
            else
                let field (name: string) =
                    match o.[name] with
                    | null -> Ok []
                    | node -> stringArray name node
                match field "edits_within", field "shell_allow", field "allow", field "deny" with
                | Error e, _, _, _ | _, Error e, _, _ | _, _, Error e, _ | _, _, _, Error e -> Error e
                | Ok editsWithin, Ok shellAllow, Ok allow, Ok deny ->
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
                    memory
                    |> Result.map (fun memory ->
                        { editsWithin = editsWithin
                          shellAllow = shellAllow
                          allow = allow
                          deny = deny
                          memory = memory })
        | _ -> Error "policy must be a JSON object"

    // ---------------------------------------------------------------------
    // Canonical form and identity

    /// The canonical JSON of a policy: UTF-8, object keys sorted ordinally,
    /// arrays order-preserving, empty fields omitted, no insignificant
    /// whitespace. These exact bytes feed the trust hash, the compiled
    /// source's identity, and the trace — so they must not drift.
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
        // Keys in ordinal sort order: allow, deny, edits_within, memory, shell_allow.
        let fields =
            [ if not policy.allow.IsEmpty then yield "\"allow\":" + array policy.allow
              if not policy.deny.IsEmpty then yield "\"deny\":" + array policy.deny
              if not policy.editsWithin.IsEmpty then yield "\"edits_within\":" + array policy.editsWithin
              match policy.memory with
              | Some m -> yield "\"memory\":\"" + escape m + "\""
              | None -> ()
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
        "(list " + String.Join(" ", values |> List.map kernelString) + ")"

    /// Split tool-name patterns into exact names and `*`-suffix prefixes.
    /// Wildcards are expanded here rather than in Kernel, so the generated
    /// rule stays a plain two-list membership test.
    let private splitPatterns (patterns: string list) =
        patterns
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
            let prefixes = String.Join(", ", policy.editsWithin)
            add (sprintf "(add-policy-restriction! %s" (kernelString (label + " edits_within")))
            add  "  (lambda (call)"
            add  "    (if (policy-file-write? call)"
            add (sprintf "        (if (policy-path-within-any? call %s)" (kernelList policy.editsWithin))
            add  "            :allow"
            add (sprintf "            %s)" (kernelString (sprintf "policy: edits are limited to %s (%s edits_within)" prefixes label)))
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
