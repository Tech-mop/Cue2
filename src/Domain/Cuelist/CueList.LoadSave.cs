// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;

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
/// Partial: ResetCuelist, GetData, LoadData / LoadCueModels (tree-order), StructureCuelist (history)
/// </summary>
public partial class CueList
{
	/// <summary>
	/// Serializes the entire cuelist (cues + top-level order) for session save.
	/// </summary>
	/// <returns>Dictionary containing "Cues" and "CueOrder".</returns>
	public Dictionary GetData()
	{
		var saveTable = new Dictionary();
		var cues = new Dictionary();
		var cueOrder = GetCueOrder();
		saveTable.Add("Kind", Kind);
		saveTable.Add("Name", ListName ?? "Cue List");
		saveTable.Add("CueOrder", cueOrder);
		foreach (int id in CollectCueIds())
		{
			var cue = FetchCueFromId(id);
			if (cue == null)
				continue;
			cues.Add(cue.Id, cue.GetData());
		}
		saveTable.Add("Cues", cues);
		return saveTable;
	}

	/// <summary>
	/// Returns a position-to-cueId map for the top-level cues only (used for save/load order).
	/// Child ordering is maintained via each Cue's ChildCues list.
	/// </summary>
	public Godot.Collections.Dictionary<int, int> GetCueOrder()
	{
		var cueOrder = new Godot.Collections.Dictionary<int, int>();
		for (int i = 0; i < RootOrder.Count; i++)
			cueOrder.Add(i, RootOrder[i]);

		return cueOrder;
	}

	/// <summary>
	/// Loads cues from serialized data in tree order (CueOrder, then ChildCues), then binds
	/// the virtual viewport. Used by history fallback. Showfile open splits model vs bind
	/// via <see cref="LoadCueModels"/> / <see cref="BindLoadedViewport"/>.
	/// </summary>
	/// <param name="cueData">The "cues" sub-dictionary from session save.</param>
	public void LoadData(Dictionary cueData)
	{
		_globalSignals?.EmitSignal(nameof(GlobalSignals.Log), "CueList:LoadData - Loading Cues", (int)LogType.Info);
		LoadCueModels(cueData);
		BindLoadedViewport();
	}

	/// <summary>
	/// Builds Cue models from showfile data in tree order. Does not instantiate ShellBars.
	/// After this returns, the document is GO-safe; call <see cref="BindLoadedViewport"/> for the list UI.
	/// </summary>
	/// <param name="cueData">The "cues" sub-dictionary from session save.</param>
	public void LoadCueModels(Dictionary cueData)
	{
		if (cueData == null)
			return;

		var payloads = IndexCuePayloads(cueData);
		int total = payloads.Count;
		if (SessionLoadTimer.Current != null)
			SessionLoadTimer.Current.CueCount = total;

		SessionLoadTimer.Current?.Begin("cues.construct");

		var visited = new HashSet<int>();
		var stack = new Stack<(int Id, int ParentId)>();

		var topIds = ParseTopLevelCueIds(cueData, payloads);
		RootOrder.Clear();
		foreach (int id in topIds)
		{
			if (payloads.ContainsKey(id) && !RootOrder.Contains(id))
				RootOrder.Add(id);
		}

		for (int i = topIds.Count - 1; i >= 0; i--)
			stack.Push((topIds[i], -1));

		while (stack.Count > 0)
		{
			var (cueId, parentId) = stack.Pop();
			if (!visited.Add(cueId))
				continue;
			if (!payloads.TryGetValue(cueId, out var data))
				continue;

			var cue = new Cue(data);
			cue.ParentId = parentId;
			if (!CueIndex.ContainsKey(cue.Id))
				CueIndex.Add(cue.Id, cue);
			else
				CueIndex[cue.Id] = cue;

			for (int i = cue.ChildCues.Count - 1; i >= 0; i--)
				stack.Push((cue.ChildCues[i], cue.Id));
		}

		foreach (int id in payloads.Keys)
		{
			if (visited.Contains(id))
				continue;
			if (!payloads.TryGetValue(id, out var data))
				continue;
			var cue = new Cue(data);
			cue.ParentId = -1;
			if (!CueIndex.ContainsKey(cue.Id))
				CueIndex.Add(cue.Id, cue);
			if (!RootOrder.Contains(cue.Id))
				RootOrder.Add(cue.Id);
			visited.Add(id);
		}

		// Repair a malformed ChildCues graph (cycles, shared or missing children) before
		// anything binds or walks it (R1-P1-03).
		SanitizeChildGraph(visited);

		SessionLoadTimer.Current?.Begin("cues.relink");
		if (CueIndex != null)
		{
			foreach (var cue in CueIndex.Values)
				RelinkCueComponents(cue);
		}

		var timer = SessionLoadTimer.Current;
		timer?.Pause();
		timer?.AnnotateLast(timer.FormatLinkNote());
	}

