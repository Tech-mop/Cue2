// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System;
using Cue2.Domain.Cuelist;
using Cue2.Domain.Playback;
using Cue2.Domain.Devices;
using Cue2.Domain.ShowSettings;
using Cue2.Domain.Metadata;
using Cue2.Domain.Cues;
using Cue2.Domain.Connections;
using Cue2.Domain.Library;
using Cue2.Domain.Commands;
using Cue2.Services;
using Cue2.UI.Utilities;
using Godot;
using AppSettings = Cue2.Domain.ShowSettings.Settings;

namespace Cue2.UI.Settings;

/// <summary>
/// Shared helpers for show-scoped component-default settings panels
/// (Audio / Video / Text / Cue Defaults).
/// </summary>
/// <remarks>
/// OptionButton metadata tokens:
/// audio — <c>preferred</c>, <c>none</c>, <c>patch:{id}</c>, <c>direct:{name}</c>;
/// layer — <c>first</c>, <c>none</c>, <c>layer:{id}</c>.
/// Field pattern: edit → <see cref="RecordDefaultsChange"/> → model write →
/// <see cref="UpdateResetButton"/>; reset button calls system default + full Sync.
/// </remarks>
public static class ComponentDefaultsUi
{
    /// <summary>
    /// Styles a per-field reset button (refresh icon) and wires its pressed handler.
    /// </summary>
    /// <param name="iconHost">Control that can resolve theme icons (the panel).</param>
    /// <param name="button">Reset button node (may be null).</param>
    /// <param name="onPressed">Handler when the user clicks reset.</param>
    public static void SetupResetButton(Control iconHost, Button button, Action onPressed)
    {
        if (button == null || onPressed == null)
            return;

        if (iconHost != null)
        {
            try
            {
                button.Icon = iconHost.GetThemeIcon("Refresh", "AtlasIcons");
            }
            catch
            {
                // Icon optional
            }
        }

        button.Pressed += onPressed;
    }

    /// <summary>
    /// Shows the reset button when the field is not at system default; sets tooltip text.
    /// </summary>
    /// <param name="button">Reset button (may be null).</param>
    /// <param name="atSystemDefault">True when current value matches factory default.</param>
    /// <param name="resetTooltip">Tooltip when visible (e.g. "Reset to default: 0s").</param>
    public static void UpdateResetButton(Button button, bool atSystemDefault, string resetTooltip)
    {
        if (button == null)
            return;

        button.Visible = !atSystemDefault;
        if (!atSystemDefault && !string.IsNullOrEmpty(resetTooltip))
            button.TooltipText = resetTooltip;
    }

    /// <summary>
    /// True when UI sync or history restore should suppress edit handlers.
    /// </summary>
    public static bool ShouldSkipEdit(bool isSyncingUi, HistoryManager history)
    {
        return isSyncingUi || history?.IsRestoring == true;
    }

    /// <summary>
    /// Records a settings-slice history step for a defaults panel key
    /// (<c>AudioDefaults</c>, <c>VideoDefaults</c>, <c>TextDefaults</c>, <c>CueDefaults</c>).
    /// </summary>
    /// <param name="history">Show history manager.</param>
    /// <param name="description">Undo label.</param>
    /// <param name="settingsKey">Narrow history key for <see cref="HistoryManager.RecordSettingsChange"/>.</param>
    /// <param name="coalesceKey">Optional coalesce key for continuous edits (pan, spin, colour).</param>
    public static void RecordDefaultsChange(
        HistoryManager history,
        string description,
        string settingsKey,
        string coalesceKey = null)
    {
        if (history == null || history.IsRestoring || string.IsNullOrEmpty(settingsKey))
            return;
        history.RecordSettingsChange(description, coalesceKey, settingsKey);
    }

    /// <summary>
    /// Ends a coalesce session for a continuous defaults edit.
    /// </summary>
    public static void EndDefaultsCoalesce(HistoryManager history, string coalesceKey)
    {
        if (history == null || string.IsNullOrEmpty(coalesceKey))
            return;
        history.EndCoalesceSession(coalesceKey);
    }

