# Changelog

## [Unreleased]

### Fixed
- Option button menus pause keyboard shortcuts while open and turn them back on when the menu closes (pick or cancel). Previously most dropdowns left shortcut listening off.
- Video with embedded audio that underflows the video content will get stuck unfinished in active cue. When audio is EOS and SDL queue has drained, software now switches from audio clock to wall time rebased to last audio position so remaining frames present and EOF can be reached.
- Shell inspector no longer shows “No Selection” in number/name when a cue is selected but those fields are empty.
- Shell inspector keeps a bottom content margin so the horizontal scrollbar no longer covers the Delete button when scrolled to the bottom.
- Active cue head progress no longer flickers while dragging a component seek bar. The cue bar keeps tracking live playback until mouse-up, then jumps to the committed seek.
- Quit / New / Open **Save & close** on a never-saved session no longer fails silently. It now uses the same File → Save path, which falls through to Save As when there is no show path yet.
- Windows: resizing the main window to the display no longer lets Godot promote it to exclusive fullscreen while Cue2 still thinks it is windowed. Same 1px / demote guard as video outputs. Header double-click still maximizes; the expand button still toggles non-exclusive fullscreen.
- Mac: Couldn't delete cues with delete key as it was registering as backspace. Cmd+Delete now delete selected cues.


### Changed
- Canvas editor: repeated clicks on stacked screens/layers cycle selection instead of always picking the topmost rect. Drag still moves the current item.
- Colour picking uses a compact Cue2 popup (preview, HSV/RGB sliders, 10 presets, recent custom colours) instead of Godot’s full ColorPicker. Scales with UI scale. Recent colours persist in user preferences.
- Settings file buttons: **Store / Recall Settings**, **Store**, and **Recall** (was Save / Load), so they are not confused with File → Save / Open for the show.  

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
