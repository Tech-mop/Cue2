// SPDX-FileCopyrightText: 2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using NUnit.Framework;
using Cue2.Services;

namespace Cue2.Tests.Updates;

/// <summary>
/// Extract marker, install layout detection and path containment used when applying an update.
/// </summary>
[TestFixture]
public class UpdateApplyHelperTests
{
    private string _root;

    /// <summary>Fresh temp folder per test.</summary>
    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "cue2-tests-update-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    /// <summary>Removes the temp folder.</summary>
    [TearDown]
    public void TearDown()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Best effort.
        }
    }

    private string HostName => RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "Cue2.exe" : "Cue2.x86_64";

    private string MakeZip(params string[] entries)
    {
        string zipPath = Path.Combine(_root, "update.zip");
        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        foreach (string entry in entries)
        {
            using var writer = new StreamWriter(zip.CreateEntry(entry).Open());
            writer.Write("x");
        }
        return zipPath;
    }

    /// <summary>A finished extract carries the archive hash; a different hash means incomplete.</summary>
    [Test]
    public void ExtractWritesMarkerThatMatchesOnlyThatArchive()
    {
        string zip = MakeZip($"Cue2/{HostName}", "Cue2/Cue2.pck");
        string dest = Path.Combine(_root, "extract");
        UpdateApplyHelper.ExtractArchive(zip, dest, "ABCDEF");

        Assert.That(UpdateApplyHelper.IsExtractComplete(dest, "abcdef"), Is.True, "hash compare ignores case");
        Assert.That(UpdateApplyHelper.IsExtractComplete(dest, "123456"), Is.False);
        Assert.That(UpdateApplyHelper.IsExtractComplete(dest, ""), Is.False);
        Assert.That(File.Exists(Path.Combine(dest, "Cue2", HostName)), Is.True);
        Assert.That(Directory.Exists(dest + ".tmp"), Is.False, "staging folder is moved, not left behind");
    }

    /// <summary>A folder without a marker (cut off by a quit or crash) is never complete.</summary>
    [Test]
    public void ExtractWithoutMarkerIsIncomplete()
    {
        string dest = Path.Combine(_root, "half");
        Directory.CreateDirectory(dest);
        File.WriteAllText(Path.Combine(dest, HostName), "x");
        Assert.That(UpdateApplyHelper.IsExtractComplete(dest, "abc"), Is.False);
        Assert.That(UpdateApplyHelper.IsExtractComplete(Path.Combine(_root, "missing"), "abc"), Is.False);
    }

    /// <summary>Deleting an extract removes the folder, the staging folder and the marker.</summary>
    [Test]
    public void DeleteExtractRemovesEverything()
    {
        string zip = MakeZip(HostName);
        string dest = Path.Combine(_root, "extract");
        UpdateApplyHelper.ExtractArchive(zip, dest, "aa");
        Directory.CreateDirectory(dest + ".tmp");

        UpdateApplyHelper.DeleteExtract(dest);

        Assert.That(Directory.Exists(dest), Is.False);
        Assert.That(Directory.Exists(dest + ".tmp"), Is.False);
        Assert.That(File.Exists(dest + ".ok"), Is.False);
    }

    /// <summary>The host binary is found at the top level or inside a single wrapping folder.</summary>
    [Test]
    public void FindsHostAtTopOrInSingleFolder()
    {
        string flat = Path.Combine(_root, "flat");
        Directory.CreateDirectory(flat);
        File.WriteAllText(Path.Combine(flat, HostName), "x");
        var layout = UpdateApplyHelper.FindLayout(flat);
        Assert.That(layout, Is.Not.Null);
        Assert.That(layout.PayloadRoot, Is.EqualTo(flat));
        Assert.That(layout.RelativeRelaunch, Is.EqualTo(HostName));
        Assert.That(layout.IsMacApp, Is.False);

        string wrapped = Path.Combine(_root, "wrapped");
        string inner = Path.Combine(wrapped, "Cue2-1.0.0");
        Directory.CreateDirectory(inner);
        File.WriteAllText(Path.Combine(inner, HostName), "x");
        Assert.That(UpdateApplyHelper.FindLayout(wrapped)?.PayloadRoot, Is.EqualTo(inner));
    }

    /// <summary>A macOS app bundle is the payload itself.</summary>
    [Test]
    public void FindsMacAppBundle()
    {
        string app = Path.Combine(_root, "Cue2.app");
        Directory.CreateDirectory(Path.Combine(app, "Contents"));
        var layout = UpdateApplyHelper.FindLayout(_root);
        Assert.That(layout?.IsMacApp, Is.True);
        Assert.That(layout.PayloadRoot, Is.EqualTo(app));
    }

    /// <summary>Folders without a Cue2 host, or with several candidates at the top, give no layout.</summary>
    [Test]
    public void UnknownLayoutGivesNull()
    {
        string other = Path.Combine(_root, "other");
        Directory.CreateDirectory(Path.Combine(other, "a"));
        Directory.CreateDirectory(Path.Combine(other, "b"));
        File.WriteAllText(Path.Combine(other, "readme.txt"), "x");
        Assert.That(UpdateApplyHelper.FindLayout(other), Is.Null);
        Assert.That(UpdateApplyHelper.FindLayout(Path.Combine(_root, "nope")), Is.Null);
        Assert.That(UpdateApplyHelper.FindLayout(null), Is.Null);
    }

    /// <summary>Path containment: the root itself and children are inside; parents, siblings and escapes are not.</summary>
    [Test]
    public void IsUnderDirectoryHandlesEdges()
    {
        string root = Path.Combine(_root, "install");
        string sep = Path.DirectorySeparatorChar.ToString();
        Assert.That(UpdateApplyHelper.IsUnderDirectory(root, root), Is.True);
        Assert.That(UpdateApplyHelper.IsUnderDirectory(root + sep, root), Is.True);
        Assert.That(UpdateApplyHelper.IsUnderDirectory(Path.Combine(root, "a", "b.txt"), root), Is.True);
        Assert.That(UpdateApplyHelper.IsUnderDirectory(Path.Combine(root, "a", "..", "b"), root), Is.True);
        Assert.That(UpdateApplyHelper.IsUnderDirectory(Path.Combine(root, "..", "x"), root), Is.False);
        Assert.That(UpdateApplyHelper.IsUnderDirectory(root + "-old" + sep + "x", root), Is.False, "sibling sharing a name prefix");
        Assert.That(UpdateApplyHelper.IsUnderDirectory(_root, root), Is.False);
        Assert.That(UpdateApplyHelper.IsUnderDirectory("", root), Is.False);
        Assert.That(UpdateApplyHelper.IsUnderDirectory(root, null), Is.False);
    }
}
