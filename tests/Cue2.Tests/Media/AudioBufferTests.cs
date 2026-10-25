// SPDX-FileCopyrightText: 2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using NUnit.Framework;
using Cue2.Media.Audio;

namespace Cue2.Tests.Media;

/// <summary>
/// Pan law, output limits, the PCM ring buffer and waveform peak storage.
/// </summary>
[TestFixture]
public class AudioBufferTests
{
    /// <summary>Centre is unity on both sides; full left or right silences the other side.</summary>
    [Test]
    public void StereoPanGains()
    {
        AudioMixMatrix.GetStereoPanGains(0f, out float l, out float r);
        Assert.That(l, Is.EqualTo(1f));
        Assert.That(r, Is.EqualTo(1f).Within(1e-6f));

        AudioMixMatrix.GetStereoPanGains(-1f, out l, out r);
        Assert.That(l, Is.EqualTo(1f));
        Assert.That(r, Is.EqualTo(0f).Within(1e-6f));

        AudioMixMatrix.GetStereoPanGains(1f, out l, out r);
        Assert.That(l, Is.EqualTo(0f).Within(1e-6f));
        Assert.That(r, Is.EqualTo(1f));

        AudioMixMatrix.GetStereoPanGains(0.5f, out l, out r);
        Assert.That(l, Is.EqualTo(MathF.Cos(MathF.PI / 4f)).Within(1e-6f), "equal power at half right");

        AudioMixMatrix.GetStereoPanGains(5f, out l, out _);
        Assert.That(l, Is.EqualTo(0f).Within(1e-6f), "pan is clamped");
    }

    /// <summary>Pan only affects stereo sources.</summary>
    [Test]
    public void InputPanGainOnlyForStereo()
    {
        Assert.That(AudioMixMatrix.GetInputPanGain(1f, 1, 0), Is.EqualTo(1f));
        Assert.That(AudioMixMatrix.GetInputPanGain(1f, 6, 0), Is.EqualTo(1f));
        Assert.That(AudioMixMatrix.GetInputPanGain(1f, 2, 0), Is.EqualTo(0f).Within(1e-6f));
        Assert.That(AudioMixMatrix.GetInputPanGain(1f, 2, 5), Is.EqualTo(1f));
    }

    /// <summary>Component gain is capped at +12 dB and never negative.</summary>
    [Test]
    public void ComponentGainClamp()
    {
        Assert.That(AudioMixMatrix.ClampComponentGainLinear(-1f), Is.EqualTo(0f));
        Assert.That(AudioMixMatrix.ClampComponentGainLinear(0.5f), Is.EqualTo(0.5f));
        Assert.That(AudioMixMatrix.MaxComponentGainLinear, Is.EqualTo(MathF.Pow(10f, 12f / 20f)).Within(1e-5f));
        Assert.That(AudioMixMatrix.ClampComponentGainLinear(100f), Is.EqualTo(AudioMixMatrix.MaxComponentGainLinear));
    }

    /// <summary>The limiter clamps peaks and gates near-silence, keeping sign.</summary>
    [Test]
    public void OutputLimitsClampAndGate()
    {
        float[] buffer = { 0.9f, -0.9f, 0.0001f, -0.0001f, 0.3f };
        AudioMixMatrix.ApplyOutputLimits(buffer, 0.5f, 0.001f);
        Assert.That(buffer, Is.EqualTo(new[] { 0.5f, -0.5f, 0f, 0f, 0.3f }));

        float[] untouched = { 2f, 0.00001f };
        AudioMixMatrix.ApplyOutputLimits(untouched, 0f, 0f);
        Assert.That(untouched, Is.EqualTo(new[] { 2f, 0.00001f }), "zero limits disable both");
    }

    /// <summary>dB to linear: 0 dB is 1, -6 dB is about half, below the floor is silence.</summary>
    [Test]
    public void DbToAbsLinear()
    {
        Assert.That(AudioMixMatrix.DbToAbsLinear(0f), Is.EqualTo(1f).Within(1e-6f));
        Assert.That(AudioMixMatrix.DbToAbsLinear(-6.0206f), Is.EqualTo(0.5f).Within(1e-4f));
        Assert.That(AudioMixMatrix.DbToAbsLinear(-200f), Is.EqualTo(0f));
        Assert.That(AudioMixMatrix.DbToAbsLinear(float.NaN), Is.EqualTo(0f));
        Assert.That(AudioMixMatrix.DbToAbsLinear(float.NegativeInfinity), Is.EqualTo(0f));
    }

    /// <summary>The ring buffer wraps, refuses overrun and reads back in order.</summary>
    [Test]
    public void RingBufferWrapsAndKeepsOrder()
    {
        var ring = new PcmRingBuffer(4);
        Assert.That(ring.Write(new float[] { 1, 2, 3, 4, 5 }), Is.EqualTo(4), "overrun is refused, not overwritten");
        Assert.That(ring.Free, Is.EqualTo(0));

        var dst = new float[3];
        Assert.That(ring.Read(dst), Is.EqualTo(3));
        Assert.That(dst, Is.EqualTo(new float[] { 1, 2, 3 }));

        Assert.That(ring.Write(new float[] { 6, 7, 8 }), Is.EqualTo(3), "wraps around the end");
        var all = new float[10];
        Assert.That(ring.Read(all), Is.EqualTo(4));
        Assert.That(all[..4], Is.EqualTo(new float[] { 4, 6, 7, 8 }));
        Assert.That(ring.Read(all), Is.EqualTo(0), "underrun reads nothing");
    }

