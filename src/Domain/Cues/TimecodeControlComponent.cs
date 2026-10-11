// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System;
using System.Threading.Tasks;
using Cue2.Domain.Timecode;
using Cue2.Services;
using Godot;
using Godot.Collections;

namespace Cue2.Domain.Cues;

/// <summary>
/// Transport applied to a registered timecode output when the parent cue fires.
/// </summary>
public enum TimecodeControlAction
{
    /// <summary>Start the output generator from its current time (or optional set time).</summary>
    Play = 0,

    /// <summary>Freeze the generator; LTC keeps sending the held frame.</summary>
    Pause = 1,

    /// <summary>Jump the generator to an absolute timecode.</summary>
    SetTime = 2,

    /// <summary>Offset the generator by a signed number of seconds.</summary>
    OffsetTime = 3,

    /// <summary>Optionally set a start time, play, then pause after a duration.</summary>
    PlayForDuration = 4
}

/// <summary>
/// Cue component that drives a session timecode output (play / pause / set / offset / timed play).
/// </summary>
public class TimecodeControlComponent : ICueComponent
{
    /// <inheritdoc />
    public string Type => "TimecodeControl";

    /// <summary>Target session timecode output id. <c>-1</c> means unset.</summary>
    public int EndpointId { get; set; } = -1;

    /// <summary>Action applied when the cue fires.</summary>
    public TimecodeControlAction Action { get; set; } = TimecodeControlAction.Play;

    /// <summary>When true, Play / PlayForDuration jump to <see cref="SetHours"/>… before starting.</summary>
    public bool UseStartTime { get; set; }

    /// <summary>Absolute hours 0–23 for Set Time / optional start.</summary>
    public int SetHours { get; set; }

    /// <summary>Absolute minutes 0–59.</summary>
    public int SetMinutes { get; set; }

    /// <summary>Absolute seconds 0–59.</summary>
    public int SetSeconds { get; set; }

    /// <summary>Absolute frames 0–(fps−1).</summary>
    public int SetFrames { get; set; }

    /// <summary>Signed offset in seconds for <see cref="TimecodeControlAction.OffsetTime"/>.</summary>
    public double OffsetSeconds { get; set; } = 10.0;

    /// <summary>
    /// Seconds to run for <see cref="TimecodeControlAction.PlayForDuration"/>.
    /// <c>0</c> means play without auto-pause.
    /// </summary>
    public double PlayDurationSeconds { get; set; } = 10.0;

    /// <summary>
    /// Generator elapsed seconds at the start of the current Play Duration (runtime only).
    /// </summary>
    public double RuntimePlayOriginElapsed { get; set; } = -1;

    /// <summary>
    /// True when this Play Duration started a generator that was stopped (runtime only).
    /// Stopping the parent cue then pauses that generator. Left false when the clock was already running.
    /// </summary>
    public bool RuntimeStartedGenerator { get; set; }

    /// <summary>
    /// Short UI label for an action.
    /// </summary>
    /// <param name="action">Timecode control action.</param>
    /// <returns>English display name.</returns>
    public static string GetActionDisplayName(TimecodeControlAction action) => action switch
    {
        TimecodeControlAction.Play => "Play",
        TimecodeControlAction.Pause => "Pause",
        TimecodeControlAction.SetTime => "Set Time",
        TimecodeControlAction.OffsetTime => "Offset Time",
        TimecodeControlAction.PlayForDuration => "Play Duration",
        _ => action.ToString()
    };

    /// <summary>
    /// Content duration for the active-cue progress row.
    /// </summary>
    /// <returns>Play-for-duration seconds, or 0 for instant actions.</returns>
    public double GetContentDurationSeconds()
    {
        if (Action == TimecodeControlAction.PlayForDuration)
            return Math.Max(0, PlayDurationSeconds);
        return 0;
    }

    /// <summary>
    /// Summary for the active-cue bar.
    /// </summary>
    /// <returns>Short description.</returns>
    public string GetDisplaySummary()
    {
        string action = GetActionDisplayName(Action);
        string target = EndpointId >= 0 ? $"#{EndpointId}" : "(no output)";
        return Action switch
        {
            TimecodeControlAction.SetTime =>
                $"{action} {target} {SetHours:00}:{SetMinutes:00}:{SetSeconds:00}:{SetFrames:00}",
            TimecodeControlAction.OffsetTime =>
                $"{action} {target} {OffsetSeconds:+0.###;-0.###}s",
            TimecodeControlAction.PlayForDuration =>
                UseStartTime
                    ? $"{action} {target} {SetHours:00}:{SetMinutes:00}:{SetSeconds:00}:{SetFrames:00} × {PlayDurationSeconds:0.###}s"
                    : $"{action} {target} {PlayDurationSeconds:0.###}s",
            TimecodeControlAction.Play when UseStartTime =>
                $"{action} {target} @ {SetHours:00}:{SetMinutes:00}:{SetSeconds:00}:{SetFrames:00}",
            _ => $"{action} {target}"
        };
    }

