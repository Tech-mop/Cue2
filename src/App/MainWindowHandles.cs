using System.Threading.Tasks;
// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using Cue2.Services;
using Cue2.UI.Utilities;
using Godot;

namespace Cue2.App;

/// <summary>
/// Borderless main-window title drag, edge/corner resize, maximize/fullscreen toggle, and geometry persistence.
/// </summary>
/// <remarks>
/// Expand button enters non-exclusive <see cref="DisplayServer.WindowMode.Fullscreen"/>.
/// Title double-click enters <see cref="DisplayServer.WindowMode.Maximized"/> only.
/// Single-click / drag on the header does not leave fullscreen; drag-out applies to OS maximize only.
/// On Windows, a borderless window sized exactly to a monitor is demoted from ExclusiveFullscreen
/// (same 1px guard as video outputs) so chrome and the engine stay in agreement.
/// </remarks>
public partial class MainWindowHandles : Control
{
	private GlobalData _globalData;
	private GlobalSignals _globalSignals;

	private static readonly Vector2I MinWindowSize = new Vector2I(600, 370);

	/// <summary>Project default size captured before any HiDPI rescale (typically 1152×648).</summary>
	private Vector2I _startupDesignSize = new Vector2I(1152, 648);

	/// <summary>True when a previous session's size/maximize was applied (not first-run defaults).</summary>
	private bool _usedSavedGeometry;

	private Timer _resizeSaveTimer;
	private Timer _positionPollTimer;
	private Vector2I _lastKnownPosition;

	/// <summary>Pre-fill-screen size/position used by maximize/fullscreen toggles and drag-out of maximize.</summary>
	private Vector2I _restoreSize = Vector2I.Zero;
	private Vector2I _restorePosition = Vector2I.Zero;
	private bool _hasRestoreRect;

	/// <summary>
	/// Fill-screen mode requested by chrome (engine <see cref="Window.Mode"/> can lag on borderless).
	/// </summary>
	private enum FillMode
	{
		None,
		Maximized,
		Fullscreen
	}

	private FillMode _fillMode = FillMode.None;

	/// <summary>Skip persistence for one frame while mode/geometry is being applied.</summary>
	private bool _suppressGeometrySave;

	/// <summary>True while demoting an unwanted ExclusiveFullscreen promotion (avoids SizeChanged loops).</summary>
	private bool _guardingExclusive;

	/// <summary>Resting border colour (edit or show mode); alert flash fades back to this.</summary>
	private Color _restBorderColor;
	private bool _isFading;
	private float _fadeProgress;
	private Color _highlightColor;
	private StyleBoxFlat _borderStylebox;

	private Control _headerHandle;
	private Control _rightHandle;
	private Control _topHandle;
	private Control _leftHandle;
	private Control _bottomHandle;
	private Control _bottomRightHandle;
	private Control _bottomLeftHandle;
	private Control _topRightHandle;
	private Control _topLeftHandle;

