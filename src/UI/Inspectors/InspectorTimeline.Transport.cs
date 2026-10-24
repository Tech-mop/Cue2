// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System;
using Cue2.Domain.Commands;
using Cue2.Domain.Cues;
using Cue2.Domain.Playback;
using Cue2.Services;
using Cue2.UI.Utilities;
using Godot;

namespace Cue2.UI.Inspectors;

/// <summary>
/// Partial: ruler playhead, play/pause, play-from-inspector, and live seek while the cue is active.
/// </summary>
public partial class InspectorTimeline
{
    private float _parkedPlayheadNorm;
    private bool _scrubbingPlayhead;
    /// <summary>Instance a ruler drag is seeking; kept until release so a rebind cannot strand it.</summary>
    private ActiveCue _scrubTarget;
    /// <summary>Last file time sent to <see cref="_scrubTarget"/>, or -1 when none was sent.</summary>
    private double _scrubFileSeconds = -1;

    /// <summary>Frames the per-frame instance lookup is reused before a fresh walk.</summary>
    private const int LookupRefreshFrames = 15;
    private ActiveCue _lookupResult;
    private int _lookupCueId = -1;
    private int _lookupRootCount = -1;
    private int _lookupFramesLeft;
    private bool _transportWired;

    /// <summary>Focused cue for play-from-playhead (house GO / pause).</summary>
    public Func<Cue> GetFocusedCue { get; set; }

    /// <summary>Session services (command executor).</summary>
    public Func<GlobalData> GetGlobalData { get; set; }

    /// <summary>User-facing log bus.</summary>
    public Func<GlobalSignals> GetGlobalSignals { get; set; }

    /// <summary>When true, ruler scrub seeks live audio file playbacks of the focused cue.</summary>
    public bool SeekAudioOnScrub { get; set; } = true;

    /// <summary>When true, ruler scrub seeks live video playbacks of the focused cue.</summary>
    public bool SeekVideoOnScrub { get; set; }

    /// <summary>
    /// Follows house playback when the cue is active, otherwise keeps the parked playhead.
    /// Does not follow live while a decoder seek or post-seek clock hold is in flight.
    /// Call from the inspector <c>_Process</c>.
    /// </summary>
    public void TickPlayhead()
    {
        if (_zoom == null || _display == null)
            return;

        if (_accordion == null || !_accordion.Visible)
        {
            _zoom.SetPlayheadNorm(-1f);
            return;
        }

        double duration = ResolvedFileDuration();
        bool canPlay = _timeline != null && GetFocusedCue?.Invoke() != null && duration > 0;
        _zoom.SetPlayPauseEnabled(canPlay);
        if (duration <= 0)
        {
            _zoom.SetPlayheadNorm(-1f);
            _zoom.SetTransportState(false, false);
            return;
        }

        var live = ReadLivePlayback();
        bool isActive = live.HasValue;
        bool isPaused = live?.IsPaused == true;

        // Keep the click/scrub park until the decoder actually lands. Live clock can still
        // read an old audio/wall position (or a leftover 4K frame) while a seek is in flight.
        if (!_scrubbingPlayhead && isActive && live.Value.FileSeconds >= 0 && !live.Value.IsDecoderSeeking)
            _parkedPlayheadNorm = (float)Math.Clamp(live.Value.FileSeconds / duration, 0, 1);

        ApplyParkedPlayheadToDisplay(duration);
        _zoom.SetTransportState(isActive, isPaused);
    }

    private void WireTransport()
    {
        if (_zoom == null || _transportWired)
            return;
        _zoom.RulerPressed += OnRulerPressed;
        _zoom.RulerDragged += OnRulerDragged;
        _zoom.RulerReleased += OnRulerReleased;
        _zoom.PlayPausePressed += OnPlayPausePressed;
        _transportWired = true;
    }

    private void UnwireTransport()
    {
        if (_zoom == null || !_transportWired)
            return;
        _zoom.RulerPressed -= OnRulerPressed;
        _zoom.RulerDragged -= OnRulerDragged;
        _zoom.RulerReleased -= OnRulerReleased;
        _zoom.PlayPausePressed -= OnPlayPausePressed;
        _transportWired = false;
        EndScrub();
    }

    private void ResetParkedPlayhead()
    {
        // Release the previous cue's scrub hold before the park moves to the new cue.
        EndScrub();
        double duration = ResolvedFileDuration();
        float startNorm = 0f;
        if (_timeline != null && duration > 0)
        {
            var regions = _timeline.GetPlaybackRegions();
            if (regions.Count > 0)
                startNorm = (float)Math.Clamp(regions[0].StartSeconds / duration, 0, 1);
        }

        _parkedPlayheadNorm = startNorm;
        ApplyParkedPlayheadToDisplay(duration);
        _zoom?.SetTransportState(false, false);
        _zoom?.SetPlayPauseEnabled(_timeline != null && GetFocusedCue?.Invoke() != null && duration > 0);
    }

