// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Channels;
using Cue2.Domain.Devices;
using Cue2.Services;
using Godot;
using Godot.Collections;
using Array = Godot.Collections.Array;

namespace Cue2.Domain.Devices;

public class OutputChannel
{
    public string Name { get; set; }
    public List<int> RoutedChannels { get; set; }
}

/// <summary>
/// Represents an audio output patch, managing channels and device outputs for routing audio signals.
/// </summary>
/// <remarks>
/// Stores patch metadata, channels, and per-device output configurations. Supports serialization for session save/load.
/// </remarks>
public partial class AudioOutputPatch : Godot.GodotObject
{
    private static int _nextId = 0;

    private const int MaxChannels = 16;
    
    public int Id { get; set; }
    public string Name { get; set; }
    public System.Collections.Generic.Dictionary<string, List<OutputChannel>> OutputDevices { get; set; }
    // <Device name, device output channnel, list of audio channel ID's>>
    public System.Collections.Generic.Dictionary<int, string> Channels { get; set; } // List of audio channels>
    private int _channelId { get; set; } = 0;
    /// <summary>
    /// Patch sub-master gain applied to all routed audio on this patch (linear 0…1).
    /// Multiplied after cue/component volume and before device outputs in the mix path.
    /// Default 1.0 (0 dB).
    /// </summary>
    /// <value>Linear amplitude scale; 1.0 = unity, 0 = silence.</value>
    public float Volume { get; set; } = 1.0f;


    /// <summary>
    /// Initializes a new unnamed audio output patch with default stereo channels.
    /// </summary>
    /// <remarks>
    /// Automatically assigns a unique ID and adds "Left" and "Right" channels. Use for new patches.
    /// </remarks>
    public AudioOutputPatch()
    {
        Id = _nextId++;
        Name = "Unnamed";
        OutputDevices = new System.Collections.Generic.Dictionary<string, List<OutputChannel>>();
        Channels = new System.Collections.Generic.Dictionary<int, string>();
        
        // Add some default blank channels.
        Channels.Add(_channelId++, "Left");
        Channels.Add(_channelId++, "Right");
    }

    /// <summary>
    /// Initializes a named audio output patch with default stereo channels.
    /// </summary>
    /// <param name="name">The name of the patch.</param>
    /// <remarks>
    /// Automatically assigns a unique ID. Ideal for user-created patches.
    /// </remarks>
    public AudioOutputPatch(string name) 
    {
        Id = _nextId++;
        Name = name;
        OutputDevices = new System.Collections.Generic.Dictionary<string, List<OutputChannel>>();
        Channels = new System.Collections.Generic.Dictionary<int, string>();
        
        // Add some default blank channels.
        Channels.Add(_channelId++, "Left");
        Channels.Add(_channelId++, "Right");
    }
    
    /// <summary>
    /// Creates an AudioOutputPatch instance from serialized data.
    /// </summary>
    /// <param name="dataDict">Godot dictionary containing patch data (Id, Name, Channels, OutputDevices).</param>
    /// <returns>A new AudioOutputPatch instance, or null on deserialization error.</returns>
    /// <remarks>
    /// Logs errors via GD.PrintErr. Ensure dataDict is validated before calling.
    /// Updates global _nextId if loaded ID is higher.
    /// </remarks>
    public static AudioOutputPatch FromData(Godot.Collections.Dictionary dataDict)
    {
        try
        {
            int id = dataDict["Id"].AsInt32();
            string name = dataDict["Name"].AsString();
            float volume = dataDict.ContainsKey("Volume") ? (float)dataDict["Volume"] : 1.0f;

            var channels = new System.Collections.Generic.Dictionary<int, string>();
            var channelsDict = dataDict["Channels"].AsGodotDictionary();
            foreach (var channelKey in channelsDict.Keys)
            {
                int key = channelKey.AsInt32();
                string value = channelsDict[channelKey].AsString();
                channels.Add(key, value);
            }


            var outputDevices = new System.Collections.Generic.Dictionary<string, List<OutputChannel>>();
            var outputDevicesDict = dataDict["OutputDevices"].AsGodotDictionary();
            foreach (var deviceKey in outputDevicesDict.Keys)
            {
                string deviceName = deviceKey.AsString();
                var outputsList = new List<OutputChannel>();
                var outputsArray = outputDevicesDict[deviceKey].AsGodotArray();
                foreach (var outputVar in outputsArray)
                {
                    var outputDict = outputVar.AsGodotDictionary();
                    var output = new OutputChannel
                    {
                        Name = outputDict["Name"].AsString(),
                        RoutedChannels = new List<int>()
                    };
                    var routedArray = outputDict["RoutedChannels"].AsGodotArray();
                    foreach (var routedVar in routedArray)
                    {
                        output.RoutedChannels.Add(routedVar.AsInt32());
                    }
                    outputsList.Add(output);
                }
                outputDevices.Add(deviceName, outputsList);
            }

            var patch = new AudioOutputPatch(name);
            patch.Id = id;
            if (id >= _nextId) _nextId = id + 1;
            patch.Channels = channels;
            patch.OutputDevices = outputDevices;
            patch.Volume = volume;
            patch._channelId = channels.Any() ? channels.Keys.Max() + 1 : 0;

            return patch;
        }
        catch (Exception ex)
        {
            GD.PrintErr("AudioOutputPatch:FromData - Error loading patch data: " + ex.Message);
            return null;
        }
    }
    
