// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Linq;
using Cue2.Domain.Cuelist;
using Cue2.Domain.Cues;
using Cue2.Domain.Timecode;
using Godot;

namespace Cue2.Services;

/// <summary>
/// Application-wide timecode input/output service.
/// </summary>
/// <remarks>
/// Show-scoped: named session inputs (listen to clocks generated elsewhere) and
/// outputs (generate on this machine and encode as configured). LTC outputs mix
/// onto a chosen playback device channel. MTC / Art-Net / OSC encode later.
/// </remarks>
public partial class TimecodeManager : Node
{
    /// <summary>Showfile and undo key for the timecode settings slice.</summary>
    public const string HistoryKey = "Timecode";

    private GlobalSignals _globalSignals;
    private GlobalData _globalData;
    private MidiManager _midiManager;
    private AudioDevices _audioDevices;

    private readonly List<TimecodeEndpoint> _inputs = new();

    /// <summary>
    /// Read-only copy of <see cref="_inputs"/> for the MIDI callback thread. Main
    /// replaces it after every change to <see cref="_inputs"/>; MTC handling iterates the copy,
    /// so a concurrent Add / Remove / Clear on main can no longer break the enumeration.
    /// </summary>
    private volatile TimecodeEndpoint[] _inputsForMidiThread = System.Array.Empty<TimecodeEndpoint>();

    /// <summary>Publishes the current inputs to <see cref="_inputsForMidiThread"/>. Main thread.</summary>
    private void PublishInputsForMidiThread() => _inputsForMidiThread = _inputs.ToArray();
    private readonly List<TimecodeEndpoint> _outputs = new();

    private int _nextId = 1;
    private bool _timecodeEnabled;
    private readonly object _clockLock = new();

    /// <summary>Last sampled reading per endpoint, used for cue trigger edge detection.</summary>
    private readonly Dictionary<int, TimecodeValue> _triggerPrevValue = new();

    /// <summary>
    /// When true, Internal generators that are running advance, System Clock follows
    /// wall time, and hardware endpoints would be opened.
    /// </summary>
    public bool TimecodeEnabled
    {
        get => _timecodeEnabled;
        set
        {
            if (_timecodeEnabled == value) return;
            _timecodeEnabled = value;
            if (!_timecodeEnabled)
            {
                foreach (var endpoint in _outputs)
                    endpoint.Running = false;
            }

            SyncProcess();
            ReconcileIo();
            EmitSignal(SignalName.TimecodeStateChanged);
        }
    }

    /// <summary>Session-configured input endpoints (order preserved).</summary>
    public IReadOnlyList<TimecodeEndpoint> Inputs => _inputs;

    /// <summary>Session-configured output endpoints (order preserved).</summary>
    public IReadOnlyList<TimecodeEndpoint> Outputs => _outputs;

    /// <summary>Fired when endpoints are added/removed/reloaded or enable state changes.</summary>
    [Signal]
    public delegate void TimecodeStateChangedEventHandler();

    public override void _Ready()
    {
        _globalSignals = GetNodeOrNull<GlobalSignals>("/root/GlobalSignals");
        _globalData = GetNodeOrNull<GlobalData>("/root/GlobalData");
        _midiManager = GetNodeOrNull<MidiManager>("/root/MidiManager");
        _audioDevices = GetNodeOrNull<AudioDevices>("/root/AudioDevices");
        HookOscListen();
        HookMidiSystem();
        if (_globalSignals != null)
            _globalSignals.AudioDevicesChanged += OnAudioDevicesChangedForLtcConflict;
        SetProcess(false);
        GD.Print("TimecodeManager:_Ready - Timecode manager ready.");
    }

    public override void _ExitTree()
    {
        if (_globalSignals != null)
            _globalSignals.AudioDevicesChanged -= OnAudioDevicesChangedForLtcConflict;
        UnhookOscListen();
        UnhookMidiSystem();
        StopLtcFillThread();
        DestroyAllLtcRuntimes();
        base._ExitTree();
    }

    public override void _Process(double delta)
    {
        if (!_timecodeEnabled)
        {
            SetProcess(false);
            return;
        }

        foreach (var endpoint in AllEndpoints())
            TickEndpoint(endpoint, delta);
        ExpireStaleInputLocks();
        SendOscAndMtcOutputs();
        try
        {
            FireCueTimecodeTriggers();
        }
        catch (Exception ex)
        {
            GD.PrintErr($"TimecodeManager:_Process - trigger fire: {ex.Message}");
        }
    }

