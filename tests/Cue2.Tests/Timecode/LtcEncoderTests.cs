// SPDX-FileCopyrightText: 2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System;
using NUnit.Framework;
using Cue2.Domain.Timecode;

namespace Cue2.Tests.Timecode;

/// <summary>
/// LTC frame packing: length, sync word, drop-frame flag and steady biphase polarity.
/// </summary>
[TestFixture]
public class LtcEncoderTests
{
    /// <summary>One LTC frame lasts one video frame of samples.</summary>
    [TestCase(TimecodeFrameRate.Fps25, 48000, 1920)]
    [TestCase(TimecodeFrameRate.Fps24, 48000, 2000)]
    [TestCase(TimecodeFrameRate.Fps30, 48000, 1600)]
    [TestCase(TimecodeFrameRate.Fps2997Drop, 48000, 1602)]
    [TestCase(TimecodeFrameRate.Fps25, 0, 80)]
    [TestCase(TimecodeFrameRate.Fps25, 1000, 80)]
    public void SamplesPerFrame(TimecodeFrameRate rate, int sampleRate, int expected)
    {
        Assert.That(LtcEncoder.SamplesPerFrame(sampleRate, rate), Is.EqualTo(expected));
    }

    /// <summary>Encoded frames end on the sync word and unpack to the same time and drop flag.</summary>
    [TestCase(TimecodeFrameRate.Fps25, 23, 59, 59, 24)]
    [TestCase(TimecodeFrameRate.Fps30, 12, 34, 56, 29)]
    [TestCase(TimecodeFrameRate.Fps2997Drop, 1, 1, 0, 2)]
    [TestCase(TimecodeFrameRate.Fps24, 0, 0, 0, 0)]
    public void EncodeThenUnpack(TimecodeFrameRate rate, int h, int m, int s, int f)
    {
        var value = new TimecodeValue(h, m, s, f, rate, true);
        byte[] bits = BitsOf(value);
        Assert.That(LtcEncoder.HasSyncWord(bits), Is.True);
        Assert.That(bits[10], Is.EqualTo(rate.IsDropFrame() ? 1 : 0), "drop-frame flag");
        Assert.That(LtcEncoder.TryUnpack(bits, rate, out var back), Is.True);
        Assert.That(back.ToDisplayString(), Is.EqualTo(value.ToDisplayString()));
        Assert.That(back.FrameRate, Is.EqualTo(rate));
    }

    /// <summary>Without the drop flag a 29.97 drop-frame preference reads as 30 fps.</summary>
    [Test]
    public void UnpackWithoutDropFlagMeansThirty()
    {
        byte[] bits = BitsOf(new TimecodeValue(1, 0, 0, 0, TimecodeFrameRate.Fps30, true));
        Assert.That(LtcEncoder.TryUnpack(bits, TimecodeFrameRate.Fps2997Drop, out var back), Is.True);
        Assert.That(back.FrameRate, Is.EqualTo(TimecodeFrameRate.Fps30));
    }

    /// <summary>A damaged sync word is refused.</summary>
    [Test]
    public void BrokenSyncWordIsRefused()
    {
        byte[] bits = BitsOf(new TimecodeValue(1, 2, 3, 4, TimecodeFrameRate.Fps25, true));
        bits[70] = 0;
        Assert.That(LtcEncoder.HasSyncWord(bits), Is.False);
        Assert.That(LtcEncoder.TryUnpack(bits, TimecodeFrameRate.Fps25, out _), Is.False);
        Assert.That(LtcEncoder.TryUnpack(new byte[40], TimecodeFrameRate.Fps25, out _), Is.False);
    }

    /// <summary>Every frame starts on the same polarity, so frames join without a glitch.</summary>
    [TestCase(TimecodeFrameRate.Fps25)]
    [TestCase(TimecodeFrameRate.Fps30)]
    [TestCase(TimecodeFrameRate.Fps2997Drop)]
    public void PolarityIsSteadyFrameToFrame(TimecodeFrameRate rate)
    {
        bool level = false;
        var pcm = new float[LtcEncoder.SamplesPerFrame(48000, rate)];
        for (int i = 0; i < 200; i++)
        {
            var value = TimecodeValue.FromElapsedSeconds(3600 + (i + 0.5) / rate.RealFramesPerSecond(), rate, true);
            LtcEncoder.EncodeFrame(value, 48000, pcm, ref level);
            Assert.That(pcm[0], Is.GreaterThan(0f), $"frame {value} starts low");
            Assert.That(Math.Abs(pcm[^1]), Is.EqualTo(LtcEncoder.Amplitude));
        }
    }

    /// <summary>Recovers the 80 bits of one encoded frame by reading its transitions.</summary>
    private static byte[] BitsOf(TimecodeValue value)
    {
        const int sampleRate = 48000;
        bool level = false;
        var pcm = new float[LtcEncoder.SamplesPerFrame(sampleRate, value.FrameRate)];
        LtcEncoder.EncodeFrame(value, sampleRate, pcm, ref level);

        var bits = new byte[80];
        int remaining = pcm.Length;
        int position = 0;
        for (int b = 0; b < 80; b++)
        {
            int bitSamples = remaining / (80 - b);
            int half = Math.Max(1, bitSamples / 2);
            bool first = pcm[position] > 0;
            bool second = pcm[position + bitSamples - 1] > 0;
            bits[b] = first != second ? (byte)1 : (byte)0;
            position += bitSamples;
            remaining -= bitSamples;
        }
        return bits;
    }
}
