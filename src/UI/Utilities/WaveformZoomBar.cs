// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System;
using Godot;

namespace Cue2.UI.Utilities;

/// <summary>
/// Scrollbar + Play/Pause + Fit / − / + chrome for inspector waveforms.
/// Zoom is duration-based via <see cref="WaveformViewport"/>.
/// The time ruler (top of the panel) parks and scrubs a file-time playhead.
/// </summary>
public partial class WaveformZoomBar : VBoxContainer
{
    /// <summary>Raised after the view window changes and chrome has been synced.</summary>
    public event Action ViewChanged;

    /// <summary>
    /// Raised on a left click that did not pan, did not start a volume add-drag,
    /// and was not a double-click Fit.
    /// Argument is the file-normalized time under the pointer at press.
    /// </summary>
    public event Action<float> PanelClicked;

    /// <summary>Raised when a double-click Fit Range is applied.</summary>
    public event Action PanelDoubleClicked;

    /// <summary>
    /// Raised once when a left-press moves past click slop and should add a timeline node
    /// that follows the pointer (fitted view, or a mostly vertical drag when zoomed).
    /// Argument is the file-normalized time at press. Only raised when subscribed.
    /// </summary>
    public event Action<float> PanelDragAddStarted;

    /// <summary>Raised on pointer motion while a <see cref="PanelDragAddStarted"/> drag is active.</summary>
    public event Action PanelDragAddMoved;

    /// <summary>Raised on left-button release after <see cref="PanelDragAddStarted"/>.</summary>
    public event Action PanelDragAddEnded;

    /// <summary>
    /// Raised when the pointer presses in the time ruler (Y &lt; <see cref="WaveformDisplay.RulerHeight"/>).
    /// Argument is the file-normalized time under the pointer.
    /// </summary>
    public event Action<float> RulerPressed;

    /// <summary>Raised on pointer motion while a ruler playhead scrub is active.</summary>
    public event Action<float> RulerDragged;

    /// <summary>Raised on left-button release after <see cref="RulerPressed"/>.</summary>
    public event Action RulerReleased;

    /// <summary>Raised when the inspector play/pause button is pressed.</summary>
    public event Action PlayPausePressed;

    /// <summary>Duration-based view window shared with the display.</summary>
    public WaveformViewport Viewport { get; } = new WaveformViewport();

    private HScrollBar _scroll;
    private Button _playPauseButton;
    private Label _playheadTimeLabel;
    private Button _fitButton;
    private Button _zoomOutButton;
    private Button _zoomInButton;
    private Label _readout;
    private WaveformDisplay _display;
    private Control _panel;
    private bool _syncing;
    private bool _panning;
    private bool _pressPending;
    private bool _addDragging;
    private bool _rulerScrubbing;
    private bool _transportActive;
    private bool _transportPaused;
    /// <summary>True once the play/pause chrome was applied in the tree for the current state.</summary>
    private bool _playPauseChromeApplied;
    /// <summary>True once the one-time icon sizing was set on the play/pause button.</summary>
    private bool _playPauseIconStyled;
    private Vector2 _pressPos;
    private float _pressFileNorm;
    private float _panGrabNorm;
    private float _selectionStartNorm;
    private float _selectionEndNorm = 1f;
    private const float ClickSlopPx = 5f;

    /// <summary>
    /// Creates the scrollbar and Play / Fit / − / + toolbar.
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

        _playPauseButton = MakeToolButton(string.Empty, 26);
        _playPauseButton.Name = "PlayPauseButton";
        _playPauseButton.ThemeTypeVariation = "AtlasIcons";
        _playPauseButton.Pressed += () => PlayPausePressed?.Invoke();
        toolbar.AddChild(_playPauseButton);

        _playheadTimeLabel = new Label
        {
            Name = "PlayheadTimeLabel",
            Text = "0:00.000",
            SizeFlagsVertical = SizeFlags.ShrinkCenter,
            VerticalAlignment = VerticalAlignment.Center,
            CustomMinimumSize = new Vector2(72, 0),
            MouseFilter = MouseFilterEnum.Ignore
        };
        _playheadTimeLabel.AddThemeFontSizeOverride("font_size", 11);
        toolbar.AddChild(_playheadTimeLabel);

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
        ApplyPlayPauseChrome();
        if (_playheadTimeLabel != null)
        {
            _playheadTimeLabel.TooltipText = UiLocalizer.T(
                "Playhead time. Click the ruler to set it; drag the ruler while playing to seek.");
        }
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

