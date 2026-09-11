// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Godot;

namespace Cue2.Services;

/// <summary>
/// Severity levels for <see cref="GlobalSignals.Log"/> and <see cref="EventLogger"/>.
/// Values are cast to <see cref="int"/> when emitted over Godot signals.
/// </summary>
/// <remarks>
/// 0 = Information (white text, default),
/// 1 = Warning (yellow text),
/// 2 = System error (red text),
/// 3 = Alert (red text; may flash window border) — for issues that may affect playback.
/// </remarks>
public enum LogType
{
	Info = 0,
	Warning = 1,
	Error = 2,
	Alert = 3
}

/// <summary>
/// Receives log signals, keeps an in-memory history for the current session only,
/// and appends to a session-rotated log file on disk.
/// </summary>
/// <remarks>
/// On startup the previous <c>cue2.log</c> (if any) is renamed with a timestamp
/// (e.g. <c>cue22026-07-29T21.28.57.log</c>) and a fresh file is opened.
/// Older session files are pruned to <see cref="UserDataManager.LogSessionDepth"/>
/// total files (including the current session). The log window only shows the
/// current session; historical files remain on disk for support until pruned.
/// <para/>
/// See <see cref="LogType"/> for severity meanings used by the log UI and alert path.
/// </remarks>
public partial class EventLogger : Node
{
	private GlobalSignals _globalSignals;
	private GlobalData _globalData;

	private static List<string> _logList = new List<string>();
	private static int _logCount;

	private const string LogDirPath = "user://logs";
	private const string LogFileName = "cue2.log";
	private const string LogFilePath = LogDirPath + "/" + LogFileName;
	private const string LogBaseName = "cue2";

	/// <summary>
	/// Matches rotated session files: cue2YYYY-MM-DDTHH.MM.SS.log
	/// </summary>
	private static readonly Regex RotatedSessionFileRegex = new Regex(
		@"^cue2\d{4}-\d{2}-\d{2}T\d{2}\.\d{2}\.\d{2}\.log$",
		RegexOptions.Compiled | RegexOptions.CultureInvariant);

	/// <summary>Absolute path of the active session log file.</summary>
	private string _currentLogFullPath = string.Empty;

	public override void _Ready()
	{
		_globalSignals = GetNode<GlobalSignals>("/root/GlobalSignals");
		_globalData = GetNodeOrNull<GlobalData>("/root/GlobalData");
		_globalSignals.Log += LogEvent;

		_logList.Clear();
		_logCount = 0;

		SetupLogFile();

		// Re-prune after the full autoload chain so UserDataManager has loaded preferences.
		CallDeferred(nameof(DeferredApplySessionDepthFromPrefs));
	}

	/// <summary>
	/// Applies the loaded <see cref="UserDataManager.LogSessionDepth"/> once user data is available.
	/// </summary>
	private void DeferredApplySessionDepthFromPrefs()
	{
		if (_globalData == null)
			_globalData = GetNodeOrNull<GlobalData>("/root/GlobalData");

		PruneSessionLogs(GetConfiguredSessionDepth());
	}

	/// <summary>
	/// Ensures the log directory exists, rotates the previous session file if present,
	/// prunes old sessions to the configured depth, and opens a fresh current log.
	/// </summary>
	private void SetupLogFile()
	{
		try
		{
			string fullPath = ProjectSettings.GlobalizePath(LogFilePath);
			_currentLogFullPath = fullPath;
			string logDir = Path.GetDirectoryName(fullPath);
			if (string.IsNullOrEmpty(logDir))
			{
				GD.PrintErr("EventLogger:SetupLogFile - Could not resolve log directory.");
				return;
			}

			if (!Directory.Exists(logDir))
			{
				Directory.CreateDirectory(logDir);
			}

			// Migrate away from the old size-based .old rotation.
			string legacyOldPath = fullPath + ".old";
			if (File.Exists(legacyOldPath))
			{
				TryRotateOrDeleteLegacyFile(legacyOldPath, logDir);
			}

			if (File.Exists(fullPath))
			{
				RotateCurrentSessionFile(fullPath, logDir);
			}

			PruneSessionLogs(GetConfiguredSessionDepth());

			// Touch an empty current session file so the path always exists after start.
			if (!File.Exists(fullPath))
			{
				File.WriteAllText(fullPath, string.Empty);
			}
		}
		catch (Exception ex)
		{
			GD.PrintErr("EventLogger:SetupLogFile - Failed to setup log file: " + ex.Message);
		}
	}

