// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Cue2.App;
using Cue2.UI.Shell;
using Cue2.Services;
using Cue2.Domain.Cuelist;
using Cue2.Domain.Playback;
using Cue2.Domain.Devices;
using Cue2.Domain.ShowSettings;
using Cue2.Domain.Metadata;
using Cue2.Domain.Cues;
using Cue2.Domain.Connections;
using Cue2.Domain.Library;
using Cue2.Domain.Commands;
using Godot;
using Godot.Collections;
using SDL3;
using Cue2.UI.Popups;
using Cue2.UI.Utilities;

namespace Cue2.Services;

/// <summary>
/// Autoload service locator and shared session state for Cue2.
/// </summary>
/// <remarks>
/// <para>
/// Owned as a Godot autoload under <c>/root/GlobalData</c>. At startup (<see cref="_Ready"/>) it
/// initializes SDL, creates core child services (settings, devices, history, library, localization,
/// shell selection, command execution), applies user preferences (locale, input map, open-last show),
/// and exposes paths and counters used across UI and domain layers.
/// </para>
/// <para>
/// Holds live document/session state such as the active <see cref="Cuelist"/>, focused cue,
/// session media directories, and showfile path.
/// </para>
/// </remarks>
public partial class GlobalData : Node
{
	private GlobalSignals _globalSignals;
	private SaveManager _saveManager;

	/// <summary>
	/// True while a showfile is being applied to the live session (after the version gate).
	/// New/Open/Save and document edits should be refused until this is false.
	/// GO uses <see cref="IsPlaybackReady"/> (true after cue models exist).
	/// </summary>
	public bool IsSessionLoading => _saveManager?.IsSessionLoading == true;

	/// <summary>
	/// False only while settings/cue models are still being applied. True after models exist
	/// (and whenever no open is in flight) so GO can fire before the first shell bind finishes.
	/// </summary>
	public bool IsPlaybackReady => _saveManager == null || _saveManager.IsPlaybackReady;

	/// <summary>
	/// Captured at startup from the project.godot [input] definitions. Used to restore "factory" bindings on New Session.
	/// </summary>
	private System.Collections.Generic.Dictionary<string, Godot.Collections.Array<InputEvent>> _defaultInputBindings = new();

	/// <summary>
	/// Live show cuelist (document model). Created/replaced by session load and cuelist rebuild paths.
	/// </summary>
	public CueList Cuelist;

	/// <summary>
	/// Selection and focus helpers for shell rows (multi-select, next/previous, GO target).
	/// </summary>
	public ShellSelection ShellSelection;

	/// <summary>
	/// Executes cue/control commands (GO, stop, pause, resume, and related command routing).
	/// </summary>
	public CueCommandExecutor CueCommandExecutor;
	
	/// <summary>
	/// Showfile settings (general, audio/video defaults, patches, input maps, displays data).
	/// </summary>
	public Settings Settings;

	/// <summary>
	/// Open hardware/device managers (audio devices and related device lifecycle).
	/// </summary>
	public Devices Devices;

	/// <summary>
	/// Cue light connection registry and transport for physical cue-light devices.
	/// </summary>
	public CueLightManager CueLightManager;

	/// <summary>
	/// Autoload that owns video output windows, screens, and target layers.
	/// </summary>
	public DisplaysManager DisplaysManager;

	/// <summary>
	/// Handles OS file-drop events into the main UI (import / create cues from media).
	/// </summary>
	public FileDropper FileDropper;

	/// <summary>
	/// App-scoped preferences in <c>user://</c> (locale, keyboard Input Map, recents, startup behavior).
	/// Not part of the showfile and not undo-tracked.
	/// </summary>
	public UserDataManager UserDataManager;

	/// <summary>
	/// GitHub Releases checker / downloader (exported builds). Prefs in <see cref="UserDataManager"/>.
	/// </summary>
	public UpdateService UpdateService;

	/// <summary>
	/// Scoped momento undo/redo stacks for document edits (cue, cuelist, settings).
	/// </summary>
	public HistoryManager HistoryManager;

	/// <summary>
	/// Cue library filesystem I/O (save/load library entries outside the show cuelist).
	/// </summary>
	public CueLibraryManager CueLibraryManager;

	/// <summary>
	/// Application localization service (CSV catalogs + <see cref="TranslationServer"/>).
	/// </summary>
	public LocalizationService LocalizationService;

	/// <summary>
	/// Id of the cue currently focused for inspectors (-1 if none).
	/// Kept in sync via <see cref="GlobalSignals.ShellFocused"/>.
	/// </summary>
	public int FocusedCue = -1;

	/// <summary>
	/// Updates <see cref="FocusedCue"/> when shell focus changes.
	/// </summary>
	/// <param name="cueId">Focused cue id, or -1 when cleared.</param>
	private void OnShellFocused(int cueId)
	{
		FocusedCue = cueId;
	}

	/// <summary>
	/// Legacy cue counter; prefer <see cref="CueTotal"/> or <see cref="CueList.TotalCueCount"/> for UI totals.
	/// </summary>
	public int CueCount; // TODO: If possible delete this in favour of CueTotal

	/// <summary>
	/// Total number of cues in the show (including nested group children). Kept in sync by <see cref="CueList"/>.
	/// </summary>
	/// <value>Non-negative count of cues currently in the cuelist.</value>
	public int CueTotal;

