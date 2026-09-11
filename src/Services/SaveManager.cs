// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Cue2.Domain.Cuelist;
using Cue2.Domain.Cues;
using Cue2.Media.Audio;
using Cue2.UI.Popups;
using Cue2.UI.Shell;
using Cue2.UI.Utilities;
using Godot;
using Godot.Collections;

namespace Cue2.Services;

/// <summary>
/// Manages saving and loading of session data, including cues and settings.
/// Handles file dialogs and serialization via Json (plain UTF-8 .c2 files).
/// </summary>
/// <remarks>
/// Showfiles are versioned via <see cref="ShowfileFormat"/>. On open, version is checked
/// <b>before</b> session reset; mismatches prompt <see cref="VersionMismatchDialog"/> and may
/// run <see cref="ShowfileMigrator"/> before load.
/// <para>
/// Storage format is <b>plain UTF-8 JSON</b>.
/// </para>
/// </remarks>
public partial class SaveManager : Node
{
	private GlobalSignals _globalSignals;
	private GlobalData _globalData;
	private AudioDevices _audioDevices;
	private MediaBackupManager _mediaBackupManager;

	/// <summary>Save As dialog — created and wired once in <see cref="_Ready"/>.</summary>
	private FileDialog _saveDialog;

	/// <summary>
	/// Open dialog on the main scene tree. Resolved after main scene load (autoload is ready first).
	/// </summary>
	private FileDialog _openDialog;

	/// <summary>Prevents overlapping open/version-dialog flows for concurrent open requests.</summary>
	private bool _isOpenInProgress;

	/// <summary>
	/// True while a showfile is being applied (reset + settings + cues + first bind).
	/// New/Open/Save and document edits must refuse until this returns to false.
	/// File-read / version dialog do not set this unless the current session is already
	/// empty (startup / New Session). GO uses <see cref="IsPlaybackReady"/>.
	/// </summary>
	public bool IsSessionLoading { get; private set; }

	/// <summary>
	/// True when GO may fire. False from apply start until cue models exist (or apply ends).
	/// First viewport bind and deferred housekeeping do not keep this false.
	/// </summary>
	public bool IsPlaybackReady { get; private set; } = true;

	private VersionMismatchDialog _activeVersionDialog;

	/// <summary>
	/// True when the live session was opened from a showfile whose formatVersion is newer than
	/// this build. Overwriting that file would re-stamp the current schema and can drop data.
	/// </summary>
	private bool _openedFromNewerFormat;

	/// <summary>
	/// Bumped on each save start; completions for older generations skip UI side effects
	/// so a rapid Save + Save As does not apply stale success handlers.
	/// </summary>
	private int _saveGeneration;

	private Godot.Timer _autosaveTimer;

	/// <summary>P0 stage timings for the in-flight open (null when idle).</summary>
	private SessionLoadTimer _loadTimer;

	private enum PendingCloseKind
	{
		None,
		NewSession,
		OpenDialog,
		OpenPath,
		Quit
	}

	private PendingCloseKind _pendingClose;
	private string _pendingOpenPath;
	private bool _proceedAfterSaveAs;
	private UnsavedChangesDialog _unsavedDialog;
	private bool _quitStarted;
	private bool _pendingBackgroundTaskPrompt;
	private string _pendingBackgroundTaskName;

	/// <summary>
	/// True when cue / cuelist / show-settings data has changed since the last save or load.
	/// </summary>
	public bool HasUnsavedChanges =>
		_globalData?.HistoryManager?.HasUnsavedDocumentChanges == true;

	public override void _Ready()
	{
		_globalData = GetNode<Cue2.Services.GlobalData>("/root/GlobalData");
		_globalSignals = GetNode<GlobalSignals>("/root/GlobalSignals");
		_audioDevices = GetNode<AudioDevices>("/root/AudioDevices");
		_mediaBackupManager = GetNodeOrNull<MediaBackupManager>("/root/MediaBackupManager");

		CreateSaveDialog();
		// Open dialog lives under Cue2Base; that scene is not present while autoloads run.
		CallDeferred(nameof(ResolveOpenDialog));

		_globalSignals.NewSession += OnNewSession;
		_globalSignals.Save += Save;
		_globalSignals.SaveAs += SaveAs;
		_globalSignals.OpenSession += OpenSession;
		_globalSignals.OpenSelectedSession += OpenSelectedSession;

		// Setup autosave timer (disabled by default until interval set)
		_autosaveTimer = new Godot.Timer { OneShot = false };
		_autosaveTimer.Timeout += PerformAutosave;
		AddChild(_autosaveTimer);

		if (!string.IsNullOrEmpty(_globalData.StartupOpenPath))
		{
			SessionLoadTimer.Current?.Begin("boot.savemanager");
			LoadStartupSession();
		}
		else
		{
			ConfigureAutosave();
		}
	}
	
	/// <summary>
	/// Opens the showfile selected by startup preference (open last from user data), after the
	/// main scene has finished <c>_Ready</c>.
	/// </summary>
	private void LoadStartupSession()
	{
		TaskUtil.Run(LoadStartupSessionAsync, "SaveManager.LoadStartupSession");
	}

	private async Task LoadStartupSessionAsync()
	{
		try
		{
			if (!GodotObject.IsInstanceValid(this))
				return;
			// Do not start apply on a single ProcessFrame: DisplayServer.WindowSetMode
			// (maximize restore in MainWindowHandles._Ready) can deliver that frame on
			// macOS while Cue2Base is still setting up children. SessionLoadOverlay then
			// hits move_child / MoveToFront on a blocked parent.
			SessionLoadTimer.Current?.Begin("boot.frame");
			await WaitForMainSceneReadyAsync();
			if (!GodotObject.IsInstanceValid(this) || _globalData == null)
				return;
			string path = _globalData.StartupOpenPath;
			if (string.IsNullOrEmpty(path))
				return;

			GD.Print($"SaveManager:LoadStartupSession - Opening startup showfile: {path}");
			if (!File.Exists(path))
			{
				AbandonStartupOpenToNewSession(path, "Showfile not found", removeFromRecents: true);
				return;
			}

			await OpenSelectedSessionAsync(path, isStartupOpen: true);
			// Successful open already configured autosave in CompleteOpenSessionAsync.
		}
		catch (Exception ex)
		{
			GD.PrintErr($"SaveManager:LoadStartupSessionAsync - {ex.Message}");
			AbandonStartupOpenToNewSession(_globalData?.StartupOpenPath, ex.Message, removeFromRecents: false);
		}
	}

	/// <summary>
	/// Last-show boot failed (missing or unreadable file). Drop the overlay, forget the
	/// startup path, and seed a playable blank session instead of leaving an unseeded workspace.
	/// </summary>
	/// <param name="path">Showfile that could not be opened.</param>
	/// <param name="reason">Short failure reason for logs.</param>
	/// <param name="removeFromRecents">True when the file is gone (not when it is merely unreadable).</param>
	private void AbandonStartupOpenToNewSession(string path, string reason, bool removeFromRecents)
	{
		GD.PrintErr($"SaveManager:AbandonStartupOpenToNewSession - {reason}: {path}");

		if (_globalData != null)
			_globalData.StartupOpenPath = null;

		if (removeFromRecents && !string.IsNullOrEmpty(path))
			_globalData?.UserDataManager?.RemoveRecentShowFile(path);

		// Cue2Base may have shown the overlay via ShowOpening without BeginSessionApply.
		if (IsSessionLoading)
			EndSessionApply();
		else
			_globalSignals?.EmitSignal(nameof(GlobalSignals.SessionLoadFinished));

		ResetSession(clearSessionIdentity: true, logAsNewSession: false);
		_globalData?.HistoryManager?.Clear();
		ConfigureAutosave();
		GetNodeOrNull<MainTitleBarUI>("/root/Cue2Base/MainWindowHandles/MainTitleBar")
			?.CallDeferred("UpdateTitle");

		string pathPart = string.IsNullOrEmpty(path) ? string.Empty : $" ({path})";
		_globalSignals?.EmitSignal(nameof(GlobalSignals.Log),
			$"Could not open last showfile{pathPart} — started a new session. {reason}",
			(int)LogType.Error);
		FinishLoadTimer("missing");
	}

	/// <summary>
	/// Waits until the current main scene exists and has finished <c>_Ready</c>.
	/// </summary>
	/// <remarks>
	/// A lone <see cref="SceneTree.SignalName.ProcessFrame"/> is not enough at boot:
	/// macOS window-mode changes can flush that frame mid-child setup.
	/// </remarks>
	private async Task WaitForMainSceneReadyAsync()
	{
		while (GodotObject.IsInstanceValid(this))
		{
			var tree = GetTree();
			if (tree == null)
				return;

			var scene = tree.CurrentScene;
			if (scene != null)
			{
				if (scene.IsNodeReady())
					return;
				await ToSignal(scene, Node.SignalName.Ready);
				return;
			}

			await ToSignal(tree, SceneTree.SignalName.ProcessFrame);
		}
	}

	/// <summary>
	/// Configures the autosave timer based on UserDataManager.AutosaveInterval (in minutes).
	/// If interval is 0 or no active session, autosave is disabled.
	/// </summary>
	public void ConfigureAutosave()
	{
		if (_autosaveTimer == null) return;

		int intervalMinutes = _globalData.UserDataManager?.AutosaveInterval ?? 0;
		if (intervalMinutes <= 0 || string.IsNullOrEmpty(_globalData.SessionPath))
		{
			_autosaveTimer.Stop();
			return;
		}

		_autosaveTimer.WaitTime = intervalMinutes * 60.0;
		_autosaveTimer.Start();
		GD.Print($"SaveManager:ConfigureAutosave - Autosave enabled every {intervalMinutes} minute(s).");
	}
	
