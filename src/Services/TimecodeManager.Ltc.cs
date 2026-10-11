// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Threading;
using Cue2.Domain.Devices;
using Cue2.Domain.Timecode;
using Godot;
using SDL3;

namespace Cue2.Services;

public partial class TimecodeManager
{
    private sealed class LtcOutputRuntime
    {
        public int EndpointId;
        public string DeviceName = string.Empty;
        public int Channel;
        public AudioDevice Device;
        public IntPtr Stream;
        public int StreamChannels;
        public int SampleRate;
        public bool LevelHigh;
        public float[] Mono;
        public float[] Interleaved;
    }

    private sealed class LtcInputRuntime
    {
        public int EndpointId;
        public string DeviceName = string.Empty;
        public int Channel;
        public IntPtr Stream;
        public int StreamChannels;
        public int SampleRate;
        public byte[] Buffer;
        public float[] Mono;
        public LtcDecoder Decoder = new();
    }

    private readonly object _ltcLock = new();
    private readonly Dictionary<int, LtcOutputRuntime> _ltcRuntimes = new();
    private readonly Dictionary<int, LtcInputRuntime> _ltcInputs = new();
    private Thread _ltcFillThread;
    private volatile bool _ltcFillRunning;

    /// <summary>
    /// Opens, updates, or closes LTC output streams to match current session outputs.
    /// </summary>
    private void ReconcileLtcOutputs()
    {
        if (_audioDevices == null)
            _audioDevices = GetNodeOrNull<AudioDevices>("/root/AudioDevices");

        var wanted = new HashSet<int>();
        if (_timecodeEnabled && _audioDevices != null)
        {
            foreach (var endpoint in _outputs)
            {
                if (endpoint.Kind != TimecodeKind.LinearTimecode)
                    continue;
                if (string.IsNullOrEmpty(endpoint.SourceName))
                    continue;
                wanted.Add(endpoint.Id);
                EnsureLtcRuntime(endpoint);
            }
        }

        var stale = new List<int>();
        lock (_ltcLock)
        {
            foreach (var id in _ltcRuntimes.Keys)
            {
                if (!wanted.Contains(id))
                    stale.Add(id);
            }
        }

        foreach (int id in stale)
            DestroyLtcRuntime(id);

        ReconcileLtcInputs();
        RefreshLtcPlaybackConflictWarnings(force: true);
    }

    /// <summary>
    /// Opens LTC recording streams for session inputs.
    /// </summary>
    private void ReconcileLtcInputs()
    {
        if (_audioDevices == null)
            _audioDevices = GetNodeOrNull<AudioDevices>("/root/AudioDevices");

        var wanted = new HashSet<int>();
        if (_timecodeEnabled && _audioDevices != null)
        {
            foreach (var endpoint in _inputs)
            {
                if (endpoint.Kind != TimecodeKind.LinearTimecode)
                    continue;
                if (string.IsNullOrEmpty(endpoint.SourceName))
                    continue;
                wanted.Add(endpoint.Id);
                EnsureLtcInputRuntime(endpoint);
            }
        }

        var stale = new List<int>();
        lock (_ltcLock)
        {
            foreach (var id in _ltcInputs.Keys)
            {
                if (!wanted.Contains(id))
                    stale.Add(id);
            }
        }

        foreach (int id in stale)
            DestroyLtcInputRuntime(id);

        bool any;
        lock (_ltcLock)
            any = _ltcRuntimes.Count > 0 || _ltcInputs.Count > 0;
        if (any)
            StartLtcFillThread();
        else
            StopLtcFillThread();
    }

    private void EnsureLtcInputRuntime(TimecodeEndpoint endpoint)
    {
        lock (_ltcLock)
        {
            if (_ltcInputs.TryGetValue(endpoint.Id, out var existing)
                && existing != null
                && string.Equals(existing.DeviceName, endpoint.SourceName, StringComparison.Ordinal)
                && existing.Channel == endpoint.SourceChannel
                && existing.Stream != IntPtr.Zero)
            {
                return;
            }
        }

        DestroyLtcInputRuntime(endpoint.Id);
        if (_audioDevices == null)
            return;

        if (!_audioDevices.TryOpenRecordingStream(
                endpoint.SourceName, 0, out var stream, out int channels, out int sampleRate, out string error))
        {
            GD.PrintErr($"TimecodeManager:EnsureLtcInputRuntime - '{endpoint.Name}': {error}");
            _globalSignals?.EmitSignal(nameof(GlobalSignals.Log),
                $"Timecode LTC input: failed to open '{endpoint.SourceName}': {error}", (int)LogType.Error);
            return;
        }

        int ch = Math.Clamp(endpoint.SourceChannel, 0, Math.Max(0, channels - 1));
        if (ch != endpoint.SourceChannel)
            endpoint.SourceChannel = ch;

        var runtime = new LtcInputRuntime
        {
            EndpointId = endpoint.Id,
            DeviceName = endpoint.SourceName,
            Channel = ch,
            Stream = stream,
            StreamChannels = channels,
            SampleRate = sampleRate,
            Buffer = new byte[4096 * Math.Max(1, channels) * sizeof(float)],
            Mono = new float[4096]
        };

        lock (_ltcLock)
            _ltcInputs[endpoint.Id] = runtime;

        GD.Print($"TimecodeManager:EnsureLtcInputRuntime - '{endpoint.Name}' ← {endpoint.SourceName} ch {ch + 1}/{channels} @ {sampleRate} Hz");
        _globalSignals?.EmitSignal(nameof(GlobalSignals.Log),
            $"Timecode LTC input listening on '{endpoint.SourceName}' ch {ch + 1}", (int)LogType.Info);
    }

