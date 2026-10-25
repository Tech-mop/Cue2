// SPDX-FileCopyrightText: 2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Cue2.Domain.Cuelist;
using Cue2.Domain.Playback;
using Cue2.Domain.ShowSettings;
using Cue2.EngineTests.Fixtures;
using Cue2.Services;
using GdUnit4;
using Godot;
using static GdUnit4.Assertions;

namespace Cue2.EngineTests.Media;

/// <summary>
/// GO, Stop All, multi-select GO with cancelled starts, sequences, standby and video start.
/// </summary>
[TestSuite]
[RequireGodotRuntime]
public class GoAndStopTests
{
    private float _savedStopFade;
    private PreloadStandbyMode _savedPreload;

    /// <summary>Loads the main window once for this suite.</summary>
    [Before]
    public async Task Boot()
    {
        await EngineHarness.BootAsync();
        AssertThat(EngineHarness.MediaAvailable).OverrideFailureMessage("FFmpeg did not load; media tests cannot run").IsTrue();
    }

    /// <summary>Starts each test from an empty show with a short stop fade.</summary>
    [BeforeTest]
    public async Task Reset()
    {
        await EngineHarness.ResetShowAsync();
        var settings = EngineHarness.Data.Settings;
        _savedStopFade = settings.StopFadeDuration;
        _savedPreload = settings.PreloadStandby;
        settings.StopFadeDuration = 0.2f;
        settings.PreloadStandby = PreloadStandbyMode.Off;
    }

    /// <summary>Puts the settings back and stops everything.</summary>
    [AfterTest]
    public async Task Restore()
    {
        var settings = EngineHarness.Data.Settings;
        settings.StopFadeDuration = _savedStopFade;
        settings.PreloadStandby = _savedPreload;
        await EngineHarness.ResetShowAsync();
    }

    private static CueCommandExecutorView Executor => new(EngineHarness.Data.CueCommandExecutor);

    /// <summary>GO starts a cue within three frames; Stop All empties the list after the stop fade.</summary>
    [TestCase(Timeout = 60000)]
    public async Task GoThenStopAll()
    {
        int mark = EngineHarness.ErrorMark();
        var cue = (await ShowBuilder.DropAsync(ShowBuilder.ToneFiles(1, 3.0, "go"))).Single();
        EngineHarness.Data.ShellSelection.SelectIndividualShell(cue, false);
        await EngineHarness.FramesAsync(2);

        EngineHarness.Signals.EmitSignal(GlobalSignals.SignalName.Go);
        int frames = 0;
        while (Executor.Count == 0 && frames < 30)
        {
            await EngineHarness.FramesAsync(1);
            frames++;
        }
        GD.Print($"GoAndStopTests:GoThenStopAll - active after {frames} frame(s)");
        AssertThat(frames).IsLessEqual(3);

        await EngineHarness.FramesAsync(10);
        var clock = Stopwatch.StartNew();
        EngineHarness.Signals.EmitSignal(GlobalSignals.SignalName.StopAll);
        AssertThat(await EngineHarness.UntilAsync(() => Executor.Count == 0, 5000)).IsTrue();
        long stopMs = clock.ElapsedMilliseconds;
        GD.Print($"GoAndStopTests:GoThenStopAll - all stopped {stopMs} ms after Stop All");
        // Stop fade (200 ms) plus 100 ms, plus 100 ms of slack for slow CI machines.
        AssertThat(stopMs).IsLessEqual(200 + 100 + 100);
        EngineHarness.AssertNoNewErrors(mark);
    }