	/// <summary>
	/// Renames an existing current log to a timestamped session file.
	/// </summary>
	/// <param name="fullPath">Absolute path of <c>cue2.log</c>.</param>
	/// <param name="logDir">Absolute log directory.</param>
	private static void RotateCurrentSessionFile(string fullPath, string logDir)
	{
		DateTime stampSource;
		try
		{
			stampSource = File.GetLastWriteTime(fullPath);
		}
		catch
		{
			stampSource = DateTime.Now;
		}

		string rotatedName = BuildRotatedFileName(stampSource);
		string rotatedPath = Path.Combine(logDir, rotatedName);

		// Avoid rare collisions if the same second is reused.
		if (File.Exists(rotatedPath))
		{
			rotatedName = BuildRotatedFileName(DateTime.Now);
			rotatedPath = Path.Combine(logDir, rotatedName);
			if (File.Exists(rotatedPath))
			{
				rotatedPath = Path.Combine(logDir,
					$"{LogBaseName}{DateTime.Now:yyyy-MM-ddTHH.mm.ss.fff}.log");
			}
		}

		File.Move(fullPath, rotatedPath);
	}

	/// <summary>
	/// Converts a legacy <c>cue2.log.old</c> into a timestamped session file when possible.
	/// </summary>
	private static void TryRotateOrDeleteLegacyFile(string legacyOldPath, string logDir)
	{
		try
		{
			DateTime stampSource;
			try
			{
				stampSource = File.GetLastWriteTime(legacyOldPath);
			}
			catch
			{
				stampSource = DateTime.Now;
			}

			string rotatedPath = Path.Combine(logDir, BuildRotatedFileName(stampSource));
			if (File.Exists(rotatedPath))
			{
				File.Delete(legacyOldPath);
				return;
			}

			File.Move(legacyOldPath, rotatedPath);
		}
		catch (Exception ex)
		{
			GD.PrintErr("EventLogger:TryRotateOrDeleteLegacyFile - " + ex.Message);
			try
			{
				if (File.Exists(legacyOldPath))
					File.Delete(legacyOldPath);
			}
			catch
			{
				// Best-effort cleanup only.
			}
		}
	}

	/// <summary>
	/// Builds a rotated log file name: cue2YYYY-MM-DDTHH.MM.SS.log
	/// </summary>
	/// <param name="stamp">Timestamp used in the file name.</param>
	/// <returns>File name only (no directory).</returns>
	private static string BuildRotatedFileName(DateTime stamp)
	{
		return $"{LogBaseName}{stamp:yyyy-MM-ddTHH.mm.ss}.log";
	}

	/// <summary>
	/// Returns the configured number of session log files to retain (including current).
	/// Falls back to <see cref="UserDataManager.DefaultLogSessionDepth"/> when unavailable.
	/// </summary>
	private int GetConfiguredSessionDepth()
	{
		var udm = _globalData?.UserDataManager;
		if (udm != null)
			return udm.LogSessionDepth;
		return UserDataManager.DefaultLogSessionDepth;
	}

	/// <summary>
	/// Deletes the oldest rotated session log files so that total session files
	/// (current <c>cue2.log</c> + rotated) do not exceed <paramref name="maxFiles"/>.
	/// </summary>
	/// <param name="maxFiles">
	/// Maximum session files to keep, including the current session.
	/// Values &lt;= 1 keep only the current <c>cue2.log</c> (no history).
	/// </param>
	/// <remarks>
	/// Safe to call when the depth preference changes mid-session.
	/// </remarks>
	public void PruneSessionLogs(int maxFiles)
	{
		try
		{
			maxFiles = Math.Clamp(maxFiles, UserDataManager.MinLogSessionDepth, UserDataManager.MaxLogSessionDepth);

			string logDir = Path.GetDirectoryName(
				string.IsNullOrEmpty(_currentLogFullPath)
					? ProjectSettings.GlobalizePath(LogFilePath)
					: _currentLogFullPath);

			if (string.IsNullOrEmpty(logDir) || !Directory.Exists(logDir))
				return;

			// maxFiles includes the current session file.
			int maxRotated = Math.Max(0, maxFiles - 1);
			var rotated = GetRotatedSessionFiles(logDir);

			// Oldest first (by embedded timestamp, then LastWriteTime).
			while (rotated.Count > maxRotated)
			{
				var oldest = rotated[0];
				rotated.RemoveAt(0);
				try
				{
					File.Delete(oldest.FullPath);
				}
				catch (Exception ex)
				{
					GD.PrintErr($"EventLogger:PruneSessionLogs - Failed to delete {oldest.FullPath}: {ex.Message}");
				}
			}
		}
		catch (Exception ex)
		{
			GD.PrintErr("EventLogger:PruneSessionLogs - " + ex.Message);
		}
	}

	/// <summary>
	/// Applies a new session depth from preferences and prunes disk history immediately.
	/// </summary>
	/// <param name="maxFiles">New total session file limit (including current).</param>
	public void ApplyLogSessionDepth(int maxFiles)
	{
		PruneSessionLogs(maxFiles);
	}

