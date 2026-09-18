#!/usr/bin/env python3
# SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
# SPDX-License-Identifier: MIT
"""
Build / fetch RtMidi 6.0.0 shared libraries into bin/{win64,winarm64,linux64,linuxarm64,macos}.

Windows + Linux: compile official RtMidi C API with Zig (ALSA-only on Linux — no JACK).
macOS: Homebrew bottle (arm64), matching Cue2's existing arm64 FFmpeg dylibs.

Usage (from repo root):
  python tools/build-rtmidi-natives.py
"""

from __future__ import annotations

import hashlib
import io
import os
import shutil
import struct
import subprocess
import sys
import tarfile
import urllib.request
import zipfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
CACHE = Path(os.environ.get("TEMP", "/tmp")) / "cue2-rtmidi-build"
ZIG_VER = "0.14.1"
ZIG_ZIP_URL = f"https://ziglang.org/download/{ZIG_VER}/zig-x86_64-windows-{ZIG_VER}.zip"
RTMIDI_VER = "6.0.0"
RTMIDI_URL = f"https://github.com/thestk/rtmidi/archive/refs/tags/{RTMIDI_VER}.tar.gz"

# Ubuntu jammy — xz-compressed debs (noble uses zstd). Headers + link-time libasound.
ALSA_DEV_DEB = (
    "http://archive.ubuntu.com/ubuntu/pool/main/a/alsa-lib/"
    "libasound2-dev_1.2.6.1-1ubuntu1_amd64.deb"
)
ALSA_LIB_AMD64 = (
    "http://archive.ubuntu.com/ubuntu/pool/main/a/alsa-lib/"
    "libasound2_1.2.6.1-1ubuntu1_amd64.deb"
)
ALSA_LIB_ARM64 = (
    "http://ports.ubuntu.com/ubuntu-ports/pool/main/a/alsa-lib/"
    "libasound2_1.2.6.1-1ubuntu1_arm64.deb"
)

# Homebrew bottle: relocatable arm64 dylib, system frameworks only.
BREW_MAC_ARM64 = (
    "https://ghcr.io/v2/homebrew/core/rtmidi/blobs/sha256:"
    "8ec6008f1f9002017e8f763b500ec1bcbba1261d4412a87c0394bf95c4105709"
)


def log(msg: str) -> None:
    print(msg, flush=True)


def download(url: str, dest: Path, headers: dict[str, str] | None = None) -> Path:
    dest.parent.mkdir(parents=True, exist_ok=True)
    if dest.exists() and dest.stat().st_size > 0:
        log(f"  cached {dest.name}")
        return dest
    log(f"  downloading {url}")
    req = urllib.request.Request(url, headers=headers or {"User-Agent": "Cue2-rtmidi-build"})
    with urllib.request.urlopen(req, timeout=120) as resp, open(dest, "wb") as out:
        shutil.copyfileobj(resp, out)
    return dest


def extract_deb(deb_path: Path, out_dir: Path) -> None:
    """Minimal System V ar extractor for .deb (data.tar.* member)."""
    data = deb_path.read_bytes()
    if not data.startswith(b"!<arch>\n"):
        raise RuntimeError(f"not a .deb: {deb_path}")
    pos = 8
    while pos + 60 <= len(data):
        header = data[pos : pos + 60]
        pos += 60
        name = header[0:16].decode("ascii", "replace").strip()
        size = int(header[48:58].decode("ascii").strip())
        blob = data[pos : pos + size]
        pos += size
        if size % 2 == 1:
            pos += 1
        if not name.startswith("data.tar"):
            continue
        out_dir.mkdir(parents=True, exist_ok=True)
        bio = io.BytesIO(blob)
        if "zst" in name:
            import zstandard

            dctx = zstandard.ZstdDecompressor()
            bio = io.BytesIO(dctx.decompress(blob, max_output_size=64 * 1024 * 1024))
        elif name.endswith(".xz") or name.endswith("xz"):
            import lzma

            bio = io.BytesIO(lzma.decompress(blob))
        elif name.endswith(".gz") or name.endswith("gz"):
            import gzip

            bio = io.BytesIO(gzip.decompress(blob))
        with tarfile.open(fileobj=bio, mode="r:") as tar:
            tar.extractall(out_dir)
        return
    raise RuntimeError(f"no data.tar in {deb_path}")


