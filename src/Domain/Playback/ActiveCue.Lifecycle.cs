// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using Cue2.Domain.Cues;
using Cue2.Services;
using Cue2.UI.Utilities;

namespace Cue2.Domain.Playback;

/// <summary>
/// Partial: Start/arm/content phase, chain pending, pre-wait entry, natural content completion.
/// </summary>
public partial class ActiveCue
{
    /// <summary>
    /// Starts the cue playback asynchronously, setting up UI and triggering components.
    /// </summary>
    /// <returns>
    /// A task that completes when this cue has entered pre-wait or started content
    /// (or shown as pending for non-head chain members). Awaited by control GO so
    /// subsequent control actions (e.g. Start Now) see a live active instance.
    /// </returns>
    public async Task StartAsync()
    {
        if (_isPlaying || _contentStarted) return;
        GD.Print($"ActiveCue:StartAsync - Starting: {_cue.Name} (chain={_chainMember != null}, incoming={_incomingMode})");

        // Leaving standby-load mode for real playback.
        _standbyPreload = false;
        
        // UI may already be prepared (chain order / OSC Load); otherwise build now.
        if (!_uiPrepared)
            PrepareUiInOrder();

        // GO chain member: show pending continue/follow + inactive pre-wait; head starts now.
        if (_chainMember != null)
        {
            if (_chainRunStarted) return;
            await BeginChainMemberRunAsync();
            return;
        }

        // Nested child under a parent, or solo / preloaded head: classic path.
        await StartPlaybackCoreAsync(includePreWait: true);
    }