	/// <summary>
	/// Legacy ordering counter; visual/list order is owned by <see cref="CueList"/>.
	/// </summary>
	public int CueOrder; // TODO: This may no longer be needed, if possible remove

	/// <summary>
	/// Cue id that is next to be manually GO'd, or -1 if none is armed as next.
	/// </summary>
	/// <value>Valid cue id, or -1 when no next cue is set.</value>
	public int NextCue = -1;

	/// <summary>
	/// Count of open video output windows (bookkeeping for multi-window presentation).
	/// </summary>
	public int VideoOutputWinNum;

	/// <summary>
	/// Count of open UI/utility windows (bookkeeping for non-output chrome windows).
	/// </summary>
	public int UiOutputWinNum;

	/// <summary>
	/// Absolute path of a showfile to open after app chrome is ready.
	/// Set from a <c>.c2</c> command-line argument (wins) or Open last showfile recents.
	/// Null/empty when neither applies so boot seeds a blank new session.
	/// </summary>
	public string StartupOpenPath;

	private SingleInstanceGuard _instanceGuard;
	
	/// <summary>
	/// OS / HiDPI display scale of the window's screen (see <see cref="UiUtilities.DetectDisplayScale"/>).
	/// Combined with user <see cref="UserDataManager.UiScale"/> when rescaling windows.
	/// Re-read after the display server settles — macOS often reports 1× during autoload <c>_Ready</c>.
	/// </summary>
	/// <value>Positive scale factor in 1–4; defaults to 1.0 when detection fails.</value>
	public float BaseDisplayScale { get; private set; } = 1.0f;
	

	/// <summary>
	/// Display name of the open session (typically the .c2 file name without extension).
	/// </summary>
	public string SessionName;

	/// <summary>
	/// Absolute path of the open showfile (<c>.c2</c>). Null when no session is saved yet.
	/// </summary>
	public string SessionPath;

	/// <summary>
	/// Absolute path to the show session folder (directory containing the .c2 file).
	/// </summary>
	public string SessionDir;

	/// <summary>
	/// Absolute path for show-local audio media (<c>SessionDir/Audio</c>).
	/// </summary>
	public string SessionAudioPath;

	/// <summary>
	/// Absolute path for show-local video media (<c>SessionDir/Video</c>).
	/// </summary>
	public string SessionVideoPath;

	/// <summary>
	/// Absolute path for show-local image media (<c>SessionDir/Images</c>).
	/// </summary>
	public string SessionImagesPath;

	/// <summary>
	/// Absolute path for waveform cache files (<c>SessionDir/Waveforms</c>).
	/// </summary>
	public string SessionWaveformsPath;

	/// <summary>
	/// Last busy status string shown on the footer progress bar (not persisted).
	/// Used by quit confirmation to name the in-flight task.
	/// </summary>
	public string FooterBackgroundTaskStatus { get; set; } = string.Empty;

	/// <summary>
	/// Resolves a cue media path that may be show-relative (e.g. <c>Audio/song.wav</c>) to an absolute path.
	/// Absolute paths are returned normalized when possible.
	/// </summary>
	/// <param name="storedPath">Path as stored on a cue component.</param>
	/// <returns>Absolute filesystem path for I/O, or the input if it cannot be resolved.</returns>
	public string ResolveMediaPath(string storedPath) => MediaPaths.Resolve(storedPath, SessionDir);

	/// <summary>
	/// The absolute filesystem path that Godot resolves "user://" to.
	/// Useful for debugging or storing app-specific data in the user data directory.
	/// </summary>
	public string GodotUserDataPath { get; private set; }
	
	/// <summary>
	/// Glob patterns for video containers / elementary streams accepted by file dialogs and drop import.
	/// </summary>
	/// <remarks>
	/// <para>
	/// Extension lists gate UI only (browse/drop). Decode depends on the FFmpeg shared libraries
	/// loaded from <c>bin/{platform}/</c> (see <c>MediaEngine</c> / <c>docs/export-packaging.md</c>).
	/// </para>
	/// <para>
	/// Current project natives are FFmpeg 9.x shared LGPL builds (macOS often Homebrew-linked).
	/// Lists below are demuxer-backed for that class of build — not a promise that every
	/// codec variant will open on every machine.
	/// </para>
	/// <para>
	/// Keep disjoint from <see cref="AudioFileFilters"/> and <see cref="ImageFileFilters"/> so drop
	/// classification stays unambiguous (audio is checked first in <c>FileDropper</c>).
	/// </para>
	/// </remarks>
	public static readonly List<string> VideoFileFilters = new List<string> {
		// Common containers (mov/mp4 family, matroska/webm, avi, windows media, ogg video)
		"*.mp4", "*.m4v", "*.mov", "*.qt", "*.avi", "*.mkv", "*.webm", "*.flv", "*.f4v",
		"*.wmv", "*.asf", "*.ogv", "*.ogm", "*.rm", "*.rmvb", "*.divx", "*.xvid",
		// Broadcast / tape / optical / interchange
		"*.mpg", "*.mpeg", "*.mpe", "*.m1v", "*.m2v", "*.mp2v", "*.ts", "*.m2ts", "*.m2t",
		"*.mts", "*.vob", "*.mxf", "*.gxf", "*.lxf", "*.dv", "*.dif",
		// Mobile / DASH-ish / raw frame wrappers
		"*.3gp", "*.3g2", "*.ismv", "*.y4m",
		// Elementary / annex-B style streams (extension selects demuxer)
		"*.h264", "*.264", "*.h265", "*.hevc", "*.265", "*.av1", "*.ivf",
		// Legacy Windows TV recorder
		"*.wtv",
	};

