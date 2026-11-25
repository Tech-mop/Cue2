// SPDX-FileCopyrightText: 2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System;
using System.IO;
using System.Text;

namespace Cue2.Media;

/// <summary>
/// Turns a filesystem path into an FFmpeg input URL and explains open failures.
/// </summary>
/// <remarks>
/// <see cref="FFmpeg.AutoGen.ffmpeg.avformat_open_input"/> treats the path as a URL.
/// A leading <c>scheme:</c> is a protocol (concat, http, …). <c>%</c> is an image-sequence
/// pattern and a URL escape. <c>#</c> is a fragment. <c>?</c> is a query string.
/// Prefixing <c>file:</c> and setting <c>pattern_type=none</c> keeps those characters literal.
/// Path length limits still come from the OS (component 255 UTF-8 bytes; Windows ~260,
/// macOS 1024, Linux 4096). SDL is not used to open media files.
/// </remarks>
public static class FfmpegMediaPath
{
    /// <summary>Characters FFmpeg may parse as URL syntax instead of a file name.</summary>
    public static readonly char[] UrlSyntaxChars = { '%', '#', '?' };

    /// <summary>Maximum UTF-8 bytes in one path component (NAME_MAX on macOS/Linux; Windows is similar).</summary>
    public const int MaxFileNameBytes = 255;

    /// <summary>
    /// Builds the URL string to pass to <c>avformat_open_input</c> for a local file.
    /// </summary>
    /// <param name="filesystemPath">Absolute or relative filesystem path.</param>
    /// <returns>A <c>file:</c> URL with forward slashes, or the original string when empty.</returns>
    public static string ToOpenUrl(string filesystemPath)
    {
        if (string.IsNullOrEmpty(filesystemPath))
            return filesystemPath;

        string full;
        try
        {
            full = Path.GetFullPath(filesystemPath);
        }
        catch
        {
            full = filesystemPath;
        }

        string slash = full.Replace('\\', '/');
        if (slash.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            return slash;

        return "file:" + slash;
    }

    /// <summary>True when the path contains <c>%</c>, <c>#</c>, or <c>?</c>.</summary>
    public static bool ContainsUrlSyntaxChars(string path)
    {
        if (string.IsNullOrEmpty(path))
            return false;
        return path.IndexOfAny(UrlSyntaxChars) >= 0;
    }

    /// <summary>
    /// True when the path looks like an FFmpeg URL protocol rather than a local file
    /// (<c>concat:</c>, <c>http:</c>, …). Windows drive letters are not protocols.
    /// </summary>
    public static bool LooksLikeUrlProtocol(string path)
    {
        if (string.IsNullOrEmpty(path))
            return false;

        // Windows drive: C:\ or C:/
        if (path.Length >= 2 && char.IsLetter(path[0]) && path[1] == ':')
        {
            if (path.Length == 2 || path[2] == '/' || path[2] == '\\')
                return false;
        }

        int colon = path.IndexOf(':');
        if (colon <= 0)
            return false;

        for (int i = 0; i < colon; i++)
        {
            char c = path[i];
            if (c == '/' || c == '\\')
                return false;
            bool ok = char.IsLetter(c)
                      || (i > 0 && (char.IsDigit(c) || c == '+' || c == '-' || c == '.'));
            if (!ok)
                return false;
        }

        return char.IsLetter(path[0]);
    }

    /// <summary>UTF-8 byte length of one path component or the full path.</summary>
    public static int Utf8ByteLength(string value)
        => string.IsNullOrEmpty(value) ? 0 : Encoding.UTF8.GetByteCount(value);

    /// <summary>
    /// True when a component exceeds <see cref="MaxFileNameBytes"/> or the full path exceeds
    /// the platform PATH_MAX (Windows 259, macOS 1023, otherwise 4095).
    /// </summary>
    public static bool IsLikelyTooLong(string path)
    {
        if (string.IsNullOrEmpty(path))
            return false;

        int maxPath = OperatingSystem.IsWindows() ? 259
            : OperatingSystem.IsMacOS() ? 1023
            : 4095;

        if (Utf8ByteLength(path) > maxPath)
            return true;

        string name;
        try
        {
            name = Path.GetFileName(path);
        }
        catch
        {
            return false;
        }

        if (Utf8ByteLength(name) > MaxFileNameBytes)
            return true;

        try
        {
            foreach (string part in path.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries))
            {
                // Skip Windows drive "C:"
                if (part.Length == 2 && char.IsLetter(part[0]) && part[1] == ':')
                    continue;
                if (Utf8ByteLength(part) > MaxFileNameBytes)
                    return true;
            }
        }
        catch
        {
            // ignore
        }

        return false;
    }

    /// <summary>
    /// User-facing error when FFmpeg (or the OS) could not open a media file.
    /// Cue2 does not add the file.
    /// </summary>
    /// <param name="path">Filesystem path that failed.</param>
    /// <param name="technicalError">FFmpeg or IO error text.</param>
    /// <returns>A log line naming the file and, when we can tell, why.</returns>
    public static string FormatRejectMessage(string path, string technicalError)
    {
        string name;
        try
        {
            name = Path.GetFileName(path);
        }
        catch
        {
            name = path;
        }

        if (string.IsNullOrWhiteSpace(name))
            name = path ?? "this file";

        string detail = string.IsNullOrWhiteSpace(technicalError)
            ? "the media reader could not open it"
            : technicalError.Trim().TrimEnd('.');

        string hint = HintFor(path, detail);
        string core = $"Could not read \"{name}\": {detail}.";
        if (!string.IsNullOrEmpty(hint))
            core += " " + hint;
        return core + " Cue2 did not add this file.";
    }

    /// <summary>Short extra sentence when length or URL syntax likely caused the failure.</summary>
    public static string HintFor(string path, string technicalError)
    {
        string err = technicalError ?? string.Empty;
        bool tooLongError = err.IndexOf("too long", StringComparison.OrdinalIgnoreCase) >= 0
                            || err.IndexOf("name too long", StringComparison.OrdinalIgnoreCase) >= 0
                            || err.IndexOf("ENAMETOOLONG", StringComparison.OrdinalIgnoreCase) >= 0;

        if (tooLongError || IsLikelyTooLong(path))
            return "The file name or path is too long.";

        if (ContainsUrlSyntaxChars(path) || LooksLikeUrlProtocol(path))
            return "Names with %, #, ?, or a colon prefix can confuse the media reader.";

        return string.Empty;
    }
}
