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
using Cue2.Media.Audio;
using Cue2.UI.Utilities;
using Godot;
using Cue2.UI.Preview;

namespace Cue2.UI.Inspectors;

/// <summary>
/// Inspector for video/image components. Supports multi-edit when Settings multi-edit is on
/// and multiple cues are selected (applies to cues that have a video component).
/// </summary>
/// <summary>
/// Partial: Pan, subtitles, timing, loop, fades, scale/offset, use-audio, volume, output selection
/// </summary>
public partial class VideoInspector
{

	/// <summary>
	/// True when pan UI should be shown (stereo embedded audio only).
	/// </summary>
	private bool IsStereoAudioSource =>
		_focusedVideoComponent?.UseAudio == true
		&& _focusedVideoComponent.HasAudio
		&& _focusedVideoComponent.Metadata != null
		&& _focusedVideoComponent.Metadata.AudioChannels == 2;

	/// <summary>
	/// Shows or hides pan controls and syncs slider/text from the component.
	/// </summary>
	private void UpdatePanUiVisibilityAndValues()
	{
		bool show = IsStereoAudioSource;
		if (_panLabel != null) _panLabel.Visible = show;
		if (_panSlider != null) _panSlider.Visible = show;
		if (_panInput != null) _panInput.Visible = show;
		if (!show || _focusedVideoComponent == null) return;
		SyncPanUiFromComponent();
	}

	/// <summary>
	/// Writes pan slider and text from <see cref="VideoComponent.Pan"/> without firing handlers.
	/// </summary>
	private void SyncPanUiFromComponent()
	{
		if (_focusedVideoComponent == null) return;
		_isUpdatingPanUi = true;
		try
		{
			float pan = Mathf.Clamp(_focusedVideoComponent.Pan, -1f, 1f);
			if (_panSlider != null)
				_panSlider.SetValueNoSignal(Mathf.Round(pan * 100f));
			if (_panInput != null && !_panInput.HasFocus())
				_panInput.Text = UiUtilities.FormatPan(pan);
		}
		finally
		{
			_isUpdatingPanUi = false;
		}
	}

	private void OnPanSliderChanged(double value)
	{
		if (_isUpdatingPanUi || _isSyncingUi) return;
		var targets = GetVideoTargets();
		if (targets.Count == 0) return;
		if (_globalData?.HistoryManager?.IsRestoring == true) return;
		if (!IsStereoAudioSource) return;

		float pan = Mathf.Clamp((float)value / 100f, -1f, 1f);
		if (targets.All(t => Math.Abs(t.Component.Pan - pan) < 1e-6f)) return;

		RecordVideoHistory("Edit video audio pan", VideoCoalesceKey("pan"));
		foreach (var (_, comp) in targets)
			comp.Pan = pan;

		_isUpdatingPanUi = true;
		try
		{
			if (_panInput != null)
				_panInput.Text = UiUtilities.FormatPan(pan);
		}
		finally
		{
			_isUpdatingPanUi = false;
		}
		RefreshRoutingInputPanLabels();
	}

	private void OnPanSliderDragEnded(bool valueChanged)
	{
		var key = VideoCoalesceKey("pan");
		if (!string.IsNullOrEmpty(key))
			InspectorMultiEditSupport.EndCoalesce(_globalData, UseMultiHistory(), key, key);
	}

	/// <summary>
	/// Commits pan from the text field (C, L50, R25, −100…100).
	/// </summary>
	private void PanInputSubmitted(string text)
	{
		var targets = GetVideoTargets();
		if (targets.Count == 0 || _panInput == null) return;
		if (_globalData?.HistoryManager?.IsRestoring == true) return;
		if (_isUpdatingPanUi) return;
		if (!IsStereoAudioSource)
		{
			if (_panInput.HasFocus()) _panInput.ReleaseFocus();
			return;
		}

		if (!UiUtilities.TryParsePan(text, out float pan))
		{
			_globalSignals.EmitSignal(nameof(GlobalSignals.Log), $"Invalid pan format: {text}", 1);
			_panInput.Text = UiUtilities.FormatPan(_focusedVideoComponent?.Pan ?? 0f);
			if (_panInput.HasFocus()) _panInput.ReleaseFocus();
			return;
		}

		pan = Mathf.Clamp(pan, -1f, 1f);
		_panInput.Text = UiUtilities.FormatPan(pan);

		if (targets.All(t => Math.Abs(t.Component.Pan - pan) < 1e-6f))
		{
			SyncPanUiFromComponent();
			if (_panInput.HasFocus()) _panInput.ReleaseFocus();
			return;
		}

		RecordVideoHistory("Edit video audio pan");
		foreach (var (_, comp) in targets)
			comp.Pan = pan;
		SyncPanUiFromComponent();
		RefreshRoutingInputPanLabels();
		if (_panInput.HasFocus()) _panInput.ReleaseFocus();
	}

	/// <summary>
	/// Updates Left/Right routing matrix row labels with the current pan status in parentheses.
	/// </summary>
	private void RefreshRoutingInputPanLabels()
	{
		if (_routingInputLabels.Count == 0 || _focusedVideoComponent == null) return;
		if (_focusedVideoComponent.Metadata?.AudioChannels != 2) return;

		string panStatus = UiUtilities.FormatPan(_focusedVideoComponent.Pan);
		for (int i = 0; i < _routingInputLabels.Count && i < 2; i++)
		{
			var label = _routingInputLabels[i];
			if (label == null || !IsInstanceValid(label)) continue;
			string baseName = i == 0 ? "Left" : "Right";
			label.Text = $"{baseName} ({panStatus})";
		}
	}

