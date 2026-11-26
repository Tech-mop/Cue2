// SPDX-FileCopyrightText: 2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System;
using Cue2.Services;
using NUnit.Framework;

namespace Cue2.Tests.Services;

/// <summary>
/// Presenter count crosses 0↔1 once; double start/stop on the same token is a no-op.
/// </summary>
[TestFixture]
public class IdleRenderPolicyTests
{
    [SetUp]
    public void ResetCount()
    {
        IdleRenderPolicy.ResetForTests();
    }

    [TearDown]
    public void TearDown()
    {
        IdleRenderPolicy.ResetForTests();
    }

    /// <summary>First presenter turns the policy on; last presenter turns it off.</summary>
    [Test]
    public void FirstAndLastPresenterCrossTheBoundary()
    {
        bool a = false;
        bool b = false;

        Assert.That(IdleRenderPolicy.SetPresenting(ref a, true), Is.True);
        Assert.That(IdleRenderPolicy.IsPresenting, Is.True);
        Assert.That(IdleRenderPolicy.PresentingCount, Is.EqualTo(1));

        Assert.That(IdleRenderPolicy.SetPresenting(ref b, true), Is.False);
        Assert.That(IdleRenderPolicy.PresentingCount, Is.EqualTo(2));

        Assert.That(IdleRenderPolicy.SetPresenting(ref a, false), Is.False);
        Assert.That(IdleRenderPolicy.IsPresenting, Is.True);

        Assert.That(IdleRenderPolicy.SetPresenting(ref b, false), Is.True);
        Assert.That(IdleRenderPolicy.IsPresenting, Is.False);
        Assert.That(IdleRenderPolicy.PresentingCount, Is.EqualTo(0));
    }

    /// <summary>Starting or stopping twice on the same instance does not change the count.</summary>
    [Test]
    public void SameTokenIsIdempotent()
    {
        bool token = false;
        Assert.That(IdleRenderPolicy.SetPresenting(ref token, true), Is.True);
        Assert.That(IdleRenderPolicy.SetPresenting(ref token, true), Is.False);
        Assert.That(IdleRenderPolicy.PresentingCount, Is.EqualTo(1));

        Assert.That(IdleRenderPolicy.SetPresenting(ref token, false), Is.True);
        Assert.That(IdleRenderPolicy.SetPresenting(ref token, false), Is.False);
        Assert.That(IdleRenderPolicy.PresentingCount, Is.EqualTo(0));
    }
}

/// <summary>Footer clock is second-resolution so idle skip-draw is not forced at 100 Hz.</summary>
[TestFixture]
public class CurrentTimeFormatTests
{
    /// <summary>Display string is hours:minutes:seconds plus AM/PM, without hundredths.</summary>
    [Test]
    public void ClockFormatIsOncePerSecond()
    {
        var t = new DateTime(2026, 11, 26, 14, 5, 9, 870);
        string text = CurrentTime.FormatClock(t);
        Assert.That(text, Does.Contain("02:05:09"));
        Assert.That(text, Does.Not.Contain(":87"));
        Assert.That(text, Does.Contain("PM"));
    }
}
