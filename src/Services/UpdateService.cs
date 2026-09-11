// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System;
using System.IO;
using System.Net.Http.Headers;
using SysHttp = System.Net.Http.HttpClient;
using SysHttpRequest = System.Net.Http.HttpRequestMessage;
using SysHttpMethod = System.Net.Http.HttpMethod;
using SysHttpCompletion = System.Net.Http.HttpCompletionOption;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Cue2.UI.Utilities;
using Godot;

namespace Cue2.Services;

/// <summary>
/// Footer / Settings UI state for the updater (passed as int on <see cref="GlobalSignals.UpdateUiStateChanged"/>).
/// </summary>
public enum UpdateUiState
{
	/// <summary>No check in flight and nothing to show.</summary>
	Idle = 0,
	/// <summary>Fetching latest.json / GitHub API.</summary>
	Checking = 1,
	/// <summary>Running version is current (or skipped).</summary>
	UpToDate = 2,
	/// <summary>A newer release exists.</summary>
	Available = 3,
	/// <summary>Downloading the platform archive.</summary>
	Downloading = 4,
	/// <summary>Archive verified and extracted; Install and Restart can run.</summary>
	ReadyToInstall = 5,
	/// <summary>Helper launched; Cue2 is about to quit.</summary>
	Applying = 6,
	/// <summary>Last operation failed.</summary>
	Error = 7
}

/// <summary>
/// Checks GitHub Releases, downloads a verified archive, and applies it after user confirmation.
/// Child of <see cref="GlobalData"/>. Prefs live in <see cref="UserDataManager"/>.
/// </summary>
public partial class UpdateService : Node
{
	/// <summary>Auto-check throttle.</summary>
	public static readonly TimeSpan AutoCheckInterval = TimeSpan.FromHours(12);

	private GlobalSignals _globalSignals;
	private GlobalData _globalData;
	private static readonly SysHttp Http = CreateClient();

	private CancellationTokenSource _cts;
	private bool _busy;
	private long _lastProgressMs;

	/// <summary>Latest successful check result (may be up-to-date or an available update).</summary>
	public UpdateFeed LastFeed { get; private set; }

	/// <summary>User-facing last-error text (empty when none).</summary>
	public string LastError { get; private set; } = string.Empty;

	/// <summary>Current UI state.</summary>
	public UpdateUiState UiState { get; private set; } = UpdateUiState.Idle;

	/// <summary>Absolute path of the last verified extract (payload parent), or empty.</summary>
	public string ReadyExtractDir { get; private set; } = string.Empty;

	/// <summary>Layout of <see cref="ReadyExtractDir"/> when <see cref="UiState"/> is ReadyToInstall.</summary>
	public UpdateApplyHelper.ExtractedLayout ReadyLayout { get; private set; }

	/// <summary>True when this process is a Godot editor run (updater is disabled).</summary>
	public static bool IsEditorBuild => OS.HasFeature("editor");

	/// <inheritdoc />
	public override void _Ready()
	{
		_globalSignals = GetNode<GlobalSignals>("/root/GlobalSignals");
		_globalData = GetNode<GlobalData>("/root/GlobalData");
		Name = nameof(UpdateService);
		_ = UpdateEndpoints.PlatformKeyCandidates();
	}

	/// <inheritdoc />
	public override void _ExitTree()
	{
		_cts?.Cancel();
		_cts?.Dispose();
		_cts = null;
	}

	/// <summary>
	/// Checks for a newer release. No-op in the editor. Failures are logged; <paramref name="force"/>
	/// also reports them to Settings via <see cref="UpdateUiState.Error"/>.
	/// </summary>
	/// <param name="force">When true, ignores the 12-hour throttle (Check Now).</param>
	public void CheckForUpdates(bool force)
	{
		if (IsEditorBuild)
		{
			if (force)
				SetState(UpdateUiState.Error, UiLocalizer.T("Update checks run in exported builds only."));
			return;
		}

		if (_busy)
			return;

		var udm = _globalData?.UserDataManager;
		if (udm != null && !force && !udm.CheckForUpdatesOnStartup)
			return;

		if (!force && udm != null && DateTime.TryParse(udm.LastUpdateCheckUtc, out var last) &&
		    DateTime.UtcNow - last.ToUniversalTime() < AutoCheckInterval)
		{
			return;
		}

		_ = CheckAsync(force);
	}