def ensure_zig() -> Path:
    zig_dir = CACHE / f"zig-x86_64-windows-{ZIG_VER}"
    zig_exe = zig_dir / "zig.exe"
    if zig_exe.exists():
        return zig_exe
    zipp = download(ZIG_ZIP_URL, CACHE / f"zig-{ZIG_VER}.zip")
    log("  extracting zig")
    with zipfile.ZipFile(zipp) as zf:
        zf.extractall(CACHE)
    if not zig_exe.exists():
        # Folder name may vary slightly
        found = list(CACHE.glob("zig-*/zig.exe"))
        if not found:
            raise RuntimeError("zig.exe not found after extract")
        return found[0]
    return zig_exe


def ensure_rtmidi_src() -> Path:
    src_root = CACHE / f"rtmidi-{RTMIDI_VER}"
    if (src_root / "RtMidi.cpp").exists():
        return src_root
    tgz = download(RTMIDI_URL, CACHE / f"rtmidi-{RTMIDI_VER}.tar.gz")
    log("  extracting rtmidi source")
    with tarfile.open(tgz, "r:gz") as tar:
        tar.extractall(CACHE)
    return src_root


def ensure_alsa() -> tuple[Path, Path, Path]:
    """Returns (include_dir, amd64_lib_dir, arm64_lib_dir)."""
    inc_root = CACHE / "alsa-dev"
    amd_root = CACHE / "alsa-amd64"
    arm_root = CACHE / "alsa-arm64"
    if not (inc_root / "usr" / "include" / "alsa" / "asoundlib.h").exists():
        deb = download(ALSA_DEV_DEB, CACHE / "libasound2-dev-jammy.deb")
        extract_deb(deb, inc_root)
    if not list((amd_root / "usr").rglob("libasound.so*")):
        deb = download(ALSA_LIB_AMD64, CACHE / "libasound2-amd64-jammy.deb")
        extract_deb(deb, amd_root)
    if not list((arm_root / "usr").rglob("libasound.so*")):
        deb = download(ALSA_LIB_ARM64, CACHE / "libasound2-arm64-jammy.deb")
        extract_deb(deb, arm_root)

    inc = inc_root / "usr" / "include"
    amd_lib = next((p.parent for p in amd_root.rglob("libasound.so.2")), None)
    arm_lib = next((p.parent for p in arm_root.rglob("libasound.so.2")), None)
    if amd_lib is None or arm_lib is None:
        raise RuntimeError(f"libasound.so.2 not found (amd={amd_lib} arm={arm_lib})")
    # Zig looks for libasound.so; Ubuntu only ships the SONAME.
    for lib_dir in (amd_lib, arm_lib):
        unversioned = lib_dir / "libasound.so"
        versioned = lib_dir / "libasound.so.2"
        if not unversioned.exists() and versioned.exists():
            shutil.copy2(versioned, unversioned)
    return inc, amd_lib, arm_lib


def run(cmd: list[str], cwd: Path | None = None) -> None:
    log("  $ " + " ".join(cmd))
    subprocess.check_call(cmd, cwd=cwd)


def compile_windows(zig: Path, src: Path, target: str, dest: Path) -> None:
    dest.parent.mkdir(parents=True, exist_ok=True)
    cmd = [
        str(zig),
        "c++",
        "-shared",
        "-target",
        target,
        "-std=c++11",
        "-O2",
        "-DNDEBUG",
        "-D__WINDOWS_MM__",
        "-DRTMIDI_EXPORT",
        str(src / "RtMidi.cpp"),
        str(src / "rtmidi_c.cpp"),
        "-lwinmm",
        "-o",
        str(dest),
    ]
    run(cmd)
    # Zig also emits .lib/.pdb next to the DLL; keep only the shared library in bin/.
    for extra in (dest.with_suffix(".lib"), dest.with_suffix(".pdb"), dest.parent / "RtMidi.lib", dest.parent / "rtmidi.pdb"):
        if extra.exists() and extra != dest:
            extra.unlink()


