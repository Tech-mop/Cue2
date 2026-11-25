// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace Cue2.Domain.Cuelist;

/// <summary>
/// Expands OS file-drop paths into supported media files, including recursive folder contents.
/// </summary>
/// <remarks>
/// Godot <c>Window.FilesDropped</c> delivers the dropped path as-is. A folder arrives as a
/// directory path, not as the files inside it. This helper walks those directories, skips
/// hidden entries, and builds both a flat file list and a folder tree for cue import.
/// Does not call Godot APIs so plain C# tests can exercise it.
/// </remarks>
public static class DroppedMediaExpansion
{
	/// <summary>
	/// File count at which the drop popup warns that the import is large.
	/// </summary>
	public const int LargeImportFileThreshold = 50;

	/// <summary>Most metadata probes that cues created from a drop run at the same time.</summary>
	public const int MaxConcurrentProbes = 3;

	/// <summary>
	/// Shared slots for drop-created cue probes, so a big import cannot start hundreds of FFmpeg
	/// opens at once. Probes from GO, selection and the inspectors do not use these slots.
	/// </summary>
	public static readonly SemaphoreSlim ProbeSlots = new(MaxConcurrentProbes, MaxConcurrentProbes);

	/// <summary>Default most media files one drop may add (the walk stops there).</summary>
	public const int DefaultMaxFiles = 2000;

	/// <summary>Default deepest folder level below a dropped folder that is walked.</summary>
	public const int DefaultMaxDepth = 32;

	/// <summary>
	/// Walks <paramref name="droppedPaths"/> and returns supported media files plus a folder tree,
	/// with the default file and depth limits.
	/// </summary>
	/// <param name="droppedPaths">Paths from the OS drop (files and/or directories).</param>
	/// <param name="mediaGlobs">Extension globs such as <c>*.wav</c> / <c>*.mp4</c>.</param>
	/// <returns>Expanded result; never null. Empty when nothing supported remains.</returns>
	public static DroppedMediaResult Expand(IEnumerable<string> droppedPaths, IEnumerable<string> mediaGlobs)
	{
		return Expand(droppedPaths, mediaGlobs, DefaultMaxFiles, DefaultMaxDepth, null, CancellationToken.None);
	}

	/// <summary>
	/// Walks <paramref name="droppedPaths"/> and returns supported media files plus a folder tree.
	/// Safe to run on a worker thread (no Godot calls). Folder links (symlinks, junctions) inside a
	/// dropped folder are not entered, so a link back to a parent cannot loop.
	/// </summary>
	/// <param name="droppedPaths">Paths from the OS drop (files and/or directories).</param>
	/// <param name="mediaGlobs">Extension globs such as <c>*.wav</c> / <c>*.mp4</c>.</param>
	/// <param name="maxFiles">Stop after this many media files (sets <see cref="DroppedMediaResult.Truncated"/>).</param>
	/// <param name="maxDepth">Do not walk folders deeper than this below a dropped folder
	/// (sets <see cref="DroppedMediaResult.Truncated"/> when something was left out).</param>
	/// <param name="progress">Optional live count of media files found, for a progress display.</param>
	/// <param name="cancel">Stops the walk early; the result then has <see cref="DroppedMediaResult.Cancelled"/> set.</param>
	/// <returns>Expanded result; never null.</returns>
	public static DroppedMediaResult Expand(
		IEnumerable<string> droppedPaths,
		IEnumerable<string> mediaGlobs,
		int maxFiles,
		int maxDepth,
		DroppedMediaWalkProgress progress,
		CancellationToken cancel)
	{
		if (droppedPaths == null)
			return DroppedMediaResult.Empty;

		var walk = new Walk(ToExtensionSet(mediaGlobs), Math.Max(1, maxFiles), Math.Max(0, maxDepth), progress, cancel);
		var roots = new List<DroppedMediaNode>();
		bool hasFolderTree = false;

		foreach (string raw in droppedPaths)
		{
			if (walk.Stopped)
			{
				if (!walk.Cancelled)
					walk.Truncated = true;
				break;
			}

			if (string.IsNullOrWhiteSpace(raw))
				continue;

			string path;
			try
			{
				path = Path.GetFullPath(raw);
			}
			catch (Exception)
			{
				continue;
			}

			if (IsHiddenPath(path))
				continue;

			if (Directory.Exists(path))
			{
				if (IsFilesystemRoot(path))
					continue;

				var node = ExpandDirectory(path, 0, walk);
				if (node == null)
					continue;
				hasFolderTree = true;
				roots.Add(node);
			}
			else if (File.Exists(path) && IsSupportedFile(path, walk.Extensions))
			{
				if (walk.TryAddFile(path))
					roots.Add(DroppedMediaNode.File(GetEntryName(path), path));
			}
		}

		return new DroppedMediaResult(walk.Files, roots, hasFolderTree, walk.FolderCount)
		{
			Truncated = walk.Truncated,
			Cancelled = walk.Cancelled,
			UnreadableFolderCount = walk.UnreadableFolders,
			MaxFiles = walk.MaxFiles
		};
	}

