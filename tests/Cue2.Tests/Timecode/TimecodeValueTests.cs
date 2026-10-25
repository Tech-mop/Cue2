// SPDX-FileCopyrightText: 2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using NUnit.Framework;
using Cue2.Domain.Timecode;

namespace Cue2.Tests.Timecode;

/// <summary>
/// Timecode parsing, formatting, drop-frame counting and the frame to seconds round trip.
/// </summary>
[TestFixture]
public class TimecodeValueTests
{
    private static readonly TimecodeFrameRate[] Rates =
    {
        TimecodeFrameRate.Fps24,
        TimecodeFrameRate.Fps25,
        TimecodeFrameRate.Fps2997Drop,
        TimecodeFrameRate.Fps2997NonDrop,
        TimecodeFrameRate.Fps30,
    };

    /// <summary>Every frame of an hour converts to seconds and back to the same frame.</summary>
    [TestCaseSource(nameof(Rates))]
    public void EveryFrameOfAnHourRoundTrips(TimecodeFrameRate rate)
    {
        int fps = rate.NominalFramesPerSecond();
        int mismatches = 0;
        string first = null;
        for (int m = 0; m < 60; m++)
        {
            for (int s = 0; s < 60; s++)
            {
                for (int f = 0; f < fps; f++)
                {
                    if (rate.IsDropFrame() && s == 0 && f < 2 && m % 10 != 0)
                        continue;
                    var value = new TimecodeValue(10, m, s, f, rate);
                    var back = TimecodeValue.FromElapsedSeconds(value.ToElapsedSeconds(), rate, false);
                    if (!back.Equals(value))
                    {
                        mismatches++;
                        first ??= $"{value} came back as {back}";
                    }
                }
            }
        }
        Assert.That(mismatches, Is.EqualTo(0), first);
    }

    /// <summary>Drop-frame skips frames 0 and 1 at each minute not divisible by ten.</summary>
    [Test]
    public void DropFrameSkipsTheRightLabels()
    {
        var rate = TimecodeFrameRate.Fps2997Drop;
        var last = new TimecodeValue(0, 0, 59, 29, rate);
        var next = TimecodeValue.FromElapsedSeconds(last.ToElapsedSeconds() + 1 / rate.RealFramesPerSecond(), rate, false);
        Assert.That(next.ToDisplayString(), Is.EqualTo("00:01:00;02"));

        var tenth = new TimecodeValue(0, 9, 59, 29, rate);
        next = TimecodeValue.FromElapsedSeconds(tenth.ToElapsedSeconds() + 1 / rate.RealFramesPerSecond(), rate, false);
        Assert.That(next.ToDisplayString(), Is.EqualTo("00:10:00;00"));

        Assert.That(new TimecodeValue(1, 0, 0, 0, rate).ToElapsedSeconds(), Is.EqualTo(3600.0).Within(0.004),
            "drop-frame keeps an hour of labels within a frame of an hour of real time");
    }

    /// <summary>Display uses a semicolon before frames only for drop-frame.</summary>
    [Test]
    public void DisplayFormat()
    {
        Assert.That(new TimecodeValue(1, 2, 3, 4, TimecodeFrameRate.Fps25).ToDisplayString(), Is.EqualTo("01:02:03:04"));
        Assert.That(new TimecodeValue(1, 2, 3, 4, TimecodeFrameRate.Fps2997Drop).ToDisplayString(), Is.EqualTo("01:02:03;04"));
        Assert.That(new TimecodeValue(25, 70, 70, 99, TimecodeFrameRate.Fps24).ToDisplayString(), Is.EqualTo("01:59:59:23"), "fields are clamped and hours wrap");
    }

