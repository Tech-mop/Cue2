#!/usr/bin/env python3
# SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
# SPDX-License-Identifier: MIT
"""One-command Cue2 release export.

Creates per-OS folders, runs Godot --export-release for every preset, copies
FFmpeg/RtMidi natives, and writes the GitHub Release archives + latest.json.

macOS Developer ID / notarization is optional and not in this repository.
If tools/macos-sign-and-notarize.sh is present locally, it is used; otherwise
the .app is archived unsigned.

Usage (from repo root):

  python tools/export-all.py
  python tools/export-all.py --only macos-arm64,linux-x86_64
  python tools/export-all.py --skip-sign
  python tools/export-all.py --bump minor
  python tools/export-all.py --no-bump
  python tools/export-all.py --dist ~/Documents/MyFiles/Cue2_Home/Exports
  python tools/export-all.py --dry-run

See docs/export-packaging.md.
"""

from __future__ import annotations

import argparse
import os
import re
import shutil
import stat
import subprocess
import sys
import tarfile
import zipfile
from dataclasses import dataclass
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]

CLOUD_MARKERS = (
    "/Library/CloudStorage/",
    "/GoogleDrive-",
    "/Mobile Documents/",
    "\\GoogleDrive-",
)

WIN_CORE = (
    "avutil-61.dll",
    "avcodec-63.dll",
    "avformat-63.dll",
    "swresample-7.dll",
    "swscale-10.dll",
    "rtmidi.dll",
)


@dataclass(frozen=True)
class Target:
    """One Godot export preset mapped to a shipping archive."""

    id: str
    preset: str
    natives: str
    host: str
    archive: str
    templates: tuple[str, ...]


TARGETS: tuple[Target, ...] = (
    Target(
        "windows-x86_64",
        "Windows Desktop x86",
        "win64",
        "Cue2.exe",
        "zip",
        ("windows_release_x86_64.exe", "windows_release_x86_64_console.exe"),
    ),
    Target(
        "windows-arm64",
        "Windows Desktop arm",
        "winarm64",
        "Cue2.exe",
        "zip",
        ("windows_release_arm64.exe", "windows_release_arm64_console.exe"),
    ),
    Target(
        "macos-arm64",
        "macOS",
        "macos",
        "Cue2.app",
        "zip",
        ("macos.zip",),
    ),
    Target(
        "linux-x86_64",
        "Linux x86",
        "linux64",
        "Cue2.x86_64",
        "tar.gz",
        ("linux_release.x86_64",),
    ),
    Target(
        "linux-arm64",
        "Linux arm",
        "linuxarm64",
        "Cue2.arm64",
        "tar.gz",
        ("linux_release.arm64",),
    ),
)

TARGET_BY_ID = {t.id: t for t in TARGETS}


def log(msg: str = "") -> None:
    print(msg, flush=True)


def is_cloud_path(path: Path) -> bool:
    text = str(path.resolve())
    return any(marker in text for marker in CLOUD_MARKERS)


def run(cmd: list[str], *, cwd: Path | None = None, check: bool = True) -> subprocess.CompletedProcess[str]:
    log("  $ " + " ".join(cmd))
    return subprocess.run(cmd, cwd=cwd, check=check, text=True)


VersionTriple = tuple[int, int, int]
SEMVER_PREFIX_RE = re.compile(r"^(\d+)\.(\d+)\.(\d+)")
VERSION_DIR_RE = re.compile(r"^(\d+)\.(\d+)\.(\d+)$")
CUE2_ARCHIVE_RE = re.compile(r"^Cue2-(\d+)\.(\d+)\.(\d+)(?:-rc\.\d+)?-")
GIT_TAG_RE = re.compile(r"^v(\d+)\.(\d+)\.(\d+)")


def parse_triple(version: str) -> VersionTriple:
    match = SEMVER_PREFIX_RE.match(version.strip().lstrip("v"))
    if not match:
        raise SystemExit(f"Version must start with X.Y.Z: {version!r}")
    return int(match.group(1)), int(match.group(2)), int(match.group(3))


def format_triple(triple: VersionTriple) -> str:
    return f"{triple[0]}.{triple[1]}.{triple[2]}"


def bump_triple(triple: VersionTriple, kind: str) -> VersionTriple:
    major, minor, patch = triple
    if kind == "major":
        return major + 1, 0, 0
    if kind == "minor":
        return major, minor + 1, 0
    if kind == "patch":
        return major, minor, patch + 1
    raise SystemExit(f"Unknown bump kind: {kind}")


