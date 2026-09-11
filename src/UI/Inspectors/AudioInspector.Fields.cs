// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using Godot;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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
using Cue2.Media.Audio;
using Cue2.UI.Utilities;

namespace Cue2.UI.Inspectors;


/// <summary>
/// Inspector UI for managing audio components in cues. Handles file selection, playback settings,
/// and output patching. Supports multi-edit when Settings multi-edit is on and multiple cues are selected.
/// </summary>
/// <remarks>
/// Multi-edit targets are selected cues that have an audio component. Uniform values are shown;
/// mixed values are blank. Waveform and routing matrix reflect the primary (focused) target;
/// scalar edits (volume, pan, loop, times, fades, play count, output, file) apply to all targets.
/// History uses a cuelist snapshot when two or more targets change.
/// </remarks>
/// <summary>
/// Partial: Timing, volume/pan, loop/playcount/fades, output option select
/// </summary>
public partial class AudioInspector
{
    private void TimeFieldSubmitted(string text, LineEdit textField)
    {
        var targets = GetAudioTargets();
        if (targets.Count == 0 || textField == null)
            return;
        if (_isSyncingUi || _globalData?.HistoryManager?.IsRestoring == true)
            return;

        try
        {
            if (string.IsNullOrWhiteSpace(text) || text.Trim() == "-1")
            {
                if (textField == _startTimeInput)
                {
                    if (targets.All(t => Math.Abs(t.Component.StartTime) < 1e-9))
                        return;
                    RecordAudioHistory("Edit audio start time");
                    foreach (var (_, comp) in targets)
                        comp.StartTime = 0.0;
                    textField.Text = "00:00.000";
                    textField.TooltipText = UiLocalizer.T("00m:00s.000ms");
                }
                else if (textField == _endTimeInput)
                {
                    if (targets.All(t => t.Component.EndTime < 0))
                        return;
                    RecordAudioHistory("Edit audio end time");
                    foreach (var (_, comp) in targets)
                        comp.EndTime = -1.0;
                    double metaDur = _focusedAudioComponent?.Metadata?.Duration ?? 0;
                    textField.Text = $"Full ({UiUtilities.FormatTime(metaDur)})";
                    textField.TooltipText = UiLocalizer.T("End time undefined (plays full file)");
                }

                SyncDuration();
                if (textField.HasFocus())
                    textField.ReleaseFocus();
                _ = DrawWaveform();
                return;
            }

            var time = UiUtilities.ParseAndFormatTime(text, out var timeSecs, out string labeledTime, out bool isValid);

            if (!isValid || string.IsNullOrEmpty(time))
            {
                _globalSignals.EmitSignal(nameof(GlobalSignals.Log), $"Invalid time format in {textField.Name}: {text}", 1);
                // Always re-sanitize the LineEdit so invalid text (e.g. "4'") cannot stick.
                RestoreAudioTimeFieldDisplay(textField);
                if (textField.HasFocus())
                    textField.ReleaseFocus();
                return;
            }

            if (textField == _startTimeInput)
            {
                // Clamp start to [0, fileDuration] per target so start cannot exceed media length.
                bool anyChange = false;
                foreach (var (_, comp) in targets)
                {
                    double clamped = comp.ClampStartTime(timeSecs);
                    if (Math.Abs(comp.StartTime - clamped) >= 1e-9)
                        anyChange = true;
                }

                if (!anyChange)
                {
                    double displaySecs = _focusedAudioComponent != null
                        ? _focusedAudioComponent.ClampStartTime(timeSecs)
                        : Math.Max(0.0, timeSecs);
                    string displayTime = UiUtilities.FormatTime(displaySecs);
                    textField.Text = displayTime;
                    UiUtilities.ParseAndFormatTime(displayTime, out _, out string displayLabeled, out _);
                    textField.TooltipText = displayLabeled;
                    return;
                }

                RecordAudioHistory("Edit audio start time");
                foreach (var (_, comp) in targets)
                    comp.StartTime = comp.ClampStartTime(timeSecs);

                // Show primary's applied (possibly clamped) value.
                double primaryStart = _focusedAudioComponent?.StartTime ?? timeSecs;
                string primaryFormatted = UiUtilities.FormatTime(primaryStart);
                textField.Text = primaryFormatted;
                UiUtilities.ParseAndFormatTime(primaryFormatted, out _, out string primaryLabeled, out _);
                textField.TooltipText = primaryLabeled;

                SyncDuration();
                if (textField.HasFocus())
                    textField.ReleaseFocus();
                _ = DrawWaveform();
                return;
            }
            else if (textField == _endTimeInput)
            {
                // At or beyond each file's duration = play to end for that target.
                bool anyChange = false;
                foreach (var (_, comp) in targets)
                {
                    double fileDuration = comp.Metadata?.Duration ?? 0;
                    if (fileDuration > 0 && timeSecs >= fileDuration)
                    {
                        if (comp.EndTime >= 0)
                            anyChange = true;
                    }
                    else if (Math.Abs(comp.EndTime - timeSecs) >= 1e-9)
                    {
                        anyChange = true;
                    }
                }

                if (!anyChange)
                {
                    textField.Text = time;
                    textField.TooltipText = labeledTime;
                    return;
                }

                RecordAudioHistory("Edit audio end time");
                foreach (var (_, comp) in targets)
                {
                    double fileDuration = comp.Metadata?.Duration ?? 0;
                    if (fileDuration > 0 && timeSecs >= fileDuration)
                        comp.EndTime = -1.0;
                    else
                        comp.EndTime = timeSecs;
                }

                double primaryMeta = _focusedAudioComponent?.Metadata?.Duration ?? 0;
                if (primaryMeta > 0 && timeSecs >= primaryMeta)
                {
                    textField.Text = $"Full ({UiUtilities.FormatTime(primaryMeta)})";
                    textField.TooltipText = UiLocalizer.T("End time undefined (plays full file)");
                }
                else
                {
                    textField.Text = time;
                    textField.TooltipText = labeledTime;
                }

                SyncDuration();
                if (textField.HasFocus())
                    textField.ReleaseFocus();
                _ = DrawWaveform();
                return;
            }

            textField.Text = time;
            textField.TooltipText = labeledTime;

            SyncDuration();
            if (textField.HasFocus())
                textField.ReleaseFocus();
            _ = DrawWaveform();
        }
        catch (Exception ex)
        {
            GD.Print($"AudioInspector:TimeFieldSubmitted - Error parsing time: {ex.Message}");
            _globalSignals.EmitSignal(nameof(GlobalSignals.Log), $"Error parsing time: {ex.Message}", 2);
            RestoreAudioTimeFieldDisplay(textField);
            if (textField != null && textField.HasFocus())
                textField.ReleaseFocus();
        }
    }

