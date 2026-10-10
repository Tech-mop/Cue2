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
/// Software control layer for an active audio cue.
/// Owns transport (play/pause/seek/loop/playcount), volume/fades, and matrix mixing.
/// Pulls PCM from <see cref="AudioSourceDecoder"/> and tops up SDL streams by queue watermark.
/// </summary>
public partial class ActiveAudioPlayback : GodotObject, IAudioPlayback, IComponentLevel
{
    private const int FillLoopSleepMs = 4;

    /// <summary>Fill / prefetch / de-click knobs from show <see cref="Settings"/>.</summary>
    private AudioPresentTuning _audioTuning = AudioPresentTuning.ForMode(
        AudioLatencyMode.Balanced, Settings.DefaultAudioDeclickMs);

    /// <summary>Pull-based audio source decoder.</summary>
    public AudioSourceDecoder Decoder { get; private set; }

    public AudioOutputPatch Patch { get; set; }
    public CuePatch Routing { get; set; }
    public string DirectOutput { get; set; }
    public Dictionary<uint, IntPtr> DeviceStreams { get; set; }
    public Dictionary<uint, int> DeviceStreamChannels { get; set; }
    public int SourceChannels { get; set; }
    public int SourceSampleRate { get; set; }
    public int SourceBytesPerFrame { get; set; }
    public SDL.AudioFormat SourceFormat { get; set; } = SDL.AudioFormat.AudioF32LE;

    private readonly AudioComponent _audioComponent;
    private readonly AudioDevices _audioDevices;
    private readonly object _lock = new object();

    /// <summary>Master fade envelope (0–1). Starts at 0 so streams cannot emit at full level before Play/FadeIn arms.</summary>
    private float _volume = 0f;
    private bool _isFadingOut;
    private bool _isFadingIn;
    /// <summary>True once natural end-fade has been scheduled (prevents repeated deferred arms).</summary>
    private bool _naturalEndFadeArmed;
    public bool IsStopped;
    public bool IsPaused;
    public bool IsSeeking;

    /// <summary>
    /// When set, replaces <see cref="AudioComponent.Volume"/> for this playback only (control fades).
    /// </summary>
    private float? _runtimeLevelLinear;
    private int _meterPeakBits;
    private float _meterDisplay;

    /// <summary>
    /// When set, replaces <see cref="AudioComponent.Pan"/> for this playback only (control fades).
    /// </summary>
    private float? _runtimePan;

    /// <summary>
    /// When set, replaces <see cref="AudioComponent.PlayRate"/> for this playback only (control fades).
    /// Timeline relative rate still applies.
    /// </summary>
    private double? _runtimePlayRate;

    /// <summary>
    /// When set, replaces <see cref="AudioComponent.PitchCents"/> for this playback only (control fades).
    /// Timeline relative pitch still applies.
    /// </summary>
    private float? _runtimePitchCents;

    /// <summary>When true, the next region or component loop is skipped after the current pass.</summary>
    private bool _devampRequested;

    /// <summary>True when <see cref="Routing"/> is a private clone (safe to mutate for control fades).</summary>
    private bool _routingIsPrivate;

    private CancellationTokenSource _fadeCts;
    private CancellationTokenSource _fillCts;
    private Task _fillTask;

    /// <summary>Cancels in-flight seek+prefetch workers when a newer seek supersedes them.</summary>
    private CancellationTokenSource _seekCts;
    private volatile bool _seekInProgress;
    /// <summary>Target media time for the in-flight seek (UI/position hold).</summary>
    private long _seekTargetUs;

    /// <summary>
    /// True while an async decoder seek is in flight. Progress UI should hold the scrub target
    /// and not sample the live decoder position until this clears.
    /// </summary>
    public bool IsDecoderSeeking => _seekInProgress;

    private long _startTimeUs;
    private long _endTimeUs;
    private bool _useCustomEnd;
    private List<AudioTimelineRegion> _regions = new();
    /// <summary>Cached wall-clock of one play of each region (rate curve).</summary>
    private double[] _regionWallSeconds = Array.Empty<double>();
    private int _regionIndex;
    private int _regionPlayIndex = 1;
    private long _regionStartUs;
    private long _regionEndUs;
    private int _currentPlayCount = 1;
    public int EffectivePlayCount;
    private bool _hasStarted;
    private long _pausedAtUs;
    private long _framesDelivered; // sample-frames delivered to mix since last seek/start

    private float[] _srcBuffer;
    private float[] _mixBuffer;
    private readonly AudioRatePitchProcessor _ratePitch = new();

    /// <summary>Frames remaining in the post-start/seek de-click ramp (0 = inactive).</summary>
    private int _declickFramesRemaining;
    /// <summary>Total frames for the current de-click ramp (fixed when armed).</summary>
    private int _declickRampTotalFrames;
    /// <summary>
    /// When true, the fill loop must not Put to SDL (seek/clear in progress).
    /// Separate from public <see cref="IsSeeking"/> (UI scrub preview holds that for the whole drag).
    /// </summary>
    private bool _fillSuspended;

    [Signal] public delegate void CompletedEventHandler();

