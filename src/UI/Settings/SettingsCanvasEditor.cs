// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
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
using Cue2.UI.Popups;
using Cue2.UI.Utilities;

namespace Cue2.UI.Settings;

/// <summary>
/// Canvas editor UI for arranging screens and target layers on the video canvas.
/// Left: Screens + Target Layers trees. Center: interactive stage (move/resize). Right: properties.
/// </summary>
public partial class SettingsCanvasEditor : Control
{
    private enum SelectionKind
    {
        None,
        Canvas,
        Screen,
        Layer
    }

    private enum DragMode
    {
        None,
        Move,
        ResizeNW,
        ResizeN,
        ResizeNE,
        ResizeE,
        ResizeSE,
        ResizeS,
        ResizeSW,
        ResizeW
    }

    private GlobalData _globalData;
    private GlobalSignals _globalSignals;
    /// <summary>Stored so we can disconnect from the process-lifetime autoload on exit.</summary>
    private Callable _displaysChangedCallable;
    /// <summary>Stored so we can disconnect from the process-lifetime autoload on exit.</summary>
    private Callable _canvasSizeChangedCallable;
    private Callable _layerGeometryChangedCallable;
    private HistoryManager _historyManager;
    private Canvas _canvas;
    private DisplaysManager _displaysManager;
    private ResourceInUseDeleteDialog _activeLayerDeleteDialog;

    /// <summary>
    /// Coalesce key for the active stage drag (move/resize). Sealed when the drag ends.
    /// </summary>
    private string _activeDragCoalesceKey;

    // Hierarchy – two trees
    private Godot.Tree _screensTree;
    private Button _refreshScreensButton;
    private Godot.Tree _layersTree;
    private Button _canvasSelectButton;
    private Button _addScreenButton;
    private Button _newTargetLayerButton;
    private Button _moveLayerUpButton;
    private Button _moveLayerDownButton;

    // Canvas view
    private Panel _canvasOutlinePanel;
    private SubViewportContainer _subViewportContainer;
    private SubViewport _viewport;
    private Control _control;
    private ScrollContainer _scrollContainer;
    private CanvasLayer _canvasLayer;
    private ColorRect _backgroundRect;
    private Button _zoomInButton;
    private Button _zoomOutButton;
    private Button _fitButton;
    private LineEdit _zoomPercentLineEdit;

    // Layout: structure | stage | properties — stage collapses first when space is tight
    private HSplitContainer _bodyHSplit;
    private HSplitContainer _centerRightSplit;
    private Control _leftPanel;
    private Control _centerPanel;
    private Control _rightPanel;
    private bool _isApplyingPanelLayout;

    /// <summary>Preferred width for the structure (screens/layers) panel while the stage still has room.</summary>
    private const float LeftPanelPreferredWidth = 180f;

    /// <summary>Preferred width for the properties panel while the stage still has room.</summary>
    private const float RightPanelPreferredWidth = 220f;

    // Properties – empty / canvas
    private Label _emptyPropsLabel;
    private Control _canvasProps;
    private LineEdit _canvasSizeXLineEdit;
    private LineEdit _canvasSizeYLineEdit;
    private CheckBox _canvasTestPatternCheckBox;
    private Button _canvasTestPatternResetButton;

    // Properties – screen
    private Control _outputProps;
    private Label _outputPropsTitle;
    private Label _outputResolutionLabel;
    private LineEdit _screenNameLineEdit;
    private OptionButton _screenOutputOption;
    private LineEdit _outputSizeXLineEdit;
    private LineEdit _outputSizeYLineEdit;
    private LineEdit _outputPosXLineEdit;
    private LineEdit _outputPosYLineEdit;
    private LineEdit _displayOffsetXLineEdit;
    private LineEdit _displayOffsetYLineEdit;
    private CheckBox _screenKeepAspectCheckBox;
    private CheckBox _outputTransparentCheckBox;
    private CheckBox _outputTestPatternCheckBox;
    private Button _deleteScreenButton;
    private Button _screenOutputResetButton;
    private Button _screenSizeResetButton;
    private Button _screenKeepAspectResetButton;
    private Button _screenPosResetButton;
    private Button _screenDisplayOffsetResetButton;
    private Button _screenTransparentResetButton;
    private Button _screenTestPatternResetButton;

