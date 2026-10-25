// SPDX-FileCopyrightText: 2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System;

namespace Cue2.Domain.Timecode;

/// <summary>
/// Guards a decoded timecode input against single bad readings.
/// </summary>
/// <remarks>
/// A reading up to two frames ahead of the last accepted one is passed straight through.
/// Any other reading (a step backward or a jump) is held until the next reading confirms it
/// by following on from it, so one stray value never reaches the cue triggers, while a real
/// relocate is accepted one frame late. Not thread-safe: use one instance per input, from one thread.
/// </remarks>
internal sealed class TimecodeContinuityFilter
{
    private const int MaxForwardFrames = 2;

    private bool _hasAccepted;
    private long _acceptedFrame;
    private bool _hasCandidate;
    private long _candidateFrame;
    private TimecodeFrameRate _rate;

    /// <summary>
    /// Returns whether <paramref name="reading"/> should be used as the input's new value.
    /// </summary>
    /// <param name="reading">Newly decoded value.</param>
    /// <returns><see langword="true"/> to apply the reading; <see langword="false"/> to keep the previous value.</returns>
    internal bool Accept(TimecodeValue reading)
    {
        if (_hasAccepted && reading.FrameRate != _rate)
            Reset();

        long frame = FrameNumber(reading);
        long perDay = FramesPerDay(reading.FrameRate);

        if (!_hasAccepted
            || FollowsOn(_acceptedFrame, frame, perDay)
            || (_hasCandidate && FollowsOn(_candidateFrame, frame, perDay)))
        {
            _hasAccepted = true;
            _acceptedFrame = frame;
            _hasCandidate = false;
            _rate = reading.FrameRate;
            return true;
        }

        _hasCandidate = true;
        _candidateFrame = frame;
        return false;
    }

    /// <summary>Forgets the running position, so the next reading is accepted as is.</summary>
    internal void Reset()
    {
        _hasAccepted = false;
        _hasCandidate = false;
    }

    private static bool FollowsOn(long previous, long next, long perDay)
    {
        long step = ((next - previous) % perDay + perDay) % perDay;
        return step <= MaxForwardFrames;
    }

    private static long FrameNumber(TimecodeValue value) =>
        (long)Math.Round(value.ToElapsedSeconds() * value.FrameRate.RealFramesPerSecond());

    private static long FramesPerDay(TimecodeFrameRate rate) => TimecodeValue.FramesPerDay(rate);
}
