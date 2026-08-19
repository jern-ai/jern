# Announce — jern v0.8 (Show HN, ready to post)

Submission:

- **Title** (70 chars): `Show HN: Jern – a coding agent with a test suite for its own behavior`
- **URL**: `https://jern.ai`
- **Text** (HN supports url+text on Show HN; plain text, no markdown):

---

I got tired of coding agents whose behavior you can only hope about: the
rules live in prompts, and a prompt is a suggestion the model follows
until it doesn't. Jern (Norwegian for "iron") is a terminal coding agent
built the other way around: its loop, tools, and policies are ~300 lines
of readable source in IronKernel (a Kernel/Scheme dialect for .NET),
shipped beside the binary — and the agent's behavior has an offline,
deterministic regression suite.

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
the run fit a four-model-call budget. Outcome plus how-it-got-there,
both deterministic.

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

Otherwise it's a normal modern agent: Anthropic, OpenAI, Ollama, or any
OpenAI-compatible endpoint; MCP servers as tools (they pass through the
same policy, approval, trace, and test layers as the built-ins);
streaming; sessions with --resume; git auto-commit with /undo; and a
local web UI ("jern ui", served by the binary itself) where approvals
are cards with colored diffs that the agent blocks on until you answer.

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

Repo (Apache-2.0): https://github.com/jern-ai/jern

---

*(Post-notes for the author — predictable objections, pre-answered:
"why not Python?" → why-ironkernel.md; "exact-match replay is brittle" →
that's golden-file testing plus trajectory properties — re-record to
bless a change, and the property assertions survive re-records;
"prompts can do TDD too" → the recorded conversation IS the model trying
to skip TDD and failing. Consider attaching a screenshot of the jern ui
approval card and a screencast of `jern test agents/tdd` catching the
weakened gate.)*
