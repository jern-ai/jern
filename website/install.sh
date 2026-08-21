#!/bin/sh
# jern installer — https://jern.ai
# Detects OS/arch, downloads the latest release, installs to ~/.jern/bin.
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

url="https://github.com/jern-ai/jern/releases/latest/download/jern-$rid.tar.gz"
dir="${JERN_INSTALL:-$HOME/.jern}"
bin="$dir/bin"

echo "installing jern ($rid) to $bin"
mkdir -p "$bin"
tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT
curl -fsSL "$url" -o "$tmp/jern.tar.gz"
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
