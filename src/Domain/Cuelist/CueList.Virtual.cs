// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using Cue2.Domain.Cues;
using Cue2.UI.Shell;
using Cue2.Services;
using Godot;

namespace Cue2.Domain.Cuelist;

/// <summary>
/// Flattened virtual cuelist: Cue models are always complete; ShellBars exist only for the
/// viewport plus overscan and are recycled on scroll. Nesting is data (ParentId / ChildCues /
/// <see cref="RootOrder"/>), not nested VBoxes.
/// </summary>
public partial class CueList
{
	/// <summary>Top-level cue ids in document order (source of truth; not container children).</summary>
	internal readonly List<int> RootOrder = new();

	/// <summary>Currently visible cue ids (roots + expanded descendants), visual order.</summary>
	internal readonly List<int> VisibleRowIds = new();

	/// <summary>Extra rows bound above and below the viewport.</summary>
	private const int VirtualOverscanRows = 8;

	private readonly List<ShellBar> _shellPool = new();
	/// <summary>
	/// In-tree parent for recycled shells. Parentless <c>RemoveChild</c> rows leak
	/// CanvasItem / LineEdit RIDs on app close (scroll then quit).
	/// </summary>
	private Node _virtualPoolHolder;
	private Control _virtualTopSpacer;
	private Control _virtualBottomSpacer;
	private bool _virtualScrollWired;
	private int _virtualBoundFirst = -1;
	private int _virtualBoundLast = -1;
	private int _virtualRefreshSuppress;

	/// <summary>Row height used for spacers and hit testing.</summary>
	internal float VirtualRowHeight => Mathf.Max(1f, ShellColumnLayout.RowMinHeight);

	/// <summary>
	/// Inserts <paramref name="cue"/> into the model at <paramref name="parentId"/> /
	/// <paramref name="insertIndex"/> without requiring a live shell.
	/// </summary>
	internal void InsertCueInModel(Cue cue, int parentId, int insertIndex)
	{
		if (cue == null)
			return;

		DetachCueFromModel(cue);
		cue.ParentId = parentId;
		var list = GetSiblingIdList(parentId);
		if (list == null)
		{
			cue.ParentId = -1;
			list = RootOrder;
		}

		int idx = insertIndex;
		if (idx < 0 || idx > list.Count)
			idx = list.Count;
		if (!list.Contains(cue.Id))
			list.Insert(idx, cue.Id);
		else
		{
			list.Remove(cue.Id);
			if (idx > list.Count)
				idx = list.Count;
			list.Insert(idx, cue.Id);
		}
	}

	/// <summary>
	/// Removes <paramref name="cue"/> from <see cref="RootOrder"/> or its parent's ChildCues.
	/// Does not remove it from <see cref="CueIndex"/>.
	/// </summary>
	internal void DetachCueFromModel(Cue cue)
	{
		if (cue == null)
			return;
		if (cue.ParentId == -1)
		{
			RootOrder.Remove(cue.Id);
			return;
		}

		var parent = FetchCueFromId(cue.ParentId);
		parent?.ChildCues.Remove(cue.Id);
		cue.ParentId = -1;
	}

	/// <summary>
	/// Sibling id list for a parent (-1 = top-level <see cref="RootOrder"/>).
	/// </summary>
	internal List<int> GetSiblingIdList(int parentId)
	{
		if (parentId < 0)
			return RootOrder;
		return FetchCueFromId(parentId)?.ChildCues;
	}

	/// <summary>
	/// Rebuilds <see cref="VisibleRowIds"/> from <see cref="RootOrder"/> and expand state.
	/// </summary>
	internal void RebuildVisibleRowIds()
	{
		VisibleRowIds.Clear();
		foreach (int id in RootOrder)
			AppendVisibleRecursive(id);
	}

	private void AppendVisibleRecursive(int cueId)
	{
		if (!CueIndex.TryGetValue(cueId, out var cue) || cue == null)
			return;
		VisibleRowIds.Add(cueId);
		if (!cue.Expanded || cue.ChildCues.Count == 0)
			return;
		foreach (int childId in cue.ChildCues)
			AppendVisibleRecursive(childId);
	}

