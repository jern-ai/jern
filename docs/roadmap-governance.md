# jern — surfacing governance (M21–M23 roadmap)

Companion to [implementation-plan.md](implementation-plan.md) (M0–M6, done),
[roadmap-v0.2.md](roadmap-v0.2.md) (M7–M10, done), and the
[README](../README.md)/[CHANGELOG](../CHANGELOG.md) milestone record
(M11–M20, done through v0.12). This document plans the repositioning phase,
decided 2026-08-24 and revised after an architecture and positioning review
the same day.

## 1. The reframe this roadmap serves

Every headline so far describes what the architecture **is** ("the agent
whose brain is editable, testable Kernel source"). That sells to the ~1% who
want to edit an agent loop. The capabilities underneath — enforced policy,
hard budgets, byte-exact traces, deterministic replay, behavioral tests —
sell to a much larger audience, but only when surfaced as what they **do**:

> **The agent you can govern unattended.** Enforced rules, a flight recorder
> for every run, hard cost caps, and a CI gate that catches behavior changes
> — no Lisp required for any of it.

Target users, in order: teams willing to run jern under repository and
organization rules; anyone burned by an agent incident; people building
their own agents on a governed substrate. This is a hypothesis, not an
already-proven expansion of the market: jern governs jern agents today, not
arbitrary Claude Code, Cursor, or aider sessions. M23 must test whether teams
will adopt that runtime in exchange for the guarantees. Individual
daily-driver users choosing on polish are **not** the primary target — that
fight stays lost on purpose (roadmap-v0.2 §1 still applies), although basic
parity remains maintained.

Cloud is where these capabilities stop being optional — an unattended agent
*requires* pre-declared policy, budgets, and an audit trail. But cloud infra
is the Phase-2+ trap the implementation plan deliberately deferred.
M23's check-only Action validates **behavioral governance in CI**: deterministic
replay, protected policy, and visible receipts on someone else's compute.
It does not by itself validate demand for hosted unattended execution,
because no new task runs live. A later, tightly constrained live Action
pilot is the actual **CI-as-cloud** test and is required before a hosted
cloud decision.

Out of scope for this phase: real cloud infra (fleets, sandboxes, a hosted
service), new IDE investment beyond keeping the existing `jern ui` surface
coherent, and any weakening of the Kernel escape hatch — the config layer
compiles *to* the same choke point; it never bypasses it.

### Claims graduate with the product

- **M21–M22:** "enforced rules and a receipt for every run."
- **M23 check-only:** "agent behavior protected in CI."
- **Live Action pilot:** "governed unattended execution."

Do not advance the stronger claim before the corresponding capability has
run outside a contrived demo.

## 2. M21 — Policy from config (v0.13): "branch protection for agents"

**Goal:** the large majority get enforced policy from `jern.json`, never
seeing a paren. Locally, the mental model is repository policy rather than
"editable policy source." In CI, the branch-protection analogy is earned
only when mandatory policy comes from a source the checked-out PR cannot
modify.

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

- A `PolicyConfig` module parses the JSON into a typed policy, emits
  byte-stable Kernel source for inspection/tracing, and installs it after
  the built-in `policy.ikr`. It does **not** rely on one later
  `tool-policy` redefinition erasing earlier restrictions.
- Policy layers have explicit provenance and composition. Mandatory
  restriction layers combine by severity (`deny` > `ask` > `allow`);
  a later layer cannot turn their denial into an approval. The built-in
  defaults remain the fallback. Trusted grants may relax that fallback,
  but never a mandatory restriction.
- `.jern/policy.ikr` remains the arbitrary-code escape hatch. Locally it is
  a separately trusted user override, but config restrictions still wrap it
  and cannot be rebound away accidentally. Provide an explicit
  user-controlled mode if fully replacing local policy is still needed for
  compatibility; never make that the CI default.
- **Trust split — the load-bearing local decision.** Restrictions
  (`edits_within`, `deny`, `memory: ask|deny`) only tighten and load without
  prompting. Grants (`shell_allow`, `allow`, `memory: allow`) can loosen
  approvals, so a cloned repo's `jern.json` is the same attack surface as
  its `policy.ikr`: grants go through a generalized `Trust` flow, keyed by
  policy identity plus SHA-256 of canonical JSON. Tightening is free;
  loosening needs a yes. If trust is declined or unavailable, restrictions
  still load and only grants are discarded.
- Canonical JSON is defined, not incidental: UTF-8, object keys sorted
  ordinally, arrays order-preserving, normalized JSON numbers, no
  insignificant whitespace. The same bytes feed the trust hash, compiled
  source identity, and trace metadata.
- `jern policy` (no args) prints the **effective** policy: built-in +
  protected baseline (when present) + config-compiled + workspace file,
  with provenance and trust status per rule.
- The compiler is ordinary generated Kernel source, loggable with
  `jern policy --show-compiled` — the escape hatch stays honest: copy the
  compiled output into `.jern/policy.ikr` and edit from there.

### Protected CI baseline

A policy checked out from a pull request cannot govern that pull request by
itself: the same diff can loosen `jern.json`, replace `.jern/policy.ikr`, or
bless changed golden traces. The Action therefore has a distinct
**enforced baseline**, obtained from base-branch/workflow-owned configuration
or supplied inline by the protected workflow. The PR checkout may add
restrictions but may not weaken this baseline.

- The baseline identity and digest are printed by `jern policy` and written
  into every trace.
- The Action must not silently read the enforced baseline from the head
  checkout. For pull requests it uses the base SHA or workflow-owned input.
- Headless CI never performs an interactive trust prompt. Grants require a
  workflow-pinned digest/trust manifest owned outside the PR; otherwise they
  are ignored while restrictions remain active.
- Golden-file changes are reported distinctly and documentation recommends
  CODEOWNERS/review protection for `.jern/golden/`. Updating a recording is
  a reviewable request to bless behavior, not proof that behavior is safe.

**Cut from M21:** an interactive wizard; UI policy editor changes beyond
displaying the effective policy. Both can follow demand.

**Tests:** deny-outside-`edits_within` (model sees the reason);
`shell_allow` + metacharacter refusal; wildcard `deny` beats `allow`;
grants prompt for trust and restrictions don't; declining a mixed policy
keeps its restrictions; workspace code cannot erase a config restriction;
protected baseline cannot be weakened by head-branch config or policy;
headless trust accepts only a pinned digest; canonical JSON and compiled
source are byte-stable.

**Exit:** a repo with only the JSON above refuses an out-of-tree edit and
auto-runs `pytest`, in a fresh clone, after one trust prompt — demoed
without opening a single `.ikr` file. In a PR fixture, changing both the
code and its checked-in policy still cannot escape a protected denial from
the base branch.

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

- First make the trace a versioned run record. Add `schema_version` and a
  `run-started` event containing run id, jern version, command/task, model,
  configured budget, agent identity, and effective-policy layer digests.
  Add exactly one `run-finished` event containing success/error/interrupted/
  budget-denied status and duration. A truncated trace remains valid but is
  visibly marked incomplete.
- **A receipt is a pure function of that trace.** A `Receipt` module parses
  trace JSONL (reusing the event vocabulary Replay already reads) into a
  typed summary; no receipt-only state threads through the loop. That keeps
  it re-derivable forever: `jern receipt` (latest trace) or
  `jern receipt <trace.jsonl>`, with `--md` emitting Markdown for PR
  comments and `--json` for tooling.
- Files touched come from `edit_file`/`write_file` results plus `git-commit`
  events; policy tallies from `policy-decision`/`approval-denied`; token and
  spend from `llm-response` usage and `budget-*` events; configured limits,
  policy identity, model, and exit status come from the run envelope.
  Historical pre-envelope traces produce an explicitly partial receipt
  rather than invented values.
- Terminal rendering uses the existing `Style` palette; the UI shows the
  same receipt at end-of-session through one SSE event.

**Tests:** golden receipt over a canned trace (text, `--md`, `--json`);
tallies for denied/approved; a trace with spawns and programs; empty,
truncated, interrupted, failed, and older-schema runs; unknown future events
are ignored while an unknown major schema version fails with guidance.

**Exit:** every `jern run` ends with the block above, and
`jern receipt --md` over any historical trace produces a PR-ready summary.

## 4. M23 — Golden tasks + the GitHub Action (v0.15): behavioral CI

**Goal:** the flagship capabilities become visible where teams already live.
A PR that changes a prompt, a policy, or agent source and thereby changes
agent *behavior* fails CI with the exact divergence — and every CI run
posts its receipt.

**Half A — golden sessions** (in the binary):

- `jern golden record "task"` — run the task live once; store the trace
  under `.jern/golden/<slug>.jsonl` (it's a normal trace; committed).
- `jern golden check [--filter <slug>]` — replay every golden trace with
  the existing Replay machinery using current agent source and current
  effective policy, while model and tool results come from the recording:
  offline, deterministic, no API key and no claim that current workspace
  file contents were re-executed. Any divergence → nonzero exit + the
  recorded-vs-actual report. `jern golden list` for inventory.
- Keep the boundaries explicit:
  - `jern replay` is ad-hoc forensics over any historical run;
  - `jern golden check` is a committed end-to-end effect-sequence snapshot;
  - `jern test` is an authored agent suite with semantic trajectory
    assertions.
  Golden sessions are the no-Lisp entry point, not a replacement for
  semantic contracts. Add declarative golden assertions for common
  invariants (`edits_within`, forbidden tools, max files/model calls/tokens)
  in sidecar metadata so non-Kernel users can protect meaning as well as
  bytes.
- Re-recording blesses a deliberate snapshot change, exactly like fixtures.
  CI calls out added/changed recordings separately so reviewers can protect
  them with CODEOWNERS rather than treating self-updated goldens as
  independent approval.

**Half B — `jern-action`** (new repo `jern-ai/jern-action`, composite
action):

- Installs a **pinned** jern release via the existing `install.sh` +
  `SHA256SUMS` verification (version input, no floating latest).
- Runs `jern test` (agent suites) and `jern golden check`; uploads all
  traces as workflow artifacts; posts/updates a single PR comment with the
  M22 `--md` receipt(s), per-golden verdicts, policy provenance/digests, and
  any golden-recording changes. On fork PRs where the token cannot comment,
  the same output goes to the job summary; commenting requires a separate,
  carefully documented `workflow_run` pattern rather than
  `pull_request_target` checking out untrusted code with write authority.
- The workflow supplies the M21 protected baseline from base-branch or
  workflow-owned data. The Action refuses a configuration that sources its
  purported baseline only from the head checkout.
- **Live runs shipped after check-only M23**: exactly one task or issue is
  accepted after `workflow_dispatch` on the default branch, under a protected
  baseline, pinned grants, mandatory Cloud evidence, and the Cloud-returned
  hard token cap. Successful work can preserve Jern's commits on an isolated
  `jern/run-*` branch and open a pull request; failed work uploads evidence but
  publishes no code. The model has no GitHub API tool and the Action never
  pushes or merges the default branch. See `security-model.md` and
  `live-action.md` for the remaining repository controls.

**Tests:** golden record/check round-trip in a temp repo; check catches a
prompt edit (reuse the tamper pattern from `TestRunnerTests`); declarative
assertions survive an intentional re-record; changed golden files are
reported; base-branch policy defeats a head-branch weakening; fork-PR
permissions degrade to job-summary output; action smoke-tested in this
repo's own CI (dogfood: jern's repo runs jern-action).

**Exit:** a public demo repo where editing the agent's system prompt turns
the PR red with the divergence excerpt, where changing head-branch policy
cannot bypass a protected base-branch denial, and where each golden run has
a receipt — screencast-able in under a minute, zero Lisp on screen.

## 5. Sequencing and sizes

| Milestone | Ships | Depends on | Rough size |
|---|---|---|---|
| M21 policy-from-config + protected baseline | v0.13 | Trust.fs, policy handler (exist) | ~4–5 sessions |
| M22 versioned run envelope + receipt | v0.14 | trace vocabulary (exists) | ~2–3 sessions |
| M23 golden + check-only Action | v0.15 | Replay (exists), M22 for comments, M21 for CI policy | ~4–5 sessions + new repo |

Order is fixed by dependencies: receipts feed the action's PR comments, and
CI needs protected, headless policy rather than interactive approval. Each
milestone is independently shippable and independently demoable; none
blocks the normal parity/bugfix stream. The live Action pilot follows M23 and
precedes any hosted-cloud commitment; issue-to-PR delivery is its first
complete customer outcome.

## 6. Messaging migration

The repositioning is not complete while public surfaces still lead with
"the brain is editable Kernel source."

- At M21, update the README, homepage, quickstart, and docs navigation to
  lead with enforced repository policy. Keep IronKernel immediately behind
  the claim as the reason enforcement is credible and as the advanced
  escape hatch.
- At M22, make the receipt the default screenshot/output artifact.
- At M23, replace the editable-brain-first screencast with the protected
  policy + PR divergence demo. Keep the TDD agent and editable source as
  technical proof, not the opening demand-generation message.
- Update `security-model.md` for policy composition, protected CI baselines,
  headless trust, Action permissions, and the eventual live-run threat
  model. Correct its handler-order diagram against the implementation.
- Add a checked-in `jern.json` example and a short "golden vs test vs
  replay" guide.

## 7. What success looks like

A team lead who has never heard of Kernel: adds a `policy` object to
`jern.json`, sees an out-of-scope edit denied with a reason; every run ends
with a receipt they can paste into a standup; their PRs fail when someone's
prompt tweak changes agent behavior, with the divergence quoted; and the PR
cannot weaken the baseline policy that judges it.

The scenario is necessary but not sufficient. Before investing in hosted
cloud infrastructure, validate the repositioning manually against these
decision gates:

1. Five external design partners attempt M21–M23 onboarding; at least four
   configure policy and a golden task without opening `.ikr`.
2. At least three external repositories keep `jern-action` enabled for
   30 days rather than installing it only for a demo.
3. At least three non-contrived divergences or policy denials are judged
   useful by those teams; track false-positive/blessing friction as well.
4. At least one team completes the constrained live Action pilot under its
   own protected baseline and wants to repeat it.
5. Interviews identify a repeated job worth paying to host — not merely
   enthusiasm for the architecture.

No invasive telemetry is required for this phase; design-partner interviews,
opt-in check-ins, and public Action usage are enough. Check-only M23 evidence
can justify continuing the governance product. The live pilot and repeated
customer demand — not replay alone — make the cloud decision.
