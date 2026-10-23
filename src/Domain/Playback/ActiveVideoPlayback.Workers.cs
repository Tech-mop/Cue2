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
/// Partial: video prefetch and embedded-audio fill workers, de-click, prefill and audio push.
/// </summary>
public partial class ActiveVideoPlayback
{
    private void StartVideoPrefetchLoop()
    {
        // The new worker waits for a cancelled one on its own thread, not on main.
        var previous = StopVideoPrefetchLoop();
        _videoPrefetchCts = new CancellationTokenSource();
        var token = _videoPrefetchCts.Token;
        _videoPrefetchTask = PlaybackWorkers.StartLoop(() =>
        {
            PlaybackWorkers.WaitBounded(500, previous);
            try
            {
                const int wakeTimeoutMs = 20;
                while (!token.IsCancellationRequested)
                {
                    bool paused;
                    lock (_lock) paused = IsPaused || IsStopped || _isExiting || !_isPlaying;
                    VideoSourceDecoder decoder = _videoDecoder;
                    if (decoder == null)
                    {
                        // Nothing to wait on: sleep so the loop does not spin until cancelled.
                        Thread.Sleep(wakeTimeoutMs);
                        continue;
                    }
                    if (decoder.IsDisposed)
                        break;
                    if (paused || _seekInProgress)
                    {
                        decoder.WaitForPrefetchWake(wakeTimeoutMs);
                        continue;
                    }

                    decoder.SetPrefetchLowWater(_presentTuning.PrefetchLowWater);
                    if (decoder.BufferedFrames < _presentTuning.PrefetchLowWater &&
                        !decoder.EndOfStream)
                    {
                        decoder.Prefetch(_presentTuning.PrefetchTarget);
                    }

                    decoder.WaitForPrefetchWake(wakeTimeoutMs);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                GD.PrintErr($"ActiveVideoPlayback:VideoPrefetch - {ex.Message}");
            }
        }, token);
    }

    /// <summary>
    /// Cancels the video prefetch worker without waiting. Main thread only.
    /// </summary>
    /// <returns>The worker if it (or an earlier cancelled one) is still running, else null.</returns>
    private Task StopVideoPrefetchLoop()
    {
        var task = _videoPrefetchTask;
        var cts = _videoPrefetchCts;
        _videoPrefetchTask = null;
        _videoPrefetchCts = null;
        Task retired = RetireWorker(task, cts, ref _retiringPrefetchTask);
        try { _videoDecoder?.WakePrefetch(); } catch { /* decoder already gone */ }
        return retired;
    }

    private void StartAudioFillLoop()
    {
        // Never two fill loops on the same streams: the new one waits on its own thread.
        var previous = StopAudioFillLoop();
        _audioFillCts = new CancellationTokenSource();
        var token = _audioFillCts.Token;
        _audioFillTask = PlaybackWorkers.StartLoop(() =>
        {
            PlaybackWorkers.WaitBounded(500, previous);
            AudioFillLoop(token);
        }, token);
    }

    /// <summary>
    /// Cancels the embedded-audio fill worker without waiting. Main thread only.
    /// </summary>
    /// <returns>The worker if it (or an earlier cancelled one) is still running, else null.</returns>
    private Task StopAudioFillLoop()
    {
        var task = _audioFillTask;
        var cts = _audioFillCts;
        _audioFillTask = null;
        _audioFillCts = null;
        return RetireWorker(task, cts, ref _retiringAudioFillTask);
    }

    /// <summary>
    /// Cancels <paramref name="task"/> and disposes its token source once it has exited.
    /// Remembers a still-running worker in <paramref name="retiring"/>.
    /// </summary>
    private static Task RetireWorker(Task task, CancellationTokenSource cts, ref Task retiring)
    {
        try { cts?.Cancel(); } catch { /* ignore */ }
        if (PlaybackWorkers.IsRunning(task))
        {
            retiring = task;
            task.ContinueWith(_ => { try { cts?.Dispose(); } catch { /* ignore */ } }, TaskScheduler.Default);
            return task;
        }

        try { cts?.Dispose(); } catch { /* ignore */ }
        return PlaybackWorkers.IsRunning(retiring) ? retiring : null;
    }

    private void AudioFillLoop(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                // Do not call RefreshAudioTuning here — fill runs off the main thread and
                // GetNodeOrNull is not allowed. Use last main-thread snapshot of _audioTuning.

                bool hold;
                lock (_lock) hold = IsPaused || IsStopped || _isExiting || _audioFillSuspended;
                if (hold)
                {
                    Thread.Sleep(AudioFillSleepMs);
                    continue;
                }

                var bindings = _streamBindings.Snapshot;
                var audioDecoder = _audioDecoder; // one read per pass; main may clear the field
                if (bindings.Length == 0 || audioDecoder == null)
                {
                    Thread.Sleep(AudioFillSleepMs);
                    continue;
                }

                bool anyNeed = false;
                int maxNeedFrames = 0;
                foreach (var b in bindings)
                {
                    long queued = _streamBindings.GetQueued(b);
                    if (queued < 0) continue;
                    _startTrace.NoteAudioQueued(queued);
                    int outCh = GetStreamChannels(b);
                    int bpf = outCh * sizeof(float);
                    if (bpf <= 0) continue;
                    long lowWater = SourceSampleRate * _audioTuning.LowWaterMs / 1000L * bpf;
                    long target = SourceSampleRate * _audioTuning.TargetBufferMs / 1000L * bpf;
                    if (queued < lowWater)
                    {
                        anyNeed = true;
                        int need = (int)Math.Max(1, (target - queued) / bpf);
                        if (need > maxNeedFrames) maxNeedFrames = need;
                    }
                }

                if (!anyNeed)
                {
                    Thread.Sleep(AudioFillSleepMs);
                    continue;
                }

                int maxFrames = _audioSrcBuffer.Length / SourceChannels;
                int framesToRead = Math.Min(maxNeedFrames, maxFrames);

                // Don't fill audio past video end region
                long pos = audioDecoder.PositionUs;
                if (pos >= _endTimeUs)
                {
                    Thread.Sleep(AudioFillSleepMs);
                    continue;
                }

                int frames = audioDecoder.Read(_audioSrcBuffer.AsSpan(), framesToRead, token);
                if (frames <= 0)
                {
                    Thread.Sleep(AudioFillSleepMs);
                    continue;
                }

                // Discard if a seek started during Read — stale PCM must not hit the cleared stream.
                lock (_lock)
                {
                    if (IsPaused || IsStopped || _isExiting || _audioFillSuspended)
                        continue;
                }

                PushMixedAudioFrames(frames, audioDecoder);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            GD.PrintErr($"ActiveVideoPlayback:AudioFillLoop - {ex.Message}");
        }
    }

