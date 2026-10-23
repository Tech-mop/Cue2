// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System;
using System.Diagnostics;
using System.Text;
using Godot;
using Cue2.Domain.Cuelist;
using Cue2.Domain.Cues;
using Cue2.Domain.Playback;
using Cue2.Services;

namespace Cue2.Domain.Commands;

/// <summary>
/// Partial: automatic standby. The cue on the playhead has its media opened ahead of GO
/// (row hidden, nothing heard or seen), so GO starts it without waiting for files to open.
/// Kept out of the active cue list until GO takes it over.
/// </summary>
public partial class CueCommandExecutor
{
    /// <summary>How often the standby is checked against the playhead and the show.</summary>
    private const double StandbyCheckSeconds = 0.25;

    /// <summary>
    /// The playhead (and the cue's settings) must be unchanged this long first, so a run of
    /// edits (typing, dragging a value) opens the cue once when it ends, not once per edit.
    /// </summary>
    private const double StandbySettleSeconds = 0.5;

    /// <summary>No preload starts within this long of a cue start.</summary>
    private const double StandbyQuietSeconds = 0.5;

    /// <summary>
    /// While video plays, no preload starts within this long of a cue start, so the new
    /// pictures have filled their buffers before the preload opens more files.
    /// </summary>
    private const double StandbyQuietWithVideoSeconds = 2.0;

    /// <summary>
    /// No preload starts while frames take this many times the display interval or more
    /// (main thread behind, pictures already being dropped).
    /// </summary>
    private const double StandbyBehindFactor = 1.25;

    /// <summary>
    /// A cue whose group tree (the cue, its children, their children...) has more cues than this
    /// is not preloaded: the standby would open every one of them at once. GO opens it as usual.
    /// </summary>
    private const int StandbyMaxTreeCues = 16;

    private bool _standbyWaitLogged;
    private int _standbyTooLargeCueId = -1;

    private Timer _standbyTimer;
    private ActiveCue _autoStandby;
    private int _autoStandbyCueId = -1;
    private string _autoStandbyKey;
    private long _autoStandbyEditId;
    private string _autoStandbyDisplays;
    private bool _autoStandbyReady;
    private string _failedStandbyKey;
    private string _settlingKey;
    private long _settlingSinceTicks;

    // Keys left out of the fingerprint: names and notes do not change what plays, and the
    // rest are filled in by opening the media (so the preload itself would change them).
    private static readonly string[] StandbyIgnoredCueKeys =
        { "Name", "CueNum", "Notes", "Memo", "Color", "Expanded", "Duration", "TotalDuration" };
    private static readonly string[] StandbyIgnoredComponentKeys =
        { "Metadata", "Duration", "IsImage", "HasAudio" };

    private void SetupAutoStandby()
    {
        _standbyTimer = new Timer
        {
            WaitTime = StandbyCheckSeconds,
            OneShot = false,
            Autostart = true,
            Name = "StandbyPreloadTimer"
        };
        _standbyTimer.Timeout += ReconcileAutoStandby;
        AddChild(_standbyTimer);

        _globalSignals.AudioDevicesChanged += OnStandbyDevicesChanged;
        _globalSignals.DisplaysChanged += OnStandbyDisplaysChanged;
        _globalSignals.SessionLoadStarted += OnStandbySessionLoadStarted;
        _globalSignals.NewSession += OnStandbyDevicesChanged;
        _globalSignals.ShowModeChanged += OnStandbyShowModeChanged;
    }

    private void TearDownAutoStandby()
    {
        if (_globalSignals != null)
        {
            _globalSignals.AudioDevicesChanged -= OnStandbyDevicesChanged;
            _globalSignals.DisplaysChanged -= OnStandbyDisplaysChanged;
            _globalSignals.SessionLoadStarted -= OnStandbySessionLoadStarted;
            _globalSignals.NewSession -= OnStandbyDevicesChanged;
            _globalSignals.ShowModeChanged -= OnStandbyShowModeChanged;
        }
        if (_standbyTimer != null && GodotObject.IsInstanceValid(_standbyTimer))
        {
            _standbyTimer.Timeout -= ReconcileAutoStandby;
            _standbyTimer.Stop();
        }
        ReleaseAutoStandby("closing", freeImmediately: true);
    }

    private void OnStandbyDevicesChanged() => ReleaseAutoStandby("outputs or show changed");

