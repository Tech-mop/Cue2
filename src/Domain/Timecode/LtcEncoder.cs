// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System;

namespace Cue2.Domain.Timecode;

/// <summary>
/// Encodes one SMPTE Linear Timecode frame as bipolar biphase-mark PCM.
/// </summary>
/// <remarks>
/// 80-bit LTC (SMPTE 12M) with biphase mark: a transition at every bit boundary,
/// plus a mid-bit transition for 1. Amplitude is linear (−1…1).
/// </remarks>
public static class LtcEncoder
{
    /// <summary>Peak amplitude of the encoded square wave (about −12 dBFS).</summary>
    public const float Amplitude = 0.25f;

    /// <summary>
    /// Number of PCM frames that make one LTC frame at <paramref name="sampleRate"/>.
    /// </summary>
    /// <param name="sampleRate">Output sample rate in Hz.</param>
    /// <param name="frameRate">Timecode counting rate.</param>
    /// <returns>At least 80 samples (one per bit).</returns>
    public static int SamplesPerFrame(int sampleRate, TimecodeFrameRate frameRate)
    {
        double fps = frameRate.RealFramesPerSecond();
        if (sampleRate <= 0 || fps <= 0)
            return 80;
        int samples = (int)Math.Round(sampleRate / fps);
        return Math.Max(80, samples);
    }

    /// <summary>
    /// Encodes <paramref name="value"/> into a mono buffer of
    /// <see cref="SamplesPerFrame"/> samples.
    /// </summary>
    /// <param name="value">Timecode to pack.</param>
    /// <param name="sampleRate">PCM sample rate.</param>
    /// <param name="destination">Mono destination (length ≥ samples per frame).</param>
    /// <param name="levelHigh">Current biphase polarity; updated for the next frame.</param>
    /// <returns>Samples written.</returns>
    public static int EncodeFrame(
        TimecodeValue value,
        int sampleRate,
        Span<float> destination,
        ref bool levelHigh)
    {
        int totalSamples = SamplesPerFrame(sampleRate, value.FrameRate);
        if (destination.Length < totalSamples)
            totalSamples = destination.Length;
        if (totalSamples < 80)
            return 0;

        Span<byte> bits = stackalloc byte[80];
        PackBits(value, bits);

        int written = 0;
        int remaining = totalSamples;
        for (int b = 0; b < 80; b++)
        {
            int bitsLeft = 80 - b;
            int bitSamples = remaining / bitsLeft;
            int half = Math.Max(1, bitSamples / 2);
            int second = bitSamples - half;

            levelHigh = !levelHigh;
            float firstLevel = levelHigh ? Amplitude : -Amplitude;
            for (int i = 0; i < half && written < destination.Length; i++)
                destination[written++] = firstLevel;

            if (bits[b] != 0)
                levelHigh = !levelHigh;
            float secondLevel = levelHigh ? Amplitude : -Amplitude;
            for (int i = 0; i < second && written < destination.Length; i++)
                destination[written++] = secondLevel;

            remaining -= bitSamples;
        }

        return written;
    }

    /// <summary>
    /// Unpacks an 80-bit LTC frame into a timecode reading.
    /// </summary>
    /// <param name="bits">80 bits, 0 or 1.</param>
    /// <param name="preferredRate">Rate to use when the frame does not distinguish 30 vs 29.97.</param>
    /// <param name="value">Decoded timecode when the sync word is valid.</param>
    /// <returns><see langword="true"/> when bits 64–79 are the LTC sync word.</returns>
    public static bool TryUnpack(ReadOnlySpan<byte> bits, TimecodeFrameRate preferredRate, out TimecodeValue value)
    {
        value = default;
        if (bits.Length < 80)
            return false;
        if (!HasSyncWord(bits))
            return false;

        int frames = Nibble(bits, 0) + 10 * (bits[8] + 2 * bits[9]);
        int seconds = Nibble(bits, 16) + 10 * (bits[24] + 2 * bits[25] + 4 * bits[26]);
        int minutes = Nibble(bits, 32) + 10 * (bits[40] + 2 * bits[41] + 4 * bits[42]);
        int hours = Nibble(bits, 48) + 10 * (bits[56] + 2 * bits[57]);
        bool drop = bits[10] != 0;
        var rate = drop ? TimecodeFrameRate.Fps2997Drop : preferredRate;
        if (drop && preferredRate == TimecodeFrameRate.Fps2997Drop)
            rate = TimecodeFrameRate.Fps2997Drop;
        else if (!drop && preferredRate.IsDropFrame())
            rate = TimecodeFrameRate.Fps30;

        value = new TimecodeValue(hours, minutes, seconds, frames, rate, isRunning: true, isLocked: true);
        return true;
    }

