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
/// Partial: Waveform draw/zoom/handles, file dialog, set audio file, accordion
/// </summary>
public partial class AudioInspector
{
    private void CancelWaveformWork()
    {
        try { _waveformCts?.Cancel(); } catch { /* ignore */ }
        try { _waveformCts?.Dispose(); } catch { /* ignore */ }
        _waveformCts = null;
    }

    /// <summary>
    /// Cancels any prior waveform job and returns a fresh token for the next generate.
    /// </summary>
    private CancellationToken RestartWaveformToken()
    {
        try { _waveformCts?.Cancel(); } catch { /* ignore */ }
        try { _waveformCts?.Dispose(); } catch { /* ignore */ }
        _waveformCts = new CancellationTokenSource();
        return _waveformCts.Token;
    }

    private void StyleWaveformHandles()
    {
        // Wider hit targets; colors match markers (cyan start / orange end)
        _startDragHandle.CustomMinimumSize = new Vector2(10, 0);
        _endDragHandle.CustomMinimumSize = new Vector2(10, 0);
        _startDragHandle.Modulate = GlobalStyles.LowColor1;
        _endDragHandle.Modulate = GlobalStyles.HighColor1;
        _startDragHandle.TooltipText = UiLocalizer.T("Start time (drag)");
        _endDragHandle.TooltipText = UiLocalizer.T("End time (drag)");
    }

    private void OnZoomChanged(double value)
    {
        float zoom = Mathf.Max(1f, (float)value);
        float oldSpan = _viewSpanNorm;
        float center = _viewStartNorm + oldSpan * 0.5f;
        _viewSpanNorm = 1f / zoom;
        _viewStartNorm = Mathf.Clamp(center - _viewSpanNorm * 0.5f, 0f, 1f - _viewSpanNorm);
        SyncWaveformScrollBar();
        _ = DrawWaveform();
    }

    private void OnWaveformScrollChanged(double value)
    {
        float maxStart = Math.Max(0f, 1f - _viewSpanNorm);
        _viewStartNorm = maxStart <= 0 ? 0 : Mathf.Clamp((float)value, 0f, maxStart);
        _ = DrawWaveform();
    }

    private void SyncWaveformScrollBar()
    {
        if (_waveformScroll == null) return;
        bool zoomed = _viewSpanNorm < 0.999f;
        _waveformScroll.Visible = zoomed;
        if (!zoomed)
        {
            _viewStartNorm = 0f;
            return;
        }
        float maxStart = Math.Max(0.0001f, 1f - _viewSpanNorm);
        _waveformScroll.MinValue = 0;
        _waveformScroll.MaxValue = maxStart;
        _waveformScroll.Page = _viewSpanNorm * maxStart; // thumb size hint
        if (_waveformScroll.Page < 0.01)
            _waveformScroll.Page = 0.01;
        _waveformScroll.Step = maxStart / 200.0;
        _waveformScroll.SetValueNoSignal(Mathf.Clamp(_viewStartNorm, 0f, maxStart));
    }

    private void OnWaveformPanelGuiInput(InputEvent @event)
    {
        // Ctrl+wheel zoom, plain wheel scroll when zoomed
        if (@event is InputEventMouseButton mb && mb.Pressed &&
            (mb.ButtonIndex == MouseButton.WheelUp || mb.ButtonIndex == MouseButton.WheelDown))
        {
            if (mb.CtrlPressed && _zoomSlider != null)
            {
                double z = _zoomSlider.Value;
                z += mb.ButtonIndex == MouseButton.WheelUp ? 0.5 : -0.5;
                _zoomSlider.Value = Mathf.Clamp((float)z, (float)_zoomSlider.MinValue, (float)_zoomSlider.MaxValue);
                AcceptEvent();
            }
            else if (_viewSpanNorm < 0.999f)
            {
                float delta = _viewSpanNorm * 0.15f * (mb.ButtonIndex == MouseButton.WheelUp ? -1f : 1f);
                float maxStart = 1f - _viewSpanNorm;
                _viewStartNorm = Mathf.Clamp(_viewStartNorm + delta, 0f, maxStart);
                SyncWaveformScrollBar();
                _ = DrawWaveform();
                AcceptEvent();
            }
        }
    }