	/// <summary>
	/// Downloads and verifies the current feed's platform asset, then extracts it.
	/// </summary>
	public void DownloadUpdate()
	{
		if (IsEditorBuild || _busy)
			return;
		if (LastFeed?.CurrentAsset == null)
		{
			SetState(UpdateUiState.Error, UiLocalizer.T("No download is available for this computer."));
			return;
		}

		if (string.IsNullOrWhiteSpace(LastFeed.CurrentAsset.Sha256))
		{
			SetState(UpdateUiState.Error, UiLocalizer.T("This release has no SHA-256 checksum; open the release page instead."));
			return;
		}

		if (!UpdateEndpoints.IsAllowedDownloadUrl(LastFeed.CurrentAsset.Url))
		{
			SetState(UpdateUiState.Error, UiLocalizer.T("This release is not hosted on GitHub."));
			return;
		}

		_ = DownloadAsync();
	}

	/// <summary>
	/// True when a verified payload is ready and no cues are playing.
	/// </summary>
	public bool CanApplyNow()
	{
		if (UiState != UpdateUiState.ReadyToInstall || ReadyLayout == null)
			return false;
		if (_globalData?.IsSessionLoading == true)
			return false;
		var exec = _globalData?.CueCommandExecutor;
		if (exec?.ActiveCues != null && exec.ActiveCues.Count > 0)
			return false;
		return true;
	}

	/// <summary>
	/// Starts the quit-and-swap helper and quits Cue2. Caller must confirm first.
	/// </summary>
	/// <returns>False when apply is not possible (caller should show <see cref="LastError"/>).</returns>
	public bool RequestApplyAndRelaunch()
	{
		if (!CanApplyNow())
		{
			var exec = _globalData?.CueCommandExecutor;
			if (exec?.ActiveCues != null && exec.ActiveCues.Count > 0)
			{
				LastError = UiLocalizer.T("Stop all playing cues before installing the update.");
				SetState(UpdateUiState.ReadyToInstall, LastError);
				return false;
			}

			LastError = UiLocalizer.T("No verified update is ready to install.");
			SetState(UpdateUiState.Error, LastError);
			return false;
		}

		string installRoot = UpdateEndpoints.GetInstallRoot();
		if (string.IsNullOrEmpty(installRoot) || !UpdateEndpoints.IsDirectoryWritable(installRoot))
		{
			RevealDownloadedArchive();
			LastError = UiLocalizer.T("Cue2 cannot write to its install folder. The downloaded update was shown in the file manager.");
			SetState(UpdateUiState.ReadyToInstall, LastError);
			return false;
		}

		string relaunch = ReadyLayout.IsMacApp
			? installRoot
			: Path.Combine(installRoot, ReadyLayout.RelativeRelaunch);

		SetState(UpdateUiState.Applying, UiLocalizer.T("Installing update…"));
		bool started = UpdateApplyHelper.LaunchReplaceAndRelaunch(
			ReadyLayout.PayloadRoot, installRoot, relaunch, ReadyLayout.IsMacApp);
		if (!started)
		{
			LastError = UiLocalizer.T("Could not start the update helper.");
			SetState(UpdateUiState.Error, LastError);
			return false;
		}

		GetTree()?.Quit();
		return true;
	}

	/// <summary>Opens the GitHub release (or releases list) in the default browser.</summary>
	public void OpenReleasePage()
	{
		string url = LastFeed?.HtmlUrl;
		if (string.IsNullOrWhiteSpace(url))
			url = UpdateEndpoints.ReleasesHtmlUrl;
		OS.ShellOpen(url);
	}

