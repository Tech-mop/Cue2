// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System;
using Cue2.Services;
using Cue2.UI.Utilities;
using Godot;
using static Cue2.UI.Utilities.UiLocalizer;

namespace Cue2.UI.Settings;

/// <summary>
/// Cue2 Preferences → Updates: check GitHub Releases, download, and Install and Restart.
/// </summary>
public partial class SettingsUpdates : ScrollContainer
{
	/// <summary>Stable Settings tree key (English, persisted in user data).</summary>
	public const string MenuKey = "Updates";

	private GlobalData _globalData;
	private GlobalSignals _globalSignals;
	private bool _syncing;

	private Label _currentVersionLabel;
	private Label _lastCheckLabel;
	private Label _statusLabel;
	private Label _latestVersionLabel;
	private TextEdit _notesEdit;
	private CheckBox _checkOnStartupCheck;
	private CheckBox _includePrereleaseCheck;
	private Button _checkNowButton;
	private Button _openReleasePageButton;
	private Button _skipVersionButton;
	private Button _downloadButton;
	private Button _installRestartButton;
	private ConfirmationDialog _installConfirmDialog;

	/// <inheritdoc />
	public override void _Ready()
	{
		_globalData = GetNode<GlobalData>("/root/GlobalData");
		_globalSignals = GetNode<GlobalSignals>("/root/GlobalSignals");

		_currentVersionLabel = GetNode<Label>("%CurrentVersionLabel");
		_lastCheckLabel = GetNode<Label>("%LastCheckLabel");
		_statusLabel = GetNode<Label>("%StatusLabel");
		_latestVersionLabel = GetNode<Label>("%LatestVersionLabel");
		_notesEdit = GetNode<TextEdit>("%NotesEdit");
		_checkOnStartupCheck = GetNode<CheckBox>("%CheckOnStartupCheck");
		_includePrereleaseCheck = GetNode<CheckBox>("%IncludePrereleaseCheck");
		_checkNowButton = GetNode<Button>("%CheckNowButton");
		_openReleasePageButton = GetNode<Button>("%OpenReleasePageButton");
		_skipVersionButton = GetNode<Button>("%SkipVersionButton");
		_downloadButton = GetNode<Button>("%DownloadButton");
		_installRestartButton = GetNode<Button>("%InstallRestartButton");
		_installConfirmDialog = GetNode<ConfirmationDialog>("%InstallConfirmDialog");

		_checkOnStartupCheck.Toggled += OnCheckOnStartupToggled;
		_includePrereleaseCheck.Toggled += OnIncludePrereleaseToggled;
		_checkNowButton.Pressed += OnCheckNowPressed;
		_openReleasePageButton.Pressed += OnOpenReleasePagePressed;
		_skipVersionButton.Pressed += OnSkipVersionPressed;
		_downloadButton.Pressed += OnDownloadPressed;
		_installRestartButton.Pressed += OnInstallPressed;
		_installConfirmDialog.Confirmed += OnInstallConfirmed;
		LinuxWindowEmbedPolicy.ApplyToAppWindow(_installConfirmDialog);

		if (_globalSignals != null)
		{
			_globalSignals.UpdateUiStateChanged += OnUpdateUiStateChanged;
			_globalSignals.LocaleChanged += OnLocaleChanged;
		}

		_notesEdit.SetMeta(MetaSkip, true);
		LocalizeTree(this);
		SyncFromService();
	}

	/// <inheritdoc />
	public override void _ExitTree()
	{
		if (_globalSignals != null)
		{
			_globalSignals.UpdateUiStateChanged -= OnUpdateUiStateChanged;
			_globalSignals.LocaleChanged -= OnLocaleChanged;
		}

		if (_checkOnStartupCheck != null)
			_checkOnStartupCheck.Toggled -= OnCheckOnStartupToggled;
		if (_includePrereleaseCheck != null)
			_includePrereleaseCheck.Toggled -= OnIncludePrereleaseToggled;
		if (_checkNowButton != null)
			_checkNowButton.Pressed -= OnCheckNowPressed;
		if (_openReleasePageButton != null)
			_openReleasePageButton.Pressed -= OnOpenReleasePagePressed;
		if (_skipVersionButton != null)
			_skipVersionButton.Pressed -= OnSkipVersionPressed;
		if (_downloadButton != null)
			_downloadButton.Pressed -= OnDownloadPressed;
		if (_installRestartButton != null)
			_installRestartButton.Pressed -= OnInstallPressed;
		if (_installConfirmDialog != null)
			_installConfirmDialog.Confirmed -= OnInstallConfirmed;
	}

	private void OnLocaleChanged(string localeCode)
	{
		if (!GodotObject.IsInstanceValid(this))
			return;
		LocalizeTree(this);
		SyncFromService();
	}

	private void OnUpdateUiStateChanged(int state, string message)
	{
		SyncFromService(message);
	}

