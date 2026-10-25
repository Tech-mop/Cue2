// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;

namespace Cue2.Services;

/// <summary>
/// A system-level failure discovered while Cue2 was starting (native libraries, SDL, and similar).
/// </summary>
/// <remarks>
/// User configuration problems (missing audio device, missing last showfile, OSC port in use)
/// are not startup faults. Those stay in the log only.
/// </remarks>
public sealed class StartupFault
{
	/// <summary>Stable id used to deduplicate reports (for example <c>ffmpeg</c>, <c>sdl</c>, <c>midi</c>).</summary>
	public string Id { get; }

	/// <summary>Short subsystem name shown in the dialog details (for example <c>FFmpeg</c>).</summary>
	public string Subsystem { get; }

	/// <summary>Technical reason, already logged; shown as secondary copy in the dialog.</summary>
	public string Detail { get; }

	/// <summary>User-facing capabilities that will not work until this is fixed.</summary>
	public IReadOnlyList<string> AffectedFeatures { get; }

	/// <summary>
	/// Creates a fault record. Callers should go through <see cref="StartupFaultRegistry.Report"/>.
	/// </summary>
	/// <param name="id">Stable id.</param>
	/// <param name="subsystem">Short subsystem name.</param>
	/// <param name="detail">Technical reason.</param>
	/// <param name="affectedFeatures">Capabilities affected by this failure.</param>
	public StartupFault(string id, string subsystem, string detail, IReadOnlyList<string> affectedFeatures)
	{
		Id = id ?? string.Empty;
		Subsystem = subsystem ?? string.Empty;
		Detail = detail ?? string.Empty;
		AffectedFeatures = affectedFeatures ?? Array.Empty<string>();
	}
}

/// <summary>
/// Collects system faults during autoload <c>_Ready</c> so the main window can show one dialog.
/// </summary>
/// <remarks>
/// Reports after <see cref="Seal"/> are ignored so later user/device errors cannot open the
/// boot dialog. Thread-safe: native load can fail off the main thread in principle.
/// </remarks>
public sealed class StartupFaultRegistry
{
	private readonly object _lock = new();
	private readonly List<StartupFault> _faults = new();
	private bool _sealed;

	/// <summary>True when at least one system fault was recorded before seal.</summary>
	public bool HasFaults
	{
		get
		{
			lock (_lock)
				return _faults.Count > 0;
		}
	}

	/// <summary>
	/// Records a boot-time system fault. Duplicate <paramref name="id"/> values are ignored.
	/// </summary>
	/// <param name="id">Stable id (for example <c>ffmpeg</c>).</param>
	/// <param name="subsystem">Short name for the dialog details line.</param>
	/// <param name="detail">Technical reason.</param>
	/// <param name="affectedFeatures">User-facing capabilities this failure takes away.</param>
	/// <returns>True when the fault was newly recorded.</returns>
	public bool Report(string id, string subsystem, string detail, params string[] affectedFeatures)
	{
		if (string.IsNullOrWhiteSpace(id))
			return false;

		lock (_lock)
		{
			if (_sealed)
				return false;

			string key = id.Trim();
			for (int i = 0; i < _faults.Count; i++)
			{
				if (string.Equals(_faults[i].Id, key, StringComparison.Ordinal))
					return false;
			}

			var features = new List<string>();
			if (affectedFeatures != null)
			{
				foreach (string feature in affectedFeatures)
				{
					if (string.IsNullOrWhiteSpace(feature))
						continue;
					string trimmed = feature.Trim();
					bool exists = false;
					for (int i = 0; i < features.Count; i++)
					{
						if (string.Equals(features[i], trimmed, StringComparison.Ordinal))
						{
							exists = true;
							break;
						}
					}
					if (!exists)
						features.Add(trimmed);
				}
			}

			_faults.Add(new StartupFault(
				key,
				string.IsNullOrWhiteSpace(subsystem) ? key : subsystem.Trim(),
				detail?.Trim() ?? string.Empty,
				features));
			return true;
		}
	}

	/// <summary>Copy of recorded faults in report order.</summary>
	/// <returns>A new list; empty when none were recorded.</returns>
	public IReadOnlyList<StartupFault> Snapshot()
	{
		lock (_lock)
			return _faults.ToArray();
	}

	/// <summary>
	/// Unique affected-feature labels across every fault, in first-seen order.
	/// </summary>
	/// <returns>A new list; empty when none were recorded.</returns>
	public IReadOnlyList<string> CollectAffectedFeatures()
	{
		lock (_lock)
		{
			var list = new List<string>();
			for (int i = 0; i < _faults.Count; i++)
			{
				IReadOnlyList<string> features = _faults[i].AffectedFeatures;
				for (int j = 0; j < features.Count; j++)
				{
					string feature = features[j];
					bool exists = false;
					for (int k = 0; k < list.Count; k++)
					{
						if (string.Equals(list[k], feature, StringComparison.Ordinal))
						{
							exists = true;
							break;
						}
					}
					if (!exists)
						list.Add(feature);
				}
			}

			return list;
		}
	}

	/// <summary>
	/// Stops further reports. Call when the startup dialog is shown (or skipped because none).
	/// </summary>
	public void Seal()
	{
		lock (_lock)
			_sealed = true;
	}
}
