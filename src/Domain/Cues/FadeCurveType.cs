// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

namespace Cue2.Domain.Cues;

/// <summary>
/// Amplitude shape for a fade over normalized time 0–1.
/// </summary>
public enum FadeCurveType
{
    /// <summary>Constant slope from silence to full level.</summary>
    Linear = 0,

    /// <summary>Slow at both ends, faster through the middle (smoothstep).</summary>
    SCurve = 1,

    /// <summary>Stays quiet, then rises quickly toward the end.</summary>
    Exponential = 2,

    /// <summary>Rises quickly, then eases into full level.</summary>
    Logarithmic = 3
}
