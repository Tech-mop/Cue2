// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using Godot;
using SDL3;

namespace Cue2.Services;

public partial class AudioDevices
{
    /// <summary>One recording device as shown in Audio Input settings.</summary>
    public readonly struct RecordingInputDevice
    {
        /// <summary>Creates a listing entry.</summary>
        /// <param name="name">SDL device name.</param>
        /// <param name="channelCount">Input channel count (at least 1).</param>
        public RecordingInputDevice(string name, int channelCount)
        {
            Name = name ?? string.Empty;
            ChannelCount = channelCount;
        }

        /// <summary>SDL device name.</summary>
        public string Name { get; }

        /// <summary>Number of input channels reported by the device.</summary>
        public int ChannelCount { get; }
    }

    private const int InputCaptureFramesPerRead = 2048;
    private const float InputMeterFallSeconds = 0.28f;

    private readonly Dictionary<string, InputCapture> _inputCaptures = new(StringComparer.Ordinal);
    private readonly HashSet<string> _inputCaptureFailures = new(StringComparer.Ordinal);

    private sealed class InputCapture
    {
        public IntPtr Stream;
        public int Channels;
        public byte[] Buffer;
        public float[] Instant;
        public float[] Display;
    }

    /// <summary>
    /// Lists connected recording devices and their channel counts. Does not open them.
    /// </summary>
    /// <returns>Devices in SDL order. Duplicate names keep the first device.</returns>
    public List<RecordingInputDevice> GetRecordingDeviceListings()
    {
        var result = new List<RecordingInputDevice>();
        try
        {
            var devices = SDL.GetAudioRecordingDevices(out int _);
            if (devices == null)
                return result;

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var deviceId in devices)
            {
                uint id = Convert.ToUInt32(deviceId);
                string name = SDL.GetAudioDeviceName(id);
                if (string.IsNullOrWhiteSpace(name) || !seen.Add(name))
                    continue;

                int channels = 2;
                if (SDL.GetAudioDeviceFormat(id, out SDL.AudioSpec spec, out int _))
                    channels = spec.Channels > 0 ? spec.Channels : 2;

                result.Add(new RecordingInputDevice(name, channels));
            }
        }
        catch (Exception ex)
        {
            GD.PrintErr($"AudioDevices:GetRecordingDeviceListings - {ex.Message}");
            _globalSignals?.EmitSignal(nameof(GlobalSignals.Log),
                $"Could not list recording devices: {ex.Message}", (int)LogType.Error);
        }