    private void DestroyLtcInputRuntime(int endpointId)
    {
        LtcInputRuntime runtime;
        lock (_ltcLock)
        {
            if (!_ltcInputs.Remove(endpointId, out runtime) || runtime == null)
                return;
        }

        _audioDevices?.CloseRecordingStream(runtime.Stream);
    }

    private void EnsureLtcRuntime(TimecodeEndpoint endpoint)
    {
        lock (_ltcLock)
        {
            if (_ltcRuntimes.TryGetValue(endpoint.Id, out var existing)
                && existing != null
                && string.Equals(existing.DeviceName, endpoint.SourceName, StringComparison.Ordinal)
                && existing.Channel == endpoint.SourceChannel
                && existing.Stream != IntPtr.Zero)
            {
                return;
            }
        }

        DestroyLtcRuntime(endpoint.Id);

        if (_audioDevices == null)
            return;

        if (!_audioDevices.TryCreateBoundPlaybackStream(
                endpoint.SourceName,
                out var device,
                out var stream,
                out int channels,
                out int sampleRate,
                out string error))
        {
            GD.PrintErr($"TimecodeManager:EnsureLtcRuntime - '{endpoint.Name}': {error}");
            _globalSignals?.EmitSignal(nameof(GlobalSignals.Log),
                $"Timecode LTC: failed to open '{endpoint.SourceName}': {error}", (int)LogType.Error);
            return;
        }

        int ch = Math.Clamp(endpoint.SourceChannel, 0, Math.Max(0, channels - 1));
        if (ch != endpoint.SourceChannel)
            endpoint.SourceChannel = ch;

        var runtime = new LtcOutputRuntime
        {
            EndpointId = endpoint.Id,
            DeviceName = endpoint.SourceName,
            Channel = ch,
            Device = device,
            Stream = stream,
            StreamChannels = channels,
            SampleRate = sampleRate,
            Mono = new float[LtcEncoder.SamplesPerFrame(sampleRate, endpoint.FrameRate)],
            Interleaved = new float[LtcEncoder.SamplesPerFrame(sampleRate, endpoint.FrameRate) * channels]
        };

        lock (_ltcLock)
            _ltcRuntimes[endpoint.Id] = runtime;

        GD.Print($"TimecodeManager:EnsureLtcRuntime - '{endpoint.Name}' → {endpoint.SourceName} ch {ch + 1}/{channels} @ {sampleRate} Hz");
    }

    private void DestroyLtcRuntime(int endpointId)
    {
        LtcOutputRuntime runtime;
        lock (_ltcLock)
        {
            if (!_ltcRuntimes.Remove(endpointId, out runtime) || runtime == null)
                return;
        }

        _audioDevices?.DestroyBoundPlaybackStream(runtime.Device, runtime.Stream);
    }

    private void DestroyAllLtcRuntimes()
    {
        List<int> ids;
        lock (_ltcLock)
            ids = new List<int>(_ltcRuntimes.Keys);
        foreach (int id in ids)
            DestroyLtcRuntime(id);

        List<int> inIds;
        lock (_ltcLock)
            inIds = new List<int>(_ltcInputs.Keys);
        foreach (int id in inIds)
            DestroyLtcInputRuntime(id);
    }

    private void StartLtcFillThread()
    {
        if (_ltcFillThread != null && _ltcFillThread.IsAlive)
            return;
        _ltcFillRunning = true;
        _ltcFillThread = new Thread(LtcFillLoop)
        {
            IsBackground = true,
            Name = "Cue2-LTC"
        };
        _ltcFillThread.Start();
    }

    private void StopLtcFillThread()
    {
        _ltcFillRunning = false;
        var thread = _ltcFillThread;
        _ltcFillThread = null;
        if (thread != null && thread.IsAlive && thread != Thread.CurrentThread)
        {
            try { thread.Join(200); }
            catch { /* shutting down */ }
        }
    }

    private void LtcFillLoop()
    {
        while (_ltcFillRunning)
        {
            try
            {
                FillLtcOnce();
                DrainLtcInputs();
            }
            catch (Exception ex)
            {
                GD.PrintErr($"TimecodeManager:LtcFillLoop - {ex.Message}");
            }

            Thread.Sleep(5);
        }
    }

