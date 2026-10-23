// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System.Diagnostics;

namespace Cue2.Media.Decoders;

/// <summary>
/// How long the stages of the last decoder open took (milliseconds), for the start timing log.
/// </summary>
public sealed class DecoderOpenTimes
{
    /// <summary>avformat_open_input (file open and container header).</summary>
    public double InputMs;

    /// <summary>avformat_find_stream_info (probe; may read and decode packets).</summary>
    public double StreamInfoMs;

    /// <summary>Codec open (including the hardware decoder attach for video).</summary>
    public double CodecMs;

    /// <summary>Full decode into memory (audio sample-accurate copy), 0 when streaming.</summary>
    public double StoreMs;

    /// <summary>True when only the sound streams were probed (MP4/MOV sound-only open).</summary>
    public bool QuickProbe;

    /// <summary>True when a sound file streams instead of being decoded into memory at open.</summary>
    public bool Streaming;

    /// <summary>Stopwatch ticks when the open finished.</summary>
    public long EndTicks;

    /// <summary>Milliseconds since <paramref name="startTicks"/>.</summary>
    public static double Since(long startTicks) =>
        (Stopwatch.GetTimestamp() - startTicks) * 1000.0 / Stopwatch.Frequency;

    /// <summary>Short text for the log, for example "input 12, probe 40, codec 8".</summary>
    public override string ToString()
    {
        string text = $"input {InputMs:F0}, probe {StreamInfoMs:F0}, codec {CodecMs:F0}";
        if (QuickProbe)
            text += ", sound streams only";
        if (Streaming)
            text += ", streaming";
        if (StoreMs > 0)
            text += $", decode to memory {StoreMs:F0}";
        return text;
    }
}
