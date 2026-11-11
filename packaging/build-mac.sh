#!/usr/bin/env bash
# Publishes self-contained macOS builds (arm64 + x64) as zip archives.
# Install: unzip, then run ./AgenticSdr.Host and open http://127.0.0.1:5088
# Homebrew librtlsdr is recommended: brew install librtlsdr
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
version="${VERSION:-0.1.0}"
out="$root/packaging/out"
mkdir -p "$out"

build_rid() {
  local rid="$1"
  local stage="$root/packaging/stage/$rid"
  local zip="$out/agenticsdr-${version}-${rid}.zip"
  rm -rf "$stage"
  mkdir -p "$stage"
  dotnet publish "$root/Host/AgenticSdr.Host.csproj" \
    -c Release -r "$rid" --self-contained true -o "$stage"
  # Drop debug symbols from the distribution zip.
  find "$stage" -name '*.pdb' -delete
  rm -f "$zip"
  (
    cd "$root/packaging/stage"
    if command -v zip >/dev/null 2>&1; then
      zip -qr "$zip" "$rid"
    else
      # Windows CI fallback (PowerShell Compress-Archive wants a trailing path carefully).
      powershell.exe -NoProfile -Command \
        "Compress-Archive -Path '$stage' -DestinationPath '$zip' -Force"
    fi
  )
  echo "Wrote $zip"
}

build_rid osx-arm64
build_rid osx-x64
