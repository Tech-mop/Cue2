// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System.Diagnostics;
using System.Threading;

namespace Cue2.Domain.Playback;

/// <summary>
/// Tracks when cues are being started, so background work that costs main-thread time
/// (autosave snapshots, the inspector preview, memory compaction) can wait for a quiet
/// moment. Begin, end and touch run on the main thread; <see cref="IsBusy"/> can be read
/// from any thread.
/// </summary>
public static class PlaybackActivity
{
    private static int _startsInProgress;
    private static long _lastActivityTicks;

    /// <summary>A cue start (media setup through to pre-wait or content) has begun.</summary>
    public static void BeginStart()
    {
        Volatile.Write(ref _startsInProgress, _startsInProgress + 1);
        Touch();
    }

    /// <summary>Pairs with <see cref="BeginStart"/>. Safe to call once per begin, in a finally.</summary>
    public static void EndStart()
    {
        if (_startsInProgress > 0)
            Volatile.Write(ref _startsInProgress, _startsInProgress - 1);
        Touch();
    }

    /// <summary>Records a GO or a content start as recent activity.</summary>
    public static void Touch() => Volatile.Write(ref _lastActivityTicks, Stopwatch.GetTimestamp());

    /// <summary>
    /// True while any cue start is in progress, or when the last GO or content start was less
    /// than <paramref name="quietSeconds"/> ago.
    /// </summary>
    public static bool IsBusy(double quietSeconds)
    {
        if (Volatile.Read(ref _startsInProgress) > 0)
            return true;
        long last = Volatile.Read(ref _lastActivityTicks);
        if (last == 0)
            return false;
        double since = (Stopwatch.GetTimestamp() - last) / (double)Stopwatch.Frequency;
        return since < quietSeconds;
    }
}
