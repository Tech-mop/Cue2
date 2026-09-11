// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

using Godot;
using Godot.Collections;

using Cue2.Domain.Connections;
using Cue2.Domain.Cues;
using Cue2.Domain.Library;
using Cue2.UI.Shell;
using Cue2.Services;
using Cue2.UI.Popups;
using Cue2.UI.Utilities;

// This script is attached to the cuelist in main UI
// Originator
namespace Cue2.Domain.Cuelist;

/// <summary>
/// Manages the main cue list UI, including creation, removal, drag-and-drop reordering
/// (with support for nesting/grouping), box multi-select, and save/load of cue hierarchy and order.
/// Partial: Create/group/import/delete/reorder shells, history apply hooks
/// </summary>
public partial class CueList
{
	public Cue CreateCue(Dictionary data) // Create a cue from data
	{
		var newCue = new Cue(data);
		if (!CueIndex.ContainsKey(newCue.Id))
			CueIndex.Add(newCue.Id, newCue);
		else
			CueIndex[newCue.Id] = newCue;

		// History may have already restored parent ChildCues / RootOrder — do not reshuffle.
		var siblings = GetSiblingIdList(newCue.ParentId) ?? RootOrder;
		if (siblings != null && !siblings.Contains(newCue.Id))
			InsertCueInModel(newCue, newCue.ParentId, siblings.Count);

		NotifyVirtualStructureChanged();
		return newCue;
	}

	/// <summary>
	/// Creates a default new cue and adds it to the cuelist (wired to Add button / signal).
	/// Shell properties come from show-scoped <see cref="Settings"/> cue defaults.
	/// When <see cref="Settings.SelectNewCues"/> is enabled, the new cue becomes the selection.
	/// </summary>
	public void CreateCue()
	{
		if (BlockIfShowMode("create cues") || BlockIfBulkBusy("create cues")) return;
		_globalData?.HistoryManager?.RecordCuelistChange("Create cue");
		var newCue = new Cue();
		_globalData?.Settings?.ApplyShellDefaults(newCue);
		AddCue(newCue);
		MaybeSelectNewCue(newCue);
	}

	/// <summary>
	/// Selects and focuses <paramref name="cue"/> when the show setting
	/// <see cref="Settings.SelectNewCues"/> is enabled; otherwise leaves selection unchanged.
	/// </summary>
	/// <param name="cue">Cue that was just created.</param>
	/// <remarks>
	/// <see cref="ShellSelection.SelectIndividualShell"/> already emits <c>ShellFocused</c>
	/// via <see cref="ShellSelection.AddSelection"/> — do not emit it again, or audio/video
	/// inspectors can cancel their first async load (generation bump) and skip
	/// <c>PopulateOutputOptions</c>, leaving the Output dropdown on "No output".
	/// </remarks>
	private void MaybeSelectNewCue(Cue cue)
	{
		if (cue == null) return;
		if (_globalData?.Settings?.SelectNewCues != true) return;

		// Covered by the create/import cuelist history step — do not push a separate selection undo.
		_globalData.ShellSelection?.SelectIndividualShell(cue, recordHistory: false);
	}

	private void _syncHotkeys()
	{
		string createHotkey = GlobalData.ParseHotkey("CreateCue");
		string expandHotkey = GlobalData.ParseHotkey("ToggleExpandAll");

		_addCueButton.TooltipText = UiLocalizer.WithHotkey(
			"Add a new cue.\nInserts below selection.", createHotkey);
		_expandAllButton.TooltipText = UiLocalizer.WithHotkey(
			"Expand/Collapse all groups.", expandHotkey);
	}

	/// <summary>
	/// Groups the currently selected cues under a newly created group cue.
	/// If no cues are selected, emits an error log.
	/// The new group is inserted at the position of the first selected cue (preserving its nesting level),
	/// and all selected cues are moved to become direct children of the group.
	/// </summary>
	public void GroupSelectedCues()
	{
		if (BlockIfShowMode("group cues") || BlockIfBulkBusy("group cues")) return;
		var selected = ShellSelection.SelectedCues?.ToList() ?? new List<Cue>();
		if (selected.Count == 0)
		{
			_globalSignals.EmitSignal(nameof(GlobalSignals.Log),
				"CueList:GroupSelectedCues - No cues selected. Select one or more cues then press the Group shortcut.", (int)LogType.Error);
			return;
		}

		_globalData?.HistoryManager?.RecordCuelistChange("Group cues");

		// Work on a snapshot to avoid issues if selection changes during the operation
		var toGroup = selected;

		// Determine insertion point from the first selected cue's current location.
		// The group will be inserted as a sibling at the same level as the anchor.
		Cue anchor = toGroup[0];
		int newGroupParentId = anchor.ParentId;
		var siblings = GetSiblingIdList(newGroupParentId) ?? RootOrder;
		int insertIndex = siblings.IndexOf(anchor.Id);
		if (insertIndex < 0)
			insertIndex = siblings.Count;

		// Create the wrapping group cue (shell defaults, then override display name)
		var groupCue = new Cue();
		_globalData?.Settings?.ApplyShellDefaults(groupCue);
		groupCue.Name = $"Group ({toGroup.Count} cues)";
		groupCue.CueNum = groupCue.Id.ToString();
		groupCue.Expanded = true;

		BeginVirtualRefreshSuppress();
		try
		{
			if (!CueIndex.ContainsKey(groupCue.Id))
				CueIndex.Add(groupCue.Id, groupCue);
			InsertCueInModel(groupCue, newGroupParentId, insertIndex);

			var selectedIds = new HashSet<int>(toGroup.Select(c => c.Id));
			var topLevelToMove = toGroup
				.Where(c => c.ParentId == -1 || !selectedIds.Contains(c.ParentId))
				.ToList();

			foreach (var cue in topLevelToMove)
			{
				if (cue == null)
					continue;
				InsertCueInModel(cue, groupCue.Id, groupCue.ChildCues.Count);
			}
		}
		finally
		{
			EndVirtualRefreshSuppress();
		}

		// Select the group cue (replacing the previous multi-selection).
		// Covered by the group cuelist history step.
		_globalData.ShellSelection.SelectIndividualShell(groupCue, recordHistory: false);

		// Recalculate durations (children first conceptually, then group)
		groupCue.CalculateTotalDuration();

		_globalSignals.EmitSignal(nameof(GlobalSignals.Log),
			$"CueList:GroupSelectedCues - Created group containing {toGroup.Count} cue(s).", (int)LogType.Info);

		GD.Print($"CueList:GroupSelectedCues - Group cue {groupCue.Id} now contains {groupCue.ChildCues.Count} children.");
	}

