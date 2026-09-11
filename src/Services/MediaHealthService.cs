// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Linq;
using Cue2.Domain.Cuelist;
using Cue2.Domain.Playback;
using Cue2.Domain.Devices;
using Cue2.Domain.ShowSettings;
using Cue2.Domain.Metadata;
using Cue2.Domain.Cues;
using Cue2.Domain.Connections;
using Cue2.Domain.Library;
using Cue2.Domain.Commands;
using Godot;

namespace Cue2.Services;

/// <summary>
/// Kind of media/routing health problem tracked for a cue.
/// </summary>
public enum MediaHealthIssueKind
{
	/// <summary>Referenced media file does not exist on disk.</summary>
	FileMissing = 0,

	/// <summary>Audio component has no valid output (not assigned or deleted/missing).</summary>
	AudioOutput = 1,

	/// <summary>Video component has no valid target layer (not assigned or deleted/missing).</summary>
	VideoTargetLayer = 2,

	/// <summary>More than one distinct issue kind is active on the cue.</summary>
	Multiple = 3
}

/// <summary>
/// Media / routing health issue currently associated with a cue.
/// </summary>
public sealed class CueMediaIssue
{
	/// <summary>Issue category (or <see cref="MediaHealthIssueKind.Multiple"/> when mixed).</summary>
	public MediaHealthIssueKind Kind { get; init; }

	/// <summary>Stored media path(s) involved when file-missing issues are present.</summary>
	public IReadOnlyList<string> Paths { get; init; } = Array.Empty<string>();

	/// <summary>User-facing tooltip / log text (one line per problem).</summary>
	public string Message { get; init; } = string.Empty;
}

/// <summary>
/// Tracks media and routing health for cues (missing files, audio output, video target layer).
/// Sources: low-frequency background scan, playback failures, path/output/layer assignment,
/// and device / display / patch changes.
/// Emits <see cref="GlobalSignals.CueMediaHealthChanged"/> when a cue's issue state changes.
/// Logs each specific problem only once until resolved and raised again.
/// </summary>
public partial class MediaHealthService : Node
{
	/// <summary>Seconds between background full scans (kept light).</summary>
	public const double CheckIntervalSeconds = 12.0;

	private GlobalData _globalData;
	private GlobalSignals _globalSignals;
	private AudioDevices _audioDevices;

	/// <summary>cueId → active aggregated issue.</summary>
	private readonly Dictionary<int, CueMediaIssue> _issues = new();

	/// <summary>
	/// Keys already logged so we do not spam the log on every scan.
	/// Format: <c>{cueId}|{category}|{detail}</c>
	/// </summary>
	private readonly HashSet<string> _loggedIssueKeys = new(StringComparer.OrdinalIgnoreCase);

	private Timer _timer;
	private int _scanCursor;

	public override void _Ready()
	{
		_globalData = GetNode<GlobalData>("/root/GlobalData");
		_globalSignals = GetNode<GlobalSignals>("/root/GlobalSignals");
		_audioDevices = GetNodeOrNull<AudioDevices>("/root/AudioDevices");

		_timer = new Timer
		{
			WaitTime = CheckIntervalSeconds,
			OneShot = false,
			Autostart = true
		};
		AddChild(_timer);
		_timer.Timeout += OnBackgroundScanTick;

		// When patches/devices/layers change, re-evaluate all cues so shell ✕ updates promptly.
		if (_globalSignals != null)
		{
			_globalSignals.DisplaysChanged += OnRoutingEnvironmentChanged;
			_globalSignals.AudioDevicesChanged += OnRoutingEnvironmentChanged;
		}

		GD.Print("MediaHealthService:_Ready - Periodic media health scan enabled.");
	}

	public override void _ExitTree()
	{
		if (_globalSignals != null)
		{
			_globalSignals.DisplaysChanged -= OnRoutingEnvironmentChanged;
			_globalSignals.AudioDevicesChanged -= OnRoutingEnvironmentChanged;
		}
	}

