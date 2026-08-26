#!/usr/bin/env bash
set -euo pipefail

fail() {
  echo "::error::$1"
  exit 1
}

if [ "${GITHUB_EVENT_NAME:-}" != "workflow_dispatch" ]; then
  fail "live-task runs only from workflow_dispatch."
fi
if [ -z "${DEFAULT_BRANCH:-}" ] || [ "${GITHUB_REF:-}" != "refs/heads/${DEFAULT_BRANCH}" ]; then
  fail "live-task runs only from the repository's default branch."
fi
if [ "${CLOUD_MODE:-}" != "true" ]; then
  fail "live-task requires cloud-upload: 'true'."
fi
if [ -z "${BASELINE_FILE:-}" ] || [ ! -f "$BASELINE_FILE" ]; then
  fail "live-task requires baseline-path from the default branch."
fi
if [[ ! "${LIVE_TOKEN_BUDGET:-}" =~ ^[1-9][0-9]*$ ]]; then
  fail "live-token-budget must be a positive integer."
fi
if [[ ! "${JERN_VERSION:-}" =~ ^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$ ]]; then
  fail "live-task requires an exact stable jern version."
fi
major=$((10#${BASH_REMATCH[1]}))
minor=$((10#${BASH_REMATCH[2]}))
patch=$((10#${BASH_REMATCH[3]}))
if (( major == 0 && (minor < 14 || (minor == 14 && patch < 5)) )); then
  fail "live-task requires jern 0.14.5 or newer for hard token-cap enforcement."
fi
if [ -z "${ACTIONS_ID_TOKEN_REQUEST_URL:-}" ] || [ -z "${ACTIONS_ID_TOKEN_REQUEST_TOKEN:-}" ]; then
  fail "live-task requires the job permission 'id-token: write'."
fi

for command in curl jq sha256sum jern; do
  command -v "$command" >/dev/null 2>&1 || fail "live-task requires '$command' on the runner."
done
installed_version="$(jern --version)"
if [ "$installed_version" != "$JERN_VERSION" ]; then
  fail "live-task installed jern $installed_version, expected $JERN_VERSION."
fi

flags=()
flags+=(--policy-baseline "$BASELINE_FILE")
for digest in $POLICY_TRUST; do
  if [[ ! "$digest" =~ ^[0-9a-f]{64}$ ]]; then
    fail "live-task policy-trust values must be full lowercase SHA-256 digests."
  fi
  flags+=(--policy-trust "$digest")
done

audience="${CLOUD_URL%/}"
encoded_audience="$(jq -rn --arg value "$audience" '$value | @uri')"
oidc_response="$(curl --fail --silent --show-error \
  -H "Authorization: Bearer ${ACTIONS_ID_TOKEN_REQUEST_TOKEN}" \
  "${ACTIONS_ID_TOKEN_REQUEST_URL}&audience=${encoded_audience}")" \
  || fail "live-task failed while requesting GitHub identity."
oidc_token="$(jq -er '.value' <<< "$oidc_response")" \
  || fail "GitHub returned an invalid OIDC response for live-task."
echo "::add-mask::$oidc_token"

run_response="$(curl --fail --silent --show-error \
  -X POST "${audience}/v1/runs" \
  -H "Authorization: Bearer $oidc_token" \
  -H 'Content-Type: application/json' \
  --data "$(jq -cn --argjson token_budget "$LIVE_TOKEN_BUDGET" '{token_budget:$token_budget}')")" \
  || fail "Jern Cloud rejected live-task authorization."
run_id="$(jq -er '.run_id | select(test("^run_[0-9a-f]{32}$"))' <<< "$run_response")" \
  || fail "Jern Cloud returned an invalid live run ID."
run_token="$(jq -er '.run_token // .upload_token' <<< "$run_response")" \
  || fail "Jern Cloud returned an invalid live run credential."
token_cap="$(jq -er '.token_budget | select(type == "number" and . > 0 and floor == .)' <<< "$run_response")" \
  || fail "Jern Cloud returned an invalid live token cap."
if [ "$token_cap" -ne "$LIVE_TOKEN_BUDGET" ]; then
  fail "Jern Cloud returned a token cap different from the reserved request."
fi
echo "::add-mask::$run_token"
echo "run_id=$run_id" >> "$GITHUB_OUTPUT"

set +e
JERN_CLOUD_RUN_ID="$run_id" JERN_CLOUD_TOKEN_CAP="$token_cap" \
  jern run ${flags[@]+"${flags[@]}"} "$LIVE_TASK"
run_exit=$?
set -e

trace=".jern/trace-${run_id}.jsonl"
if [ ! -f "$trace" ]; then
  fail "jern did not create the authorized live trace for $run_id."
fi

curl --fail --silent --show-error \
  -X POST "${audience}/v1/runs/${run_id}/trace" \
  -H 'Content-Type: application/x-ndjson' \
  -H "Authorization: Bearer $run_token" \
  --data-binary "@$trace" >/dev/null \
  || fail "Jern Cloud live trace upload failed for ${run_id}."

receipt_file="${RUNNER_TEMP}/jern-live-receipt-${run_id}.json"
jern receipt "$trace" --json > "$receipt_file" \
  || fail "Jern could not derive the live receipt for ${run_id}."
receipt_cap="$(jq -er '.cloud_token_cap | select(type == "number" and . > 0 and floor == .)' "$receipt_file")" \
  || fail "The live receipt did not record its cloud token cap."
if [ "$receipt_cap" -ne "$token_cap" ]; then
  fail "The live receipt token cap does not match its authorization."
fi
receipt_digest="$(sha256sum "$receipt_file" | cut -d ' ' -f 1)"
outcome="$(jq -er 'if .complete then (if .status == "ok" then "success" elif .status == "interrupted" then "interrupted" else "failure" end) else "abandoned" end' "$receipt_file")" \
  || fail "Jern returned an invalid live receipt outcome for ${run_id}."
input_tokens="$(jq -er '.input_tokens | select(type == "number" and . >= 0 and floor == .)' "$receipt_file")" \
  || fail "Jern returned invalid live input-token usage for ${run_id}."
output_tokens="$(jq -er '.output_tokens | select(type == "number" and . >= 0 and floor == .)' "$receipt_file")" \
  || fail "Jern returned invalid live output-token usage for ${run_id}."
completion="$(jq -cn \
  --arg outcome "$outcome" \
  --argjson input_tokens "$input_tokens" \
  --argjson output_tokens "$output_tokens" \
  --arg receipt_digest "$receipt_digest" \
  '{outcome:$outcome,input_tokens:$input_tokens,output_tokens:$output_tokens,receipt_digest:$receipt_digest}')"
curl --fail --silent --show-error \
  -X POST "${audience}/v1/runs/${run_id}/complete" \
  -H 'Content-Type: application/json' \
  -H "Authorization: Bearer $run_token" \
  --data "$completion" >/dev/null \
  || fail "Jern Cloud live completion failed for ${run_id}."

sha256sum "$trace" >> "$TRACE_BASELINE"
echo "outcome=$outcome" >> "$GITHUB_OUTPUT"
echo "::notice::Completed live task as Jern Cloud run $run_id ($outcome)."
exit "$run_exit"
