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
/// Software control layer for an active video cue with optional embedded audio.
/// <para>
/// Video: pull-based <see cref="VideoSourceDecoder"/>; presentation is driven on the main
/// thread by a master clock (audio position when available, else wall clock).
/// Audio: same pull-based path as pure audio cues (<see cref="AudioSourceDecoder"/> + SDL fill).
/// </para>
/// A/V sync strategy: audio is the master clock when present; video presents or drops frames
/// to stay within a tolerance of that clock. When the soundtrack ends before the video
/// stream, the master falls back to wall time so remaining frames can present and the cue can
/// complete. On seek/pause/loop both streams are reset to the same media timestamp.
/// </summary>
public partial class ActiveVideoPlayback : Node, IAudioPlayback, IComponentLevel
{
    private const int FadeUpdateIntervalMs = 16;
    private const long MicrosecondsPerSecond = 1_000_000;
    private const int AudioFillSleepMs = 4;

    /// <summary>
    /// Soft quality knobs resolved from show <see cref="Settings.VideoQualityMode"/>.
    /// Defaults match the previous hardcodes (Balanced).
    /// </summary>
    private VideoPresentTuning _presentTuning = VideoPresentTuning.ForMode(VideoQualityMode.Balanced);

    /// <summary>
    /// Embedded-audio fill / prefetch / de-click knobs from show <see cref="Settings"/>.
    /// Shares the same latency table as standalone audio cues.
    /// </summary>
    private AudioPresentTuning _audioTuning = AudioPresentTuning.ForMode(
        AudioLatencyMode.Balanced, Settings.DefaultAudioDeclickMs);

    private VideoSourceDecoder _videoDecoder;
    private SubtitleSourceDecoder _subtitleDecoder;
    private ActiveTextPlayback _linkedTextPlayback;
    private string _lastSubtitleText = string.Empty;
    private ImageTexture _godotTexture;
    private Image _godotImage;
    /// <summary>Fallback upload buffer, only used if a decoder buffer is not exactly one frame.</summary>
    private byte[] _displayRgba;

    // Settings last turned into tuning by RefreshTuningIfSettingsChanged.
    private GlobalData _tuningGlobalData;
    private Settings _tuningSettings;
    private VideoQualityMode _tuningVideoMode;
    private AudioLatencyMode _tuningAudioMode;
    private int _tuningDeclickMs;

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
    public SDL.AudioFormat SourceFormat { get; set; }

    private readonly VideoComponent _videoComponent;
    private readonly AudioDevices _audioDevices;
    private VideoTargetLayer _targetLayer;

    private AudioSourceDecoder _audioDecoder;
    private CancellationTokenSource _audioFillCts;
    private Task _audioFillTask;
    private float[] _audioSrcBuffer;
    private float[] _audioMixBuffer;

    /// <summary>Post-start/seek de-click ramp (shared output stage, see AudioOutputMix).</summary>
    private DeclickRamp _declick;
    /// <summary>
    /// When true, the audio fill loop must not Put to SDL (seek/clear in progress).
    /// Separate from public <see cref="IsSeeking"/>, which is also used for UI scrub preview.
    /// </summary>
    private bool _audioFillSuspended;

    private CancellationTokenSource _videoPrefetchCts;
    private Task _videoPrefetchTask;

    /// <summary>
    /// Cancelled workers that may still be running. Teardown they still touch
    /// (decoders, mix buffers, free) waits for them without blocking main. Main only.
    /// </summary>
    private Task _retiringPrefetchTask;
    private Task _retiringAudioFillTask;

    /// <summary>Cancels in-flight seek+prefetch workers when a newer seek supersedes them.</summary>
    private CancellationTokenSource _seekCts;
    private Task _seekTask;
    /// <summary>True while a seek worker owns the decoders (present path must not touch them).</summary>
    private volatile bool _seekInProgress;
    /// <summary>Target media time for the in-flight seek (UI/master clock hold).</summary>
    private long _seekTargetUs;

    /// <summary>
    /// True while an async decoder seek is in flight. Progress UI should hold the scrub target
    /// and not sample the live master clock until this clears.
    /// </summary>
    public bool IsDecoderSeeking => _seekInProgress;

    private Dictionary<Control, TextureRect> _targetLayers = new();

    private readonly object _lock = new object();
    /// <summary>Master fade envelope (0–1). Held at 0 until PlayAsync / FadeInAsync arms presentation.</summary>
    private float _volume = 0f;

    /// <summary>
    /// When set, replaces component audio level for this playback only (control fades).
    /// </summary>
    private float? _runtimeLevelLinear;
    private int _meterPeakBits;
    private float _meterDisplay;

    /// <summary>
    /// When set, replaces <see cref="VideoComponent.Pan"/> for this playback only (control fades).
    /// </summary>
    private float? _runtimePan;

    /// <summary>
    /// When set, replaces <see cref="VideoComponent.Opacity"/> for this playback only (control fades).
    /// </summary>
    private float? _runtimeOpacity;

    /// <summary>When true, the next component loop / remaining play count is skipped after this pass.</summary>
    private bool _devampRequested;

    /// <summary>True when <see cref="Routing"/> is a private clone (safe to mutate for control fades).</summary>
    private bool _routingIsPrivate;
    private bool _isFadingOut;
    private bool _isFadingIn;
    /// <summary>True once natural end-fade has been scheduled (prevents repeated arms).</summary>
    private bool _naturalEndFadeArmed;
    public bool IsStopped;
    public bool IsPaused;
    public bool IsSeeking;
    public bool IsExiting;
    /// <summary>Visual fade multiplier (0–1). Held at 0 until PlayAsync / FadeInAsync arms presentation.</summary>
    private float _fadeAlpha = 0f;
    private CancellationTokenSource _fadeCts;

