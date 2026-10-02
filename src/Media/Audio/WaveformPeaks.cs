// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Cue2.Media.Audio;

/// <summary>
/// Fixed-resolution peak envelope for waveform display (Audacity-style summary).
/// Stores interleaved min/max floats per time bin across the full file.
/// </summary>
/// <remarks>
/// Wire format (v1):
/// <list type="bullet">
/// <item><description>int32 magic 'C2WF' (0x46573243 little-endian)</description></item>
/// <item><description>int32 version = 1</description></item>
/// <item><description>int32 binCount</description></item>
/// <item><description>float32[binCount * 2] interleaved min, max</description></item>
/// </list>
/// Legacy format (no header): raw float32[binCount * 2] only — still supported on load.
/// </remarks>
public sealed class WaveformPeaks
{
    public const int Magic = 0x46573243; // 'C2WF'
    public const int CurrentVersion = 1;
    public const int DefaultBinCount = 2048;
    public const int MinBinCount = 256;
    public const int MaxBinCount = 16384;

    /// <summary>
    /// Included in <see cref="CacheFileName"/> so algorithm changes miss old sparse caches.
    /// </summary>
    public const int CacheKeyVersion = 2;

    /// <summary>Number of time bins spanning the full media duration.</summary>
    public int BinCount { get; }

    /// <summary>Interleaved min/max: [min0, max0, min1, max1, …].</summary>
    public float[] MinMax { get; }

    public WaveformPeaks(int binCount, float[] minMax)
    {
        if (binCount < 1) throw new ArgumentOutOfRangeException(nameof(binCount));
        if (minMax == null || minMax.Length < binCount * 2)
            throw new ArgumentException("MinMax must hold binCount * 2 floats.", nameof(minMax));

        BinCount = binCount;
        MinMax = minMax;
    }

    /// <summary>Min amplitude for bin <paramref name="i"/>.</summary>
    public float GetMin(int i) => MinMax[i * 2];

    /// <summary>Max amplitude for bin <paramref name="i"/>.</summary>
    public float GetMax(int i) => MinMax[i * 2 + 1];

    /// <summary>
    /// Builds display bins from streaming chunk peaks.
    /// When there are fewer chunks than <paramref name="requestedBinCount"/>, stores one bin
    /// per chunk so short files keep their real envelope instead of a sparse 4096-wide grid.
    /// When there are more chunks, peak-reduces into <paramref name="requestedBinCount"/> bins.
    /// </summary>
    /// <param name="chunks">Sequential min/max pairs covering the file in time order.</param>
    /// <param name="requestedBinCount">Target display resolution (clamped to at least 1).</param>
    /// <returns>A peak envelope spanning the full file.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="chunks"/> is empty.</exception>
    public static WaveformPeaks FromChunks(IReadOnlyList<(float min, float max)> chunks, int requestedBinCount)
    {
        if (chunks == null || chunks.Count == 0)
            throw new ArgumentException("At least one chunk is required.", nameof(chunks));

        int chunkN = chunks.Count;
        int binCount = Math.Max(1, requestedBinCount);

        if (chunkN <= binCount)
        {
            var copy = new float[chunkN * 2];
            for (int i = 0; i < chunkN; i++)
            {
                copy[i * 2] = chunks[i].min;
                copy[i * 2 + 1] = chunks[i].max;
            }
            return new WaveformPeaks(chunkN, copy);
        }

        var minMax = new float[binCount * 2];
        for (int i = 0; i < binCount; i++)
        {
            int c0 = (int)((long)i * chunkN / binCount);
            int c1 = (int)((long)(i + 1) * chunkN / binCount);
            if (c1 <= c0) c1 = c0 + 1;
            if (c1 > chunkN) c1 = chunkN;

            float mn = float.MaxValue;
            float mx = float.MinValue;
            for (int c = c0; c < c1; c++)
            {
                var (a, b) = chunks[c];
                if (a < mn) mn = a;
                if (b > mx) mx = b;
            }
            minMax[i * 2] = mn == float.MaxValue ? 0f : mn;
            minMax[i * 2 + 1] = mx == float.MinValue ? 0f : mx;
        }

        return new WaveformPeaks(binCount, minMax);
    }

    /// <summary>
    /// Linearly interpolates min/max at a fractional bin position in <c>[0, BinCount)</c>.
    /// </summary>
    /// <param name="binPos">Fractional bin index.</param>
    /// <param name="min">Interpolated minimum.</param>
    /// <param name="max">Interpolated maximum.</param>
    public void InterpolateAt(float binPos, out float min, out float max)
    {
        if (BinCount <= 1)
        {
            min = GetMin(0);
            max = GetMax(0);
            return;
        }

        if (binPos <= 0f)
        {
            min = GetMin(0);
            max = GetMax(0);
            return;
        }

        if (binPos >= BinCount - 1)
        {
            min = GetMin(BinCount - 1);
            max = GetMax(BinCount - 1);
            return;
        }

        int i = (int)Math.Floor(binPos);
        float t = binPos - i;
        int j = i + 1;
        min = GetMin(i) + (GetMin(j) - GetMin(i)) * t;
        max = GetMax(i) + (GetMax(j) - GetMax(i)) * t;
    }

