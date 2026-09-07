#!/usr/bin/env bash
# Builds jern-symbols: the tree-sitter core, eight grammars, and main.c into
# one static binary in out/. Run fetch.sh first. JERN_SYMBOLS_ARCH=x86_64
# cross-compiles on an arm64 Mac; on Windows (Git Bash) clang is used.
set -euo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
vendor="$here/vendor"
out="$here/out"
[ -d "$vendor/tree-sitter" ] || { echo "run fetch.sh first" >&2; exit 1; }
mkdir -p "$out/obj"
exe="jern-symbols"
compiler="${CC:-cc}"
flags=(-O2 -std=c11 -DNDEBUG)
case "$(uname -s)" in
  MINGW*|MSYS*|CYGWIN*|Windows_NT)
    compiler="${CC:-clang}"
    exe="jern-symbols.exe"
    flags+=(-D_CRT_SECURE_NO_WARNINGS)
    ;;
  Darwin)
    if [ -n "${JERN_SYMBOLS_ARCH:-}" ]; then flags+=(-arch "$JERN_SYMBOLS_ARCH"); fi
    ;;
  *)
    # glibc hides fdopen and the endian macros under plain -std=c11; these
    # are the feature-test macros tree-sitter's own Makefile sets.
    flags+=(-D_DEFAULT_SOURCE -D_POSIX_C_SOURCE=200112L)
    ;;
esac
objects=()
compile() {
  local name="$1"; shift
  "$compiler" "${flags[@]}" -c "$@" -o "$out/obj/$name.o"
  objects+=("$out/obj/$name.o")
}
compile core -I "$vendor/tree-sitter/lib/include" -I "$vendor/tree-sitter/lib/src" "$vendor/tree-sitter/lib/src/lib.c"
for grammar in python javascript c-sharp java go rust; do
  src="$vendor/tree-sitter-$grammar/src"
  compile "$grammar-parser" -I "$src" "$src/parser.c"
  if [ -f "$src/scanner.c" ]; then compile "$grammar-scanner" -I "$src" "$src/scanner.c"; fi
done
for dialect in typescript tsx; do
  src="$vendor/tree-sitter-typescript/$dialect/src"
  compile "$dialect-parser" -I "$src" "$src/parser.c"
  compile "$dialect-scanner" -I "$src" "$src/scanner.c"
done
compile main -I "$vendor/tree-sitter/lib/include" "$here/main.c"
"$compiler" "${flags[@]}" "${objects[@]}" -o "$out/$exe"
echo "built $out/$exe"
