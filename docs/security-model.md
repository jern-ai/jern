# jern — security model

The honest claim (implementation plan §1):

> **Policy is programmable, enforced in-runtime for in-runtime authority, and
> auditable end-to-end; process-level tools are confined by OS sandboxing plus
> approval gates.**

That sentence is deliberately narrower than "capability-secure by
construction". This document says exactly what each half means, and what it
does not mean.

## In-runtime authority: capability environments

Agent code — the loop in `agents/default`, anything run by `jern script`, every
REPL input — evaluates in an IronKernel environment built from the **safe**
capability profile. Inside the runtime it cannot:

- touch raw CLR interop (`.`, `new`, `clr-type` are absent, and the values
  behind them check the invoking environment — copying one in doesn't help);
- open files or ports (host I/O primitives are absent);
- load source (`load` is absent);
- reach the handler environment, where the host primitives with real
  authority (`jern/host-llm-call`, `jern/host-tool-call`, `jern/host-trace`,
  `jern/host-approve`) are bound.

Everything the agent does to the world goes through `perform` on effect tags
the host created (`jern/llm-call`, `jern/tool-call`, `jern/approve`,
`jern/log`). Tags are unforgeable values; the only handlers are the ones the
host installed. This is enforced by the IronKernel runtime itself
(see IronKernel's `docs/capabilities.md`), not by convention.

**Limits.** Capability environments are not a CPU, memory, or termination
sandbox: agent code can spin or allocate. Run untrusted agent packages the way
you'd run untrusted code. And the boundary discipline lives in the host: the
handler environment deliberately holds the agent environment (that direction
is safe); the reverse — handing the agent environment any privileged
environment or closure — would delegate authority, which is exactly the trap
IronKernel's documentation warns embedders about.

## The handler stack: programmable policy, one choke point

Every effect crosses the handler stack installed by
[handlers.ikr](../src/Jern.Host/kernel/handlers.ikr) and
[policy.ikr](../src/Jern.Host/kernel/policy.ikr) — ordinary Kernel source the
user can read and replace:

```
log → approval → memory → spawn → provider → budget → tool-executor → git → policy → agent code
```

(Outermost first. A `jern/tool-call` performed by agent code meets the policy
handler first and, if allowed, re-performs outward through git into the
executor; `jern/approve` questions raised by policy, budget, or memory travel
outward to the approval handler, which therefore sits outside them all. A
`kernel_eval` program and a `jern/spawn` child each get a _fresh copy_ of
this whole stack, so their effects are governed exactly like the parent's.)

- The **policy handler** sees every `jern/tool-call` first and decides
  `:allow`, `:ask`, or deny. The default policy: reads (`read_file`,
  `list_dir`, `grep`) are free; writes (`edit_file`), `shell`, MCP tools,
  and anything unknown ask first.
- `:ask` performs `jern/approve`; the **approval handler** delegates to the
  host approver — a TTY `[y/N]` prompt for `jern run`, everything-approved for
  `--yes`, `jern script`, and the REPL (where the user is the one acting).
- The **budget handler** sees every `jern/llm-call` first. A configured local
  model-call or token budget is renewable through approval. A cloud-authorized
  run also has a separate host-enforced token cap at the provider boundary;
  approval cannot renew it and spawned agents share the same counter.
- Denials come back to the agent as error tool-results, not crashes.

### Policy is composed, not overwritten

The rules in force are the composition of several layers, and the direction
each layer may push is fixed:

```
  restrictions   tighten only          jern.json + a protected baseline
  base           tool-policy           built-in, or .jern/policy.ikr
  grants         relax the base only   trusted config
```

A decision's severity — deny beats `:ask` beats `:allow` — is what makes the
guarantee: **no layer loaded later can turn a restriction's denial into an
approval.** Not a trusted grant, not a hand-written `.jern/policy.ikr` that
rebinds `tool-policy` to `:allow` wholesale. Every `policy-decision` in the
trace records the layer that decided (`:by`), and each layer announces its
identity and SHA-256 digest as a `policy-layer` trace event when the session
is built. `jern policy` prints the effective composition with provenance;
`jern policy --show-compiled` prints the Kernel source configuration
compiles to.

### Policy from configuration, and its trust split

A `"policy"` object in `jern.json` gives a repository enforced rules without
any Kernel:

```json
"policy": {
  "edits_within": ["src/", "tests/"],
  "shell_allow":  ["pytest"],
  "deny":         ["mcp__*"],
  "memory":       "ask"
}
```

The two halves are trusted differently, because they carry different risk:

- **Restrictions** (`edits_within`, `deny`, `memory: ask|deny`) only tighten,
  so they load on sight, with no prompt. A cloned repository can lock its
  agents down without asking anyone's permission.
- **Grants** (`shell_allow`, `allow`, `memory: allow`) can loosen approvals —
  the same power a workspace policy file has — so a repository-supplied grant
  is confirmed once, exactly like `.jern/policy.ikr`. Trust is keyed by the
  source's identity plus the SHA-256 of its _canonical_ JSON (UTF-8, keys
  sorted ordinally, arrays order-preserving, no insignificant whitespace), so
  a reordered or reformatted file is the same policy and an edited one asks
  again. Declining — or having no terminal — drops the grants and keeps the
  restrictions.

Malformed policy is a startup error, never a silent no-op: a typo in a rule
meant to restrict must not look like it applied.

### Protected baselines, for unattended and CI runs

A policy checked out _from_ a pull request cannot govern that pull request:
the same diff can loosen `jern.json`, replace `.jern/policy.ikr`, or bless a
changed recording. `--policy-baseline <file>` supplies rules from outside the
checkout — the base branch, or data the workflow owns — that the checkout may
tighten but never weaken. Its restrictions outrank everything in the tree,
and its identity and digest appear in `jern policy` and in the trace.

Headless runs never prompt. Where a workflow does want a policy's grants, it
pins them with `--policy-trust <sha256>`; without a pin the grants are
dropped, the restrictions stay, and jern prints the digest that would allow
them. A workflow that sources its purported baseline from the head checkout
has no protection at all — that is the Action's responsibility to prevent,
and it is documented as such.

### Live Action pilot

The Action's optional live mode is deliberately narrower than its offline
checks. A maintainer supplies exactly one `live-task` or `live-issue`; issue
content is read only after manual dispatch and any environment approval. It
accepts live work only for `workflow_dispatch` on the repository's default
branch, requires a protected `baseline-path`, requires mandatory Jern Cloud
upload, and requires jern 0.14.5 or newer. Pull requests, scheduled runs,
arbitrary branches, best-effort cloud mode, and runs without GitHub OIDC are
rejected before a cloud reservation or provider call.

The Action does not pass `--auto`. A headless task may perform only operations
the effective policy allows without an approval prompt; grants from the
protected baseline apply only when their digest is pinned with `policy-trust`.
Branch protection and review remain repository responsibilities: the Action
can verify that the ref is the default branch, not that GitHub's protection
settings are adequate.

Provider credentials are ordinary runner environment secrets and are never
Action inputs or cloud request fields. The runner requests a token reservation
with GitHub OIDC, then gives `jern run` the server-returned cap. It uploads the
trace and receipt even when the task fails before propagating that failure to
the job. With `live-delivery: "pull-request"`, a successful changed run pushes
Jern's existing per-file commits to a unique `jern/run-*` branch and opens a
pull request against the default branch. Failed runs never publish code and
successful no-change runs open no pull request. The workflow token therefore
needs `contents: write` and `pull-requests: write`, but the model receives no
GitHub API tool. Checkout credentials are not persisted, and `GH_TOKEN` plus
GitHub's OIDC request credentials are removed from the Jern process environment.
The Action configures Git authentication only after a successful run. It never
pushes or merges the default branch; branch protection, CODEOWNERS, and required
review remain repository controls.

### Workspace policy loads on first-use trust

A repo can override the rules with its own `.jern/policy.ikr`
(`jern policy init`). That file is evaluated **in the privileged handler
environment**: it is the workspace governing its own agents, and it can
loosen rules as well as tighten them — which means cloning a repository must
not be enough to run its policy. The first time a session sees a workspace
policy (and again whenever its content changes), jern shows the file on the
terminal and asks before loading it. A yes is remembered in
`~/.config/jern/trusted.json` (0600, like credentials.json; the directory
honors `JERN_CONFIG_DIR`), keyed by the file's absolute path and the SHA-256
of its content — and the session evaluates exactly the content that was
approved. Declining, or having no terminal to ask on, skips the file with a
warning and the built-in policy stands; the session still runs.

Policies the user authors through jern are trusted directly: `jern policy
init` trusts the template it writes, and saving the policy in `jern ui`'s
brain editor trusts the saved content (`jern ui` asks any first-use question
on the terminal before the server starts). Every session that loads a
workspace policy still prints `using workspace policy …`, so its presence is
never silent.

## Audit: the trace is the security log

Every run opens with a `run-started` event carrying the trace's schema
version and what the run was configured with — model, agent, budget, and the
identity and digest of every policy layer in force — and closes with exactly
one `run-finished` carrying its status and duration. Between them, every
effect is recorded as JSONL at the same choke point that enforces policy: `llm-call`/`llm-response`, `tool-call`/`tool-result`,
`policy-decision` (with the decision and the layer that made it),
`policy-layer` (each layer's identity, digest, and whether its grants were
trusted), `approval-denied`, `memory-recall`/`memory-remember`,
`spawn`/`spawn-result`, and agent `log` events, each timestamped. `jern run` writes it to `.jern/trace-*.jsonl`. A
side effect that bypassed policy would be a side effect with no
`policy-decision` line — the trace makes the claim checkable. `jern receipt`
summarizes any trace back into what the run did; because it is a pure
function of the record, a receipt cannot claim more than the trace shows,
and a truncated or pre-envelope trace is reported as partial rather than
completed.

## Process-level tools: OS sandboxing plus approval

The moment a `shell` command runs, language-level capabilities confine
nothing that process does. jern's honest posture:

- **Path scoping (host-enforced):** `read_file`, `list_dir`, `grep`,
  `edit_file`, `write_file` resolve paths workspace-relative and refuse
  escapes (`../…`, absolute paths outside the root, **and symlinks**: the
  target and every parent directory are resolved to their real paths before
  the containment check, so a link inside the workspace pointing outside —
  pre-existing or created by an approved shell command — cannot smuggle a
  read or write past the root).
- **macOS:** shell commands run under `sandbox-exec` with a deny-by-default
  write profile — writes only inside the workspace, temp, and `/dev`. Reads
  and network are **not** restricted in v1; do not run jern in a workspace
  sitting next to secrets you wouldn't paste into a prompt.
- **Linux:** shell commands run under `bubblewrap` when a working `bwrap` is
  found — the filesystem mounts read-only with the workspace and `/tmp` bound
  back writable, matching the macOS posture (reads and network are **not**
  restricted). bwrap needs user namespaces, which some kernels and containers
  deny, so jern probes once with a no-op; where bwrap is missing or unusable
  it warns once and relies on the approval gate alone.
- **A host that already confines jern** (Jern Cloud's managed runner, which
  puts the whole process in its own mount and network namespaces and denies
  nested namespaces) sets `JERN_SANDBOX=external`. jern then runs shell
  commands directly without the warning and records `"sandbox": "external"`
  in the run envelope, so a receipt says which boundary held. Set it only
  when something outside jern really does confine the process.
- **Windows:** shell commands run via `cmd.exe /c` with **no OS sandbox**,
  same as Linux — jern warns once and the approval gate is the only
  confinement for what an approved command does.
- **Approval is the last gate everywhere**: by default every shell command is
  shown to the user before it runs.

## What this model does not defend against

- A malicious _host_ binary or a modified handler stack — the handlers are
  the trusted computing base, on purpose (they're also ~100 lines of Kernel
  you can read).
- Prompt injection convincing the model to request harmful tool calls that
  the user then approves. The policy handler is the place to add rules;
  approval prompts show the exact command for this reason.
- Exfiltration via approved shell commands or via the LLM request itself
  (workspace file contents go to the provider by design).
- Sensitive prompts or tool results appearing in the trace. Jern Cloud
  encrypts retained traces, but the runner still sends the trace to the cloud;
  use a customer-controlled runner and policy appropriate for that data.
- Resource exhaustion by agent code (no CPU/memory limits in-runtime).
