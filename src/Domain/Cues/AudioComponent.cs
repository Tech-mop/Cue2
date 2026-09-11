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
    /// Duration is length of audio between start and endtime
    /// </summary>
    public double Duration { get; set; } = 0.0;
    
    /// <summary>
    /// TotalDuration is time the audio plays including playcount. ((Endtime-Starttime) * playcount)
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
    
    public double FadeInDuration { get; set; } = 0.0; // In seconds
    public double FadeOutDuration { get; set; } = 0.0; // In seconds

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
        data.Add("FadeInDuration", FadeInDuration);
        data.Add("FadeOutDuration", FadeOutDuration);
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

        Duration = EndTime < 0
            ? Math.Max(0, fileDuration - StartTime)
            : Math.Max(0, EndTime - StartTime);
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
        FadeInDuration = data.ContainsKey("FadeInDuration") ? data["FadeInDuration"].AsDouble() : 0.0;
        FadeOutDuration = data.ContainsKey("FadeOutDuration") ? data["FadeOutDuration"].AsDouble() : 0.0;
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