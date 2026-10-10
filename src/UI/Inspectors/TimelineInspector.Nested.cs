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
    /// <summary>
    /// One inner timeline region on a cue bar: file-norm span, wall-clock of one play, inner play count.
    /// Component play count is applied as outer tiles around the whole slice sequence.
    /// </summary>
    private readonly struct CueBarWaveSlice
    {
        public float FileStartNorm { get; init; }
        public float FileEndNorm { get; init; }
        public double WallSeconds { get; init; }
        public int PlayCount { get; init; }
        public bool Loop { get; init; }
    }

    private partial class CueBarWaveform : Control
    {
        public WaveformPeaks Peaks { get; set; }
        public float StartNorm { get; set; }
        public float EndNorm { get; set; } = 1f;
        /// <summary>Outer (component) play count. 1 when the cue or a region loops.</summary>
        public int PlayCount { get; set; } = 1;
        /// <summary>Inner region slices in sequence order. Empty uses StartNorm–EndNorm × PlayCount.</summary>
        public List<CueBarWaveSlice> Slices { get; set; }
        /// <summary>Visible fraction of the full cue bar (0–1). Used when the bar is clipped to the view.</summary>
        public float ViewFrom { get; set; }
        /// <summary>Visible fraction end of the full cue bar (0–1).</summary>
        public float ViewTo { get; set; } = 1f;
        public Color WaveColor { get; set; } = GlobalStyles.LowColor1;
        public Color DividerColor { get; set; } = new Color(1f, 1f, 1f, 0.45f);
        public Color RegionDividerColor { get; set; } = new Color(1f, 1f, 1f, 0.28f);
        public Color InnerDividerColor { get; set; } = new Color(1f, 1f, 1f, 0.16f);

        public override void _Draw()
        {
            if (Peaks == null || Peaks.BinCount < 1) return;
            float width = Size.X;
            float height = Size.Y;
            if (width < 2f || height < 4f) return;

            float midY = height * 0.5f;
            int outer = Math.Max(1, PlayCount);
            float startN = Mathf.Clamp(StartNorm, 0f, 1f);
            float endN = Mathf.Clamp(EndNorm, startN + 1e-5f, 1f);
            float peakScale = Peaks.ComputePeakScale(startN, endN);

            if (Slices != null && Slices.Count > 0)
                DrawSlicedSequence(width, height, outer, peakScale);
            else
                DrawUniformTiles(width, height, outer, startN, endN, peakScale);

            DrawLine(new Vector2(0, midY), new Vector2(width, midY), new Color(1, 1, 1, 0.1f), 1f);
        }

        private void GetViewWindow(out float w0, out float wSpan)
        {
            w0 = Mathf.Clamp(ViewFrom, 0f, 1f);
            float w1 = Mathf.Clamp(ViewTo, w0 + 1e-6f, 1f);
            wSpan = Math.Max(1e-6f, w1 - w0);
        }

        /// <summary>Maps a 0–1 position on the full cue bar into this control’s X.</summary>
        private static float BarNormToX(float n, float width, float w0, float wSpan) =>
            (n - w0) / wSpan * width;

        private void DrawTile(
            float barA, float barB, float fileA, float fileB,
            float width, float height, float w0, float wSpan, float peakScale)
        {
            float visA = Math.Max(barA, w0);
            float visB = Math.Min(barB, w0 + wSpan);
            if (visB <= visA + 1e-6f)
                return;

            float tileSpan = barB - barA;
            float t0 = tileSpan > 1e-9f ? (visA - barA) / tileSpan : 0f;
            float t1 = tileSpan > 1e-9f ? (visB - barA) / tileSpan : 1f;
            float n0 = fileA + (fileB - fileA) * t0;
            float n1 = fileA + (fileB - fileA) * t1;
            float x0 = BarNormToX(visA, width, w0, wSpan);
            float x1 = BarNormToX(visB, width, w0, wSpan);
            if (x1 - x0 < 0.5f)
                return;
            WaveformPainter.DrawEnvelope(
                this, Peaks, new Rect2(x0, 0f, x1 - x0, height),
                n0, n1, WaveColor, peakScale, 0.45f);
        }

        private void DrawDividerAtBarNorm(
            float n, float height, float width, float w0, float wSpan, Color color, float thickness)
        {
            if (n <= w0 + 1e-5f || n >= w0 + wSpan - 1e-5f)
                return;
            float x = BarNormToX(n, width, w0, wSpan);
            DrawLine(new Vector2(x, 1f), new Vector2(x, height - 1f), color, thickness);
        }

        private void DrawUniformTiles(
            float width, float height, int plays, float startN, float endN, float peakScale)
        {
            GetViewWindow(out float w0, out float wSpan);
            for (int play = 0; play < plays; play++)
            {
                float a = play / (float)plays;
                float b = (play + 1) / (float)plays;
                DrawTile(a, b, startN, endN, width, height, w0, wSpan, peakScale);
                if (play > 0)
                    DrawDividerAtBarNorm(a, height, width, w0, wSpan, DividerColor, 1.5f);
            }
        }

        private void DrawSlicedSequence(float width, float height, int outer, float peakScale)
        {
            double cycleWall = 0;
            foreach (var slice in Slices)
                cycleWall += slice.WallSeconds * Math.Max(1, slice.PlayCount);
            if (cycleWall < 1e-12)
                return;

            GetViewWindow(out float w0, out float wSpan);
            var font = ThemeDB.FallbackFont;
            const int fontSize = 10;
            var labelFill = new Color(0.95f, 0.95f, 0.97f, 0.95f);

            float cursor = 0f;
            float outerFrac = 1f / outer;
            for (int o = 0; o < outer; o++)
            {
                float outerA = o * outerFrac;
                if (o > 0)
                    DrawDividerAtBarNorm(outerA, height, width, w0, wSpan, DividerColor, 1.5f);

                foreach (var slice in Slices)
                {
                    int inner = Math.Max(1, slice.PlayCount);
                    float groupFrac = (float)(slice.WallSeconds * inner / cycleWall * outerFrac);
                    if (groupFrac < 1e-8f)
                        continue;

                    float groupA = cursor;
                    float tileFrac = groupFrac / inner;
                    float n0 = Mathf.Clamp(slice.FileStartNorm, 0f, 1f);
                    float n1 = Mathf.Clamp(slice.FileEndNorm, n0 + 1e-6f, 1f);

                    for (int p = 0; p < inner; p++)
                    {
                        float a = cursor;
                        float b = cursor + tileFrac;
                        DrawTile(a, b, n0, n1, width, height, w0, wSpan, peakScale);
                        if (p > 0)
                            DrawDividerAtBarNorm(a, height, width, w0, wSpan, InnerDividerColor, 1f);
                        else if (a > outerA + 1e-5f)
                            DrawDividerAtBarNorm(a, height, width, w0, wSpan, RegionDividerColor, 1.2f);
                        cursor = b;
                    }

                    if ((slice.Loop || inner > 1) && font != null)
                    {
                        float visA = Math.Max(groupA, w0);
                        float visB = Math.Min(groupA + groupFrac, w0 + wSpan);
                        float x0 = BarNormToX(visA, width, w0, wSpan);
                        float x1 = BarNormToX(visB, width, w0, wSpan);
                        float groupW = x1 - x0;
                        if (groupW >= 16f)
                        {
                            string label = slice.Loop ? "∞" : $"×{inner}";
                            var textSize = font.GetStringSize(label, HorizontalAlignment.Left, -1, fontSize);
                            if (textSize.X + 2f < groupW)
                            {
                                float tx = x0 + (groupW - textSize.X) * 0.5f;
                                DrawOutlinedString(font, new Vector2(tx, height - 3f), label, fontSize, labelFill);
                            }
                        }
                    }
                }
            }
        }

        private void DrawOutlinedString(Font font, Vector2 pos, string text, int fontSize, Color fill)
        {
            var outline = new Color(0f, 0f, 0f, 0.92f);
            for (int ox = -1; ox <= 1; ox++)
            {
                for (int oy = -1; oy <= 1; oy++)
                {
                    if (ox == 0 && oy == 0)
                        continue;
                    DrawString(font, pos + new Vector2(ox, oy), text,
                        HorizontalAlignment.Left, -1, fontSize, outline);
                }
            }

            DrawString(font, pos, text, HorizontalAlignment.Left, -1, fontSize, fill);
        }
    }

    /// <summary>
    /// Background grid for the timeline content area (major/minor vertical lines).
    /// </summary>
    private partial class TimeGrid : Control
    {
        public float ZoomScale { get; set; } = 10f;
        public float ContentHeight { get; set; }
        /// <summary>Seconds at the left edge of this control.</summary>
        public double ViewStartSeconds { get; set; }

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

            double tStart = ViewStartSeconds;
            double tEnd = tStart + w / ZoomScale + interval;
            double firstMinor = Math.Floor(tStart / minor) * minor;
            if (firstMinor < 0) firstMinor = 0;

            for (double t = firstMinor; t <= tEnd; t += minor)
            {
                float x = (float)((t - ViewStartSeconds) * ZoomScale);
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
        /// <summary>Seconds at the left edge of the ruler (view window).</summary>
        public double ViewStartSeconds { get; set; }
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

            double tStart = ViewStartSeconds + (Offset - ContentOriginX) / ZoomScale;
            double tEnd = tStart + w / ZoomScale;
            if (tStart < 0) tStart = 0;

            double firstMinor = Math.Floor(tStart / minor) * minor;
            var font = ThemeDB.FallbackFont;

            for (double t = firstMinor; t <= tEnd + minor * 0.01f; t += minor)
            {
                if (t < -1e-4f) continue;
                float x = ContentOriginX + (float)((t - ViewStartSeconds) * ZoomScale) - Offset;
                if (x < -20 || x > w + 20) continue;

                bool isMajor = Math.Abs(t / interval - Math.Round(t / interval)) < 1e-3;
                float tickTop = isMajor ? h * 0.28f : h * 0.55f;
                var tickColor = isMajor
                    ? new Color(0.88f, 0.9f, 0.92f, 0.95f)
                    : new Color(0.55f, 0.58f, 0.6f, 0.7f);
                DrawLine(new Vector2(x, tickTop), new Vector2(x, h - 1), tickColor, isMajor ? 1.2f : 1f);

                if (isMajor)
                {
                    string labelText = FormatRulerTime((float)t);
                    DrawString(font, new Vector2(x + 3, h * 0.42f), labelText, HorizontalAlignment.Left, -1, 10,
                        new Color(0.82f, 0.85f, 0.88f, 0.95f));
                }
            }

            // Playhead triangle + line
            float px = ContentOriginX + (float)((PlayheadSeconds - ViewStartSeconds) * ZoomScale) - Offset;
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
