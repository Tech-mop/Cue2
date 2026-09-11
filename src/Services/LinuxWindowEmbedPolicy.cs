// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using Godot;

namespace Cue2.Services;

/// <summary>
/// Linux-only window policy: embed <see cref="Popup"/> / OptionButton dropdowns inside their
/// parent window, while keeping real app windows as native OS windows.
/// </summary>
/// <remarks>
/// Cue2 keeps <c>embed_subwindows=false</c> so video outputs and Settings/About/Log can be
/// separate desktop windows. On Linux (especially Wayland) native popup windows cannot be
/// placed under the widget that opened them. Enabling embed on the parent viewport and
/// <see cref="Window.ForceNative"/> on non-<see cref="Popup"/> child windows fixes OptionButton
/// lists without changing Windows/macOS or turning house screens into embedded views.
/// <para>
/// Never apply <see cref="Window.ForceNative"/> to <c>/root</c>. That property must be set
/// before a window is shown; flipping it on the already-visible main viewport can hide or
/// recreate the main window.
/// </para>
/// <para>
/// <see cref="ApplyToAppWindow"/> must run in each child <see cref="Window"/> constructor —
/// before the node enters the tree — because Godot decides native vs embedded during
/// enter-tree when the window is already visible.
/// </para>
/// </remarks>
public static class LinuxWindowEmbedPolicy
{
    /// <summary>True when the process is running on Linux.</summary>
    public static bool IsLinux => OS.GetName() == "Linux";

    /// <summary>True when Godot is using the Wayland display server.</summary>
    /// <remarks>
    /// Wayland does not allow apps to set window position or current screen. A new
    /// toplevel appears on the focused output (usually the operator UI). Sizing that
    /// window to a full monitor therefore covers or replaces the main Cue2 window.
    /// </remarks>
    public static bool IsWayland => DisplayServer.GetName() == "Wayland";

    /// <summary>
    /// True when the display server can move a native window onto a chosen monitor.
    /// </summary>
    /// <value>False on Wayland; true on X11, Windows, and macOS.</value>
    public static bool CanPlaceWindowsOnSpecificScreen => !IsWayland;

    /// <summary>
    /// DisplayServer id of the process main viewport, as <see cref="int"/>.
    /// </summary>
    /// <remarks>
    /// Godot binds <see cref="DisplayServer.MainWindowId"/> as <see cref="long"/> (value 0).
    /// 0 is a valid window id — never treat it as "missing".
    /// </remarks>
    public static int MainWindowId => (int)DisplayServer.MainWindowId;

    /// <summary>
    /// True when <paramref name="windowId"/> is the process main viewport
    /// (<see cref="MainWindowId"/>, which is 0 — a valid id, not "missing").
    /// </summary>
    /// <param name="windowId">DisplayServer window id from <see cref="Window.GetWindowId"/>.</param>
    /// <returns>True when the id is the operator UI window.</returns>
    public static bool IsMainWindowId(int windowId) =>
        windowId == MainWindowId;

    /// <summary>
    /// Embeds popups in <paramref name="root"/> on Linux. Does not change whether
    /// the main window itself is native.
    /// </summary>
    /// <param name="root">Typically <c>GetTree().Root</c>.</param>
    public static void EnablePopupEmbedding(Window root)
    {
        if (!IsLinux || root == null || !GodotObject.IsInstanceValid(root))
            return;

        root.GuiEmbedSubwindows = true;
        GD.Print("LinuxWindowEmbedPolicy:EnablePopupEmbedding - Embedding popups on Linux; main window stays native.");
    }

    /// <summary>
    /// Marks a non-popup child <see cref="Window"/> as a native OS window whose own popups embed.
    /// No-op for <see cref="Popup"/>, the main viewport, and non-Linux hosts.
    /// </summary>
    /// <param name="window">App window (Settings, video output, dialog, …).</param>
    public static void ApplyToAppWindow(Window window)
    {
        if (!IsLinux || window == null || !GodotObject.IsInstanceValid(window))
            return;
        if (window is Popup)
            return;

        // The main viewport is already a native window. ForceNative after it is visible
        // can hide or recreate it on Linux.
        SceneTree tree = window.GetTree();
        if (tree?.Root != null && window == tree.Root)
            return;

        window.ForceNative = true;
        window.GuiEmbedSubwindows = true;
    }
}
