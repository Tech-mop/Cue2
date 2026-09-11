// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

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
using Cue2.Media.Audio;
using Cue2.UI.Utilities;
using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Cue2.UI.Inspectors;

/// <summary>
/// Inspector for displaying and editing cue timelines, including hierarchical children.
/// Features a fixed track sidebar, time ruler, cue-colored bars, optional waveforms,
/// playhead scrubbing, and play-from-playhead. Parent rows can collapse to hide descendants.
/// </summary>
/// <summary>
/// Partial: Nested CueBarWaveform, TimeGrid, Ruler helpers
/// </summary>
public partial class TimelineInspector
{
    private partial class CueBarWaveform : Control
    {
        public WaveformPeaks Peaks { get; set; }
        public float StartNorm { get; set; }
        public float EndNorm { get; set; } = 1f;
        public int PlayCount { get; set; } = 1;
        public Color WaveColor { get; set; } = GlobalStyles.LowColor1;
        public Color DividerColor { get; set; } = new Color(1f, 1f, 1f, 0.45f);

        public override void _Draw()
        {
            if (Peaks == null || Peaks.BinCount < 1) return;
            float width = Size.X;
            float height = Size.Y;
            if (width < 2f || height < 4f) return;

            float midY = height * 0.5f;
            float startN = Mathf.Clamp(StartNorm, 0f, 1f);
            float endN = Mathf.Clamp(EndNorm, startN + 1e-5f, 1f);
            int plays = Math.Max(1, PlayCount);

            int binCount = Peaks.BinCount;
            float peakScale = 0.001f;
            int binStart = (int)(startN * binCount);
            int binEnd = (int)Math.Ceiling(endN * binCount);
            binStart = Math.Clamp(binStart, 0, binCount - 1);
            binEnd = Math.Clamp(binEnd, binStart + 1, binCount);
            for (int i = binStart; i < binEnd; i++)
            {
                peakScale = Math.Max(peakScale, Math.Abs(Peaks.GetMin(i)));
                peakScale = Math.Max(peakScale, Math.Abs(Peaks.GetMax(i)));
            }
            peakScale = Math.Max(peakScale, 0.05f);

            float segmentWidth = width / plays;
            var color = WaveColor;

            for (int play = 0; play < plays; play++)
            {
                float playX0 = play * segmentWidth;
                float playW = segmentWidth;

                int playCols = Math.Max(1, (int)Math.Ceiling(playW));
                for (int c = 0; c < playCols; c++)
                {
                    float t = (c + 0.5f) / playCols;
                    float fileNorm = startN + t * (endN - startN);
                    int bin = (int)(fileNorm * binCount);
                    bin = Math.Clamp(bin, 0, binCount - 1);

                    float minVal = Mathf.Clamp(Peaks.GetMin(bin) / peakScale, -1f, 1f);
                    float maxVal = Mathf.Clamp(Peaks.GetMax(bin) / peakScale, -1f, 1f);

                    float yMax = midY - maxVal * (height * 0.45f);
                    float yMin = midY - minVal * (height * 0.45f);
                    if (yMin < yMax)
                        (yMin, yMax) = (yMax, yMin);
                    if (yMin - yMax < 1f)
                    {
                        yMax = midY - 0.5f;
                        yMin = midY + 0.5f;
                    }

                    float x = playX0 + (c + 0.5f) / playCols * playW;
                    if (x < -1 || x > width + 1) continue;
                    DrawLine(new Vector2(x, yMax), new Vector2(x, yMin), color, 1.2f);
                }

                // Divider at the start of each subsequent play
                if (play > 0)
                {
                    DrawLine(new Vector2(playX0, 1f), new Vector2(playX0, height - 1f), DividerColor, 1.5f);
                }
            }

            DrawLine(new Vector2(0, midY), new Vector2(width, midY), new Color(1, 1, 1, 0.1f), 1f);
        }
    }

    /// <summary>
    /// Background grid for the timeline content area (major/minor vertical lines).
    /// </summary>
    private partial class TimeGrid : Control
    {
        public float ZoomScale { get; set; } = 10f;
        public float ContentHeight { get; set; }

