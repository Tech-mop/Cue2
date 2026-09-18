#!/usr/bin/env bash
# SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
# SPDX-License-Identifier: MIT

# Build portable LGPL FFmpeg 9 shared libraries into bin/macos.
#
# The Homebrew bottle in bin/macos is NOT shippable: install names and
# LC_LOAD_DYLIB entries point at /opt/homebrew (x264, x265, openssl, …),
# and that bottle is configured --enable-gpl.
#
# This script builds FFmpeg 9.0.1 (matches FFmpeg.AutoGen 9.0.1.1) as
# LGPLv2.1+ shared libs that only link macOS system frameworks + the five
# sibling dylibs via @loader_path. Native + VideoToolbox decoders cover
# H.264 / HEVC / ProRes / VP9 / AV1 / AAC / MP3 / etc. without x264/x265.
#
# Usage (from repo root, Apple Silicon Mac, ~10–15 min):
#   ./tools/build-ffmpeg-macos.sh
#
# Optional:
#   FFMPEG_VERSION=9.0.1 PREFIX=... ./tools/build-ffmpeg-macos.sh
#
# Does not touch librtmidi.dylib.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
FFMPEG_VERSION="${FFMPEG_VERSION:-9.0.1}"
SRC_URL="https://ffmpeg.org/releases/ffmpeg-${FFMPEG_VERSION}.tar.xz"
WORK="${CUE2_FFMPEG_WORK:-${TMPDIR:-/tmp}/cue2-ffmpeg-build}"
PREFIX="$WORK/prefix"
DEST="$PROJECT_ROOT/bin/macos"
ARCH="$(uname -m)"
MACOSX_DEPLOYMENT_TARGET="${MACOSX_DEPLOYMENT_TARGET:-11.0}"
JOBS="$(sysctl -n hw.ncpu 2>/dev/null || echo 4)"

CORE_LIBS=(
  "libavutil.61.dylib"
  "libavcodec.63.dylib"
  "libavformat.63.dylib"
  "libswresample.7.dylib"
  "libswscale.10.dylib"
)

if [[ "$(uname -s)" != "Darwin" ]]; then
  echo "ERROR: this script builds macOS dylibs and must run on a Mac." >&2
  exit 1
fi

if ! command -v clang >/dev/null 2>&1; then
  echo "ERROR: clang not found. Install Xcode Command Line Tools: xcode-select --install" >&2
  exit 1
fi

echo "build-ffmpeg-macos.sh"
echo "  version:  $FFMPEG_VERSION"
echo "  arch:     $ARCH"
echo "  dest:     $DEST"
echo "  work:     $WORK"
echo "  min macOS:$MACOSX_DEPLOYMENT_TARGET"
echo ""

mkdir -p "$WORK" "$DEST"
TARBALL="$WORK/ffmpeg-${FFMPEG_VERSION}.tar.xz"
if [[ ! -f "$TARBALL" ]]; then
  echo "Downloading $SRC_URL"
  curl -fL --retry 3 -o "$TARBALL" "$SRC_URL"
fi

SRC_DIR="$WORK/ffmpeg-${FFMPEG_VERSION}"
if [[ ! -d "$SRC_DIR" ]]; then
  echo "Extracting"
  tar -xJf "$TARBALL" -C "$WORK"
fi

# Stop Homebrew / pkg-config from pulling x264, openssl, dav1d, …
export PKG_CONFIG_PATH=""
export PKG_CONFIG_LIBDIR="/usr/lib/pkgconfig"
export MACOSX_DEPLOYMENT_TARGET
# Keep system tools; drop Homebrew from PATH so configure cannot find brew ffmpeg.
export PATH="/usr/bin:/bin:/usr/sbin:/sbin"

BUILD_DIR="$WORK/build-$ARCH"
rm -rf "$BUILD_DIR" "$PREFIX"
mkdir -p "$BUILD_DIR" "$PREFIX"
cd "$BUILD_DIR"

