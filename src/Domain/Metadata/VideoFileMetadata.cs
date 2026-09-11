// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System.Collections.Generic;

namespace Cue2.Domain.Metadata;

/// <summary>
/// Simple POCO for video file metadata extracted via FFmpeg.
/// Supports duration (seconds), width, height, frame rate, codec/format string, audio metadata, and subtitle tracks.
/// </summary>
public class VideoFileMetadata
{
    /// <summary>Duration in seconds (double for precision; 0 if unknown).</summary>
    public double Duration { get; set; } = 0.0;

    /// <summary>Video width in pixels.</summary>
    public int Width { get; set; } = 0;

    /// <summary>Video height in pixels.</summary>
    public int Height { get; set; } = 0;

    /// <summary>Frame rate in fps.</summary>
    public float FrameRate { get; set; } = 0.0f;

    /// <summary>Codec/format name (e.g., "h264", "vp9").</summary>
    public string Codec { get; set; } = string.Empty;

    /// <summary>Overall file format/container (e.g., "mp4", "avi").</summary>
    public string Format { get; set; } = string.Empty;

    /// <summary>Number of audio channels if audio is present (e.g., 1 for mono, 2 for stereo; 0 if no audio).</summary>
    public int AudioChannels { get; set; } = 0;

    /// <summary>Audio sample rate in Hz if audio is present (e.g., 44100; 0 if no audio).</summary>
    public int AudioSampleRate { get; set; } = 0;

    /// <summary>Audio bit depth per sample if audio is present (e.g., 16 for S16; 0 if no audio).</summary>
    public int AudioBitDepth { get; set; } = 0;

    /// <summary>Audio codec name if audio is present (e.g., "aac", "mp3"; empty if no audio).</summary>
    public string AudioCodec { get; set; } = string.Empty;

    /// <summary>
    /// Subtitle / closed-caption streams found in the container (may be empty).
    /// </summary>
    public List<SubtitleTrackInfo> SubtitleTracks { get; set; } = new();

    /// <summary>True when at least one text-based subtitle track is available.</summary>
    public bool HasTextSubtitles =>
        SubtitleTracks != null && SubtitleTracks.Exists(t => t != null && t.IsTextBased);

    /// <summary>
    /// Returns the first text-based subtitle track, or null.
    /// </summary>
    public SubtitleTrackInfo GetDefaultTextSubtitleTrack()
    {
        if (SubtitleTracks == null)
            return null;
        return SubtitleTracks.Find(t => t != null && t.IsTextBased);
    }
}
