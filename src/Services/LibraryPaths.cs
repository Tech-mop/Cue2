// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System;
using System.IO;
using Cue2.Domain.Library;
using Cue2.UI.Utilities;
using Godot;

namespace Cue2.Services;

/// <summary>
/// Path helpers for the user-scoped cue library (folders, entries, media sidecars).
/// </summary>
public static class LibraryPaths
{
    /// <summary>
    /// Absolute filesystem path of the library root under the Godot user data directory.
    /// </summary>
    public static string GetLibraryRootAbsolute()
    {
        return ProjectSettings.GlobalizePath(LibraryFormat.UserLibraryRelative);
    }

    /// <summary>
    /// Joins a library-relative path (forward slashes) to the absolute library root.
    /// </summary>
    /// <param name="relativePath">Path relative to the library root; empty means root.</param>
    /// <returns>Absolute path.</returns>
    public static string ToAbsolute(string relativePath)
    {
        string root = GetLibraryRootAbsolute();
        if (string.IsNullOrWhiteSpace(relativePath))
            return root;

        string normalized = NormalizeRelative(relativePath);
        if (string.IsNullOrEmpty(normalized))
            return root;

        return Path.GetFullPath(Path.Combine(root, normalized.Replace('/', Path.DirectorySeparatorChar)));
    }

    /// <summary>
    /// Converts an absolute path under the library root into a forward-slash relative path.
    /// </summary>
    /// <param name="absolutePath">Absolute path inside the library.</param>
    /// <returns>Relative path, or null if outside the library root.</returns>
    public static string TryMakeRelative(string absolutePath)
    {
        return MediaPaths.TryMakeRelative(absolutePath, GetLibraryRootAbsolute());
    }

    /// <summary>
    /// Normalizes a relative path: trim, strip leading/trailing separators, use forward slashes,
    /// reject <c>..</c> segments.
    /// </summary>
    public static string NormalizeRelative(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
            return string.Empty;

        string s = relativePath.Trim().Replace('\\', '/').Trim('/');
        if (string.IsNullOrEmpty(s))
            return string.Empty;

        var parts = s.Split('/', StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in parts)
        {
            if (part == "." || part == "..")
                return string.Empty;
        }

        return string.Join("/", parts);
    }

    /// <summary>
    /// Sanitizes a display name into a safe single path segment (file or folder name).
    /// </summary>
    /// <param name="name">User-supplied name.</param>
    /// <param name="fallback">Used when the result would be empty.</param>
    /// <returns>Safe name without directory separators.</returns>
    public static string SanitizeName(string name, string fallback = "Cue")
    {
        if (string.IsNullOrWhiteSpace(name))
            name = fallback;

        char[] invalid = Path.GetInvalidFileNameChars();
        var chars = name.Trim().ToCharArray();
        for (int i = 0; i < chars.Length; i++)
        {
            char c = chars[i];
            if (c == '/' || c == '\\' || Array.IndexOf(invalid, c) >= 0)
                chars[i] = '_';
        }

        string cleaned = new string(chars).Trim().TrimEnd('.');
        if (string.IsNullOrWhiteSpace(cleaned))
            cleaned = fallback;

        // Avoid reserved Windows device names
        string upper = cleaned.ToUpperInvariant();
        if (upper is "CON" or "PRN" or "AUX" or "NUL"
            or "COM1" or "COM2" or "COM3" or "COM4" or "COM5" or "COM6" or "COM7" or "COM8" or "COM9"
            or "LPT1" or "LPT2" or "LPT3" or "LPT4" or "LPT5" or "LPT6" or "LPT7" or "LPT8" or "LPT9")
        {
            cleaned = "_" + cleaned;
        }

        if (cleaned.Length > 120)
            cleaned = cleaned.Substring(0, 120).TrimEnd();

        return cleaned;
    }

    /// <summary>
    /// Builds the absolute path for an entry file from folder + display name.
    /// </summary>
    public static string GetEntryAbsolutePath(string relativeFolder, string displayName)
    {
        string safe = SanitizeName(displayName);
        string folder = NormalizeRelative(relativeFolder);
        string relative = string.IsNullOrEmpty(folder)
            ? safe + LibraryFormat.EntryExtension
            : folder + "/" + safe + LibraryFormat.EntryExtension;
        return ToAbsolute(relative);
    }

    /// <summary>
    /// Relative path for an entry (folder + name).
    /// </summary>
    public static string GetEntryRelativePath(string relativeFolder, string displayName)
    {
        string safe = SanitizeName(displayName);
        string folder = NormalizeRelative(relativeFolder);
        return string.IsNullOrEmpty(folder)
            ? safe + LibraryFormat.EntryExtension
            : folder + "/" + safe + LibraryFormat.EntryExtension;
    }

    /// <summary>
    /// Absolute media sidecar directory for an entry absolute path.
    /// </summary>
    /// <param name="entryAbsolutePath">Path to the <c>.c2cue</c> file.</param>
    public static string GetMediaDirForEntry(string entryAbsolutePath)
    {
        if (string.IsNullOrWhiteSpace(entryAbsolutePath))
            return string.Empty;

        string dir = Path.GetDirectoryName(entryAbsolutePath) ?? string.Empty;
        string baseName = Path.GetFileNameWithoutExtension(entryAbsolutePath);
        return Path.Combine(dir, baseName + LibraryFormat.MediaFolderSuffix);
    }

    /// <summary>
    /// Resolves a library-relative media path against an entry's <c>.media</c> folder.
    /// Absolute stored paths are returned as full paths.
    /// </summary>
    public static string ResolveMedia(string storedPath, string mediaDirAbsolute)
    {
        return MediaPaths.Resolve(storedPath, mediaDirAbsolute);
    }

    /// <summary>
    /// Maps a media kind to the standard subfolder name under a media package.
    /// </summary>
    public static string KindFolderName(MediaBackupKind kind)
    {
        return kind switch
        {
            MediaBackupKind.Audio => DirectoryUtils.AudioFolderName,
            MediaBackupKind.Video => DirectoryUtils.VideoFolderName,
            MediaBackupKind.Image => DirectoryUtils.ImagesFolderName,
            _ => DirectoryUtils.AudioFolderName
        };
    }

    /// <summary>
    /// Combines a parent relative folder with a child name.
    /// </summary>
    public static string CombineRelative(string parentRelative, string childName)
    {
        string parent = NormalizeRelative(parentRelative);
        string child = SanitizeName(childName);
        if (string.IsNullOrEmpty(parent))
            return child;
        if (string.IsNullOrEmpty(child))
            return parent;
        return parent + "/" + child;
    }
}
