// SPDX-FileCopyrightText: 2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using NUnit.Framework;
using Cue2.Services;

namespace Cue2.Tests;

/// <summary>
/// Proves the test project builds against Cue2 and can call its code.
/// </summary>
[TestFixture]
public class SmokeTests
{
    /// <summary>The test project can reach a plain Cue2 type.</summary>
    [Test]
    public void CanCallCue2Code()
    {
        Assert.That(UpdateSemVer.TryParse("1.2.3", out var version), Is.True);
        Assert.That(version.Major, Is.EqualTo(1));
        Assert.That(version.Minor, Is.EqualTo(2));
        Assert.That(version.Patch, Is.EqualTo(3));
    }
}
