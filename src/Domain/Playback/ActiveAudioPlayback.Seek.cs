// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Cue2.Domain.Cues;
using Cue2.Services;
using Cue2.Media.Audio;
using Cue2.Media.Decoders;
// MediaMemory used on Clean for LOH reclaim after large PCM stores
using Godot;
using SDL3;

namespace Cue2.Domain.Playback;

/// <summary>
/// Partial: remaining/elapsed time, sequence position and seek.
/// </summary>
public partial class ActiveAudioPlayback
{
    public double GetRemainingTime()
    {
        lock (_lock)
        {
            if (_audioComponent.Loop) return -1.0;
            double sequence = GetSequenceDurationSecondsUnlocked();
            if (sequence < 0)
                return -1.0;
            double remainingSequence = GetRemainingInSequenceSecondsUnlocked();
            if (remainingSequence < 0)
                return -1.0;
            int remainingCounts = Math.Max(0, EffectivePlayCount - _currentPlayCount);
            return remainingSequence + remainingCounts * sequence;
        }
    }

    /// <summary>
    /// Content-local elapsed time including completed playcount iterations.
    /// 0 at the start of the first play; continues past segment length on replay 2, 3, …
    /// Infinite loop returns elapsed within the current segment only.
    /// </summary>
    /// <returns>Seconds of content progress for cue/component progress bars.</returns>
    public double GetTotalElapsedContentSeconds()
    {
        lock (_lock)
        {
            double sequence = GetSequenceDurationSecondsUnlocked();
            double elapsedSequence = GetElapsedInSequenceSecondsUnlocked();
            if (elapsedSequence < 0)
                elapsedSequence = 0;

            // Infinite loop: do not accumulate unboundedly for UI.
            if (_audioComponent.Loop || sequence < 0 || EffectivePlayCount >= int.MaxValue / 4)
                return elapsedSequence;

            int completed = Math.Max(0, _currentPlayCount - 1);
            completed = Math.Min(completed, Math.Max(0, EffectivePlayCount - 1));
            return completed * sequence + elapsedSequence;
        }
    }

