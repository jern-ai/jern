# Announce draft — jern v0.2 (DRAFT, for the human to edit and post)

Suggested title (HN / Lobsters style):

> **Show HN: Jern — a coding agent whose loop is a unit-tested program, not a prompt**

---

Every serious coding agent today — Claude Code, aider, OpenHands, Devin — has
an opaque loop you steer from the outside with prompts, hooks, and config
files. Jern (Norwegian for *iron*) inverts that: the agent's loop, tools, and
policies are ~300 lines of readable source in
[IronKernel](https://ironkernel.org/) (a Kernel/Scheme dialect for .NET),
shipped beside the binary.

Three things fall out of that, and the third is the one nothing else has:

1. **Read and edit the brain.** `jern eject` drops the agent into your
   workspace; the system prompt, the tool dispatch, the allow/ask/deny
   policy, and the git auto-commit layer are all just source. Change the
   loop, rerun — no fork, no rebuild.

2. **Edit it without trusting it.** Agent code runs in a capability
   environment with no file, network, or process authority — its only way
   out is `perform` on unforgeable effect tags handled by a stack you can
   also read (trace → approval → provider → git → policy). Editability
   without inheriting the host's authority is the part Python or JS can't do
   in-process; it's why the language exists in the stack at all.

3. **The agent has unit tests.** `jern test` replays recorded LLM traffic
   deterministically — offline, no API key — and fails on *any* divergence
   from the recording. Change the system prompt, reorder a message, add a
   tool: the suite catches it and points at the exact difference. We use it
   on jern's own default agent; it has caught every refactor we've made.

Otherwise it's a normal modern agent: Anthropic/OpenAI/Ollama/anything
OpenAI-compatible, streaming, sessions with `--resume`, git auto-commits
with `/undo`, approval gates with diff previews, conventions files, a JSONL
audit trace of every effect.

The demo (`demo/demo.sh`, acts 2–4 need no API key) is the whole thesis in
four commands: use it, read it, edit it, watch `jern test` catch the edit.

- Site: https://jern.ai
- Repo: https://github.com/ademar/IronAgent  (Apache-2.0)
- Why a niche language instead of Python: docs/why-ironkernel.md
- The honest security model: docs/security-model.md

*(Post-notes for the author: attach the four-act screencast; the top
predictable comments are "why not Python?" — answer in why-ironkernel.md —
and "exact-match replay is brittle" — answer: that's golden-file testing,
re-record to bless changes.)*
