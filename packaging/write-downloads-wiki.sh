#!/usr/bin/env bash
# Build Downloads.md (and a short Home.md) for the GitHub wiki from a dist/ folder of packages.
# Usage: VERSION=0.1.17 COMMIT=abc123 bash packaging/write-downloads-wiki.sh dist out-wiki
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
# shellcheck source=version.sh
source "$root/packaging/version.sh"
version="$(version_default)"
commit="${COMMIT:-unknown}"
repo_url="${REPO_URL:-https://github.com/jholovacs/agenticSDR}"
dist="${1:-dist}"
out="${2:-wiki-out}"
mkdir -p "$out" "$dist"

shopt -s nullglob
mapfile -t files < <(find "$dist" -type f \( -name '*.deb' -o -name '*.msi' -o -name '*.zip' \) | sort)
if [ "${#files[@]}" -eq 0 ]; then
  echo "No packages under $dist" >&2
  exit 1
fi

sums="$dist/SHA256SUMS.txt"
: > "$sums"
(
  cd "$dist"
  find . -type f \( -name '*.deb' -o -name '*.msi' -o -name '*.zip' \) -printf '%P\n' | sort | while read -r rel; do
    # Normalize "hash *file" (binary mode) → "hash  file" for portable SHA256SUMS.
    sha256sum "$rel" | sed -E 's/ \*/  /' >> SHA256SUMS.txt
  done
)

rel_url="${repo_url}/releases/tag/v${version}"
asset_base="${repo_url}/releases/download/v${version}"

deb="agenticsdr_${version}_amd64.deb"
msi="AgenticSdr-${version}.msi"
mac_arm="agenticsdr-${version}-osx-arm64.zip"
mac_x64="agenticsdr-${version}-osx-x64.zip"

hash_for() {
  local name="$1"
  # GNU sha256sum may print "hash  file" or "hash *file" (binary mode).
  awk -v f="$name" '{
    file = $2
    sub(/^\*/, "", file)
    if (file == f) { print $1; exit }
  }' "$sums"
}

row() {
  local label="$1" name="$2" note="$3"
  local digest
  digest="$(hash_for "$name" || true)"
  if [ -z "$digest" ]; then
    printf '| %s | _(not in this build)_ | | %s |\n' "$label" "$note"
    return
  fi
  printf '| %s | [%s](%s/%s) | `%s` | %s |\n' \
    "$label" "$name" "$asset_base" "$name" "$digest" "$note"
}

cat > "$out/Downloads.md" <<EOF
# Downloads

**Latest release: [${version}](${rel_url})** (commit \`${commit}\`)

Installers are built automatically on every merge to \`main\`. Version scheme: \`0.1.<GitHub Actions run number>\`.

## Packages

| Platform | File | SHA-256 | Notes |
| --- | --- | --- | --- |
$(row "Debian / Ubuntu" "$deb" "\`sudo apt install ./…\` — asks for OpenAI endpoint; needs \`librtlsdr0\`")
$(row "Windows x64" "$msi" "Installs the **AgenticSdr** service; Start Menu opens the page")
$(row "macOS Apple Silicon" "$mac_arm" "Unzip, run \`./AgenticSdr.Host\`; \`brew install librtlsdr\`")
$(row "macOS Intel" "$mac_x64" "Unzip, run \`./AgenticSdr.Host\`; \`brew install librtlsdr\`")

Full checksum file: [SHA256SUMS.txt](${asset_base}/SHA256SUMS.txt)

Verify after download:

\`\`\`bash
sha256sum -c SHA256SUMS.txt
# Windows (PowerShell):
# Get-FileHash .\\${msi} -Algorithm SHA256
\`\`\`

## Quick start

1. Download the package for your OS from the table above (or the [release page](${rel_url})).
2. Install or unzip it.
3. Open http://127.0.0.1:5088
4. **Start radio** → **Known channels** → **NOAA weather** to confirm audio.

New to SDRs? Read the [beginner’s guide](${repo_url}/blob/main/GUIDE.md) in the repo.

## All releases

[Release list](${repo_url}/releases) — every merge publishes a new \`v0.1.N\` tag with the same four packages plus \`SHA256SUMS.txt\`.

## Build from source

See the [README](${repo_url}/blob/main/README.md#build-from-source) if you prefer to compile locally instead of using these installers.
EOF

cat > "$out/Home.md" <<EOF
# Agentic SDR

Receive-only amateur radio listener for USB SDRs — local web UI at http://127.0.0.1:5088, plus MCP tools for Cursor and friends.

**→ [Downloads](Downloads)** — latest installers and SHA-256 checksums

**→ [Beginner’s guide](${repo_url}/blob/main/GUIDE.md)** — how to scan, listen, camp, and what the modes mean

**→ [README](${repo_url}/blob/main/README.md)** — features, config, drivers, MCP tools

Latest packaged build: **[${version}](${rel_url})**
EOF

echo "Wrote wiki pages under $out (version $version)"
