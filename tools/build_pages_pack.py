#!/usr/bin/env python3
"""Build docs/icons/asgard-icons.zip + latest.json for GitHub Pages."""
from __future__ import annotations

import hashlib
import json
import os
import time
import zipfile

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
ICONS = os.path.join(ROOT, "icons")
OUT = os.path.join(ROOT, "docs", "icons")
os.makedirs(OUT, exist_ok=True)

files = [f for f in sorted(os.listdir(ICONS)) if f.lower().endswith(".png")]
zip_path = os.path.join(OUT, "asgard-icons.zip")
with zipfile.ZipFile(zip_path, "w", zipfile.ZIP_DEFLATED) as z:
    for f in files:
        z.write(os.path.join(ICONS, f), f)
    z.writestr(
        "manifest.json",
        json.dumps({"app": "AsgardHuntMeter", "count": len(files), "naming": "IID.png"}, ensure_ascii=False),
    )

b = open(zip_path, "rb").read()
meta = {
    "ok": True,
    "name": "icon-pack",
    "version": time.strftime("%Y%m%d"),
    "count": len(files),
    "sha256": hashlib.sha256(b).hexdigest(),
    "size": len(b),
    "url": "asgard-icons.zip",
}
open(os.path.join(OUT, "latest.json"), "w", encoding="utf-8").write(
    json.dumps(meta, ensure_ascii=False, indent=2)
)
print(f"icons={len(files)} size={len(b)} version={meta['version']}")
