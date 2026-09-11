// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Godot;
using Godot.Collections;

namespace Cue2.Services;

/// <summary>
/// Manages persistent intershow data stored in "user://" directory.
/// This is a child node of GlobalData and is responsible for data that survives
/// across sessions and application restarts.
/// </summary>
public partial class UserDataManager : Node
{
	private GlobalSignals _globalSignals;
	private GlobalData _globalData;

	private const string UserDataFilePath = "user://user_data.json";
	private const int MaxRecentShowFiles = 12;

	private List<string> _recentShowFiles = new List<string>();

	private Vector2I _lastWindowSize = Vector2I.Zero;
	private bool _wasMaximized = false;
	/// <summary>True when the last fill-screen state was fullscreen (vs OS maximize).</summary>
	private bool _wasFullscreen = false;
	private Vector2I _lastWindowPosition = Vector2I.Zero;

	private Vector2I _lastSettingsWindowSize = Vector2I.Zero;
	private Vector2I _lastSettingsWindowPosition = Vector2I.Zero;
	private bool _settingsWasMaximized = false;
	/// <summary>Tree item label of the last Settings sub-menu (e.g. "Canvas Editor").</summary>
	private string _lastSettingsMenu = "General";

	private int _autosaveInterval = 5; // minutes, 0 = disabled
	private int _backupDepth = DefaultBackupDepth;
	private int _undoDepth = DefaultUndoDepth;
	private int _logSessionDepth = DefaultLogSessionDepth;

	/// <summary>
	/// True until the user has dismissed the first-time startup welcome window.
	/// Defaults to true for new installs (no user_data.json yet).
	/// </summary>
	private bool _isFirstTimeStartup = true;

	/// <summary>
	/// Preferred UI locale code (e.g. <c>en</c>). Applied by <see cref="LocalizationService"/>.
	/// </summary>
	private string _locale = DefaultLocale;

	/// <summary>
	/// User UI scale multiplier (1.0 = 100%). Combined with system/HiDPI base scale at apply time.
	/// </summary>
	private float _uiScale = DefaultUiScale;

	private bool _checkForUpdatesOnStartup = true;
	private bool _includePrereleaseUpdates;
	private string _skippedUpdateVersion = string.Empty;
	private string _lastUpdateCheckUtc = string.Empty;
	private string _registeredShowfileHandlerPath = string.Empty;

	/// <summary>
	/// Production default UI locale (English).
	/// </summary>
	public const string DefaultLocale = "en";

	/// <summary>Default UI scale (1.0 = 100%).</summary>
	public const float DefaultUiScale = 1.0f;

	/// <summary>Minimum allowed UI scale (25%).</summary>
	public const float MinUiScale = 0.25f;

	/// <summary>Maximum allowed UI scale (400%).</summary>
	public const float MaxUiScale = 4.0f;

	/// <summary>Minimum UI scale as a whole percent for sliders/fields.</summary>
	public const float MinUiScalePercent = 25f;

	/// <summary>Maximum UI scale as a whole percent for sliders/fields.</summary>
	public const float MaxUiScalePercent = 400f;

	/// <summary>
	/// Last directory the user chose in Save / Save As (parent of the session folder).
	/// Used when saving a new unsaved show.
	/// </summary>
	private string _lastShowSaveDirectory = string.Empty;

	/// <summary>Cuelist shell number column width (0 = use default).</summary>
	private float _shellNumberColumnWidth;

	/// <summary>Cuelist shell pre/duration/post column width (0 = use default).</summary>
	private float _shellTimeColumnWidth;

	/// <summary>
	/// Serialized custom InputMap bindings (action → event list). Null/empty means use project defaults.
	/// Loaded from disk before factory defaults are captured; applied after <see cref="GlobalData"/> captures defaults.
	/// </summary>
	private Dictionary _inputMapBindings;

	/// <summary>
	/// The production default autosave interval in minutes.
	/// </summary>
	public static readonly int DefaultAutosaveInterval = 5;

	/// <summary>
	/// The production default number of autosave backups to keep.
	/// </summary>
	public static readonly int DefaultBackupDepth = 3;

	/// <summary>
	/// The production default number of undo/redo history steps to keep.
	/// </summary>
	public static readonly int DefaultUndoDepth = 50;

	/// <summary>Minimum allowed undo history depth.</summary>
	public const int MinUndoDepth = 4;

	/// <summary>Maximum allowed undo history depth.</summary>
	public const int MaxUndoDepth = 200;

	/// <summary>
	/// The production default number of session log files to retain (including the current session).
	/// Matches a Godot-like file_logging max depth.
	/// </summary>
	public static readonly int DefaultLogSessionDepth = 5;

	/// <summary>
	/// Minimum allowed log session depth. 1 keeps only the current session file (no history).
	/// </summary>
	public const int MinLogSessionDepth = 1;

	/// <summary>Maximum allowed log session depth.</summary>
	public const int MaxLogSessionDepth = 50;

	/// <summary>
	/// Defines the startup behavior when the application launches without a file argument.
	/// </summary>
	public enum StartupBehavior
	{
		OpenLastShowfile = 0,
		NewShowfile = 1
	}

	/// <summary>
	/// The production default startup behavior.
	/// </summary>
	public static readonly StartupBehavior DefaultStartupBehavior = StartupBehavior.OpenLastShowfile;

