// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Linq;
using Cue2.UI.Utilities;
using Godot;

namespace Cue2.UI.Inspectors;

/// <summary>
/// Audio inspector mode for <see cref="AudioInputComponent"/>: output, routing, duration, fades, pan, and volume.
/// </summary>
public partial class AudioInspector
{
    private int ActiveOutputPatchId =>
        _focusedAudioInput?.PatchId ?? _focusedAudioComponent?.PatchId ?? -1;

    private string ActiveDirectOutput =>
        _focusedAudioInput?.DirectOutput ?? _focusedAudioComponent?.DirectOutput;

    private string ActiveOutputKey => $"{ActiveOutputPatchId}|{ActiveDirectOutput ?? ""}";

    private float ActivePan => _focusedAudioInput?.Pan ?? _focusedAudioComponent?.Pan ?? 0f;

    private CuePatch ActiveRouting => _focusedAudioInput?.Routing ?? _focusedAudioComponent?.Routing;

    private void SetActiveRouting(CuePatch routing)
    {
        if (_focusedAudioInput != null)
            _focusedAudioInput.Routing = routing;
        else if (_focusedAudioComponent != null)
            _focusedAudioComponent.Routing = routing;
    }

    /// <summary>
    /// Shows the shared audio controls for an input component and hides file-only fields.
    /// </summary>
    /// <param name="input">Component to edit.</param>
    private void LoadAudioInputInspector(AudioInputComponent input)
    {
        _focusedAudioComponent = null;
        _focusedAudioInput = input;
        _audioTargets.Clear();
        if (_infoLabel != null)
        {
            _infoLabel.Visible = false;
            _infoLabel.Text = "";
        }

        SetSelectFileVisible(true);
        if (_inspectorContent != null)
            _inspectorContent.Visible = true;
        if (_deleteAudioComponentButton != null)
        {
            _deleteAudioComponentButton.Visible = true;
            _deleteAudioComponentButton.TooltipText = UiLocalizer.T("Remove the audio input from this cue.");
        }

        ApplyAudioInputInspectorLayout(true);
        SyncAudioInputFields();
        PopulateOutputOptions();
        if (_routingAccordian != null)
            _routingAccordian.Visible = true;
        if (_routingCollapseButton != null)
            _routingCollapseButton.Icon = GetThemeIcon("Down", "AtlasIcons");
        BuildRoutingMatrix();
        PopulateAudioInputOptions();
    }

    /// <summary>
    /// Shows file-only controls in file mode and hides them while an audio input is loaded.
    /// </summary>
    /// <param name="inputMode">True when editing an audio input component.</param>
    private void ApplyAudioInputInspectorLayout(bool inputMode)
    {
        var timeRow = _startTimeInput?.GetParent();
        if (timeRow != null)
        {
            foreach (Node child in timeRow.GetChildren())
            {
                if (child == _durationValue || child.Name == "DurationLabel")
                    continue;
                if (child is CanvasItem item)
                    item.Visible = !inputMode;
            }
        }

        if (_durationValue != null)
        {
            _durationValue.Editable = inputMode;
            _durationValue.TooltipText = inputMode
                ? UiLocalizer.T("How long the input stays active. 0 or blank = stay active until stopped.")
                : UiLocalizer.T("m:s:ms");
        }

        if (_loopInput != null)
            _loopInput.Visible = !inputMode;
        SetSiblingVisible(_loopInput, "LoopLabel", !inputMode);
        if (_playCountInput != null)
            _playCountInput.Visible = !inputMode;
        SetSiblingVisible(_playCountInput, "PlaycountLabel", !inputMode);
        if (_rateRow != null)
            _rateRow.Visible = !inputMode;

        if (_waveformCollapseButton?.GetParent() is CanvasItem waveformHeader)
            waveformHeader.Visible = !inputMode;
        if (_waveformAccordian != null && inputMode)
            _waveformAccordian.Visible = false;

        if (_buttonSelectFile != null)
            _buttonSelectFile.Visible = !inputMode;
        if (_fileUrl?.GetParent() is CanvasItem fileColumn)
            fileColumn.Visible = !inputMode;
        PlaceDeleteButton(inputMode);
        if (inputMode && _selectFileContainer != null)
            _selectFileContainer.Visible = false;
        if (_panLabel != null)
            _panLabel.Visible = !inputMode && IsStereoSource;
        if (_panSlider != null)
            _panSlider.Visible = !inputMode && IsStereoSource;
        if (_panInput != null)
            _panInput.Visible = !inputMode && IsStereoSource;
    }