	/// <summary>
	/// Shows or hides caption controls based on available text subtitle tracks.
	/// </summary>
	private void RefreshSubtitleUi()
	{
		if (_subtitleRow == null)
		{
			GD.PrintErr("VideoInspector:RefreshSubtitleUi - Subtitle row was not built.");
			return;
		}

		if (_focusedVideoComponent == null)
		{
			_subtitleRow.Visible = false;
			return;
		}

		bool isImage = _focusedVideoComponent.IsImage;
		var tracks = _focusedVideoComponent.Metadata?.SubtitleTracks;
		int totalSubs = tracks?.Count ?? 0;
		int textSubs = tracks?.Count(t => t != null && t.IsTextBased) ?? 0;
		bool hasText = !isImage && textSubs > 0;
		_subtitleRow.Visible = hasText;

		GD.Print(
			$"VideoInspector:RefreshSubtitleUi - visible={hasText} totalSubs={totalSubs} textSubs={textSubs} " +
			$"use={_focusedVideoComponent.UseSubtitles}");

		if (!hasText)
		{
			// Keep model consistent when file has no text tracks.
			if (_focusedVideoComponent.UseSubtitles)
				_focusedVideoComponent.UseSubtitles = false;
			return;
		}

		_useSubtitlesCheck?.SetPressedNoSignal(_focusedVideoComponent.UseSubtitles);

		if (_subtitleTrackOption != null)
		{
			_subtitleTrackOption.SetBlockSignals(true);
			try
			{
				_subtitleTrackOption.Clear();
				int selected = 0;
				int i = 0;
				foreach (var track in tracks)
				{
					if (track == null || !track.IsTextBased)
						continue;
					_subtitleTrackOption.AddItem(track.DisplayName);
					int idx = _subtitleTrackOption.ItemCount - 1;
					_subtitleTrackOption.SetItemMetadata(idx, track.StreamIndex);
					if (track.StreamIndex == _focusedVideoComponent.SubtitleStreamIndex
					    || (_focusedVideoComponent.SubtitleStreamIndex < 0 && i == 0))
						selected = idx;
					i++;
				}

				if (_subtitleTrackOption.ItemCount == 0)
				{
					_subtitleRow.Visible = false;
					return;
				}

				_subtitleTrackOption.Selected = selected;
				// Persist resolved stream if still default -1
				if (_focusedVideoComponent.SubtitleStreamIndex < 0)
					_focusedVideoComponent.SubtitleStreamIndex =
						(int)_subtitleTrackOption.GetItemMetadata(selected);
			}
			finally
			{
				_subtitleTrackOption.SetBlockSignals(false);
			}

			_subtitleTrackOption.Disabled = !_focusedVideoComponent.UseSubtitles;
		}

		bool hasTextComp = _focusedCue != null && _focusedCue.GetTextComponent() != null;
		if (_addTextForCcButton != null)
		{
			_addTextForCcButton.Visible = _focusedVideoComponent.UseSubtitles && !hasTextComp;
		}
	}

	private void OnUseSubtitlesToggled(bool pressed)
	{
		if (_focusedCue == null || _focusedVideoComponent == null)
			return;
		if (_globalData?.HistoryManager?.IsRestoring == true)
			return;
		if (_focusedVideoComponent.UseSubtitles == pressed)
			return;

		// Enabling CC may also auto-add a Text component — record once so one Undo
		// reverts the flag and the added text together (P1-08).
		bool willAddText = pressed && _focusedCue.GetTextComponent() == null;
		string historyDesc = willAddText
			? "Enable closed captions (add text)"
			: "Edit video closed captions";
		InspectorMultiEditSupport.RecordBeforeEdit(
			_globalData, multiHistory: false, _focusedCue, historyDesc);

		_focusedVideoComponent.UseSubtitles = pressed;

		if (pressed)
		{
			// Ensure a stream index is chosen.
			if (_focusedVideoComponent.SubtitleStreamIndex < 0)
			{
				var first = _focusedVideoComponent.Metadata?.GetDefaultTextSubtitleTrack();
				if (first != null)
					_focusedVideoComponent.SubtitleStreamIndex = first.StreamIndex;
			}

			if (willAddText)
			{
				var text = _focusedCue.AddTextComponent();
				// CC slave timing: hold until video stops.
				text.Duration = 0;
				text.RecalculateDuration();
				_focusedCue.CalculateTotalDuration();
				_globalSignals?.EmitSignal(nameof(GlobalSignals.SyncShellInspector));
			}
		}

		_focusedCue.CalculateTotalDuration();
		RefreshSubtitleUi();
	}

	private void OnSubtitleTrackSelected(long index)
	{
		if (_focusedCue == null || _focusedVideoComponent == null || _subtitleTrackOption == null)
			return;
		if (_globalData?.HistoryManager?.IsRestoring == true)
			return;

		int streamIndex = (int)_subtitleTrackOption.GetItemMetadata((int)index);
		if (_focusedVideoComponent.SubtitleStreamIndex == streamIndex)
			return;

		InspectorMultiEditSupport.RecordBeforeEdit(
			_globalData, multiHistory: false, _focusedCue, "Edit subtitle track");
		_focusedVideoComponent.SubtitleStreamIndex = streamIndex;
	}

