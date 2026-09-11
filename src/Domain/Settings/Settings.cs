// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System;
using System.Linq;
using Cue2.Domain.Connections;
using Cue2.Domain.Cues;
using Cue2.Services;
using Godot;
using Godot.Collections;

namespace Cue2.Domain.ShowSettings;

/// <summary>
/// How component defaults resolve audio output (standalone audio or video embedded audio).
/// </summary>
public enum ComponentAudioOutputDefaultMode
{
    /// <summary>Use Default Patch when present, otherwise the first available patch.</summary>
    Preferred = 0,
    /// <summary>Use a specific patch by id.</summary>
    Patch = 1,
    /// <summary>Use a named direct output device.</summary>
    Direct = 2,
    /// <summary>No audio output assigned.</summary>
    None = 3
}

/// <summary>
/// How component defaults resolve a video target layer.
/// </summary>
public enum ComponentTargetLayerDefaultMode
{
    /// <summary>Use the first layer in <see cref="DisplaysManager.Layers"/> when any exist.</summary>
    FirstAvailable = 0,
    /// <summary>Use a specific layer by id.</summary>
    Layer = 1,
    /// <summary>No output (TargetLayerId = -1).</summary>
    None = 2
}

/// <summary>
/// Manages storing of settings data
/// Child of GlobalData
/// </summary>
public partial class Settings : Node
{
    private GlobalSignals _globalSignals;
    private GlobalData _globalData;
    private AudioDevices _audioDevices;
    /// <summary>
    /// Per-session audio output patches (instance field — not static, so each Settings
    /// autoload owns its table and tests/multiple instances cannot share patches).
    /// </summary>
    private Dictionary<int, AudioOutputPatch> _audioOutputPatches = new Dictionary<int, AudioOutputPatch>();
    private DisplaysManager _displaysManager;

    /// <summary>System default Go button scale (1.0 = base).</summary>
    public const float DefaultGoScale = 1.0f;

    /// <summary>Go scale that hides the entire GO header (settings option "No Go").</summary>
    public const float GoScaleNoGo = 0.0f;

    /// <summary>Go scale that hides the standby notes field (settings option "Half Go").</summary>
    public const float GoScaleHalf = 0.5f;

    /// <summary>System default cuelist UI scale (1.0 = Medium).</summary>
    public const float DefaultCueListScale = 1.0f;

    /// <summary>Cuelist UI scale for Small option.</summary>
    public const float CueListScaleSmall = 0.85f;

    /// <summary>Cuelist UI scale for Medium option.</summary>
    public const float CueListScaleMedium = 1.0f;

    /// <summary>Cuelist UI scale for Large option.</summary>
    public const float CueListScaleLarge = 1.25f;

    /// <summary>System default waveform peak bin count.</summary>
    public const int DefaultWaveformResolution = 4096;

    /// <summary>System default stop fade-out duration in seconds.</summary>
    public const float DefaultStopFadeDuration = 2.5f;

    /// <summary>System default double-GO protection (0 = off).</summary>
    public const float DefaultDoubleGoProtectionSeconds = 0f;

    /// <summary>Maximum double-GO protection duration in seconds.</summary>
    public const float MaxDoubleGoProtectionSeconds = 30f;

    /// <summary>Default for copying used media into the show folder (Audio/Video/Images).</summary>
    public const bool DefaultMediaBackupEnabled = true;

    /// <summary>Default for multi-edit of shell properties when multiple cues are selected.</summary>
    public const bool DefaultMultiEditEnabled = true;

    /// <summary>Default for selecting a newly created cue after Add / drop.</summary>
    public const bool DefaultSelectNewCues = true;

    /// <summary>Default show mode (false = edit mode; true = live show / cue edits locked).</summary>
    public const bool DefaultShowMode = false;

    /// <summary>Default for drawing audio waveforms inside Timeline Inspector cue bars.</summary>
    public const bool DefaultShowTimelineWaveforms = true;

    /// <summary>Default solid colour behind video layers on all output windows.</summary>
    public static readonly Color DefaultOutputBackgroundColor = Colors.Black;

    /// <summary>Default decode/present quality mode for live video outputs.</summary>
    public static readonly VideoQualityMode DefaultVideoQualityMode = VideoQualityMode.Balanced;

    /// <summary>Default inspector video preview quality.</summary>
    public static readonly VideoPreviewQuality DefaultVideoPreviewQuality = VideoPreviewQuality.Full;

    /// <summary>Default output window vsync / frame-pacing mode.</summary>
    public static readonly OutputVSyncMode DefaultOutputVSyncMode = OutputVSyncMode.PreferVSync;

    /// <summary>Default audio latency / buffer preset.</summary>
    public static readonly AudioLatencyMode DefaultAudioLatencyMode = AudioLatencyMode.Balanced;

    /// <summary>Default de-click ramp after audio start/seek (milliseconds).</summary>
    public const int DefaultAudioDeclickMs = 8;

    /// <summary>Minimum allowed de-click ramp (milliseconds).</summary>
    public const int MinAudioDeclickMs = 0;

    /// <summary>Maximum allowed de-click ramp (milliseconds).</summary>
    public const int MaxAudioDeclickMs = 50;

    /// <summary>Default session master volume (linear 1.0 = 0 dB / 100%).</summary>
    public const float DefaultAudioMasterVolume = 1f;

    /// <summary>Default peak clamp ceiling (dBFS). 0 dB = full scale; prevents samples above digital full scale.</summary>
    public const float DefaultAudioOutputMaxDb = 0f;

