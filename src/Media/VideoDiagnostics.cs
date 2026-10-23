// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System;
using System.Diagnostics;
using System.Threading;
using Godot;
using SDL3;

namespace Cue2.Media;

/// <summary>
/// One-time video host log, optional per-second stats switch, and editor monitors.
/// Per-second lines are off unless the process is started with <c>--video-stats</c>
/// (a Godot user argument after <c>--</c>, or an engine argument).
/// </summary>
public static class VideoDiagnostics
{
    private static bool _configured;
    private static bool _perSecond;
    private static bool _hostLogged;
    private static bool _monitorsRegistered;
    private static long _lastPresentLoopTicks;
    private static long _lastUploadTicks;
    private static long _presentedTotal;

    /// <summary>True when <c>--video-stats</c> was passed on the command line.</summary>
    public static bool PerSecondEnabled
    {
        get
        {
            ConfigureFromCommandLine();
            return _perSecond;
        }
    }

    /// <summary>
    /// Logs the renderer, GPU, CPU count, and Godot/SDL screen counts once.
    /// Registers the editor monitors used by the video present path.
    /// </summary>
    public static void LogHostOnce()
    {
        if (_hostLogged)
            return;
        _hostLogged = true;
        ConfigureFromCommandLine();
        RegisterMonitors();

        string renderer = Safe(() => RenderingServer.GetCurrentRenderingMethod());
        string driver = Safe(() => RenderingServer.GetCurrentRenderingDriverName());
        string gpu = Safe(() => RenderingServer.GetVideoAdapterName());
        string vendor = Safe(() => RenderingServer.GetVideoAdapterVendor());
        string api = Safe(() => RenderingServer.GetVideoAdapterApiVersion());
        int cpus = 0;
        try { cpus = OS.GetProcessorCount(); } catch { /* host log is best-effort */ }

        int godotScreens = -1;
        int sdlScreens = -1;
        try { godotScreens = DisplayServer.GetScreenCount(); } catch { /* ignore */ }
        try
        {
            SDL.GetDisplays(out int sdlCount);
            sdlScreens = sdlCount;
        }
        catch
        {
            // SDL video may not be up yet. The count stays -1.
        }

        GD.Print(
            "VideoDiagnostics:LogHost - " +
            $"renderer={renderer} driver={driver} gpu=\"{gpu}\" vendor=\"{vendor}\" api=\"{api}\" " +
            $"cpus={cpus} screens godot={godotScreens} sdl={sdlScreens} " +
            $"perSecondStats={(_perSecond ? "on" : "off")}");
    }

    /// <summary>Records the last present-loop interval for the editor monitor.</summary>
    public static void NotePresentLoop(long ticks)
    {
        if (ticks < 0)
            ticks = 0;
        Interlocked.Exchange(ref _lastPresentLoopTicks, ticks);
    }

    /// <summary>
    /// Called when a cue's present loop begins. Starts the totals of a new engine frame on
    /// the first call of that frame. Main thread only.
    /// </summary>
    public static void BeginCueTick(long loopStartTicks) => RollFrame(loopStartTicks);

    /// <summary>Adds one cue's present loop to the totals of the current engine frame. Main thread only.</summary>
    public static void NoteCueTick(long elapsedTicks)
    {
        if (elapsedTicks < 0)
            elapsedTicks = 0;
        _frame.PresentTicks += elapsedTicks;
        _frame.Cues++;
        Volatile.Write(ref _lastVideoTickTicks, Stopwatch.GetTimestamp());
    }

    /// <summary>Records the last image upload interval for the editor monitor.</summary>
    public static void NoteUpload(long setDataTicks, long updateTicks)
    {
        long sum = setDataTicks + updateTicks;
        if (sum < 0)
            sum = 0;
        Interlocked.Exchange(ref _lastUploadTicks, sum);
        // Uploads run on the main thread, inside a present loop.
        if (_frame.Id != ulong.MaxValue)
        {
            _frame.SetDataTicks += Math.Max(0, setDataTicks);
            _frame.UpdateTicks += Math.Max(0, updateTicks);
            _frame.Uploads++;
        }
    }

