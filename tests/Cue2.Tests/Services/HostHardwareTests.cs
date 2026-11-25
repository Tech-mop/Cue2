// SPDX-FileCopyrightText: 2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System.Runtime.InteropServices;
using NUnit.Framework;
using Cue2.Services;

namespace Cue2.Tests.Services;

/// <summary>
/// Intel Mac detection for the startup unsupported-hardware dialog.
/// </summary>
[TestFixture]
public class HostHardwareTests
{
    [TestCase(false, Architecture.X64, Architecture.X64, null, false)]
    [TestCase(false, Architecture.Arm64, Architecture.Arm64, false, false)]
    [TestCase(true, Architecture.Arm64, Architecture.Arm64, true, false)]
    [TestCase(true, Architecture.Arm64, Architecture.Arm64, null, false)]
    [TestCase(true, Architecture.X64, Architecture.X64, true, false)]
    [TestCase(true, Architecture.X64, Architecture.X64, false, true)]
    [TestCase(true, Architecture.X64, Architecture.X64, null, true)]
    [TestCase(true, Architecture.X86, Architecture.X86, null, true)]
    [TestCase(true, Architecture.Arm64, Architecture.X64, null, false)]
    [TestCase(true, Architecture.X64, Architecture.Arm64, null, false)]
    public void IntelMacRule(
        bool isMacOs,
        Architecture osArchitecture,
        Architecture processArchitecture,
        bool? appleSilicon,
        bool expected)
    {
        Assert.That(
            HostHardware.IsUnsupportedMacIntel(isMacOs, osArchitecture, processArchitecture, appleSilicon),
            Is.EqualTo(expected));
    }

    /// <summary>The live probe never flags Windows or Linux, or this Apple Silicon Mac.</summary>
    [Test]
    public void LiveProbeIsFalseOnSupportedHosts()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            Assert.That(HostHardware.IsUnsupportedMacIntel(), Is.False);
            return;
        }

        if (RuntimeInformation.OSArchitecture == Architecture.Arm64
            || RuntimeInformation.ProcessArchitecture == Architecture.Arm64)
        {
            Assert.That(HostHardware.IsUnsupportedMacIntel(), Is.False);
        }
    }

    /// <summary>The dialog looks up the hardware fault by id.</summary>
    [Test]
    public void HasUnsupportedMacIntelFaultFindsId()
    {
        Assert.That(HostHardware.HasUnsupportedMacIntelFault(null), Is.False);
        Assert.That(HostHardware.HasUnsupportedMacIntelFault(System.Array.Empty<StartupFault>()), Is.False);

        var ffmpeg = new StartupFault("ffmpeg", "FFmpeg", "missing", new[] { "Video playback" });
        Assert.That(HostHardware.HasUnsupportedMacIntelFault(new[] { ffmpeg }), Is.False);

        var intel = new StartupFault(
            HostHardware.UnsupportedMacIntelId,
            "Hardware",
            HostHardware.UnsupportedMacIntelDetail,
            new[] { "Audio playback" });
        Assert.That(HostHardware.HasUnsupportedMacIntelFault(new[] { ffmpeg, intel }), Is.True);
    }
}
