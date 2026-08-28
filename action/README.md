# `jern` behavioral check — GitHub Action

Replay your committed **golden sessions** against the agent and policy in a
pull request, run your agent test suites, and report the result as a single
PR comment. Execution is deterministic: **no API key, no model calls, no
network calls to a provider** — every model and tool result comes from the
recording. Generated traces can optionally be encrypted and retained by Jern
Cloud using GitHub's short-lived OIDC identity, with no cloud secret.

```yaml
name: agent behavior
on: [pull_request]

permissions:
  contents: read
  pull-requests: write # only needed for the PR comment
  id-token: write # enables secretless Jern Cloud trace upload

jobs:
  jern:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - uses: jern-ai/jern/action@v0.14.0
        with:
          version: "0.14.0"
          baseline-path: .jern/baseline.json
```

A pull request that changes a prompt, a policy, an agent's loop, or a model
setting — anything that changes what the agent _does_ — fails with the exact
recorded-vs-actual difference.

## What it checks

|                     |                                                                                                                                                                                                                                                                                                                 |
| ------------------- | --------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| **Golden sessions** | Every `.jern/golden/*.jsonl` is replayed against this checkout's agent source and effective policy. A behavior change is a divergence; each recording's declarative assertions (`edits_within`, `no_tools`, `max_files_edited`, `max_llm_calls`, `max_tokens`) are enforced too, and those survive a re-record. |
| **Agent tests**     | `jern test` on the repository's agent packages (`tests: auto` runs it when `./agents` exists).                                                                                                                                                                                                                  |
| **Policy**          | The effective policy — every layer, with provenance and digests — is printed into the comment, so what governed the run is on the record.                                                                                                                                                                       |

Traces are uploaded as a `jern-traces` artifact for 14 days.

## Inputs

| Input               | Default               | Notes                                                                                                                                   |
| ------------------- | --------------------- | --------------------------------------------------------------------------------------------------------------------------------------- |
| `version`           | _required_            | Exact release, e.g. `0.14.0`. Deliberately not floating: `latest` would let the tool under test change without a commit.                |
| `baseline-path`     | `""`                  | Repository-relative path to a policy baseline. **Read from the base ref, never from the PR's checkout** (see below).                    |
| `policy-trust`      | `""`                  | Whitespace-separated SHA-256 policy digests whose _grants_ may apply. Without a pin, unattended runs drop grants and keep restrictions. |
| `golden`            | `true`                | Replay `.jern/golden/`.                                                                                                                 |
| `golden-filter`     | `""`                  | Only slugs containing this.                                                                                                             |
| `tests`             | `auto`                | `auto` \| `true` \| `false`.                                                                                                            |
| `comment`           | `true`                | Post/update one PR comment; falls back to the job summary.                                                                              |
| `working-directory` | `.`                   |                                                                                                                                         |
| `github-token`      | `github.token`        | Used for PR comments and optional live pull-request delivery.                                                                           |
| `cloud-upload`      | `auto`                | `auto` uploads when `id-token: write` is available and warns on failure; `true` requires success; `false` disables cloud access.        |
| `cloud-url`         | `https://api.jern.ai` | Cloud API origin and OIDC audience.                                                                                                     |
| `live-task`         | `""`                  | Live task text; set exactly one of this or `live-issue`.                                                                                |
| `live-issue`        | `""`                  | GitHub issue number resolved after manual dispatch and environment approval.                                                            |
| `live-token-budget` | `""`                  | Positive Cloud reservation for a live run.                                                                                              |
| `live-task-id`      | `""`                  | Cloud task identity carried through OIDC authorization to correlate execution, evidence, and delivery.                                  |
| `live-delivery`     | `none`                | `none` \| `pull-request`; the latter publishes successful Jern commits to an isolated branch and PR.                                    |

## Jern Cloud upload

The action requests a short-lived GitHub OIDC token whose audience is the
configured `cloud-url`. Jern Cloud verifies GitHub's signature and admits the
run only when the token's repository ID and name match an active, selected
Jern Cloud GitHub App installation. The API returns a one-hour, single-run
upload credential; only its SHA-256 digest is stored, and the trace is encrypted
before PostgreSQL persistence.

Behavioral checks request a zero-token reservation because their replays make
no live model calls. Live tasks request a positive reservation, and the
server-returned cap is applied to Jern's local hard token budget before
execution begins.

Cloud upload never needs a repository or organization secret. To require cloud
retention rather than treating it as best-effort, set `cloud-upload: "true"`.
Committed `.jern/golden/*.jsonl` fixtures are not sent to the cloud; only traces
generated at the top level of `.jern/` are uploaded. Each invocation records a
current CI trace with the behavioral verdict, GitHub provenance, and Markdown
summary; it does not relabel a historical golden recording as a new run.

## The protected baseline

A policy checked out _from_ a pull request cannot govern that pull request:
the same diff could loosen `jern.json` or replace `.jern/policy.ikr`. So when
`baseline-path` is set, the action reads that file **from the base commit**
(`git show <base-sha>:<path>`) and passes it as jern's `--policy-baseline`.
Its restrictions outrank anything in the checkout, and a baseline that exists
only in the pull request is refused outright rather than silently trusted.

Restrictions in the baseline always apply. Its _grants_ would need a
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
- **What the agent _reads_ at runtime.** A check re-executes the agent
  against the recording's model and tool results, so it catches changes to
  the agent, its configuration, and the policy — but not a change to a file
  the agent reads through a tool during the run. Editing `CONVENTIONS.md`,
  for instance, changes what a _live_ run would see; the replay still
  answers that `read_file` from the recording. Re-record to capture it.

## Governed live tasks and issue-to-PR delivery

The Action can run one live Jern task after a manual `workflow_dispatch` on the
default branch. Set exactly one of `live-task` or `live-issue`, require a
protected baseline and Jern Cloud upload, and give the run a positive token
budget. The provider key stays on the customer runner; Cloud returns the hard
cap applied by the runtime and retains the encrypted trace and receipt.

With `live-delivery: "pull-request"`, a successful run preserves Jern's
per-file commits, pushes them to an isolated `jern/run-*` branch, and opens a
pull request. Failure still uploads evidence but does not publish code. A
no-change success opens no pull request. This mode requires `contents: write`
and `pull-requests: write`; protect the workflow, baseline, live environment,
and default branch. The agent itself receives no GitHub API tool and the Action
never pushes or merges the default branch.

See the complete workflow and threat boundary in
[docs/live-action.md](https://github.com/jern-ai/jern/blob/main/docs/live-action.md).