    /// <summary>
    /// Current reading for <paramref name="id"/>, or zero when the endpoint is missing.
    /// </summary>
    /// <param name="id">Endpoint id.</param>
    /// <returns>Latest timecode value.</returns>
    public TimecodeValue GetValue(int id)
    {
        var endpoint = FindEndpoint(id);
        if (endpoint == null)
            return TimecodeValue.Zero(TimecodeFrameRate.Fps30);
        lock (_clockLock)
        {
            RefreshComputedValue(endpoint);
            return endpoint.CurrentValue;
        }
    }

    /// <summary>
    /// Finds an endpoint by id (input or output).
    /// </summary>
    /// <param name="id">Endpoint id.</param>
    /// <returns>The endpoint, or <see langword="null"/>.</returns>
    public TimecodeEndpoint FindEndpoint(int id)
    {
        for (int i = 0; i < _inputs.Count; i++)
        {
            if (_inputs[i].Id == id)
                return _inputs[i];
        }

        for (int i = 0; i < _outputs.Count; i++)
        {
            if (_outputs[i].Id == id)
                return _outputs[i];
        }

        return null;
    }

    /// <summary>
    /// Adds a new input of <paramref name="kind"/> with a unique default name.
    /// </summary>
    /// <param name="kind">Input kind.</param>
    /// <returns>The created endpoint.</returns>
    public TimecodeEndpoint AddInput(TimecodeKind kind)
    {
        if (!kind.AllowsInput())
        {
            GD.PrintErr($"TimecodeManager:AddInput - Kind {kind} cannot be an input.");
            return null;
        }

        var endpoint = CreateEndpoint(kind, TimecodeDirection.Input);
        _inputs.Add(endpoint);
        PublishInputsForMidiThread();
        SyncProcess();
        ReconcileIo();
        GD.Print($"TimecodeManager:AddInput - {endpoint.Id} '{endpoint.Name}' {kind}");
        _globalSignals?.EmitSignal(nameof(GlobalSignals.Log),
            $"Timecode: added input '{endpoint.Name}'", (int)LogType.Info);
        EmitSignal(SignalName.TimecodeStateChanged);
        return endpoint;
    }

    /// <summary>
    /// Adds a new output of <paramref name="kind"/> with a unique default name.
    /// </summary>
    /// <param name="kind">Output kind (must allow output).</param>
    /// <returns>The created endpoint, or <see langword="null"/> when the kind cannot be an output.</returns>
    public TimecodeEndpoint AddOutput(TimecodeKind kind)
    {
        if (!kind.AllowsOutput())
        {
            GD.PrintErr($"TimecodeManager:AddOutput - Kind {kind} cannot be an output.");
            return null;
        }

        var endpoint = CreateEndpoint(kind, TimecodeDirection.Output);
        _outputs.Add(endpoint);
        SyncProcess();
        ReconcileIo();
        GD.Print($"TimecodeManager:AddOutput - {endpoint.Id} '{endpoint.Name}' {kind}");
        _globalSignals?.EmitSignal(nameof(GlobalSignals.Log),
            $"Timecode: added output '{endpoint.Name}'", (int)LogType.Info);
        EmitSignal(SignalName.TimecodeStateChanged);
        return endpoint;
    }

