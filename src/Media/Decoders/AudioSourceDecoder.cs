// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Cue2.Media.Audio;
using FFmpeg.AutoGen;
using Godot;

namespace Cue2.Media.Decoders;

/// <summary>
/// Pull-based FFmpeg audio source. Demuxes, decodes, and converts any supported format
/// to interleaved float32 PCM at the source sample rate and channel count.
/// <para>
/// Frame-based codecs (MP3, AAC, etc.) are fully decoded into an in-memory float PCM
/// store so seek and loop are sample-accurate. Streaming codecs with reliable sample
/// timestamps (PCM, FLAC, …) use a bounded ring buffer and demuxer seek.
/// </para>
/// <para>
/// All public access is serialized on <c>_lock</c>. The streaming
/// <see cref="PcmRingBuffer"/> is only touched under that lock (no nested ring lock).
/// </para>
/// </summary>
public sealed class AudioSourceDecoder : IDisposable
{
    /// <summary>
    /// Default ring capacity in milliseconds of audio (streaming mode).
    /// Prefer callers pass a ring sized for their <c>AudioPresentTuning.RecommendedRingMs</c>
    /// so prefetch targets up to PreferStability (1400 ms) fit without overflow.
    /// </summary>
    public const int DefaultRingMs = 400;

    /// <summary>Hard upper bound for streaming ring capacity (milliseconds).</summary>
    public const int MaxRingMs = 10_000;

    /// <summary>Default prefetch target in milliseconds (streaming mode).</summary>
    public const int DefaultPrefetchMs = 800;

    /// <summary>Refuse full PCM expand above this many bytes (~256 MiB).</summary>
    private const long MaxPcmStoreBytes = 256L * 1024 * 1024;

    /// <summary>
    /// Auto full-decode only for clips at or under this duration (3 minutes).
    /// Longer lossy files stream to avoid multi-hundred-MB LOH allocations.
    /// </summary>
    private const long MaxAutoPcmStoreDurationUs = 180L * 1_000_000;

    private readonly object _lock = new object();
    private bool _preferSampleAccurateStore = true;
    private unsafe AVFormatContext* _formatCtx;
    private unsafe AVCodecContext* _codecCtx;
    private unsafe SwrContext* _swrCtx;
    private AVChannelLayout _inChLayout;
    private AVChannelLayout _outChLayout;
    private unsafe AVPacket* _packet;
    private unsafe AVFrame* _frame;
    private PcmRingBuffer _ring;
    private float[] _convertScratch;
    private unsafe byte* _swrOutBuffer;
    private int _swrOutBufferSamples;
    private int _audioStreamIndex = -1;
    private AVRational _timeBase;
    private long _startTimeStream; // stream start_time in stream time_base units (or 0)
    private long _nextPtsSamples;
    private bool _endOfStream;
    private bool _isDisposed;
    private bool _isOpen;
    private string _filePath = string.Empty;
    private AVCodecID _codecId;

    // Sample-accurate PCM store (frame-based codecs)
    private float[] _pcmStore;
    private int _pcmFrameCount;
    private int _pcmReadFrame;
    private bool _usePcmStore;

    // Streaming seek: discard this many output frames before delivering after a seek
    private long _discardOutputFrames;

    /// <summary>
    /// Stream info after successful <see cref="OpenAsync"/>. Null before open.
    /// </summary>
    public AudioSourceInfo Info { get; private set; }

    /// <summary>
    /// Position of the next sample that will be delivered, in microseconds.
    /// </summary>
    public long PositionUs
    {
        get
        {
            lock (_lock)
            {
                if (Info == null || Info.SampleRate <= 0) return 0;
                long samples = _usePcmStore ? _pcmReadFrame : _nextPtsSamples;
                return (long)(samples * 1_000_000.0 / Info.SampleRate);
            }
        }
    }

    /// <summary>
    /// True when no more samples can be produced (EOF or error).
    /// </summary>
    public bool EndOfStream
    {
        get
        {
            lock (_lock)
            {
                if (_usePcmStore)
                    return _pcmReadFrame >= _pcmFrameCount;
                return _endOfStream && (_ring == null || _ring.Available == 0);
            }
        }
    }

    /// <summary>
    /// Frames currently buffered (ring) or remaining in the PCM store.
    /// </summary>
    public int BufferedFrames
    {
        get
        {
            lock (_lock)
            {
                if (Info == null || Info.Channels <= 0) return 0;
                if (_usePcmStore)
                    return Math.Max(0, _pcmFrameCount - _pcmReadFrame);
                if (_ring == null) return 0;
                return _ring.Available / Info.Channels;
            }
        }
    }

    /// <summary>
    /// Opens an audio file and prepares the decoder for pull-based PCM reads.
    /// </summary>
    /// <param name="path">Media path.</param>
    /// <param name="streamIndex">Audio stream index, or -1 for first audio stream.</param>
    /// <param name="ringMs">Streaming ring size in ms.</param>
    /// <param name="preferSampleAccurateStore">
    /// When true (default), lossy codecs may be fully expanded to float PCM for sample-accurate
    /// seek/loop (subject to size/duration caps). When false, always stream (preferred for
    /// long video-embedded audio to limit RAM).
    /// </param>
    public Task OpenAsync(string path, int streamIndex = -1, int ringMs = DefaultRingMs, bool preferSampleAccurateStore = true)
    {
        if (string.IsNullOrEmpty(path)) throw new ArgumentNullException(nameof(path));
        if (!File.Exists(path)) throw new FileNotFoundException("Audio file not found.", path);

        return Task.Run(() =>
        {
            lock (_lock)
            {
                if (_isDisposed) throw new ObjectDisposedException(nameof(AudioSourceDecoder));
                CloseInternal();
                _preferSampleAccurateStore = preferSampleAccurateStore;
                try
                {
                    OpenInternal(path, streamIndex, ringMs);
                }
                catch
                {
                    // Partial native allocs (format/codec/swr/packet/frame) must not stick around.
                    try { CloseInternal(); } catch { /* ignore secondary cleanup errors */ }
                    throw;
                }
            }
        });
    }

