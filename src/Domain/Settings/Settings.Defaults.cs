// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System;
using System.Linq;
using Cue2.Domain.Connections;
using Cue2.Domain.Cues;
using Cue2.Services;
using Godot;
using Godot.Collections;

namespace Cue2.Domain.ShowSettings;

/// <summary>
/// Partial: Cue/Audio/Video/Text defaults capture/apply/reset + resolve helpers
/// </summary>
public partial class Settings
{

    public void ResetCueDefaultsToSystem()
    {
        CueDefaultPreWait = SystemDefaultCuePreWait;
        CueDefaultPostWait = SystemDefaultCuePostWait;
        CueDefaultFollow = SystemDefaultCueFollow;
        CueDefaultColor = SystemDefaultCueColor;
        CueDefaultArmed = SystemDefaultCueArmed;
        CueDefaultSkipIfDisarmed = SystemDefaultCueSkipIfDisarmed;
        CueDefaultOnlyOneActiveInstance = SystemDefaultCueOnlyOneActiveInstance;
    }

    /// <summary>
    /// Applies the show's cue shell defaults to a newly constructed cue.
    /// Does not change id, name, number, parent/children, or components.
    /// </summary>
    /// <param name="cue">Cue instance to configure (typically just constructed).</param>
    public void ApplyShellDefaults(Cue cue)
    {
        if (cue == null) return;
        cue.PreWait = CueDefaultPreWait;
        cue.PostWait = CueDefaultPostWait;
        cue.Follow = CueDefaultFollow;
        cue.Color = CueDefaultColor;
        cue.Armed = CueDefaultArmed;
        cue.SkipIfDisarmed = CueDefaultSkipIfDisarmed;
        cue.OnlyOneActiveInstance = CueDefaultOnlyOneActiveInstance;
    }

    /// <summary>
    /// Serializes the cue shell defaults block for showfile / history.
    /// </summary>
    public Dictionary CaptureCueDefaultsDict()
    {
        return new Dictionary
        {
            ["PreWait"] = CueDefaultPreWait,
            ["PostWait"] = CueDefaultPostWait,
            ["Follow"] = (int)CueDefaultFollow,
            ["Color"] = CueDefaultColor.ToHtml(true),
            ["Armed"] = CueDefaultArmed ? 1 : 0,
            ["SkipIfDisarmed"] = CueDefaultSkipIfDisarmed ? 1 : 0,
            ["OnlyOneActiveInstance"] = CueDefaultOnlyOneActiveInstance ? 1 : 0
        };
    }

    /// <summary>
    /// Loads cue shell defaults from a dictionary (showfile or history slice).
    /// Missing keys keep their current values.
    /// </summary>
    /// <param name="data">Dictionary with PreWait, PostWait, Follow, Color, Armed, SkipIfDisarmed, OnlyOneActiveInstance.</param>
    public void ApplyCueDefaultsFromDict(Dictionary data)
    {
        if (data == null) return;

        if (TryGetSettingsValue(data, "PreWait", out var v))
            CueDefaultPreWait = v.AsDouble();
        if (TryGetSettingsValue(data, "PostWait", out v))
            CueDefaultPostWait = v.AsDouble();
        if (TryGetSettingsValue(data, "Follow", out v))
        {
            int followInt = v.AsInt32();
            if (followInt is >= 0 and <= 2)
                CueDefaultFollow = (FollowType)followInt;
        }
        if (TryGetSettingsValue(data, "Color", out v))
            CueDefaultColor = Color.FromString(v.AsString(), CueDefaultColor);
        if (TryGetSettingsValue(data, "Armed", out v))
            CueDefaultArmed = ReadBoolVariant(v);
        if (TryGetSettingsValue(data, "SkipIfDisarmed", out v))
            CueDefaultSkipIfDisarmed = ReadBoolVariant(v);
        if (TryGetSettingsValue(data, "OnlyOneActiveInstance", out v))
            CueDefaultOnlyOneActiveInstance = ReadBoolVariant(v);
    }

