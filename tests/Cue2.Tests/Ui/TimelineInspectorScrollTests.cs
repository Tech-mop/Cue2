// SPDX-FileCopyrightText: 2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System;
using System.IO;
using NUnit.Framework;

namespace Cue2.Tests.Ui;

/// <summary>
/// Timeline Inspector scene keeps its chrome inside an outer ScrollContainer.
/// </summary>
[TestFixture]
public class TimelineInspectorScrollTests
{
    /// <summary>
    /// The timeline canvas lives under OuterScroll so a short or narrow inspector can still
    /// reach the toolbar and tracks.
    /// </summary>
    [Test]
    public void TimelineCanvasIsWrappedInOuterScroll()
    {
        string tscn = File.ReadAllText(FindScene());
        Assert.That(tscn, Does.Contain("[node name=\"OuterScroll\" type=\"ScrollContainer\" parent=\".\"]"));
        Assert.That(tscn, Does.Contain("[node name=\"TimelineContainer\" type=\"MarginContainer\" parent=\"OuterScroll\"]"));
        Assert.That(tscn, Does.Contain("clip_contents = true"));
        Assert.That(tscn, Does.Contain("custom_minimum_size = Vector2(360, 160)"));
    }

    private static string FindScene()
    {
        const string relative = "src/UI/Inspectors/TimelineInspector.tscn";
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
