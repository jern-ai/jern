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
if [ -n "${LIVE_TASK:-}" ] && [ -n "${LIVE_ISSUE:-}" ]; then
  fail "set exactly one of live-task or live-issue."
fi
if [ -z "${LIVE_TASK:-}" ] && [ -z "${LIVE_ISSUE:-}" ]; then
  fail "set exactly one of live-task or live-issue."
fi
if [ -n "${LIVE_ISSUE:-}" ] && [[ ! "$LIVE_ISSUE" =~ ^[1-9][0-9]*$ ]]; then
  fail "live-issue must be a positive GitHub issue number."
fi
if [ -z "${BASELINE_FILE:-}" ] || [ ! -f "$BASELINE_FILE" ]; then
  fail "live-task requires baseline-path from the default branch."
fi
if [[ ! "${LIVE_TOKEN_BUDGET:-}" =~ ^[1-9][0-9]*$ ]]; then
  fail "live-token-budget must be a positive integer."
fi
if [ -n "${LIVE_TASK_ID:-}" ] && [[ ! "$LIVE_TASK_ID" =~ ^task_[0-9a-f]{32}$ ]]; then
  fail "live-task-id must have the form task_<32 lowercase hex characters>."
fi
case "${LIVE_AGENT:-jern-native}" in
  jern-native|codex) ;;
  *) fail "live-agent must be 'jern-native' or 'codex'." ;;
esac
if [[ ! "${LIVE_TIMEOUT_MINUTES:-30}" =~ ^[1-9][0-9]*$ ]] || [ "$LIVE_TIMEOUT_MINUTES" -gt 60 ]; then
  fail "live-timeout-minutes must be an integer from 1 to 60."
fi
if [ "${LIVE_AGENT:-jern-native}" = "codex" ] && [[ ! "${LIVE_AGENT_VERSION:-}" =~ ^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-[0-9A-Za-z.-]+)?$ ]]; then
  fail "the codex adapter requires an exact live-agent-version."
fi
if [ "${LIVE_AGENT:-jern-native}" = "codex" ] && [ -z "${OPENAI_API_KEY:-}" ]; then
  fail "the codex adapter requires OPENAI_API_KEY."
fi
case "${LIVE_DELIVERY:-none}" in
  none|pull-request) ;;
  *) fail "live-delivery must be 'none' or 'pull-request'." ;;
esac
if { [ "${LIVE_DELIVERY:-none}" = "pull-request" ] || [ -n "${LIVE_ISSUE:-}" ]; } && [ -z "${GH_TOKEN:-}" ]; then
  fail "live issue delivery requires github-token."
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
if [ "${LIVE_AGENT:-jern-native}" = "codex" ]; then
  for command in codex timeout git; do
    command -v "$command" >/dev/null 2>&1 || fail "the codex adapter requires '$command' on the runner."
  done
  installed_agent_version="$(codex --version | awk '{print $NF}')"
  if [ "$installed_agent_version" != "$LIVE_AGENT_VERSION" ]; then
    fail "the codex adapter found version $installed_agent_version, expected $LIVE_AGENT_VERSION."
  fi
fi
if [ "${LIVE_DELIVERY:-none}" = "pull-request" ] || [ -n "${LIVE_ISSUE:-}" ]; then
  for command in git gh; do
    command -v "$command" >/dev/null 2>&1 || fail "live issue delivery requires '$command' on the runner."
  done
fi

if [ -n "${LIVE_ISSUE:-}" ]; then
  issue_json="$(gh api "repos/${GITHUB_REPOSITORY}/issues/${LIVE_ISSUE}")" \
    || fail "could not read GitHub issue #${LIVE_ISSUE}."
  if jq -e 'has("pull_request")' <<< "$issue_json" >/dev/null; then
    fail "#${LIVE_ISSUE} is a pull request, not an issue."
  fi
  issue_title="$(jq -er '.title | select(type == "string" and length > 0)' <<< "$issue_json")" \
    || fail "GitHub issue #${LIVE_ISSUE} has no valid title."
  issue_body="$(jq -er '.body // "" | select(type == "string")' <<< "$issue_json")" \
    || fail "GitHub issue #${LIVE_ISSUE} has an invalid body."
  LIVE_TASK="Resolve GitHub issue #${LIVE_ISSUE}: ${issue_title}"
  if [ -n "$issue_body" ]; then
    LIVE_TASK="${LIVE_TASK}

${issue_body}"
  fi
fi
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

run_request="$(jq -cn \
  --argjson token_budget "$LIVE_TOKEN_BUDGET" \
  --arg task_id "${LIVE_TASK_ID:-}" \
  --arg agent_kind "${LIVE_AGENT:-jern-native}" \
  'if $task_id == "" then {token_budget:$token_budget,agent_kind:$agent_kind} else {token_budget:$token_budget,task_id:$task_id,agent_kind:$agent_kind} end')"
