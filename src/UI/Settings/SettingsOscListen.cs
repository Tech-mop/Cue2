// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System;
using System.Net.NetworkInformation;
using System.Text;
using Cue2.Domain.Connections;
using Cue2.Services;
using Godot;
using Cue2.UI.Utilities;

namespace Cue2.UI.Settings;

/// <summary>
/// Settings panel for OSC receive: enable/port/session (top) and live monitor log (bottom).
/// Layout mirrors <see cref="SettingsMidi"/>.
/// </summary>
public partial class SettingsOscListen : ScrollContainer
{
    private GlobalSignals _globalSignals;
    private GlobalData _globalData;
    private HistoryManager _historyManager;
    private OscListen _oscListen;

    private CheckButton _enabledCheckButton;
    private Label _ipAddressLabel;
    private Label _statusLabel;
    private LineEdit _sessionNameLineEdit;
    private LineEdit _portLineEdit;

    private CheckBox _listenCheckBox;
    private Button _clearLogButton;
    private CodeEdit _monitorLog;

    private CheckBox _replyEnabledCheck;
    private CheckBox _pushFeedbackCheck;

    private CheckBox _tcpEnabledCheck;
    private LineEdit _tcpPortLineEdit;
    private bool _tcpPortEditing;
    private LineEdit _allowlistLineEdit;
    private Button _allowlistApplyButton;

    private bool _isSyncingUi;
    private bool _portEditing;
    private bool _sessionNameEditing;
    private readonly StringBuilder _logBuilder = new();
    private int _logLineCount;

    private const int MaxUiLogLines = OscListen.MaxMonitorLines;

    public override void _Ready()
    {
        _globalSignals = GetNodeOrNull<GlobalSignals>("/root/GlobalSignals");
        _globalData = GetNodeOrNull<GlobalData>("/root/GlobalData");
        _historyManager = _globalData?.HistoryManager;
        _oscListen = GetNodeOrNull<OscListen>("/root/OscListen");

        _enabledCheckButton = GetNodeOrNull<CheckButton>("%EnabledCheckButton");
        _ipAddressLabel = GetNodeOrNull<Label>("%IpAddressLabel");
        _statusLabel = GetNodeOrNull<Label>("%StatusLabel");
        _sessionNameLineEdit = GetNodeOrNull<LineEdit>("%SessionNameLineEdit");
        _portLineEdit = GetNodeOrNull<LineEdit>("%PortLineEdit");

        _listenCheckBox = GetNodeOrNull<CheckBox>("%ListenCheckBox");
        _clearLogButton = GetNodeOrNull<Button>("%ClearLogButton");
        _monitorLog = GetNodeOrNull<CodeEdit>("%MonitorLog");

        _replyEnabledCheck = GetNodeOrNull<CheckBox>("%ReplyEnabledCheck");
        _pushFeedbackCheck = GetNodeOrNull<CheckBox>("%PushFeedbackCheck");
        _tcpEnabledCheck = GetNodeOrNull<CheckBox>("%TcpEnabledCheck");
        _tcpPortLineEdit = GetNodeOrNull<LineEdit>("%TcpPortLineEdit");
        _allowlistLineEdit = GetNodeOrNull<LineEdit>("%AllowlistLineEdit");
        _allowlistApplyButton = GetNodeOrNull<Button>("%AllowlistApplyButton");

        // Fixed built-in command catalog (read-only; generated from OscListen.BuiltInCommandCatalog).
        PopulateBuiltInCommandList();

        ConfigureMonitorLogStyle();

        if (_enabledCheckButton != null)
            _enabledCheckButton.Toggled += OnEnabledToggled;
        if (_portLineEdit != null)
        {
            _portLineEdit.EditingToggled += OnPortEditingToggled;
            _portLineEdit.TextSubmitted += OnPortTextSubmitted;
        }
        if (_sessionNameLineEdit != null)
        {
            _sessionNameLineEdit.EditingToggled += OnSessionNameEditingToggled;
            _sessionNameLineEdit.TextSubmitted += OnSessionNameTextSubmitted;
        }
        if (_listenCheckBox != null)
            _listenCheckBox.Toggled += OnListenToggled;
        if (_clearLogButton != null)
            _clearLogButton.Pressed += OnClearLogPressed;
        if (_replyEnabledCheck != null)
            _replyEnabledCheck.Toggled += OnReplyEnabledToggled;
        if (_pushFeedbackCheck != null)
            _pushFeedbackCheck.Toggled += OnPushFeedbackToggled;
        if (_tcpEnabledCheck != null)
            _tcpEnabledCheck.Toggled += OnTcpEnabledToggled;
        if (_tcpPortLineEdit != null)
        {
            _tcpPortLineEdit.EditingToggled += OnTcpPortEditingToggled;
            _tcpPortLineEdit.TextSubmitted += OnTcpPortTextSubmitted;
        }
        if (_allowlistApplyButton != null)
            _allowlistApplyButton.Pressed += OnAllowlistApplyPressed;
        if (_allowlistLineEdit != null)
            UiUtilities.BindLineEditCommit(_allowlistLineEdit, OnAllowlistTextSubmitted);

        if (_oscListen != null)
        {
            _oscListen.OscStateChanged += OnOscStateChanged;
            _oscListen.OscMonitorLine += OnOscMonitorLine;
        }

        if (_historyManager != null)
            _historyManager.HistoryRestored += OnHistoryRestored;
        if (_globalSignals != null)
            _globalSignals.NewSession += OnNewSession;

        VisibilityChanged += OnVisibilityChanged;

        DisplayIpAddresses();
        SyncFromModel();
    
        UiLocalizer.LocalizeTree(this);
        if (_globalSignals != null)
            _globalSignals.LocaleChanged += OnLocaleChanged;
}

