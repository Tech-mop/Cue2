// SPDX-FileCopyrightText: 2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using NUnit.Framework;
using Cue2.Services;

namespace Cue2.Tests.Updates;

/// <summary>
/// Version parsing and ordering used to decide whether an update is offered.
/// </summary>
[TestFixture]
public class UpdateSemVerTests
{
    /// <summary>Plain and partial versions parse; missing parts read as zero.</summary>
    [TestCase("1.2.3", 1, 2, 3, "")]
    [TestCase("0.3.0-rc.1", 0, 3, 0, "rc.1")]
    [TestCase("2", 2, 0, 0, "")]
    [TestCase("2.5", 2, 5, 0, "")]
    [TestCase("10.20.30-alpha.beta", 10, 20, 30, "alpha.beta")]
    public void ParsesValidVersions(string text, int major, int minor, int patch, string prerelease)
    {
        Assert.That(UpdateSemVer.TryParse(text, out var v), Is.True);
        Assert.That(v.Major, Is.EqualTo(major));
        Assert.That(v.Minor, Is.EqualTo(minor));
        Assert.That(v.Patch, Is.EqualTo(patch));
        Assert.That(v.Prerelease, Is.EqualTo(prerelease));
    }

    /// <summary>Garbage, signs, extra parts and build metadata are rejected.</summary>
    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    [TestCase("abc")]
    [TestCase("1.2.3.4")]
    [TestCase("-1.0.0")]
    [TestCase("1.x.0")]
    [TestCase("v1.2.3")]
    [TestCase("1.2.3+build.5")]
    public void RejectsInvalidVersions(string text)
    {
        Assert.That(UpdateSemVer.TryParse(text, out _), Is.False);
    }

    /// <summary>Normalize strips a leading v and whitespace, so tags compare as versions.</summary>
    [TestCase("v1.2.3", "1.2.3")]
    [TestCase(" V0.3.0-rc.1 ", "0.3.0-rc.1")]
    [TestCase("1.0.0", "1.0.0")]
    [TestCase(null, "")]
    public void NormalizeStripsLeadingV(string raw, string expected)
    {
        Assert.That(UpdateSemVer.Normalize(raw), Is.EqualTo(expected));
    }

    /// <summary>Each version in the list is newer than the one before it.</summary>
    [Test]
    public void OrdersReleasesAndPrereleases()
    {
        string[] order =
        {
            "0.2.0", "0.3.0-alpha", "0.3.0-alpha.1", "0.3.0-alpha.beta", "0.3.0-beta",
            "0.3.0-beta.2", "0.3.0-beta.11", "0.3.0-rc.1", "0.3.0-rc.2", "0.3.0-rc.10",
            "0.3.0", "0.3.1", "0.10.0", "1.0.0"
        };
        for (int i = 0; i + 1 < order.Length; i++)
        {
            Assert.That(UpdateSemVer.IsNewer(order[i + 1], order[i]), Is.True, $"{order[i + 1]} > {order[i]}");
            Assert.That(UpdateSemVer.IsNewer(order[i], order[i + 1]), Is.False, $"{order[i]} < {order[i + 1]}");
        }
    }

    /// <summary>The same version, with or without a v, is never newer.</summary>
    [TestCase("v0.3.0", "0.3.0")]
    [TestCase("0.3.0", "v0.3.0")]
    [TestCase("1.0.0-rc.1", "1.0.0-rc.1")]
    public void SameVersionIsNotNewer(string candidate, string current)
    {
        Assert.That(UpdateSemVer.IsNewer(candidate, current), Is.False);
    }

    /// <summary>An unreadable version on either side never offers an update.</summary>
    [TestCase("garbage", "0.1.0")]
    [TestCase("9.9.9", "garbage")]
    [TestCase("0.4.0+build5", "0.3.0")]
    public void UnreadableVersionIsNeverNewer(string candidate, string current)
    {
        Assert.That(UpdateSemVer.IsNewer(candidate, current), Is.False);
    }
}