	private void OnAddTextForCcPressed()
	{
		if (_focusedCue == null || _focusedCue.GetTextComponent() != null)
			return;
		if (_globalData?.HistoryManager?.IsRestoring == true)
			return;

		InspectorMultiEditSupport.RecordBeforeEdit(
			_globalData, multiHistory: false, _focusedCue, "Add text for closed captions");
		var text = _focusedCue.AddTextComponent();
		text.Duration = 0;
		text.RecalculateDuration();
		_focusedCue.CalculateTotalDuration();
		_globalSignals?.EmitSignal(nameof(GlobalSignals.SyncShellInspector));
		RefreshSubtitleUi();
	}

	/// <summary>
	/// Shows/hides timing controls for video vs still-image mode.
	/// Images: editable Duration only (0 = until stopped). Video: start/end/file duration + loop/playcount.
	/// </summary>
	/// <param name="isImage">True when the focused component is a still image.</param>
	private void ApplyImageVideoUiMode(bool isImage)
	{
		if (_startTimeLabel != null) _startTimeLabel.Visible = !isImage;
		if (_startTimeInput != null) _startTimeInput.Visible = !isImage;
		if (_endTimeLabel != null) _endTimeLabel.Visible = !isImage;
		if (_endTimeInput != null) _endTimeInput.Visible = !isImage;
		if (_fileDurationLabel != null) _fileDurationLabel.Visible = !isImage;
		if (_fileDurationValue != null) _fileDurationValue.Visible = !isImage;
		if (_loopPlayCountRow != null) _loopPlayCountRow.Visible = !isImage;

		if (_durationValue != null)
		{
			_durationValue.Editable = isImage;
			_durationValue.PlaceholderText = isImage ? "0 = until stopped" : "00m:00s.000ms";
			if (isImage)
			{
				_durationValue.TooltipText =
					UiLocalizer.T("How long the image stays on screen. 0 or blank = stay active until stopped.");
			}
		}
		if (_durationLabel != null)
			_durationLabel.Text = isImage ? "Duration:" : "Duration:";
	}

	/// <summary>
	/// Handles image hold-duration edits. 0 / blank / negative = until stopped.
	/// Applies to all multi-edit image targets.
	/// </summary>
	/// <param name="text">Submitted duration text.</param>
	private void OnImageDurationSubmitted(string text)
	{
		var targets = GetVideoTargets().Where(t => t.Component.IsImage).ToList();
		if (targets.Count == 0)
			return;
		if (_isSyncingUi || _globalData?.HistoryManager?.IsRestoring == true)
			return;

		try
		{
			double newDuration;
			if (string.IsNullOrWhiteSpace(text) ||
			    text.Trim() == "0" ||
			    text.Contains("until stopped", StringComparison.OrdinalIgnoreCase))
			{
				newDuration = 0;
			}
			else
			{
				var formatted = UiUtilities.ParseAndFormatTime(text, out double secs, out string _, out bool isValid);
				if (!isValid || string.IsNullOrEmpty(formatted))
				{
					_globalSignals.EmitSignal(nameof(GlobalSignals.Log),
						$"Invalid image duration: {text}", 1);
					UpdateVideoUiFields(_focusedVideoComponent?.VideoFile ?? string.Empty);
					if (_durationValue.HasFocus())
						_durationValue.ReleaseFocus();
					return;
				}
				newDuration = Math.Max(0, secs);
			}

			if (targets.All(t => Math.Abs(t.Component.Duration - newDuration) < 1e-9))
			{
				UpdateVideoUiFields(_focusedVideoComponent?.VideoFile ?? string.Empty);
				if (_durationValue.HasFocus())
					_durationValue.ReleaseFocus();
				return;
			}

			RecordVideoHistory("Edit image duration");
			foreach (var (_, comp) in targets)
				comp.Duration = newDuration;
			SyncDuration();
			UpdateVideoUiFields(_focusedVideoComponent?.VideoFile ?? string.Empty);
			if (_durationValue.HasFocus())
				_durationValue.ReleaseFocus();
			GD.Print($"VideoInspector:OnImageDurationSubmitted - Image hold duration set to {newDuration}s");
		}
		catch (Exception ex)
		{
			GD.PrintErr($"VideoInspector:OnImageDurationSubmitted - {ex.Message}");
			_globalSignals.EmitSignal(nameof(GlobalSignals.Log),
				$"Error parsing image duration: {ex.Message}", 2);
			UpdateVideoUiFields(_focusedVideoComponent?.VideoFile ?? string.Empty);
		}
	}

	private static void SelectOptionById(OptionButton button, int id)
	{
		if (button == null)
			return;

		for (int i = 0; i < button.ItemCount; i++)
		{
			if (button.GetItemId(i) == id)
			{
				button.Select(i);
				return;
			}
		}

		button.Select(0);
	}
	
