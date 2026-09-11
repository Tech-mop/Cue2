// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System;
using Cue2.Services;
using Cue2.UI.Utilities;
using Godot;

namespace Cue2.UI.Popups;

/// <summary>
/// Modal prompt when closing or replacing a session that has unsaved document changes,
/// or when quitting while a footer background task is still running.
/// </summary>
/// <remarks>
/// Implemented as a <see cref="Control"/> overlay in the operator window (same pattern as
/// the session-load card), not a native <see cref="Window"/>. A child Window created from
/// Quit / CloseRequested paints as a blank rectangle on Windows and an exclusive native
/// Window freezes exported macOS builds. Drawing in the existing viewport avoids both.
/// </remarks>
public partial class UnsavedChangesDialog : Control
{
	private enum PromptMode
	{
		UnsavedDocument,
		BackgroundTask
	}

	/// <summary>Scene path for <see cref="SceneLoader"/>.</summary>
	public const string ScenePath = "res://src/UI/Popups/UnsavedChangesDialog.tscn";

	/// <summary>Raised when the user chooses Save &amp; close.</summary>
	public event Action SaveAndClose;

	/// <summary>Raised when the user chooses Close (discard changes).</summary>
	public event Action DiscardAndClose;

	/// <summary>Raised when the user cancels.</summary>
	public event Action Cancelled;

	private GlobalSignals _globalSignals;

	private Label _titleLabel;
	private Label _bodyLabel;
	private Button _cancelButton;
	private Button _closeButton;
	private Button _saveCloseButton;

	private bool _signalsConnected;
	private bool _choiceMade;
	private string _sessionLabel;
	private string _taskName;
	private PromptMode _mode = PromptMode.UnsavedDocument;

	/// <summary>
	/// True after Save &amp; close, Close, or Cancel. False when the overlay is QueueFreed
	/// without a button (should not happen for this overlay).
	/// </summary>
	public bool ChoiceMade => _choiceMade;

	/// <inheritdoc />
	public override void _Ready()
	{
		_globalSignals = GetNodeOrNull<GlobalSignals>("/root/GlobalSignals");

		GD.Print("UnsavedChangesDialog:Loading UnsavedChangesDialog");

		MouseFilter = MouseFilterEnum.Stop;
		SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);

		if (_globalSignals != null)
			_globalSignals.LocaleChanged += OnLocaleChanged;