    public override void _ExitTree()
    {
        if (_globalSignals != null)
            _globalSignals.LocaleChanged -= OnLocaleChanged;

        VisibilityChanged -= OnVisibilityChanged;

        if (_historyManager != null)
            _historyManager.HistoryRestored -= OnHistoryRestored;
        if (_globalSignals != null)
            _globalSignals.NewSession -= OnNewSession;

        if (_oscListen != null)
        {
            _oscListen.OscStateChanged -= OnOscStateChanged;
            _oscListen.OscMonitorLine -= OnOscMonitorLine;
        }

        if (_enabledCheckButton != null)
            _enabledCheckButton.Toggled -= OnEnabledToggled;
        if (_portLineEdit != null)
        {
            _portLineEdit.EditingToggled -= OnPortEditingToggled;
            _portLineEdit.TextSubmitted -= OnPortTextSubmitted;
        }
        if (_sessionNameLineEdit != null)
        {
            _sessionNameLineEdit.EditingToggled -= OnSessionNameEditingToggled;
            _sessionNameLineEdit.TextSubmitted -= OnSessionNameTextSubmitted;
        }
        if (_listenCheckBox != null)
            _listenCheckBox.Toggled -= OnListenToggled;
        if (_clearLogButton != null)
            _clearLogButton.Pressed -= OnClearLogPressed;
        if (_replyEnabledCheck != null)
            _replyEnabledCheck.Toggled -= OnReplyEnabledToggled;
        if (_pushFeedbackCheck != null)
            _pushFeedbackCheck.Toggled -= OnPushFeedbackToggled;
        if (_tcpEnabledCheck != null)
            _tcpEnabledCheck.Toggled -= OnTcpEnabledToggled;
        if (_tcpPortLineEdit != null)
        {
            _tcpPortLineEdit.EditingToggled -= OnTcpPortEditingToggled;
            _tcpPortLineEdit.TextSubmitted -= OnTcpPortTextSubmitted;
        }
        if (_allowlistApplyButton != null)
            _allowlistApplyButton.Pressed -= OnAllowlistApplyPressed;
        // Allowlist LineEdit is wired with BindLineEditCommit; hooks die with the node.

        base._ExitTree();
    }

    private void OnVisibilityChanged()
    {
        if (!Visible) return;
        DisplayIpAddresses();
        SyncFromModel();
        // Buffered lines keep accumulating while hidden; push them into the CodeEdit now.
        RefreshMonitorLogUi(stickToBottom: true);
    }

    private void OnOscStateChanged()
    {
        if (_historyManager?.IsRestoring == true) return;
        if (Visible)
            SyncFromModel();
    }

    private void OnHistoryRestored(int scope)
    {
        if (!GodotObject.IsInstanceValid(this)) return;
        if (scope != (int)HistoryManager.HistoryScope.Settings) return;
        SyncFromModel();
    }