	/// <summary>
	/// Returns true if the cue currently has a known media health issue.
	/// </summary>
	public bool HasIssue(int cueId) => _issues.ContainsKey(cueId);

	/// <summary>
	/// Tries to get the current issue for a cue.
	/// </summary>
	public bool TryGetIssue(int cueId, out CueMediaIssue issue) =>
		_issues.TryGetValue(cueId, out issue);

	/// <summary>
	/// Tooltip text for the shell-bar issue indicator, or empty if healthy.
	/// </summary>
	public string GetIssueTooltip(int cueId) =>
		_issues.TryGetValue(cueId, out var issue) ? issue.Message : string.Empty;

	/// <summary>
	/// Issue tooltip for a shell row: this cue's own problems, plus descendant problems
	/// when the cue is collapsed so nested issues are not hidden inside a group.
	/// Expanded groups only show their own issues; children show theirs on their own rows.
	/// </summary>
	/// <param name="cue">Cue bound to the shell row.</param>
	/// <param name="tooltip">Multi-line tooltip, or empty when healthy.</param>
	/// <returns>True when the red X should be shown on this row.</returns>
	public bool TryGetShellBarIssue(Cue cue, out string tooltip)
	{
		tooltip = string.Empty;
		if (cue == null)
			return false;

		var lines = new List<string>();

		if (_issues.TryGetValue(cue.Id, out var own) && !string.IsNullOrEmpty(own.Message))
			lines.Add(own.Message);

		if (cue.ChildCues != null && cue.ChildCues.Count > 0 && !cue.Expanded)
			AppendHiddenDescendantIssueLines(cue, lines);

		if (lines.Count == 0)
			return false;

		tooltip = string.Join("\n", lines);
		return true;
	}

	/// <summary>
	/// Appends issue lines for every descendant of a collapsed group, prefixed with the
	/// child cue number/name so the parent tooltip points at the actual problem cue.
	/// </summary>
	private void AppendHiddenDescendantIssueLines(Cue parent, List<string> lines)
	{
		var seen = new HashSet<int> { parent.Id };
		Walk(parent);

		void Walk(Cue node)
		{
			if (node?.ChildCues == null || node.ChildCues.Count == 0)
				return;

			foreach (int childId in node.ChildCues)
			{
				if (!seen.Add(childId))
					continue;
				if (CueList.CueIndex == null ||
				    !CueList.CueIndex.TryGetValue(childId, out var child) ||
				    child == null)
					continue;

				if (_issues.TryGetValue(childId, out var issue) && !string.IsNullOrEmpty(issue.Message))
				{
					string label = FormatCuePointer(child);
					foreach (string raw in issue.Message.Split('\n'))
					{
						if (string.IsNullOrWhiteSpace(raw))
							continue;
						lines.Add($"{label}: {raw.Trim()}");
					}
				}

				Walk(child);
			}
		}
	}

	/// <summary>
	/// Short identity used in parent tooltips so the operator can find the child with the issue.
	/// </summary>
	private static string FormatCuePointer(Cue cue)
	{
		string num = !string.IsNullOrWhiteSpace(cue.CueNum) ? cue.CueNum : cue.Id.ToString();
		if (!string.IsNullOrWhiteSpace(cue.Name))
			return $"Cue {num} ({cue.Name})";
		return $"Cue {num}";
	}

	/// <summary>
	/// True when the given stored path is currently tracked as missing for this cue.
	/// Used by inspectors so audio vs video URL fields style independently.
	/// </summary>
	public bool IsPathMissing(int cueId, string storedPath)
	{
		if (cueId < 0 || string.IsNullOrWhiteSpace(storedPath))
			return false;
		if (!_issues.TryGetValue(cueId, out var issue) || issue.Paths == null)
			return false;

		return issue.Paths.Any(p => string.Equals(p, storedPath, StringComparison.OrdinalIgnoreCase));
	}