	/// <summary>
	/// Handles submission of time fields (start/end). Parses input, updates component, and recalculates duration.
	/// Blank or -1 input sets end time to undefined (and start time to 0).
	/// Start times are clamped to [0, file duration]. End times at or beyond file duration become full (EndTime=-1).
	/// Applies to all multi-edit video targets (images use OnImageDurationSubmitted).
	/// </summary>
	/// <param name="text">The submitted text.</param>
	/// <param name="textField">The LineEdit field.</param>
	private void TimeFieldSubmitted(string text, LineEdit textField)
	{
		var targets = GetVideoTargets().Where(t => !t.Component.IsImage).ToList();
		if (targets.Count == 0 || textField == null)
			return;
		if (_isSyncingUi || _globalData?.HistoryManager?.IsRestoring == true)
			return;

		try
		{
			if (string.IsNullOrWhiteSpace(text) || text.Trim() == "-1")
			{
				if (textField == _startTimeInput)
				{
					if (targets.All(t => Math.Abs(t.Component.StartTime) < 1e-9))
						return;
					RecordVideoHistory("Edit video start time");
					foreach (var (_, comp) in targets)
						comp.StartTime = 0.0;
					textField.Text = "00:00.000";
					textField.TooltipText = UiLocalizer.T("00m:00s.000ms");
					GD.Print("VideoInspector:TimeFieldSubmitted - Start time reset to 0");
				}
				else if (textField == _endTimeInput)
				{
					if (targets.All(t => t.Component.EndTime < 0))
						return;
					RecordVideoHistory("Edit video end time");
					foreach (var (_, comp) in targets)
						comp.EndTime = -1.0; // Undefined = play to end
					double metaDur = _focusedVideoComponent?.Metadata?.Duration ?? 0;
					textField.Text = $"Full ({UiUtilities.FormatTime(metaDur)})";
					textField.TooltipText = UiLocalizer.T("End time undefined (plays full file)");
					GD.Print("VideoInspector:TimeFieldSubmitted - End time set to undefined (full)");
				}

				SyncDuration();
				if (textField.HasFocus())
					textField.ReleaseFocus();
				_ = DrawWaveform();
				return;
			}

			var time = UiUtilities.ParseAndFormatTime(text, out var timeSecs, out string labeledTime, out bool isValid);

			if (!isValid || string.IsNullOrEmpty(time))
			{
				_globalSignals.EmitSignal(nameof(GlobalSignals.Log), $"Invalid time format in {textField.Name}: {text}", 1);
				// Always re-sanitize the LineEdit so invalid text (e.g. "4'") cannot stick.
				RestoreVideoTimeFieldDisplay(textField);
				if (textField.HasFocus())
					textField.ReleaseFocus();
				return;
			}

			if (textField == _startTimeInput)
			{
				// Clamp start to [0, fileDuration] per target so start cannot exceed media length.
				bool anyChange = false;
				foreach (var (_, comp) in targets)
				{
					double clamped = comp.ClampStartTime(timeSecs);
					if (Math.Abs(comp.StartTime - clamped) >= 1e-9)
						anyChange = true;
				}

				if (!anyChange)
				{
					double displaySecs = _focusedVideoComponent != null
						? _focusedVideoComponent.ClampStartTime(timeSecs)
						: Math.Max(0.0, timeSecs);
					string displayTime = UiUtilities.FormatTime(displaySecs);
					textField.Text = displayTime;
					UiUtilities.ParseAndFormatTime(displayTime, out _, out string displayLabeled, out _);
					textField.TooltipText = displayLabeled;
					return;
				}

				RecordVideoHistory("Edit video start time");
				foreach (var (_, comp) in targets)
					comp.StartTime = comp.ClampStartTime(timeSecs);

				// Show primary's applied (possibly clamped) value.
				double primaryStart = _focusedVideoComponent?.StartTime ?? timeSecs;
				string primaryFormatted = UiUtilities.FormatTime(primaryStart);
				textField.Text = primaryFormatted;
				UiUtilities.ParseAndFormatTime(primaryFormatted, out _, out string primaryLabeled, out _);
				textField.TooltipText = primaryLabeled;

				SyncDuration();
				if (textField.HasFocus())
					textField.ReleaseFocus();
				_ = DrawWaveform();
				return;
			}
			else if (textField == _endTimeInput)
			{
				// At or beyond each file's duration = play to end for that target.
				bool anyChange = false;
				foreach (var (_, comp) in targets)
				{
					double fileDuration = comp.Metadata?.Duration ?? 0;
					if (fileDuration > 0 && timeSecs >= fileDuration)
					{
						if (comp.EndTime >= 0)
							anyChange = true;
					}
					else if (Math.Abs(comp.EndTime - timeSecs) >= 1e-9)
					{
						anyChange = true;
					}
				}

				if (!anyChange)
				{
					textField.Text = time;
					textField.TooltipText = labeledTime;
					return;
				}

				RecordVideoHistory("Edit video end time");
				foreach (var (_, comp) in targets)
				{
					double fileDuration = comp.Metadata?.Duration ?? 0;
					if (fileDuration > 0 && timeSecs >= fileDuration)
						comp.EndTime = -1.0;
					else
						comp.EndTime = timeSecs;
				}

				// Primary display: full if primary clamped, else formatted time.
				double primaryDur = _focusedVideoComponent?.Metadata?.Duration ?? 0;
				if (primaryDur > 0 && timeSecs >= primaryDur)
				{
					textField.Text = $"Full ({UiUtilities.FormatTime(primaryDur)})";
					textField.TooltipText = UiLocalizer.T("End time undefined (plays full file)");
				}
				else
				{
					textField.Text = time;
					textField.TooltipText = labeledTime;
				}

				SyncDuration();
				if (textField.HasFocus())
					textField.ReleaseFocus();
				_ = DrawWaveform();
				return;
			}

			textField.Text = time;
			textField.TooltipText = labeledTime;

			SyncDuration();
			if (textField.HasFocus())
				textField.ReleaseFocus();
			_ = DrawWaveform();

		}
		catch (Exception ex)
		{
			GD.Print($"VideoInspector:TimeFieldSubmitted - Error parsing time: {ex.Message}");
			_globalSignals.EmitSignal(nameof(GlobalSignals.Log), $"Error parsing time: {ex.Message}", 2);
			RestoreVideoTimeFieldDisplay(textField);
			if (textField != null && textField.HasFocus())
				textField.ReleaseFocus();
		}
	}