    /// <summary>
    /// True when bits 64–79 equal the SMPTE LTC sync word 0011 1111 1111 1101.
    /// </summary>
    public static bool HasSyncWord(ReadOnlySpan<byte> bits)
    {
        if (bits.Length < 80)
            return false;
        return bits[64] == 0 && bits[65] == 0 && bits[66] == 1 && bits[67] == 1
               && bits[68] == 1 && bits[69] == 1 && bits[70] == 1 && bits[71] == 1
               && bits[72] == 1 && bits[73] == 1 && bits[74] == 1 && bits[75] == 1
               && bits[76] == 1 && bits[77] == 1 && bits[78] == 0 && bits[79] == 1;
    }

    private static int Nibble(ReadOnlySpan<byte> bits, int offset) =>
        bits[offset] | (bits[offset + 1] << 1) | (bits[offset + 2] << 2) | (bits[offset + 3] << 3);

    /// <summary>
    /// Packs SMPTE 12M LTC bits (0–79) for <paramref name="value"/>.
    /// </summary>
    private static void PackBits(TimecodeValue value, Span<byte> bits)
    {
        bits.Clear();

        int frames = value.Frames;
        int seconds = value.Seconds;
        int minutes = value.Minutes;
        int hours = value.Hours;

        PutNibble(bits, 0, frames % 10);
        bits[8] = (byte)((frames / 10) & 1);
        bits[9] = (byte)(((frames / 10) >> 1) & 1);
        bits[10] = value.FrameRate.IsDropFrame() ? (byte)1 : (byte)0;

        PutNibble(bits, 16, seconds % 10);
        int st = seconds / 10;
        bits[24] = (byte)(st & 1);
        bits[25] = (byte)((st >> 1) & 1);
        bits[26] = (byte)((st >> 2) & 1);

        PutNibble(bits, 32, minutes % 10);
        int mt = minutes / 10;
        bits[40] = (byte)(mt & 1);
        bits[41] = (byte)((mt >> 1) & 1);
        bits[42] = (byte)((mt >> 2) & 1);

        PutNibble(bits, 48, hours % 10);
        int ht = hours / 10;
        bits[56] = (byte)(ht & 1);
        bits[57] = (byte)((ht >> 1) & 1);

        // Sync word 0011 1111 1111 1101 (bits 64–79).
        bits[64] = 0;
        bits[65] = 0;
        bits[66] = 1;
        bits[67] = 1;
        for (int i = 68; i <= 77; i++)
            bits[i] = 1;
        bits[78] = 0;
        bits[79] = 1;

        ApplyPolarityCorrection(bits, value.FrameRate);
    }

    private static void PutNibble(Span<byte> bits, int offset, int value)
    {
        bits[offset] = (byte)(value & 1);
        bits[offset + 1] = (byte)((value >> 1) & 1);
        bits[offset + 2] = (byte)((value >> 2) & 1);
        bits[offset + 3] = (byte)((value >> 3) & 1);
    }

    /// <summary>
    /// Polarity correction so consecutive frames keep biphase polarity:
    /// 25 fps uses bit 27, 24/30(/29.97) use bit 43.
    /// </summary>
    private static void ApplyPolarityCorrection(Span<byte> bits, TimecodeFrameRate frameRate)
    {
        int correctionBit = frameRate == TimecodeFrameRate.Fps25 ? 27 : 43;
        bits[correctionBit] = 0;
        int zeros = 0;
        for (int i = 0; i < 80; i++)
        {
            if (bits[i] == 0)
                zeros++;
        }

        // Even count of 0-bits keeps the next frame starting polarity consistent.
        if ((zeros & 1) != 0)
            bits[correctionBit] = 1;
    }
}
