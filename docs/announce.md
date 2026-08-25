# Announce — jern v0.14 (Show HN, ready to post)

Submission:

- **Title** (74 chars): `Show HN: Jern – a coding agent governed by rules your repository enforces`
- **URL**: `https://jern.ai`
- **Text** (HN supports url+text on Show HN; plain text, no markdown):

---

Every coding agent I've used takes its rules from a prompt, and a prompt is a
suggestion the model follows until it doesn't. Jern (Norwegian for "iron") is
a terminal coding agent where the rules are enforced by the runtime instead:
you write them in your repository's jern.json, and the agent has no path to
the filesystem that skips the check.

  "policy": { "edits_within": ["src/"], "shell_allow": ["pytest"],
              "deny": ["mcp__*"] }

An edit outside src/ comes back to the model as a denial it has to work
around. Not a refusal the model chose — one it could not avoid.

The part I'd most like feedback on is what this makes possible in CI. Record
a real task once; the trace is committed. From then on, every pull request
replays it offline — no API key, no model calls — against the agent and
policy in that branch. Change a prompt, a policy, or a config value and the
check fails with the exact recorded-vs-actual difference:

  diverged from the recording at tool call #11.
    recorded: …"command":"python3 -m unittest discover -s tests -t ."}}
    actual:   …"command":"python3 -m unittest discover -s tests -t . -v"}}

There's a live demo repository — https://github.com/jern-ai/jern-demo — with
two open pull requests: one that turns the check red exactly like that, and
one that widens its own policy *and deletes the baseline*, where the comment
shows the base branch's rules still governing. A pull request cannot weaken
the rules that judge it, because CI reads them from the base commit.

The rest, briefly:

1. Rules compose in one direction. Restrictions only tighten, and combine by
severity, so nothing loaded later — a grant, a hand-written policy file — can
turn a denial into an approval. That property is what makes the CI baseline
worth anything.

2. Budgets are walls, not advice. --budget 20 means the 21st model call
becomes a question to you.

3. Every run ends with a receipt: calls and tokens against budget, tools
used, files actually written, policy decisions, and the trace path. It's a
pure function of the trace, so `jern receipt --md` re-derives it for any past
run and pastes into a PR.

4. Any recorded run can be re-run offline with a *different* handler —
`jern replay trace.jsonl --policy strict.ikr` shows you where a rule you're
considering would have changed a run you already paid for.

Why it can promise this when a prompt can't: the agent holds no authority.
Its loop, tools, and policy are IronKernel source (a Kernel/Scheme dialect
for .NET) shipped beside the binary, and agent code runs in a capability
environment with no file, network, or process access. It reaches the world
only by performing effects that a handler stack answers, and enforcement
lives at that one choke point. You never have to open that source — jern.json
covers the common cases, and there is no Lisp anywhere in the demo repo — but
it's right there when you want it, and `jern test` regression-tests it.

Honest limits, since this is a security-shaped claim:

- Once a shell command runs, language-level confinement is over. Shell is
  write-confined by the OS (sandbox-exec on macOS, bubblewrap on Linux) and
  gated by approval; reads and network are not restricted.
- A golden check re-executes the agent against recorded results, so it
  catches changes to the agent, its config, and the policy — not changes to a
  file the agent reads during the run. (I found that by building the demo and
  watching a pull request pass that shouldn't have.)
- CI here is check-only. Running an agent live in CI is the interesting next
  step and I haven't shipped it, because the threat model — untrusted
  triggers, secrets, auto-approval — deserves writing down first.
- For same-repo pull requests, the workflow file itself is editable by the
  pull request. Branch protection is what stops that, not this tool.

Otherwise it's a normal modern agent: Anthropic, OpenAI, Ollama or any
OpenAI-compatible endpoint; MCP servers as tools, through the same policy and
audit path as everything else; streaming; sessions; git auto-commit with
undo; a local web UI; and a mode where the model writes a whole program
composing many tool calls in one step, with every call inside it individually
policed.

Apache-2.0: https://github.com/jern-ai/jern
Security model, including what it does not defend against:
https://github.com/jern-ai/jern/blob/main/docs/security-model.md

---

*(Post-notes for the author — predictable objections, pre-answered:
"policy in a config file isn't new" → the composition guarantee is: no later
layer can loosen a restriction, which is what lets a base-branch baseline
govern a PR that rewrites its own config; "exact-match replay is brittle" →
that's golden-file testing, plus declarative assertions in a sidecar that
survive a re-record, so blessing new bytes can't quietly bless an agent that
now shells out; "why a niche Lisp?" → editable was never the hard part, aider
is editable Python; editable *without inheriting the host's authority*,
in-process, is what first-class environments give — why-ironkernel.md;
"isn't kernel_eval just RCE by design?" → the interpreter is the sandbox and
each effect crosses the policy engine, unlike container-based code
interpreters where granted authority is ambient; "so it's CodeAct" → the
per-call policing and deterministic replay are the new parts; "who is this
for?" → teams putting agents into a repo under rules, not individuals
choosing a daily driver on polish — say that plainly rather than
overclaiming. Lead with the demo repo link if the thread wants proof.)*
