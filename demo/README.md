# The iron demo

`./demo/demo.sh` — the four-act demo of the thesis: **the coding agent whose
brain is an inspectable, editable, testable program.** Paced for screencasting
(enter between steps); `DEMO_FAST=1` skips the pauses. Acts 2–4 run without an
API key.

## Why this beats a feature tour

Claude Code, aider, OpenHands, Devin — all of them have an opaque loop you
steer with prompts, hooks, and config files. None of them lets you *open the
loop*, and none of them can answer the question "did my change to the agent
break it?" with a test. The demo doesn't argue this; it shows the four things
in sequence, and lets the last two land as "wait, the others can't do that."

## The acts, with talking points

**Setup.** A five-line shell project with a bug and a failing test. Small on
purpose: the project is not the star.

**Act 1 — use it** *(needs `ANTHROPIC_API_KEY`)*. `iron run` fixes the failing
test like any competent agent. Talking points: approvals show the exact edit
as a diff before it lands; shell runs write-confined under `sandbox-exec`; and
`.iron/trace-*.jsonl` has one event per effect — llm calls, tool calls, and
**every policy decision**. That trace is also the raw material for fixtures.

**Act 2 — read it.** `iron eject` drops the agent into the workspace.
`wc -l` says ~120 lines. Scroll the loop on screen. Talking point: this isn't
a prompt file or a settings schema — it's the control flow. The system prompt,
the tool dispatch, the turn limit, the termination condition: all right there.

**Act 3 — test it** *(no key)*. `iron test agents/default` — three tests, green,
in under a second, offline. Talking point: the LLM is replayed from recorded
fixtures; replay fails on *any* divergence from the recording, so the agent's
behavior is pinned the way a unit test pins code. Say the sentence out loud:
**"this is a unit-tested LLM agent; name another one."**

**Act 4 — edit it, and get caught** *(no key; blessing the change needs one)*.
Apply a real behavior change — after every file edit, the loop immediately
runs the project's tests and shows the model the result. Ten lines of Kernel,
no recompile. Rerun `iron test`: **it fails**, pointing at exactly where the
conversation diverged from the recording. That's the payoff moment: you just
watched an agent's CI catch a change to the agent itself. With a key,
`iron test --record` blesses the change, and rerunning `iron run --agent`
shows the new behavior live (`→ shell (tests after edit)` between turns).

**Close.** The four-line summary the script prints — use / read / edit / test —
is the pitch. Nothing else in the market completes that loop.

## Honesty notes (if asked)

- The security claim is deliberately modest: language-level capabilities
  confine in-runtime authority; process tools are OS-sandboxed (macOS today)
  plus approval-gated. See `docs/security-model.md`.
- Replay is exact-match by design. A prompt tweak fails tests until you
  re-record — that's the point, same as updating a golden file.
- `demo/main-autotest.ikr` is generated from the shipped agent source; if
  `main.ikr` changes shape, regenerate it (the generator asserts on drift).
