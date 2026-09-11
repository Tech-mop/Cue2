// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System;
using Cue2.Services;
using Godot;
using Godot.Collections;

namespace Cue2.Domain.Cues;

/// <summary>
/// Text overlay component that displays Godot text on a target video layer.
/// </summary>
/// <remarks>
/// Duration of 0 means hold until the cue/component is stopped (same semantics as still-image video).
/// Presentation uses <see cref="ActiveTextPlayback"/> with a <see cref="RichTextLabel"/> clipped to the layer rect.
/// </remarks>
public class TextComponent : ICueComponent
{
    /// <inheritdoc />
    public string Type => "Text";

    /// <summary>
    /// Source text shown on the output (plain or BBCode depending on <see cref="UseBbcode"/>).
    /// </summary>
    public string Content { get; set; } = string.Empty;

    /// <summary>
    /// When true, <see cref="Content"/> is interpreted as BBCode by the output RichTextLabel.
    /// </summary>
    public bool UseBbcode { get; set; }

    /// <summary>
    /// Target video layer id. <c>-1</c> means no output assigned.
    /// </summary>
    public int TargetLayerId { get; set; } = -1;

    /// <summary>
    /// Hold time in seconds. <c>0</c> means stay active until stopped.
    /// </summary>
    public double Duration { get; set; }

    /// <summary>
    /// Total play length. <c>-1</c> when duration is 0 (until stopped).
    /// </summary>
    public double TotalDuration { get; set; } = -1.0;

    /// <summary>
    /// Horizontal text alignment within the layer.
    /// </summary>
    public HorizontalAlignment HorizontalAlignment { get; set; } = HorizontalAlignment.Center;

    /// <summary>
    /// Vertical text alignment within the layer.
    /// </summary>
    public VerticalAlignment VerticalAlignment { get; set; } = VerticalAlignment.Center;

    /// <summary>
    /// Font size in pixels (theme override on the output label).
    /// </summary>
    public int FontSize { get; set; } = 48;

    /// <summary>
    /// System font family name (e.g. "Arial", "Segoe UI"). Empty = project/theme default.
    /// </summary>
    /// <remarks>
    /// Resolved at runtime via Godot <see cref="SystemFont"/> so shows stay portable
    /// across machines that share the same family name (with OS fallback if missing).
    /// </remarks>
    public string FontName { get; set; } = string.Empty;

    /// <summary>
    /// Optional custom font file path (.ttf/.otf). When set and loadable, takes priority over
    /// <see cref="FontName"/>. Empty = not used.
    /// </summary>
    public string FontPath { get; set; } = string.Empty;

    /// <summary>
    /// Primary text colour (alpha is independent of <see cref="Opacity"/>).
    /// </summary>
    public Color FontColor { get; set; } = Colors.White;

    /// <summary>
    /// Overall visual opacity multiplier (0–1). Used for fades and inspector opacity control.
    /// </summary>
    public float Opacity { get; set; } = 1f;

    /// <summary>
    /// When true, text wraps within the layer width (minus margins).
    /// </summary>
    public bool Autowrap { get; set; } = true;

    /// <summary>
    /// Uniform padding in pixels inside the layer rect.
    /// </summary>
    public int Margins { get; set; } = 16;

    /// <summary>
    /// Outline thickness in pixels (0 = none). Improves readability over video.
    /// </summary>
    public int OutlineSize { get; set; }

    /// <summary>
    /// Outline colour when <see cref="OutlineSize"/> &gt; 0.
    /// </summary>
    public Color OutlineColor { get; set; } = Colors.Black;

    /// <summary>
    /// When true, draws a solid colour panel behind the text within the layer.
    /// </summary>
    public bool BackgroundEnabled { get; set; }

    /// <summary>
    /// Background panel colour (typically semi-transparent dark).
    /// </summary>
    public Color BackgroundColor { get; set; } = new Color(0f, 0f, 0f, 0.55f);

    /// <summary>
    /// Fade-in duration in seconds at play start (0 = immediate).
    /// </summary>
    public double FadeInDuration { get; set; }