	/// <summary>
	/// Cheap check on the dropped paths only (no folder walk): true when any path is a folder
	/// or an existing supported media file. Used where a full walk is not needed.
	/// </summary>
	/// <param name="droppedPaths">Paths from the OS drop.</param>
	/// <param name="mediaGlobs">Extension globs such as <c>*.wav</c>.</param>
	/// <returns>True when the drop may contain media.</returns>
	public static bool MayContainMedia(IEnumerable<string> droppedPaths, IEnumerable<string> mediaGlobs)
	{
		if (droppedPaths == null)
			return false;
		var extensions = ToExtensionSet(mediaGlobs);
		foreach (string raw in droppedPaths)
		{
			if (string.IsNullOrWhiteSpace(raw))
				continue;
			try
			{
				if (Directory.Exists(raw))
					return true;
				if (File.Exists(raw) && IsSupportedFile(raw, extensions))
					return true;
			}
			catch (Exception)
			{
				// Unreadable path: not media.
			}
		}

		return false;
	}

	/// <summary>
	/// True when any dropped path is a folder, so the drop needs a walk (worth doing off the main thread).
	/// </summary>
	/// <param name="droppedPaths">Paths from the OS drop.</param>
	/// <returns>True when at least one path is an existing directory.</returns>
	public static bool ContainsFolder(IEnumerable<string> droppedPaths)
	{
		if (droppedPaths == null)
			return false;
		foreach (string raw in droppedPaths)
		{
			if (string.IsNullOrWhiteSpace(raw))
				continue;
			try
			{
				if (Directory.Exists(raw))
					return true;
			}
			catch (Exception)
			{
				// Unreadable path: treat as not a folder.
			}
		}

		return false;
	}

	/// <summary>
	/// True when <paramref name="path"/> is a supported media file (exists, matching extension).
	/// Directories always return false.
	/// </summary>
	/// <param name="path">Absolute or relative path.</param>
	/// <param name="mediaGlobs">Extension globs such as <c>*.wav</c>.</param>
	/// <returns>True when the path is an existing supported media file.</returns>
	public static bool IsSupportedMediaFile(string path, IEnumerable<string> mediaGlobs)
	{
		if (string.IsNullOrEmpty(path) || !File.Exists(path))
			return false;
		return IsSupportedFile(path, ToExtensionSet(mediaGlobs));
	}

	/// <summary>
	/// True when the path name starts with a dot or the Hidden file attribute is set.
	/// </summary>
	/// <param name="path">File or directory path.</param>
	/// <returns>True when the entry should be skipped as hidden.</returns>
	public static bool IsHiddenPath(string path)
	{
		return IsHiddenPath(path, null);
	}

