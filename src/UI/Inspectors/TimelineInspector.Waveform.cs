// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

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
using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Cue2.UI.Inspectors;

/// <summary>
/// Inspector for displaying and editing cue timelines, including hierarchical children.
/// Features a fixed track sidebar, time ruler, cue-colored bars, optional waveforms,
/// playhead scrubbing, and play-from-playhead. Parent rows can collapse to hide descendants.
/// </summary>
/// <summary>
/// Partial: Timeline waveform layer attach and peak data
/// </summary>
public partial class TimelineInspector
{
    private static void AttachWaveformLayer(
        ColorRect bar,
        WaveformPeaks peaks,
        float startNorm,
        float endNorm,
        int playCount)
    {
        if (bar == null || peaks == null) return;
        var existing = bar.GetNodeOrNull<CueBarWaveform>("Waveform");
        if (existing != null)
        {
            existing.Peaks = peaks;
            existing.StartNorm = startNorm;
            existing.EndNorm = endNorm;
            existing.PlayCount = Math.Max(1, playCount);
            existing.Size = bar.Size;
            existing.QueueRedraw();
            return;
        }

        var wave = new CueBarWaveform
        {
            Name = "Waveform",
            MouseFilter = MouseFilterEnum.Ignore,
            Peaks = peaks,
            StartNorm = startNorm,
            EndNorm = endNorm,
            PlayCount = Math.Max(1, playCount),
            WaveColor = GlobalStyles.LowColor1.Lightened(0.25f),
            DividerColor = new Color(1f, 1f, 1f, 0.45f)
        };
        bar.AddChild(wave);
        bar.MoveChild(wave, 0);
        wave.Position = Vector2.Zero;
        wave.Size = bar.Size;
    }

    /// <summary>
    /// For each visible cue, load waveform peaks the same way inspectors do:
    /// component payload → session disk cache → generate, then store on the component.
    /// </summary>
    private async Task EnsureWaveformsForItemsAsync(List<TimelineItem> items, int gen)
    {
        if (_mediaEngine == null || items == null) return;

        // Cancel prior batch (rapid rebuild / toggle waveforms / focus) — single-flight engine
        // still shares in-flight path jobs; this abandons UI wait and stops starting more cues.
        try { _waveformCts?.Cancel(); } catch { /* ignore */ }
        try { _waveformCts?.Dispose(); } catch { /* ignore */ }
        _waveformCts = new CancellationTokenSource();
        var ct = _waveformCts.Token;

        foreach (var item in items)
        {
            if (gen != _timelineLoadGeneration || !IsInstanceValid(this) || ct.IsCancellationRequested)
                return;

            var cue = item.Cue;
            if (cue == null) continue;

            try
            {
                await EnsureCueWaveformDataAsync(cue, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                GD.PrintErr($"TimelineInspector:EnsureWaveformsForItemsAsync - Cue {cue.Id}: {ex.Message}");
                continue;
            }

            if (gen != _timelineLoadGeneration || !IsInstanceValid(this) || ct.IsCancellationRequested)
                return;

            if (!_cueToBar.TryGetValue(cue, out var bar) || bar == null || !IsInstanceValid(bar))
                continue;

            if (TryGetCueWaveformSource(cue, out var peaks, out float startNorm, out float endNorm, out int playCount))
            {
                AttachWaveformLayer(bar, peaks, startNorm, endNorm, playCount);
                if (_cueToRow.ContainsKey(cue))
                {
                    double start = ComputeActionStart(cue);
                    ApplyBarGeometry(bar, cue, start, out _, out _);
                }
            }
        }
    }

    /// <summary>
    /// Ensures <see cref="AudioComponent.WaveformData"/> / video waveform is populated via
    /// <see cref="MediaEngine.GenerateWaveformAsync"/> (cache hit or generate).
    /// </summary>
    private async Task EnsureCueWaveformDataAsync(Cue cue, CancellationToken ct = default)
    {
        if (cue == null || _mediaEngine == null) return;

        var audio = cue.GetAudioComponent();
        if (audio != null && !string.IsNullOrEmpty(audio.AudioFile))
        {
            if (audio.WaveformData == null || audio.WaveformData.Length == 0)
            {
                byte[] data = await _mediaEngine.GenerateWaveformAsync(audio.AudioFile, ct);
                if (data != null && data.Length > 0)
                    audio.WaveformData = data;
            }
            return;
        }

        var video = cue.GetVideoComponent();
        if (video != null && video.UseAudio && !video.IsImage && !string.IsNullOrEmpty(video.VideoFile))
        {
            if (video.WaveformData == null || video.WaveformData.Length == 0)
            {
                byte[] data = await _mediaEngine.GenerateWaveformAsync(video.VideoFile, ct);
                if (data != null && data.Length > 0)
                    video.WaveformData = data;
            }
        }
    }

    /// <summary>
    /// Resolves waveform peak data for a cue from dedicated audio or video-embedded audio.
    /// </summary>
    /// <returns>True when peaks are available to draw.</returns>
    private static bool TryGetCueWaveformSource(
        Cue cue,
        out WaveformPeaks peaks,
        out float startNorm,
        out float endNorm,
        out int playCount)
    {
        peaks = null;
        startNorm = 0f;
        endNorm = 1f;
        playCount = 1;
        if (cue == null) return false;

        var audio = cue.GetAudioComponent();
        if (audio != null && audio.WaveformData != null && audio.WaveformData.Length > 0)
        {
            peaks = WaveformPeaks.FromBytes(audio.WaveformData);
            if (peaks == null || peaks.BinCount < 1) return false;

            double fileDur = audio.Metadata?.Duration ?? 0;
            if (fileDur <= 1e-9 && audio.Duration > 0)
                fileDur = audio.StartTime + audio.Duration;
            if (fileDur > 1e-9)
            {
                startNorm = (float)Math.Clamp(audio.StartTime / fileDur, 0.0, 1.0);
                endNorm = audio.EndTime < 0
                    ? 1f
                    : (float)Math.Clamp(audio.EndTime / fileDur, startNorm + 1e-6, 1.0);
            }
            playCount = audio.Loop ? 1 : Math.Max(1, audio.PlayCount);
            return true;
        }

        var video = cue.GetVideoComponent();
        if (video != null && video.UseAudio && video.WaveformData != null && video.WaveformData.Length > 0)
        {
            peaks = WaveformPeaks.FromBytes(video.WaveformData);
            if (peaks == null || peaks.BinCount < 1) return false;

            double fileDur = video.Metadata?.Duration ?? 0;
            if (fileDur <= 1e-9 && video.Duration > 0)
                fileDur = video.StartTime + video.Duration;
            if (fileDur > 1e-9 && !video.IsImage)
            {
                startNorm = (float)Math.Clamp(video.StartTime / fileDur, 0.0, 1.0);
                endNorm = video.EndTime < 0
                    ? 1f
                    : (float)Math.Clamp(video.EndTime / fileDur, startNorm + 1e-6, 1.0);
            }
            playCount = video.Loop ? 1 : Math.Max(1, video.PlayCount);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Recursively collects cues and their children into a list for timeline rendering.
    /// Skips descendants of collapsed parents.
    /// </summary>
    /// <param name="cue">The current cue to add.</param>
    /// <param name="items">The list to populate with timeline items.</param>
    /// <param name="row">The current row index, incremented for each cue.</param>
    /// <param name="depth">Hierarchy depth (0 = focused root).</param>
}