    private int GetStreamChannels(AudioStreamBinding binding) =>
        binding.Channels > 0 ? binding.Channels : SourceChannels;

    /// <inheritdoc />
    public void PublishStreamBindings() =>
        _streamBindings.Publish(DeviceStreams, DeviceStreamChannels,
            id => _audioDevices?.GetAudioDeviceByLogicalId(id)?.Name);

    /// <summary>
    /// Arms a raised-cosine fade-in so the next PCM pushed after silence does not click.
    /// Uses the last main-thread <see cref="_audioTuning"/> snapshot (safe from fill threads).
    /// </summary>
    private void ArmDeclickRamp() => _declick.Arm(SourceSampleRate, _audioTuning.DeclickRampMs);

    /// <summary>
    /// Pulls current show audio latency / declick settings into fill knobs.
    /// Must only run on the main thread (uses <see cref="Node.GetNodeOrNull{T}"/>).
    /// </summary>
    private void RefreshAudioTuning()
    {
        try
        {
            var settings = GetNodeOrNull<GlobalData>("/root/GlobalData")?.Settings;
            if (settings != null)
                _audioTuning = settings.GetAudioPresentTuning();
            else
                _audioTuning = AudioPresentTuning.ForMode(
                    AudioLatencyMode.Balanced, Settings.DefaultAudioDeclickMs);
        }
        catch
        {
            _audioTuning = AudioPresentTuning.ForMode(
                AudioLatencyMode.Balanced, Settings.DefaultAudioDeclickMs);
        }
    }

