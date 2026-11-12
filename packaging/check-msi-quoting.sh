#!/usr/bin/env bash
# Wrapper so CI can keep calling bash packaging/check-msi-quoting.sh
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
cd "$root"
if command -v python >/dev/null 2>&1; then
  exec python packaging/check-msi-quoting.py
elif command -v python3 >/dev/null 2>&1; then
  exec python3 packaging/check-msi-quoting.py
elif command -v py >/dev/null 2>&1; then
  exec py -3 packaging/check-msi-quoting.py
fi
echo "error: python/python3/py not found on PATH" >&2
exit 1
