// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System;
using Godot;
using Godot.Collections;
using Cue2.Media.Audio;

namespace Cue2.Domain.Cues;

/// <summary>
/// A numbered marker on an audio file timeline, stored in file seconds.
/// Display numbers are 1-based after sorting by <see cref="TimeSeconds"/>.
/// Loop and play count apply to the region <b>before</b> this node when
/// <see cref="ContinueRegion"/> is false (from the previous non-continue node, or the in-point).
/// Volume, rate, and pitch are relative to the component and interpolate into this node.
/// </summary>
public class AudioTimelineNode
{
    /// <summary>Default relative volume (0 dB vs the component).</summary>
    public const float DefaultVolumeLinear = 1f;

    /// <summary>Default relative play-rate scale (1 = no change vs the component).</summary>
    public const float DefaultRateScale = 1f;

    /// <summary>Default relative pitch offset in cents (0 = no change vs the component).</summary>
    public const float DefaultPitchCents = 0f;

    /// <summary>Default plays of the region before this node.</summary>
    public const int DefaultPlayCount = 1;

    /// <summary>
    /// Stable identity across reorder, drag, and undo.
    /// </summary>
    /// <value>Positive integer unique on the parent <see cref="AudioComponent"/>.</value>
    public int Id { get; set; }

    /// <summary>
    /// Position on the audio file, in seconds.
    /// </summary>
    /// <value>Clamped to the file duration by the parent component. Locked when <see cref="IsFileEnd"/>.</value>
    public double TimeSeconds { get; set; }

    /// <summary>
    /// When true, this marker is the locked end-of-file node and cannot be moved or deleted.
    /// </summary>
    public bool IsFileEnd { get; set; }

    /// <summary>
    /// When true, playback continues through this node without using it as a loop bound
    /// (volume, rate, and pitch only). Loop and play count are hidden in the inspector. Default true.
    /// </summary>
    public bool ContinueRegion { get; set; } = true;

    /// <summary>
    /// When true, the region before this node repeats until the cue is stopped.
    /// Overrules <see cref="PlayCount"/>. Ignored when <see cref="ContinueRegion"/>.
    /// </summary>
    public bool Loop { get; set; }

    /// <summary>
    /// How many times to play the region before this node (ignored when <see cref="Loop"/>
    /// or <see cref="ContinueRegion"/>).
    /// </summary>
    /// <value>Integer ≥ 1. Default 1.</value>
    public int PlayCount
    {
        get => _playCount;
        set => _playCount = value < 1 ? DefaultPlayCount : value;
    }
    private int _playCount = DefaultPlayCount;

    /// <summary>
    /// Linear gain at this node, multiplied with the component volume.
    /// 1 is 0 dB relative (no change).
    /// </summary>
    /// <value>Clamped to the component gain range (silence … +12 dB).</value>
    public float VolumeLinear
    {
        get => _volumeLinear;
        set => _volumeLinear = ClampVolumeLinear(value);
    }
    private float _volumeLinear = DefaultVolumeLinear;

    /// <summary>
    /// Play-rate scale at this node, multiplied with the component <see cref="AudioComponent.PlayRate"/>.
    /// 1 is no change.
    /// </summary>
    /// <value>Clamped to <see cref="AudioComponent.MinPlayRate"/> … <see cref="AudioComponent.MaxPlayRate"/>.</value>
    public float RateScale
    {
        get => _rateScale;
        set => _rateScale = (float)AudioComponent.ClampPlayRate(value);
    }
    private float _rateScale = DefaultRateScale;

    /// <summary>
    /// Pitch offset at this node in cents, added to the component <see cref="AudioComponent.PitchCents"/>.
    /// 0 is no change.
    /// </summary>
    /// <value>Clamped to <see cref="AudioComponent.MinPitchCents"/> … <see cref="AudioComponent.MaxPitchCents"/>.</value>
    public float PitchCents
    {
        get => _pitchCents;
        set => _pitchCents = AudioComponent.ClampPitchCents(value);
    }
    private float _pitchCents = DefaultPitchCents;

    /// <summary>
    /// How volume, rate, and pitch travel from the previous node to this one.
    /// Volume interpolates in dB, rate in log scale, pitch in cents.
    /// The span before the first node stays at that node's values.
    /// </summary>
    public TimelineVolumeInterpolation VolumeInterpolation { get; set; } =
        TimelineVolumeInterpolation.Linear;

