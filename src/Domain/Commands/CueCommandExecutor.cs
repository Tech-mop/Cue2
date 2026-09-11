// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
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
using Cue2.UI.Utilities;
using Godot;

namespace Cue2.Domain.Commands;

/// <summary>
/// Executes GO and pre-spawns full continue/follow chains with event-driven arming.
/// </summary>
public partial class CueCommandExecutor : Node
{
    private GlobalData _globalData;
    private GlobalSignals _globalSignals;
    private MediaEngine _mediaEngine;
    private AudioDevices _audioDevices;

    private VBoxContainer _activeCueList;

    private readonly List<ActiveCue> _activeCues = new List<ActiveCue>();

    /// <summary>
    /// Currently playing cues (for inspector live-update of visual properties).
    /// </summary>
    public IReadOnlyList<ActiveCue> ActiveCues => _activeCues;

    /// <summary>
    /// Pushes expand/stretch/opacity changes to any playing instance of a video component.
    /// </summary>
    public void RefreshPlayingVideoVisuals(VideoComponent component)
    {
        if (component == null)
            return;

        foreach (var active in _activeCues.ToList())
            active?.RefreshVideoVisuals(component);
    }

    /// <summary>
    /// Pushes text/style changes to any playing instance of a text component.
    /// </summary>
    /// <param name="component">Text component to refresh on active cues.</param>
    public void RefreshPlayingTextVisuals(TextComponent component)
    {
        if (component == null)
            return;

        foreach (var active in _activeCues.ToList())
            active?.RefreshTextVisuals(component);
    }
    
    public override void _Ready()
    {
        _globalData = GetNode<GlobalData>("/root/GlobalData");
        _globalSignals = GetNode<GlobalSignals>("/root/GlobalSignals");
        _mediaEngine = GetNode<MediaEngine>("/root/MediaEngine");
        _audioDevices = GetNode<AudioDevices>("/root/AudioDevices");
        
        _activeCueList = GetNode("/root/Cue2Base").GetNode<PanelContainer>("%ActiveCueContainer").GetNode<VBoxContainer>("%ActiveCueList");
        GD.Print("CueCommandExecutor:_Ready - Cue Command Executor Successfully added");
        
        GD.Print("Cue Command Executor Successfully added");
        
        _globalSignals.Go += GoCommand;

        TreeExiting += CleanUp;
    }

    /// <summary>
    /// Logs and returns true when GO is blocked via <see cref="GlobalSignals.DisableGo"/>.
    /// Session-load blocks are logged; double-GO protection stays silent.
    /// </summary>
    /// <param name="actionLabel">Short action name for logs.</param>
    /// <returns>True if the caller should abort.</returns>
    private bool RejectIfGoDisabled(string actionLabel)
    {
        if (_globalSignals == null || _globalSignals.IsGoEnabled)
            return false;

        if (_globalSignals.IsGoDisabledBy(GlobalSignals.GoDisableReasonSessionLoad))
        {
            _globalSignals.EmitSignal(nameof(GlobalSignals.Log),
                $"Please wait — a showfile is still loading. Cannot {actionLabel}.", (int)LogType.Info);
            GD.Print($"CueCommandExecutor:RejectIfGoDisabled - blocked {actionLabel} (session_load)");
        }

        return true;
    }

    /// <summary>
    /// Starts the show-scoped double-GO cooldown when the setting is above 0.
    /// </summary>
    private void ApplyDoubleGoProtection()
    {
        float seconds = _globalData?.Settings?.DoubleGoProtectionSeconds ?? 0f;
        if (seconds <= 0.0001f || _globalSignals == null)
            return;
        _globalSignals.DisableGo(GlobalSignals.GoDisableReasonDoubleGo, seconds);
    }

    /// <summary>
    /// GO: pre-spawn the entire continue/follow chain for each selected armed cue, wire event-driven
    /// arming (continue at content-phase start, follow at real content complete), advance playhead.
    /// Disarmed cues do not play; selection moves to the next eligible cue.
    /// </summary>
    public void GoCommand()
    {
        if (RejectIfGoDisabled("GO"))
            return;

        ApplyDoubleGoProtection();

        if (!ShellSelection.SelectedCues.Any())
        {
            GD.Print("CueCommandExecutor:GoCommand - No Shells Selected");
            return;
        }

        var selected = ShellSelection.SelectedCues.ToList();
        foreach (var cue in selected)
        {
            if (!cue.Armed)
            {
                GD.Print($"CueCommandExecutor:GoCommand - Skipping disarmed cue {cue.Name} (id={cue.Id})");
                continue;
            }

            ActivateSequenceFrom(cue);
        }

        AdvancePlayheadAfterGo(selected);
    }