		ResolveNodes();
		ConnectUiSignals();
		UiLocalizer.LocalizeTree(this);
		ApplyLocalizedCopy();
	}

	/// <inheritdoc />
	public override void _ExitTree()
	{
		if (_globalSignals != null)
			_globalSignals.LocaleChanged -= OnLocaleChanged;

		DisconnectUiSignals();
	}

	/// <inheritdoc />
	public override void _UnhandledKeyInput(InputEvent @event)
	{
		if (!Visible || _choiceMade)
			return;
		if (@event is not InputEventKey { Pressed: true, Echo: false })
			return;
		if (!@event.IsActionPressed("ui_cancel"))
			return;

		OnCancelPressed();
		AcceptEvent();
	}

	/// <summary>
	/// Instantiates the dialog scene.
	/// </summary>
	public static UnsavedChangesDialog Create(out string errorMessage)
	{
		var node = SceneLoader.LoadScene(ScenePath, out errorMessage);
		if (node is UnsavedChangesDialog dialog)
			return dialog;

		if (node != null)
		{
			node.QueueFree();
			errorMessage = "Loaded scene is not an UnsavedChangesDialog.";
		}
		else if (string.IsNullOrEmpty(errorMessage))
		{
			errorMessage = $"Failed to load {ScenePath}.";
		}

		return null;
	}

	/// <summary>
	/// Sets title/body from the current session name (call before show).
	/// </summary>
	/// <param name="sessionLabel">Show name or empty for an unsaved session.</param>
	public void Configure(string sessionLabel)
	{
		_mode = PromptMode.UnsavedDocument;
		_sessionLabel = sessionLabel;
		_taskName = null;
		ResolveNodes();
		ConnectUiSignals();
		ApplyLocalizedCopy();
	}

	/// <summary>
	/// Two-button quit prompt while a footer-tracked background task is running.
	/// Cancel keeps Cue2 open; Close becomes Quit Anyway (Save &amp; close is hidden).
	/// </summary>
	/// <param name="taskName">Short task label from the footer progress bar (already stripped of %).</param>
	public void ConfigureBackgroundTask(string taskName)
	{
		_mode = PromptMode.BackgroundTask;
		_taskName = string.IsNullOrWhiteSpace(taskName)
			? UiLocalizer.T("Background process")
			: taskName.Trim();
		_sessionLabel = null;
		ResolveNodes();
		ConnectUiSignals();
		ApplyLocalizedCopy();
	}

	/// <summary>Shows the overlay on top of the operator window.</summary>
	public void ShowConfigured()
	{
		Visible = true;
		MouseFilter = MouseFilterEnum.Stop;
		SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
		MoveToFront();
		if (_mode == PromptMode.BackgroundTask)
			_cancelButton?.GrabFocus();
		else
			_saveCloseButton?.GrabFocus();
	}

	private void OnLocaleChanged(string localeCode)
	{
		if (!GodotObject.IsInstanceValid(this))
			return;
		UiLocalizer.LocalizeTree(this);
		ApplyLocalizedCopy();
	}

	private void ApplyLocalizedCopy()
	{
		if (_cancelButton != null)
			_cancelButton.Text = UiLocalizer.T("Cancel");

		if (_mode == PromptMode.BackgroundTask)
		{
			if (_titleLabel != null)
				_titleLabel.Text = UiLocalizer.T("Task in progress");
			if (_bodyLabel != null)
			{
				string name = string.IsNullOrWhiteSpace(_taskName)
					? UiLocalizer.T("Background process")
					: _taskName.Trim();
				_bodyLabel.Text = UiLocalizer.Tf(
					"Background task in progress: {0}. Quitting could interrupt this task.",
					name);
			}

			if (_closeButton != null)
				_closeButton.Text = UiLocalizer.T("Quit Anyway");
			if (_saveCloseButton != null)
			{
				_saveCloseButton.Visible = false;
				_saveCloseButton.Disabled = true;
			}

			return;
		}

		if (_titleLabel != null)
			_titleLabel.Text = UiLocalizer.T("Unsaved Changes");

		string sessionName = string.IsNullOrWhiteSpace(_sessionLabel)
			? UiLocalizer.T("Untitled")
			: _sessionLabel.Trim();
		if (_bodyLabel != null)
			_bodyLabel.Text = UiLocalizer.Tf(
				"\"{0}\" has unsaved changes. Save before closing this session?",
				sessionName);

		if (_closeButton != null)
			_closeButton.Text = UiLocalizer.T("Close");
		if (_saveCloseButton != null)
		{
			_saveCloseButton.Visible = true;
			_saveCloseButton.Disabled = false;
			_saveCloseButton.Text = UiLocalizer.T("Save & close");
		}
	}

	private void ResolveNodes()
	{
		_titleLabel ??= GetNodeOrNull<Label>("%TitleLabel");
		_bodyLabel ??= GetNodeOrNull<Label>("%BodyLabel");
		_cancelButton ??= GetNodeOrNull<Button>("%CancelButton");
		_closeButton ??= GetNodeOrNull<Button>("%CloseButton");
		_saveCloseButton ??= GetNodeOrNull<Button>("%SaveCloseButton");
	}

	private void ConnectUiSignals()
	{
		if (_signalsConnected)
			return;

		ResolveNodes();
		if (_cancelButton == null || _closeButton == null || _saveCloseButton == null)
			return;

		_cancelButton.Pressed += OnCancelPressed;
		_closeButton.Pressed += OnClosePressed;
		_saveCloseButton.Pressed += OnSaveClosePressed;
		_signalsConnected = true;
	}

	private void DisconnectUiSignals()
	{
		if (!_signalsConnected)
			return;

		if (_cancelButton != null)
			_cancelButton.Pressed -= OnCancelPressed;
		if (_closeButton != null)
			_closeButton.Pressed -= OnClosePressed;
		if (_saveCloseButton != null)
			_saveCloseButton.Pressed -= OnSaveClosePressed;
		_signalsConnected = false;
	}

	private void OnCancelPressed()
	{
		FinishChoice(Cancelled);
	}

	private void OnClosePressed()
	{
		FinishChoice(DiscardAndClose);
	}

	private void OnSaveClosePressed()
	{
		FinishChoice(SaveAndClose);
	}

	/// <summary>
	/// Hides the overlay before the handler runs so Quit is never issued from a nested
	/// native window (deadlocks on macOS exported builds).
	/// </summary>
	private void FinishChoice(Action handler)
	{
		if (_choiceMade)
			return;
		_choiceMade = true;
		Hide();
		handler?.Invoke();
		QueueFree();
	}
}
