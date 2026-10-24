// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Cue2.Domain.Cues;
using Cue2.Services;
using Cue2.Media.Audio;
using Cue2.Media.Decoders;
using Godot;
using SDL3;
using Image = Godot.Image;

namespace Cue2.Domain.Playback;

/// <summary>
/// Partial: playback position, content-time seek and the seek worker.
/// </summary>
public partial class ActiveVideoPlayback
{
    /// <summary>
    /// Current media playback position in seconds (master clock).
    /// </summary>
    public double GetPlaybackTimeSeconds() =>
        GetMasterClockUs() / (double)MicrosecondsPerSecond;

    /// <summary>
    /// Content-local elapsed time including completed playcount iterations.
    /// Infinite loop returns elapsed within the current segment only.
    /// </summary>
    public double GetTotalElapsedContentSeconds()
    {
        lock (_lock)
        {
            if (_videoComponent.IsImage)
            {
                double hold = _videoComponent.Duration > 0 ? _videoComponent.Duration : 0;
                if (hold <= 1e-12)
                    return 0;
                double posSec = GetMasterClockUs() / (double)MicrosecondsPerSecond;
                return Math.Clamp(posSec, 0.0, hold);
            }

            double sequence = GetSequenceDurationSecondsUnlocked();
            double elapsedSequence = GetElapsedInSequenceSecondsUnlocked();
            if (elapsedSequence < 0)
                elapsedSequence = 0;

            if (_videoComponent.Loop || sequence < 0 || EffectivePlayCount >= int.MaxValue / 4)
                return elapsedSequence;

            int completed = Math.Max(0, _currentPlayCount - 1);
            completed = Math.Min(completed, Math.Max(0, EffectivePlayCount - 1));
            return completed * sequence + elapsedSequence;
        }
    }

    /// <summary>
    /// Seeks into the multi-play content timeline and updates <see cref="CurrentPlayCount"/>.
    /// </summary>
    /// <param name="contentSeconds">Elapsed content seconds across playcount iterations.</param>
    public void SeekToTotalContentSeconds(double contentSeconds)
    {
        if (contentSeconds < 0) contentSeconds = 0;

        double sequence;
        int effectiveCount;
        bool isLoop;
        bool isImage;
        lock (_lock)
        {
            sequence = GetSequenceDurationSecondsUnlocked();
            effectiveCount = EffectivePlayCount;
            isLoop = _videoComponent.Loop;
            isImage = _videoComponent.IsImage;
        }

        if (isImage)
        {
            double hold = _videoComponent.Duration > 0 ? _videoComponent.Duration : 0;
            if (hold <= 1e-12)
            {
                Seek(0);
                return;
            }
            Seek(Math.Clamp(contentSeconds, 0.0, hold));
            return;
        }

        if (sequence <= 1e-12)
        {
            Seek(_startTimeUs / (double)MicrosecondsPerSecond);
            return;
        }

        if (isLoop || sequence < 0 || effectiveCount >= int.MaxValue / 4)
        {
            SeekIntoSequence(contentSeconds);
            return;
        }

        double total = sequence * Math.Max(1, effectiveCount);
        contentSeconds = Math.Clamp(contentSeconds, 0.0, total);

        int playIndex;
        double localInSequence;
        if (contentSeconds >= total - 1e-9)
        {
            playIndex = Math.Max(0, effectiveCount - 1);
            localInSequence = sequence;
        }
        else
        {
            playIndex = (int)Math.Floor(contentSeconds / sequence);
            playIndex = Math.Clamp(playIndex, 0, Math.Max(0, effectiveCount - 1));
            localInSequence = contentSeconds - playIndex * sequence;
            localInSequence = Math.Clamp(localInSequence, 0.0, sequence);
        }

        lock (_lock)
            _currentPlayCount = playIndex + 1;

        SeekIntoSequence(localInSequence);
    }

    /// <summary>Wall-clock length of one full pass through all timeline regions, or −1 if a region loops.</summary>
    private double GetSequenceDurationSecondsUnlocked()
    {
        if (_videoComponent.IsImage)
            return _videoComponent.Duration > 0 ? _videoComponent.Duration : 0;

        if (_regions == null || _regions.Count == 0)
            return _videoComponent.Duration > 0 ? _videoComponent.Duration : 0;

        double sum = 0;
        for (int i = 0; i < _regions.Count; i++)
        {
            if (_regions[i].Loop)
                return -1;
            sum += RegionWallSeconds(i) * Math.Max(1, _regions[i].PlayCount);
        }
        return sum;
    }