	/// <inheritdoc />
	public override void _Ready()
	{
		_globalData = GetNode<GlobalData>("/root/GlobalData");
		_globalSignals = GetNode<GlobalSignals>("/root/GlobalSignals");

		_resizeSaveTimer = new Timer { OneShot = true, WaitTime = 0.25f };
		_resizeSaveTimer.Timeout += SaveCurrentWindowState;
		AddChild(_resizeSaveTimer);

		_positionPollTimer = new Timer { OneShot = false, WaitTime = 0.25f, Autostart = true };
		_positionPollTimer.Timeout += CheckAndDebounceWindowPosition;
		AddChild(_positionPollTimer);

		SetProcess(false);

		var window = GetWindow();
		window.MinSize = MinWindowSize;
		window.MaxSize = UiUtilities.ComputeVirtualDesktopSize();

		// Capture the project default before any scale/fit so deferred finalize cannot
		// double-multiply an already-scaled Size.
		if (window.Size.X >= MinWindowSize.X && window.Size.Y >= MinWindowSize.Y)
			_startupDesignSize = window.Size;

		// Startup order (critical on macOS HiDPI):
		// 1) Content scale only (must not own outer pixel size)
		// 2) Restore saved geometry OR first-run size (display scale + usable-screen fit)
		// 3) Deferred re-apply after the display server settles (scale is often 1× in _Ready)
		_suppressGeometrySave = true;
		_globalData.RefreshBaseDisplayScale(notifyUi: false);
		float userScale = _globalData.UserDataManager?.UiScale ?? UserDataManager.DefaultUiScale;
		UiUtilities.RescaleUi(window, userScale, _globalData.BaseDisplayScale);
		// WrapControls would auto-grow the frame when ContentScaleFactor changes and
		// overwrite the restored size; main window geometry is managed explicitly.
		window.WrapControls = false;

		_usedSavedGeometry = RestoreWindowFromUserData(window);
		if (!_usedSavedGeometry)
			ApplyFirstRunGeometry(window);

		_lastKnownPosition = window.Position;
		CallDeferred(nameof(FinalizeStartupGeometry));

		_globalSignals.LogAlert += OnAlertReceived;
		_globalSignals.ShowModeChanged += OnShowModeChanged;

		var border = GetNode<Panel>("%Border");
		// Duplicate so border colour mutations do not touch a shared theme resource.
		if (border.GetThemeStylebox("panel") is StyleBoxFlat style)
		{
			_borderStylebox = (StyleBoxFlat)style.Duplicate();
			border.AddThemeStyleboxOverride("panel", _borderStylebox);
			_highlightColor = GlobalStyles.Danger;
			ApplyShowModeBorder(_globalData?.Settings?.ShowMode == true);
		}

		_headerHandle = GetNode<Control>("%HeaderHandle");
		_rightHandle = GetNode<Control>("%RightHandle");
		_topHandle = GetNode<Control>("%TopHandle");
		_leftHandle = GetNode<Control>("%LeftHandle");
		_bottomHandle = GetNode<Control>("%BottomHandle");
		_bottomRightHandle = GetNode<Control>("%BottomRightCornerHandle");
		_bottomLeftHandle = GetNode<Control>("%BottomLeftCornerHandle");
		_topRightHandle = GetNode<Control>("%TopRightCornerHandle");
		_topLeftHandle = GetNode<Control>("%TopLeftCornerHandle");

		_headerHandle.GuiInput += OnHeaderHandleGuiInput;
		_rightHandle.GuiInput += OnRightHandleGuiInput;
		_topHandle.GuiInput += OnTopHandleGuiInput;
		_leftHandle.GuiInput += OnLeftHandleGuiInput;
		_bottomHandle.GuiInput += OnBottomHandleGuiInput;
		_topRightHandle.GuiInput += OnTopRightHandleGuiInput;
		_bottomLeftHandle.GuiInput += OnBottomLeftHandleGuiInput;
		_topLeftHandle.GuiInput += OnTopLeftHandleGuiInput;
		_bottomRightHandle.GuiInput += OnBottomRightHandleGuiInput;

		window.SizeChanged += OnWindowSizeChanged;
	}

	/// <summary>
	/// Re-applies restored (or first-run) geometry after content scale and the display
	/// server finish settling. Prevents size/position from being overwritten by WrapControls
	/// or a late OS layout pass, and picks up HiDPI scale that was not ready in <c>_Ready</c>.
	/// </summary>
	private void FinalizeStartupGeometry()
	{
		var window = GetWindow();
		if (window == null || !IsInstanceValid(window))
		{
			_suppressGeometrySave = false;
			return;
		}

		_globalData.RefreshBaseDisplayScale(notifyUi: false);
		float userScale = _globalData.UserDataManager?.UiScale ?? UserDataManager.DefaultUiScale;
		UiUtilities.RescaleUi(window, userScale, _globalData.BaseDisplayScale);
		window.WrapControls = false;

		bool useSaved = _usedSavedGeometry
			&& _hasRestoreRect
			&& !ShouldDiscardSavedSizeForFirstRun(_restoreSize);

		if (_fillMode == FillMode.Fullscreen)
		{
			SetWindowMode(window, DisplayServer.WindowMode.Fullscreen);
		}
		else if (_fillMode == FillMode.Maximized)
		{
			SetWindowMode(window, DisplayServer.WindowMode.Maximized);
		}
		else if (useSaved)
		{
			ApplyGeometry(window, _restoreSize, _restorePosition);
			_lastKnownPosition = _restorePosition;
		}
		else
		{
			ApplyFirstRunGeometry(window);
			_lastKnownPosition = window.Position;
		}

		_suppressGeometrySave = false;
		SaveCurrentWindowState();
	}

