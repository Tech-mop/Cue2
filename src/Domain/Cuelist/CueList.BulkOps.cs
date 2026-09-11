// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Cue2.Domain.Cues;
using Cue2.UI.Shell;
using Cue2.Services;
using Godot;
using Godot.Collections;
using Cue2.UI.Popups;

namespace Cue2.Domain.Cuelist;

/// <summary>
/// Frame-sliced bulk cuelist operations (duplicate / copy / paste / cut / delete).
/// Large forests yield between chunks so the main thread stays responsive and the footer progress bar can update.
/// </summary>
public partial class CueList
{
	/// <summary>
	/// Work item for iterative tree clone / paste shell insertion.
	/// </summary>
	private readonly struct BulkShellJob
	{
		public readonly object Source; // Cue for duplicate, int oldId for paste
		public readonly int NewParentId;
		public readonly VBoxContainer Container;
		public readonly int InsertIndex;
		public readonly bool IsTopLevelRoot;

		public BulkShellJob(object source, int newParentId, VBoxContainer container, int insertIndex, bool isTopLevelRoot)
		{
			Source = source;
			NewParentId = newParentId;
			Container = container;
			InsertIndex = insertIndex;
			IsTopLevelRoot = isTopLevelRoot;
		}
	}

	/// <summary>
	/// Duplicates selection; large forests run over multiple frames with footer progress.
	/// </summary>
	private async Task DuplicateSelectedCuesAsync()
	{
		if (BlockIfShowMode("duplicate cues") || BlockIfBulkBusy("duplicate cues"))
			return;

		var selected = ShellSelection.SelectedCues?.ToList() ?? new List<Cue>();
		if (selected.Count == 0)
		{
			_globalSignals?.EmitSignal(nameof(GlobalSignals.Log), "No cues selected to duplicate.", (int)LogType.Info);
			return;
		}

		var selectedIds = new HashSet<int>(selected.Select(c => c.Id));
		var roots = selected
			.Where(c => c != null && !IsDescendantOfAnySelectedAncestor(c, selectedIds))
			.ToList();

		if (roots.Count == 0)
		{
			_globalSignals?.EmitSignal(nameof(GlobalSignals.Log), "No cues selected to duplicate.", (int)LogType.Info);
			return;
		}

		var visualOrder = GetVisualCueOrderIncludingCollapsed();
		roots = roots
			.OrderBy(c =>
			{
				int idx = visualOrder.IndexOf(c.Id);
				return idx < 0 ? int.MaxValue : idx;
			})
			.ToList();

		var anchor = selected.Last();
		if (anchor?.ShellBar == null || !IsInstanceValid(anchor.ShellBar))
		{
			_globalSignals?.EmitSignal(nameof(GlobalSignals.Log), "Cannot duplicate: selection has no shell UI.", 2);
			return;
		}

		int totalCues = 0;
		foreach (var root in roots)
			totalCues += CountCueTree(root);

		bool asyncPath = totalCues >= BulkAsyncThreshold;
		if (!TryBeginBulkOp("duplicate cues"))
			return;

		try
		{
			if (asyncPath)
			{
				EmitBackgroundProgress("Preparing undo…", 0, totalCues, busy: true);
				// One frame so the footer can paint before the (still-synchronous) history snapshot.
				await YieldOneFrame();
			}

			_globalData?.HistoryManager?.RecordCuelistChange("Duplicate cues");

			var (container, insertIndex, parentId) = ResolveInsertLocation(anchor.Id, DropInsertMode.Below);
			var newTopLevel = new List<Cue>();

			if (asyncPath)
			{
				BeginBulkNotifySuppress();
				try
				{
					var queue = new Queue<BulkShellJob>();
					// clone id → expanded flag from source (applied after children are inserted)
					var expandAfter = new System.Collections.Generic.Dictionary<int, bool>();
					int index = insertIndex;
					foreach (var root in roots)
					{
						queue.Enqueue(new BulkShellJob(root, parentId, container, index, isTopLevelRoot: true));
						index++;
					}

					int done = 0;
					int inFrame = 0;
					while (queue.Count > 0)
					{
						var job = queue.Dequeue();
						var source = (Cue)job.Source;
						var clone = CloneCueShallow(source);
						if (clone == null)
						{
							GD.PrintErr($"CueList:DuplicateSelectedCues - Clone failed for \"{source?.Name}\"");
							continue;
						}
						CreateShellAndInsert(clone, job.Container, job.InsertIndex, job.NewParentId);

						if (job.IsTopLevelRoot)
							newTopLevel.Add(clone);

						if (source.ChildCues.Count > 0)
						{
							int childIndex = 0;
							foreach (int childId in source.ChildCues.ToList())
							{
								var child = FetchCueFromId(childId);
								if (child == null) continue;
								queue.Enqueue(new BulkShellJob(child, clone.Id, job.Container, childIndex, isTopLevelRoot: false));
								childIndex++;
							}

							expandAfter[clone.Id] = source.Expanded;
						}

						clone.CalculateTotalDuration();
						done++;
						inFrame = await YieldBulkFrameIfNeeded(inFrame, done, totalCues, "Duplicating");
					}

					foreach (var kv in expandAfter)
					{
						var parent = FetchCueFromId(kv.Key);
						if (parent == null) continue;
						parent.Expanded = kv.Value;
						parent.ShellBar?.RelationshipChanged();
						parent.ShellBar?.SetExpanded(parent.Expanded);
					}
				}
				finally
				{
					EndBulkNotifySuppress();
				}
			}
			else
			{
				int index = insertIndex;
				foreach (var root in roots)
				{
					var clone = CloneCueTree(root, parentId, container, index);
					if (clone != null)
					{
						newTopLevel.Add(clone);
						index++;
					}
				}
			}

			if (newTopLevel.Count == 0)
			{
				_globalSignals?.EmitSignal(nameof(GlobalSignals.Log), "Duplication produced no cues.", 1);
				return;
			}

			SelectTopLevelBlock(newTopLevel);
			_globalSignals?.EmitSignal(nameof(GlobalSignals.ShellFocused), newTopLevel.Last().Id);
			_globalSignals?.EmitSignal(nameof(GlobalSignals.Log),
				newTopLevel.Count == 1
					? $"Duplicated cue \"{newTopLevel[0].Name}\"."
					: $"Duplicated {newTopLevel.Count} cues ({totalCues} total including children).",
				(int)LogType.Info);
			GD.Print($"CueList:DuplicateSelectedCues - Created {newTopLevel.Count} top-level duplicate(s), {totalCues} total cue(s).");
		}
		catch (Exception ex)
		{
			GD.PrintErr($"CueList:DuplicateSelectedCuesAsync - {ex.Message}");
			_globalSignals?.EmitSignal(nameof(GlobalSignals.Log),
				$"Duplicate failed: {ex.Message}", (int)LogType.Error);
		}
		finally
		{
			EndBulkOp(asyncPath);
		}
	}

