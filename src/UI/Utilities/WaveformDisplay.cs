// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using Cue2.Services;
using Cue2.Media.Audio;
using Cue2.Domain.Cues;
using Godot;

namespace Cue2.UI.Utilities;

/// <summary>
/// Audacity-style peak waveform with selection markers, time ruler, grid, and zoom/scroll window.
/// File norms are 0–1 of full media; the visible window is
/// [<see cref="ViewStartNorm"/>, <see cref="ViewStartNorm"/> + <see cref="ViewSpanNorm"/>).
/// </summary>
public partial class WaveformDisplay : Control
{
    /// <summary>Height reserved at top for time labels, ticks, and in/out flags.</summary>
    public const float RulerHeight = 20f;

    /// <summary>Width of the in/out flag in the ruler (pixels).</summary>
    public const float FlagWidth = 12f;

    /// <summary>Height of the in/out flag in the ruler (pixels).</summary>
    public const float FlagHeight = 13f;

    /// <summary>Hit padding on the stem side of a trim handle (pixels).</summary>
    public const float HandleStemPad = 6f;

    /// <summary>Hit height of the fade flag at the bottom of the waveform.</summary>
    public const float FadeTabHitHeight = 16f;

    private WaveformPeaks _peaks;
    private float _startNorm;
    private float _endNorm = 1f;
    private float _fadeInEndNorm;
    private float _fadeOutStartNorm = 1f;
    private FadeCurveType _fadeInCurve = FadeCurveType.Linear;
    private FadeCurveType _fadeOutCurve = FadeCurveType.Linear;
    private float _viewStartNorm;
    private float _viewSpanNorm = 1f;
    private double _durationSec = 1;
    private float _playheadNorm = -1f;
    private float _hoverNorm = -1f;

    private Color _activeColor = GlobalStyles.HighColor2;
    private Color _inactiveColor = GlobalStyles.LowColor4;
    private Color _centerLineColor = new Color(1, 1, 1, 0.16f);
    private Color _outsideOverlay = new Color(0, 0, 0, 0.42f);
    private Color _startMarkerColor = GlobalStyles.LowColor1;
    private Color _endMarkerColor = GlobalStyles.HighColor1;
    private Color _rulerBg = new Color(0.04f, 0.04f, 0.04f, 1f);
    private Color _majorGrid = new Color(1, 1, 1, 0.12f);
    private Color _minorGrid = new Color(1, 1, 1, 0.05f);
    private Color _tickColor = new Color(0.38f, 0.38f, 0.38f, 1f);
    private Color _labelColor = new Color(0.55f, 0.55f, 0.55f, 1f);
    private Color _playheadColor = new Color(0.98f, 0.94f, 0.82f, 1f);
    private Color _hoverLineColor = new Color(1f, 1f, 1f, 0.28f);
    private Color _waveBodyBg = new Color(0.02f, 0.03f, 0.035f, 1f);
    private Color _selectionBand = new Color(1f, 1f, 1f, 0.06f);

    private Font _font;
    private StyleBoxFlat _chipBox;

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

    /// <summary>File-normalized inner edge of fade-in (clamped to the selection).</summary>
    public float FadeInEndNorm => _fadeInEndNorm;

    /// <summary>File-normalized inner edge of fade-out (clamped to the selection).</summary>
    public float FadeOutStartNorm => _fadeOutStartNorm;

    public float ViewStartNorm
    {
        get => _viewStartNorm;
        set { _viewStartNorm = Mathf.Clamp(value, 0f, 1f); QueueRedraw(); }
    }

    public float ViewSpanNorm
    {
        get => _viewSpanNorm;
        set { _viewSpanNorm = Mathf.Clamp(value, 1e-7f, 1f); QueueRedraw(); }
    }

    /// <summary>File-normalized playhead; negative hides it.</summary>
    public float PlayheadNorm
    {
        get => _playheadNorm;
        set
        {
            if (Math.Abs(value - _playheadNorm) < 1e-5f) return;
            _playheadNorm = value;
            QueueRedraw();
        }
    }

    /// <summary>File-normalized hover time; negative hides it.</summary>
    public float HoverNorm
    {
        get => _hoverNorm;
        set
        {
            if (Math.Abs(value - _hoverNorm) < 1e-4f) return;
            _hoverNorm = value;
            QueueRedraw();
        }
    }