	private StartupBehavior _startupBehavior = DefaultStartupBehavior;

	/// <summary>
	/// Gets a read-only view of the current list of recently opened show file paths.
	/// The list is ordered with the most recently opened file first.
	/// </summary>
	/// <value>A read-only list of absolute file paths to recently used .c2 show files.</value>
	public IReadOnlyList<string> RecentShowFiles => _recentShowFiles.AsReadOnly();

	/// <summary>
	/// The last recorded size of the main window (when not maximized).
	/// </summary>
	public Vector2I LastWindowSize => _lastWindowSize;

	/// <summary>
	/// Whether the main window was fill-screen (maximized or fullscreen) when last closed.
	/// </summary>
	public bool WasMaximized => _wasMaximized;

	/// <summary>
	/// Whether the last fill-screen state was non-exclusive fullscreen (expand button)
	/// rather than OS maximize (title double-click).
	/// </summary>
	public bool WasFullscreen => _wasFullscreen;

	/// <summary>
	/// The last recorded position of the window (relative to the top-left of the display it was on when last saved).
	/// </summary>
	public Vector2I LastWindowPosition => _lastWindowPosition;

	/// <summary>
	/// The last recorded size of the Settings window (when not maximized).
	/// </summary>
	public Vector2I LastSettingsWindowSize => _lastSettingsWindowSize;

	/// <summary>
	/// The last recorded position of the Settings window (relative to the top-left of the display it was on when last saved).
	/// </summary>
	public Vector2I LastSettingsWindowPosition => _lastSettingsWindowPosition;

	/// <summary>
	/// Whether the Settings window was maximized when last closed.
	/// </summary>
	public bool SettingsWasMaximized => _settingsWasMaximized;

	/// <summary>
	/// Tree item label of the last open Settings sub-menu (e.g. "Canvas Editor", "General").
	/// </summary>
	public string LastSettingsMenu => _lastSettingsMenu;

	/// <summary>
	/// Last directory used for Save / Save As of a showfile (not the nested session folder).
	/// Empty until the user has saved at least once on this machine.
	/// </summary>
	/// <value>Absolute directory path, or empty.</value>
	public string LastShowSaveDirectory => _lastShowSaveDirectory;

	/// <summary>
	/// The configured startup behavior.
	/// </summary>
	public StartupBehavior Startup
	{
		get => _startupBehavior;
		set
		{
			if (_startupBehavior != value)
			{
				_startupBehavior = value;
				SaveUserData();
			}
		}
	}

	/// <summary>
	/// Whether the first-time startup welcome window should be shown.
	/// True for new installations until the user dismisses the welcome UI.
	/// </summary>
	/// <value>True if the welcome flow has not been completed yet.</value>
	public bool IsFirstTimeStartup
	{
		get => _isFirstTimeStartup;
		set
		{
			if (_isFirstTimeStartup != value)
			{
				_isFirstTimeStartup = value;
				SaveUserData();
			}
		}
	}

	/// <summary>
	/// Marks the first-time startup welcome as completed and persists user data.
	/// </summary>
	public void MarkFirstTimeStartupComplete()
	{
		if (!_isFirstTimeStartup)
			return;

		_isFirstTimeStartup = false;
		SaveUserData();
		GD.Print("UserDataManager:MarkFirstTimeStartupComplete - First-time startup flagged complete.");
	}

	/// <summary>
	/// Preferred application UI locale code (ISO-style, e.g. <c>en</c>).
	/// </summary>
	/// <value>
	/// Persisted in user preferences. Applying the locale to
	/// <see cref="TranslationServer"/> is handled by <see cref="LocalizationService"/>.
	/// </value>
	public string Locale
	{
		get => _locale;
		set
		{
			string next = string.IsNullOrWhiteSpace(value)
				? DefaultLocale
				: value.Trim().ToLowerInvariant();
			if (_locale != next)
			{
				_locale = next;
				SaveUserData();
			}
		}
	}

	/// <summary>
	/// Application UI scale multiplier (1.0 = 100%). User preference, not showfile data.
	/// </summary>
	/// <value>
	/// Clamped to <see cref="MinUiScale"/>–<see cref="MaxUiScale"/> (25%–400%).
	/// Setting a new value persists user data and emits <see cref="GlobalSignals.UiScaleChanged"/>.
	/// </value>
	public float UiScale
	{
		get => _uiScale;
		set
		{
			float clamped = Mathf.Clamp(value, MinUiScale, MaxUiScale);
			if (Mathf.IsEqualApprox(_uiScale, clamped))
				return;
			_uiScale = clamped;
			SaveUserData();
			_globalSignals?.EmitSignal(nameof(GlobalSignals.UiScaleChanged), _uiScale);
		}
	}

	/// <summary>
	/// When true, exported builds check GitHub Releases a few seconds after launch.
	/// </summary>
	public bool CheckForUpdatesOnStartup
	{
		get => _checkForUpdatesOnStartup;
		set
		{
			if (_checkForUpdatesOnStartup == value)
				return;
			_checkForUpdatesOnStartup = value;
			SaveUserData();
		}
	}

