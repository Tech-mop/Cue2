// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Cue2.Services;
using Cue2.UI.Utilities;
using Godot;
using static Cue2.UI.Utilities.UiLocalizer;

namespace Cue2.UI.Settings;

/// <summary>
/// Settings panel for CueNet: enable the host, discover boards, and manage TCP sessions.
/// </summary>
/// <remarks>
/// The Settings tree only lists this page when <see cref="FeatureFlags.CueNetEnabled"/> is true.
/// Device names, IPs, and firmware strings are hardware data and are not translated.
/// </remarks>
public partial class SettingsCueNet : ScrollContainer
{
    /// <summary>Stable Settings tree key (English, persisted in user data).</summary>
    public const string MenuKey = "CueNet";

    private GlobalSignals _globalSignals;
    private GlobalData _globalData;
    private HistoryManager _historyManager;
    private HardwarePluginLoader _loader;

    private CheckButton _enableCheck;
    private Label _statusLabel;
    private Label _hostLabel;
    private Label _deviceCountLabel;
    private VBoxContainer _deviceList;
    private Button _discoverButton;
    private Button _refreshButton;
    private Label _emptyLabel;
    private bool _isSyncingUi;

    /// <inheritdoc />
    public override void _Ready()
    {
        _globalSignals = GetNodeOrNull<GlobalSignals>("/root/GlobalSignals");
        _globalData = GetNodeOrNull<GlobalData>("/root/GlobalData");
        _historyManager = _globalData?.HistoryManager;
        _loader = _globalData?.HardwarePluginLoader;

        _enableCheck = GetNodeOrNull<CheckButton>("%EnableCueNetCheck");
        _statusLabel = GetNodeOrNull<Label>("%StatusLabel");
        _hostLabel = GetNodeOrNull<Label>("%HostLabel");
        _deviceCountLabel = GetNodeOrNull<Label>("%DeviceCountLabel");
        _deviceList = GetNodeOrNull<VBoxContainer>("%DeviceList");
        _discoverButton = GetNodeOrNull<Button>("%DiscoverButton");
        _refreshButton = GetNodeOrNull<Button>("%RefreshButton");
        _emptyLabel = GetNodeOrNull<Label>("%EmptyLabel");

        _statusLabel?.SetMeta(MetaSkip, true);
        _hostLabel?.SetMeta(MetaSkip, true);
        _deviceCountLabel?.SetMeta(MetaSkip, true);
        _emptyLabel?.SetMeta(MetaSkip, true);

        if (_enableCheck != null)
            _enableCheck.Toggled += OnEnableToggled;
        if (_discoverButton != null)
            _discoverButton.Pressed += OnDiscoverPressed;
        if (_refreshButton != null)
            _refreshButton.Pressed += OnRefreshPressed;

        if (_loader != null)
        {
            _loader.DevicesChanged += OnDevicesChanged;
            _loader.ConnectionChanged += OnConnectionChanged;
        }

        if (_historyManager != null)
            _historyManager.HistoryRestored += OnHistoryRestored;
        if (_globalSignals != null)
            _globalSignals.NewSession += OnNewSession;

        VisibilityChanged += OnVisibilityChanged;

        LocalizeTree(this);
        if (_globalSignals != null)
            _globalSignals.LocaleChanged += OnLocaleChanged;

        SyncFromLoader();
    }

    /// <inheritdoc />
    public override void _ExitTree()
    {
        VisibilityChanged -= OnVisibilityChanged;

        if (_globalSignals != null)
        {
            _globalSignals.LocaleChanged -= OnLocaleChanged;
            _globalSignals.NewSession -= OnNewSession;
        }

        if (_historyManager != null)
            _historyManager.HistoryRestored -= OnHistoryRestored;

        if (_loader != null)
        {
            _loader.DevicesChanged -= OnDevicesChanged;
            _loader.ConnectionChanged -= OnConnectionChanged;
        }

        if (_enableCheck != null)
            _enableCheck.Toggled -= OnEnableToggled;
        if (_discoverButton != null)
            _discoverButton.Pressed -= OnDiscoverPressed;
        if (_refreshButton != null)
            _refreshButton.Pressed -= OnRefreshPressed;

        base._ExitTree();
    }

