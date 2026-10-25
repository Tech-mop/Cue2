// SPDX-FileCopyrightText: 2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System;
using System.IO;
using System.Text;

namespace Cue2.EngineTests.Fixtures;

/// <summary>
/// Writes small media files at test time, so no media is stored in git.
/// </summary>
internal static class MediaFixtures
{
    /// <summary>
    /// Writes a 16-bit PCM WAV file with a sine tone (or silence when <paramref name="frequency"/> is 0).
    /// </summary>
    /// <param name="path">File to write.</param>
    /// <param name="seconds">Length in seconds.</param>
    /// <param name="frequency">Tone frequency in Hz; 0 for silence.</param>
    /// <param name="sampleRate">Sample rate in Hz.</param>
    /// <param name="channels">Channel count.</param>
    /// <param name="amplitude">Peak level, 0 to 1.</param>
    /// <returns><paramref name="path"/>.</returns>
    internal static string WriteToneWav(
        string path,
        double seconds,
        double frequency = 440,
        int sampleRate = 48000,
        int channels = 2,
        double amplitude = 0.25)
    {
        int frames = (int)Math.Round(seconds * sampleRate);
        int dataBytes = frames * channels * 2;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var writer = new BinaryWriter(stream, Encoding.ASCII);
        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + dataBytes);
        writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)channels);
        writer.Write(sampleRate);
        writer.Write(sampleRate * channels * 2);
        writer.Write((short)(channels * 2));
        writer.Write((short)16);
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(dataBytes);
        for (int i = 0; i < frames; i++)
        {
            double value = frequency > 0 ? amplitude * Math.Sin(2 * Math.PI * frequency * i / sampleRate) : 0;
            short sample = (short)Math.Round(value * short.MaxValue);
            for (int c = 0; c < channels; c++)
                writer.Write(sample);
        }
        return path;
    }

    /// <summary>
    /// Writes a raw YUV4MPEG2 (Y4M) video that cycles through solid colours, one colour per second.
    /// </summary>
    /// <param name="path">File to write.</param>
    /// <param name="width">Width in pixels (even).</param>
    /// <param name="height">Height in pixels (even).</param>
    /// <param name="frames">Number of frames.</param>
    /// <param name="fps">Frames per second.</param>
    /// <returns><paramref name="path"/>.</returns>
    internal static string WriteColourY4m(string path, int width = 160, int height = 90, int frames = 50, int fps = 25)
    {
        (byte R, byte G, byte B)[] colours = { (200, 30, 30), (30, 200, 30), (30, 30, 200), (230, 230, 230) };
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write);
        byte[] header = Encoding.ASCII.GetBytes($"YUV4MPEG2 W{width} H{height} F{fps}:1 Ip A1:1 C420jpeg\n");
        stream.Write(header);
        byte[] frameTag = Encoding.ASCII.GetBytes("FRAME\n");
        int lumaSize = width * height;
        int chromaSize = (width / 2) * (height / 2);
        var plane = new byte[lumaSize + 2 * chromaSize];
        for (int f = 0; f < frames; f++)
        {
            var (r, g, b) = colours[(f / Math.Max(1, fps)) % colours.Length];
            byte y = Clamp(0.299 * r + 0.587 * g + 0.114 * b);
            byte u = Clamp(128 - 0.168736 * r - 0.331264 * g + 0.5 * b);
            byte v = Clamp(128 + 0.5 * r - 0.418688 * g - 0.081312 * b);
            Array.Fill(plane, y, 0, lumaSize);
            Array.Fill(plane, u, lumaSize, chromaSize);
            Array.Fill(plane, v, lumaSize + chromaSize, chromaSize);
            stream.Write(frameTag);
            stream.Write(plane);
        }
        return path;
    }

    private static byte Clamp(double value) => (byte)Math.Clamp((int)Math.Round(value), 0, 255);
}
