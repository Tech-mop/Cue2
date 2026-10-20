// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using Cue2.Domain.Timecode;
using Cue2.Services;
using Cue2.UI.Utilities;
using Godot;

namespace Cue2.UI.Timecode;

/// <summary>
/// Reusable LCD-style timecode readout. Black face, small name above a monospace time.
/// Inputs use the cool low cascade; outputs use the warm high cascade.
/// </summary>
public partial class TimecodeClockCard : PanelContainer
{
    private Label _nameLabel;
    private Label _timeLabel;
    private Label _statusLabel;
    private HBoxContainer _transportRow;
    private Button _playButton;
    private Button _pauseButton;
    private Button _resetButton;

    private TimecodeManager _timecodeManager;
    private int _endpointId = -1;
    private string _displayName = "Timecode";
    private TimecodeValue _value;
    private TimecodeDirection _direction = TimecodeDirection.Output;
    private bool _enableTransport;
    private bool _showStatus = true;

    /// <summary>
    /// When true, Play / Pause / Reset are shown for controllable output generators.
    /// </summary>
    public bool EnableTransport
    {
        get => _enableTransport;
        set
        {
            _enableTransport = value;
            RefreshTransportVisibility();
        }
    }

    /// <summary>
    /// When false, the Idle/Running line is hidden (GO header uses this).
    /// </summary>
    public bool ShowStatus
    {
        get => _showStatus;
        set
        {
            _showStatus = value;
            if (_statusLabel != null)
                _statusLabel.Visible = value;
        }
    }

    /// <summary>Bound endpoint id, or <c>-1</c>.</summary>
    public int EndpointId => _endpointId;

    /// <summary>
    /// Compacts name/time/padding so this card fits inside <paramref name="maxHeight"/>
    /// without expanding a parent (used on the GO header).
    /// </summary>
    /// <param name="maxHeight">Maximum height in pixels.</param>
    public void FitToHeight(float maxHeight)
    {
        if (maxHeight < 8f)
            maxHeight = 8f;

        EnableTransport = false;
        ShowStatus = false;

        const float sidePad = 6f;
        float vertPad = maxHeight <= 32f ? 1f : 2f;
        if (GetThemeStylebox("panel") is StyleBoxFlat panel)
        {
            var compact = (StyleBoxFlat)panel.Duplicate();
            compact.ContentMarginLeft = sidePad;
            compact.ContentMarginRight = sidePad;
            compact.ContentMarginTop = vertPad;
            compact.ContentMarginBottom = vertPad;
            AddThemeStyleboxOverride("panel", compact);
        }

        float inner = Mathf.Max(8f, maxHeight - vertPad * 2f);
        bool showName = inner >= 28f;
        if (_nameLabel != null)
        {
            _nameLabel.Visible = showName;
            _nameLabel.AddThemeFontSizeOverride("font_size", inner >= 40f ? 9 : 8);
        }

        if (_timeLabel != null)
        {
            _timeLabel.CustomMinimumSize = Vector2.Zero;
            float timeBudget = showName ? inner - 12f : inner;
            int timeSize = (int)Mathf.Clamp(Mathf.Floor(timeBudget * 0.78f), 11f, 24f);
            _timeLabel.AddThemeFontSizeOverride("font_size", timeSize);
        }

        CustomMinimumSize = new Vector2(CustomMinimumSize.X, 0);
    }

    public override void _Ready()
    {
        _timecodeManager = GetNodeOrNull<TimecodeManager>("/root/TimecodeManager");
        _nameLabel = GetNodeOrNull<Label>("%NameLabel");
        _timeLabel = GetNodeOrNull<Label>("%TimeLabel");
        _statusLabel = GetNodeOrNull<Label>("%StatusLabel");
        _transportRow = GetNodeOrNull<HBoxContainer>("%TransportRow");
        _playButton = GetNodeOrNull<Button>("%PlayButton");
        _pauseButton = GetNodeOrNull<Button>("%PauseButton");
        _resetButton = GetNodeOrNull<Button>("%ResetButton");

        if (_nameLabel != null)
            _nameLabel.SetMeta(UiLocalizer.MetaSkip, true);
        if (_timeLabel != null)
            _timeLabel.SetMeta(UiLocalizer.MetaSkip, true);
        if (_statusLabel != null)
            _statusLabel.SetMeta(UiLocalizer.MetaSkip, true);

        if (_playButton != null)
            _playButton.Pressed += OnPlayPressed;
        if (_pauseButton != null)
            _pauseButton.Pressed += OnPausePressed;
        if (_resetButton != null)
            _resetButton.Pressed += OnResetPressed;

        VisibilityChanged += OnVisibilityChanged;
        var signals = GetNodeOrNull<GlobalSignals>("/root/GlobalSignals");
        if (signals != null)
            signals.ThemeColorsChanged += OnThemeColorsChanged;
        SetProcess(Visible);
        if (_statusLabel != null)
            _statusLabel.Visible = _showStatus;
        RefreshTransportVisibility();
        ApplyChrome();
    }

