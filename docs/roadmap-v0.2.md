# iron — from working thesis to usable product (v0.2+ roadmap)

Companion to [implementation-plan.md](implementation-plan.md), which is done
(M0–M6). This document plans the next phase: enough parity with existing
agents that people *stay* after the unique features get them in the door.

The two product decisions are made: **Apache-2.0, open-core** (§7, decided
2026-08-19) and **`iron` stays the working name** until the M10 domain check
(§6). Everything else is sequenced engineering.

## 1. What "parity" means (and doesn't)

The implementation plan's own warning stands: *"competing on their turf first
means losing on polish before the differentiator is ever visible"* (§1). Full
aider parity — voice, browser UI, watch mode, leaderboards, 100+ models — is a
multi-year surface. The target is narrower and testable:

> **A developer who uses aider or Claude Code daily can switch to iron for one
> real working day without hitting a missing-feature wall.**

That is: provider choice, git safety, streaming, cost visibility, an
interruptible chat with a few commands, and a config file. Not: voice, a
browser UI, or an edit-format zoo.

## 2. Gap analysis vs. aider (docs inventory, 2026-08)

| aider feature | iron today | Verdict |
|---|---|---|
| Multiple LLM providers (OpenAI, Anthropic, Gemini, DeepSeek, Ollama, …) | Anthropic only | **Must — M7** (§3) |
| Streaming responses | full-turn only | **Must — M7**; host-side, flagged in plan §1.4 |
| Cost / token display | in trace only | **Must — M7**; usage is already recorded |
| Config (yaml, .env, model aliases) | env key only | **Must — M7** |
| Git integration (auto-commit, /undo, dirty guard) | none | **Must — M8** |
| Coding conventions file | none | **Must — M8**; ~5 lines of agent source — and a nice editability demo |
| Prompt caching | none | **Should — M8**; with wire-shape passthrough the *agent source* can place `cache_control` itself |
| In-chat commands (/add, /model, /undo, …) | chat only, no commands | **Must — M9** (small set: /model /undo /clear /cost /help) |
| Interrupt (ctrl-c mid-turn) | none | **Must — M9**; cancellation is host-side work (plan §1.4) |
| Repository map (tree-sitter) | none; agent greps agentically | **Should — M9, cheap v1**: file-tree + conventions in first context; agentic search (à la Claude Code) covers much of the need. Tree-sitter map deferred until evals say otherwise |
| Linting & testing after edits | demoed as an agent-source edit | **Should — M9**: promote to config (`test_command`) read by the default agent |
| Non-code editing (docs, config files) | works (see agents/docs) | Done |
| Edit formats (whole/diff/udiff) | not needed — native function-calling `edit_file` | **Skip**, deliberately: edit formats exist to coax text-completion edits; iron uses tool calls |
| Image & URL context | none | Defer (post-v0.2) |
| Watch mode (AI comments) | none | Defer |
| Voice | none | Skip for now |
| Browser UI | none | Skip — that's the spec's Phase 2, explicitly out of scope |
| Notifications | none | Defer |

What iron has that aider doesn't — the reason any of this matters — stays the
headline: the loop is source (`iron eject`), policy is source (`policy.ikr`),
and the agent is unit-testable (`iron test`). Parity work must not regress
those: **every feature below lands as either host capability or agent
source, never as opaque host behavior.**

## 3. Provider strategy (M7)

Principles: the handler seam *is* the abstraction (plan §6.3); the canonical
conversation format stays the Anthropic-wire-shape Kernel data we froze in
M1 — so sessions, traces, and **fixtures stay provider-independent**, and
`iron test` keeps working no matter what serves the tokens.

- **Keep the native Anthropic bridge** (raw passthrough, unchanged).
- **Add one OpenAI-compatible bridge** (Chat Completions wire format). This
  single bridge covers OpenAI, Ollama, OpenRouter, DeepSeek, xAI, Mistral,
  Groq, LM Studio, and most local runtimes — it's one translation layer
  (canonical ⇄ OpenAI shapes: `tools`, `tool_calls`/`tool_use`,
  `role:"tool"`/`tool_result`), not one per provider.
- **Gemini** via its OpenAI-compatibility endpoint first; native later if
  gaps appear.
- **Selection**: aider-style model prefixes (`--model openai/gpt-…`,
  `ollama/…`, `anthropic/…`) plus a config file for endpoints, key env-var
  names, and aliases. Dispatch lives in the host bridge; the agent source
  doesn't change per provider.
- Microsoft.Extensions.AI stays what the plan called it: a seam kept in view
  for embedders, not the first dependency — its lowest-common-denominator
  types would break the wire-shape passthrough that makes fixtures exact.

Exit test for M7: the demo's act 1 runs unchanged against OpenAI and against
a local Ollama model, streaming, with a cost line at the end — and
`iron test` still replays the same committed fixtures.

## 4. Stable releases (M10, mechanics ready earlier)

1. **Unblock the dependency**: publish `IronKernel.Runtime` (and a library
   split of parser/compiler out of the exe) to NuGet — upstream work already
   flagged; it also unlocks `dotnet tool install` for the CLI.
