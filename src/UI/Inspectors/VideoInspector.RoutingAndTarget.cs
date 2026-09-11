// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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
using Cue2.Media.Audio;
using Godot;
using Cue2.UI.Preview;

namespace Cue2.UI.Inspectors;

/// <summary>
/// Inspector for video/image components. Supports multi-edit when Settings multi-edit is on
/// and multiple cues are selected (applies to cues that have a video component).
/// </summary>
/// <summary>
/// Partial: Target layer, expand/stretch/opacity, output options, routing matrix
/// </summary>
public partial class VideoInspector
{

	/// <summary>
	/// Refreshes the target-layer list when layers are added/removed/renamed, or when cues are
	/// reassigned after a layer delete (unassign / replace) while this inspector is open.
	/// Preserves the cue's stored <see cref="VideoComponent.TargetLayerId"/> (no failover).
	/// </summary>
	private void OnDisplaysChangedForTargetLayers()
	{
		if (_focusedCue == null)
			return;

		// Re-bind from live cue — external reassignment may have changed TargetLayerId.
		var live = CueList.FetchCueFromId(_focusedCue.Id);
		if (live != null)
		{
			_focusedCue = live;
			_focusedVideoComponent = live.GetVideoComponent();
		}

		if (_focusedVideoComponent == null)
			return;

		PopulateTargetLayerOptions();
		if (_videoPreviewer != null && _focusedVideoComponent.TargetLayerId >= 0)
			_videoPreviewer.SetAreasDeferred(_focusedVideoComponent.TargetLayerId);
	}

	/// <summary>
	/// Builds the target-layer OptionButton: "No Output", live layers, or a missing-layer entry.
	/// Does not rewrite <see cref="VideoComponent.TargetLayerId"/> when the layer is gone
	/// (no silent failover to another layer).
	/// </summary>
	private void PopulateTargetLayerOptions()
	{
		if (_targetLayerOptionButton == null || _focusedVideoComponent == null)
			return;

		// Block ItemSelected while rebuilding — OptionButton would otherwise auto-select
		// the first real layer and overwrite a missing / No Output assignment.
		_targetLayerOptionButton.SetBlockSignals(true);
		try
		{
			_targetLayerOptionButton.Clear();

			// Index 0: explicit none. Use metadata so id does not collide with layer 0
			// (Godot remaps AddItem id -1 to the item index).
			_targetLayerOptionButton.AddItem(UiLocalizer.T("No Output"));
			_targetLayerOptionButton.SetItemMetadata(0, -1);

			int targetId = _focusedVideoComponent.TargetLayerId;
			int selectedIndex = 0;
			bool matched = targetId < 0; // -1 = No Output

			if (DisplaysManager.Layers != null)
			{
				foreach (var layer in DisplaysManager.Layers)
				{
					if (layer == null) continue;
					_targetLayerOptionButton.AddItem(layer.LayerName);
					int idx = _targetLayerOptionButton.ItemCount - 1;
					_targetLayerOptionButton.SetItemMetadata(idx, layer.LayerId);
					if (layer.LayerId == targetId)
					{
						selectedIndex = idx;
						matched = true;
					}
				}
			}

			// Keep the stored id when the layer was deleted — show missing entry, do not reassign.
			if (!matched && targetId >= 0)
			{
				_targetLayerOptionButton.AddItem(UiLocalizer.T("!!! Missing Layer"));
				int missIdx = _targetLayerOptionButton.ItemCount - 1;
				_targetLayerOptionButton.SetItemMetadata(missIdx, targetId);
				selectedIndex = missIdx;
			}

			_targetLayerOptionButton.Select(selectedIndex);
		}
		finally
		{
			_targetLayerOptionButton.SetBlockSignals(false);
		}
	}

	/// <summary>
	/// Handles target layer selection.
	/// </summary>
	/// <param name="index">The selected index.</param>
	private void TargetLayerSelected(long index)
	{
		var targets = GetVideoTargets();
		if (targets.Count == 0) return;
		if (_isSyncingUi || _globalData?.HistoryManager?.IsRestoring == true) return;
		if (_targetLayerOptionButton == null) return;

		var item = _targetLayerOptionButton.GetItemText((int)index);
		if (item != null && item.StartsWith("!!! Missing"))
		{
			// Keep stored missing id; do not reassign.
			return;
		}

		int layerId = (int)_targetLayerOptionButton.GetItemMetadata((int)index);
		if (targets.All(t => t.Component.TargetLayerId == layerId)) return;

		RecordVideoHistory("Edit video target layer");
		foreach (var (cue, comp) in targets)
		{
			comp.TargetLayerId = layerId;
			GetNodeOrNull<MediaHealthService>("/root/MediaHealthService")?.CheckCue(cue.Id);
		}
		if (layerId >= 0)
			_videoPreviewer?.SetAreasDeferred(layerId);
		GD.Print($"VideoInspector:TargetLayerSelected - Target layer set to ID {layerId}");
	}

