// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System;
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
using Cue2.UI.Utilities;
using Godot;
using AppSettings = Cue2.Domain.ShowSettings.Settings;

namespace Cue2.UI.Settings;

/// <summary>
/// Audio settings panel: show-scoped latency, de-click, master volume (dB),
/// output peak clamp / silence floor; runtime master mute; live open-device status.
/// </summary>
/// <remarks>
/// Performance knobs, master volume, and output limits persist with the showfile and support undo.
/// Master mute is operator runtime (like video blackout). Device list is status-only.
/// </remarks>
public partial class SettingsAudio : ScrollContainer
{
    private const double DeviceStatusRefreshSec = 0.5;

    private GlobalSignals _globalSignals;
    private GlobalData _globalData;
    private HistoryManager _historyManager;
    private AudioDevices _audioDevices;

    private OptionButton _latencyModeOption;
    private Button _latencyModeResetButton;
    private SpinBox _declickSpinBox;
    private Button _declickResetButton;
    private LineEdit _masterVolumeInput;
    private Button _masterVolumeResetButton;
    private CheckBox _masterMuteCheckBox;
    private LineEdit _outputMaxInput;
    private Button _outputMaxResetButton;
    private LineEdit _outputMinInput;
    private Button _outputMinResetButton;
    private ItemList _openDevicesList;
    private Button _refreshDevicesButton;

    /// <summary>True while pushing model → controls so handlers do not re-record history.</summary>
    private bool _isSyncingUi;

    private double _deviceStatusElapsed;

    public override void _Ready()
    {
        _globalSignals = GetNode<GlobalSignals>("/root/GlobalSignals");
        _globalData = GetNode<GlobalData>("/root/GlobalData");
        _historyManager = _globalData?.HistoryManager;
        _audioDevices = GetNodeOrNull<AudioDevices>("/root/AudioDevices");

        _latencyModeOption = GetNode<OptionButton>("%LatencyModeOption");
        _latencyModeResetButton = GetNode<Button>("%LatencyModeResetButton");
        _declickSpinBox = GetNode<SpinBox>("%DeclickSpinBox");
        _declickResetButton = GetNode<Button>("%DeclickResetButton");
        _masterVolumeInput = GetNode<LineEdit>("%MasterVolumeInput");
        _masterVolumeResetButton = GetNode<Button>("%MasterVolumeResetButton");
        _masterMuteCheckBox = GetNode<CheckBox>("%MasterMuteCheckBox");
        _outputMaxInput = GetNodeOrNull<LineEdit>("%OutputMaxInput");
        _outputMaxResetButton = GetNodeOrNull<Button>("%OutputMaxResetButton");
        _outputMinInput = GetNodeOrNull<LineEdit>("%OutputMinInput");
        _outputMinResetButton = GetNodeOrNull<Button>("%OutputMinResetButton");
        _openDevicesList = GetNode<ItemList>("%OpenDevicesList");
        _refreshDevicesButton = GetNode<Button>("%RefreshDevicesButton");

        SetupResetButton(_latencyModeResetButton, OnLatencyModeResetPressed);
        SetupResetButton(_declickResetButton, OnDeclickResetPressed);
        SetupResetButton(_masterVolumeResetButton, OnMasterVolumeResetPressed);
        SetupResetButton(_outputMaxResetButton, OnOutputMaxResetPressed);
        SetupResetButton(_outputMinResetButton, OnOutputMinResetPressed);

        _latencyModeOption.ItemSelected += OnLatencyModeSelected;
        _declickSpinBox.ValueChanged += OnDeclickChanged;
        _masterVolumeInput.TextSubmitted += OnMasterVolumeSubmitted;
        _masterVolumeInput.FocusExited += OnMasterVolumeFocusExited;
        // Master is session unity-gain only (−60…0 dB); component boost is separate.
        LineEditDbDragSlider.EnableUnityVolume(_masterVolumeInput);
        _masterMuteCheckBox.Toggled += OnMasterMuteToggled;

        if (_outputMaxInput != null)
        {
            _outputMaxInput.TextSubmitted += OnOutputMaxSubmitted;
            _outputMaxInput.FocusExited += OnOutputMaxFocusExited;
            LineEditDbDragSlider.Enable(_outputMaxInput, new LineEditDbDragSlider.Config
            {
                MinDb = AppSettings.MinAudioOutputMaxDb,
                MaxDb = AppSettings.MaxAudioOutputMaxDb,
            });
        }
        if (_outputMinInput != null)
        {
            _outputMinInput.TextSubmitted += OnOutputMinSubmitted;
            _outputMinInput.FocusExited += OnOutputMinFocusExited;
            LineEditDbDragSlider.Enable(_outputMinInput, new LineEditDbDragSlider.Config
            {
                MinDb = AppSettings.MinAudioOutputMinDb,
                MaxDb = AppSettings.MaxAudioOutputMinDb,
            });
        }

        _refreshDevicesButton.Pressed += RefreshOpenDevicesList;

        if (_historyManager != null)
            _historyManager.HistoryRestored += OnHistoryRestored;
        if (_globalSignals != null)
        {
            _globalSignals.NewSession += OnNewSession;
            _globalSignals.AudioDevicesChanged += OnAudioDevicesChanged;
            _globalSignals.AudioMasterControlChanged += OnAudioMasterControlChanged;
        }

        if (_audioDevices != null && _globalData?.Settings != null)
        {
            _audioDevices.SetSessionMasterVolume(_globalData.Settings.AudioMasterVolume);
            _audioDevices.SetOutputLimits(
                _globalData.Settings.AudioOutputMaxDb,
                _globalData.Settings.AudioOutputMinDb);
        }

        SyncSettings();
        RefreshOpenDevicesList();
        VisibilityChanged += OnVisibilityChangedForProcess;
        SetProcess(IsVisibleInTree());
    
        UiLocalizer.LocalizeTree(this);
        if (_globalSignals != null)
            _globalSignals.LocaleChanged += OnLocaleChanged;
}