	/// <summary>
	/// Glob patterns for still-image formats accepted by file dialogs and drop import.
	/// </summary>
	/// <remarks>
	/// Still images use the video-component path with <c>IsImage</c> and are decoded via FFmpeg
	/// (image2 / pipe demuxers + still decoders). Animated GIF/APNG are treated as still holds,
	/// not motion video. HEIC/HEIF are omitted — this project's FFmpeg build has no heif demuxer.
	/// </remarks>
	public static readonly List<string> ImageFileFilters = new List<string> {
		// Widespread (verified open with project FFmpeg 9.x)
		"*.png", "*.apng", "*.jpg", "*.jpeg", "*.jpe", "*.jfif", "*.bmp", "*.gif", "*.webp",
		"*.tif", "*.tiff", "*.tga", "*.svg",
		// HDR / film sequence stills
		"*.exr", "*.hdr", "*.dpx", "*.dds",
		// Modern still codecs present in full FFmpeg 9 builds (libjxl / jpeg2000 / av1 still)
		"*.avif", "*.jxl", "*.jp2", "*.j2k", "*.jpf",
		// Netpbm / misc stills demuxed by image2
		"*.ico", "*.qoi", "*.pbm", "*.pgm", "*.ppm", "*.pnm", "*.pcx", "*.fits",
	};

	/// <summary>
	/// Glob patterns for audio formats accepted by file dialogs and drop import.
	/// </summary>
	/// <remarks>
	/// Prefer audio-primary extensions only (e.g. <c>.m4a</c> / <c>.mka</c>, not <c>.mp4</c> / <c>.mkv</c>).
	/// MIDI is not listed — it uses the separate MIDI subsystem. Raw extension-less PCM (e.g. bare
	/// <c>.pcm</c>) is omitted because avformat cannot probe sample format without extra options.
	/// Broadcast Wave / RF64 are normally <c>.wav</c> containers, not separate demuxers here.
	/// </remarks>
	public static readonly List<string> AudioFileFilters = new List<string> {
		// PCM / lossless containers
		"*.wav", "*.wave", "*.w64", "*.aiff", "*.aif", "*.aifc",
		"*.flac", "*.alac", "*.ape", "*.wv", "*.tta", "*.caf",
		// Lossy / common delivery
		"*.mp3", "*.mp2", "*.mpa", "*.aac", "*.m4a", "*.m4b", "*.ogg", "*.oga", "*.opus",
		"*.wma", "*.spx",
		// Broadcast / surround elementary
		"*.ac3", "*.eac3", "*.ec3", "*.dts", "*.dtshd", "*.truehd", "*.thd", "*.mlp",
		// Other containers / speech / DSD
		"*.mka", "*.au", "*.snd", "*.ra",
		"*.amr", "*.awb", "*.gsm", "*.3ga", "*.voc", "*.dsf",
	};

	/// <summary>
	/// Comma-joined union of <see cref="VideoFileFilters"/>, <see cref="ImageFileFilters"/>, and
	/// <see cref="AudioFileFilters"/> for multi-type file dialog filters.
	/// </summary>
	public static readonly string AllSupportedFileFilters = string.Join(",", VideoFileFilters.Concat(ImageFileFilters).Concat(AudioFileFilters));

	/// <summary>
	/// List of input actions whose bindings can be customized by the user and persisted in user preferences
	/// (<c>user://user_data.json</c>), not in the showfile.
	/// </summary>
	public static readonly string[] MappableInputActions =
	{
		"NewSession",
		"OpenSession",
		"SaveSession",
		"SaveAsSession",
		"Go",
		"StopAll",
		"CreateCue",
		"GroupSelectedCues",
		"SelectAll",
		"SelectNext",
		"SelectPrevious",
		"PauseAll",
		"ResumeAll",
		"ToggleSettings",
		"ToggleLog",
		"ToggleShowMode",
		"EditMode",
		"ShowMode",
		"ExpandOneLayer",
		"CollapseOneLayer",
		"ToggleExpandAll",
		"DeleteCue",
		"DuplicateSelectedCues",
		"CutSelectedCues",
		"CopySelectedCues",
		"PasteCues",
		"Undo",
		"Redo"
	};

	/// <summary>
	/// Display categories for the Input Map settings panel (order is UI order).
	/// Actions not listed here still appear under "Other" when discovered at runtime.
	/// </summary>
	public static readonly (string Category, string[] Actions)[] MappableInputActionCategories =
	{
		("Session", new[] { "NewSession", "OpenSession", "SaveSession", "SaveAsSession" }),
		("Playback", new[] { "Go", "StopAll", "PauseAll", "ResumeAll" }),
		("Cue Editing", new[] { "CreateCue", "GroupSelectedCues", "DeleteCue", "DuplicateSelectedCues", "CutSelectedCues", "CopySelectedCues", "PasteCues" }),
		("Navigation", new[] { "SelectAll", "SelectNext", "SelectPrevious", "ExpandOneLayer", "CollapseOneLayer", "ToggleExpandAll" }),
		("Windows", new[] { "ToggleSettings", "ToggleLog", "ToggleShowMode", "EditMode", "ShowMode" }),
		("History", new[] { "Undo", "Redo" }),
	};


