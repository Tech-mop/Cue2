// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using Godot;
using System;

namespace Cue2.UI.Utilities;

/// <summary>
/// Paths for a show session folder layout (type-based media subfolders).
/// </summary>
public sealed class SessionFolderPaths
{
    /// <summary>Absolute path to the session root directory (contains the .c2 file).</summary>
    public string SessionDir { get; init; } = string.Empty;

    /// <summary>Absolute path for audio media copies (<c>SessionDir/Audio</c>).</summary>
    public string AudioDir { get; init; } = string.Empty;

    /// <summary>Absolute path for video media copies (<c>SessionDir/Video</c>).</summary>
    public string VideoDir { get; init; } = string.Empty;

    /// <summary>Absolute path for image media copies (<c>SessionDir/Images</c>).</summary>
    public string ImagesDir { get; init; } = string.Empty;

    /// <summary>Absolute path for waveform cache (<c>SessionDir/Waveforms</c>).</summary>
    public string WaveformsDir { get; init; } = string.Empty;
}

/// <summary>
/// Utility class for handling directory and path operations.
/// </summary>
public static class DirectoryUtils
{
    /// <summary>Subfolder name for audio media inside a show folder.</summary>
    public const string AudioFolderName = "Audio";

    /// <summary>Subfolder name for video media inside a show folder.</summary>
    public const string VideoFolderName = "Video";

    /// <summary>Subfolder name for image media inside a show folder.</summary>
    public const string ImagesFolderName = "Images";

    /// <summary>Subfolder name for waveform caches inside a show folder.</summary>
    public const string WaveformsFolderName = "Waveforms";

    /// <summary>
    /// Prepares the session directory structure for saving a file.
    /// Given a user-provided save path (e.g., "/path/to/MySession.c2"), this method:
    /// - Extracts the base directory and filename without extension.
    /// - Creates a session folder named after the filename (e.g., "/path/to/MySession/").
    /// - Ensures type-based subfolders exist: Audio, Video, Images, Waveforms.
    /// - Returns the adjusted full path to save the file inside the session folder
    ///   (e.g., "/path/to/MySession/MySession.c2").
    /// If the directories already exist, it verifies them without overwriting.
    /// </summary>
    /// <param name="userSavePath">The user-provided full path for saving the file (e.g., "/path/to/MySession.c2").</param>
    /// <param name="folderPaths">Receives absolute paths for the session dir and media type folders.</param>
    /// <returns>The adjusted full path to save the file inside the session folder, or an empty string if an error occurs.</returns>
    public static string PrepareSessionDirectory(string userSavePath, out SessionFolderPaths folderPaths)
    {
        folderPaths = new SessionFolderPaths();

        try
        {
            if (string.IsNullOrEmpty(userSavePath))
            {
                GD.Print("DirectoryUtils:PrepareSessionDirectory - Invalid input: userSavePath is null or empty.");
                return string.Empty;
            }

            // Extract base directory, filename, and filename without extension using Godot's String extensions
            string baseDir = userSavePath.GetBaseDir();
            string fileName = userSavePath.GetFile();
            string fileNameWithoutExt = fileName.GetBaseName();
            if (string.IsNullOrEmpty(fileNameWithoutExt))
            {
                GD.Print($"DirectoryUtils:PrepareSessionDirectory - Could not extract filename without extension from: {userSavePath}");
                return string.Empty;
            }

            // Check if the base directory already ends with the session name (to avoid nesting)
            string baseDirLastSegment = baseDir.GetFile();
            string sessionDir;
            if (baseDirLastSegment == fileNameWithoutExt)
            {
                GD.Print($"DirectoryUtils:PrepareSessionDirectory - Base directory '{baseDir}' already matches session name '{fileNameWithoutExt}'. Using existing directory without nesting.");
                sessionDir = baseDir;
            }
            else
            {
                sessionDir = baseDir + "/" + fileNameWithoutExt;
            }

            // Create or verify session directory
            Error err = DirAccess.MakeDirAbsolute(sessionDir);
            if (!IsDirReady(err))
            {
                GD.Print($"DirectoryUtils:PrepareSessionDirectory - Failed to create or verify session directory '{sessionDir}': {err}");
            }

            string audioDir = EnsureSubfolder(sessionDir, AudioFolderName);
            string videoDir = EnsureSubfolder(sessionDir, VideoFolderName);
            string imagesDir = EnsureSubfolder(sessionDir, ImagesFolderName);
            string waveformsDir = EnsureSubfolder(sessionDir, WaveformsFolderName);

            folderPaths = new SessionFolderPaths
            {
                SessionDir = sessionDir,
                AudioDir = audioDir,
                VideoDir = videoDir,
                ImagesDir = imagesDir,
                WaveformsDir = waveformsDir,
            };

            // Construct and return the adjusted save path (e.g., "/path/to/MySession/MySession.c2")
            string adjustedSavePath = sessionDir + "/" + fileName;

            GD.Print($"DirectoryUtils:PrepareSessionDirectory - Session directory prepared successfully. Adjusted save path: {adjustedSavePath}");

            return adjustedSavePath;
        }
        catch (Exception ex)
        {
            GD.Print($"DirectoryUtils:PrepareSessionDirectory - Unexpected error preparing session directory for '{userSavePath}': {ex.Message}");
            folderPaths = new SessionFolderPaths();
            return string.Empty;
        }
    }