    /// <summary>
    /// Writes the current model start/end time back into a time LineEdit (after invalid input).
    /// </summary>
    private void RestoreAudioTimeFieldDisplay(LineEdit textField)
    {
        if (textField == null || _focusedAudioComponent == null)
            return;

        if (textField == _startTimeInput)
        {
            string formatted = UiUtilities.FormatTime(_focusedAudioComponent.StartTime);
            textField.Text = formatted;
            UiUtilities.ParseAndFormatTime(formatted, out _, out string labeled, out _);
            textField.TooltipText = labeled;
        }
        else if (textField == _endTimeInput)
        {
            double metaDur = _focusedAudioComponent.Metadata?.Duration ?? 0;
            if (_focusedAudioComponent.EndTime < 0)
            {
                textField.Text = $"Full ({UiUtilities.FormatTime(metaDur)})";
                textField.TooltipText = UiLocalizer.T("End time undefined (plays full file)");
            }
            else
            {
                string formatted = UiUtilities.FormatTime(_focusedAudioComponent.EndTime);
                textField.Text = formatted;
                UiUtilities.ParseAndFormatTime(formatted, out _, out string labeled, out _);
                textField.TooltipText = labeled;
            }
        }
    }

    private void OnLoopToggled(bool state)
    {
        if (_isSyncingUi) return;
        var targets = GetAudioTargets();
        if (targets.Count == 0) return;
        if (_globalData?.HistoryManager?.IsRestoring == true) return;
        if (targets.All(t => t.Component.Loop == state)) return;
        RecordAudioHistory("Edit audio loop");
        foreach (var (_, comp) in targets)
            comp.Loop = state;
        SyncDuration();
    }

