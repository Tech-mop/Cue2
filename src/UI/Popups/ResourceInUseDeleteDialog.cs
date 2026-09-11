// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Linq;
using Cue2.Domain.Cues;
using Cue2.Services;
using Cue2.UI.Utilities;
using Godot;

namespace Cue2.UI.Popups;

/// <summary>
/// Action chosen when deleting a resource that is still referenced by cues.
/// </summary>
public enum ResourceInUseDeleteAction
{
	/// <summary>Abort the delete.</summary>
	Cancel = 0,

	/// <summary>Delete resource and clear assignments on using cues.</summary>
	Unassign = 1,

	/// <summary>Delete resource and re-point using cues at another resource.</summary>
	Replace = 2
}

/// <summary>
/// Result returned when the user confirms a resource-in-use delete dialog.
/// </summary>
public sealed class ResourceInUseDeleteResult
{
	/// <summary>Chosen action.</summary>
	public ResourceInUseDeleteAction Action { get; init; }

	/// <summary>
	/// Replacement resource id when <see cref="Action"/> is <see cref="ResourceInUseDeleteAction.Replace"/>;
	/// otherwise -1.
	/// </summary>
	public int ReplaceWithId { get; init; } = -1;
}

/// <summary>
/// Modal dialog shown when deleting an audio patch or video target layer that is still used by cues.
/// Offers Cancel, Unassign, or Replace-with-alternative.
/// </summary>
/// <remarks>
/// Follows the same modular pattern as <see cref="FileDropPopup"/>:
/// <list type="number">
/// <item><see cref="Create"/> via SceneLoader</item>
/// <item><see cref="Configure"/> (works on instantiated scene children before AddChild)</item>
/// <item>Parent.AddChild(dialog)</item>
/// <item><see cref="ShowConfigured"/> → PopupCentered</item>
/// </list>
/// Window flags match FileDropPopup (borderless scene); do not combine Transient + AlwaysOnTop.
/// </remarks>
public partial class ResourceInUseDeleteDialog : Window
{
	/// <summary>Scene UID for loading via <see cref="SceneLoader"/> (matches FileDropPopup pattern).</summary>
	public const string SceneUid = "uid://e6771x0vumw2";

	/// <summary>Raised when the user confirms Unassign or Replace.</summary>
	public event Action<ResourceInUseDeleteResult> Confirmed;

	/// <summary>Raised when the user cancels.</summary>
	public event Action Cancelled;

	private GlobalData _globalData;
	private GlobalSignals _globalSignals;

	private Label _titleLabel;
	private Label _summaryLabel;
	private CheckBox _unassignCheck;
	private CheckBox _replaceCheck;
	private OptionButton _replaceOption;
	private Button _cancelButton;
	private Button _deleteButton;

	private readonly List<(int id, string name)> _replacements = new();
	private bool _signalsConnected;

	/// <summary>
	/// Applies Linux native-window policy before the scene window enters the tree.
	/// </summary>
	public ResourceInUseDeleteDialog()
	{
		LinuxWindowEmbedPolicy.ApplyToAppWindow(this);
	}

	public override void _Ready()
	{
		_globalData = GetNode<GlobalData>("/root/GlobalData");
		_globalSignals = GetNode<GlobalSignals>("/root/GlobalSignals");

		GD.Print("ResourceInUseDeleteDialog:Loading ResourceInUseDeleteDialog");

		UiUtilities.RescaleWindow(this, _globalData.BaseDisplayScale);
		float userScale = _globalData.UserDataManager?.UiScale ?? UserDataManager.DefaultUiScale;
		UiUtilities.RescaleUi(this, userScale, _globalData.BaseDisplayScale);

		_globalSignals.UiScaleChanged += ScaleUi;

		ResolveNodes();
		ConnectUiSignals();
	}

	public override void _ExitTree()
	{
		if (_globalSignals != null)
			_globalSignals.UiScaleChanged -= ScaleUi;

		DisconnectUiSignals();
	}

