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

/// <summary>
/// Partial: Follow mode, collapse, hover chrome
/// </summary>
public partial class ShellBar
{
	private void OnFollowButtonPressed()
	{
		if (_cue == null) return;
		if (IsCueEditingLocked()) return;
		if (_globalData?.HistoryManager?.IsRestoring == true) return;

		var desired = _cue.Follow switch
		{
			FollowType.None => FollowType.Continue,
			FollowType.Continue => FollowType.Follow,
			_ => FollowType.None
		};
		if (_cue.Follow == desired) return;

		_globalData?.HistoryManager?.RecordCueChange(_cue.Id, "Edit continue mode");
		_cue.Follow = desired;
		// UpdateFollowMode runs via FollowChanged; still notify inspectors for full sync.
		NotifyInspectorsOfCueEdit();
	}

	/// <summary>
	/// Updates the continue-mode button glyph and tooltip from the cue model.
	/// </summary>
	/// <param name="follow">Current continue mode.</param>
	private void UpdateFollowMode(FollowType follow)
	{
		if (_followButton == null) return;

		switch (follow)
		{
			case FollowType.Continue:
				_followButton.Text = "→";
				_followButton.TooltipText = UiLocalizer.T("Auto-continue: next cue starts after post-wait.\nClick to cycle → Auto-follow → None");
				_followButton.Modulate = Colors.White;
				break;
			case FollowType.Follow:
				_followButton.Text = "↳";
				_followButton.TooltipText = UiLocalizer.T("Auto-follow: next cue starts when this cue completes.\nClick to cycle → None → Auto-continue");
				_followButton.Modulate = Colors.White;
				break;
			default:
				_followButton.Text = "";
				_followButton.TooltipText = UiLocalizer.T("Continue mode: None.\nClick to set Auto-continue (→), then Auto-follow (↳).");
				// Keep hit target visible without drawing a permanent glyph.
				_followButton.Modulate = new Color(1, 1, 1, 0.35f);
				break;
		}
	}

	/// <summary>
	/// Pushes shell-row model edits into inspectors (and any other SyncShellInspector listeners).
	/// Name also notifies via NameChanged; cue number / waits / follow need an explicit refresh.
	/// </summary>
	private void NotifyInspectorsOfCueEdit()
	{
		if (_cue == null) return;
		_globalSignals?.EmitSignal(nameof(GlobalSignals.UpdateShellBar), _cue.Id);
		_globalSignals?.EmitSignal(nameof(GlobalSignals.SyncShellInspector));
	}


	private void UpdateName(string name)
	{
		if (!_isEditingName)
		{
			_cueNameLineEdit.Text = name;
		}
	}

	private void UpdateCueNum(string cueNum)
	{
		if (!_isEditingCueNum)
		{
			_cueNumLineEdit.Text = cueNum;
		}
	}

	private void UpdateColor(Color color)
	{
		// Ancestor rails on descendant rows use this cue's colour — refresh every bound row.
		if (_globalData?.Cuelist != null)
			_globalData.Cuelist.RefreshVisibleHierarchyChrome();
		else
		{
			RebuildColorRail();
			RefreshShellChrome();
		}
	}

	public void RelationshipChanged()
	{
		// Virtual rows are reused; nest chrome (chevron, indent, colour rail) must be re-applied
		// on this row and every other bound row that may have changed depth.
		if (_globalData?.Cuelist != null)
			_globalData.Cuelist.RefreshVisibleHierarchyChrome();
		else
			RefreshHierarchyChrome();
	}

	private void CollapsedPressed()
	{
		if (_cue == null) return;
		_cue.Expanded = !_cue.Expanded;
		UpdateCollapseUI();
		RefreshIssueIndicatorFromService();
		_globalData?.Cuelist?.NotifyVirtualStructureChanged();
	}

	/// <summary>
	/// Updates the expand/collapse chevron from <see cref="Cue.ChildCues"/>.
	/// Child visibility is owned by the virtual list (<see cref="CueList.NotifyVirtualStructureChanged"/>),
	/// not by nesting ShellBars inside <see cref="ShellChildContainer"/>.
	/// </summary>
	private void UpdateCollapseUI()
	{
		if (_cue == null || _collapseButton == null) return;

		bool hasChildren = _cue.ChildCues.Count > 0;

		// Always keep the column so parent/child names stay vertically aligned at a given depth.
		_collapseButton.Visible = true;
		_collapseButton.Disabled = !hasChildren;
		_collapseButton.MouseFilter = hasChildren
			? MouseFilterEnum.Stop
			: MouseFilterEnum.Ignore;

		if (hasChildren)
			_collapseButton.Icon = GetThemeIcon(_cue.Expanded ? "Down" : "Right", "AtlasIcons");
		else
			_collapseButton.Icon = null;

		// Nesting is data + VisibleRowIds; never grow this row with a leftover child VBox.
		if (ShellChildContainer != null)
			ShellChildContainer.Visible = false;
	}

	/// <summary>
	/// Public helper to set the expanded/collapsed state for this group (used by Expand All).
	/// </summary>
	/// <param name="expanded">Whether children should be shown.</param>
	public void SetExpanded(bool expanded)
	{
		if (_cue == null || _cue.ChildCues.Count == 0) return;
		if (_cue.Expanded == expanded) return;
		_cue.Expanded = expanded;
		UpdateCollapseUI();
		RefreshIssueIndicatorFromService();
		_globalData?.Cuelist?.NotifyVirtualStructureChanged();
	}

	private void OnMouseEntered()
	{
		// During reorder the drop indicator owns highlight; mouse enter/exit also
		// thrash between the floating preview and shells (blue hover flicker).
		if (IsReorderActive())
		{
			ClearHoverChrome();
			return;
		}

		_hovered = true;
		if (!Selected)
			RefreshShellChrome();
	}
	
	private void OnMouseExited()
	{
		if (!_hovered)
			return;
		_hovered = false;
		if (!Selected)
			RefreshShellChrome();
	}

	/// <summary>
	/// True while the cuelist drag-reorder session is active.
	/// </summary>
	private bool IsReorderActive() =>
		_globalData?.Cuelist?.IsReordering == true;

	/// <summary>
	/// Clears hover chrome without affecting selection. Used when reorder starts/ends.
	/// </summary>
}
