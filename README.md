<p align="center">
  <a href="https://www.cue2.live/">
    <img src="src/UI/Assets/Cue2Logo.svg" width="400" alt="Cue2 logo">
  </a>
</p>

<p align="center">
  <a href="#download"><img src="https://img.shields.io/badge/platform-Windows%20%7C%20macOS%20%7C%20Linux-lightgrey.svg" alt="Platform"></a>
  <a href="LICENSE"><img src="https://img.shields.io/badge/license-MIT-blue.svg" alt="License"></a>
  <a href="https://github.com/Tech-mop/Cue2/releases/latest"><img src="https://img.shields.io/github/v/release/Tech-mop/Cue2.svg" alt="Release"></a>
  <a href="https://godotengine.org"><img src="https://img.shields.io/badge/Godot-4.7-478cbf.svg?logo=godotengine&logoColor=white" alt="Godot 4.7"></a>
  <a href="https://www.cue2.live/"><img src="https://img.shields.io/badge/website-cue2.live-eb6f02.svg" alt="Website"></a>
</p>

Cue2 offers a reliable and fast way to build sound, video and show control.

## Features
- Audio and video playback (wide format support via FFmpeg)
- OSC send/receive
- Text overlays, cue library, session management
- Low-latency cue triggering

User-facing changes are listed in [CHANGELOG.md](CHANGELOG.md).

## Free and Open Source

## Dependencies & Licensing
- **Godot Engine** (MIT)
- **FFmpeg** (LGPLv2.1 or later) — see [docs/FFmpeg-Licensing.md](docs/FFmpeg-Licensing.md) and https://ffmpeg.org/legal.html
- **SDL3** (zlib)
- **FFmpeg.AutoGen** (MIT)
- **SDL3-CS** (zlib)
- **RtMidi** (MIT-style) — MIDI device I/O; natives in `bin/`

**Important (FFmpeg compliance):**  
Cue2 uses FFmpeg libraries under the LGPLv2.1 via dynamic loading.  
The corresponding source code for the FFmpeg version used to build the bundled libraries is available as described in the documentation and GitHub Releases.

Attribution is shown in the in-app About dialog.

## Tools

Scripts in `tools/` (see [tools/README.md](tools/README.md)):

```bash
# Localization catalog (extract UI strings + fill locale columns)
python tools/i18n/update_catalog.py

# Rebuild bundled RtMidi natives into bin/
python tools/build-rtmidi-natives.py

# Portable LGPL FFmpeg for macOS (overwrites bin/macos)
./tools/build-ffmpeg-macos.sh

# Godot export every OS, copy natives, zip/tar.gz + latest.json
python tools/export-all.py
```

Signing, notarization, and certificates are **not** in this repository.

## Download

Cue2 runs on Windows 10+, macOS, and Linux.

Get the latest build from [GitHub Releases](https://github.com/Tech-mop/Cue2/releases/latest).

## License
This project (Cue2 application code) is licensed under the MIT License — see the [LICENSE](LICENSE) file.

## Disclaimer
This software is provided as-is. See the LICENSE file for details. FFmpeg patent and licensing considerations are the responsibility of the user when distributing or using certain codecs.

