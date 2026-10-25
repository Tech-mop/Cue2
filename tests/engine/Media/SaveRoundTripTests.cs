// SPDX-FileCopyrightText: 2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System.Collections.Generic;
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
/// Save and open with media cues: every saved cue field comes back, and the unsaved-changes
/// flag follows edits, saves and opens. Needs FFmpeg.
/// </summary>
[TestSuite]
[RequireGodotRuntime]
public class SaveRoundTripTests
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

    /// <summary>Every field a cue saves is the same after save and open.</summary>
    [TestCase(Timeout = 60000)]
    public async Task EveryCueFieldSurvivesSaveAndOpen()
    {
        int mark = EngineHarness.ErrorMark();
        var cues = await ShowBuilder.DropAsync(ShowBuilder.ToneFiles(3, 1.0, "fields"));
        await ShowBuilder.DropAsync(ShowBuilder.ToneFiles(2, 1.0, "fieldgroup"), MultiFileDropMode.WrapInOneGroup);
        await ShowBuilder.DropAsync(new[] { ShowBuilder.VideoFile("fields.y4m") });

        cues[0].Name = "Renamed cue";
        cues[0].CueNum = "1.5";
        cues[0].PreWait = 0.25;
        cues[0].PostWait = 1.5;
        cues[0].Follow = FollowType.Follow;
        cues[0].Color = new Color(0.2f, 0.4f, 0.6f);
        cues[0].Notes = "Notes with \"quotes\" and a\nnew line";
        cues[1].Armed = false;
        cues[1].SkipIfDisarmed = true;
        cues[1].OnlyOneActiveInstance = true;
        cues[2].Follow = FollowType.Continue;

        Dictionary<int, string> before = Snapshot();
        AssertThat(before.Count).IsEqual(7);

        string path = Path.Combine(EngineHarness.WorkDir, "roundtrip", "roundtrip.c2");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        AssertThat(await EngineHarness.Saves.SaveToPathAsync(path)).IsTrue();
        path = EngineHarness.Data.SessionPath;

        EngineHarness.Saves.NewSessionWithoutPrompt();
        await EngineHarness.Saves.WaitForSessionIdleAsync(10000);
        await EngineHarness.Saves.OpenPathAsync(path);
        AssertThat(await EngineHarness.Saves.WaitForSessionIdleAsync(15000)).IsTrue();

        Dictionary<int, string> after = Snapshot();
        AssertThat(after.Keys.OrderBy(k => k).ToArray()).IsEqual(before.Keys.OrderBy(k => k).ToArray());
        foreach (var pair in before)
            AssertThat(after[pair.Key]).OverrideFailureMessage($"Cue {pair.Key} changed:\n{pair.Value}\n{after[pair.Key]}").IsEqual(pair.Value);
        EngineHarness.AssertNoNewErrors(mark);
    }

    /// <summary>Edits mark the show unsaved, saving clears it, and opening does not set it.</summary>
    [TestCase(Timeout = 60000)]
    public async Task UnsavedFlagFollowsEditsSavesAndOpens()
    {
        int mark = EngineHarness.ErrorMark();
        var saves = EngineHarness.Saves;
        AssertThat(saves.HasUnsavedChanges).IsFalse();

        var cue = (await ShowBuilder.DropAsync(ShowBuilder.ToneFiles(1, 1.0, "dirty"))).Single();
        AssertThat(saves.HasUnsavedChanges).IsTrue();

        string path = Path.Combine(EngineHarness.WorkDir, "dirty", "dirty.c2");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        AssertThat(await saves.SaveToPathAsync(path)).IsTrue();
        path = EngineHarness.Data.SessionPath;
        AssertThat(saves.HasUnsavedChanges).IsFalse();

        EngineHarness.Data.HistoryManager.RecordCueChange(cue.Id, "Rename");
        cue.Name = "Edited";
        AssertThat(saves.HasUnsavedChanges).IsTrue();
        AssertThat(await saves.SaveToPathAsync(path)).IsTrue();
        AssertThat(saves.HasUnsavedChanges).IsFalse();

        saves.NewSessionWithoutPrompt();
        await saves.WaitForSessionIdleAsync(10000);
        await saves.OpenPathAsync(path);
        AssertThat(await saves.WaitForSessionIdleAsync(15000)).IsTrue();
        await EngineHarness.FramesAsync(10);
        AssertThat(EngineHarness.AllCues().Single().Name).IsEqual("Edited");
        AssertThat(saves.HasUnsavedChanges).IsFalse();
        EngineHarness.AssertNoNewErrors(mark);
    }

    /// <summary>The saved form of every cue, by id.</summary>
    private static Dictionary<int, string> Snapshot() =>
        EngineHarness.AllCues().ToDictionary(c => c.Id, c => Json.Stringify(c.GetData(), "", true));
}
