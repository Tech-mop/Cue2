// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using Godot;
using Cue2.Services;
using Cue2.UI.Settings;
using Cue2.UI.Utilities;

namespace Cue2.UI.Windows;

/// <summary>
/// Shared title-bar drag and edge-resize handles for borderless sub-windows
/// (Settings, Log, About, etc.).
/// </summary>
/// <remarks>
/// Window IDs are resolved at interaction time. Sub-windows often start hidden
/// (to apply size/position without flicker); capturing <see cref="Window.GetWindowId"/>
/// in <c>_Ready</c> can yield an ID that DisplayServer does not yet track, which
/// breaks <see cref="DisplayServer.WindowStartDrag"/> / resize.
/// <para/>
/// Interactive resize/drag must leave maximized/fullscreen first. Starting an OS resize
/// while still maximized leaves a hybrid "maximized but not full-screen" state and breaks layout.
/// </remarks>
public partial class SubWindowHandles : Control
{
	private Control _headerHandle;
	private Control _rightHandle;
	private Control _leftHandle;
	private Control _bottomHandle;
	private Control _bottomRightHandle;
	private Control _bottomLeftHandle;
	private Control _topRightHandle;
	private Control _topLeftHandle;
	private GlobalSignals _globalSignals;
	private StyleBoxFlat _borderStylebox;

	/// <summary>
	/// Wires handle input. Does not cache a DisplayServer window id — see class remarks.
	/// </summary>
	public override void _Ready()
	{
		_headerHandle = GetNode<Control>("%HeaderHandle");
		_rightHandle = GetNode<Control>("%RightHandle");
		_leftHandle = GetNode<Control>("%LeftHandle");
		_bottomHandle = GetNode<Control>("%BottomHandle");
		_bottomRightHandle = GetNode<Control>("%BottomRightCornerHandle");
		_bottomLeftHandle = GetNode<Control>("%BottomLeftCornerHandle");
		_topRightHandle = GetNode<Control>("%TopRightCornerHandle");
		_topLeftHandle = GetNode<Control>("%TopLeftCornerHandle");

		_headerHandle.GuiInput += OnHeaderHandleGuiInput;
		_rightHandle.GuiInput += OnRightHandleGuiInput;
		_leftHandle.GuiInput += OnLeftHandleGuiInput;
		_bottomHandle.GuiInput += OnBottomHandleGuiInput;
		_topRightHandle.GuiInput += OnTopRightHandleGuiInput;
		_bottomLeftHandle.GuiInput += OnBottomLeftHandleGuiInput;
		_topLeftHandle.GuiInput += OnTopLeftHandleGuiInput;
		_bottomRightHandle.GuiInput += OnBottomRightHandleGuiInput;

		GetNode<Button>("%ExitButton").Pressed += OnExitButtonPressed;

		_globalSignals = GetNodeOrNull<GlobalSignals>("/root/GlobalSignals");
		var border = GetNodeOrNull<Panel>("%Border");
		if (border?.GetThemeStylebox("panel") is StyleBoxFlat style)
		{
			_borderStylebox = (StyleBoxFlat)style.Duplicate();
			border.AddThemeStyleboxOverride("panel", _borderStylebox);
			ApplyAccentBorder();
		}

		if (_globalSignals != null)
			_globalSignals.ThemeColorsChanged += OnThemeColorsChanged;
	}

	/// <summary>
	/// Re-tints the window outline after Cue2 Preferences high/low accents change.
	/// </summary>
	private void OnThemeColorsChanged()
	{
		ApplyAccentBorder();
	}

	/// <summary>
	/// Sets the duplicated border StyleBox to the deepest high cascade step.
	/// </summary>
	private void ApplyAccentBorder()
	{
		if (_borderStylebox == null)
			return;
		_borderStylebox.BorderColor = GlobalStyles.HighColor5;
	}

