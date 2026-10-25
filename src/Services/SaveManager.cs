// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Cue2.Domain.Cuelist;
using Cue2.Domain.Cues;
using Cue2.Domain.Playback;
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

	/// <summary>Showfile writes currently running (manual save, autosave or backup). Main thread only.</summary>
	private int _savesInFlight;

	/// <summary>
	/// Bumped whenever the loaded show is cleared (open, New Session, failed open). A save that
	/// finishes after this changed must not act on whatever show is loaded now. Main thread only.
	/// </summary>
	private int _sessionGeneration;

	/// <summary>
	/// Serializes every showfile write (main file and autosave backups) so at most one
	/// write runs at a time. Waited on inside the worker task, never on the main thread.
	/// </summary>
	private readonly SemaphoreSlim _writeGate = new SemaphoreSlim(1, 1);

	/// <summary>
	/// Newest save generation queued per target path (key from <see cref="WriteGateKey"/>).
	/// A queued write that is older than the newest one for the same path is skipped, so the
	/// newest snapshot is the one left on disk.
	/// </summary>
	private readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> _latestGenByPath = new();

	private Godot.Timer _autosaveTimer;

	/// <summary>How long after a GO or content start autosave waits before it runs.</summary>
	private const double AutosaveQuietSeconds = 2.0;

	/// <summary>Seconds between retries while autosave is deferred.</summary>
	private const double AutosaveRetrySeconds = 1.0;

	/// <summary>Longest autosave deferral; after this the save runs even if cues keep starting.</summary>
	private const double AutosaveMaxDeferSeconds = 120.0;

	/// <summary>True while a deferred-autosave retry timer is pending (at most one at a time).</summary>
	private bool _autosaveRetryPending;

	/// <summary>Stopwatch ticks when the current run of deferrals began, or 0 when none.</summary>
	private long _autosaveDeferStartTicks;

	/// <summary>P0 stage timings for the in-flight open (null when idle).</summary>
	private SessionLoadTimer _loadTimer;

	private enum PendingCloseKind
	{
		None,
		NewSession,
		OpenDialog,
		OpenPath,
		Quit,
		UpdateRestart
	}

	private PendingCloseKind _pendingClose;
	private string _pendingOpenPath;
	private bool _proceedAfterSaveAs;
	private UnsavedChangesDialog _unsavedDialog;
	private bool _quitStarted;
	private bool _pendingBackgroundTaskPrompt;
	private string _pendingBackgroundTaskName;

	/// <summary>Starts the update helper once an Install and Restart quit is certain.</summary>
	private Func<bool> _updateRestartStarter;

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
	/// True when this session already has a name and on-disk path (File → Save can write in place).
	/// </summary>
	private bool HasExistingSaveTarget() =>
		!string.IsNullOrEmpty(_globalData?.SessionName)
		&& !string.IsNullOrEmpty(_globalData?.SessionPath);

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
	/// <param name="ignorePendingClose">
	/// True when Save &amp; close itself is writing — the unsaved dialog / pending New/Open/Quit
	/// must not block the File → Save path it just requested.
	/// </param>
	/// <returns>True if the caller should abort.</returns>
	private bool RejectIfSessionBusy(string actionLabel, bool ignorePendingClose = false)
	{
		if (_quitStarted)
		{
			_globalSignals?.EmitSignal(nameof(GlobalSignals.Log),
				"Cue2 is closing.", (int)LogType.Info);
			return true;
		}

		if (!ignorePendingClose && (IsUnsavedDialogOpen || _pendingClose != PendingCloseKind.None))
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
	/// Saves the current show to <paramref name="path"/> without a dialog, the same way Save As does
	/// after the dialog closes. Used by automation and the engine tests.
	/// </summary>
	/// <param name="path">Absolute .c2 path.</param>
	/// <param name="skipMediaBackup">When true, media is not copied into the show folder.</param>
	/// <returns><see langword="true"/> when the file was written.</returns>
	internal Task<bool> SaveToPathAsync(string path, bool skipMediaBackup = true) =>
		SaveSessionAsync(path, skipMediaBackup);

	/// <summary>
	/// Opens the showfile at <paramref name="path"/> without a dialog, the same way the Open dialog does.
	/// A file that needs version confirmation still shows the version dialog.
	/// </summary>
	/// <param name="path">Absolute .c2 path.</param>
	internal Task OpenPathAsync(string path) => OpenSelectedSessionAsync(path);

	/// <summary>Starts a new empty session without asking about unsaved changes.</summary>
	internal void NewSessionWithoutPrompt() => PerformNewSession();

	/// <summary>
	/// Waits until no showfile open or session apply is running.
	/// </summary>
	/// <param name="timeoutMs">Longest wait in milliseconds.</param>
	/// <returns><see langword="true"/> when idle before the timeout.</returns>
	internal async Task<bool> WaitForSessionIdleAsync(int timeoutMs)
	{
		var clock = System.Diagnostics.Stopwatch.StartNew();
		while (IsSessionLoading || _isOpenInProgress)
		{
			if (clock.ElapsedMilliseconds > timeoutMs)
				return false;
			await YieldProcessFrame();
		}
		return true;
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

	/// <summary>
	/// Install and Restart. Runs the same prompts as Quit (show still loading, running background
	/// task, unsaved changes). When quitting is certain, calls <paramref name="startUpdater"/>;
	/// Cue2 quits only when it returns true, so Cancel never leaves an update helper waiting.
	/// </summary>
	/// <param name="startUpdater">Starts the update helper; returns false when it could not start.</param>
	public void RequestQuitForUpdate(Func<bool> startUpdater)
	{
		if (startUpdater == null || _quitStarted)
			return;
		if (IsUnsavedDialogOpen || _pendingClose != PendingCloseKind.None)
			return;
		if (IsSessionLoading || _isOpenInProgress)
		{
			_globalSignals?.EmitSignal(nameof(GlobalSignals.Log),
				"A showfile is still loading. Install the update when it has finished.", (int)LogType.Info);
			return;
		}

		_updateRestartStarter = startUpdater;
		if (!ConfirmIfBackgroundTask(PendingCloseKind.UpdateRestart))
			return;
		if (!ConfirmIfUnsaved(PendingCloseKind.UpdateRestart))
			return;
		BeginUpdateRestart(_updateRestartStarter);
	}

	/// <summary>Starts the update helper, then quits. Stays open when the helper did not start.</summary>
	private void BeginUpdateRestart(Func<bool> startUpdater)
	{
		_updateRestartStarter = null;
		if (startUpdater == null || !startUpdater())
		{
			GD.Print("SaveManager:BeginUpdateRestart - Update helper did not start; Cue2 stays open.");
			return;
		}

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
	private bool ConfirmIfBackgroundTask(PendingCloseKind kind = PendingCloseKind.Quit)
	{
		if (!TryGetActiveBackgroundTask(out string taskName))
			return true;
		if (IsUnsavedDialogOpen || _pendingClose != PendingCloseKind.None)
			return false;

		_pendingClose = kind;
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

		// Defer so the overlay has left the tree before File → Save / Save As runs
		// (native FileDialog from inside the overlay button handler can crash on macOS).
		CallDeferred(nameof(BeginSaveForPendingClose));
	}

	/// <summary>
	/// Continues New / Open / Quit through the same File → Save path (in-place or Save As).
	/// </summary>
	private void BeginSaveForPendingClose()
	{
		BeginSave(forPendingClose: true);
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
		Func<bool> updateStarter = _updateRestartStarter;
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
			case PendingCloseKind.UpdateRestart:
				BeginUpdateRestart(updateStarter);
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
		_updateRestartStarter = null;
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
		_sessionGeneration++;
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
		_sessionGeneration++;
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

			var workspace = _globalData.CuelistWorkspace;
			if (saveData.ContainsKey("cuelists") && workspace != null)
			{
				GD.Print("SaveManager:LoadSessionFromData - Loading cuelists");
				EmitSessionLoadProgress(20f, "Loading cues…", string.Empty, 0, 0);
				var doc = new Dictionary();
				doc["Cuelists"] = saveData["cuelists"];
				workspace.ApplyDocumentData(doc);
				int cueCount = CueList.CueIndex?.Count ?? 0;
				OnCueLoadProgress(cueCount, cueCount);
			}
			else if (saveData.ContainsKey("cues") && _globalData.Cuelist != null)
			{
				GD.Print("SaveManager:LoadSessionFromData - Loading Cues");
				EmitSessionLoadProgress(20f, "Loading cues…", string.Empty, 0, 0);
				var cuesData = saveData["cues"].AsGodotDictionary();
				if (workspace != null)
				{
					workspace.ApplyDocumentData(cuesData);
				}
				else
				{
					_globalData.Cuelist.LoadCueModels(cuesData);
				}

				int cueCount = _globalData.Cuelist.TotalCueCount;
				OnCueLoadProgress(cueCount, cueCount);
			}

			// Models (or settings-only show) exist — GO may fire while the first bind finishes.
			MarkPlaybackReady();
			EmitSessionLoadProgress(100f, "Loading cues…", string.Empty, 1, 1);

			if (workspace == null && saveData.ContainsKey("cues") && _globalData.Cuelist != null)
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
	
}

