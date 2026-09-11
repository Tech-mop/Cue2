// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System;
using Godot;
using System.Collections.Generic;
using System.Linq;
using Cue2.Domain.Cuelist;
using Cue2.Domain.Playback;
using Cue2.Domain.Devices;
using Cue2.Domain.ShowSettings;
using Cue2.Domain.Metadata;
using Cue2.Domain.Cues;
using Cue2.Domain.Connections;
using Cue2.Domain.Library;
using Cue2.Domain.Commands;
using Cue2.Services;
using Godot.Collections;
using Array = Godot.Collections.Array;

namespace Cue2.Domain.Cues;


/// <summary>
/// How a cue chains to the next cue at the same nesting level (cue sequences).
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><see cref="None"/> — do not auto-start the next cue.</item>
/// <item><see cref="Continue"/> — auto-continue: after this cue's pre-wait ends (content phase starts),
/// the next sibling is armed and waits this cue's <see cref="Cue.PostWait"/> before starting
/// (post-wait 0 = next starts when content phase starts).</item>
/// <item><see cref="Follow"/> — auto-follow: when this cue's content completes, the next sibling is armed
/// and waits this cue's <see cref="Cue.PostWait"/> before starting (post-wait 0 = immediate).</item>
/// </list>
/// The next cue is always the next entry at the same nested level (root order or parent <see cref="Cue.ChildCues"/>).
/// Armed next cues appear in the active list with a continue/follow lead-in timer, then their own pre-wait/content.
/// </remarks>
public enum FollowType
{
    /// <summary>Do not continue — next cue is not started automatically.</summary>
    None = 0,

    /// <summary>Auto-continue — next sibling armed after pre-wait; then post-wait lead-in on that cue.</summary>
    Continue = 1,

    /// <summary>Auto-follow — next sibling armed after content completes; then post-wait lead-in on that cue.</summary>
    Follow = 2
}

/// <summary>
/// MIDI message kinds that can be used as a cue trigger.
/// </summary>
public enum MidiTriggerMessageType
{
    /// <summary>Note On (velocity &gt; 0).</summary>
    NoteOn = 0,

    /// <summary>Note Off, or Note On with velocity 0.</summary>
    NoteOff = 1,

    /// <summary>Control Change (CC).</summary>
    ControlChange = 2,

    /// <summary>Program Change.</summary>
    ProgramChange = 3
}

public class Cue : ICue
{
    private static int _nextId = 0;
    public int Id { get; set; }

    /// <summary>
    /// Resets the static cue-id allocator (call when starting a new empty session).
    /// </summary>
    public static void ResetIdAllocator()
    {
        _nextId = 0;
    }

    private string _name;
    public string Name
    {
        get => _name;
        set
        {
            _name = value;
            NameChanged?.Invoke(value);
        }
    }

    private string _cueNum;
    public string CueNum
    {
        get => _cueNum;
        set
        {
            _cueNum = value;
            CueNumChanged?.Invoke(value);
        }
    }
    
    public ShellBar ShellBar { get; set; }

    public int ParentId = -1;

    public List<int> ChildCues = new List<int>(); // list of child cue ID's
    
    private double _preWait;
    private double _duration;
    private double _totalDuration;
    private double _postWait;

    public double PreWait
    {
        get => _preWait;
        set
        {
            if (Math.Abs(_preWait - value) < 1e-9) return;
            _preWait = value;
            PreWaitChanged?.Invoke(_preWait);
        }
    }

    /// <summary>Duration of cue contents excluding pre/post wait (includes child cues).</summary>
    public double Duration
    {
        get => _duration;
        set
        {
            if (Math.Abs(_duration - value) < 1e-9) return;
            _duration = value;
            DurationChanged?.Invoke(_duration);
        }
    }

    public double TotalDuration
    {
        get => _totalDuration;
        set
        {
            if (Math.Abs(_totalDuration - value) < 1e-9) return;
            _totalDuration = value;
            TotalDurationChanged?.Invoke(_totalDuration);
        }
    }

    public double PostWait
    {
        get => _postWait;
        set
        {
            if (Math.Abs(_postWait - value) < 1e-9) return;
            _postWait = value;
            PostWaitChanged?.Invoke(_postWait);
        }
    }

    private Color _color;
    public Color Color
    {
        get => _color;
        set
        {
            _color = value;
            ColorChanged?.Invoke(value);
        }
    }
    private FollowType _follow = FollowType.None;

    /// <summary>
    /// Continue / follow mode for cue sequences (see <see cref="FollowType"/>).
    /// </summary>
    /// <value>One of <see cref="FollowType.None"/>, <see cref="FollowType.Continue"/>, or <see cref="FollowType.Follow"/>.</value>
    public FollowType Follow
    {
        get => _follow;
        set
        {
            if (_follow == value) return;
            _follow = value;
            FollowChanged?.Invoke(_follow);
        }
    }
    
    /// <summary>
    /// Stored value if it's children are expanded to view.
    /// </summary>
    public bool Expanded { get; set; } = false;

    private bool _armed = true;

    /// <summary>
    /// Whether this cue is armed for playback. When false, GO does not play content
    /// and advances the playhead to the next cue instead.
    /// </summary>
    /// <value>Default is <c>true</c> (armed).</value>
    public bool Armed
    {
        get => _armed;
        set
        {
            if (_armed == value) return;
            _armed = value;
            ArmedChanged?.Invoke(_armed);
        }
    }

    private bool _skipIfDisarmed;

    /// <summary>
    /// When true and <see cref="Armed"/> is false, advancing the playhead after a prior GO
    /// bypasses this cue and selects the next eligible cue instead of standing on it.
    /// </summary>
    /// <value>Default is <c>false</c> (disarmed cues still receive playhead selection).</value>
    public bool SkipIfDisarmed
    {
        get => _skipIfDisarmed;
        set
        {
            if (_skipIfDisarmed == value) return;
            _skipIfDisarmed = value;
            SkipIfDisarmedChanged?.Invoke(_skipIfDisarmed);
        }
    }

    /// <summary>
    /// True when this cue should be bypassed when advancing the playhead after GO
    /// (disarmed with <see cref="SkipIfDisarmed"/>).
    /// </summary>
    public bool ShouldSkipOnPlayhead => !Armed && SkipIfDisarmed;

    private bool _onlyOneActiveInstance;

    /// <summary>
    /// When true, a new GO/activate of this cue hard-stops any existing active instance
    /// before starting a new one. When false (default), multiple concurrent instances are allowed.
    /// </summary>
    /// <value>Default is <c>false</c> (stacking / re-trigger without killing prior instances).</value>
    public bool OnlyOneActiveInstance
    {
        get => _onlyOneActiveInstance;
        set
        {
            if (_onlyOneActiveInstance == value) return;
            _onlyOneActiveInstance = value;
        }
    }

    private string _notes = string.Empty;

    /// <summary>
    /// Free-form operator notes for this cue. Not used during playback; editable in the shell inspector.
    /// </summary>
    /// <value>Plain text; empty string when unset. Default is empty.</value>
    public string Notes
    {
        get => _notes;
        set
        {
            string next = value ?? string.Empty;
            if (string.Equals(_notes, next, StringComparison.Ordinal)) return;
            _notes = next;
            NotesChanged?.Invoke(_notes);
        }
    }

    private bool _memo;

    /// <summary>
    /// When true, the shell bar in the cuelist shows notes instead of number, name, and timing fields.
    /// </summary>
    /// <value>Default is <c>false</c> (standard shell layout).</value>
    public bool Memo
    {
        get => _memo;
        set
        {
            if (_memo == value) return;
            _memo = value;
            MemoChanged?.Invoke(_memo);
        }
    }

    // ── Cue hotkey trigger ──────────────────────────────────────────────────

    private bool _hotkeyEnabled;

