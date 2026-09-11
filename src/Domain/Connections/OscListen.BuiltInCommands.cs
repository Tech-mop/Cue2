// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Globalization;
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
using Cue2.Services;
using Cue2.UI.Utilities;
using Godot;
using Rug.Osc;

namespace Cue2.Domain.Connections;

/// <summary>Documentation entry for a fixed built-in OSC command (settings UI).</summary>
public readonly struct OscBuiltInCommandInfo
{
    public string Category { get; init; }
    public string Pattern { get; init; }
    public string Description { get; init; }
}

/// <summary>Built-in OSC path handlers for show control (cue targets, queries, levels, layers).</summary>
public partial class OscListen
{
    public static readonly OscBuiltInCommandInfo[] BuiltInCommandCatalog =
    {
        // App actions (Go, StopAll, …) live in OSC Input Map defaults.

        new() { Category = "Selection", Pattern = "/SelectID [id|/###]", Description = "Select by id." },
        new() { Category = "Selection", Pattern = "/SelectNum [num|/###]", Description = "Select by number." },
        new() { Category = "Selection", Pattern = "/SelectName [name|/###]", Description = "Select by name." },
        new() { Category = "Selection", Pattern = "/SelectSelected", Description = "No-op; documents selection scope." },
        new() { Category = "Selection", Pattern = "/Playhead [num|/###]", Description = "Move playhead to number." },
        new() { Category = "Selection", Pattern = "/Back", Description = "Select previous cue." },

        new() { Category = "Cue GO", Pattern = "/GoID [id|/###]", Description = "GO by id." },
        new() { Category = "Cue GO", Pattern = "/GoNum [num|/###]", Description = "GO by number." },
        new() { Category = "Cue GO", Pattern = "/GoName [name|/###]", Description = "GO by name." },
        new() { Category = "Cue GO", Pattern = "/GoSelected", Description = "GO selected cue(s)." },

        new() { Category = "Cue control", Pattern = "/StopID … [fade]", Description = "Stop by id." },
        new() { Category = "Cue control", Pattern = "/StopNum …", Description = "Stop by number." },
        new() { Category = "Cue control", Pattern = "/StopName …", Description = "Stop by name." },
        new() { Category = "Cue control", Pattern = "/StopSelected [fade]", Description = "Stop selected." },
        new() { Category = "Cue control", Pattern = "/HardStopID …", Description = "Hard stop by id." },
        new() { Category = "Cue control", Pattern = "/HardStopSelected", Description = "Hard stop selected." },
        new() { Category = "Cue control", Pattern = "/StopAllHard", Description = "Hard stop all playing." },
        new() { Category = "Cue control", Pattern = "/PauseID|Num|Name|Selected", Description = "Pause." },
        new() { Category = "Cue control", Pattern = "/ResumeID|Num|Name|Selected", Description = "Resume." },
        new() { Category = "Cue control", Pattern = "/StartNowID|Num|Name|Selected", Description = "Skip waits." },
        new() { Category = "Cue control", Pattern = "/TogglePauseSelected", Description = "Toggle pause selected." },

        new() { Category = "Seek", Pattern = "/SeekID {id} {sec}", Description = "Absolute seek." },
        new() { Category = "Seek", Pattern = "/SeekRelID {id} {sec}", Description = "Relative seek." },
        new() { Category = "Seek", Pattern = "/SeekNum|/SeekRelNum …", Description = "Seek by number." },

        new() { Category = "Levels", Pattern = "/VolumeID {id} {0-1} [sec]", Description = "Audio volume." },
        new() { Category = "Levels", Pattern = "/OpacityID {id} {0-1} [sec]", Description = "Video opacity." },
        new() { Category = "Levels", Pattern = "/VolumeNum|/OpacityNum …", Description = "By cue number." },

        new() { Category = "Arm", Pattern = "/ArmID|Num|Name|Selected", Description = "Arm." },
        new() { Category = "Arm", Pattern = "/DisarmID|Num|Name|Selected", Description = "Disarm." },
        new() { Category = "Arm", Pattern = "/ToggleArmID|Selected", Description = "Toggle arm." },

        new() { Category = "Load", Pattern = "/LoadID|Num|Name|Selected", Description = "Preload media without GO (standby)." },
        new() { Category = "Load", Pattern = "/cue/…/load", Description = "Alias: preload target cue." },

        new() { Category = "Layer", Pattern = "/Layer/{id}/pos {x} {y}", Description = "Layer position." },
        new() { Category = "Layer", Pattern = "/Layer/{id}/size {w} {h}", Description = "Layer size." },

        new() { Category = "Query", Pattern = "/ping", Description = "Reply /pong." },
        new() { Category = "Query", Pattern = "/playhead", Description = "Reply playhead id/num/name." },
        new() { Category = "Query", Pattern = "/active", Description = "Reply active cue ids." },
        new() { Category = "Query", Pattern = "/cue/status [id]", Description = "Reply cue status." },

        new() { Category = "Alias", Pattern = "/cue/by_id/{id}/start|stop|…", Description = "By id alias." },
        new() { Category = "Alias", Pattern = "/cue/by_num/{num}/start|stop|…", Description = "By number." },
        new() { Category = "Alias", Pattern = "/cue/selected/start|stop|…", Description = "Selected alias." },
        new() { Category = "Alias", Pattern = "/cue/active/pause|stop", Description = "All active." },

        new() { Category = "MIDI", Pattern = "/MidiPanic", Description = "MIDI All Notes/Sound Off." },
    };

