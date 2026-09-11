// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using Godot;
using System;
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
using Cue2.UI.Utilities;
using Godot.Collections;

namespace Cue2.Domain.Devices;

public partial class VideoOutputDevice : Window, IDisposable
{
    private const int DEFAULT_WIDTH = 1920;
    private const int DEFAULT_HEIGHT = 1080;

    /// <summary>
    /// Unique ID for the output device.
    /// </summary>
    public int OutputId { get; set; }

    /// <summary>
    /// Name of the output device.
    /// </summary>
    public string OutputName { get; set; } = "Unnamed Output";

    /// <summary>
    /// Position on the canvas (top-left corner).
    /// </summary>
    public Vector2I CanvasPosition { get; set; } = Vector2I.Zero;

    /// <summary>
    /// Size of the output region on the canvas.
    /// </summary>
    public Vector2I OutputSize { get; set; } = new Vector2I(DEFAULT_WIDTH, DEFAULT_HEIGHT);

    /// <summary>
    /// Sentinel value for a virtual (non-physical) screen output.
    /// </summary>
    public const int VirtualMonitorIndex = -1;

    /// <summary>
    /// Sentinel value for a portable OS-decorated window output (title bar + window controls).
    /// </summary>
    public const int WindowMonitorIndex = -2;

    /// <summary>
    /// Target display monitor index.
    /// Use <see cref="VirtualMonitorIndex"/> for Virtual Output,
    /// <see cref="WindowMonitorIndex"/> for a portable Window,
    /// or a physical monitor index (≥ 0).
    /// </summary>
    public int TargetMonitor { get; set; } = VirtualMonitorIndex;

    /// <summary>
    /// Whether this screen is assigned to Virtual Output (no visible window).
    /// </summary>
    public bool IsVirtual => TargetMonitor == VirtualMonitorIndex;

    /// <summary>
    /// Whether this screen is assigned to a portable OS-decorated window.
    /// </summary>
    public bool IsWindow => TargetMonitor == WindowMonitorIndex;

    /// <summary>
    /// Whether this screen is assigned to a physical monitor (borderless output).
    /// </summary>
    public bool IsPhysical => TargetMonitor >= 0;

    /// <summary>
    /// Whether the output window is transparent.
    /// </summary>
    public bool OutputTransparent { get; set; } = false;

    /// <summary>
    /// Pixel offset of the output window.
    /// Physical: relative to the target display's origin (home position), applied after canvas clipping.
    /// Window: absolute desktop position of the portable window.
    /// </summary>
    public Vector2I DisplayOffset { get; set; } = Vector2I.Zero;

    /// <summary>
    /// When true, changing one size dimension updates the other to preserve aspect ratio.
    /// </summary>
    public bool KeepAspect { get; set; } = false;
    
    private static int _nextOutputId = 0;

    public static void SetNextOutputId(int id)
    {
        _nextOutputId = id;
    }

    private Control _sceneRoot;

    /// <summary>Solid colour behind all layers (show-scoped output background).</summary>
    private ColorRect _backgroundRect;

    /// <summary>Full-window blackout overlay above layers (runtime operator control).</summary>
    private ColorRect _blackoutOverlay;

    private TestPattern _testPattern;
    private TestPattern _canvasTestPattern;
    private Dictionary<int, TestPattern> _layerTestPatterns = new();
    
    private PackedScene _displayLayerPackedScene = SceneLoader.LoadPackedScene("uid://dwnssjgckgb8p", out _);
    private Dictionary<Control, int> _activeLayers = new();
    

    /// <summary>
    /// Cached last clipped rectangle to avoid unnecessary updates.
    /// </summary>
    private Rect2 _lastClippedRect = new Rect2(-1, -1, 0, 0);

    /// <summary>
    /// Cached display offset used with <see cref="_lastClippedRect"/> so offset-only changes still refresh.
    /// </summary>
    private Vector2I _lastDisplayOffset = new Vector2I(int.MinValue, int.MinValue);

    /// <summary>
    /// Last applied global window position.
    /// </summary>
    private Vector2I _lastWindowPos = new Vector2I(int.MinValue, int.MinValue);

    /// <summary>
    /// Last applied window size.
    /// </summary>
    private Vector2I _lastWindowSize = new Vector2I(int.MinValue, int.MinValue);

    /// <summary>
    /// When true, the user closed the portable window via the OS close button; stay hidden until re-enabled.
    /// </summary>
    private bool _userDismissedWindow;

    /// <summary>
    /// Prevents feedback loops while programmatically placing the portable window.
    /// </summary>
    private bool _isPlacingWindow;

    /// <summary>
    /// Last OS client size of a portable window (may differ from canvas size when user stretches).
    /// </summary>
    private Vector2I _lastPortableClientSize = new Vector2I(int.MinValue, int.MinValue);

    /// <summary>
    /// Last canvas-region size applied as <see cref="Window.ContentScaleSize"/> for portable windows.
    /// </summary>
    private Vector2I _lastPortableContentScaleSize = new Vector2I(int.MinValue, int.MinValue);

    /// <summary>
    /// Last requested vsync mode (re-applied after native window placement).
    /// </summary>
    private OutputVSyncMode _pendingVSyncMode = OutputVSyncMode.PreferVSync;

    /// <summary>Low-rate poll for portable-window move/resize (avoids per-frame <c>_Process</c>).</summary>
    private Timer _geometryPollTimer;

    public VideoOutputDevice()
    {
        OutputId = _nextOutputId++;
        // Stay hidden until assigned to a real destination. Default Visible=true would
        // create a native window on enter-tree (and on Linux embed, GetWindowId can
        // still report the main viewport until that window exists).
        Visible = false;
        Transient = false;
        Exclusive = false;
        Mode = ModeEnum.Windowed;
        Borderless = true;
        LinuxWindowEmbedPolicy.ApplyToAppWindow(this);
        DisplayServer.ScreenSetKeepOn(true);

        // OS close button on portable windows hides rather than frees the output device.
        CloseRequested += OnCloseRequested;

        SetProcess(false);
        _geometryPollTimer = new Timer { OneShot = false, WaitTime = 0.25 };
        _geometryPollTimer.Timeout += PollPortableGeometry;
        AddChild(_geometryPollTimer);
        VisibilityChanged += OnVisibilityChangedForGeometryPoll;

        InitSceneRoot();
    }

    private void OnCloseRequested()
    {
        if (!IsWindow)
            return;

        _userDismissedWindow = true;
        try
        {
            if (Visible)
                Hide();
        }
        catch
        {
            /* ignore */
        }

        InvalidateGeometryCache();
        GD.Print($"VideoOutputDevice:OnCloseRequested - Portable window '{OutputName}' dismissed by user.");
    }

    /// <summary>
    /// True when the user closed the portable window via the OS close button.
    /// Reselect Window in the canvas editor (or call <see cref="ClearWindowDismissed"/>) to show it again.
    /// </summary>
    public bool IsWindowDismissed => _userDismissedWindow;

