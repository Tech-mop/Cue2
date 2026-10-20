// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

namespace Cue2.Services;

/// <summary>
/// User-added Godot Theme resource remembered in Cue2 Preferences.
/// </summary>
public sealed class UserThemeEntry
{
    /// <summary>
    /// Stable id (<c>user:</c> prefix plus normalized path).
    /// </summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>
    /// OptionButton label (file name without extension).
    /// </summary>
    public string DisplayName { get; init; } = string.Empty;

    /// <summary>
    /// Absolute filesystem path to the <c>.tres</c> Theme file.
    /// </summary>
    public string Path { get; init; } = string.Empty;
}