    /// <summary>
    /// Updates the waveform display from cached peaks and start/end selection.
    /// </summary>
    private async Task DrawWaveform()
    {
        if (_waveformAccordian == null || _waveformAccordian.Visible == false) return;
        if (_focusedAudioComponent?.WaveformData == null || _focusedAudioComponent.WaveformData.Length == 0)
        {
            _globalSignals.EmitSignal(nameof(GlobalSignals.Log), "AudioInspector:DrawWaveform - No waveform data available", 1);
            return;
        }

        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

        // Guard: component may have been rebound during the await (undo/redo).
        if (_focusedAudioComponent?.WaveformData == null || _focusedAudioComponent.WaveformData.Length == 0)
            return;

        float width = _waveformPanel.Size.X;
        if (width < 50)
            width = Math.Max(0, _inspectorContent.Size.X - 48);
        if (width < 50)
        {
            _globalSignals.EmitSignal(nameof(GlobalSignals.Log), "AudioInspector:DrawWaveform - Waveform panel too small to draw", 1);
            return;
        }

        if (_cachedPeaks == null || !ReferenceEquals(_cachedPeaksSource, _focusedAudioComponent.WaveformData))
        {
            _cachedPeaks = WaveformPeaks.FromBytes(_focusedAudioComponent.WaveformData);
            _cachedPeaksSource = _focusedAudioComponent.WaveformData;
        }
        if (_cachedPeaks == null)
        {
            _globalSignals.EmitSignal(nameof(GlobalSignals.Log), "AudioInspector:DrawWaveform - Invalid waveform payload", 1);
            return;
        }

        double duration = _focusedAudioComponent.Metadata?.Duration ?? 0;
        if (duration <= 0) duration = 1;
        float startNorm = (float)(_focusedAudioComponent.StartTime / duration);
        float endTime = _focusedAudioComponent.EndTime < 0
            ? (float)duration
            : (float)_focusedAudioComponent.EndTime;
        float endNorm = (float)(endTime / duration);

        _viewSpanNorm = Mathf.Clamp(_viewSpanNorm, 0.01f, 1f);
        _viewStartNorm = Mathf.Clamp(_viewStartNorm, 0f, 1f - _viewSpanNorm);

        _waveformDisplay.SetData(_cachedPeaks, startNorm, endNorm, _viewStartNorm, _viewSpanNorm, duration);

        // Position handles in view coordinates; hide when off-screen
        PositionWaveformHandle(_startDragHandle, startNorm, width);
        PositionWaveformHandle(_endDragHandle, endNorm, width);
        SyncWaveformScrollBar();
    }

    private void PositionWaveformHandle(Button handle, float fileNorm, float width)
    {
        float x = _waveformDisplay.FileNormToX(fileNorm);
        bool visible = x >= -4 && x <= width + 4;
        handle.Visible = visible;
        if (!visible) return;
        float handleW = handle.CustomMinimumSize.X > 0 ? handle.CustomMinimumSize.X : 10f;
        handle.Position = new Vector2(x - handleW * 0.5f, 0);
        handle.Size = new Vector2(handleW, _waveformPanel.Size.Y);
    }

    private void OnStartHandleInput(InputEvent @event)
    {
        if (@event is InputEventMouseButton mouseButton && mouseButton.ButtonIndex == MouseButton.Left)
        {
            if (mouseButton.Pressed)
            {
                // Continuous drag session: one undo step for the whole drag (all multi targets).
                RecordAudioHistory("Edit audio start time", AudioCoalesceKey("start-drag"));
                _isDraggingStart = true;
            }
            else if (_isDraggingStart)
            {
                SyncDuration();
                _isDraggingStart = false;
                var key = AudioCoalesceKey("start-drag");
                if (!string.IsNullOrEmpty(key))
                    InspectorMultiEditSupport.EndCoalesce(_globalData, UseMultiHistory(), key, key);
            }
        }
        else if (@event is InputEventMouseMotion && _isDraggingStart)
        {
            if (_focusedAudioComponent == null) return;
            float localX = _waveformPanel.GetLocalMousePosition().X;
            float norm = _waveformDisplay.XToFileNorm(localX);
            double duration = _focusedAudioComponent.Metadata?.Duration ?? 0;
            if (duration <= 0) return;
            // Keep start before end (primary waveform geometry).
            float endN = _focusedAudioComponent.EndTime < 0
                ? 1f
                : (float)(_focusedAudioComponent.EndTime / duration);
            norm = Mathf.Min(norm, endN - 0.001f);
            norm = Mathf.Max(0f, norm);
            double startSecs = norm * duration;
            foreach (var (_, comp) in GetAudioTargets())
            {
                double d = comp.Metadata?.Duration ?? duration;
                if (d <= 0) d = duration;
                float localEndN = comp.EndTime < 0 ? 1f : (float)(comp.EndTime / d);
                float localNorm = Mathf.Min(norm, localEndN - 0.001f);
                localNorm = Mathf.Max(0f, localNorm);
                comp.StartTime = comp.ClampStartTime(localNorm * d);
            }
            _startTimeInput.Text = UiUtilities.FormatTime(
                _focusedAudioComponent != null ? _focusedAudioComponent.StartTime : startSecs);
            _ = DrawWaveform();
        }
    }

