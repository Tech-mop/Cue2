// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System;
using System.Text;
using Cue2.Services;
using Godot;
using Cue2.UI.Utilities;

namespace Cue2.UI.Settings;

/// <summary>
/// Settings panel for MIDI: multi-device session inputs (top) and live monitor log (bottom).
/// </summary>
public partial class SettingsMidi : ScrollContainer
{
    private GlobalSignals _globalSignals;
    private GlobalData _globalData;
    private HistoryManager _historyManager;
    private MidiManager _midiManager;

    private CheckButton _enableMidiCheck;
    private VBoxContainer _sessionDevicesList;
    private OptionButton _addDeviceOption;
    private Button _refreshDevicesButton;
    private VBoxContainer _sessionOutputsList;
    private OptionButton _addOutputOption;
    private Button _panicButton;
    private Label _statusLabel;

    private CheckBox _listenCheckBox;
    private Button _clearLogButton;
    private CodeEdit _monitorLog;

    private bool _isSyncingUi;
    private readonly StringBuilder _logBuilder = new();
    private int _logLineCount;

    private const int MaxUiLogLines = MidiManager.MaxMonitorLines;

    public override void _Ready()
    {
        _globalSignals = GetNodeOrNull<GlobalSignals>("/root/GlobalSignals");
        _globalData = GetNodeOrNull<GlobalData>("/root/GlobalData");
        _historyManager = _globalData?.HistoryManager;
        _midiManager = GetNodeOrNull<MidiManager>("/root/MidiManager");

        _enableMidiCheck = GetNodeOrNull<CheckButton>("%EnableMidiCheck");
        _sessionDevicesList = GetNodeOrNull<VBoxContainer>("%SessionDevicesList");
        _addDeviceOption = GetNodeOrNull<OptionButton>("%AddDeviceOption");
        _refreshDevicesButton = GetNodeOrNull<Button>("%RefreshDevicesButton");
        _sessionOutputsList = GetNodeOrNull<VBoxContainer>("%SessionOutputsList");
        _addOutputOption = GetNodeOrNull<OptionButton>("%AddOutputOption");
        _panicButton = GetNodeOrNull<Button>("%PanicButton");
        _statusLabel = GetNodeOrNull<Label>("%StatusLabel");

        _listenCheckBox = GetNodeOrNull<CheckBox>("%ListenCheckBox");
        _clearLogButton = GetNodeOrNull<Button>("%ClearLogButton");
        _monitorLog = GetNodeOrNull<CodeEdit>("%MonitorLog");

        ConfigureMonitorLogStyle();

        if (_enableMidiCheck != null)
            _enableMidiCheck.Toggled += OnEnableMidiToggled;
        if (_addDeviceOption != null)
            _addDeviceOption.ItemSelected += OnAddDeviceSelected;
        if (_refreshDevicesButton != null)
            _refreshDevicesButton.Pressed += OnRefreshDevicesPressed;
        if (_addOutputOption != null)
            _addOutputOption.ItemSelected += OnAddOutputSelected;
        if (_panicButton != null)
            _panicButton.Pressed += OnPanicPressed;
        if (_listenCheckBox != null)
            _listenCheckBox.Toggled += OnListenToggled;
        if (_clearLogButton != null)
            _clearLogButton.Pressed += OnClearLogPressed;

        if (_midiManager != null)
        {
            _midiManager.MidiStateChanged += OnMidiStateChanged;
            _midiManager.MidiMonitorLine += OnMidiMonitorLine;
        }

        if (_historyManager != null)
            _historyManager.HistoryRestored += OnHistoryRestored;
        if (_globalSignals != null)
            _globalSignals.NewSession += OnNewSession;

        VisibilityChanged += OnVisibilityChanged;

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

        if (_midiManager != null)
        {
            _midiManager.MidiStateChanged -= OnMidiStateChanged;
            _midiManager.MidiMonitorLine -= OnMidiMonitorLine;
        }

        if (_enableMidiCheck != null)
            _enableMidiCheck.Toggled -= OnEnableMidiToggled;
        if (_addDeviceOption != null)
            _addDeviceOption.ItemSelected -= OnAddDeviceSelected;
        if (_refreshDevicesButton != null)
            _refreshDevicesButton.Pressed -= OnRefreshDevicesPressed;
        if (_addOutputOption != null)
            _addOutputOption.ItemSelected -= OnAddOutputSelected;
        if (_panicButton != null)
            _panicButton.Pressed -= OnPanicPressed;
        if (_listenCheckBox != null)
            _listenCheckBox.Toggled -= OnListenToggled;
        if (_clearLogButton != null)
            _clearLogButton.Pressed -= OnClearLogPressed;

        base._ExitTree();
    }

