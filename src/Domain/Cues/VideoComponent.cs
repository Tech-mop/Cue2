// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System;
using System.IO;
using System.Linq;
using Cue2.Services;
using Godot;
using Godot.Collections;

namespace Cue2.Domain.Cues;

/// <summary>
/// Legacy simplified display presets (pre Expand/Stretch split). Kept for save migration only.
/// </summary>
public enum VideoDisplayMode
{
    /// <summary>Letterbox — maps to IgnoreSize + KeepAspectCentered.</summary>
    Fit = 0,
    /// <summary>Cover — maps to IgnoreSize + KeepAspectCovered.</summary>
    Fill = 1,
    /// <summary>Distort to fill — maps to IgnoreSize + Scale.</summary>
    Stretch = 2,
}

/// <summary>
/// Video or still-image media component attached to a cue.
/// Images reuse the same playback/inspector path as video; in/out points are replaced by a user duration.
/// </summary>
public class VideoComponent : ICueComponent
{
    public string Type => "Video";
    public string VideoFile { get; set; }

    /// <summary>
    /// True when <see cref="VideoFile"/> is a still image (png/jpg/etc).
    /// Images do not use start/end times; <see cref="Duration"/> is user-set (0 = until stopped).
    /// </summary>
    public bool IsImage { get; set; }

    /// <summary>Start Time in seconds (video only; ignored for images).</summary>
    public double StartTime { get; set; } = 0.0; // In seconds
    public double EndTime { get; set; } = -1.0; // -1 means play until end of cue
    /// <summary>
    /// Target video layer id for playback. <c>-1</c> means no layer assigned ("No Output").
    /// </summary>
    public int TargetLayerId { get; set; } = -1;

    /// <summary>
    /// Godot <see cref="TextureRect.ExpandMode"/> for how the control size interacts with the texture.
    /// Default matches previous Fit/Fill/Stretch behaviour (ignore size, fill host rect).
    /// </summary>
    public TextureRect.ExpandModeEnum TextureExpandMode { get; set; } =
        TextureRect.ExpandModeEnum.IgnoreSize;

    /// <summary>
    /// Godot <see cref="TextureRect.StretchMode"/> for how the frame is drawn inside the control.
    /// Default is Keep Aspect Centered (Fit).
    /// </summary>
    public TextureRect.StretchModeEnum TextureStretchMode { get; set; } =
        TextureRect.StretchModeEnum.KeepAspectCentered;

    /// <summary>
    /// Visual opacity of the video on the target layer (0 = invisible, 1 = fully opaque).
    /// Inspector edits this as a percentage.
    /// </summary>
    public float Opacity { get; set; } = 1f;

    public bool HasAudio { get; set; }

    /// <summary>
    /// Applies TextureRect expand + stretch modes for layer display.
    /// Host should be the layer rectangle with <c>ClipContents = true</c> for covered modes.
    /// </summary>
    public static void ApplyTextureLayout(
        TextureRect rect,
        TextureRect.ExpandModeEnum expandMode,
        TextureRect.StretchModeEnum stretchMode)
    {
        if (rect == null || !GodotObject.IsInstanceValid(rect))
            return;

        rect.ExpandMode = expandMode;
        rect.StretchMode = stretchMode;

        // When ignoring size, fill the layer host so stretch modes have a defined rect.
        if (expandMode == TextureRect.ExpandModeEnum.IgnoreSize
            && rect.GetParent() is Control parent
            && parent.Size.X > 0 && parent.Size.Y > 0)
        {
            rect.Position = Vector2.Zero;
            rect.Size = parent.Size;
        }
    }

    /// <summary>
    /// Convenience: apply this component's expand/stretch settings to a TextureRect.
    /// </summary>
    public void ApplyTextureLayout(TextureRect rect)
    {
        ApplyTextureLayout(rect, TextureExpandMode, TextureStretchMode);
    }

    /// <summary>
    /// Maps legacy Fit/Fill/Stretch presets onto Expand + Stretch modes.
    /// </summary>
    public static void ApplyLegacyDisplayMode(
        out TextureRect.ExpandModeEnum expand,
        out TextureRect.StretchModeEnum stretch,
        VideoDisplayMode legacy)
    {
        expand = TextureRect.ExpandModeEnum.IgnoreSize;
        stretch = legacy switch
        {
            VideoDisplayMode.Fill => TextureRect.StretchModeEnum.KeepAspectCovered,
            VideoDisplayMode.Stretch => TextureRect.StretchModeEnum.Scale,
            _ => TextureRect.StretchModeEnum.KeepAspectCentered,
        };
    }

