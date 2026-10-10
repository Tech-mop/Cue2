// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System;

namespace Cue2.Domain.Cues;

/// <summary>
/// One playable span on an audio timeline.
/// Loop-bound (non-continue) nodes own the region before them. Nodes with
/// <see cref="AudioTimelineNode.ContinueRegion"/> are skipped when splitting regions.
/// The implicit tail after the last loop-bound node uses <see cref="NodeId"/> 0, play count 1.
/// </summary>
public readonly struct AudioTimelineRegion
{
    /// <summary>
    /// Stable id of the node that owns this region, or 0 for the tail after the last node.
    /// </summary>
    public int NodeId { get; init; }

    /// <summary>File-time start of the region, in seconds.</summary>
    public double StartSeconds { get; init; }

    /// <summary>File-time end of the region, in seconds.</summary>
    public double EndSeconds { get; init; }

    /// <summary>When true, this region repeats until the cue is stopped.</summary>
    public bool Loop { get; init; }

    /// <summary>Plays of this region (ignored when <see cref="Loop"/>). Always ≥ 1.</summary>
    public int PlayCount { get; init; }

    /// <summary>File-time length of the region, in seconds.</summary>
    public double FileDuration => Math.Max(0.0, EndSeconds - StartSeconds);

    /// <summary>
    /// Builds a playback region clipped to <paramref name="startSeconds"/>–<paramref name="endSeconds"/>.
    /// </summary>
    /// <param name="nodeId">Owning node id, or 0 for the tail after the last loop bound.</param>
    /// <param name="startSeconds">File-time start, in seconds.</param>
    /// <param name="endSeconds">File-time end, in seconds.</param>
    /// <param name="loop">When true, the region repeats until stopped.</param>
    /// <param name="playCount">Plays when not looping. Always ≥ 1.</param>
    /// <returns>A region with <see cref="PlayCount"/> at least 1.</returns>
    public static AudioTimelineRegion Create(
        int nodeId,
        double startSeconds,
        double endSeconds,
        bool loop = false,
        int playCount = 1)
    {
        return new AudioTimelineRegion
        {
            NodeId = nodeId,
            StartSeconds = startSeconds,
            EndSeconds = endSeconds,
            Loop = loop,
            PlayCount = loop ? int.MaxValue : Math.Max(1, playCount)
        };
    }
}