	/// <summary>
	/// Writes the current model start/end time back into a time LineEdit (after invalid input).
	/// </summary>
	private void RestoreVideoTimeFieldDisplay(LineEdit textField)
	{
		if (textField == null || _focusedVideoComponent == null)
			return;

		if (textField == _startTimeInput)
		{
			string formatted = UiUtilities.FormatTime(_focusedVideoComponent.StartTime);
			textField.Text = formatted;
			UiUtilities.ParseAndFormatTime(formatted, out _, out string labeled, out _);
			textField.TooltipText = labeled;
		}
		else if (textField == _endTimeInput)
		{
			double metaDur = _focusedVideoComponent.Metadata?.Duration ?? 0;
			if (_focusedVideoComponent.EndTime < 0)
			{
				textField.Text = $"Full ({UiUtilities.FormatTime(metaDur)})";
				textField.TooltipText = UiLocalizer.T("End time undefined (plays full file)");
			}
			else
			{
				string formatted = UiUtilities.FormatTime(_focusedVideoComponent.EndTime);
				textField.Text = formatted;
				UiUtilities.ParseAndFormatTime(formatted, out _, out string labeled, out _);
				textField.TooltipText = labeled;
			}
		}
	}

	private void OnLoopToggled(bool state)
	{
		if (_isSyncingUi) return;
		var targets = GetVideoTargets();
		if (targets.Count == 0) return;
		if (_globalData?.HistoryManager?.IsRestoring == true) return;
		if (targets.All(t => t.Component.Loop == state)) return;
		RecordVideoHistory("Edit video loop");
		foreach (var (_, comp) in targets)
			comp.Loop = state;
		SyncDuration();
	}

	/// <summary>
	/// Re-binds the video component from the live cue and refreshes fields (undo/redo, external edits).
	/// </summary>
	private void OnSyncFromHistory()
	{
		TaskUtil.Run(OnSyncFromHistoryAsync, "VideoInspector.OnSyncFromHistory");
	}

	private async Task OnSyncFromHistoryAsync()
	{
		if (!IsInsideTree()) return;
		if (_focusedCue != null || InspectorMultiEditSupport.ShouldUseMultiEdit(_globalData))
		{
			int cueId = _focusedCue?.Id ?? _globalData?.FocusedCue ?? -1;
			if (cueId >= 0 || InspectorMultiEditSupport.ShouldUseMultiEdit(_globalData))
			{
				ShellSelected(cueId >= 0 ? cueId : (_globalData?.FocusedCue ?? -1));
				return;
			}
		}

		if (_focusedCue == null) return;
		var cue = CueList.FetchCueFromId(_focusedCue.Id);
		if (cue == null)
		{
			_focusedCue = null;
			_focusedVideoComponent = null;
			return;
		}
		_focusedCue = cue;
		_focusedVideoComponent = cue.GetVideoComponent();
		if (_focusedVideoComponent == null)
		{
			_infoLabel.Text = UiLocalizer.T("No Video File");
			_selectFileContainer.Visible = true;
			_inspectorContent.Visible = false;
			_previewContainer.Visible = false;
			_fileUrl.Text = "";
			ClearFileMetadataLabel();
			if (_deleteVideoComponentButton != null)
				_deleteVideoComponentButton.Visible = false;
			return;
		}

		_focusedVideoComponent.RefreshIsImageFromPath();
		UpdateVideoUiFields(_focusedVideoComponent.VideoFile ?? string.Empty);

		// Target layer / output assignment may have changed externally (delete→unassign/replace, undo).
		PopulateTargetLayerOptions();
		if (_videoPreviewer != null && _focusedVideoComponent.TargetLayerId >= 0)
			_videoPreviewer.SetAreasDeferred(_focusedVideoComponent.TargetLayerId);

		// Invalidate waveform cache; history omits peak payloads and component instance is new.
		_cachedPeaks = null;
		_cachedPeaksSource = null;
		_isDraggingStart = false;
		_isDraggingEnd = false;

		// Rebuild output + routing matrix; RefreshAudioUiState also regenerates waveform peaks when missing.
		await RefreshAudioUiState();
		await DrawWaveform();
	}
	