    /// <summary>
    /// Advances selection after GO: after a played sequence, or past a disarmed cue that was GO'd.
    /// Cues with <see cref="Cue.ShouldSkipOnPlayhead"/> are walked over.
    /// </summary>
    /// <param name="selectedCues">Cues that were selected when GO was pressed (order preserved).</param>
    private void AdvancePlayheadAfterGo(List<Cue> selectedCues)
    {
        if (selectedCues == null || selectedCues.Count == 0) return;

        var primary = selectedCues[selectedCues.Count - 1];
        if (primary == null) return;

        Cue target;
        if (primary.Armed)
        {
            // Played (or chain head): stand on first cue after the sequence, skipping bypass targets.
            var after = primary.GetCueAfterSequence();
            if (after == null)
            {
                // No cue after sequence — stay on sequence end (existing behaviour).
                target = primary.GetSequenceEndCue();
            }
            else
            {
                target = Cue.ResolvePlayheadTarget(after) ?? primary.GetSequenceEndCue();
            }
        }
        else
        {
            // Disarmed GO: do not play; move to next eligible sibling.
            target = Cue.ResolvePlayheadTarget(primary.GetNextCueInList());
            if (target == null)
                return; // Nothing after — leave selection on the disarmed cue.
        }

        if (target == null) return;

        if (ShellSelection.SelectedCues.Count == 1 && ShellSelection.SelectedCues[0] == target)
        {
            // Still keep playhead in view if GO left selection on this cue (e.g. sequence end).
            _globalData?.Cuelist?.EnsurePlayheadVisibleAfterGo(target);
            return;
        }

        // Playback playhead move is not a document/selection undo step.
        _globalData?.ShellSelection?.SelectIndividualShell(target, recordHistory: false);
        _globalData?.Cuelist?.EnsurePlayheadVisibleAfterGo(target);
    }

    /// <summary>
    /// Pre-spawns the continue/follow chain from <paramref name="head"/> and starts the head.
    /// Fire-and-forget wrapper for manual GO / playhead (does not block the caller).
    /// </summary>
    /// <param name="head">Chain head cue to activate.</param>
    /// <param name="controlGoFadeIn">
    /// Optional fade-in seconds for the head cue when started via a control GO.
    /// When null or ≤ 0, playback uses each component's own fade-in.
    /// </param>
    /// <param name="startAtTimelineSeconds">
    /// Optional absolute body-timeline seek (pre-wait + content) applied to the head before play.
    /// Used by the Timeline Inspector playhead.
    /// </param>
    public void ActivateSequenceFrom(Cue head, double? controlGoFadeIn = null, double? startAtTimelineSeconds = null)
    {
        _ = ActivateSequenceFromAsync(head, controlGoFadeIn, startAtTimelineSeconds);
    }

    /// <summary>
    /// Prepares media for a cue without starting playback (OSC Load / standby preload).
    /// Reuses an existing standby or playing instance when present; otherwise creates a standby
    /// <see cref="ActiveCue"/> that GO can adopt.
    /// </summary>
    /// <param name="cue">Cue to preload.</param>
    public void PreloadCue(Cue cue)
    {
        TaskUtil.FireAndForget(PreloadCueAsync(cue), "CueCommandExecutor.PreloadCue");
    }

    /// <summary>
    /// Awaitable form of <see cref="PreloadCue"/>.
    /// </summary>
    /// <param name="cue">Cue to preload.</param>
    public async Task PreloadCueAsync(Cue cue)
    {
        if (cue == null || _activeCueList == null)
            return;

        if (!cue.Armed)
        {
            _globalSignals?.EmitSignal(nameof(GlobalSignals.Log),
                $"Preload skipped disarmed \"{cue.Name}\" (id={cue.Id})", (int)LogType.Info);
            return;
        }

        try
        {
            // Prefer an existing instance of this cue (standby or already active).
            var existing = FindActiveCuesById(cue.Id)
                .FirstOrDefault(a => a != null && GodotObject.IsInstanceValid(a));
            if (existing != null)
            {
                await existing.RequestPreloadAsync();
                return;
            }

            var active = new ActiveCue(
                cue,
                _activeCueList,
                _mediaEngine,
                _audioDevices,
                _globalSignals,
                chainMember: null);
            _activeCues.Add(active);
            active.Completed += () => _activeCues.Remove(active);
            active.PrepareUiInOrder();
            await active.RequestPreloadAsync();
            GD.Print($"CueCommandExecutor:PreloadCueAsync - Standby ready for \"{cue.Name}\" (id={cue.Id})");
        }
        catch (Exception ex)
        {
            GD.PrintErr($"CueCommandExecutor:PreloadCueAsync - {cue?.Name}: {ex.Message}");
            _globalSignals?.EmitSignal(nameof(GlobalSignals.Log),
                $"Preload failed for \"{cue?.Name}\": {ex.Message}", (int)LogType.Error);
        }
    }