    /// <summary>
    /// Fade-out duration in seconds on stop (0 = immediate / use session stop fade).
    /// </summary>
    public double FadeOutDuration { get; set; }

    /// <summary>
    /// Recomputes <see cref="TotalDuration"/> from <see cref="Duration"/>.
    /// </summary>
    /// <returns>The segment <see cref="Duration"/> (0 means until stopped).</returns>
    public double RecalculateDuration()
    {
        if (Duration < 0)
            Duration = 0;

        TotalDuration = Duration <= 0 ? -1.0 : Duration;
        return Duration;
    }

    /// <inheritdoc />
    public Dictionary GetData()
    {
        return new Dictionary
        {
            { "Content", Content ?? string.Empty },
            { "UseBbcode", UseBbcode },
            { "TargetLayerId", TargetLayerId },
            { "Duration", Duration },
            { "HorizontalAlignment", (int)HorizontalAlignment },
            { "VerticalAlignment", (int)VerticalAlignment },
            { "FontSize", FontSize },
            { "FontName", FontName ?? string.Empty },
            { "FontPath", FontPath ?? string.Empty },
            { "FontColor", FontColor.ToHtml(true) },
            { "Opacity", Opacity },
            { "Autowrap", Autowrap },
            { "Margins", Margins },
            { "OutlineSize", OutlineSize },
            { "OutlineColor", OutlineColor.ToHtml(true) },
            { "BackgroundEnabled", BackgroundEnabled },
            { "BackgroundColor", BackgroundColor.ToHtml(true) },
            { "FadeInDuration", FadeInDuration },
            { "FadeOutDuration", FadeOutDuration },
        };
    }

    /// <inheritdoc />
    public void LoadFromData(Dictionary data)
    {
        if (data == null)
            return;

        Content = data.ContainsKey("Content") ? data["Content"].AsString() : string.Empty;
        UseBbcode = data.ContainsKey("UseBbcode") && data["UseBbcode"].AsBool();
        // Missing key: prefer first available layer is applied at AddTextComponent time; load keeps -1.
        TargetLayerId = data.ContainsKey("TargetLayerId") ? data["TargetLayerId"].AsInt32() : -1;
        Duration = data.ContainsKey("Duration") ? data["Duration"].AsDouble() : 0.0;
        HorizontalAlignment = data.ContainsKey("HorizontalAlignment")
            ? ParseEnum(data["HorizontalAlignment"], HorizontalAlignment.Center)
            : HorizontalAlignment.Center;
        VerticalAlignment = data.ContainsKey("VerticalAlignment")
            ? ParseEnum(data["VerticalAlignment"], VerticalAlignment.Center)
            : VerticalAlignment.Center;
        FontSize = data.ContainsKey("FontSize") ? Mathf.Max(1, data["FontSize"].AsInt32()) : 48;
        FontName = data.ContainsKey("FontName") ? data["FontName"].AsString() : string.Empty;
        FontPath = data.ContainsKey("FontPath") ? data["FontPath"].AsString() : string.Empty;
        // Legacy: if only FontPath stored a non-file family-like name, prefer FontName.
        if (string.IsNullOrWhiteSpace(FontName)
            && !string.IsNullOrWhiteSpace(FontPath)
            && !LooksLikeFontFilePath(FontPath))
        {
            FontName = FontPath;
            FontPath = string.Empty;
        }
        FontColor = ParseColor(data, "FontColor", Colors.White);
        Opacity = data.ContainsKey("Opacity")
            ? VideoComponent.ParseOpacity(data["Opacity"])
            : 1f;
        Autowrap = !data.ContainsKey("Autowrap") || data["Autowrap"].AsBool();
        Margins = data.ContainsKey("Margins") ? Mathf.Max(0, data["Margins"].AsInt32()) : 16;
        OutlineSize = data.ContainsKey("OutlineSize") ? Mathf.Max(0, data["OutlineSize"].AsInt32()) : 0;
        OutlineColor = ParseColor(data, "OutlineColor", Colors.Black);
        BackgroundEnabled = data.ContainsKey("BackgroundEnabled") && data["BackgroundEnabled"].AsBool();
        BackgroundColor = ParseColor(data, "BackgroundColor", new Color(0f, 0f, 0f, 0.55f));
        FadeInDuration = data.ContainsKey("FadeInDuration") ? Math.Max(0, data["FadeInDuration"].AsDouble()) : 0.0;
        FadeOutDuration = data.ContainsKey("FadeOutDuration") ? Math.Max(0, data["FadeOutDuration"].AsDouble()) : 0.0;

        RecalculateDuration();
    }

