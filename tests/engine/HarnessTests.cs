// SPDX-FileCopyrightText: 2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System.IO;
using System.Threading.Tasks;
using Cue2.EngineTests.Fixtures;
using GdUnit4;
using Godot;
using static GdUnit4.Assertions;

namespace Cue2.EngineTests;

/// <summary>
/// The engine test harness itself: isolation, a clean boot, and the generated media files.
/// </summary>
[TestSuite]
[RequireGodotRuntime]
public class HarnessTests
{
    /// <summary>Loads the main window once for this suite.</summary>
    [Before]
    public async Task Boot() => await EngineHarness.BootAsync();

    /// <summary>Godot's user data folder is the temporary one the run script made.</summary>
    [TestCase(Timeout = 20000)]
    public void UserDataIsIsolated()
    {
        EngineHarness.AssertIsolatedUserData();
        string root = System.Environment.GetEnvironmentVariable(EngineHarness.DataRootVariable);
        AssertThat(OS.GetUserDataDir().Replace('\\', '/')).StartsWith(Path.GetFullPath(root).Replace('\\', '/'));
    }

    /// <summary>
    /// Loading the main window reports no startup faults and no engine errors. When the run scripts
    /// found no native libraries (LFS pointers; media tests skipped), the library faults are expected
    /// and only printed.
    /// </summary>
    [TestCase(Timeout = 20000)]
    public void BootIsClean()
    {
        var faults = EngineHarness.Data.StartupFaults.Snapshot();
        string listed = string.Join("; ", System.Linq.Enumerable.Select(faults, f => $"{f.Subsystem}: {f.Detail}"));
        if (EngineHarness.NativesMissing)
            GD.Print($"HarnessTests:BootIsClean - Startup faults expected without native libraries: {listed}");
        else
            AssertThat(faults.Count).OverrideFailureMessage($"Startup faults: {listed}").IsEqual(0);
        AssertThat(EngineHarness.BootErrors.Length)
            .OverrideFailureMessage("Engine errors while loading:\n" + string.Join("\n", EngineHarness.BootErrors))
            .IsEqual(0);
    }

    /// <summary>The error check really sees engine errors (one is raised on purpose here).</summary>
    [TestCase(Timeout = 20000)]
    public void ErrorCheckSeesEngineErrors()
    {
        int mark = EngineHarness.ErrorMark();
        GD.PushError("HarnessTests:ErrorCheckSeesEngineErrors - expected test error, please ignore");
        AssertThat(EngineHarness.ErrorMark()).IsEqual(mark + 1);
    }

    /// <summary>GO with nothing selected does nothing and logs no engine error.</summary>
    [TestCase(Timeout = 20000)]
    public async Task GoWithEmptyShowIsHarmless()
    {
        await EngineHarness.ResetShowAsync();
        int mark = EngineHarness.ErrorMark();
        EngineHarness.Signals.EmitSignal(Cue2.Services.GlobalSignals.SignalName.Go);
        await EngineHarness.FramesAsync(5);
        AssertThat(EngineHarness.Data.CueCommandExecutor.ActiveCues.Count).IsEqual(0);
        EngineHarness.AssertNoNewErrors(mark);
    }

    /// <summary>Generated WAV and Y4M files have the expected sizes.</summary>
    [TestCase(Timeout = 20000)]
    public void GeneratedMediaHasExpectedSize()
    {
        string wav = MediaFixtures.WriteToneWav(Path.Combine(EngineHarness.WorkDir, "check", "one-second.wav"), 1.0);
        AssertThat(new FileInfo(wav).Length).IsEqual(44L + 48000 * 2 * 2);

        string y4m = MediaFixtures.WriteColourY4m(Path.Combine(EngineHarness.WorkDir, "check", "ten-frames.y4m"), 160, 90, 10, 25);
        long header = "YUV4MPEG2 W160 H90 F25:1 Ip A1:1 C420jpeg\n".Length;
        AssertThat(new FileInfo(y4m).Length).IsEqual(header + 10 * ("FRAME\n".Length + 160 * 90 * 3 / 2));
    }
}
