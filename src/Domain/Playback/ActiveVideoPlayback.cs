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
using Cue2.Domain.ShowSettings;
using Cue2.Services;
using Cue2.Media;
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
/// A/V sync strategy: audio is the master clock when present; each tick uploads only the
/// newest frame that is due. When the soundtrack ends before the video
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
    private Image _uvImage;
    private ImageTexture _uvTexture;
    private ShaderMaterial _yuvMaterial;
    private bool _presentPlanar;
    private string _statsUpload = "rgba";
    private int _appliedMatrixId = int.MinValue;
    private int _appliedLimited = int.MinValue;
    /// <summary>Fallback upload buffer, only used if a decoder buffer is not exactly one frame.</summary>
    private byte[] _displayRgba;

    /// <summary>Last opacity written to layer modulate. NaN forces the next write.</summary>
    private float _appliedOpacity = float.NaN;

    /// <summary>Last fade alpha written to layer modulate.</summary>
    private float _appliedFadeAlpha = float.NaN;

    private bool _summaryLogged;
    private ulong _nextStatsLogMs;
    private int _presentsThisTick;
    private long _shown;
    private long _superseded;
    private long _lateShown;
    private long _maxLatenessUs = long.MinValue;
    private int _maxPresentsInOneTick;
    private long _setDataTicks;
    private long _setDataMaxTicks;
    private long _updateTicks;
    private long _updateMaxTicks;
    private long _presentLoopTicks;
    private long _presentLoopMaxTicks;
    private long _presentLoopCount;

    // Present cadence diagnostics (main thread only).
    private long _prevTickTimestamp;
    private long _tickIntervalTicks;
    private long _tickIntervalMaxTicks;
    private long _tickIntervalCount;
    private long _prevRawMasterUs = long.MinValue;
    private long _clockStepMaxUs;
    private long _clockStallTicks;
    private long _clockSnaps;
    private bool _lastClockFromAudio;

    // Audio clock published by the code that queues PCM: the media time at the end of
    // everything queued so far. Odd sequence = a push is in progress (values may disagree).
    private long _audioQueuedEndUs;
    private int _audioClockSeq;
    // Last consistent audio master (main thread), used when a push is in progress.
    private long _lastAudioMasterUs = long.MinValue;
    private long _lastAudioEndUs;
    private long _lastAudioQueuedUs;

    // Smoothed clock used to choose frames (main thread only). See SmoothPresentClock.
    private bool _presentClockValid;
    private long _presentClockUs;
    private long _presentClockAtTimestamp;

    /// <summary>A present clock further than this from the audio clock is reset to it.</summary>
    private const long PresentClockSnapUs = 100_000;

    /// <summary>Longer than this between present ticks (pause, seek, hitch) resets the present clock.</summary>
    private const long PresentClockMaxGapUs = 100_000;

    /// <summary>Each tick corrects this fraction (1/n) of the gap to the audio clock.</summary>
    private const long PresentClockGainDivisor = 10;

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

    private readonly PlaybackStartTrace _startTrace = new PlaybackStartTrace();

    /// <summary>Sets the label and GO time (Stopwatch ticks, 0 = unknown) logged for the next play.</summary>
    public void PrepareStartTrace(string label, long goTicks) => _startTrace.Prepare(label, goTicks);

    /// <summary>Stacking position on the output layer (see <see cref="Cue2.Domain.Devices.VideoOutputDevice.ReserveLayerStackOrder"/>); 0 = when added.</summary>
    public long LayerStackOrder { get; set; }

    /// <summary>Moves this playback's output layers to <paramref name="stackOrder"/> on every output.</summary>
    public void Restack(long stackOrder)
    {
        LayerStackOrder = stackOrder;
        if (DisplaysManager.Outputs == null)
            return;
        foreach (var host in _targetLayers.Keys.ToList())
        {
            foreach (var display in DisplaysManager.Outputs)
            {
                if (display != null && IsInstanceValid(display) && display.SetLayerStackOrder(host, stackOrder))
                    break;
            }
        }
    }

    private AudioSourceDecoder _audioDecoder;
    private CancellationTokenSource _audioFillCts;
    private Task _audioFillTask;
    private float[] _audioSrcBuffer;
    private float[] _audioMixBuffer;
    private readonly AudioRatePitchProcessor _ratePitch = new();

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
    /// <summary>Latest live-scrub target; applied when the current seek worker finishes.</summary>
    private long _queuedSeekUs;
    private bool _seekQueued;
    /// <summary>Timestamp the running seek worker was started with (compare before cancelling).</summary>
    private long _inFlightSeekUs;
    /// <summary>
    /// Master-clock park after a seek (microseconds), or −1 when live. Stays set until a
    /// post-seek frame near this PTS is presented so leftover ring pictures cannot snap back.
    /// </summary>
    private long _clockHoldUs = -1;
    /// <summary>Do not cancel a running worker when the new target is within this of its start time.</summary>
    private const long SeekSameTargetEpsilonUs = 2_000;
    /// <summary>Ring frames earlier than hold − this are treated as pre-seek leftovers.</summary>
    private const long ClockHoldStaleFrameUs = 50_000;

    /// <summary>
    /// True while an async decoder seek is in flight, or while the master clock is parked
    /// on the seek target waiting for a post-seek frame. Progress UI should not sample the
    /// live master clock until this clears.
    /// </summary>
    public bool IsDecoderSeeking => _seekInProgress || _clockHoldUs >= 0;

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
    private List<AudioTimelineRegion> _regions = new();
    /// <summary>Cached wall-clock of one play of each region (rate curve).</summary>
    private double[] _regionWallSeconds = Array.Empty<double>();
    private int _regionIndex;
    private int _regionPlayIndex = 1;
    private long _regionStartUs;
    private long _regionEndUs;
    /// <summary>True while a deferred <see cref="HandleRegionBoundary"/> is already queued.</summary>
    private volatile bool _regionBoundaryQueued;
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

        _videoDecoder = new VideoSourceDecoder { DeliverPlanar420 = true };
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

        RebuildPlaybackRegions();
        ApplyRegionUnlocked(0);
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
            long initStart = Stopwatch.GetTimestamp();
            RefreshPresentTuning();
            _currentPlayCount = 1;

            string mediaPath = ResolveMediaPath(_videoComponent.VideoFile);
            var showSettings = GetNodeOrNull<GlobalData>("/root/GlobalData")?.Settings;
            _videoDecoder.AllowHardwareDecode = showSettings == null
                || showSettings.HardwareDecodeMode != HardwareDecodeMode.Off;
            // Planes need the colour shader. Without it, fall back to RGBA frames.
            _videoDecoder.DeliverPlanar420 = GetYuvShader() != null;
            // The worker copies each picture into Godot images, so the main thread only uploads.
            _videoDecoder.PreparePlaneImages = true;
            long startUs = _startTimeUs;
            // Start after a couple of pictures (four above 1080p60) and about 100 ms of sound;
            // the playback loops fill the rest once the cue plays.
            var presentTuning = _presentTuning;
            int videoPrefetch = presentTuning.StartPrefetchFrames;
            int audioRingMs = 0, audioPrefetchMs = 0;
            if (_audioDecoder != null)
            {
                // Stream embedded audio (no full PCM expand): long video soundtracks would
                // otherwise pin tens/hundreds of MB on the LOH for the whole cue lifetime.
                // Ring sized from latency mode so PreferStability prefetch (1400 ms) fits.
                RefreshAudioTuning();
                audioRingMs = _audioTuning.RecommendedRingMs;
                audioPrefetchMs = _audioTuning.StartPrefetchMs;
            }

            // Picture, sound and subtitles open at the same time, each on its own worker.
            // Each worker does its open, seek and first fill in one go, so the main thread
            // is only waited for once. The sound opener skips the picture stream so the file
            // is not probed twice (MP4/MOV only).
            var videoDecoder = _videoDecoder;
            double seekMs = 0, firstPictureMs = 0, prefetchMs = 0, soundPrefetchMs = 0;
            long videoEnd = 0, soundEnd = 0, subtitlesEnd = 0;
            Task videoTask = Task.Run(() =>
            {
                videoDecoder.Open(mediaPath);
                long t = Stopwatch.GetTimestamp();
                if (startUs > 0)
                    videoDecoder.Seek(startUs);
                seekMs = DecoderOpenTimes.Since(t);
                t = Stopwatch.GetTimestamp();
                videoDecoder.Prefetch(1);
                firstPictureMs = DecoderOpenTimes.Since(t);
                var info = videoDecoder.Info;
                if (info != null)
                    videoPrefetch = presentTuning.StartPrefetchFramesFor(info.Width, info.Height, info.FrameDurationUs);
                videoDecoder.Prefetch(videoPrefetch);
                prefetchMs = DecoderOpenTimes.Since(t);
                videoEnd = Stopwatch.GetTimestamp();
            });

            Task soundTask = Task.CompletedTask;
            var audioDecoder = _audioDecoder;
            if (audioDecoder != null)
            {
                audioDecoder.SkipVideoProbe = true;
                soundTask = Task.Run(() =>
                {
                    audioDecoder.Open(mediaPath, preferSampleAccurateStore: false, ringMs: audioRingMs);
                    long t = Stopwatch.GetTimestamp();
                    if (startUs > 0)
                        audioDecoder.Seek(startUs);
                    audioDecoder.Prefetch(audioPrefetchMs);
                    PublishAudioQueuedEnd(audioDecoder.PositionUs);
                    soundPrefetchMs = DecoderOpenTimes.Since(t);
                    soundEnd = Stopwatch.GetTimestamp();
                });
            }

            async Task LoadSubtitlesTimedAsync()
            {
                await TryLoadSubtitlesAsync(mediaPath);
                subtitlesEnd = Stopwatch.GetTimestamp();
            }
            Task subtitlesTask = LoadSubtitlesTimedAsync();

            // WhenAll waits for every worker even if one fails, so none is still running
            // against a decoder when Clean runs in the catch below.
            await Task.WhenAll(videoTask, soundTask, subtitlesTask);
            long lastWorkerEnd = Math.Max(Math.Max(videoEnd, soundEnd), subtitlesEnd);
            double mediaMs = DecoderOpenTimes.Since(initStart);
            double mainWaitMs = lastWorkerEnd > 0 ? DecoderOpenTimes.Since(lastWorkerEnd) : 0;
            double videoOpenMs = (videoDecoder.LastOpenTimes.EndTicks - initStart) * 1000.0 / Stopwatch.Frequency;
            double soundOpenMs = audioDecoder?.LastOpenTimes != null
                ? (audioDecoder.LastOpenTimes.EndTicks - initStart) * 1000.0 / Stopwatch.Frequency
                : 0;
            double subtitlesMs = subtitlesEnd > 0 ? (subtitlesEnd - initStart) * 1000.0 / Stopwatch.Frequency : 0;
            long stageStart = Stopwatch.GetTimestamp();

            // Do not replace image hold end with container duration (often 0 or N/A for stills).
            if (!_videoComponent.IsImage && !_useCustomEnd && _videoDecoder.Info.DurationUs > 0)
                _endTimeUs = _videoDecoder.Info.DurationUs;

            if (!_videoComponent.IsImage)
            {
                lock (_lock)
                {
                    RebuildPlaybackRegions();
                    ApplyRegionUnlocked(0);
                }
            }

            int w = _videoDecoder.Info.Width;
            int h = _videoDecoder.Info.Height;
            _presentPlanar = _videoDecoder.OutputsPlanar420;
            _statsUpload = _presentPlanar ? "yuv420" : "rgba";
            if (_presentPlanar)
                CreatePlanarTargets(w, h);
            else
            {
                _godotImage = Image.CreateEmpty(w, h, false, Image.Format.Rgba8);
                _godotTexture = ImageTexture.CreateFromImage(_godotImage);
            }

            foreach (var display in DisplaysManager.Outputs)
            {
                var layerControl = display.AddLayer(_videoComponent.TargetLayerId, LayerStackOrder);
                var layerTextRect = layerControl.GetNode<TextureRect>("%LayerOutput");
                layerTextRect.Texture = _godotTexture;
                if (_presentPlanar)
                    AssignYuvMaterial(layerTextRect);
                _targetLayers.Add(layerControl, layerTextRect);
                layerTextRect.TreeExited += () => OnLayerExited(layerTextRect);
            }

            // Stay invisible until PlayAsync / FadeInAsync. Init assigns the shared texture early,
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
                SourceChannels = _audioDecoder.Info.Channels;
                SourceSampleRate = _audioDecoder.Info.SampleRate;
                SourceFormat = SDL.AudioFormat.AudioF32LE;
                SourceBytesPerFrame = SourceChannels * sizeof(float);

                int maxFrames = Math.Max(SourceSampleRate / 10, 1024);
                _audioSrcBuffer = new float[maxFrames * SourceChannels];
                _audioMixBuffer = new float[maxFrames * 16];
                _ratePitch.Configure(SourceChannels, SourceSampleRate);
                _ratePitch.Reset();
            }
            double texturesMs = DecoderOpenTimes.Since(stageStart);

            SetProcess(false); // enabled on Play
            GD.Print($"ActiveVideoPlayback:InitAsync - complete video={w}x{h} audio={_audioDecoder != null}");
            string soundPart = _audioDecoder != null
                ? $", sound open {soundOpenMs:F0} ({_audioDecoder.LastOpenTimes}) then prefetch {soundPrefetchMs:F0}"
                : string.Empty;
            string seekPart = startUs > 0 ? $", seek to in point {seekMs:F0}" : string.Empty;
            string subtitlesPart = subtitlesMs > 1 ? $", subtitles {subtitlesMs:F0}" : string.Empty;
            GD.Print(
                $"ActiveVideoPlayback:InitAsync - Start timing {_startTrace.Label}: init {DecoderOpenTimes.Since(initStart):F0} ms, " +
                $"media in parallel {mediaMs:F0} = picture open {videoOpenMs:F0} " +
                $"({_videoDecoder.LastOpenTimes}, hw {_videoDecoder.HardwareName}){seekPart}, " +
                $"first picture {firstPictureMs:F0}, {videoPrefetch} pictures {prefetchMs:F0}" +
                $"{soundPart}{subtitlesPart}; waiting for the main thread {mainWaitMs:F0}; textures {texturesMs:F0}");
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
        if (frame == null || _isExiting) return;
        if (frame.IsPlanar)
        {
            if (!IsInstanceValid(_godotImage) || !IsInstanceValid(_godotTexture))
                return;
        }
        else if (frame.Rgba == null || !IsInstanceValid(_godotImage) || !IsInstanceValid(_godotTexture))
        {
            return;
        }

        // Apply fade/opacity BEFORE uploading pixels. TextureRects already hold this
        // ImageTexture from Init — Update() would otherwise flash a full-opacity frame.
        ApplyOpacityModulate();

        _presentsThisTick++;
        if (_presentsThisTick > _maxPresentsInOneTick)
            _maxPresentsInOneTick = _presentsThisTick;

        if (frame.IsPlanar)
        {
            UploadPlanar(frame);
            return;
        }

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

        bool textureReplaced = false;
        bool leavingPlanes = _presentPlanar;
        if (leavingPlanes
            || _godotImage.GetFormat() != Image.Format.Rgba8
            || _godotImage.GetWidth() != frame.Width
            || _godotImage.GetHeight() != frame.Height)
        {
            _godotImage = Image.CreateEmpty(frame.Width, frame.Height, false, Image.Format.Rgba8);
            _godotTexture = ImageTexture.CreateFromImage(_godotImage);
            textureReplaced = true;
            _presentPlanar = false;
            _statsUpload = "rgba";
        }

        long setDataStart = Stopwatch.GetTimestamp();
        _godotImage.SetData(frame.Width, frame.Height, false, Image.Format.Rgba8, pixels);
        long setDataTicks = Stopwatch.GetTimestamp() - setDataStart;
        long updateStart = Stopwatch.GetTimestamp();
        _godotTexture.Update(_godotImage);
        long updateTicks = Stopwatch.GetTimestamp() - updateStart;
        AddUploadSample(ref _setDataTicks, ref _setDataMaxTicks, setDataTicks);
        AddUploadSample(ref _updateTicks, ref _updateMaxTicks, updateTicks);
        VideoDiagnostics.NoteUpload(setDataTicks, updateTicks);
        NoteUploadCost(setDataTicks + updateTicks);
        VideoDiagnostics.NotePresented();
        _shown++;
        _startTrace.NotePicture();

        if (!textureReplaced)
            return;

        foreach (var layer in _targetLayers)
        {
            if (layer.Value == null || !IsInstanceValid(layer.Value))
                continue;
            layer.Value.Texture = _godotTexture;
            // An RGBA frame after plane frames: the colour shader must not run on it.
            if (leavingPlanes)
                layer.Value.Material = null;
        }
    }

    /// <summary>Path of the canvas shader that converts the Y and UV planes to RGB.</summary>
    private const string YuvShaderPath = "res://src/UI/Output/VideoYuv.gdshader";

    /// <summary>Loaded once per process and shared by every playback. Main thread only.</summary>
    private static Shader _yuvShader;

    private static bool _yuvShaderLoadFailed;

    /// <summary>
    /// Returns the shared YUV shader, loading it on first use. Null when it cannot be loaded;
    /// playback then uploads RGBA frames instead of planes. Main thread only.
    /// </summary>
    private static Shader GetYuvShader()
    {
        if (_yuvShader != null && IsInstanceValid(_yuvShader))
            return _yuvShader;
        if (_yuvShaderLoadFailed)
            return null;

        _yuvShader = ResourceLoader.Exists(YuvShaderPath) ? GD.Load<Shader>(YuvShaderPath) : null;
        if (_yuvShader == null)
        {
            _yuvShaderLoadFailed = true;
            GD.PrintErr($"ActiveVideoPlayback:GetYuvShader - Could not load {YuvShaderPath}. Video uses RGBA upload.");
        }
        return _yuvShader;
    }

    /// <summary>
    /// Draws the colour shader once on a 1x1 picture in a canvas layer below the whole
    /// interface, so the graphics driver compiles it at launch and not at the first video
    /// GO. The picture is black, almost fully transparent and covered by the interface, and
    /// it is removed after half a second. Main thread only.
    /// </summary>
    internal static void WarmUpYuvShader(Node host)
    {
        if (host == null || !IsInstanceValid(host) || !host.IsInsideTree())
            return;
        Shader shader = GetYuvShader();
        if (shader == null)
            return;

        var luma = ImageTexture.CreateFromImage(Image.CreateEmpty(1, 1, false, Image.Format.L8));
        var chroma = ImageTexture.CreateFromImage(Image.CreateEmpty(1, 1, false, Image.Format.Rg8));
        var material = new ShaderMaterial { Shader = shader };
        material.SetShaderParameter("chroma_tex", chroma);
        material.SetShaderParameter("range_limited", 1f);
        material.SetShaderParameter("matrix_id", 1f);

        var layer = new CanvasLayer { Layer = -128, Name = "VideoShaderWarmUp" };
        var rect = new TextureRect
        {
            Texture = luma,
            Material = material,
            Position = Vector2.Zero,
            Size = Vector2.One,
            CustomMinimumSize = Vector2.One,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            Modulate = new Color(0f, 0f, 0f, 0.01f)
        };
        layer.AddChild(rect);
        host.AddChild(layer);
        host.GetTree().CreateTimer(0.5).Timeout += () =>
        {
            if (IsInstanceValid(layer))
                layer.QueueFree();
        };
    }

    /// <summary>
    /// Builds the Y and UV textures and the colour-conversion material.
    /// The material uses the shared shader from <see cref="GetYuvShader"/>, so it compiles once.
    /// </summary>
    private void CreatePlanarTargets(int width, int height)
    {
        int cw = Math.Max(1, (width + 1) >> 1);
        int ch = Math.Max(1, (height + 1) >> 1);
        // L8 repeats luma into RGB. R8 leaves green and blue at 0, so the default
        // canvas shader draws the picture red when the colour shader is not used.
        _godotImage = Image.CreateEmpty(width, height, false, Image.Format.L8);
        _godotTexture = ImageTexture.CreateFromImage(_godotImage);
        _uvImage = Image.CreateEmpty(cw, ch, false, Image.Format.Rg8);
        _uvTexture = ImageTexture.CreateFromImage(_uvImage);

        _yuvMaterial = new ShaderMaterial();
        _yuvMaterial.Shader = GetYuvShader();
        ApplyYuvUniforms(1f, 1f);
        GD.Print("ActiveVideoPlayback:CreatePlanarTargets - YUV shader assigned.");
    }

    /// <summary>
    /// Puts the shared colour shader on one output rectangle.
    /// The same pattern as the canvas editor: set the shader code, then the material, no copy.
    /// </summary>
    private void AssignYuvMaterial(TextureRect rect)
    {
        if (_yuvMaterial == null || rect == null || !IsInstanceValid(rect))
            return;
        rect.Material = _yuvMaterial;
    }

    private void ApplyYuvUniforms(float rangeLimited, float matrixId)
    {
        if (_yuvMaterial == null || !IsInstanceValid(_yuvMaterial))
            return;
        _yuvMaterial.SetShaderParameter("chroma_tex", _uvTexture);
        _yuvMaterial.SetShaderParameter("range_limited", rangeLimited);
        _yuvMaterial.SetShaderParameter("matrix_id", matrixId);
    }

    /// <summary>
    /// Uploads luma and interleaved chroma, then lets the canvas shader convert to RGB.
    /// </summary>
    private void UploadPlanar(VideoFrame frame)
    {
        if (_yuvMaterial == null || !IsInstanceValid(_yuvMaterial))
        {
            // The cue opened on RGBA frames and the stream changed to planes mid-file.
            Shader shader = GetYuvShader();
            if (shader == null)
                return;
            _yuvMaterial = new ShaderMaterial();
            _yuvMaterial.Shader = shader;
        }

        bool replaced = false;
        if (!_presentPlanar)
        {
            _presentPlanar = true;
            _statsUpload = "yuv420";
            replaced = true;
        }
        if (_godotImage.GetFormat() != Image.Format.L8
            || _godotImage.GetWidth() != frame.Width
            || _godotImage.GetHeight() != frame.Height)
        {
            _godotImage = Image.CreateEmpty(frame.Width, frame.Height, false, Image.Format.L8);
            _godotTexture = ImageTexture.CreateFromImage(_godotImage);
            replaced = true;
        }

        if (_uvImage == null
            || !IsInstanceValid(_uvImage)
            || _uvImage.GetWidth() != frame.ChromaWidth
            || _uvImage.GetHeight() != frame.ChromaHeight)
        {
            _uvImage = Image.CreateEmpty(Math.Max(1, frame.ChromaWidth), Math.Max(1, frame.ChromaHeight), false, Image.Format.Rg8);
            _uvTexture = ImageTexture.CreateFromImage(_uvImage);
            replaced = true;
        }

        int limited = frame.LimitedRange ? 1 : 0;
        if (frame.MatrixId != _appliedMatrixId || limited != _appliedLimited || replaced)
        {
            _appliedMatrixId = frame.MatrixId;
            _appliedLimited = limited;
            ApplyYuvUniforms(limited, frame.MatrixId);
        }

        // Timed inline: closures here would allocate four delegates per presented frame.
        long setDataTicks;
        long updateTicks;
        if (frame.HasPlaneImages)
        {
            // Images were made on the decode worker: only the upload runs here.
            long u0 = Stopwatch.GetTimestamp();
            _godotTexture.Update(frame.YImage);
            _uvTexture.Update(frame.UvImage);
            setDataTicks = 0;
            updateTicks = Stopwatch.GetTimestamp() - u0;
        }
        else
        {
            long t0 = Stopwatch.GetTimestamp();
            _godotImage.SetData(frame.Width, frame.Height, false, Image.Format.L8, frame.Y);
            long t1 = Stopwatch.GetTimestamp();
            _godotTexture.Update(_godotImage);
            long t2 = Stopwatch.GetTimestamp();
            _uvImage.SetData(frame.ChromaWidth, frame.ChromaHeight, false, Image.Format.Rg8, frame.Uv);
            long t3 = Stopwatch.GetTimestamp();
            _uvTexture.Update(_uvImage);
            long t4 = Stopwatch.GetTimestamp();
            setDataTicks = (t1 - t0) + (t3 - t2);
            updateTicks = (t2 - t1) + (t4 - t3);
        }

        AddUploadSample(ref _setDataTicks, ref _setDataMaxTicks, setDataTicks);
        AddUploadSample(ref _updateTicks, ref _updateMaxTicks, updateTicks);
        VideoDiagnostics.NoteUpload(setDataTicks, updateTicks);
        NoteUploadCost(setDataTicks + updateTicks);
        VideoDiagnostics.NotePresented();
        _shown++;
        _startTrace.NotePicture();

        if (!replaced)
            return;

        foreach (var layer in _targetLayers)
        {
            if (layer.Value == null || !IsInstanceValid(layer.Value))
                continue;
            layer.Value.Texture = _godotTexture;
            AssignYuvMaterial(layer.Value);
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

        ApplyOpacityModulateForced();
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

    /// <summary>
    /// Writes layer modulate when opacity or fade alpha changed.
    /// Must stay parameterless: fade-out calls it through <c>CallDeferred</c>, and Godot
    /// only binds the method with the arguments it declares.
    /// </summary>
    private void ApplyOpacityModulate() => WriteOpacityModulate(force: false);

    /// <summary>
    /// Writes layer modulate even when the value matches the last apply.
    /// Used when layers are created or the inspector refreshes layout.
    /// </summary>
    private void ApplyOpacityModulateForced() => WriteOpacityModulate(force: true);

    private void WriteOpacityModulate(bool force)
    {
        float opacity;
        lock (_lock)
        {
            opacity = _runtimeOpacity ?? Mathf.Clamp(_videoComponent?.Opacity ?? 1f, 0f, 1f);
        }
        opacity = Mathf.Clamp(opacity * TimelineOpacityScale(), 0f, 1f);
        float fade = _fadeAlpha;
        if (!force && opacity == _appliedOpacity && fade == _appliedFadeAlpha)
            return;

        _appliedOpacity = opacity;
        _appliedFadeAlpha = fade;
        var modulate = new Color(1f, 1f, 1f, opacity * fade);

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
    private long GetMasterClockUs() => GetMasterClockUs(out _);

    private long GetMasterClockUs(out bool fromAudio)
    {
        fromAudio = false;
        // Hold at the click/scrub target until a post-seek frame is presented, and while
        // the worker still owns the decoder, so progress bars do not snap to the old clock.
        long clockHoldUs = _clockHoldUs;
        if (clockHoldUs >= 0)
            return clockHoldUs;
        if (_seekInProgress)
            return _seekTargetUs;

        bool audioMasterExhausted;
        lock (_lock) audioMasterExhausted = _audioMasterExhausted;

        if (_audioDecoder != null && UseAudio && HasBoundAudioStreams && !audioMasterExhausted)
        {
            fromAudio = true;
            long audioUs;
            long queuedUs;
            long master;
            if (TryReadAudioClock(out audioUs, out queuedUs))
            {
                master = audioUs - queuedUs;
                if (master < _startTimeUs)
                    master = _startTimeUs;
                _lastAudioMasterUs = master;
                _lastAudioEndUs = audioUs;
                _lastAudioQueuedUs = queuedUs;
            }
            else if (_lastAudioMasterUs != long.MinValue)
            {
                // PCM is being queued right now: reuse the last consistent reading.
                master = _lastAudioMasterUs;
                audioUs = _lastAudioEndUs;
                queuedUs = _lastAudioQueuedUs;
            }
            else
            {
                master = _startTimeUs;
                audioUs = _startTimeUs;
                queuedUs = 0;
            }

            // ~8ms matches present early-tolerance: treat the soundtrack as finished once
            // decode is at EOS (or past its stream duration) and SDL has drained.
            // The decoder is only asked once the queue is nearly empty: its lock is held
            // by the fill thread while it decodes, and this runs on the main thread.
            const long drainedQueuedUs = 8_000;
            bool audioDone = false;
            if (queuedUs <= drainedQueuedUs)
            {
                long audioDurationUs = _audioDecoder.Info?.DurationUs ?? 0;
                audioDone = _audioDecoder.EndOfStream
                    || (audioDurationUs > 0 && audioUs >= audioDurationUs);
            }
            if (audioDone)
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

        return WallClockToFileUs();
    }

    /// <summary>
    /// Picture (and embedded-audio) play rate for this instance. Images are always 1.
    /// </summary>
    public double EffectivePlayRate =>
        _videoComponent?.EffectivePlayRate ?? AudioComponent.DefaultPlayRate;

    /// <summary>
    /// Reads the published end-of-queue media time and the PCM still queued in SDL as one
    /// consistent pair. Returns false when PCM is being queued during every attempt.
    /// </summary>
    private bool TryReadAudioClock(out long audioEndUs, out long queuedUs)
    {
        audioEndUs = 0;
        queuedUs = 0;
        for (int attempt = 0; attempt < 4; attempt++)
        {
            int before = Volatile.Read(ref _audioClockSeq);
            if ((before & 1) != 0)
            {
                Thread.SpinWait(16);
                continue;
            }

            long queued = 0;
            int count = 0;
            foreach (var b in _streamBindings.Snapshot)
            {
                long qb = _streamBindings.GetQueued(b);
                if (qb < 0) continue;
                int outCh = GetStreamChannels(b);
                if (outCh <= 0 || SourceSampleRate <= 0) continue;
                queued += qb * MicrosecondsPerSecond / (SourceSampleRate * outCh * sizeof(float));
                count++;
            }
            if (count > 0) queued /= count;
            long endUs = Volatile.Read(ref _audioQueuedEndUs);

            if (Volatile.Read(ref _audioClockSeq) == before)
            {
                audioEndUs = endUs;
                queuedUs = queued;
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Publishes the media time at the end of the PCM queued so far. Call after the PCM
    /// has been queued (or the queue cleared and the decoder moved), from the thread that did it.
    /// </summary>
    private void PublishAudioQueuedEnd(long endUs)
    {
        Interlocked.Increment(ref _audioClockSeq);
        Volatile.Write(ref _audioQueuedEndUs, endUs);
        Interlocked.Increment(ref _audioClockSeq);
    }

    /// <summary>
    /// Clock used to pick which frame to show. The audio clock only moves when the sound
    /// device takes a block of PCM (often 10 to 21 ms), so sampled once per display tick it
    /// advances unevenly. This clock advances by real elapsed time each tick and is pulled
    /// gently towards the audio clock, so frames change on an even cadence while staying in
    /// sync on average. It resets to the audio clock after a gap, a seek, or a large error.
    /// </summary>
    private long SmoothPresentClock(long masterUs, bool fromAudio)
    {
        long now = Stopwatch.GetTimestamp();
        if (!fromAudio)
        {
            _presentClockValid = false;
            return masterUs;
        }

        if (_presentClockValid)
        {
            long elapsedUs = (now - _presentClockAtTimestamp) * MicrosecondsPerSecond / Stopwatch.Frequency;
            if (elapsedUs >= 0 && elapsedUs <= PresentClockMaxGapUs)
            {
                long predicted = _presentClockUs + elapsedUs;
                long error = masterUs - predicted;
                if (Math.Abs(error) <= PresentClockSnapUs)
                {
                    long next = predicted + error / PresentClockGainDivisor;
                    if (next < _presentClockUs)
                        next = _presentClockUs;
                    _presentClockUs = next;
                    _presentClockAtTimestamp = now;
                    return next;
                }
            }
            _clockSnaps++;
        }

        _presentClockUs = masterUs;
        _presentClockAtTimestamp = now;
        _presentClockValid = true;
        return masterUs;
    }

    /// <summary>Forgets the smoothed present clock and the cadence baselines (seek, pause, play).</summary>
    private void ResetPresentClock()
    {
        _presentClockValid = false;
        _prevTickTimestamp = 0;
        _prevRawMasterUs = long.MinValue;
        _lastAudioMasterUs = long.MinValue;
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
                if (!_videoComponent.IsImage)
                    ApplyRegionUnlocked(0);
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
            long loopStartUs = _videoComponent.IsImage ? _startTimeUs : _regionStartUs;
            SeekInternal(loopStartUs, restartClock: true, syncRegion: false);
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
            if (!IsOnLastRegionPlayUnlocked())
                return;

            double configured = _videoComponent.FadeOutDuration;
            long regionEndUs = CurrentRegionEndUs();
            if (configured <= 1e-9 || regionEndUs == long.MaxValue)
                return;

            long remainingUs = regionEndUs - masterUs;
            double rate = CurrentPlayRateAt(masterUs);
            long fadeUs = (long)(configured * rate * 1_000_000.0);
            if (remainingUs > fadeUs)
                return;

            _naturalEndFadeArmed = true;
            double remainingSec = Math.Max(0, remainingUs / 1_000_000.0 / Math.Max(AudioComponent.MinPlayRate, rate));
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
            if (!_videoComponent.IsImage)
            {
                RebuildPlaybackRegions();
                ApplyRegionUnlocked(0);
            }
            // Wall-clock origin = current media position (used when no audio master).
            // Still images always start their hold timer at 0.
            _wallMediaOriginUs = _videoComponent.IsImage
                ? 0
                : (_videoDecoder?.PositionUs ?? _regionStartUs);
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
        ApplyOpacityModulateForced();

        _startTrace.Arm();
        ResetPresentClock();
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
            _startTrace.NoteAudioPrimed(MinQueuedBytes());
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

        long loopStart = Stopwatch.GetTimestamp();
        if (_prevTickTimestamp != 0)
        {
            long interval = loopStart - _prevTickTimestamp;
            // Ignore gaps from pauses and seeks (over a quarter second).
            if (interval > 0 && interval < Stopwatch.Frequency / 4)
            {
                _tickIntervalTicks += interval;
                _tickIntervalCount++;
                if (interval > _tickIntervalMaxTicks)
                    _tickIntervalMaxTicks = interval;
            }
        }
        _prevTickTimestamp = loopStart;
        _presentsThisTick = 0;
        VideoDiagnostics.BeginCueTick(loopStart);
        try
        {
            PresentCatchUpFramesBody();
        }
        finally
        {
            long elapsed = Stopwatch.GetTimestamp() - loopStart;
            _presentLoopCount++;
            AddUploadSample(ref _presentLoopTicks, ref _presentLoopMaxTicks, elapsed);
            VideoDiagnostics.NotePresentLoop(elapsed);
            VideoDiagnostics.NoteCueTick(elapsed);
            MaybeLogPerSecondStats();
        }
    }

    private void PresentCatchUpFramesBody()
    {
        if (_videoDecoder == null)
            return;

        // Seek/discard holds the decoder lock for a long time — skip present rather than stall UI.
        // Still emit TimeUpdated at the seek target so progress bars hold (not snap back).
        if (_seekInProgress)
        {
            long holdUs = _clockHoldUs >= 0 ? _clockHoldUs : _seekTargetUs;
            EmitSignal(SignalName.TimeUpdated, holdUs / (double)MicrosecondsPerSecond);
            return;
        }

        // After the worker finishes, leftover pre-seek ring frames are "late" vs the new
        // clock and would snap the picture back (especially 4K). Drain them and wait for
        // a frame near the click target before following live.
        if (HoldPresentUntilSeekFrame())
            return;

        long masterUs = GetMasterClockUs(out bool clockFromAudio);
        NoteClockStep(masterUs, clockFromAudio);
        EmitSignal(SignalName.TimeUpdated, masterUs / (double)MicrosecondsPerSecond);
        UpdateLinkedSubtitles(masterUs);
        ApplyOpacityModulate();

        // Arm end-fade early so FadeOutDuration runs inside the last seconds of content
        // (e.g. 10s segment + 4s fade → fade begins at t=6s).
        TryArmNaturalEndFade(masterUs);

        bool fadingOut;
        lock (_lock) fadingOut = _isFadingOut;

        long regionEndUs = CurrentRegionEndUs();
        if (masterUs >= regionEndUs)
        {
            // While end-fading, hold the last frame until FadeOutAsync HardStops.
            if (fadingOut)
            {
                ApplyOpacityModulate();
                return;
            }
            HandleRegionBoundary();
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
                    _videoDecoder.ReleaseFrame(frame);
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

        // Frame choice and the drop marker follow the smoothed clock; end handling above
        // stays on the master clock.
        long presentUs = SmoothPresentClock(masterUs, clockFromAudio);

        // Pictures older than one frame are discarded on the worker before the slow convert.
        long frameDurationUs = _videoDecoder.Info?.FrameDurationUs ?? 16_667;
        _videoDecoder.SetDropBefore(presentUs - frameDurationUs);
        _videoDecoder.SetPrefetchLowWater(_presentTuning.PrefetchLowWater);

        // When several cues' uploads no longer fit in a frame, they take turns: a cue told to
        // wait keeps its pictures buffered and shows the newest one next frame.
        if (_shown > 0
            && CurrentOutputAlpha() > 0f
            && _videoDecoder.TryPeekPtsBuffered(out long duePts)
            && duePts < regionEndUs
            && presentUs - duePts >= -_presentTuning.PresentEarlyToleranceUs
            && !VideoUploadBudget.TryUpload(_uploadCostTicks, ref _lastUploadWaitFrame))
        {
            _uploadWaits++;
            return;
        }

        // One upload per tick. Older due frames are released without SetData.
        // Take cap drains a hitch backlog over several ticks without monopolising this one.
        _videoDecoder.ObserveRingDepth();
        int takeCap = Math.Max(_presentTuning.MaxPresentPerTick, 8);
        VideoFrame newest = null;
        int taken = 0;
        bool segmentEnd = false;
        bool eosEmpty = false;
        while (taken < takeCap)
        {
            // Ring-only: never DecodeMore on the present path.
            if (!_videoDecoder.TryPeekPtsBuffered(out long nextPts))
            {
                if (_videoDecoder.EndOfStream)
                    eosEmpty = true;
                break;
            }

            // Frame is at/past the out-point — do not wait for a clock that may stall short of it.
            if (regionEndUs < long.MaxValue && nextPts >= regionEndUs)
            {
                segmentEnd = true;
                break;
            }

            long lateness = presentUs - nextPts;
            if (lateness < -_presentTuning.PresentEarlyToleranceUs)
                break;

            if (!_videoDecoder.TryTakeFrame(out VideoFrame frame))
                break;

            if (newest != null)
            {
                _videoDecoder.ReleaseFrame(newest);
                _superseded++;
            }
            newest = frame;
            taken++;
        }

        // Fully transparent: nothing is seen, so the upload is skipped. The newest picture is
        // kept and uploaded as soon as the cue shows again.
        if (newest != null && _shown > 0 && CurrentOutputAlpha() <= 0f)
        {
            ApplyOpacityModulate();
            if (_hiddenFrame != null)
                _videoDecoder.ReleaseFrame(_hiddenFrame);
            _hiddenFrame = newest;
            newest = null;
            _hiddenSkips++;
        }
        else if (newest == null && _hiddenFrame != null && CurrentOutputAlpha() > 0f)
        {
            newest = _hiddenFrame;
            _hiddenFrame = null;
        }
        else if (newest != null && _hiddenFrame != null)
        {
            _videoDecoder.ReleaseFrame(_hiddenFrame);
            _hiddenFrame = null;
        }

        if (newest != null)
        {
            long lateness = presentUs - newest.PtsUs;
            if (lateness > _maxLatenessUs)
                _maxLatenessUs = lateness;
            if (lateness > _presentTuning.MaxLatenessUs)
                _lateShown++;
            PresentFrame(newest);
            _videoDecoder.ReleaseFrame(newest);
        }

        // Present first so the last in-range frame stays up for the follow handoff.
        if (segmentEnd || eosEmpty)
            HandleRegionBoundary();
    }

    /// <summary>
    /// Parks presentation on the seek target until a post-seek frame is shown.
    /// Drops leftover ring pictures whose PTS is earlier than the hold (pre-seek catch-up).
    /// </summary>
    /// <returns>True when the rest of the present tick should be skipped.</returns>
    private bool HoldPresentUntilSeekFrame()
    {
        long holdUs = _clockHoldUs;
        if (holdUs < 0)
            return false;

        EmitSignal(SignalName.TimeUpdated, holdUs / (double)MicrosecondsPerSecond);
        ApplyOpacityModulate();

        if (_videoDecoder == null || _videoComponent == null || _videoComponent.IsImage)
        {
            _clockHoldUs = -1;
            return false;
        }

        long minPts = holdUs - ClockHoldStaleFrameUs;
        int drained = 0;
        while (drained < 32
               && _videoDecoder.TryPeekPtsBuffered(out long stalePts)
               && stalePts < minPts)
        {
            if (!_videoDecoder.TryTakeFrame(out VideoFrame stale))
                break;
            _videoDecoder.ReleaseFrame(stale);
            _superseded++;
            drained++;
        }

        if (_videoDecoder.TryPeekPtsBuffered(out long holdPts) && holdPts >= minPts)
        {
            if (_videoDecoder.TryTakeFrame(out VideoFrame holdFrame))
            {
                PresentFrame(holdFrame);
                long presentedPts = holdFrame.PtsUs;
                _videoDecoder.ReleaseFrame(holdFrame);
                _wallMediaOriginUs = presentedPts;
                if (_isPlaying && !IsPaused)
                    _wallClock.Restart();
                _lastAudioMasterUs = presentedPts;
                _lastAudioEndUs = presentedPts;
                _lastAudioQueuedUs = 0;
                _clockHoldUs = -1;
            }

            return true;
        }

        if (_videoDecoder.EndOfStream)
            _clockHoldUs = -1;

        return true;
    }

    /// <summary>Records how far the master clock moved since the last present tick.</summary>
    private void NoteClockStep(long masterUs, bool fromAudio)
    {
        _lastClockFromAudio = fromAudio;
        if (_prevRawMasterUs != long.MinValue)
        {
            long step = masterUs - _prevRawMasterUs;
            if (step == 0)
                _clockStallTicks++;
            else if (step > 0 && step < 250_000 && step > _clockStepMaxUs)
                _clockStepMaxUs = step;
        }
        _prevRawMasterUs = masterUs;
    }

    // Newest picture not uploaded because the cue is fully transparent.
    private VideoFrame _hiddenFrame;
    private long _hiddenSkips;

    /// <summary>
    /// Timeline relative opacity at the current file position (1 for stills and missing media).
    /// </summary>
    private float TimelineOpacityScale()
    {
        if (_videoComponent == null || _videoComponent.IsImage)
            return AudioTimelineNode.DefaultOpacityScale;
        return CurrentTimeline().OpacityScale;
    }

    /// <summary>Opacity times fade, as written to the output layers. Main thread.</summary>
    private float CurrentOutputAlpha()
    {
        float opacity;
        lock (_lock)
        {
            opacity = _runtimeOpacity ?? Mathf.Clamp(_videoComponent?.Opacity ?? 1f, 0f, 1f);
        }
        return Mathf.Clamp(opacity * TimelineOpacityScale(), 0f, 1f) * _fadeAlpha;
    }

    /// <summary>Gives the kept transparent-time picture back to the decoder.</summary>
    private void ReleaseHiddenFrame()
    {
        VideoFrame held = _hiddenFrame;
        _hiddenFrame = null;
        if (held == null)
            return;
        try { _videoDecoder?.ReleaseFrame(held); } catch { /* decoder already closed */ }
    }

    // Smoothed cost of one upload of this cue, for the shared upload budget.
    private long _uploadCostTicks;
    private ulong _lastUploadWaitFrame = ulong.MaxValue;
    private long _uploadWaits;

    private void NoteUploadCost(long ticks)
    {
        if (ticks < 0)
            ticks = 0;
        _uploadCostTicks = _uploadCostTicks <= 0 ? ticks : _uploadCostTicks + (ticks - _uploadCostTicks) / 8;
    }

    private static void AddUploadSample(ref long sum, ref long max, long ticks)
    {
        if (ticks < 0)
            ticks = 0;
        sum += ticks;
        if (ticks > max)
            max = ticks;
    }

    private void MaybeLogPerSecondStats()
    {
        if (!VideoDiagnostics.PerSecondEnabled || _videoDecoder == null)
            return;

        ulong now = Time.GetTicksMsec();
        if (now < _nextStatsLogMs)
            return;
        _nextStatsLogMs = now + 1000;
        GD.Print("ActiveVideoPlayback:Stats - " + FormatVideoStats());
    }

    private void LogPlaybackSummary()
    {
        if (_summaryLogged)
            return;
        _summaryLogged = true;
        if (_videoDecoder == null && _shown == 0 && _superseded == 0)
            return;
        GD.Print("ActiveVideoPlayback:Stop - " + FormatVideoStats());
    }

    private string FormatVideoStats()
    {
        VideoDecodeStats.Snapshot decode = _videoDecoder != null
            ? _videoDecoder.GetDecodeStats()
            : default;
        string ring = decode.MinRingDepth == int.MaxValue ? "na" : decode.MinRingDepth.ToString();
        long prepareTicks = 0, prepareMaxTicks = 0, prepared = 0;
        _videoDecoder?.ReadPrepareStats(out prepareTicks, out prepareMaxTicks, out prepared);
        long maxLate = _maxLatenessUs == long.MinValue ? 0 : _maxLatenessUs;
        string file = _videoDecoder?.Info?.FilePath;
        if (string.IsNullOrEmpty(file))
            file = _videoComponent?.VideoFile ?? "";

        return
            $"upload={_statsUpload} hw={_videoDecoder?.HardwareName ?? "none"} file={file} shown={_shown} superseded={_superseded} lateShown={_lateShown} " +
            $"maxLateUs={maxLate} maxInTick={_maxPresentsInOneTick} ringMin={ring} " +
            $"decoded={decode.Decoded} converted={decode.Converted} droppedBeforeConvert={decode.DroppedBeforeConvert} " +
            $"keptToCatchUp={decode.KeptToCatchUp} nonRefSkips={decode.NonRefSkipStarts} " +
            $"decodeAvgMs={VideoDiagnostics.FormatAvgMs(decode.DecodeTicks, decode.Decoded)} " +
            $"decodeMaxMs={VideoDiagnostics.FormatMs(decode.DecodeMaxTicks)} " +
            $"convertAvgMs={VideoDiagnostics.FormatAvgMs(decode.ConvertTicks, decode.Converted)} " +
            $"convertMaxMs={VideoDiagnostics.FormatMs(decode.ConvertMaxTicks)} " +
            $"copyAvgMs={VideoDiagnostics.FormatAvgMs(decode.CopyTicks, decode.Converted)} " +
            $"imageAvgMs={VideoDiagnostics.FormatAvgMs(prepareTicks, prepared)} imageMaxMs={VideoDiagnostics.FormatMs(prepareMaxTicks)} " +
            $"setDataAvgMs={VideoDiagnostics.FormatAvgMs(_setDataTicks, _shown)} " +
            $"setDataMaxMs={VideoDiagnostics.FormatMs(_setDataMaxTicks)} " +
            $"updateAvgMs={VideoDiagnostics.FormatAvgMs(_updateTicks, _shown)} " +
            $"updateMaxMs={VideoDiagnostics.FormatMs(_updateMaxTicks)} " +
            $"presentAvgMs={VideoDiagnostics.FormatAvgMs(_presentLoopTicks, _presentLoopCount)} " +
            $"presentMaxMs={VideoDiagnostics.FormatMs(_presentLoopMaxTicks)} " +
            $"clock={(_lastClockFromAudio ? "audio" : "wall")} " +
            $"tickAvgMs={VideoDiagnostics.FormatAvgMs(_tickIntervalTicks, _tickIntervalCount)} " +
            $"tickMaxMs={VideoDiagnostics.FormatMs(_tickIntervalMaxTicks)} " +
            $"clockStepMaxMs={(_clockStepMaxUs / 1000.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)} " +
            $"clockStalls={_clockStallTicks} clockSnaps={_clockSnaps} uploadWaits={_uploadWaits} hiddenSkips={_hiddenSkips}";
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
        ResetPresentClock();

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
        LogPlaybackSummary();
        ReleaseHiddenFrame();
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
                {
                    kv.Value.Material = null;
                    kv.Value.Texture = null;
                }
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
        if (_uvTexture != null && IsInstanceValid(_uvTexture))
        {
            try { _uvTexture.Dispose(); } catch { /* ignore */ }
        }
        _uvTexture = null;
        if (_uvImage != null && IsInstanceValid(_uvImage))
        {
            try { _uvImage.Dispose(); } catch { /* ignore */ }
        }
        _uvImage = null;
        if (_yuvMaterial != null && IsInstanceValid(_yuvMaterial))
        {
            try { _yuvMaterial.Dispose(); } catch { /* ignore */ }
        }
        _yuvMaterial = null;
        _presentPlanar = false;
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
