// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System;
using System.Text;
using Cue2.Domain.Connections;
using Cue2.Services;
using Godot;
using Rug.Osc;
using Cue2.UI.Utilities;

namespace Cue2.UI.Settings;

/// <summary>
/// Settings panel for OSC send connections (top) and live send monitor log (bottom).
/// Layout mirrors <see cref="SettingsMidi"/>.
/// </summary>
public partial class SettingsOscConnections : ScrollContainer
{
    private GlobalSignals _globalSignals;
    private GlobalData _globalData;
    private HistoryManager _historyManager;
    private OscConnections _oscConnections;

    private Button _newOscButton;
    private VBoxContainer _connectionsContainer;
    private Label _nameLabel;
    private Label _interfaceLabel;
    private Label _destinationLabel;
    private Label _portLabel;
    private Label _statusLabel;

    private CheckBox _listenCheckBox;
    private Button _clearLogButton;
    private CodeEdit _monitorLog;
    private LineEdit _testPathLineEdit;
    private LineEdit _testArgsLineEdit;
    private Button _testSendButton;
    /// <summary>
    /// Target for Test send: metadata id = connection Id, or -1 = all connections.
    /// Defaults to the first connection so loopback tests are not double-delivered.
    /// </summary>
    private OptionButton _testTargetOption;

    private bool _isSyncingUi;
    private readonly StringBuilder _logBuilder = new();
    private int _logLineCount;

    /// <summary>OptionButton metadata value meaning "send on every connection".</summary>
    private const int TestTargetAllId = -1;

    private const int MaxUiLogLines = OscConnections.MaxMonitorLines;

    private PackedScene _oscConnectionCardScene;

