# jern

**jern** (Norwegian: *iron*) is a terminal coding agent whose brain is an
**inspectable, editable, testable program**: the agent loop, tools, and
policies are [IronKernel](https://ironkernel.org/) source shipped alongside
the binary. https://jern.ai

- `jern run "fix the failing test"` works out of the box *(M3)*
- `jern --agent ./my-agent` swaps the brain *(M6)*
- `jern test` runs the agent against recorded LLM fixtures, deterministically *(M5)*

The agent performs effects; it holds no authority. Agent code runs in a
restricted capability environment and reaches the world only through
`(perform jern/llm-call …)`, `(perform jern/tool-call …)`, … — answered by a
handler stack the host installs (trace → policy → approval → provider), itself
Kernel source you can read and replace.

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
  runs write-confined under `sandbox-exec` on macOS. Honest claims in
  [docs/security-model.md](docs/security-model.md).
- **M3 — the loop**: ~80 lines of Kernel in
  [agents/default/src/main.ikr](agents/default/src/main.ikr); every effect
  traced to `.jern/*.jsonl`.
- **M2 — tools**: `read_file`, `list_dir`, `grep`, `edit_file`, `shell`,
  defined in [tools.ikr](src/Jern.Host/kernel/tools.ikr) as data the LLM sees
  verbatim, dispatched through `jern/tool-call`.
- **M1 — the bridge**: requests cross the boundary in the exact Messages API
  wire shape as Kernel data (objects ↔ keyword plists, arrays ↔ vectors,
  null ↔ `:null` — see [Json.fs](src/Jern.Host/Json.fs)).
- **M0 — the inversion**: agent code runs in a safe-profile capability
  environment; authority lives in host primitives bound only in the handler
  environment.

Every release ships per-RID binaries (see Releases) with the kernel source
and both agents bundled; `jern eject` works offline from any of them.

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