    public override void _ExitTree()
    {
        if (_globalSignals != null)
            _globalSignals.LocaleChanged -= OnLocaleChanged;

        if (_historyManager != null)
            _historyManager.HistoryRestored -= OnHistoryRestored;
        if (_globalSignals != null)
        {
            _globalSignals.NewSession -= OnNewSession;
            _globalSignals.AudioDevicesChanged -= OnAudioDevicesChanged;
            _globalSignals.AudioMasterControlChanged -= OnAudioMasterControlChanged;
        }
        VisibilityChanged -= OnVisibilityChangedForProcess;
        base._ExitTree();
    }

    /// <summary>
    /// Device-status polling is only needed while this panel is actually on screen.
    /// </summary>
    private void OnVisibilityChangedForProcess()
    {
        SetProcess(IsVisibleInTree());
    }

    public override void _Process(double delta)
    {
        if (!Visible || !IsVisibleInTree())
            return;

        _deviceStatusElapsed += delta;
        if (_deviceStatusElapsed < DeviceStatusRefreshSec)
            return;
        _deviceStatusElapsed = 0;
        RefreshOpenDevicesList();
    }

    private static void SetupResetButton(Button button, System.Action handler)
    {
        if (button == null)
            return;
        button.Icon = button.GetThemeIcon("Refresh", "AtlasIcons");
        button.Pressed += handler;
    }

    private void OnHistoryRestored(int scope)
    {
        if (!GodotObject.IsInstanceValid(this) || _globalData?.Settings == null)
            return;
        if (scope != (int)HistoryManager.HistoryScope.Settings)
            return;
        SyncSettings();
    }

    private void OnNewSession()
    {
        if (!GodotObject.IsInstanceValid(this))
            return;
        SyncSettings();
        RefreshOpenDevicesList();
    }

    private void OnAudioDevicesChanged()
    {
        if (!GodotObject.IsInstanceValid(this))
            return;
        RefreshOpenDevicesList();
    }

