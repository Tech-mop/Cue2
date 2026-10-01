#!/usr/bin/env python3
# SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
# SPDX-License-Identifier: MIT
"""
Build a Cue2-patched SDL 3.4.2 shared library into bin/{platform}.

Raises the discrete-channel cap from 8 to 64 (DVS, PlayAUDIO12).
Windows and Linux are cross-compiled with Zig. macOS is a native build
and is produced only on a Mac.

Usage (from repo root):
  python tools/build-sdl-natives.py
  python tools/build-sdl-natives.py --target macos
  python tools/build-sdl-natives.py --target win64
"""

from __future__ import annotations

import argparse
import gzip
import io
import json
import os
import platform
import shlex
import shutil
import stat
import subprocess
import sys
import tarfile
import urllib.request
import zipfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "tools" / "sdl"))
from apply_64ch import apply as apply_64ch  # noqa: E402

CACHE = Path(os.environ.get("TEMP", "/tmp")) / "cue2-sdl-build"
ZIG_VER = "0.14.1"
CMAKE_VER = "3.31.6"
SDL_VER = "3.4.2"
SDL_URL = f"https://github.com/libsdl-org/SDL/archive/refs/tags/release-{SDL_VER}.tar.gz"
NINJA_VER = "1.12.1"

# Jammy glibc headers are not used; Zig supplies libc. These -dev packages
# are the audio/video backends SDL dlopens at runtime.
LINUX_REQUIRED = (
    "libasound2-dev",
    "libpulse-dev",
    "libpipewire-0.3-dev",
    "libx11-dev",
    "libxext-dev",
    "libxrandr-dev",
    "libxcursor-dev",
    "libxi-dev",
    "libxfixes-dev",
    "libxss-dev",
    "libxrender-dev",
    "libxxf86vm-dev",
    "libxtst-dev",
)
LINUX_OPTIONAL = (
    "libwayland-dev",
    "libxkbcommon-dev",
    "libegl1-mesa-dev",
    "libdecor-0-dev",
    "libdrm-dev",
)
SKIP_DEPS = {
    "libc6",
    "libc6-dev",
    "libc-dev-bin",
    "linux-libc-dev",
    "gcc",
    "g++",
    "cpp",
    "perl",
    "perl-base",
    "debconf",
    "dpkg",
    "dpkg-dev",
    "pkg-config",
    "pkgconf",
    "python3",
    "python3-minimal",
    "manpages",
    "manpages-dev",
    "adduser",
    "sensible-utils",
}

TARGETS = {
    "win64": {"zig": "x86_64-windows-gnu", "system": "Windows", "proc": "AMD64", "out": "SDL3.dll"},
    "winarm64": {"zig": "aarch64-windows-gnu", "system": "Windows", "proc": "ARM64", "out": "SDL3.dll"},
    "linux64": {"zig": "x86_64-linux-gnu", "system": "Linux", "proc": "x86_64", "arch": "amd64", "out": "libSDL3.so"},
    "linuxarm64": {"zig": "aarch64-linux-gnu", "system": "Linux", "proc": "aarch64", "arch": "arm64", "out": "libSDL3.so"},
    "macos": {"out": "libSDL3.dylib"},
}


def log(msg: str) -> None:
    print(msg, flush=True)


def host_os() -> str:
    if sys.platform == "win32":
        return "windows"
    if sys.platform == "darwin":
        return "macos"
    return "linux"


def host_arch() -> str:
    machine = platform.machine().lower()
    if machine in ("arm64", "aarch64"):
        return "arm64"
    return "x64"


def download(url: str, dest: Path, headers: dict[str, str] | None = None) -> Path:
    dest.parent.mkdir(parents=True, exist_ok=True)
    if dest.exists() and dest.stat().st_size > 0:
        log(f"  cached {dest.name}")
        return dest
    log(f"  downloading {url}")
    req = urllib.request.Request(url, headers=headers or {"User-Agent": "Cue2-sdl-build"})
    with urllib.request.urlopen(req, timeout=300) as resp, open(dest, "wb") as out:
        shutil.copyfileobj(resp, out)
    return dest


def unquarantine(path: Path) -> None:
    if host_os() != "macos" or not path.exists():
        return
    subprocess.run(["xattr", "-dr", "com.apple.quarantine", str(path)], check=False, capture_output=True)


def extract_zip(zpath: Path, dest: Path) -> None:
    dest.mkdir(parents=True, exist_ok=True)
    with zipfile.ZipFile(zpath) as zf:
        zf.extractall(dest)
    unquarantine(dest)


def extract_tar(tpath: Path, dest: Path) -> None:
    dest.mkdir(parents=True, exist_ok=True)
    mode = "r:gz"
    name = tpath.name
    if name.endswith(".tar.xz") or name.endswith(".txz"):
        mode = "r:xz"
    elif name.endswith(".tar.bz2"):
        mode = "r:bz2"
    with tarfile.open(tpath, mode) as tf:
        tf.extractall(dest)
    unquarantine(dest)


def chmod_x(path: Path) -> None:
    try:
        path.chmod(path.stat().st_mode | stat.S_IEXEC | stat.S_IXGRP | stat.S_IXOTH)
    except OSError:
        pass