    /// <summary>Clear empties the buffer; zero capacity is rejected.</summary>
    [Test]
    public void RingBufferClearAndCapacity()
    {
        var ring = new PcmRingBuffer(8);
        ring.Write(new float[] { 1, 2, 3 });
        ring.Clear();
        Assert.That(ring.Available, Is.EqualTo(0));
        Assert.That(ring.Free, Is.EqualTo(8));
        Assert.That(() => new PcmRingBuffer(0), Throws.InstanceOf<ArgumentOutOfRangeException>());
    }

    /// <summary>Peaks of a known sine reach plus and minus its amplitude.</summary>
    [Test]
    public void WaveformPeaksOfSine()
    {
        var chunks = new List<(float, float)>();
        for (int c = 0; c < 4096; c++)
        {
            float mn = float.MaxValue;
            float mx = float.MinValue;
            for (int s = 0; s < 64; s++)
            {
                float v = 0.8f * MathF.Sin((c * 64 + s) * 2f * MathF.PI / 480f);
                mn = Math.Min(mn, v);
                mx = Math.Max(mx, v);
            }
            chunks.Add((mn, mx));
        }

        var peaks = WaveformPeaks.FromChunks(chunks, 512);
        Assert.That(peaks.BinCount, Is.EqualTo(512));
        for (int i = 0; i < peaks.BinCount; i++)
        {
            Assert.That(peaks.GetMax(i), Is.EqualTo(0.8f).Within(0.01f));
            Assert.That(peaks.GetMin(i), Is.EqualTo(-0.8f).Within(0.01f));
        }
        Assert.That(peaks.ComputePeakScale(), Is.EqualTo(0.8f).Within(0.01f));
    }

    /// <summary>Short files keep one bin per chunk instead of stretching.</summary>
    [Test]
    public void WaveformPeaksKeepShortFiles()
    {
        var peaks = WaveformPeaks.FromChunks(new List<(float, float)> { (-1f, 1f), (-0.5f, 0.5f) }, 2048);
        Assert.That(peaks.BinCount, Is.EqualTo(2));
        Assert.That(() => WaveformPeaks.FromChunks(new List<(float, float)>(), 10), Throws.ArgumentException);
    }

    /// <summary>Serialized peaks read back; truncated or wrong-version data is refused.</summary>
    [Test]
    public void WaveformPeaksBytesRoundTrip()
    {
        var peaks = WaveformPeaks.FromChunks(new List<(float, float)> { (-1f, 1f), (-0.5f, 0.25f), (0f, 0f) }, 8);
        byte[] bytes = peaks.ToBytes();
        var back = WaveformPeaks.FromBytes(bytes);
        Assert.That(back.BinCount, Is.EqualTo(3));
        Assert.That(back.MinMax, Is.EqualTo(peaks.MinMax));

        Assert.That(WaveformPeaks.FromBytes(bytes[..^4]), Is.Null, "truncated");
        byte[] wrongVersion = (byte[])bytes.Clone();
        BitConverter.GetBytes(99).CopyTo(wrongVersion, 4);
        Assert.That(WaveformPeaks.FromBytes(wrongVersion), Is.Null);
        Assert.That(WaveformPeaks.FromBytes(null), Is.Null);
        Assert.That(WaveformPeaks.FromBytes(new byte[3]), Is.Null);
    }

    /// <summary>Legacy payloads of raw min/max floats are still read.</summary>
    [Test]
    public void WaveformPeaksReadsLegacyFloats()
    {
        byte[] legacy = new byte[16];
        BitConverter.GetBytes(-0.5f).CopyTo(legacy, 0);
        BitConverter.GetBytes(0.5f).CopyTo(legacy, 4);
        BitConverter.GetBytes(-0.25f).CopyTo(legacy, 8);
        BitConverter.GetBytes(0.75f).CopyTo(legacy, 12);
        var peaks = WaveformPeaks.FromBytes(legacy);
        Assert.That(peaks.BinCount, Is.EqualTo(2));
        Assert.That(peaks.GetMax(1), Is.EqualTo(0.75f));
    }

    /// <summary>Bin counts below the minimum fall back to the default; large ones are capped.</summary>
    [Test]
    public void ClampBinCount()
    {
        Assert.That(WaveformPeaks.ClampBinCount(10), Is.EqualTo(WaveformPeaks.DefaultBinCount));
        Assert.That(WaveformPeaks.ClampBinCount(1_000_000), Is.EqualTo(WaveformPeaks.MaxBinCount));
        Assert.That(WaveformPeaks.ClampBinCount(4096), Is.EqualTo(4096));
    }
}
