// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System.IO;
using Godot;
using Cue2.Services;
using Cue2.UI.Shell;
using Cue2.UI.Utilities;
using Cue2.UI.Windows;

namespace Cue2.App;

/// <summary>
/// Root workspace control: layout, UI scale, and show-mode inspector visibility.
/// </summary>
public partial class Cue2Base : Control
{
	/// <summary>
	/// When true, the first-time welcome window is shown every launch (debug only).
	/// Production keeps this false so only <see cref="UserDataManager.IsFirstTimeStartup"/> controls it.
	/// </summary>
	private const bool ForceFirstTimeStartupEveryLaunch = false;

	private const string FirstTimeStartupScenePath = "res://src/UI/Windows/FirstTimeStartupWindow.tscn";

	private GlobalSignals _globalSignals;
	private GlobalData _globalData;

	private Node _settingsWindow;

	//private Window _uiWindow;
	private Window VideoWindow;
	private int _playbackIndex;

	/// <summary>Bottom inspector row (Shell Inspector + InspectorTabs). Hidden in Show Mode.</summary>
	private Control _inspectorSplit;

	/// <summary>Active first-time welcome window, if any.</summary>
	private FirstTimeStartupWindow _firstTimeStartupWindow;

	/// <summary>Workspace overlay shown while the startup (or later) showfile apply is in progress.</summary>
	private SessionLoadOverlay _sessionLoadOverlay;

	public WorkspaceStates State { get; set; }

	//public GlobalMediaPlayerManager mediaManager;

	public override void _Ready()
	{
		//Connect global signals
		_globalSignals = GetNode<GlobalSignals>("/root/GlobalSignals");
		_globalData = GetNode<Cue2.Services.GlobalData>("/root/GlobalData");

		_globalSignals.UiScaleChanged += ScaleUI;
		_globalSignals.ShowModeChanged += OnShowModeChanged;

		// Prefer unique name; fall back to path for older scene instances.
		_inspectorSplit = GetNodeOrNull<Control>("%HSplitContainer2")
			?? GetNodeOrNull<Control>("MarginContainer/BoxContainer/VSplitContainer/HSplitContainer2");

		ApplyShowModeUi(_globalData?.Settings?.ShowMode == true);

		GetTree().AutoAcceptQuit = false;
		var mainWindow = GetWindow();
		if (mainWindow != null)
			mainWindow.CloseRequested += OnMainWindowCloseRequested;

		_sessionLoadOverlay = GetNodeOrNull<SessionLoadOverlay>("%SessionLoadOverlay");
		MaybeShowStartupLoadOverlay();

		// Initial window size/position/scale order is owned by MainWindowHandles:
		// content scale first, then restore saved geometry (or first-run size from
		// DetectDisplayScale + usable-screen fit). Do not call RescaleWindow here.

		// Deferred so main window geometry/chrome settles before a modal-style sub-window opens.
		CallDeferred(nameof(MaybeShowFirstTimeStartup));
		CallDeferred(nameof(ScheduleStartupUpdateCheck));
	}

	/// <summary>
	/// Runs a quiet GitHub update check after welcome/session load so it never blocks GO or first paint.
	/// </summary>
	private void ScheduleStartupUpdateCheck()
	{
		var timer = new Timer { OneShot = true, WaitTime = 8.0 };
		timer.Timeout += OnStartupUpdateCheckTimeout;
		AddChild(timer);
		timer.Start();
	}

	private void OnStartupUpdateCheckTimeout()
	{
		var udm = _globalData?.UserDataManager;
		if (udm == null)
			return;
		if (udm.IsFirstTimeStartup)
			return;
		if (!udm.CheckForUpdatesOnStartup)
			return;
		if (_firstTimeStartupWindow != null && GodotObject.IsInstanceValid(_firstTimeStartupWindow))
			return;

		_globalData.UpdateService?.CheckForUpdates(force: false);
	}

	/// <summary>
	/// Covers the workspace before the deferred last-show open starts so the grey empty
	/// cuelist is never the first painted frame after the engine splash.
	/// </summary>
	private void MaybeShowStartupLoadOverlay()
	{
		string path = _globalData?.StartupOpenPath;
		if (string.IsNullOrEmpty(path))
			return;

		string name = Path.GetFileNameWithoutExtension(path);
		_sessionLoadOverlay?.ShowOpening(name);
	}

