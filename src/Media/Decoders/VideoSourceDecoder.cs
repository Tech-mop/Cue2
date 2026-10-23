// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Cue2.Media.Audio;
using FFmpeg.AutoGen;
using Godot;

namespace Cue2.Media.Decoders;

/// <summary>
/// Pull-based FFmpeg video source. Demuxes and decodes (in hardware when available),
/// then delivers each frame either as RGBA8 or, for 4:2:0 sources when planes are
/// requested, as a Y plane plus an interleaved UV plane.
/// Manages a bounded ring of decoded frames. Does not own transport control
/// (play/pause/loop) or wall-clock pacing; the presenter shows frames
/// according to a master clock (typically audio or wall time).
/// </summary>
public sealed class VideoSourceDecoder : IDisposable
{
    private const int SwsBilinear = 2;
    private const int RgbaChannels = 4;
    private const int DefaultRingFrames = 8;
    private const double FallbackFps = 30.0;

    // Two locks. Lock order is always _decodeLock, then _ringLock; never the reverse.
    // _decodeLock: FFmpeg contexts, the swscale context and _lastDecodedPtsUs (read, decode,
    // scale, seek, open, close). Held by workers for a whole frame decode.
    // _ringLock: _ready, _bufferPool and _lastPtsUs. Held for microseconds, so the main-thread
    // present path (TryTakeFrame, TryPeekPtsBuffered, ReleaseFrameBuffer) never waits on FFmpeg.
    // State flags (_isOpen, _isDisposed, _endOfStream, Info, _rgbaBufferSize, _ringCapacity) are
    // written only while holding both locks, so either lock is enough to read them.
    private readonly object _decodeLock = new object();
    private readonly object _ringLock = new object();
    private readonly VideoDecodeStats _decodeStats = new VideoDecodeStats();
    private unsafe AVFormatContext* _formatCtx;
    private unsafe AVCodecContext* _codecCtx;
    private unsafe SwsContext* _swsCtx;
    private unsafe AVPacket* _packet;
    private unsafe AVFrame* _frame;
    private int _rgbaBufferSize;
    // Plane buffer sizes on the 4:2:0 path. Written with both locks held; zero when unused.
    private int _yBufferSize;
    private int _uvBufferSize;
    private int _swsSrcW;
    private int _swsSrcH;
    private int _swsDstW;
    private int _swsDstH;
    private AVPixelFormat _swsSrcFmt = AVPixelFormat.AV_PIX_FMT_NONE;
    private bool _loggedScaleFailure;
    private bool _loggedPlanes;

    /// <summary>
    /// When true, 8-bit 4:2:0 frames are packed as Y and UV planes instead of RGBA.
    /// House playback sets this. The inspector preview leaves it false.
    /// </summary>
    public bool DeliverPlanar420 { get; set; }

    /// <summary>
    /// When true, plane frames carry ready-made Godot images (<see cref="VideoFrame.YImage"/>,
    /// <see cref="VideoFrame.UvImage"/>) made on the decode worker, and their byte buffers go
    /// straight back to the pool. The main thread then only uploads. House playback sets this.
    /// </summary>
    public bool PreparePlaneImages { get; set; }

    /// <summary>Total and longest time making plane images, and the frame count. Any thread.</summary>
    public void ReadPrepareStats(out long ticks, out long maxTicks, out long count) =>
        _decodeStats.ReadPrepare(out ticks, out maxTicks, out count);

    /// <summary>
    /// Copies the plane buffers into Godot images on this (worker) thread and returns the
    /// buffers to the pool. On any failure the frame keeps its buffers. Caller holds
    /// <c>_decodeLock</c>, not <c>_ringLock</c>.
    /// </summary>
    private void MakePlaneImages(VideoFrame frame)
    {
        long start = Stopwatch.GetTimestamp();
        Image y = null;
        Image uv = null;
        try
        {
            y = Image.CreateFromData(frame.Width, frame.Height, false, Image.Format.L8, frame.Y);
            uv = Image.CreateFromData(frame.ChromaWidth, frame.ChromaHeight, false, Image.Format.Rg8, frame.Uv);
        }
        catch (Exception ex)
        {
            if (!_loggedPrepareFailure)
            {
                _loggedPrepareFailure = true;
                GD.PrintErr($"VideoSourceDecoder:MakePlaneImages - {ex.Message}. Uploading from buffers instead.");
            }
        }
        if (y == null || uv == null || y.IsEmpty() || uv.IsEmpty())
        {
            y?.Dispose();
            uv?.Dispose();
            return;
        }

        frame.YImage = y;
        frame.UvImage = uv;
        byte[] yBuf = frame.Y;
        byte[] uvBuf = frame.Uv;
        frame.Y = null;
        frame.Uv = null;
        lock (_ringLock)
        {
            PoolPushUnlocked(yBuf);
            PoolPushUnlocked(uvBuf);
        }
        _decodeStats.AddPrepare(Stopwatch.GetTimestamp() - start);
    }

    private bool _loggedPrepareFailure;

    /// <summary>
    /// When true, open may use the show's GPU decoder. The previewer and house playback
    /// each set this on their own decoder. Default is Auto.
    /// </summary>
    public bool AllowHardwareDecode { get; set; } = true;

    /// <summary>Active decoder device name, or "none" for software. Set at open.</summary>
    public string HardwareName { get; private set; } = "none";

    private AVPixelFormat _hwPixelFormat = AVPixelFormat.AV_PIX_FMT_NONE;
    private bool _usingHardware;

    /// <summary>
    /// True once a picture has been queued since the last open or seek. Tells a software
    /// fallback whether to resume after the last queued picture or at the seek target.
    /// </summary>
    private bool _queuedSinceReset;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private unsafe delegate AVPixelFormat GetFormatDelegate(AVCodecContext* s, AVPixelFormat* fmt);

    private GetFormatDelegate _getFormatDelegate;
    private HwFormatState _hwState;
    private unsafe AVFrame* _swFrame;

    /// <summary>True after open when this decoder will deliver 8-bit 4:2:0 planes.</summary>
    public bool OutputsPlanar420 { get; private set; }

    private int _sourceWidth;
    private int _sourceHeight;
    private int _outputWidth;
    private int _outputHeight;

    /// <summary>Decoded picture width before any preview scale. Zero before open.</summary>
    public int SourceWidth => _sourceWidth;

    /// <summary>Decoded picture height before any preview scale. Zero before open.</summary>
    public int SourceHeight => _sourceHeight;

    /// <summary>Requested output width. Zero means the decoded width.</summary>
    public int OutputWidth => _outputWidth;

    /// <summary>Requested output height. Zero means the decoded height.</summary>
    public int OutputHeight => _outputHeight;
    private unsafe byte*[] _srcSlice;
    private readonly int[] _srcStride = new int[4];
    private unsafe byte*[] _dstSlice;
    private readonly int[] _dstStride = new int[4];
    private int _videoStreamIndex = -1;
    private AVRational _timeBase;
    private long _startTimeStream;
    private bool _endOfStream;
    private bool _isDisposed;
    private bool _isOpen;

    // Ring of decoded frames (RGBA or Y/UV planes) ready for pull
    private readonly Queue<VideoFrame> _ready = new Queue<VideoFrame>();
    private readonly Stack<byte[]> _bufferPool = new Stack<byte[]>();
    private readonly Stack<byte[]> _poolHold = new Stack<byte[]>();
    private int _ringCapacity = DefaultRingFrames;
    private long _lastPtsUs;
    private long _lastDecodedPtsUs;
    private long _dropBeforeUs = long.MinValue;
    private bool _keepNextFrame = true;
    private bool _discardingNonRef;
    private int _consecutiveDrops;
    private readonly AutoResetEvent _prefetchWake = new AutoResetEvent(false);
    private int _prefetchLowWater;
    private double _fps = FallbackFps;

    /// <summary>Stream info after successful open. Null before open.</summary>
    public VideoSourceInfo Info { get; private set; }

    /// <summary>
    /// Presentation time of the next frame that will be delivered, or last delivered if empty.
    /// </summary>
    public long PositionUs
    {
        get
        {
            lock (_ringLock)
            {
                if (_ready.Count > 0)
                    return _ready.Peek().PtsUs;
                return _lastPtsUs;
            }
        }
    }

    /// <summary>True when no more frames can be produced.</summary>
    public bool EndOfStream
    {
        get
        {
            lock (_ringLock) return _endOfStream && _ready.Count == 0;
        }
    }

    /// <summary>True once <see cref="Dispose"/> has run. Safe from any thread.</summary>
    public bool IsDisposed => Volatile.Read(ref _isDisposed);

    /// <summary>Decoded frames currently buffered.</summary>
    public int BufferedFrames
    {
        get
        {
            lock (_ringLock) return _ready.Count;
        }
    }

    /// <summary>
    /// Ring depth at which <see cref="TryTakeFrame"/> wakes a waiting prefetch worker.
    /// </summary>
    /// <param name="frames">Wake when a take leaves fewer frames than this. Zero disables the wake.</param>
    public void SetPrefetchLowWater(int frames)
    {
        Volatile.Write(ref _prefetchLowWater, Math.Max(0, frames));
    }

    /// <summary>
    /// Wakes one prefetch worker blocked in <see cref="WaitForPrefetchWake"/>.
    /// </summary>
    public void WakePrefetch()
    {
        try { _prefetchWake.Set(); }
        catch (ObjectDisposedException) { /* decoder already torn down */ }
    }

