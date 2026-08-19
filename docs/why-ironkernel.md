# Why IronKernel? — the "couldn't this just be Python?" question

The question deserves a straight answer, because half of it is a concession.

**An *editable* agent loop needs no special language.** aider is an editable
agent loop: it's open-source Python, and you can change any line of it. So is
OpenHands. If "open the loop and edit it" were the whole pitch, jern would be
a worse aider.

The pitch is a conjunction, and the other three conjuncts are where the
language choice stops being interchangeable:

> an agent loop that is **editable** *and* **confined** *and* **interposable**
> *and* **deterministically testable** — at the same time, with the same code.

## What each conjunct demands

**Confined.** When you edit aider's loop — or install someone else's fork of
it — that code runs with the full authority of the Python process: `import
os`, `import requests`, every credential in the environment. Editability and
safety trade off directly. In jern they don't: agent source evaluates in an
IronKernel capability environment (the `safe` profile) that has *no* file,
network, CLR, or source-loading primitives, and the values behind those
primitives check the invoking environment, so copying one in doesn't help.
The only authority an agent package holds is the set of unforgeable effect
tags the host handed it. That's not a convention or a linter rule; it's the
runtime's semantics — first-class environments carrying host-authority sets
with intersection on inheritance, which is Kernel's founding idea
(Shutt's thesis) plus IronKernel's capability extension.

- *Python cannot do this in-process.* CPython sandboxing is a graveyard
  (`rexec`, `pysandbox`, endless escapes); the community position is "don't".
  You'd fall back to OS isolation — a subprocess or container — and then the
  host can no longer hand the agent closures as capabilities; everything
  crosses an IPC boundary as serialized data, and the architecture becomes a
  different (and heavier) product.
- *JavaScript almost can*, via SES/Hardened JS (object capabilities) or V8
  isolates — the honest nearest neighbor on this axis. It requires freezing
  the intrinsics and auditing every endowment; possible, heroic, and still
  without the next conjunct.

**Interposable.** jern's spine is the handler stack: `log → approval →
provider → tool-executor → git → policy → agent`, ~150 lines of Kernel a user
can read and replace. It works because Kernel has *tagged deep effect
handlers with resumable one-shot continuations*: the policy layer intercepts
`jern/tool-call`, and *re-performing* the same effect continues the search
outward to the git layer, then the executor. Every effect crosses one choke
point, which is why the trace is complete and why policy is enforcement
rather than middleware convention.

- Python and JS have no delimited control; you'd encode handlers as
  middleware lists or generator trampolines — reasonable engineering, but now
  the interposition machinery is host architecture, and a user-edited
  `policy.py` is a *host plugin* running with full process authority. That's
  exactly the thing hooks-and-config agents (Claude Code hooks, aider's
  config) already are.
- A Scheme with delimited continuations — *Racket*, notably, which also has
  real sandboxes — could genuinely express this. See "the honest
  competitors" below.

**Deterministically testable.** `jern test`'s record/replay is host
machinery, and a Python agent could have a VCR-style equivalent — another
concession. What Kernel adds is the *guarantee* side: the agent can only
reach a model through the `jern/llm-call` tag, so substituting the fixture
handler is exhaustive by construction. A Python agent's test double is a
convention any `import openai` can bypass — including one the model itself
writes into the loop, which is not a hypothetical failure mode for a
self-editing agent.

**And the same code.** The reason these compose in a few hundred lines
rather than a framework: code is data (agent requests are literally the
Messages-API wire shape as Kernel data, so fixtures diff as data), operatives
(`vau`) make `define-tool` and `with-fixtures` ten-line definitions with no
macro phase, and environments double as the module system, so "evaluate the
agent's source in the agent's world" is one `eval` with the right
environment — the correctness of `jern eject`/`--agent` falls out of the
semantics.

## The honest competitors

| Alternative | What it gives | What it costs |
|---|---|---|
| **Python / JS embedded as-is** | Editability, huge familiarity | No in-process confinement; interposition by convention; the security claim in docs/security-model.md becomes false |
| **Python in a subprocess/container per agent** | Real OS isolation | No first-class capability passing; IPC serialization everywhere; heavyweight sessions; still no effect handlers |
| **SES / Hardened JavaScript** | Genuine object capabilities in a mainstream language | Freeze-the-world discipline, endowment auditing, no delimited control for the handler stack; years of hardening work Agoric already spent |
| **Racket (sandboxes + delimited continuations + macros)** | Could plausibly rebuild jern; the most serious "any other Scheme" answer | Capabilities are a library posture, not the core semantic; no CLR embedding for the .NET/enterprise story; and it wouldn't be *ours* (below) |
| **WASM components** | Best-in-class sandboxing + capability imports | A compile target, not a live medium: no code-as-data, no REPL into the running agent, agents stop being one readable file |

## The strategic part (for when the question means "is this a moat?")

Owning the runtime cuts both ways, and we say both out loud:

- **It's leverage.** jern is IronKernel's first demanding customer: building
  M0–M9 surfaced four runtime bugs (keyword equality, `vector-length`,
  effect-payload double-evaluation, resumptions dropping intermediate prompt
  frames) that were diagnosed, fixed, tested, and merged upstream in a day —
  because the language team and the product team are the same person. A
  product built on someone else's Racket waits on someone else's release
  train, and its capability story is subject to someone else's priorities.
- **It's a tax.** A niche language is an adoption cost, full stop. That's why
  the plan's first risk-table row says the defaults must be excellent enough
  that nobody *has* to edit Kernel — the testability and audit story sell
  even to users who never open `main.ikr`. And the surface users touch is
  deliberately small: plists that mirror the JSON they already know, a loop
  that reads like pseudocode.

## The short answers

One-liner:
> "Editable was never the hard part — aider is editable Python. The hard part
> is *editable without inheriting the host's authority*, and that takes a
> language where environments are capabilities and effects are the only way
> out. Python can't do that in-process; Kernel is that idea, implemented."

One paragraph:
> Any language gives you an editable loop. IronKernel gives you an editable
> loop that runs confined (capability environments — the agent's world simply
> contains no file/network/CLR authority, only unforgeable effect tags),
> interposable (tagged effect handlers make policy/approval/trace/git a
> replaceable 150-line stack with one choke point), and testable with a
> guarantee (the model is only reachable through a tag, so fixture replay is
> exhaustive, not a mockable convention). Racket or Hardened JavaScript could
> approximate two of the three with serious effort; Python and vanilla JS
> can't hold the security claim at all without moving to process isolation
> and becoming a different product. And owning the runtime is itself the
> flywheel: this product found and fixed four runtime bugs upstream in its
> first day of hard use.
