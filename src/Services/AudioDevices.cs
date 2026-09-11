// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Cue2.Domain.Cuelist;
using Cue2.Domain.Playback;
using Cue2.Domain.Devices;
using Cue2.Domain.ShowSettings;
using Cue2.Domain.Metadata;
using Cue2.Domain.Cues;
using Cue2.Domain.Connections;
using Cue2.Domain.Library;
using Cue2.Domain.Commands;
using Cue2.Media.Audio;
using Godot;
using SDL3;

namespace Cue2.Services;


/// <summary>
/// AudioDevices looks after all SDL related tasks.
/// </summary>
public partial class AudioDevices : Node
{
	private GlobalData _globalData;
	private GlobalSignals _globalSignals;
	private MediaEngine _mediaEngine;
	
	private readonly Dictionary<int, AudioDevice> _openDevices = new Dictionary<int, AudioDevice>();
	private readonly Dictionary<uint, int> _physicalIdToDeviceId = new Dictionary<uint, int>();
	
	private readonly Dictionary<uint, List<IAudioPlayback>> _activeAudioPlaybacks = new Dictionary<uint, List<IAudioPlayback>>();

	/// <summary>
	/// Session master gain linear 0–1 (show-scoped value mirrored here for fill-thread reads).
	/// </summary>
	private float _sessionMasterLinear = 1f;

	/// <summary>
	/// Runtime master mute (not saved with the showfile; cleared on New Session).
	/// </summary>
	private bool _sessionMasterMuted;

	/// <summary>Peak clamp magnitude (linear) after mix; fill threads read this under the master lock.</summary>
	private float _outputMaxAbs = 1f;

	/// <summary>Silence-floor magnitude (linear); samples below this are zeroed after mix.</summary>
	private float _outputMinAbs;

	/// <summary>Lock for session master fields read from audio fill threads.</summary>
	private readonly object _sessionMasterLock = new object();
	
	private Timer _pollTimer;
	
    public override void _Ready()
    {
	    if (SingleInstanceGuard.IsSecondary)
		    return;

	    _globalData = GetNode<GlobalData>("/root/GlobalData");
	    _globalSignals = GetNode<GlobalSignals>("/root/GlobalSignals");
	    _mediaEngine = GetNode<MediaEngine>("/root/MediaEngine");

	    // Seed fill-thread limit cache from show settings (or defaults if not loaded yet).
	    if (_globalData?.Settings != null)
		    SetOutputLimits(_globalData.Settings.AudioOutputMaxDb, _globalData.Settings.AudioOutputMinDb);
	    else
		    SetOutputLimits(Settings.DefaultAudioOutputMaxDb, Settings.DefaultAudioOutputMinDb);

	    _pollTimer = new Timer();
	    _pollTimer.WaitTime = 0.5;
	    _pollTimer.Autostart = true;
	    _pollTimer.Timeout += PollSdlEvents;
	    AddChild(_pollTimer);
    }

    private void PollSdlEvents()
    {
	    bool changesDetected = false; 
	    while (SDL.PollEvent(out var ev))
	    {
		    if (ev.Type == (uint)SDL.EventType.AudioDeviceRemoved)
		    {
			    var removedPhysicalId = ev.ADevice.Which;
			    CheckMissingDevices(removedPhysicalId);
			    changesDetected = true;
		    }
		    else if (ev.Type == (uint)SDL.EventType.AudioDeviceAdded)
		    {
			    CheckAddedDevice(ev.ADevice.Which);
			    changesDetected = true;
		    }
	    }

	    if (changesDetected)
	    {
		    _globalSignals.EmitSignal(nameof(GlobalSignals.AudioDevicesChanged));
	    }
    }

    /// <summary>
    /// Checks if any devices in _openDevices are missing from the list of available audio devices.
    /// Logs a warning for each missing device and returns their names.
    /// </summary>
    private void CheckMissingDevices(uint removedPhysicalId)
    {
	    GD.Print("AudioDevices:CheckMissingDevices - Checking for missing audio devices");
	    if (!_physicalIdToDeviceId.TryGetValue(removedPhysicalId, out int deviceId))
		    return;

	    if (!_openDevices.TryGetValue(deviceId, out var device))
	    {
		    _physicalIdToDeviceId.Remove(removedPhysicalId);
		    return;
	    }

	    _globalSignals.EmitSignal(nameof(GlobalSignals.Log),
		    $"Audio device disconnected/lost: {device.Name}", 3);
	    CloseAudioDevice(deviceId);
	}
    
    /// <summary>
    /// Checks if an audio devices physical ID matches a device used in an audio output patch.
    /// Function will ensure device is opened if it is not already.
    /// </summary>
    /// <param name="addedPhysicalId">The ID of the audio device to check.</param>
    private void CheckAddedDevice(uint addedPhysicalId)
	{
		var name = SDL.GetAudioDeviceName(addedPhysicalId);
		var patches = _globalData.Settings.GetAudioOutputPatches();
		foreach (var patch in patches)
		{
			if (name != null && patch.Value.OutputDevices.ContainsKey(name))
			{
				bool alreadyOpen = false;
				foreach (var dev in _openDevices.Values)
				{
					if (dev.Name == name)
					{
						alreadyOpen = true;
						break;
					}
				}

				OpenAudioDevice(name, out var _);
				if (!alreadyOpen)
				{
					_globalSignals.EmitSignal(nameof(GlobalSignals.Log),
						$"Needed audio device reconnected: {name}", 0);
				}

				return;
			}
		}
	}
    