    /// <summary>Minimum allowed peak clamp ceiling (dBFS).</summary>
    public const float MinAudioOutputMaxDb = -24f;

    /// <summary>Maximum allowed peak clamp ceiling (dBFS).</summary>
    public const float MaxAudioOutputMaxDb = 0f;

    /// <summary>
    /// Default silence floor (dBFS). Samples quieter than this are forced to zero after mix.
    /// −90 dB is near-silent for float PCM and avoids gating normal program material.
    /// </summary>
    public const float DefaultAudioOutputMinDb = -90f;

    /// <summary>Minimum allowed silence floor (dBFS). −120 ≈ digital mute / gate off.</summary>
    public const float MinAudioOutputMinDb = -120f;

    /// <summary>Maximum allowed silence floor (dBFS).</summary>
    public const float MaxAudioOutputMinDb = -20f;

    // ── Cue shell defaults (system factory values) ─────────────────────────

    /// <summary>System default pre-wait in seconds for newly created cues.</summary>
    public const double SystemDefaultCuePreWait = 0.0;

    /// <summary>System default post-wait in seconds for newly created cues.</summary>
    public const double SystemDefaultCuePostWait = 0.0;

    /// <summary>System default continue mode for newly created cues.</summary>
    public const FollowType SystemDefaultCueFollow = FollowType.None;

    /// <summary>System default shell colour for newly created cues.</summary>
    public static readonly Color SystemDefaultCueColor = new Color(0f, 0f, 0f, 1.0f);

    /// <summary>System default armed state for newly created cues.</summary>
    public const bool SystemDefaultCueArmed = true;

    /// <summary>System default skip-if-disarmed for newly created cues.</summary>
    public const bool SystemDefaultCueSkipIfDisarmed = false;

    /// <summary>
    /// System default for only-one-active-instance (false = multiple concurrent GO instances allowed).
    /// </summary>
    public const bool SystemDefaultCueOnlyOneActiveInstance = false;

    // ── Audio component defaults (system factory values) ───────────────────

    /// <summary>System default linear volume for new audio components (1.0 = 0 dB).</summary>
    public const double SystemDefaultAudioVolume = 1.0;

    /// <summary>System default stereo pan for new audio components (0 = center).</summary>
    public const float SystemDefaultAudioPan = 0f;

    /// <summary>System default loop for new audio components.</summary>
    public const bool SystemDefaultAudioLoop = false;

    /// <summary>System default play count for new audio components.</summary>
    public const int SystemDefaultAudioPlayCount = 1;

    /// <summary>System default fade-in seconds for new audio components.</summary>
    public const double SystemDefaultAudioFadeIn = 0.0;

    /// <summary>System default fade-out seconds for new audio components.</summary>
    public const double SystemDefaultAudioFadeOut = 0.0;

    /// <summary>System default audio output mode for new audio components.</summary>
    public const ComponentAudioOutputDefaultMode SystemDefaultAudioOutputMode =
        ComponentAudioOutputDefaultMode.Preferred;

    // ── Video component defaults (system factory values) ───────────────────

    /// <summary>System default TextureRect expand mode for new video components.</summary>
    public const TextureRect.ExpandModeEnum SystemDefaultVideoExpandMode =
        TextureRect.ExpandModeEnum.IgnoreSize;

    /// <summary>System default TextureRect stretch mode for new video components.</summary>
    public const TextureRect.StretchModeEnum SystemDefaultVideoStretchMode =
        TextureRect.StretchModeEnum.KeepAspectCentered;

    /// <summary>System default opacity for new video components (0–1).</summary>
    public const float SystemDefaultVideoOpacity = 1f;

    /// <summary>System default loop for new video components.</summary>
    public const bool SystemDefaultVideoLoop = false;

    /// <summary>System default play count for new video components.</summary>
    public const int SystemDefaultVideoPlayCount = 1;

    /// <summary>System default use-embedded-audio for new video components.</summary>
    public const bool SystemDefaultVideoUseAudio = true;

    /// <summary>System default linear audio volume for new video components.</summary>
    public const float SystemDefaultVideoAudioVolume = 1f;

    /// <summary>System default stereo pan for new video components.</summary>
    public const float SystemDefaultVideoPan = 0f;

    /// <summary>System default fade-in seconds for new video components.</summary>
    public const double SystemDefaultVideoFadeIn = 0.0;

    /// <summary>System default fade-out seconds for new video components.</summary>
    public const double SystemDefaultVideoFadeOut = 0.0;

    /// <summary>System default still-image hold duration (0 = until stopped).</summary>
    public const double SystemDefaultVideoImageDuration = 0.0;

    /// <summary>System default embedded-audio output mode for new video components.</summary>
    public const ComponentAudioOutputDefaultMode SystemDefaultVideoOutputMode =
        ComponentAudioOutputDefaultMode.Preferred;

    /// <summary>System default target-layer mode for new video components.</summary>
    public const ComponentTargetLayerDefaultMode SystemDefaultVideoTargetLayerMode =
        ComponentTargetLayerDefaultMode.FirstAvailable;

    // ── Text component defaults (system factory values) ────────────────────

    /// <summary>System default hold duration for new text components (0 = until stopped).</summary>
    public const double SystemDefaultTextDuration = 0.0;

    /// <summary>System default opacity for new text components (0–1).</summary>
    public const float SystemDefaultTextOpacity = 1f;

    /// <summary>System default BBCode flag for new text components.</summary>
    public const bool SystemDefaultTextUseBbcode = false;

