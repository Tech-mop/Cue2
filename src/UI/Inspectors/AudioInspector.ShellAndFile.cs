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
/// Partial: Shell select, multi-edit load, delete component, UI field update
/// </summary>
public partial class AudioInspector
{
    private void ShellSelected(int cueId)
    {
    	TaskUtil.Run(() => ShellSelectedAsync(cueId), "AudioInspector.ShellSelected");
    }

    private async Task ShellSelectedAsync(int cueId)
    {
        int gen = ++_shellSelectGeneration;

        if (cueId < 0)
        {
            CancelWaveformWork();
            _focusedCue = null;
            _focusedAudioComponent = null;
            _isMultiEdit = false;
            _audioTargets.Clear();
            _fileUrl.Text = "";
            RestoreFileUrlPlaceholder();
            ApplyFileUrlMissingStyle(false, null);
            ClearFileMetadataLabel();
            ShowNoSelection();
            return;
        }

        _isMultiEdit = InspectorMultiEditSupport.ShouldUseMultiEdit(_globalData);
        if (_isMultiEdit)
        {
            // Cancel prior single-cue jobs; multi-edit starts its own token.
            var multiCt = RestartWaveformToken();
            await LoadMultiEditAudio(gen, cueId, multiCt);
            return;
        }

        _audioTargets.Clear();

        // Same-cue re-entry (rapid ShellFocused, e.g. drop-create + select): only take the
        // lightweight path when the component is fully hydrated. A second ShellFocused that
        // early-outs while metadata is still null cancels the in-flight full load (generation
        // guard) and leaves the matrix empty / waveform blank — common on file-drop into cuelist.
        if (_focusedCue != null && _focusedCue.Id == cueId
            && _focusedAudioComponent != null
            && _focusedCue.Components.Contains(_focusedAudioComponent)
            && _focusedAudioComponent.Metadata != null
            && _inspectorContent != null && _inspectorContent.Visible)
        {
            UpdateAudioUiFields(_focusedAudioComponent.AudioFile ?? string.Empty);
            PopulateOutputOptions();
            BuildRoutingMatrix();

            // Peaks may still be missing (drop path generates them async) — draw or generate.
            if (_focusedAudioComponent.WaveformData != null
                && _focusedAudioComponent.WaveformData.Length > 0)
            {
                if (gen != _shellSelectGeneration) return;
                await DrawWaveform();
                if (gen != _shellSelectGeneration || _focusedCue == null) return;
                GetNodeOrNull<MediaHealthService>("/root/MediaHealthService")?.CheckCue(_focusedCue.Id);
                ApplyFileUrlMissingStyleFromHealth();
                return;
            }

            // Fall through to full path so missing waveform is generated (keep component refs).
        }

        // New cue focus — cancel prior waveform and start a fresh job token.
        var waveformCt = RestartWaveformToken();
        _focusedCue = CueList.FetchCueFromId(cueId);

        if (_focusedCue == null)
        {
            _focusedAudioComponent = null;
            ApplyFileUrlMissingStyle(false, null);
            ShowNoSelection();
            return;
        }
        
        var hasAudio = UiUtilities.HasComponent<AudioComponent>(_focusedCue);
        if (!hasAudio) // No Audio component in Cue
        {
            if (_infoLabel != null)
                _infoLabel.Visible = true;
            _infoLabel.Text = UiLocalizer.T("No Audio File");
            _infoLabel.TooltipText = "";
            _selectFileContainer.Visible = true;
            _inspectorContent.Visible = false;
            _focusedAudioComponent = null;
            _fileUrl.Text = "";
            RestoreFileUrlPlaceholder();
            ApplyFileUrlMissingStyle(false, null);
            ClearFileMetadataLabel();
            if (_deleteAudioComponentButton != null)
                _deleteAudioComponentButton.Visible = false;
            return;
        }
        
        // Audio Component Found
        _focusedAudioComponent = _focusedCue.Components.OfType<AudioComponent>().First();
        if (_deleteAudioComponentButton != null)
            _deleteAudioComponentButton.Visible = true;
        var file = _focusedAudioComponent.AudioFile;
        
        if (_focusedAudioComponent.Metadata == null)
        {
            var refreshedMeta = await _mediaEngine.GetAudioFileMetadataAsync(file);
            if (gen != _shellSelectGeneration) return;
            if (_focusedAudioComponent == null) return;
            _focusedAudioComponent.Metadata = refreshedMeta;
            GD.Print("AudioInspector:ShellSelected - Refreshed metadata from file.");
        }

        if (gen != _shellSelectGeneration) return;
        
        UpdateAudioUiFields(file);
        
        PopulateOutputOptions();
        BuildRoutingMatrix();
        
        // Generate waveform data if not cached on the component
        _cachedPeaks = null;
        _cachedPeaksSource = null;
        _viewStartNorm = 0f;
        _viewSpanNorm = 1f;
        if (_zoomSlider != null) _zoomSlider.SetValueNoSignal(1);
        SyncWaveformScrollBar();
        if (_focusedAudioComponent.WaveformData == null || _focusedAudioComponent.WaveformData.Length == 0)
        {
            GD.Print("AudioInspector:ShellSelected - No waveform found");
            try
            {
                var wave = await _mediaEngine.GenerateWaveformAsync(_focusedAudioComponent.AudioFile, waveformCt);
                if (gen != _shellSelectGeneration || _focusedAudioComponent == null) return;
                if (wave == null || wave.Length == 0)
                {
                    if (!waveformCt.IsCancellationRequested)
                        _globalSignals.EmitSignal(nameof(GlobalSignals.Log), $"AudioInspector:ShellSelected - Waveform generation failed for {_focusedAudioComponent.AudioFile}", 2);
                    return;
                }
                _focusedAudioComponent.WaveformData = wave;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                if (gen != _shellSelectGeneration) return;
                _globalSignals.EmitSignal(nameof(GlobalSignals.Log), $"AudioInspector:ShellSelected - Error generating waveform: {ex.Message}", 2);
            }
        }
        else
        {
            _globalSignals.EmitSignal(nameof(GlobalSignals.Log), $"AudioInspector:ShellSelected - Using cached waveform for {_focusedAudioComponent.AudioFile}", 0);
        }

        if (gen != _shellSelectGeneration) return;
        await DrawWaveform();
        if (gen != _shellSelectGeneration || _focusedCue == null) return;

        // Validate media path for this cue (shell X + URL styling)
        GetNodeOrNull<MediaHealthService>("/root/MediaHealthService")?.CheckCue(_focusedCue.Id);
        ApplyFileUrlMissingStyleFromHealth();
        if (_deleteAudioComponentButton != null)
            _deleteAudioComponentButton.Visible = _focusedAudioComponent != null;
        if (_infoLabel != null)
        {
            _infoLabel.Visible = false;
            _infoLabel.Text = "";
            _infoLabel.TooltipText = "";
        }
    }