	/// <summary>
	/// Copies selection to the in-app clipboard; large trees are cloned over multiple frames.
	/// </summary>
	private async Task CopySelectedCuesAsync()
	{
		if (BlockIfBulkBusy("copy cues"))
			return;

		if (!TryResolveClipboardRoots("copy", out var roots, out int rootCount, out string sampleName, out int totalCues))
			return;

		bool asyncPath = totalCues >= BulkAsyncThreshold;
		if (!TryBeginBulkOp("copy cues"))
			return;

		try
		{
			bool ok = await CaptureRootsToClipboardAsync(roots, totalCues, asyncPath, "Copying");
			if (!ok)
			{
				_globalSignals?.EmitSignal(nameof(GlobalSignals.Log), "Copy produced no cue data.", 1);
				return;
			}

			_globalSignals?.EmitSignal(nameof(GlobalSignals.Log),
				rootCount == 1
					? $"Copied cue \"{sampleName}\"."
					: $"Copied {rootCount} cues ({totalCues} total including children).",
				(int)LogType.Info);
			GD.Print($"CueList:CopySelectedCues - Captured {rootCount} root(s), {totalCues} total cue(s) to clipboard.");
		}
		catch (Exception ex)
		{
			GD.PrintErr($"CueList:CopySelectedCuesAsync - {ex.Message}");
			_globalSignals?.EmitSignal(nameof(GlobalSignals.Log),
				$"Copy failed: {ex.Message}", (int)LogType.Error);
		}
		finally
		{
			EndBulkOp(asyncPath);
		}
	}

