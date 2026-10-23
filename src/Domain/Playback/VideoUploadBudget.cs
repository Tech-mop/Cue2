// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System.Diagnostics;
using Godot;
using Cue2.Media;

namespace Cue2.Domain.Playback;

/// <summary>
/// Shares picture uploads between video cues when they no longer fit in a frame.
/// Off until frames run late (1.4 times the display interval or more) and the uploads
/// asked for in a frame add up to more than half a display interval. Then each cue that
/// uploaded last frame waits while the cues that waited last frame go first, so every
/// stream slows down evenly and none waits two frames in a row. Turns off again when
/// the uploads asked for drop (a cue stopped). Main thread only.
/// </summary>
public static class VideoUploadBudget
{
    private const double BehindFactor = 1.4;
    private const double BudgetShareOfFrame = 0.5;
    private const double ReleaseShare = 0.85;
    private const double DemandSmoothing = 0.1;

    private static ulong _frameId = ulong.MaxValue;
    private static long _spentTicks;
    private static long _reservedTicks;
    private static long _nextReservedTicks;
    private static long _demandTicks;
    private static int _requests;
    private static double _demandAverageTicks;
    private static bool _engaged;
    private static double _engagedDemandTicks;

    /// <summary>True while uploads are being shared.</summary>
    public static bool Engaged => _engaged;

    /// <summary>
    /// Asks to upload one picture costing about <paramref name="estimateTicks"/>.
    /// Returns false when this cue should wait a frame; it then keeps its pictures in the
    /// buffer and shows the newest one next frame. <paramref name="lastWaitFrame"/> is the
    /// cue's own record of the last frame it waited.
    /// </summary>
    public static bool TryUpload(long estimateTicks, ref ulong lastWaitFrame)
    {
        Roll();
        if (estimateTicks < 0)
            estimateTicks = 0;
        _demandTicks += estimateTicks;
        _requests++;
        if (!_engaged)
            return true;

        bool waitedLastFrame = lastWaitFrame != ulong.MaxValue && lastWaitFrame + 1 == _frameId;
        if (waitedLastFrame)
        {
            _reservedTicks -= estimateTicks;
            if (_reservedTicks < 0)
                _reservedTicks = 0;
            _spentTicks += estimateTicks;
            return true;
        }

        long budget = BudgetTicks();
        if ((_spentTicks > 0 || _reservedTicks > 0) && _spentTicks + estimateTicks + _reservedTicks > budget)
        {
            lastWaitFrame = _frameId;
            _nextReservedTicks += estimateTicks;
            VideoDiagnostics.NoteUploadWaited();
            return false;
        }

        _spentTicks += estimateTicks;
        return true;
    }

    private static void Roll()
    {
        ulong id = Engine.GetProcessFrames();
        if (id == _frameId)
            return;

        if (_frameId != ulong.MaxValue && _requests > 0)
        {
            _demandAverageTicks = _demandAverageTicks <= 0
                ? _demandTicks
                : _demandAverageTicks + (_demandTicks - _demandAverageTicks) * DemandSmoothing;
        }
        else if (_requests == 0)
        {
            _demandAverageTicks = 0;
        }

        long budget = BudgetTicks();
        if (!_engaged)
        {
            double frameMs = VideoDiagnostics.RecentFrameMs;
            if (_demandAverageTicks > budget
                && frameMs > DisplayIntervalMs() * BehindFactor
                && !PlaybackActivity.IsBusy(1.0))
            {
                _engaged = true;
                _engagedDemandTicks = _demandAverageTicks;
                GD.Print(
                    $"VideoUploadBudget:Roll - sharing picture uploads: {VideoDiagnostics.TicksToMs((long)_demandAverageTicks):F1} ms " +
                    $"asked per frame, budget {VideoDiagnostics.TicksToMs(budget):F1} ms, frames {frameMs:F1} ms.");
            }
        }
        else if (_demandAverageTicks < _engagedDemandTicks * ReleaseShare)
        {
            _engaged = false;
            GD.Print("VideoUploadBudget:Roll - uploads fit again; every cue uploads each frame.");
        }

        _frameId = id;
        _spentTicks = 0;
        _reservedTicks = _nextReservedTicks;
        _nextReservedTicks = 0;
        _demandTicks = 0;
        _requests = 0;
    }

    private static long BudgetTicks() =>
        (long)(DisplayIntervalMs() * BudgetShareOfFrame * Stopwatch.Frequency / 1000.0);

    private static double _displayIntervalMs;
    private static ulong _displayCheckedMs;

    /// <summary>Operator screen frame interval in milliseconds (60 Hz if unknown), checked every few seconds.</summary>
    private static double DisplayIntervalMs()
    {
        ulong now = Time.GetTicksMsec();
        if (_displayIntervalMs > 0 && now < _displayCheckedMs + 5000)
            return _displayIntervalMs;
        _displayCheckedMs = now;
        double hz = 0;
        try { hz = DisplayServer.ScreenGetRefreshRate(); } catch { /* unknown */ }
        if (hz < 20 || hz > 500)
            hz = 60;
        _displayIntervalMs = 1000.0 / hz;
        return _displayIntervalMs;
    }
}