    private long _startTimeUs;
    private long _endTimeUs;
    private bool _useCustomEnd;
    public int EffectivePlayCount;
    private int _currentPlayCount = 1;
    private long _pausedAtUs;
    private bool _isExiting;
    private bool _completedEmitted;
    /// <summary>True after HandleSegmentEnd has deferred CompleteFromEnd (prevents re-entry).</summary>
    private bool _segmentEndScheduled;
    /// <summary>
    /// True after the embedded-audio master has reached EOS and the SDL queue has drained.
    /// Remaining video continues on the wall clock so a shorter soundtrack cannot stall the cue.
    /// </summary>
    private bool _audioMasterExhausted;
    private bool _isDisposed;
    private bool _isPlaying;
    /// <summary>True after the first (and typically only) still-image frame has been shown.</summary>
    private bool _imageFramePresented;

    /// <summary>
    /// Natural end holds the last frame on the output layer briefly so a follow cue can
    /// present its first frame before this host is torn down (avoids black flash between images).
    /// </summary>
    private bool _holdingLastFrame;

    /// <summary>Frames to wait before releasing a held last frame after natural completion.</summary>
    private const int NaturalEndDisplayHoldFrames = 2;

    // Master clock (wall path when no audio)
    private readonly Stopwatch _wallClock = new Stopwatch();
    private long _wallMediaOriginUs; // media time corresponding to wall start

    [Signal] public delegate void CompletedEventHandler();
    [Signal] public delegate void TimeUpdatedEventHandler(double time);

    public ActiveVideoPlayback()
    {
    }

    /// <summary>
    /// True when the component wants embedded audio and the file has an audio track.
    /// Does not guarantee an output device is bound — see <see cref="HasBoundAudioStreams"/>.
    /// </summary>
    public bool UseAudio =>
        _videoComponent.UseAudio &&
        _videoComponent.Metadata != null &&
        _videoComponent.Metadata.AudioChannels > 0;

    /// <summary>
    /// True when at least one SDL audio stream is bound and can drive the A/V master clock.
    /// </summary>
    public bool HasBoundAudioStreams => _streamBindings.HasStreams;

    public ActiveVideoPlayback(VideoComponent videoComponent, AudioDevices audioDevices)
    {
        _videoComponent = videoComponent ?? throw new ArgumentNullException(nameof(videoComponent));
        _audioDevices = audioDevices ?? throw new ArgumentNullException(nameof(audioDevices));

        _videoDecoder = new VideoSourceDecoder();
        // Only set up the embedded-audio path when an output is assigned. Without streams the
        // audio decoder position never advances and must not be used as the master clock.
        if (UseAudio && videoComponent.HasAudioOutputAssigned)
        {
            _audioDecoder = new AudioSourceDecoder();
            DeviceStreams = new Dictionary<uint, IntPtr>();
            DeviceStreamChannels = new Dictionary<uint, int>();
            Patch = videoComponent.Patch;
            Routing = videoComponent.Routing;
            DirectOutput = videoComponent.DirectOutput;
        }

        _targetLayer = DisplaysManager.Layers.Find(l => l.LayerId == _videoComponent.TargetLayerId);
        if (_targetLayer == null)
        {
            GD.PrintErr($"ActiveVideoPlayback:Constructor - Target layer {_videoComponent.TargetLayerId} not found.");
            return;
        }

        _godotImage = Image.CreateEmpty(1, 1, false, Image.Format.Rgba8);
        _godotTexture = ImageTexture.CreateFromImage(_godotImage);

        if (_videoComponent.IsImage)
        {
            // Still images: no in/out points. User Duration is hold time; 0 = until stopped.
            _startTimeUs = 0;
            if (_videoComponent.Duration > 0)
            {
                _useCustomEnd = true;
                _endTimeUs = (long)(_videoComponent.Duration * MicrosecondsPerSecond);
            }
            else
            {
                _useCustomEnd = false;
                _endTimeUs = long.MaxValue;
            }
            // Images do not multi-play; Loop with a finite duration would re-hold — keep single hold.
            EffectivePlayCount = 1;
        }
        else
        {
            _startTimeUs = (long)(Math.Max(0, _videoComponent.StartTime) * MicrosecondsPerSecond);
            _useCustomEnd = _videoComponent.EndTime >= 0;
            if (_useCustomEnd)
                _endTimeUs = (long)(_videoComponent.EndTime * MicrosecondsPerSecond);
            else if (_videoComponent.Metadata != null && _videoComponent.Metadata.Duration > 0)
                _endTimeUs = (long)(_videoComponent.Metadata.Duration * MicrosecondsPerSecond);
            else
                _endTimeUs = long.MaxValue;

            EffectivePlayCount = _videoComponent.Loop ? int.MaxValue : Math.Max(1, _videoComponent.PlayCount);

            if (_videoComponent.Metadata?.Duration > 0 &&
                _startTimeUs > (long)(_videoComponent.Metadata.Duration * MicrosecondsPerSecond))
            {
                _startTimeUs = 0;
            }
        }
    }

    /// <summary>
    /// Resolves show-relative media paths (e.g. Video/clip.mp4) to absolute paths.
    /// </summary>
    private string ResolveMediaPath(string storedPath)
    {
        var globalData = GetNodeOrNull<GlobalData>("/root/GlobalData");
        return globalData?.ResolveMediaPath(storedPath) ?? storedPath;
    }

