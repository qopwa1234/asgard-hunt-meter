#!/usr/bin/env python3
"""Validate an icon pack folder and optionally build a ZIP.

Usage:
    python tools/verify_icons.py icons
    python tools/verify_icons.py icons --zip asgard-icons.zip
"""
from __future__ import annotations

import argparse
import os
import re
import struct
import sys
import zipfile

IID_RE = re.compile(r"^[A-Za-z][A-Za-z0-9_]{2,63}\.png$")
PNG_SIG = b"\x89PNG\r\n\x1a\n"
MAX_BYTES = 2 * 1024 * 1024


def png_size(path: str) -> tuple[int, int] | None:
    with open(path, "rb") as f:
        head = f.read(24)
    if len(head) < 24 or head[:8] != PNG_SIG or head[12:16] != b"IHDR":
        return None
    w, h = struct.unpack(">II", head[16:24])
    return w, h


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("folder")
    ap.add_argument("--zip", help="build a ZIP at this path")
    args = ap.parse_args()

    folder = args.folder
    if not os.path.isdir(folder):
        print(f"not a folder: {folder}")
        return 2

    seen: dict[str, str] = {}
    errors: list[str] = []
    count = 0
    total = 0
    for name in sorted(os.listdir(folder)):
        path = os.path.join(folder, name)
        if not os.path.isfile(path):
            continue
        if not name.lower().endswith(".png"):
            continue
        count += 1
        if not IID_RE.match(name):
            errors.append(f"bad filename (use IID.png): {name}")
        size = os.path.getsize(path)
        total += size
        if size <= 8 or size > MAX_BYTES:
            errors.append(f"bad size ({size}): {name}")
        dim = png_size(path)
        if dim is None:
            errors.append(f"not a PNG: {name}")
        else:
            w, h = dim
            if not (8 <= w <= 256 and 8 <= h <= 256):
                errors.append(f"unexpected dimensions {w}x{h}: {name}")
        key = name.lower()
        if key in seen:
            errors.append(f"duplicate (case-insensitive): {name} vs {seen[key]}")
        seen[key] = name

    print(f"icons={count} total={total} bytes")
    if errors:
        print("ERRORS:")
        for e in errors[:200]:
            print(" -", e)
        return 1

    if args.zip:
        with zipfile.ZipFile(args.zip, "w", zipfile.ZIP_DEFLATED) as z:
            for name in sorted(seen.values()):
                z.write(os.path.join(folder, name), name)
            z.writestr(
                "manifest.json",
                '{"app":"AsgardHuntMeter","count":%d,"naming":"IID.png"}' % count,
            )
        print(f"zip={args.zip}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
