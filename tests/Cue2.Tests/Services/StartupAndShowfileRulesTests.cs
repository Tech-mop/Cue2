// SPDX-FileCopyrightText: 2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using NUnit.Framework;
using Cue2.Services;

namespace Cue2.Tests.Services;

/// <summary>
/// Startup fault collection and the showfile version rules that decide whether open asks first.
/// </summary>
[TestFixture]
public class StartupAndShowfileRulesTests
{
    /// <summary>Faults are recorded once per id, features are deduplicated in first-seen order.</summary>
    [Test]
    public void FaultRegistryDeduplicates()
    {
        var registry = new StartupFaultRegistry();
        Assert.That(registry.HasFaults, Is.False);
        Assert.That(registry.Report("ffmpeg", "FFmpeg", " missing ", "Video playback", "Audio playback", "Video playback", " "), Is.True);
        Assert.That(registry.Report("ffmpeg", "FFmpeg", "again"), Is.False, "same id twice");
        Assert.That(registry.Report("sdl", null, "x", "Audio playback", "Audio devices"), Is.True);
        Assert.That(registry.Report(" ", "x", "x"), Is.False, "blank id");

        var faults = registry.Snapshot();
        Assert.That(faults.Count, Is.EqualTo(2));
        Assert.That(faults[0].Detail, Is.EqualTo("missing"));
        Assert.That(faults[0].AffectedFeatures, Is.EqualTo(new[] { "Video playback", "Audio playback" }));
        Assert.That(faults[1].Subsystem, Is.EqualTo("sdl"), "blank subsystem falls back to the id");
        Assert.That(registry.CollectAffectedFeatures(), Is.EqualTo(new[] { "Video playback", "Audio playback", "Audio devices" }));
    }

    /// <summary>After the startup dialog seals the registry, later reports are ignored.</summary>
    [Test]
    public void FaultRegistrySealStopsReports()
    {
        var registry = new StartupFaultRegistry();
        registry.Seal();
        Assert.That(registry.Report("rtmidi", "MIDI", "x"), Is.False);
        Assert.That(registry.HasFaults, Is.False);
    }

    /// <summary>Only the format number decides whether open asks for confirmation.</summary>
    [Test]
    public void ShowfileVersionRules()
    {
        int current = ShowfileFormat.CurrentFormatVersion;

        var same = new ShowfileVersionInfo(current, "0.0.1", null);
        Assert.That(same.RequiresVersionConfirmation, Is.False, "a different app version alone does not ask");
        Assert.That(same.MatchesCurrentApp, Is.False);
        Assert.That(same.IsOlderFormat || same.IsNewerFormat, Is.False);

        var older = new ShowfileVersionInfo(current - 1, "0.0.1", null);
        Assert.That(older.RequiresVersionConfirmation, Is.True);
        Assert.That(older.IsOlderFormat, Is.True);

        var newer = new ShowfileVersionInfo(current + 1, "99.0.0", null);
        Assert.That(newer.RequiresVersionConfirmation, Is.True);
        Assert.That(newer.IsNewerFormat, Is.True);

        var legacy = ShowfileVersionInfo.Unknown;
        Assert.That(legacy.IsLegacyOrUnknown, Is.True);
        Assert.That(legacy.RequiresVersionConfirmation, Is.True);
        Assert.That(legacy.IsOlderFormat, Is.False, "unknown is not treated as an older numbered format");
        Assert.That(legacy.ToDisplayString(), Does.Contain("unknown"));
    }

    /// <summary>The running app version matches itself.</summary>
    [Test]
    public void ShowfileMatchesRunningApp()
    {
        var info = new ShowfileVersionInfo(ShowfileFormat.CurrentFormatVersion, Cue2.Version.SemanticVersionString, null);
        Assert.That(info.MatchesCurrentApp, Is.True);
        Assert.That(info.MatchesCurrent, Is.True);
    }
}
