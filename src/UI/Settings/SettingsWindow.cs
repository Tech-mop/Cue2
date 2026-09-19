// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

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
using Cue2.UI.Utilities;
using Godot;
using static Cue2.UI.Utilities.UiLocalizer;

namespace Cue2.UI.Settings;

/// <summary>
/// Borderless Settings window. Geometry and last sub-menu are loaded once from user data at
/// the first open of a session, then kept in a process-level session cache for every
/// subsequent open/close. Disk is only updated when <see cref="UserDataManager"/> persists
/// (typically app exit).
/// </summary>
/// <remarks>
/// Also hosts independent show-settings store/recall (.c2settings) with a multi-select filter
/// so users can export or import subsets (e.g. Audio Output Patches only).
/// </remarks>
public partial class SettingsWindow : Window
{
	private GlobalSignals _globalSignals;
	private GlobalData _globalData;
	private Godot.Tree _setTree;
	private string _currentDisplay = "";

	/// <summary>
	/// Floor size for interactive resize. Canvas editor side panels may compress below preferred widths.
	/// </summary>
	private static readonly Vector2I MinWindowSize = new Vector2I(500, 350);

	/// <summary>
	/// First-open default: wide enough for Canvas Editor (structure + stage + properties) without a cramped layout.
	/// </summary>
	private static readonly Vector2I DefaultWindowSize = new Vector2I(1100, 700);

	private const string DefaultMenuKey = "General";

	// Session cache: seeded once from UserDataManager (file load at app start), then authoritative
	// for all later Settings opens until the process ends.
	private static bool _sessionStateInitialized;
	private static Vector2I _sessionSize = Vector2I.Zero;
	private static Vector2I _sessionRelativePosition = Vector2I.Zero;
	private static bool _sessionMaximized;
	/// <summary>Tree item label, e.g. "Canvas Editor".</summary>
	private static string _sessionMenuKey = DefaultMenuKey;

	// Debounce timer for window size/position saves (only update cache after resize/move settles)
	private Timer _resizeSaveTimer;
	private Timer _positionPollTimer;
	private Vector2I _lastKnownPosition;

	// ── Settings file filter / save-load ───────────────────────────────────
	private Button _filterButton;
	private PanelContainer _filterMenu;
	private VBoxContainer _filterContainer;
	private CheckBox _filterAllCheckBox;
	private readonly System.Collections.Generic.Dictionary<string, CheckBox> _filterCheckBoxes = new();
	private bool _isUpdatingFilterUi;
	private FileDialog _settingsFileDialog;

	/// <summary>
	/// Applies Linux native-window policy before the scene window enters the tree.
	/// </summary>
	public SettingsWindow()
	{
		LinuxWindowEmbedPolicy.ApplyToAppWindow(this);
	}

	/// <summary>
	/// Initializes the settings UI, restores last window geometry and sub-menu, and wires signals.
	/// Stays hidden until geometry is applied so the default scene size never flashes on screen.
	/// </summary>
	public override void _Ready()
	{
		// Scene starts visible=false; keep hidden until final size/position are set.
		Visible = false;

		_globalSignals = GetNode<GlobalSignals>("/root/GlobalSignals");
		_globalData = GetNode<GlobalData>("/root/GlobalData");

		float userScale = _globalData.UserDataManager?.UiScale ?? UserDataManager.DefaultUiScale;
		GD.Print($"SettingsWindow:_Ready - UI Scale: " + userScale);

		MinSize = MinWindowSize;

		// Seed session state first so we can apply the correct size in one step (no intermediate resize).
		EnsureSessionStateSeeded();

		// Content scale only — does not change outer window pixel size.
		UiUtilities.RescaleUi(this, userScale, _globalData.BaseDisplayScale);

		// Prefer session/cached geometry. First run uses a canvas-editor-friendly default, then DPI scale.
		if (!TryRestoreWindowState())
		{
			Size = DefaultWindowSize;
			UiUtilities.RescaleWindow(this, _globalData.BaseDisplayScale);
		}

		_resizeSaveTimer = new Timer { OneShot = true, WaitTime = 0.25f };
		_resizeSaveTimer.Timeout += SaveCurrentWindowState;
		AddChild(_resizeSaveTimer);

		_positionPollTimer = new Timer { OneShot = false, WaitTime = 0.25f, Autostart = true };
		_positionPollTimer.Timeout += PollWindowPosition;
		AddChild(_positionPollTimer);

		_lastKnownPosition = Position;
		SizeChanged += OnWindowSizeChanged;
		SetProcess(false);

		_generateTree();
		RestoreSelectedMenu();
		_connectSignals();
		SetupSettingsFileFilterUi();
		LocalizeTree(this);
		if (_setTree?.GetRoot() != null)
			RelocalizeTreeItems(_setTree.GetRoot());
		if (_globalSignals != null)
			_globalSignals.LocaleChanged += OnLocaleChanged;

		// Reveal only after size, position, scale, and menu are ready — avoids open-time flicker.
		Show();

		// _Input is only for closing the filter popup; keep it off otherwise so slider
		// drags do not wrap every mouse motion in C# (shutdown InputEvent leaks).
		SetProcessInput(false);
	}