    private void OnAudioMasterControlChanged(float linear, bool muted)
    {
        if (!GodotObject.IsInstanceValid(this) || _isSyncingUi)
            return;

        _isSyncingUi = true;
        try
        {
            if (_masterMuteCheckBox != null)
                _masterMuteCheckBox.SetPressedNoSignal(muted);

            // Mirror volume text only when show model matches (avoid fighting in-progress edits).
            if (_masterVolumeInput != null &&
                _globalData?.Settings != null &&
                Mathf.IsEqualApprox(_globalData.Settings.AudioMasterVolume, linear) &&
                !_masterVolumeInput.HasFocus())
            {
                _masterVolumeInput.Text = FormatMasterVolumeDb(linear);
                UpdateMasterVolumeResetButton();
            }
        }
        finally
        {
            _isSyncingUi = false;
        }
    }

    /// <summary>
    /// Pulls current model values into the form without re-firing edit handlers.
    /// </summary>
    private void SyncSettings()
    {
        if (_globalData?.Settings == null)
            return;

        _isSyncingUi = true;
        try
        {
            var settings = _globalData.Settings;
            if (_latencyModeOption != null)
            {
                _latencyModeOption.SetBlockSignals(true);
                _latencyModeOption.Selected = (int)settings.AudioLatencyMode;
                _latencyModeOption.SetBlockSignals(false);
            }
            if (_declickSpinBox != null)
                _declickSpinBox.SetValueNoSignal(settings.AudioDeclickMs);

            if (_masterVolumeInput != null && !_masterVolumeInput.HasFocus())
                _masterVolumeInput.Text = FormatMasterVolumeDb(settings.AudioMasterVolume);

            if (_masterMuteCheckBox != null)
            {
                bool muted = _audioDevices?.SessionMasterMuted ?? false;
                _masterMuteCheckBox.SetPressedNoSignal(muted);
            }

            if (_outputMaxInput != null && !_outputMaxInput.HasFocus())
                _outputMaxInput.Text = FormatDb(settings.AudioOutputMaxDb);
            if (_outputMinInput != null && !_outputMinInput.HasFocus())
                _outputMinInput.Text = FormatDb(settings.AudioOutputMinDb);

            UpdateLatencyModeResetButton();
            UpdateDeclickResetButton();
            UpdateMasterVolumeResetButton();
            UpdateOutputMaxResetButton();
            UpdateOutputMinResetButton();
        }
        finally
        {
            _isSyncingUi = false;
        }
    }

    private void RefreshOpenDevicesList()
    {
        if (_openDevicesList == null)
            return;

        _openDevicesList.Clear();
        if (_audioDevices == null)
        {
            _openDevicesList.AddItem(UiLocalizer.T("AudioDevices service unavailable."));
            return;
        }

        var lines = _audioDevices.GetOpenDeviceStatusLines();
        if (lines == null || lines.Count == 0)
        {
            _openDevicesList.AddItem(UiLocalizer.T("No audio devices open."));
            return;
        }

        foreach (var line in lines)
            _openDevicesList.AddItem(line ?? string.Empty);
    }

    private static string FormatMasterVolumeDb(float linear) =>
        $"{UiUtilities.LinearToDb(Mathf.Clamp(linear, 0f, 1f))}dB";

    private static string FormatDb(float db) => $"{db:0.0}dB";

    private static bool TryParseDb(string text, out float db)
    {
        db = 0f;
        if (string.IsNullOrWhiteSpace(text))
            return false;
        string cleaned = text.Replace("dB", "", StringComparison.OrdinalIgnoreCase)
            .Replace("db", "", StringComparison.OrdinalIgnoreCase)
            .Trim();
        return float.TryParse(cleaned, out db);
    }

    // ── Latency mode ────────────────────────────────────────────────────────

    private void OnLatencyModeSelected(long index)
    {
        if (_isSyncingUi || _globalData?.Settings == null || _historyManager?.IsRestoring == true)
            return;

        var next = (AudioLatencyMode)(int)index;
        if (_globalData.Settings.AudioLatencyMode == next)
        {
            UpdateLatencyModeResetButton();
            return;
        }

        _historyManager?.RecordSettingsChange("Change audio latency mode", null, "AudioLatencyMode");
        _globalData.Settings.AudioLatencyMode = next;
        UpdateLatencyModeResetButton();
    }

    private void OnLatencyModeResetPressed()
    {
        if (_isSyncingUi || _globalData?.Settings == null || _historyManager?.IsRestoring == true)
            return;
        if (_globalData.Settings.AudioLatencyMode == AppSettings.DefaultAudioLatencyMode)
            return;

        _historyManager?.RecordSettingsChange("Reset audio latency mode", null, "AudioLatencyMode");
        _globalData.Settings.AudioLatencyMode = AppSettings.DefaultAudioLatencyMode;
        SyncSettings();
    }