    /// <summary>
    /// When true and a hotkey is bound, pressing the key triggers this cue (GO).
    /// Independent of whether a key is bound so a binding can be temporarily disabled.
    /// </summary>
    /// <value>Default is <c>false</c>.</value>
    public bool HotkeyEnabled
    {
        get => _hotkeyEnabled;
        set
        {
            if (_hotkeyEnabled == value) return;
            _hotkeyEnabled = value;
            HotkeyChanged?.Invoke();
        }
    }

    /// <summary>Keycode of the cue hotkey, or <see cref="Key.None"/> when unbound (default).</summary>
    public Key HotkeyKeycode { get; private set; } = Key.None;

    /// <summary>Physical keycode fallback for the cue hotkey.</summary>
    public Key HotkeyPhysicalKeycode { get; private set; } = Key.None;

    /// <summary>Ctrl modifier required for the cue hotkey.</summary>
    public bool HotkeyCtrl { get; private set; }

    /// <summary>Shift modifier required for the cue hotkey.</summary>
    public bool HotkeyShift { get; private set; }

    /// <summary>Alt modifier required for the cue hotkey.</summary>
    public bool HotkeyAlt { get; private set; }

    /// <summary>Meta (Cmd/Win) modifier required for the cue hotkey.</summary>
    public bool HotkeyMeta { get; private set; }

    /// <summary>
    /// True when a hotkey key is bound (regardless of <see cref="HotkeyEnabled"/>).
    /// Default is unbound.
    /// </summary>
    public bool HasHotkey => HotkeyKeycode != Key.None || HotkeyPhysicalKeycode != Key.None;

    /// <summary>
    /// True when the hotkey differs from the default (no hotkey).
    /// Used to show the reset/refresh button in the shell inspector.
    /// </summary>
    public bool IsHotkeyNonDefault => HasHotkey || HotkeyEnabled;

    /// <summary>
    /// True when the hotkey can fire: enabled, bound, and the cue is armed.
    /// </summary>
    public bool CanFireHotkey => HotkeyEnabled && HasHotkey && Armed;

    // ── Cue wall-clock trigger ──────────────────────────────────────────────

    /// <summary>
    /// Bitmask of enabled weekdays for the clock trigger.
    /// Bit <c>n</c> maps to <see cref="DayOfWeek"/> value <c>n</c> (Sunday = 0 … Saturday = 6).
    /// Default is every day (<see cref="ClockDaysAll"/>).
    /// </summary>
    public const byte ClockDaysAll = 0x7F; // bits 0–6

    private bool _clockEnabled;

    /// <summary>
    /// When true and a clock time is set, the cue GO's when local real-world time reaches that time of day
    /// on an enabled weekday (see <see cref="ClockDaysMask"/>).
    /// </summary>
    /// <value>Default is <c>false</c>.</value>
    public bool ClockEnabled
    {
        get => _clockEnabled;
        set
        {
            if (_clockEnabled == value) return;
            _clockEnabled = value;
            ClockChanged?.Invoke();
        }
    }

    /// <summary>
    /// True when a wall-clock time of day has been set (regardless of <see cref="ClockEnabled"/>).
    /// Default is unset.
    /// </summary>
    public bool HasClockTime { get; private set; }

    /// <summary>
    /// Target local time of day for the clock trigger (meaningful only when <see cref="HasClockTime"/>).
    /// </summary>
    public TimeSpan ClockTimeOfDay { get; private set; } = TimeSpan.Zero;

    /// <summary>
    /// Weekdays on which the clock trigger may fire (bitmask; see <see cref="ClockDaysAll"/>).
    /// Default is every day.
    /// </summary>
    public byte ClockDaysMask { get; private set; } = ClockDaysAll;

    /// <summary>
    /// True when every weekday is enabled for the clock trigger (factory default for days).
    /// </summary>
    public bool IsClockEveryDay => ClockDaysMask == ClockDaysAll;

    /// <summary>
    /// True when the clock trigger differs from default (no clock, every day selected).
    /// Used to show the reset/refresh button in the shell inspector.
    /// </summary>
    public bool IsClockNonDefault => HasClockTime || ClockEnabled || !IsClockEveryDay;

    /// <summary>
    /// True when the clock trigger can fire: enabled, time set, at least one day selected, and the cue is armed.
    /// </summary>
    public bool CanFireClock => ClockEnabled && HasClockTime && Armed && ClockDaysMask != 0;

    // ── Cue MIDI trigger ────────────────────────────────────────────────────

    private bool _midiTriggerEnabled;

    /// <summary>
    /// When true and a MIDI pattern is set, matching MIDI input GO's this cue.
    /// </summary>
    /// <value>Default is <c>false</c>.</value>
    public bool MidiTriggerEnabled
    {
        get => _midiTriggerEnabled;
        set
        {
            if (_midiTriggerEnabled == value) return;
            _midiTriggerEnabled = value;
            MidiTriggerChanged?.Invoke();
        }
    }

    /// <summary>
    /// True when a MIDI trigger pattern has been set (regardless of <see cref="MidiTriggerEnabled"/>).
    /// Default is unset.
    /// </summary>
    public bool HasMidiTrigger { get; private set; }

    /// <summary>MIDI message type to match (Note On / Off / CC / Program Change).</summary>
    public MidiTriggerMessageType MidiMessageType { get; private set; } = MidiTriggerMessageType.NoteOn;

    /// <summary>
    /// MIDI channel to match (1–16), or <c>0</c> for any channel.
    /// </summary>
    public int MidiChannel { get; private set; }

    /// <summary>
    /// Primary data byte: note number, CC number, or program number (0–127).
    /// </summary>
    public int MidiData1 { get; private set; }

    /// <summary>
    /// Secondary data byte: velocity or CC value (0–127). Only used when <see cref="MidiMatchValue"/> is true.
    /// </summary>
    public int MidiData2 { get; private set; }

    /// <summary>
    /// When true, <see cref="MidiData2"/> must match (velocity / CC value).
    /// When false, any value is accepted (typical for note triggers).
    /// </summary>
    public bool MidiMatchValue { get; private set; }

    /// <summary>
    /// Optional device-name filter. Empty means any session MIDI input.
    /// </summary>
    public string MidiDeviceFilter { get; private set; } = string.Empty;

    /// <summary>
    /// True when the MIDI trigger differs from default (no MIDI trigger).
    /// </summary>
    public bool IsMidiTriggerNonDefault => HasMidiTrigger || MidiTriggerEnabled;

    /// <summary>
    /// True when the MIDI trigger can fire: enabled, pattern set, and the cue is armed.
    /// </summary>
    public bool CanFireMidiTrigger => MidiTriggerEnabled && HasMidiTrigger && Armed;

    // ── Cue OSC trigger ─────────────────────────────────────────────────────

    private bool _oscTriggerEnabled;
    private string _oscTriggerAddress = string.Empty;

    /// <summary>
    /// When true and an OSC address is set, matching received OSC GO's this cue.
    /// </summary>
    public bool OscTriggerEnabled
    {
        get => _oscTriggerEnabled;
        set
        {
            if (_oscTriggerEnabled == value) return;
            _oscTriggerEnabled = value;
            OscTriggerChanged?.Invoke();
        }
    }

    /// <summary>OSC address path that GO's this cue (e.g. <c>/my/cue</c>). Empty = none.</summary>
    public string OscTriggerAddress
    {
        get => _oscTriggerAddress ?? string.Empty;
        set
        {
            string next = value?.Trim() ?? string.Empty;
            if (_oscTriggerAddress == next) return;
            _oscTriggerAddress = next;
            OscTriggerChanged?.Invoke();
        }
    }

    /// <summary>True when OSC trigger differs from default (off / empty).</summary>
    public bool IsOscTriggerNonDefault =>
        OscTriggerEnabled || !string.IsNullOrEmpty(OscTriggerAddress);

