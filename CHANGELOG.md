# Changelog

## [Unreleased]

### Fixed
- Video with embedded audio that underflows the video content will get stuck unfinished in active cue. When audio is EOS and SDL queue has drained, software now switches from audio clock to wall time rebased to last audio position so remaining frames present and EOF can be reached.  

## [0.1.0] - 2026-09-11

First public alpha (StripyHat).

### Added
- Audio and video cues with wide format support via FFmpeg
- Text overlay cues
- Pre-wait, post-wait, and follow timing
- Cue groups and a virtualized cuelist
- Waveforms and optional preloading of upcoming or selected cues
- OSC send and receive (UDP and TCP)
- MIDI input for project actions, plus MIDI output cues
- Keyboard shortcuts (app preferences)
- Save and load sessions as `.c2`, including double-click to open
- Cue library
- Undo and redo
- Optional media backup into the session folder
- Audio output patches and mix routing
- Video canvas, screens, and target layers
- In-app updater on exported builds (Settings → Updates)
- UI language preference (English fallback where a string is not translated yet)

### Platforms
- Windows 10 or later (x86_64 / arm64)
- macOS (Apple Silicon)
- Linux (x86_64 / arm64)
