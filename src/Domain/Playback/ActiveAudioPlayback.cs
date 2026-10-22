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

    /// <summary>Worker-side view of <see cref="DeviceStreams"/>; see <see cref="AudioStreamBindingSet"/>.</summary>
    private readonly AudioStreamBindingSet _streamBindings = new();

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

    /// <summary>
    /// A cancelled fill worker that may still be running. Teardown that the worker
    /// still touches (decoder, mix buffers, Free) waits for it without blocking main. Main only.
    /// </summary>
    private Task _retiringFillTask;

    /// <summary>True once the deferred decoder/buffer release and free are queued. Main only.</summary>
    private bool _teardownQueued;

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

    /// <summary>Post-start/seek de-click ramp (shared output stage, see AudioOutputMix).</summary>
    private DeclickRamp _declick;
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

            // Seek / prefetch can demux and decode (streaming ring): keep it off main.
            // Decoder calls stay serialized through the decoder's own lock.
            long startUs = _startTimeUs;
            int prefetchMs = _audioTuning.PrefetchMs;
            var decoder = Decoder;
            await Task.Run(() =>
            {
                if (startUs > 0)
                    decoder.Seek(startUs);
                decoder.Prefetch(prefetchMs);
            });
            if (IsStopped || !ReferenceEquals(Decoder, decoder))
                throw new InvalidOperationException("Playback was cleaned while the decoder was priming.");

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
                // First prefill runs on the fill worker, not on main.
                _ = StartFillLoop(prefillFirst: true);
                GD.Print("ActiveAudioPlayback:PlayAsync - Fill loop started");
            }
        }
        catch (Exception ex)
        {
            GD.PrintErr($"ActiveAudioPlayback:PlayAsync - {ex.Message}");
        }
    }

    /// <summary>
    /// Starts the fill worker. With <paramref name="prefillFirst"/>, the worker primes the
    /// streams with <see cref="PrefillStreams"/> before its loop, so the decode never runs on main.
    /// </summary>
    /// <returns>Completes once the first prefill is done (at once without prefill).</returns>
    private Task StartFillLoop(bool prefillFirst = false)
    {
        // A previous worker is cancelled, not awaited on main; the new worker waits for it on
        // its own thread, so two fill loops never write to the streams at once.
        var previous = StopFillLoop();
        _fillCts = new CancellationTokenSource();
        var token = _fillCts.Token;
        var primed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _fillTask = Task.Run(() =>
        {
            PlaybackWorkers.WaitBounded(500, previous);
            try
            {
                if (prefillFirst && !token.IsCancellationRequested)
                    PrefillStreams();
            }
            catch (Exception ex)
            {
                GD.PrintErr($"ActiveAudioPlayback:StartFillLoop - Prefill failed: {ex.Message}");
            }
            finally
            {
                primed.TrySetResult();
            }
            FillLoop(token);
        }, token);
        return primed.Task;
    }

    /// <summary>
    /// Cancels the fill worker without waiting for it. Main thread only.
    /// </summary>
    /// <returns>The worker if it (or an earlier cancelled one) is still running, else null.</returns>
    private Task StopFillLoop()
    {
        var task = _fillTask;
        var cts = _fillCts;
        _fillTask = null;
        _fillCts = null;
        try { cts?.Cancel(); } catch { /* ignore */ }

        if (PlaybackWorkers.IsRunning(task))
        {
            _retiringFillTask = task;
            // Dispose the token source only once the worker can no longer read it.
            task.ContinueWith(_ => { try { cts?.Dispose(); } catch { /* ignore */ } }, TaskScheduler.Default);
            return task;
        }

        try { cts?.Dispose(); } catch { /* ignore */ }
        return PlaybackWorkers.IsRunning(_retiringFillTask) ? _retiringFillTask : null;
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

                var bindings = _streamBindings.Snapshot;
                if (bindings.Length == 0)
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

                foreach (var b in bindings)
                {
                    long queued = _streamBindings.GetQueued(b);
                    if (queued < 0) continue;
                    int outCh = GetStreamChannels(b);
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
            // Make the early stop visible in the operator log (GlobalSignals is main-thread only).
            try { CallDeferred(nameof(ReportFillLoopError), ex.Message); } catch { /* object may be freeing */ }
            // Ensure ActiveCue can tear down the UI if the fill loop dies unexpectedly.
            try { CallDeferred(nameof(CompleteFromEnd)); } catch { /* object may be freeing */ }
        }
    }

    private int GetStreamChannels(AudioStreamBinding binding) =>
        binding.Channels > 0 ? binding.Channels : SourceChannels;

    /// <summary>Main thread: forwards a fill-loop failure to the operator log.</summary>
    private void ReportFillLoopError(string message)
    {
        if (Engine.GetMainLoop() is not SceneTree tree)
            return;
        string file = System.IO.Path.GetFileName(_audioComponent?.AudioFile ?? string.Empty);
        tree.Root.GetNodeOrNull<GlobalSignals>("/root/GlobalSignals")?.EmitSignal(nameof(GlobalSignals.Log),
            $"ActiveAudioPlayback: audio cue '{file}' stopped early, fill loop error: {message}", (int)LogType.Error);
    }

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
    /// Fills each bound SDL stream up to the configured target buffer before play/after seek.
    /// </summary>
    private void PrefillStreams()
    {
        if (Decoder == null || !_streamBindings.HasStreams)
            return;
        if (_srcBuffer == null || SourceSampleRate <= 0 || SourceChannels <= 0)
            return;

        // Uses _audioTuning last refreshed on the main thread (never call GetNode here —
        // Prefill can also run from the fill loop on loop/segment restart).
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

    private void PushMixedFrames(int frames, float timelineVolumeLinear)
    {
        var bindings = _streamBindings.Snapshot;
        if (bindings.Length == 0) return;

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

        AudioOutputMix.MixAndPut(
            _streamBindings, bindings, _srcBuffer.AsSpan(0, frames * SourceChannels), frames, SourceChannels,
            ref _mixBuffer, masterVol, componentVol, pan, Routing, Patch, isDirect,
            ref _declick, _audioDevices, ref _meterPeakBits);
    }

    public void Pause()
    {
        lock (_lock)
        {
            if (IsPaused || IsStopped) return;
            IsPaused = true;
            _pausedAtUs = GetPlaybackPositionUs();
        }

        _streamBindings.ClearAll();
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

        _streamBindings.ClearAll();

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
            _volume = 0f;
        }
        var token = cts.Token;

        float startVol = 0f;
        float endVol = 1.0f;
        // Master level is already 0. The fill worker primes the queue first; wait
        // briefly for it so the ramp starts with audio queued, without holding main.
        var primed = StartFillLoop(prefillFirst: true);
        await Task.WhenAny(primed, Task.Delay(50));
        Stopwatch timer = Stopwatch.StartNew();

        try
        {
            // Re-checked after every await: a Stop or fade-out may have taken over meanwhile,
            // and this loop must then never write the level again.
            while (timer.Elapsed.TotalSeconds < duration && IsFadeInCurrent(cts))
            {
                float t = (float)(timer.Elapsed.TotalSeconds / duration);
                float shaped = FadeCurve.Evaluate(t, _audioComponent?.FadeInCurve ?? FadeCurveType.Linear);
                SetVolume(Mathf.Lerp(startVol, endVol, shaped));
                await Task.Delay(16, token);
            }
            if (IsFadeInCurrent(cts))
                SetVolume(endVol);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            GD.PrintErr($"ActiveAudioPlayback:FadeInAsync - {ex.Message}");
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

        var shape = curve ?? _audioComponent?.FadeOutCurve ?? FadeCurveType.Linear;
        float startVol = _volume;
        var token = cts.Token;
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
                if (ReferenceEquals(_fadeCts, cts))
                    _fadeCts = null;
            }
            try { cts.Dispose(); } catch { /* ignore */ }
        }
    }

    public void SetVolume(float volume)
    {
        lock (_lock)
        {
            _volume = Mathf.Clamp(volume, 0f, 1f);
        }
    }

    /// <summary>End of media reached and <see cref="CompleteFromEnd"/> deferred (set by the fill worker).</summary>
    private bool _endScheduled;

    /// <summary>True once <see cref="Completed"/> has been emitted. Set only where it is emitted.</summary>
    private bool _completedSignalled;

    /// <inheritdoc />
    public void OnOutputDeviceLost(uint logicalDeviceId)
    {
        // Tracking for this logical id was already cleared by AudioDevices.CloseAudioDevice.
        // Detach first (the fill worker can no longer reach the handle), then destroy.
        IntPtr stream = _streamBindings.Detach(DeviceStreams, DeviceStreamChannels, logicalDeviceId);
        AudioStreamBindingSet.Destroy(stream);

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
        lock (_lock)
        {
            if (IsStopped && Decoder == null && _completedSignalled)
            {
                if (freeImmediately && IsInstanceValid(this))
                    Free();
                return;
            }
            IsStopped = true;
        }

        // Non-blocking stop: cancel the worker; never Task.Wait on main for an
        // operator Stop. App quit (freeImmediately) still waits briefly, because it frees now.
        var worker = StopFillLoop();
        if (freeImmediately)
        {
            PlaybackWorkers.WaitBounded(500, worker);
            worker = null;
        }
        CancelSeekWorker();
        _fadeCts?.Cancel();

        // Detach every stream, then destroy it. Safe while the worker still runs: after the
        // detach (under IoLock) the worker skips the handles.
        if (DeviceStreams != null)
            _audioDevices?.NotifyPlaybackCompleted(this);
        AudioStreamBindingSet.Destroy(_streamBindings.DetachAll(DeviceStreams, DeviceStreamChannels));

        // The worker still reads the decoder and mix buffers: release them only once it exits.
        bool workerRunning = PlaybackWorkers.IsRunning(worker);
        if (!workerRunning)
            ReleaseDecoderAndBuffers();

        // Emit exactly once, whichever path got here first (end of media, Stop, device loss).
        bool emitCompleted;
        lock (_lock)
        {
            emitCompleted = !_completedSignalled;
            _completedSignalled = true;
        }

        if (emitCompleted)
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
                else if (!workerRunning)
                    Callable.From(FreeDeferred).CallDeferred();
            }
        }

        // App quit on a repeat call (Completed already sent): free now, as the caller expects.
        if (freeImmediately && !emitCompleted && IsInstanceValid(this))
            Free();

        // Worker still running: release and free once it has exited, on main.
        // Completed has already fired above, at the same point as before.
        if (workerRunning && !_teardownQueued)
        {
            _teardownQueued = true;
            PlaybackWorkers.RunAfter(() =>
            {
                ReleaseDecoderAndBuffers();
                FreeDeferred();
            }, worker);
        }
    }

    /// <summary>
    /// Disposes the decoder and drops the mix buffers. Main thread, only once no fill worker can
    /// use them. Idempotent; uses no Godot API on this object, so it is safe after a Free.
    /// </summary>
    private void ReleaseDecoderAndBuffers()
    {
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

        MediaMemory.ReclaimIfNeeded();
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
