// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

namespace Cue2.Domain.Cues;

/// <summary>
/// How relative volume, play rate, and pitch travel between consecutive audio timeline nodes.
/// Volume interpolates in dB, rate in log scale, pitch in cents.
/// </summary>
public enum TimelineVolumeInterpolation
{
    /// <summary>Straight dB ramp from one node to the next (default for new cues).</summary>
    Linear = 0,

    /// <summary>Hold the destination node's volume for the whole span (step at the previous node).</summary>
    Snap = 1,

    /// <summary>Cubic ease-in-out (horizontal bezier handles) in dB between the two nodes.</summary>
    Bezier = 2
}