    /// <summary>
    /// Pulls interleaved float32 PCM into <paramref name="destination"/>.
    /// </summary>
    public int Read(Span<float> destination, int frameCount, CancellationToken ct = default)
    {
        if (frameCount <= 0) return 0;

        lock (_lock)
        {
            if (_isDisposed || !_isOpen || Info == null) return 0;

            if (_usePcmStore)
                return ReadFromPcmStoreUnlocked(destination, frameCount);

            return ReadFromStreamUnlocked(destination, frameCount, ct);
        }
    }

    /// <summary>
    /// Decodes ahead into the ring (streaming mode only). No-op for PCM store.
    /// </summary>
    public void Prefetch(int targetMs = DefaultPrefetchMs)
    {
        lock (_lock)
        {
            if (!_isOpen || Info == null || _usePcmStore || _endOfStream) return;
            int targetFrames = (int)(Info.SampleRate * (targetMs / 1000.0));
            int targetSamples = targetFrames * Info.Channels;

            while (_ring.Available < targetSamples && !_endOfStream)
            {
                if (!DecodeMoreUnlocked()) break;
            }
        }
    }

    /// <summary>
    /// Seeks to <paramref name="timestampUs"/> microseconds.
    /// PCM-store mode is sample-exact; streaming mode seeks + discards to target.
    /// </summary>
    public unsafe void Seek(long timestampUs)
    {
        lock (_lock)
        {
            if (!_isOpen || Info == null) return;
            if (timestampUs < 0) timestampUs = 0;

            long targetSamples = (long)(timestampUs / 1_000_000.0 * Info.SampleRate);

            if (_usePcmStore)
            {
                _pcmReadFrame = (int)Math.Clamp(targetSamples, 0, _pcmFrameCount);
                _nextPtsSamples = _pcmReadFrame;
                _endOfStream = _pcmReadFrame >= _pcmFrameCount;
                return;
            }

            // Streaming seek
            if (!SeekDemuxerUnlocked(timestampUs))
                return;

            _ring.Clear();
            _endOfStream = false;
            _discardOutputFrames = 0;
            _nextPtsSamples = targetSamples;

            // Sample-accurate discard after keyframe/frame-boundary seek
            RunSeekTrimUnlocked(targetSamples);
        }
    }