	/// <summary>
	/// Re-localizes settings chrome, tree labels, and the visible panel when the UI language changes.
	/// </summary>
	/// <param name="localeCode">New locale code.</param>
	private void OnLocaleChanged(string localeCode)
	{
		if (!GodotObject.IsInstanceValid(this))
			return;
		LocalizeTree(this);
		if (_setTree?.GetRoot() != null)
			RelocalizeTreeItems(_setTree.GetRoot());
		// Refresh active panel chrome (labels/tooltips) without changing selection.
		if (!string.IsNullOrEmpty(_currentDisplay))
		{
			var panel = GetNodeOrNull<Control>("%" + _currentDisplay);
			if (panel != null)
				LocalizeTree(panel);
		}
		// Filter checkboxes use the same English tree names as translation keys.
		UpdateFilterButtonLabel();
	}

	/// <summary>
	/// On the first Settings open of the process, copies geometry and last menu from
	/// UserDataManager (already loaded from user:// at app startup). Later opens skip file access.
	/// </summary>
	private void EnsureSessionStateSeeded()
	{
		if (_sessionStateInitialized)
		{
			return;
		}

		var udm = _globalData?.UserDataManager;
		if (udm != null)
		{
			_sessionSize = udm.LastSettingsWindowSize;
			_sessionRelativePosition = udm.LastSettingsWindowPosition;
			_sessionMaximized = udm.SettingsWasMaximized;
			if (!string.IsNullOrWhiteSpace(udm.LastSettingsMenu))
			{
				_sessionMenuKey = udm.LastSettingsMenu;
			}
		}

		_sessionStateInitialized = true;
	}

	/// <summary>
	/// Restores size, relative position, and maximized state from the session cache.
	/// Position is applied relative to the display that currently contains the mouse cursor.
	/// </summary>
	/// <returns>True when a valid session size (or maximized state) was applied.</returns>
	private bool TryRestoreWindowState()
	{
		if (_sessionMaximized)
		{
			// Apply last normal size first so un-maximize has a sensible restore rect.
			if (_sessionSize.X >= MinWindowSize.X && _sessionSize.Y >= MinWindowSize.Y)
			{
				Size = _sessionSize;
				ApplySessionRelativePosition();
			}
			Mode = ModeEnum.Maximized;
			return true;
		}

		if (_sessionSize.X < MinWindowSize.X || _sessionSize.Y < MinWindowSize.Y)
		{
			return false;
		}

		Size = _sessionSize;
		ApplySessionRelativePosition();
		return true;
	}

	/// <summary>
	/// Places the window on the display under the mouse using the cached relative position.
	/// </summary>
	private void ApplySessionRelativePosition()
	{
		int targetScreenIdx = UiUtilities.FindScreenAtPoint(DisplayServer.MouseGetPosition());
		Position = UiUtilities.ClampWindowPositionToScreen(targetScreenIdx, _sessionRelativePosition);
	}

	private void OnWindowSizeChanged()
	{
		if (UiUtilities.IsWindowFillScreen(this))
		{
			// Persist maximized flag without overwriting last normal size.
			SaveCurrentWindowState();
			return;
		}

		_resizeSaveTimer?.Start();
	}

	/// <summary>
	/// Flushes pending geometry so maximize does not lose the latest windowed size
	/// (debounce timer may not have fired yet). Only writes size when currently windowed.
	/// </summary>
	public void FlushGeometryBeforeModeChange()
	{
		_resizeSaveTimer?.Stop();
		if (!UiUtilities.IsWindowFillScreen(this))
			SaveCurrentWindowState();
	}

	/// <summary>
	/// Stops debounce and persists current size/position/mode (including maximized flag).
	/// </summary>
	public void PersistGeometryNow()
	{
		_resizeSaveTimer?.Stop();
		SaveCurrentWindowState();
	}

	/// <summary>
	/// Leaves maximized/fullscreen and re-applies the session-cached normal size/position.
	/// Called by <see cref="SubWindows.SubWindowHandles"/> before interactive drag/resize.
	/// </summary>
	public void RestoreNormalGeometryForInteraction()
	{
		if (!UiUtilities.IsWindowFillScreen(this))
			return;

		Mode = ModeEnum.Windowed;

		if (_sessionSize.X >= MinWindowSize.X && _sessionSize.Y >= MinWindowSize.Y)
		{
			Size = _sessionSize;
			int screen = CurrentScreen;
			if (screen < 0)
				screen = UiUtilities.FindScreenAtPoint(DisplayServer.MouseGetPosition());
			Position = UiUtilities.ClampWindowPositionToScreen(screen, _sessionRelativePosition);
		}

		SaveCurrentWindowState();
		_lastKnownPosition = Position;
	}