	/// <summary>
	/// First-launch size: design size × display scale, then grow toward a usable-screen fraction
	/// so 5K/6K panels are not left with a 1152×648-looking frame.
	/// </summary>
	private void ApplyFirstRunGeometry(Window window)
	{
		if (window == null || !IsInstanceValid(window))
			return;

		Vector2I size = ComputeFirstRunWindowSize(window);
		size = UiUtilities.ClampWindowSizeToVirtualDesktop(size, MinWindowSize);
		Vector2I pos = UiUtilities.CenterWindowOnUsableScreen(size, window);
		ApplyGeometry(window, size, pos);
		RememberRestoreRect(size, pos);
		GD.Print($"MainWindowHandles:ApplyFirstRunGeometry - design={_startupDesignSize} " +
		         $"scale={_globalData.BaseDisplayScale:0.###} size={size} pos={pos}");
	}

	/// <summary>
	/// Design size × current <see cref="GlobalData.BaseDisplayScale"/>, grown to the first-run
	/// usable-screen fraction and clamped to the max fraction.
	/// </summary>
	private Vector2I ComputeFirstRunWindowSize(Window window)
	{
		float scale = Mathf.Max(_globalData.BaseDisplayScale, 1f);
		Vector2I scaled = new Vector2I(
			Mathf.Max(MinWindowSize.X, Mathf.RoundToInt(_startupDesignSize.X * scale)),
			Mathf.Max(MinWindowSize.Y, Mathf.RoundToInt(_startupDesignSize.Y * scale)));
		return UiUtilities.FitFirstRunWindowToUsableScreen(scaled, window, MinWindowSize);
	}

	/// <summary>
	/// True when a persisted size is the unscaled project default (or still tiny while the
	/// first-time welcome is pending) and should be replaced by first-run HiDPI geometry.
	/// </summary>
	private bool ShouldDiscardSavedSizeForFirstRun(Vector2I savedSize)
	{
		if (savedSize.X < MinWindowSize.X || savedSize.Y < MinWindowSize.Y)
			return true;

		const int slop = 48;
		bool nearDesign = savedSize.X <= _startupDesignSize.X + slop
			&& savedSize.Y <= _startupDesignSize.Y + slop;
		if (nearDesign && _globalData.BaseDisplayScale > 1.05f)
			return true;

		var udm = _globalData?.UserDataManager;
		if (udm != null && udm.IsFirstTimeStartup)
		{
			Vector2I target = ComputeFirstRunWindowSize(GetWindow());
			if (savedSize.X * 4 < target.X * 3 && savedSize.Y * 4 < target.Y * 3)
				return true;
		}

		return false;
	}

	/// <summary>
	/// Restores previous size / position / fill-screen state from persistent user data.
	/// </summary>
	/// <returns>True when a valid saved size or maximized/fullscreen flag was applied.</returns>
	private bool RestoreWindowFromUserData(Window window)
	{
		var udm = _globalData?.UserDataManager;
		if (udm == null)
			return false;

		bool hasSize = udm.LastWindowSize.X >= MinWindowSize.X && udm.LastWindowSize.Y >= MinWindowSize.Y;
		if (!hasSize && !udm.WasMaximized)
			return false;

		if (hasSize && !udm.WasMaximized && ShouldDiscardSavedSizeForFirstRun(udm.LastWindowSize))
		{
			GD.Print($"MainWindowHandles:RestoreWindowFromUserData - ignoring leftover unscaled size {udm.LastWindowSize}");
			return false;
		}

		if (hasSize)
		{
			// Clamp out sizes corrupted by older order-of-ops bugs (scale applied after restore).
			Vector2I size = UiUtilities.ClampWindowSizeToVirtualDesktop(udm.LastWindowSize, MinWindowSize);
			Vector2I absPos = UiUtilities.ResolveSavedWindowPosition(udm.LastWindowPosition, size);
			RememberRestoreRect(size, absPos);

			if (!udm.WasMaximized)
				ApplyGeometry(window, _restoreSize, _restorePosition);
		}

		if (udm.WasMaximized)
		{
			_fillMode = udm.WasFullscreen ? FillMode.Fullscreen : FillMode.Maximized;
			SetWindowMode(window, udm.WasFullscreen
				? DisplayServer.WindowMode.Fullscreen
				: DisplayServer.WindowMode.Maximized);
		}

		GD.Print($"MainWindowHandles:RestoreWindowFromUserData - size={udm.LastWindowSize} " +
		         $"(clamped apply={_restoreSize}) pos={udm.LastWindowPosition} -> {_restorePosition} " +
		         $"fill={_fillMode}");
		return true;
	}

