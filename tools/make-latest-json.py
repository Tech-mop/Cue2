#!/usr/bin/env python3
# SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
# SPDX-License-Identifier: MIT

"""Build latest.json + SHA-256 for Cue2 GitHub Releases.

Usage (from repo root):

  python tools/make-latest-json.py --version 0.2.0 --dist /path/to/dist --out /path/to/dist/latest.json

Looks for files named Cue2-{version}-{platform}.{zip|tar.gz} in --dist.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import re
import sys
from datetime import datetime, timezone
from pathlib import Path

REPO = "Tech-mop/Cue2"
PLATFORM_SUFFIXES = (
    "windows-x86_64",
    "windows-arm64",
    "macos-arm64",
    "macos-x86_64",
    "linux-x86_64",
    "linux-arm64",
)
# Version may include a prerelease suffix (0.2.0-rc.1). Platform is the last -segment before the extension.
ASSET_RE = re.compile(
    r"^Cue2-(?P<version>.+)-(?P<platform>" + "|".join(PLATFORM_SUFFIXES) + r")\.(zip|tar\.gz)$"
)


def sha256_file(path: Path) -> str:
    h = hashlib.sha256()
    with path.open("rb") as f:
        for chunk in iter(lambda: f.read(1024 * 1024), b""):
            h.update(chunk)
    return h.hexdigest()


def main() -> int:
    parser = argparse.ArgumentParser(description="Emit Cue2 latest.json for GitHub Releases.")
    parser.add_argument("--version", required=True, help="Semantic version, e.g. 0.2.0")
    parser.add_argument("--dist", required=True, type=Path, help="Folder containing release archives")
    parser.add_argument("--out", required=True, type=Path, help="Path to write latest.json")
    parser.add_argument("--notes", default="", help="Release notes (plain text)")
    parser.add_argument("--notes-file", type=Path, default=None, help="Read notes from this file")
    parser.add_argument("--tag", default=None, help="GitHub tag (default v{version})")
    args = parser.parse_args()

    version = args.version.strip().lstrip("v")
    tag = (args.tag or f"v{version}").strip()
    notes = args.notes
    if args.notes_file is not None:
        notes = args.notes_file.read_text(encoding="utf-8").strip()

    dist: Path = args.dist.expanduser().resolve()
    if not dist.is_dir():
        print(f"Dist folder not found: {dist}", file=sys.stderr)
        return 1

    platforms: dict[str, dict] = {}
    for path in sorted(dist.iterdir()):
        if not path.is_file():
            continue
        match = ASSET_RE.match(path.name)
        if match is None:
            continue
        if match.group("version") != version:
            print(f"Skipping {path.name}: version does not match {version}", file=sys.stderr)
            continue
        platform = match.group("platform")
        digest = sha256_file(path)
        platforms[platform] = {
            "name": path.name,
            "url": f"https://github.com/{REPO}/releases/download/{tag}/{path.name}",
            "sha256": digest,
            "size": path.stat().st_size,
        }
        print(f"{path.name}  {digest}  {path.stat().st_size}")

    if not platforms:
        print(f"No Cue2-{version}-<platform>.zip/tar.gz files in {dist}", file=sys.stderr)
        return 1

    html = f"https://github.com/{REPO}/releases/tag/{tag}"
    payload = {
        "schema": 1,
        "name": "Cue2",
        "version": version,
        "tag": tag,
        "releasedAt": datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
        "notes": notes,
        "notesUrl": html,
        "htmlUrl": html,
        "platforms": platforms,
    }
    args.out.parent.mkdir(parents=True, exist_ok=True)
    args.out.write_text(json.dumps(payload, indent=2) + "\n", encoding="utf-8")
    print(f"Wrote {args.out}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