	private void SyncDuration()
	{
		var targets = GetVideoTargets();
		if (targets.Count == 0) return;

		foreach (var (cue, comp) in targets)
		{
			comp.RecalculateDuration();
			cue.CalculateTotalDuration();
			_globalSignals.EmitSignal(nameof(GlobalSignals.UpdateShellBar), cue.Id);
		}

		if (_focusedVideoComponent != null)
		{
			if (_focusedVideoComponent.IsImage)
			{
				if (_focusedVideoComponent.Duration <= 0)
				{
					_durationValue.Text = "0 (until stopped)";
					_durationValue.TooltipText = UiLocalizer.T("0 = stay active until stopped. Enter a time to auto-end.");
				}
				else
				{
					_durationValue.Text = UiUtilities.ParseAndFormatTime(
						_focusedVideoComponent.Duration.ToString(), out var _, out string imgDurTip);
					_durationValue.TooltipText = imgDurTip + " (0 = until stopped)";
				}
			}
			else
			{
				_durationValue.Text =
					UiUtilities.ParseAndFormatTime(
						_focusedVideoComponent.Duration.ToString(), out var _, out string durLabeledTime);
				_durationValue.TooltipText = durLabeledTime;
			}
		}

		_globalSignals.EmitSignal(nameof(GlobalSignals.SyncShellInspector));
	}
	
	/// <summary>
	/// Handles play count submission with validation to prevent invalid integers.
	/// </summary>
	/// <param name="newText">The submitted text.</param>
	private void OnPlayCountSubmitted(string newText)
	{
		var targets = GetVideoTargets();
		if (targets.Count == 0) return;
		if (_isSyncingUi || _globalData?.HistoryManager?.IsRestoring == true) return;
		if (int.TryParse(newText, out var playCount) && playCount > 0)
		{
			if (targets.All(t => t.Component.PlayCount == playCount))
			{
				if (_playCountInput.HasFocus()) _playCountInput.ReleaseFocus();
				return;
			}
			RecordVideoHistory("Edit video play count");
			foreach (var (_, comp) in targets)
				comp.PlayCount = playCount;
			SyncDuration();
		}
		else
		{
			_globalSignals.EmitSignal(nameof(GlobalSignals.Log), $"Invalid play count: {newText}. Must be positive integer.", 1);
			if (_focusedVideoComponent != null)
				_playCountInput.Text = _focusedVideoComponent.PlayCount.ToString();
		}
		if (_playCountInput.HasFocus())
			_playCountInput.ReleaseFocus();
	}

	/// <summary>
	/// Commits fade-in or fade-out duration from a time LineEdit.
	/// </summary>
	/// <param name="text">User-entered time string.</param>
	/// <param name="isIn">True for fade-in; false for fade-out.</param>
	private void OnFadeSubmitted(string text, bool isIn)
	{
		var targets = GetVideoTargets();
		if (targets.Count == 0) return;
		if (_isSyncingUi || _globalData?.HistoryManager?.IsRestoring == true) return;

		var field = isIn ? _fadeInInput : _fadeOutInput;
		if (field == null) return;

		var formatted = UiUtilities.ParseAndFormatTime(text, out var seconds, out string labeled);
		if (string.IsNullOrEmpty(formatted))
		{
			_globalSignals.EmitSignal(nameof(GlobalSignals.Log),
				$"Invalid video fade time: {text}", 1);
			if (_focusedVideoComponent != null)
			{
				double current = isIn
					? _focusedVideoComponent.FadeInDuration
					: _focusedVideoComponent.FadeOutDuration;
				field.Text = UiUtilities.FormatTime(current);
			}
			if (field.HasFocus()) field.ReleaseFocus();
			return;
		}

		seconds = Math.Max(0.0, seconds);
		field.Text = formatted;
		field.TooltipText = labeled + (isIn
			? " (fade-in at play start)"
			: " (fade-out on stop)");

		bool anyChange = targets.Any(t =>
		{
			double existing = isIn ? t.Component.FadeInDuration : t.Component.FadeOutDuration;
			return !Mathf.IsEqualApprox((float)existing, (float)seconds);
		});
		if (!anyChange)
		{
			if (field.HasFocus()) field.ReleaseFocus();
			return;
		}

		RecordVideoHistory(isIn ? "Edit video fade-in" : "Edit video fade-out");
		foreach (var (_, comp) in targets)
		{
			if (isIn)
				comp.FadeInDuration = seconds;
			else
				comp.FadeOutDuration = seconds;
		}

		if (field.HasFocus()) field.ReleaseFocus();
	}

	/// <summary>
	/// Handles scaled width submission with validation.
	/// </summary>
	/// <param name="newText">The submitted text.</param>
	private void OnScaleWidthSubmitted(string newText)
	{
		var targets = GetVideoTargets();
		if (targets.Count == 0) return;
		if (_isSyncingUi || _globalData?.HistoryManager?.IsRestoring == true) return;
		if (int.TryParse(newText, out var width) && width > 0)
		{
			if (targets.All(t => t.Component.ScaledWidth == width))
			{
				if (_scaleWidthLineEdit.HasFocus()) _scaleWidthLineEdit.ReleaseFocus();
				return;
			}
			RecordVideoHistory("Edit video scale width");
			foreach (var (_, comp) in targets)
				comp.ScaledWidth = width;
		}
		else
		{
			_globalSignals.EmitSignal(nameof(GlobalSignals.Log), $"Invalid scaled width: {newText}. Must be positive integer.", 1);
			if (_focusedVideoComponent != null)
				_scaleWidthLineEdit.Text = _focusedVideoComponent.ScaledWidth.ToString();
		}
		if (_scaleWidthLineEdit.HasFocus())
			_scaleWidthLineEdit.ReleaseFocus();
	}

