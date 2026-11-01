#!/usr/bin/env bash
# Publishes the Windows app and builds AgenticSdr.msi.
# The MSI asks for the OpenAI endpoint, model, and key, then downloads librtlsdr and the speech model.
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
stage="$root/packaging/stage/win-x64"
mkdir -p "$stage" "$root/packaging/out"
dotnet publish "$root/Host/AgenticSdr.Host.csproj" -c Release -r win-x64 --self-contained true -o "$stage"
mkdir -p "$stage/native"
if [ -f "$root/native/limesuiteng.dll" ] && [ -f "$root/native/limesuiteng-legacyapi.dll" ]; then
  cp -f "$root/native/limesuiteng.dll" "$root/native/limesuiteng-legacyapi.dll" "$stage/native/"
fi
dotnet build "$root/packaging/windows/AgenticSdr.wixproj" -c Release
cp -f "$root/packaging/windows/bin/Release/AgenticSdr.msi" "$root/packaging/out/AgenticSdr.msi"
echo "Wrote $root/packaging/out/AgenticSdr.msi"