    /// <summary>
    /// Creates a node with default continue/automation values.
    /// </summary>
    /// <param name="id">Stable id unique on the parent component.</param>
    /// <param name="timeSeconds">File time in seconds.</param>
    /// <param name="isFileEnd">When true, this is the locked end-of-file node.</param>
    /// <returns>A new node ready to add to <see cref="AudioComponent.TimelineNodes"/>.</returns>
    public static AudioTimelineNode Create(int id, double timeSeconds, bool isFileEnd = false)
    {
        return new AudioTimelineNode
        {
            Id = id,
            TimeSeconds = timeSeconds,
            IsFileEnd = isFileEnd,
            ContinueRegion = true,
            Loop = false,
            PlayCount = DefaultPlayCount,
            VolumeLinear = DefaultVolumeLinear,
            RateScale = DefaultRateScale,
            PitchCents = DefaultPitchCents,
            VolumeInterpolation = TimelineVolumeInterpolation.Linear
        };
    }

    /// <summary>
    /// Clamps a relative linear gain into the component volume range.
    /// </summary>
    /// <param name="linear">Requested relative gain.</param>
    /// <returns>Clamped linear gain.</returns>
    public static float ClampVolumeLinear(float linear)
    {
        if (float.IsNaN(linear) || float.IsInfinity(linear))
            return DefaultVolumeLinear;
        return AudioMixMatrix.ClampComponentGainLinear(linear);
    }

    /// <summary>
    /// Serializes this node for showfiles and history.
    /// </summary>
    /// <returns>A dictionary with id, time, continue, loop, play count, and relative automation.</returns>
    public Dictionary GetData()
    {
        return new Dictionary
        {
            { "Id", Id },
            { "TimeSeconds", TimeSeconds },
            { "IsFileEnd", IsFileEnd },
            { "ContinueRegion", ContinueRegion },
            { "Loop", Loop },
            { "PlayCount", PlayCount },
            { "VolumeLinear", VolumeLinear },
            { "RateScale", RateScale },
            { "PitchCents", PitchCents },
            { "VolumeInterpolation", (int)VolumeInterpolation }
        };
    }

    /// <summary>
    /// Reconstructs a node from serialized data.
    /// </summary>
    /// <param name="data">Dictionary previously produced by <see cref="GetData"/>.</param>
    /// <returns>The node, or null when the payload is missing required fields.</returns>
    public static AudioTimelineNode FromData(Dictionary data)
    {
        if (data == null)
            return null;

        int id = data.ContainsKey("Id") ? data["Id"].AsInt32() : 0;
        if (id <= 0)
            return null;

        return new AudioTimelineNode
        {
            Id = id,
            TimeSeconds = data.ContainsKey("TimeSeconds") ? data["TimeSeconds"].AsDouble() : 0.0,
            IsFileEnd = data.ContainsKey("IsFileEnd") && data["IsFileEnd"].AsBool(),
            ContinueRegion = ReadContinueRegion(data),
            Loop = data.ContainsKey("Loop") && data["Loop"].AsBool(),
            PlayCount = data.ContainsKey("PlayCount")
                ? Math.Max(DefaultPlayCount, data["PlayCount"].AsInt32())
                : DefaultPlayCount,
            VolumeLinear = data.ContainsKey("VolumeLinear")
                ? data["VolumeLinear"].AsSingle()
                : DefaultVolumeLinear,
            RateScale = data.ContainsKey("RateScale")
                ? data["RateScale"].AsSingle()
                : DefaultRateScale,
            PitchCents = data.ContainsKey("PitchCents")
                ? data["PitchCents"].AsSingle()
                : DefaultPitchCents,
            VolumeInterpolation = data.ContainsKey("VolumeInterpolation")
                ? TimelineVolume.FromInt(data["VolumeInterpolation"].AsInt32())
                : TimelineVolumeInterpolation.Linear
        };
    }

    private static bool ReadContinueRegion(Dictionary data)
    {
        if (data.ContainsKey("ContinueRegion"))
            return data["ContinueRegion"].AsBool();
        if (data.ContainsKey("BypassRegion"))
            return data["BypassRegion"].AsBool();
        return true;
    }
}