	/// <summary>
	/// When true, update checks include GitHub prereleases (rc/beta tags).
	/// </summary>
	public bool IncludePrereleaseUpdates
	{
		get => _includePrereleaseUpdates;
		set
		{
			if (_includePrereleaseUpdates == value)
				return;
			_includePrereleaseUpdates = value;
			SaveUserData();
		}
	}

	/// <summary>
	/// Semantic version the user skipped, or empty.
	/// </summary>
	public string SkippedUpdateVersion
	{
		get => _skippedUpdateVersion ?? string.Empty;
		set
		{
			string next = value?.Trim() ?? string.Empty;
			if (_skippedUpdateVersion == next)
				return;
			_skippedUpdateVersion = next;
			SaveUserData();
		}
	}

	/// <summary>
	/// Absolute host path last written into the OS <c>.c2</c> file association, or empty.
	/// </summary>
	public string RegisteredShowfileHandlerPath
	{
		get => _registeredShowfileHandlerPath ?? string.Empty;
		set
		{
			string next = value?.Trim() ?? string.Empty;
			if (_registeredShowfileHandlerPath == next)
				return;
			_registeredShowfileHandlerPath = next;
			SaveUserData();
		}
	}

	/// <summary>
	/// UTC timestamp of the last successful or failed auto/manual check (ISO-8601), or empty.
	/// </summary>
	public string LastUpdateCheckUtc
	{
		get => _lastUpdateCheckUtc ?? string.Empty;
		set
		{
			string next = value?.Trim() ?? string.Empty;
			if (_lastUpdateCheckUtc == next)
				return;
			_lastUpdateCheckUtc = next;
			SaveUserData();
		}
	}

	/// <summary>
	/// Autosave interval in minutes. 0 disables autosave.
	/// </summary>
	public int AutosaveInterval
	{
		get => _autosaveInterval;
		set
		{
			if (_autosaveInterval != value)
			{
				_autosaveInterval = Math.Max(0, value);
				SaveUserData();
			}
		}
	}

	/// <summary>
	/// Number of autosave backups to keep in the Backups folder.
	/// </summary>
	public int BackupDepth
	{
		get => _backupDepth;
		set
		{
			if (_backupDepth != value)
			{
				_backupDepth = Math.Max(1, value);
				SaveUserData();
			}
		}
	}

	/// <summary>
	/// Number of document undo/redo steps to retain. Clamped to <see cref="MinUndoDepth"/>–<see cref="MaxUndoDepth"/>.
	/// </summary>
	/// <value>History depth used by <c>HistoryManager</c>.</value>
	public int UndoDepth
	{
		get => _undoDepth;
		set
		{
			int clamped = Math.Clamp(value, MinUndoDepth, MaxUndoDepth);
			if (_undoDepth != clamped)
			{
				_undoDepth = clamped;
				SaveUserData();
			}
		}
	}

	/// <summary>
	/// Number of session log files to retain on disk (including the current session).
	/// Clamped to <see cref="MinLogSessionDepth"/>–<see cref="MaxLogSessionDepth"/>.
	/// </summary>
	/// <value>
	/// Godot-style log session depth used by <c>EventLogger</c>.
	/// 1 = current session only; higher values keep that many total session files.
	/// </value>
	public int LogSessionDepth
	{
		get => _logSessionDepth;
		set
		{
			int clamped = Math.Clamp(value, MinLogSessionDepth, MaxLogSessionDepth);
			if (_logSessionDepth != clamped)
			{
				_logSessionDepth = clamped;
				SaveUserData();
			}
		}
	}

	/// <summary>
	/// Persisted cuelist number-column width in pixels. 0 means use the layout default.
	/// </summary>
	public float ShellNumberColumnWidth => _shellNumberColumnWidth;

	/// <summary>
	/// Persisted cuelist time-column width (pre/duration/post) in pixels. 0 means use the layout default.
	/// </summary>
	public float ShellTimeColumnWidth => _shellTimeColumnWidth;

	/// <summary>
	/// Updates shell column width preferences in memory. Written to disk with the next user-data save.
	/// </summary>
	/// <param name="numberWidth">Cue number column width.</param>
	/// <param name="timeWidth">Pre-wait / duration / post-wait column width.</param>
	public void SetShellColumnWidths(float numberWidth, float timeWidth)
	{
		_shellNumberColumnWidth = numberWidth;
		_shellTimeColumnWidth = timeWidth;
	}

	public override void _Ready()
	{
		_globalSignals = GetNode<GlobalSignals>("/root/GlobalSignals");
		_globalData = GetNode<GlobalData>("/root/GlobalData");

		LoadUserData();

		GD.Print("UserDataManager:_Ready - Initialized. Recent show files loaded: " + _recentShowFiles.Count);
	}

	/// <summary>
	/// Applies input bindings loaded from user data onto the live InputMap.
	/// Must run after factory defaults were captured by <see cref="GlobalData"/>.
	/// </summary>
	public void ApplyInputMapFromUserData()
	{
		if (_globalData == null || _inputMapBindings == null || _inputMapBindings.Count == 0)
		{
			GD.Print("UserDataManager:ApplyInputMapFromUserData - No custom InputMap stored; keeping defaults.");
			return;
		}

		_globalData.ApplyInputBindings(_inputMapBindings);
		GD.Print($"UserDataManager:ApplyInputMapFromUserData - Applied {_inputMapBindings.Count} action binding(s) from user preferences.");
	}

