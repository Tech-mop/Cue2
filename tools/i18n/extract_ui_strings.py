#!/usr/bin/env python3
# SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
# SPDX-License-Identifier: MIT

"""Extract user-facing UI strings from Cue2 scenes and C# for translation catalogs.

Usage:
  python tools/i18n/extract_ui_strings.py
  python tools/i18n/extract_ui_strings.py --merge translations/cue2.csv
  python tools/i18n/extract_ui_strings.py --report-unwrapped

English source text is the message key (Godot-friendly). Runtime UI should call
UiLocalizer.T("English") / Tf("Template {0}", arg) so this extractor can find
the string without hiding English behind token keys.

Automation (all locales):
  python tools/i18n/update_catalog.py

  That merges new English keys, then fills mi/es/de/ru/ja/ar/hi from
  tools/i18n/test_locale_strings.py and test_locale_overlay.py.
  Restart Cue2 — LocalizationService reloads the CSV at startup.

Do not extract GD.Print / globalSignals.Log payloads — those stay English.
"""

from __future__ import annotations

import argparse
import csv
import re
import sys
from pathlib import Path

# ── Scene (.tscn) property extraction ─────────────────────────────────────

PROPS = (
    "text",
    "tooltip_text",
    "placeholder_text",
    "ok_button_text",
    "cancel_button_text",
    "title",
)
PROP_RE = re.compile(r"^\s*(" + "|".join(PROPS) + r')\s*=\s*"(.*)"\s*$')
PROP_OPEN_RE = re.compile(r"^\s*(" + "|".join(PROPS) + r')\s*=\s*"(.*)$')

# ── C# T() / Tf() extraction (English stays visible in source) ────────────

# UiLocalizer.T("..."), T("..."), Tf("..."), SetTooltip("..."), SetText(...),
# AddTranslatedItem(..., "..."), WithHotkey("..."), ResetDefaultTip is format-only.
CS_CALL_RE = re.compile(
    r"""(?x)
    (?:
        (?:UiLocalizer\.)?T f?
        | (?:UiLocalizer\.)?SetTooltip
        | (?:UiLocalizer\.)?SetText
        | (?:UiLocalizer\.)?SetPlaceholder
        | (?:UiLocalizer\.)?AddTranslatedItem
        | (?:UiLocalizer\.)?WithHotkey
        | (?:UiLocalizer\.)?SetTreeItemText
        | (?:UiLocalizer\.)?SetTreeItemTooltip
    )
    \s*\(\s*
    "((?:\\.|[^"\\])*)"
    """
)

# ── Unwrapped assignment report (not merged; for coverage audits) ─────────

UNWRAPPED_ASSIGN_RE = re.compile(
    r"""(?x)
    (?P<lhs>
        TooltipText
        | PlaceholderText
        | \.Title
        | OkButtonText
        | CancelButtonText
    )
    \s*=\s*
    (?P<rhs>
        \$?"(?:\\.|[^"\\])*"
        | \$@?"(?:\\.|[^"\\])*"
    )
    """
)

ADDITEM_RE = re.compile(
    r"""AddItem\s*\(\s*\$?"((?:\\.|[^"\\])*)" """
)

SKIP_EXACT = {
    "",
    "0",
    "100%",
    "--",
    "Cue2 ",
    " ",
    "X:",
    "Y:",
    "BG",
    "ID:",
    "#1",
    "∞",
    "…",
    "→",
    "↳",
}

# Paths whose string literals are logs / debug / show data, not UI chrome.
SKIP_CS_NAME_PARTS = (
    "\\obj\\",
    "/obj/",
    ".uid",
    "EventLogger",
    "GlobalSignals.cs",
)

DEFAULT_TSCN_ROOTS = ("src",)
DEFAULT_CS_ROOTS = (
    "src/UI",
    "src/Domain/Cuelist",
    "src/Domain/Playback",
    "src/Domain/Cues",
    "src/Domain/Connections",
    "src/Services",
)

# English keys used as T(variable) (display-name helpers / category titles).
# Kept here so they ship in the catalog without hiding the English in C#.
SEED_KEYS = {
    "Session",
    "Playback",
    "Cue Editing",
    "Navigation",
    "Windows",
    "History",
    "Other",
    "GO",
    "Pause",
    "Stop",
    "Resume",
    "Start Now",
    "Fade",
    "Seek",
    "Translate Layer",
    "Volume",
    "Opacity",
    "Pan",
    "Routing Matrix",
    "audio",
    "video",
    "text",
    "resource",
    "audio output patch",
    "target layer",
    "Inputs",
    "Outputs",
    "Library",
    "Timeline",
    "Visual",
    "Connection",
    "Control",
    "Text",
}