    /// <summary>
    /// True when a video cue ran its present loop within the last
    /// <paramref name="seconds"/>. Any thread.
    /// </summary>
    public static bool VideoPlayedWithin(double seconds)
    {
        long last = Volatile.Read(ref _lastVideoTickTicks);
        if (last == 0)
            return false;
        return (Stopwatch.GetTimestamp() - last) < seconds * Stopwatch.Frequency;
    }

    /// <summary>
    /// Average engine frame interval (milliseconds) over about the last second of video
    /// playback, or 0 when no video played recently. Main thread only.
    /// </summary>
    public static double RecentFrameMs => VideoPlayedWithin(0.5) ? _frameMsAverage : 0;

    // Totals of the engine frame now running, summed over every playing video cue.
    private struct FrameTotals
    {
        public ulong Id;
        public long StartTicks;
        public long PresentTicks;
        public long SetDataTicks;
        public long UpdateTicks;
        public int Uploads;
        public int Cues;
    }

    private static FrameTotals _frame = new FrameTotals { Id = ulong.MaxValue };
    private static long _lastVideoTickTicks;
    private static double _frameMsAverage;

    // Per-second sums of whole engine frames (main thread only).
    private static int _secFrames;
    private static long _secIntervalTicks;
    private static long _secIntervalMaxTicks;
    private static long _secPresentTicks;
    private static long _secPresentMaxTicks;
    private static long _secSetDataTicks;
    private static long _secUpdateTicks;
    private static int _secUploads;
    private static int _secUploadsMax;
    private static int _secCuesMax;
    private static double _secProcessMs;
    private static double _secProcessMaxMs;
    private static ulong _secNextLogMs;
    private static int _secGen2Start = -1;
    private static TimeSpan _secGcPauseStart;

    /// <summary>
    /// Starts the totals of a new engine frame when the frame number changed, and folds
    /// the finished frame into the per-second line. Main thread only.
    /// </summary>
    private static void RollFrame(long nowTicks)
    {
        ulong id = Engine.GetProcessFrames();
        if (id == _frame.Id)
            return;

        if (_frame.Id != ulong.MaxValue)
        {
            long interval = nowTicks - _frame.StartTicks;
            // A gap over a quarter second is a pause or a seek, not a slow frame.
            if (interval > 0 && interval < Stopwatch.Frequency / 4)
            {
                _secFrames++;
                _secIntervalTicks += interval;
                if (interval > _secIntervalMaxTicks)
                    _secIntervalMaxTicks = interval;
                _secPresentTicks += _frame.PresentTicks;
                if (_frame.PresentTicks > _secPresentMaxTicks)
                    _secPresentMaxTicks = _frame.PresentTicks;
                _secSetDataTicks += _frame.SetDataTicks;
                _secUpdateTicks += _frame.UpdateTicks;
                _secUploads += _frame.Uploads;
                if (_frame.Uploads > _secUploadsMax)
                    _secUploadsMax = _frame.Uploads;
                if (_frame.Cues > _secCuesMax)
                    _secCuesMax = _frame.Cues;
                double processMs = Performance.GetMonitor(Performance.Monitor.TimeProcess) * 1000.0;
                _secProcessMs += processMs;
                if (processMs > _secProcessMaxMs)
                    _secProcessMaxMs = processMs;
                double ms = TicksToMs(interval);
                _frameMsAverage = _frameMsAverage <= 0 ? ms : _frameMsAverage + (ms - _frameMsAverage) * 0.05;
            }
        }

        _frame = new FrameTotals { Id = id, StartTicks = nowTicks };
        MaybeLogFrameStats();
    }

