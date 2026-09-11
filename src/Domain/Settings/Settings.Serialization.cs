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
/// Partial: GetData / LoadSettings and related clamp helpers
/// </summary>
public partial class Settings
{

    public Dictionary GetData()
    {
        var saveTable = new Dictionary();
        var patchTable = new Dictionary();

        foreach (var patch in _audioOutputPatches)
        {
            patchTable.Add(patch.Key, patch.Value.GetData());
        }

        var devices = _audioDevices.GetOpenAudioDevicesNames();

        saveTable.Add("AudioPatch", patchTable);
        saveTable.Add("AudioDevices", devices);
        saveTable.Add("Displays", _displaysManager.GetData());
        saveTable.Add("CueLights", _globalData.CueLightManager.GetData());
        
        // UiScale is a user preference (user_data.json), not showfile settings.
        saveTable.Add("GoScale", GoScale);
        saveTable.Add("CueListScale", CueListScale);
        saveTable.Add("WaveformResolution", WaveformResolution);
        saveTable.Add("StopFadeDuration", StopFadeDuration);
        saveTable.Add("DoubleGoProtection", DoubleGoProtectionSeconds);
        saveTable.Add("MediaBackupEnabled", MediaBackupEnabled);
        saveTable.Add("MultiEditEnabled", MultiEditEnabled);
        saveTable.Add("SelectNewCues", SelectNewCues);
        saveTable.Add("ShowMode", ShowMode);
        saveTable.Add("ShowTimelineWaveforms", ShowTimelineWaveforms);
        saveTable.Add("OutputBackgroundColor", OutputBackgroundColor.ToHtml(true));
        saveTable.Add("VideoQualityMode", (int)VideoQualityMode);
        saveTable.Add("VideoPreviewQuality", (int)VideoPreviewQuality);
        saveTable.Add("OutputVSyncMode", (int)OutputVSyncMode);
        saveTable.Add("AudioLatencyMode", (int)AudioLatencyMode);
        saveTable.Add("AudioDeclickMs", AudioDeclickMs);
        saveTable.Add("AudioMasterVolume", AudioMasterVolume);
        saveTable.Add("AudioOutputMaxDb", AudioOutputMaxDb);
        saveTable.Add("AudioOutputMinDb", AudioOutputMinDb);

        // Cue shell defaults (show-scoped)
        saveTable.Add("CueDefaults", CaptureCueDefaultsDict());
        saveTable.Add("AudioDefaults", CaptureAudioDefaultsDict());
        saveTable.Add("VideoDefaults", CaptureVideoDefaultsDict());
        saveTable.Add("TextDefaults", CaptureTextDefaultsDict());
        
        // Cuelights
        saveTable.Add("CueLightIdleColour", CueLightIdleColour.ToHtml());
        saveTable.Add("CueLightGoColour", CueLightGoColour.ToHtml());
        saveTable.Add("CueLightStandbyColour", CueLightStandbyColour.ToHtml());
        saveTable.Add("CueLightCountInColour", CueLightCountInColour.ToHtml());
        saveTable.Add("CueLightBrightness", CueLightBrightness);
        
        // Osc Listen
        var oscListen = GetNodeOrNull<OscListen>("/root/OscListen");
        if (oscListen != null)
        {
            saveTable.Add("OscListen", oscListen.GetData());
            // Project-action OSC bindings (Go, Save, …) — show-scoped, separate undo slice.
            saveTable.Add("OscInputMap", oscListen.GetInputMapBindingsData());
        }
        
        // Osc Connections
        saveTable.Add("OscConnections", GetNode<OscConnections>("/root/OscConnections").GetData());

        // MIDI session (enabled + session input device list)
        var midi = GetNodeOrNull<MidiManager>("/root/MidiManager");
        if (midi != null)
        {
            saveTable.Add("Midi", midi.GetData());
            // Project-action MIDI bindings (Go, Save, …) — show-scoped, separate undo slice.
            saveTable.Add("MidiInputMap", midi.GetInputMapBindingsData());
        }

        // Keyboard Input Map is stored in user:// via UserDataManager (not in the showfile).
        
        return saveTable;
    }

    /// <summary>
    /// Applies a full showfile settings block (audio, displays, then remaining prefs).
    /// History / import should prefer this sync entry; showfile open uses the sliced loaders
    /// with a frame yield between them so the overlay can paint.
    /// </summary>
    /// <param name="settingsData">Root <c>settings</c> dictionary from the showfile.</param>
    public void LoadSettings(Dictionary settingsData)
    {
        if (settingsData == null)
            return;
        GD.Print($"Settings:LoadSettings - Loading Settings");
        LoadAudioFromData(settingsData);
        LoadDisplaysFromData(settingsData);
        LoadRemainingFromData(settingsData);
    }

