// SPDX-FileCopyrightText: 2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System.IO;
using Cue2.Media;
using NUnit.Framework;

namespace Cue2.Tests.Media;

/// <summary>
/// FFmpeg input URLs: file: prefix, URL-syntax names, over-long paths, reject copy.
/// </summary>
[TestFixture]
public class FfmpegMediaPathTests
{
    /// <summary>Local files are opened as a file: URL so %, #, and ? stay literal.</summary>
    [Test]
    public void ToOpenUrlPrefixesFileScheme()
    {
        string url = FfmpegMediaPath.ToOpenUrl("/Users/show/cue.wav");
        Assert.That(url, Does.StartWith("file:"));
        Assert.That(url, Does.Contain("cue.wav"));
        Assert.That(url, Does.Not.Contain('\\'));

        string already = FfmpegMediaPath.ToOpenUrl("file:/tmp/a.wav");
        Assert.That(already, Does.StartWith("file:"));
    }

    /// <summary>Percent-encoding the path would break FFmpeg's file protocol (it does not URL-decode).</summary>
    [Test]
    public void ToOpenUrlLeavesUrlSyntaxCharactersLiteral()
    {
        string url = FfmpegMediaPath.ToOpenUrl("/tmp/take%20a#2?.wav");
        Assert.That(url, Does.Contain("take%20a#2?.wav"));
        Assert.That(url, Does.Not.Contain("%2520"));
    }

    /// <summary>Percent, hash, and question mark are the characters FFmpeg treats as URL syntax.</summary>
    [Test]
    public void UrlSyntaxCharsAreDetected()
    {
        Assert.That(FfmpegMediaPath.ContainsUrlSyntaxChars("song%20mix.wav"), Is.True);
        Assert.That(FfmpegMediaPath.ContainsUrlSyntaxChars("take#2.wav"), Is.True);
        Assert.That(FfmpegMediaPath.ContainsUrlSyntaxChars("take?final.wav"), Is.True);
        Assert.That(FfmpegMediaPath.ContainsUrlSyntaxChars("normal-name.wav"), Is.False);
        Assert.That(FfmpegMediaPath.ContainsUrlSyntaxChars(null), Is.False);
    }

    /// <summary>concat: and http: look like protocols; a Windows drive letter does not.</summary>
    [Test]
    public void ProtocolPrefixIsDetected()
    {
        Assert.That(FfmpegMediaPath.LooksLikeUrlProtocol("concat:list.txt"), Is.True);
        Assert.That(FfmpegMediaPath.LooksLikeUrlProtocol("http://host/a.wav"), Is.True);
        Assert.That(FfmpegMediaPath.LooksLikeUrlProtocol("C:\\Shows\\a.wav"), Is.False);
        Assert.That(FfmpegMediaPath.LooksLikeUrlProtocol("C:/Shows/a.wav"), Is.False);
        Assert.That(FfmpegMediaPath.LooksLikeUrlProtocol("/Users/show/a:b.wav"), Is.False);
        Assert.That(FfmpegMediaPath.LooksLikeUrlProtocol("plain.wav"), Is.False);
    }

    /// <summary>A 256-byte file name is over NAME_MAX; a short name is not.</summary>
    [Test]
    public void OverlongFileNameIsDetected()
    {
        Assert.That(FfmpegMediaPath.IsLikelyTooLong("short.wav"), Is.False);

        string longName = new string('a', 256) + ".wav";
        Assert.That(FfmpegMediaPath.Utf8ByteLength(longName), Is.GreaterThan(FfmpegMediaPath.MaxFileNameBytes));
        Assert.That(FfmpegMediaPath.IsLikelyTooLong(Path.Combine("/tmp", longName)), Is.True);
    }

    /// <summary>Reject copy names the file, keeps the technical error, and says Cue2 did not add it.</summary>
    [Test]
    public void RejectMessageNamesTheFileAndRefusesIt()
    {
        string percent = FfmpegMediaPath.FormatRejectMessage(
            "/shows/Audio/take%20a.wav", "No such file or directory");
        Assert.That(percent, Does.Contain("take%20a.wav"));
        Assert.That(percent, Does.Contain("No such file or directory"));
        Assert.That(percent, Does.Contain("Cue2 did not add this file"));
        Assert.That(percent, Does.Contain("%").Or.Contain("media reader"));

        string longMsg = FfmpegMediaPath.FormatRejectMessage(
            Path.Combine("/tmp", new string('b', 256) + ".wav"), "File name too long");
        Assert.That(longMsg, Does.Contain("too long"));
        Assert.That(longMsg, Does.Contain("Cue2 did not add this file"));
    }
}
