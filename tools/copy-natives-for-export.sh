#!/usr/bin/env bash
# SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
# SPDX-License-Identifier: MIT

# Copies FFmpeg + RtMidi natives into a Godot macOS/Linux export.
# Usage:
#   ./tools/copy-natives-for-export.sh "/path/to/ExportTests/Mac/260802"
#   ./tools/copy-natives-for-export.sh "/path/to/Cue2.app"
#
# Preferred macOS layout (this script):
#   Cue2.app/Contents/Frameworks/*.dylib   ← primary (NativeLibPaths searches here first among bundle dirs)
#   Cue2.app/Contents/Resources/data_Cue2_*/*.dylib  ← also flat-copied next to managed assemblies
#   Cue2.app/Contents/Resources/bin/macos/  ← full bin copy (LGPL-friendly replace path)
#
# After copy, rewrites core FFmpeg install names to @loader_path so the five libs
# resolve each other from the same folder. Homebrew-built FFmpeg may still pull
# optional codec deps from /opt/homebrew — for a portable build, ship LGPL FFmpeg
# linked with @rpath only (see docs/export-packaging.md).

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"

if [[ $# -lt 1 ]]; then
  echo "Usage: $0 <ExportRoot-or-.app> [platform]"
  echo "  platform: macos (default on Darwin), linux64, linuxarm64"
  exit 1
fi

# Trim accidental leading/trailing whitespace from copy-pasted paths.
TARGET="${1#"${1%%[![:space:]]*}"}"
TARGET="${TARGET%"${TARGET##*[![:space:]]}"}"
if [[ ! -d "$TARGET" ]]; then
  echo "ERROR: not a directory: '$TARGET'" >&2
  echo "Pass the folder that contains the .app, or the .app itself." >&2
  exit 1
fi

EXPORT_ROOT="$(cd "$TARGET" && pwd)"
PLATFORM="${2:-}"

if [[ -z "$PLATFORM" ]]; then
  case "$(uname -s)" in
    Darwin) PLATFORM="macos" ;;
    *)      PLATFORM="linux64" ;;
  esac
fi

SRC_BIN="$PROJECT_ROOT/bin/$PLATFORM"
if [[ ! -d "$SRC_BIN" ]]; then
  echo "ERROR: source natives not found: $SRC_BIN" >&2
  exit 1
fi

# Resolve .app path (ExportRoot may be the folder containing the .app)
APP_BUNDLE=""
if [[ "$EXPORT_ROOT" == *.app ]]; then
  APP_BUNDLE="$EXPORT_ROOT"
elif [[ -d "$EXPORT_ROOT" ]]; then
  # Prefer first .app under export root
  APP_BUNDLE="$(find "$EXPORT_ROOT" -maxdepth 2 -name "*.app" -type d | head -1 || true)"
fi

echo "copy-natives-for-export.sh"
echo "  platform: $PLATFORM"
echo "  source:   $SRC_BIN"
echo "  export:   $EXPORT_ROOT"
echo "  app:      ${APP_BUNDLE:-"(none)"}"

CORE_MACOS=(
  "libavutil.61.dylib"
  "libavcodec.63.dylib"
  "libavformat.63.dylib"
  "libswresample.7.dylib"
  "libswscale.10.dylib"
)
MIDI_MACOS="librtmidi.dylib"
MIDI_LINUX="librtmidi.so"

CORE_LINUX=(
  "libavutil.so.61"
  "libavcodec.so.63"
  "libavformat.so.63"
  "libswresample.so.7"
  "libswscale.so.10"
)

copy_file() {
  local src="$1" dest_dir="$2"
  mkdir -p "$dest_dir"
  if [[ ! -f "$src" ]]; then
    echo "  WARN: missing $src" >&2
    return 1
  fi
  cp -f "$src" "$dest_dir/"
  echo "  -> $dest_dir/$(basename "$src")"
}

# Rewrite FFmpeg self-references to @loader_path for libs co-located in dest_dir.
# Only touches known core FFmpeg basenames; leaves external Homebrew deps alone.
fix_ffmpeg_loader_paths() {
  local dest_dir="$1"
  if [[ "$(uname -s)" != "Darwin" ]]; then
    return 0
  fi
  if ! command -v install_name_tool >/dev/null 2>&1; then
    echo "  WARN: install_name_tool not found; skipping @loader_path rewrite" >&2
    return 0
  fi

  local cores=("${CORE_MACOS[@]}")
  # Map any absolute/cellar path ending in these basenames → @loader_path/basename
  for lib in "${cores[@]}"; do
    local path="$dest_dir/$lib"
    [[ -f "$path" ]] || continue

    # Set own install name
    install_name_tool -id "@loader_path/$lib" "$path" 2>/dev/null || true

    # Rewrite deps that point at other core FFmpeg libs
    local deps
    deps="$(otool -L "$path" 2>/dev/null | tail -n +2 | awk '{print $1}')" || true
    while IFS= read -r dep; do
      [[ -z "$dep" ]] && continue
      local base
      base="$(basename "$dep")"
      for other in "${cores[@]}"; do
        if [[ "$base" == "$other" && "$dep" != "@loader_path/$other" ]]; then
          install_name_tool -change "$dep" "@loader_path/$other" "$path" 2>/dev/null || true
        fi
      done
    done <<< "$deps"
  done
  echo "  (rewrote core FFmpeg install names to @loader_path in $dest_dir)"
}

copy_core_set() {
  local dest_dir="$1"
  mkdir -p "$dest_dir"
  if [[ "$PLATFORM" == "macos" ]]; then
    for f in "${CORE_MACOS[@]}"; do
      copy_file "$SRC_BIN/$f" "$dest_dir" || true
    done
    if [[ -f "$SRC_BIN/$MIDI_MACOS" ]]; then
      copy_file "$SRC_BIN/$MIDI_MACOS" "$dest_dir" || true
    else
      echo "  WARN: MIDI native missing: $SRC_BIN/$MIDI_MACOS" >&2
    fi
    fix_ffmpeg_loader_paths "$dest_dir"
  else
    for f in "${CORE_LINUX[@]}"; do
      copy_file "$SRC_BIN/$f" "$dest_dir" || true
    done
    if [[ -f "$SRC_BIN/$MIDI_LINUX" ]]; then
      copy_file "$SRC_BIN/$MIDI_LINUX" "$dest_dir" || true
    else
      echo "  WARN: MIDI native missing: $SRC_BIN/$MIDI_LINUX" >&2
    fi
  fi
}

COPIED=0

# --- macOS .app ---
if [[ -n "$APP_BUNDLE" && -d "$APP_BUNDLE/Contents" ]]; then
  FRAMEWORKS="$APP_BUNDLE/Contents/Frameworks"
  RESOURCES="$APP_BUNDLE/Contents/Resources"
  BIN_MACOS="$RESOURCES/bin/macos"

  echo "Copying core FFmpeg + MIDI into Frameworks (primary)"
  copy_core_set "$FRAMEWORKS"
  COPIED=1

  echo "Copying full bin/$PLATFORM into Resources/bin/$PLATFORM (LGPL replace path)"
  mkdir -p "$BIN_MACOS"
  cp -f "$SRC_BIN/"* "$BIN_MACOS/" 2>/dev/null || true
  if [[ "$PLATFORM" == "macos" ]]; then
    fix_ffmpeg_loader_paths "$BIN_MACOS"
  fi

  # Flat into each data_Cue2_* under Resources (same place as libSDL3.dylib)
  while IFS= read -r -d '' data_dir; do
    echo "Copying core set into $data_dir"
    copy_core_set "$data_dir"
  done < <(find "$RESOURCES" -maxdepth 1 -type d -name 'data_Cue2_*' -print0 2>/dev/null)

  echo ""
  echo "Note: if your FFmpeg dylibs were built via Homebrew, they still depend on"
  echo "other /opt/homebrew libraries for many codecs. That works on this machine"
  echo "but is not portable. For distribution, use a portable LGPL FFmpeg build."
  echo ""
  echo "After modifying the .app, re-sign and notarize before distributing:"
  echo "  ./tools/macos-sign-and-notarize.sh \"$APP_BUNDLE\""

elif [[ "$PLATFORM" == "macos" ]]; then
  # No .app found: treat ExportRoot as a loose folder
  echo "No .app found; copying to $EXPORT_ROOT/bin/macos and data_Cue2_* if present"
  copy_core_set "$EXPORT_ROOT/bin/macos"
  mkdir -p "$EXPORT_ROOT/bin/macos"
  cp -f "$SRC_BIN/"* "$EXPORT_ROOT/bin/macos/" 2>/dev/null || true
  while IFS= read -r -d '' data_dir; do
    copy_core_set "$data_dir"
  done < <(find "$EXPORT_ROOT" -maxdepth 2 -type d -name 'data_Cue2_*' -print0 2>/dev/null)
  COPIED=1
else
  echo "Copying into $EXPORT_ROOT/bin/$PLATFORM and data_Cue2_*"
  copy_core_set "$EXPORT_ROOT/bin/$PLATFORM"
  while IFS= read -r -d '' data_dir; do
    copy_core_set "$data_dir"
  done < <(find "$EXPORT_ROOT" -maxdepth 2 -type d -name 'data_Cue2_*' -print0 2>/dev/null)
  COPIED=1
fi

if [[ "$COPIED" -eq 0 ]]; then
  echo "ERROR: nothing was copied" >&2
  exit 1
fi

echo "Done."
