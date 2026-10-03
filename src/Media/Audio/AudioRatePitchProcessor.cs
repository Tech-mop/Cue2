// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System;
using System.Threading;
using Cue2.Domain.Cues;
using Cue2.Media.Decoders;

namespace Cue2.Media.Audio;

/// <summary>
/// Streaming play-rate and pitch processor for file audio.
/// </summary>
/// <remarks>
/// Vinyl path (<c>KeepPitch</c> off): cubic resample by rate × cents, which changes
/// both speed and pitch. Keep-pitch path: WSOLA time-stretch for rate, then resample
/// for extra cents so duration follows rate only.
/// </remarks>
public sealed class AudioRatePitchProcessor
{
    private const float UnityEps = 1.0004f;
    private const int MinWindow = 256;
    private const int MaxWindow = 4096;

    private int _channels;
    private int _sampleRate;

    private float[] _src = Array.Empty<float>();
    private int _srcFrames;
    private double _srcPhase;

    private float[] _stretchIn = Array.Empty<float>();
    private int _stretchFrames;

    private float[] _ola = Array.Empty<float>();
    private int _olaFrames;

    private float[] _prevOverlap = Array.Empty<float>();
    private bool _hasPrevOverlap;

    private float[] _pull = Array.Empty<float>();
    private float[] _hopScratch = Array.Empty<float>();

    private int _win;
    private int _hopOut;
    private int _overlap;
    private int _seek;

    /// <summary>
    /// Clears overlap and FIFO state (call on seek, loop, and format change).
    /// </summary>
    public void Reset()
    {
        _srcFrames = 0;
        _srcPhase = 0;
        _stretchFrames = 0;
        _olaFrames = 0;
        _hasPrevOverlap = false;
    }

    /// <summary>
    /// Prepares channel count, sample rate, and WSOLA sizes.
    /// </summary>
    /// <param name="channels">Interleaved source channel count.</param>
    /// <param name="sampleRate">Source sample rate in Hz.</param>
    public void Configure(int channels, int sampleRate)
    {
        if (channels < 1)
            channels = 1;
        if (sampleRate < 8000)
            sampleRate = 8000;

        if (channels == _channels && sampleRate == _sampleRate && _win > 0)
            return;

        _channels = channels;
        _sampleRate = sampleRate;
        _win = Math.Clamp(sampleRate * 40 / 1000, MinWindow, MaxWindow);
        _overlap = Math.Clamp(sampleRate * 8 / 1000, 32, _win / 2);
        _hopOut = Math.Max(32, _win - _overlap);
        _seek = Math.Clamp(sampleRate * 12 / 1000, 32, _win / 2);

        Ensure(ref _prevOverlap, _overlap * _channels);

        Reset();
    }

    /// <summary>
    /// Pitch ratio from extra cents. 0 cents → 1.
    /// </summary>
    public static float CentsToRatio(float cents)
    {
        cents = AudioComponent.ClampPitchCents(cents);
        if (MathF.Abs(cents) < 0.01f)
            return 1f;
        return MathF.Pow(2f, cents / 1200f);
    }

    /// <summary>
    /// Combined pitch ratio applied after keep-pitch (rate × cents, or cents only).
    /// </summary>
    public static float PitchRatio(double playRate, bool keepPitch, float cents)
    {
        float centsRatio = CentsToRatio(cents);
        if (keepPitch)
            return centsRatio;
        return (float)(AudioComponent.ClampPlayRate(playRate) * centsRatio);
    }

    /// <summary>
    /// Writes up to <paramref name="outFrames"/> processed frames into <paramref name="destination"/>.
    /// </summary>
    /// <returns>Frames written (0 at source end and empty FIFOs).</returns>
    public int Process(
        AudioSourceDecoder decoder,
        Span<float> destination,
        int outFrames,
        double playRate,
        bool keepPitch,
        float pitchCents,
        CancellationToken ct = default)
    {
        if (decoder == null || outFrames <= 0 || _channels < 1)
            return 0;

        playRate = AudioComponent.ClampPlayRate(playRate);
        float pitch = PitchRatio(playRate, keepPitch, pitchCents);
        bool unityPitch = pitch > 1f / UnityEps && pitch < UnityEps;
        bool unityRate = playRate > 1.0 / UnityEps && playRate < UnityEps;
        bool vinyl = !keepPitch;

        if (unityPitch && unityRate && _srcFrames == 0 && _stretchFrames == 0 && _olaFrames == 0)
            return decoder.Read(destination, outFrames, ct);

        int written = 0;
        while (written < outFrames)
        {
            if (ct.IsCancellationRequested)
                break;

            int want = outFrames - written;
            int got;
            if (vinyl || (unityPitch && unityRate))
            {
                // Vinyl / bypass: one cubic resample (identity when both ratios are 1).
                float step = vinyl ? pitch : 1f;
                got = ResampleTo(decoder, destination.Slice(written * _channels), want, step, ct);
            }
            else
            {
                got = StretchTo(decoder, destination.Slice(written * _channels), want, playRate, pitch, ct);
            }

            if (got <= 0)
                break;
            written += got;
        }

        return written;
    }

