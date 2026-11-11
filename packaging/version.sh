#!/usr/bin/env bash
# Shared version helpers for packaging scripts.
# CI sets VERSION=0.1.<github.run_number>. Local builds default to 0.1.0.
version_default() {
  printf '%s' "${VERSION:-0.1.0}"
}

# WiX / Windows Installer wants up to four numeric parts (a.b.c.d).
version_four_part() {
  local v="$1"
  local a=0 b=0 c=0 d=0
  IFS=. read -r a b c d _ <<< "${v}.0.0.0"
  a="${a:-0}"
  b="${b:-0}"
  c="${c:-0}"
  d="${d:-0}"
  printf '%s.%s.%s.%s' "$a" "$b" "$c" "$d"
}
