// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System;
using Godot;

namespace Cue2.UI.Utilities;

/// <summary>
/// Scrollbar + Fit / − / + chrome for inspector waveforms.
/// Zoom is duration-based via <see cref="WaveformViewport"/>.
/// </summary>
public partial class WaveformZoomBar : VBoxContainer
{
    /// <summary>Raised after the view window changes and chrome has been synced.</summary>
    public event Action ViewChanged;

    /// <summary>
    /// Raised on a left click that did not pan and was not a double-click Fit.
    /// Argument is the file-normalized time under the pointer at press.
    /// </summary>
    public event Action<float> PanelClicked;

    /// <summary>Raised when a double-click Fit Range is applied.</summary>
    public event Action PanelDoubleClicked;

    /// <summary>Duration-based view window shared with the display.</summary>
    public WaveformViewport Viewport { get; } = new WaveformViewport();

    private HScrollBar _scroll;
    private Button _fitButton;
    private Button _zoomOutButton;
    private Button _zoomInButton;
    private Label _readout;
    private WaveformDisplay _display;
    private Control _panel;
    private bool _syncing;
    private bool _panning;
    private bool _pressPending;
    private Vector2 _pressPos;
    private float _pressFileNorm;
    private float _panGrabNorm;
    private float _selectionStartNorm;
    private float _selectionEndNorm = 1f;
    private const float ClickSlopPx = 5f;

    /// <summary>
    /// Creates the scrollbar and Fit / − / + toolbar.
    /// </summary>
    public WaveformZoomBar()
    {
        Name = "WaveformZoomBar";
        AddThemeConstantOverride("separation", 3);

        _scroll = new HScrollBar
        {
            Name = "WaveformScroll",
            CustomMinimumSize = new Vector2(0, 8),
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            Visible = true
        };
        _scroll.ValueChanged += OnScrollChanged;
        AddChild(_scroll);

        var toolbar = new HBoxContainer
        {
            Name = "ZoomToolbar",
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            Alignment = AlignmentMode.Begin
        };
        toolbar.AddThemeConstantOverride("separation", 3);
        AddChild(toolbar);

        _fitButton = MakeToolButton("Fit", 34);
        _fitButton.Pressed += OnFitPressed;
        toolbar.AddChild(_fitButton);

        _zoomOutButton = MakeToolButton("−", 22);
        _zoomOutButton.Pressed += () => ZoomFromButton(zoomIn: false);
        toolbar.AddChild(_zoomOutButton);

        _zoomInButton = MakeToolButton("+", 22);
        _zoomInButton.Pressed += () => ZoomFromButton(zoomIn: true);
        toolbar.AddChild(_zoomInButton);

        toolbar.AddChild(new Control
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            MouseFilter = MouseFilterEnum.Ignore
        });

        _readout = new Label
        {
            Name = "ZoomReadout",
            SizeFlagsVertical = SizeFlags.ShrinkCenter,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            CustomMinimumSize = new Vector2(92, 0),
            MouseFilter = MouseFilterEnum.Ignore
        };
        _readout.AddThemeFontSizeOverride("font_size", 10);
        _readout.AddThemeColorOverride("font_color", new Color(0.72f, 0.74f, 0.76f, 0.95f));
        toolbar.AddChild(_readout);

