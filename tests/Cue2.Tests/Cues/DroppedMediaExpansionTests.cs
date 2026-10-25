// SPDX-FileCopyrightText: 2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System;
using System.IO;
using System.Linq;
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
        Assert.That(result.FolderCount, Is.EqualTo(4), "Show, Act1, Nested, Act2 — empty folder omitted");
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

    private static void Touch(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, Array.Empty<byte>());
    }
}
