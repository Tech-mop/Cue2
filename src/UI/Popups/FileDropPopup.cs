// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using Godot;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Cue2.Domain.Cuelist;
using Cue2.Domain.Cues;
using Cue2.UI.Shell;
using Cue2.Services;
using Cue2.UI.Utilities;
using Cue2.UI.Popups;

namespace Cue2.UI.Popups;

/// <summary>
/// Popup window shown when files are dropped onto the cue list or a shell bar.
/// Presents context-sensitive options for how to turn the dropped media files into cues.
/// </summary>
public partial class FileDropPopup : Window
{
	private GlobalData _globalData;
	private GlobalSignals _globalSignals;

	private string[] _files = Array.Empty<string>();
	private DroppedMediaResult _expansion;
	private FileDropTargetType _targetType = FileDropTargetType.None;
	private string _targetDisplayName = "";
	private int _targetCueId = -1;

	private Button _cancelButton;
	private Button _createButton;

	private DropInsertMode _chosenInsertMode = DropInsertMode.Below;
	private MultiFileDropMode _chosenMultiFileMode = MultiFileDropMode.SeparateCues;
	private BoardDropAction _chosenBoardAction = BoardDropAction.Replace;

	/// <summary>
	/// Applies Linux native-window policy before the scene window enters the tree.
	/// </summary>
	public FileDropPopup()
	{
		LinuxWindowEmbedPolicy.ApplyToAppWindow(this);
	}

	/// <summary>
	/// Raised when the user confirms the drop action with their choices.
	/// </summary>
	public event Action<FileDropChoices> Confirmed;

	/// <summary>
	/// Raised when the user cancels the drop.
	/// </summary>
	public event Action Cancelled;

	public override void _Ready()
	{
		_globalData = GetNode<GlobalData>("/root/GlobalData");
		_globalSignals = GetNode<GlobalSignals>("/root/GlobalSignals");

		GD.Print("FileDropPopup:Loading FileDropPopup");
		
		UiUtilities.RescaleWindow(this, _globalData.BaseDisplayScale);
		float userScale = _globalData.UserDataManager?.UiScale ?? UserDataManager.DefaultUiScale;
		UiUtilities.RescaleUi(this, userScale, _globalData.BaseDisplayScale);

		_globalSignals.UiScaleChanged += ScaleUi;

		_cancelButton = GetNode<Button>("%CancelButton");
		_createButton = GetNode<Button>("%CreateButton");

		_cancelButton.Pressed += OnCancelPressed;
		_createButton.Pressed += OnCreatePressed;

		UiLocalizer.LocalizeTree(this);
	}

	public override void _ExitTree()
	{
		_globalSignals.UiScaleChanged -= ScaleUi;
		if (_cancelButton != null) _cancelButton.Pressed -= OnCancelPressed;
		if (_createButton != null) _createButton.Pressed -= OnCreatePressed;
	}

	/// <summary>
	/// Configures the popup for a specific drop and populates dynamic option controls.
	/// Call this before or instead of Show().
	/// </summary>
	/// <param name="files">The dropped file paths (already filtered to valid media).</param>
	/// <param name="targetType">Where the files landed.</param>
	/// <param name="targetDisplayName">Readable target (cue name or "Cue List").</param>
	/// <param name="targetCueId">Cue ID if dropped on a specific shell, otherwise -1.</param>
	/// <param name="expansion">Optional folder-tree expansion; enables preserve-tree and large-import warning.</param>
	public void ConfigureForDrop(
		string[] files,
		FileDropTargetType targetType,
		string targetDisplayName,
		int targetCueId,
		DroppedMediaResult expansion = null)
	{
		_files = files ?? Array.Empty<string>();
		_expansion = expansion;
		_targetType = targetType;
		_targetDisplayName = targetDisplayName ?? targetType.ToString();
		_targetCueId = targetCueId;

		_chosenInsertMode = DropInsertMode.Below;
		_chosenMultiFileMode = expansion?.HasFolderTree == true
			? MultiFileDropMode.PreserveFolderTree
			: MultiFileDropMode.SeparateCues;
		_chosenBoardAction = BoardDropAction.Replace;

		PopulateOptions();
	}

