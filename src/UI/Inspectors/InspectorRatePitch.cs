// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Globalization;
using Cue2.Domain.Cues;
using Godot;

namespace Cue2.UI.Inspectors;

/// <summary>
/// Shared play rate and pitch field helpers for the audio and video inspectors and the
/// inspector timeline node list: parse, clamp, format, and multi-target display.
/// </summary>
public static class InspectorRatePitch
{
    /// <summary>Formats a play rate for a field (clamped, up to 3 decimals).</summary>
    /// <param name="rate">Play rate multiplier.</param>
    /// <returns>Display text, for example "1" or "0.75".</returns>
    public static string FormatRate(double rate)
    {
        rate = AudioComponent.ClampPlayRate(rate);
        return rate.ToString("0.###");
    }

    /// <summary>Formats a pitch offset for a field (clamped, whole cents).</summary>
    /// <param name="cents">Pitch offset in cents.</param>
    /// <returns>Display text, for example "-100".</returns>
    public static string FormatCents(float cents)
    {
        int rounded = (int)Math.Round(AudioComponent.ClampPitchCents(cents));
        return rounded.ToString();
    }

    /// <summary>
    /// Parses a play rate typed by the user. Accepts the current culture's number format and
    /// the invariant one (a "." decimal point always works). The result is clamped.
    /// </summary>
    /// <param name="text">Field text.</param>
    /// <param name="rate">Clamped rate when the text is a number above 0.</param>
    /// <returns>True when the text is a valid rate.</returns>
    public static bool TryParseRate(string text, out double rate)
    {
        rate = 0;
        string trimmed = (text ?? string.Empty).Trim();
        if (!TryParseNumber(trimmed, out double value) || value <= 0)
            return false;
        rate = AudioComponent.ClampPlayRate(value);
        return true;
    }

    /// <summary>
    /// Parses a pitch offset typed by the user, ignoring a trailing cent sign or "c".
    /// The result is clamped.
    /// </summary>
    /// <param name="text">Field text.</param>
    /// <param name="cents">Clamped cents when the text is a number.</param>
    /// <returns>True when the text is a valid pitch offset.</returns>
    public static bool TryParseCents(string text, out float cents)
    {
        cents = 0;
        string trimmed = (text ?? string.Empty).Trim().TrimEnd('¢', 'c', 'C');
        if (!TryParseNumber(trimmed, out double value))
            return false;
        cents = AudioComponent.ClampPitchCents((float)value);
        return true;
    }

    /// <summary>Shows a shared play rate, or blank with the multi placeholder when targets differ.</summary>
    /// <param name="field">Rate field (ignored when null).</param>
    /// <param name="rates">Play rate of each target.</param>
    public static void SyncRateField(LineEdit field, IEnumerable<double> rates)
    {
        if (field == null)
            return;
        if (InspectorMultiEditSupport.TryGetUniformDouble(rates, out double rate))
        {
            field.Text = FormatRate(rate);
            field.PlaceholderText = string.Empty;
        }
        else
        {
            field.Text = string.Empty;
            field.PlaceholderText = InspectorMultiEditSupport.MultiPlaceholder;
        }
    }

    /// <summary>Shows a shared keep-pitch state; unchecked when targets differ.</summary>
    /// <param name="check">Keep-pitch check box (ignored when null).</param>
    /// <param name="keepPitch">Keep-pitch flag of each target.</param>
    public static void SyncKeepPitch(CheckBox check, IEnumerable<bool> keepPitch)
    {
        if (check == null)
            return;
        if (InspectorMultiEditSupport.TryGetUniform(keepPitch, out bool keep))
            check.SetPressedNoSignal(keep);
        else
            check.SetPressedNoSignal(false);
    }

    /// <summary>Shows a shared pitch offset, or blank with the multi placeholder when targets differ.</summary>
    /// <param name="field">Pitch field (ignored when null).</param>
    /// <param name="cents">Pitch offset of each target.</param>
    public static void SyncCentsField(LineEdit field, IEnumerable<float> cents)
    {
        if (field == null)
            return;
        if (InspectorMultiEditSupport.TryGetUniformFloat(cents, out float value))
        {
            field.Text = FormatCents(value);
            field.PlaceholderText = string.Empty;
        }
        else
        {
            field.Text = string.Empty;
            field.PlaceholderText = InspectorMultiEditSupport.MultiPlaceholder;
        }
    }

    private static bool TryParseNumber(string text, out double value) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value)
        || double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
}