    private void UpdateLatencyModeResetButton()
    {
        if (_latencyModeResetButton == null || _globalData?.Settings == null)
            return;
        bool atDefault = _globalData.Settings.AudioLatencyMode == AppSettings.DefaultAudioLatencyMode;
        _latencyModeResetButton.Visible = !atDefault;
        if (!atDefault)
            _latencyModeResetButton.TooltipText = UiLocalizer.T("Reset to default: Balanced");
    }

    // ── Declick ramp ────────────────────────────────────────────────────────

    private void OnDeclickChanged(double value)
    {
        if (_isSyncingUi || _globalData?.Settings == null || _historyManager?.IsRestoring == true)
            return;

        int next = Mathf.Clamp((int)Math.Round(value),
            AppSettings.MinAudioDeclickMs, AppSettings.MaxAudioDeclickMs);
        if (_globalData.Settings.AudioDeclickMs == next)
        {
            UpdateDeclickResetButton();
            return;
        }

        _historyManager?.RecordSettingsChange("Change audio declick ramp", null, "AudioDeclickMs");
        _globalData.Settings.AudioDeclickMs = next;
        UpdateDeclickResetButton();
    }

    private void OnDeclickResetPressed()
    {
        if (_isSyncingUi || _globalData?.Settings == null || _historyManager?.IsRestoring == true)
            return;
        if (_globalData.Settings.AudioDeclickMs == AppSettings.DefaultAudioDeclickMs)
            return;

        _historyManager?.RecordSettingsChange("Reset audio declick ramp", null, "AudioDeclickMs");
        _globalData.Settings.AudioDeclickMs = AppSettings.DefaultAudioDeclickMs;
        SyncSettings();
    }

    private void UpdateDeclickResetButton()
    {
        if (_declickResetButton == null || _globalData?.Settings == null)
            return;
        bool atDefault = _globalData.Settings.AudioDeclickMs == AppSettings.DefaultAudioDeclickMs;
        _declickResetButton.Visible = !atDefault;
        if (!atDefault)
            _declickResetButton.TooltipText = UiLocalizer.ResetDefaultTip($"{AppSettings.DefaultAudioDeclickMs} ms");
    }

    // ── Master volume (dB LineEdit) + mute (runtime) ────────────────────────

    private void OnMasterVolumeSubmitted(string text)
    {
        CommitMasterVolume(text);
    }

    private void OnMasterVolumeFocusExited()
    {
        if (_isSyncingUi || _masterVolumeInput == null)
            return;
        CommitMasterVolume(_masterVolumeInput.Text);
    }

    private void CommitMasterVolume(string text)
    {
        if (_isSyncingUi || _globalData?.Settings == null || _historyManager?.IsRestoring == true)
            return;
        if (_masterVolumeInput == null)
            return;

        try
        {
            if (!float.TryParse(text.Replace("dB", "").Replace("db", "").Trim(), out var dbValue))
            {
                _globalSignals?.EmitSignal(nameof(GlobalSignals.Log),
                    $"Invalid master volume: {text}", 1);
                _masterVolumeInput.Text = FormatMasterVolumeDb(_globalData.Settings.AudioMasterVolume);
                return;
            }

            // Match AudioInspector / Audio Defaults: positive values treated as attenuation.
            if (dbValue > 0f)
                dbValue = -dbValue;

            float linear = UiUtilities.DbToLinear(dbValue);
            _masterVolumeInput.Text = FormatMasterVolumeDb(linear);

            if (Math.Abs(_globalData.Settings.AudioMasterVolume - linear) < 1e-6f)
            {
                UpdateMasterVolumeResetButton();
                return;
            }

            _historyManager?.RecordSettingsChange("Change audio master volume", null, "AudioMasterVolume");
            _globalData.Settings.AudioMasterVolume = linear;
            _audioDevices?.SetSessionMasterVolume(linear);
            UpdateMasterVolumeResetButton();
        }
        catch (Exception ex)
        {
            _globalSignals?.EmitSignal(nameof(GlobalSignals.Log),
                $"Error parsing master volume: {ex.Message}", 2);
            _masterVolumeInput.Text = FormatMasterVolumeDb(_globalData.Settings.AudioMasterVolume);
        }
    }

