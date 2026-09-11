// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System;
using Cue2.Services;
using Cue2.UI.Utilities;
using Godot;

namespace Cue2.UI.Popups;

/// <summary>
/// Modal dialog shown when a showfile's version does not match the running Cue2 build.
/// </summary>
/// <remarks>
/// Displayed <b>before</b> session reset / open so Cancel leaves the current show intact.
/// Follows the same modular pattern as <see cref="ResourceInUseDeleteDialog"/>:
/// <list type="number">
/// <item><see cref="Create"/> via SceneLoader</item>
/// <item><see cref="Configure"/></item>
/// <item>Parent.AddChild(dialog)</item>
/// <item><see cref="ShowConfigured"/> → PopupCentered</item>
/// </list>
/// </remarks>
public partial class VersionMismatchDialog : Window
{
	/// <summary>Scene path for loading via <see cref="SceneLoader"/>.</summary>
	public const string ScenePath = "res://src/UI/Popups/VersionMismatchDialog.tscn";

	/// <summary>Raised when the user chooses Attempt Open.</summary>
	public event Action AttemptOpen;

	/// <summary>Raised when the user cancels or closes the window.</summary>
	public event Action Cancelled;

	private GlobalData _globalData;
	private GlobalSignals _globalSignals;

	private Label _titleLabel;
	private Label _summaryLabel;
	private Label _backupLabel;
	private Button _cancelButton;
	private Button _attemptOpenButton;

	private bool _signalsConnected;

	/// <summary>
	/// Applies Linux native-window policy before the scene window enters the tree.
	/// </summary>
	public VersionMismatchDialog()
	{
		LinuxWindowEmbedPolicy.ApplyToAppWindow(this);
	}

	/// <inheritdoc />
	public override void _Ready()
	{
		_globalData = GetNode<GlobalData>("/root/GlobalData");
		_globalSignals = GetNode<GlobalSignals>("/root/GlobalSignals");

		GD.Print("VersionMismatchDialog:Loading VersionMismatchDialog");

		UiUtilities.RescaleWindow(this, _globalData.BaseDisplayScale);
		float userScale = _globalData.UserDataManager?.UiScale ?? UserDataManager.DefaultUiScale;
		UiUtilities.RescaleUi(this, userScale, _globalData.BaseDisplayScale);

		_globalSignals.UiScaleChanged += ScaleUi;

		ResolveNodes();
		ConnectUiSignals();
		UiLocalizer.LocalizeTree(this);
	}

	/// <inheritdoc />
	public override void _ExitTree()
	{
		if (_globalSignals != null)
			_globalSignals.UiScaleChanged -= ScaleUi;

		DisconnectUiSignals();
	}

	/// <summary>
	/// Instantiates the dialog scene.
	/// </summary>
	/// <param name="errorMessage">Load error if null is returned.</param>
	/// <returns>Dialog instance, or null on failure.</returns>
	public static VersionMismatchDialog Create(out string errorMessage)
	{
		var node = SceneLoader.LoadScene(ScenePath, out errorMessage);
		if (node is VersionMismatchDialog dialog)
			return dialog;

		if (node != null)
		{
			node.QueueFree();
			errorMessage = "Loaded scene is not a VersionMismatchDialog.";
		}
		else if (string.IsNullOrEmpty(errorMessage))
		{
			errorMessage = $"Failed to load {ScenePath}.";
		}

		return null;
	}