	/// <summary>
	/// Debounces position changes during drag (4 Hz). Avoids a per-frame <c>_Process</c> poll.
	/// </summary>
	private void PollWindowPosition()
	{
		if (UiUtilities.IsWindowFillScreen(this))
			return;

		Vector2I currentPos = Position;
		if (currentPos != _lastKnownPosition)
		{
			_lastKnownPosition = currentPos;
			_resizeSaveTimer?.Start();
		}
	}

	/// <summary>
	/// Updates the session cache (and UserDataManager in-memory fields) with current geometry.
	/// Does not write the external user data file.
	/// </summary>
	private void SaveCurrentWindowState()
	{
		bool isMax = UiUtilities.IsWindowFillScreen(this);
		Vector2I size = Size;
		Vector2I globalPos = Position;
		Vector2I relPos = isMax
			? globalPos
			: UiUtilities.ToScreenRelativePosition(globalPos, size);

		// Session cache is the source of truth for re-opens within this process.
		_sessionMaximized = isMax;
		if (!isMax)
		{
			if (size.X > 0 && size.Y > 0)
				_sessionSize = size;
			_sessionRelativePosition = relPos;
		}
		_sessionStateInitialized = true;

		// Mirror into UserDataManager so app-exit can persist to disk once.
		_globalData?.UserDataManager?.SetSettingsWindowState(size, relPos, isMax);
	}

	/// <summary>
	/// Remembers the active Settings sub-menu in the session cache and UserDataManager memory.
	/// </summary>
	/// <param name="menuKey">Tree item label (e.g. "Canvas Editor").</param>
	private void SaveSelectedMenu(string menuKey)
	{
		if (string.IsNullOrWhiteSpace(menuKey) || !TryGetMenuNode(menuKey, out _))
		{
			return;
		}

		_sessionMenuKey = menuKey;
		_sessionStateInitialized = true;
		_globalData?.UserDataManager?.SetSettingsMenu(menuKey);
	}

	/// <summary>
	/// Selects the tree item for the session-cached menu and shows its panel.
	/// Falls back to General if the key is unknown or the item is missing.
	/// </summary>
	private void RestoreSelectedMenu()
	{
		if (_setTree == null)
		{
			return;
		}

		string menuKey = _sessionMenuKey;
		if (string.IsNullOrWhiteSpace(menuKey) || !TryGetMenuNode(menuKey, out _))
		{
			menuKey = DefaultMenuKey;
		}

		TreeItem root = _setTree.GetRoot();
		TreeItem item = FindTreeItemByText(root, menuKey);
		if (item == null && menuKey != DefaultMenuKey)
		{
			menuKey = DefaultMenuKey;
			item = FindTreeItemByText(root, menuKey);
		}

		if (item == null)
		{
			return;
		}

		// Expand ancestors so the selection is visible
		TreeItem parent = item.GetParent();
		while (parent != null && parent != root)
		{
			parent.Collapsed = false;
			parent = parent.GetParent();
		}

		item.Select(0);
		// Select does not always emit item_selected when set programmatically — show panel explicitly.
		ShowMenuForTreeLabel(menuKey);
		_setTree.ScrollToItem(item);
	}

	/// <summary>
	/// Depth-first search for a tree item whose column-0 text matches <paramref name="text"/>.
	/// </summary>
	private static TreeItem FindTreeItemByText(TreeItem item, string text)
	{
		if (item == null || string.IsNullOrEmpty(text))
		{
			return null;
		}

		if (GetTreeItemMenuKey(item) == text || item.GetText(0) == text)
		{
			return item;
		}

		TreeItem child = item.GetFirstChild();
		while (child != null)
		{
			TreeItem found = FindTreeItemByText(child, text);
			if (found != null)
			{
				return found;
			}

			child = child.GetNext();
		}

		return null;
	}

	private void _connectSignals()
	{
		_globalSignals.UiScaleChanged += ScaleUi;

		_filterButton = GetNodeOrNull<Button>("%SaveFilterOptionButton");
		_filterMenu = GetNodeOrNull<PanelContainer>("%DropMenuFilter");
		if (_filterButton != null)
			_filterButton.Pressed += ToggleFilterMenu;

		var saveAs = GetNodeOrNull<Button>("%SettingsSaveAs");
		if (saveAs != null)
			saveAs.Pressed += OnSaveSettingsPressed;

		var load = GetNodeOrNull<Button>("%SettingsLoad");
		if (load != null)
			load.Pressed += OnLoadSettingsPressed;
	}

	// ──────────────────────────────────────────────────────────────────────
	// Settings file filter UI
	// ──────────────────────────────────────────────────────────────────────