    /// <summary>
    /// Opens video (and audio) decoders, seeks to start, prefetches.
    /// </summary>
    /// <remarks>
    /// On failure after a partial open, calls <see cref="Clean"/> so native decoders and
    /// layer hosts are not left half-initialized (callers must still free the node).
    /// </remarks>
    public async Task InitAsync()
    {
        try
        {
            GD.Print("ActiveVideoPlayback:InitAsync - Initializing...");
            RefreshPresentTuning();
            _currentPlayCount = 1;

            string mediaPath = ResolveMediaPath(_videoComponent.VideoFile);
            await _videoDecoder.OpenAsync(mediaPath);
            // Do not replace image hold end with container duration (often 0 or N/A for stills).
            if (!_videoComponent.IsImage && !_useCustomEnd && _videoDecoder.Info.DurationUs > 0)
                _endTimeUs = _videoDecoder.Info.DurationUs;

            await TryLoadSubtitlesAsync(mediaPath);

            // Seek + prefetch off the main thread (GOP discard / decode can take many ms).
            long startUs = _startTimeUs;
            int videoPrefetch = _presentTuning.PrefetchTarget;
            await Task.Run(() =>
            {
                if (startUs > 0)
                    _videoDecoder.Seek(startUs);
                _videoDecoder.Prefetch(videoPrefetch);
            });

            int w = _videoDecoder.Info.Width;
            int h = _videoDecoder.Info.Height;
            _godotImage = Image.CreateEmpty(w, h, false, Image.Format.Rgba8);
            _godotTexture = ImageTexture.CreateFromImage(_godotImage);

            foreach (var display in DisplaysManager.Outputs)
            {
                var layerControl = display.AddLayer(_videoComponent.TargetLayerId);
                var layerTextRect = layerControl.GetNode<TextureRect>("%LayerOutput");
                layerTextRect.Texture = _godotTexture;
                _targetLayers.Add(layerControl, layerTextRect);
                layerTextRect.TreeExited += () => OnLayerExited(layerTextRect);
            }

            // Stay invisible until PlayAsync / FadeInAsync — Init assigns the shared texture early,
            // so a non-zero modulate would flash the first decoded frame at full opacity.
            _fadeAlpha = 0f;
            SetVolume(0f);
            RefreshVisualProperties();

            if (_targetLayers.Count == 0)
            {
                _isExiting = true;
                EmitSignalCompleted();
                Clean();
                return;
            }

            if (_audioDecoder != null)
            {
                // Stream embedded audio (no full PCM expand) — long video soundtracks would
                // otherwise pin tens/hundreds of MB on the LOH for the whole cue lifetime.
                // Ring sized from latency mode so PreferStability prefetch (1400 ms) fits.
                RefreshAudioTuning();
                await _audioDecoder.OpenAsync(
                    mediaPath,
                    preferSampleAccurateStore: false,
                    ringMs: _audioTuning.RecommendedRingMs);
                SourceChannels = _audioDecoder.Info.Channels;
                SourceSampleRate = _audioDecoder.Info.SampleRate;
                SourceFormat = SDL.AudioFormat.AudioF32LE;
                SourceBytesPerFrame = SourceChannels * sizeof(float);

                long audioStartUs = _startTimeUs;
                int audioPrefetchMs = _audioTuning.PrefetchMs;
                await Task.Run(() =>
                {
                    if (audioStartUs > 0)
                        _audioDecoder.Seek(audioStartUs);
                    _audioDecoder.Prefetch(audioPrefetchMs);
                });

                int maxFrames = Math.Max(SourceSampleRate / 10, 1024);
                _audioSrcBuffer = new float[maxFrames * SourceChannels];
                _audioMixBuffer = new float[maxFrames * 16];
            }

            SetProcess(false); // enabled on Play
            GD.Print($"ActiveVideoPlayback:InitAsync - complete video={w}x{h} audio={_audioDecoder != null}");
        }
        catch
        {
            try { Clean(); } catch { /* ignore */ }
            throw;
        }
    }

    /// <summary>
    /// Main-thread presentation: compare master clock to next frame PTS.
    /// Requires this node to be inside the scene tree (see ActiveCue.SetupVideoComponent).
    /// </summary>
    public override void _Process(double delta)
    {
        PresentCatchUpFrames();
    }

    private void PresentFrame(VideoFrame frame)
    {
        if (frame?.Rgba == null || _isExiting) return;
        if (!IsInstanceValid(_godotImage) || !IsInstanceValid(_godotTexture)) return;

        // Apply fade/opacity BEFORE uploading pixels. TextureRects already hold this
        // ImageTexture from Init — Update() would otherwise flash a full-opacity frame.
        ApplyOpacityModulate();

        // Upload the decoder's buffer directly (SetData copies it into Godot's image, and the
        // caller releases it after this returns). Pool buffers are exactly one frame; the copy
        // below is only a guard in case one is ever larger.
        int needed = frame.Width * frame.Height * 4;
        byte[] pixels = frame.Rgba;
        if (pixels.Length != needed)
        {
            if (_displayRgba == null || _displayRgba.Length != needed)
                _displayRgba = new byte[needed];
            Buffer.BlockCopy(frame.Rgba, 0, _displayRgba, 0, needed);
            pixels = _displayRgba;
        }

        if (_godotImage.GetWidth() != frame.Width || _godotImage.GetHeight() != frame.Height)
        {
            _godotImage = Image.CreateEmpty(frame.Width, frame.Height, false, Image.Format.Rgba8);
            _godotTexture = ImageTexture.CreateFromImage(_godotImage);
        }

        _godotImage.SetData(frame.Width, frame.Height, false, Image.Format.Rgba8, pixels);
        _godotTexture.Update(_godotImage);

        foreach (var layer in _targetLayers)
        {
            if (layer.Value == null || !IsInstanceValid(layer.Value))
                continue;
            layer.Value.Texture = _godotTexture;
        }
    }

