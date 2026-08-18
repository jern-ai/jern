# Iron Agent — Implementation Plan (v1)

Companion to `iron-agent-spec.md`. That document describes the destination; this one
describes the first buildable product and the concrete milestones to get there.
Intended to seed a new repository (working name: `iron-agent`, binary: `iron`).

## 1. Reframing the spec

The spec's Phases 2–4 (agent-first IDE, cloud sandbox fleet, marketplace, enterprise
tier) are company-scale. Cursor and Devin each have large teams and nine-figure
funding behind exactly those surfaces. Competing on their turf first means losing on
polish before the differentiator is ever visible.

The differentiator does not need an IDE to be visible. It is:

> **The coding agent whose brain is an inspectable, editable, testable program** —
> the agent loop, tools, and policies are IronKernel (https://ironkernel.org/) source you can open, change,
> unit-test with a mocked LLM, and package.

Every serious coding agent today (Claude Code, aider, OpenHands, Devin) has an
opaque loop configured by prompts, hooks, and config files. None lets you *test an
agent deterministically* or *swap the loop itself*. IronKernel already has the
exact machinery this needs — tagged deep effect handlers (`prompt`/`perform`/
`resume`), first-class environments with capability profiles, combiner contracts,
`.ikproj` packaging on NuGet. That machinery exists nowhere else in the agent space.

**The wedge product:** a terminal coding agent in the aider / Claude Code class,
where 100% of the agent logic is IronKernel source shipped alongside the binary.
`iron run "fix the failing test"` works out of the box; `iron --agent ./my-agent`
swaps the brain; `iron test` runs the agent against recorded LLM fixtures.

If that resonates with power users, the spec's later phases have something real to
grow from. If it doesn't, no IDE fork would have saved it.

### Honesty corrections carried into this plan

1. **"Capability-secure by construction" overclaims.** `docs/capabilities.md` is
   explicit that capability environments are not a CPU/memory/process sandbox, and
   the moment an agent holds a `shell` tool, language-level capabilities confine
   nothing that process does. The honest claim — still strong — is:
   *policy is programmable, enforced in-runtime for in-runtime authority, and
   auditable end-to-end; process-level tools are confined by OS sandboxing plus
   approval gates.* Market it that way from day one.
2. **"Match Cursor's polish / match Devin's autonomy" is not a goal.** Delete those
   rows. The v1 comparison target is aider-class utility plus the programmability
   story no one else has.
3. **Contract shapes are too coarse for tool schemas.** `number`/`string`/`list`
   cannot express the JSON Schema that LLM function-calling needs (object fields,
   enums, descriptions). Tools need their own schema form (§5.3); contracts remain
   the purity/effect layer.
4. **One-shot resumptions are sufficient for v1** — an agent turn is inherently
   sequential — but streaming output, cancellation, and parallel subagents are
   host-side work, planned as such rather than assumed from the effect system.

## 2. What exists vs. what must be built

| Needed by an agent runtime | Status in IronKernel today |
|---|---|
| Effect handlers for the control plane | ✅ tagged `prompt`/`perform`/`resume`, deep, one-shot |
| Least-privilege environments | ✅ `minimal`/`safe`/`unrestricted` profiles, intersection semantics |
| Tool metadata | ◐ contracts (purity/effects) exist; JSON-schema-grade shapes do not |
| Packaging & distribution | ✅ `.ikproj`, NuGet, `ik pack`, lockfiles |
| LLM provider bridge | ❌ new host code (F#) |
| JSON ⇄ Kernel data | ❌ new host code |
| Tools (fs, grep, shell, git) | ❌ new host code + Kernel wrappers |
| OS sandboxing for process tools | ❌ new; v1 = approval gates + cwd scoping |
| Streaming / cancellation | ❌ host-side; `runAsync` resumes serially today |
| Session persistence | ❌ new |

The new repo consumes `IronKernel.Runtime` as a NuGet package (already published).
No fork, no submodule. Gaps that turn out to belong in the language (e.g. richer
async, a dictionary/record type if s-expr plists prove painful for message data)
go upstream as normal IronKernel PRs — the agent repo is also the language's first
demanding customer, which is healthy pressure.

## 3. Product definition (v1)

`iron` — a terminal coding agent.

- `iron run "task"` — one-shot agentic task in the current workspace.
- `iron` — interactive REPL-style chat session with the agent.
- `iron --agent <path>` — run with a different agent package (the headline feature).
- `iron test [<agent>]` — run an agent's test suite: contracts + recorded-fixture
  transcripts with the `llm-call` effect handled by a mock.
- `iron trace <session>` — replay the structured effect/tool/capability log.
- Default agent ships as readable IronKernel source; `iron eject` copies it into
  the workspace for editing.

Non-goals for v1: IDE/extension UI, cloud execution, registry beyond NuGet,
multi-agent coordination, computer use, Windows sandbox depth (runs, but sandbox
honesty tier is macOS/Linux first).

## 4. Architecture

```
┌─────────────────────────────── iron (F# host binary) ───────────────────────────────┐
│  CLI / TUI          Session store        Trace log (JSONL)                          │
│  LLM bridge (Anthropic first; Microsoft.Extensions.AI abstraction behind it)        │
│  Host tools: fs, grep, shell(+sandbox/approval), git, http                          │
│  JSON ⇄ LispVal converter        Tool-schema → JSON Schema emitter                  │
└───────────────▲──────────────────────────────────────────────▲──────────────────────┘
                │ host primitives injected into a root env      │ effects performed
┌───────────────┴──────────────── IronKernel runtime ───────────┴──────────────────────┐
│  Handler stack (installed by host, in Kernel):                                       │
│    trace-handler → policy-handler → approval-handler → provider-handler              │
│  Agent package (.ikproj, runs in a restricted environment):                          │
│    agent loop (operative) · tool defs · prompts · policies · tests                   │
└──────────────────────────────────────────────────────────────────────────────────────┘
```

Key inversion: the **agent performs effects; it holds no authority.** The agent
environment is built from the `minimal`/`safe` profile — it cannot touch the CLR,
the filesystem, or the network directly. Everything reaches the world via
`(perform iron/llm-call …)`, `(perform iron/tool-call …)`, `(perform iron/ask-user …)`,
handled by a stack the *host* installs. Handlers can log, rewrite, deny, or require
approval, and the whole stack is itself Kernel source the user can read and replace.

This is the load-bearing design decision. It makes the security story honest
(authority lives in reviewed host tools + handler policy, not in agent code), makes
tracing free (one choke point), and makes `iron test` trivial (swap the provider
handler for a fixture handler).

### 4.1 Effect taxonomy (initial)

| Effect | Payload | Default handler |
|---|---|---|
| `iron/llm-call` | model, messages, tools, params | provider bridge |
| `iron/tool-call` | tool name + args (validated against schema) | dispatch to host tool |
| `iron/ask-user` | question, options | TUI prompt |
| `iron/approve` | description of a pending side effect | TUI y/n (policy may auto-answer) |
| `iron/log` | structured event | trace sink |
| `iron/spawn` | sub-agent spec *(post-v1)* | child env + own handler stack |

### 4.2 Data representation

Messages, tool schemas, and tool results cross the host boundary as Kernel data
(keyword-tagged association structures), converted to/from JSON in F#. Decide the
exact convention in M1 and freeze it early — everything else depends on it. If
plists prove miserable, that is the trigger for an upstream persistent-map type.

## 5. Milestones

Ordered so every milestone ends with something runnable and demoable.

### M0 — Skeleton
New repo. F# solution: `Iron.Cli`, `Iron.Host` (bridge + tools), `Iron.Tests`.
`agents/default/` as an `.ikproj`. Depends on `IronKernel.Runtime` NuGet. CI mirrors
IronKernel's (test on Ubuntu, release binaries per RID). `iron --version` runs.
**Exit:** `iron repl` gives an IronKernel prompt inside a restricted env with one
injected host primitive, proving the injection path.

### M1 — LLM bridge + data convention
Anthropic Messages API from F# (direct SDK; keep Microsoft.Extensions.AI as the
abstraction seam, not the first dependency). JSON ⇄ `LispVal`. `iron/llm-call`
effect wired through a provider handler. Non-streaming first; stream-to-console as
host-side polish once the loop works.
**Exit:** a 10-line `.ikr` script performs `iron/llm-call` and prints the reply.

### M2 — Tool system
`define-tool` operative in the agent prelude: name, description, parameter schema
(new schema form, JSON-Schema-expressible), a contract, and a body that performs
`iron/tool-call`. Host tools: `read_file`, `list_dir`, `grep`, `edit_file`
(string-replace based), `shell`. Schema emitter feeds the LLM tools parameter;
argument validation happens before dispatch.
**Exit:** from the REPL, an LLM round-trip that calls `read_file` via
function-calling and answers a question about a real file.

### M3 — Agent loop in Kernel
The ReAct/function-calling loop as ~100–200 lines of readable IronKernel in
`agents/default`: build context → `llm-call` → dispatch tool calls → append
results → repeat until done. Handler stack installed in order: trace (JSONL log of
every effect) → policy → approval → provider. `iron run "task"` works end to end.
**Exit:** the demo — fix a real bug in a real repo, then open `agents/default`,
change the loop's behavior (e.g. add a "run the tests after every edit" step),
and rerun. No recompile.

### M4 — Policy, approval, sandbox honesty
Policy handler in Kernel: path scoping for fs tools (workspace-relative only),
write approval, shell always-approve default. Shell hardening on macOS
(`sandbox-exec`) and Linux (`bubblewrap` or landlock) as available, degrading to
approval-only with an explicit warning. Capability audit: the trace records which
handler authorized every side effect.
**Exit:** a written security model doc making the honest claim from §1, and a
red-team afternoon against it.

### M5 — `iron test` (the flagship)
Fixture handler for `iron/llm-call`: record mode captures real transcripts;
replay mode substitutes them deterministically. Test form in Kernel:
`(deftest "adds a null check" (with-fixtures "fix-null.json" (assert-edits …)))`.
Contract checks over the agent's tools. This is the capability no competitor has —
give it disproportionate polish and make it the README's first example.
**Exit:** the default agent has a passing test suite; a deliberate prompt
regression is caught by `iron test`.

### M6 — Sessions, UX, packaging
Session persistence and `--resume` (serialize message history + trace; environments
stay ephemeral). Diff preview before writes. `iron eject` / `--agent`. Package the
default agent with `ik pack`; a second example agent (e.g. a docs-only agent with a
narrower toolset) published to NuGet proves the distribution story.
**Exit:** public v0.1 announce: repo, binaries, a screencast of M3's demo + M5's test.

### Post-v1 (only if v0.1 finds users)
Subagents via `iron/spawn` (child env, intersected authority — the language makes
this unusually clean). MCP client support to inherit the existing tool ecosystem.
Streaming/cancellation deepening. Then, and only then, revisit the spec's Phase 2+.

## 6. Design decisions to lock in M0/M1

1. **Data convention** for messages/schemas across the boundary (§4.2). Freeze early.
2. **Effect namespace** — `iron/…` tags created by the host, unforgeable, handed to
   the agent env as bindings.
3. **Provider strategy** — Anthropic direct first; the handler seam *is* the
   abstraction, so adding providers later is a new handler, not a refactor.
4. **What runs restricted** — agent + tools-as-wrappers in `safe`; only the host
   installs authority. Never hand the agent env an unrestricted environment value
   (capabilities.md's delegation warning is exactly this trap).
5. **Trace format** — JSONL, one event per effect, stable schema from day one; it
   is both the audit log and the test-fixture source.

## 7. Risks

| Risk | Assessment / mitigation |
|---|---|
| Kernel is niche; nobody edits agent source | Defaults must be excellent so editing is optional. The testability story sells even to people who never edit the loop. |
| Security claim gets publicly punctured | Ship the honest model (§1.1) before anyone else writes it for us. Shell = OS sandbox + approval, stated plainly. |
| One-shot / serial async blocks parallel subagents | True but post-v1; v1 loop is sequential by nature. Budget upstream runtime work when `iron/spawn` lands. |
| Perf of interpreted loop | Irrelevant — LLM latency dominates by 3–4 orders of magnitude. |
| Scope creep back toward the IDE | This document is the scope. The spec stays in `ideas/` as the long-range map. |
| Solo-maintainer bus factor across two repos | Keeping the agent repo thin (host bridge + Kernel source) and pushing general machinery upstream keeps both codebases small. |

## 8. What success looks like

v0.1 is successful if a stranger, in one sitting: installs a binary, runs
`iron run` on their repo and gets a competent fix, opens `agents/default`,
understands the loop, changes one behavior, and watches `iron test` catch a
regression they introduce. That single loop — *use, read, edit, test* — is the
entire thesis, and nothing else in the coding-agent market can do it.