    /// <summary>
    /// File audio keeps Delete on the file row. Audio input puts it at the right of the input row.
    /// </summary>
    /// <param name="inputMode">True when an audio input component is showing.</param>
    private void PlaceDeleteButton(bool inputMode)
    {
        if (_deleteAudioComponentButton == null || !GodotObject.IsInstanceValid(_deleteAudioComponentButton))
            return;

        Node target = inputMode ? _audioInputOption?.GetParent() : _selectFileContainer;
        if (target == null || _deleteAudioComponentButton.GetParent() == target)
            return;

        _deleteAudioComponentButton.GetParent()?.RemoveChild(_deleteAudioComponentButton);
        target.AddChild(_deleteAudioComponentButton);
    }

    private static void SetSiblingVisible(Node node, string siblingName, bool visible)
    {
        if (node?.GetParent() == null)
            return;
        var sibling = node.GetParent().GetNodeOrNull<CanvasItem>(siblingName);
        if (sibling != null)
            sibling.Visible = visible;
    }

    private void SyncAudioInputFields()
    {
        var input = _focusedAudioInput;
        if (input == null)
            return;

        _isSyncingUi = true;
        try
        {
            if (_durationValue != null)
            {
                _durationValue.Text = input.Duration <= 0
                    ? UiLocalizer.T("Until stopped")
                    : UiUtilities.FormatTime(input.Duration);
            }

            if (_volumeInput != null)
                _volumeInput.Text = UiUtilities.FormatComponentVolumeDb((float)input.Volume);
            if (_fadeInInput != null)
                _fadeInInput.Text = UiUtilities.FormatTime(input.FadeInDuration);
            if (_fadeOutInput != null)
                _fadeOutInput.Text = UiUtilities.FormatTime(input.FadeOutDuration);
            SyncFadeCurveOptions();
            UpdatePanUiVisibilityAndValues();
        }
        finally
        {
            _isSyncingUi = false;
        }
    }

    private void OnInputDurationSubmitted(string text)
    {
        if (_focusedAudioInput == null || _focusedCue == null || _durationValue == null)
            return;
        if (_isSyncingUi || _globalData?.HistoryManager?.IsRestoring == true)
            return;

        double next;
        if (string.IsNullOrWhiteSpace(text)
            || text.Trim() == "0"
            || text.Contains("until stopped", StringComparison.OrdinalIgnoreCase))
        {
            next = 0;
        }
        else
        {
            UiUtilities.ParseAndFormatTime(text, out double seconds, out _, out bool valid);
            if (!valid)
            {
                _globalSignals?.EmitSignal(nameof(GlobalSignals.Log),
                    $"Invalid input duration: {text}", (int)LogType.Warning);
                SyncAudioInputFields();
                return;
            }

            next = Math.Max(0, seconds);
        }

        if (Math.Abs(_focusedAudioInput.Duration - next) < 1e-9)
        {
            SyncAudioInputFields();
            return;
        }

        RecordAudioHistory("Edit audio input duration");
        _focusedAudioInput.Duration = next;
        _focusedAudioInput.RecalculateDuration();
        _focusedCue.CalculateTotalDuration();
        _globalSignals?.EmitSignal(nameof(GlobalSignals.UpdateShellBar), _focusedCue.Id);
        SyncAudioInputFields();
        if (_durationValue.HasFocus())
            _durationValue.ReleaseFocus();
    }

    private void CommitAudioInputVolume(string text, LineEdit field)
    {
        if (_focusedAudioInput == null || field == null)
            return;
        if (_isSyncingUi || _globalData?.HistoryManager?.IsRestoring == true)
            return;

        if (!float.TryParse((text ?? "").Replace("dB", "").Trim(), out float db))
        {
            _globalSignals?.EmitSignal(nameof(GlobalSignals.Log),
                $"Invalid volume format: {text}", (int)LogType.Warning);
            SyncAudioInputFields();
            return;
        }

        db = Mathf.Clamp(db, UiUtilities.MinVolumeDb, UiUtilities.MaxComponentGainDb);
        float volume = UiUtilities.DbToLinear(db);
        field.Text = UiUtilities.FormatComponentVolumeDb(volume);
        if (Math.Abs(_focusedAudioInput.Volume - volume) < 1e-6)
            return;

        RecordAudioHistory("Edit audio input volume");
        _focusedAudioInput.Volume = volume;
    }

    private void CommitAudioInputPanFromSlider(double value)
    {
        if (_focusedAudioInput == null || _isUpdatingPanUi || _isSyncingUi)
            return;
        if (_globalData?.HistoryManager?.IsRestoring == true)
            return;

        float pan = Mathf.Clamp((float)value / 100f, -1f, 1f);
        if (Mathf.IsEqualApprox(pan, _focusedAudioInput.Pan))
            return;

        RecordAudioHistory("Edit audio input pan", AudioCoalesceKey("input-pan"));
        _focusedAudioInput.Pan = pan;
        if (_panInput != null && !_panInput.HasFocus())
            _panInput.Text = UiUtilities.FormatPan(pan);
        RefreshRoutingInputPanLabels();
    }