	private readonly struct RotatedSessionFile
	{
		public string FullPath { get; init; }
		public DateTime SortTime { get; init; }
	}

	/// <summary>
	/// Lists rotated session files oldest-first.
	/// </summary>
	private static List<RotatedSessionFile> GetRotatedSessionFiles(string logDir)
	{
		var results = new List<RotatedSessionFile>();
		foreach (string path in Directory.EnumerateFiles(logDir, LogBaseName + "*.log"))
		{
			string name = Path.GetFileName(path);
			if (string.Equals(name, LogFileName, StringComparison.OrdinalIgnoreCase))
				continue;
			if (!RotatedSessionFileRegex.IsMatch(name))
				continue;

			DateTime sortTime = TryParseRotatedTimestamp(name)
				?? File.GetLastWriteTime(path);

			results.Add(new RotatedSessionFile
			{
				FullPath = path,
				SortTime = sortTime
			});
		}

		return results
			.OrderBy(f => f.SortTime)
			.ThenBy(f => f.FullPath, StringComparer.OrdinalIgnoreCase)
			.ToList();
	}

	/// <summary>
	/// Parses the timestamp embedded in a rotated file name.
	/// </summary>
	private static DateTime? TryParseRotatedTimestamp(string fileName)
	{
		// cue2YYYY-MM-DDTHH.MM.SS.log
		if (fileName.Length < LogBaseName.Length + "yyyy-MM-ddTHH.mm.ss".Length + 4)
			return null;

		string stamp = fileName.Substring(LogBaseName.Length,
			fileName.Length - LogBaseName.Length - 4); // strip .log
		if (DateTime.TryParseExact(stamp, "yyyy-MM-ddTHH.mm.ss",
			    CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
		{
			return dt;
		}

		return null;
	}

	private void LogEvent(string @logString, int @type)
	{
		var typeString = GetLogTypeName(@type);
		var printout = typeString + "  :  " + DateTime.Now.ToString("yyyy/MM/dd HH:mm:ss tt") + "  :  " + @logString;
		_logList.Add(printout);
		_logCount++;
		_globalSignals.EmitSignal(nameof(GlobalSignals.LogUpdated), printout, @type);
		if (@type == 3) _globalSignals.EmitSignal(nameof(GlobalSignals.LogAlert));
		GD.Print(printout);
		WriteToLogFile(printout);
	}

	private void WriteToLogFile(string line)
	{
		try
		{
			string fullPath = string.IsNullOrEmpty(_currentLogFullPath)
				? ProjectSettings.GlobalizePath(LogFilePath)
				: _currentLogFullPath;

			string logDir = Path.GetDirectoryName(fullPath);
			if (!string.IsNullOrEmpty(logDir) && !Directory.Exists(logDir))
			{
				Directory.CreateDirectory(logDir);
			}

			File.AppendAllText(fullPath, line + System.Environment.NewLine);
		}
		catch (Exception ex)
		{
			GD.PrintErr("EventLogger:WriteToLogFile - Failed to write log: " + ex.Message);
		}
	}

	private string GetLogTypeName(int type)
	{
		if (Enum.IsDefined(typeof(LogType), type))
		{
			return ((LogType)type).ToString();
		}
		return "Unknown";
	}

	/// <summary>
	/// Returns the number of log entries recorded during the current session.
	/// </summary>
	/// <returns>Session log count.</returns>
	public static int GetLogCount()
	{
		return _logCount;
	}

	/// <summary>
	/// Returns the in-memory log list for the current session only (oldest first).
	/// Callers must not mutate the list.
	/// </summary>
	/// <returns>Current-session log lines, oldest at index 0.</returns>
	public List<string> GetLogList()
	{
		return _logList;
	}

	/// <summary>
	/// Returns the number of log lines held in memory for the current session.
	/// </summary>
	/// <returns>Current-session in-memory log count.</returns>
	public int GetTotalLogCount()
	{
		return _logList.Count;
	}

	/// <summary>
	/// Clears the current session logs from memory and truncates the current session file.
	/// Historical rotated session files on disk are left intact (until pruned by depth).
	/// </summary>
	/// <remarks>
	/// Does not emit log signals; callers that need UI feedback should log after clearing.
	/// </remarks>
	public void ClearLogs()
	{
		_logList.Clear();
		_logCount = 0;

		try
		{
			string fullPath = string.IsNullOrEmpty(_currentLogFullPath)
				? ProjectSettings.GlobalizePath(LogFilePath)
				: _currentLogFullPath;

			// Truncate current session only — do not wipe historical session files.
			File.WriteAllText(fullPath, string.Empty);
		}
		catch (Exception ex)
		{
			GD.PrintErr("EventLogger:ClearLogs - Failed to clear current log file: " + ex.Message);
		}
	}
}