	/// <summary>Marks the current available version as skipped.</summary>
	public void SkipCurrentVersion()
	{
		if (LastFeed == null || string.IsNullOrWhiteSpace(LastFeed.Version))
			return;
		var udm = _globalData?.UserDataManager;
		if (udm != null)
			udm.SkippedUpdateVersion = LastFeed.Version;
		SetState(UpdateUiState.UpToDate, UiLocalizer.Tf("Skipped Cue2 {0}.", LastFeed.Version));
		EmitBackgroundCompleted();
	}

	/// <summary>Shows the verified zip/folder in the OS file manager when in-place apply is not possible.</summary>
	public void RevealDownloadedArchive()
	{
		string dir = ReadyExtractDir;
		if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
		{
			dir = UpdateEndpoints.GetUpdatesDirectory();
			if (!Directory.Exists(dir))
				return;
		}

		OS.ShellOpen(dir);
	}

	private async Task CheckAsync(bool force)
	{
		_busy = true;
		LastError = string.Empty;
		SetState(UpdateUiState.Checking, UiLocalizer.T("Checking for updates…"));
		EmitBackgroundProgress(0, true, UiLocalizer.T("Checking for updates…"), "");

		CancelAndReset();
		_cts = new CancellationTokenSource();
		_cts.CancelAfter(TimeSpan.FromSeconds(20));
		var token = _cts.Token;

		try
		{
			var udm = _globalData.UserDataManager;
			bool includePre = udm?.IncludePrereleaseUpdates == true;
			string updatesDir = UpdateEndpoints.GetUpdatesDirectory();

			UpdateFeed stable = await FetchLatestJsonAsync(token);
			UpdateFeed feed;
			if (includePre)
			{
				UpdateFeed api = await FetchGitHubApiAsync(includePrerelease: true, token);
				if (api != null && (stable == null || UpdateSemVer.IsNewer(api.Version, stable.Version)))
					feed = api;
				else
					feed = stable;
			}
			else
			{
				feed = stable ?? await FetchGitHubApiAsync(includePrerelease: false, token);
			}

			// HTTP used the 20s budget; hashing a cached zip must not be cancelled with it.
			_cts.CancelAfter(Timeout.InfiniteTimeSpan);

			bool extractReady = TryValidateReadyExtract(feed, updatesDir, out string extractDir, out var layout);
			string checkedAt = DateTime.UtcNow.ToString("o");
			RunOnMain(() => FinishCheck(force, feed, checkedAt, extractReady, extractDir, layout));
		}
		catch (OperationCanceledException)
		{
			RunOnMain(() =>
			{
				SetState(UpdateUiState.Idle, "");
				EmitBackgroundCompleted();
				_busy = false;
			});
		}
		catch (Exception ex)
		{
			GD.PrintErr($"UpdateService:CheckAsync - {ex.Message}");
			RunOnMain(() =>
			{
				LastError = UiLocalizer.T("Could not check for updates.");
				if (force)
					SetState(UpdateUiState.Error, LastError);
				else
					SetState(UpdateUiState.Idle, "");
				EmitBackgroundCompleted();
				Log($"Update check failed: {ex.Message}", 1);
				_busy = false;
			});
		}
	}

