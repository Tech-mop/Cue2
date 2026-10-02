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
		if (_waveformZoom != null)
			_waveformZoom.Viewport.DurationSeconds = duration;

		RedrawWaveformView();
	}

	private void RedrawWaveformView()
	{
		if (_waveformDisplay == null || _waveformZoom == null) return;
		if (_focusedVideoComponent == null || !_focusedVideoComponent.UseAudio) return;
		if (_focusedVideoComponent.WaveformData == null || _focusedVideoComponent.WaveformData.Length == 0)
			return;
		if (_cachedPeaks == null) return;

		double duration = _focusedVideoComponent.Metadata?.Duration ?? 0;
		if (duration <= 0) duration = 1;
		_waveformZoom.Viewport.DurationSeconds = duration;
		float startNorm = (float)(_focusedVideoComponent.StartTime / duration);
		float endTime = _focusedVideoComponent.EndTime < 0
			? (float)duration
			: (float)_focusedVideoComponent.EndTime;
		float endNorm = (float)(endTime / duration);
		_waveformZoom.SetSelection(startNorm, endNorm);

		_waveformDisplay.SetData(
			_cachedPeaks, startNorm, endNorm,
			_waveformZoom.Viewport.ViewStartNorm,
			_waveformZoom.Viewport.ViewSpanNorm,
			duration,
			_focusedVideoComponent.FadeInDuration,
			_focusedVideoComponent.FadeOutDuration,
			_focusedVideoComponent.FadeInCurve,
			_focusedVideoComponent.FadeOutCurve);

		float w = _waveformPanel != null ? _waveformPanel.Size.X : 0f;
		float h = _waveformPanel != null ? _waveformPanel.Size.Y : 0f;
		_waveformDisplay.PlaceHandle(_startDragHandle, startNorm, isStart: true, w, h);
		_waveformDisplay.PlaceHandle(_endDragHandle, endNorm, isStart: false, w, h);
		_waveformDisplay.PlaceFadeHandle(_fadeInHandle, _waveformDisplay.FadeInEndNorm, isFadeIn: true, w, h);
		_waveformDisplay.PlaceFadeHandle(_fadeOutHandle, _waveformDisplay.FadeOutStartNorm, isFadeIn: false, w, h);
	}

	private void UpdateWaveformPlayhead()
	{
		if (_waveformZoom == null || _waveformDisplay == null) return;
		if (_waveformAccordian == null || !_waveformAccordian.Visible)
		{
			_waveformZoom.SetPlayheadNorm(-1f);
			return;
		}

		double duration = _focusedVideoComponent?.Metadata?.Duration ?? 0;
		if (duration <= 0 || _focusedCue == null || _focusedVideoComponent == null || !_focusedVideoComponent.UseAudio)
		{
			_waveformZoom.SetPlayheadNorm(-1f);
			return;
		}

		var exec = _globalData?.CueCommandExecutor;
		if (exec == null)
		{
			_waveformZoom.SetPlayheadNorm(-1f);
			return;
		}

		foreach (var root in exec.ActiveCues)
		{
			if (root == null || !IsInstanceValid(root)) continue;
			try
			{
				foreach (var active in root.EnumerateSelfAndDescendants())
				{
					if (active == null || !IsInstanceValid(active)) continue;
					if (active.Cue == null || active.Cue.Id != _focusedCue.Id) continue;
					foreach (var playback in active.EnumerateVideoPlaybacks())
					{
						double sec = playback.GetPlaybackTimeSeconds();
						_waveformZoom.SetPlayheadNorm((float)Math.Clamp(sec / duration, 0, 1));
						return;
					}
				}
			}
			catch
			{
				// Playback may be mid-teardown
			}
		}

		_waveformZoom.SetPlayheadNorm(-1f);
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
				_startDragHandle?.AcceptEvent();
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
			RedrawWaveformView();
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
				_endDragHandle?.AcceptEvent();
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
			RedrawWaveformView();
		}
	}

	private void OnFadeInHandleInput(InputEvent @event)
	{
		if (@event is InputEventMouseButton mouseButton && mouseButton.ButtonIndex == MouseButton.Left)
		{
			if (mouseButton.Pressed)
			{
				RecordVideoHistory("Edit video fade-in", VideoCoalesceKey("fade-in-drag"));
				_isDraggingFadeIn = true;
				_fadeInHandle?.AcceptEvent();
			}
			else if (_isDraggingFadeIn)
			{
				_isDraggingFadeIn = false;
				var key = VideoCoalesceKey("fade-in-drag");
				if (!string.IsNullOrEmpty(key))
					InspectorMultiEditSupport.EndCoalesce(_globalData, UseMultiHistory(), key, key);
			}
		}
		else if (@event is InputEventMouseMotion && _isDraggingFadeIn)
		{
			ApplyVideoFadeFromWaveform(isIn: true);
		}
	}

	private void OnFadeOutHandleInput(InputEvent @event)
	{
		if (@event is InputEventMouseButton mouseButton && mouseButton.ButtonIndex == MouseButton.Left)
		{
			if (mouseButton.Pressed)
			{
				RecordVideoHistory("Edit video fade-out", VideoCoalesceKey("fade-out-drag"));
				_isDraggingFadeOut = true;
				_fadeOutHandle?.AcceptEvent();
			}
			else if (_isDraggingFadeOut)
			{
				_isDraggingFadeOut = false;
				var key = VideoCoalesceKey("fade-out-drag");
				if (!string.IsNullOrEmpty(key))
					InspectorMultiEditSupport.EndCoalesce(_globalData, UseMultiHistory(), key, key);
			}
		}
		else if (@event is InputEventMouseMotion && _isDraggingFadeOut)
		{
			ApplyVideoFadeFromWaveform(isIn: false);
		}
	}

	private void ApplyVideoFadeFromWaveform(bool isIn)
	{
		if (_focusedVideoComponent == null) return;
		float localX = _waveformPanel.GetLocalMousePosition().X;
		float norm = _waveformDisplay.XToFileNorm(localX);
		double duration = _focusedVideoComponent.Metadata?.Duration ?? 0;
		if (duration <= 0) return;

		double start = _focusedVideoComponent.StartTime;
		double end = _focusedVideoComponent.EndTime < 0 ? duration : _focusedVideoComponent.EndTime;
		double sel = Math.Max(0, end - start);
		double fade = isIn
			? Math.Clamp(norm * duration - start, 0, sel)
			: Math.Clamp(end - norm * duration, 0, sel);

		foreach (var (_, comp) in GetVideoTargets())
		{
			if (comp.IsImage) continue;
			double d = comp.Metadata?.Duration ?? duration;
			if (d <= 0) d = duration;
			double s = comp.StartTime;
			double e = comp.EndTime < 0 ? d : comp.EndTime;
			double localSel = Math.Max(0, e - s);
			double localFade = Math.Clamp(fade, 0, localSel);
			if (isIn)
				comp.FadeInDuration = localFade;
			else
				comp.FadeOutDuration = localFade;
		}

		var field = isIn ? _fadeInInput : _fadeOutInput;
		if (field != null)
			field.Text = UiUtilities.FormatTime(fade);
		RedrawWaveformView();
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
