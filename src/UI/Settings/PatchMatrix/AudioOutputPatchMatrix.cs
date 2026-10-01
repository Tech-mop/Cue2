// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using Cue2.Domain.Devices;
using Cue2.Services;
using Cue2.UI.Popups;
using Cue2.UI.Utilities;

namespace Cue2.UI.Settings.PatchMatrix;

/// <summary>
/// Audio output patch editor: named buses, devices added on demand, a frozen routing grid
/// with collapsible groups of 8, and a routing list.
/// </summary>
public partial class AudioOutputPatchMatrix : Control
{
    [Export] private AudioOutputPatch Patch { get; set; }

    [Export] private int PatchId { get; set; }

    private GlobalData _globalData;
    private GlobalSignals _globalSignals;
    private AudioDevices _audioDevices;

    private LineEdit _patchName;
    private LineEdit _patchVolumeInput;
    private Button _deletePatchButton;
    private Button _addChannelButton;
    private Button _refreshButton;
    private OptionButton _addDeviceOption;
    private Button _gridViewButton;
    private Button _listViewButton;
    private bool _isSyncingVolumeUi;
    private bool _syncingAddDeviceUi;

    private Control _toolbarRow;
    private Control _matrixBody;
    private Control _headerCorner;
    private ScrollContainer _channelScroll;
    private VBoxContainer _channelList;
    private ScrollContainer _headerScroll;
    private PatchMatrixColumnHeader _columnHeader;
    private ScrollContainer _cellScroll;
    private PatchMatrixCells _cells;
    private ScrollContainer _routingListScroll;
    private VBoxContainer _routingList;

    private Button _collapseButton;

    private bool _channelsExpanded = true;
    private bool _listView;
    private bool _isRebuilding;
    private bool _isDisposed;
    private bool _syncingScroll;
    private ResourceInUseDeleteDialog _activeDeleteDialog;

    private readonly Dictionary<string, HashSet<int>> _collapsedGroups = new();
    private readonly HashSet<string> _collapseInitialized = new();
    private readonly List<HBoxContainer> _channelRows = new();

    private PatchMatrixView _view;
    private List<string> _availableDeviceList = new();

    /// <inheritdoc />
    public override void _Ready()
    {
        _globalData = GetNode<GlobalData>("/root/GlobalData");
        _globalSignals = GetNode<GlobalSignals>("/root/GlobalSignals");
        _audioDevices = GetNode<AudioDevices>("/root/AudioDevices");

        _patchName = GetNode<LineEdit>("%PatchName");
        _patchVolumeInput = GetNode<LineEdit>("%PatchVolumeInput");
        _deletePatchButton = GetNode<Button>("%DeletePatchButton");
        _addChannelButton = GetNode<Button>("%AddChannelButton");
        _refreshButton = GetNode<Button>("%RefreshButton");
        _addDeviceOption = GetNode<OptionButton>("%AddDeviceOption");
        _gridViewButton = GetNode<Button>("%GridViewButton");
        _listViewButton = GetNode<Button>("%ListViewButton");
        _collapseButton = GetNode<Button>("%CollapseButton");

        _toolbarRow = GetNode<Control>("%ToolbarRow");
        _matrixBody = GetNode<Control>("%MatrixBody");
        _headerCorner = GetNode<Control>("%HeaderCorner");
        _channelScroll = GetNode<ScrollContainer>("%ChannelScroll");
        _channelList = GetNode<VBoxContainer>("%ChannelList");
        _headerScroll = GetNode<ScrollContainer>("%HeaderScroll");
        _columnHeader = GetNode<PatchMatrixColumnHeader>("%ColumnHeader");
        _cellScroll = GetNode<ScrollContainer>("%CellScroll");
        _cells = GetNode<PatchMatrixCells>("%Cells");
        _routingListScroll = GetNode<ScrollContainer>("%RoutingListScroll");
        _routingList = GetNode<VBoxContainer>("%RoutingList");

        _patchName.Text = Patch?.Name ?? "Unnamed";
        _patchName.TextChanged += PatchNameOnTextChanged;
        _patchName.TextSubmitted += _ => _patchName.ReleaseFocus();
        _patchName.FocusExited += OnPatchNameFocusExited;

        _patchVolumeInput.TextSubmitted += OnPatchVolumeSubmitted;
        _patchVolumeInput.FocusExited += OnPatchVolumeFocusExited;
        LineEditDbDragSlider.EnableVolume(_patchVolumeInput);
        SyncPatchVolumeUi();

        _deletePatchButton.Pressed += DeletePatchButtonPressed;
        _addChannelButton.Pressed += AddChannelButtonPressed;
        _refreshButton.Pressed += OnRefreshButtonPressed;
        _addDeviceOption.ItemSelected += OnAddDeviceSelected;
        _collapseButton.SetMeta(UiLocalizer.MetaSkip, true);
        _collapseButton.Pressed += OnCollapsePressed;

        var viewGroup = new ButtonGroup();
        _gridViewButton.ButtonGroup = viewGroup;
        _listViewButton.ButtonGroup = viewGroup;
        _gridViewButton.Pressed += () => SetListView(false);
        _listViewButton.Pressed += () => SetListView(true);

        _cells.CellPainted += OnCellPainted;
        _cells.GroupExpandRequested += ExpandGroup;
        _cells.HoverChanged += OnCellHoverChanged;
        _cells.PaintStarted += OnPaintStarted;
        _cells.PaintEnded += OnPaintEnded;
        _columnHeader.RemoveDeviceRequested += RemoveDeviceFromPatch;
        _columnHeader.ToggleGroupRequested += ToggleGroup;

        _cellScroll.GetHScrollBar().ValueChanged += OnCellHScroll;
        _cellScroll.GetVScrollBar().ValueChanged += OnCellVScroll;
        _headerScroll.GetHScrollBar().ValueChanged += OnHeaderHScroll;
        _channelScroll.GetVScrollBar().ValueChanged += OnChannelVScroll;

        _globalSignals.AudioDevicesChanged += SyncAudioDeviceDisplays;
        _globalSignals.LocaleChanged += OnLocaleChanged;

        UiLocalizer.LocalizeTree(this);
        ApplyChannelsExpandedUi();
        SyncAudioDeviceDisplays();
    }

