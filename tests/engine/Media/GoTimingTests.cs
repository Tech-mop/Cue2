// SPDX-FileCopyrightText: 2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Cue2.Domain.Cuelist;
using Cue2.Domain.Playback;
using Cue2.EngineTests.Fixtures;
using Cue2.Services;
using GdUnit4;
using Godot;
using static GdUnit4.Assertions;

namespace Cue2.EngineTests.Media;

/// <summary>
/// GO timing as numbers: each GO is repeated, every timing point goes to timing.csv, and loose
/// budgets catch only large slow-downs.
/// </summary>
/// <remarks>
/// Budgets are medians and well above the times measured headless (GO handler a few ms, content
/// and first picture well under 100 ms), so only a large regression fails.
/// </remarks>
[TestSuite]
[RequireGodotRuntime]
public class GoTimingTests
{
    private const int Repeats = 5;
    private const double GoHandlerBudgetMs = 100;
    private const double ContentBudgetMs = 150;
    private const double FirstOutputBudgetMs = 250;

    private float _savedStopFade;

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
        _savedStopFade = EngineHarness.Data.Settings.StopFadeDuration;
        EngineHarness.Data.Settings.StopFadeDuration = 0.05f;
    }

    /// <summary>Puts the stop fade back.</summary>
    [AfterTest]
    public async Task Restore()
    {
        EngineHarness.Data.Settings.StopFadeDuration = _savedStopFade;
        await EngineHarness.ResetShowAsync();
    }

    /// <summary>GO on an audio cue: handler, content start and first queued audio within budget.</summary>
    [TestCase(Timeout = 120000)]
    public async Task AudioGoTiming()
    {
        int mark = EngineHarness.ErrorMark();
        var cue = (await ShowBuilder.DropAsync(ShowBuilder.ToneFiles(1, 2.0, "timing"))).Single();
        var samples = await RepeatGoAsync(cue, PlaybackTimingRecorder.StageAudioQueued);
        EngineHarness.AppendTiming(nameof(AudioGoTiming), samples);

        AssertWithin(samples, PlaybackTimingRecorder.StageGoHandler, GoHandlerBudgetMs);
        AssertWithin(samples, PlaybackTimingRecorder.StageContentStarted, ContentBudgetMs);
        AssertWithin(samples, PlaybackTimingRecorder.StageAudioQueued, FirstOutputBudgetMs);
        EngineHarness.AssertNoNewErrors(mark);
    }

    /// <summary>GO on a video cue: handler, content start and first picture within budget.</summary>
    [TestCase(Timeout = 120000)]
    public async Task VideoGoTiming()
    {
        int mark = EngineHarness.ErrorMark();
        var cue = (await ShowBuilder.DropAsync(new[] { ShowBuilder.VideoFile("timing.y4m", 75) })).Single();
        var samples = await RepeatGoAsync(cue, PlaybackTimingRecorder.StageFirstPicture);
        EngineHarness.AppendTiming(nameof(VideoGoTiming), samples);

        AssertWithin(samples, PlaybackTimingRecorder.StageGoHandler, GoHandlerBudgetMs);
        AssertWithin(samples, PlaybackTimingRecorder.StageContentStarted, ContentBudgetMs);
        AssertWithin(samples, PlaybackTimingRecorder.StageFirstPicture, FirstOutputBudgetMs);
        EngineHarness.AssertNoNewErrors(mark);
    }

    /// <summary>GOs <paramref name="cue"/> <see cref="Repeats"/> times and returns every timing point.</summary>
    private static async Task<List<PlaybackTimingRecorder.Sample>> RepeatGoAsync(Cue cue, string untilStage)
    {
        var all = new List<PlaybackTimingRecorder.Sample>();
        var executor = EngineHarness.Data.CueCommandExecutor;
        for (int i = 0; i < Repeats; i++)
        {
            EngineHarness.Data.ShellSelection.SelectIndividualShell(cue, false);
            await EngineHarness.FramesAsync(5);
            PlaybackTimingRecorder.Clear();
            EngineHarness.Signals.EmitSignal(GlobalSignals.SignalName.Go);
            bool reached = await EngineHarness.UntilAsync(
                () => PlaybackTimingRecorder.Snapshot().Any(s => s.Stage == untilStage), 5000);
            AssertThat(reached).OverrideFailureMessage($"No '{untilStage}' within 5 s of GO {i + 1}").IsTrue();
            all.AddRange(PlaybackTimingRecorder.Snapshot());

            EngineHarness.Signals.EmitSignal(GlobalSignals.SignalName.StopAll);
            await EngineHarness.UntilAsync(() => executor.ActiveCues.Count == 0, 5000);
        }
        return all;
    }

    private static void AssertWithin(List<PlaybackTimingRecorder.Sample> samples, string stage, double budgetMs)
    {
        var times = samples
            .Where(s => s.Stage == stage)
            .Select(s => s.MsAfterGo)
            .Where(ms => !double.IsNaN(ms))
            .OrderBy(ms => ms)
            .ToArray();
        AssertThat(times.Length).OverrideFailureMessage($"No '{stage}' timing recorded").IsGreaterEqual(Repeats);
        double median = times[times.Length / 2];
        GD.Print($"GoTimingTests:AssertWithin - {stage}: median {median:F1} ms, max {times[^1]:F1} ms " +
                 $"over {times.Length} (budget {budgetMs:F0} ms)");
        AssertThat(median).OverrideFailureMessage($"{stage} median {median:F1} ms is over the {budgetMs:F0} ms budget").IsLessEqual(budgetMs);
    }
}