def find_named(root: Path, name: str) -> Path:
    matches = [p for p in root.rglob(name) if p.is_file() and p.name == name]
    if not matches:
        raise SystemExit(f"{name} not found under {root}")
    # CMake.app/Contents/MacOS/cmake is the GUI launcher, not the tool.
    matches.sort(key=lambda p: (0 if p.parent.name == "bin" else 1, len(p.parts)))
    chmod_x(matches[0])
    return matches[0]


def zig_archive_name() -> str:
    os_name = {"windows": "windows", "macos": "macos", "linux": "linux"}[host_os()]
    arch = "aarch64" if host_arch() == "arm64" else "x86_64"
    ext = "zip" if host_os() == "windows" else "tar.xz"
    return f"zig-{arch}-{os_name}-{ZIG_VER}.{ext}"


def ensure_zig() -> Path:
    exe_name = "zig.exe" if host_os() == "windows" else "zig"
    found = list(CACHE.glob(f"zig-*-{ZIG_VER}/{exe_name}"))
    if found:
        return found[0]
    # Reuse a Windows zig left by build-rtmidi-natives.py when this host is Windows.
    if host_os() == "windows":
        reused = Path(os.environ.get("TEMP", "/tmp")) / "cue2-rtmidi-build" / f"zig-x86_64-windows-{ZIG_VER}" / "zig.exe"
        if reused.exists():
            return reused
    archive = zig_archive_name()
    url = f"https://ziglang.org/download/{ZIG_VER}/{archive}"
    packed = download(url, CACHE / archive)
    log(f"  extracting {archive}")
    if archive.endswith(".zip"):
        extract_zip(packed, CACHE)
    else:
        extract_tar(packed, CACHE)
    found = list(CACHE.glob(f"zig-*-{ZIG_VER}/{exe_name}"))
    if not found:
        raise SystemExit(f"{exe_name} not found after extracting {archive}")
    chmod_x(found[0])
    return found[0]


def ensure_cmake() -> Path:
    on_path = shutil.which("cmake")
    if on_path:
        return Path(on_path)
    os_tag = {"windows": "windows-x86_64", "macos": "macos-universal", "linux": "linux-x86_64" if host_arch() == "x64" else "linux-aarch64"}[host_os()]
    ext = "zip" if host_os() == "windows" else "tar.gz"
    name = f"cmake-{CMAKE_VER}-{os_tag}.{ext}"
    url = f"https://github.com/Kitware/CMake/releases/download/v{CMAKE_VER}/{name}"
    dest_root = CACHE / f"cmake-{CMAKE_VER}-{os_tag}"
    exe_name = "cmake.exe" if host_os() == "windows" else "cmake"
    if dest_root.exists():
        try:
            return find_named(dest_root, exe_name)
        except SystemExit:
            pass
    packed = download(url, CACHE / name)
    log(f"  extracting {name}")
    if ext == "zip":
        extract_zip(packed, CACHE)
    else:
        extract_tar(packed, CACHE)
    return find_named(CACHE, exe_name)


def ensure_ninja() -> Path:
    on_path = shutil.which("ninja")
    if on_path:
        return Path(on_path)
    os_tag = {"windows": "win", "macos": "mac", "linux": "linux"}[host_os()]
    name = f"ninja-{os_tag}.zip"
    url = f"https://github.com/ninja-build/ninja/releases/download/v{NINJA_VER}/{name}"
    dest = CACHE / f"ninja-{os_tag}"
    exe_name = "ninja.exe" if host_os() == "windows" else "ninja"
    exe = dest / exe_name
    if exe.exists():
        chmod_x(exe)
        return exe
    packed = download(url, CACHE / name)
    extract_zip(packed, dest)
    if not exe.exists():
        return find_named(dest, exe_name)
    chmod_x(exe)
    unquarantine(exe)
    return exe


def relocate_homebrew_placeholders(root: Path) -> None:
    """Rewrite @@HOMEBREW_CELLAR@@ / @@HOMEBREW_PREFIX@@ in bottled Mach-O files."""
    if host_os() != "macos" or not root.exists():
        return
    cellar = str(root)
    for path in root.rglob("*"):
        if not path.is_file() or path.is_symlink():
            continue
        try:
            magic = path.read_bytes()[:4]
        except OSError:
            continue
        if magic not in (b"\xcf\xfa\xed\xfe", b"\xca\xfe\xba\xbe", b"\xfe\xed\xfa\xcf"):
            continue
        try:
            listing = subprocess.check_output(["otool", "-L", str(path)], text=True, stderr=subprocess.DEVNULL)
        except subprocess.CalledProcessError:
            continue
        id_line = subprocess.check_output(["otool", "-D", str(path)], text=True, stderr=subprocess.DEVNULL)
        for raw in [id_line] + listing.splitlines()[1:]:
            dep = raw.strip().split(" ", 1)[0].strip(":")
            if "@@HOMEBREW_CELLAR@@" in dep:
                new = dep.replace("@@HOMEBREW_CELLAR@@", cellar)
                flag = "-id" if ":" in raw or dep in id_line else "-change"
                if flag == "-id":
                    subprocess.run(["install_name_tool", "-id", new, str(path)], check=False)
                else:
                    subprocess.run(["install_name_tool", "-change", dep, new, str(path)], check=False)
            elif "@@HOMEBREW_PREFIX@@" in dep:
                new = dep.replace("@@HOMEBREW_PREFIX@@", cellar)
                subprocess.run(["install_name_tool", "-change", dep, new, str(path)], check=False)
        subprocess.run(["codesign", "--force", "--sign", "-", str(path)], check=False, capture_output=True)


