# jern — surfacing governance (M21–M23 roadmap)

Companion to [implementation-plan.md](implementation-plan.md) (M0–M6, done)
and [roadmap-v0.2.md](roadmap-v0.2.md) (M7–M20, done through v0.12). This
document plans the repositioning phase, decided 2026-08-24.

## 1. The reframe this roadmap serves

Every headline so far describes what the architecture **is** ("the agent
whose brain is editable, testable Kernel source"). That sells to the ~1% who
want to edit an agent loop. The capabilities underneath — enforced policy,
hard budgets, byte-exact traces, deterministic replay, behavioral tests —
sell to a much larger audience, but only when surfaced as what they **do**:

> **The agent you can trust unattended.** Branch-protection-style rules the
> repo enforces, a flight recorder for every run, hard cost caps, and a CI
> gate that catches behavior changes — no Lisp required for any of it.

Target users, in order: teams adopting agents under rules; anyone burned by
an agent incident; people building their own agents on a governed substrate.
Individual daily-driver users choosing on polish are **not** the target —
that fight stays lost on purpose (roadmap-v0.2 §1 still applies).

Cloud is where these capabilities stop being optional — an unattended agent
*requires* pre-declared policy, budgets, and an audit trail. But cloud infra
is the Phase-2+ trap the implementation plan deliberately deferred. The
wedge is **CI-as-cloud** (M23): headless jern in GitHub Actions is already
"unattended agents on someone else's compute," and it validates demand
before any infrastructure is built.

Out of scope for this phase: real cloud infra (fleets, sandboxes, a hosted
service), IDE surfaces, and any weakening of the Kernel escape hatch — the
config layer compiles *to* the same choke point; it never bypasses it.

## 2. M21 — Policy from config (v0.13): "branch protection for agents"

**Goal:** the large majority get enforced policy from `jern.json`, never
seeing a paren. The mental model is CODEOWNERS/branch protection, not
"editable policy source."

**Surface** (`jern.json`):

```json
"policy": {
  "edits_within":  ["src/", "tests/"],
  "shell_allow":   ["pytest", "npm test"],
  "allow":         ["mcp__github__get_issue"],
  "deny":          ["mcp__*"],
  "memory":        "ask"
}
```

- `edits_within` — `edit_file`/`write_file` outside the prefixes are
  **denied** with a reason the model sees; inside them the normal rules
  still apply. A pure restriction.
- `shell_allow` — commands auto-allowed via the existing `command-is?`
  (which already refuses shell metacharacters); everything else still asks.
- `allow` / `deny` — per-tool decisions, with `*` suffix wildcards
  (`mcp__*`); `deny` wins over `allow`.
- `memory` — compiles to `memory-policy` (`allow` | `ask` | `deny`).

**Design:**

- A `PolicyConfig` module compiles the JSON object to Kernel source (string)
  and the session evaluates it into the handler environment **after** the
  built-in `policy.ikr` and **before** `.jern/policy.ikr` — code beats
  config; if both exist, warn that the workspace policy file wins wherever
  it rebinds.
- **Trust split — the load-bearing decision.** Restrictions (`edits_within`,
  `deny`, `memory: ask|deny`) only tighten and load without prompting.
  Grants (`shell_allow`, `allow`, `memory: allow`) can loosen approvals, so
  a cloned repo's `jern.json` is the same attack surface as its
  `policy.ikr`: grants go through the existing first-use `Trust` flow,
  keyed on the canonical JSON of the policy object (reuse `Trust.fs`
  verbatim). Tightening is free; loosening needs a yes.
- `jern policy` (no args) prints the **effective** policy: built-in +
  config-compiled + workspace file, with provenance per rule.
- The compiler is ordinary generated Kernel source, loggable with
  `jern policy --show-compiled` — the escape hatch stays honest: copy the
  compiled output into `.jern/policy.ikr` and edit from there.

**Cut from M21:** an interactive wizard; UI policy editor changes beyond
displaying the effective policy. Both can follow demand.

**Tests:** deny-outside-`edits_within` (model sees the reason);
`shell_allow` + metacharacter refusal; wildcard `deny` beats `allow`;
grants prompt for trust and restrictions don't; precedence vs. a workspace
`policy.ikr`; compiled source is byte-stable (it will end up in traces).

**Exit:** a repo with only the JSON above refuses an out-of-tree edit and
auto-runs `pytest`, in a fresh clone, after one trust prompt — demoed
without opening a single `.ikr` file.

## 3. M22 — The run receipt (v0.14): the flight recorder, visible

**Goal:** every run ends with evidence the user *sees*, not an audit trail
they must discover in `.jern/`. This is also the artifact M23 posts to PRs.

**Surface:** after `jern run` (and on `/cost`-style demand in chat and the
UI), a styled block:

```
receipt · run 20260824-101530 · 2m 14s · exit ok
  model calls   4 (claude-opus-5) · 18.2k in / 2.1k out · budget 4/20
  tools         read_file ×3 · grep ×2 · edit_file ×2 · shell ×1
  files touched src/parser.py · tests/test_parser.py   (2 jern commits, /undo-able)
  policy        7 allowed · 2 approved by you · 1 denied (edit outside src/)
  spawns        1 (docs)  ·  programs: 1 kernel_eval
  trace         .jern/trace-20260824-101530.jsonl
```

**Design:**

- **A receipt is a pure function of the trace.** A `Receipt` module parses
  a trace JSONL (reusing the event vocabulary Replay already reads) into a
  typed summary; no new state threads through the loop. That keeps it
  re-derivable forever: `jern receipt` (latest trace) or
  `jern receipt <trace.jsonl>`, with `--md` emitting Markdown for PR
  comments and `--json` for tooling.
- Files touched come from `edit_file`/`write_file` results plus `git-commit`
  events; policy tallies from `policy-decision`/`approval-denied`; token and
  budget figures from `llm-response` usage and `budget-*` events; spawn and
  kernel_eval counts from their events. Everything needed is already traced.
- Terminal rendering uses the existing `Style` palette; the UI shows the
  same receipt at end-of-session (small SSE addition, stretch if tight).

**Tests:** golden receipt over a canned trace (text, `--md`, `--json`);
tallies for denied/approved; a trace with spawns and programs; empty run.

**Exit:** every `jern run` ends with the block above, and
`jern receipt --md` over any historical trace produces a PR-ready summary.

## 4. M23 — Golden tasks + the GitHub Action (v0.15): CI-as-cloud

**Goal:** the flagship capabilities become visible where teams already live.
A PR that changes a prompt, a policy, or agent source and thereby changes
agent *behavior* fails CI with the exact divergence — and every CI run
posts its receipt.

**Half A — golden sessions** (in the binary):

- `jern golden record "task"` — run the task live once; store the trace
  under `.jern/golden/<slug>.jsonl` (it's a normal trace; committed).
- `jern golden check [--filter <slug>]` — replay every golden trace with
  the existing Replay machinery against the *current* repo (current agent
  source, current policy, config-compiled M21 policy included): offline,
  deterministic, no API key. Any divergence → nonzero exit + the
  recorded-vs-actual report. `jern golden list` for inventory.
- This is `jern test` for people who will never write a `deftest`: record
  once, protected forever. Re-record to bless deliberate changes, exactly
  like fixtures.

**Half B — `jern-action`** (new repo `jern-ai/jern-action`, composite
action):

- Installs a **pinned** jern release via the existing `install.sh` +
  `SHA256SUMS` verification (version input, no floating latest).
- Runs `jern test` (agent suites) and `jern golden check`; uploads all
  traces as workflow artifacts; posts/updates a single PR comment with the
  M22 `--md` receipt(s) and per-golden verdicts.
- **Live runs are phase 2 of the action and ship later, deliberately**: a
  `task` input that does `jern run --auto` with an API-key secret and
  commits to a branch is the true "unattended agent in CI," but the
  security story (untrusted-PR triggers + secrets + auto-approval) must be
  designed, not defaulted. Gate to `workflow_dispatch`/same-repo branches
  only, M21 config policy required, and document the threat model in
  `security-model.md` before it ships. Check and receipts alone are enough
  for the M23 exit.

**Tests:** golden record/check round-trip in a temp repo; check catches a
prompt edit (reuse the tamper pattern from `TestRunnerTests`); action
smoke-tested in this repo's own CI (dogfood: jern's repo runs jern-action).

**Exit:** a public demo repo where editing the agent's system prompt turns
the PR red with the divergence excerpt in a comment, plus the receipt of
the golden runs — screencast-able in under a minute, zero Lisp on screen.

## 5. Sequencing and sizes

| Milestone | Ships | Depends on | Rough size |
|---|---|---|---|
| M21 policy-from-config | v0.13 | Trust.fs (exists) | ~3–4 sessions |
| M22 run receipt | v0.14 | trace vocabulary (exists) | ~2 sessions |
| M23 golden + action | v0.15 | Replay (exists), M22 for comments, M21 for CI policy | ~3–4 sessions + new repo |

Order is fixed by dependencies: receipts feed the action's PR comments, and
CI runs want config policy rather than interactive approval. Each milestone
is independently shippable and independently demoable; none blocks the
normal parity/bugfix stream.

## 6. What success looks like

A team lead who has never heard of Kernel: adds a `policy` object to
`jern.json`, sees an out-of-scope edit denied with a reason; every run ends
with a receipt they can paste into a standup; their PRs fail when someone's
prompt tweak changes agent behavior, with the divergence quoted. If that
lands, the follow-on conversation — hosted unattended runs with the same
receipts and rules — is the cloud decision, made with evidence.
