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
public partial class TimelineInspector : Control
{
    private GlobalData _globalData;
    private GlobalSignals _globalSignals;
    private HistoryManager _historyManager;
    private MediaEngine _mediaEngine;

    /// <summary>Cancels in-flight timeline waveform batch when the view reloads.</summary>
    private CancellationTokenSource _waveformCts;

    private Cue _focusedCue;

    private Label _infoLabel;
    private MarginContainer _timeLineContainer;

    // Toolbar
    private Button _goToStartButton;
    private Button _playFromPlayheadButton;
    private Label _playheadTimeLabel;
    private Label _durationSummaryLabel;
    private Button _fitButton;
    private Button _zoomOutButton;
    private HSlider _zoomSlider;
    private Button _zoomInButton;

    // Body layout
    private Control _trackSidebar;
    private Control _sidebarContent;
    private ColorRect _sidebarSeparator;
    private Control _rulerHost;
    private ScrollContainer _scrollContainer;
    private Control _timelineArea;
    private TimeGrid _timeGrid;
    private Ruler _ruler;
    private ColorRect _playheadLine;

    private float _scale = 10.0f; // Pixels per second
    private const float RowHeight = 42.0f;
    private const float MinScale = 1.0f;
    private const float MaxScale = 200.0f;
    private const float MinBarWidth = 4.0f;
    private const float InstantBarMinWidth = 8.0f;
    private const float LabelStartOffsetX = 6.0f;
    private const float ZoomStepFactor = 1.4f;
    private const float SidebarWidth = 156.0f;
    private const float CollapseBtnSize = 16.0f;
    private const float SwatchSize = 8.0f;
    private const float SidebarPadX = 4.0f;

    private readonly Dictionary<Cue, ColorRect> _cueToBar = new();
    private readonly Dictionary<Cue, ColorRect> _cueToPreWaitGhost = new();
    private readonly Dictionary<Cue, int> _cueToRow = new();
    private readonly Dictionary<Cue, Label> _cueToTimeLabel = new();
    private readonly Dictionary<Cue, Label> _cueToDurationLabel = new();
    private readonly Dictionary<Cue, Label> _cueToLoopBadge = new();
    private readonly Dictionary<Cue, Button> _cueToCollapseButton = new();
    private readonly Dictionary<Cue, Control> _cueToSidebarRow = new();
    private readonly List<ColorRect> _rowBackgrounds = new();
    private readonly List<TimelineItem> _visibleItems = new();
    /// <summary>Fallback single-cycle length when a looping cue has no measurable segment duration.</summary>
    private const float InfiniteLoopDisplaySeconds = 8.0f;
    /// <summary>Extra content size so bars/labels sit clear of ScrollContainer scrollbars.</summary>
    private const float ScrollbarPadRight = 20.0f;
    private const float ScrollbarPadBottom = 20.0f;

    /// <summary>Cue IDs whose children are hidden in the timeline (local UI state).</summary>
    private readonly HashSet<int> _collapsedCueIds = new();

    /// <summary>Bumped on each full <see cref="LoadTimeline"/> so async waveform work abandons stale runs.</summary>
    private int _timelineLoadGeneration;

    /// <summary>Structure fingerprint of the last full rebuild (skip clear/create when unchanged).</summary>
    private string _timelineStructureKey;

    /// <summary>True when a deferred LoadTimeline is already scheduled this frame.</summary>
    private bool _timelineLoadQueued;

    /// <summary>Playhead time in display/body-aligned seconds (see <see cref="DisplayTimeToBodyTime"/>).</summary>
    private double _playheadSeconds;
    private bool _scrubbingPlayhead;
    private bool _followLivePlayhead;
    private bool _clickingEmptyTimeline;
    private double _contentMaxTime;

    private float _prevOffset;
    private float _prevVOffset;
    private float _prevScale;
    private Vector2 _prevSize = Vector2.Zero;

    private bool _dragging;
    private Vector2 _initialBarPos;
    private Vector2 _initialMousePos;
    private Cue _draggedCue;
    /// <summary>True after the first real pre-wait change in the current drag (history recorded).</summary>
    private bool _preWaitDragHistoryRecorded;
    private double _lastClickTime;
    private int _lastClickCueId = -1;