def brew_bottle(formula: str, binary_name: str) -> Path | None:
    """Download a Homebrew bottle and return a host binary from it, if the API answers."""
    if host_os() != "macos":
        return None
    dest = CACHE / f"brew-{formula}"
    found = list(dest.rglob(binary_name)) if dest.exists() else []
    found = [p for p in found if p.is_file()]
    if found:
        relocate_homebrew_placeholders(dest)
        chmod_x(found[0])
        return found[0]
    api = f"https://formulae.brew.sh/api/formula/{formula}.json"
    try:
        raw = download(api, CACHE / f"{formula}.json", headers={"User-Agent": "Cue2-sdl-build"})
        meta = json.loads(raw.read_text(encoding="utf-8"))
    except Exception as ex:
        log(f"  brew formula {formula} unavailable ({ex})")
        return None
    files = (((meta.get("bottle") or {}).get("stable") or {}).get("files") or {})
    preferred = (
        "arm64_sequoia",
        "arm64_sonoma",
        "arm64_ventura",
        "arm64_tahoe",
        "sonoma",
        "ventura",
    ) if host_arch() == "arm64" else ("sonoma", "ventura", "x86_64_linux")
    entry = None
    for key in preferred:
        if key in files:
            entry = files[key]
            break
    if entry is None:
        for key, value in files.items():
            if host_arch() == "arm64" and key.startswith("arm64"):
                entry = value
                break
    if not entry or "url" not in entry:
        log(f"  no {formula} bottle for this Mac")
        return None
    blob = download(
        entry["url"],
        CACHE / f"{formula}-bottle.tar.gz",
        headers={
            "Authorization": "Bearer QQ==",
            "Accept": "application/vnd.oci.image.layer.v1.tar+gzip",
            "User-Agent": "Cue2-sdl-build",
        },
    )
    if dest.exists():
        shutil.rmtree(dest)
    try:
        extract_tar(blob, dest)
    except tarfile.ReadError:
        with tarfile.open(blob, "r:*") as tf:
            tf.extractall(dest)
        unquarantine(dest)
    relocate_homebrew_placeholders(dest)
    bins = [p for p in dest.rglob(binary_name) if p.is_file()]
    if not bins:
        log(f"  {binary_name} not in {formula} bottle")
        return None
    chmod_x(bins[0])
    unquarantine(bins[0])
    return bins[0]


def ensure_pkg_config() -> Path | None:
    for name in ("pkg-config", "pkgconf"):
        found = shutil.which(name)
        if found:
            return Path(found)
    bottled = brew_bottle("pkgconf", "pkgconf")
    if bottled is None:
        return None
    alias = bottled.parent / "pkg-config"
    if not alias.exists():
        try:
            alias.symlink_to(bottled.name)
        except OSError:
            shutil.copy2(bottled, alias)
            chmod_x(alias)
    return alias if alias.exists() else bottled


def ensure_wayland_scanner() -> Path | None:
    found = shutil.which("wayland-scanner")
    if found:
        return Path(found)
    return brew_bottle("wayland", "wayland-scanner")


def extract_sdl() -> Path:
    src = CACHE / f"SDL-release-{SDL_VER}"
    marker = src / "CMakeLists.txt"
    if marker.exists():
        return src
    tgz = download(SDL_URL, CACHE / f"SDL-release-{SDL_VER}.tar.gz")
    log(f"  extracting {tgz.name}")
    with tarfile.open(tgz, "r:gz") as tf:
        tf.extractall(CACHE)
    if not marker.exists():
        raise SystemExit(f"SDL source missing CMakeLists.txt: {src}")
    return src


def write_launcher(path: Path, command: list[str]) -> Path:
    path.parent.mkdir(parents=True, exist_ok=True)
    if host_os() == "windows":
        quoted = " ".join(f'"{part}"' if " " in part else part for part in command)
        path.write_text(f"@echo off\r\n{quoted} %*\r\n", encoding="ascii")
        return path
    body = "#!/bin/sh\nexec " + " ".join(shlex.quote(part) for part in command) + ' "$@"\n'
    path.write_text(body, encoding="utf-8")
    chmod_x(path)
    return path