    /// <summary>Updates the playhead time readout next to play/pause.</summary>
    /// <param name="seconds">File time in seconds.</param>
    public void SetPlayheadSeconds(double seconds)
    {
        if (_playheadTimeLabel == null) return;
        _playheadTimeLabel.Text = UiUtilities.FormatTime(Math.Max(0, seconds));
    }

    /// <summary>
    /// Updates the play/pause icon from house playback state.
    /// Pause while the cue is active and not paused; Play otherwise.
    /// </summary>
    /// <param name="isActive">True when an ActiveCue exists for the focused cue.</param>
    /// <param name="isPaused">True when that ActiveCue is transport-paused.</param>
    public void SetTransportState(bool isActive, bool isPaused)
    {
        // Called every frame by the inspector: only touch the button when the state changes.
        if (_playPauseChromeApplied && _transportActive == isActive && _transportPaused == isPaused)
            return;
        _transportActive = isActive;
        _transportPaused = isPaused;
        ApplyPlayPauseChrome();
    }

    /// <summary>Enables or disables the play/pause button (no cue / no media).</summary>
    /// <param name="enabled">True when play from playhead is available.</param>
    public void SetPlayPauseEnabled(bool enabled)
    {
        if (_playPauseButton != null)
            _playPauseButton.Disabled = !enabled;
    }

    /// <inheritdoc />
    public override void _Ready()
    {
        ApplyPlayPauseChrome();
    }

    /// <inheritdoc />
    public override void _Notification(int what)
    {
        // Theme icons can change with the theme: re-apply on the next state check.
        if (what == NotificationThemeChanged && _playPauseChromeApplied)
            ApplyPlayPauseChrome();
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
        EndRulerScrub();
        EndAddDrag(notify: true);
        _panning = false;
        _pressPending = false;
    }

    /// <inheritdoc />
    public override void _Input(InputEvent @event)
    {
        if (_rulerScrubbing)
        {
            HandleRulerGlobalInput(@event);
            return;
        }

        if (!_addDragging)
            return;

        if (@event is InputEventMouseMotion)
        {
            PanelDragAddMoved?.Invoke();
            GetViewport()?.SetInputAsHandled();
            return;
        }

        if (@event is InputEventMouseButton mb
            && mb.ButtonIndex == MouseButton.Left
            && !mb.Pressed)
        {
            EndAddDrag(notify: true);
            GetViewport()?.SetInputAsHandled();
        }
    }

    private void HandleRulerGlobalInput(InputEvent @event)
    {
        if (@event is InputEventMouseMotion)
        {
            RaiseRulerDraggedFromPointer();
            GetViewport()?.SetInputAsHandled();
            return;
        }

        if (@event is InputEventMouseButton mb
            && mb.ButtonIndex == MouseButton.Left
            && !mb.Pressed)
        {
            EndRulerScrub();
            GetViewport()?.SetInputAsHandled();
        }
    }

    private void RaiseRulerDraggedFromPointer()
    {
        if (_display == null || _panel == null || !GodotObject.IsInstanceValid(_panel))
            return;
        // XToFileNorm clamps to the visible view, so outside the panel the hover time and the
        // playhead both pin to the nearest view edge.
        float fileNorm = _display.XToFileNorm(_panel.GetLocalMousePosition().X);
        // This motion is marked handled here, so the panel never sees it: keep the hover time
        // following the pointer from this path while the ruler is captured.
        _display.HoverNorm = fileNorm;
        RulerDragged?.Invoke(fileNorm);
    }

    private void BeginRulerScrub(float fileNorm)
    {
        EndAddDrag(notify: false);
        _pressPending = false;
        _panning = false;
        _rulerScrubbing = true;
        SetProcessInput(true);
        if (_display != null)
            _display.HoverNorm = fileNorm;
        RulerPressed?.Invoke(fileNorm);
    }