    /// <summary>
    /// Screens or layers changed. Moving, resizing, renaming, reordering and the other layout
    /// edits reach the standby's layer hosts live (as they do a playing cue's), so the standby
    /// is kept. It is released only when a screen window was added, removed or recreated, or a
    /// layer was added or removed, because its layer hosts were made for the screens it saw.
    /// </summary>
    private void OnStandbyDisplaysChanged()
    {
        if (_autoStandby == null)
            return;
        if (BuildDisplayStructureKey() != _autoStandbyDisplays)
            ReleaseAutoStandby("screens or layers added or removed");
    }

    private void OnStandbySessionLoadStarted(string showName) => ReleaseAutoStandby("show opening");

    /// <summary>
    /// With "Show mode only", leaving Show Mode closes the standby at once (not on the next
    /// check). Entering Show Mode needs nothing here: the next check makes one.
    /// </summary>
    private void OnStandbyShowModeChanged(bool showMode)
    {
        if (_autoStandby != null && _globalData?.Settings != null && !_globalData.Settings.PreloadStandbyActive)
            ReleaseAutoStandby("edit mode");
    }

    /// <summary>
    /// Keeps one standby for the playhead cue: makes it when the playhead has settled, and
    /// releases it as soon as the playhead, the cue, the show or the outputs change, or memory
    /// runs short. Never blocks: opening runs on the media workers.
    /// </summary>
    private void ReconcileAutoStandby()
    {
        try
        {
            ReconcileAutoStandbyCore();
        }
        catch (Exception ex)
        {
            GD.PrintErr($"CueCommandExecutor:ReconcileAutoStandby - {ex.Message}");
        }
    }

    private void ReconcileAutoStandbyCore()
    {
        if (_autoStandby != null && (!GodotObject.IsInstanceValid(_autoStandby) || !_autoStandby.IsStandbyPreloaded))
            ReleaseAutoStandby("no longer waiting");

        Cue target = GetStandbyTarget();
        if (target == null)
        {
            _settlingKey = null;
            ReleaseAutoStandby("playhead cue cannot be preloaded");
            return;
        }
        if (MemoryIsLow())
        {
            ReleaseAutoStandby("memory is low");
            return;
        }

        string key = BuildStandbyKey(target);
        if (_autoStandby != null)
        {
            if (_autoStandbyCueId == target.Id && (key == _autoStandbyKey || (!_autoStandbyReady && SameStateId(key))))
                return;
            ReleaseAutoStandby(_autoStandbyCueId == target.Id ? "cue changed" : "playhead moved");
        }

        if (key != _settlingKey)
        {
            _settlingKey = key;
            _settlingSinceTicks = Stopwatch.GetTimestamp();
            return;
        }
        double settled = (Stopwatch.GetTimestamp() - _settlingSinceTicks) / (double)Stopwatch.Frequency;
        if (settled < StandbySettleSeconds)
            return;
        if (key == _failedStandbyKey)
            return;
        if (PlaybackActivity.IsBusy(StandbyQuietSeconds))
            return;
        if (Cue2.Media.VideoDiagnostics.VideoPlayedWithin(0.5))
        {
            if (PlaybackActivity.IsBusy(StandbyQuietWithVideoSeconds))
                return;
            double frameMs = Cue2.Media.VideoDiagnostics.RecentFrameMs;
            double displayMs = DisplayIntervalMs();
            if (frameMs > displayMs * StandbyBehindFactor)
            {
                if (!_standbyWaitLogged)
                {
                    _standbyWaitLogged = true;
                    GD.Print($"CueCommandExecutor:ReconcileAutoStandbyCore - preload waits: frames take {frameMs:F1} ms " +
                             $"(display {displayMs:F1} ms) while video plays.");
                }
                return;
            }
        }
        _standbyWaitLogged = false;

        StartAutoStandby(target, key);
    }

    /// <summary>The single selected, armed, idle cue, or null when no standby should exist.</summary>
    private Cue GetStandbyTarget()
    {
        if (_globalData?.Settings == null || !_globalData.Settings.PreloadStandbyActive)
            return null;
        if (_activeCueList == null || !GodotObject.IsInstanceValid(_activeCueList))
            return null;
        if (_globalSignals == null || _globalSignals.IsGoDisabledBy(GlobalSignals.GoDisableReasonSessionLoad))
            return null;
        if (ShellSelection.SelectedCues.Count != 1)
            return null;
        var cue = ShellSelection.SelectedCues[0];
        if (cue == null || !cue.Armed)
            return null;
        if ((cue.Components == null || cue.Components.Count == 0) && (cue.ChildCues == null || cue.ChildCues.Count == 0))
            return null;
        // Playing (or loaded by OSC Load): GO uses that instance or starts another; no standby.
        foreach (var _ in FindActiveCuesById(cue.Id))
            return null;
        // A large group would open all its cues at once; leave it to GO.
        if (cue.ChildCues != null && cue.ChildCues.Count > 0
            && CountTreeCues(cue, StandbyMaxTreeCues + 1) > StandbyMaxTreeCues)
        {
            if (_standbyTooLargeCueId != cue.Id)
            {
                _standbyTooLargeCueId = cue.Id;
                GD.Print($"CueCommandExecutor:GetStandbyTarget - \"{cue.Name}\" not preloaded: its group has more than " +
                         $"{StandbyMaxTreeCues} cues.");
            }
            return null;
        }
        return cue;
    }

