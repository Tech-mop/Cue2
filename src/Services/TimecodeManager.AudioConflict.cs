// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using Cue2.Domain.Cuelist;
using Cue2.Domain.Cues;
using Cue2.Domain.Timecode;
using Godot;

namespace Cue2.Services;

public partial class TimecodeManager
{
    /// <summary>LTC output id → warning text for current device conflicts.</summary>
    private readonly Dictionary<int, string> _ltcDeviceConflicts = new();

    /// <summary>Endpoint ids that have already been logged for a current conflict.</summary>
    private readonly HashSet<int> _loggedLtcDeviceConflicts = new();

    private ulong _lastLtcConflictScanMsec;

    /// <summary>
    /// True when this LTC output's playback device is also a cue direct output or in an audio patch.
    /// Sharing that SDL device with cue audio can pause or lock the timecode stream.
    /// </summary>
    /// <param name="endpoint">Endpoint to inspect.</param>
    /// <param name="message">English warning for the log and tooltip, or empty.</param>
    /// <returns><see langword="true"/> when there is a conflict.</returns>
    public bool TryGetLtcDeviceConflict(TimecodeEndpoint endpoint, out string message)
    {
        message = string.Empty;
        if (endpoint == null)
            return false;
        return _ltcDeviceConflicts.TryGetValue(endpoint.Id, out message);
    }

    /// <summary>
    /// Rebuilds the LTC-vs-audio conflict map and logs newly detected conflicts.
    /// </summary>
    /// <param name="force">When false, skip if a scan ran in the last 500 ms.</param>
    public void RefreshLtcPlaybackConflictWarnings(bool force = false)
    {
        ulong now = (ulong)System.Environment.TickCount64;
        if (!force && now - _lastLtcConflictScanMsec < 500)
            return;
        _lastLtcConflictScanMsec = now;

        var occupied = CollectOccupiedPlaybackDevices(out var patchNamesByDevice, out var directDevices);
        var current = new HashSet<int>();
        foreach (var endpoint in _outputs)
        {
            if (endpoint.Direction != TimecodeDirection.Output
                || endpoint.Kind != TimecodeKind.LinearTimecode
                || string.IsNullOrWhiteSpace(endpoint.SourceName))
            {
                _ltcDeviceConflicts.Remove(endpoint.Id);
                continue;
            }

            string device = endpoint.SourceName;
            if (!occupied.Contains(device))
            {
                _ltcDeviceConflicts.Remove(endpoint.Id);
                continue;
            }

            var reasons = new List<string>();
            if (patchNamesByDevice.TryGetValue(device, out var patchNames) && patchNames.Count > 0)
                reasons.Add("audio patch " + string.Join(", ", patchNames));
            if (directDevices.Contains(device))
                reasons.Add("a cue direct output");
            if (reasons.Count == 0)
            {
                _ltcDeviceConflicts.Remove(endpoint.Id);
                continue;
            }

            string message =
                $"LTC output '{endpoint.Name}' uses playback device '{device}', which is also {string.Join(" and ", reasons)}. " +
                "Sharing that device with cue audio can stop or lock the timecode stream. Use a dedicated output for LTC.";
            _ltcDeviceConflicts[endpoint.Id] = message;
            current.Add(endpoint.Id);
            if (!_loggedLtcDeviceConflicts.Add(endpoint.Id))
                continue;
            GD.Print($"TimecodeManager:RefreshLtcPlaybackConflictWarnings - {message}");
            _globalSignals?.EmitSignal(nameof(GlobalSignals.Log), message, (int)LogType.Warning);
        }

        var stale = new List<int>();
        foreach (int id in _ltcDeviceConflicts.Keys)
        {
            if (!current.Contains(id))
                stale.Add(id);
        }

        for (int i = 0; i < stale.Count; i++)
            _ltcDeviceConflicts.Remove(stale[i]);
        _loggedLtcDeviceConflicts.RemoveWhere(id => !current.Contains(id));
    }

    private void OnAudioDevicesChangedForLtcConflict()
    {
        RefreshLtcPlaybackConflictWarnings(force: true);
    }

    private HashSet<string> CollectOccupiedPlaybackDevices(
        out Dictionary<string, List<string>> patchNamesByDevice,
        out HashSet<string> directDevices)
    {
        var occupied = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        patchNamesByDevice = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        directDevices = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var patches = _globalData?.Settings?.GetAudioOutputPatches();
        if (patches != null)
        {
            foreach (var patch in patches.Values)
            {
                if (patch?.OutputDevices == null)
                    continue;
                string patchName = string.IsNullOrWhiteSpace(patch.Name) ? $"#{patch.Id}" : patch.Name;
                foreach (string key in patch.OutputDevices.Keys)
                {
                    if (string.IsNullOrEmpty(key))
                        continue;
                    occupied.Add(key);
                    if (!patchNamesByDevice.TryGetValue(key, out var names))
                    {
                        names = new List<string>();
                        patchNamesByDevice[key] = names;
                    }

                    if (!names.Contains(patchName))
                        names.Add(patchName);
                }
            }
        }

        if (CueList.CueIndex == null || CueList.CueIndex.Count == 0)
            return occupied;

        foreach (Cue cue in CueList.CueIndex.Values)
        {
            if (cue?.Components == null)
                continue;
            foreach (var component in cue.Components)
            {
                string direct = component switch
                {
                    AudioComponent audio => audio.DirectOutput,
                    VideoComponent video => video.DirectOutput,
                    AudioInputComponent input => input.DirectOutput,
                    _ => null
                };
                if (string.IsNullOrEmpty(direct))
                    continue;
                occupied.Add(direct);
                directDevices.Add(direct);
            }
        }

        return occupied;
    }
}