	private void FinishCheck(
		bool force,
		UpdateFeed feed,
		string checkedAt,
		bool extractReady,
		string extractDir,
		UpdateApplyHelper.ExtractedLayout layout)
	{
		var udm = _globalData?.UserDataManager;
		if (udm != null)
			udm.LastUpdateCheckUtc = checkedAt;

		LastFeed = feed;
		if (feed == null)
		{
			LastError = UiLocalizer.T("Could not read the update feed.");
			if (force)
				SetState(UpdateUiState.Error, LastError);
			else
				SetState(UpdateUiState.Idle, "");
			EmitBackgroundCompleted();
			Log(LastError, 1);
			_busy = false;
			return;
		}

		bool newer = feed.IsNewerThanRunning();
		bool skipped = udm != null &&
		               !string.IsNullOrEmpty(udm.SkippedUpdateVersion) &&
		               string.Equals(udm.SkippedUpdateVersion, feed.Version, StringComparison.OrdinalIgnoreCase);

		if (!newer || skipped)
		{
			SetState(UpdateUiState.UpToDate, UiLocalizer.T("Cue2 is up to date."));
			EmitBackgroundCompleted();
			_busy = false;
			return;
		}

		if (extractReady && layout != null && !string.IsNullOrEmpty(extractDir))
		{
			ReadyExtractDir = extractDir;
			ReadyLayout = layout;
			SetState(UpdateUiState.ReadyToInstall,
				UiLocalizer.Tf("Cue2 {0} is downloaded and ready to install.", feed.Version));
			EmitAvailableFooter(feed.Version);
			_busy = false;
			return;
		}

		SetState(UpdateUiState.Available, UiLocalizer.Tf("Cue2 {0} is available.", feed.Version));
		EmitAvailableFooter(feed.Version);
		Log($"Update available: {feed.Version} ({UpdateEndpoints.CurrentPlatformKey()})", 0);
		_busy = false;
	}

	private async Task DownloadAsync()
	{
		_busy = true;
		var asset = LastFeed.CurrentAsset;
		string version = LastFeed.Version;
		CancelAndReset();
		_cts = new CancellationTokenSource();
		var token = _cts.Token;

		string versionDir = Path.Combine(UpdateEndpoints.GetUpdatesDirectory(), Sanitize(version));
		string archivePath = Path.Combine(versionDir, SanitizeFile(asset.Name));
		string extractDir = Path.Combine(versionDir, "extracted");

		try
		{
			Directory.CreateDirectory(versionDir);
			RunOnMain(() =>
			{
				SetState(UpdateUiState.Downloading, UiLocalizer.Tf("Downloading Cue2 {0}…", version));
				EmitBackgroundProgress(0, true, UiLocalizer.Tf("Downloading Cue2 {0}…", version), asset.Name);
			});

			await DownloadAndHashAsync(asset, archivePath, version, token);

			RunOnMain(() =>
			{
				SetState(UpdateUiState.Downloading, UiLocalizer.T("Verifying update…"));
				EmitBackgroundProgress(100, true, UiLocalizer.T("Verifying update…"), "");
			});

			UpdateApplyHelper.ExtractArchive(archivePath, extractDir);
			var layout = UpdateApplyHelper.FindLayout(extractDir);
			RunOnMain(() => FinishDownload(version, extractDir, layout, archivePath));
		}
		catch (OperationCanceledException)
		{
			RunOnMain(() =>
			{
				SetState(UpdateUiState.Available, UiLocalizer.Tf("Cue2 {0} is available.", version));
				EmitAvailableFooter(version);
				_busy = false;
			});
		}
		catch (Exception ex)
		{
			GD.PrintErr($"UpdateService:DownloadAsync - {ex.Message}");
			TryDelete(archivePath);
			TryDeleteDir(extractDir);
			RunOnMain(() =>
			{
				LastError = UiLocalizer.T("Download or verification failed.");
				SetState(UpdateUiState.Error, LastError);
				EmitBackgroundCompleted();
				Log($"Update download failed: {ex.Message}", 2);
				_busy = false;
			});
		}
	}

	private void FinishDownload(string version, string extractDir, UpdateApplyHelper.ExtractedLayout layout, string archivePath)
	{
		if (layout == null)
		{
			LastError = UiLocalizer.T("The downloaded update did not contain a Cue2 application.");
			SetState(UpdateUiState.Error, LastError);
			EmitBackgroundCompleted();
			TryDelete(archivePath);
			TryDeleteDir(extractDir);
			_busy = false;
			return;
		}

		ReadyExtractDir = extractDir;
		ReadyLayout = layout;
		SetState(UpdateUiState.ReadyToInstall,
			UiLocalizer.Tf("Cue2 {0} is downloaded and ready to install.", version));
		EmitAvailableFooter(version);
		Log($"Update {version} downloaded and verified.", 0);
		_busy = false;
	}