    /// <summary>System default font size for new text components.</summary>
    public const int SystemDefaultTextFontSize = 48;

    /// <summary>System default system font family name (empty = theme default).</summary>
    public const string SystemDefaultTextFontName = "";

    /// <summary>System default font colour for new text components.</summary>
    public static readonly Color SystemDefaultTextFontColor = Colors.White;

    /// <summary>System default horizontal alignment for new text components.</summary>
    public const HorizontalAlignment SystemDefaultTextHAlign = HorizontalAlignment.Center;

    /// <summary>System default vertical alignment for new text components.</summary>
    public const VerticalAlignment SystemDefaultTextVAlign = VerticalAlignment.Center;

    /// <summary>System default autowrap for new text components.</summary>
    public const bool SystemDefaultTextAutowrap = true;

    /// <summary>System default margins (px) for new text components.</summary>
    public const int SystemDefaultTextMargins = 16;

    /// <summary>System default outline size for new text components.</summary>
    public const int SystemDefaultTextOutlineSize = 0;

    /// <summary>System default outline colour for new text components.</summary>
    public static readonly Color SystemDefaultTextOutlineColor = Colors.Black;

    /// <summary>System default background-enabled for new text components.</summary>
    public const bool SystemDefaultTextBackgroundEnabled = false;

    /// <summary>System default background colour for new text components.</summary>
    public static readonly Color SystemDefaultTextBackgroundColor = new Color(0f, 0f, 0f, 0.55f);

    /// <summary>System default fade-in seconds for new text components.</summary>
    public const double SystemDefaultTextFadeIn = 0.0;

    /// <summary>System default fade-out seconds for new text components.</summary>
    public const double SystemDefaultTextFadeOut = 0.0;

    /// <summary>System default target-layer mode for new text components.</summary>
    public const ComponentTargetLayerDefaultMode SystemDefaultTextTargetLayerMode =
        ComponentTargetLayerDefaultMode.FirstAvailable;

    // UI scale is a user preference (UserDataManager.UiScale), not showfile data.

    public float GoScale = DefaultGoScale;

    /// <summary>
    /// Cuelist UI density scale (Small / Medium / Large). Affects shell row height, chrome,
    /// and fonts only — not playback. Persisted with the showfile.
    /// </summary>
    public float CueListScale = DefaultCueListScale;

    public int WaveformResolution = DefaultWaveformResolution;

    /// <summary>
    /// Global stop fade-out duration in seconds (first Stop fades; second Stop hard-cuts).
    /// 0 = immediate stop. Persisted with the session.
    /// </summary>
    public float StopFadeDuration = DefaultStopFadeDuration;

    /// <summary>
    /// Seconds to block a second GO after each GO (hotkey, button, control, OSC/MIDI).
    /// 0 = off. Persisted with the showfile.
    /// </summary>
    public float DoubleGoProtectionSeconds = DefaultDoubleGoProtectionSeconds;

    /// <summary>
    /// When true, used media files are copied into the show folder (Audio/Video/Images)
    /// so the show can be moved between machines with relative media paths.
    /// Persisted with the showfile.
    /// </summary>
    public bool MediaBackupEnabled = DefaultMediaBackupEnabled;

    /// <summary>
    /// When true and multiple cues are selected, shell and component inspectors enter multi-edit mode
    /// (blank fields; edits apply to every selected cue). When false, only the last-focused
    /// cue is shown/edited. Persisted with the showfile.
    /// </summary>
    public bool MultiEditEnabled = DefaultMultiEditEnabled;

    /// <summary>
    /// When true, newly created cues (Add Cue, media drop) become the selection/focus.
    /// When false, selection is left unchanged. Persisted with the showfile.
    /// </summary>
    public bool SelectNewCues = DefaultSelectNewCues;

    /// <summary>
    /// When true, the UI is in Show Mode: cue/cuelist editing is locked (inspectors hidden,
    /// shell inline edits disabled, structural cue ops blocked). When false (default), Edit Mode.
    /// Persisted with the showfile. Not tracked by undo/redo.
    /// </summary>
    public bool ShowMode = DefaultShowMode;

    /// <summary>
    /// True when cue and cuelist document edits must be blocked (Show Mode active).
    /// </summary>
    public bool IsCueEditingLocked => ShowMode;

    /// <summary>
    /// When true, the Timeline Inspector draws available audio waveforms inside cue bars.
    /// When false, bars show solid colour only. Persisted with the showfile.
    /// </summary>
    public bool ShowTimelineWaveforms = DefaultShowTimelineWaveforms;

    /// <summary>
    /// Solid colour drawn behind all layers on every video output window.
    /// Persisted with the showfile. Does not affect the inspector video previewer.
    /// </summary>
    public Color OutputBackgroundColor = DefaultOutputBackgroundColor;

    /// <summary>
    /// Soft decode/present quality for live video outputs (prefetch ring, lateness drop).
    /// Persisted with the showfile.
    /// </summary>
    public VideoQualityMode VideoQualityMode = DefaultVideoQualityMode;

    /// <summary>
    /// Inspector video preview resolution scale (never affects house outputs).
    /// Persisted with the showfile.
    /// </summary>
    public VideoPreviewQuality VideoPreviewQuality = DefaultVideoPreviewQuality;

    /// <summary>
    /// Vsync / frame-pacing preference for video output windows.
    /// Persisted with the showfile.
    /// </summary>
    public OutputVSyncMode OutputVSyncMode = DefaultOutputVSyncMode;

