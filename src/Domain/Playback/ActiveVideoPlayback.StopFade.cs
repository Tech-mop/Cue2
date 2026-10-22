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
/// Partial: stop, fades, volume state and clean-up.
/// </summary>
public partial class ActiveVideoPlayback
{
    /// <summary>
    /// Stops playback. First call with <paramref name="fadeTime"/> &gt; 0 (or cue FadeOutDuration)
    /// starts a fade-out; a second call while fading hard-stops immediately.
    /// </summary>
    /// <param name="fadeTime">Stop-fade seconds from settings; 0 forces immediate stop on first press if the cue has no own fade.</param>
    /// <param name="fadeCurve">Curve for a session/control stop fade. Ignored when using component FadeOutDuration.</param>
    public async Task Stop(double fadeTime = 0.0, FadeCurveType? fadeCurve = null)
    {
        bool needFade = false;
        double fadeDuration = 0;
        FadeCurveType shape = FadeCurveType.Linear;
        bool wasFadingOut;
        lock (_lock)
        {
            if (IsStopped) return;
            wasFadingOut = _isFadingOut;
            _fadeCts?.Cancel();
            if (!wasFadingOut)
            {
                needFade = fadeTime > 0 || (_videoComponent != null && _videoComponent.FadeOutDuration > 0);
                fadeDuration = fadeTime > 0
                    ? fadeTime
                    : (_videoComponent?.FadeOutDuration ?? 0);
                shape = fadeTime > 0
                    ? (fadeCurve ?? FadeCurveType.Linear)
                    : (_videoComponent?.FadeOutCurve ?? FadeCurveType.Linear);
                _isFadingIn = false;
            }
        }

        if (wasFadingOut)
        {
            HardStop();
            return;
        }

        if (needFade)
        {
            await FadeOutAsync(fadeDuration, shape);
            return;
        }

        HardStop();
    }

    /// <summary>
    /// Stops transport immediately. When <paramref name="releaseDisplay"/> is false (natural end),
    /// keeps the last presented frame on the layer host briefly so a follow cue can paint first.
    /// </summary>
    /// <param name="releaseDisplay">True = full teardown now; false = hold frame then deferred clean.</param>
    private void HardStop(bool releaseDisplay = true)
    {
        lock (_lock)
        {
            if (IsStopped) return;
            IsStopped = true;
            IsPaused = false;
            _isPlaying = false;
        }
        SetProcess(false);
        CancelSeekWorker();
        StopVideoPrefetchLoop(); // non-blocking; Clean picks up the still-running workers
        StopAudioFillLoop();
        _wallClock.Stop();

        if (releaseDisplay)
        {
            Clean();
            return;
        }

        // Natural end: report completion first while the last frame is still on the layer,
        // then release after a short handoff window (follow can start/present in between).
        _holdingLastFrame = true;
        EmitCompletedIfNeeded();
        _ = ReleaseDisplayAfterHandoffAsync();
    }

