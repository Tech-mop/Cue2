// SPDX-FileCopyrightText: 2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System;
using System.Threading;

namespace Cue2.Services;

/// <summary>
/// Counts video/text presenters so Cue2 can sleep the Godot loop while idle and
/// run at full vsync while a picture is on screen.
/// </summary>
/// <remarks>
/// Godot-free so the count can be unit tested. Apply the result with
/// <see cref="IdleRenderEngine.Sync"/>.
/// </remarks>
public static class IdleRenderPolicy
{
    /// <summary>Default <c>low_processor_mode_sleep_usec</c> (~144 Hz loop, skip-draw when idle).</summary>
    public const int IdleSleepUsec = 6900;

    private static int _presentingCount;

    /// <summary>True when at least one presenter is in the per-frame present loop.</summary>
    public static bool IsPresenting => Volatile.Read(ref _presentingCount) > 0;

    /// <summary>Number of presenters currently counted (never negative).</summary>
    public static int PresentingCount => Math.Max(0, Volatile.Read(ref _presentingCount));

    /// <summary>
    /// Starts or stops counting <paramref name="token"/> as a live presenter.
    /// </summary>
    /// <param name="token">Per-instance flag so double start/stop is a no-op.</param>
    /// <param name="presenting">True when this instance's present loop is on.</param>
    /// <returns>True when the global 0↔1 boundary was crossed (engine policy should sync).</returns>
    public static bool SetPresenting(ref bool token, bool presenting)
    {
        if (token == presenting)
            return false;

        token = presenting;
        if (presenting)
            return Interlocked.Increment(ref _presentingCount) == 1;

        int n = Interlocked.Decrement(ref _presentingCount);
        if (n < 0)
        {
            Interlocked.Exchange(ref _presentingCount, 0);
            n = 0;
        }

        return n == 0;
    }

    /// <summary>Clears the count. Tests only.</summary>
    public static void ResetForTests()
    {
        Interlocked.Exchange(ref _presentingCount, 0);
    }
}
