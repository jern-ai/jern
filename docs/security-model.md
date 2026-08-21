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
log → approval → provider → budget → tool-executor → git → policy → agent code
```

- The **policy handler** sees every `jern/tool-call` first and decides
  `:allow`, `:ask`, or deny. The default policy: reads (`read_file`,
  `list_dir`, `grep`) are free; writes (`edit_file`), `shell`, MCP tools,
  and anything unknown ask first.
- `:ask` performs `jern/approve`; the **approval handler** delegates to the
  host approver — a TTY `[y/N]` prompt for `jern run`, everything-approved for
  `--yes`, `jern script`, and the REPL (where the user is the one acting).
- The **budget handler** sees every `jern/llm-call` first; a configured
  model-call or token budget is a hard cap — exhaustion becomes an approval
  question, not a suggestion the model may ignore.
- Denials come back to the agent as error tool-results, not crashes.

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

Every effect is recorded as JSONL at the same choke point that enforces
policy: `llm-call`/`llm-response`, `tool-call`/`tool-result`,
`policy-decision` (with the decision), `approval-denied`, and agent `log`
events, each timestamped. `jern run` writes it to `.jern/trace-*.jsonl`. A
side effect that bypassed policy would be a side effect with no
`policy-decision` line — the trace makes the claim checkable.

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
- **Linux:** no OS sandbox is wired up yet (bubblewrap/landlock is planned);
  jern warns once and relies on the approval gate alone.
- **Windows:** shell commands run via `cmd.exe /c` with **no OS sandbox**,
  same as Linux — jern warns once and the approval gate is the only
  confinement for what an approved command does.
- **Approval is the last gate everywhere**: by default every shell command is
  shown to the user before it runs.

## What this model does not defend against

- A malicious *host* binary or a modified handler stack — the handlers are
  the trusted computing base, on purpose (they're also ~100 lines of Kernel
  you can read).
- Prompt injection convincing the model to request harmful tool calls that
  the user then approves. The policy handler is the place to add rules;
  approval prompts show the exact command for this reason.
- Exfiltration via approved shell commands or via the LLM request itself
  (workspace file contents go to the provider by design).
- Resource exhaustion by agent code (no CPU/memory limits in-runtime).
