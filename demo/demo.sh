#!/bin/bash
# The iron demo: use → read → edit → test.
#
# The thesis (docs/implementation-plan.md §8): every serious coding agent has
# an opaque loop configured by prompts and config files. iron's loop is a
# program — you can read it, change it, and unit-test it deterministically.
# Acts 2–4 need no API key: fixture replay is network-free.
#
#   ./demo/demo.sh              paced (press enter between steps)
#   DEMO_FAST=1 ./demo/demo.sh  no pauses (CI / rehearsal)
#
# The demo builds iron, creates a scratch workspace with a small project and
# a failing test, and walks through the four acts. The workspace path is
# printed so you can poke around afterwards.

set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
IRON_DIST="$ROOT/dist"
IRON="$IRON_DIST/iron"

bold=$(tput bold 2>/dev/null || true); dim=$(tput dim 2>/dev/null || true)
reset=$(tput sgr0 2>/dev/null || true)

banner() { echo; echo "${bold}════ $1 ════${reset}"; echo; }
say()    { echo "${dim}# $1${reset}"; }
run()    { echo "${bold}\$ $*${reset}"; "$@"; }
pause()  { [ -n "${DEMO_FAST:-}" ] || { echo; read -r -p "${dim}[enter]${reset} "; }; }

HAVE_KEY=""
[ -n "${ANTHROPIC_API_KEY:-}" ] && HAVE_KEY=1

banner "setup"
say "building iron (once)…"
dotnet publish "$ROOT/src/Iron.Cli" -c Release -o "$IRON_DIST" -v q --nologo >/dev/null

WS="$(mktemp -d "${TMPDIR:-/tmp}/iron-demo-XXXXXX")"
cd "$WS"
say "scratch workspace: $WS"

cat > wordcount.sh <<'SH'
#!/bin/sh
# Print the number of unique words in the file given as $1.
tr ' ' '\n' < "$1" | sort | wc -l | tr -d ' '
SH

cat > test.sh <<'SH'
#!/bin/sh
printf 'the cat and the hat\n' > .test-input
actual=$(sh wordcount.sh .test-input)
rm -f .test-input
if [ "$actual" = "4" ]; then
    echo "PASS: 4 unique words"
else
    echo "FAIL: expected 4 unique words, got $actual"
    exit 1
fi
SH

say "a tiny project with a bug — the test fails:"
run sh test.sh || true
pause

# ────────────────────────────────────────────────────────────────────────────
banner "act 1 — use it (like any coding agent)"
if [ -n "$HAVE_KEY" ]; then
    say "iron fixes the bug; the trace in .iron/ records every effect."
    run "$IRON" run --yes "test.sh fails. Find the bug in wordcount.sh and fix it."
    echo
    run sh test.sh
    echo
    say "the audit trail — one JSONL event per effect, policy decisions included:"
    grep -o '"event":"[a-z-]*"' .iron/trace-*.jsonl | sort | uniq -c
else
    say "(skipped: ANTHROPIC_API_KEY not set — everything from here on is the"
    say " part no other agent can do, and none of it needs a key)"
fi
pause

# ────────────────────────────────────────────────────────────────────────────
banner "act 2 — read the brain"
say "eject the agent: its whole brain is Kernel source in your workspace."
run "$IRON" eject
echo
run wc -l agents/default/src/main.ikr
echo
say "this is not a config file; it is the loop itself:"
sed -n '/One turn: ask the model/,/show-progress response/p' agents/default/src/main.ikr
pause

# ────────────────────────────────────────────────────────────────────────────
banner "act 3 — the agent has unit tests (no other agent does)"
say "the agent ships a test suite; the LLM is replayed from recorded"
say "fixtures — deterministic, offline, no key:"
run "$IRON" test agents/default
pause

# ────────────────────────────────────────────────────────────────────────────
banner "act 4 — edit the brain; the tests catch the change"
say "change the loop: run the project's tests after every edit. Here's the diff:"
diff -u agents/default/src/main.ikr "$ROOT/demo/main-autotest.ikr" || true
cp "$ROOT/demo/main-autotest.ikr" agents/default/src/main.ikr
echo
say "no recompile — but rerun the agent's tests and the change is caught,"
say "because the recorded conversation no longer matches:"
if run "$IRON" test agents/default; then
    echo "unexpected: tests should have caught the behavior change"; exit 1
fi
pause

if [ -n "$HAVE_KEY" ]; then
    say "bless the deliberate change by re-recording against the live model:"
    run "$IRON" test agents/default --record
    echo
    say "…and use the edited brain: re-break the project, watch the loop run"
    say "the tests by itself after its edit:"
    sed -i '' -e 's/sort -u/sort/' wordcount.sh 2>/dev/null || sed -i -e 's/sort -u/sort/' wordcount.sh
    run "$IRON" run --yes --agent agents/default "test.sh fails. Find the bug in wordcount.sh and fix it."
    echo
    run sh test.sh
fi

banner "the loop nobody else has"
echo "  use it     → iron run          (parity with aider / Claude Code)"
echo "  read it    → iron eject        (the brain is ~100 lines of source)"
echo "  edit it    → \$EDITOR main.ikr  (no fork, no rebuild)"
echo "  test it    → iron test         (deterministic replay; regressions caught)"
echo
echo "workspace kept at: $WS"