	/// <summary>
	/// Reports that a media file path is missing for a cue (playback or other hard failure).
	/// UI is updated once; log only if this cue+path has not been logged yet.
	/// </summary>
	/// <param name="cueId">Cue id.</param>
	/// <param name="storedUrl">Path as stored on the component (relative or absolute).</param>
	public void ReportFileMissing(int cueId, string storedUrl)
	{
		if (cueId < 0 || string.IsNullOrWhiteSpace(storedUrl))
			return;

		if (CueList.CueIndex == null || !CueList.CueIndex.TryGetValue(cueId, out var cue) || cue == null)
		{
			// Still surface the reported path alone if cue is not in the index.
			ApplyAggregatedIssue(cueId, new List<string> { storedUrl },
				new List<string> { $"File Missing: {storedUrl}" },
				MediaHealthIssueKind.FileMissing);
			return;
		}

		var missingPaths = CollectMissingPaths(cue);
		if (!missingPaths.Any(p => string.Equals(p, storedUrl, StringComparison.OrdinalIgnoreCase)))
			missingPaths.Add(storedUrl);

		EvaluateAndApply(cue, missingPaths);
	}

	/// <summary>
	/// Re-evaluates all media paths and routing on a cue and updates/clears the issue state.
	/// Call after path assignment, output/layer changes, or when a cue is selected.
	/// </summary>
	public void CheckCue(int cueId)
	{
		if (CueList.CueIndex == null || !CueList.CueIndex.TryGetValue(cueId, out var cue) || cue == null)
		{
			ClearIssue(cueId);
			return;
		}

		CheckCue(cue);
	}

	/// <summary>
	/// Re-evaluates media paths and routing for a cue instance.
	/// </summary>
	public void CheckCue(Cue cue)
	{
		if (cue == null)
			return;

		EvaluateAndApply(cue, CollectMissingPaths(cue));
	}

	/// <summary>
	/// Clears any media health issue for the cue and notifies UI.
	/// </summary>
	public void ClearIssue(int cueId)
	{
		if (!_issues.Remove(cueId))
			return;

		// Allow future re-log if the same problem returns after being healthy
		_loggedIssueKeys.RemoveWhere(k => k.StartsWith($"{cueId}|", StringComparison.Ordinal));

		_globalSignals?.EmitSignal(nameof(GlobalSignals.CueMediaHealthChanged), cueId, false, string.Empty);
	}

	/// <summary>
	/// Quiet full re-check of all media/routing cues (no summary log).
	/// Call after media copies finish, patches change, or layers/devices change.
	/// </summary>
	public void RecheckAllQuiet()
	{
		if (CueList.CueIndex == null || CueList.CueIndex.Count == 0)
			return;

		foreach (var cue in CueList.CueIndex.Values)
		{
			if (cue == null || !ShouldScanCue(cue))
				continue;
			CheckCue(cue);
		}
	}

	/// <summary>
	/// Clears all tracked issues (e.g. new session).
	/// </summary>
	public void ClearAll()
	{
		var ids = _issues.Keys.ToList();
		_issues.Clear();
		_loggedIssueKeys.Clear();
		foreach (int id in ids)
			_globalSignals.EmitSignal(nameof(GlobalSignals.CueMediaHealthChanged), id, false, string.Empty);
	}