    /// <summary>Wall-clock from the start of the current region play to <paramref name="posSec"/>.</summary>
    private double ElapsedInRegionPlay(int index, double posSec)
    {
        if (_videoComponent == null || _regions == null || index < 0 || index >= _regions.Count)
            return 0;
        var region = _regions[index];
        double clamped = Math.Clamp(posSec, region.StartSeconds, region.EndSeconds);
        return _videoComponent.IntegrateTimelineWallClock(region.StartSeconds, clamped);
    }

    private double GetElapsedInSequenceSecondsUnlocked()
    {
        if (_regions == null || _regions.Count == 0)
            return 0;
        double posSec = GetMasterClockUs() / (double)MicrosecondsPerSecond;
        double elapsed = 0;
        for (int i = 0; i < _regionIndex && i < _regions.Count; i++)
        {
            var prior = _regions[i];
            if (prior.Loop)
                return elapsed;
            elapsed += RegionWallSeconds(i) * Math.Max(1, prior.PlayCount);
        }

        int idx = Math.Clamp(_regionIndex, 0, _regions.Count - 1);
        var region = _regions[idx];
        double regionLen = RegionWallSeconds(idx);
        double inPlay = ElapsedInRegionPlay(idx, posSec);
        if (region.Loop)
            return elapsed + inPlay;

        int completedPlays = Math.Min(
            Math.Max(0, _regionPlayIndex - 1),
            Math.Max(0, region.PlayCount - 1));
        return elapsed + completedPlays * regionLen + inPlay;
    }

    /// <summary>
    /// Seeks to a wall-clock offset within one region sequence (0 = start of first region).
    /// </summary>
    private void SeekIntoSequence(double localSeconds)
    {
        if (localSeconds < 0)
            localSeconds = 0;
        long targetUs;
        lock (_lock)
        {
            if (_regions == null || _regions.Count == 0 || _videoComponent == null)
            {
                double startSec = _startTimeUs / (double)MicrosecondsPerSecond;
                double endSec = _endTimeUs == long.MaxValue
                    ? startSec + Math.Max(localSeconds, 0)
                    : _endTimeUs / (double)MicrosecondsPerSecond;
                double fileTime = _videoComponent != null
                    ? _videoComponent.FileTimeAtWallOffset(startSec, endSec, localSeconds)
                    : startSec + localSeconds;
                targetUs = (long)(fileTime * MicrosecondsPerSecond);
            }
            else
            {
                double remaining = localSeconds;
                int found = _regions.Count - 1;
                double localInPlay = 0;
                int playInRegion = 0;
                for (int i = 0; i < _regions.Count; i++)
                {
                    var region = _regions[i];
                    double regionLen = RegionWallSeconds(i);
                    if (regionLen <= 1e-12)
                        continue;
                    if (region.Loop)
                    {
                        found = i;
                        localInPlay = remaining % regionLen;
                        if (localInPlay < 0)
                            localInPlay += regionLen;
                        playInRegion = 0;
                        break;
                    }

                    double regionTotal = regionLen * Math.Max(1, region.PlayCount);
                    if (remaining < regionTotal - 1e-9)
                    {
                        found = i;
                        playInRegion = (int)Math.Floor(remaining / regionLen);
                        playInRegion = Math.Clamp(playInRegion, 0, Math.Max(0, region.PlayCount - 1));
                        localInPlay = remaining - playInRegion * regionLen;
                        break;
                    }
                    remaining -= regionTotal;
                    found = i;
                    playInRegion = Math.Max(0, region.PlayCount - 1);
                    localInPlay = regionLen;
                }

                ApplyRegionUnlocked(found);
                _regionPlayIndex = playInRegion + 1;
                var chosen = _regions[found];
                double fileTime = _videoComponent.FileTimeAtWallOffset(
                    chosen.StartSeconds, chosen.EndSeconds, localInPlay);
                targetUs = (long)(fileTime * MicrosecondsPerSecond);
                if (targetUs < _regionStartUs)
                    targetUs = _regionStartUs;
                if (targetUs > _regionEndUs)
                    targetUs = _regionEndUs;
            }
        }

        if (_endTimeUs < long.MaxValue)
            targetUs = Math.Min(targetUs, _endTimeUs);
        SeekInternal(targetUs, restartClock: !IsPaused && _isPlaying, syncRegion: false);
        lock (_lock)
        {
            if (IsPaused)
                _pausedAtUs = targetUs;
        }
    }

    /// <summary>Playback speed at a file time (clamped, timeline-automated).</summary>
    private double CurrentPlayRateAt(long fileUs) => CurrentTimelineAt(fileUs).PlayRate;

    /// <summary>Volume, rate, and pitch at the current decoder/file position.</summary>
    private TimelineAutomation CurrentTimeline()
    {
        long posUs = _audioDecoder?.PositionUs
                     ?? (_videoDecoder?.PositionUs ?? _startTimeUs);
        return CurrentTimelineAt(posUs);
    }