	/// <summary>
	/// Snapshots the live InputMap into user preferences and writes user_data.json.
	/// Call after the user changes a shortcut in Input Map settings.
	/// </summary>
	public void PersistLiveInputMap()
	{
		if (_globalData == null)
			return;

		_inputMapBindings = _globalData.GetCustomInputBindings();
		SaveUserData();
		GD.Print("UserDataManager:PersistLiveInputMap - Input Map saved to user preferences.");
	}

	internal string NormalizeForRecent(string path)
	{
		if (string.IsNullOrWhiteSpace(path)) return string.Empty;
		try
		{
			string full = Path.GetFullPath(path);
			full = full.Replace('\\', '/');
			if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
				full = full.ToLowerInvariant();
			return full;
		}
		catch
		{
			string f = path.Replace('\\', '/').Trim();
			if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
				f = f.ToLowerInvariant();
			return f;
		}
	}

	/// <summary>
	/// Records the last Save / Save As directory for a later new-show save dialog.
	/// </summary>
	/// <param name="directory">Absolute directory (typically the parent of the session folder).</param>
	/// <param name="persistImmediately">When true, writes user_data.json now.</param>
	public void SetLastShowSaveDirectory(string directory, bool persistImmediately = true)
	{
		if (string.IsNullOrWhiteSpace(directory))
			return;

		string trimmed = directory.Trim().Replace('\\', '/').TrimEnd('/');
		if (string.IsNullOrEmpty(trimmed))
			return;

		if (string.Equals(_lastShowSaveDirectory, trimmed, StringComparison.Ordinal))
			return;

		_lastShowSaveDirectory = trimmed;
		GD.Print($"UserDataManager:SetLastShowSaveDirectory - {trimmed}");
		if (persistImmediately)
			SaveUserData();
	}

	/// <summary>
	/// Adds the specified show file path to the top of the recent files list.
	/// Duplicates are moved to the top rather than added again. The list is trimmed
	/// to MaxRecentShowFiles.
	/// </summary>
	/// <param name="path">The absolute filesystem path to the .c2 show file.</param>
	/// <param name="persistImmediately">
	/// When true (default), writes user data to disk now. Showfile open passes false
	/// and persists from deferred housekeeping so the write is off the apply path.
	/// </param>
	public void AddRecentShowFile(string path, bool persistImmediately = true)
	{
		if (string.IsNullOrWhiteSpace(path))
		{
			GD.Print("UserDataManager:AddRecentShowFile - Ignored empty path.");
			return;
		}

		string norm = NormalizeForRecent(path);

		// Remove ALL existing entries that match (handles case, separators, relative paths etc.)
		_recentShowFiles.RemoveAll(p => NormalizeForRecent(p) == norm);

		// Insert the provided path at front (most recent). We could store norm instead, but keep caller's form.
		_recentShowFiles.Insert(0, path);

		// Trim to limit
		while (_recentShowFiles.Count > MaxRecentShowFiles)
		{
			_recentShowFiles.RemoveAt(_recentShowFiles.Count - 1);
		}

		GD.Print($"UserDataManager:AddRecentShowFile - Added recent: {path} (total: {_recentShowFiles.Count})");
		_globalSignals.EmitSignal(nameof(GlobalSignals.Log), $"Added to recent files: {Path.GetFileName(path)}", 0);

		if (persistImmediately)
			SaveUserData();
	}

	/// <summary>
	/// Writes in-memory user data (including recents) to disk. Used after a deferred recents update.
	/// </summary>
	public void PersistUserData()
	{
		SaveUserData();
	}

	/// <summary>
	/// Returns a copy of the recent show file paths list (most recent first).
	/// </summary>
	/// <returns>A new List containing the current recent show file paths.</returns>
	public List<string> GetRecentShowFiles()
	{
		// Return deduplicated copy (using normalization) as a safety net
		var seen = new HashSet<string>();
		var result = new List<string>();
		foreach (string p in _recentShowFiles)
		{
			string n = NormalizeForRecent(p);
			if (seen.Add(n))
			{
				result.Add(p);
			}
		}
		return result;
	}

	/// <summary>
	/// Removes the specified path from the recent files list if present.
	/// Persists the change when any entry was removed.
	/// </summary>
	/// <param name="path">
	/// Path to remove. Matched via <see cref="NormalizeForRecent"/> (same as
	/// <see cref="AddRecentShowFile"/>) so case, separators, and relative/absolute
	/// forms that resolve to the same file all match.
	/// </param>
	public void RemoveRecentShowFile(string path)
	{
		if (string.IsNullOrWhiteSpace(path))
			return;

		string norm = NormalizeForRecent(path);
		if (string.IsNullOrEmpty(norm))
			return;

		// Same match rules as AddRecentShowFile — exact List.Remove fails when the
		// stored form differs by case (Windows), slash style, or GetFullPath form.
		int removed = _recentShowFiles.RemoveAll(p => NormalizeForRecent(p) == norm);
		if (removed > 0)
		{
			GD.Print(
				$"UserDataManager:RemoveRecentShowFile - Removed {removed} entr(y/ies) for: {path}");
			SaveUserData();
		}
	}