    /// <summary>
    /// Soft audio fill/prefetch latency mode for standalone and embedded audio.
    /// Persisted with the showfile.
    /// </summary>
    public AudioLatencyMode AudioLatencyMode = DefaultAudioLatencyMode;

    /// <summary>
    /// Raised-cosine de-click ramp after audio start/seek, in milliseconds (0 = off).
    /// Persisted with the showfile.
    /// </summary>
    public int AudioDeclickMs = DefaultAudioDeclickMs;

    /// <summary>
    /// Session master volume applied to all cue audio (linear 0–1). Persisted with the showfile.
    /// Runtime mute is separate (see <see cref="AudioDevices.SessionMasterMuted"/>).
    /// </summary>
    public float AudioMasterVolume = DefaultAudioMasterVolume;

    /// <summary>
    /// Peak clamp ceiling in dBFS applied after mix (standalone + video embedded audio).
    /// Samples above this absolute level are hard-limited. Default 0 dB (full scale).
    /// </summary>
    public float AudioOutputMaxDb = DefaultAudioOutputMaxDb;

    /// <summary>
    /// Silence floor in dBFS applied after mix. Samples below this absolute level become silence.
    /// Default −90 dB. Set to −120 dB to effectively disable the gate.
    /// </summary>
    public float AudioOutputMinDb = DefaultAudioOutputMinDb;

    /// <summary>
    /// Resolved present tuning for the current <see cref="VideoQualityMode"/>.
    /// </summary>
    public VideoPresentTuning GetVideoPresentTuning() =>
        VideoPresentTuning.ForMode(VideoQualityMode);

    /// <summary>
    /// Resolved audio fill/prefetch/declick tuning for the current latency mode and declick ms.
    /// </summary>
    public AudioPresentTuning GetAudioPresentTuning() =>
        AudioPresentTuning.ForMode(AudioLatencyMode, AudioDeclickMs);

    // ── Cue shell defaults (show-scoped; applied to newly created cues) ─────

    /// <summary>Default pre-wait (seconds) applied when a new cue is created.</summary>
    public double CueDefaultPreWait = SystemDefaultCuePreWait;

    /// <summary>Default post-wait (seconds) applied when a new cue is created.</summary>
    public double CueDefaultPostWait = SystemDefaultCuePostWait;

    /// <summary>Default continue mode applied when a new cue is created.</summary>
    public FollowType CueDefaultFollow = SystemDefaultCueFollow;

    /// <summary>Default shell colour applied when a new cue is created.</summary>
    public Color CueDefaultColor = SystemDefaultCueColor;

    /// <summary>Default armed state applied when a new cue is created.</summary>
    public bool CueDefaultArmed = SystemDefaultCueArmed;

    /// <summary>Default skip-if-disarmed applied when a new cue is created.</summary>
    public bool CueDefaultSkipIfDisarmed = SystemDefaultCueSkipIfDisarmed;

    /// <summary>
    /// Default only-one-active-instance flag for new cues (false = multiple concurrent instances allowed).
    /// </summary>
    public bool CueDefaultOnlyOneActiveInstance = SystemDefaultCueOnlyOneActiveInstance;

    // ── Audio component defaults (show-scoped; applied on new AudioComponent) ─

    /// <summary>Default linear volume (0–1) for new audio components.</summary>
    public double AudioDefaultVolume = SystemDefaultAudioVolume;

    /// <summary>Default stereo pan (−1…+1) for new audio components.</summary>
    public float AudioDefaultPan = SystemDefaultAudioPan;

    /// <summary>Default loop flag for new audio components.</summary>
    public bool AudioDefaultLoop = SystemDefaultAudioLoop;

    /// <summary>Default play count for new audio components.</summary>
    public int AudioDefaultPlayCount = SystemDefaultAudioPlayCount;

    /// <summary>Default fade-in duration (seconds) for new audio components.</summary>
    public double AudioDefaultFadeIn = SystemDefaultAudioFadeIn;

    /// <summary>Default fade-out duration (seconds) for new audio components.</summary>
    public double AudioDefaultFadeOut = SystemDefaultAudioFadeOut;

    /// <summary>Default audio output mode for new audio components.</summary>
    public ComponentAudioOutputDefaultMode AudioDefaultOutputMode = SystemDefaultAudioOutputMode;

    /// <summary>Default patch id when <see cref="AudioDefaultOutputMode"/> is <see cref="ComponentAudioOutputDefaultMode.Patch"/>.</summary>
    public int AudioDefaultPatchId = -1;

    /// <summary>Default direct device name when mode is <see cref="ComponentAudioOutputDefaultMode.Direct"/>.</summary>
    public string AudioDefaultDirectOutput = string.Empty;

    // ── Video component defaults (show-scoped; applied on new VideoComponent) ─

    /// <summary>Default TextureRect expand mode for new video components.</summary>
    public TextureRect.ExpandModeEnum VideoDefaultExpandMode = SystemDefaultVideoExpandMode;

    /// <summary>Default TextureRect stretch mode for new video components.</summary>
    public TextureRect.StretchModeEnum VideoDefaultStretchMode = SystemDefaultVideoStretchMode;

    /// <summary>Default opacity (0–1) for new video components.</summary>
    public float VideoDefaultOpacity = SystemDefaultVideoOpacity;

    /// <summary>Default loop flag for new video components.</summary>
    public bool VideoDefaultLoop = SystemDefaultVideoLoop;

    /// <summary>Default play count for new video components.</summary>
    public int VideoDefaultPlayCount = SystemDefaultVideoPlayCount;

    /// <summary>Default use-embedded-audio for new video (non-image) components.</summary>
    public bool VideoDefaultUseAudio = SystemDefaultVideoUseAudio;