    /// <summary>
    /// Updates peaks, selection, fades, view window, and duration in one redraw.
    /// </summary>
    public void SetData(
        WaveformPeaks peaks,
        float startNorm,
        float endNorm,
        float viewStartNorm = 0f,
        float viewSpanNorm = 1f,
        double durationSeconds = 1,
        double fadeInSeconds = 0,
        double fadeOutSeconds = 0,
        FadeCurveType fadeInCurve = FadeCurveType.Linear,
        FadeCurveType fadeOutCurve = FadeCurveType.Linear)
    {
        _peaks = peaks;
        _startNorm = Mathf.Clamp(startNorm, 0f, 1f);
        _endNorm = Mathf.Clamp(endNorm, 0f, 1f);
        if (_endNorm < _startNorm)
            (_startNorm, _endNorm) = (_endNorm, _startNorm);
        _viewSpanNorm = Mathf.Clamp(viewSpanNorm, 1e-7f, 1f);
        _viewStartNorm = Mathf.Clamp(viewStartNorm, 0f, 1f - _viewSpanNorm + 1e-6f);
        _durationSec = Math.Max(1e-6, durationSeconds);
        float fadeInN = (float)(Math.Max(0, fadeInSeconds) / _durationSec);
        float fadeOutN = (float)(Math.Max(0, fadeOutSeconds) / _durationSec);
        _fadeInEndNorm = Mathf.Clamp(_startNorm + fadeInN, _startNorm, _endNorm);
        _fadeOutStartNorm = Mathf.Clamp(_endNorm - fadeOutN, _startNorm, _endNorm);
        _fadeInCurve = fadeInCurve;
        _fadeOutCurve = fadeOutCurve;
        QueueRedraw();
    }

    /// <summary>
    /// Invisible hit target that covers the drawn in/out flag, including the ruler.
    /// Start flags extend right of the stem; end flags extend left.
    /// </summary>
    /// <param name="handle">Start or end drag control.</param>
    /// <param name="fileNorm">File-normalized time of the marker.</param>
    /// <param name="isStart">True for the start (in) flag.</param>
    /// <param name="panelWidth">Waveform panel width in pixels.</param>
    /// <param name="panelHeight">Waveform panel height in pixels.</param>
    public void PlaceHandle(Control handle, float fileNorm, bool isStart, float panelWidth, float panelHeight)
    {
        if (handle == null || !GodotObject.IsInstanceValid(handle))
            return;

        float x = FileNormToX(fileNorm);
        bool visible = x >= -FlagWidth && x <= panelWidth + FlagWidth;
        handle.Visible = visible;
        if (!visible)
            return;

        float width = FlagWidth + HandleStemPad + 2f;
        float left = isStart ? x - HandleStemPad : x - FlagWidth - 2f;
        float bodyH = Math.Max(4f, panelHeight - FadeTabHitHeight);
        handle.CustomMinimumSize = Vector2.Zero;
        handle.Position = new Vector2(left, 0);
        handle.Size = new Vector2(width, bodyH);
    }

    /// <summary>
    /// Invisible hit target for the fade flag at the bottom of the waveform.
    /// Fade-in tabs extend right of the stem; fade-out tabs extend left.
    /// </summary>
    /// <param name="handle">Fade-in or fade-out drag control.</param>
    /// <param name="fileNorm">File-normalized time of the fade inner edge.</param>
    /// <param name="isFadeIn">True for fade-in (inner edge after start).</param>
    /// <param name="panelWidth">Waveform panel width in pixels.</param>
    /// <param name="panelHeight">Waveform panel height in pixels.</param>
    public void PlaceFadeHandle(Control handle, float fileNorm, bool isFadeIn, float panelWidth, float panelHeight)
    {
        if (handle == null || !GodotObject.IsInstanceValid(handle))
            return;

        float x = FileNormToX(fileNorm);
        bool visible = x >= -FlagWidth && x <= panelWidth + FlagWidth;
        handle.Visible = visible;
        if (!visible)
            return;

        float width = FlagWidth + HandleStemPad + 2f;
        float left = isFadeIn ? x - HandleStemPad : x - FlagWidth - 2f;
        float top = Math.Max(RulerHeight, panelHeight - FadeTabHitHeight);
        handle.CustomMinimumSize = Vector2.Zero;
        handle.Position = new Vector2(left, top);
        handle.Size = new Vector2(width, Math.Max(4f, panelHeight - top));
    }