    /// <summary>
    /// Fills each bound SDL stream up to the configured target buffer before play/after seek.
    /// Prevents the device from consuming an empty queue (primary underrun click source).
    /// </summary>
    /// <summary>Smallest queue over the bound streams in bytes, or -1 when none can be read.</summary>
    private long MinQueuedBytes()
    {
        long min = -1;
        foreach (var b in _streamBindings.Snapshot)
        {
            long queued = _streamBindings.GetQueued(b);
            if (queued < 0) continue;
            if (min < 0 || queued < min) min = queued;
        }
        return min;
    }

    private void PrefillAudioStreams()
    {
        if (_audioDecoder == null || !_streamBindings.HasStreams)
            return;
        if (_audioSrcBuffer == null || SourceSampleRate <= 0 || SourceChannels <= 0)
            return;

        // Cap work: ~target buffer / chunk size iterations; enough for one full prime.
        // Uses _audioTuning last refreshed on the main thread (never call GetNode here).
        const int maxIterations = 48;
        for (int iter = 0; iter < maxIterations; iter++)
        {
            int maxNeedFrames = 0;
            foreach (var b in _streamBindings.Snapshot)
            {
                long queued = _streamBindings.GetQueued(b);
                if (queued < 0) continue;
                int outCh = GetStreamChannels(b);
                int bpf = outCh * sizeof(float);
                if (bpf <= 0) continue;
                long target = SourceSampleRate * _audioTuning.TargetBufferMs / 1000L * bpf;
                if (queued < target)
                {
                    int need = (int)Math.Max(1, (target - queued) / bpf);
                    if (need > maxNeedFrames) maxNeedFrames = need;
                }
            }

            if (maxNeedFrames == 0)
                break;

            if (_audioDecoder.PositionUs >= _endTimeUs)
                break;

            int maxFrames = _audioSrcBuffer.Length / SourceChannels;
            int framesToRead = Math.Min(maxNeedFrames, maxFrames);
            int frames = _audioDecoder.Read(_audioSrcBuffer.AsSpan(), framesToRead);
            if (frames <= 0)
                break;

            PushMixedAudioFrames(frames, _audioDecoder);
        }
    }

    /// <summary>
    /// Mixes and queues <paramref name="frames"/> just read from <paramref name="decoder"/>,
    /// then publishes the decoder position as the end of the queued PCM. The master clock
    /// reads that value with the SDL queue as one pair, so it never sees the decoder already
    /// advanced while the PCM is not queued yet (which made it jump ahead by a whole chunk).
    /// </summary>
    private void PushMixedAudioFrames(int frames, AudioSourceDecoder decoder)
    {
        var bindings = _streamBindings.Snapshot;
        if (bindings.Length == 0 || _isExiting) return;
        long queuedEndUs = decoder?.PositionUs ?? 0;

        float masterVol;
        float componentVol;
        float pan;
        lock (_lock)
        {
            // Cue fade envelope × session master (volume + runtime mute from AudioDevices).
            masterVol = _volume * (_audioDevices?.GetEffectiveSessionMasterLinear() ?? 1f);
            // Prefer runtime control-fade override, else component audio level.
            if (_runtimeLevelLinear.HasValue)
                componentVol = _runtimeLevelLinear.Value;
            else if (_videoComponent.UseAudio)
                componentVol = AudioMixMatrix.ClampComponentGainLinear(_videoComponent.AudioVolume);
            else
                componentVol = AudioMixMatrix.ClampComponentGainLinear((float)_videoComponent.Volume);
            // Stereo pan only; mono / multi-channel ignore (Mix applies identity).
            pan = SourceChannels == 2
                ? (_runtimePan ?? Mathf.Clamp(_videoComponent.Pan, -1f, 1f))
                : 0f;
        }
        bool isDirect = !string.IsNullOrEmpty(DirectOutput);

        Interlocked.Increment(ref _audioClockSeq);
        try
        {
            AudioOutputMix.MixAndPut(
                _streamBindings, bindings, _audioSrcBuffer.AsSpan(0, frames * SourceChannels), frames, SourceChannels,
                ref _audioMixBuffer, masterVol, componentVol, pan, Routing, Patch, isDirect,
                ref _declick, _audioDevices, ref _meterPeakBits);
        }
        finally
        {
            Volatile.Write(ref _audioQueuedEndUs, queuedEndUs);
            Interlocked.Increment(ref _audioClockSeq);
        }
    }
}