	/// <summary>
	/// Instantiates the dialog scene (same pattern as file-drop popup loading).
	/// </summary>
	/// <param name="errorMessage">Load error if null is returned.</param>
	/// <returns>Dialog instance, or null on failure.</returns>
	public static ResourceInUseDeleteDialog Create(out string errorMessage)
	{
		var node = SceneLoader.LoadScene(SceneUid, out errorMessage);
		if (node is ResourceInUseDeleteDialog dialog)
			return dialog;

		if (node != null)
		{
			node.QueueFree();
			errorMessage = "Loaded scene is not a ResourceInUseDeleteDialog.";
		}
		else if (string.IsNullOrEmpty(errorMessage))
		{
			errorMessage = $"Failed to load {SceneUid}.";
		}

		return null;
	}

	/// <summary>
	/// Configures dialog content for a resource that is in use.
	/// Safe before AddChild (scene instance children are available after Instantiate).
	/// </summary>
	/// <param name="resourceKindLabel">e.g. "audio output patch" or "target layer".</param>
	/// <param name="resourceName">Display name of the resource being deleted.</param>
	/// <param name="usingCues">Cues that reference the resource.</param>
	/// <param name="replacements">Alternate resources (id, display name). Empty disables Replace.</param>
	public void Configure(
		string resourceKindLabel,
		string resourceName,
		IReadOnlyList<Cue> usingCues,
		IReadOnlyList<(int id, string name)> replacements)
	{
		ResolveNodes();
		// Wire toggles early if Configure runs before _Ready
		ConnectUiSignals();

		_replacements.Clear();
		if (replacements != null)
			_replacements.AddRange(replacements);

		int count = usingCues?.Count ?? 0;
		string kind = UiLocalizer.T(string.IsNullOrWhiteSpace(resourceKindLabel) ? "resource" : resourceKindLabel);
		string name = string.IsNullOrWhiteSpace(resourceName) ? UiLocalizer.T("(unnamed)") : resourceName;

		string title = UiLocalizer.Tf("Delete {0}?", kind);
		Title = title;
		if (_titleLabel != null)
			_titleLabel.Text = title;

		if (_summaryLabel == null)
		{
			GD.PrintErr("ResourceInUseDeleteDialog:Configure - SummaryLabel missing from scene.");
			return;
		}

		_summaryLabel.Text = count == 1
			? UiLocalizer.Tf("\"{0}\" is used by 1 cue.\nChoose what to do with that cue before deleting.", name)
			: UiLocalizer.Tf("\"{0}\" is used by {1} cues.\nChoose what to do with those cues before deleting.", name, count);

		string tip = CueResourceUsage.BuildCueListTooltip(usingCues);
		_summaryLabel.TooltipText = string.IsNullOrEmpty(tip)
			? UiLocalizer.T("No cue numbers available.")
			: tip;

		if (_replaceOption == null || _replaceCheck == null || _unassignCheck == null)
			return;

		_replaceOption.Clear();
		foreach (var (id, repName) in _replacements)
		{
			_replaceOption.AddItem(string.IsNullOrWhiteSpace(repName) ? $"id {id}" : repName);
			_replaceOption.SetItemMetadata(_replaceOption.ItemCount - 1, id);
		}

		bool hasReplacements = _replacements.Count > 0;
		_replaceCheck.Disabled = !hasReplacements;
		_replaceOption.Disabled = !hasReplacements;
		_replaceOption.Visible = hasReplacements;

		_unassignCheck.ButtonPressed = true;
		_replaceCheck.ButtonPressed = false;
		if (hasReplacements)
			_replaceOption.Select(0);

		UpdateReplaceEnabled();
	}

	/// <summary>
	/// Shows the configured popup centered.
	/// </summary>
	/// <remarks>
	/// Do not call <see cref="Control.ResetSize"/> on full-rect children or rewrite scroll
	/// min-sizes at show time — that breaks anchors at runtime (content ends up outside the window).
	/// Window size comes from the scene + <see cref="UiUtilities.RescaleWindow"/>; content fills via anchors.
	/// </remarks>
	public void ShowConfigured()
	{
		PopupCentered();
	}

