// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System;
using Godot;
using Godot.Collections;

namespace Cue2.Domain.Cues;

/// <summary>
/// Live recording input on a cue. Mutually exclusive with <see cref="AudioComponent"/>.
/// </summary>
/// <remarks>
/// Duration matches a still image: a set hold time, or <c>0</c> to stay active until stopped.
/// Output, routing, volume, pan, and fades match a file audio component. Playback is not started from here yet.
/// </remarks>
public class AudioInputComponent : ICueComponent
{
    /// <summary>Save/load type id.</summary>
    public string Type => "AudioInput";

    /// <summary>Assigned input patch id. <c>-1</c> when none.</summary>
    public int InputPatchId { get; set; } = -1;

    /// <summary>Live input patch for <see cref="InputPatchId"/>.</summary>
    public AudioInputPatch InputPatch { get; set; }

    /// <summary>Output patch, when not using a direct device.</summary>
    public AudioOutputPatch Patch { get; set; }

    /// <summary>Output patch id. <c>-1</c> when none.</summary>
    public int PatchId { get; set; } = -1;

    /// <summary>Direct output device name, or null.</summary>
    public string DirectOutput { get; set; }

    /// <summary>Output routing matrix.</summary>
    public CuePatch Routing { get; set; }

    /// <summary>Hold time in seconds. <c>0</c> means until stopped.</summary>
    public double Duration { get; set; }

    /// <summary>Shell timing. <c>-1</c> when <see cref="Duration"/> is 0.</summary>
    public double TotalDuration { get; set; } = -1;

    /// <summary>Linear volume. 1 is unity.</summary>
    public double Volume { get; set; } = 1;

    private float _pan;

    /// <summary>Stereo pan, −1…+1.</summary>
    public float Pan
    {
        get => _pan;
        set => _pan = Math.Clamp(value, -1f, 1f);
    }

    /// <summary>Fade-in seconds.</summary>
    public double FadeInDuration { get; set; }

    /// <summary>Amplitude shape used for <see cref="FadeInDuration"/>.</summary>
    public FadeCurveType FadeInCurve { get; set; } = FadeCurveType.Linear;

    /// <summary>Fade-out seconds.</summary>
    public double FadeOutDuration { get; set; }

    /// <summary>Amplitude shape used for <see cref="FadeOutDuration"/>.</summary>
    public FadeCurveType FadeOutCurve { get; set; } = FadeCurveType.Linear;

    /// <summary>
    /// Sets <see cref="TotalDuration"/> from <see cref="Duration"/>.
    /// </summary>
    /// <returns>The segment duration.</returns>
    public double RecalculateDuration()
    {
        if (Duration <= 0)
        {
            Duration = 0;
            TotalDuration = -1;
        }
        else
        {
            TotalDuration = Duration;
        }

        return Duration;
    }

    /// <inheritdoc />
    public Dictionary GetData()
    {
        var data = new Dictionary
        {
            { "InputPatchId", InputPatch?.Id ?? InputPatchId },
            { "PatchId", Patch?.Id ?? PatchId },
            { "DirectOutput", DirectOutput ?? string.Empty },
            { "Duration", Duration },
            { "Volume", Volume },
            { "Pan", Pan },
            { "FadeInDuration", FadeInDuration },
            { "FadeInCurve", (int)FadeInCurve },
            { "FadeOutDuration", FadeOutDuration },
            { "FadeOutCurve", (int)FadeOutCurve }
        };
        if (Routing != null)
            data.Add("Routing", Routing.GetData());
        return data;
    }

    /// <inheritdoc />
    public void LoadFromData(Dictionary data)
    {
        if (data == null)
            return;

        InputPatchId = data.ContainsKey("InputPatchId") ? data["InputPatchId"].AsInt32() : -1;
        InputPatch = null;
        PatchId = data.ContainsKey("PatchId") ? data["PatchId"].AsInt32() : -1;
        Patch = null;
        if (data.ContainsKey("DirectOutput") && data["DirectOutput"].VariantType != Variant.Type.Nil)
        {
            string direct = data["DirectOutput"].AsString();
            DirectOutput = string.IsNullOrEmpty(direct) ? null : direct;
        }
        else
        {
            DirectOutput = null;
        }

        Duration = data.ContainsKey("Duration") ? data["Duration"].AsDouble() : 0;
        Volume = data.ContainsKey("Volume") ? data["Volume"].AsSingle() : 1f;
        Pan = data.ContainsKey("Pan") ? data["Pan"].AsSingle() : 0f;
        FadeInDuration = data.ContainsKey("FadeInDuration") ? data["FadeInDuration"].AsDouble() : 0;
        FadeInCurve = data.ContainsKey("FadeInCurve")
            ? FadeCurve.FromInt(data["FadeInCurve"].AsInt32())
            : FadeCurveType.Linear;
        FadeOutDuration = data.ContainsKey("FadeOutDuration") ? data["FadeOutDuration"].AsDouble() : 0;
        FadeOutCurve = data.ContainsKey("FadeOutCurve")
            ? FadeCurve.FromInt(data["FadeOutCurve"].AsInt32())
            : FadeCurveType.Linear;
        if (data.ContainsKey("Routing") && data["Routing"].VariantType == Variant.Type.Dictionary)
        {
            Routing = new CuePatch();
            Routing.LoadFromData(data["Routing"].AsGodotDictionary());
        }
        else
        {
            Routing = null;
        }

        RecalculateDuration();
    }
}
