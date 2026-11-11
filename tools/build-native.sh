#!/usr/bin/env bash
# Build MIT m17voice + xaramonitor cdylibs and copy them into Host/native/ for the current OS.
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
out="$root/Host/native"
mkdir -p "$out"

need_rust() {
  if ! command -v cargo >/dev/null 2>&1; then
    echo "cargo not found — install Rust (https://rustup.rs) to build native libraries." >&2
    exit 1
  fi
}

need_rust

echo "Building m17voice…"
cargo build --release --manifest-path "$root/native/m17voice/Cargo.toml"
echo "Building xaramonitor…"
cargo build --release --manifest-path "$root/native/xaramonitor/Cargo.toml"

copy_lib() {
  local crate="$1"
  local dir="$root/native/$crate/target/release"
  if [[ "${OS:-}" == "Windows_NT" ]] || [[ "$(uname -s)" == MINGW* ]] || [[ "$(uname -s)" == MSYS* ]] || [[ "$(uname -s)" == CYGWIN* ]]; then
    cp -f "$dir/${crate}.dll" "$out/${crate}.dll"
    echo "  → Host/native/${crate}.dll"
  elif [[ "$(uname -s)" == Darwin ]]; then
    cp -f "$dir/lib${crate}.dylib" "$out/lib${crate}.dylib"
    echo "  → Host/native/lib${crate}.dylib"
  else
    cp -f "$dir/lib${crate}.so" "$out/lib${crate}.so"
    echo "  → Host/native/lib${crate}.so"
  fi
}

copy_lib m17voice
copy_lib xaramonitor
echo "Native libraries ready under Host/native/"
