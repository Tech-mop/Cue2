// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System;
using Cue2.Media.Audio;
using Godot;

namespace Cue2.UI.Utilities;

/// <summary>
/// Shared min/max envelope drawing for inspector and timeline waveforms.
/// Columns map a file-normalized range across a rectangle: zoomed-out pixels
/// peak-reduce covering bins; zoomed-in / short-file pixels interpolate.
/// </summary>
public static class WaveformPainter
{
    /// <summary>
    /// Fills a min/max peak envelope into <paramref name="rect"/>.
    /// </summary>
    /// <param name="canvas">Control that is currently inside <c>_Draw</c>.</param>
    /// <param name="peaks">Peak envelope spanning the full file.</param>
    /// <param name="rect">Pixel rectangle the file range is mapped onto.</param>
    /// <param name="fileStartNorm">File-normalized start (0–1) at the left of <paramref name="rect"/>.</param>
    /// <param name="fileEndNorm">File-normalized end (0–1) at the right of <paramref name="rect"/>.</param>
    /// <param name="color">Fill color.</param>
    /// <param name="peakScale">Amplitude divisor (typically <see cref="WaveformPeaks.ComputePeakScale"/>).</param>
    /// <param name="amplitude">Fraction of half-height used for ±1.0, in 0.05–0.5.</param>
    public static void DrawEnvelope(
        CanvasItem canvas,
        WaveformPeaks peaks,
        Rect2 rect,
        float fileStartNorm,
        float fileEndNorm,
        Color color,
        float peakScale,
        float amplitude = 0.48f)
    {
        if (canvas == null || peaks == null || peaks.BinCount < 1)
            return;

        float width = rect.Size.X;
        float height = rect.Size.Y;
        if (width < 1f || height < 1f)
            return;

        float x0 = rect.Position.X;
        float y0 = rect.Position.Y;
        float midY = y0 + height * 0.5f;
        float halfH = height * Math.Clamp(amplitude, 0.05f, 0.5f);
        peakScale = Math.Max(peakScale, 0.001f);

        float nStart = Math.Clamp(fileStartNorm, 0f, 1f);
        float nEnd = Math.Clamp(fileEndNorm, 0f, 1f);
        if (nEnd < nStart)
            (nStart, nEnd) = (nEnd, nStart);
        float nSpan = Math.Max(1e-8f, nEnd - nStart);

        int cols = Math.Max(1, (int)Math.Ceiling(width));
        float colW = width / cols;
        // Slight overlap so sub-pixel columns do not leave hairline gaps.
        float drawW = Math.Max(1f, colW + 0.2f);

        for (int c = 0; c < cols; c++)
        {
            float t0 = c / (float)cols;
            float t1 = (c + 1) / (float)cols;
            peaks.GetEnvelope(nStart + t0 * nSpan, nStart + t1 * nSpan, out float mn, out float mx);

            float minVal = Math.Clamp(mn / peakScale, -1f, 1f);
            float maxVal = Math.Clamp(mx / peakScale, -1f, 1f);

            float yMax = midY - maxVal * halfH;
            float yMin = midY - minVal * halfH;
            if (yMin < yMax)
                (yMin, yMax) = (yMax, yMin);
            if (yMin - yMax < 1f)
            {
                yMax = midY - 0.5f;
                yMin = midY + 0.5f;
            }

            canvas.DrawRect(new Rect2(x0 + t0 * width, yMax, drawW, yMin - yMax), color, true);
        }
    }
}
