// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using SDL3;

namespace Cue2.Domain.Playback;

/// <summary>
/// Contract for an active audio session bound to SDL device streams.
/// Source format describes decoder output; DeviceStreamChannels describes
/// the post-matrix channel count written into each SDL stream.
/// </summary>
public interface IAudioPlayback
{
    /// <summary>Audio output patch</summary>
    AudioOutputPatch Patch { get; set; }

    /// <summary>Direct audio output device name</summary>
    string DirectOutput { get; set; }

    /// <summary>Audio routing matrix with volumes</summary>
    CuePatch Routing { get; set; }

    /// <summary>SDL streams keyed by device logical ID.</summary>
    Dictionary<uint, IntPtr> DeviceStreams { get; set; }

    /// <summary>
    /// Per-device output channel counts matching the PCM written to each stream.
    /// Populated by <see cref="Cue2.Media.AudioDevices.StartAudioPlayback"/>.
    /// </summary>
    Dictionary<uint, int> DeviceStreamChannels { get; set; }

    /// <summary>Source (decoded) channel count.</summary>
    int SourceChannels { get; set; }

    /// <summary>Source sample rate in Hz.</summary>
    int SourceSampleRate { get; set; }

    /// <summary>Bytes per source sample-frame (channels * sizeof(float)).</summary>
    int SourceBytesPerFrame { get; set; }

    /// <summary>Source PCM format (always AudioF32LE).</summary>
    SDL.AudioFormat SourceFormat { get; set; }

    /// <summary>
    /// Called when an SDL output device used by this playback is disconnected (hot-unplug).
    /// Implementations must drop the stream for <paramref name="logicalDeviceId"/> and either
    /// continue on remaining devices or tear down the audio path when none remain.
    /// </summary>
    /// <param name="logicalDeviceId">SDL logical device id that was removed.</param>
    /// <remarks>
    /// <see cref="Cue2.Services.AudioDevices"/> removes this playback from its tracking map
    /// for that device before calling this method — do not assume the device is still open.
    /// </remarks>
    void OnOutputDeviceLost(uint logicalDeviceId);
}