def read_version_cs_triple(root: Path) -> VersionTriple:
    text = (root / "Version.cs").read_text(encoding="utf-8")

    def field(name: str) -> int:
        match = re.search(rf"readonly int {name} = (\d+);", text)
        if not match:
            raise SystemExit(f"Could not read Version.{name} from {root / 'Version.cs'}")
        return int(match.group(1))

    return field("Major"), field("Minor"), field("Patch")


def read_version_cs(root: Path) -> str:
    return format_triple(read_version_cs_triple(root))


def numeric_version(version: str) -> str:
    return format_triple(parse_triple(version))


def collect_existing_versions(root: Path, exports_dir: Path) -> tuple[VersionTriple | None, list[str]]:
    """Highest version already used in Exports/ and git tags, plus human-readable sources."""
    found: dict[VersionTriple, list[str]] = {}

    def add(triple: VersionTriple, label: str) -> None:
        found.setdefault(triple, []).append(label)

    if exports_dir.is_dir():
        for item in exports_dir.iterdir():
            dir_match = VERSION_DIR_RE.match(item.name)
            if dir_match:
                add(
                    (int(dir_match.group(1)), int(dir_match.group(2)), int(dir_match.group(3))),
                    f"Exports/{item.name}",
                )
                continue
            archive_match = CUE2_ARCHIVE_RE.match(item.name)
            if archive_match:
                add(
                    (
                        int(archive_match.group(1)),
                        int(archive_match.group(2)),
                        int(archive_match.group(3)),
                    ),
                    f"Exports/{item.name}",
                )

    try:
        result = subprocess.run(
            ["git", "tag", "-l", "v*"],
            cwd=root,
            check=False,
            text=True,
            capture_output=True,
        )
    except OSError:
        result = None
    if result is not None and result.returncode == 0:
        for line in result.stdout.splitlines():
            tag = line.strip()
            tag_match = GIT_TAG_RE.match(tag)
            if tag_match:
                add(
                    (int(tag_match.group(1)), int(tag_match.group(2)), int(tag_match.group(3))),
                    f"git tag {tag}",
                )

    if not found:
        return None, []
    highest = max(found)
    return highest, found[highest]


@dataclass(frozen=True)
class VersionPlan:
    """Resolved export version and whether source files need stamping."""

    version: str
    code: str
    highest_existing: str | None
    existing_sources: list[str]
    reason: str
    stamp_source: bool


def plan_version(
    root: Path,
    exports_dir: Path,
    *,
    explicit: str | None,
    bump: str,
    no_bump: bool,
) -> VersionPlan:
    code_triple = read_version_cs_triple(root)
    code = format_triple(code_triple)
    highest, sources = collect_existing_versions(root, exports_dir)

    if explicit:
        version = numeric_version(explicit)
        return VersionPlan(
            version=version,
            code=code,
            highest_existing=format_triple(highest) if highest else None,
            existing_sources=sources,
            reason="from --version",
            stamp_source=parse_triple(version) != code_triple,
        )
    if no_bump:
        return VersionPlan(
            version=code,
            code=code,
            highest_existing=format_triple(highest) if highest else None,
            existing_sources=sources,
            reason="Version.cs (--no-bump)",
            stamp_source=False,
        )
    if highest is None or code_triple > highest:
        reason = "Version.cs (already ahead of exports/tags)" if highest else "Version.cs (no previous export or tag)"
        return VersionPlan(
            version=code,
            code=code,
            highest_existing=format_triple(highest) if highest else None,
            existing_sources=sources,
            reason=reason,
            stamp_source=False,
        )

    baseline = max(code_triple, highest)
    nxt = bump_triple(baseline, bump)
    return VersionPlan(
        version=format_triple(nxt),
        code=code,
        highest_existing=format_triple(highest),
        existing_sources=sources,
        reason=f"next {bump} after {format_triple(baseline)}",
        stamp_source=True,
    )