	private async Task<UpdateFeed> FetchLatestJsonAsync(CancellationToken token)
	{
		using var req = new SysHttpRequest(SysHttpMethod.Get, UpdateEndpoints.LatestJsonUrl);
		req.Headers.Accept.ParseAdd("application/json");
		using var resp = await Http.SendAsync(req, token);
		if (!resp.IsSuccessStatusCode)
		{
			GD.Print($"UpdateService:FetchLatestJsonAsync - HTTP {(int)resp.StatusCode} from GitHub latest.json");
			return null;
		}

		string json = await resp.Content.ReadAsStringAsync(token);
		return UpdateManifestParser.ParseLatestJson(json);
	}

	private async Task<UpdateFeed> FetchGitHubApiAsync(bool includePrerelease, CancellationToken token)
	{
		using var req = new SysHttpRequest(SysHttpMethod.Get, UpdateEndpoints.ReleasesApiUrl);
		req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
		req.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
		using var resp = await Http.SendAsync(req, token);
		if (!resp.IsSuccessStatusCode)
		{
			GD.Print($"UpdateService:FetchGitHubApiAsync - HTTP {(int)resp.StatusCode}");
			return null;
		}

		string json = await resp.Content.ReadAsStringAsync(token);
		return UpdateManifestParser.ParseGitHubReleases(json, includePrerelease);
	}

	private async Task DownloadAndHashAsync(UpdatePlatformAsset asset, string destPath, string version, CancellationToken token)
	{
		if (!UpdateEndpoints.IsAllowedDownloadUrl(asset.Url))
			throw new InvalidOperationException("Update URL must be HTTPS on GitHub.");

		using var req = new SysHttpRequest(SysHttpMethod.Get, asset.Url);
		req.Headers.Accept.ParseAdd("*/*");
		using var resp = await Http.SendAsync(req, SysHttpCompletion.ResponseHeadersRead, token);
		resp.EnsureSuccessStatusCode();

		long total = resp.Content.Headers.ContentLength ?? asset.Size;
		if (asset.Size > 0 && total > 0 && Math.Abs(total - asset.Size) > 1024)
			throw new InvalidOperationException("Download size did not match the update manifest.");

		await using var httpStream = await resp.Content.ReadAsStreamAsync(token);
		await using var fs = new FileStream(destPath, FileMode.Create, System.IO.FileAccess.Write, FileShare.None, 81920, true);
		using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

		var buffer = new byte[81920];
		long copied = 0;
		int read;
		while ((read = await httpStream.ReadAsync(buffer.AsMemory(0, buffer.Length), token)) > 0)
		{
			await fs.WriteAsync(buffer.AsMemory(0, read), token);
			sha.AppendData(buffer.AsSpan(0, read));
			copied += read;
			if (total > 0)
			{
				float pct = Math.Clamp(copied * 100f / total, 0, 99);
				long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
				if (pct >= 99 || now - _lastProgressMs >= 150)
				{
					_lastProgressMs = now;
					string name = asset.Name;
					RunOnMain(() =>
					{
						string status = UiLocalizer.Tf("Downloading Cue2 {0}…", version);
						EmitBackgroundProgress(pct, true, status, name);
					});
				}
			}
		}

		string hex = Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
		string expected = asset.Sha256.Trim().ToLowerInvariant();
		if (!string.Equals(hex, expected, StringComparison.Ordinal))
			throw new InvalidOperationException("SHA-256 mismatch.");
	}

