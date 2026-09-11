// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using FFmpeg.AutoGen;
using Godot;

namespace Cue2.Media.Decoders;

/// <summary>
/// Loads text-based subtitle / closed-caption cues from a media file stream via FFmpeg,
/// or from external SRT/VTT sidecars via a tolerant text parser.
/// </summary>
/// <remarks>
/// Opens a dedicated demuxer (independent of video/audio decoders), decodes the chosen
/// subtitle stream fully into timed cues, then answers <see cref="GetTextAtUs"/> queries
/// from the main-thread presentation clock. Bitmap subtitle codecs are not supported.
/// External SRT/VTT files fall back to a native parser when FFmpeg rejects non-strict timestamps
/// (e.g. <c>00:02.170</c> instead of <c>00:00:02,170</c>).
/// </remarks>
public sealed unsafe class SubtitleSourceDecoder : IDisposable
{
    private static readonly Regex AssTagRegex = new(@"\{.*?\}", RegexOptions.Compiled);
    private static readonly Regex HtmlTagRegex = new(@"<[^>]+>", RegexOptions.Compiled);

    /// <summary>
    /// Matches SRT/VTT timing lines in common variants:
    /// <c>00:00:02,170 --&gt; 00:00:04,136</c>, <c>00:02.170 --&gt; 00:04.136</c>, optional hours.
    /// </summary>
    private static readonly Regex TimingLineRegex = new(
        @"^\s*(?:(\d{1,2}):)?(\d{1,2}):(\d{1,2})[.,](\d{1,3})\s*-->\s*(?:(\d{1,2}):)?(\d{1,2}):(\d{1,2})[.,](\d{1,3})",
        RegexOptions.Compiled);

    private readonly List<SubtitleCueEntry> _cues = new();
    private bool _isDisposed;
    private int _lastCueIndex = -1;

    /// <summary>True after a successful <see cref="LoadAsync"/>.</summary>
    public bool IsLoaded { get; private set; }

    /// <summary>Number of loaded cues.</summary>
    public int CueCount => _cues.Count;

    /// <summary>
    /// Loads all cues from the given subtitle stream index in <paramref name="path"/>.
    /// </summary>
    /// <param name="path">Absolute media path.</param>
    /// <param name="streamIndex">Container stream index of a text-based subtitle track.</param>
    public Task LoadAsync(string path, int streamIndex)
    {
        return Task.Run(() => LoadInternal(path, streamIndex));
    }

    /// <summary>
    /// Loads cues from an external sidecar subtitle file (.srt, .vtt, .ass, …).
    /// </summary>
    /// <param name="subtitleFilePath">Absolute path to the subtitle file.</param>
    public Task LoadExternalAsync(string subtitleFilePath)
    {
        return Task.Run(() =>
        {
            if (string.IsNullOrWhiteSpace(subtitleFilePath) || !File.Exists(subtitleFilePath))
            {
                GD.PrintErr($"SubtitleSourceDecoder:LoadExternal - File not found: {subtitleFilePath}");
                _cues.Clear();
                IsLoaded = false;
                return;
            }

            // Prefer FFmpeg for strict/well-formed files; many real-world SRT/VTT variants fail open.
            LoadInternal(subtitleFilePath, preferredStreamIndex: -1, allowAnySubtitleStream: true);
            if (IsLoaded && _cues.Count > 0)
                return;

            if (TryLoadTextSubtitleFile(subtitleFilePath))
            {
                GD.Print(
                    $"SubtitleSourceDecoder:LoadExternal - Parsed {_cues.Count} cues via text parser " +
                    $"from {Path.GetFileName(subtitleFilePath)}");
                return;
            }

            GD.PrintErr(
                $"SubtitleSourceDecoder:LoadExternal - Failed FFmpeg and text parse for {subtitleFilePath}");
            IsLoaded = false;
        });
    }

