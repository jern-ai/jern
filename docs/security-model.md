# iron — security model

The honest claim (implementation plan §1):

> **Policy is programmable, enforced in-runtime for in-runtime authority, and
> auditable end-to-end; process-level tools are confined by OS sandboxing plus
> approval gates.**

That sentence is deliberately narrower than "capability-secure by
construction". This document says exactly what each half means, and what it
does not mean.

## In-runtime authority: capability environments

Agent code — the loop in `agents/default`, anything run by `iron script`, every
REPL input — evaluates in an IronKernel environment built from the **safe**
capability profile. Inside the runtime it cannot:

- touch raw CLR interop (`.`, `new`, `clr-type` are absent, and the values
  behind them check the invoking environment — copying one in doesn't help);
- open files or ports (host I/O primitives are absent);
- load source (`load` is absent);
- reach the handler environment, where the host primitives with real
  authority (`iron/host-llm-call`, `iron/host-tool-call`, `iron/host-trace`,
  `iron/host-approve`) are bound.

Everything the agent does to the world goes through `perform` on effect tags
the host created (`iron/llm-call`, `iron/tool-call`, `iron/approve`,
`iron/log`). Tags are unforgeable values; the only handlers are the ones the
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
[handlers.ikr](../src/Iron.Host/kernel/handlers.ikr) and
[policy.ikr](../src/Iron.Host/kernel/policy.ikr) — ordinary Kernel source the
user can read and replace:

```
log → approval → provider → tool-executor → policy → agent code
```

- The **policy handler** sees every `iron/tool-call` first and decides
  `:allow`, `:ask`, or deny. The default policy: reads (`read_file`,
  `list_dir`, `grep`) are free; writes (`edit_file`), `shell`, and anything
  unknown ask first.
- `:ask` performs `iron/approve`; the **approval handler** delegates to the
  host approver — a TTY `[y/N]` prompt for `iron run`, everything-approved for
  `--yes`, `iron script`, and the REPL (where the user is the one acting).
- Denials come back to the agent as error tool-results, not crashes.

## Audit: the trace is the security log

Every effect is recorded as JSONL at the same choke point that enforces
policy: `llm-call`/`llm-response`, `tool-call`/`tool-result`,
`policy-decision` (with the decision), `approval-denied`, and agent `log`
events, each timestamped. `iron run` writes it to `.iron/trace-*.jsonl`. A
side effect that bypassed policy would be a side effect with no
`policy-decision` line — the trace makes the claim checkable.

## Process-level tools: OS sandboxing plus approval

The moment a `shell` command runs, language-level capabilities confine
nothing that process does. iron's honest posture:

- **Path scoping (host-enforced):** `read_file`, `list_dir`, `grep`,
  `edit_file` resolve paths workspace-relative and refuse escapes
  (`../…` and absolute paths outside the root).
- **macOS:** shell commands run under `sandbox-exec` with a deny-by-default
  write profile — writes only inside the workspace, temp, and `/dev`. Reads
  and network are **not** restricted in v1; do not run iron in a workspace
  sitting next to secrets you wouldn't paste into a prompt.
- **Linux:** no OS sandbox is wired up yet (bubblewrap/landlock is planned);
  iron warns once and relies on the approval gate alone.
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
