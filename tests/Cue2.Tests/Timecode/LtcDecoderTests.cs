// SPDX-FileCopyrightText: 2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using NUnit.Framework;
using Cue2.Domain.Timecode;

namespace Cue2.Tests.Timecode;

/// <summary>
/// The LTC encoder feeding the LTC decoder, cut into blocks the way the capture loop reads them.
/// </summary>
[TestFixture]
public class LtcDecoderTests
{
    private const int SampleRate = 48000;

    private static readonly TimecodeFrameRate[] Rates =
    {
        TimecodeFrameRate.Fps24,
        TimecodeFrameRate.Fps25,
        TimecodeFrameRate.Fps2997Drop,
        TimecodeFrameRate.Fps2997NonDrop,
        TimecodeFrameRate.Fps30,
    };

    private static IEnumerable<TestCaseData> RateAndBlocks()
    {
        foreach (var rate in Rates)
        {
            foreach (int blocks in new[] { 1, 3, 7, 8 })
                yield return new TestCaseData(rate, blocks).SetName($"Rollovers_{rate}_{blocks}BlocksPerFrame");
        }
    }

    /// <summary>
    /// Across minute and hour changes every frame is reported exactly once, in order, with no stale values.
    /// </summary>
    [TestCaseSource(nameof(RateAndBlocks))]
    public void EveryFrameOnceAcrossRollovers(TimecodeFrameRate rate, int blocksPerFrame)
    {
        var signal = new LtcSignal(rate);
        long start = signal.FrameAt(0, 59, 50);
        var sent = new List<TimecodeValue>();
        var got = new List<TimecodeValue>();
        var decoder = new LtcDecoder();

        for (long n = start; n < start + (long)(25 * rate.RealFramesPerSecond()); n++)
        {
            float[] pcm = signal.Encode(n, out var value);
            sent.Add(value);
            int block = pcm.Length / blocksPerFrame;
            for (int b = 0; b < blocksPerFrame; b++)
            {
                int offset = b * block;
                int length = b == blocksPerFrame - 1 ? pcm.Length - offset : block;
                if (decoder.Push(pcm.AsSpan(offset, length), SampleRate, rate, out var decoded))
                    got.Add(decoded);
            }
        }

        AssertFollowsSent(sent, got, maxSkippedAtStart: 2);
    }

    /// <summary>The capture loop drains about every 5 ms; frames still arrive once each, in order.</summary>
    [TestCaseSource(nameof(Rates))]
    public void EveryFrameOnceIn240SampleBlocks(TimecodeFrameRate rate)
    {
        var signal = new LtcSignal(rate);
        long start = signal.FrameAt(9, 59, 58);
        var sent = new List<TimecodeValue>();
        var stream = new List<float>();
        for (long n = start; n < start + (long)(4 * rate.RealFramesPerSecond()); n++)
        {
            stream.AddRange(signal.Encode(n, out var value));
            sent.Add(value);
        }

        var got = PushInBlocks(new LtcDecoder(), stream.ToArray(), 240, rate);
        AssertFollowsSent(sent, got, maxSkippedAtStart: 2);
    }

    /// <summary>The first value reported after lock is the real time, never 00:00:00:00.</summary>
    [TestCaseSource(nameof(Rates))]
    public void FirstReportedFrameIsRealTime(TimecodeFrameRate rate)
    {
        var signal = new LtcSignal(rate);
        long start = signal.FrameAt(1, 23, 45);
        var stream = new List<float>();
        for (long n = start; n < start + 5; n++)
            stream.AddRange(signal.Encode(n, out _));

        var got = PushInBlocks(new LtcDecoder(), stream.ToArray(), 240, rate);
        Assert.That(got, Is.Not.Empty);
        Assert.That(got[0].Hours, Is.EqualTo(1));
        Assert.That(got[0].Minutes, Is.EqualTo(23));
        Assert.That(got[0].Seconds, Is.EqualTo(45));
    }

    /// <summary>A frame is not reported until its own sync word has arrived.</summary>
    [Test]
    public void NoFrameReportedBeforeItsSyncWord()
    {
        var rate = TimecodeFrameRate.Fps25;
        var signal = new LtcSignal(rate);
        var decoder = new LtcDecoder();
        long start = signal.FrameAt(1, 0, 0);
        TimecodeValue latest = default;
        for (long n = start; n < start + 30; n++)
        {
            float[] pcm = signal.Encode(n, out var value);
            int third = pcm.Length / 3;
            for (int b = 0; b < 3; b++)
            {
                int length = b == 2 ? pcm.Length - 2 * third : third;
                if (decoder.Push(pcm.AsSpan(b * third, length), SampleRate, rate, out var decoded))
                    latest = decoded;
                if (b < 2 && latest.IsLocked)
                    Assert.That(latest, Is.Not.EqualTo(value), $"{value} reported while only part sent");
            }
        }
    }

    /// <summary>Background noise and a level change do not break decoding.</summary>
    [TestCaseSource(nameof(Rates))]
    public void SurvivesNoiseAndLevelChanges(TimecodeFrameRate rate)
    {
        var signal = new LtcSignal(rate);
        var random = new Random(1234);
        long start = signal.FrameAt(2, 0, 0);
        var sent = new List<TimecodeValue>();
        var stream = new List<float>();
        int frames = (int)(6 * rate.RealFramesPerSecond());
        for (int i = 0; i < frames; i++)
        {
            float[] pcm = signal.Encode(start + i, out var value);
            float gain = i < frames / 3 ? 1f : i < 2 * frames / 3 ? 0.15f : 2.5f;
            for (int s = 0; s < pcm.Length; s++)
                pcm[s] = pcm[s] * gain + (float)(random.NextDouble() - 0.5) * 0.02f;
            stream.AddRange(pcm);
            sent.Add(value);
        }

        var got = PushInBlocks(new LtcDecoder(), stream.ToArray(), 240, rate);
        AssertFollowsSent(sent, got, maxSkippedAtStart: 2);
    }