	/// <summary>
	/// Initiates a save operation. If the session is unnamed or has no path, triggers SaveAs.
	/// Otherwise, saves to the existing path.
	/// </summary>
	private void Save()
	{
		if (RejectIfSessionBusy("save"))
			return;

		if (_globalData.SessionName == null || _globalData.SessionPath == null)
		{
			SaveAs();
			return;
		}

		// Never overwrite a newer-format original with this build's schema.
		if (_openedFromNewerFormat)
		{
			_globalSignals.EmitSignal(nameof(GlobalSignals.Log),
				"This show was opened from a newer Cue2 format. Use Save As to write a copy " +
				"this version can own — overwriting the original is blocked.",
				(int)LogType.Warning);
			SaveAs();
			return;
		}

		_globalSignals.EmitSignal(nameof(GlobalSignals.Log),
			$"Saving session to: {_globalData.SessionPath} with name: {_globalData.SessionName}:", 0);
		SaveSession(_globalData.SessionPath);
	}

	/// <summary>
	/// Opens the save file dialog to allow the user to choose a directory and name for the session.
	/// Uses the single dialog instance created in <see cref="CreateSaveDialog"/>.
	/// </summary>
	/// <remarks>
	/// Start folder: current show directory when the session is already on disk; otherwise the
	/// last Save As location; otherwise a writable Documents/Cue2 (with Desktop / user:// failover).
	/// </remarks>
	private void SaveAs()
	{
		if (RejectIfSessionBusy("save as"))
			return;

		if (_saveDialog == null || !IsInstanceValid(_saveDialog))
		{
			_globalSignals.EmitSignal(nameof(GlobalSignals.Log),
				"SaveManager:SaveAs - Save dialog is not available.", (int)LogType.Error);
			return;
		}

		ApplySaveDialogStartLocation();
		_saveDialog.PopupCentered();
		_globalSignals.EmitSignal(nameof(GlobalSignals.Log), "SaveManager:SaveAs - Waiting on save directory and show name to continue save", 0);
	}

	/// <summary>
	/// Sets the Save As dialog folder (and suggested file name) before popup.
	/// </summary>
	private void ApplySaveDialogStartLocation()
	{
		string dir = ResolveSaveDialogStartDirectory();
		if (!string.IsNullOrEmpty(dir))
		{
			_saveDialog.CurrentDir = dir;
			GD.Print($"SaveManager:ApplySaveDialogStartLocation - CurrentDir={dir}");
		}

		if (!string.IsNullOrEmpty(_globalData.SessionName))
			_saveDialog.CurrentFile = _globalData.SessionName + ".c2";
		else
			_saveDialog.CurrentFile = string.Empty;
	}

	/// <summary>
	/// Directory the Save As dialog should open in.
	/// </summary>
	/// <returns>Absolute writable directory, or empty when none could be resolved.</returns>
	private string ResolveSaveDialogStartDirectory()
	{
		string currentShowDir = FirstExistingDirectory(_globalData.SessionDir, SessionPathDirectory());
		if (!string.IsNullOrEmpty(currentShowDir))
			return currentShowDir;

		string last = _globalData.UserDataManager?.LastShowSaveDirectory;
		if (DirectoryUtils.DirectoryExists(last))
			return last;

		return DirectoryUtils.GetDefaultShowSaveDirectory();
	}

	/// <summary>Directory of the open .c2 file, or null when unset.</summary>
	private string SessionPathDirectory()
	{
		if (string.IsNullOrEmpty(_globalData?.SessionPath))
			return null;
		try
		{
			return _globalData.SessionPath.GetBaseDir();
		}
		catch (Exception ex)
		{
			GD.Print($"SaveManager:SessionPathDirectory - {_globalData.SessionPath}: {ex.Message}");
			return null;
		}
	}

	/// <summary>
	/// First candidate that exists as a directory.
	/// </summary>
	private static string FirstExistingDirectory(params string[] candidates)
	{
		if (candidates == null)
			return null;
		foreach (string path in candidates)
		{
			if (DirectoryUtils.DirectoryExists(path))
				return path;
		}
		return null;
	}

	/// <summary>
	/// Persists the last Save As folder (parent of the session directory) for the next new show.
	/// </summary>
	/// <param name="savedFilePath">Path written or chosen by the user.</param>
	private void RememberSaveDirectory(string savedFilePath)
	{
		string root = DirectoryUtils.GetShowSaveRootDirectory(savedFilePath);
		if (string.IsNullOrEmpty(root))
			return;
		_globalData.UserDataManager?.SetLastShowSaveDirectory(root);
	}

	/// <summary>
	/// Instantiates and wires the Save As <see cref="FileDialog"/> once at startup.
	/// </summary>
	private void CreateSaveDialog()
	{
		if (_saveDialog != null && IsInstanceValid(_saveDialog))
			return;

		PackedScene saveDialogScene = SceneLoader.LoadPackedScene("uid://0dv6dq3u20ku", out _);
		if (saveDialogScene == null)
		{
			GD.PrintErr("SaveManager:CreateSaveDialog - Save dialog scene is null.");
			return;
		}

		_saveDialog = saveDialogScene.Instantiate<FileDialog>();
		_saveDialog.Name = "SaveDialog";
		AddChild(_saveDialog);
		_saveDialog.FileMode = FileDialog.FileModeEnum.SaveFile;
		_saveDialog.Access = FileDialog.AccessEnum.Filesystem;
		_saveDialog.ClearFilters();
		_saveDialog.AddFilter("*.c2 ; Cue2 Session");
		_saveDialog.FileSelected += OnSaveFileSelected;
		_saveDialog.Canceled += OnSaveDialogCanceled;
		_saveDialog.Hide();
		GD.Print("SaveManager:CreateSaveDialog - Save As dialog ready.");
	}

	/// <summary>
	/// Caches the main-scene open dialog after <c>Cue2Base</c> is in the tree.
	/// Autoloads run before the main scene, so this is deferred from <see cref="_Ready"/>.
	/// </summary>
	private void ResolveOpenDialog()
	{
		if (_openDialog != null && IsInstanceValid(_openDialog))
			return;

		_openDialog = GetNodeOrNull<FileDialog>("/root/Cue2Base/OpenDialog");
		if (_openDialog != null)
			GD.Print("SaveManager:ResolveOpenDialog - Open dialog resolved.");
		// If still null (launcher, or Cue2Base not loaded), OpenSession resolves on demand.
	}

	private void OnSaveFileSelected(string path)
	{
		if (_saveDialog != null && IsInstanceValid(_saveDialog))
			_saveDialog.Hide();
		bool proceed = _proceedAfterSaveAs;
		_proceedAfterSaveAs = false;
		TaskUtil.Run(async () =>
		{
			bool ok = await SaveSessionAsync(path, skipMediaBackup: false, clearNewerFormatGuard: true);
			if (ok && proceed)
				ExecutePendingClose();
		}, "SaveManager.SaveAsThenClose");
	}

	private void OnSaveDialogCanceled()
	{
		if (_saveDialog != null && IsInstanceValid(_saveDialog))
			_saveDialog.Hide();
		_proceedAfterSaveAs = false;
		ClearPendingClose();
	}
	
	
	/// <summary>
	/// Re-saves the current session after media paths were rewritten to show-relative URLs.
	/// Skips media-backup enqueue to avoid a recursive copy loop.
	/// </summary>
	public void ResaveSessionAfterMediaPathUpdate()
	{
		if (string.IsNullOrEmpty(_globalData.SessionPath))
		{
			GD.Print("SaveManager:ResaveSessionAfterMediaPathUpdate - No SessionPath; skip.");
			return;
		}

		if (_openedFromNewerFormat)
		{
			GD.Print(
				"SaveManager:ResaveSessionAfterMediaPathUpdate - Skipped (session opened from newer format; " +
				"use Save As to create a this-version copy).");
			_globalSignals?.EmitSignal(nameof(GlobalSignals.Log),
				"Media paths updated, but silent re-save was skipped because this show uses a newer format. " +
				"Use Save As if you want a copy this version of Cue2 can own.",
				(int)LogType.Warning);
			return;
		}

		SaveSession(_globalData.SessionPath, skipMediaBackup: true);
	}

	/// <summary>
	/// Saves the current session data to the specified path and name.
	/// Builds the save dictionary on the main thread, then JSON-stringifies
	/// and encrypt-writes on a worker so large shows do not freeze the UI (P1-15).
	/// </summary>
	/// <param name="selectedPath">The full path where the session file will be saved.</param>
	/// <param name="skipMediaBackup">When true, does not enqueue media copies (used after path rewrite re-save).</param>
	/// <param name="clearNewerFormatGuard">
	/// When true (Save As), clears the forward-compat open guard after a successful write.
	/// </param>
	private void SaveSession(string selectedPath, bool skipMediaBackup = false, bool clearNewerFormatGuard = false)
	{
		_ = SaveSessionAsync(selectedPath, skipMediaBackup, clearNewerFormatGuard);
	}

