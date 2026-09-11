// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System;

namespace Cue2.Domain.ShowSettings;

/// <summary>
/// Soft SDL fill / prefetch latency mode for live audio (standalone cues and video embedded audio).
/// Maps to target buffer, low-water refill threshold, and seek/GO prefetch without exposing raw ms knobs.
/// </summary>
public enum AudioLatencyMode
{
    /// <summary>Smaller buffers — lower present latency; more underrun risk under load.</summary>
    PreferLowLatency = 0,

    /// <summary>Default balance matching historical ActiveAudioPlayback hardcodes.</summary>
    Balanced = 1,

    /// <summary>Larger buffers and longer prefetch — prioritises glitch-free multi-cue playback.</summary>
    PreferStability = 2
}

/// <summary>
/// Resolved audio fill/prefetch/declick values derived from <see cref="AudioLatencyMode"/>
/// and the show-scoped declick duration.
/// </summary>
public readonly struct AudioPresentTuning
{
    /// <summary>Target SDL stream queue depth in milliseconds.</summary>
    public int TargetBufferMs { get; }

    /// <summary>Refill when queued audio falls below this many milliseconds.</summary>
    public int LowWaterMs { get; }

    /// <summary>Decoder prefetch after open/seek (milliseconds of PCM).</summary>
    public int PrefetchMs { get; }

    /// <summary>Raised-cosine de-click ramp after start/seek (milliseconds). 0 disables.</summary>
    public int DeclickRampMs { get; }

    /// <summary>
    /// Creates a tuning set.
    /// </summary>
    public AudioPresentTuning(int targetBufferMs, int lowWaterMs, int prefetchMs, int declickRampMs)
    {
        TargetBufferMs = Math.Max(10, targetBufferMs);
        LowWaterMs = Math.Clamp(lowWaterMs, 5, TargetBufferMs);
        PrefetchMs = Math.Max(50, prefetchMs);
        DeclickRampMs = Math.Clamp(declickRampMs, 0, 100);
    }

    /// <summary>
    /// Streaming decoder ring capacity (ms) large enough for <see cref="PrefetchMs"/> and
    /// SDL fill targets so prefetch does not overflow a 400ms ring under PreferStability.
    /// </summary>
    /// <remarks>
    /// Formula: <c>max(400, PrefetchMs * 2, TargetBufferMs * 3)</c>, capped at 10s.
    /// PreferStability (prefetch 1400ms) → 2800ms ring.
    /// </remarks>
    public int RecommendedRingMs
    {
        get
        {
            int ms = Math.Max(400, Math.Max(PrefetchMs * 2, TargetBufferMs * 3));
            return Math.Clamp(ms, 400, 10_000);
        }
    }

    /// <summary>
    /// Resolves latency mode + declick duration to concrete fill knobs.
    /// </summary>
    /// <param name="mode">User-facing latency mode.</param>
    /// <param name="declickMs">Show-scoped de-click ramp in milliseconds.</param>
    /// <returns>Tuning used by ActiveAudioPlayback and ActiveVideoPlayback audio path.</returns>
    public static AudioPresentTuning ForMode(AudioLatencyMode mode, int declickMs)
    {
        int declick = Math.Clamp(declickMs, 0, 100);
        return mode switch
        {
            AudioLatencyMode.PreferLowLatency => new AudioPresentTuning(
                targetBufferMs: 50,
                lowWaterMs: 25,
                prefetchMs: 400,
                declickRampMs: declick),
            AudioLatencyMode.PreferStability => new AudioPresentTuning(
                targetBufferMs: 220,
                lowWaterMs: 110,
                prefetchMs: 1400,
                declickRampMs: declick),
            _ => new AudioPresentTuning(
                targetBufferMs: 100,
                lowWaterMs: 50,
                prefetchMs: 800,
                declickRampMs: declick)
        };
    }
}