	/// <summary>
	/// Creates one or more cues from dropped media files and inserts them according to the given parameters.
	/// This is the main entry point for file drag-and-drop cue creation.
	/// Supports single/multiple files, audio/video/images, group wrapping, parent-per-file, and precise insert positions.
	/// </summary>
	/// <param name="files">Full paths to valid media files.</param>
	/// <param name="targetCueId">If dropping relative to a specific cue/shell, its ID; otherwise -1.</param>
	/// <param name="insertMode">Desired position relative to target (ignored or treated as AtEnd if no target).</param>
	/// <param name="multiFileMode">
	/// Structure for multiple files. Single-file drops always create one media cue
	/// (modes other than <see cref="MultiFileDropMode.SeparateCues"/> are ignored when <paramref name="files"/> has one path).
	/// </param>
	public void CreateCuesFromDroppedFiles(string[] files, int targetCueId, DropInsertMode insertMode, MultiFileDropMode multiFileMode = MultiFileDropMode.SeparateCues)
	{
		if (BlockIfShowMode("create cues from dropped files") || BlockIfBulkBusy("create cues from dropped files")) return;
		if (files == null || files.Length == 0) return;

		_globalData?.HistoryManager?.RecordCuelistChange("Import media cues");

		var mediaEngine = GetNodeOrNull<MediaEngine>("/root/MediaEngine");
		var newCues = new List<Cue>();
		Cue groupCue = null;
		Cue firstParentForFocus = null;

		// Determine insertion base location once (model sibling index, not virtual container index).
		var (targetContainer, baseInsertIndex, parentIdForNew) = ResolveInsertLocation(targetCueId, insertMode);

		bool useSingleGroup = multiFileMode == MultiFileDropMode.WrapInOneGroup && files.Length > 1;
		bool useParentPerFile = multiFileMode == MultiFileDropMode.ParentPerFile && files.Length > 1;

		BeginVirtualRefreshSuppress();
		try
		{
			if (useSingleGroup)
			{
				// Create a wrapper group cue first (shell defaults, then override display name)
				groupCue = new Cue();
				_globalData?.Settings?.ApplyShellDefaults(groupCue);
				groupCue.Name = $"Group ({files.Length} files)";
				groupCue.CueNum = groupCue.Id.ToString();
				groupCue.Expanded = true;

				CreateShellAndInsert(groupCue, targetContainer, baseInsertIndex, parentIdForNew);
				newCues.Add(groupCue);

				parentIdForNew = groupCue.Id;
			}

			int currentIndex = baseInsertIndex;

			foreach (string filePath in files)
			{
				if (!File.Exists(filePath)) continue;

				string baseName = Path.GetFileNameWithoutExtension(filePath);
				if (string.IsNullOrWhiteSpace(baseName))
					baseName = null;

				// Optional empty parent for ParentPerFile mode (2 cues per file)
				Cue perFileParent = null;
				int mediaInsertIndex;
				int mediaParentId = parentIdForNew;

				if (useParentPerFile)
				{
					perFileParent = new Cue();
					_globalData?.Settings?.ApplyShellDefaults(perFileParent);
					perFileParent.Name = baseName ?? $"Cue {perFileParent.Id}";
					perFileParent.CueNum = perFileParent.Id.ToString();
					perFileParent.Expanded = true;

					CreateShellAndInsert(perFileParent, targetContainer, currentIndex, parentIdForNew);
					newCues.Add(perFileParent);
					firstParentForFocus ??= perFileParent;

					currentIndex++;
					mediaInsertIndex = perFileParent.ChildCues.Count;
					mediaParentId = perFileParent.Id;
				}
				else if (useSingleGroup)
				{
					mediaInsertIndex = groupCue?.ChildCues.Count ?? 0;
				}
				else
				{
					mediaInsertIndex = currentIndex;
				}

				var cue = new Cue();
				_globalData?.Settings?.ApplyShellDefaults(cue);
				cue.Name = baseName ?? $"Cue {cue.Id}";
				cue.CueNum = cue.Id.ToString();

				// Add the appropriate component
				string ext = Path.GetExtension(filePath).ToLowerInvariant();
				bool isAudio = GlobalData.AudioFileFilters.Any(e => e.TrimStart('*').Equals(ext, StringComparison.OrdinalIgnoreCase));
				bool isVideoOrImage = GlobalData.VideoFileFilters.Any(e => e.TrimStart('*').Equals(ext, StringComparison.OrdinalIgnoreCase)) ||
				                       GlobalData.ImageFileFilters.Any(e => e.TrimStart('*').Equals(ext, StringComparison.OrdinalIgnoreCase));

				// Store show-relative path immediately when media backup is enabled
				string pathToStore = ResolveMediaPathForNewCue(filePath, isAudio);

				if (isAudio)
				{
					cue.AddAudioComponent(pathToStore);
				}
				else if (isVideoOrImage)
				{
					cue.AddVideoComponent(pathToStore);
					// Video may contain audio - we will discover on metadata
				}
				else
				{
					// Should have been filtered; drop orphan empty parent if we already inserted one
					if (perFileParent != null)
					{
						currentIndex = Math.Max(0, currentIndex - 1);
						RemoveCue(perFileParent);
						newCues.Remove(perFileParent);
						if (firstParentForFocus == perFileParent)
							firstParentForFocus = null;
					}
					continue;
				}

				CreateShellAndInsert(cue, targetContainer, mediaInsertIndex, mediaParentId);

				// Advance sibling insert index for separate (non-group, non parent-per-file) mode
				if (!useSingleGroup && !useParentPerFile)
					currentIndex++;

				newCues.Add(cue);

				// Kick off async metadata + waveform (fire and forget with logging)
				_ = ApplyMetadataToNewCueAsync(cue, filePath, mediaEngine);

				// Parent duration depends on child media
				if (perFileParent != null)
					perFileParent.CalculateTotalDuration();
			}
		}
		finally
		{
			EndVirtualRefreshSuppress();
		}

		if (newCues.Count == 0)
		{
			_globalSignals?.EmitSignal(nameof(GlobalSignals.Log), "CueList:CreateCuesFromDroppedFiles - No cues were created from the provided files.", (int)LogType.Warning);
			return;
		}

		// Optionally select the first newly created top-level structure (group, first parent, or first cue)
		var cueToFocus = groupCue ?? firstParentForFocus ?? newCues.FirstOrDefault();
		MaybeSelectNewCue(cueToFocus);

		// Recalculate durations for affected area (simple: recalc the new ones + parents)
		foreach (var c in newCues)
		{
			c.CalculateTotalDuration();
		}
		if (groupCue != null) groupCue.CalculateTotalDuration();

		string structureNote = useSingleGroup
			? " (grouped)"
			: useParentPerFile
				? " (parent per file)"
				: "";
		_globalSignals?.EmitSignal(nameof(GlobalSignals.Log),
			$"CueList: Created {newCues.Count} cue(s) from drop{structureNote}", (int)LogType.Info);

		GD.Print($"CueList:CreateCuesFromDroppedFiles - Created {newCues.Count} cue(s){structureNote}.");
	}