    /// <summary>
    /// Seeks into the multi-play content timeline (0 = start of first play).
    /// Updates <see cref="CurrentPlayCount"/> so progress continues correctly after seek.
    /// </summary>
    /// <param name="contentSeconds">Elapsed content seconds across playcount iterations.</param>
    public void SeekToTotalContentSeconds(double contentSeconds)
    {
        if (contentSeconds < 0) contentSeconds = 0;

        double sequence;
        int effectiveCount;
        bool isLoop;
        lock (_lock)
        {
            sequence = GetSequenceDurationSecondsUnlocked();
            effectiveCount = EffectivePlayCount;
            isLoop = _audioComponent.Loop;
        }

        if (sequence <= 1e-12)
        {
            Seek(_startTimeUs);
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
        if (_regions == null || _regions.Count == 0)
            return _audioComponent.Duration > 0 ? _audioComponent.Duration : 0;

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
        if (_audioComponent == null || _regions == null || index < 0 || index >= _regions.Count)
            return 0;
        var region = _regions[index];
        double clamped = Math.Clamp(posSec, region.StartSeconds, region.EndSeconds);
        return _audioComponent.IntegrateTimelineWallClock(region.StartSeconds, clamped);
    }

    private double GetElapsedInSequenceSecondsUnlocked()
    {
        if (_regions == null || _regions.Count == 0)
            return 0;
        double posSec = GetPlaybackPositionUs() / 1_000_000.0;
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

    private double GetRemainingInSequenceSecondsUnlocked()
    {
        if (_regions == null || _regions.Count == 0)
            return 0;
        int idx = Math.Clamp(_regionIndex, 0, _regions.Count - 1);
        var region = _regions[idx];
        if (region.Loop)
            return -1;

        double posSec = GetPlaybackPositionUs() / 1_000_000.0;
        double regionLen = RegionWallSeconds(idx);
        double inPlay = ElapsedInRegionPlay(idx, posSec);
        double remainingThisPlay = Math.Max(0, regionLen - inPlay);
        int remainingPlays = Math.Max(0, Math.Max(1, region.PlayCount) - _regionPlayIndex);
        double remaining = remainingThisPlay + remainingPlays * regionLen;

        for (int i = _regionIndex + 1; i < _regions.Count; i++)
        {
            var later = _regions[i];
            if (later.Loop)
                return -1;
            remaining += RegionWallSeconds(i) * Math.Max(1, later.PlayCount);
        }
        return remaining;
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
            if (_regions == null || _regions.Count == 0 || _audioComponent == null)
            {
                double startSec = _startTimeUs / 1_000_000.0;
                double endSec = _endTimeUs == long.MaxValue
                    ? startSec + Math.Max(localSeconds, 0)
                    : _endTimeUs / 1_000_000.0;
                double fileTime = _audioComponent != null
                    ? _audioComponent.FileTimeAtWallOffset(startSec, endSec, localSeconds)
                    : startSec + localSeconds;
                targetUs = (long)(fileTime * 1_000_000.0);
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
                double fileTime = _audioComponent.FileTimeAtWallOffset(
                    chosen.StartSeconds, chosen.EndSeconds, localInPlay);
                targetUs = (long)(fileTime * 1_000_000.0);
                if (targetUs < _regionStartUs)
                    targetUs = _regionStartUs;
                if (targetUs > _regionEndUs)
                    targetUs = _regionEndUs;
            }
        }

        if (_endTimeUs < long.MaxValue)
            targetUs = Math.Min(targetUs, _endTimeUs);
        Seek(targetUs, syncRegion: false);
    }

    /// <summary>Playback speed used by fades and progress (clamped, timeline-automated).</summary>
    private double CurrentPlayRate() => CurrentTimeline().PlayRate;

    /// <summary>Volume, rate, and pitch at the current decoder position.</summary>
    private TimelineAutomation CurrentTimeline()
    {
        if (_audioComponent == null)
        {
            return new TimelineAutomation
            {
                VolumeLinear = AudioTimelineNode.DefaultVolumeLinear,
                PlayRate = AudioComponent.DefaultPlayRate,
                PitchCents = AudioComponent.DefaultPitchCents
            };
        }

        double posSec = Decoder != null ? Decoder.PositionUs / 1_000_000.0 : _audioComponent.StartTime;
        double? rateOverride;
        float? pitchOverride;
        lock (_lock)
        {
            rateOverride = _runtimePlayRate;
            pitchOverride = _runtimePitchCents;
        }
        return _audioComponent.EvaluateTimeline(posSec, rateOverride, pitchOverride);
    }

    public long GetPlaybackTimeMs()
    {
        return GetPlaybackPositionUs() / 1000;
    }

    /// <summary>
    /// Estimated audible position: decoder position minus average SDL queue latency.
    /// While an async seek is in flight, returns the seek target so progress UI does not snap back.
    /// </summary>
    private long GetPlaybackPositionUs()
    {
        if (_seekInProgress)
            return _seekTargetUs;
        if (Decoder == null) return _startTimeUs;
        long decUs = Decoder.PositionUs;
        long queuedUs = 0;
        int count = 0;
        foreach (var b in _streamBindings.Snapshot)
        {
            long queuedBytes = _streamBindings.GetQueued(b);
            if (queuedBytes < 0) continue;
            int outCh = GetStreamChannels(b);
            if (outCh <= 0 || SourceSampleRate <= 0) continue;
            queuedUs += queuedBytes * 1_000_000L / (SourceSampleRate * outCh * sizeof(float));
            count++;
        }
        if (count > 0) queuedUs /= count;
        long pos = decUs - queuedUs;
        return pos < _startTimeUs ? _startTimeUs : pos;
    }

    /// <summary>
    /// Seeks to media time <paramref name="timestampUs"/>. Heavy streaming seek/trim + prefetch
    /// run on a worker so scrub does not freeze the UI. PCM-store seeks are still dispatched
    /// to the worker for a uniform completion path.
    /// </summary>
    /// <param name="timestampUs">Target media time in microseconds.</param>
    /// <param name="syncRegion">
    /// When true, select the timeline region that contains the seek time and reset its play index.
    /// Pass false when the caller already applied a region (content-sequence seek).
    /// </param>
    public void Seek(long timestampUs, bool syncRegion = true)
    {
        try
        {
            bool wasPaused;
            lock (_lock)
            {
                wasPaused = IsPaused;
                _fillSuspended = true;
                // Hold paused until worker finishes when we need to resume after.
                if (!wasPaused && !IsStopped)
                    IsPaused = true;
            }

            CancelSeekWorker(releaseFillHold: false);

            RefreshAudioTuning();

            _streamBindings.ClearAll();

            long clamped = Math.Max(_startTimeUs, timestampUs);
            if (_endTimeUs < long.MaxValue)
                clamped = Math.Min(clamped, _endTimeUs);
            if (syncRegion)
            {
                lock (_lock)
                    SyncRegionForPositionUnlocked(clamped);
            }

            _framesDelivered = 0;
            _pausedAtUs = clamped;
            _seekTargetUs = clamped;
            _ratePitch.Reset();

            bool resumeAfter = !wasPaused && !IsStopped;
            var decoder = Decoder;
            int prefetchMs = Math.Max(50, _audioTuning.PrefetchMs / 2);
            var cts = new CancellationTokenSource();
            _seekCts = cts;
            _seekInProgress = true;

            Task.Run(() =>
            {
                try
                {
                    cts.Token.ThrowIfCancellationRequested();
                    decoder?.Seek(clamped);
                    cts.Token.ThrowIfCancellationRequested();
                    decoder?.Prefetch(prefetchMs);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    GD.PrintErr($"ActiveAudioPlayback:SeekWorker - {ex.Message}");
                }
                finally
                {
                    if (!cts.IsCancellationRequested)
                    {
                        long us = clamped;
                        bool resume = resumeAfter;
                        // GodotObject: Callable.From needs a live instance; complete via Task continuation
                        // that only touches managed state, then schedule main-thread prime if needed.
                        FinishSeekWorker(cts, us, resume);
                    }
                }
            }, cts.Token);

            GD.Print($"ActiveAudioPlayback:Seek - Queued seek to {clamped} us");
        }
        catch (Exception ex)
        {
            GD.PrintErr($"ActiveAudioPlayback:Seek - {ex.Message}");
            lock (_lock) _fillSuspended = false;
            _seekInProgress = false;
        }
    }

    /// <summary>
    /// Completes an async seek: re-prime streams when resuming play, release fill hold.
    /// Safe to call from a worker — SDL prime and state updates are lock-guarded / fill-thread compatible.
    /// </summary>
    private void FinishSeekWorker(CancellationTokenSource cts, long clampedUs, bool resumeAfter)
    {
        if (cts == null || cts.IsCancellationRequested)
            return;
        if (!ReferenceEquals(_seekCts, cts))
            return;
        if (IsStopped)
        {
            lock (_lock) _fillSuspended = false;
            _seekInProgress = false;
            return;
        }

        try
        {
            if (resumeAfter && !IsStopped)
            {
                ArmDeclickRamp();
                PrefillStreams();
            }
        }
        catch (Exception ex)
        {
            GD.PrintErr($"ActiveAudioPlayback:FinishSeekWorker - {ex.Message}");
        }
        finally
        {
            lock (_lock)
            {
                _fillSuspended = false;
                if (resumeAfter && !IsStopped)
                {
                    IsPaused = false;
                    _pausedAtUs = 0;
                }
                else if (!resumeAfter)
                {
                    // Stay parked at seek target while paused (scrub while paused).
                    _pausedAtUs = clampedUs;
                }
            }
            _seekInProgress = false;
        }
    }

    /// <summary>Cancels any in-flight seek worker without waiting for completion.</summary>
    private void CancelSeekWorker(bool releaseFillHold = true)
    {
        try { _seekCts?.Cancel(); } catch { /* ignore */ }
        try { _seekCts?.Dispose(); } catch { /* ignore */ }
        _seekCts = null;
        _seekInProgress = false;
        if (releaseFillHold)
        {
            lock (_lock)
                _fillSuspended = false;
        }
    }
}