    /// <summary>
    /// Pre-spawns the continue/follow chain from <paramref name="head"/> and starts the head,
    /// awaiting until the head has entered pre-wait or content (so control actions can chain).
    /// </summary>
    /// <param name="head">Chain head cue to activate.</param>
    /// <param name="controlGoFadeIn">Optional control GO fade-in seconds for the head.</param>
    /// <param name="startAtTimelineSeconds">
    /// Optional absolute body-timeline seek (pre-wait + content) applied to the head before start
    /// (queued as a pending seek if the body has not started yet).
    /// </param>
    public async Task ActivateSequenceFromAsync(Cue head, double? controlGoFadeIn = null, double? startAtTimelineSeconds = null)
    {
        if (head == null)
        {
            GD.PrintErr("CueCommandExecutor:ActivateSequenceFromAsync - Cue is null");
            return;
        }

        // Optional per-cue gate: hard-stop existing instances of this cue before a new GO.
        // Default is off (multiple concurrent instances allowed). Chain peers are not
        // stopped unless they share the same cue id as the head.
        // Standby-preloaded head is kept and reused below (not stopped).
        if (head.OnlyOneActiveInstance)
        {
            foreach (var old in FindActiveCuesById(head.Id).ToList())
            {
                if (old == null || !GodotObject.IsInstanceValid(old)) continue;
                if (old.IsStandbyPreloaded) continue;
                try
                {
                    old.StopAll(propagateToChildren: true, fadeDurationOverride: 0);
                }
                catch (Exception ex)
                {
                    GD.PrintErr($"CueCommandExecutor:ActivateSequenceFromAsync - stop prior: {ex.Message}");
                }
            }
        }

        var chain = CueSequencePlanner.BuildChain(head);
        if (chain.Count == 0)
        {
            chain = new List<CueChainMember>
            {
                new CueChainMember
                {
                    Cue = head,
                    IncomingMode = FollowType.None,
                    IncomingPostWait = 0
                }
            };
        }

        GD.Print($"CueCommandExecutor:ActivateSequenceFromAsync - {head.Name}: {chain.Count} cue(s)" +
                 (startAtTimelineSeconds.HasValue ? $", startAt={startAtTimelineSeconds.Value:F3}s" : string.Empty));

        // Create all active rows, build UI in sequence order (so the list matches occurrence),
        // then wire events and start playback. Reuse OSC Load standby for the head when present.
        var actives = new List<ActiveCue>(chain.Count);
        for (int mi = 0; mi < chain.Count; mi++)
        {
            var member = chain[mi];
            ActiveCue active = null;
            if (mi == 0)
            {
                active = FindActiveCuesById(member.Cue.Id)
                    .FirstOrDefault(a => a != null && GodotObject.IsInstanceValid(a) && a.IsStandbyPreloaded);
                if (active != null)
                {
                    active.AttachChainMember(member);
                    GD.Print(
                        $"CueCommandExecutor:ActivateSequenceFromAsync - Reusing standby preload for \"{member.Cue.Name}\"");
                }
            }

            if (active == null)
            {
                active = new ActiveCue(
                    member.Cue,
                    _activeCueList,
                    _mediaEngine,
                    _audioDevices,
                    _globalSignals,
                    member);
                _activeCues.Add(active);
                active.Completed += () => _activeCues.Remove(active);
            }

            actives.Add(active);
        }

        // Control GO fade-in applies to the head instance only (not continue/follow peers).
        if (controlGoFadeIn.HasValue && controlGoFadeIn.Value > 1e-9 && actives.Count > 0)
            actives[0].SetControlFadeInDuration(controlGoFadeIn.Value);

        // Timeline playhead: queue body position before StartAsync (skips pre-wait when landing in content,
        // seeks media + filters nested children that have already ended).
        if (startAtTimelineSeconds.HasValue && actives.Count > 0)
            actives[0].QueueStartAtBodyTime(Math.Max(0.0, startAtTimelineSeconds.Value));

        // Synchronous UI insert in chain order (avoids async race reordering the VBox).
        // Standby preload already prepared UI — PrepareUiInOrder is a no-op when done.
        foreach (var active in actives)
            active.PrepareUiInOrder();

        // Link chain + arming rules from each cue's Follow mode.
        for (int i = 0; i < actives.Count; i++)
        {
            if (i + 1 < actives.Count)
                actives[i].NextInChain = actives[i + 1];

            var memberCue = chain[i].Cue;
            if (i + 1 >= actives.Count) continue;

            var next = actives[i + 1];
            var current = actives[i];

            if (memberCue.Follow == FollowType.Continue)
            {
                // Continue: arm next when this cue's content phase starts (after its pre-wait).
                double postWait = Math.Max(0.0, memberCue.PostWait);
                current.ContentPhaseStarted += () =>
                {
                    if (!GodotObject.IsInstanceValid(next)) return;
                    next.ArmIncoming(FollowType.Continue, postWait);
                };
            }
            else if (memberCue.Follow == FollowType.Follow)
            {
                // Follow: arm next when this cue's content actually completes (seek-aware).
                double postWait = Math.Max(0.0, memberCue.PostWait);
                current.ContentCompleted += () =>
                {
                    if (!GodotObject.IsInstanceValid(next)) return;
                    next.ArmIncoming(FollowType.Follow, postWait);
                };
            }
        }

        // Non-head chain members stay pending until armed — start them without waiting.
        for (int i = 1; i < actives.Count; i++)
            _ = StartActiveSafe(actives[i]);

        // Await head until pre-wait is running or content has triggered so a following
        // control action (Start Now, Pause, …) can see a live instance.
        if (actives.Count > 0)
            await StartActiveSafe(actives[0]);
    }

    /// <summary>
    /// Starts a single cue (and its sequence chain).
    /// </summary>
    public void ActivateCue(Cue cue)
    {
        if (cue == null) return;
        ActivateSequenceFrom(cue);
    }

