// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System;
using Godot;
using Godot.Collections;

namespace Cue2.Domain.Timecode;

/// <summary>
/// Named timecode input or output registered in show settings.
/// </summary>
/// <remarks>
/// Identity is <see cref="Id"/>. Hardware decode/encode is owned by
/// <c>TimecodeManager</c>; this type is the serializable configuration plus
/// generator state for Internal clocks.
/// </remarks>
public sealed class TimecodeEndpoint
{
    /// <summary>Stable id within the show (unique across inputs and outputs).</summary>
    public int Id { get; set; }

    /// <summary>User-facing name.</summary>
    public string Name { get; set; } = "Timecode";

    /// <summary>Whether this endpoint is a source or a destination.</summary>
    public TimecodeDirection Direction { get; set; }

    /// <summary>Protocol or generator.</summary>
    public TimecodeKind Kind { get; set; } = TimecodeKind.Internal;

    /// <summary>Counting rate.</summary>
    public TimecodeFrameRate FrameRate { get; set; } = TimecodeFrameRate.Fps30;

    /// <summary>
    /// Device, address, or OSC path this endpoint is bound to.
    /// Empty for Internal and System Clock.
    /// </summary>
    public string SourceName { get; set; } = string.Empty;

    /// <summary>
    /// 0-based audio channel on <see cref="SourceName"/> for LTC. Ignored for other kinds.
    /// </summary>
    public int SourceChannel { get; set; }

    /// <summary>
    /// When true, this clock is shown on the right half of the GO header.
    /// </summary>
    public bool ShowInHeader { get; set; }

    /// <summary>Art-Net universe (0–32767). Ignored for other kinds.</summary>
    public int ArtNetUniverse { get; set; }

    /// <summary>
    /// Elapsed real seconds for an Internal generator. Session-only (not saved).
    /// </summary>
    public double ElapsedSeconds { get; set; }

    /// <summary>
    /// True while a controllable output generator is advancing. Session-only (not saved).
    /// </summary>
    public bool Running { get; set; }

    /// <summary>
    /// When ≥ 0, the generator pauses once <see cref="ElapsedSeconds"/> reaches this value.
    /// Session-only (not saved). <c>-1</c> means no auto-pause.
    /// </summary>
    public double PlayUntilElapsed { get; set; } = -1;

    /// <summary>Latest computed or received value (session-only).</summary>
    public TimecodeValue CurrentValue { get; set; }

    /// <summary>Engine msec of the last locked input frame. Session-only.</summary>
    public ulong LastLockMsec { get; set; }

    /// <summary>
    /// Serializes configuration for the showfile / undo slice.
    /// </summary>
    /// <returns>Dictionary of persisted fields.</returns>
    public Dictionary GetData()
    {
        return new Dictionary
        {
            ["Id"] = Id,
            ["Name"] = Name ?? string.Empty,
            ["Direction"] = (int)Direction,
            ["Kind"] = (int)Kind,
            ["FrameRate"] = (int)FrameRate,
            ["SourceName"] = SourceName ?? string.Empty,
            ["SourceChannel"] = SourceChannel,
            ["ShowInHeader"] = ShowInHeader,
            ["ArtNetUniverse"] = ArtNetUniverse
        };
    }

    /// <summary>
    /// Restores configuration from <paramref name="data"/>. Does not start generators.
    /// </summary>
    /// <param name="data">Dictionary previously produced by <see cref="GetData"/>.</param>
    public void LoadFromData(Dictionary data)
    {
        if (data == null)
            return;

        if (data.TryGetValue("Id", out var idVar))
            Id = idVar.AsInt32();
        if (data.TryGetValue("Name", out var nameVar))
            Name = nameVar.AsString()?.Trim() ?? Name;
        if (string.IsNullOrEmpty(Name))
            Name = "Timecode";

        if (data.TryGetValue("Direction", out var dirVar)
            && Enum.IsDefined(typeof(TimecodeDirection), dirVar.AsInt32()))
            Direction = (TimecodeDirection)dirVar.AsInt32();

        if (data.TryGetValue("Kind", out var kindVar)
            && Enum.IsDefined(typeof(TimecodeKind), kindVar.AsInt32()))
            Kind = (TimecodeKind)kindVar.AsInt32();

        if (data.TryGetValue("FrameRate", out var rateVar)
            && Enum.IsDefined(typeof(TimecodeFrameRate), rateVar.AsInt32()))
            FrameRate = (TimecodeFrameRate)rateVar.AsInt32();

        if (data.TryGetValue("SourceName", out var srcVar))
            SourceName = srcVar.AsString() ?? string.Empty;

        if (data.TryGetValue("SourceChannel", out var chVar))
            SourceChannel = Math.Max(0, chVar.AsInt32());

        if (data.TryGetValue("ShowInHeader", out var headerVar))
            ShowInHeader = headerVar.AsBool();

        if (data.TryGetValue("ArtNetUniverse", out var uniVar))
            ArtNetUniverse = Math.Clamp(uniVar.AsInt32(), 0, 32767);

        ElapsedSeconds = 0;

        if (Kind == TimecodeKind.ArtNet)
        {
            Kind = Direction == TimecodeDirection.Input ? TimecodeKind.Osc : TimecodeKind.Internal;
            if (Direction == TimecodeDirection.Input && string.IsNullOrEmpty(SourceName))
                SourceName = "/timecode";
            if (Direction == TimecodeDirection.Output)
                SourceName = string.Empty;
        }

        if (Direction == TimecodeDirection.Input && !Kind.AllowsInput())
            Kind = TimecodeKind.MidiTimecode;
        if (Direction == TimecodeDirection.Output && !Kind.AllowsOutput())
            Kind = TimecodeKind.Internal;

        Running = false;
        CurrentValue = Kind == TimecodeKind.SystemClock
            ? TimecodeValue.FromLocalTimeOfDay(FrameRate)
            : TimecodeValue.FromElapsedSeconds(ElapsedSeconds, FrameRate, isRunning: false);
    }
}