def compile_linux(
    zig: Path, src: Path, target: str, inc: Path, lib_dir: Path, dest: Path
) -> None:
    dest.parent.mkdir(parents=True, exist_ok=True)
    cmd = [
        str(zig),
        "c++",
        "-shared",
        "-target",
        target,
        "-std=c++11",
        "-O2",
        "-fPIC",
        "-DNDEBUG",
        "-D__LINUX_ALSA__",
        "-DRTMIDI_EXPORT",
        f"-I{inc}",
        f"-L{lib_dir}",
        str(src / "RtMidi.cpp"),
        str(src / "rtmidi_c.cpp"),
        "-lasound",
        "-lpthread",
        "-ldl",
        "-Wl,-soname,librtmidi.so",
        "-o",
        str(dest),
    ]
    run(cmd)
    stripped = dest.with_suffix(dest.suffix + ".stripped")
    try:
        run([str(zig), "objcopy", "--strip-all", str(dest), str(stripped)])
        if stripped.exists() and stripped.stat().st_size > 0:
            stripped.replace(dest)
    except subprocess.CalledProcessError:
        if stripped.exists():
            stripped.unlink()


def fetch_macos_dylib(dest: Path) -> None:
    dest.parent.mkdir(parents=True, exist_ok=True)
    bottle = download(
        BREW_MAC_ARM64,
        CACHE / "rtmidi-macos-arm64.tar.gz",
        headers={
            "Authorization": "Bearer QQ==",
            "Accept": "application/vnd.oci.image.layer.v1.tar+gzip",
            "User-Agent": "Cue2-rtmidi-build",
        },
    )
    extract_dir = CACHE / "rtmidi-macos-extract"
    if extract_dir.exists():
        shutil.rmtree(extract_dir)
    extract_dir.mkdir(parents=True)
    # Homebrew bottles may be gzip or raw tar
    try:
        with tarfile.open(bottle, "r:gz") as tar:
            tar.extractall(extract_dir)
    except tarfile.ReadError:
        with tarfile.open(bottle, "r:*") as tar:
            tar.extractall(extract_dir)
    dylibs = list(extract_dir.rglob("librtmidi*.dylib"))
    if not dylibs:
        raise RuntimeError(f"no dylib in bottle; contents={list(extract_dir.rglob('*'))[:40]}")
    # Prefer the versioned real file over a symlink
    real = None
    for p in dylibs:
        if p.is_symlink():
            continue
        if p.name.startswith("librtmidi."):
            real = p
            break
    if real is None:
        real = dylibs[0]
    shutil.copy2(real, dest)
    log(f"  macos dylib from {real} -> {dest} ({dest.stat().st_size} bytes)")


def sha256(path: Path) -> str:
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def main() -> int:
    CACHE.mkdir(parents=True, exist_ok=True)
    log(f"cache: {CACHE}")

    zig = ensure_zig()
    log(f"zig: {zig}")
    src = ensure_rtmidi_src()
    log(f"rtmidi src: {src}")
    inc, amd_lib, arm_lib = ensure_alsa()
    log(f"alsa include: {inc}")
    log(f"alsa amd64: {amd_lib}")
    log(f"alsa arm64: {arm_lib}")

    outs = {
        "win64": ROOT / "bin" / "win64" / "rtmidi.dll",
        "winarm64": ROOT / "bin" / "winarm64" / "rtmidi.dll",
        "linux64": ROOT / "bin" / "linux64" / "librtmidi.so",
        "linuxarm64": ROOT / "bin" / "linuxarm64" / "librtmidi.so",
        "macos": ROOT / "bin" / "macos" / "librtmidi.dylib",
    }

    compile_windows(zig, src, "x86_64-windows-gnu", outs["win64"])
    compile_windows(zig, src, "aarch64-windows-gnu", outs["winarm64"])
    compile_linux(zig, src, "x86_64-linux-gnu", inc, amd_lib, outs["linux64"])
    compile_linux(zig, src, "aarch64-linux-gnu", inc, arm_lib, outs["linuxarm64"])
    fetch_macos_dylib(outs["macos"])

    log("\nBuilt / fetched:")
    for name, path in outs.items():
        if not path.exists():
            log(f"  MISSING {name}: {path}")
            return 1
        log(f"  {name:12} {path.stat().st_size:8}  {sha256(path)[:16]}  {path}")
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except Exception as ex:
        log(f"ERROR: {ex}")
        raise
