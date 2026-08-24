#!/bin/sh
# jern installer — https://jern.ai
# Detects OS/arch, downloads a release, installs to ~/.jern/bin.
#
#   JERN_VERSION=0.13.0   pin an exact release (default: latest)
#   JERN_INSTALL=~/.jern  install prefix
#   JERN_REQUIRE_SUMS=1   fail (rather than warn) if SHA256SUMS is missing —
#                         unattended installs should never skip verification
set -eu

case "$(uname -s)" in
  Darwin) os="osx" ;;
  Linux)  os="linux" ;;
  *) echo "unsupported OS: $(uname -s) — grab a build from https://github.com/jern-ai/jern/releases" >&2; exit 1 ;;
esac
case "$(uname -m)" in
  arm64|aarch64) arch="arm64" ;;
  x86_64|amd64)  arch="x64" ;;
  *) echo "unsupported architecture: $(uname -m)" >&2; exit 1 ;;
esac
rid="$os-$arch"

version="${JERN_VERSION:-latest}"
if [ "$version" = "latest" ]; then
  base="https://github.com/jern-ai/jern/releases/latest/download"
else
  # Accept both "0.13.0" and "v0.13.0".
  base="https://github.com/jern-ai/jern/releases/download/v${version#v}"
fi
url="$base/jern-$rid.tar.gz"
sums_url="$base/SHA256SUMS"
dir="${JERN_INSTALL:-$HOME/.jern}"
bin="$dir/bin"

echo "installing jern $version ($rid) to $bin"
mkdir -p "$bin"
tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT
curl -fsSL "$url" -o "$tmp/jern.tar.gz"

# Verify the download against the release's published checksums. A missing
# checksum file (releases before v0.11) only warns; a mismatch always aborts.
if curl -fsSL "$sums_url" -o "$tmp/SHA256SUMS" 2>/dev/null; then
  expected="$(grep " jern-$rid.tar.gz\$" "$tmp/SHA256SUMS" | cut -d' ' -f1)"
  if command -v sha256sum >/dev/null 2>&1; then
    actual="$(sha256sum "$tmp/jern.tar.gz" | cut -d' ' -f1)"
  else
    actual="$(shasum -a 256 "$tmp/jern.tar.gz" | cut -d' ' -f1)"
  fi
  if [ -z "$expected" ] || [ "$expected" != "$actual" ]; then
    echo "checksum verification FAILED for jern-$rid.tar.gz" >&2
    echo "  expected: ${expected:-<not in SHA256SUMS>}" >&2
    echo "  actual:   $actual" >&2
    exit 1
  fi
  echo "checksum verified"
else
  if [ "${JERN_REQUIRE_SUMS:-0}" = "1" ]; then
    echo "error: no SHA256SUMS published for this release and JERN_REQUIRE_SUMS=1" >&2
    exit 1
  fi
  echo "warning: no SHA256SUMS published for this release; skipping verification" >&2
fi

tar -xzf "$tmp/jern.tar.gz" -C "$tmp"
rm -rf "$bin/current"
mv "$tmp/jern-$rid" "$bin/current"
ln -sf "$bin/current/jern" "$bin/jern"

echo
echo "installed: $("$bin/jern" version 2>/dev/null || echo jern)"
case ":$PATH:" in
  *":$bin:"*) echo "run: jern" ;;
  *)
    echo "add it to your PATH, e.g.:"
    echo "  echo 'export PATH=\"$bin:\$PATH\"' >> ~/.zshrc && source ~/.zshrc"
    ;;
esac
echo
echo "next: set a provider key (export ANTHROPIC_API_KEY=…) and run: jern"