    private void CommitAudioInputPanFromText(string text)
    {
        if (_focusedAudioInput == null || _isUpdatingPanUi || _isSyncingUi)
            return;
        if (!UiUtilities.TryParsePan(text, out float pan))
        {
            _globalSignals?.EmitSignal(nameof(GlobalSignals.Log),
                $"Invalid pan: {text}", (int)LogType.Warning);
            SyncPanUiValue(_focusedAudioInput.Pan);
            return;
        }

        if (Mathf.IsEqualApprox(pan, _focusedAudioInput.Pan))
        {
            SyncPanUiValue(pan);
            return;
        }

        RecordAudioHistory("Edit audio input pan");
        _focusedAudioInput.Pan = pan;
        SyncPanUiValue(pan);
        RefreshRoutingInputPanLabels();
    }

    private void CommitAudioInputFade(string text, bool isIn)
    {
        if (_focusedAudioInput == null)
            return;
        var field = isIn ? _fadeInInput : _fadeOutInput;
        if (field == null)
            return;

        var formatted = UiUtilities.ParseAndFormatTime(text, out double seconds, out string _);
        if (string.IsNullOrEmpty(formatted))
        {
            _globalSignals?.EmitSignal(nameof(GlobalSignals.Log),
                $"Invalid fade: {text}", (int)LogType.Warning);
            SyncAudioInputFields();
            return;
        }

        seconds = Math.Max(0, seconds);
        double current = isIn ? _focusedAudioInput.FadeInDuration : _focusedAudioInput.FadeOutDuration;
        field.Text = UiUtilities.FormatTime(seconds);
        if (Math.Abs(current - seconds) < 1e-9)
            return;

        RecordAudioHistory(isIn ? "Edit audio input fade-in" : "Edit audio input fade-out");
        if (isIn)
            _focusedAudioInput.FadeInDuration = seconds;
        else
            _focusedAudioInput.FadeOutDuration = seconds;
    }

    private void PopulateAudioInputOutputOptions()
    {
        if (_outputOptionButton == null || _focusedAudioInput == null)
            return;

        if (_focusedAudioInput.Patch != null && GodotObject.IsInstanceValid(_focusedAudioInput.Patch))
            _focusedAudioInput.PatchId = _focusedAudioInput.Patch.Id;

        int assignedPatchId = _focusedAudioInput.Patch?.Id ?? _focusedAudioInput.PatchId;
        _outputOptionButton.SetBlockSignals(true);
        try
        {
            _outputOptionButton.Clear();
            UiLocalizer.AddTranslatedItem(_outputOptionButton, "No output");
            _outputOptionButton.SetItemMetadata(0, -1);
            int selectedIndex = 0;
            foreach (var patch in _globalData.Settings.GetAudioOutputPatches())
            {
                if (patch.Value == null || !GodotObject.IsInstanceValid(patch.Value))
                    continue;
                _outputOptionButton.AddItem(patch.Value.Name ?? string.Empty);
                int idx = _outputOptionButton.ItemCount - 1;
                _outputOptionButton.SetItemMetadata(idx, patch.Value.Id);
                if (patch.Value.Id == assignedPatchId && string.IsNullOrEmpty(_focusedAudioInput.DirectOutput))
                    selectedIndex = idx;
            }

            foreach (var output in _audioDevices.GetAvailableAudioDeviceNames() ?? new List<string>())
            {
                _outputOptionButton.AddItem(UiLocalizer.Tf("Direct Output: {0}", output));
                int idx = _outputOptionButton.ItemCount - 1;
                _outputOptionButton.SetItemMetadata(idx, output);
                if (!string.IsNullOrEmpty(_focusedAudioInput.DirectOutput)
                    && output == _focusedAudioInput.DirectOutput)
                    selectedIndex = idx;
            }

            if (selectedIndex == 0 && assignedPatchId >= 0 && string.IsNullOrEmpty(_focusedAudioInput.DirectOutput))
            {
                string name = _focusedAudioInput.Patch?.Name ?? $"id {assignedPatchId}";
                _outputOptionButton.AddItem(UiLocalizer.Tf("Missing patch: {0}", name));
                _outputOptionButton.SetItemMetadata(_outputOptionButton.ItemCount - 1, assignedPatchId);
                selectedIndex = _outputOptionButton.ItemCount - 1;
            }

            if (_outputOptionButton.ItemCount > 0)
                _outputOptionButton.Select(selectedIndex);
        }
        finally
        {
            _outputOptionButton.SetBlockSignals(false);
        }
    }

