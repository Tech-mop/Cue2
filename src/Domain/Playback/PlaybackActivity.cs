// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System.Diagnostics;

namespace Cue2.Domain.Playback;

/// <summary>
/// Tracks when cues are being started, so background work that costs main-thread time
/// (autosave snapshots) can wait for a quiet moment. Main thread only.
/// </summary>
public static class PlaybackActivity
{
    private static int _startsInProgress;
    private static long _lastActivityTicks;

    /// <summary>A cue start (media setup through to pre-wait or content) has begun.</summary>
    public static void BeginStart()
    {
        _startsInProgress++;
        Touch();
    }

    /// <summary>Pairs with <see cref="BeginStart"/>. Safe to call once per begin, in a finally.</summary>
    public static void EndStart()
    {
        if (_startsInProgress > 0)
            _startsInProgress--;
        Touch();
    }

    /// <summary>Records a GO or a content start as recent activity.</summary>
    public static void Touch() => _lastActivityTicks = Stopwatch.GetTimestamp();

    /// <summary>
    /// True while any cue start is in progress, or when the last GO or content start was less
    /// than <paramref name="quietSeconds"/> ago.
    /// </summary>
    public static bool IsBusy(double quietSeconds)
    {
        if (_startsInProgress > 0)
            return true;
        if (_lastActivityTicks == 0)
            return false;
        double since = (Stopwatch.GetTimestamp() - _lastActivityTicks) / (double)Stopwatch.Frequency;
        return since < quietSeconds;
    }
}