run_response="$(curl --fail --silent --show-error \
  -X POST "${audience}/v1/runs" \
  -H "Authorization: Bearer $oidc_token" \
  -H 'Content-Type: application/json' \
  --data "$run_request")" \
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

trace=".jern/trace-${run_id}.jsonl"
if [ "${LIVE_AGENT:-jern-native}" = "jern-native" ]; then
  set +e
  env -u GH_TOKEN -u GITHUB_TOKEN -u ACTIONS_ID_TOKEN_REQUEST_URL -u ACTIONS_ID_TOKEN_REQUEST_TOKEN \
    -u ACTIONS_RUNTIME_TOKEN -u ACTIONS_CACHE_URL -u ACTIONS_RESULTS_URL -u ACTIONS_RUNTIME_URL \
    JERN_CLOUD_RUN_ID="$run_id" JERN_CLOUD_TOKEN_CAP="$token_cap" \
    jern run ${flags[@]+"${flags[@]}"} "$LIVE_TASK"
  run_exit=$?
  set -e
  if [ ! -f "$trace" ]; then
    fail "jern did not create the authorized live trace for $run_id."
  fi
else
  mkdir -p .jern "$RUNNER_TEMP/jern-codex-home"
  chmod 700 "$RUNNER_TEMP/jern-codex-home"
  codex_stdout="$RUNNER_TEMP/jern-codex-${run_id}.jsonl"
  codex_stderr="$RUNNER_TEMP/jern-codex-${run_id}.stderr"
  started_at="$(date -u +%Y-%m-%dT%H:%M:%S.%NZ)"
  jq -cn \
    --arg ts "$started_at" --arg run_id "$run_id" --arg version "$LIVE_AGENT_VERSION" \
    --argjson reservation "$token_cap" \
    '{ts:$ts,event:"run-started",schema_version:1,run_id:$run_id,command:"run",model:"foreign/codex",agent:"codex",budget:{llm_calls:null,tokens:null},cloud:{run_id:$run_id,reservation:$reservation},assurance:{level:"supervised",filesystem:"codex-workspace-write",network:"agent-sandbox",credentials:"provider-only",publish:"jern-wrapper",token_enforcement:"unavailable"},foreign_agent:{name:"codex",version:$version}}' \
    > "$trace"
  set +e
  printf '%s' "$OPENAI_API_KEY" | env -i PATH="$PATH" HOME="$RUNNER_TEMP/jern-codex-home" LANG="${LANG:-C.UTF-8}" \
    CODEX_HOME="$RUNNER_TEMP/jern-codex-home" codex login --with-api-key \
    > /dev/null 2> "$codex_stderr"
  login_exit=${PIPESTATUS[1]}
  if [ "$login_exit" -eq 0 ]; then
    timeout --signal=TERM --kill-after=30s "${LIVE_TIMEOUT_MINUTES}m" \
    env -i PATH="$PATH" HOME="$RUNNER_TEMP/jern-codex-home" LANG="${LANG:-C.UTF-8}" \
      CODEX_HOME="$RUNNER_TEMP/jern-codex-home" \
      codex exec --json --ephemeral --ignore-user-config --strict-config \
        --approve-for-me -c 'web_search="disabled"' "$LIVE_TASK" \
        > "$codex_stdout" 2>> "$codex_stderr"
    run_exit=$?
  else
    : > "$codex_stdout"
    run_exit=$login_exit
  fi
  set -e
  jq -R -c '{event:"foreign-agent-log",stream:"stdout",line:.}' "$codex_stdout" >> "$trace"
  jq -R -c '{event:"foreign-agent-log",stream:"stderr",line:.}' "$codex_stderr" >> "$trace"

  changed_paths="$RUNNER_TEMP/jern-codex-${run_id}.paths"
  changed_paths_nul="$RUNNER_TEMP/jern-codex-${run_id}.paths0"
  { git diff --name-only -z "$GITHUB_SHA" --; git ls-files -z --others --exclude-standard -- ':!.jern'; } > "$changed_paths_nul"
  : > "$changed_paths"
  policy_json="$(jq -c '.policy // .' "$BASELINE_FILE")" || fail "the protected baseline is not valid JSON."
  : > "$RUNNER_TEMP/jern-codex-${run_id}.violations"
  if [ "$(git rev-parse HEAD)" != "$GITHUB_SHA" ]; then
    printf '%s\n' 'agent_modified_git_history' >> "$RUNNER_TEMP/jern-codex-${run_id}.violations"
  fi
  while IFS= read -r -d '' path; do
    [ -z "$path" ] && continue
    printf '%q\n' "$path" >> "$changed_paths"
    allowed=false
    while IFS= read -r prefix; do
      prefix="${prefix#./}"
      if [ "$prefix" = "." ] || [ "$path" = "$prefix" ] || { [[ "$prefix" = */ ]] && [[ "$path" = "$prefix"* ]]; }; then
        allowed=true
        break
      fi
    done < <(jq -r '.edits_within[]?' <<< "$policy_json")
    if [[ "$path" == *[$'\n\r\t']* ]] || [[ "$path" = .github/* ]] || [ -L "$path" ] || [ "$allowed" != "true" ]; then
      printf '%q\n' "$path" >> "$RUNNER_TEMP/jern-codex-${run_id}.violations"
    fi
  done < "$changed_paths_nul"
  if [ -s "$RUNNER_TEMP/jern-codex-${run_id}.violations" ]; then
    run_exit=1
    jq -Rn -c '[inputs] | {event:"supervision-check",status:"denied",reason:"edits_outside_protected_paths",paths:.}' < "$RUNNER_TEMP/jern-codex-${run_id}.violations" >> "$trace"
  elif [ "$run_exit" -eq 0 ]; then
    tests_failed=0
    while IFS= read -r test_command; do
      [ -z "$test_command" ] && continue
      set +e
      env -u OPENAI_API_KEY -u GH_TOKEN -u GITHUB_TOKEN -u ACTIONS_ID_TOKEN_REQUEST_URL -u ACTIONS_ID_TOKEN_REQUEST_TOKEN \
        -u ACTIONS_RUNTIME_TOKEN -u ACTIONS_CACHE_URL -u ACTIONS_RESULTS_URL -u ACTIONS_RUNTIME_URL \
        bash -lc "$test_command" >> "$codex_stderr" 2>&1
      test_exit=$?
      set -e
      if [ "$test_exit" -ne 0 ]; then tests_failed=1; fi
    done < <(jq -r '.shell_allow[]?' <<< "$policy_json")
    if [ "$tests_failed" -ne 0 ]; then
      run_exit=1
      jq -cn '{event:"supervision-check",status:"denied",reason:"protected_tests_failed"}' >> "$trace"
    else
      jq -cn --argjson paths "$(jq -Rn '[inputs]' < "$changed_paths")" '{event:"supervision-check",status:"passed",paths:$paths}' >> "$trace"
      if [ -s "$changed_paths_nul" ]; then
        xargs -0 git add -A -- < "$changed_paths_nul"
        git -c user.name=jern -c user.email=jern@localhost commit -m "jern: supervised codex task" >/dev/null
      fi
    fi
  fi
  if [ "$run_exit" -eq 124 ]; then
    jq -cn '{event:"supervision-check",status:"denied",reason:"agent_timeout"}' >> "$trace"
  fi
  if [ "$run_exit" -eq 0 ]; then
    jq -cn --arg ts "$(date -u +%Y-%m-%dT%H:%M:%S.%NZ)" '{ts:$ts,event:"run-finished",status:"ok",duration_ms:0}' >> "$trace"
  else
    jq -cn --arg ts "$(date -u +%Y-%m-%dT%H:%M:%S.%NZ)" --arg reason "supervised agent failed with exit code $run_exit" '{ts:$ts,event:"run-finished",status:"error",reason:$reason,duration_ms:0}' >> "$trace"
  fi
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
if [ "${LIVE_AGENT:-jern-native}" = "jern-native" ]; then
  receipt_cap="$(jq -er '.cloud_token_cap | select(type == "number" and . > 0 and floor == .)' "$receipt_file")" \
    || fail "The live receipt did not record its cloud token cap."
  if [ "$receipt_cap" -ne "$token_cap" ]; then
    fail "The live receipt token cap does not match its authorization."
  fi
fi
receipt_digest="$(sha256sum "$receipt_file" | cut -d ' ' -f 1)"
outcome="$(jq -er 'if .complete then (if .status == "ok" then "success" elif .status == "interrupted" then "interrupted" else "failure" end) else "abandoned" end' "$receipt_file")" \
  || fail "Jern returned an invalid live receipt outcome for ${run_id}."
input_tokens="$(jq -er '.input_tokens | select(type == "number" and . >= 0 and floor == .)' "$receipt_file")" \
  || fail "Jern returned invalid live input-token usage for ${run_id}."
output_tokens="$(jq -er '.output_tokens | select(type == "number" and . >= 0 and floor == .)' "$receipt_file")" \
  || fail "Jern returned invalid live output-token usage for ${run_id}."
sha256sum "$trace" >> "$TRACE_BASELINE"
echo "outcome=$outcome" >> "$GITHUB_OUTPUT"
echo "pull_request_url=" >> "$GITHUB_OUTPUT"

branch=""
pull_request_url=""
failure_reason=""
if [ "$run_exit" -eq 0 ]; then
  task_status="succeeded"
else
  task_status="failed"
  if [ "${LIVE_AGENT:-jern-native}" = "codex" ]; then failure_reason="supervised_agent_failed"; else failure_reason="agent_failed"; fi
fi

report_completion() {
  completion="$(jq -cn \
    --arg outcome "$outcome" \
    --argjson input_tokens "$input_tokens" \
    --argjson output_tokens "$output_tokens" \
    --argjson usage_reported "$([ "${LIVE_AGENT:-jern-native}" = "jern-native" ] && echo true || echo false)" \
    --arg receipt_digest "$receipt_digest" \
    --arg task_id "${LIVE_TASK_ID:-}" \
    --arg task_status "$task_status" \
    --arg branch "$branch" \
    --arg pull_request_url "$pull_request_url" \
    --arg failure_reason "$failure_reason" \
    '{outcome:$outcome,input_tokens:$input_tokens,output_tokens:$output_tokens,usage_reported:$usage_reported,receipt_digest:$receipt_digest}
     + if $task_id == "" then {} else {
         task_status:$task_status,
         branch:(if $branch == "" then null else $branch end),
         pull_request_url:(if $pull_request_url == "" then null else $pull_request_url end),
         failure_reason:(if $failure_reason == "" then null else $failure_reason end)
       } end')"
  curl --fail --silent --show-error \
    -X POST "${audience}/v1/runs/${run_id}/complete" \
    -H 'Content-Type: application/json' \
    -H "Authorization: Bearer $run_token" \
    --data "$completion" >/dev/null \
    || fail "Jern Cloud live completion failed for ${run_id}."
}

if [ "$run_exit" -eq 0 ] && [ "${LIVE_DELIVERY:-none}" = "pull-request" ]; then
  if git diff --quiet "$GITHUB_SHA" HEAD --; then
    task_status="no_change"
    echo "::notice::Jern completed without repository changes; no pull request was opened."
  else
    branch="jern/run-${GITHUB_RUN_ID}-${GITHUB_RUN_ATTEMPT}"
    title_source="${issue_title:-$LIVE_TASK}"
    title="$(printf '%s' "$title_source" | tr '\r\n' '  ' | cut -c1-68)"
    body_file="${RUNNER_TEMP}/jern-live-pr-${run_id}.md"
    {
      if [ "${LIVE_AGENT:-jern-native}" = "jern-native" ]; then echo "## Governed Jern task"; else echo "## Supervised Codex task"; fi
      echo
      printf '%s\n' "$LIVE_TASK"
      echo
      echo "- Jern Cloud run: \`$run_id\`"
      echo "- GitHub Actions run: https://github.com/${GITHUB_REPOSITORY}/actions/runs/${GITHUB_RUN_ID}"
      echo "- Receipt outcome: \`$outcome\`"
      if [ -n "${LIVE_ISSUE:-}" ]; then
        echo "- Source issue: #${LIVE_ISSUE}"
        echo
        echo "Closes #${LIVE_ISSUE}"
      fi
      echo
      if [ "${LIVE_AGENT:-jern-native}" = "jern-native" ]; then
        echo "Each file change was committed by Jern under the repository's protected policy. Review and merge this branch through the repository's normal controls."
      else
        echo "Codex ran with workspace-write sandboxing and no GitHub credentials. Jern validated changed paths and tests after execution, then published this branch. This supervised mode does not enforce Codex tool calls or model-token usage."
      fi
    } > "$body_file"
    git switch -c "$branch"
    gh auth setup-git
    git push origin "HEAD:refs/heads/$branch"
    if ! pull_request_url="$(gh pr create \
      --repo "$GITHUB_REPOSITORY" \
      --base "$DEFAULT_BRANCH" \
      --head "$branch" \
      --title "Jern: $title" \
      --body-file "$body_file")"; then
      git push origin --delete "$branch" >/dev/null 2>&1 \
        || echo "::warning::Pull request creation failed and branch cleanup also failed: $branch"
      task_status="failed"
      failure_reason="pull_request_failed"
      report_completion
      fail "GitHub rejected pull request creation; the delivery branch was removed."
    fi
    task_status="pr_ready"
    echo "pull_request_url=$pull_request_url" >> "$GITHUB_OUTPUT"
    echo "::notice::Opened governed pull request $pull_request_url"
  fi
fi

report_completion
echo "::notice::Completed live task as Jern Cloud run $run_id ($outcome)."
exit "$run_exit"
