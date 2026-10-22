// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System;
using Godot;
using Cue2.Services;

namespace Cue2.UI.Utilities;

/// <summary>
/// Shared styling for inspector media URL fields when the referenced file is missing.
/// </summary>
public static class InspectorMediaUrlStyle
{
    /// <summary>
    /// Builds a red-bordered style box for a missing-file URL field.
    /// </summary>
    public static StyleBoxFlat CreateMissingStyle()
    {
        var box = new StyleBoxFlat();
        box.BgColor = new Color(0.12f, 0.08f, 0.08f, 0.9f);
        box.SetBorderWidthAll(1);
        box.BorderColor = GlobalStyles.Danger;
        box.SetCornerRadiusAll(2);
        box.ContentMarginLeft = 6;
        box.ContentMarginRight = 6;
        box.ContentMarginTop = 4;
        box.ContentMarginBottom = 4;
        return box;
    }

    /// <summary>
    /// Applies or clears missing-file styling on a LineEdit URL field.
    /// </summary>
    /// <param name="edit">Target LineEdit.</param>
    /// <param name="missingStyle">Prebuilt red border style (caller owns lifetime).</param>
    /// <param name="missing">True to show missing state.</param>
    /// <param name="tooltip">Tooltip when missing (defaults to "File Missing").</param>
    public static void Apply(LineEdit edit, StyleBoxFlat missingStyle, bool missing, string tooltip = null)
    {
        if (edit == null)
            return;

        if (missing)
        {
            if (missingStyle != null)
            {
                edit.AddThemeStyleboxOverride("normal", missingStyle);
                edit.AddThemeStyleboxOverride("focus", missingStyle);
            }

            edit.AddThemeColorOverride("font_color", GlobalStyles.Danger);
            edit.AddThemeColorOverride("font_uneditable_color", GlobalStyles.Danger);
            edit.TooltipText = string.IsNullOrEmpty(tooltip) ? UiLocalizer.T("File Missing") : tooltip;
        }
        else
        {
            edit.RemoveThemeStyleboxOverride("normal");
            edit.RemoveThemeStyleboxOverride("focus");
            edit.RemoveThemeColorOverride("font_color");
            edit.RemoveThemeColorOverride("font_uneditable_color");
            edit.TooltipText = string.Empty;
        }
    }

    /// <summary>
    /// Trims a typed media URL. Surrounding quotes are removed, and a <c>file://</c> URI
    /// becomes a local filesystem path.
    /// </summary>
    /// <param name="text">Raw LineEdit text.</param>
    /// <returns>Path suitable for <see cref="GlobalData.ResolveMediaPath"/>, or empty.</returns>
    public static string NormalizeTypedPath(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        string path = text.Trim();
        if (path.Length >= 2
            && ((path[0] == '"' && path[^1] == '"') || (path[0] == '\'' && path[^1] == '\'')))
        {
            path = path.Substring(1, path.Length - 2).Trim();
        }

        if (path.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
            && Uri.TryCreate(path, UriKind.Absolute, out Uri uri)
            && uri.IsFile)
        {
            path = uri.LocalPath;
        }

        return path;
    }

    /// <summary>
    /// True when a stored cue path and a typed path resolve to the same file.
    /// </summary>
    /// <param name="storedPath">Path currently on the component.</param>
    /// <param name="typedPath">Path from the URL field, already normalized.</param>
    /// <param name="sessionDir">Show session directory, or null.</param>
    public static bool SameResolvedFile(string storedPath, string typedPath, string sessionDir)
    {
        if (string.IsNullOrWhiteSpace(storedPath) || string.IsNullOrWhiteSpace(typedPath))
            return false;

        string stored = MediaPaths.Resolve(storedPath, sessionDir);
        string typed = MediaPaths.Resolve(typedPath, sessionDir);
        return string.Equals(stored, typed, StringComparison.OrdinalIgnoreCase);
    }
}