    /// <inheritdoc />
    public override void _ExitTree()
    {
        _isDisposed = true;

        if (_globalSignals != null && GodotObject.IsInstanceValid(_globalSignals))
        {
            _globalSignals.AudioDevicesChanged -= SyncAudioDeviceDisplays;
            _globalSignals.LocaleChanged -= OnLocaleChanged;
        }

        if (_patchName != null && GodotObject.IsInstanceValid(_patchName))
            _patchName.TextChanged -= PatchNameOnTextChanged;
        if (_patchVolumeInput != null && GodotObject.IsInstanceValid(_patchVolumeInput))
        {
            _patchVolumeInput.TextSubmitted -= OnPatchVolumeSubmitted;
            _patchVolumeInput.FocusExited -= OnPatchVolumeFocusExited;
        }
        if (_deletePatchButton != null && GodotObject.IsInstanceValid(_deletePatchButton))
            _deletePatchButton.Pressed -= DeletePatchButtonPressed;
        if (_addChannelButton != null && GodotObject.IsInstanceValid(_addChannelButton))
            _addChannelButton.Pressed -= AddChannelButtonPressed;
        if (_refreshButton != null && GodotObject.IsInstanceValid(_refreshButton))
            _refreshButton.Pressed -= OnRefreshButtonPressed;
        if (_addDeviceOption != null && GodotObject.IsInstanceValid(_addDeviceOption))
            _addDeviceOption.ItemSelected -= OnAddDeviceSelected;
        if (_collapseButton != null && GodotObject.IsInstanceValid(_collapseButton))
            _collapseButton.Pressed -= OnCollapsePressed;

        if (_cells != null && GodotObject.IsInstanceValid(_cells))
        {
            _cells.CellPainted -= OnCellPainted;
            _cells.GroupExpandRequested -= ExpandGroup;
            _cells.HoverChanged -= OnCellHoverChanged;
            _cells.PaintStarted -= OnPaintStarted;
            _cells.PaintEnded -= OnPaintEnded;
        }

        if (_columnHeader != null && GodotObject.IsInstanceValid(_columnHeader))
        {
            _columnHeader.RemoveDeviceRequested -= RemoveDeviceFromPatch;
            _columnHeader.ToggleGroupRequested -= ToggleGroup;
        }

        if (_cellScroll != null && GodotObject.IsInstanceValid(_cellScroll))
        {
            _cellScroll.GetHScrollBar().ValueChanged -= OnCellHScroll;
            _cellScroll.GetVScrollBar().ValueChanged -= OnCellVScroll;
        }
        if (_headerScroll != null && GodotObject.IsInstanceValid(_headerScroll))
            _headerScroll.GetHScrollBar().ValueChanged -= OnHeaderHScroll;
        if (_channelScroll != null && GodotObject.IsInstanceValid(_channelScroll))
            _channelScroll.GetVScrollBar().ValueChanged -= OnChannelVScroll;

        FreeDynamicChildren(_channelList);
        FreeDynamicChildren(_routingList);
    }