    /// <summary>
    /// True when two doubles match within float epsilon (settings scalar compare).
    /// </summary>
    public static bool NearlyEqual(double a, double b) =>
        Mathf.IsEqualApprox((float)a, (float)b);

    /// <summary>
    /// True when two floats match within a small absolute epsilon.
    /// </summary>
    public static bool NearlyEqual(float a, float b, float epsilon = 1e-6f) =>
        Math.Abs(a - b) < epsilon;

    /// <summary>
    /// Adds an OptionButton item with integer metadata (expand/stretch/align enums).
    /// </summary>
    public static void AddOptionItem(OptionButton button, string label, int metadata)
    {
        if (button == null) return;
        UiLocalizer.AddTranslatedItem(button, label);
        button.SetItemMetadata(button.ItemCount - 1, metadata);
    }

    /// <summary>
    /// Selects the OptionButton item whose integer metadata matches <paramref name="metadata"/>.
    /// </summary>
    public static void SelectOptionByMetadata(OptionButton button, int metadata)
    {
        if (button == null) return;
        button.SetBlockSignals(true);
        try
        {
            for (int i = 0; i < button.ItemCount; i++)
            {
                if (button.GetItemMetadata(i).AsInt32() == metadata)
                {
                    button.Selected = i;
                    return;
                }
            }

            button.Selected = 0;
        }
        finally
        {
            button.SetBlockSignals(false);
        }
    }

    /// <summary>
    /// Parses a time LineEdit for defaults (fade / duration). Updates the field text on success
    /// or restores <paramref name="currentSeconds"/> on failure.
    /// </summary>
    /// <returns>True when a new distinct value should be applied.</returns>
    public static bool TryParseTimeDefault(
        LineEdit field,
        string text,
        double currentSeconds,
        GlobalSignals logSignals,
        string invalidLogMessage,
        out double newSeconds)
    {
        newSeconds = currentSeconds;
        if (field == null)
            return false;

        var formatted = UiUtilities.ParseAndFormatTime(text, out var seconds, out string labeled);
        if (string.IsNullOrEmpty(formatted))
        {
            logSignals?.EmitSignal(nameof(GlobalSignals.Log),
                invalidLogMessage ?? $"Invalid default time: {text}", 1);
            field.Text = UiUtilities.FormatTime(currentSeconds);
            return false;
        }

        field.Text = formatted;
        field.TooltipText = labeled;
        newSeconds = Math.Max(0.0, seconds);
        return !NearlyEqual(currentSeconds, newSeconds);
    }

    /// <summary>
    /// Common reset path: no-op if already at system default (still re-syncs UI);
    /// otherwise records history, applies system value, and re-syncs.
    /// </summary>
    /// <returns>True when a value was actually reset.</returns>
    public static bool TryResetField(
        bool isSyncingUi,
        HistoryManager history,
        string settingsKey,
        string resetDescription,
        bool atSystemDefault,
        Action applySystemDefault,
        Action syncSettings)
    {
        if (isSyncingUi || applySystemDefault == null || syncSettings == null)
            return false;

        if (atSystemDefault)
        {
            syncSettings();
            return false;
        }

        RecordDefaultsChange(history, resetDescription, settingsKey);
        applySystemDefault();
        syncSettings();
        return true;
    }

