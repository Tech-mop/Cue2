// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using Godot;
using Cue2.Domain.Cuelist;
using Cue2.Domain.Playback;
using Cue2.Domain.Devices;
using Cue2.Domain.ShowSettings;
using Cue2.Domain.Metadata;
using Cue2.Domain.Cues;
using Cue2.Domain.Connections;
using Cue2.Domain.Library;
using Cue2.Domain.Commands;
using Cue2.Services;
using Cue2.UI.Utilities;

// This script is attached to instanced shell bars in the cue list, it handles
// -UI of itself
// -Emitting signals of interactions attached with it's relevant info
namespace Cue2.UI.Shell;

public partial class ShellBar : PanelContainer
{
	private GlobalData _globalData;
	private GlobalSignals _globalSignals;
	private GlobalStyles _globalStyles;

	[Export] public int CueId { get; set; } = -1;
	private Cue _cue;
	
	// Ui Properties
	/// <summary>Left nest-color rail (ancestor strips + own colour). HBox of ColorRects.</summary>
	private HBoxContainer _colorPanel;
	private Panel _groupPanel;
	private Panel _shellPanel;
	private Button _dragButton;
	private Button _collapseButton;
	private Button _issueIndicator;
	private StyleBoxFlat _issueActiveStyle;
	private StyleBoxEmpty _issueIdleStyle;
	private Control _treeIndent;
	
	private LineEdit _cueNumLineEdit;
	private LineEdit _cueNameLineEdit;
	/// <summary>Shown in place of number/name/times when the cue is in memo mode.</summary>
	private LineEdit _memoLineEdit;

	private LineEdit _preWaitLineEdit;
	private LineEdit _durationLineEdit;
	private LineEdit _postWaitLineEdit;
	/// <summary>Cycles None → Auto-continue → Auto-follow; shows → / ↳.</summary>
	private Button _followButton;

	/// <summary>Main cue strip HBox (not including nested children).</summary>
	private HBoxContainer _rowHBox;

	/// <summary>Left flexible band: color, drag, issue, indent, collapse, number, name.</summary>
	private HBoxContainer _leadingHBox;

	/// <summary>Right fixed band: pre-wait, duration, post-wait, follow (pinned to trailing edge).</summary>
	private HBoxContainer _trailingHBox;

	public VBoxContainer ShellChildContainer;
	
	private bool _isEditingName = false;
	private bool _isEditingCueNum = false;
	private bool _isEditingPreWait = false;
	private bool _isEditingPostWait = false;
	private bool _isEditingMemo = false;
	
	public bool Selected = false;

	/// <summary>Visual row index for zebra striping (0-based, includes nested visible shells).</summary>
	private int _zebraIndex;

	/// <summary>True while the mouse is over this shell (and not selected).</summary>
	private bool _hovered;

	/// <summary>Per-row panel style (metrics fixed; colours rebuilt for zebra + cue + state).</summary>
	private StyleBoxFlat _panelStyle;

	/// <summary>Right-click context menu for cue editing actions (replaces built-in LineEdit menu).</summary>
	private PopupMenu _contextMenu;

	/// <summary>Ids for <see cref="_contextMenu"/> items.</summary>
	private enum ShellContextMenuId
	{
		Cut = 0,
		Copy = 1,
		Paste = 2,
		Duplicate = 3,
		Delete = 4,
		Group = 5,
		CreateCue = 6
	}

	// Called when the node enters the scene tree for the first time.
	public override void _Ready()
	{
		_globalData = GetNode<GlobalData>("/root/GlobalData");
		_globalSignals = GetNode<GlobalSignals>("/root/GlobalSignals");
		
		DefineUi();
		ApplyShellChromeDefaults();
		
		_cueNumLineEdit.Editable = false;
		_cueNameLineEdit.Editable = false;
		if (_memoLineEdit != null)
			_memoLineEdit.Editable = false;
		
		// Connect Ui events
		GuiInput += OnInput;
		MouseEntered += OnMouseEntered;
		MouseExited += OnMouseExited;
		
		// GuiInput + AcceptEvent: do not use ButtonDown. Reorder commits on global mouse-up
		// in CueList._Input (before GUI), which left BaseButton latched with keep_pressed_outside
		// and required a second click on the same grabber.
		_dragButton.GuiInput += OnDragBarGuiInput;
		
		_collapseButton.Pressed += CollapsedPressed;
		_cueNumLineEdit.GuiInput += OnCueNumGuiInput;
		_cueNumLineEdit.EditingToggled += OnCueNumEditToggled;
		_cueNumLineEdit.FocusExited += OnCueNumFocusExited;
		_cueNumLineEdit.TextSubmitted += OnCueNumTextSubmitted;
		_cueNameLineEdit.GuiInput += OnNameGuiInput;
		_cueNameLineEdit.EditingToggled += OnNameEditToggled;
		_cueNameLineEdit.FocusExited += OnNameFocusExited;
		_cueNameLineEdit.TextSubmitted += OnNameTextSubmitted;
		// Pre/post waits are always-editable time fields (not double-click-to-edit like name/num).
		WireTimeField(_preWaitLineEdit, isPreWait: true);
		WireTimeField(_postWaitLineEdit, isPreWait: false);
		// Duration is read-only but still needs right-click context menu + selection.
		if (_durationLineEdit != null)
			_durationLineEdit.GuiInput += OnTimeFieldGuiInput;
		if (_memoLineEdit != null)
		{
			_memoLineEdit.GuiInput += OnMemoGuiInput;
			_memoLineEdit.EditingToggled += OnMemoEditToggled;
			_memoLineEdit.FocusExited += OnMemoFocusExited;
			_memoLineEdit.TextSubmitted += OnMemoTextSubmitted;
		}
		if (_followButton != null)
			_followButton.Pressed += OnFollowButtonPressed;

		// Reorder grabber — force visible chrome (theme icon + readable modulate).
		ApplyDragGrabberStyle();
		// Collapse column is always reserved; icon is applied only when the cue has children.
		_collapseButton.Icon = null;
		_collapseButton.Disabled = true;
		_collapseButton.MouseFilter = MouseFilterEnum.Ignore;

		// Optional global refresh targeting this cue id
		_globalSignals.UpdateShellBar += OnUpdateShellBar;
		_globalSignals.CueMediaHealthChanged += OnCueMediaHealthChanged;
		_globalSignals.ShowModeChanged += OnShowModeChanged;

		ShellColumnLayout.Changed += OnShellColumnLayoutChanged;
		ApplyColumnLayout();
		ApplyShowModeEditLock(_globalData?.Settings?.IsCueEditingLocked == true);
	}

