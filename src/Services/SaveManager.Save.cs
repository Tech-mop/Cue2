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
/// Partial: Save / Save As, save dialogs, showfile write and session identity.
/// </summary>
public partial class SaveManager
{
	/// <summary>
	/// File → Save. If the session is unnamed, has no path, or was opened from a newer format,
	/// falls through to Save As.
	/// </summary>
	private void Save()
	{
		BeginSave(forPendingClose: false);
	}

	/// <summary>
	/// File → Save As. Opens the save file dialog.
	/// </summary>
	/// <remarks>
	/// Start folder: current show directory when the session is already on disk; otherwise the
	/// last Save As location; otherwise a writable Documents/Cue2 (with Desktop / user:// failover).
	/// </remarks>
	private void SaveAs()
	{
		BeginSaveAs(forPendingClose: false);
	}

	/// <summary>
	/// Shared File → Save / unsaved-dialog Save &amp; close path.
	/// Never-saved and newer-format shows open Save As; otherwise writes in place.
	/// </summary>
	/// <param name="forPendingClose">
	/// True when New / Open / Quit is waiting on this save. Skips the unsaved-dialog busy
	/// gate and continues that action after a successful write.
	/// </param>
	private void BeginSave(bool forPendingClose)
	{
		if (RejectIfSessionBusy("save", ignorePendingClose: forPendingClose))
		{
			if (forPendingClose)
				ClearPendingClose();
			return;
		}

		if (!HasExistingSaveTarget() || _openedFromNewerFormat)
		{
			if (_openedFromNewerFormat && HasExistingSaveTarget())
			{
				_globalSignals.EmitSignal(nameof(GlobalSignals.Log),
					"This show was opened from a newer Cue2 format. Use Save As to write a copy " +
					"this version can own — overwriting the original is blocked.",
					(int)LogType.Warning);
			}

			BeginSaveAs(forPendingClose);
			return;
		}

		_globalSignals.EmitSignal(nameof(GlobalSignals.Log),
			$"Saving session to: {_globalData.SessionPath} with name: {_globalData.SessionName}:", 0);

		if (!forPendingClose)
		{
			SaveSession(_globalData.SessionPath);
			return;
		}

		string path = _globalData.SessionPath;
		TaskUtil.Run(async () =>
		{
			bool ok = await SaveSessionAsync(path);
			if (ok)
				ExecutePendingClose();
			else
				ClearPendingClose();
		}, "SaveManager.SaveThenClose");
	}

