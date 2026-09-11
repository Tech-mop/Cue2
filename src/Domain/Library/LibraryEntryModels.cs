// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using Godot;
using Godot.Collections;

namespace Cue2.Domain.Library;

/// <summary>
/// Format constants for on-disk cue library entries (<c>.c2cue</c>).
/// </summary>
public static class LibraryFormat
{
    /// <summary>Marker string written into each entry file.</summary>
    public const string FormatId = "cue2-library-entry";

    /// <summary>Current on-disk schema version.</summary>
    public const int Version = 1;

    /// <summary>File extension for library cue entries (including leading dot).</summary>
    public const string EntryExtension = ".c2cue";

    /// <summary>Suffix for the optional media sidecar folder (e.g. <c>MyCue.media</c>).</summary>
    public const string MediaFolderSuffix = ".media";

    /// <summary>Godot user-path relative library root.</summary>
    public const string UserLibraryRelative = "user://library";

    /// <summary>Marker file created in the library root.</summary>
    public const string MarkerFileName = ".cue2library";

    /// <summary>
    /// Default top-level folders created under the library root for organizing cues.
    /// Missing folders are recreated on init; existing user folders are left alone.
    /// </summary>
    public static readonly string[] DefaultFolders =
    {
        "Audio",
        "Video",
        "Control",
        "Connections",
        "Templates"
    };
}

/// <summary>
/// Options for saving a cue (and optionally its descendants) into the library.
/// </summary>
public sealed class LibrarySaveOptions
{
    /// <summary>Folder under the library root (forward-slash relative). Empty = root.</summary>
    public string RelativeFolder { get; set; } = string.Empty;

    /// <summary>Display / file base name for the entry.</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>When true, recursively include nested child cues.</summary>
    public bool IncludeChildren { get; set; } = true;

    /// <summary>When true, copy referenced media into a <c>.media</c> sidecar folder.</summary>
    public bool IncludeMedia { get; set; } = true;

    /// <summary>When true, replace an existing entry with the same name.</summary>
    public bool Overwrite { get; set; }
}

/// <summary>
/// Where to place a library entry when loading into the open cuelist.
/// </summary>
public enum LibraryInsertMode
{
    /// <summary>Insert as a sibling below the focused cue (or end of list if none).</summary>
    BelowSelection = 0,

    /// <summary>Append at the end of the top-level cuelist.</summary>
    End = 1,

    /// <summary>Insert as a child of the focused cue (or end of list if none).</summary>
    AsChild = 2
}

/// <summary>
/// Options for loading a library entry into the current show.
/// </summary>
public sealed class LibraryLoadOptions
{
    /// <summary>Insert placement relative to the current selection.</summary>
    public LibraryInsertMode InsertMode { get; set; } = LibraryInsertMode.BelowSelection;

    /// <summary>
    /// When true and a session is open, copy library (or original) media into the show folder
    /// via media backup when enabled.
    /// </summary>
    public bool CopyMediaIntoShow { get; set; } = true;
}

/// <summary>
/// Lightweight result wrapper for library file operations.
/// </summary>
public sealed class LibraryResult
{
    /// <summary>True when the operation completed successfully.</summary>
    public bool Success { get; init; }

    /// <summary>Readable error or info message.</summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>Relative path of the written/affected entry (when applicable).</summary>
    public string RelativePath { get; init; } = string.Empty;

    /// <summary>New root cue id after a successful load (-1 if not a load).</summary>
    public int LoadedRootCueId { get; init; } = -1;

    /// <summary>Creates a successful result.</summary>
    public static LibraryResult Ok(string message = "", string relativePath = "", int loadedRootCueId = -1) =>
        new LibraryResult
        {
            Success = true,
            Message = message ?? string.Empty,
            RelativePath = relativePath ?? string.Empty,
            LoadedRootCueId = loadedRootCueId
        };

    /// <summary>Creates a failed result.</summary>
    public static LibraryResult Fail(string message) =>
        new LibraryResult
        {
            Success = false,
            Message = message ?? "Unknown error"
        };
}

/// <summary>
/// Folder node metadata for the library browser tree.
/// </summary>
public sealed class LibraryFolderInfo
{
    /// <summary>Folder name only (not full path).</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Forward-slash path relative to the library root.</summary>
    public string RelativePath { get; init; } = string.Empty;
}

/// <summary>
/// Summary of a single library entry file for list UI.
/// </summary>
public sealed class LibraryEntryInfo
{
    /// <summary>Display name stored in the entry (or file base name).</summary>
    public string DisplayName { get; init; } = string.Empty;

    /// <summary>Forward-slash path to the <c>.c2cue</c> relative to the library root.</summary>
    public string RelativePath { get; init; } = string.Empty;

    /// <summary>Absolute path to the entry file.</summary>
    public string AbsolutePath { get; init; } = string.Empty;

    /// <summary>UTC save timestamp when present in the file.</summary>
    public string SavedAt { get; init; } = string.Empty;

    /// <summary>Number of cues packaged in the entry (root + children).</summary>
    public int CueCount { get; init; }

    /// <summary>True when the entry was saved with nested children.</summary>
    public bool IncludeChildren { get; init; }

    /// <summary>True when a <c>.media</c> sidecar folder exists or the entry claims media.</summary>
    public bool HasMedia { get; init; }

    /// <summary>True when the entry claims library-relative media packaging.</summary>
    public bool LibraryRelativeMedia { get; init; }
}

/// <summary>
/// Fully parsed library entry ready for import.
/// </summary>
public sealed class LibraryEntryDocument
{
    /// <summary>Schema version from the file.</summary>
    public int Version { get; set; } = LibraryFormat.Version;

    /// <summary>Display name.</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>ISO-8601 save time when present.</summary>
    public string SavedAt { get; set; } = string.Empty;

    /// <summary>Whether nested children were included when saved.</summary>
    public bool IncludeChildren { get; set; }

    /// <summary>Whether media was packaged when saved.</summary>
    public bool IncludeMedia { get; set; }

    /// <summary>Temp id of the root cue inside <see cref="Cues"/>.</summary>
    public int RootTempId { get; set; } = 1;

    /// <summary>Cue dictionaries keyed by temp id string.</summary>
    public Dictionary Cues { get; set; } = new Dictionary();

    /// <summary>True when component media paths are relative to the entry <c>.media</c> folder.</summary>
    public bool LibraryRelativeMedia { get; set; }

    /// <summary>Optional media file manifest from the entry.</summary>
    public Godot.Collections.Array MediaFiles { get; set; } = new Godot.Collections.Array();

    /// <summary>Soft dependency metadata (patch/layer/OSC names).</summary>
    public Dictionary Dependencies { get; set; } = new Dictionary();

    /// <summary>Absolute path of the source <c>.c2cue</c> file.</summary>
    public string AbsoluteEntryPath { get; set; } = string.Empty;

    /// <summary>Absolute path of the sibling <c>.media</c> folder (may not exist).</summary>
    public string AbsoluteMediaDir { get; set; } = string.Empty;
}
