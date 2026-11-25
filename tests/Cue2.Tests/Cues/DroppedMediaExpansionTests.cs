// SPDX-FileCopyrightText: 2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System;
using System.IO;
using System.Linq;
using System.Threading;
using Cue2.Domain.Cuelist;
using NUnit.Framework;

namespace Cue2.Tests.Cues;

/// <summary>
/// Folder drops expand into supported media, skip hidden files, and keep folder names for groups.
/// </summary>
[TestFixture]
public class DroppedMediaExpansionTests
{
    private string _root;
    private static readonly string[] WavGlobs = { "*.wav", "*.mp3", "*.png" };

    /// <summary>Fresh temp folder per test.</summary>
    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "cue2-drop-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    /// <summary>Removes the temp folder.</summary>
    [TearDown]
    public void TearDown()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, true);
        }
        catch (IOException)
        {
            // The OS temp cleaner gets leftovers if a handle is still open.
        }
    }

    /// <summary>A dropped folder collects nested media and uses folder names as tree node names.</summary>
    [Test]
    public void ExpandWalksNestedFoldersAndKeepsFolderNames()
    {
        string show = Path.Combine(_root, "Show");
        string act1 = Path.Combine(show, "Act1");
        string nested = Path.Combine(act1, "Nested");
        string act2 = Path.Combine(show, "Act2");
        Directory.CreateDirectory(nested);
        Directory.CreateDirectory(act2);
        Directory.CreateDirectory(Path.Combine(show, "Empty"));
        Touch(Path.Combine(act1, "a.wav"));
        Touch(Path.Combine(nested, "b.wav"));
        Touch(Path.Combine(act2, "c.wav"));
        Touch(Path.Combine(show, "notes.txt"));

        var result = DroppedMediaExpansion.Expand(new[] { show }, WavGlobs);

        Assert.That(result.HasFolderTree, Is.True);
        Assert.That(result.FileCount, Is.EqualTo(3));
        Assert.That(result.FolderCount, Is.EqualTo(4), "Show, Act1, Nested, Act2; empty folder omitted");
        Assert.That(result.Roots.Count, Is.EqualTo(1));
        Assert.That(result.Roots[0].Name, Is.EqualTo("Show"));
        Assert.That(result.Roots[0].IsDirectory, Is.True);

        var showChildren = result.Roots[0].Children;
        Assert.That(showChildren.Select(n => n.Name), Is.EqualTo(new[] { "Act1", "Act2" }));

        var act1Node = showChildren.First(n => n.Name == "Act1");
        Assert.That(act1Node.Children.Select(n => n.Name), Is.EqualTo(new[] { "Nested", "a.wav" }));
        Assert.That(act1Node.Children.First(n => n.Name == "Nested").Children.Select(n => n.Name),
            Is.EqualTo(new[] { "b.wav" }));
    }

    /// <summary>Dotfiles, AppleDouble files, and the Hidden attribute are skipped.</summary>
    [Test]
    public void ExpandSkipsHiddenFilesAndFolders()
    {
        string folder = Path.Combine(_root, "Media");
        Directory.CreateDirectory(folder);
        Directory.CreateDirectory(Path.Combine(folder, ".secretDir"));
        Touch(Path.Combine(folder, "keep.wav"));
        Touch(Path.Combine(folder, ".hidden.wav"));
        Touch(Path.Combine(folder, "._keep.wav"));
        Touch(Path.Combine(folder, ".secretDir", "buried.wav"));

        string attributed = Path.Combine(folder, "attributed.wav");
        Touch(attributed);
        File.SetAttributes(attributed, File.GetAttributes(attributed) | FileAttributes.Hidden);
        bool hiddenAttributePersists = (File.GetAttributes(attributed) & FileAttributes.Hidden) != 0;

        var result = DroppedMediaExpansion.Expand(new[] { folder }, WavGlobs);
        var names = result.Files.Select(Path.GetFileName).ToList();

        Assert.That(names, Does.Contain("keep.wav"));
        Assert.That(names, Does.Not.Contain(".hidden.wav"));
        Assert.That(names, Does.Not.Contain("._keep.wav"));
        Assert.That(names, Does.Not.Contain("buried.wav"));
        if (hiddenAttributePersists)
            Assert.That(names, Does.Not.Contain("attributed.wav"));
        Assert.That(result.FolderCount, Is.EqualTo(1));
    }

    /// <summary>A drop of only files is not a folder tree.</summary>
    [Test]
    public void ExpandLooseFilesDoesNotMarkFolderTree()
    {
        string a = Path.Combine(_root, "a.wav");
        string b = Path.Combine(_root, "b.wav");
        Touch(a);
        Touch(b);

        var result = DroppedMediaExpansion.Expand(new[] { b, a }, WavGlobs);

        Assert.That(result.HasFolderTree, Is.False);
        Assert.That(result.FolderCount, Is.EqualTo(0));
        Assert.That(result.FileCount, Is.EqualTo(2));
        Assert.That(result.Roots.Select(n => n.Name), Is.EqualTo(new[] { "b.wav", "a.wav" }),
            "top-level drop order is kept");
    }

    /// <summary>The same file listed twice, or as a file and inside a dropped folder, is kept once.</summary>
    [Test]
    public void ExpandDeduplicatesTheSameFile()
    {
        string folder = Path.Combine(_root, "Takes");
        Directory.CreateDirectory(folder);
        string wav = Path.Combine(folder, "one.wav");
        Touch(wav);

        var result = DroppedMediaExpansion.Expand(new[] { folder, wav, wav }, WavGlobs);

        Assert.That(result.FileCount, Is.EqualTo(1));
        Assert.That(result.HasFolderTree, Is.True);
    }

    /// <summary>Fifty or more media files flag a large import for the drop popup warning.</summary>
    [Test]
    public void ExpandMarksLargeImportAtThreshold()
    {
        string folder = Path.Combine(_root, "Big");
        Directory.CreateDirectory(folder);
        for (int i = 0; i < DroppedMediaExpansion.LargeImportFileThreshold; i++)
            Touch(Path.Combine(folder, $"f{i:000}.wav"));

        var result = DroppedMediaExpansion.Expand(new[] { folder }, WavGlobs);

        Assert.That(result.FileCount, Is.EqualTo(DroppedMediaExpansion.LargeImportFileThreshold));
        Assert.That(result.IsLargeImport, Is.True);
        Assert.That(DroppedMediaResult.Empty.IsLargeImport, Is.False);
    }

    /// <summary>Hidden names are detected from the path even when the file is missing.</summary>
    [Test]
    public void HiddenPathDetectsDotPrefix()
    {
        Assert.That(DroppedMediaExpansion.IsHiddenPath(".hidden.wav"), Is.True);
        Assert.That(DroppedMediaExpansion.IsHiddenPath("visible.wav"), Is.False);
        Assert.That(DroppedMediaExpansion.GetEntryName(Path.Combine("Show", "Act1") + Path.DirectorySeparatorChar),
            Is.EqualTo("Act1"));
    }

    /// <summary>A folder link back to a parent is not entered, so each file is found once.</summary>
    [Test]
    public void ExpandDoesNotFollowFolderLinks()
    {
        string show = Path.Combine(_root, "Show");
        string act = Path.Combine(show, "Act1");
        Touch(Path.Combine(act, "a.wav"));
        string link = Path.Combine(act, "loop");
        try
        {
            Directory.CreateSymbolicLink(link, show);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            Assert.Ignore("Cannot create a folder link here: " + ex.Message);
        }

        var result = DroppedMediaExpansion.Expand(new[] { show }, WavGlobs);

        Assert.That(result.FileCount, Is.EqualTo(1));
        Assert.That(result.FolderCount, Is.EqualTo(2), "Show and Act1 only, not the link");
        Assert.That(result.Truncated, Is.False);
    }

    /// <summary>The walk stops at the file limit and says it left files out.</summary>
    [Test]
    public void ExpandStopsAtFileLimit()
    {
        string folder = Path.Combine(_root, "Many");
        for (int i = 0; i < 10; i++)
            Touch(Path.Combine(folder, $"f{i:00}.wav"));

        var result = DroppedMediaExpansion.Expand(new[] { folder }, WavGlobs, 4, 32, null, CancellationToken.None);

        Assert.That(result.FileCount, Is.EqualTo(4));
        Assert.That(result.Truncated, Is.True);
        Assert.That(result.Files.Select(Path.GetFileName), Is.EqualTo(new[] { "f00.wav", "f01.wav", "f02.wav", "f03.wav" }));
    }

    /// <summary>Folders below the depth limit are left out and reported.</summary>
    [Test]
    public void ExpandStopsAtDepthLimit()
    {
        string top = Path.Combine(_root, "Top");
        Touch(Path.Combine(top, "0.wav"));
        Touch(Path.Combine(top, "L1", "1.wav"));
        Touch(Path.Combine(top, "L1", "L2", "2.wav"));

        var result = DroppedMediaExpansion.Expand(new[] { top }, WavGlobs, 100, 1, null, CancellationToken.None);

        Assert.That(result.Files.Select(Path.GetFileName), Is.EquivalentTo(new[] { "0.wav", "1.wav" }));
        Assert.That(result.Truncated, Is.True);
    }

    /// <summary>A complete walk is not marked as cut short, and progress reports the count.</summary>
    [Test]
    public void ExpandReportsProgressAndNoTruncation()
    {
        string folder = Path.Combine(_root, "Few");
        Touch(Path.Combine(folder, "a.wav"));
        Touch(Path.Combine(folder, "b.wav"));
        var progress = new DroppedMediaWalkProgress();

        var result = DroppedMediaExpansion.Expand(new[] { folder }, WavGlobs, 100, 32, progress, CancellationToken.None);

        Assert.That(result.Truncated, Is.False);
        Assert.That(result.Cancelled, Is.False);
        Assert.That(progress.FilesFound, Is.EqualTo(2));
    }

    /// <summary>A cancelled walk stops and says so.</summary>
    [Test]
    public void ExpandHonoursCancellation()
    {
        string folder = Path.Combine(_root, "Cancel");
        Touch(Path.Combine(folder, "a.wav"));
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();

        var result = DroppedMediaExpansion.Expand(new[] { folder }, WavGlobs, 100, 32, null, cancel.Token);

        Assert.That(result.Cancelled, Is.True);
        Assert.That(result.FileCount, Is.EqualTo(0));
    }

    /// <summary>An unreadable sub-folder is counted so the drop can say media was skipped.</summary>
    [Test]
    public void ExpandCountsUnreadableFolders()
    {
        if (OperatingSystem.IsWindows())
            Assert.Ignore("Folder permissions are set differently on Windows.");
        string top = Path.Combine(_root, "Top");
        string locked = Path.Combine(top, "Locked");
        Touch(Path.Combine(top, "a.wav"));
        Touch(Path.Combine(locked, "b.wav"));
        File.SetUnixFileMode(locked, UnixFileMode.None);
        try
        {
            if (Directory.GetFileSystemEntries(locked).Length > 0)
                Assert.Ignore("Running with rights that ignore folder permissions.");
        }
        catch (UnauthorizedAccessException)
        {
            // Expected: the folder cannot be listed.
        }

        try
        {
            var result = DroppedMediaExpansion.Expand(new[] { top }, WavGlobs);
            Assert.That(result.FileCount, Is.EqualTo(1));
            Assert.That(result.UnreadableFolderCount, Is.EqualTo(1));
        }
        finally
        {
            File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    /// <summary>The cheap checks look at the dropped paths only.</summary>
    [Test]
    public void ShallowChecksSeeFoldersAndMediaFiles()
    {
        string folder = Path.Combine(_root, "Empty");
        Directory.CreateDirectory(folder);
        string wav = Path.Combine(_root, "a.wav");
        string txt = Path.Combine(_root, "notes.txt");
        Touch(wav);
        Touch(txt);

        Assert.That(DroppedMediaExpansion.ContainsFolder(new[] { wav, folder }), Is.True);
        Assert.That(DroppedMediaExpansion.ContainsFolder(new[] { wav, txt }), Is.False);
        Assert.That(DroppedMediaExpansion.MayContainMedia(new[] { txt, wav }, WavGlobs), Is.True);
        Assert.That(DroppedMediaExpansion.MayContainMedia(new[] { folder }, WavGlobs), Is.True);
        Assert.That(DroppedMediaExpansion.MayContainMedia(new[] { txt }, WavGlobs), Is.False);
    }

    /// <summary>On Linux two files whose names differ only by case are both kept.</summary>
    [Test]
    public void ExpandKeepsCaseDistinctFilesOnLinux()
    {
        if (!OperatingSystem.IsLinux())
            Assert.Ignore("Windows and macOS folders ignore case by default.");
        string folder = Path.Combine(_root, "Case");
        Touch(Path.Combine(folder, "Clip.wav"));
        Touch(Path.Combine(folder, "clip.wav"));

        var result = DroppedMediaExpansion.Expand(new[] { folder }, WavGlobs);

        Assert.That(result.FileCount, Is.EqualTo(2));
    }

    /// <summary>One map decides audio, video and image by extension, ignoring case.</summary>
    [Test]
    public void ExtensionMapClassifiesByKind()
    {
        var map = MediaExtensionMap.Create(new[] { "*.wav", "*.mp3" }, new[] { "*.mp4" }, new[] { "*.png", "*.wav" });

        Assert.That(map.Classify("/a/b/Song.WAV"), Is.EqualTo(MediaFileKind.Audio), "audio wins over a duplicate image glob");
        Assert.That(map.Classify("clip.mp4"), Is.EqualTo(MediaFileKind.Video));
        Assert.That(map.Classify("still.png"), Is.EqualTo(MediaFileKind.Image));
        Assert.That(map.Classify("notes.txt"), Is.EqualTo(MediaFileKind.None));
        Assert.That(map.Classify("noext"), Is.EqualTo(MediaFileKind.None));
        Assert.That(map.IsSupported("x.mp3"), Is.True);
        Assert.That(map.Globs, Is.EqualTo(new[] { "*.wav", "*.mp3", "*.mp4", "*.png", "*.wav" }));
    }

    private static void Touch(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, Array.Empty<byte>());
    }
}
