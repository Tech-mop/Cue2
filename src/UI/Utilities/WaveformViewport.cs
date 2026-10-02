// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System;

namespace Cue2.UI.Utilities;

/// <summary>
/// Duration-based waveform view window. Stores start/span in file-normalized units
/// (0–1 of the media) but zooms in <em>seconds</em>: Fit is the whole file, max zoom
/// is <see cref="MinVisibleSeconds"/> (or the full file if it is shorter).
/// </summary>
public sealed class WaveformViewport
{
    /// <summary>Smallest window the user can zoom into, in seconds.</summary>
    public const double MinVisibleSeconds = 0.05;

    /// <summary>Multiply / divide the visible window for +/− and Ctrl+wheel.</summary>
    public const float ZoomStepFactor = 1.5f;

    private double _durationSec = 1;

    /// <summary>Full media duration in seconds.</summary>
    public double DurationSeconds
    {
        get => _durationSec;
        set
        {
            _durationSec = Math.Max(1e-6, value);
            SetWindow(ViewStartNorm, ViewSpanNorm);
        }
    }

    /// <summary>Left edge of the view, 0–1 of the file.</summary>
    public float ViewStartNorm { get; private set; }

    /// <summary>Visible fraction of the file (1 = Fit).</summary>
    public float ViewSpanNorm { get; private set; } = 1f;

    /// <summary>
    /// Smallest legal span for the current duration (1 when the file is shorter than
    /// <see cref="MinVisibleSeconds"/>).
    /// </summary>
    public float MinSpanNorm
    {
        get
        {
            if (_durationSec <= MinVisibleSeconds)
                return 1f;
            return (float)Math.Min(1.0, MinVisibleSeconds / _durationSec);
        }
    }

    /// <summary>True when the view is the whole file.</summary>
    public bool IsFitted => ViewSpanNorm >= 0.999f;

    /// <summary>Visible window in seconds.</summary>
    public double VisibleSeconds => ViewSpanNorm * _durationSec;

    /// <summary>
    /// Logarithmic slider position: 0 = Fit (whole file), 1 = max zoom.
    /// </summary>
    public float SliderT
    {
        get
        {
            float minSpan = MinSpanNorm;
            if (minSpan >= 0.999f || ViewSpanNorm >= 0.999f)
                return 0f;
            double logMin = Math.Log(minSpan);
            if (logMin >= -1e-12)
                return 0f;
            return (float)Math.Clamp(Math.Log(Math.Max(minSpan, ViewSpanNorm)) / logMin, 0, 1);
        }
    }

    /// <summary>Resets to the whole file.</summary>
    public void FitAll()
    {
        ViewStartNorm = 0f;
        ViewSpanNorm = 1f;
    }

    /// <summary>
    /// Frames <paramref name="startNorm"/>–<paramref name="endNorm"/> with padding.
    /// </summary>
    /// <param name="startNorm">Range start in 0–1 of the file.</param>
    /// <param name="endNorm">Range end in 0–1 of the file.</param>
    /// <param name="paddingFraction">Extra fraction of the range on each side.</param>
    public void FitRange(float startNorm, float endNorm, float paddingFraction = 0.08f)
    {
        startNorm = Math.Clamp(startNorm, 0f, 1f);
        endNorm = Math.Clamp(endNorm, 0f, 1f);
        if (endNorm < startNorm)
            (startNorm, endNorm) = (endNorm, startNorm);

        float range = Math.Max(1e-5f, endNorm - startNorm);
        float pad = range * Math.Clamp(paddingFraction, 0f, 0.5f);
        SetWindow(startNorm - pad, range + pad * 2f);
    }

    /// <summary>
    /// Sets span from a 0–1 log slider, keeping the current view centre still.
    /// </summary>
    /// <param name="t">0 = Fit, 1 = max zoom.</param>
    public void SetFromSliderT(float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        float minSpan = MinSpanNorm;
        float newSpan = minSpan >= 0.999f ? 1f : (float)Math.Pow(minSpan, t);
        float anchor = ViewStartNorm + ViewSpanNorm * 0.5f;
        SetWindow(anchor - newSpan * 0.5f, newSpan);
    }

    /// <summary>
    /// Zooms so <paramref name="anchorNorm"/> stays at the same relative X in the view.
    /// </summary>
    /// <param name="anchorNorm">File-normalized time under the cursor or view centre.</param>
    /// <param name="factor">Greater than 1 zooms in (narrower window).</param>
    public void ZoomAt(float anchorNorm, float factor)
    {
        if (factor <= 0f || float.IsNaN(factor))
            return;
        float newSpan = ViewSpanNorm / factor;
        float rel = ViewSpanNorm > 1e-8f
            ? (anchorNorm - ViewStartNorm) / ViewSpanNorm
            : 0.5f;
        rel = Math.Clamp(rel, 0f, 1f);
        SetWindow(anchorNorm - rel * newSpan, newSpan);
    }

    /// <summary>Shifts the window by a file-normalized delta (positive = later).</summary>
    /// <param name="deltaNorm">Added to <see cref="ViewStartNorm"/>.</param>
    public void Pan(float deltaNorm)
    {
        SetWindow(ViewStartNorm + deltaNorm, ViewSpanNorm);
    }

    /// <summary>Clamps an existing window after duration changes.</summary>
    public void Clamp() => SetWindow(ViewStartNorm, ViewSpanNorm);

    private void SetWindow(float start, float span)
    {
        span = Math.Clamp(span, MinSpanNorm, 1f);
        start = Math.Clamp(start, 0f, Math.Max(0f, 1f - span));
        ViewStartNorm = start;
        ViewSpanNorm = span;
    }
}
