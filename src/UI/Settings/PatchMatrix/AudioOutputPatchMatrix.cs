// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using Godot;
using System;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;
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
using Cue2.UI.Popups;
using Cue2.UI.Utilities;

namespace Cue2.UI.Settings.PatchMatrix;

/// <summary>
/// Manages the UI for an audio output patch matrix, allowing users to configure routing between channels and devices.
/// </summary>
public partial class AudioOutputPatchMatrix : Control
{
    [Export] private AudioOutputPatch Patch { get; set; } // This is set in SettingsAudioOutputPatch when created. 
    
    [Export] private int PatchId { get; set; }
    
    private GlobalData _globalData;
    private GlobalSignals _globalSignals;
    private AudioDevices _audioDevices;
    
    private List<string> _availableDeviceList;
    
    private PackedScene _deviceHeaderScene;
    private PackedScene _deviceOutputHeaderScene;
    private PackedScene _checkBoxScene;
    
    
    private HBoxContainer _deviceContainer;
    private VBoxContainer _channelList;
    private GridContainer _patchMatrix;
    private LineEdit _patchName;
    private LineEdit _patchVolumeInput;
    private Button _deletePatchButton;
    private Button _addChannelButton;
    private bool _isSyncingVolumeUi;

    /// <summary>Device-header strip (columns) — part of the expandable channel/routing body.</summary>
    private Control _deviceHeadersContainer;
    /// <summary>Channel name list + Add Channel.</summary>
    private Control _channelListContainer;
    /// <summary>Routing checkbox grid panel.</summary>
    private Control _matrixPanelContainer;

    private Control _expandedChrome;
    private Control _collapsedChrome;
    private Button _expandedButton;
    private Button _collapsedButton;

    /// <summary>
    /// When true, channel list + device headers + routing matrix are visible.
    /// When false, only the patch header (name / refresh / delete / accordion) is shown.
    /// </summary>
    private bool _channelsExpanded = true;

    private bool _isRebuilding;
    private bool _isDisposed;
    private ResourceInUseDeleteDialog _activeDeleteDialog;
    /// <summary>Bumped on each patch-matrix rebuild so stale async completions abort (P2-08).</summary>
    private int _patchMatrixBuildGeneration;
    /// <summary>Structure fingerprint of last successful checkbox grid build.</summary>
    private string _patchMatrixStructureKey;
    
    /// <summary>
    /// Initializes the node, loads required scenes, sets up UI elements, and connects signals.
    /// </summary>
    public override void _Ready()
    {
        _globalData = GetNode<GlobalData>("/root/GlobalData");
        _globalSignals = GetNode<GlobalSignals>("/root/GlobalSignals"); // Global
        _audioDevices = GetNode<AudioDevices>("/root/AudioDevices");
        
        
        // This is "PatchMatrixDeviceHeader" header
        _deviceHeaderScene = SceneLoader.LoadPackedScene("uid://cisr40jsg2jgp", out string _);
        
        // This is "PatchMatrixDeviceOutputHeader"
        _deviceOutputHeaderScene = SceneLoader.LoadPackedScene("uid://bmi0eibnauemp", out string _);
        
        // This is "AudioPatchMatrixCheckBox"
        _checkBoxScene = SceneLoader.LoadPackedScene("uid://cbdaknpeq3im1", out string _); // Check box
            
            
        _deviceContainer = GetNode<HBoxContainer>("%DeviceOutputsListHBoxContainer");
        _patchMatrix = GetNode<GridContainer>("%PatchMatrixContainer");
        _channelList = GetNode<VBoxContainer>("%ChannelList");
        _deviceHeadersContainer = GetNode<Control>("%TopContainer");
        _channelListContainer = GetNode<Control>("%LeftContainer");
        _matrixPanelContainer = GetNode<Control>("%MatrixPanelContainer");

        _expandedChrome = GetNode<Control>("%Expanded");
        _collapsedChrome = GetNode<Control>("%Collapsed");
        _expandedButton = GetNode<Button>("%ExpandedButton");
        _collapsedButton = GetNode<Button>("%CollapsedButton");
        _expandedButton.Pressed += OnCollapseChannelsPressed;
        _collapsedButton.Pressed += OnExpandChannelsPressed;
        
        
        // Load its patch info
        GD.Print($"AudioOutputPatchMatrix:_Ready - Patch matrix loaded with id: {PatchId} and name: {Patch?.Name}");

        _patchName = GetNode<LineEdit>("%PatchName");
        _patchName.Text = Patch?.Name ?? "Unnamed";
        _patchName.TextChanged += PatchNameOnTextChanged;
        _patchName.TextSubmitted += _ => _patchName.ReleaseFocus();
        _patchName.FocusExited += OnPatchNameFocusExited;

        _patchVolumeInput = GetNode<LineEdit>("%PatchVolumeInput");
        _patchVolumeInput.TextSubmitted += OnPatchVolumeSubmitted;
        _patchVolumeInput.FocusExited += OnPatchVolumeFocusExited;
        LineEditDbDragSlider.EnableVolume(_patchVolumeInput);
        SyncPatchVolumeUi();
        
        _deletePatchButton = GetNode<Button>("%DeletePatchButton");
        _deletePatchButton.Pressed += DeletePatchButtonPressed;

        _addChannelButton = GetNode<Button>("%AddChannelButton");
        _addChannelButton.Pressed += AddChannelButtonPressed;
        
        // Signal from AudioDevices events (hotplug etc). We unsubscribe in _ExitTree.
        _globalSignals.AudioDevicesChanged += SyncAudioDeviceDisplays;

        ApplyChannelsExpandedUi();
        SyncAudioDeviceDisplays();
    }