    /// <summary>
    /// Returns true when every cue shell default matches the system factory value.
    /// </summary>
    public bool AreCueDefaultsAtSystem()
    {
        return Mathf.IsEqualApprox((float)CueDefaultPreWait, (float)SystemDefaultCuePreWait)
               && Mathf.IsEqualApprox((float)CueDefaultPostWait, (float)SystemDefaultCuePostWait)
               && CueDefaultFollow == SystemDefaultCueFollow
               && CueDefaultColor.IsEqualApprox(SystemDefaultCueColor)
               && CueDefaultArmed == SystemDefaultCueArmed
               && CueDefaultSkipIfDisarmed == SystemDefaultCueSkipIfDisarmed
               && CueDefaultOnlyOneActiveInstance == SystemDefaultCueOnlyOneActiveInstance;
    }

    // ── Audio component defaults ───────────────────────────────────────────

    /// <summary>
    /// Resets all audio component defaults to system factory values.
    /// </summary>
    public void ResetAudioDefaultsToSystem()
    {
        AudioDefaultVolume = SystemDefaultAudioVolume;
        AudioDefaultPan = SystemDefaultAudioPan;
        AudioDefaultLoop = SystemDefaultAudioLoop;
        AudioDefaultPlayCount = SystemDefaultAudioPlayCount;
        AudioDefaultFadeIn = SystemDefaultAudioFadeIn;
        AudioDefaultFadeOut = SystemDefaultAudioFadeOut;
        AudioDefaultOutputMode = SystemDefaultAudioOutputMode;
        AudioDefaultPatchId = -1;
        AudioDefaultDirectOutput = string.Empty;
    }

    /// <summary>
    /// Applies show audio defaults to a newly constructed audio component.
    /// Does not change file path, routing matrix, metadata, or in/out times.
    /// Includes default audio output assignment.
    /// </summary>
    /// <param name="comp">Audio component to configure.</param>
    public void ApplyAudioDefaults(AudioComponent comp)
    {
        if (comp == null) return;
        comp.Volume = Cue2.Media.Audio.AudioMixMatrix.ClampComponentGainLinear((float)AudioDefaultVolume);
        comp.Pan = AudioDefaultPan;
        comp.Loop = AudioDefaultLoop;
        comp.PlayCount = Math.Max(1, AudioDefaultPlayCount);
        comp.FadeInDuration = Math.Max(0.0, AudioDefaultFadeIn);
        comp.FadeOutDuration = Math.Max(0.0, AudioDefaultFadeOut);
        ApplyResolvedAudioOutput(
            AudioDefaultOutputMode,
            AudioDefaultPatchId,
            AudioDefaultDirectOutput,
            out var patch,
            out int patchId,
            out string direct);
        comp.Patch = patch;
        comp.PatchId = patchId;
        comp.DirectOutput = direct;
    }

    /// <summary>
    /// Serializes audio component defaults for showfile / history.
    /// </summary>
    public Dictionary CaptureAudioDefaultsDict()
    {
        return new Dictionary
        {
            ["Volume"] = AudioDefaultVolume,
            ["Pan"] = AudioDefaultPan,
            ["Loop"] = AudioDefaultLoop ? 1 : 0,
            ["PlayCount"] = AudioDefaultPlayCount,
            ["FadeIn"] = AudioDefaultFadeIn,
            ["FadeOut"] = AudioDefaultFadeOut,
            ["OutputMode"] = (int)AudioDefaultOutputMode,
            ["PatchId"] = AudioDefaultPatchId,
            ["DirectOutput"] = AudioDefaultDirectOutput ?? string.Empty
        };
    }

    /// <summary>
    /// Loads audio component defaults from a dictionary. Missing keys keep current values.
    /// </summary>
    /// <param name="data">Dictionary with Volume, Pan, Loop, PlayCount, FadeIn, FadeOut, output fields.</param>
    public void ApplyAudioDefaultsFromDict(Dictionary data)
    {
        if (data == null) return;

        if (TryGetSettingsValue(data, "Volume", out var v))
            AudioDefaultVolume = Cue2.Media.Audio.AudioMixMatrix.ClampComponentGainLinear((float)v.AsDouble());
        if (TryGetSettingsValue(data, "Pan", out v))
            AudioDefaultPan = Mathf.Clamp(v.AsSingle(), -1f, 1f);
        if (TryGetSettingsValue(data, "Loop", out v))
            AudioDefaultLoop = ReadBoolVariant(v);
        if (TryGetSettingsValue(data, "PlayCount", out v))
            AudioDefaultPlayCount = Math.Max(1, v.AsInt32());
        if (TryGetSettingsValue(data, "FadeIn", out v))
            AudioDefaultFadeIn = Math.Max(0.0, v.AsDouble());
        if (TryGetSettingsValue(data, "FadeOut", out v))
            AudioDefaultFadeOut = Math.Max(0.0, v.AsDouble());
        if (TryGetSettingsValue(data, "OutputMode", out v))
            AudioDefaultOutputMode = ParseAudioOutputMode(v.AsInt32());
        if (TryGetSettingsValue(data, "PatchId", out v))
            AudioDefaultPatchId = v.AsInt32();
        if (TryGetSettingsValue(data, "DirectOutput", out v))
            AudioDefaultDirectOutput = v.AsString() ?? string.Empty;
    }