	/// <summary>
	/// Initializes SDL, child services, display scale, localization, startup open path, and factory input bindings.
	/// </summary>
	/// <remarks>
	/// Order matters: library before SDL consumers; <see cref="UserDataManager"/> before localization;
	/// default input capture before applying user rebinds.
	/// </remarks>
	public override void _Ready()
	{
		// Earliest C# we own — process clock for last-show "time to GO" including boot.
		SessionLoadTimer.Touch();

		string cliShow = ShowfileLaunchArgs.GetShowfileFromCommandLine();
		if (!SingleInstanceGuard.TryClaimExclusive(cliShow))
		{
			GetTree()?.Quit();
			return;
		}

		_globalSignals = GetNode<GlobalSignals>("/root/GlobalSignals");
		_saveManager = GetNode<SaveManager>("/root/SaveManager");

		// Track focused cue for history restore and cross-system queries.
		_globalSignals.ShellFocused += OnShellFocused;

		// Print the full resolved path for user:// (Godot's user data directory) as early as possible
		GodotUserDataPath = ProjectSettings.GlobalizePath("user://");
		GD.Print("GlobalData:_Ready - Godot user:// resolves to full path: " + GodotUserDataPath);
		_globalSignals.EmitSignal(nameof(GlobalSignals.Log), $"Godot user:// full path: {GodotUserDataPath}", 0);

		// Library is pure filesystem I/O — create before SDL so inspectors never race a late null.
		CueLibraryManager = new CueLibraryManager();
		CueLibraryManager.Name = nameof(CueLibraryManager);
		AddChild(CueLibraryManager);

		// Initialize SDL with audio, events, and video
		if (SDL.Init(SDL.InitFlags.Audio | SDL.InitFlags.Events | SDL.InitFlags.Video) == false)
		{
			var errorMsg = $"SDL Init failed: {SDL.GetError()}";
			GD.Print("GlobalData:_Ready - " + errorMsg);
			_globalSignals.EmitSignal(nameof(GlobalSignals.Log), errorMsg, 3);
			return;
		}
		GD.Print("GlobalData:_Ready - SDL initialized successfully.");

		ShellSelection = new ShellSelection();
		AddChild(ShellSelection);
		
		CueCommandExecutor = new CueCommandExecutor();
		AddChild(CueCommandExecutor);
		
		Settings = new Settings();
		AddChild(Settings);

		Devices = new Devices();
		AddChild(Devices);
		
		CueLightManager = new CueLightManager();
		AddChild(CueLightManager);
		
		FileDropper = new FileDropper();
		AddChild(FileDropper);

		UserDataManager = new UserDataManager();
		AddChild(UserDataManager);

		_instanceGuard = new SingleInstanceGuard();
		_instanceGuard.Name = nameof(SingleInstanceGuard);
		AddChild(_instanceGuard);
		_instanceGuard.OpenShowRequested += OnIpcOpenShow;

		ShowfileAssociation.MaybeRegisterOnFirstLaunch();
		if (ShowfileAssociation.IsRegisteredToThisBuild())
			UserDataManager.RegisteredShowfileHandlerPath = ShowfileAssociation.HostExecutablePath;

		UpdateService = new UpdateService();
		AddChild(UpdateService);

		// Localization after user prefs so the saved Locale is available to apply.
		LocalizationService = new LocalizationService();
		LocalizationService.Name = nameof(LocalizationService);
		AddChild(LocalizationService);
		LocalizationService.Initialize();

		HistoryManager = new HistoryManager();
		AddChild(HistoryManager);

		DisplaysManager = GetNode<DisplaysManager>("/root/DisplaysManager");

		// First read is often too early on macOS HiDPI (window not mapped yet → 1×).
		// Deferred refresh re-applies after the display server has a real screen.
		RefreshBaseDisplayScale(notifyUi: false);
		CallDeferred(nameof(DeferredRefreshDisplayScale));

		// Startup show: CLI .c2 wins, else Cue2 Preferences → Open last showfile (recents).
		if (!string.IsNullOrEmpty(cliShow) && File.Exists(cliShow))
		{
			StartupOpenPath = cliShow;
			var bootTimer = SessionLoadTimer.Start(StartupOpenPath);
			bootTimer.IncludesBoot = true;
			bootTimer.Begin("boot.globaldata");
			GD.Print("GlobalData:_Ready - Opening showfile from command line: " + StartupOpenPath);
		}
		else if (UserDataManager != null)
		{
			if (UserDataManager.Startup == UserDataManager.StartupBehavior.OpenLastShowfile)
			{
				var recents = UserDataManager.GetRecentShowFiles();
				if (recents.Count > 0)
				{
					string lastPath = recents[0];
					if (File.Exists(lastPath))
					{
						StartupOpenPath = lastPath;
						var bootTimer = SessionLoadTimer.Start(StartupOpenPath);
						bootTimer.IncludesBoot = true;
						bootTimer.Begin("boot.globaldata");
						GD.Print("GlobalData:_Ready - Startup preference: opening last showfile: " + StartupOpenPath);
					}
					else
					{
						// Missing last-show must not start the open pipeline: DisplaysManager /
						// Settings skip default seed when StartupOpenPath is set, and the
						// boot overlay would stick with no file to apply.
						UserDataManager.RemoveRecentShowFile(lastPath);
						GD.PrintErr("GlobalData:_Ready - Last showfile is missing; starting a new session: " + lastPath);
						_globalSignals.EmitSignal(nameof(GlobalSignals.Log),
							$"Last showfile not found — started a new session: {lastPath}",
							(int)LogType.Error);
					}
				}
				else
				{
					GD.Print("GlobalData:_Ready - Startup preference set to open last, but no recent showfiles found.");
				}
			}
			else
			{
				GD.Print("GlobalData:_Ready - Startup preference: new showfile.");
			}
		}

		// Capture project.godot factory bindings first, then overlay user-customized shortcuts.
		CaptureDefaultInputBindings();
		UserDataManager?.ApplyInputMapFromUserData();

		// Remaining autoloads (signals, styles, logger, displays) run before SaveManager.
		if (!string.IsNullOrEmpty(StartupOpenPath))
			SessionLoadTimer.Current?.Begin("boot.autoloads");
	}