	/// <summary>
	/// Stable panel chrome + compact field styling so row controls stay inside the outline
	/// and align on a single vertical center line.
	/// </summary>
	private void ApplyShellChromeDefaults()
	{
		// Do not clip the grabber icon; shell metrics are stable so overflow is not needed.
		ClipContents = false;
		CustomMinimumSize = new Vector2(0, ShellColumnLayout.RowMinHeight);
		SizeFlagsVertical = SizeFlags.Fill;

		// Per-row style with locked metrics (zebra + cue wash applied in RefreshShellChrome).
		EnsurePanelStyle();
		RefreshShellChrome();

		// Legacy overlay panels used for selection drew under OuterHBox and got covered.
		// Keep them non-interactive and hidden — selection uses this container's panel instead.
		if (_shellPanel != null)
		{
			_shellPanel.Visible = false;
			_shellPanel.MouseFilter = MouseFilterEnum.Ignore;
		}
		if (_groupPanel != null)
		{
			_groupPanel.Visible = false;
			_groupPanel.MouseFilter = MouseFilterEnum.Ignore;
		}

		// Structure (flat virtual list — children are sibling rows, not nested VBoxes):
		//   OuterHBox
		//     ColorPanel          ← ancestor colours + own colour (own strip is rightmost)
		//     ContentVBox
		//       RowHBox (Leading | Trailing)
		// ColorNestGap sits between colour strips and between the rail and row content.
		var outerHBox = GetNodeOrNull<HBoxContainer>("OuterHBox");
		if (outerHBox != null)
		{
			outerHBox.SizeFlagsHorizontal = SizeFlags.ExpandFill;
			outerHBox.SizeFlagsVertical = SizeFlags.ExpandFill;
			outerHBox.AddThemeConstantOverride("separation", ShellColumnLayout.ColorNestGap);
		}

		var contentVBox = GetNodeOrNull<VBoxContainer>("OuterHBox/ContentVBox");
		if (contentVBox != null)
		{
			contentVBox.SizeFlagsHorizontal = SizeFlags.ExpandFill;
			contentVBox.SizeFlagsVertical = SizeFlags.ExpandFill;
			contentVBox.AddThemeConstantOverride("separation", 0);
		}

		if (_rowHBox != null)
		{
			_rowHBox.SizeFlagsHorizontal = SizeFlags.ExpandFill;
			_rowHBox.AddThemeConstantOverride("separation", ShellColumnLayout.RowSeparation);
			_rowHBox.Alignment = BoxContainer.AlignmentMode.Begin;
		}

		if (_leadingHBox != null)
		{
			_leadingHBox.SizeFlagsHorizontal = SizeFlags.ExpandFill;
			_leadingHBox.SizeFlagsVertical = SizeFlags.ShrinkCenter;
			_leadingHBox.AddThemeConstantOverride("separation", ShellColumnLayout.RowSeparation);
		}

		if (_trailingHBox != null)
		{
			// Never expand — stays glued to the right of RowHBox.
			_trailingHBox.SizeFlagsHorizontal = SizeFlags.Fill;
			_trailingHBox.SizeFlagsVertical = SizeFlags.ShrinkCenter;
			_trailingHBox.AddThemeConstantOverride("separation", ShellColumnLayout.RowSeparation);
		}

		if (ShellChildContainer != null)
		{
			// Unused after virtualization (nesting is data + VisibleRowIds). Keep hidden
			// so a leftover child cannot grow this row past VirtualRowHeight.
			ShellChildContainer.Visible = false;
			ShellChildContainer.SizeFlagsHorizontal = SizeFlags.ExpandFill;
			ShellChildContainer.AddThemeConstantOverride("separation", 0);
		}

		// Nested shells must fill parent width (not shrink to content).
		SizeFlagsHorizontal = SizeFlags.ExpandFill;
		SizeFlagsVertical = SizeFlags.Fill;

		if (_cueNameLineEdit != null)
			_cueNameLineEdit.SizeFlagsHorizontal = SizeFlags.ExpandFill;
		if (_memoLineEdit != null)
			_memoLineEdit.SizeFlagsHorizontal = SizeFlags.ExpandFill;

		ApplyCompactFieldStyle(_cueNumLineEdit);
		ApplyCompactFieldStyle(_cueNameLineEdit);
		ApplyCompactFieldStyle(_memoLineEdit);
		ApplyCompactFieldStyle(_preWaitLineEdit);
		ApplyCompactFieldStyle(_durationLineEdit);
		ApplyCompactFieldStyle(_postWaitLineEdit);
	}