def write_windres(zig: Path) -> Path:
    script = CACHE / "zig-windres.py"
    script.write_text(
        "import subprocess, sys\n"
        "from pathlib import Path\n"
        f"zig = Path(r'''{zig}''')\n"
        "args = sys.argv[1:]\n"
        "defs, includes, inp, out = [], [], None, None\n"
        "i = 0\n"
        "while i < len(args):\n"
        "    a = args[i]\n"
        "    if a == '-O' and i + 1 < len(args):\n"
        "        i += 2\n"
        "        continue\n"
        "    if a.startswith('-D'):\n"
        "        defs += ['/d', a[2:] if len(a) > 2 else args[i + 1]]\n"
        "        i += 1 if len(a) > 2 else 2\n"
        "        continue\n"
        "    if a.startswith('-I'):\n"
        "        includes += ['/i', a[2:] if len(a) > 2 else args[i + 1]]\n"
        "        i += 1 if len(a) > 2 else 2\n"
        "        continue\n"
        "    if a.startswith('-o') and len(a) > 2:\n"
        "        out = a[2:]\n"
        "        i += 1\n"
        "        continue\n"
        "    if not a.startswith('-'):\n"
        "        if inp is None:\n"
        "            inp = a\n"
        "        else:\n"
        "            out = a\n"
        "    i += 1\n"
        "if not inp or not out:\n"
        "    sys.exit('zig-windres: need input.rc and output')\n"
        "cmd = [str(zig), 'rc'] + defs + includes + ['/fo', out, inp]\n"
        "sys.exit(subprocess.call(cmd))\n",
        encoding="utf-8",
    )
    launcher = CACHE / ("zig-windres.cmd" if host_os() == "windows" else "zig-windres.sh")
    return write_launcher(launcher, [sys.executable, str(script)])


def cmake_path(path: Path) -> str:
    return str(path).replace("\\", "/")


def cmake_configure(cmake: Path, ninja: Path, src: Path, build_dir: Path, extra: list[str], env: dict[str, str]) -> None:
    if build_dir.exists():
        shutil.rmtree(build_dir)
    build_dir.mkdir(parents=True)
    env = env.copy()
    env["PATH"] = str(ninja.parent) + os.pathsep + env.get("PATH", "")
    cmd = [
        str(cmake),
        "-S",
        str(src),
        "-B",
        str(build_dir),
        "-G",
        "Ninja",
        "-DCMAKE_BUILD_TYPE=Release",
        "-DSDL_SHARED=ON",
        "-DSDL_STATIC=OFF",
        "-DSDL_TEST_LIBRARY=OFF",
        "-DSDL_TESTS=OFF",
        "-DSDL_EXAMPLES=OFF",
        "-DSDL_INSTALL=OFF",
        "-DSDL_DEPS_SHARED=ON",
        "-DSDL_JACK=OFF",
        "-DSDL_SNDIO=OFF",
        *extra,
    ]
    log("  cmake configure")
    subprocess.check_call(cmd, env=env, cwd=str(src))
    log("  cmake build")
    subprocess.check_call([str(cmake), "--build", str(build_dir), "--config", "Release", "--parallel"], env=env)


def enabled_defines(build_dir: Path, prefix: str) -> list[str]:
    headers = list(build_dir.rglob("SDL_build_config.h"))
    if not headers:
        return []
    text = headers[0].read_text(encoding="utf-8", errors="replace")
    names = []
    needle = f"#define {prefix}"
    for line in text.splitlines():
        line = line.strip()
        if line.startswith(needle) and line.endswith("1"):
            names.append(line.split()[1].removeprefix(prefix))
    return names


def require_drivers(build_dir: Path, audio: tuple[str, ...], video: tuple[str, ...] = ()) -> None:
    audio_on = set(enabled_defines(build_dir, "SDL_AUDIO_DRIVER_"))
    video_on = set(enabled_defines(build_dir, "SDL_VIDEO_DRIVER_"))
    log(f"  audio drivers: {', '.join(sorted(audio_on)) or '(none)'}")
    if video_on:
        log(f"  video drivers: {', '.join(sorted(video_on))}")
    missing = [name for name in audio if name not in audio_on]
    missing += [name for name in video if name not in video_on]
    if missing:
        raise SystemExit(f"SDL build is missing drivers: {', '.join(missing)}")


def elf_needed(path: Path) -> list[str]:
    data = path.read_bytes()
    if data[:4] != b"\x7fELF":
        raise SystemExit(f"{path} is not ELF")
    if data[4] != 2 or data[5] != 1:
        raise SystemExit(f"{path}: expected ELF64 little-endian")
    e_shoff = int.from_bytes(data[40:48], "little")
    e_shentsize = int.from_bytes(data[58:60], "little")
    e_shnum = int.from_bytes(data[60:62], "little")

    def section(i: int) -> bytes:
        start = e_shoff + i * e_shentsize
        return data[start : start + e_shentsize]

    dyn_off = dyn_size = strtab_off = 0
    for i in range(e_shnum):
        sh = section(i)
        sh_type = int.from_bytes(sh[4:8], "little")
        sh_offset = int.from_bytes(sh[24:32], "little")
        sh_size = int.from_bytes(sh[32:40], "little")
        sh_link = int.from_bytes(sh[40:44], "little")
        if sh_type == 6:  # SHT_DYNAMIC
            dyn_off, dyn_size = sh_offset, sh_size
            strtab = section(sh_link)
            strtab_off = int.from_bytes(strtab[24:32], "little")
            break
    if not dyn_off:
        raise SystemExit(f"{path}: no dynamic section")
    needed: list[str] = []
    pos = dyn_off
    end = dyn_off + dyn_size
    while pos + 16 <= end:
        tag = int.from_bytes(data[pos : pos + 8], "little", signed=True)
        val = int.from_bytes(data[pos + 8 : pos + 16], "little")
        pos += 16
        if tag == 0:
            break
        if tag == 1:  # DT_NEEDED
            start = strtab_off + val
            stop = data.find(b"\x00", start)
            needed.append(data[start:stop].decode("ascii"))
    return needed