    public void RenameChannel(int id, string newName)
    {
        if (Channels.ContainsKey(id))
        {
            Channels[id] = newName;
            GD.Print("Successfuly renamed channel " + id + " to " + newName);
        }
    }

    /// <summary>
    /// Adds a new channel to the patch with the given name.
    /// </summary>
    /// <param name="name">The name of the new channel.</param>
    /// <param name="error">Output error string if channel limit is reached.</param>
    /// <remarks>
    /// Enforces a maximum of 16 channels for performance. Logs a warning if limit is reached.
    /// </remarks>
    public void NewChannel(string name, out string error)
    {
        if (Channels.Count >= MaxChannels)
        {
            GD.Print("AudioOutputPatch:NewChannel - Maximum channel limit (24) reached; cannot add more.");
            // Assuming globalSignals accessible; inject if needed
            error = $"Patch '{Name}' channel limit (16) reached; '{name}' not added.";
            return;
        }
        Channels.Add(_channelId++, name);
        error = null;
    }

    public void RemoveChannel(int channelId)
    {
        if (!Channels.ContainsKey(channelId))
        {
            GD.Print("AudioOutputPatch:RemoveChannel - Channel ID not found: " + channelId);
            return;
        }
        string removedName = Channels[channelId];
        Channels.Remove(channelId);
        foreach (var device in OutputDevices.Values)
        {
            foreach (var output in device)
            {
                output.RoutedChannels.RemoveAll(id => id == channelId);
            }
        }
        GD.Print("AudioOutputPatch:RemoveChannel - Successfully removed channel " + channelId + " (" + removedName + ") and cleaned routes.");
    }

    public void AddDeviceOutputs(string deviceName, int outputCount)
    {
        if (OutputDevices.ContainsKey(deviceName))
        {
            GD.PrintErr($"AudioOutputPatch:AddOutputDevice - Device '{deviceName}' already exists in patch '{Name}'.");
            return;
        }

        var outputs = new List<OutputChannel>();
        for (int i = 0; i < outputCount; i++)
        {
            outputs.Add(new OutputChannel { Name = $"Output {i+1}", RoutedChannels = new List<int>() });
        }
        OutputDevices.Add(deviceName, outputs);
    }
    public void RemoveOutputDevice(string deviceName)
    {
        if (OutputDevices.ContainsKey(deviceName))
        {
            OutputDevices.Remove(deviceName);
            return;
        }
        GD.Print($"Could not remove device: {deviceName} from patch: {Name}");
    }

    /// <summary>
    /// Returns whether the specified patch channel is routed to a device output.
    /// </summary>
    /// <param name="deviceName">Name of the output device.</param>
    /// <param name="outputIndex">Index of the output on the device.</param>
    /// <param name="channelId">Patch channel ID.</param>
    /// <returns>True if routed; otherwise false.</returns>
    public bool IsChannelRouted(string deviceName, int outputIndex, int channelId)
    {
        if (OutputDevices.TryGetValue(deviceName, out var outputs) &&
            outputIndex >= 0 && outputIndex < outputs.Count)
        {
            return outputs[outputIndex].RoutedChannels.Contains(channelId);
        }
        return false;
    }