	private void OnAlertReceived()
	{
		TaskUtil.Run(OnAlertReceivedAsync, "MainWindowHandles.OnAlertReceived");
	}

	private async Task OnAlertReceivedAsync()
	{
		if (_borderStylebox == null)
			return;

		_isFading = false;
		_borderStylebox.BorderColor = _highlightColor;
		await ToSignal(GetTree().CreateTimer(0.5), "timeout");
		_fadeProgress = 0.0f;
		_isFading = true;
		SetProcess(true);
	}

	private void OnShowModeChanged(bool enabled)
	{
		ApplyShowModeBorder(enabled);
	}

	/// <summary>
	/// Accents the window border for Show Mode (title bar stays default chrome).
	/// </summary>
	/// <param name="showMode">True when Show Mode is active.</param>
	private void ApplyShowModeBorder(bool showMode)
	{
		_restBorderColor = showMode
			? GlobalStyles.WindowBorderShowMode
			: GlobalStyles.WindowBorderEditMode;

		if (_borderStylebox == null)
			return;

		// If an alert flash is mid-fade, continue fading toward the new rest colour.
		if (!_isFading)
			_borderStylebox.BorderColor = _restBorderColor;
	}

	private void OnHeaderHandleGuiInput(InputEvent @event)
	{
		if (@event is not InputEventMouseButton mouseEvent)
			return;

		if (mouseEvent.DoubleClick && mouseEvent.ButtonIndex == MouseButton.Left)
		{
			// Header double-click: OS maximize only (never fullscreen).
			ToggleMaximizeFromChrome();
			return;
		}

		if (mouseEvent.Pressed && mouseEvent.ButtonIndex == MouseButton.Left)
			StartDrag();
	}

	private void OnRightHandleGuiInput(InputEvent @event)
	{
		if (@event is InputEventMouseButton { Pressed: true })
			StartResize(DisplayServer.WindowResizeEdge.Right);
	}

	private void OnTopHandleGuiInput(InputEvent @event)
	{
		if (@event is InputEventMouseButton { Pressed: true })
			StartResize(DisplayServer.WindowResizeEdge.Top);
	}

	private void OnLeftHandleGuiInput(InputEvent @event)
	{
		if (@event is InputEventMouseButton { Pressed: true })
			StartResize(DisplayServer.WindowResizeEdge.Left);
	}

	private void OnBottomHandleGuiInput(InputEvent @event)
	{
		if (@event is InputEventMouseButton { Pressed: true })
			StartResize(DisplayServer.WindowResizeEdge.Bottom);
	}

	private void OnBottomRightHandleGuiInput(InputEvent @event)
	{
		if (@event is InputEventMouseButton { Pressed: true })
			StartResize(DisplayServer.WindowResizeEdge.BottomRight);
	}

	private void OnBottomLeftHandleGuiInput(InputEvent @event)
	{
		if (@event is InputEventMouseButton { Pressed: true })
			StartResize(DisplayServer.WindowResizeEdge.BottomLeft);
	}

	private void OnTopRightHandleGuiInput(InputEvent @event)
	{
		if (@event is InputEventMouseButton { Pressed: true })
			StartResize(DisplayServer.WindowResizeEdge.TopRight);
	}

	private void OnTopLeftHandleGuiInput(InputEvent @event)
	{
		if (@event is InputEventMouseButton { Pressed: true })
			StartResize(DisplayServer.WindowResizeEdge.TopLeft);
	}

	/// <summary>
	/// Begins an OS edge resize. Leaves OS-maximize without snapping to the pre-maximize rect.
	/// Does nothing while in chrome fullscreen (edges should not break fullscreen).
	/// </summary>
	private void StartResize(DisplayServer.WindowResizeEdge edge)
	{
		var window = GetWindow();
		if (window == null || !IsInstanceValid(window))
			return;

		if (IsChromeFullscreen(window))
			return;

		// Keep current (full) size so the edge drag continues naturally.
		LeaveFillScreen(window, applyRestoreRect: false);

		int windowId = window.GetWindowId();
		if (windowId >= 0)
			DisplayServer.WindowStartResize(edge, windowId);
	}