    public const string CmdGoIdPattern = "/GoID/###";
    public const string CmdGoNumPattern = "/GoNum/###";
    public const string CmdGoNamePattern = "/GoName/###";

    private enum CueLookup { Id, Num, Name, Selected }

    private bool TryFireBuiltInCommands(OscInputMessage msg)
    {
        string address = msg.Address;
        if (string.IsNullOrEmpty(address) || address[0] != '/') return false;

        // /cue/... hierarchical aliases
        if (address.StartsWith("/cue/", StringComparison.Ordinal)
            || string.Equals(address, "/cue", StringComparison.Ordinal))
            return TryHandleCueAlias(msg);

        string[] parts = address.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return false;
        string cmd = parts[0];

        try
        {
            return cmd switch
            {
                "MidiPanic" when parts.Length == 1 => ExecMidiPanic(),
                "Back" when parts.Length == 1 => ExecBack(),
                "Playhead" => ExecPlayhead(parts, msg),

                "SelectID" => ExecSelect(parts, msg, CueLookup.Id),
                "SelectNum" => ExecSelect(parts, msg, CueLookup.Num),
                "SelectName" => ExecSelect(parts, msg, CueLookup.Name),
                "SelectSelected" => true,

                "GoID" => ExecGo(parts, msg, CueLookup.Id),
                "GoNum" => ExecGo(parts, msg, CueLookup.Num),
                "GoName" => ExecGo(parts, msg, CueLookup.Name),
                "GoSelected" => ExecGo(parts, msg, CueLookup.Selected),

                "StopID" => ExecControl(parts, msg, CueLookup.Id, ControlAction.Stop, hard: false),
                "StopNum" => ExecControl(parts, msg, CueLookup.Num, ControlAction.Stop, hard: false),
                "StopName" => ExecControl(parts, msg, CueLookup.Name, ControlAction.Stop, hard: false),
                "StopSelected" => ExecControl(parts, msg, CueLookup.Selected, ControlAction.Stop, hard: false),
                "HardStopID" => ExecControl(parts, msg, CueLookup.Id, ControlAction.Stop, hard: true),
                "HardStopNum" => ExecControl(parts, msg, CueLookup.Num, ControlAction.Stop, hard: true),
                "HardStopName" => ExecControl(parts, msg, CueLookup.Name, ControlAction.Stop, hard: true),
                "HardStopSelected" => ExecControl(parts, msg, CueLookup.Selected, ControlAction.Stop, hard: true),
                "StopAllHard" when parts.Length == 1 => ExecStopAllHard(),

                "PauseID" => ExecControl(parts, msg, CueLookup.Id, ControlAction.Pause, hard: false),
                "PauseNum" => ExecControl(parts, msg, CueLookup.Num, ControlAction.Pause, hard: false),
                "PauseName" => ExecControl(parts, msg, CueLookup.Name, ControlAction.Pause, hard: false),
                "PauseSelected" => ExecControl(parts, msg, CueLookup.Selected, ControlAction.Pause, hard: false),
                "ResumeID" => ExecControl(parts, msg, CueLookup.Id, ControlAction.Resume, hard: false),
                "ResumeNum" => ExecControl(parts, msg, CueLookup.Num, ControlAction.Resume, hard: false),
                "ResumeName" => ExecControl(parts, msg, CueLookup.Name, ControlAction.Resume, hard: false),
                "ResumeSelected" => ExecControl(parts, msg, CueLookup.Selected, ControlAction.Resume, hard: false),
                "StartNowID" => ExecControl(parts, msg, CueLookup.Id, ControlAction.StartNow, hard: false),
                "StartNowNum" => ExecControl(parts, msg, CueLookup.Num, ControlAction.StartNow, hard: false),
                "StartNowName" => ExecControl(parts, msg, CueLookup.Name, ControlAction.StartNow, hard: false),
                "StartNowSelected" => ExecControl(parts, msg, CueLookup.Selected, ControlAction.StartNow, hard: false),
                "TogglePauseSelected" when parts.Length == 1 => ExecTogglePauseSelected(),

                "SeekID" => ExecSeek(parts, msg, CueLookup.Id, relative: false),
                "SeekRelID" => ExecSeek(parts, msg, CueLookup.Id, relative: true),
                "SeekNum" => ExecSeek(parts, msg, CueLookup.Num, relative: false),
                "SeekRelNum" => ExecSeek(parts, msg, CueLookup.Num, relative: true),

                "VolumeID" => ExecLevel(parts, msg, CueLookup.Id, volume: true),
                "VolumeNum" => ExecLevel(parts, msg, CueLookup.Num, volume: true),
                "OpacityID" => ExecLevel(parts, msg, CueLookup.Id, volume: false),
                "OpacityNum" => ExecLevel(parts, msg, CueLookup.Num, volume: false),

                "ArmID" => ExecArm(parts, msg, CueLookup.Id, armed: true, toggle: false),
                "DisarmID" => ExecArm(parts, msg, CueLookup.Id, armed: false, toggle: false),
                "ArmNum" => ExecArm(parts, msg, CueLookup.Num, armed: true, toggle: false),
                "DisarmNum" => ExecArm(parts, msg, CueLookup.Num, armed: false, toggle: false),
                "ArmName" => ExecArm(parts, msg, CueLookup.Name, armed: true, toggle: false),
                "DisarmName" => ExecArm(parts, msg, CueLookup.Name, armed: false, toggle: false),
                "ArmSelected" => ExecArm(parts, msg, CueLookup.Selected, armed: true, toggle: false),
                "DisarmSelected" => ExecArm(parts, msg, CueLookup.Selected, armed: false, toggle: false),
                "ToggleArmID" => ExecArm(parts, msg, CueLookup.Id, armed: false, toggle: true),
                "ToggleArmSelected" => ExecArm(parts, msg, CueLookup.Selected, armed: false, toggle: true),

                "LoadID" => ExecLoad(parts, msg, CueLookup.Id),
                "LoadNum" => ExecLoad(parts, msg, CueLookup.Num),
                "LoadName" => ExecLoad(parts, msg, CueLookup.Name),
                "LoadSelected" => ExecLoad(parts, msg, CueLookup.Selected),

                "Layer" => ExecLayer(parts, msg),

                "ping" when parts.Length == 1 => ExecPing(msg),
                "playhead" when parts.Length == 1 => ExecQueryPlayhead(msg),
                "active" when parts.Length == 1 => ExecQueryActive(msg),

                _ => false
            };
        }
        catch (Exception ex)
        {
            LogBuiltIn($"{cmd}: error — {ex.Message}", LogType.Error);
            return true;
        }
    }

