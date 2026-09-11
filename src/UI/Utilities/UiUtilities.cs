// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using Cue2.Domain.Cuelist;
using Cue2.Domain.Playback;
using Cue2.Domain.Devices;
using Cue2.Domain.ShowSettings;
using Cue2.Domain.Metadata;
using Cue2.Domain.Cues;
using Cue2.Domain.Connections;
using Cue2.Domain.Library;
using Cue2.Domain.Commands;
using Cue2.Services;
using Godot;

namespace Cue2.UI.Utilities;

/// <summary>
/// A utility class for UI elements that need to inspect Cue components.
/// </summary>
public partial class UiUtilities : Node
{
    private static readonly Regex IpRegex = new Regex(@"^(?:(?:25[0-5]|2[0-4][0-9]|[01]?[0-9][0-9]?)\.){3}(?:25[0-5]|2[0-4][0-9]|[01]?[0-9][0-9]?)$");
    private static readonly Regex CleanRegex = new Regex(@"[^\d.]"); // Removes anything that's not digit or dot
    
    /// <summary>
    /// Checks if the given Cue contains a component of the specified type.
    /// </summary>
    /// <typeparam name="T">The type of ICueComponent to check for (e.g., AudioComponent).</typeparam>
    /// <param name="cue">The Cue instance to inspect.</param>
    /// <returns>True if at least one component of type T is present; otherwise, false.</returns>
    public static bool HasComponent<T>(Cue cue) where T : ICueComponent
    {
        if (cue == null)
        {
            GD.Print("UiUtilities:HasComponent - Attempted to check component on null Cue.");
            return false;
        }

        try
        {
            return cue.Components.OfType<T>().Any();
        }
        catch (System.Exception ex)
        {
            GD.PrintErr($"UiUtilities:HasComponent - Error checking component: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Parses user input from a LineEdit (e.g., "1:02:03.000", "3723", "1:2:3") into seconds and formats to "h:m:s.ms".
    /// </summary>
    /// <param name="input">The raw string from the LineEdit.</param>
    /// <param name="seconds">Out: The parsed time in seconds (double).</param>
    /// <param name="isValid">Out: True if the input was successfully parsed, false otherwise.</param>
    /// <returns>The formatted string (e.g., "01:02:03.000") or "00:00:00.000" on parse failure.</returns>
    /// <remarks>
    /// Supports flexible formats: colon-separated (h:m:s.ms), plain seconds (e.g., "3723" -> "01:02:03.000"), or partial (e.g., "1:2:3" -> "01:02:03.000").
    /// Plain numbers are treated as total seconds. Logs warnings on invalid input. Use in UI for time fields like cue start/end times.
    /// </remarks>
    public static string ParseAndFormatTime(string input, out double seconds, out bool isValid)
    {
        return ParseAndFormatTime(input, out seconds, out _, out isValid); // Overload to default without labeledFormat
    }
    
    public static string ParseAndFormatTime(string input, out double seconds)
    {
        return ParseAndFormatTime(input, out seconds, out _, out _); // Overload to default without labeledFormat
    }
    
    public static string ParseAndFormatTime(string input, out double seconds, out string labeledFormat)
    {
        return ParseAndFormatTime(input, out seconds, out labeledFormat, out _); // Overload to default without labeledFormat
    }
    
    
    
    /// <summary>
    /// Parses user input from a LineEdit (e.g., "2:02.000", "122", "2:2", "4'", "1'30\"") into seconds
    /// and formats to "m:s.ms" (with hours when needed).
    /// </summary>
    /// <param name="input">The raw string from the LineEdit.</param>
    /// <param name="seconds">Out: The parsed time in seconds (double).</param>
    /// <param name="labeledFormat">Out: Optional labeled format (e.g., "01hr:02m:03s.000ms" or "02m:03s.000ms" if hours are 0).</param>
    /// <param name="isValid">Out: True if the input was successfully parsed, false otherwise.</param>
    /// <returns>The formatted string (e.g., "02:02.000") or "" on parse failure. Never null.</returns>
    /// <remarks>
    /// Supports:
    /// <list type="bullet">
    /// <item>Colon form: m:s.ms, h:m:s.ms (partial segments allowed).</item>
    /// <item>Plain number: total seconds (e.g. "122" → 2:02.000).</item>
    /// <item>Ticks: minutes with <c>'</c>, seconds with <c>"</c> (e.g. "4'", "1'30", "30\"").</item>
    /// </list>
    /// Unknown letters/symbols are stripped when using colon/plain form so fields can always re-sanitize.
    /// </remarks>
    public static string ParseAndFormatTime(string input, out double seconds, out string labeledFormat, out bool isValid)
    {
        seconds = 0.0;
        labeledFormat = "00m:00s.000ms";
        isValid = false;
        if (string.IsNullOrWhiteSpace(input))
        {
            GD.Print("UiUtilities:ParseAndFormatTime - Empty input, defaulting to 0.");
            return "";
        }

        try
        {
            string raw = input.Trim();

            // Normalize unicode primes / curly quotes to ASCII feet/inches marks.
            raw = raw
                .Replace('′', '\'')
                .Replace('’', '\'')
                .Replace('`', '\'')
                .Replace('″', '"')
                .Replace('”', '"')
                .Replace('“', '"');

            // Minute/second ticks take priority when present.
            if (raw.Contains('\'') || raw.Contains('"'))
            {
                if (!TryParseTickTime(raw, out seconds))
                {
                    GD.Print($"UiUtilities:ParseAndFormatTime - Invalid tick time '{input}'");
                    return "";
                }

                labeledFormat = FormatLabeledTime(seconds);
                isValid = true;
                return FormatTime(seconds);
            }

            // Colon / plain-seconds path: strip junk (letters, spaces, etc.) then parse.
            string cleaned = Regex.Replace(raw, @"[^0-9:.]", "");
            if (string.IsNullOrEmpty(cleaned) || cleaned is "." or ":" or ".:")
            {
                GD.Print($"UiUtilities:ParseAndFormatTime - No numeric content in '{input}'");
                return "";
            }

            // Collapse accidental repeated separators (e.g. "1::2" → "1:2").
            cleaned = Regex.Replace(cleaned, @":{2,}", ":");
            cleaned = Regex.Replace(cleaned, @"\.{2,}", ".");
            cleaned = cleaned.Trim(':', '.');

            if (string.IsNullOrEmpty(cleaned))
            {
                GD.Print($"UiUtilities:ParseAndFormatTime - Empty after cleanup: '{input}'");
                return "";
            }

            // Plain number (no colon) → total seconds (supports fractional).
            if (!cleaned.Contains(':'))
            {
                if (!double.TryParse(cleaned, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out double totalSec) ||
                    totalSec < 0 || double.IsNaN(totalSec) || double.IsInfinity(totalSec))
                {
                    GD.Print($"UiUtilities:ParseAndFormatTime - Invalid plain seconds '{input}'");
                    return "";
                }

                seconds = totalSec;
                labeledFormat = FormatLabeledTime(seconds);
                isValid = true;
                return FormatTime(seconds);
            }

            // Colon-separated: [h:]m:s[.ms] or m:s[.ms] or h:m:s
            var parts = cleaned.Split(':');
            if (parts.Length is < 2 or > 3)
            {
                GD.Print($"UiUtilities:ParseAndFormatTime - Invalid colon segments in '{input}'");
                return "";
            }

            double hour = 0;
            double min;
            double sec;
            double fracSec = 0.0;

            if (parts.Length == 3)
            {
                if (!double.TryParse(parts[0], System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out hour) ||
                    !double.TryParse(parts[1], System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out min))
                    return "";
                if (!TryParseSecondsWithFraction(parts[2], out sec, out fracSec))
                    return "";
            }
            else // 2 parts: m:s
            {
                if (!double.TryParse(parts[0], System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out min))
                    return "";
                if (!TryParseSecondsWithFraction(parts[1], out sec, out fracSec))
                    return "";
            }

            if (hour < 0 || min < 0 || sec < 0 || fracSec < 0)
                return "";

            seconds = (hour * 3600.0) + (min * 60.0) + sec + fracSec;
            labeledFormat = FormatLabeledTime(seconds);
            isValid = true;
            return FormatTime(seconds);
        }
        catch (Exception ex)
        {
            GD.Print($"UiUtilities:ParseAndFormatTime - Invalid input '{input}': {ex.Message}");
            seconds = 0.0;
            labeledFormat = "00m:00s.000ms";
            isValid = false;
            return "";
        }
    }

    /// <summary>
    /// Parses tick times: minutes with <c>'</c>, seconds with <c>"</c>.
    /// Examples: <c>4'</c> → 240s, <c>1'30</c> / <c>1'30"</c> → 90s, <c>30"</c> → 30s.
    /// </summary>
    private static bool TryParseTickTime(string raw, out double seconds)
    {
        seconds = 0.0;
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        // Allow spaces around marks: "4 ' 30 \"", "4'", "30\""
        // Minutes optional only when a seconds mark is present.
        var match = Regex.Match(raw,
            @"^\s*(?:(\d+(?:\.\d+)?)\s*')?\s*(?:(\d+(?:\.\d+)?)\s*""?)?\s*$");
        if (!match.Success)
            return false;

        bool hasMinMark = raw.Contains('\'');
        bool hasSecMark = raw.Contains('"');
        string minStr = match.Groups[1].Success ? match.Groups[1].Value : null;
        string secStr = match.Groups[2].Success ? match.Groups[2].Value : null;

        // Require structure that matches the marks present.
        if (hasMinMark && string.IsNullOrEmpty(minStr))
            return false;
        if (!hasMinMark && !hasSecMark)
            return false;
        // Bare "4'" → minutes only (sec group empty). Bare "30\"" → seconds only.
        // "4'30" → both. Reject empty after marks.
        if (string.IsNullOrEmpty(minStr) && string.IsNullOrEmpty(secStr))
            return false;

        // If only a seconds mark, group1 is empty and group2 is the number (regex may put
        // a lone number in group2 only when ' is absent — re-parse when needed).
        if (!hasMinMark && hasSecMark)
        {
            var secOnly = Regex.Match(raw, @"^\s*(\d+(?:\.\d+)?)\s*""\s*$");
            if (!secOnly.Success)
                return false;
            if (!double.TryParse(secOnly.Groups[1].Value, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double secOnlyVal) ||
                secOnlyVal < 0)
                return false;
            seconds = secOnlyVal;
            return true;
        }

        double mins = 0;
        double secs = 0;
        if (!string.IsNullOrEmpty(minStr))
        {
            if (!double.TryParse(minStr, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out mins) || mins < 0)
                return false;
        }

        if (!string.IsNullOrEmpty(secStr))
        {
            if (!double.TryParse(secStr, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out secs) || secs < 0)
                return false;
        }

        seconds = (mins * 60.0) + secs;
        return true;
    }

    /// <summary>
    /// Parses a seconds segment that may include a fractional part ("3", "3.5", "03.250").
    /// </summary>
    private static bool TryParseSecondsWithFraction(string segment, out double wholeSeconds, out double fracSeconds)
    {
        wholeSeconds = 0;
        fracSeconds = 0;
        if (string.IsNullOrEmpty(segment))
            return true;

        int dot = segment.IndexOf('.');
        if (dot < 0)
        {
            return double.TryParse(segment, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out wholeSeconds);
        }

        string whole = segment.Substring(0, dot);
        string frac = segment.Substring(dot + 1);
        if (!string.IsNullOrEmpty(whole) &&
            !double.TryParse(whole, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out wholeSeconds))
            return false;
        if (string.IsNullOrEmpty(frac))
            return true;

        // At most 3 ms digits; extra digits ignored (same as prior behavior).
        if (frac.Length > 3)
            frac = frac.Substring(0, 3);
        if (!double.TryParse("0." + frac, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out fracSeconds))
            return false;
        return true;
    }

    public static string FormatTime(double seconds)
    {
        var hour = (int)Math.Floor(seconds / 3600);
        var min = (int)Math.Floor((seconds % 3600) / 60);
        var sec = (int)Math.Floor(seconds % 60);
        var fracSec = seconds - Math.Floor(seconds);
        var ms = (int)Math.Round(fracSec * 1000); // Round to nearest ms
        if (ms >= 1000) // Carry over if rounding causes overflow
        {
            ms -= 1000;
            sec += 1;
            if (sec >= 60)
            {
                sec -= 60;
                min += 1;
                if (min >= 60)
                {
                    min -= 60;
                    hour += 1;
                }
            }
        }

        var time = $"{min:D2}:{sec:D2}.{ms:D3}";
        if (hour > 0)
        {
            time = $"{hour:D2}:" + time;
        }
        return time;
    }

    private static string FormatLabeledTime(double seconds)
    {
        var hour = (int)Math.Floor(seconds / 3600);
        var min = (int)Math.Floor((seconds % 3600) / 60);
        var sec = (int)Math.Floor(seconds % 60);
        var fracSec = seconds - Math.Floor(seconds);
        var ms = (int)Math.Round(fracSec * 1000); // Round to nearest ms
        if (ms >= 1000) // Carry over if rounding causes overflow
        {
            ms -= 1000;
            sec += 1;
            if (sec >= 60)
            {
                sec -= 60;
                min += 1;
                if (min >= 60)
                {
                    min -= 60;
                    hour += 1;
                }
            }
        }

        string labeled = $"{min:D2}m:{sec:D2}s.{ms:D3}ms";
        if (hour > 0)
        {
            labeled = $"{hour:D2}hr:" + labeled;
        }
        return labeled;
    }

    /// <summary>
    /// Parses a wall-clock time of day for cue clock triggers.
    /// </summary>
    /// <param name="input">User text (e.g. "14:30", "14:30:00", "2:30 PM", "2:30:00 pm").</param>
    /// <param name="timeOfDay">Out: parsed local time of day.</param>
    /// <param name="display">Out: normalized <c>HH:mm:ss</c> display, or empty on failure / empty input.</param>
    /// <returns>
    /// <c>true</c> when input is empty (means clear) or successfully parsed;
    /// <c>false</c> when input is non-empty but invalid.
    /// </returns>
    /// <remarks>
    /// Empty/whitespace input is treated as "clear" (success with <see cref="TimeSpan.Zero"/> and empty display).
    /// 12-hour times without AM/PM are rejected when the hour is 1–12 ambiguous? No — bare 1–12 without meridiem
    /// is treated as 24h if hour ≤ 23 (so "2:30" = 02:30). Prefer AM/PM when using 12h.
    /// </remarks>
    public static bool TryParseClockTime(string input, out TimeSpan timeOfDay, out string display)
    {
        timeOfDay = TimeSpan.Zero;
        display = string.Empty;

        if (string.IsNullOrWhiteSpace(input))
            return true; // clear

        string raw = input.Trim();
        bool pm = false;
        bool am = false;
        string upper = raw.ToUpperInvariant();
        if (upper.EndsWith("PM") || upper.EndsWith("P.M.") || upper.EndsWith("P.M"))
        {
            pm = true;
            raw = Regex.Replace(raw, @"\s*[Pp]\.?[Mm]\.?\s*$", "").Trim();
        }
        else if (upper.EndsWith("AM") || upper.EndsWith("A.M.") || upper.EndsWith("A.M"))
        {
            am = true;
            raw = Regex.Replace(raw, @"\s*[Aa]\.?[Mm]\.?\s*$", "").Trim();
        }

        // Allow H:M, H:M:S, H:M:S.ms — also H.M via dots as separators is not supported; use colons.
        if (!Regex.IsMatch(raw, @"^\d{1,2}:\d{1,2}(:\d{1,2}(\.\d+)?)?$"))
        {
            // Also accept plain "HHMM" or "HHMMSS"? Keep strict for clarity.
            GD.Print($"UiUtilities:TryParseClockTime - Invalid clock format '{input}'");
            return false;
        }

        var parts = raw.Split(':');
        if (!int.TryParse(parts[0], out int hour) ||
            !int.TryParse(parts[1], out int minute))
            return false;

        int second = 0;
        if (parts.Length >= 3)
        {
            // Allow fractional seconds but store whole seconds for display.
            if (!double.TryParse(parts[2], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double secFrac))
                return false;
            second = (int)Math.Floor(secFrac);
            if (second < 0 || second > 59) return false;
        }

        if (minute < 0 || minute > 59) return false;

        if (am || pm)
        {
            if (hour < 1 || hour > 12) return false;
            if (hour == 12) hour = 0;
            if (pm) hour += 12;
        }
        else
        {
            if (hour < 0 || hour > 23) return false;
        }

        timeOfDay = new TimeSpan(hour, minute, second);
        display = Cue.FormatClockTimeOfDay(timeOfDay);
        return true;
    }
    
    /// <summary>Practical silence floor for volume UI (−60 dB → linear 0).</summary>
    public const float MinVolumeDb = Cue2.Media.Audio.AudioMixMatrix.MinVolumeDb;

    /// <summary>
    /// Maximum digital gain for cue component / embedded-audio volume fields (+12 dB).
    /// </summary>
    public const float MaxComponentGainDb = Cue2.Media.Audio.AudioMixMatrix.MaxComponentGainDb;

    /// <summary>
    /// Linear magnitude for <see cref="MaxComponentGainDb"/> (≈ 3.981).
    /// </summary>
    public static float MaxComponentGainLinear => Cue2.Media.Audio.AudioMixMatrix.MaxComponentGainLinear;

    /// <summary>Unity gain (0 dBFS) for master / routing matrix paths that must not boost.</summary>
    public const float MaxUnityVolumeLinear = 1f;

    /// <summary>
    /// Clamps a component-gain linear volume to the allowed digital-gain range (0…+12 dB).
    /// </summary>
    public static float ClampComponentGainLinear(float linear) =>
        Cue2.Media.Audio.AudioMixMatrix.ClampComponentGainLinear(linear);

    /// <summary>
    /// Formats a linear volume as a dB label for component fields (e.g. <c>+6.0dB</c>, <c>0.0dB</c>, <c>-12.0dB</c>).
    /// </summary>
    public static string FormatComponentVolumeDb(float linear)
    {
        float db = LinearToDb(linear);
        if (db > 0f)
            return $"+{db:0.0}dB";
        return $"{db:0.0}dB";
    }

    /// <summary>
    /// Converts a linear volume to decibels (dB).
    /// </summary>
    /// <param name="linear">Linear volume (0 = silence; 1 = 0 dB; up to <see cref="MaxComponentGainLinear"/> for +12 dB).</param>
    /// <returns>dB rounded to one decimal (e.g. 0 for 1.0, −60 for silence, +12 for max gain).</returns>
    /// <remarks>
    /// Formula: 20 * log10(linear). Values above max gain or below 0 are clamped.
    /// </remarks>
    public static float LinearToDb(float linear)
    {
        if (linear < 0f || linear > MaxComponentGainLinear + 1e-4f)
        {
            GD.Print($"UiUtilities:LinearToDb - Linear {linear} outside 0…{MaxComponentGainLinear:0.###}; clamping.");
            linear = Mathf.Clamp(linear, 0f, MaxComponentGainLinear);
        }

        if (Mathf.IsZeroApprox(linear)) return MinVolumeDb; // Avoid -inf.
        float db = 20f * MathF.Log10(linear);
        float dbRounded = MathF.Round(db, 1);
        // Keep display inside the published component range.
        return Mathf.Clamp(dbRounded, MinVolumeDb, MaxComponentGainDb);
    }
    
    /// <summary>
    /// Converts a dB string to linear volume (component gain range −60…+12 dB).
    /// </summary>
    /// <param name="dbInput">The dB value (e.g. <c>0dB</c>, <c>+6</c>, <c>-12.5dB</c>).</param>
    /// <returns>Linear volume (0…≈3.98). Returns −1f on parse failure.</returns>
    public static float DbToLinear(string dbInput)
    {
        if (string.IsNullOrWhiteSpace(dbInput))
        {
            GD.Print("UiUtilities:DbToLinear - Empty input; returning 0.");
            return -1f;
        }

        try
        {
            // Clean: remove 'dB' case-insensitively, trim
            string cleaned = dbInput.ToLower().Replace("db", "").Trim();

            if (!float.TryParse(cleaned, out float db))
            {
                throw new FormatException("Invalid numeric format after parsing.");
            }

            return DbToLinear(db);
        }
        catch (Exception ex)
        {
            GD.Print($"UiUtilities:DbToLinear - Invalid input '{dbInput}': {ex.Message}; returning 0.");
            return -1f;
        }
    }

    /// <summary>
    /// Converts a dB value to linear volume with digital gain (range −60…+12 dB → 0…≈3.98).
    /// </summary>
    /// <param name="db">Decibels (0 = unity, +12 = max boost, −60 or lower = silence).</param>
    /// <returns>Linear volume in 0…<see cref="MaxComponentGainLinear"/>.</returns>
    public static float DbToLinear(float db)
    {
        if (db <= MinVolumeDb)
            return 0f;
        if (db > MaxComponentGainDb)
            db = MaxComponentGainDb;
        return Mathf.Pow(10f, db / 20f);
    }

    /// <summary>
    /// Converts dB to linear with a unity ceiling (master / routing matrix: −60…0 dB → 0…1).
    /// </summary>
    /// <param name="db">Decibels (0 = full scale; positive values are clamped to 0).</param>
    /// <returns>Linear volume in 0…1.</returns>
    public static float DbToUnityLinear(float db)
    {
        if (db <= MinVolumeDb)
            return 0f;
        if (db > 0f)
            db = 0f;
        return Mathf.Pow(10f, db / 20f);
    }

    /// <summary>
    /// Formats a stereo pan value (−1…1) for UI display: "C", "L50", "R100", etc.
    /// </summary>
    /// <param name="pan">Pan in [−1, 1] (negative = left, positive = right, 0 = center).</param>
    /// <returns>Readable pan status string.</returns>
    public static string FormatPan(float pan)
    {
        pan = Mathf.Clamp(pan, -1f, 1f);
        if (Mathf.IsZeroApprox(pan))
            return "C";
        int percent = Mathf.RoundToInt(Mathf.Abs(pan) * 100f);
        if (percent <= 0)
            return "C";
        if (percent > 100)
            percent = 100;
        return pan < 0f ? $"L{percent}" : $"R{percent}";
    }

    /// <summary>
    /// Parses user pan text into a linear pan value (−1…1).
    /// Accepts: C / center / 0, L / L50 / left, R / R25 / right, or numeric −100…100.
    /// </summary>
    /// <param name="text">User input.</param>
    /// <param name="pan">Parsed pan on success.</param>
    /// <returns>True when parsing succeeded.</returns>
    public static bool TryParsePan(string text, out float pan)
    {
        pan = 0f;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        string cleaned = text.Trim().TrimEnd('%');
        string upper = cleaned.ToUpperInvariant();

        if (upper is "C" or "CENTER" or "CENTRE" or "0")
        {
            pan = 0f;
            return true;
        }

        // L / L50 / LEFT / LEFT50  (must check LEFT before bare L)
        if (upper.StartsWith("LEFT") || (upper.Length >= 1 && upper[0] == 'L'))
        {
            string numPart = upper.StartsWith("LEFT") ? upper.Substring(4) : upper.Substring(1);
            numPart = numPart.Trim();
            if (string.IsNullOrEmpty(numPart))
            {
                pan = -1f;
                return true;
            }
            if (float.TryParse(numPart, out float leftPct))
            {
                pan = -Mathf.Clamp(leftPct / 100f, 0f, 1f);
                return true;
            }
            return false;
        }

        // R / R50 / RIGHT / RIGHT25
        if (upper.StartsWith("RIGHT") || (upper.Length >= 1 && upper[0] == 'R'))
        {
            string numPart = upper.StartsWith("RIGHT") ? upper.Substring(5) : upper.Substring(1);
            numPart = numPart.Trim();
            if (string.IsNullOrEmpty(numPart))
            {
                pan = 1f;
                return true;
            }
            if (float.TryParse(numPart, out float rightPct))
            {
                pan = Mathf.Clamp(rightPct / 100f, 0f, 1f);
                return true;
            }
            return false;
        }

        // Bare number on −100…100 scale (negative = left, positive = right).
        if (!float.TryParse(cleaned, out float value))
            return false;

        if (Mathf.IsZeroApprox(value))
        {
            pan = 0f;
            return true;
        }

        // Values outside ±1 are always percent; ±1 and fractions with a decimal are normalized;
        // whole numbers in 1…100 are percent (so "50" → R50).
        if (Math.Abs(value) > 1f || (!cleaned.Contains('.') && Math.Abs(value) >= 1f))
            pan = Mathf.Clamp(value / 100f, -1f, 1f);
        else
            pan = Mathf.Clamp(value, -1f, 1f);
        return true;
    }
    
    /// <summary>
    /// Recursively sets the colour of all label children of provided root
    /// </summary>
    /// <param name="root">Parent node</param>
    /// <param name="colour">Colour to set labels to</param>
    public static void FormatLabelsColours(Node root, Color colour)
    {

        if (root is Label label)
        {
            label.AddThemeColorOverride("font_color", colour);
        }
        foreach (var child in root.GetChildren())
        {
            FormatLabelsColours(child, colour);
        }
    }

    
    /// <summary>
    /// Cleans and verifies an IP address string.
    /// - Removes invalid characters (non-digits/dots).
    /// - Trims leading/trailing dots.
    /// - Validates as a proper IPv4 address (four octets, each 0-255).
    /// - If invalid, logs an error via GlobalSignals and returns null.
    /// - If valid, returns the cleaned IP string.
    /// </summary>
    /// <param name="input">The raw user input string.</param>
    /// <param name="globalSignals">Reference to GlobalSignals for logging errors. If null, falls back to GD.PrintErr.</param>
    /// <returns>Cleaned valid IP string, or null if invalid.</returns>
    public static string VerifyIpInput(string input, GlobalSignals globalSignals = null)
    {
        try
        {
            string cleaned = CleanRegex.Replace(input ?? "", "").Trim('.');
            
            if (string.IsNullOrEmpty(cleaned) || cleaned.Count(c => c == '.') != 3)
            {
                LogError("Invalid IP format: Must have exactly three dots and non-empty octets.", globalSignals);
                return null;
            }
            
            string[] octets = cleaned.Split('.');
            if (octets.Length != 4 || octets.Any(o => !int.TryParse(o, out int val) || val < 0 || val > 255))
            {
                LogError("Invalid IP: Each octet must be an integer between 0-255.", globalSignals);
                return null;
            }
            
            if (!IpRegex.IsMatch(cleaned))
            {
                LogError("Invalid IP format: Leading zeros not allowed except for octet value 0.", globalSignals);
                return null;
            }

            return cleaned;
        }
        catch (Exception ex)
        {
            LogError($"Unexpected error validating IP: {ex.Message}", globalSignals, 2);
            return null;
        }
    }

    public static IPAddress ValidateIpAddress(string input)
    {
        return IPAddress.None;
    }

    public static int ValidatePort(string input)
    {
        if (int.TryParse(input, out int port))
        {
            if (port >= 1 && port <= 65535)
            {
                return port;
            }
            else
            {
                GD.Print($"SettingsOscListen: Invalid port number. Must be between 1 and 65535.");
                return -1;
            }
        }
        else
        {
            GD.Print($"SettingsOscListen: Invalid port number. Please enter a valid integer.");
            return -1;
        }
    }
    
    
    /// <summary>
    /// Helper to log errors via GlobalSignals if available, else GD.PrintErr.
    /// Uses log level 1 (Warning) by default, or specified level.
    /// </summary>
    private static void LogError(string message, GlobalSignals globalSignals, int logLevel = 1)
    {
        if (globalSignals != null)
        {
            globalSignals.EmitSignal(nameof(GlobalSignals.Log), message, logLevel);
        }
        else
        {
            GD.PrintErr(message);
        }
    }
    
    
    


    /// <summary>
    /// Minimum fraction of the usable screen a first-run main window should occupy.
    /// Design-size × HiDPI scale is still too small on 5K/6K panels.
    /// </summary>
    public const float FirstRunMinScreenFraction = 0.58f;

    /// <summary>
    /// Maximum fraction of the usable screen a first-run main window may occupy.
    /// </summary>
    public const float FirstRunMaxScreenFraction = 0.88f;

    /// <summary>
    /// Resolves the DisplayServer screen index that currently hosts <paramref name="window"/>.
    /// </summary>
    /// <remarks>
    /// Autoload <c>_Ready</c> can run before the native window is mapped; in that case this
    /// falls back to the screen under the mouse, then the primary screen.
    /// </remarks>
    /// <param name="window">Window to locate; null is allowed.</param>
    /// <returns>A valid screen index, or 0 when no screens are reported.</returns>
    public static int ResolveWindowScreen(Window window)
    {
        int count = DisplayServer.GetScreenCount();
        if (count <= 0)
            return 0;

        if (window != null && GodotObject.IsInstanceValid(window))
        {
            int windowId = window.GetWindowId();
            if (windowId >= 0)
            {
                int screen = DisplayServer.WindowGetCurrentScreen(windowId);
                if (screen >= 0 && screen < count)
                    return screen;
            }
        }

        int mouseScreen = FindScreenAtPoint(DisplayServer.MouseGetPosition());
        if (mouseScreen >= 0 && mouseScreen < count)
            return mouseScreen;

        int primary = DisplayServer.GetPrimaryScreen();
        if (primary >= 0 && primary < count)
            return primary;

        return 0;
    }

    /// <summary>
    /// Detects the OS / HiDPI scale factor used as <c>BaseDisplayScale</c>.
    /// </summary>
    /// <remarks>
    /// Combines <see cref="DisplayServer.ScreenGetScale"/>, <see cref="DisplayServer.ScreenGetMaxScale"/>
    /// (Godot uses max scale for macOS window pixel conversion), and a DPI/resolution guess when the
    /// OS still reports 1× — common on first frame, mixed-DPI setups, and 1× 4K/5K panels.
    /// Do not multiply the result by max-scale again; the guess is only applied when OS scale is ~1.
    /// </remarks>
    /// <param name="screen">Screen index, or -1 to use the primary/current screen.</param>
    /// <returns>Scale factor clamped to 1–4.</returns>
    public static float DetectDisplayScale(int screen = -1)
    {
        int count = DisplayServer.GetScreenCount();
        if (screen < 0 || (count > 0 && screen >= count))
            screen = count > 0 ? DisplayServer.GetPrimaryScreen() : 0;
        if (screen < 0)
            screen = 0;

        float osScale = SafePositiveScale(DisplayServer.ScreenGetScale(screen));
        float maxScale = SafePositiveScale(DisplayServer.ScreenGetMaxScale());
        float scale = Mathf.Max(osScale, maxScale);

        Vector2I size = DisplayServer.ScreenGetSize(screen);
        int dpi = DisplayServer.ScreenGetDpi(screen);
        float guessed = 1f;
        if (scale <= 1.05f)
        {
            guessed = GuessScaleFromScreenMetrics(size, dpi);
            scale = Mathf.Max(scale, guessed);
        }

        scale = Mathf.Clamp(scale, 1f, 4f);
        GD.Print($"UiUtilities:DetectDisplayScale - screen={screen} osScale={osScale:0.###} " +
                 $"maxScale={maxScale:0.###} dpi={dpi} size={size} guessed={guessed:0.###} result={scale:0.###}");
        return scale;
    }

    /// <summary>
    /// Grows a first-run window toward a comfortable fraction of the usable screen.
    /// Never shrinks below <paramref name="scaledDesign"/> (except to fit the max fraction / min size).
    /// </summary>
    /// <param name="scaledDesign">Design-time size already multiplied by display scale.</param>
    /// <param name="window">Window used to pick the host screen.</param>
    /// <param name="minSize">Minimum outer size.</param>
    /// <returns>Size clamped to the usable screen.</returns>
    public static Vector2I FitFirstRunWindowToUsableScreen(Vector2I scaledDesign, Window window, Vector2I minSize)
    {
        Rect2I usable = GetUsableRectForWindow(window);
        int w = Mathf.Max(minSize.X, scaledDesign.X);
        int h = Mathf.Max(minSize.Y, scaledDesign.Y);

        if (usable.Size.X > 0 && usable.Size.Y > 0)
        {
            int minW = Mathf.RoundToInt(usable.Size.X * FirstRunMinScreenFraction);
            int minH = Mathf.RoundToInt(usable.Size.Y * FirstRunMinScreenFraction);
            int maxW = Mathf.RoundToInt(usable.Size.X * FirstRunMaxScreenFraction);
            int maxH = Mathf.RoundToInt(usable.Size.Y * FirstRunMaxScreenFraction);

            w = Mathf.Max(w, minW);
            h = Mathf.Max(h, minH);
            w = Mathf.Clamp(w, minSize.X, Mathf.Max(minSize.X, maxW));
            h = Mathf.Clamp(h, minSize.Y, Mathf.Max(minSize.Y, maxH));
        }

        return new Vector2I(w, h);
    }

    /// <summary>
    /// Centers a window size on the usable area of the screen that hosts <paramref name="window"/>
    /// (excludes the macOS menu bar).
    /// </summary>
    /// <param name="windowSize">Outer size to place.</param>
    /// <param name="window">Window used to pick the host screen.</param>
    /// <returns>Global position suitable for <see cref="DisplayServer.WindowSetPosition"/>.</returns>
    public static Vector2I CenterWindowOnUsableScreen(Vector2I windowSize, Window window)
    {
        Rect2I usable = GetUsableRectForWindow(window);
        int x = usable.Position.X + Mathf.Max(0, (usable.Size.X - windowSize.X) / 2);
        int y = usable.Position.Y + Mathf.Max(0, (usable.Size.Y - windowSize.Y) / 2);
        return new Vector2I(x, y);
    }

    /// <summary>
    /// Releases a C#-created Godot object so engine shutdown does not report
    /// "Leaked unsafe reference". Safe for null or already-freed instances.
    /// </summary>
    /// <param name="obj">RefCounted resource or Object created from C# (<c>new StyleBoxFlat()</c>, Duplicate, etc.).</param>
    public static void DisposeRefCounted(GodotObject obj)
    {
        if (obj == null || !GodotObject.IsInstanceValid(obj))
            return;
        obj.Dispose();
    }

    /// <summary>
    /// Applies content scale factor for UI density. Does not intentionally change the OS window
    /// outer size — callers that need a design-time frame size should use <see cref="RescaleWindow"/>
    /// only when no saved geometry exists.
    /// </summary>
    /// <param name="window">Target window.</param>
    /// <param name="scale">User UI scale (typically 0.25–4.0).</param>
    /// <param name="baseDisplayScale">HiDPI factor from <see cref="DetectDisplayScale"/>.</param>
    public static void RescaleUi(Window window, double scale, double baseDisplayScale = 1.0)
    {
        GD.Print($"UiUtilities:RescaleUi - Scale: {scale}, Display Scale: {baseDisplayScale}");
        try
        {
            var effectiveScale = scale * baseDisplayScale;
            // ContentScaleFactor only. Leaving WrapControls alone avoids growing/shrinking the
            // outer frame when scale changes (which previously clobbered restored geometry).
            window.ContentScaleFactor = (float)effectiveScale;
            window.ChildControlsChanged();
        }
        catch (Exception ex)
        {
            GD.PrintErr($"UiUtilities:RescaleUI - Error applying UI scale: {ex.Message}");
            window.ContentScaleFactor = (float)scale; // Fallback to original value without multiplier
        }
    }

    /// <summary>
    /// Scales the window's outer pixel size by <paramref name="scale"/> and recenters on the
    /// usable screen. Only for design-time / first-run defaults — never call after restoring
    /// saved geometry, and never call twice on an already-scaled size.
    /// </summary>
    public static void RescaleWindow(Window window, double scale)
    {
        if (window == null || !GodotObject.IsInstanceValid(window))
            return;
        if (scale <= 0.0 || Mathf.IsEqualApprox((float)scale, 1f))
            return;

        ApplyScaledDesignWindowSize(window, window.Size, scale);
    }

    /// <summary>
    /// Sets outer size to <paramref name="designSize"/> × <paramref name="displayScale"/>, clamped
    /// so it fits the usable screen, and centers on that screen.
    /// </summary>
    /// <param name="window">Target window.</param>
    /// <param name="designSize">Unscaled scene / project default size.</param>
    /// <param name="displayScale">HiDPI / detected display scale.</param>
    public static void ApplyScaledDesignWindowSize(Window window, Vector2I designSize, double displayScale)
    {
        if (window == null || !GodotObject.IsInstanceValid(window))
            return;

        float scale = displayScale > 0.0 ? (float)displayScale : 1f;
        Vector2I newSize = new Vector2I(
            Mathf.Max(1, Mathf.RoundToInt(designSize.X * scale)),
            Mathf.Max(1, Mathf.RoundToInt(designSize.Y * scale)));

        Rect2I usable = GetUsableRectForWindow(window);
        if (usable.Size.X > 0 && usable.Size.Y > 0)
        {
            int maxW = Mathf.RoundToInt(usable.Size.X * FirstRunMaxScreenFraction);
            int maxH = Mathf.RoundToInt(usable.Size.Y * FirstRunMaxScreenFraction);
            newSize = new Vector2I(
                Mathf.Min(newSize.X, Mathf.Max(1, maxW)),
                Mathf.Min(newSize.Y, Mathf.Max(1, maxH)));
        }

        window.Size = newSize;
        window.Position = CenterWindowOnUsableScreen(newSize, window);
    }

    /// <summary>
    /// Usable desktop rectangle for <paramref name="window"/>'s screen, falling back to the
    /// full screen bounds when the OS reports an empty usable rect.
    /// </summary>
    public static Rect2I GetUsableRectForWindow(Window window)
    {
        int screen = ResolveWindowScreen(window);
        Rect2I usable = DisplayServer.ScreenGetUsableRect(screen);
        if (usable.Size.X > 0 && usable.Size.Y > 0)
            return usable;

        Vector2I pos = DisplayServer.ScreenGetPosition(screen);
        Vector2I size = DisplayServer.ScreenGetSize(screen);
        if (size.X <= 0 || size.Y <= 0)
            return new Rect2I(Vector2I.Zero, new Vector2I(1920, 1080));
        return new Rect2I(pos, size);
    }

    private static float SafePositiveScale(float value)
    {
        return value > 0f ? value : 1f;
    }

    /// <summary>
    /// Godot-editor-style scale guess from pixel size and DPI. Used only when the OS scale is ~1×.
    /// </summary>
    private static float GuessScaleFromScreenMetrics(Vector2I size, int dpi)
    {
        if (size.X <= 0 || size.Y <= 0)
            return 1f;

        int smallest = Mathf.Min(size.X, size.Y);
        float fromDpi = 1f;
        if (dpi >= 220)
            fromDpi = 2f;
        else if (dpi >= 168)
            fromDpi = 1.5f;
        else if (dpi >= 140)
            fromDpi = 1.25f;

        float fromSize = 1f;
        if (size.X >= 5120 && size.Y >= 2160)
            fromSize = 2f;
        else if (size.X >= 3840 && size.Y >= 2160)
            fromSize = dpi >= 140 ? 1.5f : 1.25f;
        else if (dpi >= 192 && smallest >= 1400)
            fromSize = 2f;
        else if (dpi >= 144 && smallest >= 1400)
            fromSize = 1.5f;
        else if (dpi >= 192 && smallest <= 800)
            fromSize = 1.5f;

        return Mathf.Max(fromDpi, fromSize);
    }

    /// <summary>
    /// Maps a desktop-screen point to the coordinate space <see cref="Window.Position"/> expects.
    /// Native windows use screen coordinates; embedded windows use the embedder viewport.
    /// </summary>
    /// <param name="popup">Popup or other window being placed.</param>
    /// <param name="screenPoint">Point in desktop screen coordinates (e.g. from <see cref="Control.GetScreenTransform"/>).</param>
    /// <returns>Position suitable for <see cref="Window.Position"/> or <see cref="Window.Popup(Rect2I)"/>.</returns>
    public static Vector2I ScreenPointToPopupPosition(Window popup, Vector2 screenPoint)
    {
        if (popup == null || !GodotObject.IsInstanceValid(popup) || !popup.IsEmbedded())
            return new Vector2I(Mathf.RoundToInt(screenPoint.X), Mathf.RoundToInt(screenPoint.Y));

        Window embedder = FindParentWindow(popup);
        if (embedder == null)
            return new Vector2I(Mathf.RoundToInt(screenPoint.X), Mathf.RoundToInt(screenPoint.Y));

        Vector2 local = embedder.GetScreenTransform().AffineInverse() * screenPoint;
        return new Vector2I(Mathf.RoundToInt(local.X), Mathf.RoundToInt(local.Y));
    }

    /// <summary>
    /// Usable rectangle in the same coordinate space as <see cref="Window.Position"/> for
    /// <paramref name="popup"/> (desktop usable rect when native; embedder viewport when embedded).
    /// </summary>
    /// <param name="popup">Popup being placed.</param>
    /// <returns>Clamp bounds for the popup position and size.</returns>
    public static Rect2I GetPopupUsableRect(Window popup)
    {
        if (popup != null && GodotObject.IsInstanceValid(popup) && popup.IsEmbedded())
        {
            Window embedder = FindParentWindow(popup);
            if (embedder != null)
                return new Rect2I(Vector2I.Zero, embedder.Size);
        }

        int screenIdx = DisplayServer.WindowGetCurrentScreen();
        return DisplayServer.ScreenGetUsableRect(screenIdx);
    }

    /// <summary>
    /// Mouse position in the coordinate space <paramref name="popup"/> uses for <see cref="Window.Position"/>.
    /// </summary>
    /// <param name="popup">Popup being placed.</param>
    /// <param name="viewportHost">Control in the same viewport as the popup (used when embedded).</param>
    /// <returns>Position suitable for <see cref="Window.Position"/>.</returns>
    public static Vector2I GetPopupMousePosition(Window popup, Control viewportHost = null)
    {
        if (popup != null && GodotObject.IsInstanceValid(popup) && popup.IsEmbedded())
        {
            if (viewportHost != null && GodotObject.IsInstanceValid(viewportHost))
                return (Vector2I)viewportHost.GetGlobalMousePosition();

            Window embedder = FindParentWindow(popup);
            if (embedder != null)
                return (Vector2I)embedder.GetMousePosition();
        }

        return DisplayServer.MouseGetPosition();
    }

    /// <summary>
    /// Walks ancestors to find the native/parent <see cref="Window"/> that embeds <paramref name="node"/>.
    /// </summary>
    /// <param name="node">Typically an embedded popup.</param>
    /// <returns>The parent window, or null if none is in the tree yet.</returns>
    private static Window FindParentWindow(Node node)
    {
        Node current = node?.GetParent();
        while (current != null)
        {
            if (current is Window window)
                return window;
            current = current.GetParent();
        }

        return null;
    }

    /// <summary>
    /// True when the window fills the screen via maximize or any fullscreen mode.
    /// </summary>
    /// <param name="window">Window to inspect.</param>
    /// <returns>True if maximized or fullscreen; false if null/invalid or windowed.</returns>
    public static bool IsWindowFillScreen(Window window)
    {
        if (window == null || !GodotObject.IsInstanceValid(window))
            return false;

        return window.Mode is Window.ModeEnum.Maximized
            or Window.ModeEnum.Fullscreen
            or Window.ModeEnum.ExclusiveFullscreen;
    }

    /// <summary>
    /// Leaves maximized/fullscreen before interactive drag or edge-resize.
    /// </summary>
    /// <remarks>
    /// Borderless windows that remain maximized while the OS changes their frame size
    /// end up in a hybrid state: still "maximized" while no longer full-screen, which
    /// breaks layout/content scaling until the next full mode toggle. Always call this
    /// before <see cref="DisplayServer.WindowStartResize"/> or <see cref="DisplayServer.WindowStartDrag"/>.
    /// Prefer applying a known normal size/position so restore is not full-monitor sized.
    /// </remarks>
    /// <param name="window">Target window.</param>
    /// <param name="restoreSize">Optional normal size to apply after leaving fill-screen mode.</param>
    /// <param name="restorePosition">Optional global position to apply after leaving fill-screen mode.</param>
    /// <returns>True if the mode was changed to windowed.</returns>
    public static bool EnsureWindowedForInteraction(
        Window window,
        Vector2I? restoreSize = null,
        Vector2I? restorePosition = null)
    {
        if (window == null || !GodotObject.IsInstanceValid(window))
            return false;

        if (!IsWindowFillScreen(window))
            return false;

        // Leave fill-screen first so subsequent Size/Position apply as true windowed geometry.
        window.Mode = Window.ModeEnum.Windowed;

        if (restoreSize is Vector2I size && size.X > 0 && size.Y > 0)
            window.Size = size;

        if (restorePosition is Vector2I pos)
            window.Position = pos;

        return true;
    }

    /// <summary>
    /// Toggles between maximized and windowed. Prefer <see cref="ToggleFullscreen"/> for the
    /// main window chrome; this remains for callers that want OS maximize rather than fullscreen.
    /// </summary>
    /// <param name="window">Target window.</param>
    public static void ToggleMaximize(Window window)
    {
        if (window == null || !GodotObject.IsInstanceValid(window))
            return;

        if (IsWindowFillScreen(window))
            window.Mode = Window.ModeEnum.Windowed;
        else
            window.Mode = Window.ModeEnum.Maximized;
    }

    /// <summary>
    /// Toggles between non-exclusive fullscreen and windowed.
    /// Does not use ExclusiveFullscreen (avoids black flashes / exclusive display takeover).
    /// </summary>
    /// <param name="window">Target window.</param>
    public static void ToggleFullscreen(Window window)
    {
        if (window == null || !GodotObject.IsInstanceValid(window))
            return;

        if (IsWindowFillScreen(window))
            window.Mode = Window.ModeEnum.Windowed;
        else
            window.Mode = Window.ModeEnum.Fullscreen;
    }

    /// <summary>
    /// Computes absolute window position from a display-relative position on the given screen,
    /// clamped so the window stays at least partially visible.
    /// </summary>
    /// <param name="screenIndex">DisplayServer screen index.</param>
    /// <param name="relativePosition">Position relative to that screen's top-left.</param>
    /// <param name="minVisibleWidth">Minimum horizontal overlap kept on-screen.</param>
    /// <param name="minVisibleHeight">Minimum vertical overlap kept on-screen.</param>
    /// <returns>Clamped global position.</returns>
    public static Vector2I ClampWindowPositionToScreen(
        int screenIndex,
        Vector2I relativePosition,
        int minVisibleWidth = 200,
        int minVisibleHeight = 80)
    {
        int count = DisplayServer.GetScreenCount();
        if (count <= 0)
            return relativePosition;

        screenIndex = Mathf.Clamp(screenIndex, 0, count - 1);
        Vector2I screenPos = DisplayServer.ScreenGetPosition(screenIndex);
        Vector2I screenSize = DisplayServer.ScreenGetSize(screenIndex);
        Vector2I absPos = screenPos + relativePosition;

        absPos.X = Mathf.Clamp(absPos.X, screenPos.X, screenPos.X + screenSize.X - minVisibleWidth);
        absPos.Y = Mathf.Clamp(absPos.Y, screenPos.Y, screenPos.Y + screenSize.Y - minVisibleHeight);
        return absPos;
    }

    /// <summary>
    /// Finds the screen index whose bounds contain <paramref name="point"/>, or 0 if none.
    /// </summary>
    public static int FindScreenAtPoint(Vector2I point)
    {
        int count = DisplayServer.GetScreenCount();
        for (int i = 0; i < count; i++)
        {
            Vector2I sPos = DisplayServer.ScreenGetPosition(i);
            Vector2I sSize = DisplayServer.ScreenGetSize(i);
            if (new Rect2I(sPos, sSize).HasPoint(point))
                return i;
        }

        return 0;
    }

    /// <summary>
    /// Converts a global window position to coordinates relative to the screen that contains
    /// the top-left (or the window center / nearest screen as fallback).
    /// </summary>
    /// <remarks>
    /// Always returns screen-relative coordinates. Older code returned the raw global position
    /// when no screen contained the point, which later restores mis-placed the window (common
    /// with multi-monitor and macOS coordinate quirks).
    /// </remarks>
    public static Vector2I ToScreenRelativePosition(Vector2I globalPos, Vector2I windowSize)
    {
        int count = DisplayServer.GetScreenCount();
        if (count <= 0)
            return globalPos;

        for (int i = 0; i < count; i++)
        {
            Vector2I sPos = DisplayServer.ScreenGetPosition(i);
            Vector2I sSize = DisplayServer.ScreenGetSize(i);
            if (new Rect2I(sPos, sSize).HasPoint(globalPos))
                return globalPos - sPos;
        }

        Vector2I center = globalPos + (windowSize / 2);
        for (int i = 0; i < count; i++)
        {
            Vector2I sPos = DisplayServer.ScreenGetPosition(i);
            Vector2I sSize = DisplayServer.ScreenGetSize(i);
            if (new Rect2I(sPos, sSize).HasPoint(center))
                return globalPos - sPos;
        }

        int nearest = FindNearestScreen(center);
        return globalPos - DisplayServer.ScreenGetPosition(nearest);
    }

    /// <summary>
    /// Resolves a saved window position that may be screen-relative or (legacy) absolute global.
    /// Picks the placement that keeps the most of the window on-screen.
    /// </summary>
    /// <param name="savedPosition">Position from user data (relative preferred; absolute tolerated).</param>
    /// <param name="windowSize">Restored window size used for visibility scoring.</param>
    /// <returns>Clamped global position suitable for <see cref="DisplayServer.WindowSetPosition"/>.</returns>
    public static Vector2I ResolveSavedWindowPosition(Vector2I savedPosition, Vector2I windowSize)
    {
        int count = DisplayServer.GetScreenCount();
        if (count <= 0)
            return savedPosition;

        Vector2I best = savedPosition;
        long bestScore = -1;

        void Consider(Vector2I absPos)
        {
            long score = ComputeOnScreenArea(absPos, windowSize);
            if (score > bestScore)
            {
                bestScore = score;
                best = absPos;
            }
        }

        // Prefer treating the value as relative to the screen under the mouse (session restore UX).
        int mouseScreen = FindScreenAtPoint(DisplayServer.MouseGetPosition());
        Consider(DisplayServer.ScreenGetPosition(mouseScreen) + savedPosition);

        // Also try relative to every screen and as an absolute global (legacy bad saves).
        for (int i = 0; i < count; i++)
            Consider(DisplayServer.ScreenGetPosition(i) + savedPosition);
        Consider(savedPosition);

        // Final clamp onto the screen that best contains the chosen placement.
        int host = FindNearestScreen(best + (windowSize / 2));
        Vector2I hostPos = DisplayServer.ScreenGetPosition(host);
        return ClampWindowPositionToScreen(host, best - hostPos);
    }

    /// <summary>
    /// Clamps a window size so it does not exceed the virtual desktop (union of all monitors).
    /// </summary>
    public static Vector2I ClampWindowSizeToVirtualDesktop(Vector2I size, Vector2I minSize)
    {
        Vector2I desktop = ComputeVirtualDesktopSize();
        int w = Mathf.Clamp(size.X, minSize.X, Mathf.Max(minSize.X, desktop.X));
        int h = Mathf.Clamp(size.Y, minSize.Y, Mathf.Max(minSize.Y, desktop.Y));
        return new Vector2I(w, h);
    }

    /// <summary>
    /// Union of all monitor rectangles — upper bound for window size.
    /// </summary>
    public static Vector2I ComputeVirtualDesktopSize()
    {
        int screenCount = DisplayServer.GetScreenCount();
        if (screenCount <= 0)
            return new Vector2I(4096, 2160);

        Vector2I minPos = new Vector2I(int.MaxValue, int.MaxValue);
        Vector2I maxPos = new Vector2I(int.MinValue, int.MinValue);
        for (int i = 0; i < screenCount; i++)
        {
            Vector2I pos = DisplayServer.ScreenGetPosition(i);
            Vector2I sSize = DisplayServer.ScreenGetSize(i);
            minPos = new Vector2I(Mathf.Min(minPos.X, pos.X), Mathf.Min(minPos.Y, pos.Y));
            maxPos = new Vector2I(Mathf.Max(maxPos.X, pos.X + sSize.X), Mathf.Max(maxPos.Y, pos.Y + sSize.Y));
        }

        return maxPos - minPos;
    }

    /// <summary>
    /// Finds the screen whose center is nearest to <paramref name="point"/>.
    /// </summary>
    public static int FindNearestScreen(Vector2I point)
    {
        int count = DisplayServer.GetScreenCount();
        if (count <= 0)
            return 0;

        int best = 0;
        long bestDist = long.MaxValue;
        for (int i = 0; i < count; i++)
        {
            Vector2I sPos = DisplayServer.ScreenGetPosition(i);
            Vector2I sSize = DisplayServer.ScreenGetSize(i);
            Vector2I center = sPos + (sSize / 2);
            long dx = (long)point.X - center.X;
            long dy = (long)point.Y - center.Y;
            long dist = dx * dx + dy * dy;
            if (dist < bestDist)
            {
                bestDist = dist;
                best = i;
            }
        }

        return best;
    }

    /// <summary>
    /// Commits a <see cref="LineEdit"/> on Enter <b>and</b> when focus leaves the field
    /// (clicking another control, tab, etc.) so the displayed text cannot diverge from the model.
    /// </summary>
    /// <remarks>
    /// Safe to call more than once on the same instance. Hosts must keep
    /// <paramref name="onCommit"/> idempotent: Enter still fires <see cref="LineEdit.TextSubmitted"/>
    /// then <see cref="Control.FocusExited"/> after the global unfocus hook.
    /// </remarks>
    /// <param name="lineEdit">Field to wire (ignored if null/invalid).</param>
    /// <param name="onCommit">Apply <c>text</c> to the model (and revert the field on invalid input).</param>
    public static void BindLineEditCommit(LineEdit lineEdit, Action<string> onCommit)
    {
        if (lineEdit == null || !GodotObject.IsInstanceValid(lineEdit) || onCommit == null)
            return;

        const string metaKey = "cue2_commit_on_blur";
        if (lineEdit.HasMeta(metaKey))
            return;
        lineEdit.SetMeta(metaKey, true);

        lineEdit.TextSubmitted += text => onCommit(text ?? string.Empty);
        lineEdit.FocusExited += () =>
        {
            if (!GodotObject.IsInstanceValid(lineEdit))
                return;
            onCommit(lineEdit.Text ?? string.Empty);
        };
    }

    /// <summary>
    /// Wires Escape on a <see cref="LineEdit"/> to end edit mode and release focus.
    /// </summary>
    /// <remarks>
    /// Safe to call more than once (hooks stack; release is idempotent). Prefer calling when the
    /// field is created so Esc always unfocuses even if global text-field policy is unavailable.
    /// FocusExited still runs after release (commit/cancel remains the host's responsibility).
    /// </remarks>
    /// <param name="lineEdit">Target field (ignored if null/invalid).</param>
    public static void WireLineEditEscapeReleasesFocus(LineEdit lineEdit)
    {
        if (lineEdit == null || !GodotObject.IsInstanceValid(lineEdit))
            return;

        // Avoid double-connect if already tagged by a previous call on this instance.
        const string metaKey = "cue2_esc_unfocus";
        if (lineEdit.HasMeta(metaKey))
            return;
        lineEdit.SetMeta(metaKey, true);

        lineEdit.GuiInput += @event =>
        {
            if (!GodotObject.IsInstanceValid(lineEdit) || !lineEdit.HasFocus())
                return;
            if (@event is not InputEventKey key || !key.Pressed || key.Echo)
                return;
            if (key.Keycode != Key.Escape
                && key.PhysicalKeycode != Key.Escape
                && !key.IsAction("ui_cancel"))
                return;

            if (lineEdit.IsEditing())
                lineEdit.Unedit();
            lineEdit.ReleaseFocus();
            lineEdit.AcceptEvent();
            lineEdit.GetViewport()?.SetInputAsHandled();
            // Same-frame re-focus races (e.g. after submit/engine edit mode).
            lineEdit.CallDeferred(Control.MethodName.ReleaseFocus);
        };
    }

    /// <summary>
    /// Configures a MIDI/OSC monitor <see cref="CodeEdit"/> as a non-interactive log view.
    /// </summary>
    /// <remarks>
    /// Read-only, no keyboard focus, no selection/shortcuts/context menu. Scroll still works
    /// (mouse wheel / scrollbar) so long histories remain usable without stealing GO/hotkeys.
    /// </remarks>
    /// <param name="monitorLog">The monitor CodeEdit to configure (no-op if null).</param>
    /// <param name="fontColor">Optional monospace log text color; default is a soft green.</param>
    public static void ConfigureReadOnlyMonitorLog(CodeEdit monitorLog, Color? fontColor = null)
    {
        if (monitorLog == null || !GodotObject.IsInstanceValid(monitorLog))
            return;

        Color text = fontColor ?? new Color(0.75f, 0.95f, 0.75f, 1f);

        monitorLog.Editable = false;
        monitorLog.FocusMode = Control.FocusModeEnum.None;
        monitorLog.ContextMenuEnabled = false;
        monitorLog.SelectingEnabled = false;
        monitorLog.DragAndDropSelectionEnabled = false;
        monitorLog.ShortcutKeysEnabled = false;
        monitorLog.MiddleMousePasteEnabled = false;
        monitorLog.GuttersDrawLineNumbers = false;
        monitorLog.ScrollPastEndOfFile = false;
        monitorLog.WrapMode = TextEdit.LineWrappingMode.None;
        monitorLog.CaretBlink = false;
        monitorLog.CaretType = TextEdit.CaretTypeEnum.Line;
        monitorLog.MouseDefaultCursorShape = Control.CursorShape.Arrow;

        // Keep mouse for scrolling only; never take focus on click.
        monitorLog.MouseFilter = Control.MouseFilterEnum.Stop;
        if (monitorLog.HasFocus())
            monitorLog.ReleaseFocus();

        var bg = new StyleBoxFlat
        {
            BgColor = new Color(0.05f, 0.05f, 0.05f, 1f),
            BorderColor = new Color(0.22f, 0.22f, 0.22f, 1f),
            ContentMarginLeft = 8,
            ContentMarginRight = 8,
            ContentMarginTop = 6,
            ContentMarginBottom = 6,
        };
        bg.SetBorderWidthAll(1);
        bg.SetCornerRadiusAll(3);
        monitorLog.AddThemeStyleboxOverride("normal", bg);
        monitorLog.AddThemeStyleboxOverride("focus", bg);
        monitorLog.AddThemeStyleboxOverride("read_only", bg);

        monitorLog.AddThemeColorOverride("font_color", text);
        monitorLog.AddThemeColorOverride("font_readonly_color", text);
        monitorLog.AddThemeColorOverride("caret_color", new Color(text.R, text.G, text.B, 0.35f));
        monitorLog.AddThemeColorOverride("background_color", new Color(0.05f, 0.05f, 0.05f, 1f));
        monitorLog.AddThemeFontSizeOverride("font_size", 12);
    }

    /// <summary>
    /// Approximate visible area of a window rect against the union of all screens.
    /// </summary>
    private static long ComputeOnScreenArea(Vector2I windowPos, Vector2I windowSize)
    {
        if (windowSize.X <= 0 || windowSize.Y <= 0)
            return 0;

        long area = 0;
        int count = DisplayServer.GetScreenCount();
        for (int i = 0; i < count; i++)
        {
            Vector2I sPos = DisplayServer.ScreenGetPosition(i);
            Vector2I sSize = DisplayServer.ScreenGetSize(i);

            int x0 = Mathf.Max(windowPos.X, sPos.X);
            int y0 = Mathf.Max(windowPos.Y, sPos.Y);
            int x1 = Mathf.Min(windowPos.X + windowSize.X, sPos.X + sSize.X);
            int y1 = Mathf.Min(windowPos.Y + windowSize.Y, sPos.Y + sSize.Y);
            int w = x1 - x0;
            int h = y1 - y0;
            if (w > 0 && h > 0)
                area += (long)w * h;
        }

        return area;
    }
}