    /// <summary>
    /// Formats and displays the patch sub-master volume in dB without recording history.
    /// </summary>
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

    /// <summary>Formats linear patch volume as a dB LineEdit string (e.g. "0.0dB").</summary>
    private static string FormatPatchVolumeDb(float linear) =>
        $"{UiUtilities.LinearToDb(Mathf.Clamp(linear, 0f, 1f))}dB";

    private void OnPatchVolumeSubmitted(string text)
    {
        CommitPatchVolume(text);
    }

    private void OnPatchVolumeFocusExited()
    {
        if (_isSyncingVolumeUi || _patchVolumeInput == null || !GodotObject.IsInstanceValid(_patchVolumeInput))
            return;
        CommitPatchVolume(_patchVolumeInput.Text);
    }

    /// <summary>
    /// Parses dB text, updates <see cref="AudioOutputPatch.Volume"/>, and records settings history.
    /// Live playback already multiplies by this value in <c>AudioMixMatrix</c>.
    /// </summary>
    /// <param name="text">Submitted volume text (e.g. "-6dB" or "-6").</param>
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

            // Match AudioInspector / master volume: positive values treated as attenuation.
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

    /// <summary>
    /// Collapses the channel list, device headers, and routing matrix (header chrome only).
    /// </summary>
    private void OnCollapseChannelsPressed()
    {
        if (!_channelsExpanded) return;
        _channelsExpanded = false;
        ApplyChannelsExpandedUi();
    }

    /// <summary>
    /// Expands the channel list, device headers, and routing matrix.
    /// </summary>
    private void OnExpandChannelsPressed()
    {
        if (_channelsExpanded) return;
        _channelsExpanded = true;
        ApplyChannelsExpandedUi();
    }

    /// <summary>
    /// Applies accordion visibility for channel/routing body and chevron chrome.
    /// </summary>
    private void ApplyChannelsExpandedUi()
    {
        bool showBody = _channelsExpanded;

        if (_deviceHeadersContainer != null && GodotObject.IsInstanceValid(_deviceHeadersContainer))
            _deviceHeadersContainer.Visible = showBody;
        if (_channelListContainer != null && GodotObject.IsInstanceValid(_channelListContainer))
            _channelListContainer.Visible = showBody;
        if (_matrixPanelContainer != null && GodotObject.IsInstanceValid(_matrixPanelContainer))
            _matrixPanelContainer.Visible = showBody;

        // Expanded chrome = chevron-down (click to collapse). Collapsed chrome = chevron-right (click to expand).
        if (_expandedChrome != null && GodotObject.IsInstanceValid(_expandedChrome))
            _expandedChrome.Visible = showBody;
        if (_collapsedChrome != null && GodotObject.IsInstanceValid(_collapsedChrome))
            _collapsedChrome.Visible = !showBody;
    }