    /// <summary>
    /// Parses an int-like Variant to an enum value of type <typeparamref name="TEnum"/>.
    /// Uses the enum's underlying type so Godot long-backed enums do not throw in
    /// <see cref="Enum.IsDefined"/>.
    /// </summary>
    public static TEnum ParseEnumVariant<TEnum>(Variant value, TEnum fallback) where TEnum : struct, Enum
    {
        try
        {
            long modeVal = value.VariantType switch
            {
                Variant.Type.Int => value.AsInt64(),
                Variant.Type.Float => (long)value.AsDouble(),
                Variant.Type.String => long.TryParse(value.AsString(), out long parsed)
                    ? parsed
                    : Convert.ToInt64(fallback),
                _ => value.AsInt64()
            };
            Type enumType = typeof(TEnum);
            Type underlying = Enum.GetUnderlyingType(enumType);
            object boxed = Convert.ChangeType(modeVal, underlying);
            if (!Enum.IsDefined(enumType, boxed))
                return fallback;
            return (TEnum)Enum.ToObject(enumType, boxed);
        }
        catch
        {
            return fallback;
        }
    }

    /// <summary>
    /// Loads expand/stretch modes, migrating legacy DisplayMode when present.
    /// </summary>
    private void LoadTextureLayoutFromData(Dictionary data)
    {
        bool hasExpand = data.ContainsKey("TextureExpandMode");
        bool hasStretch = data.ContainsKey("TextureStretchMode");

        if (hasExpand || hasStretch)
        {
            TextureExpandMode = hasExpand
                ? ParseEnumVariant(data["TextureExpandMode"], TextureRect.ExpandModeEnum.IgnoreSize)
                : TextureRect.ExpandModeEnum.IgnoreSize;
            TextureStretchMode = hasStretch
                ? ParseEnumVariant(data["TextureStretchMode"], TextureRect.StretchModeEnum.KeepAspectCentered)
                : TextureRect.StretchModeEnum.KeepAspectCentered;
            return;
        }

        // Migrate pre-expand/stretch saves that only had DisplayMode (Fit/Fill/Stretch).
        if (data.ContainsKey("DisplayMode"))
        {
            var legacy = ParseEnumVariant(data["DisplayMode"], VideoDisplayMode.Fit);
            ApplyLegacyDisplayMode(out var expand, out var stretch, legacy);
            TextureExpandMode = expand;
            TextureStretchMode = stretch;
            return;
        }

        TextureExpandMode = TextureRect.ExpandModeEnum.IgnoreSize;
        TextureStretchMode = TextureRect.StretchModeEnum.KeepAspectCentered;
    }

    /// <summary>
    /// Parses opacity from save data, clamped to 0–1.
    /// </summary>
    public static float ParseOpacity(Variant value)
    {
        try
        {
            float v = value.VariantType switch
            {
                Variant.Type.Float => (float)(double)value,
                Variant.Type.Int => (int)value,
                Variant.Type.String => float.TryParse((string)value, out float p) ? p : 1f,
                _ => (float)(double)value
            };
            // Guard against accidental percentage storage (e.g. 50 meaning 50%)
            if (v > 1f)
                v /= 100f;
            return Mathf.Clamp(v, 0f, 1f);
        }
        catch
        {
            return 1f;
        }
    }

    /// <summary>
    /// Segment length in seconds.
    /// Video: length between start and end time.
    /// Image: user-set hold time; <c>0</c> means stay active until stopped.
    /// </summary>
    public double Duration { get; set; } = 0.0;

    /// <summary>
    /// Total play length including play count.
    /// Video: ((EndTime-StartTime) * PlayCount); Image: Duration * PlayCount when Duration &gt; 0.
    /// </summary>
    /// <value>Returns -1 if looping, or if image duration is 0 (until stopped).</value>
    public double TotalDuration { get; set; } = 0.0;

