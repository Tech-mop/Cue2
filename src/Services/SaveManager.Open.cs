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
/// Partial: open dialog, showfile read, validation and open flow.
/// </summary>
public partial class SaveManager
{
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
				// Point at the previous version kept by the atomic save. Never restored automatically.
				if (File.Exists(selectedPath + ".bak"))
				{
					_globalSignals.EmitSignal(nameof(GlobalSignals.Log),
						$"A backup of the previous save exists: {selectedPath}.bak (copy it to a name ending in .c2 to open it).", 1);
				}
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
			CueList.TakeLoadRepairCount(); // drop counts from earlier loads or cuelist undo

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
			// A repaired showfile differs from disk: mark it unsaved so close prompts.
			if (CueList.TakeLoadRepairCount() > 0)
				_globalData.HistoryManager?.MarkUnsavedWithoutHistory();
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

		var workspace = _globalData.CuelistWorkspace;
		if (workspace != null)
		{
			var doc = workspace.GetDocumentData();
			if (doc.TryGetValue("Cuelists", out var listsVar))
				saveData.Add("cuelists", listsVar);
			var lists = workspace.Lists;
			if (lists.Count > 0)
				saveData.Add("cues", lists[0].GetData());
			else
				saveData.Add("cues", _globalData.Cuelist?.GetData() ?? new Dictionary());
		}
		else
		{
			var cueSaveData = _globalData.Cuelist.GetData();
			saveData.Add("cues", cueSaveData);
		}

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
}