    /// <summary>
    /// Shows the centred empty-state overlay when no cue shell is focused.
    /// </summary>
    private void ShowNoSelection()
    {
        if (_deleteAudioComponentButton != null)
            _deleteAudioComponentButton.Visible = false;
        if (_inspectorContent != null)
            _inspectorContent.Visible = false;
        if (_selectFileContainer != null)
            _selectFileContainer.Visible = false;
        if (_infoLabel != null)
        {
            _infoLabel.Visible = true;
            _infoLabel.Text = UiLocalizer.T("No shell selected");
            _infoLabel.TooltipText = "";
        }
    }

    /// <summary>
    /// Loads multi-edit audio UI for the current selection.
    /// </summary>
    private async Task LoadMultiEditAudio(int gen, int focusedCueId, CancellationToken waveformCt = default)
    {
        _audioTargets = InspectorMultiEditSupport.CollectComponentTargets(c => c.GetAudioComponent());
        _focusedCue = CueList.FetchCueFromId(focusedCueId);
        _focusedAudioComponent = _focusedCue?.GetAudioComponent();
        if (_focusedAudioComponent == null && _audioTargets.Count > 0)
        {
            _focusedCue = _audioTargets[^1].Cue;
            _focusedAudioComponent = _audioTargets[^1].Component;
        }

        int selected = InspectorMultiEditSupport.GetSelectedCues().Count;
        if (_audioTargets.Count == 0)
        {
            _focusedAudioComponent = null;
            if (_infoLabel != null)
                _infoLabel.Visible = true;
            _infoLabel.Text = UiLocalizer.Tf("No audio on {0} selected cue(s)", selected);
            _infoLabel.TooltipText = UiLocalizer.T("None of the selected cues have an audio component. Choose a file to add audio to all.");
            _selectFileContainer.Visible = true;
            _inspectorContent.Visible = false;
            _fileUrl.Text = "";
            RestoreFileUrlPlaceholder();
            ApplyFileUrlMissingStyle(false, null);
            ClearFileMetadataLabel();
            if (_deleteAudioComponentButton != null)
                _deleteAudioComponentButton.Visible = false;
            return;
        }

        if (_infoLabel != null)
            _infoLabel.Visible = true;
        _infoLabel.Text = InspectorMultiEditSupport.FormatComponentMultiHeader("Audio", _audioTargets.Count, selected);
        _infoLabel.TooltipText = InspectorMultiEditSupport.FormatComponentMultiTooltip(
            "audio",
            _audioTargets.Select(t => (t.Cue, (object)t.Component)).ToList(),
            selected);

        // Ensure primary has metadata for waveform / pan visibility.
        if (_focusedAudioComponent != null
            && _focusedAudioComponent.Metadata == null
            && !string.IsNullOrEmpty(_focusedAudioComponent.AudioFile))
        {
            try
            {
                _focusedAudioComponent.Metadata =
                    await _mediaEngine.GetAudioFileMetadataAsync(_focusedAudioComponent.AudioFile);
            }
            catch (Exception ex)
            {
                GD.PrintErr($"AudioInspector:LoadMultiEditAudio - Metadata: {ex.Message}");
            }
            if (gen != _shellSelectGeneration) return;
        }

        if (gen != _shellSelectGeneration) return;

        UpdateAudioUiFields(_focusedAudioComponent?.AudioFile ?? string.Empty);
        PopulateOutputOptions();
        // Routing is primary-only in multi-edit (channel layouts may differ).
        BuildRoutingMatrix();

        _cachedPeaks = null;
        _cachedPeaksSource = null;
        _viewStartNorm = 0f;
        _viewSpanNorm = 1f;
        if (_zoomSlider != null) _zoomSlider.SetValueNoSignal(1);
        SyncWaveformScrollBar();

        if (_focusedAudioComponent != null
            && !string.IsNullOrEmpty(_focusedAudioComponent.AudioFile)
            && (_focusedAudioComponent.WaveformData == null || _focusedAudioComponent.WaveformData.Length == 0))
        {
            try
            {
                var wave = await _mediaEngine.GenerateWaveformAsync(
                    _focusedAudioComponent.AudioFile, waveformCt);
                if (gen != _shellSelectGeneration || _focusedAudioComponent == null) return;
                if (wave != null && wave.Length > 0)
                    _focusedAudioComponent.WaveformData = wave;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                GD.PrintErr($"AudioInspector:LoadMultiEditAudio - Waveform: {ex.Message}");
            }
        }

        if (gen != _shellSelectGeneration) return;
        await DrawWaveform();
        if (gen != _shellSelectGeneration) return;

        if (_focusedCue != null)
            GetNodeOrNull<MediaHealthService>("/root/MediaHealthService")?.CheckCue(_focusedCue.Id);
        ApplyFileUrlMissingStyleFromHealth();
        if (_deleteAudioComponentButton != null)
        {
            _deleteAudioComponentButton.Visible = true;
            _deleteAudioComponentButton.TooltipText =
                $"Remove audio from {_audioTargets.Count} cue(s)";
        }
    }