    /// <summary>
    /// Blocks until <see cref="WakePrefetch"/> or until the timeout elapses.
    /// </summary>
    /// <param name="timeoutMs">Maximum wait, in milliseconds.</param>
    /// <returns>True when a wake arrived before the timeout.</returns>
    public bool WaitForPrefetchWake(int timeoutMs)
    {
        try { return _prefetchWake.WaitOne(Math.Max(0, timeoutMs)); }
        catch (ObjectDisposedException) { return false; }
    }

    /// <summary>
    /// Copies decode and convert counters. Does not take the decode lock.
    /// </summary>
    public VideoDecodeStats.Snapshot GetDecodeStats() => _decodeStats.Copy();

    /// <summary>
    /// Records the current ring depth for the minimum-depth stat.
    /// Safe on the present path (ring lock only).
    /// </summary>
    public void ObserveRingDepth()
    {
        int depth;
        lock (_ringLock)
            depth = _ready.Count;
        _decodeStats.NoteRingDepth(depth);
    }

    /// <summary>Opens a video file for pull-based frame reads.</summary>
    public Task OpenAsync(string path, int ringFrames = DefaultRingFrames)
    {
        if (string.IsNullOrEmpty(path)) throw new ArgumentNullException(nameof(path));
        if (!File.Exists(path)) throw new FileNotFoundException("Video file not found.", path);

        return Task.Run(() => Open(path, ringFrames));
    }

    /// <summary>
    /// Opens a video file on the calling thread. Blocks while the file is read and probed,
    /// so call it from a worker, never from the main thread.
    /// </summary>
    public void Open(string path, int ringFrames = DefaultRingFrames)
    {
        if (string.IsNullOrEmpty(path)) throw new ArgumentNullException(nameof(path));
        if (!File.Exists(path)) throw new FileNotFoundException("Video file not found.", path);

        lock (_decodeLock)
        lock (_ringLock)
        {
            if (_isDisposed) throw new ObjectDisposedException(nameof(VideoSourceDecoder));
            CloseInternal();
            try
            {
                OpenInternal(path, ringFrames);
            }
            catch
            {
                // Partial native allocs (format/codec/sws/frames/buffers) must not stick around.
                try { CloseInternal(); } catch { /* ignore secondary cleanup errors */ }
                throw;
            }
        }
    }

    /// <summary>
    /// Pulls the next decoded RGBA frame.
    /// Decodes on demand if the ring is empty — avoid on the main/UI thread; prefer
    /// <see cref="TryTakeFrame"/> + background <see cref="Prefetch"/>.
    /// </summary>
    /// <param name="frame">Receives frame data (Rgba may be recycled on a later ReadFrame — copy if needed).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if a frame was returned; false on EOS or error.</returns>
    public bool ReadFrame(out VideoFrame frame, CancellationToken ct = default)
    {
        frame = null;
        lock (_decodeLock)
        {
            if (_isDisposed || !_isOpen || Info == null) return false;

            while (RingCount() == 0)
            {
                ct.ThrowIfCancellationRequested();
                if (_endOfStream) return false;
                if (!DecodeMoreUnlocked()) return false;
            }

            lock (_ringLock)
            {
                if (_ready.Count == 0) return false; // taken by a concurrent TryTakeFrame
                frame = _ready.Dequeue();
                _lastPtsUs = frame.PtsUs;
                return true;
            }
        }
    }

    /// <summary>
    /// Takes the next frame from the ring only (no decode). Safe for the present/UI path.
    /// </summary>
    /// <param name="frame">Receives frame data when true.</param>
    /// <returns>True if a buffered frame was returned.</returns>
    public bool TryTakeFrame(out VideoFrame frame)
    {
        frame = null;
        int remaining;
        lock (_ringLock)
        {
            if (_isDisposed || !_isOpen || _ready.Count == 0) return false;
            frame = _ready.Dequeue();
            _lastPtsUs = frame.PtsUs;
            remaining = _ready.Count;
        }

        // Wake outside the ring lock. A take that leaves the ring under the low-water
        // mark is the signal the prefetch worker waits for.
        if (remaining < Volatile.Read(ref _prefetchLowWater))
            WakePrefetch();
        return true;
    }

    /// <summary>
    /// Peeks next frame PTS without consuming, decoding if needed.
    /// Prefer <see cref="TryPeekPtsBuffered"/> on the present/UI path.
    /// </summary>
    public bool TryPeekPts(out long ptsUs)
    {
        ptsUs = 0;
        lock (_decodeLock)
        {
            if (RingCount() == 0)
            {
                if (_endOfStream || Info == null) return false;
                if (!DecodeMoreUnlocked()) return false;
            }
            lock (_ringLock)
            {
                if (_ready.Count == 0) return false;
                ptsUs = _ready.Peek().PtsUs;
                return true;
            }
        }
    }

    /// <summary>
    /// Peeks next buffered frame PTS without decoding. Safe for the present/UI path.
    /// </summary>
    /// <param name="ptsUs">PTS of the head frame when true.</param>
    /// <returns>True when the ring has at least one frame.</returns>
    public bool TryPeekPtsBuffered(out long ptsUs)
    {
        ptsUs = 0;
        lock (_ringLock)
        {
            if (_ready.Count == 0) return false;
            ptsUs = _ready.Peek().PtsUs;
            return true;
        }
    }

    /// <summary>
    /// Decodes ahead until the ring has approximately <paramref name="targetFrames"/> frames.
    /// Intended for worker threads; can block for multiple decode cycles.
    /// </summary>
    /// <remarks>
    /// Decodes under the decode lock only, one frame per hold. The ring lock is taken just to
    /// read the ring size and to enqueue the finished frame, so main-thread ring calls
    /// (<see cref="TryPeekPtsBuffered"/>, <see cref="TryTakeFrame"/>,
    /// <see cref="ReleaseFrameBuffer"/>) never wait on FFmpeg work.
    /// </remarks>
    public void Prefetch(int targetFrames = DefaultRingFrames / 2)
    {
        while (true)
        {
            lock (_decodeLock)
            {
                // Re-check each frame: a Seek, Flush or Dispose may have run between holds.
                if (!_isOpen || Info == null || _endOfStream) return;
                int target = Math.Clamp(targetFrames, 1, _ringCapacity);
                if (RingCount() >= target) return;
                if (!DecodeMoreUnlocked()) return;
            }

            // Let a waiting Seek or Dispose (decode lock) in between frames.
            Thread.Yield();
        }
    }

    /// <summary>
    /// Seeks to <paramref name="timestampUs"/> microseconds. Keyframe seek + decode-discard to target.
    /// Blocks while discarding to the target — call from a worker (see <see cref="SeekAsync"/>).
    /// </summary>
    public unsafe void Seek(long timestampUs)
    {
        try
        {
            lock (_decodeLock)
            {
                if (!_isOpen || Info == null) return;
                if (timestampUs < 0) timestampUs = 0;

                long seekTs = ffmpeg.av_rescale_q(
                    timestampUs,
                    new AVRational { num = 1, den = ffmpeg.AV_TIME_BASE },
                    _timeBase);

                if (_startTimeStream != 0 && _startTimeStream != ffmpeg.AV_NOPTS_VALUE)
                    seekTs += _startTimeStream;

                int ret = ffmpeg.avformat_seek_file(
                    _formatCtx, _videoStreamIndex,
                    long.MinValue, seekTs, seekTs, 0);

                if (ret < 0)
                    ret = ffmpeg.av_seek_frame(_formatCtx, _videoStreamIndex, seekTs, ffmpeg.AVSEEK_FLAG_BACKWARD);

                if (ret < 0)
                {
                    GD.PrintErr($"VideoSourceDecoder:Seek - failed: {MediaEngine.GetFFmpegError(ret)}");
                    return;
                }

                ffmpeg.avformat_flush(_formatCtx);
                ffmpeg.avcodec_flush_buffers(_codecCtx);

                // Return ready frames to pool; one ring hold so main never sees old frames after
                // the flush, or a stale end-of-stream.
                lock (_ringLock)
                {
                    while (_ready.Count > 0)
                    {
                        var f = _ready.Dequeue();
                        RecycleUnlocked(f);
                    }

                    _endOfStream = false;
                    _lastPtsUs = timestampUs;
                }
                _lastDecodedPtsUs = timestampUs;
                _queuedSinceReset = false;
                ResetDropCatchupUnlocked();

                // Discard until first frame with PTS >= target
                DiscardUntilUnlocked(timestampUs);
            }
        }
        finally
        {
            WakePrefetch();
        }
    }