    /// <summary>
    /// Makes a trim handle an invisible hit target (the display draws the flag).
    /// </summary>
    /// <param name="handle">Start or end drag button.</param>
    /// <param name="isStart">True for the start (in) handle.</param>
    public static void StyleHandle(Button handle, bool isStart)
    {
        if (handle == null)
            return;

        var empty = new StyleBoxEmpty();
        handle.AddThemeStyleboxOverride("normal", empty);
        handle.AddThemeStyleboxOverride("hover", empty);
        handle.AddThemeStyleboxOverride("pressed", empty);
        handle.AddThemeStyleboxOverride("disabled", empty);
        handle.AddThemeStyleboxOverride("focus", empty);
        handle.MouseDefaultCursorShape = CursorShape.Hsize;
        handle.FocusMode = FocusModeEnum.None;
        handle.MouseFilter = MouseFilterEnum.Stop;
        handle.Flat = true;
        handle.CustomMinimumSize = Vector2.Zero;
        handle.TooltipText = UiLocalizer.T(isStart ? "Start time (drag)" : "End time (drag)");
    }

    /// <summary>
    /// Makes a fade flag an invisible hit target at the bottom of the waveform.
    /// </summary>
    /// <param name="handle">Fade-in or fade-out drag button.</param>
    /// <param name="isFadeIn">True for fade-in.</param>
    public static void StyleFadeHandle(Button handle, bool isFadeIn)
    {
        if (handle == null)
            return;

        var empty = new StyleBoxEmpty();
        handle.AddThemeStyleboxOverride("normal", empty);
        handle.AddThemeStyleboxOverride("hover", empty);
        handle.AddThemeStyleboxOverride("pressed", empty);
        handle.AddThemeStyleboxOverride("disabled", empty);
        handle.AddThemeStyleboxOverride("focus", empty);
        handle.MouseDefaultCursorShape = CursorShape.Hsize;
        handle.FocusMode = FocusModeEnum.None;
        handle.MouseFilter = MouseFilterEnum.Stop;
        handle.Flat = true;
        handle.CustomMinimumSize = Vector2.Zero;
        handle.TooltipText = UiLocalizer.T(isFadeIn ? "Fade-in (drag)" : "Fade-out (drag)");
    }

    /// <summary>
    /// Creates a styled fade-flag button.
    /// </summary>
    /// <param name="isFadeIn">True for fade-in.</param>
    /// <returns>A button ready to parent under the handle host.</returns>
    public static Button CreateFadeHandle(bool isFadeIn)
    {
        var handle = new Button
        {
            Name = isFadeIn ? "FadeInHandle" : "FadeOutHandle",
            ActionMode = BaseButton.ActionModeEnum.Press,
            KeepPressedOutside = true
        };
        StyleFadeHandle(handle, isFadeIn);
        return handle;
    }

