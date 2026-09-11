// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System;

namespace Cue2.Domain.ShowSettings;

/// <summary>
/// Soft decode/present quality mode for live video outputs.
/// Maps to ring size, lateness drop threshold, and max present-per-tick without exposing raw knobs.
/// </summary>
public enum VideoQualityMode
{
    /// <summary>Larger prefetch ring, stricter lateness — fewer dropped frames when CPU allows.</summary>
    PreferQuality = 0,

    /// <summary>Default balance used by historical hardcodes in ActiveVideoPlayback.</summary>
    Balanced = 1,

    /// <summary>Smaller ring, more aggressive drop — prioritises staying in real time.</summary>
    PreferPerformance = 2
}

/// <summary>
/// Inspector video preview resolution scale (never affects house outputs).
/// </summary>
public enum VideoPreviewQuality
{
    /// <summary>Present preview frames at source resolution.</summary>
    Full = 0,

    /// <summary>Present at half resolution (area ≈ 1/4).</summary>
    Half = 1,

    /// <summary>Present at quarter resolution (area ≈ 1/16).</summary>
    Quarter = 2
}

/// <summary>
/// Output window vsync / frame-pacing preference for VideoOutputDevice windows.
/// </summary>
public enum OutputVSyncMode
{
    /// <summary>Enable vsync on output windows (less tearing, more present latency).</summary>
    PreferVSync = 0,

    /// <summary>Disable vsync (lower latency, possible tearing).</summary>
    Off = 1,

    /// <summary>Prefer mailbox / adaptive-style low-latency present. Only partialy supported</summary>
    LowLatency = 2
}

/// <summary>
/// Resolved present/decode tuning values derived from <see cref="VideoQualityMode"/>.
/// </summary>
public readonly struct VideoPresentTuning
{
    /// <summary>Target number of decoded video frames to keep buffered.</summary>
    public int PrefetchTarget { get; }

    /// <summary>Prefetch again when buffered frames fall below this.</summary>
    public int PrefetchLowWater { get; }

    /// <summary>Drop frames later than this many microseconds vs the master clock.</summary>
    public long MaxLatenessUs { get; }

    /// <summary>Maximum frames to present or drop in a single main-thread tick.</summary>
    public int MaxPresentPerTick { get; }

    /// <summary>Present a frame if it is no more than this early of the master clock (µs).</summary>
    public long PresentEarlyToleranceUs { get; }

    /// <summary>
    /// Creates a tuning set.
    /// </summary>
    public VideoPresentTuning(
        int prefetchTarget,
        int prefetchLowWater,
        long maxLatenessUs,
        int maxPresentPerTick,
        long presentEarlyToleranceUs)
    {
        PrefetchTarget = Math.Max(1, prefetchTarget);
        PrefetchLowWater = Math.Max(0, prefetchLowWater);
        MaxLatenessUs = Math.Max(0, maxLatenessUs);
        MaxPresentPerTick = Math.Max(1, maxPresentPerTick);
        PresentEarlyToleranceUs = Math.Max(0, presentEarlyToleranceUs);
    }

    /// <summary>
    /// Resolves soft quality mode to concrete present knobs.
    /// </summary>
    /// <param name="mode">User-facing quality mode.</param>
    /// <returns>Tuning used by ActiveVideoPlayback.</returns>
    public static VideoPresentTuning ForMode(VideoQualityMode mode)
    {
        return mode switch
        {
            VideoQualityMode.PreferQuality => new VideoPresentTuning(
                prefetchTarget: 8,
                prefetchLowWater: 4,
                maxLatenessUs: 40_000,
                maxPresentPerTick: 6,
                presentEarlyToleranceUs: 8_000),
            VideoQualityMode.PreferPerformance => new VideoPresentTuning(
                prefetchTarget: 3,
                prefetchLowWater: 1,
                maxLatenessUs: 120_000,
                maxPresentPerTick: 2,
                presentEarlyToleranceUs: 12_000),
            _ => new VideoPresentTuning(
                prefetchTarget: 6,
                prefetchLowWater: 3,
                maxLatenessUs: 80_000,
                maxPresentPerTick: 4,
                presentEarlyToleranceUs: 8_000)
        };
    }

    /// <summary>
    /// Scale factor for inspector preview (1.0 = full, 0.5 = half, 0.25 = quarter).
    /// </summary>
    public static float PreviewScale(VideoPreviewQuality quality)
    {
        return quality switch
        {
            VideoPreviewQuality.Half => 0.5f,
            VideoPreviewQuality.Quarter => 0.25f,
            _ => 1f
        };
    }
}