    private void OnNewSession()
    {
        if (!GodotObject.IsInstanceValid(this)) return;
        OnClearLogPressed();
        SyncFromModel();
    }

    private void RecordHistory(string description)
    {
        if (_historyManager == null || _historyManager.IsRestoring) return;
        _historyManager.RecordSettingsChange(description, null, "OscListen");
    }

    /// <summary>
    /// Builds the read-only built-in command list from <see cref="OscListen.BuiltInCommandCatalog"/>.
    /// Categories stack vertically; commands within each category wrap in a multi-column flow.
    /// </summary>
    private void PopulateBuiltInCommandList()
    {
        var list = GetNodeOrNull<VBoxContainer>("%CommandsList");
        if (list == null) return;

        foreach (Node child in list.GetChildren())
            child.QueueFree();

        list.AddThemeConstantOverride("separation", 6);

        string lastCategory = null;
        FlowContainer flow = null;

        foreach (var cmd in OscListen.BuiltInCommandCatalog)
        {
            if (cmd.Category != lastCategory)
            {
                lastCategory = cmd.Category;

                var cat = new Label
                {
                    Text = UiLocalizer.T(lastCategory),
                    TooltipText = UiLocalizer.Tf("{0} commands", UiLocalizer.T(lastCategory)),
                    SizeFlagsHorizontal = SizeFlags.ExpandFill,
                };
                cat.AddThemeFontSizeOverride("font_size", 11);
                cat.AddThemeColorOverride("font_color", new Color(0.75f, 0.75f, 0.75f, 1f));
                list.AddChild(cat);

                flow = new FlowContainer
                {
                    SizeFlagsHorizontal = SizeFlags.ExpandFill,
                };
                flow.AddThemeConstantOverride("h_separation", 6);
                flow.AddThemeConstantOverride("v_separation", 4);
                list.AddChild(flow);
            }

            if (flow == null) continue;

            // Compact chip: monospaced-looking path with tooltip description.
            var chip = new PanelContainer
            {
                TooltipText = UiLocalizer.T(cmd.Description),
                CustomMinimumSize = new Vector2(148, 0),
                SizeFlagsHorizontal = SizeFlags.ShrinkBegin,
            };

            var chipStyle = new StyleBoxFlat
            {
                BgColor = new Color(0.1f, 0.1f, 0.1f, 1f),
                BorderColor = new Color(0.28f, 0.28f, 0.28f, 1f),
                ContentMarginLeft = 6,
                ContentMarginRight = 6,
                ContentMarginTop = 3,
                ContentMarginBottom = 3,
            };
            chipStyle.SetBorderWidthAll(1);
            chipStyle.SetCornerRadiusAll(3);
            chip.AddThemeStyleboxOverride("panel", chipStyle);

            var pattern = new LineEdit
            {
                Text = cmd.Pattern,
                Editable = false,
                ExpandToTextLength = false,
                SelectAllOnFocus = true,
                TooltipText = UiLocalizer.T(cmd.Description),
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
                Flat = true,
                ContextMenuEnabled = true,
            };
            pattern.AddThemeFontSizeOverride("font_size", 10);
            pattern.AddThemeColorOverride("font_uneditable_color", new Color(0.85f, 0.9f, 0.95f, 1f));
            pattern.AddThemeColorOverride("font_readonly_color", new Color(0.85f, 0.9f, 0.95f, 1f));
            // Transparent so the panel style shows through.
            var clear = new StyleBoxEmpty();
            pattern.AddThemeStyleboxOverride("normal", clear);
            pattern.AddThemeStyleboxOverride("focus", clear);
            pattern.AddThemeStyleboxOverride("read_only", clear);

            chip.AddChild(pattern);
            flow.AddChild(chip);
        }
    }

    private void ConfigureMonitorLogStyle()
    {
        UiUtilities.ConfigureReadOnlyMonitorLog(
            _monitorLog,
            fontColor: new Color(0.75f, 0.95f, 0.75f, 1f));
    }