    /// <summary>
    /// Lets pan/zoom reach the waveform panel everywhere except the handle buttons.
    /// </summary>
    /// <param name="handle">Any handle whose parent hosts both trim buttons.</param>
    public static void PrepareHandleHost(Control handle)
    {
        if (handle?.GetParent() is Control host)
            host.MouseFilter = MouseFilterEnum.Ignore;
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
        float waveBottom = height;
        float waveHeight = Math.Max(1f, waveBottom - waveTop);
        float midY = waveTop + waveHeight * 0.5f;
        float viewEnd = _viewStartNorm + _viewSpanNorm;

        DrawRect(new Rect2(0, 0, width, RulerHeight), _rulerBg, true);
        DrawRect(new Rect2(0, waveTop, width, waveHeight), _waveBodyBg, true);

        DrawSelectionRangeOnRuler(width);

        DrawTimeGridAndRuler(width, waveTop, waveHeight);

        DrawLine(new Vector2(0, midY), new Vector2(width, midY), _centerLineColor, 1f);

        if (_peaks != null && _peaks.BinCount >= 1)
        {
            float peakScale = _peaks.ComputePeakScale(0f, 1f);
            var waveRect = new Rect2(0, waveTop, width, waveHeight);

            DrawEnvelopeSpan(waveRect, _viewStartNorm, Math.Min(_startNorm, viewEnd), _inactiveColor, peakScale);
            DrawEnvelopeSpan(waveRect, Math.Max(_startNorm, _viewStartNorm), Math.Min(_endNorm, viewEnd), _activeColor, peakScale);
            DrawEnvelopeSpan(waveRect, Math.Max(_endNorm, _viewStartNorm), viewEnd, _inactiveColor, peakScale);
        }

        DrawOutsideSelectionOverlay(width, waveTop, waveBottom);
        DrawSelectionTopBand(width, waveTop);
        DrawFadeWedges(width, waveTop, waveBottom);
        DrawHover(width, height, waveTop);
        DrawTimeMarker(FileNormToX(_startNorm), waveBottom, _startMarkerColor, isStart: true);
        DrawTimeMarker(FileNormToX(_endNorm), waveBottom, _endMarkerColor, isStart: false);
        DrawFadeMarkers(width, waveTop, waveBottom, _fadeInEndNorm, _startMarkerColor, isFadeIn: true);
        DrawFadeMarkers(width, waveTop, waveBottom, _fadeOutStartNorm, _endMarkerColor, isFadeIn: false);
        DrawPlayhead(width, height);
    }