	private void ExpandModeSelected(long index)
	{
		var targets = GetVideoTargets();
		if (targets.Count == 0 || _expandModeOptionButton == null)
			return;
		if (_isSyncingUi || _globalData?.HistoryManager?.IsRestoring == true) return;

		int id = _expandModeOptionButton.GetItemId((int)index);
		if (targets.All(t => (int)t.Component.TextureExpandMode == id)) return;
		RecordVideoHistory("Edit video expand mode");
		foreach (var (_, comp) in targets)
			comp.TextureExpandMode = (TextureRect.ExpandModeEnum)id;
		if (_focusedVideoComponent != null)
			_videoPreviewer?.ApplyTextureLayout(_focusedVideoComponent);
		ApplyVisualsToPlayingCues();
		GD.Print($"VideoInspector:ExpandModeSelected - Expand mode id {id}");
	}

	private void StretchModeSelected(long index)
	{
		var targets = GetVideoTargets();
		if (targets.Count == 0 || _stretchModeOptionButton == null)
			return;
		if (_isSyncingUi || _globalData?.HistoryManager?.IsRestoring == true) return;

		int id = _stretchModeOptionButton.GetItemId((int)index);
		if (targets.All(t => (int)t.Component.TextureStretchMode == id)) return;
		RecordVideoHistory("Edit video stretch mode");
		foreach (var (_, comp) in targets)
			comp.TextureStretchMode = (TextureRect.StretchModeEnum)id;
		if (_focusedVideoComponent != null)
			_videoPreviewer?.ApplyTextureLayout(_focusedVideoComponent);
		ApplyVisualsToPlayingCues();
		GD.Print($"VideoInspector:StretchModeSelected - Stretch mode id {id}");
	}

	/// <summary>
	/// Parses opacity as a percentage (0–100) and stores 0–1 on the component.
	/// </summary>
	private void OnOpacitySubmitted(string text)
	{
		var targets = GetVideoTargets();
		if (targets.Count == 0 || _opacityLineEdit == null)
			return;
		if (_isSyncingUi || _globalData?.HistoryManager?.IsRestoring == true) return;

		try
		{
			string cleaned = (text ?? string.Empty).Replace("%", "").Trim();
			if (!float.TryParse(cleaned, out float pct))
			{
				_globalSignals.EmitSignal(nameof(GlobalSignals.Log), $"Invalid opacity: {text}", 1);
				if (_focusedVideoComponent != null)
					_opacityLineEdit.Text = $"{_focusedVideoComponent.Opacity * 100f:0.#}";
				if (_opacityLineEdit.HasFocus()) _opacityLineEdit.ReleaseFocus();
				return;
			}

			pct = Mathf.Clamp(pct, 0f, 100f);
			float opacity = pct / 100f;
			if (targets.All(t => Math.Abs(t.Component.Opacity - opacity) < 1e-6f))
			{
				_opacityLineEdit.Text = $"{pct:0.#}";
				if (_opacityLineEdit.HasFocus()) _opacityLineEdit.ReleaseFocus();
				return;
			}
			RecordVideoHistory("Edit video opacity");
			foreach (var (_, comp) in targets)
				comp.Opacity = opacity;
			_opacityLineEdit.Text = $"{pct:0.#}";
			_videoPreviewer?.ApplyOpacity(opacity);
			ApplyVisualsToPlayingCues();
			if (_opacityLineEdit.HasFocus()) _opacityLineEdit.ReleaseFocus();
			GD.Print($"VideoInspector:OnOpacitySubmitted - Opacity set to {pct:0.#}%");
		}
		catch (Exception ex)
		{
			_globalSignals.EmitSignal(nameof(GlobalSignals.Log), $"Error parsing opacity: {ex.Message}", 2);
			if (_focusedVideoComponent != null)
				_opacityLineEdit.Text = $"{_focusedVideoComponent.Opacity * 100f:0.#}";
			if (_opacityLineEdit.HasFocus()) _opacityLineEdit.ReleaseFocus();
		}
	}

