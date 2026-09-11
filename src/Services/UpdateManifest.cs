// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cue2.Services;

/// <summary>
/// Parsed GitHub <c>latest.json</c> (schema 1) plus the asset for this machine.
/// </summary>
public sealed class UpdateFeed
{
	/// <summary>Schema version; currently 1.</summary>
	[JsonPropertyName("schema")]
	public int Schema { get; set; }

	/// <summary>Product name.</summary>
	[JsonPropertyName("name")]
	public string Name { get; set; } = "Cue2";

	/// <summary>Semantic version without a leading <c>v</c>.</summary>
	[JsonPropertyName("version")]
	public string Version { get; set; } = string.Empty;

	/// <summary>GitHub tag, e.g. <c>v0.2.0</c>.</summary>
	[JsonPropertyName("tag")]
	public string Tag { get; set; } = string.Empty;

	/// <summary>UTC release timestamp (ISO-8601).</summary>
	[JsonPropertyName("releasedAt")]
	public string ReleasedAt { get; set; } = string.Empty;

	/// <summary>User-facing notes (not translated).</summary>
	[JsonPropertyName("notes")]
	public string Notes { get; set; } = string.Empty;

	/// <summary>URL for longer notes.</summary>
	[JsonPropertyName("notesUrl")]
	public string NotesUrl { get; set; } = string.Empty;

	/// <summary>GitHub release HTML page.</summary>
	[JsonPropertyName("htmlUrl")]
	public string HtmlUrl { get; set; } = string.Empty;

	/// <summary>Per-platform download metadata.</summary>
	[JsonPropertyName("platforms")]
	public Dictionary<string, UpdatePlatformAsset> Platforms { get; set; } = new(StringComparer.OrdinalIgnoreCase);

	/// <summary>Asset for this machine (process arch, then OS arch / x86_64 fallback), or null.</summary>
	[JsonIgnore]
	public UpdatePlatformAsset CurrentAsset
	{
		get
		{
			if (Platforms == null || Platforms.Count == 0)
				return null;
			foreach (string key in UpdateEndpoints.PlatformKeyCandidates())
			{
				if (Platforms.TryGetValue(key, out var asset)
				    && asset != null
				    && !string.IsNullOrWhiteSpace(asset.Url))
					return asset;
			}

			return null;
		}
	}

	/// <summary>True when this feed is newer than the running <see cref="Cue2.Version.SemanticVersionString"/>.</summary>
	public bool IsNewerThanRunning()
	{
		return UpdateSemVer.IsNewer(Version, Cue2.Version.SemanticVersionString);
	}
}

/// <summary>One platform archive listed in <see cref="UpdateFeed.Platforms"/>.</summary>
public sealed class UpdatePlatformAsset
{
	/// <summary>Archive file name.</summary>
	[JsonPropertyName("name")]
	public string Name { get; set; } = string.Empty;

	/// <summary>HTTPS download URL.</summary>
	[JsonPropertyName("url")]
	public string Url { get; set; } = string.Empty;

	/// <summary>Lowercase hex SHA-256 of the archive. Required for in-app download.</summary>
	[JsonPropertyName("sha256")]
	public string Sha256 { get; set; } = string.Empty;

	/// <summary>Archive size in bytes (0 if unknown).</summary>
	[JsonPropertyName("size")]
	public long Size { get; set; }
}

/// <summary>Parse helpers for <c>latest.json</c> and GitHub Releases API payloads.</summary>
public static class UpdateManifestParser
{
	private static readonly JsonSerializerOptions JsonOptions = new()
	{
		PropertyNameCaseInsensitive = true,
		ReadCommentHandling = JsonCommentHandling.Skip,
		AllowTrailingCommas = true
	};

