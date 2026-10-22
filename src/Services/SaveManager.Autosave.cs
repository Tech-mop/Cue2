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
/// Partial: autosave, autosave backups and pruning.
/// </summary>
public partial class SaveManager
{
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

			// Busy / clean gates: never race another write, never rewrite an unchanged show.
			if (_savesInFlight > 0)
			{
				GD.Print("SaveManager:PerformAutosave - Autosave skipped (save in progress).");
				return;
			}
			var history = _globalData.HistoryManager;
			if (history != null && !history.HasUnsavedDocumentChanges)
			{
				GD.Print("SaveManager:PerformAutosave - Autosave skipped (no unsaved changes).");
				_autosaveDeferStartTicks = 0;
				return;
			}

			// High-load gate: while a cue is starting, or within a short quiet window
			// after a GO, retry shortly instead of taking a snapshot on main. The save is never
			// dropped: after AutosaveMaxDeferSeconds of deferral it runs anyway.
			if (ShouldDeferAutosaveForPlayback())
			{
				ScheduleAutosaveRetry();
				return;
			}

			_globalSignals?.EmitSignal(nameof(GlobalSignals.Log), "Performing autosave...", 0);
			GD.Print("SaveManager:PerformAutosave - Autosave triggered.");
			int sessionGen = _sessionGeneration;

			// First, update the main file (off-main stringify/write)
			if (!await SaveSessionAsync(_globalData.SessionPath))
			{
				GD.Print("SaveManager:PerformAutosave - Main save did not complete; no backup this time.");
				return;
			}
			if (!GodotObject.IsInstanceValid(this))
				return;
			if (sessionGen != _sessionGeneration)
			{
				GD.Print("SaveManager:PerformAutosave - Show changed after the save; no backup this time.");
				return;
			}

			// Then copy the file just written into the session's Backups folder.
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
	/// True when autosave should wait because cues are starting (see <see cref="PlaybackActivity"/>),
	/// unless it has already waited <see cref="AutosaveMaxDeferSeconds"/>. Main thread only.
	/// </summary>
	private bool ShouldDeferAutosaveForPlayback()
	{
		if (!PlaybackActivity.IsBusy(AutosaveQuietSeconds))
		{
			_autosaveDeferStartTicks = 0;
			return false;
		}

		long now = System.Diagnostics.Stopwatch.GetTimestamp();
		if (_autosaveDeferStartTicks == 0)
		{
			_autosaveDeferStartTicks = now;
			GD.Print("SaveManager:PerformAutosave - Autosave deferred (cue starting); retrying shortly.");
			return true;
		}

		double waited = (now - _autosaveDeferStartTicks) / (double)System.Diagnostics.Stopwatch.Frequency;
		if (waited < AutosaveMaxDeferSeconds)
			return true;

		GD.Print($"SaveManager:PerformAutosave - Autosave deferred {waited:F0}s; saving now.");
		_autosaveDeferStartTicks = 0;
		return false;
	}

	/// <summary>
	/// Retries autosave after <see cref="AutosaveRetrySeconds"/>. At most one retry is pending;
	/// the regular interval timer keeps running alongside it.
	/// </summary>
	private void ScheduleAutosaveRetry()
	{
		if (_autosaveRetryPending || !IsInsideTree())
			return;
		_autosaveRetryPending = true;
		var retry = GetTree().CreateTimer(AutosaveRetrySeconds, processAlways: true, processInPhysics: false, ignoreTimeScale: true);
		retry.Timeout += () =>
		{
			_autosaveRetryPending = false;
			if (GodotObject.IsInstanceValid(this) && _autosaveTimer != null && !_autosaveTimer.IsStopped())
				PerformAutosave();
		};
	}

	/// <summary>
	/// Creates a timestamped backup of the current session in the session's Backups folder.
	/// Prunes old backups to respect the BackupDepth setting.
	/// </summary>
	/// <remarks>
	/// Copies the main showfile that the autosave just wrote (off main, behind the write gate)
	/// instead of building a second snapshot, so the backup always matches the main file. The copy
	/// goes to a temp name and is then renamed, and pruning runs only after it succeeded.
	/// </remarks>
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

			string mainPath = _globalData.SessionPath;
			(bool Ok, string Error) writeResult;
			_savesInFlight++;
			try
			{
				writeResult = await Task.Run(async () =>
				{
					await _writeGate.WaitAsync().ConfigureAwait(false);
					try
					{
						return CopyShowfileAtomically(mainPath, backupPath);
					}
					finally
					{
						_writeGate.Release();
					}
				}).ConfigureAwait(true);
			}
			finally
			{
				_savesInFlight--;
			}

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

			// Prune old backups (directory listing on main), only after a good backup.
			if (writeResult.Ok)
				PruneAutosaveBackups(backupDir, depth);
		}
		catch (Exception ex)
		{
			_globalSignals.EmitSignal(nameof(GlobalSignals.Log), $"Autosave backup failed: {ex.Message}", 2);
			GD.PrintErr($"SaveManager:CreateAutosaveBackup - Error: {ex.Message}");
		}
	}

	/// <summary>
	/// Copies <paramref name="sourcePath"/> to <paramref name="destPath"/> through a temp file and a
	/// rename, so a failed copy never leaves a truncated file under the final name. Worker thread;
	/// callers hold <see cref="_writeGate"/>.
	/// </summary>
	private static (bool Ok, string Error) CopyShowfileAtomically(string sourcePath, string destPath)
	{
		string tmpPath = destPath + ".tmp";
		try
		{
			File.Copy(sourcePath, tmpPath, overwrite: true);
			File.Move(tmpPath, destPath, overwrite: true);
			return (true, null);
		}
		catch (Exception ex)
		{
			try
			{
				if (File.Exists(tmpPath))
					File.Delete(tmpPath);
			}
			catch { /* best-effort */ }
			return (false, ex.Message);
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