    private void ApplyParkedPlayheadToDisplay(double duration)
    {
        if (_zoom == null)
            return;
        if (duration <= 0)
        {
            _zoom.SetPlayheadNorm(-1f);
            _zoom.SetPlayheadSeconds(0);
            return;
        }

        _zoom.SetPlayheadNorm(_parkedPlayheadNorm);
        _zoom.SetPlayheadSeconds(_parkedPlayheadNorm * duration);
    }

    private void OnRulerPressed(float fileNorm)
    {
        EndScrub();
        ParkPlayhead(fileNorm);
        _scrubbingPlayhead = true;
        SeekScrubTarget();
    }

    private void OnRulerDragged(float fileNorm)
    {
        if (!_scrubbingPlayhead)
            return;
        ParkPlayhead(fileNorm);
        SeekScrubTarget();
    }

    private void OnRulerReleased()
    {
        if (_scrubbingPlayhead)
            EndScrub();
    }

    private void ParkPlayhead(float fileNorm)
    {
        _parkedPlayheadNorm = Mathf.Clamp(fileNorm, 0f, 1f);
        ApplyParkedPlayheadToDisplay(ResolvedFileDuration());
    }

    /// <summary>
    /// Live-seeks the instance being scrubbed to the parked playhead (pointer still down).
    /// The instance is resolved on the first drag event that finds one, then kept.
    /// </summary>
    private void SeekScrubTarget()
    {
        if (!IsDrivableInstance(_scrubTarget))
            _scrubTarget = FindPlayingFocusedActiveCue(forceRefresh: true);
        if (_scrubTarget == null)
            return;

        double duration = ResolvedFileDuration();
        if (duration <= 0)
            return;

        _scrubFileSeconds = _parkedPlayheadNorm * duration;
        SeekInstance(_scrubTarget, _scrubFileSeconds, scrubbing: true);
    }

    /// <summary>
    /// Ends a ruler scrub: sends the final seek with the scrub hold released, so the instance
    /// resumes. Safe to call when no scrub is active. Used on release, rebind and detach.
    /// </summary>
    private void EndScrub()
    {
        _scrubbingPlayhead = false;
        var target = _scrubTarget;
        double fileSeconds = _scrubFileSeconds;
        _scrubTarget = null;
        _scrubFileSeconds = -1;
        if (IsDrivableInstance(target) && fileSeconds >= 0)
            SeekInstance(target, fileSeconds, scrubbing: false);
    }