def stamp_version_cs(root: Path, triple: VersionTriple) -> None:
    path = root / "Version.cs"
    text = path.read_text(encoding="utf-8")
    for name, value in (("Major", triple[0]), ("Minor", triple[1]), ("Patch", triple[2])):
        updated, count = re.subn(
            rf"(readonly int {name} = )\d+;",
            rf"\g<1>{value};",
            text,
            count=1,
        )
        if count != 1:
            raise SystemExit(f"Could not stamp Version.{name} in {path}")
        text = updated
    path.write_text(text, encoding="utf-8")
    log(f"Stamped Version.cs to {format_triple(triple)}")


def stamp_project_godot(root: Path, version: str) -> None:
    path = root / "project.godot"
    text = path.read_text(encoding="utf-8")
    updated, count = re.subn(
        r'^config/version="[^"]+"',
        f'config/version="{version}"',
        text,
        count=1,
        flags=re.MULTILINE,
    )
    if count != 1:
        raise SystemExit(f"Could not stamp config/version in {path}")
    if updated != text:
        path.write_text(updated, encoding="utf-8")
        log(f"Stamped project.godot config/version to {version}")
    else:
        log(f"project.godot config/version already {version}")


def read_project_version(root: Path) -> str | None:
    text = (root / "project.godot").read_text(encoding="utf-8")
    match = re.search(r'^config/version="([^"]+)"', text, re.MULTILINE)
    return match.group(1) if match else None


def parse_preset_names(cfg: Path) -> list[str]:
    names: list[str] = []
    for line in cfg.read_text(encoding="utf-8").splitlines():
        if line.startswith("name="):
            names.append(line.split("=", 1)[1].strip().strip('"'))
    return names


def stamp_export_preset_versions(cfg: Path, version: str) -> None:
    """Keep Apple/Windows file versions in sync with Version.cs for this export."""
    numeric = numeric_version(version)
    win = f"{numeric}.0"
    replacements = {
        "application/file_version": win,
        "application/product_version": win,
        "application/short_version": numeric,
        "application/version": numeric,
    }
    lines = cfg.read_text(encoding="utf-8").splitlines(keepends=True)
    out: list[str] = []
    changed = False
    for line in lines:
        key = line.split("=", 1)[0].strip() if "=" in line else ""
        if key in replacements:
            newline = "\r\n" if line.endswith("\r\n") else "\n"
            new_line = f'{key}="{replacements[key]}"{newline}'
            if line != new_line:
                changed = True
            out.append(new_line)
        else:
            out.append(line)
    if changed:
        cfg.write_text("".join(out), encoding="utf-8")
        log(f"Stamped export_presets.cfg versions to {numeric}")
    else:
        log(f"export_presets.cfg versions already {numeric}")


def default_exports_parent() -> Path:
    env = os.environ.get("CUE2_EXPORTS")
    if env:
        return Path(env).expanduser()
    if os.name == "nt":
        preferred = Path(r"C:\MyFiles\Cue2_Home\Exports")
        if preferred.is_dir() or preferred.parent.is_dir():
            return preferred
        return Path.home() / "Cue2_Home" / "Exports"
    preferred = Path.home() / "Documents" / "MyFiles" / "Cue2_Home" / "Exports"
    if preferred.is_dir() or preferred.parent.is_dir():
        return preferred
    return Path.home() / "Cue2_Home" / "Exports"


def exports_parent(arg: str | None) -> Path:
    """Unversioned Exports/ directory. A trailing X.Y.Z folder name is stripped."""
    if arg:
        path = Path(arg).expanduser()
        if VERSION_DIR_RE.match(path.name):
            return path.parent
        return path
    return default_exports_parent()


def godot_version_string(binary: Path) -> str:
    result = subprocess.run(
        [str(binary), "--version"],
        check=True,
        text=True,
        capture_output=True,
    )
    return (result.stdout or result.stderr).strip().splitlines()[0].strip()


def template_dir_name(godot_version: str) -> str:
    tokens = godot_version.split(".")
    if "mono" not in tokens:
        raise SystemExit(
            f"Godot is not a .NET/mono build ({godot_version}). Cue2 needs the Mono editor."
        )
    return ".".join(tokens[: tokens.index("mono") + 1])


def default_template_root() -> Path:
    if sys.platform == "darwin":
        return Path.home() / "Library" / "Application Support" / "Godot" / "export_templates"
    if os.name == "nt":
        appdata = os.environ.get("APPDATA", str(Path.home() / "AppData" / "Roaming"))
        return Path(appdata) / "Godot" / "export_templates"
    xdg = os.environ.get("XDG_DATA_HOME", str(Path.home() / ".local" / "share"))
    return Path(xdg) / "godot" / "export_templates"