	/// <summary>
	/// Pushes expand/stretch/opacity to any currently playing instance of this video component.
	/// </summary>
	private void ApplyVisualsToPlayingCues()
	{
		if (_focusedVideoComponent == null)
			return;

		// CueCommandExecutor is owned by GlobalData.
		_globalData?.CueCommandExecutor?.RefreshPlayingVideoVisuals(_focusedVideoComponent);
	}

	
	/// <summary>
	/// Populates the output option button with available audio outputs.
	/// </summary>
	private void PopulateOutputOptions()
	{
		if (_outputOptionButton == null || _focusedVideoComponent == null) return;

		// Keep PatchId aligned with the live Patch reference (create/drop assigns both).
		if (_focusedVideoComponent.Patch != null && GodotObject.IsInstanceValid(_focusedVideoComponent.Patch)
		    && _focusedVideoComponent.PatchId != _focusedVideoComponent.Patch.Id)
		{
			_focusedVideoComponent.PatchId = _focusedVideoComponent.Patch.Id;
		}

		int assignedPatchId = _focusedVideoComponent.Patch?.Id ?? _focusedVideoComponent.PatchId;

		_outputOptionButton.SetBlockSignals(true);
		try
		{
			var itemCount = _outputOptionButton.GetItemCount();
			for (int i = 0; i < itemCount; i++)
				_outputOptionButton.RemoveItem(_outputOptionButton.GetItemCount() - 1);

			_outputOptionButton.AddItem(UiLocalizer.T("No output"));
			int selectedIndex = 0;

			foreach (var patch in _globalData.Settings.GetAudioOutputPatches())
			{
				_outputOptionButton.AddItem(UiLocalizer.Tf("Patch: {0}", patch.Value.Name));
				int idx = _outputOptionButton.GetItemCount() - 1;
				_outputOptionButton.SetItemMetadata(idx, patch.Value.Id);
				if (patch.Value.Id == assignedPatchId)
					selectedIndex = idx;
			}

			foreach (var output in _audioDevices.GetAvailableAudioDeviceNames())
			{
				_outputOptionButton.AddItem(UiLocalizer.Tf("Direct Output: {0}", output));
				int idx = _outputOptionButton.GetItemCount() - 1;
				if (!string.IsNullOrEmpty(_focusedVideoComponent.DirectOutput)
				    && output == _focusedVideoComponent.DirectOutput)
				{
					selectedIndex = idx;
				}
			}

			if (selectedIndex == 0 && !string.IsNullOrEmpty(_focusedVideoComponent.DirectOutput))
			{
				_outputOptionButton.AddItem(UiLocalizer.Tf("!!! Missing output: {0}", _focusedVideoComponent.DirectOutput));
				selectedIndex = _outputOptionButton.GetItemCount() - 1;
			}
			if (selectedIndex == 0 && assignedPatchId >= 0)
			{
				string name = _focusedVideoComponent.Patch?.Name ?? $"ID {assignedPatchId}";
				_outputOptionButton.AddItem(UiLocalizer.Tf("!!! Missing patch: {0}", name));
				selectedIndex = _outputOptionButton.GetItemCount() - 1;
			}

			_outputOptionButton.Select(selectedIndex);
		}
		finally
		{
			_outputOptionButton.SetBlockSignals(false);
		}
	}

	/// <summary>
	/// Builds the routing matrix for audio channels.
	/// </summary>
	private void BuildRoutingMatrix()
	{
		TaskUtil.Run(BuildRoutingMatrixAsync, "VideoInspector.BuildRoutingMatrix");
	}