    private void OnMasterVolumeResetPressed()
    {
        if (_isSyncingUi || _globalData?.Settings == null || _historyManager?.IsRestoring == true)
            return;
        if (Mathf.IsEqualApprox(_globalData.Settings.AudioMasterVolume, AppSettings.DefaultAudioMasterVolume))
            return;

        _historyManager?.RecordSettingsChange("Reset audio master volume", null, "AudioMasterVolume");
        _globalData.Settings.AudioMasterVolume = AppSettings.DefaultAudioMasterVolume;
        _audioDevices?.SetSessionMasterVolume(AppSettings.DefaultAudioMasterVolume);
        SyncSettings();
    }

    private void UpdateMasterVolumeResetButton()
    {
        if (_masterVolumeResetButton == null || _globalData?.Settings == null)
            return;
        bool atDefault = Mathf.IsEqualApprox(
            _globalData.Settings.AudioMasterVolume, AppSettings.DefaultAudioMasterVolume);
        _masterVolumeResetButton.Visible = !atDefault;
        if (!atDefault)
            _masterVolumeResetButton.TooltipText = UiLocalizer.T("Reset to default: 0.0dB");
    }

    private void OnMasterMuteToggled(bool pressed)
    {
        if (_isSyncingUi || _audioDevices == null)
            return;
        _audioDevices.SetSessionMasterMuted(pressed);
    }

    // ── Output max (peak clamp) ─────────────────────────────────────────────

    private void OnOutputMaxSubmitted(string text) => CommitOutputMax(text);

    private void OnOutputMaxFocusExited()
    {
        if (_isSyncingUi || _outputMaxInput == null)
            return;
        CommitOutputMax(_outputMaxInput.Text);
    }

    private void CommitOutputMax(string text)
    {
        if (_isSyncingUi || _globalData?.Settings == null || _historyManager?.IsRestoring == true)
            return;
        if (_outputMaxInput == null)
            return;

        if (!TryParseDb(text, out float db))
        {
            _globalSignals?.EmitSignal(nameof(GlobalSignals.Log),
                $"Invalid max output level: {text}", 1);
            _outputMaxInput.Text = FormatDb(_globalData.Settings.AudioOutputMaxDb);
            return;
        }

        // Positive typed as attenuation toward 0, same habit as volume fields.
        if (db > 0f)
            db = 0f;
        db = Mathf.Clamp(db, AppSettings.MinAudioOutputMaxDb, AppSettings.MaxAudioOutputMaxDb);
        // Max must stay above (or equal) the silence floor.
        if (db < _globalData.Settings.AudioOutputMinDb)
            db = Mathf.Clamp(_globalData.Settings.AudioOutputMinDb,
                AppSettings.MinAudioOutputMaxDb, AppSettings.MaxAudioOutputMaxDb);

        _outputMaxInput.Text = FormatDb(db);
        if (Mathf.IsEqualApprox(_globalData.Settings.AudioOutputMaxDb, db))
        {
            UpdateOutputMaxResetButton();
            return;
        }

        _historyManager?.RecordSettingsChange("Change audio max output level", null, "AudioOutputMaxDb");
        _globalData.Settings.AudioOutputMaxDb = db;
        _audioDevices?.SetOutputLimits(db, _globalData.Settings.AudioOutputMinDb);
        UpdateOutputMaxResetButton();
    }

    private void OnOutputMaxResetPressed()
    {
        if (_isSyncingUi || _globalData?.Settings == null || _historyManager?.IsRestoring == true)
            return;
        if (Mathf.IsEqualApprox(_globalData.Settings.AudioOutputMaxDb, AppSettings.DefaultAudioOutputMaxDb))
            return;

        _historyManager?.RecordSettingsChange("Reset audio max output level", null, "AudioOutputMaxDb");
        _globalData.Settings.AudioOutputMaxDb = AppSettings.DefaultAudioOutputMaxDb;
        _audioDevices?.SetOutputLimits(
            AppSettings.DefaultAudioOutputMaxDb,
            _globalData.Settings.AudioOutputMinDb);
        SyncSettings();
    }