    /// <summary>
    /// Re-applies expand/stretch layout and opacity from the video component to live TextureRects.
    /// Call when the inspector changes these while the cue is already playing.
    /// </summary>
    public void RefreshVisualProperties()
    {
        if (_videoComponent == null || _isExiting)
            return;

        foreach (var kv in _targetLayers.ToList())
        {
            var rect = kv.Value;
            if (rect == null || !IsInstanceValid(rect))
                continue;
            _videoComponent.ApplyTextureLayout(rect);
        }

        ApplyOpacityModulate();
    }

    /// <summary>
    /// Whether this playback is driven by the given video component instance.
    /// </summary>
    public bool UsesVideoComponent(VideoComponent component) =>
        component != null && ReferenceEquals(_videoComponent, component);

    /// <summary>
    /// Links a text playback so closed captions update its live text during presentation.
    /// </summary>
    /// <param name="textPlayback">Active text playback on the same cue, or null to unlink.</param>
    public void SetLinkedTextPlayback(ActiveTextPlayback textPlayback)
    {
        _linkedTextPlayback = textPlayback;
        if (textPlayback != null)
            textPlayback.IsSubtitleSlave = true;
    }

    private void ApplyOpacityModulate()
    {
        float opacity;
        lock (_lock)
        {
            opacity = _runtimeOpacity ?? Mathf.Clamp(_videoComponent?.Opacity ?? 1f, 0f, 1f);
        }
        opacity = Mathf.Clamp(opacity, 0f, 1f);
        var modulate = new Color(1f, 1f, 1f, opacity * _fadeAlpha);

        foreach (var layer in _targetLayers)
        {
            if (layer.Value == null || !IsInstanceValid(layer.Value))
                continue;
            layer.Value.Modulate = modulate;
        }
    }