    /// <summary>
    /// GO on thirteen top-level cues, Stop All on the third frame: nothing starts afterwards and the
    /// log says how many starts were cancelled.
    /// </summary>
    [TestCase(Timeout = 60000)]
    public async Task StopAllCancelsPendingStarts()
    {
        int mark = EngineHarness.ErrorMark();
        var cues = await ShowBuilder.DropAsync(ShowBuilder.ToneFiles(13, 2.0, "many"));
        EngineHarness.Data.ShellSelection.SelectAllShells(false);
        AssertThat(ShellSelection.SelectedCues.Count).IsEqual(13);
        await EngineHarness.FramesAsync(2);

        int log = EngineHarness.LogMark();
        EngineHarness.Signals.EmitSignal(GlobalSignals.SignalName.Go);
        await EngineHarness.FramesAsync(3);
        var startedBeforeStop = new HashSet<int>(Executor.CueIds());
        EngineHarness.Signals.EmitSignal(GlobalSignals.SignalName.StopAll);

        var lateStarts = new HashSet<int>();
        for (int i = 0; i < 120; i++)
        {
            await EngineHarness.FramesAsync(1);
            foreach (int id in Executor.CueIds())
            {
                if (!startedBeforeStop.Contains(id))
                    lateStarts.Add(id);
            }
        }
        AssertThat(lateStarts.Count).OverrideFailureMessage($"Cues started after Stop All: {string.Join(", ", lateStarts)}").IsEqual(0);
        AssertThat(Executor.Count).IsEqual(0);

        string line = await EngineHarness.WaitForLogAsync(log, "start cancelled;", 2000);
        AssertThat(line).OverrideFailureMessage("No 'start cancelled' log line").IsNotNull();
        var match = Regex.Match(line, @"start cancelled; (\d+) of 13 top-level cues were not started");
        AssertThat(match.Success).IsTrue();
        int cancelled = int.Parse(match.Groups[1].Value);
        GD.Print($"GoAndStopTests:StopAllCancelsPendingStarts - {startedBeforeStop.Count} started, {cancelled} cancelled");
        AssertThat(cancelled).IsGreater(0);
        AssertThat(cancelled + startedBeforeStop.Count).IsLessEqual(13);
        EngineHarness.AssertNoNewErrors(mark);
    }

    /// <summary>An auto-follow sequence plays its cues one after another, in order.</summary>
    [TestCase(Timeout = 60000)]
    public async Task FollowSequencePlaysInOrder()
    {
        int mark = EngineHarness.ErrorMark();
        var cues = await ShowBuilder.DropAsync(ShowBuilder.ToneFiles(3, 0.4, "follow"));
        ShowBuilder.Chain(cues, FollowType.Follow);
        var starts = await GoAndRecordStartsAsync(cues[0], cues.Count, 10000);

        AssertThat(starts.Keys.ToArray()).IsEqual(cues.Select(c => c.Id).ToArray());
        double[] times = cues.Select(c => starts[c.Id]).ToArray();
        GD.Print($"GoAndStopTests:FollowSequencePlaysInOrder - content started at {string.Join(", ", times.Select(t => $"{t:F0}"))} ms");
        // Each follow waits for the previous 400 ms tone to end.
        AssertThat(times[1] - times[0]).IsGreater(300.0);
        AssertThat(times[2] - times[1]).IsGreater(300.0);
        EngineHarness.AssertNoNewErrors(mark);
    }

    /// <summary>An auto-continue sequence starts its cues together, in order.</summary>
    [TestCase(Timeout = 60000)]
    public async Task ContinueSequenceStartsTogether()
    {
        int mark = EngineHarness.ErrorMark();
        var cues = await ShowBuilder.DropAsync(ShowBuilder.ToneFiles(3, 2.0, "continue"));
        ShowBuilder.Chain(cues, FollowType.Continue);
        var starts = await GoAndRecordStartsAsync(cues[0], cues.Count, 10000);

        // A continue starts the next cue from inside the previous cue's start, so cues can be
        // seen in the same instant in either order; all three must start, close together.
        AssertThat(starts.Count).IsEqual(3);
        double[] times = cues.Select(c => starts[c.Id]).ToArray();
        GD.Print($"GoAndStopTests:ContinueSequenceStartsTogether - content started at {string.Join(", ", times.Select(t => $"{t:F0}"))} ms");
        AssertThat(times[2] - times[0]).IsLess(1000.0);
        EngineHarness.AssertNoNewErrors(mark);
    }

