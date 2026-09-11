// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System.Collections.Generic;
using System.Linq;
using Cue2.Domain.Devices;
using Godot;

#nullable enable

namespace Cue2.Services;

/// <summary>
/// Legacy placeholder registry for audio devices (pre-SDL <see cref="AudioDevices"/> era).
/// </summary>
/// <remarks>
/// <para>
/// Live SDL open/close lifecycle is owned exclusively by the <c>/root/AudioDevices</c> autoload.
/// Session load, New Session, and audio-patch history reconcile open devices via
/// <see cref="AudioDevices.SyncOpenDevices"/> (see <c>Settings.ReconcileOpenAudioDevices</c>).
/// </para>
/// <para>
/// <see cref="ResetAudioDevices"/> remains for call-site compatibility but only clears this
/// unused in-memory map — it does <b>not</b> close SDL devices.
/// </para>
/// </remarks>
public partial class Devices : Node
{
    #pragma warning disable CS8618 // Fields are initialized in _Ready
    private GlobalData _globalData;
    private AudioDevices _audioDevices;
    #pragma warning restore CS8618

    private int Index = 0;
    
    /// <summary>Unused legacy map — not the SDL open set.</summary>
    private static readonly Dictionary<int, AudioDevice> AudioDevices = new Dictionary<int, AudioDevice>();
    
    public override void _Ready()
    {
        _globalData = GetNode<GlobalData>("/root/GlobalData");
        _audioDevices = GetNodeOrNull<AudioDevices>("/root/AudioDevices");
    }

    private AudioDevice? CreateAudioDevice(string deviceName, int deviceId = -1)
    {
        string? device = null;

        var available = _audioDevices?.GetAvailableAudioDeviceNames();
        if (available == null)
            return null;

        foreach (var i in available)
        {
            if (i == deviceName)
            {
                GD.Print("Selected device is: " + i);
                device = i;
            } 
        }

        if (device != null)
        {
            // Legacy VLC path removed — real open is AudioDevices.OpenAudioDevice.
            return null;
        }

        GD.Print("Device null return");
        return null;
    }

    public List<AudioDevice> GetAudioDevices()
    {
        var deviceList = new List<AudioDevice>();
        foreach (var device in AudioDevices)
        {
            deviceList.Add(device.Value);
        }
        return deviceList;
    }

    public AudioDevice? GetAudioDeviceFromId(int deviceId)
    {
        return AudioDevices.TryGetValue(deviceId, out var device) ? device : null;
    }

    public AudioDevice? EnableAudioDevice(string deviceName)
    {
        GD.Print(AudioDevices.Count);
        // Returns audio device, first if it already exists, if not it'll create one and return that.
        return AudioDevices.Values.FirstOrDefault(obj => obj.Name == deviceName) ?? CreateAudioDevice(deviceName);

    }

    public void DisableAudioDevice(int deviceId)
    {
        var deviceName = AudioDevices[deviceId].Name;
        AudioDevices.Remove(deviceId);
        GD.Print("Audio device: " + deviceName + " from list of enabled audio devices");
    }

    /// <summary>
    /// Clears the legacy in-memory map only. Does not close SDL devices.
    /// </summary>
    /// <remarks>
    /// Real reconcile happens in <c>Settings.ResetSettings</c> /
    /// <c>Settings.LoadSettings</c> via <see cref="AudioDevices.SyncOpenDevices"/>.
    /// </remarks>
    public void ResetAudioDevices()
    {
        AudioDevices.Clear();
        GD.Print("Devices:ResetAudioDevices - Cleared legacy map (SDL open set unchanged; " +
                 "Settings.ReconcileOpenAudioDevices owns load/reset close).");
    }
    
    public void AddAudioDeviceWithId(int deviceId, string deviceName)
    {
        CreateAudioDevice(deviceName, deviceId);
    }

}