	/// <summary>
	/// Re-reads OS / HiDPI scale after the display server has a real screen assignment.
	/// </summary>
	/// <param name="notifyUi">
	/// When true and the value changed, emit <see cref="GlobalSignals.UiScaleChanged"/> so
	/// already-ready windows re-apply content scale.
	/// </param>
	/// <returns>True when <see cref="BaseDisplayScale"/> changed.</returns>
	public bool RefreshBaseDisplayScale(bool notifyUi = true)
	{
		int screen = UiUtilities.ResolveWindowScreen(GetWindow());
		float detected = UiUtilities.DetectDisplayScale(screen);
		if (detected <= 0f)
			detected = 1f;

		if (Mathf.IsEqualApprox(detected, BaseDisplayScale))
		{
			GD.Print($"GlobalData:RefreshBaseDisplayScale - unchanged {BaseDisplayScale:0.###} (screen={screen})");
			return false;
		}

		float previous = BaseDisplayScale;
		BaseDisplayScale = detected;
		GD.Print($"GlobalData:RefreshBaseDisplayScale - {previous:0.###} -> {detected:0.###} (screen={screen})");
		_globalSignals?.EmitSignal(nameof(GlobalSignals.Log),
			$"Display scale {previous:0.##}× → {detected:0.##}× (screen {screen})", 0);

		if (notifyUi)
		{
			float userScale = UserDataManager?.UiScale ?? UserDataManager.DefaultUiScale;
			_globalSignals?.EmitSignal(nameof(GlobalSignals.UiScaleChanged), userScale);
		}

		return true;
	}

	/// <summary>
	/// Second-pass scale read after the native window is mapped (macOS HiDPI).
	/// Registered from autoload <c>_Ready</c> so it runs before deferred window geometry.
	/// </summary>
	private void DeferredRefreshDisplayScale()
	{
		RefreshBaseDisplayScale(notifyUi: true);
	}

	/// <summary>
	/// Second process asked this instance to come to the front and optionally open a show.
	/// </summary>
	private void OnIpcOpenShow(string path)
	{
		try
		{
			Window win = GetTree()?.Root;
			if (win != null)
				DisplayServer.WindowMoveToForeground(win.GetWindowId());
		}
		catch
		{
			// ignore
		}

		if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
			return;

		_globalSignals?.EmitSignal(nameof(GlobalSignals.OpenSelectedSession), path);
	}

	/// <summary>
	/// Shuts down SDL if it was initialized during <see cref="_Ready"/>.
	/// </summary>
	public override void _ExitTree()
	{
		if (_instanceGuard != null)
		{
			_instanceGuard.OpenShowRequested -= OnIpcOpenShow;
			_instanceGuard = null;
		}

		foreach (var pair in _defaultInputBindings)
		{
			if (pair.Value == null)
				continue;
			foreach (InputEvent ev in pair.Value)
				UiUtilities.DisposeRefCounted(ev);
		}
		_defaultInputBindings.Clear();

		if (SDL.WasInit(SDL.InitFlags.Audio) != 0) SDL.Quit();
		GD.Print("GlobalData:_ExitTree - Cleaned up SDL.");
	}

	/// <summary>
	/// Returns a short Readable string for the first key binding of an InputMap action
	/// (e.g. <c>Ctrl + G</c>), for tooltips and menu chrome.
	/// </summary>
	/// <param name="action">InputMap action name (e.g. <c>Go</c>, <c>ToggleSettings</c>).</param>
	/// <returns>Formatted hotkey string, or empty if the action has no key events.</returns>
	public static string ParseHotkey(string action)
	{
		// Check if the action exists in the Input Map
		if (InputMap.HasAction(action))
		{
			// Get the list of input events for the action
			var events = InputMap.ActionGetEvents(action);

			foreach (InputEvent @event in events)
			{
				if (@event is InputEventKey keyEvent)
				{
					// Get the key and modifiers
					string keyName = GetKeyDisplayName(keyEvent.Keycode);
					bool ctrlPressed = keyEvent.CtrlPressed;
					bool shiftPressed = keyEvent.ShiftPressed;
					bool altPressed = keyEvent.AltPressed;
					bool metaPressed = keyEvent.MetaPressed;

					// Build the hotkey string
					string hotkey = "";
					if (ctrlPressed) hotkey += "Ctrl + ";
					if (shiftPressed) hotkey += "Shift + ";
					if (altPressed) hotkey += "Alt + ";
					if (metaPressed) hotkey += "Meta + ";
					hotkey += keyName;

					return hotkey;
				}
			}
		}
		return "";
	}

