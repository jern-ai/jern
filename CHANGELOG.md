# Changelog

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