	/// <summary>
	/// Pops the Save As file dialog. Used by File → Save As and by <see cref="BeginSave"/>
	/// when there is no existing show path.
	/// </summary>
	/// <param name="forPendingClose">
	/// When true, a successful file choice continues the pending New / Open / Quit.
	/// </param>
	private void BeginSaveAs(bool forPendingClose)
	{
		if (RejectIfSessionBusy("save as", ignorePendingClose: forPendingClose))
		{
			if (forPendingClose)
				ClearPendingClose();
			return;
		}

		if (_saveDialog == null || !IsInstanceValid(_saveDialog))
		{
			_globalSignals.EmitSignal(nameof(GlobalSignals.Log),
				"SaveManager:SaveAs - Save dialog is not available.", (int)LogType.Error);
			if (forPendingClose)
				ClearPendingClose();
			return;
		}

		if (forPendingClose)
			_proceedAfterSaveAs = true;

		ApplySaveDialogStartLocation();
		_saveDialog.PopupCentered();
		_globalSignals.EmitSignal(nameof(GlobalSignals.Log),
			"SaveManager:SaveAs - Waiting on save directory and show name to continue save", 0);
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
	/// <returns>True when a re-save was started; false when it was skipped.</returns>
	public bool ResaveSessionAfterMediaPathUpdate()
	{
		if (string.IsNullOrEmpty(_globalData.SessionPath))
		{
			GD.Print("SaveManager:ResaveSessionAfterMediaPathUpdate - No SessionPath; skip.");
			return false;
		}

		// Never write a show that is still being loaded; the next save stores the paths.
		if (IsSessionLoading)
		{
			GD.Print("SaveManager:ResaveSessionAfterMediaPathUpdate - Show is loading; skip.");
			return false;
		}

		// The re-save writes the whole live show. With edits the operator has not saved, it would
		// persist them and mark the show clean, so leave both for the operator's next save.
		if (HasUnsavedChanges)
		{
			GD.Print("SaveManager:ResaveSessionAfterMediaPathUpdate - Unsaved edits; skip.");
			_globalSignals?.EmitSignal(nameof(GlobalSignals.Log),
				"Media paths updated to show-relative. The show has unsaved changes, so it was not re-saved " +
				"automatically; save to keep the new paths.",
				(int)LogType.Info);
			return false;
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
			return false;
		}

		SaveSession(_globalData.SessionPath, skipMediaBackup: true);
		return true;
	}

	/// <summary>
	/// Saves the current session data to the specified path and name.
	/// Builds the save dictionary on the main thread, then JSON-stringifies
	/// and encrypt-writes on a worker so large shows do not freeze the UI.
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

		// The show this save belongs to. If it is cleared before the write finishes, the
		// completion steps below must not run against the newly loaded show.
		int sessionGen = _sessionGeneration;

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

		// Session paths must be known before serializing so relative media URLs resolve correctly.
		// Keep the current identity so a failed Save As can put it back.
		var previousIdentity = CaptureSessionIdentity();
		ApplySessionPaths(sessionPath, folderPaths);

		// Snapshot on main thread (touches Cuelist / Settings / Godot nodes).
		// Remember which document state the snapshot holds: edits made while the write runs
		// must still read as unsaved afterwards.
		long stateAtSnapshot = _globalData.HistoryManager?.CurrentStateId ?? 0;
		Dictionary saveData;
		try
		{
			saveData = BuildSaveDataDictionary();
		}
		catch (Exception ex)
		{
			_globalSignals.EmitSignal(nameof(GlobalSignals.Log), $"Failed to build save data: {ex.Message}", 2);
			GD.PrintErr($"SaveManager:SaveSession - BuildSaveDataDictionary: {ex.Message}");
			RestoreSessionIdentityAfterFailedSave(previousIdentity);
			return false;
		}

		int gen = Interlocked.Increment(ref _saveGeneration);
		string pathForWrite = sessionPath;

		_globalSignals.EmitSignal(nameof(GlobalSignals.Log), "Saving session…", 0);

		(bool Ok, string Error) writeResult;
		_savesInFlight++;
		try
		{
			// Heavy work: stringify large JSON + atomic disk write (off main thread), one at a time.
			string gateKey = WriteGateKey(pathForWrite);
			_latestGenByPath.AddOrUpdate(gateKey, gen, (_, prev) => Math.Max(prev, gen));
			writeResult = await Task.Run(async () =>
			{
				await _writeGate.WaitAsync().ConfigureAwait(false);
				try
				{
					// A newer save for this path is queued behind us; let it write instead.
					if (_latestGenByPath.TryGetValue(gateKey, out int latest) && gen < latest)
						return (false, "superseded by a newer save");
					return WritePlainShowfile(pathForWrite, saveData);
				}
				finally
				{
					_writeGate.Release();
				}
			}).ConfigureAwait(true);
		}
		catch (Exception ex)
		{
			writeResult = (false, ex.Message);
		}
		finally
		{
			_savesInFlight--;
		}

		// The show was closed or replaced (open, New Session) while this write ran. The file
		// written belongs to the old show; skip every follow-up step (identity restore, recents,
		// media backup, autosave, saved state), because they would act on the show loaded now.
		if (sessionGen != _sessionGeneration)
		{
			GD.Print($"SaveManager:SaveSession - Show changed while saving {pathForWrite} " +
			         $"(write ok={writeResult.Ok}); follow-up steps skipped.");
			_globalSignals.EmitSignal(nameof(GlobalSignals.Log),
				writeResult.Ok
					? $"Saved {pathForWrite} (it finished after that show was closed)."
					: $"Failed to write showfile: {selectedPath} ({writeResult.Error})",
				writeResult.Ok ? 0 : 2);
			return false;
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
			// Newest save failed: do not leave Ctrl+S / autosave / title pointing at the failed target.
			RestoreSessionIdentityAfterFailedSave(previousIdentity);
			return false;
		}

		// Successful Save As of a forward-compat open: this file is now owned by current format.
		if (clearNewerFormatGuard)
			_openedFromNewerFormat = false;

		// Mark the written state saved before the media steps below, so a path re-save they start
		// sees whether edits were made after this snapshot.
		_globalData.HistoryManager?.MarkSaved(stateAtSnapshot);

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

		// Update title (in case signal timing)
		GetNodeOrNull<MainTitleBarUI>("/root/Cue2Base/MainWindowHandles/MainTitleBar")?.CallDeferred("UpdateTitle");
		return true;
	}