    /// <summary>
    /// Rebuilds an audio-output OptionButton from live patches and devices, then selects the stored default.
    /// </summary>
    public static void PopulateAudioOutputOption(
        OptionButton button,
        AppSettings settings,
        AudioDevices audioDevices,
        ComponentAudioOutputDefaultMode mode,
        int patchId,
        string directOutput)
    {
        if (button == null) return;

        button.SetBlockSignals(true);
        try
        {
            button.Clear();

            AddTokenItem(button, "Preferred (Default Patch)", "preferred");
            AddTokenItem(button, "No output", "none");

            if (settings != null)
            {
                foreach (var kv in settings.GetAudioOutputPatches())
                {
                    var patch = kv.Value;
                    if (patch == null || !GodotObject.IsInstanceValid(patch)) continue;
                    AddTokenItem(button, $"Patch: {patch.Name}", $"patch:{patch.Id}");
                }
            }

            if (audioDevices != null)
            {
                foreach (var name in audioDevices.GetAvailableAudioDeviceNames())
                {
                    if (string.IsNullOrEmpty(name)) continue;
                    AddTokenItem(button, $"Direct: {name}", $"direct:{name}");
                }
            }

            string want = EncodeAudioOutput(mode, patchId, directOutput);
            int selected = FindTokenIndex(button, want);

            // Missing stored patch/device — add a visible placeholder so the choice is not silently rewritten.
            if (selected < 0)
            {
                if (mode == ComponentAudioOutputDefaultMode.Patch && patchId >= 0)
                {
                    AddTokenItem(button, $"!!! Missing patch id {patchId}", $"patch:{patchId}");
                    selected = button.ItemCount - 1;
                }
                else if (mode == ComponentAudioOutputDefaultMode.Direct
                         && !string.IsNullOrEmpty(directOutput))
                {
                    AddTokenItem(button, $"!!! Missing: {directOutput}", $"direct:{directOutput}");
                    selected = button.ItemCount - 1;
                }
                else
                {
                    selected = 0; // Preferred
                }
            }

            button.Selected = selected;
        }
        finally
        {
            button.SetBlockSignals(false);
        }
    }

    /// <summary>
    /// Reads the selected audio-output default from an OptionButton.
    /// </summary>
    public static void ReadAudioOutputSelection(
        OptionButton button,
        out ComponentAudioOutputDefaultMode mode,
        out int patchId,
        out string directOutput)
    {
        mode = ComponentAudioOutputDefaultMode.Preferred;
        patchId = -1;
        directOutput = string.Empty;
        if (button == null || button.Selected < 0) return;

        string token = button.GetItemMetadata(button.Selected).AsString();
        DecodeAudioOutput(token, out mode, out patchId, out directOutput);
    }

    /// <summary>
    /// Rebuilds a target-layer OptionButton from live layers, then selects the stored default.
    /// </summary>
    public static void PopulateTargetLayerOption(
        OptionButton button,
        ComponentTargetLayerDefaultMode mode,
        int layerId)
    {
        if (button == null) return;

        button.SetBlockSignals(true);
        try
        {
            button.Clear();
            AddTokenItem(button, "First available layer", "first");
            AddTokenItem(button, "No output", "none");

            if (DisplaysManager.Layers != null)
            {
                foreach (var layer in DisplaysManager.Layers)
                {
                    if (layer == null) continue;
                    string name = string.IsNullOrEmpty(layer.LayerName)
                        ? $"Layer {layer.LayerId}"
                        : layer.LayerName;
                    AddTokenItem(button, name, $"layer:{layer.LayerId}");
                }
            }

            string want = EncodeTargetLayer(mode, layerId);
            int selected = FindTokenIndex(button, want);

            if (selected < 0)
            {
                if (mode == ComponentTargetLayerDefaultMode.Layer && layerId >= 0)
                {
                    AddTokenItem(button, $"!!! Missing layer {layerId}", $"layer:{layerId}");
                    selected = button.ItemCount - 1;
                }
                else
                {
                    selected = 0; // First available
                }
            }

            button.Selected = selected;
        }
        finally
        {
            button.SetBlockSignals(false);
        }
    }

    /// <summary>
    /// Reads the selected target-layer default from an OptionButton.
    /// </summary>
    public static void ReadTargetLayerSelection(
        OptionButton button,
        out ComponentTargetLayerDefaultMode mode,
        out int layerId)
    {
        mode = ComponentTargetLayerDefaultMode.FirstAvailable;
        layerId = -1;
        if (button == null || button.Selected < 0) return;

        string token = button.GetItemMetadata(button.Selected).AsString();
        DecodeTargetLayer(token, out mode, out layerId);
    }

