// SPDX-FileCopyrightText: 2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using NUnit.Framework;
using Cue2.Domain.Timecode;

namespace Cue2.Tests.Timecode;

/// <summary>
/// When a cue's timecode trigger fires, for generated clocks and for external inputs.
/// </summary>
[TestFixture]
public class TimecodeTriggerRuleTests
{
    private const TimecodeFrameRate Rate = TimecodeFrameRate.Fps25;

    private static TimecodeValue At(int h, int m, int s, int f, bool locked = true) =>
        new(h, m, s, f, Rate, true, locked);

    private static bool Fires(TimecodeValue previous, TimecodeValue current, bool isInput = false)
    {
        var target = new TimecodeValue(1, 0, 10, 0, Rate);
        return TimecodeTriggerRule.ShouldFire(previous, current, isInput, 1, 0, 10, 0, target.ToElapsedSeconds());
    }

    /// <summary>Landing on the frame or stepping across it fires once.</summary>
    [Test]
    public void FiresOnArrivalAndOnCrossing()
    {
        Assert.That(Fires(At(1, 0, 9, 24), At(1, 0, 10, 0)), Is.True, "lands on the frame");
        Assert.That(Fires(At(1, 0, 9, 20), At(1, 0, 10, 3)), Is.True, "steps across it");
        Assert.That(Fires(At(1, 0, 10, 0), At(1, 0, 10, 0)), Is.False, "sits on it");
        Assert.That(Fires(At(1, 0, 10, 0), At(1, 0, 10, 1)), Is.False, "already past");
        Assert.That(Fires(At(1, 0, 8, 0), At(1, 0, 9, 0)), Is.False, "not reached yet");
    }

    /// <summary>Rewinding does not fire; passing again after a rewind does.</summary>
    [Test]
    public void RewindThenPassAgainFires()
    {
        Assert.That(Fires(At(1, 0, 12, 0), At(1, 0, 5, 0)), Is.False);
        Assert.That(Fires(At(1, 0, 9, 24), At(1, 0, 10, 0)), Is.True);
    }

    /// <summary>A trigger just after midnight fires when the clock wraps from 23:59:59.</summary>
    [Test]
    public void FiresAcrossMidnight()
    {
        var target = new TimecodeValue(0, 0, 0, 5, Rate);
        Assert.That(TimecodeTriggerRule.ShouldFire(At(23, 59, 59, 20), At(0, 0, 0, 10), false, 0, 0, 0, 5, target.ToElapsedSeconds()), Is.True);
    }

    /// <summary>The first lock and each re-lock only set the starting point.</summary>
    [Test]
    public void InputLockOnlySetsStartingPoint()
    {
        var unlockedZero = new TimecodeValue(0, 0, 0, 0, Rate, false, false);
        Assert.That(Fires(unlockedZero, At(1, 0, 30, 0), isInput: true), Is.False, "first lock after the trigger time");
        Assert.That(Fires(At(1, 0, 9, 0, locked: false), At(1, 0, 10, 0), isInput: true), Is.False, "re-lock on the frame");
        Assert.That(Fires(At(1, 0, 9, 24), At(1, 0, 10, 0), isInput: true), Is.True, "locked and running");
        Assert.That(Fires(At(1, 0, 9, 24), At(1, 0, 10, 0, locked: false), isInput: true), Is.False, "lost lock");
    }

    /// <summary>Generated clocks do not lose lock, so the lock flag does not matter for them.</summary>
    [Test]
    public void GeneratedClockIgnoresLockFlag()
    {
        Assert.That(Fires(At(1, 0, 9, 24, locked: false), At(1, 0, 10, 0, locked: false)), Is.True);
    }

    /// <summary>No trigger time means no fire; trigger frames beyond the rate are clamped.</summary>
    [Test]
    public void NoTriggerAndClampedFrames()
    {
        Assert.That(TimecodeTriggerRule.ShouldFire(At(1, 0, 0, 0), At(2, 0, 0, 0), false, 1, 0, 10, 0, -1), Is.False);
        Assert.That(TimecodeTriggerRule.MatchesFrame(At(1, 0, 10, 24), 1, 0, 10, 29), Is.True);
    }

    /// <summary>CrossedElapsed: forward, backward, equal and the 24 hour wrap.</summary>
    [Test]
    public void CrossedElapsed()
    {
        Assert.That(TimecodeValue.CrossedElapsed(9, 11, 10), Is.True);
        Assert.That(TimecodeValue.CrossedElapsed(9, 10, 10), Is.True);
        Assert.That(TimecodeValue.CrossedElapsed(10, 11, 10), Is.False);
        Assert.That(TimecodeValue.CrossedElapsed(11, 9, 10), Is.False);
        Assert.That(TimecodeValue.CrossedElapsed(10, 10, 10), Is.False);
        Assert.That(TimecodeValue.CrossedElapsed(86399, 1, 0.5), Is.True);
        Assert.That(TimecodeValue.CrossedElapsed(86399, 1, 86399.5), Is.True);
        Assert.That(TimecodeValue.CrossedElapsed(50000, 10, 20), Is.False, "a big rewind is not a wrap");
    }

    /// <summary>At 29.97 non-drop a trigger late in the day fires there and nowhere else, and one at midnight fires on the wrap.</summary>
    [Test]
    public void NonDropLateTriggerFiresOnlyAtItsTime()
    {
        var rate = TimecodeFrameRate.Fps2997NonDrop;
        var target = new TimecodeValue(23, 59, 50, 0, rate);
        double targetElapsed = target.ToElapsedSeconds();
        double frame = 1 / rate.RealFramesPerSecond();

        int fired = 0;
        string firedAt = null;
        var previous = new TimecodeValue(0, 0, 0, 0, rate, true, true);
        for (long n = 1; n < TimecodeValue.FramesPerDay(rate) + 30; n++)
        {
            var current = TimecodeValue.FromElapsedSeconds(n * frame, rate, true);
            if (TimecodeTriggerRule.ShouldFire(previous, current, false, 23, 59, 50, 0, targetElapsed))
            {
                fired++;
                firedAt ??= current.ToDisplayString();
            }
            previous = current;
        }
        Assert.That(fired, Is.EqualTo(1));
        Assert.That(firedAt, Is.EqualTo("23:59:50:00"));

        var midnight = new TimecodeValue(0, 0, 0, 0, rate);
        Assert.That(TimecodeTriggerRule.ShouldFire(
            new TimecodeValue(23, 59, 59, 28, rate), new TimecodeValue(0, 0, 0, 1, rate),
            false, 0, 0, 0, 0, midnight.ToElapsedSeconds()), Is.True);
        Assert.That(TimecodeValue.CrossedElapsed(86486, 0.5, 86486.2, TimecodeValue.SecondsPerDay(rate)), Is.True);
    }
}
