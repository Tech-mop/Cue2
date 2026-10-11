// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

namespace Cue2.Domain.Timecode;

/// <summary>
/// Protocol or generator used by a registered timecode endpoint.
/// </summary>
public enum TimecodeKind
{
    /// <summary>Cue2-generated free-running clock.</summary>
    Internal = 0,

    /// <summary>Local wall clock mapped to hours/minutes/seconds/frames.</summary>
    SystemClock = 1,

    /// <summary>MIDI Timecode (MTC quarter-frame and full-frame).</summary>
    MidiTimecode = 2,

    /// <summary>Linear / SMPTE timecode encoded in an audio stream (LTC).</summary>
    LinearTimecode = 3,

    /// <summary>Art-Net TimeCode packets (OpTimeCode).</summary>
    ArtNet = 4,

    /// <summary>OSC timecode messages.</summary>
    Osc = 5
}

/// <summary>
/// Display and capability helpers for <see cref="TimecodeKind"/>.
/// </summary>
public static class TimecodeKinds
{
    /// <summary>
    /// English UI label for <paramref name="kind"/> (catalog key).
    /// </summary>
    /// <param name="kind">Timecode kind.</param>
    /// <returns>English display name.</returns>
    public static string GetDisplayName(this TimecodeKind kind) => kind switch
    {
        TimecodeKind.Internal => "Internal",
        TimecodeKind.SystemClock => "System Clock",
        TimecodeKind.MidiTimecode => "MIDI Timecode",
        TimecodeKind.LinearTimecode => "Linear Timecode",
        TimecodeKind.ArtNet => "Art-Net",
        TimecodeKind.Osc => "OSC",
        _ => "Internal"
    };

    /// <summary>
    /// Short English label used in compact status text.
    /// </summary>
    /// <param name="kind">Timecode kind.</param>
    /// <returns>Short English name.</returns>
    public static string GetShortName(this TimecodeKind kind) => kind switch
    {
        TimecodeKind.Internal => "Internal",
        TimecodeKind.SystemClock => "Clock",
        TimecodeKind.MidiTimecode => "MTC",
        TimecodeKind.LinearTimecode => "LTC",
        TimecodeKind.ArtNet => "Art-Net",
        TimecodeKind.Osc => "OSC",
        _ => "TC"
    };

    /// <summary>
    /// True when this kind is a Cue2-side clock with no hardware send (Internal / System Clock).
    /// </summary>
    /// <param name="kind">Timecode kind.</param>
    /// <returns><see langword="true"/> for virtual generators.</returns>
    public static bool IsVirtualGenerator(this TimecodeKind kind) =>
        kind == TimecodeKind.Internal || kind == TimecodeKind.SystemClock;

    /// <summary>
    /// True when this kind may be registered as a session input (listen to an external clock).
    /// </summary>
    /// <param name="kind">Timecode kind.</param>
    /// <returns><see langword="true"/> for receivable protocols.</returns>
    public static bool AllowsInput(this TimecodeKind kind) =>
        kind is TimecodeKind.MidiTimecode
            or TimecodeKind.LinearTimecode
            or TimecodeKind.Osc;

    /// <summary>
    /// True when this kind may be registered as a session output (generate on this machine).
    /// </summary>
    /// <param name="kind">Timecode kind.</param>
    /// <returns><see langword="true"/> for generators and sendable protocols.</returns>
    public static bool AllowsOutput(this TimecodeKind kind) =>
        kind is TimecodeKind.Internal
            or TimecodeKind.SystemClock
            or TimecodeKind.MidiTimecode
            or TimecodeKind.LinearTimecode
            or TimecodeKind.Osc;

    /// <summary>
    /// True when Play / Pause / Set / Offset apply (not System Clock, which follows wall time).
    /// </summary>
    /// <param name="kind">Timecode kind.</param>
    /// <returns><see langword="true"/> when the output is a controllable generator.</returns>
    public static bool IsControllableGenerator(this TimecodeKind kind) =>
        kind != TimecodeKind.SystemClock;

    /// <summary>
    /// True when this kind encodes onto a named audio device channel (LTC).
    /// </summary>
    /// <param name="kind">Timecode kind.</param>
    /// <returns><see langword="true"/> for Linear Timecode.</returns>
    public static bool UsesAudioDeviceChannel(this TimecodeKind kind) =>
        kind == TimecodeKind.LinearTimecode;

    /// <summary>
    /// True when the source field is a free-text OSC address rather than a device picker.
    /// </summary>
    /// <param name="kind">Timecode kind.</param>
    /// <returns><see langword="true"/> for OSC.</returns>
    public static bool UsesFreeTextSource(this TimecodeKind kind) =>
        kind == TimecodeKind.Osc;

    /// <summary>
    /// Default source string when creating a new endpoint of this kind.
    /// </summary>
    /// <param name="kind">Timecode kind.</param>
    /// <param name="direction">Input or output.</param>
    /// <returns>Default source, or empty.</returns>
    public static string DefaultSource(this TimecodeKind kind, TimecodeDirection direction)
    {
        return kind switch
        {
            TimecodeKind.Osc => "/timecode",
            _ => string.Empty
        };
    }
}
