# jern

**jern** (Norwegian: *iron*) is a terminal coding agent you can **govern**:
the rules it works under are enforced by the runtime rather than requested in
a prompt, every run leaves a byte-exact audit trail, and its behavior has a
regression suite. https://jern.ai

```json
// jern.json — your repository's rules, enforced for every session in it
"policy": { "edits_within": ["src/"], "shell_allow": ["pytest"], "deny": ["mcp__*"] }
```

- **Rules are enforced, not suggested.** An edit outside `src/` comes back to
  the model as a denial it must work around — there is no path to the
  filesystem that skips the check. Restrictions compose by severity, so
  nothing loaded later can turn a denial into an approval.
- **Budgets are walls.** `--budget 20` means the 21st model call becomes a
  question to you, not a surprise on your bill.
- **Every run ends with a receipt** — calls, tokens against budget, files
  touched, policy decisions, trace path — re-derivable later with
  `jern receipt --md` for a pull request. And `jern replay` re-runs any
  recorded run offline: swap in stricter rules to see exactly where it
  would have gone differently.
- **`jern test`** replays recorded LLM traffic byte-exactly and asserts
  properties of the whole trajectory: never shelled out, edits stayed under
  `src/`, at most four model calls.

Why it can promise that, when a prompt cannot: the loop, the tools, and the
policy are [IronKernel](https://ironkernel.org/) source shipped beside the
binary, and agent code holds *no authority* — it reaches the world only
through effects the host's handler stack answers. Enforcement lives at that
choke point, so it cannot be argued around. You never have to open that
source (`jern.json` covers the common cases); when you want to, it is right
there — `jern eject`, edit, `jern test`.

Scope and milestones: [docs/implementation-plan.md](docs/implementation-plan.md).
Long-range map: [ideas/iron-agent-spec.md](ideas/iron-agent-spec.md).

## Status

Milestones M0–M6 of the [implementation plan](docs/implementation-plan.md) are
built; the *use → read → edit → test* loop from §8 works end to end:

```
$ export ANTHROPIC_API_KEY=…             # or OPENAI_API_KEY, or none for ollama
$ jern run "fix the failing test"        # the agent works in your workspace,
                                         # asking before writes and shell
$ jern run --model ollama/qwen3 "…"      # any provider; same agent, same tests
$ jern eject                             # the brain, as readable Kernel source
$ $EDITOR agents/default/src/main.ikr    # change the loop; no recompile
$ jern run --agent agents/default "…"
$ jern test agents/default               # deterministic replay against
                                         # recorded LLM fixtures
```

- **M23 — behavioral CI** (unreleased): `jern golden record "task"` keeps a
  real run as a committed snapshot; `jern golden check` replays every
  recording offline against the current agent and policy and fails with the
  exact divergence when behavior changed — plus declarative assertions
  (`edits_within`, `no_tools`, `max_files_edited`, …) that survive a
  deliberate re-record, so blessing new bytes cannot quietly bless an agent
  that now shells out. The bundled [GitHub Action](action/README.md) runs
  both in CI and posts one PR comment; its policy baseline is read from the
  **base commit**, so a pull request cannot weaken the rules that judge it.
- **M22 — the run receipt** (v0.13): every run ends with its own
  evidence — calls and tokens against budget, tools, files written, policy
  decisions, and the trace path — and `jern receipt [--md|--json]`
  re-derives it for any past run, because the receipt is a *pure function*
  of the trace rather than something accumulated as the run goes. The trace
  became a versioned run record to make that honest: `run-started` carries
  the schema version and the run's configuration, one `run-finished`
  carries status and duration, and a trace without them summarizes as
  explicitly partial instead of guessing. `jern ui` now persists its trace
  too, and shows the receipt when a turn ends.
- **M21 — policy from configuration** (v0.13): a `"policy"` object in
  `jern.json` — `edits_within`, `shell_allow`, `allow`, `deny`, `memory` —
  gives a repository enforced rules with no Kernel in sight. The policy
  handler now *composes* layers instead of asking one redefinable function:
  restrictions tighten, grants relax the base, and severity decides, so
  **nothing loaded later can turn a restriction's denial into an approval** —
  not a trusted grant, not a hand-written `.jern/policy.ikr`. Restrictions
  load on sight; grants can loosen approvals, so a repo-supplied one is
  confirmed once (keyed by canonical-JSON digest) and declining keeps the
  restrictions. `--policy-baseline <file>` supplies rules from outside the
  checkout that a pull request may tighten but never weaken, and
  `--policy-trust <sha256>` blesses grants where jern must not prompt.
  `jern policy` shows every rule with provenance and trust status;
  `jern policy --show-compiled` prints the Kernel it all compiles to.
- **M20 — programmatic tool calling** (v0.12): the model writes a
  whole IronKernel program instead of one tool call per round-trip — the
  new `kernel_eval` tool evaluates it in a persistent sandbox child of
  the agent environment, under a fresh copy of the entire handler stack.
  Real control flow across many tool calls in one step, definitions
  that persist across programs, program errors returned for the model
  to fix — and every effect *inside* a program still individually
  policed, approved, budgeted, and traced, because authority lives at
  the effects, not the code. Runaway programs hit a wall-clock cap and
  are abandoned (they lose all effect access); recorded programs
  re-execute under `jern replay`. Companion: `.jern/skills.ikr`, an
  unprivileged workspace library both agent source and model programs
  can call.
- **M19 — the effect architecture pays out** (v0.11): four features
  that fall out of "everything is an effect through one choke point".
  **Subagents**: `(spawn-agent "task")` / `(spawn-agent-named "docs" "task")`
  fork a child session — the same policy/approval/budget/trace stack
  composes recursively onto it, its trace lines are tagged with a spawn
  id, and depth is capped host-side. **Persistent memory**:
  `(remember "key" "value")` / `(recall "key")` are effects answered by a
  host store in `.jern/memory.json` that survives across sessions —
  traced, and policed by a `memory-policy` hook workspace policies can
  rebind. **Time-travel replay**: `jern replay <trace.jsonl>` re-runs a
  recorded session offline (model *and* tool effects answer from the
  trace); add `--policy strict.ikr` or `--agent edited/` to fork the
  past and see exactly where behavior would have diverged. **Richer
  assertions**: `(assert-tokens-within n)`, `(assert-max-files-edited n)`,
  `(assert-trajectory pred msg)` — budgets, blast radius, and cross-turn
  invariants over the captured trajectory. Plus a `symbols` tool
  (definition-aware code search) and bubblewrap shell sandboxing on
  Linux.
- **M18 — approvals, reasoning, docs** (v0.10): `--auto` approves
  whatever policy would ask (denials still deny); `y/n/a` prompts and an
  "always" card button remember per-tool answers for the session.
  `--think <tokens>` / `--effort <level>` drive Anthropic extended
  thinking and OpenAI-style reasoning models from ~15 lines of agent
  source. Full CLI reference at https://jern.ai/docs/ and a one-line
  installer at https://jern.ai/install.sh.
- **M17 — the UI opens the brain** (v0.9): a drawer in `jern ui` lists
  the session's loaded sources and the workspace policy; edit, save, and
  the session rebuilds mid-conversation — with a one-click run of the
  agent's regression suite right beside the editor. Settings switch
  models and set provider API keys (never echoed back; optional 0600
  credentials file). The server is guarded by a startup token, Jupyter
  style. Assistant markdown, expandable tool chips with diffs.
- **M16 — `jern ui` and a styled CLI** (v0.8): the session as a local web
  app served by the binary itself — streaming replies, live tool-call
  chips from the trace, and approval cards with colored diffs the agent
  blocks on until you answer. The page is a single `ui/index.html` beside
  the binary, editable like the agents; the server is a small readable
  HTTP layer in [Ui.fs](src/Jern.Cli/Ui.fs). The terminal grew matching
  colors: brand-palette chrome, diff-colored approval prompts, green/red
  test verdicts (`NO_COLOR` respected).
- **M15 — workspace policy** (v0.7): the repo governs its agents.
  `jern policy init` writes `.jern/policy.ikr` — enforced rules that
  override the built-ins for every session in that workspace: scope edits
  with `(path-within? call "src/")`, allowlist commands with
  `(command-is? call "pytest")`, allow specific MCP tools, or deny
  categories outright with a reason the model sees. It's Kernel source at
  the same choke point as everything else, so decisions land in the trace
  and the rules are testable.
- **M14 — run budgets** (v0.6): `jern run --budget 20 "task"` (or a
  `"budget"` object in `jern.json` with `llm_calls`/`tokens` limits) is a
  *hard* cap enforced by a ~40-line handler in
  [handlers.ikr](src/Jern.Host/kernel/handlers.ikr): once exhausted, the
  next model call becomes an approval question — grant another round or
  end the run. Not advice to the model; a wall in front of it.
- **M13 — trajectory assertions** (v0.5): agent tests can assert
  properties of the run itself — `(assert-no-tool-call "shell")`,
  `(assert-edits-within "src/")`, `(assert-max-llm-calls 4)` — from the
  captured effect trace, offline and deterministic. Behavioral contracts
  on top of byte-exact replay: the TDD agent's suite asserts the refused
  premature edit *never became an effect*; the docs agent's asserts a
  docs run never shells out. Vocabulary in
  [test-prelude.ikr](src/Jern.Host/kernel/test-prelude.ikr), extensible
  like everything else.
- **M12 — the TDD agent** (v0.4): a bundled example that *enforces*
  red→green in its own loop — implementation edits are refused as tool
  errors until a failing test run has been observed
  ([agents/tdd](agents/tdd/README.md), the gate is ~35 lines of
  [main.ikr](agents/tdd/src/main.ikr)). A prompt can request this; only a
  loop you own can promise it. Its regression suite replays a recorded
  conversation where the model tries to implement first and is refused —
  weaken the gate and `jern test agents/tdd` fails offline. Host support:
  `string-contains?`/`string-prefix?`/`string-suffix?` now injected into
  the agent environment (pure predicates, no authority).
- **M11 — MCP** (v0.3): jern is an MCP client. Add servers in `jern.json`
  (`"mcp_servers": { "github": { "command": "npx", "args": […] } }`) and
  their tools join the agent's toolset as `mcp__<server>__<tool>`, flowing
  through the same effect, policy, approval, trace, and fixture layers as
  the built-ins — every MCP call is ask-gated until your
  [policy.ikr](src/Jern.Host/kernel/policy.ikr) says otherwise, and shows
  up in the JSONL trace. `jern mcp` lists what the configured servers
  offer. The whole client (stdio JSON-RPC on the frozen JSON⇄Kernel
  convention) is [~250 readable lines](src/Jern.Host/Mcp.fs), zero new
  dependencies.
- **M9 — chat UX** (v0.2 roadmap): Ctrl-C interrupts the turn (streaming
  aborts, the next dispatch refuses, history stays consistent); chat gets
  `/model` (switch providers mid-session), `/undo`, `/clear`, `/cost`,
  `/help`, and a status line (model · tokens · session). The default agent
  opens with a `file_tree` snapshot in the first message (cache-friendly:
  the system prompt stays byte-stable) and, when `jern.json` sets
  `test_command`, runs your tests after every edit and reacts to the result.
- **M8 — git safety** (v0.2 roadmap): every approved `edit_file` is
  auto-committed (author `jern <jern@localhost>`, task in the message), with
  your uncommitted changes to that file saved on their own commit first;
  `jern undo` / `/undo` pops exactly one jern commit and refuses anything
  else. The whole layer is ~40 lines of
  [handlers.ikr](src/Jern.Host/kernel/handlers.ikr). The default agent also
  pulls `CONVENTIONS.md` into its system prompt and places an Anthropic
  prompt-cache breakpoint — both from agent source.
- **M7 — providers, streaming, cost** (v0.2 roadmap): `--model
  provider/model` routes to Anthropic natively or to any OpenAI-compatible
  endpoint — OpenAI, Ollama, OpenRouter, DeepSeek, Groq, Mistral, xAI,
  Gemini, LM Studio, or your own via `jern.json`. Responses stream to the
  terminal; a token line prints per command. The canonical conversation
  format is unchanged, so fixtures and `jern test` are provider-independent.
- **M6 — sessions & distribution**: bare `jern` is an interactive chat whose
  history persists to `.jern/sessions/` (`jern --resume` continues it);
  `jern eject` / `--agent` swap the brain; `ik pack` builds both bundled
  agents as NuGet packages. The second agent,
  [agents/docs](agents/docs/src/main.ikr), narrows its own tool surface in
  ~10 lines of source — no shell, docs-only.
- **M5 — `jern test`**: `(deftest …)` + `(with-fixtures "f.json" …)`; record
  once, then replay network-free. Any divergence from the recording — a
  changed system prompt, different tool wiring — fails the test.
- **M4 — policy & approval**: [policy.ikr](src/Jern.Host/kernel/policy.ikr)
  decides allow/ask/deny per tool call; approvals show a diff preview; shell
  runs write-confined under `sandbox-exec` on macOS and `bubblewrap` on
  Linux. Honest claims in [docs/security-model.md](docs/security-model.md).
- **M3 — the loop**: ~80 lines of Kernel in
  [agents/default/src/main.ikr](agents/default/src/main.ikr); every effect
  traced to `.jern/*.jsonl`.
- **M2 — tools**: `read_file`, `list_dir`, `grep`, `edit_file`, `write_file`, `shell`,
  defined in [tools.ikr](src/Jern.Host/kernel/tools.ikr) as data the LLM sees
  verbatim, dispatched through `jern/tool-call`.
- **M1 — the bridge**: requests cross the boundary in the exact Messages API
  wire shape as Kernel data (objects ↔ keyword plists, arrays ↔ vectors,
  null ↔ `:null` — see [Json.fs](src/Jern.Host/Json.fs)).
- **M0 — the inversion**: agent code runs in a safe-profile capability
  environment; authority lives in host primitives bound only in the handler
  environment.

Every release ships per-RID binaries (see Releases) with the kernel source
and all three agents (default, docs, tdd) bundled; `jern eject` works
offline from any of them.

```
$ jern repl
jern> (jern/host-version)
"0.2.0"
jern> (clr-type "System.IO.File")
error : Getting an unbound variable: 'clr-type'
jern> (prompt jern/llm-call
        (lambda (payload k) (resume k (list payload 42)))
        (perform jern/llm-call "hello"))
("hello" 42)
```

## Building

Requires the .NET 10 SDK and a sibling checkout of
[IronKernel](https://github.com/ironkernel-lang/IronKernel) (i.e.
`../IronKernel` next to this repo; override with
`-p:IronKernelRepo=/path/to/IronKernel`). This is temporary: the dependency
moves to NuGet once `IronKernel.Runtime` and a library split of the
parser/compiler are published — tracked as upstream work, this repo being the
language's first demanding customer.

```bash
dotnet build Jern.slnx
dotnet test Jern.slnx
dotnet run --project src/Jern.Cli -- repl
```

## Layout

| Path | What |
|---|---|
| `src/Jern.Host` | F# host: restricted env construction, host-surface injection; later the LLM bridge, tools, JSON⇄Kernel conversion |
| `src/Jern.Cli` | the `jern` binary |
| `agents/default` | the default agent as an `.ikproj` of readable Kernel source |
| `agents/docs` | a docs-only example agent with a narrowed tool surface |
| `agents/tdd` | an example agent that enforces test-first in its loop |
| `tests/Jern.Tests` | host tests (xunit) |