	/// <summary>
	/// Begins an OS window drag. Drag-out of OS maximize restores the pre-maximize rect.
	/// Fullscreen is left alone on press/click — use the expand button or double-click instead.
	/// </summary>
	private void StartDrag()
	{
		var window = GetWindow();
		if (window == null || !IsInstanceValid(window))
			return;

		// Single-click / drag must not exit fullscreen.
		if (IsChromeFullscreen(window))
			return;

		// OS maximize: desktop convention is drag title bar to pull out with restore rect.
		if (IsEffectivelyFillScreen(window))
			LeaveFillScreen(window, applyRestoreRect: true);

		int windowId = window.GetWindowId();
		if (windowId >= 0)
			DisplayServer.WindowStartDrag(windowId);
	}

	/// <summary>
	/// Toggle non-exclusive fullscreen from the expand button.
	/// Does not use ExclusiveFullscreen (avoids black flashes / exclusive display takeover).
	/// </summary>
	public void ToggleFullscreenFromChrome()
	{
		var window = GetWindow();
		if (window == null || !IsInstanceValid(window))
			return;

		if (IsEffectivelyFillScreen(window))
		{
			LeaveFillScreen(window, applyRestoreRect: true);
			return;
		}

		EnterFillScreen(window, FillMode.Fullscreen);
	}

	/// <summary>
	/// Toggle OS maximize from title double-click (never fullscreen).
	/// </summary>
	public void ToggleMaximizeFromChrome()
	{
		var window = GetWindow();
		if (window == null || !IsInstanceValid(window))
			return;

		if (IsEffectivelyFillScreen(window))
		{
			LeaveFillScreen(window, applyRestoreRect: true);
			return;
		}

		EnterFillScreen(window, FillMode.Maximized);
	}

	/// <summary>
	/// Snapshots the current rect and enters maximize or non-exclusive fullscreen.
	/// </summary>
	private void EnterFillScreen(Window window, FillMode mode)
	{
		if (mode == FillMode.None)
			return;

		_resizeSaveTimer.Stop();
		RememberRestoreRect(window.Size, window.Position);
		PersistWindowState(fill: false);

		_suppressGeometrySave = true;
		_fillMode = mode;
		SetWindowMode(window, mode == FillMode.Fullscreen
			? DisplayServer.WindowMode.Fullscreen
			: DisplayServer.WindowMode.Maximized);
		PersistWindowState(fill: true);
		CallDeferred(nameof(EndGeometrySaveSuppression));
	}

	/// <summary>
	/// Leaves maximize/fullscreen. Optionally restores the pre-fill rect (button / double-click / drag),
	/// or keeps the current size (edge-resize out of maximize).
	/// </summary>
	/// <param name="window">Target window.</param>
	/// <param name="applyRestoreRect">True to apply remembered pre-fill size/position.</param>
	private void LeaveFillScreen(Window window, bool applyRestoreRect)
	{
		if (!IsEffectivelyFillScreen(window))
			return;

		_resizeSaveTimer.Stop();
		_suppressGeometrySave = true;
		_fillMode = FillMode.None;

		SetWindowMode(window, DisplayServer.WindowMode.Windowed);

		if (applyRestoreRect && _hasRestoreRect)
		{
			ApplyGeometry(window, _restoreSize, _restorePosition);
			_lastKnownPosition = _restorePosition;
			// Borderless: engine often overwrites size on the next frame.
			CallDeferred(nameof(ReapplyRestoreRectDeferred));
		}
		else
		{
			// Free-size path (edge resize): current dimensions stay; next save captures them.
			_lastKnownPosition = window.Position;
			PersistWindowState(fill: false);
			CallDeferred(nameof(EndGeometrySaveSuppression));
		}
	}

	/// <summary>
	/// Re-applies the restore rect after the display server finishes leaving fill-screen mode.
	/// </summary>
	private void ReapplyRestoreRectDeferred()
	{
		var window = GetWindow();
		if (window != null && IsInstanceValid(window) && _hasRestoreRect)
		{
			if (UiUtilities.IsWindowFillScreen(window))
				SetWindowMode(window, DisplayServer.WindowMode.Windowed);

			ApplyGeometry(window, _restoreSize, _restorePosition);
			_lastKnownPosition = _restorePosition;
			PersistWindowState(fill: false);
		}

		_suppressGeometrySave = false;
	}

