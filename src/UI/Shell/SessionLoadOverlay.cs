// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using Cue2.Services;
using Cue2.UI.Utilities;
using Godot;

namespace Cue2.UI.Shell;

/// <summary>
/// Workspace overlay shown while a showfile is being applied (startup or File → Open).
/// Title bar stays usable; GO and document edits are gated until the load finishes.
/// </summary>
public partial class SessionLoadOverlay : Control
{
	private GlobalSignals _globalSignals;
	private Label _titleLabel;
	private ProgressBar _progressBar;
	private Label _statusLabel;
	private Label _detailLabel;

	private string _showName = string.Empty;
	private string _statusKey = "Reading showfile…";
	private string _detailText = string.Empty;

	/// <inheritdoc />
	public override void _Ready()
	{
		_globalSignals = GetNodeOrNull<GlobalSignals>("/root/GlobalSignals");
		_titleLabel = GetNodeOrNull<Label>("%TitleLabel");
		_progressBar = GetNodeOrNull<ProgressBar>("%ProgressBar");
		_statusLabel = GetNodeOrNull<Label>("%StatusLabel");
		_detailLabel = GetNodeOrNull<Label>("%DetailLabel");

		Visible = false;
		MouseFilter = MouseFilterEnum.Stop;

		if (_progressBar != null)
		{
			_progressBar.MinValue = 0;
			_progressBar.MaxValue = 100;
			_progressBar.Value = 0;
			_progressBar.ShowPercentage = false;
		}

		UiLocalizer.LocalizeTree(this);

		if (_globalSignals != null)
		{
			_globalSignals.SessionLoadStarted += OnSessionLoadStarted;
			_globalSignals.SessionLoadProgress += OnSessionLoadProgress;
			_globalSignals.SessionLoadFinished += OnSessionLoadFinished;
			_globalSignals.LocaleChanged += OnLocaleChanged;
		}
	}

	/// <inheritdoc />
	public override void _ExitTree()
	{
		if (_globalSignals == null)
			return;

		_globalSignals.SessionLoadStarted -= OnSessionLoadStarted;
		_globalSignals.SessionLoadProgress -= OnSessionLoadProgress;
		_globalSignals.SessionLoadFinished -= OnSessionLoadFinished;
		_globalSignals.LocaleChanged -= OnLocaleChanged;
	}

	/// <summary>
	/// Shows the overlay immediately (used at startup so the grey workspace never paints empty).
	/// </summary>
	/// <param name="showName">Show file name without extension.</param>
	public void ShowOpening(string showName)
	{
		OnSessionLoadStarted(showName ?? string.Empty);
		OnSessionLoadProgress(0f, "Reading showfile…", string.Empty, 0, 0);
	}

	private void OnSessionLoadStarted(string showName)
	{
		_showName = showName ?? string.Empty;
		_statusKey = "Reading showfile…";
		_detailText = string.Empty;
		if (_progressBar != null)
			_progressBar.Value = 0;
		RefreshTexts();
		Visible = true;
		// WindowSetMode on macOS can flush a process frame while Cue2Base is still
		// inside child _Ready; move_child is illegal then. Raise after setup finishes.
		CallDeferred(CanvasItem.MethodName.MoveToFront);
	}

	private void OnSessionLoadProgress(float percent, string statusText, string detail, int completed, int total)
	{
		if (!Visible)
			Visible = true;

		if (!string.IsNullOrEmpty(statusText))
			_statusKey = statusText;
		_detailText = detail ?? string.Empty;

		if (_progressBar != null)
			_progressBar.Value = Mathf.Clamp(percent, 0f, 100f);

		if (total > 0 && string.IsNullOrEmpty(_detailText))
			_detailText = $"{completed}/{total}";

		RefreshTexts();
	}

	private void OnSessionLoadFinished()
	{
		Visible = false;
		_showName = string.Empty;
		_detailText = string.Empty;
		if (_progressBar != null)
			_progressBar.Value = 0;
	}

	private void OnLocaleChanged(string localeCode)
	{
		if (!GodotObject.IsInstanceValid(this))
			return;
		UiLocalizer.LocalizeTree(this);
		RefreshTexts();
	}

	private void RefreshTexts()
	{
		if (_titleLabel != null)
		{
			_titleLabel.Text = string.IsNullOrEmpty(_showName)
				? UiLocalizer.T("Opening show…")
				: UiLocalizer.Tf("Opening {0}…", _showName);
		}

		if (_statusLabel != null)
			_statusLabel.Text = UiLocalizer.T(_statusKey);

		if (_detailLabel != null)
		{
			_detailLabel.Text = _detailText;
			_detailLabel.Visible = !string.IsNullOrEmpty(_detailText);
		}
	}
}