    /// <summary>
    /// Applies a control-component action to the target cue (by id).
    /// </summary>
    /// <param name="action">GO, Pause, Stop, Resume, or Start Now.</param>
    /// <param name="targetCueId">Id of the cue to control.</param>
    /// <param name="stopFadeDuration">
    /// Optional fade-out seconds for Stop. When null, uses session <see cref="Settings.StopFadeDuration"/>.
    /// When 0, stops immediately.
    /// </param>
    /// <param name="goFadeInDuration">
    /// Optional fade-in seconds for GO. When null or 0, target component fade-ins are used as-is.
    /// </param>
    /// <summary>
    /// Applies a full control component (including Fade property animation), awaiting completion.
    /// </summary>
    /// <param name="control">Control component to execute.</param>
    /// <param name="sourceCueId">Owning cue id (self-target guard for non-Fade actions).</param>
    /// <param name="sessionStopFadeDuration">Session default stop fade for Stop actions.</param>
    public async Task ApplyControlComponentAsync(
        ControlComponent control,
        int sourceCueId = -1,
        float sessionStopFadeDuration = 0f)
    {
        if (control == null)
        {
            GD.PrintErr("CueCommandExecutor:ApplyControlComponentAsync - control is null");
            return;
        }

        // Translate Layer targets a canvas layer, not a cue.
        if (control.Action == ControlAction.TranslateLayer)
        {
            await ApplyTranslateLayerAsync(control);
            return;
        }

        int targetCueId = control.TargetCueId;
        if (targetCueId < 0)
        {
            GD.Print("CueCommandExecutor:ApplyControlComponentAsync - Invalid target cue id");
            return;
        }

        double? stopFade = control.Action == ControlAction.Stop
            ? control.ResolveStopFadeDuration(sessionStopFadeDuration)
            : null;
        double? goFadeIn = control.Action == ControlAction.Go
            ? Math.Max(0.0, control.GoFadeInDuration)
            : null;

        switch (control.Action)
        {
            case ControlAction.Go:
            {
                var cue = CueList.FetchCueFromId(targetCueId);
                if (cue == null)
                {
                    _globalSignals?.EmitSignal(nameof(GlobalSignals.Log),
                        $"Control GO: cue id {targetCueId} not found", (int)LogType.Warning);
                    return;
                }

                if (!cue.Armed)
                {
                    GD.Print($"CueCommandExecutor:ApplyControlComponentAsync - Target cue {cue.Name} is disarmed; skipping GO");
                    _globalSignals?.EmitSignal(nameof(GlobalSignals.Log),
                        $"Control GO skipped: \"{cue.Name}\" is disarmed", (int)LogType.Info);
                    return;
                }

                if (RejectIfGoDisabled("GO"))
                    return;
                ApplyDoubleGoProtection();
                await ActivateSequenceFromAsync(cue, goFadeIn);
                break;
            }

            case ControlAction.Pause:
            {
                var matches = FindActiveCuesById(targetCueId).ToList();
                if (matches.Count == 0)
                {
                    GD.Print($"CueCommandExecutor:ApplyControlComponentAsync - No playing instance of cue id {targetCueId} to pause");
                    return;
                }

                foreach (var active in matches)
                    active.RequestPause();
                break;
            }

            case ControlAction.Stop:
            {
                var matches = FindActiveCuesById(targetCueId).ToList();
                if (matches.Count == 0)
                {
                    GD.Print($"CueCommandExecutor:ApplyControlComponentAsync - No playing instance of cue id {targetCueId} to stop");
                    return;
                }

                foreach (var active in matches)
                    active.StopAll(propagateToChildren: true, fadeDurationOverride: stopFade);
                break;
            }

            case ControlAction.Resume:
            {
                var matches = FindActiveCuesById(targetCueId).ToList();
                if (matches.Count == 0)
                {
                    GD.Print($"CueCommandExecutor:ApplyControlComponentAsync - No playing instance of cue id {targetCueId} to resume");
                    return;
                }

                foreach (var active in matches)
                    active.RequestResume();
                break;
            }

            case ControlAction.StartNow:
            {
                var matches = FindActiveCuesById(targetCueId).ToList();
                if (matches.Count == 0)
                {
                    GD.Print($"CueCommandExecutor:ApplyControlComponentAsync - No waiting instance of cue id {targetCueId} for Start Now");
                    return;
                }

                foreach (var active in matches)
                    active.RequestStartNow();
                break;
            }

            case ControlAction.Fade:
                await ApplyPropertyFadeAsync(control);
                break;

            case ControlAction.Seek:
            {
                var matches = FindActiveCuesById(targetCueId).ToList();
                if (matches.Count == 0)
                {
                    GD.Print($"CueCommandExecutor:ApplyControlComponentAsync - No playing instance of cue id {targetCueId} to seek");
                    _globalSignals?.EmitSignal(nameof(GlobalSignals.Log),
                        $"Control Seek: no playing instance of cue id {targetCueId}", (int)LogType.Warning);
                    return;
                }

                bool relative = control.SeekMode == ControlFadeMode.Relative;
                foreach (var active in matches)
                    active.RequestSeek(control.SeekTimeSeconds, relative);
                break;
            }

            default:
                GD.PrintErr($"CueCommandExecutor:ApplyControlComponentAsync - Unknown action {control.Action}");
                break;
        }
    }