	/// <summary>
	/// Resolves the target UI container, insert index, and logical parent ID for a drop insertion.
	/// </summary>
	private (VBoxContainer container, int index, int parentId) ResolveInsertLocation(int targetCueId, DropInsertMode mode)
	{
		if (targetCueId < 0 || mode == DropInsertMode.AtEnd)
			return (_cueContainer, RootOrder.Count, -1);

		var targetCue = FetchCueFromId(targetCueId);
		if (targetCue == null)
			return (_cueContainer, RootOrder.Count, -1);

		switch (mode)
		{
			case DropInsertMode.Above:
			{
				var siblings = GetSiblingIdList(targetCue.ParentId) ?? RootOrder;
				int idx = siblings.IndexOf(targetCue.Id);
				if (idx < 0)
					idx = siblings.Count;
				return (_cueContainer, idx, targetCue.ParentId);
			}

			case DropInsertMode.Below:
			{
				var siblings = GetSiblingIdList(targetCue.ParentId) ?? RootOrder;
				int idx = siblings.IndexOf(targetCue.Id);
				if (idx < 0)
					idx = siblings.Count;
				else
					idx += 1;
				return (_cueContainer, idx, targetCue.ParentId);
			}

			case DropInsertMode.AsChild:
				targetCue.Expanded = true;
				return (_cueContainer, targetCue.ChildCues.Count, targetCue.Id);

			default:
				return (_cueContainer, RootOrder.Count, -1);
		}
	}

	/// <summary>
	/// Creates the ShellBar UI, wires it, inserts it into the given container at index, updates data model.
	/// </summary>
	/// <param name="skipIssueLookup">
	/// When true, skip media-health lookup in <see cref="ShellBar.SetCue"/> (showfile load;
	/// health runs after the overlay).
	/// </param>
	private ShellBar CreateShellAndInsert(
		Cue cue,
		VBoxContainer container,
		int insertIndex,
		int parentId,
		bool skipIssueLookup = false)
	{
		if (cue == null)
			return null;

		_ = container;
		_ = skipIssueLookup;

		if (!CueIndex.ContainsKey(cue.Id))
			CueIndex.Add(cue.Id, cue);
		else
			CueIndex[cue.Id] = cue;

		if (parentId != -1)
		{
			var parent = FetchCueFromId(parentId);
			if (parent != null && parent.ChildCues.Count == 0)
				parent.Expanded = true;
		}

		InsertCueInModel(cue, parentId, insertIndex);
		NotifyVirtualStructureChanged();
		return cue.ShellBar;
	}

	/// <summary>
	/// When media backup is enabled and a show is open, returns a show-relative path and queues copy.
	/// Otherwise returns the original absolute path. Metadata loading should still use the absolute source.
	/// </summary>
	private string ResolveMediaPathForNewCue(string absolutePath, bool isAudio)
	{
		try
		{
			var backup = GetNodeOrNull<MediaBackupManager>("/root/MediaBackupManager");
			if (backup == null)
				return absolutePath;

			var kind = isAudio
				? MediaBackupKind.Audio
				: MediaBackupManager.DetectKindFromPath(absolutePath);

			string relative = backup.EnsureMediaBackedUp(absolutePath, kind);
			return string.IsNullOrEmpty(relative) ? absolutePath : relative;
		}
		catch (Exception ex)
		{
			GD.PrintErr($"CueList:ResolveMediaPathForNewCue - {ex.Message}");
			return absolutePath;
		}
	}

	/// <summary>
	/// Asynchronously fetches metadata (and waveform for audio), attaches it to the component, and updates cue duration.
	/// Safe to fire-and-forget. <paramref name="filePath"/> should be a readable absolute path (source file).
	/// </summary>
	private async Task ApplyMetadataToNewCueAsync(Cue cue, string filePath, MediaEngine mediaEngine)
	{
		if (cue == null || string.IsNullOrEmpty(filePath) || mediaEngine == null) return;

		try
		{
			var audioComp = cue.GetAudioComponent();
			var videoComp = cue.GetVideoComponent();

			if (audioComp != null)
			{
				var meta = await mediaEngine.GetAudioFileMetadataAsync(filePath);
				audioComp.Metadata = meta;
				audioComp.RecalculateDuration();

				// Optional waveform (best effort)
				try
				{
					audioComp.WaveformData = await mediaEngine.GenerateWaveformAsync(filePath);
				}
				catch { /* non-fatal */ }
			}
			else if (videoComp != null)
			{
				videoComp.RefreshIsImageFromPath();
				var meta = await mediaEngine.GetVideoFileMetadataAsync(filePath);
				videoComp.Metadata = meta;
				if (videoComp.IsImage)
				{
					// Still image: no embedded audio; duration is user hold time (0 = until stopped).
					videoComp.HasAudio = false;
					videoComp.UseAudio = false;
					videoComp.StartTime = 0;
					videoComp.EndTime = -1;
				}
				else
				{
					videoComp.HasAudio = meta.AudioChannels > 0;
					videoComp.UseAudio = videoComp.HasAudio;
				}
				videoComp.ScaledWidth = meta.Width;
				videoComp.ScaledHeight = meta.Height;
				videoComp.RecalculateDuration();
			}

			cue.CalculateTotalDuration();

			// Notify UI that a shell may need refresh (duration etc.)
			_globalSignals?.EmitSignal(nameof(GlobalSignals.UpdateShellBar), cue.Id);

			// Drop-create often focuses the cue before this async metadata/waveform finishes.
			// Re-emit focus so open audio/video inspectors rebuild matrix + waveform from hydrated data.
			if (_globalData != null && _globalData.FocusedCue == cue.Id)
			{
				_globalSignals?.EmitSignal(nameof(GlobalSignals.ShellFocused), cue.Id);
				_globalSignals?.EmitSignal(nameof(GlobalSignals.SyncShellInspector));
			}
		}
		catch (Exception ex)
		{
			_globalSignals?.EmitSignal(nameof(GlobalSignals.Log),
				$"CueList: Failed to load metadata for dropped file '{Path.GetFileName(filePath)}': {ex.Message}", (int)LogType.Warning);
		}
	}

	private void AddCue(Cue cue)
	{
		if (cue == null)
			return;
		if (!CueIndex.ContainsKey(cue.Id))
			CueIndex.Add(cue.Id, cue);
		CreateNewShell(cue);
	}

	/// <summary>
	/// Syncs <see cref="GlobalData.CueTotal"/> and notifies listeners when the cue count changes.
	/// </summary>
	/// <summary>
	/// Assigns even/odd zebra indices to every visible shell (including expanded children)
	/// so shell washes and the blank-space stripes stay consistent.
	/// </summary>
	public void RefreshShellZebra()
	{
		for (int i = 0; i < VisibleRowIds.Count; i++)
		{
			var cue = FetchCueFromId(VisibleRowIds[i]);
			cue?.ShellBar?.SetZebraIndex(i);
		}
	}

	private static void AssignZebraRecursive(VBoxContainer container, ref int index)
	{
		if (container == null) return;
		foreach (var child in container.GetChildren())
		{
			if (child is not ShellBar shell) continue;
			shell.SetZebraIndex(index++);
			// Only walk into currently visible nested shells.
			if (shell.ShellChildContainer != null && shell.ShellChildContainer.Visible)
				AssignZebraRecursive(shell.ShellChildContainer, ref index);
		}
	}