	/// <summary>
	/// Async save implementation. Prefer this when the caller can await (autosave).
	/// </summary>
	private async Task<bool> SaveSessionAsync(string selectedPath, bool skipMediaBackup = false, bool clearNewerFormatGuard = false)
	{
		// Block accidental overwrite of a newer-format original via any path (autosave, etc.).
		if (_openedFromNewerFormat &&
		    !string.IsNullOrEmpty(_globalData.SessionPath) &&
		    !clearNewerFormatGuard &&
		    PathsEqual(selectedPath, _globalData.SessionPath))
		{
			_globalSignals.EmitSignal(nameof(GlobalSignals.Log),
				"Save blocked: this show was opened from a newer Cue2 format. Use Save As to write a new file.",
				(int)LogType.Warning);
			GD.Print("SaveManager:SaveSession - Blocked overwrite of newer-format session path.");
			return false;
		}

		// Capture before Prepare/Apply so Save As can copy show-local media out of the old folder.
		// Relative URLs (Audio/…, Video/…) resolve against SessionDir; if we switch SessionDir first,
		// those files look missing in the empty new folder and are never copied.
		string previousSessionDir = _globalData.SessionDir;

		// Verify save folder structure (type-based: Audio, Video, Images, Waveforms) — main thread I/O dirs.
		var sessionPath = DirectoryUtils.PrepareSessionDirectory(selectedPath, out var folderPaths);
		GD.Print($"SaveManager:SaveSession - Session path: {sessionPath} skipMediaBackup={skipMediaBackup}");

		if (string.IsNullOrEmpty(sessionPath))
		{
			_globalSignals.EmitSignal(nameof(GlobalSignals.Log), $"Failed to prepare session directory for: {selectedPath}", 2);
			GD.PrintErr($"SaveManager:SaveSession - PrepareSessionDirectory failed for: {selectedPath}");
			return false;
		}

		// Session paths must be known before serializing so relative media URLs resolve correctly
		ApplySessionPaths(sessionPath, folderPaths);

		// Snapshot on main thread (touches Cuelist / Settings / Godot nodes).
		Dictionary saveData;
		try
		{
			saveData = BuildSaveDataDictionary();
		}
		catch (Exception ex)
		{
			_globalSignals.EmitSignal(nameof(GlobalSignals.Log), $"Failed to build save data: {ex.Message}", 2);
			GD.PrintErr($"SaveManager:SaveSession - BuildSaveDataDictionary: {ex.Message}");
			return false;
		}

		int gen = Interlocked.Increment(ref _saveGeneration);
		string pathForWrite = sessionPath;

		_globalSignals.EmitSignal(nameof(GlobalSignals.Log), "Saving session…", 0);

		(bool Ok, string Error) writeResult;
		try
		{
			// Heavy work: stringify large JSON + plain disk write (off main thread).
			writeResult = await Task.Run(() => WritePlainShowfile(pathForWrite, saveData))
				.ConfigureAwait(true);
		}
		catch (Exception ex)
		{
			writeResult = (false, ex.Message);
		}

		// Superseded by a newer Save/Save As — do not touch recents / UI for the stale job.
		if (gen != Volatile.Read(ref _saveGeneration))
		{
			GD.Print($"SaveManager:SaveSession - Superseded (gen {gen}); discarding completion for {pathForWrite}");
			return false;
		}

		if (!writeResult.Ok)
		{
			_globalSignals.EmitSignal(nameof(GlobalSignals.Log),
				$"Failed to write showfile: {selectedPath} ({writeResult.Error})", 2);
			GD.PrintErr($"SaveManager:SaveSession - Write failed: {pathForWrite} Error: {writeResult.Error}");
			return false;
		}

		// Successful Save As of a forward-compat open: this file is now owned by current format.
		if (clearNewerFormatGuard)
			_openedFromNewerFormat = false;

		_globalSignals.EmitSignal(nameof(GlobalSignals.Log),
			skipMediaBackup
				? $"Session re-saved with relative media paths to {sessionPath}"
				: $"Session saved successfully to {sessionPath}",
			0);

		// Recents + last Save As folder (parent of the session dir for a later new show).
		RememberSaveDirectory(sessionPath);
		_globalData.UserDataManager?.AddRecentShowFile(sessionPath);

		// Relocate already-local media when the show folder changed (Save As), then
		// optionally back up remaining external files (respects MediaBackupEnabled).
		try
		{
			_mediaBackupManager ??= GetNodeOrNull<MediaBackupManager>("/root/MediaBackupManager");
			if (_mediaBackupManager != null)
			{
				if (!string.IsNullOrEmpty(previousSessionDir))
					_mediaBackupManager.EnqueueCopyFromPreviousSession(previousSessionDir);

				if (!skipMediaBackup)
					_mediaBackupManager.EnqueueShowMediaBackup();
			}
		}
		catch (Exception ex)
		{
			GD.PrintErr($"SaveManager:SaveSession - Media backup enqueue failed: {ex.Message}");
			_globalSignals.EmitSignal(nameof(GlobalSignals.Log), $"Media backup enqueue failed: {ex.Message}", 2);
		}

		ConfigureAutosave();
		_globalData.HistoryManager?.MarkSaved();

		// Update title (in case signal timing)
		GetNodeOrNull<MainTitleBarUI>("/root/Cue2Base/MainWindowHandles/MainTitleBar")?.CallDeferred("UpdateTitle");
		return true;
	}

	/// <summary>
	/// JSON-stringify + plain UTF-8 write. Intended for a worker thread (no Godot node access).
	/// Showfiles are not encrypted — open, editable JSON for an open-source tool.
	/// </summary>
	/// <param name="sessionPath">Absolute .c2 path.</param>
	/// <param name="saveData">Snapshot dictionary built on the main thread.</param>
	/// <returns>Success flag and error text.</returns>
	private static (bool Ok, string Error) WritePlainShowfile(string sessionPath, Dictionary saveData)
	{
		try
		{
			string jsonString = Json.Stringify(saveData);
			if (string.IsNullOrEmpty(jsonString))
				return (false, "JSON stringify produced empty output.");

			// Prefer System.IO for plain UTF-8 so worker-thread writes are reliable.
			System.IO.File.WriteAllText(sessionPath, jsonString);
			return (true, null);
		}
		catch (Exception ex)
		{
			return (false, ex.Message);
		}
	}

	/// <summary>
	/// Applies session file path, name, and type-based media folder paths to <see cref="GlobalData"/>.
	/// Ensures the <c>Waveforms/</c> peak-cache folder exists so disk cache hits work after open.
	/// </summary>
	/// <param name="sessionFilePath">Absolute path to the .c2 file.</param>
	/// <param name="folderPaths">Optional precomputed folder layout; derived from the session path when null.</param>
	private void ApplySessionPaths(string sessionFilePath, SessionFolderPaths folderPaths = null)
	{
		if (string.IsNullOrEmpty(sessionFilePath))
			return;

		folderPaths ??= DirectoryUtils.GetSessionFolderPaths(sessionFilePath);

		_globalData.SessionPath = sessionFilePath;
		_globalData.SessionName = Path.GetFileNameWithoutExtension(sessionFilePath);
		_globalData.SessionDir = folderPaths.SessionDir;
		_globalData.SessionAudioPath = folderPaths.AudioDir;
		_globalData.SessionVideoPath = folderPaths.VideoDir;
		_globalData.SessionImagesPath = folderPaths.ImagesDir;
		_globalData.SessionWaveformsPath = folderPaths.WaveformsDir;

		// Peak envelopes are stored only under Waveforms/ (not in the .c2). Create on open as well as save.
		try
		{
			if (!string.IsNullOrEmpty(folderPaths.WaveformsDir) && !Directory.Exists(folderPaths.WaveformsDir))
				Directory.CreateDirectory(folderPaths.WaveformsDir);
		}
		catch (Exception ex)
		{
			GD.PrintErr($"SaveManager:ApplySessionPaths - Failed to create Waveforms dir: {ex.Message}");
		}

		GD.Print($"SaveManager:ApplySessionPaths - SessionDir={_globalData.SessionDir}, Audio={_globalData.SessionAudioPath}, Video={_globalData.SessionVideoPath}, Images={_globalData.SessionImagesPath}, Waveforms={_globalData.SessionWaveformsPath}");
	}

	/// <summary>
	/// True when the workspace is empty so the read phase should show the load overlay
	/// (startup last-show, or File → Open after New Session).
	/// </summary>
	private bool ShouldCoverReadPhase()
	{
		if (!string.IsNullOrEmpty(_globalData?.SessionPath))
			return false;
		var index = CueList.CueIndex;
		return index == null || index.Count == 0;
	}

	/// <summary>
	/// Logs and returns true when New / Open / Save should be refused (open in flight or applying).
	/// </summary>
	/// <param name="actionLabel">Short action name for the log message.</param>
	/// <returns>True if the caller should abort.</returns>
	private bool RejectIfSessionBusy(string actionLabel)
	{
		if (_quitStarted)
		{
			_globalSignals?.EmitSignal(nameof(GlobalSignals.Log),
				"Cue2 is closing.", (int)LogType.Info);
			return true;
		}

		if (IsUnsavedDialogOpen || _pendingClose != PendingCloseKind.None)
		{
			_globalSignals?.EmitSignal(nameof(GlobalSignals.Log),
				"Finish the unsaved-changes dialog first.", (int)LogType.Info);
			return true;
		}

		if (!IsSessionLoading && !_isOpenInProgress)
			return false;
		_globalSignals?.EmitSignal(nameof(GlobalSignals.Log),
			$"Please wait — a showfile is still loading. Cannot {actionLabel}.", (int)LogType.Info);
		return true;
	}

	/// <summary>
	/// Marks the apply phase, shows the workspace overlay, and gates GO / document edits.
	/// </summary>
	/// <param name="sessionPath">Showfile path (name is derived for the overlay title).</param>
	/// <param name="statusText">English source status key.</param>
	/// <param name="percent">Initial progress 0–100.</param>
	private void BeginSessionApply(string sessionPath, string statusText, float percent)
	{
		IsSessionLoading = true;
		IsPlaybackReady = false;
		_globalSignals?.DisableGo(GlobalSignals.GoDisableReasonSessionLoad);
		string showName = string.IsNullOrEmpty(sessionPath)
			? string.Empty
			: Path.GetFileNameWithoutExtension(sessionPath);
		_globalSignals?.EmitSignal(nameof(GlobalSignals.SessionLoadStarted), showName ?? string.Empty);
		EmitSessionLoadProgress(percent, statusText, string.Empty, 0, 0);
	}

	/// <summary>
	/// Updates overlay + footer-compatible session-load progress.
	/// </summary>
	private void EmitSessionLoadProgress(float percent, string statusText, string detail, int completed, int total)
	{
		if (_globalSignals == null || !IsSessionLoading)
			return;
		_globalSignals.EmitSignal(nameof(GlobalSignals.SessionLoadProgress),
			percent, statusText ?? string.Empty, detail ?? string.Empty, completed, total);
	}

