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
/// Partial: Body timeline clock, head scrub, seek into pre-wait/content, head progress UI.
/// </summary>
public partial class ActiveCue
{


    private void UpdateUi()
    {
        if (_isCleaned) return;

        // Snapshot keys — dictionary may change if a component completes mid-tick.
        foreach (var panel in _activeAudioComponents.Keys.ToList())
        {
            if (!IsInstanceValid(panel)) continue;
            if (!_componentToAudio.TryGetValue(panel, out var audioComponent) || audioComponent == null)
                continue;
            UpdateComponentUiState(panel, audioComponent);
        }
        // Video components are updated via TimeUpdated event for real-time updates

        foreach (var kv in _activeControlComponents.ToList())
        {
            if (!IsInstanceValid(kv.Key) || kv.Value == null) continue;
            UpdateControlComponentUiState(kv.Key, kv.Value);
        }

        UpdateHeadProgressUi();
    }

    /// <summary>
    /// Playable head-bar span: PreWait + Duration. -1 when infinite/unknown.
    /// </summary>
    public double GetPlayableTimelineDuration()
    {
        if (_cue == null) return 0;
        if (_cue.Duration < 0) return -1;
        return Math.Max(0.0, _cue.PreWait) + Math.Max(0.0, _cue.Duration);
    }

    /// <summary>
    /// True when this active cue still holds audio/video/text playback instances.
    /// </summary>
    private bool HasOwnMediaPlayback()
    {
        return _activeAudioComponents.Count > 0
               || _activeVideoComponents.Count > 0
               || _activeTextComponents.Count > 0;
    }

    /// <summary>
    /// Seeks into the pre-wait region of the body timeline (0 .. PreWait).
    /// </summary>
    private void SeekIntoPreWaitRegion(double preWaitElapsed)
    {
        double pre = Math.Max(0.0, _cue?.PreWait ?? 0.0);
        preWaitElapsed = Math.Clamp(preWaitElapsed, 0.0, Math.Max(0.0, pre - 1e-4));

        // Rewind from content: tear down children / park media, re-enter pre-wait.
        if (_contentPlaybackActive || (_contentPhaseStartedRaised && !_inPreWait))
        {
            TearDownContentPlaybackForRewind();
            BeginPreWaitAtElapsed(preWaitElapsed);
            SetTimelineSeconds(preWaitElapsed);
            UpdateHeadProgressUi();
            return;
        }

        if (_inPreWait)
        {
            AdjustPreWaitElapsed(preWaitElapsed);
            SetTimelineSeconds(preWaitElapsed);
            UpdateHeadProgressUi();
            return;
        }

        // Body not in pre-wait yet — queue.
        _pendingTimelineSeekSeconds = preWaitElapsed;
    }

    /// <summary>
    /// Seeks into the content region. <paramref name="contentTimeSeconds"/> is time since content origin;
    /// <paramref name="absoluteTimelineSeconds"/> is the full body playhead (including pre-wait).
    /// </summary>
    private void SeekIntoContentRegion(double contentTimeSeconds, double absoluteTimelineSeconds)
    {
        if (contentTimeSeconds < 0) contentTimeSeconds = 0;

        // Still in pre-wait: finish it, then land at this absolute body time.
        if (_inPreWait)
        {
            _pendingTimelineSeekSeconds = absoluteTimelineSeconds;
            _ = FinishPreWaitAndStartContent();
            return;
        }

        // Content not started yet (setup) — apply once content begins.
        if (!_contentPhaseStartedRaised && !_contentPlaybackActive)
        {
            _pendingTimelineSeekSeconds = absoluteTimelineSeconds;
            return;
        }

        SetTimelineSeconds(absoluteTimelineSeconds);

        // Scrub to content start: allow finished children to re-fire (P1-18).
        // Mid-timeline scrub-back still skips _finishedChildCueIds.
        bool atContentStart = contentTimeSeconds <= 1e-3;
        if (atContentStart && _finishedChildCueIds.Count > 0)
        {
            GD.Print(
                $"ActiveCue:SeekIntoContentRegion - {_cue?.Name}: content t≈0 — clearing {_finishedChildCueIds.Count} finished child id(s)");
            _finishedChildCueIds.Clear();
        }

        // Content was rewound away (e.g. scrubbed into pre-wait) — restart playback.
        if (!_contentPlaybackActive)
        {
            _pendingTimelineSeekSeconds = absoluteTimelineSeconds;
            _ = BeginContentPhaseAsync();
            return;
        }

        SeekOwnMediaToContentTime(contentTimeSeconds);
        PropagateTimelineSeekToChildren(contentTimeSeconds);

        // Re-spawn children that finished earlier when scrubbing back to the content origin.
        if (atContentStart)
            StartChildCues();

        UpdateHeadProgressUi();
    }