def verify_artifact(path: Path) -> None:
    if not path.is_file() or path.stat().st_size < 500_000:
        raise SystemExit(f"built library looks too small: {path}")
    name = path.name
    blob = path.read_bytes()
    if name.endswith(".dll"):
        if blob[:2] != b"MZ":
            raise SystemExit(f"{path} is not a PE DLL")
        return
    if name.endswith(".so"):
        needed = elf_needed(path)
        log(f"  NEEDED: {', '.join(needed) or '(none)'}")
        allowed = {
            "libc.so.6",
            "libm.so.6",
            "libdl.so.2",
            "libpthread.so.0",
            "librt.so.1",
            "ld-linux-x86-64.so.2",
            "ld-linux-aarch64.so.1",
        }
        extra = [item for item in needed if item not in allowed]
        if extra:
            raise SystemExit(f"{path} links extra libraries (SDL_DEPS_SHARED should dlopen them): {extra}")
        return
    if name.endswith(".dylib") and blob[:4] not in (b"\xcf\xfa\xed\xfe", b"\xfe\xed\xfa\xcf"):
        raise SystemExit(f"{path} is not a Mach-O dylib")


def strip_linux(zig: Path, path: Path) -> None:
    tmp = CACHE / f"{path.parent.name}-{path.name}.stripped"
    subprocess.check_call([str(zig), "objcopy", "--strip-all", str(path), str(tmp)])
    shutil.move(str(tmp), str(path))
    chmod_x(path)
    log(f"  stripped {path} ({path.stat().st_size} bytes)")


def copy_built(build_dir: Path, names: tuple[str, ...], dest: Path) -> None:
    real: list[Path] = []
    linked: list[Path] = []
    for name in names:
        for cand in build_dir.rglob(name):
            if not cand.is_file():
                continue
            if cand.is_symlink():
                linked.append(cand.resolve())
            else:
                real.append(cand)
    pool = real or linked
    if not pool:
        raise SystemExit(f"library {names} not produced in {build_dir}")
    built = max(pool, key=lambda p: p.stat().st_size)
    dest.parent.mkdir(parents=True, exist_ok=True)
    shutil.copy2(built, dest)
    log(f"  copied {built.name} -> {dest} ({dest.stat().st_size} bytes)")
    verify_artifact(dest)


def build_macos(cmake: Path, ninja: Path, src: Path, dest: Path) -> None:
    arch = "arm64" if host_arch() == "arm64" else "x86_64"
    build_dir = CACHE / f"build-macos-{arch}"
    cmake_configure(
        cmake,
        ninja,
        src,
        build_dir,
        [
            f"-DCMAKE_OSX_ARCHITECTURES={arch}",
            "-DCMAKE_OSX_DEPLOYMENT_TARGET=11.0",
            "-DSDL_FRAMEWORK=OFF",
        ],
        os.environ.copy(),
    )
    require_drivers(build_dir, ("COREAUDIO",), ("COCOA",))
    copy_built(build_dir, ("libSDL3.0.dylib", "libSDL3.dylib"), dest)
    subprocess.check_call(["install_name_tool", "-id", "@rpath/libSDL3.dylib", str(dest)])
    subprocess.check_call(["codesign", "--force", "--sign", "-", str(dest)])
    smoke_test_macos(src, build_dir, dest)


