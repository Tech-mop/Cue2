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
/// Partial: Pause/resume/stop, completion, cleanup, missing-media helpers.
/// </summary>
public partial class ActiveCue
{
    
    
    private void TogglePauseAll()
    {
        if (_isPaused)
        {
            ResumeAll();
        }
        else
        {
            PauseAll();
        }
    }

    private void ResumeAll(bool propagateToChildren = true)
    {
        lock (_lock)
        {
            // _isPlaying may be false during edge cases; still allow resume when paused.
            if (!_isPaused) return;
            _isPaused = false;
            _isPlaying = true;
        }

        ResumeTimelineClock();
        if (_inPreWait)
            PreWaitResume();

        if (propagateToChildren)
        {
            foreach (var child in _childActiveCues.ToList())
                child.ResumeAll(true);
        }

        // Only resume media while content playback is active (not parked for pre-wait rewind).
        if (_contentPlaybackActive)
        {
            foreach (var playback in _activeAudioComponents.Values)
                playback.Resume();

            foreach (var playback in _activeVideoComponents.Values)
                playback.Resume();

            foreach (var playback in _activeTextComponents.Values)
                playback.Resume();
        }

        if (_updateTimer != null && IsInstanceValid(_updateTimer))
            _updateTimer.Start();

        // Keep head + component + nested transport icons in sync with playback state.
        SyncPauseTransportUi();
    }

    private void PauseAll(bool propagateToChildren = true)
    {
        _isPaused = true;
        PauseTimelineClock();
        if (_inPreWait)
            PreWaitPause();

        if (propagateToChildren)
        {
            foreach (var child in _childActiveCues.ToList())
                child.PauseAll(true);
        }

        foreach (var playback in _activeAudioComponents.Values)
            playback.Pause();

        foreach (var playback in _activeVideoComponents.Values)
            playback.Pause();

        foreach (var playback in _activeTextComponents.Values)
            playback.Pause();

        SyncPauseTransportUi();
    }

    /// <summary>
    /// Sets head and component pause/play icons from actual pause state.
    /// Call after any pause/resume path (per-cue, global, nested).
    /// </summary>
    private void SyncPauseTransportUi()
    {
        if (_isCleaned || _activeCueBar == null || !IsInstanceValid(_activeCueBar))
            return;

        // Head: Play icon means "currently paused / press to resume".
        if (_headPause != null && IsInstanceValid(_headPause))
        {
            _headPause.Icon = _activeCueBar.GetThemeIcon(
                _isPaused ? "Play" : "Pause", "AtlasIcons");
        }

        SyncComponentPauseIcons(_activeAudioComponents, p => p != null && p.IsPaused);
        SyncComponentPauseIcons(_activeVideoComponents, p => p != null && p.IsPaused);
        SyncComponentPauseIcons(_activeTextComponents, p => p != null && p.IsPaused);
    }

    private static void SyncComponentPauseIcons<T>(
        Dictionary<PanelContainer, T> map,
        Func<T, bool> isPaused)
    {
        foreach (var kv in map)
        {
            if (!IsInstanceValid(kv.Key) || kv.Value == null) continue;
            var btn = kv.Key.GetNodeOrNull<Button>("%ComponentPause");
            if (btn == null) continue;
            // Component may be paused on its own, or via cue-level pause (IsPaused true on playback).
            btn.Icon = kv.Key.GetThemeIcon(isPaused(kv.Value) ? "Play" : "Pause", "AtlasIcons");
        }
    }
    

    /// <summary>
    /// Stops all playback for this cue.
    /// First call: fade-out using <paramref name="fadeDurationOverride"/> or
    /// <see cref="Settings.StopFadeDuration"/> (0 = immediate).
    /// Second call while still fading: hard-stop immediately.
    /// </summary>
    /// <param name="propagateToChildren">Whether to stop child cues as well.</param>
    /// <param name="fadeDurationOverride">
    /// Optional fade seconds for this stop (e.g. from a control component).
    /// When null, uses the session stop-fade setting. When 0, stops immediately.
    /// </param>
    /// <remarks>
    /// Fire-and-forget entry for signals / void call sites. Prefer
    /// <see cref="StopAllAsync"/> when the caller can await.
    /// </remarks>
    public void StopAll(bool propagateToChildren = true, double? fadeDurationOverride = null)
    {
        TaskUtil.FireAndForget(
            StopAllAsync(propagateToChildren, fadeDurationOverride),
            "ActiveCue.StopAll");
    }