    public override void _ExitTree()
    {
        VisibilityChanged -= OnVisibilityChanged;
        var signals = GetNodeOrNull<GlobalSignals>("/root/GlobalSignals");
        if (signals != null)
            signals.ThemeColorsChanged -= OnThemeColorsChanged;
        if (_playButton != null)
            _playButton.Pressed -= OnPlayPressed;
        if (_pauseButton != null)
            _pauseButton.Pressed -= OnPausePressed;
        if (_resetButton != null)
            _resetButton.Pressed -= OnResetPressed;
        base._ExitTree();
    }

    public override void _Process(double delta)
    {
        if (!Visible || _endpointId < 0 || _timecodeManager == null)
            return;
        RefreshFromManager();
    }

    /// <summary>
    /// Binds this card to a live session endpoint (input or output).
    /// </summary>
    /// <param name="endpointId">Endpoint id.</param>
    public void Bind(int endpointId)
    {
        _endpointId = endpointId;
        RefreshFromManager();
    }

    /// <summary>
    /// Pushes a one-shot preview (no live manager bind).
    /// </summary>
    public void SetPreview(string name, TimecodeValue value, TimecodeDirection direction)
    {
        _endpointId = -1;
        _displayName = name ?? string.Empty;
        _value = value;
        _direction = direction;
        ApplyChrome();
    }

    private void OnVisibilityChanged()
    {
        SetProcess(Visible);
        if (Visible)
            RefreshFromManager();
    }

    private void RefreshFromManager()
    {
        if (_timecodeManager == null || _endpointId < 0)
            return;

        var endpoint = _timecodeManager.FindEndpoint(_endpointId);
        if (endpoint == null)
            return;

        _displayName = endpoint.Name ?? string.Empty;
        _direction = endpoint.Direction;
        _value = _timecodeManager.GetValue(_endpointId);
        ApplyChrome();
    }

    private void ApplyChrome()
    {
        bool running = _value.IsRunning || _value.IsLocked;
        Color color = ResolveDigitColor(_direction, running);

        if (_nameLabel != null)
        {
            _nameLabel.Text = string.IsNullOrWhiteSpace(_displayName) ? "Timecode" : _displayName;
            _nameLabel.AddThemeColorOverride("font_color", color.Darkened(0.15f));
        }

        if (_timeLabel != null)
        {
            _timeLabel.Text = _value.ToDisplayString();
            _timeLabel.AddThemeColorOverride("font_color", color);
        }

        if (_statusLabel != null)
        {
            string status;
            if (_timecodeManager != null && !_timecodeManager.TimecodeEnabled)
                status = UiLocalizer.T("Off");
            else if (_value.IsLocked)
                status = UiLocalizer.T("Locked");
            else if (_value.IsRunning)
                status = UiLocalizer.T("Running");
            else if (_direction == TimecodeDirection.Output)
                status = UiLocalizer.T("Stopped");
            else
                status = UiLocalizer.T("Idle");
            _statusLabel.Text = status;
            _statusLabel.AddThemeColorOverride("font_color", color.Darkened(0.25f));
        }

        RefreshTransportVisibility();
    }

    private void RefreshTransportVisibility()
    {
        if (_transportRow == null) return;
        bool show = _enableTransport
                    && _direction == TimecodeDirection.Output
                    && _endpointId >= 0;
        if (show && _timecodeManager != null)
        {
            var endpoint = _timecodeManager.FindEndpoint(_endpointId);
            show = endpoint != null && endpoint.Kind.IsControllableGenerator();
        }

        _transportRow.Visible = show;
    }

    private static Color ResolveDigitColor(TimecodeDirection direction, bool active)
    {
        if (direction == TimecodeDirection.Input)
            return active ? GlobalStyles.LowColor1 : GlobalStyles.LowColor3;
        return active ? GlobalStyles.HighColor1 : GlobalStyles.HighColor3;
    }

    private void OnThemeColorsChanged()
    {
        if (!GodotObject.IsInstanceValid(this))
            return;
        ApplyChrome();
    }

    private void OnPlayPressed()
    {
        if (_endpointId >= 0)
            _timecodeManager?.Start(_endpointId);
    }

    private void OnPausePressed()
    {
        if (_endpointId >= 0)
            _timecodeManager?.Pause(_endpointId);
    }

    private void OnResetPressed()
    {
        if (_endpointId >= 0)
            _timecodeManager?.Reset(_endpointId);
    }
}
