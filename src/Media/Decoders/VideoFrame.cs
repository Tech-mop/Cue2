// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

namespace Cue2.Media.Decoders;

/// <summary>
/// One decoded video frame in interleaved RGBA8, with presentation timestamp.
/// Buffer ownership: the decoder may recycle <see cref="Rgba"/> after the next
/// successful <see cref="VideoSourceDecoder.ReadFrame"/> unless the caller copies it.
/// Prefer copying into a display buffer before the next read when presenting asynchronously.
/// </summary>
public sealed class VideoFrame
{
    /// <summary>Interleaved RGBA8 pixel data (width * height * 4).</summary>
    public byte[] Rgba { get; set; }

    /// <summary>Presentation timestamp in microseconds from media start.</summary>
    public long PtsUs { get; set; }

    /// <summary>Frame width.</summary>
    public int Width { get; set; }

    /// <summary>Frame height.</summary>
    public int Height { get; set; }
}