def should_keep(s: str) -> bool:
    s = s.strip()
    if not s or s in SKIP_EXACT:
        return False
    if len(s) <= 1:
        return False
    # Pure numbers / times / units — not translatable chrome.
    if re.fullmatch(r"[\d\s.%/\-:]+", s) and "{" not in s:
        return False
    # OSC / file-path-like tokens.
    if s.startswith("/") and " " not in s:
        return False
    return True


def unescape_cs(s: str) -> str:
    return (
        s.replace("\\n", "\n")
        .replace("\\t", "\t")
        .replace("\\r", "\r")
        .replace('\\"', '"')
        .replace("\\\\", "\\")
    )


def normalize(s: str) -> str:
    return (
        s.replace("\\n", "\n")
        .replace('\\"', '"')
        .replace("\\t", "\t")
        .strip("\n")
    )


def extract_from_tscn(path: Path) -> set[str]:
    found: set[str] = set()
    lines = path.read_text(encoding="utf-8").splitlines()
    i = 0
    while i < len(lines):
        line = lines[i]
        m = PROP_RE.match(line)
        if m:
            s = normalize(m.group(2))
            if should_keep(s):
                found.add(s)
            i += 1
            continue
        m2 = PROP_OPEN_RE.match(line)
        if m2 and line.count('"') < 2:
            buf = [m2.group(2)]
            i += 1
            while i < len(lines):
                if lines[i].rstrip().endswith('"'):
                    buf.append(lines[i].rstrip()[:-1])
                    break
                buf.append(lines[i])
                i += 1
            s = normalize("\n".join(buf))
            if should_keep(s):
                found.add(s)
        i += 1
    return found


OSC_CATALOG_RE = re.compile(
    r"""(?:Category|Description)\s*=\s*"((?:\\.|[^"\\])*)" """
)


def extract_from_cs(path: Path) -> set[str]:
    found: set[str] = set()
    text = path.read_text(encoding="utf-8")
    for m in CS_CALL_RE.finditer(text):
        s = unescape_cs(m.group(1))
        if should_keep(s):
            found.add(s)
    # Built-in OSC help catalog: English Category / Description shown in Settings.
    if path.name == "OscListen.BuiltInCommands.cs":
        for m in OSC_CATALOG_RE.finditer(text):
            s = unescape_cs(m.group(1))
            if should_keep(s):
                found.add(s)
    return found


def is_skipped_cs(path: Path, root: Path) -> bool:
    rel = str(path.relative_to(root)).replace("/", "\\")
    low = rel.lower()
    for part in SKIP_CS_NAME_PARTS:
        if part.lower().replace("/", "\\") in f"\\{low}\\" or part.lower() in low:
            if part in (".uid",) and not low.endswith(".uid"):
                continue
            if "globalevents" in low:
                continue
            if part.lower() in ("eventlogger", "globalsignals.cs") and part.lower() not in low:
                continue
            if part in ("\\obj\\", "/obj/") and "\\obj\\" not in f"\\{low}":
                continue
    if path.suffix != ".cs":
        return True
    if "\\obj\\" in f"\\{rel}" or "/obj/" in rel.replace("\\", "/"):
        return True
    name = path.name.lower()
    if name.endswith(".uid") or name == "uilocalizer.cs":
        return True
    return False


def collect_tscn(root: Path, roots: list[str]) -> set[str]:
    strings: set[str] = set()
    for rel in roots:
        p = root / rel
        if p.is_file() and p.suffix == ".tscn":
            strings |= extract_from_tscn(p)
        elif p.is_dir():
            for tscn in p.rglob("*.tscn"):
                strings |= extract_from_tscn(tscn)
    return strings


def collect_cs(root: Path, roots: list[str]) -> tuple[set[str], list[Path]]:
    strings: set[str] = set()
    files: list[Path] = []
    for rel in roots:
        p = root / rel
        candidates: list[Path]
        if p.is_file() and p.suffix == ".cs":
            candidates = [p]
        elif p.is_dir():
            candidates = list(p.rglob("*.cs"))
        else:
            continue
        for cs in candidates:
            if is_skipped_cs(cs, root):
                continue
            files.append(cs)
            strings |= extract_from_cs(cs)
    return strings, files


def load_existing(csv_path: Path) -> dict[str, dict[str, str]]:
    if not csv_path.exists():
        return {}
    with csv_path.open(encoding="utf-8", newline="") as f:
        reader = csv.DictReader(f)
        rows: dict[str, dict[str, str]] = {}
        for row in reader:
            key = row.get("keys") or row.get("key")
            if key:
                rows[key] = row
        return rows