    private void OnEndHandleInput(InputEvent @event)
    {
        if (@event is InputEventMouseButton mouseButton && mouseButton.ButtonIndex == MouseButton.Left)
        {
            if (mouseButton.Pressed)
            {
                RecordAudioHistory("Edit audio end time", AudioCoalesceKey("end-drag"));
                _isDraggingEnd = true;
            }
            else if (_isDraggingEnd)
            {
                SyncDuration();
                _isDraggingEnd = false;
                var key = AudioCoalesceKey("end-drag");
                if (!string.IsNullOrEmpty(key))
                    InspectorMultiEditSupport.EndCoalesce(_globalData, UseMultiHistory(), key, key);
            }
        }
        else if (@event is InputEventMouseMotion && _isDraggingEnd)
        {
            if (_focusedAudioComponent == null) return;
            float localX = _waveformPanel.GetLocalMousePosition().X;
            float norm = _waveformDisplay.XToFileNorm(localX);
            double duration = _focusedAudioComponent.Metadata?.Duration ?? 0;
            if (duration <= 0) return;
            float startN = (float)(_focusedAudioComponent.StartTime / duration);
            norm = Mathf.Max(norm, startN + 0.001f);
            norm = Mathf.Min(1f, norm);
            double endSecs = norm * duration;
            foreach (var (_, comp) in GetAudioTargets())
            {
                double d = comp.Metadata?.Duration ?? duration;
                if (d <= 0) d = duration;
                float localStartN = (float)(comp.StartTime / d);
                float localNorm = Mathf.Max(norm, localStartN + 0.001f);
                localNorm = Mathf.Min(1f, localNorm);
                comp.EndTime = localNorm * d;
            }
            _endTimeInput.Text = UiUtilities.FormatTime(endSecs);
            _ = DrawWaveform();
        }
    }

    
    private void SyncDuration()
    {
        var targets = GetAudioTargets();
        if (targets.Count == 0) return;

        foreach (var (cue, comp) in targets)
        {
            comp.RecalculateDuration();
            cue.CalculateTotalDuration();
            _globalSignals.EmitSignal(nameof(GlobalSignals.UpdateShellBar), cue.Id);
        }

        if (_focusedAudioComponent != null)
        {
            _durationValue.Text =
                UiUtilities.ParseAndFormatTime(
                    _focusedAudioComponent.Duration.ToString(), out var _, out string durLabeledTime);
            _durationValue.TooltipText = durLabeledTime;
        }

        // Shell list + shell inspector
        _globalSignals.EmitSignal(nameof(GlobalSignals.SyncShellInspector));
    }
    
    
    /// <summary>
    /// Opens a file dialog for selecting an audio file.
    /// </summary>
    private void OpenFileDialog()
    {
        _fileDialog = new FileDialog();
        _fileDialog.FileSelected += FileSelected;
        _fileDialog.FileMode = FileDialog.FileModeEnum.OpenFile;
        _fileDialog.Access = FileDialog.AccessEnum.Filesystem;
        _fileDialog.Title = UiLocalizer.T("Open an Audio File");
        _fileDialog.UseNativeDialog = true;
        _fileDialog.AddFilter(string.Join(",", GlobalData.AudioFileFilters), "Audio Files");
        AddChild(_fileDialog);
        _fileDialog.PopupCentered();
        _fileDialog.Canceled += ClearFileDialog;
    }
    
    