    /// <summary>
    /// Envelope over a file-normalized range. A column wider than one bin uses peak min/max
    /// of covering bins; a sub-bin column (zoom in / short file) interpolates adjacent bins.
    /// </summary>
    /// <param name="startNorm">Range start in 0–1 of the full file.</param>
    /// <param name="endNorm">Range end in 0–1 of the full file.</param>
    /// <param name="min">Minimum amplitude in the range.</param>
    /// <param name="max">Maximum amplitude in the range.</param>
    public void GetEnvelope(float startNorm, float endNorm, out float min, out float max)
    {
        startNorm = Math.Clamp(startNorm, 0f, 1f);
        endNorm = Math.Clamp(endNorm, 0f, 1f);
        if (endNorm < startNorm)
            (startNorm, endNorm) = (endNorm, startNorm);

        float b0 = startNorm * BinCount;
        float b1 = endNorm * BinCount;
        if (b1 < b0 + 1e-6f)
            b1 = b0 + 1e-6f;

        if (b1 - b0 < 1f)
        {
            InterpolateAt((b0 + b1) * 0.5f, out min, out max);
            return;
        }

        int i0 = Math.Clamp((int)Math.Floor(b0), 0, BinCount - 1);
        int i1 = Math.Clamp((int)Math.Ceiling(b1), i0 + 1, BinCount);
        min = float.MaxValue;
        max = float.MinValue;
        for (int i = i0; i < i1; i++)
        {
            float mn = GetMin(i);
            float mx = GetMax(i);
            if (mn < min) min = mn;
            if (mx > max) max = mx;
        }

        if (min > max)
        {
            min = 0f;
            max = 0f;
        }
    }

    /// <summary>
    /// Absolute peak in <paramref name="startNorm"/>–<paramref name="endNorm"/>, floored so
    /// near-silent files still fill the display.
    /// </summary>
    /// <param name="startNorm">Range start in 0–1 of the full file.</param>
    /// <param name="endNorm">Range end in 0–1 of the full file.</param>
    /// <param name="floor">Minimum scale so a quiet file is not stretched by tiny noise.</param>
    /// <returns>Divisor for normalizing min/max into −1…1 display space.</returns>
    public float ComputePeakScale(float startNorm = 0f, float endNorm = 1f, float floor = 0.05f)
    {
        GetEnvelope(startNorm, endNorm, out float min, out float max);
        float scale = Math.Max(Math.Abs(min), Math.Abs(max));
        return Math.Max(floor, scale);
    }

    /// <summary>
    /// Serializes to the versioned byte format for component/session storage.
    /// </summary>
    public byte[] ToBytes()
    {
        using var ms = new MemoryStream(12 + MinMax.Length * sizeof(float));
        using var bw = new BinaryWriter(ms);
        bw.Write(Magic);
        bw.Write(CurrentVersion);
        bw.Write(BinCount);
        for (int i = 0; i < MinMax.Length; i++)
            bw.Write(MinMax[i]);
        return ms.ToArray();
    }

    /// <summary>
    /// Deserializes versioned or legacy raw-float payloads.
    /// </summary>
    public static WaveformPeaks FromBytes(byte[] data)
    {
        if (data == null || data.Length < sizeof(float) * 2)
            return null;

        // Versioned header?
        if (data.Length >= 12)
        {
            int magic = BitConverter.ToInt32(data, 0);
            if (magic == Magic)
            {
                int version = BitConverter.ToInt32(data, 4);
                int binCount = BitConverter.ToInt32(data, 8);
                if (version != CurrentVersion || binCount < 1)
                    return null;
                int expected = 12 + binCount * 2 * sizeof(float);
                if (data.Length < expected)
                    return null;
                var minMax = new float[binCount * 2];
                Buffer.BlockCopy(data, 12, minMax, 0, binCount * 2 * sizeof(float));
                return new WaveformPeaks(binCount, minMax);
            }
        }

        // Legacy: raw min/max floats only
        if (data.Length % (sizeof(float) * 2) != 0)
            return null;
        int legacyBins = data.Length / (sizeof(float) * 2);
        var legacy = new float[legacyBins * 2];
        Buffer.BlockCopy(data, 0, legacy, 0, data.Length);
        return new WaveformPeaks(legacyBins, legacy);
    }

    /// <summary>
    /// Stable cache file name for a media path (path + size + mtime).
    /// </summary>
    public static string CacheFileName(string mediaPath)
    {
        long length = 0;
        long mtime = 0;
        try
        {
            var fi = new FileInfo(mediaPath);
            if (fi.Exists)
            {
                length = fi.Length;
                mtime = fi.LastWriteTimeUtc.Ticks;
            }
        }
        catch { /* ignore */ }

        string key = $"{Path.GetFullPath(mediaPath)}|{length}|{mtime}|v{CacheKeyVersion}";
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        var sb = new StringBuilder(hash.Length * 2);
        foreach (byte b in hash)
            sb.Append(b.ToString("x2"));
        return sb.ToString() + ".c2wf";
    }

    /// <summary>Clamps a UI/settings resolution into a valid bin count.</summary>
    public static int ClampBinCount(int requested)
    {
        if (requested < MinBinCount) return DefaultBinCount;
        return Math.Clamp(requested, MinBinCount, MaxBinCount);
    }
}
