// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Godot.Collections;

namespace Cue2.Domain.ShowSettings;

/// <summary>
/// Categories and file-format helpers for exporting/importing show settings
/// independently of the session (.c2) showfile.
/// </summary>
/// <remarks>
/// Filter labels and ids match the show-scoped Settings tree item names exactly
/// (English menu keys). Keyboard Input Map and Cue2 Preferences live in user://
/// and are intentionally excluded — this format only covers show-scoped settings.
/// Parent-only tree headers (Connections) have no filter. Runtime-only controls
/// (master mute, output disable/blackout) are not serialized.
/// </remarks>
public static class SettingsExport
{
    /// <summary>Marker stored in the JSON root so loaders can reject unrelated files.</summary>
    public const string FormatId = "cue2-settings";

    /// <summary>Current on-disk format version.</summary>
    public const int FormatVersion = 1;

    /// <summary>File extension without the leading dot.</summary>
    public const string FileExtension = "c2settings";

    /// <summary>Godot FileDialog filter string.</summary>
    public const string FileDialogFilter = "*.c2settings ; Cue2 Settings";

    /// <summary>
    /// One user-facing filter category mapped to one or more <see cref="Settings.GetData"/> keys.
    /// </summary>
    public readonly struct Category
    {
        /// <summary>
        /// Stable id stored in export documents and used as UI checkbox metadata.
        /// Matches the Settings tree English menu key (same as <see cref="Label"/>).
        /// </summary>
        public string Id { get; }

        /// <summary>Label shown in the filter dropdown (Settings tree name).</summary>
        public string Label { get; }

        /// <summary>Settings serialization keys included when this category is selected.</summary>
        public string[] Keys { get; }

        /// <summary>
        /// Creates a filter category.
        /// </summary>
        /// <param name="id">Stable category id (Settings tree English name).</param>
        /// <param name="label">UI label (must match the Settings tree item text).</param>
        /// <param name="keys">Settings keys to include.</param>
        public Category(string id, string label, params string[] keys)
        {
            Id = id;
            Label = label;
            Keys = keys ?? System.Array.Empty<string>();
        }
    }

    /// <summary>
    /// All selectable export/import categories.
    /// Order and names match the show-scoped Settings tree (excluding user prefs and stubs).
    /// </summary>
    public static readonly Category[] Categories =
    {
        // Settings → General (plus related show scalars not currently on that panel)
        new("General", "General",
            "GoScale", "CueListScale", "WaveformResolution", "StopFadeDuration",
            "DoubleGoProtection",
            "MediaBackupEnabled", "MultiEditEnabled", "SelectNewCues", "ShowTimelineWaveforms",
            "ShowMode"),

        // Settings → Audio
        new("Audio", "Audio",
            "AudioLatencyMode", "AudioDeclickMs", "AudioMasterVolume",
            "AudioOutputMaxDb", "AudioOutputMinDb"),

        // Settings → Audio → Audio Output Patch
        new("Audio Output Patch", "Audio Output Patch",
            "AudioPatch", "AudioDevices"),

        // Settings → Video/Image (general video output panel; not Canvas topology)
        new("Video/Image", "Video/Image",
            "OutputBackgroundColor", "VideoQualityMode", "VideoPreviewQuality", "OutputVSyncMode"),

        // Settings → Video/Image → Canvas Editor
        new("Canvas Editor", "Canvas Editor",
            "Displays"),

        // Settings → Connections → …
        // Cue Lights not shipped in v1 — re-enable with the tree item.
        // new("Cue Lights", "Cue Lights",
        //     "CueLights", "CueLightIdleColour", "CueLightGoColour",
        //     "CueLightStandbyColour", "CueLightCountInColour", "CueLightBrightness"),
        new("OSC Connections", "OSC Connections", "OscConnections"),
        new("OSC Listener", "OSC Listener", "OscListen"),
        new("OSC Input Map", "OSC Input Map", "OscInputMap"),
        new("MIDI", "MIDI", "Midi"),
        new("MIDI Input Map", "MIDI Input Map", "MidiInputMap"),

        // Settings → Cue Defaults (+ component default children)
        new("Cue Defaults", "Cue Defaults", "CueDefaults"),
        new("Audio Defaults", "Audio Defaults", "AudioDefaults"),
        new("Video Defaults", "Video Defaults", "VideoDefaults"),
        new("Text Defaults", "Text Defaults", "TextDefaults"),
    };

    /// <summary>
    /// Pre-rename category ids from early .c2settings files / tools → current tree-name ids.
    /// Keys already in the file still load; this only maps category selection ids.
    /// </summary>
    private static readonly System.Collections.Generic.Dictionary<string, string> LegacyCategoryIdMap =
        new(StringComparer.Ordinal)
        {
            ["AudioPatch"] = "Audio Output Patch",
            ["Displays"] = "Canvas Editor",
            ["VideoOutput"] = "Video/Image",
            ["OscConnections"] = "OSC Connections",
            ["OscListen"] = "OSC Listener",
            ["OscInputMap"] = "OSC Input Map",
            ["Midi"] = "MIDI",
            ["MidiInputMap"] = "MIDI Input Map",
        };