	/// <summary>Deepest group nesting accepted from a showfile; deeper children are detached.</summary>
	internal const int MaxChildDepth = 256;

	/// <summary>
	/// Makes this list's loaded <see cref="Cue.ChildCues"/> graph a forest. Among the
	/// cues in <paramref name="ownIds"/>, removes child entries that point back up the current
	/// path (a loop, including a cue listing itself), that another group already claimed, or
	/// that sit deeper than <see cref="MaxChildDepth"/>. Fixes <see cref="Cue.ParentId"/> to
	/// match, removes grouped cues from <see cref="RootOrder"/>, and puts any cue left without a
	/// parent back at top level. Child ids outside <paramref name="ownIds"/> (missing, or owned by
	/// another list) are left as they are; the walks that follow them have their own depth caps.
	/// Each repair is logged as a warning. The UI cannot create such a graph
	/// (CueReorder.WouldCreateCycle); this guards hand-edited or tool-made files.
	/// </summary>
	/// <param name="ownIds">Ids of the cues constructed by this load.</param>
	private void SanitizeChildGraph(HashSet<int> ownIds)
	{
		if (CueIndex == null || ownIds == null || ownIds.Count == 0)
			return;

		var claimed = new HashSet<int>();
		var onPath = new HashSet<int>();
		int repairs = 0;

		void Repair(string message)
		{
			repairs++;
			GD.PrintErr($"CueList:SanitizeChildGraph - {message}");
			_globalSignals?.EmitSignal(nameof(GlobalSignals.Log),
				$"Repaired showfile: {message}", (int)LogType.Warning);
		}

		void Visit(Cue cue, int depth)
		{
			onPath.Add(cue.Id);
			for (int i = 0; i < cue.ChildCues.Count; i++)
			{
				int id = cue.ChildCues[i];
				if (!ownIds.Contains(id) || !CueIndex.TryGetValue(id, out var child) || child == null)
					continue;

				string reason = null;
				if (onPath.Contains(id))
					reason = "it would form a loop";
				else if (claimed.Contains(id))
					reason = "it already belongs to another group";
				else if (depth >= MaxChildDepth)
					reason = $"groups are nested deeper than {MaxChildDepth}";

				if (reason != null)
				{
					cue.ChildCues.RemoveAt(i);
					i--;
					Repair($"removed child cue {id} from cue {cue.Id} ({reason}).");
					continue;
				}

				claimed.Add(id);
				child.ParentId = cue.Id;
				Visit(child, depth + 1);
			}
			onPath.Remove(cue.Id);
		}

		// Roots first, in order; a cue listed both at top level and inside an earlier group
		// keeps the group placement the loader already gave it.
		foreach (int rootId in new List<int>(RootOrder))
		{
			if (!ownIds.Contains(rootId) || claimed.Contains(rootId))
				continue;
			if (!CueIndex.TryGetValue(rootId, out var root) || root == null)
				continue;
			claimed.Add(rootId);
			root.ParentId = -1;
			Visit(root, 0);
		}

		for (int i = RootOrder.Count - 1; i >= 0; i--)
		{
			int id = RootOrder[i];
			if (ownIds.Contains(id) && CueIndex.TryGetValue(id, out var cue) && cue != null && cue.ParentId >= 0)
			{
				RootOrder.RemoveAt(i);
				Repair($"cue {id} was listed both at top level and inside group {cue.ParentId}; kept it in the group.");
			}
		}

		// Cues cut loose by a repair (only reachable through a removed entry) go back to top level.
		foreach (int id in ownIds)
		{
			if (claimed.Contains(id) || !CueIndex.TryGetValue(id, out var cue) || cue == null)
				continue;
			claimed.Add(id);
			cue.ParentId = -1;
			RootOrder.Add(id);
			Repair($"cue {id} had no valid parent after repair; moved it to top level.");
			Visit(cue, 0);
		}

		if (repairs > 0)
		{
			_globalSignals?.EmitSignal(nameof(GlobalSignals.Log),
				$"Showfile had {repairs} group-structure problem(s) and was repaired in memory. Save to keep the fix.",
				(int)LogType.Warning);
		}
	}

