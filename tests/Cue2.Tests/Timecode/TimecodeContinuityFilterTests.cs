// SPDX-FileCopyrightText: 2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using NUnit.Framework;
using Cue2.Domain.Timecode;

namespace Cue2.Tests.Timecode;

/// <summary>
/// One stray input reading never reaches the triggers; real relocates are accepted a frame late.
/// </summary>
[TestFixture]
public class TimecodeContinuityFilterTests
{
    private const TimecodeFrameRate Rate = TimecodeFrameRate.Fps25;

    private static TimecodeValue At(int h, int m, int s, int f) => new(h, m, s, f, Rate, true, true);

    /// <summary>Running forward is passed straight through, including a skipped frame.</summary>
    [Test]
    public void ForwardRunPasses()
    {
        var filter = new TimecodeContinuityFilter();
        Assert.That(filter.Accept(At(1, 0, 0, 0)), Is.True, "first reading");
        Assert.That(filter.Accept(At(1, 0, 0, 1)), Is.True);
        Assert.That(filter.Accept(At(1, 0, 0, 1)), Is.True, "repeat");
        Assert.That(filter.Accept(At(1, 0, 0, 3)), Is.True, "one frame missed");
    }

    /// <summary>A single backward value between good ones is dropped.</summary>
    [Test]
    public void StrayBackwardValueIsDropped()
    {
        var filter = new TimecodeContinuityFilter();
        filter.Accept(At(1, 0, 59, 24));
        Assert.That(filter.Accept(At(1, 0, 50, 0)), Is.False);
        Assert.That(filter.Accept(At(1, 1, 0, 0)), Is.True);
    }

    /// <summary>A real jump is held for one reading, then accepted once the next follows on.</summary>
    [Test]
    public void RelocateAcceptedWhenConfirmed()
    {
        var filter = new TimecodeContinuityFilter();
        filter.Accept(At(1, 0, 0, 0));
        Assert.That(filter.Accept(At(2, 0, 0, 0)), Is.False);
        Assert.That(filter.Accept(At(2, 0, 0, 1)), Is.True);
        Assert.That(filter.Accept(At(2, 0, 0, 2)), Is.True);

        Assert.That(filter.Accept(At(1, 30, 0, 0)), Is.False, "rewind held");
        Assert.That(filter.Accept(At(1, 30, 0, 1)), Is.True, "rewind confirmed");
    }

    /// <summary>Midnight is a normal step forward.</summary>
    [Test]
    public void MidnightWrapPasses()
    {
        var filter = new TimecodeContinuityFilter();
        filter.Accept(At(23, 59, 59, 24));
        Assert.That(filter.Accept(At(0, 0, 0, 0)), Is.True);
    }

    /// <summary>A change of frame rate, or Reset, starts again.</summary>
    [Test]
    public void RateChangeAndResetStartAgain()
    {
        var filter = new TimecodeContinuityFilter();
        filter.Accept(At(1, 0, 0, 0));
        Assert.That(filter.Accept(new TimecodeValue(5, 0, 0, 0, TimecodeFrameRate.Fps30, true, true)), Is.True);
        filter.Reset();
        Assert.That(filter.Accept(At(9, 0, 0, 0)), Is.True);
    }

    /// <summary>Midnight is a normal step forward at every rate, including 29.97.</summary>
    [TestCase(TimecodeFrameRate.Fps24, "23:59:59:23")]
    [TestCase(TimecodeFrameRate.Fps2997NonDrop, "23:59:59:29")]
    [TestCase(TimecodeFrameRate.Fps2997Drop, "23:59:59;29")]
    [TestCase(TimecodeFrameRate.Fps30, "23:59:59:29")]
    public void MidnightWrapPassesAtEveryRate(TimecodeFrameRate rate, string last)
    {
        var filter = new TimecodeContinuityFilter();
        Assert.That(TimecodeValue.TryParse(last, rate, out var lastValue), Is.True);
        filter.Accept(lastValue);
        Assert.That(filter.Accept(new TimecodeValue(0, 0, 0, 0, rate, true, true)), Is.True);
    }
}