    // ── Video component defaults ───────────────────────────────────────────

    /// <summary>
    /// Resets all video component defaults to system factory values.
    /// </summary>
    public void ResetVideoDefaultsToSystem()
    {
        VideoDefaultExpandMode = SystemDefaultVideoExpandMode;
        VideoDefaultStretchMode = SystemDefaultVideoStretchMode;
        VideoDefaultOpacity = SystemDefaultVideoOpacity;
        VideoDefaultLoop = SystemDefaultVideoLoop;
        VideoDefaultPlayCount = SystemDefaultVideoPlayCount;
        VideoDefaultUseAudio = SystemDefaultVideoUseAudio;
        VideoDefaultAudioVolume = SystemDefaultVideoAudioVolume;
        VideoDefaultPan = SystemDefaultVideoPan;
        VideoDefaultFadeIn = SystemDefaultVideoFadeIn;
        VideoDefaultFadeOut = SystemDefaultVideoFadeOut;
        VideoDefaultImageDuration = SystemDefaultVideoImageDuration;
        VideoDefaultOutputMode = SystemDefaultVideoOutputMode;
        VideoDefaultPatchId = -1;
        VideoDefaultDirectOutput = string.Empty;
        VideoDefaultTargetLayerMode = SystemDefaultVideoTargetLayerMode;
        VideoDefaultTargetLayerId = -1;
    }

    /// <summary>
    /// Applies show video defaults to a newly constructed video component.
    /// Does not change file path, routing matrix, or metadata.
    /// Includes default target layer and embedded-audio output.
    /// Still-image path still forces UseAudio/HasAudio off after this call.
    /// </summary>
    /// <param name="comp">Video component to configure.</param>
    public void ApplyVideoDefaults(VideoComponent comp)
    {
        if (comp == null) return;
        comp.TextureExpandMode = VideoDefaultExpandMode;
        comp.TextureStretchMode = VideoDefaultStretchMode;
        comp.Opacity = Mathf.Clamp(VideoDefaultOpacity, 0f, 1f);
        comp.Loop = VideoDefaultLoop;
        comp.PlayCount = Math.Max(1, VideoDefaultPlayCount);
        comp.UseAudio = VideoDefaultUseAudio;
        comp.AudioVolume = Cue2.Media.Audio.AudioMixMatrix.ClampComponentGainLinear(VideoDefaultAudioVolume);
        comp.Volume = comp.AudioVolume;
        comp.Pan = VideoDefaultPan;
        comp.FadeInDuration = Math.Max(0.0, VideoDefaultFadeIn);
        comp.FadeOutDuration = Math.Max(0.0, VideoDefaultFadeOut);
        comp.TargetLayerId = ResolveTargetLayerId(
            VideoDefaultTargetLayerMode, VideoDefaultTargetLayerId);
        ApplyResolvedAudioOutput(
            VideoDefaultOutputMode,
            VideoDefaultPatchId,
            VideoDefaultDirectOutput,
            out var patch,
            out int patchId,
            out string direct);
        comp.Patch = patch;
        comp.PatchId = patchId;
        comp.DirectOutput = direct;
        if (comp.IsImage)
        {
            double hold = Math.Max(0.0, VideoDefaultImageDuration);
            comp.Duration = hold;
            comp.TotalDuration = hold <= 0 ? -1.0 : hold * (comp.Loop ? 1 : Math.Max(1, comp.PlayCount));
            if (comp.Loop)
                comp.TotalDuration = -1.0;
            // Images never use embedded audio.
            comp.UseAudio = false;
            comp.HasAudio = false;
        }
    }