	/// <summary>
	/// Restores the reorder grabber icon and hit target (readable on dark rows).
	/// Grabber is visual-only for press state: start is handled in <see cref="OnDragBarGuiInput"/>.
	/// </summary>
	private void ApplyDragGrabberStyle()
	{
		if (_dragButton == null) return;

		_dragButton.Visible = true;
		_dragButton.Disabled = false;
		_dragButton.ToggleMode = false;
		_dragButton.KeepPressedOutside = false;
		_dragButton.FocusMode = FocusModeEnum.None;
		_dragButton.MouseFilter = MouseFilterEnum.Stop;
		_dragButton.MouseDefaultCursorShape = Control.CursorShape.Drag;
		_dragButton.Flat = true;
		_dragButton.ExpandIcon = true;
		_dragButton.IconAlignment = HorizontalAlignment.Center;
		_dragButton.VerticalIconAlignment = VerticalAlignment.Center;
		_dragButton.AddThemeConstantOverride("icon_max_width", ShellColumnLayout.IconMaxWidth);
		// Brighter than the old dark-grey so the grabber is visible on shell chrome.
		var grabberColor = new Color(0.72f, 0.78f, 0.82f, 0.95f);
		_dragButton.AddThemeColorOverride("icon_normal_color", grabberColor);
		_dragButton.AddThemeColorOverride("icon_hover_color", Colors.White);
		_dragButton.AddThemeColorOverride("icon_pressed_color", new Color(0.55f, 0.85f, 0.9f, 1f));

		var icon = GetThemeIcon("Rearrange", "AtlasIcons");
		if (icon != null)
			_dragButton.Icon = icon;

		// Ensure no latched press from a previous session.
		_dragButton.SetPressedNoSignal(false);
		_dragButton.ButtonPressed = false;
	}

	/// <summary>
	/// Tight LineEdit padding so theme content margins don't force fields taller than the row.
	/// Disables Godot's built-in right-click text menu (replaced by shell cue context menu).
	/// Font size follows <see cref="ShellColumnLayout.Scale"/>.
	/// </summary>
	private static void ApplyCompactFieldStyle(LineEdit field)
	{
		if (field == null) return;
		field.ContextMenuEnabled = false;
		field.CustomMinimumSize = new Vector2(field.CustomMinimumSize.X, ShellColumnLayout.RowControlHeight);
		field.SizeFlagsVertical = SizeFlags.ShrinkCenter;
		field.Alignment = HorizontalAlignment.Left;
		field.AddThemeFontSizeOverride("font_size", ShellColumnLayout.FontSize);
		ShellColumnLayout.ApplyCompactLineEditStyleBoxes(field);
		field.AddThemeConstantOverride("minimum_character_width", 1);
	}

	public override void _ExitTree()
	{
		// ShellBars are frequently reparented (group, reorder, show load nesting).
		// Godot calls _ExitTree on reparent as well as free — do not tear down UI/cue
		// state here or ColorPanel styling, signal wiring, and cue binding are lost.
		base._ExitTree();
	}

	public override void _Notification(int what)
	{
		if (what == NotificationResized)
		{
			QueueRedraw();
			return;
		}

		// Permanent cleanup only when the node is actually being destroyed.
		if (what != NotificationPredelete)
			return;

		ShellColumnLayout.Changed -= OnShellColumnLayoutChanged;

		if (_globalSignals != null)
		{
			_globalSignals.UpdateShellBar -= OnUpdateShellBar;
			_globalSignals.CueMediaHealthChanged -= OnCueMediaHealthChanged;
			_globalSignals.ShowModeChanged -= OnShowModeChanged;
		}
		UnbindCue();
		if (_issueIndicator != null && IsInstanceValid(_issueIndicator))
		{
			_issueIndicator.RemoveThemeStyleboxOverride("normal");
			_issueIndicator.RemoveThemeStyleboxOverride("hover");
			_issueIndicator.RemoveThemeStyleboxOverride("pressed");
			_issueIndicator.RemoveThemeStyleboxOverride("disabled");
			_issueIndicator.RemoveThemeStyleboxOverride("focus");
		}
		RemoveThemeStyleboxOverride("panel");
		UiUtilities.DisposeRefCounted(_issueActiveStyle);
		UiUtilities.DisposeRefCounted(_issueIdleStyle);
		UiUtilities.DisposeRefCounted(_panelStyle);
		_issueActiveStyle = null;
		_issueIdleStyle = null;
		_panelStyle = null;
	}

	private void OnShellColumnLayoutChanged()
	{
		if (!IsInstanceValid(this))
			return;
		ApplyColumnLayout();
	}

