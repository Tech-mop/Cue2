// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.IO;
using Godot;

namespace Cue2.Services;

/// <summary>
/// Parses OS / Godot command-line arguments for a <c>.c2</c> showfile to open at launch.
/// </summary>
public static class ShowfileLaunchArgs
{
	/// <summary>Showfile extension including the dot.</summary>
	public const string Extension = ".c2";

	private static readonly HashSet<string> FlagsWithValue = new(StringComparer.OrdinalIgnoreCase)
	{
		"--path", "--main-pack", "--display-driver", "--audio-driver", "--rendering-method",
		"--write-movie", "--resolution", "--position", "--wid", "--debug-server",
		"--remote-debug", "--language", "--log-file", "--export-release", "--export-debug",
		"--export-pack", "--script", "--scene"
	};

	/// <summary>
	/// True when <paramref name="path"/> looks like a Cue2 showfile (extension only).
	/// </summary>
	public static bool HasShowfileExtension(string path)
	{
		if (string.IsNullOrWhiteSpace(path))
			return false;
		return path.Trim().Trim('"').EndsWith(Extension, StringComparison.OrdinalIgnoreCase);
	}

	/// <summary>
	/// Resolves <paramref name="raw"/> to an existing <c>.c2</c> file, or null.
	/// </summary>
	public static string TryResolveShowfile(string raw)
	{
		if (string.IsNullOrWhiteSpace(raw))
			return null;

		string trimmed = raw.Trim().Trim('"');
		if (!HasShowfileExtension(trimmed))
			return null;

		try
		{
			if (Path.IsPathRooted(trimmed) && File.Exists(trimmed))
				return Path.GetFullPath(trimmed);

			string fromCwd = Path.GetFullPath(trimmed);
			if (File.Exists(fromCwd))
				return fromCwd;

			string exeDir = Path.GetDirectoryName(OS.GetExecutablePath());
			if (!string.IsNullOrEmpty(exeDir))
			{
				string nextToExe = Path.GetFullPath(Path.Combine(exeDir, trimmed));
				if (File.Exists(nextToExe))
					return nextToExe;
			}
		}
		catch (Exception)
		{
			return null;
		}

		return null;
	}

	/// <summary>
	/// First existing <c>.c2</c> path from the process command line, or null.
	/// User args (after <c>--</c>) are preferred over engine args.
	/// </summary>
	public static string GetShowfileFromCommandLine()
	{
		foreach (string arg in OS.GetCmdlineUserArgs() ?? Array.Empty<string>())
		{
			string resolved = TryResolveShowfile(arg);
			if (resolved != null)
				return resolved;
		}

		string[] all = OS.GetCmdlineArgs() ?? Array.Empty<string>();
		for (int i = 0; i < all.Length; i++)
		{
			string arg = all[i];
			if (i == 0 && LooksLikeExecutable(arg))
				continue;
			if (!string.IsNullOrEmpty(arg) && arg[0] == '-')
			{
				if (FlagsWithValue.Contains(arg) && i + 1 < all.Length)
					i++;
				continue;
			}

			string resolved = TryResolveShowfile(arg);
			if (resolved != null)
				return resolved;
		}

		return null;
	}

	private static bool LooksLikeExecutable(string arg)
	{
		if (string.IsNullOrEmpty(arg))
			return false;
		string name = Path.GetFileName(arg);
		return name.StartsWith("Cue2", StringComparison.OrdinalIgnoreCase)
		       || name.Contains("godot", StringComparison.OrdinalIgnoreCase)
		       || name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
	}
}
