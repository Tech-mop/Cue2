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
/// Helpers for finding cues that reference show resources (audio patches, video target layers)
/// and for bulk-unassign / replace when those resources are deleted.
/// </summary>
public static class CueResourceUsage
{
	/// <summary>
	/// Result of scanning the cuelist for resource references.
	/// </summary>
	public sealed class UsageResult
	{
		/// <summary>Cues that reference the resource (unique by id).</summary>
		public List<Cue> Cues { get; init; } = new();

		/// <summary>Readable cue numbers for tooltips (e.g. "1", "1.2", "3").</summary>
		public IReadOnlyList<string> CueNumbers =>
			Cues.Select(FormatCueNumber).Where(n => !string.IsNullOrEmpty(n)).ToList();

		/// <summary>Count of distinct cues using the resource.</summary>
		public int Count => Cues.Count;
	}

	/// <summary>
	/// Finds cues whose audio or video-embedded-audio routing uses the given patch id.
	/// </summary>
	public static UsageResult FindCuesUsingAudioPatch(int patchId)
	{
		var result = new UsageResult();
		if (patchId < 0 || CueList.CueIndex == null)
			return result;

		foreach (var cue in CueList.CueIndex.Values)
		{
			if (cue == null) continue;

			var audio = cue.GetAudioComponent();
			if (audio != null)
			{
				int id = audio.Patch?.Id ?? audio.PatchId;
				if (id == patchId)
				{
					result.Cues.Add(cue);
					continue;
				}
			}

			var video = cue.GetVideoComponent();
			if (video != null)
			{
				int id = video.Patch?.Id ?? video.PatchId;
				if (id == patchId)
					result.Cues.Add(cue);
			}
		}

		return result;
	}

	/// <summary>
	/// Finds cues whose video component targets the given layer id.
	/// </summary>
	public static UsageResult FindCuesUsingTargetLayer(int layerId)
	{
		var result = new UsageResult();
		if (layerId < 0 || CueList.CueIndex == null)
			return result;

		foreach (var cue in CueList.CueIndex.Values)
		{
			if (cue == null) continue;
			var video = cue.GetVideoComponent();
			if (video != null && video.TargetLayerId == layerId)
				result.Cues.Add(cue);
		}

		return result;
	}

	/// <summary>
	/// Unassigns the audio patch from all listed cues (sets No Output).
	/// </summary>
	public static void UnassignAudioPatch(IEnumerable<Cue> cues, int patchId)
	{
		if (cues == null) return;
		foreach (var cue in cues)
		{
			if (cue == null) continue;

			var audio = cue.GetAudioComponent();
			if (audio != null && (audio.Patch?.Id ?? audio.PatchId) == patchId)
			{
				audio.Patch = null;
				audio.PatchId = -1;
				audio.DirectOutput = null;
				audio.Routing = null;
			}

			var video = cue.GetVideoComponent();
			if (video != null && (video.Patch?.Id ?? video.PatchId) == patchId)
			{
				video.Patch = null;
				video.PatchId = -1;
				video.DirectOutput = null;
				video.Routing = null;
			}
		}
	}

	/// <summary>
	/// Replaces the audio patch on all listed cues with <paramref name="replacement"/>.
	/// </summary>
	public static void ReplaceAudioPatch(IEnumerable<Cue> cues, int oldPatchId, AudioOutputPatch replacement)
	{
		if (cues == null || replacement == null || !GodotObject.IsInstanceValid(replacement))
			return;

		foreach (var cue in cues)
		{
			if (cue == null) continue;

			var audio = cue.GetAudioComponent();
			if (audio != null && (audio.Patch?.Id ?? audio.PatchId) == oldPatchId)
			{
				audio.Patch = replacement;
				audio.PatchId = replacement.Id;
				audio.DirectOutput = null;
				audio.Routing = null;
			}

			var video = cue.GetVideoComponent();
			if (video != null && (video.Patch?.Id ?? video.PatchId) == oldPatchId)
			{
				video.Patch = replacement;
				video.PatchId = replacement.Id;
				video.DirectOutput = null;
				video.Routing = null;
			}
		}
	}

	/// <summary>
	/// Unassigns the target layer from all listed cues (sets No Output / -1).
	/// </summary>
	public static void UnassignTargetLayer(IEnumerable<Cue> cues, int layerId)
	{
		if (cues == null) return;
		foreach (var cue in cues)
		{
			var video = cue?.GetVideoComponent();
			if (video != null && video.TargetLayerId == layerId)
				video.TargetLayerId = -1;
		}
	}

	/// <summary>
	/// Replaces the target layer on all listed cues with <paramref name="newLayerId"/>.
	/// </summary>
	public static void ReplaceTargetLayer(IEnumerable<Cue> cues, int oldLayerId, int newLayerId)
	{
		if (cues == null || newLayerId < 0) return;
		foreach (var cue in cues)
		{
			var video = cue?.GetVideoComponent();
			if (video != null && video.TargetLayerId == oldLayerId)
				video.TargetLayerId = newLayerId;
		}
	}

	/// <summary>
	/// Formats a cue number for display (falls back to id when CueNum is empty).
	/// </summary>
	public static string FormatCueNumber(Cue cue)
	{
		if (cue == null) return string.Empty;
		if (!string.IsNullOrWhiteSpace(cue.CueNum))
			return cue.CueNum.Trim();
		return cue.Id.ToString();
	}

	/// <summary>
	/// Builds a multi-line tooltip listing cue numbers (and names when helpful).
	/// </summary>
	public static string BuildCueListTooltip(IEnumerable<Cue> cues)
	{
		if (cues == null)
			return string.Empty;

		var lines = cues
			.Where(c => c != null)
			.OrderBy(c => FormatCueNumber(c), StringComparer.OrdinalIgnoreCase)
			.Select(c =>
			{
				string num = FormatCueNumber(c);
				string name = c.Name ?? string.Empty;
				return string.IsNullOrWhiteSpace(name) ? num : $"{num} — {name}";
			})
			.ToList();

		if (lines.Count == 0)
			return string.Empty;

		return "Cues using this resource: " + string.Join("\n", lines);
	}
}
