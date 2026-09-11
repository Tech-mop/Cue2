// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Cue2.Domain.Cuelist;
using Cue2.Domain.Playback;
using Cue2.Domain.Devices;
using Cue2.Domain.ShowSettings;
using Cue2.Domain.Metadata;
using Cue2.Domain.Cues;
using Cue2.Domain.Connections;
using Cue2.Domain.Library;
using Cue2.Domain.Commands;
using Cue2.UI.Utilities;
using Godot;
using Godot.Collections;
using Array = Godot.Collections.Array;

namespace Cue2.Services;

/// <summary>
/// Manages the user-scoped cue library under <c>user://library/</c>:
/// folder organization, save/load of cue entries, and optional media packaging.
/// </summary>
/// <remarks>
/// Library content is machine/user data (not part of the show <c>.c2</c>).
/// Child of <see cref="GlobalData"/>.
/// </remarks>
public partial class CueLibraryManager : Node
{
    private GlobalData _globalData;
    private GlobalSignals _globalSignals;
    private bool _initialized;

    /// <summary>Absolute path to the library root directory.</summary>
    public string LibraryRootPath => LibraryPaths.GetLibraryRootAbsolute();

    /// <inheritdoc />
    public override void _Ready()
    {
        _globalData = GetParent() as GlobalData ?? GetNodeOrNull<GlobalData>("/root/GlobalData");
        _globalSignals = GetNodeOrNull<GlobalSignals>("/root/GlobalSignals");
        EnsureLibraryInitialized();
    }

    /// <summary>
    /// Creates the library root, marker file, and default folder tree if missing.
    /// </summary>
    /// <remarks>
    /// Default folders (<see cref="LibraryFormat.DefaultFolders"/>) are ensured on every call
    /// so existing libraries pick them up without removing user-created folders.
    /// </remarks>
    public void EnsureLibraryInitialized()
    {
        try
        {
            string root = LibraryRootPath;
            if (!Directory.Exists(root))
                Directory.CreateDirectory(root);

            string marker = Path.Combine(root, LibraryFormat.MarkerFileName);
            if (!File.Exists(marker))
            {
                var meta = new Dictionary
                {
                    { "Format", "cue2-library" },
                    { "Version", LibraryFormat.Version },
                    { "CreatedAt", DateTime.UtcNow.ToString("o") }
                };
                WriteJsonFile(marker, meta);
            }

            EnsureDefaultFolders(root);

            if (!_initialized)
            {
                _initialized = true;
                GD.Print($"CueLibraryManager:EnsureLibraryInitialized - Root: {root}");
            }
        }
        catch (Exception ex)
        {
            GD.PrintErr($"CueLibraryManager:EnsureLibraryInitialized - {ex.Message}");
            Log($"Library: failed to initialize — {ex.Message}", LogType.Error);
        }
    }

    /// <summary>
    /// Ensures each default top-level folder exists under the library root.
    /// </summary>
    /// <param name="libraryRootAbsolute">Absolute path to the library root directory.</param>
    private static void EnsureDefaultFolders(string libraryRootAbsolute)
    {
        if (string.IsNullOrEmpty(libraryRootAbsolute))
            return;

        foreach (string name in LibraryFormat.DefaultFolders)
        {
            if (string.IsNullOrWhiteSpace(name))
                continue;

            try
            {
                string path = Path.Combine(libraryRootAbsolute, name);
                if (!Directory.Exists(path))
                    Directory.CreateDirectory(path);
            }
            catch (Exception ex)
            {
                GD.PrintErr($"CueLibraryManager:EnsureDefaultFolders - Failed to create '{name}': {ex.Message}");
            }
        }
    }

    // ── Listing ────────────────────────────────────────────────────────────

