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
/// Partial: Waveform draw/zoom/handles, accordion, preview toggle, file dialog cleanup
/// </summary>
public partial class VideoInspector
{

	/// <summary>
	/// Updates the waveform display from peak data and start/end selection.
	/// </summary>
	/// <summary>
	/// Cancels any prior waveform wait without starting a new one (clear focus).
	/// </summary>
	private void CancelWaveformWork()
	{
		try { _waveformCts?.Cancel(); } catch { /* ignore */ }
		try { _waveformCts?.Dispose(); } catch { /* ignore */ }
		_waveformCts = null;
	}

	/// <summary>
	/// Cancels any prior waveform job and returns a fresh token for the next generate.
	/// </summary>
	private CancellationToken RestartWaveformToken()
	{
		try { _waveformCts?.Cancel(); } catch { /* ignore */ }
		try { _waveformCts?.Dispose(); } catch { /* ignore */ }
		_waveformCts = new CancellationTokenSource();
		return _waveformCts.Token;
	}

	private async Task DrawWaveform()
	{
		if (_waveformAccordian == null || _waveformAccordian.Visible == false) return;
		if (_focusedVideoComponent == null || !_focusedVideoComponent.UseAudio ||
		    _focusedVideoComponent.WaveformData == null ||
		    _focusedVideoComponent.WaveformData.Length == 0)
		{
			_globalSignals.EmitSignal(nameof(GlobalSignals.Log),
				"VideoInspector:DrawWaveform - No waveform data available or audio not enabled", 1);
			return;
		}

		await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

		// Guard: component may have been rebound during the await (undo/redo).
		if (_focusedVideoComponent == null || !_focusedVideoComponent.UseAudio ||
		    _focusedVideoComponent.WaveformData == null ||
		    _focusedVideoComponent.WaveformData.Length == 0)
			return;

		float width = _waveformPanel.Size.X;
		if (width < 50)
			width = Math.Max(0, _inspectorContent.Size.X - 48);
		if (width < 50)
		{
			_globalSignals.EmitSignal(nameof(GlobalSignals.Log),
				"VideoInspector:DrawWaveform - Waveform panel too small to draw", 1);
			return;
		}

		if (_cachedPeaks == null || !ReferenceEquals(_cachedPeaksSource, _focusedVideoComponent.WaveformData))
		{
			_cachedPeaks = WaveformPeaks.FromBytes(_focusedVideoComponent.WaveformData);
			_cachedPeaksSource = _focusedVideoComponent.WaveformData;
		}
		if (_cachedPeaks == null)
		{
			_globalSignals.EmitSignal(nameof(GlobalSignals.Log),
				"VideoInspector:DrawWaveform - Invalid waveform payload", 1);
			return;
		}

		double duration = _focusedVideoComponent.Metadata?.Duration ?? 0;
		if (duration <= 0) duration = 1;
		float startNorm = (float)(_focusedVideoComponent.StartTime / duration);
		float endTime = _focusedVideoComponent.EndTime < 0
			? (float)duration
			: (float)_focusedVideoComponent.EndTime;
		float endNorm = (float)(endTime / duration);

		_viewSpanNorm = Mathf.Clamp(_viewSpanNorm, 0.01f, 1f);
		_viewStartNorm = Mathf.Clamp(_viewStartNorm, 0f, 1f - _viewSpanNorm);

		_waveformDisplay.SetData(_cachedPeaks, startNorm, endNorm, _viewStartNorm, _viewSpanNorm, duration);

		PositionWaveformHandle(_startDragHandle, startNorm, width);
		PositionWaveformHandle(_endDragHandle, endNorm, width);
		SyncWaveformScrollBar();
	}

	private void PositionWaveformHandle(Button handle, float fileNorm, float width)
	{
		float x = _waveformDisplay.FileNormToX(fileNorm);
		bool visible = x >= -4 && x <= width + 4;
		handle.Visible = visible;
		if (!visible) return;
		float handleW = handle.CustomMinimumSize.X > 0 ? handle.CustomMinimumSize.X : 10f;
		handle.Position = new Vector2(x - handleW * 0.5f, 0);
		handle.Size = new Vector2(handleW, _waveformPanel.Size.Y);
	}