    private void SyncFromModel()
    {
        if (_oscListen == null) return;

        _isSyncingUi = true;
        try
        {
            if (_enabledCheckButton != null)
                _enabledCheckButton.SetPressedNoSignal(_oscListen.OscListenEnabled);

            if (_portLineEdit != null && !_portEditing)
                _portLineEdit.Text = _oscListen.Port.ToString();

            if (_sessionNameLineEdit != null && !_sessionNameEditing)
                _sessionNameLineEdit.Text = _oscListen.SessionName ?? string.Empty;

            if (_listenCheckBox != null)
                _listenCheckBox.SetPressedNoSignal(_oscListen.MonitorEnabled);

            if (_replyEnabledCheck != null)
                _replyEnabledCheck.SetPressedNoSignal(_oscListen.ReplyEnabled);
            if (_pushFeedbackCheck != null)
                _pushFeedbackCheck.SetPressedNoSignal(_oscListen.PushFeedback);

            if (_tcpEnabledCheck != null)
                _tcpEnabledCheck.SetPressedNoSignal(_oscListen.TcpEnabled);
            if (_tcpPortLineEdit != null && !_tcpPortEditing)
                _tcpPortLineEdit.Text = _oscListen.TcpPort.ToString();
            if (_allowlistLineEdit != null && !_allowlistLineEdit.HasFocus())
            {
                var list = _oscListen.AllowlistIps;
                _allowlistLineEdit.Text = list.Count == 0 ? string.Empty : string.Join(", ", list);
            }

            UpdateStatusLabel();
        }
        finally
        {
            _isSyncingUi = false;
        }
    }

    private void OnTcpEnabledToggled(bool pressed)
    {
        if (_isSyncingUi || _oscListen == null) return;
        if (_historyManager?.IsRestoring == true) return;
        if (_oscListen.TcpEnabled == pressed) return;
        RecordHistory(pressed ? "Enable OSC TCP listen" : "Disable OSC TCP listen");
        _oscListen.TcpEnabled = pressed;
    }

    private void OnTcpPortEditingToggled(bool editing)
    {
        if (editing) { _tcpPortEditing = true; return; }
        if (!_tcpPortEditing) return;
        _tcpPortEditing = false;
        _tcpPortLineEdit?.ReleaseFocus();
        SubmitTcpPort();
    }

    private void OnTcpPortTextSubmitted(string _)
    {
        _tcpPortEditing = false;
        _tcpPortLineEdit?.ReleaseFocus();
        SubmitTcpPort();
    }

    private void SubmitTcpPort()
    {
        if (_oscListen == null || _tcpPortLineEdit == null) return;
        if (_historyManager?.IsRestoring == true) return;
        if (int.TryParse(_tcpPortLineEdit.Text, out int port) && port >= 1 && port <= 65535)
        {
            if (_oscListen.TcpPort == port) return;
            RecordHistory($"Set OSC TCP port to {port}");
            _oscListen.TcpPort = port;
        }
        else
            _tcpPortLineEdit.Text = _oscListen.TcpPort.ToString();
    }

    private void OnAllowlistTextSubmitted(string _)
    {
        // Apply first (while still focused is fine); global LineEdit TextSubmitted also releases focus.
        OnAllowlistApplyPressed();
    }

    private void OnAllowlistApplyPressed()
    {
        if (_isSyncingUi || _oscListen == null || _allowlistLineEdit == null) return;
        if (_historyManager?.IsRestoring == true) return;

        string text = _allowlistLineEdit.Text ?? string.Empty;
        var parts = text.Split(new[] { ',', ';', '\n', '\r', ' ' }, StringSplitOptions.RemoveEmptyEntries);
        var current = _oscListen.AllowlistIps;
        if (current.Count == parts.Length)
        {
            bool same = true;
            for (int i = 0; i < parts.Length; i++)
            {
                if (!string.Equals(current[i], parts[i], StringComparison.Ordinal))
                {
                    same = false;
                    break;
                }
            }
            if (same)
                return;
        }
        RecordHistory("Update OSC IP allowlist");
        _oscListen.SetAllowlist(parts);
        // Refresh normalized display
        var list = _oscListen.AllowlistIps;
        _allowlistLineEdit.Text = list.Count == 0 ? string.Empty : string.Join(", ", list);
        // Enter: unfocus this field. Focus-exit commit must not steal the next LineEdit's focus.
        if (_allowlistLineEdit.HasFocus())
        {
            if (_allowlistLineEdit.IsEditing())
                _allowlistLineEdit.Unedit();
            _allowlistLineEdit.ReleaseFocus();
            _allowlistLineEdit.CallDeferred(Control.MethodName.ReleaseFocus);
        }
        _globalSignals?.EmitSignal(nameof(GlobalSignals.Log),
            list.Count == 0
                ? "OSC allowlist cleared (all IPs allowed)"
                : $"OSC allowlist: {list.Count} IP(s)",
            (int)LogType.Info);
    }