    // Properties – layer
    private Control _layerProps;
    private LineEdit _layerNameLineEdit;
    private LineEdit _layerSizeXLineEdit;
    private LineEdit _layerSizeYLineEdit;
    private LineEdit _layerPosXLineEdit;
    private LineEdit _layerPosYLineEdit;
    private CheckBox _layerKeepAspectCheckBox;
    private CheckBox _layerTransparentCheckBox;
    private CheckBox _layerTestPatternCheckBox;
    private CheckBox _layerLockCheckBox;
    private Button _deleteLayerButton;
    private Button _layerSizeResetButton;
    private Button _layerKeepAspectResetButton;
    private Button _layerPosResetButton;
    private Button _layerTransparentResetButton;
    private Button _layerTestPatternResetButton;
    private Button _layerLockResetButton;

    private float _zoom = 0.2f;
    private const float MinZoom = 0.05f;
    private const float MaxZoom = 3.0f;
    private const float HandleSizePx = 10f;
    /// <summary>Extra pixels around resize handles so they stay hittable at low zoom / HiDPI.</summary>
    private const float HandleHitSlopPx = 6f;
    private const float MinItemSize = 16f;
    private const float FitPadding = 48f;

    private bool _isPanning;
    private bool _isUpdatingProps;
    private bool _isRebuildingTree;
    private bool _isDraggingCanvas;
    /// <summary>
    /// Left-press on a stacked selection: wait to see if this is a click (cycle) or a drag (move).
    /// </summary>
    private bool _pendingClickCycle;
    private Vector2 _clickCycleStartLayer;
    /// <summary>Heavy stage setup is deferred until the panel is actually shown.</summary>
    private bool _stageInitialized;

    /// <summary>
    /// Full-rect overlay above the SubViewport that receives stage mouse events.
    /// SubViewport children (outline panel, checker ColorRect) would otherwise swallow
    /// GUI input, and Linux + content-scale makes Viewport.GetMousePosition mismatch
    /// GetGlobalRect so _Input hit-tests miss the middle of the stage.
    /// </summary>
    private Control _stagePointer;

    /// <summary>Last stage-local mouse from a GuiInput event (preferred over polling).</summary>
    private Vector2 _lastStageLocalMouse;

    /// <summary>True when <see cref="_lastStageLocalMouse"/> came from a real stage event.</summary>
    private bool _hasStageLocalMouse;

    /// <summary>
    /// True when Displays history restored while this editor was hidden — refresh on next show.
    /// </summary>
    private bool _needsHistoryRefresh;

    private SelectionKind _selectionKind = SelectionKind.None;
    private int _selectedScreenId = -1;
    private int _selectedLayerId = -1;

    private DragMode _dragMode = DragMode.None;
    private Vector2 _dragStartCanvasMouse;
    private Vector2I _dragStartPos;
    private Vector2I _dragStartSize;

    /// <summary>
    /// Maps OptionButton item index → destination index
    /// (VirtualMonitorIndex, WindowMonitorIndex, or physical monitor index).
    /// </summary>
    private readonly List<int> _outputOptionMonitorMap = new();

    private readonly List<CanvasItemGizmo> _gizmos = new();

    /// <summary>
    /// Interactive visual for a screen or layer rectangle on the stage.
    /// </summary>
    private partial class CanvasItemGizmo : Control
    {
        public Color BorderColor = Colors.Red;
        public Color FillColor = new Color(1, 0, 0, 0.08f);
        public float DashLength = 10f;
        public bool OffsetDash;
        public bool Selected;
        public string LabelText = string.Empty;
        public bool IsScreen;
        public int ItemId;

        public override void _Draw()
        {
            Vector2 size = Size;
            if (size.X < 1 || size.Y < 1)
                return;

            // Soft fill
            Color fill = Selected ? FillColor.Lightened(0.15f) : FillColor;
            fill.A = Selected ? 0.18f : 0.08f;
            DrawRect(new Rect2(Vector2.Zero, size), fill, true);

            float offset = OffsetDash ? DashLength / 2 : 0;
            float width = Selected ? 2.5f : 1.5f;
            Color border = Selected ? BorderColor.Lightened(0.2f) : BorderColor;
            DrawDashedLine(new Vector2(0, 0), new Vector2(size.X, 0), border, width, DashLength, offset);
            DrawDashedLine(new Vector2(size.X, 0), new Vector2(size.X, size.Y), border, width, DashLength, offset);
            DrawDashedLine(new Vector2(size.X, size.Y), new Vector2(0, size.Y), border, width, DashLength, offset);
            DrawDashedLine(new Vector2(0, size.Y), new Vector2(0, 0), border, width, DashLength, offset);

            // Label
            if (!string.IsNullOrEmpty(LabelText))
            {
                var font = ThemeDB.FallbackFont;
                int fontSize = 11;
                Vector2 textSize = font.GetStringSize(LabelText, HorizontalAlignment.Left, -1, fontSize);
                Vector2 labelPos = new Vector2(4, 2 + textSize.Y);
                DrawRect(new Rect2(2, 2, textSize.X + 6, textSize.Y + 2), new Color(0, 0, 0, 0.55f), true);
                DrawString(font, labelPos, LabelText, HorizontalAlignment.Left, -1, fontSize, Colors.White);
            }

            // Resize handles when selected
            if (Selected)
            {
                float hs = HandleSizePx;
                Color handleFill = Colors.White;
                Color handleBorder = border;
                foreach (var center in GetHandleCenters(size, hs))
                {
                    var r = new Rect2(center - new Vector2(hs, hs) * 0.5f, new Vector2(hs, hs));
                    DrawRect(r, handleFill, true);
                    DrawRect(r, handleBorder, false, 1.5f);
                }
            }
        }

