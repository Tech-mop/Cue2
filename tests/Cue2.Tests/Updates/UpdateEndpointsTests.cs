// SPDX-FileCopyrightText: 2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using NUnit.Framework;
using Cue2.Services;

namespace Cue2.Tests.Updates;

/// <summary>
/// Platform keys and the URL allowlists the updater checks before downloading or opening links.
/// </summary>
[TestFixture]
public class UpdateEndpointsTests
{
    /// <summary>Asset keys per OS and architecture, best match first.</summary>
    [TestCase("windows", "x86_64", "x86_64", false, new[] { "windows-x86_64" })]
    [TestCase("windows", "arm64", "arm64", false, new[] { "windows-arm64", "windows-x86_64" })]
    [TestCase("windows", "x86_64", "arm64", false, new[] { "windows-x86_64", "windows-arm64" })]
    [TestCase("linux", "x86_64", "x86_64", false, new[] { "linux-x86_64" })]
    [TestCase("linux", "arm64", "arm64", false, new[] { "linux-arm64" })]
    [TestCase("macos", "arm64", "arm64", true, new[] { "macos-arm64", "macos-x86_64" })]
    [TestCase("macos", "arm64", "arm64", false, new[] { "macos-arm64" })]
    [TestCase("macos", "x86_64", "x86_64", false, new[] { "macos-x86_64" })]
    public void BuildsPlatformKeys(string os, string process, string machine, bool rosetta, string[] expected)
    {
        Assert.That(UpdateEndpoints.BuildPlatformKeys(os, process, machine, rosetta), Is.EqualTo(expected));
    }

    /// <summary>Only HTTPS GitHub and GitHub content hosts may serve a download.</summary>
    [TestCase("https://github.com/Tech-mop/Cue2/releases/download/v1/a.zip", true)]
    [TestCase("https://www.github.com/x", true)]
    [TestCase("https://objects.githubusercontent.com/x", true)]
    [TestCase("https://release-assets.githubusercontent.com/x", true)]
    [TestCase("HTTPS://GITHUB.COM/x", true)]
    [TestCase("http://github.com/x", false)]
    [TestCase("https://github.com.evil.example/x", false)]
    [TestCase("https://evilgithub.com/x", false)]
    [TestCase("https://githubusercontent.com.evil.example/x", false)]
    [TestCase("https://fakegithubusercontent.com/x", false)]
    [TestCase("ftp://github.com/x", false)]
    [TestCase("github.com/x", false)]
    [TestCase("", false)]
    [TestCase(null, false)]
    public void AllowsOnlyGitHubHttpsDownloads(string url, bool allowed)
    {
        Assert.That(UpdateEndpoints.IsAllowedDownloadUrl(url), Is.EqualTo(allowed));
    }

    /// <summary>Release links opened in the browser must be this repository on github.com.</summary>
    [Test]
    public void AllowsOnlyThisRepositoryReleasePages()
    {
        string repo = UpdateEndpoints.OwnerRepo;
        Assert.That(UpdateEndpoints.IsAllowedReleasePageUrl($"https://github.com/{repo}/releases/tag/v1.0.0"), Is.True);
        Assert.That(UpdateEndpoints.IsAllowedReleasePageUrl($"https://www.github.com/{repo}/releases"), Is.True);
        Assert.That(UpdateEndpoints.IsAllowedReleasePageUrl($"http://github.com/{repo}/releases"), Is.False);
        Assert.That(UpdateEndpoints.IsAllowedReleasePageUrl($"https://github.com:8443/{repo}/releases"), Is.False);
        Assert.That(UpdateEndpoints.IsAllowedReleasePageUrl($"https://user@github.com/{repo}/releases"), Is.False);
        Assert.That(UpdateEndpoints.IsAllowedReleasePageUrl("https://github.com/someone-else/Cue2/releases"), Is.False);
        Assert.That(UpdateEndpoints.IsAllowedReleasePageUrl($"https://objects.githubusercontent.com/{repo}/x"), Is.False);
        Assert.That(UpdateEndpoints.IsAllowedReleasePageUrl($"https://github.com/{repo}"), Is.False, "repository root has no trailing path");
        Assert.That(UpdateEndpoints.IsAllowedReleasePageUrl(null), Is.False);
    }
}
