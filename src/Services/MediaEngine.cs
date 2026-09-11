// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Cue2.Domain.Cuelist;
using Cue2.Domain.Playback;
using Cue2.Domain.Devices;
using Cue2.Domain.ShowSettings;
using Cue2.Domain.Metadata;
using Cue2.Domain.Cues;
using Cue2.Domain.Connections;
using Cue2.Domain.Library;
using Cue2.Domain.Commands;
using Cue2.Media.Audio;
using Godot;
using FFmpeg.AutoGen;

namespace Cue2.Services;

/// <summary>
/// Singleton media service for FFmpeg-backed decode, waveform generation, and metadata.
/// Coordinates preloading and cleanup for audio/video cue playback with minimal GO latency.
/// </summary>
public partial class MediaEngine : Node
{
    private GlobalSignals _globalSignals;
    private GlobalData _globalData;

    /// <summary>
    /// Limits concurrent FFmpeg waveform jobs so rapid focus changes cannot pile decode tasks.
    /// </summary>
    private readonly SemaphoreSlim _waveformSlots = new SemaphoreSlim(1, 1);

    /// <summary>
    /// In-flight waveform jobs keyed by absolute path + bin count (single-flight per media).
    /// </summary>
    private readonly object _waveformInflightLock = new object();
    private readonly Dictionary<string, Task<byte[]>> _waveformInflight = new Dictionary<string, Task<byte[]>>();
    
    public override void _Ready()
    {
        if (SingleInstanceGuard.IsSecondary)
            return;

        _globalSignals = GetNode<GlobalSignals>("/root/GlobalSignals");
        _globalData = GetNodeOrNull<GlobalData>("/root/GlobalData");
        try
        {
            GD.Print("MediaEngine:_Ready - Loading FFmpeg libs.");
            LinkFFmpegLibraries();
            GD.Print($"MediaEngine:_Ready - FFmpeg version: {ffmpeg.av_version_info()}");
        }
        catch (NotSupportedException ex)
        {
            string msg =
                "FFmpeg.AutoGen could not bind a native function (wrong FFmpeg ABI). " +
                "FFmpeg.AutoGen 9 requires FFmpeg 9 shared libraries " +
                "(avutil-61, avcodec-63, avformat-63, swresample-7, swscale-10). " +
                ex.Message;
            _globalSignals.EmitSignal(nameof(GlobalSignals.Log),
                $"MediaEngine:_Ready - Failed to initialize MediaEngine: {msg}", 2);
            GD.PrintErr($"MediaEngine:_Ready - Initialization error: {msg}, {ex.StackTrace}");
        }
        catch (Exception ex)
        {
            _globalSignals.EmitSignal(nameof(GlobalSignals.Log),
                $"MediaEngine:_Ready - Failed to initialize MediaEngine: {ex.Message}", 2);
            GD.PrintErr($"MediaEngine:_Ready - Initialization error: {ex.Message}, {ex.StackTrace}");
        }
        
    }

    // NOTE ON LICENSING:
    // The native FFmpeg libraries loaded here are distributed under the LGPLv2.1 (or later).
    // See docs/FFmpeg-Licensing.md and the LGPL-2.1.txt in the LICENSES folder.
    // This code uses dynamic loading (NativeLibrary.Load + RootPath) which is the
    // recommended pattern for LGPL compliance when bundling.
    
