// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using Cue2.Services;
using Cue2.Media.Audio;
using Godot;

namespace Cue2.UI.Utilities;

/// <summary>
/// Audacity-style peak waveform with selection markers, time ruler, grid, and zoom/scroll window.
/// File norms are 0–1 of full media; the visible window is
/// [<see cref="ViewStartNorm"/>, <see cref="ViewStartNorm"/> + <see cref="ViewSpanNorm"/>).
/// </summary>
public partial class WaveformDisplay : Control
{
    /// <summary>Height reserved at top for time labels / ticks.</summary>
    public const float RulerHeight = 18f;

    private WaveformPeaks _peaks;
    private float _startNorm;
    private float _endNorm = 1f;
    private float _viewStartNorm;
    private float _viewSpanNorm = 1f;
    private double _durationSec = 1;

    private Color _activeColor = GlobalStyles.HighColor3;
    private Color _inactiveColor = GlobalStyles.LowColor4;
    private Color _centerLineColor = new Color(1, 1, 1, 0.12f);
    private Color _outsideOverlay = new Color(0, 0, 0, 0.45f);
    private Color _startMarkerColor = GlobalStyles.LowColor1;
    private Color _endMarkerColor = GlobalStyles.HighColor1;
    private Color _rulerBg = new Color(0.08f, 0.08f, 0.1f, 0.95f);
    private Color _majorGrid = new Color(1, 1, 1, 0.14f);
    private Color _minorGrid = new Color(1, 1, 1, 0.06f);
    private Color _tickColor = new Color(0.75f, 0.78f, 0.8f, 0.9f);
    private Color _labelColor = new Color(0.78f, 0.8f, 0.82f, 0.95f);

    private Font _font;

    public WaveformPeaks Peaks
    {
        get => _peaks;
        set { _peaks = value; QueueRedraw(); }
    }

    /// <summary>Full media duration in seconds (for time ticks).</summary>
    public double DurationSeconds
    {
        get => _durationSec;
        set { _durationSec = Math.Max(1e-6, value); QueueRedraw(); }
    }

    public float StartNorm
    {
        get => _startNorm;
        set { _startNorm = Mathf.Clamp(value, 0f, 1f); QueueRedraw(); }
    }

    public float EndNorm
    {
        get => _endNorm;
        set { _endNorm = Mathf.Clamp(value, 0f, 1f); QueueRedraw(); }
    }

    public float ViewStartNorm
    {
        get => _viewStartNorm;
        set { _viewStartNorm = Mathf.Clamp(value, 0f, 1f); QueueRedraw(); }
    }

    public float ViewSpanNorm
    {
        get => _viewSpanNorm;
        set { _viewSpanNorm = Mathf.Clamp(value, 0.01f, 1f); QueueRedraw(); }
    }

    /// <summary>
    /// Updates peaks, selection, view window, and duration in one redraw.
    /// </summary>
    public void SetData(
        WaveformPeaks peaks,
        float startNorm,
        float endNorm,
        float viewStartNorm = 0f,
        float viewSpanNorm = 1f,
        double durationSeconds = 1)
    {
        _peaks = peaks;
        _startNorm = Mathf.Clamp(startNorm, 0f, 1f);
        _endNorm = Mathf.Clamp(endNorm, 0f, 1f);
        if (_endNorm < _startNorm)
            (_startNorm, _endNorm) = (_endNorm, _startNorm);
        _viewSpanNorm = Mathf.Clamp(viewSpanNorm, 0.01f, 1f);
        _viewStartNorm = Mathf.Clamp(viewStartNorm, 0f, 1f - _viewSpanNorm + 1e-6f);
        _durationSec = Math.Max(1e-6, durationSeconds);
        QueueRedraw();
    }

    public float FileNormToX(float fileNorm)
    {
        float width = Size.X;
        if (width < 1f || _viewSpanNorm <= 0f) return 0f;
        return (fileNorm - _viewStartNorm) / _viewSpanNorm * width;
    }

    public float XToFileNorm(float x)
    {
        float width = Size.X;
        if (width < 1f) return _viewStartNorm;
        float t = Mathf.Clamp(x / width, 0f, 1f);
        return Mathf.Clamp(_viewStartNorm + t * _viewSpanNorm, 0f, 1f);
    }

