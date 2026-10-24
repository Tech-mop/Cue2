// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using Godot;
using Array = Godot.Collections.Array;
using Dictionary = Godot.Collections.Dictionary;

namespace Cue2.Domain.Cues;

/// <summary>
/// Supplies in/out window, file duration, and base rate/pitch for a <see cref="MediaTimeline"/>.
/// Implemented by <see cref="AudioComponent"/> and <see cref="VideoComponent"/>.
/// </summary>
public interface IMediaTimelineHost
{
    /// <summary>File duration in seconds from metadata, or 0 when unknown.</summary>
    double TimelineFileDuration { get; }

    /// <summary>Cue in-point in file seconds.</summary>
    double TimelineStartTime { get; }

    /// <summary>Cue out-point in file seconds, or −1 for the file end.</summary>
    double TimelineEndTime { get; }

    /// <summary>Component play-rate multiplier (images report 1).</summary>
    double TimelinePlayRate { get; }

    /// <summary>Component pitch offset in cents.</summary>
    float TimelinePitchCents { get; }

    /// <summary>True when file metadata is known so an end-of-file node can be pinned.</summary>
    bool TimelineHasMetadata { get; }

    /// <summary>
    /// Clamps a proposed in-point into the file.
    /// </summary>
    /// <param name="proposedSeconds">Requested start time in seconds.</param>
    /// <returns>Clamped start time in seconds.</returns>
    double ClampTimelineStartTime(double proposedSeconds);
}

/// <summary>
/// Shared file-timeline for audio and video: numbered nodes, loop regions, and
/// volume / rate / pitch / opacity curves. Owned by one <see cref="IMediaTimelineHost"/>.
/// </summary>
/// <remarks>
/// Serialized as the <c>TimelineNodes</c> array on the host component. Peak envelopes
/// are not part of this object.
/// </remarks>
public class MediaTimeline
{
    /// <summary>Maximum user-placed markers (plus the locked end-of-file node).</summary>
    public const int MaxUserNodes = 32;

    /// <summary>Numbered markers on the file timeline (file seconds).</summary>
    /// <remarks>
    /// Main thread only. Add and remove markers through the methods of this class, which
    /// republish the snapshot that playback threads read; do not change the list directly.
    /// </remarks>
    public List<AudioTimelineNode> Nodes { get; } = new List<AudioTimelineNode>();

    private readonly IMediaTimelineHost _host;
    private int _nextNodeId = 1;

    /// <summary>
    /// Immutable copy of <see cref="Nodes"/> in time order, replaced (never changed) on the
    /// main thread after every add, remove, load or clamp. Playback threads evaluate the
    /// curve from this array, so a main-thread edit can never change a list they enumerate.
    /// </summary>
    private volatile AudioTimelineNode[] _snapshot = System.Array.Empty<AudioTimelineNode>();