    /// <summary>
    /// Handles file selection from dialog. Adds/replaces AudioComponent and loads metadata + waveform.
    /// </summary>
    /// <param name="path">The selected file path.</param>
    private void FileSelected(string path)
    {
        ClearFileDialog();
        if (_focusedCue == null && !InspectorMultiEditSupport.ShouldUseMultiEdit(_globalData))
        {
            GD.Print("AudioInspector:FileSelected - No cue selected");
            _globalSignals.EmitSignal(nameof(GlobalSignals.Log), "AudioInspector:No cue selected", 2);
            return;
        }
        // File picker always treats selection as a fresh media assignment (reset in/out).
        SetAudioFile(path, resetInOutPoints: true);
    }

    /// <summary>
    /// Handles setting audio file URL from drag-and-drop. Creates AudioComponent if none exists.
    /// </summary>
    /// <param name="filePath">The dropped file path.</param>
    public void SetAudioFileUrlFromDrop(string filePath)
    {
        if (_focusedCue == null && !InspectorMultiEditSupport.ShouldUseMultiEdit(_globalData))
        {
            GD.Print("AudioInspector:SetAudioFileUrlFromDrop - No cue selected");
            _globalSignals.EmitSignal(nameof(GlobalSignals.Log), "AudioInspector:No cue selected for audio file drop", 2);
            return;
        }
        // Drop onto URL bar: replace media, clamp existing in/out if still valid.
        SetAudioFile(filePath, resetInOutPoints: false);
    }

    /// <summary>
    /// Sets the audio file for the focused cue (or all multi-edit selected cues): create or replace
    /// component, load metadata, generate waveform, refresh UI.
    /// </summary>
    /// <param name="filePath">The audio file path.</param>
    /// <param name="resetInOutPoints">If true, start/end are reset to full file; otherwise clamp to new duration.</param>
    private void SetAudioFile(string filePath, bool resetInOutPoints)
    {
    	TaskUtil.Run(() => SetAudioFileAsync(filePath, resetInOutPoints), "AudioInspector.SetAudioFile");
    }

    private async Task SetAudioFileAsync(string filePath, bool resetInOutPoints)
    {
        bool multi = InspectorMultiEditSupport.ShouldUseMultiEdit(_globalData);
        var multiCues = multi ? InspectorMultiEditSupport.GetSelectedCues() : null;
        if (!multi && _focusedCue == null) return;
        if (multi && (multiCues == null || multiCues.Count == 0)) return;

        string resolvedPath = _globalData?.ResolveMediaPath(filePath) ?? filePath;
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(resolvedPath))
        {
            GD.Print($"AudioInspector:SetAudioFile - File not found: {filePath}");
            _globalSignals.EmitSignal(nameof(GlobalSignals.Log), $"AudioInspector:File not found: {filePath}", 2);
            return;
        }

        // Prefer show-relative path when media backup is enabled (copy runs in background)
        string pathToStore = filePath;
        try
        {
            var backup = GetNodeOrNull<MediaBackupManager>("/root/MediaBackupManager");
            string relative = backup?.EnsureMediaBackedUp(resolvedPath, MediaBackupKind.Audio);
            if (!string.IsNullOrEmpty(relative))
                pathToStore = relative;
        }
        catch (Exception ex)
        {
            GD.PrintErr($"AudioInspector:SetAudioFile - Media backup: {ex.Message}");
        }

