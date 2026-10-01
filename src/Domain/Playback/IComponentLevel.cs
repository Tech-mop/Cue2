// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System;
using System.Threading;

namespace Cue2.Domain.Playback;

/// <summary>
/// Peak level for the thin meter on an active-cue component row. Read on the UI thread.
/// </summary>
public interface IComponentLevel
{
    /// <summary>
    /// Peak of samples sent to the output since the last read, held with a short fall.
    /// </summary>
    /// <param name="deltaSeconds">UI tick length used for fall.</param>
    /// <returns>Linear amplitude 0…1.</returns>
    float ReadDisplayLevel(float deltaSeconds);
}

/// <summary>
/// Thread-safe peak hold for a playback fill thread and a UI reader.
/// </summary>
internal static class PlaybackLevel
{
    /// <summary>Display fall time, matching input meters.</summary>
    public const float FallSeconds = 0.28f;

    /// <summary>
    /// Raises the held peak if <paramref name="samples"/> is louder. Safe to call from the fill thread.
    /// </summary>
    /// <param name="peakBits">IEEE bits of the current peak, stored as an int for <see cref="Interlocked"/>.</param>
    /// <param name="samples">Interleaved float32 PCM that was just sent to the device.</param>
    public static void Note(ref int peakBits, ReadOnlySpan<float> samples)
    {
        float peak = 0f;
        for (int i = 0; i < samples.Length; i++)
        {
            float abs = MathF.Abs(samples[i]);
            if (abs > peak)
                peak = abs;
        }

        if (peak > 1f)
            peak = 1f;
        if (peak <= 0f)
            return;

        int next = BitConverter.SingleToInt32Bits(peak);
        int current;
        do
        {
            current = Volatile.Read(ref peakBits);
            float have = BitConverter.Int32BitsToSingle(current);
            if (peak <= have)
                return;
        }
        while (Interlocked.CompareExchange(ref peakBits, next, current) != current);
    }

    /// <summary>
    /// Takes the held instant peak and decays the display value. Call from the UI thread.
    /// </summary>
    /// <param name="peakBits">IEEE bits of the current peak.</param>
    /// <param name="display">In-out display value.</param>
    /// <param name="deltaSeconds">Time since the last read.</param>
    /// <returns>Decayed display peak 0…1.</returns>
    public static float Read(ref int peakBits, ref float display, float deltaSeconds)
    {
        int raw = Interlocked.Exchange(ref peakBits, 0);
        float instant = BitConverter.Int32BitsToSingle(raw);
        if (float.IsNaN(instant) || instant < 0f)
            instant = 0f;
        else if (instant > 1f)
            instant = 1f;

        float decay = MathF.Exp(-MathF.Max(0f, deltaSeconds) / FallSeconds);
        display = MathF.Max(instant, display * decay);
        return display;
    }
}
