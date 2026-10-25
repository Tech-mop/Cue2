// SPDX-FileCopyrightText: 2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Cue2.Domain.Cuelist;
using Cue2.EngineTests.Fixtures;
using Cue2.UI.Popups;
using GdUnit4;
using Godot;
using static GdUnit4.Assertions;

namespace Cue2.EngineTests.Media;

/// <summary>
/// The sample show fixture: generated media becomes cues, saves, and opens again. Needs FFmpeg.
/// </summary>
[TestSuite]
[RequireGodotRuntime]
public class ShowFixtureTests
{
    /// <summary>Loads the main window once for this suite.</summary>
    [Before]
    public async Task Boot()
    {
        await EngineHarness.BootAsync();
        AssertThat(EngineHarness.MediaAvailable).OverrideFailureMessage("FFmpeg did not load; media tests cannot run").IsTrue();
    }

    /// <summary>Starts each test from an empty show.</summary>
    [BeforeTest]
    public async Task Reset() => await EngineHarness.ResetShowAsync();

    /// <summary>A show with tones, a group and a video builds quickly, saves and opens with the same cues.</summary>
    [TestCase(Timeout = 60000)]
    public async Task BuildSaveAndReopen()
    {
        int mark = EngineHarness.ErrorMark();
        var clock = Stopwatch.StartNew();
        string[] tones = ShowBuilder.ToneFiles(3);
        string[] grouped = ShowBuilder.ToneFiles(2, 1.0, "grouped");
        string video = ShowBuilder.VideoFile();
        long generateMs = clock.ElapsedMilliseconds;

        var cues = await ShowBuilder.DropAsync(tones);
        ShowBuilder.Chain(cues, FollowType.Continue);
        var group = await ShowBuilder.DropAsync(grouped, MultiFileDropMode.WrapInOneGroup);
        await ShowBuilder.DropAsync(new[] { video });
        GD.Print($"ShowFixtureTests:BuildSaveAndReopen - media written in {generateMs} ms, show built in {clock.ElapsedMilliseconds} ms");
        AssertThat(generateMs).IsLess(2000);
        AssertThat(group.Count).IsEqual(3);
        int expected = EngineHarness.AllCues().Length;
        AssertThat(expected).IsEqual(7);

        string path = Path.Combine(EngineHarness.WorkDir, "shows", "fixture.c2");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        AssertThat(await EngineHarness.Saves.SaveToPathAsync(path)).IsTrue();
        // Saving puts the show in its own folder; the file path moves with it.
        path = EngineHarness.Data.SessionPath;
        AssertThat(File.Exists(path)).IsTrue();

        EngineHarness.Saves.NewSessionWithoutPrompt();
        await EngineHarness.Saves.WaitForSessionIdleAsync(10000);
        AssertThat(EngineHarness.AllCues().Length).IsEqual(0);

        await EngineHarness.Saves.OpenPathAsync(path);
        AssertThat(await EngineHarness.Saves.WaitForSessionIdleAsync(15000)).IsTrue();
        var reopened = EngineHarness.AllCues();
        AssertThat(reopened.Length).IsEqual(expected);
        AssertThat(reopened.Count(c => c.Follow == FollowType.Continue)).IsEqual(2);
        EngineHarness.AssertNoNewErrors(mark);
    }
}