        if (multi)
        {
            bool anyNew = multiCues.Any(c => c.GetAudioComponent() == null);
            InspectorMultiEditSupport.RecordBeforeEdit(
                _globalData,
                multiCues.Count > 1,
                multiCues[^1],
                anyNew ? "Add audio component" : "Change audio file",
                anyNew ? "Multi-add audio components" : "Multi-edit audio file");

            AudioFileMetadata sharedMeta = null;
            try
            {
                sharedMeta = await _mediaEngine.GetAudioFileMetadataAsync(resolvedPath);
            }
            catch (Exception ex)
            {
                GD.PrintErr($"AudioInspector:SetAudioFile multi - Metadata: {ex.Message}");
            }

            foreach (var cue in multiCues)
            {
                var existing = cue.GetAudioComponent();
                bool isNew = existing == null;
                AudioComponent comp;
                if (existing != null)
                {
                    comp = existing;
                    bool pathChanged = !string.Equals(existing.AudioFile, pathToStore, StringComparison.OrdinalIgnoreCase);
                    existing.AudioFile = pathToStore;
                    if (pathChanged)
                    {
                        existing.WaveformData = null;
                        existing.Metadata = null;
                    }
                }
                else
                {
                    comp = cue.AddAudioComponent(pathToStore);
                }

                if (sharedMeta != null)
                    comp.Metadata = sharedMeta;

                if (resetInOutPoints || isNew)
                {
                    comp.StartTime = 0.0;
                    comp.EndTime = -1.0;
                }
                else if (sharedMeta != null)
                {
                    double fileDuration = sharedMeta.Duration > 0 ? sharedMeta.Duration : 0.0;
                    if (comp.StartTime >= fileDuration)
                        comp.StartTime = 0.0;
                    if (comp.EndTime >= 0 && (comp.EndTime > fileDuration || comp.EndTime <= comp.StartTime))
                        comp.EndTime = -1.0;
                }

                comp.RecalculateDuration();
                cue.CalculateTotalDuration();
                _globalSignals?.EmitSignal(nameof(GlobalSignals.UpdateShellBar), cue.Id);
                GetNodeOrNull<MediaHealthService>("/root/MediaHealthService")?.CheckCue(cue.Id);
            }

            _globalSignals.EmitSignal(nameof(GlobalSignals.SyncShellInspector));
            int focusId = _focusedCue?.Id ?? multiCues[^1].Id;
            ShellSelected(focusId);
            return;
        }

        // Resolve or create component; always assign the path (AddAudioComponent alone does not update existing).
        var existingAudio = _focusedCue.Components.OfType<AudioComponent>().FirstOrDefault();
        bool isNewComponent = existingAudio == null;
        if (_focusedCue != null)
        {
            InspectorMultiEditSupport.RecordBeforeEdit(
                _globalData,
                multiHistory: false,
                _focusedCue,
                isNewComponent ? "Add audio component" : "Change audio file");
        }
        if (existingAudio != null)
        {
            _focusedAudioComponent = existingAudio;
            bool pathChanged = !string.Equals(existingAudio.AudioFile, pathToStore, StringComparison.OrdinalIgnoreCase);
            existingAudio.AudioFile = pathToStore;
            if (pathChanged)
            {
                // Stale peaks/metadata from previous file must not stick
                existingAudio.WaveformData = null;
                existingAudio.Metadata = null;
            }
        }
        else
        {
            _focusedAudioComponent = _focusedCue.AddAudioComponent(pathToStore);
        }

        _fileUrl.Text = pathToStore;
        _inspectorContent.Visible = true;
        _selectFileContainer.Visible = true;
        _infoLabel.Text = "";

        // Invalidate display cache while loading
        _cachedPeaks = null;
        _cachedPeaksSource = null;