	/// <summary>
	/// Immediately checks every cue with media (not round-robin).
	/// Updates shell/inspector UI via existing signals, logs individual new missing paths,
	/// and emits a summary log: "All files present" or "N file(s) missing".
	/// </summary>
	/// <returns>Number of missing media path references found.</returns>
	public int CheckAllMediaNow()
	{
		if (CueList.CueIndex == null || CueList.CueIndex.Count == 0)
		{
			_globalSignals.EmitSignal(nameof(GlobalSignals.Log), "File presence: no cues to check.", 0);
			return 0;
		}

		int totalMediaPaths = 0;
		int missingPaths = 0;
		int cuesWithMedia = 0;

		foreach (var cue in CueList.CueIndex.Values)
		{
			if (cue == null || !HasMediaFileReference(cue))
				continue;

			cuesWithMedia++;

			var audio = cue.GetAudioComponent();
			if (audio != null && !string.IsNullOrWhiteSpace(audio.AudioFile))
			{
				totalMediaPaths++;
				if (!MediaFileExists(audio.AudioFile))
					missingPaths++;
			}

			var video = cue.GetVideoComponent();
			if (video != null && !string.IsNullOrWhiteSpace(video.VideoFile))
			{
				totalMediaPaths++;
				if (!MediaFileExists(video.VideoFile))
					missingPaths++;
			}

			// Apply UI/log side-effects for this cue (files + routing)
			CheckCue(cue);
		}

		// Also refresh cues that only have routing concerns (component with no file yet)
		foreach (var cue in CueList.CueIndex.Values)
		{
			if (cue == null || HasMediaFileReference(cue) || !ShouldScanCue(cue))
				continue;
			CheckCue(cue);
		}

		if (totalMediaPaths == 0)
		{
			_globalSignals.EmitSignal(nameof(GlobalSignals.Log), "File presence: no media files referenced.", 0);
		}
		else if (missingPaths == 0)
		{
			_globalSignals.EmitSignal(nameof(GlobalSignals.Log),
				$"All files present ({totalMediaPaths} media path(s) on {cuesWithMedia} cue(s)).", 0);
		}
		else
		{
			_globalSignals.EmitSignal(nameof(GlobalSignals.Log),
				$"{missingPaths} file(s) missing (of {totalMediaPaths} media path(s) on {cuesWithMedia} cue(s)).",
				1);
		}

		GD.Print($"MediaHealthService:CheckAllMediaNow - total={totalMediaPaths} missing={missingPaths} cues={cuesWithMedia}");
		return missingPaths;
	}

	private void OnRoutingEnvironmentChanged()
	{
		RecheckAllQuiet();
	}

	private void OnBackgroundScanTick()
	{
		if (CueList.CueIndex == null || CueList.CueIndex.Count == 0)
			return;

		// Round-robin a small batch each tick to keep cost minimal
		var cues = CueList.CueIndex.Values.Where(c => c != null).ToList();
		if (cues.Count == 0)
			return;

		const int batchSize = 8;
		int checkedCount = 0;
		int start = _scanCursor % cues.Count;

		for (int i = 0; i < cues.Count && checkedCount < batchSize; i++)
		{
			int index = (start + i) % cues.Count;
			var cue = cues[index];
			if (!ShouldScanCue(cue))
				continue;

			CheckCue(cue);
			checkedCount++;
		}

		_scanCursor = (start + Math.Max(1, checkedCount)) % Math.Max(1, cues.Count);
	}

	/// <summary>
	/// True when the cue has an audio or video component worth health-checking.
	/// </summary>
	private static bool ShouldScanCue(Cue cue)
	{
		return cue.GetAudioComponent() != null || cue.GetVideoComponent() != null;
	}

	/// <summary>
	/// True when the cue references at least one media file path.
	/// </summary>
	private static bool HasMediaFileReference(Cue cue)
	{
		var audio = cue.GetAudioComponent();
		if (audio != null && !string.IsNullOrWhiteSpace(audio.AudioFile))
			return true;
		var video = cue.GetVideoComponent();
		if (video != null && !string.IsNullOrWhiteSpace(video.VideoFile))
			return true;
		return false;
	}

	private List<string> CollectMissingPaths(Cue cue)
	{
		var missing = new List<string>();

		var audio = cue.GetAudioComponent();
		if (audio != null && !string.IsNullOrWhiteSpace(audio.AudioFile))
		{
			if (!MediaFileExists(audio.AudioFile))
				missing.Add(audio.AudioFile);
		}

		var video = cue.GetVideoComponent();
		if (video != null && !string.IsNullOrWhiteSpace(video.VideoFile))
		{
			if (!MediaFileExists(video.VideoFile))
				missing.Add(video.VideoFile);
		}

		return missing;
	}

