// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

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
            try
            {
                var italic = new SystemFont();
                italic.FontNames = new[] { "Segoe UI", "Arial", "Helvetica", "sans-serif" };
                italic.FontItalic = true;
                edit.AddThemeFontOverride("font", italic);
            }
            catch
            {
                // Optional: theme default font is fine
            }

            edit.TooltipText = string.IsNullOrEmpty(tooltip) ? "File Missing" : tooltip;
        }
        else
        {
            edit.RemoveThemeStyleboxOverride("normal");
            edit.RemoveThemeStyleboxOverride("focus");
            edit.RemoveThemeColorOverride("font_color");
            edit.RemoveThemeColorOverride("font_uneditable_color");
            edit.RemoveThemeFontOverride("font");
            edit.TooltipText = string.Empty;
        }
    }
}