	/// <summary>
	/// Cue models (or a settings-only show) are live. GO may fire; overlay may still be up
	/// for the first viewport bind.
	/// </summary>
	private void MarkPlaybackReady()
	{
		IsPlaybackReady = true;
		_globalSignals?.EnableGo(GlobalSignals.GoDisableReasonSessionLoad);
		_loadTimer?.MarkApplyComplete();
	}

	/// <summary>
	/// Clears the apply gate and hides the overlay. Safe to call when not loading.
	/// </summary>
	private void EndSessionApply()
	{
		if (!IsSessionLoading)
			return;
		IsSessionLoading = false;
		IsPlaybackReady = true;
		_globalSignals?.EnableGo(GlobalSignals.GoDisableReasonSessionLoad);
		string applySummary = _loadTimer?.FormatApplySummary();
		if (!string.IsNullOrEmpty(applySummary))
		{
			_globalSignals?.EmitSignal(nameof(GlobalSignals.Log), applySummary, 0);
		}
		_globalSignals?.EmitSignal(nameof(GlobalSignals.SessionLoadFinished));
	}

	/// <summary>Prints P0 timings and drops the in-flight timer.</summary>
	private void FinishLoadTimer(string outcome)
	{
		if (_loadTimer == null)
			return;
		_loadTimer.Finish(outcome);
		_loadTimer = null;
	}

	/// <summary>Awaits one process frame so the overlay can paint.</summary>
	private async Task YieldProcessFrame()
	{
		var tree = GetTree();
		if (tree != null)
			await ToSignal(tree, SceneTree.SignalName.ProcessFrame);
	}

	/// <summary>
	/// Media health + legacy waveform cache after the overlay is gone (not required for GO).
	/// </summary>
	private async Task RunDeferredOpenHousekeepingAsync()
	{
		try
		{
			if (!GodotObject.IsInstanceValid(this))
				return;
			await YieldProcessFrame();
			if (!GodotObject.IsInstanceValid(this))
				return;

			_globalData?.UserDataManager?.PersistUserData();
			_loadTimer?.Begin("housekeeping.health");
			GetNodeOrNull<MediaHealthService>("/root/MediaHealthService")?.RecheckAllQuiet();
			_loadTimer?.Begin("housekeeping.waveforms");
			MigrateLegacyEmbeddedWaveformsToDisk();
			FinishLoadTimer("complete");
		}
		catch (Exception ex)
		{
			GD.PrintErr($"SaveManager:RunDeferredOpenHousekeepingAsync - {ex.Message}");
			FinishLoadTimer("complete");
		}
	}
	
	/// <summary>
	/// Opens the open file dialog for selecting a session to load.
	/// </summary>
	private void OpenSession()
	{
		if (RejectIfSessionBusy("open a showfile"))
			return;
		if (!ConfirmIfUnsaved(PendingCloseKind.OpenDialog))
			return;
		PopupOpenDialog();
	}
	
	/// <summary>
	/// Loads and processes a selected session file (startup last-show, File → Open, recents).
	/// Version is checked first (before any session reset); mismatches require user confirmation.
	/// </summary>
	/// <param name="selectedPath">The file path of the session to load.</param>
	private void OpenSelectedSession(string selectedPath)
	{
		if (RejectIfSessionBusy("open a showfile"))
			return;
		if (!ConfirmIfUnsaved(PendingCloseKind.OpenPath, selectedPath))
			return;
		TaskUtil.Run(() => OpenSelectedSessionAsync(selectedPath), "SaveManager.OpenSelectedSession");
	}

	/// <summary>
	/// Shared async open: read/parse off the critical paint path, then apply via
	/// <see cref="CompleteOpenSessionAsync"/>. Used for startup and File → Open.
	/// </summary>
	/// <param name="selectedPath">Absolute path of the .c2 file.</param>
	/// <param name="isStartupOpen">
	/// True when this is the boot last-show open. A missing or unreadable file then
	/// falls back to a blank new session instead of leaving the workspace unseeded.
	/// </param>
	private async Task OpenSelectedSessionAsync(string selectedPath, bool isStartupOpen = false)
	{
		if (_isOpenInProgress || IsSessionLoading)
		{
			_globalSignals.EmitSignal(nameof(GlobalSignals.Log),
				"A showfile open is already in progress.", (int)LogType.Warning);
			GD.Print("SaveManager:OpenSelectedSession - Open already in progress; ignoring.");
			return;
		}

		if (!File.Exists(selectedPath))
		{
			_globalSignals.EmitSignal(nameof(GlobalSignals.Log), $"Session file not found: {selectedPath}", 2);
			GD.PrintErr("SaveManager:OpenSelectedSession - File not found: " + selectedPath);
			if (isStartupOpen)
				AbandonStartupOpenToNewSession(selectedPath, "Showfile not found", removeFromRecents: true);
			return;
		}

		// Empty workspace (startup / New Session): cover the read so the grey list never shows.
		// A live show stays interactive until the version gate passes and apply begins.
		bool coverRead = ShouldCoverReadPhase();
		if (coverRead)
			BeginSessionApply(selectedPath, "Reading showfile…", 2f);

		_isOpenInProgress = true;
		bool handedToVersionDialog = false;
		bool enteredComplete = false;
		_loadTimer = SessionLoadTimer.Start(selectedPath);
		try
		{
			try
			{
				_loadTimer.FileBytes = new FileInfo(selectedPath).Length;
			}
			catch
			{
				// Size is diagnostics only.
			}

			var (ok, saveData, readError) = await ReadSaveDataAsync(selectedPath);
			if (!ok)
			{
				_globalSignals.EmitSignal(nameof(GlobalSignals.Log),
					$"Failed to read session (session not changed): {readError}", 2);
				GD.PrintErr($"SaveManager:OpenSelectedSession - Read failed: {readError}");
				if (isStartupOpen)
					AbandonStartupOpenToNewSession(selectedPath, readError ?? "Failed to read showfile",
						removeFromRecents: false);
				return;
			}

			var fileVersion = ShowfileFormat.ReadVersion(saveData);
			GD.Print(
				$"SaveManager:OpenSelectedSession - File version: {fileVersion.ToDisplayString()}; " +
				$"app: {Cue2.Version.SemanticVersionString} format {ShowfileFormat.CurrentFormatVersion}");

			if (!fileVersion.RequiresVersionConfirmation)
			{
				if (!fileVersion.MatchesCurrentApp && !string.IsNullOrEmpty(fileVersion.AppVersion))
				{
					_globalSignals.EmitSignal(nameof(GlobalSignals.Log),
						$"Showfile app version differs ({fileVersion.AppVersion} → {Cue2.Version.SemanticVersionString}); " +
						$"format {fileVersion.FormatVersion} matches — opening without prompt.",
						0);
				}

				enteredComplete = true;
				await CompleteOpenSessionAsync(selectedPath, saveData, fileVersion, userConfirmedMismatch: false);
				return;
			}

			// Version dialog: current show (if any) is still live. Drop the empty-session overlay.
			if (coverRead)
				EndSessionApply();
			FinishLoadTimer("pre-apply");
			handedToVersionDialog = true;
			ShowVersionMismatchDialog(selectedPath, saveData, fileVersion);
		}
		catch (Exception ex)
		{
			GD.PrintErr($"SaveManager:OpenSelectedSessionAsync - {ex.Message}\n{ex.StackTrace}");
			_globalSignals.EmitSignal(nameof(GlobalSignals.Log),
				$"Failed to open session (session not changed): {ex.Message}", 2);
		}
		finally
		{
			if (!handedToVersionDialog)
				_isOpenInProgress = false;
			// Read failed (or threw) after a cover-read BeginSessionApply — drop the overlay.
			// Successful apply ends itself; version dialog already ended the cover-read overlay.
			if (coverRead && IsSessionLoading && !handedToVersionDialog)
				EndSessionApply();
			if (!enteredComplete && !handedToVersionDialog)
				FinishLoadTimer("failed");
		}
	}

	/// <summary>
	/// Shows the version-mismatch confirmation dialog. Session remains untouched until Attempt Open.
	/// </summary>
	private void ShowVersionMismatchDialog(string selectedPath, Dictionary saveData, ShowfileVersionInfo fileVersion)
	{
		DismissActiveVersionDialog();

		var dialog = VersionMismatchDialog.Create(out string loadError);
		if (dialog == null)
		{
			_globalSignals.EmitSignal(nameof(GlobalSignals.Log),
				$"Could not show version dialog ({loadError}). Open cancelled; session unchanged.", 2);
			GD.PrintErr($"SaveManager:ShowVersionMismatchDialog - {loadError}");
			return;
		}

		_isOpenInProgress = true;
		_activeVersionDialog = dialog;

		dialog.Configure(selectedPath, fileVersion);

		dialog.AttemptOpen += () =>
		{
			_activeVersionDialog = null;
			_isOpenInProgress = false;
			TaskUtil.Run(
				() => CompleteOpenSessionAsync(selectedPath, saveData, fileVersion, userConfirmedMismatch: true),
				"SaveManager.CompleteOpenSession");
		};

		dialog.Cancelled += () =>
		{
			_activeVersionDialog = null;
			_isOpenInProgress = false;
			_globalSignals.EmitSignal(nameof(GlobalSignals.Log),
				$"Open cancelled (version mismatch): {selectedPath}", 0);
			GD.Print("SaveManager:ShowVersionMismatchDialog - User cancelled open.");
		};

		// Parent under main scene when available so the window attaches cleanly.
		var parent = GetTree()?.Root?.GetNodeOrNull("Cue2Base") ?? (Node)this;
		parent.AddChild(dialog);
		dialog.ShowConfigured();

		_globalSignals.EmitSignal(nameof(GlobalSignals.Log),
			$"Showfile version differs ({fileVersion.ToDisplayString()}). Confirm before open.", 1);
	}