    /// <summary>True when enabled, path set, and cue is armed.</summary>
    public bool CanFireOscTrigger =>
        OscTriggerEnabled && !string.IsNullOrEmpty(OscTriggerAddress) && Armed;

    /// <summary>Exact address match (case-sensitive).</summary>
    public bool OscTriggerMatches(string address) =>
        !string.IsNullOrEmpty(address)
        && string.Equals(OscTriggerAddress, address, StringComparison.Ordinal);

    /// <summary>Raised when OSC trigger enable/address changes.</summary>
    public event Action OscTriggerChanged;

    // Events
    public event Action<string> NameChanged;
    public event Action<string> CueNumChanged;
    public event Action<string> NotesChanged;
    public event Action<bool> MemoChanged;
    public event Action<double> PreWaitChanged;
    public event Action<double> DurationChanged;
    public event Action<double> TotalDurationChanged;
    public event Action<double> PostWaitChanged;
    public event Action<Color> ColorChanged;
    public event Action<FollowType> FollowChanged;
    public event Action<bool> ArmedChanged;
    public event Action<bool> SkipIfDisarmedChanged;

    /// <summary>Raised when hotkey enable state or binding changes.</summary>
    public event Action HotkeyChanged;

    /// <summary>Raised when clock enable state or target time changes.</summary>
    public event Action ClockChanged;

    /// <summary>Raised when MIDI trigger enable state or pattern changes.</summary>
    public event Action MidiTriggerChanged;

    
    
    public List<ICueComponent> Components = new List<ICueComponent>();
    
    public Cue() // // Default constructor for base cue
    {
        Id = _nextId++;
        _name = "New cue number " + Id.ToString();
        _cueNum = Id.ToString();
        Color = new Color(0f, 0f, 0f, 1.0f);
    }
    
    

    public Cue(Dictionary data) // Load from saved data - Using full namespace
    {
        if (!data.ContainsKey("Id"))
        {
            GD.PrintErr("Cue:Constructor - Missing 'Id' key in data.");
            return;
        }
        Id = data["Id"].AsInt32();
        if (Id >= _nextId) _nextId = Id + 1;
        Name = data.ContainsKey("Name") ? (string)data["Name"] : "Unnamed Cue";
        _cueNum = data.ContainsKey("CueNum") ? (string)data["CueNum"] : Id.ToString();
        ParentId = data.ContainsKey("ParentId") ? (int)data["ParentId"] : -1;
        if (data.ContainsKey("ChildCues"))
        {
            var childArray = data["ChildCues"].AsGodotArray();
            foreach (var childInt in childArray)
            {
                ChildCues.Add(childInt.AsInt32());
            }
        }
        PreWait = data.ContainsKey("PreWait") ? (double)data["PreWait"] : 0.0;
        Duration = data.ContainsKey("Duration") ? (double)data["Duration"] : 0.0;
        TotalDuration = data.ContainsKey("TotalDuration") ? (double)data["TotalDuration"] : 0.0;
        PostWait = data.ContainsKey("PostWait") ? (double)data["PostWait"] : 0.0;
        _follow = data.ContainsKey("Follow") ? (FollowType)(int)data["Follow"] : FollowType.None;
        Expanded = data.TryGetValue("Expanded", out var expVal) ? expVal.AsBool() : false;
        Color = data.TryGetValue("Color", out var value) ? Color.FromString(value.AsString(), Color) : Color;
        // Missing keys (legacy saves) default to armed / not skip / multiple instances allowed.
        _armed = data.TryGetValue("Armed", out var armedVal) ? armedVal.AsBool() : true;
        _skipIfDisarmed = data.TryGetValue("SkipIfDisarmed", out var skipVal) && skipVal.AsBool();
        _onlyOneActiveInstance = data.TryGetValue("OnlyOneActiveInstance", out var onlyOneVal)
            && onlyOneVal.AsBool();
        _notes = data.TryGetValue("Notes", out var notesVal) ? notesVal.AsString() : string.Empty;
        _memo = data.TryGetValue("Memo", out var memoVal) && memoVal.AsBool();

        LoadHotkeyFromData(data);
        LoadClockFromData(data);
        LoadMidiTriggerFromData(data);
        LoadOscTriggerFromData(data);

        if (data.ContainsKey("Components"))
        {
            var compData = data["Components"].AsGodotArray();
            foreach (var compVar in compData)
            {
                if (compVar.VariantType != Variant.Type.Dictionary)
                {
                    GD.PrintErr("Cue:Constructor - Component data is not a dictionary.");
                    continue;
                }
                var compHash = compVar.AsGodotDictionary();
                if (!compHash.ContainsKey("Type"))
                {
                    GD.PrintErr("Cue:Constructor - Missing 'Type' in component data.");
                    continue;
                }
                string type = (string)compHash["Type"];
                // "Network" stub removed (P2-05) — treat as unknown if present in old data.
                ICueComponent comp = type switch
                {
                    "Audio" => new AudioComponent(),
                    "Video" => new VideoComponent(),
                    "Text" => new TextComponent(),
                    "CueLight" => new CueLightComponent(),
                    "OscComponent" => new OscComponent(),
                    "Control" => new ControlComponent(),
                    "MidiOutput" => new MidiOutputComponent(),
                    _ => null
                };
                if (comp != null)
                {
                    try
                    {
                        comp.LoadFromData(compHash);
                        Components.Add(comp);
                    }
                    catch (Exception ex)
                    {
                        GD.PrintErr($"Cue:Constructor - Error loading component '{type}': {ex.Message}");
                    }
                }
                else
                {
                    GD.PrintErr($"Cue:Constructor - Unknown or unsupported component type '{type}' (skipped).");
                }
            }
        }
    }
    
    // Methods to add components dynamically
    /// <summary>
    /// Adds an audio component to this cue.
    /// </summary>
    /// <param name="audioFile">Path to the audio media file (show-relative or absolute).</param>
    /// <param name="patch">
    /// Optional output patch override. When null, show audio defaults choose the output
    /// (Preferred Default Patch, a specific patch, direct device, or none).
    /// </param>
    /// <returns>The new or existing audio component.</returns>
    public AudioComponent AddAudioComponent(string audioFile, AudioOutputPatch patch = null)
    {
        if (Components.FirstOrDefault(c => c.Type == "Audio") is AudioComponent existing)
        {
            GD.Print($"Cue:AddAudioComponent - Audio component already exists in cue {Id}. Returning existing.");
            return existing;
        }

        var audioComp = new AudioComponent { AudioFile = audioFile };
        // Show defaults (volume, pan, fades, output, …). Explicit patch param wins after.
        ResolveSettings()?.ApplyAudioDefaults(audioComp);
        if (patch != null)
        {
            audioComp.Patch = patch;
            audioComp.PatchId = patch.Id;
            audioComp.DirectOutput = null;
        }
        Components.Add(audioComp);
        return audioComp;
    }
    
    public AudioComponent GetAudioComponent()
    {
        return Components.FirstOrDefault(c => c.Type == "Audio", defaultValue:null) as AudioComponent;
    }
    
    public VideoComponent GetVideoComponent()
    {
        return Components.FirstOrDefault(c => c.Type == "Video", defaultValue:null) as VideoComponent;
    }

    /// <summary>
    /// Returns the text overlay component on this cue, if any.
    /// </summary>
    /// <returns>The text component, or null.</returns>
    public TextComponent GetTextComponent()
    {
        return Components.FirstOrDefault(c => c.Type == "Text", defaultValue: null) as TextComponent;
    }

    /// <summary>
    /// Adds a text overlay component to this cue (one per cue).
    /// </summary>
    /// <returns>The new or existing text component.</returns>
    /// <remarks>
    /// Defaults come from show Text Defaults (target layer, typography, duration, fades, …).
    /// Content starts empty.
    /// </remarks>
    public TextComponent AddTextComponent()
    {
        if (Components.FirstOrDefault(c => c.Type == "Text") is TextComponent existing)
        {
            GD.Print($"Cue:AddTextComponent - Text component already exists in cue {Id}. Returning existing.");
            return existing;
        }

        var textComp = new TextComponent();
        // Includes target layer default (First available / specific / No Output).
        ResolveSettings()?.ApplyTextDefaults(textComp);
        textComp.RecalculateDuration();
        Components.Add(textComp);
        return textComp;
    }

