#!/usr/bin/env bash
# Fetches the pinned tree-sitter core and grammars into vendor/ (not checked
# in) as shallow clones of each tag, verifying the commit each tag resolves
# to against sources.txt, and refreshes the checked-in tags queries. A clone
# goes through git's own transport; GitHub's generated tarballs time out on
# the larger grammars. Network once; everything after is offline.
set -euo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
vendor="$here/vendor"
mkdir -p "$vendor"
while read -r name tag commit; do
  # A Windows checkout may hand sources.txt over with CRLF endings; the
  # commit must not carry the carriage return into the comparison.
  commit="${commit%$'\r'}"
  case "$name" in ''|'#'*) continue ;; esac
  if [ -f "$vendor/$name/.fetched" ] && [ "$(cat "$vendor/$name/.fetched")" = "$tag $commit" ]; then
    continue
  fi
  rm -rf "$vendor/$name"
  attempt=1
  until git -c advice.detachedHead=false clone --quiet --depth 1 --branch "$tag" "https://github.com/tree-sitter/$name" "$vendor/$name"; do
    if [ "$attempt" -ge 5 ]; then echo "$name $tag: clone failed after $attempt attempts" >&2; exit 1; fi
    attempt=$((attempt + 1))
    rm -rf "$vendor/$name"
    sleep $((attempt * 5))
  done
  actual="$(git -C "$vendor/$name" rev-parse HEAD)"
  [ "$actual" = "$commit" ] || { echo "$name $tag: commit $actual, expected $commit" >&2; exit 1; }
  rm -rf "$vendor/$name/.git"
  printf '%s %s\n' "$tag" "$commit" > "$vendor/$name/.fetched"
  echo "fetched $name $tag"
done < "$here/sources.txt"
# The tags queries ship beside the binary; keep the checked-in copies current.
for grammar in python javascript c-sharp java go rust; do
  cp "$vendor/tree-sitter-$grammar/queries/tags.scm" "$here/queries/$grammar.scm"
done
# The typescript grammar's own tags cover only signatures; the tree-sitter
# CLI combines them with javascript's, and so do we.
{
  echo "; The typescript grammar's own tags cover only signatures; the javascript"
  echo "; tags apply to the shared node types and come first."
  cat "$vendor/tree-sitter-javascript/queries/tags.scm"
  echo
  cat "$vendor/tree-sitter-typescript/queries/tags.scm"
} > "$here/queries/typescript.scm"