    /// <summary>
    /// True when audio output defaults match system factory (Preferred, empty patch/direct).
    /// </summary>
    public static bool IsAudioOutputAtSystem(
        ComponentAudioOutputDefaultMode mode,
        int patchId,
        string directOutput)
    {
        return mode == ComponentAudioOutputDefaultMode.Preferred
               && patchId < 0
               && string.IsNullOrEmpty(directOutput);
    }

    /// <summary>
    /// True when target-layer defaults match system factory (First available).
    /// </summary>
    public static bool IsTargetLayerAtSystem(
        ComponentTargetLayerDefaultMode mode,
        int layerId)
    {
        return mode == ComponentTargetLayerDefaultMode.FirstAvailable && layerId < 0;
    }

    public static string EncodeAudioOutput(
        ComponentAudioOutputDefaultMode mode,
        int patchId,
        string directOutput)
    {
        return mode switch
        {
            ComponentAudioOutputDefaultMode.None => "none",
            ComponentAudioOutputDefaultMode.Patch => $"patch:{patchId}",
            ComponentAudioOutputDefaultMode.Direct => $"direct:{directOutput ?? string.Empty}",
            _ => "preferred"
        };
    }

    public static void DecodeAudioOutput(
        string token,
        out ComponentAudioOutputDefaultMode mode,
        out int patchId,
        out string directOutput)
    {
        mode = ComponentAudioOutputDefaultMode.Preferred;
        patchId = -1;
        directOutput = string.Empty;
        if (string.IsNullOrEmpty(token)) return;

        if (token == "none")
        {
            mode = ComponentAudioOutputDefaultMode.None;
            return;
        }

        if (token.StartsWith("patch:", StringComparison.Ordinal))
        {
            mode = ComponentAudioOutputDefaultMode.Patch;
            if (int.TryParse(token.AsSpan("patch:".Length), out int id))
                patchId = id;
            return;
        }

        if (token.StartsWith("direct:", StringComparison.Ordinal))
        {
            mode = ComponentAudioOutputDefaultMode.Direct;
            directOutput = token.Substring("direct:".Length);
            return;
        }

        mode = ComponentAudioOutputDefaultMode.Preferred;
    }

    public static string EncodeTargetLayer(ComponentTargetLayerDefaultMode mode, int layerId)
    {
        return mode switch
        {
            ComponentTargetLayerDefaultMode.None => "none",
            ComponentTargetLayerDefaultMode.Layer => $"layer:{layerId}",
            _ => "first"
        };
    }

    public static void DecodeTargetLayer(
        string token,
        out ComponentTargetLayerDefaultMode mode,
        out int layerId)
    {
        mode = ComponentTargetLayerDefaultMode.FirstAvailable;
        layerId = -1;
        if (string.IsNullOrEmpty(token)) return;

        if (token == "none")
        {
            mode = ComponentTargetLayerDefaultMode.None;
            return;
        }

        if (token.StartsWith("layer:", StringComparison.Ordinal))
        {
            mode = ComponentTargetLayerDefaultMode.Layer;
            if (int.TryParse(token.AsSpan("layer:".Length), out int id))
                layerId = id;
            return;
        }

        mode = ComponentTargetLayerDefaultMode.FirstAvailable;
    }

    private static void AddTokenItem(OptionButton button, string label, string token)
    {
        int index = button.ItemCount;
        button.AddItem(label);
        button.SetItemMetadata(index, token);
    }

    private static int FindTokenIndex(OptionButton button, string token)
    {
        for (int i = 0; i < button.ItemCount; i++)
        {
            if (button.GetItemMetadata(i).AsString() == token)
                return i;
        }
        return -1;
    }
}