    /// <summary>
    /// Re-binds the audio component from the live cue and refreshes fields (undo/redo, external edits).
    /// </summary>
    private void OnSyncFromHistory()
    {
    	TaskUtil.Run(OnSyncFromHistoryAsync, "AudioInspector.OnSyncFromHistory");
    }

    private async Task OnSyncFromHistoryAsync()
    {
        // SyncShellInspector is global (shell pre-wait edits, etc.). Skip if this inspector
        // is not in the live tree (tab not built / freed) to avoid get_node absolute-path errors.
        if (!IsInsideTree()) return;

        // Multi-edit / full rebind after cuelist restore.
        if (_focusedCue != null || InspectorMultiEditSupport.ShouldUseMultiEdit(_globalData))
        {
            int cueId = _focusedCue?.Id ?? _globalData?.FocusedCue ?? -1;
            if (cueId >= 0 || InspectorMultiEditSupport.ShouldUseMultiEdit(_globalData))
            {
                ShellSelected(cueId >= 0 ? cueId : (_globalData?.FocusedCue ?? -1));
                return;
            }
        }

        if (_focusedCue == null) return;
        // Re-fetch cue in case instance was replaced (cuelist-scope restore).
        var cue = CueList.FetchCueFromId(_focusedCue.Id);
        if (cue == null)
        {
            _focusedCue = null;
            _focusedAudioComponent = null;
            return;
        }
        _focusedCue = cue;
        _focusedAudioComponent = cue.GetAudioComponent();
        if (_focusedAudioComponent == null)
        {
            _infoLabel.Text = UiLocalizer.T("No Audio File");
            _selectFileContainer.Visible = true;
            _inspectorContent.Visible = false;
            _fileUrl.Text = "";
            RestoreFileUrlPlaceholder();
            ClearFileMetadataLabel();
            if (_deleteAudioComponentButton != null)
                _deleteAudioComponentButton.Visible = false;
            return;
        }

        UpdateAudioUiFields(_focusedAudioComponent.AudioFile ?? string.Empty);
        // Output routing is not part of the scalar time fields — refresh dropdown + matrix too.
        PopulateOutputOptions();
        // Heavy matrix rebuild only when the routing UI is actually visible (avoid thrashing
        // on every shell pre/post-wait keystroke commit while Audio tab is inactive).
        if (_routingContainer != null && _routingContainer.Visible)
            BuildRoutingMatrix();

        // History snapshots omit WaveformData; invalidate cache and regenerate peaks so start/end
        // selection colors + handles redraw after undo/redo.
        _cachedPeaks = null;
        _cachedPeaksSource = null;
        _isDraggingStart = false;
        _isDraggingEnd = false;

        if (!string.IsNullOrEmpty(_focusedAudioComponent.AudioFile)
            && (_focusedAudioComponent.WaveformData == null || _focusedAudioComponent.WaveformData.Length == 0))
        {
            try
            {
                var wave = await _mediaEngine.GenerateWaveformAsync(
                    _focusedAudioComponent.AudioFile, RestartWaveformToken());
                if (_focusedAudioComponent != null && wave != null && wave.Length > 0)
                    _focusedAudioComponent.WaveformData = wave;
            }
            catch (OperationCanceledException) { /* focus moved */ }
            catch (Exception ex)
            {
                GD.PrintErr($"AudioInspector:OnSyncFromHistory - Waveform regen failed: {ex.Message}");
            }
        }

        if (_focusedAudioComponent.Metadata == null && !string.IsNullOrEmpty(_focusedAudioComponent.AudioFile))
        {
            try
            {
                _focusedAudioComponent.Metadata =
                    await _mediaEngine.GetAudioFileMetadataAsync(_focusedAudioComponent.AudioFile);
                // Channel count now known — pan is stereo-only.
                UpdatePanUiVisibilityAndValues();
                RefreshRoutingInputPanLabels();
            }
            catch (Exception ex)
            {
                GD.PrintErr($"AudioInspector:OnSyncFromHistory - Metadata refresh failed: {ex.Message}");
            }
        }
        else
        {
            UpdatePanUiVisibilityAndValues();
        }

        await DrawWaveform();
    }
    
    
    /// <summary>
    /// Handles volume input submission. Converts dB to linear, updates component, and formats display.
    /// </summary>
    /// <param name="text">The submitted text.</param>
    /// <param name="textField">The LineEdit field.</param>
    private void VolumeInputSubmitted(string text, LineEdit textField)
    {
        var targets = GetAudioTargets();
        if (targets.Count == 0 || textField == null) return;
        if (_isSyncingUi || _globalData?.HistoryManager?.IsRestoring == true) return;
        try
        {
            if (!float.TryParse(text.Replace("dB", "").Trim(), out var dbValue))
            {
                _globalSignals.EmitSignal(nameof(GlobalSignals.Log), $"Invalid volume format: {text}", 1);
                UpdateAudioUiFields(_focusedAudioComponent?.AudioFile ?? string.Empty);
                if (textField.HasFocus()) textField.ReleaseFocus();
                return;
            }
            // Digital gain allowed (−60…+12 dB). Do not treat positive as attenuation.
            dbValue = Mathf.Clamp(dbValue, UiUtilities.MinVolumeDb, UiUtilities.MaxComponentGainDb);
            var volume = UiUtilities.DbToLinear(dbValue);
            textField.Text = UiUtilities.FormatComponentVolumeDb(volume);
            if (targets.All(t => Math.Abs(t.Component.Volume - volume) < 1e-6f))
            {
                if (textField.HasFocus()) textField.ReleaseFocus();
                return;
            }
            RecordAudioHistory("Edit audio volume");
            foreach (var (_, comp) in targets)
                comp.Volume = volume;
            if (textField.HasFocus()) textField.ReleaseFocus();
        }
        catch (Exception ex)
        {
            _globalSignals.EmitSignal(nameof(GlobalSignals.Log), $"Error parsing volume: {ex.Message}", 2);
        }
    }