    // ── /cue aliases ────────────────────────────────────────────────────────

    private bool TryHandleCueAlias(OscInputMessage msg)
    {
        // /cue/by_id/12/start | /cue/by_num/1.2/stop | /cue/selected/pause | /cue/active/stop
        // /cue/status [id]
        string[] parts = msg.Address.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 1 || !string.Equals(parts[0], "cue", StringComparison.Ordinal))
            return false;

        if (parts.Length == 2 && parts[1] == "status")
            return ExecQueryCueStatus(msg, null);

        if (parts.Length >= 2 && parts[1] == "active")
        {
            string action = parts.Length >= 3 ? parts[2] : "";
            return action switch
            {
                "pause" => ExecActiveControl(ControlAction.Pause),
                "stop" => ExecActiveControl(ControlAction.Stop),
                "resume" => ExecActiveControl(ControlAction.Resume),
                "hardStop" => ExecStopAllHard(),
                _ => false
            };
        }

        if (parts.Length >= 2 && parts[1] == "selected")
        {
            string action = parts.Length >= 3 ? parts[2] : "start";
            return MapAliasAction(action, Array.Empty<string>(), msg, CueLookup.Selected);
        }

        if (parts.Length >= 4 && parts[1] == "by_id")
        {
            // by_id / {id} / action
            var synthetic = new[] { "ID", parts[2] };
            return MapAliasAction(parts[3], synthetic, msg, CueLookup.Id);
        }

        if (parts.Length >= 4 && parts[1] == "by_num")
        {
            string num = string.Join("/", parts.Skip(2).Take(parts.Length - 3));
            // parts: cue, by_num, num..., action
            string action = parts[^1];
            string numPath = string.Join("/", parts.Skip(2).Take(parts.Length - 3));
            if (string.IsNullOrEmpty(numPath)) numPath = parts.Length > 2 ? parts[2] : "";
            // Re-parse: /cue/by_num/1.2/start → parts = cue, by_num, 1.2, start
            if (parts.Length == 4)
                numPath = parts[2];
            else if (parts.Length > 4)
                numPath = string.Join("/", parts.Skip(2).Take(parts.Length - 3));

            return MapAliasAction(action, new[] { "Num", numPath }, msg, CueLookup.Num);
        }

        if (parts.Length >= 3 && parts[1] == "status")
            return ExecQueryCueStatus(msg, parts[2]);