    /// <summary>
    /// Emits <see cref="Completed"/> once (safe to call from natural-end hold path).
    /// </summary>
    private void EmitCompletedIfNeeded()
    {
        lock (_lock)
        {
            if (_completedEmitted) return;
            _completedEmitted = true;
        }
        try
        {
            EmitSignal(SignalName.Completed);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"ActiveVideoPlayback:EmitCompletedIfNeeded - {ex.Message}");
        }
    }

    /// <summary>
    /// Waits a couple of process frames so a follow-armed next cue can present, then full clean.
    /// </summary>
    private async Task ReleaseDisplayAfterHandoffAsync()
    {
        try
        {
            var tree = GetTree();
            if (tree != null)
            {
                for (int i = 0; i < NaturalEndDisplayHoldFrames; i++)
                    await ToSignal(tree, SceneTree.SignalName.ProcessFrame);
            }
            else
            {
                await Task.Yield();
            }
        }
        catch (Exception ex)
        {
            GD.PrintErr($"ActiveVideoPlayback:ReleaseDisplayAfterHandoffAsync - {ex.Message}");
        }

        if (_isDisposed) return;
        // Full teardown (Clean is idempotent regarding completed signal).
        Clean();
    }

    /// <summary>
    /// True while the fade-in that owns <paramref name="cts"/> is still the active fade:
    /// not cancelled, and not replaced by a Stop or fade-out. Each fade owns its own token
    /// source; <see cref="_fadeCts"/> is only the handle Stop and Clean cancel.
    /// </summary>
    private bool IsFadeInCurrent(CancellationTokenSource cts)
    {
        lock (_lock)
            return _isFadingIn && ReferenceEquals(_fadeCts, cts) && !cts.IsCancellationRequested;
    }

    public async Task FadeInAsync(double duration)
    {
        CancellationTokenSource cts;
        lock (_lock)
        {
            if (_isFadingIn || _isFadingOut) return;
            _isFadingIn = true;
            cts = new CancellationTokenSource();
            _fadeCts = cts;
            // Arm silent state before PlayAsync presents or prefills audio.
            _fadeAlpha = 0f;
            _volume = 0f;
        }

        float startVol = 0f;
        float endVol = 1.0f;
        float startAlpha = 0f;
        ApplyOpacityModulate();
        var token = cts.Token;
        Stopwatch timer = Stopwatch.StartNew();
        await PlayAsync();

        try
        {
            // Re-checked after PlayAsync and every delay: a Stop or fade-out may have taken over
            // meanwhile, and this loop must then never write level or opacity again.
            while (timer.Elapsed.TotalSeconds < duration && IsFadeInCurrent(cts))
            {
                float t = (float)(timer.Elapsed.TotalSeconds / duration);
                float shaped = FadeCurve.Evaluate(t, _videoComponent?.FadeInCurve ?? FadeCurveType.Linear);
                SetVolume(Mathf.Lerp(startVol, endVol, shaped));
                _fadeAlpha = Mathf.Lerp(startAlpha, 1.0f, shaped);
                ApplyOpacityModulate();
                await Task.Delay(FadeUpdateIntervalMs, token);
            }
            if (IsFadeInCurrent(cts))
            {
                SetVolume(endVol);
                _fadeAlpha = 1.0f;
                ApplyOpacityModulate();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            GD.PrintErr($"ActiveVideoPlayback:FadeInAsync - {ex.Message}");
        }
        finally
        {
            // Only clear state this fade still owns; a newer fade keeps its own token.
            lock (_lock)
            {
                if (ReferenceEquals(_fadeCts, cts))
                {
                    _isFadingIn = false;
                    _fadeCts = null;
                }
            }
            cts.Dispose();
        }
    }

    public async Task FadeOutAsync(double duration, FadeCurveType? curve = null)
    {
        if (duration <= 0)
        {
            HardStop();
            return;
        }

        CancellationTokenSource cts;
        lock (_lock)
        {
            if (IsStopped) return;
            if (_isFadingOut) return;
            _isFadingIn = false;
            _isFadingOut = true;
            // Cancel (not dispose) a running fade-in: it still reads its own token and
            // disposes it itself.
            _fadeCts?.Cancel();
            cts = new CancellationTokenSource();
            _fadeCts = cts;
        }

        var shape = curve ?? _videoComponent?.FadeOutCurve ?? FadeCurveType.Linear;
        float startVol = _volume;
        float startAlpha = _fadeAlpha;
        var token = cts.Token;
        Stopwatch timer = Stopwatch.StartNew();

        try
        {
            while (timer.Elapsed.TotalSeconds < duration && !token.IsCancellationRequested)
            {
                float t = (float)(timer.Elapsed.TotalSeconds / duration);
                float shaped = FadeCurve.Evaluate(t, shape);
                SetVolume(Mathf.Lerp(startVol, 0f, shaped));
                _fadeAlpha = Mathf.Lerp(startAlpha, 0f, shaped);
                // Match FadeInAsync: push visual alpha immediately (images do not re-present frames).
                // Deferred so TextureRect updates always land on the main thread after await.
                CallDeferred(nameof(ApplyOpacityModulate));
                await Task.Delay(FadeUpdateIntervalMs, token);
            }
            if (!token.IsCancellationRequested)
            {
                SetVolume(0f);
                _fadeAlpha = 0f;
                CallDeferred(nameof(ApplyOpacityModulate));
                HardStop();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            GD.PrintErr($"ActiveVideoPlayback:FadeOutAsync - {ex.Message}");
        }
        finally
        {
            lock (_lock)
            {
                _isFadingOut = false;
                if (ReferenceEquals(_fadeCts, cts))
                    _fadeCts = null;
            }
            try { cts.Dispose(); } catch { /* ignore */ }
        }
    }

    public void SetVolume(float volume)
    {
        lock (_lock) _volume = Mathf.Clamp(volume, 0f, 1f);
    }

    public bool IsFadingOut
    {
        get { lock (_lock) return _isFadingOut; }
    }

    public bool IsFadingIn
    {
        get { lock (_lock) return _isFadingIn; }
    }

    public float CurrentVolume
    {
        get { lock (_lock) return _volume; }
    }

    public int CurrentPlayCount
    {
        get { lock (_lock) return _currentPlayCount; }
    }

    public double GetDuration()
    {
        if (_videoComponent.IsImage)
        {
            // 0 = until stopped (no finite progress span).
            return _videoComponent.Duration > 0 ? _videoComponent.Duration : 0;
        }

        if (_videoDecoder?.Info != null && _videoDecoder.Info.DurationUs > 0)
            return _videoDecoder.Info.DurationUs / (double)MicrosecondsPerSecond;
        return _videoComponent.Metadata?.Duration ?? 0;
    }

    public void Clean()
    {
        lock (_lock)
        {
            if (_isDisposed) return;
            _isExiting = true;
            IsStopped = true;
            _isPlaying = false;
            _holdingLastFrame = false;
        }

        SetProcess(false);
        var seekWorker = _seekTask;
        CancelSeekWorker();
        // Non-blocking stop: cancel, never Task.Wait on main.
        var prefetchWorker = StopVideoPrefetchLoop();
        var fillWorker = StopAudioFillLoop();
        _wallClock.Stop();

        if (DeviceStreams != null)
        {
            try { _audioDevices?.NotifyPlaybackCompleted(this); } catch { /* ignore */ }
        }
        // Detach, then destroy. Safe while the fill worker is still exiting: after the detach
        // (under IoLock) it skips the handles.
        AudioStreamBindingSet.Destroy(_streamBindings.DetachAll(DeviceStreams, DeviceStreamChannels));

        // Prefer signaling completion before tearing down the display when Clean is the
        // first completion path (user Stop). Natural end already emitted while holding frame.
        EmitCompletedIfNeeded();

        // Display, layers and subtitles are main-thread only: release now, after Completed.
        ReleaseDisplayResources();

        lock (_lock)
        {
            _isDisposed = true;
        }

        bool inTree = IsInstanceValid(this) && IsInsideTree();

        // Decoders and mix buffers are read by the workers: release them, return LOH memory
        // and free this object once every worker has exited. Inline when none is running.
        PlaybackWorkers.RunAfter(() =>
        {
            ReleaseWorkerResources();
            MediaMemory.ReclaimIfNeeded();
            FreeAfterTeardown(inTree);
        }, seekWorker, prefetchWorker, fillWorker);
    }

    /// <summary>
    /// Frees this playback after teardown. In the tree it uses QueueFree (Clean often runs inside
    /// signal dispatch); otherwise C# Free via a deferred Callable. Skips if already freed
    /// (for example with its ActiveCue bar).
    /// </summary>
    private void FreeAfterTeardown(bool wasInTree)
    {
        if (!IsInstanceValid(this))
            return;
        if (wasInTree)
            CallDeferred(Node.MethodName.QueueFree);
        else
            Callable.From(FreeDeferred).CallDeferred();
    }

    /// <summary>
    /// Invokes <see cref="GodotObject.Free"/> if this instance is still valid (not in tree).
    /// </summary>
    private void FreeDeferred()
    {
        if (IsInstanceValid(this))
            Free();
    }
}