	private bool MediaFileExists(string storedPath) =>
		MediaPaths.Exists(storedPath, _globalData?.SessionDir);

	/// <summary>
	/// Builds the full issue list for a cue and applies or clears state.
	/// </summary>
	private void EvaluateAndApply(Cue cue, List<string> missingPaths)
	{
		var orderedPaths = (missingPaths ?? new List<string>())
			.Where(p => !string.IsNullOrWhiteSpace(p))
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
			.ToList();

		var messages = new List<string>();
		var kinds = new HashSet<MediaHealthIssueKind>();

		foreach (string path in orderedPaths)
		{
			messages.Add($"File Missing: {path}");
			kinds.Add(MediaHealthIssueKind.FileMissing);
		}

		if (TryGetAudioOutputIssue(cue, out string audioMsg, out string audioLogKey))
		{
			messages.Add(audioMsg);
			kinds.Add(MediaHealthIssueKind.AudioOutput);
			LogIssueOnce(cue.Id, audioLogKey, audioMsg);
		}

		if (TryGetVideoEmbeddedAudioOutputIssue(cue, out string videoAudioMsg, out string videoAudioLogKey))
		{
			messages.Add(videoAudioMsg);
			kinds.Add(MediaHealthIssueKind.AudioOutput);
			LogIssueOnce(cue.Id, videoAudioLogKey, videoAudioMsg);
		}

		if (TryGetVideoTargetLayerIssue(cue, out string videoMsg, out string videoLogKey))
		{
			messages.Add(videoMsg);
			kinds.Add(MediaHealthIssueKind.VideoTargetLayer);
			LogIssueOnce(cue.Id, videoLogKey, videoMsg);
		}

		// Log file-missing paths once each
		foreach (string path in orderedPaths)
			LogIssueOnce(cue.Id, $"missing|{path}", $"Media missing on cue {cue.Id}: {path}");

		if (messages.Count == 0)
		{
			ClearIssue(cue.Id);
			return;
		}

		MediaHealthIssueKind kind = kinds.Count == 1
			? kinds.First()
			: MediaHealthIssueKind.Multiple;

		ApplyAggregatedIssue(cue.Id, orderedPaths, messages, kind);
	}

	/// <summary>
	/// Evaluates audio output assignment for the cue's dedicated audio component.
	/// </summary>
	/// <returns>True when there is an output problem.</returns>
	private bool TryGetAudioOutputIssue(Cue cue, out string message, out string logKey)
	{
		message = null;
		logKey = null;

		var audio = cue.GetAudioComponent();
		if (audio == null)
			return false;

		return EvaluateAudioRoutingIssue(
			audio.Patch?.Id ?? audio.PatchId,
			audio.Patch?.Name,
			audio.DirectOutput,
			messagePrefix: "Audio output",
			logPrefix: "audio_output",
			out message,
			out logKey);
	}

	/// <summary>
	/// Evaluates audio output for a video component's embedded audio (when enabled).
	/// Same rules as a standalone audio component.
	/// </summary>
	private bool TryGetVideoEmbeddedAudioOutputIssue(Cue cue, out string message, out string logKey)
	{
		message = null;
		logKey = null;

		var video = cue.GetVideoComponent();
		if (video == null || !video.HasAudio || !video.UseAudio)
			return false;

		return EvaluateAudioRoutingIssue(
			video.Patch?.Id ?? video.PatchId,
			video.Patch?.Name,
			video.DirectOutput,
			messagePrefix: "Video audio output",
			logPrefix: "video_audio_output",
			out message,
			out logKey);
	}