    /// <summary>
    /// Serializes video component defaults for showfile / history.
    /// </summary>
    public Dictionary CaptureVideoDefaultsDict()
    {
        return new Dictionary
        {
            ["ExpandMode"] = (int)VideoDefaultExpandMode,
            ["StretchMode"] = (int)VideoDefaultStretchMode,
            ["Opacity"] = VideoDefaultOpacity,
            ["Loop"] = VideoDefaultLoop ? 1 : 0,
            ["PlayCount"] = VideoDefaultPlayCount,
            ["UseAudio"] = VideoDefaultUseAudio ? 1 : 0,
            ["AudioVolume"] = VideoDefaultAudioVolume,
            ["Pan"] = VideoDefaultPan,
            ["FadeIn"] = VideoDefaultFadeIn,
            ["FadeOut"] = VideoDefaultFadeOut,
            ["ImageDuration"] = VideoDefaultImageDuration,
            ["OutputMode"] = (int)VideoDefaultOutputMode,
            ["PatchId"] = VideoDefaultPatchId,
            ["DirectOutput"] = VideoDefaultDirectOutput ?? string.Empty,
            ["TargetLayerMode"] = (int)VideoDefaultTargetLayerMode,
            ["TargetLayerId"] = VideoDefaultTargetLayerId
        };
    }

    /// <summary>
    /// Loads video component defaults from a dictionary. Missing keys keep current values.
    /// </summary>
    /// <param name="data">Dictionary of video default fields.</param>
    public void ApplyVideoDefaultsFromDict(Dictionary data)
    {
        if (data == null) return;

        if (TryGetSettingsValue(data, "ExpandMode", out var v)
            && TryParseEnum(v, out TextureRect.ExpandModeEnum expand))
        {
            VideoDefaultExpandMode = expand;
        }
        if (TryGetSettingsValue(data, "StretchMode", out v)
            && TryParseEnum(v, out TextureRect.StretchModeEnum stretch))
        {
            VideoDefaultStretchMode = stretch;
        }
        if (TryGetSettingsValue(data, "Opacity", out v))
            VideoDefaultOpacity = VideoComponent.ParseOpacity(v);
        if (TryGetSettingsValue(data, "Loop", out v))
            VideoDefaultLoop = ReadBoolVariant(v);
        if (TryGetSettingsValue(data, "PlayCount", out v))
            VideoDefaultPlayCount = Math.Max(1, v.AsInt32());
        if (TryGetSettingsValue(data, "UseAudio", out v))
            VideoDefaultUseAudio = ReadBoolVariant(v);
        if (TryGetSettingsValue(data, "AudioVolume", out v))
            VideoDefaultAudioVolume = Cue2.Media.Audio.AudioMixMatrix.ClampComponentGainLinear(v.AsSingle());
        if (TryGetSettingsValue(data, "Pan", out v))
            VideoDefaultPan = Mathf.Clamp(v.AsSingle(), -1f, 1f);
        if (TryGetSettingsValue(data, "FadeIn", out v))
            VideoDefaultFadeIn = Math.Max(0.0, v.AsDouble());
        if (TryGetSettingsValue(data, "FadeOut", out v))
            VideoDefaultFadeOut = Math.Max(0.0, v.AsDouble());
        if (TryGetSettingsValue(data, "ImageDuration", out v))
            VideoDefaultImageDuration = Math.Max(0.0, v.AsDouble());
        if (TryGetSettingsValue(data, "OutputMode", out v))
            VideoDefaultOutputMode = ParseAudioOutputMode(v.AsInt32());
        if (TryGetSettingsValue(data, "PatchId", out v))
            VideoDefaultPatchId = v.AsInt32();
        if (TryGetSettingsValue(data, "DirectOutput", out v))
            VideoDefaultDirectOutput = v.AsString() ?? string.Empty;
        if (TryGetSettingsValue(data, "TargetLayerMode", out v))
            VideoDefaultTargetLayerMode = ParseTargetLayerMode(v.AsInt32());
        if (TryGetSettingsValue(data, "TargetLayerId", out v))
            VideoDefaultTargetLayerId = v.AsInt32();
    }

    // ── Text component defaults ────────────────────────────────────────────

