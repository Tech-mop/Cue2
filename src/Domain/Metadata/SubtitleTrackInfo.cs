// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

namespace Cue2.Domain.Metadata;

/// <summary>
/// Describes a subtitle / closed-caption stream discovered in a media file or sidecar.
/// </summary>
public class SubtitleTrackInfo
{
    /// <summary>
    /// FFmpeg stream index within the container. For external sidecar files this is the
    /// stream index inside the sidecar (usually 0), not the video container.
    /// </summary>
    public int StreamIndex { get; set; } = -1;

    /// <summary>Codec short name (e.g. "subrip", "ass", "hdmv_pgs_subtitle").</summary>
    public string Codec { get; set; } = string.Empty;

    /// <summary>ISO language tag when present (e.g. "eng").</summary>
    public string Language { get; set; } = string.Empty;

    /// <summary>Optional stream title from container metadata, or sidecar file name.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// True when the track is a text-based format we can render via the Text component.
    /// Bitmap tracks (PGS, DVD, DVB) are listed but not selectable for text linking.
    /// </summary>
    public bool IsTextBased { get; set; }

    /// <summary>
    /// Absolute path to an external sidecar subtitle file (.srt, .vtt, .ass, …).
    /// Empty when the track is embedded in the video container.
    /// </summary>
    public string ExternalFilePath { get; set; } = string.Empty;

    /// <summary>True when this track is loaded from a sidecar file next to the video.</summary>
    public bool IsExternal => !string.IsNullOrWhiteSpace(ExternalFilePath);

    /// <summary>
    /// Compact label for inspector option buttons.
    /// </summary>
    public string DisplayName
    {
        get
        {
            string lang = string.IsNullOrWhiteSpace(Language) ? "" : Language.Trim();
            string title = string.IsNullOrWhiteSpace(Title) ? "" : Title.Trim();
            string codec = string.IsNullOrWhiteSpace(Codec) ? "subtitle" : Codec.Trim();
            string source = IsExternal ? "file" : "embedded";

            if (IsExternal && !string.IsNullOrEmpty(title))
                return $"{title} ({codec})";
            if (!string.IsNullOrEmpty(title) && !string.IsNullOrEmpty(lang))
                return $"{title} ({lang}) — {codec}";
            if (!string.IsNullOrEmpty(title))
                return $"{title} — {codec}";
            if (!string.IsNullOrEmpty(lang))
                return $"{lang} — {codec} [{source}]";
            if (IsExternal)
                return $"{codec} [sidecar]";
            return $"Stream {StreamIndex} — {codec}";
        }
    }
}