	/// <summary>
	/// Handles scaled height submission with validation.
	/// </summary>
	/// <param name="newText">The submitted text.</param>
	private void OnScaleHeightSubmitted(string newText)
	{
		var targets = GetVideoTargets();
		if (targets.Count == 0) return;
		if (_isSyncingUi || _globalData?.HistoryManager?.IsRestoring == true) return;
		if (int.TryParse(newText, out var height) && height > 0)
		{
			if (targets.All(t => t.Component.ScaledHeight == height))
			{
				if (_scaleHeightLineEdit.HasFocus()) _scaleHeightLineEdit.ReleaseFocus();
				return;
			}
			RecordVideoHistory("Edit video scale height");
			foreach (var (_, comp) in targets)
				comp.ScaledHeight = height;
		}
		else
		{
			_globalSignals.EmitSignal(nameof(GlobalSignals.Log), $"Invalid scaled height: {newText}. Must be positive integer.", 1);
			if (_focusedVideoComponent != null)
				_scaleHeightLineEdit.Text = _focusedVideoComponent.ScaledHeight.ToString();
		}
		if (_scaleHeightLineEdit.HasFocus())
			_scaleHeightLineEdit.ReleaseFocus();
	}

	/// <summary>
	/// Handles offset X submission with validation.
	/// </summary>
	/// <param name="newText">The submitted text.</param>
	private void OnOffsetXSubmitted(string newText)
	{
		var targets = GetVideoTargets();
		if (targets.Count == 0) return;
		if (_isSyncingUi || _globalData?.HistoryManager?.IsRestoring == true) return;
		if (int.TryParse(newText, out var offsetX))
		{
			if (targets.All(t => t.Component.OffsetX == offsetX))
			{
				if (_offsetXLineEdit.HasFocus()) _offsetXLineEdit.ReleaseFocus();
				return;
			}
			RecordVideoHistory("Edit video offset X");
			foreach (var (_, comp) in targets)
				comp.OffsetX = offsetX;
		}
		else
		{
			_globalSignals.EmitSignal(nameof(GlobalSignals.Log), $"Invalid offset X: {newText}. Must be integer.", 1);
			if (_focusedVideoComponent != null)
				_offsetXLineEdit.Text = _focusedVideoComponent.OffsetX.ToString();
		}
		if (_offsetXLineEdit.HasFocus())
			_offsetXLineEdit.ReleaseFocus();
	}

	/// <summary>
	/// Handles offset Y submission with validation.
	/// </summary>
	/// <param name="newText">The submitted text.</param>
	private void OnOffsetYSubmitted(string newText)
	{
		var targets = GetVideoTargets();
		if (targets.Count == 0) return;
		if (_isSyncingUi || _globalData?.HistoryManager?.IsRestoring == true) return;
		if (int.TryParse(newText, out var offsetY))
		{
			if (targets.All(t => t.Component.OffsetY == offsetY))
			{
				if (_offsetYLineEdit.HasFocus()) _offsetYLineEdit.ReleaseFocus();
				return;
			}
			RecordVideoHistory("Edit video offset Y");
			foreach (var (_, comp) in targets)
				comp.OffsetY = offsetY;
		}
		else
		{
			_globalSignals.EmitSignal(nameof(GlobalSignals.Log), $"Invalid offset Y: {newText}. Must be integer.", 1);
			if (_focusedVideoComponent != null)
				_offsetYLineEdit.Text = _focusedVideoComponent.OffsetY.ToString();
		}
		if (_offsetYLineEdit.HasFocus())
			_offsetYLineEdit.ReleaseFocus();
	}

	/// <summary>
	/// Handles toggling of the use audio checkbox. Expands audio accordion when enabled.
	/// </summary>
	/// <param name="state">The toggle state.</param>
	private void OnUseAudioToggled(bool state)
	{
		var targets = GetVideoTargets();
		if (targets.Count == 0) return;
		if (_isSyncingUi || _globalData?.HistoryManager?.IsRestoring == true) return;
		if (targets.All(t => t.Component.UseAudio == state)) return;
		RecordVideoHistory("Edit video use-audio");
		foreach (var (cue, comp) in targets)
		{
			comp.UseAudio = state;
			GetNodeOrNull<MediaHealthService>("/root/MediaHealthService")?.CheckCue(cue.Id);
		}
		_ = RefreshAudioUiState();
	}