        public override void _Draw()
        {
            if (ZoomScale <= 0.001f) return;

            float h = Math.Max(Size.Y, ContentHeight);
            float w = Size.X;
            if (w < 2f || h < 2f) return;

            float targetPixelSpacing = 100.0f;
            float interval = (float)Mathf.Pow(10, Mathf.Round(Math.Log10(targetPixelSpacing / ZoomScale)));
            if (interval * ZoomScale < 50) interval *= 2;
            else if (interval * ZoomScale > 200) interval /= 2;

            float minor = interval / 4f;
            if (minor * ZoomScale < 8f)
                minor = interval / 2f;

            var majorColor = new Color(1f, 1f, 1f, 0.07f);
            var minorColor = new Color(1f, 1f, 1f, 0.03f);

            float tEnd = w / ZoomScale + interval;
            for (float t = 0; t <= tEnd; t += minor)
            {
                float x = t * ZoomScale;
                if (x < -1 || x > w + 1) continue;
                bool isMajor = Math.Abs(t / interval - Math.Round(t / interval)) < 1e-4;
                DrawLine(new Vector2(x, 0), new Vector2(x, h), isMajor ? majorColor : minorColor, 1f);
            }
        }
    }

    /// <summary>
    /// Custom control for rendering the timeline ruler with major/minor ticks, time labels, and playhead triangle.
    /// </summary>
    private partial class Ruler : Control
    {
        public float ZoomScale { get; set; }
        public float Offset { get; set; }
        /// <summary>Pixel offset of content origin (0 when sidebar is separate).</summary>
        public float ContentOriginX { get; set; }
        /// <summary>Playhead time in seconds (display timeline).</summary>
        public double PlayheadSeconds { get; set; }

        public override void _Draw()
        {
            float h = Size.Y;
            float w = Size.X;

            // Taller professional background
            DrawRect(new Rect2(0, 0, w, h), new Color(0.07f, 0.08f, 0.09f, 0.98f), true);
            DrawLine(new Vector2(0, h - 1), new Vector2(w, h - 1), new Color(0.3f, 0.32f, 0.34f, 0.8f), 1f);

            if (ZoomScale <= 0.001f) return;

            float targetPixelSpacing = 90.0f;
            float interval = (float)Mathf.Pow(10, Mathf.Round(Math.Log10(targetPixelSpacing / ZoomScale)));
            if (interval * ZoomScale < 45) interval *= 2;
            else if (interval * ZoomScale > 180) interval /= 2;

            float minor = interval / 4f;
            if (minor * ZoomScale < 6f)
                minor = interval / 2f;

            float tStart = (Offset - ContentOriginX) / ZoomScale;
            float tEnd = (Offset + w - ContentOriginX) / ZoomScale;
            if (tStart < 0) tStart = 0;

            float firstMinor = Mathf.Floor(tStart / minor) * minor;
            var font = ThemeDB.FallbackFont;

            for (float t = firstMinor; t <= tEnd + minor * 0.01f; t += minor)
            {
                if (t < -1e-4f) continue;
                float x = ContentOriginX + t * ZoomScale - Offset;
                if (x < -20 || x > w + 20) continue;

                bool isMajor = Math.Abs(t / interval - Math.Round(t / interval)) < 1e-3;
                float tickTop = isMajor ? h * 0.28f : h * 0.55f;
                var tickColor = isMajor
                    ? new Color(0.88f, 0.9f, 0.92f, 0.95f)
                    : new Color(0.55f, 0.58f, 0.6f, 0.7f);
                DrawLine(new Vector2(x, tickTop), new Vector2(x, h - 1), tickColor, isMajor ? 1.2f : 1f);

                if (isMajor)
                {
                    string labelText = FormatRulerTime(t);
                    DrawString(font, new Vector2(x + 3, h * 0.42f), labelText, HorizontalAlignment.Left, -1, 10,
                        new Color(0.82f, 0.85f, 0.88f, 0.95f));
                }
            }

            // Playhead triangle + line
            float px = ContentOriginX + (float)(PlayheadSeconds * ZoomScale) - Offset;
            if (px >= -6 && px <= w + 6)
            {
                var phColor = new Color(0.95f, 0.35f, 0.15f, 1f);
                DrawLine(new Vector2(px, 0), new Vector2(px, h), phColor, 2f);
                DrawColoredPolygon(new[]
                {
                    new Vector2(px - 6, 0),
                    new Vector2(px + 6, 0),
                    new Vector2(px, 8)
                }, phColor);
            }
        }

        private static string FormatRulerTime(float seconds)
        {
            if (seconds < 0) seconds = 0;
            int total = (int)Math.Floor(seconds);
            int min = total / 60;
            int sec = total % 60;
            float frac = seconds - total;
            if (min > 0)
            {
                if (frac > 0.05f)
                    return $"{min}:{sec:D2}.{ (int)(frac * 10) }";
                return $"{min}:{sec:D2}";
            }
            if (seconds < 10 && frac > 0.01f)
                return $"{seconds:0.#}s";
            return $"{sec}s";
        }
    }
}