	private void NotifyTotalCuesChanged()
	{
		int total = TotalCueCount;
		if (_globalData != null)
			_globalData.CueTotal = total;
		_globalSignals?.EmitSignal(nameof(GlobalSignals.TotalCuesChanged), total);
		// Count/structure changes almost always affect visual order.
		CallDeferred(nameof(RefreshShellZebra));
	}
	
	// This instantiates the shell scene which creates the UI elements to represent the cue in the scene
	private void CreateNewShell(Cue newCue)
	{
		int parentId = -1;
		int insertIndex = RootOrder.Count;
		if (ShellSelection.SelectedCues.Count > 0)
		{
			var selectedCue = ShellSelection.SelectedCues.Last();
			parentId = selectedCue.ParentId;
			var siblings = GetSiblingIdList(parentId) ?? RootOrder;
			int selIdx = siblings.IndexOf(selectedCue.Id);
			insertIndex = selIdx >= 0 ? selIdx + 1 : siblings.Count;
			if (parentId != -1)
			{
				var parent = FetchCueFromId(parentId);
				if (parent != null && parent.ChildCues.Count == 0)
					parent.Expanded = true;
			}
		}

		InsertCueInModel(newCue, parentId, insertIndex);
		NotifyVirtualStructureChanged();
	}
	
	/// <summary>
	/// Duplicates selected cues (Ctrl+D).
	/// <list type="bullet">
	/// <item>If a parent is selected, its full child tree is duplicated; selected descendants of that parent are ignored.</item>
	/// <item>A single selection is cloned directly below the origin.</item>
	/// <item>Multiple roots insert as one contiguous block below the most recently selected cue.</item>
	/// </list>
	/// Large forests are frame-sliced with footer progress so the UI stays responsive.
	/// </summary>
	public void DuplicateSelectedCues()
	{
		_ = DuplicateSelectedCuesAsync();
	}

	/// <summary>
	/// Copies selected cue roots (and full child trees) to the in-app cue clipboard (Ctrl+C).
	/// Parent selected ⇒ whole tree; selected descendants of that parent are ignored as separate roots.
	/// Large captures are frame-sliced with footer progress.
	/// </summary>
	public void CopySelectedCues()
	{
		_ = CopySelectedCuesAsync();
	}

	/// <summary>
	/// Cuts selected cue roots to the clipboard then deletes them (Ctrl+X).
	/// One cuelist history step covers the removal; paste is a separate undo step.
	/// Large cuts are frame-sliced with footer progress.
	/// </summary>
	public void CutSelectedCues()
	{
		_ = CutSelectedCuesAsync();
	}

	/// <summary>
	/// Pastes the cue clipboard as a contiguous block below the last selected cue (Ctrl+V).
	/// If nothing is selected, pastes at the end of the top-level list.
	/// Control targets that point inside the pasted forest are remapped to the new ids.
	/// Large pastes are frame-sliced with footer progress.
	/// </summary>
	public void PasteCues()
	{
		_ = PasteCuesAsync();
	}

	/// <summary>
	/// True if <paramref name="cue"/> has an ancestor whose id is in <paramref name="selectedIds"/>.
	/// </summary>
	private static bool IsDescendantOfAnySelectedAncestor(Cue cue, HashSet<int> selectedIds)
	{
		int parentId = cue.ParentId;
		while (parentId != -1)
		{
			if (selectedIds.Contains(parentId))
				return true;
			var parent = FetchCueFromId(parentId);
			if (parent == null) break;
			parentId = parent.ParentId;
		}
		return false;
	}

	/// <summary>
	/// Flat list of all cue ids in UI order (including inside collapsed groups).
	/// </summary>
	private List<int> GetVisualCueOrderIncludingCollapsed()
	{
		return GetModelVisualOrder(includeCollapsed: true);
	}

