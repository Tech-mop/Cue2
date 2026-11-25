// SPDX-FileCopyrightText: 2026 Samuel Moxham
// SPDX-License-Identifier: MIT

namespace Cue2.Services;

/// <summary>
/// Which InputMap actions may fire while a LineEdit or TextEdit has focus.
/// </summary>
/// <remarks>
/// Plain keys (Go, Stop, arrows) stay off while typing. Cmd/Ctrl Save and similar
/// app shortcuts still run. Cue-edit chords (Copy, Paste, Cut, Select All, Duplicate)
/// stay off so they do not also run on the cuelist. Undo and Redo stay off so
/// Godot's LineEdit/TextEdit history handles Cmd/Ctrl+Z instead of the show.
/// </remarks>
public static class InputMapFocusGate
{
    /// <summary>
    /// True when <paramref name="actionName"/> may run while a text field is focused.
    /// </summary>
    /// <param name="actionName">Project InputMap action name (e.g. <c>SaveSession</c>).</param>
    /// <returns>True for file/window shortcuts that do not collide with text editing.</returns>
    public static bool AllowWhileTextFieldFocused(string actionName)
    {
        if (string.IsNullOrEmpty(actionName))
            return false;

        switch (actionName)
        {
            case "SaveSession":
            case "SaveAsSession":
            case "OpenSession":
            case "NewSession":
            case "ToggleSettings":
            case "ToggleLog":
                return true;
            default:
                return false;
        }
    }
}