    /// <summary>
    /// Clears the PCM ring without seeking (streaming mode).
    /// </summary>
    public void FlushBuffers()
    {
        lock (_lock)
        {
            _ring?.Clear();
            _discardOutputFrames = 0;
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

    private int ReadFromPcmStoreUnlocked(Span<float> destination, int frameCount)
    {
        int channels = Info.Channels;
        int maxFrames = destination.Length / channels;
        int framesWanted = Math.Min(frameCount, maxFrames);
        int remaining = _pcmFrameCount - _pcmReadFrame;
        if (remaining <= 0) return 0;

        int frames = Math.Min(framesWanted, remaining);
        int samples = frames * channels;
        int srcOffset = _pcmReadFrame * channels;
        _pcmStore.AsSpan(srcOffset, samples).CopyTo(destination.Slice(0, samples));
        _pcmReadFrame += frames;
        _nextPtsSamples = _pcmReadFrame;
        return frames;
    }

    private int ReadFromStreamUnlocked(Span<float> destination, int frameCount, CancellationToken ct)
    {
        int channels = Info.Channels;
        int maxFrames = destination.Length / channels;
        int framesWanted = Math.Min(frameCount, maxFrames);
        int framesWritten = 0;

        while (framesWritten < framesWanted)
        {
            ct.ThrowIfCancellationRequested();

            int samplesNeeded = (framesWanted - framesWritten) * channels;
            int available = _ring.Available;

            if (available == 0)
            {
                if (_endOfStream) break;
                if (!DecodeMoreUnlocked()) break;
                continue;
            }

            int samplesToRead = Math.Min(samplesNeeded, available);
            samplesToRead -= samplesToRead % channels;
            if (samplesToRead <= 0)
            {
                if (_endOfStream) break;
                if (!DecodeMoreUnlocked()) break;
                continue;
            }

            Span<float> destSlice = destination.Slice(framesWritten * channels, samplesToRead);
            int read = _ring.Read(destSlice);
            int frames = read / channels;
            framesWritten += frames;
            _nextPtsSamples += frames;
        }

        return framesWritten;
    }

    private unsafe void OpenInternal(string path, int streamIndex, int ringMs)
    {
        _filePath = path;
        int ret;

        fixed (AVFormatContext** pCtx = &_formatCtx)
        {
            ret = ffmpeg.avformat_open_input(pCtx, path, null, null);
            if (ret < 0)
                throw new Exception($"AudioSourceDecoder:Open - open_input failed: {MediaEngine.GetFFmpegError(ret)}");
        }

        // Generate missing PTS where possible (helps some containers)
        _formatCtx->flags |= ffmpeg.AVFMT_FLAG_GENPTS;

        ret = ffmpeg.avformat_find_stream_info(_formatCtx, null);
        if (ret < 0)
            throw new Exception($"AudioSourceDecoder:Open - find_stream_info failed: {MediaEngine.GetFFmpegError(ret)}");

        _audioStreamIndex = streamIndex;
        if (_audioStreamIndex < 0)
        {
            _audioStreamIndex = -1;
            for (uint i = 0; i < _formatCtx->nb_streams; i++)
            {
                if (_formatCtx->streams[i]->codecpar->codec_type == AVMediaType.AVMEDIA_TYPE_AUDIO)
                {
                    _audioStreamIndex = (int)i;
                    break;
                }
            }
        }

        if (_audioStreamIndex < 0)
            throw new Exception("AudioSourceDecoder:Open - No audio stream found.");

        AVStream* stream = _formatCtx->streams[(uint)_audioStreamIndex];
        _timeBase = stream->time_base;
        _startTimeStream = stream->start_time != ffmpeg.AV_NOPTS_VALUE ? stream->start_time : 0;
        _codecId = stream->codecpar->codec_id;

        AVCodec* codec = ffmpeg.avcodec_find_decoder(_codecId);
        if (codec == null)
            throw new Exception($"AudioSourceDecoder:Open - Unsupported codec id {_codecId}.");

        fixed (AVCodecContext** pCodec = &_codecCtx)
        {
            _codecCtx = ffmpeg.avcodec_alloc_context3(codec);
            ret = ffmpeg.avcodec_parameters_to_context(_codecCtx, stream->codecpar);
            if (ret < 0)
                throw new Exception($"AudioSourceDecoder:Open - params_to_context failed: {MediaEngine.GetFFmpegError(ret)}");

            // Request refcounted frames; helps with skip_samples side data
            _codecCtx->pkt_timebase = _timeBase;

            ret = ffmpeg.avcodec_open2(_codecCtx, codec, null);
            if (ret < 0)
                throw new Exception($"AudioSourceDecoder:Open - codec open failed: {MediaEngine.GetFFmpegError(ret)}");
        }

        int channels = _codecCtx->ch_layout.nb_channels;
        if (channels <= 0)
            throw new Exception("AudioSourceDecoder:Open - Invalid channel count.");

        int sampleRate = _codecCtx->sample_rate;
        if (sampleRate <= 0)
            throw new Exception("AudioSourceDecoder:Open - Invalid sample rate.");

        fixed (AVChannelLayout* pIn = &_inChLayout)
        fixed (AVChannelLayout* pOut = &_outChLayout)
        {
            ret = ffmpeg.av_channel_layout_copy(pIn, &_codecCtx->ch_layout);
            if (ret < 0)
                throw new Exception("AudioSourceDecoder:Open - channel layout copy (in) failed.");

            ret = ffmpeg.av_channel_layout_copy(pOut, pIn);
            if (ret < 0)
                throw new Exception("AudioSourceDecoder:Open - channel layout copy (out) failed.");

            fixed (SwrContext** ppSwr = &_swrCtx)
            {
                ret = ffmpeg.swr_alloc_set_opts2(
                    ppSwr,
                    pOut, AVSampleFormat.AV_SAMPLE_FMT_FLT, sampleRate,
                    pIn, _codecCtx->sample_fmt, sampleRate,
                    0, null);
                if (ret < 0 || _swrCtx == null)
                    throw new Exception($"AudioSourceDecoder:Open - swr_alloc failed: {MediaEngine.GetFFmpegError(ret)}");

                ret = ffmpeg.swr_init(_swrCtx);
                if (ret < 0)
                    throw new Exception($"AudioSourceDecoder:Open - swr_init failed: {MediaEngine.GetFFmpegError(ret)}");
            }
        }

        _packet = ffmpeg.av_packet_alloc();
        _frame = ffmpeg.av_frame_alloc();
        if (_packet == null || _frame == null)
            throw new Exception("AudioSourceDecoder:Open - packet/frame alloc failed.");

        int clampedMs = Math.Clamp(ringMs, DefaultRingMs, MaxRingMs);
        int ringSamples = Math.Max(channels * sampleRate * clampedMs / 1000, channels * 1024);
        _ring = new PcmRingBuffer(ringSamples);
        _convertScratch = new float[channels * 8192];
        GD.Print(
            $"AudioSourceDecoder:Open - streaming ring {clampedMs} ms " +
            $"({ringSamples} floats, {channels} ch @ {sampleRate} Hz)");

        long durationUs = 0;
        if (stream->duration > 0 && stream->duration != ffmpeg.AV_NOPTS_VALUE)
        {
            durationUs = ffmpeg.av_rescale_q(stream->duration, _timeBase, new AVRational { num = 1, den = ffmpeg.AV_TIME_BASE });
        }
        else if (_formatCtx->duration > 0 && _formatCtx->duration != ffmpeg.AV_NOPTS_VALUE)
        {
            durationUs = _formatCtx->duration;
        }

        int bytesPerSample = ffmpeg.av_get_bytes_per_sample(_codecCtx->sample_fmt);
        string codecName = ffmpeg.avcodec_get_name(codec->id) ?? "unknown";

        // Sample-accurate float store for any short cue (including WAV/PCM). Streaming
        // demuxer seek is unreliable on some PCM/WAV files and causes "seek to UI time,
        // play last second then end". Lossy codecs need the store for loop/seek accuracy too.
        bool preferStore = _preferSampleAccurateStore && CanAffordPcmStore(durationUs, sampleRate, channels);
        bool builtStore = false;
        if (preferStore)
        {
            builtStore = TryBuildPcmStoreUnlocked(sampleRate, channels, durationUs);
        }

        // Duration from actual PCM if store built
        if (builtStore && _pcmFrameCount > 0)
        {
            durationUs = (long)(_pcmFrameCount * 1_000_000.0 / sampleRate);
        }

        Info = new AudioSourceInfo
        {
            SampleRate = sampleRate,
            Channels = channels,
            DurationUs = durationUs,
            CodecName = codecName,
            BitDepth = bytesPerSample * 8,
            FilePath = path,
            IsSampleAccurateStore = builtStore
        };

        _nextPtsSamples = 0;
        _endOfStream = false;
        _isOpen = true;
        _discardOutputFrames = 0;

        GD.Print($"AudioSourceDecoder:Open - {path} rate={sampleRate} ch={channels} codec={codecName} " +
                 $"durationUs={durationUs} pcmStore={builtStore} frames={_pcmFrameCount}");
    }

    private static bool CanAffordPcmStore(long durationUs, int sampleRate, int channels)
    {
        if (durationUs <= 0)
        {
            // Unknown duration: allow store, hard-capped while building
            return true;
        }

        // Prefer streaming for long clips so we don't park multi-minute float PCM on the LOH
        if (durationUs > MaxAutoPcmStoreDurationUs)
            return false;

        double seconds = durationUs / 1_000_000.0;
        long bytes = (long)(seconds * sampleRate * channels * sizeof(float) * 1.05);
        return bytes > 0 && bytes <= MaxPcmStoreBytes;
    }

    /// <summary>
    /// Fully decode the file into interleaved float PCM for sample-accurate seek/loop.
    /// </summary>
    private unsafe bool TryBuildPcmStoreUnlocked(int sampleRate, int channels, long durationUs)
    {
        try
        {
            // Rewind to beginning
            int ret = ffmpeg.av_seek_frame(_formatCtx, _audioStreamIndex, 0, ffmpeg.AVSEEK_FLAG_BACKWARD);
            if (ret < 0)
            {
                // Try file start via byte seek
                ret = ffmpeg.avformat_seek_file(_formatCtx, _audioStreamIndex, long.MinValue, 0, long.MaxValue, ffmpeg.AVSEEK_FLAG_BYTE);
            }
            ffmpeg.avformat_flush(_formatCtx);
            ffmpeg.avcodec_flush_buffers(_codecCtx);
            ReinitSwrUnlocked(sampleRate);

            int estimateFrames = durationUs > 0
                ? (int)Math.Min(int.MaxValue / 4, (long)(durationUs / 1_000_000.0 * sampleRate) + sampleRate)
                : sampleRate * 60; // 1 minute initial guess

            long maxSamples = MaxPcmStoreBytes / sizeof(float);
            int maxFrames = (int)Math.Min(int.MaxValue / channels, maxSamples / channels);

            var builder = new PcmBuilder(Math.Min(estimateFrames, maxFrames) * channels, channels);

            _endOfStream = false;
            int safety = 0;
            const int maxIters = 5_000_000;

            while (!_endOfStream && safety++ < maxIters)
            {
                ret = ffmpeg.av_read_frame(_formatCtx, _packet);
                if (ret == ffmpeg.AVERROR_EOF)
                {
                    // Flush decoder
                    ffmpeg.avcodec_send_packet(_codecCtx, null);
                    DrainAllFramesToBuilderUnlocked(builder, channels);
                    FlushSwrToBuilderUnlocked(builder, channels);
                    break;
                }
                if (ret < 0)
                {
                    GD.PrintErr($"AudioSourceDecoder:BuildPcmStore - read_frame: {MediaEngine.GetFFmpegError(ret)}");
                    break;
                }

                if (_packet->stream_index != _audioStreamIndex)
                {
                    ffmpeg.av_packet_unref(_packet);
                    continue;
                }

                ret = ffmpeg.avcodec_send_packet(_codecCtx, _packet);
                ffmpeg.av_packet_unref(_packet);
                if (ret < 0 && ret != ffmpeg.AVERROR(ffmpeg.EAGAIN))
                {
                    // Soft-skip bad packets
                    continue;
                }

                DrainAllFramesToBuilderUnlocked(builder, channels);

                if (builder.FrameCount >= maxFrames)
                {
                    GD.PrintErr("AudioSourceDecoder:BuildPcmStore - Hit max PCM size; truncating.");
                    break;
                }
            }

            // Final flush
            ffmpeg.avcodec_send_packet(_codecCtx, null);
            DrainAllFramesToBuilderUnlocked(builder, channels);
            FlushSwrToBuilderUnlocked(builder, channels);

            if (builder.FrameCount <= 0)
            {
                GD.PrintErr("AudioSourceDecoder:BuildPcmStore - No samples decoded; falling back to stream mode.");
                ResetToStreamStartUnlocked(sampleRate);
                return false;
            }

            _pcmStore = builder.ToArray();
            _pcmFrameCount = builder.FrameCount;
            _pcmReadFrame = 0;
            _usePcmStore = true;

            // Release demux/decode native resources — only the float store is needed now
            ReleaseStreamingResourcesUnlocked();

            GD.Print($"AudioSourceDecoder:BuildPcmStore - Stored {_pcmFrameCount} frames " +
                     $"({_pcmStore.Length * sizeof(float) / (1024.0 * 1024.0):F1} MiB)");
            return true;
        }
        catch (Exception ex)
        {
            GD.PrintErr($"AudioSourceDecoder:BuildPcmStore - Failed: {ex.Message}; using stream mode.");
            _pcmStore = null;
            _pcmFrameCount = 0;
            _pcmReadFrame = 0;
            _usePcmStore = false;
            try { ResetToStreamStartUnlocked(sampleRate); } catch { /* best effort */ }
            return false;
        }
    }

    /// <summary>
    /// Rewind demuxer/codec after a failed full-decode attempt so stream mode can start cleanly.
    /// </summary>
    private unsafe void ResetToStreamStartUnlocked(int sampleRate)
    {
        if (_formatCtx == null || _codecCtx == null) return;
        ffmpeg.av_seek_frame(_formatCtx, _audioStreamIndex, 0, ffmpeg.AVSEEK_FLAG_BACKWARD);
        ffmpeg.avformat_flush(_formatCtx);
        ffmpeg.avcodec_flush_buffers(_codecCtx);
        ReinitSwrUnlocked(sampleRate);
        _ring?.Clear();
        _endOfStream = false;
        _nextPtsSamples = 0;
        _discardOutputFrames = 0;
    }

    private unsafe void DrainAllFramesToBuilderUnlocked(PcmBuilder builder, int channels)
    {
        while (true)
        {
            int ret = ffmpeg.avcodec_receive_frame(_codecCtx, _frame);
            if (ret == ffmpeg.AVERROR(ffmpeg.EAGAIN) || ret == ffmpeg.AVERROR_EOF)
                return;
            if (ret < 0)
            {
                GD.PrintErr($"AudioSourceDecoder:DrainAllFrames - receive_frame: {MediaEngine.GetFFmpegError(ret)}");
                return;
            }

            int skip = GetFrameSkipSamplesUnlocked(_frame);
            AppendFrameToBuilderUnlocked(_frame, skip, builder, channels);
            ffmpeg.av_frame_unref(_frame);
        }
    }

    /// <summary>
    /// Reads encoder delay / padding skip from frame side data when present.
    /// </summary>
    private unsafe int GetFrameSkipSamplesUnlocked(AVFrame* frame)
    {
        // AV_FRAME_DATA_SKIP_SAMPLES: u32 skip, u32 discard padding, u8 reasons...
        for (int i = 0; i < frame->nb_side_data; i++)
        {
            AVFrameSideData* sd = frame->side_data[i];
            if (sd->type == AVFrameSideDataType.AV_FRAME_DATA_SKIP_SAMPLES && sd->size >= 10)
            {
                uint skip = *(uint*)sd->data;
                return (int)Math.Min(skip, (uint)frame->nb_samples);
            }
        }
        return 0;
    }

    private unsafe void AppendFrameToBuilderUnlocked(AVFrame* frame, int skipSamples, PcmBuilder builder, int channels)
    {
        if (_swrCtx == null) return;

        long delay = ffmpeg.swr_get_delay(_swrCtx, Info?.SampleRate ?? _codecCtx->sample_rate);
        int rate = Info?.SampleRate ?? _codecCtx->sample_rate;
        int maxOut = (int)ffmpeg.av_rescale_rnd(delay + frame->nb_samples, rate, rate, AVRounding.AV_ROUND_UP) + 256;
        EnsureSwrBuffer(maxOut, channels);

        byte* outPtr = _swrOutBuffer;
        int produced = ffmpeg.swr_convert(
            _swrCtx,
            &outPtr, maxOut,
            frame->extended_data, frame->nb_samples);

        if (produced <= 0) return;

        int skip = Math.Clamp(skipSamples, 0, produced);
        int usable = produced - skip;
        if (usable <= 0) return;

        float* fptr = (float*)_swrOutBuffer + (skip * channels);
        int samples = usable * channels;
        if (samples > _convertScratch.Length)
            _convertScratch = new float[samples];

        Marshal.Copy((IntPtr)fptr, _convertScratch, 0, samples);
        builder.Append(_convertScratch.AsSpan(0, samples));
    }

    private unsafe void FlushSwrToBuilderUnlocked(PcmBuilder builder, int channels)
    {
        if (_swrCtx == null) return;
        int maxOut = 8192;
        EnsureSwrBuffer(maxOut, channels);
        byte* outPtr = _swrOutBuffer;
        int produced = ffmpeg.swr_convert(_swrCtx, &outPtr, maxOut, null, 0);
        if (produced <= 0) return;

        int samples = produced * channels;
        if (samples > _convertScratch.Length)
            _convertScratch = new float[samples];
        Marshal.Copy((IntPtr)_swrOutBuffer, _convertScratch, 0, samples);
        builder.Append(_convertScratch.AsSpan(0, samples));
    }

    /// <summary>
    /// After full PCM expand we no longer need demuxer/codec.
    /// </summary>
    private unsafe void ReleaseStreamingResourcesUnlocked()
    {
        if (_swrOutBuffer != null)
        {
            ffmpeg.av_free(_swrOutBuffer);
            _swrOutBuffer = null;
            _swrOutBufferSamples = 0;
        }

        if (_packet != null)
        {
            fixed (AVPacket** pp = &_packet) ffmpeg.av_packet_free(pp);
            _packet = null;
        }
        if (_frame != null)
        {
            fixed (AVFrame** pp = &_frame) ffmpeg.av_frame_free(pp);
            _frame = null;
        }
        if (_swrCtx != null)
        {
            fixed (SwrContext** pp = &_swrCtx) ffmpeg.swr_free(pp);
            _swrCtx = null;
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
        fixed (AVChannelLayout* pIn = &_inChLayout) ffmpeg.av_channel_layout_uninit(pIn);
        fixed (AVChannelLayout* pOut = &_outChLayout) ffmpeg.av_channel_layout_uninit(pOut);

        _ring = null;
        _convertScratch = null;
    }

    private unsafe bool SeekDemuxerUnlocked(long timestampUs)
    {
        long seekTs = ffmpeg.av_rescale_q(
            timestampUs,
            new AVRational { num = 1, den = ffmpeg.AV_TIME_BASE },
            _timeBase);

        // Include stream start_time offset when present
        if (_startTimeStream != 0 && _startTimeStream != ffmpeg.AV_NOPTS_VALUE)
            seekTs += _startTimeStream;

        // BACKWARD seek_frame is the most reliable for audio (esp. WAV/PCM).
        // avformat_seek_file with max_ts == target is too strict and mis-seeks some WAVs.
        int ret = ffmpeg.av_seek_frame(_formatCtx, _audioStreamIndex, seekTs, ffmpeg.AVSEEK_FLAG_BACKWARD);
        if (ret < 0)
        {
            ret = ffmpeg.avformat_seek_file(
                _formatCtx,
                _audioStreamIndex,
                long.MinValue,
                seekTs,
                long.MaxValue,
                0);
        }

        if (ret < 0)
        {
            // Last resort: any-frame seek
            ret = ffmpeg.av_seek_frame(_formatCtx, _audioStreamIndex, seekTs, ffmpeg.AVSEEK_FLAG_ANY);
        }

        if (ret < 0)
        {
            GD.PrintErr($"AudioSourceDecoder:Seek - demuxer seek failed: {MediaEngine.GetFFmpegError(ret)}");
            return false;
        }

        ffmpeg.avformat_flush(_formatCtx);
        ffmpeg.avcodec_flush_buffers(_codecCtx);
        ReinitSwrUnlocked(Info.SampleRate);
        return true;
    }

    private unsafe void ReinitSwrUnlocked(int sampleRate)
    {
        if (_codecCtx == null) return;

        if (_swrCtx != null)
        {
            fixed (SwrContext** pp = &_swrCtx) ffmpeg.swr_free(pp);
            _swrCtx = null;
        }

        fixed (AVChannelLayout* pIn = &_inChLayout)
        fixed (AVChannelLayout* pOut = &_outChLayout)
        fixed (SwrContext** ppSwr = &_swrCtx)
        {
            int ret = ffmpeg.swr_alloc_set_opts2(
                ppSwr,
                pOut, AVSampleFormat.AV_SAMPLE_FMT_FLT, sampleRate,
                pIn, _codecCtx->sample_fmt, _codecCtx->sample_rate,
                0, null);
            if (ret < 0 || _swrCtx == null)
            {
                GD.PrintErr($"AudioSourceDecoder:ReinitSwr - alloc failed: {MediaEngine.GetFFmpegError(ret)}");
                return;
            }
            ret = ffmpeg.swr_init(_swrCtx);
            if (ret < 0)
                GD.PrintErr($"AudioSourceDecoder:ReinitSwr - init failed: {MediaEngine.GetFFmpegError(ret)}");
        }
    }

    private unsafe void ReinitSwrUnlocked()
    {
        if (Info != null)
            ReinitSwrUnlocked(Info.SampleRate);
    }

    /// <summary>
    /// After demuxer seek, decode and optionally discard until output reaches <paramref name="targetSamples"/>.
    /// Uses absolute PTS only when timestamps look consistent with the demuxer landing near the target.
    /// If PTS is missing or restarts at 0 after seek, trusts the demuxer position (no double-discard to EOF).
    /// </summary>
    private unsafe void RunSeekTrimUnlocked(long targetSamples)
    {
        int safety = 0;
        const int maxIterations = 50_000;
        // -1 = undecided, 0 = trust demuxer (no absolute discard), 1 = absolute PTS discard
        int ptsMode = -1;
        long decodedSamplesCursor = 0;

        while (safety++ < maxIterations && !_endOfStream)
        {
            int ret = ffmpeg.av_read_frame(_formatCtx, _packet);
            if (ret == ffmpeg.AVERROR_EOF)
            {
                ffmpeg.avcodec_send_packet(_codecCtx, null);
                if (!DrainFramesForSeekUnlocked(targetSamples, ref ptsMode, ref decodedSamplesCursor, out bool reached) || !reached)
                {
                    FlushSwrDiscardUnlocked();
                    // Only mark EOS if we never queued anything — seek past end
                    if (_ring.Available == 0)
                        _endOfStream = true;
                }
                return;
            }
            if (ret < 0)
            {
                GD.PrintErr($"AudioSourceDecoder:RunSeekTrim - read_frame failed: {MediaEngine.GetFFmpegError(ret)}");
                _endOfStream = true;
                return;
            }

            if (_packet->stream_index != _audioStreamIndex)
            {
                ffmpeg.av_packet_unref(_packet);
                continue;
            }

            ret = ffmpeg.avcodec_send_packet(_codecCtx, _packet);
            ffmpeg.av_packet_unref(_packet);
            if (ret < 0 && ret != ffmpeg.AVERROR(ffmpeg.EAGAIN))
                continue;

            if (DrainFramesForSeekUnlocked(targetSamples, ref ptsMode, ref decodedSamplesCursor, out bool reachedTarget)
                && reachedTarget)
            {
                return;
            }
        }
    }

    private unsafe bool DrainFramesForSeekUnlocked(
        long targetSamples,
        ref int ptsMode,
        ref long decodedSamplesCursor,
        out bool reachedTarget)
    {
        reachedTarget = false;
        bool any = false;
        // Allow absolute discard only if first frame PTS is not far past target and not "near zero while target is large"
        long nearZeroThreshold = Info.SampleRate; // 1 second of samples

        while (true)
        {
            int ret = ffmpeg.avcodec_receive_frame(_codecCtx, _frame);
            if (ret == ffmpeg.AVERROR(ffmpeg.EAGAIN) || ret == ffmpeg.AVERROR_EOF)
                return any;
            if (ret < 0)
                return any;

            any = true;
            int skipEncoder = GetFrameSkipSamplesUnlocked(_frame);
            long? absoluteStart = TryGetAbsoluteFrameStartSamplesUnlocked(_frame);
            int nb = _frame->nb_samples;
            if (skipEncoder > 0)
                nb = Math.Max(0, nb - skipEncoder);

            if (ptsMode < 0)
            {
                if (absoluteStart.HasValue)
                {
                    long fs = absoluteStart.Value + skipEncoder;
                    // PTS restarted / unusable: near zero while seeking deep into the file
                    if (fs < nearZeroThreshold && targetSamples > nearZeroThreshold * 2)
                    {
                        ptsMode = 0; // trust demuxer
                    }
                    // Seek overshot: first frame already past target — keep without discard
                    else if (fs > targetSamples + Info.SampleRate / 10)
                    {
                        ptsMode = 0;
                    }
                    else
                    {
                        ptsMode = 1; // absolute trim
                        decodedSamplesCursor = fs;
                    }
                }
                else
                {
                    // No PTS: demuxer BACKWARD seek is best-effort position; do not count from 0 to target
                    ptsMode = 0;
                }
            }

            if (ptsMode == 0)
            {
                // Trust demuxer landing; only apply encoder delay skip
                ConvertFrameToRingUnlocked(_frame, skipEncoder);
                ffmpeg.av_frame_unref(_frame);
                reachedTarget = true;
                return true;
            }

            // Absolute PTS mode
            long frameStart = absoluteStart ?? decodedSamplesCursor;
            frameStart += skipEncoder;
            long frameEnd = frameStart + nb;

            if (frameEnd <= targetSamples)
            {
                DiscardFrameThroughSwrUnlocked(_frame);
                decodedSamplesCursor = frameEnd;
                ffmpeg.av_frame_unref(_frame);
                continue;
            }

            int skipInFrame = 0;
            if (frameStart < targetSamples)
                skipInFrame = (int)(targetSamples - frameStart);
            skipInFrame += skipEncoder;

            ConvertFrameToRingUnlocked(_frame, skipInFrame);
            decodedSamplesCursor = Math.Max(frameEnd, targetSamples);
            ffmpeg.av_frame_unref(_frame);
            reachedTarget = true;
            return true;
        }
    }

    /// <summary>
    /// Absolute sample index of frame start from PTS, or null if timestamps are unusable.
    /// </summary>
    private unsafe long? TryGetAbsoluteFrameStartSamplesUnlocked(AVFrame* frame)
    {
        long pts = frame->best_effort_timestamp;
        if (pts == ffmpeg.AV_NOPTS_VALUE)
            pts = frame->pts;
        if (pts == ffmpeg.AV_NOPTS_VALUE)
            pts = frame->pkt_dts;
        if (pts == ffmpeg.AV_NOPTS_VALUE)
            return null;

        long adj = pts;
        if (_startTimeStream != 0 && _startTimeStream != ffmpeg.AV_NOPTS_VALUE)
            adj = pts - _startTimeStream;

        long samples = ffmpeg.av_rescale_q(adj, _timeBase, new AVRational { num = 1, den = Info.SampleRate });
        if (samples < 0) samples = 0;
        return samples;
    }

    private unsafe void DiscardFrameThroughSwrUnlocked(AVFrame* frame)
    {
        if (_swrCtx == null) return;
        int channels = Info.Channels;
        long delay = ffmpeg.swr_get_delay(_swrCtx, Info.SampleRate);
        int maxOut = (int)ffmpeg.av_rescale_rnd(delay + frame->nb_samples, Info.SampleRate, Info.SampleRate, AVRounding.AV_ROUND_UP) + 256;
        EnsureSwrBuffer(maxOut, channels);
        byte* outPtr = _swrOutBuffer;
        ffmpeg.swr_convert(_swrCtx, &outPtr, maxOut, frame->extended_data, frame->nb_samples);
        // discard output
    }

    private unsafe void FlushSwrDiscardUnlocked()
    {
        if (_swrCtx == null) return;
        int channels = Info.Channels;
        EnsureSwrBuffer(4096, channels);
        byte* outPtr = _swrOutBuffer;
        ffmpeg.swr_convert(_swrCtx, &outPtr, 4096, null, 0);
    }

    private unsafe bool DecodeMoreUnlocked()
    {
        if (_endOfStream) return false;

        int iterations = 0;
        const int maxIterations = 64;
        int before = _ring.Available;

        while (iterations++ < maxIterations && _ring.Free >= Info.Channels)
        {
            int ret = ffmpeg.avcodec_receive_frame(_codecCtx, _frame);
            if (ret >= 0)
            {
                int skip = GetFrameSkipSamplesUnlocked(_frame);
                ConvertFrameToRingUnlocked(_frame, skip);
                ffmpeg.av_frame_unref(_frame);
                if (_ring.Available > before) return true;
                continue;
            }
            if (ret != ffmpeg.AVERROR(ffmpeg.EAGAIN) && ret != ffmpeg.AVERROR_EOF)
            {
                GD.PrintErr($"AudioSourceDecoder:DecodeMore - receive_frame failed: {MediaEngine.GetFFmpegError(ret)}");
                _endOfStream = true;
                return false;
            }

            if (ret == ffmpeg.AVERROR_EOF)
            {
                FlushSwrToRingUnlocked();
                _endOfStream = true;
                return _ring.Available > before;
            }

            ret = ffmpeg.av_read_frame(_formatCtx, _packet);
            if (ret == ffmpeg.AVERROR_EOF)
            {
                ffmpeg.avcodec_send_packet(_codecCtx, null);
                continue;
            }
            if (ret < 0)
            {
                GD.PrintErr($"AudioSourceDecoder:DecodeMore - read_frame failed: {MediaEngine.GetFFmpegError(ret)}");
                _endOfStream = true;
                return false;
            }

            if (_packet->stream_index != _audioStreamIndex)
            {
                ffmpeg.av_packet_unref(_packet);
                continue;
            }

            ret = ffmpeg.avcodec_send_packet(_codecCtx, _packet);
            ffmpeg.av_packet_unref(_packet);
            if (ret < 0 && ret != ffmpeg.AVERROR(ffmpeg.EAGAIN))
                continue;
        }

        return _ring.Available > before;
    }

    private unsafe void ConvertFrameToRingUnlocked(AVFrame* frame, int skipSamples)
    {
        if (_swrCtx == null || Info == null) return;

        int channels = Info.Channels;
        long delay = ffmpeg.swr_get_delay(_swrCtx, Info.SampleRate);
        int maxOut = (int)ffmpeg.av_rescale_rnd(delay + frame->nb_samples, Info.SampleRate, Info.SampleRate, AVRounding.AV_ROUND_UP) + 256;
        EnsureSwrBuffer(maxOut, channels);

        byte* outPtr = _swrOutBuffer;
        int produced = ffmpeg.swr_convert(
            _swrCtx,
            &outPtr, maxOut,
            frame->extended_data, frame->nb_samples);

        if (produced < 0)
        {
            GD.PrintErr($"AudioSourceDecoder:ConvertFrame - swr_convert failed: {MediaEngine.GetFFmpegError(produced)}");
            return;
        }

        if (produced == 0) return;

        int skip = Math.Max(0, skipSamples);
        if (_discardOutputFrames > 0)
        {
            int d = (int)Math.Min(_discardOutputFrames, produced);
            skip += d;
            _discardOutputFrames -= d;
        }

        if (skip >= produced) return;

        int usableFrames = produced - skip;
        int usableSamples = usableFrames * channels;
        float* fptr = (float*)_swrOutBuffer + (skip * channels);

        int offset = 0;
        while (offset < usableSamples)
        {
            int free = _ring.Free;
            // Only write whole sample-frames; stop when the ring cannot take another frame.
            // Callers (Prefetch / DecodeMore) should stop decoding when Free < channels so this
            // path is rare — reserved for a single oversized converted frame vs remaining space.
            if (free < channels)
                break;

            int chunk = Math.Min(usableSamples - offset, free);
            chunk -= chunk % channels;
            if (chunk <= 0)
                break;

            if (chunk > _convertScratch.Length)
                _convertScratch = new float[chunk];

            Marshal.Copy((IntPtr)(fptr + offset), _convertScratch, 0, chunk);
            int written = _ring.Write(_convertScratch.AsSpan(0, chunk));
            offset += written;
            if (written < chunk)
                break;
        }
        // Any remaining samples in this converted frame are discarded only when the ring is full;
        // with RecommendedRingMs sizing, Prefetch stops before that under normal load.
    }

    private unsafe void FlushSwrToRingUnlocked()
    {
        if (_swrCtx == null || Info == null) return;
        int channels = Info.Channels;
        int maxOut = 4096;
        EnsureSwrBuffer(maxOut, channels);
        byte* outPtr = _swrOutBuffer;
        int produced = ffmpeg.swr_convert(_swrCtx, &outPtr, maxOut, null, 0);
        if (produced <= 0) return;

        int samples = produced * channels;
        if (samples > _convertScratch.Length)
            _convertScratch = new float[samples];
        Marshal.Copy((IntPtr)_swrOutBuffer, _convertScratch, 0, samples);
        _ring.Write(_convertScratch.AsSpan(0, samples));
    }

    private unsafe void EnsureSwrBuffer(int frames, int channels)
    {
        int needed = frames * channels * sizeof(float);
        if (_swrOutBuffer != null && _swrOutBufferSamples >= frames) return;

        if (_swrOutBuffer != null)
        {
            ffmpeg.av_free(_swrOutBuffer);
            _swrOutBuffer = null;
        }

        _swrOutBuffer = (byte*)ffmpeg.av_malloc((ulong)needed);
        _swrOutBufferSamples = frames;
        if (_swrOutBuffer == null)
            throw new Exception("AudioSourceDecoder:EnsureSwrBuffer - av_malloc failed.");
    }

    private unsafe void CloseInternal()
    {
        _isOpen = false;
        _endOfStream = true;
        Info = null;

        // Release large managed buffers and note size for LOH reclaim
        long released = 0;
        if (_pcmStore != null)
        {
            released += Cue2.Media.Audio.MediaMemory.FloatBufferBytes(_pcmStore);
            _pcmStore = null;
        }
        if (_convertScratch != null)
        {
            released += Cue2.Media.Audio.MediaMemory.FloatBufferBytes(_convertScratch);
            _convertScratch = null;
        }
        if (_ring != null)
        {
            released += (long)_ring.Capacity * sizeof(float);
            _ring = null;
        }
        _pcmFrameCount = 0;
        _pcmReadFrame = 0;
        _usePcmStore = false;
        _discardOutputFrames = 0;
        if (released > 0)
            Cue2.Media.Audio.MediaMemory.NoteReleased(released);

        if (_swrOutBuffer != null)
        {
            ffmpeg.av_free(_swrOutBuffer);
            _swrOutBuffer = null;
            _swrOutBufferSamples = 0;
        }

        if (_packet != null)
        {
            fixed (AVPacket** pp = &_packet) ffmpeg.av_packet_free(pp);
            _packet = null;
        }

        if (_frame != null)
        {
            fixed (AVFrame** pp = &_frame) ffmpeg.av_frame_free(pp);
            _frame = null;
        }

        if (_swrCtx != null)
        {
            fixed (SwrContext** pp = &_swrCtx) ffmpeg.swr_free(pp);
            _swrCtx = null;
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

        fixed (AVChannelLayout* pIn = &_inChLayout) ffmpeg.av_channel_layout_uninit(pIn);
        fixed (AVChannelLayout* pOut = &_outChLayout) ffmpeg.av_channel_layout_uninit(pOut);
    }

    /// <summary>
    /// Growable interleaved float buffer used while building the PCM store.
    /// </summary>
    private sealed class PcmBuilder
    {
        private float[] _data;
        private int _count;
        private readonly int _channels;

        public PcmBuilder(int initialSamples, int channels)
        {
            _channels = channels;
            _data = new float[Math.Max(initialSamples, channels * 1024)];
            _count = 0;
        }

        public int FrameCount => _channels > 0 ? _count / _channels : 0;

        public void Append(ReadOnlySpan<float> samples)
        {
            if (samples.IsEmpty) return;
            Ensure(samples.Length);
            samples.CopyTo(_data.AsSpan(_count, samples.Length));
            _count += samples.Length;
        }

        public float[] ToArray()
        {
            if (_count == _data.Length) return _data;
            Cue2.Media.Audio.MediaMemory.NoteReleased((long)_data.Length * sizeof(float));
            var exact = new float[_count];
            Array.Copy(_data, exact, _count);
            return exact;
        }

        private void Ensure(int additional)
        {
            int need = _count + additional;
            if (need <= _data.Length) return;
            int newSize = _data.Length;
            while (newSize < need)
            {
                newSize *= 2;
                if (newSize < 0 || newSize > int.MaxValue / 2)
                {
                    newSize = need;
                    break;
                }
            }
            // Old backing store becomes LOH garbage; count it so stop compact can reclaim.
            Cue2.Media.Audio.MediaMemory.NoteReleased((long)_data.Length * sizeof(float));
            Array.Resize(ref _data, newSize);
        }
    }
}