	/// <summary>
	/// Imports a library cue forest (temp-id keyed dictionaries) into the live cuelist.
	/// Allocates new session cue ids, remaps parent/child and in-tree control targets, creates shells.
	/// </summary>
	/// <param name="cuesByTempId">Cue <see cref="Cue.GetData"/> dicts keyed by temp id string.</param>
	/// <param name="rootTempId">Temp id of the forest root.</param>
	/// <param name="insertMode">Where to place the new root relative to the current selection.</param>
	/// <returns>New root cue id, or -1 on failure.</returns>
	public int ImportCueTreeFromLibrary(
		Dictionary cuesByTempId,
		int rootTempId,
		LibraryInsertMode insertMode)
	{
		if (BlockIfShowMode("load library cues into the cuelist") || BlockIfBulkBusy("load library cues"))
			return -1;
		if (cuesByTempId == null || cuesByTempId.Count == 0)
			return -1;

		string rootKey = rootTempId.ToString();
		if (!cuesByTempId.ContainsKey(rootKey) && cuesByTempId.Count > 0)
		{
			// Fallback: first key
			rootKey = cuesByTempId.Keys.First().AsString();
			if (!int.TryParse(rootKey, out rootTempId))
				return -1;
		}

		if (!cuesByTempId.ContainsKey(rootKey))
			return -1;

		// Resolve insert location from focused / selected cue
		int focusId = _globalData?.FocusedCue ?? -1;
		if (focusId < 0 && ShellSelection.SelectedCues != null && ShellSelection.SelectedCues.Count > 0)
			focusId = ShellSelection.SelectedCues[0].Id;

		VBoxContainer container;
		int insertIndex;
		int parentId;
		switch (insertMode)
		{
			case LibraryInsertMode.AsChild:
				(container, insertIndex, parentId) = ResolveInsertLocation(focusId, DropInsertMode.AsChild);
				break;
			case LibraryInsertMode.End:
				(container, insertIndex, parentId) = ResolveInsertLocation(-1, DropInsertMode.AtEnd);
				break;
			default:
				(container, insertIndex, parentId) = ResolveInsertLocation(
					focusId, focusId >= 0 ? DropInsertMode.Below : DropInsertMode.AtEnd);
				break;
		}

		// First pass: build live cues from data with reminted ids; track temp→new map
		var tempToNew = new System.Collections.Generic.Dictionary<int, int>();
		var tempToCue = new System.Collections.Generic.Dictionary<int, Cue>();
		var tempChildOrder = new System.Collections.Generic.Dictionary<int, List<int>>();

		foreach (var kv in cuesByTempId)
		{
			string key = kv.Key.AsString();
			if (!int.TryParse(key, out int tempId))
				continue;
			if (kv.Value.VariantType != Variant.Type.Dictionary)
				continue;

			var data = kv.Value.AsGodotDictionary();

			// Capture child order (temp ids) before we clear hierarchy fields
			var childTemps = new List<int>();
			if (data.TryGetValue("ChildCues", out var childVar))
			{
				foreach (var c in childVar.AsGodotArray())
					childTemps.Add(c.AsInt32());
			}
			tempChildOrder[tempId] = childTemps;

			// Build a clean copy for ApplyFromData: hierarchy applied by CreateShellAndInsert
			var applyData = DeepCloneDict(data);
			applyData["ParentId"] = "-1";
			applyData["ChildCues"] = new Godot.Collections.Array();
			// Id in data is ignored by ApplyFromData (keeps live Id)

			var cue = new Cue();
			cue.ApplyFromData(applyData);
			// Ensure ChildCues empty until we insert children
			cue.ChildCues = new List<int>();
			cue.ParentId = -1;

			tempToNew[tempId] = cue.Id;
			tempToCue[tempId] = cue;
		}

		if (!tempToCue.ContainsKey(rootTempId))
		{
			GD.PrintErr($"CueList:ImportCueTreeFromLibrary - Root temp id {rootTempId} missing.");
			return -1;
		}

		// Remap Control targets: library temp ids → new session ids
		foreach (var cue in tempToCue.Values)
		{
			foreach (var comp in cue.Components)
			{
				if (comp is not ControlComponent control) continue;
				if (control.TargetCueId < 0) continue;
				if (tempToNew.TryGetValue(control.TargetCueId, out int newTarget))
					control.TargetCueId = newTarget;
				else
					control.TargetCueId = -1;
			}

			RelinkCueComponents(cue);
		}

		// Second pass: insert shells in tree order
		Cue ImportNode(int tempId, int newParentId, VBoxContainer cont, int index)
		{
			if (!tempToCue.TryGetValue(tempId, out var cue))
				return null;

			CreateShellAndInsert(cue, cont, index, newParentId);

			var children = tempChildOrder.TryGetValue(tempId, out var list) ? list : new List<int>();
			var childContainer = cue.ShellBar?.ShellChildContainer;
			if (childContainer != null && children.Count > 0)
			{
				int childIndex = 0;
				foreach (int childTemp in children)
				{
					ImportNode(childTemp, cue.Id, childContainer, childIndex);
					childIndex++;
				}

				// Restore expanded state from data if present
				if (cuesByTempId.TryGetValue(tempId.ToString(), out var raw) &&
				    raw.VariantType == Variant.Type.Dictionary)
				{
					var d = raw.AsGodotDictionary();
					if (d.TryGetValue("Expanded", out var exp))
						cue.Expanded = exp.AsBool();
				}

				cue.ShellBar?.RelationshipChanged();
				cue.ShellBar?.SetExpanded(cue.Expanded);
			}

			cue.CalculateTotalDuration();
			return cue;
		}

		var rootCue = ImportNode(rootTempId, parentId, container, insertIndex);
		if (rootCue == null)
			return -1;

		MaybeSelectNewCue(rootCue);
		_globalSignals?.EmitSignal(nameof(GlobalSignals.UpdateShellBar), rootCue.Id);
		_globalSignals?.EmitSignal(nameof(GlobalSignals.SyncShellInspector));

		GD.Print($"CueList:ImportCueTreeFromLibrary - Imported root id {rootCue.Id} ({tempToCue.Count} cue(s)).");
		return rootCue.Id;
	}

	private static Dictionary DeepCloneDict(Dictionary source)
	{
		if (source == null) return new Dictionary();
		string json = Json.Stringify(source);
		using var parser = new Json();
		var err = parser.Parse(json);
		if (err != Error.Ok)
			throw new InvalidOperationException($"CueList deep-clone JSON parse failed: {err}");
		return parser.Data.AsGodotDictionary();
	}

	/// <summary>
	/// Deep-clones a cue and all descendants into <paramref name="container"/> at <paramref name="insertIndex"/>.
	/// </summary>
	/// <returns>The new root clone, or null on failure.</returns>
	private Cue CloneCueTree(Cue source, int newParentId, VBoxContainer container, int insertIndex)
	{
		if (source == null) return null;

		var clone = CloneCueShallow(source);
		if (clone == null)
			return null;

		CreateShellAndInsert(clone, container, insertIndex, newParentId);

		// Children of the source go under the clone's child container, in ChildCues order
		var childContainer = clone.ShellBar?.ShellChildContainer;
		if (childContainer != null && source.ChildCues.Count > 0)
		{
			int childIndex = 0;
			foreach (int childId in source.ChildCues.ToList())
			{
				var child = FetchCueFromId(childId);
				if (child == null) continue;
				CloneCueTree(child, clone.Id, childContainer, childIndex);
				childIndex++;
			}
			clone.Expanded = source.Expanded;
			clone.ShellBar?.RelationshipChanged();
			clone.ShellBar?.SetExpanded(clone.Expanded);
		}

		clone.CalculateTotalDuration();
		return clone;
	}

	/// <summary>
	/// Creates a new cue with a fresh id and deep-copies all document fields from
	/// <paramref name="source"/> (shell props, arming, triggers, components).
	/// Does not copy ParentId/ChildCues (set by tree insert).
	/// </summary>
	/// <remarks>
	/// Uses <see cref="Cue.GetData"/> / <see cref="Cue.ApplyFromData"/> so hotkey, clock,
	/// MIDI, OSC trigger, and component data stay aligned with clipboard paste.
	/// </remarks>
	private Cue CloneCueShallow(Cue source)
	{
		if (source == null)
			return null;

		try
		{
			var data = DeepCloneDict(source.GetData());
			// ApplyFromData keeps the new Cue's Id; clear hierarchy for the insert path.
			data.Remove("Id");
			data["ParentId"] = "-1";
			data["ChildCues"] = new Godot.Collections.Array();

			var clone = new Cue();
			clone.ApplyFromData(data);
			clone.ParentId = -1;
			clone.ChildCues = new List<int>();
			RelinkCueComponents(clone);
			return clone;
		}
		catch (Exception ex)
		{
			GD.PrintErr($"CueList:CloneCueShallow - Failed to clone cue \"{source.Name}\": {ex.Message}");
			return null;
		}
	}

	/// <summary>
	/// Deletes all currently selected cues (Delete key / shell inspector).
	/// When a parent is selected, children are removed with it even if not multi-selected.
	/// Large deletes are frame-sliced with footer progress.
	/// </summary>
	public void DeleteSelectedCues()
	{
		_ = DeleteSelectedCuesAsync();
	}