    /// <summary>
    /// Creates a timeline bound to <paramref name="host"/>.
    /// </summary>
    /// <param name="host">Audio or video component that owns this timeline.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="host"/> is null.</exception>
    public MediaTimeline(IMediaTimelineHost host)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
    }

    /// <summary>
    /// Writes <see cref="Nodes"/> into <paramref name="data"/> as <c>TimelineNodes</c>.
    /// </summary>
    /// <param name="data">Host component dictionary.</param>
    public void WriteToData(Dictionary data)
    {
        if (data == null || Nodes.Count == 0)
            return;

        var nodes = new Array();
        foreach (var node in Nodes)
        {
            if (node == null) continue;
            nodes.Add(node.GetData());
        }

        if (nodes.Count > 0)
            data.Add("TimelineNodes", nodes);
    }

    /// <summary>
    /// Restores markers from showfile / history data.
    /// </summary>
    /// <param name="data">Host component dictionary.</param>
    public void LoadFromData(Dictionary data)
    {
        Nodes.Clear();
        _nextNodeId = 1;
        PublishSnapshot();
        if (data == null || !data.ContainsKey("TimelineNodes"))
            return;
        if (data["TimelineNodes"].VariantType != Variant.Type.Array)
            return;

        var arr = data["TimelineNodes"].AsGodotArray();
        TimelineVolumeInterpolation? legacyInterp = data.ContainsKey("TimelineVolumeInterpolation")
            ? TimelineVolume.FromInt(data["TimelineVolumeInterpolation"].AsInt32())
            : null;
        int maxId = 0;
        int userCount = 0;
        foreach (var item in arr)
        {
            if (item.VariantType != Variant.Type.Dictionary)
                continue;
            var dict = item.AsGodotDictionary();
            var node = AudioTimelineNode.FromData(dict);
            if (node == null)
                continue;
            node.TimeSeconds = Math.Max(0.0, node.TimeSeconds);
            if (!dict.ContainsKey("VolumeInterpolation"))
                node.VolumeInterpolation = legacyInterp ?? TimelineVolumeInterpolation.Snap;

            if (!node.IsFileEnd)
            {
                if (userCount >= MaxUserNodes)
                    continue;
                userCount++;
            }

            Nodes.Add(node);
            if (node.Id > maxId)
                maxId = node.Id;
        }

        _nextNodeId = maxId + 1;
        if (_nextNodeId < 1)
            _nextNodeId = 1;
        ClampAllNodes();
    }

    /// <summary>
    /// Wall-clock of one pass through <see cref="GetPlaybackRegions"/> at the live rate curve.
    /// Component play count / loop are not applied here.
    /// </summary>
    /// <param name="loopingRegionsAsOnePlay">
    /// When true, a looping region counts as one play (always finite).
    /// When false, any looping region yields −1.
    /// </param>
    /// <returns>Seconds, or −1 when a region loops and <paramref name="loopingRegionsAsOnePlay"/> is false.</returns>
    public double ComputeSequenceWallClock(bool loopingRegionsAsOnePlay)
    {
        double fileDuration = Math.Max(0.0, _host.TimelineFileDuration);
        double windowStart = _host.ClampTimelineStartTime(_host.TimelineStartTime);
        double windowEnd = _host.TimelineEndTime < 0 ? fileDuration : _host.TimelineEndTime;
        double windowLen = Math.Max(0.0, windowEnd - windowStart);

        var playbackRegions = GetPlaybackRegions();
        if (playbackRegions.Count == 0)
            return windowLen / Math.Max(AudioComponent.MinPlayRate, _host.TimelinePlayRate);

        double passWall = 0.0;
        foreach (var span in playbackRegions)
        {
            if (span.Loop && !loopingRegionsAsOnePlay)
                return -1.0;

            double onePass = IntegrateTimelineWallClock(span.StartSeconds, span.EndSeconds);
            int plays = span.Loop ? 1 : Math.Max(1, span.PlayCount);
            passWall += onePass * plays;
        }

        return passWall;
    }

    /// <summary>
    /// Playable spans for timeline loop bounds: each non-continue node owns the region before it
    /// (from the previous non-continue node, or the in-point). Continue nodes are volume/rate/pitch
    /// only. The tail after the last loop-bound node plays once. Empty when the window has no length.
    /// </summary>
    /// <returns>Regions clipped to the cue in/out window, in time order.</returns>
    public List<AudioTimelineRegion> GetPlaybackRegions()
    {
        var regions = new List<AudioTimelineRegion>();
        double fileDuration = Math.Max(0.0, _host.TimelineFileDuration);
        double windowStart = _host.ClampTimelineStartTime(_host.TimelineStartTime);
        double windowEnd = _host.TimelineEndTime < 0 ? fileDuration : _host.TimelineEndTime;
        if (windowEnd < windowStart)
            windowEnd = windowStart;

        const double eps = 1e-6;
        double cursor = windowStart;
        foreach (var node in GetOrderedSnapshot())
        {
            if (node.ContinueRegion)
                continue;
            if (node.TimeSeconds <= cursor + eps)
                continue;

            double regionEnd = Math.Min(node.TimeSeconds, windowEnd);
            if (regionEnd > cursor + eps)
            {
                regions.Add(AudioTimelineRegion.Create(
                    node.Id, cursor, regionEnd, node.Loop, node.PlayCount));
                cursor = regionEnd;
            }

            if (cursor >= windowEnd - eps)
                break;
        }

        if (cursor < windowEnd - eps)
            regions.Add(AudioTimelineRegion.Create(0, cursor, windowEnd));

        return regions;
    }

    /// <summary>
    /// Absolute volume, play rate, and pitch at a file time (one neighbor scan).
    /// </summary>
    /// <param name="fileSeconds">Position on the file, in seconds.</param>
    /// <returns>Clamped mix of component values and the timeline curve.</returns>
    public TimelineAutomation EvaluateTimeline(double fileSeconds) =>
        EvaluateTimeline(fileSeconds, playRateOverride: null, pitchCentsOverride: null);

    /// <summary>
    /// Absolute play rate and pitch, plus relative volume and opacity, at a file time,
    /// with optional component-rate/pitch overrides (control fades). Timeline relative scale/offset still apply.
    /// </summary>
    /// <param name="fileSeconds">Position on the file, in seconds.</param>
    /// <param name="playRateOverride">When set, used instead of the host play rate as the base rate.</param>
    /// <param name="pitchCentsOverride">When set, used instead of the host pitch as the base pitch.</param>
    /// <returns>Clamped mix of base values and the timeline curve.</returns>
    public TimelineAutomation EvaluateTimeline(
        double fileSeconds,
        double? playRateOverride,
        float? pitchCentsOverride)
    {
        double baseRate = playRateOverride ?? _host.TimelinePlayRate;
        float basePitch = pitchCentsOverride ?? _host.TimelinePitchCents;

        if (!TryGetNeighbors(fileSeconds, out var prev, out var next, out float t)
            || (prev == null && next == null))
        {
            return new TimelineAutomation
            {
                VolumeLinear = AudioTimelineNode.DefaultVolumeLinear,
                PlayRate = AudioComponent.ClampPlayRate(baseRate),
                PitchCents = AudioComponent.ClampPitchCents(basePitch),
                OpacityScale = AudioTimelineNode.DefaultOpacityScale
            };
        }

        float volume;
        float rateScale;
        float pitchOffset;
        float opacityScale;
        if (prev == null)
        {
            volume = next.VolumeLinear;
            rateScale = next.RateScale;
            pitchOffset = next.PitchCents;
            opacityScale = next.OpacityScale;
        }
        else if (next == null)
        {
            volume = prev.VolumeLinear;
            rateScale = prev.RateScale;
            pitchOffset = prev.PitchCents;
            opacityScale = prev.OpacityScale;
        }
        else
        {
            var mode = next.VolumeInterpolation;
            volume = TimelineVolume.InterpolateVolumeLinear(mode, prev.VolumeLinear, next.VolumeLinear, t);
            rateScale = TimelineVolume.InterpolateRateScale(mode, prev.RateScale, next.RateScale, t);
            pitchOffset = TimelineVolume.InterpolatePitchCents(mode, prev.PitchCents, next.PitchCents, t);
            opacityScale = TimelineVolume.InterpolateOpacityScale(mode, prev.OpacityScale, next.OpacityScale, t);
        }

        return new TimelineAutomation
        {
            VolumeLinear = volume,
            PlayRate = AudioComponent.ClampPlayRate(baseRate * rateScale),
            PitchCents = AudioComponent.ClampPitchCents(basePitch + pitchOffset),
            OpacityScale = opacityScale
        };
    }

    /// <summary>
    /// Relative linear gain at a file time, interpolated between timeline nodes in dB.
    /// </summary>
    /// <param name="fileSeconds">Position on the file, in seconds.</param>
    /// <returns>Clamped linear gain (1 = 0 dB relative). Unity when there are no nodes.</returns>
    public float EvaluateTimelineVolumeLinear(double fileSeconds) =>
        EvaluateTimeline(fileSeconds).VolumeLinear;

    /// <summary>
    /// Absolute play rate at a file time: host play rate × relative node scale.
    /// </summary>
    /// <param name="fileSeconds">Position on the file, in seconds.</param>
    /// <returns>Clamped play-rate multiplier.</returns>
    public double EvaluateTimelinePlayRate(double fileSeconds) =>
        EvaluateTimeline(fileSeconds).PlayRate;

    /// <summary>
    /// Absolute pitch in cents at a file time: host pitch + relative node offset.
    /// </summary>
    /// <param name="fileSeconds">Position on the file, in seconds.</param>
    /// <returns>Clamped pitch in cents.</returns>
    public float EvaluateTimelinePitchCents(double fileSeconds) =>
        EvaluateTimeline(fileSeconds).PitchCents;

    /// <summary>
    /// Wall-clock seconds to play a file-time span at the live rate curve.
    /// </summary>
    /// <param name="startSeconds">File-time start, in seconds.</param>
    /// <param name="endSeconds">File-time end, in seconds.</param>
    /// <returns>Integrated wall-clock length. 0 when the span is empty.</returns>
    public double IntegrateTimelineWallClock(double startSeconds, double endSeconds)
    {
        if (endSeconds <= startSeconds)
            return 0.0;

        return WalkRateCurve(startSeconds, endSeconds, double.PositiveInfinity, out _);
    }

    /// <summary>
    /// File time at which wall-clock from <paramref name="startSeconds"/> equals <paramref name="wallOffset"/>.
    /// </summary>
    /// <param name="startSeconds">File-time start, in seconds.</param>
    /// <param name="endSeconds">File-time end, in seconds.</param>
    /// <param name="wallOffset">Desired wall-clock offset from the start of the span.</param>
    /// <returns>
    /// File time in <paramref name="startSeconds"/>–<paramref name="endSeconds"/>.
    /// Clamped to the span ends when the offset is outside the integrated length.
    /// </returns>
    public double FileTimeAtWallOffset(double startSeconds, double endSeconds, double wallOffset)
    {
        if (endSeconds <= startSeconds || wallOffset <= 0)
            return startSeconds;

        WalkRateCurve(startSeconds, endSeconds, wallOffset, out double fileTime);
        return fileTime;
    }

    /// <summary>
    /// Walks the rate curve from <paramref name="startSeconds"/> to <paramref name="endSeconds"/>
    /// in 8 midpoint steps per node span, adding up wall-clock time.
    /// </summary>
    /// <param name="startSeconds">File-time start, in seconds (less than the end).</param>
    /// <param name="endSeconds">File-time end, in seconds.</param>
    /// <param name="stopAtWall">
    /// Stop as soon as the wall clock reaches this value; pass
    /// <see cref="double.PositiveInfinity"/> to integrate the whole span.
    /// </param>
    /// <param name="stopFileTime">
    /// File time where <paramref name="stopAtWall"/> was reached, or <paramref name="endSeconds"/>.
    /// </param>
    /// <returns>Wall-clock seconds walked.</returns>
    private double WalkRateCurve(double startSeconds, double endSeconds, double stopAtWall, out double stopFileTime)
    {
        stopFileTime = endSeconds;
        var ordered = GetOrderedSnapshot();
        AudioTimelineNode prev = null;
        int i = 0;
        while (i < ordered.Length && ordered[i].TimeSeconds <= startSeconds)
        {
            prev = ordered[i];
            i++;
        }

        const int steps = 8;
        const double eps = 1e-12;
        double wall = 0.0;
        double cursor = startSeconds;
        while (cursor < endSeconds - eps)
        {
            var next = i < ordered.Length ? ordered[i] : null;
            double nextTime = next != null ? Math.Min(next.TimeSeconds, endSeconds) : endSeconds;
            if (nextTime <= cursor + eps)
            {
                prev = next;
                i++;
                continue;
            }

            double span = nextTime - cursor;
            for (int s = 0; s < steps; s++)
            {
                double t0 = cursor + span * s / steps;
                double t1 = cursor + span * (s + 1) / steps;
                double mid = 0.5 * (t0 + t1);
                float scale = RateScaleAt(prev, next, mid);
                double rate = AudioComponent.ClampPlayRate(_host.TimelinePlayRate * scale);
                if (rate < AudioComponent.MinPlayRate)
                    rate = AudioComponent.MinPlayRate;
                double stepWall = (t1 - t0) / rate;
                if (wall + stepWall >= stopAtWall - eps)
                {
                    double frac = stepWall <= eps ? 0.0 : (stopAtWall - wall) / stepWall;
                    stopFileTime = t0 + Math.Clamp(frac, 0.0, 1.0) * (t1 - t0);
                    return stopAtWall;
                }

                wall += stepWall;
            }

            cursor = nextTime;
            if (next != null && next.TimeSeconds <= cursor + eps)
            {
                prev = next;
                i++;
            }
        }

        return wall;
    }

    /// <summary>
    /// Wall-clock seconds from the start of the region sequence to the first occurrence of
    /// <paramref name="fileSeconds"/> (one play of a looping region).
    /// </summary>
    /// <param name="fileSeconds">Position on the file, in seconds.</param>
    /// <returns>
    /// Seconds ≥ 0. Times before the in-point map to 0; times after the last region map to
    /// the full sequence wall-clock (looping regions count as one play).
    /// </returns>
    public double WallClockAtFileTime(double fileSeconds)
    {
        var regions = GetPlaybackRegions();
        if (regions.Count == 0)
            return 0.0;

        double wall = 0.0;
        const double eps = 1e-9;
        foreach (var span in regions)
        {
            if (fileSeconds < span.StartSeconds - eps)
                return wall;

            if (fileSeconds <= span.EndSeconds + eps)
            {
                double clamped = Math.Clamp(fileSeconds, span.StartSeconds, span.EndSeconds);
                return wall + IntegrateTimelineWallClock(span.StartSeconds, clamped);
            }

            double onePass = IntegrateTimelineWallClock(span.StartSeconds, span.EndSeconds);
            int plays = span.Loop ? 1 : Math.Max(1, span.PlayCount);
            wall += onePass * plays;
        }

        return wall;
    }

    /// <summary>
    /// Picks a file time for a manually added node (midpoint of the longest gap).
    /// </summary>
    /// <returns>A time in file seconds, or −1 when no file duration is available.</returns>
    public double PickNewNodeTime()
    {
        double fileDuration = Math.Max(0.0, _host.TimelineFileDuration);
        if (fileDuration <= 1e-6)
            return -1;

        EnsureFileEndNode();
        double bestStart = 0;
        double bestLen = 0;
        double prev = 0;
        foreach (var node in GetNodesInTimeOrder())
        {
            double len = node.TimeSeconds - prev;
            if (len > bestLen)
            {
                bestLen = len;
                bestStart = prev;
            }
            prev = node.TimeSeconds;
        }

        if (bestLen < 0.002)
            return ClampNodeTime(fileDuration * 0.5);

        return ClampNodeTime(bestStart + bestLen * 0.5);
    }

    /// <summary>
    /// Clamps a timeline node into the file duration.
    /// User nodes stay just before the locked end-of-file node.
    /// </summary>
    /// <param name="proposedSeconds">Requested time in file seconds.</param>
    /// <param name="isFileEnd">When true, pin to the file duration.</param>
    /// <returns>Clamped time in seconds.</returns>
    public double ClampNodeTime(double proposedSeconds, bool isFileEnd = false)
    {
        double fileDuration = Math.Max(0.0, _host.TimelineFileDuration);
        if (isFileEnd)
            return fileDuration;

        double result = Math.Max(0.0, proposedSeconds);
        double maxUser = fileDuration > 0.002 ? fileDuration - 0.001 : fileDuration;
        if (result > maxUser)
            result = maxUser;
        return result;
    }

    /// <summary>
    /// Timeline nodes sorted by file time (stable by id when times match).
    /// </summary>
    /// <returns>A new list in display order. Index 0 is node number 1.</returns>
    public List<AudioTimelineNode> GetNodesInTimeOrder()
    {
        var ordered = new List<AudioTimelineNode>(Nodes.Count);
        foreach (var node in Nodes)
        {
            if (node != null)
                ordered.Add(node);
        }

        ordered.Sort(CompareByTimeThenId);
        return ordered;
    }

    /// <summary>
    /// Adds a numbered marker at the given file time.
    /// Volume and opacity default to the previous node, or the start of the curve when there is none.
    /// </summary>
    /// <param name="timeSeconds">Position on the file, in seconds.</param>
    /// <param name="volumeLinear">When set, used as the new node's relative volume.</param>
    /// <param name="opacityScale">When set, used as the new node's relative opacity.</param>
    /// <returns>The new node, or null when the cap is reached.</returns>
    public AudioTimelineNode AddNode(double timeSeconds, float? volumeLinear = null, float? opacityScale = null)
    {
        EnsureFileEndNode();
        if (CountUserNodes() >= MaxUserNodes)
            return null;

        double time = ClampNodeTime(timeSeconds, isFileEnd: false);
        var node = AudioTimelineNode.Create(_nextNodeId++, time);
        node.VolumeLinear = volumeLinear ?? GetInheritedVolume(time);
        node.OpacityScale = opacityScale ?? GetInheritedOpacity(time);
        Nodes.Add(node);
        PublishSnapshot();
        return node;
    }

    /// <summary>
    /// Relative volume a newly inserted node should take: the previous node's volume, or the
    /// start of the curve (first/EOF held value) when the insert is before every existing node.
    /// </summary>
    /// <param name="fileSeconds">File time of the insert.</param>
    /// <returns>Clamped linear relative gain.</returns>
    public float GetInheritedVolume(double fileSeconds)
    {
        if (!TryGetNeighbors(fileSeconds, out var prev, out var next, out _)
            || (prev == null && next == null))
            return AudioTimelineNode.DefaultVolumeLinear;
        if (prev != null)
            return prev.VolumeLinear;
        return next != null ? next.VolumeLinear : AudioTimelineNode.DefaultVolumeLinear;
    }

    /// <summary>
    /// Relative opacity a newly inserted node should take: the previous node's opacity, or the
    /// start of the curve when the insert is before every existing node.
    /// </summary>
    /// <param name="fileSeconds">File time of the insert.</param>
    /// <returns>Clamped relative opacity scale.</returns>
    public float GetInheritedOpacity(double fileSeconds)
    {
        if (!TryGetNeighbors(fileSeconds, out var prev, out var next, out _)
            || (prev == null && next == null))
            return AudioTimelineNode.DefaultOpacityScale;
        if (prev != null)
            return prev.OpacityScale;
        return next != null ? next.OpacityScale : AudioTimelineNode.DefaultOpacityScale;
    }

    /// <summary>
    /// Removes the marker with the given id. The locked end-of-file node cannot be removed.
    /// </summary>
    /// <param name="id">Stable node id.</param>
    /// <returns>True when a node was removed.</returns>
    public bool RemoveNode(int id)
    {
        var node = FindNode(id);
        if (node == null || node.IsFileEnd)
            return false;
        bool removed = Nodes.RemoveAll(n => n != null && n.Id == id) > 0;
        if (removed)
            PublishSnapshot();
        return removed;
    }

    /// <summary>
    /// Finds a marker by stable id.
    /// </summary>
    /// <param name="id">Stable node id.</param>
    /// <returns>The node, or null.</returns>
    public AudioTimelineNode FindNode(int id)
    {
        return Nodes.Find(n => n != null && n.Id == id);
    }

    /// <summary>
    /// Removes every user-placed timeline marker and restores the locked end-of-file node.
    /// </summary>
    public void ClearNodes()
    {
        Nodes.Clear();
        _nextNodeId = 1;
        EnsureFileEndNode();
        PublishSnapshot();
    }

    /// <summary>
    /// How many user-placed markers are on this timeline (excludes the end-of-file node).
    /// </summary>
    /// <returns>Count of movable nodes.</returns>
    public int CountUserNodes()
    {
        int count = 0;
        foreach (var node in Nodes)
        {
            if (node != null && !node.IsFileEnd)
                count++;
        }

        return count;
    }

    /// <summary>
    /// Ensures a locked node exists at the file duration. No-op until file duration is known.
    /// </summary>
    public void EnsureFileEndNode()
    {
        if (!_host.TimelineHasMetadata)
            return;

        double fileDuration = _host.TimelineFileDuration;
        if (fileDuration < 0)
            fileDuration = 0;

        AudioTimelineNode eof = null;
        bool changed = false;
        for (int i = Nodes.Count - 1; i >= 0; i--)
        {
            var node = Nodes[i];
            if (node == null)
            {
                Nodes.RemoveAt(i);
                changed = true;
                continue;
            }

            if (!node.IsFileEnd)
                continue;
            if (eof == null)
            {
                eof = node;
            }
            else
            {
                Nodes.RemoveAt(i);
                changed = true;
            }
        }

        if (eof == null)
        {
            Nodes.Add(AudioTimelineNode.Create(_nextNodeId++, fileDuration, isFileEnd: true));
            PublishSnapshot();
            return;
        }

        eof.TimeSeconds = fileDuration;
        eof.IsFileEnd = true;
        if (changed)
            PublishSnapshot();
    }

    /// <summary>
    /// Clamps every marker into the current file duration and pins the end-of-file node.
    /// </summary>
    public void ClampAllNodes()
    {
        EnsureFileEndNode();
        foreach (var node in Nodes)
        {
            if (node == null) continue;
            node.TimeSeconds = ClampNodeTime(node.TimeSeconds, node.IsFileEnd);
        }
        PublishSnapshot();
    }

    /// <summary>
    /// Replaces the playback snapshot with a sorted copy of <see cref="Nodes"/>. Main thread.
    /// </summary>
    private void PublishSnapshot()
    {
        var copy = new List<AudioTimelineNode>(Nodes.Count);
        foreach (var node in Nodes)
        {
            if (node != null)
                copy.Add(node);
        }

        var ordered = copy.ToArray();
        System.Array.Sort(ordered, CompareByTimeThenId);
        _snapshot = ordered;
    }

    /// <summary>
    /// The published snapshot in time order. Node times can move after publishing (a drag
    /// changes a node in place), so when the array is no longer sorted a sorted copy is
    /// returned instead. Safe on any thread; never modifies shared state.
    /// </summary>
    private AudioTimelineNode[] GetOrderedSnapshot()
    {
        var snapshot = _snapshot;
        for (int i = 1; i < snapshot.Length; i++)
        {
            if (CompareByTimeThenId(snapshot[i - 1], snapshot[i]) > 0)
            {
                var sorted = (AudioTimelineNode[])snapshot.Clone();
                System.Array.Sort(sorted, CompareByTimeThenId);
                return sorted;
            }
        }

        return snapshot;
    }

    private static int CompareByTimeThenId(AudioTimelineNode a, AudioTimelineNode b)
    {
        int byTime = a.TimeSeconds.CompareTo(b.TimeSeconds);
        return byTime != 0 ? byTime : a.Id.CompareTo(b.Id);
    }

    private static float RateScaleAt(AudioTimelineNode prev, AudioTimelineNode next, double mid)
    {
        if (prev == null && next == null)
            return AudioTimelineNode.DefaultRateScale;
        if (prev == null)
            return next.RateScale;
        if (next == null)
            return prev.RateScale;
        double pairSpan = next.TimeSeconds - prev.TimeSeconds;
        float t = pairSpan <= 1e-9 ? 1f : (float)((mid - prev.TimeSeconds) / pairSpan);
        return TimelineVolume.InterpolateRateScale(
            next.VolumeInterpolation, prev.RateScale, next.RateScale, t);
    }

    /// <summary>
    /// Finds the previous and next timeline nodes around a file time, and the mix t in 0–1.
    /// </summary>
    /// <param name="fileSeconds">Position on the file, in seconds.</param>
    /// <param name="prev">Nearest node strictly before the time, or null.</param>
    /// <param name="next">Nearest node at or after the time, or null.</param>
    /// <param name="t">Progress from prev to next; 1 when the span is empty.</param>
    /// <returns>False when there are no nodes.</returns>
    private bool TryGetNeighbors(
        double fileSeconds,
        out AudioTimelineNode prev,
        out AudioTimelineNode next,
        out float t)
    {
        prev = null;
        next = null;
        t = 1f;
        bool any = false;
        // Snapshot, not Nodes: this runs on the audio fill threads.
        foreach (var node in _snapshot)
        {
            if (node == null)
                continue;
            any = true;
            if (node.TimeSeconds < fileSeconds)
            {
                if (prev == null
                    || node.TimeSeconds > prev.TimeSeconds
                    || (node.TimeSeconds == prev.TimeSeconds && node.Id > prev.Id))
                    prev = node;
            }
            else if (next == null
                     || node.TimeSeconds < next.TimeSeconds
                     || (node.TimeSeconds == next.TimeSeconds && node.Id < next.Id))
            {
                next = node;
            }
        }

        if (!any)
            return false;
        if (prev == null || next == null)
            return true;

        double span = next.TimeSeconds - prev.TimeSeconds;
        t = span <= 1e-9 ? 1f : (float)((fileSeconds - prev.TimeSeconds) / span);
        return true;
    }
}