    public override void _Ready()
    {
        _globalSignals = GetNodeOrNull<GlobalSignals>("/root/GlobalSignals");
        _globalData = GetNodeOrNull<GlobalData>("/root/GlobalData");
        _historyManager = _globalData?.HistoryManager;
        _oscConnections = GetNodeOrNull<OscConnections>("/root/OscConnections");

        _newOscButton = GetNodeOrNull<Button>("%NewOscButton");
        _connectionsContainer = GetNodeOrNull<VBoxContainer>("%ConnectionsContainer");
        _nameLabel = GetNodeOrNull<Label>("%NameLabel");
        _interfaceLabel = GetNodeOrNull<Label>("%InterfaceLabel");
        _destinationLabel = GetNodeOrNull<Label>("%DestinationLabel");
        _portLabel = GetNodeOrNull<Label>("%PortLabel");
        _statusLabel = GetNodeOrNull<Label>("%StatusLabel");

        _listenCheckBox = GetNodeOrNull<CheckBox>("%ListenCheckBox");
        _clearLogButton = GetNodeOrNull<Button>("%ClearLogButton");
        _monitorLog = GetNodeOrNull<CodeEdit>("%MonitorLog");
        _testPathLineEdit = GetNodeOrNull<LineEdit>("%TestPathLineEdit");
        _testArgsLineEdit = GetNodeOrNull<LineEdit>("%TestArgsLineEdit");
        _testSendButton = GetNodeOrNull<Button>("%TestSendButton");
        EnsureTestTargetOption();

        _oscConnectionCardScene = SceneLoader.LoadPackedScene(
            "uid://b53mk1xolhtmv", out string err);
        if (_oscConnectionCardScene == null)
            GD.PrintErr($"SettingsOscConnections:_Ready - card scene: {err}");

        ConfigureMonitorLogStyle();

        if (_newOscButton != null)
            _newOscButton.Pressed += OnNewConnectionPressed;
        if (_listenCheckBox != null)
            _listenCheckBox.Toggled += OnListenToggled;
        if (_clearLogButton != null)
            _clearLogButton.Pressed += OnClearLogPressed;
        if (_testSendButton != null)
        {
            _testSendButton.Pressed += OnTestSendPressed;
            // Prefer single-target test; "All" remains available in the picker.
            if (string.IsNullOrEmpty(_testSendButton.TooltipText)
                || _testSendButton.TooltipText.Contains("all", StringComparison.OrdinalIgnoreCase))
            {
                _testSendButton.TooltipText =
                    UiLocalizer.T("Send the test message on the selected connection (or all, if chosen).");
            }
        }
        if (_testPathLineEdit != null)
            _testPathLineEdit.TextSubmitted += _ => _testPathLineEdit.ReleaseFocus();
        if (_testArgsLineEdit != null)
            _testArgsLineEdit.TextSubmitted += _ =>
            {
                _testArgsLineEdit.ReleaseFocus();
                OnTestSendPressed();
            };

        if (_nameLabel != null) _nameLabel.Resized += UpdateUiColumns;
        if (_interfaceLabel != null) _interfaceLabel.Resized += UpdateUiColumns;
        if (_destinationLabel != null) _destinationLabel.Resized += UpdateUiColumns;
        if (_portLabel != null) _portLabel.Resized += UpdateUiColumns;

        if (_oscConnections != null)
        {
            _oscConnections.OscConnectionsStateChanged += OnConnectionsStateChanged;
            _oscConnections.OscSendMonitorLine += OnSendMonitorLine;
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

        if (_oscConnections != null)
        {
            _oscConnections.OscConnectionsStateChanged -= OnConnectionsStateChanged;
            _oscConnections.OscSendMonitorLine -= OnSendMonitorLine;
        }

        if (_newOscButton != null)
            _newOscButton.Pressed -= OnNewConnectionPressed;
        if (_listenCheckBox != null)
            _listenCheckBox.Toggled -= OnListenToggled;
        if (_clearLogButton != null)
            _clearLogButton.Pressed -= OnClearLogPressed;
        if (_testSendButton != null)
            _testSendButton.Pressed -= OnTestSendPressed;

        base._ExitTree();
    }

    private void OnVisibilityChanged()
    {
        if (!Visible) return;
        SyncFromModel();
        // Buffered lines keep accumulating while hidden; push them into the CodeEdit now.
        RefreshMonitorLogUi(stickToBottom: true);
    }

    private void OnConnectionsStateChanged()
    {
        if (_isSyncingUi) return;
        if (_historyManager?.IsRestoring == true) return;
        if (!Visible) return;

        // TCP connect status fires this signal often — refresh in place instead of
        // rebuilding cards (which would drop LineEdit focus and transport selection).
        if (ConnectionCardsNeedRebuild())
            SyncFromModel();
        else
            RefreshExistingCards();
    }

    /// <summary>
    /// True when the live connection list no longer matches the instantiated cards.
    /// </summary>
    private bool ConnectionCardsNeedRebuild()
    {
        if (_connectionsContainer == null)
            return false;

        var list = OscConnections.Connections;
        int listCount = list?.Count ?? 0;
        var cards = new System.Collections.Generic.List<SettingsOscConnectionCard>();
        int other = 0;
        foreach (var child in _connectionsContainer.GetChildren())
        {
            if (child is SettingsOscConnectionCard card)
                cards.Add(card);
            else
                other++;
        }

        if (listCount == 0)
            return cards.Count != 0 || other == 0;

        if (other > 0 || cards.Count != listCount)
            return true;

        for (int i = 0; i < listCount; i++)
        {
            var conn = list[i];
            var card = cards[i];
            if (conn == null || !GodotObject.IsInstanceValid(conn))
                return true;
            if (!GodotObject.IsInstanceValid(card) || card.CueOscConnection != conn)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Refreshes per-row TCP status and the panel summary without recreating cards.
    /// </summary>
    /// <param name="refreshTargets">When true, also rebuild the test-send target picker (locale / list identity).</param>
    private void RefreshExistingCards(bool refreshTargets = false)
    {
        _isSyncingUi = true;
        try
        {
            if (_listenCheckBox != null && _oscConnections != null)
                _listenCheckBox.SetPressedNoSignal(_oscConnections.MonitorEnabled);

            if (_connectionsContainer != null)
            {
                foreach (var child in _connectionsContainer.GetChildren())
                {
                    if (child is SettingsOscConnectionCard card)
                        card.RefreshStatus();
                }
            }

            if (refreshTargets)
                RefreshTestTargetOptions();
            UpdateStatusLabel();
        }
        finally
        {
            _isSyncingUi = false;
        }
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
        ClearConnectionCards();
        OnClearLogPressed();
        SyncFromModel();
    }

    private void RecordHistory(string description)
    {
        if (_historyManager == null || _historyManager.IsRestoring) return;
        _historyManager.RecordSettingsChange(description, null, "OscConnections");
    }

    private void ConfigureMonitorLogStyle()
    {
        // Cool cyan-ish to distinguish send monitor from receive/MIDI (green).
        UiUtilities.ConfigureReadOnlyMonitorLog(
            _monitorLog,
            fontColor: new Color(0.7f, 0.9f, 0.95f, 1f));
    }

    private void SyncFromModel()
    {
        _isSyncingUi = true;
        try
        {
            if (_listenCheckBox != null && _oscConnections != null)
                _listenCheckBox.SetPressedNoSignal(_oscConnections.MonitorEnabled);

            RebuildConnectionCards();
            RefreshTestTargetOptions();
            UpdateStatusLabel();
        }
        finally
        {
            _isSyncingUi = false;
        }
    }

    /// <summary>
    /// Ensures a connection target OptionButton exists in the test-send row (created in code
    /// so older scene files still work).
    /// </summary>
    private void EnsureTestTargetOption()
    {
        if (_testTargetOption != null && GodotObject.IsInstanceValid(_testTargetOption))
            return;

        // Prefer scene node if present; otherwise insert before the Test button.
        _testTargetOption = GetNodeOrNull<OptionButton>("%TestTargetOption");
        if (_testTargetOption != null)
            return;

        if (_testSendButton?.GetParent() is not Control row)
            return;

        _testTargetOption = new OptionButton
        {
            Name = "TestTargetOption",
            CustomMinimumSize = new Vector2(140, 0),
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            TooltipText = UiLocalizer.T("Which OSC connection receives the test message."),
        };
        _testTargetOption.UniqueNameInOwner = true;
        int insertAt = _testSendButton.GetIndex();
        row.AddChild(_testTargetOption);
        row.MoveChild(_testTargetOption, insertAt);
    }

    /// <summary>
    /// Rebuilds the test-target dropdown from the live connection list.
    /// Preserves the previous selection when still valid; otherwise selects the first connection
    /// (not "All") so a single test click does not fan out to every destination.
    /// </summary>
    private void RefreshTestTargetOptions()
    {
        EnsureTestTargetOption();
        if (_testTargetOption == null) return;

        // int.MinValue = first open / empty picker — default to first concrete connection.
        const int noPriorSelection = int.MinValue;
        int previousId = noPriorSelection;
        if (_testTargetOption.ItemCount > 0 && _testTargetOption.Selected >= 0)
            previousId = (int)_testTargetOption.GetItemMetadata(_testTargetOption.Selected);

        _testTargetOption.Clear();

        var list = OscConnections.Connections;
        if (list == null || list.Count == 0)
        {
            _testTargetOption.AddItem(UiLocalizer.T("(no connections)"), 0);
            _testTargetOption.SetItemMetadata(0, TestTargetAllId);
            _testTargetOption.Disabled = true;
            return;
        }

        _testTargetOption.Disabled = false;
        int selectIndex = 0;
        int idx = 0;

        foreach (var conn in list)
        {
            if (conn == null || !GodotObject.IsInstanceValid(conn)) continue;
            string label = string.IsNullOrWhiteSpace(conn.Name)
                ? $"OSC {conn.Id}"
                : conn.Name;
            string dest = $"{conn.Address}:{conn.Port}";
            _testTargetOption.AddItem($"{label} → {dest}", idx);
            _testTargetOption.SetItemMetadata(idx, conn.Id);
            if (conn.Id == previousId)
                selectIndex = idx;
            idx++;
        }

        // Optional multi-dest fan-out (explicit choice only).
        int allIdx = idx;
        _testTargetOption.AddItem(UiLocalizer.T("All connections"), allIdx);
        _testTargetOption.SetItemMetadata(allIdx, TestTargetAllId);

        if (previousId == noPriorSelection)
        {
            selectIndex = 0; // first real connection
        }
        else if (previousId == TestTargetAllId)
        {
            selectIndex = allIdx;
        }
        else
        {
            bool stillThere = false;
            for (int i = 0; i < allIdx; i++)
            {
                if ((int)_testTargetOption.GetItemMetadata(i) == previousId)
                {
                    stillThere = true;
                    selectIndex = i;
                    break;
                }
            }
            if (!stillThere)
                selectIndex = 0;
        }

        _testTargetOption.Select(selectIndex);
    }

    private void RebuildConnectionCards()
    {
        if (_connectionsContainer == null || _oscConnectionCardScene == null) return;

        ClearConnectionCards();

        var list = OscConnections.Connections;
        if (list == null || list.Count == 0)
        {
            var empty = new Label
            {
                Text = "No OSC connections — create one above.",
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
            };
            empty.AddThemeFontSizeOverride("font_size", 11);
            empty.AddThemeColorOverride("font_color", new Color(0.55f, 0.55f, 0.55f, 1f));
            _connectionsContainer.AddChild(empty);
            return;
        }

        var ratios = GetColumnRatios();
        foreach (var connection in list)
        {
            if (connection == null || !GodotObject.IsInstanceValid(connection)) continue;
            var card = _oscConnectionCardScene.Instantiate<SettingsOscConnectionCard>();
            _connectionsContainer.AddChild(card);
            card.SetCueOscConnection(connection);
            card.HistoryRecordRequested += OnCardHistoryRequested;
            if (ratios.Count > 0)
                card.UpdateRatios(ratios);
        }
    }

    private void ClearConnectionCards()
    {
        if (_connectionsContainer == null) return;
        foreach (var child in _connectionsContainer.GetChildren())
        {
            if (child is SettingsOscConnectionCard card)
                card.HistoryRecordRequested -= OnCardHistoryRequested;
            _connectionsContainer.RemoveChild(child);
            child.QueueFree();
        }
    }

    private void OnCardHistoryRequested(string description)
    {
        RecordHistory(description);
    }

    private void UpdateStatusLabel()
    {
        if (_statusLabel == null) return;
        int count = OscConnections.Connections?.Count ?? 0;
        int open = 0;
        if (OscConnections.Connections != null)
        {
            foreach (var c in OscConnections.Connections)
            {
                if (c != null && GodotObject.IsInstanceValid(c) && c.IsSenderOpen)
                    open++;
            }
        }
        _statusLabel.Text = count == 0
            ? "No connections"
            : $"{open}/{count} connected";
    }

    private void OnNewConnectionPressed()
    {
        if (_historyManager?.IsRestoring == true) return;
        RecordHistory("Add OSC connection");
        OscConnections.CreateConnection();
        // Rebuild via OscConnectionsStateChanged
    }

    private void OnTestSendPressed()
    {
        var list = OscConnections.Connections;
        if (list == null || list.Count == 0)
        {
            _globalSignals?.EmitSignal(nameof(GlobalSignals.Log),
                "OSC test: no connections", (int)LogType.Warning);
            return;
        }

        string rawPath = (_testPathLineEdit?.Text ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(rawPath))
        {
            _globalSignals?.EmitSignal(nameof(GlobalSignals.Log),
                "OSC test: enter a path", (int)LogType.Warning);
            return;
        }

        try
        {
            string rawArgs = _testArgsLineEdit?.Text ?? string.Empty;
            // Accept QLab-style combined lines in the path box ("/jump 2").
            if (!OscMessageUtil.SplitPathAndArgs(rawPath, rawArgs, out string path, out string args)
                || string.IsNullOrEmpty(path) || path == "/")
            {
                _globalSignals?.EmitSignal(nameof(GlobalSignals.Log),
                    "OSC test: enter a path", (int)LogType.Warning);
                return;
            }

            // Reflect normalized path/args back into the UI (space-split once).
            if (_testPathLineEdit != null && _testPathLineEdit.Text != path)
                _testPathLineEdit.Text = path;
            if (_testArgsLineEdit != null
                && string.IsNullOrWhiteSpace(rawArgs)
                && !string.IsNullOrEmpty(args)
                && _testArgsLineEdit.Text != args)
            {
                _testArgsLineEdit.Text = args;
            }

            OscMessage msg = string.IsNullOrWhiteSpace(args)
                ? new OscMessage(path)
                : OscMessageUtil.BuildMessage(path, args);

            int targetId = TestTargetAllId;
            if (_testTargetOption != null
                && _testTargetOption.ItemCount > 0
                && _testTargetOption.Selected >= 0)
            {
                targetId = (int)_testTargetOption.GetItemMetadata(_testTargetOption.Selected);
            }
            else if (list.Count > 0 && list[0] != null)
            {
                // No picker yet — still only hit the first connection (not every one).
                targetId = list[0].Id;
            }

            int sent = 0;
            string targetLabel;
            if (targetId == TestTargetAllId)
            {
                targetLabel = "all connections";
                foreach (var conn in list)
                {
                    if (conn == null || !GodotObject.IsInstanceValid(conn)) continue;
                    conn.SendMessage(msg);
                    sent++;
                }
            }
            else
            {
                var conn = OscConnections.GetCueOscConnection(targetId);
                if (conn == null || !GodotObject.IsInstanceValid(conn))
                {
                    _globalSignals?.EmitSignal(nameof(GlobalSignals.Log),
                        "OSC test: selected connection not found", (int)LogType.Warning);
                    RefreshTestTargetOptions();
                    return;
                }
                targetLabel = conn.Name ?? $"id {conn.Id}";
                conn.SendMessage(msg);
                sent = 1;
            }

            _globalSignals?.EmitSignal(nameof(GlobalSignals.Log),
                $"OSC test: {path} {OscMessageUtil.FormatArgs(msg)} → {targetLabel} ({sent})",
                (int)LogType.Info);
        }
        catch (Exception ex)
        {
            _globalSignals?.EmitSignal(nameof(GlobalSignals.Log),
                $"OSC test failed: {ex.Message}", (int)LogType.Error);
        }
    }

    private void OnListenToggled(bool pressed)
    {
        if (_isSyncingUi || _oscConnections == null) return;
        if (_historyManager?.IsRestoring == true) return;
        if (_oscConnections.MonitorEnabled == pressed) return;

        RecordHistory(pressed ? "Enable OSC send monitor" : "Disable OSC send monitor");
        _oscConnections.MonitorEnabled = pressed;
    }

    private void OnClearLogPressed()
    {
        _logBuilder.Clear();
        _logLineCount = 0;
        if (_monitorLog != null)
            _monitorLog.Text = string.Empty;
        _oscConnections?.ClearPendingMonitorLines();
    }

    private void OnSendMonitorLine(string line)
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

    private Godot.Collections.Dictionary GetColumnRatios()
    {
        if (_nameLabel == null || _interfaceLabel == null || _destinationLabel == null || _portLabel == null)
            return new Godot.Collections.Dictionary();

        float totalWidth = _nameLabel.Size.X + _interfaceLabel.Size.X
            + _destinationLabel.Size.X + _portLabel.Size.X;
        if (totalWidth <= 0) return new Godot.Collections.Dictionary();

        var ratios = new Godot.Collections.Dictionary();
        ratios["Name"] = _nameLabel.Size.X / totalWidth;
        ratios["Interface"] = _interfaceLabel.Size.X / totalWidth;
        ratios["Destination"] = _destinationLabel.Size.X / totalWidth;
        ratios["Port"] = _portLabel.Size.X / totalWidth;
        return ratios;
    }

    private void UpdateUiColumns()
    {
        var ratios = GetColumnRatios();
        if (ratios.Count == 0 || _connectionsContainer == null) return;
        foreach (var child in _connectionsContainer.GetChildren())
        {
            if (child is SettingsOscConnectionCard card)
                card.UpdateRatios(ratios);
        }
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
        RefreshExistingCards(refreshTargets: true);
    }

}