    public ActiveAudioPlayback()
    {
    }

    public ActiveAudioPlayback(AudioComponent audioComponent, AudioDevices audioDevices)
    {
        _audioComponent = audioComponent ?? throw new ArgumentNullException(nameof(audioComponent));
        _audioDevices = audioDevices ?? throw new ArgumentNullException(nameof(audioDevices));
        Patch = _audioComponent.Patch;
        Routing = _audioComponent.Routing;
        DirectOutput = _audioComponent.DirectOutput;
        DeviceStreams = new Dictionary<uint, IntPtr>();
        DeviceStreamChannels = new Dictionary<uint, int>();

        Decoder = new AudioSourceDecoder();

        _startTimeUs = (long)(Math.Max(0, _audioComponent.StartTime) * 1_000_000.0);
        _useCustomEnd = _audioComponent.EndTime >= 0;
        if (_useCustomEnd)
            _endTimeUs = (long)(_audioComponent.EndTime * 1_000_000.0);
        else if (_audioComponent.Metadata != null && _audioComponent.Metadata.Duration > 0)
            _endTimeUs = (long)(_audioComponent.Metadata.Duration * 1_000_000.0);
        else
            _endTimeUs = long.MaxValue;

        EffectivePlayCount = _audioComponent.Loop ? int.MaxValue : Math.Max(1, _audioComponent.PlayCount);
        RebuildPlaybackRegions();
        ApplyRegionUnlocked(0);
    }

    /// <summary>
    /// Resolves show-relative media paths (e.g. Audio/song.wav) to absolute paths.
    /// </summary>
    private static string ResolveMediaPath(string storedPath)
    {
        if (Engine.GetMainLoop() is SceneTree tree)
        {
            var globalData = tree.Root.GetNodeOrNull<GlobalData>("/root/GlobalData");
            if (globalData != null)
                return globalData.ResolveMediaPath(storedPath);
        }
        return storedPath;
    }