	private void SyncFromService(string statusOverride = null)
	{
		var udm = _globalData?.UserDataManager;
		var svc = _globalData?.UpdateService;
		_syncing = true;
		try
		{
			_currentVersionLabel.Text = $"Cue2 {Version.FullVersionString}";
			_lastCheckLabel.Text = FormatLastCheck(udm?.LastUpdateCheckUtc);
			if (udm != null)
			{
				_checkOnStartupCheck.SetPressedNoSignal(udm.CheckForUpdatesOnStartup);
				_includePrereleaseCheck.SetPressedNoSignal(udm.IncludePrereleaseUpdates);
			}

			var feed = svc?.LastFeed;
			if (feed != null && !string.IsNullOrEmpty(feed.Version))
			{
				_latestVersionLabel.Text = string.IsNullOrEmpty(feed.Tag) ? feed.Version : feed.Tag;
				_notesEdit.Text = string.IsNullOrWhiteSpace(feed.Notes)
					? (feed.NotesUrl ?? "")
					: feed.Notes;
			}
			else
			{
				_latestVersionLabel.Text = "—";
				_notesEdit.Text = "";
			}

			if (!string.IsNullOrEmpty(statusOverride))
				_statusLabel.Text = statusOverride;
			else if (svc != null && !string.IsNullOrEmpty(svc.LastError) && svc.UiState == UpdateUiState.Error)
				_statusLabel.Text = svc.LastError;
			else if (svc != null)
				_statusLabel.Text = StatusFor(svc.UiState, feed);
			else
				_statusLabel.Text = "";

			if (UpdateService.IsEditorBuild)
			{
				_statusLabel.Text = T("Update checks run in exported builds only.");
				_checkNowButton.Disabled = true;
				_downloadButton.Disabled = true;
				_installRestartButton.Disabled = true;
				_skipVersionButton.Disabled = true;
			}
			else
			{
				_checkNowButton.Disabled = false;
				bool available = svc?.UiState is UpdateUiState.Available or UpdateUiState.ReadyToInstall;
				bool hasAsset = feed?.CurrentAsset != null && !string.IsNullOrWhiteSpace(feed.CurrentAsset.Url);
				_downloadButton.Disabled = svc?.UiState != UpdateUiState.Available || !hasAsset;
				_installRestartButton.Disabled = svc?.UiState != UpdateUiState.ReadyToInstall;
				_skipVersionButton.Disabled = !available;
			}
		}
		finally
		{
			_syncing = false;
		}
	}

	private static string StatusFor(UpdateUiState state, UpdateFeed feed)
	{
		return state switch
		{
			UpdateUiState.Checking => T("Checking for updates…"),
			UpdateUiState.UpToDate => T("Cue2 is up to date."),
			UpdateUiState.Available => feed != null
				? Tf("Cue2 {0} is available.", feed.Version)
				: T("An update is available."),
			UpdateUiState.Downloading => T("Downloading…"),
			UpdateUiState.ReadyToInstall => feed != null
				? Tf("Cue2 {0} is downloaded and ready to install.", feed.Version)
				: T("Ready to install."),
			UpdateUiState.Applying => T("Installing update…"),
			UpdateUiState.Error => T("Could not check for updates."),
			_ => ""
		};
	}

	private static string FormatLastCheck(string iso)
	{
		if (string.IsNullOrWhiteSpace(iso))
			return T("Never");
		if (DateTime.TryParse(iso, out var dt))
			return dt.ToLocalTime().ToString("g");
		return iso;
	}

	private void OnCheckOnStartupToggled(bool on)
	{
		if (_syncing)
			return;
		var udm = _globalData?.UserDataManager;
		if (udm != null)
			udm.CheckForUpdatesOnStartup = on;
	}

	private void OnIncludePrereleaseToggled(bool on)
	{
		if (_syncing)
			return;
		var udm = _globalData?.UserDataManager;
		if (udm != null)
			udm.IncludePrereleaseUpdates = on;
	}

	private void OnCheckNowPressed()
	{
		_globalData?.UpdateService?.CheckForUpdates(force: true);
	}

	private void OnOpenReleasePagePressed()
	{
		_globalData?.UpdateService?.OpenReleasePage();
	}

	private void OnSkipVersionPressed()
	{
		_globalData?.UpdateService?.SkipCurrentVersion();
	}

	private void OnDownloadPressed()
	{
		_globalData?.UpdateService?.DownloadUpdate();
	}

	private void OnInstallPressed()
	{
		var svc = _globalData?.UpdateService;
		if (svc == null)
			return;

		var exec = _globalData.CueCommandExecutor;
		if (exec?.ActiveCues != null && exec.ActiveCues.Count > 0)
		{
			_statusLabel.Text = T("Stop all playing cues before installing the update.");
			return;
		}

		_installConfirmDialog.PopupCentered();
	}

	private void OnInstallConfirmed()
	{
		_globalData?.UpdateService?.RequestApplyAndRelaunch();
	}
}