	/// <summary>
	/// Cuts selection: clipboard capture then delete, frame-sliced when large.
	/// </summary>
	private async Task CutSelectedCuesAsync()
	{
		if (BlockIfShowMode("cut cues") || BlockIfBulkBusy("cut cues"))
			return;

		if (!TryResolveClipboardRoots("cut", out var roots, out int rootCount, out string sampleName, out int totalCues))
			return;

		bool asyncPath = totalCues >= BulkAsyncThreshold;
		if (!TryBeginBulkOp("cut cues"))
			return;

		try
		{
			bool ok = await CaptureRootsToClipboardAsync(roots, totalCues, asyncPath, "Cutting");
			if (!ok)
			{
				_globalSignals?.EmitSignal(nameof(GlobalSignals.Log), "Cut produced no cue data.", 1);
				return;
			}

			if (asyncPath)
			{
				EmitBackgroundProgress("Preparing undo…", 0, totalCues, busy: true);
				await YieldOneFrame();
			}

			_globalData?.HistoryManager?.RecordCuelistChange("Cut cues");

			int count = await RemoveRootsChunkedAsync(roots, totalCues, asyncPath, "Cutting");

			ShellSelection.SelectedCues.Clear();
			_globalSignals?.EmitSignal(nameof(GlobalSignals.ShellFocused), -1);
			_globalSignals?.EmitSignal(nameof(GlobalSignals.Log),
				rootCount == 1
					? $"Cut cue \"{sampleName}\" ({count} cue(s) removed)."
					: $"Cut {rootCount} cues ({count} total removed).",
				(int)LogType.Info);
			GD.Print($"CueList:CutSelectedCues - Cut {rootCount} root(s), removed {count} cue(s).");
		}
		catch (Exception ex)
		{
			GD.PrintErr($"CueList:CutSelectedCuesAsync - {ex.Message}");
			_globalSignals?.EmitSignal(nameof(GlobalSignals.Log),
				$"Cut failed: {ex.Message}", (int)LogType.Error);
		}
		finally
		{
			EndBulkOp(asyncPath);
		}
	}