    /// <summary>
    /// Lists immediate subfolders under a library-relative folder.
    /// </summary>
    public IReadOnlyList<LibraryFolderInfo> ListFolders(string relativeFolder = "")
    {
        EnsureLibraryInitialized();
        var result = new List<LibraryFolderInfo>();
        try
        {
            string abs = LibraryPaths.ToAbsolute(relativeFolder);
            if (!Directory.Exists(abs))
                return result;

            foreach (var dir in Directory.GetDirectories(abs).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
            {
                string name = Path.GetFileName(dir);
                // Skip media sidecar folders
                if (name.EndsWith(LibraryFormat.MediaFolderSuffix, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (name.StartsWith('.'))
                    continue;

                string rel = LibraryPaths.CombineRelative(relativeFolder, name);
                result.Add(new LibraryFolderInfo { Name = name, RelativePath = rel });
            }
        }
        catch (Exception ex)
        {
            GD.PrintErr($"CueLibraryManager:ListFolders - {ex.Message}");
        }

        return result;
    }

    /// <summary>
    /// Lists all folders recursively under the library root (for tree UI).
    /// </summary>
    public IReadOnlyList<LibraryFolderInfo> ListAllFolders()
    {
        EnsureLibraryInitialized();
        var result = new List<LibraryFolderInfo>();
        try
        {
            WalkFolders(string.Empty, result);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"CueLibraryManager:ListAllFolders - {ex.Message}");
        }

        return result;
    }

    private void WalkFolders(string relative, List<LibraryFolderInfo> acc)
    {
        foreach (var folder in ListFolders(relative))
        {
            acc.Add(folder);
            WalkFolders(folder.RelativePath, acc);
        }
    }

    /// <summary>
    /// Lists <c>.c2cue</c> entries in a library-relative folder (non-recursive).
    /// </summary>
    public IReadOnlyList<LibraryEntryInfo> ListEntries(string relativeFolder = "")
    {
        EnsureLibraryInitialized();
        var result = new List<LibraryEntryInfo>();
        try
        {
            string abs = LibraryPaths.ToAbsolute(relativeFolder);
            if (!Directory.Exists(abs))
                return result;

            foreach (var file in Directory.GetFiles(abs, "*" + LibraryFormat.EntryExtension)
                         .OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                var info = ReadEntryInfo(file);
                if (info != null)
                    result.Add(info);
            }
        }
        catch (Exception ex)
        {
            GD.PrintErr($"CueLibraryManager:ListEntries - {ex.Message}");
        }

        return result;
    }

    /// <summary>
    /// Reads summary metadata for an entry file without fully parsing cue bodies.
    /// </summary>
    public LibraryEntryInfo ReadEntryInfo(string absoluteEntryPath)
    {
        try
        {
            if (string.IsNullOrEmpty(absoluteEntryPath) || !File.Exists(absoluteEntryPath))
                return null;

            var doc = ReadJsonFile(absoluteEntryPath);
            string baseName = Path.GetFileNameWithoutExtension(absoluteEntryPath);
            string rel = LibraryPaths.TryMakeRelative(absoluteEntryPath) ?? baseName + LibraryFormat.EntryExtension;

            string displayName = baseName;
            string savedAt = string.Empty;
            int cueCount = 1;
            bool includeChildren = false;
            bool includeMedia = false;
            bool libraryRelative = false;

            if (doc != null)
            {
                if (doc.TryGetValue("DisplayName", out var dn))
                    displayName = dn.AsString();
                if (doc.TryGetValue("SavedAt", out var sa))
                    savedAt = sa.AsString();
                if (doc.TryGetValue("IncludeChildren", out var ic))
                    includeChildren = ic.AsBool();
                if (doc.TryGetValue("IncludeMedia", out var im))
                    includeMedia = im.AsBool();
                if (doc.TryGetValue("Cues", out var cuesVar) && cuesVar.VariantType == Variant.Type.Dictionary)
                    cueCount = cuesVar.AsGodotDictionary().Count;
                if (doc.TryGetValue("Media", out var mediaVar) && mediaVar.VariantType == Variant.Type.Dictionary)
                {
                    var media = mediaVar.AsGodotDictionary();
                    if (media.TryGetValue("LibraryRelative", out var lr))
                        libraryRelative = lr.AsBool();
                }
            }

            string mediaDir = LibraryPaths.GetMediaDirForEntry(absoluteEntryPath);
            bool hasMedia = includeMedia || Directory.Exists(mediaDir);

            return new LibraryEntryInfo
            {
                DisplayName = string.IsNullOrWhiteSpace(displayName) ? baseName : displayName,
                RelativePath = rel.Replace('\\', '/'),
                AbsolutePath = absoluteEntryPath,
                SavedAt = savedAt,
                CueCount = Math.Max(1, cueCount),
                IncludeChildren = includeChildren,
                HasMedia = hasMedia,
                LibraryRelativeMedia = libraryRelative
            };
        }
        catch (Exception ex)
        {
            GD.PrintErr($"CueLibraryManager:ReadEntryInfo - {ex.Message}");
            return null;
        }
    }

    // ── Folder CRUD ────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a new folder under the given parent relative path.
    /// </summary>
    public LibraryResult CreateFolder(string parentRelative, string name)
    {
        EnsureLibraryInitialized();
        try
        {
            string safe = LibraryPaths.SanitizeName(name, "New Folder");
            string rel = LibraryPaths.CombineRelative(parentRelative, safe);
            string abs = LibraryPaths.ToAbsolute(rel);
            if (Directory.Exists(abs))
                return LibraryResult.Fail($"Folder already exists: {safe}");

            Directory.CreateDirectory(abs);
            Log($"Library: created folder '{rel}'", LogType.Info);
            return LibraryResult.Ok($"Created folder '{safe}'", rel);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"CueLibraryManager:CreateFolder - {ex.Message}");
            return LibraryResult.Fail(ex.Message);
        }
    }

    /// <summary>
    /// Renames a folder (same parent).
    /// </summary>
    public LibraryResult RenameFolder(string relativeFolder, string newName)
    {
        EnsureLibraryInitialized();
        try
        {
            string rel = LibraryPaths.NormalizeRelative(relativeFolder);
            if (string.IsNullOrEmpty(rel))
                return LibraryResult.Fail("Cannot rename the library root.");

            string abs = LibraryPaths.ToAbsolute(rel);
            if (!Directory.Exists(abs))
                return LibraryResult.Fail("Folder not found.");

            string safe = LibraryPaths.SanitizeName(newName);
            string parent = Path.GetDirectoryName(abs) ?? LibraryRootPath;
            string dest = Path.Combine(parent, safe);
            if (Directory.Exists(dest))
                return LibraryResult.Fail($"A folder named '{safe}' already exists.");

            Directory.Move(abs, dest);
            string newRel = LibraryPaths.TryMakeRelative(dest) ?? safe;
            Log($"Library: renamed folder '{rel}' → '{newRel}'", LogType.Info);
            return LibraryResult.Ok($"Renamed to '{safe}'", newRel);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"CueLibraryManager:RenameFolder - {ex.Message}");
            return LibraryResult.Fail(ex.Message);
        }
    }