    /// <summary>After a dropout the decoder picks up the new time and never reports a stale one.</summary>
    [TestCaseSource(nameof(Rates))]
    public void RecoversAfterDropout(TimecodeFrameRate rate)
    {
        var signal = new LtcSignal(rate);
        long first = signal.FrameAt(3, 0, 0);
        long second = signal.FrameAt(3, 10, 0);
        var before = new List<TimecodeValue>();
        var after = new List<TimecodeValue>();
        var stream = new List<float>();
        for (long n = first; n < first + 20; n++)
        {
            stream.AddRange(signal.Encode(n, out var value));
            before.Add(value);
        }
        int dropAt = stream.Count;
        stream.AddRange(new float[SampleRate / 5]);
        for (long n = second; n < second + 20; n++)
        {
            stream.AddRange(signal.Encode(n, out var value));
            after.Add(value);
        }

        var decoder = new LtcDecoder();
        float[] pcm = stream.ToArray();
        var gotBefore = PushInBlocks(decoder, pcm[..dropAt], 240, rate);
        var gotAfter = PushInBlocks(decoder, pcm[dropAt..], 240, rate);
        AssertFollowsSent(before, gotBefore, maxSkippedAtStart: 2);
        AssertFollowsSent(after, gotAfter, maxSkippedAtStart: 2);
    }

    /// <summary>Reset forgets the old stream; the next stream locks on fresh.</summary>
    [Test]
    public void ResetStartsClean()
    {
        var rate = TimecodeFrameRate.Fps30;
        var signal = new LtcSignal(rate);
        var decoder = new LtcDecoder();
        var stream = new List<float>();
        long start = signal.FrameAt(4, 0, 0);
        for (long n = start; n < start + 10; n++)
            stream.AddRange(signal.Encode(n, out _));
        PushInBlocks(decoder, stream.ToArray(), 240, rate);

        decoder.Reset();
        var sent = new List<TimecodeValue>();
        stream.Clear();
        start = signal.FrameAt(5, 0, 0);
        for (long n = start; n < start + 10; n++)
        {
            stream.AddRange(signal.Encode(n, out var value));
            sent.Add(value);
        }
        AssertFollowsSent(sent, PushInBlocks(decoder, stream.ToArray(), 240, rate), maxSkippedAtStart: 2);
    }

    private static List<TimecodeValue> PushInBlocks(LtcDecoder decoder, float[] pcm, int block, TimecodeFrameRate rate)
    {
        var got = new List<TimecodeValue>();
        for (int offset = 0; offset < pcm.Length; offset += block)
        {
            int length = Math.Min(block, pcm.Length - offset);
            if (decoder.Push(pcm.AsSpan(offset, length), SampleRate, rate, out var decoded))
                got.Add(decoded);
        }
        return got;
    }

    /// <summary>
    /// The decoded list must be the sent list from a small starting offset to the second last frame, one for one.
    /// </summary>
    private static void AssertFollowsSent(List<TimecodeValue> sent, List<TimecodeValue> got, int maxSkippedAtStart)
    {
        Assert.That(got, Is.Not.Empty, "nothing decoded");
        int index = sent.FindIndex(v => SameTime(v, got[0]));
        Assert.That(index, Is.InRange(0, maxSkippedAtStart), $"first decoded {got[0]} is not one of the first sent frames");
        for (int i = 0; i < got.Count; i++)
        {
            Assert.That(index + i, Is.LessThan(sent.Count), "more frames decoded than sent");
            var expected = sent[index + i];
            Assert.That(SameTime(expected, got[i]), Is.True,
                $"decoded #{i} was {got[i]}, expected {expected} (previous {(i > 0 ? got[i - 1].ToString() : "none")})");
            Assert.That(got[i].FrameRate, Is.EqualTo(expected.FrameRate));
        }
        // The last bit of a frame is only known when the next transition arrives, so the very
        // last frame of a finite signal is never reported.
        Assert.That(index + got.Count, Is.EqualTo(sent.Count - 1), "frames missing at the end");
    }

    private static bool SameTime(TimecodeValue a, TimecodeValue b) =>
        a.Hours == b.Hours && a.Minutes == b.Minutes && a.Seconds == b.Seconds && a.Frames == b.Frames;

    /// <summary>Continuous LTC at one rate, frame by frame.</summary>
    private sealed class LtcSignal
    {
        private readonly TimecodeFrameRate _rate;
        private bool _level;

        public LtcSignal(TimecodeFrameRate rate) => _rate = rate;

        public long FrameAt(int hours, int minutes, int seconds) =>
            (long)Math.Round(new TimecodeValue(hours, minutes, seconds, 0, _rate).ToElapsedSeconds() * _rate.RealFramesPerSecond());

        public float[] Encode(long frameNumber, out TimecodeValue value)
        {
            // Half a frame in, so the frame number never sits on a rounding edge.
            value = TimecodeValue.FromElapsedSeconds((frameNumber + 0.5) / _rate.RealFramesPerSecond(), _rate, true);
            var pcm = new float[LtcEncoder.SamplesPerFrame(SampleRate, _rate)];
            int written = LtcEncoder.EncodeFrame(value, SampleRate, pcm, ref _level);
            Assert.That(written, Is.EqualTo(pcm.Length));
            return pcm;
        }
    }
}
