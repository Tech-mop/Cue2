# Cue2 tools

Public helper scripts for localization, native libraries, and Godot export.

Signing, notarization, and certificates are **not** in this repository.

## Localization

One runtime catalog: `translations/cue2.csv` (loaded by `LocalizationService`).

```bash
# Discover new English keys from scenes + T()/Tf(), then fill locale columns
python tools/i18n/update_catalog.py

# Extract only (optional --merge into the CSV)
python tools/i18n/extract_ui_strings.py --merge translations/cue2.csv

# List C# UI strings not yet wrapped in T()/Tf()
python tools/i18n/extract_ui_strings.py --report-unwrapped
```

`update_catalog.py` never overwrites a cell that is already translated. Authored strings live in:

- `tools/i18n/test_locale_strings.py` — de, ru, ja, ar, hi
- `tools/i18n/test_locale_overlay.py` — mi, es

Restart Cue2 after a catalog update.

## Native libraries

Godot export does not ship FFmpeg or RtMidi as loadable OS libraries. Rebuild into `bin/` when those natives change:

```bash
python tools/build-rtmidi-natives.py
./tools/build-ffmpeg-macos.sh          # macOS only; portable LGPL FFmpeg into bin/macos
```

Copy into an already-exported folder:

```bash
./tools/copy-natives-for-export.sh /path/to/Cue2.app
./tools/copy-natives-for-export.sh /path/to/linux-export linux64
powershell -NoProfile -ExecutionPolicy Bypass -File tools/copy-natives-for-export.ps1 -ExportRoot ..\Exports\Cue2-windows-x86_64 -Platform win64
```

## Export all platforms

Requires Godot 4.7 Mono, export templates, and `dotnet` on PATH.

```bash
python tools/export-all.py
python tools/export-all.py --dry-run
python tools/export-all.py --only macos-arm64,linux-x86_64
python tools/export-all.py --bump minor
python tools/export-all.py --no-bump
```

This creates per-OS folders, runs each export preset, copies natives, and writes zip/tar.gz plus `latest.json`. Version is taken from `Version.cs` unless a higher `Exports/X.Y.Z` folder or git tag exists, in which case patch is incremented.

macOS codesign/notarize runs only if `tools/macos-sign-and-notarize.sh` exists locally (that file is not published). Otherwise the `.app` is archived unsigned.

```bash
python tools/make-latest-json.py --version 0.2.0 --dist /path/to/dist --out /path/to/dist/latest.json
```