        try
        {
            var fileMetadata = await _mediaEngine.GetAudioFileMetadataAsync(resolvedPath);
            if (fileMetadata == null)
            {
                _globalSignals.EmitSignal(nameof(GlobalSignals.Log),
                    $"AudioInspector:SetAudioFile - Failed to read metadata for {Path.GetFileName(filePath)}", 2);
                return;
            }

            _focusedAudioComponent.Metadata = fileMetadata;
            var fileDuration = fileMetadata.Duration > 0 ? fileMetadata.Duration : 0.0;

            if (resetInOutPoints || isNewComponent)
            {
                _focusedAudioComponent.StartTime = 0.0;
                _focusedAudioComponent.EndTime = -1.0; // full file
                GD.Print($"AudioInspector:SetAudioFile - Metadata loaded: Duration {fileDuration}s, Channels {fileMetadata.Channels}");
            }
            else
            {
                if (_focusedAudioComponent.StartTime >= fileDuration)
                {
                    _focusedAudioComponent.StartTime = 0.0;
                    GD.Print("AudioInspector:SetAudioFile - Reset start time (exceeded file duration)");
                }

                if (_focusedAudioComponent.EndTime >= 0 && _focusedAudioComponent.EndTime > fileDuration)
                {
                    _focusedAudioComponent.EndTime = -1.0;
                    GD.Print("AudioInspector:SetAudioFile - Reset end time to undefined (exceeded file duration)");
                }
                else if (_focusedAudioComponent.EndTime >= 0 &&
                         _focusedAudioComponent.EndTime <= _focusedAudioComponent.StartTime)
                {
                    _focusedAudioComponent.EndTime = -1.0;
                    GD.Print("AudioInspector:SetAudioFile - Reset end time to undefined (was <= start time)");
                }
            }

            // Duration fields need RecalculateDuration after Metadata is set
            _focusedAudioComponent.RecalculateDuration();
            _focusedCue.CalculateTotalDuration();
        }
        catch (Exception ex)
        {
            GD.PrintErr($"AudioInspector:SetAudioFile - Metadata error: {ex.Message}");
            _globalSignals.EmitSignal(nameof(GlobalSignals.Log),
                $"AudioInspector:SetAudioFile - Metadata error: {ex.Message}", 2);
            return;
        }

        // Always (re)generate waveform for the assigned file
        try
        {
            // Use absolute source for waveform while background copy may still be running
            var wave = await _mediaEngine.GenerateWaveformAsync(resolvedPath, RestartWaveformToken());
            if (_focusedAudioComponent == null) return;
            if (wave == null || wave.Length == 0)
            {
                _globalSignals.EmitSignal(nameof(GlobalSignals.Log),
                    $"AudioInspector:SetAudioFile - Waveform generation failed for {pathToStore}", 2);
            }
            else
            {
                _focusedAudioComponent.WaveformData = wave;
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            _globalSignals.EmitSignal(nameof(GlobalSignals.Log),
                $"AudioInspector:SetAudioFile - Error generating waveform: {ex.Message}", 2);
        }

        UpdateAudioUiFields(pathToStore);
        PopulateOutputOptions();
        BuildRoutingMatrix();
        SyncDuration();

        // Reset zoom/view for new media, then draw if accordion is open
        _viewStartNorm = 0f;
        _viewSpanNorm = 1f;
        if (_zoomSlider != null) _zoomSlider.SetValueNoSignal(1);
        SyncWaveformScrollBar();
        await DrawWaveform();

        GD.Print($"AudioInspector:SetAudioFile - Set audio file: {pathToStore}");
        _globalSignals.EmitSignal(nameof(GlobalSignals.Log),
            $"AudioInspector:Set audio file to: {pathToStore}", 0);

        GetNodeOrNull<MediaHealthService>("/root/MediaHealthService")?.CheckCue(_focusedCue.Id);
        ApplyFileUrlMissingStyleFromHealth();
        if (_deleteAudioComponentButton != null)
            _deleteAudioComponentButton.Visible = true;
    }

    /// <summary>
    /// Clears the file dialog instance (safe if already null or freed).
    /// </summary>
    private void ClearFileDialog()
    {
        if (_fileDialog == null)
            return;

        try
        {
            if (IsInstanceValid(_fileDialog))
            {
                _fileDialog.FileSelected -= FileSelected;
                _fileDialog.Canceled -= ClearFileDialog;
                _fileDialog.QueueFree();
            }
        }
        catch
        {
            /* best-effort during exit */
        }

        _fileDialog = null;
    }
    
    /// <summary>
    /// Toggles visibility of an accordion container and updates button icon.
    /// </summary>
    /// <param name="accordian">The VBoxContainer to toggle.</param>
    /// <param name="button">The Button controlling the toggle.</param>
    private void ToggleAccordian(VBoxContainer accordian, Button button)
    {
    	TaskUtil.Run(() => ToggleAccordianAsync(accordian, button), "AudioInspector.ToggleAccordian");
    }

    private async Task ToggleAccordianAsync(VBoxContainer accordian, Button button)
    {
        accordian.Visible = !accordian.Visible;
        button.Icon = GetThemeIcon(accordian.Visible ? "Down" : "Right", "AtlasIcons");

        if (accordian.Name == "WaveformAccordian")
        {
            await DrawWaveform();
        }
    }
}