    /// <summary>
    /// Sets or clears routing of a patch channel to a specific device output.
    /// </summary>
    /// <param name="deviceName">Name of the output device.</param>
    /// <param name="outputIndex">Index of the output on the device.</param>
    /// <param name="channelId">Patch channel ID.</param>
    /// <param name="routed">True to route (add), false to unroute (remove).</param>
    public void SetRouting(string deviceName, int outputIndex, int channelId, bool routed)
    {
        if (!OutputDevices.TryGetValue(deviceName, out var outputs) ||
            outputIndex < 0 || outputIndex >= outputs.Count)
        {
            GD.PrintErr($"AudioOutputPatch:SetRouting - Invalid device/output for {deviceName}:{outputIndex}");
            return;
        }

        var list = outputs[outputIndex].RoutedChannels;
        if (routed)
        {
            if (!list.Contains(channelId))
            {
                list.Add(channelId);
            }
        }
        else
        {
            list.Remove(channelId);
        }
    }
    
    public string GetDeviceOutputName(string deviceName, int outputIndex)
    {
        if (OutputDevices.TryGetValue(deviceName, out var outputs) && 
            outputIndex >= 0 && outputIndex < outputs.Count)
        {
            return outputs[outputIndex].Name;
        }
        GD.PrintErr($"AudioOutputPatch:GetOutputName - Failed to get name for device '{deviceName}' at index {outputIndex} in patch '{Name}'.");
        return null;
    }


    public bool RenameDeviceChannel(string deviceName, int outputIndex, string newOutputName)
    {
        if (!OutputDevices.TryGetValue(deviceName, out var outputs))
        {
            GD.PrintErr($"AudioOutputPatch:RenameOutput - Device '{deviceName}' not found in patch '{Name}'.");
            return false;
        }

        if (outputIndex < 0 || outputIndex >= outputs.Count)
        {
            GD.PrintErr($"AudioOutputPatch:RenameOutput - Invalid output index {outputIndex} for device '{deviceName}' in patch '{Name}'.");
            return false;
        }

        string oldName = outputs[outputIndex].Name;
        if (newOutputName == oldName)
        {
            GD.Print("AudioOutputPatch:RenameOutput - No name change needed.");
            return true; // No change needed
        }

        if (outputs.Any(o => o.Name == newOutputName))
        {
            GD.Print($"AudioOutputPatch:RenameOutput - Output name '{newOutputName}' already exists for device '{deviceName}' in patch '{Name}'.");
            return false;
        }

        outputs[outputIndex].Name = newOutputName;
        GD.Print($"AudioOutputPatch:RenameOutput - Successfully renamed output '{oldName}' to '{newOutputName}' for device '{deviceName}' at index {outputIndex} in patch '{Name}'.");
        return true;
    }

    /// <summary>
    /// Serializes the patch data into a Hashtable for saving.
    /// </summary>
    /// <returns>A Hashtable with keys: Id, Name, Channels, OutputDevices.</returns>
    /// <remarks>
    /// Uses ArrayList for nested collections to match Godot's serialization needs.
    /// </remarks>
    public Dictionary GetData()
    {
        var data = new Dictionary();
        data.Add("Id", Id);
        data.Add("Name", Name);

        var channelsData = new Dictionary();
        foreach (var channel in Channels)
        {
            channelsData.Add(channel.Key, channel.Value);
        }
        data.Add("Channels", channelsData);

        var outputDevicesData = new Dictionary();
        foreach (var device in OutputDevices)
        {
            var outputsArray = new Array();
            foreach (var output in device.Value)
            {
                var outputData = new Dictionary();
                outputData.Add("Name", output.Name);
                var routedChannelsArray = new Godot.Collections.Array<int>(output.RoutedChannels);
                outputData.Add("RoutedChannels", routedChannelsArray);
                outputsArray.Add(outputData);
            }
            outputDevicesData.Add(device.Key, outputsArray);
        }
        data.Add("OutputDevices", outputDevicesData);
        data.Add("Volume", Volume);

        return data;
    }
}