// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System;
using Godot;
using Godot.Collections;

namespace Cue2.Services;

/// <summary>
/// Constants and helpers for Cue2 showfile (.c2) format versioning.
/// </summary>
/// <remarks>
/// Root-level keys are written by <see cref="SaveManager"/> and read before session reset
/// so version mismatches can be confirmed without wiping the open show.
/// <para>
/// <see cref="CurrentFormatVersion"/> tracks schema changes that need migration.
/// Application marketing version (<see cref="Cue2.Version.SemanticVersionString"/>) is stored
/// separately for user-facing comparison.
/// </para>
/// </remarks>
public static class ShowfileFormat
{
	/// <summary>
	/// Current showfile schema version written by this build.
	/// Increment when the on-disk dictionary layout changes in a way that requires migration.
	/// </summary>
	public const int CurrentFormatVersion = 1;

	/// <summary>Root key for integer schema version.</summary>
	public const string FormatVersionKey = "formatVersion";

	/// <summary>Root key for semantic app version string (e.g. "0.1.0").</summary>
	public const string AppVersionKey = "appVersion";

	/// <summary>Root key for full app version string used in UI (status + code name).</summary>
	public const string AppVersionFullKey = "appVersionFull";

	/// <summary>
	/// Diagnostic key: which app build last opened a showfile without rewriting its format version
	/// (e.g. a newer schema opened on an older app with user confirmation).
	/// </summary>
	public const string OpenedByAppVersionKey = "openedByAppVersion";

	/// <summary>
	/// Writes current app + format version metadata onto a save dictionary.
	/// Use only when this build is the authoritative writer (save / successful migration).
	/// </summary>
	/// <param name="saveData">Root save dictionary (mutated in place).</param>
	public static void StampCurrentVersion(Dictionary saveData)
	{
		if (saveData == null)
			return;

		saveData[FormatVersionKey] = CurrentFormatVersion;
		saveData[AppVersionKey] = Cue2.Version.SemanticVersionString;
		saveData[AppVersionFullKey] = Cue2.Version.FullVersionString;
		// Clear forward-compat diagnostic once we fully own the schema.
		if (saveData.ContainsKey(OpenedByAppVersionKey))
			saveData.Remove(OpenedByAppVersionKey);
	}

	/// <summary>
	/// Records that this app opened the file without claiming schema ownership.
	/// Does <b>not</b> change <see cref="FormatVersionKey"/> (critical for newer-than-supported files).
	/// </summary>
	/// <param name="saveData">Root save dictionary (mutated in place).</param>
	public static void StampOpenedByThisApp(Dictionary saveData)
	{
		if (saveData == null)
			return;

		saveData[OpenedByAppVersionKey] = Cue2.Version.SemanticVersionString;
	}

	/// <summary>
	/// Reads version metadata from a parsed showfile root dictionary.
	/// </summary>
	/// <param name="saveData">Parsed root dictionary, or null.</param>
	/// <returns>Version info; missing keys yield unknown/legacy defaults.</returns>
	public static ShowfileVersionInfo ReadVersion(Dictionary saveData)
	{
		if (saveData == null)
			return ShowfileVersionInfo.Unknown;

		int formatVersion = -1;
		if (saveData.TryGetValue(FormatVersionKey, out var formatVariant))
		{
			try
			{
				formatVersion = formatVariant.AsInt32();
			}
			catch
			{
				formatVersion = -1;
			}
		}

		string appVersion = null;
		if (saveData.TryGetValue(AppVersionKey, out var appVariant))
		{
			try
			{
				var s = appVariant.AsString();
				if (!string.IsNullOrWhiteSpace(s))
					appVersion = s.Trim();
			}
			catch
			{
				appVersion = null;
			}
		}

		string appVersionFull = null;
		if (saveData.TryGetValue(AppVersionFullKey, out var fullVariant))
		{
			try
			{
				var s = fullVariant.AsString();
				if (!string.IsNullOrWhiteSpace(s))
					appVersionFull = s.Trim();
			}
			catch
			{
				appVersionFull = null;
			}
		}

		return new ShowfileVersionInfo(formatVersion, appVersion, appVersionFull);
	}
}