    /// <summary>
    /// Resets all text component defaults to system factory values.
    /// </summary>
    public void ResetTextDefaultsToSystem()
    {
        TextDefaultDuration = SystemDefaultTextDuration;
        TextDefaultOpacity = SystemDefaultTextOpacity;
        TextDefaultUseBbcode = SystemDefaultTextUseBbcode;
        TextDefaultFontSize = SystemDefaultTextFontSize;
        TextDefaultFontName = SystemDefaultTextFontName;
        TextDefaultFontColor = SystemDefaultTextFontColor;
        TextDefaultHAlign = SystemDefaultTextHAlign;
        TextDefaultVAlign = SystemDefaultTextVAlign;
        TextDefaultAutowrap = SystemDefaultTextAutowrap;
        TextDefaultMargins = SystemDefaultTextMargins;
        TextDefaultOutlineSize = SystemDefaultTextOutlineSize;
        TextDefaultOutlineColor = SystemDefaultTextOutlineColor;
        TextDefaultBackgroundEnabled = SystemDefaultTextBackgroundEnabled;
        TextDefaultBackgroundColor = SystemDefaultTextBackgroundColor;
        TextDefaultFadeIn = SystemDefaultTextFadeIn;
        TextDefaultFadeOut = SystemDefaultTextFadeOut;
        TextDefaultTargetLayerMode = SystemDefaultTextTargetLayerMode;
        TextDefaultTargetLayerId = -1;
    }

    /// <summary>
    /// Applies show text defaults to a newly constructed text component.
    /// Does not change content. Includes default target layer assignment.
    /// </summary>
    /// <param name="comp">Text component to configure.</param>
    public void ApplyTextDefaults(TextComponent comp)
    {
        if (comp == null) return;
        comp.Duration = Math.Max(0.0, TextDefaultDuration);
        comp.Opacity = Mathf.Clamp(TextDefaultOpacity, 0f, 1f);
        comp.UseBbcode = TextDefaultUseBbcode;
        comp.FontSize = Math.Max(1, TextDefaultFontSize);
        comp.FontName = TextDefaultFontName ?? string.Empty;
        comp.FontColor = TextDefaultFontColor;
        comp.HorizontalAlignment = TextDefaultHAlign;
        comp.VerticalAlignment = TextDefaultVAlign;
        comp.Autowrap = TextDefaultAutowrap;
        comp.Margins = Math.Max(0, TextDefaultMargins);
        comp.OutlineSize = Math.Max(0, TextDefaultOutlineSize);
        comp.OutlineColor = TextDefaultOutlineColor;
        comp.BackgroundEnabled = TextDefaultBackgroundEnabled;
        comp.BackgroundColor = TextDefaultBackgroundColor;
        comp.FadeInDuration = Math.Max(0.0, TextDefaultFadeIn);
        comp.FadeOutDuration = Math.Max(0.0, TextDefaultFadeOut);
        comp.TargetLayerId = ResolveTargetLayerId(
            TextDefaultTargetLayerMode, TextDefaultTargetLayerId);
        comp.RecalculateDuration();
    }

    /// <summary>
    /// Serializes text component defaults for showfile / history.
    /// </summary>
    public Dictionary CaptureTextDefaultsDict()
    {
        return new Dictionary
        {
            ["Duration"] = TextDefaultDuration,
            ["Opacity"] = TextDefaultOpacity,
            ["UseBbcode"] = TextDefaultUseBbcode ? 1 : 0,
            ["FontSize"] = TextDefaultFontSize,
            ["FontName"] = TextDefaultFontName ?? string.Empty,
            ["FontColor"] = TextDefaultFontColor.ToHtml(true),
            ["HAlign"] = (int)TextDefaultHAlign,
            ["VAlign"] = (int)TextDefaultVAlign,
            ["Autowrap"] = TextDefaultAutowrap ? 1 : 0,
            ["Margins"] = TextDefaultMargins,
            ["OutlineSize"] = TextDefaultOutlineSize,
            ["OutlineColor"] = TextDefaultOutlineColor.ToHtml(true),
            ["BackgroundEnabled"] = TextDefaultBackgroundEnabled ? 1 : 0,
            ["BackgroundColor"] = TextDefaultBackgroundColor.ToHtml(true),
            ["FadeIn"] = TextDefaultFadeIn,
            ["FadeOut"] = TextDefaultFadeOut,
            ["TargetLayerMode"] = (int)TextDefaultTargetLayerMode,
            ["TargetLayerId"] = TextDefaultTargetLayerId
        };
    }