def smoke_test_macos(src: Path, build_dir: Path, dylib: Path) -> None:
    test_c = CACHE / "sdl_64ch_smoke.c"
    test_c.write_text(
        r"""
#include <SDL3/SDL.h>
#include <stdio.h>

static int fail(const char *msg) {
    fprintf(stderr, "%s: %s\n", msg, SDL_GetError());
    return 1;
}

static int convert(int src_ch, int dst_ch, const float *in, int in_n, float *out, int out_n) {
    SDL_AudioSpec src = { SDL_AUDIO_F32LE, src_ch, 48000 };
    SDL_AudioSpec dst = { SDL_AUDIO_F32LE, dst_ch, 48000 };
    SDL_AudioStream *stream = SDL_CreateAudioStream(&src, &dst);
    if (!stream) return fail("SDL_CreateAudioStream");
    if (!SDL_PutAudioStreamData(stream, in, in_n * (int)sizeof(float))) return fail("put");
    if (!SDL_FlushAudioStream(stream)) return fail("flush");
    int got = SDL_GetAudioStreamData(stream, out, out_n * (int)sizeof(float));
    SDL_DestroyAudioStream(stream);
    if (got != out_n * (int)sizeof(float)) {
        fprintf(stderr, "expected %d bytes, got %d\n", out_n * (int)sizeof(float), got);
        return 1;
    }
    return 0;
}

int main(void) {
    if (!SDL_Init(SDL_INIT_AUDIO)) return fail("SDL_Init");
    float in9[9];
    for (int i = 0; i < 9; i++) in9[i] = (float)(i + 1);
    float out2[2] = {0, 0};
    if (convert(9, 2, in9, 9, out2, 2)) return 1;
    if (out2[0] != 1.f || out2[1] != 2.f) {
        fprintf(stderr, "9->2 produced %f %f\n", out2[0], out2[1]);
        return 1;
    }
    float in2[2] = {3.f, 4.f};
    float out9[9];
    for (int i = 0; i < 9; i++) out9[i] = -1.f;
    if (convert(2, 9, in2, 2, out9, 9)) return 1;
    if (out9[0] != 3.f || out9[1] != 4.f) {
        fprintf(stderr, "2->9 produced %f %f\n", out9[0], out9[1]);
        return 1;
    }
    for (int i = 2; i < 9; i++) {
        if (out9[i] != 0.f) {
            fprintf(stderr, "silence[%d]=%f\n", i, out9[i]);
            return 1;
        }
    }
    SDL_AudioSpec wide = { SDL_AUDIO_F32LE, 64, 48000 };
    SDL_AudioStream *ok = SDL_CreateAudioStream(&wide, &wide);
    if (!ok) return fail("64-channel stream");
    SDL_DestroyAudioStream(ok);
    wide.channels = 65;
    SDL_AudioStream *rejected = SDL_CreateAudioStream(&wide, &wide);
    if (rejected) {
        fprintf(stderr, "65 channels was accepted\n");
        return 1;
    }
    /* Dante path: 64 ch at 44100 into a 48000 device. ResampleFrame[] is only 8 deep. */
    SDL_AudioSpec src64 = { SDL_AUDIO_F32LE, 64, 44100 };
    SDL_AudioSpec dst64 = { SDL_AUDIO_F32LE, 64, 48000 };
    SDL_AudioStream *rs = SDL_CreateAudioStream(&src64, &dst64);
    if (!rs) return fail("64ch resample stream");
    float in64[64 * 256];
    for (int f = 0; f < 256; f++) {
        for (int c = 0; c < 64; c++) {
            in64[f * 64 + c] = (float)(c + 1);
        }
    }
    if (!SDL_PutAudioStreamData(rs, in64, (int)sizeof(in64))) return fail("64ch resample put");
    if (!SDL_FlushAudioStream(rs)) return fail("64ch resample flush");
    float out64[64 * 32];
    int got64 = SDL_GetAudioStreamData(rs, out64, (int)sizeof(out64));
    SDL_DestroyAudioStream(rs);
    if (got64 < 64 * (int)sizeof(float)) {
        fprintf(stderr, "64ch resample produced %d bytes\n", got64);
        return 1;
    }
    printf("sdl 64ch smoke ok\n");
    SDL_Quit();
    return 0;
}
""",
        encoding="utf-8",
    )
    includes = [f"-I{src / 'include'}"]
    for cfg in build_dir.rglob("SDL_build_config.h"):
        if cfg.parent.name == "SDL3":
            includes.append(f"-I{cfg.parent.parent}")
    test_bin = CACHE / "sdl_64ch_smoke"
    # install_name is @rpath/libSDL3.dylib; the test binary needs an LC_RPATH.
    cmd = [
        "clang",
        "-O2",
        *includes,
        str(test_c),
        str(dylib),
        "-Xlinker",
        "-rpath",
        "-Xlinker",
        str(dylib.parent),
        "-o",
        str(test_bin),
    ]
    log("  smoke test compile")
    subprocess.check_call(cmd)
    subprocess.check_call(["codesign", "--force", "--sign", "-", str(test_bin)])
    log("  smoke test run")
    subprocess.check_call([str(test_bin)])
    log("  sdl 64ch smoke ok")