    /// <summary>
    /// Animates (or snaps) a canvas target layer's size and/or position.
    /// </summary>
    private async Task ApplyTranslateLayerAsync(ControlComponent control)
    {
        var displays = _globalData?.DisplaysManager;
        if (displays == null && Engine.GetMainLoop() is SceneTree st)
            displays = st.Root.GetNodeOrNull<DisplaysManager>("/root/DisplaysManager");

        if (displays == null)
        {
            GD.PrintErr("CueCommandExecutor:ApplyTranslateLayerAsync - DisplaysManager not found");
            return;
        }

        var layer = DisplaysManager.GetLayerById(control.TargetLayerId);
        if (layer == null)
        {
            _globalSignals?.EmitSignal(nameof(GlobalSignals.Log),
                $"Control Translate Layer: layer id {control.TargetLayerId} not found", (int)LogType.Warning);
            return;
        }

        if (layer.Locked)
        {
            _globalSignals?.EmitSignal(nameof(GlobalSignals.Log),
                $"Control Translate Layer: layer '{layer.LayerName}' is locked", (int)LogType.Warning);
            GD.Print(
                $"CueCommandExecutor:ApplyTranslateLayerAsync - Layer '{layer.LayerName}' (id {layer.LayerId}) is locked; skipping translate");
            return;
        }

        if (!control.TranslateSizeEnabled && !control.TranslatePositionEnabled)
        {
            GD.Print("CueCommandExecutor:ApplyTranslateLayerAsync - Neither size nor position enabled");
            return;
        }

        Vector2I startPos = layer.CanvasPosition;
        Vector2I startSize = layer.Size;
        Vector2I endPos = startPos;
        Vector2I endSize = startSize;

        bool relative = control.TranslateMode == ControlFadeMode.Relative;

        if (control.TranslatePositionEnabled)
        {
            endPos = relative
                ? new Vector2I(startPos.X + control.TranslatePosX, startPos.Y + control.TranslatePosY)
                : new Vector2I(control.TranslatePosX, control.TranslatePosY);
        }

        if (control.TranslateSizeEnabled)
        {
            endSize = relative
                ? new Vector2I(startSize.X + control.TranslateSizeX, startSize.Y + control.TranslateSizeY)
                : new Vector2I(control.TranslateSizeX, control.TranslateSizeY);
            endSize = new Vector2I(Math.Max(1, endSize.X), Math.Max(1, endSize.Y));
        }

        double duration = Math.Max(0.0, control.TranslateDuration);
        GD.Print(
            $"CueCommandExecutor:ApplyTranslateLayerAsync - layer '{layer.LayerName}' mode={control.TranslateMode} " +
            $"dur={duration:0.###}s pos {startPos}→{endPos} size {startSize}→{endSize}");

        if (duration <= 1e-9)
        {
            displays.ApplyLayerGeometryLive(
                layer.LayerId,
                control.TranslatePositionEnabled ? endPos : null,
                control.TranslateSizeEnabled ? endSize : null);
            return;
        }

        var timer = Stopwatch.StartNew();
        while (timer.Elapsed.TotalSeconds < duration)
        {
            float t = (float)Math.Clamp(timer.Elapsed.TotalSeconds / duration, 0.0, 1.0);
            Vector2I pos = new Vector2I(
                Mathf.RoundToInt(Mathf.Lerp(startPos.X, endPos.X, t)),
                Mathf.RoundToInt(Mathf.Lerp(startPos.Y, endPos.Y, t)));
            Vector2I size = new Vector2I(
                Math.Max(1, Mathf.RoundToInt(Mathf.Lerp(startSize.X, endSize.X, t))),
                Math.Max(1, Mathf.RoundToInt(Mathf.Lerp(startSize.Y, endSize.Y, t))));

            displays.ApplyLayerGeometryLive(
                layer.LayerId,
                control.TranslatePositionEnabled ? pos : null,
                control.TranslateSizeEnabled ? size : null);
            await Task.Delay(16);
        }

        displays.ApplyLayerGeometryLive(
            layer.LayerId,
            control.TranslatePositionEnabled ? endPos : null,
            control.TranslateSizeEnabled ? endSize : null);
    }

    /// <summary>
    /// Fades a single property on <b>active playback only</b> (does not mutate stored cue components).
    /// Requires a currently playing instance of the target cue.
    /// </summary>
    private async Task ApplyPropertyFadeAsync(ControlComponent control)
    {
        var cue = CueList.FetchCueFromId(control.TargetCueId);
        if (cue == null)
        {
            _globalSignals?.EmitSignal(nameof(GlobalSignals.Log),
                $"Control Fade: cue id {control.TargetCueId} not found", (int)LogType.Warning);
            return;
        }

        var activeMatches = FindActiveCuesById(control.TargetCueId).ToList();
        if (activeMatches.Count == 0)
        {
            _globalSignals?.EmitSignal(nameof(GlobalSignals.Log),
                $"Control Fade: no playing instance of \"{cue.Name}\"", (int)LogType.Warning);
            GD.Print(
                $"CueCommandExecutor:ApplyPropertyFadeAsync - No active playback for \"{cue.Name}\"");
            return;
        }

        var audioPlaybacks = new List<ActiveAudioPlayback>();
        var videoPlaybacks = new List<ActiveVideoPlayback>();
        foreach (var active in activeMatches)
        {
            if (active == null || !GodotObject.IsInstanceValid(active)) continue;
            audioPlaybacks.AddRange(active.EnumerateAudioPlaybacks());
            videoPlaybacks.AddRange(active.EnumerateVideoPlaybacks());
        }

        double duration = Math.Max(0.0, control.PropertyFadeDuration);
        GD.Print(
            $"CueCommandExecutor:ApplyPropertyFadeAsync - \"{cue.Name}\" " +
            $"prop={control.FadeProperty} mode={control.FadeMode} dur={duration:0.###}s " +
            $"audioPb={audioPlaybacks.Count} videoPb={videoPlaybacks.Count}");

        switch (control.FadeProperty)
        {
            case ControlFadeProperty.Volume:
                await FadeRuntimeVolumeAsync(control, audioPlaybacks, videoPlaybacks, duration);
                break;

            case ControlFadeProperty.Opacity:
                await FadeRuntimeOpacityAsync(control, videoPlaybacks, duration);
                break;

            case ControlFadeProperty.Pan:
                await FadeRuntimePanAsync(control, audioPlaybacks, videoPlaybacks, duration);
                break;

            case ControlFadeProperty.RoutingMatrix:
                await FadeRuntimeMatrixAsync(control, audioPlaybacks, videoPlaybacks, duration);
                break;

            default:
                GD.PrintErr(
                    $"CueCommandExecutor:ApplyPropertyFadeAsync - Unknown property {control.FadeProperty}");
                break;
        }
    }