    private void EndRulerScrub()
    {
        if (!_rulerScrubbing)
            return;
        _rulerScrubbing = false;
        // The hover time stayed up while the pointer was captured: hide it if the release
        // happened outside the waveform panel.
        if (_display != null && !IsPointerOverPanel())
            _display.HoverNorm = -1f;
        if (!_addDragging)
            SetProcessInput(false);
        RulerReleased?.Invoke();
    }

    /// <summary>True when the mouse pointer is inside the attached waveform panel.</summary>
    private bool IsPointerOverPanel()
    {
        if (_panel == null || !GodotObject.IsInstanceValid(_panel) || !_panel.IsInsideTree())
            return false;
        return _panel.GetGlobalRect().HasPoint(_panel.GetGlobalMousePosition());
    }

    private void ApplyPlayPauseChrome()
    {
        if (_playPauseButton == null)
            return;

        bool showPause = _transportActive && !_transportPaused;
        _playPauseButton.TooltipText = showPause
            ? UiLocalizer.T("Pause")
            : UiLocalizer.T("Play from playhead");

        if (!IsInsideTree())
        {
            _playPauseButton.Text = showPause ? "❚❚" : "▶";
            _playPauseChromeApplied = false;
            return;
        }

        try
        {
            _playPauseButton.Icon = GetThemeIcon(showPause ? "Pause" : "Play", "AtlasIcons");
            _playPauseButton.Text = string.Empty;
            if (!_playPauseIconStyled)
            {
                // A theme override notifies a theme change on every call: set it once.
                _playPauseButton.ExpandIcon = true;
                _playPauseButton.AddThemeConstantOverride("icon_max_width", 14);
                _playPauseIconStyled = true;
            }
            _playPauseChromeApplied = true;
        }
        catch
        {
            _playPauseButton.Text = showPause ? "❚❚" : "▶";
            _playPauseChromeApplied = false;
        }
    }

    private void EndAddDrag(bool notify)
    {
        if (!_addDragging)
            return;
        _addDragging = false;
        SetProcessInput(false);
        _pressPending = false;
        _panning = false;
        if (notify)
            PanelDragAddEnded?.Invoke();
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
        // During a ruler scrub the hover time keeps following the pointer outside the panel;
        // EndRulerScrub hides it on release.
        if (_display != null && !_rulerScrubbing)
            _display.HoverNorm = -1f;
        if (_addDragging || _rulerScrubbing)
            return;
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
                EndAddDrag(notify: true);
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
                _pressPos = click.Position;
                _pressFileNorm = _display.XToFileNorm(click.Position.X);
                if (click.Position.Y >= 0f && click.Position.Y < WaveformDisplay.RulerHeight)
                {
                    BeginRulerScrub(_pressFileNorm);
                    _panel.GetViewport()?.SetInputAsHandled();
                    return;
                }

                _pressPending = true;
                _panning = false;
            }
            else
            {
                if (_rulerScrubbing)
                {
                    EndRulerScrub();
                    _panel.GetViewport()?.SetInputAsHandled();
                    return;
                }

                if (_addDragging)
                    EndAddDrag(notify: true);
                else if (_pressPending && !_panning)
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

            if (_rulerScrubbing)
            {
                RulerDragged?.Invoke(fileNorm);
                _panel.GetViewport()?.SetInputAsHandled();
                return;
            }

            if (_addDragging)
            {
                PanelDragAddMoved?.Invoke();
                _panel.GetViewport()?.SetInputAsHandled();
                return;
            }

            if (_pressPending && !_panning && motion.Position.DistanceTo(_pressPos) > ClickSlopPx)
            {
                bool vertical = Math.Abs(motion.Position.Y - _pressPos.Y)
                    >= Math.Abs(motion.Position.X - _pressPos.X);
                if (PanelDragAddStarted != null && (Viewport.IsFitted || vertical))
                {
                    _pressPending = false;
                    _addDragging = true;
                    SetProcessInput(true);
                    PanelDragAddStarted.Invoke(_pressFileNorm);
                    PanelDragAddMoved?.Invoke();
                    _panel.GetViewport()?.SetInputAsHandled();
                    return;
                }

                if (!Viewport.IsFitted)
                {
                    _panning = true;
                    _pressPending = false;
                    _panGrabNorm = _display.XToFileNorm(_pressPos.X);
                }
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