	/// <summary>
	/// Captures the original input bindings as defined in project.godot for all mappable actions.
	/// Must be called once early in startup before any user rebinding.
	/// </summary>
	/// <remarks>
	/// Events are duplicated into <c>_defaultInputBindings</c> so later live map edits do not mutate the factory set.
	/// </remarks>
	private void CaptureDefaultInputBindings()
	{
		_defaultInputBindings.Clear();
		foreach (var action in MappableInputActions)
		{
			if (!InputMap.HasAction(action))
				InputMap.AddAction(action);
			if (InputMap.HasAction(action))
			{
				var events = InputMap.ActionGetEvents(action);
				var clone = new Godot.Collections.Array<InputEvent>();
				foreach (InputEvent e in events)
				{
					clone.Add((InputEvent)e.Duplicate());
				}
				_defaultInputBindings[action] = clone;
			}
		}
		GD.Print($"GlobalData:CaptureDefaultInputBindings - Captured defaults for {_defaultInputBindings.Count} actions.");
	}

	/// <summary>
	/// Serializes the current state of all mappable input actions into a Dictionary
	/// suitable for user-preference storage.
	/// Empty lists are included so that "cleared" bindings are preserved.
	/// </summary>
	/// <returns>
	/// Godot dictionary keyed by action name; each value is an array of key-event dictionaries
	/// with type, keycode, physical_keycode, and modifier flags.
	/// </returns>
	public Dictionary GetCustomInputBindings()
	{
		var data = new Dictionary();
		foreach (var action in MappableInputActions)
		{
			if (!InputMap.HasAction(action)) continue;

			var events = InputMap.ActionGetEvents(action);
			var eventList = new Array();
			foreach (InputEvent ev in events)
			{
				if (ev is InputEventKey key)
				{
					var evData = new Dictionary();
					evData["type"] = "InputEventKey";
					evData["keycode"] = (int)key.Keycode;
					evData["physical_keycode"] = (int)key.PhysicalKeycode;
					evData["ctrl"] = key.CtrlPressed;
					evData["shift"] = key.ShiftPressed;
					evData["alt"] = key.AltPressed;
					evData["meta"] = key.MetaPressed;
					eventList.Add(evData);
				}
			}
			// Always include the key so cleared actions (0 events) are saved explicitly.
			data[action] = eventList;
		}
		return data;
	}

	/// <summary>
	/// Applies a previously saved set of input bindings to the live InputMap.
	/// Existing events for each matched action are erased first.
	/// </summary>
	/// <param name="bindingsData">Dictionary from user data (under the <c>InputMap</c> key).</param>
	/// <remarks>
	/// Only actions listed in <see cref="MappableInputActions"/> are updated; unknown keys are ignored.
	/// </remarks>
	public void ApplyInputBindings(Dictionary bindingsData)
	{
		if (bindingsData == null || bindingsData.Count == 0) return;

		foreach (var action in MappableInputActions)
		{
			if (!InputMap.HasAction(action)) continue;
			if (!TryGetBindingActionList(bindingsData, action, out var evList)) continue;

			InputMap.ActionEraseEvents(action);

			foreach (var item in evList)
			{
				if (item.VariantType != Variant.Type.Dictionary) continue;
				var evDict = item.AsGodotDictionary();
				if (evDict == null) continue;

				if (!TryGetDictValue(evDict, "type", out var typeVal) || typeVal.AsString() != "InputEventKey")
					continue;

				var keyEvent = new InputEventKey();

				if (TryGetDictValue(evDict, "keycode", out var kc))
					keyEvent.Keycode = (Key)kc.AsInt32();
				if (TryGetDictValue(evDict, "physical_keycode", out var pkc))
					keyEvent.PhysicalKeycode = (Key)pkc.AsInt32();
				if (TryGetDictValue(evDict, "ctrl", out var ctrl))
					keyEvent.CtrlPressed = ctrl.AsBool();
				if (TryGetDictValue(evDict, "shift", out var shift))
					keyEvent.ShiftPressed = shift.AsBool();
				if (TryGetDictValue(evDict, "alt", out var alt))
					keyEvent.AltPressed = alt.AsBool();
				if (TryGetDictValue(evDict, "meta", out var meta))
					keyEvent.MetaPressed = meta.AsBool();

				InputMap.ActionAddEvent(action, keyEvent);
			}
		}
		GD.Print("GlobalData:ApplyInputBindings - Restored custom input bindings from user preferences.");
	}

	/// <summary>
	/// Finds an action's event list in a bindings dictionary (tolerates StringName keys after JSON clone).
	/// </summary>
	/// <param name="bindingsData">Serialized InputMap dictionary from user data.</param>
	/// <param name="action">Action name to look up.</param>
	/// <param name="evList">On success, the array of event dictionaries for that action.</param>
	/// <returns><c>true</c> if the action key was found and converted to an array.</returns>
	private static bool TryGetBindingActionList(Dictionary bindingsData, string action, out Godot.Collections.Array evList)
	{
		evList = null;
		if (bindingsData == null || string.IsNullOrEmpty(action)) return false;

		if (bindingsData.TryGetValue(action, out var raw))
		{
			evList = raw.AsGodotArray();
			return true;
		}

		foreach (var k in bindingsData.Keys)
		{
			if (k.AsString() == action)
			{
				evList = bindingsData[k].AsGodotArray();
				return true;
			}
		}
		return false;
	}