    /// <summary>
    /// Short label for the active cue bar (truncated content or "Text").
    /// </summary>
    /// <param name="maxChars">Maximum characters before ellipsis.</param>
    /// <returns>Display label.</returns>
    public string GetDisplayLabel(int maxChars = 32)
    {
        string text = (Content ?? string.Empty).Replace('\n', ' ').Trim();
        if (string.IsNullOrEmpty(text))
            return "Text";
        if (text.Length <= maxChars)
            return text;
        return text.Substring(0, Math.Max(1, maxChars - 1)) + "…";
    }

    /// <summary>
    /// Applies content, typography, and wrap settings to a <see cref="RichTextLabel"/>.
    /// </summary>
    /// <param name="label">Output or preview label.</param>
    /// <param name="fontScale">
    /// Multiplier for font size and outline (use canvas preview scale so text matches output proportion).
    /// </param>
    public void ApplyToRichTextLabel(RichTextLabel label, float fontScale = 1f)
    {
        if (label == null || !GodotObject.IsInstanceValid(label))
            return;

        float scale = fontScale > 1e-6f ? fontScale : 1f;

        label.BbcodeEnabled = UseBbcode;
        label.Text = Content ?? string.Empty;
        label.ScrollActive = false;
        label.FitContent = false;
        label.ClipContents = true;
        label.MouseFilter = Control.MouseFilterEnum.Ignore;

        label.HorizontalAlignment = HorizontalAlignment;
        label.VerticalAlignment = VerticalAlignment;
        label.AutowrapMode = Autowrap
            ? TextServer.AutowrapMode.WordSmart
            : TextServer.AutowrapMode.Off;

        int fontSize = Mathf.Max(1, Mathf.RoundToInt(FontSize * scale));
        label.AddThemeFontSizeOverride("normal_font_size", fontSize);
        label.AddThemeFontSizeOverride("bold_font_size", fontSize);
        label.AddThemeFontSizeOverride("italics_font_size", fontSize);
        label.AddThemeFontSizeOverride("bold_italics_font_size", fontSize);
        label.AddThemeFontSizeOverride("mono_font_size", fontSize);

        ApplyFontThemeOverrides(label);

        label.AddThemeColorOverride("default_color", FontColor);

        int outline = Mathf.Max(0, Mathf.RoundToInt(OutlineSize * scale));
        label.AddThemeConstantOverride("outline_size", outline);
        if (outline > 0)
            label.AddThemeColorOverride("font_outline_color", OutlineColor);
    }

    /// <summary>
    /// True when a non-default font (system family or file) is configured.
    /// </summary>
    public bool HasCustomFont =>
        !string.IsNullOrWhiteSpace(FontName) || !string.IsNullOrWhiteSpace(FontPath);

    /// <summary>
    /// Builds the runtime font for this component, or null for the theme default.
    /// </summary>
    /// <param name="weight">Font weight (400 regular, 700 bold).</param>
    /// <param name="italic">Whether to request an italic face.</param>
    /// <returns>A <see cref="Font"/> resource, or null to use theme defaults.</returns>
    public Font ResolveFont(int weight = 400, bool italic = false)
    {
        // File fonts take priority when a loadable path is set.
        if (!string.IsNullOrWhiteSpace(FontPath) && LooksLikeFontFilePath(FontPath))
        {
            try
            {
                if (System.IO.File.Exists(FontPath))
                {
                    var fileFont = new FontFile();
                    Error err = fileFont.LoadDynamicFont(FontPath);
                    if (err == Error.Ok)
                        return fileFont;
                    GD.PrintErr($"TextComponent:ResolveFont - LoadDynamicFont failed ({err}) for '{FontPath}'");
                }
            }
            catch (Exception ex)
            {
                GD.PrintErr($"TextComponent:ResolveFont - File font error: {ex.Message}");
            }
        }

        if (string.IsNullOrWhiteSpace(FontName))
            return null;

        try
        {
            var systemFont = new SystemFont();
            systemFont.FontNames = new[] { FontName.Trim() };
            systemFont.FontWeight = Mathf.Clamp(weight, 100, 999);
            systemFont.FontItalic = italic;
            return systemFont;
        }
        catch (Exception ex)
        {
            GD.PrintErr($"TextComponent:ResolveFont - SystemFont '{FontName}': {ex.Message}");
            return null;
        }
    }