    /// <summary>
    /// Builds absolute type-folder paths for an existing session file without creating directories.
    /// Used when loading a show so media paths can be resolved relative to the show folder.
    /// </summary>
    /// <param name="sessionFilePath">Absolute path to the .c2 session file.</param>
    /// <returns>Folder paths rooted at the session directory, or empty paths if input is invalid.</returns>
    public static SessionFolderPaths GetSessionFolderPaths(string sessionFilePath)
    {
        if (string.IsNullOrEmpty(sessionFilePath))
            return new SessionFolderPaths();

        string sessionDir = sessionFilePath.GetBaseDir();
        if (string.IsNullOrEmpty(sessionDir))
            return new SessionFolderPaths();

        return new SessionFolderPaths
        {
            SessionDir = sessionDir,
            AudioDir = sessionDir + "/" + AudioFolderName,
            VideoDir = sessionDir + "/" + VideoFolderName,
            ImagesDir = sessionDir + "/" + ImagesFolderName,
            WaveformsDir = sessionDir + "/" + WaveformsFolderName,
        };
    }

    /// <summary>
    /// Creates a subfolder under <paramref name="sessionDir"/> if needed and returns its absolute path.
    /// </summary>
    /// <param name="sessionDir">Session root directory.</param>
    /// <param name="folderName">Subfolder name (e.g. Audio).</param>
    /// <returns>Absolute path to the subfolder.</returns>
    private static string EnsureSubfolder(string sessionDir, string folderName)
    {
        string path = sessionDir + "/" + folderName;
        Error err = DirAccess.MakeDirAbsolute(path);
        if (!IsDirReady(err))
            GD.Print($"DirectoryUtils:EnsureSubfolder - Failed to create or verify '{path}': {err}");
        return path;
    }

    /// <summary>
    /// True when <see cref="DirAccess.MakeDirAbsolute"/> succeeded or the folder already exists.
    /// </summary>
    private static bool IsDirReady(Error err) =>
        err == Error.Ok || err == Error.AlreadyExists || err == Error.AlreadyInUse;

    /// <summary>
    /// Folder name under Documents / Desktop for first-time showfile saves.
    /// </summary>
    public const string DefaultShowsFolderName = "Cue2";

