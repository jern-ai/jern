# Changelog

## 0.5.0 — 2026-08-19

- **Trajectory assertions**: agent test suites can now assert properties
  of the *run*, not just its outcome. `(trajectory)` exposes every traced
  effect of the test's session as data; on top of it the test prelude
  provides `trajectory-events`, `tool-calls`, `tool-calls-named`,
  `llm-call-count`, `assert-max-llm-calls`, `assert-no-tool-call`, and
  `assert-edits-within`. "The agent never shelled out", "every edit stayed
  under src/", "the run fit a four-model-call budget" are now offline,
  deterministic test failures — behavioral contracts alongside the
  byte-exact fixture replay. All three bundled agents' suites use them
  (existing fixtures unchanged: assertions add no LLM traffic).

## 0.4.1 — 2026-08-19

- The TDD agent's recorded fixture embedded bash's "command not found"
  wording, which diverged on dash (Linux CI) — replay caught its own
  fixture being platform-dependent. The recorded failing test now
  silences the shell and prints its own marker, so the fixture replays
  identically everywhere.

## 0.4.0 — 2026-08-19

- **The TDD agent** (`agents/tdd`): a bundled example that enforces
  red→green in its own loop. Implementation edits come back as tool
  errors until a failing test run has been observed; the tests run after
  every edit and move the phase; the model gets no shell tool. The gate is
  ~35 lines of agent source with its own regression suite — including a
  recorded conversation where the model tries to implement first and is
  refused, so weakening the gate fails `jern test agents/tdd` offline.
- New agent-environment bindings: `string-contains?`, `string-prefix?`,
  `string-suffix?` (pure predicates injected by the host; the safe
  profile's generated bindings stop at `String.concat`).

## 0.3.0 — 2026-08-19

- **MCP client support.** Configure servers in `jern.json`
  (`"mcp_servers": { "<name>": { "command": …, "args": […], "env": {…} } }`)
  and their tools join the agent's toolset as `mcp__<server>__<tool>`.
  MCP calls dispatch through the ordinary `jern/tool-call` effect, so the
  policy, approval, git, trace, and fixture layers apply to them unchanged —
  and the default policy asks before every MCP call until your `policy.ikr`
  allows specific ones. New `jern mcp` command connects the configured
  servers and lists their tools. Stdio transport; the client is ~250 lines
  of readable F# on the existing JSON⇄Kernel convention, no new
  dependencies. Verified against the official
  `@modelcontextprotocol/server-filesystem`.

## 0.2.7 — 2026-08-19

- Serialize the release publish build (`-m:1`): the SDK's transitive
  publish walk builds the same project several times with differing
  leaked global properties, and on Windows those concurrent builds race
  on one obj path (CS2012 in win-x64). Confirmed via binlog; the build
  graph is a linear chain so this costs nothing.

## 0.2.6 — 2026-08-19

- 0.2.5 was verified on SDK 10.0.1xx; CI runs 10.0.4xx, whose new
  host-RID `PublishRuntimeIdentifier` default re-broke every publish
  job. IronKernel now also sets `UseDefaultPublishRuntimeIdentifier=false`
  upstream. This release was verified on the CI's exact SDK band.

## 0.2.5 — 2026-08-19

- The 0.2.4 cross-compile workaround raced on Windows (CS2012 in the
  win-x64 build). Root-caused for real: IronKernel now declares
  `IsRidAgnostic=true` upstream, so referencing hosts build it exactly
  once, RID-less, on every platform. The workaround is removed.

## 0.2.4 — 2026-08-19

- Fixed the osx-x64 release build (cross-compiled on arm64 macOS runners):
  the IronKernel Exe reference is now built once, RID-agnostic, instead of
  once per target-plus-host RID (NETSDK1047/NETSDK1152). No behavior change
  in the shipped binaries. (0.2.1–0.2.3 were CI-infrastructure iterations
  on the same problem.)

## 0.2.0 — 2026-08-19

The productization release. The project is now **jern** (Norwegian for
*iron*) — new name, same thesis: the coding agent whose brain is an
inspectable, editable, testable program.

- **Renamed** from iron to jern throughout: the binary, the effect tags
  (`jern/llm-call`, …), the workspace dir (`.jern/`), the config file
  (`jern.json`), and the agent packages (`Jern.Agent.Default`,
  `Jern.Agent.Docs`). IronKernel — the language — keeps its name.
- **Providers**: `--model provider/model` routes natively to Anthropic or to
  any OpenAI-compatible endpoint (OpenAI, Ollama, OpenRouter, DeepSeek, Groq,
  Mistral, xAI, Gemini, LM Studio, custom); aliases and defaults in
  `jern.json`. Fixtures are provider-independent.
- **Streaming** responses with graceful non-streaming fallback; per-command
  token totals.
- **Git safety**: every approved edit auto-committed (task in the message),
  your uncommitted changes saved separately first; `jern undo` / `/undo`
  pops exactly one jern-authored commit.
- **Chat**: persisted sessions with `--resume`, Ctrl-C interrupt, `/model`,
  `/clear`, `/cost`, `/help`, a status line.
- **Agent quality of life**: `CONVENTIONS.md` in the system prompt, a
  `file_tree` first-turn snapshot, prompt caching via `cache_control`, and
  `test_command` — run your tests after every edit — all implemented in
  agent source.
- **`jern test`**: record/replay LLM fixtures with byte-exact divergence
  detection; `deftest`/`with-fixtures`/`setup-file` test forms.
- **Policy & sandboxing**: allow/ask/deny policy in Kernel source, approval
  prompts with diff previews, `sandbox-exec` write-confinement on macOS.

## 0.1.0 — unreleased

Milestones M0–M6 of the implementation plan, under the working name iron.