    /// <summary>
    /// Interpolates a single float over <paramref name="duration"/> seconds, applying <paramref name="apply"/> each tick.
    /// </summary>
    private static async Task RunScalarFadeAsync(
        double duration,
        (float start, float end) range,
        Action<float> apply)
    {
        if (duration <= 1e-9)
        {
            apply(range.end);
            return;
        }

        var timer = Stopwatch.StartNew();
        while (timer.Elapsed.TotalSeconds < duration)
        {
            float t = (float)Math.Clamp(timer.Elapsed.TotalSeconds / duration, 0.0, 1.0);
            apply(Mathf.Lerp(range.start, range.end, t));
            await Task.Delay(16);
        }

        apply(range.end);
    }

    private static async Task FadeRuntimeVolumeAsync(
        ControlComponent control,
        List<ActiveAudioPlayback> audioPlaybacks,
        List<ActiveVideoPlayback> videoPlaybacks,
        double duration)
    {
        // Capture per-playback start → end so each instance fades from its own current level.
        var audioTargets = new List<(ActiveAudioPlayback pb, float start, float end)>();
        foreach (var pb in audioPlaybacks)
        {
            if (pb == null || !GodotObject.IsInstanceValid(pb)) continue;
            float start = pb.EffectiveLevelLinear;
            audioTargets.Add((pb, start, ResolveFadeAudioLinear(start, control)));
        }

        var videoTargets = new List<(ActiveVideoPlayback pb, float start, float end)>();
        foreach (var pb in videoPlaybacks)
        {
            if (pb == null || !GodotObject.IsInstanceValid(pb)) continue;
            // Only video playings that actually mix embedded audio.
            if (pb.SourceChannels <= 0) continue;
            float start = pb.EffectiveLevelLinear;
            videoTargets.Add((pb, start, ResolveFadeAudioLinear(start, control)));
        }

        if (audioTargets.Count == 0 && videoTargets.Count == 0)
        {
            GD.Print("CueCommandExecutor:FadeRuntimeVolumeAsync - No audio streams on active playback");
            return;
        }

        await RunMultiTargetFadeAsync(duration, t =>
        {
            foreach (var (pb, start, end) in audioTargets)
            {
                if (pb != null && GodotObject.IsInstanceValid(pb))
                    pb.SetRuntimeLevelLinear(Mathf.Lerp(start, end, t));
            }
            foreach (var (pb, start, end) in videoTargets)
            {
                if (pb != null && GodotObject.IsInstanceValid(pb))
                    pb.SetRuntimeLevelLinear(Mathf.Lerp(start, end, t));
            }
        });
    }

    private static async Task FadeRuntimeOpacityAsync(
        ControlComponent control,
        List<ActiveVideoPlayback> videoPlaybacks,
        double duration)
    {
        var targets = new List<(ActiveVideoPlayback pb, float start, float end)>();
        foreach (var pb in videoPlaybacks)
        {
            if (pb == null || !GodotObject.IsInstanceValid(pb)) continue;
            float start = pb.EffectiveOpacity;
            float startPct = start * 100f;
            float endPct = control.FadeMode == ControlFadeMode.Absolute
                ? control.FadeOpacityPercent
                : startPct + control.FadeOpacityPercent;
            endPct = Mathf.Clamp(endPct, 0f, 100f);
            targets.Add((pb, start, endPct / 100f));
        }

        if (targets.Count == 0)
        {
            GD.Print("CueCommandExecutor:FadeRuntimeOpacityAsync - No video playback to fade");
            return;
        }

        await RunMultiTargetFadeAsync(duration, t =>
        {
            foreach (var (pb, start, end) in targets)
            {
                if (pb != null && GodotObject.IsInstanceValid(pb))
                    pb.SetRuntimeOpacity(Mathf.Lerp(start, end, t));
            }
        });
    }

