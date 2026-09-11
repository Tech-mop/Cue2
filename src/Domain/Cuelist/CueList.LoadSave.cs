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
		saveTable.Add("CueOrder", cueOrder);
		foreach (var cue in CueIndex.Values)
		{
			var cueData = cue.GetData();
			
			cues.Add(cue.Id, cueData);
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
