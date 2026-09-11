// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

namespace Cue2.Media.Decoders;

/// <summary>
/// Immutable description of an opened video stream after decoder open.
/// </summary>
public sealed class VideoSourceInfo
{
    /// <summary>Frame width in pixels.</summary>
    public int Width { get; init; }

    /// <summary>Frame height in pixels.</summary>
    public int Height { get; init; }

    /// <summary>Nominal frames per second (r_frame_rate or avg).</summary>
    public double Fps { get; init; }

    /// <summary>Average frame duration in microseconds.</summary>
    public long FrameDurationUs { get; init; }

    /// <summary>Stream duration in microseconds, or 0 if unknown.</summary>
    public long DurationUs { get; init; }

    /// <summary>Codec short name, or "unknown".</summary>
    public string CodecName { get; init; } = "unknown";

    /// <summary>File path that was opened.</summary>
    public string FilePath { get; init; } = string.Empty;

    /// <summary>Bytes per RGBA frame (width * height * 4).</summary>
    public int FrameByteSize { get; init; }
}
