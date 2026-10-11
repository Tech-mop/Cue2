// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using Cue2.Domain.Timecode;
using Cue2.Services;
using Cue2.UI.Timecode;
using Cue2.UI.Utilities;
using Godot;

namespace Cue2.UI.Settings;

/// <summary>
/// Settings panel for timecode: session inputs/outputs (top) and live clock display (bottom).
/// </summary>
public partial class SettingsTimecode : ScrollContainer
{
    /// <summary>Stable Settings tree key (English, persisted in user data).</summary>
    public const string MenuKey = "Timecode";

    private GlobalSignals _globalSignals;
    private GlobalData _globalData;
    private HistoryManager _historyManager;
    private TimecodeManager _timecodeManager;
    private MidiManager _midiManager;

    private CheckButton _enableTimecodeCheck;
    private Label _statusLabel;
    private OptionButton _addInputOption;
    private Button _refreshDevicesButton;
    private VBoxContainer _sessionInputsList;
    private OptionButton _addOutputOption;
    private VBoxContainer _sessionOutputsList;
    private FlowContainer _displayList;

    private PackedScene _cardScene;
    private PackedScene _clockScene;
    private bool _isSyncingUi;

    public override void _Ready()
    {
        _globalSignals = GetNodeOrNull<GlobalSignals>("/root/GlobalSignals");
        _globalData = GetNodeOrNull<GlobalData>("/root/GlobalData");
        _historyManager = _globalData?.HistoryManager;
        _timecodeManager = GetNodeOrNull<TimecodeManager>("/root/TimecodeManager");
        _midiManager = GetNodeOrNull<MidiManager>("/root/MidiManager");

        _enableTimecodeCheck = GetNodeOrNull<CheckButton>("%EnableTimecodeCheck");
        _statusLabel = GetNodeOrNull<Label>("%StatusLabel");
        _addInputOption = GetNodeOrNull<OptionButton>("%AddInputOption");
        _refreshDevicesButton = GetNodeOrNull<Button>("%RefreshDevicesButton");
        _sessionInputsList = GetNodeOrNull<VBoxContainer>("%SessionInputsList");
        _addOutputOption = GetNodeOrNull<OptionButton>("%AddOutputOption");
        _sessionOutputsList = GetNodeOrNull<VBoxContainer>("%SessionOutputsList");
        _displayList = GetNodeOrNull<FlowContainer>("%TimecodeDisplayList");

        _cardScene = SceneLoader.LoadPackedScene(
            "res://src/UI/Settings/SettingsTimecodeEndpointCard.tscn", out string err);
        if (_cardScene == null)
            GD.PrintErr($"SettingsTimecode:_Ready - card scene: {err}");
        _clockScene = SceneLoader.LoadPackedScene(
            "res://src/UI/Timecode/TimecodeClockCard.tscn", out string clockErr);
        if (_clockScene == null)
            GD.PrintErr($"SettingsTimecode:_Ready - clock scene: {clockErr}");

        if (_enableTimecodeCheck != null)
            _enableTimecodeCheck.Toggled += OnEnableTimecodeToggled;
        if (_addInputOption != null)
            _addInputOption.ItemSelected += OnAddInputSelected;
        if (_addOutputOption != null)
            _addOutputOption.ItemSelected += OnAddOutputSelected;
        if (_refreshDevicesButton != null)
            _refreshDevicesButton.Pressed += OnRefreshDevicesPressed;

        if (_timecodeManager != null)
            _timecodeManager.TimecodeStateChanged += OnTimecodeStateChanged;

        if (_historyManager != null)
            _historyManager.HistoryRestored += OnHistoryRestored;
        if (_globalSignals != null)
            _globalSignals.NewSession += OnNewSession;

        VisibilityChanged += OnVisibilityChanged;

        PopulateAddOptions();
        SyncFromModel();

        UiLocalizer.LocalizeTree(this);
        if (_globalSignals != null)
            _globalSignals.LocaleChanged += OnLocaleChanged;

        SetProcess(Visible);
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

        if (_timecodeManager != null)
            _timecodeManager.TimecodeStateChanged -= OnTimecodeStateChanged;

        if (_enableTimecodeCheck != null)
            _enableTimecodeCheck.Toggled -= OnEnableTimecodeToggled;
        if (_addInputOption != null)
            _addInputOption.ItemSelected -= OnAddInputSelected;
        if (_addOutputOption != null)
            _addOutputOption.ItemSelected -= OnAddOutputSelected;
        if (_refreshDevicesButton != null)
            _refreshDevicesButton.Pressed -= OnRefreshDevicesPressed;

        base._ExitTree();
    }

