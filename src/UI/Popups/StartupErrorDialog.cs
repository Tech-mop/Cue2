// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Text;
using Cue2.Services;
using Cue2.UI.Utilities;
using Godot;

namespace Cue2.UI.Popups;

/// <summary>
/// Modal overlay shown when a system dependency failed while Cue2 was starting.
/// </summary>
/// <remarks>
/// Same <see cref="Control"/> overlay pattern as <see cref="UnsavedChangesDialog"/>: a child
/// <see cref="Window"/> from Quit / CloseRequested paints blank on Windows and can freeze
/// exported macOS builds. Drawing in the operator viewport avoids both.
/// </remarks>
public partial class StartupErrorDialog : Control
{
	/// <summary>Scene path for <see cref="SceneLoader"/>.</summary>
	public const string ScenePath = "res://src/UI/Popups/StartupErrorDialog.tscn";

	/// <summary>Raised when the user chooses Restart (relaunch Cue2).</summary>
	public event Action RestartRequested;

	/// <summary>Raised when the user chooses Continue.</summary>
	public event Action ContinueRequested;

	private GlobalSignals _globalSignals;

	private Label _titleLabel;
	private Label _bodyLabel;
	private Label _detailsLabel;
	private Button _continueButton;
	private Button _restartButton;

	private bool _signalsConnected;
	private bool _choiceMade;
	private IReadOnlyList<string> _affectedFeatures = Array.Empty<string>();
	private IReadOnlyList<StartupFault> _faults = Array.Empty<StartupFault>();

	/// <summary>
	/// True after Restart or Continue. False when the overlay is QueueFreed without a button.
	/// </summary>
	public bool ChoiceMade => _choiceMade;

	/// <inheritdoc />
	public override void _Ready()
	{
		_globalSignals = GetNodeOrNull<GlobalSignals>("/root/GlobalSignals");

		GD.Print("StartupErrorDialog:Loading StartupErrorDialog");

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

		OnContinuePressed();
		AcceptEvent();
	}

	/// <summary>
	/// Instantiates the dialog scene.
	/// </summary>
	/// <param name="errorMessage">Load error if null is returned.</param>
	/// <returns>Dialog instance, or null on failure.</returns>
	public static StartupErrorDialog Create(out string errorMessage)
	{
		var node = SceneLoader.LoadScene(ScenePath, out errorMessage);
		if (node is StartupErrorDialog dialog)
			return dialog;

		if (node != null)
		{
			node.QueueFree();
			errorMessage = "Loaded scene is not a StartupErrorDialog.";
		}
		else if (string.IsNullOrEmpty(errorMessage))
		{
			errorMessage = $"Failed to load {ScenePath}.";
		}

		return null;
	}

	/// <summary>
	/// Sets title, affected features, and technical details (call before show).
	/// </summary>
	/// <param name="faults">Recorded boot faults.</param>
	/// <param name="affectedFeatures">Unique capability labels in first-seen order.</param>
	public void Configure(IReadOnlyList<StartupFault> faults, IReadOnlyList<string> affectedFeatures)
	{
		_faults = faults ?? Array.Empty<StartupFault>();
		_affectedFeatures = affectedFeatures ?? Array.Empty<string>();
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
		_continueButton?.GrabFocus();
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
		if (_titleLabel != null)
			_titleLabel.Text = UiLocalizer.T("Startup error");

		if (_bodyLabel != null)
			_bodyLabel.Text = BuildBodyText();

		if (_detailsLabel != null)
		{
			string details = BuildDetailsText();
			_detailsLabel.Text = details;
			_detailsLabel.Visible = !string.IsNullOrEmpty(details);
		}

		if (_continueButton != null)
			_continueButton.Text = UiLocalizer.T("Continue");
		if (_restartButton != null)
			_restartButton.Text = UiLocalizer.T("Restart");
	}

	private string BuildBodyText()
	{
		var sb = new StringBuilder();
		sb.Append(UiLocalizer.T("An error occurred during startup that will affect:"));
		sb.AppendLine();

		if (_affectedFeatures.Count == 0)
		{
			sb.AppendLine();
			sb.Append("• ");
			sb.Append(UiLocalizer.T("Some Cue2 features"));
		}
		else
		{
			for (int i = 0; i < _affectedFeatures.Count; i++)
			{
				sb.AppendLine();
				sb.Append("• ");
				sb.Append(UiLocalizer.T(_affectedFeatures[i]));
			}
		}

		sb.AppendLine();
		sb.AppendLine();
		sb.Append(UiLocalizer.T(
			"You can restart Cue2 to try again, or continue with these features unavailable."));
		return sb.ToString();
	}

	private string BuildDetailsText()
	{
		if (_faults.Count == 0)
			return string.Empty;

		var sb = new StringBuilder();
		for (int i = 0; i < _faults.Count; i++)
		{
			StartupFault fault = _faults[i];
			string subsystem = string.IsNullOrEmpty(fault.Subsystem)
				? fault.Id
				: fault.Subsystem;
			if (string.IsNullOrEmpty(fault.Detail))
				sb.Append(subsystem);
			else
				sb.Append(UiLocalizer.Tf("{0}: {1}", subsystem, fault.Detail));
			if (i < _faults.Count - 1)
				sb.AppendLine();
		}

		return sb.ToString();
	}

	private void ResolveNodes()
	{
		_titleLabel ??= GetNodeOrNull<Label>("%TitleLabel");
		_bodyLabel ??= GetNodeOrNull<Label>("%BodyLabel");
		_detailsLabel ??= GetNodeOrNull<Label>("%DetailsLabel");
		_continueButton ??= GetNodeOrNull<Button>("%ContinueButton");
		_restartButton ??= GetNodeOrNull<Button>("%RestartButton");
	}

	private void ConnectUiSignals()
	{
		if (_signalsConnected)
			return;

		ResolveNodes();
		if (_continueButton == null || _restartButton == null)
			return;

		_continueButton.Pressed += OnContinuePressed;
		_restartButton.Pressed += OnRestartPressed;
		_signalsConnected = true;
	}

	private void DisconnectUiSignals()
	{
		if (!_signalsConnected)
			return;

		if (_continueButton != null)
			_continueButton.Pressed -= OnContinuePressed;
		if (_restartButton != null)
			_restartButton.Pressed -= OnRestartPressed;
		_signalsConnected = false;
	}

	private void OnContinuePressed()
	{
		FinishChoice(ContinueRequested);
	}

	private void OnRestartPressed()
	{
		FinishChoice(RestartRequested);
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
