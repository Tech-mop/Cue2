// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

namespace Cue2.Media.Decoders;

/// <summary>
/// Immutable description of an opened audio stream after decoder open.
/// </summary>
public sealed class AudioSourceInfo
{
    /// <summary>
    /// Sample rate in Hz.
    /// </summary>
    public int SampleRate { get; init; }

    /// <summary>
    /// Number of channels (interleaved in decoder output).
    /// </summary>
    public int Channels { get; init; }

    /// <summary>
    /// Stream duration in microseconds, or 0 if unknown.
    /// </summary>
    public long DurationUs { get; init; }

    /// <summary>
    /// Codec short name (e.g. "mp3", "flac"), or "unknown".
    /// </summary>
    public string CodecName { get; init; } = "unknown";

    /// <summary>
    /// Source bit depth estimated from sample format, or 0 if unknown.
    /// </summary>
    public int BitDepth { get; init; }

    /// <summary>
    /// File path that was opened.
    /// </summary>
    public string FilePath { get; init; } = string.Empty;

    /// <summary>
    /// True when the decoder fully expanded the stream to interleaved float PCM
    /// for sample-accurate seek/loop (typical for frame-based codecs like MP3).
    /// </summary>
    public bool IsSampleAccurateStore { get; init; }
}