    /// <summary>
    /// Prepares UI and opens media components without starting pre-wait or content (OSC Load).
    /// Safe to call repeatedly; shared with follow-chain pending preload.
    /// </summary>
    /// <returns>Task that completes when component setup finishes (or aborts if cleaned).</returns>
    public async Task RequestPreloadAsync()
    {
        if (_isCleaned || _contentStarted)
            return;

        if (!_uiPrepared)
            PrepareUiInOrder();

        // Mark standby so GO can reuse this instance instead of spawning a second bar.
        _standbyPreload = true;

        if (_cue != null && !_cue.Armed)
        {
            GD.Print($"ActiveCue:RequestPreloadAsync - {_cue.Name}: disarmed, skipping media open");
            return;
        }

        try
        {
            await EnsureComponentsSetupAsync();
            if (!_isCleaned && !_contentStarted)
            {
                GD.Print($"ActiveCue:RequestPreloadAsync - {_cue?.Name}: components ready (standby)");
            }
        }
        catch (Exception ex)
        {
            GD.PrintErr($"ActiveCue:RequestPreloadAsync - {_cue?.Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Attaches chain membership for a standby-preloaded instance that is about to GO as sequence head or peer.
    /// </summary>
    /// <param name="member">Chain planner member, or null for a solo activation.</param>
    public void AttachChainMember(CueChainMember member)
    {
        if (_isCleaned || _contentStarted)
            return;

        _chainMember = member;
        _incomingMode = member?.IncomingMode ?? FollowType.None;
        _incomingWaitDuration = member?.IncomingPostWait ?? 0.0;
        // Reset chain-run gate so BeginChainMemberRunAsync can run after a pure preload.
        _chainRunStarted = false;
    }

    /// <summary>
    /// Arms this cue from the previous chain member (continue at content-phase start, follow at complete).
    /// Starts post-wait lead-in, then this cue's pre-wait and content.
    /// Follow with zero post-wait starts content as soon as the predecessor completes (components
    /// are typically preloaded while this cue was pending).
    /// </summary>
    /// <param name="mode">Continue or Follow (for UI).</param>
    /// <param name="postWait">Previous cue's post-wait duration.</param>
    public void ArmIncoming(FollowType mode, double postWait)
    {
        if (_isCleaned || _suppressContentCompleted || _incomingArmed || _contentStarted)
            return;

        _incomingMode = mode;
        _incomingWaitDuration = Math.Max(0.0, postWait);
        _incomingArmed = true;

        GD.Print(
            $"ActiveCue:ArmIncoming - {_cue.Name} mode={mode} postWait={_incomingWaitDuration:F3} skipPre={_skipPreWait} componentsSetup={_componentsSetup}");

        // Zero post-wait: go straight into this cue's pre-wait / content.
        if (_incomingWaitDuration <= 1e-9)
        {
            HideSequencePanel();
            _ = StartPlaybackCoreAsync(includePreWait: true);
            return;
        }

        BeginIncomingPostWait();
    }

    /// <summary>
    /// Cancels this cue if it has not started content yet, and propagates to the rest of the chain.
    /// </summary>
    public void CancelPendingFromPredecessor()
    {
        if (_isCleaned) return;
        if (_contentStarted) return;

        _suppressContentCompleted = true;
        NextInChain?.CancelPendingFromPredecessor();
        Cleanup();
    }

    /// <summary>
    /// Optional pre-wait, then content phase (children + component trigger).
    /// Children start only when the content phase begins so parent pre-wait delays the group.
    /// </summary>
    private async Task StartPlaybackCoreAsync(bool includePreWait)
    {
        if (_isCleaned || _suppressContentCompleted) return;
        // Prevent double-entry (e.g. control Start Now racing StartAsync / GO).
        if (_contentStarted) return;
        _contentStarted = true;
        _incomingArmed = true;
        HideSequencePanel();

        // Disarmed: no content, children, or components. Still raise phase events so
        // continue/follow chains arm the next member without playing this cue.
        if (_cue != null && !_cue.Armed)
        {
            GD.Print($"ActiveCue:StartPlaybackCoreAsync - {_cue.Name}: disarmed, skipping playback");
            _isFinished = true;
            RaiseContentPhaseStarted();
            HandleNaturalContentFinished();
            return;
        }

        // Preload own media (or finish pending chain preload) before pre-wait / content.
        // Children still wait for content phase to spawn.
        await EnsureComponentsSetupAsync();

        bool doPreWait = includePreWait && !_skipPreWait && _cue.PreWait > 0;
        if (doPreWait)
        {
            EnsureTimelineStarted();
            PreWait();
            ApplyPendingTimelineSeekIfAny();
            return;
        }

        // Zero or skipped pre-wait: content origin sits at PreWait on the proportional bar
        // (skipped wait still occupies the pre-wait segment so scrub proportions stay stable).
        EnsureTimelineStarted();
        _preWaitSecondsHonored = Math.Max(0.0, _cue.PreWait);
        SetTimelineSeconds(_preWaitSecondsHonored);

        HidePreWaitPanel();
        await BeginContentPhaseAsync();
    }

    /// <summary>
    /// Starts (or restarts) content playback: nested children + component trigger.
    /// Body timeline clock continues from pre-wait (not reset).
    /// </summary>
    private async Task BeginContentPhaseAsync()
    {
        if (_isCleaned || _suppressContentCompleted) return;

        RaiseContentPhaseStarted();
        EnsureTimelineStarted();

        // _preWaitSecondsHonored set by FinishPreWait / skip path before we get here.
        _contentPlaybackActive = true;
        _isPlaying = true;

        // Play-from-playhead past end: finish without firing components or children.
        if (_pendingTimelineSeekSeconds.HasValue)
        {
            double playable = GetPlayableTimelineDuration();
            if (playable >= 0 && _pendingTimelineSeekSeconds.Value >= playable - 1e-4)
            {
                GD.Print(
                    $"ActiveCue:BeginContentPhaseAsync - {_cue?.Name}: start-at past end " +
                    $"({_pendingTimelineSeekSeconds.Value:F3}s ≥ {playable:F3}s) — finishing without play");
                _pendingTimelineSeekSeconds = null;
                _isFinished = true;
                HandleNaturalContentFinished();
                return;
            }
        }

        // Nested children share this content origin (child t=0 == parent content t=0).
        // StartChildCues reads pending body time to skip already-ended children and queue their start-at.
        if (_childActiveCues.Count == 0)
            StartChildCues();

        bool hasChildren = _childActiveCues.Count > 0;
        if (_activeComponentCount == 0)
        {
            _isFinished = true;
            if (!hasChildren)
            {
                _pendingTimelineSeekSeconds = null;
                HandleNaturalContentFinished();
                return;
            }

            // Group with only children: play until children complete.
            ApplyPendingTimelineSeekIfAny();
            UpdateHeadProgressUi();
            return;
        }

        if (!_componentsTriggered)
        {
            await TriggerComponents();
            _componentsTriggered = true;
        }
        else
        {
            // Re-entered content after rewind into pre-wait: media still loaded — unpause transport.
            if (!_isPaused)
            {
                foreach (var playback in _activeAudioComponents.Values)
                    playback.Resume();
                foreach (var playback in _activeVideoComponents.Values)
                    playback.Resume();
                foreach (var playback in _activeTextComponents.Values)
                    playback.Resume();
            }
        }

        // Seek media + still-live children to the queued playhead (must run after TriggerComponents).
        ApplyPendingTimelineSeekIfAny();
        EnsureAliveOrCleanup();
        UpdateHeadProgressUi();
    }

    /// <summary>
    /// Spawns nested active cues under this bar's child list and starts them.
    /// Skips children listed in <see cref="_finishedChildCueIds"/> (mid-timeline scrub-back).
    /// When a pending body seek is set (play-from-playhead), children that have already ended
    /// at that content time are not started; others are queued to start at that body time.
    /// </summary>
    private void StartChildCues()
    {
        if (_cue?.ChildCues == null || _cue.ChildCues.Count == 0) return;
        if (_activeCueBar == null || !IsInstanceValid(_activeCueBar)) return;

        var childCueList = _activeCueBar.GetNodeOrNull<VBoxContainer>("%ChildCuelist");
        if (childCueList == null)
        {
            GD.PrintErr($"ActiveCue:StartChildCues - ChildCuelist missing on {_cue.Name}");
            return;
        }

        // Parent content-local time for play-from-playhead (child body t == parent content t).
        double? contentTimeAtStart = null;
        if (_pendingTimelineSeekSeconds.HasValue)
        {
            double pre = Math.Max(0.0, _cue.PreWait);
            contentTimeAtStart = Math.Max(0.0, _pendingTimelineSeekSeconds.Value - pre);
        }

        foreach (var childId in _cue.ChildCues)
        {
            if (_finishedChildCueIds.Contains(childId))
                continue; // Already completed this run — do not resurrect.

            // Already live under this parent.
            if (_childActiveCues.Any(c => c != null && IsInstanceValid(c) && c.Cue?.Id == childId))
                continue;

            var child = CueList.FetchCueFromId(childId);
            if (child == null)
            {
                GD.PrintErr($"ActiveCue:StartChildCues - Child cue {childId} not found");
                _globalSignals.EmitSignal(nameof(GlobalSignals.Log),
                    $"Child cue {childId} not found for parent {_cue.Name}", 2);
                continue;
            }

            // Playhead past this child's end on the parent content clock → do not start it.
            if (contentTimeAtStart.HasValue &&
                IsCuePastEndAtBodyTime(child, contentTimeAtStart.Value))
            {
                _finishedChildCueIds.Add(childId);
                GD.Print(
                    $"ActiveCue:StartChildCues - Skipping child {child.Name} (ended before content t={contentTimeAtStart.Value:F3}s)");
                continue;
            }

            var activeCue = new ActiveCue(child, childCueList, _mediaEngine, _audioDevices, _globalSignals);
            _childActiveCues.Add(activeCue);
            activeCue.Completed += () => OnChildCompleted(activeCue);

            // Nested children share parent content origin as body t=0.
            if (contentTimeAtStart.HasValue)
                activeCue.QueueStartAtBodyTime(contentTimeAtStart.Value);

            _ = activeCue.StartAsync();
        }
    }

    /// <summary>
    /// True when <paramref name="bodyTime"/> is at/after the end of a cue's body span
    /// (pre-wait + duration). Instant (0-duration) cues are past end once time moves beyond their start.
    /// </summary>
    private static bool IsCuePastEndAtBodyTime(Cue cue, double bodyTime)
    {
        if (cue == null) return true;
        double pre = Math.Max(0.0, cue.PreWait);
        if (cue.Duration < 0)
            return false; // infinite / unknown — still active
        double dur = Math.Max(0.0, cue.Duration);
        if (dur <= 1e-9)
        {
            // Fire-and-forget / zero length: only "active" at the action instant.
            return bodyTime > pre + 1e-3;
        }
        return bodyTime >= pre + dur - 1e-4;
    }

    /// <summary>
    /// Applies a body-timeline seek deferred across pre-wait / content-phase start.
    /// </summary>
    private void ApplyPendingTimelineSeekIfAny()
    {
        if (!_pendingTimelineSeekSeconds.HasValue) return;
        double t = _pendingTimelineSeekSeconds.Value;
        _pendingTimelineSeekSeconds = null;
        ApplyCueTimelineSeek(t);
    }

    /// <summary>
    /// After trigger, if nothing is left running and no children remain, finish content (and sequence if needed).
    /// </summary>
    private void EnsureAliveOrCleanup()
    {
        if (_isCleaned) return;

        bool hasActive =
            _activeAudioComponents.Count > 0 ||
            _activeVideoComponents.Count > 0 ||
            _activeTextComponents.Count > 0 ||
            _activeOscComponents.Count > 0 ||
            _activeMidiOutputComponents.Count > 0 ||
            _activeCueLightComponents.Count > 0 ||
            _activeControlComponents.Count > 0;

        if (!hasActive)
        {
            _isFinished = true;
            if (_childActiveCues.Count == 0)
                HandleNaturalContentFinished();
        }
    }

    /// <summary>
    /// Starts this cue's media and non-control components, and runs control components.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Media / OSC / MIDI / cue-light / text always kick off in parallel and are never
    /// delayed by control components that happen to appear earlier in the component list.
    /// </para>
    /// <para>
    /// Controls run concurrently with that media batch. Order-sensitive transport actions
    /// (GO, Start Now, Pause, Stop, Resume, Seek) are awaited in list order so e.g. GO then
    /// Start Now still works. Long-running property animations (Fade, Translate Layer) are
    /// fire-and-forget so a 10s fade cannot hold media start or later transport actions.
    /// </para>
    /// </remarks>
    private async Task TriggerComponents()
    {
        var mediaAndInstant = new List<Task>();
        var controls = new List<ControlComponent>();

        foreach (var comp in _cue.Components)
        {
            if (comp is AudioComponent audioComp)
            {
                mediaAndInstant.Add(TriggerAudioComponent(audioComp));
            }
            else if (comp is VideoComponent videoComp)
            {
                mediaAndInstant.Add(TriggerVideoComponent(videoComp));
            }
            else if (comp is TextComponent textComp)
            {
                mediaAndInstant.Add(TriggerTextComponent(textComp));
            }
            else if (comp is CueLightComponent cueLightComp)
            {
                mediaAndInstant.Add(TriggerCueLightComponent(cueLightComp));
            }
            else if (comp is OscComponent oscComp)
            {
                mediaAndInstant.Add(TriggerOscComponent(oscComp));
            }
            else if (comp is MidiOutputComponent midiOutComp)
            {
                mediaAndInstant.Add(TriggerMidiOutputComponent(midiOutComp));
            }
            else if (comp is ControlComponent controlComp)
            {
                controls.Add(controlComp);
            }
        }

        Task mediaTask = mediaAndInstant.Count > 0
            ? Task.WhenAll(mediaAndInstant)
            : Task.CompletedTask;
        Task controlsTask = controls.Count > 0
            ? RunControlComponentsInOrderAsync(controls)
            : Task.CompletedTask;

        // Media must not wait for control fades; controls still run in their own sequence.
        await Task.WhenAll(mediaTask, controlsTask);
    }

    /// <summary>
    /// True when the control action's <see cref="ControlComponent.ExecuteAsync"/> may take
    /// many seconds (property animation). These must not gate media or later transport controls.
    /// </summary>
    /// <summary>
    /// True when the control may occupy wall-clock time (progress bar / non-blocking of media).
    /// Includes Stop/GO when they have a non-zero fade duration.
    /// </summary>
    private bool IsLongRunningControlAction(ControlComponent control)
    {
        if (control == null) return false;
        float sessionStop = _settings?.StopFadeDuration ?? 0f;
        // Fade defaults to 1s; Stop may use session default; GO only if fade-in &gt; 0.
        return control.GetContentDurationSeconds(sessionStop) > 1e-9;
    }

    /// <summary>
    /// Runs control components in list order. Transport actions are awaited; Fade / Translate
    /// Layer are started without waiting for their full duration.
    /// </summary>
    /// <param name="controls">Control components in cue component order.</param>
    private async Task RunControlComponentsInOrderAsync(List<ControlComponent> controls)
    {
        if (controls == null || controls.Count == 0)
            return;

        foreach (var controlComp in controls)
        {
            if (controlComp == null)
                continue;

            if (_isCleaned || _suppressContentCompleted)
                return;

            if (IsLongRunningControlAction(controlComp))
            {
                // Panel completes when the timed action finishes (see TriggerControlComponent finally).
                // Fire-and-forget so media / other controls are not gated by long fades.
                _ = TriggerControlComponent(controlComp);
            }
            else
            {
                await TriggerControlComponent(controlComp);
            }
        }
    }

    private async Task TriggerAudioComponent(AudioComponent audioComp)
    {
        PanelContainer panel = null;
        try
        {
            // Find specific playback for this audioComp
            panel = _componentToAudio.FirstOrDefault(kv => kv.Value == audioComp).Key;
            if (panel == null)
            {
                GD.PrintErr($"ActiveCue:TriggerAudioComponent - No playback found for {audioComp.AudioFile}");
                _globalSignals.EmitSignal(nameof(GlobalSignals.Log),
                    $"No playback for audio component in cue {_cue.Name}", 2);
                return;
            }
            var playback = _activeAudioComponents[panel];

            bool started = await _audioDevices.StartAudioPlayback(playback);
            if (!started || playback.DeviceStreams == null || playback.DeviceStreams.Count == 0)
            {
                GD.PrintErr($"ActiveCue:TriggerAudioComponent - Failed to start audio for {audioComp.AudioFile}");
                _globalSignals.EmitSignal(nameof(GlobalSignals.Log),
                    $"Failed to start audio in {_cue.Name}: no output streams (check patch/device assignment).", 2);
                // Clean() emits Completed → CompleteAudioByPanelId removes UI
                playback.Clean();
                return;
            }

            // Control GO fade-in override wins when set; otherwise component FadeInDuration.
            double fadeIn = _controlFadeInDuration ?? audioComp.FadeInDuration;
            await playback.PlayAsync(fadeIn);
            if (_isCleaned || !IsInstanceValid(this))
                return;
            // Honour cue-level pause (e.g. global pause while this component was still setting up).
            if (_isPaused)
                playback.Pause();
            SyncPauseTransportUi();
        }
        catch (Exception ex)
        {
            GD.PrintErr($"ActiveCue:TriggerAudioComponent - Error triggering {audioComp.AudioFile}: {ex.Message}");
            _globalSignals.EmitSignal(nameof(GlobalSignals.Log),
                $"Trigger failed for audio in {_cue.Name}: {ex.Message}", 2);
            if (panel != null && _activeAudioComponents.TryGetValue(panel, out var failedPlayback))
            {
                try { failedPlayback.Clean(); } catch { /* best-effort */ }
            }
        }
    }

    private async Task TriggerVideoComponent(VideoComponent videoComp)
    {
        PanelContainer panel = null;
        try
        {
            // Find specific playback for this videoComp
            panel = _componentToVideo.FirstOrDefault(kv => kv.Value == videoComp).Key;
            if (panel == null)
            {
                GD.PrintErr($"ActiveCue:TriggerVideoComponent - No playback found for {videoComp.VideoFile}");
                _globalSignals.EmitSignal(nameof(GlobalSignals.Log),
                    $"No playback for video component in cue {_cue.Name}", 2);
                return;
            }
            var playback = _activeVideoComponents[panel];

            if (videoComp.UseAudio && videoComp.HasAudio && videoComp.HasAudioOutputAssigned)
            {
                bool started = await _audioDevices.StartAudioPlayback(playback);
                if (!started || !playback.HasBoundAudioStreams)
                {
                    _globalSignals.EmitSignal(nameof(GlobalSignals.Log),
                        $"Video audio in {_cue.Name} failed to bind; playing video silently.", 1);
                    // Ensure presentation uses wall clock (no stuck audio master).
                    playback.DisableEmbeddedAudio();
                }
            }
            else if (videoComp.UseAudio && videoComp.HasAudio && !videoComp.HasAudioOutputAssigned)
            {
                _globalSignals.EmitSignal(nameof(GlobalSignals.Log),
                    $"Video in {_cue.Name} has no audio output assigned; playing video silently.", 1);
            }

            // Control GO fade-in override wins when set; otherwise component FadeInDuration.
            // FadeInAsync starts playback then ramps volume/opacity; PlayAsync is the zero-fade path.
            double fadeIn = _controlFadeInDuration ?? videoComp.FadeInDuration;
            if (fadeIn > 1e-9)
                await playback.FadeInAsync(fadeIn);
            else
                await playback.PlayAsync();

            if (_isCleaned || !IsInstanceValid(this))
                return;
            if (_isPaused)
                playback.Pause();
            SyncPauseTransportUi();
        }
        catch (Exception ex)
        {
            GD.PrintErr($"ActiveCue:TriggerVideoComponent - Error triggering {videoComp.VideoFile}: {ex.Message}");
            _globalSignals.EmitSignal(nameof(GlobalSignals.Log),
                $"Trigger failed for video in {_cue.Name}: {ex.Message}", 2);
            if (panel != null && _activeVideoComponents.TryGetValue(panel, out var failedPlayback))
            {
                try { _ = failedPlayback.Stop(0); } catch { /* best-effort */ }
            }
        }
    }

    private async Task TriggerCueLightComponent(CueLightComponent comp)
    {
        try
        {
            await comp.ExecuteAsync();
        }
        catch (Exception ex)
        {
            GD.PrintErr($"ActiveCue:TriggerCueLightComponent - {ex.Message}");
            _globalSignals.EmitSignal(nameof(GlobalSignals.Log),
                $"Cue light trigger failed in {_cue.Name}: {ex.Message}", 2);
        }
        finally
        {
            var panel = _activeCueLightComponents.FirstOrDefault(kv => kv.Value == comp).Key;
            if (panel != null)
            {
                HandleCueLightComponentCompleted(panel);
            }
        }
    }

    private async Task TriggerOscComponent(OscComponent comp)
    {
        try
        {
            await comp.Execute();
        }
        catch (Exception ex)
        {
            GD.PrintErr($"ActiveCue:TriggerOscComponent - {ex.Message}");
            _globalSignals.EmitSignal(nameof(GlobalSignals.Log),
                $"OSC trigger failed in {_cue.Name}: {ex.Message}", 2);
        }
        finally
        {
            var panel = _activeOscComponents.FirstOrDefault(kv => kv.Value == comp).Key;
            if (panel != null)
            {
                HandleOscComponentCompleted(panel);
            }
        }
    }

    private async Task TriggerMidiOutputComponent(MidiOutputComponent comp)
    {
        try
        {
            await comp.Execute();
        }
        catch (Exception ex)
        {
            GD.PrintErr($"ActiveCue:TriggerMidiOutputComponent - {ex.Message}");
            _globalSignals.EmitSignal(nameof(GlobalSignals.Log),
                $"MIDI output failed in {_cue.Name}: {ex.Message}", 2);
        }
        finally
        {
            var panel = _activeMidiOutputComponents.FirstOrDefault(kv => kv.Value == comp).Key;
            if (panel != null)
                HandleMidiOutputComponentCompleted(panel);
        }
    }

    private async Task TriggerControlComponent(ControlComponent comp)
    {
        var panel = _activeControlComponents.FirstOrDefault(kv => kv.Value == comp).Key;
        double holdDuration = 0;
        ulong startMsec = 0;
        try
        {
            // Mark timed progress start so the active bar fills while ExecuteAsync runs.
            float sessionStop = _settings?.StopFadeDuration ?? 0f;
            holdDuration = comp?.GetContentDurationSeconds(sessionStop) ?? 0;
            if (panel != null && holdDuration > 1e-9)
            {
                startMsec = Time.GetTicksMsec();
                _controlTimedProgress[panel] = new ControlTimedProgress
                {
                    DurationSec = holdDuration,
                    StartMsec = startMsec,
                    Started = true
                };
            }

            GlobalData gd = null;
            if (Engine.GetMainLoop() is SceneTree st)
                gd = st.Root.GetNodeOrNull<GlobalData>("/root/GlobalData");

            float sessionStopFade = gd?.Settings?.StopFadeDuration ?? 0f;
            await comp.ExecuteAsync(gd?.CueCommandExecutor, _cue?.Id ?? -1, sessionStopFade);

            // Stop/GO kick off target fades without awaiting them — keep this row until
            // its configured duration elapses so the progress bar can complete.
            if (holdDuration > 1e-9 && startMsec > 0)
            {
                while (!_isCleaned && !_suppressContentCompleted)
                {
                    double elapsed = (Time.GetTicksMsec() - startMsec) / 1000.0;
                    if (elapsed >= holdDuration - 1e-3)
                        break;
                    await Task.Delay(16);
                }
            }
        }
        catch (Exception ex)
        {
            GD.PrintErr($"ActiveCue:TriggerControlComponent - {ex.Message}");
            _globalSignals.EmitSignal(nameof(GlobalSignals.Log),
                $"Control trigger failed in {_cue.Name}: {ex.Message}", 2);
        }
        finally
        {
            if (panel != null)
                HandleControlComponentCompleted(panel);
        }
    }


    private void PreWait()
    {
        GD.Print($"ActiveCue:PreWait - Pre-wait of {_cue.PreWait} detected");
        
        // Ensure timer exists (chain path may have skipped SetupTimers pre-wait if duration was 0 at setup).
        if (_preWaitTimer == null || !IsInstanceValid(_preWaitTimer))
        {
            _preWaitTimer = new Timer { WaitTime = _cue.PreWait, OneShot = true, IgnoreTimeScale = true };
            _activeCueBar.AddChild(_preWaitTimer);
        }
        else
        {
            _preWaitTimer.WaitTime = _cue.PreWait;
        }

        //Ui
        var preWaitNameLabel = _activeCueBar.GetNode<Label>("%PreWaitNameLabel");
        preWaitNameLabel.Text = _cue.Name;
        _preWaitTimerLabel.Text = UiUtilities.FormatTime(_cue.PreWait);
        if (_preWaitProgress != null)
            _preWaitProgress.Value = 100;

        _preWaitPause.Icon = _activeCueBar.GetThemeIcon("Pause", "AtlasIcons");
        _preWaitSkip.Icon = _activeCueBar.GetThemeIcon("Skip", "AtlasIcons");
        
        _preWaitPanel.Modulate = Colors.White;
        _preWaitPanel.Visible = true;

        _inPreWait = true;
        _preWaitFinished = false;
        _contentPlaybackActive = false;
        EnsureTimelineStarted();
        HookPreWaitUpdate(true);
        
        // Pause logic
        if (!_preWaitTimeoutHooked)
        {
            _preWaitTimer.Timeout += OnPreWaitTimerTimeout;
            _preWaitTimeoutHooked = true;
        }
        _preWaitTimer.Start();
        if (_isPaused)
            _preWaitTimer.SetPaused(true);

        UpdateHeadProgressUi();
    }

    private void PreWaitUpdate()
    {
        if (_preWaitTimer == null || !IsInstanceValid(_preWaitTimer)) return;
        _preWaitTimerLabel.Text = UiUtilities.FormatTime(_preWaitTimer.TimeLeft);
        var preWaitPercentage = (_preWaitTimer.TimeLeft / (float)_cue.PreWait) * 100;
        _preWaitProgress.Value = preWaitPercentage;
        // Keep head bar in sync during pre-wait (proportional pre-wait segment).
        UpdateHeadProgressUi();
    }

    private void TogglePreWaitPause()
    {
        if (_preWaitTimer == null || !IsInstanceValid(_preWaitTimer)) return;
        if (_preWaitTimer.Paused)
            PreWaitResume();
        else
            PreWaitPause();
    }

    private void PreWaitPause()
    {
        if (_preWaitTimer == null || !IsInstanceValid(_preWaitTimer)) return;
        _preWaitTimer.SetPaused(true);
        PauseTimelineClock();
        _preWaitPause.Icon = _activeCueBar.GetThemeIcon("Play", "AtlasIcons");
        UpdateHeadProgressUi();
    }

    private void PreWaitResume()
    {
        if (_preWaitTimer == null || !IsInstanceValid(_preWaitTimer)) return;
        if (_isPaused) return; // cue-level pause owns transport
        _preWaitTimer.SetPaused(false);
        ResumeTimelineClock();
        _preWaitPause.Icon = _activeCueBar.GetThemeIcon("Pause", "AtlasIcons");
        UpdateHeadProgressUi();
    }

    private void OnPreWaitTimerTimeout()
    {
        _ = FinishPreWaitAndStartContent();
    }

    /// <summary>
    /// Ends pre-wait (timer or skip) and starts content once. Safe against double-call / missing timer hooks.
    /// </summary>
    private async Task FinishPreWaitAndStartContent()
    {
        if (_preWaitFinished || _isCleaned || _suppressContentCompleted) return;
        if (!_inPreWait && _contentStarted) return;

        _preWaitFinished = true;
        _inPreWait = false;
        HookPreWaitUpdate(false);

        if (_preWaitTimer != null && IsInstanceValid(_preWaitTimer))
        {
            _preWaitTimer.Stop();
            if (_preWaitTimeoutHooked)
            {
                _preWaitTimer.Timeout -= OnPreWaitTimerTimeout;
                _preWaitTimeoutHooked = false;
            }
        }

        HidePreWaitPanel();

        // Content region starts after the pre-wait segment on the proportional head bar.
        _preWaitSecondsHonored = Math.Max(0.0, _cue?.PreWait ?? 0.0);
        // Keep playhead continuous: if wall clock drifted, snap to pre-wait boundary.
        if (GetCueTimelineSeconds() < _preWaitSecondsHonored - 1e-3)
            SetTimelineSeconds(_preWaitSecondsHonored);

        await BeginContentPhaseAsync();
    }

    private void HidePreWaitPanel()
    {
        if (_preWaitPanel != null && IsInstanceValid(_preWaitPanel))
        {
            _preWaitPanel.Visible = false;
            _preWaitPanel.Modulate = Colors.White;
        }
    }

    /// <summary>
    /// Pre-wait skip: only advances past pre-wait. Does not skip continue/follow.
    /// If pre-wait is not active yet, marks it skipped for when playback reaches that phase.
    /// </summary>
    private void OnPreWaitSkipPressed()
    {
        if (_isCleaned || _suppressContentCompleted) return;

        // Already past pre-wait / in content.
        if (_contentStarted && !_inPreWait)
            return;

        if (_inPreWait)
        {
            // Active pre-wait → end it now (content starts; continue/follow already finished).
            _ = FinishPreWaitAndStartContent();
            return;
        }

        // Not in pre-wait yet (still on continue/follow, or pending) → skip pre-wait when we get there.
        _skipPreWait = true;
        HidePreWaitPanel();
        GD.Print($"ActiveCue:OnPreWaitSkipPressed - {_cue.Name}: pre-wait will be skipped");
    }

    /// <summary>
    /// GO chain path: head starts immediately; other members wait to be armed (continue/follow rules).
    /// </summary>
    /// <returns>
    /// Completes when the head has entered pre-wait or started content, or when a non-head
    /// member has shown its pending UI.
    /// </returns>
    private async Task BeginChainMemberRunAsync()
    {
        if (_isCleaned || _chainRunStarted) return;
        _chainRunStarted = true;

        // Pre-wait strip: inactive until this cue is allowed to run its own pre-wait.
        ShowInactivePreWaitPreview();

        // Outgoing post-wait strip is not used — next cue shows continue/follow lead-in instead.
        if (_postWaitPanel != null && IsInstanceValid(_postWaitPanel))
            _postWaitPanel.Visible = false;

        bool isHead = _incomingMode == FollowType.None;
        if (isHead)
        {
            // Head of the GO: no incoming wait — go straight into pre-wait / content.
            _incomingArmed = true;
            HideSequencePanel();
            // Await so control GO can finish before a following Start Now / Pause / etc.
            await StartPlaybackCoreAsync(includePreWait: true);
            return;
        }

        // Pending: visible but not counting until predecessor arms us.
        ShowPendingIncomingUi();

        // Preload media while waiting so Follow can trigger with no open/decode gap.
        // (Continue also benefits — arm happens at content-phase start of the predecessor.)
        _ = PreloadComponentsWhilePendingAsync();
    }

    /// <summary>
    /// Opens decoders / builds component UI while this chain member is still pending arm.
    /// Safe: video Init keeps fade alpha at 0 until <see cref="TriggerComponents"/>.
    /// </summary>
    private async Task PreloadComponentsWhilePendingAsync()
    {
        if (_isCleaned || _contentStarted || _componentsSetup)
            return;
        if (_cue != null && !_cue.Armed)
            return;

        try
        {
            _pendingPreloadTask = EnsureComponentsSetupAsync();
            await _pendingPreloadTask;
            if (!_isCleaned && !_contentStarted)
                GD.Print($"ActiveCue:PreloadComponentsWhilePendingAsync - {_cue?.Name}: components ready for arm");
        }
        catch (Exception ex)
        {
            GD.PrintErr($"ActiveCue:PreloadComponentsWhilePendingAsync - {_cue?.Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Ensures <see cref="SetupComponents"/> has completed once (shared by preload and content start).
    /// </summary>
    private async Task EnsureComponentsSetupAsync()
    {
        if (_componentsSetup || _isCleaned)
            return;

        // Single-flight: first caller starts setup; concurrent callers await the same task.
        _pendingPreloadTask ??= SetupComponents();
        try
        {
            await _pendingPreloadTask;
            // Cleanup may have run while setup tasks were finishing — do not treat as ready.
            if (_isCleaned || !_componentsSetup)
            {
                if (!_componentsSetup)
                    _pendingPreloadTask = null;
            }
        }
        catch (Exception ex)
        {
            // Allow a retry on the next ensure if setup failed hard.
            if (!_componentsSetup)
                _pendingPreloadTask = null;
            GD.PrintErr($"ActiveCue:EnsureComponentsSetupAsync - {_cue?.Name}: {ex.Message}");
            throw;
        }
    }

    private void ShowInactivePreWaitPreview()
    {
        if (_preWaitPanel == null || !IsInstanceValid(_preWaitPanel)) return;
        if (_cue.PreWait <= 1e-9)
        {
            _preWaitPanel.Visible = false;
            return;
        }

        var preWaitNameLabel = _activeCueBar.GetNodeOrNull<Label>("%PreWaitNameLabel");
        if (preWaitNameLabel != null)
            preWaitNameLabel.Text = _cue.Name ?? string.Empty;
        if (_preWaitTimerLabel != null)
            _preWaitTimerLabel.Text = UiUtilities.FormatTime(_cue.PreWait);
        if (_preWaitProgress != null)
            _preWaitProgress.Value = 100;
        _preWaitPanel.Modulate = new Color(1f, 1f, 1f, 0.4f);
        _preWaitPanel.Visible = true;
        if (_preWaitPause != null)
            _preWaitPause.Icon = _activeCueBar.GetThemeIcon("Pause", "AtlasIcons");
        if (_preWaitSkip != null)
            _preWaitSkip.Icon = _activeCueBar.GetThemeIcon("Skip", "AtlasIcons");
    }

    private void ShowPendingIncomingUi()
    {
        if (_sequencePanel == null || !IsInstanceValid(_sequencePanel)) return;

        bool isFollow = _incomingMode == FollowType.Follow;
        if (_sequenceLabel != null)
            _sequenceLabel.Text = isFollow ? "Follow" : "Continue";
        if (_sequenceNameLabel != null)
        {
            _sequenceNameLabel.Text = isFollow
                ? "Waiting for previous to complete…"
                : "Waiting to continue…";
        }
        if (_sequenceTimerLabel != null)
        {
            _sequenceTimerLabel.Text = _incomingWaitDuration > 1e-9
                ? UiUtilities.FormatTime(_incomingWaitDuration)
                : "";
        }

        // Continue: no progress bar — label only. Follow: keep bar for post-wait once armed.
        if (_sequenceProgress != null)
        {
            _sequenceProgress.Value = 100;
            // Hide fill for continue (label-style); show for follow.
            _sequenceProgress.Modulate = isFollow ? Colors.White : new Color(1, 1, 1, 0.15f);
        }

        _sequencePanel.Modulate = new Color(1f, 1f, 1f, 0.45f);
        _sequencePanel.Visible = true;
        if (_sequencePause != null)
            _sequencePause.Icon = _activeCueBar.GetThemeIcon("Pause", "AtlasIcons");
        if (_sequenceSkip != null)
            _sequenceSkip.Icon = _activeCueBar.GetThemeIcon("Skip", "AtlasIcons");
    }

    /// <summary>
    /// Starts the post-wait after arm (continue label countdown or follow progress bar).
    /// </summary>
    private void BeginIncomingPostWait()
    {
        if (_isCleaned || _suppressContentCompleted) return;

        _inIncomingWait = true;
        bool isFollow = _incomingMode == FollowType.Follow;

        if (_sequencePanel != null && IsInstanceValid(_sequencePanel))
        {
            if (_sequenceLabel != null)
                _sequenceLabel.Text = isFollow ? "Follow" : "Continue";
            if (_sequenceNameLabel != null)
            {
                // Continue uses a text status instead of a progress bar.
                _sequenceNameLabel.Text = isFollow
                    ? (_cue.Name ?? "")
                    : $"Continuing after {UiUtilities.FormatTime(_incomingWaitDuration)}";
            }
            if (_sequenceTimerLabel != null)
                _sequenceTimerLabel.Text = UiUtilities.FormatTime(_incomingWaitDuration);
            if (_sequenceProgress != null)
            {
                _sequenceProgress.Value = 100;
                _sequenceProgress.Modulate = isFollow ? Colors.White : new Color(1, 1, 1, 0.12f);
            }
            _sequencePanel.Modulate = Colors.White;
            _sequencePanel.Visible = true;
        }

        _incomingWaitTimer = new Timer
        {
            WaitTime = _incomingWaitDuration,
            OneShot = true,
            IgnoreTimeScale = true,
            Autostart = false
        };
        _activeCueBar.AddChild(_incomingWaitTimer);
        _incomingWaitTimer.Timeout += OnIncomingPostWaitComplete;
        _incomingWaitTimeoutHooked = true;
        _incomingWaitTimer.Start();
        HookIncomingWaitUpdate(true);
    }

    private void IncomingWaitUpdate()
    {
        if (!_inIncomingWait || _incomingWaitTimer == null || !IsInstanceValid(_incomingWaitTimer))
            return;

        double left = _incomingWaitTimer.TimeLeft;
        bool isFollow = _incomingMode == FollowType.Follow;

        if (_sequenceTimerLabel != null)
            _sequenceTimerLabel.Text = UiUtilities.FormatTime(left);

        if (!isFollow && _sequenceNameLabel != null)
            _sequenceNameLabel.Text = UiLocalizer.Tf("Continuing after {0}", UiUtilities.FormatTime(left));

        if (isFollow && _sequenceProgress != null && _incomingWaitDuration > 1e-9)
            _sequenceProgress.Value = (left / _incomingWaitDuration) * 100.0;
    }

    private void OnIncomingPostWaitComplete()
    {
        if (!_inIncomingWait) return;
        HookIncomingWaitUpdate(false);
        FreeIncomingWaitTimer();
        _inIncomingWait = false;
        HideSequencePanel();

        if (_isCleaned || _suppressContentCompleted) return;
        // Proceed to this cue's pre-wait (unless pre-wait was already skipped).
        _ = StartPlaybackCoreAsync(includePreWait: true);
    }

    /// <summary>
    /// Continue/follow skip: abandon continue/follow entirely and play this cue now.
    /// Does not skip this cue's own pre-wait (use the pre-wait skip for that).
    /// </summary>
    private void OnSequenceSkipPressed()
    {
        if (_isCleaned || _suppressContentCompleted) return;
        if (_contentStarted) return;

        // Head has no continue/follow bar to skip, or we already moved past it into pre-wait alone.
        if (_incomingMode == FollowType.None) return;
        if (_incomingArmed && !_inIncomingWait) return;

        GD.Print($"ActiveCue:OnSequenceSkipPressed - {_cue.Name}: skipping continue/follow, playing cue now");

        // Cancel any active continue/follow countdown.
        if (_inIncomingWait)
        {
            HookIncomingWaitUpdate(false);
            if (_incomingWaitTimer != null && IsInstanceValid(_incomingWaitTimer))
                _incomingWaitTimer.Stop();
            FreeIncomingWaitTimer();
            _inIncomingWait = false;
        }

        // Independently started — ignore later ArmIncoming from the predecessor.
        _incomingArmed = true;
        HideSequencePanel();

        // Play this cue (own pre-wait still applies unless the user also skipped it).
        _ = StartPlaybackCoreAsync(includePreWait: true);
    }

    private void OnPostWaitSkipPressed()
    {
        // Outgoing post-wait bar unused in chain mode.
    }

    private void ToggleSchedulePause()
    {
        // Pause incoming post-wait and/or media.
        if (_inIncomingWait && _incomingWaitTimer != null && IsInstanceValid(_incomingWaitTimer))
        {
            if (_incomingWaitTimer.Paused)
            {
                _incomingWaitTimer.SetPaused(false);
                if (_sequencePause != null)
                    _sequencePause.Icon = _activeCueBar.GetThemeIcon("Pause", "AtlasIcons");
            }
            else
            {
                _incomingWaitTimer.SetPaused(true);
                if (_sequencePause != null)
                    _sequencePause.Icon = _activeCueBar.GetThemeIcon("Play", "AtlasIcons");
            }
            return;
        }

        if (_inPreWait)
            TogglePreWaitPause();
        else if (_contentStarted)
            TogglePauseAll();
    }

    private void HideSequencePanel()
    {
        if (_sequencePanel != null && IsInstanceValid(_sequencePanel))
        {
            _sequencePanel.Visible = false;
            _sequencePanel.Modulate = Colors.White;
        }
    }

    private void FreeIncomingWaitTimer()
    {
        if (_incomingWaitTimer == null) return;
        var timer = _incomingWaitTimer;
        _incomingWaitTimer = null;
        bool wasHooked = _incomingWaitTimeoutHooked;
        _incomingWaitTimeoutHooked = false;
        if (!IsInstanceValid(timer))
            return;
        timer.Stop();
        // Only disconnect if we still believe we are hooked (avoids Godot disconnect errors).
        if (wasHooked)
        {
            try { timer.Timeout -= OnIncomingPostWaitComplete; }
            catch { /* already disconnected */ }
        }
        timer.QueueFree();
    }

    private void HookIncomingWaitUpdate(bool hook)
    {
        if (_updateTimer == null || !IsInstanceValid(_updateTimer))
        {
            _incomingWaitUpdateHooked = false;
            return;
        }
        if (hook && !_incomingWaitUpdateHooked)
        {
            _updateTimer.Timeout += IncomingWaitUpdate;
            _incomingWaitUpdateHooked = true;
        }
        else if (!hook && _incomingWaitUpdateHooked)
        {
            _updateTimer.Timeout -= IncomingWaitUpdate;
            _incomingWaitUpdateHooked = false;
        }
    }

    private void HookPreWaitUpdate(bool hook)
    {
        if (_updateTimer == null || !IsInstanceValid(_updateTimer))
        {
            _preWaitUpdateHooked = false;
            return;
        }

        if (hook && !_preWaitUpdateHooked)
        {
            _updateTimer.Timeout += PreWaitUpdate;
            _preWaitUpdateHooked = true;
        }
        else if (!hook && _preWaitUpdateHooked)
        {
            _updateTimer.Timeout -= PreWaitUpdate;
            _preWaitUpdateHooked = false;
        }
    }

    private void RaiseContentPhaseStarted()
    {
        if (_contentPhaseStartedRaised || _suppressContentCompleted || _isCleaned)
            return;
        _contentPhaseStartedRaised = true;
        try
        {
            ContentPhaseStarted?.Invoke();
        }
        catch (Exception ex)
        {
            GD.PrintErr($"ActiveCue:RaiseContentPhaseStarted - {_cue?.Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Natural content end: raise ContentCompleted (follow arms next in real time) and cleanup.
    /// Follow handoff: arm next first while predecessor display may still hold its last frame.
    /// </summary>
    private void HandleNaturalContentFinished()
    {
        if (_isCleaned) return;

        // Arm follow/continue before freeing this bar so the next cue can start immediately.
        RaiseContentCompleted();
        ScheduleCleanup();
    }
}
