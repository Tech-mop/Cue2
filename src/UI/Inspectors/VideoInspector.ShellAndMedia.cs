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
/// Partial: Media path/health, multi-edit history helpers, shell select, file load, field sync entry
/// </summary>
public partial class VideoInspector
{
	private void RefreshMediaPathDisplay()
	{
		if (_fileUrl == null || _focusedVideoComponent == null)
			return;

		string path = _focusedVideoComponent.VideoFile ?? string.Empty;
		if (!string.Equals(_fileUrl.Text, path, StringComparison.Ordinal))
			_fileUrl.Text = path;

		GetNodeOrNull<MediaHealthService>("/root/MediaHealthService")?.CheckCue(_focusedCue?.Id ?? -1);
		ApplyFileUrlMissingStyleFromHealth();
	}

	private void OnCueMediaHealthChanged(int cueId, bool hasIssue, string message)
	{
		if (_focusedCue == null || _focusedCue.Id != cueId)
			return;
		// Only style this inspector's URL if *video* is among the missing paths
		ApplyFileUrlMissingStyleFromHealth();
	}

	/// <summary>
	/// Styles the video URL field only when this cue's video path is reported missing
	/// (not when only audio/other media is missing).
	/// </summary>
	private void ApplyFileUrlMissingStyleFromHealth()
	{
		if (_focusedCue == null || _focusedVideoComponent == null ||
		    string.IsNullOrWhiteSpace(_focusedVideoComponent.VideoFile))
		{
			ApplyFileUrlMissingStyle(false, null);
			return;
		}

		var health = GetNodeOrNull<MediaHealthService>("/root/MediaHealthService");
		bool missing = health != null && health.IsPathMissing(_focusedCue.Id, _focusedVideoComponent.VideoFile);
		ApplyFileUrlMissingStyle(missing, missing ? "File Missing" : null);
	}

	/// <summary>
	/// Applies or clears italic + red border styling on the URL field for missing media.
	/// </summary>
	private void ApplyFileUrlMissingStyle(bool missing, string tooltip)
	{
		_fileUrlMissingStyle ??= InspectorMediaUrlStyle.CreateMissingStyle();
		InspectorMediaUrlStyle.Apply(_fileUrl, _fileUrlMissingStyle, missing, tooltip);
	}

	/// <summary>
	/// Targets for the next edit: multi-edit subset, or the single focused video component.
	/// </summary>
	private List<(Cue Cue, VideoComponent Component)> GetVideoTargets()
	{
		if (_isMultiEdit)
			return _videoTargets ?? new List<(Cue, VideoComponent)>();
		if (_focusedCue != null && _focusedVideoComponent != null)
			return new List<(Cue, VideoComponent)> { (_focusedCue, _focusedVideoComponent) };
		return new List<(Cue, VideoComponent)>();
	}

	private bool UseMultiHistory() => GetVideoTargets().Count > 1;

	/// <summary>
	/// Records history before mutating video targets (cuelist when multi).
	/// </summary>
	private void RecordVideoHistory(string singleDescription, string coalesceKey = null)
	{
		var targets = GetVideoTargets();
		if (targets.Count == 0)
			return;
		InspectorMultiEditSupport.RecordBeforeEdit(
			_globalData,
			UseMultiHistory(),
			targets[^1].Cue,
			singleDescription,
			multiDescription: null,
			coalesceKey);
	}

	private string VideoCoalesceKey(string field) =>
		UseMultiHistory()
			? $"multi:video:{field}"
			: (_focusedCue != null ? $"cue:{_focusedCue.Id}:video:{field}" : null);

	/// <summary>
	/// Loads the focused cue (or multi-selection) into the video inspector.
	/// </summary>
	private void ShellSelected(int cueId)
	{
		TaskUtil.Run(() => ShellSelectedAsync(cueId), "VideoInspector.ShellSelected");
	}