	/// <summary>
	/// Visual order of cue ids. When <paramref name="includeCollapsed"/> is true, walks every
	/// descendant regardless of <see cref="Cue.Expanded"/>.
	/// </summary>
	internal List<int> GetModelVisualOrder(bool includeCollapsed)
	{
		var result = new List<int>(CueIndex?.Count ?? 0);
		void Walk(int id)
		{
			if (CueIndex == null || !CueIndex.TryGetValue(id, out var cue) || cue == null)
				return;
			result.Add(id);
			if (cue.ChildCues.Count == 0)
				return;
			if (!includeCollapsed && !cue.Expanded)
				return;
			foreach (int childId in cue.ChildCues)
				Walk(childId);
		}

		foreach (int id in RootOrder)
			Walk(id);
		return result;
	}

	/// <summary>Visible cues in list order (collapsed descendants omitted).</summary>
	internal List<Cue> GetVisibleCues()
	{
		var list = new List<Cue>(VisibleRowIds.Count);
		foreach (int id in VisibleRowIds)
		{
			var cue = FetchCueFromId(id);
			if (cue != null)
				list.Add(cue);
		}
		return list;
	}

	/// <summary>Index in <see cref="VisibleRowIds"/>, or -1.</summary>
	internal int GetVisibleRowIndex(int cueId)
	{
		return VisibleRowIds.IndexOf(cueId);
	}

	/// <summary>
	/// Rebuilds the visible id list and rebinds the shell pool. No-op while suppressed.
	/// </summary>
	internal void NotifyVirtualStructureChanged()
	{
		if (_virtualRefreshSuppress > 0)
			return;
		RebuildVisibleRowIds();
		SyncVirtualViewport();
		NotifyTotalCuesChanged();
	}

	/// <summary>Defers <see cref="NotifyVirtualStructureChanged"/> across a bulk mutation.</summary>
	internal void BeginVirtualRefreshSuppress()
	{
		_virtualRefreshSuppress++;
	}

	/// <summary>Ends a suppress started by <see cref="BeginVirtualRefreshSuppress"/>.</summary>
	internal void EndVirtualRefreshSuppress()
	{
		_virtualRefreshSuppress = Math.Max(0, _virtualRefreshSuppress - 1);
		if (_virtualRefreshSuppress == 0)
			NotifyVirtualStructureChanged();
	}

	/// <summary>
	/// Re-applies nest indent, collapse chevron, and ancestor colour rails on every bound row.
	/// Needed after ParentId / ChildCues change because the virtual pool reuses ShellBars
	/// without calling <see cref="ShellBar.SetCue"/>.
	/// </summary>
	internal void RefreshVisibleHierarchyChrome()
	{
		if (_cueContainer == null || !IsInstanceValid(_cueContainer))
			return;
		foreach (var child in _cueContainer.GetChildren())
		{
			if (child is ShellBar sb && IsInstanceValid(sb))
				sb.RefreshHierarchyChrome();
		}
	}

	/// <summary>
	/// Binds pooled ShellBars to the viewport (plus overscan) and sizes spacers so scroll
	/// height matches the full visible list.
	/// </summary>
	internal void SyncVirtualViewport()
	{
		if (_cueContainer == null || !IsInstanceValid(_cueContainer))
			return;

		EnsureVirtualChrome();
		WireVirtualScroll();

		float rowH = VirtualRowHeight;
		int count = VisibleRowIds.Count;
		int viewH = 0;
		int scrollY = 0;
		if (_cueListScroll != null && IsInstanceValid(_cueListScroll))
		{
			viewH = Mathf.Max(0, (int)_cueListScroll.Size.Y);
			scrollY = _cueListScroll.ScrollVertical;
		}

		int first = 0;
		int last = count;
		if (rowH > 0f && viewH > 0)
		{
			first = Mathf.Max(0, (int)(scrollY / rowH) - VirtualOverscanRows);
			last = Mathf.Min(count, (int)Math.Ceiling((scrollY + viewH) / rowH) + VirtualOverscanRows);
		}

		_virtualBoundFirst = first;
		_virtualBoundLast = last;

		if (_virtualTopSpacer != null)
			_virtualTopSpacer.CustomMinimumSize = new Vector2(0, first * rowH);
		if (_virtualBottomSpacer != null)
			_virtualBottomSpacer.CustomMinimumSize = new Vector2(0, Math.Max(0, count - last) * rowH);

		var desired = new HashSet<int>();
		for (int i = first; i < last; i++)
			desired.Add(VisibleRowIds[i]);

		var bound = new Dictionary<int, ShellBar>();
		foreach (var child in _cueContainer.GetChildren())
		{
			if (child is not ShellBar sb)
				continue;
			int id = sb.CueId;
			if (id >= 0 && desired.Contains(id) && !bound.ContainsKey(id))
				bound[id] = sb;
			else
				ReleaseVirtualShell(sb);
		}

		bool lightBind = _globalData?.IsSessionLoading == true;
		int insertAt = _virtualTopSpacer != null && _virtualTopSpacer.GetParent() == _cueContainer
			? _virtualTopSpacer.GetIndex() + 1
			: 0;

		for (int i = first; i < last; i++)
		{
			int id = VisibleRowIds[i];
			if (!bound.TryGetValue(id, out var shell))
			{
				shell = BindVirtualShell(id, lightBind);
				if (shell == null)
					continue;
			}

			if (shell.GetParent() != _cueContainer)
				_cueContainer.AddChild(shell);
			int idx = shell.GetIndex();
			if (idx != insertAt)
				_cueContainer.MoveChild(shell, insertAt);
			shell.SetZebraIndex(i);
			// Reused rows keep their previous cue binding — nest chrome (indent / chevron /
			// ancestor colour rails) must still update when ParentId or ChildCues change.
			shell.RefreshHierarchyChrome();
			insertAt++;
		}

		UpdateScrollEndPadding();
	}