	private void PopulateOptions()
	{
		var container = GetNode<VBoxContainer>("%OptionsListContiner");
		if (container == null) return;

		// Clear previous dynamic content immediately so header MoveChild indices stay correct.
		// Keep the two static header labels (identified by node Name).
		foreach (Node child in container.GetChildren().ToArray())
		{
			if (child is Label lbl && (lbl.Name == "DropTargetLabel" || lbl.Name == "DropFileName"))
				continue;
			container.RemoveChild(child);
			child.Free();
		}

		var dropTargetLabel = GetNode<Label>("%DropTargetLabel");
		var dropFileNameLabel = GetNode<Label>("%DropFileName");

		// Header: "N Files Dropped" (names on hover), separator, then drop target.
		var fileNames = _files.Select(Path.GetFileName).ToArray();
		int count = _files.Length;
		dropFileNameLabel.Text = count == 1
			? UiLocalizer.T("1 File Dropped")
			: UiLocalizer.Tf("{0} Files Dropped", count);
		// Labels ignore mouse by default; Stop so the tooltip can appear on hover.
		dropFileNameLabel.MouseFilter = Control.MouseFilterEnum.Stop;
		dropFileNameLabel.TooltipText = fileNames.Length > 0
			? string.Join("\n", fileNames)
			: string.Empty;

		dropTargetLabel.Text = FormatDropTargetLine();
		EnableLabelWrap(dropFileNameLabel);
		EnableLabelWrap(dropTargetLabel);

		var headerSep = new HSeparator();
		container.AddChild(headerSep);

		// Fixed order: files count → H line → drop target → options below.
		container.MoveChild(dropFileNameLabel, 0);
		container.MoveChild(headerSep, 1);
		container.MoveChild(dropTargetLabel, 2);

		if (_targetType == FileDropTargetType.ButtonBoard)
		{
			PopulateBoardOptions(container);
			if (_createButton != null)
				_createButton.Text = UiLocalizer.T("Apply");
			return;
		}

		if (_createButton != null)
			_createButton.Text = UiLocalizer.T("Create Cues");

		// Position options (only meaningful for ShellBar target)
		if (_targetType == FileDropTargetType.ShellBar && _targetCueId >= 0)
		{
			container.AddChild(CreateWrappingLabel(UiLocalizer.T("Insert Position:")));

			var posContainer = new VBoxContainer();
			posContainer.AddThemeConstantOverride("separation", 2);
			posContainer.AddChild(CreatePositionChoice(UiLocalizer.T("Above target cue"), DropInsertMode.Above));
			posContainer.AddChild(CreatePositionChoice(UiLocalizer.T("Below target cue"), DropInsertMode.Below, isDefault: true));
			posContainer.AddChild(CreatePositionChoice(UiLocalizer.T("As child of target cue"), DropInsertMode.AsChild));
			container.AddChild(posContainer);
		}
		else
		{
			container.AddChild(CreateWrappingLabel(UiLocalizer.T("Insert Location: End of list (or after current selection)")));
		}

		if (_expansion?.IsLargeImport == true)
		{
			container.AddChild(new HSeparator());
			container.AddChild(CreateWrappingLabel(FormatLargeImportWarning(_expansion)));
		}

		// Multi-file options
		if (_files.Length > 1)
		{
			container.AddChild(new HSeparator());
			container.AddChild(CreateWrappingLabel(UiLocalizer.T("Multiple Files Action:")));

			bool hasFolderTree = _expansion?.HasFolderTree == true;
			var multiContainer = new VBoxContainer();
			multiContainer.AddThemeConstantOverride("separation", 2);
			if (hasFolderTree)
			{
				multiContainer.AddChild(CreateMultiFileChoice(
					UiLocalizer.T("Preserve folder tree as groups (folder names become group cues)"),
					MultiFileDropMode.PreserveFolderTree,
					isDefault: true));
			}
			multiContainer.AddChild(CreateMultiFileChoice(
				hasFolderTree
					? UiLocalizer.T("Create separate cue for each file")
					: UiLocalizer.T("Create separate cue for each file (recommended)"),
				MultiFileDropMode.SeparateCues,
				isDefault: !hasFolderTree));
			multiContainer.AddChild(CreateMultiFileChoice(
				UiLocalizer.T("Wrap all files inside one new Group cue"),
				MultiFileDropMode.WrapInOneGroup));
			multiContainer.AddChild(CreateMultiFileChoice(
				UiLocalizer.T("Create each file as child of its own parent cue"),
				MultiFileDropMode.ParentPerFile));
			container.AddChild(multiContainer);
		}
	}