	private async Task ShellSelectedAsync(int cueId)
	{
		int gen = ++_shellSelectGeneration;

		if (cueId < 0)
		{
			CancelWaveformWork();
			_focusedCue = null;
			_focusedVideoComponent = null;
			_isMultiEdit = false;
			_videoTargets.Clear();
			_fileUrl.Text = "";
			ApplyFileUrlMissingStyle(false, null);
			ClearFileMetadataLabel();
			try { _videoPreviewer?.ClearDecoder(); } catch { /* optional */ }
			ShowNoSelection();
			return;
		}

		// New focus — abandon prior waveform wait so jobs do not pile.
		var waveformCt = RestartWaveformToken();

		_isMultiEdit = InspectorMultiEditSupport.ShouldUseMultiEdit(_globalData);
		if (_isMultiEdit)
		{
			await LoadMultiEditVideo(gen, cueId);
			return;
		}

		_videoTargets.Clear();
		_focusedCue = CueList.FetchCueFromId(cueId);

		if (_focusedCue == null)
		{
			_focusedVideoComponent = null;
			ApplyFileUrlMissingStyle(false, null);
			ClearFileMetadataLabel();
			ShowNoSelection();
			return;
		}
		
		var hasVideo = UiUtilities.HasComponent<VideoComponent>(_focusedCue);
		if (!hasVideo) // No Video component in Cue
		{
			if (_infoLabel != null)
				_infoLabel.Visible = true;
			_infoLabel.Text = UiLocalizer.T("No Video File");
			_selectFileContainer.Visible = true;
			_inspectorContent.Visible = false;
			_focusedVideoComponent = null;
			_fileUrl.Text = "";
			ApplyFileUrlMissingStyle(false, null);
			ClearFileMetadataLabel();
			if (_deleteVideoComponentButton != null)
				_deleteVideoComponentButton.Visible = false;
			return;
		}
		
		// Video Component Found
		_focusedVideoComponent = _focusedCue.Components.OfType<VideoComponent>().First();
		// Keep IsImage in sync with path (covers older saves / renames).
		_focusedVideoComponent.RefreshIsImageFromPath();
		if (_deleteVideoComponentButton != null)
			_deleteVideoComponentButton.Visible = true;
		var file = _focusedVideoComponent.VideoFile;
		
		// Always re-probe when metadata is missing, or when subtitle tracks were never scanned
		// (older sessions saved Metadata without SubtitleTracks).
		bool needsMetaProbe = _focusedVideoComponent.Metadata == null
		                      || (!_focusedVideoComponent.IsImage
		                          && (_focusedVideoComponent.Metadata.SubtitleTracks == null
		                              || _focusedVideoComponent.Metadata.SubtitleTracks.Count == 0));
		if (needsMetaProbe && !string.IsNullOrWhiteSpace(file))
		{
			var refreshedMeta = await _mediaEngine.GetVideoFileMetadataAsync(file);
			if (gen != _shellSelectGeneration) return;
			if (_focusedVideoComponent == null) return;

			// Merge: keep existing duration/size if probe failed partially, but always take subtitles.
			if (refreshedMeta != null)
			{
				if (_focusedVideoComponent.Metadata == null)
				{
					_focusedVideoComponent.Metadata = refreshedMeta;
				}
				else
				{
					// Prefer fresh probe for subtitle discovery; keep other fields from probe too.
					_focusedVideoComponent.Metadata = refreshedMeta;
				}
			}

			if (_focusedVideoComponent.IsImage)
			{
				_focusedVideoComponent.HasAudio = false;
				_focusedVideoComponent.UseAudio = false;
			}
			else
			{
				_focusedVideoComponent.HasAudio = _focusedVideoComponent.Metadata?.AudioChannels > 0;
			}
			_focusedVideoComponent.RecalculateDuration();
			GD.Print(
				$"VideoInspector:ShellSelected - Refreshed metadata from file " +
				$"(subs={_focusedVideoComponent.Metadata?.SubtitleTracks?.Count ?? 0})");
		}

		if (gen != _shellSelectGeneration) return;

		UpdateVideoUiFields(file);

		PopulateTargetLayerOptions();

		// Initalize preview
		if (_previewContainer.Visible)
		{
			_videoPreviewer.SetAreasDeferred(_focusedVideoComponent.TargetLayerId);
			_videoPreviewer.LoadDecoder(file);
		}
		else
		{
			// Fluch video decoder if residual from previous shell selected remains
			_videoPreviewer.ClearDecoder();
		}

		await RefreshAudioUiState(waveformCt);
		if (gen != _shellSelectGeneration || _focusedCue == null) return;

		GetNodeOrNull<MediaHealthService>("/root/MediaHealthService")?.CheckCue(_focusedCue.Id);
		ApplyFileUrlMissingStyleFromHealth();
		if (_deleteVideoComponentButton != null)
			_deleteVideoComponentButton.Visible = _focusedVideoComponent != null;
		if (_infoLabel != null)
		{
			_infoLabel.Visible = false;
			_infoLabel.Text = "";
			_infoLabel.TooltipText = "";
		}
	}

	/// <summary>
	/// Shows the centred empty-state overlay when no cue shell is focused.
	/// </summary>
	private void ShowNoSelection()
	{
		if (_deleteVideoComponentButton != null)
			_deleteVideoComponentButton.Visible = false;
		if (_inspectorContent != null)
			_inspectorContent.Visible = false;
		if (_selectFileContainer != null)
			_selectFileContainer.Visible = false;
		if (_previewContainer != null)
			_previewContainer.Visible = false;
		if (_infoLabel != null)
		{
			_infoLabel.Visible = true;
			_infoLabel.Text = UiLocalizer.T("No shell selected");
			_infoLabel.TooltipText = "";
		}
	}