	/// <summary>
	/// Removes the cue from the index and queues its ShellBar for deletion.
	/// Prunes from parent's ChildCues (if any) and refreshes the parent's collapse/expand UI.
	/// Does not recurse into children — use <see cref="RemoveCueRecursive"/> for groups.
	/// </summary>
	/// <param name="cue">The cue to remove.</param>
	public void RemoveCue(Cue cue)
	{
		if (cue == null) return;

		// Drop selection state
		if (ShellSelection.SelectedCues != null && ShellSelection.SelectedCues.Contains(cue))
		{
			cue.ShellBar?.Deselect();
			ShellSelection.SelectedCues.Remove(cue);
		}

		// Clear media-health tracking for this cue
		try
		{
			GetNodeOrNull<MediaHealthService>("/root/MediaHealthService")?.ClearIssue(cue.Id);
		}
		catch { /* optional */ }

		if (cue.ParentId != -1)
		{
			var p = FetchCueFromId(cue.ParentId);
			if (p != null)
			{
				p.ChildCues.Remove(cue.Id);
				p.ShellBar?.RelationshipChanged();
			}
		}

		DetachCueFromModel(cue);
		if (cue.ShellBar != null && IsInstanceValid(cue.ShellBar))
			ReleaseVirtualShell(cue.ShellBar);
		cue.ShellBar = null;

		CueIndex?.Remove(cue.Id);
		if (_bulkNotifySuppressDepth == 0 && _virtualRefreshSuppress == 0)
			NotifyVirtualStructureChanged();
	}

	/// <summary>
	/// Removes a cue and all descendants. Returns number of cues removed.
	/// </summary>
	public int RemoveCueRecursive(Cue cue)
	{
		if (cue == null) return 0;

		int removed = 0;
		// Copy child list — RemoveCue mutates parent ChildCues
		foreach (int childId in cue.ChildCues.ToList())
		{
			var child = FetchCueFromId(childId);
			if (child != null)
				removed += RemoveCueRecursive(child);
		}

		RemoveCue(cue);
		return removed + 1;
	}

	/// <summary>
	/// Retrieves a Cue by its ID using the static index.
	/// </summary>
	/// <param name="id">The unique cue identifier.</param>
	/// <returns>The Cue if found; otherwise null.</returns>
	public static Cue FetchCueFromId(int id)
	{
		if (CueIndex == null) return null;
		CueIndex.TryGetValue(id, out Cue cue);
		return cue;
	}

	/// <summary>
	/// Retrieves the first cue whose <see cref="Cue.CueNum"/> matches <paramref name="cueNum"/> (exact, case-sensitive).
	/// </summary>
	/// <param name="cueNum">User-facing cue number string.</param>
	/// <returns>The matching cue, or null if none / empty input.</returns>
	public static Cue FetchCueFromCueNum(string cueNum)
	{
		if (string.IsNullOrWhiteSpace(cueNum) || CueIndex == null)
			return null;

		string needle = cueNum.Trim();
		foreach (var cue in CueIndex.Values)
		{
			if (cue != null && string.Equals(cue.CueNum, needle, StringComparison.Ordinal))
				return cue;
		}

		return null;
	}
	

	private void OnMouseEntered(ShellBar shellbar)
	{
		if (_reorderController != null && _reorderController.IsActive)
		{
			_reorderController.SetMouseOver(shellbar);
		}
	}

	/// <summary>
	/// Begins a drag-reorder operation for the given shell (and its current multi-selection).
	/// Shows the floating reorder preview and enables mouse tracking in _Input.
	/// </summary>
	/// <param name="shellbar">The ShellBar that initiated the drag (via its drag button).</param>
	/// <summary>
	/// True while a drag-reorder session is active (drop indicator visible).
	/// </summary>
	public bool IsReordering => _reorderController != null && _reorderController.IsActive;

	public void StartReorder(ShellBar shellbar)
	{
		if (IsCueEditingLocked())
			return;
		// Reorder wins over any in-progress marquee / pending click-drag select.
		_boxSelectController?.Cancel();
		// Drop blue hover wash for the session; reorder indicator owns highlight.
		ClearAllShellHoverChrome();
		_reorderController.Start(shellbar);
		SyncPointerInputProcessing();
	}

	/// <summary>
	/// Starts a potential box-select after a left press on a shell row (not the drag grabber).
	/// Click without drag still selects; drag past threshold draws a marquee.
	/// </summary>
	/// <param name="originShell">Shell under the press, or null for empty list space.</param>
	/// <param name="globalPos">Press position in global coordinates.</param>
	/// <param name="additive">Ctrl/Cmd: toggle click membership; marquee unions with existing selection.</param>
	public void BeginPotentialBoxSelect(ShellBar originShell, Vector2 globalPos, bool additive = false)
	{
		if (IsReordering)
			return;
		_boxSelectController?.BeginPending(originShell, globalPos, additive);
		SyncPointerInputProcessing();
	}

	/// <summary>
	/// Enables <c>_Input</c> only while reorder or box-select is tracking the pointer.
	/// </summary>
	internal void SyncPointerInputProcessing()
	{
		bool need = IsReordering
			|| (_boxSelectController != null && (_boxSelectController.IsPending || _boxSelectController.IsActive));
		SetProcessInput(need);
	}

	/// <summary>
	/// Empty-area clicks in the cue container: box-select / clear selection.
	/// Shell rows handle their own presses; this only receives events not taken by children.
	/// </summary>
	private void OnCueContainerGuiInput(InputEvent @event)
	{
		if (@event is not InputEventMouseButton mb || !mb.Pressed)
			return;
		if (mb.ButtonIndex != MouseButton.Left)
			return;
		if (IsReordering)
			return;

		// Shift range-select only applies to shells; ignore here.
		if (Input.IsKeyPressed(Key.Shift))
			return;

		bool additive = Input.IsKeyPressed(Key.Ctrl) || Input.IsKeyPressed(Key.Meta);
		BeginPotentialBoxSelect(null, mb.GlobalPosition, additive);
	}

	/// <summary>
	/// Clears hover chrome on every shell so reorder does not inherit a stuck hover wash.
	/// </summary>
	internal void ClearAllShellHoverChrome()
	{
		foreach (int id in VisibleRowIds)
			FetchCueFromId(id)?.ShellBar?.ClearHoverChrome();
	}
	
	public override void _Input(InputEvent @event)
	{
		// Reorder and box-select both track global mouse-up; reorder takes exclusive priority.
		if (_reorderController != null && _reorderController.IsActive)
		{
			_reorderController.ProcessInput(@event);
			return;
		}

		_boxSelectController?.ProcessInput(@event);
	}

	/// <inheritdoc />
	public override void _Process(double delta)
	{
		_boxSelectController?.Tick();
	}

	private void EndReorder()
	{
		_reorderController?.Commit();
	}

	/// <summary>
	/// Common cleanup for ending or cancelling a reorder drag.
	/// </summary>
	private void CleanupReorder(bool keepChanges)
	{
		_reorderController?.Cancel();
	}

	/// <summary>
	/// Deferred grabber unstick after reorder ends (called from <see cref="CueReorder"/>).
	/// Ensures BaseButton pressed state is cleared after the mouse-up event finishes.
	/// </summary>
	/// <param name="draggedCueId">Cue that initiated the drag, or -1.</param>
	public void DeferredReleaseReorderGrabbers(int draggedCueId)
	{
		if (draggedCueId >= 0)
			FetchCueFromId(draggedCueId)?.ShellBar?.ReleaseDragGrabber();

		if (ShellSelection.SelectedCues == null)
			return;

		foreach (var cue in ShellSelection.SelectedCues)
			cue?.ShellBar?.ReleaseDragGrabber();
	}