    private int ResampleTo(
        AudioSourceDecoder decoder,
        Span<float> dest,
        int outFrames,
        float step,
        CancellationToken ct)
    {
        if (step < 1f / 64f)
            step = 1f / 64f;

        int written = 0;
        while (written < outFrames)
        {
            int needed = (int)Math.Ceiling(_srcPhase + step * (outFrames - written) + 4);
            if (!EnsureSourceFrames(decoder, needed, ct))
            {
                if (_srcFrames < 4)
                    break;
            }

            int available = _srcFrames;
            if (available < 4)
                break;

            int maxOut = (int)Math.Floor((available - 3 - _srcPhase) / step);
            if (maxOut <= 0)
            {
                CompactSrc();
                if (!EnsureSourceFrames(decoder, needed, ct))
                    break;
                available = _srcFrames;
                maxOut = (int)Math.Floor((available - 3 - _srcPhase) / step);
                if (maxOut <= 0)
                    break;
            }

            int n = Math.Min(outFrames - written, maxOut);
            int ch = _channels;
            for (int i = 0; i < n; i++)
            {
                double pos = _srcPhase + i * (double)step;
                int i0 = (int)pos;
                float t = (float)(pos - i0);
                int dst = (written + i) * ch;
                HermiteFrame(_src, i0, t, dest, dst, ch);
            }

            _srcPhase += n * (double)step;
            int consumed = (int)_srcPhase;
            if (consumed > 0)
            {
                _srcPhase -= consumed;
                ShiftFrames(ref _src, ref _srcFrames, consumed, ch);
            }

            written += n;
        }

        return written;
    }

    private int StretchTo(
        AudioSourceDecoder decoder,
        Span<float> dest,
        int outFrames,
        double playRate,
        float pitch,
        CancellationToken ct)
    {
        int copied = DrainOla(dest, outFrames);
        while (copied < outFrames)
        {
            if (!ProduceHop(decoder, playRate, pitch, ct))
                break;
            copied += DrainOla(dest.Slice(copied * _channels), outFrames - copied);
        }

        return copied;
    }

    private int DrainOla(Span<float> dest, int outFrames)
    {
        if (_olaFrames <= 0 || outFrames <= 0)
            return 0;

        int n = Math.Min(outFrames, _olaFrames);
        int samples = n * _channels;
        _ola.AsSpan(0, samples).CopyTo(dest);
        ShiftFrames(ref _ola, ref _olaFrames, n, _channels);
        return n;
    }

    private bool ProduceHop(AudioSourceDecoder decoder, double playRate, float pitch, CancellationToken ct)
    {
        float sigma = (float)(pitch / playRate);
        if (sigma < 1f / 64f)
            sigma = 1f / 64f;

        int hopIn = Math.Max(1, (int)Math.Round(_hopOut / sigma));
        int need = hopIn + _win + _seek + 4;
        if (!FillStretch(decoder, need, pitch, ct))
        {
            if (_stretchFrames <= 0)
                return false;
            AppendOlaFromStretch(_stretchFrames);
            _stretchFrames = 0;
            _hasPrevOverlap = false;
            return _olaFrames > 0;
        }

        int offset = _hasPrevOverlap ? BestOffset() : 0;
        int start = Math.Clamp(offset, 0, Math.Max(0, _stretchFrames - _win));
        if (_stretchFrames - start < _hopOut + _overlap)
        {
            AppendOlaFromStretch(_stretchFrames);
            _stretchFrames = 0;
            _hasPrevOverlap = false;
            return _olaFrames > 0;
        }

        int ch = _channels;
        int olaStart = _olaFrames;
        Ensure(ref _ola, (olaStart + _hopOut) * ch);

        for (int f = 0; f < _hopOut; f++)
        {
            int src = (start + f) * ch;
            int dst = (olaStart + f) * ch;
            bool mix = _hasPrevOverlap && f < _overlap;
            float fade = _overlap <= 1 ? 1f : f / (float)(_overlap - 1);
            float wIn = mix ? 0.5f - 0.5f * MathF.Cos(MathF.PI * fade) : 1f;
            float wPrev = mix ? 1f - wIn : 0f;
            for (int c = 0; c < ch; c++)
            {
                float incoming = _stretchIn[src + c];
                _ola[dst + c] = mix
                    ? _prevOverlap[f * ch + c] * wPrev + incoming * wIn
                    : incoming;
            }
        }

        _olaFrames = olaStart + _hopOut;

        int ovSamples = _overlap * ch;
        Ensure(ref _prevOverlap, ovSamples);
        Array.Copy(_stretchIn, (start + _hopOut) * ch, _prevOverlap, 0, ovSamples);
        _hasPrevOverlap = true;

        int consume = Math.Min(hopIn, _stretchFrames);
        ShiftFrames(ref _stretchIn, ref _stretchFrames, consume, ch);
        return true;
    }