    /// <summary>
    /// Removes an input or output by id.
    /// </summary>
    /// <param name="id">Endpoint id.</param>
    /// <returns><see langword="true"/> when an endpoint was removed.</returns>
    public bool RemoveEndpoint(int id)
    {
        int inIdx = _inputs.FindIndex(e => e.Id == id);
        if (inIdx >= 0)
        {
            string name = _inputs[inIdx].Name;
            _inputs.RemoveAt(inIdx);
            PublishInputsForMidiThread();
            GD.Print($"TimecodeManager:RemoveEndpoint - Removed input '{name}' (id={id}).");
            _globalSignals?.EmitSignal(nameof(GlobalSignals.Log),
                $"Timecode: removed input '{name}'", (int)LogType.Info);
            SyncProcess();
            EmitSignal(SignalName.TimecodeStateChanged);
            return true;
        }

        int outIdx = _outputs.FindIndex(e => e.Id == id);
        if (outIdx >= 0)
        {
            string name = _outputs[outIdx].Name;
            _outputs.RemoveAt(outIdx);
            GD.Print($"TimecodeManager:RemoveEndpoint - Removed output '{name}' (id={id}).");
            _globalSignals?.EmitSignal(nameof(GlobalSignals.Log),
                $"Timecode: removed output '{name}'", (int)LogType.Info);
            SyncProcess();
            ReconcileIo();
            EmitSignal(SignalName.TimecodeStateChanged);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Renames an endpoint. Empty names are refused.
    /// </summary>
    /// <param name="id">Endpoint id.</param>
    /// <param name="name">New name.</param>
    /// <returns><see langword="true"/> when the name changed.</returns>
    public bool SetName(int id, string name)
    {
        var endpoint = FindEndpoint(id);
        if (endpoint == null) return false;
        string trimmed = name?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(trimmed) || endpoint.Name == trimmed)
            return false;
        endpoint.Name = trimmed;
        return true;
    }

    /// <summary>
    /// Shows or hides this clock on the GO header.
    /// </summary>
    /// <param name="id">Endpoint id.</param>
    /// <param name="show">When true, the clock appears on the right half of the GO header.</param>
    /// <returns><see langword="true"/> when the flag changed.</returns>
    public bool SetShowInHeader(int id, bool show)
    {
        var endpoint = FindEndpoint(id);
        if (endpoint == null || endpoint.ShowInHeader == show)
            return false;
        endpoint.ShowInHeader = show;
        EmitSignal(SignalName.TimecodeStateChanged);
        return true;
    }

    /// <summary>
    /// Changes the kind of an endpoint. Output kinds that cannot send are refused.
    /// </summary>
    /// <param name="id">Endpoint id.</param>
    /// <param name="kind">New kind.</param>
    /// <returns><see langword="true"/> when the kind changed.</returns>
    public bool SetKind(int id, TimecodeKind kind)
    {
        var endpoint = FindEndpoint(id);
        if (endpoint == null) return false;
        if (endpoint.Direction == TimecodeDirection.Output && !kind.AllowsOutput())
            return false;
        if (endpoint.Direction == TimecodeDirection.Input && !kind.AllowsInput())
            return false;
        if (endpoint.Kind == kind)
            return false;

        endpoint.Kind = kind;
        endpoint.Running = false;
        endpoint.ElapsedSeconds = 0;
        endpoint.PlayUntilElapsed = -1;
        endpoint.SourceName = ResolveDefaultSource(kind, endpoint.Direction);
        endpoint.SourceChannel = 0;
        endpoint.CurrentValue = TimecodeValue.Zero(endpoint.FrameRate);
        SyncProcess();
        ReconcileIo();
        EmitSignal(SignalName.TimecodeStateChanged);
        return true;
    }

    /// <summary>
    /// Changes the frame rate of an endpoint.
    /// </summary>
    /// <param name="id">Endpoint id.</param>
    /// <param name="frameRate">New rate.</param>
    /// <returns><see langword="true"/> when the rate changed.</returns>
    public bool SetFrameRate(int id, TimecodeFrameRate frameRate)
    {
        var endpoint = FindEndpoint(id);
        if (endpoint == null || endpoint.FrameRate == frameRate)
            return false;
        endpoint.FrameRate = frameRate;
        RefreshComputedValue(endpoint);
        return true;
    }

    /// <summary>
    /// Sets the source/device/address string for an endpoint.
    /// </summary>
    /// <param name="id">Endpoint id.</param>
    /// <param name="sourceName">Device name, IP, or OSC path.</param>
    /// <returns><see langword="true"/> when the source changed.</returns>
    public bool SetSourceName(int id, string sourceName)
    {
        var endpoint = FindEndpoint(id);
        if (endpoint == null) return false;
        string next = sourceName ?? string.Empty;
        if (string.Equals(endpoint.SourceName, next, StringComparison.Ordinal))
            return false;
        endpoint.SourceName = next;
        ReconcileIo();
        return true;
    }

    /// <summary>
    /// Sets the 0-based audio channel for an LTC endpoint.
    /// </summary>
    /// <param name="id">Endpoint id.</param>
    /// <param name="channel">0-based channel index.</param>
    /// <returns><see langword="true"/> when the channel changed.</returns>
    public bool SetSourceChannel(int id, int channel)
    {
        var endpoint = FindEndpoint(id);
        if (endpoint == null) return false;
        int next = Math.Max(0, channel);
        if (endpoint.SourceChannel == next)
            return false;
        endpoint.SourceChannel = next;
        ReconcileIo();
        return true;
    }

    /// <summary>
    /// Hardware channel count for the endpoint's current audio device, or 0.
    /// </summary>
    /// <param name="endpoint">Endpoint to query.</param>
    /// <returns>Channel count.</returns>
    public int GetSourceChannelCount(TimecodeEndpoint endpoint)
    {
        if (endpoint == null || !endpoint.Kind.UsesAudioDeviceChannel())
            return 0;
        if (string.IsNullOrEmpty(endpoint.SourceName))
            return 0;
        if (_audioDevices == null)
            _audioDevices = GetNodeOrNull<AudioDevices>("/root/AudioDevices");
        if (_audioDevices == null)
            return 0;

        if (endpoint.Direction == TimecodeDirection.Output)
            return _audioDevices.GetPlaybackChannelCount(endpoint.SourceName);

        var listings = _audioDevices.GetRecordingDeviceListings();
        foreach (var listing in listings)
        {
            if (string.Equals(listing.Name, endpoint.SourceName, StringComparison.OrdinalIgnoreCase))
                return listing.ChannelCount;
        }

        return 0;
    }

    /// <summary>
    /// Starts a controllable output generator.
    /// </summary>
    /// <param name="id">Output endpoint id.</param>
    /// <param name="durationSeconds">When &gt; 0, pause after this many seconds of generation.</param>
    public void Start(int id, double durationSeconds = 0)
    {
        var endpoint = FindGenerator(id);
        if (endpoint == null) return;
        if (!_timecodeEnabled)
        {
            _globalSignals?.EmitSignal(nameof(GlobalSignals.Log),
                "Timecode: enable Timecode to run generators.", (int)LogType.Warning);
            return;
        }

        endpoint.Running = true;
        endpoint.PlayUntilElapsed = durationSeconds > 1e-9
            ? endpoint.ElapsedSeconds + durationSeconds
            : -1;
        SyncProcess();
        ReconcileIo();
        RefreshComputedValue(endpoint);
    }

    /// <summary>
    /// Pauses a controllable output generator without clearing elapsed time.
    /// </summary>
    /// <param name="id">Output endpoint id.</param>
    public void Pause(int id)
    {
        var endpoint = FindGenerator(id);
        if (endpoint == null) return;
        endpoint.Running = false;
        endpoint.PlayUntilElapsed = -1;
        RefreshComputedValue(endpoint);
        SyncProcess();
    }

    /// <summary>
    /// Clears a Play Duration auto-pause window without stopping the generator.
    /// Used when the operator stops the cue so session timecode keeps running.
    /// </summary>
    /// <param name="id">Output endpoint id.</param>
    public void ClearPlayUntil(int id)
    {
        var endpoint = FindGenerator(id);
        if (endpoint == null) return;
        if (endpoint.PlayUntilElapsed < 0)
            return;
        endpoint.PlayUntilElapsed = -1;
        RefreshComputedValue(endpoint);
    }

    /// <summary>
    /// Resets a controllable output generator to 00:00:00:00. Running state is unchanged.
    /// </summary>
    /// <param name="id">Output endpoint id.</param>
    public void Reset(int id)
    {
        var endpoint = FindGenerator(id);
        if (endpoint == null) return;
        endpoint.ElapsedSeconds = 0;
        if (endpoint.PlayUntilElapsed >= 0)
            endpoint.PlayUntilElapsed = endpoint.Running ? endpoint.PlayUntilElapsed : -1;
        RefreshComputedValue(endpoint);
    }

    /// <summary>
    /// Jumps a controllable output generator to an absolute timecode.
    /// </summary>
    /// <param name="id">Output endpoint id.</param>
    /// <param name="hours">Hours 0–23.</param>
    /// <param name="minutes">Minutes 0–59.</param>
    /// <param name="seconds">Seconds 0–59.</param>
    /// <param name="frames">Frames.</param>
    public void SetTime(int id, int hours, int minutes, int seconds, int frames)
    {
        var endpoint = FindGenerator(id);
        if (endpoint == null) return;
        var value = new TimecodeValue(hours, minutes, seconds, frames, endpoint.FrameRate, endpoint.Running);
        endpoint.ElapsedSeconds = value.ToElapsedSeconds();
        RefreshComputedValue(endpoint);
    }

    /// <summary>
    /// Jumps a controllable output generator to an elapsed-seconds position.
    /// Does not change running state or a scheduled play-until window.
    /// </summary>
    /// <param name="id">Output endpoint id.</param>
    /// <param name="elapsedSeconds">Non-negative elapsed seconds.</param>
    public void SetElapsedSeconds(int id, double elapsedSeconds)
    {
        var endpoint = FindGenerator(id);
        if (endpoint == null) return;
        endpoint.ElapsedSeconds = Math.Max(0, elapsedSeconds);
        RefreshComputedValue(endpoint);
    }

    /// <summary>
    /// Offsets a controllable output generator by real seconds (wrapped to 24 hours).
    /// </summary>
    /// <param name="id">Output endpoint id.</param>
    /// <param name="seconds">Signed offset.</param>
    public void OffsetSeconds(int id, double seconds)
    {
        var endpoint = FindGenerator(id);
        if (endpoint == null) return;
        var current = TimecodeValue.FromElapsedSeconds(endpoint.ElapsedSeconds, endpoint.FrameRate, endpoint.Running);
        var next = current.OffsetBySeconds(seconds);
        endpoint.ElapsedSeconds = next.ToElapsedSeconds();
        RefreshComputedValue(endpoint);
    }

    private TimecodeEndpoint FindGenerator(int id)
    {
        var endpoint = FindEndpoint(id);
        if (endpoint == null || endpoint.Direction != TimecodeDirection.Output)
            return null;
        if (!endpoint.Kind.IsControllableGenerator())
        {
            _globalSignals?.EmitSignal(nameof(GlobalSignals.Log),
                "Timecode: System Clock follows wall time and cannot be controlled.", (int)LogType.Warning);
            return null;
        }

        return endpoint;
    }

    /// <summary>
    /// Device or address names that can be assigned as the source for <paramref name="endpoint"/>.
    /// </summary>
    /// <param name="endpoint">Endpoint to list sources for.</param>
    /// <returns>Suggested source names (may be empty).</returns>
    public IReadOnlyList<string> ListSourceOptions(TimecodeEndpoint endpoint)
    {
        if (endpoint == null)
            return Array.Empty<string>();

        return endpoint.Kind switch
        {
            TimecodeKind.MidiTimecode when endpoint.Direction == TimecodeDirection.Input =>
                ListMidiNames(inputs: true),
            TimecodeKind.MidiTimecode =>
                ListMidiNames(inputs: false),
            TimecodeKind.LinearTimecode when endpoint.Direction == TimecodeDirection.Input =>
                ListRecordingDeviceNames(),
            TimecodeKind.LinearTimecode =>
                ListPlaybackDeviceNames(),
            TimecodeKind.Osc when endpoint.Direction == TimecodeDirection.Output =>
                ListOscConnectionNames(),
            _ => Array.Empty<string>()
        };
    }

    /// <summary>
    /// Serializes session timecode configuration for the showfile / history.
    /// </summary>
    /// <returns>Dictionary with TimecodeEnabled, Inputs, and Outputs.</returns>
    public Godot.Collections.Dictionary GetData()
    {
        var dict = new Godot.Collections.Dictionary();
        dict["TimecodeEnabled"] = _timecodeEnabled;
        var inputs = new Godot.Collections.Array();
        foreach (var endpoint in _inputs)
            inputs.Add(endpoint.GetData());
        dict["Inputs"] = inputs;
        var outputs = new Godot.Collections.Array();
        foreach (var endpoint in _outputs)
            outputs.Add(endpoint.GetData());
        dict["Outputs"] = outputs;
        return dict;
    }

    /// <summary>
    /// Restores session timecode configuration from show load or undo/redo.
    /// </summary>
    /// <param name="data">Dictionary previously produced by <see cref="GetData"/>.</param>
    public void LoadFromData(Godot.Collections.Dictionary data)
    {
        if (data == null) return;

        _inputs.Clear();
        _outputs.Clear();
        _nextId = 1;

        if (data.TryGetValue("Inputs", out var inputsVar)
            && inputsVar.AsGodotArray() is Godot.Collections.Array inputArr)
        {
            foreach (var item in inputArr)
            {
                if (item.VariantType != Variant.Type.Dictionary)
                    continue;
                var endpoint = new TimecodeEndpoint { Direction = TimecodeDirection.Input };
                endpoint.LoadFromData(item.AsGodotDictionary());
                if (endpoint.Id <= 0)
                    endpoint.Id = _nextId++;
                if (endpoint.Id >= _nextId)
                    _nextId = endpoint.Id + 1;
                _inputs.Add(endpoint);
            }
        }
        PublishInputsForMidiThread();

        if (data.TryGetValue("Outputs", out var outputsVar)
            && outputsVar.AsGodotArray() is Godot.Collections.Array outputArr)
        {
            foreach (var item in outputArr)
            {
                if (item.VariantType != Variant.Type.Dictionary)
                    continue;
                var endpoint = new TimecodeEndpoint { Direction = TimecodeDirection.Output };
                endpoint.LoadFromData(item.AsGodotDictionary());
                if (endpoint.Id <= 0)
                    endpoint.Id = _nextId++;
                if (endpoint.Id >= _nextId)
                    _nextId = endpoint.Id + 1;
                if (!endpoint.Kind.AllowsOutput())
                    endpoint.Kind = TimecodeKind.MidiTimecode;
                _outputs.Add(endpoint);
            }
        }

        _timecodeEnabled = data.TryGetValue("TimecodeEnabled", out var en) && en.AsBool();
        MigrateGeneratorInputsToOutputs();
        SyncProcess();
        ReconcileIo();
        EmitSignal(SignalName.TimecodeStateChanged);
        GD.Print($"TimecodeManager:LoadFromData - Enabled={_timecodeEnabled}, " +
                 $"inputs={_inputs.Count}, outputs={_outputs.Count}");
    }

    /// <summary>
    /// Early builds stored Internal / System Clock as inputs. Move them to outputs.
    /// </summary>
    private void MigrateGeneratorInputsToOutputs()
    {
        for (int i = _inputs.Count - 1; i >= 0; i--)
        {
            var endpoint = _inputs[i];
            if (endpoint.Kind.AllowsInput())
                continue;
            endpoint.Direction = TimecodeDirection.Output;
            _inputs.RemoveAt(i);
            _outputs.Insert(0, endpoint);
            GD.Print($"TimecodeManager:LoadFromData - Moved '{endpoint.Name}' to outputs.");
        }
        PublishInputsForMidiThread();
    }

    /// <summary>
    /// Clears session timecode configuration for a new empty show.
    /// </summary>
    public void ResetToDefaults()
    {
        StopLtcFillThread();
        DestroyAllLtcRuntimes();
        _inputs.Clear();
        PublishInputsForMidiThread();
        _outputs.Clear();
        _nextId = 1;
        _timecodeEnabled = false;
        SetProcess(false);
        EmitSignal(SignalName.TimecodeStateChanged);
        GD.Print("TimecodeManager:ResetToDefaults - Timecode disabled, endpoints cleared.");
    }

    private TimecodeEndpoint CreateEndpoint(TimecodeKind kind, TimecodeDirection direction)
    {
        var endpoint = new TimecodeEndpoint
        {
            Id = _nextId++,
            Name = NextDefaultName(kind),
            Direction = direction,
            Kind = kind,
            FrameRate = TimecodeFrameRate.Fps30,
            SourceName = ResolveDefaultSource(kind, direction),
            CurrentValue = TimecodeValue.Zero(TimecodeFrameRate.Fps30)
        };
        return endpoint;
    }

    private string NextDefaultName(TimecodeKind kind)
    {
        string baseName = kind.GetDisplayName();
        int n = 1;
        string candidate = $"{baseName} {n}";
        while (NameExists(candidate))
        {
            n++;
            candidate = $"{baseName} {n}";
        }

        return candidate;
    }

    private bool NameExists(string name)
    {
        foreach (var endpoint in AllEndpoints())
        {
            if (string.Equals(endpoint.Name, name, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private string ResolveDefaultSource(TimecodeKind kind, TimecodeDirection direction)
    {
        string preset = kind.DefaultSource(direction);
        if (!string.IsNullOrEmpty(preset))
            return preset;

        var options = kind switch
        {
            TimecodeKind.MidiTimecode when direction == TimecodeDirection.Input =>
                ListMidiNames(inputs: true),
            TimecodeKind.MidiTimecode =>
                ListMidiNames(inputs: false),
            TimecodeKind.LinearTimecode when direction == TimecodeDirection.Input =>
                ListRecordingDeviceNames(),
            TimecodeKind.LinearTimecode =>
                ListPlaybackDeviceNames(),
            TimecodeKind.Osc when direction == TimecodeDirection.Output =>
                ListOscConnectionNames(),
            _ => Array.Empty<string>()
        };

        return options.Count > 0 ? options[0] : string.Empty;
    }

    private IEnumerable<TimecodeEndpoint> AllEndpoints()
    {
        foreach (var endpoint in _inputs)
            yield return endpoint;
        foreach (var endpoint in _outputs)
            yield return endpoint;
    }

    private void TickEndpoint(TimecodeEndpoint endpoint, double delta)
    {
        if (endpoint.Direction == TimecodeDirection.Output
            && endpoint.Kind.IsControllableGenerator()
            && endpoint.Kind != TimecodeKind.LinearTimecode
            && endpoint.Running
            && _timecodeEnabled)
        {
            endpoint.ElapsedSeconds += delta;
            if (endpoint.PlayUntilElapsed >= 0 && endpoint.ElapsedSeconds >= endpoint.PlayUntilElapsed)
            {
                endpoint.ElapsedSeconds = endpoint.PlayUntilElapsed;
                endpoint.Running = false;
                endpoint.PlayUntilElapsed = -1;
            }
        }

        RefreshComputedValue(endpoint);
    }

    private void RefreshComputedValue(TimecodeEndpoint endpoint)
    {
        if (endpoint.Direction == TimecodeDirection.Output
            && endpoint.Kind == TimecodeKind.SystemClock)
        {
            endpoint.CurrentValue = _timecodeEnabled
                ? TimecodeValue.FromLocalTimeOfDay(endpoint.FrameRate)
                : TimecodeValue.Zero(endpoint.FrameRate);
            return;
        }

        if (endpoint.Direction == TimecodeDirection.Output
            && endpoint.Kind.IsControllableGenerator())
        {
            endpoint.CurrentValue = TimecodeValue.FromElapsedSeconds(
                endpoint.ElapsedSeconds,
                endpoint.FrameRate,
                isRunning: endpoint.Running && _timecodeEnabled);
            return;
        }

        if (endpoint.Direction == TimecodeDirection.Input)
            return;

        endpoint.CurrentValue = new TimecodeValue(
            endpoint.CurrentValue.Hours,
            endpoint.CurrentValue.Minutes,
            endpoint.CurrentValue.Seconds,
            endpoint.CurrentValue.Frames,
            endpoint.FrameRate,
            isRunning: false,
            isLocked: false);
    }

    private void ApplyLockedInput(TimecodeEndpoint endpoint, TimecodeValue decoded)
    {
        if (endpoint == null)
            return;
        lock (_clockLock)
        {
            endpoint.CurrentValue = new TimecodeValue(
                decoded.Hours, decoded.Minutes, decoded.Seconds, decoded.Frames,
                decoded.FrameRate, isRunning: true, isLocked: true);
            endpoint.LastLockMsec = (ulong)System.Environment.TickCount64;
            endpoint.ElapsedSeconds = decoded.ToElapsedSeconds();
        }
    }

    private void ExpireStaleInputLocks()
    {
        ulong now = (ulong)System.Environment.TickCount64;
        lock (_clockLock)
        {
            foreach (var endpoint in _inputs)
            {
                if (!endpoint.CurrentValue.IsLocked)
                    continue;
                if (now - endpoint.LastLockMsec < 250)
                    continue;
                var v = endpoint.CurrentValue;
                endpoint.CurrentValue = new TimecodeValue(
                    v.Hours, v.Minutes, v.Seconds, v.Frames, v.FrameRate,
                    isRunning: false, isLocked: false);
            }
        }
    }

    private void ReconcileIo()
    {
        ReconcileLtcOutputs();
        EnsureMtcPorts();
        SyncProcess();
    }

    private void SyncProcess()
    {
        // Keep ticking whenever Timecode is enabled so cue triggers see paused
        // Internal clocks and SetTime jumps, not only running generators.
        SetProcess(_timecodeEnabled && (_inputs.Count > 0 || _outputs.Count > 0));
    }

    /// <summary>
    /// GO's armed cues once each time a session clock reaches (or jumps across) their trigger frame.
    /// Rewinding and passing the same time again fires again, including while the first instance is still live.
    /// Sitting on the frame does not retrigger. The decision itself is <see cref="TimecodeTriggerRule.ShouldFire"/>.
    /// </summary>
    private void FireCueTimecodeTriggers()
    {
        if (CueList.CueIndex == null || CueList.CueIndex.Count == 0)
        {
            SnapshotTriggerReadings();
            return;
        }

        var executor = _globalData?.CueCommandExecutor;
        if (executor == null)
        {
            SnapshotTriggerReadings();
            return;
        }

        var toFire = new List<Cue>();
        foreach (Cue cue in CueList.CueIndex.Values)
        {
            if (cue == null || !cue.CanFireTimecodeTrigger)
                continue;

            var endpoint = FindEndpoint(cue.TimecodeTriggerEndpointId);
            if (endpoint == null)
                continue;
            if (endpoint.Direction == TimecodeDirection.Input && !endpoint.CurrentValue.IsLocked)
                continue;

            double targetElapsed = cue.GetTimecodeTriggerElapsedSeconds(endpoint.FrameRate);
            if (targetElapsed < 0)
                continue;
            if (!_triggerPrevValue.TryGetValue(endpoint.Id, out var previous))
                continue;

            bool hit = TimecodeTriggerRule.ShouldFire(
                previous,
                endpoint.CurrentValue,
                endpoint.Direction == TimecodeDirection.Input,
                cue.TimecodeTriggerHours,
                cue.TimecodeTriggerMinutes,
                cue.TimecodeTriggerSeconds,
                cue.TimecodeTriggerFrames,
                targetElapsed);
            if (!hit)
                continue;

            toFire.Add(cue);
        }

        SnapshotTriggerReadings();

        for (int i = 0; i < toFire.Count; i++)
        {
            var cue = toFire[i];
            string display = cue.GetTimecodeTriggerDisplay();
            var endpoint = FindEndpoint(cue.TimecodeTriggerEndpointId);
            string clockName = endpoint?.Name ?? "?";
            GD.Print($"TimecodeManager:FireCueTimecodeTriggers - GO \"{cue.Name}\" (id={cue.Id}) at {display} ← {clockName}");
            _globalSignals?.EmitSignal(nameof(GlobalSignals.Log),
                $"Timecode trigger: \"{cue.Name}\" at {display} ({clockName})", (int)LogType.Info);
            CallDeferred(nameof(DeferredActivateCue), cue.Id);
        }
    }

    private void DeferredActivateCue(int cueId)
    {
        var executor = _globalData?.CueCommandExecutor;
        if (executor == null)
            return;
        var cue = CueList.FetchCueFromId(cueId);
        if (cue == null || !cue.CanFireTimecodeTrigger)
            return;
        executor.ActivateSequenceFrom(cue);
    }

    private void SnapshotTriggerReadings()
    {
        var seen = new HashSet<int>();
        foreach (var endpoint in AllEndpoints())
        {
            seen.Add(endpoint.Id);
            _triggerPrevValue[endpoint.Id] = endpoint.CurrentValue;
        }

        if (_triggerPrevValue.Count == seen.Count)
            return;

        var stale = new List<int>();
        foreach (int id in _triggerPrevValue.Keys)
        {
            if (!seen.Contains(id))
                stale.Add(id);
        }

        for (int i = 0; i < stale.Count; i++)
            _triggerPrevValue.Remove(stale[i]);
    }

    private IReadOnlyList<string> ListMidiNames(bool inputs)
    {
        if (_midiManager == null)
            _midiManager = GetNodeOrNull<MidiManager>("/root/MidiManager");
        if (_midiManager == null)
            return Array.Empty<string>();

        var names = new List<string>();
        var session = inputs ? _midiManager.SessionInputNames : _midiManager.SessionOutputNames;
        var available = inputs ? _midiManager.AvailableInputNames : _midiManager.AvailableOutputNames;
        foreach (string name in session)
        {
            if (!string.IsNullOrEmpty(name) && !names.Contains(name, StringComparer.OrdinalIgnoreCase))
                names.Add(name);
        }

        foreach (string name in available)
        {
            if (!string.IsNullOrEmpty(name) && !names.Contains(name, StringComparer.OrdinalIgnoreCase))
                names.Add(name);
        }

        return names;
    }

    private IReadOnlyList<string> ListRecordingDeviceNames()
    {
        if (_audioDevices == null)
            _audioDevices = GetNodeOrNull<AudioDevices>("/root/AudioDevices");
        if (_audioDevices == null)
            return Array.Empty<string>();

        var listings = _audioDevices.GetRecordingDeviceListings();
        var names = new List<string>(listings.Count);
        foreach (var listing in listings)
        {
            if (!string.IsNullOrEmpty(listing.Name))
                names.Add(listing.Name);
        }

        return names;
    }

    private IReadOnlyList<string> ListPlaybackDeviceNames()
    {
        if (_audioDevices == null)
            _audioDevices = GetNodeOrNull<AudioDevices>("/root/AudioDevices");
        var names = _audioDevices?.GetAvailableAudioDeviceNames();
        return names ?? (IReadOnlyList<string>)Array.Empty<string>();
    }

    private static IReadOnlyList<string> ListOscConnectionNames()
    {
        var names = new List<string>();
        var list = Cue2.Domain.Connections.OscConnections.Connections;
        if (list == null)
            return names;
        foreach (var conn in list)
        {
            if (conn == null || !GodotObject.IsInstanceValid(conn))
                continue;
            string name = string.IsNullOrWhiteSpace(conn.Name) ? $"OSC {conn.Id}" : conn.Name;
            names.Add(name);
        }

        return names;
    }
}
