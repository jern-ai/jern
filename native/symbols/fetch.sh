#!/usr/bin/env bash
# Fetches the pinned tree-sitter core and grammars into vendor/ (not checked
# in), verifying each tarball's sha256 from sources.txt, and refreshes the
# checked-in tags queries. Network once; everything after is offline.
set -euo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
vendor="$here/vendor"
mkdir -p "$vendor"
sha() { if command -v sha256sum >/dev/null 2>&1; then sha256sum "$1" | cut -d' ' -f1; else shasum -a 256 "$1" | cut -d' ' -f1; fi; }
while read -r name tag expected; do
  case "$name" in ''|'#'*) continue ;; esac
  if [ -f "$vendor/$name/.fetched" ] && [ "$(cat "$vendor/$name/.fetched")" = "$tag $expected" ]; then
    continue
  fi
  archive="$vendor/$name-$tag.tar.gz"
  curl -fsSL --retry 6 --retry-delay 5 --retry-all-errors -o "$archive" "https://github.com/tree-sitter/$name/archive/refs/tags/$tag.tar.gz"
  actual="$(sha "$archive")"
  [ "$actual" = "$expected" ] || { echo "$name $tag: sha256 $actual, expected $expected" >&2; exit 1; }
  rm -rf "$vendor/$name"
  mkdir -p "$vendor/$name"
  tar -xzf "$archive" -C "$vendor/$name" --strip-components=1
  rm -f "$archive"
  printf '%s %s\n' "$tag" "$expected" > "$vendor/$name/.fetched"
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