    private void SeekInstance(ActiveCue active, double fileSeconds, bool scrubbing)
    {
        try
        {
            active.SeekOwnMediaToFileTime(fileSeconds, SeekAudioOnScrub, SeekVideoOnScrub, scrubbing);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"InspectorTimeline:SeekInstance - {ex.Message}");
        }
    }

    private void OnPlayPausePressed()
    {
        var cue = GetFocusedCue?.Invoke();
        var data = GetGlobalData?.Invoke();
        var signals = GetGlobalSignals?.Invoke();
        if (cue == null)
        {
            signals?.EmitSignal(nameof(GlobalSignals.Log), "Timeline: No cue selected to play", (int)LogType.Warning);
            return;
        }

        if (!cue.Armed)
        {
            signals?.EmitSignal(nameof(GlobalSignals.Log),
                $"Timeline: Cue \"{cue.Name}\" is disarmed", (int)LogType.Warning);
            return;
        }

        var existing = FindPlayingFocusedActiveCue(forceRefresh: true);
        if (existing != null)
        {
            if (existing.IsTransportPaused)
                existing.RequestResume();
            else
                existing.RequestPause();
            return;
        }

        var executor = data?.CueCommandExecutor;
        if (executor == null)
        {
            GD.PrintErr("InspectorTimeline:OnPlayPausePressed - CueCommandExecutor missing");
            return;
        }

        try { cue.CalculateTotalDuration(); } catch { /* best-effort */ }

        double duration = ResolvedFileDuration();
        double fileSec = _parkedPlayheadNorm * Math.Max(duration, 1e-9);
        double wall = _timeline != null
            ? _timeline.WallClockAtFileTime(fileSec)
            : fileSec;
        double pre = Math.Max(0.0, cue.PreWait);
        double bodyTime = pre + wall;
        double playable = cue.Duration < 0
            ? -1.0
            : pre + Math.Max(0.0, cue.Duration);

        if (playable >= 0 && bodyTime >= playable - 1e-4)
        {
            signals?.EmitSignal(nameof(GlobalSignals.Log),
                $"Timeline: Playhead is past the end of \"{cue.Name}\", nothing to play", (int)LogType.Warning);
            return;
        }

        GD.Print(
            $"InspectorTimeline:OnPlayPausePressed - {cue.Name} @ file={fileSec:F3}s " +
            $"body={bodyTime:F3}s (pre={pre:F3}s wall={wall:F3}s playable={playable:F3}s)");

        executor.ActivateSequenceFrom(cue, controlGoFadeIn: null, startAtTimelineSeconds: bodyTime);
        signals?.EmitSignal(nameof(GlobalSignals.Log),
            $"Timeline: Playing \"{cue.Name}\" from {UiUtilities.FormatTime(fileSec)}", (int)LogType.Info);
    }

    private readonly struct LivePlaybackState
    {
        public LivePlaybackState(double fileSeconds, bool isPaused, bool isDecoderSeeking = false)
        {
            FileSeconds = fileSeconds;
            IsPaused = isPaused;
            IsDecoderSeeking = isDecoderSeeking;
        }

        /// <summary>Decoder file time in seconds, or &lt; 0 to keep the parked playhead.</summary>
        public double FileSeconds { get; }

        /// <summary>True when cue transport is paused.</summary>
        public bool IsPaused { get; }

        /// <summary>True while a seek worker or post-seek clock hold is active.</summary>
        public bool IsDecoderSeeking { get; }
    }

    private LivePlaybackState? ReadLivePlayback()
    {
        var active = FindPlayingFocusedActiveCue(forceRefresh: false);
        if (active == null)
            return null;

        bool paused = active.IsTransportPaused;
        try
        {
            if (SeekAudioOnScrub)
            {
                foreach (var playback in active.EnumerateAudioPlaybacks())
                {
                    if (playback == null || playback.IsStopped)
                        continue;
                    return new LivePlaybackState(playback.GetPlaybackTimeMs() / 1000.0,
                        paused || playback.IsPaused, playback.IsDecoderSeeking);
                }
            }

            if (SeekVideoOnScrub)
            {
                foreach (var playback in active.EnumerateVideoPlaybacks())
                {
                    if (playback == null || playback.IsStopped)
                        continue;
                    return new LivePlaybackState(playback.GetPlaybackTimeSeconds(),
                        paused || playback.IsPaused, playback.IsDecoderSeeking);
                }
            }
        }
        catch
        {
            // Playback may be mid-teardown
        }

        // Pre-wait or setup: cue is active but media has not started.
        return new LivePlaybackState(-1, paused);
    }

    /// <summary>
    /// Finds the instance of the focused cue the transport should drive: the newest live
    /// instance, preferring one that is not stop-fading. Standby preloads that have not
    /// started (OSC Load) and cleaned instances are skipped.
    /// </summary>
    /// <param name="forceRefresh">
    /// True for operator actions (Play, ruler press). False for the per-frame playhead, which
    /// reuses the last result until the focused cue, the number of active roots or the result
    /// changes, or <see cref="LookupRefreshFrames"/> frames pass.
    /// </param>
    private ActiveCue FindPlayingFocusedActiveCue(bool forceRefresh = true)
    {
        var cue = GetFocusedCue?.Invoke();
        var executor = GetGlobalData?.Invoke()?.CueCommandExecutor;
        if (cue == null || executor == null)
        {
            _lookupCueId = -1;
            _lookupResult = null;
            return null;
        }

        var roots = executor.ActiveCues;
        if (!forceRefresh
            && _lookupFramesLeft > 0
            && _lookupCueId == cue.Id
            && _lookupRootCount == roots.Count
            && (_lookupResult == null || (IsDrivableInstance(_lookupResult) && !_lookupResult.IsStopFading)))
        {
            _lookupFramesLeft--;
            return _lookupResult;
        }

        ActiveCue fading = null;
        ActiveCue found = null;
        // Roots are appended in start order: walk from the end so the newest instance wins.
        for (int i = roots.Count - 1; i >= 0 && found == null; i--)
        {
            var root = roots[i];
            if (root == null || !GodotObject.IsInstanceValid(root))
                continue;
            try
            {
                foreach (var active in root.EnumerateSelfAndDescendants())
                {
                    if (active?.Cue == null || active.Cue.Id != cue.Id || !IsDrivableInstance(active))
                        continue;
                    if (!active.IsStopFading)
                    {
                        found = active;
                        break;
                    }
                    fading ??= active;
                }
            }
            catch
            {
                // Active may be mid-teardown (silent: this runs every frame).
            }
        }

        _lookupResult = found ?? fading;
        _lookupCueId = cue.Id;
        _lookupRootCount = roots.Count;
        _lookupFramesLeft = LookupRefreshFrames;
        return _lookupResult;
    }

    private static bool IsDrivableInstance(ActiveCue active) =>
        active != null
        && GodotObject.IsInstanceValid(active)
        && !active.IsCleaned
        && !active.IsStandbyPreloaded;
}