	/// <summary>
	/// Configures dialog text from the showfile path and version comparison.
	/// Safe before AddChild (scene instance children are available after Instantiate).
	/// </summary>
	/// <param name="filePath">Absolute path to the .c2 being opened.</param>
	/// <param name="fileVersion">Version metadata from the showfile.</param>
	public void Configure(string filePath, ShowfileVersionInfo fileVersion)
	{
		ResolveNodes();
		ConnectUiSignals();

		string fileName = string.IsNullOrEmpty(filePath)
			? "(unknown file)"
			: System.IO.Path.GetFileName(filePath);

		Title = UiLocalizer.T("Showfile Version Differs");
		if (_titleLabel != null)
			_titleLabel.Text = UiLocalizer.T("Showfile Version Differs");

		string fileDisplay = fileVersion.ToDisplayString();
		string appDisplay = $"{Cue2.Version.FullVersionString} (format {ShowfileFormat.CurrentFormatVersion})";

		var summary = new System.Text.StringBuilder();
		summary.AppendLine(UiLocalizer.Tf("\"{0}\" was saved with a different version of Cue2.", fileName));
		summary.AppendLine();
		summary.AppendLine(UiLocalizer.Tf("Showfile:  {0}", fileDisplay));
		summary.AppendLine(UiLocalizer.Tf("This app:  {0}", appDisplay));
		summary.AppendLine();

		if (fileVersion.IsLegacyOrUnknown)
		{
			summary.AppendLine(UiLocalizer.T(
				"This file has no version metadata (created before version tracking). " +
				"Cue2 will attempt to migrate it to the current format when opening."));
		}
		else if (fileVersion.IsOlderFormat)
		{
			summary.AppendLine(UiLocalizer.T(
				"The showfile format is older than this version of Cue2. " +
				"Opening will attempt to migrate the data to the current format."));
		}
		else if (fileVersion.IsNewerFormat)
		{
			summary.AppendLine(UiLocalizer.T(
				"The showfile format is newer than this version of Cue2 understands. " +
				"Opening may fail, drop settings, or behave unexpectedly. " +
				"Cue2 will not re-label the file as this version’s format, and will not " +
				"overwrite the original on Save — use Save As if you need a copy this app can own."));
		}
		else if (!fileVersion.MatchesCurrentApp)
		{
			// Informational only when dialog is shown for other reasons; same-format app
			// mismatches no longer open this dialog by themselves.
			summary.AppendLine(UiLocalizer.T(
				"The file format matches, but the app version differs. " +
				"Opening should usually work; review the show after load."));
		}
		else
		{
			summary.AppendLine(UiLocalizer.T("Version details differ; proceed with care."));
		}

		if (_summaryLabel != null)
			_summaryLabel.Text = summary.ToString().TrimEnd();

		if (_backupLabel != null)
		{
			_backupLabel.Text = UiLocalizer.T(
				"Before opening, make a backup copy of this showfile (.c2) and its media folders " +
				"(Audio, Video, Images, Waveforms, Backups). Opening or migrating can change the file when you save.");
		}
	}

	/// <summary>
	/// Shows the configured popup centered.
	/// </summary>
	public void ShowConfigured()
	{
		PopupCentered();
	}

	private void ResolveNodes()
	{
		_titleLabel ??= GetNodeOrNull<Label>("%TitleLabel");
		_summaryLabel ??= GetNodeOrNull<Label>("%SummaryLabel");
		_backupLabel ??= GetNodeOrNull<Label>("%BackupLabel");
		_cancelButton ??= GetNodeOrNull<Button>("%CancelButton");
		_attemptOpenButton ??= GetNodeOrNull<Button>("%AttemptOpenButton");
	}

	private void ConnectUiSignals()
	{
		if (_signalsConnected)
			return;

		ResolveNodes();
		if (_cancelButton == null || _attemptOpenButton == null)
			return;

		_cancelButton.Pressed += OnCancelPressed;
		_attemptOpenButton.Pressed += OnAttemptOpenPressed;
		CloseRequested += OnCancelPressed;
		_signalsConnected = true;
	}

	private void DisconnectUiSignals()
	{
		if (!_signalsConnected)
			return;

		if (_cancelButton != null) _cancelButton.Pressed -= OnCancelPressed;
		if (_attemptOpenButton != null) _attemptOpenButton.Pressed -= OnAttemptOpenPressed;
		CloseRequested -= OnCancelPressed;
		_signalsConnected = false;
	}

	private void ScaleUi(float value)
	{
		try
		{
			float effectiveScale = value * _globalData.BaseDisplayScale;
			WrapControls = true;
			ContentScaleFactor = effectiveScale;
			ChildControlsChanged();
			GD.Print($"VersionMismatchDialog:ScaleUi - Applied effective UI scale: {effectiveScale}");
		}
		catch (Exception ex)
		{
			_globalSignals?.EmitSignal(nameof(GlobalSignals.Log),
				$"Error applying UI scale: {ex.Message}", (int)LogType.Warning);
		}
	}

	private void OnCancelPressed()
	{
		Cancelled?.Invoke();
		Hide();
		QueueFree();
	}

	private void OnAttemptOpenPressed()
	{
		AttemptOpen?.Invoke();
		Hide();
		QueueFree();
	}
}
