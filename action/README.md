# `jern` behavioral check — GitHub Action

Replay your committed **golden sessions** against the agent and policy in a
pull request, run your agent test suites, and report the result as a single
PR comment. Offline and deterministic: **no API key, no model calls, no
network calls to a provider** — every model and tool result comes from the
recording.

```yaml
name: agent behavior
on: [pull_request]

permissions:
  contents: read
  pull-requests: write     # only needed for the PR comment

jobs:
  jern:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - uses: jern-ai/jern/action@v0.13.0
        with:
          version: "0.13.0"
          baseline-path: .jern/baseline.json
```

A pull request that changes a prompt, a policy, an agent's loop, or a model
setting — anything that changes what the agent *does* — fails with the exact
recorded-vs-actual difference.

## What it checks

| | |
|---|---|
| **Golden sessions** | Every `.jern/golden/*.jsonl` is replayed against this checkout's agent source and effective policy. A behavior change is a divergence; each recording's declarative assertions (`edits_within`, `no_tools`, `max_files_edited`, `max_llm_calls`, `max_tokens`) are enforced too, and those survive a re-record. |
| **Agent tests** | `jern test` on the repository's agent packages (`tests: auto` runs it when `./agents` exists). |
| **Policy** | The effective policy — every layer, with provenance and digests — is printed into the comment, so what governed the run is on the record. |

Traces are uploaded as a `jern-traces` artifact for 14 days.

## Inputs

| Input | Default | Notes |
|---|---|---|
| `version` | *required* | Exact release, e.g. `0.13.0`. Deliberately not floating: `latest` would let the tool under test change without a commit. |
| `baseline-path` | `""` | Repository-relative path to a policy baseline. **Read from the base ref, never from the PR's checkout** (see below). |
| `policy-trust` | `""` | Whitespace-separated SHA-256 policy digests whose *grants* may apply. Without a pin, unattended runs drop grants and keep restrictions. |
| `golden` | `true` | Replay `.jern/golden/`. |
| `golden-filter` | `""` | Only slugs containing this. |
| `tests` | `auto` | `auto` \| `true` \| `false`. |
| `comment` | `true` | Post/update one PR comment; falls back to the job summary. |
| `working-directory` | `.` | |
| `github-token` | `github.token` | Used only for the comment. |

## The protected baseline

A policy checked out *from* a pull request cannot govern that pull request:
the same diff could loosen `jern.json` or replace `.jern/policy.ikr`. So when
`baseline-path` is set, the action reads that file **from the base commit**
(`git show <base-sha>:<path>`) and passes it as jern's `--policy-baseline`.
Its restrictions outrank anything in the checkout, and a baseline that exists
only in the pull request is refused outright rather than silently trusted.

Restrictions in the baseline always apply. Its *grants* would need a
`policy-trust` digest, because headless runs never prompt.

## What this does **not** protect against

State it plainly, because CI security is where optimism gets expensive:

- **The workflow file itself.** For same-repo pull requests, GitHub runs the
  workflow as it exists on the head branch, so a pull request can edit the
  very job that judges it. Branch protection and required review are what stop
  that; no action can. Put the workflow (and `.jern/golden/`, and your
  baseline) behind CODEOWNERS.
- **Blessed recordings.** Re-recording a golden session is a legitimate way to
  approve new behavior, so a pull request that changes recordings is reporting
  a change, not proving it safe. The action lists changed recordings
  separately for exactly this reason — review them like policy.
- **Fork pull requests** get a read-only token, so results go to the job
  summary instead of a comment. Do not "fix" that with
  `pull_request_target`: that checks out untrusted code with write authority.
  Use a separate `workflow_run` job if you need comments on forks.
- **Anything a recording never exercised.** A golden session protects the
  behavior it captured. It is a snapshot, not a proof.

## Live runs are not here yet — on purpose

Running an agent *live* in CI (`jern run --auto` with a provider key, then
opening a branch) is the real "unattended agent" story, and it needs a threat
model written before it ships: untrusted triggers, secrets, auto-approval, and
a destination branch that cannot touch protected branches. This action is
check-only until that exists. See
[docs/roadmap-governance.md](https://github.com/jern-ai/jern/blob/main/docs/roadmap-governance.md).
