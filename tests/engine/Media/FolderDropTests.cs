// SPDX-FileCopyrightText: 2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Cue2.Domain.Cuelist;
using Cue2.Services;
using Cue2.EngineTests.Fixtures;
using Cue2.UI.Popups;
using GdUnit4;
using static GdUnit4.Assertions;

namespace Cue2.EngineTests.Media;

/// <summary>
/// Dropping a folder recreates that tree as group cues named after the folders.
/// </summary>
[TestSuite]
[RequireGodotRuntime]
public class FolderDropTests
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

    /// <summary>Preserve-folder-tree import names groups after folders and nests children.</summary>
    [TestCase(Timeout = 30000)]
    public async Task PreserveFolderTreeUsesFolderNamesAsGroups()
    {
        int mark = EngineHarness.ErrorMark();
        string showDir = Path.Combine(EngineHarness.WorkDir, "folderdrop", "Show");
        string act1 = Path.Combine(showDir, "Act1");
        string nested = Path.Combine(act1, "Nested");
        string act2 = Path.Combine(showDir, "Act2");
        Directory.CreateDirectory(nested);
        Directory.CreateDirectory(act2);
        Directory.CreateDirectory(Path.Combine(showDir, "Empty"));
        MediaFixtures.WriteToneWav(Path.Combine(act1, "alpha.wav"), 0.2, 220);
        MediaFixtures.WriteToneWav(Path.Combine(nested, "beta.wav"), 0.2, 330);
        MediaFixtures.WriteToneWav(Path.Combine(act2, "gamma.wav"), 0.2, 440);
        File.WriteAllText(Path.Combine(showDir, "notes.txt"), "skip");
        File.WriteAllBytes(Path.Combine(showDir, ".hidden.wav"), System.Array.Empty<byte>());

        var globs = GlobalData.AudioFileFilters
            .Concat(GlobalData.VideoFileFilters)
            .Concat(GlobalData.ImageFileFilters);
        var expansion = DroppedMediaExpansion.Expand(new[] { showDir }, globs);
        AssertThat(expansion.HasFolderTree).IsTrue();
        AssertThat(expansion.FileCount).IsEqual(3);

        EngineHarness.Data.Cuelist.CreateCuesFromDroppedFiles(
            expansion, -1, DropInsertMode.AtEnd, MultiFileDropMode.PreserveFolderTree);

        bool ready = await EngineHarness.UntilAsync(() =>
        {
            var media = EngineHarness.AllCues().Where(c => c.Components.Count > 0).ToList();
            return media.Count == 3 && media.All(c => c.Duration > 0);
        }, 15000);
        AssertThat(ready).OverrideFailureMessage("Folder-drop media cues did not become ready").IsTrue();

        var list = EngineHarness.Data.Cuelist;
        AssertThat(list.RootOrder.Count).IsEqual(1);
        var show = CueList.FetchCueFromId(list.RootOrder[0]);
        AssertThat(show).IsNotNull();
        AssertThat(show.Name).IsEqual("Show");
        AssertThat(show.ChildCues.Count).IsEqual(2);

        var act1Cue = CueList.FetchCueFromId(show.ChildCues[0]);
        var act2Cue = CueList.FetchCueFromId(show.ChildCues[1]);
        AssertThat(act1Cue.Name).IsEqual("Act1");
        AssertThat(act2Cue.Name).IsEqual("Act2");
        AssertThat(act1Cue.ChildCues.Count).IsEqual(2);

        var nestedCue = CueList.FetchCueFromId(act1Cue.ChildCues[0]);
        var alpha = CueList.FetchCueFromId(act1Cue.ChildCues[1]);
        AssertThat(nestedCue.Name).IsEqual("Nested");
        AssertThat(alpha.Name).IsEqual("alpha");
        AssertThat(CueList.FetchCueFromId(nestedCue.ChildCues[0]).Name).IsEqual("beta");
        AssertThat(CueList.FetchCueFromId(act2Cue.ChildCues[0]).Name).IsEqual("gamma");
        AssertThat(EngineHarness.AllCues().Any(c => c.Name == "hidden" || c.Name == "Empty" || c.Name == "notes"))
            .IsFalse();

        // Folder groups pick up their children's length once the media has been read, at every level.
        bool groupsTimed = await EngineHarness.UntilAsync(
            () => nestedCue.Duration > 0.15 && act1Cue.Duration > 0.15 && act2Cue.Duration > 0.15 && show.Duration > 0.15,
            5000);
        AssertThat(groupsTimed)
            .OverrideFailureMessage($"Group durations stayed stale: Show {show.Duration}, Act1 {act1Cue.Duration}, Nested {nestedCue.Duration}, Act2 {act2Cue.Duration}")
            .IsTrue();
        EngineHarness.AssertNoNewErrors(mark);
    }

    /// <summary>
    /// A large folder import is built over several frames as one bulk step, every cue gets its
    /// length, and one Undo removes the whole import.
    /// </summary>
    [TestCase(Timeout = 60000)]
    public async Task LargeFolderImportRunsOverFramesAndUndoesInOneStep()
    {
        int mark = EngineHarness.ErrorMark();
        string bigDir = Path.Combine(EngineHarness.WorkDir, "folderdrop-large", "Big");
        string partA = Path.Combine(bigDir, "PartA");
        string partB = Path.Combine(bigDir, "PartB");
        Directory.CreateDirectory(partA);
        Directory.CreateDirectory(partB);
        const int perFolder = 30;
        for (int i = 0; i < perFolder; i++)
        {
            MediaFixtures.WriteToneWav(Path.Combine(partA, $"a{i:00}.wav"), 0.05, 220);
            MediaFixtures.WriteToneWav(Path.Combine(partB, $"b{i:00}.wav"), 0.05, 330);
        }

        var globs = GlobalData.AudioFileFilters
            .Concat(GlobalData.VideoFileFilters)
            .Concat(GlobalData.ImageFileFilters);
        var expansion = DroppedMediaExpansion.Expand(new[] { bigDir }, globs);
        AssertThat(expansion.FileCount).IsEqual(perFolder * 2);
        AssertThat(expansion.IsLargeImport).IsTrue();

        var list = EngineHarness.Data.Cuelist;
        list.CreateCuesFromDroppedFiles(expansion, -1, DropInsertMode.AtEnd, MultiFileDropMode.PreserveFolderTree);
        AssertThat(list.IsBulkOpInProgress)
            .OverrideFailureMessage("A large import should run as a bulk step over several frames")
            .IsTrue();

        bool ready = await EngineHarness.UntilAsync(() =>
        {
            if (list.IsBulkOpInProgress)
                return false;
            var media = EngineHarness.AllCues().Where(c => c.Components.Count > 0).ToList();
            return media.Count == perFolder * 2 && media.All(c => c.Duration > 0);
        }, 45000);
        AssertThat(ready).OverrideFailureMessage("Large folder import did not finish with every length known").IsTrue();
        AssertThat(list.RootOrder.Count).IsEqual(1);
        AssertThat(EngineHarness.AllCues().Length).IsEqual(perFolder * 2 + 3);

        EngineHarness.Data.HistoryManager.Undo();
        bool undone = await EngineHarness.UntilAsync(() => EngineHarness.AllCues().Length == 0, 5000);
        AssertThat(undone).OverrideFailureMessage("One Undo should remove the whole import").IsTrue();
        EngineHarness.AssertNoNewErrors(mark);
    }
}
