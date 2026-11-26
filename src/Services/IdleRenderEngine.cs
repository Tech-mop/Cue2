// SPDX-FileCopyrightText: 2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using Godot;

namespace Cue2.Services;

/// <summary>
/// Pushes <see cref="IdleRenderPolicy"/> into Godot's low-processor mode.
/// Off while a video/text presenter is running; on (skip-draw + sleep) when idle.
/// </summary>
public static class IdleRenderEngine
{
    /// <summary>
    /// Applies low-processor mode from the current presenter count.
    /// </summary>
    public static void Sync()
    {
        bool presenting = IdleRenderPolicy.IsPresenting;
        OS.LowProcessorUsageMode = !presenting;
        if (!presenting)
            OS.LowProcessorUsageModeSleepUsec = IdleRenderPolicy.IdleSleepUsec;
    }
}