    /// <summary>
    /// Counts the cue and everything nested under it, stopping once <paramref name="limit"/> is reached.
    /// </summary>
    private static int CountTreeCues(Cue root, int limit)
    {
        int count = 0;
        var pending = new System.Collections.Generic.Stack<(Cue cue, int depth)>();
        pending.Push((root, 0));
        while (pending.Count > 0 && count < limit)
        {
            var (cue, depth) = pending.Pop();
            if (cue == null)
                continue;
            count++;
            if (cue.ChildCues == null || depth >= 64)
                continue;
            foreach (int childId in cue.ChildCues)
                pending.Push((CueList.FetchCueFromId(childId), depth + 1));
        }
        return count;
    }

    /// <summary>Frame interval of the operator screen, in milliseconds (60 Hz if unknown).</summary>
    private static double DisplayIntervalMs()
    {
        double hz = 0;
        try { hz = DisplayServer.ScreenGetRefreshRate(); } catch { /* unknown */ }
        if (hz < 20 || hz > 500)
            hz = 60;
        return 1000.0 / hz;
    }

    private static bool MemoryIsLow()
    {
        var info = GC.GetGCMemoryInfo();
        return info.HighMemoryLoadThresholdBytes > 0 && info.MemoryLoadBytes >= info.HighMemoryLoadThresholdBytes;
    }

    private void StartAutoStandby(Cue cue, string key)
    {
        var active = new ActiveCue(
            cue,
            _activeCueList,
            _mediaEngine,
            _audioDevices,
            _globalSignals,
            chainMember: null);
        _autoStandby = active;
        _autoStandbyCueId = cue.Id;
        _autoStandbyKey = key;
        _autoStandbyEditId = CurrentPlaybackEditId();
        _autoStandbyDisplays = BuildDisplayStructureKey();
        _autoStandbyReady = false;
        active.Completed += () =>
        {
            if (_autoStandby == active)
                ClearAutoStandbyFields();
        };
        GD.Print($"CueCommandExecutor:StartAutoStandby - preloading \"{cue.Name}\" (id={cue.Id})");
        TaskUtil.FireAndForget(PreloadAutoStandbyAsync(active, cue), "CueCommandExecutor.AutoStandby");
    }

    private async System.Threading.Tasks.Task PreloadAutoStandbyAsync(ActiveCue active, Cue cue)
    {
        long started = Stopwatch.GetTimestamp();
        bool ok = await active.PreloadAsAutoStandbyAsync();
        if (_autoStandby != active)
            return; // taken by GO, or released while opening
        if (!ok)
        {
            GD.Print($"CueCommandExecutor:PreloadAutoStandbyAsync - preload of \"{cue.Name}\" failed; GO will open it normally.");
            string failedKey = _autoStandbyKey;
            ReleaseAutoStandby("preload failed");
            _failedStandbyKey = failedKey;
            return;
        }
        if (CurrentPlaybackEditId() != _autoStandbyEditId)
        {
            ReleaseAutoStandby("cue changed while opening");
            return;
        }
        // Opening the media fills in some cue fields (for example an image's sound flag):
        // take the fingerprint again so that is not seen as an edit.
        _autoStandbyKey = BuildStandbyKey(cue);
        _autoStandbyReady = true;
        double ms = (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency;
        GD.Print($"CueCommandExecutor:PreloadAutoStandbyAsync - \"{cue.Name}\" ready in {ms:F0} ms");
    }

    /// <summary>
    /// GO on the playhead cue: hands over the standby when it still matches the cue, or null.
    /// The caller adds it to the active list. A standby still opening is handed over too; the
    /// start then waits only for the rest of the open.
    /// </summary>
    private ActiveCue TakeAutoStandby(Cue cue, out string note)
    {
        note = "not preloaded";
        var active = _autoStandby;
        if (active == null || cue == null || _autoStandbyCueId != cue.Id)
            return null;
        if (!GodotObject.IsInstanceValid(active) || !active.IsStandbyPreloaded)
        {
            ReleaseAutoStandby("no longer waiting");
            return null;
        }
        string key = BuildStandbyKey(cue);
        bool matches = key == _autoStandbyKey || (!_autoStandbyReady && SameStateId(key));
        if (!matches)
        {
            ReleaseAutoStandby("cue changed");
            return null;
        }

        note = active.ComponentsReady ? "preloaded" : "preload in progress";
        ClearAutoStandbyFields();
        _settlingKey = null;
        active.PromoteAutoStandby();
        return active;
    }

    private void ReleaseAutoStandby(string reason, bool freeImmediately = false)
    {
        var active = _autoStandby;
        ClearAutoStandbyFields();
        if (active == null || !GodotObject.IsInstanceValid(active))
            return;
        GD.Print($"CueCommandExecutor:ReleaseAutoStandby - released \"{active.Cue?.Name}\" ({reason})");
        try
        {
            active.Cleanup(freeImmediately);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"CueCommandExecutor:ReleaseAutoStandby - {ex.Message}");
        }
    }

