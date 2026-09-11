// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Cue2.Media.Audio;
using FFmpeg.AutoGen;
using Godot;

namespace Cue2.Media.Decoders;

/// <summary>
/// Pull-based FFmpeg video source. Demuxes, decodes, and converts frames to RGBA8.
/// Manages a bounded ring of decoded frames. Does not own transport control
/// (play/pause/loop) or wall-clock pacing — the software layer presents frames
/// according to a master clock (typically audio or wall time).
/// </summary>
public sealed class VideoSourceDecoder : IDisposable
{
    private const int SwsBilinear = 2;
    private const int RgbaChannels = 4;
    private const int DefaultRingFrames = 8;
    private const double FallbackFps = 30.0;

    private readonly object _lock = new object();
    private unsafe AVFormatContext* _formatCtx;
    private unsafe AVCodecContext* _codecCtx;
    private unsafe SwsContext* _swsCtx;
    private unsafe AVPacket* _packet;
    private unsafe AVFrame* _frame;
    private unsafe AVFrame* _rgbaFrame;
    private unsafe byte* _rgbaBuffer;
    private int _rgbaBufferSize;
    private int _videoStreamIndex = -1;
    private AVRational _timeBase;
    private long _startTimeStream;
    private bool _endOfStream;
    private bool _isDisposed;
    private bool _isOpen;

