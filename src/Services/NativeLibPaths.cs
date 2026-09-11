// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Godot;

namespace Cue2.Services;

/// <summary>
/// Resolves on-disk directories for platform native libraries (FFmpeg, RtMidi, etc.).
/// </summary>
/// <remarks>
/// Editor builds load from <c>res://bin/{platform}/</c>. Exported Godot C# builds cannot
/// reliably <see cref="System.Runtime.InteropServices.NativeLibrary.Load"/> from a PCK, so
/// post-export packaging must place shared libraries as real files and this helper searches
/// (see <see cref="GetCandidateDirectories"/> and <c>docs/export-packaging.md</c>).
/// <para>
/// macOS .app layout (preferred):
/// <c>Contents/Frameworks/</c> for loadable dylibs, with optional
/// <c>Contents/Resources/bin/macos/</c> as an LGPL-friendly replace path.
/// </para>
/// </remarks>
public static class NativeLibPaths
{
    /// <summary>
    /// Platform folder name under <c>bin/</c> for the current OS and process architecture
    /// (e.g. <c>win64</c>, <c>macos</c>, <c>linux64</c>).
    /// </summary>
    /// <param name="label">Readable platform label for logs.</param>
    /// <returns>Directory name under <c>bin/</c>.</returns>
    public static string GetPlatformDir(out string label)
    {
        Architecture arch = RuntimeInformation.ProcessArchitecture;

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            if (arch == Architecture.Arm64)
            {
                label = "Windows ARM64";
                return "winarm64";
            }

            if (arch == Architecture.X64)
            {
                label = "Windows x64";
                return "win64";
            }

            label = $"Windows x64 (fallback for {arch})";
            GD.PrintErr($"NativeLibPaths:GetPlatformDir - Unsupported Windows arch {arch}; defaulting to win64.");
            return "win64";
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            label = arch == Architecture.Arm64 ? "macOS ARM64" : "macOS x64";
            return "macos";
        }

        if (arch == Architecture.Arm64)
        {
            label = "Linux ARM64";
            return "linuxarm64";
        }

        if (arch == Architecture.X64)
        {
            label = "Linux x64";
            return "linux64";
        }