    private void FillLtcOnce()
    {
        List<LtcOutputRuntime> runtimes;
        lock (_ltcLock)
            runtimes = new List<LtcOutputRuntime>(_ltcRuntimes.Values);

        foreach (var runtime in runtimes)
        {
            if (runtime.Stream == IntPtr.Zero)
                continue;

            var endpoint = FindEndpoint(runtime.EndpointId);
            if (endpoint == null || endpoint.Kind != TimecodeKind.LinearTimecode)
                continue;

            int samplesPerFrame = LtcEncoder.SamplesPerFrame(runtime.SampleRate, endpoint.FrameRate);
            int bytesPerFrame = samplesPerFrame * runtime.StreamChannels * sizeof(float);
            long queued = SDL.GetAudioStreamQueued(runtime.Stream);
            // Keep ~120 ms queued (about 3–4 LTC frames at 30 fps).
            int targetBytes = Math.Max(bytesPerFrame * 3, runtime.SampleRate * runtime.StreamChannels * sizeof(float) / 8);
            if (queued >= targetBytes)
                continue;

            if (runtime.Mono == null || runtime.Mono.Length < samplesPerFrame)
                runtime.Mono = new float[samplesPerFrame];
            int interleavedLen = samplesPerFrame * runtime.StreamChannels;
            if (runtime.Interleaved == null || runtime.Interleaved.Length < interleavedLen)
                runtime.Interleaved = new float[interleavedLen];

            bool running = _timecodeEnabled && endpoint.Running;
            var value = TimecodeValue.FromElapsedSeconds(
                endpoint.ElapsedSeconds, endpoint.FrameRate, running);

            bool level = runtime.LevelHigh;
            int written = LtcEncoder.EncodeFrame(value, runtime.SampleRate, runtime.Mono.AsSpan(0, samplesPerFrame), ref level);
            runtime.LevelHigh = level;

            Array.Clear(runtime.Interleaved, 0, interleavedLen);
            int ch = Math.Clamp(runtime.Channel, 0, runtime.StreamChannels - 1);
            for (int i = 0; i < written; i++)
                runtime.Interleaved[i * runtime.StreamChannels + ch] = runtime.Mono[i];

            unsafe
            {
                fixed (float* p = runtime.Interleaved)
                {
                    SDL.PutAudioStreamData(runtime.Stream, (IntPtr)p, interleavedLen * sizeof(float));
                }
            }

            if (running)
            {
                endpoint.ElapsedSeconds += 1.0 / endpoint.FrameRate.RealFramesPerSecond();
                if (endpoint.PlayUntilElapsed >= 0 && endpoint.ElapsedSeconds >= endpoint.PlayUntilElapsed)
                {
                    endpoint.ElapsedSeconds = endpoint.PlayUntilElapsed;
                    endpoint.Running = false;
                    endpoint.PlayUntilElapsed = -1;
                }
            }
        }
    }

    private void DrainLtcInputs()
    {
        List<LtcInputRuntime> runtimes;
        lock (_ltcLock)
            runtimes = new List<LtcInputRuntime>(_ltcInputs.Values);

        foreach (var runtime in runtimes)
        {
            if (runtime.Stream == IntPtr.Zero)
                continue;

            var endpoint = FindEndpoint(runtime.EndpointId);
            if (endpoint == null || endpoint.Kind != TimecodeKind.LinearTimecode)
                continue;

            int frameBytes = Math.Max(1, runtime.StreamChannels) * sizeof(float);
            int available = SDL.GetAudioStreamAvailable(runtime.Stream);
            if (available < frameBytes)
                continue;
            if (available > runtime.Buffer.Length * 8)
            {
                SDL.ClearAudioStream(runtime.Stream);
                continue;
            }

            int toRead = Math.Min(available, runtime.Buffer.Length);
            toRead -= toRead % frameBytes;
            if (toRead < frameBytes)
                continue;

            int got = SDL.GetAudioStreamData(runtime.Stream, runtime.Buffer, toRead);
            if (got < frameBytes)
                continue;

            int frames = got / frameBytes;
            int ch = Math.Clamp(runtime.Channel, 0, runtime.StreamChannels - 1);
            if (runtime.Mono.Length < frames)
                runtime.Mono = new float[frames];

            var buffer = runtime.Buffer;
            for (int i = 0; i < frames; i++)
            {
                int offset = i * frameBytes + ch * sizeof(float);
                runtime.Mono[i] = System.Buffers.Binary.BinaryPrimitives.ReadSingleLittleEndian(
                    buffer.AsSpan(offset, sizeof(float)));
            }

            if (runtime.Decoder.Push(runtime.Mono.AsSpan(0, frames), runtime.SampleRate, endpoint.FrameRate, out var decoded))
                ApplyLockedInput(endpoint, decoded);
        }
    }
}