    /// <summary>
    /// Draws the envelope for a file-normalized span clipped to the current view.
    /// </summary>
    private void DrawEnvelopeSpan(Rect2 waveRect, float fromNorm, float toNorm, Color color, float peakScale)
    {
        if (toNorm - fromNorm < 1e-6f || _viewSpanNorm <= 0f)
            return;

        float x = (fromNorm - _viewStartNorm) / _viewSpanNorm * waveRect.Size.X;
        float x2 = (toNorm - _viewStartNorm) / _viewSpanNorm * waveRect.Size.X;
        if (x2 - x < 0.5f)
            return;

        var spanRect = new Rect2(waveRect.Position.X + x, waveRect.Position.Y, x2 - x, waveRect.Size.Y);
        WaveformPainter.DrawEnvelope(this, _peaks, spanRect, fromNorm, toNorm, color, peakScale);
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
        int fontSize = 10;

        for (double t = firstMinor; t <= viewEndSec + minorStep * 0.5; t += minorStep)
        {
            if (t < -1e-9 || t > _durationSec + 1e-6) continue;
            float norm = (float)(t / _durationSec);
            float x = FileNormToX(norm);
            if (x < -1 || x > width + 1) continue;

            bool isMajor = IsNearMultiple(t, majorStep, majorStep * 0.001);

            var gridColor = isMajor ? _majorGrid : _minorGrid;
            DrawLine(new Vector2(x, waveTop), new Vector2(x, waveTop + waveHeight), gridColor, isMajor ? 1.1f : 1f);

            float tickH = isMajor ? 4f : 2f;
            DrawVBar(SnapPx(x), RulerHeight - tickH, RulerHeight, _tickColor, 1f);

            if (isMajor && font != null)
            {
                string label = FormatTickLabel(t, majorStep);
                var textSize = font.GetStringSize(label, HorizontalAlignment.Left, -1, fontSize);
                float tx = x + 3f;
                if (tx + textSize.X > width - 2)
                    tx = x - textSize.X - 3f;
                if (tx < 1f) tx = 1f;
                DrawString(font, new Vector2(tx, 15f), label,
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

    private void DrawOutsideSelectionOverlay(float width, float waveTop, float waveBottom)
    {
        float startX = FileNormToX(_startNorm);
        float endX = FileNormToX(_endNorm);
        float waveH = waveBottom - waveTop;

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

    private void DrawFadeWedges(float width, float waveTop, float waveBottom)
    {
        float startX = FileNormToX(_startNorm);
        float endX = FileNormToX(_endNorm);
        float fadeInX = FileNormToX(_fadeInEndNorm);
        float fadeOutX = FileNormToX(_fadeOutStartNorm);
        var fill = new Color(0f, 0f, 0f, 0.4f);

        if (fadeInX - startX > 1f)
        {
            float x0 = Mathf.Clamp(startX, -2, width + 2);
            float x1 = Mathf.Clamp(fadeInX, -2, width + 2);
            if (x1 - x0 > 0.5f)
                DrawFadeCurve(x0, x1, waveTop, waveBottom, fill, _startMarkerColor, _fadeInCurve, fadeOut: false);
        }

        if (endX - fadeOutX > 1f)
        {
            float x0 = Mathf.Clamp(fadeOutX, -2, width + 2);
            float x1 = Mathf.Clamp(endX, -2, width + 2);
            if (x1 - x0 > 0.5f)
                DrawFadeCurve(x0, x1, waveTop, waveBottom, fill, _endMarkerColor, _fadeOutCurve, fadeOut: true);
        }
    }

    private void DrawFadeCurve(
        float x0, float x1, float waveTop, float waveBottom, Color fill, Color line,
        FadeCurveType curve, bool fadeOut)
    {
        float span = x1 - x0;
        int cols = Math.Max(2, (int)Math.Ceiling(span));
        float colW = span / cols;
        var pts = new Vector2[cols + 1];
        for (int i = 0; i <= cols; i++)
        {
            float u = i / (float)cols;
            float x = x0 + u * span;
            float shaped = FadeCurve.Evaluate(u, curve);
            float gain = fadeOut ? 1f - shaped : shaped;
            float y = Mathf.Lerp(waveBottom, waveTop, gain);
            pts[i] = new Vector2(x, y);
            if (i < cols && y > waveTop + 0.5f)
                DrawRect(new Rect2(x, waveTop, colW + 0.2f, y - waveTop), fill, true);
        }

        DrawPolyline(pts, line, 1f, antialiased: true);
    }

    private void DrawFadeMarkers(float width, float waveTop, float waveBottom, float fileNorm, Color color, bool isFadeIn)
    {
        float x = SnapPx(FileNormToX(fileNorm));
        if (x < -20 || x > width + 20)
            return;

        bool hoverNear = _hoverNorm >= 0f && Math.Abs(FileNormToX(_hoverNorm) - FileNormToX(fileNorm)) <= 8f;
        if (hoverNear)
            color = color.Lightened(0.18f);

        // Skip a second stem when the fade edge sits on start/end.
        float sibling = isFadeIn ? FileNormToX(_startNorm) : FileNormToX(_endNorm);
        if (Math.Abs(x - SnapPx(sibling)) >= 1.5f)
            DrawVBar(x, waveTop, waveBottom, color, 1f);

        float w = FlagWidth;
        float h = FlagHeight;
        Vector2[] flag = isFadeIn
            ? new[]
            {
                new Vector2(x, waveBottom - 1f),
                new Vector2(x + w - 4f, waveBottom - 1f),
                new Vector2(x + w, waveBottom - h * 0.5f),
                new Vector2(x + w - 4f, waveBottom - h),
                new Vector2(x, waveBottom - h)
            }
            : new[]
            {
                new Vector2(x, waveBottom - 1f),
                new Vector2(x - w + 4f, waveBottom - 1f),
                new Vector2(x - w, waveBottom - h * 0.5f),
                new Vector2(x - w + 4f, waveBottom - h),
                new Vector2(x, waveBottom - h)
            };
        SnapPoly(flag);
        DrawColoredPolygon(flag, color);
    }

    private void DrawSelectionRangeOnRuler(float width)
    {
        float startX = FileNormToX(_startNorm);
        float endX = FileNormToX(_endNorm);
        float left = Mathf.Clamp(Math.Min(startX, endX), 0, width);
        float right = Mathf.Clamp(Math.Max(startX, endX), 0, width);
        if (right - left < 1f)
            return;
        DrawRect(new Rect2(left, 0, right - left, RulerHeight), _selectionBand, true);
    }

    private void DrawSelectionTopBand(float width, float waveTop)
    {
        float startX = FileNormToX(_startNorm);
        float endX = FileNormToX(_endNorm);
        float left = Mathf.Clamp(Math.Min(startX, endX), 0, width);
        float right = Mathf.Clamp(Math.Max(startX, endX), 0, width);
        if (right - left < 1f)
            return;
        DrawRect(new Rect2(left, waveTop, right - left, 2f), new Color(1f, 1f, 1f, 0.18f), true);
    }

    private void DrawHover(float width, float height, float waveTop)
    {
        if (_hoverNorm < 0f || !IsInView(_hoverNorm))
            return;

        float x = FileNormToX(_hoverNorm);
        DrawLine(new Vector2(x, waveTop), new Vector2(x, height), _hoverLineColor, 1f);

        var font = _font ?? ThemeDB.FallbackFont;
        if (font == null) return;
        double t = _hoverNorm * _durationSec;
        double visibleSec = Math.Max(1e-6, _viewSpanNorm * _durationSec);
        string label = FormatTickLabel(t, NiceTimeStep(visibleSec / 7.0));
        const int fontSize = 10;
        var textSize = font.GetStringSize(label, HorizontalAlignment.Left, -1, fontSize);
        float chipW = textSize.X + 8f;
        float chipH = 14f;
        float chipX = Mathf.Clamp(x + 6f, 2f, Math.Max(2f, width - chipW - 2f));
        var chipRect = new Rect2(chipX, 3f, chipW, chipH);
        DrawChip(chipRect, new Color(0.05f, 0.06f, 0.07f, 0.88f));
        DrawString(font, new Vector2(chipX + 4f, 13f), label,
            HorizontalAlignment.Left, -1, fontSize, Colors.White);
    }

    private void DrawChip(Rect2 rect, Color bg)
    {
        _chipBox ??= new StyleBoxFlat
        {
            CornerRadiusTopLeft = 0,
            CornerRadiusTopRight = 0,
            CornerRadiusBottomLeft = 0,
            CornerRadiusBottomRight = 0
        };
        _chipBox.BgColor = bg;
        DrawStyleBox(_chipBox, rect);
    }

    private void DrawPlayhead(float width, float height)
    {
        if (_playheadNorm < 0f || !IsInView(_playheadNorm))
            return;
        float x = SnapPx(FileNormToX(_playheadNorm));
        if (x < -2 || x > width + 2) return;

        DrawVBar(x, 8f, height, _playheadColor, 2f);
        var tip = new[]
        {
            new Vector2(x - 5f, 1f),
            new Vector2(x + 5f, 1f),
            new Vector2(x, 9f)
        };
        SnapPoly(tip);
        DrawColoredPolygon(tip, _playheadColor);
    }

    private void DrawTimeMarker(float x, float height, Color color, bool isStart)
    {
        x = SnapPx(x);
        if (x < -20 || x > Size.X + 20) return;

        bool hoverNear = _hoverNorm >= 0f && Math.Abs(FileNormToX(_hoverNorm) - x) <= 8f;
        if (hoverNear)
            color = color.Lightened(0.18f);

        DrawVBar(x, FlagHeight - 1f, height, color, 2f);

        float w = FlagWidth;
        float h = FlagHeight;
        Vector2[] flag = isStart
            ? new[]
            {
                new Vector2(x, 1f),
                new Vector2(x + w - 4f, 1f),
                new Vector2(x + w, h * 0.5f),
                new Vector2(x + w - 4f, h - 1f),
                new Vector2(x, h - 1f)
            }
            : new[]
            {
                new Vector2(x, 1f),
                new Vector2(x - w + 4f, 1f),
                new Vector2(x - w, h * 0.5f),
                new Vector2(x - w + 4f, h - 1f),
                new Vector2(x, h - 1f)
            };
        SnapPoly(flag);
        DrawColoredPolygon(flag, color);
    }

    private void DrawVBar(float x, float y0, float y1, Color color, float thickness)
    {
        float w = Math.Max(1f, Mathf.Round(thickness));
        float left = Mathf.Round(x - w * 0.5f);
        float top = Math.Min(y0, y1);
        float h = Math.Abs(y1 - y0);
        if (h < 1f)
            return;
        DrawRect(new Rect2(left, top, w, h), color, true);
    }

    private static float SnapPx(float v) => Mathf.Round(v);

    private static void SnapPoly(Vector2[] points)
    {
        if (points == null) return;
        for (int i = 0; i < points.Length; i++)
            points[i] = new Vector2(Mathf.Round(points[i].X), Mathf.Round(points[i].Y));
    }
}
