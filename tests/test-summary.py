# SPDX-FileCopyrightText: 2026 Samuel Moxham
# SPDX-License-Identifier: MIT
"""Prints a Markdown summary of a test run: totals from the .trx files and GO timings from timing.csv.

Usage: python3 test-summary.py TITLE RESULTS_DIR
"""

import csv
import pathlib
import statistics
import sys
import xml.etree.ElementTree as ET

NS = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}


def main() -> None:
    title, folder = sys.argv[1], pathlib.Path(sys.argv[2])
    print(f"### {title}")
    trx_files = sorted(folder.glob("*.trx"))
    if not trx_files:
        print("No results file was written (the run stopped before testing).")
        return
    for trx in trx_files:
        counters = ET.parse(trx).getroot().find(".//t:ResultSummary/t:Counters", NS)
        if counters is None:
            continue
        total = counters.get("total", "0")
        passed = counters.get("passed", "0")
        failed = counters.get("failed", "0")
        skipped = int(total) - int(counters.get("executed", total))
        print(f"- `{trx.name}`: {passed} passed, {failed} failed, {skipped} skipped, {total} total")

    timing = folder / "timing.csv"
    if timing.exists():
        groups: dict[tuple[str, str], list[float]] = {}
        with timing.open(newline="", encoding="utf-8") as handle:
            for row in csv.DictReader(handle):
                if row["ms_after_go"]:
                    groups.setdefault((row["test"], row["stage"]), []).append(float(row["ms_after_go"]))
        print()
        print("| Test | Stage | Median ms after GO | Max ms | Samples |")
        print("|---|---|---|---|---|")
        for (test, stage), values in sorted(groups.items()):
            print(f"| {test} | {stage} | {statistics.median(values):.1f} | {max(values):.1f} | {len(values)} |")


if __name__ == "__main__":
    main()