    private void OnReplyEnabledToggled(bool pressed)
    {
        if (_isSyncingUi || _oscListen == null) return;
        if (_historyManager?.IsRestoring == true) return;
        if (_oscListen.ReplyEnabled == pressed) return;
        RecordHistory(pressed ? "Enable OSC reply" : "Disable OSC reply");
        _oscListen.ReplyEnabled = pressed;
    }

    private void OnPushFeedbackToggled(bool pressed)
    {
        if (_isSyncingUi || _oscListen == null) return;
        if (_historyManager?.IsRestoring == true) return;
        if (_oscListen.PushFeedback == pressed) return;
        RecordHistory(pressed ? "Enable OSC playhead push" : "Disable OSC playhead push");
        _oscListen.PushFeedback = pressed;
    }

    private void UpdateStatusLabel()
    {
        if (_statusLabel == null || _oscListen == null) return;

        if (!_oscListen.OscListenEnabled)
        {
            _statusLabel.Text = UiLocalizer.T("Listener off");
            return;
        }

        if (!_oscListen.IsListening)
        {
            _statusLabel.Text = UiLocalizer.T("Enabled · not bound");
            return;
        }

        string tcp = _oscListen.TcpEnabled ? $" · TCP {_oscListen.TcpPort}" : "";
        int n = _oscListen.AllowlistIps.Count;
        string allow = n == 0 ? "" : $" · allow {n}";
        string prefix = _oscListen.HasRequiredSessionPrefix
            ? $" · /{_oscListen.SessionName}/"
            : "";
        _statusLabel.Text = $"UDP {_oscListen.Port}{tcp}{prefix}{allow}";
    }

    private void OnEnabledToggled(bool pressed)
    {
        if (_isSyncingUi || _oscListen == null) return;
        if (_historyManager?.IsRestoring == true) return;
        if (_oscListen.OscListenEnabled == pressed) return;

        RecordHistory(pressed ? "Enable OSC Listener" : "Disable OSC Listener");
        _oscListen.OscListenEnabled = pressed;
        UpdateStatusLabel();
    }

    private void OnListenToggled(bool pressed)
    {
        if (_isSyncingUi || _oscListen == null) return;
        if (_historyManager?.IsRestoring == true) return;
        if (_oscListen.MonitorEnabled == pressed) return;

        RecordHistory(pressed ? "Enable OSC receive monitor" : "Disable OSC receive monitor");
        _oscListen.MonitorEnabled = pressed;
    }

    private void OnPortEditingToggled(bool editing)
    {
        if (editing)
        {
            _portEditing = true;
            return;
        }

        if (!_portEditing) return;
        _portEditing = false;
        _portLineEdit?.ReleaseFocus();
        SubmitPort();
    }

    private void OnPortTextSubmitted(string _)
    {
        _portEditing = false;
        _portLineEdit?.ReleaseFocus();
        SubmitPort();
    }

    private void SubmitPort()
    {
        if (_oscListen == null || _portLineEdit == null) return;
        if (_historyManager?.IsRestoring == true) return;

        if (int.TryParse(_portLineEdit.Text, out int port) && port >= 1 && port <= 65535)
        {
            if (_oscListen.Port == port) return;
            RecordHistory($"Set OSC listen port to {port}");
            _oscListen.Port = port;
            _globalSignals?.EmitSignal(nameof(GlobalSignals.Log),
                $"OSC Listener: port set to {port}", (int)LogType.Info);
        }
        else
        {
            _globalSignals?.EmitSignal(nameof(GlobalSignals.Log),
                "OSC Listener: invalid port (1–65535).", (int)LogType.Warning);
            _portLineEdit.Text = _oscListen.Port.ToString();
        }
    }

    private void OnSessionNameEditingToggled(bool editing)
    {
        if (editing)
        {
            _sessionNameEditing = true;
            return;
        }

        if (!_sessionNameEditing) return;
        _sessionNameEditing = false;
        _sessionNameLineEdit?.ReleaseFocus();
        SubmitSessionName();
    }

    private void OnSessionNameTextSubmitted(string _)
    {
        _sessionNameEditing = false;
        _sessionNameLineEdit?.ReleaseFocus();
        SubmitSessionName();
    }

