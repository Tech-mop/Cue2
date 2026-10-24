// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using Cue2.Domain.Cues;
using Godot;

namespace Cue2.Domain.Playback;

/// <summary>
/// Partial: timeline regions, segment end, and rate-curve wall clock for video.
/// Still images skip the region machine and keep a simple hold timer.
/// </summary>
public partial class ActiveVideoPlayback
{
    /// <summary>
    /// Reloads timeline regions from the component. Fallback is a single start–end span.
    /// Call on the main thread before play starts.
    /// </summary>
    private void RebuildPlaybackRegions()
    {
        if (_videoComponent == null || _videoComponent.IsImage)
        {
            _regions = new List<AudioTimelineRegion>();
            _regionWallSeconds = Array.Empty<double>();
            _regionIndex = 0;
            _regionPlayIndex = 1;
            _regionStartUs = _startTimeUs;
            _regionEndUs = _endTimeUs;
            return;
        }

        _videoComponent.EnsureFileEndNode();
        _regions = _videoComponent.GetPlaybackRegions() ?? new List<AudioTimelineRegion>();
        if (_regions.Count == 0)
        {
            double start = _startTimeUs / (double)MicrosecondsPerSecond;
            double end = _endTimeUs == long.MaxValue
                ? (_videoComponent.Metadata?.Duration ?? start)
                : _endTimeUs / (double)MicrosecondsPerSecond;
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
        if (n == 0 || _videoComponent == null)
            return;
        for (int i = 0; i < n; i++)
        {
            var region = _regions[i];
            _regionWallSeconds[i] = _videoComponent.IntegrateTimelineWallClock(
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
        _regionStartUs = (long)(region.StartSeconds * MicrosecondsPerSecond);
        _regionEndUs = (long)(region.EndSeconds * MicrosecondsPerSecond);
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
        double sec = timestampUs / (double)MicrosecondsPerSecond;
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
        if (_videoComponent.IsImage || _regions == null || _regions.Count == 0)
            return true;
        if (_regionIndex < _regions.Count - 1)
            return false;
        var region = _regions[_regionIndex];
        bool morePlays = region.Loop || _regionPlayIndex < Math.Max(1, region.PlayCount);
        if (morePlays)
            return _devampRequested;
        return true;
    }

    /// <summary>File-time end of the current region (or image hold), in microseconds.</summary>
    private long CurrentRegionEndUs() =>
        _videoComponent != null && _videoComponent.IsImage ? _endTimeUs : _regionEndUs;

    /// <summary>
    /// Queues <see cref="HandleRegionBoundary"/> on the main thread at most once until it runs.
    /// The fill loop can sit at the region end for many 4 ms sleeps; without this it would
    /// stack deferred calls and skip plays after the seek finished.
    /// </summary>
    private void QueueRegionBoundary()
    {
        if (_regionBoundaryQueued || _seekInProgress || _segmentEndScheduled)
            return;
        _regionBoundaryQueued = true;
        try
        {
            CallDeferred(nameof(HandleRegionBoundary));
        }
        catch
        {
            _regionBoundaryQueued = false;
        }
    }

    /// <summary>
    /// End of the current region: repeat it, advance to the next, or finish the component play.
    /// Main thread (present path, or deferred from the audio fill loop).
    /// </summary>
    private void HandleRegionBoundary()
    {
        _regionBoundaryQueued = false;
        if (!IsInstanceValid(this))
            return;

        if (_videoComponent != null && _videoComponent.IsImage)
        {
            HandleSegmentEnd();
            return;
        }

        bool seekRegionStart = false;
        bool completeSegment = false;
        long seekUs = 0;
        lock (_lock)
        {
            if (IsStopped || _isExiting || _isFadingOut || _completedEmitted || _segmentEndScheduled
                || _seekInProgress)
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
                    seekRegionStart = true;
                    seekUs = _regionStartUs;
                }
                else if (_regionIndex + 1 < _regions.Count)
                {
                    ApplyRegionUnlocked(_regionIndex + 1);
                    _naturalEndFadeArmed = false;
                    seekRegionStart = true;
                    seekUs = _regionStartUs;
                }
                else
                {
                    completeSegment = true;
                }
            }
        }

        if (seekRegionStart)
        {
            SeekInternal(seekUs, restartClock: true, syncRegion: false);
            return;
        }

        if (completeSegment)
            HandleSegmentEnd();
    }

    /// <summary>
    /// Maps wall-clock elapsed since the last origin onto file time using the live rate curve.
    /// Images and empty region lists keep a constant <see cref="EffectivePlayRate"/>.
    /// </summary>
    private long WallClockToFileUs()
    {
        if (!_wallClock.IsRunning)
            return _wallMediaOriginUs;

        long elapsedUs = _wallClock.Elapsed.Ticks / 10;
        if (_videoComponent == null || _videoComponent.IsImage
            || _regions == null || _regions.Count == 0)
        {
            double rate = EffectivePlayRate;
            if (Math.Abs(rate - 1.0) > 1e-9)
                elapsedUs = (long)(elapsedUs * rate);
            return _wallMediaOriginUs + elapsedUs;
        }

        double wallSec = elapsedUs / (double)MicrosecondsPerSecond;
        double originSec = _wallMediaOriginUs / (double)MicrosecondsPerSecond;
        double regionEnd = _regionEndUs == long.MaxValue
            ? originSec + wallSec + 1.0
            : _regionEndUs / (double)MicrosecondsPerSecond;
        if (regionEnd <= originSec + 1e-12)
            return _wallMediaOriginUs;

        double fileSec = _videoComponent.FileTimeAtWallOffset(originSec, regionEnd, wallSec);
        return (long)(fileSec * MicrosecondsPerSecond);
    }
}
