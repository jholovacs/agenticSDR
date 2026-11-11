#!/usr/bin/env bash
# Publishes the Windows app and builds AgenticSdr-<version>.msi.
# The MSI installs AgenticSdr.Host.exe as the AgenticSdr Windows service, asks for the
# OpenAI endpoint/model/key (saved under ProgramData), ships librtlsdr from native/, and
# downloads the speech model if needed.
# Same UpgradeCode every build: installing a newer (or same) MSI upgrades in place — service
# stops/restarts, Program Files is replaced, ProgramData llm.json is kept.
# VERSION defaults to 0.1.0 locally; CI sets 0.1.<run_number>.
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
# shellcheck source=version.sh
source "$root/packaging/version.sh"
version="$(version_default)"
wix_version="$(version_four_part "$version")"
stage="$root/packaging/stage/win-x64"
msi_name="AgenticSdr-${version}.msi"
mkdir -p "$stage" "$root/packaging/out"
dotnet publish "$root/Host/AgenticSdr.Host.csproj" -c Release -r win-x64 --self-contained true -o "$stage"
mkdir -p "$stage/native"
# GPL librtlsdr (tracked under native/) — next to the host and under native/ for the loader.
for f in rtlsdr.dll pthreadVC2.dll msvcr100.dll; do
  if [ ! -f "$root/native/$f" ]; then
    echo "missing native/$f — run bash tools/fetch-native.sh" >&2
    exit 1
  fi
  cp -f "$root/native/$f" "$stage/$f"
  cp -f "$root/native/$f" "$stage/native/$f"
done
if [ -f "$root/native/limesuiteng.dll" ] && [ -f "$root/native/limesuiteng-legacyapi.dll" ]; then
  cp -f "$root/native/limesuiteng.dll" "$root/native/limesuiteng-legacyapi.dll" "$stage/native/"
fi
dotnet build "$root/packaging/windows/AgenticSdr.wixproj" -c Release -p:ProductVersion="$wix_version"
cp -f "$root/packaging/windows/bin/Release/AgenticSdr.msi" "$root/packaging/out/$msi_name"
# Stable name for scripts that still expect AgenticSdr.msi
cp -f "$root/packaging/out/$msi_name" "$root/packaging/out/AgenticSdr.msi"
echo "Wrote $root/packaging/out/$msi_name (Windows version $wix_version)"
