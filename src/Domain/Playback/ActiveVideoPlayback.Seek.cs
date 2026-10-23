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
            double segmentDuration = GetSegmentDurationSecondsUnlocked();
            if (segmentDuration <= 1e-12)
                return 0;

            double posSec = GetMasterClockUs() / (double)MicrosecondsPerSecond;
            double startSec = _startTimeUs / (double)MicrosecondsPerSecond;
            double segmentElapsed = Math.Clamp(posSec - startSec, 0.0, segmentDuration);

            if (_videoComponent.Loop || EffectivePlayCount >= int.MaxValue / 4)
                return segmentElapsed;

            int completed = Math.Max(0, _currentPlayCount - 1);
            completed = Math.Min(completed, Math.Max(0, EffectivePlayCount - 1));
            return completed * segmentDuration + segmentElapsed;
        }
    }

    /// <summary>
    /// Seeks into the multi-play content timeline and updates <see cref="CurrentPlayCount"/>.
    /// </summary>
    /// <param name="contentSeconds">Elapsed content seconds across playcount iterations.</param>
    public void SeekToTotalContentSeconds(double contentSeconds)
    {
        if (contentSeconds < 0) contentSeconds = 0;

        double segmentDuration;
        int effectiveCount;
        bool isLoop;
        lock (_lock)
        {
            segmentDuration = GetSegmentDurationSecondsUnlocked();
            effectiveCount = EffectivePlayCount;
            isLoop = _videoComponent.Loop;
        }

        if (segmentDuration <= 1e-12)
        {
            Seek(_startTimeUs / (double)MicrosecondsPerSecond);
            return;
        }

        if (isLoop || effectiveCount >= int.MaxValue / 4)
        {
            double local = contentSeconds % segmentDuration;
            if (local < 0) local += segmentDuration;
            Seek(_startTimeUs / (double)MicrosecondsPerSecond + local);
            return;
        }

        double total = segmentDuration * Math.Max(1, effectiveCount);
        contentSeconds = Math.Clamp(contentSeconds, 0.0, total);

        int playIndex;
        double localInSegment;
        if (contentSeconds >= total - 1e-9)
        {
            playIndex = Math.Max(0, effectiveCount - 1);
            localInSegment = segmentDuration;
        }
        else
        {
            playIndex = (int)Math.Floor(contentSeconds / segmentDuration);
            playIndex = Math.Clamp(playIndex, 0, Math.Max(0, effectiveCount - 1));
            localInSegment = contentSeconds - playIndex * segmentDuration;
            localInSegment = Math.Clamp(localInSegment, 0.0, segmentDuration);
        }

        lock (_lock)
        {
            _currentPlayCount = playIndex + 1;
        }

        Seek(_startTimeUs / (double)MicrosecondsPerSecond + localInSegment);
    }

    private double GetSegmentDurationSecondsUnlocked()
    {
        if (_endTimeUs < long.MaxValue && _endTimeUs > _startTimeUs)
            return (_endTimeUs - _startTimeUs) / (double)MicrosecondsPerSecond;

        if (_videoComponent.IsImage)
            return _videoComponent.Duration > 0 ? _videoComponent.Duration : 0;

        double span = GetDuration();
        if (span > 0)
        {
            double start = _startTimeUs / (double)MicrosecondsPerSecond;
            // GetDuration is full file length for video decoder; prefer component Duration when set.
            if (_videoComponent.Duration > 0)
                return _videoComponent.Duration;
            return Math.Max(0, span - start);
        }
        return _videoComponent.Duration > 0 ? _videoComponent.Duration : 0;
    }

    /// <summary>Seeks both video and audio to the same media time (seconds).</summary>
    public void Seek(double timeSeconds)
    {
        long us = (long)(timeSeconds * MicrosecondsPerSecond);
        us = Math.Max(_startTimeUs, us);
        if (_endTimeUs < long.MaxValue)
            us = Math.Min(us, _endTimeUs);

        SeekInternal(us, restartClock: !IsPaused && _isPlaying);
        lock (_lock)
        {
            if (IsPaused)
                _pausedAtUs = us;
        }
    }

    /// <summary>
    /// Seeks video (+ embedded audio) to <paramref name="timestampUs"/>.
    /// Heavy FFmpeg seek/discard/prefetch runs on a worker so scrub does not freeze the UI.
    /// </summary>
    private void SeekInternal(long timestampUs, bool restartClock)
    {
        // Hold the fill loop so it cannot PutAudioStreamData across a Clear (stale PCM after seek).
        // Do not use public IsSeeking here — that flag is also held for the whole UI scrub drag.
        // Keep fill suspended across cancel → new seek (do not release the hold here).
        CancelSeekWorker(releaseFillHold: false);

        var cts = new CancellationTokenSource();
        _seekCts = cts;
        _seekTargetUs = timestampUs;
        _seekInProgress = true;
        ReleaseHiddenFrame();

        lock (_lock)
        {
            _audioFillSuspended = true;
            _audioMasterExhausted = false;
            _segmentEndScheduled = false;
        }

        _streamBindings.ClearAll();
        ResetPresentClock();

        // Always park the wall clock at the seek target so silent-video masters and UI
        // match immediately (audio master still holds via _seekTargetUs until finish).
        _wallMediaOriginUs = timestampUs;
        if (restartClock && _isPlaying && !IsPaused)
            _wallClock.Restart();
        else if (!restartClock)
            _wallClock.Reset();

        RefreshPresentTuning();
        RefreshAudioTuning();

        var video = _videoDecoder;
        var audio = _audioDecoder;
        int videoPrefetch = _presentTuning.PrefetchTarget;
        int audioPrefetchMs = _audioTuning.PrefetchMs;
        bool shouldPrime;
        lock (_lock)
            shouldPrime = audio != null && _isPlaying && !IsPaused && HasBoundAudioStreams;

        _seekTask = Task.Run(() =>
        {
            try
            {
                cts.Token.ThrowIfCancellationRequested();
                video?.Seek(timestampUs);
                cts.Token.ThrowIfCancellationRequested();
                video?.Prefetch(videoPrefetch);

                if (audio != null)
                {
                    cts.Token.ThrowIfCancellationRequested();
                    audio.FlushBuffers();
                    audio.Seek(timestampUs);
                    audio.Prefetch(audioPrefetchMs);
                    // Fill is held and the streams are empty: the clock now starts at the target.
                    PublishAudioQueuedEnd(audio.PositionUs);
                }
            }
            catch (OperationCanceledException)
            {
                // Superseded by a newer seek or teardown.
            }
            catch (Exception ex)
            {
                GD.PrintErr($"ActiveVideoPlayback:SeekWorker - {ex.Message}");
            }
            finally
            {
                // Complete only if this generation is still current.
                if (!cts.IsCancellationRequested)
                {
                    bool prime = shouldPrime;
                    Callable.From(() => FinishSeekWorker(cts, prime)).CallDeferred();
                }
            }
        }, cts.Token);
    }

    /// <summary>
    /// Main-thread completion for an async seek: re-prime audio and release present/fill holds.
    /// </summary>
    private void FinishSeekWorker(CancellationTokenSource cts, bool shouldPrime)
    {
        if (cts == null || cts.IsCancellationRequested)
            return;
        // Superseded by a newer seek generation.
        if (!ReferenceEquals(_seekCts, cts))
            return;
        if (_isExiting || IsStopped)
        {
            lock (_lock) _audioFillSuspended = false;
            _seekInProgress = false;
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
        try
        {
            _seekCts?.Cancel();
        }
        catch { /* ignore */ }
        finally
        {
            try { _seekCts?.Dispose(); } catch { /* ignore */ }
            _seekCts = null;
            _seekTask = null;
            _seekInProgress = false;
            try { _videoDecoder?.WakePrefetch(); } catch { /* decoder already gone */ }
            if (releaseFillHold)
            {
                lock (_lock)
                    _audioFillSuspended = false;
            }
        }
    }
}
