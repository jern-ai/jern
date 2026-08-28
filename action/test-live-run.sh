#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
temp="$(mktemp -d)"
trap 'rm -rf "$temp"' EXIT
mkdir -p "$temp/bin" "$temp/work" "$temp/runner"

cat > "$temp/bin/curl" <<'EOF'
#!/usr/bin/env bash
set -euo pipefail
printf '%s\n' "$*" >> "$FAKE_CURL_LOG"
url=""
for arg in "$@"; do
  case "$arg" in http://*|https://*) url="$arg";; esac
done
case "$url" in
  https://oidc.example/*)
    printf '%s\n' '{"value":"github-oidc-token"}'
    ;;
  https://cloud.example/v1/runs)
    printf '%s\n' '{"run_id":"run_0123456789abcdef0123456789abcdef","run_token":"secret-run-token","token_budget":123}'
    ;;
  https://cloud.example/v1/runs/run_0123456789abcdef0123456789abcdef/trace)
    ;;
  https://cloud.example/v1/runs/run_0123456789abcdef0123456789abcdef/complete)
    ;;
  *)
    echo "unexpected curl URL: $url" >&2
    exit 1
    ;;
esac
EOF

cat > "$temp/bin/jern" <<'EOF'
#!/usr/bin/env bash
set -euo pipefail
command="$1"
shift
case "$command" in
  --version)
    printf '%s\n' '0.14.5'
    ;;
  run)
    test "$JERN_CLOUD_RUN_ID" = "run_0123456789abcdef0123456789abcdef"
    test "$JERN_CLOUD_TOKEN_CAP" = "123"
    test -z "${GH_TOKEN+x}"
    test -z "${ACTIONS_ID_TOKEN_REQUEST_URL+x}"
    test -z "${ACTIONS_ID_TOKEN_REQUEST_TOKEN+x}"
    printf '%s\n' "$*" > "$FAKE_JERN_ARGS"
    mkdir -p .jern
    status="ok"
    reason=""
    if [ "${FAKE_RUN_EXIT:-0}" != "0" ]; then
      status="error"
      reason=',"reason":"scripted failure"'
    fi
    printf '%s\n' \
      '{"event":"run-started","schema_version":1,"run_id":"run_0123456789abcdef0123456789abcdef","cloud":{"run_id":"run_0123456789abcdef0123456789abcdef","token_cap":123}}' \
      '{"event":"llm-response","response":{"usage":{"input_tokens":40,"output_tokens":2}}}' \
      "{\"event\":\"run-finished\",\"status\":\"$status\"$reason}" \
      > .jern/trace-run_0123456789abcdef0123456789abcdef.jsonl
    if [ "${FAKE_MAKE_COMMIT:-0}" = "1" ]; then
      mkdir -p src
      printf '%s\n' 'governed change' > src/fix.txt
      git add src/fix.txt
      git -c user.name=jern -c user.email=jern@localhost commit -m 'jern: fix the parser' >/dev/null
    fi
    exit "${FAKE_RUN_EXIT:-0}"
    ;;
  receipt)
    status="ok"
    if [ "${FAKE_RUN_EXIT:-0}" != "0" ]; then status="error"; fi
    printf '{"complete":true,"status":"%s","input_tokens":40,"output_tokens":2,"cloud_token_cap":123,"hard_token_budget_denied":false}\n' "$status"
    ;;
  *)
    echo "unexpected jern command: $command" >&2
    exit 1
    ;;
esac
EOF
chmod +x "$temp/bin/curl" "$temp/bin/jern"

cat > "$temp/bin/gh" <<'EOF'
#!/usr/bin/env bash
set -euo pipefail
printf '%s\n' "$*" >> "$FAKE_GH_LOG"
if [ "$1" = "api" ]; then
  printf '%s\n' '{"number":17,"title":"Fix the parser","body":"Preserve escaped delimiters."}'
elif [ "$1" = "auth" ]; then
  exit 0
else
  previous=""
  for argument in "$@"; do
    if [ "$previous" = "--body-file" ]; then cp "$argument" "$FAKE_PR_BODY"; fi
    previous="$argument"
  done
  if [ "${FAKE_GH_FAIL_CREATE:-0}" = "1" ]; then
    echo "scripted pull request rejection" >&2
    exit 1
  fi
  printf '%s\n' 'https://github.com/acme/example/pull/17'
fi
EOF
chmod +x "$temp/bin/gh"

git init --bare "$temp/remote.git" >/dev/null
(
  cd "$temp/work"
  git init >/dev/null
  git checkout -b main >/dev/null
  git -c user.name=test -c user.email=test@example.com commit --allow-empty -m initial >/dev/null
  git remote add origin "$temp/remote.git"
  git push -u origin main >/dev/null
)

