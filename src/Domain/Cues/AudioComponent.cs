// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using Godot;
using Array = Godot.Collections.Array;
using Dictionary = Godot.Collections.Dictionary;

namespace Cue2.Domain.Cues;

public class AudioComponent : ICueComponent
{
    public string Type => "Audio";
    public AudioOutputPatch Patch { get; set; }
    public int PatchId { get; set; } = -1; // This value is used to link patch whence loaded
    public string DirectOutput { get; set; }
    public string AudioFile { get; set; }

    /// <summary>
    /// Returns true when a patch or direct output device has been assigned for playback.
    /// </summary>
    public bool HasOutputAssigned =>
        Patch != null || !string.IsNullOrEmpty(DirectOutput);
    
    /// <summary>
    /// Start time, double in seconds. 
    /// </summary>
    public double StartTime { get; set; } = 0.0; // In seconds
    public double EndTime { get; set; } = -1.0; // -1 means play until end of cue
    public CuePatch Routing { get; set; }

    /// <summary>
    /// Wall-clock of one pass through the timeline regions (rate curve × each region’s play count).
    /// </summary>
    /// <value>−1 when a region loops. Component <see cref="PlayCount"/> is applied in <see cref="TotalDuration"/>.</value>
    public double Duration { get; set; } = 0.0;
    
    /// <summary>
    /// Wall-clock the audio plays, including component play count (Duration × play count).
    /// Component play count wraps the whole region sequence, including inner loop slices.
    /// </summary>
    /// <value>−1 when the component or any region loops.</value>
    public double TotalDuration { get; set; } = 0.0;
    /// <summary>
    /// Component volume (linear). Unity = 1.0 (0 dB); digital gain up to ≈3.98 (+12 dB) is allowed.
    /// </summary>
    public double Volume { get; set; } = 1.0f;

    /// <summary>
    /// Stereo pan/balance applied after volume and before the routing matrix.
    /// Range −1 (full left) … 0 (center) … +1 (full right). Only used for stereo sources.
    /// </summary>
    /// <value>Clamped to [−1, 1]. Default 0 (center / "C").</value>
    public float Pan
    {
        get => _pan;
        set => _pan = Math.Clamp(value, -1f, 1f);
    }
    private float _pan;

    public bool Loop { get; set; } = false;
    public int PlayCount { get; set; } = 1;

    /// <summary>Default playback speed (original).</summary>
    public const double DefaultPlayRate = 1.0;

    /// <summary>Slowest supported playback multiplier.</summary>
    public const double MinPlayRate = 0.1;

    /// <summary>Fastest supported playback multiplier.</summary>
    public const double MaxPlayRate = 8.0;

    /// <summary>Default extra pitch shift in cents.</summary>
    public const float DefaultPitchCents = 0f;

    /// <summary>Lowest extra pitch shift in cents (two octaves down).</summary>
    public const float MinPitchCents = -2400f;

    /// <summary>Highest extra pitch shift in cents (two octaves up).</summary>
    public const float MaxPitchCents = 2400f;

    /// <summary>
    /// Playback speed multiplier. 1 is original, 0.5 is half speed, 2 is double speed.
    /// Shortens <see cref="Duration"/> and <see cref="TotalDuration"/> by the same factor.
    /// </summary>
    public double PlayRate
    {
        get => _playRate;
        set => _playRate = ClampPlayRate(value);
    }
    private double _playRate = DefaultPlayRate;

    /// <summary>
    /// When true, time-stretch so musical pitch stays put as <see cref="PlayRate"/> changes.
    /// Extra <see cref="PitchCents"/> still apply.
    /// </summary>
    public bool KeepPitch { get; set; }

    /// <summary>
    /// Extra pitch shift in cents (100 cents = 1 semitone), independent of rate.
    /// Without <see cref="KeepPitch"/>, rate also shifts pitch (vinyl-style).
    /// </summary>
    public float PitchCents
    {
        get => _pitchCents;
        set => _pitchCents = ClampPitchCents(value);
    }
    private float _pitchCents = DefaultPitchCents;

    /// <summary>
    /// Clamps a play-rate multiplier into the supported range.
    /// </summary>
    public static double ClampPlayRate(double rate)
    {
        if (double.IsNaN(rate) || double.IsInfinity(rate))
            return DefaultPlayRate;
        return Math.Clamp(rate, MinPlayRate, MaxPlayRate);
    }

