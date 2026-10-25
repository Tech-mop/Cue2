// SPDX-FileCopyrightText: 2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System;

namespace Cue2.Domain.Timecode;

/// <summary>
/// Decides whether a cue's timecode trigger fires between two readings of its clock.
/// </summary>
internal static class TimecodeTriggerRule
{
    /// <summary>
    /// Returns whether the trigger fires when the clock moves from <paramref name="previous"/> to <paramref name="current"/>.
    /// </summary>
    /// <remarks>
    /// Fires when the clock lands on the trigger frame, or steps forward across the trigger time
    /// (including a 24 hour wrap). Sitting on the frame does not fire again; rewinding and passing
    /// again does. For an input clock, the first locked reading after no lock (first lock or a
    /// re-lock after a dropout) only sets the starting point, so earlier triggers do not fire in a burst.
    /// </remarks>
    /// <param name="previous">Reading at the last check.</param>
    /// <param name="current">Reading now.</param>
    /// <param name="isInput">Whether the clock is an external input that can lose lock.</param>
    /// <param name="hours">Trigger hours.</param>
    /// <param name="minutes">Trigger minutes.</param>
    /// <param name="seconds">Trigger seconds.</param>
    /// <param name="frames">Trigger frames (clamped to the clock's rate).</param>
    /// <param name="targetElapsed">Trigger time in elapsed seconds at the clock's rate; negative means no trigger.</param>
    /// <returns><see langword="true"/> when the cue should GO.</returns>
    internal static bool ShouldFire(
        TimecodeValue previous,
        TimecodeValue current,
        bool isInput,
        int hours,
        int minutes,
        int seconds,
        int frames,
        double targetElapsed)
    {
        if (targetElapsed < 0)
            return false;
        if (isInput && (!current.IsLocked || !previous.IsLocked))
            return false;

        bool onFrame = MatchesFrame(current, hours, minutes, seconds, frames);
        bool wasOn = MatchesFrame(previous, hours, minutes, seconds, frames);
        if (onFrame && !wasOn)
            return true;
        return TimecodeValue.CrossedElapsed(
            previous.ToElapsedSeconds(),
            current.ToElapsedSeconds(),
            targetElapsed,
            TimecodeValue.SecondsPerDay(current.FrameRate));
    }

    /// <summary>Whether <paramref name="reading"/> shows exactly the trigger frame.</summary>
    /// <param name="reading">Clock reading.</param>
    /// <param name="hours">Trigger hours.</param>
    /// <param name="minutes">Trigger minutes.</param>
    /// <param name="seconds">Trigger seconds.</param>
    /// <param name="frames">Trigger frames (clamped to the reading's rate).</param>
    /// <returns><see langword="true"/> when every field matches.</returns>
    internal static bool MatchesFrame(TimecodeValue reading, int hours, int minutes, int seconds, int frames)
    {
        int fps = Math.Max(1, reading.FrameRate.NominalFramesPerSecond());
        int clamped = Math.Clamp(frames, 0, fps - 1);
        return reading.Hours == hours
               && reading.Minutes == minutes
               && reading.Seconds == seconds
               && reading.Frames == clamped;
    }
}
