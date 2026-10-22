// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System;
using System.Diagnostics;
using Godot;

namespace Cue2.Domain.Playback;

/// <summary>
/// One-shot countdown against a <see cref="Stopwatch"/> deadline, for cue pre-waits and
/// continue/follow post-waits. Unlike a Godot <see cref="Timer"/>, it does not add up frame
/// deltas: the deadline is a fixed point in time, and <see cref="StartAt"/> can count from an
/// earlier moment (the time the wait should have started), so a late frame does not push the
/// next wait later. It is still checked once per frame (idle process). Main thread only.
/// </summary>
public partial class MonotonicTimer : Node
{
    private long _deadlineTicks;
    private long _pausedRemainingTicks;
    private bool _running;
    private bool _paused;

    /// <summary>Raised once, on the first frame at or after the deadline.</summary>
    public event Action Timeout;

    /// <summary>Countdown length in seconds, used by <see cref="Start"/> and <see cref="StartAt"/>.</summary>
    public double WaitTime { get; set; }

    /// <summary>True while paused (the remaining time is frozen).</summary>
    public bool Paused => _paused;

    /// <summary>Stopwatch ticks at which the countdown ends (moves on resume after a pause).</summary>
    public long DeadlineTicks => _deadlineTicks;

    /// <summary>Seconds left; 0 when stopped or done.</summary>
    public double TimeLeft
    {
        get
        {
            if (!_running)
                return 0.0;
            long left = _paused ? _pausedRemainingTicks : _deadlineTicks - Stopwatch.GetTimestamp();
            return Math.Max(0.0, left / (double)Stopwatch.Frequency);
        }
    }

    /// <summary>Starts (or restarts) the countdown from now.</summary>
    public void Start() => StartAt(Stopwatch.GetTimestamp());

    /// <summary>
    /// Starts (or restarts) the countdown as if it began at <paramref name="startTicks"/>.
    /// A start in the past shortens what is left (it may end on the next frame).
    /// </summary>
    /// <param name="startTicks">Stopwatch ticks the countdown counts from.</param>
    public void StartAt(long startTicks)
    {
        _deadlineTicks = startTicks + (long)(Math.Max(0.0, WaitTime) * Stopwatch.Frequency);
        _running = true;
        if (_paused)
            _pausedRemainingTicks = Math.Max(0L, _deadlineTicks - Stopwatch.GetTimestamp());
    }

    /// <summary>Stops the countdown without raising <see cref="Timeout"/>.</summary>
    public void Stop()
    {
        _running = false;
    }

    /// <summary>Freezes or resumes the remaining time.</summary>
    /// <param name="paused">True to pause.</param>
    public void SetPaused(bool paused)
    {
        if (paused == _paused)
            return;
        _paused = paused;
        if (!_running)
            return;
        long now = Stopwatch.GetTimestamp();
        if (paused)
            _pausedRemainingTicks = Math.Max(0L, _deadlineTicks - now);
        else
            _deadlineTicks = now + _pausedRemainingTicks;
    }

    /// <inheritdoc />
    public override void _Process(double delta)
    {
        if (!_running || _paused)
            return;
        if (Stopwatch.GetTimestamp() < _deadlineTicks)
            return;
        _running = false;
        Timeout?.Invoke();
    }
}
