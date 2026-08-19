# Announce — jern v0.2 (Show HN, posted 2026-08-19)

Submission:

- **Title** (67 chars): `Show HN: Jern – a coding agent whose loop is a unit-tested program`
- **URL**: `https://jern.ai`
- **Text** (HN supports url+text on Show HN; plain text, no markdown):

---

Every serious coding agent today — Claude Code, aider, OpenHands — has an
opaque loop you steer from the outside with prompts, hooks, and config
files. Jern (Norwegian for "iron") inverts that: the agent's loop, tools,
and policies are ~300 lines of readable source in IronKernel (a
Kernel/Scheme dialect for .NET), shipped beside the binary.

Three things fall out of that, and the third is the one nothing else has:

1. Read and edit the brain. "jern eject" drops the agent into your
workspace; the system prompt, the tool dispatch, the allow/ask/deny policy,
and the git auto-commit layer are all just source. Change the loop, rerun —
no fork, no rebuild.

2. Edit it without trusting it. Agent code runs in a capability environment
with no file, network, or process authority — its only way out is
performing effects on unforgeable tags handled by a stack you can also read
(trace → approval → provider → git → policy). Editability without
inheriting the host's authority is the part Python or JS can't do
in-process; it's why the language exists in the stack at all.

3. The agent has unit tests. "jern test" replays recorded LLM traffic
deterministically — offline, no API key — and fails on any divergence from
the recording. Change the system prompt, reorder a message, add a tool: the
suite catches it and points at the exact difference. We use it on jern's
own default agent; it has caught every refactor we've made.

Otherwise it's a normal modern agent: Anthropic/OpenAI/Ollama/anything
OpenAI-compatible, streaming, sessions with --resume, git auto-commits with
/undo, approval gates with diff previews, conventions files, a JSONL audit
trace of every effect.

The demo (demo/demo.sh — acts 2–4 need no API key) is the whole thesis in
four commands: use it, read it, edit it, and watch "jern test" catch the
edit.

Repo: https://github.com/jern-ai/jern (Apache-2.0)

Why a niche language instead of Python:
https://github.com/jern-ai/jern/blob/main/docs/why-ironkernel.md

The honest security model:
https://github.com/jern-ai/jern/blob/main/docs/security-model.md

---

*(Predictable objections, pre-answered in the linked docs: "why not
Python?" → why-ironkernel.md; "exact-match replay is brittle" → that's
golden-file testing, re-record to bless changes.)*
