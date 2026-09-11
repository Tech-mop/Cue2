// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System;
using System.Runtime;
using System.Threading;
using System.Threading.Tasks;
using Godot;

namespace Cue2.Media.Audio;

/// <summary>
/// Helpers for reclaiming large media allocations (PCM stores, frame pools).
/// .NET places large arrays on the Large Object Heap, which is not compacted by
/// default — nulling references alone often leaves process working-set high until
/// an LOH-compacting GC runs.
/// </summary>
/// <remarks>
/// Compact is <b>deferred and coalesced</b>: <see cref="ReclaimIfNeeded"/> returns
/// immediately so stop/Clean (and GO of the next cue) never block the main thread on
/// a multi-hundred-ms LOH compact. Multiple Clean() calls in one stop batch schedule
/// a single compact.
/// </remarks>
public static class MediaMemory
{
    /// <summary>Threshold above which we request an LOH-compacting GC after release.</summary>
    public const long CompactThresholdBytes = 8L * 1024 * 1024; // 8 MiB

    /// <summary>
    /// Delay before compact so the stop/Clean frame, Completed handlers, and next-cue
    /// GO can finish without sharing the STW pause.
    /// </summary>
    private const int DeferMs = 32;

    private static long _pendingReclaimBytes;

    /// <summary>0 = idle, 1 = compact scheduled or running (coalesce gate).</summary>
    private static int _reclaimGate;

    /// <summary>
    /// Note that approximately <paramref name="bytes"/> of large media memory was released.
    /// Does not collect immediately — call <see cref="ReclaimIfNeeded"/> after a batch of disposals.
    /// </summary>
    public static void NoteReleased(long bytes)
    {
        if (bytes <= 0) return;
        Interlocked.Add(ref _pendingReclaimBytes, bytes);
    }

    /// <summary>
    /// If enough large media has been released since the last compact, schedule a
    /// coalesced LOH-compacting GC on a background worker.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Non-blocking: safe on the main thread during <c>Clean()</c> / stop. Does not run
    /// GC on the caller’s stack. Concurrent calls coalesce into one compact.
    /// </para>
    /// <para>
    /// Compact still pauses the process briefly when it runs (STW), but that happens
    /// after the stop/GO hot path, not inside it — and never as a double Aggressive
    /// + <see cref="GC.WaitForPendingFinalizers"/> pair. Uses
    /// <see cref="GCCollectionMode.Forced"/> so the collect cannot be skipped
    /// (Optimized was leaving CompactOnce unused and the working set high).
    /// </para>
    /// </remarks>
    /// <param name="forceIfAtLeastBytes">Minimum pending release to trigger compact (default 8 MiB).</param>
    public static void ReclaimIfNeeded(long forceIfAtLeastBytes = CompactThresholdBytes)
    {
        long minBytes = forceIfAtLeastBytes > 0 ? forceIfAtLeastBytes : CompactThresholdBytes;
        long pending = Interlocked.Read(ref _pendingReclaimBytes);
        if (pending < minBytes)
            return;

        // Already scheduled/running — pending bytes keep accumulating for this or the next pass.
        if (Interlocked.CompareExchange(ref _reclaimGate, 1, 0) != 0)
            return;

        long threshold = minBytes;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(DeferMs).ConfigureAwait(false);
                CompactPending(threshold);
            }
            catch (Exception ex)
            {
                GD.PrintErr($"MediaMemory:Reclaim - {ex.Message}");
            }
            finally
            {
                Interlocked.Exchange(ref _reclaimGate, 0);
                // Releases that arrived during compact / defer may still exceed threshold.
                if (Interlocked.Read(ref _pendingReclaimBytes) >= threshold)
                    ReclaimIfNeeded(threshold);
            }
        });
    }

    /// <summary>
    /// Runs a single LOH-compacting collection if pending release meets <paramref name="minBytes"/>.
    /// </summary>
    private static void CompactPending(long minBytes)
    {
        long pending = Interlocked.Exchange(ref _pendingReclaimBytes, 0);
        if (pending < minBytes)
        {
            // Below threshold after coalesce wait — put bytes back for a later stop.
            if (pending > 0)
                Interlocked.Add(ref _pendingReclaimBytes, pending);
            return;
        }

        GD.Print(
            $"MediaMemory:Reclaim - Compacting LOH after releasing ~{pending / (1024.0 * 1024.0):F1} MiB (deferred)");

        // Forced (not Optimized): Optimized may no-op when the GC thinks gen2 is
        // "not due", which leaves CompactOnce unused and the process working set high
        // (LOH holes from PCM/frame arrays). Still one collect — not the old
        // Aggressive + WaitForPendingFinalizers + second Aggressive hitch.
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
    }

    /// <summary>
    /// Estimate size of a float sample buffer in bytes.
    /// </summary>
    public static long FloatBufferBytes(float[] buffer) =>
        buffer == null ? 0 : (long)buffer.Length * sizeof(float);

    /// <summary>
    /// Estimate size of a byte buffer in bytes.
    /// </summary>
    public static long ByteBufferBytes(byte[] buffer) =>
        buffer?.Length ?? 0;
}