	/// <summary>
	/// Loads multi-edit video UI for the current selection.
	/// </summary>
	private async Task LoadMultiEditVideo(int gen, int focusedCueId)
	{
		_videoTargets = InspectorMultiEditSupport.CollectComponentTargets(c => c.GetVideoComponent());
		_focusedCue = CueList.FetchCueFromId(focusedCueId);
		_focusedVideoComponent = _focusedCue?.GetVideoComponent();
		if (_focusedVideoComponent == null && _videoTargets.Count > 0)
		{
			_focusedCue = _videoTargets[^1].Cue;
			_focusedVideoComponent = _videoTargets[^1].Component;
		}

		int selected = InspectorMultiEditSupport.GetSelectedCues().Count;
		if (_videoTargets.Count == 0)
		{
			_focusedVideoComponent = null;
			if (_infoLabel != null)
				_infoLabel.Visible = true;
			_infoLabel.Text = UiLocalizer.Tf("No video on {0} selected cue(s)", selected);
			_infoLabel.TooltipText = UiLocalizer.T("None of the selected cues have a video component. Choose a file to add video to all.");
			_selectFileContainer.Visible = true;
			_inspectorContent.Visible = false;
			_previewContainer.Visible = false;
			_fileUrl.Text = "";
			ApplyFileUrlMissingStyle(false, null);
			ClearFileMetadataLabel();
			if (_deleteVideoComponentButton != null)
				_deleteVideoComponentButton.Visible = false;
			try { _videoPreviewer?.ClearDecoder(); } catch { /* optional */ }
			return;
		}

		if (_infoLabel != null)
			_infoLabel.Visible = true;
		_infoLabel.Text = InspectorMultiEditSupport.FormatComponentMultiHeader("Video", _videoTargets.Count, selected);
		_infoLabel.TooltipText = InspectorMultiEditSupport.FormatComponentMultiTooltip(
			"video",
			_videoTargets.Select(t => (t.Cue, (object)t.Component)).ToList(),
			selected);

		_focusedVideoComponent?.RefreshIsImageFromPath();
		if (gen != _shellSelectGeneration) return;

		UpdateVideoUiFields(_focusedVideoComponent?.VideoFile ?? string.Empty);
		PopulateTargetLayerOptions();

		if (_previewContainer != null && _previewContainer.Visible && _focusedVideoComponent != null
		    && !string.IsNullOrEmpty(_focusedVideoComponent.VideoFile))
		{
			_videoPreviewer?.SetAreasDeferred(_focusedVideoComponent.TargetLayerId);
			_videoPreviewer?.LoadDecoder(_focusedVideoComponent.VideoFile);
		}
		else
		{
			try { _videoPreviewer?.ClearDecoder(); } catch { /* optional */ }
		}

		await RefreshAudioUiState();
		if (gen != _shellSelectGeneration) return;

		if (_focusedCue != null)
			GetNodeOrNull<MediaHealthService>("/root/MediaHealthService")?.CheckCue(_focusedCue.Id);
		ApplyFileUrlMissingStyleFromHealth();
		if (_deleteVideoComponentButton != null)
		{
			_deleteVideoComponentButton.Visible = true;
			_deleteVideoComponentButton.TooltipText =
				$"Remove video from {_videoTargets.Count} cue(s)";
		}
	}

	/// <summary>
	/// Removes the video component from edit targets (multi or focused).
	/// </summary>
	private void OnDeleteVideoComponentPressed()
	{
		var targets = GetVideoTargets();
		if (targets.Count == 0)
			return;

		RecordVideoHistory("Remove video component");
		foreach (var (cue, comp) in targets)
		{
			cue.RemoveICueComponent(comp);
			cue.CalculateTotalDuration();
			GetNodeOrNull<MediaHealthService>("/root/MediaHealthService")?.CheckCue(cue.Id);
			_globalSignals.EmitSignal(nameof(GlobalSignals.UpdateShellBar), cue.Id);
		}

		_focusedVideoComponent = null;
		_videoTargets.Clear();
		_fileUrl.Text = "";
		ApplyFileUrlMissingStyle(false, null);
		ClearFileMetadataLabel();
		if (_deleteVideoComponentButton != null)
			_deleteVideoComponentButton.Visible = false;

		try { _videoPreviewer?.ClearDecoder(); } catch { /* optional */ }

		_globalSignals.EmitSignal(nameof(GlobalSignals.SyncShellInspector));
		_globalSignals.EmitSignal(nameof(GlobalSignals.Log),
			$"Removed video component from {targets.Count} cue(s)", 0);

		if (_focusedCue != null)
			ShellSelected(_focusedCue.Id);
		else
		{
			_infoLabel.Text = UiLocalizer.T("No Video File");
			_inspectorContent.Visible = false;
			_previewContainer.Visible = false;
		}
	}