    /// <summary>Strict parsing reads four fields; a semicolon means drop-frame.</summary>
    [Test]
    public void StrictParse()
    {
        Assert.That(TimecodeValue.TryParse("01:02:03:04", TimecodeFrameRate.Fps25, out var v), Is.True);
        Assert.That(v.ToDisplayString(), Is.EqualTo("01:02:03:04"));
        Assert.That(v.FrameRate, Is.EqualTo(TimecodeFrameRate.Fps25));

        Assert.That(TimecodeValue.TryParse("-00:00:01;02", TimecodeFrameRate.Fps30, out v, out int sign), Is.True);
        Assert.That(sign, Is.EqualTo(-1));
        Assert.That(v.FrameRate, Is.EqualTo(TimecodeFrameRate.Fps2997Drop));

        Assert.That(TimecodeValue.TryParse("1:2:3", TimecodeFrameRate.Fps25, out _), Is.False);
        Assert.That(TimecodeValue.TryParse("aa:bb:cc:dd", TimecodeFrameRate.Fps25, out _), Is.False);
        Assert.That(TimecodeValue.TryParse(" ", TimecodeFrameRate.Fps25, out _), Is.False);
        Assert.That(TimecodeValue.TryParse("-", TimecodeFrameRate.Fps25, out _), Is.False);
    }

    /// <summary>Operator input fills missing fields and rolls overflow up.</summary>
    [TestCase("200", 200.0)]
    [TestCase("12:20", 12.8)]
    [TestCase("3:12:20", 192.8)]
    [TestCase("1:3:12:20", 3792.8)]
    [TestCase("0:0:0:50", 2.0)]
    [TestCase("1.5", 1.5)]
    [TestCase("1::30", 1.0 + 30 / 25.0)]
    public void UserElapsedInput(string text, double expected)
    {
        Assert.That(TimecodeValue.TryParseUserElapsed(text, TimecodeFrameRate.Fps25, out double elapsed, out int sign), Is.True);
        Assert.That(sign, Is.EqualTo(1));
        Assert.That(elapsed, Is.EqualTo(expected).Within(1e-9));
    }

    /// <summary>Junk and too many fields are refused; a minus sign is reported separately.</summary>
    [Test]
    public void UserInputRefusalsAndSign()
    {
        Assert.That(TimecodeValue.TryParseUserElapsed("abc", TimecodeFrameRate.Fps25, out _, out _), Is.False);
        Assert.That(TimecodeValue.TryParseUserElapsed("1:2:3:4:5", TimecodeFrameRate.Fps25, out _, out _), Is.False);
        Assert.That(TimecodeValue.TryParseUserElapsed(".", TimecodeFrameRate.Fps25, out _, out _), Is.False);
        Assert.That(TimecodeValue.TryParseUserElapsed("-5", TimecodeFrameRate.Fps25, out double e, out int sign), Is.True);
        Assert.That(e, Is.EqualTo(5));
        Assert.That(sign, Is.EqualTo(-1));
    }

    /// <summary>Typed timecode lands on the typed frame.</summary>
    [TestCase(TimecodeFrameRate.Fps24)]
    [TestCase(TimecodeFrameRate.Fps25)]
    [TestCase(TimecodeFrameRate.Fps30)]
    [TestCase(TimecodeFrameRate.Fps2997Drop)]
    [TestCase(TimecodeFrameRate.Fps2997NonDrop)]
    public void UserInputLandsOnTypedFrame(TimecodeFrameRate rate)
    {
        string sep = rate.IsDropFrame() ? ";" : ":";
        Assert.That(TimecodeValue.TryParseUserInput($"00:00:34{sep}03", rate, out var v), Is.True);
        Assert.That(v.ToDisplayString(), Is.EqualTo($"00:00:34{sep}03"));
    }

    /// <summary>Elapsed formatting, including negative values and the 99 hour cap.</summary>
    [Test]
    public void FormatElapsed()
    {
        Assert.That(TimecodeValue.FormatElapsed(12.8, TimecodeFrameRate.Fps25), Is.EqualTo("00:00:12:20"));
        Assert.That(TimecodeValue.FormatElapsed(-1, TimecodeFrameRate.Fps25), Is.EqualTo("00:00:00:00"));
        Assert.That(TimecodeValue.FormatElapsed(-1, TimecodeFrameRate.Fps25, allowNegative: true), Is.EqualTo("-00:00:01:00"));
        Assert.That(TimecodeValue.FormatElapsed(500 * 3600.0, TimecodeFrameRate.Fps25), Does.StartWith("99:"));
    }

