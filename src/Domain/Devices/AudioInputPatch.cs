// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using Godot;
using Godot.Collections;

namespace Cue2.Domain.Devices;

/// <summary>
/// One assignable input on an <see cref="AudioInputPatch"/>.
/// </summary>
/// <remarks>
/// <see cref="DeviceChannel"/> is 0-based. <c>-1</c> means no device channel is chosen.
/// <see cref="TrimDb"/> is show-scoped gain in decibels (0 = unity). The channel meter is before this trim.
/// The patch header meter includes this trim and <see cref="AudioInputPatch.TrimDb"/>.
/// </remarks>
public sealed class AudioInputChannel
{
    /// <summary>Stable id within the owning patch.</summary>
    public int Id { get; set; }

    /// <summary>Recording device name, or empty when unassigned.</summary>
    public string DeviceName { get; set; } = string.Empty;

    /// <summary>0-based channel on <see cref="DeviceName"/>, or -1 when unassigned.</summary>
    public int DeviceChannel { get; set; } = -1;

    private float _trimDb;

    /// <summary>Input trim in dB, clamped to <see cref="AudioInputPatch.MinTrimDb"/>…<see cref="AudioInputPatch.MaxTrimDb"/>.</summary>
    public float TrimDb
    {
        get => _trimDb;
        set => _trimDb = Mathf.Clamp(value, AudioInputPatch.MinTrimDb, AudioInputPatch.MaxTrimDb);
    }
}

/// <summary>
/// Show-scoped group of recording inputs. A cue audio component will later use a whole patch as its source.
/// </summary>
/// <remarks>
/// Persisted under <see cref="HistoryKey"/>. Not placed in the scene tree; the settings owner frees it.
/// </remarks>
public partial class AudioInputPatch : GodotObject
{
    /// <summary>Showfile and undo key for the input-patch table.</summary>
    public const string HistoryKey = "AudioInputPatch";

    /// <summary>Maximum channels on one patch.</summary>
    public const int MaxChannels = 64;

    /// <summary>Lowest input trim (dB).</summary>
    public const float MinTrimDb = -60f;

    /// <summary>Highest input trim (dB).</summary>
    public const float MaxTrimDb = 12f;

    private static int _nextId;

    private readonly List<AudioInputChannel> _channels = new();
    private int _nextChannelId;

    /// <summary>Stable patch id.</summary>
    public int Id { get; set; }

    /// <summary>User-facing patch name.</summary>
    public string Name { get; set; }

    private float _trimDb;

    /// <summary>
    /// Submaster trim in dB for every channel on this patch. 0 is unity.
    /// </summary>
    /// <value>Clamped to <see cref="MinTrimDb"/>…<see cref="MaxTrimDb"/>.</value>
    public float TrimDb
    {
        get => _trimDb;
        set => _trimDb = Mathf.Clamp(value, MinTrimDb, MaxTrimDb);
    }

    /// <summary>Channels in display order.</summary>
    public IReadOnlyList<AudioInputChannel> Channels => _channels;

    /// <summary>
    /// Creates a patch with a new id and no channels.
    /// </summary>
    /// <param name="name">Display name. Empty becomes "Input Patch".</param>
    public AudioInputPatch(string name)
    {
        Id = _nextId++;
        Name = string.IsNullOrWhiteSpace(name) ? "Input Patch" : name;
    }

    /// <summary>
    /// Builds a patch from a showfile or history dictionary.
    /// </summary>
    /// <param name="data">Dictionary with Id, Name, and Channels.</param>
    /// <returns>The patch, or null when <paramref name="data"/> cannot be read.</returns>
    public static AudioInputPatch FromData(Dictionary data)
    {
        if (data == null)
            return null;

        try
        {
            int id = data["Id"].AsInt32();
            string name = data.ContainsKey("Name") ? data["Name"].AsString() : "Input Patch";
            var patch = new AudioInputPatch(name);
            patch.Id = id;
            if (id >= _nextId)
                _nextId = id + 1;
            if (data.ContainsKey("TrimDb"))
                patch.TrimDb = data["TrimDb"].AsSingle();

            if (data.ContainsKey("Channels") && data["Channels"].VariantType == Variant.Type.Array)
            {
                int maxChannelId = -1;
                foreach (var entry in data["Channels"].AsGodotArray())
                {
                    if (entry.VariantType != Variant.Type.Dictionary)
                        continue;
                    var channelData = entry.AsGodotDictionary();
                    int channelId = channelData.ContainsKey("Id") ? channelData["Id"].AsInt32() : patch._nextChannelId;
                    var channel = new AudioInputChannel
                    {
                        Id = channelId,
                        DeviceName = channelData.ContainsKey("DeviceName") ? channelData["DeviceName"].AsString() : string.Empty,
                        DeviceChannel = channelData.ContainsKey("DeviceChannel") ? channelData["DeviceChannel"].AsInt32() : -1,
                        TrimDb = channelData.ContainsKey("TrimDb") ? channelData["TrimDb"].AsSingle() : 0f
                    };
                    patch._channels.Add(channel);
                    if (channelId > maxChannelId)
                        maxChannelId = channelId;
                }

                patch._nextChannelId = maxChannelId + 1;
            }

            return patch;
        }
        catch (Exception ex)
        {
            GD.PrintErr($"AudioInputPatch:FromData - {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Serializes this patch for the showfile and undo.
    /// </summary>
    /// <returns>Dictionary with Id, Name, TrimDb, and Channels.</returns>
    public Dictionary GetData()
    {
        var data = new Dictionary
        {
            { "Id", Id },
            { "Name", Name ?? string.Empty },
            { "TrimDb", TrimDb }
        };

        var channels = new Godot.Collections.Array();
        foreach (var channel in _channels)
        {
            channels.Add(new Dictionary
            {
                { "Id", channel.Id },
                { "DeviceName", channel.DeviceName ?? string.Empty },
                { "DeviceChannel", channel.DeviceChannel },
                { "TrimDb", channel.TrimDb }
            });
        }

        data.Add("Channels", channels);
        return data;
    }

    /// <summary>
    /// Appends an unassigned channel.
    /// </summary>
    /// <returns>The new channel, or null when <see cref="MaxChannels"/> is already reached.</returns>
    public AudioInputChannel AddChannel()
    {
        if (_channels.Count >= MaxChannels)
        {
            GD.Print($"AudioInputPatch:AddChannel - Patch '{Name}' is at the {MaxChannels} channel limit.");
            return null;
        }

        var channel = new AudioInputChannel { Id = _nextChannelId++ };
        _channels.Add(channel);
        return channel;
    }

    /// <summary>
    /// Finds a channel by id.
    /// </summary>
    /// <param name="channelId">Channel id.</param>
    /// <returns>The channel, or null.</returns>
    public AudioInputChannel FindChannel(int channelId)
    {
        foreach (var channel in _channels)
        {
            if (channel.Id == channelId)
                return channel;
        }

        return null;
    }

    /// <summary>
    /// Removes a channel by id.
    /// </summary>
    /// <param name="channelId">Channel id.</param>
    /// <returns>True when a channel was removed.</returns>
    public bool RemoveChannel(int channelId)
    {
        for (int i = 0; i < _channels.Count; i++)
        {
            if (_channels[i].Id != channelId)
                continue;
            _channels.RemoveAt(i);
            return true;
        }

        return false;
    }
}