	/// <summary>
	/// Applies shared column widths and cuelist scale from <see cref="ShellColumnLayout"/> to this row.
	/// Color rail lives in OuterHBox (ancestor colours + own colour).
	/// Row layout: [Color rail | Leading: Drag|Issue|Indent|Collapse|Num|Name(expand)] | [Trailing: Pre|Dur|Post|Follow].
	/// Trailing is fixed-width and non-expanding so nest indent never shifts time columns.
	/// </summary>
	public void ApplyColumnLayout()
	{
		if (_cueNumLineEdit == null)
			return;

		float numW = ShellColumnLayout.NumberWidth;
		float timeW = ShellColumnLayout.TimeWidth;
		float followW = ShellColumnLayout.FollowWidth;
		float ctrlH = ShellColumnLayout.RowControlHeight;
		float rowH = ShellColumnLayout.RowMinHeight;
		int sep = ShellColumnLayout.RowSeparation;
		int fontSize = ShellColumnLayout.FontSize;
		int iconMax = ShellColumnLayout.IconMaxWidth;

		// Keep nest gap / separations in sync when cuelist scale changes.
		var outerHBox = GetNodeOrNull<HBoxContainer>("OuterHBox");
		outerHBox?.AddThemeConstantOverride("separation", ShellColumnLayout.ColorNestGap);

		// Nest colour rail: ancestor strips + own colour (own strip stays on the right).
		RebuildColorRail();
		if (_dragButton != null)
		{
			_dragButton.CustomMinimumSize = new Vector2(ShellColumnLayout.DragWidth, ctrlH);
			_dragButton.SizeFlagsHorizontal = SizeFlags.Fill;
			_dragButton.SizeFlagsVertical = SizeFlags.ShrinkCenter;
			_dragButton.AddThemeConstantOverride("icon_max_width", iconMax);
		}
		if (_collapseButton != null)
		{
			_collapseButton.CustomMinimumSize = new Vector2(ShellColumnLayout.CollapseWidth, ctrlH);
			_collapseButton.SizeFlagsHorizontal = SizeFlags.Fill;
			_collapseButton.SizeFlagsVertical = SizeFlags.ShrinkCenter;
			_collapseButton.AddThemeConstantOverride("icon_max_width", iconMax);
		}
		if (_issueIndicator != null)
		{
			_issueIndicator.CustomMinimumSize = new Vector2(ShellColumnLayout.IssueWidth, ctrlH);
			_issueIndicator.SizeFlagsHorizontal = SizeFlags.Fill;
			_issueIndicator.SizeFlagsVertical = SizeFlags.ShrinkCenter;
			_issueIndicator.AddThemeConstantOverride("icon_max_width", iconMax);
		}

		_cueNumLineEdit.CustomMinimumSize = new Vector2(numW, ctrlH);
		_cueNumLineEdit.SizeFlagsHorizontal = SizeFlags.Fill;
		_cueNumLineEdit.SizeFlagsVertical = SizeFlags.ShrinkCenter;
		_cueNumLineEdit.AddThemeFontSizeOverride("font_size", fontSize);

		// Name absorbs indent; keep min small so deep nests shrink name, not the trailing band.
		if (_cueNameLineEdit != null)
		{
			_cueNameLineEdit.CustomMinimumSize = new Vector2(40f, ctrlH);
			_cueNameLineEdit.SizeFlagsHorizontal = SizeFlags.ExpandFill;
			_cueNameLineEdit.SizeFlagsVertical = SizeFlags.ShrinkCenter;
			_cueNameLineEdit.AddThemeFontSizeOverride("font_size", fontSize);
		}

		if (_memoLineEdit != null)
		{
			_memoLineEdit.CustomMinimumSize = new Vector2(40f, ctrlH);
			_memoLineEdit.SizeFlagsHorizontal = SizeFlags.ExpandFill;
			_memoLineEdit.SizeFlagsVertical = SizeFlags.ShrinkCenter;
			_memoLineEdit.AddThemeFontSizeOverride("font_size", fontSize);
		}

		// Fixed trailing band widths (Pre + Dur + Post + Follow + separations).
		// In memo mode only Follow remains visible — shrink the band so notes get the space.
		bool memo = _cue?.Memo == true;

		_preWaitLineEdit.CustomMinimumSize = new Vector2(timeW, ctrlH);
		_preWaitLineEdit.SizeFlagsHorizontal = SizeFlags.Fill;
		_preWaitLineEdit.SizeFlagsVertical = SizeFlags.ShrinkCenter;
		_preWaitLineEdit.AddThemeFontSizeOverride("font_size", fontSize);

		_durationLineEdit.CustomMinimumSize = new Vector2(timeW, ctrlH);
		_durationLineEdit.SizeFlagsHorizontal = SizeFlags.Fill;
		_durationLineEdit.SizeFlagsVertical = SizeFlags.ShrinkCenter;
		_durationLineEdit.AddThemeFontSizeOverride("font_size", fontSize);

		_postWaitLineEdit.CustomMinimumSize = new Vector2(timeW, ctrlH);
		_postWaitLineEdit.SizeFlagsHorizontal = SizeFlags.Fill;
		_postWaitLineEdit.SizeFlagsVertical = SizeFlags.ShrinkCenter;
		_postWaitLineEdit.AddThemeFontSizeOverride("font_size", fontSize);

		if (_followButton != null)
		{
			_followButton.CustomMinimumSize = new Vector2(followW, ctrlH);
			_followButton.SizeFlagsHorizontal = SizeFlags.Fill;
			_followButton.SizeFlagsVertical = SizeFlags.ShrinkCenter;
			_followButton.Alignment = HorizontalAlignment.Center;
			_followButton.AddThemeFontSizeOverride("font_size", fontSize);
		}

		if (_trailingHBox != null)
		{
			// Explicit fixed width so the band never shrinks or grows with indent.
			float trailW = memo
				? followW
				: timeW * 3f + followW + sep * 3f;
			_trailingHBox.CustomMinimumSize = new Vector2(trailW, ctrlH);
			_trailingHBox.SizeFlagsHorizontal = SizeFlags.Fill;
			_trailingHBox.SizeFlagsVertical = SizeFlags.ShrinkCenter;
		}

		if (_leadingHBox != null)
		{
			_leadingHBox.SizeFlagsHorizontal = SizeFlags.ExpandFill;
			_leadingHBox.SizeFlagsVertical = SizeFlags.ShrinkCenter;
			_leadingHBox.AddThemeConstantOverride("separation", sep);
		}

		if (_trailingHBox != null)
			_trailingHBox.AddThemeConstantOverride("separation", sep);

		if (_rowHBox != null)
		{
			_rowHBox.CustomMinimumSize = new Vector2(0, rowH);
			_rowHBox.SizeFlagsHorizontal = SizeFlags.ExpandFill;
			_rowHBox.SizeFlagsVertical = SizeFlags.ShrinkBegin;
			_rowHBox.Alignment = BoxContainer.AlignmentMode.Begin;
			_rowHBox.AddThemeConstantOverride("separation", sep);
		}

		CustomMinimumSize = new Vector2(0, rowH);

		// Nested shells must stretch to parent content width.
		SizeFlagsHorizontal = SizeFlags.ExpandFill;

		// Re-apply compact padding / font when cuelist scale changes mid-session.
		ApplyCompactFieldStyle(_cueNumLineEdit);
		ApplyCompactFieldStyle(_cueNameLineEdit);
		ApplyCompactFieldStyle(_memoLineEdit);
		ApplyCompactFieldStyle(_preWaitLineEdit);
		ApplyCompactFieldStyle(_durationLineEdit);
		ApplyCompactFieldStyle(_postWaitLineEdit);

		ApplyTreeIndent();
	}