	/// <summary>
	/// Closes an open audio device by its ID: detaches active playback streams, removes tracking,
	/// then closes the SDL device.
	/// </summary>
	/// <param name="deviceId">The ID of the audio device to close (key in _openDevices).</param>
	/// <param name="emitChanged">
	/// When true (default), emits <see cref="GlobalSignals.AudioDevicesChanged"/> after a successful close.
	/// Batch callers (e.g. <see cref="SyncOpenDevices"/>) pass false and emit once.
	/// </param>
	/// <returns>True if successfully closed and removed; false on error.</returns>
    private bool CloseAudioDevice(int deviceId, bool emitChanged = true)
    {
	    if (!_openDevices.TryGetValue(deviceId, out var device))
		    return false;

	    uint logicalId = device.LogicalId;
	    string deviceName = device.Name ?? deviceId.ToString();

	    // Snapshot playbacks bound to this logical device and drop tracking before SDL close
	    // so fill threads are not treated as active on a dead handle.
	    List<IAudioPlayback> affected;
	    lock (_activeAudioPlaybacks)
	    {
		    if (_activeAudioPlaybacks.TryGetValue(logicalId, out var list))
		    {
			    affected = list.ToList();
			    _activeAudioPlaybacks.Remove(logicalId);
		    }
		    else
		    {
			    affected = new List<IAudioPlayback>();
		    }
	    }

	    foreach (var playback in affected)
	    {
		    if (playback == null)
			    continue;
		    try
		    {
			    playback.OnOutputDeviceLost(logicalId);
		    }
		    catch (Exception ex)
		    {
			    GD.PrintErr(
				    $"AudioDevices:CloseAudioDevice - OnOutputDeviceLost for '{deviceName}': {ex.Message}");
		    }
	    }

	    try
	    {
		    if (logicalId != 0)
			    SDL.CloseAudioDevice(logicalId);
		    _openDevices.Remove(deviceId);
		    _physicalIdToDeviceId.Remove(device.PhysicalId);

		    GD.Print(
			    $"AudioDevices:CloseAudioDevice - Closed device ID {deviceId} ('{deviceName}'); " +
			    $"detached {affected.Count} playback(s).");
		    if (emitChanged)
			    _globalSignals?.EmitSignal(nameof(GlobalSignals.AudioDevicesChanged));
		    return true;
	    }
	    catch (Exception ex)
	    {
		    // Still drop bookkeeping so we do not keep a zombie open-device entry.
		    _openDevices.Remove(deviceId);
		    _physicalIdToDeviceId.Remove(device.PhysicalId);
		    _globalSignals.EmitSignal(nameof(GlobalSignals.Log),
			    $"Error closing device '{deviceName}' (ID: {deviceId}): {ex.Message}", 2);
		    GD.PrintErr("AudioDevices:CloseAudioDevice - Error closing device ID " + deviceId + ": " + ex.Message);
		    if (emitChanged)
			    _globalSignals?.EmitSignal(nameof(GlobalSignals.AudioDevicesChanged));
		    return false;
	    }
    }

	/// <summary>
	/// Closes an open playback device by display name (no-op if not open).
	/// </summary>
	/// <param name="name">Exact device name as returned by SDL enumeration / <see cref="GetOpenAudioDevicesNames"/>.</param>
	/// <returns>True if a device was found and closed; false if not open or close failed.</returns>
	public bool CloseAudioDeviceByName(string name)
	{
		if (string.IsNullOrEmpty(name))
			return false;

		foreach (var kv in _openDevices)
		{
			if (kv.Value != null && string.Equals(kv.Value.Name, name, StringComparison.Ordinal))
				return CloseAudioDevice(kv.Key);
		}

		return false;
	}