    /// <summary>
    /// Called when the node enters the scene tree for the first time.
    /// Initializes references and connects signals.
    /// </summary>
    public override void _Ready()
    {
        _globalData = GetNode<GlobalData>("/root/GlobalData");
        _globalSignals = GetNode<GlobalSignals>("/root/GlobalSignals");
        _mediaEngine = GetNodeOrNull<MediaEngine>("/root/MediaEngine");
        _historyManager = _globalData?.HistoryManager;

        FocusMode = FocusModeEnum.Click;

        _globalSignals.ShellFocused += ShellSelected;
        _globalSignals.ShowTimelineWaveformsChanged += OnShowTimelineWaveformsChanged;
        _globalSignals.NewSession += OnNewSession;
        if (_historyManager != null)
            _historyManager.HistoryRestored += OnHistoryRestored;

        _infoLabel = GetNode<Label>("%InfoLabel");
        _timeLineContainer = GetNode<MarginContainer>("%TimelineContainer");
        if (_infoLabel != null)
            _infoLabel.AddThemeColorOverride("font_color", GlobalStyles.DisabledColor);

        VisibilityChanged += OnTimelineVisibilityChanged;
        SetProcess(false);

        WireToolbar();
        WireBody();

        if (_globalData != null && _globalData.FocusedCue >= 0)
            ShellSelected(_globalData.FocusedCue);
        else
            ShowNoSelection();
        UpdateIdleProcess();
    
        UiLocalizer.LocalizeTree(this);
        if (_globalSignals != null)
            _globalSignals.LocaleChanged += OnLocaleChanged;
}

    /// <summary>
    /// Disconnects signals and cleans up when leaving the tree.
    /// </summary>
    public override void _ExitTree()
    {
        if (_globalSignals != null)
            _globalSignals.LocaleChanged -= OnLocaleChanged;

        try { _waveformCts?.Cancel(); } catch { /* ignore */ }
        try { _waveformCts?.Dispose(); } catch { /* ignore */ }
        _waveformCts = null;

        if (_globalSignals != null)
        {
            _globalSignals.ShellFocused -= ShellSelected;
            _globalSignals.ShowTimelineWaveformsChanged -= OnShowTimelineWaveformsChanged;
            _globalSignals.NewSession -= OnNewSession;
        }
        if (_historyManager != null)
            _historyManager.HistoryRestored -= OnHistoryRestored;

        if (_zoomSlider != null)
            _zoomSlider.ValueChanged -= OnZoomChanged;
        if (_goToStartButton != null)
            _goToStartButton.Pressed -= OnGoToStartPressed;
        if (_playFromPlayheadButton != null)
            _playFromPlayheadButton.Pressed -= OnPlayFromPlayheadPressed;
        if (_fitButton != null)
            _fitButton.Pressed -= OnFitPressed;
        if (_zoomInButton != null)
            _zoomInButton.Pressed -= OnZoomInPressed;
        if (_zoomOutButton != null)
            _zoomOutButton.Pressed -= OnZoomOutPressed;
        if (_ruler != null)
            _ruler.GuiInput -= OnRulerGuiInput;
        if (_timelineArea != null)
            _timelineArea.GuiInput -= OnTimelineAreaGuiInput;
        if (_scrollContainer != null)
            _scrollContainer.GuiInput -= OnScrollContainerGuiInput;

        VisibilityChanged -= OnTimelineVisibilityChanged;

        // Explicitly free dynamic timeline nodes so CanvasItem RIDs are not leaked on exit
        // (playhead / bars may have been detached from the tree by clear/rebuild paths).
        ClearTimelineVisuals();
        FreeNodeIfValid(ref _playheadLine);
        FreeNodeIfValid(ref _timeGrid);
        FreeNodeIfValid(ref _ruler);
        FreeNodeIfValid(ref _sidebarSeparator);
        FreeNodeIfValid(ref _sidebarContent);

        base._ExitTree();
    }

    /// <summary>
    /// Detaches and frees a dynamically created node, clearing the field reference.
    /// </summary>
    private static void FreeNodeIfValid<T>(ref T node) where T : Node
    {
        if (node == null) return;
        if (IsInstanceValid(node))
        {
            var parent = node.GetParent();
            if (parent != null)
                parent.RemoveChild(node);
            node.QueueFree();
        }
        node = null;
    }

    private void WireToolbar()
    {
        _zoomSlider = GetNode<HSlider>("%ZoomSlider");
        _zoomSlider.MinValue = MinScale;
        _zoomSlider.MaxValue = MaxScale;
        _zoomSlider.Value = _scale;
        _zoomSlider.ValueChanged += OnZoomChanged;

        _goToStartButton = GetNodeOrNull<Button>("%GoToStartButton");
        if (_goToStartButton != null)
        {
            TrySetAtlasIcon(_goToStartButton, "Left", "⏮");
            _goToStartButton.Pressed += OnGoToStartPressed;
        }

        _playFromPlayheadButton = GetNodeOrNull<Button>("%PlayFromPlayheadButton");
        if (_playFromPlayheadButton != null)
        {
            TrySetAtlasIcon(_playFromPlayheadButton, "Play", "▶");
            _playFromPlayheadButton.Pressed += OnPlayFromPlayheadPressed;
        }

        _playheadTimeLabel = GetNodeOrNull<Label>("%PlayheadTimeLabel");
        _durationSummaryLabel = GetNodeOrNull<Label>("%DurationSummaryLabel");
        UpdatePlayheadTimeLabel();

        _fitButton = GetNodeOrNull<Button>("%FitButton");
        if (_fitButton != null)
            _fitButton.Pressed += OnFitPressed;

        _zoomOutButton = GetNodeOrNull<Button>("%ZoomOutButton");
        if (_zoomOutButton != null)
            _zoomOutButton.Pressed += OnZoomOutPressed;

        _zoomInButton = GetNodeOrNull<Button>("%ZoomInButton");
        if (_zoomInButton != null)
            _zoomInButton.Pressed += OnZoomInPressed;
    }

