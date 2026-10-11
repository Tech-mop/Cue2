// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

namespace Cue2.Domain.Timecode;

/// <summary>
/// SMPTE-style frame rates supported by Cue2 timecode endpoints.
/// </summary>
public enum TimecodeFrameRate
{
    /// <summary>24 fps (film).</summary>
    Fps24 = 0,

    /// <summary>25 fps (PAL / EBU).</summary>
    Fps25 = 1,

    /// <summary>29.97 fps drop-frame (NTSC DF).</summary>
    Fps2997Drop = 2,

    /// <summary>29.97 fps non-drop (NTSC NDF).</summary>
    Fps2997NonDrop = 3,

    /// <summary>30 fps.</summary>
    Fps30 = 4
}

/// <summary>
/// Display and conversion helpers for <see cref="TimecodeFrameRate"/>.
/// </summary>
public static class TimecodeRates
{
    /// <summary>
    /// English UI label for <paramref name="rate"/> (catalog key).
    /// </summary>
    /// <param name="rate">Frame rate.</param>
    /// <returns>English display name.</returns>
    public static string GetDisplayName(this TimecodeFrameRate rate) => rate switch
    {
        TimecodeFrameRate.Fps24 => "24 fps",
        TimecodeFrameRate.Fps25 => "25 fps",
        TimecodeFrameRate.Fps2997Drop => "29.97 df",
        TimecodeFrameRate.Fps2997NonDrop => "29.97 ndf",
        TimecodeFrameRate.Fps30 => "30 fps",
        _ => "30 fps"
    };

    /// <summary>
    /// Integer frame count used in HH:MM:SS:FF (30 for both 29.97 variants).
    /// </summary>
    /// <param name="rate">Frame rate.</param>
    /// <returns>Nominal frames per second.</returns>
    public static int NominalFramesPerSecond(this TimecodeFrameRate rate) => rate switch
    {
        TimecodeFrameRate.Fps24 => 24,
        TimecodeFrameRate.Fps25 => 25,
        TimecodeFrameRate.Fps2997Drop => 30,
        TimecodeFrameRate.Fps2997NonDrop => 30,
        TimecodeFrameRate.Fps30 => 30,
        _ => 30
    };

    /// <summary>
    /// Real-time frames per second used when advancing a generator from wall time.
    /// </summary>
    /// <param name="rate">Frame rate.</param>
    /// <returns>Frames per real second.</returns>
    public static double RealFramesPerSecond(this TimecodeFrameRate rate) => rate switch
    {
        TimecodeFrameRate.Fps24 => 24.0,
        TimecodeFrameRate.Fps25 => 25.0,
        TimecodeFrameRate.Fps2997Drop => 30000.0 / 1001.0,
        TimecodeFrameRate.Fps2997NonDrop => 30000.0 / 1001.0,
        TimecodeFrameRate.Fps30 => 30.0,
        _ => 30.0
    };

    /// <summary>
    /// True when this rate uses SMPTE drop-frame numbering (semicolon separator).
    /// </summary>
    /// <param name="rate">Frame rate.</param>
    /// <returns><see langword="true"/> for 29.97 drop-frame.</returns>
    public static bool IsDropFrame(this TimecodeFrameRate rate) =>
        rate == TimecodeFrameRate.Fps2997Drop;
}