        return false;
    }

    private bool MapAliasAction(string action, string[] idParts, OscInputMessage msg, CueLookup lookup)
    {
        // Build fake parts array for existing handlers: e.g. ["GoID","12"] via lookup
        string[] parts = lookup switch
        {
            CueLookup.Id when idParts.Length >= 2 => new[] { "X", idParts[1] },
            CueLookup.Num when idParts.Length >= 2 => new[] { "X", idParts[1] },
            CueLookup.Selected => new[] { "X" },
            _ => new[] { "X" }
        };

        return action switch
        {
            "start" or "go" => ExecGo(parts, msg, lookup),
            "stop" => ExecControl(parts, msg, lookup, ControlAction.Stop, hard: false),
            "hardStop" => ExecControl(parts, msg, lookup, ControlAction.Stop, hard: true),
            "pause" => ExecControl(parts, msg, lookup, ControlAction.Pause, hard: false),
            "resume" => ExecControl(parts, msg, lookup, ControlAction.Resume, hard: false),
            "startNow" => ExecControl(parts, msg, lookup, ControlAction.StartNow, hard: false),
            "arm" => ExecArm(parts, msg, lookup, armed: true, toggle: false),
            "disarm" => ExecArm(parts, msg, lookup, armed: false, toggle: false),
            "load" => ExecLoad(parts, msg, lookup),
            _ => false
        };
    }

    // ── Resolve cues ────────────────────────────────────────────────────────

    private List<Cue> ResolveCues(string[] parts, OscInputMessage msg, CueLookup lookup, int tokenStartIndex = 1)
    {
        var result = new List<Cue>();
        if (lookup == CueLookup.Selected)
        {
            if (ShellSelection.SelectedCues != null)
                result.AddRange(ShellSelection.SelectedCues.Where(c => c != null));
            return result;
        }

        if (CueList.CueIndex == null) return result;

        string token = null;
        if (parts != null && parts.Length > tokenStartIndex)
        {
            token = lookup == CueLookup.Name
                ? string.Join("/", parts.Skip(tokenStartIndex))
                : parts[tokenStartIndex];
        }

        // Prefer path token; fall back to first string/int arg
        if (string.IsNullOrWhiteSpace(token))
        {
            if (lookup == CueLookup.Id && OscMessageUtil.TryGetInt(msg.Args, 0, out int idArg))
                token = idArg.ToString(CultureInfo.InvariantCulture);
            else if (OscMessageUtil.TryGetString(msg.Args, 0, out string sArg) && !string.IsNullOrWhiteSpace(sArg))
                token = sArg.Trim();
            else if (msg.FirstFloat.HasValue && lookup == CueLookup.Id)
                token = ((int)Math.Round(msg.FirstFloat.Value)).ToString(CultureInfo.InvariantCulture);
        }

        if (string.IsNullOrWhiteSpace(token)) return result;
        token = token.Trim();

        switch (lookup)
        {
            case CueLookup.Id:
                if (int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out int id)
                    && CueList.CueIndex.TryGetValue(id, out Cue byId) && byId != null)
                    result.Add(byId);
                break;
            case CueLookup.Num:
                foreach (Cue cue in CueList.CueIndex.Values)
                {
                    if (cue != null && string.Equals((cue.CueNum ?? "").Trim(), token, StringComparison.Ordinal))
                        result.Add(cue);
                }
                break;
            case CueLookup.Name:
                foreach (Cue cue in CueList.CueIndex.Values)
                {
                    if (cue != null && string.Equals((cue.Name ?? "").Trim(), token, StringComparison.Ordinal))
                        result.Add(cue);
                }
                break;
        }
        return result;
    }

    private static string LookupLabel(CueLookup lookup) => lookup switch
    {
        CueLookup.Id => "ID",
        CueLookup.Num => "Num",
        CueLookup.Name => "Name",
        CueLookup.Selected => "Selected",
        _ => "?"
    };

    // ── Actions ─────────────────────────────────────────────────────────────

    private bool ExecMidiPanic()
    {
        var midi = GetNodeOrNull<MidiManager>("/root/MidiManager");
        if (midi == null) { LogBuiltIn("MidiPanic: MidiManager missing", LogType.Warning); return true; }
        midi.PanicAllOutputs();
        LogBuiltIn("MidiPanic: All Notes/Sound Off", LogType.Info);
        return true;
    }

    private bool ExecBack()
    {
        _globalSignals?.EmitSignal(nameof(GlobalSignals.SelectPreviousCue));
        LogBuiltIn("Back: SelectPrevious", LogType.Info);
        return true;
    }

    private bool ExecPlayhead(string[] parts, OscInputMessage msg)
    {
        // Move selection to cue by number (path or arg)
        var cues = ResolveCues(parts, msg, CueLookup.Num);
        if (cues.Count == 0)
        {
            // Try id
            cues = ResolveCues(parts, msg, CueLookup.Id);
        }
        if (cues.Count == 0)
        {
            LogBuiltIn("Playhead: no matching cue", LogType.Warning);
            return true;
        }
        _globalData?.ShellSelection?.SelectIndividualShell(cues[0], recordHistory: false);
        LogBuiltIn($"Playhead: \"{cues[0].Name}\" (id={cues[0].Id})", LogType.Info);
        return true;
    }

    private bool ExecGo(string[] parts, OscInputMessage msg, CueLookup lookup)
    {
        var cues = ResolveCues(parts, msg, lookup);
        if (cues.Count == 0) { LogBuiltIn($"Go{LookupLabel(lookup)}: no matching cue", LogType.Warning); return true; }
        foreach (var cue in cues)
            TryActivateCue(cue, $"Go{LookupLabel(lookup)}");
        return true;
    }

    private void TryActivateCue(Cue cue, string via)
    {
        if (cue == null) return;
        if (!cue.Armed)
        {
            LogBuiltIn($"{via}: skipped disarmed \"{cue.Name}\" (id={cue.Id})", LogType.Info);
            return;
        }
        var executor = _globalData?.CueCommandExecutor;
        if (executor == null) { LogBuiltIn($"{via}: executor missing", LogType.Error); return; }
        _globalSignals?.EmitSignal(nameof(GlobalSignals.Log),
            $"OSC {via}: GO \"{cue.Name}\" (#{cue.CueNum}, id={cue.Id})", (int)LogType.Info);
        executor.ActivateSequenceFrom(cue);
    }

    private bool ExecLoad(string[] parts, OscInputMessage msg, CueLookup lookup)
    {
        var cues = ResolveCues(parts, msg, lookup);
        if (cues.Count == 0)
        {
            LogBuiltIn($"Load{LookupLabel(lookup)}: no matching cue", LogType.Warning);
            return true;
        }

        var executor = _globalData?.CueCommandExecutor;
        if (executor == null)
        {
            LogBuiltIn($"Load{LookupLabel(lookup)}: executor missing", LogType.Error);
            return true;
        }

        foreach (var cue in cues)
        {
            if (cue == null) continue;
            LogBuiltIn(
                $"Load{LookupLabel(lookup)}: preloading \"{cue.Name}\" (#{cue.CueNum}, id={cue.Id})",
                LogType.Info);
            // Fire-and-forget: open decoders / build component UI without starting pre-wait or content.
            executor.PreloadCue(cue);
        }

        return true;
    }

    private bool ExecControl(string[] parts, OscInputMessage msg, CueLookup lookup, ControlAction action, bool hard)
    {
        var cues = ResolveCues(parts, msg, lookup);
        if (cues.Count == 0) { LogBuiltIn($"{action}{LookupLabel(lookup)}: no matching cue", LogType.Warning); return true; }
        var executor = _globalData?.CueCommandExecutor;
        if (executor == null) { LogBuiltIn($"{action}: executor missing", LogType.Error); return true; }

        double? stopFade = hard ? 0.0 : null;
        if (action == ControlAction.Stop && !hard)
        {
            if (lookup != CueLookup.Name && parts.Length >= 3 && TryParseDouble(parts[2], out double pathFade))
                stopFade = Math.Max(0, pathFade);
            else if (msg.SecondFloat.HasValue)
                stopFade = Math.Max(0, msg.SecondFloat.Value);
            else if (lookup == CueLookup.Selected && msg.FirstFloat.HasValue)
                stopFade = Math.Max(0, msg.FirstFloat.Value);
            else if (lookup != CueLookup.Id && lookup != CueLookup.Num && msg.FirstFloat.HasValue
                     && parts.Length < 2)
                stopFade = Math.Max(0, msg.FirstFloat.Value);
        }

        foreach (var cue in cues)
        {
            executor.ApplyControlAction(action, cue.Id, stopFadeDuration: stopFade);
            string note = hard ? " hard" : (stopFade.HasValue ? $" fade={stopFade.Value:0.###}s" : "");
            LogBuiltIn($"{action}{LookupLabel(lookup)}: \"{cue.Name}\" (id={cue.Id}){note}", LogType.Info);
        }
        return true;
    }

    private bool ExecStopAllHard()
    {
        var executor = _globalData?.CueCommandExecutor;
        if (executor == null) { LogBuiltIn("StopAllHard: executor missing", LogType.Error); return true; }
        int count = 0;
        foreach (var active in executor.ActiveCues.ToList())
        {
            if (active == null || !GodotObject.IsInstanceValid(active)) continue;
            active.StopAll(propagateToChildren: true, fadeDurationOverride: 0);
            count++;
        }
        LogBuiltIn($"StopAllHard: stopped {count} root active cue(s)", LogType.Info);
        return true;
    }

    private bool ExecActiveControl(ControlAction action)
    {
        var executor = _globalData?.CueCommandExecutor;
        if (executor == null) return true;
        var seen = new HashSet<int>();
        foreach (var root in executor.ActiveCues.ToList())
        {
            if (root?.Cue == null || !GodotObject.IsInstanceValid(root)) continue;
            foreach (var active in root.EnumerateSelfAndDescendants())
            {
                if (active?.Cue == null) continue;
                if (!seen.Add(active.Cue.Id)) continue;
                executor.ApplyControlAction(action, active.Cue.Id,
                    stopFadeDuration: action == ControlAction.Stop ? null : null);
            }
        }
        LogBuiltIn($"cue/active/{action}: applied", LogType.Info);
        return true;
    }

    private bool ExecTogglePauseSelected()
    {
        var cues = ResolveCues(null, default, CueLookup.Selected);
        if (cues.Count == 0) { LogBuiltIn("TogglePauseSelected: none selected", LogType.Warning); return true; }
        var executor = _globalData?.CueCommandExecutor;
        if (executor == null) return true;

        // Inspect live transport: paused instances must Resume, not re-Pause.
        // Previous logic treated any active instance as Pause — once paused, toggle never resumed.
        bool anyPlaying = false;
        bool anyPaused = false;
        var selectedIds = new HashSet<int>(cues.Select(c => c.Id));
        foreach (var root in executor.ActiveCues)
        {
            if (root == null || !GodotObject.IsInstanceValid(root)) continue;
            foreach (var active in root.EnumerateSelfAndDescendants())
            {
                if (active?.Cue == null || !selectedIds.Contains(active.Cue.Id)) continue;
                if (active.IsTransportPaused)
                    anyPaused = true;
                else
                    anyPlaying = true;
            }
        }

        ControlAction action;
        if (anyPlaying)
            action = ControlAction.Pause;
        else if (anyPaused)
            action = ControlAction.Resume;
        else
        {
            // Nothing live for selection — Resume is a no-op but documents intent.
            LogBuiltIn("TogglePauseSelected: no active instances", LogType.Info);
            return true;
        }

        foreach (var cue in cues)
            executor.ApplyControlAction(action, cue.Id);
        LogBuiltIn($"TogglePauseSelected: {action}", LogType.Info);
        return true;
    }

    private bool ExecSeek(string[] parts, OscInputMessage msg, CueLookup lookup, bool relative)
    {
        var cues = ResolveCues(parts, msg, lookup);
        if (cues.Count == 0) { LogBuiltIn($"Seek{LookupLabel(lookup)}: no matching cue", LogType.Warning); return true; }

        double seconds;
        if (parts.Length >= 3 && TryParseDouble(parts[2], out double pathSec))
            seconds = pathSec;
        else if (msg.SecondFloat.HasValue)
            seconds = msg.SecondFloat.Value;
        else if (msg.FirstFloat.HasValue && parts.Length < 2)
            seconds = msg.FirstFloat.Value;
        else if (OscMessageUtil.TryGetFloat(msg.Args, lookup == CueLookup.Id || lookup == CueLookup.Num ? 1 : 0, out double argSec))
            seconds = argSec;
        else if (msg.FirstFloat.HasValue)
            seconds = msg.FirstFloat.Value;
        else
        {
            LogBuiltIn("Seek: missing seconds", LogType.Warning);
            return true;
        }

        var executor = _globalData?.CueCommandExecutor;
        if (executor == null) return true;
        foreach (var cue in cues)
        {
            var stub = new ControlComponent
            {
                Action = ControlAction.Seek,
                TargetCueId = cue.Id,
                SeekTimeSeconds = seconds,
                SeekMode = relative ? ControlFadeMode.Relative : ControlFadeMode.Absolute
            };
            _ = executor.ApplyControlComponentAsync(stub, -1, _globalData?.Settings?.StopFadeDuration ?? 0f);
            LogBuiltIn($"{(relative ? "SeekRel" : "Seek")}{LookupLabel(lookup)}: \"{cue.Name}\" → {seconds:0.###}s", LogType.Info);
        }
        return true;
    }

    private bool ExecLevel(string[] parts, OscInputMessage msg, CueLookup lookup, bool volume)
    {
        var cues = ResolveCues(parts, msg, lookup);
        if (cues.Count == 0) { LogBuiltIn($"{(volume ? "Volume" : "Opacity")}{LookupLabel(lookup)}: no cue", LogType.Warning); return true; }

        // level from path parts[2] or arg after id
        double level;
        double fade = 0;
        if (parts.Length >= 3 && TryParseDouble(parts[2], out double pathLevel))
            level = pathLevel;
        else if (OscMessageUtil.TryGetFloat(msg.Args, 1, out double a1))
            level = a1;
        else if (msg.FirstFloat.HasValue && parts.Length < 2)
            level = msg.FirstFloat.Value;
        else if (OscMessageUtil.TryGetFloat(msg.Args, 0, out double a0) && parts.Length >= 2)
            level = a0;
        else if (msg.SecondFloat.HasValue)
            level = msg.SecondFloat.Value;
        else if (msg.FirstFloat.HasValue)
            level = msg.FirstFloat.Value;
        else
        {
            LogBuiltIn($"{(volume ? "Volume" : "Opacity")}: missing level 0–1", LogType.Warning);
            return true;
        }

        if (parts.Length >= 4 && TryParseDouble(parts[3], out double pathFade))
            fade = Math.Max(0, pathFade);
        else if (OscMessageUtil.TryGetFloat(msg.Args, 2, out double a2))
            fade = Math.Max(0, a2);

        level = Math.Clamp(level, 0.0, 1.0);
        var executor = _globalData?.CueCommandExecutor;
        if (executor == null) return true;

        foreach (var cue in cues)
        {
            var stub = new ControlComponent
            {
                Action = ControlAction.Fade,
                TargetCueId = cue.Id,
                PropertyFadeDuration = fade,
                FadeMode = ControlFadeMode.Absolute,
                FadeProperty = volume ? ControlFadeProperty.Volume : ControlFadeProperty.Opacity,
                FadeAudioVolumeEnabled = volume,
                FadeVideoOpacityEnabled = !volume,
                FadeAudioDb = volume ? UiUtilities.LinearToDb((float)level) : 0f,
                FadeOpacityPercent = !volume ? (float)(level * 100.0) : 100f
            };
            _ = executor.ApplyControlComponentAsync(stub, -1, _globalData?.Settings?.StopFadeDuration ?? 0f);
            LogBuiltIn($"{(volume ? "Volume" : "Opacity")}{LookupLabel(lookup)}: \"{cue.Name}\" → {level:0.###} fade={fade:0.###}s", LogType.Info);
        }
        return true;
    }

    private bool ExecSelect(string[] parts, OscInputMessage msg, CueLookup lookup)
    {
        var cues = ResolveCues(parts, msg, lookup);
        if (cues.Count == 0) { LogBuiltIn($"Select{LookupLabel(lookup)}: no matching cue", LogType.Warning); return true; }
        _globalData?.ShellSelection?.SelectIndividualShell(cues[0], recordHistory: false);
        LogBuiltIn($"Select{LookupLabel(lookup)}: \"{cues[0].Name}\" (id={cues[0].Id})", LogType.Info);
        return true;
    }

    private bool ExecArm(string[] parts, OscInputMessage msg, CueLookup lookup, bool armed, bool toggle)
    {
        var cues = ResolveCues(parts, msg, lookup);
        if (cues.Count == 0) { LogBuiltIn($"Arm{LookupLabel(lookup)}: no matching cue", LogType.Warning); return true; }

        // Collect real changes first so we can record one undo step (cue or cuelist scope).
        var changes = new List<(Cue Cue, bool Next)>();
        foreach (var cue in cues)
        {
            bool next = toggle ? !cue.Armed : armed;
            if (cue.Armed == next)
            {
                LogBuiltIn($"{(next ? "Arm" : "Disarm")}: \"{cue.Name}\" already", LogType.Info);
                continue;
            }
            changes.Add((cue, next));
        }

        if (changes.Count == 0)
            return true;

        var history = _globalData?.HistoryManager;
        if (history != null && !history.IsRestoring)
        {
            if (changes.Count == 1)
            {
                bool next = changes[0].Next;
                history.RecordCueChange(
                    changes[0].Cue.Id,
                    next ? "Arm cue (OSC)" : "Disarm cue (OSC)");
            }
            else
            {
                // Mixed arm/disarm toggle across selection still one structural snapshot.
                bool allArm = changes.All(c => c.Next);
                bool allDisarm = changes.All(c => !c.Next);
                string desc = allArm
                    ? "Arm cues (OSC)"
                    : allDisarm
                        ? "Disarm cues (OSC)"
                        : "Toggle arm cues (OSC)";
                history.RecordCuelistChange(desc);
            }
        }

        foreach (var (cue, next) in changes)
        {
            cue.Armed = next;
            _globalSignals?.EmitSignal(nameof(GlobalSignals.UpdateShellBar), cue.Id);
            LogBuiltIn($"{(next ? "Arm" : "Disarm")}: \"{cue.Name}\" (id={cue.Id})", LogType.Info);
        }
        return true;
    }

    private bool ExecLayer(string[] parts, OscInputMessage msg)
    {
        // /Layer/{id}/pos {x} {y}  or /Layer/{id}/size {w} {h}
        if (parts.Length < 3) { LogBuiltIn("Layer: need /Layer/{id}/pos|size", LogType.Warning); return true; }
        if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int layerId))
        {
            LogBuiltIn($"Layer: invalid id '{parts[1]}'", LogType.Warning);
            return true;
        }

        string op = parts[2];
        var displays = _globalData?.DisplaysManager
                       ?? GetNodeOrNull<DisplaysManager>("/root/DisplaysManager");
        if (displays == null) { LogBuiltIn("Layer: DisplaysManager missing", LogType.Error); return true; }

        var layer = DisplaysManager.GetLayerById(layerId);
        if (layer == null) { LogBuiltIn($"Layer: id {layerId} not found", LogType.Warning); return true; }
        if (layer.Locked) { LogBuiltIn($"Layer: '{layer.LayerName}' locked", LogType.Warning); return true; }

        int a0 = 0, a1 = 0;
        if (parts.Length >= 5)
        {
            int.TryParse(parts[3], out a0);
            int.TryParse(parts[4], out a1);
        }
        else
        {
            OscMessageUtil.TryGetInt(msg.Args, 0, out a0);
            OscMessageUtil.TryGetInt(msg.Args, 1, out a1);
        }

        if (op == "pos")
        {
            displays.ApplyLayerGeometryLive(layerId, new Vector2I(a0, a1), null);
            LogBuiltIn($"Layer {layerId} pos → ({a0},{a1})", LogType.Info);
        }
        else if (op == "size")
        {
            displays.ApplyLayerGeometryLive(layerId, null, new Vector2I(Math.Max(1, a0), Math.Max(1, a1)));
            LogBuiltIn($"Layer {layerId} size → ({a0},{a1})", LogType.Info);
        }
        else
        {
            LogBuiltIn($"Layer: unknown op '{op}' (pos|size)", LogType.Warning);
        }
        return true;
    }

    // ── Queries ─────────────────────────────────────────────────────────────

    private bool ExecPing(OscInputMessage msg)
    {
        SendReply(msg.Origin, new OscMessage("/pong"));
        LogBuiltIn("ping → /pong", LogType.Info);
        return true;
    }

    private bool ExecQueryPlayhead(OscInputMessage msg)
    {
        int id = _globalData?.FocusedCue ?? -1;
        var cue = id >= 0 ? CueList.FetchCueFromId(id) : null;
        if (cue == null && ShellSelection.SelectedCues?.Count > 0)
            cue = ShellSelection.SelectedCues[0];

        if (cue == null)
            SendReply(msg.Origin, new OscMessage("/reply/playhead", -1, "", ""));
        else
            SendReply(msg.Origin, new OscMessage("/reply/playhead", cue.Id, cue.CueNum ?? "", cue.Name ?? ""));
        LogBuiltIn("playhead query", LogType.Info);
        return true;
    }

    private bool ExecQueryActive(OscInputMessage msg)
    {
        var executor = _globalData?.CueCommandExecutor;
        var ids = new List<int>();
        if (executor != null)
        {
            foreach (var root in executor.ActiveCues)
            {
                if (root?.Cue == null || !GodotObject.IsInstanceValid(root)) continue;
                foreach (var a in root.EnumerateSelfAndDescendants())
                {
                    if (a?.Cue != null && !ids.Contains(a.Cue.Id))
                        ids.Add(a.Cue.Id);
                }
            }
        }
        // Reply as /reply/active with each id as int arg (cap 32)
        var args = ids.Take(32).Cast<object>().ToArray();
        var reply = args.Length == 0 ? new OscMessage("/reply/active") : new OscMessage("/reply/active", args);
        SendReply(msg.Origin, reply);
        LogBuiltIn($"active query: {ids.Count} cue(s)", LogType.Info);
        return true;
    }

    private bool ExecQueryCueStatus(OscInputMessage msg, string idToken)
    {
        int id = -1;
        if (!string.IsNullOrEmpty(idToken) && int.TryParse(idToken, out int pathId))
            id = pathId;
        else if (OscMessageUtil.TryGetInt(msg.Args, 0, out int argId))
            id = argId;

        var cue = id >= 0 ? CueList.FetchCueFromId(id) : null;
        if (cue == null)
        {
            SendReply(msg.Origin, new OscMessage("/reply/cue/status", id, "missing", 0, 0));
            LogBuiltIn($"cue/status: id {id} missing", LogType.Warning);
            return true;
        }

        bool playing = false;
        var executor = _globalData?.CueCommandExecutor;
        if (executor != null)
        {
            foreach (var root in executor.ActiveCues)
            {
                if (root == null || !GodotObject.IsInstanceValid(root)) continue;
                foreach (var a in root.EnumerateSelfAndDescendants())
                {
                    if (a?.Cue?.Id == cue.Id) { playing = true; break; }
                }
            }
        }

        // /reply/cue/status id name armed playing
        SendReply(msg.Origin, new OscMessage("/reply/cue/status",
            cue.Id, cue.Name ?? "", cue.Armed ? 1 : 0, playing ? 1 : 0));
        LogBuiltIn($"cue/status id={cue.Id} armed={cue.Armed} playing={playing}", LogType.Info);
        return true;
    }

    private static bool TryParseDouble(string text, out double value) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
}
