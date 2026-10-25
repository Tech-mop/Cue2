// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System;

namespace Cue2.Domain.Timecode;

/// <summary>
/// Decodes SMPTE Linear Timecode from a mono float PCM stream (biphase mark).
/// </summary>
public sealed class LtcDecoder
{
    private const float Hysteresis = 0.012f;
    private const int BitHistory = 160;

    private bool _high;
    private int _samplesSinceCrossing;
    private int _halfBitSamples = 10;
    private int _halfRun;
    private readonly byte[] _bits = new byte[BitHistory];
    private int _bitWrite;
    private int _bitsFilled;
    private TimecodeFrameRate _hint = TimecodeFrameRate.Fps30;
    private long _bitCount;
    private long _lastSyncBit = -1;

    /// <summary>
    /// Pushes interleaved-extracted mono samples and returns a frame when the sync word is found.
    /// </summary>
    /// <param name="samples">Mono PCM, roughly −1…1.</param>
    /// <param name="sampleRate">Capture sample rate.</param>
    /// <param name="preferredRate">Endpoint frame-rate hint.</param>
    /// <param name="value">Latest decoded frame.</param>
    /// <returns><see langword="true"/> when a new valid frame was assembled.</returns>
    public bool Push(ReadOnlySpan<float> samples, int sampleRate, TimecodeFrameRate preferredRate, out TimecodeValue value)
    {
        value = default;
        if (samples.Length == 0 || sampleRate <= 0)
            return false;

        _hint = preferredRate;
        double fps = preferredRate.RealFramesPerSecond();
        int expectedHalf = Math.Max(2, (int)Math.Round(sampleRate / (fps * 80.0) / 2.0));
        if (_halfBitSamples < 2)
            _halfBitSamples = expectedHalf;

        bool decoded = false;
        TimecodeValue latest = default;

        for (int i = 0; i < samples.Length; i++)
        {
            float s = samples[i];
            _samplesSinceCrossing++;
            bool crossing = false;
            if (_high)
            {
                if (s < -Hysteresis)
                {
                    _high = false;
                    crossing = true;
                }
            }
            else if (s > Hysteresis)
            {
                _high = true;
                crossing = true;
            }

            if (!crossing)
                continue;

            int interval = _samplesSinceCrossing;
            _samplesSinceCrossing = 0;
            if (interval < 2)
                continue;

            int half = _halfBitSamples;
            if (interval > half * 4)
            {
                // Dropout or silence: the next sync word only sets a fresh starting point.
                _halfRun = 0;
                _lastSyncBit = -1;
                continue;
            }

            bool isHalf = interval < (half * 3) / 2;
            if (isHalf)
            {
                _halfBitSamples = Math.Clamp((_halfBitSamples * 3 + interval) / 4, 2, expectedHalf * 3);
                _halfRun++;
                if (_halfRun >= 2)
                {
                    PushBit(1);
                    _halfRun = 0;
                    if (TryReadFrame(out var frame))
                    {
                        latest = frame;
                        decoded = true;
                    }
                }
            }
            else
            {
                _halfBitSamples = Math.Clamp((_halfBitSamples * 3 + interval / 2) / 4, 2, expectedHalf * 3);
                _halfRun = 0;
                PushBit(0);
                if (TryReadFrame(out var frame))
                {
                    latest = frame;
                    decoded = true;
                }
            }
        }

        if (decoded)
            value = latest;
        return decoded;
    }

    /// <summary>Clears crossing and bit state (device change).</summary>
    public void Reset()
    {
        _high = false;
        _samplesSinceCrossing = 0;
        _halfRun = 0;
        _bitWrite = 0;
        _bitsFilled = 0;
        _bitCount = 0;
        _lastSyncBit = -1;
        Array.Clear(_bits);
    }

    private void PushBit(byte bit)
    {
        _bits[_bitWrite] = (byte)(bit & 1);
        _bitWrite = (_bitWrite + 1) % BitHistory;
        if (_bitsFilled < BitHistory)
            _bitsFilled++;
        _bitCount++;
    }

    private bool TryReadFrame(out TimecodeValue value)
    {
        value = default;
        if (_bitsFilled < 80)
            return false;

        // A frame ends with its sync word, so it is complete exactly when the newest 80 bits
        // hold it. Older offsets would mix bits from two frames and give wrong times.
        Span<byte> window = stackalloc byte[80];
        CopyWindow(window);
        if (!LtcEncoder.TryUnpack(window, _hint, out value))
            return false;

        // Report a frame only when the previous sync word was exactly one frame earlier.
        // The first frame after start, a dropout or a bit slip may begin with stale bits,
        // so it only sets the starting point.
        bool followsLastFrame = _lastSyncBit >= 0 && _bitCount - _lastSyncBit == 80;
        _lastSyncBit = _bitCount;
        return followsLastFrame;
    }

    private void CopyWindow(Span<byte> window)
    {
        int start = _bitWrite - 80;
        if (start < 0)
            start += BitHistory;
        for (int i = 0; i < 80; i++)
            window[i] = _bits[(start + i) % BitHistory];
    }
}
