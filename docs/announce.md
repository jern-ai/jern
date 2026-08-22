# Announce — jern v0.11 (Show HN, ready to post)

Submission:

- **Title** (70 chars): `Show HN: Jern – a coding agent with a test suite for its own behavior`
- **URL**: `https://jern.ai`
- **Text** (HN supports url+text on Show HN; plain text, no markdown):

---

I got tired of coding agents whose behavior you can only hope about: the
rules live in prompts, and a prompt is a suggestion the model follows
until it doesn't. Jern (Norwegian for "iron") is a terminal coding agent
built the other way around: its loop, tools, and policies are a few
hundred lines of readable source in IronKernel (a Kernel/Scheme dialect
for .NET), shipped beside the binary — and the agent's behavior has an
offline, deterministic regression suite.

The party trick that shows what that buys:

Jern ships a TDD agent that refuses to write implementation code until a
failing test exists. The rule is ~35 lines of the agent's own loop —
edit_file on a non-test path comes back as a tool error until a failing
test run has been observed — so the premature edit never touches the
filesystem, however persuasively the model argues. And the workflow
itself is tested: "jern test agents/tdd" replays a recorded conversation
in which the model tries to implement first and gets refused. Weaken the
gate and the suite fails — offline, no API key, pointing at the exact
divergence. Our CI has a test that deletes the gate and watches the
suite catch it.

Everything in jern works like this:

1. Test the agent like software. "jern test" replays recorded LLM
traffic byte-exactly, and suites can also assert properties of the whole
trajectory: the agent never shelled out, every edit stayed under src/,
the run fit a four-model-call (or 50k-token) budget, no more than two
files changed, or any cross-turn invariant you can write as a predicate
over the trace. Outcome plus how-it-got-there, both deterministic.

2. Policy is enforcement, not etiquette. "jern policy init" writes a
workspace policy — the repo governs its agents: scope edits to src/,
allowlist shell to your test runner, deny categories with a reason the
model sees. Agent code runs in a capability environment with no file,
network, or process authority; unforgeable effect tags are the only way
out, so the policy layer can't be imported around and the JSONL audit
trace is complete by construction.

3. Budgets are hard caps. "--budget 20" means the 21st model call
becomes a question to you, not a surprise on your bill. Enforced in ~40
lines of handler source you can read and change.

4. The trace is a time machine. Every run writes a byte-exact JSONL
effect log, and "jern replay trace.jsonl" re-runs the whole session
offline — the agent source executes for real, but model AND tool
effects answer from the recording, so nothing touches the network or
your files. The fork is the point: add "--policy strict.ikr" (or an
edited agent) and jern shows you the first effect where the run you
already paid for would have gone differently under the new rules.

5. New capabilities aren't new subsystems. Subagents:
(spawn-agent "task") forks a child session and the same policy, budget,
approval, and trace stack composes recursively onto it — children can't
escape rules their parent runs under, and their effects land in the
same log tagged with a spawn id. Memory: (remember k v) / (recall k)
persist across sessions through a host store, but as effects — traced,
and a workspace policy can make remembering ask first, or deny it.

Otherwise it's a normal modern agent: Anthropic, OpenAI, Ollama, or any
OpenAI-compatible endpoint; MCP servers as tools (they pass through the
same policy, approval, trace, and test layers as the built-ins);
grep plus a definition-aware symbols search; streaming; sessions with
--resume; git auto-commit with /undo; shell write-confined by the OS
sandbox (sandbox-exec on macOS, bubblewrap on Linux); and a local web
UI ("jern ui", served by the binary itself) where approvals are cards
with colored diffs that the agent blocks on until you answer.

Why a niche Lisp instead of Python: editable was never the hard part —
aider is editable Python. Editable without inheriting the host's
authority, in-process, is what Kernel-style first-class environments
give us; that's the only reason a language is in the stack at all.
Longer answer:
https://github.com/jern-ai/jern/blob/main/docs/why-ironkernel.md

The honest security model (what it does and doesn't defend against):
https://github.com/jern-ai/jern/blob/main/docs/security-model.md

The TDD agent, gate and tests included:
https://github.com/jern-ai/jern/blob/main/agents/tdd/README.md

A 30-second screencast of exactly this (tamper with the prompt, watch
replay catch it): https://jern.ai/screencast.webm

Repo (Apache-2.0): https://github.com/jern-ai/jern

---

*(Post-notes for the author — predictable objections, pre-answered:
"why not Python?" → why-ironkernel.md; "exact-match replay is brittle" →
that's golden-file testing plus trajectory properties — re-record to
bless a change, and the property assertions survive re-records;
"prompts can do TDD too" → the recorded conversation IS the model trying
to skip TDD and failing; "isn't jern replay just the fixtures again?" →
fixtures are recordings you authored for tests, replay forks any
production trace from any past run — no test written, and it's the
trace's byte-exactness that makes the fork trustworthy; "replay caveats?"
→ it auto-approves and covers `jern run` traces (not chat yet), so a
session with a denied approval diverges at the denial — which is the
honest answer, not a bug; "subagent fork bombs?" → spawn depth is capped
host-side at 2. Consider attaching a screenshot of the jern ui approval
card and a screencast of `jern test agents/tdd` catching the weakened
gate.)*