	/// <summary>
	/// Trusts a previous extract only when the zip is still present and its SHA-256 matches the feed.
	/// Safe to call off the Godot main thread (System.IO + hashing only).
	/// </summary>
	private static bool TryValidateReadyExtract(
		UpdateFeed feed,
		string updatesDir,
		out string extractDir,
		out UpdateApplyHelper.ExtractedLayout layout)
	{
		extractDir = string.Empty;
		layout = null;
		var asset = feed?.CurrentAsset;
		if (asset == null
		    || string.IsNullOrWhiteSpace(asset.Sha256)
		    || string.IsNullOrWhiteSpace(feed.Version)
		    || string.IsNullOrWhiteSpace(updatesDir))
			return false;

		string versionDir = Path.Combine(updatesDir, Sanitize(feed.Version));
		string archivePath = Path.Combine(versionDir, SanitizeFile(asset.Name));
		extractDir = Path.Combine(versionDir, "extracted");
		if (!File.Exists(archivePath) || !Directory.Exists(extractDir))
			return false;
		if (!FileSha256Equals(archivePath, asset.Sha256))
			return false;

		layout = UpdateApplyHelper.FindLayout(extractDir);
		return layout != null;
	}

	private static bool FileSha256Equals(string path, string expectedHex)
	{
		try
		{
			using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
			using var fs = new FileStream(path, FileMode.Open, System.IO.FileAccess.Read, FileShare.Read, 81920);
			var buffer = new byte[81920];
			int read;
			while ((read = fs.Read(buffer, 0, buffer.Length)) > 0)
				sha.AppendData(buffer.AsSpan(0, read));
			string hex = Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
			return string.Equals(hex, expectedHex.Trim().ToLowerInvariant(), StringComparison.Ordinal);
		}
		catch (Exception)
		{
			return false;
		}
	}

	private void RunOnMain(Action action)
	{
		if (action == null)
			return;
		Callable.From(() =>
		{
			if (!GodotObject.IsInstanceValid(this))
				return;
			action();
		}).CallDeferred();
	}

	private void SetState(UpdateUiState state, string message)
	{
		UiState = state;
		_globalSignals?.EmitSignal(GlobalSignals.SignalName.UpdateUiStateChanged, (int)state, message ?? "");
	}

	private void EmitBackgroundProgress(float percent, bool busy, string status, string detail)
	{
		_globalSignals?.EmitSignal(GlobalSignals.SignalName.BackgroundProcessProgress,
			percent, busy, status ?? "", detail ?? "", 0, busy ? 1 : 0);
	}

	private void EmitBackgroundCompleted()
	{
		_globalSignals?.EmitSignal(GlobalSignals.SignalName.BackgroundProcessCompleted);
	}

	private void EmitAvailableFooter(string version)
	{
		string status = UiLocalizer.Tf("Update available — Cue2 {0}", version);
		_globalSignals?.EmitSignal(GlobalSignals.SignalName.BackgroundProcessProgress,
			100f, false, status, UiLocalizer.T("Open Settings → Updates"), 1, 1);
	}

	private void Log(string message, int type)
	{
		_globalSignals?.EmitSignal(GlobalSignals.SignalName.Log, message, type);
	}

	private void CancelAndReset()
	{
		try
		{
			_cts?.Cancel();
		}
		catch (Exception)
		{
			// ignore
		}

		_cts?.Dispose();
		_cts = null;
	}

	private static SysHttp CreateClient()
	{
		var client = new SysHttp
		{
			Timeout = TimeSpan.FromMinutes(30)
		};
		client.DefaultRequestHeaders.UserAgent.ParseAdd(UpdateEndpoints.UserAgent);
		return client;
	}

	private static string Sanitize(string version)
	{
		var sb = new StringBuilder();
		foreach (char c in version ?? "")
		{
			if (char.IsLetterOrDigit(c) || c is '.' or '-' or '_')
				sb.Append(c);
		}

		return sb.Length > 0 ? sb.ToString() : "unknown";
	}

	private static string SanitizeFile(string name)
	{
		string file = Path.GetFileName(name ?? "");
		return string.IsNullOrEmpty(file) ? "update.bin" : file;
	}

	private static void TryDelete(string path)
	{
		try
		{
			if (!string.IsNullOrEmpty(path) && File.Exists(path))
				File.Delete(path);
		}
		catch (Exception)
		{
			// ignore
		}
	}

	private static void TryDeleteDir(string path)
	{
		try
		{
			if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
				Directory.Delete(path, recursive: true);
		}
		catch (Exception)
		{
			// ignore
		}
	}
}