    /// <summary>Default linear audio volume for new video components.</summary>
    public float VideoDefaultAudioVolume = SystemDefaultVideoAudioVolume;

    /// <summary>Default stereo pan for new video components.</summary>
    public float VideoDefaultPan = SystemDefaultVideoPan;

    /// <summary>Default fade-in duration (seconds) for new video components.</summary>
    public double VideoDefaultFadeIn = SystemDefaultVideoFadeIn;

    /// <summary>Default fade-out duration (seconds) for new video components.</summary>
    public double VideoDefaultFadeOut = SystemDefaultVideoFadeOut;

    /// <summary>Default still-image hold duration in seconds (0 = until stopped).</summary>
    public double VideoDefaultImageDuration = SystemDefaultVideoImageDuration;

    /// <summary>Default embedded-audio output mode for new video components.</summary>
    public ComponentAudioOutputDefaultMode VideoDefaultOutputMode = SystemDefaultVideoOutputMode;

    /// <summary>Default patch id when <see cref="VideoDefaultOutputMode"/> is Patch.</summary>
    public int VideoDefaultPatchId = -1;

    /// <summary>Default direct device name when video audio mode is Direct.</summary>
    public string VideoDefaultDirectOutput = string.Empty;

    /// <summary>Default target-layer mode for new video components.</summary>
    public ComponentTargetLayerDefaultMode VideoDefaultTargetLayerMode =
        SystemDefaultVideoTargetLayerMode;

    /// <summary>Default layer id when <see cref="VideoDefaultTargetLayerMode"/> is Layer.</summary>
    public int VideoDefaultTargetLayerId = -1;

    // ── Text component defaults (show-scoped; applied on new TextComponent) ──

    /// <summary>Default hold duration (seconds) for new text components (0 = until stopped).</summary>
    public double TextDefaultDuration = SystemDefaultTextDuration;

    /// <summary>Default opacity (0–1) for new text components.</summary>
    public float TextDefaultOpacity = SystemDefaultTextOpacity;

    /// <summary>Default BBCode interpretation for new text components.</summary>
    public bool TextDefaultUseBbcode = SystemDefaultTextUseBbcode;

    /// <summary>Default font size (px) for new text components.</summary>
    public int TextDefaultFontSize = SystemDefaultTextFontSize;

    /// <summary>Default system font family name for new text components (empty = theme).</summary>
    public string TextDefaultFontName = SystemDefaultTextFontName;

    /// <summary>Default font colour for new text components.</summary>
    public Color TextDefaultFontColor = SystemDefaultTextFontColor;

    /// <summary>Default horizontal alignment for new text components.</summary>
    public HorizontalAlignment TextDefaultHAlign = SystemDefaultTextHAlign;

    /// <summary>Default vertical alignment for new text components.</summary>
    public VerticalAlignment TextDefaultVAlign = SystemDefaultTextVAlign;

    /// <summary>Default autowrap for new text components.</summary>
    public bool TextDefaultAutowrap = SystemDefaultTextAutowrap;

    /// <summary>Default margins (px) for new text components.</summary>
    public int TextDefaultMargins = SystemDefaultTextMargins;

    /// <summary>Default outline size (px) for new text components.</summary>
    public int TextDefaultOutlineSize = SystemDefaultTextOutlineSize;

    /// <summary>Default outline colour for new text components.</summary>
    public Color TextDefaultOutlineColor = SystemDefaultTextOutlineColor;

    /// <summary>Default background panel enabled for new text components.</summary>
    public bool TextDefaultBackgroundEnabled = SystemDefaultTextBackgroundEnabled;

    /// <summary>Default background panel colour for new text components.</summary>
    public Color TextDefaultBackgroundColor = SystemDefaultTextBackgroundColor;

    /// <summary>Default fade-in duration (seconds) for new text components.</summary>
    public double TextDefaultFadeIn = SystemDefaultTextFadeIn;

    /// <summary>Default fade-out duration (seconds) for new text components.</summary>
    public double TextDefaultFadeOut = SystemDefaultTextFadeOut;

    /// <summary>Default target-layer mode for new text components.</summary>
    public ComponentTargetLayerDefaultMode TextDefaultTargetLayerMode =
        SystemDefaultTextTargetLayerMode;

    /// <summary>Default layer id when <see cref="TextDefaultTargetLayerMode"/> is Layer.</summary>
    public int TextDefaultTargetLayerId = -1;

    public bool VerbosePrint = true;
    
    
    // Cuelight settings
    public Color CueLightIdleColour = new Color(0f, 0f, 0.1f, 1f);
    public Color CueLightGoColour = new Color(0f, 1f, 0f, 1f);
    public Color CueLightStandbyColour = new Color(1f, 0.4f, 0f, 1f);
    public Color CueLightCountInColour = new Color(1f, 0f, 0f, 1f);
    public byte CueLightBrightness = 50;
    
    
    /// <summary>Factory name for the audio output patch created on new sessions.</summary>
    public const string DefaultAudioPatchName = "Default Patch";

    public override void _Ready()
    {
        _globalSignals = GetNode<GlobalSignals>("/root/GlobalSignals");
        _globalData = GetNode<GlobalData>("/root/GlobalData");
        _audioDevices = GetNode<AudioDevices>("/root/AudioDevices");
        _displaysManager = GetNode<DisplaysManager>("/root/DisplaysManager");

        // First launch / empty show: seed Default Patch after SDL enumerate.
        // Skip when opening last-show — LoadAudioFromData builds the real patches.
        CallDeferred(nameof(EnsureBootDefaultAudioPatch));
    }

