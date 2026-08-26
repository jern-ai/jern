# Cloud runner contract

Jern Cloud authorizes work, reserves an organization token allowance, and
returns a short-lived run credential. Execution and provider credentials stay
on infrastructure controlled by the customer.

Before invoking a live run, a cooperating runner sets both variables:

```sh
export JERN_CLOUD_RUN_ID="run_<32 lowercase hex characters>"
export JERN_CLOUD_TOKEN_CAP="100000"
jern run --auto "the task"
```

The variables are accepted only by `jern run`, must be present together, and
are validated before a trace file is created. The cloud run ID becomes the
trace run ID. The token cap is a positive 64-bit integer.

The cap is distinct from the renewable local budget in `jern.json` or
`--budget`. It is enforced at the provider boundary, cannot be extended by an
approval, and is shared by spawned agents. A capped run fails closed if its
provider omits or returns invalid input/output token usage. Because provider
usage is known only after a response, the response that crosses the cap is
recorded and the run fails immediately; no later model or tool work proceeds.

The `run-started` trace event records:

```json
{
  "run_id": "run_<id>",
  "cloud": {
    "run_id": "run_<id>",
    "token_cap": 100000
  }
}
```

`jern receipt --json` includes `cloud_token_cap` and
`hard_token_budget_denied`. A runner hashes those exact receipt bytes before
posting completion evidence, binding reported usage and enforcement metadata
without sending provider credentials or plaintext prompts to the control
plane.

The runner obtains `JERN_CLOUD_TOKEN_CAP` from the `token_budget` field returned
by `POST /v1/runs`; it must never substitute the requested value. It uploads the
trace and posts completion with the accompanying short-lived `run_token`.