// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System;
using Godot;
using Godot.Collections;

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
    /// Wall-clock length of the start–end region at the current <see cref="PlayRate"/>.
    /// </summary>
    public double Duration { get; set; } = 0.0;
    
    /// <summary>
    /// Wall-clock time the audio plays including play count (Duration × play count).
    /// </summary>
    /// <value>Returns -1 if looping enabled</value>
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

        double fileDuration = Metadata.Duration;
        if (fileDuration < 0) fileDuration = 0;

        // Keep start within file bounds so duration/playback cannot go invalid.
        StartTime = ClampStartTime(StartTime);

        double region = EndTime < 0
            ? Math.Max(0, fileDuration - StartTime)
            : Math.Max(0, EndTime - StartTime);
        // Duration / TotalDuration are wall-clock (rate 2 → half as long).
        Duration = region / PlayRate;
        TotalDuration = Loop ? -1.0 : Duration * PlayCount;
        return Duration;
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

        if (Metadata != null)
            RecalculateDuration();
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