    /// <summary>
    /// Removes the audio component from edit targets (all multi-edit targets, or focused cue).
    /// </summary>
    private void OnDeleteAudioComponentPressed()
    {
        var targets = GetAudioTargets();
        if (targets.Count == 0)
            return;

        RecordAudioHistory("Remove audio component");
        foreach (var (cue, comp) in targets)
        {
            cue.RemoveICueComponent(comp);
            cue.CalculateTotalDuration();
            GetNodeOrNull<MediaHealthService>("/root/MediaHealthService")?.CheckCue(cue.Id);
            _globalSignals.EmitSignal(nameof(GlobalSignals.UpdateShellBar), cue.Id);
        }

        _focusedAudioComponent = null;
        _audioTargets.Clear();
        _fileUrl.Text = "";
        RestoreFileUrlPlaceholder();
        ApplyFileUrlMissingStyle(false, null);
        ClearFileMetadataLabel();
        if (_deleteAudioComponentButton != null)
            _deleteAudioComponentButton.Visible = false;

        _globalSignals.EmitSignal(nameof(GlobalSignals.SyncShellInspector));
        _globalSignals.EmitSignal(nameof(GlobalSignals.Log),
            $"Removed audio component from {targets.Count} cue(s)", 0);
        // Re-enter selection path for multi empty / single empty.
        if (_focusedCue != null)
            ShellSelected(_focusedCue.Id);
        else
        {
            _inspectorContent.Visible = false;
            _infoLabel.Text = UiLocalizer.T("No Audio File");
        }
    }