	/// <summary>
	/// Same as <see cref="IsHiddenPath(string)"/>, using attributes the caller already has
	/// (from a directory listing) instead of reading them again.
	/// </summary>
	/// <param name="path">File or directory path.</param>
	/// <param name="knownAttributes">Attributes from the listing, or null to read them.</param>
	/// <returns>True when the entry should be skipped as hidden.</returns>
	public static bool IsHiddenPath(string path, FileAttributes? knownAttributes)
	{
		if (string.IsNullOrWhiteSpace(path))
			return true;

		string trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
		string name = Path.GetFileName(trimmed);
		if (string.IsNullOrEmpty(name))
			return false;
		if (name[0] == '.')
			return true;
		if (knownAttributes.HasValue)
			return (knownAttributes.Value & FileAttributes.Hidden) != 0;

		try
		{
			if ((File.GetAttributes(trimmed) & FileAttributes.Hidden) != 0)
				return true;
		}
		catch (Exception)
		{
			// Missing or inaccessible entries are treated as not hidden; callers still skip them.
		}

		return false;
	}

	/// <summary>
	/// File-system entry name with trailing separators stripped.
	/// </summary>
	/// <param name="path">File or directory path.</param>
	/// <returns>Last path segment, or empty when the path has no name.</returns>
	public static string GetEntryName(string path)
	{
		if (string.IsNullOrWhiteSpace(path))
			return string.Empty;
		string trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
		return Path.GetFileName(trimmed) ?? string.Empty;
	}

	/// <summary>Shared state for one walk.</summary>
	private sealed class Walk
	{
		private readonly HashSet<string> _seenFiles = new(PathComparer);
		private readonly HashSet<string> _seenDirs = new(PathComparer);
		private readonly DroppedMediaWalkProgress _progress;
		private readonly CancellationToken _cancel;

		public Walk(HashSet<string> extensions, int maxFiles, int maxDepth, DroppedMediaWalkProgress progress, CancellationToken cancel)
		{
			Extensions = extensions;
			MaxFiles = maxFiles;
			MaxDepth = maxDepth;
			_progress = progress;
			_cancel = cancel;
		}

		public HashSet<string> Extensions { get; }
		public int MaxFiles { get; }
		public int MaxDepth { get; }
		public List<string> Files { get; } = new();
		public int FolderCount { get; set; }
		public int UnreadableFolders { get; set; }
		public bool Truncated { get; set; }
		public bool Cancelled { get; private set; }

		/// <summary>True when the walk should stop (file cap reached or cancelled).</summary>
		public bool Stopped
		{
			get
			{
				if (!Cancelled && _cancel.IsCancellationRequested)
					Cancelled = true;
				return Cancelled || Files.Count >= MaxFiles;
			}
		}

		public bool TryEnterDirectory(string path) => _seenDirs.Add(NormalizeKey(path));

		/// <summary>Adds a media file once; false when already seen or the cap is reached.</summary>
		public bool TryAddFile(string path)
		{
			if (Files.Count >= MaxFiles)
			{
				Truncated = true;
				return false;
			}

			if (!_seenFiles.Add(NormalizeKey(path)))
				return false;
			Files.Add(path);
			_progress?.Report(Files.Count);
			return true;
		}
	}

	private static DroppedMediaNode ExpandDirectory(string dirPath, int depth, Walk walk)
	{
		if (walk.Stopped)
		{
			if (!walk.Cancelled)
				walk.Truncated = true;
			return null;
		}

		if (!walk.TryEnterDirectory(dirPath))
			return null;

		var childDirs = new List<FileSystemInfo>();
		var childFiles = new List<FileSystemInfo>();
		try
		{
			// One listing call gives names and attributes together (no extra stat per entry).
			foreach (FileSystemInfo entry in new DirectoryInfo(dirPath).EnumerateFileSystemInfos())
			{
				FileAttributes attrs;
				try
				{
					attrs = entry.Attributes;
				}
				catch (Exception)
				{
					continue;
				}

				if (IsHiddenPath(entry.FullName, attrs))
					continue;

				if ((attrs & FileAttributes.Directory) != 0)
				{
					// Folder links (symlinks, junctions) can point back up the tree: do not enter them.
					if ((attrs & FileAttributes.ReparsePoint) != 0)
						continue;
					childDirs.Add(entry);
				}
				else
				{
					childFiles.Add(entry);
				}
			}
		}
		catch (Exception)
		{
			walk.UnreadableFolders++;
			return null;
		}

		childDirs.Sort(CompareEntryNames);
		childFiles.Sort(CompareEntryNames);

		var children = new List<DroppedMediaNode>();
		foreach (FileSystemInfo childDir in childDirs)
		{
			if (walk.Stopped)
			{
				if (!walk.Cancelled)
					walk.Truncated = true;
				break;
			}

			if (IsFilesystemRoot(childDir.FullName))
				continue;
			if (depth + 1 > walk.MaxDepth)
			{
				walk.Truncated = true;
				continue;
			}

			var child = ExpandDirectory(childDir.FullName, depth + 1, walk);
			if (child != null)
				children.Add(child);
		}

		foreach (FileSystemInfo childFile in childFiles)
		{
			if (walk.Cancelled)
				break;
			string filePath = childFile.FullName;
			if (!IsSupportedFile(filePath, walk.Extensions))
				continue;
			if (!walk.TryAddFile(filePath))
			{
				if (walk.Files.Count >= walk.MaxFiles)
					break;
				continue;
			}

			children.Add(DroppedMediaNode.File(childFile.Name, filePath));
		}

		if (children.Count == 0)
			return null;

		walk.FolderCount++;
		return DroppedMediaNode.Directory(GetEntryName(dirPath), dirPath, children);
	}