    /// <summary>
    /// Resolves selected category ids to a de-duplicated ordered list of settings keys.
    /// Unknown ids are ignored. Empty or null selection yields an empty array (caller should treat as no-op).
    /// Accepts current tree-name ids and legacy ids from earlier export builds.
    /// </summary>
    /// <param name="categoryIds">Selected category ids (e.g. "Audio Output Patch"). Pass all category ids for a full export.</param>
    /// <returns>Settings keys suitable for <see cref="Settings.CaptureHistorySlice"/>.</returns>
    public static string[] ResolveKeys(IEnumerable<string> categoryIds)
    {
        if (categoryIds == null)
            return System.Array.Empty<string>();

        var selected = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in categoryIds)
        {
            if (string.IsNullOrWhiteSpace(raw))
                continue;

            string id = raw.Trim();

            // Early exports used a single "CueDefaults" category for shell + component defaults.
            if (string.Equals(id, "CueDefaults", StringComparison.Ordinal))
            {
                selected.Add("Cue Defaults");
                selected.Add("Audio Defaults");
                selected.Add("Video Defaults");
                selected.Add("Text Defaults");
                continue;
            }

            if (LegacyCategoryIdMap.TryGetValue(id, out var mapped))
                id = mapped;
            selected.Add(id);
        }

        if (selected.Count == 0)
            return System.Array.Empty<string>();

        var keys = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var cat in Categories)
        {
            if (!selected.Contains(cat.Id))
                continue;
            foreach (var key in cat.Keys)
            {
                if (seen.Add(key))
                    keys.Add(key);
            }
        }

        return keys.ToArray();
    }

    /// <summary>
    /// Builds a root document dictionary for writing a .c2settings file.
    /// </summary>
    /// <param name="categoryIds">Categories the user chose to include.</param>
    /// <param name="settingsSlice">Payload from <see cref="Settings.CaptureHistorySlice"/>.</param>
    /// <returns>Dictionary ready for <see cref="Json.Stringify"/>.</returns>
    public static Dictionary BuildDocument(IEnumerable<string> categoryIds, Dictionary settingsSlice)
    {
        var cats = new Godot.Collections.Array();
        if (categoryIds != null)
        {
            foreach (var id in categoryIds.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal))
                cats.Add(id);
        }

        return new Dictionary
        {
            ["format"] = FormatId,
            ["version"] = FormatVersion,
            ["categories"] = cats,
            ["settings"] = settingsSlice ?? new Dictionary()
        };
    }

    /// <summary>
    /// Parses and validates a settings export document.
    /// </summary>
    /// <param name="root">Parsed JSON root.</param>
    /// <param name="settings">On success, the nested settings dictionary.</param>
    /// <param name="categoriesInFile">Category ids recorded when the file was saved (may be empty for older/hand-edited files).</param>
    /// <param name="error">Readable failure reason when false is returned.</param>
    /// <returns>True when the document is a usable Cue2 settings export.</returns>
    public static bool TryParseDocument(Dictionary root, out Dictionary settings,
        out string[] categoriesInFile, out string error)
    {
        settings = null;
        categoriesInFile = System.Array.Empty<string>();
        error = null;

        if (root == null || root.Count == 0)
        {
            error = "File is empty or not a valid JSON object.";
            return false;
        }

        // Accept either the dedicated export format or a bare settings table (advanced / hand-made).
        bool hasFormat = TryGetString(root, "format", out var format);
        if (hasFormat)
        {
            if (!string.Equals(format, FormatId, StringComparison.Ordinal))
            {
                error = $"Unsupported settings file format '{format}'.";
                return false;
            }

            if (TryGetVariant(root, "settings", out var settingsVar) &&
                settingsVar.VariantType == Variant.Type.Dictionary)
            {
                settings = settingsVar.AsGodotDictionary();
            }
            else
            {
                error = "Settings file is missing the 'settings' object.";
                return false;
            }

            if (TryGetVariant(root, "categories", out var catsVar) &&
                catsVar.VariantType == Variant.Type.Array)
            {
                var list = new List<string>();
                foreach (var item in catsVar.AsGodotArray())
                {
                    string id = item.AsString();
                    if (!string.IsNullOrWhiteSpace(id))
                        list.Add(id);
                }
                categoriesInFile = list.ToArray();
            }
        }
        else
        {
            // Bare settings dictionary (e.g. the "settings" block copied from a showfile dump).
            settings = root;
        }

        if (settings == null || settings.Count == 0)
        {
            error = "Settings file contains no settings data.";
            return false;
        }

        return true;
    }

    /// <summary>
    /// Filters a loaded settings dictionary to only the keys belonging to the selected categories.
    /// Keys present in the file but not selected are dropped; selected keys missing from the file are ignored.
    /// </summary>
    /// <param name="fileSettings">Settings payload from the file.</param>
    /// <param name="categoryIds">User-selected import categories.</param>
    /// <returns>Subset dictionary safe to pass to <see cref="Settings.ApplyPartialFromHistory"/>.</returns>
    public static Dictionary FilterSettingsByCategories(Dictionary fileSettings, IEnumerable<string> categoryIds)
    {
        var result = new Dictionary();
        if (fileSettings == null || fileSettings.Count == 0)
            return result;

        var keys = ResolveKeys(categoryIds);
        if (keys.Length == 0)
            return result;

        var keySet = new HashSet<string>(keys, StringComparer.Ordinal);
        foreach (var kvp in fileSettings)
        {
            string key = kvp.Key.AsString();
            if (keySet.Contains(key))
                result[key] = kvp.Value;
        }

        return result;
    }

    private static bool TryGetString(Dictionary data, string key, out string value)
    {
        value = null;
        if (!TryGetVariant(data, key, out var v))
            return false;
        value = v.AsString();
        return !string.IsNullOrEmpty(value);
    }

    private static bool TryGetVariant(Dictionary data, string key, out Variant value)
    {
        value = default;
        if (data == null || string.IsNullOrEmpty(key))
            return false;
        if (data.TryGetValue(key, out value))
            return true;

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
}