    private static async Task FadeRuntimePanAsync(
        ControlComponent control,
        List<ActiveAudioPlayback> audioPlaybacks,
        List<ActiveVideoPlayback> videoPlaybacks,
        double duration)
    {
        var audioTargets = new List<(ActiveAudioPlayback pb, float start, float end)>();
        foreach (var pb in audioPlaybacks)
        {
            if (pb == null || !GodotObject.IsInstanceValid(pb)) continue;
            if (pb.SourceChannels != 2) continue;
            float start = pb.EffectivePan;
            float end = control.FadeMode == ControlFadeMode.Absolute
                ? control.FadePan
                : start + control.FadePan;
            audioTargets.Add((pb, start, Mathf.Clamp(end, -1f, 1f)));
        }

        var videoTargets = new List<(ActiveVideoPlayback pb, float start, float end)>();
        foreach (var pb in videoPlaybacks)
        {
            if (pb == null || !GodotObject.IsInstanceValid(pb)) continue;
            if (pb.SourceChannels != 2) continue;
            float start = pb.EffectivePan;
            float end = control.FadeMode == ControlFadeMode.Absolute
                ? control.FadePan
                : start + control.FadePan;
            videoTargets.Add((pb, start, Mathf.Clamp(end, -1f, 1f)));
        }

        if (audioTargets.Count == 0 && videoTargets.Count == 0)
        {
            GD.Print("CueCommandExecutor:FadeRuntimePanAsync - No stereo streams on active playback");
            return;
        }

        await RunMultiTargetFadeAsync(duration, t =>
        {
            foreach (var (pb, start, end) in audioTargets)
            {
                if (pb != null && GodotObject.IsInstanceValid(pb))
                    pb.SetRuntimePan(Mathf.Lerp(start, end, t));
            }
            foreach (var (pb, start, end) in videoTargets)
            {
                if (pb != null && GodotObject.IsInstanceValid(pb))
                    pb.SetRuntimePan(Mathf.Lerp(start, end, t));
            }
        });
    }

    private static async Task FadeRuntimeMatrixAsync(
        ControlComponent control,
        List<ActiveAudioPlayback> audioPlaybacks,
        List<ActiveVideoPlayback> videoPlaybacks,
        double duration)
    {
        // Multi-cell: each stored target fades in parallel on every matching playback.
        var cellTargets = control.FadeMatrixCellTargets;
        if (cellTargets == null || cellTargets.Count == 0)
        {
            // Legacy fallback: single cell fields.
            if (control.FadeProperty == ControlFadeProperty.RoutingMatrix)
            {
                cellTargets = new Dictionary<int, float>
                {
                    {
                        ControlComponent.PackMatrixCellKey(
                            control.FadeMatrixInputIndex,
                            control.FadeMatrixOutputIndex),
                        control.FadeAudioDb
                    }
                };
            }
            else
            {
                GD.Print("CueCommandExecutor:FadeRuntimeMatrixAsync - No matrix cells targeted");
                return;
            }
        }

        // (playback, in, out, start, end) for audio and video separately.
        var audioTargets = new List<(ActiveAudioPlayback pb, int inIdx, int outIdx, float start, float end)>();
        var videoTargets = new List<(ActiveVideoPlayback pb, int inIdx, int outIdx, float start, float end)>();

        foreach (var kvp in cellTargets)
        {
            ControlComponent.UnpackMatrixCellKey(kvp.Key, out int inIdx, out int outIdx);
            float targetDb = kvp.Value;

            foreach (var pb in audioPlaybacks)
            {
                if (pb == null || !GodotObject.IsInstanceValid(pb)) continue;
                if (!pb.TryGetMatrixCell(inIdx, outIdx, out float start)) continue;
                audioTargets.Add((pb, inIdx, outIdx, start, ResolveFadeAudioLinear(start, targetDb, control.FadeMode)));
            }

            foreach (var pb in videoPlaybacks)
            {
                if (pb == null || !GodotObject.IsInstanceValid(pb)) continue;
                if (!pb.TryGetMatrixCell(inIdx, outIdx, out float start)) continue;
                videoTargets.Add((pb, inIdx, outIdx, start, ResolveFadeAudioLinear(start, targetDb, control.FadeMode)));
            }
        }

        if (audioTargets.Count == 0 && videoTargets.Count == 0)
        {
            GD.Print(
                $"CueCommandExecutor:FadeRuntimeMatrixAsync - No valid matrix cells " +
                $"(targets={cellTargets.Count})");
            return;
        }

        GD.Print(
            $"CueCommandExecutor:FadeRuntimeMatrixAsync - Fading {cellTargets.Count} cell(s) " +
            $"across {audioTargets.Count} audio + {videoTargets.Count} video cell-instances");

        await RunMultiTargetFadeAsync(duration, t =>
        {
            foreach (var (pb, inIdx, outIdx, start, end) in audioTargets)
            {
                if (pb != null && GodotObject.IsInstanceValid(pb))
                    pb.SetRuntimeMatrixCell(inIdx, outIdx, Mathf.Lerp(start, end, t));
            }
            foreach (var (pb, inIdx, outIdx, start, end) in videoTargets)
            {
                if (pb != null && GodotObject.IsInstanceValid(pb))
                    pb.SetRuntimeMatrixCell(inIdx, outIdx, Mathf.Lerp(start, end, t));
            }
        });
    }

    /// <summary>
    /// Runs a multi-target fade: <paramref name="apply"/> receives t in 0…1 each tick.
    /// </summary>
    private static async Task RunMultiTargetFadeAsync(double duration, Action<float> apply)
    {
        if (duration <= 1e-9)
        {
            apply(1f);
            return;
        }

        var timer = Stopwatch.StartNew();
        while (timer.Elapsed.TotalSeconds < duration)
        {
            float t = (float)Math.Clamp(timer.Elapsed.TotalSeconds / duration, 0.0, 1.0);
            apply(t);
            await Task.Delay(16);
        }

        apply(1f);
    }