	/// <summary>
	/// Shared patch/direct-output validation used by audio and video-embedded-audio components.
	/// </summary>
	private bool EvaluateAudioRoutingIssue(
		int patchId,
		string patchName,
		string directOutput,
		string messagePrefix,
		string logPrefix,
		out string message,
		out string logKey)
	{
		message = null;
		logKey = null;

		bool hasDirect = !string.IsNullOrWhiteSpace(directOutput);
		var patches = _globalData?.Settings?.GetAudioOutputPatches();

		bool patchValid = false;
		if (patchId >= 0 && patches != null &&
		    patches.TryGetValue(patchId, out var patch) &&
		    patch != null &&
		    GodotObject.IsInstanceValid(patch))
		{
			patchValid = true;
		}

		bool directValid = false;
		if (hasDirect)
		{
			var available = _audioDevices?.GetAvailableAudioDeviceNames();
			if (available != null &&
			    available.Any(n => string.Equals(n, directOutput, StringComparison.OrdinalIgnoreCase)))
			{
				directValid = true;
			}
		}

		if (patchValid || directValid)
			return false;

		if (patchId >= 0)
		{
			string label = string.IsNullOrEmpty(patchName) ? $"id {patchId}" : patchName;
			message = $"{messagePrefix} missing: patch {label}";
			logKey = $"{logPrefix}|missing_patch|{patchId}";
			return true;
		}

		if (hasDirect)
		{
			message = $"{messagePrefix} missing: {directOutput}";
			logKey = $"{logPrefix}|missing_direct|{directOutput}";
			return true;
		}

		message = $"{messagePrefix} not assigned";
		logKey = $"{logPrefix}|not_assigned";
		return true;
	}

	/// <summary>
	/// Evaluates video target layer for the cue's video component.
	/// <c>TargetLayerId &lt; 0</c> means explicitly unassigned ("No Output").
	/// Does not rewrite the cue's layer id when a layer is missing.
	/// </summary>
	/// <returns>True when there is a target-layer problem.</returns>
	private bool TryGetVideoTargetLayerIssue(Cue cue, out string message, out string logKey)
	{
		message = null;
		logKey = null;

		var video = cue.GetVideoComponent();
		if (video == null)
			return false;

		int layerId = video.TargetLayerId;

		// Explicit "No Output" / never assigned
		if (layerId < 0)
		{
			message = "Video target layer not assigned";
			logKey = "video_layer|not_assigned";
			return true;
		}

		var layer = DisplaysManager.GetLayerById(layerId);
		if (layer != null)
			return false;

		// Id was assigned but the layer no longer exists — keep the stored id for "missing" UI
		message = $"Video target layer missing: id {layerId}";
		logKey = $"video_layer|missing|{layerId}";
		return true;
	}

	private void ApplyAggregatedIssue(
		int cueId,
		List<string> missingPaths,
		List<string> messages,
		MediaHealthIssueKind kind)
	{
		if (messages == null || messages.Count == 0)
		{
			ClearIssue(cueId);
			return;
		}

		string message = string.Join("\n", messages);

		bool stateChanged = true;
		if (_issues.TryGetValue(cueId, out var previous) &&
		    previous.Kind == kind &&
		    string.Equals(previous.Message, message, StringComparison.Ordinal))
		{
			stateChanged = false;
		}

		_issues[cueId] = new CueMediaIssue
		{
			Kind = kind,
			Paths = missingPaths ?? new List<string>(),
			Message = message
		};

		if (stateChanged)
		{
			_globalSignals?.EmitSignal(nameof(GlobalSignals.CueMediaHealthChanged), cueId, true, message);
		}
	}

	private void LogIssueOnce(int cueId, string detailKey, string logMessage)
	{
		if (string.IsNullOrEmpty(detailKey) || string.IsNullOrEmpty(logMessage))
			return;

		string key = $"{cueId}|{detailKey}";
		if (!_loggedIssueKeys.Add(key))
			return;

		_globalSignals?.EmitSignal(nameof(GlobalSignals.Log), logMessage, 1);
		GD.Print($"MediaHealthService:LogIssueOnce - cue={cueId} key={detailKey}");
	}
}
