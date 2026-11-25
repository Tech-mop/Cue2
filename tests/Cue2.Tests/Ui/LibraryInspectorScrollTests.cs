// SPDX-FileCopyrightText: 2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System;
using System.IO;
using NUnit.Framework;

namespace Cue2.Tests.Ui;

/// <summary>
/// Library Inspector scene scrolls both axes when the panel is smaller than its chrome.
/// </summary>
[TestFixture]
public class LibraryInspectorScrollTests
{
    /// <summary>
    /// Horizontal scrolling is enabled and content sits in a padded scroll child so a
    /// narrow inspector can still reach the toolbar.
    /// </summary>
    [Test]
    public void LibraryContentScrollsOnBothAxes()
    {
        string tscn = File.ReadAllText(FindScene());
        Assert.That(tscn, Does.Contain("name=\"ContentScroll\" type=\"ScrollContainer\" parent=\"RootMargin\""));
        Assert.That(tscn, Does.Contain("name=\"ContentMargin\" type=\"MarginContainer\" parent=\"RootMargin/ContentScroll\""));
        Assert.That(tscn, Does.Contain("name=\"VBox\" type=\"VBoxContainer\" parent=\"RootMargin/ContentScroll/ContentMargin\""));
        Assert.That(tscn, Does.Not.Contain("horizontal_scroll_mode = 0"));
        Assert.That(tscn, Does.Contain("theme_override_constants/margin_bottom = 16"));
    }

    private static string FindScene()
    {
        const string relative = "src/UI/Inspectors/LibraryInspector.tscn";
        string dir = AppContext.BaseDirectory;
        for (int i = 0; i < 10; i++)
        {
            string candidate = Path.Combine(dir, relative);
            if (File.Exists(candidate))
                return candidate;
            if (File.Exists(Path.Combine(dir, "Cue2.csproj")))
            {
                candidate = Path.Combine(dir, relative);
                Assert.That(File.Exists(candidate), Is.True, $"Missing {relative} next to Cue2.csproj");
                return candidate;
            }

            string parent = Path.GetDirectoryName(dir);
            if (string.IsNullOrEmpty(parent) || parent == dir)
                break;
            dir = parent;
        }

        Assert.Fail($"Could not find {relative} from {AppContext.BaseDirectory}");
        return null;
    }
}