	/// <summary>
	/// Reconciles the open SDL playback set with a required name list.
	/// </summary>
	/// <param name="requiredNames">
	/// Device names that must be open after this call (showfile <c>AudioDevices</c> list and/or
	/// names referenced by audio output patches). Null or empty means close all open devices.
	/// </param>
	/// <param name="closeOthers">
	/// When true (default), closes any currently open device whose name is not in
	/// <paramref name="requiredNames"/>. When false, only opens missing required devices
	/// (keep-alive — not used for show load/reset).
	/// </param>
	/// <remarks>
	/// <para>
	/// <b>Contract (show load / New Session / settings history for AudioDevices+AudioPatch):</b>
	/// after opening listed devices and applying the patch table, call this with the union of
	/// the showfile open-device list and every device key in every patch. Devices left over from
	/// a previous show that are not required are closed (SDL handles released, playbacks detached
	/// via <see cref="IAudioPlayback.OnOutputDeviceLost"/>).
	/// </para>
	/// <para>
	/// Interactive matrix open and GO-time direct-output open still use
	/// <see cref="OpenAudioDevice"/> alone and do not force-close siblings.
	/// </para>
	/// </remarks>
	public void SyncOpenDevices(IEnumerable<string> requiredNames, bool closeOthers = true)
	{
		var required = new HashSet<string>(StringComparer.Ordinal);
		if (requiredNames != null)
		{
			foreach (var name in requiredNames)
			{
				if (!string.IsNullOrEmpty(name))
					required.Add(name);
			}
		}

		// Open missing required devices first so a failed close cannot leave the set empty
		// when the show still needs output.
		foreach (var name in required)
		{
			if (GetAudioDeviceIdFromName(name) != null)
				continue;
			OpenAudioDevice(name, out var error);
			if (!string.IsNullOrEmpty(error))
			{
				GD.PrintErr($"AudioDevices:SyncOpenDevices - open '{name}': {error}");
			}
		}

		if (!closeOthers)
			return;

		bool anyClosed = false;
		foreach (var device in _openDevices.Values.ToList())
		{
			if (device == null || string.IsNullOrEmpty(device.Name))
				continue;
			if (required.Contains(device.Name))
				continue;

			GD.Print($"AudioDevices:SyncOpenDevices - Closing leftover device '{device.Name}'");
			if (CloseAudioDevice(device.DeviceId, emitChanged: false))
				anyClosed = true;
		}

		if (anyClosed && !SuppressChangedSignals)
			_globalSignals?.EmitSignal(nameof(GlobalSignals.AudioDevicesChanged));
	}

	/// <summary>
	/// When true, open/close and <see cref="SyncOpenDevices"/> do not emit
	/// <see cref="GlobalSignals.AudioDevicesChanged"/>. The caller emits once after a batch.
	/// </summary>
	public bool SuppressChangedSignals { get; set; }

	/// <summary>
	/// Emits <see cref="GlobalSignals.AudioDevicesChanged"/> (used after a suppressed batch).
	/// </summary>
	public void NotifyDevicesChanged()
	{
		_globalSignals?.EmitSignal(nameof(GlobalSignals.AudioDevicesChanged));
	}

    /// <summary>
    /// Opens an audio device by name if not already open, registers it, and retrieves its specs.
    /// </summary>
    /// <param name="name">The name of the audio device to open.</param>
    /// <param name="error">Output parameter for any error message; empty string on success.</param>
    /// <returns>The opened AudioDevice instance, or null on failure.</returns>
    /// <remarks>
    /// If the device is already open, returns the existing instance.
    /// </remarks>
    public AudioDevice OpenAudioDevice(string name, out string error)
    {
	    // Check if audio device already opened
	    GD.Print($"OpenAudioDevice called for: {name}");
	    foreach (var dev in _openDevices.Values)
	    {
		    if (dev.Name == name)
		    {
			    GD.Print("    ^^ Device already opened");
			    error = "";
			    return dev;
		    }
	    }

	    var physicalDeviceId = GetAudioDevicePhysicalIdFromName(name);
	    // Open with device-native settings (null/0 spec).
	    uint logicalId = SDL.OpenAudioDevice(physicalDeviceId, 0);
	    if (logicalId == 0)
	    {
		    _globalSignals.EmitSignal(nameof(GlobalSignals.Log),
			    $"AudioDevices:OpenAudioDevice - Failed to find and open audio device of name: {name} ", 3);
		    error = "Failed to open audio device: " + SDL.GetError();
		    return null;
	    }

	    // From here, any abort must CloseAudioDevice(logicalId) so SDL handles do not leak.
	    try
	    {
		    var device = new AudioDevice(name, logicalId, out string adError);
		    if (!string.IsNullOrEmpty(adError))
		    {
			    error = adError;
			    SDL.CloseAudioDevice(logicalId);
			    return null;
		    }

		    ApplyDeviceFormat(device, logicalId);
		    device.PhysicalId = physicalDeviceId;

		    _openDevices.Add(device.DeviceId, device);
		    _physicalIdToDeviceId[device.PhysicalId] = device.DeviceId;

		    if (!SuppressChangedSignals)
			    _globalSignals.EmitSignal(nameof(GlobalSignals.AudioDevicesChanged));

		    error = "";
		    return device;
	    }
	    catch (Exception ex)
	    {
		    try
		    {
			    SDL.CloseAudioDevice(logicalId);
		    }
		    catch (Exception closeEx)
		    {
			    GD.PrintErr($"AudioDevices:OpenAudioDevice - Close after failed open: {closeEx.Message}");
		    }

		    error = ex.Message;
		    GD.PrintErr($"AudioDevices:OpenAudioDevice - {name}: {ex.Message}");
		    _globalSignals.EmitSignal(nameof(GlobalSignals.Log),
			    $"Failed to register audio device '{name}': {ex.Message}", 2);
		    return null;
	    }
    }
    