    /// <summary>
    /// Worker-thread seek. Prefer this (or Task.Run of <see cref="Seek"/>) over calling Seek on the main thread.
    /// </summary>
    /// <param name="timestampUs">Target media time in microseconds.</param>
    /// <param name="ct">Cancellation token (checked before seek; discard itself is not mid-abortable).</param>
    public Task SeekAsync(long timestampUs, CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            Seek(timestampUs);
        }, ct);
    }

    /// <summary>Clears the frame ring without seeking.</summary>
    public void FlushBuffers()
    {
        try
        {
            lock (_decodeLock)
            lock (_ringLock)
            {
                while (_ready.Count > 0)
                    RecycleUnlocked(_ready.Dequeue());
                ResetDropCatchupUnlocked();
            }
        }
        finally
        {
            WakePrefetch();
        }
    }

    /// <summary>
    /// Tells the decode worker to discard pictures older than <paramref name="timestampUs"/>
    /// before colour conversion. The present thread calls this. It does not take the decode lock.
    /// </summary>
    /// <param name="timestampUs">Drop frames with a timestamp strictly earlier than this, in microseconds.</param>
    public void SetDropBefore(long timestampUs)
    {
        Volatile.Write(ref _dropBeforeUs, timestampUs);
    }

    /// <summary>
    /// Clears the drop marker so the next decoded picture is kept. Caller holds <c>_decodeLock</c>.
    /// </summary>
    private unsafe void ResetDropCatchupUnlocked()
    {
        Volatile.Write(ref _dropBeforeUs, long.MinValue);
        _keepNextFrame = true;
        _consecutiveDrops = 0;
        if (_discardingNonRef && _codecCtx != null)
        {
            _codecCtx->skip_frame = AVDiscard.AVDISCARD_DEFAULT;
            _discardingNonRef = false;
        }
    }

    /// <summary>True when this picture is already behind the present clock and can be skipped.</summary>
    private bool ShouldDropBeforeConvert(long ptsUs)
    {
        if (_keepNextFrame)
            return false;
        long dropBefore = Volatile.Read(ref _dropBeforeUs);
        if (dropBefore == long.MinValue)
            return false;
        return ptsUs < dropBefore;
    }

    /// <summary>
    /// Longest run of late pictures dropped in a row before one is kept anyway
    /// (about 100 ms of pictures, at least 3), so a decoder slower than real time
    /// still updates the output instead of freezing it.
    /// </summary>
    private int MaxConsecutiveDrops() => Math.Max(3, (int)Math.Round(_fps / 10.0));

    /// <summary>
    /// While badly behind, ask the codec to skip non-reference pictures.
    /// Caller holds <c>_decodeLock</c>.
    /// </summary>
    private unsafe void UpdateSkipFrame(long ptsUs)
    {
        if (_codecCtx == null)
            return;
        long dropBefore = Volatile.Read(ref _dropBeforeUs);
        if (dropBefore == long.MinValue)
            return;
        long lagUs = dropBefore - ptsUs;
        if (!_discardingNonRef && lagUs > 250_000)
        {
            _codecCtx->skip_frame = AVDiscard.AVDISCARD_NONREF;
            _discardingNonRef = true;
            _decodeStats.NoteNonRefSkipStart();
        }
        else if (_discardingNonRef && lagUs < 100_000)
        {
            _codecCtx->skip_frame = AVDiscard.AVDISCARD_DEFAULT;
            _discardingNonRef = false;
        }
    }

    /// <summary>
    /// Returns a frame's pixel buffer to the pool after the caller has finished with it.
    /// </summary>
    public void ReleaseFrameBuffer(byte[] buffer)
    {
        if (buffer == null || buffer.Length == 0) return;
        lock (_ringLock)
            PoolPushUnlocked(buffer);
    }

    /// <summary>True when the decoder still produces buffers of this length. Caller holds <c>_ringLock</c>.</summary>
    private bool IsPooledSizeUnlocked(int length) =>
        length > 0 && (length == _rgbaBufferSize || length == _yBufferSize || length == _uvBufferSize);

    /// <summary>
    /// Returns a buffer to the pool, or lets it go when its size is no longer produced
    /// (after a preview resize or a stream size change). Caller holds <c>_ringLock</c>.
    /// </summary>
    private void PoolPushUnlocked(byte[] buffer)
    {
        if (buffer == null || buffer.Length == 0)
            return;
        if (IsPooledSizeUnlocked(buffer.Length))
            _bufferPool.Push(buffer);
        else
            MediaMemory.NoteReleased(MediaMemory.ByteBufferBytes(buffer));
    }

    /// <summary>Drops pooled buffers whose size is no longer produced. Caller holds <c>_ringLock</c>.</summary>
    private void PurgePoolUnlocked()
    {
        long released = 0;
        while (_bufferPool.Count > 0)
        {
            byte[] buf = _bufferPool.Pop();
            if (buf != null && IsPooledSizeUnlocked(buf.Length))
                _poolHold.Push(buf);
            else
                released += MediaMemory.ByteBufferBytes(buf);
        }
        while (_poolHold.Count > 0)
            _bufferPool.Push(_poolHold.Pop());
        if (released > 0)
            MediaMemory.NoteReleased(released);
    }

    /// <summary>
    /// Scales RGBA output to this size. Pass 0, 0 to keep the decoded size.
    /// House playback leaves this unset. The inspector preview sets the canvas preview size.
    /// Takes the decode lock. Does not share state with any other decoder.
    /// </summary>
    /// <param name="width">Output width in pixels, or 0 for the decoded width.</param>
    /// <param name="height">Output height in pixels, or 0 for the decoded height.</param>
    public unsafe void SetOutputSize(int width, int height)
    {
        if (width < 0)
            width = 0;
        if (height < 0)
            height = 0;
        lock (_decodeLock)
        {
            if (_outputWidth == width && _outputHeight == height)
                return;
            _outputWidth = width;
            _outputHeight = height;
            if (!_isOpen)
                return;
            if (_swsCtx != null)
            {
                ffmpeg.sws_freeContext(_swsCtx);
                _swsCtx = null;
            }
            _swsSrcW = 0;
            _swsSrcH = 0;
            _swsDstW = 0;
            _swsDstH = 0;
            _swsSrcFmt = AVPixelFormat.AV_PIX_FMT_NONE;
            int bytes = OutputByteSize();
            lock (_ringLock)
            {
                if (bytes > 0)
                    _rgbaBufferSize = bytes;
                while (_ready.Count > 0)
                    RecycleUnlocked(_ready.Dequeue());
                PurgePoolUnlocked();
            }
        }
    }

    /// <summary>RGBA byte count for the current output size. Caller holds <c>_decodeLock</c>.</summary>
    private int OutputByteSize()
    {
        int w = _outputWidth > 0 ? _outputWidth : _sourceWidth;
        int h = _outputHeight > 0 ? _outputHeight : _sourceHeight;
        if (w < 1 || h < 1)
            return 0;
        return w * h * RgbaChannels;
    }

    /// <summary>
    /// Returns every buffer on <paramref name="frame"/> to the pool.
    /// </summary>
    public void ReleaseFrame(VideoFrame frame)
    {
        if (frame == null) return;
        // Free the plane images outside the ring lock so the worker is not held up.
        Image y = frame.YImage;
        Image uv = frame.UvImage;
        frame.YImage = null;
        frame.UvImage = null;
        lock (_ringLock)
            RecycleUnlocked(frame);
        y?.Dispose();
        uv?.Dispose();
    }

    /// <summary>Caller holds <c>_ringLock</c>.</summary>
    private void RecycleUnlocked(VideoFrame frame)
    {
        if (frame == null) return;
        PoolPushUnlocked(frame.Rgba);
        PoolPushUnlocked(frame.Y);
        PoolPushUnlocked(frame.Uv);
        frame.Rgba = null;
        frame.Y = null;
        frame.Uv = null;
        frame.DisposePlaneImages();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_decodeLock)
        lock (_ringLock)
        {
            if (_isDisposed) return;
            CloseInternal();
            _isDisposed = true;
        }

        try { _prefetchWake.Set(); } catch (ObjectDisposedException) { /* already closed */ }
        try { _prefetchWake.Dispose(); } catch (ObjectDisposedException) { /* already closed */ }
    }

    /// <summary>Stage times of the last open (for the start timing log).</summary>
    public DecoderOpenTimes LastOpenTimes { get; private set; } = new DecoderOpenTimes();

    private unsafe void OpenInternal(string path, int ringFrames)
    {
        _ringCapacity = Math.Clamp(ringFrames, 2, 32);
        int ret;
        var times = new DecoderOpenTimes();
        long stageStart = Stopwatch.GetTimestamp();

        // Always free options — open_input failure used to leak the dict before throw.
        AVDictionary* options = null;
        try
        {
            ffmpeg.av_dict_set(&options, "fflags", "+genpts", 0);
            fixed (AVFormatContext** pCtx = &_formatCtx)
            {
                ret = ffmpeg.avformat_open_input(pCtx, path, null, &options);
                if (ret < 0)
                    throw new Exception($"VideoSourceDecoder:Open - open_input failed: {MediaEngine.GetFFmpegError(ret)}");
            }
        }
        finally
        {
            if (options != null)
                ffmpeg.av_dict_free(&options);
        }

        _formatCtx->flags |= ffmpeg.AVFMT_FLAG_GENPTS;
        times.InputMs = DecoderOpenTimes.Since(stageStart);
        stageStart = Stopwatch.GetTimestamp();

        ret = ffmpeg.avformat_find_stream_info(_formatCtx, null);
        if (ret < 0)
            throw new Exception($"VideoSourceDecoder:Open - find_stream_info failed: {MediaEngine.GetFFmpegError(ret)}");
        times.StreamInfoMs = DecoderOpenTimes.Since(stageStart);

        _videoStreamIndex = -1;
        for (uint i = 0; i < _formatCtx->nb_streams; i++)
        {
            if (_formatCtx->streams[i]->codecpar->codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO)
            {
                _videoStreamIndex = (int)i;
                break;
            }
        }
        if (_videoStreamIndex < 0)
            throw new Exception("VideoSourceDecoder:Open - No video stream found.");

        AVStream* stream = _formatCtx->streams[(uint)_videoStreamIndex];
        _timeBase = stream->time_base;
        _startTimeStream = stream->start_time != ffmpeg.AV_NOPTS_VALUE ? stream->start_time : 0;

        AVCodec* codec = ffmpeg.avcodec_find_decoder(stream->codecpar->codec_id);
        if (codec == null)
            throw new Exception($"VideoSourceDecoder:Open - Unsupported codec {stream->codecpar->codec_id}.");

        stageStart = Stopwatch.GetTimestamp();
        OpenCodecUnlocked(codec, stream, AllowHardwareDecode);
        times.CodecMs = DecoderOpenTimes.Since(stageStart);

        int width = _codecCtx->width;
        int height = _codecCtx->height;
        if (width <= 0 || height <= 0)
            throw new Exception("VideoSourceDecoder:Open - Invalid dimensions.");

        if (ffmpeg.sws_isSupportedInput(_codecCtx->pix_fmt) == 0)
        {
            string unsupported = PixFmtName(_codecCtx->pix_fmt);
            throw new Exception($"VideoSourceDecoder:Open - unsupported pixel format {unsupported}.");
        }

        _sourceWidth = width;
        _sourceHeight = height;
        int poolBytes = OutputByteSize();
        _rgbaBufferSize = poolBytes > 0 ? poolBytes : width * height * RgbaChannels;
        _frame = ffmpeg.av_frame_alloc();
        _packet = ffmpeg.av_packet_alloc();
        if (_frame == null || _packet == null)
            throw new Exception("VideoSourceDecoder:Open - frame/packet alloc failed.");

        _srcSlice ??= new byte*[4];
        _dstSlice ??= new byte*[4];
        _swsSrcW = 0;
        _swsSrcH = 0;
        _swsDstW = 0;
        _swsDstH = 0;
        _swsSrcFmt = AVPixelFormat.AV_PIX_FMT_NONE;
        _loggedScaleFailure = false;
        _loggedPlanes = false;
        OutputsPlanar420 = DeliverPlanar420 && Is8Bit420(_codecCtx->pix_fmt);

        // Pre-allocate buffer pool. Planar frames need a luma buffer and a chroma buffer.
        _yBufferSize = 0;
        _uvBufferSize = 0;
        if (OutputsPlanar420)
        {
            int ySize = width * height;
            int cw = (width + 1) >> 1;
            int ch = (height + 1) >> 1;
            int uvSize = cw * ch * 2;
            _yBufferSize = ySize;
            _uvBufferSize = uvSize;
            for (int i = 0; i < _ringCapacity + 2; i++)
            {
                _bufferPool.Push(new byte[ySize]);
                _bufferPool.Push(new byte[uvSize]);
            }
        }
        else
        {
            for (int i = 0; i < _ringCapacity + 2; i++)
                _bufferPool.Push(new byte[_rgbaBufferSize]);
        }

        _fps = ffmpeg.av_q2d(stream->r_frame_rate);
        if (_fps <= 0.1) _fps = ffmpeg.av_q2d(stream->avg_frame_rate);
        if (_fps <= 0.1) _fps = FallbackFps;
        long frameDurationUs = (long)(1_000_000.0 / _fps);

        long durationUs = 0;
        if (stream->duration > 0 && stream->duration != ffmpeg.AV_NOPTS_VALUE)
            durationUs = ffmpeg.av_rescale_q(stream->duration, _timeBase, new AVRational { num = 1, den = ffmpeg.AV_TIME_BASE });
        else if (_formatCtx->duration > 0 && _formatCtx->duration != ffmpeg.AV_NOPTS_VALUE)
            durationUs = _formatCtx->duration;

        string codecName = ffmpeg.avcodec_get_name(codec->id) ?? "unknown";
        string decoderName = Utf8Or(codec->name, codecName);
        string pixFmt = PixFmtName(_codecCtx->pix_fmt);
        int bits = _codecCtx->bits_per_raw_sample;
        if (bits <= 0 && stream->codecpar != null)
            bits = stream->codecpar->bits_per_raw_sample;
        string space = ColorName(ffmpeg.av_color_space_name(_codecCtx->colorspace));
        string range = ColorName(ffmpeg.av_color_range_name(_codecCtx->color_range));
        string primaries = ColorName(ffmpeg.av_color_primaries_name(_codecCtx->color_primaries));
        string transfer = ColorName(ffmpeg.av_color_transfer_name(_codecCtx->color_trc));

        Info = new VideoSourceInfo
        {
            Width = width,
            Height = height,
            Fps = _fps,
            FrameDurationUs = frameDurationUs,
            DurationUs = durationUs,
            CodecName = codecName,
            FilePath = path,
            FrameByteSize = _rgbaBufferSize
        };

        _lastPtsUs = 0;
        _lastDecodedPtsUs = 0;
        _queuedSinceReset = false;
        _endOfStream = false;
        _isOpen = true;
        ResetDropCatchupUnlocked();
        times.EndTicks = Stopwatch.GetTimestamp();
        LastOpenTimes = times;

        GD.Print(
            $"VideoSourceDecoder:Open - {path} {width}x{height} @{_fps:F2}fps codec={codecName} " +
            $"pix_fmt={pixFmt} bits={(bits > 0 ? bits.ToString() : "na")} space={space} range={range} " +
            $"primaries={primaries} transfer={transfer} decoder={decoderName} " +
            $"threads={_codecCtx->thread_count} hw={HardwareName} upload={(OutputsPlanar420 ? "yuv420" : "rgba")} durationUs={durationUs}");
    }

    private unsafe void DiscardUntilUnlocked(long targetUs)
    {
        int safety = 0;
        // Cap discard work per seek so a bad stream cannot spin forever on a worker.
        // ~GOP-length discards after keyframe seek; 30k packet/frame steps is generous for 4K.
        const int maxIterations = 30_000;

        while (safety++ < maxIterations && !_endOfStream)
        {
            // Try receive first
            int ret = ffmpeg.avcodec_receive_frame(_codecCtx, _frame);
            if (ret >= 0 && HardwareFormatRejected())
            {
                ffmpeg.av_frame_unref(_frame);
                FallbackToSoftwareUnlocked("the GPU decoder rejected this stream");
                return;
            }
            if (ret >= 0)
            {
                long ptsUs = FramePtsUsUnlocked(_frame);
                if (ptsUs + Info.FrameDurationUs / 2 < targetUs)
                {
                    ffmpeg.av_frame_unref(_frame);
                    continue; // still before target; do not copy a hardware frame back
                }
                if (!TryPrepareDecodedFrame(_frame, out AVFrame* ready))
                {
                    ffmpeg.av_frame_unref(_frame);
                    FallbackToSoftwareUnlocked("hardware copy-back failed");
                    return;
                }
                EnqueueConvertedUnlocked(ready, ptsUs);
                ffmpeg.av_frame_unref(_frame);
                if (ready != _frame)
                    ffmpeg.av_frame_unref(ready);
                return;
            }
            if (ret != ffmpeg.AVERROR(ffmpeg.EAGAIN) && ret != ffmpeg.AVERROR_EOF)
            {
                GD.PrintErr($"VideoSourceDecoder:DiscardUntil - receive: {MediaEngine.GetFFmpegError(ret)}");
                if (_usingHardware)
                    FallbackToSoftwareUnlocked($"decode error {MediaEngine.GetFFmpegError(ret)}");
                return;
            }
            if (ret == ffmpeg.AVERROR_EOF)
            {
                SetEndOfStreamUnderDecode();
                return;
            }

            ret = ffmpeg.av_read_frame(_formatCtx, _packet);
            if (ret == ffmpeg.AVERROR_EOF)
            {
                ffmpeg.avcodec_send_packet(_codecCtx, null);
                continue;
            }
            if (ret < 0)
            {
                GD.PrintErr($"VideoSourceDecoder:DiscardUntil - read: {MediaEngine.GetFFmpegError(ret)}");
                SetEndOfStreamUnderDecode();
                return;
            }

            if (_packet->stream_index != _videoStreamIndex)
            {
                ffmpeg.av_packet_unref(_packet);
                continue;
            }

            // Optional: skip packets clearly before target using packet PTS
            long pktPts = _packet->pts != ffmpeg.AV_NOPTS_VALUE ? _packet->pts : _packet->dts;
            if (pktPts != ffmpeg.AV_NOPTS_VALUE)
            {
                long pktUs = PacketPtsToUsUnlocked(pktPts);
                // Still send — need decoder state for keyframe rebuild; only skip if far before
                // (always send for correctness after keyframe seek)
            }

            ret = ffmpeg.avcodec_send_packet(_codecCtx, _packet);
            ffmpeg.av_packet_unref(_packet);
            if (ret < 0 && ret != ffmpeg.AVERROR(ffmpeg.EAGAIN))
            {
                if (_usingHardware)
                {
                    FallbackToSoftwareUnlocked($"send error {MediaEngine.GetFFmpegError(ret)}");
                    return;
                }
                continue;
            }
        }
    }

    private unsafe bool DecodeMoreUnlocked()
    {
        // Caller holds _decodeLock. Only this lock's holder adds to the ring, so the ring can
        // only shrink while we decode (main takes frames).
        if (_endOfStream) return false;
        if (RingCount() >= _ringCapacity) return true;

        int iterations = 0;
        int dropsThisCall = 0;
        const int maxIterations = 128;
        const int maxDropsBeforeYield = 16;
        long workStart = Stopwatch.GetTimestamp();

        while (iterations++ < maxIterations && RingCount() < _ringCapacity)
        {
            int ret = ffmpeg.avcodec_receive_frame(_codecCtx, _frame);
            if (ret >= 0 && HardwareFormatRejected())
            {
                ffmpeg.av_frame_unref(_frame);
                FallbackToSoftwareUnlocked("the GPU decoder rejected this stream");
                return true;
            }
            if (ret >= 0)
            {
                long ptsUs = FramePtsUsUnlocked(_frame);
                // Lag is judged on every picture, dropped or kept, so the skip can engage.
                UpdateSkipFrame(ptsUs);
                if (ShouldDropBeforeConvert(ptsUs))
                {
                    if (_consecutiveDrops < MaxConsecutiveDrops())
                    {
                        _consecutiveDrops++;
                        ffmpeg.av_frame_unref(_frame);
                        _decodeStats.NoteDecoded();
                        _decodeStats.NoteDroppedBeforeConvert();
                        if (++dropsThisCall >= maxDropsBeforeYield)
                            return true;
                        workStart = Stopwatch.GetTimestamp();
                        continue;
                    }

                    // Too many in a row: show this late picture so the output keeps moving.
                    _decodeStats.NoteKeptToCatchUp();
                }

                _consecutiveDrops = 0;
                _keepNextFrame = false;
                _decodeStats.AddDecode(Stopwatch.GetTimestamp() - workStart);
                _decodeStats.NoteDecoded();
                if (!TryPrepareDecodedFrame(_frame, out AVFrame* ready))
                {
                    ffmpeg.av_frame_unref(_frame);
                    FallbackToSoftwareUnlocked("hardware copy-back failed");
                    return true;
                }
                bool queued = EnqueueConvertedUnlocked(ready, ptsUs);
                ffmpeg.av_frame_unref(_frame);
                if (ready != _frame)
                    ffmpeg.av_frame_unref(ready);
                return queued;
            }
            if (ret != ffmpeg.AVERROR(ffmpeg.EAGAIN) && ret != ffmpeg.AVERROR_EOF)
            {
                GD.PrintErr($"VideoSourceDecoder:DecodeMore - receive: {MediaEngine.GetFFmpegError(ret)}");
                if (_usingHardware)
                {
                    FallbackToSoftwareUnlocked($"decode error {MediaEngine.GetFFmpegError(ret)}");
                    return true;
                }
                SetEndOfStreamUnderDecode();
                return false;
            }
            if (ret == ffmpeg.AVERROR_EOF)
            {
                SetEndOfStreamUnderDecode();
                return false;
            }

            ret = ffmpeg.av_read_frame(_formatCtx, _packet);
            if (ret == ffmpeg.AVERROR_EOF)
            {
                ffmpeg.avcodec_send_packet(_codecCtx, null);
                continue;
            }
            if (ret < 0)
            {
                GD.PrintErr($"VideoSourceDecoder:DecodeMore - read: {MediaEngine.GetFFmpegError(ret)}");
                SetEndOfStreamUnderDecode();
                return false;
            }

            if (_packet->stream_index != _videoStreamIndex)
            {
                ffmpeg.av_packet_unref(_packet);
                continue;
            }

            ret = ffmpeg.avcodec_send_packet(_codecCtx, _packet);
            ffmpeg.av_packet_unref(_packet);
            if (ret < 0 && ret != ffmpeg.AVERROR(ffmpeg.EAGAIN))
            {
                if (_usingHardware)
                {
                    FallbackToSoftwareUnlocked($"send error {MediaEngine.GetFFmpegError(ret)}");
                    return true;
                }
                continue;
            }
        }

        return false;
    }

    /// <summary>Ring size, read under the ring lock. Safe while holding the decode lock.</summary>
    private int RingCount()
    {
        lock (_ringLock) return _ready.Count;
    }

    /// <summary>Sets end of stream. Caller holds _decodeLock; takes _ringLock for the write.</summary>
    private void SetEndOfStreamUnderDecode()
    {
        lock (_ringLock) _endOfStream = true;
    }

    /// <summary>
    /// Converts <paramref name="src"/> straight into a pooled RGBA buffer and enqueues it.
    /// Caller holds <c>_decodeLock</c>. Returns false when the scaler cannot be built.
    /// </summary>
    private unsafe bool EnqueueConvertedUnlocked(AVFrame* src, long ptsUs)
    {
        if (src == null)
        {
            _lastDecodedPtsUs = ptsUs;
            return false;
        }

        bool nativeSize = _outputWidth <= 0 || _outputHeight <= 0
            || (_outputWidth == src->width && _outputHeight == src->height);
        var srcFmt = (AVPixelFormat)src->format;
        if (OutputsPlanar420 && nativeSize && IsNv12(srcFmt)
            && src->data[0] != null && src->data[1] != null)
            return EnqueueNv12Unlocked(src, ptsUs);

        if (OutputsPlanar420 && Is8Bit420(srcFmt)
            && src->data[0] != null && src->data[1] != null && src->data[2] != null)
            return EnqueuePlanar420Unlocked(src, ptsUs);

        if (!EnsureScaleContext(src))
        {
            _lastDecodedPtsUs = ptsUs;
            return false;
        }

        int width = src->width;
        int height = src->height;
        int byteSize = _rgbaBufferSize;
        _srcSlice[0] = src->data[0];
        _srcSlice[1] = src->data[1];
        _srcSlice[2] = src->data[2];
        _srcSlice[3] = src->data[3];
        _srcStride[0] = src->linesize[0];
        _srcStride[1] = src->linesize[1];
        _srcStride[2] = src->linesize[2];
        _srcStride[3] = src->linesize[3];

        byte[] buf = TakeExactBuffer(byteSize);
        bool scaledOk;
        fixed (byte* dst = buf)
        {
            _dstSlice[0] = dst;
            _dstSlice[1] = null;
            _dstSlice[2] = null;
            _dstSlice[3] = null;
            _dstStride[0] = (_swsDstW > 0 ? _swsDstW : width) * RgbaChannels;
            _dstStride[1] = 0;
            _dstStride[2] = 0;
            _dstStride[3] = 0;

            long convertStart = Stopwatch.GetTimestamp();
            int scaled = ffmpeg.sws_scale(_swsCtx, _srcSlice, _srcStride, 0, height, _dstSlice, _dstStride);
            _decodeStats.AddConvert(Stopwatch.GetTimestamp() - convertStart);
            scaledOk = scaled > 0;
        }

        if (!scaledOk)
        {
            if (!_loggedScaleFailure)
            {
                _loggedScaleFailure = true;
                GD.PrintErr($"VideoSourceDecoder:Scale - sws_scale failed for {width}x{height}.");
            }
            ReleaseFrameBuffer(buf);
            _lastDecodedPtsUs = ptsUs;
            return false;
        }

        var frame = new VideoFrame
        {
            Rgba = buf,
            PtsUs = ptsUs,
            Width = _swsDstW > 0 ? _swsDstW : width,
            Height = _swsDstH > 0 ? _swsDstH : height
        };
        lock (_ringLock)
        {
            _ready.Enqueue(frame);
            _lastPtsUs = ptsUs;
        }
        _lastDecodedPtsUs = ptsUs;
        _queuedSinceReset = true;
        _decodeStats.NoteConverted();
        _decodeStats.NoteRingDepth(RingCount());
        return true;
    }

    /// <summary>
    /// Pops a pool buffer of exactly <paramref name="byteSize"/> bytes, or allocates one.
    /// Caller holds <c>_decodeLock</c>. The pool only holds sizes the decoder still produces
    /// (see <see cref="PoolPushUnlocked"/>), so the search is short.
    /// </summary>
    private byte[] TakeExactBuffer(int byteSize)
    {
        byte[] buf = null;
        lock (_ringLock)
        {
            while (_bufferPool.Count > 0)
            {
                byte[] candidate = _bufferPool.Pop();
                if (candidate != null && candidate.Length == byteSize)
                {
                    buf = candidate;
                    break;
                }
                if (candidate != null)
                    _poolHold.Push(candidate);
            }
            while (_poolHold.Count > 0)
                _bufferPool.Push(_poolHold.Pop());
        }

        return buf ?? new byte[byteSize];
    }

    /// <summary>
    /// Packs 8-bit 4:2:0 into a full-size Y buffer and a half-size interleaved UV buffer.
    /// Caller holds <c>_decodeLock</c>.
    /// </summary>
    private unsafe bool EnqueuePlanar420Unlocked(AVFrame* src, long ptsUs)
    {
        int width = src->width;
        int height = src->height;
        if (width <= 0 || height <= 0)
        {
            _lastDecodedPtsUs = ptsUs;
            return false;
        }

        int cw = (width + 1) >> 1;
        int ch = (height + 1) >> 1;
        int ySize = width * height;
        int uvSize = cw * ch * 2;
        NotePlaneSizesUnlocked(ySize, uvSize);
        byte[] yBuf = TakeExactBuffer(ySize);
        byte[] uvBuf = TakeExactBuffer(uvSize);

        long copyStart = Stopwatch.GetTimestamp();
        CopyPlane(src->data[0], src->linesize[0], yBuf, width, height);
        CopyChromaInterleaved(src->data[1], src->linesize[1], src->data[2], src->linesize[2], uvBuf, cw, ch);
        _decodeStats.AddCopy(Stopwatch.GetTimestamp() - copyStart);

        int swsCs = SwsColorspaceFor(src->colorspace, height);
        bool limited = !IsFullRange(src, (AVPixelFormat)src->format);
        if (!_loggedPlanes)
        {
            _loggedPlanes = true;
            GD.Print(
                $"VideoSourceDecoder:Planes - {width}x{height} src={PixFmtName((AVPixelFormat)src->format)} " +
                $"matrix={SwsColorspaceName(swsCs)} srcRange={(limited ? "limited" : "full")}");
        }

        var frame = new VideoFrame
        {
            Y = yBuf,
            Uv = uvBuf,
            ChromaWidth = cw,
            ChromaHeight = ch,
            LimitedRange = limited,
            MatrixId = MatrixIdFor(swsCs),
            PtsUs = ptsUs,
            Width = width,
            Height = height
        };
        if (PreparePlaneImages)
            MakePlaneImages(frame);
        lock (_ringLock)
        {
            _ready.Enqueue(frame);
            _lastPtsUs = ptsUs;
        }
        _lastDecodedPtsUs = ptsUs;
        _queuedSinceReset = true;
        _decodeStats.NoteConverted();
        _decodeStats.NoteRingDepth(RingCount());
        return true;
    }

    /// <summary>
    /// Packs NV12 (hardware copy-back) into the same Y and interleaved UV buffers as 4:2:0.
    /// Caller holds <c>_decodeLock</c>.
    /// </summary>
    private unsafe bool EnqueueNv12Unlocked(AVFrame* src, long ptsUs)
    {
        int width = src->width;
        int height = src->height;
        if (width <= 0 || height <= 0)
        {
            _lastDecodedPtsUs = ptsUs;
            return false;
        }

        int cw = (width + 1) >> 1;
        int ch = (height + 1) >> 1;
        NotePlaneSizesUnlocked(width * height, cw * ch * 2);
        byte[] yBuf = TakeExactBuffer(width * height);
        byte[] uvBuf = TakeExactBuffer(cw * ch * 2);
        long copyStart = Stopwatch.GetTimestamp();
        CopyPlane(src->data[0], src->linesize[0], yBuf, width, height);
        CopyPlane(src->data[1], src->linesize[1], uvBuf, cw * 2, ch);
        _decodeStats.AddCopy(Stopwatch.GetTimestamp() - copyStart);

        int swsCs = SwsColorspaceFor(src->colorspace, height);
        bool limited = !IsFullRange(src, AVPixelFormat.AV_PIX_FMT_NV12);
        if (!_loggedPlanes)
        {
            _loggedPlanes = true;
            GD.Print(
                $"VideoSourceDecoder:Planes - {width}x{height} src=nv12 " +
                $"matrix={SwsColorspaceName(swsCs)} srcRange={(limited ? "limited" : "full")}");
        }

        var frame = new VideoFrame
        {
            Y = yBuf,
            Uv = uvBuf,
            ChromaWidth = cw,
            ChromaHeight = ch,
            LimitedRange = limited,
            MatrixId = MatrixIdFor(swsCs),
            PtsUs = ptsUs,
            Width = width,
            Height = height
        };
        if (PreparePlaneImages)
            MakePlaneImages(frame);
        lock (_ringLock)
        {
            _ready.Enqueue(frame);
            _lastPtsUs = ptsUs;
        }
        _lastDecodedPtsUs = ptsUs;
        _queuedSinceReset = true;
        _decodeStats.NoteConverted();
        _decodeStats.NoteRingDepth(RingCount());
        return true;
    }

    private static bool IsNv12(AVPixelFormat fmt) => fmt == AVPixelFormat.AV_PIX_FMT_NV12;

    /// <summary>
    /// Records the plane sizes the 4:2:0 path now produces and drops pooled buffers of
    /// older sizes. Caller holds <c>_decodeLock</c>.
    /// </summary>
    private void NotePlaneSizesUnlocked(int ySize, int uvSize)
    {
        if (ySize == _yBufferSize && uvSize == _uvBufferSize)
            return;
        lock (_ringLock)
        {
            _yBufferSize = ySize;
            _uvBufferSize = uvSize;
            PurgePoolUnlocked();
        }
    }

    /// <summary>
    /// Opens the codec, trying the shared GPU device first when <paramref name="allowHardware"/> is set.
    /// Caller holds <c>_decodeLock</c>.
    /// </summary>
    private unsafe void OpenCodecUnlocked(AVCodec* codec, AVStream* stream, bool allowHardware)
    {
        FreeCodecUnlocked();
        if (!TryCreateCodecContext(codec, stream))
            throw new Exception("VideoSourceDecoder:Open - codec context alloc failed.");

        if (allowHardware && TryAttachHardware(codec))
        {
            _codecCtx->thread_count = 1;
            int opened = ffmpeg.avcodec_open2(_codecCtx, codec, null);
            if (opened >= 0 && _hwState != null && !_hwState.Rejected)
            {
                _usingHardware = true;
                HardwareName = _hwState.Name;
                return;
            }

            string reason = opened < 0
                ? MediaEngine.GetFFmpegError(opened)
                : "pixel format rejected";
            GD.Print($"VideoSourceDecoder:Open - hardware decode unavailable ({reason}), using software.");
            FreeCodecUnlocked();
            if (!TryCreateCodecContext(codec, stream))
                throw new Exception("VideoSourceDecoder:Open - codec context alloc failed.");
        }

        _codecCtx->thread_count = Math.Max(1, System.Environment.ProcessorCount / 2);
        int ret = ffmpeg.avcodec_open2(_codecCtx, codec, null);
        if (ret < 0)
        {
            string err = MediaEngine.GetFFmpegError(ret);
            FreeCodecUnlocked();
            throw new Exception($"VideoSourceDecoder:Open - codec open failed: {err}");
        }

        _usingHardware = false;
        HardwareName = "none";
    }

    private unsafe bool TryCreateCodecContext(AVCodec* codec, AVStream* stream)
    {
        _codecCtx = ffmpeg.avcodec_alloc_context3(codec);
        if (_codecCtx == null)
            return false;
        int ret = ffmpeg.avcodec_parameters_to_context(_codecCtx, stream->codecpar);
        if (ret < 0)
        {
            FreeCodecUnlocked();
            return false;
        }
        _codecCtx->pkt_timebase = _timeBase;
        return true;
    }

    /// <summary>
    /// Attaches a ref of the process-wide hardware device when this codec can use it.
    /// Caller holds <c>_decodeLock</c>.
    /// </summary>
    private unsafe bool TryAttachHardware(AVCodec* codec)
    {
        AVBufferRef* device = MediaEngine.RetainHardwareDevice(out string name);
        if (device == null || string.IsNullOrEmpty(name))
            return false;

        AVPixelFormat hwFmt = AVPixelFormat.AV_PIX_FMT_NONE;
        for (int i = 0; ; i++)
        {
            AVCodecHWConfig* config = ffmpeg.avcodec_get_hw_config(codec, i);
            if (config == null)
                break;
            if ((config->methods & (int)AvCodecHwConfigMethod.AV_CODEC_HW_CONFIG_METHOD_HW_DEVICE_CTX) == 0)
                continue;
            if (config->device_type != MediaEngine.ActiveHardwareDeviceType)
                continue;
            hwFmt = config->pix_fmt;
            break;
        }

        if (hwFmt == AVPixelFormat.AV_PIX_FMT_NONE)
        {
            ffmpeg.av_buffer_unref(&device);
            return false;
        }

        _hwState = new HwFormatState { HwFormat = hwFmt, Name = name };
        _getFormatDelegate = SelectHwFormat;
        _codecCtx->hw_device_ctx = device;
        _codecCtx->get_format = new AVCodecContext_get_format_func
        {
            Pointer = Marshal.GetFunctionPointerForDelegate(_getFormatDelegate)
        };
        _hwPixelFormat = hwFmt;
        return true;
    }

    /// <summary>
    /// Negotiates the hardware pixel format. The delegate is kept on this decoder so the GC
    /// cannot collect it while FFmpeg calls it.
    /// </summary>
    private unsafe AVPixelFormat SelectHwFormat(AVCodecContext* ctx, AVPixelFormat* formats)
    {
        if (formats == null)
            return AVPixelFormat.AV_PIX_FMT_NONE;
        AVPixelFormat wanted = _hwState?.HwFormat ?? _hwPixelFormat;
        for (int i = 0; formats[i] != AVPixelFormat.AV_PIX_FMT_NONE; i++)
        {
            if (formats[i] == wanted)
                return wanted;
        }
        if (_hwState != null)
            _hwState.Rejected = true;

        // Hardware formats are listed first. Pick the first software format so FFmpeg
        // does not try another hardware path that needs a legacy context.
        for (int i = 0; formats[i] != AVPixelFormat.AV_PIX_FMT_NONE; i++)
        {
            AVPixFmtDescriptor* desc = ffmpeg.av_pix_fmt_desc_get(formats[i]);
            if (desc != null && (desc->flags & ffmpeg.AV_PIX_FMT_FLAG_HWACCEL) == 0)
                return formats[i];
        }
        return formats[0];
    }

    /// <summary>
    /// True when the codec was opened for the GPU but refused the GPU format on the first
    /// packets, so it is now decoding in software with one thread. Caller holds <c>_decodeLock</c>.
    /// </summary>
    private bool HardwareFormatRejected() => _usingHardware && _hwState != null && _hwState.Rejected;

    /// <summary>
    /// Copies a hardware frame back to a software frame. Software frames pass through.
    /// Caller holds <c>_decodeLock</c>.
    /// </summary>
    private unsafe bool TryPrepareDecodedFrame(AVFrame* decoded, out AVFrame* ready)
    {
        ready = decoded;
        if (decoded == null)
            return false;
        if (!_usingHardware || (AVPixelFormat)decoded->format != _hwPixelFormat)
            return true;

        if (_swFrame == null)
            _swFrame = ffmpeg.av_frame_alloc();
        if (_swFrame == null)
            return false;
        ffmpeg.av_frame_unref(_swFrame);
        int ret = ffmpeg.av_hwframe_transfer_data(_swFrame, decoded, 0);
        if (ret < 0)
        {
            GD.PrintErr($"VideoSourceDecoder:Decode - hardware copy-back failed: {MediaEngine.GetFFmpegError(ret)}");
            return false;
        }

        _swFrame->pts = decoded->pts;
        _swFrame->best_effort_timestamp = decoded->best_effort_timestamp;
        _swFrame->pkt_dts = decoded->pkt_dts;
        _swFrame->color_range = decoded->color_range;
        _swFrame->colorspace = decoded->colorspace;
        _swFrame->color_primaries = decoded->color_primaries;
        _swFrame->color_trc = decoded->color_trc;
        ready = _swFrame;
        return true;
    }

    /// <summary>
    /// Drops the hardware codec, reopens it in software (multi-threaded) and resumes at the
    /// next picture after the last one queued, or at the seek target when none was queued yet.
    /// Runs at most once per open, because it clears <c>_usingHardware</c>.
    /// Caller holds <c>_decodeLock</c>.
    /// </summary>
    /// <param name="reason">Short cause for the log line.</param>
    private unsafe void FallbackToSoftwareUnlocked(string reason)
    {
        if (!_usingHardware || _formatCtx == null || _videoStreamIndex < 0)
            return;
        _usingHardware = false;
        long frameUs = Info?.FrameDurationUs ?? 33_333;
        long resumeUs = Math.Max(0, _lastDecodedPtsUs);
        // Skip pictures already queued so none is shown twice.
        long discardToUs = _queuedSinceReset ? resumeUs + frameUs : resumeUs;
        AVStream* stream = _formatCtx->streams[(uint)_videoStreamIndex];
        AVCodec* codec = ffmpeg.avcodec_find_decoder(stream->codecpar->codec_id);
        if (codec == null)
        {
            SetEndOfStreamUnderDecode();
            return;
        }
        GD.PrintErr($"VideoSourceDecoder:Decode - switching this file to software decode ({reason}).");
        try
        {
            OpenCodecUnlocked(codec, stream, allowHardware: false);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"VideoSourceDecoder:Decode - software reopen failed: {ex.Message}");
            SetEndOfStreamUnderDecode();
            return;
        }
        // Always seek: the packets already sent to the GPU decoder, including the keyframe, are gone.
        SeekDemuxUnlocked(resumeUs);
        ResetDropCatchupUnlocked();
        DiscardUntilUnlocked(discardToUs);
    }

    /// <summary>Seeks the demuxer. Caller holds <c>_decodeLock</c>. Does not touch the frame ring.</summary>
    private unsafe void SeekDemuxUnlocked(long timestampUs)
    {
        if (_formatCtx == null || _codecCtx == null)
            return;
        long seekTs = ffmpeg.av_rescale_q(
            timestampUs,
            new AVRational { num = 1, den = ffmpeg.AV_TIME_BASE },
            _timeBase);
        if (_startTimeStream != 0 && _startTimeStream != ffmpeg.AV_NOPTS_VALUE)
            seekTs += _startTimeStream;
        int ret = ffmpeg.avformat_seek_file(_formatCtx, _videoStreamIndex, long.MinValue, seekTs, seekTs, 0);
        if (ret < 0)
            ret = ffmpeg.av_seek_frame(_formatCtx, _videoStreamIndex, seekTs, ffmpeg.AVSEEK_FLAG_BACKWARD);
        if (ret < 0)
            return;
        ffmpeg.avformat_flush(_formatCtx);
        ffmpeg.avcodec_flush_buffers(_codecCtx);
    }

    private unsafe void FreeCodecUnlocked()
    {
        if (_codecCtx != null)
        {
            fixed (AVCodecContext** pp = &_codecCtx)
                ffmpeg.avcodec_free_context(pp);
            _codecCtx = null;
        }
        _getFormatDelegate = null;
        _hwState = null;
        _hwPixelFormat = AVPixelFormat.AV_PIX_FMT_NONE;
        _usingHardware = false;
    }

    /// <summary>Per-decoder hardware format choice, reached from <c>AVCodecContext.opaque</c>.</summary>
    private sealed class HwFormatState
    {
        public AVPixelFormat HwFormat;
        public string Name;
        public bool Rejected;
    }

    private static unsafe void CopyPlane(byte* src, int srcStride, byte[] dst, int width, int height)
    {
        if (src == null || srcStride <= 0)
            return;
        if (srcStride == width)
        {
            Marshal.Copy((IntPtr)src, dst, 0, width * height);
            return;
        }
        for (int y = 0; y < height; y++)
            Marshal.Copy((IntPtr)(src + y * srcStride), dst, y * width, width);
    }

    private static unsafe void CopyChromaInterleaved(
        byte* uPlane, int uStride, byte* vPlane, int vStride, byte[] dst, int width, int height)
    {
        fixed (byte* pinned = dst)
        {
            for (int y = 0; y < height; y++)
            {
                byte* u = uPlane + y * uStride;
                byte* v = vPlane + y * vStride;
                byte* row = pinned + y * width * 2;
                for (int x = 0; x < width; x++)
                {
                    row[x * 2] = u[x];
                    row[x * 2 + 1] = v[x];
                }
            }
        }
    }

    private static bool Is8Bit420(AVPixelFormat fmt) =>
        fmt == AVPixelFormat.AV_PIX_FMT_YUV420P || fmt == AVPixelFormat.AV_PIX_FMT_YUVJ420P;

    private static int MatrixIdFor(int swsCs)
    {
        if (swsCs == ffmpeg.SWS_CS_BT2020)
            return 2;
        if (swsCs == ffmpeg.SWS_CS_ITU709)
            return 1;
        return 0;
    }

    /// <summary>
    /// Builds or rebuilds the swscale context from the decoded frame's format.
    /// Caller holds <c>_decodeLock</c>.
    /// </summary>
    private unsafe bool EnsureScaleContext(AVFrame* src)
    {
        int width = src->width;
        int height = src->height;
        var fmt = (AVPixelFormat)src->format;
        if (width <= 0 || height <= 0)
            return false;

        int dstW = _outputWidth > 0 ? _outputWidth : width;
        int dstH = _outputHeight > 0 ? _outputHeight : height;
        if (_swsCtx != null
            && _swsSrcW == width && _swsSrcH == height
            && _swsDstW == dstW && _swsDstH == dstH
            && _swsSrcFmt == fmt)
            return true;

        if (_swsCtx != null)
        {
            ffmpeg.sws_freeContext(_swsCtx);
            _swsCtx = null;
        }

        int threads = Math.Clamp(System.Environment.ProcessorCount / 4, 1, 4);
        int srcRange = IsFullRange(src, fmt) ? 1 : 0;
        int swsCs = SwsColorspaceFor(src->colorspace, height);
        string backend = "auto";
        SwsContext* ctx = CreateScaleContext(width, height, dstW, dstH, fmt, threads, srcRange, backends: (SwsBackend)0);
        int details = ctx != null ? ApplyColorspaceDetails(ctx, swsCs, srcRange) : -1;
        if (ctx == null || details < 0)
        {
            // The default scaler can reject the colour-matrix call. Rebuild on the legacy
            // backend, which still honours sws_setColorspaceDetails.
            if (ctx != null)
                ffmpeg.sws_freeContext(ctx);
            backend = "legacy";
            ctx = CreateScaleContext(
                width, height, dstW, dstH, fmt, threads, srcRange, SwsBackend.SWS_BACKEND_LEGACY);
            if (ctx == null)
            {
                SetEndOfStreamUnderDecode();
                return false;
            }
            details = ApplyColorspaceDetails(ctx, swsCs, srcRange);
        }

        _swsCtx = ctx;
        _swsSrcW = width;
        _swsSrcH = height;
        _swsDstW = dstW;
        _swsDstH = dstH;
        _swsSrcFmt = fmt;
        _sourceWidth = width;
        _sourceHeight = height;

        int byteSize = dstW * dstH * RgbaChannels;
        if (byteSize != _rgbaBufferSize)
        {
            lock (_ringLock)
            {
                _rgbaBufferSize = byteSize;
                PurgePoolUnlocked();
            }
        }
        if (Info != null && (Info.Width != width || Info.Height != height))
        {
            Info = new VideoSourceInfo
            {
                Width = width,
                Height = height,
                Fps = Info.Fps,
                FrameDurationUs = Info.FrameDurationUs,
                DurationUs = Info.DurationUs,
                CodecName = Info.CodecName,
                FilePath = Info.FilePath,
                FrameByteSize = width * height * RgbaChannels
            };
        }

        string dstNote = (dstW == width && dstH == height) ? "" : $" -> {dstW}x{dstH}";
        GD.Print(
            $"VideoSourceDecoder:Scale - {width}x{height}{dstNote} src={PixFmtName(fmt)} swsThreads={threads} " +
            $"backend={backend} matrix={SwsColorspaceName(swsCs)} colorspaceRet={details} " +
            $"srcRange={(srcRange == 1 ? "full" : "limited")} dstRange=full");
        return true;
    }

    /// <summary>
    /// Allocates and initialises a scaler. Caller holds <c>_decodeLock</c>.
    /// <paramref name="backends"/> 0 leaves the scaler's default backend set.
    /// </summary>
    private unsafe SwsContext* CreateScaleContext(
        int srcW, int srcH, int dstW, int dstH, AVPixelFormat fmt, int threads, int srcRange, SwsBackend backends)
    {
        SwsContext* ctx = ffmpeg.sws_alloc_context();
        if (ctx == null)
        {
            GD.PrintErr($"VideoSourceDecoder:Scale - sws_alloc_context failed for {srcW}x{srcH}.");
            return null;
        }

        ctx->flags = SwsBilinear;
        ctx->threads = threads;
        ctx->src_w = srcW;
        ctx->src_h = srcH;
        ctx->dst_w = dstW;
        ctx->dst_h = dstH;
        ctx->src_format = (int)fmt;
        ctx->dst_format = (int)AVPixelFormat.AV_PIX_FMT_RGBA;
        ctx->src_range = srcRange;
        ctx->dst_range = 1;
        if (backends != (SwsBackend)0)
            ctx->backends = backends;

        int init = ffmpeg.sws_init_context(ctx, null, null);
        if (init < 0)
        {
            ffmpeg.sws_freeContext(ctx);
            GD.PrintErr(
                $"VideoSourceDecoder:Scale - sws_init_context failed ({backends}): {MediaEngine.GetFFmpegError(init)}");
            return null;
        }

        return ctx;
    }

    /// <summary>
    /// Applies the YUV matrix. RGB sources ignore it. Returns the FFmpeg result, or -1
    /// when the coefficient table cannot be read.
    /// </summary>
    private unsafe int ApplyColorspaceDetails(SwsContext* ctx, int swsCs, int srcRange)
    {
        if (!TryCopyCoefficients(swsCs, out int_array4 srcTable))
            return -1;
        int_array4 dstTable = srcTable;
        return ffmpeg.sws_setColorspaceDetails(
            ctx, in srcTable, srcRange, in dstTable, 1, 0, 1 << 16, 1 << 16);
    }

    private static unsafe bool TryCopyCoefficients(int swsCs, out int_array4 table)
    {
        table = default;
        int* coeff = ffmpeg.sws_getCoefficients(swsCs);
        if (coeff == null)
            return false;
        table[0] = coeff[0];
        table[1] = coeff[1];
        table[2] = coeff[2];
        table[3] = coeff[3];
        return true;
    }

    private static int SwsColorspaceFor(AVColorSpace space, int height)
    {
        switch (space)
        {
            case AVColorSpace.AVCOL_SPC_BT709:
                return ffmpeg.SWS_CS_ITU709;
            case AVColorSpace.AVCOL_SPC_BT2020_NCL:
            case AVColorSpace.AVCOL_SPC_BT2020_CL:
                return ffmpeg.SWS_CS_BT2020;
            case AVColorSpace.AVCOL_SPC_SMPTE240M:
                return ffmpeg.SWS_CS_SMPTE240M;
            case AVColorSpace.AVCOL_SPC_FCC:
                return ffmpeg.SWS_CS_FCC;
            case AVColorSpace.AVCOL_SPC_BT470BG:
            case AVColorSpace.AVCOL_SPC_SMPTE170M:
                return ffmpeg.SWS_CS_ITU601;
            default:
                // Unspecified, RGB, and anything else: HD uses BT.709, SD uses BT.601.
                return height >= 720 ? ffmpeg.SWS_CS_ITU709 : ffmpeg.SWS_CS_ITU601;
        }
    }

    private static string SwsColorspaceName(int swsCs)
    {
        if (swsCs == ffmpeg.SWS_CS_ITU709)
            return "bt709";
        if (swsCs == ffmpeg.SWS_CS_BT2020)
            return "bt2020";
        if (swsCs == ffmpeg.SWS_CS_SMPTE240M)
            return "smpte240m";
        if (swsCs == ffmpeg.SWS_CS_FCC)
            return "fcc";
        return "bt601";
    }

    private static unsafe bool IsFullRange(AVFrame* src, AVPixelFormat fmt)
    {
        if (fmt == AVPixelFormat.AV_PIX_FMT_YUVJ420P
            || fmt == AVPixelFormat.AV_PIX_FMT_YUVJ422P
            || fmt == AVPixelFormat.AV_PIX_FMT_YUVJ444P
            || fmt == AVPixelFormat.AV_PIX_FMT_YUVJ440P
            || fmt == AVPixelFormat.AV_PIX_FMT_YUVJ411P)
            return true;
        if (src->color_range == AVColorRange.AVCOL_RANGE_JPEG)
            return true;
        if (src->color_range == AVColorRange.AVCOL_RANGE_MPEG)
            return false;
        return IsRgbFormat(fmt);
    }

    private static unsafe bool IsRgbFormat(AVPixelFormat fmt)
    {
        AVPixFmtDescriptor* desc = ffmpeg.av_pix_fmt_desc_get(fmt);
        if (desc == null)
            return false;
        return (desc->flags & ffmpeg.AV_PIX_FMT_FLAG_RGB) != 0;
    }

    private static string PixFmtName(AVPixelFormat fmt)
    {
        string name = ffmpeg.av_get_pix_fmt_name(fmt);
        return string.IsNullOrEmpty(name) ? "unknown" : name;
    }

    private static string ColorName(string name) =>
        string.IsNullOrEmpty(name) ? "unknown" : name;

    private static unsafe string Utf8Or(byte* text, string fallback)
    {
        if (text == null)
            return string.IsNullOrEmpty(fallback) ? "unknown" : fallback;
        return Marshal.PtrToStringUTF8((IntPtr)text) ?? (string.IsNullOrEmpty(fallback) ? "unknown" : fallback);
    }

    private unsafe long FramePtsUsUnlocked(AVFrame* frame)
    {
        long pts = frame->best_effort_timestamp;
        if (pts == ffmpeg.AV_NOPTS_VALUE)
            pts = frame->pts;
        if (pts == ffmpeg.AV_NOPTS_VALUE)
            pts = frame->pkt_dts;

        if (pts == ffmpeg.AV_NOPTS_VALUE)
        {
            // Advance by one frame from the last decoded frame (decode lock state).
            return _lastDecodedPtsUs + (Info?.FrameDurationUs ?? 33_333);
        }

        return PacketPtsToUsUnlocked(pts);
    }

    private long PacketPtsToUsUnlocked(long pts)
    {
        long adj = pts;
        if (_startTimeStream != 0 && _startTimeStream != ffmpeg.AV_NOPTS_VALUE)
            adj = pts - _startTimeStream;
        long us = ffmpeg.av_rescale_q(adj, _timeBase, new AVRational { num = 1, den = ffmpeg.AV_TIME_BASE });
        return us < 0 ? 0 : us;
    }

    private unsafe void CloseInternal()
    {
        _isOpen = false;
        _endOfStream = true;
        OutputsPlanar420 = false;
        _sourceWidth = 0;
        _sourceHeight = 0;
        _outputWidth = 0;
        _outputHeight = 0;
        Info = null;

        long released = 0;
        while (_ready.Count > 0)
        {
            var f = _ready.Dequeue();
            if (f == null) continue;
            released += MediaMemory.ByteBufferBytes(f.Rgba);
            released += MediaMemory.ByteBufferBytes(f.Y);
            released += MediaMemory.ByteBufferBytes(f.Uv);
            f.Rgba = null;
            f.Y = null;
            f.Uv = null;
            f.DisposePlaneImages();
        }
        while (_bufferPool.Count > 0)
        {
            var buf = _bufferPool.Pop();
            released += MediaMemory.ByteBufferBytes(buf);
        }
        if (released > 0)
            MediaMemory.NoteReleased(released);

        if (_swsCtx != null)
        {
            ffmpeg.sws_freeContext(_swsCtx);
            _swsCtx = null;
        }
        _rgbaBufferSize = 0;
        _yBufferSize = 0;
        _uvBufferSize = 0;
        _swsSrcW = 0;
        _swsSrcH = 0;
        _swsDstW = 0;
        _swsDstH = 0;
        _swsSrcFmt = AVPixelFormat.AV_PIX_FMT_NONE;
        if (_frame != null)
        {
            fixed (AVFrame** pp = &_frame) ffmpeg.av_frame_free(pp);
            _frame = null;
        }
        if (_packet != null)
        {
            fixed (AVPacket** pp = &_packet) ffmpeg.av_packet_free(pp);
            _packet = null;
        }
        if (_swFrame != null)
        {
            fixed (AVFrame** pp = &_swFrame) ffmpeg.av_frame_free(pp);
            _swFrame = null;
        }
        FreeCodecUnlocked();
        if (_formatCtx != null)
        {
            fixed (AVFormatContext** pp = &_formatCtx) ffmpeg.avformat_close_input(pp);
            _formatCtx = null;
        }
    }
}