    public override void _ExitTree()
    {
        // Ensure all AudioOutputPatch GodotObjects (which are never added to the scene tree)
        // are explicitly Freed on shutdown to prevent leaks. They are owned by this static
        // collection and the audio patch settings UI.
        foreach (var patch in _audioOutputPatches.Values.ToList())
        {
            if (patch != null && GodotObject.IsInstanceValid(patch))
            {
                patch.Free();
            }
        }
        _audioOutputPatches.Clear();
    }
    
    public Dictionary<int, AudioOutputPatch> GetAudioOutputPatches() => _audioOutputPatches;
    
    
    public void UpdatePatch(AudioOutputPatch patch)
    {
        _audioOutputPatches[patch.Id] = patch;
        GD.Print($"Settings:UpdatePatch - Updated patch with id: {patch.Id} and name: {patch.Name}");
    }

    public void DeletePatch(int patchId)
    {
        _audioOutputPatches[patchId].Free();
        _audioOutputPatches.Remove(patchId);
        // Shell ✕ / media health depends on patch existence
        GetNodeOrNull<MediaHealthService>("/root/MediaHealthService")?.RecheckAllQuiet();
    } 
    
    public AudioOutputPatch CreateNewPatch()
    {
        var newPatch = new AudioOutputPatch();
        _audioOutputPatches.Add(newPatch.Id, newPatch);
        return newPatch;
    }

    /// <summary>
    /// Creates the session "Default Patch", optionally routing the current system default playback device.
    /// </summary>
    /// <returns>The newly created patch.</returns>
    /// <remarks>
    /// Stereo patch channels Left/Right are 1:1 mapped to device outputs 0/1 when available.
    /// On mono devices both channels route to output 0. If the system default cannot be resolved
    /// or opened, the patch is still created empty so the user can assign a device later.
    /// </remarks>
    public AudioOutputPatch CreateDefaultAudioPatch()
    {
        var patch = new AudioOutputPatch(DefaultAudioPatchName);
        _audioOutputPatches.Add(patch.Id, patch);

        TryRouteSystemDefaultDevice(patch);

        GD.Print($"Settings:CreateDefaultAudioPatch - Created '{patch.Name}' (id={patch.Id}), " +
                 $"devices={patch.OutputDevices.Count}");
        return patch;
    }

    /// <summary>
    /// Ensures at least one audio output patch exists by creating <see cref="DefaultAudioPatchName"/> when empty.
    /// </summary>
    /// <remarks>
    /// Used on first launch and after resets. Does nothing when patches already exist (loaded show, etc.).
    /// </remarks>
    public void EnsureDefaultAudioPatch()
    {
        if (_audioOutputPatches.Count > 0)
            return;

        CreateDefaultAudioPatch();
        GD.Print("Settings:EnsureDefaultAudioPatch - No patches present; created Default Patch.");
    }

    /// <summary>
    /// Boot-only seed. Skips when <see cref="GlobalData.StartupOpenPath"/> is set so last-show
    /// open does not open headphones then throw them away in <c>ClearForOpen</c>.
    /// </summary>
    private void EnsureBootDefaultAudioPatch()
    {
        if (!string.IsNullOrEmpty(_globalData?.StartupOpenPath))
            return;
        EnsureDefaultAudioPatch();
    }

    /// <summary>
    /// Returns the preferred audio output patch for newly created media cues.
    /// </summary>
    /// <returns>
    /// Patch named <see cref="DefaultAudioPatchName"/> when present; otherwise the lowest-id patch;
    /// or <c>null</c> when no patches exist.
    /// </returns>
    public AudioOutputPatch GetPreferredAudioOutputPatch()
    {
        if (_audioOutputPatches.Count == 0)
            return null;

        foreach (var patch in _audioOutputPatches.Values)
        {
            if (patch != null && GodotObject.IsInstanceValid(patch) &&
                string.Equals(patch.Name, DefaultAudioPatchName, StringComparison.Ordinal))
            {
                return patch;
            }
        }

        return _audioOutputPatches
            .OrderBy(kv => kv.Key)
            .Select(kv => kv.Value)
            .FirstOrDefault(p => p != null && GodotObject.IsInstanceValid(p));
    }

    /// <summary>
    /// Opens the system default playback device (if any) and wires it into <paramref name="patch"/>
    /// with a simple stereo default route.
    /// </summary>
    private void TryRouteSystemDefaultDevice(AudioOutputPatch patch)
    {
        if (patch == null || _audioDevices == null)
            return;

        string deviceName = _audioDevices.GetSystemDefaultPlaybackDeviceName();
        if (string.IsNullOrEmpty(deviceName))
        {
            _globalSignals?.EmitSignal(nameof(GlobalSignals.Log),
                "Default Patch created without a system output device (none detected).", 1);
            return;
        }

        // Prefer a name that appears in the current playback enumeration so open-by-name succeeds.
        var available = _audioDevices.GetAvailableAudioDeviceNames();
        if (available != null && available.Count > 0 &&
            !available.Contains(deviceName, StringComparer.Ordinal))
        {
            // Case-insensitive match (Windows device names can vary slightly).
            var match = available.FirstOrDefault(n =>
                string.Equals(n, deviceName, StringComparison.OrdinalIgnoreCase));
            if (match != null)
                deviceName = match;
            else
            {
                GD.Print($"Settings:TryRouteSystemDefaultDevice - Default name '{deviceName}' " +
                         "not in available list; attempting open anyway.");
            }
        }

        var device = _audioDevices.OpenAudioDevice(deviceName, out string error);
        if (device == null)
        {
            _globalSignals?.EmitSignal(nameof(GlobalSignals.Log),
                $"Default Patch: could not open system output '{deviceName}': {error}", 1);
            return;
        }

        int outputCount = Math.Max(1, device.Channels);
        patch.AddDeviceOutputs(deviceName, outputCount);

        // Default stereo channels (constructor order: Left=0, Right=1).
        var channelIds = patch.Channels.Keys.OrderBy(k => k).ToList();
        if (channelIds.Count > 0 && outputCount > 0)
            patch.SetRouting(deviceName, 0, channelIds[0], true);

        if (channelIds.Count > 1)
        {
            // Stereo device: Right → output 1. Mono: fold Right onto output 0.
            int rightOut = outputCount > 1 ? 1 : 0;
            patch.SetRouting(deviceName, rightOut, channelIds[1], true);
        }

        _globalSignals?.EmitSignal(nameof(GlobalSignals.Log),
            $"Default Patch routed to system output: {deviceName}", 0);
    }
    