    /// <summary>
    /// Retrieves the names of all available audio playback devices using SDL.
    /// </summary>
    /// <returns>A list of device names, or null if an error occurs during enumeration.</returns>
    /// <remarks>
    /// Catches exceptions and logs them internally. Does not include already opened devices' status.
    /// </remarks>
    public List<string> GetAvailableAudioDeviceNames()
    {
	    try
	    {
		    //GD.Print("AudioDevices:GetAvailableAudioDeviceNames - Enumerating playback devices");
		    // Get number of playback devices
		    var devices = SDL.GetAudioPlaybackDevices(out int _);
		    var deviceNames = new List<string>();

		    // Enumerate playback devices
		    if (devices == null) return null;
		    foreach (var deviceId in devices)
		    {
			    var deviceUintId = Convert.ToUInt32(deviceId);
			    string deviceName = SDL.GetAudioDeviceName(deviceUintId);
			    if (deviceName != null)
			    {
				    deviceNames.Add(deviceName);
			    }
			    else
			    {
				    Console.WriteLine($"  Playback Device {deviceId}: [Unknown]");
			    }
		    }
		    return deviceNames;
	    }
	    catch (Exception ex)
	    {
		    _globalSignals.EmitSignal(nameof(GlobalSignals.Log), $"Error enumerating audio devices: {ex.Message}", 2);
		    GD.PrintErr("AudioDevices:GetAvailableAudioDeviceNames - " + ex.Message);
		    return null;
	    }
	    
	    
    }

    /// <summary>
    /// Returns the Readable name of the current system default playback device, if known.
    /// </summary>
    /// <returns>
    /// The physical device name as reported by SDL, or <c>null</c> when SDL cannot resolve a default
    /// or the name is empty.
    /// </returns>
    /// <remarks>
    /// Uses <see cref="SDL.AudioDeviceDefaultPlayback"/>. The default can change at any time at the OS
    /// level; this is a snapshot for session setup (e.g. routing a new Default Patch). Prefer matching
    /// the returned name against <see cref="GetAvailableAudioDeviceNames"/> before opening.
    /// </remarks>
    public string GetSystemDefaultPlaybackDeviceName()
    {
	    try
	    {
		    string name = SDL.GetAudioDeviceName(SDL.AudioDeviceDefaultPlayback);
		    if (string.IsNullOrWhiteSpace(name))
		    {
			    GD.Print("AudioDevices:GetSystemDefaultPlaybackDeviceName - SDL returned empty default playback name.");
			    return null;
		    }

		    GD.Print($"AudioDevices:GetSystemDefaultPlaybackDeviceName - System default playback: '{name}'");
		    return name;
	    }
	    catch (Exception ex)
	    {
		    _globalSignals?.EmitSignal(nameof(GlobalSignals.Log),
			    $"Error resolving system default playback device: {ex.Message}", 1);
		    GD.PrintErr("AudioDevices:GetSystemDefaultPlaybackDeviceName - " + ex.Message);
		    return null;
	    }
    }

    public Godot.Collections.Array<string> GetOpenAudioDevicesNames()
    {
	    var deviceNames = new Godot.Collections.Array<string>();
	    foreach (var device in _openDevices.Values)
	    {
		    deviceNames.Add(device.Name);
	    }

	    return deviceNames;
    }

    /// <summary>
    /// Snapshot of currently open playback devices (for status UI). Order is dictionary order.
    /// </summary>
    /// <returns>List of open <see cref="AudioDevice"/> instances (not copies).</returns>
    public List<AudioDevice> GetOpenAudioDevices()
    {
	    return _openDevices.Values.ToList();
    }

    /// <summary>
    /// Refreshes format, buffer size, and channel counts from SDL for all open devices.
    /// </summary>
    public void RefreshOpenDeviceFormats()
    {
	    foreach (var device in _openDevices.Values)
	    {
		    if (device == null || device.LogicalId == 0)
			    continue;
		    try
		    {
			    ApplyDeviceFormat(device, device.LogicalId);
		    }
		    catch
		    {
			    // Ignore transient SDL errors while listing status.
		    }
	    }
    }

    /// <summary>
    /// Builds readable status lines for open devices (rate, sample depth, out channels, buffer).
    /// </summary>
    /// <returns>One line per open device (or a single placeholder when none are open).</returns>
    public List<string> GetOpenDeviceStatusLines()
    {
	    RefreshOpenDeviceFormats();
	    var devices = GetOpenAudioDevices();
	    var lines = new List<string>();
	    if (devices.Count == 0)
	    {
		    lines.Add("No audio devices open. Open devices via Audio Output Patch or by playing a cue.");
		    return lines;
	    }

	    foreach (var device in devices)
	    {
		    if (device == null)
			    continue;

		    // Classic audio buffer size is sample frames (often 64/128/256/512/1024 on pro drivers).
		    string bufferPart = device.BufferFrames > 0
			    ? $"buffer {device.BufferFrames}"
			    : "buffer ?";

		    int outCh = device.OutputChannels > 0
			    ? device.OutputChannels
			    : Math.Max(0, device.Channels);

		    string ratePart = device.SampleRate > 0 ? $"{device.SampleRate} Hz" : "rate ?";
		    string depthPart = FormatSampleDepth(device);
		    lines.Add($"{device.Name} — {ratePart}, {depthPart}, out {outCh}, {bufferPart}");
	    }

	    return lines;
    }