	/// <summary>
	/// Returns the host <see cref="Window"/> node, preferring the direct parent.
	/// </summary>
	private Window ResolveHostWindow()
	{
		Window window = GetParent() as Window ?? GetWindow();
		if (window == null || !IsInstanceValid(window))
			return null;
		return window;
	}

	/// <summary>
	/// Returns the host <see cref="Window"/>'s DisplayServer id, or -1 if unavailable.
	/// Prefers the parent Window (this control is always a direct child of the borderless window).
	/// </summary>
	private int ResolveWindowId()
	{
		Window window = ResolveHostWindow();
		if (window == null)
			return -1;

		// Hidden windows may not be registered with DisplayServer yet.
		if (!window.Visible)
			return -1;

		return window.GetWindowId();
	}

	private void OnExitButtonPressed()
	{
		GetParent().QueueFree();
	}

	private void OnHeaderHandleGuiInput(InputEvent @event)
	{
		// Sub-windows intentionally do not maximize/fullscreen on header double-click
		// (main window chrome still does via MainWindowHandles).
		if (@event is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left })
			StartDrag();
	}

	private void OnRightHandleGuiInput(InputEvent @event)
	{
		if (@event is InputEventMouseButton { Pressed: true })
			StartResize(DisplayServer.WindowResizeEdge.Right);
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
	/// Begins an OS edge resize after ensuring the window is not maximized/fullscreen.
	/// </summary>
	private void StartResize(DisplayServer.WindowResizeEdge edge)
	{
		var window = ResolveHostWindow();
		if (window == null)
			return;

		// Settings stores last normal size; other sub-windows rely on Godot's restore rect.
		RestoreWindowedIfNeeded(window);

		int windowId = ResolveWindowId();
		if (windowId >= 0)
			DisplayServer.WindowStartResize(edge, windowId);
	}

	/// <summary>
	/// Begins an OS window drag after ensuring the window is not maximized/fullscreen.
	/// </summary>
	private void StartDrag()
	{
		var window = ResolveHostWindow();
		if (window == null)
			return;

		RestoreWindowedIfNeeded(window);

		int windowId = ResolveWindowId();
		if (windowId >= 0)
			DisplayServer.WindowStartDrag(windowId);
	}

	/// <summary>
	/// Leaves maximized/fullscreen before interactive drag/resize.
	/// Settings windows also re-apply the last cached normal size when available.
	/// </summary>
	private static void RestoreWindowedIfNeeded(Window window)
	{
		if (!UiUtilities.IsWindowFillScreen(window))
			return;

		// Settings keeps an authoritative session size — restore that explicitly.
		if (window is SettingsWindow settings)
		{
			settings.RestoreNormalGeometryForInteraction();
			return;
		}

		// Other sub-windows: leave fill-screen and rely on Godot's restore rect.
		UiUtilities.EnsureWindowedForInteraction(window);
	}

	public override void _ExitTree()
	{
		if (_headerHandle != null) _headerHandle.GuiInput -= OnHeaderHandleGuiInput;
		if (_rightHandle != null) _rightHandle.GuiInput -= OnRightHandleGuiInput;
		if (_leftHandle != null) _leftHandle.GuiInput -= OnLeftHandleGuiInput;
		if (_bottomHandle != null) _bottomHandle.GuiInput -= OnBottomHandleGuiInput;
		if (_bottomRightHandle != null) _bottomRightHandle.GuiInput -= OnBottomRightHandleGuiInput;
		if (_bottomLeftHandle != null) _bottomLeftHandle.GuiInput -= OnBottomLeftHandleGuiInput;
		if (_topRightHandle != null) _topRightHandle.GuiInput -= OnTopRightHandleGuiInput;
		if (_topLeftHandle != null) _topLeftHandle.GuiInput -= OnTopLeftHandleGuiInput;
		if (_globalSignals != null)
			_globalSignals.ThemeColorsChanged -= OnThemeColorsChanged;
	}
}