    private void ApplyFontThemeOverrides(RichTextLabel label)
    {
        if (!HasCustomFont)
        {
            label.RemoveThemeFontOverride("normal_font");
            label.RemoveThemeFontOverride("bold_font");
            label.RemoveThemeFontOverride("italics_font");
            label.RemoveThemeFontOverride("bold_italics_font");
            label.RemoveThemeFontOverride("mono_font");
            return;
        }

        // File fonts: same face for all styles. System fonts: request weight/italic variants.
        bool fileFont = !string.IsNullOrWhiteSpace(FontPath) && LooksLikeFontFilePath(FontPath);
        Font regular = ResolveFont(400, false);
        if (regular == null)
        {
            label.RemoveThemeFontOverride("normal_font");
            label.RemoveThemeFontOverride("bold_font");
            label.RemoveThemeFontOverride("italics_font");
            label.RemoveThemeFontOverride("bold_italics_font");
            label.RemoveThemeFontOverride("mono_font");
            return;
        }

        Font bold = fileFont ? regular : (ResolveFont(700, false) ?? regular);
        Font italics = fileFont ? regular : (ResolveFont(400, true) ?? regular);
        Font boldItalics = fileFont ? regular : (ResolveFont(700, true) ?? bold);

        label.AddThemeFontOverride("normal_font", regular);
        label.AddThemeFontOverride("bold_font", bold);
        label.AddThemeFontOverride("italics_font", italics);
        label.AddThemeFontOverride("bold_italics_font", boldItalics);
        label.AddThemeFontOverride("mono_font", regular);
    }

    private static bool LooksLikeFontFilePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;
        string ext = System.IO.Path.GetExtension(path);
        return ext.Equals(".ttf", StringComparison.OrdinalIgnoreCase)
               || ext.Equals(".otf", StringComparison.OrdinalIgnoreCase)
               || ext.Equals(".ttc", StringComparison.OrdinalIgnoreCase)
               || ext.Equals(".woff", StringComparison.OrdinalIgnoreCase)
               || ext.Equals(".woff2", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Fills a control to its parent with uniform margins (anchors + offsets).
    /// </summary>
    /// <param name="control">Child control to pin.</param>
    /// <param name="margin">Padding in pixels (already scaled for preview if needed).</param>
    public static void ApplyFillWithMargins(Control control, float margin)
    {
        if (control == null || !GodotObject.IsInstanceValid(control))
            return;

        float m = Mathf.Max(0f, margin);
        control.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        control.OffsetLeft = m;
        control.OffsetTop = m;
        control.OffsetRight = -m;
        control.OffsetBottom = -m;
    }

    private static TEnum ParseEnum<TEnum>(Variant value, TEnum fallback) where TEnum : struct, Enum
    {
        return VideoComponent.ParseEnumVariant(value, fallback);
    }

    private static Color ParseColor(Dictionary data, string key, Color fallback)
    {
        if (data == null || !data.ContainsKey(key))
            return fallback;

        try
        {
            var v = data[key];
            if (v.VariantType == Variant.Type.String)
            {
                string html = v.AsString();
                if (!string.IsNullOrWhiteSpace(html))
                    return Color.FromString(html, fallback);
            }
            else if (v.VariantType == Variant.Type.Color)
            {
                return v.AsColor();
            }
        }
        catch (Exception ex)
        {
            GD.PrintErr($"TextComponent:ParseColor - {key}: {ex.Message}");
        }

        return fallback;
    }
}