	/// <summary>
	/// Opens a file dialog for selecting an audio file.
	/// </summary>
	private void OpenFileDialog()
	{
		_fileDialog = new FileDialog();
		_fileDialog.FileSelected += FileSelected;
		_fileDialog.FileMode = FileDialog.FileModeEnum.OpenFile;
		_fileDialog.Access = FileDialog.AccessEnum.Filesystem;
		_fileDialog.Title = UiLocalizer.T("Select Video or Image File");
		_fileDialog.UseNativeDialog = true;

		// Add filters from GlobalData
		_fileDialog.AddFilter(string.Join(",", GlobalData.VideoFileFilters), "Video Files");
		_fileDialog.AddFilter(string.Join(",", GlobalData.ImageFileFilters), "Image Files");
		
		AddChild(_fileDialog);
		_fileDialog.PopupCentered();
		_fileDialog.Canceled += ClearFileDialog;
	}
	
	
	/// <summary>
	/// Handles file selection from dialog. Adds/replaces VideoComponent and loads metadata + waveform.
	/// </summary>
	/// <param name="path">The selected file path.</param>
	private void FileSelected(string path)
	{
		ClearFileDialog();
		if (_focusedCue == null)
		{
			GD.Print("VideoInspector:FileSelected - No cue selected");
			_globalSignals.EmitSignal(nameof(GlobalSignals.Log), "VideoInspector:No cue selected", 2);
			return;
		}
		SetVideoFile(path, resetInOutPoints: true);
	}
	
	/// <summary>
	/// Handles setting video file URL from drag-and-drop. Creates VideoComponent if none exists.
	/// </summary>
	/// <param name="filePath">The dropped file path.</param>
	public void SetVideoFileUrlFromDrop(string filePath)
	{
		bool multi = InspectorMultiEditSupport.ShouldUseMultiEdit(_globalData);
		if (!multi && _focusedCue == null)
		{
			GD.Print("VideoInspector:SetVideoFileUrlFromDrop - No cue selected");
			_globalSignals.EmitSignal(nameof(GlobalSignals.Log), "VideoInspector:No cue selected for video file drop", 2);
			return;
		}
		SetVideoFile(filePath, resetInOutPoints: false);
	}
	
	/// <summary>
	/// Sets the video/image file for the focused cue (or all multi-edit selected cues):
	/// create or replace component, load metadata, generate waveform, refresh UI.
	/// </summary>
	/// <param name="filePath">The video file path.</param>
	/// <param name="resetInOutPoints">If true, start/end are reset to full file; otherwise clamp to new duration.</param>
	private void SetVideoFile(string filePath, bool resetInOutPoints)
	{
		TaskUtil.Run(() => SetVideoFileAsync(filePath, resetInOutPoints), "VideoInspector.SetVideoFile");
	}

	private async Task SetVideoFileAsync(string filePath, bool resetInOutPoints)
	{
		bool multi = InspectorMultiEditSupport.ShouldUseMultiEdit(_globalData);
		var multiCues = multi ? InspectorMultiEditSupport.GetSelectedCues() : null;
		if (!multi && _focusedCue == null) return;
		if (multi && (multiCues == null || multiCues.Count == 0)) return;

		string resolvedPath = _globalData?.ResolveMediaPath(filePath) ?? filePath;
		if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(resolvedPath))
		{
			GD.Print($"VideoInspector:SetVideoFile - File not found: {filePath}");
			_globalSignals.EmitSignal(nameof(GlobalSignals.Log), $"VideoInspector:File not found: {filePath}", 2);
			return;
		}

		// Prefer show-relative path when media backup is enabled (copy runs in background)
		string pathToStore = filePath;
		try
		{
			var backup = GetNodeOrNull<MediaBackupManager>("/root/MediaBackupManager");
			var kind = MediaBackupManager.DetectKindFromPath(resolvedPath);
			if (kind != MediaBackupKind.Image)
				kind = MediaBackupKind.Video;
			string relative = backup?.EnsureMediaBackedUp(resolvedPath, kind);
			if (!string.IsNullOrEmpty(relative))
				pathToStore = relative;
		}
		catch (Exception ex)
		{
			GD.PrintErr($"VideoInspector:SetVideoFile - Media backup: {ex.Message}");
		}