    /// <summary>
    /// Clears a user-dismissed portable window so the next placement shows it again.
    /// Call when the screen is reassigned to Window from the canvas editor.
    /// </summary>
    public void ClearWindowDismissed()
    {
        _userDismissedWindow = false;
    }

    /// <summary>
    /// Shows a previously dismissed portable window again (no-op if not in Window mode).
    /// </summary>
    public void ShowPortableWindow()
    {
        if (!IsWindow)
            return;
        _userDismissedWindow = false;
        UpdateOutputRegion();
    }

    /// <summary>
    /// Forces a full geometry refresh (used by canvas editor "refresh screens").
    /// Restores a user-closed portable window and re-applies physical placement.
    /// </summary>
    public void ForceRefreshOutput()
    {
        if (IsWindow)
            _userDismissedWindow = false;
        InvalidateGeometryCache();
        UpdateOutputRegion();
    }

    /// <summary>
    /// Hides this output window without changing assignment (used by master disable output).
    /// </summary>
    public void ForceHideForDisable()
    {
        HideScreenWindow();
    }

    private void InitSceneRoot()
    {
        // Full-rect root so layer content always fills the Window content viewport.
        // SizeFlags alone do not layout against Window (not a container) — anchors are required.
        _sceneRoot = new Control
        {
            Name = "OutputRoot",
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        AddChild(_sceneRoot);
        _sceneRoot.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);

        // Background sits under layers on the content root.
        _backgroundRect = new ColorRect
        {
            Name = "OutputBackground",
            Color = Colors.Black,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        _sceneRoot.AddChild(_backgroundRect);
        _backgroundRect.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);

        // Blackout is a direct Window child so it also covers screen/layer test patterns.
        _blackoutOverlay = new ColorRect
        {
            Name = "OutputBlackout",
            Color = Colors.Black,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            Visible = false,
            ZIndex = 4096,
        };
        AddChild(_blackoutOverlay);
        _blackoutOverlay.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
    }

    /// <summary>
    /// Sets the solid colour drawn behind all video/text layers on this output.
    /// </summary>
    /// <param name="color">Background colour (alpha is honoured).</param>
    public void SetOutputBackgroundColor(Color color)
    {
        if (_backgroundRect == null || !IsInstanceValid(_backgroundRect))
            return;
        _backgroundRect.Color = color;
    }

    /// <summary>
    /// Shows or hides the full-window blackout overlay (layers stay loaded underneath).
    /// </summary>
    /// <param name="enabled">True to black out this output.</param>
    public void SetBlackout(bool enabled)
    {
        if (_blackoutOverlay == null || !IsInstanceValid(_blackoutOverlay))
            return;
        _blackoutOverlay.Visible = enabled;
        if (enabled)
        {
            // Keep blackout above scene root and any test-pattern children.
            MoveChild(_blackoutOverlay, GetChildCount() - 1);
        }
    }

    /// <summary>
    /// Applies vsync / frame-pacing mode to this output window via DisplayServer.
    /// </summary>
    /// <param name="mode">Machine preference for output present behaviour.</param>
    /// <remarks>
    /// Godot exposes vsync per native window id (not as a Window node property in 4.6 Mono).
    /// Effectiveness depends on the rendering backend; mailbox may fall back to enabled.
    /// </remarks>
    public void ApplyVSyncMode(OutputVSyncMode mode)
    {
        _pendingVSyncMode = mode;
        ApplyPendingVSyncMode();
    }

    /// <summary>
    /// Applies <see cref="_pendingVSyncMode"/> once a native window id exists.
    /// </summary>
    private void ApplyPendingVSyncMode()
    {
        if (!TryGetNativeWindowId(out int windowId))
            return;

        var dsMode = _pendingVSyncMode switch
        {
            OutputVSyncMode.Off => DisplayServer.VSyncMode.Disabled,
            OutputVSyncMode.LowLatency => DisplayServer.VSyncMode.Mailbox,
            _ => DisplayServer.VSyncMode.Enabled
        };
        try
        {
            DisplayServer.WindowSetVsyncMode(dsMode, windowId);
        }
        catch (Exception ex)
        {
            GD.Print($"VideoOutputDevice:ApplyVSyncMode - DisplayServer vsync failed: {ex.Message}");
        }
    }