        public override void _Notification(int what)
        {
            // Ensure custom draw runs after first size assignment / enter tree
            if (what == NotificationResized || what == NotificationVisibilityChanged || what == NotificationDraw)
            {
                if (what != NotificationDraw)
                    QueueRedraw();
            }
        }

        public static Vector2[] GetHandleCenters(Vector2 size, float hs)
        {
            float hx = size.X;
            float hy = size.Y;
            return new[]
            {
                new Vector2(0, 0),       // NW
                new Vector2(hx * 0.5f, 0), // N
                new Vector2(hx, 0),      // NE
                new Vector2(hx, hy * 0.5f), // E
                new Vector2(hx, hy),     // SE
                new Vector2(hx * 0.5f, hy), // S
                new Vector2(0, hy),      // SW
                new Vector2(0, hy * 0.5f), // W
            };
        }

        private void DrawDashedLine(Vector2 from, Vector2 to, Color color, float width, float dashLength, float startOffset)
        {
            Vector2 dir = (to - from).Normalized();
            float length = (to - from).Length();
            float current = startOffset;
            while (current < length)
            {
                Vector2 start = from + dir * current;
                float endDist = Mathf.Min(current + dashLength, length);
                Vector2 end = from + dir * endDist;
                DrawLine(start, end, color, width);
                current += dashLength * 2;
            }
        }
    }

    public override void _Ready()
    {
        _globalData = GetNode<GlobalData>("/root/GlobalData");
        _globalSignals = GetNode<GlobalSignals>("/root/GlobalSignals");
        _historyManager = _globalData?.HistoryManager;
        _canvas = DisplaysManager.Canvas;
        _displaysManager = GetNode<DisplaysManager>("/root/DisplaysManager");

        // Store Callables: GlobalSignals is an autoload and outlives Settings. Connecting with
        // anonymous Callable.From(...) without Disconnect keeps the editor (and settings tree) alive → ObjectDB leak on exit.
        _displaysChangedCallable = Callable.From(OnDisplaysChanged);
        _canvasSizeChangedCallable = Callable.From<Vector2I>(OnCanvasSizeChanged);
        _layerGeometryChangedCallable = Callable.From<int>(OnLayerGeometryChanged);
        _globalSignals.Connect(nameof(GlobalSignals.DisplaysChanged), _displaysChangedCallable);
        _globalSignals.Connect(nameof(GlobalSignals.CanvasSizeChanged), _canvasSizeChangedCallable);
        _globalSignals.Connect(nameof(GlobalSignals.LayerGeometryChanged), _layerGeometryChangedCallable);

        if (_historyManager != null)
            _historyManager.HistoryRestored += OnHistoryRestored;

        GetWindow().SizeChanged += OnWindowSizeChanged;
        VisibilityChanged += OnEditorVisibilityChanged;
        TreeExiting += Cleanup;

        // Light setup only — heavy stage work waits until this panel is shown.
        // Settings embeds every panel at open; running SubViewport + SDL display scans here
        // stalls main-thread video presentation for every Settings open.
        BindNodes();
        ConnectSignals();

        RefreshCanvasSelectButtonText();

        // Never Always — that keeps rendering while the panel is hidden and competes with playback.
        _viewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled;
        _viewport.TransparentBg = true;
        _viewport.HandleInputLocally = false;
        // Stretch fills available stage space without a CustomMinimumSize ratchet that
        // prevents the center panel from shrinking when the window is reduced.
        // Do not set SubViewport.Size while stretch is on — Godot owns that size.
        _subViewportContainer.Stretch = true;
        _subViewportContainer.CustomMinimumSize = Vector2.Zero;

        _scrollContainer.MouseFilter = MouseFilterEnum.Ignore;
        _subViewportContainer.MouseFilter = MouseFilterEnum.Ignore;
        _scrollContainer.HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled;
        _scrollContainer.VerticalScrollMode = ScrollContainer.ScrollMode.Disabled;
        if (_canvasOutlinePanel != null)
            _canvasOutlinePanel.MouseFilter = MouseFilterEnum.Ignore;
        if (_control != null)
            _control.MouseFilter = MouseFilterEnum.Ignore;
        InstallStagePointerOverlay();
        _scrollContainer.Resized += OnStageResized;

        if (_bodyHSplit != null)
            _bodyHSplit.Resized += OnBodyHSplitResized;
        CallDeferred(nameof(ApplyResponsivePanelLayout));

        // Global _Input is only needed while a drag/pan is active (release outside the stage).
        SetProcessInput(false);

        if (IsVisibleInTree())
            CallDeferred(nameof(EnsureStageInitialized));

        GD.Print("SettingsCanvasEditor:_Ready - Light init (stage deferred until shown)");
    
        UiLocalizer.LocalizeTree(this);
        if (_globalSignals != null)
            _globalSignals.LocaleChanged += OnLocaleChanged;
}