	private void ResolveNodes()
	{
		_titleLabel ??= GetNodeOrNull<Label>("%TitleLabel");
		_summaryLabel ??= GetNodeOrNull<Label>("%SummaryLabel");
		_unassignCheck ??= GetNodeOrNull<CheckBox>("%UnassignCheck");
		_replaceCheck ??= GetNodeOrNull<CheckBox>("%ReplaceCheck");
		_replaceOption ??= GetNodeOrNull<OptionButton>("%ReplaceOption");
		_cancelButton ??= GetNodeOrNull<Button>("%CancelButton");
		_deleteButton ??= GetNodeOrNull<Button>("%DeleteButton");
	}

	private void ConnectUiSignals()
	{
		if (_signalsConnected)
			return;

		ResolveNodes();
		if (_unassignCheck == null || _replaceCheck == null || _cancelButton == null || _deleteButton == null)
			return;

		_unassignCheck.Toggled += OnUnassignToggled;
		_replaceCheck.Toggled += OnReplaceToggled;
		_cancelButton.Pressed += OnCancelPressed;
		_deleteButton.Pressed += OnDeletePressed;
		CloseRequested += OnCancelPressed;
		_signalsConnected = true;
	}

	private void DisconnectUiSignals()
	{
		if (!_signalsConnected)
			return;

		if (_unassignCheck != null) _unassignCheck.Toggled -= OnUnassignToggled;
		if (_replaceCheck != null) _replaceCheck.Toggled -= OnReplaceToggled;
		if (_cancelButton != null) _cancelButton.Pressed -= OnCancelPressed;
		if (_deleteButton != null) _deleteButton.Pressed -= OnDeletePressed;
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
			GD.Print($"ResourceInUseDeleteDialog:ScaleUi - Applied effective UI scale: {effectiveScale}");
		}
		catch (Exception ex)
		{
			_globalSignals?.EmitSignal(nameof(GlobalSignals.Log),
				$"Error applying UI scale: {ex.Message}", (int)LogType.Warning);
		}
	}

	private void OnUnassignToggled(bool pressed)
	{
		if (pressed)
		{
			_replaceCheck.SetPressedNoSignal(false);
			UpdateReplaceEnabled();
		}
		else if (!_replaceCheck.ButtonPressed)
		{
			_unassignCheck.SetPressedNoSignal(true);
		}
	}

	private void OnReplaceToggled(bool pressed)
	{
		if (pressed)
		{
			if (_replaceCheck.Disabled)
			{
				_replaceCheck.SetPressedNoSignal(false);
				_unassignCheck.SetPressedNoSignal(true);
				return;
			}
			_unassignCheck.SetPressedNoSignal(false);
		}
		else if (!_unassignCheck.ButtonPressed)
		{
			_unassignCheck.SetPressedNoSignal(true);
		}
		UpdateReplaceEnabled();
	}

	private void UpdateReplaceEnabled()
	{
		if (_replaceOption == null || _replaceCheck == null)
			return;
		bool replace = _replaceCheck.ButtonPressed && !_replaceCheck.Disabled;
		_replaceOption.Disabled = !replace;
	}

	private void OnCancelPressed()
	{
		Cancelled?.Invoke();
		// Match FileDropPopup lifecycle: hide then free
		Hide();
		QueueFree();
	}

	private void OnDeletePressed()
	{
		if (_replaceCheck.ButtonPressed && !_replaceCheck.Disabled)
		{
			int id = -1;
			if (_replaceOption.Selected >= 0 && _replaceOption.Selected < _replaceOption.ItemCount)
				id = (int)_replaceOption.GetItemMetadata(_replaceOption.Selected);

			if (id < 0)
			{
				_globalSignals?.EmitSignal(nameof(GlobalSignals.Log),
					"ResourceInUseDeleteDialog: No valid replacement selected.", 1);
				return;
			}

			Confirmed?.Invoke(new ResourceInUseDeleteResult
			{
				Action = ResourceInUseDeleteAction.Replace,
				ReplaceWithId = id
			});
		}
		else
		{
			Confirmed?.Invoke(new ResourceInUseDeleteResult
			{
				Action = ResourceInUseDeleteAction.Unassign,
				ReplaceWithId = -1
			});
		}

		Hide();
		QueueFree();
	}
}