    /// <summary>Offsets wrap around midnight in both directions and keep the lock flag.</summary>
    [Test]
    public void OffsetWraps()
    {
        var value = new TimecodeValue(23, 59, 59, 0, TimecodeFrameRate.Fps25, true, true);
        var later = value.OffsetBySeconds(2);
        Assert.That(later.ToDisplayString(), Is.EqualTo("00:00:01:00"));
        Assert.That(later.IsLocked, Is.True);
        Assert.That(new TimecodeValue(0, 0, 1, 0, TimecodeFrameRate.Fps25).OffsetBySeconds(-2).ToDisplayString(), Is.EqualTo("23:59:59:00"));
    }

    /// <summary>Elapsed times past 24 hours wrap.</summary>
    [Test]
    public void ElapsedWrapsAtTwentyFourHours()
    {
        Assert.That(TimecodeValue.FromElapsedSeconds(86400 + 3661, TimecodeFrameRate.Fps25, false).ToDisplayString(), Is.EqualTo("01:01:01:00"));
        Assert.That(TimecodeValue.FromElapsedSeconds(-5, TimecodeFrameRate.Fps25, false).ToDisplayString(), Is.EqualTo("00:00:00:00"));
    }

    /// <summary>Every frame of the whole 24 hour day converts to seconds and back to the same frame.</summary>
    [TestCaseSource(nameof(Rates))]
    public void EveryFrameOfTheDayRoundTrips(TimecodeFrameRate rate)
    {
        int fps = rate.NominalFramesPerSecond();
        long counted = 0;
        int mismatches = 0;
        string first = null;
        for (int h = 0; h < 24; h++)
        for (int m = 0; m < 60; m++)
        for (int s = 0; s < 60; s++)
        for (int f = 0; f < fps; f++)
        {
            if (rate.IsDropFrame() && s == 0 && f < 2 && m % 10 != 0)
                continue;
            counted++;
            var value = new TimecodeValue(h, m, s, f, rate);
            var back = TimecodeValue.FromElapsedSeconds(value.ToElapsedSeconds(), rate, false);
            if (!back.Equals(value))
            {
                mismatches++;
                first ??= $"{value} came back as {back}";
            }
        }
        Assert.That(mismatches, Is.EqualTo(0), first);
        Assert.That(counted, Is.EqualTo(TimecodeValue.FramesPerDay(rate)), "every label of the day is counted once");
    }

    /// <summary>Typed timecode lands on the typed frame across the whole day, at every rate.</summary>
    [TestCaseSource(nameof(Rates))]
    public void TypedTimecodeLandsOnTypedFrameAllDay(TimecodeFrameRate rate)
    {
        int fps = rate.NominalFramesPerSecond();
        string sep = rate.IsDropFrame() ? ";" : ":";
        int mismatches = 0;
        string first = null;
        for (int h = 0; h < 24; h++)
        for (int m = 0; m < 60; m++)
        foreach (int s in new[] { 0, 1, 13, 34, 59 })
        foreach (int f in new[] { 2, 3, fps / 2, fps - 1 })
        {
            string typed = $"{h:00}:{m:00}:{s:00}{sep}{f:00}";
            if (!TimecodeValue.TryParseUserInput(typed, rate, out var v) || v.ToDisplayString() != typed)
            {
                mismatches++;
                first ??= $"{typed} became {v.ToDisplayString()}";
            }
        }
        Assert.That(mismatches, Is.EqualTo(0), first);
    }

    /// <summary>Short and plain typed input is read as timecode labels.</summary>
    [TestCase("34", TimecodeFrameRate.Fps2997NonDrop, "00:00:34:00")]
    [TestCase("34.5", TimecodeFrameRate.Fps2997NonDrop, "00:00:34:15")]
    [TestCase("12:20", TimecodeFrameRate.Fps2997NonDrop, "00:00:12:20")]
    [TestCase("1:0:0", TimecodeFrameRate.Fps2997NonDrop, "00:01:00:00")]
    [TestCase("0:0:90:45", TimecodeFrameRate.Fps2997NonDrop, "00:01:31:15")]
    [TestCase("25:00:00:00", TimecodeFrameRate.Fps25, "01:00:00:00")]
    [TestCase("23:59:59:29", TimecodeFrameRate.Fps2997NonDrop, "23:59:59:29")]
    [TestCase("23:59:59;29", TimecodeFrameRate.Fps30, "23:59:59;29")]
    [TestCase("00:01:00;00", TimecodeFrameRate.Fps2997Drop, "00:01:00;02")]
    [TestCase("00:10:00;00", TimecodeFrameRate.Fps2997Drop, "00:10:00;00")]
    public void UserInputReadsLabels(string text, TimecodeFrameRate rate, string expected)
    {
        Assert.That(TimecodeValue.TryParseUserInput(text, rate, out var v, out int sign), Is.True);
        Assert.That(sign, Is.EqualTo(1));
        Assert.That(v.ToDisplayString(), Is.EqualTo(expected));
    }