def looks_already_wrapped(line: str) -> bool:
    return bool(
        re.search(
            r"(?:UiLocalizer\.)?(?:T|Tf|SetTooltip|SetText|SetPlaceholder|AddTranslatedItem|WithHotkey)\s*\(",
            line,
        )
    )


def report_unwrapped(cs_files: list[Path], root: Path) -> int:
    """Print C# UI assignments that are not yet wrapped in T()/Tf()."""
    hits = 0
    for path in sorted(cs_files):
        rel = path.relative_to(root)
        lines = path.read_text(encoding="utf-8").splitlines()
        for i, line in enumerate(lines, start=1):
            stripped = line.strip()
            if stripped.startswith("//") or stripped.startswith("*"):
                continue
            if "GD.Print" in line or "EmitSignal" in line and "Log" in line:
                continue
            if looks_already_wrapped(line):
                continue
            m = UNWRAPPED_ASSIGN_RE.search(line)
            if m:
                rhs = m.group("rhs")
                # Skip empty / numeric-only literals.
                lit = rhs.lstrip("$")
                if lit.startswith('"'):
                    inner = unescape_cs(lit[1:-1]) if lit.endswith('"') else lit
                    if not should_keep(inner) and "{" not in inner:
                        continue
                print(f"{rel}:{i}: {stripped}")
                hits += 1
                continue
            # AddItem("English") — skip AddItem(T(...)) already filtered.
            am = ADDITEM_RE.search(line)
            if am and should_keep(unescape_cs(am.group(1))):
                print(f"{rel}:{i}: {stripped}")
                hits += 1
    print(f"# {hits} unwrapped assignment(s)", file=sys.stderr)
    return 0


def merge_csv(out_path: Path, strings: set[str]) -> None:
    existing = load_existing(out_path)
    locales = ["en"]
    if existing:
        sample = next(iter(existing.values()))
        locales = [c for c in sample.keys() if c not in ("keys", "key")]
        if "en" not in locales:
            locales = ["en"] + locales

    added = 0
    for s in strings:
        if s not in existing:
            added += 1
            row = {"keys": s, "en": s}
            for loc in locales:
                row[loc] = s  # untranslated placeholder = English identity
            existing[s] = row
        else:
            # Ensure English column matches the source key when empty.
            row = existing[s]
            if not (row.get("en") or "").strip():
                row["en"] = s
            for loc in locales:
                if loc != "en" and not (row.get(loc) or "").strip():
                    row[loc] = s

    fieldnames = ["keys"] + locales
    with out_path.open("w", encoding="utf-8", newline="") as f:
        w = csv.DictWriter(f, fieldnames=fieldnames, lineterminator="\n")
        w.writeheader()
        for key in sorted(existing.keys(), key=lambda k: k.lower()):
            row = existing[key]
            out = {"keys": key}
            for loc in locales:
                out[loc] = row.get(loc, row.get("en", key))
            w.writerow(out)
    print(
        f"Merged {len(strings)} extracted strings "
        f"({added} new) into {out_path} ({len(existing)} total keys)"
    )


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--merge",
        type=Path,
        help="Merge extracted English keys into this CSV (preserves other locale columns).",
    )
    parser.add_argument(
        "--roots",
        nargs="*",
        default=list(DEFAULT_TSCN_ROOTS),
        help="Directories or .tscn files to scan for scene strings",
    )
    parser.add_argument(
        "--cs-roots",
        nargs="*",
        default=list(DEFAULT_CS_ROOTS),
        help="Directories or .cs files to scan for T()/Tf() literals",
    )
    parser.add_argument(
        "--report-unwrapped",
        action="store_true",
        help="List C# TooltipText/PlaceholderText/AddItem literals not wrapped in T()/Tf().",
    )
    parser.add_argument(
        "--scenes-only",
        action="store_true",
        help="Skip C# T()/Tf() extraction (legacy scene-only mode).",
    )
    args = parser.parse_args()
    root = Path(__file__).resolve().parents[2]

    strings = collect_tscn(root, args.roots)
    strings |= {s for s in SEED_KEYS if should_keep(s)}
    cs_files: list[Path] = []
    if not args.scenes_only:
        cs_strings, cs_files = collect_cs(root, args.cs_roots)
        strings |= cs_strings

    if args.report_unwrapped:
        if not cs_files:
            _, cs_files = collect_cs(root, args.cs_roots)
        return report_unwrapped(cs_files, root)

    if args.merge:
        out_path = args.merge if args.merge.is_absolute() else root / args.merge
        merge_csv(out_path, strings)
    else:
        w = csv.writer(sys.stdout, lineterminator="\n")
        w.writerow(["keys", "en"])
        for s in sorted(strings, key=str.lower):
            w.writerow([s, s])
        print(f"# {len(strings)} strings", file=sys.stderr)

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