		bool isImage = VideoComponent.IsImagePath(resolvedPath);
		VideoFileMetadata fileMetadata = null;
		try
		{
			fileMetadata = await _mediaEngine.GetVideoFileMetadataAsync(resolvedPath);
		}
		catch (Exception ex)
		{
			GD.PrintErr($"VideoInspector:SetVideoFile - Metadata error: {ex.Message}");
			_globalSignals.EmitSignal(nameof(GlobalSignals.Log),
				$"VideoInspector:SetVideoFile - Metadata error: {ex.Message}", 2);
			return;
		}
		if (fileMetadata == null)
		{
			_globalSignals.EmitSignal(nameof(GlobalSignals.Log),
				$"VideoInspector:SetVideoFile - Failed to read metadata for {Path.GetFileName(filePath)}", 2);
			return;
		}

		if (multi)
		{
			bool anyNew = multiCues.Any(c => c.GetVideoComponent() == null);
			string singleDesc = anyNew
				? (isImage ? "Add image component" : "Add video component")
				: (isImage ? "Change image file" : "Change video file");
			InspectorMultiEditSupport.RecordBeforeEdit(
				_globalData,
				multiCues.Count > 1,
				multiCues[^1],
				singleDesc,
				anyNew
					? (isImage ? "Multi-add image components" : "Multi-add video components")
					: (isImage ? "Multi-edit image file" : "Multi-edit video file"));

			byte[] sharedWave = null;
			if (!isImage && fileMetadata.AudioChannels > 0)
			{
				try
				{
					sharedWave = await _mediaEngine.GenerateWaveformAsync(pathToStore, RestartWaveformToken());
				}
				catch (OperationCanceledException)
				{
					return;
				}
				catch (Exception ex)
				{
					GD.PrintErr($"VideoInspector:SetVideoFile multi - Waveform: {ex.Message}");
				}
			}

			foreach (var cue in multiCues)
			{
				var existing = cue.GetVideoComponent();
				bool isNew = existing == null;
				VideoComponent comp = existing ?? cue.AddVideoComponent(pathToStore);
				ApplyVideoFileToComponent(comp, pathToStore, isImage, fileMetadata, isNew, resetInOutPoints, sharedWave);
				cue.CalculateTotalDuration();
				_globalSignals?.EmitSignal(nameof(GlobalSignals.UpdateShellBar), cue.Id);
				GetNodeOrNull<MediaHealthService>("/root/MediaHealthService")?.CheckCue(cue.Id);
			}

			_globalSignals.EmitSignal(nameof(GlobalSignals.SyncShellInspector));
			int focusId = _focusedCue?.Id ?? multiCues[^1].Id;
			ShellSelected(focusId);
			return;
		}

		// Single-cue path
		var existingVideo = _focusedCue.Components.OfType<VideoComponent>().FirstOrDefault();
		bool isNewComponent = existingVideo == null;
		InspectorMultiEditSupport.RecordBeforeEdit(
			_globalData,
			multiHistory: false,
			_focusedCue,
			isNewComponent
				? (isImage ? "Add image component" : "Add video component")
				: (isImage ? "Change image file" : "Change video file"));
		if (existingVideo != null)
			_focusedVideoComponent = existingVideo;
		else
			_focusedVideoComponent = _focusedCue.AddVideoComponent(pathToStore);

		ApplyVideoFileToComponent(
			_focusedVideoComponent, pathToStore, isImage, fileMetadata, isNewComponent, resetInOutPoints, null);

		_fileUrl.Text = pathToStore;
		_inspectorContent.Visible = true;
		_selectFileContainer.Visible = true;
		_infoLabel.Text = "";

		_cachedPeaks = null;
		_cachedPeaksSource = null;

		_focusedCue.CalculateTotalDuration();

		// Always regenerate waveform when audio is present (RefreshAudioUiState skips if old data remains)
		if (_focusedVideoComponent.HasAudio && _focusedVideoComponent.UseAudio)
		{
			try
			{
				var wave = await _mediaEngine.GenerateWaveformAsync(
					_focusedVideoComponent.VideoFile, RestartWaveformToken());
				if (_focusedVideoComponent == null) return;
				if (wave == null || wave.Length == 0)
				{
					_globalSignals.EmitSignal(nameof(GlobalSignals.Log),
						$"VideoInspector:SetVideoFile - Waveform generation failed for {_focusedVideoComponent.VideoFile}", 2);
				}
				else
				{
					_focusedVideoComponent.WaveformData = wave;
				}
			}
			catch (OperationCanceledException)
			{
				return;
			}
			catch (Exception ex)
			{
				_globalSignals.EmitSignal(nameof(GlobalSignals.Log),
					$"VideoInspector:SetVideoFile - Error generating waveform: {ex.Message}", 2);
			}
		}

		UpdateVideoUiFields(pathToStore);
		await RefreshAudioUiState();