    /// <summary>
    /// Formats sample bit depth and SDL codec/format tag for status UI (e.g. "32-bit float (F32LE)").
    /// </summary>
    private static string FormatSampleDepth(AudioDevice device)
    {
	    if (device == null)
		    return "depth ?";

	    string codec = FormatCodecTag(device.Format);
	    if (device.BitDepth > 0)
	    {
		    bool isFloat = device.Format is SDL.AudioFormat.AudioF32LE or SDL.AudioFormat.AudioF32BE;
		    string kind = isFloat ? "float" : "int";
		    return string.IsNullOrEmpty(codec)
			    ? $"{device.BitDepth}-bit {kind}"
			    : $"{device.BitDepth}-bit {kind} ({codec})";
	    }

	    return string.IsNullOrEmpty(codec) ? "depth ?" : codec;
    }

    /// <summary>
    /// Short SDL format tag (e.g. F32LE) from <see cref="SDL.AudioFormat"/>.
    /// </summary>
    private static string FormatCodecTag(SDL.AudioFormat format)
    {
	    if (format == 0)
		    return string.Empty;
	    string raw = format.ToString();
	    // Enum names are typically "AudioF32LE" → "F32LE"
	    if (raw.StartsWith("Audio", StringComparison.Ordinal))
		    return raw.Substring("Audio".Length);
	    return raw;
    }

    /// <summary>
    /// Fills <see cref="AudioDevice"/> format fields from SDL for an opened logical device id.
    /// </summary>
    private void ApplyDeviceFormat(AudioDevice device, uint logicalDeviceId)
    {
	    if (device == null || logicalDeviceId == 0)
		    return;

	    SDL.GetAudioDeviceFormat(logicalDeviceId, out SDL.AudioSpec spec, out int bufferFrames);
	    device.Channels = spec.Channels;
	    device.Format = spec.Format;
	    device.SampleRate = spec.Freq;
	    device.BitDepth = GetBitDepth(spec.Format);
	    // Sample frames fed to hardware per chunk (often 64–1024 on pro drivers).
	    device.BufferFrames = Math.Max(0, bufferFrames);

	    // Playback open path: Channels is the output count.
	    int outCh = Math.Max(0, (int)spec.Channels);
	    try
	    {
		    // Channel map length can refine multi-channel interfaces when present.
		    int[] map = SDL.GetAudioDeviceChannelMap(logicalDeviceId, out int mapCount);
		    if (map != null && map.Length > 0)
			    outCh = map.Length;
		    else if (mapCount > 0)
			    outCh = mapCount;
	    }
	    catch
	    {
		    // Channel map optional.
	    }

	    device.OutputChannels = outCh;
    }

    /// <summary>
    /// Effective session master gain for mix threads (0 when muted). Thread-safe.
    /// </summary>
    public float GetEffectiveSessionMasterLinear()
    {
	    lock (_sessionMasterLock)
	    {
		    return _sessionMasterMuted ? 0f : Math.Clamp(_sessionMasterLinear, 0f, 1f);
	    }
    }

    /// <summary>
    /// Peak clamp and silence-floor magnitudes for fill/mix threads. Thread-safe.
    /// </summary>
    /// <param name="maxAbs">Absolute sample ceiling (linear). ≤0 means no clamp.</param>
    /// <param name="minAbs">Silence floor (linear). ≤0 means no gate.</param>
    public void GetOutputLimits(out float maxAbs, out float minAbs)
    {
	    lock (_sessionMasterLock)
	    {
		    maxAbs = _outputMaxAbs;
		    minAbs = _outputMinAbs;
	    }
    }

    /// <summary>
    /// Pushes show-scoped max/min dB levels into the live fill-thread cache.
    /// </summary>
    /// <param name="maxDb">Peak clamp ceiling in dBFS (0 = full scale).</param>
    /// <param name="minDb">Silence floor in dBFS (−120 ≈ gate off).</param>
    public void SetOutputLimits(float maxDb, float minDb)
    {
	    float maxAbs = AudioMixMatrix.DbToAbsLinear(maxDb);
	    float minAbs = AudioMixMatrix.DbToAbsLinear(minDb);
	    // 0 dB must remain exact full-scale clamp (1.0), not a float drift under 1.
	    if (maxDb >= -0.0001f)
		    maxAbs = 1f;

	    lock (_sessionMasterLock)
	    {
		    _outputMaxAbs = maxAbs;
		    _outputMinAbs = minAbs;
	    }
    }

    /// <summary>
    /// Current session master volume linear 0–1 (ignores mute). Thread-safe.
    /// </summary>
    public float SessionMasterLinear
    {
	    get
	    {
		    lock (_sessionMasterLock) return _sessionMasterLinear;
	    }
    }