    /// <summary>
    /// Pushes a content-origin timeline (child t=0) into every nested active child.
    /// </summary>
    private void PropagateTimelineSeekToChildren(double childTimelineSeconds)
    {
        foreach (var child in _childActiveCues.ToList())
        {
            if (child == null || !IsInstanceValid(child)) continue;
            try
            {
                // Child body timeline starts when parent content starts.
                child.ApplyCueTimelineSeek(childTimelineSeconds);
            }
            catch (Exception ex)
            {
                GD.PrintErr($"ActiveCue:PropagateTimelineSeekToChildren - {_cue?.Name}: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Stops nested children and parks own media so pre-wait can be re-entered without freeing this cue.
    /// Clears <see cref="_finishedChildCueIds"/> so re-entering content re-fires nested children (P1-18).
    /// </summary>
    private void TearDownContentPlaybackForRewind()
    {
        _isRewindingContent = true;
        try
        {
            foreach (var child in _childActiveCues.ToList())
            {
                if (child == null || !IsInstanceValid(child)) continue;
                try
                {
                    // Immediate cleanup (no fade) — parent is rewinding.
                    child.Cleanup();
                }
                catch (Exception ex)
                {
                    GD.PrintErr($"ActiveCue:TearDownContentPlaybackForRewind - child: {ex.Message}");
                }
            }
            _childActiveCues.Clear();
            // Scrub into pre-wait is a full content rewind — allow children to re-fire on next start.
            _finishedChildCueIds.Clear();
        }
        finally
        {
            _isRewindingContent = false;
        }

        // Park media at start without completing (Stop/Clean would free component rows).
        foreach (var kv in _activeAudioComponents.ToList())
        {
            var playback = kv.Value;
            if (playback == null) continue;
            _componentToAudio.TryGetValue(kv.Key, out var audioComp);
            try
            {
                playback.Pause();
                double start = audioComp?.StartTime ?? 0;
                playback.Seek((long)(start * 1_000_000.0));
            }
            catch (Exception ex)
            {
                GD.PrintErr($"ActiveCue:TearDownContentPlaybackForRewind - audio: {ex.Message}");
            }
        }

        foreach (var kv in _activeVideoComponents.ToList())
        {
            var playback = kv.Value;
            if (playback == null) continue;
            _componentToVideo.TryGetValue(kv.Key, out var videoComp);
            try
            {
                playback.Pause();
                double start = videoComp != null && !videoComp.IsImage ? videoComp.StartTime : 0;
                playback.Seek(start);
            }
            catch (Exception ex)
            {
                GD.PrintErr($"ActiveCue:TearDownContentPlaybackForRewind - video: {ex.Message}");
            }
        }

        foreach (var playback in _activeTextComponents.Values.ToList())
        {
            try { playback?.Pause(); }
            catch { /* best-effort */ }
        }

        _contentPlaybackActive = false;
        _isFinished = false;
    }

    /// <summary>
    /// Re-enters pre-wait at a specific elapsed time (used when scrubbing backward into pre-wait).
    /// </summary>
    private void BeginPreWaitAtElapsed(double elapsedSeconds)
    {
        double pre = Math.Max(0.0, _cue?.PreWait ?? 0.0);
        if (pre <= 1e-9)
        {
            _ = BeginContentPhaseAsync();
            return;
        }

        elapsedSeconds = Math.Clamp(elapsedSeconds, 0.0, pre);
        double remaining = pre - elapsedSeconds;
        if (remaining <= 1e-4)
        {
            _preWaitSecondsHonored = pre;
            _ = BeginContentPhaseAsync();
            return;
        }

        // Reuse PreWait UI path then adjust remaining.
        if (!_inPreWait)
            PreWait();

        AdjustPreWaitElapsed(elapsedSeconds);
    }

    /// <summary>
    /// Seeks own audio/video components to a content-local time, mapping across playcount iterations.
    /// </summary>
    private void SeekOwnMediaToContentTime(double contentTimeSeconds)
    {
        if (contentTimeSeconds < 0) contentTimeSeconds = 0;

        foreach (var kv in _activeAudioComponents.ToList())
        {
            var playback = kv.Value;
            if (playback == null) continue;
            try
            {
                // Multi-play: maps total content seconds → play index + in-segment media seek.
                playback.SeekToTotalContentSeconds(contentTimeSeconds);
            }
            catch (Exception ex)
            {
                GD.PrintErr($"ActiveCue:SeekOwnMediaToContentTime - Audio seek failed on {_cue?.Name}: {ex.Message}");
            }
        }

        foreach (var kv in _activeVideoComponents.ToList())
        {
            var playback = kv.Value;
            if (playback == null) continue;
            try
            {
                playback.SeekToTotalContentSeconds(contentTimeSeconds);
            }
            catch (Exception ex)
            {
                GD.PrintErr($"ActiveCue:SeekOwnMediaToContentTime - Video seek failed on {_cue?.Name}: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Starts the body timeline clock at 0 if not already running.
    /// </summary>
    private void EnsureTimelineStarted()
    {
        if (_timelineStarted) return;
        _timelineStarted = true;
        _timelineBase = 0;
        _timelineClockStartMsec = Time.GetTicksMsec();
        _timelineClockRunning = !_isPaused;
    }

    /// <summary>
    /// Sets the body playhead, preserving running/paused clock state.
    /// </summary>
    private void SetTimelineSeconds(double seconds)
    {
        EnsureTimelineStarted();
        _timelineBase = Math.Max(0.0, seconds);
        _timelineClockStartMsec = Time.GetTicksMsec();
    }

    private void PauseTimelineClock()
    {
        if (!_timelineStarted || !_timelineClockRunning) return;
        _timelineBase = GetWallTimelineSeconds();
        _timelineClockRunning = false;
    }

    private void ResumeTimelineClock()
    {
        if (!_timelineStarted || _isCleaned || _timelineClockRunning) return;
        _timelineClockStartMsec = Time.GetTicksMsec();
        _timelineClockRunning = true;
    }

    /// <summary>
    /// Wall-clock body playhead (no media/child soft-sync).
    /// </summary>
    private double GetWallTimelineSeconds()
    {
        if (!_timelineStarted) return 0;
        double t = _timelineBase;
        if (_timelineClockRunning)
            t += (Time.GetTicksMsec() - _timelineClockStartMsec) / 1000.0;
        return Math.Max(0.0, t);
    }

    /// <summary>
    /// Current body playhead in seconds (0 = start of pre-wait).
    /// Prefers live pre-wait timer / media / nested children when available so group bars track real progress.
    /// </summary>
    public double GetCueTimelineSeconds()
    {
        if (!_timelineStarted && !_inPreWait && !_contentPlaybackActive)
            return 0;

        double pre = Math.Max(0.0, _cue?.PreWait ?? 0.0);

        // Pre-wait: derive from timer so head bar matches pre-wait strip.
        if (_inPreWait && _preWaitTimer != null && IsInstanceValid(_preWaitTimer))
        {
            double elapsed = pre - _preWaitTimer.TimeLeft;
            return Math.Clamp(elapsed, 0.0, pre);
        }

        if (!(_contentPlaybackActive || _contentPhaseStartedRaised))
            return GetWallTimelineSeconds();

        double media = TryGetMaxOwnMediaContentSeconds();

        // Leaf with own media: media playhead is authoritative (component scrub must move the head bar,
        // including seek-backward — wall clock alone would stick at the high-water mark).
        if (_childActiveCues.Count == 0 && media >= 0)
            return _preWaitSecondsHonored + media;

        // Groups: max of wall, own media, and still-active children.
        double best = GetWallTimelineSeconds();
        if (media >= 0)
            best = Math.Max(best, _preWaitSecondsHonored + media);

        foreach (var child in _childActiveCues)
        {
            if (child == null || !IsInstanceValid(child)) continue;
            try
            {
                double childT = child.GetCueTimelineSeconds();
                best = Math.Max(best, _preWaitSecondsHonored + childT);
            }
            catch
            {
                // ignore disposed child during teardown
            }
        }

        return Math.Max(0.0, best);
    }

    /// <summary>
    /// After a component-level scrub, snap the body playhead to own media so the head bar updates immediately.
    /// </summary>
    /// <param name="contentLocalSeconds">Content-local time (media time − StartTime).</param>
    private void SyncHeadTimelineFromComponentSeek(double contentLocalSeconds)
    {
        if (_isCleaned) return;
        if (contentLocalSeconds < 0) contentLocalSeconds = 0;

        double absolute = _preWaitSecondsHonored + contentLocalSeconds;
        SetTimelineSeconds(absolute);
        // Force head bar to the scrub target even if a decoder seek is still in flight
        // (UpdateHeadProgressUi would otherwise early-return and leave a stale fill).
        ApplyHeadProgressDisplay(absolute, GetPlayableTimelineDuration());
    }

    /// <summary>
    /// True when any active media component has an async seek in flight.
    /// </summary>
    private bool AnyComponentDecoderSeeking()
    {
        foreach (var pb in _activeAudioComponents.Values)
        {
            if (pb != null && !pb.IsStopped && pb.IsDecoderSeeking)
                return true;
        }
        foreach (var pb in _activeVideoComponents.Values)
        {
            if (pb != null && !pb.IsStopped && pb.IsDecoderSeeking)
                return true;
        }
        return false;
    }

    /// <summary>
    /// Writes head progress bar + time labels for a fixed elapsed position (scrub hold / force).
    /// </summary>
    private void ApplyHeadProgressDisplay(double elapsed, double playable)
    {
        if (_headProgressBar == null || !IsInstanceValid(_headProgressBar))
            return;
        UpdateHeadTimeLabels(elapsed, playable);
        if (playable < 0)
            _headProgressBar.Value = 0;
        else if (playable <= 1e-9)
            _headProgressBar.Value = 0;
        else
            _headProgressBar.Value = Math.Clamp(elapsed / playable * 100.0, 0.0, 100.0);
    }

    /// <summary>
    /// Max content-local time from active audio/video, including completed playcount iterations.
    /// </summary>
    /// <returns>Seconds, or -1 when no running media.</returns>
    private double TryGetMaxOwnMediaContentSeconds()
    {
        double max = -1;

        foreach (var kv in _activeAudioComponents)
        {
            var playback = kv.Value;
            if (playback == null || playback.IsStopped) continue;
            // Includes (playCount-1)*segment + in-segment elapsed so head bar does not reset on loop.
            double local = playback.GetTotalElapsedContentSeconds();
            if (local > max) max = local;
        }

        foreach (var kv in _activeVideoComponents)
        {
            var playback = kv.Value;
            if (playback == null || playback.IsStopped) continue;
            double local = playback.GetTotalElapsedContentSeconds();
            if (local > max) max = local;
        }

        return max;
    }

    /// <summary>
    /// Adjusts an active pre-wait timer so elapsed time matches <paramref name="elapsedSeconds"/>.
    /// </summary>
    private void AdjustPreWaitElapsed(double elapsedSeconds)
    {
        if (!_inPreWait || _preWaitTimer == null || !IsInstanceValid(_preWaitTimer))
            return;

        double preWait = Math.Max(0.0, _cue?.PreWait ?? 0.0);
        if (preWait <= 1e-9)
            return;

        elapsedSeconds = Math.Clamp(elapsedSeconds, 0.0, preWait);
        double remaining = preWait - elapsedSeconds;
        if (remaining <= 1e-4)
        {
            _ = FinishPreWaitAndStartContent();
            return;
        }

        bool wasPaused = _preWaitTimer.Paused || _isPaused;
        _preWaitTimer.Stop();
        _preWaitTimer.WaitTime = remaining;
        _preWaitTimer.Start();
        if (wasPaused)
            _preWaitTimer.SetPaused(true);

        if (_preWaitTimerLabel != null)
            _preWaitTimerLabel.Text = UiUtilities.FormatTime(remaining);
        if (_preWaitProgress != null)
            _preWaitProgress.Value = (remaining / preWait) * 100.0;
    }

    /// <summary>
    /// Refreshes head progress bar and time labels from the body timeline (pre-wait + content).
    /// </summary>
    private void UpdateHeadProgressUi()
    {
        if (_isCleaned || _headProgressBar == null || !IsInstanceValid(_headProgressBar))
            return;
        if (_headIsSeeking)
            return;

        // While any component is mid async seek, hold the last scrub/preview head position
        // so the bar does not flick back to the pre-seek playhead.
        if (AnyComponentDecoderSeeking())
            return;

        double playable = GetPlayableTimelineDuration();
        double elapsed = 0;

        if (_timelineStarted || _inPreWait || _contentPlaybackActive)
            elapsed = GetCueTimelineSeconds();

        // Soft-sync wall clock to derived playhead so pause/resume stays consistent.
        if (!_headIsSeeking && (_timelineClockRunning || _timelineStarted))
        {
            _timelineBase = elapsed;
            _timelineClockStartMsec = Time.GetTicksMsec();
        }

        UpdateHeadTimeLabels(elapsed, playable);

        if (playable < 0)
        {
            // Infinite: show empty fill, elapsed only.
            _headProgressBar.Value = 0;
        }
        else if (playable <= 1e-9)
        {
            _headProgressBar.Value = _contentPlaybackActive || _inPreWait ? 100 : 0;
        }
        else
        {
            _headProgressBar.Value = Math.Clamp(elapsed / playable * 100.0, 0.0, 100.0);
        }
    }

    /// <summary>
    /// Writes head elapsed / remaining labels for the playable body span.
    /// </summary>
    private void UpdateHeadTimeLabels(double elapsedSeconds, double durationSeconds)
    {
        if (_headLabelTimeLeft != null && IsInstanceValid(_headLabelTimeLeft))
            _headLabelTimeLeft.Text = UiUtilities.FormatTime(Math.Max(0, elapsedSeconds));

        if (_headLabelTimeRight == null || !IsInstanceValid(_headLabelTimeRight))
            return;

        if (durationSeconds < 0)
        {
            _headLabelTimeRight.Text = "∞";
        }
        else
        {
            double remaining = Math.Max(0, durationSeconds - elapsedSeconds);
            _headLabelTimeRight.Text = $"-({UiUtilities.FormatTime(remaining)})";
        }
    }
}