def build_cross(zig: Path, cmake: Path, ninja: Path, src: Path, target: str, dest: Path, pkg_config: Path | None, wayland_scanner: Path | None) -> None:
    spec = TARGETS[target]
    zig_target = spec["zig"]
    extra_cc: list[str] = ["-target", zig_target]
    env = os.environ.copy()
    sysroot = None
    if spec["system"] == "Linux":
        sysroot = ensure_linux_sysroot(spec["arch"])
        triple = "x86_64-linux-gnu" if spec["arch"] == "amd64" else "aarch64-linux-gnu"
        extra_cc += [
            f"-I{sysroot / 'usr' / 'include'}",
            f"-I{sysroot / 'usr' / 'include' / triple}",
        ]
        pc_dirs = [
            sysroot / "usr" / "lib" / triple / "pkgconfig",
            sysroot / "usr" / "lib" / "pkgconfig",
            sysroot / "usr" / "share" / "pkgconfig",
        ]
        pc_path = os.pathsep.join(str(p) for p in pc_dirs if p.exists())
        env["PKG_CONFIG_SYSROOT_DIR"] = str(sysroot)
        env["PKG_CONFIG_LIBDIR"] = pc_path
        env["PKG_CONFIG_PATH"] = pc_path
        if pkg_config is not None:
            env["PATH"] = str(pkg_config.parent) + os.pathsep + env.get("PATH", "")
            env["PKG_CONFIG"] = str(pkg_config)
    if wayland_scanner is not None:
        env["PATH"] = str(wayland_scanner.parent) + os.pathsep + env.get("PATH", "")

    suffix = "cmd" if host_os() == "windows" else "sh"
    cc = write_launcher(CACHE / f"zig-cc-{target}.{suffix}", [str(zig), "cc", *extra_cc])
    cxx = write_launcher(CACHE / f"zig-cxx-{target}.{suffix}", [str(zig), "c++", *extra_cc])
    ar = write_launcher(CACHE / f"zig-ar.{suffix}", [str(zig), "ar"])
    ranlib = write_launcher(CACHE / f"zig-ranlib.{suffix}", [str(zig), "ranlib"])
    toolchain = CACHE / f"toolchain-{target}.cmake"
    lines = [
        f'set(CMAKE_SYSTEM_NAME {spec["system"]})',
        f'set(CMAKE_SYSTEM_PROCESSOR {spec["proc"]})',
        f'set(CMAKE_C_COMPILER "{cmake_path(cc)}")',
        f'set(CMAKE_CXX_COMPILER "{cmake_path(cxx)}")',
        f'set(CMAKE_AR "{cmake_path(ar)}")',
        f'set(CMAKE_RANLIB "{cmake_path(ranlib)}")',
    ]
    if spec["system"] == "Windows":
        windres = write_windres(zig)
        lines.append(f'set(CMAKE_RC_COMPILER "{cmake_path(windres)}")')
    if sysroot is not None:
        triple = "x86_64-linux-gnu" if spec["arch"] == "amd64" else "aarch64-linux-gnu"
        lines += [
            f'set(CMAKE_LIBRARY_ARCHITECTURE "{triple}")',
            f'set(CMAKE_FIND_ROOT_PATH "{cmake_path(sysroot)}")',
            "set(CMAKE_FIND_ROOT_PATH_MODE_PROGRAM NEVER)",
            "set(CMAKE_FIND_ROOT_PATH_MODE_LIBRARY ONLY)",
            "set(CMAKE_FIND_ROOT_PATH_MODE_INCLUDE ONLY)",
            "set(CMAKE_FIND_ROOT_PATH_MODE_PACKAGE ONLY)",
        ]
    toolchain.write_text("\n".join(lines) + "\n", encoding="utf-8")
    extra = [f"-DCMAKE_TOOLCHAIN_FILE={cmake_path(toolchain)}"]
    if spec["system"] == "Linux" and pkg_config is not None:
        extra.append(f"-DPKG_CONFIG_EXECUTABLE={cmake_path(pkg_config)}")
    build_dir = CACHE / f"build-{target}"
    cmake_configure(cmake, ninja, src, build_dir, extra, env)
    if spec["system"] == "Windows":
        require_drivers(build_dir, ("WASAPI",))
        copy_built(build_dir, ("SDL3.dll",), dest)
    else:
        require_drivers(build_dir, ("ALSA", "PULSEAUDIO"), ("X11",))
        drivers = set(enabled_defines(build_dir, "SDL_AUDIO_DRIVER_"))
        if "PIPEWIRE" not in drivers:
            log("  warning: PipeWire backend is off; ALSA still allows 64 channels")
        copy_built(build_dir, ("libSDL3.so.0", "libSDL3.so"), dest)
        strip_linux(zig, dest)


def ubuntu_indexes(arch: str) -> dict[str, dict[str, str]]:
    if arch == "amd64":
        base = "http://archive.ubuntu.com/ubuntu"
    else:
        base = "http://ports.ubuntu.com/ubuntu-ports"
    merged: dict[str, dict[str, str]] = {}
    for component in ("main", "universe"):
        url = f"{base}/dists/jammy/{component}/binary-{arch}/Packages.gz"
        packed = download(url, CACHE / f"Packages-jammy-{component}-{arch}.gz")
        text = gzip.open(packed, "rt", encoding="utf-8", errors="replace").read()
        for block in text.split("\n\n"):
            fields: dict[str, str] = {}
            key = ""
            for line in block.splitlines():
                if line.startswith(" ") and key:
                    fields[key] += " " + line.strip()
                    continue
                if ":" not in line:
                    continue
                key, value = line.split(":", 1)
                fields[key] = value.strip()
            name = fields.get("Package")
            if name and name not in merged:
                fields["_base"] = base
                merged[name] = fields
    return merged


def dep_names(depends: str) -> list[str]:
    names: list[str] = []
    for part in depends.split(","):
        alt = part.split("|")[0].strip()
        name = alt.split("(")[0].strip()
        if ":" in name:
            name = name.split(":", 1)[0].strip()
        if name and name not in SKIP_DEPS and not name.endswith("-doc") and not name.endswith("-dbg"):
            names.append(name)
    return names


def decompress_zstd(blob: bytes) -> bytes:
    """Inflate a zstd payload without the optional Python zstandard module."""
    try:
        import zstandard

        return zstandard.ZstdDecompressor().decompress(blob, max_output_size=256 * 1024 * 1024)
    except ImportError:
        pass
    zstd_bin = shutil.which("zstd")
    if zstd_bin is None:
        bottled = brew_bottle("zstd", "zstd")
        zstd_bin = str(bottled) if bottled is not None else None
    if zstd_bin is None:
        raise RuntimeError("zstd is required to extract Ubuntu .deb packages (no Python zstandard, no zstd binary)")
    proc = subprocess.run([zstd_bin, "-d", "-c"], input=blob, capture_output=True, check=False)
    if proc.returncode != 0:
        err = proc.stderr.decode("utf-8", "replace").strip()
        raise RuntimeError(f"zstd -d failed: {err or proc.returncode}")
    return proc.stdout