    /// <summary>
    /// With standby preload on, the playhead and its standby move to the next cue after GO, past a
    /// disarmed cue set to skip, and stay on the last cue at the end of the list.
    /// </summary>
    [TestCase(Timeout = 60000)]
    public async Task StandbyFollowsThePlayhead()
    {
        int mark = EngineHarness.ErrorMark();
        var cues = await ShowBuilder.DropAsync(ShowBuilder.ToneFiles(3, 1.0, "standby"));
        cues[1].Armed = false;
        cues[1].SkipIfDisarmed = true;
        EngineHarness.Data.Settings.PreloadStandby = PreloadStandbyMode.EditAndShowMode;

        int log = EngineHarness.LogMark();
        EngineHarness.Data.ShellSelection.SelectIndividualShell(cues[0], false);
        AssertThat(await EngineHarness.WaitForLogAsync(log, $"\"{cues[0].Name}\" ready in", 5000))
            .OverrideFailureMessage("No standby for the first cue").IsNotNull();

        log = EngineHarness.LogMark();
        EngineHarness.Signals.EmitSignal(GlobalSignals.SignalName.Go);
        await EngineHarness.FramesAsync(2);
        AssertThat(ShellSelection.SelectedCues.Single().Id).IsEqual(cues[2].Id);
        AssertThat(await EngineHarness.WaitForLogAsync(log, $"\"{cues[2].Name}\" ready in", 8000))
            .OverrideFailureMessage("Standby did not move to the third cue").IsNotNull();
        AssertThat(EngineHarness.LogSince(log).Any(line => line.Contains($"preloading \"{cues[1].Name}\"", System.StringComparison.Ordinal)))
            .OverrideFailureMessage("The disarmed cue was preloaded").IsFalse();

        EngineHarness.Signals.EmitSignal(GlobalSignals.SignalName.Go);
        await EngineHarness.FramesAsync(2);
        AssertThat(ShellSelection.SelectedCues.Single().Id).IsEqual(cues[2].Id);
        EngineHarness.AssertNoNewErrors(mark);
    }

    /// <summary>A video cue shows its first picture soon after GO.</summary>
    [TestCase(Timeout = 60000)]
    public async Task VideoShowsFirstPicture()
    {
        int mark = EngineHarness.ErrorMark();
        var cue = (await ShowBuilder.DropAsync(new[] { ShowBuilder.VideoFile("first-picture.y4m", 75) })).Single();
        EngineHarness.Data.ShellSelection.SelectIndividualShell(cue, false);
        await EngineHarness.FramesAsync(2);

        int log = EngineHarness.LogMark();
        EngineHarness.Signals.EmitSignal(GlobalSignals.SignalName.Go);
        string line = await EngineHarness.WaitForLogAsync(log, $"PlaybackStartTrace:NotePicture - Start timing {cue.Name} ", 5000);
        AssertThat(line).OverrideFailureMessage("No first picture logged within 5 s").IsNotNull();
        GD.Print($"GoAndStopTests:VideoShowsFirstPicture - {line.Trim()}");
        EngineHarness.AssertNoNewErrors(mark);
    }

    /// <summary>
    /// GOs <paramref name="head"/> and records when each cue's content phase starts, in milliseconds after GO.
    /// </summary>
    private static async Task<Dictionary<int, double>> GoAndRecordStartsAsync(Cue head, int expected, int timeoutMs)
    {
        var starts = new Dictionary<int, double>();
        var watched = new HashSet<ActiveCue>();
        var handlers = new List<(ActiveCue Active, System.Action Handler)>();
        var clock = new Stopwatch();

        void Watch()
        {
            foreach (var active in EngineHarness.Data.CueCommandExecutor.ActiveCues.ToList())
            {
                if (active == null || !watched.Add(active))
                    continue;
                int id = active.Cue.Id;
                System.Action handler = () =>
                {
                    if (!starts.ContainsKey(id))
                        starts[id] = clock.Elapsed.TotalMilliseconds;
                };
                active.ContentPhaseStarted += handler;
                handlers.Add((active, handler));
            }
        }

        try
        {
            EngineHarness.Data.ShellSelection.SelectIndividualShell(head, false);
            await EngineHarness.FramesAsync(2);
            clock.Start();
            EngineHarness.Signals.EmitSignal(GlobalSignals.SignalName.Go);
            Watch();
            await EngineHarness.UntilAsync(() =>
            {
                Watch();
                return starts.Count >= expected;
            }, timeoutMs);
        }
        finally
        {
            foreach (var (active, handler) in handlers)
            {
                if (GodotObject.IsInstanceValid(active))
                    active.ContentPhaseStarted -= handler;
            }
        }

        return starts.OrderBy(pair => pair.Value).ToDictionary(pair => pair.Key, pair => pair.Value);
    }
}