    /// <summary>
    /// True when pan UI should be shown (stereo source only).
    /// </summary>
    private bool IsStereoSource =>
        _focusedAudioComponent?.Metadata != null && _focusedAudioComponent.Metadata.Channels == 2;

    /// <summary>
    /// Shows or hides pan controls and syncs slider/text from the component.
    /// </summary>
    private void UpdatePanUiVisibilityAndValues()
    {
        bool show = IsStereoSource;
        if (_panLabel != null) _panLabel.Visible = show;
        if (_panSlider != null) _panSlider.Visible = show;
        if (_panInput != null) _panInput.Visible = show;
        if (!show || _focusedAudioComponent == null) return;
        SyncPanUiFromComponent();
    }

    /// <summary>
    /// Writes pan slider and text from <see cref="AudioComponent.Pan"/> without firing handlers.
    /// </summary>
    private void SyncPanUiFromComponent()
    {
        if (_focusedAudioComponent == null) return;
        _isUpdatingPanUi = true;
        try
        {
            float pan = Mathf.Clamp(_focusedAudioComponent.Pan, -1f, 1f);
            if (_panSlider != null)
                _panSlider.SetValueNoSignal(Mathf.Round(pan * 100f));
            if (_panInput != null && !_panInput.HasFocus())
                _panInput.Text = UiUtilities.FormatPan(pan);
        }
        finally
        {
            _isUpdatingPanUi = false;
        }
    }

    private void OnPanSliderChanged(double value)
    {
        if (_isUpdatingPanUi || _isSyncingUi) return;
        var targets = GetAudioTargets();
        if (targets.Count == 0) return;
        if (_globalData?.HistoryManager?.IsRestoring == true) return;
        if (!IsStereoSource) return;

        float pan = Mathf.Clamp((float)value / 100f, -1f, 1f);
        if (targets.All(t => Math.Abs(t.Component.Pan - pan) < 1e-6f)) return;

        RecordAudioHistory("Edit audio pan", AudioCoalesceKey("pan"));
        foreach (var (_, comp) in targets)
            comp.Pan = pan;

        _isUpdatingPanUi = true;
        try
        {
            if (_panInput != null)
                _panInput.Text = UiUtilities.FormatPan(pan);
        }
        finally
        {
            _isUpdatingPanUi = false;
        }
        RefreshRoutingInputPanLabels();
    }

