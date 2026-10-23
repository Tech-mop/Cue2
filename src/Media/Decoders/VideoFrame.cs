// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using Godot;

namespace Cue2.Media.Decoders;

/// <summary>
/// One decoded video frame, either interleaved RGBA8 or 8-bit 4:2:0 planes.
/// The planes are either byte buffers or Godot images made on the decode worker.
/// Buffer ownership stays with the decoder. Return every frame through
/// <see cref="VideoSourceDecoder.ReleaseFrame"/> after the upload returns.
/// </summary>
public sealed class VideoFrame
{
    /// <summary>Interleaved RGBA8 (width * height * 4). Null on the plane path.</summary>
    public byte[] Rgba { get; set; }

    /// <summary>Full-size luma, one byte per pixel. Null on the RGBA path.</summary>
    public byte[] Y { get; set; }

    /// <summary>Half-size chroma, interleaved U then V. Null on the RGBA path.</summary>
    public byte[] Uv { get; set; }

    /// <summary>Chroma plane width. Zero on the RGBA path.</summary>
    public int ChromaWidth { get; set; }

    /// <summary>Chroma plane height. Zero on the RGBA path.</summary>
    public int ChromaHeight { get; set; }

    /// <summary>True when luma and chroma use limited (TV) range.</summary>
    public bool LimitedRange { get; set; }

    /// <summary>0 = BT.601, 1 = BT.709, 2 = BT.2020.</summary>
    public int MatrixId { get; set; }

    /// <summary>Presentation timestamp in microseconds from media start.</summary>
    public long PtsUs { get; set; }

    /// <summary>Frame width.</summary>
    public int Width { get; set; }

    /// <summary>Frame height.</summary>
    public int Height { get; set; }

    /// <summary>
    /// Full-size luma as a Godot image (L8), made on the decode worker so the main thread
    /// only uploads it. Null unless the decoder prepares images.
    /// </summary>
    public Image YImage { get; set; }

    /// <summary>Half-size interleaved chroma as a Godot image (RG8). Null unless the decoder prepares images.</summary>
    public Image UvImage { get; set; }

    /// <summary>True when the planes are ready-made Godot images.</summary>
    public bool HasPlaneImages => YImage != null && UvImage != null;

    /// <summary>True when this frame carries Y and UV planes instead of RGBA.</summary>
    public bool IsPlanar => (Y != null && Uv != null) || HasPlaneImages;

    /// <summary>Frees the plane images, if any. Safe from any thread.</summary>
    public void DisposePlaneImages()
    {
        YImage?.Dispose();
        UvImage?.Dispose();
        YImage = null;
        UvImage = null;
    }
}