    /// <summary>Volume, rate, and pitch at a file time.</summary>
    private TimelineAutomation CurrentTimelineAt(long fileUs)
    {
        if (_videoComponent == null)
        {
            return new TimelineAutomation
            {
                VolumeLinear = AudioTimelineNode.DefaultVolumeLinear,
                PlayRate = AudioComponent.DefaultPlayRate,
                PitchCents = AudioComponent.DefaultPitchCents,
                OpacityScale = AudioTimelineNode.DefaultOpacityScale
            };
        }

        double posSec = fileUs / (double)MicrosecondsPerSecond;
        return _videoComponent.EvaluateTimeline(posSec);
    }

    /// <summary>Seeks both video and audio to the same media time (seconds).</summary>
    public void Seek(double timeSeconds)
    {
        long us = (long)(timeSeconds * MicrosecondsPerSecond);
        us = Math.Max(_startTimeUs, us);
        if (_endTimeUs < long.MaxValue)
            us = Math.Min(us, _endTimeUs);

        SeekInternal(us, restartClock: !IsPaused && _isPlaying, syncRegion: true);
        lock (_lock)
        {
            if (IsPaused)
                _pausedAtUs = us;
        }
    }

    /// <summary>
    /// Seeks video (+ embedded audio) to <paramref name="timestampUs"/>.
    /// Heavy FFmpeg seek/discard/prefetch runs on a worker so scrub does not freeze the UI.
    /// Rapid live-scrub calls coalesce onto one worker: the UI parks immediately, and the
    /// decoder seeks to the latest target when the in-flight seek finishes.
    /// </summary>
    /// <param name="timestampUs">Target media time in microseconds.</param>
    /// <param name="restartClock">When true, restart the wall clock from the seek target.</param>
    /// <param name="syncRegion">
    /// When true, select the timeline region that contains the seek time and reset its play index.
    /// Pass false when the caller already applied a region (content-sequence seek).
    /// </param>
    private void SeekInternal(long timestampUs, bool restartClock, bool syncRegion = true)
    {
        timestampUs = Math.Max(_startTimeUs, timestampUs);
        if (_endTimeUs < long.MaxValue)
            timestampUs = Math.Min(timestampUs, _endTimeUs);

        // Park UI/clock immediately so the playhead follows the pointer during live scrub.
        _seekTargetUs = timestampUs;
        _clockHoldUs = timestampUs;
        _seekInProgress = true;
        ReleaseHiddenFrame();

        lock (_lock)
        {
            _audioFillSuspended = true;
            _audioMasterExhausted = false;
            _segmentEndScheduled = false;
            if (syncRegion && _videoComponent is { IsImage: false })
                SyncRegionForPositionUnlocked(timestampUs);
        }

        _wallMediaOriginUs = timestampUs;
        if (restartClock && _isPlaying && !IsPaused)
            _wallClock.Restart();
        else if (!restartClock)
            _wallClock.Reset();

        _queuedSeekUs = timestampUs;
        _seekQueued = true;

        // A worker is running or its completion is still queued on the main thread:
        // that completion starts the latest queued target.
        if (_seekTask != null)
        {
            // Click press+release (and tiny pointer jitter) must not abort the seek already
            // going to this time: cancelling it lets CompleteSeekWorker "finish" without
            // seeking, then leftover 4K frames snap the picture and playhead back.
            if (Math.Abs(timestampUs - _inFlightSeekUs) <= SeekSameTargetEpsilonUs)
                return;
            try { _seekCts?.Cancel(); } catch { /* ignore */ }
            return;
        }

        StartSeekWorker();
    }

    /// <summary>Starts a decoder seek to the latest queued target. Main thread only.</summary>
    private void StartSeekWorker()
    {
        if (_isExiting || IsStopped)
        {
            _seekQueued = false;
            _seekInProgress = false;
            _clockHoldUs = -1;
            lock (_lock)
                _audioFillSuspended = false;
            return;
        }

        long timestampUs = _queuedSeekUs;
        _seekQueued = false;
        _seekTargetUs = timestampUs;
        _inFlightSeekUs = timestampUs;
        _clockHoldUs = timestampUs;
        _seekInProgress = true;

        _streamBindings.ClearAll();
        _ratePitch.Reset();
        ResetPresentClock();
        RefreshPresentTuning();
        RefreshAudioTuning();

        var cts = new CancellationTokenSource();
        _seekCts = cts;

        var video = _videoDecoder;
        var audio = _audioDecoder;
        int videoPrefetch = _presentTuning.PrefetchTarget;
        int audioPrefetchMs = _audioTuning.PrefetchMs;

        _seekTask = Task.Run(() =>
        {
            bool completed = false;
            try
            {
                if (IsSeekTokenCancelled(cts))
                    return;
                video?.Seek(timestampUs);
                if (IsSeekTokenCancelled(cts))
                    return;
                video?.Prefetch(videoPrefetch);

                if (audio != null)
                {
                    if (IsSeekTokenCancelled(cts))
                        return;
                    audio.FlushBuffers();
                    audio.Seek(timestampUs);
                    audio.Prefetch(audioPrefetchMs);
                    PublishAudioQueuedEnd(audio.PositionUs);
                }
                completed = true;
            }
            catch (OperationCanceledException)
            {
                // Superseded by a newer seek or teardown.
            }
            catch (ObjectDisposedException)
            {
                // Token disposed during teardown.
            }
            catch (Exception ex)
            {
                GD.PrintErr($"ActiveVideoPlayback:SeekWorker - {ex.Message}");
            }
            finally
            {
                long us = timestampUs;
                bool ok = completed;
                Callable.From(() => CompleteSeekWorker(cts, us, ok)).CallDeferred();
            }
        });
    }