    /// <summary>
    /// Opens show audio devices, rebuilds the patch table, and reconciles leftover SDL devices.
    /// Emits <see cref="GlobalSignals.AudioDevicesChanged"/> once at the end of the batch.
    /// </summary>
    /// <param name="settingsData">Showfile settings dictionary.</param>
    public void LoadAudioFromData(Dictionary settingsData)
    {
        if (settingsData == null)
            return;

        // Names from the showfile open-device list (may include devices opened for direct-output
        // that are not currently in a patch). Unioned with patch keys before reconcile.
        var showfileDeviceNames = new System.Collections.Generic.List<string>();

        SessionLoadTimer.Current?.Begin("settings.audio");

        bool prevSuppress = _audioDevices != null && _audioDevices.SuppressChangedSignals;
        if (_audioDevices != null)
            _audioDevices.SuppressChangedSignals = true;
        try
        {
            if (settingsData.TryGetValue("AudioDevices", out var devices))
            {
                GD.Print($"Settings:LoadSettings - Loading AudioDevices");
                // Soft convert — hard cast (Array<string>) throws when JSON yields Array or Variant mix.
                var deviceArray = devices.AsGodotArray();
                foreach (var device in deviceArray)
                {
                    string deviceName = device.AsString();
                    if (string.IsNullOrEmpty(deviceName)) continue;
                    showfileDeviceNames.Add(deviceName);
                    _audioDevices.OpenAudioDevice(deviceName, out var _);
                }
            }

            if (settingsData.TryGetValue("AudioPatch", out var patchs))
            {
                GD.Print($"Settings:LoadSettings - Loading AudioPatches");
                // Replace any leftover patches so open/load is authoritative.
                foreach (var existing in _audioOutputPatches.Values.ToList())
                {
                    if (existing != null && GodotObject.IsInstanceValid(existing))
                        existing.Free();
                }
                _audioOutputPatches.Clear();

                if (patchs.VariantType == Variant.Type.Dictionary)
                {
                    var patchDict = patchs.AsGodotDictionary();
                    foreach (var patchKey in patchDict.Keys)
                    {
                        var patchAsDict = patchDict[patchKey].AsGodotDictionary();
                        var patchObj = AudioOutputPatch.FromData(patchAsDict);
                        if (patchObj != null)
                            AddPatch(patchObj);
                        else
                            GD.PrintErr("Settings:LoadSettings - Failed to deserialize an audio output patch.");
                    }
                }
                else
                {
                    GD.PrintErr($"Settings:LoadSettings - AudioPatch is not a Dictionary (got {patchs.VariantType}).");
                }
            }

            // Missing or empty patch table still gets a usable Default Patch.
            EnsureDefaultAudioPatch();
            ReconcileOpenAudioDevices(showfileDeviceNames);
        }
        finally
        {
            if (_audioDevices != null)
            {
                _audioDevices.SuppressChangedSignals = prevSuppress;
                if (!prevSuppress)
                    _audioDevices.NotifyDevicesChanged();
            }
        }

        SessionLoadTimer.Current?.Pause();
    }

    /// <summary>
    /// Recreates canvas / layers / output windows from the showfile Displays block.
    /// </summary>
    /// <param name="settingsData">Showfile settings dictionary.</param>
    public void LoadDisplaysFromData(Dictionary settingsData)
    {
        if (settingsData == null)
            return;

        SessionLoadTimer.Current?.Begin("settings.displays");

        if (settingsData.TryGetValue("Displays", out var displays))
        {
            GD.Print($"Settings:LoadSettings - Loading Displays");
            var displaysAsDict = displays.AsGodotDictionary();
            _displaysManager.LoadFromData(displaysAsDict);
        }
        else if (_displaysManager != null && DisplaysManager.Outputs.Count == 0)
        {
            // Legacy / partial showfile with no Displays block after ClearForOpen.
            _displaysManager.ResetToDefaults();
        }

        SessionLoadTimer.Current?.Pause();
    }

