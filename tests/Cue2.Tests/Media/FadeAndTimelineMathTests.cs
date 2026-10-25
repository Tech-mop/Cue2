// SPDX-FileCopyrightText: 2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System;
using NUnit.Framework;
using Cue2.Domain.Cues;

namespace Cue2.Tests.Media;

/// <summary>
/// Fade curves and timeline interpolation: the shapes that become audible gain.
/// </summary>
[TestFixture]
public class FadeAndTimelineMathTests
{
    private static readonly FadeCurveType[] Curves = FadeCurve.All;

    /// <summary>Every curve starts at silence, ends at full level and never goes backwards.</summary>
    [TestCaseSource(nameof(Curves))]
    public void FadeCurveRunsFromZeroToOneWithoutDipping(FadeCurveType type)
    {
        Assert.That(FadeCurve.Evaluate(0f, type), Is.EqualTo(0f).Within(1e-6f));
        Assert.That(FadeCurve.Evaluate(1f, type), Is.EqualTo(1f).Within(1e-5f));
        float previous = -1f;
        for (int i = 0; i <= 1000; i++)
        {
            float v = FadeCurve.Evaluate(i / 1000f, type);
            Assert.That(v, Is.GreaterThanOrEqualTo(previous - 1e-6f), $"{type} at {i / 1000f}");
            Assert.That(v, Is.InRange(0f, 1f + 1e-5f));
            previous = v;
        }
    }

    /// <summary>Progress outside 0..1 is clamped, so an overrun fade never overshoots.</summary>
    [TestCaseSource(nameof(Curves))]
    public void FadeCurveClampsProgress(FadeCurveType type)
    {
        Assert.That(FadeCurve.Evaluate(-0.5f, type), Is.EqualTo(FadeCurve.Evaluate(0f, type)));
        Assert.That(FadeCurve.Evaluate(1.5f, type), Is.EqualTo(FadeCurve.Evaluate(1f, type)));
    }

    /// <summary>Shapes differ in the middle the way their names say.</summary>
    [Test]
    public void FadeCurveShapesDifferAtMidpoint()
    {
        Assert.That(FadeCurve.Evaluate(0.5f, FadeCurveType.Linear), Is.EqualTo(0.5f).Within(1e-6f));
        Assert.That(FadeCurve.Evaluate(0.5f, FadeCurveType.SCurve), Is.EqualTo(0.5f).Within(1e-6f));
        Assert.That(FadeCurve.Evaluate(0.5f, FadeCurveType.Exponential), Is.LessThan(0.5f));
        Assert.That(FadeCurve.Evaluate(0.5f, FadeCurveType.Logarithmic), Is.GreaterThan(0.5f));
        Assert.That(FadeCurve.Evaluate(0.25f, FadeCurveType.SCurve), Is.LessThan(0.25f));
    }

    /// <summary>Stored integers map back to a curve; unknown values fall back to linear.</summary>
    [Test]
    public void FadeCurveFromIntRoundTripsAndFallsBack()
    {
        foreach (var type in Curves)
            Assert.That(FadeCurve.FromInt((int)type), Is.EqualTo(type));
        Assert.That(FadeCurve.FromInt(999), Is.EqualTo(FadeCurveType.Linear));
        Assert.That(FadeCurve.FromInt(-1), Is.EqualTo(FadeCurveType.Linear));
    }

    /// <summary>Snap holds until any progress, Bezier eases, Linear is straight.</summary>
    [Test]
    public void TimelineShapes()
    {
        Assert.That(TimelineVolume.Shape(TimelineVolumeInterpolation.Snap, 0f), Is.EqualTo(0f));
        Assert.That(TimelineVolume.Shape(TimelineVolumeInterpolation.Snap, 0.001f), Is.EqualTo(1f));
        Assert.That(TimelineVolume.Shape(TimelineVolumeInterpolation.Linear, 0.3f), Is.EqualTo(0.3f).Within(1e-6f));
        Assert.That(TimelineVolume.Shape(TimelineVolumeInterpolation.Bezier, 0.5f), Is.EqualTo(0.5f).Within(1e-6f));
        Assert.That(TimelineVolume.Shape(TimelineVolumeInterpolation.Bezier, 0.1f), Is.LessThan(0.1f));
        Assert.That(TimelineVolume.Shape(TimelineVolumeInterpolation.Linear, 2f), Is.EqualTo(1f));
    }

    /// <summary>Volume interpolates in dB, so halfway between 0 dB and -20 dB is -10 dB.</summary>
    [Test]
    public void TimelineVolumeInterpolatesInDecibels()
    {
        float from = 1f;
        float to = 0.1f;
        float mid = TimelineVolume.InterpolateVolumeLinear(TimelineVolumeInterpolation.Linear, from, to, 0.5f);
        Assert.That(20f * MathF.Log10(mid), Is.EqualTo(-10f).Within(0.01f));
        Assert.That(TimelineVolume.InterpolateVolumeLinear(TimelineVolumeInterpolation.Linear, from, to, 0f), Is.EqualTo(from));
        Assert.That(TimelineVolume.InterpolateVolumeLinear(TimelineVolumeInterpolation.Linear, from, to, 1f), Is.EqualTo(to).Within(1e-6f));
    }

    /// <summary>Bad node volumes are made safe rather than passed to the mixer.</summary>
    [Test]
    public void TimelineVolumeClampsBadValues()
    {
        Assert.That(AudioTimelineNode.ClampVolumeLinear(float.NaN), Is.EqualTo(AudioTimelineNode.ClampVolumeLinear(1f)));
        Assert.That(AudioTimelineNode.ClampVolumeLinear(-3f), Is.EqualTo(0f));
        Assert.That(AudioTimelineNode.ClampVolumeLinear(100f), Is.EqualTo(Cue2.Media.Audio.AudioMixMatrix.MaxComponentGainLinear));
    }

    /// <summary>Pitch interpolates linearly in cents between nodes.</summary>
    [Test]
    public void TimelinePitchInterpolatesInCents()
    {
        Assert.That(TimelineVolume.InterpolatePitchCents(TimelineVolumeInterpolation.Linear, 0f, 100f, 0.25f), Is.EqualTo(25f).Within(1e-4f));
        Assert.That(TimelineVolume.InterpolatePitchCents(TimelineVolumeInterpolation.Snap, 0f, 100f, 0.25f), Is.EqualTo(100f).Within(1e-4f));
    }
}
