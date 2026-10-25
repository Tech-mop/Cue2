// SPDX-FileCopyrightText: 2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System.Text.Json;
using NUnit.Framework;
using Cue2.Services;

namespace Cue2.Tests.Updates;

/// <summary>
/// Parsing of <c>latest.json</c> and the GitHub Releases list.
/// </summary>
[TestFixture]
public class UpdateManifestParserTests
{
    private const string GoodLatest = """
        {
          "schema": 1,
          "version": "v0.3.0",
          "notes": "Fixes",
          "platforms": {
            "windows-x86_64": { "name": "Cue2-windows-x86_64.zip", "url": "https://github.com/a/b.zip", "sha256": "ab", "size": 10 },
            "linux-x86_64": { "url": "https://github.com/a/c.tar.gz", "sha256": "cd" }
          }
        }
        """;

    /// <summary>A complete feed keeps its fields, drops the v and fills in the tag.</summary>
    [Test]
    public void ParsesCompleteLatestJson()
    {
        var feed = UpdateManifestParser.ParseLatestJson(GoodLatest);
        Assert.That(feed, Is.Not.Null);
        Assert.That(feed.Version, Is.EqualTo("0.3.0"));
        Assert.That(feed.Tag, Is.EqualTo("v0.3.0"));
        Assert.That(feed.Notes, Is.EqualTo("Fixes"));
        Assert.That(feed.Platforms.Count, Is.EqualTo(2));
        Assert.That(feed.Platforms["windows-x86_64"].Size, Is.EqualTo(10));
        Assert.That(feed.Platforms["linux-x86_64"].Sha256, Is.EqualTo("cd"));
        Assert.That(feed.HtmlUrl, Is.Not.Empty, "a missing release link falls back to the releases page");
    }

    /// <summary>
    /// Platform keys from latest.json match exactly: the deserializer builds its own dictionary,
    /// so the case-insensitive default on <see cref="UpdateFeed.Platforms"/> does not apply here.
    /// The export script writes lower-case keys, which is what the updater looks up.
    /// </summary>
    [Test]
    public void LatestJsonPlatformKeysMatchExactly()
    {
        var feed = UpdateManifestParser.ParseLatestJson(GoodLatest);
        Assert.That(feed.Platforms.ContainsKey("windows-x86_64"), Is.True);
        Assert.That(feed.Platforms.ContainsKey("WINDOWS-X86_64"), Is.False);
    }

    /// <summary>Comments and trailing commas are accepted, as the parser options allow.</summary>
    [Test]
    public void AcceptsCommentsAndTrailingCommas()
    {
        var feed = UpdateManifestParser.ParseLatestJson("{ // note\n \"version\": \"1.0.0\", }");
        Assert.That(feed?.Version, Is.EqualTo("1.0.0"));
        Assert.That(feed.Platforms, Is.Not.Null);
    }

    /// <summary>Empty, broken or versionless feeds are ignored rather than thrown.</summary>
    [TestCase(null)]
    [TestCase("")]
    [TestCase("not json")]
    [TestCase("{")]
    [TestCase("{\"version\": \"\"}")]
    [TestCase("{\"notes\": \"no version\"}")]
    [TestCase("{\"version\": 3}")]
    [TestCase("[]")]
    public void RejectsUnusableLatestJson(string json)
    {
        Assert.That(UpdateManifestParser.ParseLatestJson(json), Is.Null);
    }

    private const string Releases = """
        [
          { "tag_name": "v0.4.0", "draft": true, "assets": [] },
          { "tag_name": "v0.3.1-rc.1", "prerelease": true, "html_url": "https://github.com/Tech-mop/Cue2/releases/tag/v0.3.1-rc.1",
            "assets": [ { "name": "Cue2-linux-x86_64.tar.gz", "browser_download_url": "https://github.com/x/rc.tar.gz", "digest": "sha256:FFEE", "size": 5 } ] },
          { "tag_name": "v0.3.0", "html_url": "https://github.com/Tech-mop/Cue2/releases/tag/v0.3.0", "body": "Notes",
            "assets": [
              { "name": "Cue2-windows-x86_64.zip", "browser_download_url": "https://github.com/x/w.zip", "digest": "sha256:AABB", "size": 7 },
              { "name": "Cue2-macos-arm64.zip", "browser_download_url": "https://github.com/x/m.zip" },
              { "name": "latest.json", "browser_download_url": "https://github.com/x/latest.json" },
              { "name": "Cue2-windows-x86_64.zip", "browser_download_url": "" }
            ] },
          { "tag_name": "nightly", "assets": [] },
          { "tag_name": "v0.2.0", "assets": [] }
        ]
        """;

    /// <summary>Stable channel picks the newest non-draft, non-prerelease tag and maps its assets.</summary>
    [Test]
    public void ReleasesStableChannelPicksNewestStable()
    {
        var feed = UpdateManifestParser.ParseGitHubReleases(Releases, includePrerelease: false);
        Assert.That(feed, Is.Not.Null);
        Assert.That(feed.Version, Is.EqualTo("0.3.0"));
        Assert.That(feed.Notes, Is.EqualTo("Notes"));
        Assert.That(feed.Platforms.Keys, Is.EquivalentTo(new[] { "windows-x86_64", "macos-arm64" }));
        Assert.That(feed.Platforms["windows-x86_64"].Sha256, Is.EqualTo("AABB"));
        Assert.That(feed.Platforms["windows-x86_64"].Size, Is.EqualTo(7));
        Assert.That(feed.Platforms["macos-arm64"].Sha256, Is.Empty, "no digest means no checksum");
    }

    /// <summary>Prerelease channel can pick a newer prerelease; drafts are never picked.</summary>
    [Test]
    public void ReleasesPrereleaseChannelPicksNewerPrerelease()
    {
        var feed = UpdateManifestParser.ParseGitHubReleases(Releases, includePrerelease: true);
        Assert.That(feed.Version, Is.EqualTo("0.3.1-rc.1"));
        Assert.That(feed.Platforms["linux-x86_64"].Sha256, Is.EqualTo("FFEE"));
    }

    /// <summary>Lists with nothing usable give no feed.</summary>
    [TestCase(null)]
    [TestCase("")]
    [TestCase("{}")]
    [TestCase("[]")]
    [TestCase("[{\"tag_name\": \"latest\"}]")]
    public void ReleasesWithoutUsableEntriesGiveNoFeed(string json)
    {
        Assert.That(UpdateManifestParser.ParseGitHubReleases(json, includePrerelease: true), Is.Null);
    }

    /// <summary>
    /// Broken JSON from the releases API throws (unlike latest.json, which returns null);
    /// the update service catches it around the request.
    /// </summary>
    [Test]
    public void ReleasesBrokenJsonThrows()
    {
        Assert.That(() => UpdateManifestParser.ParseGitHubReleases("[{", includePrerelease: false),
            Throws.InstanceOf<JsonException>());
    }
}