	/// <summary>
	/// Closes any open version dialog without proceeding with load.
	/// </summary>
	private void DismissActiveVersionDialog()
	{
		if (_activeVersionDialog != null && IsInstanceValid(_activeVersionDialog))
		{
			_activeVersionDialog.Hide();
			_activeVersionDialog.QueueFree();
		}

		_activeVersionDialog = null;
		_isOpenInProgress = false;
	}

	/// <summary>
	/// Migrates (if needed), resets the session, and applies save data.
	/// Called only after version matches or the user confirms Attempt Open.
	/// </summary>
	/// <remarks>
	/// The live session is wiped only after migration succeeds. If load then fails, the
	/// session is forced back to an empty New Session state and the path is <b>not</b>
	/// added to recents (see <see cref="FailOpenAfterReset"/>).
	/// </remarks>
	/// <param name="selectedPath">Absolute path of the .c2 file.</param>
	/// <param name="saveData">Already-parsed root dictionary (may be mutated by migration).</param>
	/// <param name="fileVersion">Version metadata as read from the file.</param>
	/// <param name="userConfirmedMismatch">True when the user accepted the version warning.</param>
	private async Task CompleteOpenSessionAsync(
		string selectedPath,
		Dictionary saveData,
		ShowfileVersionInfo fileVersion,
		bool userConfirmedMismatch)
	{
		bool sessionWiped = false;
		bool applyStarted = false;
		bool success = false;
		try
		{
			_loadTimer ??= SessionLoadTimer.Start(selectedPath);

			if (!IsSessionLoading)
				BeginSessionApply(selectedPath, "Opening show…", 5f);
			applyStarted = true;

			// Let the overlay paint before the first heavy main-thread chunk.
			_loadTimer.Pause();
			await YieldProcessFrame();

			EmitSessionLoadProgress(8f, "Checking showfile…", string.Empty, 0, 0);
			_loadTimer.Begin("migrate");

			// Migrate older (or stamp current) formats before applying to the live model.
			// Session is not wiped yet — failed migration leaves the previous show intact.
			if (ShowfileMigrator.NeedsMigration(fileVersion.FormatVersion) ||
			    ShowfileMigrator.IsNewerThanSupported(fileVersion.FormatVersion) ||
			    userConfirmedMismatch)
			{
				var migration = ShowfileMigrator.MigrateToCurrent(saveData, fileVersion.FormatVersion);
				if (!string.IsNullOrEmpty(migration.Log))
				{
					GD.Print($"SaveManager:CompleteOpenSession - Migration log:\n{migration.Log}");
					_globalSignals.EmitSignal(nameof(GlobalSignals.Log),
						$"Showfile migration: {migration.Log.Replace("\n", " | ")}", 0);
				}

				if (!migration.Success)
				{
					_globalSignals.EmitSignal(nameof(GlobalSignals.Log),
						$"Showfile migration failed (session not changed): {migration.Error}", 2);
					GD.PrintErr($"SaveManager:CompleteOpenSession - Migration failed: {migration.Error}");
					return;
				}
			}
			else
			{
				// Matching open path: ensure stamps exist for any later re-save consistency.
				ShowfileFormat.StampCurrentVersion(saveData);
			}

			if (!TryValidateOpenPayload(saveData, out var validateError))
			{
				_globalSignals.EmitSignal(nameof(GlobalSignals.Log),
					$"Showfile rejected (session not changed): {validateError}", 2);
				GD.PrintErr($"SaveManager:CompleteOpenSession - Validation failed: {validateError}");
				return;
			}

			EmitSessionLoadProgress(12f, "Loading settings…", string.Empty, 0, 0);

			// Wipe previous show only after version gate + successful migration + shape check.
			// Clear-for-open: do not seed a throwaway New Session (P1).
			_loadTimer.Begin("reset");
			ClearSessionForOpen();
			sessionWiped = true;

			ApplySessionPaths(selectedPath);

			_loadTimer.Pause();
			await YieldProcessFrame();

			if (!await LoadSessionFromDataAsync(saveData))
			{
				FailOpenAfterReset(selectedPath, "Failed to apply showfile data.");
				return;
			}

			// Success only: history, recents, autosave, title. Housekeeping after overlay hides.
			_openedFromNewerFormat = fileVersion.IsNewerFormat;
			_globalData.HistoryManager?.Clear();
			_globalData.UserDataManager?.AddRecentShowFile(selectedPath, persistImmediately: false);
			ConfigureAutosave();
			GetNodeOrNull<MainTitleBarUI>("/root/Cue2Base/MainWindowHandles/MainTitleBar")
				?.CallDeferred("UpdateTitle");

			_globalSignals.EmitSignal(nameof(GlobalSignals.Log),
				$"Session opened: {selectedPath}", 0);

			if (_openedFromNewerFormat)
			{
				_globalSignals.EmitSignal(nameof(GlobalSignals.Log),
					"Opened a newer showfile format. Saving will not overwrite this file — use Save As " +
					"to create a copy this version of Cue2 can own.",
					(int)LogType.Warning);
			}

			if (userConfirmedMismatch)
			{
				_globalSignals.EmitSignal(nameof(GlobalSignals.Log),
					$"Opened showfile after version confirmation: {selectedPath}", 0);
			}

			_loadTimer?.MarkApplyComplete();
			TaskUtil.Run(RunDeferredOpenHousekeepingAsync, "SaveManager.DeferredOpenHousekeeping");
			success = true;
		}
		catch (Exception ex)
		{
			GD.PrintErr($"SaveManager:CompleteOpenSession - Error: {ex.Message}\n{ex.StackTrace}");
			if (sessionWiped)
			{
				// Previous show already gone — do not leave a half-applied open or pollute recents.
				try
				{
					FailOpenAfterReset(selectedPath, ex.Message);
				}
				catch (Exception failEx)
				{
					GD.PrintErr($"SaveManager:CompleteOpenSession - FailOpenAfterReset: {failEx.Message}");
					_globalSignals.EmitSignal(nameof(GlobalSignals.Log),
						$"Failed to open session: {ex.Message}", 2);
				}
			}
			else
			{
				_globalSignals.EmitSignal(nameof(GlobalSignals.Log),
					$"Failed to open session (session not changed): {ex.Message}", 2);
			}
		}
		finally
		{
			if (applyStarted)
				EndSessionApply();
			if (!success)
				FinishLoadTimer("failed");
		}
	}

	/// <summary>
	/// Lightweight pre-reset check so we do not wipe the live show for an obviously unusable file.
	/// </summary>
	/// <param name="saveData">Parsed showfile root.</param>
	/// <param name="error">Readable failure reason.</param>
	/// <returns>True when the payload is worth attempting a load after reset.</returns>
	private static bool TryValidateOpenPayload(Dictionary saveData, out string error)
	{
		error = null;
		if (saveData == null || saveData.Count == 0)
		{
			error = "Showfile is empty or not a valid object.";
			return false;
		}

		// Accept shows that only have settings or only cues (legacy / partial tooling dumps),
		// but reject non-dictionary blocks that would throw after wipe.
		if (saveData.ContainsKey("settings"))
		{
			var settingsVar = saveData["settings"];
			if (settingsVar.VariantType != Variant.Type.Dictionary)
			{
				error = "Showfile 'settings' block is not an object.";
				return false;
			}
		}

		if (saveData.ContainsKey("cues"))
		{
			var cuesVar = saveData["cues"];
			if (cuesVar.VariantType != Variant.Type.Dictionary)
			{
				error = "Showfile 'cues' block is not an object.";
				return false;
			}
		}

		return true;
	}

	/// <summary>
	/// After a failed open that already wiped the previous show: force a clean empty session,
	/// do not add the path to recents, and surface a clear error.
	/// </summary>
	/// <param name="selectedPath">Path that failed to open (for logs only).</param>
	/// <param name="reason">Failure reason shown to the user.</param>
	private void FailOpenAfterReset(string selectedPath, string reason)
	{
		GD.PrintErr($"SaveManager:FailOpenAfterReset - path={selectedPath} reason={reason}");

		// Drop any half-applied settings/cues from the failed load.
		ResetSession(clearSessionIdentity: true, logAsNewSession: false);
		_globalData.HistoryManager?.Clear();
		ConfigureAutosave();
		GetNodeOrNull<MainTitleBarUI>("/root/Cue2Base/MainWindowHandles/MainTitleBar")
			?.CallDeferred("UpdateTitle");

		_globalSignals.EmitSignal(nameof(GlobalSignals.Log),
			$"Failed to open showfile — session reset to empty (not added to recents): {reason}", 2);
	}

