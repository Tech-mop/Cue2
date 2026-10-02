// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System;
using Cue2.Services;
using Cue2.UI.Utilities;
using Godot;
using static Cue2.UI.Utilities.UiLocalizer;

namespace Cue2.UI.Settings;

/// <summary>
/// One CueNet device: discovered summary, or a connected card with component enable flags.
/// </summary>
public partial class SettingsCueNetDeviceCard : PanelContainer
{
    /// <summary>Device id this card represents.</summary>
    public string DeviceId { get; private set; } = "";

    /// <summary>Connect this discovered device.</summary>
    public event Action<string> ConnectRequested = delegate { };

    /// <summary>Drop the TCP session.</summary>
    public event Action<string> DisconnectRequested = delegate { };

    /// <summary>Send <c>cuelight identify</c>.</summary>
    public event Action<string> IdentifyRequested = delegate { };

    /// <summary>Component checkbox changed (device id, token, enabled).</summary>
    public event Action<string, string, bool> ComponentToggled = delegate { };

    private HardwarePluginLoader _loader;
    private bool _syncing;
    private Label _nameLabel;
    private Label _ipLabel;
    private Label _fwLabel;
    private Label _statusLabel;
    private ColorRect _statusDot;
    private Button _identifyButton;
    private Button _connectButton;
    private VBoxContainer _componentsBox;

    /// <summary>
    /// Fills the card from a device snapshot and optional TCP session.
    /// </summary>
    public void Bind(
        HardwarePluginLoader.DeviceInfo device,
        HardwarePluginLoader.SessionInfo session,
        HardwarePluginLoader loader)
    {
        _loader = loader;
        DeviceId = device.Id ?? "";
        EnsureUi();

        bool connected = session?.IsConnected == true;
        string sku = string.IsNullOrWhiteSpace(device.Sku) ? "CueNet" : device.Sku;
        _nameLabel.Text = sku;
        _ipLabel.Text = string.IsNullOrEmpty(device.IpAddress) ? "—" : device.IpAddress;
        _fwLabel.Text = string.IsNullOrEmpty(device.FirmwareVersion)
            ? "—"
            : $"fw {device.FirmwareVersion}";

        string state = connected
            ? (string.IsNullOrEmpty(session.State) ? "Connected" : session.State)
            : (string.IsNullOrEmpty(session?.State) ? "Discovered" : session.State);
        if (!string.IsNullOrEmpty(session?.Error) && !connected)
            state = $"{state} ({session.Error})";
        _statusLabel.Text = state;
        _statusDot.Color = connected
            ? new Color(0.25f, 0.75f, 0.4f)
            : new Color(0.55f, 0.55f, 0.55f);

        bool hasCueLight = HasComponent(device, "cuelight")
                           && (_loader == null || _loader.IsComponentEnabled(DeviceId, "cuelight"));
        _identifyButton.Visible = HasComponent(device, "cuelight");
        _identifyButton.Disabled = !hasCueLight;
        _identifyButton.TooltipText = hasCueLight
            ? T("Flash cue pixels cyan for about 1.6 s.")
            : T("Cue Light is disabled on this device.");

        _connectButton.Text = connected ? T("Disconnect") : T("Connect");

        ApplyCardStyle(connected);
        RebuildComponents(device, connected);
        UiLocalizer.LocalizeTree(this);
    }

    private void EnsureUi()
    {
        if (_nameLabel != null)
            return;

        SizeFlagsHorizontal = SizeFlags.ExpandFill;
        var margin = new MarginContainer();
        margin.AddThemeConstantOverride("margin_left", 10);
        margin.AddThemeConstantOverride("margin_top", 8);
        margin.AddThemeConstantOverride("margin_right", 10);
        margin.AddThemeConstantOverride("margin_bottom", 8);
        AddChild(margin);

        var root = new VBoxContainer();
        root.AddThemeConstantOverride("separation", 6);
        root.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        margin.AddChild(root);

        var header = new HBoxContainer();
        header.AddThemeConstantOverride("separation", 8);
        root.AddChild(header);

        var titles = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        titles.AddThemeConstantOverride("separation", 1);
        _nameLabel = new Label();
        _nameLabel.AddThemeFontSizeOverride("font_size", 13);
        _nameLabel.SetMeta(MetaSkip, true);
        _ipLabel = new Label();
        _ipLabel.AddThemeFontSizeOverride("font_size", 11);
        _ipLabel.AddThemeColorOverride("font_color", new Color(0.75f, 0.75f, 0.75f));
        _ipLabel.SetMeta(MetaSkip, true);
        _fwLabel = new Label();
        _fwLabel.AddThemeFontSizeOverride("font_size", 10);
        _fwLabel.AddThemeColorOverride("font_color", new Color(0.6f, 0.6f, 0.6f));
        _fwLabel.SetMeta(MetaSkip, true);
        titles.AddChild(_nameLabel);
        titles.AddChild(_ipLabel);
        titles.AddChild(_fwLabel);
        header.AddChild(titles);

        var statusCol = new HBoxContainer();
        statusCol.AddThemeConstantOverride("separation", 6);
        _statusDot = new ColorRect
        {
            CustomMinimumSize = new Vector2(10, 10),
            SizeFlagsVertical = SizeFlags.ShrinkCenter,
        };
        _statusDot.SetMeta(MetaSkip, true);
        _statusLabel = new Label();
        _statusLabel.AddThemeFontSizeOverride("font_size", 10);
        _statusLabel.SetMeta(MetaSkip, true);
        statusCol.AddChild(_statusDot);
        statusCol.AddChild(_statusLabel);
        header.AddChild(statusCol);

        _identifyButton = new Button
        {
            Text = "Identify",
            FocusMode = FocusModeEnum.None,
            CustomMinimumSize = new Vector2(88, 0),
        };
        _identifyButton.Pressed += () => IdentifyRequested?.Invoke(DeviceId);
        header.AddChild(_identifyButton);

        _connectButton = new Button
        {
            Text = "Connect",
            FocusMode = FocusModeEnum.None,
            CustomMinimumSize = new Vector2(100, 0),
        };
        _connectButton.Pressed += OnConnectPressed;
        header.AddChild(_connectButton);

        _componentsBox = new VBoxContainer();
        _componentsBox.AddThemeConstantOverride("separation", 4);
        root.AddChild(_componentsBox);
    }