	/// <summary>
	/// Parses a <c>latest.json</c> body.
	/// </summary>
	/// <param name="json">JSON text.</param>
	/// <returns>Feed, or null when required fields are missing.</returns>
	public static UpdateFeed ParseLatestJson(string json)
	{
		if (string.IsNullOrWhiteSpace(json))
			return null;

		UpdateFeed feed;
		try
		{
			feed = JsonSerializer.Deserialize<UpdateFeed>(json, JsonOptions);
		}
		catch (JsonException)
		{
			return null;
		}

		if (feed == null || string.IsNullOrWhiteSpace(feed.Version))
			return null;

		feed.Version = feed.Version.Trim().TrimStart('v', 'V');
		if (string.IsNullOrWhiteSpace(feed.Tag))
			feed.Tag = "v" + feed.Version;
		if (string.IsNullOrWhiteSpace(feed.HtmlUrl))
			feed.HtmlUrl = UpdateEndpoints.ReleasesHtmlUrl;
		feed.Platforms ??= new Dictionary<string, UpdatePlatformAsset>(StringComparer.OrdinalIgnoreCase);
		return feed;
	}

	/// <summary>
	/// Builds a feed from the GitHub Releases REST list (newest matching channel).
	/// </summary>
	/// <param name="json">API JSON array.</param>
	/// <param name="includePrerelease">When true, prereleases are eligible.</param>
	/// <returns>Feed, or null when nothing usable is listed.</returns>
	public static UpdateFeed ParseGitHubReleases(string json, bool includePrerelease)
	{
		if (string.IsNullOrWhiteSpace(json))
			return null;

		using var doc = JsonDocument.Parse(json);
		if (doc.RootElement.ValueKind != JsonValueKind.Array)
			return null;

		UpdateFeed best = null;
		foreach (var rel in doc.RootElement.EnumerateArray())
		{
			if (rel.TryGetProperty("draft", out var draft) && draft.GetBoolean())
				continue;

			bool pre = rel.TryGetProperty("prerelease", out var preEl) && preEl.GetBoolean();
			if (pre && !includePrerelease)
				continue;

			string tag = rel.TryGetProperty("tag_name", out var tagEl) ? tagEl.GetString() ?? "" : "";
			string version = UpdateSemVer.Normalize(tag);
			if (string.IsNullOrEmpty(version) || !UpdateSemVer.TryParse(version, out _))
				continue;

			var feed = new UpdateFeed
			{
				Schema = 1,
				Name = "Cue2",
				Version = version,
				Tag = string.IsNullOrWhiteSpace(tag) ? "v" + version : tag,
				ReleasedAt = rel.TryGetProperty("published_at", out var pub) ? pub.GetString() ?? "" : "",
				Notes = rel.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "",
				HtmlUrl = rel.TryGetProperty("html_url", out var html) ? html.GetString() ?? "" : UpdateEndpoints.ReleasesHtmlUrl,
				NotesUrl = rel.TryGetProperty("html_url", out var notesUrl) ? notesUrl.GetString() ?? "" : "",
				Platforms = new Dictionary<string, UpdatePlatformAsset>(StringComparer.OrdinalIgnoreCase)
			};

			if (rel.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
			{
				foreach (var asset in assets.EnumerateArray())
					TryAddAsset(feed, asset);
			}

			if (best == null || UpdateSemVer.IsNewer(feed.Version, best.Version))
				best = feed;
		}

		return best;
	}

	private static void TryAddAsset(UpdateFeed feed, JsonElement asset)
	{
		string name = asset.TryGetProperty("name", out var nameEl) ? nameEl.GetString() ?? "" : "";
		string url = asset.TryGetProperty("browser_download_url", out var urlEl) ? urlEl.GetString() ?? "" : "";
		if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(url))
			return;

		string platform = MatchPlatform(name);
		if (platform == null)
			return;

		string sha = "";
		if (asset.TryGetProperty("digest", out var digestEl))
		{
			string digest = digestEl.GetString() ?? "";
			const string prefix = "sha256:";
			if (digest.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
				sha = digest[prefix.Length..].Trim();
		}

		long size = 0;
		if (asset.TryGetProperty("size", out var sizeEl) && sizeEl.TryGetInt64(out long s))
			size = s;

		feed.Platforms[platform] = new UpdatePlatformAsset
		{
			Name = name,
			Url = url,
			Sha256 = sha,
			Size = size
		};
	}

	private static string MatchPlatform(string fileName)
	{
		string lower = fileName.ToLowerInvariant();
		string[] keys =
		{
			"windows-x86_64", "windows-arm64",
			"macos-arm64", "macos-x86_64",
			"linux-x86_64", "linux-arm64"
		};
		return keys.FirstOrDefault(k => lower.Contains(k, StringComparison.Ordinal));
	}
}

/// <summary>Minimal SemVer compare for Cue2 tags (<c>1.2.3</c> and optional <c>-rc.1</c>).</summary>
public static class UpdateSemVer
{
	/// <summary>Strips a leading <c>v</c> and surrounding whitespace.</summary>
	public static string Normalize(string raw)
	{
		if (string.IsNullOrWhiteSpace(raw))
			return string.Empty;
		string s = raw.Trim();
		if (s.StartsWith("v", StringComparison.OrdinalIgnoreCase))
			s = s[1..];
		return s.Trim();
	}