	/// <summary>
	/// Pastes clipboard forest; large pastes build data then insert shells over multiple frames.
	/// </summary>
	private async Task PasteCuesAsync()
	{
		if (BlockIfShowMode("paste cues") || BlockIfBulkBusy("paste cues"))
			return;

		if (_clipboardRootIds == null || _clipboardRootIds.Count == 0 ||
		    _clipboardCuesByOldId == null || _clipboardCuesByOldId.Count == 0)
		{
			_globalSignals?.EmitSignal(nameof(GlobalSignals.Log), "Nothing to paste.", (int)LogType.Info);
			return;
		}

		int totalCues = _clipboardCuesByOldId.Count;
		bool asyncPath = totalCues >= BulkAsyncThreshold;
		if (!TryBeginBulkOp("paste cues"))
			return;

		try
		{
			if (asyncPath)
			{
				EmitBackgroundProgress("Preparing undo…", 0, totalCues, busy: true);
				await YieldOneFrame();
			}

			_globalData?.HistoryManager?.RecordCuelistChange("Paste cues");

			var selected = ShellSelection.SelectedCues?.ToList() ?? new List<Cue>();
			Cue anchor = selected.Count > 0 ? selected.Last() : null;
			VBoxContainer container;
			int insertIndex;
			int parentId;
			if (anchor?.ShellBar != null && IsInstanceValid(anchor.ShellBar))
			{
				(container, insertIndex, parentId) = ResolveInsertLocation(anchor.Id, DropInsertMode.Below);
			}
			else
			{
				(container, insertIndex, parentId) = ResolveInsertLocation(-1, DropInsertMode.AtEnd);
			}

			// Snapshot clipboard so concurrent copy cannot corrupt mid-paste
			var clipboardSnapshot = _clipboardCuesByOldId;
			var rootIdsSnapshot = _clipboardRootIds.ToList();

			var oldToNew = new System.Collections.Generic.Dictionary<int, int>();
			var oldToCue = new System.Collections.Generic.Dictionary<int, Cue>();
			var oldChildOrder = new System.Collections.Generic.Dictionary<int, List<int>>();

			// Phase 1: materialize Cue objects (no shells yet)
			var entries = new List<(int oldId, Dictionary data)>();
			foreach (var kv in clipboardSnapshot)
			{
				string key = kv.Key.AsString();
				if (!int.TryParse(key, out int oldId))
					continue;
				if (kv.Value.VariantType != Variant.Type.Dictionary)
					continue;
				entries.Add((oldId, kv.Value.AsGodotDictionary()));
			}

			int done = 0;
			int inFrame = 0;
			foreach (var (oldId, data) in entries)
			{
				var childIds = new List<int>();
				if (data.TryGetValue("ChildCues", out var childVar))
				{
					foreach (var c in childVar.AsGodotArray())
						childIds.Add(c.AsInt32());
				}
				oldChildOrder[oldId] = childIds;

				var applyData = DeepCloneDict(data);
				applyData["ParentId"] = "-1";
				applyData["ChildCues"] = new Godot.Collections.Array();

				var cue = new Cue();
				cue.ApplyFromData(applyData);
				cue.ChildCues = new List<int>();
				cue.ParentId = -1;

				oldToNew[oldId] = cue.Id;
				oldToCue[oldId] = cue;

				done++;
				if (asyncPath)
					inFrame = await YieldBulkFrameIfNeeded(inFrame, done, totalCues * 2, "Pasting");
			}

			if (oldToCue.Count == 0)
			{
				_globalSignals?.EmitSignal(nameof(GlobalSignals.Log), "Paste produced no cues (clipboard empty or invalid).", 1);
				return;
			}

			// Remap Control targets inside the pasted forest
			foreach (var cue in oldToCue.Values)
			{
				foreach (var comp in cue.Components)
				{
					if (comp is not ControlComponent control) continue;
					if (control.TargetCueId < 0) continue;
					if (oldToNew.TryGetValue(control.TargetCueId, out int newTarget))
						control.TargetCueId = newTarget;
				}
				RelinkCueComponents(cue);
			}

			// Phase 2: insert shells in tree order (BFS from roots)
			var newTopLevel = new List<Cue>();
			var parentsNeedingExpand = new HashSet<int>();

			if (asyncPath)
				BeginBulkNotifySuppress();
			try
			{
				var queue = new Queue<BulkShellJob>();
				int index = insertIndex;
				foreach (int rootOldId in rootIdsSnapshot)
				{
					queue.Enqueue(new BulkShellJob(rootOldId, parentId, container, index, isTopLevelRoot: true));
					index++;
				}

				inFrame = 0;
				while (queue.Count > 0)
				{
					var job = queue.Dequeue();
					int oldId = (int)job.Source;
					if (!oldToCue.TryGetValue(oldId, out var cue))
						continue;

					CreateShellAndInsert(cue, job.Container, job.InsertIndex, job.NewParentId);

					if (job.IsTopLevelRoot)
						newTopLevel.Add(cue);

					var children = oldChildOrder.TryGetValue(oldId, out var list) ? list : new List<int>();
					if (children.Count > 0)
					{
						int childIndex = 0;
						foreach (int childOld in children)
						{
							queue.Enqueue(new BulkShellJob(childOld, cue.Id, job.Container, childIndex, isTopLevelRoot: false));
							childIndex++;
						}

						if (clipboardSnapshot.TryGetValue(oldId.ToString(), out var raw) &&
						    raw.VariantType == Variant.Type.Dictionary)
						{
							var d = raw.AsGodotDictionary();
							if (d.TryGetValue("Expanded", out var exp))
								cue.Expanded = exp.AsBool();
						}

						parentsNeedingExpand.Add(cue.Id);
					}

					cue.CalculateTotalDuration();
					done++;
					if (asyncPath)
						inFrame = await YieldBulkFrameIfNeeded(inFrame, done, totalCues * 2, "Pasting");
				}

				foreach (int pid in parentsNeedingExpand)
				{
					var parent = FetchCueFromId(pid);
					if (parent == null) continue;
					parent.ShellBar?.RelationshipChanged();
					parent.ShellBar?.SetExpanded(parent.Expanded);
				}
			}
			finally
			{
				if (asyncPath)
					EndBulkNotifySuppress();
			}

			if (newTopLevel.Count == 0)
			{
				_globalSignals?.EmitSignal(nameof(GlobalSignals.Log), "Paste produced no cues.", 1);
				return;
			}

			SelectTopLevelBlock(newTopLevel);
			_globalSignals?.EmitSignal(nameof(GlobalSignals.ShellFocused), newTopLevel.Last().Id);
			_globalSignals?.EmitSignal(nameof(GlobalSignals.SyncShellInspector));
			_globalSignals?.EmitSignal(nameof(GlobalSignals.Log),
				newTopLevel.Count == 1
					? $"Pasted cue \"{newTopLevel[0].Name}\"."
					: $"Pasted {newTopLevel.Count} cues ({oldToCue.Count} total including children).",
				(int)LogType.Info);
			GD.Print($"CueList:PasteCues - Pasted {newTopLevel.Count} top-level cue(s), {oldToCue.Count} total.");
		}
		catch (Exception ex)
		{
			GD.PrintErr($"CueList:PasteCuesAsync - {ex.Message}");
			_globalSignals?.EmitSignal(nameof(GlobalSignals.Log),
				$"Paste failed: {ex.Message}", (int)LogType.Error);
		}
		finally
		{
			EndBulkOp(asyncPath);
		}
	}