	private static bool IsSupportedFile(string path, HashSet<string> extensions)
	{
		if (extensions == null || extensions.Count == 0)
			return false;
		string ext = Path.GetExtension(path);
		return !string.IsNullOrEmpty(ext) && extensions.Contains(ext);
	}

	internal static HashSet<string> ToExtensionSet(IEnumerable<string> mediaGlobs)
	{
		var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		if (mediaGlobs == null)
			return set;
		foreach (string glob in mediaGlobs)
		{
			if (string.IsNullOrWhiteSpace(glob))
				continue;
			string ext = glob.Trim();
			if (ext.StartsWith('*'))
				ext = ext.Substring(1);
			if (ext.Length == 0)
				continue;
			if (ext[0] != '.')
				ext = "." + ext;
			set.Add(ext);
		}
		return set;
	}

	private static string NormalizeKey(string path)
	{
		try
		{
			return Path.GetFullPath(path)
				.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
		}
		catch (Exception)
		{
			return path ?? string.Empty;
		}
	}

	private static bool IsFilesystemRoot(string path)
	{
		try
		{
			string full = Path.GetFullPath(path)
				.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
			string root = Path.GetPathRoot(full);
			if (string.IsNullOrEmpty(root))
				return false;
			string rootTrimmed = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
			if (string.IsNullOrEmpty(full) || string.IsNullOrEmpty(rootTrimmed))
			{
				// Unix root "/" trims to empty.
				return full.Length == 0 && root == Path.DirectorySeparatorChar.ToString();
			}
			return string.Equals(full, rootTrimmed, StringComparison.OrdinalIgnoreCase);
		}
		catch (Exception)
		{
			return false;
		}
	}

	private static int CompareEntryNames(FileSystemInfo a, FileSystemInfo b)
	{
		return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
	}

	/// <summary>
	/// Path comparer for duplicate checks: Windows and macOS folders ignore case by default, Linux
	/// does not (<c>Clip.wav</c> and <c>clip.wav</c> are two files there).
	/// </summary>
	public static StringComparer PathComparer { get; } =
		OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
			? StringComparer.OrdinalIgnoreCase
			: StringComparer.Ordinal;
}

/// <summary>
/// Kind of media a file is, by extension.
/// </summary>
public enum MediaFileKind
{
	/// <summary>Not a supported media file.</summary>
	None = 0,

	/// <summary>Audio file (audio component).</summary>
	Audio = 1,

	/// <summary>Moving video (video component).</summary>
	Video = 2,

	/// <summary>Still image (video component shown as an image).</summary>
	Image = 3
}

/// <summary>
/// One lookup table from file extension to <see cref="MediaFileKind"/>, built once from the
/// audio, video and image globs. The single place that decides what a dropped file is.
/// </summary>
public sealed class MediaExtensionMap
{
	private readonly Dictionary<string, MediaFileKind> _kinds;

	private MediaExtensionMap(Dictionary<string, MediaFileKind> kinds, string[] globs)
	{
		_kinds = kinds;
		Globs = globs;
	}