def iter_godot_candidates(explicit: str | None) -> list[Path]:
    found: list[Path] = []
    seen: set[str] = set()

    def add(path: Path | None) -> None:
        if path is None:
            return
        resolved = path.expanduser()
        key = str(resolved)
        if key in seen:
            return
        seen.add(key)
        found.append(resolved)

    if explicit:
        add(Path(explicit))
    add(Path(os.environ["GODOT"]) if os.environ.get("GODOT") else None)
    add(Path(os.environ["CUE2_GODOT"]) if os.environ.get("CUE2_GODOT") else None)
    which = shutil.which("godot")
    add(Path(which) if which else None)

    home = Path.home()
    mac_apps = [
        home / "Documents" / "MyFiles" / "Cue2_Home" / "Ext" / "Godot_mono.app" / "Contents" / "MacOS" / "Godot",
        home / "Documents" / "MyFiles" / "Cue2_Home" / "Ext" / "Godot7.1" / "Godot_mono.app" / "Contents" / "MacOS" / "Godot",
        Path("/Applications/Godot_mono.app/Contents/MacOS/Godot"),
        Path("/Applications/Godot.app/Contents/MacOS/Godot"),
    ]
    for item in mac_apps:
        add(item)

    win_roots = [
        Path(r"C:\MyFiles\Cue2_Home\Ext"),
        home / "Documents" / "MyFiles" / "Cue2_Home" / "Ext",
    ]
    for root in win_roots:
        if not root.is_dir():
            continue
        for exe in root.glob("Godot*mono*/Godot*_console.exe"):
            add(exe)
        for exe in root.glob("Godot*mono*/Godot*.exe"):
            if "console" not in exe.name.lower():
                add(exe)

    return found


def find_godot(explicit: str | None) -> tuple[Path, str]:
    errors: list[str] = []
    for candidate in iter_godot_candidates(explicit):
        binary = candidate
        if binary.suffix.lower() == ".app" and (binary / "Contents" / "MacOS" / "Godot").is_file():
            binary = binary / "Contents" / "MacOS" / "Godot"
        if not binary.is_file():
            continue
        try:
            version = godot_version_string(binary)
        except (OSError, subprocess.CalledProcessError) as exc:
            errors.append(f"{binary}: {exc}")
            continue
        if "4.7" in version and "mono" in version:
            return binary, version
        errors.append(f"{binary}: {version} (need 4.7.x mono)")
    hint = "\n  ".join(errors) if errors else "(no candidates found)"
    raise SystemExit(
        "Could not find Godot 4.7 .NET (mono).\n"
        "Pass --godot /path/to/Godot or set GODOT / CUE2_GODOT.\n"
        f"  {hint}"
    )


def missing_templates(template_dir: Path, target: Target) -> list[str]:
    return [name for name in target.templates if not (template_dir / name).is_file()]


def folder_name(version: str, target: Target) -> str:
    return f"Cue2-{version}-{target.id}"


def archive_name(version: str, target: Target) -> str:
    return f"{folder_name(version, target)}.{target.archive}"


def export_root_for(dist: Path, version: str, target: Target) -> Path:
    return dist / folder_name(version, target)


def host_path_for(export_root: Path, target: Target) -> Path:
    return export_root / target.host


def ensure_natives(root: Path, target: Target) -> None:
    src = root / "bin" / target.natives
    if not src.is_dir():
        raise SystemExit(f"Missing natives folder: {src}")
    expected = WIN_CORE if target.natives.startswith("win") else None
    if expected:
        missing = [name for name in expected if not (src / name).is_file()]
        if missing:
            raise SystemExit(f"Missing {target.natives} natives in {src}: {', '.join(missing)}")


def copy_windows_natives(root: Path, export_root: Path, platform: str) -> None:
    src = root / "bin" / platform
    data_dirs = []
    for pattern in (export_root.glob("data_Cue2_*"), export_root.rglob("data_Cue2_*")):
        for item in pattern:
            if item.is_dir():
                data_dirs.append(item)
    unique: list[Path] = []
    seen: set[Path] = set()
    for item in data_dirs:
        resolved = item.resolve()
        if resolved in seen:
            continue
        seen.add(resolved)
        unique.append(item)

    log(f"Copying {platform} natives into {export_root}")
    copied = False
    for dest_dir in unique:
        log(f"  core set -> {dest_dir}")
        for name in WIN_CORE:
            shutil.copy2(src / name, dest_dir / name)
        copied = True
    if not copied:
        dest = export_root / "bin" / platform
        dest.mkdir(parents=True, exist_ok=True)
        log(f"  full folder -> {dest}")
        for item in src.iterdir():
            if item.is_file():
                shutil.copy2(item, dest / item.name)