    private static void MaybeLogFrameStats()
    {
        ulong now = Time.GetTicksMsec();
        if (_secGen2Start < 0)
        {
            _secGen2Start = GC.CollectionCount(2);
            _secGcPauseStart = GC.GetTotalPauseDuration();
            _secNextLogMs = now + 1000;
        }
        if (now < _secNextLogMs)
            return;
        _secNextLogMs = now + 1000;

        int gen2 = GC.CollectionCount(2);
        TimeSpan pause = GC.GetTotalPauseDuration();
        if (PerSecondEnabled && _secFrames > 0)
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            GD.Print(
                "VideoDiagnostics:Frames - " +
                $"frames={_secFrames} cues={_secCuesMax} " +
                $"frameAvgMs={FormatAvgMs(_secIntervalTicks, _secFrames)} frameMaxMs={FormatMs(_secIntervalMaxTicks)} " +
                $"videoMainAvgMs={FormatAvgMs(_secPresentTicks, _secFrames)} videoMainMaxMs={FormatMs(_secPresentMaxTicks)} " +
                $"uploadsPerFrame={(_secUploads / (double)_secFrames).ToString("0.00", inv)} uploadsMax={_secUploadsMax} " +
                $"setDataPerFrameMs={FormatAvgMs(_secSetDataTicks, _secFrames)} updatePerFrameMs={FormatAvgMs(_secUpdateTicks, _secFrames)} " +
                $"processAvgMs={(_secProcessMs / _secFrames).ToString("0.00", inv)} processMaxMs={_secProcessMaxMs.ToString("0.00", inv)} " +
                $"gen2={gen2 - _secGen2Start} gcPauseMs={(pause - _secGcPauseStart).TotalMilliseconds.ToString("0.0", inv)} " +
                $"uploadWaits={_secUploadWaits}");
        }

        _secGen2Start = gen2;
        _secGcPauseStart = pause;
        _secFrames = 0;
        _secIntervalTicks = 0;
        _secIntervalMaxTicks = 0;
        _secPresentTicks = 0;
        _secPresentMaxTicks = 0;
        _secSetDataTicks = 0;
        _secUpdateTicks = 0;
        _secUploads = 0;
        _secUploadsMax = 0;
        _secCuesMax = 0;
        _secProcessMs = 0;
        _secProcessMaxMs = 0;
        _secUploadWaits = 0;
    }

    private static int _secUploadWaits;

    /// <summary>Counts one picture upload put off a frame by the shared upload budget. Main thread only.</summary>
    public static void NoteUploadWaited() => _secUploadWaits++;

    /// <summary>Counts one uploaded frame across all playing videos.</summary>
    public static void NotePresented()
    {
        Interlocked.Increment(ref _presentedTotal);
    }

    /// <summary>Converts stopwatch ticks to milliseconds.</summary>
    public static double TicksToMs(long ticks)
    {
        if (ticks <= 0)
            return 0;
        return ticks * 1000.0 / Stopwatch.Frequency;
    }

    /// <summary>Average milliseconds, or 0 when <paramref name="count"/> is 0.</summary>
    public static string FormatAvgMs(long sumTicks, long count)
    {
        if (count <= 0)
            return "0.00";
        return (TicksToMs(sumTicks) / count).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Milliseconds for one interval.</summary>
    public static string FormatMs(long ticks)
    {
        return TicksToMs(ticks).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
    }

    private static void ConfigureFromCommandLine()
    {
        if (_configured)
            return;
        _configured = true;
        _perSecond = HasFlag("--video-stats");
    }

    private static bool HasFlag(string flag)
    {
        try
        {
            foreach (string arg in OS.GetCmdlineUserArgs() ?? Array.Empty<string>())
            {
                if (string.Equals(arg, flag, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            foreach (string arg in OS.GetCmdlineArgs() ?? Array.Empty<string>())
            {
                if (string.Equals(arg, flag, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        catch
        {
            return false;
        }

        return false;
    }

    private static void RegisterMonitors()
    {
        if (_monitorsRegistered)
            return;
        _monitorsRegistered = true;
        TryAdd("cue2/video/present_loop_ms", () => TicksToMs(Interlocked.Read(ref _lastPresentLoopTicks)));
        TryAdd("cue2/video/upload_ms", () => TicksToMs(Interlocked.Read(ref _lastUploadTicks)));
        TryAdd("cue2/video/presented", () => (double)Interlocked.Read(ref _presentedTotal));
        TryAdd("cue2/video/frame_avg_ms", () => RecentFrameMs);
    }

    private static void TryAdd(string id, Func<double> read)
    {
        try
        {
            if (Performance.HasCustomMonitor(id))
                return;
            Performance.AddCustomMonitor(id, Callable.From(read));
        }
        catch (Exception ex)
        {
            GD.PrintErr($"VideoDiagnostics:RegisterMonitors - {id}: {ex.Message}");
        }
    }

    private static string Safe(Func<string> read)
    {
        try
        {
            string value = read();
            return string.IsNullOrEmpty(value) ? "unknown" : value;
        }
        catch
        {
            return "unknown";
        }
    }
}
