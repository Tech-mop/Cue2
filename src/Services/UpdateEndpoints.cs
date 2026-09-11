// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Godot;

namespace Cue2.Services;

/// <summary>
/// GitHub Releases URLs, platform ids, and install-root helpers for the in-app updater.
/// </summary>
public static class UpdateEndpoints
{
	/// <summary>GitHub owner/repo used by the updater feed.</summary>
	public static string OwnerRepo => $"{Version.GitHubOwner}/{Version.GitHubRepo}";

	/// <summary>
	/// Primary feed (stable). GitHub <c>/releases/latest</c> skips prereleases and does not use the REST quota.
	/// </summary>
	public static string LatestJsonUrl =>
		$"https://github.com/{OwnerRepo}/releases/latest/download/latest.json";

	/// <summary>REST list used when the user includes prereleases, or when <c>latest.json</c> is missing.</summary>
	public static string ReleasesApiUrl =>
		$"https://api.github.com/repos/{OwnerRepo}/releases?per_page=15";

	/// <summary>User-Agent GitHub requires on API and download requests.</summary>
	public static string UserAgent =>
		$"Cue2/{Version.SemanticVersionString} (+https://github.com/{OwnerRepo})";

	/// <summary>HTML releases page (fallback when no matching asset exists).</summary>
	public static string ReleasesHtmlUrl =>
		$"https://github.com/{OwnerRepo}/releases";

	private static string[] _platformKeyCandidates;
	private static readonly object PlatformKeysLock = new();

	/// <summary>
	/// Preferred platform key matching <c>latest.json</c> <c>platforms</c> (process arch first).
	/// </summary>
	public static string CurrentPlatformKey()
	{
		string[] keys = PlatformKeyCandidates();
		return keys.Length > 0 ? keys[0] : "unknown";
	}

	/// <summary>
	/// Platform keys to try when picking an asset: process arch, OS arch, then x86_64 on ARM.
	/// Cached after the first call (must be from the Godot main thread).
	/// </summary>
	/// <returns>Deduplicated keys, best match first.</returns>
	public static string[] PlatformKeyCandidates()
	{
		string[] cached = _platformKeyCandidates;
		if (cached != null)
			return cached;

		lock (PlatformKeysLock)
		{
			if (_platformKeyCandidates != null)
				return _platformKeyCandidates;

			string os = MapOsName(OS.GetName());
			string process = MapArchitecture(RuntimeInformation.ProcessArchitecture);
			string machine = MapArchitecture(RuntimeInformation.OSArchitecture);

			var keys = new List<string>(4);
			void Add(string key)
			{
				if (!string.IsNullOrEmpty(key) && !keys.Contains(key))
					keys.Add(key);
			}

			Add($"{os}-{process}");
			Add($"{os}-{machine}");
			if (process == "arm64" || machine == "arm64")
				Add($"{os}-x86_64");

			_platformKeyCandidates = keys.ToArray();
			return _platformKeyCandidates;
		}
	}

	/// <summary>
	/// True when <paramref name="url"/> is HTTPS on GitHub or GitHub content hosts.
	/// </summary>
	public static bool IsAllowedDownloadUrl(string url)
	{
		if (!Uri.TryCreate(url, UriKind.Absolute, out Uri uri))
			return false;
		if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
			return false;

		string host = uri.Host.Trim().ToLowerInvariant();
		if (host == "github.com" || host == "www.github.com")
			return true;
		return host.EndsWith(".githubusercontent.com", StringComparison.Ordinal);
	}

	private static string MapOsName(string osName) => osName switch
	{
		"Windows" => "windows",
		"macOS" => "macos",
		"Linux" => "linux",
		_ => (osName ?? "unknown").ToLowerInvariant()
	};

	private static string MapArchitecture(Architecture arch) => arch switch
	{
		Architecture.X64 => "x86_64",
		Architecture.Arm64 => "arm64",
		Architecture.X86 => "x86",
		_ => arch.ToString().ToLowerInvariant()
	};

	/// <summary>
	/// Directory that would be replaced on Install and Restart (the <c>.app</c> on macOS).
	/// </summary>
	public static string GetInstallRoot()
	{
		string exe = OS.GetExecutablePath();
		if (string.IsNullOrEmpty(exe))
			return string.Empty;

		string exeDir = Path.GetDirectoryName(exe) ?? string.Empty;
		if (OS.GetName() != "macOS")
			return exeDir;

		// …/Cue2.app/Contents/MacOS/Cue2
		if (string.Equals(Path.GetFileName(exeDir), "MacOS", StringComparison.OrdinalIgnoreCase))
		{
			string contents = Path.GetDirectoryName(exeDir);
			if (!string.IsNullOrEmpty(contents) &&
			    string.Equals(Path.GetFileName(contents), "Contents", StringComparison.OrdinalIgnoreCase))
			{
				string app = Path.GetDirectoryName(contents);
				if (!string.IsNullOrEmpty(app) && app.EndsWith(".app", StringComparison.OrdinalIgnoreCase))
					return app;
			}
		}

		return exeDir;
	}

	/// <summary>
	/// True when Cue2 can write a probe file in <paramref name="directory"/>.
	/// </summary>
	public static bool IsDirectoryWritable(string directory)
	{
		if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
			return false;

		string probe = Path.Combine(directory, $".cue2-write-{Guid.NewGuid():N}");
		try
		{
			File.WriteAllText(probe, "ok");
			File.Delete(probe);
			return true;
		}
		catch (Exception)
		{
			try
			{
				if (File.Exists(probe))
					File.Delete(probe);
			}
			catch (Exception)
			{
				// ignore cleanup
			}

			return false;
		}
	}

	/// <summary>
	/// Absolute <c>user://updates</c> folder used for downloads and extracts.
	/// </summary>
	public static string GetUpdatesDirectory()
	{
		return ProjectSettings.GlobalizePath("user://updates");
	}
}