    private void WireBody()
    {
        // Corner cell above the track list — same height as RulerHost so rows align with bars.
        var sidebarCorner = GetNodeOrNull<Control>("%SidebarRulerCorner");
        if (sidebarCorner != null)
        {
            var cornerBg = new ColorRect
            {
                Name = "CornerBg",
                Color = new Color(0.08f, 0.09f, 0.1f, 0.95f),
                MouseFilter = MouseFilterEnum.Ignore
            };
            sidebarCorner.AddChild(cornerBg);
            cornerBg.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        }

        _trackSidebar = GetNodeOrNull<Control>("%TrackSidebar");
        if (_trackSidebar != null)
        {
            _trackSidebar.ClipContents = true;
            _trackSidebar.MouseFilter = MouseFilterEnum.Stop;

            // Dark professional sidebar background
            var sidebarBg = new ColorRect
            {
                Name = "SidebarBg",
                Color = new Color(0.07f, 0.075f, 0.08f, 1f),
                MouseFilter = MouseFilterEnum.Ignore,
                ZIndex = -2
            };
            _trackSidebar.AddChild(sidebarBg);
            sidebarBg.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);

            _sidebarContent = new Control
            {
                Name = "SidebarContent",
                MouseFilter = MouseFilterEnum.Pass,
                ZIndex = 0
            };
            _trackSidebar.AddChild(_sidebarContent);
            _sidebarContent.Position = Vector2.Zero;
            _sidebarContent.Size = new Vector2(SidebarWidth, 100);
        }

        // Full-height separator between sidebar and tracks (body-level, not content-height)
        _sidebarSeparator = new ColorRect
        {
            Name = "SidebarSeparator",
            Color = new Color(0.32f, 0.34f, 0.36f, 0.9f),
            MouseFilter = MouseFilterEnum.Ignore,
            ZIndex = 25
        };
        AddChild(_sidebarSeparator);

        _scrollContainer = GetNode<ScrollContainer>("%TimelineScrollContainer");
        _scrollContainer.HorizontalScrollMode = ScrollContainer.ScrollMode.ShowAlways;
        _scrollContainer.VerticalScrollMode = ScrollContainer.ScrollMode.ShowAlways;
        _scrollContainer.GuiInput += OnScrollContainerGuiInput;

        _timelineArea = GetNode<Control>("%TimelineArea");
        _timelineArea.MouseFilter = MouseFilterEnum.Stop;
        _timelineArea.GuiInput += OnTimelineAreaGuiInput;
        _timelineArea.FocusMode = FocusModeEnum.Click;

        // Time grid (background of timeline content)
        _timeGrid = new TimeGrid
        {
            Name = "TimeGrid",
            MouseFilter = MouseFilterEnum.Ignore,
            ZIndex = -2,
            ZoomScale = _scale
        };
        _timelineArea.AddChild(_timeGrid);

        _rulerHost = GetNodeOrNull<Control>("%RulerHost");
        if (_rulerHost == null)
        {
            _rulerHost = new Control { CustomMinimumSize = new Vector2(0, 26) };
        }

        // Keep corner spacer height locked to the ruler host height.
        SyncSidebarRulerCornerHeight();

        _ruler = new Ruler
        {
            Name = "Ruler",
            MouseFilter = MouseFilterEnum.Stop,
            ContentOriginX = 0f,
            ZoomScale = _scale
        };
        _rulerHost.AddChild(_ruler);
        _ruler.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        _ruler.GuiInput += OnRulerGuiInput;