def copy_natives_ps1(root: Path, export_root: Path, platform: str) -> None:
    script = root / "tools" / "copy-natives-for-export.ps1"
    run(
        [
            "powershell",
            "-NoProfile",
            "-ExecutionPolicy",
            "Bypass",
            "-File",
            str(script),
            "-ExportRoot",
            str(export_root),
            "-Platform",
            platform,
        ]
    )


def copy_natives(root: Path, export_root: Path, target: Target) -> None:
    ensure_natives(root, target)
    if os.name == "nt":
        copy_natives_ps1(root, export_root, target.natives)
        if target.natives == "macos":
            log("  WARN: PowerShell copy cannot rewrite @loader_path; prefer exporting macOS on a Mac")
        return
    if target.natives.startswith("win"):
        copy_windows_natives(root, export_root, target.natives)
        return
    script = root / "tools" / "copy-natives-for-export.sh"
    run(["bash", str(script), str(export_root), target.natives])


def chmod_linux_hosts(export_root: Path, target: Target) -> None:
    names = [target.host, "Cue2.sh", "Cue2.x86_64", "Cue2.arm64"]
    seen: set[Path] = set()
    for name in names:
        path = export_root / name
        if path in seen or not path.is_file():
            continue
        seen.add(path)
        mode = path.stat().st_mode
        path.chmod(mode | stat.S_IXUSR | stat.S_IXGRP | stat.S_IXOTH)
        log(f"  chmod +x {path.name}")


def should_skip_zip_member(name: str) -> bool:
    parts = Path(name).parts
    if ".DS_Store" in parts or name.endswith(".DS_Store"):
        return True
    return any(part.startswith("._") for part in parts)


def zip_folder(folder: Path, zip_path: Path) -> None:
    if zip_path.exists():
        zip_path.unlink()
    log(f"Writing {zip_path.name}")
    with zipfile.ZipFile(zip_path, "w", compression=zipfile.ZIP_DEFLATED) as zf:
        for file in sorted(folder.rglob("*")):
            if not file.is_file():
                continue
            rel = file.relative_to(folder.parent)
            if should_skip_zip_member(str(rel)):
                continue
            zf.write(file, rel.as_posix())


def tar_gz_folder(folder: Path, tar_path: Path, exec_names: set[str]) -> None:
    if tar_path.exists():
        tar_path.unlink()
    log(f"Writing {tar_path.name}")

    def filter_info(info: tarfile.TarInfo) -> tarfile.TarInfo | None:
        name = Path(info.name).name
        if name == ".DS_Store" or name.startswith("._"):
            return None
        if name in exec_names and info.isfile():
            info.mode = 0o755
        return info

    with tarfile.open(tar_path, "w:gz") as tar:
        tar.add(folder, arcname=folder.name, filter=filter_info)


def macos_zip(folder: Path, zip_path: Path) -> None:
    if zip_path.exists():
        zip_path.unlink()
    leftover = folder / "Cue2.zip"
    if leftover.is_file():
        leftover.unlink()
    log(f"Writing {zip_path.name} (ditto --keepParent)")
    run(["ditto", "-c", "-k", "--keepParent", str(folder), str(zip_path)])


def warn_homebrew_ffmpeg(root: Path) -> None:
    dylib = root / "bin" / "macos" / "libavcodec.63.dylib"
    if sys.platform != "darwin" or not dylib.is_file():
        return
    if shutil.which("otool") is None:
        return
    result = subprocess.run(
        ["otool", "-L", str(dylib)],
        check=False,
        text=True,
        capture_output=True,
    )
    blob = result.stdout + result.stderr
    if "/opt/homebrew" in blob:
        log(
            "WARN: bin/macos FFmpeg still points at /opt/homebrew. "
            "Do not ship that. Run ./tools/build-ffmpeg-macos.sh for a portable LGPL build."
        )