    private void OnLocaleChanged(string localeCode)
    {
        if (_isDisposed || !GodotObject.IsInstanceValid(this))
            return;
        UiLocalizer.LocalizeTree(this);
        PopulateAddDeviceOption();
        ApplyCollapseButtonUi();
        RebuildRoutingList();
        _columnHeader?.QueueRedraw();
        _cells?.QueueRedraw();
    }

    private void SyncPatchVolumeUi()
    {
        if (_patchVolumeInput == null || !GodotObject.IsInstanceValid(_patchVolumeInput))
            return;

        float linear = Patch != null && GodotObject.IsInstanceValid(Patch)
            ? Mathf.Clamp(Patch.Volume, 0f, 1f)
            : 1f;

        _isSyncingVolumeUi = true;
        _patchVolumeInput.Text = FormatPatchVolumeDb(linear);
        _isSyncingVolumeUi = false;
    }

    private static string FormatPatchVolumeDb(float linear) =>
        $"{UiUtilities.LinearToDb(Mathf.Clamp(linear, 0f, 1f))}dB";

    private void OnPatchVolumeSubmitted(string text) => CommitPatchVolume(text);

    private void OnPatchVolumeFocusExited()
    {
        if (_isSyncingVolumeUi || _patchVolumeInput == null || !GodotObject.IsInstanceValid(_patchVolumeInput))
            return;
        CommitPatchVolume(_patchVolumeInput.Text);
    }

    private void CommitPatchVolume(string text)
    {
        if (_isSyncingVolumeUi || _isDisposed)
            return;
        if (_patchVolumeInput == null || !GodotObject.IsInstanceValid(_patchVolumeInput))
            return;
        if (Patch == null || !GodotObject.IsInstanceValid(Patch))
            return;
        if (_globalData?.HistoryManager?.IsRestoring == true)
            return;

        try
        {
            if (!float.TryParse(text.Replace("dB", "").Replace("db", "").Trim(), out var dbValue))
            {
                _globalSignals?.EmitSignal(nameof(GlobalSignals.Log),
                    $"Invalid patch volume: {text}", 1);
                SyncPatchVolumeUi();
                return;
            }

            if (dbValue > 0f)
                dbValue = -dbValue;

            float linear = UiUtilities.DbToLinear(dbValue);
            _isSyncingVolumeUi = true;
            _patchVolumeInput.Text = FormatPatchVolumeDb(linear);
            _isSyncingVolumeUi = false;

            if (Math.Abs(Patch.Volume - linear) < 1e-6f)
            {
                if (_patchVolumeInput.HasFocus())
                    _patchVolumeInput.ReleaseFocus();
                return;
            }

            RecordPatchHistory("Change patch volume");
            Patch.Volume = linear;
            _globalData?.Settings?.UpdatePatch(Patch);

            if (_patchVolumeInput.HasFocus())
                _patchVolumeInput.ReleaseFocus();
        }
        catch (Exception ex)
        {
            _globalSignals?.EmitSignal(nameof(GlobalSignals.Log),
                $"Error parsing patch volume: {ex.Message}", 2);
            SyncPatchVolumeUi();
        }
    }

    private void OnCollapsePressed()
    {
        _channelsExpanded = !_channelsExpanded;
        ApplyChannelsExpandedUi();
    }

    private void SetListView(bool listView)
    {
        _listView = listView;
        ApplyChannelsExpandedUi();
        if (_listView)
            RebuildRoutingList();
    }

    private void ApplyChannelsExpandedUi()
    {
        bool showBody = _channelsExpanded;
        if (_toolbarRow != null && GodotObject.IsInstanceValid(_toolbarRow))
            _toolbarRow.Visible = showBody;
        if (_matrixBody != null && GodotObject.IsInstanceValid(_matrixBody))
            _matrixBody.Visible = showBody && !_listView;
        if (_routingListScroll != null && GodotObject.IsInstanceValid(_routingListScroll))
            _routingListScroll.Visible = showBody && _listView;

        ApplyCollapseButtonUi();
    }

