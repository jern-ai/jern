# Live tasks in GitHub Actions

The behavioral-check Action can also run one explicitly authorized live task
on a customer-controlled GitHub runner. Jern Cloud supplies governance and
encrypted evidence retention; the model provider key stays in the job's secret
environment.

The pilot accepts only a manual dispatch on the repository's default branch.
Protect that branch and the workflow with review and CODEOWNERS before enabling
live work.

```yaml
name: governed live task

on:
  workflow_dispatch:
    inputs:
      task:
        description: Task for the agent (set this or issue_number)
        required: false
      issue_number:
        description: GitHub issue to resolve (set this or task)
        required: false
        type: string
      task_id:
        description: Jern Cloud task identity
        required: false
        type: string

permissions:
  contents: write
  id-token: write
  pull-requests: write

jobs:
  run:
    runs-on: ubuntu-latest
    env:
      ANTHROPIC_API_KEY: ${{ secrets.ANTHROPIC_API_KEY }}
    steps:
      - uses: actions/checkout@v4
        with:
          persist-credentials: false
      - uses: jern-ai/jern/action@main
        with:
          version: "0.14.5"
          live-task: ${{ inputs.task }}
          live-issue: ${{ inputs.issue_number }}
          live-task-id: ${{ inputs.task_id }}
          live-delivery: "pull-request"
          live-token-budget: "100000"
          baseline-path: .jern/baseline.json
          policy-trust: <full SHA-256 digest for any required grants>
          tests: "false"
          golden: "false"
          comment: "false"
          cloud-upload: "true"
```

Pin the Action to a release tag or commit before production use. `@main` above
is suitable only while testing unreleased Action changes.

Live mode does not use `--auto`. The baseline should grant only the exact tools
the task needs, and grant digests must be pinned with `policy-trust`; operations
that still require interactive approval are denied on the headless runner.

The Action requests `live-token-budget` from Cloud but executes under the
`token_budget` returned by the authorization response. Jern 0.14.5 enforces
that cap independently of renewable local budgets and across spawned agents.
After execution, including a failed execution, the Action uploads the trace,
derives a receipt, verifies that its cap matches the authorization, and posts
completion evidence. The Action then returns the original `jern run` exit code.

Set exactly one of `task` or `issue_number`. Issue content is not an automatic
trigger: a maintainer must dispatch the workflow, and any required reviewers on
the `governed-live` environment approve before the issue is read and executed.

Jern commits each governed file edit separately during the run. After a
successful run, `live-delivery: "pull-request"` preserves those commits, pushes
them to `jern/run-<workflow run>-<attempt>`, and opens a pull request against the
default branch. A failed run still uploads evidence but never publishes code;
a successful no-change run opens no pull request. The Action never pushes or
merges the default branch.

Pull-request delivery requires `contents: write` and `pull-requests: write` on
the workflow token. Keep the workflow and baseline behind CODEOWNERS, require
review on the `governed-live` environment, and retain ordinary branch protection
on the destination branch. The model has no GitHub API tool: write authority is
used only by the Action after the governed run succeeds. Checkout credentials
are not persisted, and the Action removes `GH_TOKEN` and GitHub's OIDC request
credentials from the Jern process environment before model execution.