	/// <summary>
	/// Reads and parses a plain UTF-8 .c2 on a worker thread. Does not modify session state.
	/// </summary>
	/// <remarks>
	/// Read + UTF-8 / <c>{</c> checks run off-main. <see cref="Json.Parse"/> is tried on the
	/// worker (same as save stringify). If Godot objects refuse a background thread, parse
	/// retries on the main thread with the already-read string.
	/// </remarks>
	/// <param name="selectedPath">Absolute path to the .c2 file.</param>
	/// <returns>Ok flag, parsed root dictionary, and error text.</returns>
	private async Task<(bool Ok, Dictionary Data, string Error)> ReadSaveDataAsync(string selectedPath)
	{
		string jsonString;
		try
		{
			if (!File.Exists(selectedPath))
				return (false, null, "file not found");

			EmitSessionLoadProgress(3f, "Reading showfile…", string.Empty, 0, 0);
			SessionLoadTimer.Current?.Begin("read");
			jsonString = await Task.Run(() => ReadShowfileText(selectedPath)).ConfigureAwait(true);
		}
		catch (Exception ex)
		{
			return (false, null, ex.Message);
		}

		if (string.IsNullOrWhiteSpace(jsonString))
			return (false, null, "empty or unreadable");

		string trimmed = jsonString.TrimStart();
		if (trimmed.Length == 0 || (trimmed[0] != '{' && trimmed[0] != '['))
			return (false, null, "not JSON text (expected plain UTF-8 .c2)");

		EmitSessionLoadProgress(6f, "Reading showfile…", string.Empty, 0, 0);
		SessionLoadTimer.Current?.Begin("parse");

		try
		{
			var worker = await Task.Run(() => ParseShowfileJson(jsonString)).ConfigureAwait(true);
			if (worker.Ok)
			{
				SessionLoadTimer.Current?.Pause();
				return (true, worker.Data, null);
			}

			if (!worker.RetryOnMain)
			{
				SessionLoadTimer.Current?.Pause();
				return (false, null, worker.Error);
			}

			GD.Print($"SaveManager:ReadSaveDataAsync - Worker parse threw ({worker.Error}); retrying on main thread.");
			var main = ParseShowfileJson(jsonString);
			SessionLoadTimer.Current?.Pause();
			return main.Ok ? (true, main.Data, null) : (false, null, main.Error);
		}
		catch (Exception ex)
		{
			SessionLoadTimer.Current?.Pause();
			return (false, null, ex.Message);
		}
	}