	/// <summary>
	/// Clears all recent show files and persists the change.
	/// </summary>
	public void ClearRecentShowFiles()
	{
		_recentShowFiles.Clear();
		GD.Print("UserDataManager:ClearRecentShowFiles - Cleared recent show files.");
		_globalSignals.EmitSignal(nameof(GlobalSignals.Log), "Recent files list cleared.", 0);
		SaveUserData();
	}

	/// <summary>
	/// Resets all persistent user preferences to factory defaults and overwrites
	/// <c>user://user_data.json</c>. Does not modify showfiles on disk.
	/// </summary>
	/// <remarks>
	/// Clears recent show files, window geometry, shell column widths, and custom
	/// InputMap bindings. Restores first-time startup flag and live InputMap to
	/// project defaults when <see cref="GlobalData"/> is available. Callers should
	/// re-sync dependent systems (autosave timer, log pruning, history depth, Input Map UI).
	/// </remarks>
	public void ResetToDefaults()
	{
		_recentShowFiles.Clear();

		_lastWindowSize = Vector2I.Zero;
		_lastWindowPosition = Vector2I.Zero;
		_wasMaximized = false;
		_wasFullscreen = false;

		_lastSettingsWindowSize = Vector2I.Zero;
		_lastSettingsWindowPosition = Vector2I.Zero;
		_settingsWasMaximized = false;
		_lastSettingsMenu = "General";

		_startupBehavior = DefaultStartupBehavior;
		_autosaveInterval = DefaultAutosaveInterval;
		_backupDepth = DefaultBackupDepth;
		_undoDepth = DefaultUndoDepth;
		_logSessionDepth = DefaultLogSessionDepth;
		_isFirstTimeStartup = true;
		_locale = DefaultLocale;
		_uiScale = DefaultUiScale;
		_checkForUpdatesOnStartup = true;
		_includePrereleaseUpdates = false;
		_skippedUpdateVersion = string.Empty;
		_lastUpdateCheckUtc = string.Empty;
		_registeredShowfileHandlerPath = string.Empty;

		_shellNumberColumnWidth = 0;
		_shellTimeColumnWidth = 0;
		_lastShowSaveDirectory = string.Empty;

		// Restore live keyboard shortcuts to project defaults, then clear stored bindings.
		if (_globalData != null)
		{
			_globalData.ResetInputBindingsToDefaults();
		}
		_inputMapBindings = new Dictionary();

		// Re-apply default UI locale so TranslationServer matches persisted prefs.
		_globalData?.LocalizationService?.ApplyLocale(DefaultLocale, emitSignal: true);

		SaveUserData();

		// Notify windows that user UI scale returned to default.
		_globalSignals?.EmitSignal(nameof(GlobalSignals.UiScaleChanged), _uiScale);

		GD.Print("UserDataManager:ResetToDefaults - User preferences reset to factory defaults.");
		_globalSignals?.EmitSignal(nameof(GlobalSignals.Log), "User preferences reset to defaults.", 0);
	}

	/// <summary>
	/// Updates the stored main window size, position (relative to its display), and fill-screen state.
	/// Position and size are only updated when not fill-screen. Persists immediately if changed.
	/// </summary>
	/// <param name="size">Window size in pixels.</param>
	/// <param name="position">Position relative to the display's top-left.</param>
	/// <param name="maximized">Whether the window is currently fill-screen (maximized or fullscreen).</param>
	/// <param name="fullscreen">When <paramref name="maximized"/> is true, whether that fill is fullscreen
	/// (expand button) rather than OS maximize (title double-click).</param>
	public void SetWindowState(Vector2I size, Vector2I position, bool maximized, bool fullscreen = false)
	{
		bool changed = false;

		if (maximized != _wasMaximized)
		{
			_wasMaximized = maximized;
			changed = true;
		}

		// Clear fullscreen when leaving fill-screen; otherwise track the fill kind.
		bool nextFullscreen = maximized && fullscreen;
		if (nextFullscreen != _wasFullscreen)
		{
			_wasFullscreen = nextFullscreen;
			changed = true;
		}

		if (!maximized)
		{
			if (size.X > 0 && size.Y > 0 && size != _lastWindowSize)
			{
				_lastWindowSize = size;
				changed = true;
			}
			if (position != _lastWindowPosition)
			{
				_lastWindowPosition = position;
				changed = true;
			}
		}

		if (changed)
		{
			SaveUserData();
		}
	}

	/// <summary>
	/// Updates the in-memory Settings window size, position (relative to its display), and maximized state.
	/// Position and size are only updated when not maximized.
	/// </summary>
	/// <remarks>
	/// Does not write to disk immediately — geometry is held in memory for the session and written
	/// with the rest of user data on app exit (or the next explicit <see cref="SaveUserData"/>).
	/// Load from file happens once at startup via <see cref="LoadUserData"/>.
	/// </remarks>
	/// <param name="size">Window size in pixels.</param>
	/// <param name="position">Position relative to the display's top-left.</param>
	/// <param name="maximized">Whether the window is currently maximized.</param>
	public void SetSettingsWindowState(Vector2I size, Vector2I position, bool maximized)
	{
		if (maximized != _settingsWasMaximized)
		{
			_settingsWasMaximized = maximized;
		}

		if (!maximized)
		{
			if (size.X > 0 && size.Y > 0 && size != _lastSettingsWindowSize)
			{
				_lastSettingsWindowSize = size;
			}
			if (position != _lastSettingsWindowPosition)
			{
				_lastSettingsWindowPosition = position;
			}
		}
	}