    private void OnPanSliderDragEnded(bool valueChanged)
    {
        var key = AudioCoalesceKey("pan");
        if (!string.IsNullOrEmpty(key))
            InspectorMultiEditSupport.EndCoalesce(_globalData, UseMultiHistory(), key, key);
    }

    /// <summary>
    /// Commits pan from the text field (C, L50, R25, −100…100).
    /// </summary>
    private void PanInputSubmitted(string text)
    {
        var targets = GetAudioTargets();
        if (targets.Count == 0 || _panInput == null) return;
        if (_globalData?.HistoryManager?.IsRestoring == true) return;
        if (_isUpdatingPanUi || _isSyncingUi) return;
        if (!IsStereoSource)
        {
            if (_panInput.HasFocus()) _panInput.ReleaseFocus();
            return;
        }

        if (!UiUtilities.TryParsePan(text, out float pan))
        {
            _globalSignals.EmitSignal(nameof(GlobalSignals.Log), $"Invalid pan format: {text}", 1);
            SyncPanUiFromComponent();
            if (_panInput.HasFocus()) _panInput.ReleaseFocus();
            return;
        }

        pan = Mathf.Clamp(pan, -1f, 1f);
        _panInput.Text = UiUtilities.FormatPan(pan);

        if (targets.All(t => Math.Abs(t.Component.Pan - pan) < 1e-6f))
        {
            SyncPanUiFromComponent();
            if (_panInput.HasFocus()) _panInput.ReleaseFocus();
            return;
        }

        RecordAudioHistory("Edit audio pan");
        foreach (var (_, comp) in targets)
            comp.Pan = pan;
        SyncPanUiFromComponent();
        RefreshRoutingInputPanLabels();
        if (_panInput.HasFocus()) _panInput.ReleaseFocus();
    }

    /// <summary>
    /// Updates Left/Right routing matrix row labels with the current pan status in parentheses.
    /// </summary>
    private void RefreshRoutingInputPanLabels()
    {
        if (_routingInputLabels.Count == 0 || _focusedAudioComponent == null) return;
        if (_focusedAudioComponent.Metadata?.Channels != 2) return;

        string panStatus = UiUtilities.FormatPan(_focusedAudioComponent.Pan);
        for (int i = 0; i < _routingInputLabels.Count && i < 2; i++)
        {
            var label = _routingInputLabels[i];
            if (label == null || !IsInstanceValid(label)) continue;
            string baseName = i == 0 ? "Left" : "Right";
            label.Text = $"{baseName} ({panStatus})";
        }
    }
    
    /// <summary>
    /// Handles play count submission with validation to prevent invalid integers.
    /// </summary>
    /// <param name="newText">The submitted text.</param>
    private void OnPlayCountSubmitted(string newText)
    {
        var targets = GetAudioTargets();
        if (targets.Count == 0) return;
        if (_isSyncingUi || _globalData?.HistoryManager?.IsRestoring == true) return;
        if (int.TryParse(newText, out var playCount) && playCount > 0)
        {
            if (targets.All(t => t.Component.PlayCount == playCount))
            {
                if (_playCountInput.HasFocus()) _playCountInput.ReleaseFocus();
                return;
            }
            RecordAudioHistory("Edit audio play count");
            foreach (var (_, comp) in targets)
                comp.PlayCount = playCount;
            SyncDuration();
        }
        else
        {
            _globalSignals.EmitSignal(nameof(GlobalSignals.Log), $"Invalid play count: {newText}. Must be positive integer.", 1);
            UpdateAudioUiFields(_focusedAudioComponent?.AudioFile ?? string.Empty);
        }
        if (_playCountInput.HasFocus())
            _playCountInput.ReleaseFocus();
    }