    private void OnAudioInputOutputSelected(long index)
    {
        if (_focusedAudioInput == null || _isSyncingUi || _globalData?.HistoryManager?.IsRestoring == true)
            return;

        int item = (int)index;
        if (item < 0 || item >= _outputOptionButton.ItemCount)
            return;

        string label = _outputOptionButton.GetItemText(item);
        var meta = _outputOptionButton.GetItemMetadata(item);
        if (label.StartsWith("Missing"))
            return;

        int newPatchId = -1;
        string newDirect = null;
        AudioOutputPatch newPatch = null;
        if (item != 0 && meta.VariantType == Variant.Type.String)
        {
            newDirect = meta.AsString();
        }
        else if (item != 0 && meta.VariantType == Variant.Type.Int)
        {
            newPatchId = meta.AsInt32();
            _globalData.Settings.GetAudioOutputPatches().TryGetValue(newPatchId, out newPatch);
        }

        bool same = newPatchId == _focusedAudioInput.PatchId
            && string.Equals(newDirect ?? "", _focusedAudioInput.DirectOutput ?? "", StringComparison.Ordinal);
        if (same)
            return;

        RecordAudioHistory("Edit audio input output");
        _focusedAudioInput.Patch = newPatch;
        _focusedAudioInput.PatchId = newPatchId;
        _focusedAudioInput.DirectOutput = newDirect;
        BuildRoutingMatrix();
    }

    private bool TryResolveAudioInputRoutingIo(
        out int inputChannels,
        out List<string> inputLabels,
        out int outputChannels,
        out List<string> outputLabels)
    {
        inputChannels = 0;
        inputLabels = null;
        outputChannels = 0;
        outputLabels = new List<string>();
        var input = _focusedAudioInput;
        if (input == null)
            return false;

        var source = input.InputPatch;
        if (source == null || !GodotObject.IsInstanceValid(source))
            _globalData.Settings.GetAudioInputPatches().TryGetValue(input.InputPatchId, out source);
        input.InputPatch = source;
        // One mono feed: the patch submaster, not one row per device channel.
        inputChannels = 1;
        inputLabels = new List<string> { UiLocalizer.T("Submaster") };

        if (input.Patch != null && GodotObject.IsInstanceValid(input.Patch))
            input.PatchId = input.Patch.Id;

        if (input.PatchId >= 0 || input.Patch != null)
        {
            AudioOutputPatch patch = input.Patch;
            if (patch == null || !GodotObject.IsInstanceValid(patch))
                _globalData.Settings.GetAudioOutputPatches().TryGetValue(input.PatchId, out patch);
            if (patch == null || !GodotObject.IsInstanceValid(patch))
            {
                input.Patch = null;
                input.PatchId = -1;
                input.Routing = null;
                if (_routingContainer != null)
                    _routingContainer.Visible = false;
                ClearRoutingMatrixUi();
                return false;
            }

            input.Patch = patch;
            input.PatchId = patch.Id;
            outputChannels = patch.Channels.Count;
            outputLabels = patch.Channels.OrderBy(kv => kv.Key).Select(kv => kv.Value).ToList();
            return outputChannels > 0;
        }

        if (!string.IsNullOrEmpty(input.DirectOutput))
        {
            var device = _audioDevices.OpenAudioDevice(input.DirectOutput, out _);
            if (device == null)
            {
                input.DirectOutput = null;
                if (_routingContainer != null)
                    _routingContainer.Visible = false;
                ClearRoutingMatrixUi();
                return false;
            }

            outputChannels = Math.Max(0, device.OutputChannels > 0 ? device.OutputChannels : device.Channels);
            for (int i = 0; i < outputChannels; i++)
                outputLabels.Add($"Channel {i + 1}");
            return outputChannels > 0;
        }

        if (_routingContainer != null)
            _routingContainer.Visible = false;
        ClearRoutingMatrixUi();
        return false;
    }

    private void RemoveAudioInputComponent(Cue cue)
    {
        if (cue == null)
            return;
        var input = cue.GetAudioInputComponent();
        if (input == null)
            return;

        InspectorMultiEditSupport.RecordBeforeEdit(
            _globalData, false, cue, "Remove audio input");
        cue.RemoveICueComponent(input);
        cue.CalculateTotalDuration();
        _globalSignals?.EmitSignal(nameof(GlobalSignals.UpdateShellBar), cue.Id);
        _focusedAudioInput = null;
        ShellSelected(cue.Id);
    }
}
