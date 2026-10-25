// SPDX-FileCopyrightText: 2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Cue2.Domain.Cuelist;
using Cue2.Domain.Playback;
using Cue2.Services;
using Godot;

namespace Cue2.EngineTests;

/// <summary>
/// Shared setup for engine tests: checks that user data is isolated, loads the main window once,
/// resets the show between tests and waits for frames.
/// </summary>
/// <remarks>
/// The GdUnit4 runner boots the autoloads but not the main scene, so the first suite loads
/// <c>Cue2Base.tscn</c> itself. The window stays loaded for the rest of the run.
/// </remarks>
internal static class EngineHarness
{
    /// <summary>Environment variable the run scripts set to the temporary user data folder.</summary>
    internal const string DataRootVariable = "CUE2_TEST_DATA_ROOT";

    /// <summary>Environment variable the run scripts set to 1 when the native libraries are LFS pointers.</summary>
    internal const string NoNativesVariable = "CUE2_TEST_NO_NATIVES";

    /// <summary>Environment variable the run scripts set to the folder for results such as timing.csv.</summary>
    internal const string ArtifactsVariable = "CUE2_TEST_ARTIFACTS";

    private static Node _main;
    private static ErrorCapture _errors;

    /// <summary>The running scene tree.</summary>
    internal static SceneTree Tree => (SceneTree)Engine.GetMainLoop();

    /// <summary>The GlobalData autoload.</summary>
    internal static GlobalData Data => Tree.Root.GetNode<GlobalData>("GlobalData");

    /// <summary>The GlobalSignals autoload.</summary>
    internal static GlobalSignals Signals => Tree.Root.GetNode<GlobalSignals>("GlobalSignals");

    /// <summary>The SaveManager autoload.</summary>
    internal static SaveManager Saves => Tree.Root.GetNode<SaveManager>("SaveManager");

    /// <summary>The MediaEngine autoload.</summary>
    internal static MediaEngine Media => Tree.Root.GetNode<MediaEngine>("MediaEngine");

    /// <summary>Folder for generated media and test shows, inside the temporary user data folder.</summary>
    internal static string WorkDir
    {
        get
        {
            string dir = Path.Combine(RequireDataRoot(), "work");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    /// <summary>
    /// Checks isolation, then loads the main window if it is not loaded yet and waits until it settles.
    /// </summary>
    internal static async Task BootAsync()
    {
        AssertIsolatedUserData();
        if (_main != null && GodotObject.IsInstanceValid(_main))
            return;

        _errors ??= ErrorCapture.Install();
        PlaybackTimingRecorder.Enabled = true;
        var scene = GD.Load<PackedScene>("res://src/App/Cue2Base.tscn");
        _main = scene.Instantiate();
        Tree.Root.AddChild(_main);
        Tree.CurrentScene = _main;
        await FramesAsync(60);
        if (!await Saves.WaitForSessionIdleAsync(15000))
            throw new InvalidOperationException("EngineHarness:BootAsync - The startup session did not finish loading.");
        BootErrors = _errors.Since(0);
        GD.Print($"EngineHarness:BootAsync - Main window loaded; user data in {OS.GetUserDataDir()}");
    }

    /// <summary>
    /// Stops everything, starts a new empty show and waits until it is idle.
    /// </summary>
    internal static async Task ResetShowAsync()
    {
        Signals.EmitSignal(GlobalSignals.SignalName.StopAll);
        var executor = Data.CueCommandExecutor;
        await UntilAsync(() => executor.ActiveCues.Count == 0, 5000);
        Saves.NewSessionWithoutPrompt();
        await Saves.WaitForSessionIdleAsync(10000);
        await FramesAsync(5);
    }

    /// <summary>
    /// Throws unless Godot's user data folder is inside the folder named by <see cref="DataRootVariable"/>.
    /// </summary>
    internal static void AssertIsolatedUserData()
    {
        string root = RequireDataRoot();
        string userDir = Normalize(OS.GetUserDataDir());
        string rootDir = Normalize(root);
        var comparison = OS.GetName() == "Windows" ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!userDir.StartsWith(rootDir + "/", comparison))
        {
            throw new InvalidOperationException(
                $"EngineHarness:AssertIsolatedUserData - User data is {userDir}, outside {rootDir}. " +
                "Run the engine tests through tests/run-tests.ps1 or tests/run-tests.sh.");
        }
    }

    /// <summary>Engine errors reported while the main window loaded.</summary>
    internal static string[] BootErrors { get; private set; } = Array.Empty<string>();

    /// <summary>Whether FFmpeg loaded, so media cues can be created and played.</summary>
    internal static bool MediaAvailable => Media.IsFFmpegAvailable;

    /// <summary>True when the run scripts found LFS pointers instead of the native libraries.</summary>
    internal static bool NativesMissing => System.Environment.GetEnvironmentVariable(NoNativesVariable) == "1";

    /// <summary>Position in the engine error stream, for <see cref="AssertNoNewErrors"/>.</summary>
    internal static int ErrorMark() => _errors?.Count ?? 0;

    /// <summary>Throws when the engine reported an error after <paramref name="mark"/>.</summary>
    /// <param name="mark">Value from <see cref="ErrorMark"/>.</param>
    internal static void AssertNoNewErrors(int mark)
    {
        var errors = _errors?.Since(mark) ?? Array.Empty<string>();
        if (errors.Length > 0)
            throw new InvalidOperationException("EngineHarness:AssertNoNewErrors - Engine errors:\n" + string.Join("\n", errors));
    }

    /// <summary>
    /// Adds timing points to <c>timing.csv</c> in the results folder (or the user data folder when
    /// no results folder is set), writing the header first when the file is new.
    /// </summary>
    internal static string AppendTiming(string test, System.Collections.Generic.IEnumerable<PlaybackTimingRecorder.Sample> samples)
    {
        string dir = System.Environment.GetEnvironmentVariable(ArtifactsVariable);
        if (string.IsNullOrEmpty(dir))
            dir = RequireDataRoot();
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "timing.csv");
        var lines = new System.Collections.Generic.List<string>();
        if (!File.Exists(path))
            lines.Add("test,label,stage,ms_after_go,ms_after_play");
        foreach (var sample in samples)
        {
            lines.Add(string.Join(",",
                Csv(test), Csv(sample.Label), Csv(sample.Stage),
                CsvNumber(sample.MsAfterGo), CsvNumber(sample.MsAfterPlay)));
        }
        File.AppendAllLines(path, lines);
        return path;
    }