	/// <summary>
	/// Rebuilds the left nest-color rail: one strip per ancestor (root first) plus this cue
	/// on the right. Parent strips on descendant rows make the group colour appear to span
	/// children. Extra rail width is compensated when drawing tree guides.
	/// </summary>
	private void RebuildColorRail()
	{
		if (_colorPanel == null)
			return;

		int depth = _cue != null ? ComputeNestDepth() : 0;
		int stripCount = depth + 1;
		float stripW = ShellColumnLayout.ColorWidth;
		int sep = ShellColumnLayout.ColorNestGap;

		_colorPanel.AddThemeConstantOverride("separation", sep);
		_colorPanel.CustomMinimumSize = new Vector2(
			stripCount * stripW + Mathf.Max(0, stripCount - 1) * sep, 0);
		_colorPanel.SizeFlagsHorizontal = SizeFlags.Fill;
		_colorPanel.SizeFlagsVertical = SizeFlags.ExpandFill;
		_colorPanel.MouseFilter = MouseFilterEnum.Ignore;

		var colors = new Color[stripCount];
		if (_cue == null)
		{
			colors[0] = Colors.Black;
		}
		else
		{
			var current = _cue;
			for (int i = depth; i >= 0; i--)
			{
				colors[i] = current?.Color ?? Colors.Black;
				if (current == null || current.ParentId < 0)
					break;
				current = CueList.FetchCueFromId(current.ParentId);
			}
		}

		int rectIndex = 0;
		foreach (var child in _colorPanel.GetChildren())
		{
			if (child is not ColorRect rect)
				continue;
			if (rectIndex < stripCount)
			{
				rect.Visible = true;
				rect.Color = colors[rectIndex];
				rect.CustomMinimumSize = new Vector2(stripW, 0);
				rect.SizeFlagsHorizontal = SizeFlags.Fill;
				rect.SizeFlagsVertical = SizeFlags.ExpandFill;
				rect.MouseFilter = MouseFilterEnum.Ignore;
				rectIndex++;
			}
			else
			{
				rect.Visible = false;
			}
		}

		while (rectIndex < stripCount)
		{
			var rect = new ColorRect
			{
				Name = $"ColorRail{rectIndex}",
				Color = colors[rectIndex],
				CustomMinimumSize = new Vector2(stripW, 0),
				SizeFlagsHorizontal = SizeFlags.Fill,
				SizeFlagsVertical = SizeFlags.ExpandFill,
				MouseFilter = MouseFilterEnum.Ignore
			};
			_colorPanel.AddChild(rect);
			rectIndex++;
		}
	}

	/// <summary>
	/// Re-applies nest-dependent chrome after ParentId / ChildCues change without a full
	/// <see cref="SetCue"/> (virtual rows are reused across hierarchy edits).
	/// </summary>
	public void RefreshHierarchyChrome()
	{
		if (!IsInstanceValid(this))
			return;
		UpdateCollapseUI();
		ApplyTreeIndent();
		RebuildColorRail();
		RefreshShellChrome();
		// Expand/collapse and ChildCues changes reuse this row without SetCue.
		if (_globalData?.IsSessionLoading != true)
			RefreshIssueIndicatorFromService();
	}

	/// <summary>
	/// Sets the tree indent from nesting depth. Indent sits in LeadingHBox only
	/// (after Drag/Issue) and steals width from the expanding Name — trailing times stay put.
	/// </summary>
	public void ApplyTreeIndent()
	{
		if (_treeIndent == null)
			return;

		int depth = ComputeNestDepth();
		float indent = depth * ShellColumnLayout.NestIndent;
		_treeIndent.CustomMinimumSize = new Vector2(indent, 0);
		_treeIndent.SizeFlagsHorizontal = SizeFlags.Fill;
		_treeIndent.SizeFlagsVertical = SizeFlags.ExpandFill;
		// Hide zero-width spacer so HBox separation is not doubled at root.
		_treeIndent.Visible = depth > 0;
		QueueRedraw();

		ApplyPanelMetricsForDepth();
		if (_panelStyle != null)
			AddThemeStyleboxOverride("panel", _panelStyle);
	}