    /// <summary>
    /// Updates the audio-related UI fields from the current AudioComponent state
    /// (or multi-edit uniform / blank values).
    /// </summary>
    /// <param name="file">Fallback file path when not multi-editing.</param>
    private void UpdateAudioUiFields(string file)
    {
        var targets = GetAudioTargets();
        _selectFileContainer.Visible = true;
        _inspectorContent.Visible = targets.Count > 0;
        if (_deleteAudioComponentButton != null)
            _deleteAudioComponentButton.Visible = targets.Count > 0;

        if (targets.Count == 0)
            return;

        _isSyncingUi = true;
        try
        {
            // File path
            if (InspectorMultiEditSupport.TryGetUniformString(
                    targets.Select(t => t.Component.AudioFile ?? string.Empty), out string path))
            {
                _fileUrl.Text = path;
                RestoreFileUrlPlaceholder();
            }
            else
            {
                _fileUrl.Text = string.Empty;
                _fileUrl.PlaceholderText = InspectorMultiEditSupport.MultiPlaceholder;
            }

            if (!_isMultiEdit && _infoLabel != null)
            {
                _infoLabel.Text = "";
                _infoLabel.TooltipText = "";
            }

            ApplyFileUrlMissingStyleFromHealth();
            UpdateFileMetadataLabel();

            // Start time
            if (InspectorMultiEditSupport.TryGetUniformDouble(targets.Select(t => t.Component.StartTime), out double start))
            {
                _startTimeInput.Text =
                    UiUtilities.ParseAndFormatTime(start.ToString(), out _, out string startTip);
                _startTimeInput.TooltipText = startTip;
                _startTimeInput.PlaceholderText = string.Empty;
            }
            else
            {
                _startTimeInput.Text = string.Empty;
                _startTimeInput.PlaceholderText = InspectorMultiEditSupport.MultiPlaceholder;
            }

            // End time
            if (InspectorMultiEditSupport.TryGetUniformDouble(targets.Select(t => t.Component.EndTime), out double end))
            {
                double metaDur = _focusedAudioComponent?.Metadata?.Duration ?? 0;
                if (end < 0)
                    _endTimeInput.Text = $"Full ({UiUtilities.FormatTime(metaDur)})";
                else
                    _endTimeInput.Text = UiUtilities.FormatTime(end);
                _endTimeInput.PlaceholderText = string.Empty;
            }
            else
            {
                _endTimeInput.Text = string.Empty;
                _endTimeInput.PlaceholderText = InspectorMultiEditSupport.MultiPlaceholder;
            }

            // Duration / file duration from primary when multi
            double primaryMeta = _focusedAudioComponent?.Metadata?.Duration ?? 0;
            if (InspectorMultiEditSupport.TryGetUniformDouble(targets.Select(t => t.Component.Duration), out double dur))
                _durationValue.Text = UiUtilities.FormatTime(dur);
            else
            {
                _durationValue.Text = string.Empty;
                _durationValue.PlaceholderText = InspectorMultiEditSupport.MultiPlaceholder;
            }

            _fileDurationValue.Text = UiUtilities.FormatTime(primaryMeta);

            if (InspectorMultiEditSupport.TryGetUniform(targets.Select(t => t.Component.Loop), out bool loop))
                _loopInput.SetPressedNoSignal(loop);
            else
                _loopInput.SetPressedNoSignal(false);

            if (InspectorMultiEditSupport.TryGetUniform(targets.Select(t => t.Component.PlayCount), out int playCount))
            {
                _playCountInput.Text = playCount.ToString();
                _playCountInput.PlaceholderText = string.Empty;
            }
            else
            {
                _playCountInput.Text = string.Empty;
                _playCountInput.PlaceholderText = InspectorMultiEditSupport.MultiPlaceholder;
            }

            if (InspectorMultiEditSupport.TryGetUniformDouble(targets.Select(t => t.Component.Volume), out double vol))
            {
                _volumeInput.Text = UiUtilities.FormatComponentVolumeDb((float)vol);
                _volumeInput.PlaceholderText = string.Empty;
            }
            else
            {
                _volumeInput.Text = string.Empty;
                _volumeInput.PlaceholderText = InspectorMultiEditSupport.MultiPlaceholder;
            }

            if (_fadeInInput != null)
            {
                if (InspectorMultiEditSupport.TryGetUniformDouble(
                        targets.Select(t => t.Component.FadeInDuration), out double fadeIn))
                {
                    _fadeInInput.Text = UiUtilities.FormatTime(fadeIn);
                    _fadeInInput.PlaceholderText = string.Empty;
                }
                else
                {
                    _fadeInInput.Text = string.Empty;
                    _fadeInInput.PlaceholderText = InspectorMultiEditSupport.MultiPlaceholder;
                }
            }

            if (_fadeOutInput != null)
            {
                if (InspectorMultiEditSupport.TryGetUniformDouble(
                        targets.Select(t => t.Component.FadeOutDuration), out double fadeOut))
                {
                    _fadeOutInput.Text = UiUtilities.FormatTime(fadeOut);
                    _fadeOutInput.PlaceholderText = string.Empty;
                }
                else
                {
                    _fadeOutInput.Text = string.Empty;
                    _fadeOutInput.PlaceholderText = InspectorMultiEditSupport.MultiPlaceholder;
                }
            }

            UpdatePanUiVisibilityAndValues();
        }
        finally
        {
            _isSyncingUi = false;
        }
    }

    /// <summary>
    /// Cancels any prior waveform wait without starting a new one (clear focus).
    /// </summary>
}