    private void OnConnectPressed()
    {
        if (_connectButton.Text == T("Disconnect"))
            DisconnectRequested?.Invoke(DeviceId);
        else
            ConnectRequested?.Invoke(DeviceId);
    }

    private void RebuildComponents(HardwarePluginLoader.DeviceInfo device, bool connected)
    {
        foreach (Node child in _componentsBox.GetChildren())
            child.QueueFree();

        if (!connected)
        {
            _componentsBox.Visible = false;
            return;
        }

        string[] tokens = SplitComponents(device.Components);
        if (tokens.Length == 0)
        {
            _componentsBox.Visible = false;
            return;
        }

        _componentsBox.Visible = true;
        var caption = new Label { Text = "Components" };
        caption.AddThemeFontSizeOverride("font_size", 11);
        _componentsBox.AddChild(caption);

        var grid = new GridContainer { Columns = 2 };
        grid.AddThemeConstantOverride("h_separation", 16);
        grid.AddThemeConstantOverride("v_separation", 2);
        _syncing = true;
        foreach (string token in tokens)
        {
            var box = new CheckBox
            {
                Text = ComponentLabel(token),
                FocusMode = FocusModeEnum.None,
            };
            box.SetPressedNoSignal(_loader == null || _loader.IsComponentEnabled(DeviceId, token));
            string captured = token;
            box.Toggled += pressed =>
            {
                if (_syncing)
                    return;
                ComponentToggled?.Invoke(DeviceId, captured, pressed);
            };
            grid.AddChild(box);
        }

        _syncing = false;
        _componentsBox.AddChild(grid);
    }

    private void ApplyCardStyle(bool connected)
    {
        var style = new StyleBoxFlat
        {
            BgColor = connected ? new Color(0.12f, 0.16f, 0.16f) : new Color(0.11f, 0.11f, 0.11f),
            BorderColor = connected ? new Color(0.2f, 0.45f, 0.45f) : new Color(0.35f, 0.35f, 0.35f),
            BorderWidthLeft = 1,
            BorderWidthTop = 1,
            BorderWidthRight = 1,
            BorderWidthBottom = 1,
            CornerRadiusTopLeft = 3,
            CornerRadiusTopRight = 3,
            CornerRadiusBottomRight = 3,
            CornerRadiusBottomLeft = 3,
            ContentMarginLeft = 0,
            ContentMarginTop = 0,
            ContentMarginRight = 0,
            ContentMarginBottom = 0,
        };
        AddThemeStyleboxOverride("panel", style);
    }

    /// <summary>English label for a HELLO component token.</summary>
    public static string ComponentLabel(string token)
    {
        return token switch
        {
            "cuelight" => "Cue Light",
            "dmx" => "DMX",
            "audio_in" => "Audio In",
            "audio_out" => "Audio Out",
            "gpio" => "GPIO",
            "tape" => "Tape",
            "rs232" => "RS-232",
            "encoder" => "Encoder",
            "tft" => "Display",
            "button" => "Button",
            _ => token,
        };
    }

    private static bool HasComponent(HardwarePluginLoader.DeviceInfo device, string token)
    {
        foreach (string item in SplitComponents(device.Components))
        {
            if (string.Equals(item, token, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static string[] SplitComponents(string comps)
    {
        if (string.IsNullOrWhiteSpace(comps))
            return Array.Empty<string>();
        return comps.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
}