	/// <summary>
	/// True when <paramref name="candidate"/> is a higher SemVer than <paramref name="current"/>.
	/// </summary>
	public static bool IsNewer(string candidate, string current)
	{
		if (!TryParse(Normalize(candidate), out var a) || !TryParse(Normalize(current), out var b))
			return false;
		return Compare(a, b) > 0;
	}

	/// <summary>Parses <c>major.minor.patch[-prerelease]</c>.</summary>
	public static bool TryParse(string version, out SemVer value)
	{
		value = default;
		if (string.IsNullOrWhiteSpace(version))
			return false;

		string core = version;
		string pre = "";
		int dash = version.IndexOf('-');
		if (dash >= 0)
		{
			core = version[..dash];
			pre = version[(dash + 1)..];
		}

		string[] parts = core.Split('.');
		if (parts.Length < 1 || parts.Length > 3)
			return false;
		if (!int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int major))
			return false;
		int minor = 0;
		int patch = 0;
		if (parts.Length > 1 &&
		    !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out minor))
			return false;
		if (parts.Length > 2 &&
		    !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out patch))
			return false;

		value = new SemVer(major, minor, patch, pre ?? "");
		return true;
	}

	/// <summary>SemVer 2.0-ish compare: prerelease is older than the same numeric triple.</summary>
	public static int Compare(SemVer a, SemVer b)
	{
		int c = a.Major.CompareTo(b.Major);
		if (c != 0) return c;
		c = a.Minor.CompareTo(b.Minor);
		if (c != 0) return c;
		c = a.Patch.CompareTo(b.Patch);
		if (c != 0) return c;

		bool aPre = !string.IsNullOrEmpty(a.Prerelease);
		bool bPre = !string.IsNullOrEmpty(b.Prerelease);
		if (!aPre && !bPre) return 0;
		if (!aPre) return 1;
		if (!bPre) return -1;
		return ComparePrerelease(a.Prerelease, b.Prerelease);
	}

	private static int ComparePrerelease(string a, string b)
	{
		string[] ap = a.Split('.');
		string[] bp = b.Split('.');
		int n = Math.Max(ap.Length, bp.Length);
		for (int i = 0; i < n; i++)
		{
			if (i >= ap.Length) return -1;
			if (i >= bp.Length) return 1;
			bool aNum = int.TryParse(ap[i], NumberStyles.None, CultureInfo.InvariantCulture, out int ai);
			bool bNum = int.TryParse(bp[i], NumberStyles.None, CultureInfo.InvariantCulture, out int bi);
			if (aNum && bNum)
			{
				int c = ai.CompareTo(bi);
				if (c != 0) return c;
				continue;
			}

			if (aNum != bNum)
				return aNum ? -1 : 1;

			int sc = string.Compare(ap[i], bp[i], StringComparison.Ordinal);
			if (sc != 0) return sc;
		}

		return 0;
	}

	/// <summary>Parsed semantic version.</summary>
	public readonly struct SemVer
	{
		/// <summary>Major component.</summary>
		public int Major { get; }
		/// <summary>Minor component.</summary>
		public int Minor { get; }
		/// <summary>Patch component.</summary>
		public int Patch { get; }
		/// <summary>Optional prerelease identifier (no leading dash).</summary>
		public string Prerelease { get; }

		/// <summary>Creates a parsed version.</summary>
		public SemVer(int major, int minor, int patch, string prerelease)
		{
			Major = major;
			Minor = minor;
			Patch = patch;
			Prerelease = prerelease ?? "";
		}
	}
}