	private void EndGeometrySaveSuppression()
	{
		_suppressGeometrySave = false;
		CallDeferred(nameof(DemoteUnwantedExclusiveFullscreen));
	}

	/// <summary>
	/// Windows/Godot promotes a borderless window that exactly covers a monitor to
	/// ExclusiveFullscreen. Chrome still thinks the window is windowed (or OS-maximized),
	/// so resize/drag break. Same 1px / demote pattern as video output screens.
	/// </summary>
	/// <remarks>
	/// Expand-button fullscreen stays non-exclusive <see cref="DisplayServer.WindowMode.Fullscreen"/>.
	/// Header double-click maximize is unchanged; exclusive is never an intended main-window mode.
	/// </remarks>
	private void DemoteUnwantedExclusiveFullscreen()
	{
		if (_guardingExclusive)
			return;

		var window = GetWindow();
		if (window == null || !IsInstanceValid(window))
			return;

		bool exclusive = IsEngineExclusiveFullscreen(window);
		bool windowsExactCover = OS.GetName() == "Windows"
			&& _fillMode == FillMode.None
			&& IsExactOrNearMonitorCoverage(window);

		if (_fillMode == FillMode.Fullscreen)
		{
			if (exclusive)
				SetWindowMode(window, DisplayServer.WindowMode.Fullscreen);
			return;
		}

		if (!exclusive && !windowsExactCover)
			return;

		_guardingExclusive = true;
		_suppressGeometrySave = true;
		try
		{
			ForceMainBorderlessWindowed(window);
			NudgeOffExactMonitorCoverage(window);
			GD.Print(
				$"MainWindowHandles:DemoteUnwantedExclusiveFullscreen - exclusive={exclusive} " +
				$"fill={_fillMode} size={window.Size}");
		}
		finally
		{
			_guardingExclusive = false;
			_suppressGeometrySave = false;
		}
	}

	/// <summary>
	/// Proven Godot/Windows path out of ExclusiveFullscreen: briefly drop borderless, nudge size,
	/// set Windowed, restore borderless (see <c>VideoOutputDevice.ForceBorderlessWindowed</c>).
	/// </summary>
	private static void ForceMainBorderlessWindowed(Window window)
	{
		int id = window.GetWindowId();
		bool exclusive = IsEngineExclusiveFullscreen(window);

		if (exclusive)
		{
			if (id >= 0)
				DisplayServer.WindowSetFlag(DisplayServer.WindowFlags.Borderless, false, id);
			else
				window.Borderless = false;

			Vector2I s = id >= 0 ? DisplayServer.WindowGetSize(id) : window.Size;
			if (s.X > 1)
			{
				var nudged = new Vector2I(s.X - 1, Mathf.Max(1, s.Y));
				if (id >= 0)
					DisplayServer.WindowSetSize(nudged, id);
				else
					window.Size = nudged;
			}
		}

		SetWindowMode(window, DisplayServer.WindowMode.Windowed);

		if (id >= 0)
			DisplayServer.WindowSetFlag(DisplayServer.WindowFlags.Borderless, true, id);
		else
			window.Borderless = true;
	}

	/// <summary>
	/// +1px width when the frame matches a monitor, so Windows cannot re-promote to exclusive.
	/// Extra pixel hangs off the right edge (same as house-output placement).
	/// </summary>
	private static void NudgeOffExactMonitorCoverage(Window window)
	{
		if (!TryGetCoveredMonitor(window, out Vector2I monPos, out Vector2I monSize))
			return;

		ApplyGeometry(window, new Vector2I(monSize.X + 1, monSize.Y), monPos);
	}

	private static bool IsEngineExclusiveFullscreen(Window window)
	{
		int id = window.GetWindowId();
		if (id >= 0)
			return DisplayServer.WindowGetMode(id) == DisplayServer.WindowMode.ExclusiveFullscreen;
		return window.Mode == Window.ModeEnum.ExclusiveFullscreen;
	}

