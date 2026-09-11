# Cue-2

## Cross-platform show control and playback
Cue-2 offers a reliable and fast way to build sound, video and show control.

## Features
- Audio and video playback (wide format support via FFmpeg)
- OSC send/receive
- Text overlays, cue library, session management
- Low-latency cue triggering

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

## Platforms
Cue-2 targets:
- Windows 10+
- macOS
- Linux

## License
This project (Cue2 application code) is licensed under the MIT License — see the [LICENSE](LICENSE) file.

## Disclaimer
This software is provided as-is. See the LICENSE file for details. FFmpeg patent and licensing considerations are the responsibility of the user when distributing or using certain codecs.