    public override void _Process(double delta)
    {
        if (!Visible) return;
        RefreshCardStatuses();
    }

    private void OnVisibilityChanged()
    {
        SetProcess(Visible);
        if (!Visible) return;
        SyncFromModel();
    }

    private void OnTimecodeStateChanged()
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
        SyncFromModel();
    }

    private void RecordTimecodeHistory(string description)
    {
        if (_historyManager == null || _historyManager.IsRestoring) return;
        _historyManager.RecordSettingsChange(description, null, TimecodeManager.HistoryKey);
    }

    private void SyncFromModel()
    {
        if (_timecodeManager == null) return;

        _isSyncingUi = true;
        try
        {
            if (_enableTimecodeCheck != null)
                _enableTimecodeCheck.SetPressedNoSignal(_timecodeManager.TimecodeEnabled);

            _timecodeManager.RefreshLtcPlaybackConflictWarnings(force: true);
            RebuildEndpointCards(_sessionInputsList, _timecodeManager.Inputs,
                UiLocalizer.T("No inputs in session — add one above."));
            RebuildEndpointCards(_sessionOutputsList, _timecodeManager.Outputs,
                UiLocalizer.T("No outputs in session — add one above."));
            RebuildDisplayRows();
            UpdateStatusLabel();
        }
        finally
        {
            _isSyncingUi = false;
        }
    }

    private void PopulateAddOptions()
    {
        if (_addInputOption != null)
        {
            _addInputOption.Clear();
            UiLocalizer.AddTranslatedItem(_addInputOption, "Select type to add…");
            _addInputOption.SetItemDisabled(0, true);
            foreach (TimecodeKind kind in Enum.GetValues<TimecodeKind>())
            {
                if (!kind.AllowsInput()) continue;
                UiLocalizer.AddTranslatedItem(_addInputOption, kind.GetDisplayName(), (int)kind);
            }
            _addInputOption.Select(0);
        }

        if (_addOutputOption != null)
        {
            _addOutputOption.Clear();
            UiLocalizer.AddTranslatedItem(_addOutputOption, "Select type to add…");
            _addOutputOption.SetItemDisabled(0, true);
            foreach (TimecodeKind kind in Enum.GetValues<TimecodeKind>())
            {
                if (!kind.AllowsOutput()) continue;
                UiLocalizer.AddTranslatedItem(_addOutputOption, kind.GetDisplayName(), (int)kind);
            }
            _addOutputOption.Select(0);
        }
    }

    /// <summary>
    /// Rebuilds endpoint editor cards for one session list.
    /// </summary>
    /// <param name="list">Container to fill.</param>
    /// <param name="endpoints">Live endpoints.</param>
    /// <param name="emptyText">Already-translated empty-state copy.</param>
    private void RebuildEndpointCards(
        VBoxContainer list,
        IReadOnlyList<TimecodeEndpoint> endpoints,
        string emptyText)
    {
        if (list == null) return;

        foreach (Node child in list.GetChildren())
        {
            if (child is SettingsTimecodeEndpointCard card)
            {
                card.HistoryRecordRequested -= OnCardHistoryRequested;
                card.EndpointIdentityChanged -= OnCardIdentityChanged;
            }
            child.QueueFree();
        }

        if (endpoints == null || endpoints.Count == 0)
        {
            var empty = new Label
            {
                Text = emptyText,
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
            };
            empty.AddThemeFontSizeOverride("font_size", 11);
            empty.AddThemeColorOverride("font_color", new Color(0.55f, 0.55f, 0.55f, 1f));
            list.AddChild(empty);
            return;
        }

        if (_cardScene == null) return;

        foreach (var endpoint in endpoints)
        {
            if (_cardScene.Instantiate() is not SettingsTimecodeEndpointCard card)
                continue;
            card.HistoryRecordRequested += OnCardHistoryRequested;
            card.EndpointIdentityChanged += OnCardIdentityChanged;
            list.AddChild(card);
            card.Bind(endpoint);
        }
    }

    private void RebuildDisplayRows()
    {
        if (_displayList == null || _timecodeManager == null) return;

        foreach (Node child in _displayList.GetChildren())
            child.QueueFree();

        int total = _timecodeManager.Inputs.Count + _timecodeManager.Outputs.Count;
        if (total == 0)
        {
            var empty = new Label
            {
                Text = UiLocalizer.T("No timecode registered — add an input or output above."),
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
            };
            empty.AddThemeFontSizeOverride("font_size", 11);
            empty.AddThemeColorOverride("font_color", new Color(0.55f, 0.55f, 0.55f, 1f));
            _displayList.AddChild(empty);
            return;
        }

        foreach (var endpoint in _timecodeManager.Inputs)
            AddClockCard(endpoint, enableTransport: false);
        foreach (var endpoint in _timecodeManager.Outputs)
            AddClockCard(endpoint, enableTransport: true);
    }

    private void AddClockCard(TimecodeEndpoint endpoint, bool enableTransport)
    {
        if (_clockScene == null || endpoint == null) return;
        if (_clockScene.Instantiate() is not TimecodeClockCard clock)
            return;
        _displayList.AddChild(clock);
        clock.EnableTransport = enableTransport;
        clock.Bind(endpoint.Id);
    }

    private void RefreshCardStatuses()
    {
        _timecodeManager?.RefreshLtcPlaybackConflictWarnings();
        RefreshListStatuses(_sessionInputsList);
        RefreshListStatuses(_sessionOutputsList);
    }

    private static void RefreshListStatuses(VBoxContainer list)
    {
        if (list == null) return;
        foreach (Node child in list.GetChildren())
        {
            if (child is SettingsTimecodeEndpointCard card)
                card.RefreshStatus();
        }
    }

    private void UpdateStatusLabel()
    {
        if (_statusLabel == null || _timecodeManager == null) return;

        int nin = _timecodeManager.Inputs.Count;
        int nout = _timecodeManager.Outputs.Count;
        if (!_timecodeManager.TimecodeEnabled)
        {
            _statusLabel.Text = (nin + nout) == 0
                ? UiLocalizer.T("Timecode off")
                : UiLocalizer.Tf("Timecode off · {0} in / {1} out", nin, nout);
            return;
        }

        _statusLabel.Text = UiLocalizer.Tf("{0} in / {1} out", nin, nout);
    }

    private void OnEnableTimecodeToggled(bool pressed)
    {
        if (_isSyncingUi || _timecodeManager == null) return;
        if (_historyManager?.IsRestoring == true) return;
        if (_timecodeManager.TimecodeEnabled == pressed) return;

        RecordTimecodeHistory(pressed ? "Enable Timecode" : "Disable Timecode");
        _timecodeManager.TimecodeEnabled = pressed;
        UpdateStatusLabel();
    }

    private void OnAddInputSelected(long index)
    {
        if (_isSyncingUi || _timecodeManager == null || _addInputOption == null) return;
        if (_historyManager?.IsRestoring == true) return;
        if (index <= 0 || index >= _addInputOption.ItemCount) return;

        var kind = (TimecodeKind)_addInputOption.GetItemId((int)index);
        RecordTimecodeHistory($"Add timecode input ({kind.GetDisplayName()})");
        _timecodeManager.AddInput(kind);
        _addInputOption.Select(0);
    }

    private void OnAddOutputSelected(long index)
    {
        if (_isSyncingUi || _timecodeManager == null || _addOutputOption == null) return;
        if (_historyManager?.IsRestoring == true) return;
        if (index <= 0 || index >= _addOutputOption.ItemCount) return;

        var kind = (TimecodeKind)_addOutputOption.GetItemId((int)index);
        RecordTimecodeHistory($"Add timecode output ({kind.GetDisplayName()})");
        _timecodeManager.AddOutput(kind);
        _addOutputOption.Select(0);
    }

    private void OnRefreshDevicesPressed()
    {
        _midiManager?.RefreshDeviceList();
        SyncFromModel();
        _globalSignals?.EmitSignal(nameof(GlobalSignals.Log),
            "Timecode: device list refreshed", (int)LogType.Info);
    }

    private void OnCardHistoryRequested(string description)
    {
        RecordTimecodeHistory(description);
    }

    private void OnCardIdentityChanged()
    {
        if (_isSyncingUi) return;
        RebuildDisplayRows();
        UpdateStatusLabel();
    }

    private void OnLocaleChanged(string localeCode)
    {
        if (!GodotObject.IsInstanceValid(this))
            return;
        UiLocalizer.LocalizeTree(this);
        PopulateAddOptions();
        if (Visible)
            SyncFromModel();
    }
}
