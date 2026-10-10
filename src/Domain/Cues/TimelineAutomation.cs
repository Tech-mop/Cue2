// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

namespace Cue2.Domain.Cues;

/// <summary>
/// Absolute volume, play rate, and pitch at one file time on an audio timeline.
/// </summary>
public readonly struct TimelineAutomation
{
    /// <summary>Relative linear gain at this time (1 = 0 dB vs the component).</summary>
    public float VolumeLinear { get; init; }

    /// <summary>Absolute play-rate multiplier (component rate × relative scale, clamped).</summary>
    public double PlayRate { get; init; }

    /// <summary>Absolute pitch in cents (component cents + relative offset, clamped).</summary>
    public float PitchCents { get; init; }
}