	/// <summary>
	/// True when the window is on a monitor origin and its size matches that monitor (or is 1px off).
	/// </summary>
	private static bool IsExactOrNearMonitorCoverage(Window window) =>
		TryGetCoveredMonitor(window, out _, out _);

	/// <summary>
	/// Finds a monitor this window fully covers (exact size, or ±1px width after a demote nudge).
	/// </summary>
	private static bool TryGetCoveredMonitor(Window window, out Vector2I monPos, out Vector2I monSize)
	{
		monPos = Vector2I.Zero;
		monSize = Vector2I.Zero;
		int id = window.GetWindowId();
		Vector2I pos = id >= 0 ? DisplayServer.WindowGetPosition(id) : window.Position;
		Vector2I size = id >= 0 ? DisplayServer.WindowGetSize(id) : window.Size;

		int screens = DisplayServer.GetScreenCount();
		for (int i = 0; i < screens; i++)
		{
			Vector2I p = DisplayServer.ScreenGetPosition(i);
			Vector2I s = DisplayServer.ScreenGetSize(i);
			if (pos != p)
				continue;
			if (size.Y != s.Y)
				continue;
			if (size.X == s.X || size.X == s.X - 1 || size.X == s.X + 1)
			{
				monPos = p;
				monSize = s;
				return size.X != s.X + 1;
			}
		}

		return false;
	}

	/// <summary>True when chrome or engine reports any fill-screen mode.</summary>
	private bool IsEffectivelyFillScreen(Window window) =>
		_fillMode != FillMode.None || UiUtilities.IsWindowFillScreen(window);

	/// <summary>True when chrome fullscreen (or engine fullscreen) is active.</summary>
	private bool IsChromeFullscreen(Window window)
	{
		if (_fillMode == FillMode.Fullscreen)
			return true;
		if (window == null || !IsInstanceValid(window))
			return false;
		return window.Mode is Window.ModeEnum.Fullscreen or Window.ModeEnum.ExclusiveFullscreen;
	}

	private void RememberRestoreRect(Vector2I size, Vector2I globalPosition)
	{
		if (size.X < MinWindowSize.X || size.Y < MinWindowSize.Y)
			return;

		_restoreSize = size;
		_restorePosition = globalPosition;
		_hasRestoreRect = true;
	}

	private static void SetWindowMode(Window window, DisplayServer.WindowMode mode)
	{
		int id = window.GetWindowId();
		if (id >= 0)
			DisplayServer.WindowSetMode(mode, id);
		else
			window.Mode = mode switch
			{
				DisplayServer.WindowMode.Maximized => Window.ModeEnum.Maximized,
				DisplayServer.WindowMode.Fullscreen => Window.ModeEnum.Fullscreen,
				DisplayServer.WindowMode.ExclusiveFullscreen => Window.ModeEnum.ExclusiveFullscreen,
				DisplayServer.WindowMode.Minimized => Window.ModeEnum.Minimized,
				_ => Window.ModeEnum.Windowed
			};
	}

	private static void ApplyGeometry(Window window, Vector2I size, Vector2I globalPosition)
	{
		int id = window.GetWindowId();
		if (id >= 0)
		{
			if (size.X > 0 && size.Y > 0)
				DisplayServer.WindowSetSize(size, id);
			DisplayServer.WindowSetPosition(globalPosition, id);
			return;
		}

		if (size.X > 0 && size.Y > 0)
			window.Size = size;
		window.Position = globalPosition;
	}

	/// <summary>
	/// Persists restore rect (when known) and fill-screen flags. Uses session restore fields while
	/// fill-screen so transitional sizes never overwrite the pre-fill rect.
	/// </summary>
	/// <param name="fill">True when currently maximized or fullscreen.</param>
	private void PersistWindowState(bool fill)
	{
		Vector2I size;
		Vector2I relPos;

		if (fill && _hasRestoreRect)
		{
			size = _restoreSize;
			relPos = UiUtilities.ToScreenRelativePosition(_restorePosition, size);
		}
		else
		{
			var win = GetWindow();
			if (win == null || !IsInstanceValid(win))
				return;

			size = win.Size;
			relPos = UiUtilities.ToScreenRelativePosition(win.Position, size);

			if (!fill && size.X >= MinWindowSize.X && size.Y >= MinWindowSize.Y)
				RememberRestoreRect(size, win.Position);
		}

		bool fullscreen = fill && _fillMode == FillMode.Fullscreen;
		// SetWindowState only writes size/position when not fill-screen.
		_globalData?.UserDataManager?.SetWindowState(size, relPos, fill, fullscreen);
	}

