// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System;
using System.Text;
using Godot;
using Godot.Collections;

namespace Cue2.Services;

/// <summary>
/// Migrates showfile dictionaries from older schema versions to <see cref="ShowfileFormat.CurrentFormatVersion"/>.
/// </summary>
/// <remarks>
/// Each step migrates from version N to N+1. Add a new private method and register it when
/// the on-disk layout changes. Steps must be idempotent where practical and must not require
/// live session state (they run before <see cref="SaveManager"/> resets the session).
/// </remarks>
public static class ShowfileMigrator
{
	/// <summary>
	/// Result of attempting to migrate a showfile dictionary.
	/// </summary>
	public sealed class MigrationResult
	{
		/// <summary>True when migration finished without fatal error (including no-op).</summary>
		public bool Success { get; init; }

		/// <summary>Format version after migration (or best-effort on failure).</summary>
		public int ResultFormatVersion { get; init; }

		/// <summary>Readable log of steps applied or skipped.</summary>
		public string Log { get; init; } = string.Empty;

		/// <summary>Error message when <see cref="Success"/> is false.</summary>
		public string Error { get; init; }
	}

	/// <summary>
	/// Whether the file's schema is older than this build and has upgrade steps.
	/// </summary>
	/// <param name="fileFormatVersion">Format version from the showfile (-1 = legacy).</param>
	/// <returns>True when migration should run before load.</returns>
	public static bool NeedsMigration(int fileFormatVersion)
	{
		int from = NormalizeFromVersion(fileFormatVersion);
		return from < ShowfileFormat.CurrentFormatVersion;
	}

	/// <summary>
	/// Whether the file claims a schema newer than this build supports.
	/// </summary>
	/// <param name="fileFormatVersion">Format version from the showfile.</param>
	/// <returns>True when opening may fail or drop data.</returns>
	public static bool IsNewerThanSupported(int fileFormatVersion)
	{
		return fileFormatVersion > ShowfileFormat.CurrentFormatVersion;
	}

	/// <summary>
	/// Migrates <paramref name="saveData"/> in place up to the current format version.
	/// </summary>
	/// <param name="saveData">Root showfile dictionary.</param>
	/// <param name="fileFormatVersion">Version reported by the file (-1 for legacy).</param>
	/// <returns>Outcome including log text for the event log.</returns>
	public static MigrationResult MigrateToCurrent(Dictionary saveData, int fileFormatVersion)
	{
		if (saveData == null)
		{
			return new MigrationResult
			{
				Success = false,
				ResultFormatVersion = fileFormatVersion,
				Error = "Save data is null."
			};
		}

		if (IsNewerThanSupported(fileFormatVersion))
		{
			var newerLog = new StringBuilder();
			newerLog.AppendLine(
				$"Showfile format {fileFormatVersion} is newer than supported format {ShowfileFormat.CurrentFormatVersion}; skipping migration.");
			// Do NOT stamp formatVersion to CurrentFormatVersion — that mislabels a newer schema
			// and would cause a re-save to claim this build authored an unsupported layout.
			// Keep the original formatVersion; record which older app opened the file.
			ShowfileFormat.StampOpenedByThisApp(saveData);
			newerLog.AppendLine(
				$"Preserved formatVersion={fileFormatVersion}; recorded openedByAppVersion={Cue2.Version.SemanticVersionString}.");
			return new MigrationResult
			{
				Success = true,
				ResultFormatVersion = fileFormatVersion,
				Log = newerLog.ToString().TrimEnd()
			};
		}

		int version = NormalizeFromVersion(fileFormatVersion);
		var log = new StringBuilder();

		if (version == ShowfileFormat.CurrentFormatVersion)
		{
			ShowfileFormat.StampCurrentVersion(saveData);
			log.AppendLine("Showfile already at current format; version metadata refreshed.");
			return new MigrationResult
			{
				Success = true,
				ResultFormatVersion = version,
				Log = log.ToString().TrimEnd()
			};
		}

		log.AppendLine($"Migrating showfile format {version} → {ShowfileFormat.CurrentFormatVersion}.");

		try
		{
			while (version < ShowfileFormat.CurrentFormatVersion)
			{
				switch (version)
				{
					case 0:
						MigrateV0ToV1(saveData, log);
						version = 1;
						break;

					// case 1:
					//     MigrateV1ToV2(saveData, log);
					//     version = 2;
					//     break;

					default:
						return new MigrationResult
						{
							Success = false,
							ResultFormatVersion = version,
							Log = log.ToString().TrimEnd(),
							Error = $"No migration path from format version {version}."
						};
				}
			}

			ShowfileFormat.StampCurrentVersion(saveData);
			log.AppendLine($"Migration complete. Format version is now {ShowfileFormat.CurrentFormatVersion}.");

			return new MigrationResult
			{
				Success = true,
				ResultFormatVersion = ShowfileFormat.CurrentFormatVersion,
				Log = log.ToString().TrimEnd()
			};
		}
		catch (Exception ex)
		{
			return new MigrationResult
			{
				Success = false,
				ResultFormatVersion = version,
				Log = log.ToString().TrimEnd(),
				Error = ex.Message
			};
		}
	}

	/// <summary>
	/// Legacy files (no formatVersion) are treated as format 0.
	/// </summary>
	private static int NormalizeFromVersion(int fileFormatVersion)
	{
		return fileFormatVersion < 0 ? 0 : fileFormatVersion;
	}

	/// <summary>
	/// First versioned format: ensure root shape and stamp format 1.
	/// </summary>
	/// <remarks>
	/// Pre-versioned showfiles already used <c>cues</c> / <c>settings</c> keys.
	/// This step is intentionally a no-op on data shape so existing files load cleanly.
	/// </remarks>
	private static void MigrateV0ToV1(Dictionary saveData, StringBuilder log)
	{
		// Ensure expected top-level containers exist so later loaders do not NRE.
		if (!saveData.ContainsKey("cues"))
		{
			saveData["cues"] = new Dictionary();
			log.AppendLine("v0→v1: added empty 'cues' dictionary.");
		}

		if (!saveData.ContainsKey("settings"))
		{
			saveData["settings"] = new Dictionary();
			log.AppendLine("v0→v1: added empty 'settings' dictionary.");
		}

		saveData[ShowfileFormat.FormatVersionKey] = 1;
		log.AppendLine("v0→v1: stamped formatVersion = 1.");
	}
}
