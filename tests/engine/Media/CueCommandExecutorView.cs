// SPDX-FileCopyrightText: 2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System.Linq;
using Cue2.Domain.Commands;
using Cue2.Domain.Playback;
using Godot;

namespace Cue2.EngineTests.Media;

/// <summary>
/// Read-only questions the engine tests ask about the cue executor.
/// </summary>
internal readonly struct CueCommandExecutorView
{
    private readonly CueCommandExecutor _executor;

    /// <summary>Wraps <paramref name="executor"/>.</summary>
    internal CueCommandExecutorView(CueCommandExecutor executor) => _executor = executor;

    /// <summary>Active cues that are playing or starting (standby preloads excluded).</summary>
    internal int Count => _executor.ActiveCues.Count(a => a != null && !a.IsStandbyPreloaded);

    /// <summary>Cue ids of the active cues that are playing or starting.</summary>
    internal int[] CueIds() =>
        _executor.ActiveCues.Where(a => a != null && !a.IsStandbyPreloaded).Select(a => a.Cue.Id).ToArray();

    /// <summary>The live playing (or starting) instance of <paramref name="cueId"/>, or null.</summary>
    internal ActiveCue Find(int cueId) =>
        _executor.ActiveCues.FirstOrDefault(a =>
            a != null && GodotObject.IsInstanceValid(a) && !a.IsStandbyPreloaded && a.Cue?.Id == cueId);
}