echo "Configuring (LGPL, no autodetect, VideoToolbox only)"
# --disable-autodetect is the important bit: otherwise configure finds
# Homebrew libs and we are back to a non-portable bottle.
# FFmpeg 9 dropped libpostproc (no --disable-postproc). GPL/nonfree stay
# off by default; do not pass --disable-gpl (unknown option).
"$SRC_DIR/configure" \
  --prefix="$PREFIX" \
  --install-name-dir=@loader_path \
  --enable-shared \
  --disable-static \
  --disable-autodetect \
  --disable-debug \
  --disable-doc \
  --disable-htmlpages \
  --disable-manpages \
  --disable-podpages \
  --disable-txtpages \
  --disable-programs \
  --disable-avdevice \
  --disable-avfilter \
  --disable-network \
  --disable-appkit \
  --disable-coreimage \
  --disable-metal \
  --enable-pthreads \
  --enable-videotoolbox \
  --enable-audiotoolbox \
  --enable-zlib \
  --enable-bzlib \
  --enable-iconv \
  --enable-protocol=file \
  --enable-protocol=pipe \
  --enable-protocol=crypto \
  --enable-protocol=data \
  --extra-cflags="-mmacosx-version-min=${MACOSX_DEPLOYMENT_TARGET}" \
  --extra-ldflags="-mmacosx-version-min=${MACOSX_DEPLOYMENT_TARGET} -Wl,-headerpad_max_install_names" \
  --extra-libs="-liconv -lbz2 -lz" \
  --cc=clang \
  --arch="$ARCH"

echo "Building (-j$JOBS)"
make -j"$JOBS"
make install

echo "Installing dylibs into $DEST"
# Resolve libavcodec.63.dylib (symlink) to the real file, write the SONAME Cue2 loads.
for soname in "${CORE_LIBS[@]}"; do
  src="$PREFIX/lib/$soname"
  if [[ ! -e "$src" ]]; then
    echo "ERROR: expected $src after install" >&2
    ls -la "$PREFIX/lib" >&2
    exit 1
  fi
  cp -fH "$src" "$DEST/$soname"
done

# Sibling refs → @loader_path so NativeLibrary.Load from Frameworks/ works
# on any Mac (copy-natives-for-export.sh does the same rewrite after export).
for soname in "${CORE_LIBS[@]}"; do
  path="$DEST/$soname"
  install_name_tool -id "@loader_path/$soname" "$path"
  deps="$(otool -L "$path" | tail -n +2 | awk '{print $1}')"
  while IFS= read -r dep; do
    [[ -z "$dep" ]] && continue
    base="$(basename "$dep")"
    for other in "${CORE_LIBS[@]}"; do
      if [[ "$base" == "$other" && "$dep" != "@loader_path/$other" ]]; then
        install_name_tool -change "$dep" "@loader_path/$other" "$path"
      fi
    done
  done <<< "$deps"
done

echo ""
echo "Dependency check (must be system + @loader_path only):"
BAD=0
for soname in "${CORE_LIBS[@]}"; do
  echo "---- $soname ----"
  otool -L "$DEST/$soname" | sed 's/^/  /'
  if otool -L "$DEST/$soname" | awk 'NR>1 {print $1}' | grep -Eq '/opt/homebrew|/usr/local/Cellar|/usr/local/opt'; then
    echo "ERROR: $soname still links a Homebrew path" >&2
    BAD=1
  fi
done

if [[ "$BAD" -ne 0 ]]; then
  exit 1
fi

cat > "$DEST/FFMPEG_BUILD.txt" <<EOF
FFmpeg ${FFMPEG_VERSION} (LGPLv2.1+)
Source: ${SRC_URL}
Built:  $(date -u +"%Y-%m-%dT%H:%M:%SZ")
Host:   $(uname -m)  macOS ${MACOSX_DEPLOYMENT_TARGET}+
Config: --disable-gpl --disable-nonfree --disable-autodetect
        --enable-videotoolbox --enable-audiotoolbox
        no x264 / x265 / openssl / dav1d / libvpx
See docs/FFmpeg-Licensing.md
EOF

echo ""
echo "Done. Portable LGPL dylibs are in $DEST"
echo "Re-export (or copy-natives + sign) so the .app picks them up."
echo "librtmidi.dylib was left unchanged."