def macos_sign_script(root: Path) -> Path:
    return root / "tools" / "macos-sign-and-notarize.sh"


def sign_macos(root: Path, app: Path, *, sign_only: bool) -> None:
    script = macos_sign_script(root)
    if not script.is_file():
        raise RuntimeError(f"macOS sign script not found: {script}")
    cmd = ["bash", str(script), str(app)]
    if sign_only:
        cmd.append("--sign-only")
    run(cmd)


def make_latest_json(root: Path, version: str, dist: Path, notes_file: Path | None) -> None:
    script = root / "tools" / "make-latest-json.py"
    out = dist / "latest.json"
    cmd = [
        sys.executable,
        str(script),
        "--version",
        version,
        "--dist",
        str(dist),
        "--out",
        str(out),
    ]
    if notes_file is not None and notes_file.is_file():
        cmd.extend(["--notes-file", str(notes_file)])
    run(cmd)


def parse_only(raw: str | None) -> list[Target]:
    if not raw:
        return list(TARGETS)
    ids: list[str] = []
    for part in raw.split(","):
        item = part.strip()
        if item:
            ids.append(item)
    unknown = [item for item in ids if item not in TARGET_BY_ID]
    if unknown:
        known = ", ".join(t.id for t in TARGETS)
        raise SystemExit(f"Unknown --only id(s): {', '.join(unknown)}\nKnown: {known}")
    return [TARGET_BY_ID[item] for item in ids]


def parse_args(argv: list[str]) -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Export every Cue2 preset, copy natives, sign, and archive.",
        formatter_class=argparse.RawDescriptionHelpFormatter,
        epilog="""
Examples:
  python tools/export-all.py
  python tools/export-all.py --only macos-arm64
  python tools/export-all.py --bump minor
  python tools/export-all.py --no-bump
  python tools/export-all.py --skip-sign --skip-latest
  python tools/export-all.py --godot ~/Documents/MyFiles/Cue2_Home/Ext/Godot_mono.app
        """.strip(),
    )
    parser.add_argument("--only", help="Comma-separated target ids (see --list)")
    parser.add_argument("--list", action="store_true", help="Print targets and exit")
    parser.add_argument("--dist", help="Exports parent folder (default: ~/Documents/MyFiles/Cue2_Home/Exports)")
    parser.add_argument("--godot", help="Godot 4.7 Mono editor binary or .app")
    parser.add_argument("--version", help="Exact version (skips auto next-version)")
    parser.add_argument(
        "--bump",
        choices=("patch", "minor", "major"),
        default="patch",
        help="Which component to increment when Version.cs is not already ahead (default: patch)",
    )
    parser.add_argument(
        "--no-bump",
        action="store_true",
        help="Use Version.cs as-is even if that version was already exported or tagged",
    )
    parser.add_argument("--debug", action="store_true", help="Use --export-debug instead of --export-release")
    parser.add_argument("--skip-export", action="store_true", help="Reuse existing export folders")
    parser.add_argument("--skip-copy", action="store_true", help="Do not copy FFmpeg/RtMidi")
    parser.add_argument("--skip-sign", action="store_true", help="Skip macOS codesign and notarization")
    parser.add_argument("--sign-only", action="store_true", help="Codesign macOS but skip notarytool")
    parser.add_argument("--skip-archive", action="store_true", help="Leave unpacked export folders")
    parser.add_argument("--skip-latest", action="store_true", help="Do not write latest.json")
    parser.add_argument("--notes-file", type=Path, help="Release notes for latest.json")
    parser.add_argument("--keep", action="store_true", help="Do not wipe the target folder before export")
    parser.add_argument("--dry-run", action="store_true", help="Print the plan without exporting")
    parser.add_argument("--stop-on-error", action="store_true", help="Abort on the first failed target")
    return parser.parse_args(argv)


def print_list() -> None:
    log("Target id          Preset                  Host          Archive")
    for target in TARGETS:
        log(
            f"  {target.id:<16}  {target.preset:<22}  {target.host:<12}  "
            f"Cue2-{{version}}-{target.id}.{target.archive}"
        )


def export_with_godot(
    godot: Path,
    root: Path,
    target: Target,
    host: Path,
    *,
    debug: bool,
) -> None:
    host.parent.mkdir(parents=True, exist_ok=True)
    flag = "--export-debug" if debug else "--export-release"
    cmd = [
        str(godot),
        "--headless",
        "--path",
        str(root),
        flag,
        target.preset,
        str(host),
    ]
    log(f"Godot export: {target.preset} -> {host}")
    run(cmd, cwd=root)