	/// <summary>
	/// Deletes selection; large trees are removed over multiple frames.
	/// </summary>
	private async Task DeleteSelectedCuesAsync()
	{
		if (BlockIfShowMode("delete cues") || BlockIfBulkBusy("delete cues"))
			return;

		var selected = ShellSelection.SelectedCues?.ToList() ?? new List<Cue>();
		if (selected.Count == 0)
		{
			_globalSignals?.EmitSignal(nameof(GlobalSignals.Log), "No cues selected to delete.", (int)LogType.Info);
			return;
		}

		var selectedIds = new HashSet<int>(selected.Select(c => c.Id));
		var roots = selected
			.Where(c => c != null && (c.ParentId == -1 || !selectedIds.Contains(c.ParentId)))
			.ToList();

		if (roots.Count == 0)
		{
			_globalSignals?.EmitSignal(nameof(GlobalSignals.Log), "No cues selected to delete.", (int)LogType.Info);
			return;
		}

		int totalCues = 0;
		foreach (var root in roots)
			totalCues += CountCueTree(root);

		bool asyncPath = totalCues >= BulkAsyncThreshold;
		if (!TryBeginBulkOp("delete cues"))
			return;

		try
		{
			if (asyncPath)
			{
				EmitBackgroundProgress("Preparing undo…", 0, totalCues, busy: true);
				await YieldOneFrame();
			}

			_globalData?.HistoryManager?.RecordCuelistChange("Delete cues");

			int count = await RemoveRootsChunkedAsync(roots, totalCues, asyncPath, "Deleting");

			ShellSelection.SelectedCues.Clear();
			_globalSignals?.EmitSignal(nameof(GlobalSignals.ShellFocused), -1);
			_globalSignals?.EmitSignal(nameof(GlobalSignals.Log),
				count == 1 ? "Deleted 1 cue." : $"Deleted {count} cues.", (int)LogType.Info);
			GD.Print($"CueList:DeleteSelectedCues - Removed {count} cue(s).");
		}
		catch (Exception ex)
		{
			GD.PrintErr($"CueList:DeleteSelectedCuesAsync - {ex.Message}");
			_globalSignals?.EmitSignal(nameof(GlobalSignals.Log),
				$"Delete failed: {ex.Message}", (int)LogType.Error);
		}
		finally
		{
			EndBulkOp(asyncPath);
		}
	}

	// --- Bulk helpers ---