    /// <summary>
    /// Returns true when <paramref name="path"/> matches a known still-image extension.
    /// </summary>
    /// <param name="path">File path (absolute, relative, or extension-only).</param>
    /// <returns>True if the extension is in <see cref="GlobalData.ImageFileFilters"/>.</returns>
    public static bool IsImagePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;
        string ext = Path.GetExtension(path);
        if (string.IsNullOrEmpty(ext))
            return false;
        return GlobalData.ImageFileFilters.Any(e =>
            e.TrimStart('*').Equals(ext, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Updates <see cref="IsImage"/> from the current <see cref="VideoFile"/> path.
    /// </summary>
    public void RefreshIsImageFromPath()
    {
        IsImage = IsImagePath(VideoFile);
    }
    public double Volume { get; set; } = 1.0f;
    public bool Loop { get; set; } = false;
    public int PlayCount { get; set; } = 1;

    /// <summary>Scaled width in pixels</summary>
    public int ScaledWidth { get; set; } = 0;
    /// <summary>Scaled height in pixels</summary>
    public int ScaledHeight { get; set; } = 0;
    /// <summary>Offset X position in pixels</summary>
    public int OffsetX { get; set; } = 0;
    /// <summary>Offset Y position in pixels</summary>
    public int OffsetY { get; set; } = 0;
    /// <summary>Whether to use audio if available</summary>
    public bool UseAudio { get; set; } = true;

    /// <summary>Audio output patch</summary>
    public AudioOutputPatch Patch { get; set; } = null;
    /// <summary>Audio output patch ID</summary>
    public int PatchId { get; set; } = -1;
    /// <summary>Direct audio output device name</summary>
    public string DirectOutput { get; set; } = null;

    /// <summary>
    /// Returns true when a patch or direct output device has been assigned for embedded audio.
    /// </summary>
    public bool HasAudioOutputAssigned =>
        Patch != null || !string.IsNullOrEmpty(DirectOutput);

    /// <summary>Audio routing matrix with volumes</summary>
    public CuePatch Routing { get; set; } = null;
    /// <summary>
    /// Volume multiplier for embedded audio (linear). Unity = 1.0 (0 dB);
    /// digital gain up to ≈3.98 (+12 dB) is allowed.
    /// </summary>
    public float AudioVolume { get; set; } = 1f;

    /// <summary>
    /// Stereo pan/balance for embedded audio, applied after volume and before the routing matrix.
    /// Range −1 (full left) … 0 (center) … +1 (full right). Only used for stereo sources.
    /// </summary>
    /// <value>Clamped to [−1, 1]. Default 0 (center / "C").</value>
    public float Pan
    {
        get => _pan;
        set => _pan = Math.Clamp(value, -1f, 1f);
    }
    private float _pan;

    /// <summary>
    /// In-memory peak envelope for UI display only.
    /// Not written into showfiles — peaks persist under <c>SessionDir/Waveforms/*.c2wf</c>
    /// via <see cref="Cue2.Services.MediaEngine.GenerateWaveformAsync"/>.
    /// </summary>
    public byte[] WaveformData { get; set; } = null;

    public double FadeInDuration { get; set; } = 0.0; // In seconds
    public double FadeOutDuration { get; set; } = 0.0; // In seconds

    /// <summary>
    /// When true and a text-based subtitle track is available, drive the cue's
    /// <see cref="TextComponent"/> with timed closed captions during playback.
    /// </summary>
    public bool UseSubtitles { get; set; }

    /// <summary>
    /// FFmpeg stream index of the selected subtitle track. <c>-1</c> = first text track
    /// (or none if no text subtitles exist).
    /// </summary>
    public int SubtitleStreamIndex { get; set; } = -1;

    /// <summary>
    /// Full metadata from file (duration, width, height, frame rate, codec, format).
    /// Set via inspector on load; used for UI/display and playback routing.
    /// </summary>
    public VideoFileMetadata Metadata { get; set; } = null;

    /// <summary>
    /// True when metadata lists at least one text-based subtitle track.
    /// </summary>
    public bool HasTextSubtitles => Metadata?.HasTextSubtitles == true;

    /// <summary>
    /// Resolves the effective subtitle stream index for playback, or -1 if none.
    /// Prefer <see cref="ResolveSubtitleTrack"/> when external sidecars may be used.
    /// </summary>
    public int ResolveSubtitleStreamIndex() => ResolveSubtitleTrack()?.StreamIndex ?? -1;

    /// <summary>
    /// Resolves the selected text subtitle track (embedded or sidecar), or null.
    /// </summary>
    public SubtitleTrackInfo ResolveSubtitleTrack()
    {
        if (Metadata?.SubtitleTracks == null || Metadata.SubtitleTracks.Count == 0)
            return null;

        // Match explicit selection (including synthetic negative indices for sidecars).
        foreach (var t in Metadata.SubtitleTracks)
        {
            if (t != null && t.IsTextBased && t.StreamIndex == SubtitleStreamIndex)
                return t;
        }

        // SubtitleStreamIndex unmatched / default → first text track.
        return Metadata.GetDefaultTextSubtitleTrack();
    }
    
    
    public Godot.Collections.Dictionary GetData()
    {
        var data = new Dictionary();
        data.Add("VideoFile", VideoFile);
        data.Add("IsImage", IsImage);
        data.Add("StartTime", StartTime);
        data.Add("EndTime", EndTime);
        data.Add("TargetLayerId", TargetLayerId);
        data.Add("TextureExpandMode", (int)TextureExpandMode);
        data.Add("TextureStretchMode", (int)TextureStretchMode);
        data.Add("Opacity", Opacity);
        data.Add("Duration", Duration);
        data.Add("Loop", Loop);
        data.Add("Volume", Volume);
        data.Add("PlayCount", PlayCount);
        data.Add("FadeInDuration", FadeInDuration);
        data.Add("FadeOutDuration", FadeOutDuration);
        data.Add("ScaledWidth", ScaledWidth);
        data.Add("ScaledHeight", ScaledHeight);
        data.Add("OffsetX", OffsetX);
        data.Add("OffsetY", OffsetY);
        data.Add("HasAudio", HasAudio);
        data.Add("UseAudio", UseAudio);
        data.Add("UseSubtitles", UseSubtitles);
        data.Add("SubtitleStreamIndex", SubtitleStreamIndex);
        data.Add("PatchId", Patch?.Id ?? PatchId); // Reference patch by ID; fall back to stored PatchId
        data.Add("DirectOutput", DirectOutput ?? string.Empty);
        if (Routing != null)
        {
            data.Add("Routing", Routing.GetData());
        }
        data.Add("AudioVolume", AudioVolume);
        data.Add("Pan", Pan);
        // Waveform peaks are session disk-cache only (Waveforms/*.c2wf), not showfile payload.

        if (Metadata != null)
        {
            var metaDict = new Dictionary();
            metaDict.Add("Duration", Metadata.Duration);
            metaDict.Add("Width", Metadata.Width);
            metaDict.Add("Height", Metadata.Height);
            metaDict.Add("FrameRate", Metadata.FrameRate);
            metaDict.Add("Codec", Metadata.Codec);
            metaDict.Add("Format", Metadata.Format);
            metaDict.Add("AudioChannels", Metadata.AudioChannels);
            metaDict.Add("AudioSampleRate", Metadata.AudioSampleRate);
            metaDict.Add("AudioBitDepth", Metadata.AudioBitDepth);
            metaDict.Add("AudioCodec", Metadata.AudioCodec);
            if (Metadata.SubtitleTracks != null && Metadata.SubtitleTracks.Count > 0)
            {
                var tracks = new Godot.Collections.Array();
                foreach (var t in Metadata.SubtitleTracks)
                {
                    if (t == null) continue;
                    tracks.Add(new Dictionary
                    {
                        { "StreamIndex", t.StreamIndex },
                        { "Codec", t.Codec ?? string.Empty },
                        { "Language", t.Language ?? string.Empty },
                        { "Title", t.Title ?? string.Empty },
                        { "IsTextBased", t.IsTextBased },
                        { "ExternalFilePath", t.ExternalFilePath ?? string.Empty },
                    });
                }
                metaDict.Add("SubtitleTracks", tracks);
            }
            data.Add("Metadata", metaDict);
        }

        return data;
    }

    public void LoadFromData(Godot.Collections.Dictionary data)
    {
        if (!data.ContainsKey("VideoFile"))
        {
            GD.PrintErr("VideoComponent:LoadFromData - Missing 'VideoFile' key.");
            return;
        }
        VideoFile = (string)data["VideoFile"];
        // Prefer saved flag; fall back to extension detection for older show files.
        if (data.ContainsKey("IsImage"))
            IsImage = data["IsImage"].AsBool();
        else
            RefreshIsImageFromPath();
        StartTime = data.ContainsKey("StartTime") ? data["StartTime"].AsDouble() : 0.0;
        EndTime = data.ContainsKey("EndTime") ? data["EndTime"].AsDouble() : -1.0;
        // Legacy saves without the key used layer 0; explicit -1 is "No Output".
        TargetLayerId = data.ContainsKey("TargetLayerId") ? data["TargetLayerId"].AsInt32() : 0;
        LoadTextureLayoutFromData(data);
        Opacity = data.ContainsKey("Opacity") ? ParseOpacity(data["Opacity"]) : 1f;
        Duration = data.ContainsKey("Duration") ? data["Duration"].AsDouble() : 0.0;
        Loop = data.ContainsKey("Loop") ? data["Loop"].AsBool() : false;
        Volume = data.ContainsKey("Volume") ? data["Volume"].AsSingle() : 1.0f;
        PlayCount = data.ContainsKey("PlayCount") ? data["PlayCount"].AsInt32() : 1;
        FadeInDuration = data.ContainsKey("FadeInDuration") ? data["FadeInDuration"].AsDouble() : 0.0;
        FadeOutDuration = data.ContainsKey("FadeOutDuration") ? data["FadeOutDuration"].AsDouble() : 0.0;
        ScaledWidth = data.ContainsKey("ScaledWidth") ? data["ScaledWidth"].AsInt32() : 0;
        ScaledHeight = data.ContainsKey("ScaledHeight") ? data["ScaledHeight"].AsInt32() : 0;
        OffsetX = data.ContainsKey("OffsetX") ? data["OffsetX"].AsInt32() : 0;
        OffsetY = data.ContainsKey("OffsetY") ? data["OffsetY"].AsInt32() : 0;
        HasAudio = data.ContainsKey("HasAudio") ? data["HasAudio"].AsBool() : false;
        UseAudio = data.ContainsKey("UseAudio") ? data["UseAudio"].AsBool() : true;
        UseSubtitles = data.ContainsKey("UseSubtitles") && data["UseSubtitles"].AsBool();
        SubtitleStreamIndex = data.ContainsKey("SubtitleStreamIndex")
            ? data["SubtitleStreamIndex"].AsInt32()
            : -1;
        PatchId = data.ContainsKey("PatchId") ? data["PatchId"].AsInt32() : -1;
        DirectOutput = data.ContainsKey("DirectOutput") ? data["DirectOutput"].AsString() : null;

        // Legacy showfiles may still embed peaks; accept into memory so open can migrate to Waveforms/.
        WaveformData = TryReadByteArray(data, "WaveformData");
        if (data.ContainsKey("Routing"))
        {
            Routing = new CuePatch();
            Routing.LoadFromData((Dictionary)data["Routing"]);
        }
        AudioVolume = data.ContainsKey("AudioVolume") ? (float)(double)data["AudioVolume"] : 1f;
        Pan = data.ContainsKey("Pan")
            ? Math.Clamp(data["Pan"].AsSingle(), -1f, 1f)
            : 0f;

        if (data.ContainsKey("Metadata"))
        {
            var metaDict = (Dictionary)data["Metadata"];
            Metadata = new VideoFileMetadata();
            Metadata.Duration = metaDict.ContainsKey("Duration") ? (double)metaDict["Duration"] : 0.0;
            Metadata.Width = metaDict.ContainsKey("Width") ? (int)metaDict["Width"] : 0;
            Metadata.Height = metaDict.ContainsKey("Height") ? (int)metaDict["Height"] : 0;
            Metadata.FrameRate = metaDict.ContainsKey("FrameRate") ? (float)metaDict["FrameRate"] : 0.0f;
            Metadata.Codec = metaDict.ContainsKey("Codec") ? (string)metaDict["Codec"] : "unknown";
            Metadata.Format = metaDict.ContainsKey("Format") ? (string)metaDict["Format"] : "unknown";
            Metadata.AudioChannels = metaDict.ContainsKey("AudioChannels") ? (int)metaDict["AudioChannels"] : 0;
            Metadata.AudioSampleRate = metaDict.ContainsKey("AudioSampleRate") ? (int)metaDict["AudioSampleRate"] : 0;
            Metadata.AudioBitDepth = metaDict.ContainsKey("AudioBitDepth") ? (int)metaDict["AudioBitDepth"] : 0;
            Metadata.AudioCodec = metaDict.ContainsKey("AudioCodec") ? (string)metaDict["AudioCodec"] : string.Empty;
            Metadata.SubtitleTracks = LoadSubtitleTracksFromMeta(metaDict);
        }
        else
        {
            GD.Print("VideoComponent:LoadFromData - No metadata in save data; will extract on next load.");
            Metadata = null;
        }
    }

    private static System.Collections.Generic.List<SubtitleTrackInfo> LoadSubtitleTracksFromMeta(Dictionary metaDict)
    {
        var list = new System.Collections.Generic.List<SubtitleTrackInfo>();
        if (metaDict == null || !metaDict.ContainsKey("SubtitleTracks"))
            return list;

        try
        {
            var tracks = metaDict["SubtitleTracks"].AsGodotArray();
            foreach (var item in tracks)
            {
                if (item.VariantType != Variant.Type.Dictionary)
                    continue;
                var d = item.AsGodotDictionary();
                list.Add(new SubtitleTrackInfo
                {
                    StreamIndex = d.ContainsKey("StreamIndex") ? d["StreamIndex"].AsInt32() : -1,
                    Codec = d.ContainsKey("Codec") ? d["Codec"].AsString() : string.Empty,
                    Language = d.ContainsKey("Language") ? d["Language"].AsString() : string.Empty,
                    Title = d.ContainsKey("Title") ? d["Title"].AsString() : string.Empty,
                    IsTextBased = !d.ContainsKey("IsTextBased") || d["IsTextBased"].AsBool(),
                    ExternalFilePath = d.ContainsKey("ExternalFilePath")
                        ? d["ExternalFilePath"].AsString()
                        : string.Empty,
                });
            }
        }
        catch (System.Exception ex)
        {
            GD.PrintErr($"VideoComponent:LoadSubtitleTracksFromMeta - {ex.Message}");
        }

        return list;
    }

    /// <summary>
    /// Reads a byte[] field that may arrive as raw bytes, PackedByteArray, or a JSON number array.
    /// </summary>
    private static byte[] TryReadByteArray(Godot.Collections.Dictionary data, string key)
    {
        if (data == null || !data.ContainsKey(key)) return null;
        try
        {
            var variant = data[key];
            if (variant.VariantType == Variant.Type.Nil) return null;
            if (variant.AsByteArray() is { Length: > 0 } packed)
                return packed;
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

    /// <summary>
    /// Clamps a proposed start time into the valid range for this component's media.
    /// Never negative; never greater than the file duration when metadata is known.
    /// Images ignore start time and return 0.
    /// </summary>
    /// <param name="proposedSeconds">Requested start time in seconds.</param>
    /// <returns>Clamped start time in seconds.</returns>
    public double ClampStartTime(double proposedSeconds)
    {
        if (IsImage)
            return 0.0;
        double result = Math.Max(0.0, proposedSeconds);
        double fileDuration = Metadata?.Duration ?? 0.0;
        if (fileDuration > 0.0 && result > fileDuration)
            result = fileDuration;
        return result;
    }

    /// <summary>
    /// Recomputes <see cref="Duration"/> / <see cref="TotalDuration"/> from in/out points (video)
    /// or from the user-set hold duration (image).
    /// </summary>
    /// <returns>The segment <see cref="Duration"/> (0 for image until-stopped).</returns>
    public double RecalculateDuration()
    {
        if (IsImage)
        {
            // Duration is user-authored; 0 / negative means stay active until stopped.
            if (Duration < 0)
                Duration = 0;
            if (Duration <= 0 || Loop)
                TotalDuration = -1.0;
            else
                TotalDuration = Duration * Math.Max(1, PlayCount);
            return Duration;
        }

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
}