    private void OnLocaleChanged(string localeCode)
    {
        if (!GodotObject.IsInstanceValid(this))
            return;
        LocalizeTree(this);
        SyncFromLoader();
    }

    private void OnVisibilityChanged()
    {
        if (Visible)
            SyncFromLoader();
    }

    private void OnDevicesChanged()
    {
        if (_historyManager?.IsRestoring == true)
            return;
        if (Visible)
            SyncFromLoader();
    }

    private void OnConnectionChanged()
    {
        if (_historyManager?.IsRestoring == true)
            return;
        if (Visible)
            SyncFromLoader();
    }

    private void OnHistoryRestored(int scope)
    {
        if (!GodotObject.IsInstanceValid(this))
            return;
        if (scope != (int)HistoryManager.HistoryScope.Settings)
            return;
        SyncFromLoader();
    }

    private void OnNewSession()
    {
        if (!GodotObject.IsInstanceValid(this))
            return;
        SyncFromLoader();
    }

    private void OnEnableToggled(bool pressed)
    {
        if (_isSyncingUi || _loader == null)
            return;
        if (_historyManager?.IsRestoring == true)
            return;
        if (_loader.RuntimeEnabled == pressed)
            return;
        RecordCueNetHistory(pressed ? "Enable CueNet" : "Disable CueNet");
        _ = SetEnabledAsync(pressed);
    }

    private async Task SetEnabledAsync(bool enabled)
    {
        await _loader.SetRuntimeEnabledAsync(enabled);
        if (IsInsideTree())
            SyncFromLoader();
    }

    private void OnDiscoverPressed() => _ = DiscoverAsync();

    private async Task DiscoverAsync()
    {
        if (_loader == null || !_loader.RuntimeEnabled)
            return;
        GD.Print("SettingsCueNet:Discover - broadcasting DISCOVER");
        await _loader.DiscoverNowAsync();
        if (IsInsideTree())
            SyncFromLoader();
    }

    private void OnRefreshPressed() => _ = RefreshAsync();

    private async Task RefreshAsync()
    {
        if (_loader == null || !_loader.RuntimeEnabled)
            return;
        GD.Print("SettingsCueNet:Refresh - checking TCP sessions");
        await _loader.RefreshConnectionsAsync();
        if (IsInsideTree())
            SyncFromLoader();
    }

    private void RecordCueNetHistory(string description)
    {
        if (_historyManager == null || _historyManager.IsRestoring)
            return;
        _historyManager.RecordSettingsChange(description, null, HardwarePluginLoader.HistoryKey);
    }

    private void SyncFromLoader()
    {
        if (_loader == null || !FeatureFlags.CueNetEnabled)
        {
            SetStatus(T("CueNet is disabled in this build."));
            SetHost("—");
            RebuildDeviceList(Array.Empty<HardwarePluginLoader.DeviceInfo>());
            return;
        }

        _isSyncingUi = true;
        try
        {
            if (_enableCheck != null)
                _enableCheck.SetPressedNoSignal(_loader.RuntimeEnabled);

            bool on = _loader.RuntimeEnabled && _loader.IsHostRunning;
            if (_discoverButton != null)
                _discoverButton.Disabled = !_loader.RuntimeEnabled;
            if (_refreshButton != null)
                _refreshButton.Disabled = !_loader.RuntimeEnabled;

            if (!_loader.IsPluginLoaded)
            {
                SetStatus(T("CueNet.Host.dll not loaded"));
                SetHost("—");
                RebuildDeviceList(Array.Empty<HardwarePluginLoader.DeviceInfo>());
                return;
            }

            string name = string.IsNullOrEmpty(_loader.PluginDisplayName) ? "CueNet" : _loader.PluginDisplayName;
            string version = string.IsNullOrEmpty(_loader.PluginVersion) ? "?" : _loader.PluginVersion;
            SetHost($"{name} {version}");
            SetStatus(on ? T("Running") : T("CueNet off"));
            RebuildDeviceList(on
                ? _loader.SnapshotDevices()
                : Array.Empty<HardwarePluginLoader.DeviceInfo>());
        }
        finally
        {
            _isSyncingUi = false;
        }
    }