	/// <summary>
	/// Scrolls so <paramref name="cueId"/> is centered in the cuelist viewport, then rebinds.
	/// </summary>
	internal void ScrollToCueId(int cueId)
	{
		if (_cueListScroll == null || !IsInstanceValid(_cueListScroll))
			return;
		int vis = GetVisibleRowIndex(cueId);
		if (vis < 0)
		{
			// Hidden in a collapsed group — expand ancestors then retry.
			ExpandAncestors(cueId);
			RebuildVisibleRowIds();
			vis = GetVisibleRowIndex(cueId);
			if (vis < 0)
				return;
		}

		float rowH = VirtualRowHeight;
		float viewH = _cueListScroll.Size.Y;
		int next = Mathf.RoundToInt(vis * rowH - (viewH - rowH) * 0.5f);
		var vBar = _cueListScroll.GetVScrollBar();
		if (vBar != null)
			next = (int)Mathf.Clamp(next, vBar.MinValue, vBar.MaxValue);
		else
			next = Mathf.Max(0, next);
		_cueListScroll.ScrollVertical = next;
		SyncVirtualViewport();
	}

	/// <summary>
	/// Global rect of the row at <paramref name="visualIndex"/> in the flattened list
	/// (on- or off-screen). Used by box-select so a scrolled origin cue still intersects.
	/// </summary>
	internal Rect2 GetVisibleRowGlobalRect(int visualIndex)
	{
		if (visualIndex < 0 || visualIndex >= VisibleRowIds.Count || _cueContainer == null)
			return new Rect2();

		float rowH = VirtualRowHeight;
		var origin = _cueContainer.GlobalPosition;
		// Container top is the start of the virtual list (top spacer = rows 0..first-1).
		return new Rect2(origin.X, origin.Y + visualIndex * rowH, _cueContainer.Size.X, rowH);
	}

	/// <summary>Frees pooled shells and spacers' bound state. Does not QueueFree the Cue models.</summary>
	internal void ClearVirtualState()
	{
		if (_cueContainer != null && IsInstanceValid(_cueContainer))
		{
			foreach (var child in _cueContainer.GetChildren())
			{
				if (child is ShellBar sb)
					ReleaseVirtualShell(sb, returnToPool: false);
			}
		}

		foreach (var sb in _shellPool)
		{
			if (sb != null && IsInstanceValid(sb))
				sb.QueueFree();
		}
		_shellPool.Clear();
		if (_virtualPoolHolder != null && IsInstanceValid(_virtualPoolHolder))
			_virtualPoolHolder.QueueFree();
		_virtualPoolHolder = null;
		RootOrder.Clear();
		VisibleRowIds.Clear();
		_virtualBoundFirst = -1;
		_virtualBoundLast = -1;
	}

