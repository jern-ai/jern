# iron

A terminal coding agent whose brain is an **inspectable, editable, testable
program**: the agent loop, tools, and policies are
[IronKernel](https://ironkernel.org/) source shipped alongside the binary.

- `iron run "fix the failing test"` works out of the box *(M3)*
- `iron --agent ./my-agent` swaps the brain *(M6)*
- `iron test` runs the agent against recorded LLM fixtures, deterministically *(M5)*

The agent performs effects; it holds no authority. Agent code runs in a
restricted capability environment and reaches the world only through
`(perform iron/llm-call …)`, `(perform iron/tool-call …)`, … — answered by a
handler stack the host installs (trace → policy → approval → provider), itself
Kernel source you can read and replace.

Scope and milestones: [docs/implementation-plan.md](docs/implementation-plan.md).
Long-range map: [ideas/iron-agent-spec.md](ideas/iron-agent-spec.md).

## Status

Milestones M0–M5 of the [implementation plan](docs/implementation-plan.md) are
built; the *use → read → edit → test* loop from §8 works end to end:

```
$ export ANTHROPIC_API_KEY=…
$ iron run "fix the failing test"        # the agent works in your workspace,
                                         # asking before writes and shell
$ iron eject                             # the brain, as readable Kernel source
$ $EDITOR agents/default/src/main.ikr    # change the loop; no recompile
$ iron run --agent agents/default "…"
$ iron test agents/default               # deterministic replay against
                                         # recorded LLM fixtures
```

- **M5 — `iron test`**: `(deftest …)` + `(with-fixtures "f.json" …)`; record
  once, then replay network-free. Any divergence from the recording — a
  changed system prompt, different tool wiring — fails the test.
- **M4 — policy & approval**: [policy.ikr](src/Iron.Host/kernel/policy.ikr)
  decides allow/ask/deny per tool call; approvals show a diff preview; shell
  runs write-confined under `sandbox-exec` on macOS. Honest claims in
  [docs/security-model.md](docs/security-model.md).
- **M3 — the loop**: ~80 lines of Kernel in
  [agents/default/src/main.ikr](agents/default/src/main.ikr); every effect
  traced to `.iron/*.jsonl`.
- **M2 — tools**: `read_file`, `list_dir`, `grep`, `edit_file`, `shell`,
  defined in [tools.ikr](src/Iron.Host/kernel/tools.ikr) as data the LLM sees
  verbatim, dispatched through `iron/tool-call`.
- **M1 — the bridge**: requests cross the boundary in the exact Messages API
  wire shape as Kernel data (objects ↔ keyword plists, arrays ↔ vectors,
  null ↔ `:null` — see [Json.fs](src/Iron.Host/Json.fs)).
- **M0 — the inversion**: agent code runs in a safe-profile capability
  environment; authority lives in host primitives bound only in the handler
  environment.

Remaining for v0.1 (M6): session persistence/`--resume`, an interactive chat
mode, `ik pack` packaging + a second example agent on NuGet, the announce.

```
$ iron repl
iron> (iron/host-version)
"0.1.0"
iron> (clr-type "System.IO.File")
error : Getting an unbound variable: 'clr-type'
iron> (prompt iron/llm-call
        (lambda (payload k) (resume k (list payload 42)))
        (perform iron/llm-call "hello"))
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
dotnet build Iron.slnx
dotnet test Iron.slnx
dotnet run --project src/Iron.Cli -- repl
```

## Layout

| Path | What |
|---|---|
| `src/Iron.Host` | F# host: restricted env construction, host-surface injection; later the LLM bridge, tools, JSON⇄Kernel conversion |
| `src/Iron.Cli` | the `iron` binary |
| `agents/default` | the default agent as an `.ikproj` of readable Kernel source |
| `tests/Iron.Tests` | host tests (xunit) |