	/// <summary>
	/// Handles volume input submission with validation and conversion.
	/// </summary>
	/// <param name="text">The submitted text.</param>
	/// <param name="textField">The LineEdit field.</param>
	private void VolumeInputSubmitted(string text, LineEdit textField)
	{
		var targets = GetVideoTargets();
		if (targets.Count == 0 || textField == null) return;
		if (_isSyncingUi || _globalData?.HistoryManager?.IsRestoring == true) return;
		try
		{
			if (!float.TryParse(text.Replace("dB", "").Trim(), out var dbValue))
			{
				_globalSignals.EmitSignal(nameof(GlobalSignals.Log), $"Invalid volume format: {text}", 1);
				float fallback = _focusedVideoComponent != null
					? (_focusedVideoComponent.UseAudio
						? _focusedVideoComponent.AudioVolume
						: (float)_focusedVideoComponent.Volume)
					: 1f;
				textField.Text = $"{UiUtilities.LinearToDb(fallback)}dB";
				if (textField.HasFocus()) textField.ReleaseFocus();
				return;
			}
			// Digital gain allowed (−60…+12 dB). Do not treat positive as attenuation.
			dbValue = Mathf.Clamp(dbValue, UiUtilities.MinVolumeDb, UiUtilities.MaxComponentGainDb);
			float volume = UiUtilities.DbToLinear(dbValue);
			textField.Text = UiUtilities.FormatComponentVolumeDb(volume);

			bool unchanged = targets.All(t =>
			{
				float current = t.Component.UseAudio ? t.Component.AudioVolume : (float)t.Component.Volume;
				return Math.Abs(current - volume) < 1e-6f;
			});
			if (unchanged)
			{
				if (textField.HasFocus()) textField.ReleaseFocus();
				return;
			}

			RecordVideoHistory("Edit video volume");
			foreach (var (_, comp) in targets)
			{
				if (comp.UseAudio)
					comp.AudioVolume = volume;
				else
					comp.Volume = volume;
			}
			if (textField.HasFocus()) textField.ReleaseFocus();
		}
		catch (Exception ex)
		{
			GD.Print($"VideoInspector:VolumeInputSubmitted - Error: {ex.Message}");
			_globalSignals.EmitSignal(nameof(GlobalSignals.Log), $"Error parsing volume: {ex.Message}", 2);
		}
	}

	/// <summary>
	/// Handles output option selection for audio routing.
	/// </summary>
	/// <param name="index">The selected index.</param>
	private void OutputOptionSelected(long index)
	{
		var targets = GetVideoTargets();
		if (targets.Count == 0 || _focusedVideoComponent == null) return;
		if (_isSyncingUi || _globalData?.HistoryManager?.IsRestoring == true) return;

		var item = _outputOptionButton.GetItemText((int)index);

		// Resolve intended new routing without writing yet (so we can skip no-ops).
		int newPatchId = -1;
		string newDirect = null;
		AudioOutputPatch newPatch = null;

		if (item.StartsWith("Patch"))
		{
			newPatchId = (int)_outputOptionButton.GetItemMetadata((int)index);
			if (_globalData.Settings.GetAudioOutputPatches().TryGetValue(newPatchId, out var patch))
			{
				newPatch = patch;
				newDirect = null;
			}
			else
			{
				newPatchId = -1;
				newPatch = null;
				newDirect = null;
			}
		}
		else if (item.StartsWith("Direct Output"))
		{
			newDirect = item.Replace("Direct Output: ", "");
			newPatchId = -1;
			newPatch = null;
		}
		else if (item.StartsWith("!!! Missing"))
		{
			// Keep current assignment when user re-selects a missing entry.
			return;
		}
		else
		{
			// "No output"
			newPatchId = -1;
			newPatch = null;
			newDirect = null;
		}

		// No-op when every target already has this routing.
		bool unchanged = targets.All(t =>
			newPatchId == t.Component.PatchId
			&& string.Equals(
				newDirect ?? string.Empty,
				t.Component.DirectOutput ?? string.Empty,
				StringComparison.Ordinal));
		if (unchanged)
			return;

		RecordVideoHistory("Edit video audio output");

		void ApplyRouting(VideoComponent comp)
		{
			if (item.StartsWith("Patch"))
			{
				if (newPatch != null)
				{
					comp.Patch = newPatch;
					comp.PatchId = newPatchId;
					comp.DirectOutput = null;
				}
				else
				{
					comp.Patch = null;
					comp.PatchId = -1;
					comp.DirectOutput = null;
				}
			}
			else if (item.StartsWith("Direct Output"))
			{
				comp.DirectOutput = newDirect;
				comp.Patch = null;
				comp.PatchId = -1;
			}
			else
			{
				comp.DirectOutput = null;
				comp.Patch = null;
				comp.PatchId = -1;
			}
		}

		foreach (var (cue, comp) in targets)
		{
			ApplyRouting(comp);
			GetNodeOrNull<MediaHealthService>("/root/MediaHealthService")?.CheckCue(cue.Id);
		}

		if (item.StartsWith("Patch"))
		{
			if (newPatch == null)
			{
				_globalSignals.EmitSignal(nameof(GlobalSignals.Log),
					$"VideoInspector:OutputOptionSelected - Patch ID {newPatchId} not found, resetting output", 1);
				_outputOptionButton.SetBlockSignals(true);
				_outputOptionButton.Select(0);
				_outputOptionButton.SetBlockSignals(false);
			}
			BuildRoutingMatrix();
		}
		else if (item.StartsWith("Direct Output"))
		{
			BuildRoutingMatrix();
		}
		else if (_routingContainer != null)
		{
			// "No output" — hide matrix for single-target; multi keeps primary matrix if still routed.
			_routingContainer.Visible = false;
		}
	}
}