    /// <summary>
    /// Dynamically links FFmpeg native libraries manually for cross-platform compatibility in Godot Mono.
    /// Ensures core shared libraries (avcodec, avformat, etc.) are resolved before any FFmpeg calls.
    /// Searches export-friendly paths then <c>res://bin/{platform}/</c> (see <see cref="NativeLibPaths"/>).
    /// </summary>
    private void LinkFFmpegLibraries()
    {
        try
        {
            string platformDir = NativeLibPaths.GetPlatformDir(out string platformLabel);

            // Load order: avutil first (dependency of the rest). Majors come from
            // FFmpeg.AutoGen so they stay in lockstep with the bindings (9.x = 61/63/63/7/10).
            string[] loadOrder = { "avutil", "swresample", "avcodec", "avformat", "swscale" };
            var libs = new (string name, string major)[loadOrder.Length];
            for (int i = 0; i < loadOrder.Length; i++)
            {
                string name = loadOrder[i];
                if (!ffmpeg.LibraryVersionMap.TryGetValue(name, out int major))
                    throw new InvalidOperationException($"FFmpeg.AutoGen has no LibraryVersionMap entry for '{name}'.");
                libs[i] = (name, major.ToString());
            }

            var fileNames = new string[libs.Length];
            for (int i = 0; i < libs.Length; i++)
                fileNames[i] = NativeLibPaths.GetFFmpegLibraryFileName(libs[i].name, libs[i].major);

            string libPath = NativeLibPaths.FindDirectoryContainingAll(fileNames, platformDir, out var tried);
            if (string.IsNullOrEmpty(libPath))
            {
                string triedList = NativeLibPaths.FormatTriedDirectories(tried);
                string msg =
                    $"FFmpeg libraries not found for {platformLabel}. Looked in: {triedList}. " +
                    "After export, place core FFmpeg dylibs/DLLs in Contents/Frameworks (macOS), " +
                    "data_Cue2_*, or bin/{platform}/ (see docs/export-packaging.md).";
                GD.PrintErr($"MediaEngine:LoadFFmpegLibraries - {msg}");
                _globalSignals?.EmitSignal(nameof(GlobalSignals.Log), msg, 2);
                throw new DllNotFoundException(msg);
            }

            GD.Print($"MediaEngine:LoadFFmpegLibraries - Using {platformLabel} libs from: {libPath}");
            GD.Print($"MediaEngine:LoadFFmpegLibraries - Candidates tried: {NativeLibPaths.FormatTriedDirectories(tried)}");

            // FFmpeg.AutoGen resolves further loads from RootPath
            ffmpeg.RootPath = libPath;

            // Preload in dependency order so dyld/Windows loader can resolve siblings when
            // install names use @loader_path (portable builds) rather than absolute Homebrew paths.
            foreach (string fileName in fileNames)
            {
                string fullPath = Path.Combine(libPath, fileName);
                if (!File.Exists(fullPath))
                    throw new FileNotFoundException($"Missing FFmpeg library file: {fullPath}");

                nint handle = NativeLibrary.Load(fullPath);
                GD.Print($"MediaEngine:LoadFFmpegLibraries - Loaded {fileName} (handle: {handle})");
            }

            GD.Print("MediaEngine:LoadFFmpegLibraries - All FFmpeg libs loaded successfully.");
        }
        catch (DllNotFoundException ex)
        {
            GD.PrintErr($"MediaEngine:LoadFFmpegLibraries - Library not found: {ex.Message}");
            _globalSignals?.EmitSignal(nameof(GlobalSignals.Log),
                $"FFmpeg: {ex.Message}", 2);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"MediaEngine:LoadFFmpegLibraries - Load error: {ex.Message}");
            _globalSignals?.EmitSignal(nameof(GlobalSignals.Log),
                $"FFmpeg load error: {ex.Message}", 2);
        }
    }

    
    /// <summary>
    /// Gets metadata for an audio file using FFmpeg (duration, channels, sample rate, bit depth, codec/format).
    /// Fast extraction without full decoding; supports broad formats (MP3, FLAC, AAC, etc.).
    /// Returns default-initialized metadata on failure.
    /// </summary>
    /// <param name="path">Audio file path.</param>
    /// <returns>AudioFileMetadata with extracted values.</returns>
    public async Task<AudioFileMetadata> GetAudioFileMetadataAsync(string path)
    {
        path = ResolveMediaPath(path);
        if (!File.Exists(path)) 
        { 
            GD.PrintErr("MediaEngine:GetAudioFileMetadataAsync - File not found.");
            return new AudioFileMetadata(); // Default empty on fail
        }

        GD.Print("MediaEngine:GetAudioFileMetadataAsync - Extracting metadata.");

        return await Task.Run(() =>
        {
            unsafe 
            { 
                AVFormatContext* formatCtx = null; 

                var metadata = new AudioFileMetadata(); 

                try 
                { 
                    // Open input 
                    int ret = ffmpeg.avformat_open_input(&formatCtx, path, null, null); 
                    if (ret < 0) throw new Exception($"Failed to open file: {GetFFmpegError(ret)}");

                    ret = ffmpeg.avformat_find_stream_info(formatCtx, null); 
                    if (ret < 0) throw new Exception($"Failed to find stream info: {GetFFmpegError(ret)}"); 

                    // Duration from container (in seconds; handle AV_NOPTS_VALUE) 
                    long durationTicks = formatCtx->duration; 
                    if (durationTicks != -9223372036854775807L) //  Use numeric literal for AV_NOPTS_VALUE (int64_t max -1; no symbol resolution needed)
                    {
                        metadata.Duration = durationTicks / (double)ffmpeg.AV_TIME_BASE; // AV_TIME_BASE = 1000000 (ticks/sec)
                    }
                    else 
                    {
                        GD.PrintErr("MediaEngine:GetAudioFileMetadataAsync - Duration unknown (NOPTS); returning 0.0.");
                    }// Else remains 0.0

                    int audioStreamIndex = -1; 
                    for (uint i = 0; i < formatCtx->nb_streams; i++) 
                    { 
                        if (formatCtx->streams[i]->codecpar->codec_type == AVMediaType.AVMEDIA_TYPE_AUDIO) 
                        { 
                            audioStreamIndex = (int)i; 
                            break; 
                        } 
                    } 

                    if (audioStreamIndex == -1) 
                    { 
                        throw new Exception("No audio stream found."); 
                    } 

                    AVCodecParameters* codecPar = formatCtx->streams[(uint)audioStreamIndex]->codecpar; 

                    // Channels from layout (modern API) 
                    metadata.Channels = (int)codecPar->ch_layout.nb_channels; 

                    // Sample rate 
                    metadata.SampleRate = codecPar->sample_rate; 

                    // Bit depth from sample format 
                    AVSampleFormat sampleFmt = (AVSampleFormat)codecPar->format; 
                    int bytesPerSample = ffmpeg.av_get_bytes_per_sample(sampleFmt); 
                    metadata.BitDepth = bytesPerSample * 8; // Bytes to bits; 0 if unknown

                    // Codec name 
                    AVCodec* codec = ffmpeg.avcodec_find_decoder(codecPar->codec_id); 
                    metadata.Codec = codec != null ? ffmpeg.avcodec_get_name(codec->id) : "unknown"; 

                    // Format from container
                    string ext = Path.GetExtension(path).TrimStart('.');
                    AVOutputFormat* fmtPtr = ffmpeg.av_guess_format(null, ext, null); // Get pointer
                    if (fmtPtr != null)
                    { 
                        metadata.Format = Marshal.PtrToStringAnsi((IntPtr)fmtPtr->name) ?? "unknown"; //Dereference pointer's name (byte*) via Marshal; prefixed minimal
                    } 
                    else 
                    { 
                        metadata.Format = "unknown"; // Fallback for null guess
                    }
                    GD.Print("MediaEngine:GetAudioFileMetadataAsync - Metadata extracted successfully.");
                    return metadata; 
                } 
                catch (Exception ex) 
                { 
                    GD.PrintErr($"MediaEngine:GetAudioFileMetadataAsync - Error: {ex.Message}");
                    return new AudioFileMetadata(); // Default on fail
                } 
                finally 
                { 
                    if (formatCtx != null) ffmpeg.avformat_close_input(&formatCtx); // Cleanup
                } 
            } 
        }); 
    } 
    
    
    /// <summary>
    /// Resolves show-relative media paths against the current session directory.
    /// </summary>
    private string ResolveMediaPath(string path) =>
        _globalData?.ResolveMediaPath(path) ?? path;

    /// <summary>
    /// Generates a waveform peak envelope for an audio (or video) file using FFmpeg.
    /// Audacity-style: decode once, stream min/max into fixed bins (no full-sample buffer).
    /// Uses <see cref="Settings.WaveformResolution"/> when available. Peaks are not stored in
    /// the .c2 showfile; the session disk cache under <see cref="GlobalData.SessionWaveformsPath"/>
    /// (<c>Waveforms/*.c2wf</c>) is the durable store across save/load.
    /// </summary>
    /// <remarks>
    /// Single-flight per path+resolution and a global one-at-a-time FFmpeg slot prevent rapid
    /// focus changes from piling decode jobs. Pass a <paramref name="cancellationToken"/> from
    /// inspectors so focus changes abandon awaits (and cancel a job that is still exclusive).
    /// </remarks>
    /// <param name="path">Media file path with an audio stream.</param>
    /// <param name="cancellationToken">Cancels wait / in-flight exclusive decode when possible.</param>
    /// <returns>Serialized <see cref="WaveformPeaks"/> bytes, or empty on failure/cancel.</returns>
    public async Task<byte[]> GenerateWaveformAsync(string path, CancellationToken cancellationToken = default)
    {
        path = ResolveMediaPath(path);
        if (!File.Exists(path))
        {
            GD.PrintErr("MediaEngine:GenerateWaveformAsync - File not found.");
            return Array.Empty<byte>();
        }

        cancellationToken.ThrowIfCancellationRequested();

        // Disk cache (session Waveforms/ folder when a show is open)
        string cachePath = TryGetWaveformCachePath(path);
        if (cachePath != null && File.Exists(cachePath))
        {
            try
            {
                byte[] cached = await File.ReadAllBytesAsync(cachePath, cancellationToken).ConfigureAwait(false);
                if (WaveformPeaks.FromBytes(cached) != null)
                {
                    GD.Print($"MediaEngine:GenerateWaveformAsync - Cache hit: {Path.GetFileName(cachePath)}");
                    return cached;
                }
            }
            catch (OperationCanceledException)
            {
                return Array.Empty<byte>();
            }
            catch (Exception ex)
            {
                GD.PrintErr($"MediaEngine:GenerateWaveformAsync - Cache read failed: {ex.Message}");
            }
        }

        int binCount = WaveformPeaks.DefaultBinCount;
        try
        {
            if (_globalData?.Settings != null)
                binCount = WaveformPeaks.ClampBinCount(_globalData.Settings.WaveformResolution);
        }
        catch { /* Settings may not be ready */ }

        string flightKey = path + "\u001f" + binCount;

        // Single-flight: concurrent requests for the same file share one decode.
        Task<byte[]> job;
        lock (_waveformInflightLock)
        {
            if (_waveformInflight.TryGetValue(flightKey, out var existing) && !existing.IsCompleted)
            {
                job = existing;
            }
            else
            {
                job = RunWaveformJobAsync(path, binCount, cachePath, flightKey);
                _waveformInflight[flightKey] = job;
            }
        }

        try
        {
            // Caller cancel abandons the wait; shared job may continue for other waiters / cache.
            return await job.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return Array.Empty<byte>();
        }
    }

    /// <summary>
    /// Runs exclusive FFmpeg peak extraction (one slot), writes disk cache, clears inflight entry.
    /// </summary>
    private async Task<byte[]> RunWaveformJobAsync(string path, int binCount, string cachePath, string flightKey)
    {
        try
        {
            await _waveformSlots.WaitAsync().ConfigureAwait(false);
            try
            {
                // Re-check disk cache after waiting for the slot (another job may have filled it).
                if (cachePath != null && File.Exists(cachePath))
                {
                    try
                    {
                        byte[] cached = await File.ReadAllBytesAsync(cachePath).ConfigureAwait(false);
                        if (WaveformPeaks.FromBytes(cached) != null)
                            return cached;
                    }
                    catch { /* fall through to generate */ }
                }

                GD.Print($"MediaEngine:GenerateWaveformAsync - Generating {binCount} bins for file.");
                // Shared single-flight job is not tied to one caller's CT — finish so waiters/cache benefit.
                byte[] result = await Task.Run(() => GenerateWaveformCore(path, binCount, CancellationToken.None))
                    .ConfigureAwait(false);

                if (result.Length > 0 && cachePath != null)
                {
                    try
                    {
                        string dir = Path.GetDirectoryName(cachePath);
                        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                            Directory.CreateDirectory(dir);
                        await File.WriteAllBytesAsync(cachePath, result).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        GD.PrintErr($"MediaEngine:GenerateWaveformAsync - Cache write failed: {ex.Message}");
                    }
                }

                return result;
            }
            finally
            {
                _waveformSlots.Release();
            }
        }
        finally
        {
            lock (_waveformInflightLock)
                _waveformInflight.Remove(flightKey);
        }
    }

    /// <summary>
    /// Resolves the disk cache path for peaks under <see cref="GlobalData.SessionWaveformsPath"/>.
    /// Creates the folder when missing so open/generate can write without a prior save.
    /// </summary>
    private string TryGetWaveformCachePath(string mediaPath)
    {
        try
        {
            string root = _globalData?.SessionWaveformsPath;
            if (string.IsNullOrEmpty(root))
                return null;
            if (!Directory.Exists(root))
                Directory.CreateDirectory(root);
            return Path.Combine(root, WaveformPeaks.CacheFileName(mediaPath));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Streaming peak extraction: bin samples as they decode (O(1) memory in sample count).
    /// Reuses a single swr output buffer for the whole decode (no per-frame alloc).
    /// </summary>
    /// <param name="path">Absolute media path.</param>
    /// <param name="binCount">Display bin resolution.</param>
    /// <param name="ct">Cooperative cancel checked while demuxing.</param>
    private unsafe byte[] GenerateWaveformCore(string path, int binCount, CancellationToken ct)
    {
        AVFormatContext* formatCtx = null;
        AVCodecContext* codecCtx = null;
        AVPacket* packet = ffmpeg.av_packet_alloc();
        AVFrame* frame = ffmpeg.av_frame_alloc();
        SwrContext* swrCtx = null;
        AVChannelLayout inChLayout = default;
        AVChannelLayout outChLayout = default;
        // Reused mono float convert buffer (avoids per-frame av_samples_alloc).
        byte* outBuffer = null;
        int outBufferSamples = 0;

        try
        {
            ct.ThrowIfCancellationRequested();

            int ret = ffmpeg.avformat_open_input(&formatCtx, path, null, null);
            if (ret < 0) throw new Exception($"Open failed: {GetFFmpegError(ret)}");

            ret = ffmpeg.avformat_find_stream_info(formatCtx, null);
            if (ret < 0) throw new Exception($"Stream info failed: {GetFFmpegError(ret)}");

            int audioStreamIndex = -1;
            for (uint i = 0; i < formatCtx->nb_streams; i++)
            {
                if (formatCtx->streams[i]->codecpar->codec_type == AVMediaType.AVMEDIA_TYPE_AUDIO)
                {
                    audioStreamIndex = (int)i;
                    break;
                }
            }
            if (audioStreamIndex == -1) throw new Exception("No audio stream found.");

            AVStream* stream = formatCtx->streams[(uint)audioStreamIndex];
            AVCodecParameters* codecPar = stream->codecpar;
            AVCodec* codec = ffmpeg.avcodec_find_decoder(codecPar->codec_id);
            if (codec == null) throw new Exception("Unsupported codec.");

            codecCtx = ffmpeg.avcodec_alloc_context3(codec);
            ret = ffmpeg.avcodec_parameters_to_context(codecCtx, codecPar);
            if (ret < 0) throw new Exception($"Params failed: {GetFFmpegError(ret)}");

            ret = ffmpeg.avcodec_open2(codecCtx, codec, null);
            if (ret < 0) throw new Exception($"Codec open failed: {GetFFmpegError(ret)}");

            // Prefer actual channel layout from the codec
            ret = ffmpeg.av_channel_layout_copy(&inChLayout, &codecCtx->ch_layout);
            if (ret < 0)
                ffmpeg.av_channel_layout_default(&inChLayout, Math.Max(1, codecCtx->ch_layout.nb_channels));

            ffmpeg.av_channel_layout_default(&outChLayout, 1);

            const int outRate = 44100;
            ret = ffmpeg.swr_alloc_set_opts2(
                &swrCtx,
                &outChLayout, AVSampleFormat.AV_SAMPLE_FMT_FLT, outRate,
                &inChLayout, codecCtx->sample_fmt, codecCtx->sample_rate,
                0, null);
            if (ret < 0 || swrCtx == null) throw new Exception($"Swr alloc failed: {GetFFmpegError(ret)}");
            ret = ffmpeg.swr_init(swrCtx);
            if (ret < 0) throw new Exception($"Swr init failed: {GetFFmpegError(ret)}");

            // Audacity-style: summarize into fixed-size sample chunks while decoding,
            // then reduce chunks → display bins. Memory is O(duration / chunk), not O(samples).
            const int samplesPerChunk = 256;
            var chunks = new List<(float min, float max)>(4096);
            float chunkMin = float.MaxValue;
            float chunkMax = float.MinValue;
            int chunkCount = 0;
            long sampleIndex = 0;
            int packetIter = 0;

            void FlushChunk()
            {
                if (chunkCount <= 0) return;
                chunks.Add((chunkMin, chunkMax));
                chunkMin = float.MaxValue;
                chunkMax = float.MinValue;
                chunkCount = 0;
            }

            void Accumulate(float* mono, int count)
            {
                for (int j = 0; j < count; j++)
                {
                    float s = mono[j];
                    if (s < chunkMin) chunkMin = s;
                    if (s > chunkMax) chunkMax = s;
                    chunkCount++;
                    sampleIndex++;
                    if (chunkCount >= samplesPerChunk)
                        FlushChunk();
                }
            }

            while (ffmpeg.av_read_frame(formatCtx, packet) >= 0)
            {
                // Cooperative cancel every 16 packets so focus changes can abort long files.
                if ((packetIter++ & 15) == 0)
                    ct.ThrowIfCancellationRequested();

                if (packet->stream_index != audioStreamIndex)
                {
                    ffmpeg.av_packet_unref(packet);
                    continue;
                }

                ret = ffmpeg.avcodec_send_packet(codecCtx, packet);
                ffmpeg.av_packet_unref(packet);
                if (ret < 0 && ret != ffmpeg.AVERROR(ffmpeg.EAGAIN))
                    continue;

                while (ffmpeg.avcodec_receive_frame(codecCtx, frame) >= 0)
                {
                    int maxOut = (int)ffmpeg.av_rescale_rnd(
                        ffmpeg.swr_get_delay(swrCtx, codecCtx->sample_rate) + frame->nb_samples,
                        outRate, codecCtx->sample_rate, AVRounding.AV_ROUND_UP) + 256;

                    if (maxOut > outBufferSamples || outBuffer == null)
                    {
                        if (outBuffer != null)
                            ffmpeg.av_freep(&outBuffer);
                        outBuffer = null;
                        outBufferSamples = 0;
                        int linesizeGrow = 0;
                        if (ffmpeg.av_samples_alloc(&outBuffer, &linesizeGrow, 1, maxOut, AVSampleFormat.AV_SAMPLE_FMT_FLT, 0) < 0
                            || outBuffer == null)
                        {
                            ffmpeg.av_frame_unref(frame);
                            continue;
                        }
                        outBufferSamples = maxOut;
                    }

                    int outSamples = ffmpeg.swr_convert(swrCtx, &outBuffer, maxOut, frame->extended_data, frame->nb_samples);
                    if (outSamples > 0)
                        Accumulate((float*)outBuffer, outSamples);

                    ffmpeg.av_frame_unref(frame);
                }
            }

            // Flush decoder + resampler
            ffmpeg.avcodec_send_packet(codecCtx, null);
            while (ffmpeg.avcodec_receive_frame(codecCtx, frame) >= 0)
            {
                ct.ThrowIfCancellationRequested();
                int maxOut = (int)ffmpeg.av_rescale_rnd(
                    ffmpeg.swr_get_delay(swrCtx, codecCtx->sample_rate) + frame->nb_samples,
                    outRate, codecCtx->sample_rate, AVRounding.AV_ROUND_UP) + 256;
                if (maxOut > outBufferSamples || outBuffer == null)
                {
                    if (outBuffer != null)
                        ffmpeg.av_freep(&outBuffer);
                    outBuffer = null;
                    outBufferSamples = 0;
                    int linesizeFlush = 0;
                    if (ffmpeg.av_samples_alloc(&outBuffer, &linesizeFlush, 1, maxOut, AVSampleFormat.AV_SAMPLE_FMT_FLT, 0) >= 0
                        && outBuffer != null)
                        outBufferSamples = maxOut;
                }
                if (outBuffer != null && outBufferSamples > 0)
                {
                    int outSamples = ffmpeg.swr_convert(swrCtx, &outBuffer, maxOut, frame->extended_data, frame->nb_samples);
                    if (outSamples > 0)
                        Accumulate((float*)outBuffer, outSamples);
                }
                ffmpeg.av_frame_unref(frame);
            }
            {
                const int flushSamples = 4096;
                if (flushSamples > outBufferSamples || outBuffer == null)
                {
                    if (outBuffer != null)
                        ffmpeg.av_freep(&outBuffer);
                    outBuffer = null;
                    outBufferSamples = 0;
                    int linesizeTail = 0;
                    if (ffmpeg.av_samples_alloc(&outBuffer, &linesizeTail, 1, flushSamples, AVSampleFormat.AV_SAMPLE_FMT_FLT, 0) >= 0
                        && outBuffer != null)
                        outBufferSamples = flushSamples;
                }
                if (outBuffer != null && outBufferSamples > 0)
                {
                    int outSamples = ffmpeg.swr_convert(swrCtx, &outBuffer, flushSamples, null, 0);
                    if (outSamples > 0)
                        Accumulate((float*)outBuffer, outSamples);
                }
            }

            FlushChunk();

            if (sampleIndex == 0 || chunks.Count == 0)
                throw new Exception("No samples decoded.");

            // Reduce chunk peaks → fixed display resolution
            float[] minMax = new float[binCount * 2];
            for (int i = 0; i < binCount; i++)
            {
                minMax[i * 2] = float.MaxValue;
                minMax[i * 2 + 1] = float.MinValue;
            }

            int chunkN = chunks.Count;
            for (int c = 0; c < chunkN; c++)
            {
                int binIdx = (int)((long)c * binCount / chunkN);
                if (binIdx >= binCount) binIdx = binCount - 1;
                var (mn, mx) = chunks[c];
                if (mn < minMax[binIdx * 2]) minMax[binIdx * 2] = mn;
                if (mx > minMax[binIdx * 2 + 1]) minMax[binIdx * 2 + 1] = mx;
            }

            for (int i = 0; i < binCount; i++)
            {
                if (minMax[i * 2] == float.MaxValue)
                {
                    minMax[i * 2] = 0f;
                    minMax[i * 2 + 1] = 0f;
                }
            }

            var peaks = new WaveformPeaks(binCount, minMax);
            GD.Print($"MediaEngine:GenerateWaveformAsync - OK bins={binCount} samples={sampleIndex} chunks={chunkN}");
            return peaks.ToBytes();
        }
        catch (OperationCanceledException)
        {
            GD.Print("MediaEngine:GenerateWaveformAsync - Cancelled");
            return Array.Empty<byte>();
        }
        catch (Exception ex)
        {
            GD.PrintErr($"MediaEngine:GenerateWaveformAsync - Error: {ex.Message}");
            return Array.Empty<byte>();
        }
        finally
        {
            if (outBuffer != null)
                ffmpeg.av_freep(&outBuffer);
            if (packet != null) ffmpeg.av_packet_free(&packet);
            if (frame != null) ffmpeg.av_frame_free(&frame);
            if (swrCtx != null) ffmpeg.swr_free(&swrCtx);
            if (codecCtx != null) ffmpeg.avcodec_free_context(&codecCtx);
            if (formatCtx != null) ffmpeg.avformat_close_input(&formatCtx);
            ffmpeg.av_channel_layout_uninit(&inChLayout);
            ffmpeg.av_channel_layout_uninit(&outChLayout);
        }
    }

    /// <summary>
    /// Gets metadata for a video file using FFmpeg (duration, width, height, frame rate, codec/format, and audio metadata if present).
    /// Fast extraction without full decoding; supports broad formats (MP4, AVI, etc.).
    /// Returns default-initialized metadata on failure.
    /// </summary>
    /// <param name="path">Video file path.</param>
    /// <returns>VideoFileMetadata with extracted values.</returns>
    public async Task<VideoFileMetadata> GetVideoFileMetadataAsync(string path)
    {
        path = ResolveMediaPath(path);
        if (!File.Exists(path))
        {
            GD.PrintErr("MediaEngine:GetVideoFileMetadataAsync - File not found.");
            return new VideoFileMetadata(); // Default empty on fail
        }

        GD.Print("MediaEngine:GetVideoFileMetadataAsync - Extracting metadata.");

        return await Task.Run(() =>
        {
            unsafe
            {
                AVFormatContext* formatCtx = null;

                var metadata = new VideoFileMetadata();

                try
                {
                    // Open input
                    int ret = ffmpeg.avformat_open_input(&formatCtx, path, null, null);
                    if (ret < 0) throw new Exception($"Failed to open file: {GetFFmpegError(ret)}");

                    ret = ffmpeg.avformat_find_stream_info(formatCtx, null);
                    if (ret < 0) throw new Exception($"Failed to find stream info: {GetFFmpegError(ret)}");

                    // Duration from container
                    long durationTicks = formatCtx->duration;
                    if (durationTicks != -9223372036854775807L) // AV_NOPTS_VALUE
                    {
                        metadata.Duration = durationTicks / (double)ffmpeg.AV_TIME_BASE;
                    }
                    else
                    {
                        GD.PrintErr("MediaEngine:GetVideoFileMetadataAsync - Duration unknown.");
                    }

                    int videoStreamIndex = -1;
                    for (uint i = 0; i < formatCtx->nb_streams; i++)
                    {
                        if (formatCtx->streams[i]->codecpar->codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO)
                        {
                            videoStreamIndex = (int)i;
                            break;
                        }
                    }

                    if (videoStreamIndex == -1)
                    {
                        throw new Exception("No video stream found.");
                    }

                    AVCodecParameters* codecPar = formatCtx->streams[(uint)videoStreamIndex]->codecpar;

                    // Width and height
                    metadata.Width = codecPar->width;
                    metadata.Height = codecPar->height;

                    // Frame rate
                    AVRational frameRate = formatCtx->streams[(uint)videoStreamIndex]->r_frame_rate;
                    if (frameRate.den != 0)
                    {
                        metadata.FrameRate = (float)frameRate.num / frameRate.den;
                    }

                    // Codec name
                    AVCodec* codec = ffmpeg.avcodec_find_decoder(codecPar->codec_id);
                    metadata.Codec = codec != null ? ffmpeg.avcodec_get_name(codec->id) : "unknown";

                    // Format from container
                    string ext = Path.GetExtension(path).TrimStart('.');
                    AVOutputFormat* fmtPtr = ffmpeg.av_guess_format(null, ext, null);
                    if (fmtPtr != null)
                    {
                        metadata.Format = Marshal.PtrToStringAnsi((IntPtr)fmtPtr->name) ?? "unknown";
                    }
                    else
                    {
                        metadata.Format = "unknown";
                    }

                    // Check for audio stream and extract audio metadata if present
                    int audioStreamIndex = -1;
                    for (uint i = 0; i < formatCtx->nb_streams; i++)
                    {
                        if (formatCtx->streams[i]->codecpar->codec_type == AVMediaType.AVMEDIA_TYPE_AUDIO)
                        {
                            audioStreamIndex = (int)i;
                            break;
                        }
                    }

                    if (audioStreamIndex != -1)
                    {
                        AVCodecParameters* audioCodecPar = formatCtx->streams[(uint)audioStreamIndex]->codecpar;

                        // Audio channels from layout
                        metadata.AudioChannels = (int)audioCodecPar->ch_layout.nb_channels;

                        // Audio sample rate
                        metadata.AudioSampleRate = audioCodecPar->sample_rate;

                        // Audio bit depth from sample format
                        AVSampleFormat audioSampleFmt = (AVSampleFormat)audioCodecPar->format;
                        int audioBytesPerSample = ffmpeg.av_get_bytes_per_sample(audioSampleFmt);
                        metadata.AudioBitDepth = audioBytesPerSample * 8; // Bytes to bits; 0 if unknown

                        // Audio codec name
                        AVCodec* audioCodec = ffmpeg.avcodec_find_decoder(audioCodecPar->codec_id);
                        metadata.AudioCodec = audioCodec != null ? ffmpeg.avcodec_get_name(audioCodec->id) : "unknown";
                    }

                    // Subtitle / closed-caption streams (embedded)
                    metadata.SubtitleTracks = new System.Collections.Generic.List<SubtitleTrackInfo>();
                    var streamTypeCounts = new System.Collections.Generic.Dictionary<string, int>();
                    for (uint i = 0; i < formatCtx->nb_streams; i++)
                    {
                        AVStream* subStream = formatCtx->streams[i];
                        var codecType = subStream->codecpar->codec_type;
                        string typeKey = codecType.ToString();
                        streamTypeCounts[typeKey] = streamTypeCounts.TryGetValue(typeKey, out int c) ? c + 1 : 1;

                        // Include SUBTITLE streams and caption-disposition / known CC data streams.
                        bool isSubtitleType = codecType == AVMediaType.AVMEDIA_TYPE_SUBTITLE;
                        bool hasCaptionDisposition =
                            (subStream->disposition & ffmpeg.AV_DISPOSITION_CAPTIONS) != 0
                            || (subStream->disposition & ffmpeg.AV_DISPOSITION_DESCRIPTIONS) != 0;
                        AVCodecID subCodecId = subStream->codecpar->codec_id;
                        string codecName = ffmpeg.avcodec_get_name(subCodecId) ?? "unknown";
                        bool knownCcCodec =
                            Cue2.Media.Decoders.SubtitleSourceDecoder.IsTextBasedCodecId(subCodecId)
                            || Cue2.Media.Decoders.SubtitleSourceDecoder.IsTextBasedCodecName(codecName);

                        if (!isSubtitleType && !(hasCaptionDisposition && knownCcCodec)
                            && !(codecType == AVMediaType.AVMEDIA_TYPE_DATA && knownCcCodec))
                            continue;

                        string language = ReadStreamMetadataTag(subStream, "language");
                        string title = ReadStreamMetadataTag(subStream, "title");
                        bool isText = knownCcCodec
                                      || (isSubtitleType && !IsLikelyBitmapSubtitleCodec(subCodecId, codecName));

                        metadata.SubtitleTracks.Add(new SubtitleTrackInfo
                        {
                            StreamIndex = (int)i,
                            Codec = codecName,
                            Language = language,
                            Title = title,
                            IsTextBased = isText,
                            ExternalFilePath = string.Empty
                        });
                    }

                    // Sidecar subtitle files next to the media (video.srt, video.en.vtt, …)
                    foreach (var sidecar in FindSidecarSubtitleTracks(path))
                        metadata.SubtitleTracks.Add(sidecar);

                    string typeSummary = string.Join(", ",
                        System.Linq.Enumerable.Select(streamTypeCounts, kv => $"{kv.Key}={kv.Value}"));
                    GD.Print(
                        $"MediaEngine:GetVideoFileMetadataAsync - Metadata extracted successfully. " +
                        $"streams=[{typeSummary}] subtitles={metadata.SubtitleTracks.Count} " +
                        $"text={metadata.HasTextSubtitles}");
                    return metadata;
                }
                catch (Exception ex)
                {
                    GD.PrintErr($"MediaEngine:GetVideoFileMetadataAsync - Error: {ex.Message}");
                    return new VideoFileMetadata(); // Default on fail
                }
                finally
                {
                    if (formatCtx != null) ffmpeg.avformat_close_input(&formatCtx);
                }
            }
        });
    }

    /// <summary>
    /// Checks for available hardware acceleration devices and returns the best supported type.
    /// Prioritizes CUDA (NVDEC) for NVIDIA GPUs, then VAAPI for Linux, VideoToolbox for macOS.
    /// Returns AVHWDeviceType.None if no hardware acceleration is available.
    /// </summary>
    /// <returns>The best available hardware device type for video decoding.</returns>
    public static unsafe AVHWDeviceType GetBestHardwareDeviceType()
    {
        // List of preferred hardware types in order
        AVHWDeviceType[] preferredTypes = {
            AVHWDeviceType.AV_HWDEVICE_TYPE_CUDA,    // NVIDIA NVDEC
            AVHWDeviceType.AV_HWDEVICE_TYPE_VAAPI,   // Intel/AMD on Linux
            AVHWDeviceType.AV_HWDEVICE_TYPE_VIDEOTOOLBOX, // Apple on macOS
            AVHWDeviceType.AV_HWDEVICE_TYPE_DXVA2,   // Windows DirectX
            AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA  // Windows D3D11
        };

        foreach (var type in preferredTypes)
        {
            if (ffmpeg.av_hwdevice_get_type_name(type) != null)
            {
                // Check if we can create a device context (basic availability test)
                AVBufferRef* hwDeviceCtx = null;
                int ret = ffmpeg.av_hwdevice_ctx_create(&hwDeviceCtx, type, null, null, 0);
                if (ret >= 0)
                {
                    ffmpeg.av_buffer_unref(&hwDeviceCtx);
                    GD.Print($"MediaEngine:GetBestHardwareDeviceType - Hardware acceleration available: {ffmpeg.av_hwdevice_get_type_name(type)}");
                    return type;
                }
            }
        }

        GD.Print("MediaEngine:GetBestHardwareDeviceType - No hardware acceleration available, falling back to software decoding.");
        return AVHWDeviceType.AV_HWDEVICE_TYPE_NONE;
    }

    /// <summary>
    /// Creates a hardware device context for the specified device type.
    /// Returns null if creation fails or type is NONE.
    /// </summary>
    /// <param name="deviceType">The hardware device type to create.</param>
    /// <returns>Pointer to the hardware device context buffer, or null on failure.</returns>
    public static unsafe AVBufferRef* CreateHardwareDeviceContext(AVHWDeviceType deviceType)
    {
        if (deviceType == AVHWDeviceType.AV_HWDEVICE_TYPE_NONE)
        {
            return null;
        }

        AVBufferRef* hwDeviceCtx = null;
        int ret = ffmpeg.av_hwdevice_ctx_create(&hwDeviceCtx, deviceType, null, null, 0);
        if (ret < 0)
        {
            GD.PrintErr($"MediaEngine:CreateHardwareDeviceContext - Failed to create hardware device context for {ffmpeg.av_hwdevice_get_type_name(deviceType)}: {GetFFmpegError(ret)}");
            return null;
        }

        GD.Print($"MediaEngine:CreateHardwareDeviceContext - Created hardware device context for {ffmpeg.av_hwdevice_get_type_name(deviceType)}");
        return hwDeviceCtx;
    }

    /// <summary>
    /// Reads a string tag from an FFmpeg stream's metadata dictionary.
    /// </summary>
    private static unsafe string ReadStreamMetadataTag(AVStream* stream, string key)
    {
        if (stream == null || string.IsNullOrEmpty(key))
            return string.Empty;

        AVDictionaryEntry* entry = ffmpeg.av_dict_get(stream->metadata, key, null, 0);
        if (entry == null || entry->value == null)
            return string.Empty;

        return Marshal.PtrToStringUTF8((IntPtr)entry->value) ?? string.Empty;
    }

    private static readonly string[] SidecarSubtitleExtensions =
    {
        ".srt", ".vtt", ".webvtt", ".ass", ".ssa", ".sub", ".sbv", ".lrc", ".ttml", ".dfxp"
    };

    /// <summary>
    /// Finds external subtitle files next to a media path (same base name, common extensions).
    /// </summary>
    private static System.Collections.Generic.List<SubtitleTrackInfo> FindSidecarSubtitleTracks(string mediaPath)
    {
        var list = new System.Collections.Generic.List<SubtitleTrackInfo>();
        try
        {
            if (string.IsNullOrWhiteSpace(mediaPath) || !File.Exists(mediaPath))
                return list;

            string dir = Path.GetDirectoryName(mediaPath);
            string baseName = Path.GetFileNameWithoutExtension(mediaPath);
            if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(baseName))
                return list;

            foreach (string file in Directory.EnumerateFiles(dir, baseName + "*"))
            {
                string ext = Path.GetExtension(file);
                if (string.IsNullOrEmpty(ext))
                    continue;
                bool match = false;
                foreach (string allowed in SidecarSubtitleExtensions)
                {
                    if (ext.Equals(allowed, StringComparison.OrdinalIgnoreCase))
                    {
                        match = true;
                        break;
                    }
                }
                if (!match)
                    continue;

                // Avoid listing the media file itself if it somehow matches.
                if (string.Equals(file, mediaPath, StringComparison.OrdinalIgnoreCase))
                    continue;

                string codec = ext.TrimStart('.').ToLowerInvariant();
                if (codec == "webvtt")
                    codec = "vtt";

                // Language guess: video.en.srt → "en"
                string lang = string.Empty;
                string nameOnly = Path.GetFileNameWithoutExtension(file);
                if (nameOnly.Length > baseName.Length + 1
                    && nameOnly.StartsWith(baseName, StringComparison.OrdinalIgnoreCase))
                {
                    string suffix = nameOnly.Substring(baseName.Length).TrimStart('.', '_', '-');
                    if (suffix.Length >= 2 && suffix.Length <= 8)
                        lang = suffix;
                }

                list.Add(new SubtitleTrackInfo
                {
                    // Synthetic negative index for external tracks (unique per path hash).
                    StreamIndex = -1000 - list.Count,
                    Codec = codec,
                    Language = lang,
                    Title = Path.GetFileName(file),
                    IsTextBased = true,
                    ExternalFilePath = file
                });
            }
        }
        catch (Exception ex)
        {
            GD.PrintErr($"MediaEngine:FindSidecarSubtitleTracks - {ex.Message}");
        }

        return list;
    }

    private static bool IsLikelyBitmapSubtitleCodec(AVCodecID codecId, string codecName)
    {
        string c = (codecName ?? string.Empty).ToLowerInvariant();
        if (c.Contains("pgs") || c.Contains("dvd") || c.Contains("dvb") || c.Contains("xsub")
            || c.Contains("vobsub") || c.Contains("hdmv"))
            return true;

        // Compare by name so we don't hard-depend on every enum member existing.
        string idName = codecId.ToString();
        return idName.Contains("PGS", StringComparison.OrdinalIgnoreCase)
               || idName.Contains("DVD_SUBTITLE", StringComparison.OrdinalIgnoreCase)
               || idName.Contains("DVB_SUBTITLE", StringComparison.OrdinalIgnoreCase)
               || idName.Contains("XSUB", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Retrieves a readable error message from an FFmpeg return code.
    /// </summary>
    /// <param name="ret">The FFmpeg error code (negative value).</param>
    /// <returns>Error string, or "Unknown error" if unavailable.</returns>
    public static unsafe string GetFFmpegError(int ret)
    {
        byte[] buffer = new byte[1024];
        fixed (byte* buf = buffer)
        {
            ffmpeg.av_strerror(ret, buf, (ulong)buffer.Length);
            return Marshal.PtrToStringAnsi((IntPtr)buf) ?? "Unknown error";
        }
    }
}