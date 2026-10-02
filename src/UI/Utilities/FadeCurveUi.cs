// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using Cue2.Domain.Cues;
using Godot;

namespace Cue2.UI.Utilities;

/// <summary>
/// Shared OptionButton wiring for <see cref="FadeCurveType"/> menus.
/// </summary>
public static class FadeCurveUi
{
    /// <summary>
    /// Fills <paramref name="button"/> with Linear / S-Curve / Exponential / Logarithmic.
    /// Call again on locale change.
    /// </summary>
    /// <param name="button">Target OptionButton.</param>
    /// <param name="fadeOut">True for fade-out captions and tooltips.</param>
    public static void Populate(OptionButton button, bool fadeOut)
    {
        if (button == null || !GodotObject.IsInstanceValid(button))
            return;

        int keepId = button.ItemCount > 0 ? button.GetSelectedId() : (int)FadeCurveType.Linear;
        button.SetBlockSignals(true);
        try
        {
            button.Clear();
            foreach (var type in FadeCurve.All)
            {
                UiLocalizer.AddTranslatedItem(
                    button,
                    FadeCurve.DisplayName(type),
                    (int)type,
                    FadeCurve.Tooltip(type, fadeOut));
            }

            UiLocalizer.SetTooltip(button, fadeOut ? "Fade-out curve" : "Fade-in curve");
            if (keepId < 0)
            {
                button.Select(-1);
                return;
            }

            int idx = button.GetItemIndex(keepId);
            button.Selected = idx >= 0 ? idx : 0;
        }
        finally
        {
            button.SetBlockSignals(false);
        }
    }

    /// <summary>
    /// Shows or hides the menu and selects a uniform curve (blank when mixed).
    /// </summary>
    /// <param name="button">Target OptionButton.</param>
    /// <param name="visible">Whether the menu should show.</param>
    /// <param name="uniform">Curve when all targets match; null when mixed.</param>
    public static void Sync(OptionButton button, bool visible, FadeCurveType? uniform)
    {
        if (button == null || !GodotObject.IsInstanceValid(button))
            return;

        button.Visible = visible;
        if (!visible)
            return;

        button.SetBlockSignals(true);
        try
        {
            if (uniform is FadeCurveType curve)
            {
                int idx = button.GetItemIndex((int)curve);
                button.Selected = idx >= 0 ? idx : 0;
            }
            else
            {
                button.Select(-1);
            }
        }
        finally
        {
            button.SetBlockSignals(false);
        }
    }

    /// <summary>
    /// Reads the curve for a selected item index.
    /// </summary>
    /// <param name="button">Source OptionButton.</param>
    /// <param name="index">Selected item index.</param>
    /// <returns>A defined <see cref="FadeCurveType"/>.</returns>
    public static FadeCurveType CurveAt(OptionButton button, long index)
    {
        if (button == null || index < 0 || index >= button.ItemCount)
            return FadeCurveType.Linear;
        return FadeCurve.FromInt(button.GetItemId((int)index));
    }
}