    /// <summary>
    /// Opens the decoder, seeks to start, and prefetches PCM for low-latency GO.
    /// </summary>
    /// <remarks>
    /// On failure, calls <see cref="Clean"/> so a half-open decoder is never left alive
    /// (callers must still discard the playback instance).
    /// </remarks>
    public async Task InitAsync()
    {
        try
        {
            RefreshAudioTuning();

            // Prefer sample-accurate PCM store for lossy codecs (fixes MP3 loop drift),
            // subject to decoder size/duration caps. Short looping cues stay exact.
            // Streaming path (long/lossy overflow): ring sized for PrefetchMs / TargetBufferMs.
            string mediaPath = ResolveMediaPath(_audioComponent.AudioFile);
            await Decoder.OpenAsync(
                mediaPath,
                preferSampleAccurateStore: true,
                ringMs: _audioTuning.RecommendedRingMs);
            SourceChannels = Decoder.Info.Channels;
            SourceSampleRate = Decoder.Info.SampleRate;
            SourceFormat = SDL.AudioFormat.AudioF32LE;
            SourceBytesPerFrame = SourceChannels * sizeof(float);
            _ratePitch.Configure(SourceChannels, SourceSampleRate);
            _ratePitch.Reset();

            if (!_useCustomEnd && Decoder.Info.DurationUs > 0)
                _endTimeUs = Decoder.Info.DurationUs;

            if (_startTimeUs > 0)
                Decoder.Seek(_startTimeUs);
            else
                Decoder.Prefetch(_audioTuning.PrefetchMs);

            // Prefetch after seek as well
            Decoder.Prefetch(_audioTuning.PrefetchMs);

            int maxFrames = Math.Max(SourceSampleRate / 10, 1024); // ~100 ms chunk
            _srcBuffer = new float[maxFrames * SourceChannels];
            _mixBuffer = new float[maxFrames * 16]; // up to 16 out channels

            GD.Print($"ActiveAudioPlayback:InitAsync - rate={SourceSampleRate} ch={SourceChannels} codec={Decoder.Info.CodecName}");
        }
        catch
        {
            try { Clean(); } catch { /* ignore */ }
            throw;
        }
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

    /// <summary>
    /// Effective component-level volume for mixing (runtime control-fade override or cue component).
    /// </summary>
    public float EffectiveLevelLinear
    {
        get
        {
            lock (_lock)
            {
                if (_runtimeLevelLinear.HasValue)
                    return _runtimeLevelLinear.Value;
            }
            return AudioMixMatrix.ClampComponentGainLinear((float)_audioComponent.Volume);
        }
    }

    /// <inheritdoc />
    public float ReadDisplayLevel(float deltaSeconds) =>
        PlaybackLevel.Read(ref _meterPeakBits, ref _meterDisplay, deltaSeconds);

    /// <summary>
    /// Effective pan for mixing (runtime control-fade override or cue component). Non-stereo → 0.
    /// </summary>
    public float EffectivePan
    {
        get
        {
            if (SourceChannels != 2) return 0f;
            lock (_lock)
            {
                if (_runtimePan.HasValue)
                    return _runtimePan.Value;
            }
            return Mathf.Clamp(_audioComponent.Pan, -1f, 1f);
        }
    }

    /// <summary>
    /// Sets a playback-only volume level (does not mutate the cue component).
    /// </summary>
    /// <param name="linear">Linear volume 0…1.</param>
    public void SetRuntimeLevelLinear(float linear)
    {
        lock (_lock)
            _runtimeLevelLinear = AudioMixMatrix.ClampComponentGainLinear(linear);
    }

    /// <summary>
    /// Sets a playback-only pan (does not mutate the cue component).
    /// </summary>
    /// <param name="pan">Pan −1…1.</param>
    public void SetRuntimePan(float pan)
    {
        lock (_lock)
            _runtimePan = Mathf.Clamp(pan, -1f, 1f);
    }

    /// <summary>
    /// Component play rate used as the timeline base (runtime control-fade override or cue component).
    /// </summary>
    public double EffectivePlayRate
    {
        get
        {
            lock (_lock)
            {
                if (_runtimePlayRate.HasValue)
                    return _runtimePlayRate.Value;
            }
            return _audioComponent != null
                ? AudioComponent.ClampPlayRate(_audioComponent.PlayRate)
                : AudioComponent.DefaultPlayRate;
        }
    }

    /// <summary>
    /// Component pitch in cents used as the timeline base (runtime control-fade override or cue component).
    /// </summary>
    public float EffectivePitchCents
    {
        get
        {
            lock (_lock)
            {
                if (_runtimePitchCents.HasValue)
                    return _runtimePitchCents.Value;
            }
            return _audioComponent != null
                ? AudioComponent.ClampPitchCents(_audioComponent.PitchCents)
                : AudioComponent.DefaultPitchCents;
        }
    }

    /// <summary>
    /// Sets a playback-only play rate (does not mutate the cue component). Timeline relative rate still applies.
    /// </summary>
    /// <param name="rate">Play-rate multiplier (0.1…8).</param>
    public void SetRuntimePlayRate(double rate)
    {
        lock (_lock)
            _runtimePlayRate = AudioComponent.ClampPlayRate(rate);
    }

    /// <summary>
    /// Sets a playback-only pitch in cents (does not mutate the cue component). Timeline relative pitch still applies.
    /// </summary>
    /// <param name="cents">Pitch offset in cents (−2400…+2400).</param>
    public void SetRuntimePitchCents(float cents)
    {
        lock (_lock)
            _runtimePitchCents = AudioComponent.ClampPitchCents(cents);
    }

    /// <summary>
    /// After the current pass of the innermost loop (region, then component play count / Loop),
    /// do not repeat that loop.
    /// </summary>
    public void RequestDevamp()
    {
        lock (_lock)
            _devampRequested = true;
    }

    /// <summary>
    /// Ensures <see cref="Routing"/> is a private clone, then sets one matrix cell for this playback only.
    /// </summary>
    /// <param name="inputCh">Input channel index.</param>
    /// <param name="outputCh">Output channel index.</param>
    /// <param name="linear">Linear volume 0…1.</param>
    /// <returns><c>true</c> when the cell was written.</returns>
    public bool SetRuntimeMatrixCell(int inputCh, int outputCh, float linear)
    {
        lock (_lock)
        {
            if (!_routingIsPrivate)
            {
                if (Routing == null)
                    return false;
                Routing = Routing.Clone();
                _routingIsPrivate = true;
            }

            if (Routing == null) return false;
            if (inputCh < 0 || inputCh >= Routing.InputChannels) return false;
            if (outputCh < 0 || outputCh >= Routing.OutputChannels) return false;
            Routing.SetVolume(inputCh, outputCh, Mathf.Clamp(linear, 0f, 1f));
            return true;
        }
    }

    /// <summary>
    /// Reads the current matrix cell (private runtime copy or shared component routing).
    /// </summary>
    public bool TryGetMatrixCell(int inputCh, int outputCh, out float linear)
    {
        linear = 0f;
        lock (_lock)
        {
            var routing = Routing;
            if (routing == null) return false;
            if (inputCh < 0 || inputCh >= routing.InputChannels) return false;
            if (outputCh < 0 || outputCh >= routing.OutputChannels) return false;
            linear = routing.GetVolume(inputCh, outputCh);
            return true;
        }
    }

    public int CurrentPlayCount
    {
        get { lock (_lock) return _currentPlayCount; }
        set { lock (_lock) _currentPlayCount = value; }
    }

    /// <summary>
    /// Starts the demand-driven fill loop (optionally with fade-in).
    /// Fire-and-forget entry; prefer <see cref="PlayAsync"/> when the caller can await.
    /// </summary>
    /// <param name="fadeInDuration">
    /// Fade-in seconds for this start. When null, uses <see cref="AudioComponent.FadeInDuration"/>.
    /// When 0, starts at full volume (declick ramp only).
    /// </param>
    public void Play(double? fadeInDuration = null)
    {
        TaskUtil.FireAndForget(PlayAsync(fadeInDuration), "ActiveAudioPlayback.Play");
    }

    /// <summary>
    /// Starts the demand-driven fill loop (optionally with fade-in). Awaitable form of <see cref="Play"/>.
    /// </summary>
    /// <param name="fadeInDuration">
    /// Fade-in seconds for this start. When null, uses <see cref="AudioComponent.FadeInDuration"/>.
    /// When 0, starts at full volume (declick ramp only).
    /// </param>
    public async Task PlayAsync(double? fadeInDuration = null)
    {
        try
        {
            lock (_lock)
            {
                if (_hasStarted || IsStopped) return;
                _hasStarted = true;
            }

            // Main thread: snapshot settings before fill loop / prefill use _audioTuning.
            RefreshAudioTuning();
            lock (_lock)
            {
                RebuildPlaybackRegions();
                ApplyRegionUnlocked(0);
            }

            double fadeIn = fadeInDuration ?? _audioComponent.FadeInDuration;
            if (fadeIn > 1e-9)
            {
                // Zero master level before any PCM is pushed (FadeInAsync also sets this; do it here
                // so a slow await cannot race a prefill path later).
                SetVolume(0f);
                await FadeInAsync(fadeIn);
            }
            else
            {
                SetVolume(1f);
                ArmDeclickRamp();
                PrefillStreams();
                StartFillLoop();
                GD.Print("ActiveAudioPlayback:PlayAsync - Fill loop started");
            }
        }
        catch (Exception ex)
        {
            GD.PrintErr($"ActiveAudioPlayback:PlayAsync - {ex.Message}");
        }
    }

    private void StartFillLoop()
    {
        _fillCts?.Cancel();
        _fillCts = new CancellationTokenSource();
        var token = _fillCts.Token;
        _fillTask = Task.Run(() => FillLoop(token), token);
    }

    private void StopFillLoop()
    {
        try
        {
            _fillCts?.Cancel();
            if (_fillTask != null)
            {
                try { _fillTask.Wait(500); } catch { /* ignore */ }
            }
        }
        catch { /* ignore */ }
        finally
        {
            _fillCts?.Dispose();
            _fillCts = null;
            _fillTask = null;
        }
    }

    /// <summary>
    /// Demand-driven fill: tops up each SDL stream when queued data is below the low-water mark.
    /// </summary>
    private void FillLoop(CancellationToken token)
    {
        // If streams never appear after start, abort rather than spinning forever.
        // (~2s at FillLoopSleepMs) — setup should already prevent this path.
        int emptyStreamIterations = 0;
        const int maxEmptyStreamIterations = 500;

        try
        {
            while (!token.IsCancellationRequested)
            {
                // Do not call RefreshAudioTuning here — fill runs off the main thread and
                // SceneTree/GetNode is not allowed. Use last main-thread snapshot of _audioTuning.

                bool hold;
                lock (_lock) hold = IsPaused || IsStopped || _fillSuspended || _seekInProgress;
                if (hold)
                {
                    Thread.Sleep(FillLoopSleepMs);
                    continue;
                }

                if (DeviceStreams == null || DeviceStreams.Count == 0)
                {
                    emptyStreamIterations++;
                    if (emptyStreamIterations >= maxEmptyStreamIterations)
                    {
                        GD.PrintErr("ActiveAudioPlayback:FillLoop - No device streams after start; aborting playback.");
                        CallDeferred(nameof(CompleteFromEnd));
                        return;
                    }
                    Thread.Sleep(FillLoopSleepMs);
                    continue;
                }

                emptyStreamIterations = 0;

                bool anyNeed = false;
                int maxNeedFrames = 0;

                foreach (var kv in DeviceStreams)
                {
                    long queued = SDL.GetAudioStreamQueued(kv.Value);
                    int outCh = GetStreamChannels(kv.Key);
                    int bytesPerOutFrame = outCh * sizeof(float);
                    if (bytesPerOutFrame <= 0) continue;

                    long lowWater = SourceSampleRate * _audioTuning.LowWaterMs / 1000L * bytesPerOutFrame;
                    long target = SourceSampleRate * _audioTuning.TargetBufferMs / 1000L * bytesPerOutFrame;

                    if (queued < lowWater)
                    {
                        anyNeed = true;
                        int need = (int)Math.Max(1, (target - queued) / bytesPerOutFrame);
                        if (need > maxNeedFrames) maxNeedFrames = need;
                    }
                }

                if (!anyNeed)
                {
                    Thread.Sleep(FillLoopSleepMs);
                    continue;
                }

                // Cap read size to buffer capacity
                int maxFrames = _srcBuffer.Length / SourceChannels;
                int framesToRead = Math.Min(maxNeedFrames, maxFrames);

                // Respect current timeline-region end (or the cue out point when there are no nodes).
                long posUs = Decoder.PositionUs;
                long regionEndUs;
                lock (_lock) regionEndUs = _regionEndUs;

                // Arm end-fade early so FadeOutDuration runs inside the last seconds of content
                // (e.g. 10s segment + 4s fade → fade begins at t=6s).
                TryArmNaturalEndFade(posUs);

                bool fadingOut;
                lock (_lock) fadingOut = _isFadingOut;

                if (posUs >= regionEndUs)
                {
                    // While end-fading, keep the fill loop alive until FadeOutAsync HardStops.
                    if (fadingOut)
                    {
                        Thread.Sleep(FillLoopSleepMs);
                        continue;
                    }
                    HandleRegionBoundary();
                    continue;
                }

                // Limit output frames so we don't read past the region end (rate consumes more source).
                var timeline = CurrentTimeline();
                if (regionEndUs < long.MaxValue && SourceSampleRate > 0)
                {
                    long remainingUs = regionEndUs - posUs;
                    int remainingSource = (int)Math.Max(0, remainingUs * SourceSampleRate / 1_000_000L);
                    int remainingOut = (int)Math.Max(0, remainingSource / timeline.PlayRate);
                    framesToRead = Math.Min(framesToRead, Math.Max(1, remainingOut));
                }

                lock (_lock)
                {
                    if (IsPaused || IsStopped || _fillSuspended) continue;
                }

                // Decode + rate/pitch outside playback lock to avoid blocking Pause/Seek
                int frames = _ratePitch.Process(
                    Decoder,
                    _srcBuffer.AsSpan(),
                    framesToRead,
                    timeline.PlayRate,
                    _audioComponent.KeepPitch,
                    timeline.PitchCents,
                    token);

                if (frames <= 0)
                {
                    long endUs;
                    lock (_lock) endUs = _regionEndUs;
                    if (Decoder.EndOfStream || Decoder.PositionUs >= endUs)
                    {
                        bool fading;
                        lock (_lock) fading = _isFadingOut;
                        if (fading)
                        {
                            Thread.Sleep(FillLoopSleepMs);
                        }
                        else
                        {
                            HandleRegionBoundary();
                        }
                    }
                    else
                    {
                        Thread.Sleep(FillLoopSleepMs);
                    }
                    continue;
                }

                // Discard if seek/pause started during Read — stale PCM must not hit a cleared stream.
                lock (_lock)
                {
                    if (IsPaused || IsStopped || _fillSuspended) continue;
                }

                _framesDelivered += frames;
                PushMixedFrames(frames, timeline.VolumeLinear);
            }
        }
        catch (OperationCanceledException)
        {
            // normal stop
        }
        catch (Exception ex)
        {
            GD.PrintErr($"ActiveAudioPlayback:FillLoop - {ex.Message}");
            // Ensure ActiveCue can tear down the UI if the fill loop dies unexpectedly.
            try { CallDeferred(nameof(CompleteFromEnd)); } catch { /* object may be freeing */ }
        }
    }

    private int GetStreamChannels(uint deviceLogicalId)
    {
        if (DeviceStreamChannels != null &&
            DeviceStreamChannels.TryGetValue(deviceLogicalId, out int ch) &&
            ch > 0)
        {
            return ch;
        }
        return SourceChannels;
    }

    /// <summary>
    /// Arms a raised-cosine fade-in so the next PCM pushed after silence does not click.
    /// Uses the last main-thread <see cref="_audioTuning"/> snapshot (safe from fill threads).
    /// </summary>
    private void ArmDeclickRamp()
    {
        if (SourceSampleRate <= 0 || _audioTuning.DeclickRampMs <= 0)
        {
            _declickFramesRemaining = 0;
            _declickRampTotalFrames = 0;
            return;
        }

        _declickRampTotalFrames = Math.Max(1, SourceSampleRate * _audioTuning.DeclickRampMs / 1000);
        _declickFramesRemaining = _declickRampTotalFrames;
    }

    /// <summary>
    /// Pulls current show audio latency / declick settings into fill knobs.
    /// Must only run on the main thread (uses SceneTree / GetNodeOrNull).
    /// </summary>
    private void RefreshAudioTuning()
    {
        try
        {
            if (Engine.GetMainLoop() is SceneTree tree)
            {
                var settings = tree.Root.GetNodeOrNull<GlobalData>("/root/GlobalData")?.Settings;
                if (settings != null)
                {
                    _audioTuning = settings.GetAudioPresentTuning();
                    return;
                }
            }
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
    /// Applies the active de-click gain curve to interleaved float samples (in-place).
    /// </summary>
    private void ApplyDeclickRamp(Span<float> interleaved, int frames, int channels)
    {
        if (_declickFramesRemaining <= 0 || frames <= 0 || channels <= 0 || _declickRampTotalFrames <= 0)
            return;

        int total = _declickRampTotalFrames;
        for (int f = 0; f < frames && _declickFramesRemaining > 0; f++)
        {
            int progressed = total - _declickFramesRemaining;
            float t = (progressed + 1) / (float)total;
            if (t > 1f) t = 1f;
            float gain = 0.5f * (1f - MathF.Cos(MathF.PI * t));
            int baseIdx = f * channels;
            for (int c = 0; c < channels; c++)
                interleaved[baseIdx + c] *= gain;
            _declickFramesRemaining--;
        }
    }

    /// <summary>
    /// Fills each bound SDL stream up to the configured target buffer before play/after seek.
    /// </summary>
    private void PrefillStreams()
    {
        if (Decoder == null || DeviceStreams == null || DeviceStreams.Count == 0)
            return;
        if (_srcBuffer == null || SourceSampleRate <= 0 || SourceChannels <= 0)
            return;

        // Uses _audioTuning last refreshed on the main thread (never call GetNode here —
        // Prefill can also run from the fill loop on loop/segment restart).
        const int maxIterations = 48;
        for (int iter = 0; iter < maxIterations; iter++)
        {
            int maxNeedFrames = 0;
            foreach (var kv in DeviceStreams)
            {
                long queued = SDL.GetAudioStreamQueued(kv.Value);
                int outCh = GetStreamChannels(kv.Key);
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

            long regionEndUs;
            lock (_lock) regionEndUs = _regionEndUs;
            if (Decoder.PositionUs >= regionEndUs)
                break;

            int maxFrames = _srcBuffer.Length / SourceChannels;
            int framesToRead = Math.Min(maxNeedFrames, maxFrames);
            var timeline = CurrentTimeline();
            if (regionEndUs < long.MaxValue && SourceSampleRate > 0)
            {
                long remainingUs = regionEndUs - Decoder.PositionUs;
                int remainingSource = (int)Math.Max(0, remainingUs * SourceSampleRate / 1_000_000L);
                int remainingOut = (int)Math.Max(0, remainingSource / timeline.PlayRate);
                framesToRead = Math.Min(framesToRead, Math.Max(1, remainingOut));
            }
            int frames = _ratePitch.Process(
                Decoder,
                _srcBuffer.AsSpan(),
                framesToRead,
                timeline.PlayRate,
                _audioComponent.KeepPitch,
                timeline.PitchCents);
            if (frames <= 0)
                break;

            _framesDelivered += frames;
            PushMixedFrames(frames, timeline.VolumeLinear);
        }
    }

    private unsafe void PushMixedFrames(int frames, float timelineVolumeLinear)
    {
        if (DeviceStreams == null) return;

        float masterVol;
        float componentVol;
        float pan;
        lock (_lock)
        {
            // Cue fade envelope × session master (volume + runtime mute from AudioDevices).
            masterVol = _volume * (_audioDevices?.GetEffectiveSessionMasterLinear() ?? 1f);
            float baseVol = _runtimeLevelLinear
                ?? AudioMixMatrix.ClampComponentGainLinear((float)_audioComponent.Volume);
            componentVol = AudioMixMatrix.ClampComponentGainLinear(baseVol * timelineVolumeLinear);
            // Stereo pan only; mono / multi-channel ignore (Mix applies identity).
            pan = SourceChannels == 2
                ? (_runtimePan ?? Mathf.Clamp(_audioComponent.Pan, -1f, 1f))
                : 0f;
        }
        bool isDirect = !string.IsNullOrEmpty(DirectOutput);

        int declickRemainSnapshot = _declickFramesRemaining;
        int declickTotalSnapshot = _declickRampTotalFrames;

        foreach (var kv in DeviceStreams)
        {
            int outCh = GetStreamChannels(kv.Key);
            int outSamples = frames * outCh;
            if (outSamples > _mixBuffer.Length)
                _mixBuffer = new float[outSamples];

            string deviceName = _audioDevices.GetAudioDeviceByLogicalId(kv.Key)?.Name;

            AudioMixMatrix.Mix(
                _srcBuffer.AsSpan(0, frames * SourceChannels),
                frames,
                SourceChannels,
                _mixBuffer.AsSpan(0, outSamples),
                outCh,
                masterVol,
                componentVol,
                pan,
                Routing,
                Patch,
                deviceName,
                isDirect);

            _declickFramesRemaining = declickRemainSnapshot;
            _declickRampTotalFrames = declickTotalSnapshot;
            ApplyDeclickRamp(_mixBuffer.AsSpan(0, outSamples), frames, outCh);

            // Peak clamp + silence floor (show Audio settings) before handing PCM to SDL.
            if (_audioDevices != null)
            {
                _audioDevices.GetOutputLimits(out float maxAbs, out float minAbs);
                AudioMixMatrix.ApplyOutputLimits(_mixBuffer.AsSpan(0, outSamples), maxAbs, minAbs);
            }

            PlaybackLevel.Note(ref _meterPeakBits, _mixBuffer.AsSpan(0, outSamples));

            int byteCount = outSamples * sizeof(float);
            fixed (float* p = _mixBuffer)
            {
                SDL.PutAudioStreamData(kv.Value, (IntPtr)p, byteCount);
            }
        }

        if (declickRemainSnapshot > 0)
            _declickFramesRemaining = Math.Max(0, declickRemainSnapshot - frames);
    }

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
            if (IsStopped || _completedEmitted || _isFadingOut)
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
            if (DeviceStreams != null)
            {
                foreach (var stream in DeviceStreams.Values)
                    SDL.ClearAudioStream(stream);
            }

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
            // _completedEmitted also covers "already finishing"
            if (IsStopped || _completedEmitted || _isFadingOut) return;

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
                // Mark finishing so fill loop stops re-entering (IsStopped set in Clean)
                _completedEmitted = true;
                scheduleComplete = true;
            }
        }

        if (!scheduleComplete)
        {
            // Loop path: seek + re-prime outside lock; hold fill via _fillSuspended.
            try
            {
                if (DeviceStreams != null)
                {
                    foreach (var stream in DeviceStreams.Values)
                        SDL.ClearAudioStream(stream);
                }
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
            if (IsStopped || IsPaused || _isFadingOut || _isFadingIn || _completedEmitted
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
            if (IsStopped || IsPaused || _isFadingOut || _completedEmitted)
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
            _completedEmitted = false;
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

    public void Pause()
    {
        lock (_lock)
        {
            if (IsPaused || IsStopped) return;
            IsPaused = true;
            _pausedAtUs = GetPlaybackPositionUs();
        }

        if (DeviceStreams != null)
        {
            foreach (var stream in DeviceStreams.Values)
                SDL.ClearAudioStream(stream);
        }
        Decoder?.FlushBuffers();
        GD.Print($"ActiveAudioPlayback:Pause - Paused at {_pausedAtUs / 1000} ms");
    }

    public void Resume()
    {
        long resumeAtUs;
        lock (_lock)
        {
            if (!IsPaused || IsStopped) return;
            resumeAtUs = _pausedAtUs;
            _pausedAtUs = 0;
            // Unpause before async seek so FinishSeekWorker can re-prime.
            IsPaused = false;
        }

        if (resumeAtUs > 0)
        {
            // Seek worker holds fill until complete, then primes (same path as scrub Seek).
            Seek(resumeAtUs);
        }
        else
        {
            lock (_lock) _fillSuspended = true;
            try
            {
                RefreshAudioTuning();
                ArmDeclickRamp();
                PrefillStreams();
            }
            finally
            {
                lock (_lock) _fillSuspended = false;
            }
        }

        GD.Print("ActiveAudioPlayback:Resume - Resumed");
    }

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
            // Cancel any in-progress fade-in/out so a second Stop can hard-stop,
            // or a first Stop can preempt fade-in and start fade-out.
            _fadeCts?.Cancel();
            if (!wasFadingOut)
            {
                needFade = fadeTime > 0 || (_audioComponent != null && _audioComponent.FadeOutDuration > 0);
                fadeDuration = fadeTime > 0
                    ? fadeTime
                    : (_audioComponent?.FadeOutDuration ?? 0);
                shape = fadeTime > 0
                    ? (fadeCurve ?? FadeCurveType.Linear)
                    : (_audioComponent?.FadeOutCurve ?? FadeCurveType.Linear);
                // Preempt fade-in so FadeOutAsync is allowed to start
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

    private void HardStop()
    {
        lock (_lock)
        {
            if (IsStopped) return;
            IsStopped = true;
            IsPaused = false;
        }

        StopFillLoop();

        if (DeviceStreams != null)
        {
            foreach (var stream in DeviceStreams.Values)
            {
                try { SDL.ClearAudioStream(stream); } catch { /* ignore */ }
            }
        }

        Clean();
    }

    public async Task FadeInAsync(double duration)
    {
        lock (_lock)
        {
            if (_isFadingIn || _isFadingOut) return;
            _isFadingIn = true;
            _fadeCts = new CancellationTokenSource();
            _volume = 0f;
        }

        float startVol = 0f;
        float endVol = 1.0f;
        // Master level is already 0 — prefill silence-level PCM, then ramp after the queue is armed.
        PrefillStreams();
        StartFillLoop();
        Stopwatch timer = Stopwatch.StartNew();

        try
        {
            while (timer.Elapsed.TotalSeconds < duration && !_fadeCts.Token.IsCancellationRequested)
            {
                float t = (float)(timer.Elapsed.TotalSeconds / duration);
                float shaped = FadeCurve.Evaluate(t, _audioComponent?.FadeInCurve ?? FadeCurveType.Linear);
                SetVolume(Mathf.Lerp(startVol, endVol, shaped));
                await Task.Delay(16, _fadeCts.Token);
            }
            if (!_fadeCts.Token.IsCancellationRequested)
                SetVolume(endVol);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            GD.PrintErr($"ActiveAudioPlayback:FadeInAsync - {ex.Message}");
        }
        finally
        {
            lock (_lock)
            {
                _isFadingIn = false;
                _fadeCts?.Dispose();
                _fadeCts = null;
            }
        }
    }

    public async Task FadeOutAsync(double duration, FadeCurveType? curve = null)
    {
        if (duration <= 0)
        {
            HardStop();
            return;
        }

        lock (_lock)
        {
            if (IsStopped) return;
            if (_isFadingOut) return;
            _isFadingIn = false;
            _isFadingOut = true;
            _fadeCts?.Dispose();
            _fadeCts = new CancellationTokenSource();
        }

        var shape = curve ?? _audioComponent?.FadeOutCurve ?? FadeCurveType.Linear;
        float startVol = _volume;
        var token = _fadeCts.Token;
        Stopwatch timer = Stopwatch.StartNew();

        try
        {
            while (timer.Elapsed.TotalSeconds < duration && !token.IsCancellationRequested)
            {
                float t = (float)(timer.Elapsed.TotalSeconds / duration);
                float shaped = FadeCurve.Evaluate(t, shape);
                SetVolume(Mathf.Lerp(startVol, 0f, shaped));
                await Task.Delay(16, token);
            }
            if (!token.IsCancellationRequested)
            {
                SetVolume(0f);
                HardStop();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            GD.PrintErr($"ActiveAudioPlayback:FadeOutAsync - {ex.Message}");
        }
        finally
        {
            lock (_lock)
            {
                _isFadingOut = false;
                if (_fadeCts != null)
                {
                    try { _fadeCts.Dispose(); } catch { /* ignore */ }
                    _fadeCts = null;
                }
            }
        }
    }

    public void SetVolume(float volume)
    {
        lock (_lock)
        {
            _volume = Mathf.Clamp(volume, 0f, 1f);
        }
    }

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
        if (DeviceStreams != null)
        {
            foreach (var kv in DeviceStreams)
            {
                long queuedBytes = SDL.GetAudioStreamQueued(kv.Value);
                int outCh = GetStreamChannels(kv.Key);
                if (outCh <= 0 || SourceSampleRate <= 0) continue;
                queuedUs += queuedBytes * 1_000_000L / (SourceSampleRate * outCh * sizeof(float));
                count++;
            }
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

            if (DeviceStreams != null)
            {
                foreach (var stream in DeviceStreams.Values)
                    SDL.ClearAudioStream(stream);
            }

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

    private bool _completedEmitted;

    /// <inheritdoc />
    public void OnOutputDeviceLost(uint logicalDeviceId)
    {
        // Tracking for this logical id was already cleared by AudioDevices.CloseAudioDevice.
        if (DeviceStreams != null && DeviceStreams.TryGetValue(logicalDeviceId, out var stream))
        {
            try
            {
                if (stream != IntPtr.Zero)
                    SDL.DestroyAudioStream(stream);
            }
            catch
            {
                // ignore
            }

            DeviceStreams.Remove(logicalDeviceId);
        }

        DeviceStreamChannels?.Remove(logicalDeviceId);

        if (DeviceStreams == null || DeviceStreams.Count == 0)
        {
            GD.Print(
                $"ActiveAudioPlayback:OnOutputDeviceLost - Device {logicalDeviceId} lost; no streams remain, cleaning up.");
            Clean();
        }
        else
        {
            GD.Print(
                $"ActiveAudioPlayback:OnOutputDeviceLost - Device {logicalDeviceId} lost; " +
                $"{DeviceStreams.Count} stream(s) remain.");
        }
    }

    /// <summary>
    /// Tears down decoder, streams, and fill loop, then signals <see cref="Completed"/>.
    /// Safe to call multiple times; only the first call emits Completed.
    /// </summary>
    /// <param name="freeImmediately">When true, <see cref="GodotObject.Free"/> runs before return (app quit).</param>
    public void Clean(bool freeImmediately = false)
    {
        bool alreadyDone;
        lock (_lock)
        {
            alreadyDone = _completedEmitted;
            if (IsStopped && Decoder == null && _completedEmitted)
            {
                if (freeImmediately && IsInstanceValid(this))
                    Free();
                return;
            }
            IsStopped = true;
            _completedEmitted = true;
        }

        StopFillLoop();
        CancelSeekWorker();
        _fadeCts?.Cancel();

        if (Decoder != null)
        {
            try
            {
                Decoder.Dispose();
            }
            catch (Exception ex)
            {
                GD.PrintErr($"ActiveAudioPlayback:Clean - Decoder dispose: {ex.Message}");
            }
            Decoder = null;
        }

        // Drop mix buffers
        if (_srcBuffer != null)
        {
            MediaMemory.NoteReleased(MediaMemory.FloatBufferBytes(_srcBuffer));
            _srcBuffer = null;
        }
        if (_mixBuffer != null)
        {
            MediaMemory.NoteReleased(MediaMemory.FloatBufferBytes(_mixBuffer));
            _mixBuffer = null;
        }

        if (DeviceStreams != null)
        {
            _audioDevices?.NotifyPlaybackCompleted(this);
            foreach (var stream in DeviceStreams.Values)
            {
                try { SDL.DestroyAudioStream(stream); } catch { /* ignore */ }
            }
            DeviceStreams.Clear();
        }
        DeviceStreamChannels?.Clear();

        MediaMemory.ReclaimIfNeeded();

        if (!alreadyDone)
        {
            try
            {
                if (IsInstanceValid(this))
                    EmitSignal(SignalName.Completed);
            }
            catch (Exception ex)
            {
                GD.PrintErr($"ActiveAudioPlayback:Clean - Completed signal: {ex.Message}");
            }

            // Free after call lock is released. CallDeferred(MethodName.Free) is unreliable
            // on C# GodotObject ("locked" / "Nonexistent function 'free'").
            if (IsInstanceValid(this))
            {
                if (freeImmediately)
                    Free();
                else
                    Callable.From(FreeDeferred).CallDeferred();
            }
        }
    }

    /// <summary>
    /// Invokes <see cref="GodotObject.Free"/> if this instance is still valid.
    /// </summary>
    private void FreeDeferred()
    {
        if (IsInstanceValid(this))
            Free();
    }
}