/// <summary>
/// Version metadata extracted from a showfile, plus comparison against the running app.
/// </summary>
public readonly struct ShowfileVersionInfo
{
	/// <summary>Sentinel for unreadable / missing save data.</summary>
	public static ShowfileVersionInfo Unknown => new(-1, null, null);

	/// <summary>
	/// Schema version from the file, or -1 when absent (legacy pre-versioned showfiles).
	/// </summary>
	public int FormatVersion { get; }

	/// <summary>Semantic app version that wrote the file, or null if unknown.</summary>
	public string AppVersion { get; }

	/// <summary>Full app version string that wrote the file, or null if unknown.</summary>
	public string AppVersionFull { get; }

	/// <summary>
	/// Creates version info from raw showfile fields.
	/// </summary>
	/// <param name="formatVersion">Schema version, or -1 if unknown.</param>
	/// <param name="appVersion">Semantic app version, or null.</param>
	/// <param name="appVersionFull">Full app version display string, or null.</param>
	public ShowfileVersionInfo(int formatVersion, string appVersion, string appVersionFull)
	{
		FormatVersion = formatVersion;
		AppVersion = appVersion;
		AppVersionFull = appVersionFull;
	}

	/// <summary>True when the file had no format or app version metadata.</summary>
	public bool IsLegacyOrUnknown => FormatVersion < 0 && string.IsNullOrEmpty(AppVersion);

	/// <summary>True when schema version matches this build.</summary>
	public bool MatchesCurrentFormat =>
		FormatVersion == ShowfileFormat.CurrentFormatVersion;

	/// <summary>True when semantic app version matches this build.</summary>
	public bool MatchesCurrentApp =>
		!string.IsNullOrEmpty(AppVersion) &&
		string.Equals(AppVersion, Cue2.Version.SemanticVersionString, StringComparison.Ordinal);

	/// <summary>
	/// True when the file is safe to open without a version prompt.
	/// Only the schema <see cref="FormatVersion"/> is required to match — app marketing
	/// versions (patch/minor bumps) may differ so opening a show after a Cue2 update does not nag.
	/// </summary>
	/// <remarks>
	/// Prefer this (or <see cref="RequiresVersionConfirmation"/>) over checking app version.
	/// </remarks>
	public bool MatchesCurrent => MatchesCurrentFormat;

	/// <summary>
	/// True when SaveManager should show <c>VersionMismatchDialog</c> before open.
	/// Format older/newer/legacy → confirm. Same format, different app version → no dialog.
	/// </summary>
	public bool RequiresVersionConfirmation => !MatchesCurrentFormat;

	/// <summary>True when format is older than this build (migration may apply).</summary>
	public bool IsOlderFormat =>
		FormatVersion >= 0 && FormatVersion < ShowfileFormat.CurrentFormatVersion;

	/// <summary>True when format is newer than this build understands.</summary>
	public bool IsNewerFormat =>
		FormatVersion > ShowfileFormat.CurrentFormatVersion;

	/// <summary>
	/// User-facing description of the showfile version for dialogs.
	/// </summary>
	/// <returns>Display string.</returns>
	public string ToDisplayString()
	{
		if (IsLegacyOrUnknown)
			return "unknown (saved before version tracking)";

		string appPart = !string.IsNullOrEmpty(AppVersionFull)
			? AppVersionFull
			: !string.IsNullOrEmpty(AppVersion)
				? $"v{AppVersion}"
				: "unknown app version";

		string formatPart = FormatVersion >= 0
			? $"format {FormatVersion}"
			: "format unknown";

		return $"{appPart} ({formatPart})";
	}
}