    private void OnVisibilityChanged()
    {
        if (!Visible) return;
        SyncFromModel();
        // Buffered lines keep accumulating while hidden; push them into the CodeEdit now.
        RefreshMonitorLogUi(stickToBottom: true);
    }

    private void OnMidiStateChanged()
    {
        // Skip mid-history restore — OnHistoryRestored performs a coordinated refresh.
        if (_historyManager?.IsRestoring == true) return;
        if (Visible)
            SyncFromModel();
    }

    /// <summary>
    /// After undo/redo of a settings-scoped entry, re-sync this panel when relevant.
    /// </summary>
    private void OnHistoryRestored(int scope)
    {
        if (!GodotObject.IsInstanceValid(this)) return;
        if (scope != (int)HistoryManager.HistoryScope.Settings) return;
        SyncFromModel();
    }

    private void OnNewSession()
    {
        if (!GodotObject.IsInstanceValid(this)) return;
        SyncFromModel();
    }

    /// <summary>
    /// Snapshots the MIDI settings slice before a user mutation (session undo).
    /// </summary>
    private void RecordMidiHistory(string description)
    {
        if (_historyManager == null || _historyManager.IsRestoring) return;
        _historyManager.RecordSettingsChange(description, null, "Midi");
    }

    /// <summary>
    /// Styles the monitor <see cref="CodeEdit"/> as a dark monospace console that cannot steal focus.
    /// </summary>
    private void ConfigureMonitorLogStyle()
    {
        UiUtilities.ConfigureReadOnlyMonitorLog(
            _monitorLog,
            fontColor: new Color(0.75f, 0.95f, 0.75f, 1f));
    }

    /// <summary>
    /// Pushes <see cref="MidiManager"/> state into the form controls.
    /// </summary>
    private void SyncFromModel()
    {
        if (_midiManager == null) return;

        _isSyncingUi = true;
        try
        {
            if (_enableMidiCheck != null)
                _enableMidiCheck.SetPressedNoSignal(_midiManager.MidiEnabled);

            if (_listenCheckBox != null)
                _listenCheckBox.SetPressedNoSignal(_midiManager.MonitorEnabled);

            RebuildSessionDeviceRows();
            PopulateAddDeviceOption();
            RebuildSessionOutputRows();
            PopulateAddOutputOption();
            UpdateStatusLabel();
        }
        finally
        {
            _isSyncingUi = false;
        }
    }