    /// <summary>
    /// Applies scalars, component defaults, cue lights, OSC, and MIDI after audio and displays.
    /// </summary>
    /// <param name="settingsData">Showfile settings dictionary.</param>
    public void LoadRemainingFromData(Dictionary settingsData)
    {
        if (settingsData == null)
            return;

        SessionLoadTimer.Current?.Begin("settings.other");

        if (settingsData.TryGetValue("CueLights", out var cueLights))
        {
            GD.Print($"Settings:LoadSettings - Loading CueLights");
            var cueLightsAsDict = cueLights.AsGodotDictionary();
            _globalData.CueLightManager.LoadData(cueLightsAsDict);
        }
        
        // Legacy showfiles may still contain "UiScale"; ignore — scale lives in UserDataManager.
        GoScale = settingsData.TryGetValue("GoScale", out var value) ? (float)value : GoScale;
        _globalSignals.EmitSignal(nameof(GlobalSignals.GoScaleChanged), GoScale);
        CueListScale = settingsData.TryGetValue("CueListScale", out value)
            ? (float)value
            : DefaultCueListScale;
        _globalSignals.EmitSignal(nameof(GlobalSignals.CueListScaleChanged), CueListScale);
        WaveformResolution = settingsData.TryGetValue("WaveformResolution", out value) ? (int)value : WaveformResolution;
        StopFadeDuration = settingsData.TryGetValue("StopFadeDuration", out value) ? (float)value : StopFadeDuration;
        DoubleGoProtectionSeconds = settingsData.TryGetValue("DoubleGoProtection", out value)
            ? Mathf.Clamp((float)value, 0f, MaxDoubleGoProtectionSeconds)
            : DefaultDoubleGoProtectionSeconds;
        // Default true for older shows that predate this setting
        MediaBackupEnabled = settingsData.TryGetValue("MediaBackupEnabled", out value)
            ? value.AsBool()
            : DefaultMediaBackupEnabled;
        MultiEditEnabled = settingsData.TryGetValue("MultiEditEnabled", out value)
            ? value.AsBool()
            : DefaultMultiEditEnabled;
        // Default true for older shows that predate this setting
        SelectNewCues = settingsData.TryGetValue("SelectNewCues", out value)
            ? value.AsBool()
            : DefaultSelectNewCues;
        // Default false (edit mode) for older shows that predate this setting
        ShowMode = settingsData.TryGetValue("ShowMode", out value)
            ? value.AsBool()
            : DefaultShowMode;
        ShowTimelineWaveforms = settingsData.TryGetValue("ShowTimelineWaveforms", out value)
            ? value.AsBool()
            : DefaultShowTimelineWaveforms;
        OutputBackgroundColor = settingsData.TryGetValue("OutputBackgroundColor", out value)
            ? Color.FromString(value.AsString(), DefaultOutputBackgroundColor)
            : DefaultOutputBackgroundColor;
        VideoQualityMode = settingsData.TryGetValue("VideoQualityMode", out value)
            ? ClampEnum(value.AsInt32(), VideoQualityMode.PreferQuality, VideoQualityMode.PreferPerformance, DefaultVideoQualityMode)
            : DefaultVideoQualityMode;
        VideoPreviewQuality = settingsData.TryGetValue("VideoPreviewQuality", out value)
            ? ClampEnum(value.AsInt32(), VideoPreviewQuality.Full, VideoPreviewQuality.Quarter, DefaultVideoPreviewQuality)
            : DefaultVideoPreviewQuality;
        OutputVSyncMode = settingsData.TryGetValue("OutputVSyncMode", out value)
            ? ClampEnum(value.AsInt32(), OutputVSyncMode.PreferVSync, OutputVSyncMode.LowLatency, DefaultOutputVSyncMode)
            : DefaultOutputVSyncMode;
        AudioLatencyMode = settingsData.TryGetValue("AudioLatencyMode", out value)
            ? ClampEnum(value.AsInt32(), AudioLatencyMode.PreferLowLatency, AudioLatencyMode.PreferStability, DefaultAudioLatencyMode)
            : DefaultAudioLatencyMode;
        AudioDeclickMs = settingsData.TryGetValue("AudioDeclickMs", out value)
            ? Math.Clamp(value.AsInt32(), MinAudioDeclickMs, MaxAudioDeclickMs)
            : DefaultAudioDeclickMs;
        AudioMasterVolume = settingsData.TryGetValue("AudioMasterVolume", out value)
            ? Math.Clamp(value.AsSingle(), 0f, 1f)
            : DefaultAudioMasterVolume;
        AudioOutputMaxDb = settingsData.TryGetValue("AudioOutputMaxDb", out value)
            ? Math.Clamp(value.AsSingle(), MinAudioOutputMaxDb, MaxAudioOutputMaxDb)
            : DefaultAudioOutputMaxDb;
        AudioOutputMinDb = settingsData.TryGetValue("AudioOutputMinDb", out value)
            ? Math.Clamp(value.AsSingle(), MinAudioOutputMinDb, MaxAudioOutputMinDb)
            : DefaultAudioOutputMinDb;
        // Keep max ≥ min so the gate and clamp cannot invert.
        if (AudioOutputMaxDb < AudioOutputMinDb)
            AudioOutputMaxDb = Math.Clamp(AudioOutputMinDb, MinAudioOutputMaxDb, MaxAudioOutputMaxDb);

        // Loading a showfile should not keep the previous show's emergency disable/blackout.
        _displaysManager?.ClearRuntimeOutputControls();
        _displaysManager?.ApplyOutputBackgroundColor(OutputBackgroundColor);
        _displaysManager?.ApplyOutputVSyncPreference();
        _audioDevices?.SyncSessionMasterFromSettings();

        // Cue shell defaults (older shows without this key keep system defaults)
        if (settingsData.TryGetValue("CueDefaults", out value) && value.VariantType == Variant.Type.Dictionary)
            ApplyCueDefaultsFromDict(value.AsGodotDictionary());
        else
            ResetCueDefaultsToSystem();

        if (settingsData.TryGetValue("AudioDefaults", out value) && value.VariantType == Variant.Type.Dictionary)
            ApplyAudioDefaultsFromDict(value.AsGodotDictionary());
        else
            ResetAudioDefaultsToSystem();

        if (settingsData.TryGetValue("VideoDefaults", out value) && value.VariantType == Variant.Type.Dictionary)
            ApplyVideoDefaultsFromDict(value.AsGodotDictionary());
        else
            ResetVideoDefaultsToSystem();

        if (settingsData.TryGetValue("TextDefaults", out value) && value.VariantType == Variant.Type.Dictionary)
            ApplyTextDefaultsFromDict(value.AsGodotDictionary());
        else
            ResetTextDefaultsToSystem();
        
        CueLightIdleColour = settingsData.TryGetValue("CueLightIdleColour", out value) ? Color.FromString(value.AsString(), CueLightIdleColour) : CueLightIdleColour;
        CueLightGoColour = settingsData.TryGetValue("CueLightGoColour", out value) ? Color.FromString(value.AsString(), CueLightGoColour) : CueLightGoColour;
        CueLightStandbyColour = settingsData.TryGetValue("CueLightStandbyColour", out value) ? Color.FromString(value.AsString(), CueLightStandbyColour) : CueLightStandbyColour;
        CueLightCountInColour = settingsData.TryGetValue("CueLightCountInColour", out value) ? Color.FromString(value.AsString(), CueLightCountInColour) : CueLightCountInColour;
        CueLightBrightness = settingsData.TryGetValue("CueLightBrightness", out value) ? (byte)value : CueLightBrightness;
        
        // Osc Listen
        SessionLoadTimer.Current?.Begin("settings.osc");
        if (settingsData.TryGetValue("OscListen", out var oscListen))
        {
            GD.Print($"Settings:LoadSettings - Loading OscListen");
            var oscListenAsDict = oscListen.AsGodotDictionary();
            GetNodeOrNull<OscListen>("/root/OscListen")?.LoadFromData(oscListenAsDict);
        }

        if (settingsData.TryGetValue("OscInputMap", out var oscInputMap) &&
            oscInputMap.VariantType == Variant.Type.Dictionary)
        {
            GD.Print("Settings:LoadSettings - Loading OscInputMap");
            GetNodeOrNull<OscListen>("/root/OscListen")
                ?.LoadInputMapBindingsData(oscInputMap.AsGodotDictionary());
        }
        
        // Osc Connections
        if (settingsData.TryGetValue("OscConnections", out var oscConnections))
        {
            GD.Print($"Settings:LoadSettings - Loading OscConnections");
            var oscConnectionsAsDict = oscConnections.AsGodotDictionary();
            GetNode<OscConnections>("/root/OscConnections").LoadFromData(oscConnectionsAsDict);
        }

        SessionLoadTimer.Current?.Begin("settings.midi");
        if (settingsData.TryGetValue("Midi", out var midiData) && midiData.VariantType == Variant.Type.Dictionary)
        {
            GD.Print("Settings:LoadSettings - Loading Midi");
            GetNodeOrNull<MidiManager>("/root/MidiManager")?.LoadFromData(midiData.AsGodotDictionary());
        }

        if (settingsData.TryGetValue("MidiInputMap", out var midiInputMap) &&
            midiInputMap.VariantType == Variant.Type.Dictionary)
        {
            GD.Print("Settings:LoadSettings - Loading MidiInputMap");
            GetNodeOrNull<MidiManager>("/root/MidiManager")
                ?.LoadInputMapBindingsData(midiInputMap.AsGodotDictionary());
        }

        // Legacy showfiles may still contain "InputMap" — ignore; bindings are user preferences.
        if (settingsData.ContainsKey("InputMap"))
        {
            GD.Print("Settings:LoadSettings - Ignoring showfile InputMap (now stored in user preferences).");
        }

        // Sync show/edit mode UI after load (always notify so chrome matches the loaded value).
        NotifyShowModeChanged();
        SessionLoadTimer.Current?.Pause();
    }
}