    /// <summary>
    /// Records a full audio-patch table snapshot before a user mutation (settings-scoped history).
    /// </summary>
    private void RecordPatchHistory(string description, string coalesceKey = null)
    {
        if (_isDisposed || _globalData?.HistoryManager == null) return;
        if (_globalData.HistoryManager.IsRestoring) return;
        _globalData.HistoryManager.RecordSettingsChange(description, coalesceKey, "AudioPatch", "AudioDevices");
    }

    /// <summary>
    /// Handles the deletion of the current patch and removes the UI node.
    /// If cues still use the patch, prompts to unassign or replace before deleting.
    /// </summary>
    private void DeletePatchButtonPressed()
    {
        if (_isDisposed || !GodotObject.IsInstanceValid(this) || Patch == null || !GodotObject.IsInstanceValid(Patch))
            return;
        if (_globalData?.HistoryManager?.IsRestoring == true)
            return;

        // Avoid stacking multiple dialogs for the same matrix
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

        // Same flow as FileDropPopup: Create → Configure → AddChild → ShowConfigured
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

    /// <summary>
    /// Records history, optionally reassigns cues, deletes the patch, and frees this matrix UI.
    /// </summary>
    private void PerformPatchDelete(int patchId, Action reassign)
    {
        // Capture settings (with patch) then cuelist (with assignments) so undo restores both.
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

    
    /// <summary>
    /// Synchronizes the displayed audio devices and channels with the current data, rebuilding the UI as needed.
    /// </summary>
    private void SyncAudioDeviceDisplays()
    {
    	TaskUtil.Run(SyncAudioDeviceDisplaysAsync, "AudioOutputPatchMatrix.SyncAudioDeviceDisplays");
    }

    private async Task SyncAudioDeviceDisplaysAsync()
    {
        if (_isDisposed || !GodotObject.IsInstanceValid(this) || Patch == null || !GodotObject.IsInstanceValid(Patch))
            return;
        // Skip mid-history restore: Settings frees/recreates patches and the parent panel rebuilds UIs after.
        if (_globalData?.HistoryManager?.IsRestoring == true)
            return;
        if (_isRebuilding)
            return;

        _isRebuilding = true;
        try
        {
            GD.Print("AudioOutputPatchMatrix:SyncAudioDeviceDisplays - Syncing devices in audio output patch matrix");
            // For now we remove devices and start fresh while developing, in future match against info instead.

            var deviceHeaders = _deviceContainer.GetChildren();
            foreach (var deviceHeader in deviceHeaders)
            {
                deviceHeader.QueueFree();
            }
            var channelRows = _channelList.GetChildren();
            foreach (var channelRow in channelRows)
            {
                if (channelRow.Name == "AddChannelButton") continue; // Exempt add channel button from being deleted.
                channelRow.QueueFree();
            }

            await ToSignal(GetTree(), "process_frame");

            if (_isDisposed || !GodotObject.IsInstanceValid(this)
                || Patch == null || !GodotObject.IsInstanceValid(Patch)
                || _globalData?.HistoryManager?.IsRestoring == true)
                return;

            var available = _audioDevices.GetAvailableAudioDeviceNames() ?? new List<string>();
            _availableDeviceList = available;

            // CHANNELS (ROWS)
            var sortedChannels = Patch.Channels.OrderBy(kv => kv.Key).ToList();
            foreach (var channel in sortedChannels)
            {
                NewChannelRow(channel);
            }

            // DEVICES (COLUMNS)
            // Copy to avoid mutating the original available list when removing used devices.
            var unusedDeviceList = new List<string>(_availableDeviceList);

            foreach (var device in Patch.OutputDevices)
            {
                if (_availableDeviceList.Contains(device.Key))
                {
                    NewUsedDeviceColumn(device.Key, device.Value);
                    unusedDeviceList.Remove(device.Key);
                }
                else
                {
                    NewUsedButNotFoundDeviceColumn(device.Key, device.Value);
                    _globalSignals.EmitSignal(nameof(GlobalSignals.Log), $"Device used in audio patch but not found: {device.Key}", 3);
                    unusedDeviceList.Remove(device.Key);
                }
            }

            foreach (var device in unusedDeviceList)
            {
                NewUnusedDeviceColumn(device);
            }

            // Then build all checkboxes between cue output channels and devices/device channels.
            BuildPatchMatrix();
        }
        finally
        {
            _isRebuilding = false;
        }
    }

    
    /// <summary>
    /// Creates a new UI row for a channel, including delete button and editable label.
    /// </summary>
    /// <param name="channel">The channel key-value pair (ID and name).</param>
    private void NewChannelRow(KeyValuePair<int, string> channel)
    {
        HBoxContainer channelHBox = new HBoxContainer();
        channelHBox.Name = $"{channel.Key}HBox";
        _channelList.AddChild(channelHBox);
        int currentIndex = channelHBox.GetIndex();
        if (currentIndex > 0) _channelList.MoveChild(channelHBox, currentIndex - 1);
        Button deleteChannelButton = new Button();
        deleteChannelButton.CustomMinimumSize = new Vector2(32, 32);
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
        
        LineEdit channelLabel = new LineEdit();
        channelLabel.Text = channel.Value;
        channelHBox.AddChild(channelLabel);
        
        channelLabel.SetMaxLength(24);
        channelLabel.SetHSizeFlags(SizeFlags.ExpandFill);
        channelLabel.SetHorizontalAlignment(HorizontalAlignment.Right);
        channelLabel.CustomMinimumSize = new Vector2(0, 32);
        channelLabel.SetMouseFilter(MouseFilterEnum.Pass);
        channelLabel.TooltipText =
            $"Channel: {channel.Value}, cues get routed to this channel. " +
            $"From here you route this to a physical output device."; 
        
        string chCoalesceKey = $"settings:patch:{Patch.Id}:ch:{channelId}:name";
        channelLabel.TextChanged += newText =>
        {
            if (_isDisposed || !GodotObject.IsInstanceValid(this) || Patch == null || !GodotObject.IsInstanceValid(Patch))
                return;
            try
            {
                // Continuous rename session; sealed on focus exit.
                RecordPatchHistory("Rename patch channel", chCoalesceKey);
                Patch.RenameChannel(channelId, newText);
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
    
    private void NewUsedDeviceColumn(string deviceName, List<OutputChannel> outputChannels)
    {
        // Double-check the device is open (no-op + no emit if already open).
        _audioDevices.OpenAudioDevice(deviceName, out var _);
        
        
        var header = LoadDeviceOutputDeviceHeader(deviceName, true);
    
        var specs = _audioDevices.GetReadableAudioDeviceSpecs(deviceName);
        header.GetChild<Label>(1).TooltipText = deviceName;
        foreach (var spec in specs)
        {
            header.GetChild<Label>(1).TooltipText += "\n" + spec;
        }
        
        // Add device outputs
        AddDeviceOutputColumns(deviceName, outputChannels);
    }
    
    private void NewUsedButNotFoundDeviceColumn(string deviceName, List<OutputChannel> outputChannels)
    {
        var header = LoadDeviceOutputDeviceHeader(deviceName, true);
        var label = header.GetChild<Label>(1);
        label.TooltipText = UiLocalizer.Tf("{0}: Is used in patch but is currently unavailable.", deviceName);
        var style = new StyleBoxFlat();
        style.BgColor = new Color(1.0f, 0.0f, 0.0f, 0.5f);

        header.AddThemeStyleboxOverride("panel", style);

        var outputNodes = AddDeviceOutputColumns(deviceName, outputChannels);
        foreach (var outputNode in outputNodes)
        {
            outputNode.AddThemeStyleboxOverride("panel", style);
        }
    }
    
    /// <summary>
    /// Creates header for a device that is available but not enabled in this patch.
    /// </summary>
    private void NewUnusedDeviceColumn(string deviceName)
    {
        var header = LoadDeviceOutputDeviceHeader(deviceName);
        header.GetChild<Label>(1).TooltipText = UiLocalizer.Tf("{0}: Currently disabled (enable to use in patch)", deviceName);
    }


    private List<Panel> AddDeviceOutputColumns(string deviceName, List<OutputChannel> outputChannels)
    {
        var deviceOutputNodes = new List<Panel>();
        for (int outputIndex = 0; outputIndex < outputChannels.Count; outputIndex++)
        {
            var outHeader = _deviceOutputHeaderScene.Instantiate<Panel>();
            _deviceContainer.AddChild(outHeader);
            
            deviceOutputNodes.Add(outHeader);
            
            var outputNameEdit = outHeader.GetNode<LineEdit>("OutputName");
            outputNameEdit.Text = outputChannels[outputIndex].Name;
            outHeader.Set("ParentDevice", deviceName);
            outHeader.Set("OutputIndex", outputIndex);
        
            // Capture locals for the closure
            int capturedIndex = outputIndex;
            string outCoalesceKey = $"settings:patch:{Patch.Id}:dev:{deviceName}:out:{capturedIndex}:name";
            outputNameEdit.TextChanged += newText =>
            {
                if (_isDisposed || !GodotObject.IsInstanceValid(this) || Patch == null || !GodotObject.IsInstanceValid(Patch))
                    return;
                // Snapshot before rename; coalesce continuous typing on this field.
                RecordPatchHistory("Rename device output", outCoalesceKey);
                if (!Patch.RenameDeviceChannel(deviceName, capturedIndex, newText))
                {
                    // Revert to current (unchanged) name on failure — history may include a no-op step if rename rejected.
                    string currentName = Patch.GetDeviceOutputName(deviceName, capturedIndex);
                    if (currentName != null)
                    {
                        outputNameEdit.Text = currentName;
                    }
                    else
                    {
                        _globalSignals.EmitSignal(nameof(GlobalSignals.Log), $"Failed to revert output name for device '{deviceName}' at index {capturedIndex}", 2);
                    }
                }
            };
            outputNameEdit.TextSubmitted += _ => outputNameEdit.ReleaseFocus();
            outputNameEdit.FocusExited += () =>
                _globalData?.HistoryManager?.EndCoalesceSession(outCoalesceKey);
        }

        return deviceOutputNodes;
    }
    
    /// <summary>
    /// Adds a new default channel to the patch and refreshes the matrix.
    /// </summary>
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


    private Panel LoadDeviceOutputDeviceHeader(string name, bool state = false)
    {
        Panel instance = _deviceHeaderScene.Instantiate<Panel>();
        instance.Set("DeviceName", name);
        _deviceContainer.AddChild(instance);
        instance.GetNode<Label>("Label").Text = name;
        instance.Name = name; 
        
        CheckButton toggleDeviceButton = instance.GetNode<CheckButton>("ToggleDeviceButton");
        toggleDeviceButton.SetPressed(state);
        
        // Connect functions to the use device check button. 
        // Temporarily unsubscribe from AudioDevicesChanged during Open+Sync to avoid the
        // double-rebuild: OpenAudioDevice emits the signal, and we explicitly rebuild after model change.
        toggleDeviceButton.Toggled += pressed =>
        {
            if (_isDisposed || !GodotObject.IsInstanceValid(this) || Patch == null || !GodotObject.IsInstanceValid(Patch))
            {
                return;
            }
            if (_globalData?.HistoryManager?.IsRestoring == true)
                return;

            _globalSignals.AudioDevicesChanged -= SyncAudioDeviceDisplays;
            try
            {
                if (pressed)
                {
                    // Record before open/model change so undo restores prior routing + open set.
                    RecordPatchHistory(pressed ? "Enable patch device" : "Disable patch device");
                    AudioDevice enabledDevice = _audioDevices.OpenAudioDevice(name, out string error);
                    if (enabledDevice == null)
                    {
                        // Revert visual state without re-triggering Toggled
                        toggleDeviceButton.SetPressedNoSignal(false);
                        _globalSignals.EmitSignal(nameof(GlobalSignals.Log), $"Failed to enable audio device '{name}': {error}", 2);
                        return;
                    }

                    int outputCount = enabledDevice.Channels;
                    Patch.AddDeviceOutputs(name, outputCount);
                    instance.Set("DeviceId", enabledDevice.DeviceId);
                }
                else
                {
                    RecordPatchHistory("Disable patch device");
                    Patch.RemoveOutputDevice(name);
                }

                SyncAudioDeviceDisplays();
            }
            finally
            {
                if (!_isDisposed && GodotObject.IsInstanceValid(this))
                {
                    _globalSignals.AudioDevicesChanged += SyncAudioDeviceDisplays;
                }
            }
        };
        return instance;
    }

    /// <summary>
    /// Builds the matrix of checkboxes for routing channels to device outputs.
    /// </summary>
    private void BuildPatchMatrix()
    {
    	TaskUtil.Run(BuildPatchMatrixAsync, "AudioOutputPatchMatrix.BuildPatchMatrix");
    }

    private async Task BuildPatchMatrixAsync()
    {
        if (_isDisposed || !GodotObject.IsInstanceValid(this) || Patch == null || !GodotObject.IsInstanceValid(Patch))
            return;
        if (_globalData?.HistoryManager?.IsRestoring == true)
            return;

        int buildGen = ++_patchMatrixBuildGeneration;
        var deviceHeaders = _deviceContainer.GetChildren();
        int columnCount = deviceHeaders.Count;
        var sortedChannels = Patch.Channels.OrderBy(kv => kv.Key).ToList();

        // Structure: channel ids + device/output columns (P2-08 skip full free/rebuild).
        var structureSb = new System.Text.StringBuilder();
        structureSb.Append(columnCount).Append('|');
        foreach (var ch in sortedChannels)
            structureSb.Append(ch.Key).Append(',');
        structureSb.Append('|');
        for (int col = 0; col < columnCount; col++)
        {
            var header = deviceHeaders[col];
            var parentDeviceVar = header.Get("ParentDevice");
            if (parentDeviceVar.VariantType != Variant.Type.Nil)
                structureSb.Append(parentDeviceVar).Append(':').Append(header.Get("OutputIndex").AsInt32()).Append(';');
            else
                structureSb.Append("empty;");
        }
        string structureKey = structureSb.ToString();

        if (structureKey == _patchMatrixStructureKey
            && _patchMatrix.GetChildCount() == sortedChannels.Count * Math.Max(1, columnCount))
        {
            // Refresh checkbox pressed state only.
            int childIdx = 0;
            foreach (var channel in sortedChannels)
            {
                int channelId = channel.Key;
                for (int col = 0; col < columnCount; col++, childIdx++)
                {
                    if (childIdx >= _patchMatrix.GetChildCount()) return;
                    var child = _patchMatrix.GetChild(childIdx);
                    if (child is not CheckBox checkBox)
                        continue;
                    var header = deviceHeaders[col];
                    var parentDeviceVar = header.Get("ParentDevice");
                    if (parentDeviceVar.VariantType == Variant.Type.Nil)
                        continue;
                    string deviceName = parentDeviceVar.ToString();
                    int outputIndex = header.Get("OutputIndex").AsInt32();
                    bool routed = Patch.IsChannelRouted(deviceName, outputIndex, channelId);
                    if (checkBox.ButtonPressed != routed)
                        checkBox.SetPressedNoSignal(routed);
                }
            }
            return;
        }

        foreach (var child in _patchMatrix.GetChildren())
            child.QueueFree();

        await ToSignal(GetTree(), "process_frame");

        if (_isDisposed || !GodotObject.IsInstanceValid(this)
            || Patch == null || !GodotObject.IsInstanceValid(Patch)
            || _globalData?.HistoryManager?.IsRestoring == true
            || buildGen != _patchMatrixBuildGeneration)
            return;

        // Headers may have been rebuilt while we waited.
        deviceHeaders = _deviceContainer.GetChildren();
        columnCount = deviceHeaders.Count;
        sortedChannels = Patch.Channels.OrderBy(kv => kv.Key).ToList();
        _patchMatrix.Columns = columnCount;

        foreach (var channel in sortedChannels)
        {
            int channelId = channel.Key;

            for (int col = 0; col < columnCount; col++)
            {
                var header = deviceHeaders[col];
                var parentDeviceVar = header.Get("ParentDevice");
                if (parentDeviceVar.VariantType != Variant.Type.Nil)
                {
                    string deviceName = parentDeviceVar.ToString();
                    int outputIndex = header.Get("OutputIndex").AsInt32();

                    CheckBox checkBox = _checkBoxScene.Instantiate<CheckBox>();
                    checkBox.ButtonPressed = Patch.IsChannelRouted(deviceName, outputIndex, channelId);

                    checkBox.Toggled += pressed =>
                    {
                        if (_isDisposed || !GodotObject.IsInstanceValid(this) || Patch == null || !GodotObject.IsInstanceValid(Patch))
                            return;
                        if (_globalData?.HistoryManager?.IsRestoring == true)
                            return;
                        try
                        {
                            RecordPatchHistory(pressed
                                ? "Route patch channel"
                                : "Unroute patch channel");
                            Patch.SetRouting(deviceName, outputIndex, channelId, pressed);
                        }
                        catch (Exception ex)
                        {
                            _globalSignals.EmitSignal(nameof(GlobalSignals.Log),
                                $"Error updating channel routing: {ex.Message}", 2);
                        }
                    };

                    _patchMatrix.AddChild(checkBox);
                }
                else
                {
                    Control empty = new Control();
                    empty.CustomMinimumSize = new Vector2(32, 32);
                    _patchMatrix.AddChild(empty);
                }
            }
        }

        _patchMatrixStructureKey = structureKey;
    }
        
    
    /// <summary>
    /// Updates the patch name when the text in the LineEdit changes.
    /// </summary>
    /// <param name="newtext">The new text entered by the user.</param>
    private void PatchNameOnTextChanged(string newtext)
    {
        if (_isDisposed || !GodotObject.IsInstanceValid(this) || Patch == null || !GodotObject.IsInstanceValid(Patch))
            return;
        // Continuous typing session; sealed when the name field loses focus.
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
    
    /// <summary>
    /// Triggers a refresh of the audio device displays.
    /// </summary>
    private void _onRefreshButtonPressed()
    {
        if (_isDisposed || !GodotObject.IsInstanceValid(this) || Patch == null || !GodotObject.IsInstanceValid(Patch))
            return;
        SyncAudioDeviceDisplays();
    }
    
    

    /// <summary>
    /// Unsubscribes from global signals, disconnects handlers, and explicitly frees
    /// dynamically generated child nodes (channel rows, device headers/outputs, checkboxes,
    /// filler controls etc.) created during patch matrix building. This prevents leaks
    /// of Godot objects that were instantiated for the UI but might not be freed if
    /// parents are removed without cascading properly (especially on app quit).
    /// </summary>
    public override void _ExitTree()
    {
        _isDisposed = true;

        if (_globalSignals != null && GodotObject.IsInstanceValid(_globalSignals))
        {
            _globalSignals.AudioDevicesChanged -= SyncAudioDeviceDisplays;
        }

        // Disconnect direct child signal handlers (release any captured references in method groups).
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
        if (_expandedButton != null && GodotObject.IsInstanceValid(_expandedButton))
            _expandedButton.Pressed -= OnCollapseChannelsPressed;
        if (_collapsedButton != null && GodotObject.IsInstanceValid(_collapsedButton))
            _collapsedButton.Pressed -= OnExpandChannelsPressed;

        // Proactively QueueFree all objects we generated while building the matrix UI.
        // These include: channel HBoxes + their Buttons/LineEdits, device header Panels,
        // output header Panels, CheckBoxes for the matrix, and empty filler Controls.
        FreeDynamicChildren(_deviceContainer);
        FreeDynamicChildren(_channelList, skipName: "AddChannelButton");
        FreeDynamicChildren(_patchMatrix);
    }

    private void FreeDynamicChildren(Node parent, string skipName = null)
    {
        if (parent == null || !GodotObject.IsInstanceValid(parent)) return;

        var children = parent.GetChildren();
        foreach (Node child in children)
        {
            if (skipName != null && child.Name == skipName) continue;
            if (GodotObject.IsInstanceValid(child))
                child.QueueFree();
        }
    }
}