        return result;
    }

    /// <summary>
    /// Opens recording streams for <paramref name="deviceNames"/> and closes the rest.
    /// Call from the main thread. Used while Audio Input settings are on screen.
    /// </summary>
    /// <param name="deviceNames">Recording device names to meter. Null or empty stops capture.</param>
    public void SetInputMonitorDevices(IReadOnlyCollection<string> deviceNames)
    {
        if (SingleInstanceGuard.IsSecondary)
            return;

        var wanted = new HashSet<string>(StringComparer.Ordinal);
        if (deviceNames != null)
        {
            foreach (var name in deviceNames)
            {
                if (!string.IsNullOrWhiteSpace(name))
                    wanted.Add(name);
            }
        }

        foreach (var name in new List<string>(_inputCaptures.Keys))
        {
            if (!wanted.Contains(name))
                CloseInputCapture(name);
        }

        foreach (var name in wanted)
        {
            if (!_inputCaptures.ContainsKey(name))
                TryOpenInputCapture(name);
        }

        SetProcess(_inputCaptures.Count > 0);
    }

    /// <summary>
    /// Closes settings meters for <paramref name="deviceNames"/> so cue playback can open those inputs.
    /// </summary>
    /// <param name="deviceNames">Recording device names playback is about to open.</param>
    public void ReleaseInputMonitors(System.Collections.Generic.IEnumerable<string> deviceNames)
    {
        if (deviceNames == null)
            return;
        foreach (var name in deviceNames)
        {
            if (!string.IsNullOrWhiteSpace(name))
                CloseInputCapture(name);
        }
    }

    /// <summary>
    /// Closes every recording stream opened for input meters.
    /// </summary>
    public void StopInputMonitors()
    {
        foreach (var name in new List<string>(_inputCaptures.Keys))
            CloseInputCapture(name);
        _inputCaptureFailures.Clear();
        SetProcess(false);
    }

    /// <summary>
    /// Latest metered peak for one recording channel, 0…1 linear, before trim.
    /// </summary>
    /// <param name="deviceName">Recording device name.</param>
    /// <param name="channelIndex">0-based channel.</param>
    /// <returns>0 when that device is not being metered.</returns>
    public float GetInputPeak(string deviceName, int channelIndex)
    {
        if (string.IsNullOrEmpty(deviceName) || channelIndex < 0)
            return 0f;
        if (!_inputCaptures.TryGetValue(deviceName, out var capture))
            return 0f;
        if (channelIndex >= capture.Display.Length)
            return 0f;
        return capture.Display[channelIndex];
    }

    /// <inheritdoc />
    public override void _Process(double delta)
    {
        if (_inputCaptures.Count == 0)
        {
            SetProcess(false);
            return;
        }

        PollInputCaptures(Mathf.Clamp((float)delta, 0f, 0.1f));
    }

    private void PollInputCaptures(float delta)
    {
        float decay = MathF.Exp(-delta / InputMeterFallSeconds);
        foreach (var capture in _inputCaptures.Values)
        {
            try
            {
                ReadInputCapture(capture);
            }
            catch (Exception ex)
            {
                GD.PrintErr($"AudioDevices:PollInputCaptures - {ex.Message}");
            }

            for (int i = 0; i < capture.Display.Length; i++)
            {
                capture.Display[i] = MathF.Max(capture.Instant[i], capture.Display[i] * decay);
                capture.Instant[i] = 0f;
            }
        }
    }

    private static void ReadInputCapture(InputCapture capture)
    {
        if (capture.Stream == IntPtr.Zero || capture.Channels <= 0)
            return;

        int frameBytes = capture.Channels * sizeof(float);
        for (int spin = 0; spin < 6; spin++)
        {
            int available = SDL.GetAudioStreamAvailable(capture.Stream);
            if (available < frameBytes)
                return;
            if (available > capture.Buffer.Length * 8)
            {
                SDL.ClearAudioStream(capture.Stream);
                return;
            }

            int toRead = Math.Min(available, capture.Buffer.Length);
            toRead -= toRead % frameBytes;
            if (toRead < frameBytes)
                return;

            int got = SDL.GetAudioStreamData(capture.Stream, capture.Buffer, toRead);
            if (got < frameBytes)
                return;

            int frames = got / frameBytes;
            var buffer = capture.Buffer;
            for (int frame = 0; frame < frames; frame++)
            {
                int frameStart = frame * frameBytes;
                for (int channel = 0; channel < capture.Channels; channel++)
                {
                    float sample = BinaryPrimitives.ReadSingleLittleEndian(
                        buffer.AsSpan(frameStart + (channel * sizeof(float)), sizeof(float)));
                    float abs = MathF.Abs(sample);
                    if (abs > capture.Instant[channel])
                        capture.Instant[channel] = abs > 1f ? 1f : abs;
                }
            }
        }
    }

    private bool TryResolveRecordingDevice(string name, out uint physicalId, out int channels)
    {
        physicalId = 0;
        channels = 0;
        var devices = SDL.GetAudioRecordingDevices(out int _);
        if (devices == null)
            return false;

        foreach (var deviceId in devices)
        {
            uint id = Convert.ToUInt32(deviceId);
            string deviceName = SDL.GetAudioDeviceName(id);
            if (!string.Equals(deviceName, name, StringComparison.Ordinal))
                continue;

            physicalId = id;
            channels = 2;
            if (SDL.GetAudioDeviceFormat(id, out SDL.AudioSpec spec, out int _) && spec.Channels > 0)
                channels = spec.Channels;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Opens an SDL recording stream for <paramref name="name"/> as float32.
    /// When Windows rejects the input because it does not share the output clock,
    /// the Windows input format is aligned to that output and the open is retried.
    /// </summary>
    /// <param name="name">Recording device name.</param>
    /// <param name="preferredRate">App sample rate. 0 uses the endpoint rate.</param>
    /// <param name="stream">Opened stream, or zero.</param>
    /// <param name="channels">Channel count of <paramref name="stream"/>.</param>
    /// <param name="sampleRate">Sample rate of <paramref name="stream"/>.</param>
    /// <param name="error">Failure text. Empty on success.</param>
    /// <returns>True when the stream is open and running.</returns>
    public bool TryOpenRecordingStream(string name, int preferredRate, out IntPtr stream, out int channels, out int sampleRate, out string error)
    {
        stream = IntPtr.Zero;
        channels = 0;
        sampleRate = 0;
        error = string.Empty;
        try
        {
            if (!TryResolveRecordingDevice(name, out uint physicalId, out channels))
            {
                error = "device is not connected";
                LogInputOpenFailure(name, error);
                return false;
            }

            int deviceRate = 48000;
            if (SDL.GetAudioDeviceFormat(physicalId, out SDL.AudioSpec deviceSpec, out int _) && deviceSpec.Freq > 0)
                deviceRate = deviceSpec.Freq;
            int rate = preferredRate > 0 ? preferredRate : deviceRate;

            stream = OpenRecordingStream(physicalId, channels, rate, out channels, out sampleRate, out error);
            if (stream != IntPtr.Zero)
            {
                _inputCaptureFailures.Remove(name);
                return true;
            }

            if (!IsWasapiInitializeFailure(error))
            {
                LogInputOpenFailure(name, error);
                return false;
            }

            string sdlError = error;
            if (WasapiRecordingClock.TryAlignCaptureToOutput(name, out int alignedRate, out string detail))
            {
                _globalSignals?.EmitSignal(nameof(GlobalSignals.Log), detail, (int)LogType.Info);
                GD.Print($"AudioDevices:TryOpenRecordingStream - {detail}");
                if (!TryResolveRecordingDevice(name, out physicalId, out channels))
                {
                    error = string.IsNullOrEmpty(detail) ? sdlError : $"{sdlError} {detail}";
                    LogInputOpenFailure(name, error);
                    return false;
                }

                rate = alignedRate > 0 ? alignedRate : rate;
                stream = OpenRecordingStream(physicalId, channels, rate, out channels, out sampleRate, out error);
                if (stream != IntPtr.Zero)
                {
                    _inputCaptureFailures.Remove(name);
                    return true;
                }
            }
            else if (preferredRate > 0 && preferredRate != deviceRate)
            {
                stream = OpenRecordingStream(physicalId, channels, deviceRate, out channels, out sampleRate, out error);
                if (stream != IntPtr.Zero)
                {
                    _inputCaptureFailures.Remove(name);
                    return true;
                }
            }

            if (!string.IsNullOrEmpty(detail))
                error = string.IsNullOrEmpty(error) ? detail : $"{error} {detail}";
            else if (!string.IsNullOrEmpty(sdlError))
                error = sdlError;
            LogInputOpenFailure(name, error);
            return false;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            LogInputOpenFailure(name, error);
            GD.PrintErr($"AudioDevices:TryOpenRecordingStream - {name}: {ex.Message}");
            return false;
        }
    }

    private void TryOpenInputCapture(string name)
    {
        if (!TryOpenRecordingStream(name, 0, out IntPtr stream, out int channels, out _, out _))
            return;

        _inputCaptures[name] = new InputCapture
        {
            Stream = stream,
            Channels = channels,
            Buffer = new byte[InputCaptureFramesPerRead * channels * sizeof(float)],
            Instant = new float[channels],
            Display = new float[channels]
        };
        GD.Print($"AudioDevices:TryOpenInputCapture - Metering '{name}' ({channels} ch)");
    }

    private static bool IsWasapiInitializeFailure(string error)
    {
        return !string.IsNullOrEmpty(error)
            && error.IndexOf("WASAPI can't initialize audio client", StringComparison.Ordinal) >= 0;
    }

    private static IntPtr OpenRecordingStream(uint physicalId, int channels, int sampleRate, out int outChannels, out int outRate, out string error)
    {
        outChannels = Math.Clamp(channels, 1, 255);
        outRate = sampleRate > 0 ? sampleRate : 48000;
        error = string.Empty;
        var appSpec = new SDL.AudioSpec
        {
            Freq = outRate,
            Format = SDL.AudioFormat.AudioF32LE,
            Channels = outChannels
        };
        IntPtr stream = SDL.OpenAudioDeviceStream(physicalId, in appSpec, null, IntPtr.Zero);
        if (stream == IntPtr.Zero)
        {
            error = SDL.GetError();
            if (string.IsNullOrEmpty(error))
                error = "could not open recording";
            return IntPtr.Zero;
        }

        if (!SDL.ResumeAudioStreamDevice(stream))
        {
            error = SDL.GetError();
            SDL.DestroyAudioStream(stream);
            if (string.IsNullOrEmpty(error))
                error = "could not start recording";
            return IntPtr.Zero;
        }

        if (SDL.GetAudioStreamFormat(stream, out _, out SDL.AudioSpec dst))
        {
            if (dst.Channels > 0)
                outChannels = dst.Channels;
            if (dst.Freq > 0)
                outRate = dst.Freq;
            if (dst.Format != SDL.AudioFormat.AudioF32LE)
            {
                SDL.DestroyAudioStream(stream);
                error = $"unexpected capture format {dst.Format}";
                return IntPtr.Zero;
            }
        }

        return stream;
    }

    private void LogInputOpenFailure(string name, string reason)
    {
        if (!_inputCaptureFailures.Add(name))
            return;
        string message = $"Could not meter recording device '{name}': {reason}";
        _globalSignals?.EmitSignal(nameof(GlobalSignals.Log), message, (int)LogType.Warning);
        GD.PrintErr($"AudioDevices:TryOpenInputCapture - {message}");
    }

    private void CloseInputCapture(string name)
    {
        if (!_inputCaptures.TryGetValue(name, out var capture))
            return;

        _inputCaptures.Remove(name);
        _inputCaptureFailures.Remove(name);
        if (capture.Stream == IntPtr.Zero)
            return;

        try
        {
            SDL.DestroyAudioStream(capture.Stream);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"AudioDevices:CloseInputCapture - {name}: {ex.Message}");
        }
    }
}
