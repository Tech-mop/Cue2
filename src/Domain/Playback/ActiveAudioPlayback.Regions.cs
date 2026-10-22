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
/// Partial: timeline regions, segment end, natural end fade and completion.
/// </summary>
public partial class ActiveAudioPlayback
{
    /// <summary>
    /// Reloads timeline regions from the component. Fallback is a single start–end span.
    /// Call on the main thread before fill starts.
    /// </summary>
    private void RebuildPlaybackRegions()
    {
        _audioComponent?.EnsureFileEndNode();
        _regions = _audioComponent?.GetPlaybackRegions() ?? new List<AudioTimelineRegion>();
        if (_regions.Count == 0)
        {
            double start = _startTimeUs / 1_000_000.0;
            double end = _endTimeUs == long.MaxValue
                ? (_audioComponent.Metadata?.Duration ?? start)
                : _endTimeUs / 1_000_000.0;
            _regions.Add(AudioTimelineRegion.Create(0, start, Math.Max(start, end)));
        }

        CacheRegionWallSeconds();
    }

    /// <summary>Stores one-play wall-clock for each playback region.</summary>
    private void CacheRegionWallSeconds()
    {
        int n = _regions?.Count ?? 0;
        if (_regionWallSeconds == null || _regionWallSeconds.Length != n)
            _regionWallSeconds = new double[Math.Max(n, 1)];
        if (n == 0 || _audioComponent == null)
            return;
        for (int i = 0; i < n; i++)
        {
            var region = _regions[i];
            _regionWallSeconds[i] = _audioComponent.IntegrateTimelineWallClock(
                region.StartSeconds, region.EndSeconds);
        }
    }

    /// <summary>Wall-clock of one play of region <paramref name="index"/>.</summary>
    private double RegionWallSeconds(int index)
    {
        if (_regionWallSeconds != null && index >= 0 && index < _regionWallSeconds.Length)
            return Math.Max(0.0, _regionWallSeconds[index]);
        return 0.0;
    }

    /// <summary>
    /// Selects a region and resets its play index. Caller holds <see cref="_lock"/> or is on the ctor path.
    /// </summary>
    private void ApplyRegionUnlocked(int index)
    {
        if (_regions == null || _regions.Count == 0)
            return;
        index = Math.Clamp(index, 0, _regions.Count - 1);
        var region = _regions[index];
        _regionIndex = index;
        _regionPlayIndex = 1;
        _regionStartUs = (long)(region.StartSeconds * 1_000_000.0);
        _regionEndUs = (long)(region.EndSeconds * 1_000_000.0);
        if (_regionEndUs <= _regionStartUs)
            _regionEndUs = _regionStartUs + 1;
    }

    /// <summary>
    /// Chooses the region that contains <paramref name="timestampUs"/>.
    /// </summary>
    private void SyncRegionForPositionUnlocked(long timestampUs)
    {
        if (_regions == null || _regions.Count == 0)
            return;
        double sec = timestampUs / 1_000_000.0;
        int found = 0;
        for (int i = 0; i < _regions.Count; i++)
        {
            if (sec + 1e-6 >= _regions[i].StartSeconds)
                found = i;
            if (sec < _regions[i].EndSeconds - 1e-6)
            {
                found = i;
                break;
            }
        }
        ApplyRegionUnlocked(found);
    }

    private bool IsOnLastRegionPlayUnlocked()
    {
        if (_regions == null || _regions.Count == 0)
            return true;
        if (_regionIndex < _regions.Count - 1)
            return false;
        var region = _regions[_regionIndex];
        bool morePlays = region.Loop || _regionPlayIndex < Math.Max(1, region.PlayCount);
        if (morePlays)
            return _devampRequested;
        return true;
    }

    /// <summary>
    /// End of the current region: repeat it, advance to the next, or finish the component play.
    /// </summary>
    private void HandleRegionBoundary()
    {
        bool seekRegionStart = false;
        bool completeSegment = false;
        lock (_lock)
        {
            if (IsStopped || _endScheduled || _isFadingOut)
                return;
            if (_regions == null || _regionIndex < 0 || _regionIndex >= _regions.Count)
            {
                completeSegment = true;
            }
            else
            {
                var region = _regions[_regionIndex];
                bool morePlays = region.Loop || _regionPlayIndex < Math.Max(1, region.PlayCount);
                if (morePlays && _devampRequested)
                {
                    _devampRequested = false;
                    morePlays = false;
                }
                if (morePlays)
                {
                    _regionPlayIndex++;
                    _naturalEndFadeArmed = false;
                    _fillSuspended = true;
                    seekRegionStart = true;
                }
                else if (_regionIndex + 1 < _regions.Count)
                {
                    ApplyRegionUnlocked(_regionIndex + 1);
                    _naturalEndFadeArmed = false;
                    _fillSuspended = true;
                    seekRegionStart = true;
                }
                else
                {
                    completeSegment = true;
                }
            }
        }

        if (seekRegionStart)
        {
            RestartAtRegionStart();
            return;
        }

        if (completeSegment)
            HandleSegmentEnd();
    }

    /// <summary>
    /// Seeks the decoder to the current region's start and re-primes streams.
    /// </summary>
    private void RestartAtRegionStart()
    {
        try
        {
            _streamBindings.ClearAll();

            long startUs;
            lock (_lock)
                startUs = _regionStartUs;
            Decoder.Seek(startUs);
            Decoder.Prefetch(_audioTuning.PrefetchMs);
            _ratePitch.Reset();
            _framesDelivered = 0;
            ArmDeclickRamp();
            PrefillStreams();
        }
        finally
        {
            lock (_lock)
                _fillSuspended = false;
        }
    }