touch "$temp/baseline.json" "$temp/trace-baseline.sha256" "$temp/github-output"
export PATH="$temp/bin:$PATH"
export FAKE_CURL_LOG="$temp/curl.log"
export FAKE_GH_LOG="$temp/gh.log"
export FAKE_PR_BODY="$temp/pr-body.md"
export FAKE_JERN_ARGS="$temp/jern-args"
export GITHUB_EVENT_NAME=workflow_dispatch
export GITHUB_REF=refs/heads/main
export DEFAULT_BRANCH=main
export CLOUD_MODE=true
export CLOUD_URL=https://cloud.example
export LIVE_TASK="fix the parser"
export LIVE_TASK_ID=task_0123456789abcdef0123456789abcdef
export LIVE_TOKEN_BUDGET=123
export BASELINE_FILE="$temp/baseline.json"
export POLICY_TRUST="aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
export TRACE_BASELINE="$temp/trace-baseline.sha256"
export JERN_VERSION=0.14.5
export ACTIONS_ID_TOKEN_REQUEST_URL="https://oidc.example/token?request=1"
export ACTIONS_ID_TOKEN_REQUEST_TOKEN=oidc-request-token
export RUNNER_TEMP="$temp/runner"
export GITHUB_OUTPUT="$temp/github-output"
export GITHUB_REPOSITORY=acme/example
export GITHUB_RUN_ID=42
export GITHUB_RUN_ATTEMPT=3
export GITHUB_SHA="$(git -C "$temp/work" rev-parse HEAD)"
export GH_TOKEN=test-github-token
export LIVE_DELIVERY=none

run_success() {
  : > "$FAKE_CURL_LOG"
  : > "$GITHUB_OUTPUT"
  : > "$TRACE_BASELINE"
  rm -rf "$temp/work/.jern"
  (
    cd "$temp/work"
    FAKE_RUN_EXIT=0 bash "$repo_root/action/live-run.sh"
  )
  grep -Fxq 'run_id=run_0123456789abcdef0123456789abcdef' "$GITHUB_OUTPUT"
  grep -Fxq 'outcome=success' "$GITHUB_OUTPUT"
  grep -Fq -- '--policy-baseline' "$FAKE_JERN_ARGS"
  grep -Fq -- "$BASELINE_FILE" "$FAKE_JERN_ARGS"
  grep -Fq -- '--policy-trust' "$FAKE_JERN_ARGS"
  grep -Fq -- 'fix the parser' "$FAKE_JERN_ARGS"
  grep -Fq -- 'https://oidc.example/token?request=1&audience=https%3A%2F%2Fcloud.example' "$FAKE_CURL_LOG"
  grep -Fq -- '--data {"token_budget":123,"task_id":"task_0123456789abcdef0123456789abcdef"}' "$FAKE_CURL_LOG"
  grep -Fq -- '/trace' "$FAKE_CURL_LOG"
  grep -Fq -- '/complete' "$FAKE_CURL_LOG"
  grep -Fq -- '"task_status":"succeeded"' "$FAKE_CURL_LOG"
  test "$(wc -l < "$TRACE_BASELINE")" -eq 1
}

run_failure_uploads_evidence() {
  : > "$FAKE_CURL_LOG"
  : > "$GITHUB_OUTPUT"
  : > "$TRACE_BASELINE"
  rm -rf "$temp/work/.jern"
  set +e
  (
    cd "$temp/work"
    FAKE_RUN_EXIT=1 bash "$repo_root/action/live-run.sh"
  )
  code=$?
  set -e
  test "$code" -eq 1
  grep -Fxq 'outcome=failure' "$GITHUB_OUTPUT"
  grep -Fq -- '/trace' "$FAKE_CURL_LOG"
  grep -Fq -- '/complete' "$FAKE_CURL_LOG"
  grep -Fq -- '"task_status":"failed"' "$FAKE_CURL_LOG"
  grep -Fq -- '"failure_reason":"agent_failed"' "$FAKE_CURL_LOG"
}

run_success_opens_pull_request() {
  : > "$FAKE_CURL_LOG"
  : > "$FAKE_GH_LOG"
  : > "$GITHUB_OUTPUT"
  : > "$TRACE_BASELINE"
  git -C "$temp/work" switch -C main "$GITHUB_SHA" >/dev/null
  rm -rf "$temp/work/.jern" "$temp/work/src"
  (
    cd "$temp/work"
    LIVE_DELIVERY=pull-request FAKE_MAKE_COMMIT=1 FAKE_RUN_EXIT=0 bash "$repo_root/action/live-run.sh"
  )
  test "$(git --git-dir="$temp/remote.git" rev-parse refs/heads/jern/run-42-3^)" = "$GITHUB_SHA"
  grep -Fq -- 'pr create --repo acme/example --base main --head jern/run-42-3' "$FAKE_GH_LOG"
  grep -Fxq 'pull_request_url=https://github.com/acme/example/pull/17' "$GITHUB_OUTPUT"
  grep -Fq -- '"task_status":"pr_ready"' "$FAKE_CURL_LOG"
  grep -Fq -- '"pull_request_url":"https://github.com/acme/example/pull/17"' "$FAKE_CURL_LOG"
}