	/// <summary>
	/// Builds the large-import warning shown when many files were expanded from a drop.
	/// </summary>
	/// <param name="expansion">Expanded drop (file and folder counts).</param>
	/// <returns>Localized warning text.</returns>
	private static string FormatLargeImportWarning(DroppedMediaResult expansion)
	{
		int files = expansion?.FileCount ?? 0;
		int folders = expansion?.FolderCount ?? 0;
		if (folders > 0)
		{
			return UiLocalizer.Tf(
				"This drop includes {0} files in {1} folders. Creating that many cues may take a moment.",
				files,
				folders);
		}

		return UiLocalizer.Tf(
			"This drop includes {0} files. Creating that many cues may take a moment.",
			files);
	}

	private void PopulateBoardOptions(VBoxContainer container)
	{
		container.AddChild(CreateWrappingLabel(UiLocalizer.T("This cell already has a cue:")));

		var actionContainer = new VBoxContainer();
		actionContainer.AddThemeConstantOverride("separation", 2);
		actionContainer.AddChild(CreateBoardActionChoice(
			UiLocalizer.T("Replace this cue's media"),
			BoardDropAction.Replace,
			isDefault: true));
		actionContainer.AddChild(CreateBoardActionChoice(
			UiLocalizer.T("Place in the next available cell"),
			BoardDropAction.FillNext));
		container.AddChild(actionContainer);
	}

	/// <summary>
	/// Builds the drop-target summary line: cue number + name for shell drops, or list fallback.
	/// </summary>
	private string FormatDropTargetLine()
	{
		if (_targetType == FileDropTargetType.ShellBar && _targetCueId >= 0)
		{
			Cue cue = CueList.FetchCueFromId(_targetCueId);
			if (cue != null)
			{
				string cueNum = string.IsNullOrWhiteSpace(cue.CueNum)
					? cue.Id.ToString()
					: cue.CueNum.Trim();
				string cueName = cue.Name ?? string.Empty;
				return UiLocalizer.Tf("Drop Location: Cue Number: {0} - Name: {1}", cueNum, cueName);
			}
		}

		if (_targetType == FileDropTargetType.CueList)
			return UiLocalizer.T("Drop Location: Cue List");

		if (_targetType == FileDropTargetType.ButtonBoard)
		{
			if (_targetCueId >= 0)
			{
				Cue cue = CueList.FetchCueFromId(_targetCueId);
				if (cue != null)
				{
					string cueNum = string.IsNullOrWhiteSpace(cue.CueNum)
						? cue.Id.ToString()
						: cue.CueNum.Trim();
					string cueName = cue.Name ?? string.Empty;
					return UiLocalizer.Tf("Drop Location: Button Board — Cue Number: {0} - Name: {1}", cueNum, cueName);
				}
			}

			return UiLocalizer.Tf("Drop Location: {0}",
				string.IsNullOrWhiteSpace(_targetDisplayName) ? "Button Board" : _targetDisplayName);
		}

		return string.IsNullOrWhiteSpace(_targetDisplayName)
			? UiLocalizer.T("Drop Location: —")
			: UiLocalizer.Tf("Drop Location: {0}", _targetDisplayName);
	}

	/// <summary>
	/// Builds a mutually exclusive checkbox for multi-file structure mode.
	/// </summary>
	private CheckBox CreateMultiFileChoice(string text, MultiFileDropMode mode, bool isDefault = false)
	{
		var cb = new CheckBox
		{
			Text = text,
			ButtonPressed = isDefault || _chosenMultiFileMode == mode
		};
		EnableButtonWrap(cb);

		if (isDefault) _chosenMultiFileMode = mode;

		cb.Toggled += pressed =>
		{
			if (!pressed) return;

			_chosenMultiFileMode = mode;
			var parent = cb.GetParent();
			if (parent == null) return;
			foreach (Node sibling in parent.GetChildren())
			{
				if (sibling is CheckBox other && other != cb)
					other.ButtonPressed = false;
			}
		};

		return cb;
	}