    private void ClearAutoStandbyFields()
    {
        _autoStandby = null;
        _autoStandbyCueId = -1;
        _autoStandbyKey = null;
        _autoStandbyDisplays = null;
        _autoStandbyReady = false;
    }

    /// <summary>
    /// Edit counter that ignores screen and layer layout edits (see
    /// <see cref="HistoryManager.PlaybackEditId"/>), so the canvas editor does not release the standby.
    /// </summary>
    private long CurrentPlaybackEditId() => _globalData?.HistoryManager?.PlaybackEditId ?? 0;

    /// <summary>
    /// The screen windows (by object, so a window recreated by undo or show load counts as new)
    /// and the layer ids. Positions, sizes, names and order are left out: they are applied live.
    /// </summary>
    private static string BuildDisplayStructureKey()
    {
        var sb = new StringBuilder(64);
        var outputs = DisplaysManager.Outputs;
        if (outputs != null)
        {
            foreach (var output in outputs)
            {
                if (output != null && GodotObject.IsInstanceValid(output))
                    sb.Append(output.GetInstanceId()).Append(',');
            }
        }
        sb.Append('/');
        var layers = DisplaysManager.Layers;
        if (layers != null)
        {
            var ids = new System.Collections.Generic.List<int>(layers.Count);
            foreach (var layer in layers)
            {
                if (layer != null)
                    ids.Add(layer.LayerId);
            }
            ids.Sort();
            foreach (int id in ids)
                sb.Append(id).Append(',');
        }
        return sb.ToString();
    }

    private bool SameStateId(string key)
    {
        if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(_autoStandbyKey))
            return false;
        int a = key.IndexOf('\n');
        int b = _autoStandbyKey.IndexOf('\n');
        return a > 0 && a == b && string.CompareOrdinal(key, 0, _autoStandbyKey, 0, a) == 0;
    }

    /// <summary>
    /// Fingerprint of everything a standby depends on: the show and its edit state (leaving out
    /// screen and layer layout edits), the screen windows and layers, and the cue with its
    /// components and children (media, in and out points, loops, routing, target layers).
    /// </summary>
    private string BuildStandbyKey(Cue cue)
    {
        var sb = new StringBuilder(512);
        sb.Append(_globalData?.SessionPath ?? string.Empty).Append('|')
          .Append(CurrentPlaybackEditId()).Append('|')
          .Append(BuildDisplayStructureKey())
          .Append('\n');
        AppendCueKey(sb, cue, 0);
        return sb.ToString();
    }

    private static void AppendCueKey(StringBuilder sb, Cue cue, int depth)
    {
        if (cue == null || depth > 64)
            return;
        var data = cue.GetData();
        foreach (var k in StandbyIgnoredCueKeys)
            data.Remove(k);
        if (data.TryGetValue("Components", out var compsVariant))
        {
            var comps = compsVariant.AsGodotArray();
            foreach (var c in comps)
            {
                var comp = c.AsGodotDictionary();
                foreach (var k in StandbyIgnoredComponentKeys)
                    comp.Remove(k);
            }
        }
        sb.Append(Json.Stringify(data)).Append('\n');
        if (cue.ChildCues == null)
            return;
        foreach (int childId in cue.ChildCues)
            AppendCueKey(sb, CueList.FetchCueFromId(childId), depth + 1);
    }
}