	private async Task BuildRoutingMatrixAsync()
	{
		if (_routingMatrixGrid == null)
			return;

		int shellGen = _shellSelectGeneration;
		int buildGen = ++_routingMatrixBuildGeneration;

		if (_focusedVideoComponent == null || !_focusedVideoComponent.HasAudio || !_focusedVideoComponent.UseAudio)
		{
			ClearVideoRoutingMatrixUi();
			if (_routingContainer != null)
				_routingContainer.Visible = false;
			return;
		}

		if (_focusedVideoComponent.Metadata == null)
		{
			// Defer — drop-create focus can run before metadata is probed. Avoid wiping a valid grid.
			return;
		}

		if (!TryResolveVideoRoutingIo(out int inputChannels, out var inputLabels,
			    out int outputChannels, out var outputLabels))
			return;

		EnsureVideoRoutingPatchShape(inputChannels, inputLabels, outputChannels, outputLabels);

		string structureKey =
			$"{_focusedVideoComponent.PatchId}|{_focusedVideoComponent.DirectOutput ?? ""}|{inputChannels}x{outputChannels}|{string.Join(',', inputLabels)}|{string.Join(',', outputLabels)}";

		if (structureKey == _routingMatrixStructureKey
		    && _routingVolumeEdits.Count == inputChannels * outputChannels
		    && _routingMatrixGrid.GetChildCount() > 0)
		{
			RefreshVideoRoutingMatrixValues(inputChannels, outputChannels, inputLabels);
			if (_routingContainer != null)
				_routingContainer.Visible = true;
			return;
		}

		_routingInputLabels.Clear();
		_routingVolumeEdits.Clear();
		foreach (var child in _routingMatrixGrid.GetChildren())
			child.QueueFree();

		await ToSignal(GetTree(), "process_frame");
		if (shellGen != _shellSelectGeneration
		    || buildGen != _routingMatrixBuildGeneration
		    || _focusedVideoComponent == null
		    || !IsInstanceValid(this))
			return;

		if (!TryResolveVideoRoutingIo(out inputChannels, out inputLabels, out outputChannels, out outputLabels))
			return;
		EnsureVideoRoutingPatchShape(inputChannels, inputLabels, outputChannels, outputLabels);
		structureKey =
			$"{_focusedVideoComponent.PatchId}|{_focusedVideoComponent.DirectOutput ?? ""}|{inputChannels}x{outputChannels}|{string.Join(',', inputLabels)}|{string.Join(',', outputLabels)}";

		_routingMatrixGrid.Columns = outputChannels + 1;
		_routingMatrixGrid.AddChild(new Label { Text = "" });
		foreach (var label in outputLabels)
			_routingMatrixGrid.AddChild(new Label { Text = label, HorizontalAlignment = HorizontalAlignment.Center });

		string panStatus = inputChannels == 2
			? UiUtilities.FormatPan(_focusedVideoComponent.Pan)
			: null;
		for (int row = 0; row < inputChannels; row++)
		{
			string labelText = inputLabels[row];
			if (panStatus != null && row < 2)
				labelText = $"{labelText} ({panStatus})";
			var inLabel = new Label { Text = labelText };
			_routingMatrixGrid.AddChild(inLabel);
			_routingInputLabels.Add(inLabel);

			for (int col = 0; col < outputChannels; col++)
			{
				var volumeEdit = new LineEdit();
				float linearVol = _focusedVideoComponent.Routing.GetVolume(row, col);
				if (linearVol > 0.0f)
					volumeEdit.Text = $"{UiUtilities.LinearToDb(linearVol)}dB";
				int row1 = row;
				int col1 = col;
				volumeEdit.TextSubmitted += newText => OnMatrixVolumeSubmitted(newText, volumeEdit, row1, col1);
				volumeEdit.FocusExited += () => OnMatrixVolumeSubmitted(volumeEdit.Text, volumeEdit, row1, col1);
				// Routing matrix stays unity-max (no digital boost); boost lives on component volume.
				LineEditDbDragSlider.EnableUnityVolume(volumeEdit);
				_routingMatrixGrid.AddChild(volumeEdit);
				_routingVolumeEdits.Add(volumeEdit);
			}
		}

		_routingMatrixStructureKey = structureKey;
		if (_routingContainer != null)
			_routingContainer.Visible = true;
	}

	private void ClearVideoRoutingMatrixUi()
	{
		_routingInputLabels.Clear();
		_routingVolumeEdits.Clear();
		_routingMatrixStructureKey = null;
		if (_routingMatrixGrid == null) return;
		foreach (var child in _routingMatrixGrid.GetChildren())
			child.QueueFree();
	}

