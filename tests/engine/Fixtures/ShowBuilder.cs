// SPDX-FileCopyrightText: 2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Cue2.Domain.Cuelist;
using Cue2.UI.Popups;

namespace Cue2.EngineTests.Fixtures;

/// <summary>
/// Builds test shows the way an operator does: by dropping generated media files onto the cue list.
/// </summary>
internal static class ShowBuilder
{
    /// <summary>Writes <paramref name="count"/> tone files of <paramref name="seconds"/> each.</summary>
    /// <returns>Absolute file paths.</returns>
    internal static string[] ToneFiles(int count, double seconds = 2.0, string prefix = "tone")
    {
        var files = new string[count];
        for (int i = 0; i < count; i++)
        {
            string path = Path.Combine(EngineHarness.WorkDir, "media", $"{prefix}{i + 1:00}.wav");
            if (!File.Exists(path))
                MediaFixtures.WriteToneWav(path, seconds, 220 + 40 * i);
            files[i] = path;
        }
        return files;
    }

    /// <summary>Writes a short colour video file.</summary>
    /// <returns>Absolute file path.</returns>
    internal static string VideoFile(string name = "colours.y4m", int frames = 50)
    {
        string path = Path.Combine(EngineHarness.WorkDir, "media", name);
        if (!File.Exists(path))
            MediaFixtures.WriteColourY4m(path, frames: frames);
        return path;
    }

    /// <summary>
    /// Drops <paramref name="files"/> at the end of the cue list and waits until every new media cue knows its length.
    /// </summary>
    /// <param name="files">Media files to drop.</param>
    /// <param name="mode">How several files become cues.</param>
    /// <param name="timeoutMs">Longest wait for the media to be probed.</param>
    /// <returns>New cues in creation order (a group comes before its children).</returns>
    internal static async Task<List<Cue>> DropAsync(
        string[] files,
        MultiFileDropMode mode = MultiFileDropMode.SeparateCues,
        int timeoutMs = 15000)
    {
        var before = new HashSet<int>(EngineHarness.AllCues().Select(c => c.Id));
        EngineHarness.Data.Cuelist.CreateCuesFromDroppedFiles(files, -1, DropInsertMode.AtEnd, mode);

        List<Cue> added = null;
        bool ready = await EngineHarness.UntilAsync(() =>
        {
            added = EngineHarness.AllCues().Where(c => !before.Contains(c.Id)).ToList();
            var media = added.Where(c => c.Components.Count > 0).ToList();
            return media.Count == files.Length && media.All(c => c.Duration > 0);
        }, timeoutMs);
        if (!ready)
            throw new TimeoutException($"ShowBuilder:DropAsync - {files.Length} file(s) did not become ready cues in {timeoutMs} ms");
        return added;
    }

    /// <summary>Links each cue to the next with <paramref name="follow"/> (the last cue keeps None).</summary>
    internal static void Chain(IReadOnlyList<Cue> cues, FollowType follow, double postWait = 0)
    {
        for (int i = 0; i < cues.Count - 1; i++)
        {
            cues[i].Follow = follow;
            cues[i].PostWait = postWait;
        }
    }
}
