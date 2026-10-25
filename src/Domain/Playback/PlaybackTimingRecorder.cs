// SPDX-FileCopyrightText: 2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System.Collections.Generic;
using System.Diagnostics;

namespace Cue2.Domain.Playback;

/// <summary>
/// Start timing as numbers, for tests and timing runs. Off by default; while off, recording
/// costs one flag check. The log lines from <see cref="PlaybackStartTrace"/> are unchanged.
/// </summary>
internal static class PlaybackTimingRecorder
{
    /// <summary>Time the GO handler took, on the main thread.</summary>
    internal const string StageGoHandler = "go handler";

    /// <summary>A cue's content phase began (after its pre-wait).</summary>
    internal const string StageContentStarted = "content started";

    /// <summary>The first picture of a video play was uploaded.</summary>
    internal const string StageFirstPicture = "first picture";

    /// <summary>The first audio of a play was queued for output.</summary>
    internal const string StageAudioQueued = "audio queued";

    /// <summary>The output device first took audio from a play.</summary>
    internal const string StageOutputTookAudio = "output took audio";

    private const int MaxSamples = 10000;

    private static readonly object Gate = new();
    private static readonly List<Sample> Samples = new();
    private static volatile bool _enabled;

    /// <summary>One timing point.</summary>
    /// <param name="Label">Cue (and file) the point belongs to; empty for the GO handler.</param>
    /// <param name="Stage">One of the <c>Stage</c> constants.</param>
    /// <param name="MsAfterGo">Milliseconds after GO, or NaN when the GO time is not known.</param>
    /// <param name="MsAfterPlay">Milliseconds after play started, or NaN when not known.</param>
    internal readonly record struct Sample(string Label, string Stage, double MsAfterGo, double MsAfterPlay);

    /// <summary>When true, timing points are kept. Set by test and timing runs only.</summary>
    internal static bool Enabled
    {
        get => _enabled;
        set => _enabled = value;
    }

    /// <summary>
    /// Keeps a timing point measured now. <paramref name="goTicks"/> and <paramref name="playTicks"/>
    /// are <see cref="Stopwatch"/> timestamps, 0 when not known.
    /// </summary>
    internal static void Record(string label, string stage, long goTicks, long playTicks)
    {
        if (!_enabled)
            return;
        long now = Stopwatch.GetTimestamp();
        var sample = new Sample(label ?? string.Empty, stage, Since(goTicks, now), Since(playTicks, now));
        lock (Gate)
        {
            if (Samples.Count >= MaxSamples)
                Samples.RemoveRange(0, MaxSamples / 2);
            Samples.Add(sample);
        }
    }

    /// <summary>Points kept so far, oldest first.</summary>
    internal static Sample[] Snapshot()
    {
        lock (Gate)
            return Samples.ToArray();
    }

    /// <summary>Forgets every point kept so far.</summary>
    internal static void Clear()
    {
        lock (Gate)
            Samples.Clear();
    }

    private static double Since(long ticks, long now) =>
        ticks == 0 ? double.NaN : (now - ticks) * 1000.0 / Stopwatch.Frequency;
}