    public AudioOutputPatch GetPatch(int patchId) => _audioOutputPatches[patchId];

    public void AddPatch(AudioOutputPatch patch)
    {
        if (_audioOutputPatches.ContainsKey(patch.Id))
        {
            GD.PrintErr($"Settings:AddPatch - Patch ID already exists: {patch.Id}");
            _globalSignals.EmitSignal(nameof(GlobalSignals.Log), 
                $"Settings:AddPatch - Failed to add patch due to duplicate ID: {patch.Id}", 2);
            return;
        }
        _audioOutputPatches.Add(patch.Id, patch);
        GD.Print($"Settings:AddPatch - Added patch with ID: {patch.Id} and name: {patch.Name}");
        
        // Double check audio devices in added patch are opened. 
        foreach (var device in patch.OutputDevices)
        {
            _audioDevices.OpenAudioDevice(device.Key, out var _);
        }
        // Note: callers that bulk-add patches (load/history) should recheck once afterward;
        // single interactive deletes recheck in DeletePatch.
    }

    /// <summary>
    /// Builds the set of audio device names that must stay open for the current patch table
    /// (plus any extra names, e.g. the showfile <c>AudioDevices</c> list).
    /// </summary>
    /// <param name="extraNames">Optional additional required names (null-safe).</param>
    /// <returns>Case-sensitive name set (SDL device names are ordinal-matched elsewhere).</returns>
    /// <remarks>
    /// Used by load/reset/history to call <see cref="AudioDevices.SyncOpenDevices"/> so leftover
    /// SDL devices from a previous show are closed after the new required set is opened.
    /// </remarks>
    internal System.Collections.Generic.HashSet<string> CollectRequiredAudioDeviceNames(
        System.Collections.Generic.IEnumerable<string> extraNames = null)
    {
        var required = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
        if (extraNames != null)
        {
            foreach (var name in extraNames)
            {
                if (!string.IsNullOrEmpty(name))
                    required.Add(name);
            }
        }

        foreach (var patch in _audioOutputPatches.Values)
        {
            if (patch?.OutputDevices == null)
                continue;
            foreach (var deviceName in patch.OutputDevices.Keys)
            {
                if (!string.IsNullOrEmpty(deviceName))
                    required.Add(deviceName);
            }
        }

        return required;
    }

    /// <summary>
    /// Opens any missing required devices and closes open SDL devices not needed by the current
    /// patch table (and optional extra names). See <see cref="AudioDevices.SyncOpenDevices"/>.
    /// </summary>
    /// <param name="extraNames">Showfile open-device list or history snapshot names.</param>
    public void ReconcileOpenAudioDevices(System.Collections.Generic.IEnumerable<string> extraNames = null)
    {
        if (_audioDevices == null)
            return;

        var required = CollectRequiredAudioDeviceNames(extraNames);
        _audioDevices.SyncOpenDevices(required, closeOthers: true);
        GD.Print($"Settings:ReconcileOpenAudioDevices - Required {required.Count} device(s)");
    }

    private void PrintPatches()
    {
        foreach (var patch in _audioOutputPatches)
        {
            foreach (var channels in patch.Value.Channels)
            {
                GD.Print($"Settings:PrintPatches - ID: {patch.Key} Name: {patch.Value.Name} Channel: {channels.Key} Name: {channels.Value}");
            }
        }
    }
    
    
    // Save and loads
    /// <summary>
    /// Frees all audio output patches without seeding a Default Patch.
    /// </summary>
    private void FreeAllAudioPatches()
    {
        foreach (var existing in _audioOutputPatches.Values.ToList())
        {
            if (existing != null && GodotObject.IsInstanceValid(existing))
                existing.Free();
        }
        _audioOutputPatches.Clear();
    }