	/// <summary>
	/// Returns the visually last (bottom-most) ShellBar in the current list,
	/// walking into expanded child containers. Used to detect "blank space below everything".
	/// </summary>
	internal ShellBar GetLastVisibleShellBar()
	{
		if (VisibleRowIds.Count == 0)
			return null;
		for (int i = VisibleRowIds.Count - 1; i >= 0; i--)
		{
			var shell = FetchCueFromId(VisibleRowIds[i])?.ShellBar;
			if (shell != null && IsInstanceValid(shell))
				return shell;
		}
		return null;
	}

	internal void EmitLog(string message, int type)
	{
		_globalSignals?.EmitSignal(nameof(GlobalSignals.Log), message, type);
	}

	/// <summary>
	/// Records a cuelist-scoped history checkpoint (used by structural ops and <see cref="CueReorder"/>).
	/// </summary>
	/// <param name="description">Readable undo description.</param>
	/// <param name="coalesceKey">Optional coalesce key for continuous edits.</param>
	internal void RecordHistory(string description, string coalesceKey = null)
	{
		_globalData?.HistoryManager?.RecordCuelistChange(description, coalesceKey);
	}

	/// <summary>
	/// Re-links component runtime references (patches, cue lights, OSC) after a cue data apply.
	/// </summary>
	/// <param name="cue">Cue whose components should be re-linked.</param>
	internal void RelinkCueComponents(Cue cue)
	{
		if (cue == null) return;

		var timer = SessionLoadTimer.Current;

		var audio = cue.GetAudioComponent();
		if (audio != null)
		{
			var patches = _globalData.Settings.GetAudioOutputPatches();
			patches.TryGetValue(audio.PatchId, out var patch);
			audio.Patch = patch;
			if (timer != null)
				timer.LinkAudio++;
		}

		var video = cue.GetVideoComponent();
		if (video != null)
		{
			var patches = _globalData.Settings.GetAudioOutputPatches();
			patches.TryGetValue(video.PatchId, out var patch);
			video.Patch = patch;
			if (timer != null)
				timer.LinkVideo++;
		}

		var cueLightComps = cue.GetCueLightComponents();
		if (cueLightComps != null)
		{
			foreach (var cueLightComp in cueLightComps)
			{
				var cuelight = _globalData.CueLightManager.GetCueLight(cueLightComp.CueLightId);
				cueLightComp.CueLight = cuelight;
				if (timer != null)
					timer.LinkCueLight++;
			}
		}

		var oscComponents = cue.GetOscComponents();
		if (oscComponents != null)
		{
			foreach (var oscComp in oscComponents)
			{
				var oscConnection = OscConnections.GetCueOscConnection(oscComp.OscConnectionId);
				oscComp.OscConnection = oscConnection;
				if (timer != null)
					timer.LinkOsc++;
			}
		}
	}

	/// <summary>
	/// Applies a single-cue history snapshot in place without rebuilding the cuelist or settings.
	/// </summary>
	/// <param name="cueId">Target cue id.</param>
	/// <param name="cueData">Deep-cloned <see cref="Cue.GetData"/> dictionary.</param>
	/// <returns>True if the cue was found and applied.</returns>
	internal bool ApplyCueHistorySnapshot(int cueId, Dictionary cueData)
	{
		var cue = FetchCueFromId(cueId);
		if (cue == null || cueData == null)
		{
			GD.PrintErr($"CueList:ApplyCueHistorySnapshot - Cue {cueId} not found or data null.");
			return false;
		}

		cue.ApplyFromData(cueData);
		RelinkCueComponents(cue);
		cue.ShellBar?.RefreshAllFromCue();

		// Keep shell selection list pointing at the same cue instance.
		GetNodeOrNull<MediaHealthService>("/root/MediaHealthService")?.CheckCue(cueId);

		_globalSignals?.EmitSignal(nameof(GlobalSignals.UpdateShellBar), cueId);
		// Always request shell-inspector repaint from model (no-op if nothing focused).
		// Do not rely on re-emitting ShellFocused: ShellSelected early-outs when id is unchanged.
		_globalSignals?.EmitSignal(nameof(GlobalSignals.SyncShellInspector));

		// Other inspectors (audio/video) rebuild on ShellFocused; force when this cue is focused.
		if (_globalData != null && _globalData.FocusedCue == cueId)
			_globalSignals?.EmitSignal(nameof(GlobalSignals.ShellFocused), cueId);

		return true;
	}

	/// <summary>
	/// Applies a multi-cue history memento in place (no full list free/rebuild).
	/// </summary>
	/// <param name="cuesById">Map of cue-id string → <see cref="Cue.GetData"/> dictionary.</param>
	internal void ApplyMultiCueHistorySnapshot(Dictionary cuesById)
	{
		if (cuesById == null || cuesById.Count == 0)
			return;

		var health = GetNodeOrNull<MediaHealthService>("/root/MediaHealthService");
		int lastId = -1;

		foreach (var key in cuesById.Keys)
		{
			if (!int.TryParse(key.AsString(), out int cueId))
				continue;
			if (cuesById[key].VariantType != Variant.Type.Dictionary)
				continue;

			var data = cuesById[key].AsGodotDictionary();
			var cue = FetchCueFromId(cueId);
			if (cue == null)
			{
				GD.PrintErr($"CueList:ApplyMultiCueHistorySnapshot - Cue {cueId} not found; skip.");
				continue;
			}

			cue.ApplyFromData(data);
			RelinkCueComponents(cue);
			cue.ShellBar?.RefreshAllFromCue();
			health?.CheckCue(cueId);
			lastId = cueId;
		}

		_globalSignals?.EmitSignal(nameof(GlobalSignals.SyncShellInspector));
		if (_globalData != null && _globalData.FocusedCue >= 0
		    && FetchCueFromId(_globalData.FocusedCue) != null)
		{
			_globalSignals?.EmitSignal(nameof(GlobalSignals.ShellFocused), _globalData.FocusedCue);
		}
		else if (lastId >= 0)
		{
			_globalSignals?.EmitSignal(nameof(GlobalSignals.UpdateShellBar), lastId);
		}
	}