	/// <summary>
	/// Builds per-category checkboxes under the filter dropdown (All is in the scene).
	/// Default selection is All / every category.
	/// </summary>
	private void SetupSettingsFileFilterUi()
	{
		_filterContainer = GetNodeOrNull<VBoxContainer>("%dmSaveFilterContainer");
		_filterAllCheckBox = GetNodeOrNull<CheckBox>("%FilterAllCheckBox");
		if (_filterContainer == null || _filterAllCheckBox == null)
		{
			GD.PrintErr("SettingsWindow:SetupSettingsFileFilterUi - Filter UI nodes missing.");
			return;
		}

		_filterAllCheckBox.Toggled += OnFilterAllToggled;

		foreach (var cat in SettingsExport.Categories)
		{
			var box = new CheckBox
			{
				Text = cat.Label,
				ButtonPressed = true,
				FocusMode = Control.FocusModeEnum.None,
				MouseFilter = Control.MouseFilterEnum.Stop
			};
			box.SetMeta("category_id", cat.Id);
			box.Toggled += pressed => OnFilterCategoryToggled(cat.Id, pressed);
			_filterContainer.AddChild(box);
			_filterCheckBoxes[cat.Id] = box;
		}

		UpdateFilterButtonLabel();
	}

	private void ToggleFilterMenu()
	{
		if (_filterMenu == null || _filterButton == null)
			return;

		if (_filterMenu.Visible)
		{
			HideFilterMenu();
			return;
		}

		// Position the popup above the filter button so it sits over the left column.
		var btnRect = _filterButton.GetGlobalRect();
		float menuWidth = Mathf.Max(_filterMenu.CustomMinimumSize.X, btnRect.Size.X);
		float menuHeight = _filterMenu.GetCombinedMinimumSize().Y;
		if (menuHeight < 80f)
			menuHeight = 240f;

		// Convert to local coords of the popup's parent (Control under this Window).
		var parent = _filterMenu.GetParent() as Control;
		Vector2 localTopLeft;
		if (parent != null)
		{
			// Prefer below the button when there is room; otherwise above.
			float spaceBelow = parent.Size.Y - (btnRect.Position.Y - parent.GlobalPosition.Y + btnRect.Size.Y);
			float yOffset = spaceBelow >= menuHeight + 4f
				? btnRect.Size.Y + 2f
				: -menuHeight - 2f;
			localTopLeft = parent.GetGlobalTransformWithCanvas().AffineInverse()
				* (btnRect.Position + new Vector2(0, yOffset));
		}
		else
		{
			localTopLeft = new Vector2(btnRect.Position.X, btnRect.Position.Y - menuHeight);
		}

		_filterMenu.Position = localTopLeft;
		_filterMenu.Size = new Vector2(menuWidth, menuHeight);
		_filterMenu.Visible = true;
		_filterMenu.MoveToFront();
		SetProcessInput(true);
	}

	private void HideFilterMenu()
	{
		if (_filterMenu != null)
			_filterMenu.Visible = false;
		SetProcessInput(false);
	}

	public override void _Input(InputEvent @event)
	{
		// Close filter popup when clicking outside it.
		if (_filterMenu != null && _filterMenu.Visible &&
		    @event is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } mb)
		{
			var local = _filterMenu.GetGlobalTransformWithCanvas().AffineInverse() * mb.GlobalPosition;
			bool overMenu = new Rect2(Vector2.Zero, _filterMenu.Size).HasPoint(local);
			bool overButton = false;
			if (_filterButton != null)
			{
				var btnLocal = _filterButton.GetGlobalTransformWithCanvas().AffineInverse() * mb.GlobalPosition;
				overButton = new Rect2(Vector2.Zero, _filterButton.Size).HasPoint(btnLocal);
			}

			if (!overMenu && !overButton)
				HideFilterMenu();
		}

