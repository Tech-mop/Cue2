// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using SDL3;

namespace Cue2.Domain.Playback;

/// <summary>
/// One SDL output stream bound to a playback, as seen by that playback's fill worker.
/// Device name and channel count are resolved on the main thread at publish time so the
/// worker never reads <see cref="Cue2.Services.AudioDevices"/> device maps.
/// </summary>
public sealed class AudioStreamBinding
{
    public AudioStreamBinding(uint logicalId, IntPtr stream, int channels, string deviceName)
    {
        LogicalId = logicalId;
        Stream = stream;
        Channels = channels;
        DeviceName = deviceName;
    }

    /// <summary>SDL logical device id the stream is bound to.</summary>
    public uint LogicalId { get; }

    /// <summary>SDL audio stream handle.</summary>
    public IntPtr Stream { get; }

    /// <summary>Post-matrix channel count written into the stream (0 = unknown, use source).</summary>
    public int Channels { get; }

    /// <summary>Device name used for patch lookups in the mix matrix (may be null).</summary>
    public string DeviceName { get; }

    /// <summary>
    /// False once the main thread has detached the stream. Read and written only while
    /// holding the owning <see cref="AudioStreamBindingSet.IoLock"/>.
    /// </summary>
    internal bool Alive = true;
}

/// <summary>
/// Thread-safe view of a playback's SDL output streams for its fill worker (R1-P0-01).
/// </summary>
/// <remarks>
/// Threading handshake:
/// <list type="number">
/// <item>The main thread owns <c>DeviceStreams</c> / <c>DeviceStreamChannels</c> and calls
/// <see cref="Publish"/> after every change. Workers never read those dictionaries.</item>
/// <item>Workers read <see cref="Snapshot"/> (an immutable array swapped atomically) and make
/// every SDL call on a bound stream through <see cref="GetQueued"/>, <see cref="Put"/> or
/// <see cref="Clear"/>, which hold <see cref="IoLock"/> and skip detached bindings.</item>
/// <item>The main thread detaches with <see cref="Detach"/> / <see cref="DetachAll"/>: under
/// <see cref="IoLock"/> it marks the binding dead and republishes, and only after the lock is
/// released does the caller destroy the returned handles. A worker therefore either finished
/// its SDL call before the detach, or sees the binding dead and skips it.</item>
/// </list>
/// Lock order: <see cref="IoLock"/> is always innermost. Nothing is called under it except
/// the SDL stream call itself and dictionary bookkeeping, so it can be taken while holding a
/// playback's own <c>_lock</c> without risk of deadlock.
/// </remarks>
public sealed class AudioStreamBindingSet
{
    /// <summary>Guards SDL calls on bound streams against concurrent detach.</summary>
    public readonly object IoLock = new();

    private volatile AudioStreamBinding[] _snapshot = Array.Empty<AudioStreamBinding>();

    /// <summary>Current bindings. Safe to read from any thread; never mutated after publish.</summary>
    public AudioStreamBinding[] Snapshot => _snapshot;

    /// <summary>True when at least one stream is published.</summary>
    public bool HasStreams => _snapshot.Length > 0;

    /// <summary>
    /// Rebuilds the snapshot from the main-thread dictionaries. Main thread only.
    /// Bindings whose stream is no longer in <paramref name="streams"/> are marked dead.
    /// </summary>
    /// <param name="streams">Stream handles keyed by logical device id (may be null).</param>
    /// <param name="channels">Output channel counts keyed by logical device id (may be null).</param>
    /// <param name="resolveDeviceName">Maps a logical device id to its device name (main thread).</param>
    public void Publish(
        Dictionary<uint, IntPtr> streams,
        Dictionary<uint, int> channels,
        Func<uint, string> resolveDeviceName)
    {
        lock (IoLock)
        {
            var old = _snapshot;
            var next = new List<AudioStreamBinding>(streams?.Count ?? 0);
            if (streams != null)
            {
                foreach (var kv in streams)
                {
                    if (kv.Value == IntPtr.Zero)
                        continue;
                    int ch = channels != null && channels.TryGetValue(kv.Key, out int c) ? c : 0;
                    AudioStreamBinding existing = null;
                    foreach (var b in old)
                    {
                        if (b.Alive && b.LogicalId == kv.Key && b.Stream == kv.Value && b.Channels == ch)
                        {
                            existing = b;
                            break;
                        }
                    }
                    next.Add(existing ?? new AudioStreamBinding(kv.Key, kv.Value, ch, resolveDeviceName?.Invoke(kv.Key)));
                }
            }

            foreach (var b in old)
            {
                if (!next.Contains(b))
                    b.Alive = false;
            }

            _snapshot = next.ToArray();
        }
    }