    /// <summary>
    /// Awaitable stop path — see <see cref="StopAll"/>.
    /// </summary>
    public async Task StopAllAsync(bool propagateToChildren = true, double? fadeDurationOverride = null)
    {
        try
        {
            bool hardStop;
            lock (_lock)
            {
                if (_isCleaned) return;

                // User/panic stop — do not arm continue/follow; cancel unstarted chain peers.
                _suppressContentCompleted = true;
                NextInChain?.CancelPendingFromPredecessor();

                // Waiting / paused / not yet playing content — tear down immediately.
                // Still stop nested children first so they do not outlive the parent bar.
                if (!_contentStarted || _inIncomingWait || _inPreWait || _isPaused)
                {
                    if (propagateToChildren)
                        StopChildCuesImmediate(fadeDurationOverride ?? 0.0);
                    Cleanup();
                    return;
                }

                // Second Stop while a fade is in progress → hard stop.
                hardStop = _isStopFading;
                _isStopFading = true;
            }

            // Stop child cues if propagating (same fade override).
            // Nested bars live under this cue's UI — do not free parent until children finish.
            if (propagateToChildren)
            {
                foreach (var child in _childActiveCues.ToList())
                {
                    if (child == null || !IsInstanceValid(child)) continue;
                    // Fire-and-forget children; parent waits on own media then TryCleanupAfterStop.
                    child.StopAll(true, fadeDurationOverride);
                }
            }

            // Override or session fade; second press or zero duration forces immediate stop.
            double baseFade = fadeDurationOverride ?? _settings.StopFadeDuration;
            double fadeDuration = hardStop ? 0.0 : Math.Max(0.0, baseFade);

            var tasks = new List<Task>();
            foreach (var audioComp in _activeAudioComponents.Values.ToList())
            {
                tasks.Add(audioComp.Stop(fadeDuration));
            }
            foreach (var videoComp in _activeVideoComponents.Values.ToList())
            {
                tasks.Add(videoComp.Stop(fadeDuration));
            }
            foreach (var textComp in _activeTextComponents.Values.ToList())
            {
                tasks.Add(textComp.Stop(fadeDuration));
            }

            // Instant components (OSC / MIDI / cue light / control) have no async stop — clear them now.
            foreach (var panel in _activeOscComponents.Keys.ToList())
            {
                HandleOscComponentCompleted(panel);
            }
            foreach (var panel in _activeMidiOutputComponents.Keys.ToList())
            {
                HandleMidiOutputComponentCompleted(panel);
            }
            foreach (var panel in _activeCueLightComponents.Keys.ToList())
            {
                HandleCueLightComponentCompleted(panel);
            }
            foreach (var panel in _activeControlComponents.Keys.ToList())
            {
                HandleControlComponentCompleted(panel);
            }

            // Group with no own media (or only instant comps): keep parent bar until children finish fade.
            if (tasks.Count == 0)
            {
                TryCleanupAfterStop();
                return;
            }

            await Task.WhenAll(tasks);
            if (_isCleaned || !IsInstanceValid(this))
                return;

            _isPlaying = false;

            // Own media finished stop-fade; still wait for nested children before freeing UI.
            TryCleanupAfterStop();
        }
        catch (Exception ex)
        {
            GD.PrintErr($"ActiveCue:StopAllAsync - {_cue?.Name}: {ex.Message}");
            try
            {
                if (!_isCleaned && IsInstanceValid(this))
                    TryCleanupAfterStop();
            }
            catch { /* best-effort */ }
        }
    }