    /// <summary>
    /// Loads text component defaults from a dictionary. Missing keys keep current values.
    /// </summary>
    /// <param name="data">Dictionary of text default fields.</param>
    public void ApplyTextDefaultsFromDict(Dictionary data)
    {
        if (data == null) return;

        if (TryGetSettingsValue(data, "Duration", out var v))
            TextDefaultDuration = Math.Max(0.0, v.AsDouble());
        if (TryGetSettingsValue(data, "Opacity", out v))
            TextDefaultOpacity = VideoComponent.ParseOpacity(v);
        if (TryGetSettingsValue(data, "UseBbcode", out v))
            TextDefaultUseBbcode = ReadBoolVariant(v);
        if (TryGetSettingsValue(data, "FontSize", out v))
            TextDefaultFontSize = Math.Max(1, v.AsInt32());
        if (TryGetSettingsValue(data, "FontName", out v))
            TextDefaultFontName = v.AsString() ?? string.Empty;
        if (TryGetSettingsValue(data, "FontColor", out v))
            TextDefaultFontColor = Color.FromString(v.AsString(), TextDefaultFontColor);
        if (TryGetSettingsValue(data, "HAlign", out v)
            && TryParseEnum(v, out HorizontalAlignment hAlign))
        {
            TextDefaultHAlign = hAlign;
        }
        if (TryGetSettingsValue(data, "VAlign", out v)
            && TryParseEnum(v, out VerticalAlignment vAlign))
        {
            TextDefaultVAlign = vAlign;
        }
        if (TryGetSettingsValue(data, "Autowrap", out v))
            TextDefaultAutowrap = ReadBoolVariant(v);
        if (TryGetSettingsValue(data, "Margins", out v))
            TextDefaultMargins = Math.Max(0, v.AsInt32());
        if (TryGetSettingsValue(data, "OutlineSize", out v))
            TextDefaultOutlineSize = Math.Max(0, v.AsInt32());
        if (TryGetSettingsValue(data, "OutlineColor", out v))
            TextDefaultOutlineColor = Color.FromString(v.AsString(), TextDefaultOutlineColor);
        if (TryGetSettingsValue(data, "BackgroundEnabled", out v))
            TextDefaultBackgroundEnabled = ReadBoolVariant(v);
        if (TryGetSettingsValue(data, "BackgroundColor", out v))
            TextDefaultBackgroundColor = Color.FromString(v.AsString(), TextDefaultBackgroundColor);
        if (TryGetSettingsValue(data, "FadeIn", out v))
            TextDefaultFadeIn = Math.Max(0.0, v.AsDouble());
        if (TryGetSettingsValue(data, "FadeOut", out v))
            TextDefaultFadeOut = Math.Max(0.0, v.AsDouble());
        if (TryGetSettingsValue(data, "TargetLayerMode", out v))
            TextDefaultTargetLayerMode = ParseTargetLayerMode(v.AsInt32());
        if (TryGetSettingsValue(data, "TargetLayerId", out v))
            TextDefaultTargetLayerId = v.AsInt32();
    }

    // ── Shared output / layer resolution for component defaults ────────────

    /// <summary>
    /// Resolves an audio output assignment from defaults mode + patch id / direct name.
    /// Preferred falls back to <see cref="GetPreferredAudioOutputPatch"/>; missing patch → none.
    /// </summary>
    public void ApplyResolvedAudioOutput(
        ComponentAudioOutputDefaultMode mode,
        int patchId,
        string directOutput,
        out AudioOutputPatch patch,
        out int resolvedPatchId,
        out string resolvedDirect)
    {
        patch = null;
        resolvedPatchId = -1;
        resolvedDirect = null;

        switch (mode)
        {
            case ComponentAudioOutputDefaultMode.None:
                return;

            case ComponentAudioOutputDefaultMode.Patch:
                if (patchId >= 0
                    && _audioOutputPatches.TryGetValue(patchId, out var byId)
                    && byId != null
                    && GodotObject.IsInstanceValid(byId))
                {
                    patch = byId;
                    resolvedPatchId = byId.Id;
                }
                return;

            case ComponentAudioOutputDefaultMode.Direct:
                if (!string.IsNullOrEmpty(directOutput))
                    resolvedDirect = directOutput;
                return;

            case ComponentAudioOutputDefaultMode.Preferred:
            default:
                var preferred = GetPreferredAudioOutputPatch();
                if (preferred != null)
                {
                    patch = preferred;
                    resolvedPatchId = preferred.Id;
                }
                return;
        }
    }

