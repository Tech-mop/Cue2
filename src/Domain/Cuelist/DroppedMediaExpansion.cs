// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.IO;

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

	/// <summary>
	/// Walks <paramref name="droppedPaths"/> and returns supported media files plus a folder tree.
	/// </summary>
	/// <param name="droppedPaths">Paths from the OS drop (files and/or directories).</param>
	/// <param name="mediaGlobs">Extension globs such as <c>*.wav</c> / <c>*.mp4</c>.</param>
	/// <returns>Expanded result; never null. Empty when nothing supported remains.</returns>
	public static DroppedMediaResult Expand(IEnumerable<string> droppedPaths, IEnumerable<string> mediaGlobs)
	{
		var extensions = ToExtensionSet(mediaGlobs);
		var files = new List<string>();
		var roots = new List<DroppedMediaNode>();
		var seenFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		var seenDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		bool hasFolderTree = false;
		int folderCount = 0;

		if (droppedPaths == null)
			return DroppedMediaResult.Empty;

		foreach (string raw in droppedPaths)
		{
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

				var node = ExpandDirectory(path, extensions, files, seenFiles, seenDirs, ref folderCount);
				if (node == null)
					continue;
				hasFolderTree = true;
				roots.Add(node);
			}
			else if (File.Exists(path) && IsSupportedFile(path, extensions) && seenFiles.Add(NormalizeKey(path)))
			{
				files.Add(path);
				roots.Add(DroppedMediaNode.File(GetEntryName(path), path));
			}
		}

		return new DroppedMediaResult(files, roots, hasFolderTree, folderCount);
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
		if (string.IsNullOrWhiteSpace(path))
			return true;

		string trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
		string name = Path.GetFileName(trimmed);
		if (string.IsNullOrEmpty(name))
			return false;
		if (name[0] == '.')
			return true;

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

	private static DroppedMediaNode ExpandDirectory(
		string dirPath,
		HashSet<string> extensions,
		List<string> files,
		HashSet<string> seenFiles,
		HashSet<string> seenDirs,
		ref int folderCount)
	{
		string key = NormalizeKey(dirPath);
		if (!seenDirs.Add(key))
			return null;

		string[] entries;
		try
		{
			entries = Directory.GetFileSystemEntries(dirPath);
		}
		catch (Exception)
		{
			return null;
		}

		var childDirs = new List<string>();
		var childFiles = new List<string>();
		foreach (string entry in entries)
		{
			if (IsHiddenPath(entry))
				continue;

			try
			{
				var attrs = File.GetAttributes(entry);
				if ((attrs & FileAttributes.Directory) != 0)
					childDirs.Add(entry);
				else
					childFiles.Add(entry);
			}
			catch (Exception)
			{
				// Skip entries we cannot stat.
			}
		}

		childDirs.Sort(CompareEntryNames);
		childFiles.Sort(CompareEntryNames);

		var children = new List<DroppedMediaNode>();
		foreach (string childDir in childDirs)
		{
			if (IsFilesystemRoot(childDir))
				continue;
			var child = ExpandDirectory(childDir, extensions, files, seenFiles, seenDirs, ref folderCount);
			if (child != null)
				children.Add(child);
		}

		foreach (string childFile in childFiles)
		{
			if (!IsSupportedFile(childFile, extensions))
				continue;
			if (!seenFiles.Add(NormalizeKey(childFile)))
				continue;
			files.Add(childFile);
			children.Add(DroppedMediaNode.File(GetEntryName(childFile), childFile));
		}

		if (children.Count == 0)
			return null;

		folderCount++;
		return DroppedMediaNode.Directory(GetEntryName(dirPath), dirPath, children);
	}

	private static bool IsSupportedFile(string path, HashSet<string> extensions)
	{
		if (extensions == null || extensions.Count == 0)
			return false;
		string ext = Path.GetExtension(path);
		return !string.IsNullOrEmpty(ext) && extensions.Contains(ext);
	}

	private static HashSet<string> ToExtensionSet(IEnumerable<string> mediaGlobs)
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

	private static int CompareEntryNames(string a, string b)
	{
		return string.Compare(GetEntryName(a), GetEntryName(b), StringComparison.OrdinalIgnoreCase);
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

	/// <summary>True when the popup should warn that the import is large.</summary>
	/// <value>True when <see cref="FileCount"/> is at least <see cref="DroppedMediaExpansion.LargeImportFileThreshold"/>.</value>
	public bool IsLargeImport => FileCount >= DroppedMediaExpansion.LargeImportFileThreshold;
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