	/// <summary>
	/// Updates the in-memory last Settings sub-menu (tree item label).
	/// Disk is written with the rest of user data on app exit.
	/// </summary>
	/// <param name="menuKey">Settings tree item text, e.g. "Canvas Editor".</param>
	public void SetSettingsMenu(string menuKey)
	{
		if (string.IsNullOrWhiteSpace(menuKey))
		{
			return;
		}

		if (_lastSettingsMenu != menuKey)
		{
			_lastSettingsMenu = menuKey;
		}
	}

	/// <summary>
	/// Loads persistent user data from user://user_data.json.
	/// If the file does not exist or cannot be parsed, starts with an empty recent list.
	/// </summary>
	private void LoadUserData()
	{
		_recentShowFiles.Clear();

		if (!Godot.FileAccess.FileExists(UserDataFilePath))
		{
			GD.Print("UserDataManager:LoadUserData - No existing user data file found. Starting fresh.");
			return;
		}

		try
		{
			using var file = Godot.FileAccess.Open(UserDataFilePath, Godot.FileAccess.ModeFlags.Read);
			if (file == null)
			{
				Error err = Godot.FileAccess.GetOpenError();
				GD.PrintErr($"UserDataManager:LoadUserData - Failed to open user data file: {err}");
				_globalSignals.EmitSignal(nameof(GlobalSignals.Log), "Failed to read user data file.", 2);
				return;
			}

			string jsonString = file.GetAsText();
			file.Close();

			if (string.IsNullOrWhiteSpace(jsonString))
			{
				return;
			}

			using var json = new Json();
			Error parseErr = json.Parse(jsonString);
			if (parseErr != Error.Ok)
			{
				GD.PrintErr($"UserDataManager:LoadUserData - JSON parse error: {parseErr}");
				_globalSignals.EmitSignal(nameof(GlobalSignals.Log), "User data file is corrupted; starting fresh.", 2);
				return;
			}

			var data = json.Data.AsGodotDictionary();
			if (data == null)
			{
				return;
			}

			if (data.TryGetValue("RecentShowFiles", out var recentsValue))
			{
				var recentsArray = recentsValue.AsGodotArray();
				foreach (var item in recentsArray)
				{
					string p = item.AsString();
					if (!string.IsNullOrWhiteSpace(p) && !_recentShowFiles.Contains(p))
					{
						_recentShowFiles.Add(p);
					}
				}
			}

			// Deduplicate any legacy entries using path normalization (case, separators, etc.)
			var seen = new HashSet<string>();
			var deduped = new List<string>();
			foreach (string p in _recentShowFiles)
			{
				string n = NormalizeForRecent(p);
				if (!seen.Contains(n))
				{
					seen.Add(n);
					deduped.Add(p);
				}
			}
			int beforeDedup = _recentShowFiles.Count;
			_recentShowFiles = deduped;

			if (_recentShowFiles.Count < beforeDedup)
			{
				// Persist the deduplicated list
				SaveUserData();
			}

			// Window state
			if (data.TryGetValue("LastWindowSize", out var sizeVal))
			{
				var sd = sizeVal.AsGodotDictionary();
				if (sd != null)
				{
					int w = sd.TryGetValue("width", out var wv) ? wv.AsInt32() : 0;
					int h = sd.TryGetValue("height", out var hv) ? hv.AsInt32() : 0;
					if (w > 0 && h > 0)
					{
						_lastWindowSize = new Vector2I(w, h);
					}
				}
			}
			if (data.TryGetValue("LastWindowPosition", out var posVal))
			{
				var pd = posVal.AsGodotDictionary();
				if (pd != null)
				{
					int x = pd.TryGetValue("x", out var xv) ? xv.AsInt32() : 0;
					int y = pd.TryGetValue("y", out var yv) ? yv.AsInt32() : 0;
					_lastWindowPosition = new Vector2I(x, y);
				}
			}
			if (data.TryGetValue("WasMaximized", out var maxVal))
			{
				_wasMaximized = maxVal.AsBool();
			}
			if (data.TryGetValue("WasFullscreen", out var fsVal))
			{
				_wasFullscreen = fsVal.AsBool();
			}
			else
			{
				// Pre-split prefs: treat fill-screen as maximize (not expand-fullscreen).
				_wasFullscreen = false;
			}

			// Settings window state
			if (data.TryGetValue("LastSettingsWindowSize", out var settingsSizeVal))
			{
				var sd = settingsSizeVal.AsGodotDictionary();
				if (sd != null)
				{
					int w = sd.TryGetValue("width", out var wv) ? wv.AsInt32() : 0;
					int h = sd.TryGetValue("height", out var hv) ? hv.AsInt32() : 0;
					if (w > 0 && h > 0)
					{
						_lastSettingsWindowSize = new Vector2I(w, h);
					}
				}
			}
			if (data.TryGetValue("LastSettingsWindowPosition", out var settingsPosVal))
			{
				var pd = settingsPosVal.AsGodotDictionary();
				if (pd != null)
				{
					int x = pd.TryGetValue("x", out var xv) ? xv.AsInt32() : 0;
					int y = pd.TryGetValue("y", out var yv) ? yv.AsInt32() : 0;
					_lastSettingsWindowPosition = new Vector2I(x, y);
				}
			}
			if (data.TryGetValue("SettingsWasMaximized", out var settingsMaxVal))
			{
				_settingsWasMaximized = settingsMaxVal.AsBool();
			}
			if (data.TryGetValue("LastSettingsMenu", out var settingsMenuVal))
			{
				string menu = settingsMenuVal.AsString();
				if (!string.IsNullOrWhiteSpace(menu))
				{
					_lastSettingsMenu = menu;
				}
			}

			if (data.TryGetValue("StartupBehavior", out var startupVal))
			{
				int val = startupVal.AsInt32();
				_startupBehavior = (StartupBehavior)val;
			}

			if (data.TryGetValue("AutosaveInterval", out var autoVal))
			{
				_autosaveInterval = Math.Max(0, autoVal.AsInt32());
			}
			if (data.TryGetValue("BackupDepth", out var depthVal))
			{
				_backupDepth = Math.Max(1, depthVal.AsInt32());
			}
			if (data.TryGetValue("UndoDepth", out var undoDepthVal))
			{
				_undoDepth = Math.Clamp(undoDepthVal.AsInt32(), MinUndoDepth, MaxUndoDepth);
			}
			if (data.TryGetValue("LogSessionDepth", out var logSessionDepthVal))
			{
				_logSessionDepth = Math.Clamp(logSessionDepthVal.AsInt32(), MinLogSessionDepth, MaxLogSessionDepth);
			}

			// Missing key = legacy install; treat as already past first-time welcome.
			if (data.TryGetValue("IsFirstTimeStartup", out var firstTimeVal))
			{
				_isFirstTimeStartup = firstTimeVal.AsBool();
			}
			else
			{
				_isFirstTimeStartup = false;
			}

			// Missing key = legacy install; default to English.
			if (data.TryGetValue("Locale", out var localeVal))
			{
				string loadedLocale = localeVal.AsString();
				_locale = string.IsNullOrWhiteSpace(loadedLocale)
					? DefaultLocale
					: loadedLocale.Trim().ToLowerInvariant();
			}
			else
			{
				_locale = DefaultLocale;
			}

			// Missing key = legacy install (scale used to live in show Settings); default 100%.
			if (data.TryGetValue("UiScale", out var uiScaleVal))
			{
				_uiScale = Mathf.Clamp(uiScaleVal.AsSingle(), MinUiScale, MaxUiScale);
			}
			else
			{
				_uiScale = DefaultUiScale;
			}

			if (data.TryGetValue("CheckForUpdatesOnStartup", out var checkUpdVal))
				_checkForUpdatesOnStartup = checkUpdVal.AsBool();
			else
				_checkForUpdatesOnStartup = true;

			if (data.TryGetValue("IncludePrereleaseUpdates", out var preVal))
				_includePrereleaseUpdates = preVal.AsBool();
			else
				_includePrereleaseUpdates = false;

			_skippedUpdateVersion = data.TryGetValue("SkippedUpdateVersion", out var skipVal)
				? skipVal.AsString() ?? string.Empty
				: string.Empty;
			_lastUpdateCheckUtc = data.TryGetValue("LastUpdateCheckUtc", out var lastChkVal)
				? lastChkVal.AsString() ?? string.Empty
				: string.Empty;
			_registeredShowfileHandlerPath = data.TryGetValue("RegisteredShowfileHandlerPath", out var assocVal)
				? assocVal.AsString() ?? string.Empty
				: string.Empty;

			if (data.TryGetValue("ShellNumberColumnWidth", out var shellNumW))
			{
				_shellNumberColumnWidth = shellNumW.AsSingle();
			}
			if (data.TryGetValue("ShellTimeColumnWidth", out var shellTimeW))
			{
				_shellTimeColumnWidth = shellTimeW.AsSingle();
			}

			if (data.TryGetValue("LastShowSaveDirectory", out var lastSaveDirVal))
			{
				string lastSaveDir = lastSaveDirVal.AsString();
				_lastShowSaveDirectory = string.IsNullOrWhiteSpace(lastSaveDir)
					? string.Empty
					: lastSaveDir.Trim().Replace('\\', '/').TrimEnd('/');
			}

			// Custom keyboard shortcuts (Cue2 Preferences → Input Map)
			if (data.TryGetValue("InputMap", out var inputMapVal))
			{
				var mapDict = inputMapVal.AsGodotDictionary();
				if (mapDict != null && mapDict.Count > 0)
					_inputMapBindings = mapDict;
			}

			// Legacy keys VideoQualityMode / HardwareDecodePreference / VideoPreviewQuality /
			// OutputVSyncMode were machine prefs; they now live in the showfile (Settings). Ignore if present.

			// Future: version handling, other user prefs can be loaded here.
			GD.Print($"UserDataManager:LoadUserData - Loaded {_recentShowFiles.Count} recent show file(s). Window size:{_lastWindowSize} pos(rel):{_lastWindowPosition} maximized:{_wasMaximized} settings size:{_lastSettingsWindowSize} pos(rel):{_lastSettingsWindowPosition} settingsMax:{_settingsWasMaximized} startup:{_startupBehavior} firstTime:{_isFirstTimeStartup} locale:{_locale} uiScale:{_uiScale} autosave:{_autosaveInterval}m backups:{_backupDepth} undoDepth:{_undoDepth} logSessionDepth:{_logSessionDepth} inputMapKeys:{_inputMapBindings?.Count ?? 0} lastSaveDir:{_lastShowSaveDirectory}");
		}
		catch (Exception ex)
		{
			GD.PrintErr($"UserDataManager:LoadUserData - Error loading user data: {ex.Message}");
			_globalSignals.EmitSignal(nameof(GlobalSignals.Log), $"Error loading user preferences: {ex.Message}", 2);
			_recentShowFiles.Clear();
		}
	}

