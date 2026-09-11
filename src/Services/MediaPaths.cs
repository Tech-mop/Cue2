// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System;
using System.IO;
using Godot;

namespace Cue2.Services;

/// <summary>
/// Helpers for storing media paths relative to the show session folder and resolving them for playback.
/// </summary>
public static class MediaPaths
{
    /// <summary>
    /// Resolves a stored media path (absolute or show-relative) to an absolute filesystem path.
    /// </summary>
    /// <param name="storedPath">Path as stored on a cue component (e.g. <c>Audio/song.wav</c> or an absolute path).</param>
    /// <param name="sessionDir">Absolute path to the show session directory, or null/empty if none.</param>
    /// <returns>Absolute path suitable for <see cref="File.Exists"/> and decoders, or the original string on failure.</returns>
    public static string Resolve(string storedPath, string sessionDir)
    {
        if (string.IsNullOrWhiteSpace(storedPath))
            return storedPath ?? string.Empty;

        try
        {
            string trimmed = storedPath.Trim();

            // Already absolute
            if (Path.IsPathRooted(trimmed))
            {
                try
                {
                    return Path.GetFullPath(trimmed);
                }
                catch
                {
                    return trimmed;
                }
            }

            // Show-relative (e.g. Audio/foo.wav)
            if (!string.IsNullOrEmpty(sessionDir))
            {
                string combined = Path.Combine(sessionDir, trimmed.Replace('/', Path.DirectorySeparatorChar));
                return Path.GetFullPath(combined);
            }

            return trimmed;
        }
        catch (Exception ex)
        {
            GD.PrintErr($"MediaPaths:Resolve - {ex.Message}");
            return storedPath;
        }
    }

    /// <summary>
    /// True when the resolved media file exists on disk.
    /// </summary>
    public static bool Exists(string storedPath, string sessionDir)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(storedPath))
                return false;
            string resolved = Resolve(storedPath, sessionDir);
            return !string.IsNullOrEmpty(resolved) && File.Exists(resolved);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Converts an absolute path under the session directory into a forward-slash relative path.
    /// </summary>
    /// <param name="absolutePath">Absolute media path.</param>
    /// <param name="sessionDir">Show session root directory.</param>
    /// <returns>Relative path (e.g. <c>Audio/song.wav</c>), or null if not under the session directory.</returns>
    public static string TryMakeRelative(string absolutePath, string sessionDir)
    {
        if (string.IsNullOrWhiteSpace(absolutePath) || string.IsNullOrWhiteSpace(sessionDir))
            return null;

        try
        {
            if (!IsUnderDirectory(absolutePath, sessionDir))
                return null;

            string session = Path.GetFullPath(sessionDir)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            string full = Path.GetFullPath(absolutePath);
            return full.Substring(session.Length).Replace('\\', '/');
        }
        catch (Exception ex)
        {
            GD.PrintErr($"MediaPaths:TryMakeRelative - {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// True when <paramref name="filePath"/> is inside <paramref name="directory"/> (or is that directory).
    /// Uses a trailing-separator prefix so <c>C:\Show</c> does not match <c>C:\ShowExtra</c>.
    /// </summary>
    public static bool IsUnderDirectory(string filePath, string directory)
    {
        try
        {
            if (string.IsNullOrEmpty(filePath) || string.IsNullOrEmpty(directory))
                return false;

            string dir = Path.GetFullPath(directory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            string full = Path.GetFullPath(filePath);
            return full.StartsWith(dir, StringComparison.OrdinalIgnoreCase)
                   || string.Equals(
                       full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                       dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                       StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Returns true when the path is not rooted (treated as show-relative).
    /// </summary>
    public static bool IsShowRelative(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;
        try
        {
            return !Path.IsPathRooted(path.Trim());
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Normalizes a path for case-insensitive identity comparison.
    /// </summary>
    public static string NormalizeKey(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return string.Empty;
        try
        {
            return Path.GetFullPath(path).Replace('\\', '/');
        }
        catch
        {
            return path.Replace('\\', '/').Trim();
        }
    }
}
