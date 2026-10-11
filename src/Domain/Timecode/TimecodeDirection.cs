// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

namespace Cue2.Domain.Timecode;

/// <summary>
/// Whether a registered timecode endpoint is a source Cue2 listens to, or a destination it sends to.
/// </summary>
public enum TimecodeDirection
{
    /// <summary>Receive or generate a clock that cues can follow.</summary>
    Input = 0,

    /// <summary>Send timecode to hardware or the network.</summary>
    Output = 1
}
