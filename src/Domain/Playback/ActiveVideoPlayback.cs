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
/// to stay within a tolerance of that clock. On seek/pause/loop both streams are reset to
/// the same media timestamp.
/// </summary>
public partial class ActiveVideoPlayback : Node, IAudioPlayback
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
    private byte[] _displayRgba;

    public AudioOutputPatch Patch { get; set; }
    public CuePatch Routing { get; set; }
    public string DirectOutput { get; set; }
    public Dictionary<uint, IntPtr> DeviceStreams { get; set; }
    public Dictionary<uint, int> DeviceStreamChannels { get; set; }
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

    /// <summary>Frames remaining in the post-start/seek de-click ramp (0 = inactive).</summary>
    private int _declickFramesRemaining;
    /// <summary>Total frames for the current de-click ramp (fixed when armed).</summary>
    private int _declickRampTotalFrames;
    /// <summary>
    /// When true, the audio fill loop must not Put to SDL (seek/clear in progress).
    /// Separate from public <see cref="IsSeeking"/>, which is also used for UI scrub preview.
    /// </summary>
    private bool _audioFillSuspended;

    private CancellationTokenSource _videoPrefetchCts;
    private Task _videoPrefetchTask;

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

    /// <summary>
    /// When set, replaces <see cref="VideoComponent.Pan"/> for this playback only (control fades).
    /// </summary>
    private float? _runtimePan;

    /// <summary>
    /// When set, replaces <see cref="VideoComponent.Opacity"/> for this playback only (control fades).
    /// </summary>
    private float? _runtimeOpacity;

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
    public bool HasBoundAudioStreams =>
        DeviceStreams != null && DeviceStreams.Count > 0;

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
            _displayRgba = new byte[_videoDecoder.Info.FrameByteSize];

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

        int needed = frame.Width * frame.Height * 4;
        if (_displayRgba == null || _displayRgba.Length < needed)
            _displayRgba = new byte[needed];
        Buffer.BlockCopy(frame.Rgba, 0, _displayRgba, 0, needed);

        if (_godotImage.GetWidth() != frame.Width || _godotImage.GetHeight() != frame.Height)
        {
            _godotImage = Image.CreateEmpty(frame.Width, frame.Height, false, Image.Format.Rgba8);
            _godotTexture = ImageTexture.CreateFromImage(_godotImage);
        }

        _godotImage.SetData(frame.Width, frame.Height, false, Image.Format.Rgba8, _displayRgba);
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
    /// Without audio output (silent video / failed bind): wall clock from play start.
    /// </summary>
    /// <remarks>
    /// Must not use the audio decoder as master when no streams are bound — the fill loop
    /// never advances <see cref="AudioSourceDecoder.PositionUs"/>, so presentation freezes.
    /// </remarks>
    private long GetMasterClockUs()
    {
        // Hold at seek target while the worker discards/prefetches so progress bars do not
        // snap back to the pre-seek audio/wall position.
        if (_seekInProgress)
            return _seekTargetUs;

        if (_audioDecoder != null && UseAudio && HasBoundAudioStreams)
        {
            long audioUs = _audioDecoder.PositionUs;
            long queuedUs = 0;
            int count = 0;
            foreach (var kv in DeviceStreams)
            {
                long qb = SDL.GetAudioStreamQueued(kv.Value);
                int outCh = GetStreamChannels(kv.Key);
                if (outCh <= 0 || SourceSampleRate <= 0) continue;
                queuedUs += qb * MicrosecondsPerSecond / (SourceSampleRate * outCh * sizeof(float));
                count++;
            }
            if (count > 0) queuedUs /= count;
            long master = audioUs - queuedUs;
            return master < _startTimeUs ? _startTimeUs : master;
        }

        if (!_wallClock.IsRunning)
            return _wallMediaOriginUs;
        return _wallMediaOriginUs + _wallClock.ElapsedMilliseconds * 1000;
    }

    private void HandleSegmentEnd()
    {
        lock (_lock)
        {
            if (IsStopped || _isExiting || _isFadingOut) return;

            if (_videoComponent.Loop || _currentPlayCount < EffectivePlayCount)
            {
                _currentPlayCount++;
                _naturalEndFadeArmed = false;
                GD.Print($"ActiveVideoPlayback:HandleSegmentEnd - Loop/play {_currentPlayCount}/{EffectivePlayCount}");
                SeekInternal(_startTimeUs, restartClock: true);
                return;
            }
        }

        GD.Print("ActiveVideoPlayback:HandleSegmentEnd - Completed");
        CallDeferred(nameof(CompleteFromEnd));
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
            if (_videoComponent.Loop || _currentPlayCount < EffectivePlayCount)
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
        StopAudioFillLoop();

        if (DeviceStreams != null)
        {
            try { _audioDevices?.NotifyPlaybackCompleted(this); } catch { /* ignore */ }
            foreach (var stream in DeviceStreams.Values)
            {
                try { SDL.DestroyAudioStream(stream); } catch { /* ignore */ }
            }
            DeviceStreams.Clear();
        }
        DeviceStreamChannels?.Clear();

        try { _audioDecoder?.Dispose(); } catch { /* ignore */ }
        _audioDecoder = null;

        GD.Print("ActiveVideoPlayback:DisableEmbeddedAudio - Audio path disabled; using wall-clock master.");
    }

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
            // Wall-clock origin = current media position (used when no audio master).
            // Still images always start their hold timer at 0.
            _wallMediaOriginUs = _videoComponent.IsImage
                ? 0
                : (_videoDecoder?.PositionUs ?? _startTimeUs);
            _wallClock.Restart();
            if (_videoComponent.IsImage)
                _imageFramePresented = false;

            // Reveal at full level only when not mid fade-in (FadeInAsync pre-arms alpha/volume to 0).
            if (!_isFadingIn)
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

        // Re-read show prefs on the main thread so mid-show changes apply without restart.
        // Audio fill threads consume the last _audioTuning snapshot only (no GetNode off-thread).
        RefreshPresentTuning();
        RefreshAudioTuning();

        int presented = 0;
        int maxPresentPerTick = _presentTuning.MaxPresentPerTick;
        // Cap late drops per tick so a large catch-up never monopolises the main thread.
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
                presented++;
                continue;
            }

            PresentFrame(frame);
            _videoDecoder.ReleaseFrameBuffer(frame.Rgba);
            presented++;
        }
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

        if (DeviceStreams != null)
        {
            foreach (var stream in DeviceStreams.Values)
                SDL.ClearAudioStream(stream);
        }
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

        lock (_lock)
        {
            _audioFillSuspended = true;
        }

        if (DeviceStreams != null)
        {
            foreach (var stream in DeviceStreams.Values)
                SDL.ClearAudioStream(stream);
        }

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
            if (releaseFillHold)
            {
                lock (_lock)
                    _audioFillSuspended = false;
            }
        }
    }

    private void StartVideoPrefetchLoop()
    {
        StopVideoPrefetchLoop();
        _videoPrefetchCts = new CancellationTokenSource();
        var token = _videoPrefetchCts.Token;
        _videoPrefetchTask = Task.Run(() =>
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    bool paused;
                    lock (_lock) paused = IsPaused || IsStopped || _isExiting || !_isPlaying;
                    if (paused || _seekInProgress)
                    {
                        Thread.Sleep(10);
                        continue;
                    }

                    if (_videoDecoder != null &&
                        _videoDecoder.BufferedFrames < _presentTuning.PrefetchLowWater &&
                        !_videoDecoder.EndOfStream)
                    {
                        _videoDecoder.Prefetch(_presentTuning.PrefetchTarget);
                    }
                    Thread.Sleep(4);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                GD.PrintErr($"ActiveVideoPlayback:VideoPrefetch - {ex.Message}");
            }
        }, token);
    }

    private void StopVideoPrefetchLoop()
    {
        try
        {
            _videoPrefetchCts?.Cancel();
            _videoPrefetchTask?.Wait(500);
        }
        catch { /* ignore */ }
        finally
        {
            _videoPrefetchCts?.Dispose();
            _videoPrefetchCts = null;
            _videoPrefetchTask = null;
        }
    }

    private void StartAudioFillLoop()
    {
        StopAudioFillLoop();
        _audioFillCts = new CancellationTokenSource();
        var token = _audioFillCts.Token;
        _audioFillTask = Task.Run(() => AudioFillLoop(token), token);
    }

    private void StopAudioFillLoop()
    {
        try
        {
            _audioFillCts?.Cancel();
            _audioFillTask?.Wait(500);
        }
        catch { /* ignore */ }
        finally
        {
            _audioFillCts?.Dispose();
            _audioFillCts = null;
            _audioFillTask = null;
        }
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

                if (DeviceStreams == null || DeviceStreams.Count == 0 || _audioDecoder == null)
                {
                    Thread.Sleep(AudioFillSleepMs);
                    continue;
                }

                bool anyNeed = false;
                int maxNeedFrames = 0;
                foreach (var kv in DeviceStreams)
                {
                    long queued = SDL.GetAudioStreamQueued(kv.Value);
                    int outCh = GetStreamChannels(kv.Key);
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
                long pos = _audioDecoder.PositionUs;
                if (pos >= _endTimeUs)
                {
                    Thread.Sleep(AudioFillSleepMs);
                    continue;
                }

                int frames = _audioDecoder.Read(_audioSrcBuffer.AsSpan(), framesToRead, token);
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

                PushMixedAudioFrames(frames);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            GD.PrintErr($"ActiveVideoPlayback:AudioFillLoop - {ex.Message}");
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
    /// Applies the active de-click gain curve to interleaved float samples (in-place).
    /// Shared across devices for one Push so all streams stay phase-aligned on the ramp.
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
            // Raised cosine: smooth 0→1 with zero slope at endpoints (better than linear for clicks).
            float gain = 0.5f * (1f - MathF.Cos(MathF.PI * t));
            int baseIdx = f * channels;
            for (int c = 0; c < channels; c++)
                interleaved[baseIdx + c] *= gain;
            _declickFramesRemaining--;
        }
    }

    /// <summary>
    /// Fills each bound SDL stream up to the configured target buffer before play/after seek.
    /// Prevents the device from consuming an empty queue (primary underrun click source).
    /// </summary>
    private void PrefillAudioStreams()
    {
        if (_audioDecoder == null || DeviceStreams == null || DeviceStreams.Count == 0)
            return;
        if (_audioSrcBuffer == null || SourceSampleRate <= 0 || SourceChannels <= 0)
            return;

        // Cap work: ~target buffer / chunk size iterations; enough for one full prime.
        // Uses _audioTuning last refreshed on the main thread (never call GetNode here).
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

            if (_audioDecoder.PositionUs >= _endTimeUs)
                break;

            int maxFrames = _audioSrcBuffer.Length / SourceChannels;
            int framesToRead = Math.Min(maxNeedFrames, maxFrames);
            int frames = _audioDecoder.Read(_audioSrcBuffer.AsSpan(), framesToRead);
            if (frames <= 0)
                break;

            PushMixedAudioFrames(frames);
        }
    }

    private unsafe void PushMixedAudioFrames(int frames)
    {
        if (DeviceStreams == null || _isExiting) return;

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

        // Snapshot de-click state so multi-device push uses the same ramp positions.
        int declickRemainSnapshot = _declickFramesRemaining;
        int declickTotalSnapshot = _declickRampTotalFrames;

        foreach (var kv in DeviceStreams)
        {
            int outCh = GetStreamChannels(kv.Key);
            int outSamples = frames * outCh;
            if (outSamples > _audioMixBuffer.Length)
                _audioMixBuffer = new float[outSamples];

            string deviceName = _audioDevices.GetAudioDeviceByLogicalId(kv.Key)?.Name;
            AudioMixMatrix.Mix(
                _audioSrcBuffer.AsSpan(0, frames * SourceChannels),
                frames,
                SourceChannels,
                _audioMixBuffer.AsSpan(0, outSamples),
                outCh,
                masterVol,
                componentVol,
                pan,
                Routing,
                Patch,
                deviceName,
                isDirect);

            // Restore ramp counters per device so each stream gets the same envelope.
            _declickFramesRemaining = declickRemainSnapshot;
            _declickRampTotalFrames = declickTotalSnapshot;
            ApplyDeclickRamp(_audioMixBuffer.AsSpan(0, outSamples), frames, outCh);

            // Peak clamp + silence floor (show Audio settings) before handing PCM to SDL.
            if (_audioDevices != null)
            {
                _audioDevices.GetOutputLimits(out float maxAbs, out float minAbs);
                AudioMixMatrix.ApplyOutputLimits(_audioMixBuffer.AsSpan(0, outSamples), maxAbs, minAbs);
            }

            int byteCount = outSamples * sizeof(float);
            fixed (float* p = _audioMixBuffer)
            {
                SDL.PutAudioStreamData(kv.Value, (IntPtr)p, byteCount);
            }
        }

        // Advance de-click once for the whole multi-device write (all devices already applied it).
        if (declickRemainSnapshot > 0)
            _declickFramesRemaining = Math.Max(0, declickRemainSnapshot - frames);
    }

    /// <summary>
    /// Stops playback. First call with <paramref name="fadeTime"/> &gt; 0 (or cue FadeOutDuration)
    /// starts a fade-out; a second call while fading hard-stops immediately.
    /// </summary>
    /// <param name="fadeTime">Stop-fade seconds from settings; 0 forces immediate stop on first press if the cue has no own fade.</param>
    public async Task Stop(double fadeTime = 0.0)
    {
        bool needFade = false;
        double fadeDuration = 0;
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
            await FadeOutAsync(fadeDuration);
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
        StopVideoPrefetchLoop();
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

    public async Task FadeInAsync(double duration)
    {
        lock (_lock)
        {
            if (_isFadingIn || _isFadingOut) return;
            _isFadingIn = true;
            _fadeCts = new CancellationTokenSource();
            // Arm silent state before PlayAsync presents or prefills audio.
            _fadeAlpha = 0f;
            _volume = 0f;
        }

        float startVol = 0f;
        float endVol = 1.0f;
        float startAlpha = 0f;
        ApplyOpacityModulate();
        Stopwatch timer = Stopwatch.StartNew();
        await PlayAsync();

        try
        {
            while (timer.Elapsed.TotalSeconds < duration && !_fadeCts.Token.IsCancellationRequested)
            {
                float t = (float)(timer.Elapsed.TotalSeconds / duration);
                SetVolume(Mathf.Lerp(startVol, endVol, t));
                _fadeAlpha = Mathf.Lerp(startAlpha, 1.0f, t);
                ApplyOpacityModulate();
                await Task.Delay(FadeUpdateIntervalMs, _fadeCts.Token);
            }
            if (!_fadeCts.Token.IsCancellationRequested)
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
            lock (_lock)
            {
                _isFadingIn = false;
                _fadeCts?.Dispose();
                _fadeCts = null;
            }
        }
    }

    public async Task FadeOutAsync(double duration)
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

        float startVol = _volume;
        float startAlpha = _fadeAlpha;
        var token = _fadeCts.Token;
        Stopwatch timer = Stopwatch.StartNew();

        try
        {
            while (timer.Elapsed.TotalSeconds < duration && !token.IsCancellationRequested)
            {
                float t = (float)(timer.Elapsed.TotalSeconds / duration);
                SetVolume(Mathf.Lerp(startVol, 0f, t));
                _fadeAlpha = Mathf.Lerp(startAlpha, 0f, t);
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
        CancelSeekWorker();
        StopVideoPrefetchLoop();
        StopAudioFillLoop();
        _wallClock.Stop();

        if (DeviceStreams != null)
        {
            try { _audioDevices?.NotifyPlaybackCompleted(this); } catch { /* ignore */ }
            foreach (var stream in DeviceStreams.Values)
            {
                try { SDL.DestroyAudioStream(stream); } catch { /* ignore */ }
            }
            DeviceStreams.Clear();
        }
        DeviceStreamChannels?.Clear();

        // Prefer signaling completion before tearing down the display when Clean is the
        // first completion path (user Stop). Natural end already emitted while holding frame.
        EmitCompletedIfNeeded();

        ReleaseMediaBuffers();

        lock (_lock)
        {
            _isDisposed = true;
        }

        // Return LOH memory after large frame/PCM releases
        MediaMemory.ReclaimIfNeeded();

        // Defer free — Clean often runs inside signal/method dispatch (object locked).
        // QueueFree is safe for Nodes in the tree; otherwise use C# Free via Callable.
        if (IsInstanceValid(this))
        {
            if (IsInsideTree())
                CallDeferred(Node.MethodName.QueueFree);
            else
                Callable.From(FreeDeferred).CallDeferred();
        }
    }

    /// <summary>
    /// Invokes <see cref="GodotObject.Free"/> if this instance is still valid (not in tree).
    /// </summary>
    private void FreeDeferred()
    {
        if (IsInstanceValid(this))
            Free();
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
        ClearLinkedSubtitles();
        try { _subtitleDecoder?.Dispose(); } catch { /* ignore */ }
        _subtitleDecoder = null;

        try { _audioDecoder?.Dispose(); } catch { /* ignore */ }
        _audioDecoder = null;
        try { _videoDecoder?.Dispose(); } catch { /* ignore */ }
        _videoDecoder = null;

        if (_displayRgba != null)
        {
            MediaMemory.NoteReleased(MediaMemory.ByteBufferBytes(_displayRgba));
            _displayRgba = null;
        }
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
        CancelSeekWorker();
        StopVideoPrefetchLoop();
        StopAudioFillLoop();
        if (DeviceStreams != null)
        {
            foreach (var stream in DeviceStreams.Values)
            {
                try { SDL.DestroyAudioStream(stream); } catch { /* ignore */ }
            }
            DeviceStreams.Clear();
        }
        ReleaseMediaBuffers();
        MediaMemory.ReclaimIfNeeded();
        _isDisposed = true;
    }
}