    /// <summary>
    /// Commits fade-in or fade-out duration from a time LineEdit.
    /// </summary>
    /// <param name="text">User-entered time string.</param>
    /// <param name="isIn">True for fade-in; false for fade-out.</param>
    private void OnFadeSubmitted(string text, bool isIn)
    {
        var targets = GetAudioTargets();
        if (targets.Count == 0) return;
        if (_isSyncingUi || _globalData?.HistoryManager?.IsRestoring == true) return;

        var field = isIn ? _fadeInInput : _fadeOutInput;
        if (field == null) return;

        var formatted = UiUtilities.ParseAndFormatTime(text, out var seconds, out string labeled);
        if (string.IsNullOrEmpty(formatted))
        {
            _globalSignals.EmitSignal(nameof(GlobalSignals.Log),
                $"Invalid audio fade time: {text}", 1);
            UpdateAudioUiFields(_focusedAudioComponent?.AudioFile ?? string.Empty);
            if (field.HasFocus()) field.ReleaseFocus();
            return;
        }

        seconds = Math.Max(0.0, seconds);
        field.Text = formatted;
        field.TooltipText = labeled + (isIn
            ? " (fade-in at play start)"
            : " (fade-out on stop)");

        bool anyChange = targets.Any(t =>
        {
            double existing = isIn ? t.Component.FadeInDuration : t.Component.FadeOutDuration;
            return !Mathf.IsEqualApprox((float)existing, (float)seconds);
        });
        if (!anyChange)
        {
            if (field.HasFocus()) field.ReleaseFocus();
            return;
        }

        RecordAudioHistory(isIn ? "Edit audio fade-in" : "Edit audio fade-out");
        foreach (var (_, comp) in targets)
        {
            if (isIn)
                comp.FadeInDuration = seconds;
            else
                comp.FadeOutDuration = seconds;
        }

        if (field.HasFocus()) field.ReleaseFocus();
    }
    
    private void PopulateOutputOptions()
    {
        if (_outputOptionButton == null || _focusedAudioComponent == null) return;

        // Keep PatchId aligned with the live Patch reference (drop/create assigns both; relink/history may not).
        if (_focusedAudioComponent.Patch != null && GodotObject.IsInstanceValid(_focusedAudioComponent.Patch)
            && _focusedAudioComponent.PatchId != _focusedAudioComponent.Patch.Id)
        {
            _focusedAudioComponent.PatchId = _focusedAudioComponent.Patch.Id;
        }

        int assignedPatchId = _focusedAudioComponent.Patch?.Id ?? _focusedAudioComponent.PatchId;

        // Block ItemSelected while rebuilding the list (Select would re-enter OutputOptionSelected).
        _outputOptionButton.SetBlockSignals(true);
        try
        {
            // Remove items from output options
            var itemCount = _outputOptionButton.GetItemCount();
            for (int i = 0; i < itemCount; i++)
            {
                _outputOptionButton.RemoveItem(_outputOptionButton.GetItemCount() - 1); // Removes last item
            }

            // Add patches as options
            _outputOptionButton.AddItem(UiLocalizer.T("No output"));
            int selectedIndex = 0;

            foreach (var patch in _globalData.Settings.GetAudioOutputPatches())
            {
                _outputOptionButton.AddItem(UiLocalizer.Tf("Patch: {0}", patch.Value.Name));
                int idx = _outputOptionButton.GetItemCount() - 1;
                _outputOptionButton.SetItemMetadata(idx, patch.Value.Id);
                if (patch.Value.Id == assignedPatchId)
                    selectedIndex = idx;
            }

            foreach (var output in _audioDevices.GetAvailableAudioDeviceNames())
            {
                _outputOptionButton.AddItem(UiLocalizer.Tf("Direct Output: {0}", output));
                int idx = _outputOptionButton.GetItemCount() - 1;
                if (!string.IsNullOrEmpty(_focusedAudioComponent.DirectOutput)
                    && output == _focusedAudioComponent.DirectOutput)
                {
                    selectedIndex = idx;
                }
            }

            if (selectedIndex == 0 && !string.IsNullOrEmpty(_focusedAudioComponent.DirectOutput))
            {
                _outputOptionButton.AddItem(UiLocalizer.Tf("!!! Missing output: {0}", _focusedAudioComponent.DirectOutput));
                selectedIndex = _outputOptionButton.GetItemCount() - 1;
            }
            if (selectedIndex == 0 && assignedPatchId >= 0
                && (_focusedAudioComponent.Patch != null
                    || !_globalData.Settings.GetAudioOutputPatches().ContainsKey(assignedPatchId)))
            {
                string name = _focusedAudioComponent.Patch?.Name ?? $"id {assignedPatchId}";
                _outputOptionButton.AddItem(UiLocalizer.Tf("!!! Missing patch: {0}", name));
                selectedIndex = _outputOptionButton.GetItemCount() - 1;
            }

            _outputOptionButton.Select(selectedIndex);
        }
        finally
        {
            _outputOptionButton.SetBlockSignals(false);
        }
    }
    
