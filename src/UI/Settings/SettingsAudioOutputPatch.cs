using System.Threading.Tasks;
// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System.Collections.Generic;
using System.Linq;
using Cue2.Services;
using Godot;
using Cue2.UI.Utilities;

namespace Cue2.UI.Settings;

/// <summary>
/// Settings panel that hosts one <see cref="Cue2.UI.Settings.PatchMatrix.AudioOutputPatchMatrix"/> per patch.
/// Rebuilds fully after settings-scoped undo/redo so UI never holds freed patch references.
/// </summary>
public partial class SettingsAudioOutputPatch : ScrollContainer
{
	private GlobalData _globalData;
	private HistoryManager _historyManager;

	private Button _newPatchButton;

	/// <summary>
	/// Bumps on each rebuild request so overlapping async rebuilds do not recreate a stale tree.
	/// </summary>
	private int _rebuildGeneration;
	
	public override void _Ready()
	{
		_globalData = GetNode<GlobalData>("/root/GlobalData");
		_historyManager = _globalData?.HistoryManager;

		_newPatchButton = GetNode<Button>("%NewPatchButton");
		_newPatchButton.Pressed += NewPatchButtonPressed;
		
		DisplayPatchMatrix();
		VisibilityChanged += OnVisibilityChanged;

		if (_historyManager != null)
			_historyManager.HistoryRestored += OnHistoryRestored;

		var signals = GetNodeOrNull<GlobalSignals>("/root/GlobalSignals");
		if (signals != null)
			signals.NewSession += OnNewSession;
	
		UiLocalizer.LocalizeTree(this);
		var localeSignals = GetNodeOrNull<GlobalSignals>("/root/GlobalSignals");
		if (localeSignals != null)
			localeSignals.LocaleChanged += OnLocaleChanged;
}

	public override void _ExitTree()
	{
		var localeSignals = GetNodeOrNull<GlobalSignals>("/root/GlobalSignals");
		if (localeSignals != null)
			localeSignals.LocaleChanged -= OnLocaleChanged;

		VisibilityChanged -= OnVisibilityChanged;
		if (_historyManager != null)
			_historyManager.HistoryRestored -= OnHistoryRestored;

		var signals = GetNodeOrNull<GlobalSignals>("/root/GlobalSignals");
		if (signals != null)
			signals.NewSession -= OnNewSession;

		ClearAllPatchMatrices();
	}

	private void OnNewSession()
	{
		// Patches were freed — drop stale matrix UIs (and rebuild if this panel is open).
		if (!Visible)
		{
			ClearAllPatchMatrices();
			return;
		}
		RebuildAllPatchMatrices();
	}

	private void OnVisibilityChanged()
	{
		if (Visible)
			DisplayPatchMatrix();
	}

	/// <summary>
	/// After settings undo/redo that may touch AudioPatch / devices, rebuild all matrix UIs.
	/// Always discard existing matrix nodes — their <see cref="AudioOutputPatch"/> references
	/// are freed during restore and must never be reused.
	/// </summary>
	private void OnHistoryRestored(int scope)
	{
		if (scope != (int)HistoryManager.HistoryScope.Settings) return;
		// Even when hidden: drop stale matrices so the next show creates fresh Patch refs.
		if (!Visible)
		{
			ClearAllPatchMatrices();
			return;
		}
		// Full rebuild: free UI first so no node keeps a freed AudioOutputPatch reference.
		RebuildAllPatchMatrices();
	}

	private void NewPatchButtonPressed()
	{
		if (_globalData?.HistoryManager?.IsRestoring == true) return;
		_globalData?.HistoryManager?.RecordSettingsChange("Create audio output patch", null, "AudioPatch", "AudioDevices");
		_globalData.Settings.CreateNewPatch();
		DisplayPatchMatrix();
	}

	/// <summary>
	/// Removes all patch matrix instances from the container.
	/// </summary>
	private void ClearAllPatchMatrices()
	{
		var patchMatrixContainer = GetNodeOrNull<VBoxContainer>("%PatchesVBoxContainer");
		if (patchMatrixContainer == null || !IsInstanceValid(patchMatrixContainer))
			return;

		foreach (Node child in patchMatrixContainer.GetChildren().ToArray())
		{
			if (!IsInstanceValid(child)) continue;
			patchMatrixContainer.RemoveChild(child);
			child.QueueFree();
		}
	}

	/// <summary>
	/// Tears down and recreates every patch matrix from current Settings state.
	/// </summary>
	private void RebuildAllPatchMatrices()
	{
		TaskUtil.Run(RebuildAllPatchMatricesAsync, "SettingsAudioOutputPatch.RebuildAllPatchMatrices");
	}

	private async Task RebuildAllPatchMatricesAsync()
	{
		int generation = ++_rebuildGeneration;
		ClearAllPatchMatrices();
		// Wait one frame so QueueFree completes before re-instantiating.
		if (IsInstanceValid(this) && GetTree() != null)
			await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
		// A newer restore/rebuild superseded this one.
		if (generation != _rebuildGeneration) return;
		if (!IsInstanceValid(this) || !Visible) return;
		DisplayPatchMatrix();
	}

	/// <summary>
	/// Ensures a matrix UI exists for each patch in settings (and removes orphaned UIs).
	/// </summary>
	private void DisplayPatchMatrix()
	{
		if (!Visible) return;
		var patches = _globalData.Settings.GetAudioOutputPatches();

		VBoxContainer patchMatrixContainer = GetNode<VBoxContainer>("%PatchesVBoxContainer");
		PackedScene patchMatrixScene = SceneLoader.LoadPackedScene("uid://dgy2bmmm4rjpt", out _);

		var childList = patchMatrixContainer.GetChildren();
		var alreadyExistingPatches = new List<int>();
		
		foreach (Node child in childList)
		{
			var id = child.Get("PatchId").AsInt32();
			if (!patches.ContainsKey(id))
			{
				GD.Print($"SettingsAudioOutputPatch:DisplayPatchMatrix - Removing orphan matrix for patch {id}");
				child.QueueFree();
			}
			else
			{
				alreadyExistingPatches.Add(id);
			}
		}
		
		foreach (var patch in patches)
		{
			if (alreadyExistingPatches.Contains(patch.Key)) continue;
			
			GD.Print($"SettingsAudioOutputPatch:DisplayPatchMatrix - Creating matrix id={patch.Key} name={patch.Value.Name}");
			Node instance = patchMatrixScene.Instantiate();
			instance.Set("Patch", patch.Value);
			instance.Set("PatchId", patch.Key);
			patchMatrixContainer.AddChild(instance);
		}
	}

	/// <summary>
	/// Re-localizes panel chrome when the UI language changes.
	/// </summary>
	/// <param name="localeCode">New locale code.</param>
	private void OnLocaleChanged(string localeCode)
	{
		if (!GodotObject.IsInstanceValid(this))
			return;
		UiLocalizer.LocalizeTree(this);
	}

}