    /// <summary>
    /// Deletes a folder and all of its contents.
    /// </summary>
    public LibraryResult DeleteFolder(string relativeFolder)
    {
        EnsureLibraryInitialized();
        try
        {
            string rel = LibraryPaths.NormalizeRelative(relativeFolder);
            if (string.IsNullOrEmpty(rel))
                return LibraryResult.Fail("Cannot delete the library root.");

            string abs = LibraryPaths.ToAbsolute(rel);
            if (!Directory.Exists(abs))
                return LibraryResult.Fail("Folder not found.");

            Directory.Delete(abs, recursive: true);
            Log($"Library: deleted folder '{rel}'", LogType.Info);
            return LibraryResult.Ok($"Deleted folder '{rel}'", rel);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"CueLibraryManager:DeleteFolder - {ex.Message}");
            return LibraryResult.Fail(ex.Message);
        }
    }

    // ── Entry CRUD ─────────────────────────────────────────────────────────

    /// <summary>
    /// Renames a library entry (and its media sidecar when present).
    /// </summary>
    public LibraryResult RenameEntry(string entryRelativePath, string newDisplayName)
    {
        EnsureLibraryInitialized();
        try
        {
            string abs = LibraryPaths.ToAbsolute(entryRelativePath);
            if (!File.Exists(abs))
                return LibraryResult.Fail("Entry not found.");

            string safe = LibraryPaths.SanitizeName(newDisplayName);
            string dir = Path.GetDirectoryName(abs) ?? LibraryRootPath;
            string dest = Path.Combine(dir, safe + LibraryFormat.EntryExtension);
            if (File.Exists(dest) && !string.Equals(dest, abs, StringComparison.OrdinalIgnoreCase))
                return LibraryResult.Fail($"An entry named '{safe}' already exists.");

            string oldMedia = LibraryPaths.GetMediaDirForEntry(abs);
            string newMedia = LibraryPaths.GetMediaDirForEntry(dest);

            File.Move(abs, dest);

            // Update DisplayName inside the file
            var doc = ReadJsonFile(dest);
            if (doc != null)
            {
                doc["DisplayName"] = safe;
                WriteJsonFile(dest, doc);
            }

            if (Directory.Exists(oldMedia))
            {
                if (Directory.Exists(newMedia))
                    Directory.Delete(newMedia, true);
                Directory.Move(oldMedia, newMedia);
            }

            string newRel = LibraryPaths.TryMakeRelative(dest) ?? safe + LibraryFormat.EntryExtension;
            Log($"Library: renamed entry → '{newRel}'", LogType.Info);
            return LibraryResult.Ok($"Renamed to '{safe}'", newRel.Replace('\\', '/'));
        }
        catch (Exception ex)
        {
            GD.PrintErr($"CueLibraryManager:RenameEntry - {ex.Message}");
            return LibraryResult.Fail(ex.Message);
        }
    }

    /// <summary>
    /// Deletes a library entry and its media sidecar.
    /// </summary>
    public LibraryResult DeleteEntry(string entryRelativePath)
    {
        EnsureLibraryInitialized();
        try
        {
            string abs = LibraryPaths.ToAbsolute(entryRelativePath);
            if (!File.Exists(abs))
                return LibraryResult.Fail("Entry not found.");

            string media = LibraryPaths.GetMediaDirForEntry(abs);
            File.Delete(abs);
            if (Directory.Exists(media))
                Directory.Delete(media, true);

            Log($"Library: deleted entry '{entryRelativePath}'", LogType.Info);
            return LibraryResult.Ok("Deleted entry", entryRelativePath);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"CueLibraryManager:DeleteEntry - {ex.Message}");
            return LibraryResult.Fail(ex.Message);
        }
    }

    /// <summary>
    /// Moves an entry into another library folder (same name).
    /// </summary>
    public LibraryResult MoveEntry(string entryRelativePath, string destFolderRelative)
    {
        EnsureLibraryInitialized();
        try
        {
            string abs = LibraryPaths.ToAbsolute(entryRelativePath);
            if (!File.Exists(abs))
                return LibraryResult.Fail("Entry not found.");

            string destFolder = LibraryPaths.ToAbsolute(destFolderRelative);
            if (!Directory.Exists(destFolder))
                Directory.CreateDirectory(destFolder);

            string fileName = Path.GetFileName(abs);
            string dest = Path.Combine(destFolder, fileName);
            if (File.Exists(dest))
                return LibraryResult.Fail($"Destination already has '{fileName}'.");

            string oldMedia = LibraryPaths.GetMediaDirForEntry(abs);
            string newMedia = LibraryPaths.GetMediaDirForEntry(dest);

            File.Move(abs, dest);
            if (Directory.Exists(oldMedia))
            {
                if (Directory.Exists(newMedia))
                    Directory.Delete(newMedia, true);
                Directory.Move(oldMedia, newMedia);
            }

            string newRel = LibraryPaths.TryMakeRelative(dest) ?? fileName;
            Log($"Library: moved entry → '{newRel}'", LogType.Info);
            return LibraryResult.Ok("Moved entry", newRel.Replace('\\', '/'));
        }
        catch (Exception ex)
        {
            GD.PrintErr($"CueLibraryManager:MoveEntry - {ex.Message}");
            return LibraryResult.Fail(ex.Message);
        }
    }

    /// <summary>
    /// True when an entry file already exists for the given folder + name.
    /// </summary>
    public bool EntryExists(string relativeFolder, string displayName)
    {
        string abs = LibraryPaths.GetEntryAbsolutePath(relativeFolder, displayName);
        return File.Exists(abs);
    }

    // ── Save ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Saves a cue (and optionally nested children / media) into the library.
    /// </summary>
    /// <param name="root">Root cue to export.</param>
    /// <param name="options">Save options.</param>
    public LibraryResult SaveCue(Cue root, LibrarySaveOptions options)
    {
        EnsureLibraryInitialized();
        if (root == null)
            return LibraryResult.Fail("No cue to save.");
        if (options == null)
            options = new LibrarySaveOptions();

        try
        {
            string displayName = string.IsNullOrWhiteSpace(options.DisplayName)
                ? root.Name
                : options.DisplayName;
            displayName = LibraryPaths.SanitizeName(displayName, "Cue");

            string folder = LibraryPaths.NormalizeRelative(options.RelativeFolder);
            string relativePath = LibraryPaths.GetEntryRelativePath(folder, displayName);
            string absPath = LibraryPaths.ToAbsolute(relativePath);

            if (File.Exists(absPath) && !options.Overwrite)
                return LibraryResult.Fail($"Entry already exists: {displayName}");

            // Ensure destination folder
            string absFolder = Path.GetDirectoryName(absPath);
            if (!string.IsNullOrEmpty(absFolder) && !Directory.Exists(absFolder))
                Directory.CreateDirectory(absFolder);

            // Build cue forest with temp ids
            var cuesDict = new Dictionary();
            var sessionToTemp = new System.Collections.Generic.Dictionary<int, int>();
            int nextTemp = 1;

            void Walk(Cue cue, int parentTempId)
            {
                if (cue == null) return;
                int tempId = nextTemp++;
                sessionToTemp[cue.Id] = tempId;

                var data = DeepClone(cue.GetData());
                StripWaveformPayloads(data);
                data["Id"] = tempId.ToString();
                data["ParentId"] = parentTempId.ToString();
                // ChildCues rewritten after full walk of this branch
                data["ChildCues"] = new Array();
                cuesDict[tempId.ToString()] = data;

                if (options.IncludeChildren && cue.ChildCues != null && cue.ChildCues.Count > 0)
                {
                    var childTemps = new Array();
                    foreach (int childId in cue.ChildCues)
                    {
                        var child = CueList.FetchCueFromId(childId);
                        if (child == null) continue;
                        Walk(child, tempId);
                        if (sessionToTemp.TryGetValue(childId, out int childTemp))
                            childTemps.Add(childTemp);
                    }

                    data["ChildCues"] = childTemps;
                }
                else
                {
                    data["ChildCues"] = new Array();
                }
            }

            Walk(root, -1);

            // Remap Control component targets that fall inside the exported set; clear outsiders
            RemapControlTargetsInCues(cuesDict, sessionToTemp);

            // Dependencies snapshot (names for soft-relink)
            var dependencies = CaptureDependencies(cuesDict);

            // Media packaging
            string mediaDir = LibraryPaths.GetMediaDirForEntry(absPath);
            var mediaManifest = new Array();
            bool libraryRelative = false;

            if (options.IncludeMedia)
            {
                // Clean previous media package on overwrite
                if (Directory.Exists(mediaDir))
                {
                    try { Directory.Delete(mediaDir, true); }
                    catch { /* best effort */ }
                }

                libraryRelative = PackageMedia(cuesDict, mediaDir, mediaManifest);
                if (!libraryRelative && mediaManifest.Count == 0)
                {
                    // No files copied — remove empty media dir if created
                    if (Directory.Exists(mediaDir) && !Directory.EnumerateFileSystemEntries(mediaDir).Any())
                    {
                        try { Directory.Delete(mediaDir); } catch { /* ignore */ }
                    }
                }
            }
            else
            {
                // Leave existing media alone only if not overwriting name; if overwrite, drop old media
                if (options.Overwrite && Directory.Exists(mediaDir))
                {
                    try { Directory.Delete(mediaDir, true); } catch { /* ignore */ }
                }
            }

            int rootTempId = sessionToTemp.TryGetValue(root.Id, out int rt) ? rt : 1;

            var entry = new Dictionary
            {
                { "Format", LibraryFormat.FormatId },
                { "Version", LibraryFormat.Version },
                { "SavedAt", DateTime.UtcNow.ToString("o") },
                { "DisplayName", displayName },
                { "IncludeChildren", options.IncludeChildren },
                { "IncludeMedia", options.IncludeMedia && libraryRelative },
                { "RootTempId", rootTempId },
                { "Cues", cuesDict },
                {
                    "Media", new Dictionary
                    {
                        { "LibraryRelative", libraryRelative },
                        { "Files", mediaManifest }
                    }
                },
                { "Dependencies", dependencies }
            };

            WriteJsonFile(absPath, entry);
            Log($"Library: saved '{displayName}' → {relativePath} (cues={cuesDict.Count}, media={libraryRelative})", LogType.Info);
            return LibraryResult.Ok($"Saved '{displayName}'", relativePath);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"CueLibraryManager:SaveCue - {ex.Message}\n{ex.StackTrace}");
            return LibraryResult.Fail(ex.Message);
        }
    }

    // ── Load ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Loads a library entry into the current cuelist.
    /// </summary>
    /// <param name="entryRelativePath">Relative path to the <c>.c2cue</c>.</param>
    /// <param name="options">Load options.</param>
    public LibraryResult LoadEntry(string entryRelativePath, LibraryLoadOptions options = null)
    {
        if (_globalData?.Settings?.IsCueEditingLocked == true)
            return LibraryResult.Fail("Show Mode is active — turn it off to load library cues into the cuelist.");

        EnsureLibraryInitialized();
        options ??= new LibraryLoadOptions();

        try
        {
            string abs = LibraryPaths.ToAbsolute(entryRelativePath);
            if (!File.Exists(abs))
                return LibraryResult.Fail("Entry not found.");

            var document = ParseEntryDocument(abs);
            if (document == null || document.Cues == null || document.Cues.Count == 0)
                return LibraryResult.Fail("Invalid or empty library entry.");

            var cuelist = _globalData?.Cuelist;
            if (cuelist == null)
                return LibraryResult.Fail("Cuelist is not available.");

            // Rewrite media paths to absolute (library package) or leave as-is
            PrepareMediaPathsForImport(document, options);

            // Soft-relink dependency ids by name before import
            ApplyDependencyNameRelink(document);

            string description = string.IsNullOrWhiteSpace(document.DisplayName)
                ? "Load library cue"
                : $"Load library cue '{document.DisplayName}'";

            _globalData.HistoryManager?.RecordCuelistChange(description);

            int newRootId = cuelist.ImportCueTreeFromLibrary(
                document.Cues,
                document.RootTempId,
                options.InsertMode);

            if (newRootId < 0)
                return LibraryResult.Fail("Failed to import cue into the cuelist.");

            GetNodeOrNull<MediaHealthService>("/root/MediaHealthService")?.RecheckAllQuiet();
            _globalSignals?.EmitSignal(nameof(GlobalSignals.SyncShellInspector));

            Log($"Library: loaded '{document.DisplayName}' as cue id {newRootId}", LogType.Info);
            return LibraryResult.Ok($"Loaded '{document.DisplayName}'", entryRelativePath, newRootId);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"CueLibraryManager:LoadEntry - {ex.Message}\n{ex.StackTrace}");
            return LibraryResult.Fail(ex.Message);
        }
    }

    /// <summary>
    /// Fully parses a library entry document from disk.
    /// </summary>
    public LibraryEntryDocument ParseEntryDocument(string absoluteEntryPath)
    {
        var raw = ReadJsonFile(absoluteEntryPath);
        if (raw == null)
            return null;

        if (raw.TryGetValue("Format", out var fmt) &&
            fmt.AsString() != LibraryFormat.FormatId)
        {
            GD.PrintErr($"CueLibraryManager:ParseEntryDocument - Unexpected format '{fmt.AsString()}'");
        }

        var doc = new LibraryEntryDocument
        {
            AbsoluteEntryPath = absoluteEntryPath,
            AbsoluteMediaDir = LibraryPaths.GetMediaDirForEntry(absoluteEntryPath)
        };

        if (raw.TryGetValue("Version", out var ver))
            doc.Version = ver.AsInt32();
        if (raw.TryGetValue("DisplayName", out var dn))
            doc.DisplayName = dn.AsString();
        if (raw.TryGetValue("SavedAt", out var sa))
            doc.SavedAt = sa.AsString();
        if (raw.TryGetValue("IncludeChildren", out var ic))
            doc.IncludeChildren = ic.AsBool();
        if (raw.TryGetValue("IncludeMedia", out var im))
            doc.IncludeMedia = im.AsBool();
        if (raw.TryGetValue("RootTempId", out var rt))
            doc.RootTempId = rt.AsInt32();
        if (raw.TryGetValue("Cues", out var cuesVar) && cuesVar.VariantType == Variant.Type.Dictionary)
            doc.Cues = cuesVar.AsGodotDictionary();
        if (raw.TryGetValue("Dependencies", out var depVar) && depVar.VariantType == Variant.Type.Dictionary)
            doc.Dependencies = depVar.AsGodotDictionary();
        if (raw.TryGetValue("Media", out var mediaVar) && mediaVar.VariantType == Variant.Type.Dictionary)
        {
            var media = mediaVar.AsGodotDictionary();
            if (media.TryGetValue("LibraryRelative", out var lr))
                doc.LibraryRelativeMedia = lr.AsBool();
            if (media.TryGetValue("Files", out var files) && files.VariantType == Variant.Type.Array)
                doc.MediaFiles = files.AsGodotArray();
        }

        return doc;
    }

    // ── Internals: media packaging ─────────────────────────────────────────

    /// <summary>
    /// Copies media referenced by the exported cues into <paramref name="mediaDir"/>
    /// and rewrites component paths to library-relative form.
    /// </summary>
    /// <returns>True when at least one file was packaged and paths rewritten.</returns>
    private bool PackageMedia(Dictionary cuesDict, string mediaDir, Array mediaManifest)
    {
        string sessionDir = _globalData?.SessionDir;
        bool any = false;

        foreach (var kv in cuesDict)
        {
            if (kv.Value.VariantType != Variant.Type.Dictionary) continue;
            var cueData = kv.Value.AsGodotDictionary();
            if (!cueData.TryGetValue("Components", out var compsVar) ||
                compsVar.VariantType != Variant.Type.Array)
                continue;

            var comps = compsVar.AsGodotArray();
            for (int i = 0; i < comps.Count; i++)
            {
                if (comps[i].VariantType != Variant.Type.Dictionary) continue;
                var comp = comps[i].AsGodotDictionary();
                string type = comp.TryGetValue("Type", out var t) ? t.AsString() : string.Empty;

                if (type == "Audio" && comp.TryGetValue("AudioFile", out var af))
                {
                    if (TryPackageOneFile(af.AsString(), MediaBackupKind.Audio, sessionDir, mediaDir,
                            out string libRel, out string original))
                    {
                        comp["AudioFile"] = libRel;
                        mediaManifest.Add(new Dictionary
                        {
                            { "ComponentType", "Audio" },
                            { "OriginalStoredPath", original },
                            { "LibraryRelativePath", libRel }
                        });
                        any = true;
                    }
                }
                else if (type == "Video" && comp.TryGetValue("VideoFile", out var vf))
                {
                    string path = vf.AsString();
                    var kind = MediaBackupManager.DetectKindFromPath(path);
                    if (kind != MediaBackupKind.Audio)
                    {
                        // Prefer Image when extension matches images, else Video
                        if (kind == MediaBackupKind.Image || IsImagePath(path))
                            kind = MediaBackupKind.Image;
                        else
                            kind = MediaBackupKind.Video;
                    }
                    else
                    {
                        kind = MediaBackupKind.Video;
                    }

                    if (TryPackageOneFile(path, kind, sessionDir, mediaDir,
                            out string libRel, out string original))
                    {
                        comp["VideoFile"] = libRel;
                        mediaManifest.Add(new Dictionary
                        {
                            { "ComponentType", "Video" },
                            { "OriginalStoredPath", original },
                            { "LibraryRelativePath", libRel }
                        });
                        any = true;
                    }
                }
            }
        }

        return any;
    }

    private static bool IsImagePath(string path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        string ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".png" or ".jpg" or ".jpeg" or ".webp" or ".bmp" or ".gif" or ".tif" or ".tiff" or ".svg";
    }

    private bool TryPackageOneFile(
        string storedPath,
        MediaBackupKind kind,
        string sessionDir,
        string mediaDir,
        out string libraryRelativePath,
        out string originalStored)
    {
        libraryRelativePath = string.Empty;
        originalStored = storedPath ?? string.Empty;

        if (string.IsNullOrWhiteSpace(storedPath))
            return false;

        try
        {
            string resolved = MediaPaths.Resolve(storedPath, sessionDir);
            if (string.IsNullOrEmpty(resolved) || !File.Exists(resolved))
            {
                Log($"Library: media missing, not packaged — {storedPath}", LogType.Warning);
                return false;
            }

            string sub = LibraryPaths.KindFolderName(kind);
            string destDir = Path.Combine(mediaDir, sub);
            Directory.CreateDirectory(destDir);

            string fileName = Path.GetFileName(resolved);
            string destPath = Path.Combine(destDir, fileName);
            destPath = MakeUniqueDest(destPath);

            File.Copy(resolved, destPath, overwrite: false);
            libraryRelativePath = sub + "/" + Path.GetFileName(destPath);
            libraryRelativePath = libraryRelativePath.Replace('\\', '/');
            return true;
        }
        catch (Exception ex)
        {
            GD.PrintErr($"CueLibraryManager:TryPackageOneFile - {ex.Message}");
            return false;
        }
    }

    private static string MakeUniqueDest(string destPath)
    {
        if (!File.Exists(destPath))
            return destPath;

        string dir = Path.GetDirectoryName(destPath) ?? string.Empty;
        string name = Path.GetFileNameWithoutExtension(destPath);
        string ext = Path.GetExtension(destPath);
        for (int i = 1; i < 1000; i++)
        {
            string candidate = Path.Combine(dir, $"{name}_{i}{ext}");
            if (!File.Exists(candidate))
                return candidate;
        }

        return Path.Combine(dir, $"{name}_{Guid.NewGuid():N}{ext}");
    }

    private void PrepareMediaPathsForImport(LibraryEntryDocument document, LibraryLoadOptions options)
    {
        if (document?.Cues == null) return;

        string mediaDir = document.AbsoluteMediaDir;
        bool useLibraryMedia = document.LibraryRelativeMedia && Directory.Exists(mediaDir);
        string sessionDir = _globalData?.SessionDir;
        var backup = GetNodeOrNull<MediaBackupManager>("/root/MediaBackupManager");

        foreach (var kv in document.Cues)
        {
            if (kv.Value.VariantType != Variant.Type.Dictionary) continue;
            var cueData = kv.Value.AsGodotDictionary();
            if (!cueData.TryGetValue("Components", out var compsVar) ||
                compsVar.VariantType != Variant.Type.Array)
                continue;

            var comps = compsVar.AsGodotArray();
            for (int i = 0; i < comps.Count; i++)
            {
                if (comps[i].VariantType != Variant.Type.Dictionary) continue;
                var comp = comps[i].AsGodotDictionary();
                string type = comp.TryGetValue("Type", out var t) ? t.AsString() : string.Empty;

                if (type == "Audio" && comp.TryGetValue("AudioFile", out var af))
                {
                    string stored = af.AsString();
                    string absolute = useLibraryMedia && MediaPaths.IsShowRelative(stored)
                        ? LibraryPaths.ResolveMedia(stored, mediaDir)
                        : MediaPaths.Resolve(stored, sessionDir);

                    // If still missing and library media exists, try library root
                    if (!File.Exists(absolute) && useLibraryMedia)
                        absolute = LibraryPaths.ResolveMedia(stored, mediaDir);

                    string toStore = absolute;
                    if (options.CopyMediaIntoShow && backup != null && File.Exists(absolute))
                    {
                        string relative = backup.EnsureMediaBackedUp(absolute, MediaBackupKind.Audio);
                        if (!string.IsNullOrEmpty(relative))
                            toStore = relative;
                    }
                    else if (File.Exists(absolute))
                    {
                        toStore = absolute;
                    }

                    comp["AudioFile"] = toStore;
                }
                else if (type == "Video" && comp.TryGetValue("VideoFile", out var vf))
                {
                    string stored = vf.AsString();
                    string absolute = useLibraryMedia && MediaPaths.IsShowRelative(stored)
                        ? LibraryPaths.ResolveMedia(stored, mediaDir)
                        : MediaPaths.Resolve(stored, sessionDir);

                    if (!File.Exists(absolute) && useLibraryMedia)
                        absolute = LibraryPaths.ResolveMedia(stored, mediaDir);

                    string toStore = absolute;
                    if (options.CopyMediaIntoShow && backup != null && File.Exists(absolute))
                    {
                        var kind = IsImagePath(absolute) ? MediaBackupKind.Image : MediaBackupKind.Video;
                        string relative = backup.EnsureMediaBackedUp(absolute, kind);
                        if (!string.IsNullOrEmpty(relative))
                            toStore = relative;
                    }
                    else if (File.Exists(absolute))
                    {
                        toStore = absolute;
                    }

                    comp["VideoFile"] = toStore;
                }
            }
        }
    }

    // ── Internals: dependencies & control remaps ───────────────────────────

    private Dictionary CaptureDependencies(Dictionary cuesDict)
    {
        var patches = new Array();
        var osc = new Array();
        var layers = new Array();
        var cueLights = new Array();
        var seenPatch = new HashSet<int>();
        var seenOsc = new HashSet<int>();
        var seenLayer = new HashSet<int>();
        var seenCl = new HashSet<int>();

        var patchTable = _globalData?.Settings?.GetAudioOutputPatches();

        foreach (var kv in cuesDict)
        {
            if (kv.Value.VariantType != Variant.Type.Dictionary) continue;
            var cueData = kv.Value.AsGodotDictionary();
            if (!cueData.TryGetValue("Components", out var compsVar) ||
                compsVar.VariantType != Variant.Type.Array)
                continue;

            foreach (var compVar in compsVar.AsGodotArray())
            {
                if (compVar.VariantType != Variant.Type.Dictionary) continue;
                var comp = compVar.AsGodotDictionary();
                string type = comp.TryGetValue("Type", out var t) ? t.AsString() : string.Empty;

                if ((type == "Audio" || type == "Video") && comp.TryGetValue("PatchId", out var pidVar))
                {
                    int pid = pidVar.AsInt32();
                    if (pid >= 0 && seenPatch.Add(pid))
                    {
                        string name = string.Empty;
                        if (patchTable != null && patchTable.TryGetValue(pid, out var patch) && patch != null)
                            name = patch.Name ?? string.Empty;
                        patches.Add(new Dictionary { { "Id", pid }, { "Name", name } });
                    }
                }

                if (type == "Video" && comp.TryGetValue("TargetLayerId", out var lidVar))
                {
                    int lid = lidVar.AsInt32();
                    if (lid >= 0 && seenLayer.Add(lid))
                    {
                        string name = string.Empty;
                        var layer = DisplaysManager.GetLayerById(lid);
                        if (layer != null)
                            name = layer.LayerName ?? string.Empty;
                        layers.Add(new Dictionary { { "Id", lid }, { "Name", name } });
                    }
                }

                if (type == "OscComponent" && comp.TryGetValue("OscConnectionId", out var oidVar))
                {
                    int oid = oidVar.AsInt32();
                    if (oid >= 0 && seenOsc.Add(oid))
                    {
                        string name = string.Empty;
                        var conn = OscConnections.GetCueOscConnection(oid);
                        if (conn != null)
                            name = conn.Name ?? string.Empty;
                        osc.Add(new Dictionary { { "Id", oid }, { "Name", name } });
                    }
                }

                if (type == "CueLight" && comp.TryGetValue("CueLightId", out var clidVar))
                {
                    int clid = clidVar.AsInt32();
                    if (clid >= 0 && seenCl.Add(clid))
                    {
                        string name = string.Empty;
                        var cl = _globalData?.CueLightManager?.GetCueLight(clid);
                        if (cl != null)
                            name = cl.Name ?? string.Empty;
                        cueLights.Add(new Dictionary { { "Id", clid }, { "Name", name } });
                    }
                }
            }
        }

        return new Dictionary
        {
            { "Patches", patches },
            { "OscConnections", osc },
            { "Layers", layers },
            { "CueLights", cueLights }
        };
    }

    private void ApplyDependencyNameRelink(LibraryEntryDocument document)
    {
        if (document?.Cues == null || document.Dependencies == null)
            return;

        // Build name → current id maps
        var patchByName = new System.Collections.Generic.Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var patchTable = _globalData?.Settings?.GetAudioOutputPatches();
        if (patchTable != null)
        {
            foreach (var p in patchTable.Values)
            {
                if (p != null && GodotObject.IsInstanceValid(p) && !string.IsNullOrEmpty(p.Name))
                    patchByName[p.Name] = p.Id;
            }
        }

        var layerByName = new System.Collections.Generic.Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (DisplaysManager.Layers != null)
        {
            foreach (var layer in DisplaysManager.Layers)
            {
                if (layer != null && !string.IsNullOrEmpty(layer.LayerName))
                    layerByName[layer.LayerName] = layer.LayerId;
            }
        }

        var oscByName = new System.Collections.Generic.Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var conn in OscConnections.Connections ?? new System.Collections.Generic.List<CueOscConnection>())
        {
            if (conn != null && !string.IsNullOrEmpty(conn.Name))
                oscByName[conn.Name] = conn.Id;
        }

        var clByName = new System.Collections.Generic.Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var lights = _globalData?.CueLightManager?.GetCueLights();
        if (lights != null)
        {
            foreach (var cl in lights)
            {
                if (cl != null && !string.IsNullOrEmpty(cl.Name))
                    clByName[cl.Name] = cl.Id;
            }
        }

        // Saved id → name
        var patchIdToName = BuildIdNameMap(document.Dependencies, "Patches");
        var oscIdToName = BuildIdNameMap(document.Dependencies, "OscConnections");
        var layerIdToName = BuildIdNameMap(document.Dependencies, "Layers");
        var clIdToName = BuildIdNameMap(document.Dependencies, "CueLights");

        int preferredPatchId = _globalData?.Settings?.GetPreferredAudioOutputPatch()?.Id ?? -1;
        int firstLayerId = -1;
        if (DisplaysManager.Layers != null && DisplaysManager.Layers.Count > 0)
            firstLayerId = DisplaysManager.Layers[0].LayerId;

        foreach (var kv in document.Cues)
        {
            if (kv.Value.VariantType != Variant.Type.Dictionary) continue;
            var cueData = kv.Value.AsGodotDictionary();
            if (!cueData.TryGetValue("Components", out var compsVar) ||
                compsVar.VariantType != Variant.Type.Array)
                continue;

            foreach (var compVar in compsVar.AsGodotArray())
            {
                if (compVar.VariantType != Variant.Type.Dictionary) continue;
                var comp = compVar.AsGodotDictionary();
                string type = comp.TryGetValue("Type", out var t) ? t.AsString() : string.Empty;

                if ((type == "Audio" || type == "Video") && comp.TryGetValue("PatchId", out var pidVar))
                {
                    int oldId = pidVar.AsInt32();
                    int newId = ResolveByNameOrFallback(oldId, patchIdToName, patchByName, preferredPatchId, patchTable);
                    comp["PatchId"] = newId;
                }

                if (type == "Video" && comp.TryGetValue("TargetLayerId", out var lidVar))
                {
                    int oldId = lidVar.AsInt32();
                    int newId = ResolveByNameOrFallback(oldId, layerIdToName, layerByName, firstLayerId, null);
                    // If still unknown and we have a first layer, prefer it over -1 for playability
                    if (newId < 0 && firstLayerId >= 0 && oldId >= 0)
                        newId = firstLayerId;
                    comp["TargetLayerId"] = newId;
                }

                if (type == "OscComponent" && comp.TryGetValue("OscConnectionId", out var oidVar))
                {
                    int oldId = oidVar.AsInt32();
                    int newId = ResolveByNameOrFallback(oldId, oscIdToName, oscByName, -1, null);
                    comp["OscConnectionId"] = newId;
                }

                if (type == "CueLight" && comp.TryGetValue("CueLightId", out var clidVar))
                {
                    int oldId = clidVar.AsInt32();
                    int newId = ResolveByNameOrFallback(oldId, clIdToName, clByName, -1, null);
                    comp["CueLightId"] = newId;
                }
            }
        }
    }

    private static System.Collections.Generic.Dictionary<int, string> BuildIdNameMap(
        Dictionary dependencies, string key)
    {
        var map = new System.Collections.Generic.Dictionary<int, string>();
        if (dependencies == null || !dependencies.TryGetValue(key, out var arrVar) ||
            arrVar.VariantType != Variant.Type.Array)
            return map;

        foreach (var item in arrVar.AsGodotArray())
        {
            if (item.VariantType != Variant.Type.Dictionary) continue;
            var d = item.AsGodotDictionary();
            int id = d.TryGetValue("Id", out var idV) ? idV.AsInt32() : -1;
            string name = d.TryGetValue("Name", out var nV) ? nV.AsString() : string.Empty;
            if (id >= 0 && !string.IsNullOrEmpty(name))
                map[id] = name;
        }

        return map;
    }

    private static int ResolveByNameOrFallback(
        int oldId,
        System.Collections.Generic.Dictionary<int, string> idToName,
        System.Collections.Generic.Dictionary<string, int> nameToId,
        int fallbackId,
        System.Collections.Generic.IDictionary<int, AudioOutputPatch> patchTable)
    {
        if (oldId < 0)
            return -1;

        // Exact id still valid (patches only)
        if (patchTable != null && patchTable.ContainsKey(oldId))
            return oldId;

        if (idToName.TryGetValue(oldId, out string name) &&
            !string.IsNullOrEmpty(name) &&
            nameToId.TryGetValue(name, out int matched))
            return matched;

        if (fallbackId >= 0)
            return fallbackId;

        return -1;
    }

    private static void RemapControlTargetsInCues(
        Dictionary cuesDict,
        System.Collections.Generic.Dictionary<int, int> sessionToTemp)
    {
        foreach (var kv in cuesDict)
        {
            if (kv.Value.VariantType != Variant.Type.Dictionary) continue;
            var cueData = kv.Value.AsGodotDictionary();
            if (!cueData.TryGetValue("Components", out var compsVar) ||
                compsVar.VariantType != Variant.Type.Array)
                continue;

            foreach (var compVar in compsVar.AsGodotArray())
            {
                if (compVar.VariantType != Variant.Type.Dictionary) continue;
                var comp = compVar.AsGodotDictionary();
                string type = comp.TryGetValue("Type", out var t) ? t.AsString() : string.Empty;
                if (type != "Control") continue;

                if (comp.TryGetValue("TargetCueId", out var tidVar))
                {
                    int oldTarget = tidVar.AsInt32();
                    if (oldTarget >= 0 && sessionToTemp.TryGetValue(oldTarget, out int newTemp))
                        comp["TargetCueId"] = newTemp;
                    else
                    {
                        comp["TargetCueId"] = -1;
                        // Keep TargetCueNum as a soft hint; import will not remint numbers
                    }
                }
            }
        }
    }

    // ── JSON / utilities ───────────────────────────────────────────────────

    private static void StripWaveformPayloads(Dictionary cueData)
    {
        if (cueData == null || !cueData.ContainsKey("Components")) return;
        var comps = cueData["Components"].AsGodotArray();
        foreach (var compVar in comps)
        {
            if (compVar.VariantType != Variant.Type.Dictionary) continue;
            var comp = compVar.AsGodotDictionary();
            if (comp.ContainsKey("WaveformData"))
                comp["WaveformData"] = System.Array.Empty<byte>();
        }
    }

    private static Dictionary DeepClone(Dictionary source)
    {
        if (source == null) return new Dictionary();
        string json = Json.Stringify(source);
        using var parser = new Json();
        var err = parser.Parse(json);
        if (err != Error.Ok)
            throw new InvalidOperationException($"Library deep-clone JSON parse failed: {err}");
        return parser.Data.AsGodotDictionary();
    }

    private static void WriteJsonFile(string absolutePath, Dictionary data)
    {
        string json = Json.Stringify(data, "\t");
        File.WriteAllText(absolutePath, json);
    }

    private static Dictionary ReadJsonFile(string absolutePath)
    {
        if (!File.Exists(absolutePath))
            return null;

        string text = File.ReadAllText(absolutePath);
        if (string.IsNullOrWhiteSpace(text))
            return null;

        using var parser = new Json();
        var err = parser.Parse(text);
        if (err != Error.Ok)
        {
            GD.PrintErr($"CueLibraryManager:ReadJsonFile - Parse failed for {absolutePath}: {err}");
            return null;
        }

        if (parser.Data.VariantType != Variant.Type.Dictionary)
            return null;

        return parser.Data.AsGodotDictionary();
    }

    private void Log(string message, LogType type)
    {
        _globalSignals?.EmitSignal(nameof(GlobalSignals.Log), message, (int)type);
    }
}
