#!/usr/bin/env bash
# Downloads the Windows x64 librtlsdr build used by the host. The DLL is GPL and is not committed.
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
mkdir -p "$root/native"
tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT
curl -fsSL -o "$tmp/Release.zip" -L "https://github.com/rtlsdrblog/rtl-sdr-blog/releases/download/V1.4.0/Release.zip"
unzip -o -j "$tmp/Release.zip" "x64/rtlsdr.dll" "x64/pthreadVC2.dll" "x64/msvcr100.dll" -d "$root/native"
echo "Wrote rtlsdr.dll to $root/native"