	private void RefreshVideoRoutingMatrixValues(int inputChannels, int outputChannels, List<string> inputLabels)
	{
		string panStatus = inputChannels == 2
			? UiUtilities.FormatPan(_focusedVideoComponent.Pan)
			: null;
		for (int row = 0; row < inputChannels && row < _routingInputLabels.Count; row++)
		{
			string labelText = inputLabels[row];
			if (panStatus != null && row < 2)
				labelText = $"{labelText} ({panStatus})";
			_routingInputLabels[row].Text = labelText;
		}

		int idx = 0;
		for (int row = 0; row < inputChannels; row++)
		{
			for (int col = 0; col < outputChannels; col++, idx++)
			{
				if (idx >= _routingVolumeEdits.Count) return;
				var edit = _routingVolumeEdits[idx];
				if (edit == null || !IsInstanceValid(edit) || edit.HasFocus())
					continue;
				float linearVol = _focusedVideoComponent.Routing.GetVolume(row, col);
				edit.Text = linearVol > 0.0f ? $"{UiUtilities.LinearToDb(linearVol)}dB" : string.Empty;
			}
		}
	}

	private bool TryResolveVideoRoutingIo(
		out int inputChannels,
		out List<string> inputLabels,
		out int outputChannels,
		out List<string> outputLabels)
	{
		inputChannels = 0;
		inputLabels = null;
		outputChannels = 0;
		outputLabels = new List<string>();
		if (_focusedVideoComponent?.Metadata == null)
			return false;

		inputChannels = _focusedVideoComponent.Metadata.AudioChannels;
		inputLabels = GetChannelLabels(inputChannels, isInput: true);

		if (_focusedVideoComponent.Patch != null && GodotObject.IsInstanceValid(_focusedVideoComponent.Patch)
		    && _focusedVideoComponent.PatchId != _focusedVideoComponent.Patch.Id)
		{
			_focusedVideoComponent.PatchId = _focusedVideoComponent.Patch.Id;
		}

		if (_focusedVideoComponent.PatchId != -1 || _focusedVideoComponent.Patch != null)
		{
			AudioOutputPatch patch = _focusedVideoComponent.Patch;
			if (patch == null || !GodotObject.IsInstanceValid(patch))
			{
				_globalData.Settings.GetAudioOutputPatches()
					.TryGetValue(_focusedVideoComponent.PatchId, out patch);
			}

			if (patch == null || !GodotObject.IsInstanceValid(patch))
			{
				_globalSignals.EmitSignal(nameof(GlobalSignals.Log),
					$"VideoInspector:BuildRoutingMatrix - Patch ID {_focusedVideoComponent.PatchId} not found, resetting output", 2);
				_focusedVideoComponent.Patch = null;
				_focusedVideoComponent.PatchId = -1;
				_focusedVideoComponent.Routing = null;
				PopulateOutputOptions();
				if (_routingContainer != null)
					_routingContainer.Visible = false;
				ClearVideoRoutingMatrixUi();
				return false;
			}

			_focusedVideoComponent.Patch = patch;
			_focusedVideoComponent.PatchId = patch.Id;
			outputChannels = patch.Channels.Count;
			outputLabels = patch.Channels.OrderBy(kv => kv.Key).Select(kv => kv.Value).ToList();
			return true;
		}

		if (!string.IsNullOrEmpty(_focusedVideoComponent.DirectOutput))
		{
			var device = _audioDevices.OpenAudioDevice(_focusedVideoComponent.DirectOutput, out var _);
			if (device == null)
			{
				_globalSignals.EmitSignal(nameof(GlobalSignals.Log),
					$"VideoInspector:BuildRoutingMatrix - Direct output device not found: {_focusedVideoComponent.DirectOutput}", 2);
				_focusedVideoComponent.DirectOutput = null;
				PopulateOutputOptions();
				if (_routingContainer != null)
					_routingContainer.Visible = false;
				ClearVideoRoutingMatrixUi();
				return false;
			}
			outputChannels = device.Channels;
			for (int i = 0; i < outputChannels; i++)
				outputLabels.Add($"Channel {i}");
			return true;
		}

		if (_routingContainer != null)
			_routingContainer.Visible = false;
		ClearVideoRoutingMatrixUi();
		return false;
	}