	/// <summary>Every glob in the map (audio, then video, then image), for walks and dialogs.</summary>
	/// <value>Never null.</value>
	public IReadOnlyList<string> Globs { get; }

	/// <summary>
	/// Builds the map. When an extension is listed twice, audio wins over video, and video over
	/// image (the same order drop import has always used).
	/// </summary>
	/// <param name="audioGlobs">Audio globs such as <c>*.wav</c>.</param>
	/// <param name="videoGlobs">Video globs such as <c>*.mp4</c>.</param>
	/// <param name="imageGlobs">Image globs such as <c>*.png</c>.</param>
	/// <returns>New map; never null.</returns>
	public static MediaExtensionMap Create(
		IEnumerable<string> audioGlobs,
		IEnumerable<string> videoGlobs,
		IEnumerable<string> imageGlobs)
	{
		var kinds = new Dictionary<string, MediaFileKind>(StringComparer.OrdinalIgnoreCase);
		var globs = new List<string>();
		Add(kinds, globs, audioGlobs, MediaFileKind.Audio);
		Add(kinds, globs, videoGlobs, MediaFileKind.Video);
		Add(kinds, globs, imageGlobs, MediaFileKind.Image);
		return new MediaExtensionMap(kinds, globs.ToArray());
	}

	/// <summary>Kind of media <paramref name="path"/> is, by extension only (the file is not opened).</summary>
	/// <param name="path">File path or name.</param>
	/// <returns><see cref="MediaFileKind.None"/> when the extension is not supported.</returns>
	public MediaFileKind Classify(string path)
	{
		if (string.IsNullOrEmpty(path))
			return MediaFileKind.None;
		string ext = Path.GetExtension(path);
		if (string.IsNullOrEmpty(ext))
			return MediaFileKind.None;
		return _kinds.TryGetValue(ext, out var kind) ? kind : MediaFileKind.None;
	}

	/// <summary>True when <paramref name="path"/> has a supported media extension.</summary>
	/// <param name="path">File path or name.</param>
	/// <returns>True for audio, video and image extensions.</returns>
	public bool IsSupported(string path) => Classify(path) != MediaFileKind.None;

	private static void Add(
		Dictionary<string, MediaFileKind> kinds,
		List<string> globs,
		IEnumerable<string> source,
		MediaFileKind kind)
	{
		if (source == null)
			return;
		foreach (string glob in source)
		{
			if (string.IsNullOrWhiteSpace(glob))
				continue;
			globs.Add(glob);
			foreach (string ext in DroppedMediaExpansion.ToExtensionSet(new[] { glob }))
				kinds.TryAdd(ext, kind);
		}
	}
}

/// <summary>
/// Result of expanding a file drop into media files and an optional folder tree.
/// </summary>
public sealed class DroppedMediaResult
{
	/// <summary>Empty expansion (no files, no tree).</summary>
	public static readonly DroppedMediaResult Empty = new(
		Array.Empty<string>(),
		Array.Empty<DroppedMediaNode>(),
		false,
		0);

	/// <summary>
	/// Creates an expansion result.
	/// </summary>
	/// <param name="files">Flat list of supported media paths in tree-walk order.</param>
	/// <param name="roots">Top-level tree nodes (dropped files and folders).</param>
	/// <param name="hasFolderTree">True when at least one dropped directory contributed media.</param>
	/// <param name="folderCount">Number of folder nodes in the tree (including dropped folders).</param>
	public DroppedMediaResult(
		IReadOnlyList<string> files,
		IReadOnlyList<DroppedMediaNode> roots,
		bool hasFolderTree,
		int folderCount)
	{
		Files = files ?? Array.Empty<string>();
		Roots = roots ?? Array.Empty<DroppedMediaNode>();
		HasFolderTree = hasFolderTree;
		FolderCount = folderCount < 0 ? 0 : folderCount;
	}

	/// <summary>Supported media files in tree-walk order.</summary>
	/// <value>Never null.</value>
	public IReadOnlyList<string> Files { get; }

	/// <summary>Top-level nodes matching the drop (files and folders).</summary>
	/// <value>Never null.</value>
	public IReadOnlyList<DroppedMediaNode> Roots { get; }