    /// <summary>
    /// Removes the stream for <paramref name="logicalId"/> from the dictionaries and the snapshot.
    /// Main thread only. The caller must destroy the returned handle after this returns.
    /// </summary>
    /// <returns>The detached stream handle, or <see cref="IntPtr.Zero"/> if none was bound.</returns>
    public IntPtr Detach(Dictionary<uint, IntPtr> streams, Dictionary<uint, int> channels, uint logicalId)
    {
        lock (IoLock)
        {
            IntPtr stream = IntPtr.Zero;
            if (streams != null && streams.TryGetValue(logicalId, out stream))
                streams.Remove(logicalId);
            channels?.Remove(logicalId);

            var old = _snapshot;
            var next = new List<AudioStreamBinding>(old.Length);
            foreach (var b in old)
            {
                if (b.LogicalId == logicalId)
                    b.Alive = false;
                else
                    next.Add(b);
            }
            _snapshot = next.ToArray();
            return stream;
        }
    }

    /// <summary>
    /// Removes every stream from the dictionaries and the snapshot. Main thread only.
    /// The caller must destroy the returned handles after this returns.
    /// </summary>
    /// <returns>Detached stream handles (non-zero, distinct).</returns>
    public List<IntPtr> DetachAll(Dictionary<uint, IntPtr> streams, Dictionary<uint, int> channels)
    {
        lock (IoLock)
        {
            var handles = new List<IntPtr>();
            if (streams != null)
            {
                foreach (var stream in streams.Values)
                {
                    if (stream != IntPtr.Zero && !handles.Contains(stream))
                        handles.Add(stream);
                }
                streams.Clear();
            }
            channels?.Clear();

            foreach (var b in _snapshot)
            {
                b.Alive = false;
                if (b.Stream != IntPtr.Zero && !handles.Contains(b.Stream))
                    handles.Add(b.Stream);
            }
            _snapshot = Array.Empty<AudioStreamBinding>();
            return handles;
        }
    }

    /// <summary>Bytes queued in the stream, or -1 if the binding was detached.</summary>
    public long GetQueued(AudioStreamBinding binding)
    {
        lock (IoLock)
        {
            if (binding == null || !binding.Alive)
                return -1;
            return SDL.GetAudioStreamQueued(binding.Stream);
        }
    }

    /// <summary>Queues PCM into the stream. Returns false (and writes nothing) if detached.</summary>
    public bool Put(AudioStreamBinding binding, IntPtr data, int byteCount)
    {
        lock (IoLock)
        {
            if (binding == null || !binding.Alive)
                return false;
            return SDL.PutAudioStreamData(binding.Stream, data, byteCount);
        }
    }

    /// <summary>Queues PCM into the stream. Returns false (and writes nothing) if detached.</summary>
    public bool Put(AudioStreamBinding binding, byte[] data, int byteCount)
    {
        lock (IoLock)
        {
            if (binding == null || !binding.Alive)
                return false;
            return SDL.PutAudioStreamData(binding.Stream, data, byteCount);
        }
    }

    /// <summary>Drops queued PCM from every live bound stream.</summary>
    public void ClearAll()
    {
        lock (IoLock)
        {
            foreach (var b in _snapshot)
            {
                if (!b.Alive)
                    continue;
                try { SDL.ClearAudioStream(b.Stream); } catch { /* ignore */ }
            }
        }
    }

    /// <summary>Destroys handles returned by <see cref="Detach"/> / <see cref="DetachAll"/>.</summary>
    public static void Destroy(IEnumerable<IntPtr> handles)
    {
        if (handles == null)
            return;
        foreach (var stream in handles)
            Destroy(stream);
    }

    /// <summary>Destroys one handle returned by <see cref="Detach"/>. Zero is ignored.</summary>
    public static void Destroy(IntPtr stream)
    {
        if (stream == IntPtr.Zero)
            return;
        try { SDL.DestroyAudioStream(stream); } catch { /* ignore */ }
    }
}