def verify_host(host: Path, target: Target) -> None:
    if target.host.endswith(".app"):
        if not (host / "Contents" / "MacOS").is_dir():
            raise RuntimeError(f"Export did not produce a valid .app: {host}")
        return
    if not host.is_file():
        raise RuntimeError(f"Export did not produce {host}")


def archive_target(version: str, dist: Path, export_root: Path, target: Target) -> Path:
    dest = dist / archive_name(version, target)
    if target.id.startswith("macos") and sys.platform == "darwin" and shutil.which("ditto"):
        macos_zip(export_root, dest)
    elif target.archive == "tar.gz":
        tar_gz_folder(export_root, dest, {target.host, "Cue2.sh"})
    else:
        zip_folder(export_root, dest)
    if not dest.is_file() or dest.stat().st_size == 0:
        raise RuntimeError(f"Archive missing or empty: {dest}")
    log(f"  {dest} ({dest.stat().st_size} bytes)")
    return dest


def process_target(
    args: argparse.Namespace,
    *,
    root: Path,
    godot: Path | None,
    version: str,
    dist: Path,
    template_dir: Path | None,
    target: Target,
) -> None:
    export_root = export_root_for(dist, version, target)
    host = host_path_for(export_root, target)
    archive = dist / archive_name(version, target)
    log("")
    log(f"=== {target.id} ({target.preset}) ===")
    log(f"  folder:  {export_root}")
    log(f"  host:    {host}")
    log(f"  archive: {archive}")

    if args.dry_run:
        if template_dir is not None:
            missing = missing_templates(template_dir, target)
            if missing:
                log(f"  WARN: missing templates: {', '.join(missing)}")
        return

    if not args.skip_export:
        if template_dir is not None:
            missing = missing_templates(template_dir, target)
            if missing:
                raise RuntimeError(
                    "Missing Godot export templates: "
                    + ", ".join(missing)
                    + f"\nInstall 4.7.1 Mono templates into {template_dir}"
                    + "\nEditor → Manage Export Templates, or download "
                    "Godot_v4.7.1-stable_mono_export_templates.tpz"
                )
        if not args.keep and export_root.exists():
            log(f"  wiping {export_root}")
            shutil.rmtree(export_root)
        export_root.mkdir(parents=True, exist_ok=True)
        if godot is None:
            raise RuntimeError("Godot binary is required unless --skip-export")
        export_with_godot(godot, root, target, host, debug=args.debug)
        verify_host(host, target)
    else:
        verify_host(host, target)

    if not args.skip_copy:
        copy_natives(root, export_root, target)
        if target.id.startswith("linux"):
            chmod_linux_hosts(export_root, target)

    wrote_macos_zip = False
    if target.id.startswith("macos") and not args.skip_sign:
        if not macos_sign_script(root).is_file():
            log("  skip sign: macos-sign-and-notarize.sh is not in this tree (unsigned archive)")
        elif sys.platform != "darwin":
            log("  WARN: macOS signing requires Darwin; leaving unsigned")
        elif is_cloud_path(export_root):
            raise RuntimeError(
                "Refusing to sign a cloud-synced path. "
                "Re-run with --dist on a local disk (e.g. ~/Documents/MyFiles/Cue2_Home/Exports)."
            )
        else:
            sign_macos(root, host, sign_only=args.sign_only)
            wrote_macos_zip = archive.is_file() and not args.sign_only

    if args.skip_archive:
        return
    if wrote_macos_zip:
        log(f"  using zip from macos-sign-and-notarize.sh: {archive}")
        return
    archive_target(version, dist, export_root, target)