    private void SubmitSessionName()
    {
        if (_oscListen == null || _sessionNameLineEdit == null) return;
        if (_historyManager?.IsRestoring == true) return;

        string sessionName = _sessionNameLineEdit.Text.Trim();

        // Empty = no prefix required. Non-empty = all OSC must use /{SessionName}/…
        if (!string.IsNullOrEmpty(sessionName))
        {
            if (sessionName.Length > 20)
            {
                _globalSignals?.EmitSignal(nameof(GlobalSignals.Log),
                    "OSC Listener: session name max 20 characters.", (int)LogType.Warning);
                _sessionNameLineEdit.Text = _oscListen.SessionName;
                return;
            }
            if (!System.Text.RegularExpressions.Regex.IsMatch(sessionName, @"^[a-zA-Z0-9_]+$"))
            {
                _globalSignals?.EmitSignal(nameof(GlobalSignals.Log),
                    "OSC Listener: session name may only use letters, numbers, and underscores.",
                    (int)LogType.Warning);
                _sessionNameLineEdit.Text = _oscListen.SessionName;
                return;
            }
        }

        if (_oscListen.SessionName == sessionName) return;
        RecordHistory("Set OSC session name");
        _oscListen.SessionName = sessionName;
        if (!string.IsNullOrEmpty(sessionName))
        {
            _globalSignals?.EmitSignal(nameof(GlobalSignals.Log),
                $"OSC session prefix required: /{sessionName}/… (e.g. /{sessionName}/Go)",
                (int)LogType.Info);
        }
        else
        {
            _globalSignals?.EmitSignal(nameof(GlobalSignals.Log),
                "OSC session prefix cleared — unprefixed paths accepted",
                (int)LogType.Info);
        }
    }

    private void OnClearLogPressed()
    {
        _logBuilder.Clear();
        _logLineCount = 0;
        if (_monitorLog != null)
            _monitorLog.Text = string.Empty;
        _oscListen?.ClearPendingMonitorLines();
    }

    private void OnOscMonitorLine(string line)
    {
        if (string.IsNullOrEmpty(line)) return;

        if (_logLineCount >= MaxUiLogLines)
        {
            string current = _logBuilder.ToString();
            int cut = 0;
            int drops = MaxUiLogLines / 4;
            for (int i = 0; i < drops; i++)
            {
                int next = current.IndexOf('\n', cut);
                if (next < 0)
                {
                    cut = current.Length;
                    break;
                }
                cut = next + 1;
            }
            _logBuilder.Clear();
            if (cut < current.Length)
                _logBuilder.Append(current, cut, current.Length - cut);
            _logLineCount = Math.Max(0, _logLineCount - drops);
        }

        if (_logBuilder.Length > 0)
            _logBuilder.Append('\n');
        _logBuilder.Append(line);
        _logLineCount++;

        if (!Visible) return;

        bool stickToBottom = true;
        if (_monitorLog != null && _monitorLog.GetLineCount() > 0)
        {
            int lastVisible = _monitorLog.GetLastFullVisibleLine();
            stickToBottom = lastVisible >= _monitorLog.GetLineCount() - 2;
        }

        RefreshMonitorLogUi(stickToBottom);
    }

    /// <summary>
    /// Pushes the in-memory monitor buffer into the CodeEdit (e.g. when the panel opens).
    /// </summary>
    private void RefreshMonitorLogUi(bool stickToBottom)
    {
        if (_monitorLog == null) return;
        _monitorLog.Text = _logBuilder.ToString();
        if (stickToBottom && _monitorLog.GetLineCount() > 0)
            _monitorLog.SetCaretLine(_monitorLog.GetLineCount());
    }

    private void DisplayIpAddresses()
    {
        if (_ipAddressLabel == null) return;

        var ipAddresses = new System.Collections.Generic.List<string>();
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                foreach (var ip in ni.GetIPProperties().UnicastAddresses)
                {
                    if (ip.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                        ipAddresses.Add(ip.Address.ToString());
                }
            }
        }
        catch (Exception ex)
        {
            GD.PrintErr($"SettingsOscListen:DisplayIpAddresses - {ex.Message}");
        }

        _ipAddressLabel.Text = ipAddresses.Count > 0
            ? string.Join("  ·  ", ipAddresses)
            : "(none found)";
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