	/// <summary>
	/// Opens the first-time welcome sub-window when appropriate.
	/// Currently forced every launch for testing; production will rely on the user-data flag only.
	/// </summary>
	private void MaybeShowFirstTimeStartup()
	{
		var udm = _globalData?.UserDataManager;
		if (udm == null)
			return;

		bool shouldShow = ForceFirstTimeStartupEveryLaunch || udm.IsFirstTimeStartup;
		if (!shouldShow)
			return;

		if (_firstTimeStartupWindow != null && GodotObject.IsInstanceValid(_firstTimeStartupWindow))
		{
			_firstTimeStartupWindow.Show();
			_firstTimeStartupWindow.GrabFocus();
			return;
		}

		var node = SceneLoader.LoadScene(FirstTimeStartupScenePath, out string error);
		if (node == null)
		{
			GD.PrintErr($"Cue2Base:MaybeShowFirstTimeStartup - Failed to load welcome window: {error}");
			_globalSignals?.EmitSignal(nameof(GlobalSignals.Log),
				$"Failed to open first-time startup window: {error}", 2);
			return;
		}

		if (node is not FirstTimeStartupWindow window)
		{
			GD.PrintErr("Cue2Base:MaybeShowFirstTimeStartup - Scene root is not FirstTimeStartupWindow.");
			node.QueueFree();
			return;
		}

		_firstTimeStartupWindow = window;
		_firstTimeStartupWindow.TreeExiting += OnFirstTimeStartupWindowExiting;
		AddChild(_firstTimeStartupWindow);
		GD.Print("Cue2Base:MaybeShowFirstTimeStartup - First-time startup window opened.");
	}

	/// <summary>
	/// Clears the welcome window reference when it is freed.
	/// </summary>
	private void OnFirstTimeStartupWindowExiting()
	{
		if (_firstTimeStartupWindow != null)
			_firstTimeStartupWindow.TreeExiting -= OnFirstTimeStartupWindowExiting;
		_firstTimeStartupWindow = null;
	}

	private void ScaleUI(float uiScale)
	{
		// Runtime scale changes only touch ContentScaleFactor, not outer pixel size.
		var window = GetWindow();
		float userScale = _globalData.UserDataManager?.UiScale ?? Cue2.Services.UserDataManager.DefaultUiScale;
		UiUtilities.RescaleUi(window, userScale, _globalData.BaseDisplayScale);
		// Keep explicit geometry; do not let WrapControls auto-resize the main frame.
		if (window != null)
			window.WrapControls = false;
	}

	/// <summary>
	/// Hides or shows the inspector strip when entering/leaving Show Mode.
	/// </summary>
	private void OnShowModeChanged(bool enabled)
	{
		ApplyShowModeUi(enabled);
	}

	/// <summary>
	/// In Show Mode the inspector row is hidden so the cuelist uses full workspace height.
	/// </summary>
	/// <param name="showMode">True when Show Mode is active.</param>
	private void ApplyShowModeUi(bool showMode)
	{
		if (_inspectorSplit != null && IsInstanceValid(_inspectorSplit))
			_inspectorSplit.Visible = !showMode;
	}

	private void OnMainWindowCloseRequested()
	{
		GetNodeOrNull<SaveManager>("/root/SaveManager")?.RequestQuit();
	}

	public override void _ExitTree()
	{
		var mainWindow = GetWindow();
		if (mainWindow != null && GodotObject.IsInstanceValid(mainWindow))
			mainWindow.CloseRequested -= OnMainWindowCloseRequested;

		if (_firstTimeStartupWindow != null && GodotObject.IsInstanceValid(_firstTimeStartupWindow))
			_firstTimeStartupWindow.TreeExiting -= OnFirstTimeStartupWindowExiting;

		if (_globalSignals != null)
		{
			_globalSignals.UiScaleChanged -= ScaleUI;
			_globalSignals.ShowModeChanged -= OnShowModeChanged;
		}
	}
}