    /// <summary>
    /// Main-thread completion: either start the latest queued live-scrub target, or resume.
    /// A cancelled worker always restarts when a target is still queued, even if the
    /// queued time equals this worker's time (click-release must not "succeed" without seeking).
    /// </summary>
    private void CompleteSeekWorker(CancellationTokenSource cts, long timestampUs, bool completed)
    {
        if (ReferenceEquals(_seekCts, cts))
            _seekCts = null;
        try { cts?.Dispose(); } catch { /* ignore */ }

        if (_isExiting || IsStopped)
        {
            _seekQueued = false;
            _seekTask = null;
            lock (_lock)
                _audioFillSuspended = false;
            _seekInProgress = false;
            _clockHoldUs = -1;
            try { _videoDecoder?.WakePrefetch(); } catch { /* decoder already gone */ }
            return;
        }

        if (_seekQueued && (!completed || _queuedSeekUs != timestampUs))
        {
            StartSeekWorker();
            return;
        }

        _seekQueued = false;
        _seekTask = null;
        if (!completed)
            _clockHoldUs = -1;
        bool shouldPrime = _isPlaying && !IsPaused && !IsSeeking && HasBoundAudioStreams
                           && _audioDecoder != null;
        FinishSeekWorker(shouldPrime);
    }

    /// <summary>
    /// Main-thread completion for an async seek: re-prime audio and release present/fill holds.
    /// </summary>
    private void FinishSeekWorker(bool shouldPrime)
    {
        if (_isExiting || IsStopped)
        {
            lock (_lock) _audioFillSuspended = false;
            _seekInProgress = false;
            _clockHoldUs = -1;
            _videoDecoder?.WakePrefetch();
            return;
        }

        try
        {
            if (shouldPrime && _audioDecoder != null && HasBoundAudioStreams)
            {
                ArmDeclickRamp();
                PrefillAudioStreams();
            }
        }
        catch (Exception ex)
        {
            GD.PrintErr($"ActiveVideoPlayback:FinishSeekWorker - {ex.Message}");
        }
        finally
        {
            lock (_lock)
            {
                _audioFillSuspended = false;
            }
            // Seed the audio-master fallback so the first readable clock after hold
            // is the seek target, not the clip in-point.
            if (_clockHoldUs >= 0)
            {
                _lastAudioMasterUs = _clockHoldUs;
                _lastAudioEndUs = _clockHoldUs;
                _lastAudioQueuedUs = 0;
            }
            _seekInProgress = false;
            _videoDecoder?.WakePrefetch();
        }
    }

    /// <summary>Cancels any in-flight seek worker without waiting for completion.</summary>
    /// <param name="releaseFillHold">
    /// When true (teardown), also clears <see cref="_audioFillSuspended"/>.
    /// When false (chained seek), leave the hold so the fill loop cannot race the next clear.
    /// </param>
    private void CancelSeekWorker(bool releaseFillHold = true)
    {
        _seekQueued = false;
        try { _seekCts?.Cancel(); } catch { /* ignore */ }
        // Do not Dispose here: the worker may still read the token. CompleteSeekWorker disposes.
        if (releaseFillHold)
        {
            _seekInProgress = false;
            _clockHoldUs = -1;
            try { _videoDecoder?.WakePrefetch(); } catch { /* decoder already gone */ }
            lock (_lock)
                _audioFillSuspended = false;
        }
    }

    /// <summary>True when the seek token is cancelled or already disposed.</summary>
    private static bool IsSeekTokenCancelled(CancellationTokenSource cts)
    {
        if (cts == null)
            return true;
        try
        {
            return cts.IsCancellationRequested;
        }
        catch (ObjectDisposedException)
        {
            return true;
        }
    }
}