	/// <summary>
	/// Reads a showfile as strict UTF-8 (no BOM required; invalid bytes throw).
	/// Safe to call from a worker thread.
	/// </summary>
	private static string ReadShowfileText(string selectedPath)
	{
		return File.ReadAllText(selectedPath, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true));
	}

	/// <summary>
	/// Parses showfile JSON into a root dictionary. Safe to attempt on a worker;
	/// callers retry on the main thread when <c>RetryOnMain</c> is true.
	/// </summary>
	private static (bool Ok, Dictionary Data, string Error, bool RetryOnMain) ParseShowfileJson(string jsonString)
	{
		try
		{
			using var json = new Json();
			Error parseResult = json.Parse(jsonString);
			if (parseResult != Error.Ok)
				return (false, null, $"JSON parse error: {parseResult}", false);

			var saveData = json.Data.AsGodotDictionary();
			if (saveData == null)
				return (false, null, "root is not a dictionary", false);

			return (true, saveData, null, false);
		}
		catch (Exception ex)
		{
			return (false, null, ex.Message, true);
		}
	}

	/// <summary>
	/// Builds the root save dictionary (version stamps + cues + settings).
	/// Waveform peaks are omitted from component payloads; they live under <c>Waveforms/</c>.
	/// </summary>
	/// <returns>Dictionary ready for JSON serialization.</returns>
	private Dictionary BuildSaveDataDictionary()
	{
		var saveData = new Dictionary();
		ShowfileFormat.StampCurrentVersion(saveData);

		var cueSaveData = _globalData.Cuelist.GetData();
		saveData.Add("cues", cueSaveData);

		var settingsData = _globalData.Settings.GetData();
		saveData.Add("settings", settingsData);

		return saveData;
	}

	/// <summary>
	/// Writes in-memory <c>WaveformData</c> (from legacy showfiles) into <c>SessionDir/Waveforms</c>
	/// when a matching <c>.c2wf</c> cache file is not already present.
	/// </summary>
	/// <remarks>
	/// New saves never embed peaks. Without this one-shot migration, opening an old show and
	/// re-saving would drop peaks from the .c2 with no disk cache yet, forcing a full FFmpeg regen.
	/// </remarks>
	private void MigrateLegacyEmbeddedWaveformsToDisk()
	{
		string root = _globalData?.SessionWaveformsPath;
		if (string.IsNullOrEmpty(root))
			return;

		try
		{
			if (!Directory.Exists(root))
				Directory.CreateDirectory(root);
		}
		catch (Exception ex)
		{
			GD.PrintErr($"SaveManager:MigrateLegacyEmbeddedWaveformsToDisk - Create dir failed: {ex.Message}");
			return;
		}

		var index = CueList.CueIndex;
		if (index == null || index.Count == 0)
			return;

		int written = 0;
		foreach (var cue in index.Values)
		{
			if (cue?.Components == null) continue;
			foreach (var comp in cue.Components)
			{
				string mediaPath = null;
				byte[] peaks = null;
				if (comp is AudioComponent audio)
				{
					mediaPath = audio.AudioFile;
					peaks = audio.WaveformData;
				}
				else if (comp is VideoComponent video)
				{
					mediaPath = video.VideoFile;
					peaks = video.WaveformData;
				}

				if (string.IsNullOrEmpty(mediaPath) || peaks == null || peaks.Length == 0)
					continue;
				if (WaveformPeaks.FromBytes(peaks) == null)
					continue;

				try
				{
					string absolute = _globalData.ResolveMediaPath(mediaPath);
					string cachePath = Path.Combine(root, WaveformPeaks.CacheFileName(absolute));
					if (File.Exists(cachePath))
						continue;
					File.WriteAllBytes(cachePath, peaks);
					written++;
				}
				catch (Exception ex)
				{
					GD.PrintErr($"SaveManager:MigrateLegacyEmbeddedWaveformsToDisk - {mediaPath}: {ex.Message}");
				}
			}
		}

		if (written > 0)
			GD.Print($"SaveManager:MigrateLegacyEmbeddedWaveformsToDisk - Migrated {written} waveform cache file(s).");
	}

	private bool _ignoreNewSessionSignal;

	/// <summary>File → New / New Session hotkey. Prompts when the document is dirty.</summary>
	public void RequestNewSession()
	{
		if (RejectIfSessionBusy("start a new session"))
			return;
		if (!ConfirmIfUnsaved(PendingCloseKind.NewSession))
			return;
		PerformNewSession();
	}

	/// <summary>Signal handler for File → New / New Session hotkey.</summary>
	private void OnNewSession()
	{
		if (_ignoreNewSessionSignal)
			return;
		RequestNewSession();
	}

	private void PerformNewSession()
	{
		ResetSession(clearSessionIdentity: true, logAsNewSession: true);
		_globalData?.HistoryManager?.Clear();
		_ignoreNewSessionSignal = true;
		_globalSignals?.EmitSignal(nameof(GlobalSignals.NewSession));
		_ignoreNewSessionSignal = false;
	}

	/// <summary>
	/// File → Quit / window close. Prompts when a footer background task is running,
	/// then when the document is dirty, then quits.
	/// </summary>
	public void RequestQuit()
	{
		if (_quitStarted)
			return;
		if (IsUnsavedDialogOpen || _pendingClose != PendingCloseKind.None)
			return;
		if (IsSessionLoading || _isOpenInProgress)
		{
			_globalSignals?.EmitSignal(nameof(GlobalSignals.Log),
				"Please wait — a showfile is still loading. Cannot quit.", (int)LogType.Info);
			return;
		}

		if (!ConfirmIfBackgroundTask())
			return;
		if (!ConfirmIfUnsaved(PendingCloseKind.Quit))
			return;
		BeginQuit();
	}

	private bool IsUnsavedDialogOpen =>
		_unsavedDialog != null && GodotObject.IsInstanceValid(_unsavedDialog);

	/// <summary>
	/// Returns true when the caller should proceed immediately. False means a dialog is showing
	/// (or the user cancelled later); the pending action runs after Save &amp; close / Close.
	/// </summary>
	private bool ConfirmIfUnsaved(PendingCloseKind kind, string openPath = null)
	{
		if (!HasUnsavedChanges)
			return true;
		if (IsUnsavedDialogOpen || _pendingClose != PendingCloseKind.None)
			return false;

		_pendingClose = kind;
		_pendingOpenPath = openPath;
		_pendingBackgroundTaskPrompt = false;
		_pendingBackgroundTaskName = null;
		// Overlay is a Control in the operator window. Still defer so we are not
		// parenting during CloseRequested.
		CallDeferred(nameof(ShowUnsavedChangesDialog));
		return false;
	}

	/// <summary>
	/// Returns true when quit can proceed. False means the background-task overlay is showing.
	/// </summary>
	private bool ConfirmIfBackgroundTask()
	{
		if (!TryGetActiveBackgroundTask(out string taskName))
			return true;
		if (IsUnsavedDialogOpen || _pendingClose != PendingCloseKind.None)
			return false;

		_pendingClose = PendingCloseKind.Quit;
		_pendingBackgroundTaskPrompt = true;
		_pendingBackgroundTaskName = taskName;
		CallDeferred(nameof(ShowUnsavedChangesDialog));
		return false;
	}

	/// <summary>
	/// True when a footer-tracked background process is still running (media copy, bulk
	/// cuelist edit, update download/check). Idle "update available" chrome is not busy.
	/// </summary>
	private bool TryGetActiveBackgroundTask(out string taskName)
	{
		taskName = string.Empty;
		string footerStatus = StripProgressPercent(_globalData?.FooterBackgroundTaskStatus);

		_mediaBackupManager ??= GetNodeOrNull<MediaBackupManager>("/root/MediaBackupManager");
		if (_mediaBackupManager != null && _mediaBackupManager.IsBusy)
		{
			taskName = FirstNonEmpty(footerStatus, UiLocalizer.T("Copying media"));
			return true;
		}

		var cuelist = _globalData?.Cuelist;
		if (cuelist != null && cuelist.IsBulkOpInProgress)
		{
			string bulk = string.IsNullOrEmpty(cuelist.BulkOpLabel)
				? UiLocalizer.T("Cuelist edit")
				: cuelist.BulkOpLabel;
			taskName = FirstNonEmpty(footerStatus, bulk);
			return true;
		}

		var updates = _globalData?.UpdateService;
		if (updates != null)
		{
			if (updates.UiState == UpdateUiState.Downloading)
			{
				taskName = FirstNonEmpty(footerStatus, UiLocalizer.T("Downloading…"));
				return true;
			}

			if (updates.UiState == UpdateUiState.Checking)
			{
				taskName = FirstNonEmpty(footerStatus, UiLocalizer.T("Checking for updates…"));
				return true;
			}
		}

		return false;
	}

	private static string FirstNonEmpty(string preferred, string fallback) =>
		string.IsNullOrWhiteSpace(preferred) ? fallback ?? string.Empty : preferred.Trim();

	/// <summary>
	/// Drops a trailing <c> 45%</c> from footer labels so the quit prompt names the task, not the percent.
	/// </summary>
	private static string StripProgressPercent(string status)
	{
		if (string.IsNullOrWhiteSpace(status))
			return string.Empty;

		string s = status.Trim();
		if (!s.EndsWith('%'))
			return s;

		int i = s.Length - 2;
		while (i >= 0 && (char.IsDigit(s[i]) || s[i] == '.'))
			i--;
		while (i >= 0 && char.IsWhiteSpace(s[i]))
			i--;
		return i >= 0 ? s[..(i + 1)].Trim() : s;
	}

	private void ShowUnsavedChangesDialog()
	{
		if (_pendingClose == PendingCloseKind.None)
			return;
		if (IsUnsavedDialogOpen)
			return;

		var dialog = UnsavedChangesDialog.Create(out string err);
		if (dialog == null)
		{
			GD.PrintErr($"SaveManager:ShowUnsavedChangesDialog - {err}");
			_globalSignals?.EmitSignal(nameof(GlobalSignals.Log),
				"Could not open the unsaved-changes dialog.", (int)LogType.Error);
			ClearPendingClose();
			return;
		}

		_unsavedDialog = dialog;
		dialog.SaveAndClose += OnUnsavedSaveAndClose;
		dialog.DiscardAndClose += OnUnsavedDiscardAndClose;
		dialog.Cancelled += OnUnsavedCancelled;
		dialog.TreeExiting += OnUnsavedDialogExiting;
		// Parent under Cue2Base so the overlay fills the operator viewport.
		Node parent = GetTree()?.Root?.GetNodeOrNull("Cue2Base");
		parent ??= GetTree()?.Root;
		parent ??= this;
		parent.AddChild(dialog);
		if (_pendingBackgroundTaskPrompt)
			dialog.ConfigureBackgroundTask(_pendingBackgroundTaskName);
		else
			dialog.Configure(_globalData?.SessionName);
		dialog.ShowConfigured();
	}

	private void OnUnsavedSaveAndClose()
	{
		if (_pendingBackgroundTaskPrompt)
		{
			ClearPendingClose();
			return;
		}

		TaskUtil.Run(SaveThenPendingCloseAsync, "SaveManager.SaveThenClose");
	}

	private async Task SaveThenPendingCloseAsync()
	{
		bool canSaveInPlace = !string.IsNullOrEmpty(_globalData?.SessionPath)
		                      && !string.IsNullOrEmpty(_globalData.SessionName)
		                      && !_openedFromNewerFormat;
		if (canSaveInPlace)
		{
			bool ok = await SaveSessionAsync(_globalData.SessionPath);
			if (ok)
				ExecutePendingClose();
			else
				ClearPendingClose();
			return;
		}

		_proceedAfterSaveAs = true;
		SaveAs();
	}

	private void OnUnsavedDiscardAndClose()
	{
		if (_pendingBackgroundTaskPrompt)
		{
			_pendingBackgroundTaskPrompt = false;
			_pendingBackgroundTaskName = null;
			if (HasUnsavedChanges)
			{
				CallDeferred(nameof(ShowUnsavedChangesDialog));
				return;
			}
		}

		CallDeferred(nameof(ExecutePendingClose));
	}

	private void OnUnsavedCancelled()
	{
		ClearPendingClose();
	}

	private void OnUnsavedDialogExiting()
	{
		if (_unsavedDialog == null)
			return;
		bool choiceMade = _unsavedDialog.ChoiceMade;
		_unsavedDialog.SaveAndClose -= OnUnsavedSaveAndClose;
		_unsavedDialog.DiscardAndClose -= OnUnsavedDiscardAndClose;
		_unsavedDialog.Cancelled -= OnUnsavedCancelled;
		_unsavedDialog.TreeExiting -= OnUnsavedDialogExiting;
		_unsavedDialog = null;

		// Overlay QueueFreed without a button handler — drop the pending New/Open/Quit.
		if (!choiceMade && !_quitStarted)
			ClearPendingClose();
	}

	private void ExecutePendingClose()
	{
		PendingCloseKind kind = _pendingClose;
		string path = _pendingOpenPath;
		ClearPendingClose();

		switch (kind)
		{
			case PendingCloseKind.NewSession:
				PerformNewSession();
				break;
			case PendingCloseKind.OpenDialog:
				PopupOpenDialog();
				break;
			case PendingCloseKind.OpenPath:
				if (!string.IsNullOrEmpty(path))
					TaskUtil.Run(() => OpenSelectedSessionAsync(path), "SaveManager.OpenSelectedSession");
				break;
			case PendingCloseKind.Quit:
				BeginQuit();
				break;
		}
	}

	private void ClearPendingClose()
	{
		_pendingClose = PendingCloseKind.None;
		_pendingOpenPath = null;
		_proceedAfterSaveAs = false;
		_pendingBackgroundTaskPrompt = false;
		_pendingBackgroundTaskName = null;
	}

	private void PopupOpenDialog()
	{
		if (_openDialog == null || !IsInstanceValid(_openDialog))
			_openDialog = GetNodeOrNull<FileDialog>("/root/Cue2Base/OpenDialog");

		if (_openDialog == null || !IsInstanceValid(_openDialog))
		{
			_globalSignals.EmitSignal(nameof(GlobalSignals.Log),
				"SaveManager:OpenSession - Open dialog is not available.", (int)LogType.Error);
			GD.PrintErr("SaveManager:OpenSession - /root/Cue2Base/OpenDialog not found.");
			return;
		}

		_openDialog.PopupCentered();
	}

	/// <summary>
	/// Persists prefs then quits on the next idle frame so we are never inside
	/// <see cref="Window.CloseRequested"/> or a dialog button handler (macOS hang).
	/// </summary>
	private void BeginQuit()
	{
		if (_quitStarted)
			return;
		_quitStarted = true;
		_globalData?.UserDataManager?.PersistUserData();
		CallDeferred(nameof(FinishQuit));
	}

	private void FinishQuit()
	{
		GetTree()?.Quit();
	}

	/// <summary>
	/// Wipes the live show for a showfile apply without seeding a playable empty session.
	/// </summary>
	/// <remarks>
	/// Stops playback, frees cues/patches/windows, clears OSC/MIDI/cue lights.
	/// Does not create a Default Patch or default canvas screen, and does not
	/// poke inspectors beyond dropping focus on the previous (now-freed) cue.
	/// <see cref="LoadSessionFromDataAsync"/> then constructs the incoming show.
	/// File → New still uses <see cref="ResetSession"/>.
	/// </remarks>
	private void ClearSessionForOpen()
	{
		_globalSignals?.EmitSignal(nameof(GlobalSignals.StopAll));
		_openedFromNewerFormat = false;

		_globalData.SessionName = null;
		_globalData.SessionPath = null;
		_globalData.SessionDir = null;
		_globalData.SessionAudioPath = null;
		_globalData.SessionVideoPath = null;
		_globalData.SessionImagesPath = null;
		_globalData.SessionWaveformsPath = null;

		_globalData.FocusedCue = -1;
		_globalData.NextCue = -1;
		_globalData.CueTotal = 0;

		_globalData.Cuelist?.ResetCuelist();
		_globalData.Devices?.ResetAudioDevices();
		_globalData.Settings?.ClearForOpen();

		GetNodeOrNull<MediaHealthService>("/root/MediaHealthService")?.ClearAll();
		_mediaBackupManager?.ClearPendingJobs();

		// Drop inspector bindings to freed cues; do not SyncShellInspector (no media reload).
		_globalSignals?.EmitSignal(nameof(GlobalSignals.ShellFocused), -1);

		ConfigureAutosave();
		GD.Print("SaveManager:ClearSessionForOpen - Session cleared for showfile apply.");
	}

	/// <summary>
	/// Fully clears the open show and restores session defaults (File → New / New Session hotkey).
	/// </summary>
	/// <param name="clearSessionIdentity">
	/// When true (New Session), clears SessionPath/name/media dirs.
	/// When opening a file, paths are cleared then re-applied by the caller after this wipe.
	/// </param>
	/// <param name="logAsNewSession">When true, logs the user-facing "New session" message.</param>
	private void ResetSession(bool clearSessionIdentity, bool logAsNewSession)
	{
		// Stop live playback before tearing down cues / devices
		_globalSignals?.EmitSignal(nameof(GlobalSignals.StopAll));

		// New / failed open wipe — no longer protecting a newer-format original path.
		_openedFromNewerFormat = false;

		if (clearSessionIdentity)
		{
			// Detach from any saved show path (hotkey New may skip the menu path-clearing)
			_globalData.SessionName = null;
			_globalData.SessionPath = null;
			_globalData.SessionDir = null;
			_globalData.SessionAudioPath = null;
			_globalData.SessionVideoPath = null;
			_globalData.SessionImagesPath = null;
			_globalData.SessionWaveformsPath = null;
		}

		_globalData.FocusedCue = -1;
		_globalData.NextCue = -1;
		_globalData.CueTotal = 0;

		// Document model
		_globalData.Cuelist?.ResetCuelist();
		// Legacy Devices map only (no SDL). Real open-device reconcile runs inside ResetSettings
		// via Settings.ReconcileOpenAudioDevices → AudioDevices.SyncOpenDevices.
		_globalData.Devices?.ResetAudioDevices();
		_globalData.Settings?.ResetSettings();

		// Transient services
		GetNodeOrNull<MediaHealthService>("/root/MediaHealthService")?.ClearAll();
		_mediaBackupManager?.ClearPendingJobs();

		// Inspectors / selection
		_globalSignals?.EmitSignal(nameof(GlobalSignals.ShellFocused), -1);
		_globalSignals?.EmitSignal(nameof(GlobalSignals.SyncShellInspector));

		// No path → autosave off (reconfigured again after Open applies paths)
		ConfigureAutosave();

		if (logAsNewSession)
			_globalSignals?.EmitSignal(nameof(GlobalSignals.Log), "New session — show data reset to defaults.", 0);
		GD.Print("SaveManager:ResetSession - Full session reset complete.");
	}
	
	
	/// <summary>
	/// Applies settings then cues from an already-parsed (and optionally migrated) dictionary.
	/// Cue models are built synchronously (GO-safe), then the viewport is bound.
	/// Media health is deferred to <see cref="RunDeferredOpenHousekeepingAsync"/>.
	/// </summary>
	/// <param name="saveData">Root showfile dictionary with settings/cues keys.</param>
	/// <returns>True when settings and cues were applied without throwing.</returns>
	private async Task<bool> LoadSessionFromDataAsync(Dictionary saveData)
	{
		if (saveData == null)
		{
			GD.PrintErr("SaveManager:LoadSessionFromData - save data is null");
			return false;
		}

		try
		{
			if (saveData.ContainsKey("settings"))
			{
				GD.Print("SaveManager:LoadSessionFromData - Loading Settings");
				var settingsData = saveData["settings"].AsGodotDictionary();
				var settings = _globalData.Settings;

				EmitSessionLoadProgress(14f, "Opening audio devices…", string.Empty, 0, 0);
				settings.LoadAudioFromData(settingsData);
				SessionLoadTimer.Current?.Pause();
				await YieldProcessFrame();

				EmitSessionLoadProgress(16f, "Creating outputs…", string.Empty, 0, 0);
				settings.LoadDisplaysFromData(settingsData);
				SessionLoadTimer.Current?.Pause();
				await YieldProcessFrame();

				EmitSessionLoadProgress(18f, "Loading settings…", string.Empty, 0, 0);
				settings.LoadRemainingFromData(settingsData);
				SessionLoadTimer.Current?.Pause();
				await YieldProcessFrame();
			}

			if (saveData.ContainsKey("cues") && _globalData.Cuelist != null)
			{
				GD.Print("SaveManager:LoadSessionFromData - Loading Cues");
				EmitSessionLoadProgress(20f, "Loading cues…", string.Empty, 0, 0);
				var cuesData = saveData["cues"].AsGodotDictionary();
				_globalData.Cuelist.LoadCueModels(cuesData);
				int cueCount = _globalData.Cuelist.TotalCueCount;
				OnCueLoadProgress(cueCount, cueCount);
			}

			// Models (or settings-only show) exist — GO may fire while the first bind finishes.
			MarkPlaybackReady();
			EmitSessionLoadProgress(100f, "Loading cues…", string.Empty, 1, 1);

			if (saveData.ContainsKey("cues") && _globalData.Cuelist != null)
				_globalData.Cuelist.BindLoadedViewport();

			return true;
		}
		catch (Exception ex)
		{
			GD.PrintErr($"SaveManager:LoadSessionFromData - Error: {ex.Message}  \n{ex.StackTrace}");
			return false;
		}
	}

	private void OnCueLoadProgress(int completed, int total)
	{
		float percent = total <= 0 ? 95f : 20f + completed * 75f / total;
		string detail = total > 0 ? $"{completed}/{total} cues" : string.Empty;
		EmitSessionLoadProgress(percent, "Loading cues…", detail, completed, total);
	}
	
	/// <summary>
	/// Performs an autosave by saving the current session data as a backup.
	/// Also ensures the main file is up to date.
	/// </summary>
	private void PerformAutosave()
	{
		TaskUtil.Run(PerformAutosaveAsync, "SaveManager.PerformAutosave");
	}

	private async Task PerformAutosaveAsync()
	{
		try
		{
			if (!GodotObject.IsInstanceValid(this) || _globalData == null)
				return;
			if (string.IsNullOrEmpty(_globalData.SessionPath) || string.IsNullOrEmpty(_globalData.SessionName))
			{
				_autosaveTimer?.Stop();
				return;
			}

			_globalSignals?.EmitSignal(nameof(GlobalSignals.Log), "Performing autosave...", 0);
			GD.Print("SaveManager:PerformAutosave - Autosave triggered.");

			// First, update the main file (off-main stringify/write)
			await SaveSessionAsync(_globalData.SessionPath);
			if (!GodotObject.IsInstanceValid(this))
				return;

			// Then create a timestamped backup in the session's Backups folder.
			await CreateAutosaveBackupAsync();
		}
		catch (Exception ex)
		{
			GD.PrintErr($"SaveManager:PerformAutosaveAsync - {ex.Message}");
			_globalSignals?.EmitSignal(nameof(GlobalSignals.Log),
				$"Autosave failed: {ex.Message}", 2);
		}
	}

	/// <summary>
	/// Creates a timestamped backup of the current session in the session's Backups folder.
	/// Prunes old backups to respect the BackupDepth setting.
	/// </summary>
	private async Task CreateAutosaveBackupAsync()
	{
		if (string.IsNullOrEmpty(_globalData.SessionPath) || string.IsNullOrEmpty(_globalData.SessionName))
			return;

		string sessionDir = _globalData.SessionPath.GetBaseDir();
		string backupDir = sessionDir + "/Backups";

		try
		{
			if (!DirAccess.DirExistsAbsolute(backupDir))
			{
				DirAccess.MakeDirAbsolute(backupDir);
			}

			int depth = _globalData.UserDataManager?.BackupDepth ?? 3;
			if (depth < 1) depth = 1;

			string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
			string backupName = $"{_globalData.SessionName}_autosave_{timestamp}.c2";
			string backupPath = backupDir + "/" + backupName;

			// Snapshot on main; stringify + plain write on worker (same as SaveSession).
			var saveData = BuildSaveDataDictionary();
			var writeResult = await Task.Run(() => WritePlainShowfile(backupPath, saveData))
				.ConfigureAwait(true);

			if (writeResult.Ok)
			{
				_globalSignals.EmitSignal(nameof(GlobalSignals.Log), $"Autosave backup created: {backupName}", 0);
				GD.Print($"SaveManager:CreateAutosaveBackup - Backup saved to {backupPath}");
			}
			else
			{
				_globalSignals.EmitSignal(nameof(GlobalSignals.Log),
					$"Autosave backup failed: {writeResult.Error}", 2);
				GD.PrintErr($"SaveManager:CreateAutosaveBackup - Write failed: {writeResult.Error}");
			}

			// Prune old backups (directory listing on main)
			PruneAutosaveBackups(backupDir, depth);
		}
		catch (Exception ex)
		{
			_globalSignals.EmitSignal(nameof(GlobalSignals.Log), $"Autosave backup failed: {ex.Message}", 2);
			GD.PrintErr($"SaveManager:CreateAutosaveBackup - Error: {ex.Message}");
		}
	}

	/// <summary>
	/// Path equality for save/open guards (normalized absolute paths, case-insensitive on Windows).
	/// </summary>
	private static bool PathsEqual(string a, string b)
	{
		if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b))
			return false;
		try
		{
			string na = System.IO.Path.GetFullPath(a.Replace('\\', '/'));
			string nb = System.IO.Path.GetFullPath(b.Replace('\\', '/'));
			return string.Equals(na, nb, StringComparison.OrdinalIgnoreCase);
		}
		catch
		{
			return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
		}
	}

	/// <summary>
	/// Removes old autosave backups so that only <paramref name="maxBackups"/> most recent ones remain.
	/// </summary>
	/// <remarks>
	/// Uses <see cref="Directory.GetFiles"/> rather than Godot <see cref="DirAccess.GetNext"/> alone
	/// (listing requires <c>ListDirBegin</c>; without it prune never saw any files and backups grew unbounded).
	/// </remarks>
	private void PruneAutosaveBackups(string backupDir, int maxBackups)
	{
		if (maxBackups < 1)
			maxBackups = 1;

		if (string.IsNullOrEmpty(backupDir) || !Directory.Exists(backupDir))
			return;

		var backupFiles = new System.Collections.Generic.List<string>();
		try
		{
			foreach (string path in Directory.GetFiles(backupDir))
			{
				string fileName = Path.GetFileName(path);
				if (string.IsNullOrEmpty(fileName))
					continue;
				// Match CreateAutosaveBackup naming: {SessionName}_autosave_{timestamp}.c2
				if (fileName.Contains("_autosave_", StringComparison.Ordinal) &&
				    fileName.EndsWith(".c2", StringComparison.OrdinalIgnoreCase))
				{
					backupFiles.Add(path);
				}
			}
		}
		catch (Exception ex)
		{
			GD.PrintErr($"SaveManager:PruneAutosaveBackups - Failed to list {backupDir}: {ex.Message}");
			return;
		}

		if (backupFiles.Count <= maxBackups)
			return;

		// Sort by last write time, oldest first
		backupFiles.Sort((a, b) => File.GetLastWriteTimeUtc(a).CompareTo(File.GetLastWriteTimeUtc(b)));

		int toDelete = backupFiles.Count - maxBackups;
		for (int i = 0; i < toDelete; i++)
		{
			try
			{
				File.Delete(backupFiles[i]);
				GD.Print($"SaveManager:PruneAutosaveBackups - Deleted old backup {Path.GetFileName(backupFiles[i])}");
			}
			catch (Exception ex)
			{
				GD.PrintErr($"SaveManager:PruneAutosaveBackups - Failed to delete {backupFiles[i]}: {ex.Message}");
			}
		}
	}
}

