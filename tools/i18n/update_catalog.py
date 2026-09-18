#!/usr/bin/env python3
# SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
# SPDX-License-Identifier: MIT

"""Refresh the Cue2 translation catalog for every locale.

One runtime: LocalizationService loads translations/cue2.csv.
This is the only command you need after adding UI strings:

  python tools/i18n/update_catalog.py

Steps:
  1. extract_ui_strings.py --merge  — discover new English keys from scenes + T()/Tf()
  2. fill_test_locales.py           — fill mi/es/de/ru/ja/ar/hi from authored tables
                                     (never overwrites a cell that is already translated)

Then restart Cue2. Authored strings live in:
  tools/i18n/test_locale_strings.py   (de ru ja ar hi)
  tools/i18n/test_locale_overlay.py   (mi es)

To add a language later: add a CSV column, add it to LocalizationService.SupportedLocales,
and add translations to those tables.
"""

from __future__ import annotations

import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
HERE = Path(__file__).resolve().parent


def run(script: str, *args: str) -> int:
    cmd = [sys.executable, str(HERE / script), *args]
    print("+", " ".join(cmd))
    return subprocess.call(cmd, cwd=ROOT)


def main() -> int:
    code = run("extract_ui_strings.py", "--merge", "translations/cue2.csv")
    if code != 0:
        return code
    return run("fill_test_locales.py")


if __name__ == "__main__":
    raise SystemExit(main())