		// Preview decoder for new path
		if (_previewContainer != null && _previewContainer.Visible && _videoPreviewer != null)
		{
			_videoPreviewer.SetAreasDeferred(_focusedVideoComponent.TargetLayerId);
			_videoPreviewer.LoadDecoder(filePath);
		}

		_globalSignals.EmitSignal(nameof(GlobalSignals.SyncShellInspector));
		_globalSignals.EmitSignal(nameof(GlobalSignals.UpdateShellBar), _focusedCue.Id);

		GD.Print($"VideoInspector:SetVideoFile - Set video file: {pathToStore}");
		_globalSignals.EmitSignal(nameof(GlobalSignals.Log),
			$"VideoInspector:Set video file to: {pathToStore}", 0);

		GetNodeOrNull<MediaHealthService>("/root/MediaHealthService")?.CheckCue(_focusedCue.Id);
		ApplyFileUrlMissingStyleFromHealth();
		if (_deleteVideoComponentButton != null)
			_deleteVideoComponentButton.Visible = true;
	}

	/// <summary>
	/// Applies a resolved media path + metadata onto one video component (create path already assigned).
	/// </summary>
	private void ApplyVideoFileToComponent(
		VideoComponent comp,
		string pathToStore,
		bool isImage,
		VideoFileMetadata fileMetadata,
		bool isNewComponent,
		bool resetInOutPoints,
		byte[] sharedWaveform)
	{
		if (comp == null || fileMetadata == null) return;

		bool pathChanged = !string.Equals(comp.VideoFile, pathToStore, StringComparison.OrdinalIgnoreCase);
		bool wasImage = comp.IsImage;
		comp.VideoFile = pathToStore;
		comp.IsImage = isImage;
		if (pathChanged)
		{
			comp.WaveformData = null;
			comp.Metadata = null;
		}

		// Switching media kind resets timing model.
		if (wasImage != isImage || (pathChanged && isImage))
		{
			comp.StartTime = 0.0;
			comp.EndTime = -1.0;
			if (isImage)
			{
				double hold = _globalData?.Settings?.VideoDefaultImageDuration ?? 0.0;
				comp.Duration = Math.Max(0.0, hold);
				comp.TotalDuration = hold <= 0 ? -1.0 : hold;
			}
		}

		comp.Metadata = fileMetadata;
		comp.IsImage = isImage;
		comp.HasAudio = !isImage && fileMetadata.AudioChannels > 0;
		if (isNewComponent)
			comp.UseAudio = comp.HasAudio && comp.UseAudio;
		else
			comp.UseAudio = comp.HasAudio;
		comp.ScaledWidth = fileMetadata.Width;
		comp.ScaledHeight = fileMetadata.Height;

		var fileDuration = fileMetadata.Duration > 0 ? fileMetadata.Duration : 0.0;

		if (isImage)
		{
			comp.StartTime = 0.0;
			comp.EndTime = -1.0;
			if (resetInOutPoints)
				comp.Duration = 0.0;
		}
		else if (resetInOutPoints || isNewComponent)
		{
			comp.StartTime = 0.0;
			comp.EndTime = -1.0;
		}
		else
		{
			if (fileDuration > 0 && comp.StartTime >= fileDuration)
				comp.StartTime = 0.0;

			if (fileDuration > 0 && comp.EndTime >= 0 && comp.EndTime > fileDuration)
				comp.EndTime = -1.0;
			else if (comp.EndTime >= 0 && comp.EndTime <= comp.StartTime)
				comp.EndTime = -1.0;
		}

		if (sharedWaveform != null && sharedWaveform.Length > 0 && comp.HasAudio && comp.UseAudio)
			comp.WaveformData = sharedWaveform;

		comp.RecalculateDuration();
	}
	
	/// <summary>
	/// Refreshes the audio-related UI elements based on the current VideoComponent's audio state.
	/// Handles visibility of audio controls, labels, output options, routing matrix, and waveform.
	/// </summary>
	/// <param name="waveformCt">Optional cancel token for waveform generate (focus change).</param>
	private async Task RefreshAudioUiState(CancellationToken waveformCt = default)
	{
		if (_focusedVideoComponent == null)
			return;

		// Still images never use the embedded-audio / waveform path.
		if (_focusedVideoComponent.IsImage)
		{
			_audioCollapseButton.Visible = false;
			_useAudioCheckButton.Visible = false;
			_useAudioLabel.Text = UiLocalizer.T("No audio (still image)");
			_audioAccordian.Visible = false;
			_audioCollapseButton.ButtonPressed = false;
			_waveformAccordian.Visible = false;
			_waveformCollapseButton.ButtonPressed = false;
			_routingAccordian.Visible = false;
			_routingCollapseButton.ButtonPressed = false;
			return;
		}

		if (_focusedVideoComponent.HasAudio)
		{
			_useAudioCheckButton.Visible = true;
			_useAudioCheckButton.ButtonPressed = _focusedVideoComponent.UseAudio;
			_useAudioLabel.Text = UiLocalizer.T("Use Embedded Audio");
			_audioCollapseButton.Visible = true;
			
			PopulateOutputOptions();
			BuildRoutingMatrix();
			
			if (_focusedVideoComponent.UseAudio)
			{
				if (_focusedVideoComponent.WaveformData == null || _focusedVideoComponent.WaveformData.Length == 0)
				{
					try
					{
						var ct = waveformCt.CanBeCanceled ? waveformCt : RestartWaveformToken();
						var wave = await _mediaEngine.GenerateWaveformAsync(
							_focusedVideoComponent.VideoFile, ct);
						if (_focusedVideoComponent == null) return;
						if (wave == null || wave.Length == 0)
						{
							if (!ct.IsCancellationRequested)
								_globalSignals.EmitSignal(nameof(GlobalSignals.Log), $"VideoInspector:RefreshAudioUiState - Waveform generation failed for {_focusedVideoComponent.VideoFile}", 2);
						}
						else
						{
							_focusedVideoComponent.WaveformData = wave;
						}
					}
					catch (OperationCanceledException)
					{
						return;
					}
					catch (Exception ex)
					{
						_globalSignals.EmitSignal(nameof(GlobalSignals.Log), $"VideoInspector:RefreshAudioUiState - Error generating waveform: {ex.Message}", 2);
					}
				}
				
				await DrawWaveform();
			}
			else
			{
				_waveformAccordian.Visible = false;
				_waveformCollapseButton.ButtonPressed = false;
			}
		}
		else
		{
			_audioCollapseButton.Visible = false;
			_useAudioCheckButton.Visible = false;
			_useAudioLabel.Text = UiLocalizer.T("No audio in file");
			_audioAccordian.Visible = false;
			_audioCollapseButton.ButtonPressed = false;
			_waveformAccordian.Visible = false;
			_waveformCollapseButton.ButtonPressed = false;
			_routingAccordian.Visible = false;
			_routingCollapseButton.ButtonPressed = false;
		}

		UpdatePanUiVisibilityAndValues();
	}
	
	/// <summary>
	/// Updates the video-related UI fields from the current VideoComponent state.
	/// </summary>
	/// <param name="file">The video file path to display.</param>
	private void UpdateVideoUiFields(string file)
	{
		if (_focusedVideoComponent == null) return;

		_selectFileContainer.Visible = true;
		_infoLabel.Text = "";
		_inspectorContent.Visible = true;
		
		_fileUrl.Text = file;
		ApplyFileUrlMissingStyleFromHealth();
		if (_deleteVideoComponentButton != null)
			_deleteVideoComponentButton.Visible = true;

		ApplyImageVideoUiMode(_focusedVideoComponent.IsImage);

		if (_focusedVideoComponent.IsImage)
		{
			// Image: only user duration (0 = until stopped). No start/end/file duration.
			if (_focusedVideoComponent.Duration <= 0)
			{
				_durationValue.Text = "0 (until stopped)";
				_durationValue.TooltipText = UiLocalizer.T("0 = stay active until stopped. Enter a time to auto-end.");
			}
			else
			{
				_durationValue.Text = UiUtilities.ParseAndFormatTime(
					_focusedVideoComponent.Duration.ToString(), out _, out string imgDurTip);
				_durationValue.TooltipText = imgDurTip + " (0 = until stopped)";
			}
		}
		else
		{
			_startTimeInput.Text = UiUtilities.ParseAndFormatTime(_focusedVideoComponent.StartTime.ToString(), out _, out string startTip);
			_startTimeInput.TooltipText = startTip;

			double metaDur = _focusedVideoComponent.Metadata?.Duration ?? 0;
			if (_focusedVideoComponent.EndTime < 0)
			{
				_endTimeInput.Text = $"Full ({UiUtilities.FormatTime(metaDur)})";
			}
			else
			{
				_endTimeInput.Text = UiUtilities.FormatTime(_focusedVideoComponent.EndTime);
			}
			_durationValue.Text = UiUtilities.FormatTime(_focusedVideoComponent.Duration);
			_durationValue.TooltipText = UiLocalizer.T("m:s:ms (derived from start/end)");
			_fileDurationValue.Text = UiUtilities.FormatTime(metaDur);
			_loopInput.SetPressedNoSignal(_focusedVideoComponent.Loop);
			_playCountInput.Text = _focusedVideoComponent.PlayCount.ToString();
		}

		// Fades apply to both video and still images.
		if (_fadeInInput != null)
			_fadeInInput.Text = UiUtilities.FormatTime(_focusedVideoComponent.FadeInDuration);
		if (_fadeOutInput != null)
			_fadeOutInput.Text = UiUtilities.FormatTime(_focusedVideoComponent.FadeOutDuration);
		
		// Update metadata label
		var meta = _focusedVideoComponent.Metadata;
		if (meta != null)
		{
			string metadataText;
			if (_focusedVideoComponent.IsImage)
			{
				metadataText =
					UiLocalizer.T("Type: Still Image") + "\n" +
					UiLocalizer.Tf("Resolution: {0}x{1}", meta.Width, meta.Height) + "\n" +
					UiLocalizer.Tf("Codec: {0}", meta.Codec) + "\n" +
					UiLocalizer.Tf("Format: {0}", meta.Format) + "\n" +
					UiLocalizer.Tf("Hold: {0}", _focusedVideoComponent.Duration <= 0
						? UiLocalizer.T("Until stopped")
						: UiUtilities.FormatTime(_focusedVideoComponent.Duration));
			}
			else
			{
				metadataText =
					UiLocalizer.Tf("Duration: {0}", UiUtilities.FormatTime(meta.Duration)) + "\n" +
					UiLocalizer.Tf("Resolution: {0}x{1}", meta.Width, meta.Height) + "\n" +
					UiLocalizer.Tf("Frame Rate: {0} fps", meta.FrameRate.ToString("F1")) + "\n" +
					UiLocalizer.Tf("Codec: {0}", meta.Codec) + "\n" +
					UiLocalizer.Tf("Format: {0}", meta.Format);
				if (meta.AudioChannels > 0)
				{
					metadataText += $"\nAudio Channels: {meta.AudioChannels} \n" +
					                $"Audio Sample Rate: {meta.AudioSampleRate} Hz \n" +
					                $"Audio Bit Depth: {meta.AudioBitDepth} \n" +
					                $"Audio Codec: {meta.AudioCodec}";
				}
				else
				{
					metadataText += "\nNo Audio";
				}

				int subCount = meta.SubtitleTracks?.Count ?? 0;
				int textSubCount = meta.SubtitleTracks?.Count(t => t != null && t.IsTextBased) ?? 0;
				if (subCount > 0)
					metadataText += $"\nSubtitles: {subCount} track(s) ({textSubCount} text)";
				else
					metadataText += "\nNo Subtitles";
			}
			_fileUrl.TooltipText = metadataText;
			if (_fileMetadataLabel != null)
			{
				int textSubs = meta.SubtitleTracks?.Count(t => t != null && t.IsTextBased) ?? 0;
				_fileMetadataLabel.Text = textSubs > 0
					? $"{meta.Width}x{meta.Height} · {meta.Codec} · CC×{textSubs}"
					: $"{meta.Width}x{meta.Height} · {meta.Codec}";
				_fileMetadataLabel.TooltipText = metadataText;
				_fileMetadataLabel.Visible = !string.IsNullOrWhiteSpace(_fileMetadataLabel.Text);
			}
		}
		else
		{
			ClearFileMetadataLabel();
		}

		RefreshSubtitleUi();
		
		// Update scale and offset
		_scaleWidthLineEdit.Text = _focusedVideoComponent.ScaledWidth.ToString();
		_scaleHeightLineEdit.Text = _focusedVideoComponent.ScaledHeight.ToString();
		_offsetXLineEdit.Text = _focusedVideoComponent.OffsetX.ToString();
		_offsetYLineEdit.Text = _focusedVideoComponent.OffsetY.ToString();

		// TextureRect expand + stretch + opacity
		SelectOptionById(_expandModeOptionButton, (int)_focusedVideoComponent.TextureExpandMode);
		SelectOptionById(_stretchModeOptionButton, (int)_focusedVideoComponent.TextureStretchMode);
		_videoPreviewer?.ApplyTextureLayout(_focusedVideoComponent);
		float opacityPct = Mathf.Clamp(_focusedVideoComponent.Opacity, 0f, 1f) * 100f;
		_opacityLineEdit.Text = $"{opacityPct:0.#}";
		_videoPreviewer?.ApplyOpacity(_focusedVideoComponent.Opacity);
		
		// Update volume
		var volume = _focusedVideoComponent.UseAudio ? _focusedVideoComponent.AudioVolume : _focusedVideoComponent.Volume;
		_volumeInput.Text = UiUtilities.FormatComponentVolumeDb((float)volume);
		UpdatePanUiVisibilityAndValues();
	}

	/// <summary>
	/// Hides the compact file-details line under the media URL.
	/// </summary>
	private void ClearFileMetadataLabel()
	{
		if (_fileMetadataLabel == null)
			return;
		_fileMetadataLabel.Text = "";
		_fileMetadataLabel.TooltipText = "";
		_fileMetadataLabel.Visible = false;
	}
}