    public bool IsInView(float fileNorm)
    {
        return fileNorm >= _viewStartNorm - 1e-6f &&
               fileNorm <= _viewStartNorm + _viewSpanNorm + 1e-6f;
    }

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        _font = ThemeDB.FallbackFont;
    }

    public override void _Draw()
    {
        var size = Size;
        if (size.X < 2 || size.Y < 2)
            return;

        float width = size.X;
        float height = size.Y;
        float waveTop = RulerHeight;
        float waveHeight = Math.Max(1f, height - RulerHeight);
        float midY = waveTop + waveHeight * 0.5f;
        float viewEnd = _viewStartNorm + _viewSpanNorm;

        // Ruler strip
        DrawRect(new Rect2(0, 0, width, RulerHeight), _rulerBg, true);
        DrawLine(new Vector2(0, RulerHeight - 0.5f), new Vector2(width, RulerHeight - 0.5f),
            new Color(1, 1, 1, 0.2f), 1f);

        // Waveform body background
        DrawRect(new Rect2(0, waveTop, width, waveHeight), new Color(0, 0, 0, 0.28f), true);

        // Time grid + ruler labels (behind peaks)
        DrawTimeGridAndRuler(width, waveTop, waveHeight);

        // Zero line across waveform only
        DrawLine(new Vector2(0, midY), new Vector2(width, midY), _centerLineColor, 1f);

        if (_peaks == null || _peaks.BinCount < 1)
        {
            DrawSelectionAndMarkers(width, height, waveTop);
            return;
        }

        int binCount = _peaks.BinCount;

        float peakScale = 0.001f;
        for (int i = 0; i < binCount; i++)
        {
            peakScale = Math.Max(peakScale, Math.Abs(_peaks.GetMin(i)));
            peakScale = Math.Max(peakScale, Math.Abs(_peaks.GetMax(i)));
        }
        peakScale = Math.Max(peakScale, 0.05f);

        int firstBin = (int)(_viewStartNorm * binCount);
        int lastBin = (int)Math.Ceiling(viewEnd * binCount);
        firstBin = Math.Clamp(firstBin, 0, binCount - 1);
        lastBin = Math.Clamp(lastBin, firstBin + 1, binCount);

        float binsInView = Math.Max(1f, viewEnd * binCount - _viewStartNorm * binCount);
        float barWidth = Math.Max(1f, width / binsInView);

        for (int i = firstBin; i < lastBin; i++)
        {
            float binCenterNorm = (i + 0.5f) / binCount;
            float x = FileNormToX(binCenterNorm);
            if (x < -barWidth || x > width + barWidth) continue;

            float minVal = Mathf.Clamp(_peaks.GetMin(i) / peakScale, -1f, 1f);
            float maxVal = Mathf.Clamp(_peaks.GetMax(i) / peakScale, -1f, 1f);

            float yMax = midY - maxVal * (waveHeight * 0.5f);
            float yMin = midY - minVal * (waveHeight * 0.5f);
            if (yMin < yMax)
                (yMin, yMax) = (yMax, yMin);
            if (yMin - yMax < 1f)
            {
                yMax = midY - 0.5f;
                yMin = midY + 0.5f;
            }

            bool inSelection = binCenterNorm >= _startNorm && binCenterNorm <= _endNorm;
            var color = inSelection ? _activeColor : _inactiveColor;
            DrawLine(new Vector2(x, yMax), new Vector2(x, yMin), color, barWidth);
        }

        DrawSelectionAndMarkers(width, height, waveTop);
    }

    private void DrawSelectionAndMarkers(float width, float height, float waveTop)
    {
        DrawOutsideSelectionOverlay(width, height, waveTop);
        DrawTimeMarker(FileNormToX(_startNorm), height, waveTop, _startMarkerColor, isStart: true);
        DrawTimeMarker(FileNormToX(_endNorm), height, waveTop, _endMarkerColor, isStart: false);
    }

    /// <summary>
    /// Vertical grid lines through the waveform and ticks/labels on the top ruler.
    /// </summary>
    private void DrawTimeGridAndRuler(float width, float waveTop, float waveHeight)
    {
        double viewStartSec = _viewStartNorm * _durationSec;
        double viewEndSec = (_viewStartNorm + _viewSpanNorm) * _durationSec;
        double visibleSec = Math.Max(1e-6, viewEndSec - viewStartSec);

        // Aim for ~6–10 major divisions across the view
        double majorStep = NiceTimeStep(visibleSec / 7.0);
        double minorStep = majorStep / 5.0;
        // Avoid overcrowding minors when very zoomed out
        if (visibleSec / minorStep > 80)
            minorStep = majorStep / 2.0;

        double firstMinor = Math.Floor(viewStartSec / minorStep) * minorStep;
        if (firstMinor < 0) firstMinor = 0;

        var font = _font ?? ThemeDB.FallbackFont;
        int fontSize = 11;

        for (double t = firstMinor; t <= viewEndSec + minorStep * 0.5; t += minorStep)
        {
            if (t < -1e-9 || t > _durationSec + 1e-6) continue;
            float norm = (float)(t / _durationSec);
            float x = FileNormToX(norm);
            if (x < -1 || x > width + 1) continue;

            bool isMajor = IsNearMultiple(t, majorStep, majorStep * 0.001);

            // Grid behind waveform body
            var gridColor = isMajor ? _majorGrid : _minorGrid;
            float gridWidth = isMajor ? 1.2f : 1f;
            DrawLine(new Vector2(x, waveTop), new Vector2(x, waveTop + waveHeight), gridColor, gridWidth);

            // Ruler tick
            float tickH = isMajor ? 8f : 4f;
            DrawLine(new Vector2(x, RulerHeight - tickH), new Vector2(x, RulerHeight), _tickColor, isMajor ? 1.5f : 1f);

            if (isMajor && font != null)
            {
                string label = FormatTickLabel(t, majorStep);
                var textSize = font.GetStringSize(label, HorizontalAlignment.Left, -1, fontSize);
                float tx = x + 3f;
                // Keep last labels on screen
                if (tx + textSize.X > width - 2)
                    tx = x - textSize.X - 3f;
                if (tx < 1f) tx = 1f;
                DrawString(font, new Vector2(tx, RulerHeight - 6f), label,
                    HorizontalAlignment.Left, -1, fontSize, _labelColor);
            }
        }
    }

    private static bool IsNearMultiple(double value, double step, double eps)
    {
        if (step <= 0) return false;
        double q = value / step;
        return Math.Abs(q - Math.Round(q)) * step <= eps;
    }

    /// <summary>
    /// Round <paramref name="raw"/> to a 1/2/5 × 10^n second step.
    /// </summary>
    private static double NiceTimeStep(double raw)
    {
        if (raw <= 0) return 1;
        double exp = Math.Floor(Math.Log10(raw));
        double baseStep = Math.Pow(10, exp);
        double n = raw / baseStep;
        double nice;
        if (n <= 1) nice = 1;
        else if (n <= 2) nice = 2;
        else if (n <= 5) nice = 5;
        else nice = 10;
        return nice * baseStep;
    }

    private static string FormatTickLabel(double seconds, double majorStep)
    {
        if (seconds < 0) seconds = 0;
        // Sub-second majors
        if (majorStep < 1.0)
        {
            if (majorStep < 0.01)
                return $"{seconds:0.000}s";
            if (majorStep < 0.1)
                return $"{seconds:0.00}s";
            return $"{seconds:0.0}s";
        }

        int totalMs = (int)Math.Round(seconds * 1000);
        int ms = totalMs % 1000;
        int totalSec = totalMs / 1000;
        int s = totalSec % 60;
        int m = (totalSec / 60) % 60;
        int h = totalSec / 3600;

        if (h > 0)
            return majorStep >= 60 ? $"{h}:{m:D2}:{s:D2}" : $"{h}:{m:D2}:{s:D2}";
        if (majorStep >= 1 && ms == 0)
            return $"{m}:{s:D2}";
        return $"{m}:{s:D2}.{ms:D3}";
    }

    private void DrawOutsideSelectionOverlay(float width, float height, float waveTop)
    {
        float startX = FileNormToX(_startNorm);
        float endX = FileNormToX(_endNorm);
        float waveH = height - waveTop;

        if (startX > 0)
        {
            float w = Mathf.Clamp(startX, 0, width);
            DrawRect(new Rect2(0, waveTop, w, waveH), _outsideOverlay, true);
        }

        if (endX < width)
        {
            float x = Mathf.Clamp(endX, 0, width);
            DrawRect(new Rect2(x, waveTop, width - x, waveH), _outsideOverlay, true);
        }
    }

    private void DrawTimeMarker(float x, float height, float waveTop, Color color, bool isStart)
    {
        if (x < -20 || x > Size.X + 20) return;

        // Full height including ruler
        DrawLine(new Vector2(x, 0), new Vector2(x, height), color, 3f);

        float flagW = 10f;
        float flagH = 11f;
        Vector2[] tri = isStart
            ? new[] { new Vector2(x, 0), new Vector2(x + flagW, 0), new Vector2(x, flagH) }
            : new[] { new Vector2(x, 0), new Vector2(x - flagW, 0), new Vector2(x, flagH) };
        DrawColoredPolygon(tri, color);

        float gy = height - 8f;
        if (isStart)
            DrawRect(new Rect2(x, gy, 6f, 8f), color, true);
        else
            DrawRect(new Rect2(x - 6f, gy, 6f, 8f), color, true);
    }
}