run_issue_opens_linked_pull_request() {
  : > "$FAKE_CURL_LOG"
  : > "$FAKE_GH_LOG"
  : > "$GITHUB_OUTPUT"
  : > "$TRACE_BASELINE"
  git -C "$temp/work" switch -C main "$GITHUB_SHA" >/dev/null
  rm -rf "$temp/work/.jern" "$temp/work/src"
  (
    cd "$temp/work"
    GITHUB_RUN_ATTEMPT=4 LIVE_TASK= LIVE_ISSUE=17 LIVE_DELIVERY=pull-request \
      FAKE_MAKE_COMMIT=1 FAKE_RUN_EXIT=0 bash "$repo_root/action/live-run.sh"
  )
  test "$(git --git-dir="$temp/remote.git" rev-parse refs/heads/jern/run-42-4^)" = "$GITHUB_SHA"
  grep -Fq 'Resolve GitHub issue #17: Fix the parser' "$FAKE_JERN_ARGS"
  grep -Fq 'Preserve escaped delimiters.' "$FAKE_JERN_ARGS"
  grep -Fq -- '--title Jern: Fix the parser' "$FAKE_GH_LOG"
  grep -Fxq 'Closes #17' "$FAKE_PR_BODY"
}

run_failure_does_not_publish() {
  : > "$FAKE_CURL_LOG"
  : > "$FAKE_GH_LOG"
  : > "$GITHUB_OUTPUT"
  : > "$TRACE_BASELINE"
  git -C "$temp/work" switch -C main "$GITHUB_SHA" >/dev/null
  rm -rf "$temp/work/.jern" "$temp/work/src"
  set +e
  (
    cd "$temp/work"
    GITHUB_RUN_ATTEMPT=5 LIVE_DELIVERY=pull-request FAKE_MAKE_COMMIT=1 \
      FAKE_RUN_EXIT=1 bash "$repo_root/action/live-run.sh"
  )
  code=$?
  set -e
  test "$code" -eq 1
  ! git --git-dir="$temp/remote.git" show-ref --verify --quiet refs/heads/jern/run-42-5
  test ! -s "$FAKE_GH_LOG"
}

run_no_change_does_not_publish() {
  : > "$FAKE_CURL_LOG"
  : > "$FAKE_GH_LOG"
  : > "$GITHUB_OUTPUT"
  : > "$TRACE_BASELINE"
  git -C "$temp/work" switch -C main "$GITHUB_SHA" >/dev/null
  rm -rf "$temp/work/.jern" "$temp/work/src"
  (
    cd "$temp/work"
    GITHUB_RUN_ATTEMPT=6 LIVE_DELIVERY=pull-request FAKE_MAKE_COMMIT=0 \
      FAKE_RUN_EXIT=0 bash "$repo_root/action/live-run.sh"
  )
  ! git --git-dir="$temp/remote.git" show-ref --verify --quiet refs/heads/jern/run-42-6
  test ! -s "$FAKE_GH_LOG"
  grep -Fxq 'pull_request_url=' "$GITHUB_OUTPUT"
  grep -Fq -- '"task_status":"no_change"' "$FAKE_CURL_LOG"
}

run_pull_request_failure_removes_branch() {
  : > "$FAKE_CURL_LOG"
  : > "$FAKE_GH_LOG"
  : > "$GITHUB_OUTPUT"
  : > "$TRACE_BASELINE"
  git -C "$temp/work" switch -C main "$GITHUB_SHA" >/dev/null
  rm -rf "$temp/work/.jern" "$temp/work/src"
  set +e
  (
    cd "$temp/work"
    GITHUB_RUN_ATTEMPT=7 LIVE_DELIVERY=pull-request FAKE_MAKE_COMMIT=1 \
      FAKE_GH_FAIL_CREATE=1 FAKE_RUN_EXIT=0 bash "$repo_root/action/live-run.sh"
  )
  code=$?
  set -e
  test "$code" -eq 1
  ! git --git-dir="$temp/remote.git" show-ref --verify --quiet refs/heads/jern/run-42-7
  grep -Fq -- '"failure_reason":"pull_request_failed"' "$FAKE_CURL_LOG"
}

rejects_unsafe_context() {
  set +e
  GITHUB_EVENT_NAME=push bash "$repo_root/action/live-run.sh" > "$temp/unsafe.out" 2>&1
  event_code=$?
  JERN_VERSION=0.14.4 bash "$repo_root/action/live-run.sh" > "$temp/version.out" 2>&1
  version_code=$?
  set -e
  test "$event_code" -eq 1
  grep -Fq "only from workflow_dispatch" "$temp/unsafe.out"
  test "$version_code" -eq 1
  grep -Fq "requires jern 0.14.5 or newer" "$temp/version.out"

  set +e
  POLICY_TRUST=not-a-digest bash "$repo_root/action/live-run.sh" > "$temp/policy.out" 2>&1
  policy_code=$?
  set -e
  test "$policy_code" -eq 1
  grep -Fq "full lowercase SHA-256 digests" "$temp/policy.out"
}

run_success
run_failure_uploads_evidence
run_success_opens_pull_request
run_issue_opens_linked_pull_request
run_failure_does_not_publish
run_no_change_does_not_publish
run_pull_request_failure_removes_branch
rejects_unsafe_context
printf '%s\n' "live Action contract tests passed"
