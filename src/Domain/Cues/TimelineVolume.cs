// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System;
using Cue2.Media.Audio;

namespace Cue2.Domain.Cues;

/// <summary>
/// Evaluates <see cref="TimelineVolumeInterpolation"/> shapes for volume (dB), rate (log), and pitch (cents).
/// </summary>
public static class TimelineVolume
{
    /// <summary>Inspector order for the interpolation OptionButton.</summary>
    public static readonly TimelineVolumeInterpolation[] All =
    {
        TimelineVolumeInterpolation.Linear,
        TimelineVolumeInterpolation.Snap,
        TimelineVolumeInterpolation.Bezier
    };

    /// <summary>
    /// Maps normalized span progress 0–1 to a 0–1 mix toward the destination node.
    /// </summary>
    /// <param name="mode">Interpolation shape.</param>
    /// <param name="t">Progress from the previous node to the next, 0–1.</param>
    /// <returns>0 at the previous node, 1 at the destination (Snap is 1 for any t &gt; 0).</returns>
    public static float Shape(TimelineVolumeInterpolation mode, float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        return mode switch
        {
            TimelineVolumeInterpolation.Snap => t <= 0f ? 0f : 1f,
            TimelineVolumeInterpolation.Bezier => t * t * (3f - 2f * t),
            _ => t
        };
    }

    /// <summary>
    /// Interpolates relative linear gain in dB space (matches the waveform volume line).
    /// </summary>
    /// <param name="mode">Interpolation shape.</param>
    /// <param name="fromLinear">Volume at the previous node.</param>
    /// <param name="toLinear">Volume at the destination node.</param>
    /// <param name="t">Progress 0–1 along the span.</param>
    /// <returns>Clamped linear relative gain.</returns>
    public static float InterpolateVolumeLinear(
        TimelineVolumeInterpolation mode, float fromLinear, float toLinear, float t)
    {
        float u = Shape(mode, t);
        if (u <= 0f)
            return AudioTimelineNode.ClampVolumeLinear(fromLinear);
        if (u >= 1f)
            return AudioTimelineNode.ClampVolumeLinear(toLinear);

        float db0 = LinearToDb(fromLinear);
        float db1 = LinearToDb(toLinear);
        return AudioTimelineNode.ClampVolumeLinear(DbToLinear(db0 + (db1 - db0) * u));
    }

    /// <summary>
    /// Interpolates relative play-rate scale in log space (matches the waveform rate line).
    /// </summary>
    /// <param name="mode">Interpolation shape.</param>
    /// <param name="fromScale">Rate scale at the previous node.</param>
    /// <param name="toScale">Rate scale at the destination node.</param>
    /// <param name="t">Progress 0–1 along the span.</param>
    /// <returns>Clamped relative rate scale.</returns>
    public static float InterpolateRateScale(
        TimelineVolumeInterpolation mode, float fromScale, float toScale, float t)
    {
        float u = Shape(mode, t);
        double a = AudioComponent.ClampPlayRate(fromScale);
        double b = AudioComponent.ClampPlayRate(toScale);
        if (u <= 0f)
            return (float)a;
        if (u >= 1f)
            return (float)b;

        double logA = Math.Log(a);
        double logB = Math.Log(b);
        return (float)AudioComponent.ClampPlayRate(Math.Exp(logA + (logB - logA) * u));
    }

    /// <summary>
    /// Interpolates relative pitch offset in cents (matches the waveform pitch line).
    /// </summary>
    /// <param name="mode">Interpolation shape.</param>
    /// <param name="fromCents">Pitch offset at the previous node.</param>
    /// <param name="toCents">Pitch offset at the destination node.</param>
    /// <param name="t">Progress 0–1 along the span.</param>
    /// <returns>Clamped relative pitch in cents.</returns>
    public static float InterpolatePitchCents(
        TimelineVolumeInterpolation mode, float fromCents, float toCents, float t)
    {
        float u = Shape(mode, t);
        float a = AudioComponent.ClampPitchCents(fromCents);
        float b = AudioComponent.ClampPitchCents(toCents);
        if (u <= 0f)
            return a;
        if (u >= 1f)
            return b;
        return AudioComponent.ClampPitchCents(a + (b - a) * u);
    }

    /// <summary>English caption for the inspector OptionButton.</summary>
    /// <param name="mode">Interpolation shape.</param>
    /// <returns>English caption.</returns>
    public static string DisplayName(TimelineVolumeInterpolation mode) => mode switch
    {
        TimelineVolumeInterpolation.Snap => "Snap",
        TimelineVolumeInterpolation.Bezier => "Bezier",
        _ => "Linear"
    };

    /// <summary>English tooltip for an interpolation OptionButton item.</summary>
    /// <param name="mode">Interpolation shape.</param>
    /// <returns>English tooltip.</returns>
    public static string Tooltip(TimelineVolumeInterpolation mode) => mode switch
    {
        TimelineVolumeInterpolation.Snap => "Hold this node's volume, rate, and pitch for the whole span from the previous node (step).",
        TimelineVolumeInterpolation.Bezier => "Ease in and out from the previous node to this one.",
        _ => "Straight ramp from the previous node to this one."
    };

    /// <summary>
    /// Parses a stored integer; unknown values become <see cref="TimelineVolumeInterpolation.Linear"/>.
    /// </summary>
    /// <param name="value">Serialized enum integer.</param>
    /// <returns>A defined interpolation mode.</returns>
    public static TimelineVolumeInterpolation FromInt(int value)
    {
        foreach (var mode in All)
        {
            if ((int)mode == value)
                return mode;
        }

        return TimelineVolumeInterpolation.Linear;
    }

    private static float LinearToDb(float linear)
    {
        linear = AudioMixMatrix.ClampComponentGainLinear(linear);
        if (linear <= 1e-8f)
            return AudioMixMatrix.MinVolumeDb;
        float db = 20f * MathF.Log10(linear);
        return Math.Clamp(db, AudioMixMatrix.MinVolumeDb, AudioMixMatrix.MaxComponentGainDb);
    }

    private static float DbToLinear(float db)
    {
        if (db <= AudioMixMatrix.MinVolumeDb)
            return 0f;
        if (db > AudioMixMatrix.MaxComponentGainDb)
            db = AudioMixMatrix.MaxComponentGainDb;
        return MathF.Pow(10f, db / 20f);
    }
}
