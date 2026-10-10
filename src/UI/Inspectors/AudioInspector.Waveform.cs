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

    private void OnWaveformViewChanged()
    {
        RedrawWaveformView();
    }

    /// <summary>
    /// Applies the current viewport window to the display without waiting a frame.
    /// Used for zoom/pan so X↔time mapping stays in sync with the pointer.
    /// </summary>
    private void RedrawWaveformView()
    {
        if (_waveformDisplay == null || _waveformZoom == null)
        {
            SyncTimelineNodes();
            return;
        }
        if (_focusedAudioComponent?.WaveformData == null || _focusedAudioComponent.WaveformData.Length == 0
            || _cachedPeaks == null)
        {
            SyncTimelineNodes();
            return;
        }

        double duration = _focusedAudioComponent.Metadata?.Duration ?? 0;
        if (duration <= 0) duration = 1;
        double playRate = _focusedAudioComponent.PlayRate;
        _waveformZoom.Viewport.DurationSeconds = duration / playRate;
        float startNorm = (float)(_focusedAudioComponent.StartTime / duration);
        float endTime = _focusedAudioComponent.EndTime < 0
            ? (float)duration
            : (float)_focusedAudioComponent.EndTime;
        float endNorm = (float)(endTime / duration);
        _waveformZoom.SetSelection(startNorm, endNorm);

        _waveformDisplay.SetData(
            _cachedPeaks, startNorm, endNorm,
            _waveformZoom.Viewport.ViewStartNorm,
            _waveformZoom.Viewport.ViewSpanNorm,
            duration,
            _focusedAudioComponent.FadeInDuration,
            _focusedAudioComponent.FadeOutDuration,
            _focusedAudioComponent.FadeInCurve,
            _focusedAudioComponent.FadeOutCurve,
            playRate);

        float width = _waveformPanel != null ? _waveformPanel.Size.X : 0f;
        float height = _waveformPanel != null ? _waveformPanel.Size.Y : 0f;
        _waveformDisplay.PlaceHandle(_startDragHandle, startNorm, isStart: true, width, height);
        _waveformDisplay.PlaceHandle(_endDragHandle, endNorm, isStart: false, width, height);
        _waveformDisplay.PlaceFadeHandle(_fadeInHandle, _waveformDisplay.FadeInEndNorm, isFadeIn: true, width, height);
        _waveformDisplay.PlaceFadeHandle(_fadeOutHandle, _waveformDisplay.FadeOutStartNorm, isFadeIn: false, width, height);
        SyncTimelineNodes();
    }

    private void UpdateWaveformPlayhead()
    {
        if (_waveformZoom == null || _waveformDisplay == null) return;
        if (_timelineAccordian == null || !_timelineAccordian.Visible)
        {
            _waveformZoom.SetPlayheadNorm(-1f);
            return;
        }

        double duration = _focusedAudioComponent?.Metadata?.Duration ?? 0;
        if (duration <= 0 || _focusedCue == null)
        {
            _waveformZoom.SetPlayheadNorm(-1f);
            return;
        }

        var exec = _globalData?.CueCommandExecutor;
        if (exec == null)
        {
            _waveformZoom.SetPlayheadNorm(-1f);
            return;
        }

        foreach (var root in exec.ActiveCues)
        {
            if (root == null || !IsInstanceValid(root)) continue;
            try
            {
                foreach (var active in root.EnumerateSelfAndDescendants())
                {
                    if (active == null || !IsInstanceValid(active)) continue;
                    if (active.Cue == null || active.Cue.Id != _focusedCue.Id) continue;
                    foreach (var playback in active.EnumerateAudioPlaybacks())
                    {
                        double sec = playback.GetPlaybackTimeMs() / 1000.0;
                        _waveformZoom.SetPlayheadNorm((float)Math.Clamp(sec / duration, 0, 1));
                        return;
                    }
                }
            }
            catch
            {
                // Playback may be mid-teardown
            }
        }

        _waveformZoom.SetPlayheadNorm(-1f);
    }

    /// <summary>
    /// Updates the waveform display from cached peaks and start/end selection.
    /// </summary>
    private async Task DrawWaveform()
    {
        if (_timelineAccordian == null || _timelineAccordian.Visible == false) return;
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
        if (_waveformZoom != null)
            _waveformZoom.Viewport.DurationSeconds = duration / _focusedAudioComponent.PlayRate;

        RedrawWaveformView();
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
                _startDragHandle?.AcceptEvent();
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
            RedrawWaveformView();
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
                _endDragHandle?.AcceptEvent();
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
            RedrawWaveformView();
        }
    }

    private void OnFadeInHandleInput(InputEvent @event)
    {
        if (@event is InputEventMouseButton mouseButton && mouseButton.ButtonIndex == MouseButton.Left)
        {
            if (mouseButton.Pressed)
            {
                RecordAudioHistory("Edit audio fade-in", AudioCoalesceKey("fade-in-drag"));
                _isDraggingFadeIn = true;
                _fadeInHandle?.AcceptEvent();
            }
            else if (_isDraggingFadeIn)
            {
                _isDraggingFadeIn = false;
                var key = AudioCoalesceKey("fade-in-drag");
                if (!string.IsNullOrEmpty(key))
                    InspectorMultiEditSupport.EndCoalesce(_globalData, UseMultiHistory(), key, key);
            }
        }
        else if (@event is InputEventMouseMotion && _isDraggingFadeIn)
        {
            ApplyAudioFadeFromWaveform(isIn: true);
        }
    }

    private void OnFadeOutHandleInput(InputEvent @event)
    {
        if (@event is InputEventMouseButton mouseButton && mouseButton.ButtonIndex == MouseButton.Left)
        {
            if (mouseButton.Pressed)
            {
                RecordAudioHistory("Edit audio fade-out", AudioCoalesceKey("fade-out-drag"));
                _isDraggingFadeOut = true;
                _fadeOutHandle?.AcceptEvent();
            }
            else if (_isDraggingFadeOut)
            {
                _isDraggingFadeOut = false;
                var key = AudioCoalesceKey("fade-out-drag");
                if (!string.IsNullOrEmpty(key))
                    InspectorMultiEditSupport.EndCoalesce(_globalData, UseMultiHistory(), key, key);
            }
        }
        else if (@event is InputEventMouseMotion && _isDraggingFadeOut)
        {
            ApplyAudioFadeFromWaveform(isIn: false);
        }
    }

    private void ApplyAudioFadeFromWaveform(bool isIn)
    {
        if (_focusedAudioComponent == null) return;
        float localX = _waveformPanel.GetLocalMousePosition().X;
        float norm = _waveformDisplay.XToFileNorm(localX);
        double duration = _focusedAudioComponent.Metadata?.Duration ?? 0;
        if (duration <= 0) return;

        double start = _focusedAudioComponent.StartTime;
        double end = _focusedAudioComponent.EndTime < 0 ? duration : _focusedAudioComponent.EndTime;
        double sel = Math.Max(0, end - start);
        double fileFade = isIn
            ? Math.Clamp(norm * duration - start, 0, sel)
            : Math.Clamp(end - norm * duration, 0, sel);
        double rate = Math.Max(1e-6, _focusedAudioComponent.PlayRate);
        double fade = fileFade / rate;

        foreach (var (_, comp) in GetAudioTargets())
        {
            double d = comp.Metadata?.Duration ?? duration;
            if (d <= 0) d = duration;
            double s = comp.StartTime;
            double e = comp.EndTime < 0 ? d : comp.EndTime;
            double localSel = Math.Max(0, e - s);
            double localRate = Math.Max(1e-6, comp.PlayRate);
            double localFade = Math.Clamp(fileFade / localRate, 0, localSel / localRate);
            if (isIn)
                comp.FadeInDuration = localFade;
            else
                comp.FadeOutDuration = localFade;
        }

        var field = isIn ? _fadeInInput : _fadeOutInput;
        if (field != null)
            field.Text = UiUtilities.FormatTime(fade);
        RedrawWaveformView();
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
        if (_focusedCue?.GetAudioInputComponent() != null)
        {
            _globalSignals?.EmitSignal(nameof(GlobalSignals.Log),
                "This cue has an audio input. Remove it before adding an audio file.", (int)LogType.Warning);
            return;
        }

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
        if (_focusedCue?.GetAudioInputComponent() != null)
        {
            _globalSignals?.EmitSignal(nameof(GlobalSignals.Log),
                "This cue has an audio input. Remove it before adding an audio file.", (int)LogType.Warning);
            return;
        }

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
        if ((_focusedCue ?? multiCues?[0])?.GetAudioInputComponent() != null)
        {
            _globalSignals?.EmitSignal(nameof(GlobalSignals.Log),
                "This cue has an audio input. Remove it before adding an audio file.", (int)LogType.Warning);
            return;
        }

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
                        existing.ClearTimelineNodes();
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
                    comp.ClearTimelineNodes();
                }
                else if (sharedMeta != null)
                {
                    double fileDuration = sharedMeta.Duration > 0 ? sharedMeta.Duration : 0.0;
                    if (comp.StartTime >= fileDuration)
                        comp.StartTime = 0.0;
                    if (comp.EndTime >= 0 && (comp.EndTime > fileDuration || comp.EndTime <= comp.StartTime))
                        comp.EndTime = -1.0;
                    comp.ClampAllTimelineNodes();
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
                existingAudio.ClearTimelineNodes();
            }
        }
        else
        {
            _focusedAudioComponent = _focusedCue.AddAudioComponent(pathToStore);
        }

        _fileUrl.Text = pathToStore;
        _inspectorContent.Visible = true;
        SetSelectFileVisible(true);
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
                _focusedAudioComponent.ClearTimelineNodes();
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

                _focusedAudioComponent.ClampAllTimelineNodes();
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
        _waveformZoom?.Reset();
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
    /// Creates the node list under the timeline accordion (hint + rows).
    /// </summary>
    private void BuildTimelineNodeListHost()
    {
        if (_timelineAccordian == null)
            return;

        var margin = new MarginContainer { Name = "TimelineNodeListMargin" };
        margin.AddThemeConstantOverride("margin_left", 20);
        margin.AddThemeConstantOverride("margin_right", 20);
        margin.AddThemeConstantOverride("margin_top", 4);
        _timelineAccordian.AddChild(margin);

        _timelineNodeList = new VBoxContainer { Name = "TimelineNodeList" };
        _timelineNodeList.AddThemeConstantOverride("separation", 2);
        margin.AddChild(_timelineNodeList);

        _timelineNodeHint = new Label
        {
            Name = "TimelineNodeHint",
            Text = UiLocalizer.T("Click the waveform to add a node"),
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            MouseFilter = MouseFilterEnum.Ignore
        };
        _timelineNodeHint.AddThemeFontSizeOverride("font_size", 10);
        _timelineNodeHint.AddThemeColorOverride("font_color", GlobalStyles.SoftFontColor);
        _timelineNodeList.AddChild(_timelineNodeHint);

        _timelineNodeHeader = new HBoxContainer
        {
            Name = "TimelineNodeHeader",
            SizeFlagsHorizontal = SizeFlags.ExpandFill
        };
        _timelineNodeHeader.AddThemeConstantOverride("separation", 6);
        _timelineNodeHeader.AddChild(new Control { CustomMinimumSize = new Vector2(TimelineNodeDeleteWidth, 0) });
        _timelineNodeHeader.AddChild(MakeTimelineHeaderLabel("#", 28, HorizontalAlignment.Right));
        _timelineNodeHeader.AddChild(MakeTimelineHeaderLabel("Time", TimelineNodeTimeWidth));
        _timelineNodeHeader.AddChild(MakeTimelineHeaderLabel("Vol", 56, HorizontalAlignment.Center));
        _timelineNodeHeader.AddChild(MakeTimelineHeaderLabel("Rate", 48, HorizontalAlignment.Center));
        _timelineNodeHeader.AddChild(MakeTimelineHeaderLabel("Pitch", 48, HorizontalAlignment.Center));
        _timelineNodeHeader.AddChild(MakeTimelineHeaderLabel("Interp", 78, HorizontalAlignment.Center));
        _timelineNodeHeader.AddChild(MakeTimelineHeaderLabel("Continue", TimelineNodeContinueWidth, HorizontalAlignment.Center));
        _timelineNodeHeader.AddChild(MakeTimelineHeaderLabel("Loop", TimelineNodeLoopWidth, HorizontalAlignment.Center));
        _timelineNodeHeader.AddChild(MakeTimelineHeaderLabel("n", TimelineNodePlayCountWidth, HorizontalAlignment.Center));
        _timelineNodeHeader.Visible = false;
        _timelineNodeList.AddChild(_timelineNodeHeader);

        _timelineAddRow = new HBoxContainer { Name = "TimelineAddRow" };
        _timelineAddRow.AddThemeConstantOverride("separation", 8);

        _timelineAddNodeButton = new Button
        {
            Name = "TimelineAddNodeButton",
            Text = UiLocalizer.T("Add node"),
            FocusMode = FocusModeEnum.None,
            SizeFlagsHorizontal = SizeFlags.ShrinkBegin
        };
        _timelineAddNodeButton.AddThemeFontSizeOverride("font_size", 10);
        _timelineAddNodeButton.TooltipText = UiLocalizer.T("Add a node at the midpoint of the longest gap");
        _timelineAddNodeButton.Pressed += OnAddTimelineNodePressed;
        _timelineAddRow.AddChild(_timelineAddNodeButton);

        _timelineNodeList.AddChild(_timelineAddRow);
    }

    private const float TimelineNodeTimeWidth = 76f;
    private const float TimelineNodeContinueWidth = 64f;
    private const float TimelineNodeLoopWidth = 36f;
    private const float TimelineNodePlayCountWidth = 36f;
    private const float TimelineNodeDeleteWidth = 22f;

    /// <summary>
    /// Left-column delete control, or a matching empty slot for the locked end-of-file row.
    /// </summary>
    /// <param name="nodeId">Stable node id.</param>
    /// <param name="isFileEnd">When true, return a spacer instead of a button.</param>
    /// <returns>A 22px-wide control for the row.</returns>
    private Control MakeTimelineNodeDeleteControl(int nodeId, bool isFileEnd)
    {
        if (isFileEnd)
        {
            return new Control
            {
                CustomMinimumSize = new Vector2(TimelineNodeDeleteWidth, TimelineNodeDeleteWidth),
                MouseFilter = MouseFilterEnum.Ignore
            };
        }

        var del = new Button
        {
            FocusMode = FocusModeEnum.None,
            CustomMinimumSize = new Vector2(TimelineNodeDeleteWidth, TimelineNodeDeleteWidth),
            Flat = true
        };
        try
        {
            del.Icon = GetThemeIcon("DeleteBin", "AtlasIcons");
            del.ExpandIcon = true;
            del.AddThemeConstantOverride("icon_max_width", 12);
        }
        catch
        {
            del.Text = "×";
        }
        del.TooltipText = UiLocalizer.T("Remove node");
        del.Pressed += () => RemoveTimelineNodeAt(nodeId);
        return del;
    }

    private static void FillInterpOption(OptionButton button, TimelineVolumeInterpolation selected)
    {
        if (button == null || !GodotObject.IsInstanceValid(button))
            return;

        button.SetBlockSignals(true);
        try
        {
            button.Clear();
            foreach (var mode in TimelineVolume.All)
            {
                UiLocalizer.AddTranslatedItem(
                    button,
                    TimelineVolume.DisplayName(mode),
                    (int)mode,
                    TimelineVolume.Tooltip(mode));
            }

            UiLocalizer.SetTooltip(button, "Interpolation into this node (volume, rate, pitch)");
            int idx = button.GetItemIndex((int)selected);
            button.Selected = idx >= 0 ? idx : 0;
        }
        finally
        {
            button.SetBlockSignals(false);
        }
    }

    private void SyncTimelineAddRowState()
    {
        if (_timelineAddNodeButton == null)
            return;
        bool atCap = _focusedAudioComponent != null
            && _focusedAudioComponent.CountUserTimelineNodes() >= AudioComponent.MaxTimelineNodes;
        _timelineAddNodeButton.Disabled = atCap || _focusedAudioComponent == null;
    }

    private static Label MakeTimelineHeaderLabel(string text, float minWidth, HorizontalAlignment align = HorizontalAlignment.Left)
    {
        var label = new Label
        {
            Text = UiLocalizer.T(text),
            CustomMinimumSize = new Vector2(minWidth, 0),
            HorizontalAlignment = align,
            VerticalAlignment = VerticalAlignment.Center,
            MouseFilter = MouseFilterEnum.Ignore
        };
        label.AddThemeFontSizeOverride("font_size", 9);
        label.AddThemeColorOverride("font_color", GlobalStyles.SoftFontColor);
        return label;
    }

    /// <summary>
    /// Relocalizes the empty-state hint and rebuilds node row chrome.
    /// </summary>
    private void RefreshTimelineNodeListChrome()
    {
        if (_timelineNodeHint != null)
            _timelineNodeHint.Text = UiLocalizer.T("Click the waveform to add a node");
        if (_timelineAddNodeButton != null)
        {
            _timelineAddNodeButton.Text = UiLocalizer.T("Add node");
            _timelineAddNodeButton.TooltipText = UiLocalizer.T("Add a node at the midpoint of the longest gap");
        }
        if (_timelineNodeHeader != null)
        {
            string[] keys = { "#", "Time", "Vol", "Rate", "Pitch", "Interp", "Continue", "Loop", "n" };
            int i = 0;
            foreach (var child in _timelineNodeHeader.GetChildren())
            {
                if (child is Label label && i < keys.Length)
                    label.Text = UiLocalizer.T(keys[i++]);
            }
        }
        foreach (var kv in _timelineNodeInterpOptions)
        {
            var node = _focusedAudioComponent?.FindTimelineNode(kv.Key);
            FillInterpOption(kv.Value, node?.VolumeInterpolation ?? TimelineVolumeInterpolation.Linear);
        }
        SyncTimelineNodes();
    }

    /// <summary>
    /// Draws node slices, places drag handles, and refreshes the list under the waveform.
    /// </summary>
    private void SyncTimelineNodes()
    {
        _focusedAudioComponent?.EnsureFileEndNode();
        var ordered = _focusedAudioComponent?.GetTimelineNodesInTimeOrder()
                      ?? new List<AudioTimelineNode>();
        double duration = _focusedAudioComponent?.Metadata?.Duration ?? 0;
        if (duration <= 0)
            duration = 1;

        var draw = BuildTimelineNodeDrawList(ordered);
        int selectedNumber = -1;
        for (int i = 0; i < ordered.Count; i++)
        {
            if (ordered[i].Id == _selectedTimelineNodeId)
            {
                selectedNumber = i + 1;
                break;
            }
        }

        _waveformDisplay?.SetNodes(draw, selectedNumber);
        SyncTimelineNodeHandles(ordered, duration);
        SyncTimelineNodeList(ordered);
    }

    /// <summary>
    /// Ensures one time handle and one volume handle per timeline node.
    /// </summary>
    private void SyncTimelineNodeHandles(List<AudioTimelineNode> ordered, double duration)
    {
        var host = _startDragHandle?.GetParent() as Control;
        if (host == null)
            return;

        float width = _waveformPanel != null ? _waveformPanel.Size.X : 0f;
        var keep = new HashSet<int>();
        foreach (var node in ordered)
            keep.Add(node.Id);

        PruneTimelineHandles(_timelineNodeHandles, keep, OnTimelineNodeHandleInput);
        PruneTimelineHandles(_timelineVolumeHandles, keep, OnTimelineVolumeHandleInput);

        var squares = IndexHandlesByNodeId(_timelineNodeHandles);
        var circles = IndexHandlesByNodeId(_timelineVolumeHandles);
        int flagIndex = _startDragHandle != null && GodotObject.IsInstanceValid(_startDragHandle)
            ? Math.Max(0, _startDragHandle.GetIndex())
            : 0;

        foreach (var node in ordered)
        {
            float norm = (float)Math.Clamp(node.TimeSeconds / duration, 0, 1);
            if (!squares.TryGetValue(node.Id, out var square))
            {
                square = WaveformDisplay.CreateNodeHandle(node.Id);
                square.GuiInput += OnTimelineNodeHandleInput;
                host.AddChild(square);
                host.MoveChild(square, 0);
                _timelineNodeHandles.Add(square);
            }
            if (node.IsFileEnd)
            {
                square.Visible = false;
                square.MouseFilter = MouseFilterEnum.Ignore;
            }
            else
            {
                square.MouseFilter = MouseFilterEnum.Stop;
                _waveformDisplay?.PlaceNodeHandle(square, norm, width);
            }

            if (!circles.TryGetValue(node.Id, out var circle))
            {
                circle = WaveformDisplay.CreateVolumeHandle(node.Id);
                circle.GuiInput += OnTimelineVolumeHandleInput;
                host.AddChild(circle);
                host.MoveChild(circle, flagIndex);
                _timelineVolumeHandles.Add(circle);
                flagIndex = circle.GetIndex() + 1;
            }
            if (node.IsFileEnd)
                WaveformDisplay.StyleFileEndVolumeHandle(circle);
            else
                WaveformDisplay.StyleVolumeHandle(circle);
            _waveformDisplay?.PlaceVolumeHandle(circle, norm, node.VolumeLinear, width);
        }
    }

    private static void PruneTimelineHandles(
        List<Button> handles, HashSet<int> keep, Control.GuiInputEventHandler handler)
    {
        for (int i = handles.Count - 1; i >= 0; i--)
        {
            var handle = handles[i];
            int id = handle != null && GodotObject.IsInstanceValid(handle) && handle.HasMeta("node_id")
                ? handle.GetMeta("node_id").AsInt32()
                : -1;
            if (keep.Contains(id))
                continue;
            if (handle != null && GodotObject.IsInstanceValid(handle))
            {
                handle.GuiInput -= handler;
                handle.QueueFree();
            }
            handles.RemoveAt(i);
        }
    }

    private static Dictionary<int, Button> IndexHandlesByNodeId(List<Button> handles)
    {
        var byId = new Dictionary<int, Button>();
        foreach (var handle in handles)
        {
            if (handle == null || !GodotObject.IsInstanceValid(handle) || !handle.HasMeta("node_id"))
                continue;
            byId[handle.GetMeta("node_id").AsInt32()] = handle;
        }
        return byId;
    }

    /// <summary>
    /// Rebuilds list rows when node identity or order changes; otherwise refreshes times.
    /// </summary>
    private void SyncTimelineNodeList(List<AudioTimelineNode> ordered)
    {
        var ids = new List<int>(ordered.Count);
        foreach (var node in ordered)
            ids.Add(node.Id);

        bool structureChanged = ids.Count != _listedTimelineNodeIds.Count;
        if (!structureChanged)
        {
            for (int i = 0; i < ids.Count; i++)
            {
                if (ids[i] != _listedTimelineNodeIds[i])
                {
                    structureChanged = true;
                    break;
                }
            }
        }

        if (structureChanged && _draggingTimelineNodeId < 0)
            RebuildTimelineNodeList(ordered);
        else
            RefreshTimelineNodeListTimes(ordered);
    }

    /// <summary>
    /// Recreates numbered time rows under the waveform.
    /// </summary>
    /// <param name="ordered">Nodes in display (time) order.</param>
    private void RebuildTimelineNodeList(List<AudioTimelineNode> ordered)
    {
        if (_timelineNodeList == null)
            return;

        foreach (var child in _timelineNodeList.GetChildren())
        {
            if (child == _timelineNodeHint || child == _timelineNodeHeader || child == _timelineAddRow)
                continue;
            child.QueueFree();
        }

        _timelineNodeTimeEdits.Clear();
        _timelineNodeVolumeEdits.Clear();
        _timelineNodeRateEdits.Clear();
        _timelineNodePitchEdits.Clear();
        _timelineNodeInterpOptions.Clear();
        _timelineNodeContinueChecks.Clear();
        _timelineNodeLoopChecks.Clear();
        _timelineNodePlayCountEdits.Clear();
        _listedTimelineNodeIds = new List<int>(ordered.Count);
        foreach (var node in ordered)
            _listedTimelineNodeIds.Add(node.Id);

        if (_timelineNodeHint != null)
        {
            int userCount = _focusedAudioComponent?.CountUserTimelineNodes() ?? 0;
            _timelineNodeHint.Visible = userCount == 0;
            _timelineNodeHint.Text = UiLocalizer.T("Click the waveform to add a node");
        }
        if (_timelineNodeHeader != null)
            _timelineNodeHeader.Visible = ordered.Count > 0;
        SyncTimelineAddRowState();

        for (int i = 0; i < ordered.Count; i++)
        {
            var node = ordered[i];
            int number = i + 1;
            int capturedId = node.Id;
            var row = new HBoxContainer
            {
                Name = $"TimelineNodeRow_{capturedId}",
                SizeFlagsHorizontal = SizeFlags.Fill
            };
            row.AddThemeConstantOverride("separation", 6);

            row.AddChild(MakeTimelineNodeDeleteControl(capturedId, node.IsFileEnd));

            var num = new Label
            {
                Text = node.IsFileEnd ? UiLocalizer.T("EOF") : number.ToString(),
                CustomMinimumSize = new Vector2(28, 0),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                MouseFilter = MouseFilterEnum.Stop
            };
            num.AddThemeFontSizeOverride("font_size", 10);
            num.AddThemeColorOverride("font_color", GlobalStyles.SoftFontColor);
            num.GuiInput += @event =>
            {
                if (@event is InputEventMouseButton mb && mb.Pressed && mb.ButtonIndex == MouseButton.Left)
                {
                    _selectedTimelineNodeId = capturedId;
                    SyncTimelineNodes();
                    num.AcceptEvent();
                }
            };
            row.AddChild(num);

            var time = new LineEdit
            {
                Text = UiUtilities.FormatTime(node.TimeSeconds),
                CustomMinimumSize = new Vector2(TimelineNodeTimeWidth, 0),
                SizeFlagsHorizontal = SizeFlags.ShrinkBegin,
                Alignment = HorizontalAlignment.Center
            };
            time.AddThemeFontSizeOverride("font_size", 10);
            time.Editable = !node.IsFileEnd;
            time.TooltipText = node.IsFileEnd
                ? UiLocalizer.T("End of file")
                : UiLocalizer.T("End of this node's region");
            if (!node.IsFileEnd)
            {
                time.TextSubmitted += text => OnTimelineNodeTimeSubmitted(capturedId, text, time);
                time.FocusExited += () =>
                {
                    if (GodotObject.IsInstanceValid(time))
                        OnTimelineNodeTimeSubmitted(capturedId, time.Text, time);
                };
            }
            _timelineNodeTimeEdits[capturedId] = time;
            row.AddChild(time);

            var vol = new LineEdit
            {
                Text = UiUtilities.FormatComponentVolumeDb(node.VolumeLinear),
                CustomMinimumSize = new Vector2(56, 0),
                Alignment = HorizontalAlignment.Center
            };
            vol.AddThemeFontSizeOverride("font_size", 10);
            vol.TooltipText = UiLocalizer.T("Volume relative to the component");
            vol.TextSubmitted += text => OnTimelineNodeVolumeSubmitted(capturedId, text, vol);
            vol.FocusExited += () =>
            {
                if (GodotObject.IsInstanceValid(vol))
                    OnTimelineNodeVolumeSubmitted(capturedId, vol.Text, vol);
            };
            LineEditDbDragSlider.Enable(vol, new LineEditDbDragSlider.Config
            {
                MinDb = LineEditDbDragSlider.DefaultMinDb,
                MaxDb = LineEditDbDragSlider.DefaultMaxDb,
                FormatSigned = true,
                ValueChanged = db => OnTimelineNodeVolumeScrubbed(capturedId, db)
            });
            _timelineNodeVolumeEdits[capturedId] = vol;
            row.AddChild(vol);

            var rate = new LineEdit
            {
                Text = FormatPlayRate(node.RateScale),
                CustomMinimumSize = new Vector2(48, 0),
                Alignment = HorizontalAlignment.Center
            };
            rate.AddThemeFontSizeOverride("font_size", 10);
            rate.TooltipText = UiLocalizer.T("Play rate relative to the component");
            rate.TextSubmitted += text => OnTimelineNodeRateSubmitted(capturedId, text, rate);
            rate.FocusExited += () =>
            {
                if (GodotObject.IsInstanceValid(rate))
                    OnTimelineNodeRateSubmitted(capturedId, rate.Text, rate);
            };
            _timelineNodeRateEdits[capturedId] = rate;
            row.AddChild(rate);

            var pitch = new LineEdit
            {
                Text = FormatPitchCents(node.PitchCents),
                CustomMinimumSize = new Vector2(48, 0),
                Alignment = HorizontalAlignment.Center
            };
            pitch.AddThemeFontSizeOverride("font_size", 10);
            pitch.TooltipText = UiLocalizer.T("Pitch offset in cents relative to the component");
            pitch.TextSubmitted += text => OnTimelineNodePitchSubmitted(capturedId, text, pitch);
            pitch.FocusExited += () =>
            {
                if (GodotObject.IsInstanceValid(pitch))
                    OnTimelineNodePitchSubmitted(capturedId, pitch.Text, pitch);
            };
            _timelineNodePitchEdits[capturedId] = pitch;
            row.AddChild(pitch);

            var interp = new OptionButton
            {
                FocusMode = FocusModeEnum.None,
                CustomMinimumSize = new Vector2(78, 0),
                SizeFlagsHorizontal = SizeFlags.ShrinkBegin,
                FitToLongestItem = false
            };
            interp.AddThemeFontSizeOverride("font_size", 10);
            FillInterpOption(interp, node.VolumeInterpolation);
            interp.ItemSelected += index => OnTimelineNodeInterpSelected(capturedId, index);
            _timelineNodeInterpOptions[capturedId] = interp;
            row.AddChild(interp);

            var continueBox = new CheckBox
            {
                ButtonPressed = node.ContinueRegion,
                FocusMode = FocusModeEnum.None,
                CustomMinimumSize = new Vector2(TimelineNodeContinueWidth, 0)
            };
            continueBox.TooltipText = UiLocalizer.T("Continue through this node without using it as a loop bound (volume, rate, and pitch only)");
            continueBox.Toggled += state => OnTimelineNodeContinueToggled(capturedId, state);
            _timelineNodeContinueChecks[capturedId] = continueBox;
            row.AddChild(continueBox);

            var loop = new CheckBox
            {
                ButtonPressed = node.Loop,
                FocusMode = FocusModeEnum.None,
                CustomMinimumSize = new Vector2(TimelineNodeLoopWidth, 0),
                Visible = !node.ContinueRegion
            };
            loop.TooltipText = UiLocalizer.T("Loop the region before this node");
            loop.Toggled += state => OnTimelineNodeLoopToggled(capturedId, state);
            _timelineNodeLoopChecks[capturedId] = loop;
            row.AddChild(loop);

            var count = new LineEdit
            {
                Text = node.PlayCount.ToString(),
                CustomMinimumSize = new Vector2(TimelineNodePlayCountWidth, 0),
                Alignment = HorizontalAlignment.Center,
                Editable = !node.Loop,
                Visible = !node.ContinueRegion
            };
            count.AddThemeFontSizeOverride("font_size", 10);
            count.TooltipText = UiLocalizer.T("Plays of the region before this node");
            count.TextSubmitted += text => OnTimelineNodePlayCountSubmitted(capturedId, text, count);
            count.FocusExited += () =>
            {
                if (GodotObject.IsInstanceValid(count))
                    OnTimelineNodePlayCountSubmitted(capturedId, count.Text, count);
            };
            _timelineNodePlayCountEdits[capturedId] = count;
            row.AddChild(count);

            _timelineNodeList.AddChild(row);
        }

        if (_timelineAddRow != null && GodotObject.IsInstanceValid(_timelineAddRow))
            _timelineNodeList.MoveChild(_timelineAddRow, _timelineNodeList.GetChildCount() - 1);
    }

    /// <summary>
    /// Updates time fields in place without rebuilding rows.
    /// </summary>
    private void RefreshTimelineNodeListTimes(List<AudioTimelineNode> ordered)
    {
        if (_timelineNodeHint != null)
            _timelineNodeHint.Visible = (_focusedAudioComponent?.CountUserTimelineNodes() ?? 0) == 0;
        if (_timelineNodeHeader != null)
            _timelineNodeHeader.Visible = ordered.Count > 0;
        SyncTimelineAddRowState();

        foreach (var node in ordered)
        {
            if (_timelineNodeTimeEdits.TryGetValue(node.Id, out var time)
                && time != null && GodotObject.IsInstanceValid(time) && !time.HasFocus())
                time.Text = UiUtilities.FormatTime(node.TimeSeconds);

            if (_timelineNodeVolumeEdits.TryGetValue(node.Id, out var vol)
                && vol != null && GodotObject.IsInstanceValid(vol) && !vol.HasFocus())
                vol.Text = UiUtilities.FormatComponentVolumeDb(node.VolumeLinear);

            if (_timelineNodeRateEdits.TryGetValue(node.Id, out var rate)
                && rate != null && GodotObject.IsInstanceValid(rate) && !rate.HasFocus())
                rate.Text = FormatPlayRate(node.RateScale);

            if (_timelineNodePitchEdits.TryGetValue(node.Id, out var pitch)
                && pitch != null && GodotObject.IsInstanceValid(pitch) && !pitch.HasFocus())
                pitch.Text = FormatPitchCents(node.PitchCents);

            if (_timelineNodeInterpOptions.TryGetValue(node.Id, out var interp)
                && interp != null && GodotObject.IsInstanceValid(interp))
            {
                int idx = interp.GetItemIndex((int)node.VolumeInterpolation);
                if (idx >= 0 && interp.Selected != idx)
                {
                    interp.SetBlockSignals(true);
                    interp.Selected = idx;
                    interp.SetBlockSignals(false);
                }
            }

            if (_timelineNodeContinueChecks.TryGetValue(node.Id, out var continueBox)
                && continueBox != null && GodotObject.IsInstanceValid(continueBox)
                && continueBox.ButtonPressed != node.ContinueRegion)
                continueBox.SetPressedNoSignal(node.ContinueRegion);

            if (_timelineNodeLoopChecks.TryGetValue(node.Id, out var loop)
                && loop != null && GodotObject.IsInstanceValid(loop))
            {
                loop.Visible = !node.ContinueRegion;
                if (loop.ButtonPressed != node.Loop)
                    loop.SetPressedNoSignal(node.Loop);
            }

            if (_timelineNodePlayCountEdits.TryGetValue(node.Id, out var count)
                && count != null && GodotObject.IsInstanceValid(count))
            {
                count.Visible = !node.ContinueRegion;
                count.Editable = !node.Loop;
                if (!count.HasFocus())
                    count.Text = node.PlayCount.ToString();
            }
        }
    }

    /// <summary>
    /// Frees overlay drag handles for timeline nodes.
    /// </summary>
    private void ClearTimelineNodeHandles()
    {
        foreach (var handle in _timelineNodeHandles)
        {
            if (handle == null || !GodotObject.IsInstanceValid(handle))
                continue;
            handle.GuiInput -= OnTimelineNodeHandleInput;
            handle.QueueFree();
        }
        _timelineNodeHandles.Clear();

        foreach (var handle in _timelineVolumeHandles)
        {
            if (handle == null || !GodotObject.IsInstanceValid(handle))
                continue;
            handle.GuiInput -= OnTimelineVolumeHandleInput;
            handle.QueueFree();
        }
        _timelineVolumeHandles.Clear();
    }

    private const double TimelineNodeClickDelaySec = 0.2;

    /// <summary>
    /// Starts a short delay so a double-click Fit does not also create a node.
    /// </summary>
    /// <param name="fileNorm">File-normalized click position.</param>
    private void OnTimelinePanelClicked(float fileNorm)
    {
        if (_isSyncingUi || _globalData?.HistoryManager?.IsRestoring == true)
            return;
        if (_focusedAudioComponent == null)
            return;

        CancelPendingTimelineNodeClick();
        _pendingTimelineNodeNorm = fileNorm;
        _pendingTimelineNodeCueId = _focusedCue?.Id ?? -1;
        var tree = GetTree();
        if (tree == null)
            return;
        _pendingTimelineNodeTimer = tree.CreateTimer(TimelineNodeClickDelaySec);
        _pendingTimelineNodeTimer.Timeout += OnPendingTimelineNodeClickTimeout;
    }

    /// <summary>
    /// Cancels a pending node-create when the click was a double-click Fit.
    /// </summary>
    private void OnTimelinePanelDoubleClicked()
    {
        CancelPendingTimelineNodeClick();
    }

    private void OnPendingTimelineNodeClickTimeout()
    {
        float norm = _pendingTimelineNodeNorm;
        int cueId = _pendingTimelineNodeCueId;
        CancelPendingTimelineNodeClick();
        if (_focusedCue == null || _focusedCue.Id != cueId)
            return;
        AddTimelineNodeAtFileNorm(norm);
    }

    /// <summary>
    /// Drops a pending click-to-add so a double-click Fit does not create a node.
    /// </summary>
    private void CancelPendingTimelineNodeClick()
    {
        if (_pendingTimelineNodeTimer != null)
        {
            _pendingTimelineNodeTimer.Timeout -= OnPendingTimelineNodeClickTimeout;
            _pendingTimelineNodeTimer = null;
        }
        _pendingTimelineNodeNorm = -1f;
        _pendingTimelineNodeCueId = -1;
    }

    /// <summary>
    /// Adds a numbered marker at the given file-normalized time.
    /// </summary>
    /// <param name="fileNorm">0–1 of the file.</param>
    private void AddTimelineNodeAtFileNorm(float fileNorm)
    {
        if (_focusedAudioComponent == null)
            return;
        if (_isSyncingUi || _globalData?.HistoryManager?.IsRestoring == true)
            return;

        double duration = _focusedAudioComponent.Metadata?.Duration ?? 0;
        if (duration <= 0)
            return;

        if (_focusedAudioComponent.CountUserTimelineNodes() >= AudioComponent.MaxTimelineNodes)
        {
            _globalSignals?.EmitSignal(nameof(GlobalSignals.Log),
                $"Audio inspector: maximum of {AudioComponent.MaxTimelineNodes} timeline nodes.", 1);
            return;
        }

        double time = _focusedAudioComponent.ClampTimelineNodeTime(fileNorm * duration);
        foreach (var existing in _focusedAudioComponent.TimelineNodes)
        {
            if (existing != null && Math.Abs(existing.TimeSeconds - time) < 0.001)
                return;
        }

        RecordAudioHistory("Add timeline node");
        var node = _focusedAudioComponent.AddTimelineNode(time);
        if (node == null)
            return;
        _selectedTimelineNodeId = node.Id;
        SyncDuration();
        SyncTimelineNodes();
    }

    /// <summary>
    /// Adds a node at the midpoint of the longest gap on the file.
    /// </summary>
    private void OnAddTimelineNodePressed()
    {
        if (_focusedAudioComponent == null)
            return;
        if (_isSyncingUi || _globalData?.HistoryManager?.IsRestoring == true)
            return;

        double time = _focusedAudioComponent.PickNewTimelineNodeTime();
        if (time < 0)
            return;
        double duration = _focusedAudioComponent.Metadata?.Duration ?? 0;
        if (duration <= 0)
            return;
        AddTimelineNodeAtFileNorm((float)Math.Clamp(time / duration, 0, 1));
    }

    /// <summary>
    /// Live relative-volume scrub (history coalesced; commit on TextSubmitted).
    /// </summary>
    private void OnTimelineNodeVolumeScrubbed(int nodeId, float db)
    {
        if (_focusedAudioComponent == null)
            return;
        if (_isSyncingUi || _globalData?.HistoryManager?.IsRestoring == true)
            return;
        var node = _focusedAudioComponent.FindTimelineNode(nodeId);
        if (node == null)
            return;

        float linear = UiUtilities.DbToLinear(db);
        if (Math.Abs(node.VolumeLinear - linear) < 1e-6f)
            return;

        RecordAudioHistory("Edit timeline node volume", AudioCoalesceKey($"node-vol:{nodeId}"));
        node.VolumeLinear = linear;
        _waveformDisplay?.SetNodes(
            BuildTimelineNodeDrawList(_focusedAudioComponent.GetTimelineNodesInTimeOrder()),
            SelectedTimelineNodeNumber());
    }

    private void OnTimelineNodeVolumeSubmitted(int nodeId, string text, LineEdit field)
    {
        if (_focusedAudioComponent == null || field == null)
            return;
        if (_isSyncingUi || _globalData?.HistoryManager?.IsRestoring == true)
            return;
        var node = _focusedAudioComponent.FindTimelineNode(nodeId);
        if (node == null)
            return;

        if (!float.TryParse(text.Replace("dB", "").Trim(), out var dbValue))
        {
            field.Text = UiUtilities.FormatComponentVolumeDb(node.VolumeLinear);
            return;
        }

        dbValue = Mathf.Clamp(dbValue, UiUtilities.MinVolumeDb, UiUtilities.MaxComponentGainDb);
        float linear = UiUtilities.DbToLinear(dbValue);
        field.Text = UiUtilities.FormatComponentVolumeDb(linear);
        var key = AudioCoalesceKey($"node-vol:{nodeId}");
        if (Math.Abs(node.VolumeLinear - linear) >= 1e-6f)
        {
            RecordAudioHistory("Edit timeline node volume", key);
            node.VolumeLinear = linear;
        }

        if (!string.IsNullOrEmpty(key))
            InspectorMultiEditSupport.EndCoalesce(_globalData, UseMultiHistory(), key, key);
        SyncTimelineNodes();
    }

    private void OnTimelineNodeLoopToggled(int nodeId, bool state)
    {
        if (_focusedAudioComponent == null)
            return;
        if (_isSyncingUi || _globalData?.HistoryManager?.IsRestoring == true)
            return;
        var node = _focusedAudioComponent.FindTimelineNode(nodeId);
        if (node == null || node.Loop == state)
            return;

        RecordAudioHistory("Edit timeline node loop");
        node.Loop = state;
        SyncDuration();
        SyncTimelineNodes();
    }

    private void OnTimelineNodePlayCountSubmitted(int nodeId, string text, LineEdit field)
    {
        if (_focusedAudioComponent == null || field == null)
            return;
        if (_isSyncingUi || _globalData?.HistoryManager?.IsRestoring == true)
            return;
        var node = _focusedAudioComponent.FindTimelineNode(nodeId);
        if (node == null)
            return;

        if (!int.TryParse(text, out var playCount) || playCount < 1)
        {
            field.Text = node.PlayCount.ToString();
            return;
        }

        if (node.PlayCount == playCount)
        {
            field.Text = playCount.ToString();
            return;
        }

        RecordAudioHistory("Edit timeline node play count");
        node.PlayCount = playCount;
        field.Text = node.PlayCount.ToString();
        SyncDuration();
        SyncTimelineNodes();
    }

    private void OnTimelineNodeInterpSelected(int nodeId, long index)
    {
        if (_focusedAudioComponent == null)
            return;
        if (_isSyncingUi || _globalData?.HistoryManager?.IsRestoring == true)
            return;
        var node = _focusedAudioComponent.FindTimelineNode(nodeId);
        if (node == null)
            return;
        if (!_timelineNodeInterpOptions.TryGetValue(nodeId, out var button)
            || button == null || !GodotObject.IsInstanceValid(button))
            return;

        var mode = TimelineVolume.FromInt(button.GetItemId((int)index));
        if (node.VolumeInterpolation == mode)
            return;

        RecordAudioHistory("Edit timeline node interpolation");
        node.VolumeInterpolation = mode;
        SyncTimelineNodes();
    }

    private void OnTimelineNodeContinueToggled(int nodeId, bool state)
    {
        if (_focusedAudioComponent == null)
            return;
        if (_isSyncingUi || _globalData?.HistoryManager?.IsRestoring == true)
            return;
        var node = _focusedAudioComponent.FindTimelineNode(nodeId);
        if (node == null || node.ContinueRegion == state)
            return;

        RecordAudioHistory("Edit timeline node continue");
        node.ContinueRegion = state;
        SyncDuration();
        SyncTimelineNodes();
    }

    private void OnTimelineNodeRateSubmitted(int nodeId, string text, LineEdit field)
    {
        if (_focusedAudioComponent == null || field == null)
            return;
        if (_isSyncingUi || _globalData?.HistoryManager?.IsRestoring == true)
            return;
        var node = _focusedAudioComponent.FindTimelineNode(nodeId);
        if (node == null)
            return;

        if (!double.TryParse((text ?? "").Trim(), out double rate) || rate <= 0)
        {
            field.Text = FormatPlayRate(node.RateScale);
            return;
        }

        rate = AudioComponent.ClampPlayRate(rate);
        field.Text = FormatPlayRate(rate);
        if (Math.Abs(node.RateScale - rate) < 1e-6)
            return;

        RecordAudioHistory("Edit timeline node rate");
        node.RateScale = (float)rate;
        SyncDuration();
        SyncTimelineNodes();
    }

    private void OnTimelineNodePitchSubmitted(int nodeId, string text, LineEdit field)
    {
        if (_focusedAudioComponent == null || field == null)
            return;
        if (_isSyncingUi || _globalData?.HistoryManager?.IsRestoring == true)
            return;
        var node = _focusedAudioComponent.FindTimelineNode(nodeId);
        if (node == null)
            return;

        string trimmed = (text ?? "").Trim().TrimEnd('¢', 'c', 'C');
        if (!float.TryParse(trimmed, out float cents))
        {
            field.Text = FormatPitchCents(node.PitchCents);
            return;
        }

        cents = AudioComponent.ClampPitchCents(cents);
        field.Text = FormatPitchCents(cents);
        if (Math.Abs(node.PitchCents - cents) < 0.5f)
            return;

        RecordAudioHistory("Edit timeline node pitch");
        node.PitchCents = cents;
        SyncTimelineNodes();
    }

    private List<WaveformDisplay.TimelineNodeDraw> BuildTimelineNodeDrawList(
        List<AudioTimelineNode> ordered)
    {
        double duration = _focusedAudioComponent?.Metadata?.Duration ?? 0;
        if (duration <= 0)
            duration = 1;
        var draw = new List<WaveformDisplay.TimelineNodeDraw>(ordered.Count);
        for (int i = 0; i < ordered.Count; i++)
        {
            var node = ordered[i];
            float norm = (float)Math.Clamp(node.TimeSeconds / duration, 0, 1);
            draw.Add(new WaveformDisplay.TimelineNodeDraw
            {
                Number = i + 1,
                FileNorm = norm,
                VolumeLinear = node.VolumeLinear,
                RateScale = node.RateScale,
                PitchCents = node.PitchCents,
                Loop = node.Loop,
                PlayCount = node.PlayCount,
                ContinueRegion = node.ContinueRegion,
                IsFileEnd = node.IsFileEnd,
                Interpolation = node.VolumeInterpolation
            });
        }
        return draw;
    }

    private int SelectedTimelineNodeNumber()
    {
        if (_focusedAudioComponent == null || _selectedTimelineNodeId < 0)
            return -1;
        var ordered = _focusedAudioComponent.GetTimelineNodesInTimeOrder();
        for (int i = 0; i < ordered.Count; i++)
        {
            if (ordered[i].Id == _selectedTimelineNodeId)
                return i + 1;
        }
        return -1;
    }

    private void OnTimelineNodeHandleInput(InputEvent @event)
    {
        HandleTimelineNodeDrag(@event, volumePoint: false);
    }

    private void OnTimelineVolumeHandleInput(InputEvent @event)
    {
        HandleTimelineNodeDrag(@event, volumePoint: true);
    }

    private void HandleTimelineNodeDrag(InputEvent @event, bool volumePoint)
    {
        if (@event is InputEventMouseButton mouseButton && mouseButton.ButtonIndex == MouseButton.Left)
        {
            if (mouseButton.Pressed)
            {
                int id = HitTestTimelineHandles(volumePoint
                    ? _timelineVolumeHandles
                    : _timelineNodeHandles);
                if (id < 0)
                    return;
                _selectedTimelineNodeId = id;
                RecordAudioHistory("Move timeline node", AudioCoalesceKey($"node-drag:{id}"));
                _draggingTimelineNodeId = id;
                _draggingTimelineVolume = volumePoint;
                var handles = volumePoint ? _timelineVolumeHandles : _timelineNodeHandles;
                foreach (var handle in handles)
                {
                    if (handle != null && GodotObject.IsInstanceValid(handle)
                        && handle.HasMeta("node_id") && handle.GetMeta("node_id").AsInt32() == id)
                    {
                        handle.AcceptEvent();
                        break;
                    }
                }
                SyncTimelineNodes();
            }
            else if (_draggingTimelineNodeId >= 0)
            {
                var key = AudioCoalesceKey($"node-drag:{_draggingTimelineNodeId}");
                _draggingTimelineNodeId = -1;
                _draggingTimelineVolume = false;
                if (!string.IsNullOrEmpty(key))
                    InspectorMultiEditSupport.EndCoalesce(_globalData, UseMultiHistory(), key, key);
                SyncDuration();
                SyncTimelineNodes();
            }
        }
        else if (@event is InputEventMouseMotion && _draggingTimelineNodeId >= 0)
        {
            MoveDraggingTimelineNode();
        }
    }

    /// <summary>
    /// Node id under the pointer among the given overlay handles.
    /// </summary>
    /// <param name="handles">Handle set to test.</param>
    /// <returns>Stable node id, or −1.</returns>
    private static int HitTestTimelineHandles(List<Button> handles)
    {
        foreach (var handle in handles)
        {
            if (handle == null || !GodotObject.IsInstanceValid(handle) || !handle.Visible)
                continue;
            var local = handle.GetLocalMousePosition();
            if (local.X >= 0 && local.Y >= 0 && local.X <= handle.Size.X && local.Y <= handle.Size.Y)
                return handle.GetMeta("node_id").AsInt32();
        }

        return -1;
    }

    /// <summary>
    /// Moves the node currently being dragged to the pointer (time, and volume when grabbing the circle).
    /// </summary>
    private void MoveDraggingTimelineNode()
    {
        if (_focusedAudioComponent == null || _waveformDisplay == null || _waveformPanel == null)
            return;
        var node = _focusedAudioComponent.FindTimelineNode(_draggingTimelineNodeId);
        if (node == null)
            return;

        double duration = _focusedAudioComponent.Metadata?.Duration ?? 0;
        if (duration <= 0)
            return;

        var local = _waveformPanel.GetLocalMousePosition();
        if (!node.IsFileEnd)
        {
            float norm = _waveformDisplay.XToFileNorm(local.X);
            node.TimeSeconds = _focusedAudioComponent.ClampTimelineNodeTime(norm * duration);
        }
        if (_draggingTimelineVolume)
            node.VolumeLinear = _waveformDisplay.YToVolumeLinear(local.Y);
        SyncTimelineNodes();
    }

    /// <summary>
    /// Commits a typed time for a timeline node.
    /// </summary>
    /// <param name="nodeId">Stable node id.</param>
    /// <param name="text">Submitted field text.</param>
    /// <param name="field">The time LineEdit.</param>
    private void OnTimelineNodeTimeSubmitted(int nodeId, string text, LineEdit field)
    {
        if (_focusedAudioComponent == null || field == null)
            return;
        if (_isSyncingUi || _globalData?.HistoryManager?.IsRestoring == true)
            return;
        var node = _focusedAudioComponent.FindTimelineNode(nodeId);
        if (node == null || node.IsFileEnd)
            return;

        var formatted = UiUtilities.ParseAndFormatTime(text, out var seconds, out _, out bool isValid);
        if (!isValid || string.IsNullOrEmpty(formatted))
        {
            field.Text = UiUtilities.FormatTime(node.TimeSeconds);
            return;
        }

        double clamped = _focusedAudioComponent.ClampTimelineNodeTime(seconds);
        if (Math.Abs(node.TimeSeconds - clamped) < 1e-9)
        {
            field.Text = UiUtilities.FormatTime(clamped);
            return;
        }

        RecordAudioHistory("Edit timeline node");
        node.TimeSeconds = clamped;
        field.Text = UiUtilities.FormatTime(clamped);
        SyncDuration();
        SyncTimelineNodes();
    }

    /// <summary>
    /// Deletes a timeline node and records history.
    /// </summary>
    /// <param name="nodeId">Stable node id.</param>
    private void RemoveTimelineNodeAt(int nodeId)
    {
        if (_focusedAudioComponent == null)
            return;
        if (_isSyncingUi || _globalData?.HistoryManager?.IsRestoring == true)
            return;
        var existing = _focusedAudioComponent.FindTimelineNode(nodeId);
        if (existing == null || existing.IsFileEnd)
            return;

        RecordAudioHistory("Remove timeline node");
        _focusedAudioComponent.RemoveTimelineNode(nodeId);
        if (_selectedTimelineNodeId == nodeId)
            _selectedTimelineNodeId = -1;
        if (_draggingTimelineNodeId == nodeId)
        {
            _draggingTimelineNodeId = -1;
            _draggingTimelineVolume = false;
        }
        SyncDuration();
        SyncTimelineNodes();
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

        if (accordian.Name == "TimelineAccordian")
        {
            await DrawWaveform();
        }
    }
}