    /// <summary>
    /// Applies this action to the selected timecode output.
    /// </summary>
    public async Task ExecuteAsync()
    {
        try
        {
            if (Engine.GetMainLoop() is not SceneTree tree)
                return;

            var manager = tree.Root.GetNodeOrNull<TimecodeManager>("/root/TimecodeManager");
            if (manager == null)
            {
                GD.PrintErr("TimecodeControlComponent:ExecuteAsync - TimecodeManager not found");
                return;
            }

            if (EndpointId < 0)
            {
                GD.Print("TimecodeControlComponent:ExecuteAsync - No timecode output selected");
                return;
            }

            var endpoint = manager.FindEndpoint(EndpointId);
            if (endpoint == null || endpoint.Direction != TimecodeDirection.Output)
            {
                GD.Print($"TimecodeControlComponent:ExecuteAsync - Output {EndpointId} not found");
                return;
            }

            switch (Action)
            {
                case TimecodeControlAction.Pause:
                    manager.Pause(EndpointId);
                    break;
                case TimecodeControlAction.SetTime:
                    manager.SetTime(EndpointId, SetHours, SetMinutes, SetSeconds, SetFrames);
                    break;
                case TimecodeControlAction.OffsetTime:
                    manager.OffsetSeconds(EndpointId, OffsetSeconds);
                    break;
                case TimecodeControlAction.Play:
                    if (UseStartTime)
                        manager.SetTime(EndpointId, SetHours, SetMinutes, SetSeconds, SetFrames);
                    manager.Start(EndpointId);
                    break;
                case TimecodeControlAction.PlayForDuration:
                    if (UseStartTime)
                        manager.SetTime(EndpointId, SetHours, SetMinutes, SetSeconds, SetFrames);
                    endpoint = manager.FindEndpoint(EndpointId);
                    RuntimePlayOriginElapsed = endpoint?.ElapsedSeconds ?? 0;
                    RuntimeStartedGenerator = endpoint != null && !endpoint.Running;
                    manager.Start(EndpointId, PlayDurationSeconds);
                    if (PlayDurationSeconds > 1e-9)
                        await WaitForPlayWindowAsync(manager);
                    break;
            }

            GD.Print($"TimecodeControlComponent:ExecuteAsync - {GetDisplaySummary()}");
        }
        catch (Exception ex)
        {
            GD.PrintErr($"TimecodeControlComponent:ExecuteAsync - {ex.Message}");
        }

        if (GetContentDurationSeconds() <= 1e-9)
            await Task.Delay(1);
    }

    /// <summary>
    /// Waits until the generator reaches its play-until time, is paused, or the window is cleared.
    /// Seek moves elapsed inside that window; the wait follows the generator, not wall clock.
    /// </summary>
    private async Task WaitForPlayWindowAsync(TimecodeManager manager)
    {
        while (manager != null && GodotObject.IsInstanceValid(manager))
        {
            var endpoint = manager.FindEndpoint(EndpointId);
            if (endpoint == null)
                return;
            if (endpoint.PlayUntilElapsed < 0)
                return;
            if (endpoint.ElapsedSeconds + 1e-4 >= endpoint.PlayUntilElapsed)
                return;
            await Task.Delay(33);
        }
    }

    /// <inheritdoc />
    public Dictionary GetData()
    {
        return new Dictionary
        {
            { "EndpointId", EndpointId },
            { "Action", (int)Action },
            { "UseStartTime", UseStartTime },
            { "SetHours", SetHours },
            { "SetMinutes", SetMinutes },
            { "SetSeconds", SetSeconds },
            { "SetFrames", SetFrames },
            { "OffsetSeconds", OffsetSeconds },
            { "PlayDurationSeconds", PlayDurationSeconds },
        };
    }

    /// <inheritdoc />
    public void LoadFromData(Dictionary data)
    {
        if (data == null) return;
        if (data.TryGetValue("EndpointId", out var v))
            EndpointId = v.AsInt32();
        if (data.TryGetValue("Action", out v) && Enum.IsDefined(typeof(TimecodeControlAction), v.AsInt32()))
            Action = (TimecodeControlAction)v.AsInt32();
        if (data.TryGetValue("UseStartTime", out v))
            UseStartTime = v.AsBool();
        if (data.TryGetValue("SetHours", out v))
            SetHours = Math.Clamp(v.AsInt32(), 0, 23);
        if (data.TryGetValue("SetMinutes", out v))
            SetMinutes = Math.Clamp(v.AsInt32(), 0, 59);
        if (data.TryGetValue("SetSeconds", out v))
            SetSeconds = Math.Clamp(v.AsInt32(), 0, 59);
        if (data.TryGetValue("SetFrames", out v))
            SetFrames = Math.Max(0, v.AsInt32());
        if (data.TryGetValue("OffsetSeconds", out v))
            OffsetSeconds = v.AsDouble();
        if (data.TryGetValue("PlayDurationSeconds", out v))
            PlayDurationSeconds = Math.Max(0, v.AsDouble());
    }
}