    /// <summary>
    /// Restores show scalars and component defaults in memory (no UI signals).
    /// </summary>
    private void ResetScalarSettingsToDefaults()
    {
        GoScale = DefaultGoScale;
        CueListScale = DefaultCueListScale;
        WaveformResolution = DefaultWaveformResolution;
        StopFadeDuration = DefaultStopFadeDuration;
        DoubleGoProtectionSeconds = DefaultDoubleGoProtectionSeconds;
        MediaBackupEnabled = DefaultMediaBackupEnabled;
        MultiEditEnabled = DefaultMultiEditEnabled;
        SelectNewCues = DefaultSelectNewCues;
        ShowMode = DefaultShowMode;
        ShowTimelineWaveforms = DefaultShowTimelineWaveforms;
        OutputBackgroundColor = DefaultOutputBackgroundColor;
        VideoQualityMode = DefaultVideoQualityMode;
        VideoPreviewQuality = DefaultVideoPreviewQuality;
        OutputVSyncMode = DefaultOutputVSyncMode;
        AudioLatencyMode = DefaultAudioLatencyMode;
        AudioDeclickMs = DefaultAudioDeclickMs;
        AudioMasterVolume = DefaultAudioMasterVolume;
        AudioOutputMaxDb = DefaultAudioOutputMaxDb;
        AudioOutputMinDb = DefaultAudioOutputMinDb;
        VerbosePrint = true;

        ResetCueDefaultsToSystem();
        ResetAudioDefaultsToSystem();
        ResetVideoDefaultsToSystem();
        ResetTextDefaultsToSystem();

        CueLightIdleColour = new Color(0f, 0f, 0.1f, 1f);
        CueLightGoColour = new Color(0f, 1f, 0f, 1f);
        CueLightStandbyColour = new Color(1f, 0.4f, 0f, 1f);
        CueLightCountInColour = new Color(1f, 0f, 0f, 1f);
        CueLightBrightness = 50;
    }

    /// <summary>
    /// Clears live show settings for a showfile apply without seeding a playable empty show.
    /// </summary>
    /// <remarks>
    /// Frees patches and output windows, resets scalars/OSC/MIDI/cue lights. Does <b>not</b>
    /// create a Default Patch or default canvas screen, does <b>not</b> open/close SDL devices,
    /// and does <b>not</b> emit scale/show-mode/displays signals.
    /// <see cref="LoadSettings"/> is the single constructor of the incoming show.
    /// </remarks>
    public void ClearForOpen()
    {
        FreeAllAudioPatches();
        ResetScalarSettingsToDefaults();

        _displaysManager?.ClearForOpen();
        _globalData?.CueLightManager?.Reset();
        GetNodeOrNull<OscListen>("/root/OscListen")?.ResetToDefaults();
        GetNodeOrNull<OscConnections>("/root/OscConnections")?.ClearAll();
        GetNodeOrNull<MidiManager>("/root/MidiManager")?.ResetToDefaults();

        GD.Print("Settings:ClearForOpen - Settings cleared for showfile apply (no default seed).");
    }

    /// <summary>
    /// Resets all show/session settings to factory defaults (File → New).
    /// </summary>
    /// <remarks>
    /// Clears audio patches then seeds a <see cref="DefaultAudioPatchName"/> (system playback
    /// device when available). Also resets displays (canvas/layers/screens), cue lights,
    /// OSC listen/connections, and general scalars. Does <b>not</b> reset Input Map — that
    /// lives in user preferences. Emits scale/display signals so live UI can resync.
    /// Showfile open uses <see cref="ClearForOpen"/> instead so LoadSettings does not
    /// throw away a just-created Default Patch and virtual screen.
    /// </remarks>
    public void ResetSettings()
    {
        FreeAllAudioPatches();

        // Seed a Default Patch (system playback device when available) so new cues can play out.
        CreateDefaultAudioPatch();

        // Close leftover SDL devices from the previous show; keep only what the new Default Patch needs.
        ReconcileOpenAudioDevices();

        ResetScalarSettingsToDefaults();

        // Operator runtime video controls should never carry across New Session.
        _displaysManager?.ClearRuntimeOutputControls();
        _displaysManager?.ApplyOutputBackgroundColor(OutputBackgroundColor);
        _displaysManager?.ApplyOutputVSyncPreference();
        _audioDevices?.SyncSessionMasterFromSettings();

        // Input Map / UI scale are user-scoped (UserDataManager) — leave alone on New Session.

        _displaysManager?.ResetToDefaults();

        _globalData?.CueLightManager?.Reset();
        GetNodeOrNull<OscListen>("/root/OscListen")?.ResetToDefaults();
        GetNodeOrNull<OscConnections>("/root/OscConnections")?.ClearAll();
        GetNodeOrNull<MidiManager>("/root/MidiManager")?.ResetToDefaults();

        _globalSignals?.EmitSignal(nameof(GlobalSignals.GoScaleChanged), GoScale);
        _globalSignals?.EmitSignal(nameof(GlobalSignals.CueListScaleChanged), CueListScale);
        NotifyShowModeChanged();

        GD.Print("Settings:ResetSettings - Show settings restored to defaults.");
    }

    /// <summary>
    /// Broadcasts the current <see cref="ShowMode"/> so title bar, inspectors, and shell bars resync.
    /// Does not push undo history.
    /// </summary>
    public void NotifyShowModeChanged()
    {
        _globalSignals?.EmitSignal(nameof(GlobalSignals.ShowModeChanged), ShowMode);
    }

    /// <summary>
    /// Sets <see cref="ShowMode"/> and notifies listeners. No-ops when the value is unchanged
    /// (unless <paramref name="forceNotify"/> is true). Not tracked by undo/redo.
    /// </summary>
    /// <param name="enabled">True for Show Mode; false for Edit Mode.</param>
    /// <param name="forceNotify">When true, always emit even if the value did not change.</param>
    public void SetShowMode(bool enabled, bool forceNotify = false)
    {
        if (ShowMode == enabled && !forceNotify)
            return;
        ShowMode = enabled;
        NotifyShowModeChanged();
        GD.Print($"Settings:SetShowMode - ShowMode={ShowMode}");
    }

}