    private void OutputOptionSelected(long index)
    {
        var targets = GetAudioTargets();
        if (targets.Count == 0 || _focusedAudioComponent == null) return;
        if (_isSyncingUi || _globalData?.HistoryManager?.IsRestoring == true) return;

        var item = _outputOptionButton.GetItemText((int)index);

        // Resolve intended new routing without writing yet (so we can skip no-ops).
        int newPatchId = -1;
        string newDirect = null;
        AudioOutputPatch newPatch = null;

        if (item.StartsWith("Patch"))
        {
            newPatchId = (int)_outputOptionButton.GetItemMetadata((int)index);
            if (_globalData.Settings.GetAudioOutputPatches().TryGetValue(newPatchId, out var patch))
            {
                newPatch = patch;
                newDirect = null;
            }
            else
            {
                newPatchId = -1;
                newPatch = null;
                newDirect = null;
            }
        }
        else if (item.StartsWith("Direct Output"))
        {
            newDirect = item.Replace("Direct Output: ", "");
            newPatchId = -1;
            newPatch = null;
        }
        else if (item.StartsWith("!!! Missing"))
        {
            // Keep current assignment when user re-selects a missing entry.
            return;
        }
        else
        {
            // "No output"
            newPatchId = -1;
            newPatch = null;
            newDirect = null;
        }

        // No-op when every target already has this routing.
        bool unchanged = targets.All(t =>
            newPatchId == t.Component.PatchId
            && string.Equals(
                newDirect ?? string.Empty,
                t.Component.DirectOutput ?? string.Empty,
                StringComparison.Ordinal));
        if (unchanged)
            return;

        // Discrete selection — do not coalesce; each change is its own undo step.
        RecordAudioHistory("Edit audio output");

        void ApplyRouting(AudioComponent comp)
        {
            if (item.StartsWith("Patch"))
            {
                if (newPatch != null)
                {
                    comp.Patch = newPatch;
                    comp.PatchId = newPatchId;
                    comp.DirectOutput = null;
                }
                else
                {
                    comp.Patch = null;
                    comp.PatchId = -1;
                    comp.DirectOutput = null;
                }
            }
            else if (item.StartsWith("Direct Output"))
            {
                comp.DirectOutput = newDirect;
                comp.Patch = null;
                comp.PatchId = -1;
            }
            else
            {
                comp.Patch = null;
                comp.PatchId = -1;
                comp.DirectOutput = null;
            }
        }

        foreach (var (_, comp) in targets)
            ApplyRouting(comp);

        if (item.StartsWith("Patch") && newPatch == null)
        {
            _globalSignals.EmitSignal(nameof(GlobalSignals.Log),
                $"AudioInspector:OutputOptionSelected - Patch ID {newPatchId} not found, resetting output", 1);
            _outputOptionButton.SetBlockSignals(true);
            _outputOptionButton.Select(0);
            _outputOptionButton.SetBlockSignals(false);
        }

        // Routing matrix reflects primary target only.
        BuildRoutingMatrix();

        // Refresh shell ✕ for output not assigned / missing
        foreach (var (cue, _) in targets)
            GetNodeOrNull<MediaHealthService>("/root/MediaHealthService")?.CheckCue(cue.Id);
    }

    
    /// <summary>
    /// Builds the per-cue routing matrix grid based on selected output (patch or direct).
    /// </summary>
}