	/// <inheritdoc />
	public override void _Process(double delta)
	{
		if (!_isFading || _borderStylebox == null)
		{
			SetProcess(false);
			return;
		}

		_fadeProgress += (float)delta / 1.0f;
		if (_fadeProgress >= 1.0f)
		{
			_fadeProgress = 1.0f;
			_isFading = false;
			SetProcess(false);
		}

		_borderStylebox.BorderColor = _highlightColor.Lerp(_restBorderColor, _fadeProgress);
	}

	private void OnWindowSizeChanged()
	{
		if (_guardingExclusive)
			return;

		if (!_suppressGeometrySave)
			CallDeferred(nameof(DemoteUnwantedExclusiveFullscreen));

		if (_suppressGeometrySave)
			return;

		var win = GetWindow();
		// Unwanted exclusive is not a real maximize/fullscreen — wait for the demote.
		if (win != null && IsEngineExclusiveFullscreen(win) && _fillMode != FillMode.Fullscreen)
			return;

		if (IsEffectivelyFillScreen(win))
		{
			PersistWindowState(fill: true);
			return;
		}

		_resizeSaveTimer.Start();
	}

	/// <summary>
	/// Saves the current window size, relative position, and fill-screen state.
	/// </summary>
	private void SaveCurrentWindowState()
	{
		if (_suppressGeometrySave)
			return;

		var win = GetWindow();
		if (win == null || !IsInstanceValid(win))
			return;

		PersistWindowState(IsEffectivelyFillScreen(win));
	}

	private void CheckAndDebounceWindowPosition()
	{
		if (_suppressGeometrySave || _guardingExclusive)
			return;

		var win = GetWindow();
		if (win != null && IsInstanceValid(win) && IsEngineExclusiveFullscreen(win)
		    && _fillMode != FillMode.Fullscreen)
		{
			DemoteUnwantedExclusiveFullscreen();
			return;
		}

		if (IsEffectivelyFillScreen(win))
			return;

		Vector2I currentPos = win.Position;
		if (currentPos != _lastKnownPosition)
		{
			_lastKnownPosition = currentPos;
			_resizeSaveTimer.Start();
		}
	}

	/// <inheritdoc />
	public override void _ExitTree()
	{
		var window = GetWindow();
		if (window != null && IsInstanceValid(window))
			window.SizeChanged -= OnWindowSizeChanged;

		if (_headerHandle != null) _headerHandle.GuiInput -= OnHeaderHandleGuiInput;
		if (_rightHandle != null) _rightHandle.GuiInput -= OnRightHandleGuiInput;
		if (_topHandle != null) _topHandle.GuiInput -= OnTopHandleGuiInput;
		if (_leftHandle != null) _leftHandle.GuiInput -= OnLeftHandleGuiInput;
		if (_bottomHandle != null) _bottomHandle.GuiInput -= OnBottomHandleGuiInput;
		if (_bottomRightHandle != null) _bottomRightHandle.GuiInput -= OnBottomRightHandleGuiInput;
		if (_bottomLeftHandle != null) _bottomLeftHandle.GuiInput -= OnBottomLeftHandleGuiInput;
		if (_topRightHandle != null) _topRightHandle.GuiInput -= OnTopRightHandleGuiInput;
		if (_topLeftHandle != null) _topLeftHandle.GuiInput -= OnTopLeftHandleGuiInput;

		if (_globalSignals != null)
		{
			_globalSignals.LogAlert -= OnAlertReceived;
			_globalSignals.ShowModeChanged -= OnShowModeChanged;
		}

		_resizeSaveTimer?.Stop();
		if (_positionPollTimer != null)
		{
			_positionPollTimer.Stop();
			_positionPollTimer.Timeout -= CheckAndDebounceWindowPosition;
		}
		SaveCurrentWindowState();
		var border = GetNodeOrNull<Panel>("%Border");
		if (border != null && IsInstanceValid(border))
			border.RemoveThemeStyleboxOverride("panel");
		UiUtilities.DisposeRefCounted(_borderStylebox);
		_borderStylebox = null;
	}
}