    /// <summary>
    /// One-time heavy stage setup (background shader, trees, gizmos). Deferred until visible.
    /// </summary>
    private void EnsureStageInitialized()
    {
        if (_stageInitialized || !IsInsideTree())
            return;

        _stageInitialized = true;

        _displaysManager.EnsureDefaultScreen();
        SetupBackground();

        _canvasOutlinePanel.CustomMinimumSize = new Vector2(_canvas.CanvasSize.X, _canvas.CanvasSize.Y);

        RebuildTrees(selectCanvas: true);
        UpdateCanvasGizmos();
        UpdateViewportRenderMode();
        CallDeferred(nameof(RefreshStageView));

        GD.Print("SettingsCanvasEditor:EnsureStageInitialized - Stage ready");
    }

    /// <summary>
    /// Enables SubViewport rendering only while this panel is visible in the settings stack.
    /// </summary>
    private void UpdateViewportRenderMode()
    {
        if (_viewport == null || !IsInstanceValid(_viewport))
            return;

        _viewport.RenderTargetUpdateMode = IsVisibleInTree()
            ? SubViewport.UpdateMode.WhenVisible
            : SubViewport.UpdateMode.Disabled;
    }

    /// <summary>
    /// Recomputes zoom/fit and redraws the stage. Safe to call when size was previously zero.
    /// </summary>
    private void RefreshStageView()
    {
        if (!IsInsideTree() || !_stageInitialized || !IsVisibleInTree())
            return;

        // Stage collapsed or not laid out yet — wait for Resized; do not spin forever at zero width.
        if (_scrollContainer.Size.X < 8f || _scrollContainer.Size.Y < 8f)
            return;

        FitToView();
        UpdateCanvasGizmos();
        ForceStageRedraw();
    }

    private void OnEditorVisibilityChanged()
    {
        UpdateViewportRenderMode();

        if (IsVisibleInTree())
        {
            UpdateStageInputProcessing();

            // Heavy init only when user actually opens Canvas Editor (not every Settings open).
            CallDeferred(nameof(EnsureStageInitialized));
            CallDeferred(nameof(ApplyResponsivePanelLayout));
            CallDeferred(nameof(RefreshStageView));
            if (_needsHistoryRefresh && _stageInitialized)
            {
                _needsHistoryRefresh = false;
                CallDeferred(nameof(RefreshAfterHistoryRestore));
            }
        }
        else
        {
            SetProcessInput(false);
            CancelStageInteractionOnHide();
        }
    }

    /// <summary>
    /// Ends pan/drag and resets cursor when leaving the canvas editor so no further model
    /// updates or DisplaysManager logs fire while another settings panel is visible.
    /// </summary>
    private void CancelStageInteractionOnHide()
    {
        if (_isDraggingCanvas)
        {
            // Commit the drag cleanly (history already recorded at StartDrag) rather than
            // leaving half-applied geometry without a DisplaysManager update.
            EndCanvasInteraction();
        }

        _isPanning = false;
        _isDraggingCanvas = false;
        _dragMode = DragMode.None;
        if (!string.IsNullOrEmpty(_activeDragCoalesceKey))
        {
            _historyManager?.EndCoalesceSession(_activeDragCoalesceKey);
            _activeDragCoalesceKey = null;
        }

        MouseDefaultCursorShape = CursorShape.Arrow;
        if (_stagePointer != null && IsInstanceValid(_stagePointer))
            _stagePointer.MouseDefaultCursorShape = CursorShape.Arrow;
        UpdateStageInputProcessing();
    }