def main(argv: list[str]) -> int:
    args = parse_args(argv)
    if args.list:
        print_list()
        return 0
    if args.version and args.no_bump:
        raise SystemExit("Use either --version or --no-bump, not both.")

    parent = exports_parent(args.dist)
    plan = plan_version(
        ROOT,
        parent,
        explicit=args.version,
        bump=args.bump,
        no_bump=args.no_bump,
    )
    version = plan.version
    numeric = numeric_version(version)
    targets = parse_only(args.only)
    dist = parent / version
    cfg = ROOT / "export_presets.cfg"
    notes = args.notes_file
    if notes is None:
        candidate = ROOT / "docs" / "releases" / f"{numeric}.txt"
        notes = candidate if candidate.is_file() else None

    log("Cue2 export-all")
    log(f"  project: {ROOT}")
    log(f"  Version.cs: {plan.code}")
    if plan.highest_existing:
        log(f"  existing: {plan.highest_existing} ({', '.join(plan.existing_sources[:3])})")
    else:
        log("  existing: none")
    log(f"  version: {version} ({plan.reason})")
    log(f"  dist:    {dist}")
    log(f"  targets: {', '.join(t.id for t in targets)}")
    if is_cloud_path(ROOT):
        log("  note: project is on a cloud-synced volume; export output is still written to --dist")
    if is_cloud_path(dist):
        log("  WARN: dist is on a cloud-synced volume. macOS signing will refuse this path.")

    project_ver = read_project_version(ROOT)
    if project_ver and project_ver != numeric and not plan.stamp_source:
        log(f"WARN: project.godot config/version={project_ver} but export version is {numeric}")

    if not cfg.is_file():
        raise SystemExit(f"Missing {cfg}")
    preset_names = parse_preset_names(cfg)
    for target in targets:
        if target.preset not in preset_names:
            raise SystemExit(
                f"Preset {target.preset!r} not in export_presets.cfg. Found: {', '.join(preset_names)}"
            )
        ensure_natives(ROOT, target)

    godot: Path | None = None
    godot_ver = ""
    template_dir: Path | None = None
    if not args.skip_export:
        godot, godot_ver = find_godot(args.godot)
        log(f"  godot:   {godot}")
        log(f"  engine:  {godot_ver}")
        template_dir = default_template_root() / template_dir_name(godot_ver)
        log(f"  templates: {template_dir}")
        if not template_dir.is_dir():
            raise SystemExit(f"Export template folder missing: {template_dir}")
        if shutil.which("dotnet") is None:
            raise SystemExit("dotnet is not on PATH. Install the .NET SDK used by this Godot build.")

    if args.dry_run:
        log("")
        log("Dry run — no files will be written.")
        if plan.stamp_source:
            log(f"Would stamp Version.cs and project.godot to {numeric}")
    else:
        dist.mkdir(parents=True, exist_ok=True)
        if plan.stamp_source:
            stamp_version_cs(ROOT, parse_triple(numeric))
            stamp_project_godot(ROOT, numeric)
        stamp_export_preset_versions(cfg, numeric)
        warn_homebrew_ffmpeg(ROOT)

    failed: list[tuple[str, str]] = []
    for target in targets:
        try:
            process_target(
                args,
                root=ROOT,
                godot=godot,
                version=version,
                dist=dist,
                template_dir=template_dir,
                target=target,
            )
        except (RuntimeError, subprocess.CalledProcessError, OSError, SystemExit) as exc:
            message = str(exc)
            if isinstance(exc, subprocess.CalledProcessError):
                message = f"command failed ({exc.returncode}): {' '.join(exc.cmd)}"
            log(f"ERROR: {target.id}: {message}")
            failed.append((target.id, message))
            if args.stop_on_error:
                break

    if not args.dry_run and not args.skip_latest and not args.skip_archive:
        archives = list(dist.glob(f"Cue2-{version}-*.zip")) + list(dist.glob(f"Cue2-{version}-*.tar.gz"))
        if archives:
            try:
                make_latest_json(ROOT, version, dist, notes)
            except subprocess.CalledProcessError as exc:
                log(f"ERROR: latest.json: command failed ({exc.returncode})")
                failed.append(("latest.json", "make-latest-json.py failed"))

    log("")
    if failed:
        log("Finished with errors:")
        for name, message in failed:
            log(f"  - {name}: {message.splitlines()[0]}")
        log(f"Output folder: {dist}")
        return 1

    log("All requested targets succeeded.")
    log(f"Output folder: {dist}")
    if not args.dry_run and not args.skip_archive:
        for target in targets:
            path = dist / archive_name(version, target)
            if path.is_file():
                log(f"  {path.name}")
        latest = dist / "latest.json"
        if latest.is_file():
            log(f"  {latest.name}")
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main(sys.argv[1:]))
    except KeyboardInterrupt:
        log("\nInterrupted.")
        raise SystemExit(130)