	/// <summary>
	/// Walks <see cref="Cue.ParentId"/> to count nesting levels (0 = root list).
	/// </summary>
	private int ComputeNestDepth()
	{
		if (_cue == null)
			return 0;

		int depth = 0;
		int parentId = _cue.ParentId;
		while (parentId != -1 && depth < 64)
		{
			depth++;
			var parent = CueList.FetchCueFromId(parentId);
			if (parent == null)
				break;
			parentId = parent.ParentId;
		}
		return depth;
	}

	/// <summary>
	/// Refreshes column layout and nest chrome for this shell.
	/// Descendants are sibling rows in the virtual list — <see cref="CueList"/> refreshes those.
	/// </summary>
	public void RefreshTreeLayoutRecursive()
	{
		ApplyColumnLayout();
		RefreshHierarchyChrome();
	}

	private void OnUpdateShellBar(int cueId)
	{
		if (_cue != null && _cue.Id == cueId)
		{
			// Times only — do not re-run ApplyMemoMode/ApplyColumnLayout here (that thrash can
			// interrupt or re-style the active pre/post LineEdit mid-commit).
			RefreshTimesFromCue();
			QueueRedraw();
		}
	}

	private void OnCueMediaHealthChanged(int cueId, bool hasIssue, string message)
	{
		if (_cue == null)
			return;
		// Recompute rather than applying the signal payload: a collapsed parent must
		// aggregate descendant issues, and clearing one child must not wipe siblings.
		if (_cue.Id != cueId && !IsCollapsedAncestorOf(cueId))
			return;
		RefreshIssueIndicatorFromService();
	}

	/// <summary>
	/// True when this bound cue is a collapsed group that currently hides <paramref name="descendantId"/>.
	/// </summary>
	private bool IsCollapsedAncestorOf(int descendantId)
	{
		if (_cue == null || _cue.ChildCues.Count == 0 || _cue.Expanded)
			return false;

		var current = CueList.FetchCueFromId(descendantId);
		int guard = 0;
		while (current != null && current.ParentId >= 0 && guard++ < 64)
		{
			if (current.ParentId == _cue.Id)
				return true;
			current = CueList.FetchCueFromId(current.ParentId);
		}

		return false;
	}

	/// <summary>
	/// Shows or clears the full-height issue indicator using AtlasIcons "Stop" (X).
	/// The column always reserves space after Drag so presence of the icon never shifts other columns.
	/// </summary>
	private void ApplyIssueIndicator(bool hasIssue, string message)
	{
		if (_issueIndicator == null)
			return;

		EnsureIssueStyles();

		// Always visible for layout stability; icon only when unhealthy.
		_issueIndicator.Visible = true;
		_issueIndicator.Text = string.Empty;
		_issueIndicator.Icon = hasIssue ? GetThemeIcon("Stop", "AtlasIcons") : null;
		_issueIndicator.TooltipText = hasIssue
			? (string.IsNullOrEmpty(message) ? UiLocalizer.T("Issue") : message)
			: string.Empty;
		_issueIndicator.MouseFilter = hasIssue
			? MouseFilterEnum.Stop
			: MouseFilterEnum.Ignore;
		_issueIndicator.Disabled = !hasIssue;

		// Full-height tint when unhealthy; transparent when idle (column still reserved).
		var style = hasIssue ? (StyleBox)_issueActiveStyle : _issueIdleStyle;
		_issueIndicator.AddThemeStyleboxOverride("normal", style);
		_issueIndicator.AddThemeStyleboxOverride("hover", style);
		_issueIndicator.AddThemeStyleboxOverride("pressed", style);
		_issueIndicator.AddThemeStyleboxOverride("disabled", style);
		_issueIndicator.AddThemeStyleboxOverride("focus", style);
	}

	/// <summary>
	/// Builds flat styles for the full-height issue column (active danger tint vs empty slot).
	/// </summary>
	private void EnsureIssueStyles()
	{
		if (_issueActiveStyle != null)
			return;

		_issueActiveStyle = new StyleBoxFlat
		{
			BgColor = new Color(GlobalStyles.Danger.R, GlobalStyles.Danger.G, GlobalStyles.Danger.B, 0.28f),
			ContentMarginLeft = 0,
			ContentMarginRight = 0,
			ContentMarginTop = 0,
			ContentMarginBottom = 0
		};
		_issueIdleStyle = new StyleBoxEmpty();
	}

	/// <summary>
	/// Syncs issue indicator from <see cref="MediaHealthService"/> for the bound cue,
	/// including descendant issues when this row is a collapsed group.
	/// </summary>
	private void RefreshIssueIndicatorFromService()
	{
		if (_cue == null || _issueIndicator == null)
			return;

		var health = GetNodeOrNull<MediaHealthService>("/root/MediaHealthService");
		if (health == null)
		{
			ApplyIssueIndicator(false, string.Empty);
			return;
		}

		bool has = health.TryGetShellBarIssue(_cue, out var tooltip);
		ApplyIssueIndicator(has, tooltip);
	}