    private void OnWindowSizeChanged()
    {
        if (IsVisibleInTree() && _stageInitialized)
            UpdateZoom();
    }

    private void OnStageResized()
    {
        if (!IsVisibleInTree() || !_stageInitialized)
            return;

        // Collapsed stage (narrow window): skip zoom work until space returns.
        if (_scrollContainer.Size.X < 8f || _scrollContainer.Size.Y < 8f)
            return;

        UpdateZoom();
    }

    private void OnBodyHSplitResized()
    {
        ApplyResponsivePanelLayout();
    }

    /// <summary>
    /// Shrink order: center stage collapses to zero first. Side panels keep their preferred
    /// widths; when the window is narrower than left+right, <c>BodyScroll</c> provides a scrollbar
    /// instead of crushing the trees/properties.
    /// </summary>
    private void ApplyResponsivePanelLayout()
    {
        if (_isApplyingPanelLayout || !IsInsideTree())
            return;
        if (_bodyHSplit == null || _leftPanel == null || _rightPanel == null)
            return;

        float separation = _bodyHSplit.GetThemeConstant("separation");
        if (separation < 0f)
            separation = 4f;
        float innerSep = _centerRightSplit != null
            ? _centerRightSplit.GetThemeConstant("separation")
            : 4f;
        if (innerSep < 0f)
            innerSep = 4f;

        _isApplyingPanelLayout = true;
        try
        {
            _leftPanel.CustomMinimumSize = new Vector2(LeftPanelPreferredWidth, 0f);
            _rightPanel.CustomMinimumSize = new Vector2(RightPanelPreferredWidth, 0f);
            if (_centerPanel != null)
                _centerPanel.CustomMinimumSize = Vector2.Zero;

            float minBody = LeftPanelPreferredWidth + RightPanelPreferredWidth + separation + innerSep;
            _bodyHSplit.CustomMinimumSize = new Vector2(minBody, 0f);
        }
        finally
        {
            _isApplyingPanelLayout = false;
        }
    }

