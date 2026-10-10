# Changelog

## [Unreleased]

### Added
- Settings → Audio → Audio Input. Create named input patches, collapse them, and add channels. Each channel is labelled device: and ch: and is assigned one recording input.
- Channel trim uses the usual dB field. Its meter is the device level before trim. Beside the patch name, a submaster trim applies to every channel, and a second meter shows the loudest channel after both trims. Meters run while the Audio Input page is open.
- Input patches are saved with the show, included in undo, and included in Settings Store / Recall.
- Audio inspector, under Select File: a compact Audio Input menu of those patches and a Setup Audio Inputs button that opens Audio Input. That row is hidden when the cue already has file audio. Setup Audio Patches sits beside the output menu for both file audio and audio input. Choosing a patch adds an audio input to the cue. A cue cannot have both an audio file and an audio input.
- An audio input hides Select File and the file URL. The inspector keeps output, routing, volume, and fades. Duration is set like an image: a hold time, or blank / 0 to stay active until stopped. The routing matrix has one mono Submaster row, 0 dB to every output by default. Pan is not shown. The Audio tab dot appears for an audio input.
- While the cue is active, the input patch is summed to that submaster and routed to the assigned output. Blank duration stays up until the cue stops.
- Settings → Audio → Display → Level meters (on by default, saved with the show). When an audio file, video with audio, or audio input is playing, a thin level meter sits inside that component’s progress row.
- Audio output patches allow 64 buses. Builds ship a patched SDL 3.4.2 so devices with more than 8 channels can open on Windows, macOS, and Linux. PulseAudio itself stops at 32 channels; ALSA and PipeWire allow 64.
- Fade-in and fade-out on audio (file and input), video, and text can be Linear, S-Curve, Exponential, or Logarithmic. Each inspector fade time has a curve menu beside it. Waveforms draw those curves. Settings → General Stop Fade Out has a matching curve for the session stop.
- Audio file cues have a play rate. Rate shortens the cue and raises pitch unless Keep Pitch is on. Pitch (cents) shifts also added.
- Audio inspector Timeline: click the waveform or use Add node to place numbered markers. Each node is a draggable square with a slice line, and a filled volume circle on the automation line (drag for time and level). Listed with time, relative volume, rate, pitch, interpolation (Linear / Snap / Bezier per node), continue, loop, and play count for the region before it. Loop regions show as a bottom inset bracket ┌── ×n ──┐ (or ∞), one per loop span. Volume, rate, and pitch are relative to the component, including playback. Rate (cyan) and pitch (violet) draw on the waveform; they are edited in the list only.
- Timeline inspector cue bars show audio region slices at their wall-clock widths (each region’s play count, ∞ for a looping region). Component play count then repeats that whole sequence. Active-cue progress and seek follow the same durations, including play rate along the timeline.
- Control Fade can target play rate and pitch when the target cue has file audio (shown in the property list only then). Fades apply to the playing instance; timeline relative rate/pitch still apply. Keep Pitch on the target still holds musical pitch when rate changes.
- Control Devamp: on a playing target, leave the current loop after this pass. A looping timeline region is escaped first; otherwise remaining cue play count / Loop is skipped. Nested children are included. Can target the same cue.
- Dragging a cue to reorder auto-scrolls the list when the pointer is near the top or bottom, same as the control pick-target tool. The mouse wheel and trackpad also scroll the list while that drag is held.

### Fixed
- Closing Cue2 no longer leaves leaked objects from video preview textures, inspector tab icons, and playback objects that only freed on a deferred idle frame.
- Video inspector preview stays inside the canvas outline when expand or stretch mode changes. The preview rect was filling the whole view instead of the target layer.
- Video inspector preview sits on the left above the transport controls (it was centering in the view).
- Expand / stretch in the inspector preview use the scaled canvas size. Fit Width was using the file’s native pixel width, so the picture ran wider than the preview.
- Moving or resizing a target layer no longer resets a playing video to Ignore Size. House outputs keep the cue’s expand and stretch modes.
- Short audio files and zoomed-in waveforms draw as a continuous envelope. They previously showed as sparse thin vertical bars.
- Playing to a 64-channel device (Dante Virtual Soundcard) no longer crashes when SDL resamples the stream (for example 44100 Hz into 48000 Hz).
- Cmd+S / Ctrl+S Save, and other modifier shortcuts, work while Settings is open, including when a text field there is focused. Plain keys such as Go and Delete still wait until you leave a text field and close an open menu.
- Native popups follow UI scale (OptionButton lists, colour picker, shell context menu, clock-days). They were opening at 1× because a PopupPanel is its own window.

### Changed
- Audio inspector waveform accordion renamed to "timeline"
- Video inspector no longer shows the unused Scale resolution / Offset row. Expand and stretch size the picture on the layer; those leftover fields were never applied to preview or playback.
- Audio and video inspector waveforms zoom in seconds (Fit is the whole file; max zoom is 50ms), with Fit, −, +, an always-visible scrollbar, and a visible-window readout. Ctrl+wheel zooms at the cursor; drag pans; double-click fits the start–end region. Hover time and a live playhead sit on the ruler. Start and end flags in the time bar can be dragged. Fade-in and fade-out show as wedges with a thin vertical bar and a flag at the bottom of the waveform.
- Timeline inspector zoom matches that: Fit is the whole show, max zoom is a 50ms window, the slider is logarithmic, and a readout shows the visible span. Long shows can zoom out to fit and in to 50ms; short cues can fill the view.
- Audio output patch editor: unused devices stay off the grid (Add device). Choosing a device adds every hardware channel with no routes. The routing grid uses frozen headers, drawn cells (click-drag paint), collapsible groups of 8 on large devices, and a List view of bus → hardware routes.


## [0.1.1] - 2026-09-21

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
- Improved coverage of translations - now includes more tooltips. 

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
