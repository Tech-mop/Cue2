// SPDX-FileCopyrightText: 2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using NUnit.Framework;
using Cue2.Services;

namespace Cue2.Tests.Services;

/// <summary>
/// Relaunch arguments for the startup-error Restart button
/// (<see cref="ShowfileLaunchArgs.BuildRestartArgs"/>).
/// </summary>
[TestFixture]
public class RestartArgsTests
{
    /// <summary>A show opened by double-click (a plain engine arg) is passed on.</summary>
    [Test]
    public void DoubleClickedShowIsKept()
    {
        string[] args = ShowfileLaunchArgs.BuildRestartArgs(
            new[] { @"C:\Shows\x.c2" }, new string[0], null);
        Assert.That(args, Is.EqualTo(new[] { @"C:\Shows\x.c2" }));
    }

    /// <summary>User args go after <c>--</c> so the engine does not read them as its own.</summary>
    [Test]
    public void UserArgsGoAfterSeparator()
    {
        string[] args = ShowfileLaunchArgs.BuildRestartArgs(
            new[] { "--verbose" }, new[] { "--video-diag" }, null);
        Assert.That(args, Is.EqualTo(new[] { "--verbose", "--", "--video-diag" }));
    }

    /// <summary>The open show replaces any showfile on the command line and comes first.</summary>
    [Test]
    public void OpenShowReplacesCommandLineShow()
    {
        string[] args = ShowfileLaunchArgs.BuildRestartArgs(
            new[] { "/shows/old.c2", "--verbose" },
            new[] { "/shows/user.c2", "--video-diag" },
            "/shows/now.c2");
        Assert.That(args, Is.EqualTo(new[] { "/shows/now.c2", "--verbose", "--", "--video-diag" }));
    }

    /// <summary>The value after a flag that takes one is kept with it, even when it ends in .c2.</summary>
    [Test]
    public void FlagValuesStayWithTheirFlag()
    {
        string[] args = ShowfileLaunchArgs.BuildRestartArgs(
            new[] { "--path", "/proj", "--log-file", "/tmp/a.c2" }, null, "/shows/now.c2");
        Assert.That(args, Is.EqualTo(new[] { "/shows/now.c2", "--path", "/proj", "--log-file", "/tmp/a.c2" }));
    }

    /// <summary>A leading executable path (some platforms include it) is dropped.</summary>
    [Test]
    public void LeadingExecutableIsDropped()
    {
        string[] args = ShowfileLaunchArgs.BuildRestartArgs(
            new[] { @"C:\Program Files\Cue2\Cue2.exe", @"C:\Shows\x.c2" }, null, null);
        Assert.That(args, Is.EqualTo(new[] { @"C:\Shows\x.c2" }));
    }

    /// <summary>A show whose name starts with Cue2 is not mistaken for the executable.</summary>
    [Test]
    public void ShowNamedLikeTheAppIsKept()
    {
        string[] args = ShowfileLaunchArgs.BuildRestartArgs(new[] { "/shows/Cue2 demo.c2" }, null, null);
        Assert.That(args, Is.EqualTo(new[] { "/shows/Cue2 demo.c2" }));
    }

    /// <summary>Nothing in, nothing out.</summary>
    [Test]
    public void EmptyCommandLineGivesNoArgs()
    {
        Assert.That(ShowfileLaunchArgs.BuildRestartArgs(null, null, ""), Is.Empty);
    }
}