	/// <summary>
	/// Reads a dictionary value by string key, tolerating StringName keys after JSON round-trips.
	/// </summary>
	/// <param name="dict">Source dictionary.</param>
	/// <param name="key">Logical string key.</param>
	/// <param name="value">Matched value when found.</param>
	/// <returns><c>true</c> if a matching key exists.</returns>
	private static bool TryGetDictValue(Dictionary dict, string key, out Variant value)
	{
		value = default;
		if (dict == null || string.IsNullOrEmpty(key)) return false;
		if (dict.TryGetValue(key, out value)) return true;
		foreach (var k in dict.Keys)
		{
			if (k.AsString() == key)
			{
				value = dict[k];
				return true;
			}
		}
		return false;
	}

	/// <summary>
	/// Restores every mappable action to the original bindings captured from project.godot at startup.
	/// Used by Input Map "reset all" style flows; does not change user-data storage until
	/// <see cref="UserDataManager.PersistLiveInputMap"/> is called.
	/// </summary>
	public void ResetInputBindingsToDefaults()
	{
		foreach (var kvp in _defaultInputBindings)
		{
			string action = kvp.Key;
			if (!InputMap.HasAction(action)) continue;

			InputMap.ActionEraseEvents(action);
			foreach (InputEvent ev in kvp.Value)
			{
				InputMap.ActionAddEvent(action, (InputEvent)ev.Duplicate());
			}
		}
		GD.Print("GlobalData:ResetInputBindingsToDefaults - Input bindings restored to project defaults.");
	}

	/// <summary>
	/// Returns a copy of the default events captured for the given action, or empty array if none.
	/// </summary>
	/// <param name="action">InputMap action name.</param>
	/// <returns>Shallow-copied array of default <see cref="InputEvent"/> instances (never null).</returns>
	public Godot.Collections.Array<InputEvent> GetDefaultInputEvents(string action)
	{
		if (string.IsNullOrEmpty(action) || !_defaultInputBindings.TryGetValue(action, out var events))
			return new Godot.Collections.Array<InputEvent>();
		// Return a shallow copy of the stored array (events are already duplicated at capture time)
		var copy = new Godot.Collections.Array<InputEvent>();
		foreach (var e in events)
			copy.Add(e);
		return copy;
	}

	/// <summary>
	/// Returns true if the current binding for the action exactly matches the captured default.
	/// </summary>
	/// <param name="action">InputMap action name.</param>
	/// <returns>
	/// <c>true</c> if bindings match (or the action/defaults are missing); otherwise <c>false</c>.
	/// </returns>
	public bool IsInputActionAtDefault(string action)
	{
		if (string.IsNullOrEmpty(action) || !InputMap.HasAction(action))
			return true;

		if (!_defaultInputBindings.TryGetValue(action, out var defaults) || defaults == null)
			return true;

		var current = InputMap.ActionGetEvents(action);
		if (current.Count != defaults.Count)
			return false;

		for (int i = 0; i < current.Count; i++)
		{
			if (!InputEventsEqual(current[i], defaults[i]))
				return false;
		}
		return true;
	}

	/// <summary>
	/// Restores only the specified action to its captured default binding.
	/// </summary>
	/// <param name="action">InputMap action name to reset.</param>
	/// <remarks>
	/// Does not persist to user data until <see cref="UserDataManager.PersistLiveInputMap"/> is called.
	/// </remarks>
	public void ResetInputActionToDefault(string action)
	{
		if (string.IsNullOrEmpty(action) || !InputMap.HasAction(action))
			return;

		if (!_defaultInputBindings.TryGetValue(action, out var defaults) || defaults == null)
			return;

		InputMap.ActionEraseEvents(action);
		foreach (InputEvent ev in defaults)
		{
			InputMap.ActionAddEvent(action, (InputEvent)ev.Duplicate());
		}
		GD.Print($"GlobalData:ResetInputActionToDefault - Restored '{action}' to default binding.");
	}

	/// <summary>
	/// Returns a readable string for the default binding of an action (e.g. "Ctrl+G" or "Space").
	/// </summary>
	/// <param name="action">InputMap action name.</param>
	/// <returns>
	/// Up to two formatted events joined by <c> / </c>, with an ellipsis if more exist;
	/// <c>Unbound</c> when no defaults were captured.
	/// </returns>
	public string GetDefaultBindingDisplay(string action)
	{
		var defaults = GetDefaultInputEvents(action);
		if (defaults.Count == 0)
			return "Unbound";

		var parts = new System.Collections.Generic.List<string>();
		int shown = 0;
		foreach (InputEvent ev in defaults)
		{
			if (shown >= 2) break;
			string s = FormatInputEvent(ev);
			if (!string.IsNullOrEmpty(s))
			{
				parts.Add(s);
				shown++;
			}
		}
		string result = string.Join(" / ", parts);
		if (defaults.Count > 2)
			result += " …";
		return result;
	}