	private void EnsureVirtualChrome()
	{
		if (_cueContainer == null)
			return;
		if (_virtualTopSpacer == null || !IsInstanceValid(_virtualTopSpacer))
		{
			_virtualTopSpacer = new Control
			{
				Name = "VirtualTopSpacer",
				MouseFilter = Control.MouseFilterEnum.Ignore
			};
			_cueContainer.AddChild(_virtualTopSpacer);
			_cueContainer.MoveChild(_virtualTopSpacer, 0);
		}

		if (_virtualBottomSpacer == null || !IsInstanceValid(_virtualBottomSpacer))
		{
			_virtualBottomSpacer = new Control
			{
				Name = "VirtualBottomSpacer",
				MouseFilter = Control.MouseFilterEnum.Ignore
			};
			_cueContainer.AddChild(_virtualBottomSpacer);
		}
		else if (_virtualBottomSpacer.GetParent() == _cueContainer)
		{
			_cueContainer.MoveChild(_virtualBottomSpacer, _cueContainer.GetChildCount() - 1);
		}
	}

	private void WireVirtualScroll()
	{
		if (_virtualScrollWired || _cueListScroll == null || !IsInstanceValid(_cueListScroll))
			return;
		_virtualScrollWired = true;
		_cueListScroll.Resized += OnVirtualScrollChanged;
		var vBar = _cueListScroll.GetVScrollBar();
		if (vBar != null)
			vBar.ValueChanged += OnVirtualScrollValueChanged;
	}

	private void OnVirtualScrollChanged()
	{
		SyncVirtualViewport();
	}

	private void OnVirtualScrollValueChanged(double value)
	{
		SyncVirtualViewport();
	}

	private ShellBar BindVirtualShell(int cueId, bool lightBind)
	{
		var cue = FetchCueFromId(cueId);
		if (cue == null)
			return null;

		ShellBar shell;
		if (_shellPool.Count > 0)
		{
			shell = _shellPool[^1];
			_shellPool.RemoveAt(_shellPool.Count - 1);
		}
		else
		{
			shell = _shellBarPackedScene.Instantiate<ShellBar>();
			if (!shell.HasMeta("virtual_wired"))
			{
				shell.MouseEntered += () => OnMouseEntered(shell);
				shell.SetMeta("virtual_wired", true);
			}
		}

		ReparentVirtualShell(shell, _cueContainer);
		shell.Visible = true;
		shell.ProcessMode = ProcessModeEnum.Inherit;
		shell.MouseFilter = Control.MouseFilterEnum.Stop;

		shell.SetCue(cue, skipIssueLookup: lightBind, deferChrome: lightBind);
		cue.ShellBar = shell;
		shell.Set("CueId", cue.Id);
		shell.ApplyTreeIndent();
		if (ShellSelection.SelectedCues != null && ShellSelection.SelectedCues.Contains(cue))
			shell.Select();
		else
			shell.Deselect();
		return shell;
	}

	private void ReleaseVirtualShell(ShellBar shell, bool returnToPool = true)
	{
		if (shell == null || !IsInstanceValid(shell))
			return;

		shell.Deselect();
		shell.UnbindCue();

		if (returnToPool)
		{
			var holder = EnsureVirtualPoolHolder();
			ReparentVirtualShell(shell, holder);
			shell.Visible = false;
			shell.ProcessMode = ProcessModeEnum.Disabled;
			shell.MouseFilter = Control.MouseFilterEnum.Ignore;
			_shellPool.Add(shell);
		}
		else
		{
			var parent = shell.GetParent();
			parent?.RemoveChild(shell);
			shell.QueueFree();
		}
	}

	/// <summary>Hidden in-tree bin so recycled shells are freed with the scene.</summary>
	private Node EnsureVirtualPoolHolder()
	{
		if (_virtualPoolHolder != null && IsInstanceValid(_virtualPoolHolder))
			return _virtualPoolHolder;

		_virtualPoolHolder = new Node { Name = "VirtualShellPool" };
		AddChild(_virtualPoolHolder);
		return _virtualPoolHolder;
	}

	private static void ReparentVirtualShell(ShellBar shell, Node newParent)
	{
		if (shell == null || newParent == null)
			return;
		var current = shell.GetParent();
		if (current == newParent)
			return;
		if (current != null)
			shell.Reparent(newParent);
		else
			newParent.AddChild(shell);
	}

	private void ExpandAncestors(int cueId)
	{
		var cue = FetchCueFromId(cueId);
		int guard = 0;
		while (cue != null && cue.ParentId >= 0 && guard++ < 64)
		{
			var parent = FetchCueFromId(cue.ParentId);
			if (parent == null)
				break;
			parent.Expanded = true;
			cue = parent;
		}
	}
}
