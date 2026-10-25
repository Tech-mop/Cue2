// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Cue2.Domain.Timecode;

/// <summary>
/// A single SMPTE-style timecode reading.
/// </summary>
public readonly struct TimecodeValue : IEquatable<TimecodeValue>
{
    /// <summary>Hours 0–23.</summary>
    public int Hours { get; }

    /// <summary>Minutes 0–59.</summary>
    public int Minutes { get; }

    /// <summary>Seconds 0–59.</summary>
    public int Seconds { get; }

    /// <summary>Frames 0–(nominal fps − 1).</summary>
    public int Frames { get; }

    /// <summary>Frame rate this value was counted in.</summary>
    public TimecodeFrameRate FrameRate { get; }

    /// <summary>True while a generator is advancing or a receiver is locked.</summary>
    public bool IsRunning { get; }

    /// <summary>True when an external source is supplying valid timecode.</summary>
    public bool IsLocked { get; }

    /// <summary>
    /// Creates a clamped timecode reading.
    /// </summary>
    /// <param name="hours">Hours (wrapped to 0–23).</param>
    /// <param name="minutes">Minutes (clamped 0–59).</param>
    /// <param name="seconds">Seconds (clamped 0–59).</param>
    /// <param name="frames">Frames (clamped to the rate).</param>
    /// <param name="frameRate">Counting rate.</param>
    /// <param name="isRunning">Whether the clock is advancing or locked.</param>
    /// <param name="isLocked">Whether an external source is valid.</param>
    public TimecodeValue(
        int hours,
        int minutes,
        int seconds,
        int frames,
        TimecodeFrameRate frameRate,
        bool isRunning = false,
        bool isLocked = false)
    {
        int fps = frameRate.NominalFramesPerSecond();
        Hours = ((hours % 24) + 24) % 24;
        Minutes = Math.Clamp(minutes, 0, 59);
        Seconds = Math.Clamp(seconds, 0, 59);
        Frames = Math.Clamp(frames, 0, Math.Max(0, fps - 1));
        FrameRate = frameRate;
        IsRunning = isRunning;
        IsLocked = isLocked;
    }

    /// <summary>
    /// Parses <c>HH:MM:SS:FF</c> or drop-frame <c>HH:MM:SS;FF</c>.
    /// </summary>
    /// <param name="text">Timecode string.</param>
    /// <param name="frameRate">Rate used when the string does not imply drop-frame.</param>
    /// <param name="value">Parsed reading.</param>
    /// <returns><see langword="true"/> when four numeric fields were read.</returns>
    public static bool TryParse(string text, TimecodeFrameRate frameRate, out TimecodeValue value) =>
        TryParse(text, frameRate, out value, out _);

    /// <summary>
    /// Parses a (optionally signed) <c>HH:MM:SS:FF</c> or drop-frame <c>HH:MM:SS;FF</c>.
    /// </summary>
    /// <param name="text">Timecode string, with optional leading <c>+</c> or <c>-</c>.</param>
    /// <param name="frameRate">Rate used when the string does not imply drop-frame.</param>
    /// <param name="value">Parsed magnitude (always non-negative fields).</param>
    /// <param name="sign"><c>-1</c> when the string started with <c>-</c>, otherwise <c>1</c>.</param>
    /// <returns><see langword="true"/> when four numeric fields were read.</returns>
    public static bool TryParse(
        string text,
        TimecodeFrameRate frameRate,
        out TimecodeValue value,
        out int sign)
    {
        value = default;
        if (!TryReadSignedBody(text, out string body, out sign))
            return false;

        bool drop = body.Contains(';');
        body = body.Replace(';', ':');
        string[] parts = body.Split(':');
        if (parts.Length != 4)
            return false;
        if (!int.TryParse(parts[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int h)
            || !int.TryParse(parts[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int m)
            || !int.TryParse(parts[2].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int sec)
            || !int.TryParse(parts[3].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int f))
            return false;

        var rate = drop ? TimecodeFrameRate.Fps2997Drop : frameRate;
        value = new TimecodeValue(h, m, sec, f, rate, isRunning: true, isLocked: true);
        return true;
    }

    /// <summary>
    /// Parses a LineEdit the way Cue2 time fields do: cleanup, then fill missing higher digits.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item>Plain number (e.g. <c>200</c>, <c>-200</c>) is total seconds.</item>
    /// <item><c>12:20</c> is 12 seconds + 20 frames.</item>
    /// <item><c>3:12:20</c> is 3 minutes + 12 seconds + 20 frames.</item>
    /// <item><c>1:3:12:20</c> / <c>00:01:00:00</c> is hours : minutes : seconds : frames.</item>
    /// <item>Drop-frame may use a semicolon before frames. Overflow frames/seconds/minutes roll up.</item>
    /// </list>
    /// </remarks>
    /// <param name="text">Operator input.</param>
    /// <param name="frameRate">Rate used when the string does not imply drop-frame.</param>
    /// <param name="value">Normalized reading (magnitude only).</param>
    /// <returns><see langword="true"/> when the input could be interpreted.</returns>
    public static bool TryParseUserInput(
        string text,
        TimecodeFrameRate frameRate,
        out TimecodeValue value) =>
        TryParseUserInput(text, frameRate, out value, out _);

    /// <summary>
    /// Parses a LineEdit the way Cue2 time fields do: cleanup, then fill missing higher digits.
    /// </summary>
    /// <remarks>
    /// The typed fields are the timecode label, so the result shows exactly the typed frame at
    /// every rate (29.97 included). A plain number is label seconds (<c>34</c> is 00:00:34:00).
    /// A drop-frame label that does not exist (frames 0 and 1 of most minutes) moves to the
    /// next real label, as drop-frame clocks do.
    /// </remarks>
    /// <param name="text">Operator input, with optional leading <c>+</c> or <c>-</c>.</param>
    /// <param name="frameRate">Rate used when the string does not imply drop-frame.</param>
    /// <param name="value">Normalized reading (magnitude only).</param>
    /// <param name="sign"><c>-1</c> when the string started with <c>-</c>, otherwise <c>1</c>.</param>
    /// <returns><see langword="true"/> when the input could be interpreted.</returns>
    public static bool TryParseUserInput(
        string text,
        TimecodeFrameRate frameRate,
        out TimecodeValue value,
        out int sign)
    {
        value = default;
        if (!TryParseUserLabelFrames(text, frameRate, out long labelFrames, out var rate, out sign))
            return false;

        int nominal = Math.Max(1, rate.NominalFramesPerSecond());
        int frames = (int)(labelFrames % nominal);
        long totalSeconds = labelFrames / nominal;
        int seconds = (int)(totalSeconds % 60);
        int minutes = (int)((totalSeconds / 60) % 60);
        int hours = (int)((totalSeconds / 3600) % 24);
        if (rate.IsDropFrame() && seconds == 0 && frames < 2 && minutes % 10 != 0)
            frames = 2;
        value = new TimecodeValue(hours, minutes, seconds, frames, rate, isRunning: true);
        return true;
    }

    /// <summary>
    /// Parses operator input to elapsed seconds without wrapping at 24 hours
    /// (for offset and duration fields).
    /// </summary>
    /// <remarks>
    /// A plain number is real seconds. Colon input is read as a frame count (fields counted at
    /// the nominal rate) and converted at the real rate, so it is the exact inverse of
    /// <see cref="FormatElapsed"/>: the field shows what was typed. At 24, 25 and 30 fps this is
    /// the same as reading the fields as seconds; at 29.97 one typed timecode second is
    /// 30 frames, 1.001 real seconds.
    /// </remarks>
    /// <param name="text">Operator input, with optional leading <c>+</c> or <c>-</c>.</param>
    /// <param name="frameRate">Rate used to convert frames to seconds.</param>
    /// <param name="elapsedSeconds">Non-negative elapsed seconds (magnitude).</param>
    /// <param name="sign"><c>-1</c> when the string started with <c>-</c>, otherwise <c>1</c>.</param>
    /// <returns><see langword="true"/> when the input could be interpreted.</returns>
    public static bool TryParseUserElapsed(
        string text,
        TimecodeFrameRate frameRate,
        out double elapsedSeconds,
        out int sign)
    {
        elapsedSeconds = 0;
        if (!TryCleanUserInput(text, frameRate, out string cleaned, out var rate, out sign))
            return false;

        if (!cleaned.Contains(':'))
        {
            if (!TryParsePlainSeconds(cleaned, out double totalSec))
                return false;
            elapsedSeconds = totalSec;
            return true;
        }

        if (!TryReadLabelFields(cleaned, rate, out long labelFrames))
            return false;
        elapsedSeconds = labelFrames / Math.Max(1e-6, rate.RealFramesPerSecond());
        return elapsedSeconds >= 0 && !double.IsInfinity(elapsedSeconds);
    }

    /// <summary>
    /// Reads operator input as a label frame count (fields at the nominal rate). A plain number is
    /// label seconds; its fraction becomes frames.
    /// </summary>
    private static bool TryParseUserLabelFrames(
        string text,
        TimecodeFrameRate frameRate,
        out long labelFrames,
        out TimecodeFrameRate rate,
        out int sign)
    {
        labelFrames = 0;
        if (!TryCleanUserInput(text, frameRate, out string cleaned, out rate, out sign))
            return false;

        int nominal = Math.Max(1, rate.NominalFramesPerSecond());
        if (!cleaned.Contains(':'))
        {
            if (!TryParsePlainSeconds(cleaned, out double totalSec) || totalSec > long.MaxValue / (2.0 * nominal))
                return false;
            double whole = Math.Floor(totalSec);
            int frames = Math.Min(nominal - 1, (int)Math.Floor((totalSec - whole) * nominal + 1e-6));
            labelFrames = (long)whole * nominal + frames;
            return true;
        }

        return TryReadLabelFields(cleaned, rate, out labelFrames);
    }

    /// <summary>Strips everything but digits and separators; a semicolon means drop-frame.</summary>
    private static bool TryCleanUserInput(
        string text,
        TimecodeFrameRate frameRate,
        out string cleaned,
        out TimecodeFrameRate rate,
        out int sign)
    {
        cleaned = string.Empty;
        rate = frameRate;
        if (!TryReadSignedBody(text, out string raw, out sign))
            return false;

        if (raw.Contains(';'))
            rate = TimecodeFrameRate.Fps2997Drop;
        cleaned = NonTimecodeChars.Replace(raw, "");
        cleaned = cleaned.Replace(';', ':');
        cleaned = RepeatedColons.Replace(cleaned, ":");
        cleaned = RepeatedDots.Replace(cleaned, ".");
        cleaned = cleaned.Trim(':');
        return !string.IsNullOrEmpty(cleaned) && cleaned != ".";
    }

    private static bool TryParsePlainSeconds(string cleaned, out double totalSec) =>
        double.TryParse(cleaned, NumberStyles.Float, CultureInfo.InvariantCulture, out totalSec)
        && totalSec >= 0
        && !double.IsNaN(totalSec)
        && !double.IsInfinity(totalSec);

    /// <summary>
    /// Reads <c>SS:FF</c>, <c>MM:SS:FF</c> or <c>HH:MM:SS:FF</c> as a frame count at the nominal
    /// rate. Overflowing fields roll up (90 seconds is 1 minute 30).
    /// </summary>
    private static bool TryReadLabelFields(string cleaned, TimecodeFrameRate rate, out long labelFrames)
    {
        labelFrames = 0;
        string[] parts = cleaned.Split(':');
        if (parts.Length is < 2 or > 4)
            return false;

        int hours = 0;
        int minutes = 0;
        if (!TryParseNonNegativeInt(parts[^1], out int frames)
            || !TryParseNonNegativeInt(parts[^2], out int seconds))
            return false;
        if (parts.Length >= 3 && !TryParseNonNegativeInt(parts[^3], out minutes))
            return false;
        if (parts.Length == 4 && !TryParseNonNegativeInt(parts[^4], out hours))
            return false;

        int nominal = Math.Max(1, rate.NominalFramesPerSecond());
        labelFrames = (((long)hours * 60 + minutes) * 60 + seconds) * nominal + frames;
        return true;
    }

    private static readonly Regex NonTimecodeChars = new(@"[^0-9:.;]", RegexOptions.Compiled);
    private static readonly Regex RepeatedColons = new(@":{2,}", RegexOptions.Compiled);
    private static readonly Regex RepeatedDots = new(@"\.{2,}", RegexOptions.Compiled);

    private static bool TryReadSignedBody(string text, out string body, out int sign)
    {
        body = string.Empty;
        sign = 1;
        if (string.IsNullOrWhiteSpace(text))
            return false;
        string raw = text.Trim();
        if (raw.StartsWith('-'))
        {
            sign = -1;
            raw = raw[1..].Trim();
        }
        else if (raw.StartsWith('+'))
        {
            raw = raw[1..].Trim();
        }

        if (string.IsNullOrEmpty(raw))
            return false;
        body = raw;
        return true;
    }

    private static bool TryParseNonNegativeInt(string text, out int value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text))
            return false;
        if (!int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
            return false;
        return value >= 0;
    }

    /// <summary>
    /// Formats elapsed real seconds as <c>HH:MM:SS:FF</c> (optional leading minus).
    /// </summary>
    /// <param name="elapsedSeconds">Elapsed seconds (negative allowed when <paramref name="allowNegative"/>).</param>
    /// <param name="frameRate">Counting rate.</param>
    /// <param name="allowNegative">When true, prefix minus for negative values.</param>
    /// <returns>Fixed-width timecode string.</returns>
    public static string FormatElapsed(
        double elapsedSeconds,
        TimecodeFrameRate frameRate,
        bool allowNegative = false)
    {
        int sign = 1;
        if (allowNegative && elapsedSeconds < 0)
        {
            sign = -1;
            elapsedSeconds = -elapsedSeconds;
        }
        else if (elapsedSeconds < 0)
        {
            elapsedSeconds = 0;
        }

        int nominal = Math.Max(1, frameRate.NominalFramesPerSecond());
        double realFps = Math.Max(1e-6, frameRate.RealFramesPerSecond());
        long totalFrames = (long)Math.Floor(elapsedSeconds * realFps + 1e-9);
        if (totalFrames < 0)
            totalFrames = 0;
        int frames = (int)(totalFrames % nominal);
        long totalSec = totalFrames / nominal;
        int seconds = (int)(totalSec % 60);
        int minutes = (int)((totalSec / 60) % 60);
        int hours = (int)Math.Min(99, totalSec / 3600);
        char sep = frameRate.IsDropFrame() ? ';' : ':';
        string body = $"{hours:00}:{minutes:00}:{seconds:00}{sep}{frames:00}";
        return sign < 0 ? "-" + body : body;
    }

    /// <summary>
    /// True when <paramref name="targetElapsed"/> is crossed while moving from
    /// <paramref name="previousElapsed"/> to <paramref name="currentElapsed"/> (forward only).
    /// A 24-hour wrap counts as forward; a backward seek does not.
    /// </summary>
    /// <param name="previousElapsed">Previous sample in real seconds.</param>
    /// <param name="currentElapsed">Current sample in real seconds.</param>
    /// <param name="targetElapsed">Target time in real seconds.</param>
    /// <returns><see langword="true"/> when the target was crossed this sample.</returns>
    public static bool CrossedElapsed(double previousElapsed, double currentElapsed, double targetElapsed) =>
        CrossedElapsed(previousElapsed, currentElapsed, targetElapsed, 24.0 * 3600.0);

    /// <summary>
    /// True when <paramref name="targetElapsed"/> is crossed while moving from
    /// <paramref name="previousElapsed"/> to <paramref name="currentElapsed"/> (forward only),
    /// with a day of <paramref name="daySeconds"/> (see <see cref="SecondsPerDay"/>).
    /// </summary>
    /// <param name="previousElapsed">Previous sample in real seconds.</param>
    /// <param name="currentElapsed">Current sample in real seconds.</param>
    /// <param name="targetElapsed">Target time in real seconds.</param>
    /// <param name="daySeconds">Length of the timecode day in real seconds.</param>
    /// <returns><see langword="true"/> when the target was crossed this sample.</returns>
    public static bool CrossedElapsed(double previousElapsed, double currentElapsed, double targetElapsed, double daySeconds)
    {
        double day = daySeconds > 0 ? daySeconds : 24.0 * 3600.0;
        double previous = ((previousElapsed % day) + day) % day;
        double current = ((currentElapsed % day) + day) % day;
        double target = ((targetElapsed % day) + day) % day;

        if (Math.Abs(current - previous) < 1e-9)
            return false;

        if (current >= previous)
            return target > previous && target <= current;

        // Backward jump: treat as a 24h wrap only when the step is more than half a day.
        if (previous - current > day * 0.5)
            return target > previous || target <= current;

        return false;
    }

    /// <summary>
    /// Number of frames in one 24-hour run of labels at <paramref name="frameRate"/>
    /// (00:00:00:00 to 23:59:59:last). Drop-frame skips 2 labels in 9 of every 10 minutes.
    /// </summary>
    /// <param name="frameRate">Counting rate.</param>
    /// <returns>Frames per timecode day.</returns>
    public static long FramesPerDay(TimecodeFrameRate frameRate)
    {
        if (frameRate.IsDropFrame())
            return 24L * 6 * (30 * 60 * 10 - 9 * 2);
        return 24L * 3600 * Math.Max(1, frameRate.NominalFramesPerSecond());
    }

    /// <summary>
    /// Real seconds in one 24-hour run of labels at <paramref name="frameRate"/>: exactly 86400 at
    /// 24, 25 and 30 fps; 86486.4 at 29.97 non-drop and 86399.9136 at 29.97 drop-frame.
    /// Elapsed seconds wrap at this length so every label of the day keeps its own time.
    /// </summary>
    /// <param name="frameRate">Counting rate.</param>
    /// <returns>Seconds per timecode day.</returns>
    public static double SecondsPerDay(TimecodeFrameRate frameRate) =>
        FramesPerDay(frameRate) / frameRate.RealFramesPerSecond();

    /// <summary>00:00:00:00 at <paramref name="frameRate"/>.</summary>
    /// <param name="frameRate">Counting rate.</param>
    /// <returns>Zero timecode.</returns>
    public static TimecodeValue Zero(TimecodeFrameRate frameRate) =>
        new(0, 0, 0, 0, frameRate);

    /// <summary>
    /// Builds a generator reading from elapsed real seconds at <paramref name="frameRate"/>.
    /// </summary>
    /// <param name="elapsedSeconds">Non-negative elapsed seconds.</param>
    /// <param name="frameRate">Counting rate.</param>
    /// <param name="isRunning">Whether the generator is currently advancing.</param>
    /// <returns>Wrapped 24-hour timecode.</returns>
    public static TimecodeValue FromElapsedSeconds(
        double elapsedSeconds,
        TimecodeFrameRate frameRate,
        bool isRunning)
    {
        if (elapsedSeconds < 0)
            elapsedSeconds = 0;

        double realFps = frameRate.RealFramesPerSecond();
        // Wrap at the day of labels, not 24 real hours: at 29.97 non-drop those differ by
        // 86 seconds, and wrapping early turned the last labels of the day into 00:00 times.
        double framesPerDay = FramesPerDay(frameRate);
        double wrapped = elapsedSeconds * realFps;
        if (framesPerDay > 0)
            wrapped %= framesPerDay;
        if (wrapped < 0)
            wrapped += framesPerDay;

        // Exact frame times can land a hair below the frame in floating point; nudge before flooring.
        long frameNumber = (long)Math.Floor(wrapped + 1e-6);
        if (frameRate.IsDropFrame())
            return FromDropFrameNumber(frameNumber, frameRate, isRunning);

        int fps = frameRate.NominalFramesPerSecond();
        int frames = (int)(frameNumber % fps);
        long totalSeconds = frameNumber / fps;
        int seconds = (int)(totalSeconds % 60);
        int minutes = (int)((totalSeconds / 60) % 60);
        int hours = (int)((totalSeconds / 3600) % 24);
        return new TimecodeValue(hours, minutes, seconds, frames, frameRate, isRunning);
    }

    /// <summary>
    /// Maps local wall-clock time of day onto a timecode at <paramref name="frameRate"/>.
    /// </summary>
    /// <param name="frameRate">Counting rate.</param>
    /// <returns>Running system-clock reading.</returns>
    public static TimecodeValue FromLocalTimeOfDay(TimecodeFrameRate frameRate)
    {
        DateTime now = DateTime.Now;
        int fps = frameRate.NominalFramesPerSecond();
        int frames = (int)(now.Millisecond / 1000.0 * fps);
        if (frames >= fps)
            frames = fps - 1;
        return new TimecodeValue(now.Hour, now.Minute, now.Second, frames, frameRate, isRunning: true);
    }

    /// <summary>
    /// Converts this reading to elapsed real seconds at its frame rate (24-hour wrap).
    /// </summary>
    /// <returns>Non-negative elapsed seconds.</returns>
    public double ToElapsedSeconds()
    {
        int fps = FrameRate.NominalFramesPerSecond();
        long frameNumber;
        if (FrameRate.IsDropFrame())
        {
            int totalMinutes = Hours * 60 + Minutes;
            frameNumber = (long)fps * 3600 * Hours
                          + (long)fps * 60 * Minutes
                          + (long)fps * Seconds
                          + Frames
                          - 2L * (totalMinutes - totalMinutes / 10);
        }
        else
        {
            frameNumber = ((long)Hours * 3600 + Minutes * 60 + Seconds) * fps + Frames;
        }

        if (frameNumber < 0)
            frameNumber = 0;
        return frameNumber / FrameRate.RealFramesPerSecond();
    }

    /// <summary>
    /// Returns this reading offset by <paramref name="seconds"/> (wrapped to 24 hours).
    /// </summary>
    /// <param name="seconds">Signed offset in real seconds.</param>
    /// <returns>New reading at the same rate and running flags.</returns>
    public TimecodeValue OffsetBySeconds(double seconds)
    {
        double elapsed = ToElapsedSeconds() + seconds;
        double day = SecondsPerDay(FrameRate);
        elapsed %= day;
        if (elapsed < 0)
            elapsed += day;
        var next = FromElapsedSeconds(elapsed, FrameRate, IsRunning);
        return new TimecodeValue(
            next.Hours, next.Minutes, next.Seconds, next.Frames, FrameRate, IsRunning, IsLocked);
    }

    /// <summary>
    /// Formats as <c>HH:MM:SS:FF</c>, or <c>HH:MM:SS;FF</c> for drop-frame.
    /// </summary>
    /// <returns>Fixed-width timecode string.</returns>
    public string ToDisplayString()
    {
        char sep = FrameRate.IsDropFrame() ? ';' : ':';
        return $"{Hours:00}:{Minutes:00}:{Seconds:00}{sep}{Frames:00}";
    }

    /// <inheritdoc />
    public override string ToString() => ToDisplayString();

    /// <inheritdoc />
    public bool Equals(TimecodeValue other) =>
        Hours == other.Hours
        && Minutes == other.Minutes
        && Seconds == other.Seconds
        && Frames == other.Frames
        && FrameRate == other.FrameRate
        && IsRunning == other.IsRunning
        && IsLocked == other.IsLocked;

    /// <inheritdoc />
    public override bool Equals(object obj) => obj is TimecodeValue other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() =>
        HashCode.Combine(Hours, Minutes, Seconds, Frames, FrameRate, IsRunning, IsLocked);

    /// <summary>
    /// Converts a 29.97 drop-frame frame number into HH:MM:SS;FF.
    /// </summary>
    private static TimecodeValue FromDropFrameNumber(
        long frameNumber,
        TimecodeFrameRate frameRate,
        bool isRunning)
    {
        const int fps = 30;
        const int drop = 2;
        const int framesPer10Min = fps * 60 * 10 - 9 * drop;
        const int framesPerMinMinusDrop = fps * 60 - drop;

        if (frameNumber < 0)
            frameNumber = 0;

        int d = (int)(frameNumber / framesPer10Min);
        int m = (int)(frameNumber % framesPer10Min);

        long adjusted = m < drop
            ? frameNumber + 9L * drop * d
            : frameNumber + 9L * drop * d + drop * ((m - drop) / framesPerMinMinusDrop);

        int frames = (int)(adjusted % fps);
        long totalSeconds = adjusted / fps;
        int seconds = (int)(totalSeconds % 60);
        int minutes = (int)((totalSeconds / 60) % 60);
        int hours = (int)((totalSeconds / 3600) % 24);
        return new TimecodeValue(hours, minutes, seconds, frames, frameRate, isRunning);
    }
}