        label = $"Linux x64 (fallback for {arch})";
        GD.PrintErr($"NativeLibPaths:GetPlatformDir - Unsupported Linux arch {arch}; defaulting to linux64.");
        return "linux64";
    }

    /// <summary>
    /// Builds the on-disk shared library file name for an FFmpeg library on the current platform.
    /// </summary>
    /// <param name="name">Base name without prefix/suffix (e.g. <c>avutil</c>, <c>avcodec</c>).</param>
    /// <param name="major">ABI major version (e.g. <c>60</c>, <c>62</c>).</param>
    /// <returns>File name such as <c>avutil-61.dll</c>, <c>libavutil.61.dylib</c>, or <c>libavutil.so.61</c>.</returns>
    public static string GetFFmpegLibraryFileName(string name, string major)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return $"{name}-{major}.dll";

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return $"lib{name}.{major}.dylib";

        return $"lib{name}.so.{major}";
    }

    /// <summary>
    /// File name of the RtMidi 6.0 native library for the current process, or empty if unsupported.
    /// </summary>
    /// <param name="platformLabel">Readable platform label for logs.</param>
    /// <returns>Native library file name (e.g. <c>rtmidi.dll</c>, <c>librtmidi.so</c>).</returns>
    public static string GetRtMidiNativeFileName(out string platformLabel)
    {
        Architecture arch = RuntimeInformation.ProcessArchitecture;

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            platformLabel = arch == Architecture.Arm64 ? "Windows ARM64" : "Windows x64";
            return "rtmidi.dll";
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            platformLabel = arch == Architecture.Arm64 ? "macOS ARM64" : "macOS x64";
            return "librtmidi.dylib";
        }

        if (arch == Architecture.Arm64)
        {
            platformLabel = "Linux ARM64";
            return "librtmidi.so";
        }

        if (arch == Architecture.X64)
        {
            platformLabel = "Linux x64";
            return "librtmidi.so";
        }

        platformLabel = $"Linux x64 (fallback for {arch})";
        GD.PrintErr($"NativeLibPaths:GetRtMidiNativeFileName - Unsupported Linux arch {arch}; defaulting to linux64 name.");
        return "librtmidi.so";
    }

    /// <summary>
    /// Candidate directories that may contain platform natives, in search order.
    /// </summary>
    /// <param name="platformDir">Folder under <c>bin/</c> (from <see cref="GetPlatformDir"/>).</param>
    /// <returns>Absolute directory paths (may not all exist).</returns>
    public static IReadOnlyList<string> GetCandidateDirectories(string platformDir)
    {
        var dirs = new List<string>(16);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string dir)
        {
            if (string.IsNullOrWhiteSpace(dir))
                return;

            string full;
            try
            {
                full = Path.GetFullPath(dir);
            }
            catch
            {
                return;
            }

            full = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (seen.Add(full))
                dirs.Add(full);
        }

        string exePath = OS.GetExecutablePath();
        string exeDir = string.IsNullOrEmpty(exePath)
            ? string.Empty
            : Path.GetDirectoryName(exePath) ?? string.Empty;

        string baseDir = AppContext.BaseDirectory ?? string.Empty;
        if (!string.IsNullOrEmpty(baseDir))
            baseDir = baseDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        // ── High priority: managed host directory (data_Cue2_*) and flat natives ──
        if (!string.IsNullOrEmpty(baseDir))
        {
            Add(baseDir);
            Add(Path.Combine(baseDir, "bin", platformDir));
        }

        // ── macOS .app bundle layout ──
        // Executable is typically: App.app/Contents/MacOS/Cue2
        // Prefer Contents/Frameworks (standard for loadable dylibs), then Resources/bin/macos.
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX) && !string.IsNullOrEmpty(exeDir))
        {
            string contentsDir = TryGetMacAppContentsDir(exeDir);
            if (!string.IsNullOrEmpty(contentsDir))
            {
                string frameworks = Path.Combine(contentsDir, "Frameworks");
                Add(frameworks);
                Add(Path.Combine(frameworks, "bin", platformDir));

                string resources = Path.Combine(contentsDir, "Resources");
                Add(Path.Combine(resources, "bin", platformDir));
                Add(Path.Combine(resources, "bin"));
                Add(resources);

                // Universal exports ship both data_Cue2_macos_arm64 and data_Cue2_macos_x86_64.
                // BaseDirectory is the active arch; also probe siblings if needed.
                try
                {
                    if (Directory.Exists(resources))
                    {
                        foreach (string dataDir in Directory.EnumerateDirectories(resources, "data_Cue2_*"))
                        {
                            Add(dataDir);
                            Add(Path.Combine(dataDir, "bin", platformDir));
                        }
                    }
                }
                catch (Exception ex)
                {
                    GD.PrintErr($"NativeLibPaths:GetCandidateDirectories - Enumerate data_* failed: {ex.Message}");
                }

                Add(Path.Combine(contentsDir, "MacOS", "bin", platformDir));
                Add(Path.Combine(contentsDir, "MacOS"));
            }
            else
            {
                // Loose macOS binary (not in .app): same layout as Windows next to the exe.
                Add(Path.Combine(exeDir, "bin", platformDir));
                Add(exeDir);
            }
        }
        else if (!string.IsNullOrEmpty(exeDir))
        {
            // Windows / Linux: beside the host binary
            Add(Path.Combine(exeDir, "bin", platformDir));
            Add(exeDir);
        }

        // Parent of BaseDirectory (e.g. Resources/ when BaseDirectory is data_Cue2_*)
        if (!string.IsNullOrEmpty(baseDir))
        {
            try
            {
                string parent = Directory.GetParent(baseDir)?.FullName;
                if (!string.IsNullOrEmpty(parent))
                {
                    Add(Path.Combine(parent, "bin", platformDir));
                    Add(Path.Combine(parent, "Frameworks"));
                    Add(parent);
                }
            }
            catch
            {
                // ignore
            }
        }

        // Editor / project checkout: real files under res://bin/{platform}/
        try
        {
            string resBin = ProjectSettings.GlobalizePath($"res://bin/{platformDir}/");
            Add(resBin);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"NativeLibPaths:GetCandidateDirectories - GlobalizePath failed: {ex.Message}");
        }

        return dirs;
    }

    /// <summary>
    /// If <paramref name="exeDir"/> is <c>.../Something.app/Contents/MacOS</c>, returns the
    /// <c>Contents</c> directory; otherwise empty.
    /// </summary>
    private static string TryGetMacAppContentsDir(string exeDir)
    {
        try
        {
            // .../App.app/Contents/MacOS
            string dir = Path.GetFullPath(exeDir);
            string leaf = Path.GetFileName(dir);
            if (!leaf.Equals("MacOS", StringComparison.OrdinalIgnoreCase))
                return string.Empty;

            string contents = Directory.GetParent(dir)?.FullName;
            if (string.IsNullOrEmpty(contents))
                return string.Empty;

            if (!Path.GetFileName(contents).Equals("Contents", StringComparison.OrdinalIgnoreCase))
                return string.Empty;

            // Optional sanity: parent ends with .app
            string appBundle = Directory.GetParent(contents)?.FullName ?? string.Empty;
            if (!string.IsNullOrEmpty(appBundle) &&
                !appBundle.EndsWith(".app", StringComparison.OrdinalIgnoreCase))
            {
                // Still accept Contents/MacOS layout even if not named .app
            }

            return contents;
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// Finds the first candidate directory that contains <paramref name="fileName"/>.
    /// </summary>
    /// <param name="fileName">Library file name (not a path).</param>
    /// <param name="platformDir">Optional platform dir; resolved via <see cref="GetPlatformDir"/> when null.</param>
    /// <param name="directory">Directory that contains the file when found; otherwise empty.</param>
    /// <param name="tried">Directories that were searched (for error messages).</param>
    /// <returns>Full path to the library file, or empty if not found.</returns>
    public static string FindLibraryFile(
        string fileName,
        string platformDir,
        out string directory,
        out IReadOnlyList<string> tried)
    {
        directory = string.Empty;
        if (string.IsNullOrEmpty(fileName))
        {
            tried = Array.Empty<string>();
            return string.Empty;
        }

        if (string.IsNullOrEmpty(platformDir))
            platformDir = GetPlatformDir(out _);

        var candidates = GetCandidateDirectories(platformDir);
        tried = candidates;

        foreach (string dir in candidates)
        {
            string fullPath = Path.Combine(dir, fileName);
            if (File.Exists(fullPath))
            {
                directory = dir;
                return fullPath;
            }
        }

        return string.Empty;
    }

    /// <summary>
    /// Finds a directory that contains all of the given library file names (same folder).
    /// </summary>
    /// <param name="fileNames">Required file names in one directory.</param>
    /// <param name="platformDir">Platform folder under <c>bin/</c>.</param>
    /// <param name="tried">Directories searched when not found.</param>
    /// <returns>Directory path, or empty if no single directory has every file.</returns>
    public static string FindDirectoryContainingAll(
        IReadOnlyList<string> fileNames,
        string platformDir,
        out IReadOnlyList<string> tried)
    {
        if (fileNames == null || fileNames.Count == 0)
        {
            tried = Array.Empty<string>();
            return string.Empty;
        }

        if (string.IsNullOrEmpty(platformDir))
            platformDir = GetPlatformDir(out _);

        var candidates = GetCandidateDirectories(platformDir);
        tried = candidates;

        foreach (string dir in candidates)
        {
            bool allPresent = true;
            foreach (string name in fileNames)
            {
                if (!File.Exists(Path.Combine(dir, name)))
                {
                    allPresent = false;
                    break;
                }
            }

            if (allPresent)
                return dir;
        }

        return string.Empty;
    }

    /// <summary>
    /// Formats candidate directories for error logs (existing dirs marked with *).
    /// </summary>
    public static string FormatTriedDirectories(IReadOnlyList<string> tried)
    {
        if (tried == null || tried.Count == 0)
            return "(no candidates)";

        var parts = new List<string>(tried.Count);
        foreach (string dir in tried)
        {
            bool exists = Directory.Exists(dir);
            parts.Add(exists ? $"{dir} [*]" : dir);
        }

        return string.Join("; ", parts);
    }
}