	/// <summary>
	/// Persists the current user data (including recent show files) to user://user_data.json.
	/// Creates or overwrites the file using Godot FileAccess.
	/// </summary>
	private void SaveUserData()
	{
		try
		{
			var data = new Dictionary();
			var recentsArray = new Godot.Collections.Array();

			foreach (string path in _recentShowFiles)
			{
				recentsArray.Add(path);
			}

			data["RecentShowFiles"] = recentsArray;

			// Window state
			var winSize = new Dictionary();
			winSize["width"] = _lastWindowSize.X;
			winSize["height"] = _lastWindowSize.Y;
			data["LastWindowSize"] = winSize;

			var winPos = new Dictionary();
			winPos["x"] = _lastWindowPosition.X;
			winPos["y"] = _lastWindowPosition.Y;
			data["LastWindowPosition"] = winPos;

			data["WasMaximized"] = _wasMaximized;
			data["WasFullscreen"] = _wasFullscreen;

			// Settings window state
			var settingsWinSize = new Dictionary();
			settingsWinSize["width"] = _lastSettingsWindowSize.X;
			settingsWinSize["height"] = _lastSettingsWindowSize.Y;
			data["LastSettingsWindowSize"] = settingsWinSize;

			var settingsWinPos = new Dictionary();
			settingsWinPos["x"] = _lastSettingsWindowPosition.X;
			settingsWinPos["y"] = _lastSettingsWindowPosition.Y;
			data["LastSettingsWindowPosition"] = settingsWinPos;

			data["SettingsWasMaximized"] = _settingsWasMaximized;
			data["LastSettingsMenu"] = _lastSettingsMenu ?? "General";

			data["StartupBehavior"] = (int)_startupBehavior;
			data["IsFirstTimeStartup"] = _isFirstTimeStartup;
			data["Locale"] = string.IsNullOrWhiteSpace(_locale) ? DefaultLocale : _locale;
			data["UiScale"] = _uiScale;
			data["CheckForUpdatesOnStartup"] = _checkForUpdatesOnStartup;
			data["IncludePrereleaseUpdates"] = _includePrereleaseUpdates;
			data["SkippedUpdateVersion"] = _skippedUpdateVersion ?? string.Empty;
			data["LastUpdateCheckUtc"] = _lastUpdateCheckUtc ?? string.Empty;
			data["RegisteredShowfileHandlerPath"] = _registeredShowfileHandlerPath ?? string.Empty;

			data["AutosaveInterval"] = _autosaveInterval;
			data["BackupDepth"] = _backupDepth;
			data["UndoDepth"] = _undoDepth;
			data["LogSessionDepth"] = _logSessionDepth;

			if (_shellNumberColumnWidth > 0)
				data["ShellNumberColumnWidth"] = _shellNumberColumnWidth;
			if (_shellTimeColumnWidth > 0)
				data["ShellTimeColumnWidth"] = _shellTimeColumnWidth;

			if (!string.IsNullOrEmpty(_lastShowSaveDirectory))
				data["LastShowSaveDirectory"] = _lastShowSaveDirectory;

			// Live InputMap snapshot (or last loaded if GlobalData not ready)
			if (_globalData != null)
				_inputMapBindings = _globalData.GetCustomInputBindings();
			if (_inputMapBindings != null)
				data["InputMap"] = _inputMapBindings;
			else
				data["InputMap"] = new Dictionary();

			data["Version"] = 1;
			// Additional user-persistent keys can be added here in the future.

			string jsonString = Json.Stringify(data);

			using var file = Godot.FileAccess.Open(UserDataFilePath, Godot.FileAccess.ModeFlags.Write);
			if (file == null)
			{
				Error err = Godot.FileAccess.GetOpenError();
				GD.PrintErr($"UserDataManager:SaveUserData - Failed to open file for writing: {err}");
				_globalSignals.EmitSignal(nameof(GlobalSignals.Log), "Failed to save user data.", 2);
				return;
			}

			file.StoreString(jsonString);
			file.Close();

			GD.Print("UserDataManager:SaveUserData - User data saved successfully.");
		}
		catch (Exception ex)
		{
			GD.PrintErr($"UserDataManager:SaveUserData - Error saving user data: {ex.Message}");
			_globalSignals.EmitSignal(nameof(GlobalSignals.Log), $"Error saving user preferences: {ex.Message}", 2);
		}
	}

	public override void _ExitTree()
	{
		// Final safety save in case any in-memory changes were not persisted.
		SaveUserData();
		GD.Print("UserDataManager:_ExitTree - Final user data save attempted.");
	}
}