    /// <summary>
    /// Clamps a pitch shift in cents into the supported range.
    /// </summary>
    public static float ClampPitchCents(float cents)
    {
        if (float.IsNaN(cents) || float.IsInfinity(cents))
            return DefaultPitchCents;
        return Math.Clamp(cents, MinPitchCents, MaxPitchCents);
    }
    
    public double FadeInDuration { get; set; } = 0.0; // In seconds

    /// <summary>
    /// Amplitude shape used for <see cref="FadeInDuration"/>.
    /// </summary>
    public FadeCurveType FadeInCurve { get; set; } = FadeCurveType.Linear;

    public double FadeOutDuration { get; set; } = 0.0; // In seconds

    /// <summary>
    /// Amplitude shape used for <see cref="FadeOutDuration"/> (natural end and component fade-out).
    /// </summary>
    public FadeCurveType FadeOutCurve { get; set; } = FadeCurveType.Linear;

    /// <summary>Maximum user-placed markers on one audio timeline (plus the locked end-of-file node).</summary>
    public const int MaxTimelineNodes = 32;

    /// <summary>
    /// Numbered markers on the file timeline (file seconds). Display order is by time.
    /// </summary>
    public List<AudioTimelineNode> TimelineNodes { get; } = new List<AudioTimelineNode>();

    private int _nextTimelineNodeId = 1;

    /// <summary>
    /// In-memory peak envelope for UI display only.
    /// Not written into showfiles — peaks persist under <c>SessionDir/Waveforms/*.c2wf</c>
    /// via <see cref="Cue2.Services.MediaEngine.GenerateWaveformAsync"/>.
    /// </summary>
    public byte[] WaveformData { get; set; }
    
    /// <summary>
    /// Full metadata from file (duration, channels, sample rate, bit depth, codec, format).
    /// Set via inspector on load; used for UI/display and playback routing.
    /// </summary>
    public AudioFileMetadata Metadata { get; set; } = null;

    public Dictionary GetData()
    {
        var data = new Dictionary();
        // Prefer live Patch object id, but fall back to stored PatchId so history/save never drops routing.
        data.Add("PatchId", Patch?.Id ?? PatchId);
        data.Add("DirectOutput", DirectOutput ?? string.Empty);
        data.Add("AudioFile", AudioFile);
        data.Add("StartTime", StartTime);
        data.Add("EndTime", EndTime);
        data.Add("Duration", Duration);
        data.Add("Loop", Loop);
        data.Add("Volume", Volume);
        data.Add("Pan", Pan);
        data.Add("PlayCount", PlayCount);
        data.Add("PlayRate", PlayRate);
        data.Add("KeepPitch", KeepPitch);
        data.Add("PitchCents", PitchCents);
        data.Add("FadeInDuration", FadeInDuration);
        data.Add("FadeInCurve", (int)FadeInCurve);
        data.Add("FadeOutDuration", FadeOutDuration);
        data.Add("FadeOutCurve", (int)FadeOutCurve);
        if (Routing != null)
        {
            data.Add("Routing", Routing.GetData());
        }

        if (TimelineNodes.Count > 0)
        {
            var nodes = new Array();
            foreach (var node in TimelineNodes)
            {
                if (node == null) continue;
                nodes.Add(node.GetData());
            }
            data.Add("TimelineNodes", nodes);
        }
        // Waveform peaks are session disk-cache only (Waveforms/*.c2wf), not showfile payload.

        if (Metadata != null) 
        { 
            var metaDict = new Dictionary(); 
            metaDict.Add("Duration", Metadata.Duration); 
            metaDict.Add("Channels", Metadata.Channels); 
            metaDict.Add("SampleRate", Metadata.SampleRate); 
            metaDict.Add("BitDepth", Metadata.BitDepth); 
            metaDict.Add("Codec", Metadata.Codec); 
            metaDict.Add("Format", Metadata.Format); 
            data.Add("Metadata", metaDict); 
        }
        
        
        return data;
    }

    /// <summary>
    /// Clamps a proposed start time into the valid range for this component's media.
    /// Never negative; never greater than the file duration when metadata is known.
    /// </summary>
    /// <param name="proposedSeconds">Requested start time in seconds.</param>
    /// <returns>Clamped start time in seconds.</returns>
    public double ClampStartTime(double proposedSeconds)
    {
        double result = Math.Max(0.0, proposedSeconds);
        double fileDuration = Metadata?.Duration ?? 0.0;
        if (fileDuration > 0.0 && result > fileDuration)
            result = fileDuration;
        return result;
    }