		base._Input(@event);
	}

	private void OnFilterAllToggled(bool pressed)
	{
		if (_isUpdatingFilterUi)
			return;

		_isUpdatingFilterUi = true;
		foreach (var box in _filterCheckBoxes.Values)
			box.ButtonPressed = pressed;
		_isUpdatingFilterUi = false;
		UpdateFilterButtonLabel();
	}

	private void OnFilterCategoryToggled(string categoryId, bool pressed)
	{
		if (_isUpdatingFilterUi)
			return;

		_isUpdatingFilterUi = true;
		if (_filterAllCheckBox != null)
		{
			bool allOn = _filterCheckBoxes.Values.All(b => b.ButtonPressed);
			bool anyOn = _filterCheckBoxes.Values.Any(b => b.ButtonPressed);
			// All is only fully checked when every category is on; unchecked otherwise.
			_filterAllCheckBox.SetPressedNoSignal(allOn);
			// If user unchecked the last category, leave All off (GetSelectedCategoryIds handles empty).
			if (!anyOn)
				_filterAllCheckBox.SetPressedNoSignal(false);
		}
		_isUpdatingFilterUi = false;
		UpdateFilterButtonLabel();
	}

	/// <summary>
	/// Updates the filter button caption to summarise the current selection.
	/// </summary>
	private void UpdateFilterButtonLabel()
	{
		if (_filterButton == null)
			return;

		var selected = GetSelectedCategoryIds();
		if (selected.Count == 0)
		{
			_filterButton.Text = T("None");
			return;
		}

		if (selected.Count == SettingsExport.Categories.Length)
		{
			_filterButton.Text = T("All");
			return;
		}

		if (selected.Count == 1)
		{
			var cat = SettingsExport.Categories.FirstOrDefault(c => c.Id == selected[0]);
			string label = string.IsNullOrEmpty(cat.Label) ? selected[0] : cat.Label;
			// Tree-matched English labels are translation keys.
			_filterButton.Text = T(label);
			return;
		}

		_filterButton.Text = Tf("{0} selected", selected.Count);
	}

	/// <summary>
	/// Category ids currently checked in the filter dropdown.
	/// When All is pressed, returns every known category id.
	/// </summary>
	private List<string> GetSelectedCategoryIds()
	{
		// Prefer explicit category boxes so partial selections stay accurate.
		if (_filterCheckBoxes.Count > 0)
		{
			return _filterCheckBoxes
				.Where(kv => kv.Value.ButtonPressed)
				.Select(kv => kv.Key)
				.ToList();
		}

		if (_filterAllCheckBox != null && _filterAllCheckBox.ButtonPressed)
			return SettingsExport.Categories.Select(c => c.Id).ToList();

		return new List<string>();
	}

	// ──────────────────────────────────────────────────────────────────────
	// Save / Load settings file
	// ──────────────────────────────────────────────────────────────────────

	private void OnSaveSettingsPressed()
	{
		HideFilterMenu();
		var categories = GetSelectedCategoryIds();
		if (categories.Count == 0)
		{
			LogSettingsFile("Select at least one filter category before storing settings.", LogType.Warning);
			return;
		}

		OpenSettingsFileDialog(FileDialog.FileModeEnum.SaveFile, path => SaveSettingsToPath(path, categories));
	}

	private void OnLoadSettingsPressed()
	{
		HideFilterMenu();
		var categories = GetSelectedCategoryIds();
		if (categories.Count == 0)
		{
			LogSettingsFile("Select at least one filter category before recalling settings.", LogType.Warning);
			return;
		}

		OpenSettingsFileDialog(FileDialog.FileModeEnum.OpenFile, path => LoadSettingsFromPath(path, categories));
	}

	/// <summary>
	/// Opens a native filesystem dialog for .c2settings files.
	/// </summary>
	private void OpenSettingsFileDialog(FileDialog.FileModeEnum mode, Action<string> onSelected)
	{
		ClearSettingsFileDialog();

		_settingsFileDialog = new FileDialog
		{
			FileMode = mode,
			Access = FileDialog.AccessEnum.Filesystem,
			Title = mode == FileDialog.FileModeEnum.SaveFile ? "Store Settings" : "Recall Settings",
			UseNativeDialog = true
		};
		_settingsFileDialog.AddFilter(SettingsExport.FileDialogFilter);
		_settingsFileDialog.AddFilter("*.json ; JSON");

		// Prefer the active show folder when available.
		if (!string.IsNullOrEmpty(_globalData?.SessionPath))
		{
			try
			{
				string baseDir = _globalData.SessionPath.GetBaseDir();
				if (DirAccess.DirExistsAbsolute(baseDir))
					_settingsFileDialog.CurrentDir = baseDir;
			}
			catch (Exception ex)
			{
				GD.Print($"SettingsWindow:OpenSettingsFileDialog - Could not set initial dir: {ex.Message}");
			}
		}

		if (mode == FileDialog.FileModeEnum.SaveFile)
			_settingsFileDialog.CurrentFile = $"settings.{SettingsExport.FileExtension}";

		AddChild(_settingsFileDialog);
		_settingsFileDialog.FileSelected += path =>
		{
			try
			{
				onSelected?.Invoke(path);
			}
			finally
			{
				ClearSettingsFileDialog();
			}
		};
		_settingsFileDialog.Canceled += ClearSettingsFileDialog;
		_settingsFileDialog.PopupCenteredClamped(new Vector2I(900, 600));
	}

	private void ClearSettingsFileDialog()
	{
		if (_settingsFileDialog == null)
			return;

		if (GodotObject.IsInstanceValid(_settingsFileDialog))
			_settingsFileDialog.QueueFree();
		_settingsFileDialog = null;
	}

	/// <summary>
	/// Writes the selected settings categories to a plain JSON .c2settings file.
	/// </summary>
	private void SaveSettingsToPath(string path, List<string> categoryIds)
	{
		if (string.IsNullOrWhiteSpace(path) || _globalData?.Settings == null)
			return;

		try
		{
			path = EnsureSettingsExtension(path);
			var keys = SettingsExport.ResolveKeys(categoryIds);
			if (keys.Length == 0)
			{
				LogSettingsFile("No settings keys resolved for the selected filter.", LogType.Warning);
				return;
			}

			var slice = _globalData.Settings.CaptureHistorySlice(keys);
			// Deep-clone via JSON so the document does not alias live dictionaries.
			string sliceJson = Json.Stringify(slice);
			using var sliceParser = new Json();
			if (sliceParser.Parse(sliceJson) != Error.Ok)
			{
				LogSettingsFile("Failed to serialise settings for export.", LogType.Error);
				return;
			}

			var document = SettingsExport.BuildDocument(categoryIds, sliceParser.Data.AsGodotDictionary());
			string json = Json.Stringify(document, "\t");

			using var file = FileAccess.Open(path, FileAccess.ModeFlags.Write);
			if (file == null)
			{
				Error err = FileAccess.GetOpenError();
				LogSettingsFile($"Failed to write settings file: {path} ({err})", LogType.Error);
				return;
			}

			file.StoreString(json);
			LogSettingsFile($"Settings stored to {path} ({categoryIds.Count} categor{(categoryIds.Count == 1 ? "y" : "ies")}).", LogType.Info);
			GD.Print($"SettingsWindow:SaveSettingsToPath - Wrote {keys.Length} key(s) → {path}");
		}
		catch (Exception ex)
		{
			LogSettingsFile($"Error storing settings: {ex.Message}", LogType.Error);
			GD.PrintErr($"SettingsWindow:SaveSettingsToPath - {ex}");
		}
	}

	/// <summary>
	/// Loads a .c2settings (or bare settings JSON) file and applies only the selected categories.
	/// Records a single undo step covering the keys that will change.
	/// </summary>
	private void LoadSettingsFromPath(string path, List<string> categoryIds)
	{
		if (string.IsNullOrWhiteSpace(path) || _globalData?.Settings == null)
			return;

		try
		{
			if (!FileAccess.FileExists(path))
			{
				LogSettingsFile($"Settings file not found: {path}", LogType.Error);
				return;
			}

			using var file = FileAccess.Open(path, FileAccess.ModeFlags.Read);
			if (file == null)
			{
				Error err = FileAccess.GetOpenError();
				LogSettingsFile($"Failed to open settings file: {path} ({err})", LogType.Error);
				return;
			}

			string json = file.GetAsText();
			using var parser = new Json();
			var parseErr = parser.Parse(json);
			if (parseErr != Error.Ok)
			{
				LogSettingsFile($"Invalid JSON in settings file: {parseErr}", LogType.Error);
				return;
			}

			if (parser.Data.VariantType != Variant.Type.Dictionary)
			{
				LogSettingsFile("Settings file root must be a JSON object.", LogType.Error);
				return;
			}

			var root = parser.Data.AsGodotDictionary();
			if (!SettingsExport.TryParseDocument(root, out var fileSettings, out var catsInFile, out var parseError))
			{
				LogSettingsFile(parseError ?? "Could not parse settings file.", LogType.Error);
				return;
			}

			var toApply = SettingsExport.FilterSettingsByCategories(fileSettings, categoryIds);
			if (toApply.Count == 0)
			{
				string hint = catsInFile is { Length: > 0 }
					? $" File contains: {string.Join(", ", catsInFile)}."
					: string.Empty;
				LogSettingsFile(
					"No matching settings found for the selected filter." + hint,
					LogType.Warning);
				return;
			}

			// Keys actually present after filtering — record those for undo.
			var appliedKeys = new List<string>();
			foreach (var k in toApply.Keys)
				appliedKeys.Add(k.AsString());

			var history = _globalData.HistoryManager;
			history?.RecordSettingsChange(
				$"Recall settings ({string.Join(", ", categoryIds)})",
				null,
				appliedKeys.ToArray());

			_globalData.Settings.ApplyPartialFromHistory(toApply);
			history?.NotifySettingsApplied(appliedKeys.ToArray());

			LogSettingsFile(
				$"Settings recalled from {path} ({appliedKeys.Count} key(s): {string.Join(", ", appliedKeys)}).",
				LogType.Info);
			GD.Print($"SettingsWindow:LoadSettingsFromPath - Applied [{string.Join(", ", appliedKeys)}] from {path}");
		}
		catch (Exception ex)
		{
			LogSettingsFile($"Error recalling settings: {ex.Message}", LogType.Error);
			GD.PrintErr($"SettingsWindow:LoadSettingsFromPath - {ex}");
		}
	}

	/// <summary>
	/// Ensures the path ends with .<see cref="SettingsExport.FileExtension"/> when saving
	/// without an extension (or with a bare name).
	/// </summary>
	private static string EnsureSettingsExtension(string path)
	{
		if (string.IsNullOrWhiteSpace(path))
			return path;

		string ext = path.GetExtension().TrimStart('.').ToLowerInvariant();
		if (ext is SettingsExport.FileExtension or "json")
			return path;

		return path + "." + SettingsExport.FileExtension;
	}

	private void LogSettingsFile(string message, LogType type)
	{
		_globalSignals?.EmitSignal(nameof(GlobalSignals.Log), message, (int)type);
	}

	private void ScaleUi(float value)
	{
		float userScale = _globalData.UserDataManager?.UiScale ?? UserDataManager.DefaultUiScale;
		UiUtilities.RescaleUi(this, userScale, _globalData.BaseDisplayScale);
	}

	// On tree item pressed display each settings menu.
	private void _on_tree_item_selected()
	{
		var selected = _setTree?.GetSelected();
		if (selected == null)
		{
			return;
		}

		// Use stable English menu key (not the possibly-translated display text).
		string action = GetTreeItemMenuKey(selected);
		if (!TryGetMenuNode(action, out string menuNode))
		{
			// Category headers / unimplemented pages — keep previous panel.
			return;
		}

		ShowMenuPanel(menuNode);
		SaveSelectedMenu(action);
	}

	/// <summary>
	/// Shows the settings panel for a tree label and hides the previous one.
	/// </summary>
	private void ShowMenuForTreeLabel(string treeLabel)
	{
		if (!TryGetMenuNode(treeLabel, out string menuNode))
		{
			return;
		}

		ShowMenuPanel(menuNode);
		_sessionMenuKey = treeLabel;
	}

	private void ShowMenuPanel(string menuNode)
	{
		if (_currentDisplay != "")
		{
			var previous = GetNodeOrNull<Control>("%" + _currentDisplay);
			previous?.Hide();
		}
		else
		{
			// Checks all settings displays in case one is already open
			foreach (var node in GetNode<MarginContainer>("%RightSide").GetChildren())
			{
				if (node is Control child && child.IsVisible())
					child.Hide();
			}
		}

		var panel = GetNodeOrNull<Control>("%" + menuNode);
		if (panel == null)
		{
			GD.PrintErr($"SettingsWindow:ShowMenuPanel - Panel %{menuNode} not found.");
			return;
		}

		panel.Show();
		_currentDisplay = menuNode;
	}

	/// <summary>
	/// Maps a Settings tree item label to the unique-name of its content panel.
	/// </summary>
	/// <returns>True when the label has an implemented settings page.</returns>
	private static bool TryGetMenuNode(string action, out string menuNode)
	{
		menuNode = action switch
		{
			"General" => "SettingsGeneral",
			"Input Map" => "SettingsInputMap",
			"Audio" => "SettingsAudio",
			"Audio Output Patch" => "AudioOutputPatch",
			"Canvas Editor" => "CanvasEditor",
			// Parent Video/Image hosts the general video output panel; legacy label kept for session restore.
			"Video/Image" => "SettingsVideoOutput",
			"Video Output" => "SettingsVideoOutput",
			"Cue Lights" => "CueLights",
			"OSC Connections" => "SettingsOscConnections",
			"OSC Listener" => "SettingsOscListen",
			"OSC Input Map" => "SettingsOscInputMap",
			"MIDI" => "SettingsMidi",
			"MIDI Input Map" => "SettingsMidiInputMap",
			"Cue Defaults" => "SettingsCueDefaults",
			"Audio Defaults" => "SettingsAudioDefaults",
			"Video Defaults" => "SettingsVideoDefaults",
			"Text Defaults" => "SettingsTextDefaults",
			"Cue2 Preferences" => "SettingsCue2Prefs",
			"Updates" => "SettingsUpdates",
			_ => null
		};
		return menuNode != null;
	}

	private void _generateTree()
	{
		// Settings Tree
		_setTree = GetNode<Godot.Tree>("%SettingsTree");
		TreeItem root = _setTree.CreateItem();
		_setTree.HideRoot = true;

		// English labels are stable menu keys (persisted / panel mapping); display is translated.
		//General (show-scoped)
		TreeItem tiGeneral = _setTree.CreateItem(root);
		SetTreeItemText(tiGeneral, 0, "General");

		// Audio
		TreeItem tiAudio = _setTree.CreateItem(root);
		SetTreeItemText(tiAudio, 0, "Audio");
		TreeItem tiAudioOutputPatch = _setTree.CreateItem(tiAudio);
		SetTreeItemText(tiAudioOutputPatch, 0, "Audio Output Patch");

		// Video / Image (parent shows general video output panel; Canvas Editor is topology)
		TreeItem tiOutputDevices = _setTree.CreateItem(root);
		SetTreeItemText(tiOutputDevices, 0, "Video/Image");
		SetTreeItemTooltip(tiOutputDevices, 0,
			"Disable/blackout, background colour, and machine video performance preferences.");
		TreeItem tiVideoDevice = _setTree.CreateItem(tiOutputDevices);
		SetTreeItemText(tiVideoDevice, 0, "Canvas Editor");

		// Connections
		TreeItem tiConnections = _setTree.CreateItem(root);
		SetTreeItemText(tiConnections, 0, "Connections");
		// Cue Lights: kept in codebase (panel + TryGetMenuNode mapping) but not shipped in v1 UI.
		// TreeItem tiCueLights = _setTree.CreateItem(tiConnections);
		// SetTreeItemText(tiCueLights, 0, "Cue Lights");
		TreeItem tiOscConnections = _setTree.CreateItem(tiConnections);
		SetTreeItemText(tiOscConnections, 0, "OSC Connections");
		SetTreeItemTooltip(tiOscConnections, 0, "Named OSC send destinations and send monitor");
		TreeItem tiOscListener = _setTree.CreateItem(tiConnections);
		SetTreeItemText(tiOscListener, 0, "OSC Listener");
		SetTreeItemTooltip(tiOscListener, 0, "UDP receive port and live receive monitor");
		TreeItem tiOscInputMap = _setTree.CreateItem(tiOscListener);
		SetTreeItemText(tiOscInputMap, 0, "OSC Input Map");
		SetTreeItemTooltip(tiOscInputMap, 0, "Assign OSC addresses to app actions (Go, Save, Undo, …)");
		TreeItem tiMidi = _setTree.CreateItem(tiConnections);
		SetTreeItemText(tiMidi, 0, "MIDI");
		SetTreeItemTooltip(tiMidi, 0, "MIDI input devices and live monitor");
		TreeItem tiMidiInputMap = _setTree.CreateItem(tiMidi);
		SetTreeItemText(tiMidiInputMap, 0, "MIDI Input Map");
		SetTreeItemTooltip(tiMidiInputMap, 0, "Assign MIDI controls to app actions (Go, Save, Undo, …)");
		// Art-Net: not shipped in this version — re-add under Connections when implementing.

		// Cue defaults (shell + component defaults applied to newly created cues/components)
		TreeItem tiDefaults = _setTree.CreateItem(root);
		SetTreeItemText(tiDefaults, 0, "Cue Defaults");
		SetTreeItemTooltip(tiDefaults, 0, "Default shell properties for newly created cues (pre-wait, colour, arming, etc.).");
		TreeItem tiAudioCueDefaults = _setTree.CreateItem(tiDefaults);
		SetTreeItemText(tiAudioCueDefaults, 0, "Audio Defaults");
		SetTreeItemTooltip(tiAudioCueDefaults, 0, "Default volume, pan, loop, play count, and fades for new audio components.");
		TreeItem tiVideoCueDefaults = _setTree.CreateItem(tiDefaults);
		SetTreeItemText(tiVideoCueDefaults, 0, "Video Defaults");
		SetTreeItemTooltip(tiVideoCueDefaults, 0, "Default layout, opacity, loop, embedded audio, image duration, and fades for new video components.");
		TreeItem tiTextCueDefaults = _setTree.CreateItem(tiDefaults);
		SetTreeItemText(tiTextCueDefaults, 0, "Text Defaults");
		SetTreeItemTooltip(tiTextCueDefaults, 0, "Default typography, alignment, duration, outline/background, and fades for new text components.");

		// App preferences (user:// — not stored in the showfile)
		TreeItem tiCue2Preferences = _setTree.CreateItem(root);
		SetTreeItemText(tiCue2Preferences, 0, "Cue2 Preferences");
		SetTreeItemTooltip(tiCue2Preferences, 0, "Showfile-independent preferences (stored per user)");
		TreeItem tiInputMap = _setTree.CreateItem(tiCue2Preferences);
		SetTreeItemText(tiInputMap, 0, "Input Map");
		SetTreeItemTooltip(tiInputMap, 0, "Keyboard shortcuts — saved with Cue2 Preferences, not the show");
		TreeItem tiUpdates = _setTree.CreateItem(tiCue2Preferences);
		SetTreeItemText(tiUpdates, 0, "Updates");
		SetTreeItemTooltip(tiUpdates, 0, "Check for Cue2 updates — saved with Cue2 Preferences, not the show");
	}

	/// <summary>
	/// Selects the Settings page for <paramref name="menuKey"/> (stable English tree key).
	/// </summary>
	/// <param name="menuKey">E.g. <c>Updates</c> or <c>General</c>.</param>
	public void OpenMenu(string menuKey)
	{
		if (string.IsNullOrWhiteSpace(menuKey) || !TryGetMenuNode(menuKey, out _))
			return;

		_sessionMenuKey = menuKey;
		RestoreSelectedMenu();
		if (!Visible)
			Show();
		GrabFocus();
	}

	/// <summary>
	/// Disconnects signals and persists final window geometry and menu before the node is freed.
	/// </summary>
	public override void _ExitTree()
	{
		SizeChanged -= OnWindowSizeChanged;
		_resizeSaveTimer?.Stop();
		if (_positionPollTimer != null)
		{
			_positionPollTimer.Stop();
			_positionPollTimer.Timeout -= PollWindowPosition;
		}
		SaveCurrentWindowState();
		ClearSettingsFileDialog();

		// Ensure last visible page is cached even if selection signal didn't fire again.
		if (!string.IsNullOrEmpty(_sessionMenuKey))
		{
			SaveSelectedMenu(_sessionMenuKey);
		}

		if (_globalSignals != null)
		{
			_globalSignals.UiScaleChanged -= ScaleUi;
			_globalSignals.LocaleChanged -= OnLocaleChanged;
		}

		if (_filterButton != null)
			_filterButton.Pressed -= ToggleFilterMenu;
	}
}