	/// <summary>
	/// Restores cuelist structure from a history snapshot without settings/displays reload.
	/// Prefers in-place update of existing shells; only instantiates/frees changed cue ids.
	/// </summary>
	/// <param name="cuesData"><see cref="GetData"/> dictionary (Cues + CueOrder).</param>
	internal void ApplyCuelistHistorySnapshot(Dictionary cuesData)
	{
		if (cuesData == null)
		{
			_globalSignals?.EmitSignal(nameof(GlobalSignals.StopAll));
			ResetCuelist();
			GetNodeOrNull<MediaHealthService>("/root/MediaHealthService")?.ClearAll();
			_globalSignals?.EmitSignal(nameof(GlobalSignals.SyncShellInspector));
			return;
		}

		// Full free/rebuild remains correct fallback when snapshot is empty or malformed.
		if (!cuesData.TryGetValue("Cues", out var cuesVar) || cuesVar.VariantType != Variant.Type.Dictionary)
		{
			_globalSignals?.EmitSignal(nameof(GlobalSignals.StopAll));
			ResetCuelist();
			GetNodeOrNull<MediaHealthService>("/root/MediaHealthService")?.ClearAll();
			LoadData(cuesData);
			GetNodeOrNull<MediaHealthService>("/root/MediaHealthService")?.RecheckAllQuiet();
			_globalSignals?.EmitSignal(nameof(GlobalSignals.SyncShellInspector));
			return;
		}

		var snapshotCues = cuesVar.AsGodotDictionary();
		var snapshotIds = new HashSet<int>();
		foreach (var key in snapshotCues.Keys)
		{
			if (int.TryParse(key.AsString(), out int id))
				snapshotIds.Add(id);
			else if (key.VariantType == Variant.Type.Int)
				snapshotIds.Add(key.AsInt32());
		}

		// Structural change can leave orphan active playbacks — stop all before mutating list.
		_globalSignals?.EmitSignal(nameof(GlobalSignals.StopAll));
		BeginVirtualRefreshSuppress();
		try
		{

		// Remove live cues not present in snapshot (children first via recursive remove on roots).
		var liveIds = CueIndex?.Keys.ToList() ?? new List<int>();
		var toRemove = new List<Cue>();
		foreach (int id in liveIds)
		{
			if (snapshotIds.Contains(id)) continue;
			var cue = FetchCueFromId(id);
			if (cue == null) continue;
			// Only remove roots of deleted subtrees; recursive remove handles descendants.
			// If parent is also deleted, wait for parent pass — mark for remove only if parent stays
			// or parent is also deleted (then remove deepest roots among deleted set).
			if (cue.ParentId >= 0 && !snapshotIds.Contains(cue.ParentId))
				continue; // parent deleted too — will be removed with parent
			toRemove.Add(cue);
		}

		foreach (var cue in toRemove)
			RemoveCueRecursive(cue);

		var health = GetNodeOrNull<MediaHealthService>("/root/MediaHealthService");
		var changedIds = new List<int>();

		// Create missing / update existing
		foreach (var kv in snapshotCues)
		{
			string keyStr = kv.Key.AsString();
			if (!int.TryParse(keyStr, out int cueId) && kv.Key.VariantType == Variant.Type.Int)
				cueId = kv.Key.AsInt32();
			else if (!int.TryParse(keyStr, out cueId))
				continue;

			if (kv.Value.VariantType != Variant.Type.Dictionary)
				continue;
			var data = kv.Value.AsGodotDictionary();

			// Ensure Id field present for constructor/apply
			if (!data.ContainsKey("Id"))
				data["Id"] = cueId.ToString();

			var live = FetchCueFromId(cueId);
			if (live != null)
			{
				live.ApplyFromData(data);
				RelinkCueComponents(live);
				live.ShellBar?.RefreshAllFromCue();
				changedIds.Add(cueId);
			}
			else
			{
				// CreateCue → AddCue instantiates a shell (insert position fixed later by StructureCuelist).
				CreateCue(data);
				changedIds.Add(cueId);
			}
		}

		// Apply order + nesting from snapshot
		if (cuesData.TryGetValue("CueOrder", out var orderVar))
		{
			var cueOrder = new Godot.Collections.Dictionary<int, int>();
			if (orderVar.VariantType == Variant.Type.Dictionary)
			{
				foreach (var cue in orderVar.AsGodotDictionary())
					cueOrder.Add((int)cue.Key, (int)cue.Value);
			}
			StructureCuelist(cueOrder);
		}

		// Bump id allocator past any restored ids
		int maxId = 0;
		if (CueIndex != null)
		{
			foreach (int id in CueIndex.Keys)
				if (id >= maxId) maxId = id + 1;
		}
		// Cue.ResetIdAllocator only zeros; ApplyFromData already raises _nextId when Id >= _nextId.

		if (health != null)
		{
			foreach (int id in changedIds)
				health.CheckCue(id);
		}

		// Selection + focus are restored by HistoryManager after this returns.
		}
		finally
		{
			EndVirtualRefreshSuppress();
		}

		_globalSignals?.EmitSignal(nameof(GlobalSignals.SyncShellInspector));
	}

	internal void SelectIndividualForReorder(int cueId)
	{
		var cue = FetchCueFromId(cueId);
		if (cue != null)
			_globalData.ShellSelection.SelectIndividualShell(cue, recordHistory: false);
	}

	private ShellBar FindLastShell(VBoxContainer container)
	{
		ShellBar last = null;
		foreach (var child in container.GetChildren())
		{
			if (child is ShellBar sb)
			{
				last = sb;
				var subContainer = sb.GetNodeOrNull<VBoxContainer>("%ShellChildContainer");
				if (subContainer != null && subContainer.GetChildCount() > 0 && subContainer.Visible)
				{
					var subLast = FindLastShell(subContainer);
					if (subLast != null)
						last = subLast;
				}
			}
		}
		return last;
	}

	/// <summary>
	/// Handler for the Expand/Collapse All button. Toggles the expanded state of all groups
	/// (recursively) and updates the button icon.
	/// </summary>
	private void OnExpandAllPressed()
	{
		_allExpanded = !_allExpanded;
		SetAllExpanded(_allExpanded);
		_expandAllButton.Icon = GetThemeIcon(_allExpanded ? "Down" : "Right", "AtlasIcons");
		RefreshShellZebra();
	}

	private void SetAllExpanded(bool expanded)
	{
		SetExpandedAllGroups(expanded);
	}

	/// <summary>
	/// Expands only one layer of nesting (top-level groups only, does not recurse into subgroups).
	/// </summary>
	public void ExpandOneLayer()
	{
		SetExpandedOneLayer(true);
	}

	/// <summary>
	/// Collapses only one layer of nesting (top-level groups only).
	/// </summary>
	public void CollapseOneLayer()
	{
		SetExpandedOneLayer(false);
	}

	private void SetExpandedOneLayer(bool expanded)
	{
		foreach (int id in RootOrder)
		{
			var cue = FetchCueFromId(id);
			if (cue == null || cue.ChildCues.Count == 0)
				continue;
			cue.Expanded = expanded;
		}
		NotifyVirtualStructureChanged();
	}

	/// <summary>
	/// Sets expand/collapse on every group in the model (all nesting levels).
	/// </summary>
	private void SetExpandedAllGroups(bool expanded)
	{
		if (CueIndex == null)
			return;
		foreach (var cue in CueIndex.Values)
		{
			if (cue != null && cue.ChildCues.Count > 0)
				cue.Expanded = expanded;
		}
		NotifyVirtualStructureChanged();
	}
	
	public void ResetCuelist()
	{
		ClearVirtualState();
		CueIndex = new System.Collections.Generic.Dictionary<int, Cue>();
		ShellSelection.SelectedCues = new List<Cue>();
		Cue.ResetIdAllocator();
		NotifyTotalCuesChanged();
	}
}
