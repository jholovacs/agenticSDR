#!/usr/bin/env python3
"""Catch QuietExec quoting footguns in Package.wxs before an MSI is published.

MSI directory properties include a trailing backslash; quoting them as "C:\\Dir\\"
escapes the closing quote and produces paths like C:\\Program Files\\AgenticSDR\"\\native.
"""
from __future__ import annotations

import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
WXS = ROOT / "packaging" / "windows" / "Package.wxs"


def main() -> int:
    text = WXS.read_text(encoding="utf-8")
    fail = 0

    # Safe form: --home "[INSTALLFOLDER]\\"
    safe = "--home &quot;[INSTALLFOLDER]" + ("\\" * 2) + "&quot;"
    if safe not in text:
        print(
            'error: ApplyFetch --home must use "[INSTALLFOLDER]\\\\" '
            "so the trailing directory slash does not escape the quote.",
            file=sys.stderr,
        )
        for i, line in enumerate(text.splitlines(), 1):
            if "--home" in line:
                print(f"{i}:{line}", file=sys.stderr)
        fail = 1

    bare_dir = re.compile(r"&quot;\[INSTALLFOLDER\]&quot;")
    for i, line in enumerate(text.splitlines(), 1):
        if bare_dir.search(line):
            print(
                f'error:{i}: "[INSTALLFOLDER]" is unsafe — use "[INSTALLFOLDER]\\\\" instead:\n{line}',
                file=sys.stderr,
            )
            fail = 1

    if fail:
        return 1
    print("MSI QuietExec quoting checks passed.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
