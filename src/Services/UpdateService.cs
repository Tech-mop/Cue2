// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System;
using System.IO;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Cue2.UI.Utilities;
using Godot;
using SysHttp = System.Net.Http.HttpClient;
using SysHttpCompletion = System.Net.Http.HttpCompletionOption;
using SysHttpMethod = System.Net.Http.HttpMethod;
using SysHttpRequest = System.Net.Http.HttpRequestMessage;

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

	/// <summary>Time budget for the feed requests of one check.</summary>
	private static readonly TimeSpan CheckHttpTimeout = TimeSpan.FromSeconds(20);

	/// <summary>A download with no data for this long is treated as stalled and stopped.</summary>
	private static readonly TimeSpan DownloadStallTimeout = TimeSpan.FromSeconds(30);

	private const int HashBufferSize = 81920;

	/// <summary>Free space needed, as a multiple of the archive size: archive, extract and staging copy.</summary>
	private const long FreeSpaceFactor = 3;

	private const long ProgressIntervalMs = 150;

	private CancellationTokenSource _cts;
	private bool _busy;

	/// <summary>Version of <see cref="ReadyExtractDir"/>, passed to the helper for its result file.</summary>
	private string _readyVersion = string.Empty;

	/// <summary>Archive SHA-256 of <see cref="ReadyExtractDir"/>; its extract marker must match before install.</summary>
	private string _readySha256 = string.Empty;

	/// <summary>Latest successful check result (may be up-to-date or an available update).</summary>
	public UpdateFeed LastFeed { get; private set; }

	/// <summary>User-facing last-error text (empty when none).</summary>
	public string LastError { get; private set; } = string.Empty;

	/// <summary>Localized message sent with the last <see cref="UiState"/> change.</summary>
	public string StatusMessage { get; private set; } = string.Empty;

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
		ReportLastApplyResult();
	}

	/// <summary>
	/// Reads the result the update helper left for this launch (pool thread) and reports it.
	/// </summary>
	private void ReportLastApplyResult()
	{
		if (IsEditorBuild)
			return;
		string updatesDir = UpdateEndpoints.GetUpdatesDirectory();
		_ = Task.Run(() =>
		{
			var result = UpdateApplyHelper.ReadAndClearApplyResult(updatesDir);
			if (result == null)
				return;
			// After a good install the downloads and the old-install backup are no longer needed.
			// After a failure they stay: the backup may be the only copy, and the download can be retried.
			if (result.Succeeded)
				DeleteDownloads(updatesDir, keepVersionDir: null, includeBackup: true);
			RunOnMain(() => ShowApplyResult(result));
		});
	}

	private void ShowApplyResult(UpdateApplyHelper.ApplyResult result)
	{
		if (result.Succeeded)
		{
			if (string.Equals(result.Version, Cue2.Version.SemanticVersionString, StringComparison.OrdinalIgnoreCase))
				Log($"Updated to Cue2 {result.Version}.", (int)LogType.Info);
			else
				Log($"The update helper installed Cue2 {result.Version}, but this is Cue2 {Cue2.Version.SemanticVersionString}.", (int)LogType.Warning);
			return;
		}

		string logPath = UpdateApplyHelper.HelperLogPath;
		LastError = result.HelperDidNotRun
			? UiLocalizer.Tf("The update to Cue2 {0} did not run. Details: {1}", result.Version, logPath)
			: UiLocalizer.Tf("The update to Cue2 {0} could not be installed: {1} Details: {2}", result.Version, result.Detail, logPath);
		SetState(UpdateUiState.Error, LastError);
		Log(result.HelperDidNotRun
				? $"Update to Cue2 {result.Version} did not run (no result from the helper). See {logPath}"
				: $"Update to Cue2 {result.Version} failed: {result.Detail} See {logPath}",
			(int)LogType.Error);
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

		StartCheck(force, udm?.IncludePrereleaseUpdates == true);
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

		long needed = LastFeed.CurrentAsset.Size * FreeSpaceFactor;
		long free = GetFreeSpace(UpdateEndpoints.GetUpdatesDirectory());
		if (needed > 0 && free >= 0 && free < needed)
		{
			LastError = UiLocalizer.Tf("Not enough free disk space for the update. It needs about {0} MB.",
				(needed + (1024 * 1024) - 1) / (1024 * 1024));
			SetState(UpdateUiState.Error, LastError);
			Log($"Update download not started: {free} bytes free, about {needed} needed.", (int)LogType.Warning);
			return;
		}

		StartDownload(LastFeed.CurrentAsset, LastFeed.Version);
	}

	/// <summary>Stops a running download. The partial file is deleted and the update stays available.</summary>
	public void CancelDownload()
	{
		if (UiState != UpdateUiState.Downloading || _cts == null)
			return;
		try
		{
			_cts.Cancel();
		}
		catch (ObjectDisposedException)
		{
			// Download already finished.
		}
	}

	/// <summary>
	/// True when a verified payload is ready, no show is loading and Show mode is off.
	/// Playing cues do not block: Install is a user choice, and quitting stops them.
	/// </summary>
	public bool CanApplyNow() => string.IsNullOrEmpty(GetApplyBlockedReason());

	/// <summary>
	/// User-facing reason why Install and Restart cannot run now, or empty when it can.
	/// </summary>
	/// <returns>Localized reason, or an empty string.</returns>
	public string GetApplyBlockedReason()
	{
		if (UiState != UpdateUiState.ReadyToInstall || ReadyLayout == null)
			return UiLocalizer.T("No verified update is ready to install.");
		if (_globalData?.Settings?.ShowMode == true)
			return UiLocalizer.T("Leave Show mode to install the update.");
		if (_globalData?.IsSessionLoading == true)
			return UiLocalizer.T("Wait for the show to finish loading, then install the update.");
		return string.Empty;
	}

	/// <summary>
	/// Install and Restart, after the user confirmed. Runs the normal quit flow (save prompt,
	/// running background tasks); the helper starts only when Cue2 is certain to quit.
	/// </summary>
	/// <returns>False when apply is not possible now (see <see cref="LastError"/>).</returns>
	public bool RequestApplyAndRelaunch()
	{
		if (!TryGetApplyTarget(out _, out _))
			return false;

		var saveManager = GetNodeOrNull<SaveManager>("/root/SaveManager");
		if (saveManager == null)
		{
			GD.PrintErr("UpdateService:RequestApplyAndRelaunch - SaveManager not found.");
			LastError = UiLocalizer.T("Could not start the update helper.");
			SetState(UpdateUiState.Error, LastError);
			return false;
		}

		saveManager.RequestQuitForUpdate(StartApplyHelper);
		return true;
	}

	/// <summary>
	/// Checks the apply guard and the install folder. On failure sets <see cref="LastError"/>.
	/// </summary>
	private bool TryGetApplyTarget(out string installRoot, out string relaunch)
	{
		installRoot = string.Empty;
		relaunch = string.Empty;
		string reason = GetApplyBlockedReason();
		if (!string.IsNullOrEmpty(reason))
		{
			LastError = reason;
			SetState(UiState == UpdateUiState.ReadyToInstall ? UpdateUiState.ReadyToInstall : UpdateUiState.Error, reason);
			return false;
		}

		installRoot = UpdateEndpoints.GetInstallRoot();
		if (string.IsNullOrEmpty(installRoot) || !UpdateEndpoints.CanReplaceInstall(installRoot, ReadyLayout.IsMacApp))
		{
			RevealDownloadedArchive();
			LastError = UiLocalizer.T("Cue2 cannot write to its install folder. The downloaded update was shown in the file manager.");
			SetState(UpdateUiState.ReadyToInstall, LastError);
			return false;
		}

		if (!UpdateApplyHelper.IsExtractComplete(ReadyExtractDir, _readySha256))
		{
			LastError = UiLocalizer.T("The downloaded update is incomplete. Check for updates and download it again.");
			SetState(UpdateUiState.Error, LastError);
			return false;
		}

		relaunch = ReadyLayout.IsMacApp
			? installRoot
			: Path.Combine(installRoot, ReadyLayout.RelativeRelaunch);
		return true;
	}

	/// <summary>
	/// Called by <see cref="SaveManager"/> when the quit is certain. Re-checks the guard (Show mode
	/// may have been switched on while a prompt was open) and starts the helper.
	/// </summary>
	/// <returns>True when the helper started and Cue2 may quit.</returns>
	private bool StartApplyHelper()
	{
		if (!GodotObject.IsInstanceValid(this) || !TryGetApplyTarget(out string installRoot, out string relaunch))
			return false;

		SetState(UpdateUiState.Applying, UiLocalizer.T("Installing update…"));
		// On failure the helper starts the running build again: the .app on macOS, else this host.
		string oldRelaunch = ReadyLayout.IsMacApp ? installRoot : OS.GetExecutablePath();
		bool started = UpdateApplyHelper.LaunchReplaceAndRelaunch(
			ReadyLayout.PayloadRoot, installRoot, relaunch, oldRelaunch, ReadyLayout.IsMacApp, _readyVersion,
			out string helperError);
		if (!started)
		{
			LastError = UiLocalizer.T("Could not start the update helper.");
			SetState(UpdateUiState.Error, LastError);
			Log($"Could not start the update helper: {helperError}", (int)LogType.Error);
			return false;
		}

		return true;
	}

	/// <summary>Opens the GitHub release (or releases list) in the default browser.</summary>
	public void OpenReleasePage()
	{
		string url = LastFeed?.HtmlUrl;
		if (!UpdateEndpoints.IsAllowedReleasePageUrl(url))
		{
			if (!string.IsNullOrWhiteSpace(url))
				GD.PrintErr($"UpdateService:OpenReleasePage - Release link is not a Cue2 GitHub page, opening the releases list instead: {url}");
			url = UpdateEndpoints.ReleasesHtmlUrl;
		}

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

		if (!_busy)
		{
			ReadyExtractDir = null;
			ReadyLayout = null;
			_readyVersion = string.Empty;
			_readySha256 = string.Empty;
			string versionDir = UpdatePaths.For(UpdateEndpoints.GetUpdatesDirectory(), LastFeed.Version, "").VersionDir;
			_ = Task.Run(() => TryDeleteDir(versionDir));
		}

		SetState(UpdateUiState.UpToDate, UiLocalizer.Tf("Skipped Cue2 {0}.", LastFeed.Version));
		EmitBackgroundCompleted();
	}

	/// <summary>
	/// Shows the downloaded version folder (verified archive and its extract) in the OS file manager,
	/// for installing by hand when in-place apply is not possible.
	/// </summary>
	public void RevealDownloadedArchive()
	{
		string dir = string.IsNullOrEmpty(ReadyExtractDir) ? null : Path.GetDirectoryName(ReadyExtractDir);
		if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
		{
			dir = UpdateEndpoints.GetUpdatesDirectory();
			if (!Directory.Exists(dir))
				return;
		}

		OS.ShellOpen(dir);
	}

	/// <summary>
	/// Starts a check on a pool thread. Main thread only: reads Godot paths and prefs first,
	/// so the worker touches only System.Net, System.IO and hashing.
	/// </summary>
	private void StartCheck(bool force, bool includePrerelease)
	{
		_busy = true;
		LastError = string.Empty;
		string checking = UiLocalizer.T("Checking for updates…");
		SetState(UpdateUiState.Checking, checking);
		EmitBackgroundProgress(0, true, checking, "");

		CancelAndReset();
		_cts = new CancellationTokenSource();
		CancellationToken token = _cts.Token;
		string updatesDir = UpdateEndpoints.GetUpdatesDirectory();

		string skipped = _globalData?.UserDataManager?.SkippedUpdateVersion ?? string.Empty;

		_ = Task.Run(() => CheckWorkerAsync(force, includePrerelease, updatesDir, skipped, token));
	}

	/// <summary>Pool thread: fetches the feed and validates a cached download.</summary>
	private async Task CheckWorkerAsync(bool force, bool includePre, string updatesDir, string skipped, CancellationToken token)
	{
		try
		{
			UpdateFeed feed;
			using (var http = CancellationTokenSource.CreateLinkedTokenSource(token))
			{
				http.CancelAfter(CheckHttpTimeout);
				UpdateFeed stable = await FetchLatestJsonAsync(http.Token).ConfigureAwait(false);
				if (includePre)
				{
					UpdateFeed api = await FetchGitHubApiAsync(includePrerelease: true, http.Token).ConfigureAwait(false);
					feed = api != null && (stable == null || UpdateSemVer.IsNewer(api.Version, stable.Version))
						? api
						: stable;
				}
				else
				{
					feed = stable ?? await FetchGitHubApiAsync(includePrerelease: false, http.Token).ConfigureAwait(false);
				}
			}

			// Only the offered version's download is worth keeping.
			string keep = feed != null && feed.IsNewerThanRunning() &&
			              !string.Equals(feed.Version, skipped, StringComparison.OrdinalIgnoreCase)
				? UpdatePaths.For(updatesDir, feed.Version, "").VersionDir
				: null;
			DeleteDownloads(updatesDir, keep, includeBackup: false);

			// Hashing a cached zip is not bound to the HTTP time budget.
			bool extractReady = TryValidateReadyExtract(feed, updatesDir, out string extractDir, out var layout);
			string checkedAt = DateTime.UtcNow.ToString("o");
			RunOnMain(() => FinishCheck(force, feed, checkedAt, extractReady, extractDir, layout));
		}
		catch (OperationCanceledException)
		{
			// Not cancelled from outside, so the HTTP time budget ran out.
			bool timedOut = !token.IsCancellationRequested;
			RunOnMain(() =>
			{
				if (timedOut)
				{
					LastError = UiLocalizer.T("Could not check for updates: GitHub did not respond in time.");
					Log("Update check timed out.", (int)LogType.Warning);
				}

				if (timedOut && force)
					SetState(UpdateUiState.Error, LastError);
				else
					SetState(UpdateUiState.Idle, "");
				EmitBackgroundCompleted();
				_busy = false;
			});
		}
		catch (Exception ex)
		{
			GD.PrintErr($"UpdateService:CheckWorkerAsync - {ex.Message}");
			string detail = ex.Message;
			RunOnMain(() =>
			{
				LastError = UiLocalizer.T("Could not check for updates.");
				if (force)
					SetState(UpdateUiState.Error, LastError);
				else
					SetState(UpdateUiState.Idle, "");
				EmitBackgroundCompleted();
				Log($"Update check failed: {detail}", (int)LogType.Warning);
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
			Log(LastError, (int)LogType.Warning);
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
			_readyVersion = feed.Version;
			_readySha256 = feed.CurrentAsset?.Sha256 ?? string.Empty;
			SetState(UpdateUiState.ReadyToInstall,
				UiLocalizer.Tf("Cue2 {0} is downloaded and ready to install.", feed.Version));
			EmitAvailableFooter(feed.Version);
			_busy = false;
			return;
		}

		SetState(UpdateUiState.Available, UiLocalizer.Tf("Cue2 {0} is available.", feed.Version));
		EmitAvailableFooter(feed.Version);
		Log($"Update available: {feed.Version} ({UpdateEndpoints.CurrentPlatformKey()})", (int)LogType.Info);
		_busy = false;
	}

	/// <summary>
	/// Starts a download on a pool thread. Main thread only: paths and UI state are set here.
	/// </summary>
	private void StartDownload(UpdatePlatformAsset asset, string version)
	{
		_busy = true;
		CancelAndReset();
		_cts = new CancellationTokenSource();
		CancellationToken token = _cts.Token;
		UpdatePaths paths = UpdatePaths.For(UpdateEndpoints.GetUpdatesDirectory(), version, asset.Name);

		string status = DownloadingStatus(version);
		SetState(UpdateUiState.Downloading, status);
		EmitBackgroundProgress(0, true, status, asset.Name);

		_ = Task.Run(() => DownloadWorkerAsync(asset, version, paths, token));
	}

	/// <summary>Pool thread: download, verify, extract, find the layout.</summary>
	private async Task DownloadWorkerAsync(UpdatePlatformAsset asset, string version, UpdatePaths paths, CancellationToken token)
	{
		try
		{
			Directory.CreateDirectory(paths.VersionDir);
			await DownloadAndHashAsync(asset, paths.ArchivePath, version, token).ConfigureAwait(false);

			RunOnMain(() =>
			{
				string verifying = UiLocalizer.T("Verifying update…");
				SetState(UpdateUiState.Downloading, verifying);
				EmitBackgroundProgress(100, true, verifying, "");
			});

			UpdateApplyHelper.ExtractArchive(paths.ArchivePath, paths.ExtractDir, asset.Sha256);
			token.ThrowIfCancellationRequested();
			var layout = UpdateApplyHelper.FindLayout(paths.ExtractDir);
			if (layout == null)
			{
				TryDelete(paths.ArchivePath);
				UpdateApplyHelper.DeleteExtract(paths.ExtractDir);
			}

			string sha = asset.Sha256;
			RunOnMain(() => FinishDownload(version, sha, paths.ExtractDir, layout));
		}
		catch (OperationCanceledException)
		{
			TryDelete(paths.ArchivePath);
			UpdateApplyHelper.DeleteExtract(paths.ExtractDir);
			// Not cancelled by the user or by quitting, so the stall timeout fired.
			bool stalled = !token.IsCancellationRequested;
			RunOnMain(() =>
			{
				if (stalled)
				{
					LastError = UiLocalizer.T("The download stopped responding. Try again.");
					SetState(UpdateUiState.Error, LastError);
					EmitBackgroundCompleted();
					Log($"Update download of Cue2 {version} stalled and was stopped.", (int)LogType.Warning);
				}
				else
				{
					SetState(UpdateUiState.Available, UiLocalizer.T("Download cancelled."));
					EmitAvailableFooter(version);
				}

				_busy = false;
			});
		}
		catch (Exception ex)
		{
			GD.PrintErr($"UpdateService:DownloadWorkerAsync - {ex.Message}");
			TryDelete(paths.ArchivePath);
			UpdateApplyHelper.DeleteExtract(paths.ExtractDir);
			string detail = ex.Message;
			RunOnMain(() =>
			{
				LastError = UiLocalizer.T("Download or verification failed.");
				SetState(UpdateUiState.Error, LastError);
				EmitBackgroundCompleted();
				Log($"Update download failed: {detail}", (int)LogType.Error);
				_busy = false;
			});
		}
	}

	private void FinishDownload(string version, string sha256, string extractDir, UpdateApplyHelper.ExtractedLayout layout)
	{
		if (layout == null)
		{
			LastError = UiLocalizer.T("The downloaded update did not contain a Cue2 application.");
			SetState(UpdateUiState.Error, LastError);
			EmitBackgroundCompleted();
			_busy = false;
			return;
		}

		ReadyExtractDir = extractDir;
		ReadyLayout = layout;
		_readyVersion = version;
		_readySha256 = sha256;
		SetState(UpdateUiState.ReadyToInstall,
			UiLocalizer.Tf("Cue2 {0} is downloaded and ready to install.", version));
		EmitAvailableFooter(version);
		Log($"Update {version} downloaded and verified.", (int)LogType.Info);
		_busy = false;
	}

	private async Task<UpdateFeed> FetchLatestJsonAsync(CancellationToken token)
	{
		using var req = new SysHttpRequest(SysHttpMethod.Get, UpdateEndpoints.LatestJsonUrl);
		req.Headers.Accept.ParseAdd("application/json");
		using var resp = await Http.SendAsync(req, token).ConfigureAwait(false);
		if (!resp.IsSuccessStatusCode)
		{
			GD.Print($"UpdateService:FetchLatestJsonAsync - HTTP {(int)resp.StatusCode} from GitHub latest.json");
			return null;
		}

		string json = await resp.Content.ReadAsStringAsync(token).ConfigureAwait(false);
		return UpdateManifestParser.ParseLatestJson(json);
	}

	private async Task<UpdateFeed> FetchGitHubApiAsync(bool includePrerelease, CancellationToken token)
	{
		using var req = new SysHttpRequest(SysHttpMethod.Get, UpdateEndpoints.ReleasesApiUrl);
		req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
		req.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
		using var resp = await Http.SendAsync(req, token).ConfigureAwait(false);
		if (!resp.IsSuccessStatusCode)
		{
			GD.Print($"UpdateService:FetchGitHubApiAsync - HTTP {(int)resp.StatusCode}");
			return null;
		}

		string json = await resp.Content.ReadAsStringAsync(token).ConfigureAwait(false);
		return UpdateManifestParser.ParseGitHubReleases(json, includePrerelease);
	}

	/// <summary>Pool thread: streams the asset to <paramref name="destPath"/> and checks its SHA-256.</summary>
	private async Task DownloadAndHashAsync(UpdatePlatformAsset asset, string destPath, string version, CancellationToken token)
	{
		if (!UpdateEndpoints.IsAllowedDownloadUrl(asset.Url))
			throw new InvalidOperationException("Update URL must be HTTPS on GitHub.");

		// HttpClient.Timeout does not cover reading the body; this token is re-armed after every read.
		using var stall = CancellationTokenSource.CreateLinkedTokenSource(token);
		stall.CancelAfter(DownloadStallTimeout);

		using var req = new SysHttpRequest(SysHttpMethod.Get, asset.Url);
		req.Headers.Accept.ParseAdd("*/*");
		using var resp = await Http.SendAsync(req, SysHttpCompletion.ResponseHeadersRead, stall.Token).ConfigureAwait(false);
		resp.EnsureSuccessStatusCode();

		long total = resp.Content.Headers.ContentLength ?? asset.Size;
		if (asset.Size > 0 && total > 0 && Math.Abs(total - asset.Size) > 1024)
			throw new InvalidOperationException("Download size did not match the update manifest.");

		await using var httpStream = await resp.Content.ReadAsStreamAsync(stall.Token).ConfigureAwait(false);
		await using var fs = new FileStream(destPath, FileMode.Create, System.IO.FileAccess.Write, FileShare.None, HashBufferSize, true);
		using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

		var buffer = new byte[HashBufferSize];
		long copied = 0;
		long lastProgressMs = 0;
		string name = asset.Name;
		int read;
		while ((read = await httpStream.ReadAsync(buffer.AsMemory(0, buffer.Length), stall.Token).ConfigureAwait(false)) > 0)
		{
			stall.CancelAfter(DownloadStallTimeout);
			await fs.WriteAsync(buffer.AsMemory(0, read), stall.Token).ConfigureAwait(false);
			sha.AppendData(buffer.AsSpan(0, read));
			copied += read;
			if (total <= 0)
				continue;

			float pct = Math.Clamp(copied * 100f / total, 0, 99);
			long now = System.Environment.TickCount64;
			if (pct >= 99 || now - lastProgressMs >= ProgressIntervalMs)
			{
				lastProgressMs = now;
				RunOnMain(() => EmitBackgroundProgress(pct, true, DownloadingStatus(version), name));
			}
		}

		if (!HashMatches(Convert.ToHexString(sha.GetHashAndReset()), asset.Sha256))
			throw new InvalidOperationException("SHA-256 mismatch.");
	}

	/// <summary>
	/// Trusts a previous download only when the archive is still present, its SHA-256 matches the
	/// feed and its extract is complete. Pool thread only (hashes and may extract the archive).
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

		UpdatePaths paths = UpdatePaths.For(updatesDir, feed.Version, asset.Name);
		extractDir = paths.ExtractDir;
		if (!File.Exists(paths.ArchivePath) || !FileSha256Equals(paths.ArchivePath, asset.Sha256))
			return false;

		// A verified archive whose extract was cut off (quit or crash) is extracted again here.
		if (!UpdateApplyHelper.IsExtractComplete(paths.ExtractDir, asset.Sha256))
		{
			try
			{
				UpdateApplyHelper.ExtractArchive(paths.ArchivePath, paths.ExtractDir, asset.Sha256);
			}
			catch (Exception ex)
			{
				GD.PrintErr($"UpdateService:TryValidateReadyExtract - {ex.Message}");
				UpdateApplyHelper.DeleteExtract(paths.ExtractDir);
				return false;
			}
		}

		layout = UpdateApplyHelper.FindLayout(paths.ExtractDir);
		return layout != null;
	}

	private static bool FileSha256Equals(string path, string expectedHex)
	{
		try
		{
			using var fs = new FileStream(path, FileMode.Open, System.IO.FileAccess.Read, FileShare.Read, HashBufferSize);
			using var sha = SHA256.Create();
			return HashMatches(Convert.ToHexString(sha.ComputeHash(fs)), expectedHex);
		}
		catch (Exception)
		{
			return false;
		}
	}

	/// <summary>Case-insensitive compare of a hex digest with the feed value.</summary>
	private static bool HashMatches(string actualHex, string expectedHex) =>
		!string.IsNullOrWhiteSpace(expectedHex)
		&& string.Equals(actualHex, expectedHex.Trim(), StringComparison.OrdinalIgnoreCase);

	private static string DownloadingStatus(string version) =>
		UiLocalizer.Tf("Downloading Cue2 {0}…", version);

	/// <summary>Queues <paramref name="action"/> on the Godot main thread. Safe from any thread.</summary>
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
		StatusMessage = message ?? string.Empty;
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
		string status = UiLocalizer.Tf("Update available: Cue2 {0}", version);
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

	/// <summary>
	/// Free bytes on the drive holding <paramref name="dir"/>, or -1 when unknown.
	/// </summary>
	private static long GetFreeSpace(string dir)
	{
		try
		{
			string root = Path.GetPathRoot(Path.GetFullPath(dir));
			if (string.IsNullOrEmpty(root))
				return -1;
			if (!OperatingSystem.IsWindows())
			{
				// DriveInfo on Unix wants the mount point; walk up to the nearest existing folder.
				string probe = Path.GetFullPath(dir);
				while (!string.IsNullOrEmpty(probe) && !Directory.Exists(probe))
					probe = Path.GetDirectoryName(probe);
				root = string.IsNullOrEmpty(probe) ? root : probe;
			}

			return new DriveInfo(root).AvailableFreeSpace;
		}
		catch (Exception ex)
		{
			GD.PrintErr($"UpdateService:GetFreeSpace - {ex.Message}");
			return -1;
		}
	}

	/// <summary>
	/// Deletes downloaded version folders under <paramref name="updatesDir"/>, except
	/// <paramref name="keepVersionDir"/>. The install backup goes only with <paramref name="includeBackup"/>.
	/// Result files are left alone. Pool thread only.
	/// </summary>
	private static void DeleteDownloads(string updatesDir, string keepVersionDir, bool includeBackup)
	{
		try
		{
			if (string.IsNullOrEmpty(updatesDir) || !Directory.Exists(updatesDir))
				return;
			string keep = string.IsNullOrEmpty(keepVersionDir) ? null : Path.GetFullPath(keepVersionDir);
			StringComparison cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
			foreach (string dir in Directory.GetDirectories(updatesDir))
			{
				string name = Path.GetFileName(dir);
				if (string.Equals(name, UpdateApplyHelper.BackupDirName, cmp) && !includeBackup)
					continue;
				if (keep != null && string.Equals(Path.GetFullPath(dir), keep, cmp))
					continue;
				TryDeleteDir(dir);
			}
		}
		catch (Exception ex)
		{
			GD.PrintErr($"UpdateService:DeleteDownloads - {ex.Message}");
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

	/// <summary>Where one version's archive and extract live under <c>user://updates</c>.</summary>
	private readonly record struct UpdatePaths(string VersionDir, string ArchivePath, string ExtractDir)
	{
		public static UpdatePaths For(string updatesDir, string version, string assetName)
		{
			string versionDir = Path.Combine(updatesDir, Sanitize(version));
			return new UpdatePaths(
				versionDir,
				Path.Combine(versionDir, SanitizeFile(assetName)),
				Path.Combine(versionDir, "extracted"));
		}
	}
}