	/// <summary>
	/// Returns a user-friendly display name for a keycode.
	/// Uses symbols for punctuation keys instead of "BracketLeft", "QuoteLeft", etc.
	/// </summary>
	/// <param name="keycode">Godot keycode to format.</param>
	/// <returns>Short display label suitable for UI hotkey chrome.</returns>
	public static string GetKeyDisplayName(Key keycode)
	{
		string name = OS.GetKeycodeString(keycode);
		if (string.IsNullOrEmpty(name) || name == "None")
		{
			name = keycode.ToString();
			if (name.StartsWith("Key"))
				name = name.Substring(3);
		}

		// Map ugly key names to nice symbols / short names
		switch (name)
		{
			case "BracketLeft": return "[";
			case "BracketRight": return "]";
			case "QuoteLeft": return "`";
			case "Apostrophe": return "'";
			case "Semicolon": return ";";
			case "Comma": return ",";
			case "Period": return ".";
			case "Slash": return "/";
			case "Backslash": return "\\";
			case "Minus": return "-";
			case "Equal": return "=";
			case "Escape": return "Esc";
			default:
				return name;
		}
	}

	/// <summary>
	/// Formats a single input event for display (matches Input Map card formatting style).
	/// </summary>
	/// <param name="ev">Event to format (key events get modifier + key name treatment).</param>
	/// <returns>Compact display string, or <see cref="InputEvent.AsText"/> for non-key events.</returns>
	public static string FormatInputEvent(InputEvent ev)
	{
		if (ev is InputEventKey key)
		{
			string keyName = GetKeyDisplayName(key.Keycode);
			if (string.IsNullOrEmpty(keyName) || keyName == "None") keyName = key.PhysicalKeycode.ToString();

			string mods = "";
			if (key.CtrlPressed) mods += "Ctrl+";
			if (key.ShiftPressed) mods += "Shift+";
			if (key.AltPressed) mods += "Alt+";
			if (key.MetaPressed) mods += "Meta+";
			return mods + keyName;
		}
		return ev.AsText();
	}

	/// <summary>
	/// Compares two input events for equality used by default-binding checks.
	/// </summary>
	/// <param name="a">First event.</param>
	/// <param name="b">Second event.</param>
	/// <returns><c>true</c> when both are matching keys or have identical <c>AsText()</c>.</returns>
	private bool InputEventsEqual(InputEvent a, InputEvent b)
	{
		if (a is InputEventKey ka && b is InputEventKey kb)
			return KeyEventsMatch(ka, kb);
		return a.AsText() == b.AsText();
	}

	/// <summary>
	/// Returns true if two key events represent the same hotkey (key + modifiers).
	/// Compares effective keycode (falls back to physical) and Ctrl/Shift/Alt/Meta.
	/// </summary>
	/// <param name="a">First key event.</param>
	/// <param name="b">Second key event.</param>
	/// <returns><c>true</c> when both are non-null and encode the same combo.</returns>
	public static bool KeyEventsMatch(InputEventKey a, InputEventKey b)
	{
		if (a == null || b == null) return false;

		Key aKey = a.Keycode != Key.None ? a.Keycode : a.PhysicalKeycode;
		Key bKey = b.Keycode != Key.None ? b.Keycode : b.PhysicalKeycode;
		if (aKey == Key.None || bKey == Key.None || aKey != bKey)
			return false;

		return a.CtrlPressed == b.CtrlPressed &&
		       a.ShiftPressed == b.ShiftPressed &&
		       a.AltPressed == b.AltPressed &&
		       a.MetaPressed == b.MetaPressed;
	}

	/// <summary>
	/// Finds another mappable action that already uses the given key combo.
	/// </summary>
	/// <param name="excludeAction">Action currently being rebound (ignored in the search).</param>
	/// <param name="keyEvent">Proposed key binding.</param>
	/// <returns>Conflicting action name, or null if the combo is free.</returns>
	public static string FindConflictingInputAction(string excludeAction, InputEventKey keyEvent)
	{
		if (keyEvent == null) return null;

		foreach (var action in MappableInputActions)
		{
			if (action == excludeAction) continue;
			if (!InputMap.HasAction(action)) continue;

			foreach (InputEvent ev in InputMap.ActionGetEvents(action))
			{
				if (ev is InputEventKey other && KeyEventsMatch(other, keyEvent))
					return action;
			}
		}

		// Also scan non-ui_ actions not in the curated list (same scope as the settings UI).
		foreach (StringName actionName in InputMap.GetActions())
		{
			string action = actionName.ToString();
			if (string.IsNullOrEmpty(action) || action.StartsWith("ui_")) continue;
			if (action == excludeAction) continue;
			if (System.Array.IndexOf(MappableInputActions, action) >= 0) continue;

			foreach (InputEvent ev in InputMap.ActionGetEvents(action))
			{
				if (ev is InputEventKey other && KeyEventsMatch(other, keyEvent))
					return action;
			}
		}

		return null;
	}

	/// <summary>
	/// Builds a map of live connection objects available for cue/control routing UI.
	/// </summary>
	/// <returns>
	/// Dictionary keyed by connection instance (cue light or OSC connection),
	/// with values of <c>"Cue Light"</c> or <c>"Osc Connection"</c> for type display.
	/// </returns>
	/// <remarks>
	/// Used by <c>ConnectionInspector</c> when listing attachable destinations.
	/// </remarks>
	public Dictionary GetAvailableConnections()
	{
		var dict = new Dictionary();
		var cueLights = CueLightManager.GetCueLights();
		foreach (var cueLight in cueLights)
		{
			dict.Add(cueLight,"Cue Light");
		}

		var oscConnections = OscConnections.Connections;
		foreach (var oscCon in oscConnections)
		{
			dict.Add(oscCon,"Osc Connection");
		}
		return dict;
	}

}