	private void EnsureVideoRoutingPatchShape(
		int inputChannels,
		List<string> inputLabels,
		int outputChannels,
		List<string> outputLabels)
	{
		var routing = _focusedVideoComponent.Routing;
		bool needsUpdate = routing == null ||
		                   routing.OutputChannels != outputChannels ||
		                   !routing.OutputLabels.SequenceEqual(outputLabels) ||
		                   routing.InputChannels != inputChannels ||
		                   !routing.InputLabels.SequenceEqual(inputLabels);
		if (!needsUpdate)
			return;

		var oldRouting = routing;
		routing = new CuePatch(inputChannels, inputLabels, outputChannels, outputLabels);
		_focusedVideoComponent.Routing = routing;
		if (oldRouting == null)
			return;
		int copyInputs = Math.Min(oldRouting.InputChannels, inputChannels);
		int copyOutputs = Math.Min(oldRouting.OutputChannels, outputChannels);
		for (int i = 0; i < copyInputs; i++)
		{
			for (int j = 0; j < copyOutputs; j++)
				routing.SetVolume(i, j, oldRouting.GetVolume(i, j));
		}
	}

	/// <summary>
	/// Handles matrix volume submission. Converts dB to linear and updates CuePatch.
	/// </summary>
	/// <param name="text">Submitted text.</param>
	/// <param name="textField">LineEdit field.</param>
	/// <param name="inputCh">Input channel index.</param>
	/// <param name="outputCh">Output channel index.</param>
	private void OnMatrixVolumeSubmitted(string text, LineEdit textField, int inputCh, int outputCh)
	{
		if (_focusedCue == null || _focusedVideoComponent?.Routing == null || textField == null)
			return;
		if (_globalData?.HistoryManager?.IsRestoring == true)
			return;

		GD.Print($"VideoInspector:OnMatrixVolumeSubmitted - In {inputCh}. Out {outputCh}");
		try
		{
			float dbValue;
			if (string.IsNullOrWhiteSpace((text ?? string.Empty).Replace("dB", "").Trim()))
			{
				dbValue = -60.0f;
				_globalSignals.EmitSignal(nameof(GlobalSignals.Log), $"VideoInspector:OnMatrixVolumeSubmitted - Blank input treated as OFF for In {inputCh}, Out {outputCh}", 0);
			}
			else if (!float.TryParse((text ?? string.Empty).Replace("dB", "").Trim(), out dbValue))
			{
				_globalSignals.EmitSignal(nameof(GlobalSignals.Log), $"VideoInspector:OnMatrixVolumeSubmitted - Invalid matrix volume: {text}", 1);
				return;
			}

			// Matrix is unity-max only (−60…0 dB).
			float linear = UiUtilities.DbToUnityLinear(dbValue);
			var routingForSet = _focusedVideoComponent.Routing;
			float current = routingForSet.GetVolume(inputCh, outputCh);
			if (Math.Abs(current - linear) < 1e-6f)
			{
				if (linear > 0.0f)
					textField.Text = $"{UiUtilities.LinearToDb(linear)}dB";
				if (textField.HasFocus())
					textField.ReleaseFocus();
				return;
			}

			// Discrete cell commit — each matrix cell change is its own undo step.
			RecordVideoHistory("Edit video routing volume");
			routingForSet.SetVolume(inputCh, outputCh, linear);
			if (linear > 0.0f)
			{
				var dbReturn = UiUtilities.LinearToDb(linear);
				textField.Text = $"{dbReturn}dB";
			}
			else
			{
				textField.Text = string.Empty;
			}
			if (textField.HasFocus())
				textField.ReleaseFocus();
		}
		catch (Exception ex)
		{
			_globalSignals.EmitSignal(nameof(GlobalSignals.Log), $"VideoInspector:OnMatrixVolumeSubmitted - Error: {ex.Message}", 2);
		}
	}

	/// <summary>
	/// Generates channel labels for routing matrix.
	/// </summary>
	/// <param name="channels">Number of channels.</param>
	/// <param name="isInput">Whether these are input channels.</param>
	/// <returns>List of channel labels.</returns>
	private List<string> GetChannelLabels(int channels, bool isInput)
	{
		return channels switch
		{
			1 => new List<string> { "Mono" },
			2 => new List<string> { "Left", "Right" },
			4 => new List<string> { "Front Left", "Front Right", "Rear Left", "Rear Right" }, // Quad
			6 => new List<string> { "Front Left", "Front Right", "Center", "LFE", "Surround Left", "Surround Right" }, // 5.1
			8 => new List<string> { "Front Left", "Front Right", "Center", "LFE", "Surround Left", "Surround Right", "Surround Back Left", "Surround Back Right" }, // 7.1
			_ => Enumerable.Range(1, channels).Select(i => $"Ch {i}").ToList() // Fallback for others
		};
	}
}