    /// <summary>Offset and duration fields show what was typed, at every rate.</summary>
    [TestCaseSource(nameof(Rates))]
    public void TypedElapsedShowsWhatWasTyped(TimecodeFrameRate rate)
    {
        string sep = rate.IsDropFrame() ? ";" : ":";
        foreach (string typed in new[] { $"00:00:34{sep}03", $"00:10:00{sep}00", $"01:00:00{sep}05", $"30:00:00{sep}01" })
        {
            Assert.That(TimecodeValue.TryParseUserElapsed(typed, rate, out double elapsed, out _), Is.True);
            Assert.That(TimecodeValue.FormatElapsed(elapsed, rate), Is.EqualTo(typed), typed);
        }
    }

    /// <summary>At 29.97 non-drop the last minutes of the day convert back, and the day ends at 00:00:00:00.</summary>
    [Test]
    public void NonDropKeepsTheEndOfTheDay()
    {
        var rate = TimecodeFrameRate.Fps2997NonDrop;
        foreach (var label in new[] { "23:59:13:00", "23:59:30:15", "23:59:59:29" })
        {
            Assert.That(TimecodeValue.TryParse(label, rate, out var value), Is.True);
            var back = TimecodeValue.FromElapsedSeconds(value.ToElapsedSeconds(), rate, false);
            Assert.That(back.ToDisplayString(), Is.EqualTo(label));
        }

        var last = new TimecodeValue(23, 59, 59, 29, rate);
        var next = TimecodeValue.FromElapsedSeconds(last.ToElapsedSeconds() + 1 / rate.RealFramesPerSecond(), rate, false);
        Assert.That(next.ToDisplayString(), Is.EqualTo("00:00:00:00"));
        Assert.That(TimecodeValue.SecondsPerDay(rate), Is.EqualTo(86486.4).Within(1e-6));
        Assert.That(TimecodeValue.FromElapsedSeconds(TimecodeValue.SecondsPerDay(rate) + 1.001, rate, false).ToDisplayString(), Is.EqualTo("00:00:01:00"));
        Assert.That(last.OffsetBySeconds(1 / rate.RealFramesPerSecond()).ToDisplayString(), Is.EqualTo("00:00:00:00"));
        Assert.That(new TimecodeValue(0, 0, 1, 0, rate).OffsetBySeconds(-2 * 1001 / 1000.0).ToDisplayString(), Is.EqualTo("23:59:59:00"));
    }

    /// <summary>The day length matches the labels at each rate.</summary>
    [Test]
    public void DayLengths()
    {
        Assert.That(TimecodeValue.FramesPerDay(TimecodeFrameRate.Fps24), Is.EqualTo(2073600));
        Assert.That(TimecodeValue.FramesPerDay(TimecodeFrameRate.Fps25), Is.EqualTo(2160000));
        Assert.That(TimecodeValue.FramesPerDay(TimecodeFrameRate.Fps30), Is.EqualTo(2592000));
        Assert.That(TimecodeValue.FramesPerDay(TimecodeFrameRate.Fps2997NonDrop), Is.EqualTo(2592000));
        Assert.That(TimecodeValue.FramesPerDay(TimecodeFrameRate.Fps2997Drop), Is.EqualTo(2589408));
        Assert.That(TimecodeValue.SecondsPerDay(TimecodeFrameRate.Fps25), Is.EqualTo(86400.0));
        Assert.That(TimecodeValue.SecondsPerDay(TimecodeFrameRate.Fps2997Drop), Is.EqualTo(86399.9136).Within(1e-6));
    }
}
