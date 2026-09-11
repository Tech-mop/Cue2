// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System;
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

        string key = $"{Path.GetFullPath(mediaPath)}|{length}|{mtime}";
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
