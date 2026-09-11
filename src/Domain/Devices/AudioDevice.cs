// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using Godot;
using SDL3;

namespace Cue2.Domain.Devices;

public class AudioDevice : IDevice
{
    private static int _nextId = 0;
    public int DeviceId { get; }
    public string Name { get; set; }
    
    public uint PhysicalId { get; set; }
    
    public uint LogicalId { get; set; }
    
    public int Channels { get; set; } = -1;
    public int SampleRate { get; set; } = -1;
    public int BitDepth { get; set; } = -1;

    /// <summary>
    /// Playback (output) channel count from the device format, or 0 if unknown.
    /// </summary>
    public int OutputChannels { get; set; }

    /// <summary>
    /// Device buffer size in sample frames (SDL chunk fed to hardware). Often a power of two
    /// (64–1024) on pro/ASIO-style drivers; shared-mode OS devices may use other sizes (e.g. 480).
    /// </summary>
    public int BufferFrames { get; set; }

    public SDL.AudioFormat Format { get; set; } = 0;
    public float VolumeLevel { get; set; } = 1f;
    
    public AudioDevice(string name, uint logicalId,  out string error, int forcedId = -1)
    {
        if (forcedId != -1)
        {
            // If Id provided, will set using that. For example, loading from a save.
            DeviceId = forcedId;
            if (forcedId >= _nextId)
            {
                // Set next ID to be highest ID, to avoid ID conflict.
                _nextId = forcedId + 1;
            }
        }
        else
        {
            DeviceId = _nextId++;
        }
        Name = name;
        LogicalId = logicalId;
        
        error = "";
    }
    
    /// <summary>
    /// Device buffer duration in milliseconds, or 0 when sample rate / buffer frames are unknown.
    /// </summary>
    public float BufferMs =>
        SampleRate > 0 && BufferFrames > 0
            ? 1000f * BufferFrames / SampleRate
            : 0f;

    public override string ToString()
    {
        return $"Device: {Name}\n" +
               $"ID: {DeviceId}\n" +
               $"Output channels: {OutputChannels}\n" +
               $"Sample Rate: {SampleRate} Hz\n" +
               $"Bit Depth: {BitDepth}-bit\n" +
               $"Buffer: {BufferFrames} samples\n" +
               $"Volume: {VolumeLevel * 100:F1}%\n" +
               $"Format: {Format}";
    }
    
}