    /// <summary>
    /// Adds a video component to this cue.
    /// </summary>
    /// <param name="videoFile">Path to the video/image media file (show-relative or absolute).</param>
    /// <returns>The new or existing video component.</returns>
    /// <remarks>
    /// Target layer and embedded-audio output come from show Video Defaults
    /// (First available / Preferred by factory default).
    /// </remarks>
    public VideoComponent AddVideoComponent(string videoFile)
    {
        if (Components.FirstOrDefault(c => c.Type == "Video") is VideoComponent existing)
        {
            GD.Print($"Cue:AddVideoComponent - Video component already exists in cue {Id}. Returning existing.");
            return existing;
        }
        var videoComp = new VideoComponent { VideoFile = videoFile };
        videoComp.RefreshIsImageFromPath();
        if (videoComp.IsImage)
        {
            // Still images: no in/out points; hold duration comes from video defaults.
            videoComp.StartTime = 0;
            videoComp.EndTime = -1;
        }
        // Apply show defaults (layout, loop, audio, fades, image hold, target layer, output, etc.).
        ResolveSettings()?.ApplyVideoDefaults(videoComp);

        //videoComp.ExtractAudioIfPresent(videoFile, globalSignals);
        Components.Add(videoComp);
        return videoComp;
    }

    /// <summary>
    /// Resolves the live <see cref="Settings"/> instance from the scene tree, if available.
    /// </summary>
    /// <returns>Settings node, or null when the tree / GlobalData is unavailable.</returns>
    private static Settings ResolveSettings()
    {
        try
        {
            if (Engine.GetMainLoop() is not SceneTree tree)
                return null;
            return tree.Root?.GetNodeOrNull<GlobalData>("/root/GlobalData")?.Settings;
        }
        catch (Exception ex)
        {
            GD.PrintErr($"Cue:ResolveSettings - {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Adds a ICueComponent to this cue
    /// </summary>
    /// <param name="component"></param>
    public void AddICueComponent(ICueComponent component)
    {
        Components.Add(component);
    }

    public void RemoveICueComponent(ICueComponent component)
    {
        Components.Remove(component);
    }
    
    public CueLightComponent[] GetCueLightComponents()
    {
        return Components.OfType<CueLightComponent>().ToArray();
    }

    public OscComponent[] GetOscComponents()
    {
        return Components.OfType<OscComponent>().ToArray();
    }

    /// <summary>
    /// Returns all MIDI output components on this cue.
    /// </summary>
    public MidiOutputComponent[] GetMidiOutputComponents()
    {
        return Components.OfType<MidiOutputComponent>().ToArray();
    }

    /// <summary>
    /// Returns all control components on this cue.
    /// </summary>
    /// <returns>Array of <see cref="ControlComponent"/> instances (may be empty).</returns>
    public ControlComponent[] GetControlComponents()
    {
        return Components.OfType<ControlComponent>().ToArray();
    }

    public double CalculateTotalDuration()
    {
        var contentsDuration = 0.0;
        foreach (var component in Components)
        {
            if (component.Type == "Audio")
            {
                if (((AudioComponent)component).Loop == true)
                {
                    contentsDuration = -1;
                    break;
                }
                ((AudioComponent)component).RecalculateDuration();
                var componentDuration = ((AudioComponent)component).TotalDuration;
                if (contentsDuration < componentDuration) contentsDuration = componentDuration;
            }
            else if (component.Type == "Video")
            {
                var video = (VideoComponent)component;
                video.RecalculateDuration();
                // Infinite: video loop, or image hold with Duration 0 (until stopped).
                if (video.Loop || video.TotalDuration < 0)
                {
                    contentsDuration = -1;
                    break;
                }
                var componentDuration = video.TotalDuration;
                if (contentsDuration < componentDuration) contentsDuration = componentDuration;
            }
            else if (component.Type == "Text")
            {
                var text = (TextComponent)component;
                // When video closed captions drive this text component, timing follows video —
                // do not treat text hold-until-stopped as infinite shell duration.
                var videoForCc = GetVideoComponent();
                if (videoForCc != null && videoForCc.UseSubtitles && !videoForCc.IsImage)
                    continue;

                text.RecalculateDuration();
                // Duration 0 = until stopped (infinite for shell timing).
                if (text.TotalDuration < 0)
                {
                    contentsDuration = -1;
                    break;
                }
                if (contentsDuration < text.TotalDuration)
                    contentsDuration = text.TotalDuration;
            }
            else if (component is ControlComponent control)
            {
                // Timed fades (property / stop / GO fade-in / layer translate) occupy content time.
                float sessionStop = 0f;
                try
                {
                    if (Engine.GetMainLoop() is SceneTree st)
                    {
                        var gd = st.Root.GetNodeOrNull<GlobalData>("/root/GlobalData");
                        sessionStop = gd?.Settings?.StopFadeDuration ?? 0f;
                    }
                }
                catch
                {
                    /* offline / tests */
                }

                double controlDur = control.GetContentDurationSeconds(sessionStop);
                if (controlDur > contentsDuration)
                    contentsDuration = controlDur;
            }
        }

        // If loop
        if (contentsDuration == -1)
        {
            Duration = -1;
            TotalDuration = -1;
            return TotalDuration;
        }
        
        var childDuration = DurationOfChildren();
        if (childDuration == -1)
        {
            Duration = -1;
            TotalDuration = -1;
            return TotalDuration;
        }
        if (childDuration > contentsDuration) contentsDuration = childDuration;
        Duration = contentsDuration;
        TotalDuration = PreWait + contentsDuration + PostWait;
        return TotalDuration;
    }

    /// <summary>
    /// Longest nested child occupancy under this group (content origin shared with parent body).
    /// </summary>
    /// <remarks>
    /// Each child contributes <c>PreWait + Duration</c> only. Child <see cref="PostWait"/> is
    /// sequence lead-in for siblings / follow chains, not nested body length, so it must not
    /// inflate the parent group duration (or head progress / timeline).
    /// </remarks>
    /// <returns>Max nested span in seconds, or -1 if any child has infinite content.</returns>
    private double DurationOfChildren()
    {
        var longestDuration = 0.0;
        foreach (var childId in ChildCues)
        {
            var childCue = CueList.FetchCueFromId(childId);
            if (childCue == null)
                continue;

            // Refresh child's Duration (includes that child's own nested content) without using
            // child TotalDuration, which incorrectly added PostWait into the parent span.
            childCue.CalculateTotalDuration();
            if (childCue.Duration < 0)
                return -1; // Loop / until-stopped content under the child tree

            double nestedSpan = Math.Max(0.0, childCue.PreWait) + Math.Max(0.0, childCue.Duration);
            if (nestedSpan > longestDuration)
                longestDuration = nestedSpan;
        }
        return longestDuration;
    }
    
    public void AddChildCue(int childId)
    {
        ChildCues.Add(childId);
    }

    public void RemoveChildCue(int childId)
    {
        ChildCues.Remove(childId);
    }

    public void SetParent(int parentId)
    {
        ParentId = parentId;
    }

    // ── Hotkey helpers ──────────────────────────────────────────────────────

    /// <summary>
    /// Sets the cue hotkey from a key event (key + modifiers). Does not change <see cref="HotkeyEnabled"/>.
    /// </summary>
    /// <param name="source">Pressed key event to capture.</param>
    public void SetHotkey(InputEventKey source)
    {
        if (source == null)
        {
            ClearHotkey();
            return;
        }

        HotkeyKeycode = source.Keycode;
        HotkeyPhysicalKeycode = source.PhysicalKeycode;
        HotkeyCtrl = source.CtrlPressed;
        HotkeyShift = source.ShiftPressed;
        HotkeyAlt = source.AltPressed;
        HotkeyMeta = source.MetaPressed;
        HotkeyChanged?.Invoke();
    }

    /// <summary>
    /// Clears the hotkey binding back to default (no key). Does not change <see cref="HotkeyEnabled"/>.
    /// </summary>
    public void ClearHotkey()
    {
        if (!HasHotkey) return;
        HotkeyKeycode = Key.None;
        HotkeyPhysicalKeycode = Key.None;
        HotkeyCtrl = false;
        HotkeyShift = false;
        HotkeyAlt = false;
        HotkeyMeta = false;
        HotkeyChanged?.Invoke();
    }

    /// <summary>
    /// Resets hotkey to factory default: unbound and disabled.
    /// </summary>
    public void ResetHotkeyToDefault()
    {
        bool changed = HasHotkey || _hotkeyEnabled;
        HotkeyKeycode = Key.None;
        HotkeyPhysicalKeycode = Key.None;
        HotkeyCtrl = false;
        HotkeyShift = false;
        HotkeyAlt = false;
        HotkeyMeta = false;
        _hotkeyEnabled = false;
        if (changed)
            HotkeyChanged?.Invoke();
    }

    /// <summary>
    /// Returns true if <paramref name="keyEvent"/> matches this cue's bound hotkey (key + modifiers).
    /// </summary>
    /// <param name="keyEvent">Key event to test.</param>
    /// <returns><c>true</c> when bound and matching.</returns>
    public bool HotkeyMatches(InputEventKey keyEvent)
    {
        if (!HasHotkey || keyEvent == null) return false;

        // Build a synthetic event from stored fields for comparison.
        var stored = new InputEventKey
        {
            Keycode = HotkeyKeycode,
            PhysicalKeycode = HotkeyPhysicalKeycode,
            CtrlPressed = HotkeyCtrl,
            ShiftPressed = HotkeyShift,
            AltPressed = HotkeyAlt,
            MetaPressed = HotkeyMeta,
        };
        return GlobalData.KeyEventsMatch(stored, keyEvent);
    }

    /// <summary>
    /// Readable hotkey string (e.g. "Ctrl+G"), or empty when unbound.
    /// </summary>
    public string GetHotkeyDisplay()
    {
        if (!HasHotkey) return string.Empty;
        var ev = new InputEventKey
        {
            Keycode = HotkeyKeycode != Key.None ? HotkeyKeycode : HotkeyPhysicalKeycode,
            PhysicalKeycode = HotkeyPhysicalKeycode,
            CtrlPressed = HotkeyCtrl,
            ShiftPressed = HotkeyShift,
            AltPressed = HotkeyAlt,
            MetaPressed = HotkeyMeta,
        };
        return GlobalData.FormatInputEvent(ev);
    }

    /// <summary>
    /// Writes hotkey fields into a serialization dictionary.
    /// </summary>
    private void WriteHotkeyToData(Dictionary dict)
    {
        dict["HotkeyEnabled"] = HotkeyEnabled;
        dict["HotkeyKeycode"] = (int)HotkeyKeycode;
        dict["HotkeyPhysicalKeycode"] = (int)HotkeyPhysicalKeycode;
        dict["HotkeyCtrl"] = HotkeyCtrl;
        dict["HotkeyShift"] = HotkeyShift;
        dict["HotkeyAlt"] = HotkeyAlt;
        dict["HotkeyMeta"] = HotkeyMeta;
    }

    /// <summary>
    /// Loads hotkey fields from saved data (constructor path; no events).
    /// </summary>
    private void LoadHotkeyFromData(Dictionary data)
    {
        _hotkeyEnabled = data.TryGetValue("HotkeyEnabled", out var en) && en.AsBool();
        HotkeyKeycode = data.TryGetValue("HotkeyKeycode", out var kc)
            ? (Key)kc.AsInt32()
            : Key.None;
        HotkeyPhysicalKeycode = data.TryGetValue("HotkeyPhysicalKeycode", out var pk)
            ? (Key)pk.AsInt32()
            : Key.None;
        HotkeyCtrl = data.TryGetValue("HotkeyCtrl", out var c) && c.AsBool();
        HotkeyShift = data.TryGetValue("HotkeyShift", out var s) && s.AsBool();
        HotkeyAlt = data.TryGetValue("HotkeyAlt", out var a) && a.AsBool();
        HotkeyMeta = data.TryGetValue("HotkeyMeta", out var m) && m.AsBool();
    }

    /// <summary>
    /// Applies hotkey fields from history/undo data and notifies listeners when changed.
    /// </summary>
    private void ApplyHotkeyFromData(Dictionary data)
    {
        bool prevEnabled = _hotkeyEnabled;
        Key prevKey = HotkeyKeycode;
        Key prevPhys = HotkeyPhysicalKeycode;
        bool prevCtrl = HotkeyCtrl;
        bool prevShift = HotkeyShift;
        bool prevAlt = HotkeyAlt;
        bool prevMeta = HotkeyMeta;

        LoadHotkeyFromData(data);

        bool changed = prevEnabled != _hotkeyEnabled ||
                       prevKey != HotkeyKeycode ||
                       prevPhys != HotkeyPhysicalKeycode ||
                       prevCtrl != HotkeyCtrl ||
                       prevShift != HotkeyShift ||
                       prevAlt != HotkeyAlt ||
                       prevMeta != HotkeyMeta;
        if (changed)
            HotkeyChanged?.Invoke();
    }

    // ── Clock trigger helpers ───────────────────────────────────────────────

    /// <summary>
    /// Sets the wall-clock target time of day. Does not change <see cref="ClockEnabled"/>.
    /// </summary>
    /// <param name="timeOfDay">Local time of day (fractional day components beyond 24h are normalized via <see cref="TimeSpan"/>).</param>
    public void SetClockTime(TimeSpan timeOfDay)
    {
        // Normalize into [0, 24h).
        double total = timeOfDay.TotalSeconds % 86400.0;
        if (total < 0) total += 86400.0;
        var normalized = TimeSpan.FromSeconds(total);

        if (HasClockTime && ClockTimeOfDay == normalized) return;

        HasClockTime = true;
        ClockTimeOfDay = normalized;
        ClockChanged?.Invoke();
    }

    /// <summary>
    /// Clears the clock time back to unset. Does not change <see cref="ClockEnabled"/>.
    /// </summary>
    public void ClearClockTime()
    {
        if (!HasClockTime) return;
        HasClockTime = false;
        ClockTimeOfDay = TimeSpan.Zero;
        ClockChanged?.Invoke();
    }

    /// <summary>
    /// Resets clock trigger to factory default: no time, disabled, every day.
    /// </summary>
    public void ResetClockToDefault()
    {
        bool changed = HasClockTime || _clockEnabled || ClockDaysMask != ClockDaysAll;
        HasClockTime = false;
        ClockTimeOfDay = TimeSpan.Zero;
        ClockDaysMask = ClockDaysAll;
        _clockEnabled = false;
        if (changed)
            ClockChanged?.Invoke();
    }

    /// <summary>
    /// Returns whether the clock trigger is enabled on the given weekday.
    /// </summary>
    /// <param name="day">Weekday to query.</param>
    public bool IsClockDayEnabled(DayOfWeek day)
    {
        int bit = (int)day;
        if (bit < 0 || bit > 6) return false;
        return (ClockDaysMask & (1 << bit)) != 0;
    }

    /// <summary>
    /// Enables or disables the clock trigger for a single weekday.
    /// </summary>
    /// <param name="day">Weekday to change.</param>
    /// <param name="enabled">Whether the cue may fire on that day.</param>
    public void SetClockDayEnabled(DayOfWeek day, bool enabled)
    {
        int bit = (int)day;
        if (bit < 0 || bit > 6) return;

        byte next = enabled
            ? (byte)(ClockDaysMask | (1 << bit))
            : (byte)(ClockDaysMask & ~(1 << bit));
        if (next == ClockDaysMask) return;

        ClockDaysMask = next;
        ClockChanged?.Invoke();
    }

    /// <summary>
    /// Replaces the full weekday mask for the clock trigger.
    /// </summary>
    /// <param name="mask">Bitmask of enabled days (bits 0–6; see <see cref="ClockDaysAll"/>).</param>
    public void SetClockDaysMask(byte mask)
    {
        byte next = (byte)(mask & ClockDaysAll);
        if (next == ClockDaysMask) return;
        ClockDaysMask = next;
        ClockChanged?.Invoke();
    }

    /// <summary>
    /// Readable 24h clock string (e.g. "14:30:00"), or empty when unset.
    /// </summary>
    public string GetClockDisplay()
    {
        if (!HasClockTime) return string.Empty;
        return FormatClockTimeOfDay(ClockTimeOfDay);
    }

    /// <summary>
    /// Formats a time-of-day as <c>HH:mm:ss</c>.
    /// </summary>
    public static string FormatClockTimeOfDay(TimeSpan timeOfDay)
    {
        int hours = (int)timeOfDay.TotalHours;
        if (hours < 0) hours = 0;
        if (hours > 23) hours %= 24;
        return $"{hours:D2}:{timeOfDay.Minutes:D2}:{timeOfDay.Seconds:D2}";
    }

    /// <summary>
    /// Returns true if local wall time crossed this cue's clock target between
    /// <paramref name="previous"/> (exclusive) and <paramref name="current"/> (inclusive)
    /// on an enabled weekday.
    /// Used so the cue fires once per selected day at the moment of reaching the time, not retroactively.
    /// </summary>
    /// <param name="previous">Previous sample of local time.</param>
    /// <param name="current">Current sample of local time.</param>
    public bool ClockCrossedBetween(DateTime previous, DateTime current)
    {
        if (!HasClockTime) return false;
        if (ClockDaysMask == 0) return false;
        if (current <= previous) return false;

        // Next candidate at the target time of day after previous.
        var candidate = previous.Date + ClockTimeOfDay;
        if (candidate <= previous)
            candidate = candidate.AddDays(1);

        // Walk forward (handles multi-day frame skips / disabled weekdays).
        for (int i = 0; i < 8 && candidate <= current; i++)
        {
            if (candidate > previous && IsClockDayEnabled(candidate.DayOfWeek))
                return true;
            candidate = candidate.AddDays(1);
        }

        return false;
    }

    /// <summary>
    /// Writes clock trigger fields into a serialization dictionary.
    /// </summary>
    private void WriteClockToData(Dictionary dict)
    {
        dict["ClockEnabled"] = ClockEnabled;
        dict["HasClockTime"] = HasClockTime;
        // Total seconds from midnight (double for future sub-second if needed).
        dict["ClockSecondsOfDay"] = HasClockTime ? ClockTimeOfDay.TotalSeconds : 0.0;
        dict["ClockDaysMask"] = (int)ClockDaysMask;
    }

    /// <summary>
    /// Loads clock fields from saved data (constructor path; no events).
    /// </summary>
    private void LoadClockFromData(Dictionary data)
    {
        _clockEnabled = data.TryGetValue("ClockEnabled", out var en) && en.AsBool();
        HasClockTime = data.TryGetValue("HasClockTime", out var has) && has.AsBool();
        if (HasClockTime && data.TryGetValue("ClockSecondsOfDay", out var secs))
        {
            double total = secs.AsDouble();
            if (total < 0) total = 0;
            if (total >= 86400.0) total %= 86400.0;
            ClockTimeOfDay = TimeSpan.FromSeconds(total);
        }
        else
        {
            HasClockTime = false;
            ClockTimeOfDay = TimeSpan.Zero;
        }

        // Legacy saves without the key default to every day.
        if (data.TryGetValue("ClockDaysMask", out var days))
            ClockDaysMask = (byte)(days.AsInt32() & ClockDaysAll);
        else
            ClockDaysMask = ClockDaysAll;
    }

    /// <summary>
    /// Applies clock fields from history/undo data and notifies listeners when changed.
    /// </summary>
    private void ApplyClockFromData(Dictionary data)
    {
        bool prevEnabled = _clockEnabled;
        bool prevHas = HasClockTime;
        TimeSpan prevTime = ClockTimeOfDay;
        byte prevDays = ClockDaysMask;

        LoadClockFromData(data);

        bool changed = prevEnabled != _clockEnabled ||
                       prevHas != HasClockTime ||
                       prevTime != ClockTimeOfDay ||
                       prevDays != ClockDaysMask;
        if (changed)
            ClockChanged?.Invoke();
    }

    // ── MIDI trigger helpers ────────────────────────────────────────────────

    /// <summary>
    /// Sets the MIDI trigger pattern. Does not change <see cref="MidiTriggerEnabled"/>.
    /// </summary>
    /// <param name="type">Message type to match.</param>
    /// <param name="channel">Channel 1–16, or 0 for any.</param>
    /// <param name="data1">Note / CC / program number (0–127).</param>
    /// <param name="data2">Velocity / value (0–127); used only when <paramref name="matchValue"/> is true.</param>
    /// <param name="matchValue">When true, require exact <paramref name="data2"/>.</param>
    /// <param name="deviceFilter">Optional device name filter; empty = any device.</param>
    public void SetMidiTrigger(
        MidiTriggerMessageType type,
        int channel,
        int data1,
        int data2 = 0,
        bool matchValue = false,
        string deviceFilter = null)
    {
        channel = Math.Clamp(channel, 0, 16);
        data1 = Math.Clamp(data1, 0, 127);
        data2 = Math.Clamp(data2, 0, 127);
        deviceFilter ??= string.Empty;

        if (HasMidiTrigger &&
            MidiMessageType == type &&
            MidiChannel == channel &&
            MidiData1 == data1 &&
            MidiData2 == data2 &&
            MidiMatchValue == matchValue &&
            string.Equals(MidiDeviceFilter, deviceFilter, StringComparison.Ordinal))
        {
            return;
        }

        HasMidiTrigger = true;
        MidiMessageType = type;
        MidiChannel = channel;
        MidiData1 = data1;
        MidiData2 = data2;
        MidiMatchValue = matchValue;
        MidiDeviceFilter = deviceFilter;
        MidiTriggerChanged?.Invoke();
    }

    /// <summary>
    /// Clears the MIDI trigger pattern. Does not change <see cref="MidiTriggerEnabled"/>.
    /// </summary>
    public void ClearMidiTrigger()
    {
        if (!HasMidiTrigger) return;
        HasMidiTrigger = false;
        MidiMessageType = MidiTriggerMessageType.NoteOn;
        MidiChannel = 0;
        MidiData1 = 0;
        MidiData2 = 0;
        MidiMatchValue = false;
        MidiDeviceFilter = string.Empty;
        MidiTriggerChanged?.Invoke();
    }

    /// <summary>
    /// Resets MIDI trigger to factory default: no pattern and disabled.
    /// </summary>
    public void ResetMidiTriggerToDefault()
    {
        bool changed = HasMidiTrigger || _midiTriggerEnabled;
        HasMidiTrigger = false;
        MidiMessageType = MidiTriggerMessageType.NoteOn;
        MidiChannel = 0;
        MidiData1 = 0;
        MidiData2 = 0;
        MidiMatchValue = false;
        MidiDeviceFilter = string.Empty;
        _midiTriggerEnabled = false;
        if (changed)
            MidiTriggerChanged?.Invoke();
    }

    /// <summary>
    /// Readable summary (e.g. "NoteOn ch1 n60" or "CC ch* cc7=64").
    /// </summary>
    public string GetMidiTriggerDisplay()
    {
        if (!HasMidiTrigger) return string.Empty;

        string ch = MidiChannel == 0 ? "ch*" : $"ch{MidiChannel}";
        string typeLabel = MidiMessageType switch
        {
            MidiTriggerMessageType.NoteOn => "NoteOn",
            MidiTriggerMessageType.NoteOff => "NoteOff",
            MidiTriggerMessageType.ControlChange => "CC",
            MidiTriggerMessageType.ProgramChange => "Program",
            _ => MidiMessageType.ToString()
        };

        string core = MidiMessageType switch
        {
            MidiTriggerMessageType.NoteOn or MidiTriggerMessageType.NoteOff =>
                MidiMatchValue ? $"{typeLabel} {ch} n{MidiData1} v{MidiData2}" : $"{typeLabel} {ch} n{MidiData1}",
            MidiTriggerMessageType.ControlChange =>
                MidiMatchValue ? $"{typeLabel} {ch} cc{MidiData1}={MidiData2}" : $"{typeLabel} {ch} cc{MidiData1}",
            MidiTriggerMessageType.ProgramChange =>
                $"{typeLabel} {ch} p{MidiData1}",
            _ => $"{typeLabel} {ch} {MidiData1}"
        };

        if (!string.IsNullOrEmpty(MidiDeviceFilter))
            core += $" @{MidiDeviceFilter}";
        return core;
    }

    /// <summary>
    /// Returns true when <paramref name="type"/>/<paramref name="channel"/>/<paramref name="data1"/>/<paramref name="data2"/>
    /// from <paramref name="deviceName"/> matches this cue's MIDI trigger pattern.
    /// </summary>
    public bool MidiTriggerMatches(
        MidiTriggerMessageType type,
        int channel,
        int data1,
        int data2,
        string deviceName = null)
    {
        if (!HasMidiTrigger) return false;

        if (MidiMessageType != type) return false;

        // Channel: 0 = any; stored/UI is 1–16, incoming channel should also be 1–16.
        if (MidiChannel != 0 && MidiChannel != channel) return false;

        if (MidiData1 != data1) return false;

        if (MidiMatchValue && MidiMessageType != MidiTriggerMessageType.ProgramChange)
        {
            if (MidiData2 != data2) return false;
        }

        if (!string.IsNullOrEmpty(MidiDeviceFilter))
        {
            if (string.IsNullOrEmpty(deviceName) ||
                !string.Equals(MidiDeviceFilter, deviceName, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    private void WriteMidiTriggerToData(Dictionary dict)
    {
        dict["MidiTriggerEnabled"] = MidiTriggerEnabled;
        dict["HasMidiTrigger"] = HasMidiTrigger;
        dict["MidiMessageType"] = (int)MidiMessageType;
        dict["MidiChannel"] = MidiChannel;
        dict["MidiData1"] = MidiData1;
        dict["MidiData2"] = MidiData2;
        dict["MidiMatchValue"] = MidiMatchValue;
        dict["MidiDeviceFilter"] = MidiDeviceFilter ?? string.Empty;
    }

    private void WriteOscTriggerToData(Dictionary dict)
    {
        dict["OscTriggerEnabled"] = OscTriggerEnabled;
        dict["OscTriggerAddress"] = OscTriggerAddress ?? string.Empty;
    }

    private void LoadOscTriggerFromData(Dictionary data)
    {
        if (data == null) return;
        _oscTriggerEnabled = data.TryGetValue("OscTriggerEnabled", out var en) && en.AsBool();
        _oscTriggerAddress = data.TryGetValue("OscTriggerAddress", out var addr)
            ? addr.AsString() ?? string.Empty
            : string.Empty;
    }

    private void ApplyOscTriggerFromData(Dictionary data)
    {
        bool prevEn = _oscTriggerEnabled;
        string prevAddr = _oscTriggerAddress;
        LoadOscTriggerFromData(data);
        if (prevEn != _oscTriggerEnabled
            || !string.Equals(prevAddr, _oscTriggerAddress, StringComparison.Ordinal))
            OscTriggerChanged?.Invoke();
    }

    private void LoadMidiTriggerFromData(Dictionary data)
    {
        _midiTriggerEnabled = data.TryGetValue("MidiTriggerEnabled", out var en) && en.AsBool();
        HasMidiTrigger = data.TryGetValue("HasMidiTrigger", out var has) && has.AsBool();
        MidiMessageType = data.TryGetValue("MidiMessageType", out var mt)
            ? (MidiTriggerMessageType)mt.AsInt32()
            : MidiTriggerMessageType.NoteOn;
        MidiChannel = data.TryGetValue("MidiChannel", out var ch) ? Math.Clamp(ch.AsInt32(), 0, 16) : 0;
        MidiData1 = data.TryGetValue("MidiData1", out var d1) ? Math.Clamp(d1.AsInt32(), 0, 127) : 0;
        MidiData2 = data.TryGetValue("MidiData2", out var d2) ? Math.Clamp(d2.AsInt32(), 0, 127) : 0;
        MidiMatchValue = data.TryGetValue("MidiMatchValue", out var mv) && mv.AsBool();
        MidiDeviceFilter = data.TryGetValue("MidiDeviceFilter", out var df) ? df.AsString() : string.Empty;
        if (!HasMidiTrigger)
        {
            MidiMessageType = MidiTriggerMessageType.NoteOn;
            MidiChannel = 0;
            MidiData1 = 0;
            MidiData2 = 0;
            MidiMatchValue = false;
            MidiDeviceFilter = string.Empty;
        }
    }

    private void ApplyMidiTriggerFromData(Dictionary data)
    {
        bool prevEnabled = _midiTriggerEnabled;
        bool prevHas = HasMidiTrigger;
        var prevType = MidiMessageType;
        int prevCh = MidiChannel;
        int prevD1 = MidiData1;
        int prevD2 = MidiData2;
        bool prevMatch = MidiMatchValue;
        string prevDev = MidiDeviceFilter;

        LoadMidiTriggerFromData(data);

        bool changed = prevEnabled != _midiTriggerEnabled ||
                       prevHas != HasMidiTrigger ||
                       prevType != MidiMessageType ||
                       prevCh != MidiChannel ||
                       prevD1 != MidiData1 ||
                       prevD2 != MidiData2 ||
                       prevMatch != MidiMatchValue ||
                       !string.Equals(prevDev, MidiDeviceFilter, StringComparison.Ordinal);
        if (changed)
            MidiTriggerChanged?.Invoke();
    }

    public Dictionary GetData()
    {
        var dict = new Dictionary();
        dict.Add("Id", Id.ToString());
        dict.Add("Name", Name);
        dict.Add("CueNum", CueNum);
        dict.Add("ParentId", ParentId.ToString());
        dict.Add("ChildCues", new Array<int>(ChildCues));
        dict.Add("PreWait", PreWait);
        dict.Add("Duration", Duration);
        dict.Add("TotalDuration", TotalDuration);
        dict.Add("PostWait", PostWait);
        dict.Add("Follow", (int)Follow);
        dict.Add("Expanded", Expanded);
        dict.Add("Color", Color.ToHtml());
        dict.Add("Armed", Armed);
        dict.Add("SkipIfDisarmed", SkipIfDisarmed);
        dict.Add("OnlyOneActiveInstance", OnlyOneActiveInstance);
        dict.Add("Notes", Notes ?? string.Empty);
        dict.Add("Memo", Memo);
        WriteHotkeyToData(dict);
        WriteClockToData(dict);
        WriteMidiTriggerToData(dict);
        WriteOscTriggerToData(dict);

        var compData = new Array();
        foreach (var comp in Components)
        {
            var compDict = comp.GetData();
            compDict.Add("Type", comp.Type);
            compData.Add(compDict);
        }
        dict.Add("Components", compData);

        return dict;
    }

    /// <summary>
    /// Applies serialized cue data onto this instance in place (identity preserved).
    /// Used by scoped undo/redo so a single cue can be restored without rebuilding the list.
    /// </summary>
    /// <param name="data">Dictionary previously produced by <see cref="GetData"/>.</param>
    /// <remarks>
    /// Does not free or recreate <see cref="ShellBar"/>. Hierarchy fields (ParentId, ChildCues)
    /// are applied from data; structural list rebuilds should use full cuelist history instead.
    /// </remarks>
    public void ApplyFromData(Dictionary data)
    {
        if (data == null) return;

        // Identity: keep existing Id; only advance static counter if needed for consistency.
        if (data.ContainsKey("Id"))
        {
            int loadedId = data["Id"].AsInt32();
            if (loadedId != Id)
                GD.PrintErr($"Cue:ApplyFromData - Id mismatch (live={Id}, data={loadedId}); keeping live Id.");
        }

        Name = data.ContainsKey("Name") ? (string)data["Name"] : Name;
        CueNum = data.ContainsKey("CueNum") ? (string)data["CueNum"] : CueNum;
        ParentId = data.ContainsKey("ParentId") ? data["ParentId"].AsInt32() : ParentId;

        ChildCues.Clear();
        if (data.ContainsKey("ChildCues"))
        {
            var childArray = data["ChildCues"].AsGodotArray();
            foreach (var childInt in childArray)
                ChildCues.Add(childInt.AsInt32());
        }

        PreWait = data.ContainsKey("PreWait") ? (double)data["PreWait"] : PreWait;
        Duration = data.ContainsKey("Duration") ? (double)data["Duration"] : Duration;
        TotalDuration = data.ContainsKey("TotalDuration") ? (double)data["TotalDuration"] : TotalDuration;
        PostWait = data.ContainsKey("PostWait") ? (double)data["PostWait"] : PostWait;
        // Assign via field then notify once so UI gets a single FollowChanged after full apply.
        var loadedFollow = data.ContainsKey("Follow") ? (FollowType)(int)data["Follow"] : _follow;
        Expanded = data.TryGetValue("Expanded", out var expVal) ? expVal.AsBool() : Expanded;
        Color = data.TryGetValue("Color", out var colorVal)
            ? Color.FromString(colorVal.AsString(), Color)
            : Color;
        // Assign via properties so ShellBar / inspector listeners refresh after undo.
        Armed = data.TryGetValue("Armed", out var armedVal) ? armedVal.AsBool() : Armed;
        SkipIfDisarmed = data.TryGetValue("SkipIfDisarmed", out var skipVal)
            ? skipVal.AsBool()
            : SkipIfDisarmed;
        OnlyOneActiveInstance = data.TryGetValue("OnlyOneActiveInstance", out var onlyOneVal)
            ? onlyOneVal.AsBool()
            : OnlyOneActiveInstance;
        Notes = data.TryGetValue("Notes", out var notesVal) ? notesVal.AsString() : Notes;
        Memo = data.TryGetValue("Memo", out var memoVal) ? memoVal.AsBool() : Memo;

        ApplyHotkeyFromData(data);
        ApplyClockFromData(data);
        ApplyMidiTriggerFromData(data);
        ApplyOscTriggerFromData(data);

        Components.Clear();
        if (data.ContainsKey("Components"))
        {
            var compData = data["Components"].AsGodotArray();
            foreach (var compVar in compData)
            {
                if (compVar.VariantType != Variant.Type.Dictionary)
                {
                    GD.PrintErr("Cue:ApplyFromData - Component data is not a dictionary.");
                    continue;
                }
                var compHash = compVar.AsGodotDictionary();
                if (!compHash.ContainsKey("Type"))
                {
                    GD.PrintErr("Cue:ApplyFromData - Missing 'Type' in component data.");
                    continue;
                }
                string type = (string)compHash["Type"];
                // "Network" stub removed (P2-05) — skip unknown types quietly after log.
                ICueComponent comp = type switch
                {
                    "Audio" => new AudioComponent(),
                    "Video" => new VideoComponent(),
                    "Text" => new TextComponent(),
                    "CueLight" => new CueLightComponent(),
                    "OscComponent" => new OscComponent(),
                    "Control" => new ControlComponent(),
                    "MidiOutput" => new MidiOutputComponent(),
                    _ => null
                };
                if (comp == null)
                {
                    GD.PrintErr($"Cue:ApplyFromData - Unknown or unsupported component type '{type}' (skipped).");
                    continue;
                }
                try
                {
                    comp.LoadFromData(compHash);
                    Components.Add(comp);
                }
                catch (Exception ex)
                {
                    GD.PrintErr($"Cue:ApplyFromData - Failed to load component '{type}': {ex.Message}");
                }
            }
        }

        _follow = loadedFollow;
        FollowChanged?.Invoke(_follow);
        ShellBar?.RelationshipChanged();
    }

    /// <summary>
    /// Returns the next cue at the same nesting level (next root sibling or next entry in the parent's
    /// <see cref="ChildCues"/> list). Used by auto-continue / auto-follow sequences.
    /// </summary>
    /// <returns>The next sibling cue, or null if this is the last at its level.</returns>
    public Cue GetNextSiblingCue()
    {
        var siblings = ParentId >= 0
            ? CueList.FetchCueFromId(ParentId)?.ChildCues
            : CueList.Live?.RootOrder;
        if (siblings == null)
            return null;

        int idx = siblings.IndexOf(Id);
        if (idx < 0 || idx + 1 >= siblings.Count)
            return null;
        return CueList.FetchCueFromId(siblings[idx + 1]);
    }

    /// <summary>
    /// Next cue after this one in document order: next sibling, or the next sibling of the
    /// nearest ancestor that has one (leaves a group when this is the last child).
    /// </summary>
    /// <returns>The following cue in the cuelist, or null if this is the last cue in the show.</returns>
    public Cue GetNextCueInList()
    {
        var current = this;
        int guard = 0;
        while (current != null && guard++ < 64)
        {
            var sibling = current.GetNextSiblingCue();
            if (sibling != null)
                return sibling;
            if (current.ParentId < 0)
                return null;
            current = CueList.FetchCueFromId(current.ParentId);
        }

        return null;
    }

    /// <summary>
    /// Walks auto-continue / auto-follow links from this cue and returns the last cue that will play
    /// as part of the sequence (the first cue with <see cref="FollowType.None"/>, or the last reachable).
    /// </summary>
    /// <returns>The terminal cue of the sequence starting at this cue (always at least this cue).</returns>
    public Cue GetSequenceEndCue()
    {
        var current = this;
        var guard = 0;
        while (current.Follow != FollowType.None && guard++ < 10000)
        {
            var next = current.GetNextSiblingCue();
            if (next == null) break;
            current = next;
        }
        return current;
    }

    /// <summary>
    /// Returns the first cue after this cue's continue/follow sequence (playhead target after GO),
    /// walking out of a group when the sequence ends on its last child.
    /// </summary>
    /// <returns>The next cue in the cuelist, or null if the sequence ends the show.</returns>
    public Cue GetCueAfterSequence()
    {
        return GetSequenceEndCue().GetNextCueInList();
    }

    /// <summary>
    /// Walks forward from <paramref name="start"/> (inclusive) past cues that should be bypassed
    /// when advancing the playhead (<see cref="ShouldSkipOnPlayhead"/>).
    /// </summary>
    /// <param name="start">First candidate cue, or null.</param>
    /// <returns>The first cue that should receive the playhead, or null if none remain.</returns>
    public static Cue ResolvePlayheadTarget(Cue start)
    {
        var current = start;
        var guard = 0;
        while (current != null && current.ShouldSkipOnPlayhead && guard++ < 10000)
            current = current.GetNextCueInList();
        return current;
    }
    
}