    /// <summary>
    /// Resolves a writable default directory for a first-ever showfile save.
    /// </summary>
    /// <remarks>
    /// Order: <c>Documents/Cue2</c>, <c>Documents</c>, <c>Desktop/Cue2</c>, <c>Desktop</c>,
    /// then Godot <c>user://Shows</c>, then <c>user://</c>. Each candidate is created if
    /// missing and probed for write access so installed / sandboxed apps do not land in
    /// a read-only exe folder.
    /// </remarks>
    /// <returns>Absolute directory path (may be empty only if even user:// failed).</returns>
    public static string GetDefaultShowSaveDirectory()
    {
        string documents = OS.GetSystemDir(OS.SystemDir.Documents);
        string documentsCue2 = JoinDir(documents, DefaultShowsFolderName);
        if (TryEnsureWritableDirectory(documentsCue2))
            return documentsCue2;
        if (TryEnsureWritableDirectory(documents))
            return documents;

        string desktop = OS.GetSystemDir(OS.SystemDir.Desktop);
        string desktopCue2 = JoinDir(desktop, DefaultShowsFolderName);
        if (TryEnsureWritableDirectory(desktopCue2))
            return desktopCue2;
        if (TryEnsureWritableDirectory(desktop))
            return desktop;

        string userShows = ProjectSettings.GlobalizePath("user://Shows");
        if (TryEnsureWritableDirectory(userShows))
            return userShows;

        string userRoot = ProjectSettings.GlobalizePath("user://");
        if (TryEnsureWritableDirectory(userRoot))
            return userRoot;

        GD.PrintErr("DirectoryUtils:GetDefaultShowSaveDirectory - No writable save directory found.");
        return userRoot ?? string.Empty;
    }

    /// <summary>
    /// True when <paramref name="path"/> exists as a directory.
    /// </summary>
    /// <param name="path">Absolute directory path.</param>
    /// <returns>True when the path is a non-empty existing directory.</returns>
    public static bool DirectoryExists(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;
        try
        {
            return DirAccess.DirExistsAbsolute(path);
        }
        catch (Exception ex)
        {
            GD.Print($"DirectoryUtils:DirectoryExists - {path}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Creates <paramref name="path"/> if needed and verifies the process can write a file there.
    /// </summary>
    /// <param name="path">Absolute directory path.</param>
    /// <returns>True when the directory is usable for saving a showfile.</returns>
    public static bool TryEnsureWritableDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        try
        {
            if (!DirAccess.DirExistsAbsolute(path))
            {
                Error mkdir = DirAccess.MakeDirRecursiveAbsolute(path);
                if (mkdir != Error.Ok && mkdir != Error.AlreadyExists)
                    return false;
            }

            if (!DirAccess.DirExistsAbsolute(path))
                return false;

            string probe = path.TrimEnd('/', '\\') + "/.cue2_write_probe";
            using (var file = Godot.FileAccess.Open(probe, Godot.FileAccess.ModeFlags.Write))
            {
                if (file == null)
                    return false;
                file.StoreString("ok");
            }

            DirAccess.RemoveAbsolute(probe);
            return true;
        }
        catch (Exception ex)
        {
            GD.Print($"DirectoryUtils:TryEnsureWritableDirectory - {path}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Parent directory of a show session folder named after the .c2 file
    /// (<c>…/MyShow/MyShow.c2</c> → <c>…</c>), otherwise the file's directory.
    /// Used as the remembered "last save location" for a later new show.
    /// </summary>
    /// <param name="savedFilePath">Absolute path of the .c2 the user chose or that was written.</param>
    /// <returns>Absolute directory, or empty when the path is invalid.</returns>
    public static string GetShowSaveRootDirectory(string savedFilePath)
    {
        if (string.IsNullOrWhiteSpace(savedFilePath))
            return string.Empty;

        string dir = savedFilePath.GetBaseDir();
        if (string.IsNullOrEmpty(dir))
            return string.Empty;

        string fileBase = savedFilePath.GetFile().GetBaseName();
        if (!string.IsNullOrEmpty(fileBase) && dir.GetFile() == fileBase)
        {
            string parent = dir.GetBaseDir();
            if (!string.IsNullOrEmpty(parent))
                return parent;
        }

        return dir;
    }

    private static string JoinDir(string baseDir, string child)
    {
        if (string.IsNullOrWhiteSpace(baseDir))
            return string.Empty;
        if (string.IsNullOrWhiteSpace(child))
            return baseDir;
        return baseDir.TrimEnd('/', '\\') + "/" + child;
    }
}