    private void HandleSegmentEnd()
    {
        bool scheduleComplete = false;
        lock (_lock)
        {
            // _endScheduled covers "already finishing"
            if (IsStopped || _endScheduled || _isFadingOut) return;

            bool morePlays = _audioComponent.Loop || _currentPlayCount < EffectivePlayCount;
            if (morePlays && _devampRequested)
            {
                _devampRequested = false;
                morePlays = false;
            }
            if (morePlays)
            {
                _currentPlayCount++;
                _naturalEndFadeArmed = false;
                GD.Print($"ActiveAudioPlayback:HandleSegmentEnd - Loop/play {_currentPlayCount}/{EffectivePlayCount}");
                _fillSuspended = true;
            }
            else
            {
                // Mark finishing so fill loop stops re-entering (IsStopped set in Clean).
                // Not the Completed flag: a Stop in this window must still emit Completed.
                _endScheduled = true;
                scheduleComplete = true;
            }
        }

        if (!scheduleComplete)
        {
            // Loop path: seek + re-prime outside lock; hold fill via _fillSuspended.
            try
            {
                _streamBindings.ClearAll();
                long loopStartUs;
                lock (_lock)
                {
                    ApplyRegionUnlocked(0);
                    loopStartUs = _regionStartUs;
                }
                Decoder.Seek(loopStartUs);
                Decoder.Prefetch(_audioTuning.PrefetchMs);
                _ratePitch.Reset();
                _framesDelivered = 0;
                ArmDeclickRamp();
                PrefillStreams();
            }
            finally
            {
                lock (_lock) _fillSuspended = false;
            }
            return;
        }

        try { _fillCts?.Cancel(); } catch { /* ignore */ }

        GD.Print("ActiveAudioPlayback:HandleSegmentEnd - Playback completed");
        try
        {
            CallDeferred(nameof(CompleteFromEnd));
        }
        catch
        {
            CompleteFromEnd();
        }
    }

    /// <summary>
    /// Starts component end-fade when remaining content time is within <see cref="AudioComponent.FadeOutDuration"/>.
    /// Runs on the fill thread via deferred call so fade timing stays on the main thread.
    /// </summary>
    /// <param name="posUs">Current decoder/media position in microseconds.</param>
    private void TryArmNaturalEndFade(long posUs)
    {
        lock (_lock)
        {
            if (IsStopped || IsPaused || _isFadingOut || _isFadingIn || _endScheduled
                || _naturalEndFadeArmed)
                return;
            // Only the last playcount of a finite cue ends with a fade (infinite loop never auto-fades
            // unless Devamp has marked this pass as the last).
            if ((_audioComponent.Loop || _currentPlayCount < EffectivePlayCount) && !_devampRequested)
                return;
            if (!IsOnLastRegionPlayUnlocked())
                return;
            double fadeSec = _audioComponent.FadeOutDuration;
            if (fadeSec <= 1e-9 || _regionEndUs == long.MaxValue)
                return;

            long remainingUs = _regionEndUs - posUs;
            // Fade duration is wall-clock; file time advances at PlayRate.
            long fadeUs = (long)(fadeSec * CurrentPlayRate() * 1_000_000.0);
            if (remainingUs > fadeUs)
                return;

            _naturalEndFadeArmed = true;
        }

        try
        {
            CallDeferred(nameof(BeginNaturalEndFade));
        }
        catch
        {
            lock (_lock) _naturalEndFadeArmed = false;
        }
    }

    /// <summary>
    /// Main-thread entry for natural end-fade (last seconds of content).
    /// Fade length is clamped to remaining content so the cue still ends at the out point.
    /// </summary>
    private void BeginNaturalEndFade()
    {
        if (!IsInstanceValid(this)) return;

        double fadeDuration;
        lock (_lock)
        {
            if (IsStopped || IsPaused || _isFadingOut || _endScheduled)
            {
                _naturalEndFadeArmed = false;
                return;
            }
            if (((_audioComponent.Loop || _currentPlayCount < EffectivePlayCount) && !_devampRequested)
                || !IsOnLastRegionPlayUnlocked())
            {
                _naturalEndFadeArmed = false;
                return;
            }

            double configured = _audioComponent.FadeOutDuration;
            if (configured <= 1e-9 || _regionEndUs == long.MaxValue)
            {
                _naturalEndFadeArmed = false;
                return;
            }

            long remainingUs = Math.Max(0, _regionEndUs - GetPlaybackPositionUs());
            double remainingSec = remainingUs / 1_000_000.0 / CurrentPlayRate();

            // Clamp to remaining content so fade completes at the natural end (not after).
            fadeDuration = Math.Max(remainingSec, 1e-3);
            fadeDuration = Math.Min(fadeDuration, configured);
        }

        GD.Print($"ActiveAudioPlayback:BeginNaturalEndFade - Starting end fade ({fadeDuration:F3}s)");
        _ = FadeOutAsync(fadeDuration);
    }

    private void CompleteFromEnd()
    {
        if (!IsInstanceValid(this)) return;

        // End-fade already in progress — FadeOutAsync will HardStop when done.
        if (IsFadingOut) return;

        double residualFade = 0;
        lock (_lock)
        {
            if (IsStopped) return;
            // Safety net if end was hit without early arm (seek into tail, timing miss).
            if (!_audioComponent.Loop && _currentPlayCount >= EffectivePlayCount)
                residualFade = Math.Max(0, _audioComponent.FadeOutDuration);
            _endScheduled = false;
        }

        if (residualFade > 1e-9)
        {
            GD.Print($"ActiveAudioPlayback:CompleteFromEnd - Residual end fade ({residualFade:F3}s)");
            _ = FadeOutAsync(residualFade);
            return;
        }

        // Natural end without fade: Clean (do not use Stop — session stop-fade is for user Stop).
        Clean();
    }
}