    private void UpdateOutputMaxResetButton()
    {
        if (_outputMaxResetButton == null || _globalData?.Settings == null)
            return;
        bool atDefault = Mathf.IsEqualApprox(
            _globalData.Settings.AudioOutputMaxDb, AppSettings.DefaultAudioOutputMaxDb);
        _outputMaxResetButton.Visible = !atDefault;
        if (!atDefault)
            _outputMaxResetButton.TooltipText =
                UiLocalizer.ResetDefaultTip(FormatDb(AppSettings.DefaultAudioOutputMaxDb));
    }

    // ── Output min (silence floor) ──────────────────────────────────────────

    private void OnOutputMinSubmitted(string text) => CommitOutputMin(text);

    private void OnOutputMinFocusExited()
    {
        if (_isSyncingUi || _outputMinInput == null)
            return;
        CommitOutputMin(_outputMinInput.Text);
    }

    private void CommitOutputMin(string text)
    {
        if (_isSyncingUi || _globalData?.Settings == null || _historyManager?.IsRestoring == true)
            return;
        if (_outputMinInput == null)
            return;

        if (!TryParseDb(text, out float db))
        {
            _globalSignals?.EmitSignal(nameof(GlobalSignals.Log),
                $"Invalid min output level: {text}", 1);
            _outputMinInput.Text = FormatDb(_globalData.Settings.AudioOutputMinDb);
            return;
        }

        if (db > 0f)
            db = -db;
        db = Mathf.Clamp(db, AppSettings.MinAudioOutputMinDb, AppSettings.MaxAudioOutputMinDb);
        // Floor must stay at or below the peak clamp.
        if (db > _globalData.Settings.AudioOutputMaxDb)
            db = Mathf.Clamp(_globalData.Settings.AudioOutputMaxDb,
                AppSettings.MinAudioOutputMinDb, AppSettings.MaxAudioOutputMinDb);

        _outputMinInput.Text = FormatDb(db);
        if (Mathf.IsEqualApprox(_globalData.Settings.AudioOutputMinDb, db))
        {
            UpdateOutputMinResetButton();
            return;
        }

        _historyManager?.RecordSettingsChange("Change audio min output level", null, "AudioOutputMinDb");
        _globalData.Settings.AudioOutputMinDb = db;
        _audioDevices?.SetOutputLimits(_globalData.Settings.AudioOutputMaxDb, db);
        UpdateOutputMinResetButton();
    }

    private void OnOutputMinResetPressed()
    {
        if (_isSyncingUi || _globalData?.Settings == null || _historyManager?.IsRestoring == true)
            return;
        if (Mathf.IsEqualApprox(_globalData.Settings.AudioOutputMinDb, AppSettings.DefaultAudioOutputMinDb))
            return;

        _historyManager?.RecordSettingsChange("Reset audio min output level", null, "AudioOutputMinDb");
        _globalData.Settings.AudioOutputMinDb = AppSettings.DefaultAudioOutputMinDb;
        _audioDevices?.SetOutputLimits(
            _globalData.Settings.AudioOutputMaxDb,
            AppSettings.DefaultAudioOutputMinDb);
        SyncSettings();
    }

    private void UpdateOutputMinResetButton()
    {
        if (_outputMinResetButton == null || _globalData?.Settings == null)
            return;
        bool atDefault = Mathf.IsEqualApprox(
            _globalData.Settings.AudioOutputMinDb, AppSettings.DefaultAudioOutputMinDb);
        _outputMinResetButton.Visible = !atDefault;
        if (!atDefault)
            _outputMinResetButton.TooltipText =
                UiLocalizer.ResetDefaultTip(FormatDb(AppSettings.DefaultAudioOutputMinDb));
    }

    /// <summary>
    /// Re-localizes panel chrome when the UI language changes.
    /// </summary>
    /// <param name="localeCode">New locale code.</param>
    private void OnLocaleChanged(string localeCode)
    {
        if (!GodotObject.IsInstanceValid(this))
            return;
        UiLocalizer.LocalizeTree(this);
    }

}