	/// <summary>
	/// Resolves ordered clipboard roots and total cue count from the current selection.
	/// </summary>
	private bool TryResolveClipboardRoots(
		string verb,
		out List<Cue> roots,
		out int rootCount,
		out string sampleName,
		out int totalCues)
	{
		roots = null;
		rootCount = 0;
		sampleName = string.Empty;
		totalCues = 0;

		var selected = ShellSelection.SelectedCues?.ToList() ?? new List<Cue>();
		if (selected.Count == 0)
		{
			_globalSignals?.EmitSignal(nameof(GlobalSignals.Log), $"No cues selected to {verb}.", (int)LogType.Info);
			return false;
		}

		var selectedIds = new HashSet<int>(selected.Select(c => c.Id));
		roots = selected
			.Where(c => c != null && !IsDescendantOfAnySelectedAncestor(c, selectedIds))
			.ToList();

		if (roots.Count == 0)
		{
			_globalSignals?.EmitSignal(nameof(GlobalSignals.Log), $"No cues selected to {verb}.", (int)LogType.Info);
			return false;
		}

		var visualOrder = GetVisualCueOrderIncludingCollapsed();
		roots = roots
			.OrderBy(c =>
			{
				int idx = visualOrder.IndexOf(c.Id);
				return idx < 0 ? int.MaxValue : idx;
			})
			.ToList();

		foreach (var root in roots)
			totalCues += CountCueTree(root);

		rootCount = roots.Count;
		sampleName = roots[0].Name ?? string.Empty;
		return true;
	}

	/// <summary>
	/// Deep-clones root forests into the in-app clipboard, optionally frame-sliced.
	/// </summary>
	private async Task<bool> CaptureRootsToClipboardAsync(List<Cue> roots, int totalCues, bool asyncPath, string progressVerb)
	{
		var cuesByOldId = new Dictionary();
		var flatList = new List<Cue>();
		var seen = new HashSet<int>();
		void WalkCollect(Cue cue)
		{
			if (cue == null || !seen.Add(cue.Id)) return;
			flatList.Add(cue);
			foreach (int childId in cue.ChildCues.ToList())
			{
				var child = FetchCueFromId(childId);
				if (child != null)
					WalkCollect(child);
			}
		}

		foreach (var root in roots)
			WalkCollect(root);

		if (flatList.Count == 0)
			return false;

		int done = 0;
		int inFrame = 0;
		foreach (var cue in flatList)
		{
			cuesByOldId[cue.Id.ToString()] = DeepCloneDict(cue.GetData());
			done++;
			if (asyncPath)
				inFrame = await YieldBulkFrameIfNeeded(inFrame, done, Math.Max(totalCues, flatList.Count), progressVerb);
		}

		_clipboardRootIds = roots.Select(r => r.Id).ToList();
		_clipboardCuesByOldId = cuesByOldId;
		return true;
	}

	/// <summary>
	/// Removes root trees; when <paramref name="asyncPath"/> is true, yields every few removals.
	/// </summary>
	private async Task<int> RemoveRootsChunkedAsync(List<Cue> roots, int totalCues, bool asyncPath, string progressVerb)
	{
		// Flatten post-order so children leave before parents (matches RemoveCueRecursive semantics)
		var toRemove = new List<Cue>();
		void WalkPost(Cue cue)
		{
			if (cue == null) return;
			foreach (int childId in cue.ChildCues.ToList())
			{
				var child = FetchCueFromId(childId);
				if (child != null)
					WalkPost(child);
			}
			toRemove.Add(cue);
		}

		foreach (var root in roots)
			WalkPost(root);

		int count = 0;
		int inFrame = 0;
		if (asyncPath)
			BeginBulkNotifySuppress();
		try
		{
			foreach (var cue in toRemove)
			{
				// RemoveCue already drops from parent ChildCues; children already processed
				RemoveCue(cue);
				count++;
				if (asyncPath)
					inFrame = await YieldBulkFrameIfNeeded(inFrame, count, totalCues, progressVerb);
			}
		}
		finally
		{
			if (asyncPath)
				EndBulkNotifySuppress();
		}

		return count;
	}