        // Create and parent playhead immediately so it is never an orphaned ObjectDB instance.
        // (Previously created unparented here and only added in EnsurePlayheadLine.)
        _playheadLine = new ColorRect
        {
            Name = "PlayheadLine",
            Color = new Color(0.95f, 0.35f, 0.15f, 0.95f),
            MouseFilter = MouseFilterEnum.Ignore,
            ZIndex = 50,
            Size = new Vector2(2, RowHeight),
            Visible = false
        };
        if (_timelineArea != null)
            _timelineArea.AddChild(_playheadLine);
    }

    /// <summary>
    /// Matches the left corner cell height to <see cref="_rulerHost"/> so track rows
    /// align with timeline bars (ruler sits above the scroll area on the right).
    /// </summary>
    private void SyncSidebarRulerCornerHeight()
    {
        var corner = GetNodeOrNull<Control>("%SidebarRulerCorner");
        if (corner == null || _rulerHost == null) return;

        float h = _rulerHost.CustomMinimumSize.Y;
        if (_rulerHost.Size.Y > 1f)
            h = _rulerHost.Size.Y;
        if (h < 1f) h = 26f;

        corner.CustomMinimumSize = new Vector2(SidebarWidth, h);
    }

    private void TrySetAtlasIcon(Button button, string iconName, string fallbackText)
    {
        if (button == null) return;
        try
        {
            button.Icon = GetThemeIcon(iconName, "AtlasIcons");
            button.ExpandIcon = true;
            button.Text = string.Empty;
        }
        catch
        {
            button.Text = fallbackText;
        }
    }

    private void OnShowTimelineWaveformsChanged(bool _)
    {
        if (IsInstanceValid(this) && Visible)
            LoadTimeline();
    }

    private void OnNewSession()
    {
        _collapsedCueIds.Clear();
        _playheadSeconds = 0;
        _followLivePlayhead = false;
        UpdatePlayheadTimeLabel();
        ShowNoSelection();
    }

    private void OnHistoryRestored(int scope)
    {
        if (!IsInstanceValid(this) || !Visible) return;
        if (scope == (int)HistoryManager.HistoryScope.Settings
            || scope == (int)HistoryManager.HistoryScope.Cue
            || scope == (int)HistoryManager.HistoryScope.Cuelist
            || scope == (int)HistoryManager.HistoryScope.MultiCue)
        {
            LoadTimeline();
        }
    }

    /// <summary>
    /// Enables per-frame timeline work only while the tab is visible and a cue is loaded.
    /// </summary>
    private void UpdateIdleProcess()
    {
        bool need = Visible
                    && _focusedCue != null
                    && _timeLineContainer != null
                    && _timeLineContainer.Visible;
        SetProcess(need);
    }

    /// <summary>
    /// Reloads and gates process when the Timeline tab is shown or hidden.
    /// </summary>
    private void OnTimelineVisibilityChanged()
    {
        UpdateIdleProcess();
        if (Visible)
            LoadTimeline();
    }

    /// <summary>
    /// Called every frame. Syncs ruler, sidebar scroll, playhead, and live follow during playback.
    /// </summary>
    /// <param name="delta">The time elapsed since the previous frame.</param>
    public override void _Process(double delta)
    {
        if (!Visible || _focusedCue == null || _timeLineContainer == null || !_timeLineContainer.Visible)
            return;

        SyncSidebarRulerCornerHeight();
        LayoutSidebarSeparator();
        SyncSidebarScroll();

        if (_followLivePlayhead && !_scrubbingPlayhead)
            TryFollowLivePlayhead();

        if (_ruler == null || _scrollContainer == null) return;

        float currentOffset = _scrollContainer.GetHScroll();
        float currentScale = _scale;
        Vector2 currentSize = _rulerHost != null
            ? new Vector2(_rulerHost.Size.X, _rulerHost.Size.Y)
            : new Vector2(_scrollContainer.Size.X, 26);

        bool needsRedraw = false;

        if (Mathf.Abs(currentOffset - _prevOffset) > 0.001f)
        {
            _ruler.Offset = currentOffset;
            _prevOffset = currentOffset;
            needsRedraw = true;
        }

        if (Mathf.Abs(currentScale - _prevScale) > 0.001f)
        {
            _ruler.ZoomScale = currentScale;
            if (_timeGrid != null)
                _timeGrid.ZoomScale = currentScale;
            _prevScale = currentScale;
            needsRedraw = true;
        }

        if (currentSize != _prevSize)
        {
            _ruler.Size = currentSize;
            _prevSize = currentSize;
            needsRedraw = true;
        }

        _ruler.PlayheadSeconds = _playheadSeconds;
        if (needsRedraw)
            _ruler.QueueRedraw();
        else if (_followLivePlayhead || _scrubbingPlayhead)
            _ruler.QueueRedraw();

        if (_timeGrid != null && needsRedraw)
            _timeGrid.QueueRedraw();

        UpdatePlayheadLineGeometry();
    }

    /// <summary>
    /// Handles keyboard shortcuts when the timeline is visible and no LineEdit is focused.
    /// Left/Right nudge playhead; Home resets. Play is via the toolbar button only.
    /// </summary>
    /// <param name="event">The input event.</param>
    public override void _UnhandledKeyInput(InputEvent @event)
    {
        if (!Visible || _focusedCue == null || _timeLineContainer == null || !_timeLineContainer.Visible)
            return;

        if (IsTextInputFocused())
            return;

        if (@event is not InputEventKey key || !key.Pressed || key.Echo)
            return;

        bool handled = false;
        switch (key.Keycode)
        {
            case Key.Left:
                NudgePlayhead(key.ShiftPressed ? -1.0 : -0.1);
                handled = true;
                break;
            case Key.Right:
                NudgePlayhead(key.ShiftPressed ? 1.0 : 0.1);
                handled = true;
                break;
            case Key.Home:
                SetPlayheadSeconds(0);
                _followLivePlayhead = false;
                handled = true;
                break;
        }

        if (handled)
            GetViewport()?.SetInputAsHandled();
    }

    private static bool IsTextInputFocused()
    {
        var focus = GodotObject.IsInstanceValid(Engine.GetMainLoop())
            ? ((SceneTree)Engine.GetMainLoop()).Root?.GuiGetFocusOwner()
            : null;
        return focus is LineEdit or TextEdit or CodeEdit;
    }

    private void NudgePlayhead(double deltaSeconds)
    {
        _followLivePlayhead = false;
        SetPlayheadSeconds(_playheadSeconds + deltaSeconds);
        EnsurePlayheadVisible();
    }

    private void LayoutSidebarSeparator()
    {
        if (_sidebarSeparator == null || !IsInstanceValid(_sidebarSeparator))
            return;

        bool show = _timeLineContainer != null && _timeLineContainer.Visible && _focusedCue != null;
        _sidebarSeparator.Visible = show;
        if (!show) return;

        // Align with right edge of the left column (corner + track sidebar).
        Control leftEdge = _trackSidebar;
        var leftCol = GetNodeOrNull<Control>("TimelineContainer/VBoxContainer/Body/LeftColumn");
        if (leftCol != null && IsInstanceValid(leftCol))
            leftEdge = leftCol;
        if (leftEdge == null) return;

        var sideOrigin = leftEdge.GlobalPosition - GlobalPosition;
        float x = sideOrigin.X + leftEdge.Size.X - 1f;
        _sidebarSeparator.Position = new Vector2(x, 0);
        _sidebarSeparator.Size = new Vector2(1f, Size.Y);
    }

    private void SyncSidebarScroll()
    {
        if (_sidebarContent == null || _scrollContainer == null || !IsInstanceValid(_sidebarContent))
            return;

        float vScroll = _scrollContainer.GetVScroll();
        if (Mathf.Abs(vScroll - _prevVOffset) > 0.01f || _sidebarContent.Position.Y != -vScroll)
        {
            _sidebarContent.Position = new Vector2(0, -vScroll);
            _prevVOffset = vScroll;
        }

        // Keep sidebar content width matched to sidebar
        if (_trackSidebar != null)
        {
            float h = Math.Max(_timelineArea?.CustomMinimumSize.Y ?? 0, _trackSidebar.Size.Y);
            _sidebarContent.Size = new Vector2(_trackSidebar.Size.X, Math.Max(h, RowHeight));
        }
    }

    /// <summary>
    /// Handles changes to the zoom slider value.
    /// </summary>
    /// <param name="value">The new zoom scale value.</param>
    private void OnZoomChanged(double value)
    {
        _scale = (float)value;
        ApplyScaleToVisuals();
    }

    private void OnZoomInPressed()
    {
        SetScaleAnchored(_scale * ZoomStepFactor, _scrollContainer?.Size.X * 0.5f ?? 0f);
    }

    private void OnZoomOutPressed()
    {
        SetScaleAnchored(_scale / ZoomStepFactor, _scrollContainer?.Size.X * 0.5f ?? 0f);
    }

    private void OnGoToStartPressed()
    {
        _followLivePlayhead = false;
        SetPlayheadSeconds(0);
        if (_scrollContainer != null)
            _scrollContainer.SetHScroll(0);
    }

    private void OnFitPressed()
    {
        if (_scrollContainer == null || _contentMaxTime <= 1e-6)
            return;

        float viewW = Math.Max(40f, _scrollContainer.Size.X - 8f);
        // Leave a little padding on the right
        float targetScale = (float)(viewW / (_contentMaxTime + 0.5));
        targetScale = Mathf.Clamp(targetScale, MinScale, MaxScale);

        _scale = targetScale;
        if (_zoomSlider != null && Math.Abs(_zoomSlider.Value - _scale) > 0.001)
            _zoomSlider.SetValueNoSignal(_scale);
        ApplyScaleToVisuals();
        _scrollContainer.SetHScroll(0);
    }

    /// <summary>
    /// Sets zoom scale while keeping the time under <paramref name="anchorViewportX"/> stable.
    /// </summary>
    private void SetScaleAnchored(float newScale, float anchorViewportX)
    {
        newScale = Mathf.Clamp(newScale, MinScale, MaxScale);
        if (Mathf.Abs(newScale - _scale) < 0.001f) return;

        float hScroll = _scrollContainer?.GetHScroll() ?? 0f;
        double timeUnderCursor = (hScroll + anchorViewportX) / Math.Max(0.001f, _scale);

        _scale = newScale;
        if (_zoomSlider != null && Math.Abs(_zoomSlider.Value - _scale) > 0.001)
            _zoomSlider.SetValueNoSignal(_scale);

        ApplyScaleToVisuals();

        if (_scrollContainer != null)
        {
            float newScroll = (float)(timeUnderCursor * _scale) - anchorViewportX;
            _scrollContainer.SetHScroll(Mathf.Max(0, (int)newScroll));
        }
    }

    private void ApplyScaleToVisuals()
    {
        UpdateAllPositionsAndSizes();
        UpdatePlayheadLineGeometry();
        if (_ruler != null)
        {
            _ruler.ZoomScale = _scale;
            _ruler.QueueRedraw();
        }
        if (_timeGrid != null)
        {
            _timeGrid.ZoomScale = _scale;
            _timeGrid.QueueRedraw();
        }
    }

    private void OnRulerGuiInput(InputEvent @event)
    {
        if (_focusedCue == null || _scrollContainer == null) return;

        if (@event is InputEventMouseButton mb && mb.ButtonIndex == MouseButton.Left)
        {
            if (mb.Pressed)
            {
                _scrubbingPlayhead = true;
                _followLivePlayhead = false;
                SetPlayheadFromRulerLocalX(mb.Position.X);
                GrabFocusSafe();
            }
            else
            {
                _scrubbingPlayhead = false;
            }
            _ruler.GetViewport()?.SetInputAsHandled();
        }
        else if (@event is InputEventMouseMotion mm && _scrubbingPlayhead)
        {
            SetPlayheadFromRulerLocalX(mm.Position.X);
            _ruler.GetViewport()?.SetInputAsHandled();
        }
        else if (@event is InputEventMouseButton wheel
                 && (wheel.ButtonIndex == MouseButton.WheelUp || wheel.ButtonIndex == MouseButton.WheelDown)
                 && wheel.Pressed
                 && (wheel.CtrlPressed || wheel.MetaPressed))
        {
            ZoomAtViewportX(wheel.Position.X, wheel.ButtonIndex == MouseButton.WheelUp);
            _ruler.GetViewport()?.SetInputAsHandled();
        }
    }

    private void OnTimelineAreaGuiInput(InputEvent @event)
    {
        if (_focusedCue == null) return;

        if (@event is InputEventMouseButton mb)
        {
            if ((mb.ButtonIndex == MouseButton.WheelUp || mb.ButtonIndex == MouseButton.WheelDown)
                && mb.Pressed
                && (mb.CtrlPressed || mb.MetaPressed))
            {
                // Position is local to timeline area; convert to viewport-relative for scroll container
                float viewportX = mb.Position.X - (_scrollContainer?.GetHScroll() ?? 0);
                // Actually timeline area is inside scroll: local X is content X
                float contentX = mb.Position.X;
                float viewX = contentX - (_scrollContainer?.GetHScroll() ?? 0);
                ZoomAtViewportX(viewX, mb.ButtonIndex == MouseButton.WheelUp);
                _timelineArea.GetViewport()?.SetInputAsHandled();
                return;
            }

            if (mb.ButtonIndex == MouseButton.Left && mb.Pressed)
            {
                // Empty area click (not on a bar — bars consume the event themselves)
                _clickingEmptyTimeline = true;
                _followLivePlayhead = false;
                SetPlayheadFromContentX(mb.Position.X);
                GrabFocusSafe();
                _timelineArea.GetViewport()?.SetInputAsHandled();
            }
            else if (mb.ButtonIndex == MouseButton.Left && !mb.Pressed)
            {
                _clickingEmptyTimeline = false;
            }
        }
        else if (@event is InputEventMouseMotion mm && _clickingEmptyTimeline)
        {
            SetPlayheadFromContentX(mm.Position.X);
            _timelineArea.GetViewport()?.SetInputAsHandled();
        }
    }

    private void OnScrollContainerGuiInput(InputEvent @event)
    {
        if (@event is InputEventMouseButton wheel
            && (wheel.ButtonIndex == MouseButton.WheelUp || wheel.ButtonIndex == MouseButton.WheelDown)
            && wheel.Pressed
            && (wheel.CtrlPressed || wheel.MetaPressed))
        {
            ZoomAtViewportX(wheel.Position.X, wheel.ButtonIndex == MouseButton.WheelUp);
            _scrollContainer.GetViewport()?.SetInputAsHandled();
        }
    }

    private void ZoomAtViewportX(float viewportX, bool zoomIn)
    {
        float factor = zoomIn ? ZoomStepFactor : 1f / ZoomStepFactor;
        SetScaleAnchored(_scale * factor, viewportX);
    }

    private void SetPlayheadFromRulerLocalX(float localX)
    {
        float contentX = localX + (_scrollContainer?.GetHScroll() ?? 0);
        SetPlayheadFromContentX(contentX);
    }

    private void SetPlayheadFromContentX(float contentX)
    {
        double time = contentX / Math.Max(0.001f, _scale);
        SetPlayheadSeconds(time);
    }

    /// <summary>
    /// Sets the playhead to the given display-timeline seconds (clamped ≥ 0).
    /// </summary>
    /// <param name="seconds">Display time in seconds.</param>
    private void SetPlayheadSeconds(double seconds)
    {
        if (seconds < 0) seconds = 0;
        _playheadSeconds = seconds;
        UpdatePlayheadTimeLabel();
        UpdatePlayheadLineGeometry();
        _ruler?.QueueRedraw();
    }

    private void UpdatePlayheadTimeLabel()
    {
        if (_playheadTimeLabel == null || !IsInstanceValid(_playheadTimeLabel)) return;
        _playheadTimeLabel.Text = UiUtilities.FormatTime(_playheadSeconds);
    }

    private void UpdateDurationSummary()
    {
        if (_durationSummaryLabel == null || !IsInstanceValid(_durationSummaryLabel)) return;
        int tracks = _visibleItems.Count;
        string total = FormatCompactDuration(_contentMaxTime);
        _durationSummaryLabel.Text = tracks > 0
            ? $"Total: {total} · {tracks} track{(tracks == 1 ? "" : "s")}"
            : string.Empty;
    }

    private static string FormatCompactDuration(double seconds)
    {
        if (seconds < 0) return "∞";
        int totalSec = (int)Math.Floor(Math.Max(0, seconds));
        int min = totalSec / 60;
        int sec = totalSec % 60;
        if (min >= 60)
        {
            int hour = min / 60;
            min %= 60;
            return $"{hour}h{min:D2}m:{sec:D2}";
        }
        return $"{min}m:{sec:D2}";
    }

    private void EnsurePlayheadLine()
    {
        if (_timelineArea == null) return;

        // Reuse existing valid line when already under the timeline area.
        if (_playheadLine != null && IsInstanceValid(_playheadLine))
        {
            if (_playheadLine.GetParent() == _timelineArea)
                return;

            // Orphaned / wrong parent from older code paths — free before recreating.
            if (_playheadLine.GetParent() != null)
                _playheadLine.GetParent().RemoveChild(_playheadLine);
            _playheadLine.QueueFree();
            _playheadLine = null;
        }

        _playheadLine = new ColorRect
        {
            Name = "PlayheadLine",
            Color = new Color(0.95f, 0.35f, 0.15f, 0.95f),
            MouseFilter = MouseFilterEnum.Ignore,
            ZIndex = 50,
            Size = new Vector2(2, RowHeight)
        };
        _timelineArea.AddChild(_playheadLine);
    }

    private void UpdatePlayheadLineGeometry()
    {
        if (_playheadLine == null || !IsInstanceValid(_playheadLine) || _timelineArea == null)
            return;

        float x = (float)(_playheadSeconds * _scale);
        // Stay within drawable content (exclude scrollbar pad) so the line doesn't sit under the bar.
        float h = Math.Max(RowHeight, _timelineArea.CustomMinimumSize.Y - ScrollbarPadBottom);
        if (_cueToRow.Count > 0)
            h = Math.Max(h, _cueToRow.Values.Max() * RowHeight + RowHeight);
        _playheadLine.Position = new Vector2(x, 0);
        _playheadLine.Size = new Vector2(2, h);
        _playheadLine.Visible = _focusedCue != null && _timeLineContainer != null && _timeLineContainer.Visible;
    }

    /// <summary>
    /// Auto-scrolls horizontally so the playhead stays within the visible viewport.
    /// </summary>
    private void EnsurePlayheadVisible()
    {
        if (_scrollContainer == null) return;
        float px = (float)(_playheadSeconds * _scale);
        float viewW = _scrollContainer.Size.X;
        float hScroll = _scrollContainer.GetHScroll();
        const float margin = 40f;

        if (px < hScroll + margin)
            _scrollContainer.SetHScroll(Mathf.Max(0, (int)(px - margin)));
        else if (px > hScroll + viewW - margin)
            _scrollContainer.SetHScroll(Mathf.Max(0, (int)(px - viewW + margin)));
    }

    private void GrabFocusSafe()
    {
        try { GrabFocus(); } catch { /* ignore */ }
    }

    /// <summary>
    /// Display timeline origin for the focused cue's body (pre-wait start).
    /// For a root cue this is 0; for nested focus it is the parent's action start.
    /// </summary>
    private double GetFocusedBodyOriginDisplayTime()
    {
        if (_focusedCue == null) return 0;
        double actionStart = ComputeActionStart(_focusedCue);
        return Math.Max(0.0, actionStart - Math.Max(0.0, _focusedCue.PreWait));
    }

    /// <summary>Maps inspector display time → ActiveCue body timeline seconds.</summary>
    private double DisplayTimeToBodyTime(double displaySeconds)
    {
        return Math.Max(0.0, displaySeconds - GetFocusedBodyOriginDisplayTime());
    }

    /// <summary>Maps ActiveCue body timeline seconds → inspector display time.</summary>
    private double BodyTimeToDisplayTime(double bodySeconds)
    {
        return Math.Max(0.0, GetFocusedBodyOriginDisplayTime() + bodySeconds);
    }

    private void TryFollowLivePlayhead()
    {
        var active = FindPlayingFocusedActiveCue();
        if (active == null)
        {
            _followLivePlayhead = false;
            return;
        }

        try
        {
            double bodyT = active.GetCueTimelineSeconds();
            SetPlayheadSeconds(BodyTimeToDisplayTime(bodyT));
            EnsurePlayheadVisible();
        }
        catch
        {
            _followLivePlayhead = false;
        }
    }

    private ActiveCue FindPlayingFocusedActiveCue()
    {
        if (_focusedCue == null || _globalData?.CueCommandExecutor == null)
            return null;

        foreach (var root in _globalData.CueCommandExecutor.ActiveCues)
        {
            if (root == null || !IsInstanceValid(root)) continue;
            try
            {
                foreach (var active in root.EnumerateSelfAndDescendants())
                {
                    if (active == null || !IsInstanceValid(active)) continue;
                    if (active.Cue != null && active.Cue.Id == _focusedCue.Id)
                        return active;
                }
            }
            catch
            {
                // Active may be mid-teardown
            }
        }
        return null;
    }

    /// <summary>
    /// Starts (or seeks) the focused cue from the current playhead position.
    /// </summary>
    private void OnPlayFromPlayheadPressed()
    {
        if (_focusedCue == null)
        {
            _globalSignals?.EmitSignal(nameof(GlobalSignals.Log),
                "Timeline: No cue selected to play", 1);
            return;
        }

        if (!_focusedCue.Armed)
        {
            _globalSignals?.EmitSignal(nameof(GlobalSignals.Log),
                $"Timeline: Cue \"{_focusedCue.Name}\" is disarmed", 1);
            return;
        }

        var executor = _globalData?.CueCommandExecutor;
        if (executor == null)
        {
            GD.PrintErr("TimelineInspector:OnPlayFromPlayheadPressed - CueCommandExecutor missing");
            return;
        }

        try { _focusedCue.CalculateTotalDuration(); } catch { /* best-effort */ }

        double bodyTime = DisplayTimeToBodyTime(_playheadSeconds);
        double pre = Math.Max(0.0, _focusedCue.PreWait);
        double playable = _focusedCue.Duration < 0
            ? -1.0
            : pre + Math.Max(0.0, _focusedCue.Duration);

        if (playable >= 0 && bodyTime >= playable - 1e-4)
        {
            _globalSignals?.EmitSignal(nameof(GlobalSignals.Log),
                $"Timeline: Playhead is past the end of \"{_focusedCue.Name}\" — nothing to play", 1);
            GD.Print(
                $"TimelineInspector:OnPlayFromPlayheadPressed - past end body={bodyTime:F3}s playable={playable:F3}s");
            return;
        }

        GD.Print(
            $"TimelineInspector:OnPlayFromPlayheadPressed - {_focusedCue.Name} @ display={_playheadSeconds:F3}s " +
            $"body={bodyTime:F3}s (pre={pre:F3}s content={Math.Max(0, bodyTime - pre):F3}s playable={playable:F3}s)");

        var existing = FindPlayingFocusedActiveCue();
        if (existing != null)
        {
            existing.RequestSeek(bodyTime, relative: false);
            _followLivePlayhead = true;
            return;
        }

        executor.ActivateSequenceFrom(_focusedCue, controlGoFadeIn: null, startAtTimelineSeconds: bodyTime);
        _followLivePlayhead = true;
        _globalSignals?.EmitSignal(nameof(GlobalSignals.Log),
            $"Timeline: Playing \"{_focusedCue.Name}\" from {UiUtilities.FormatTime(_playheadSeconds)}", 0);
    }

    /// <summary>
    /// Loads and renders the timeline for the focused cue.
    /// Clears existing UI elements and rebuilds based on the cue hierarchy.
    /// </summary>

    /// <summary>
    /// Re-localizes panel chrome when the UI language changes.
    /// </summary>
    /// <param name="localeCode">New locale code.</param>
    private void OnLocaleChanged(string localeCode)
    {
        if (!GodotObject.IsInstanceValid(this))
            return;
        UiLocalizer.LocalizeTree(this);
        if (_focusedCue == null)
            ShowNoSelection();
    }

}