	/// <summary>
	/// Binds the virtual viewport after <see cref="LoadCueModels"/>. Suppresses per-node
	/// keyboard-policy wiring during instantiate, then scans the container once.
	/// </summary>
	public void BindLoadedViewport()
	{
		var signals = _globalSignals;
		bool prevSuppress = false;
		if (signals != null)
		{
			prevSuppress = signals.SuppressUiKeyboardScan;
			signals.SuppressUiKeyboardScan = true;
		}

		try
		{
			SessionLoadTimer.Current?.Begin("finish");
			// Rebuilds visible rows, binds shells, and notifies the footer cue count.
			NotifyVirtualStructureChanged();
		}
		finally
		{
			if (signals != null)
			{
				signals.SuppressUiKeyboardScan = prevSuppress;
				if (_cueContainer != null && IsInstanceValid(_cueContainer))
					signals.ScanForUiKeyboardPolicy(_cueContainer);
			}

			SessionLoadTimer.Current?.Pause();
		}
	}

	/// <summary>
	/// Maps showfile cue ids to their payload dictionaries (no extra key-remap copy).
	/// </summary>
	private static System.Collections.Generic.Dictionary<int, Dictionary> IndexCuePayloads(Dictionary cueData)
	{
		var map = new System.Collections.Generic.Dictionary<int, Dictionary>();
		if (cueData == null || !cueData.TryGetValue("Cues", out var cuesVar))
			return map;
		if (cuesVar.VariantType != Variant.Type.Dictionary)
			return map;

		foreach (var kv in (Dictionary)cuesVar)
		{
			if (kv.Value.VariantType != Variant.Type.Dictionary)
				continue;
			var data = kv.Value.AsGodotDictionary();
			int id;
			if (data.ContainsKey("Id"))
				id = data["Id"].AsInt32();
			else if (!int.TryParse(kv.Key.ToString(), out id))
				continue;
			if (!data.ContainsKey("Id"))
				data["Id"] = id;
			map[id] = data;
		}

		return map;
	}

	/// <summary>
	/// Top-level ids from <c>CueOrder</c> (position order). If missing, uses payloads with ParentId -1.
	/// </summary>
	private static List<int> ParseTopLevelCueIds(
		Dictionary cueData,
		System.Collections.Generic.Dictionary<int, Dictionary> payloads)
	{
		var ids = new List<int>();
		if (cueData != null &&
		    cueData.TryGetValue("CueOrder", out var orderVar) &&
		    orderVar.VariantType == Variant.Type.Dictionary)
		{
			var pairs = new List<(int Pos, int Id)>();
			foreach (var kv in (Dictionary)orderVar)
				pairs.Add(((int)kv.Key, (int)kv.Value));
			pairs.Sort((a, b) => a.Pos.CompareTo(b.Pos));
			foreach (var pair in pairs)
				ids.Add(pair.Id);
			return ids;
		}

		if (payloads == null)
			return ids;
		foreach (var kv in payloads)
		{
			int parentId = kv.Value.ContainsKey("ParentId") ? kv.Value["ParentId"].AsInt32() : -1;
			if (parentId < 0)
				ids.Add(kv.Key);
		}

		return ids;
	}
	
	/// <summary>
	/// Applies top-level <paramref name="cueOrder"/> to <see cref="RootOrder"/> and rebinds
	/// the virtual viewport. Child order is already on each cue's <c>ChildCues</c>.
	/// </summary>
	private void StructureCuelist(Godot.Collections.Dictionary<int, int> cueOrder)
	{
		RootOrder.Clear();
		if (cueOrder != null)
		{
			for (int i = 0; i < cueOrder.Count; i++)
			{
				if (!cueOrder.TryGetValue(i, out int id))
					continue;
				if (CueIndex.ContainsKey(id) && !RootOrder.Contains(id))
					RootOrder.Add(id);
			}
		}

		NotifyVirtualStructureChanged();
	}
}