        Localize();
        SyncChrome();
    }

    /// <summary>
    /// Hooks panel pointer input and the waveform display used for X↔time mapping.
    /// </summary>
    /// <param name="display">Waveform painter inside the panel.</param>
    /// <param name="panel">Hit target for wheel / drag / double-click.</param>
    public void Attach(WaveformDisplay display, Control panel)
    {
        DetachPanel();
        _display = display;
        _panel = panel;
        if (_panel == null) return;
        _panel.GuiInput += OnPanelGuiInput;
        _panel.MouseExited += OnPanelMouseExited;
    }

    /// <summary>Start/end of the cue region, used by double-click Fit Range.</summary>
    /// <param name="startNorm">Selection start, 0–1 of the file.</param>
    /// <param name="endNorm">Selection end, 0–1 of the file.</param>
    public void SetSelection(float startNorm, float endNorm)
    {
        _selectionStartNorm = startNorm;
        _selectionEndNorm = endNorm;
    }

    /// <summary>Resets to the whole file and refreshes chrome (does not raise <see cref="ViewChanged"/>).</summary>
    public void Reset()
    {
        Viewport.FitAll();
        SyncChrome();
    }

    /// <summary>Applies English-source strings (call again on locale change).</summary>
    public void Localize()
    {
        if (_fitButton != null)
        {
            _fitButton.Text = UiLocalizer.T("Fit");
            _fitButton.TooltipText = UiLocalizer.T("Fit whole file");
        }
        if (_zoomOutButton != null)
            _zoomOutButton.TooltipText = UiLocalizer.T("Zoom out");
        if (_zoomInButton != null)
            _zoomInButton.TooltipText = UiLocalizer.T("Zoom in");
        if (_scroll != null)
            _scroll.TooltipText = UiLocalizer.T("Scroll zoomed waveform");
        UpdateReadout();
    }

    /// <summary>Syncs buttons and scrollbar from <see cref="Viewport"/>.</summary>
    public void SyncChrome()
    {
        _syncing = true;
        try
        {
            bool canZoom = Viewport.MinSpanNorm < 0.999f;
            bool fitted = Viewport.IsFitted;
            bool atMax = Viewport.ViewSpanNorm <= Viewport.MinSpanNorm + 1e-6f;

            if (_zoomOutButton != null)
                _zoomOutButton.Disabled = fitted || !canZoom;
            if (_zoomInButton != null)
                _zoomInButton.Disabled = atMax || !canZoom;
            if (_fitButton != null)
                _fitButton.Disabled = fitted;

            if (_scroll != null)
            {
                _scroll.Visible = true;
                _scroll.MinValue = 0;
                _scroll.MaxValue = 1;
                _scroll.Page = Math.Max(0.001, Viewport.ViewSpanNorm);
                _scroll.Step = Viewport.ViewSpanNorm * 0.05;
                _scroll.SetValueNoSignal(Viewport.ViewStartNorm);
                _scroll.Modulate = fitted ? new Color(1, 1, 1, 0.45f) : Colors.White;
            }

            if (_panel != null && GodotObject.IsInstanceValid(_panel))
                _panel.MouseDefaultCursorShape = fitted ? CursorShape.Arrow : CursorShape.Move;

            UpdateReadout();
        }
        finally
        {
            _syncing = false;
        }
    }

    /// <summary>File-normalized playhead, or negative to hide.</summary>
    /// <param name="norm">0–1 of the file, or &lt; 0 to clear.</param>
    public void SetPlayheadNorm(float norm)
    {
        if (_display == null) return;
        _display.PlayheadNorm = norm;
    }

    /// <inheritdoc />
    public override void _ExitTree()
    {
        DetachPanel();
        base._ExitTree();
    }

    private void DetachPanel()
    {
        if (_panel != null && GodotObject.IsInstanceValid(_panel))
        {
            _panel.GuiInput -= OnPanelGuiInput;
            _panel.MouseExited -= OnPanelMouseExited;
        }
        _panel = null;
        _display = null;
        _panning = false;
        _pressPending = false;
    }

    private static Button MakeToolButton(string text, float width)
    {
        var button = new Button
        {
            Text = text,
            CustomMinimumSize = new Vector2(width, 22),
            FocusMode = FocusModeEnum.None,
            Flat = false
        };
        button.AddThemeFontSizeOverride("font_size", 11);
        return button;
    }

    private void OnFitPressed()
    {
        Viewport.FitAll();
        NotifyViewChanged();
    }

    private void ZoomFromButton(bool zoomIn)
    {
        float anchor = Viewport.ViewStartNorm + Viewport.ViewSpanNorm * 0.5f;
        float factor = zoomIn ? WaveformViewport.ZoomStepFactor : 1f / WaveformViewport.ZoomStepFactor;
        Viewport.ZoomAt(anchor, factor);
        NotifyViewChanged();
    }

    private void OnScrollChanged(double value)
    {
        if (_syncing) return;
        Viewport.Pan((float)value - Viewport.ViewStartNorm);
        NotifyViewChanged();
    }

    private void NotifyViewChanged()
    {
        SyncChrome();
        ViewChanged?.Invoke();
    }

    private void UpdateReadout()
    {
        if (_readout == null) return;
        string shown = UiUtilities.FormatTime(Viewport.VisibleSeconds);
        string total = UiUtilities.FormatTime(Viewport.DurationSeconds);
        _readout.Text = UiLocalizer.Tf("{0} / {1}", shown, total);
        _readout.TooltipText = UiLocalizer.Tf("Showing {0} of {1}", shown, total);
    }

    private void OnPanelMouseExited()
    {
        if (_display != null)
            _display.HoverNorm = -1f;
        _panning = false;
        _pressPending = false;
    }

    private void OnPanelGuiInput(InputEvent @event)
    {
        if (_display == null || _panel == null) return;
        if (GetViewport()?.IsInputHandled() == true) return;

        if (@event is InputEventMouseButton mb
            && (mb.ButtonIndex == MouseButton.WheelUp || mb.ButtonIndex == MouseButton.WheelDown)
            && mb.Pressed)
        {
            bool zoomWheel = mb.CtrlPressed || mb.MetaPressed;
            if (zoomWheel)
            {
                float anchor = _display.XToFileNorm(mb.Position.X);
                float factor = mb.ButtonIndex == MouseButton.WheelUp
                    ? WaveformViewport.ZoomStepFactor
                    : 1f / WaveformViewport.ZoomStepFactor;
                Viewport.ZoomAt(anchor, factor);
                NotifyViewChanged();
                _panel.GetViewport()?.SetInputAsHandled();
                return;
            }

            if (!Viewport.IsFitted)
            {
                float delta = Viewport.ViewSpanNorm * 0.15f
                    * (mb.ButtonIndex == MouseButton.WheelUp ? -1f : 1f);
                Viewport.Pan(delta);
                NotifyViewChanged();
                _panel.GetViewport()?.SetInputAsHandled();
            }
            return;
        }

        if (@event is InputEventMouseButton click && click.ButtonIndex == MouseButton.Left)
        {
            if (click.DoubleClick && click.Pressed)
            {
                PanelDoubleClicked?.Invoke();
                Viewport.FitRange(_selectionStartNorm, _selectionEndNorm);
                NotifyViewChanged();
                _panning = false;
                _pressPending = false;
                _panel.GetViewport()?.SetInputAsHandled();
                return;
            }

            if (click.Pressed)
            {
                _pressPending = true;
                _panning = false;
                _pressPos = click.Position;
                _pressFileNorm = _display.XToFileNorm(click.Position.X);
            }
            else
            {
                if (_pressPending && !_panning)
                    PanelClicked?.Invoke(_pressFileNorm);
                _pressPending = false;
                _panning = false;
            }
            return;
        }

        if (@event is InputEventMouseMotion motion)
        {
            float fileNorm = _display.XToFileNorm(motion.Position.X);
            _display.HoverNorm = fileNorm;
            if (_pressPending && !_panning && !Viewport.IsFitted
                && motion.Position.DistanceTo(_pressPos) > ClickSlopPx)
            {
                _panning = true;
                _pressPending = false;
                _panGrabNorm = _display.XToFileNorm(_pressPos.X);
            }

            if (_panning && (motion.ButtonMask & MouseButtonMask.Left) != 0)
            {
                Viewport.Pan(_panGrabNorm - fileNorm);
                // Keep the grabbed time under the cursor after the pan.
                _panGrabNorm = _display.XToFileNorm(motion.Position.X);
                NotifyViewChanged();
                _panel.GetViewport()?.SetInputAsHandled();
            }
        }
    }
}