	private CheckBox CreateBoardActionChoice(string text, BoardDropAction action, bool isDefault = false)
	{
		var cb = new CheckBox
		{
			Text = text,
			ButtonPressed = isDefault || _chosenBoardAction == action
		};
		EnableButtonWrap(cb);

		if (isDefault) _chosenBoardAction = action;

		cb.Toggled += pressed =>
		{
			if (!pressed) return;
			_chosenBoardAction = action;
			var parent = cb.GetParent();
			if (parent == null) return;
			foreach (Node sibling in parent.GetChildren())
			{
				if (sibling is CheckBox other && other != cb)
					other.ButtonPressed = false;
			}
		};

		return cb;
	}

	private CheckBox CreatePositionChoice(string text, DropInsertMode mode, bool isDefault = false)
	{
		var cb = new CheckBox
		{
			Text = text,
			ButtonPressed = isDefault || _chosenInsertMode == mode
		};
		EnableButtonWrap(cb);

		if (isDefault) _chosenInsertMode = mode;

		cb.Toggled += pressed =>
		{
			if (pressed)
			{
				_chosenInsertMode = mode;
				// Uncheck siblings (simple manual mutual exclusion)
				var parent = cb.GetParent();
				if (parent != null)
				{
					foreach (Node sibling in parent.GetChildren())
					{
						if (sibling is CheckBox other && other != cb)
							other.ButtonPressed = false;
					}
				}
			}
		};

		return cb;
	}

	private void OnCreatePressed()
	{
		var choices = new FileDropChoices
		{
			InsertMode = _chosenInsertMode,
			MultiFileMode = _chosenMultiFileMode,
			BoardAction = _chosenBoardAction
		};

		Confirmed?.Invoke(choices);
		// Do not queue free here — caller decides (usually after acting)
		Hide();
	}

	private void OnCancelPressed()
	{
		Cancelled?.Invoke();
		Hide();
	}

	private void ScaleUi(float value)
	{
		try
		{
			float effectiveScale = value * _globalData.BaseDisplayScale;
			WrapControls = true;
			ContentScaleFactor = effectiveScale;
			ChildControlsChanged();
			GD.Print($"FileDropPopup:ScaleUi - Applied effective UI scale: {effectiveScale} (user: {value} * base: {_globalData.BaseDisplayScale})");
		} 
		catch (Exception ex)
		{
			_globalSignals.EmitSignal(nameof(GlobalSignals.Log), $"Error applying UI scale: {ex.Message}", (int)LogType.Warning);
			GetWindow().ContentScaleFactor = value;
		}
	}

	/// <summary>
	/// Shows the configured popup centered.
	/// </summary>
	/// <remarks>
	/// Avoid runtime ResetSize / min-size rewrites on full-rect layout — they break anchors.
	/// Size is set by the scene and <see cref="UiUtilities.RescaleWindow"/>.
	/// </remarks>
	public void ShowConfigured()
	{
		PopupCentered();
	}

	/// <summary>
	/// Returns the files that were passed to ConfigureForDrop (used by caller after confirmation).
	/// </summary>
	public string[] GetFilesForCreation() => _files?.ToArray() ?? Array.Empty<string>();

	/// <summary>
	/// Returns the cue id of the shell that was the drop target (or -1).
	/// </summary>
	public int GetTargetCueId() => _targetCueId;

	/// <summary>
	/// Creates a full-width wrapping label for popup option headers.
	/// </summary>
	/// <param name="text">Already-localized label text.</param>
	/// <returns>Configured label.</returns>
	private static Label CreateWrappingLabel(string text)
	{
		var label = new Label { Text = text };
		EnableLabelWrap(label);
		return label;
	}

	/// <summary>
	/// Allows a label to wrap inside the popup width instead of stretching the window.
	/// </summary>
	/// <param name="label">Label to configure.</param>
	private static void EnableLabelWrap(Label label)
	{
		if (label == null) return;
		label.AutowrapMode = TextServer.AutowrapMode.WordSmart;
		label.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
	}

	/// <summary>
	/// Allows a checkbox / button caption to wrap inside the popup width.
	/// </summary>
	/// <param name="button">Button or checkbox to configure.</param>
	private static void EnableButtonWrap(Button button)
	{
		if (button == null) return;
		button.AutowrapMode = TextServer.AutowrapMode.WordSmart;
		button.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
	}
}