2. `release.yml` mirroring IronKernel's: tag `vX.Y.Z` → per-RID archives
   (osx-arm64, osx-x64, linux-x64, linux-arm64, win-x64) with the kernel
   files and both agents bundled.
3. Install story in order of effort: GitHub release archives → `dotnet tool
   install` → Homebrew tap.
4. Versioning: keep the root `version` file + tags; add a CHANGELOG.

## 5. Website & documentation (M10)

Follow the IronKernel pattern (static site + GitHub Pages, `pages.yml`).
Docs to write, in priority order:

1. Install & quickstart (the four-act demo, textified)
2. Connecting providers (per-provider one-pagers, aider-style)
3. The agent's anatomy: prelude, tools.ikr, policy.ikr, handlers.ikr, main.ikr
4. Writing and testing your own agent (`iron test`, fixtures, `--record`)
5. Security model (exists — docs/security-model.md, promote to site)
6. Reference: effects, boundary data convention, prelude functions, CLI
7. FAQ + comparison page (honest table vs aider / Claude Code / OpenHands)

Domain and site name blocked on §6.

## 6. Decision: the name  *(decided: keep `iron` until M10)*

"iron" the working name has real collisions: the dormant-but-known Rust web
framework, SRWare Iron (browser), Iron Fish (crypto). None is a dev-tools
agent, and the Iron* prefix has genuine .NET lineage (IronPython, IronRuby,
IronKernel — the last one is ours), which argues for keeping it.

Criteria: typeable binary name (≤5 chars), unique enough to search, domain +
GitHub org + NuGet prefix available, no software-class trademark conflict.

Shortlist to check against those criteria:
- **iron** — keep; strongest lineage tie to IronKernel; weakest uniqueness.
- **vau** — the Kernel operative the whole design stands on; 3 letters,
  deeply on-brand, almost certainly available; opaque to outsiders (which a
  tagline fixes).
- **ingot / wrought / ferrum** — iron-adjacent, more searchable, keep the
  metallurgy without the collisions.

Recommendation: decide only when the website/domain work starts (M10);
everything in the codebase stays `iron` until then — renaming the binary is
one MSBuild property.

## 7. Decision: open source vs. commercial  *(decided: Apache-2.0, open-core)*

Constraints: the long-range plan monetizes Pro/Team/Enterprise tiers, a
registry, cloud execution, and an embeddable runtime (spec §7). IronKernel —
which iron ships and depends on — is already **Apache-2.0**. And the
product's entire pitch is *inspectability*: a closed-source agent whose
tagline is "read the agent source" is self-refuting.

| Option | Gets you | Costs you |
|---|---|---|
| **Apache-2.0 everywhere, open-core business** | Max adoption + trust; coherent with IronKernel; OSI "open source" label; commercial layer = cloud execution, team registry, org policy/audit, embedding support | A competitor may ship your CLI; moat must live in services and pace |
| AGPL core + commercial dual license | Legal shield against cloud-wrapping | Enterprise-lawyer friction for the exact power-user-in-a-company persona the plan targets; CLA overhead |
| FSL-1.1-Apache (source-available, auto-converts to Apache-2.0 after 2 years) | Reads like open source, converts to it; blocks direct competitors meanwhile | Can't be called open source; chills some contribution; brand risk in a community-driven niche |

Recommendation: **Apache-2.0 for everything in this repo** (CLI, host,
default agents), open-core for monetization. The wedge's risk is obscurity,
not cloning — nobody fast-follows a product whose moat is a language runtime
you also maintain. Two hygiene items either way: adopt DCO sign-offs now so
relicensing stays possible, and keep third-party agent packages under their
authors' licenses (the registry ToS problem, later).

If the fear of a hosted clone outweighs adoption, FSL is the fallback — but
pick before the announce; relicensing *after* people arrive burns trust
(see HashiCorp).

## 8. Milestones

Same discipline as before: every milestone ends demoable, and `iron test`
keeps passing throughout.

- **M7 — providers, streaming, cost, config.**
  Exit: act 1 of the demo runs against Anthropic, OpenAI, and local Ollama;
  responses stream; a cost/usage line prints per turn; `iron.yml` holds
  model + aliases; the committed fixtures still replay unchanged.
- **M8 — git safety + conventions + caching.**
  Auto-commit per agent change (message from the task + diff), `/undo`,
  dirty-repo guard, `CONVENTIONS.md` pulled into the system prompt by the
  default agent, `cache_control` placed from agent source.
  Exit: a session's `git log` reads like aider's; killing iron mid-task
  loses nothing.
- **M9 — chat UX.**
  Ctrl-C interrupt, `/model /undo /clear /cost /help`, status line
  (model, tokens, cost), file-tree context v1, `test_command` config wired
  into the default agent.
  Exit: a 30-minute real session in a stranger's repo with no walls.
- **M10 — productization.**
  Name and license decided (§6, §7); IronKernel.Runtime on NuGet; per-RID
  releases; website + the seven docs; announce with the four-act screencast.
  Exit: plan §8's stranger test, starting from a downloaded binary.
