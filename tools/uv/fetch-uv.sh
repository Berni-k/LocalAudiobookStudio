#!/usr/bin/env sh
# Downloads the pinned uv release for linux-x64 and win-x64 into tools/uv/<rid>/.
# Usage: sh tools/uv/fetch-uv.sh [version]
# Without a version it fetches the version in tools/uv/VERSION.
set -eu
HERE="$(cd "$(dirname "$0")" && pwd)"
VERSION="${1:-$(head -n 1 "$HERE/VERSION" | tr -d '[:space:]')}"
BASE="https://github.com/astral-sh/uv/releases/download/$VERSION"
TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT

mkdir -p "$HERE/linux-x64" "$HERE/win-x64"
curl -fsSL "$BASE/uv-x86_64-unknown-linux-gnu.tar.gz" -o "$TMP/uv.tar.gz"
tar -xzf "$TMP/uv.tar.gz" -C "$TMP"
cp "$TMP/uv-x86_64-unknown-linux-gnu/uv" "$HERE/linux-x64/uv"
chmod +x "$HERE/linux-x64/uv"

curl -fsSL "$BASE/uv-x86_64-pc-windows-msvc.zip" -o "$TMP/uv.zip"
(cd "$TMP" && unzip -o -q uv.zip uv.exe)
cp "$TMP/uv.exe" "$HERE/win-x64/uv.exe"

echo "$VERSION" > "$HERE/VERSION"
echo "uv $VERSION downloaded."