    /// <summary>
    /// Hard-stops nested children without awaiting (used when parent tears down immediately).
    /// </summary>
    private void StopChildCuesImmediate(double fadeDuration)
    {
        foreach (var child in _childActiveCues.ToList())
        {
            if (child == null || !IsInstanceValid(child)) continue;
            try
            {
                child.StopAll(true, fadeDuration);
            }
            catch (Exception ex)
            {
                GD.PrintErr($"ActiveCue:StopChildCuesImmediate - {_cue?.Name}: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// After a stop, free this cue only when own media is gone and nested children have finished.
    /// Parent bars own the child UI tree — freeing early aborts child stop-fades.
    /// </summary>
    private void TryCleanupAfterStop()
    {
        if (_isCleaned) return;

        bool hasOwnMedia =
            _activeAudioComponents.Count > 0 ||
            _activeVideoComponents.Count > 0 ||
            _activeTextComponents.Count > 0;

        if (hasOwnMedia)
            return;

        if (_childActiveCues.Count > 0)
        {
            // Children still fading/playing — OnChildCompleted will call back when the last one ends.
            _isFinished = true;
            _isPlaying = false;
            GD.Print(
                $"ActiveCue:TryCleanupAfterStop - {_cue?.Name}: waiting for {_childActiveCues.Count} child cue(s) to finish stop");
            return;
        }

        Cleanup();
    }

    private void GlobalStopAll()
    {
        StopAll(false); // Don't propagate, as all cues receive the global signal
    }
    
    private void GlobalPauseAll()
    {
        // Every ActiveCue (including nested group children) receives this signal itself —
        // do not rely on parent propagation for globals.
        if (_inIncomingWait && _incomingWaitTimer != null && IsInstanceValid(_incomingWaitTimer))
        {
            _incomingWaitTimer.SetPaused(true);
            if (_sequencePause != null && _activeCueBar != null)
                _sequencePause.Icon = _activeCueBar.GetThemeIcon("Play", "AtlasIcons");
        }

        if (_inPreWait)
            PreWaitPause();

        // Marks _isPaused so components that start later also come up paused, and syncs icons.
        PauseAll(propagateToChildren: false);
    }

    private void GlobalResumeAll()
    {
        if (_inIncomingWait && _incomingWaitTimer != null && IsInstanceValid(_incomingWaitTimer))
        {
            _incomingWaitTimer.SetPaused(false);
            if (_sequencePause != null && _activeCueBar != null)
                _sequencePause.Icon = _activeCueBar.GetThemeIcon("Pause", "AtlasIcons");
        }

        if (_inPreWait)
            PreWaitResume();

        ResumeAll(propagateToChildren: false);
    }
    
    
    
    private void HandleOscComponentCompleted(PanelContainer componentPanel)
    {
        RemoveInstantComponent(componentPanel, _activeOscComponents);
    }

    private void HandleCueLightComponentCompleted(PanelContainer componentPanel)
    {
        RemoveInstantComponent(componentPanel, _activeCueLightComponents);
    }

    private void HandleControlComponentCompleted(PanelContainer componentPanel)
    {
        if (componentPanel != null)
            _controlTimedProgress.Remove(componentPanel);
        RemoveInstantComponent(componentPanel, _activeControlComponents);
    }

    private void HandleMidiOutputComponentCompleted(PanelContainer componentPanel)
    {
        RemoveInstantComponent(componentPanel, _activeMidiOutputComponents);
    }

    /// <summary>
    /// Removes a short-lived component row (OSC / MIDI / cue light / control) and checks cue completion.
    /// </summary>
    private void RemoveInstantComponent<T>(PanelContainer componentPanel, Dictionary<PanelContainer, T> map)
    {
        if (!IsInstanceValid(this) || _isCleaned) return;
        if (componentPanel == null || !IsInstanceValid(componentPanel) || !map.ContainsKey(componentPanel))
            return;

        map.Remove(componentPanel);
        componentPanel.QueueFree();
        CheckForCueCompletion();
    }

    private void CheckForCueCompletion()
    {
        if (_isCleaned) return;

        if (_activeAudioComponents.Count == 0 
            && _activeVideoComponents.Count == 0
            && _activeTextComponents.Count == 0
            && _activeOscComponents.Count == 0
            && _activeMidiOutputComponents.Count == 0
            && _activeCueLightComponents.Count == 0
            && _activeControlComponents.Count == 0)
        {
            _isFinished = true;
            if (_childActiveCues.Count == 0)
            {
                // Prefer immediate finish when already on a deferred panel-complete path so
                // Follow can arm the next cue without an extra frame of gap.
                // Fall back to deferred if we are still inside a re-entrant signal (rare).
                try
                {
                    HandleNaturalContentFinished();
                }
                catch (Exception)
                {
                    Callable.From(HandleNaturalContentFinished).CallDeferred();
                }
            }
        }
    }

    private void OnChildCompleted(ActiveCue child)
    {
        int childId = -1;
        try { childId = child?.Cue?.Id ?? -1; } catch { /* disposed */ }

        _childActiveCues.Remove(child);

        // Permanent finish for this activation (not a scrub-rewind teardown).
        if (!_isRewindingContent && childId >= 0)
            _finishedChildCueIds.Add(childId);

        if (_childActiveCues.Count > 0 || _isCleaned || _isRewindingContent)
            return;

        // Stop/panic path: parent may have been waiting for child stop-fades before freeing UI.
        if (_suppressContentCompleted || _isStopFading)
        {
            Callable.From(TryCleanupAfterStop).CallDeferred();
            return;
        }

        // Natural completion: parent content is done when components finished and last child ends.
        if (_isFinished)
            Callable.From(HandleNaturalContentFinished).CallDeferred();
    }

    /// <summary>
    /// Reports natural content completion once. Ignored after stop/panic or for looping media.
    /// </summary>
    private void RaiseContentCompleted()
    {
        if (_contentCompletedRaised || _suppressContentCompleted || _isCleaned)
            return;
        // Looping media never truly completes for sequence purposes.
        if (_cue != null && _cue.Duration < 0)
            return;

        _contentCompletedRaised = true;
        try
        {
            ContentCompleted?.Invoke();
        }
        catch (Exception ex)
        {
            GD.PrintErr($"ActiveCue:RaiseContentCompleted - {_cue?.Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Queues <see cref="Cleanup"/> for the next idle frame so Free is never attempted
    /// while this object is still locked by a Godot method/signal dispatch.
    /// </summary>
    private void ScheduleCleanup()
    {
        if (_isCleaned || !IsInstanceValid(this)) return;
        // Pending chain members stay until armed / cancelled.
        if (_chainMember != null && !_contentStarted && !_suppressContentCompleted)
            return;
        Callable.From(Cleanup).CallDeferred();
    }

    /// <summary>
    /// Frees this <see cref="GodotObject"/> after the call stack has unwound.
    /// Prefer this over <c>CallDeferred(MethodName.Free)</c>: C# Object subclasses often
    /// report "Nonexistent function 'free'" for the engine free via CallDeferred.
    /// </summary>
    private void FreeDeferred()
    {
        if (IsInstanceValid(this))
            Free();
    }

    /// <summary>
    /// Cleans up resources and removes the cue from the UI.
    /// </summary>
    public void Cleanup()
    {
        lock (_lock) // Add lock for thread safety
        {
            if (_isCleaned)
            {
                GD.Print("ActiveCue:Cleanup - Already cleaned");
                return;
            }

            _isCleaned = true;
            _isPlaying = false;
        }

        // Disconnect completion handlers BEFORE Clean/Stop so they cannot touch freed panels.
        DisconnectPlaybackCompletionHandlers();

        // Hard-stop any remaining playbooks so decoders/fill loops do not leak.
        foreach (var playback in _activeAudioComponents.Values.ToList())
        {
            try { playback.Clean(); } catch (Exception ex)
            {
                GD.PrintErr($"ActiveCue:Cleanup - Audio clean failed: {ex.Message}");
            }
        }
        foreach (var playback in _activeVideoComponents.Values.ToList())
        {
            try { _ = playback.Stop(0); } catch (Exception ex)
            {
                GD.PrintErr($"ActiveCue:Cleanup - Video stop failed: {ex.Message}");
            }
        }
        foreach (var playback in _activeTextComponents.Values.ToList())
        {
            try { _ = playback.Stop(0); } catch (Exception ex)
            {
                GD.PrintErr($"ActiveCue:Cleanup - Text stop failed: {ex.Message}");
            }
        }
        _activeAudioComponents.Clear();
        _componentToAudio.Clear();
        _activeVideoComponents.Clear();
        _componentToVideo.Clear();
        _activeTextComponents.Clear();
        _componentToText.Clear();
        _activeOscComponents.Clear();
        _activeMidiOutputComponents.Clear();
        _activeCueLightComponents.Clear();
        _activeControlComponents.Clear();
        _controlTimedProgress.Clear();

        HookIncomingWaitUpdate(false);
        FreeIncomingWaitTimer();

        if (_updateTimer != null && IsInstanceValid(_updateTimer))
        {
            _updateTimer.Stop();
            _updateTimer.Timeout -= UpdateUi;
            HookPreWaitUpdate(false);
            _updateTimer.QueueFree();
            _updateTimer = null;
        }
        if (_preWaitTimer != null && IsInstanceValid(_preWaitTimer))
        {
            _preWaitTimer.QueueFree();
            _preWaitTimer = null;
        }
        if (_fadeTimer != null && IsInstanceValid(_fadeTimer))
        {
            _fadeTimer.QueueFree();
            _fadeTimer = null;
        }

        // Propagate cancel to unstarted followers if we never completed naturally.
        if (_suppressContentCompleted)
            NextInChain?.CancelPendingFromPredecessor();

        if (_globalSignals != null)
        {
            _globalSignals.StopAll -= GlobalStopAll;
            _globalSignals.PauseAll -= GlobalPauseAll;
            _globalSignals.ResumeAll -= GlobalResumeAll;
        }

        // Nested active cues own UI under this bar — clean them before freeing the parent tree.
        // Prefer waiting for stop-fades via TryCleanupAfterStop; this is the hard teardown path.
        foreach (var child in _childActiveCues.ToList())
        {
            if (child == null || !IsInstanceValid(child)) continue;
            try
            {
                child.Cleanup();
            }
            catch (Exception ex)
            {
                GD.PrintErr($"ActiveCue:Cleanup - Child cleanup failed on {_cue?.Name}: {ex.Message}");
            }
        }
        _childActiveCues.Clear();

        // Do not manually disconnect child-bar button handlers (PreWaitPause etc.): rebinding
        // for the scheduled path makes -= of the original handler throw "nonexistent connection".
        // QueueFree of the active bar tears down those signals with the nodes.

        if (_activeCueBar != null && IsInstanceValid(_activeCueBar))
        {
            _activeCueBar.QueueFree();
        }
        
        try
        {
            if (IsInstanceValid(this))
                EmitSignal(SignalName.Completed);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"ActiveCue:Cleanup - Completed signal: {ex.Message}");
        }

        GD.Print($"ActiveCue:Cleanup - Cleaned up active cue: {_cue?.Name}");

        // Free must not run while this GodotObject is locked (signal/method dispatch).
        // Callable.From invokes the C# Free() after the idle frame — reliable for GodotObject.
        if (IsInstanceValid(this))
            Callable.From(FreeDeferred).CallDeferred();
    }

    /// <summary>
    /// Detaches Completed handlers so Clean/Stop cannot re-enter UI after free.
    /// </summary>
    private void DisconnectPlaybackCompletionHandlers()
    {
        foreach (var (playback, handler) in _audioCompleteHandlers)
        {
            try
            {
                if (playback != null && IsInstanceValid(playback))
                    playback.Completed -= handler;
            }
            catch (Exception ex)
            {
                GD.PrintErr($"ActiveCue:DisconnectPlaybackCompletionHandlers - Audio: {ex.Message}");
            }
        }
        _audioCompleteHandlers.Clear();

        foreach (var (playback, handler) in _videoCompleteHandlers)
        {
            try
            {
                if (playback != null && IsInstanceValid(playback))
                    playback.Completed -= handler;
            }
            catch (Exception ex)
            {
                GD.PrintErr($"ActiveCue:DisconnectPlaybackCompletionHandlers - Video: {ex.Message}");
            }
        }
        _videoCompleteHandlers.Clear();

        foreach (var (playback, handler) in _textCompleteHandlers)
        {
            try
            {
                if (playback != null && IsInstanceValid(playback))
                    playback.Completed -= handler;
            }
            catch (Exception ex)
            {
                GD.PrintErr($"ActiveCue:DisconnectPlaybackCompletionHandlers - Text: {ex.Message}");
            }
        }
        _textCompleteHandlers.Clear();
    }

    /// <summary>
    /// True when the stored media path resolves to an existing file on disk.
    /// </summary>
    private bool MediaFileAvailable(string storedPath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(storedPath))
                return false;
            var globalData = Engine.GetMainLoop() is SceneTree tree
                ? tree.Root.GetNodeOrNull<GlobalData>("/root/GlobalData")
                : null;
            return MediaPaths.Exists(storedPath, globalData?.SessionDir);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Reports a missing media file for this cue via <see cref="MediaHealthService"/> (shell ✕ + inspector styling).
    /// </summary>
    private void ReportMissingMedia(string storedUrl)
    {
        try
        {
            var health = Engine.GetMainLoop() is SceneTree tree
                ? tree.Root.GetNodeOrNull<MediaHealthService>("/root/MediaHealthService")
                : null;
            health?.ReportFileMissing(_cue.Id, storedUrl);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"ActiveCue:ReportMissingMedia - {ex.Message}");
        }
    }

    /// <summary>
    /// Heuristic: decoder/open exceptions that indicate a missing or unreadable file.
    /// </summary>
    private static bool IsMissingFileException(Exception ex, string storedPath)
    {
        if (ex == null)
            return false;
        string msg = ex.Message ?? string.Empty;
        if (msg.Contains("No such file", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("not found", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("open_input failed", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("File not found", StringComparison.OrdinalIgnoreCase))
            return true;

        // Also treat as missing if path clearly does not exist
        try
        {
            if (!string.IsNullOrEmpty(storedPath) &&
                Engine.GetMainLoop() is SceneTree tree)
            {
                var globalData = tree.Root.GetNodeOrNull<GlobalData>("/root/GlobalData");
                string resolved = globalData?.ResolveMediaPath(storedPath) ?? storedPath;
                if (!File.Exists(resolved))
                    return true;
            }
        }
        catch { /* ignore */ }

        return false;
    }
}
