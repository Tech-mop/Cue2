// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System;
using Cue2.Domain.Cuelist;
using Cue2.Domain.Devices;
using Cue2.Media.Audio;
using Cue2.Services;

namespace Cue2.Domain.Playback;

/// <summary>
/// Raised-cosine fade-in applied to the first PCM pushed after silence (start, seek, resume)
/// so it does not click. Plain counters, owned by one playback; armed on the main thread and
/// consumed by that playback's fill worker, as before.
/// </summary>
internal struct DeclickRamp
{
    /// <summary>Frames left in the ramp (0 = inactive).</summary>
    public int Remaining;

    /// <summary>Total frames of the current ramp (fixed when armed).</summary>
    public int Total;

    /// <summary>Starts a ramp of <paramref name="rampMs"/>, or clears it when either value is 0.</summary>
    /// <param name="sampleRate">Source sample rate in Hz.</param>
    /// <param name="rampMs">Ramp length in milliseconds.</param>
    public void Arm(int sampleRate, int rampMs)
    {
        if (sampleRate <= 0 || rampMs <= 0)
        {
            Remaining = 0;
            Total = 0;
            return;
        }

        Total = Math.Max(1, sampleRate * rampMs / 1000);
        Remaining = Total;
    }

    /// <summary>Applies the ramp gain to interleaved samples in place and advances it.</summary>
    /// <param name="interleaved">Interleaved float samples.</param>
    /// <param name="frames">Frames in <paramref name="interleaved"/>.</param>
    /// <param name="channels">Channels per frame.</param>
    public void Apply(Span<float> interleaved, int frames, int channels)
    {
        if (Remaining <= 0 || frames <= 0 || channels <= 0 || Total <= 0)
            return;

        int total = Total;
        for (int f = 0; f < frames && Remaining > 0; f++)
        {
            int progressed = total - Remaining;
            float t = (progressed + 1) / (float)total;
            if (t > 1f) t = 1f;
            // Raised cosine: smooth 0 to 1 with zero slope at both ends (fewer clicks than linear).
            float gain = 0.5f * (1f - MathF.Cos(MathF.PI * t));
            int baseIdx = f * channels;
            for (int c = 0; c < channels; c++)
                interleaved[baseIdx + c] *= gain;
            Remaining--;
        }
    }
}

/// <summary>
/// Output stage shared by the file and video playbacks: mixes one block of source PCM into
/// every bound SDL stream with the same de-click envelope, applies the show output limits,
/// feeds the level meter and queues the result. Runs on the playback's fill worker.
/// </summary>
internal static class AudioOutputMix
{
    /// <summary>Mixes and queues one block to every binding (see class summary).</summary>
    /// <param name="streamBindings">The playback's stream binding set (does the guarded Put).</param>
    /// <param name="bindings">Binding snapshot taken by the caller (not empty).</param>
    /// <param name="source">Interleaved source PCM, <paramref name="frames"/> x <paramref name="sourceChannels"/>.</param>
    /// <param name="frames">Frames in this block.</param>
    /// <param name="sourceChannels">Source channel count (also used when a binding has none).</param>
    /// <param name="mixBuffer">Per-playback scratch buffer; grown when too small.</param>
    /// <param name="masterVol">Cue fade envelope times session master.</param>
    /// <param name="componentVol">Component level.</param>
    /// <param name="pan">Stereo pan (ignored for other layouts).</param>
    /// <param name="routing">Cue routing.</param>
    /// <param name="patch">Output patch.</param>
    /// <param name="isDirect">True for a direct output.</param>
    /// <param name="declick">The playback's de-click ramp; every device gets the same envelope and it advances once.</param>
    /// <param name="audioDevices">Source of the show output limits (may be null).</param>
    /// <param name="meterPeakBits">The playback's level meter peak.</param>
    public static unsafe void MixAndPut(
        AudioStreamBindingSet streamBindings,
        AudioStreamBinding[] bindings,
        ReadOnlySpan<float> source,
        int frames,
        int sourceChannels,
        ref float[] mixBuffer,
        float masterVol,
        float componentVol,
        float pan,
        CuePatch routing,
        AudioOutputPatch patch,
        bool isDirect,
        ref DeclickRamp declick,
        AudioDevices audioDevices,
        ref int meterPeakBits)
    {
        // Snapshot de-click state so a multi-device push uses the same ramp positions.
        DeclickRamp snapshot = declick;

        foreach (var b in bindings)
        {
            int outCh = b.Channels > 0 ? b.Channels : sourceChannels;
            int outSamples = frames * outCh;
            if (outSamples > mixBuffer.Length)
                mixBuffer = new float[outSamples];

            AudioMixMatrix.Mix(
                source,
                frames,
                sourceChannels,
                mixBuffer.AsSpan(0, outSamples),
                outCh,
                masterVol,
                componentVol,
                pan,
                routing,
                patch,
                b.DeviceName,
                isDirect);

            // Restore ramp counters per device so each stream gets the same envelope.
            declick = snapshot;
            declick.Apply(mixBuffer.AsSpan(0, outSamples), frames, outCh);

            // Peak clamp + silence floor (show Audio settings) before handing PCM to SDL.
            if (audioDevices != null)
            {
                audioDevices.GetOutputLimits(out float maxAbs, out float minAbs);
                AudioMixMatrix.ApplyOutputLimits(mixBuffer.AsSpan(0, outSamples), maxAbs, minAbs);
            }

            PlaybackLevel.Note(ref meterPeakBits, mixBuffer.AsSpan(0, outSamples));

            int byteCount = outSamples * sizeof(float);
            fixed (float* p = mixBuffer)
            {
                streamBindings.Put(b, (IntPtr)p, byteCount);
            }
        }

        // Advance de-click once for the whole multi-device write (all devices already applied it).
        if (snapshot.Remaining > 0)
            declick.Remaining = Math.Max(0, snapshot.Remaining - frames);
    }
}