    // Ring of decoded RGBA frames ready for pull
    private readonly Queue<VideoFrame> _ready = new Queue<VideoFrame>();
    private readonly Stack<byte[]> _bufferPool = new Stack<byte[]>();
    private int _ringCapacity = DefaultRingFrames;
    private long _lastPtsUs;
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
            lock (_lock)
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
            lock (_lock) return _endOfStream && _ready.Count == 0;
        }
    }

    /// <summary>Decoded frames currently buffered.</summary>
    public int BufferedFrames
    {
        get
        {
            lock (_lock) return _ready.Count;
        }
    }

    /// <summary>Opens a video file for pull-based frame reads.</summary>
    public Task OpenAsync(string path, int ringFrames = DefaultRingFrames)
    {
        if (string.IsNullOrEmpty(path)) throw new ArgumentNullException(nameof(path));
        if (!File.Exists(path)) throw new FileNotFoundException("Video file not found.", path);

        return Task.Run(() =>
        {
            lock (_lock)
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
        });
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
        lock (_lock)
        {
            if (_isDisposed || !_isOpen || Info == null) return false;

            while (_ready.Count == 0)
            {
                ct.ThrowIfCancellationRequested();
                if (_endOfStream) return false;
                if (!DecodeMoreUnlocked()) return false;
            }

            frame = _ready.Dequeue();
            _lastPtsUs = frame.PtsUs;
            return true;
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
        lock (_lock)
        {
            if (_isDisposed || !_isOpen || _ready.Count == 0) return false;
            frame = _ready.Dequeue();
            _lastPtsUs = frame.PtsUs;
            return true;
        }
    }

    /// <summary>
    /// Peeks next frame PTS without consuming, decoding if needed.
    /// Prefer <see cref="TryPeekPtsBuffered"/> on the present/UI path.
    /// </summary>
    public bool TryPeekPts(out long ptsUs)
    {
        ptsUs = 0;
        lock (_lock)
        {
            if (_ready.Count == 0)
            {
                if (_endOfStream || Info == null) return false;
                if (!DecodeMoreUnlocked()) return false;
                if (_ready.Count == 0) return false;
            }
            ptsUs = _ready.Peek().PtsUs;
            return true;
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
        lock (_lock)
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
    public void Prefetch(int targetFrames = DefaultRingFrames / 2)
    {
        lock (_lock)
        {
            if (!_isOpen || Info == null || _endOfStream) return;
            targetFrames = Math.Clamp(targetFrames, 1, _ringCapacity);
            while (_ready.Count < targetFrames && !_endOfStream)
            {
                if (!DecodeMoreUnlocked()) break;
            }
        }
    }

    /// <summary>
    /// Seeks to <paramref name="timestampUs"/> microseconds. Keyframe seek + decode-discard to target.
    /// Blocks while discarding to the target — call from a worker (see <see cref="SeekAsync"/>).
    /// </summary>
    public unsafe void Seek(long timestampUs)
    {
        lock (_lock)
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

            // Return ready frames to pool
            while (_ready.Count > 0)
            {
                var f = _ready.Dequeue();
                if (f.Rgba != null) _bufferPool.Push(f.Rgba);
            }

            _endOfStream = false;
            _lastPtsUs = timestampUs;

            // Discard until first frame with PTS >= target
            DiscardUntilUnlocked(timestampUs);
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
        lock (_lock)
        {
            while (_ready.Count > 0)
            {
                var f = _ready.Dequeue();
                if (f.Rgba != null) _bufferPool.Push(f.Rgba);
            }
        }
    }

    /// <summary>
    /// Returns a frame's pixel buffer to the pool after the caller has finished with it
    /// (e.g. after copying into a display buffer).
    /// </summary>
    public void ReleaseFrameBuffer(byte[] rgba)
    {
        if (rgba == null) return;
        lock (_lock)
        {
            if (_rgbaBufferSize > 0 && rgba.Length >= _rgbaBufferSize)
                _bufferPool.Push(rgba);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_lock)
        {
            if (_isDisposed) return;
            CloseInternal();
            _isDisposed = true;
        }
    }

    private unsafe void OpenInternal(string path, int ringFrames)
    {
        _ringCapacity = Math.Clamp(ringFrames, 2, 32);
        int ret;

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

        ret = ffmpeg.avformat_find_stream_info(_formatCtx, null);
        if (ret < 0)
            throw new Exception($"VideoSourceDecoder:Open - find_stream_info failed: {MediaEngine.GetFFmpegError(ret)}");

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

        fixed (AVCodecContext** pCodec = &_codecCtx)
        {
            _codecCtx = ffmpeg.avcodec_alloc_context3(codec);
            _codecCtx->thread_count = Math.Max(1, System.Environment.ProcessorCount / 2);
            ret = ffmpeg.avcodec_parameters_to_context(_codecCtx, stream->codecpar);
            if (ret < 0)
                throw new Exception($"VideoSourceDecoder:Open - params_to_context failed: {MediaEngine.GetFFmpegError(ret)}");

            _codecCtx->pkt_timebase = _timeBase;
            ret = ffmpeg.avcodec_open2(_codecCtx, codec, null);
            if (ret < 0)
                throw new Exception($"VideoSourceDecoder:Open - codec open failed: {MediaEngine.GetFFmpegError(ret)}");
        }

        int width = _codecCtx->width;
        int height = _codecCtx->height;
        if (width <= 0 || height <= 0)
            throw new Exception("VideoSourceDecoder:Open - Invalid dimensions.");

        _swsCtx = ffmpeg.sws_getContext(
            width, height, _codecCtx->pix_fmt,
            width, height, AVPixelFormat.AV_PIX_FMT_RGBA,
            SwsBilinear, null, null, null);
        if (_swsCtx == null)
            throw new Exception("VideoSourceDecoder:Open - sws_getContext failed.");

        _rgbaBufferSize = width * height * RgbaChannels;
        _rgbaBuffer = (byte*)ffmpeg.av_malloc((ulong)_rgbaBufferSize);
        _rgbaFrame = ffmpeg.av_frame_alloc();
        _frame = ffmpeg.av_frame_alloc();
        _packet = ffmpeg.av_packet_alloc();
        if (_rgbaBuffer == null || _rgbaFrame == null || _frame == null || _packet == null)
            throw new Exception("VideoSourceDecoder:Open - frame/packet alloc failed.");

        _rgbaFrame->data[0] = _rgbaBuffer;
        _rgbaFrame->linesize[0] = width * RgbaChannels;
        _rgbaFrame->format = (int)AVPixelFormat.AV_PIX_FMT_RGBA;
        _rgbaFrame->width = width;
        _rgbaFrame->height = height;

        // Pre-allocate buffer pool
        for (int i = 0; i < _ringCapacity + 2; i++)
            _bufferPool.Push(new byte[_rgbaBufferSize]);

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
        _endOfStream = false;
        _isOpen = true;

        GD.Print($"VideoSourceDecoder:Open - {path} {width}x{height} @{_fps:F2}fps codec={codecName} durationUs={durationUs}");
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
            if (ret >= 0)
            {
                long ptsUs = FramePtsUsUnlocked(_frame);
                if (ptsUs + Info.FrameDurationUs / 2 < targetUs)
                {
                    ffmpeg.av_frame_unref(_frame);
                    continue; // still before target
                }
                // Keep this frame in ring
                EnqueueConvertedUnlocked(_frame, ptsUs);
                ffmpeg.av_frame_unref(_frame);
                return;
            }
            if (ret != ffmpeg.AVERROR(ffmpeg.EAGAIN) && ret != ffmpeg.AVERROR_EOF)
            {
                GD.PrintErr($"VideoSourceDecoder:DiscardUntil - receive: {MediaEngine.GetFFmpegError(ret)}");
                return;
            }
            if (ret == ffmpeg.AVERROR_EOF)
            {
                _endOfStream = true;
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
                _endOfStream = true;
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
                continue;
        }
    }

    private unsafe bool DecodeMoreUnlocked()
    {
        if (_endOfStream) return false;
        if (_ready.Count >= _ringCapacity) return true;

        int iterations = 0;
        const int maxIterations = 128;
        int before = _ready.Count;

        while (iterations++ < maxIterations && _ready.Count < _ringCapacity)
        {
            int ret = ffmpeg.avcodec_receive_frame(_codecCtx, _frame);
            if (ret >= 0)
            {
                long ptsUs = FramePtsUsUnlocked(_frame);
                EnqueueConvertedUnlocked(_frame, ptsUs);
                ffmpeg.av_frame_unref(_frame);
                if (_ready.Count > before) return true;
                continue;
            }
            if (ret != ffmpeg.AVERROR(ffmpeg.EAGAIN) && ret != ffmpeg.AVERROR_EOF)
            {
                GD.PrintErr($"VideoSourceDecoder:DecodeMore - receive: {MediaEngine.GetFFmpegError(ret)}");
                _endOfStream = true;
                return false;
            }
            if (ret == ffmpeg.AVERROR_EOF)
            {
                _endOfStream = true;
                return _ready.Count > before;
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
                _endOfStream = true;
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
                continue;
        }

        return _ready.Count > before;
    }

    private unsafe void EnqueueConvertedUnlocked(AVFrame* src, long ptsUs)
    {
        byte*[] srcSlice = { src->data[0], src->data[1], src->data[2], src->data[3] };
        int[] srcStride = { src->linesize[0], src->linesize[1], src->linesize[2], src->linesize[3] };
        byte*[] dstSlice = { _rgbaFrame->data[0], null, null, null };
        int[] dstStride = { _rgbaFrame->linesize[0], 0, 0, 0 };

        ffmpeg.sws_scale(_swsCtx, srcSlice, srcStride, 0, Info.Height, dstSlice, dstStride);

        byte[] buf = _bufferPool.Count > 0 ? _bufferPool.Pop() : new byte[_rgbaBufferSize];
        if (buf.Length < _rgbaBufferSize)
            buf = new byte[_rgbaBufferSize];

        Marshal.Copy((IntPtr)_rgbaBuffer, buf, 0, _rgbaBufferSize);

        _ready.Enqueue(new VideoFrame
        {
            Rgba = buf,
            PtsUs = ptsUs,
            Width = Info.Width,
            Height = Info.Height
        });
        _lastPtsUs = ptsUs;
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
            // Advance by one frame from last known
            return _lastPtsUs + (Info?.FrameDurationUs ?? 33_333);
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
        Info = null;

        long released = 0;
        while (_ready.Count > 0)
        {
            var f = _ready.Dequeue();
            if (f?.Rgba != null)
            {
                released += MediaMemory.ByteBufferBytes(f.Rgba);
                f.Rgba = null;
            }
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
        if (_rgbaBuffer != null)
        {
            ffmpeg.av_free(_rgbaBuffer);
            _rgbaBuffer = null;
        }
        _rgbaBufferSize = 0;
        if (_rgbaFrame != null)
        {
            fixed (AVFrame** pp = &_rgbaFrame) ffmpeg.av_frame_free(pp);
            _rgbaFrame = null;
        }
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
        if (_codecCtx != null)
        {
            fixed (AVCodecContext** pp = &_codecCtx) ffmpeg.avcodec_free_context(pp);
            _codecCtx = null;
        }
        if (_formatCtx != null)
        {
            fixed (AVFormatContext** pp = &_formatCtx) ffmpeg.avformat_close_input(pp);
            _formatCtx = null;
        }
    }
}