    /// <summary>
    /// Runtime master mute (not showfile-persisted). Thread-safe.
    /// </summary>
    public bool SessionMasterMuted
    {
	    get
	    {
		    lock (_sessionMasterLock) return _sessionMasterMuted;
	    }
    }

    /// <summary>
    /// Applies show-scoped master volume into the live session gain used by mix/fill threads.
    /// </summary>
    /// <param name="linear">Linear gain 0–1.</param>
    public void SetSessionMasterVolume(float linear)
    {
	    float clamped = Math.Clamp(linear, 0f, 1f);
	    lock (_sessionMasterLock)
	    {
		    if (Math.Abs(_sessionMasterLinear - clamped) < 1e-6f)
			    return;
		    _sessionMasterLinear = clamped;
	    }
	    EmitSessionMasterControlChanged();
    }

    /// <summary>
    /// Sets runtime master mute. Does not write the showfile.
    /// </summary>
    /// <param name="muted">True to silence all cue audio output.</param>
    public void SetSessionMasterMuted(bool muted)
    {
	    lock (_sessionMasterLock)
	    {
		    if (_sessionMasterMuted == muted)
			    return;
		    _sessionMasterMuted = muted;
	    }
	    EmitSessionMasterControlChanged();
    }

    /// <summary>
    /// Clears runtime mute and reloads volume + output limits from show settings (New Session / load).
    /// </summary>
    public void SyncSessionMasterFromSettings()
    {
	    float linear = 1f;
	    float maxDb = Settings.DefaultAudioOutputMaxDb;
	    float minDb = Settings.DefaultAudioOutputMinDb;
	    if (_globalData?.Settings != null)
	    {
		    linear = Math.Clamp(_globalData.Settings.AudioMasterVolume, 0f, 1f);
		    maxDb = _globalData.Settings.AudioOutputMaxDb;
		    minDb = _globalData.Settings.AudioOutputMinDb;
	    }

	    lock (_sessionMasterLock)
	    {
		    _sessionMasterLinear = linear;
		    _sessionMasterMuted = false;
	    }
	    SetOutputLimits(maxDb, minDb);
	    EmitSessionMasterControlChanged();
    }

    private void EmitSessionMasterControlChanged()
    {
	    float linear;
	    bool muted;
	    lock (_sessionMasterLock)
	    {
		    linear = _sessionMasterLinear;
		    muted = _sessionMasterMuted;
	    }
	    _globalSignals?.EmitSignal(nameof(GlobalSignals.AudioMasterControlChanged), linear, muted);
    }