	private void OnStartHandleInput(InputEvent @event)
	{
		if (@event is InputEventMouseButton mouseButton && mouseButton.ButtonIndex == MouseButton.Left)
		{
			if (mouseButton.Pressed)
			{
				// Continuous drag session: one undo step for the whole drag (all multi targets).
				RecordVideoHistory("Edit video start time", VideoCoalesceKey("start-drag"));
				_isDraggingStart = true;
			}
			else if (_isDraggingStart)
			{
				SyncDuration();
				_isDraggingStart = false;
				var key = VideoCoalesceKey("start-drag");
				if (!string.IsNullOrEmpty(key))
					InspectorMultiEditSupport.EndCoalesce(_globalData, UseMultiHistory(), key, key);
			}
		}
		else if (@event is InputEventMouseMotion && _isDraggingStart)
		{
			if (_focusedVideoComponent == null) return;
			float localX = _waveformPanel.GetLocalMousePosition().X;
			float norm = _waveformDisplay.XToFileNorm(localX);
			double duration = _focusedVideoComponent.Metadata?.Duration ?? 0;
			if (duration <= 0) return;
			// Keep start before end (primary waveform geometry).
			float endN = _focusedVideoComponent.EndTime < 0
				? 1f
				: (float)(_focusedVideoComponent.EndTime / duration);
			norm = Mathf.Min(norm, endN - 0.001f);
			norm = Mathf.Max(0f, norm);
			double startSecs = norm * duration;
			foreach (var (_, comp) in GetVideoTargets())
			{
				if (comp.IsImage) continue;
				double d = comp.Metadata?.Duration ?? duration;
				if (d <= 0) d = duration;
				float localEndN = comp.EndTime < 0 ? 1f : (float)(comp.EndTime / d);
				float localNorm = Mathf.Min(norm, localEndN - 0.001f);
				localNorm = Mathf.Max(0f, localNorm);
				comp.StartTime = comp.ClampStartTime(localNorm * d);
			}
			_startTimeInput.Text = UiUtilities.FormatTime(
				_focusedVideoComponent != null ? _focusedVideoComponent.StartTime : startSecs);
			_ = DrawWaveform();
		}
	}

	private void OnEndHandleInput(InputEvent @event)
	{
		if (@event is InputEventMouseButton mouseButton && mouseButton.ButtonIndex == MouseButton.Left)
		{
			if (mouseButton.Pressed)
			{
				RecordVideoHistory("Edit video end time", VideoCoalesceKey("end-drag"));
				_isDraggingEnd = true;
			}
			else if (_isDraggingEnd)
			{
				SyncDuration();
				_isDraggingEnd = false;
				var key = VideoCoalesceKey("end-drag");
				if (!string.IsNullOrEmpty(key))
					InspectorMultiEditSupport.EndCoalesce(_globalData, UseMultiHistory(), key, key);
			}
		}
		else if (@event is InputEventMouseMotion && _isDraggingEnd)
		{
			if (_focusedVideoComponent == null) return;
			float localX = _waveformPanel.GetLocalMousePosition().X;
			float norm = _waveformDisplay.XToFileNorm(localX);
			double duration = _focusedVideoComponent.Metadata?.Duration ?? 0;
			if (duration <= 0) return;
			float startN = (float)(_focusedVideoComponent.StartTime / duration);
			norm = Mathf.Max(norm, startN + 0.001f);
			norm = Mathf.Min(1f, norm);
			double endSecs = norm * duration;
			foreach (var (_, comp) in GetVideoTargets())
			{
				if (comp.IsImage) continue;
				double d = comp.Metadata?.Duration ?? duration;
				if (d <= 0) d = duration;
				float localStartN = (float)(comp.StartTime / d);
				float localNorm = Mathf.Max(norm, localStartN + 0.001f);
				localNorm = Mathf.Min(1f, localNorm);
				comp.EndTime = localNorm * d;
			}
			_endTimeInput.Text = UiUtilities.FormatTime(endSecs);
			_ = DrawWaveform();
		}
	}

	/// <summary>
	/// Toggles the visibility of an accordion container and updates the button icon.
	/// </summary>
	/// <param name="accordian">The container to toggle.</param>
	/// <param name="button">The button controlling the accordion.</param>
	private void ToggleAccordian(Control accordian, Button button)
	{
		TaskUtil.Run(() => ToggleAccordianAsync(accordian, button), "VideoInspector.ToggleAccordian");
	}

	private async Task ToggleAccordianAsync(Control accordian, Button button)
	{
		accordian.Visible = !accordian.Visible;
		button.Icon = GetThemeIcon(accordian.Visible ? "Down" : "Right", "AtlasIcons");

		if (accordian.Name == "WaveformAccordian")
		{
			await DrawWaveform();
		}
	}
	
	private void PreviewToggled()
	{
		if (_previewContainer.Visible)
		{
			_videoPreviewer.SetAreasDeferred(_focusedVideoComponent.TargetLayerId);
			_videoPreviewer.LoadDecoder(_focusedVideoComponent.VideoFile);
		}
		else
		{
			// Fluch video decoder if preview not opened
			_videoPreviewer.ClearDecoder();
		}
	}


	/// <summary>
	/// Clears the file dialog instance (safe if already null or freed).
	/// </summary>
	private void ClearFileDialog()
	{
		if (_fileDialog == null)
			return;

		try
		{
			if (IsInstanceValid(_fileDialog))
			{
				_fileDialog.FileSelected -= FileSelected;
				_fileDialog.Canceled -= ClearFileDialog;
				_fileDialog.QueueFree();
			}
		}
		catch
		{
			/* best-effort during exit */
		}

		_fileDialog = null;
	}
}