	private void DefineUi()
	{
		// Define Ui
		_colorPanel = GetNode<HBoxContainer>("%ColorPanel");
		_groupPanel = GetNode<Panel>("%GroupPanel");
		_shellPanel = GetNode<Panel>("%ShellPanel");
		_dragButton = GetNode<Button>("%DragBar");
		_collapseButton = GetNode<Button>("%CollapseButton");
		_issueIndicator = GetNodeOrNull<Button>("%IssueIndicator");
		_treeIndent = GetNodeOrNull<Control>("%TreeIndent");
		if (_issueIndicator != null)
		{
			// Column always reserved after Drag; full row height via SIZE_EXPAND_FILL.
			// Icon is AtlasIcons "Stop" (X) — sized via icon_max_width / expand_icon.
			_issueIndicator.Visible = true;
			_issueIndicator.Text = string.Empty;
			_issueIndicator.Icon = null;
			_issueIndicator.MouseFilter = MouseFilterEnum.Ignore;
			_issueIndicator.Disabled = true;
			_issueIndicator.SizeFlagsVertical = SizeFlags.ExpandFill;
			_issueIndicator.ExpandIcon = true;
			_issueIndicator.IconAlignment = HorizontalAlignment.Center;
			_issueIndicator.VerticalIconAlignment = VerticalAlignment.Center;
			_issueIndicator.AddThemeConstantOverride("icon_max_width", 12);
			// Red X via icon modulate colors
			_issueIndicator.AddThemeColorOverride("icon_normal_color", GlobalStyles.Danger);
			_issueIndicator.AddThemeColorOverride("icon_hover_color", GlobalStyles.Danger);
			_issueIndicator.AddThemeColorOverride("icon_pressed_color", GlobalStyles.Danger);
			_issueIndicator.AddThemeColorOverride("icon_disabled_color", GlobalStyles.Danger);
			ApplyIssueIndicator(false, string.Empty);
		}
		
		_rowHBox = GetNodeOrNull<HBoxContainer>("%RowHBox");
		_leadingHBox = GetNodeOrNull<HBoxContainer>("%LeadingHBox");
		_trailingHBox = GetNodeOrNull<HBoxContainer>("%TrailingHBox");
		_cueNumLineEdit = GetNode<LineEdit>("%CueNumLineEdit");
		_cueNameLineEdit = GetNode<LineEdit>("%CueNameLineEdit");
		_memoLineEdit = GetNodeOrNull<LineEdit>("%MemoLineEdit");

		_preWaitLineEdit = GetNode<LineEdit>("%PreWaitLineEdit");
		_durationLineEdit = GetNode<LineEdit>("%DurationLineEdit");
		_postWaitLineEdit = GetNode<LineEdit>("%PostWaitLineEdit");
		// Prefer new name; fall back if an older packed scene still uses the checkbox.
		_followButton = GetNodeOrNull<Button>("%FollowButton");
		if (_followButton == null)
			_followButton = GetNodeOrNull<Button>("%FollowCheckBox");
		
		ShellChildContainer = GetNode<VBoxContainer>("%ShellChildContainer");
	}

	/// <summary>
	/// Detaches cue property events so a pooled (out-of-viewport) row does not keep
	/// the last Cue or this node alive after <see cref="CueList"/> recycle.
	/// </summary>
	public void UnbindCue()
	{
		if (_cue != null)
		{
			_cue.NameChanged -= UpdateName;
			_cue.CueNumChanged -= UpdateCueNum;
			_cue.ColorChanged -= UpdateColor;
			_cue.DurationChanged -= UpdateDuration;
			_cue.TotalDurationChanged -= UpdateTotalDuration;
			_cue.PreWaitChanged -= UpdatePreWait;
			_cue.PostWaitChanged -= UpdatePostWait;
			_cue.FollowChanged -= UpdateFollowMode;
			_cue.ArmedChanged -= OnArmedVisualChanged;
			_cue.SkipIfDisarmedChanged -= OnArmedVisualChanged;
			_cue.NotesChanged -= OnNotesChanged;
			_cue.MemoChanged -= OnMemoChanged;
			if (ReferenceEquals(_cue.ShellBar, this))
				_cue.ShellBar = null;
			_cue = null;
		}

		CueId = -1;
	}

	/// <summary>
	/// Binds this row to <paramref name="cue"/> and refreshes visible fields.
	/// </summary>
	/// <param name="cue">Cue to display.</param>
	/// <param name="skipIssueLookup">
	/// When true, do not query <see cref="MediaHealthService"/> (showfile load; health is deferred).
	/// </param>
	/// <param name="deferChrome">
	/// When true, skip indent / zebra chrome / redraw — caller applies indent then
	/// <see cref="SetZebraIndex"/> (first virtual bind).
	/// </param>
	public void SetCue(Cue cue, bool skipIssueLookup = false, bool deferChrome = false)
	{
		UnbindCue();
		if (cue == null)
			return;
		_cue = cue;
		_cue.NameChanged += UpdateName;
		_cue.CueNumChanged += UpdateCueNum;
		_cue.ColorChanged += UpdateColor;
		_cue.DurationChanged += UpdateDuration;
		_cue.TotalDurationChanged += UpdateTotalDuration;
		_cue.PreWaitChanged += UpdatePreWait;
		_cue.PostWaitChanged += UpdatePostWait;
		_cue.FollowChanged += UpdateFollowMode;
		_cue.ArmedChanged += OnArmedVisualChanged;
		_cue.SkipIfDisarmedChanged += OnArmedVisualChanged;
		_cue.NotesChanged += OnNotesChanged;
		_cue.MemoChanged += OnMemoChanged;
		_cueNumLineEdit.Text = cue.CueNum;
		_cueNameLineEdit.Text = cue.Name;
		RefreshTimesFromCue();
		RebuildColorRail();
		UpdateFollowMode(cue.Follow);
		// Initialize collapse/expand UI based on children (SetCue path)
		UpdateCollapseUI();
		if (skipIssueLookup)
			ApplyIssueIndicator(false, string.Empty);
		else
			RefreshIssueIndicatorFromService();
		ApplyMemoMode();
		if (!deferChrome)
		{
			ApplyTreeIndent();
			RefreshShellChrome();
			QueueRedraw();
		}

		_cueNumLineEdit.Editable = false;
		_cueNameLineEdit.Editable = false;
		if (_memoLineEdit != null)
			_memoLineEdit.Editable = false;
		_isEditingCueNum = false;
		_isEditingName = false;
		_isEditingMemo = false;
	}