    /// <summary>
    /// Effective embedded-audio level for mixing (runtime control-fade override or cue component).
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
            return ControlComponent.GetVideoAudioLinear(_videoComponent);
        }
    }

    /// <inheritdoc />
    public float ReadDisplayLevel(float deltaSeconds) =>
        PlaybackLevel.Read(ref _meterPeakBits, ref _meterDisplay, deltaSeconds);

    /// <summary>
    /// Effective pan for mixing (runtime override or component). Non-stereo → 0.
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
            return Mathf.Clamp(_videoComponent?.Pan ?? 0f, -1f, 1f);
        }
    }

    /// <summary>
    /// Effective opacity for visuals (runtime control-fade override or cue component).
    /// </summary>
    public float EffectiveOpacity
    {
        get
        {
            lock (_lock)
            {
                if (_runtimeOpacity.HasValue)
                    return _runtimeOpacity.Value;
            }
            return Mathf.Clamp(_videoComponent?.Opacity ?? 1f, 0f, 1f);
        }
    }

    /// <summary>
    /// Sets a playback-only audio level (does not mutate the cue component).
    /// </summary>
    public void SetRuntimeLevelLinear(float linear)
    {
        lock (_lock)
            _runtimeLevelLinear = AudioMixMatrix.ClampComponentGainLinear(linear);
    }

    /// <summary>
    /// Sets a playback-only pan (does not mutate the cue component).
    /// </summary>
    public void SetRuntimePan(float pan)
    {
        lock (_lock)
            _runtimePan = Mathf.Clamp(pan, -1f, 1f);
    }

    /// <summary>
    /// Sets a playback-only opacity and refreshes layer modulate (does not mutate the cue component).
    /// </summary>
    public void SetRuntimeOpacity(float opacity)
    {
        lock (_lock)
            _runtimeOpacity = Mathf.Clamp(opacity, 0f, 1f);
        // Visual update must run on the main thread.
        CallDeferred(nameof(ApplyOpacityModulate));
    }

    /// <summary>
    /// After the current pass, do not repeat component Loop / remaining play count.
    /// </summary>
    public void RequestDevamp()
    {
        lock (_lock)
            _devampRequested = true;
    }

    /// <summary>
    /// Ensures <see cref="Routing"/> is a private clone, then sets one matrix cell for this playback only.
    /// </summary>
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

    /// <summary>
    /// Master media clock in microseconds.
    /// With bound audio streams: audio decode position minus average SDL stream latency.
    /// After the soundtrack drains (or with no audio output): wall clock from play start.
    /// </summary>
    /// <remarks>
    /// Must not use the audio decoder as master when no streams are bound — the fill loop
    /// never advances <see cref="AudioSourceDecoder.PositionUs"/>, so presentation freezes.
    /// Must also stop using audio as master once it has ended: many files have a shorter
    /// audio stream than video, and a frozen playhead would leave the last frames in the
    /// ring (too early to present) so the cue would never hit EOS or <c>_endTimeUs</c>.
    /// </remarks>
    private long GetMasterClockUs()
    {
        // Hold at seek target while the worker discards/prefetches so progress bars do not
        // snap back to the pre-seek audio/wall position.
        if (_seekInProgress)
            return _seekTargetUs;

        bool audioMasterExhausted;
        lock (_lock) audioMasterExhausted = _audioMasterExhausted;

        if (_audioDecoder != null && UseAudio && HasBoundAudioStreams && !audioMasterExhausted)
        {
            long audioUs = _audioDecoder.PositionUs;
            long queuedUs = 0;
            int count = 0;
            foreach (var b in _streamBindings.Snapshot)
            {
                long qb = _streamBindings.GetQueued(b);
                if (qb < 0) continue;
                int outCh = GetStreamChannels(b);
                if (outCh <= 0 || SourceSampleRate <= 0) continue;
                queuedUs += qb * MicrosecondsPerSecond / (SourceSampleRate * outCh * sizeof(float));
                count++;
            }
            if (count > 0) queuedUs /= count;
            long master = audioUs - queuedUs;
            if (master < _startTimeUs)
                master = _startTimeUs;

            // ~8ms matches present early-tolerance: treat the soundtrack as finished once
            // decode is at EOS (or past its stream duration) and SDL has drained.
            const long drainedQueuedUs = 8_000;
            long audioDurationUs = _audioDecoder.Info?.DurationUs ?? 0;
            bool audioDone = _audioDecoder.EndOfStream
                || (audioDurationUs > 0 && audioUs >= audioDurationUs);
            if (audioDone && queuedUs <= drainedQueuedUs)
            {
                lock (_lock)
                {
                    if (!_audioMasterExhausted)
                    {
                        _audioMasterExhausted = true;
                        _wallMediaOriginUs = master;
                        _wallClock.Restart();
                        GD.Print(
                            $"ActiveVideoPlayback:GetMasterClockUs - Audio master exhausted at {master}us; " +
                            "continuing on wall clock");
                    }
                }
            }

            return master;
        }

        if (!_wallClock.IsRunning)
            return _wallMediaOriginUs;
        return _wallMediaOriginUs + _wallClock.ElapsedMilliseconds * 1000;
    }

    private void HandleSegmentEnd()
    {
        bool loopAgain = false;
        lock (_lock)
        {
            if (IsStopped || _isExiting || _isFadingOut || _completedEmitted || _segmentEndScheduled)
                return;

            bool morePlays = _videoComponent.Loop || _currentPlayCount < EffectivePlayCount;
            if (morePlays && _devampRequested)
            {
                _devampRequested = false;
                morePlays = false;
            }
            if (morePlays)
            {
                _currentPlayCount++;
                _naturalEndFadeArmed = false;
                _audioMasterExhausted = false;
                loopAgain = true;
            }
            else
            {
                _segmentEndScheduled = true;
            }
        }

        if (loopAgain)
        {
            GD.Print($"ActiveVideoPlayback:HandleSegmentEnd - Loop/play {_currentPlayCount}/{EffectivePlayCount}");
            SeekInternal(_startTimeUs, restartClock: true);
            return;
        }

        GD.Print("ActiveVideoPlayback:HandleSegmentEnd - Completed");
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
    /// Starts component end-fade when remaining content is within <see cref="VideoComponent.FadeOutDuration"/>.
    /// </summary>
    /// <param name="masterUs">Current master-clock media time in microseconds.</param>
    private void TryArmNaturalEndFade(long masterUs)
    {
        double fadeDuration;
        lock (_lock)
        {
            if (IsStopped || IsPaused || _isExiting || _isFadingOut || _isFadingIn
                || _naturalEndFadeArmed || !_isPlaying)
                return;
            if ((_videoComponent.Loop || _currentPlayCount < EffectivePlayCount) && !_devampRequested)
                return;

            double configured = _videoComponent.FadeOutDuration;
            if (configured <= 1e-9 || _endTimeUs == long.MaxValue)
                return;

            long remainingUs = _endTimeUs - masterUs;
            long fadeUs = (long)(configured * 1_000_000.0);
            if (remainingUs > fadeUs)
                return;

            _naturalEndFadeArmed = true;
            double remainingSec = Math.Max(0, remainingUs / 1_000_000.0);
            fadeDuration = Math.Max(remainingSec, 1e-3);
            fadeDuration = Math.Min(fadeDuration, configured);
        }

        GD.Print($"ActiveVideoPlayback:TryArmNaturalEndFade - Starting end fade ({fadeDuration:F3}s)");
        _ = FadeOutAsync(fadeDuration);
    }

    private void CompleteFromEnd()
    {
        if (_isFadingOut || IsStopped || _isExiting)
            return;

        // User scrubbed away from the out-point after end was scheduled — stay alive.
        if (_seekInProgress)
        {
            lock (_lock) _segmentEndScheduled = false;
            return;
        }

        // Safety net if end was hit without early arm (seek into tail, etc.).
        double residual = 0;
        lock (_lock)
        {
            if (!_videoComponent.Loop && _currentPlayCount >= EffectivePlayCount)
                residual = Math.Max(0, _videoComponent.FadeOutDuration);
        }

        if (residual > 1e-9)
        {
            GD.Print($"ActiveVideoPlayback:CompleteFromEnd - Residual end fade ({residual:F3}s)");
            _ = FadeOutAsync(residual);
            return;
        }

        // Natural end without fade: stop transport but keep last frame long enough for follow handoff.
        HardStop(releaseDisplay: false);
    }

    private void OnLayerExited(TextureRect layer)
    {
        lock (_lock)
        {
            foreach (var kv in _targetLayers)
            {
                if (kv.Value == layer)
                {
                    _targetLayers.Remove(kv.Key);
                    break;
                }
            }
        }
    }

    /// <summary>
    /// Tears down the embedded-audio path so presentation can continue silently on the wall clock.
    /// Call when audio output fails to bind after the decoder was opened.
    /// </summary>
    public void DisableEmbeddedAudio()
    {
        var fillWorker = StopAudioFillLoop();

        if (DeviceStreams != null)
        {
            try { _audioDevices?.NotifyPlaybackCompleted(this); } catch { /* ignore */ }
        }
        // Detach before destroy so a fill worker that is still exiting skips the handles.
        AudioStreamBindingSet.Destroy(_streamBindings.DetachAll(DeviceStreams, DeviceStreamChannels));

        // Wall-clock master from now on; dispose the decoder once the worker has let go of it
        // (the worker reads the field once per pass, so clearing it now is safe).
        var audioDecoder = _audioDecoder;
        _audioDecoder = null;
        PlaybackWorkers.RunAfter(() =>
        {
            try { audioDecoder?.Dispose(); } catch { /* ignore */ }
        }, fillWorker);

        GD.Print("ActiveVideoPlayback:DisableEmbeddedAudio - Audio path disabled; using wall-clock master.");
    }

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
                $"ActiveVideoPlayback:OnOutputDeviceLost - Device {logicalDeviceId} lost; " +
                "no audio streams remain — continuing video on wall clock.");
            // Avoid double-notify: streams for this device already untracked; remaining empty.
            DisableEmbeddedAudio();
        }
        else
        {
            GD.Print(
                $"ActiveVideoPlayback:OnOutputDeviceLost - Device {logicalDeviceId} lost; " +
                $"{DeviceStreams.Count} audio stream(s) remain.");
        }
    }

    public async Task PlayAsync()
    {
        // Avoid a free Task.Yield() when already parented — follow handoff needs the first
        // frame this same call so the previous still can release without a black flash.
        // Yield only if we are not yet in the tree (caller is still attaching us).
        if (!IsInsideTree())
            await Task.Yield();

        lock (_lock)
        {
            if (IsStopped || _isExiting) return;
            _isPlaying = true;
            IsPaused = false;
            _audioMasterExhausted = false;
            _segmentEndScheduled = false;
            // Wall-clock origin = current media position (used when no audio master).
            // Still images always start their hold timer at 0.
            _wallMediaOriginUs = _videoComponent.IsImage
                ? 0
                : (_videoDecoder?.PositionUs ?? _startTimeUs);
            _wallClock.Restart();
            if (_videoComponent.IsImage)
                _imageFramePresented = false;

            // Reveal at full level only when no fade owns the level (FadeInAsync pre-arms alpha/volume
            // to 0; a Stop during the fade-in's PlayAsync has a fade-out in charge).
            if (!_isFadingIn && !_isFadingOut)
            {
                _fadeAlpha = 1f;
                _volume = 1f;
            }
        }

        if (!IsInsideTree())
        {
            GD.PrintErr("ActiveVideoPlayback:PlayAsync - Node is not in the scene tree; _Process will not run and video will not display. Parent must AddChild(this) before Play.");
        }

        // Push modulate before any present/prefill so the first visible frame is not full-opacity.
        ApplyOpacityModulate();

        SetProcess(true);
        StartVideoPrefetchLoop();
        // Only drive audio when streams were bound; otherwise wall-clock masters silent video.
        if (_audioDecoder != null && HasBoundAudioStreams)
        {
            // Main thread: snapshot settings before fill loop consumes _audioTuning.
            RefreshAudioTuning();
            // Prime the SDL queue before the device can underrun, then start demand fill.
            ArmDeclickRamp();
            PrefillAudioStreams();
            StartAudioFillLoop();
        }

        // Present first frame immediately so output isn't blank for a tick
        PresentCatchUpFrames();

        bool audioMaster = _audioDecoder != null && HasBoundAudioStreams;
        GD.Print($"ActiveVideoPlayback:PlayAsync - Playing (audio-master={audioMaster}, silent={!audioMaster && UseAudio}, inTree={IsInsideTree()})");
    }

    /// <summary>
    /// Shared present logic used by _Process and the first Play tick.
    /// Never heavy-decodes on the main thread: pulls only from the ring; the prefetch
    /// worker refills. Skips while a seek worker owns the decoder.
    /// </summary>
    private void PresentCatchUpFrames()
    {
        if (!_isPlaying || IsPaused || IsStopped || _isExiting || _videoDecoder == null)
            return;

        // Seek/discard holds the decoder lock for a long time — skip present rather than stall UI.
        // Still emit TimeUpdated at the seek target so progress bars hold (not snap back).
        if (_seekInProgress)
        {
            EmitSignal(SignalName.TimeUpdated, _seekTargetUs / (double)MicrosecondsPerSecond);
            return;
        }

        long masterUs = GetMasterClockUs();
        EmitSignal(SignalName.TimeUpdated, masterUs / (double)MicrosecondsPerSecond);
        UpdateLinkedSubtitles(masterUs);

        // Arm end-fade early so FadeOutDuration runs inside the last seconds of content
        // (e.g. 10s segment + 4s fade → fade begins at t=6s).
        TryArmNaturalEndFade(masterUs);

        bool fadingOut;
        lock (_lock) fadingOut = _isFadingOut;

        if (masterUs >= _endTimeUs)
        {
            // While end-fading, hold the last frame until FadeOutAsync HardStops.
            if (fadingOut)
            {
                ApplyOpacityModulate();
                return;
            }
            HandleSegmentEnd();
            return;
        }

        // Still image: take one buffered frame (init already prefetched), then hold.
        // Keep re-applying modulate every tick so stop fade-out / fade-in alpha is visible
        // (video gets this for free via PresentFrame; stills would otherwise freeze at full opacity).
        if (_videoComponent.IsImage)
        {
            if (!_imageFramePresented)
            {
                if (_videoDecoder.TryTakeFrame(out VideoFrame frame))
                {
                    PresentFrame(frame);
                    _videoDecoder.ReleaseFrameBuffer(frame.Rgba);
                    _imageFramePresented = true;
                }
                else if (_videoDecoder.EndOfStream && !_imageFramePresented)
                {
                    // No frame available — fail closed so the cue does not hang forever blank.
                    GD.PrintErr("ActiveVideoPlayback:PresentCatchUpFrames - Image produced no frames.");
                    CallDeferred(nameof(CompleteFromEnd));
                }
            }
            else
            {
                ApplyOpacityModulate();
            }
            return;
        }

        // Pick up show pref changes on the main thread so they apply without restart.
        // Audio fill threads consume the last _audioTuning snapshot only (no GetNode off-thread).
        RefreshTuningIfSettingsChanged();

        int presented = 0;
        int maxPresentPerTick = _presentTuning.MaxPresentPerTick;
        // Cap late drops per tick so a large catch-up never monopolises the main thread.
        // Drops are counted on their own and do not use up the present budget.
        int maxLateDrops = Math.Max(maxPresentPerTick, 8);
        int lateDrops = 0;
        while (presented < maxPresentPerTick)
        {
            // Ring-only: never DecodeMore on the present path.
            if (!_videoDecoder.TryPeekPtsBuffered(out long nextPts))
            {
                if (_videoDecoder.EndOfStream)
                    HandleSegmentEnd();
                break;
            }

            // Frame is at/past the out-point — do not wait for a clock that may stall short of it.
            if (_endTimeUs < long.MaxValue && nextPts >= _endTimeUs)
            {
                HandleSegmentEnd();
                break;
            }

            long lateness = masterUs - nextPts;
            if (lateness < -_presentTuning.PresentEarlyToleranceUs)
                break;

            if (!_videoDecoder.TryTakeFrame(out VideoFrame frame))
                break;

            if (lateness > _presentTuning.MaxLatenessUs
                && lateDrops < maxLateDrops
                && _videoDecoder.TryPeekPtsBuffered(out long peek2)
                && peek2 <= masterUs)
            {
                _videoDecoder.ReleaseFrameBuffer(frame.Rgba);
                lateDrops++;
                continue;
            }

            PresentFrame(frame);
            _videoDecoder.ReleaseFrameBuffer(frame.Rgba);
            presented++;
        }
    }

    /// <summary>
    /// Per-frame check: re-resolves present and audio tuning only when the show settings
    /// object or one of its quality/latency/de-click values changed since the last call,
    /// instead of looking up GlobalData and rebuilding the tuning every frame. Main thread only.
    /// </summary>
    private void RefreshTuningIfSettingsChanged()
    {
        if (_tuningGlobalData == null || !IsInstanceValid(_tuningGlobalData))
            _tuningGlobalData = GetNodeOrNull<GlobalData>("/root/GlobalData");
        var settings = _tuningGlobalData?.Settings;
        if (settings != null
            && ReferenceEquals(settings, _tuningSettings)
            && settings.VideoQualityMode == _tuningVideoMode
            && settings.AudioLatencyMode == _tuningAudioMode
            && settings.AudioDeclickMs == _tuningDeclickMs)
            return;

        _tuningSettings = settings;
        if (settings != null)
        {
            _tuningVideoMode = settings.VideoQualityMode;
            _tuningAudioMode = settings.AudioLatencyMode;
            _tuningDeclickMs = settings.AudioDeclickMs;
        }
        RefreshPresentTuning();
        RefreshAudioTuning();
    }

    /// <summary>
    /// Pulls current show quality mode into present/prefetch knobs.
    /// </summary>
    private void RefreshPresentTuning()
    {
        try
        {
            var settings = GetNodeOrNull<GlobalData>("/root/GlobalData")?.Settings;
            if (settings != null)
                _presentTuning = settings.GetVideoPresentTuning();
            else
                _presentTuning = VideoPresentTuning.ForMode(VideoQualityMode.Balanced);
        }
        catch
        {
            _presentTuning = VideoPresentTuning.ForMode(VideoQualityMode.Balanced);
        }
    }

    public void Pause()
    {
        lock (_lock)
        {
            if (IsPaused || IsStopped) return;
            IsPaused = true;
            _pausedAtUs = GetMasterClockUs();
            _wallClock.Stop();
        }

        _streamBindings.ClearAll();
        _audioDecoder?.FlushBuffers();
        GD.Print($"ActiveVideoPlayback:Pause - at {_pausedAtUs / 1000} ms");
    }

    public void Resume()
    {
        long resumeAtUs = 0;
        lock (_lock)
        {
            if (!IsPaused || IsStopped) return;
            resumeAtUs = _pausedAtUs;
            _pausedAtUs = 0;
            // Unpause before async seek so FinishSeekWorker can re-prime audio.
            IsPaused = false;
            _wallClock.Restart();
        }

        if (resumeAtUs > 0)
        {
            // Seek worker holds fill until decode+prefetch complete, then primes.
            SeekInternal(resumeAtUs, restartClock: true);
        }
        else if (_audioDecoder != null && HasBoundAudioStreams)
        {
            // Streams were cleared on pause; re-prime before the fill loop can underrun.
            RefreshAudioTuning();
            ArmDeclickRamp();
            PrefillAudioStreams();
        }

        GD.Print("ActiveVideoPlayback:Resume");
    }

    /// <summary>
    /// Drops decoder, display, and mix buffers so large arrays become collectible.
    /// </summary>
    private async Task TryLoadSubtitlesAsync(string mediaPath)
    {
        try
        {
            _subtitleDecoder?.Dispose();
            _subtitleDecoder = null;
            _lastSubtitleText = string.Empty;

            if (_videoComponent == null || _videoComponent.IsImage || !_videoComponent.UseSubtitles)
                return;

            var track = _videoComponent.ResolveSubtitleTrack();
            if (track == null)
            {
                GD.Print("ActiveVideoPlayback:TryLoadSubtitlesAsync - No text subtitle track selected.");
                return;
            }

            var decoder = new SubtitleSourceDecoder();
            if (track.IsExternal)
            {
                await decoder.LoadExternalAsync(track.ExternalFilePath);
            }
            else
            {
                await decoder.LoadAsync(mediaPath, track.StreamIndex);
            }

            if (!decoder.IsLoaded || decoder.CueCount == 0)
            {
                decoder.Dispose();
                GD.PrintErr(
                    $"ActiveVideoPlayback:TryLoadSubtitlesAsync - No cues loaded " +
                    $"(stream={track.StreamIndex}, external={track.ExternalFilePath}).");
                return;
            }

            _subtitleDecoder = decoder;
            GD.Print(
                $"ActiveVideoPlayback:TryLoadSubtitlesAsync - Loaded {decoder.CueCount} cues " +
                $"from {(track.IsExternal ? track.ExternalFilePath : $"stream {track.StreamIndex}")}.");
        }
        catch (Exception ex)
        {
            GD.PrintErr($"ActiveVideoPlayback:TryLoadSubtitlesAsync - {ex.Message}");
            try { _subtitleDecoder?.Dispose(); } catch { /* ignore */ }
            _subtitleDecoder = null;
        }
    }

    private void UpdateLinkedSubtitles(long masterUs)
    {
        if (_linkedTextPlayback == null || _subtitleDecoder == null || !_subtitleDecoder.IsLoaded)
            return;
        if (!IsInstanceValid(_linkedTextPlayback))
        {
            _linkedTextPlayback = null;
            return;
        }

        string text = _subtitleDecoder.GetTextAtUs(masterUs) ?? string.Empty;
        if (string.Equals(text, _lastSubtitleText, StringComparison.Ordinal))
            return;

        _lastSubtitleText = text;
        _linkedTextPlayback.SetLiveTextOverride(text);
    }

    private void ClearLinkedSubtitles()
    {
        _lastSubtitleText = string.Empty;
        try
        {
            if (_linkedTextPlayback != null && IsInstanceValid(_linkedTextPlayback))
                _linkedTextPlayback.ClearLiveTextOverride();
        }
        catch
        {
            // best-effort
        }
    }

    private void ReleaseMediaBuffers()
    {
        ReleaseWorkerResources();
        ReleaseDisplayResources();
    }

    /// <summary>
    /// Disposes the decoders and drops the audio mix buffers. Only once no prefetch, fill or
    /// seek worker can use them. Idempotent; uses no Godot API on this object, so it
    /// is safe after the node was freed with its parent.
    /// </summary>
    private void ReleaseWorkerResources()
    {
        try { _audioDecoder?.Dispose(); } catch { /* ignore */ }
        _audioDecoder = null;
        try { _videoDecoder?.Dispose(); } catch { /* ignore */ }
        _videoDecoder = null;

        if (_audioSrcBuffer != null)
        {
            MediaMemory.NoteReleased(MediaMemory.FloatBufferBytes(_audioSrcBuffer));
            _audioSrcBuffer = null;
        }
        if (_audioMixBuffer != null)
        {
            MediaMemory.NoteReleased(MediaMemory.FloatBufferBytes(_audioMixBuffer));
            _audioMixBuffer = null;
        }
    }

    /// <summary>
    /// Releases main-thread display state: subtitles, the display copy buffer, layer textures
    /// and the Godot image/texture. Main thread only.
    /// </summary>
    private void ReleaseDisplayResources()
    {
        ClearLinkedSubtitles();
        try { _subtitleDecoder?.Dispose(); } catch { /* ignore */ }
        _subtitleDecoder = null;

        if (_displayRgba != null)
        {
            MediaMemory.NoteReleased(MediaMemory.ByteBufferBytes(_displayRgba));
            _displayRgba = null;
        }

        // Detach textures from output layers so Godot can free GPU resources
        foreach (var kv in _targetLayers)
        {
            try
            {
                if (IsInstanceValid(kv.Value))
                    kv.Value.Texture = null;
                if (IsInstanceValid(kv.Key))
                    kv.Key.QueueFree();
            }
            catch { /* ignore */ }
        }
        _targetLayers.Clear();

        if (_godotTexture != null && IsInstanceValid(_godotTexture))
        {
            try { _godotTexture.Dispose(); } catch { /* ignore */ }
        }
        _godotTexture = null;
        if (_godotImage != null && IsInstanceValid(_godotImage))
        {
            try { _godotImage.Dispose(); } catch { /* ignore */ }
        }
        _godotImage = null;
    }

    public override void _ExitTree()
    {
        if (_isDisposed) return;
        _isExiting = true;
        IsStopped = true;
        _isPlaying = false;
        SetProcess(false);
        var seekWorker = _seekTask;
        CancelSeekWorker();
        var prefetchWorker = StopVideoPrefetchLoop();
        var fillWorker = StopAudioFillLoop();
        // Leaving the tree without Clean (app quit, parent freed): teardown must finish before
        // this returns, so a bounded wait is kept here only.
        PlaybackWorkers.WaitBounded(500, seekWorker, prefetchWorker, fillWorker);
        AudioStreamBindingSet.Destroy(_streamBindings.DetachAll(DeviceStreams, DeviceStreamChannels));
        ReleaseMediaBuffers();
        MediaMemory.ReclaimIfNeeded();
        _isDisposed = true;
    }
}