	/// <summary>True when a dropped directory contributed at least one media file.</summary>
	/// <value>False for a drop of loose files only.</value>
	public bool HasFolderTree { get; }

	/// <summary>Number of folder nodes that will become group cues when the tree is preserved.</summary>
	/// <value>Zero when <see cref="HasFolderTree"/> is false.</value>
	public int FolderCount { get; }

	/// <summary>Number of media files after expansion.</summary>
	/// <value>Same as <see cref="Files"/> count.</value>
	public int FileCount => Files.Count;

	/// <summary>True when the walk stopped at the file or depth limit, so some media was left out.</summary>
	/// <value>False for a complete walk.</value>
	public bool Truncated { get; init; }

	/// <summary>True when the walk was cancelled before it finished.</summary>
	/// <value>False for a finished walk.</value>
	public bool Cancelled { get; init; }

	/// <summary>Folders that could not be read (their media is missing from the result).</summary>
	/// <value>Zero when every folder was read.</value>
	public int UnreadableFolderCount { get; init; }

	/// <summary>File limit the walk used; meaningful when <see cref="Truncated"/> is true.</summary>
	/// <value>Zero when not set.</value>
	public int MaxFiles { get; init; }

	/// <summary>True when the popup should warn that the import is large.</summary>
	/// <value>True when <see cref="FileCount"/> is at least <see cref="DroppedMediaExpansion.LargeImportFileThreshold"/>.</value>
	public bool IsLargeImport => FileCount >= DroppedMediaExpansion.LargeImportFileThreshold;
}

/// <summary>
/// Live count of media files found by a running walk. Written by the walk thread, read by the UI.
/// </summary>
public sealed class DroppedMediaWalkProgress
{
	private int _filesFound;

	/// <summary>Media files found so far.</summary>
	/// <value>Zero before the walk starts.</value>
	public int FilesFound => Volatile.Read(ref _filesFound);

	/// <summary>Stores the current count (walk thread).</summary>
	/// <param name="filesFound">Media files found so far.</param>
	public void Report(int filesFound) => Volatile.Write(ref _filesFound, filesFound);
}

/// <summary>
/// One file or folder in an expanded drop tree.
/// </summary>
public sealed class DroppedMediaNode
{
	/// <summary>
	/// Creates a folder node with child files and/or folders.
	/// </summary>
	/// <param name="name">Folder name used as the group cue name.</param>
	/// <param name="fullPath">Absolute directory path.</param>
	/// <param name="children">Child nodes (already filtered).</param>
	/// <returns>Directory node.</returns>
	public static DroppedMediaNode Directory(string name, string fullPath, IReadOnlyList<DroppedMediaNode> children)
	{
		return new DroppedMediaNode(name, fullPath, true, children ?? Array.Empty<DroppedMediaNode>());
	}

	/// <summary>
	/// Creates a media file node.
	/// </summary>
	/// <param name="name">File name including extension.</param>
	/// <param name="fullPath">Absolute file path.</param>
	/// <returns>File node.</returns>
	public static DroppedMediaNode File(string name, string fullPath)
	{
		return new DroppedMediaNode(name, fullPath, false, Array.Empty<DroppedMediaNode>());
	}

	private DroppedMediaNode(string name, string fullPath, bool isDirectory, IReadOnlyList<DroppedMediaNode> children)
	{
		Name = name ?? string.Empty;
		FullPath = fullPath ?? string.Empty;
		IsDirectory = isDirectory;
		Children = children ?? Array.Empty<DroppedMediaNode>();
	}

	/// <summary>Folder name or file name (with extension for files).</summary>
	/// <value>Last path segment.</value>
	public string Name { get; }

	/// <summary>Absolute path of this file or folder.</summary>
	/// <value>Full path string.</value>
	public string FullPath { get; }

	/// <summary>True when this node is a folder that should become a group cue.</summary>
	/// <value>False for media files.</value>
	public bool IsDirectory { get; }

	/// <summary>Child folders and files. Empty for file nodes.</summary>
	/// <value>Never null.</value>
	public IReadOnlyList<DroppedMediaNode> Children { get; }
}