	/// <summary>
	/// Counts <paramref name="root"/> plus all descendants.
	/// </summary>
	private int CountCueTree(Cue root)
	{
		if (root == null) return 0;
		int n = 1;
		foreach (int childId in root.ChildCues)
		{
			var child = FetchCueFromId(childId);
			if (child != null)
				n += CountCueTree(child);
		}
		return n;
	}

	/// <summary>
	/// Selects the given top-level block, replacing the current selection.
	/// </summary>
	private void SelectTopLevelBlock(List<Cue> newTopLevel)
	{
		foreach (var c in ShellSelection.SelectedCues.ToList())
			c.ShellBar?.Deselect();
		ShellSelection.SelectedCues.Clear();
		foreach (var c in newTopLevel)
		{
			if (c.ShellBar != null)
			{
				c.ShellBar.Select();
				ShellSelection.SelectedCues.Add(c);
			}
		}
	}

	/// <summary>
	/// Marks a bulk op as in progress. Returns false if one is already running.
	/// </summary>
	private bool TryBeginBulkOp(string actionLabel)
	{
		if (_bulkOpInProgress)
		{
			BlockIfBulkBusy(actionLabel);
			return false;
		}
		_bulkOpInProgress = true;
		BulkOpLabel = actionLabel ?? string.Empty;
		return true;
	}

	/// <summary>
	/// Ends a bulk op, restores count notifications, and completes footer progress when used.
	/// </summary>
	private void EndBulkOp(bool showedProgress)
	{
		_bulkOpInProgress = false;
		BulkOpLabel = string.Empty;
		// Ensure suppress is fully cleared even if an exception left depth non-zero
		if (_bulkNotifySuppressDepth != 0 || _virtualRefreshSuppress != 0)
		{
			_bulkNotifySuppressDepth = 0;
			_virtualRefreshSuppress = 0;
			NotifyVirtualStructureChanged();
		}

		if (showedProgress)
		{
			EmitBackgroundProgress("Done", 1, 1, busy: false);
			_globalSignals?.EmitSignal(nameof(GlobalSignals.BackgroundProcessCompleted));
		}
	}

	private void BeginBulkNotifySuppress()
	{
		_bulkNotifySuppressDepth++;
		BeginVirtualRefreshSuppress();
	}

	private void EndBulkNotifySuppress()
	{
		_bulkNotifySuppressDepth = Math.Max(0, _bulkNotifySuppressDepth - 1);
		EndVirtualRefreshSuppress();
	}

	/// <summary>
	/// Emits footer background progress for cuelist bulk work.
	/// </summary>
	private void EmitBackgroundProgress(string statusText, int completed, int total, bool busy = true)
	{
		if (_globalSignals == null) return;
		float percent = total <= 0 ? 100f : Math.Clamp(completed * 100f / total, 0f, 100f);
		string detail = total > 0 ? $"{completed}/{total} cues" : string.Empty;
		string label = string.IsNullOrEmpty(statusText)
			? $"{percent:F0}%"
			: (statusText.Contains('%', StringComparison.Ordinal) || statusText.EndsWith("…", StringComparison.Ordinal)
				? statusText
				: $"{statusText} {percent:F0}%");

		_globalSignals.EmitSignal(nameof(GlobalSignals.BackgroundProcessProgress),
			percent, busy, label, detail, completed, Math.Max(total, completed));
	}

	/// <summary>
	/// Yields a process frame after every <see cref="BulkItemsPerFrame"/> items so the UI can paint.
	/// </summary>
	/// <returns>Updated per-frame item counter (0 after a yield).</returns>
	private async Task<int> YieldBulkFrameIfNeeded(int inFrame, int completed, int total, string verb)
	{
		inFrame++;
		if (inFrame < BulkItemsPerFrame)
			return inFrame;

		EmitBackgroundProgress(verb, completed, total, busy: true);
		await YieldOneFrame();
		return 0;
	}

	/// <summary>Awaits one scene-tree process frame (no-op if tree is unavailable).</summary>
	private async Task YieldOneFrame()
	{
		var tree = GetTree();
		if (tree != null)
			await ToSignal(tree, SceneTree.SignalName.ProcessFrame);
	}
}