    /// <summary>
    /// Rebuilds the session device list rows (name + status + remove).
    /// </summary>
    private void RebuildSessionDeviceRows()
    {
        if (_sessionDevicesList == null || _midiManager == null) return;

        foreach (Node child in _sessionDevicesList.GetChildren())
            child.QueueFree();

        var session = _midiManager.SessionInputNames;
        if (session.Count == 0)
        {
            var empty = new Label
            {
                Text = "No inputs in session — add one below.",
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
            };
            empty.AddThemeFontSizeOverride("font_size", 11);
            empty.AddThemeColorOverride("font_color", new Color(0.55f, 0.55f, 0.55f, 1f));
            _sessionDevicesList.AddChild(empty);
            return;
        }

        foreach (string name in session)
        {
            bool available = _midiManager.IsDeviceAvailable(name);
            bool open = _midiManager.IsDeviceOpen(name);

            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 6);

            var nameLabel = new Label
            {
                Text = name,
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
                ClipText = true,
                TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
            };
            nameLabel.AddThemeFontSizeOverride("font_size", 12);
            row.AddChild(nameLabel);

            string statusText;
            Color statusColor;
            if (!_midiManager.MidiEnabled)
            {
                statusText = available ? "Ready" : "Offline";
                statusColor = available
                    ? new Color(0.6f, 0.6f, 0.6f, 1f)
                    : new Color(0.85f, 0.55f, 0.35f, 1f);
            }
            else if (open)
            {
                statusText = "Open";
                statusColor = new Color(0.45f, 0.85f, 0.5f, 1f);
            }
            else if (available)
            {
                statusText = "Closed";
                statusColor = new Color(0.85f, 0.7f, 0.35f, 1f);
            }
            else
            {
                statusText = "Offline";
                statusColor = new Color(0.85f, 0.45f, 0.4f, 1f);
            }

            var status = new Label
            {
                Text = statusText,
                HorizontalAlignment = HorizontalAlignment.Right,
            };
            status.AddThemeFontSizeOverride("font_size", 11);
            status.AddThemeColorOverride("font_color", statusColor);
            status.CustomMinimumSize = new Vector2(56, 0);
            row.AddChild(status);

            var removeBtn = new Button
            {
                Text = "Remove",
                FocusMode = FocusModeEnum.None,
                TooltipText = UiLocalizer.Tf("Remove '{0}' from this session", name),
            };
            removeBtn.AddThemeFontSizeOverride("font_size", 11);
            string captured = name;
            removeBtn.Pressed += () => OnRemoveDevicePressed(captured);
            row.AddChild(removeBtn);

            _sessionDevicesList.AddChild(row);
        }
    }

    /// <summary>
    /// Fills the Add dropdown with available devices not already in the session.
    /// First item is a placeholder; selecting a real device adds it.
    /// </summary>
    private void PopulateAddDeviceOption()
    {
        if (_addDeviceOption == null || _midiManager == null) return;

        _addDeviceOption.Clear();
        _addDeviceOption.AddItem(UiLocalizer.T("Select device to add…"));
        _addDeviceOption.SetItemMetadata(0, "");
        _addDeviceOption.SetItemDisabled(0, true);

        var available = _midiManager.AvailableInputsNotInSession;
        if (available.Count == 0)
        {
            int idx = _addDeviceOption.ItemCount;
            string label = _midiManager.AvailableInputNames.Count == 0
                ? "(No MIDI inputs found)"
                : "(All available devices added)";
            _addDeviceOption.AddItem(label);
            _addDeviceOption.SetItemMetadata(idx, "");
            _addDeviceOption.SetItemDisabled(idx, true);
        }
        else
        {
            foreach (string name in available)
            {
                int idx = _addDeviceOption.ItemCount;
                _addDeviceOption.AddItem(name);
                _addDeviceOption.SetItemMetadata(idx, name);
            }
        }

        _addDeviceOption.Select(0);
    }

    private void UpdateStatusLabel()
    {
        if (_statusLabel == null || _midiManager == null) return;

        if (!_midiManager.IsNativeReady)
        {
            _statusLabel.Text = UiLocalizer.T("Native library missing");
            return;
        }

        if (!_midiManager.MidiEnabled)
        {
            int nin = _midiManager.SessionInputNames.Count;
            int nout = _midiManager.SessionOutputNames.Count;
            _statusLabel.Text = (nin + nout) == 0
                ? "MIDI off"
                : $"MIDI off · {nin} in / {nout} out";
            return;
        }

        int sessionIn = _midiManager.SessionInputNames.Count;
        int openIn = _midiManager.OpenInputCount;
        int sessionOut = _midiManager.SessionOutputNames.Count;
        int openOut = _midiManager.OpenOutputCount;
        _statusLabel.Text = $"In {openIn}/{sessionIn} · Out {openOut}/{sessionOut}";
    }

    private void OnEnableMidiToggled(bool pressed)
    {
        if (_isSyncingUi || _midiManager == null) return;
        if (_historyManager?.IsRestoring == true) return;
        if (_midiManager.MidiEnabled == pressed) return;

        RecordMidiHistory(pressed ? "Enable MIDI" : "Disable MIDI");
        _midiManager.MidiEnabled = pressed;
        UpdateStatusLabel();
    }

    private void OnListenToggled(bool pressed)
    {
        if (_isSyncingUi || _midiManager == null) return;
        if (_historyManager?.IsRestoring == true) return;
        if (_midiManager.MonitorEnabled == pressed) return;

        RecordMidiHistory(pressed ? "Enable MIDI monitor" : "Disable MIDI monitor");
        _midiManager.MonitorEnabled = pressed;
    }

    /// <summary>
    /// Selecting a real device in the Add dropdown adds it to the session.
    /// </summary>
    private void OnAddDeviceSelected(long index)
    {
        if (_isSyncingUi || _midiManager == null || _addDeviceOption == null) return;
        if (_historyManager?.IsRestoring == true) return;
        if (index <= 0 || index >= _addDeviceOption.ItemCount) return;

        string name = _addDeviceOption.GetItemMetadata((int)index).AsString();
        if (string.IsNullOrEmpty(name)) return;

        RecordMidiHistory($"Add MIDI input '{name}'");
        _midiManager.AddInputDevice(name);
        // SyncFromModel via MidiStateChanged rebuilds list and resets dropdown.
    }

    private void OnRemoveDevicePressed(string deviceName)
    {
        if (_midiManager == null || string.IsNullOrEmpty(deviceName)) return;
        if (_historyManager?.IsRestoring == true) return;

        RecordMidiHistory($"Remove MIDI input '{deviceName}'");
        _midiManager.RemoveInputDevice(deviceName);
    }

    private void RebuildSessionOutputRows()
    {
        if (_sessionOutputsList == null || _midiManager == null) return;

        foreach (Node child in _sessionOutputsList.GetChildren())
            child.QueueFree();

        var session = _midiManager.SessionOutputNames;
        if (session.Count == 0)
        {
            var empty = new Label
            {
                Text = "No outputs in session — add one below.",
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
            };
            empty.AddThemeFontSizeOverride("font_size", 11);
            empty.AddThemeColorOverride("font_color", new Color(0.55f, 0.55f, 0.55f, 1f));
            _sessionOutputsList.AddChild(empty);
            return;
        }

        foreach (string name in session)
        {
            bool available = _midiManager.IsOutputAvailable(name);
            bool open = _midiManager.IsOutputOpen(name);

            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 6);

            var nameLabel = new Label
            {
                Text = name,
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
                ClipText = true,
                TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
            };
            nameLabel.AddThemeFontSizeOverride("font_size", 12);
            row.AddChild(nameLabel);

            string statusText;
            Color statusColor;
            if (!_midiManager.MidiEnabled)
            {
                statusText = available ? "Ready" : "Offline";
                statusColor = available
                    ? new Color(0.6f, 0.6f, 0.6f, 1f)
                    : new Color(0.85f, 0.55f, 0.35f, 1f);
            }
            else if (open)
            {
                statusText = "Open";
                statusColor = new Color(0.45f, 0.85f, 0.5f, 1f);
            }
            else if (available)
            {
                statusText = "Closed";
                statusColor = new Color(0.85f, 0.7f, 0.35f, 1f);
            }
            else
            {
                statusText = "Offline";
                statusColor = new Color(0.85f, 0.45f, 0.4f, 1f);
            }

            var status = new Label
            {
                Text = statusText,
                HorizontalAlignment = HorizontalAlignment.Right,
            };
            status.AddThemeFontSizeOverride("font_size", 11);
            status.AddThemeColorOverride("font_color", statusColor);
            status.CustomMinimumSize = new Vector2(56, 0);
            row.AddChild(status);

            var removeBtn = new Button
            {
                Text = "Remove",
                FocusMode = FocusModeEnum.None,
                TooltipText = UiLocalizer.Tf("Remove '{0}' from session outputs", name),
            };
            removeBtn.AddThemeFontSizeOverride("font_size", 11);
            string captured = name;
            removeBtn.Pressed += () => OnRemoveOutputPressed(captured);
            row.AddChild(removeBtn);

            _sessionOutputsList.AddChild(row);
        }
    }

    private void PopulateAddOutputOption()
    {
        if (_addOutputOption == null || _midiManager == null) return;

        _addOutputOption.Clear();
        _addOutputOption.AddItem(UiLocalizer.T("Select output to add…"));
        _addOutputOption.SetItemMetadata(0, "");
        _addOutputOption.SetItemDisabled(0, true);

        var available = _midiManager.AvailableOutputsNotInSession;
        if (available.Count == 0)
        {
            int idx = _addOutputOption.ItemCount;
            string label = _midiManager.AvailableOutputNames.Count == 0
                ? "(No MIDI outputs found)"
                : "(All available outputs added)";
            _addOutputOption.AddItem(label);
            _addOutputOption.SetItemMetadata(idx, "");
            _addOutputOption.SetItemDisabled(idx, true);
        }
        else
        {
            foreach (string name in available)
            {
                int idx = _addOutputOption.ItemCount;
                _addOutputOption.AddItem(name);
                _addOutputOption.SetItemMetadata(idx, name);
            }
        }

        _addOutputOption.Select(0);
    }

    private void OnAddOutputSelected(long index)
    {
        if (_isSyncingUi || _midiManager == null || _addOutputOption == null) return;
        if (_historyManager?.IsRestoring == true) return;
        if (index <= 0 || index >= _addOutputOption.ItemCount) return;

        string name = _addOutputOption.GetItemMetadata((int)index).AsString();
        if (string.IsNullOrEmpty(name)) return;

        RecordMidiHistory($"Add MIDI output '{name}'");
        _midiManager.AddOutputDevice(name);
    }

    private void OnRemoveOutputPressed(string deviceName)
    {
        if (_midiManager == null || string.IsNullOrEmpty(deviceName)) return;
        if (_historyManager?.IsRestoring == true) return;

        RecordMidiHistory($"Remove MIDI output '{deviceName}'");
        _midiManager.RemoveOutputDevice(deviceName);
    }

    private void OnPanicPressed()
    {
        _midiManager?.PanicAllOutputs();
    }

    private void OnRefreshDevicesPressed()
    {
        // Refresh re-enumerates hardware only — not an undoable document edit.
        _midiManager?.RefreshDeviceList();
        SyncFromModel();
        _globalSignals?.EmitSignal(nameof(GlobalSignals.Log),
            "MIDI: device list refreshed", (int)LogType.Info);
    }

    private void OnClearLogPressed()
    {
        _logBuilder.Clear();
        _logLineCount = 0;
        if (_monitorLog != null)
            _monitorLog.Text = string.Empty;
        _midiManager?.ClearPendingMonitorLines();
    }

    private void OnMidiMonitorLine(string line)
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