    /// <summary>
    /// Returns the audio devices that are "used" (configured in any AudioOutputPatch or currently opened via direct/patch)
    /// mapped to whether they are currently connected and available.
    /// Used by the footer to display status on hover.
    /// </summary>
    /// <returns>Dictionary of device name → isConnected. True means green/connected; false means red (used but not connected).</returns>
    public Dictionary<string, bool> GetUsedAudioDeviceStatuses()
    {
        var result = new Dictionary<string, bool>();
        var availableList = GetAvailableAudioDeviceNames() ?? new List<string>();
        var available = new HashSet<string>(availableList);
        var openList = GetOpenAudioDevicesNames();
        var open = new HashSet<string>(openList);

        var used = new HashSet<string>(open);

        // Include devices configured in audio output patches (these are the persistent "used" devices)
        if (_globalData?.Settings != null)
        {
            try
            {
                var patches = _globalData.Settings.GetAudioOutputPatches();
                foreach (var patch in patches.Values)
                {
                    if (patch?.OutputDevices != null)
                    {
                        foreach (var deviceName in patch.OutputDevices.Keys)
                        {
                            used.Add(deviceName);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                GD.PrintErr($"AudioDevices:GetUsedAudioDeviceStatuses - Error reading patches: {ex.Message}");
            }
        }

        foreach (var name in used)
        {
            // Connected if present in available devices or successfully opened
            bool isConnected = available.Contains(name) || open.Contains(name);
            result[name] = isConnected;
        }

        return result;
    }

    /// <summary>
    /// Retrieves the audio specifications for a device by name.
    /// </summary>
    /// <param name="name">The name of the audio device.</param>
    /// <returns>The SDL.AudioSpec struct for the device.</returns>
    /// <remarks>
    /// Assumes the device exists; no error checking for invalid names.
    /// </remarks>
    private SDL.AudioSpec GetAudioDeviceSpec(string name)
    {
	    var device = GetAudioDevicePhysicalIdFromName(name);
	    SDL.GetAudioDeviceFormat(device, out SDL.AudioSpec spec, out _);
	    return spec; // Return SDL_AudioSpec structu
    } 

    
    /// <summary>
    /// Converts audio device specs into a readable list of strings.
    /// </summary>
    /// <param name="name">The name of the audio device.</param>
    /// <returns>A list of formatted spec strings (e.g., "Bit Depth: 16 (S16LE)").</returns>
    public List<string> GetReadableAudioDeviceSpecs(string name)
    {
	    var specs = new List<string>();
	    var device = GetAudioDevicePhysicalIdFromName(name);
	    SDL.GetAudioDeviceFormat(device, out SDL.AudioSpec spec, out _);
	    var format = spec.Format.ToString().Substring(5);
	    specs.Add($"Bit Depth: {GetBitDepth(spec.Format)} ({format})");
	    specs.Add($"Bit Rate: {spec.Freq}");
	    specs.Add($"Channels: {spec.Channels}");
	    return specs;
    }

    private uint GetAudioDevicePhysicalIdFromName(string name)
    {
	    var devices = SDL.GetAudioPlaybackDevices(out int _);

	    if (devices != null)
	    {
		    foreach (var deviceId in devices)
		    {
			    var deviceName = SDL.GetAudioDeviceName(deviceId);
			    if (deviceName == name) return deviceId;
		    }
	    }

	    return 0;
    }

    public int? GetAudioDeviceIdFromName(string name)
    {
	    foreach (var device in _openDevices.Where(device => name == device.Value.Name))
	    {
		    return device.Key;
	    }
		
	    return null;
    }

    public AudioDevice GetAudioDevice(int deviceId)
    {
	    return _openDevices.GetValueOrDefault(deviceId);
    }

    public AudioDevice GetAudioDeviceByLogicalId(uint logicalId)
    {
	    return _openDevices.Values.FirstOrDefault(d => d.LogicalId == logicalId);
    }
    
    /// <summary>
    /// Binds SDL audio streams for an active playback to its patch or direct output device(s).
    /// </summary>
    /// <param name="playback">The playback session to bind streams for.</param>
    /// <returns>
    /// True if at least one stream was created and bound; false if no output is assigned,
    /// devices could not be opened, or stream creation failed for all devices.
    /// </returns>
    public async Task<bool> StartAudioPlayback(IAudioPlayback playback)
    {
	    if (playback == null)
	    {
		    GD.PrintErr("AudioDevices:StartAudioPlayback - Playback is null.");
		    return false;
	    }

	    await Task.Yield(); // keep async signature for call sites

	    if (playback.DeviceStreams == null)
		    playback.DeviceStreams = new Dictionary<uint, IntPtr>();
	    if (playback.DeviceStreamChannels == null)
		    playback.DeviceStreamChannels = new Dictionary<uint, int>();

	    bool isDirect = !string.IsNullOrEmpty(playback.DirectOutput);
	    List<AudioDevice> devicesToOpen;

	    if (isDirect)
	    {
		    var device = OpenAudioDevice(playback.DirectOutput, out string error);
		    if (device == null)
		    {
			    _globalSignals.EmitSignal(nameof(GlobalSignals.Log),
				    $"AudioDevices:StartAudioPlayback - Failed to open direct output device {playback.DirectOutput}: {error}", 2);
			    return false;
		    }
		    devicesToOpen = new List<AudioDevice> { device };
	    }
	    else if (playback.Patch != null)
	    {
		    // Ensure patch devices are open
		    foreach (var deviceName in playback.Patch.OutputDevices.Keys)
		    {
			    if (GetAudioDeviceIdFromName(deviceName) == null)
				    OpenAudioDevice(deviceName, out _);
		    }
		    devicesToOpen = _openDevices.Values.Where(d => ShouldRouteToDeviceAudio(d, playback)).ToList();
		    if (devicesToOpen.Count == 0)
		    {
			    _globalSignals.EmitSignal(nameof(GlobalSignals.Log),
				    "AudioDevices:StartAudioPlayback - No valid devices found for patch.", 2);
			    return false;
		    }
	    }
	    else
	    {
		    _globalSignals.EmitSignal(nameof(GlobalSignals.Log),
			    "AudioDevices:StartAudioPlayback - No patch or direct output assigned.", 2);
		    return false;
	    }

	    foreach (var device in devicesToOpen)
	    {
		    if (device.LogicalId == 0)
		    {
			    GD.PrintErr($"AudioDevices:StartAudioPlayback - Invalid LogicalId for device {device.Name}.");
			    continue;
		    }

		    // Output channel count must match what the mixer writes into this stream
		    int outChannels = AudioMixMatrix.ResolveOutputChannelCount(
			    playback.SourceChannels,
			    isDirect,
			    device,
			    playback.Routing,
			    playback.Patch,
			    device.Name);

		    if (outChannels <= 0)
			    outChannels = Math.Max(1, playback.SourceChannels);

		    var sourceSpec = new SDL.AudioSpec
		    {
			    Freq = playback.SourceSampleRate,
			    Format = playback.SourceFormat,
			    Channels = (byte)outChannels
		    };

		    SDL.GetAudioDeviceFormat(device.LogicalId, out var deviceSpec, out var _);

		    var stream = SDL.CreateAudioStream(sourceSpec, deviceSpec);
		    if (stream == IntPtr.Zero)
		    {
			    _globalSignals.EmitSignal(nameof(GlobalSignals.Log),
				    $"AudioDevices:StartAudioPlayback - Failed to create stream for {device.Name}: {SDL.GetError()}", 2);
			    continue;
		    }

		    if (!SDL.BindAudioStream(device.LogicalId, stream))
		    {
			    _globalSignals.EmitSignal(nameof(GlobalSignals.Log),
				    $"AudioDevices:StartAudioPlayback - Failed to bind stream for {device.Name}: {SDL.GetError()}", 2);
			    SDL.DestroyAudioStream(stream);
			    continue;
		    }

		    playback.DeviceStreams[device.LogicalId] = stream;
		    playback.DeviceStreamChannels[device.LogicalId] = outChannels;

		    lock (_activeAudioPlaybacks)
		    {
			    if (!_activeAudioPlaybacks.ContainsKey(device.LogicalId))
				    _activeAudioPlaybacks[device.LogicalId] = new List<IAudioPlayback>();
			    _activeAudioPlaybacks[device.LogicalId].Add(playback);
		    }

		    if (SDL.AudioDevicePaused(device.LogicalId) == true)
		    {
			    SDL.ResumeAudioDevice(device.LogicalId);
			    GD.Print($"AudioDevices:StartAudioPlayback - Resumed device {device.Name}");
		    }

		    GD.Print($"AudioDevices:StartAudioPlayback - Stream for {device.Name}: srcCh={outChannels} rate={playback.SourceSampleRate} → devCh={deviceSpec.Channels} rate={deviceSpec.Freq}");
	    }

	    if (playback.DeviceStreams.Count == 0)
	    {
		    _globalSignals.EmitSignal(nameof(GlobalSignals.Log),
			    "AudioDevices:StartAudioPlayback - No streams were created for playback.", 2);
		    return false;
	    }

	    GD.Print("AudioDevices:StartAudioPlayback - Audio playback streams ready.");
	    return true;
    }
    
    
    
    private bool ShouldRouteToDeviceAudio(AudioDevice device, IAudioPlayback playback)
    {
	    return playback.Patch.OutputDevices.ContainsKey(device.Name); // Simplified; expand if needed for channel routing
    }
    
    /// <summary>
    /// Handles cleanup when an audio playback completes, pausing devices with no active playbacks.
    /// </summary>
    /// <param name="playback">The completed ActiveAudioPlayback instance.</param>
    /// <remarks>
    /// For each device in the playback:
    /// 1. Removes the playback from _activePlaybacks.
    /// 2. Destroys the associated SDL audio stream.
    /// 3. Pauses the device if no playbacks remain.
    /// Ensures resources are freed and devices are paused to save CPU when idle.
    /// Logs cleanup actions via GD.Print for debugging.
    /// </remarks>
    /// <summary>
    /// Removes a finished playback from tracking and pauses idle devices.
    /// Call before destroying streams.
    /// </summary>
    public void NotifyPlaybackCompleted(IAudioPlayback playback)
    {
	    if (playback?.DeviceStreams == null) return;

	    foreach (var logicalId in playback.DeviceStreams.Keys.ToList())
	    {
		    lock (_activeAudioPlaybacks)
		    {
			    if (_activeAudioPlaybacks.TryGetValue(logicalId, out var list))
			    {
				    list.Remove(playback);
				    GD.Print($"AudioDevices:NotifyPlaybackCompleted - Removed playback from device {logicalId}");
				    if (list.Count == 0)
				    {
					    var device = GetAudioDeviceByLogicalId(logicalId);
					    if (device != null)
					    {
						    SDL.PauseAudioDevice(device.LogicalId);
						    GD.Print($"AudioDevices:NotifyPlaybackCompleted - Paused idle device {device.Name}");
					    }
					    _activeAudioPlaybacks.Remove(logicalId);
				    }
			    }
		    }
	    }
    }
    
    

    /// <summary>
    /// Maps an SDL.AudioFormat to its bit depth.
    /// </summary>
    /// <param name="format">The SDL audio format.</param>
    /// <returns>The bit depth (e.g., 16), or 0 for unsupported formats.</returns>
    /// <remarks>
    /// Logs a warning for unknown formats. Consider throwing an exception in strict modes.
    /// </remarks>
	public static int GetBitDepth(SDL.AudioFormat format)
	{
		switch (format)
		{
			case SDL.AudioFormat.AudioU8:
			case SDL.AudioFormat.AudioS8:
				return 8;
			case SDL.AudioFormat.AudioS16BE:
			case SDL.AudioFormat.AudioS16LE:
				return 16;
			case SDL.AudioFormat.AudioF32BE:
			case SDL.AudioFormat.AudioF32LE:
			case SDL.AudioFormat.AudioS32BE:
			case SDL.AudioFormat.AudioS32LE:
				return 32;
			default:
				return 0; // Unknown or unsupported format
		}
	}

    

	public override void _ExitTree()
	{
		_activeAudioPlaybacks.Clear();
		
		foreach (var device in _openDevices.Values.ToList())
		{
			CloseAudioDevice(device.DeviceId);
		}
		GD.Print("AudioDevices:_ExitTree - Cleaned up devices.");
	}
}