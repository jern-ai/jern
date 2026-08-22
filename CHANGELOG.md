# Changelog

## Unreleased

- **Persistent memory as effects.** `(remember "key" "value")` and
  `(recall "key")` in agent source perform the new `jern/remember` /
  `jern/recall` effects, answered by a host-backed store at
  `.jern/memory.json` that survives across sessions. Because they are
  effects, every access crosses the same choke point as tool calls: it
  lands in the JSONL trace (`memory-remember` / `memory-recall` events)
  and is decided by the new `memory-policy` hook in policy.ikr —
  `:allow` by default, and a workspace policy can rebind it to `:ask`
  (an approval question) or deny with a reason.

- **Richer trajectory vocabulary.** Agent tests can now assert token
  budgets (`(assert-tokens-within n)`, `(total-tokens-used)` — summed
  from each recorded response's `:usage`), blast radius
  (`(assert-max-files-edited n)`, `(edited-files)` — distinct paths
  touched by `edit_file`/`write_file`), and arbitrary cross-turn
  invariants (`(assert-trajectory pred message)` — a predicate that must
  hold for every traced event, failing with the first violating event).
  All offline and deterministic, like the rest of `jern test`.

- **First-use trust for workspace policies.** `.jern/policy.ikr` runs in
  the privileged handler environment, so `git clone && jern` in a repo you
  did not author is no longer enough to execute its policy: the first time
  a session sees one (and again whenever its content changes), jern shows
  the file on the terminal and asks before loading it. Yes answers persist
  in `~/.config/jern/trusted.json` (0600, `JERN_CONFIG_DIR` overrides the
  directory), keyed by absolute path + SHA-256 of the content, and the
  session evaluates exactly the content that was approved. Declining — or
  having no terminal to ask on — falls back to the built-in policy with a
  warning; the session still runs. `jern policy init` and saves from the
  UI's brain editor trust the file the user just authored; `jern ui` asks
  any first-use question on the terminal before the server starts.
  Embedders decide via the new `Session.Config.policyTrust` hook.

## 0.10.0 — 2026-08-19

- **`--auto`** on any command auto-approves everything the policy would
  ask about — explicit policy denials still deny (they never reach an
  approver). Interactive prompts now take `y`/`n`/`a`, where `a` approves
  and stops asking about that tool for the session; the UI's approval
  cards grow an "always" button and a live auto toggle in the header.
- **Reasoning models.** `--think <tokens>` (or jern.json
  `"thinking_tokens"`) enables Anthropic extended thinking with that
  budget — thinking blocks are preserved across turns and `max_tokens`
  grows automatically. `--effort low|medium|high` (or
  `"reasoning_effort"`) drives OpenAI-style reasoning models: the effort
  passes through, the token cap moves to `max_completion_tokens`, and
  DeepSeek-style `reasoning_content` comes back as a canonical thinking
  block (streaming included). The request wiring is ~15 lines of the
  default agent's own source.
- **jern.ai/docs** — the full CLI reference (commands, flags,
  configuration, providers, reasoning, approvals, policy, budgets, MCP,
  testing, the UI); the homepage gets a sticky nav with a Download
  button and a `curl … | sh` installer (`https://jern.ai/install.sh`).

## 0.9.2 — 2026-08-19

- Fixed `Unauthorized` after setting an Anthropic key in the UI settings:
  the SDK client reads `ANTHROPIC_API_KEY` in its constructor and was
  cached forever on first use, so a key that arrived mid-process (settings
  panel, persisted credentials) never reached it. The bridge now rebuilds
  the client whenever the key changes. (Found on the first live run —
  restarting `jern ui` was the workaround; now unnecessary.)

## 0.9.1 — 2026-08-19

- The brain editor gained Kernel syntax highlighting — a dependency-free
  colored layer under the textarea (comments, strings incl. multi-line,
  `:keywords`, special forms, booleans, numbers, parens in the brand
  palette).

## 0.9.0 — 2026-08-19

- **The UI can now open the brain.** A "brain" drawer lists the sources
  the session loaded plus the workspace policy; open one in the editor,
  save, and the session rebuilds on the new source while the conversation
  continues. A "run tests" button runs the agent package's regression
  suite (offline replay) with live verdicts. Installed files are
  read-only — edit a workspace copy (`jern eject`). The workspace policy
  can be created from its template in place. `jern ui --agent <dir>`
  serves a different agent package.
- **Settings in the UI**: switch models (validated against the provider
  table) and set API keys per provider — key status shown as presence
  only, values never echoed back; keys live in the jern process unless
  you opt into persisting to `~/.config/jern/credentials.json` (0600),
  which the CLI also reads at startup (env vars always win).
- **Token-guarded server.** Every request must carry the startup token
  from the printed URL (query or `X-Jern-Token`), so other local
  processes cannot drive the session, edit the brain, or set keys.
- **Feed upgrades**: assistant text renders markdown (code fences,
  inline code, bold); tool chips expand on click to show results, with
  red/green diffs for edits; brain-reload notes.

## 0.8.2 — 2026-08-19

- UI layout: the message feed and the composer now span the full window
  instead of a centered 46rem column.

## 0.8.1 — 2026-08-19

- Fixed an SSE subscription race the slower CI runner exposed: the
  `/events` client is now registered before its hello event, so a client
  that has received state is guaranteed to see every later broadcast
  (an approval fired into the gap could previously be lost).

## 0.8.0 — 2026-08-19

- **`jern ui`** — the chat session as a local web app, served by the
  binary itself on 127.0.0.1: streamed replies, live tool-call chips from
  the trace, git-commit notes, token totals, stop/undo — and **interactive
  approval cards**: the policy gate's question renders with a colored diff
  and approve/deny buttons, and the agent blocks until you answer. The
  page is `ui/index.html` beside the binary (edit it like everything
  else); the server is a small readable TcpListener HTTP layer (the
  managed HttpListener mis-reads request bodies on kept-alive
  connections). `--port n` to pin the port.
- **CLI styling**: brand-palette color for the chat banner, prompt, and
  status line; red/green diffs in approval prompts; green/red test
  verdicts; colored errors and interrupts. Automatically disabled when
  output is redirected or `NO_COLOR` is set.

## 0.7.0 — 2026-08-19

- **Workspace policy.** A repo can govern its own agents: `jern policy
  init` writes `.jern/policy.ikr`, which loads after the built-in policy
  and overrides what it redefines — enforced Kernel source, not prompt
  text. New rule helpers for policy authors: `(path-within? call "src/")`
  for path-scoped rules and `(command-is? call "pytest")` for shell
  allowlists (matches the command with arguments, never lookalikes), plus
  `call-path`/`call-command` and the string predicates in the handler
  environment. `jern policy` shows whichever policy is active. Sessions
  announce `using workspace policy …` whenever one loads; trust it like
  the repo's `test_command` (see docs/security-model.md).

## 0.6.0 — 2026-08-19

- **Run budgets, enforced in the handler stack.** `--budget <n>` caps a
  run at n model calls; `jern.json` `"budget": {"llm_calls": n, "tokens": m}`
  adds a token ceiling (accounted from response usage). The budget handler
  sits inside the provider handler in `handlers.ikr` (~40 lines of readable
  Kernel), counts every call before it is made, and on exhaustion turns the
  next call into an approval question — approve to grant another round,
  decline to end the run with a budget error. Exhaustion and extension land
  in the trace. Runaway loops stop themselves at the choke point, not in a
  prompt.

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