    private int BestOffset()
    {
        int ch = _channels;
        int best = 0;
        float bestCost = float.MaxValue;
        int maxOff = Math.Min(_seek, Math.Max(0, _stretchFrames - _overlap));
        int ov = _overlap;
        for (int off = 0; off <= maxOff; off++)
        {
            float cost = 0f;
            int src = off * ch;
            for (int f = 0; f < ov; f++)
            {
                int a = f * ch;
                int b = src + a;
                for (int c = 0; c < ch; c++)
                {
                    float d = _prevOverlap[a + c] - _stretchIn[b + c];
                    cost += d >= 0 ? d : -d;
                }
            }

            if (cost < bestCost)
            {
                bestCost = cost;
                best = off;
            }
        }

        return best;
    }

    private bool FillStretch(AudioSourceDecoder decoder, int needFrames, float pitch, CancellationToken ct)
    {
        while (_stretchFrames < needFrames)
        {
            int want = needFrames - _stretchFrames;
            Ensure(ref _hopScratch, want * _channels);
            int got = ResampleTo(decoder, _hopScratch.AsSpan(0, want * _channels), want, pitch, ct);
            if (got <= 0)
                return _stretchFrames >= _overlap;
            Ensure(ref _stretchIn, (_stretchFrames + got) * _channels);
            Array.Copy(_hopScratch, 0, _stretchIn, _stretchFrames * _channels, got * _channels);
            _stretchFrames += got;
        }

        return true;
    }

    private bool EnsureSourceFrames(AudioSourceDecoder decoder, int needFrames, CancellationToken ct)
    {
        while (_srcFrames < needFrames)
        {
            int want = Math.Max(256, needFrames - _srcFrames);
            Ensure(ref _pull, want * _channels);
            int got = decoder.Read(_pull.AsSpan(), want, ct);
            if (got <= 0)
                return _srcFrames > 0;
            Ensure(ref _src, (_srcFrames + got) * _channels);
            Array.Copy(_pull, 0, _src, _srcFrames * _channels, got * _channels);
            _srcFrames += got;
        }

        return true;
    }

    private void CompactSrc()
    {
        int drop = (int)_srcPhase;
        if (drop <= 0)
            return;
        _srcPhase -= drop;
        ShiftFrames(ref _src, ref _srcFrames, drop, _channels);
    }

    private void AppendOlaFromStretch(int frames)
    {
        if (frames <= 0)
            return;
        int ch = _channels;
        Ensure(ref _ola, (_olaFrames + frames) * ch);
        Array.Copy(_stretchIn, 0, _ola, _olaFrames * ch, frames * ch);
        _olaFrames += frames;
    }

    private static void HermiteFrame(float[] src, int i0, float t, Span<float> dest, int dst, int ch)
    {
        int iM1 = Math.Max(0, i0 - 1);
        int i1 = i0 + 1;
        int i2 = i0 + 2;
        int b0 = i0 * ch;
        int bm = iM1 * ch;
        int b1 = i1 * ch;
        int b2 = i2 * ch;
        for (int c = 0; c < ch; c++)
        {
            float ym = src[bm + c];
            float y0 = src[b0 + c];
            float y1 = src[b1 + c];
            float y2 = src[b2 + c];
            float c0 = y0;
            float c1 = 0.5f * (y1 - ym);
            float c2 = ym - 2.5f * y0 + 2f * y1 - 0.5f * y2;
            float c3 = 0.5f * (y2 - ym) + 1.5f * (y0 - y1);
            dest[dst + c] = ((c3 * t + c2) * t + c1) * t + c0;
        }
    }

    private static void ShiftFrames(ref float[] buf, ref int frames, int drop, int ch)
    {
        if (drop <= 0)
            return;
        if (drop >= frames)
        {
            frames = 0;
            return;
        }

        int remain = (frames - drop) * ch;
        Array.Copy(buf, drop * ch, buf, 0, remain);
        frames -= drop;
    }

    private static void Ensure(ref float[] buf, int samples)
    {
        if (buf.Length >= samples)
            return;
        int cap = buf.Length == 0 ? 256 : buf.Length;
        while (cap < samples)
            cap *= 2;
        Array.Resize(ref buf, cap);
    }
}
