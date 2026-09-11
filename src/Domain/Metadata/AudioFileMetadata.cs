// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

namespace Cue2.Domain.Metadata;

/// <summary>
/// Simple POCO for audio file metadata extracted via FFmpeg.
/// Supports duration (seconds), channels, sample rate (Hz), bit depth, and codec/format string.
/// </summary>
public class AudioFileMetadata
{
    /// <summary>Duration in seconds (double for precision; 0 if unknown).</summary>
    public double Duration { get; set; } = 0.0;

    /// <summary>Number of audio channels (e.g., 1 for mono, 2 for stereo).</summary>
    public int Channels { get; set; } = 0;

    /// <summary>Sample rate in Hz (e.g., 44100).</summary>
    public int SampleRate { get; set; } = 0;

    /// <summary>Bit depth per sample (e.g., 16 for S16).</summary>
    public int BitDepth { get; set; } = 0;
    
    /// <summary>Codec/format name (e.g., "mp3", "flac").</summary>
    public string Codec { get; set; } = string.Empty;

    /// <summary>Overall file format/container (e.g., "mp3", "wav").</summary>
    public string Format { get; set; } = string.Empty;
}