    /// <summary>
    /// True when global output disable is active (house displays must stay closed).
    /// </summary>
    private static bool IsGlobalOutputDisabled()
    {
        try
        {
            return DisplaysManager.OutputDisabled;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Sets the reference to the parent canvas.
    /// </summary>
    /// <param name="canvas">The canvas this output belongs to.</param>
    public void SetCanvasReference(Canvas canvas)
    {
        UpdateOutputRegion();
    }


    public Control AddLayer(int LayerId)
    {
        var layer = DisplaysManager.GetLayerById(LayerId);
        var displayLayer = _displayLayerPackedScene.Instantiate<Control>();
        // Host = layer rectangle on this screen (not full output). Clip so Fill mode crops.
        displayLayer.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.TopLeft);
        displayLayer.ClipContents = true;
        _sceneRoot.AddChild(displayLayer);
        var outputLayer = displayLayer.GetNode<TextureRect>("%LayerOutput");
        ApplyLayerRectToHost(displayLayer, outputLayer, layer);
        _activeLayers[displayLayer] = LayerId;
        ApplyLayerDrawOrder();

        return displayLayer;
    }

    /// <summary>
    /// Applies target-layer stack order so first/highest ZIndex draws on top.
    /// </summary>
    public void ApplyLayerDrawOrder()
    {
        // Assign Control.ZIndex from layer data (higher = on top).
        foreach (var kvp in _activeLayers.ToList())
        {
            var host = kvp.Key;
            if (host == null || !IsInstanceValid(host))
            {
                _activeLayers.Remove(host);
                continue;
            }

            var layer = DisplaysManager.GetLayerById(kvp.Value);
            host.ZIndex = layer?.ZIndex ?? 0;
        }

        // Also reorder children so draw order is deterministic even without z-index.
        // Bottom of stack first, top last (later siblings paint above).
        // Background stays index 0 under layer hosts.
        if (_sceneRoot == null || !IsInstanceValid(_sceneRoot))
            return;

        if (_backgroundRect != null && IsInstanceValid(_backgroundRect))
            _sceneRoot.MoveChild(_backgroundRect, 0);

        var ordered = _activeLayers
            .Where(kv => kv.Key != null && IsInstanceValid(kv.Key))
            .OrderBy(kv =>
            {
                var layer = DisplaysManager.GetLayerById(kv.Value);
                return layer?.ZIndex ?? 0;
            })
            .Select(kv => kv.Key)
            .ToList();

        for (int i = 0; i < ordered.Count; i++)
            _sceneRoot.MoveChild(ordered[i], i + 1);
    }

    /// <summary>
    /// Updates the on-screen TextureRect position/size for a target layer (e.g. after canvas editor edits).
    /// Live video playback uses these same rects, so they must move with the layer data.
    /// </summary>
    /// <param name="layerId">Target layer id to refresh on this output.</param>
    public void UpdateLayerDisplayRect(int layerId)
    {
        var layer = DisplaysManager.GetLayerById(layerId);
        if (layer == null)
            return;

        foreach (var kvp in _activeLayers.ToList())
        {
            var host = kvp.Key;
            if (kvp.Value != layerId)
                continue;

            if (host == null || !IsInstanceValid(host))
            {
                _activeLayers.Remove(host);
                continue;
            }

            var outputLayer = host.GetNodeOrNull<TextureRect>("%LayerOutput");
            if (outputLayer == null)
                continue;

            ApplyLayerRectToHost(host, outputLayer, layer);
        }
    }

    /// <summary>
    /// Refreshes display rects for every active layer on this output (after screen canvas position changes).
    /// </summary>
    public void UpdateAllLayerDisplayRects()
    {
        foreach (var layerId in _activeLayers.Values.Distinct().ToList())
            UpdateLayerDisplayRect(layerId);
    }

    /// <summary>
    /// Positions the layer host on this screen and makes LayerOutput fill it so Fit/Fill/Stretch work.
    /// StretchMode is left alone if already set by <see cref="VideoComponent.ApplyTextureLayout"/>.
    /// </summary>
    private void ApplyLayerRectToHost(Control host, TextureRect outputLayer, VideoTargetLayer layer)
    {
        if (host == null || outputLayer == null || layer == null)
            return;

        // Layer rect in output-window space: canvas coords minus this screen's canvas origin.
        host.Position = (Vector2)(layer.CanvasPosition - CanvasPosition);
        host.Size = (Vector2)layer.Size;
        host.ClipContents = true;

        // Texture fills the host; VideoDisplayMode stretch mode maps the frame inside.
        outputLayer.Position = Vector2.Zero;
        outputLayer.Size = host.Size;
        outputLayer.ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize;
    }

    

    /// <summary>
    /// Returns true when this Window has a native DisplayServer window id that can be resized/moved.
    /// Virtual screens never create a usable native window until assigned to a physical output.
    /// </summary>
    /// <remarks>
    /// Embedded windows report the embedder id via <see cref="Window.GetWindowId"/>. On Linux
    /// that is <see cref="LinuxWindowEmbedPolicy.MainWindowId"/> (0). DisplayServer calls with that id
    /// resize, borderless, or move the operator UI — never treat 0 as "this output".
    /// </remarks>
    private bool TryGetNativeWindowId(out int windowId)
    {
        windowId = -1;
        if (!IsInsideTree() || !GodotObject.IsInstanceValid(this))
            return false;
        if (IsEmbedded())
            return false;

        try
        {
            windowId = GetWindowId();
            // DisplayServer.InvalidWindowId is -1. MainWindowId is 0 — a valid id for
            // the operator UI, not for a child output.
            if (windowId == DisplayServer.InvalidWindowId || windowId < 0)
                return false;
            if (LinuxWindowEmbedPolicy.IsMainWindowId(windowId))
                return false;
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Ensures this output is a native OS window (not an embedded sub-view of /root)
    /// before DisplayServer placement. Hides first when ForceNative must be applied
    /// to an already-visible window.
    /// </summary>
    /// <returns>True when a distinct native window id exists.</returns>
    private bool EnsureNativeOutputWindow()
    {
        Transient = false;
        Exclusive = false;

        if (Visible && (IsEmbedded() || !ForceNative))
        {
            try { Hide(); } catch { /* ignore */ }
        }

        LinuxWindowEmbedPolicy.ApplyToAppWindow(this);

        if (!Visible)
            Show();

        return TryGetNativeWindowId(out _) && !IsEmbedded();
    }

    /// <summary>Operator-window geometry captured before house-screen placement.</summary>
    private readonly struct OperatorWindowSnapshot
    {
        public DisplayServer.WindowMode Mode { get; init; }
        public Vector2I Position { get; init; }
        public Vector2I Size { get; init; }
        public bool Borderless { get; init; }
        public bool Valid { get; init; }
    }

    /// <summary>
    /// Snapshots the main Cue2 window so house-screen placement can restore it if
    /// DisplayServer calls accidentally targeted <see cref="LinuxWindowEmbedPolicy.MainWindowId"/>.
    /// </summary>
    /// <returns>A snapshot, or default when the main window cannot be queried.</returns>
    private static OperatorWindowSnapshot CaptureOperatorWindow()
    {
        try
        {
            int id = LinuxWindowEmbedPolicy.MainWindowId;
            return new OperatorWindowSnapshot
            {
                Mode = DisplayServer.WindowGetMode(id),
                Position = DisplayServer.WindowGetPosition(id),
                Size = DisplayServer.WindowGetSize(id),
                Borderless = DisplayServer.WindowGetFlag(DisplayServer.WindowFlags.Borderless, id),
                Valid = true
            };
        }
        catch
        {
            return default;
        }
    }

    /// <summary>
    /// Restores the main window when output placement changed its mode, chrome, or rect.
    /// </summary>
    /// <param name="before">Snapshot from <see cref="CaptureOperatorWindow"/>.</param>
    private static void RestoreOperatorWindowIfClobbered(OperatorWindowSnapshot before)
    {
        if (!before.Valid)
            return;

        try
        {
            int id = LinuxWindowEmbedPolicy.MainWindowId;
            var mode = DisplayServer.WindowGetMode(id);
            bool borderless = DisplayServer.WindowGetFlag(DisplayServer.WindowFlags.Borderless, id);
            var pos = DisplayServer.WindowGetPosition(id);
            var size = DisplayServer.WindowGetSize(id);
            if (mode == before.Mode && borderless == before.Borderless
                && pos == before.Position && size == before.Size)
                return;

            GD.Print($"VideoOutputDevice:RestoreOperatorWindowIfClobbered - Main window mutated during output placement "
                     + $"(mode {mode}->{before.Mode}, borderless {borderless}->{before.Borderless}, "
                     + $"pos {pos}->{before.Position}, size {size}->{before.Size}). Restoring.");

            DisplayServer.WindowSetMode(before.Mode, id);
            DisplayServer.WindowSetFlag(DisplayServer.WindowFlags.Borderless, before.Borderless, id);
            DisplayServer.WindowSetPosition(before.Position, id);
            DisplayServer.WindowSetSize(before.Size, id);
        }
        catch (Exception ex)
        {
            GD.Print($"VideoOutputDevice:RestoreOperatorWindowIfClobbered - Restore failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Hides a virtual / invalid screen window.
    /// </summary>
    private void HideScreenWindow()
    {
        try
        {
            if (Visible)
                Hide();
        }
        catch
        {
            /* ignore */
        }

        DisableContentScale();
        InvalidateGeometryCache();
    }

    private void InvalidateGeometryCache()
    {
        _lastClippedRect = new Rect2(-1, -1, 0, 0);
        _lastDisplayOffset = new Vector2I(int.MinValue, int.MinValue);
        _lastWindowPos = new Vector2I(int.MinValue, int.MinValue);
        _lastWindowSize = new Vector2I(int.MinValue, int.MinValue);
        _lastPortableClientSize = new Vector2I(int.MinValue, int.MinValue);
        _lastPortableContentScaleSize = new Vector2I(int.MinValue, int.MinValue);
    }

    /// <summary>
    /// Disables Godot content scaling (used for 1:1 physical outputs and virtual hide).
    /// </summary>
    private void DisableContentScale()
    {
        ContentScaleMode = ContentScaleModeEnum.Disabled;
        ContentScaleFactor = 1f;
        ContentScaleSize = Vector2I.Zero;
    }

    /// <summary>
    /// Enables content scale so the canvas-region design size stretches to the OS window client area.
    /// Canvas editor size is unchanged; only the on-screen presentation scales.
    /// </summary>
    private void ApplyPortableContentScale(Vector2I designSize)
    {
        if (designSize.X <= 0 || designSize.Y <= 0)
            return;

        ContentScaleSize = designSize;
        ContentScaleMode = ContentScaleModeEnum.CanvasItems;
        // Stretch to fill the window (user may freely resize OS chrome; canvas data stays fixed).
        ContentScaleAspect = ContentScaleAspectEnum.Ignore;
        ContentScaleFactor = 1f;
    }

    /// <summary>
    /// True when the intended window rect fully covers the target monitor (no offset / partial size).
    /// Exact match is when Godot/Windows most often promotes borderless windows to ExclusiveFullscreen,
    /// which causes black frame flashes — especially on multi-monitor setups with continuous video present.
    /// </summary>
    private bool IsFullMonitorCoverage(Vector2I windowPos, Vector2I windowSize)
    {
        if (TargetMonitor < 0 || TargetMonitor >= DisplayServer.GetScreenCount())
            return false;

        var monPos = DisplayServer.ScreenGetPosition(TargetMonitor);
        var monSize = DisplayServer.ScreenGetSize(TargetMonitor);
        return windowPos == monPos && windowSize == monSize;
    }

    /// <summary>
    /// Whether the window is currently in ExclusiveFullscreen (DXGI exclusive — black flashes).
    /// </summary>
    private bool IsExclusiveFullscreen() => Mode == ModeEnum.ExclusiveFullscreen;

    /// <summary>
    /// Whether the window is in any fullscreen-like mode we do not want for free placement.
    /// </summary>
    private bool IsFullscreenLike() =>
        Mode == ModeEnum.ExclusiveFullscreen || Mode == ModeEnum.Fullscreen;

    /// <summary>
    /// Forces the window out of ExclusiveFullscreen / Fullscreen into borderless Windowed.
    /// Exclusive mode must never stick on video outputs — it causes black flickering.
    /// </summary>
    /// <remarks>
    /// On Windows, Godot can stick in exclusive unless we briefly leave borderless and nudge size
    /// before setting Mode back to Windowed. Then we reinforce via DisplayServer.
    /// </remarks>
    private void ForceBorderlessWindowed()
    {
        bool wasExclusive = Mode == ModeEnum.ExclusiveFullscreen;

        if (wasExclusive)
        {
            // Proven Godot/Windows path to leave exclusive mode without getting stuck.
            Borderless = false;
            Vector2I s = Size;
            if (s.X > 1)
                Size = new Vector2I(s.X - 1, Mathf.Max(1, s.Y));
        }

        Mode = ModeEnum.Windowed;
        Borderless = true;

        if (TryGetNativeWindowId(out int windowId))
        {
            DisplayServer.WindowSetMode(DisplayServer.WindowMode.Windowed, windowId);
            DisplayServer.WindowSetFlag(DisplayServer.WindowFlags.Borderless, true, windowId);
        }

        if (wasExclusive)
            InvalidateGeometryCache();
    }

    /// <summary>
    /// When a window is sized/positioned exactly to a monitor, Windows/Godot often promotes it to
    /// ExclusiveFullscreen. Expanding width by 1px (off the right edge) breaks the exact match
    /// while still fully covering the display — no visible gap on the target screen.
    /// </summary>
    private static Vector2I AntiExclusivePlacementSize(Vector2I intendedSize, bool fullCoverage)
    {
        if (!fullCoverage || intendedSize.X <= 0)
            return intendedSize;
        return new Vector2I(intendedSize.X + 1, intendedSize.Y);
    }

    /// <summary>
    /// Updates the output to show the correct region of the canvas.
    /// Physical screens are borderless windowed windows on a monitor;
    /// Window screens are portable OS-decorated windows the user can move freely.
    /// </summary>
    /// <remarks>
    /// Never use ExclusiveFullscreen for physical outputs (black frame flashes). Exact full-monitor
    /// geometry is placed 1px wider so the engine cannot promote the window to exclusive mode.
    /// Partial sizes must stay true Windowed with Window.Size in sync, or the surface goes grey.
    /// </remarks>
    public void UpdateOutputRegion()
    {
        if (OutputSize.X <= 0 || OutputSize.Y <= 0)
        {
            GD.Print("VideoOutputDevice:UpdateOutputRegion - Invalid output size, must be positive.");
            return;
        }

        // Master disable closes all house displays without destroying the canvas model.
        if (IsGlobalOutputDisabled())
        {
            HideScreenWindow();
            return;
        }

        if (IsVirtual)
        {
            HideScreenWindow();
            return;
        }

        try
        {
            var canvas = DisplaysManager.Canvas;

            Rect2 canvasRect = new Rect2(0, 0, canvas.CanvasSize.X, canvas.CanvasSize.Y);
            Rect2 outputRect = new Rect2(CanvasPosition, OutputSize);
            Rect2 clippedRect = canvasRect.Intersection(outputRect);

            if (clippedRect.Size.X <= 0 || clippedRect.Size.Y <= 0)
            {
                HideScreenWindow();
                GD.Print($"VideoOutputDevice:UpdateOutputRegion - No valid region for output '{OutputName}' within canvas bounds.");
                return;
            }

            var intendedSize = (Vector2I)clippedRect.Size;

            // In-place test pattern update (shared by physical + window paths)
            if (_testPattern != null && GodotObject.IsInstanceValid(_testPattern))
                _testPattern.ApplyLayout(OutputSize, Vector2I.Zero, OutputName);

            if (IsWindow)
            {
                UpdatePortableWindowRegion(clippedRect, intendedSize);
            }
            else
            {
                UpdatePhysicalMonitorRegion(clippedRect, intendedSize);
            }

            // Native window id exists after placement — re-assert vsync + blackout stacking.
            ApplyPendingVSyncMode();
            if (_blackoutOverlay != null && IsInstanceValid(_blackoutOverlay) && _blackoutOverlay.Visible)
                MoveChild(_blackoutOverlay, GetChildCount() - 1);
        }
        catch (Exception ex)
        {
            GD.Print($"VideoOutputDevice:UpdateOutputRegion - Error: {ex.Message}. Stack trace: {ex.StackTrace}");
        }
    }

    /// <summary>
    /// Places a portable window with OS title bar and standard window controls.
    /// Canvas region size drives <see cref="Window.ContentScaleSize"/>; the OS window may be
    /// freely resized and content stretches to match without changing canvas editor data.
    /// </summary>
    private void UpdatePortableWindowRegion(Rect2 clippedRect, Vector2I intendedSize)
    {
        if (_userDismissedWindow)
        {
            if (Visible)
                Hide();
            return;
        }

        Title = string.IsNullOrWhiteSpace(OutputName) ? "Cue2 Output" : OutputName;
        Transparent = OutputTransparent;
        Unresizable = false;
        AlwaysOnTop = false;
        MinSize = new Vector2I(160, 90);

        // Ensure OS-decorated windowed mode (not borderless / exclusive).
        if (IsFullscreenLike() || Mode != ModeEnum.Windowed)
            Mode = ModeEnum.Windowed;
        Borderless = false;

        // Design resolution = canvas output region. OS resize stretches this (Godot content scale).
        ApplyPortableContentScale(intendedSize);

        Vector2I placePos = ResolvePortableWindowPosition(intendedSize);

        bool designSizeChanged = _lastPortableContentScaleSize != intendedSize
            || _lastClippedRect != clippedRect;
        bool firstShow = !Visible || _lastPortableClientSize.X <= 0;

        // Keep the user's OS window size when they stretched it; only reset size when the
        // canvas-editor design size changes or the window is first shown.
        Vector2I placeSize = (firstShow || designSizeChanged)
            ? intendedSize
            : new Vector2I(Mathf.Max(MinSize.X, Size.X), Mathf.Max(MinSize.Y, Size.Y));

        bool scaleOk = ContentScaleMode == ContentScaleModeEnum.CanvasItems
            && ContentScaleSize == intendedSize
            && ContentScaleAspect == ContentScaleAspectEnum.Ignore;

        bool alreadyPlaced = _lastClippedRect == clippedRect
            && _lastDisplayOffset == DisplayOffset
            && _lastWindowPos == placePos
            && _lastPortableContentScaleSize == intendedSize
            && Visible
            && !Borderless
            && Mode == ModeEnum.Windowed
            && scaleOk;

        if (alreadyPlaced)
            return;

        _isPlacingWindow = true;
        try
        {
            if (!Visible)
                Show();

            // Only force Size when design size changed / first show; always sync position.
            ApplyPortableWindowGeometry(placePos, placeSize, forceSize: firstShow || designSizeChanged);
            EnsureContentLayout(intendedSize);
            RefreshLayerHostSizes();
        }
        finally
        {
            _isPlacingWindow = false;
        }

        _lastClippedRect = clippedRect;
        _lastDisplayOffset = DisplayOffset;
        _lastPortableContentScaleSize = intendedSize;
        _lastPortableClientSize = Size;
        _lastWindowSize = Size;

        GD.Print($"VideoOutputDevice:UpdatePortableWindowRegion - '{OutputName}' pos={placePos} " +
                 $"clientSize={Size} designSize={intendedSize} scale={ContentScaleMode} " +
                 $"Borderless={Borderless} clipped={clippedRect}");
    }

    /// <summary>
    /// Absolute desktop position for a portable window: saved DisplayOffset, or a default on the primary screen.
    /// </summary>
    private Vector2I ResolvePortableWindowPosition(Vector2I windowSize)
    {
        // Non-zero DisplayOffset is treated as an absolute desktop position (user-moved or saved).
        if (DisplayOffset != Vector2I.Zero)
            return DisplayOffset;

        // Default: slightly inset on the primary monitor so the title bar is visible.
        int primary = DisplayServer.GetPrimaryScreen();
        if (primary < 0)
            primary = 0;
        if (primary >= DisplayServer.GetScreenCount())
            return new Vector2I(80, 80);

        var monPos = DisplayServer.ScreenGetPosition(primary);
        var monSize = DisplayServer.ScreenGetSize(primary);
        int x = monPos.X + Mathf.Max(40, (monSize.X - windowSize.X) / 4);
        int y = monPos.Y + Mathf.Max(40, (monSize.Y - windowSize.Y) / 4);
        return new Vector2I(x, y);
    }

    /// <summary>
    /// Applies OS-decorated (bordered) window geometry without forcing borderless flags.
    /// </summary>
    /// <param name="windowPos">Absolute desktop position.</param>
    /// <param name="windowSize">Client size to apply when <paramref name="forceSize"/> is true.</param>
    /// <param name="forceSize">When false, leave the user's OS resize alone and only move the window.</param>
    private void ApplyPortableWindowGeometry(Vector2I windowPos, Vector2I windowSize, bool forceSize = true)
    {
        if (windowSize.X <= 0 || windowSize.Y <= 0)
            return;

        Mode = ModeEnum.Windowed;
        Borderless = false;

        if (forceSize && Size != windowSize)
            Size = windowSize;
        if (Position != windowPos)
            Position = windowPos;

        if (TryGetNativeWindowId(out int windowId))
        {
            DisplayServer.WindowSetMode(DisplayServer.WindowMode.Windowed, windowId);
            DisplayServer.WindowSetFlag(DisplayServer.WindowFlags.Borderless, false, windowId);
            DisplayServer.WindowSetPosition(windowPos, windowId);
            if (forceSize)
                DisplayServer.WindowSetSize(windowSize, windowId);
        }

        _lastWindowPos = windowPos;
        _lastWindowSize = forceSize ? windowSize : Size;
    }

    /// <summary>
    /// Places a borderless window on a physical monitor (existing show-control path).
    /// </summary>
    private void UpdatePhysicalMonitorRegion(Rect2 clippedRect, Vector2I intendedSize)
    {
        if (TargetMonitor < 0 || TargetMonitor >= DisplayServer.GetScreenCount())
        {
            GD.PrintErr($"VideoOutputDevice:UpdatePhysicalMonitorRegion - Target monitor {TargetMonitor} is out of bounds (screen_count = {DisplayServer.GetScreenCount()})");
            HideScreenWindow();
            return;
        }

        // Physical outputs are 1:1 pixels — do not use content scale / stretch.
        DisableContentScale();

        // Display home origin + DisplayOffset + canvas clip adjustment (global desktop coords)
        var monitorPos = DisplayServer.ScreenGetPosition(TargetMonitor);
        var intendedPos = monitorPos + DisplayOffset + (Vector2I)(clippedRect.Position - (Vector2)CanvasPosition);
        bool fullCoverage = IsFullMonitorCoverage(intendedPos, intendedSize);

        // Exact full-monitor match → +1px width so we never land in ExclusiveFullscreen.
        var placePos = intendedPos;
        var placeSize = AntiExclusivePlacementSize(intendedSize, fullCoverage);

        // Wayland cannot move a window onto a chosen output. A full-monitor toplevel
        // is created on the focused screen (the operator UI) and hides the main window.
        bool compositorPlacesWindow = !LinuxWindowEmbedPolicy.CanPlaceWindowsOnSpecificScreen;
        if (compositorPlacesWindow)
            placeSize = WaylandSafeOutputSize(intendedSize);

        bool modeOk = Mode == ModeEnum.Windowed && !IsExclusiveFullscreen();
        bool chromeOk = compositorPlacesWindow ? !Borderless : Borderless;

        // Skip only when geometry + safe windowed mode are already applied.
        if (_lastClippedRect == clippedRect
            && _lastDisplayOffset == DisplayOffset
            && _lastWindowPos == placePos
            && _lastWindowSize == placeSize
            && Visible
            && chromeOk
            && modeOk
            && Size == placeSize)
        {
            return;
        }

        var operatorBefore = CaptureOperatorWindow();
        bool wasFullscreenLike = IsFullscreenLike();

        Transparent = OutputTransparent;
        Unresizable = !compositorPlacesWindow;

        // Always demote exclusive/fullscreen before free placement (never leave exclusive active).
        if (wasFullscreenLike || Mode != ModeEnum.Windowed)
            ForceBorderlessWindowed();
        else
            Mode = ModeEnum.Windowed;

        // Wayland: keep OS chrome so the user can drag the output onto the house display.
        // X11/Windows/macOS: borderless free placement on the target monitor.
        Borderless = !compositorPlacesWindow;
        if (compositorPlacesWindow)
        {
            Title = string.IsNullOrWhiteSpace(OutputName) ? "Cue2 Output" : OutputName;
            MinSize = new Vector2I(320, 180);
            // Size before Show so the first Wayland toplevel is not created full-monitor.
            if (Size != placeSize)
                Size = placeSize;
        }

        if (!EnsureNativeOutputWindow())
        {
            GD.PrintErr($"VideoOutputDevice:UpdatePhysicalMonitorRegion - '{OutputName}' has no native window id; "
                        + "refusing DisplayServer placement so the main UI is not resized.");
            HideScreenWindow();
            RestoreOperatorWindowIfClobbered(operatorBefore);
            return;
        }

        if (LinuxWindowEmbedPolicy.CanPlaceWindowsOnSpecificScreen)
            CurrentScreen = TargetMonitor;

        // When leaving exclusive/fullscreen, bounce visibility so the content surface
        // is recreated cleanly (prevents grey blank windows after mode exit).
        if (wasFullscreenLike && Visible)
        {
            Hide();
            EnsureNativeOutputWindow();
            if (compositorPlacesWindow)
            {
                Mode = ModeEnum.Windowed;
                Borderless = false;
            }
            else
                ForceBorderlessWindowed();
        }

        if (compositorPlacesWindow)
            ApplyPortableWindowGeometry(placePos, placeSize);
        else
            ApplyNativeWindowGeometry(placePos, placeSize);

        EnsureContentLayout(intendedSize);
        RefreshLayerHostSizes();

        // Final safety: if engine still promoted to exclusive (race after Show/size), demote and re-place.
        if (IsExclusiveFullscreen() || Mode != ModeEnum.Windowed)
        {
            GD.Print($"VideoOutputDevice:UpdatePhysicalMonitorRegion - '{OutputName}' demoting Mode={Mode} (exclusive/fullscreen not allowed on outputs).");
            if (compositorPlacesWindow)
            {
                Mode = ModeEnum.Windowed;
                Borderless = false;
            }
            else
            {
                ForceBorderlessWindowed();
                CurrentScreen = TargetMonitor;
            }

            // Ensure anti-exclusive size even if fullCoverage detection raced
            var safeSize = placeSize;
            if (!compositorPlacesWindow
                && safeSize == DisplayServer.ScreenGetSize(TargetMonitor)
                && placePos == DisplayServer.ScreenGetPosition(TargetMonitor))
            {
                safeSize = AntiExclusivePlacementSize(safeSize, true);
            }

            if (compositorPlacesWindow)
                ApplyPortableWindowGeometry(placePos, safeSize);
            else
                ApplyNativeWindowGeometry(placePos, safeSize);
            EnsureContentLayout(intendedSize);
            RefreshLayerHostSizes();
        }

        RestoreOperatorWindowIfClobbered(operatorBefore);

        // Deferred re-check: promotion sometimes happens a frame later on Windows.
        CallDeferred(nameof(DeferredDemoteExclusiveIfNeeded));

        _lastClippedRect = clippedRect;
        _lastDisplayOffset = DisplayOffset;

        GD.Print($"VideoOutputDevice:UpdatePhysicalMonitorRegion - '{OutputName}' Mode={Mode} Borderless={Borderless} " +
                 $"monitor={TargetMonitor} pos={placePos} size={placeSize} intended={intendedSize} " +
                 $"full={fullCoverage} exclusivePrevent={fullCoverage} waylandSafe={compositorPlacesWindow} clipped={clippedRect}");
    }

    /// <summary>
    /// Caps a house-screen window so a Wayland toplevel (always spawned on the focused
    /// output) cannot cover the operator UI.
    /// </summary>
    /// <param name="intended">Canvas-clipped output size.</param>
    /// <returns>A size that leaves the main window reachable.</returns>
    private static Vector2I WaylandSafeOutputSize(Vector2I intended)
    {
        Vector2I operatorSize;
        try
        {
            operatorSize = DisplayServer.WindowGetSize(LinuxWindowEmbedPolicy.MainWindowId);
        }
        catch
        {
            operatorSize = new Vector2I(1280, 720);
        }

        if (operatorSize.X <= 0 || operatorSize.Y <= 0)
            operatorSize = new Vector2I(1280, 720);

        int maxW = Mathf.Max(640, (int)(operatorSize.X * 0.55f));
        int maxH = Mathf.Max(360, (int)(operatorSize.Y * 0.55f));
        int w = Mathf.Clamp(intended.X > 0 ? intended.X : 640, 320, maxW);
        int h = Mathf.Clamp(intended.Y > 0 ? intended.Y : 360, 180, maxH);
        return new Vector2I(w, h);
    }

    /// <summary>
    /// One-frame later guard: Windows/Godot may promote exact-size borderless windows to exclusive
    /// after the initial placement call returns. Portable Window mode is excluded.
    /// </summary>
    private void DeferredDemoteExclusiveIfNeeded()
    {
        if (!GodotObject.IsInstanceValid(this) || IsVirtual || IsWindow)
            return;

        if (!IsExclusiveFullscreen() && Mode == ModeEnum.Windowed)
            return;

        var operatorBefore = CaptureOperatorWindow();
        GD.Print($"VideoOutputDevice:DeferredDemoteExclusiveIfNeeded - '{OutputName}' Mode={Mode}, forcing borderless windowed.");
        ForceBorderlessWindowed();

        if (_lastWindowPos.X != int.MinValue && _lastWindowSize.X > 0)
        {
            var pos = _lastWindowPos;
            var size = _lastWindowSize;
            // If cached size still exactly matches the monitor, widen by 1px.
            if (TargetMonitor >= 0 && TargetMonitor < DisplayServer.GetScreenCount())
            {
                var monPos = DisplayServer.ScreenGetPosition(TargetMonitor);
                var monSize = DisplayServer.ScreenGetSize(TargetMonitor);
                if (pos == monPos && (size == monSize || size == new Vector2I(monSize.X, monSize.Y)))
                    size = AntiExclusivePlacementSize(monSize, true);
            }

            if (LinuxWindowEmbedPolicy.CanPlaceWindowsOnSpecificScreen && TryGetNativeWindowId(out _))
                CurrentScreen = TargetMonitor;
            ApplyNativeWindowGeometry(pos, size);
            EnsureContentLayout(size);
            RefreshLayerHostSizes();
        }

        RestoreOperatorWindowIfClobbered(operatorBefore);
    }

    /// <summary>
    /// Starts/stops geometry polling when a portable output window is shown or hidden.
    /// </summary>
    private void OnVisibilityChangedForGeometryPoll()
    {
        if (_geometryPollTimer == null || !GodotObject.IsInstanceValid(_geometryPollTimer))
            return;
        bool need = IsWindow && Visible && !_userDismissedWindow;
        if (need && !_geometryPollTimer.IsStopped())
            return;
        if (need)
            _geometryPollTimer.Start();
        else
            _geometryPollTimer.Stop();
    }

    /// <summary>
    /// Syncs portable-window position into DisplayOffset after the user moves it (for session save).
    /// Content stretch on OS resize is handled by Godot <see cref="Window.ContentScaleMode"/>.
    /// </summary>
    private void PollPortableGeometry()
    {
        if (!IsWindow || !Visible || _isPlacingWindow || _userDismissedWindow)
            return;

        // Persist free placement so reload restores the last user position.
        if (Position != DisplayOffset && Position != Vector2I.Zero)
        {
            DisplayOffset = Position;
            _lastDisplayOffset = DisplayOffset;
            _lastWindowPos = Position;
        }

        // Remember client size only — do not write back to OutputSize / canvas editor.
        // ContentScaleSize stays at the canvas design size so content stretches to fit.
        if (Size.X > 0 && Size.Y > 0)
        {
            _lastPortableClientSize = Size;
            _lastWindowSize = Size;
        }

        // Keep content scale locked to canvas design size if something external cleared it.
        if (_lastPortableContentScaleSize.X > 0
            && (ContentScaleMode != ContentScaleModeEnum.CanvasItems
                || ContentScaleSize != _lastPortableContentScaleSize))
        {
            ApplyPortableContentScale(_lastPortableContentScaleSize);
        }
    }

    /// <summary>
    /// Keeps the content root filling the window content viewport after size changes.
    /// </summary>
    private void EnsureContentLayout(Vector2I windowSize)
    {
        if (_sceneRoot == null || !IsInstanceValid(_sceneRoot))
            return;

        _sceneRoot.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        // Explicit size backup if anchors have not resolved yet this frame
        if (_sceneRoot.Size.X < 1 || _sceneRoot.Size.Y < 1)
            _sceneRoot.Size = windowSize;
    }

    /// <summary>
    /// Keeps per-layer host controls sized to the current output region after resizes.
    /// </summary>
    private void RefreshLayerHostSizes()
    {
        UpdateAllLayerDisplayRects();
    }

    /// <summary>
    /// Places the window as borderless windowed. Window.Size is the content-viewport authority;
    /// DisplayServer reinforces absolute multi-monitor desktop coordinates and windowed mode.
    /// </summary>
    private void ApplyNativeWindowGeometry(Vector2I windowPos, Vector2I windowSize)
    {
        if (windowSize.X <= 0 || windowSize.Y <= 0)
            return;

        // Never apply geometry while exclusive — exit first or content surface can go grey/black.
        if (IsExclusiveFullscreen())
            ForceBorderlessWindowed();

        Mode = ModeEnum.Windowed;
        Borderless = true;

        // Always drive Godot Window.Size first — this is what the content viewport uses.
        // DisplayServer-only size changes (without Size) are a common cause of grey blank windows.
        if (Size != windowSize)
            Size = windowSize;
        if (Position != windowPos)
            Position = windowPos;

        if (TryGetNativeWindowId(out int windowId))
        {
            // Force windowed + borderless at the OS level so exact full-screen rects do not
            // stick as ExclusiveFullscreen (black flashes on mode entry / multi-monitor).
            DisplayServer.WindowSetMode(DisplayServer.WindowMode.Windowed, windowId);
            DisplayServer.WindowSetFlag(DisplayServer.WindowFlags.Borderless, true, windowId);
            DisplayServer.WindowSetPosition(windowPos, windowId);
            DisplayServer.WindowSetSize(windowSize, windowId);
        }

        _lastWindowPos = windowPos;
        _lastWindowSize = windowSize;
    }

    /// <summary>
    /// Enables or disables the full-screen test pattern for this output.
    /// </summary>
    /// <param name="toggle">True to show; false to remove.</param>
    public void ToggleTestPattern(bool toggle)
    {
        SetTestPatternRect(toggle, new Rect2(Vector2.Zero, OutputSize));
    }

    /// <summary>
    /// Shows, updates, or removes the screen-level test pattern.
    /// </summary>
    /// <param name="enable">True to show/update; false to remove.</param>
    /// <param name="rect">Local rect for the pattern (usually full <see cref="OutputSize"/>).</param>
    public void SetTestPatternRect(bool enable, Rect2 rect)
    {
        if (enable)
        {
            Vector2I size = (Vector2I)rect.Size;
            Vector2I pos = (Vector2I)rect.Position;
            if (_testPattern == null || !GodotObject.IsInstanceValid(_testPattern))
            {
                _testPattern = new TestPattern(size, pos, OutputName);
                AddChild(_testPattern);
            }
            else
            {
                _testPattern.ApplyLayout(size, pos, OutputName);
            }
        }
        else if (_testPattern != null)
        {
            if (GodotObject.IsInstanceValid(_testPattern))
            {
                if (_testPattern.GetParent() == this)
                    RemoveChild(_testPattern);
                _testPattern.QueueFree();
            }
            _testPattern = null;
        }
    }

    /// <summary>
    /// Shows, updates, or removes the canvas-wide test pattern slice for this output.
    /// </summary>
    /// <param name="enable">True to show/update; false to remove.</param>
    /// <param name="rect">Rect in this output's local coordinates (typically full canvas size,
    /// positioned so the pattern origin aligns with canvas (0,0)).</param>
    /// <param name="label">Display name drawn on the pattern (e.g. "Canvas").</param>
    public void SetCanvasTestPattern(bool enable, Rect2 rect, string label = "Canvas")
    {
        if (enable)
        {
            Vector2I size = (Vector2I)rect.Size;
            Vector2I pos = (Vector2I)rect.Position;
            string name = string.IsNullOrEmpty(label) ? "Canvas" : label;
            if (_canvasTestPattern == null || !GodotObject.IsInstanceValid(_canvasTestPattern))
            {
                _canvasTestPattern = new TestPattern(size, pos, name);
                AddChild(_canvasTestPattern);
            }
            else
            {
                _canvasTestPattern.ApplyLayout(size, pos, name);
            }
        }
        else
        {
            RemoveCanvasTestPattern();
        }
    }

    /// <summary>
    /// Removes the canvas-wide test pattern if present.
    /// </summary>
    public void RemoveCanvasTestPattern()
    {
        if (_canvasTestPattern == null)
            return;

        if (GodotObject.IsInstanceValid(_canvasTestPattern))
        {
            if (_canvasTestPattern.GetParent() == this)
                RemoveChild(_canvasTestPattern);
            _canvasTestPattern.QueueFree();
        }
        _canvasTestPattern = null;
    }

    /// <summary>
    /// True when a canvas-wide test pattern is currently shown on this output.
    /// </summary>
    public bool CanvasTestPatternStatus() =>
        _canvasTestPattern != null && GodotObject.IsInstanceValid(_canvasTestPattern);

    /// <summary>
    /// Adds or updates a layer test pattern at the given local rect.
    /// </summary>
    /// <param name="layerId">Layer identity.</param>
    /// <param name="layerName">Label drawn on the pattern.</param>
    /// <param name="rect">Rect in this output's local coordinates.</param>
    public void AddLayerTestPattern(int layerId, string layerName, Rect2 rect)
    {
        Vector2I size = (Vector2I)rect.Size;
        Vector2I pos = (Vector2I)rect.Position;

        if (_layerTestPatterns.TryGetValue(layerId, out var existing)
            && existing != null && GodotObject.IsInstanceValid(existing))
        {
            existing.ApplyLayout(size, pos, layerName);
            return;
        }

        var tp = new TestPattern(size, pos, layerName);
        AddChild(tp);
        _layerTestPatterns[layerId] = tp;
    }

    /// <summary>
    /// Removes a layer test pattern if present.
    /// </summary>
    /// <param name="layerId">Layer identity.</param>
    public void RemoveLayerTestPattern(int layerId)
    {
        if (!_layerTestPatterns.TryGetValue(layerId, out var tp))
            return;

        _layerTestPatterns.Remove(layerId);
        if (tp != null && GodotObject.IsInstanceValid(tp))
        {
            if (tp.GetParent() == this)
                RemoveChild(tp);
            tp.QueueFree();
        }
    }

    /// <summary>
    /// True when the screen-level test pattern is currently shown.
    /// </summary>
    public bool TestPatternStatus() =>
        _testPattern != null && GodotObject.IsInstanceValid(_testPattern);

    /// <summary>
    /// Refreshes the screen-level test pattern size to match <see cref="OutputSize"/> (if enabled).
    /// </summary>
    public void RefreshScreenTestPattern()
    {
        if (!TestPatternStatus())
            return;
        SetTestPatternRect(true, new Rect2(Vector2.Zero, OutputSize));
    }

    public void SetTransparent(bool state)
    {
        OutputTransparent = state;
        this.Transparent = state;
    }

    /// <summary>
    /// Serializes the output device data.
    /// </summary>
    /// <returns>Dictionary containing output data.</returns>
    public Godot.Collections.Dictionary GetData()
    {
        var data = new Godot.Collections.Dictionary();
        data.Add("OutputId", OutputId);
        data.Add("OutputName", OutputName);
        data.Add("CanvasPositionX", CanvasPosition.X);
        data.Add("CanvasPositionY", CanvasPosition.Y);
        data.Add("OutputSizeX", OutputSize.X);
        data.Add("OutputSizeY", OutputSize.Y);
        data.Add("TargetMonitor", TargetMonitor);
        data.Add("Transparent", OutputTransparent);
        data.Add("DisplayOffsetX", DisplayOffset.X);
        data.Add("DisplayOffsetY", DisplayOffset.Y);
        data.Add("KeepAspect", KeepAspect);
        // Runtime overlay state — required so undo/redo restores screen test patterns.
        data.Add("TestPatternEnabled", TestPatternStatus());
        return data;
    }

    /// <summary>
    /// Loads the output device data from a dictionary.
    /// </summary>
    /// <param name="data">Dictionary containing output data.</param>
    /// <remarks>
    /// Does not apply test-pattern overlays here — callers re-apply after the window is
    /// parented and sized (see <see cref="Cue2.Services.DisplaysManager.LoadFromData"/>).
    /// </remarks>
    public void LoadFromData(Godot.Collections.Dictionary data)
    {
        OutputId = (int)data["OutputId"];
        OutputName = (string)data["OutputName"];
        TargetMonitor = (int)data["TargetMonitor"];

        var canvPosX = data.ContainsKey("CanvasPositionX") ? (int)data["CanvasPositionX"] : 0;
        var canvPosY = data.ContainsKey("CanvasPositionY") ? (int)data["CanvasPositionY"] : 0;
        CanvasPosition = new Vector2I(canvPosX, canvPosY);

        var outSizeX = data.ContainsKey("OutputSizeX") ? (int)data["OutputSizeX"] : 1920;
        var outSizeY = data.ContainsKey("OutputSizeY") ? (int)data["OutputSizeY"] : 1080;
        OutputSize = new Vector2I(outSizeX, outSizeY);

        OutputTransparent = data.ContainsKey("Transparent") ? (bool)data["Transparent"] : false;

        var offX = data.ContainsKey("DisplayOffsetX") ? (int)data["DisplayOffsetX"] : 0;
        var offY = data.ContainsKey("DisplayOffsetY") ? (int)data["DisplayOffsetY"] : 0;
        DisplayOffset = new Vector2I(offX, offY);

        KeepAspect = data.ContainsKey("KeepAspect") && (bool)data["KeepAspect"];
    }
    
    private bool _disposed;

    public override void _ExitTree()
    {
        VisibilityChanged -= OnVisibilityChangedForGeometryPoll;
        if (_geometryPollTimer != null && GodotObject.IsInstanceValid(_geometryPollTimer))
        {
            _geometryPollTimer.Stop();
            _geometryPollTimer.Timeout -= PollPortableGeometry;
        }
        // Only clean children here — do not QueueFree self (already exiting tree)
        DisposeContents();
        base._ExitTree();
    }

    public new void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        DisposeContents();

        if (IsInsideTree())
            QueueFree();
        else if (IsInstanceValid(this))
            Free();

        GD.Print($"VideoOutputDevice:Dispose - Disposed output device '{OutputName}'.");
    }

    private void DisposeContents()
    {
        try { Hide(); } catch { /* ignore */ }

        if (_testPattern != null && IsInstanceValid(_testPattern))
        {
            try
            {
                if (_testPattern.GetParent() == this)
                    RemoveChild(_testPattern);
                _testPattern.QueueFree();
            }
            catch { /* ignore */ }
            _testPattern = null;
        }

        if (_canvasTestPattern != null && IsInstanceValid(_canvasTestPattern))
        {
            try
            {
                if (_canvasTestPattern.GetParent() == this)
                    RemoveChild(_canvasTestPattern);
                _canvasTestPattern.QueueFree();
            }
            catch { /* ignore */ }
            _canvasTestPattern = null;
        }

        foreach (var kvp in _layerTestPatterns.ToList())
        {
            var tp = kvp.Value;
            if (tp != null && IsInstanceValid(tp))
            {
                try
                {
                    if (tp.GetParent() == this)
                        RemoveChild(tp);
                    tp.QueueFree();
                }
                catch { /* ignore */ }
            }
        }
        _layerTestPatterns.Clear();

        foreach (var layer in _activeLayers.Keys.ToList())
        {
            if (layer != null && IsInstanceValid(layer))
            {
                try
                {
                    if (layer.GetParent() != null)
                        layer.GetParent().RemoveChild(layer);
                    layer.QueueFree();
                }
                catch { /* ignore */ }
            }
        }
        _activeLayers.Clear();
    }

}