    private void SetStatus(string text)
    {
        if (_statusLabel != null)
            _statusLabel.Text = text;
    }

    private void SetHost(string text)
    {
        if (_hostLabel != null)
            _hostLabel.Text = text;
    }

    private void RebuildDeviceList(IReadOnlyList<HardwarePluginLoader.DeviceInfo> devices)
    {
        if (_deviceCountLabel != null)
            _deviceCountLabel.Text = Tf("{0} devices", devices.Count);

        if (_deviceList == null)
            return;

        foreach (Node child in _deviceList.GetChildren())
        {
            if (child == _emptyLabel)
                continue;
            child.QueueFree();
        }

        bool any = devices.Count > 0;
        if (_emptyLabel != null)
        {
            _emptyLabel.Visible = !any;
            _emptyLabel.Text = _loader?.RuntimeEnabled == true
                ? T("No CueNet devices found. Plug etherCON, wait for an IP, then Discover.")
                : T("CueNet is off.");
        }

        if (!any)
            return;

        var sessions = _loader?.SnapshotSessions()
                       ?? Array.Empty<HardwarePluginLoader.SessionInfo>();

        foreach (HardwarePluginLoader.DeviceInfo device in devices)
        {
            HardwarePluginLoader.SessionInfo session = sessions.FirstOrDefault(
                s => string.Equals(s.DeviceId, device.Id, StringComparison.OrdinalIgnoreCase)
                     || string.Equals(s.IpAddress, device.IpAddress, StringComparison.OrdinalIgnoreCase));
            var card = new SettingsCueNetDeviceCard();
            _deviceList.AddChild(card);
            card.ConnectRequested += id => _ = ConnectDeviceAsync(id);
            card.DisconnectRequested += id => _ = DisconnectDeviceAsync(id);
            card.IdentifyRequested += id => _ = IdentifyDeviceAsync(id);
            card.ComponentToggled += OnComponentToggled;
            card.Bind(device, session, _loader);
        }
    }

    private void OnComponentToggled(string deviceId, string token, bool enabled)
    {
        if (_loader == null || _historyManager?.IsRestoring == true)
            return;
        if (_loader.IsComponentEnabled(deviceId, token) == enabled)
            return;
        RecordCueNetHistory("Toggle CueNet component");
        _loader.SetComponentEnabled(deviceId, token, enabled);
        SyncFromLoader();
    }

    private async Task ConnectDeviceAsync(string deviceId)
    {
        if (_loader == null)
            return;
        GD.Print($"SettingsCueNet:Connect - {deviceId}");
        HardwarePluginLoader.SessionInfo session = await _loader.ConnectAsync(deviceId);
        GD.Print($"SettingsCueNet:Connect - {session?.Summary ?? deviceId} state={session?.State}");
        if (IsInsideTree())
            SyncFromLoader();
    }

    private async Task DisconnectDeviceAsync(string deviceId)
    {
        if (_loader == null)
            return;
        GD.Print($"SettingsCueNet:Disconnect - {deviceId}");
        await _loader.DisconnectAsync(deviceId);
        if (IsInsideTree())
            SyncFromLoader();
    }

    private async Task IdentifyDeviceAsync(string deviceId)
    {
        if (_loader == null)
            return;
        if (!_loader.IsComponentEnabled(deviceId, "cuelight"))
        {
            GD.Print("SettingsCueNet:Identify - cuelight disabled");
            return;
        }

        GD.Print($"SettingsCueNet:Identify - {deviceId}");
        HardwarePluginLoader.CommandResult reply = await _loader.CommandAsync(
            deviceId, "cuelight", "identify");
        GD.Print($"SettingsCueNet:Identify - ok={reply.Ok} {reply.Error} {reply.Raw}");
        if (!reply.Ok)
        {
            _globalSignals?.EmitSignal(
                nameof(GlobalSignals.Log),
                $"CueNet identify failed: {(string.IsNullOrEmpty(reply.Error) ? reply.Raw : reply.Error)}",
                1);
        }
    }
}