	/// <summary>
	/// Redraws disarmed hatch when armed / skip-if-disarmed flags change.
	/// </summary>
	private void OnArmedVisualChanged(bool _)
	{
		QueueRedraw();
	}

	/// <summary>
	/// Sets the visual zebra index for this shell (even/odd row). Called by <see cref="CueList"/> after structure changes.
	/// </summary>
	/// <param name="index">0-based index in visual list order.</param>
	public void SetZebraIndex(int index)
	{
		_zebraIndex = index;
		RefreshShellChrome();
	}

	/// <summary>
	/// Refreshes pre-wait, duration, and post-wait fields from the bound cue.
	/// </summary>
	public void RefreshTimesFromCue()
	{
		if (_cue == null) return;
		if (!_isEditingPreWait)
			_preWaitLineEdit.Text = FormatDurationField(_cue.PreWait);
		// Shell shows content duration (TotalDuration when not looping includes pre/post in inspector only)
		_durationLineEdit.Text = FormatDurationField(_cue.Duration);
		if (!_isEditingPostWait)
			_postWaitLineEdit.Text = FormatDurationField(_cue.PostWait);
	}

	/// <summary>
	/// Refreshes shell fields from the bound cue after an in-place history restore.
	/// Property change events cover name/num/color/times; this covers follow and a full times pass.
	/// </summary>
	public void RefreshAllFromCue()
	{
		if (_cue == null) return;
		if (!_isEditingName)
			_cueNameLineEdit.Text = _cue.Name;
		if (!_isEditingCueNum)
			_cueNumLineEdit.Text = _cue.CueNum;
		RefreshTimesFromCue();
		UpdateFollowMode(_cue.Follow);
		RefreshIssueIndicatorFromService();
		RefreshHierarchyChrome();
		ApplyMemoMode();
	}

	/// <summary>
	/// Swaps shell row content between standard fields and memo (notes) layout.
	/// Memo mode hides number, name, pre-wait, duration, and post-wait; follow stays.
	/// </summary>
	private void ApplyMemoMode()
	{
		bool memo = _cue?.Memo == true;

		if (_cueNumLineEdit != null)
			_cueNumLineEdit.Visible = !memo;
		if (_cueNameLineEdit != null)
			_cueNameLineEdit.Visible = !memo;
		if (_preWaitLineEdit != null)
			_preWaitLineEdit.Visible = !memo;
		if (_durationLineEdit != null)
			_durationLineEdit.Visible = !memo;
		if (_postWaitLineEdit != null)
			_postWaitLineEdit.Visible = !memo;

		if (_memoLineEdit != null)
		{
			_memoLineEdit.Visible = memo;
			if (memo && !_isEditingMemo)
			{
				_memoLineEdit.Text = FlattenNotesForShell(_cue?.Notes);
				_memoLineEdit.TooltipText = string.IsNullOrEmpty(_cue?.Notes)
					? UiLocalizer.T("Memo cue — double-click to edit notes.")
					: _cue.Notes;
			}
		}

		// Trailing band width changes when times hide/show.
		ApplyColumnLayout();
	}

	private void OnMemoChanged(bool _)
	{
		ApplyMemoMode();
	}

	private void OnNotesChanged(string notes)
	{
		if (_memoLineEdit == null || _isEditingMemo)
			return;
		if (_cue?.Memo != true)
			return;
		_memoLineEdit.Text = FlattenNotesForShell(notes);
		_memoLineEdit.TooltipText = string.IsNullOrEmpty(notes)
			? UiLocalizer.T("Memo cue — double-click to edit notes.")
			: notes;
	}

	/// <summary>Collapses multi-line notes for the single-line shell field.</summary>
	private static string FlattenNotesForShell(string notes)
	{
		if (string.IsNullOrEmpty(notes))
			return string.Empty;
		return notes
			.Replace("\r\n", " ")
			.Replace('\n', ' ')
			.Replace('\r', ' ')
			.Trim();
	}

	private static string FormatDurationField(double seconds)
	{
		if (seconds < 0)
			return "∞";
		return UiUtilities.FormatTime(seconds);
	}

	private void UpdateDuration(double duration) =>
		_durationLineEdit.Text = FormatDurationField(duration);

	private void UpdateTotalDuration(double totalDuration)
	{
		// Content duration field already updated via DurationChanged; keep tooltip with total
		if (_durationLineEdit != null && _cue != null)
			_durationLineEdit.TooltipText = _cue.TotalDuration < 0
				? UiLocalizer.T("Looping")
				: UiLocalizer.Tf("Total (with waits): {0}", UiUtilities.FormatTime(_cue.TotalDuration));
	}

	private void UpdatePreWait(double preWait)
	{
		if (!_isEditingPreWait)
			_preWaitLineEdit.Text = FormatDurationField(preWait);
	}

	private void UpdatePostWait(double postWait)
	{
		if (!_isEditingPostWait)
			_postWaitLineEdit.Text = FormatDurationField(postWait);
	}
	
	/// <summary>
	/// True when Show Mode is locking cue property / structure edits.
	/// </summary>
}