    /// <summary>
    /// Computes end linear volume for absolute/relative audio fade from a start linear level
    /// using the control's volume <see cref="ControlComponent.FadeAudioDb"/>.
    /// </summary>
    private static float ResolveFadeAudioLinear(float startLinear, ControlComponent control) =>
        ResolveFadeAudioLinear(startLinear, control.FadeAudioDb, control.FadeMode);

    /// <summary>
    /// Computes end linear volume for absolute/relative fade from a start linear level and target/delta dB.
    /// </summary>
    private static float ResolveFadeAudioLinear(float startLinear, float targetOrDeltaDb, ControlFadeMode mode)
    {
        // Start may already include digital gain (above unity linear).
        float startDb = UiUtilities.LinearToDb(
            Cue2.Media.Audio.AudioMixMatrix.ClampComponentGainLinear(startLinear));
        float endDb = mode == ControlFadeMode.Absolute
            ? targetOrDeltaDb
            : startDb + targetOrDeltaDb;
        // Absolute and relative ends share component gain range (−60…+12 dB).
        endDb = Mathf.Clamp(endDb, UiUtilities.MinVolumeDb, UiUtilities.MaxComponentGainDb);
        return UiUtilities.DbToLinear(endDb);
    }

    /// <summary>
    /// Fire-and-forget control action (legacy/manual callers).
    /// </summary>
    public void ApplyControlAction(
        ControlAction action,
        int targetCueId,
        double? stopFadeDuration = null,
        double? goFadeInDuration = null)
    {
        var stub = new ControlComponent
        {
            Action = action,
            TargetCueId = targetCueId,
            StopFadeUsesSessionDefault = !stopFadeDuration.HasValue,
            StopFadeDuration = stopFadeDuration ?? 0,
            GoFadeInDuration = goFadeInDuration ?? 0
        };
        if (stopFadeDuration.HasValue)
        {
            stub.StopFadeUsesSessionDefault = false;
            stub.StopFadeDuration = stopFadeDuration.Value;
        }

        _ = ApplyControlComponentAsync(stub, -1, _globalData?.Settings?.StopFadeDuration ?? 0f);
    }

    /// <summary>
    /// Finds all live <see cref="ActiveCue"/> instances (including nested children) for a cue id.
    /// </summary>
    /// <param name="cueId">Cue identity to match.</param>
    /// <returns>Matching active instances (may be empty).</returns>
    private IEnumerable<ActiveCue> FindActiveCuesById(int cueId)
    {
        foreach (var root in _activeCues.ToList())
        {
            if (!GodotObject.IsInstanceValid(root)) continue;
            foreach (var active in root.EnumerateSelfAndDescendants())
            {
                if (active?.Cue != null && active.Cue.Id == cueId)
                    yield return active;
            }
        }
    }

    /// <summary>
    /// Stops live root <see cref="ActiveCue"/> instances whose cue id matches
    /// <paramref name="cueId"/> (used when <see cref="Cue.OnlyOneActiveInstance"/> is set).
    /// Nested group-child activations of other parents are also stopped when they share the id.
    /// </summary>
    /// <param name="cueId">Cue document id.</param>
    /// <param name="hardStop">When true, stop with zero fade so re-GO can start immediately.</param>
    private void StopExistingRootInstancesOfCue(int cueId, bool hardStop)
    {
        double? fade = hardStop ? 0.0 : null;
        var matches = FindActiveCuesById(cueId).ToList();
        if (matches.Count == 0)
            return;

        GD.Print(
            $"CueCommandExecutor:StopExistingRootInstancesOfCue - Stopping {matches.Count} instance(s) of cue id {cueId} " +
            $"(OnlyOneActiveInstance)");

        foreach (var active in matches)
        {
            if (active == null || !GodotObject.IsInstanceValid(active))
                continue;
            try
            {
                // Root list entries: full stop. Nested instances also receive StopAll.
                active.StopAll(propagateToChildren: true, fadeDurationOverride: fade);
            }
            catch (Exception ex)
            {
                GD.PrintErr(
                    $"CueCommandExecutor:StopExistingRootInstancesOfCue - {active.Cue?.Name}: {ex.Message}");
            }
        }
    }

    private async Task StartActiveSafe(ActiveCue activeCue)
    {
        try
        {
            await activeCue.StartAsync();
        }
        catch (Exception ex)
        {
            var name = activeCue?.Cue?.Name ?? "?";
            _globalSignals.EmitSignal(nameof(GlobalSignals.Log), $"Failed to execute cue {name}: {ex.Message}", 2);
            GD.PrintErr($"CueCommandExecutor:StartActiveSafe - {ex.Message}");
            try { activeCue?.Cleanup(); } catch { /* best-effort */ }
            if (activeCue != null)
                _activeCues.Remove(activeCue);
        }
    }
    
    private void CleanUp()
    {
        if (_globalSignals != null)
            _globalSignals.Go -= GoCommand;

        foreach (var activeCue in _activeCues.ToList())
        {
            try
            {
                if (GodotObject.IsInstanceValid(activeCue))
                    activeCue.Cleanup();
            }
            catch (Exception ex)
            {
                GD.PrintErr($"CueCommandExecutor:CleanUp - {ex.Message}");
            }
        }
        _activeCues.Clear();
    }
    
}
