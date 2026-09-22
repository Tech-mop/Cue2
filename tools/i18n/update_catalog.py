#!/usr/bin/env python3
# SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
# SPDX-License-Identifier: MIT

"""Refresh the Cue2 translation catalog for every locale.

One runtime: LocalizationService loads translations/cue2.csv.
This is the only command you need after adding UI strings:

  python tools/i18n/update_catalog.py

Steps:
  1. extract_ui_strings.py --merge  — discover new English keys from scenes + T()/Tf()
  2. copy FirstTimeStartupWindow.WelcomeBodyEnglish into FIRST_TIME_WELCOME_BODY
     (edit that C# constant, not the CSV). If the English changed, other locales
     for that key are cleared so the next step can refill them.
  3. fill_test_locales.py           — fill mi/es/de/ru/ja/ar/hi from authored tables
                                     (never overwrites a cell that is already translated)

Then restart Cue2. Authored strings live in:
  tools/i18n/test_locale_strings.py   (de ru ja ar hi)
  tools/i18n/test_locale_overlay.py   (mi es)

To add a language later: add a CSV column, add it to LocalizationService.SupportedLocales,
and add translations to those tables.
"""

from __future__ import annotations

import csv
import re
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
HERE = Path(__file__).resolve().parent
WELCOME_CS = ROOT / "src/UI/Windows/FirstTimeStartupWindow.cs"
WELCOME_KEY = "FIRST_TIME_WELCOME_BODY"
WELCOME_CONST_RE = re.compile(
    r"const string WelcomeBodyEnglish\s*=\s*(.*?);",
    re.S,
)
QUOTED_RE = re.compile(r'"((?:\\.|[^"\\])*)"')


def run(script: str, *args: str) -> int:
    cmd = [sys.executable, str(HERE / script), *args]
    print("+", " ".join(cmd))
    return subprocess.call(cmd, cwd=ROOT)


def read_welcome_english() -> str:
    """Read WelcomeBodyEnglish from the welcome window (the editable English source)."""
    text = WELCOME_CS.read_text(encoding="utf-8")
    match = WELCOME_CONST_RE.search(text)
    if not match:
        raise SystemExit(f"WelcomeBodyEnglish not found in {WELCOME_CS}")
    parts = QUOTED_RE.findall(match.group(1))
    if not parts:
        raise SystemExit(f"WelcomeBodyEnglish has no string literals in {WELCOME_CS}")
    body = "".join(parts)
    return (
        body.replace("\\n", "\n")
        .replace("\\t", "\t")
        .replace("\\r", "\r")
        .replace('\\"', '"')
        .replace("\\\\", "\\")
    )


def sync_welcome_english(csv_path: Path) -> None:
    """Copy the C# welcome body into the catalog en column.

    When that English text changes, other locale cells for the key are cleared so
    the fill step can write translations of the new source.
    """
    english = read_welcome_english()
    with csv_path.open(encoding="utf-8", newline="") as handle:
        reader = csv.DictReader(handle)
        fieldnames = list(reader.fieldnames or [])
        rows = list(reader)

    if "en" not in fieldnames:
        raise SystemExit(f"{csv_path} has no en column")

    found = False
    for row in rows:
        if (row.get("keys") or "") != WELCOME_KEY:
            continue
        found = True
        previous = row.get("en") or ""
        row["en"] = english
        if previous != english:
            for column in fieldnames:
                if column in ("keys", "key", "en"):
                    continue
                row[column] = ""
            print(f"Welcome English changed; cleared {WELCOME_KEY} translations for refill.")
        else:
            print(f"Welcome English unchanged ({WELCOME_KEY}).")
        break

    if not found:
        row = {column: "" for column in fieldnames}
        row["keys"] = WELCOME_KEY
        row["en"] = english
        rows.append(row)
        print(f"Added {WELCOME_KEY} from WelcomeBodyEnglish.")

    with csv_path.open("w", encoding="utf-8", newline="") as handle:
        writer = csv.DictWriter(handle, fieldnames=fieldnames, lineterminator="\n")
        writer.writeheader()
        for row in rows:
            writer.writerow({column: row.get(column, "") for column in fieldnames})


def main() -> int:
    code = run("extract_ui_strings.py", "--merge", "translations/cue2.csv")
    if code != 0:
        return code
    sync_welcome_english(ROOT / "translations/cue2.csv")
    return run("fill_test_locales.py")


if __name__ == "__main__":
    raise SystemExit(main())
