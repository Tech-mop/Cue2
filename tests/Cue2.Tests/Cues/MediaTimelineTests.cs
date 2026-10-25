// SPDX-FileCopyrightText: 2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System;
using System.Threading;
using NUnit.Framework;
using Cue2.Domain.Cues;

namespace Cue2.Tests.Cues;

/// <summary>
/// The shared media timeline: nodes, loop regions, curves and trims.
/// </summary>
[TestFixture]
public class MediaTimelineTests
{
    private sealed class Host : IMediaTimelineHost
    {
        public double TimelineFileDuration { get; set; } = 10;
        public double TimelineStartTime { get; set; }
        public double TimelineEndTime { get; set; } = -1;
        public double TimelinePlayRate { get; set; } = 1;
        public float TimelinePitchCents { get; set; }
        public bool TimelineHasMetadata { get; set; } = true;
        public double ClampTimelineStartTime(double s) => Math.Clamp(s, 0, TimelineFileDuration);
    }

    /// <summary>With no markers the whole file plays once and the curve is flat.</summary>
    [Test]
    public void EmptyTimelinePlaysWholeFile()
    {
        var timeline = new MediaTimeline(new Host());
        var regions = timeline.GetPlaybackRegions();
        Assert.That(regions.Count, Is.EqualTo(1));
        Assert.That(regions[0].StartSeconds, Is.EqualTo(0));
        Assert.That(regions[0].EndSeconds, Is.EqualTo(10));
        Assert.That(timeline.ComputeSequenceWallClock(false), Is.EqualTo(10).Within(1e-6));
        Assert.That(timeline.EvaluateTimelineVolumeLinear(5), Is.EqualTo(1f));
    }

    /// <summary>A loop-bound marker owns the region before it; its play count multiplies the length.</summary>
    [Test]
    public void LoopBoundMarkerMakesARegion()
    {
        var timeline = new MediaTimeline(new Host());
        var node = timeline.AddNode(4.0);
        node.ContinueRegion = false;
        node.PlayCount = 2;

        var regions = timeline.GetPlaybackRegions();
        Assert.That(regions.Count, Is.EqualTo(2));
        Assert.That(regions[0].NodeId, Is.EqualTo(node.Id));
        Assert.That(regions[0].EndSeconds, Is.EqualTo(4));
        Assert.That(regions[0].PlayCount, Is.EqualTo(2));
        Assert.That(regions[1].StartSeconds, Is.EqualTo(4));
        Assert.That(timeline.ComputeSequenceWallClock(false), Is.EqualTo(14).Within(1e-6));
        Assert.That(timeline.WallClockAtFileTime(6), Is.EqualTo(10).Within(1e-6), "after two plays of the first region");
    }

    /// <summary>A looping region has no finite length unless counted as one play.</summary>
    [Test]
    public void LoopingRegionLength()
    {
        var timeline = new MediaTimeline(new Host());
        var node = timeline.AddNode(4.0);
        node.ContinueRegion = false;
        node.Loop = true;
        Assert.That(timeline.ComputeSequenceWallClock(false), Is.EqualTo(-1));
        Assert.That(timeline.ComputeSequenceWallClock(true), Is.EqualTo(10).Within(1e-6));
    }

    /// <summary>In and out points clip the regions.</summary>
    [Test]
    public void TrimsClipRegions()
    {
        var host = new Host { TimelineStartTime = 2, TimelineEndTime = 8 };
        var timeline = new MediaTimeline(host);
        var node = timeline.AddNode(5.0);
        node.ContinueRegion = false;

        var regions = timeline.GetPlaybackRegions();
        Assert.That(regions.Count, Is.EqualTo(2));
        Assert.That(regions[0].StartSeconds, Is.EqualTo(2));
        Assert.That(regions[1].EndSeconds, Is.EqualTo(8));
        Assert.That(timeline.ComputeSequenceWallClock(false), Is.EqualTo(6).Within(1e-6));
    }

    /// <summary>Volume interpolates between markers in dB and holds before and after them.</summary>
    [Test]
    public void VolumeCurveBetweenMarkers()
    {
        var timeline = new MediaTimeline(new Host());
        var a = timeline.AddNode(2.0, volumeLinear: 1f);
        var b = timeline.AddNode(4.0, volumeLinear: 0.1f);
        b.VolumeInterpolation = TimelineVolumeInterpolation.Linear;
        timeline.ClampAllNodes();

        Assert.That(timeline.EvaluateTimelineVolumeLinear(1.0), Is.EqualTo(a.VolumeLinear));
        float mid = timeline.EvaluateTimelineVolumeLinear(3.0);
        Assert.That(20 * Math.Log10(mid), Is.EqualTo(-10).Within(0.05));
    }

    /// <summary>A rate scale on a marker changes how long the file takes to play.</summary>
    [Test]
    public void RateCurveChangesWallClock()
    {
        var timeline = new MediaTimeline(new Host());
        timeline.AddNode(5.0);
        foreach (var node in timeline.Nodes)
            node.RateScale = 2f;
        timeline.ClampAllNodes();
        Assert.That(timeline.ComputeSequenceWallClock(false), Is.EqualTo(5).Within(0.05));
    }

    /// <summary>Markers stay inside the file; the end marker cannot be removed; the cap is enforced.</summary>
    [Test]
    public void MarkerLimits()
    {
        var timeline = new MediaTimeline(new Host());
        var late = timeline.AddNode(50);
        Assert.That(late.TimeSeconds, Is.LessThan(10));
        var end = timeline.FindNode(timeline.GetNodesInTimeOrder()[^1].Id);
        Assert.That(end.IsFileEnd, Is.True);
        Assert.That(timeline.RemoveNode(end.Id), Is.False);
        Assert.That(timeline.RemoveNode(late.Id), Is.True);

        for (int i = 0; i < MediaTimeline.MaxUserNodes; i++)
            Assert.That(timeline.AddNode(0.1 + i * 0.2), Is.Not.Null);
        Assert.That(timeline.AddNode(9), Is.Null);
        timeline.ClearNodes();
        Assert.That(timeline.CountUserNodes(), Is.EqualTo(0));
    }

    /// <summary>
    /// Playback threads read the timeline while the inspector edits it. Reads must never throw
    /// (a throw used to end the audio fill loop and silence the cue).
    /// </summary>
    [Test]
    public void ReadsWhileEditingNeverThrow()
    {
        var timeline = new MediaTimeline(new Host());
        Exception failure = null;
        using var stop = new CancellationTokenSource();
        var reader = new Thread(() =>
        {
            try
            {
                double t = 0;
                while (!stop.IsCancellationRequested)
                {
                    timeline.EvaluateTimeline(t);
                    timeline.GetPlaybackRegions();
                    timeline.IntegrateTimelineWallClock(0, 10);
                    t = (t + 0.37) % 10;
                }
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        reader.Start();

        var random = new Random(7);
        var deadline = DateTime.UtcNow.AddMilliseconds(500);
        while (DateTime.UtcNow < deadline && failure == null)
        {
            var node = timeline.AddNode(random.NextDouble() * 10);
            if (node != null)
                node.ContinueRegion = random.Next(2) == 0;
            if (timeline.CountUserNodes() > 20)
                timeline.ClearNodes();
            timeline.ClampAllNodes();
        }

        stop.Cancel();
        reader.Join();
        Assert.That(failure, Is.Null, failure?.ToString());
    }
}