    /// <summary>
    /// Forces SubViewport + gizmo redraw so the stage is not blank after show/layout.
    /// Uses WhenVisible (not Always) so hidden settings do not steal GPU from playback.
    /// </summary>
    private void ForceStageRedraw()
    {
        UpdateViewportRenderMode();

        if (_subViewportContainer != null && IsInstanceValid(_subViewportContainer))
            _subViewportContainer.QueueRedraw();

        if (_canvasOutlinePanel != null && IsInstanceValid(_canvasOutlinePanel))
            _canvasOutlinePanel.QueueRedraw();

        if (_control != null && IsInstanceValid(_control))
            _control.QueueRedraw();

        foreach (var g in _gizmos)
        {
            if (IsInstanceValid(g))
                g.QueueRedraw();
        }
    }

    
    public override void _ExitTree()
    {
        // TreeExiting may have already run Cleanup; safe to call again.
        Cleanup();
        if (_globalSignals != null)
            _globalSignals.LocaleChanged -= OnLocaleChanged;
        base._ExitTree();
    }

private void BindNodes()
    {
        _screensTree = GetNode<Godot.Tree>("%ScreensTree");
        _refreshScreensButton = GetNode<Button>("%RefreshScreensButton");
        _layersTree = GetNode<Godot.Tree>("%LayersTree");
        _canvasSelectButton = GetNode<Button>("%CanvasSelectButton");
        _addScreenButton = GetNode<Button>("%AddScreenButton");
        _newTargetLayerButton = GetNode<Button>("%AddTargetLayerButton");
        _moveLayerUpButton = GetNode<Button>("%MoveLayerUpButton");
        _moveLayerDownButton = GetNode<Button>("%MoveLayerDownButton");

        _canvasSizeXLineEdit = GetNode<LineEdit>("%CanvasSizeX");
        _canvasSizeYLineEdit = GetNode<LineEdit>("%CanvasSizeY");
        _canvasTestPatternCheckBox = GetNode<CheckBox>("%CanvasTestPatternCheckBox");
        _canvasTestPatternResetButton = GetNode<Button>("%CanvasTestPatternResetButton");

        _canvasOutlinePanel = GetNode<Panel>("%CanvasOutlinePanel");
        _subViewportContainer = GetNode<SubViewportContainer>("%SubViewportContainer");
        _viewport = GetNode<SubViewport>("%Viewport");
        _control = GetNode<Control>("%CanvasControl");
        _scrollContainer = GetNode<ScrollContainer>("%ScrollContainer");
        _canvasLayer = GetNode<CanvasLayer>("%CanvasLayer");
        _zoomInButton = GetNode<Button>("%ZoomInButton");
        _zoomOutButton = GetNode<Button>("%ZoomOutButton");
        _fitButton = GetNode<Button>("%FitButton");
        _zoomPercentLineEdit = GetNode<LineEdit>("%ZoomPercentLabel");

        _bodyHSplit = GetNodeOrNull<HSplitContainer>("%BodyHSplit");
        _centerRightSplit = GetNodeOrNull<HSplitContainer>("%CenterRightSplit");
        _leftPanel = GetNodeOrNull<Control>("%LeftPanel");
        _centerPanel = GetNodeOrNull<Control>("%CenterPanel");
        _rightPanel = GetNodeOrNull<Control>("%RightPanel");

        _emptyPropsLabel = GetNode<Label>("%EmptyPropsLabel");
        _canvasProps = GetNode<Control>("%CanvasProps");
        _outputProps = GetNode<Control>("%OutputProps");
        _outputPropsTitle = GetNode<Label>("%OutputPropsTitle");
        _outputResolutionLabel = GetNode<Label>("%OutputResolutionLabel");
        _screenNameLineEdit = GetNode<LineEdit>("%ScreenNameLineEdit");
        _screenOutputOption = GetNode<OptionButton>("%ScreenOutputOption");
        _outputSizeXLineEdit = GetNode<LineEdit>("%SizeXLineEdit");
        _outputSizeYLineEdit = GetNode<LineEdit>("%SizeYLineEdit");
        _outputPosXLineEdit = GetNode<LineEdit>("%PosXLineEdit");
        _outputPosYLineEdit = GetNode<LineEdit>("%PosYLineEdit");
        _displayOffsetXLineEdit = GetNode<LineEdit>("%DisplayOffsetXLineEdit");
        _displayOffsetYLineEdit = GetNode<LineEdit>("%DisplayOffsetYLineEdit");
        _screenKeepAspectCheckBox = GetNode<CheckBox>("%ScreenKeepAspectCheckBox");
        _outputTransparentCheckBox = GetNode<CheckBox>("%OutputTransparentCheckBox");
        _outputTestPatternCheckBox = GetNode<CheckBox>("%OutputTestPatternCheckBox");
        _deleteScreenButton = GetNode<Button>("%DeleteScreenButton");
        _screenOutputResetButton = GetNode<Button>("%ScreenOutputResetButton");
        _screenSizeResetButton = GetNode<Button>("%ScreenSizeResetButton");
        _screenKeepAspectResetButton = GetNode<Button>("%ScreenKeepAspectResetButton");
        _screenPosResetButton = GetNode<Button>("%ScreenPosResetButton");
        _screenDisplayOffsetResetButton = GetNode<Button>("%ScreenDisplayOffsetResetButton");
        _screenTransparentResetButton = GetNode<Button>("%ScreenTransparentResetButton");
        _screenTestPatternResetButton = GetNode<Button>("%ScreenTestPatternResetButton");

        _layerProps = GetNode<Control>("%LayerProps");
        _layerNameLineEdit = GetNode<LineEdit>("%LayerNameLineEdit");
        _layerSizeXLineEdit = GetNode<LineEdit>("%LayerSizeXLineEdit");
        _layerSizeYLineEdit = GetNode<LineEdit>("%LayerSizeYLineEdit");
        _layerPosXLineEdit = GetNode<LineEdit>("%LayerPosXLineEdit");
        _layerPosYLineEdit = GetNode<LineEdit>("%LayerPosYLineEdit");
        _layerKeepAspectCheckBox = GetNode<CheckBox>("%LayerKeepAspectCheckBox");
        _layerTransparentCheckBox = GetNode<CheckBox>("%LayerTransparentCheckBox");
        _layerTestPatternCheckBox = GetNode<CheckBox>("%LayerTestPatternCheckBox");
        _layerLockCheckBox = GetNode<CheckBox>("%LayerLockCheckBox");
        _deleteLayerButton = GetNode<Button>("%DeleteLayerButton");
        _layerSizeResetButton = GetNode<Button>("%LayerSizeResetButton");
        _layerKeepAspectResetButton = GetNode<Button>("%LayerKeepAspectResetButton");
        _layerPosResetButton = GetNode<Button>("%LayerPosResetButton");
        _layerTransparentResetButton = GetNode<Button>("%LayerTransparentResetButton");
        _layerTestPatternResetButton = GetNode<Button>("%LayerTestPatternResetButton");
        _layerLockResetButton = GetNode<Button>("%LayerLockResetButton");

        SetupResetButtonIcons();
    }

