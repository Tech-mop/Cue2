// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System;
using System.Linq;
using System.Threading.Tasks;
using Godot;

namespace Cue2.Domain.Playback;

/// <summary>
/// Non-blocking teardown for playback worker threads. Stop paths cancel their
/// workers and hand the work that the workers still touch (decoder dispose, mix buffers,
/// freeing the playback object) to <see cref="RunAfter"/>, instead of waiting on the main thread.
/// </summary>
public static class PlaybackWorkers
{
    /// <summary>
    /// Runs <paramref name="onMain"/> once every task in <paramref name="workers"/> has finished.
    /// Runs it inline when none is still running; otherwise queues it to the main thread
    /// (CallDeferred) when the last one exits. Never blocks. Call on the main thread.
    /// </summary>
    /// <param name="onMain">Main-thread teardown. Must not assume the playback is still valid.</param>
    /// <param name="workers">Worker tasks (null entries are ignored).</param>
    public static void RunAfter(Action onMain, params Task[] workers)
    {
        if (onMain == null)
            return;
        var pending = workers?.Where(t => t != null && !t.IsCompleted).ToArray() ?? Array.Empty<Task>();
        if (pending.Length == 0)
        {
            onMain();
            return;
        }

        Task.WhenAll(pending).ContinueWith(_ =>
        {
            // Wrap so the Callable never targets the (possibly already freed) playback node:
            // a lambda that only captures `this` compiles to an instance method on it.
            Action run = () => onMain();
            try { Callable.From(run).CallDeferred(); }
            catch (Exception ex) { GD.PrintErr($"PlaybackWorkers:RunAfter - {ex.Message}"); }
        }, TaskScheduler.Default);
    }

    /// <summary>
    /// Waits up to <paramref name="timeoutMs"/> for the workers. Only for paths that must finish
    /// teardown before they return (app quit, node leaving the tree), never for an operator Stop.
    /// </summary>
    public static void WaitBounded(int timeoutMs, params Task[] workers)
    {
        var pending = workers?.Where(t => t != null && !t.IsCompleted).ToArray() ?? Array.Empty<Task>();
        if (pending.Length == 0)
            return;
        try { Task.WaitAll(pending, timeoutMs); }
        catch { /* cancelled or faulted workers are finished */ }
    }

    /// <summary>True when <paramref name="task"/> is non-null and still running.</summary>
    public static bool IsRunning(Task task) => task != null && !task.IsCompleted;
}