    /// <summary>
    /// Resolves a target layer id from defaults mode + stored layer id.
    /// FirstAvailable uses the first entry in <see cref="DisplaysManager.Layers"/>; missing layer → -1.
    /// </summary>
    /// <param name="mode">Default mode.</param>
    /// <param name="layerId">Stored layer id when mode is Layer.</param>
    /// <returns>Resolved layer id, or -1 for no output.</returns>
    public static int ResolveTargetLayerId(ComponentTargetLayerDefaultMode mode, int layerId)
    {
        switch (mode)
        {
            case ComponentTargetLayerDefaultMode.None:
                return -1;

            case ComponentTargetLayerDefaultMode.Layer:
                if (layerId >= 0 && DisplaysManager.GetLayerById(layerId) != null)
                    return layerId;
                // Missing layer: fall back to first available when possible.
                if (DisplaysManager.Layers != null && DisplaysManager.Layers.Count > 0)
                    return DisplaysManager.Layers[0].LayerId;
                return -1;

            case ComponentTargetLayerDefaultMode.FirstAvailable:
            default:
                if (DisplaysManager.Layers != null && DisplaysManager.Layers.Count > 0)
                    return DisplaysManager.Layers[0].LayerId;
                return -1;
        }
    }

    private static ComponentAudioOutputDefaultMode ParseAudioOutputMode(int value)
    {
        return TryParseEnum(value, out ComponentAudioOutputDefaultMode mode)
            ? mode
            : ComponentAudioOutputDefaultMode.Preferred;
    }

    private static ComponentTargetLayerDefaultMode ParseTargetLayerMode(int value)
    {
        return TryParseEnum(value, out ComponentTargetLayerDefaultMode mode)
            ? mode
            : ComponentTargetLayerDefaultMode.FirstAvailable;
    }

    /// <summary>
    /// Parses a numeric Variant into an enum, handling Godot long-backed enums
    /// (e.g. <see cref="TextureRect.ExpandModeEnum"/>) where <see cref="Enum.IsDefined"/>
    /// rejects a plain <see cref="int"/>.
    /// </summary>
    private static bool TryParseEnum<TEnum>(Variant value, out TEnum result) where TEnum : struct, Enum
    {
        result = default;
        try
        {
            long raw = value.VariantType switch
            {
                Variant.Type.Int => value.AsInt64(),
                Variant.Type.Float => (long)value.AsDouble(),
                Variant.Type.String => long.TryParse(value.AsString(), out long p) ? p : long.MinValue,
                _ => value.AsInt64()
            };
            if (raw == long.MinValue && value.VariantType == Variant.Type.String)
                return false;
            return TryParseEnum(raw, out result);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Parses an integer into an enum using the enum's actual underlying type
    /// (int vs long) so <see cref="Enum.IsDefined"/> does not throw.
    /// </summary>
    private static bool TryParseEnum<TEnum>(long value, out TEnum result) where TEnum : struct, Enum
    {
        result = default;
        try
        {
            Type enumType = typeof(TEnum);
            Type underlying = Enum.GetUnderlyingType(enumType);
            object boxed = Convert.ChangeType(value, underlying);
            if (!Enum.IsDefined(enumType, boxed))
                return false;
            result = (TEnum)Enum.ToObject(enumType, boxed);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Parses a 32-bit int into an enum (convenience for non-Variant call sites).
    /// </summary>
    private static bool TryParseEnum<TEnum>(int value, out TEnum result) where TEnum : struct, Enum
        => TryParseEnum((long)value, out result);

    /// <summary>
    /// TryGetValue that tolerates string / StringName keys after JSON history clones.
    /// </summary>
    private static bool TryGetSettingsValue(Dictionary data, string key, out Variant value)
    {
        value = default;
        if (data == null || string.IsNullOrEmpty(key)) return false;
        if (data.TryGetValue(key, out value)) return true;

        foreach (var k in data.Keys)
        {
            if (k.AsString() == key)
            {
                value = data[k];
                return true;
            }
        }
        return false;
    }

    private static bool ReadBoolVariant(Variant value)
    {
        return value.VariantType switch
        {
            Variant.Type.Bool => value.AsBool(),
            Variant.Type.Int => value.AsInt32() != 0,
            Variant.Type.Float => !Mathf.IsZeroApprox(value.AsSingle()),
            Variant.Type.String => value.AsString() is "1" or "true" or "True",
            _ => value.AsBool()
        };
    }
}