    private void SetupResetButtonIcons()
    {
        var icon = GetThemeIcon("Refresh", "AtlasIcons");
        foreach (var btn in new[]
                 {
                     _canvasTestPatternResetButton,
                     _screenOutputResetButton, _screenSizeResetButton, _screenKeepAspectResetButton,
                     _screenPosResetButton, _screenDisplayOffsetResetButton, _screenTransparentResetButton,
                     _screenTestPatternResetButton, _layerSizeResetButton, _layerKeepAspectResetButton,
                     _layerPosResetButton, _layerTransparentResetButton, _layerTestPatternResetButton,
                     _layerLockResetButton
                 })
        {
            if (btn != null)
                btn.Icon = icon;
        }

        if (_refreshScreensButton != null)
        {
            _refreshScreensButton.Icon = icon;
            _refreshScreensButton.ExpandIcon = true;
            _refreshScreensButton.AddThemeConstantOverride("icon_max_width", 14);
        }
    }

    private void SetupBackground()
    {
        var backgroundRect = new ColorRect();
        backgroundRect.ZIndex = -1;

        var shader = new Shader();
        shader.Code = @"
            shader_type canvas_item;
            uniform vec2 rect_size;
            void fragment() {
                vec2 uv = UV;
                float aspect = max(rect_size.x, rect_size.y) / min(rect_size.x, rect_size.y);
                if (rect_size.x > rect_size.y) { uv.x *= aspect; } else { uv.y *= aspect; }
                vec2 scaled_uv = uv * 20.0;
                float diagonal1 = mod(scaled_uv.x + scaled_uv.y, 2.0);
                float diagonal2 = mod(scaled_uv.x - scaled_uv.y, 2.0);
                if (diagonal1 < 0.07 || diagonal2 < 0.07) {
                    COLOR = vec4(0.2, 0.2, 0.2, 1.0);
                } else {
                    COLOR = vec4(0.0, 0.0, 0.0, 0.0);
                }
            }
            ";
        var material = new ShaderMaterial();
        material.Shader = shader;
        material.SetShaderParameter("rect_size", _scrollContainer.Size);
        backgroundRect.Material = material;

        _backgroundRect = backgroundRect;
        _backgroundRect.MouseFilter = MouseFilterEnum.Ignore;
        var backgroundLayer = new CanvasLayer();
        backgroundLayer.Layer = -1;
        _viewport.AddChild(backgroundLayer);
        backgroundLayer.AddChild(_backgroundRect);
    }

    /// <summary>
    /// Wraps the SubViewport in a host and stacks a pointer overlay on top so stage
    /// clicks are delivered as local <c>GuiInput</c> regardless of SubViewport swallowing.
    /// </summary>
    private void InstallStagePointerOverlay()
    {
        if (_stagePointer != null || _scrollContainer == null || _subViewportContainer == null)
            return;
        if (_subViewportContainer.GetParent() != _scrollContainer)
            return;

        var host = new Control
        {
            Name = "StageHost",
            MouseFilter = MouseFilterEnum.Ignore,
            CustomMinimumSize = Vector2.Zero
        };
        host.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        host.SizeFlagsVertical = SizeFlags.ExpandFill;

        _scrollContainer.RemoveChild(_subViewportContainer);
        host.AddChild(_subViewportContainer);
        _subViewportContainer.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        _subViewportContainer.MouseFilter = MouseFilterEnum.Ignore;

        _stagePointer = new Control
        {
            Name = "StagePointer",
            MouseFilter = MouseFilterEnum.Stop
        };
        _stagePointer.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        host.AddChild(_stagePointer);

        _scrollContainer.AddChild(host);

        _stagePointer.GuiInput += OnStageGuiInput;
        _stagePointer.MouseExited += OnStageMouseExited;
    }