def extract_deb(deb_path: Path, out_dir: Path) -> None:
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
        bio: io.BytesIO | None = None
        if "zst" in name:
            bio = io.BytesIO(decompress_zstd(blob))
        elif name.endswith(".xz") or ".xz" in name:
            import lzma

            bio = io.BytesIO(lzma.decompress(blob))
        elif name.endswith(".gz") or name.endswith("gz"):
            bio = io.BytesIO(gzip.decompress(blob))
        if bio is None:
            raise RuntimeError(f"unsupported deb member {name}")
        with tarfile.open(fileobj=bio, mode="r:") as tar:
            tar.extractall(out_dir)
        return
    raise RuntimeError(f"no data.tar in {deb_path}")


def fix_sysroot_symlinks(root: Path) -> None:
    for path in root.rglob("*"):
        if not path.is_symlink():
            continue
        target = os.readlink(path)
        if not target.startswith("/"):
            continue
        local = root / target.lstrip("/")
        if not local.exists():
            continue
        relative = os.path.relpath(local, path.parent)
        path.unlink()
        path.symlink_to(relative)


def ensure_linux_sysroot(arch: str) -> Path:
    root = CACHE / f"sysroot-{arch}"
    marker = root / ".complete"
    index = ubuntu_indexes(arch)
    wanted: list[str] = []
    seen: set[str] = set()

    def add(name: str, required: bool) -> None:
        if name in seen or name in SKIP_DEPS:
            return
        meta = index.get(name)
        if meta is None:
            # LINUX_REQUIRED must exist. Recursive Depends often name virtuals
            # (python3:any, awk) that are not real packages in Packages.gz.
            if required and name in LINUX_REQUIRED:
                raise SystemExit(f"Ubuntu package not found: {name} ({arch})")
            log(f"  skipping missing package: {name}")
            return
        seen.add(name)
        wanted.append(name)
        for dep in dep_names(meta.get("Depends", "")):
            add(dep, True)

    for name in LINUX_REQUIRED:
        add(name, True)
    for name in LINUX_OPTIONAL:
        add(name, False)
    wanted_key = "\n".join(sorted(wanted)) + "\n"
    if marker.exists() and marker.read_text(encoding="ascii") == wanted_key:
        return root
    root.mkdir(parents=True, exist_ok=True)
    log(f"  linux {arch} sysroot packages: {len(wanted)}")
    for name in wanted:
        meta = index[name]
        filename = meta.get("Filename")
        if not filename:
            raise SystemExit(f"{name} has no Filename")
        deb = download(meta["_base"] + "/" + filename, CACHE / "debs" / arch / Path(filename).name)
        extract_deb(deb, root)
    fix_sysroot_symlinks(root)
    marker.write_text(wanted_key, encoding="ascii")
    return root


def selected_targets(requested: str) -> list[str]:
    if requested != "all":
        if requested == "macos" and host_os() != "macos":
            raise SystemExit("macOS SDL must be built on a Mac (CoreAudio).")
        return [requested]
    names = ["win64", "winarm64", "linux64", "linuxarm64"]
    if host_os() == "macos":
        names.insert(0, "macos")
    else:
        log("skipping macos: CoreAudio build needs a Mac")
    return names


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--target", choices=(*TARGETS.keys(), "all"), default="all")
    args = parser.parse_args()
    targets = selected_targets(args.target)

    CACHE.mkdir(parents=True, exist_ok=True)
    log(f"Cue2 SDL {SDL_VER} 64-channel native build")
    log(f"  cache {CACHE}")
    needs_cross = any(name != "macos" for name in targets)
    needs_linux = any(name.startswith("linux") for name in targets)
    zig = ensure_zig() if needs_cross else None
    cmake = ensure_cmake()
    ninja = ensure_ninja()
    src = extract_sdl()
    log("  applying 64-channel patch")
    apply_64ch(src)
    pkg_config = ensure_pkg_config() if needs_linux else None
    if needs_linux and pkg_config is None:
        raise SystemExit("pkg-config is required to enable Linux audio/video backends")
    wayland_scanner = ensure_wayland_scanner() if needs_linux else None
    log(f"  cmake {cmake}")
    log(f"  ninja {ninja}")
    if zig is not None:
        log(f"  zig {zig}")
    if pkg_config is not None:
        log(f"  pkg-config {pkg_config}")

    for name in targets:
        dest = ROOT / "bin" / name / TARGETS[name]["out"]
        log(f"== {name} ==")
        if name == "macos":
            build_macos(cmake, ninja, src, dest)
        else:
            assert zig is not None
            build_cross(zig, cmake, ninja, src, name, dest, pkg_config, wayland_scanner)
    log("done")
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except subprocess.CalledProcessError as ex:
        log(f"ERROR: command failed ({ex.returncode}): {ex.cmd}")
        sys.exit(ex.returncode or 1)