    private static string Csv(string value)
    {
        value ??= string.Empty;
        return value.IndexOfAny(new[] { ',', '"', '\n', '\r' }) < 0 ? value : "\"" + value.Replace("\"", "\"\"") + "\"";
    }

    private static string CsvNumber(double value) =>
        double.IsNaN(value) ? string.Empty : value.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Position in the log, for <see cref="LogSince"/> and <see cref="WaitForLogAsync"/>.</summary>
    internal static int LogMark() => _errors?.MessageCount ?? 0;

    /// <summary>Log lines printed after <paramref name="mark"/>.</summary>
    internal static string[] LogSince(int mark) => _errors?.MessagesSince(mark) ?? Array.Empty<string>();

    /// <summary>
    /// Waits until a log line printed after <paramref name="mark"/> contains <paramref name="text"/>.
    /// </summary>
    /// <returns>The line, or null after the timeout.</returns>
    internal static async Task<string> WaitForLogAsync(int mark, string text, int timeoutMs)
    {
        string found = null;
        await UntilAsync(() =>
        {
            found = LogSince(mark).FirstOrDefault(line => line.Contains(text, StringComparison.Ordinal));
            return found != null;
        }, timeoutMs);
        return found;
    }

    /// <summary>Waits <paramref name="count"/> process frames.</summary>
    internal static async Task FramesAsync(int count)
    {
        var tree = Tree;
        for (int i = 0; i < count; i++)
            await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
    }

    /// <summary>Waits frame by frame until <paramref name="condition"/> holds or the timeout passes.</summary>
    /// <returns><see langword="true"/> when the condition held in time.</returns>
    internal static async Task<bool> UntilAsync(Func<bool> condition, int timeoutMs)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            if (clock.ElapsedMilliseconds > timeoutMs)
                return false;
            await FramesAsync(1);
        }
        return true;
    }

    /// <summary>Cues in the index, in creation order.</summary>
    internal static Cue[] AllCues() =>
        CueList.CueIndex?.Values.OrderBy(c => c.Id).ToArray() ?? Array.Empty<Cue>();

    private static string RequireDataRoot()
    {
        string root = System.Environment.GetEnvironmentVariable(DataRootVariable);
        if (string.IsNullOrWhiteSpace(root))
        {
            throw new InvalidOperationException(
                $"EngineHarness:RequireDataRoot - {DataRootVariable} is not set. Run the engine tests through " +
                "tests/run-tests.ps1 or tests/run-tests.sh so they never touch real user data.");
        }
        return root;
    }

    private static string Normalize(string path) =>
        Path.GetFullPath(path).Replace('\\', '/').TrimEnd('/');
}
