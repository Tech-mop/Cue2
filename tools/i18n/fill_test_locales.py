#!/usr/bin/env python3
# SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
# SPDX-License-Identifier: MIT

"""Fill locale columns in translations/cue2.csv from authored string tables.

One runtime catalog: translations/cue2.csv (LocalizationService loads it).
This script writes every locale column except English.

  python tools/i18n/update_catalog.py          # extract + fill (preferred)
  python tools/i18n/fill_test_locales.py       # fill only

Existing non-English cells are kept. Identity-English and empty cells are filled
from test_locale_strings.py (de/ru/ja/ar/hi) and test_locale_overlay.py (mi/es).
"""

from __future__ import annotations

import argparse
import csv
import re
import sys
from pathlib import Path

# Tuple order in test_locale_strings.STRINGS
TUPLE_LOCALES = ("de", "ru", "ja", "ar", "hi")

# Technical / brand / path tokens — stay identical in every locale.
IDENTITY_EXACT = {
    "",
    "#1",
    "0.0dB",
    "0dB",
    "-90.0dB",
    "00m:00s.000ms",
    "HH:mm:ss",
    "m:s:ms",
    "m:s:ms (derived from start/end)",
    "Cue2",
    "Cue2 ",
    "Art-Net",
    "BBCode",
    "DHCP",
    "SSID",
    "TCP",
    "UDP",
    "OSC",
    "MIDI",
    "ID:",
    "X:",
    "Y:",
    "BG",
    "args",
    "Col",
    "Dur",
    "Op%",
    "Val",
    "Pad",
    "Out",
    "IP",
    "Port",
    "ActionName",
}

IDENTITY_PREFIXES = ("/", "LOCALE_NAME_")


def is_identity(text: str) -> bool:
    if text in IDENTITY_EXACT:
        return True
    if any(text.startswith(p) for p in IDENTITY_PREFIXES):
        return True
    if re.fullmatch(r"[\d\s.%/\-:×x]+", text or ""):
        return True
    return False


def as_locale_map(value: object) -> dict[str, str]:
    """Normalize a STRINGS entry to {locale: text}."""
    if isinstance(value, dict):
        return {str(k): str(v) for k, v in value.items() if v}
    if isinstance(value, (tuple, list)):
        if len(value) >= 7:
            keys = ("mi", "es", "de", "ru", "ja", "ar", "hi")
            return {keys[i]: str(value[i]) for i in range(7) if value[i]}
        if len(value) >= 5:
            return {TUPLE_LOCALES[i]: str(value[i]) for i in range(5) if value[i]}
    return {}


def lookup_map(strings: dict, overlay: dict, key: str, english: str) -> dict[str, str]:
    merged: dict[str, str] = {}
    for candidate in (key, english):
        if candidate in strings:
            merged.update(as_locale_map(strings[candidate]))
        if candidate in overlay:
            merged.update(as_locale_map(overlay[candidate]))
    return merged


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--csv", type=Path, default=Path("translations/cue2.csv"))
    args = parser.parse_args()
    root = Path(__file__).resolve().parents[2]
    csv_path = args.csv if args.csv.is_absolute() else root / args.csv

    sys.path.insert(0, str(Path(__file__).resolve().parent))
    from test_locale_strings import STRINGS as strings

    overlay: dict = {}
    try:
        from test_locale_overlay import OVERLAY as overlay
    except ImportError:
        overlay = {}

    with csv_path.open(encoding="utf-8", newline="") as f:
        reader = csv.DictReader(f)
        fieldnames = list(reader.fieldnames or [])
        rows = list(reader)

    # Ensure every authored locale column exists (never drop en/mi/es).
    for loc in ("mi", "es") + TUPLE_LOCALES:
        if loc not in fieldnames:
            fieldnames.append(loc)

    fill_locales = [c for c in fieldnames if c not in ("keys", "key", "en")]

    filled = {loc: 0 for loc in fill_locales}
    skipped = {loc: 0 for loc in fill_locales}
    identity = 0
    missing = 0
    for row in rows:
        key = row.get("keys") or ""
        english = row.get("en") or key
        loc_map = lookup_map(strings, overlay, key, english)
        ident_src = is_identity(english) or is_identity(key)

        for loc in fill_locales:
            existing = (row.get(loc) or "").strip()
            if existing and existing != english:
                skipped[loc] += 1
                continue
            if ident_src:
                row[loc] = english
                identity += 1
                continue
            text = loc_map.get(loc)
            if text:
                row[loc] = text
                filled[loc] += 1
            else:
                row[loc] = english
                missing += 1

    with csv_path.open("w", encoding="utf-8", newline="") as f:
        w = csv.DictWriter(f, fieldnames=fieldnames, lineterminator="\n")
        w.writeheader()
        for row in rows:
            w.writerow({k: row.get(k, "") for k in fieldnames})

    parts = ", ".join(f"{loc}={filled[loc]}" for loc in fill_locales)
    print(f"Wrote {csv_path} ({len(rows)} keys). Newly filled: {parts}")
    print(
        f"  kept existing translations; "
        f"identity-tokens≈{identity // max(1, len(fill_locales))}; "
        f"still-English≈{missing // max(1, len(fill_locales))}"
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