    public double RecalculateDuration()
    {
        // Metadata is filled asynchronously after file drop — do not NRE before it arrives
        if (Metadata == null)
        {
            Duration = 0.0;
            TotalDuration = Loop ? -1.0 : 0.0;
            return Duration;
        }

        // Keep start within file bounds so duration/playback cannot go invalid.
        StartTime = ClampStartTime(StartTime);
        EnsureFileEndNode();

        Duration = ComputeSequenceWallClock(loopingRegionsAsOnePlay: false);
        TotalDuration = Loop || Duration < 0
            ? -1.0
            : Duration * PlayCount;
        return Duration;
    }

    /// <summary>
    /// Wall-clock of one pass through <see cref="GetPlaybackRegions"/> at the live rate curve.
    /// Component <see cref="PlayCount"/> / <see cref="Loop"/> are not applied here.
    /// </summary>
    /// <param name="loopingRegionsAsOnePlay">
    /// When true, a looping region counts as one play (always finite).
    /// When false, any looping region yields −1.
    /// </param>
    /// <returns>Seconds, or −1 when a region loops and <paramref name="loopingRegionsAsOnePlay"/> is false.</returns>
    public double ComputeSequenceWallClock(bool loopingRegionsAsOnePlay)
    {
        double fileDuration = Metadata?.Duration ?? 0.0;
        if (fileDuration < 0)
            fileDuration = 0;

        double windowStart = ClampStartTime(StartTime);
        double windowEnd = EndTime < 0 ? fileDuration : EndTime;
        double windowLen = Math.Max(0.0, windowEnd - windowStart);

        var playbackRegions = GetPlaybackRegions();
        if (playbackRegions.Count == 0)
            return windowLen / Math.Max(MinPlayRate, PlayRate);

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
    /// Finite wall-clock of one display cycle: inner region play counts, looping regions as one play.
    /// Used by the timeline inspector and looping progress bars.
    /// </summary>
    /// <returns>Seconds ≥ 0. 0 when metadata is missing.</returns>
    public double GetDisplayCycleSeconds()
    {
        if (Metadata == null)
            return 0.0;
        EnsureFileEndNode();
        return Math.Max(0.0, ComputeSequenceWallClock(loopingRegionsAsOnePlay: true));
    }

    /// <summary>
    /// Progress / seek span for the active-cue bar.
    /// Finite cues use <see cref="TotalDuration"/> (component play count wraps the region sequence).
    /// Looping cues (component or region) use one <see cref="GetDisplayCycleSeconds"/> cycle.
    /// </summary>
    /// <returns>Seconds ≥ 0.</returns>
    public double GetProgressSpanSeconds()
    {
        if (Loop || TotalDuration < 0)
            return GetDisplayCycleSeconds();
        return Math.Max(0.0, TotalDuration);
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
        double fileDuration = Metadata?.Duration ?? 0.0;
        if (fileDuration < 0)
            fileDuration = 0;

        double windowStart = ClampStartTime(StartTime);
        double windowEnd = EndTime < 0 ? fileDuration : EndTime;
        if (windowEnd < windowStart)
            windowEnd = windowStart;

        const double eps = 1e-6;
        double cursor = windowStart;
        foreach (var node in GetTimelineNodesInTimeOrder())
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
        {
            regions.Add(AudioTimelineRegion.Create(0, cursor, windowEnd));
        }

        return regions;
    }

    /// <summary>
    /// Absolute volume, play rate, and pitch at a file time (one neighbor scan).
    /// </summary>
    /// <param name="fileSeconds">Position on the audio file, in seconds.</param>
    /// <returns>Clamped mix of component values and the timeline curve.</returns>
    public TimelineAutomation EvaluateTimeline(double fileSeconds) =>
        EvaluateTimeline(fileSeconds, playRateOverride: null, pitchCentsOverride: null);

    /// <summary>
    /// Absolute volume, play rate, and pitch at a file time, with optional component-rate/pitch overrides
    /// (control fades). Timeline relative scale/offset still apply.
    /// </summary>
    /// <param name="fileSeconds">Position on the audio file, in seconds.</param>
    /// <param name="playRateOverride">When set, used instead of <see cref="PlayRate"/> as the base rate.</param>
    /// <param name="pitchCentsOverride">When set, used instead of <see cref="PitchCents"/> as the base pitch.</param>
    /// <returns>Clamped mix of base values and the timeline curve.</returns>
    public TimelineAutomation EvaluateTimeline(
        double fileSeconds,
        double? playRateOverride,
        float? pitchCentsOverride)
    {
        double baseRate = playRateOverride ?? PlayRate;
        float basePitch = pitchCentsOverride ?? PitchCents;

        if (!TryGetTimelineNeighbors(fileSeconds, out var prev, out var next, out float t)
            || (prev == null && next == null))
        {
            return new TimelineAutomation
            {
                VolumeLinear = AudioTimelineNode.DefaultVolumeLinear,
                PlayRate = ClampPlayRate(baseRate),
                PitchCents = ClampPitchCents(basePitch)
            };
        }

        float volume;
        float rateScale;
        float pitchOffset;
        if (prev == null)
        {
            volume = next.VolumeLinear;
            rateScale = next.RateScale;
            pitchOffset = next.PitchCents;
        }
        else if (next == null)
        {
            volume = prev.VolumeLinear;
            rateScale = prev.RateScale;
            pitchOffset = prev.PitchCents;
        }
        else
        {
            var mode = next.VolumeInterpolation;
            volume = TimelineVolume.InterpolateVolumeLinear(mode, prev.VolumeLinear, next.VolumeLinear, t);
            rateScale = TimelineVolume.InterpolateRateScale(mode, prev.RateScale, next.RateScale, t);
            pitchOffset = TimelineVolume.InterpolatePitchCents(mode, prev.PitchCents, next.PitchCents, t);
        }

        return new TimelineAutomation
        {
            VolumeLinear = volume,
            PlayRate = ClampPlayRate(baseRate * rateScale),
            PitchCents = ClampPitchCents(basePitch + pitchOffset)
        };
    }

    /// <summary>
    /// Relative linear gain at a file time, interpolated between timeline nodes in dB.
    /// </summary>
    /// <param name="fileSeconds">Position on the audio file, in seconds.</param>
    /// <returns>Clamped linear gain (1 = 0 dB relative). Unity when there are no nodes.</returns>
    public float EvaluateTimelineVolumeLinear(double fileSeconds) =>
        EvaluateTimeline(fileSeconds).VolumeLinear;

    /// <summary>
    /// Absolute play rate at a file time: component <see cref="PlayRate"/> × relative node scale.
    /// </summary>
    /// <param name="fileSeconds">Position on the audio file, in seconds.</param>
    /// <returns>Clamped play-rate multiplier.</returns>
    public double EvaluateTimelinePlayRate(double fileSeconds) =>
        EvaluateTimeline(fileSeconds).PlayRate;

    /// <summary>
    /// Absolute pitch in cents at a file time: component <see cref="PitchCents"/> + relative node offset.
    /// </summary>
    /// <param name="fileSeconds">Position on the audio file, in seconds.</param>
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

        var ordered = GetTimelineNodesInTimeOrder();
        AudioTimelineNode prev = null;
        int i = 0;
        while (i < ordered.Count && ordered[i].TimeSeconds <= startSeconds)
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
            var next = i < ordered.Count ? ordered[i] : null;
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
                float scale;
                if (prev == null && next == null)
                    scale = AudioTimelineNode.DefaultRateScale;
                else if (prev == null)
                    scale = next.RateScale;
                else if (next == null)
                    scale = prev.RateScale;
                else
                {
                    double pairSpan = next.TimeSeconds - prev.TimeSeconds;
                    float t = pairSpan <= 1e-9 ? 1f : (float)((mid - prev.TimeSeconds) / pairSpan);
                    scale = TimelineVolume.InterpolateRateScale(
                        next.VolumeInterpolation, prev.RateScale, next.RateScale, t);
                }

                double rate = ClampPlayRate(PlayRate * scale);
                if (rate < MinPlayRate)
                    rate = MinPlayRate;
                wall += (t1 - t0) / rate;
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

        var ordered = GetTimelineNodesInTimeOrder();
        AudioTimelineNode prev = null;
        int i = 0;
        while (i < ordered.Count && ordered[i].TimeSeconds <= startSeconds)
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
            var next = i < ordered.Count ? ordered[i] : null;
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
                float scale;
                if (prev == null && next == null)
                    scale = AudioTimelineNode.DefaultRateScale;
                else if (prev == null)
                    scale = next.RateScale;
                else if (next == null)
                    scale = prev.RateScale;
                else
                {
                    double pairSpan = next.TimeSeconds - prev.TimeSeconds;
                    float t = pairSpan <= 1e-9 ? 1f : (float)((mid - prev.TimeSeconds) / pairSpan);
                    scale = TimelineVolume.InterpolateRateScale(
                        next.VolumeInterpolation, prev.RateScale, next.RateScale, t);
                }

                double rate = ClampPlayRate(PlayRate * scale);
                if (rate < MinPlayRate)
                    rate = MinPlayRate;
                double stepWall = (t1 - t0) / rate;
                if (wall + stepWall >= wallOffset - eps)
                {
                    double frac = stepWall <= eps ? 0.0 : (wallOffset - wall) / stepWall;
                    return t0 + Math.Clamp(frac, 0.0, 1.0) * (t1 - t0);
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

        return endSeconds;
    }

    /// <summary>
    /// Picks a file time for a manually added node (midpoint of the longest gap).
    /// </summary>
    /// <returns>A time in file seconds, or −1 when no metadata is available.</returns>
    public double PickNewTimelineNodeTime()
    {
        double fileDuration = Metadata?.Duration ?? 0.0;
        if (fileDuration <= 1e-6)
            return -1;

        EnsureFileEndNode();
        double bestStart = 0;
        double bestLen = 0;
        double prev = 0;
        foreach (var node in GetTimelineNodesInTimeOrder())
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
            return ClampTimelineNodeTime(fileDuration * 0.5);

        return ClampTimelineNodeTime(bestStart + bestLen * 0.5);
    }

    public void LoadFromData(Dictionary data)
    {
        if (!data.ContainsKey("AudioFile")) 
        {
            GD.PrintErr("AudioComponent:LoadFromData - Missing 'AudioFile' key.");
            return;
        }
        AudioFile = (string)data["AudioFile"];
        StartTime = data.ContainsKey("StartTime") ? data["StartTime"].AsDouble() : 0.0;
        EndTime = data.ContainsKey("EndTime") ? data["EndTime"].AsDouble() : -1.0;
        Duration = data.ContainsKey("Duration") ? data["Duration"].AsDouble() : 0.0;
        Loop = data.ContainsKey("Loop") ? data["Loop"].AsBool() : false;
        Volume = data.ContainsKey("Volume") ? data["Volume"].AsSingle() : 1.0f;
        Pan = data.ContainsKey("Pan")
            ? Math.Clamp(data["Pan"].AsSingle(), -1f, 1f)
            : 0f;
        PlayCount = data.ContainsKey("PlayCount") ? data["PlayCount"].AsInt32() : 1;
        PlayRate = data.ContainsKey("PlayRate") ? data["PlayRate"].AsDouble() : DefaultPlayRate;
        KeepPitch = data.ContainsKey("KeepPitch") && data["KeepPitch"].AsBool();
        PitchCents = data.ContainsKey("PitchCents") ? data["PitchCents"].AsSingle() : DefaultPitchCents;
        FadeInDuration = data.ContainsKey("FadeInDuration") ? data["FadeInDuration"].AsDouble() : 0.0;
        FadeInCurve = data.ContainsKey("FadeInCurve")
            ? FadeCurve.FromInt(data["FadeInCurve"].AsInt32())
            : FadeCurveType.Linear;
        FadeOutDuration = data.ContainsKey("FadeOutDuration") ? data["FadeOutDuration"].AsDouble() : 0.0;
        FadeOutCurve = data.ContainsKey("FadeOutCurve")
            ? FadeCurve.FromInt(data["FadeOutCurve"].AsInt32())
            : FadeCurveType.Linear;
        // Legacy showfiles may still embed peaks; accept into memory so open can migrate to Waveforms/.
        // New saves omit this key — UI regenerates via MediaEngine disk cache when empty.
        WaveformData = TryReadByteArray(data, "WaveformData");
        PatchId = data.ContainsKey("PatchId") ? data["PatchId"].AsInt32() : -1;
        // Runtime Patch reference is re-linked after load; clear here so a stale object cannot win.
        Patch = null;
        if (data.ContainsKey("Routing") && data["Routing"].VariantType == Variant.Type.Dictionary)
        {
            Routing = new CuePatch();
            Routing.LoadFromData((Dictionary)data["Routing"]);
        }
        else
        {
            Routing = null;
        }
        if (data.ContainsKey("DirectOutput") && data["DirectOutput"].VariantType != Variant.Type.Nil)
        {
            var direct = data["DirectOutput"].AsString();
            DirectOutput = string.IsNullOrEmpty(direct) ? null : direct;
        }
        else
        {
            DirectOutput = null;
        }
        
        if (data.ContainsKey("Metadata")) 
        { 
            var metaDict = (Dictionary)data["Metadata"]; 
            Metadata = new AudioFileMetadata(); 
            Metadata.Duration = metaDict.ContainsKey("Duration") ? (double)metaDict["Duration"] : 0.0; 
            Metadata.Channels = metaDict.ContainsKey("Channels") ? (int)metaDict["Channels"] : 0; 
            Metadata.SampleRate = metaDict.ContainsKey("SampleRate") ? (int)metaDict["SampleRate"] : 0; 
            Metadata.BitDepth = metaDict.ContainsKey("BitDepth") ? (int)metaDict["BitDepth"] : 0; 
            Metadata.Codec = metaDict.ContainsKey("Codec") ? (string)metaDict["Codec"] : "unknown"; 
            Metadata.Format = metaDict.ContainsKey("Format") ? (string)metaDict["Format"] : "unknown"; 
            // Sync legacy fields from metadata (for backward compat) 
        } 
        else 
        { 
            GD.Print("AudioComponent:LoadFromData - No metadata in save data; will extract on next load.");
            Metadata = null; 
        }

        LoadTimelineNodesFromData(data);

        if (Metadata != null)
            RecalculateDuration();
    }

    /// <summary>
    /// Clamps a timeline node into the file duration.
    /// User nodes stay just before the locked end-of-file node.
    /// </summary>
    /// <param name="proposedSeconds">Requested time in file seconds.</param>
    /// <param name="isFileEnd">When true, pin to the file duration.</param>
    /// <returns>Clamped time in seconds.</returns>
    public double ClampTimelineNodeTime(double proposedSeconds, bool isFileEnd = false)
    {
        double fileDuration = Metadata?.Duration ?? 0.0;
        if (fileDuration < 0.0)
            fileDuration = 0.0;
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
    public List<AudioTimelineNode> GetTimelineNodesInTimeOrder()
    {
        var ordered = new List<AudioTimelineNode>(TimelineNodes.Count);
        foreach (var node in TimelineNodes)
        {
            if (node != null)
                ordered.Add(node);
        }

        ordered.Sort(static (a, b) =>
        {
            int byTime = a.TimeSeconds.CompareTo(b.TimeSeconds);
            return byTime != 0 ? byTime : a.Id.CompareTo(b.Id);
        });
        return ordered;
    }

    /// <summary>
    /// Adds a numbered marker at the given file time.
    /// </summary>
    /// <param name="timeSeconds">Position on the file, in seconds.</param>
    /// <returns>The new node, or null when the cap is reached.</returns>
    public AudioTimelineNode AddTimelineNode(double timeSeconds)
    {
        EnsureFileEndNode();
        if (CountUserTimelineNodes() >= MaxTimelineNodes)
            return null;

        var node = AudioTimelineNode.Create(
            _nextTimelineNodeId++,
            ClampTimelineNodeTime(timeSeconds, isFileEnd: false));
        TimelineNodes.Add(node);
        return node;
    }

    /// <summary>
    /// Removes the marker with the given id. The locked end-of-file node cannot be removed.
    /// </summary>
    /// <param name="id">Stable node id.</param>
    /// <returns>True when a node was removed.</returns>
    public bool RemoveTimelineNode(int id)
    {
        var node = FindTimelineNode(id);
        if (node == null || node.IsFileEnd)
            return false;
        return TimelineNodes.RemoveAll(n => n != null && n.Id == id) > 0;
    }

    /// <summary>
    /// Finds a marker by stable id.
    /// </summary>
    /// <param name="id">Stable node id.</param>
    /// <returns>The node, or null.</returns>
    public AudioTimelineNode FindTimelineNode(int id)
    {
        return TimelineNodes.Find(n => n != null && n.Id == id);
    }

    /// <summary>
    /// Removes every user-placed timeline marker and restores the locked end-of-file node.
    /// </summary>
    public void ClearTimelineNodes()
    {
        TimelineNodes.Clear();
        _nextTimelineNodeId = 1;
        EnsureFileEndNode();
    }

    /// <summary>
    /// How many user-placed markers are on this component (excludes the end-of-file node).
    /// </summary>
    /// <returns>Count of movable nodes.</returns>
    public int CountUserTimelineNodes()
    {
        int count = 0;
        foreach (var node in TimelineNodes)
        {
            if (node != null && !node.IsFileEnd)
                count++;
        }

        return count;
    }

    /// <summary>
    /// Ensures a locked node exists at the file duration. No-op until metadata is known.
    /// </summary>
    public void EnsureFileEndNode()
    {
        if (Metadata == null)
            return;

        double fileDuration = Metadata.Duration;
        if (fileDuration < 0)
            fileDuration = 0;

        AudioTimelineNode eof = null;
        for (int i = TimelineNodes.Count - 1; i >= 0; i--)
        {
            var node = TimelineNodes[i];
            if (node == null)
            {
                TimelineNodes.RemoveAt(i);
                continue;
            }

            if (!node.IsFileEnd)
                continue;
            if (eof == null)
                eof = node;
            else
                TimelineNodes.RemoveAt(i);
        }

        if (eof == null)
        {
            TimelineNodes.Add(AudioTimelineNode.Create(_nextTimelineNodeId++, fileDuration, isFileEnd: true));
            return;
        }

        eof.TimeSeconds = fileDuration;
        eof.IsFileEnd = true;
    }

    /// <summary>
    /// Clamps every marker into the current file duration and pins the end-of-file node.
    /// </summary>
    public void ClampAllTimelineNodes()
    {
        EnsureFileEndNode();
        foreach (var node in TimelineNodes)
        {
            if (node == null) continue;
            node.TimeSeconds = ClampTimelineNodeTime(node.TimeSeconds, node.IsFileEnd);
        }
    }

    /// <summary>
    /// Restores timeline markers from showfile / history data.
    /// </summary>
    /// <param name="data">Component dictionary.</param>
    private void LoadTimelineNodesFromData(Dictionary data)
    {
        TimelineNodes.Clear();
        _nextTimelineNodeId = 1;
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
            {
                node.VolumeInterpolation = legacyInterp ?? TimelineVolumeInterpolation.Snap;
            }

            if (!node.IsFileEnd)
            {
                if (userCount >= MaxTimelineNodes)
                    continue;
                userCount++;
            }

            TimelineNodes.Add(node);
            if (node.Id > maxId)
                maxId = node.Id;
        }

        _nextTimelineNodeId = maxId + 1;
        if (_nextTimelineNodeId < 1)
            _nextTimelineNodeId = 1;
        ClampAllTimelineNodes();
    }

    /// <summary>
    /// Finds the previous and next timeline nodes around a file time, and the mix t in 0–1.
    /// </summary>
    /// <param name="fileSeconds">Position on the audio file, in seconds.</param>
    /// <param name="prev">Nearest node strictly before the time, or null.</param>
    /// <param name="next">Nearest node at or after the time, or null.</param>
    /// <param name="t">Progress from prev to next; 1 when the span is empty.</param>
    /// <returns>False when there are no nodes.</returns>
    private bool TryGetTimelineNeighbors(
        double fileSeconds,
        out AudioTimelineNode prev,
        out AudioTimelineNode next,
        out float t)
    {
        prev = null;
        next = null;
        t = 1f;
        bool any = false;
        foreach (var node in TimelineNodes)
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

    /// <summary>
    /// Reads a byte[] field that may arrive as raw bytes, PackedByteArray, or a JSON number array.
    /// </summary>
    private static byte[] TryReadByteArray(Dictionary data, string key)
    {
        if (data == null || !data.ContainsKey(key)) return null;
        try
        {
            var variant = data[key];
            if (variant.VariantType == Variant.Type.Nil) return null;
            if (variant.AsByteArray() is { Length: > 0 } packed)
                return packed;
            // Empty PackedByteArray is valid (stripped history snapshots).
            if (variant.VariantType == Variant.Type.PackedByteArray)
                return variant.AsByteArray();
            if (variant.Obj is byte[] bytes)
                return bytes;
            if (variant.VariantType == Variant.Type.Array)
            {
                var arr = variant.AsGodotArray();
                var result = new byte[arr.Count];
                for (int i = 0; i < arr.Count; i++)
                    result[i] = (byte)arr[i].AsInt32();
                return result;
            }
        }
        catch
        {
            // Leave waveform null; UI regenerates peaks from media when needed.
        }
        return null;
    }

}