    /// <summary>
    /// Returns the subtitle text active at <paramref name="mediaTimeUs"/>, or empty if none.
    /// </summary>
    /// <param name="mediaTimeUs">Master media clock in microseconds.</param>
    public string GetTextAtUs(long mediaTimeUs)
    {
        if (_cues.Count == 0)
            return string.Empty;

        // Fast path: still inside the last matched cue.
        if (_lastCueIndex >= 0 && _lastCueIndex < _cues.Count)
        {
            var last = _cues[_lastCueIndex];
            if (mediaTimeUs >= last.StartUs && mediaTimeUs < last.EndUs)
                return last.Text ?? string.Empty;
        }

        // Binary search for a cue covering mediaTimeUs.
        int lo = 0;
        int hi = _cues.Count - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            var cue = _cues[mid];
            if (mediaTimeUs < cue.StartUs)
                hi = mid - 1;
            else if (mediaTimeUs >= cue.EndUs)
                lo = mid + 1;
            else
            {
                _lastCueIndex = mid;
                return cue.Text ?? string.Empty;
            }
        }

        _lastCueIndex = -1;
        return string.Empty;
    }

    private void LoadInternal(string path, int preferredStreamIndex, bool allowAnySubtitleStream = false)
    {
        _cues.Clear();
        _lastCueIndex = -1;
        IsLoaded = false;

        if (string.IsNullOrWhiteSpace(path))
            return;
        if (!allowAnySubtitleStream && preferredStreamIndex < 0)
            return;

        AVFormatContext* formatCtx = null;
        AVCodecContext* codecCtx = null;
        AVPacket* packet = null;

        try
        {
            int ret = ffmpeg.avformat_open_input(&formatCtx, path, null, null);
            if (ret < 0)
                throw new Exception($"open_input failed: {MediaEngine.GetFFmpegError(ret)}");

            ret = ffmpeg.avformat_find_stream_info(formatCtx, null);
            if (ret < 0)
                throw new Exception($"find_stream_info failed: {MediaEngine.GetFFmpegError(ret)}");

            int streamIndex = preferredStreamIndex;
            if (allowAnySubtitleStream || streamIndex < 0 || streamIndex >= formatCtx->nb_streams)
            {
                streamIndex = -1;
                for (uint i = 0; i < formatCtx->nb_streams; i++)
                {
                    if (formatCtx->streams[i]->codecpar->codec_type == AVMediaType.AVMEDIA_TYPE_SUBTITLE)
                    {
                        streamIndex = (int)i;
                        break;
                    }
                }
            }

            if (streamIndex < 0 || streamIndex >= formatCtx->nb_streams)
                throw new Exception("No subtitle stream found in file.");

            AVStream* stream = formatCtx->streams[(uint)streamIndex];
            if (stream->codecpar->codec_type != AVMediaType.AVMEDIA_TYPE_SUBTITLE)
                throw new Exception($"Stream {streamIndex} is not a subtitle stream.");

            AVCodec* codec = ffmpeg.avcodec_find_decoder(stream->codecpar->codec_id);
            if (codec == null)
                throw new Exception($"No decoder for subtitle codec {stream->codecpar->codec_id}.");

            // For pure sidecar files, allow decode even if codec id is unfamiliar — still try.

            codecCtx = ffmpeg.avcodec_alloc_context3(codec);
            if (codecCtx == null)
                throw new Exception("avcodec_alloc_context3 failed.");

            ret = ffmpeg.avcodec_parameters_to_context(codecCtx, stream->codecpar);
            if (ret < 0)
                throw new Exception($"params_to_context failed: {MediaEngine.GetFFmpegError(ret)}");

            codecCtx->pkt_timebase = stream->time_base;
            ret = ffmpeg.avcodec_open2(codecCtx, codec, null);
            if (ret < 0)
                throw new Exception($"codec open failed: {MediaEngine.GetFFmpegError(ret)}");

            packet = ffmpeg.av_packet_alloc();
            if (packet == null)
                throw new Exception("av_packet_alloc failed.");

            AVRational timeBase = stream->time_base;
            long startTime = stream->start_time != ffmpeg.AV_NOPTS_VALUE ? stream->start_time : 0;

            while (ffmpeg.av_read_frame(formatCtx, packet) >= 0)
            {
                try
                {
                    if (packet->stream_index != streamIndex)
                        continue;

                    AVSubtitle subtitle;
                    int gotSub = 0;
                    ret = ffmpeg.avcodec_decode_subtitle2(codecCtx, &subtitle, &gotSub, packet);
                    if (ret < 0 || gotSub == 0)
                        continue;

                    try
                    {
                        string text = ExtractSubtitleText(&subtitle);
                        if (string.IsNullOrWhiteSpace(text))
                            continue;

                        long pts = packet->pts != ffmpeg.AV_NOPTS_VALUE
                            ? packet->pts
                            : (packet->dts != ffmpeg.AV_NOPTS_VALUE ? packet->dts : 0);
                        if (startTime != 0)
                            pts -= startTime;

                        long ptsUs = (long)(pts * ffmpeg.av_q2d(timeBase) * 1_000_000.0);
                        // start/end_display_time are milliseconds relative to PTS.
                        long startUs = ptsUs + subtitle.start_display_time * 1000L;
                        long endUs = ptsUs + subtitle.end_display_time * 1000L;
                        if (endUs <= startUs)
                            endUs = startUs + 2_000_000; // 2s fallback hold

                        _cues.Add(new SubtitleCueEntry
                        {
                            StartUs = startUs,
                            EndUs = endUs,
                            Text = text.Trim()
                        });
                    }
                    finally
                    {
                        ffmpeg.avsubtitle_free(&subtitle);
                    }
                }
                finally
                {
                    ffmpeg.av_packet_unref(packet);
                }
            }

            // Flush decoder
            {
                AVSubtitle subtitle;
                int gotSub = 0;
                ffmpeg.avcodec_decode_subtitle2(codecCtx, &subtitle, &gotSub, null);
                if (gotSub != 0)
                {
                    try
                    {
                        string text = ExtractSubtitleText(&subtitle);
                        if (!string.IsNullOrWhiteSpace(text))
                        {
                            _cues.Add(new SubtitleCueEntry
                            {
                                StartUs = 0,
                                EndUs = 2_000_000,
                                Text = text.Trim()
                            });
                        }
                    }
                    finally
                    {
                        ffmpeg.avsubtitle_free(&subtitle);
                    }
                }
            }

            _cues.Sort((a, b) => a.StartUs.CompareTo(b.StartUs));
            IsLoaded = _cues.Count > 0;
            GD.Print($"SubtitleSourceDecoder:Load - stream={streamIndex} cues={_cues.Count}");
        }
        catch (Exception ex)
        {
            GD.PrintErr($"SubtitleSourceDecoder:Load - {ex.Message}");
            _cues.Clear();
            IsLoaded = false;
        }
        finally
        {
            if (packet != null)
                ffmpeg.av_packet_free(&packet);
            if (codecCtx != null)
                ffmpeg.avcodec_free_context(&codecCtx);
            if (formatCtx != null)
                ffmpeg.avformat_close_input(&formatCtx);
        }
    }

    private static string ExtractSubtitleText(AVSubtitle* subtitle)
    {
        if (subtitle == null || subtitle->num_rects == 0 || subtitle->rects == null)
            return string.Empty;

        var sb = new StringBuilder();
        for (uint i = 0; i < subtitle->num_rects; i++)
        {
            AVSubtitleRect* rect = subtitle->rects[i];
            if (rect == null)
                continue;

            string piece = null;
            if (rect->type == AVSubtitleType.SUBTITLE_TEXT && rect->text != null)
                piece = PtrToUtf8((IntPtr)rect->text);
            else if (rect->type == AVSubtitleType.SUBTITLE_ASS && rect->ass != null)
                piece = CleanAssDialogue(PtrToUtf8((IntPtr)rect->ass));

            if (string.IsNullOrWhiteSpace(piece))
                continue;

            if (sb.Length > 0)
                sb.Append('\n');
            sb.Append(piece.Trim());
        }

        return sb.ToString();
    }

    /// <summary>
    /// Parses SRT / WebVTT-like text files without FFmpeg. Tolerates short timestamps
    /// (<c>mm:ss.mmm</c>) and comma/period millisecond separators.
    /// </summary>
    private bool TryLoadTextSubtitleFile(string path)
    {
        _cues.Clear();
        _lastCueIndex = -1;
        IsLoaded = false;

        try
        {
            // UTF-8 with BOM fallback to default ANSI for older SRT dumps.
            string text;
            try
            {
                text = File.ReadAllText(path, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true));
            }
            catch
            {
                text = File.ReadAllText(path);
            }

            if (string.IsNullOrWhiteSpace(text))
                return false;

            // Normalize newlines; strip BOM / WEBVTT header noise.
            text = text.TrimStart('\uFEFF').Replace("\r\n", "\n").Replace('\r', '\n');
            var lines = text.Split('\n');

            long startUs = 0;
            long endUs = 0;
            bool haveTiming = false;
            var body = new StringBuilder();

            void Flush()
            {
                if (!haveTiming)
                {
                    body.Clear();
                    return;
                }

                string cueText = body.ToString().Trim();
                body.Clear();
                haveTiming = false;
                if (string.IsNullOrEmpty(cueText))
                    return;

                cueText = HtmlTagRegex.Replace(cueText, string.Empty);
                if (endUs <= startUs)
                    endUs = startUs + 2_000_000;

                _cues.Add(new SubtitleCueEntry
                {
                    StartUs = startUs,
                    EndUs = endUs,
                    Text = cueText
                });
            }

            foreach (string rawLine in lines)
            {
                string line = rawLine.TrimEnd();
                string trimmed = line.Trim();

                // Skip WebVTT preamble / NOTE blocks / STYLE (simple skip of known headers).
                if (trimmed.StartsWith("WEBVTT", StringComparison.OrdinalIgnoreCase)
                    || trimmed.StartsWith("NOTE", StringComparison.OrdinalIgnoreCase)
                    || trimmed.StartsWith("STYLE", StringComparison.OrdinalIgnoreCase)
                    || trimmed.StartsWith("REGION", StringComparison.OrdinalIgnoreCase)
                    || trimmed.StartsWith("X-TIMESTAMP", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (string.IsNullOrWhiteSpace(trimmed))
                {
                    Flush();
                    continue;
                }

                if (TryParseTimingLine(trimmed, out long s, out long e))
                {
                    // New cue timing — flush previous.
                    Flush();
                    startUs = s;
                    endUs = e;
                    haveTiming = true;
                    continue;
                }

                // Pure index lines ("1", "12") between cues — ignore only when not collecting body.
                if (!haveTiming && int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
                    continue;

                if (haveTiming)
                {
                    if (body.Length > 0)
                        body.Append('\n');
                    body.Append(trimmed);
                }
            }

            Flush();
            _cues.Sort((a, b) => a.StartUs.CompareTo(b.StartUs));
            IsLoaded = _cues.Count > 0;
            return IsLoaded;
        }
        catch (Exception ex)
        {
            GD.PrintErr($"SubtitleSourceDecoder:TryLoadTextSubtitleFile - {ex.Message}");
            _cues.Clear();
            IsLoaded = false;
            return false;
        }
    }

    /// <summary>
    /// Parses a timing line into start/end microseconds.
    /// Supports <c>HH:MM:SS,mmm</c>, <c>HH:MM:SS.mmm</c>, <c>MM:SS.mmm</c>.
    /// </summary>
    private static bool TryParseTimingLine(string line, out long startUs, out long endUs)
    {
        startUs = 0;
        endUs = 0;
        var m = TimingLineRegex.Match(line);
        if (!m.Success)
            return false;

        startUs = PartsToUs(m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value, m.Groups[4].Value);
        endUs = PartsToUs(m.Groups[5].Value, m.Groups[6].Value, m.Groups[7].Value, m.Groups[8].Value);
        return true;
    }

    private static long PartsToUs(string hours, string minutes, string seconds, string frac)
    {
        int h = string.IsNullOrEmpty(hours) ? 0 : int.Parse(hours, CultureInfo.InvariantCulture);
        int min = int.Parse(minutes, CultureInfo.InvariantCulture);
        int sec = int.Parse(seconds, CultureInfo.InvariantCulture);
        // Normalize fractional part to milliseconds (1–3 digits).
        if (frac.Length == 1) frac += "00";
        else if (frac.Length == 2) frac += "0";
        else if (frac.Length > 3) frac = frac.Substring(0, 3);
        int ms = int.Parse(frac, CultureInfo.InvariantCulture);
        return ((long)h * 3600L + min * 60L + sec) * 1_000_000L + ms * 1000L;
    }

    /// <summary>
    /// Strips ASS dialogue prefixes and override tags to plain text.
    /// </summary>
    public static string CleanAssDialogue(string ass)
    {
        if (string.IsNullOrWhiteSpace(ass))
            return string.Empty;

        string text = ass;
        // Dialogue: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text
        if (text.StartsWith("Dialogue:", StringComparison.OrdinalIgnoreCase))
        {
            int commas = 0;
            int idx = 0;
            for (; idx < text.Length; idx++)
            {
                if (text[idx] == ',')
                {
                    commas++;
                    if (commas == 9)
                    {
                        idx++;
                        break;
                    }
                }
            }
            if (commas >= 9 && idx < text.Length)
                text = text.Substring(idx);
        }

        text = AssTagRegex.Replace(text, string.Empty);
        text = text.Replace("\\N", "\n", StringComparison.Ordinal)
                   .Replace("\\n", "\n", StringComparison.Ordinal)
                   .Replace("\\h", " ", StringComparison.Ordinal);
        text = HtmlTagRegex.Replace(text, string.Empty);
        return text.Trim();
    }

    private static string PtrToUtf8(IntPtr ptr)
    {
        if (ptr == IntPtr.Zero)
            return string.Empty;
        return Marshal.PtrToStringUTF8(ptr) ?? string.Empty;
    }

    /// <summary>
    /// True when FFmpeg codec id is a text subtitle format we can extract.
    /// </summary>
    public static bool IsTextBasedCodecId(AVCodecID codecId)
    {
        return codecId switch
        {
            AVCodecID.AV_CODEC_ID_SUBRIP => true,
            AVCodecID.AV_CODEC_ID_SRT => true,
            AVCodecID.AV_CODEC_ID_ASS => true,
            AVCodecID.AV_CODEC_ID_SSA => true,
            AVCodecID.AV_CODEC_ID_WEBVTT => true,
            AVCodecID.AV_CODEC_ID_MOV_TEXT => true,
            AVCodecID.AV_CODEC_ID_TEXT => true,
            AVCodecID.AV_CODEC_ID_TTML => true,
            AVCodecID.AV_CODEC_ID_EIA_608 => true,
            _ => false
        };
    }

    /// <summary>
    /// Best-effort text-based check from codec name string (metadata path).
    /// </summary>
    public static bool IsTextBasedCodecName(string codecName)
    {
        if (string.IsNullOrWhiteSpace(codecName))
            return false;
        string c = codecName.Trim().ToLowerInvariant();
        return c is "subrip" or "srt" or "ass" or "ssa" or "webvtt" or "mov_text"
            or "text" or "ttml" or "eia_608" or "cc_dec" or "timed_id3";
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_isDisposed)
            return;
        _isDisposed = true;
        _cues.Clear();
        IsLoaded = false;
    }
}
