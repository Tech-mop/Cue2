// SPDX-FileCopyrightText: 2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Cue2.Services;

namespace Cue2.Tests.Services;

/// <summary>
/// Error paths of <see cref="TaskUtil"/> and <see cref="MediaPaths"/>, with their log lines caught
/// through <see cref="Diag"/> instead of Godot.
/// </summary>
[TestFixture]
[NonParallelizable]
public class HelperErrorPathTests
{
    private readonly List<string> _errors = new();
    private Action<string> _savedError;
    private Action<string> _savedInfo;

    /// <summary>Catches log lines for this test.</summary>
    [SetUp]
    public void SetUp()
    {
        _savedError = Diag.Error;
        _savedInfo = Diag.Info;
        Diag.Error = message => { lock (_errors) _errors.Add(message); };
        Diag.Info = _ => { };
    }

    /// <summary>Puts the Godot log back.</summary>
    [TearDown]
    public void TearDown()
    {
        Diag.Error = _savedError;
        Diag.Info = _savedInfo;
        _errors.Clear();
    }

    /// <summary>A task that already failed is logged with its context, and nothing is thrown.</summary>
    [Test]
    public void FailedTaskIsLoggedWithContext()
    {
        TaskUtil.FireAndForget(Task.FromException(new InvalidOperationException("boom")), "Tests.Failed");
        Assert.That(_errors, Is.EqualTo(new[] { "TaskUtil:Tests.Failed - InvalidOperationException: boom" }));
    }

    /// <summary>A task that fails later is logged once it fails.</summary>
    [Test]
    public void LaterFailureIsLogged()
    {
        var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskUtil.FireAndForget(source.Task, "Tests.Later");
        Assert.That(_errors, Is.Empty);
        source.SetException(new TimeoutException("late"));

        var clock = Stopwatch.StartNew();
        while (Count() == 0 && clock.ElapsedMilliseconds < 5000)
            Thread.Sleep(10);
        Assert.That(_errors, Is.EqualTo(new[] { "TaskUtil:Tests.Later - TimeoutException: late" }));
    }

    /// <summary>Work that throws before returning a task is logged; no context gives the plain prefix.</summary>
    [Test]
    public void SynchronousThrowIsLogged()
    {
        TaskUtil.Run(() => throw new ArgumentException("sync"));
        Assert.That(_errors, Is.EqualTo(new[] { "TaskUtil - ArgumentException: sync" }));
    }

    /// <summary>Null and successful tasks log nothing.</summary>
    [Test]
    public void NullAndSuccessfulTasksAreQuiet()
    {
        TaskUtil.FireAndForget(null);
        TaskUtil.Run(null);
        TaskUtil.FireAndForget(Task.CompletedTask, "Tests.Ok");
        TaskUtil.Run(() => Task.CompletedTask, "Tests.Ok");
        Assert.That(_errors, Is.Empty);
    }

    /// <summary>A path the OS cannot use is logged and handed back unchanged.</summary>
    [Test]
    public void UnusableMediaPathIsLoggedAndReturned()
    {
        string bad = "Audio/bad\0name.wav";
        string session = Path.Combine(Path.GetTempPath(), "cue2-show");
        Assert.That(MediaPaths.Resolve(bad, session), Is.EqualTo(bad));
        Assert.That(_errors.Count, Is.EqualTo(1));
        Assert.That(_errors[0], Does.StartWith("MediaPaths:Resolve - "));
    }

    /// <summary>Show-relative and absolute paths resolve without log lines.</summary>
    [Test]
    public void GoodMediaPathsResolveQuietly()
    {
        string session = Path.Combine(Path.GetTempPath(), "cue2-show");
        string expected = Path.GetFullPath(Path.Combine(session, "Audio", "song.wav"));
        Assert.That(MediaPaths.Resolve("Audio/song.wav", session), Is.EqualTo(expected));
        Assert.That(MediaPaths.Resolve(expected, null), Is.EqualTo(expected));
        Assert.That(MediaPaths.TryMakeRelative(expected, session), Is.EqualTo("Audio/song.wav"));
        Assert.That(MediaPaths.TryMakeRelative(Path.Combine(Path.GetTempPath(), "elsewhere.wav"), session), Is.Null);
        Assert.That(_errors, Is.Empty);
    }

    private int Count()
    {
        lock (_errors)
            return _errors.Count;
    }
}