	/// <summary>
	/// JSON-stringify + plain UTF-8 write. Intended for a worker thread (no Godot node access).
	/// Showfiles are not encrypted — open, editable JSON for an open-source tool.
	/// </summary>
	/// <remarks>
	/// Atomic: writes <c>{path}.tmp</c> next to the target, flushes it to disk, then swaps it in
	/// with <see cref="File.Replace(string, string, string, bool)"/> (previous version kept as
	/// <c>{path}.bak</c>). The live file is never truncated, so a crash or power loss mid-save
	/// leaves either the old or the new show. Callers must hold <see cref="_writeGate"/>.
	/// </remarks>
	/// <param name="sessionPath">Absolute .c2 path.</param>
	/// <param name="saveData">Snapshot dictionary built on the main thread.</param>
	/// <returns>Success flag and error text.</returns>
	internal static (bool Ok, string Error) WritePlainShowfile(string sessionPath, Dictionary saveData)
	{
		string tmpPath = sessionPath + ".tmp";
		try
		{
			string jsonString = Json.Stringify(saveData);
			if (string.IsNullOrEmpty(jsonString))
				return (false, "JSON stringify produced empty output.");

			// A stale .tmp is left only by a write that died mid-way; it is never the live show.
			if (File.Exists(tmpPath))
				File.Delete(tmpPath);

			// Prefer System.IO for plain UTF-8 so worker-thread writes are reliable.
			using (var fs = new FileStream(tmpPath, FileMode.Create, System.IO.FileAccess.Write, FileShare.None))
			using (var sw = new StreamWriter(fs, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
			{
				sw.Write(jsonString);
				sw.Flush();
				fs.Flush(flushToDisk: true);
			}

			if (File.Exists(sessionPath))
				ReplaceShowfile(tmpPath, sessionPath);
			else
				File.Move(tmpPath, sessionPath);
			return (true, null);
		}
		catch (Exception ex)
		{
			try
			{
				if (File.Exists(tmpPath))
					File.Delete(tmpPath);
			}
			catch
			{
				// Best effort; the next write deletes it.
			}
			return (false, ex.Message);
		}
	}

	/// <summary>
	/// Swaps a fully written temp file over the live showfile, keeping the old one as <c>.bak</c>.
	/// Falls back to copy-to-.bak plus overwrite-move where the volume does not support
	/// <see cref="File.Replace(string, string, string, bool)"/>.
	/// </summary>
	private static void ReplaceShowfile(string tmpPath, string sessionPath)
	{
		string bakPath = sessionPath + ".bak";
		try
		{
			File.Replace(tmpPath, sessionPath, bakPath, ignoreMetadataErrors: true);
		}
		catch (Exception ex) when (ex is IOException || ex is PlatformNotSupportedException || ex is UnauthorizedAccessException)
		{
			if (!File.Exists(tmpPath))
				throw;
			GD.Print($"SaveManager:ReplaceShowfile - File.Replace failed ({ex.Message}); using copy + move.");
			File.Copy(sessionPath, bakPath, overwrite: true);
			File.Move(tmpPath, sessionPath, overwrite: true);
		}
	}

	/// <summary>Normalized key for per-path write supersede (case-insensitive, like <see cref="PathsEqual"/>).</summary>
	private static string WriteGateKey(string path)
	{
		try
		{
			return System.IO.Path.GetFullPath(path.Replace('\\', '/')).ToUpperInvariant();
		}
		catch
		{
			return path.ToUpperInvariant();
		}
	}

	/// <summary>
	/// Applies session file path, name, and type-based media folder paths to <see cref="GlobalData"/>.
	/// Ensures the <c>Waveforms/</c> peak-cache folder exists so disk cache hits work after open.
	/// </summary>
	/// <param name="sessionFilePath">Absolute path to the .c2 file.</param>
	/// <param name="folderPaths">Optional precomputed folder layout; derived from the session path when null.</param>
	/// <summary>Session file and folder paths as one value, for undoing a failed Save As.</summary>
	private readonly record struct SessionIdentity(
		string Path,
		string Name,
		string Dir,
		string AudioPath,
		string VideoPath,
		string ImagesPath,
		string WaveformsPath);

	private SessionIdentity CaptureSessionIdentity() => new(
		_globalData.SessionPath,
		_globalData.SessionName,
		_globalData.SessionDir,
		_globalData.SessionAudioPath,
		_globalData.SessionVideoPath,
		_globalData.SessionImagesPath,
		_globalData.SessionWaveformsPath);

	/// <summary>
	/// Puts the session identity back after a failed save. A plain Save to the same
	/// path is a no-op; after a failed Save As, title, Ctrl+S and autosave target the old file again.
	/// </summary>
	private void RestoreSessionIdentityAfterFailedSave(SessionIdentity previous)
	{
		if (previous == CaptureSessionIdentity())
			return;

		_globalData.SessionPath = previous.Path;
		_globalData.SessionName = previous.Name;
		_globalData.SessionDir = previous.Dir;
		_globalData.SessionAudioPath = previous.AudioPath;
		_globalData.SessionVideoPath = previous.VideoPath;
		_globalData.SessionImagesPath = previous.ImagesPath;
		_globalData.SessionWaveformsPath = previous.WaveformsPath;

		if (!string.IsNullOrEmpty(previous.Path))
		{
			_globalSignals.EmitSignal(nameof(GlobalSignals.Log),
				$"Save As failed. The show is still saved at {previous.Path}", (int)LogType.Warning);
		}
		GD.Print($"SaveManager:RestoreSessionIdentityAfterFailedSave - Restored SessionPath={previous.Path}");
		GetNodeOrNull<MainTitleBarUI>("/root/Cue2Base/MainWindowHandles/MainTitleBar")?.CallDeferred("UpdateTitle");
	}

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
}