    /// <summary>Updates the top-left collapse control caption and tooltip.</summary>
    private void ApplyCollapseButtonUi()
    {
        if (_collapseButton == null || !GodotObject.IsInstanceValid(_collapseButton))
            return;
        _collapseButton.Text = _channelsExpanded ? "▼" : "▶";
        UiLocalizer.SetTooltip(_collapseButton,
            _channelsExpanded ? "Hide routing" : "Show routing");
    }

    private void RecordPatchHistory(string description, string coalesceKey = null)
    {
        if (_isDisposed || _globalData?.HistoryManager == null) return;
        if (_globalData.HistoryManager.IsRestoring) return;
        _globalData.HistoryManager.RecordSettingsChange(description, coalesceKey, "AudioPatch", "AudioDevices");
    }

    private void DeletePatchButtonPressed()
    {
        if (_isDisposed || !GodotObject.IsInstanceValid(this) || Patch == null || !GodotObject.IsInstanceValid(Patch))
            return;
        if (_globalData?.HistoryManager?.IsRestoring == true)
            return;

        if (_activeDeleteDialog != null && GodotObject.IsInstanceValid(_activeDeleteDialog))
            return;

        int patchId = Patch.Id;
        string patchName = Patch.Name ?? $"Patch {patchId}";
        var usage = CueResourceUsage.FindCuesUsingAudioPatch(patchId);

        if (usage.Count == 0)
        {
            PerformPatchDelete(patchId, reassign: null);
            return;
        }

        var alternatives = _globalData.Settings.GetAudioOutputPatches()
            .Where(p => p.Key != patchId && p.Value != null && GodotObject.IsInstanceValid(p.Value))
            .OrderBy(p => p.Value.Name ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .Select(p => (p.Key, p.Value.Name ?? $"Patch {p.Key}"))
            .ToList();

        var dialog = ResourceInUseDeleteDialog.Create(out string loadErr);
        if (dialog == null)
        {
            _globalSignals?.EmitSignal(nameof(GlobalSignals.Log),
                $"Failed to open delete dialog: {loadErr}", 2);
            return;
        }

        _activeDeleteDialog = dialog;
        dialog.Configure("audio output patch", patchName, usage.Cues, alternatives);
        dialog.Confirmed += result => OnPatchDeleteDialogConfirmed(patchId, result);
        dialog.Cancelled += () =>
        {
            if (_activeDeleteDialog == dialog) _activeDeleteDialog = null;
        };
        dialog.TreeExiting += () =>
        {
            if (_activeDeleteDialog == dialog) _activeDeleteDialog = null;
        };

        GetTree()?.Root?.AddChild(dialog);
        dialog.ShowConfigured();
    }

    private void OnPatchDeleteDialogConfirmed(int patchId, ResourceInUseDeleteResult result)
    {
        if (_activeDeleteDialog != null)
            _activeDeleteDialog = null;

        if (result == null || result.Action == ResourceInUseDeleteAction.Cancel)
            return;

        if (_isDisposed || !GodotObject.IsInstanceValid(this))
            return;

        var usingCues = CueResourceUsage.FindCuesUsingAudioPatch(patchId).Cues;
        Action reassign = null;

        if (result.Action == ResourceInUseDeleteAction.Unassign)
        {
            reassign = () => CueResourceUsage.UnassignAudioPatch(usingCues, patchId);
        }
        else if (result.Action == ResourceInUseDeleteAction.Replace)
        {
            if (!_globalData.Settings.GetAudioOutputPatches().TryGetValue(result.ReplaceWithId, out var replacement)
                || replacement == null || !GodotObject.IsInstanceValid(replacement))
            {
                _globalSignals?.EmitSignal(nameof(GlobalSignals.Log),
                    $"Cannot replace patch: target id {result.ReplaceWithId} not found.", 2);
                return;
            }
            reassign = () => CueResourceUsage.ReplaceAudioPatch(usingCues, patchId, replacement);
        }

        PerformPatchDelete(patchId, reassign);
    }

    private void PerformPatchDelete(int patchId, Action reassign)
    {
        RecordPatchHistory("Delete audio output patch");
        if (reassign != null)
        {
            _globalData?.HistoryManager?.RecordCuelistChange("Reassign cues after patch delete");
            reassign.Invoke();
        }

        _globalData.Settings.DeletePatch(patchId);
        GetNodeOrNull<MediaHealthService>("/root/MediaHealthService")?.RecheckAllQuiet();
        _globalSignals?.EmitSignal(nameof(GlobalSignals.SyncShellInspector));
        QueueFree();
    }

    private void SyncAudioDeviceDisplays()
    {
        if (_isDisposed || !GodotObject.IsInstanceValid(this) || Patch == null || !GodotObject.IsInstanceValid(Patch))
            return;
        if (_globalData?.HistoryManager?.IsRestoring == true)
            return;
        if (_isRebuilding)
            return;

        _isRebuilding = true;
        try
        {
            _availableDeviceList = _audioDevices.GetAvailableAudioDeviceNames() ?? new List<string>();
            var availableSet = new HashSet<string>(_availableDeviceList, StringComparer.Ordinal);

            foreach (var deviceName in Patch.OutputDevices.Keys)
            {
                int count = Patch.OutputDevices[deviceName]?.Count ?? 0;
                EnsureCollapseDefaults(deviceName, count);
                if (!availableSet.Contains(deviceName))
                {
                    _globalSignals.EmitSignal(nameof(GlobalSignals.Log),
                        $"Device used in audio patch but not found: {deviceName}", 3);
                }
            }

            RebuildChannelRows();
            _view = PatchMatrixViewBuilder.Build(Patch, _availableDeviceList, _collapsedGroups);
            ApplyViewToGrid();
            PopulateAddDeviceOption();
            RebuildRoutingList();
        }
        finally
        {
            _isRebuilding = false;
        }
    }

    private void ApplyViewToGrid()
    {
        if (_view == null)
            return;

        float headerH = _view.HeaderHeight;
        float bodyH = Math.Max(_view.BodyHeight, PatchMatrixMetrics.CellSize);
        if (_headerCorner != null && GodotObject.IsInstanceValid(_headerCorner))
            _headerCorner.CustomMinimumSize = new Vector2(PatchMatrixMetrics.RowHeaderWidth, headerH);
        if (_headerScroll != null && GodotObject.IsInstanceValid(_headerScroll))
            _headerScroll.CustomMinimumSize = new Vector2(0, headerH);
        if (_channelScroll != null && GodotObject.IsInstanceValid(_channelScroll))
            _channelScroll.CustomMinimumSize = new Vector2(0, bodyH);
        if (_cellScroll != null && GodotObject.IsInstanceValid(_cellScroll))
            _cellScroll.CustomMinimumSize = new Vector2(0, bodyH);

        _columnHeader?.ApplyView(_view);
        _cells?.ApplyView(Patch, _view);
    }

    private void RebuildChannelRows()
    {
        _channelRows.Clear();
        foreach (Node child in _channelList.GetChildren().ToArray())
        {
            _channelList.RemoveChild(child);
            child.QueueFree();
        }

        if (Patch?.Channels == null)
            return;

        foreach (var channel in Patch.Channels.OrderBy(kv => kv.Key))
            NewChannelRow(channel);
    }

    private void NewChannelRow(KeyValuePair<int, string> channel)
    {
        var channelHBox = new HBoxContainer();
        channelHBox.Name = $"{channel.Key}HBox";
        channelHBox.CustomMinimumSize = new Vector2(0, PatchMatrixMetrics.CellSize);
        _channelList.AddChild(channelHBox);
        _channelRows.Add(channelHBox);

        var deleteChannelButton = new Button();
        deleteChannelButton.CustomMinimumSize = new Vector2(PatchMatrixMetrics.CellSize, PatchMatrixMetrics.CellSize);
        deleteChannelButton.SetMouseFilter(MouseFilterEnum.Pass);
        deleteChannelButton.TooltipText = UiLocalizer.T("Delete this channel");
        deleteChannelButton.Icon = GetThemeIcon("DeleteBin", "AtlasIcons");
        deleteChannelButton.ExpandIcon = true;
        deleteChannelButton.FocusMode = FocusModeEnum.None;
        deleteChannelButton.AddThemeConstantOverride("icon_max_width", 13);
        deleteChannelButton.IconAlignment = HorizontalAlignment.Center;
        channelHBox.AddChild(deleteChannelButton);

        int channelId = channel.Key;
        deleteChannelButton.Pressed += () =>
        {
            if (_isDisposed || !GodotObject.IsInstanceValid(this) || Patch == null || !GodotObject.IsInstanceValid(Patch))
                return;
            RecordPatchHistory("Delete patch channel");
            Patch.RemoveChannel(channelId);
            SyncAudioDeviceDisplays();
        };

        var channelLabel = new LineEdit();
        channelLabel.Text = channel.Value;
        channelLabel.SetMaxLength(24);
        channelLabel.SetHSizeFlags(SizeFlags.ExpandFill);
        channelLabel.SetHorizontalAlignment(HorizontalAlignment.Right);
        channelLabel.CustomMinimumSize = new Vector2(0, PatchMatrixMetrics.CellSize);
        channelLabel.SetMouseFilter(MouseFilterEnum.Pass);
        channelLabel.TooltipText = UiLocalizer.Tf(
            "Channel: {0}, cues get routed to this channel. From here you route this to a physical output device.",
            channel.Value);
        channelHBox.AddChild(channelLabel);

        string chCoalesceKey = $"settings:patch:{Patch.Id}:ch:{channelId}:name";
        channelLabel.TextChanged += newText =>
        {
            if (_isDisposed || !GodotObject.IsInstanceValid(this) || Patch == null || !GodotObject.IsInstanceValid(Patch))
                return;
            try
            {
                RecordPatchHistory("Rename patch channel", chCoalesceKey);
                Patch.RenameChannel(channelId, newText);
                RebuildRoutingList();
            }
            catch (Exception ex)
            {
                _globalSignals.EmitSignal(nameof(GlobalSignals.Log), $"Failed to rename channel {channelId}: {ex.Message}", 2);
                GD.PrintErr($"AudioOutputPatchMatrix:NewChannelRow - Rename exception: {ex}");
            }
        };
        channelLabel.TextSubmitted += _ => channelLabel.ReleaseFocus();
        channelLabel.FocusExited += () =>
            _globalData?.HistoryManager?.EndCoalesceSession(chCoalesceKey);
    }

    private void RebuildRoutingList()
    {
        if (_routingList == null || !GodotObject.IsInstanceValid(_routingList) || Patch == null)
            return;

        foreach (Node child in _routingList.GetChildren().ToArray())
        {
            _routingList.RemoveChild(child);
            child.QueueFree();
        }

        var entries = Patch.GetRoutingList();
        if (entries.Count == 0)
        {
            var empty = new Label { Text = UiLocalizer.T("This patch has no buses yet.") };
            _routingList.AddChild(empty);
            return;
        }

        foreach (var (busId, busName, destinations) in entries)
        {
            _ = busId;
            var row = new Label();
            row.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            if (destinations == null || destinations.Count == 0)
            {
                row.Text = UiLocalizer.Tf("{0}  →  (none)", busName);
            }
            else
            {
                row.Text = UiLocalizer.Tf("{0}  →  {1}", busName, string.Join(", ", destinations));
            }
            _routingList.AddChild(row);
        }

        if (_routingListScroll != null && GodotObject.IsInstanceValid(_routingListScroll))
        {
            float listH = Math.Max(PatchMatrixMetrics.CellSize, _routingList.GetCombinedMinimumSize().Y);
            _routingListScroll.CustomMinimumSize = new Vector2(0, listH);
        }
    }

    private void AddChannelButtonPressed()
    {
        if (_isDisposed || !GodotObject.IsInstanceValid(this) || Patch == null || !GodotObject.IsInstanceValid(Patch))
            return;
        RecordPatchHistory("Add patch channel");
        Patch.NewChannel("New Channel", out var error);
        if (error != null)
        {
            _globalSignals.EmitSignal(nameof(GlobalSignals.Log), error, 2);
            return;
        }
        SyncAudioDeviceDisplays();
    }

    /// <summary>
    /// Fills the Add dropdown with playback devices not already in this patch.
    /// First item is a placeholder; selecting a real device adds every hardware channel unrouted.
    /// </summary>
    private void PopulateAddDeviceOption()
    {
        if (_addDeviceOption == null || !GodotObject.IsInstanceValid(_addDeviceOption) || Patch == null)
            return;

        _syncingAddDeviceUi = true;
        _addDeviceOption.Clear();
        _addDeviceOption.AddItem(UiLocalizer.T("Select device to add…"));
        _addDeviceOption.SetItemMetadata(0, "");
        _addDeviceOption.SetItemDisabled(0, true);

        var unused = new List<string>();
        foreach (var name in _availableDeviceList)
        {
            if (!Patch.OutputDevices.ContainsKey(name))
                unused.Add(name);
        }

        if (unused.Count == 0)
        {
            int idx = _addDeviceOption.ItemCount;
            string label = _availableDeviceList.Count == 0
                ? UiLocalizer.T("(No output devices found)")
                : UiLocalizer.T("(All available devices added)");
            _addDeviceOption.AddItem(label);
            _addDeviceOption.SetItemMetadata(idx, "");
            _addDeviceOption.SetItemDisabled(idx, true);
        }
        else
        {
            foreach (string name in unused)
            {
                int idx = _addDeviceOption.ItemCount;
                _addDeviceOption.AddItem(name);
                _addDeviceOption.SetItemMetadata(idx, name);
            }
        }

        _addDeviceOption.Select(0);
        _syncingAddDeviceUi = false;
    }

    private void OnAddDeviceSelected(long index)
    {
        if (_syncingAddDeviceUi || _isDisposed || Patch == null || !GodotObject.IsInstanceValid(Patch))
            return;
        if (index <= 0 || _addDeviceOption.IsItemDisabled((int)index))
        {
            _addDeviceOption.Select(0);
            return;
        }

        var meta = _addDeviceOption.GetItemMetadata((int)index);
        string deviceName = meta.AsString();
        _addDeviceOption.Select(0);
        if (string.IsNullOrEmpty(deviceName))
            return;

        AddDeviceToPatch(deviceName);
    }

    /// <summary>
    /// Opens the device and adds every hardware channel with no routes.
    /// </summary>
    /// <param name="name">Playback device name.</param>
    private void AddDeviceToPatch(string name)
    {
        if (_isDisposed || Patch == null || !GodotObject.IsInstanceValid(Patch))
            return;
        if (_globalData?.HistoryManager?.IsRestoring == true)
            return;
        if (Patch.OutputDevices.ContainsKey(name))
            return;

        _globalSignals.AudioDevicesChanged -= SyncAudioDeviceDisplays;
        try
        {
            RecordPatchHistory("Enable patch device");
            var enabledDevice = _audioDevices.OpenAudioDevice(name, out string error);
            if (enabledDevice == null)
            {
                _globalSignals.EmitSignal(nameof(GlobalSignals.Log),
                    $"Failed to enable audio device '{name}': {error}", 2);
                return;
            }

            int channelCount = Math.Max(1, enabledDevice.Channels);
            Patch.AddDeviceOutputs(name, channelCount);
            EnsureCollapseDefaults(name, channelCount);
            SyncAudioDeviceDisplays();
        }
        finally
        {
            if (!_isDisposed && GodotObject.IsInstanceValid(this))
                _globalSignals.AudioDevicesChanged += SyncAudioDeviceDisplays;
        }
    }

    private void RemoveDeviceFromPatch(string deviceName)
    {
        if (_isDisposed || Patch == null || !GodotObject.IsInstanceValid(Patch))
            return;
        if (_globalData?.HistoryManager?.IsRestoring == true)
            return;
        if (string.IsNullOrEmpty(deviceName) || !Patch.OutputDevices.ContainsKey(deviceName))
            return;

        RecordPatchHistory("Disable patch device");
        Patch.RemoveOutputDevice(deviceName);
        _collapsedGroups.Remove(deviceName);
        _collapseInitialized.Remove(deviceName);
        SyncAudioDeviceDisplays();
    }

    private void EnsureCollapseDefaults(string deviceName, int outputCount)
    {
        if (string.IsNullOrEmpty(deviceName) || _collapseInitialized.Contains(deviceName))
            return;
        _collapseInitialized.Add(deviceName);
        if (outputCount <= PatchMatrixMetrics.GroupSize)
            return;

        int groups = (outputCount + PatchMatrixMetrics.GroupSize - 1) / PatchMatrixMetrics.GroupSize;
        var set = new HashSet<int>();
        for (int g = 1; g < groups; g++)
            set.Add(g);
        _collapsedGroups[deviceName] = set;
    }

    private void ToggleGroup(string deviceName, int groupIndex)
    {
        if (!_collapsedGroups.TryGetValue(deviceName, out var set) || set == null)
        {
            set = new HashSet<int>();
            _collapsedGroups[deviceName] = set;
        }

        if (!set.Add(groupIndex))
            set.Remove(groupIndex);

        _collapseInitialized.Add(deviceName);
        _view = PatchMatrixViewBuilder.Build(Patch, _availableDeviceList, _collapsedGroups);
        ApplyViewToGrid();
    }

    private void ExpandGroup(string deviceName, int groupIndex)
    {
        if (_collapsedGroups.TryGetValue(deviceName, out var set) && set != null)
            set.Remove(groupIndex);
        _view = PatchMatrixViewBuilder.Build(Patch, _availableDeviceList, _collapsedGroups);
        ApplyViewToGrid();
    }

    private void OnCellPainted(int row, int col, bool routed)
    {
        if (_isDisposed || Patch == null || !GodotObject.IsInstanceValid(Patch) || _view == null)
            return;
        if (_globalData?.HistoryManager?.IsRestoring == true)
            return;
        if (col < 0 || col >= _view.Columns.Count || row < 0 || row >= _view.BusIds.Count)
            return;

        var column = _view.Columns[col];
        if (column.IsGroupPlaceholder || column.OutputIndex < 0)
            return;

        int busId = _view.BusIds[row];
        bool current = Patch.IsChannelRouted(column.DeviceName, column.OutputIndex, busId);
        if (current == routed)
            return;

        RecordPatchHistory(routed ? "Route patch channel" : "Unroute patch channel",
            $"settings:patch:{Patch.Id}:route-paint");
        try
        {
            Patch.SetRouting(column.DeviceName, column.OutputIndex, busId, routed);
        }
        catch (Exception ex)
        {
            _globalSignals.EmitSignal(nameof(GlobalSignals.Log),
                $"Error updating channel routing: {ex.Message}", 2);
        }

        RebuildRoutingList();
        _cells?.QueueRedraw();
    }

    private void OnPaintStarted()
    {
        if (Patch == null) return;
        RecordPatchHistory("Route patch channel", $"settings:patch:{Patch.Id}:route-paint");
    }

    private void OnPaintEnded()
    {
        if (Patch == null) return;
        _globalData?.HistoryManager?.EndCoalesceSession($"settings:patch:{Patch.Id}:route-paint");
        RebuildRoutingList();
    }

    private void OnCellHoverChanged(int row, int col)
    {
        if (_columnHeader != null && GodotObject.IsInstanceValid(_columnHeader))
            _columnHeader.HoverCol = col;

        for (int i = 0; i < _channelRows.Count; i++)
        {
            var box = _channelRows[i];
            if (box == null || !GodotObject.IsInstanceValid(box))
                continue;
            box.Modulate = i == row ? new Color(1.2f, 1.2f, 1.2f) : Colors.White;
        }
    }

    private void OnCellHScroll(double v)
    {
        if (_syncingScroll) return;
        _syncingScroll = true;
        if (_headerScroll != null && GodotObject.IsInstanceValid(_headerScroll))
            _headerScroll.ScrollHorizontal = (int)Math.Round(v);
        _syncingScroll = false;
    }

    private void OnCellVScroll(double v)
    {
        if (_syncingScroll) return;
        _syncingScroll = true;
        if (_channelScroll != null && GodotObject.IsInstanceValid(_channelScroll))
            _channelScroll.ScrollVertical = (int)Math.Round(v);
        _syncingScroll = false;
    }

    private void OnHeaderHScroll(double v)
    {
        if (_syncingScroll) return;
        _syncingScroll = true;
        if (_cellScroll != null && GodotObject.IsInstanceValid(_cellScroll))
            _cellScroll.ScrollHorizontal = (int)Math.Round(v);
        _syncingScroll = false;
    }

    private void OnChannelVScroll(double v)
    {
        if (_syncingScroll) return;
        _syncingScroll = true;
        if (_cellScroll != null && GodotObject.IsInstanceValid(_cellScroll))
            _cellScroll.ScrollVertical = (int)Math.Round(v);
        _syncingScroll = false;
    }

    private void PatchNameOnTextChanged(string newtext)
    {
        if (_isDisposed || !GodotObject.IsInstanceValid(this) || Patch == null || !GodotObject.IsInstanceValid(Patch))
            return;
        _globalData?.HistoryManager?.RecordSettingsChange("Rename audio output patch",
            $"settings:patch:{Patch.Id}:name", "AudioPatch");
        Patch.Name = newtext;
        _globalData.Settings.UpdatePatch(Patch);
    }

    private void OnPatchNameFocusExited()
    {
        if (Patch == null) return;
        _globalData?.HistoryManager?.EndCoalesceSession($"settings:patch:{Patch.Id}:name");
    }

    private void OnRefreshButtonPressed()
    {
        if (_isDisposed || !GodotObject.IsInstanceValid(this) || Patch == null || !GodotObject.IsInstanceValid(Patch))
            return;
        SyncAudioDeviceDisplays();
    }

    private void FreeDynamicChildren(Node parent)
    {
        if (parent == null || !GodotObject.IsInstanceValid(parent)) return;
        foreach (Node child in parent.GetChildren())
        {
            if (GodotObject.IsInstanceValid(child))
                child.QueueFree();
        }
    }
}
