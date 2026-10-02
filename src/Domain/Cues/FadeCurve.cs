// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System;

namespace Cue2.Domain.Cues;

/// <summary>
/// Maps normalized fade time (0–1) to a 0–1 amplitude gain for a <see cref="FadeCurveType"/>.
/// </summary>
public static class FadeCurve
{
    /// <summary>Steepness for exponential / logarithmic shapes.</summary>
    public const float ExpK = 4f;

    /// <summary>
    /// Evaluates the fade gain at normalized time <paramref name="t"/>.
    /// </summary>
    /// <param name="t">Progress through the fade, 0 at start and 1 at end.</param>
    /// <param name="type">Curve shape.</param>
    /// <returns>Linear amplitude in 0–1.</returns>
    public static float Evaluate(float t, FadeCurveType type)
    {
        t = Math.Clamp(t, 0f, 1f);
        return type switch
        {
            FadeCurveType.SCurve => t * t * (3f - 2f * t),
            FadeCurveType.Exponential => (MathF.Exp(ExpK * t) - 1f) / (MathF.Exp(ExpK) - 1f),
            FadeCurveType.Logarithmic => MathF.Log(1f + (MathF.Exp(ExpK) - 1f) * t) / ExpK,
            _ => t
        };
    }

    /// <summary>
    /// English display name for an inspector OptionButton.
    /// </summary>
    /// <param name="type">Curve shape.</param>
    /// <returns>English caption.</returns>
    public static string DisplayName(FadeCurveType type) => type switch
    {
        FadeCurveType.SCurve => "S-Curve",
        FadeCurveType.Exponential => "Exponential",
        FadeCurveType.Logarithmic => "Logarithmic",
        _ => "Linear"
    };

    /// <summary>
    /// English tooltip for an inspector OptionButton item.
    /// </summary>
    /// <param name="type">Curve shape.</param>
    /// <param name="fadeOut">True for fade-out wording; false for fade-in.</param>
    /// <returns>English tooltip.</returns>
    public static string Tooltip(FadeCurveType type, bool fadeOut = false)
    {
        if (fadeOut)
        {
            return type switch
            {
                FadeCurveType.SCurve => "Slow at both ends, faster in the middle.",
                FadeCurveType.Exponential => "Stays loud, then drops quickly toward silence.",
                FadeCurveType.Logarithmic => "Drops quickly, then eases into silence.",
                _ => "Constant slope from full level to silence."
            };
        }

        return type switch
        {
            FadeCurveType.SCurve => "Slow at both ends, faster in the middle.",
            FadeCurveType.Exponential => "Starts quiet, then rises quickly.",
            FadeCurveType.Logarithmic => "Rises quickly, then eases into full level.",
            _ => "Constant slope from silence to full level."
        };
    }

    /// <summary>
    /// All curve types in inspector order.
    /// </summary>
    public static readonly FadeCurveType[] All =
    {
        FadeCurveType.Linear,
        FadeCurveType.SCurve,
        FadeCurveType.Exponential,
        FadeCurveType.Logarithmic
    };

    /// <summary>
    /// Parses a stored integer; unknown values become <see cref="FadeCurveType.Linear"/>.
    /// </summary>
    /// <param name="value">Serialized enum integer.</param>
    /// <returns>A defined <see cref="FadeCurveType"/>.</returns>
    public static FadeCurveType FromInt(int value)
    {
        foreach (var type in All)
        {
            if ((int)type == value)
                return type;
        }

        return FadeCurveType.Linear;
    }
}