    private void ConnectSignals()
    {
        _screensTree.ItemSelected += OnScreensTreeItemSelected;
        _layersTree.ItemSelected += OnLayersTreeItemSelected;
        _canvasSelectButton.Pressed += OnCanvasSelectPressed;
        _addScreenButton.Pressed += OnNewScreenPressed;
        if (_refreshScreensButton != null)
            _refreshScreensButton.Pressed += OnRefreshScreensPressed;
        _newTargetLayerButton.Pressed += OnNewTargetLayerPressed;
        _moveLayerUpButton.Pressed += OnMoveLayerUpPressed;
        _moveLayerDownButton.Pressed += OnMoveLayerDownPressed;

        UiUtilities.BindLineEditCommit(_canvasSizeXLineEdit, OnCanvasSizeSubmitted);
        UiUtilities.BindLineEditCommit(_canvasSizeYLineEdit, OnCanvasSizeSubmitted);
        _canvasTestPatternCheckBox.Toggled += OnCanvasTestPatternToggled;
        _canvasTestPatternResetButton.Pressed += OnCanvasTestPatternResetPressed;

        _zoomInButton.Pressed += ZoomIn;
        _zoomOutButton.Pressed += ZoomOut;
        _fitButton.Pressed += FitToView;
        UiUtilities.BindLineEditCommit(_zoomPercentLineEdit, OnZoomPercentSubmitted);

        UiUtilities.BindLineEditCommit(_screenNameLineEdit, OnScreenNameSubmitted);
        _screenOutputOption.ItemSelected += OnScreenOutputSelected;
        UiUtilities.BindLineEditCommit(_outputSizeXLineEdit, OnScreenSizeXSubmitted);
        UiUtilities.BindLineEditCommit(_outputSizeYLineEdit, OnScreenSizeYSubmitted);
        UiUtilities.BindLineEditCommit(_outputPosXLineEdit, OnScreenPosXSubmitted);
        UiUtilities.BindLineEditCommit(_outputPosYLineEdit, OnScreenPosYSubmitted);
        UiUtilities.BindLineEditCommit(_displayOffsetXLineEdit, OnDisplayOffsetXSubmitted);
        UiUtilities.BindLineEditCommit(_displayOffsetYLineEdit, OnDisplayOffsetYSubmitted);
        _screenKeepAspectCheckBox.Toggled += OnScreenKeepAspectToggled;
        _outputTransparentCheckBox.Toggled += OnScreenTransparentToggled;
        _outputTestPatternCheckBox.Toggled += OnScreenTestPatternToggled;
        _deleteScreenButton.Pressed += OnDeleteScreenPressed;
        _screenOutputResetButton.Pressed += OnScreenOutputResetPressed;
        _screenSizeResetButton.Pressed += OnScreenSizeResetPressed;
        _screenKeepAspectResetButton.Pressed += OnScreenKeepAspectResetPressed;
        _screenPosResetButton.Pressed += OnScreenPosResetPressed;
        _screenDisplayOffsetResetButton.Pressed += OnScreenDisplayOffsetResetPressed;
        _screenTransparentResetButton.Pressed += OnScreenTransparentResetPressed;
        _screenTestPatternResetButton.Pressed += OnScreenTestPatternResetPressed;

        UiUtilities.BindLineEditCommit(_layerNameLineEdit, OnLayerNameSubmitted);
        UiUtilities.BindLineEditCommit(_layerSizeXLineEdit, OnLayerSizeXSubmitted);
        UiUtilities.BindLineEditCommit(_layerSizeYLineEdit, OnLayerSizeYSubmitted);
        UiUtilities.BindLineEditCommit(_layerPosXLineEdit, OnLayerPosXSubmitted);
        UiUtilities.BindLineEditCommit(_layerPosYLineEdit, OnLayerPosYSubmitted);
        _layerKeepAspectCheckBox.Toggled += OnLayerKeepAspectToggled;
        _layerTransparentCheckBox.Toggled += OnLayerTransparentToggled;
        _layerTestPatternCheckBox.Toggled += OnLayerTestPatternToggled;
        _layerLockCheckBox.Toggled += OnLayerLockToggled;
        _deleteLayerButton.Pressed += OnDeleteLayerPressed;
        _layerSizeResetButton.Pressed += OnLayerSizeResetPressed;
        _layerKeepAspectResetButton.Pressed += OnLayerKeepAspectResetPressed;
        _layerPosResetButton.Pressed += OnLayerPosResetPressed;
        _layerTransparentResetButton.Pressed += OnLayerTransparentResetPressed;
        _layerTestPatternResetButton.Pressed += OnLayerTestPatternResetPressed;
        _layerLockResetButton.Pressed += OnLayerLockResetPressed;
    }


    /// <summary>
    /// Re-localizes panel chrome when the UI language changes.
    /// </summary>
    /// <param name="localeCode">New locale code.</param>
    private void OnLocaleChanged(string localeCode)
    {
        if (!GodotObject.IsInstanceValid(this))
            return;
        UiLocalizer.LocalizeTree(this);
        RefreshCanvasSelectButtonText();
    }

    /// <summary>
    /// Updates the canvas select button with localized size label.
    /// </summary>
    private void RefreshCanvasSelectButtonText()
    {
        if (_canvasSelectButton == null || !IsInstanceValid(_canvasSelectButton) || _canvas == null)
            return;
        var size = _canvas.CanvasSize;
        _canvasSelectButton.Text = UiLocalizer.Tf("Canvas ({0}×{1})", size.X, size.Y);
    }

}
