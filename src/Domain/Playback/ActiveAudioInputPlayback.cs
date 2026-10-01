// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Cue2.Domain.Cues;
using Cue2.Media.Audio;
using Cue2.Services;
using Godot;
using SDL3;

namespace Cue2.Domain.Playback;

/// <summary>
/// Plays an <see cref="AudioInputComponent"/>: capture the patch, sum it to the mono submaster,
/// and route that bus to the cue output while the cue is active.
/// </summary>
public partial class ActiveAudioInputPlayback : GodotObject, IAudioPlayback
{
    private const int SampleRate = 48000;
    private const int FramesPerChunk = 256;

    private readonly AudioInputComponent _component;
    private readonly AudioDevices _audioDevices;
    private readonly List<Capture> _captures = new();
    private readonly object _lock = new();

    private CancellationTokenSource _fillCts;
    private Task _fillTask;
    private float[] _mono;
    private float[] _mix;
    private byte[] _pcmBytes;
    private long _framesSent;
    private bool _cleaned;
    private bool _stopRequested;
    private double _requestedFadeOut;
    private double _fadeInSeconds;
    private long _fadeOutStartMs = -1;
    private readonly System.Diagnostics.Stopwatch _clock = new();
    private float _envelope;

    /// <summary>True while the fade-in ramp is running.</summary>
    public bool IsFadingIn { get; private set; }

    /// <summary>True while a stop or end fade-out is running.</summary>
    public bool IsFadingOut { get; private set; }

    /// <summary>Fade envelope, 0 silent to 1 full. Drives the component fade overlay.</summary>
    public float CurrentVolume => _envelope;

    /// <summary>Seconds of audio written since play, ignoring pause.</summary>
    public double ElapsedSeconds { get; private set; }

    /// <summary>True while pause is holding the output at silence.</summary>
    public bool IsPaused { get; private set; }

    /// <summary>Raised once when playback finishes or is cleaned up.</summary>
    public event Action Completed;

    public AudioOutputPatch Patch { get; set; }
    public string DirectOutput { get; set; }
    public CuePatch Routing { get; set; }
    public Dictionary<uint, IntPtr> DeviceStreams { get; set; } = new();
    public Dictionary<uint, int> DeviceStreamChannels { get; set; } = new();
    public int SourceChannels { get; set; } = 1;
    public int SourceSampleRate { get; set; } = SampleRate;
    public int SourceBytesPerFrame { get; set; } = sizeof(float);
    public SDL.AudioFormat SourceFormat { get; set; } = SDL.AudioFormat.AudioF32LE;

    private sealed class Capture
    {
        public IntPtr Stream;
        public int Channels;
        public int PickChannel;
        public float Trim = 1f;
        public byte[] Buffer;
    }

    /// <summary>
    /// Creates a playback bound to <paramref name="component"/>.
    /// </summary>
    public ActiveAudioInputPlayback(AudioInputComponent component, AudioDevices audioDevices)
    {
        _component = component ?? throw new ArgumentNullException(nameof(component));
        _audioDevices = audioDevices ?? throw new ArgumentNullException(nameof(audioDevices));
        Patch = component.Patch;
        DirectOutput = component.DirectOutput;
        Routing = component.Routing;
        _mono = new float[FramesPerChunk];
        _mix = new float[FramesPerChunk * 8];
        _pcmBytes = new byte[FramesPerChunk * 8 * sizeof(float)];
    }

    /// <summary>
    /// Opens the patch inputs and starts filling the output streams.
    /// </summary>
    /// <param name="fadeInSeconds">Fade-in length. 0 starts at full level.</param>
    public Task PlayAsync(double fadeInSeconds)
    {
        OpenCaptures();
        _fadeInSeconds = Math.Max(0, fadeInSeconds);
        _fadeOutStartMs = -1;
        _envelope = _fadeInSeconds > 1e-4 ? 0f : 1f;
        IsFadingIn = _fadeInSeconds > 1e-4;
        IsFadingOut = false;
        _framesSent = 0;
        ElapsedSeconds = 0;
        IsPaused = false;
        _stopRequested = false;
        _clock.Restart();
        _fillCts = new CancellationTokenSource();
        var token = _fillCts.Token;
        _fillTask = Task.Run(() => FillLoop(token), token);
        return Task.CompletedTask;
    }

    /// <summary>Holds the output at silence without closing the inputs.</summary>
    public void Pause() => IsPaused = true;

    /// <summary>Continues output after <see cref="Pause"/>.</summary>
    public void Resume() => IsPaused = false;

    /// <summary>
    /// Fades out, then tears the playback down.
    /// </summary>
    /// <param name="fadeOutSeconds">Fade length before close.</param>
    public async Task Stop(double fadeOutSeconds)
    {
        _requestedFadeOut = Math.Max(0, fadeOutSeconds);
        _stopRequested = true;
        if (fadeOutSeconds > 1e-4 && _fillTask != null)
        {
            try { await _fillTask; }
            catch (OperationCanceledException) { }
        }
        Clean();
    }

    /// <summary>Stops capture and output and raises <see cref="Completed"/> once.</summary>
    public void Clean()
    {
        lock (_lock)
        {
            if (_cleaned)
                return;
            _cleaned = true;
        }

        try { _fillCts?.Cancel(); } catch { /* already done */ }
        CloseCaptures();
        try { _audioDevices?.NotifyPlaybackCompleted(this); } catch { /* device map */ }
        if (DeviceStreams != null)
        {
            foreach (var stream in DeviceStreams.Values)
            {
                try { if (stream != IntPtr.Zero) SDL.DestroyAudioStream(stream); }
                catch { /* ignore */ }
            }
            DeviceStreams.Clear();
        }
        DeviceStreamChannels?.Clear();
        try { Completed?.Invoke(); }
        catch (Exception ex)
        {
            GD.PrintErr($"ActiveAudioInputPlayback:Clean - {ex.Message}");
        }
    }

    /// <inheritdoc />
    public void OnOutputDeviceLost(uint logicalDeviceId)
    {
        DeviceStreams?.Remove(logicalDeviceId);
        DeviceStreamChannels?.Remove(logicalDeviceId);
        if (DeviceStreams == null || DeviceStreams.Count == 0)
            Clean();
    }

    private void OpenCaptures()
    {
        var patch = _component.InputPatch;
        if (patch == null)
            return;

        var names = new List<string>();
        foreach (var channel in patch.Channels)
        {
            if (!string.IsNullOrWhiteSpace(channel.DeviceName))
                names.Add(channel.DeviceName);
        }
        _audioDevices.ReleaseInputMonitors(names);

        var byDevice = new Dictionary<string, Capture>(StringComparer.Ordinal);
        foreach (var channel in patch.Channels)
        {
            if (string.IsNullOrWhiteSpace(channel.DeviceName) || channel.DeviceChannel < 0)
                continue;
            if (!byDevice.TryGetValue(channel.DeviceName, out var opened))
            {
                opened = OpenDevice(channel.DeviceName);
                if (opened == null)
                    continue;
                byDevice[channel.DeviceName] = opened;
            }

            _captures.Add(new Capture
            {
                Stream = opened.Stream,
                Channels = opened.Channels,
                PickChannel = channel.DeviceChannel,
                Trim = DbToLinear(channel.TrimDb),
                Buffer = opened.Buffer
            });
        }
    }

    private Capture OpenDevice(string name)
    {
        try
        {
            if (!_audioDevices.TryOpenRecordingStream(name, SampleRate, out IntPtr stream, out int channels, out int rate, out string error))
            {
                GD.PrintErr($"ActiveAudioInputPlayback:OpenDevice - {name}: {error}");
                return null;
            }

            if (rate > 0 && rate != SampleRate)
                GD.Print($"ActiveAudioInputPlayback:OpenDevice - '{name}' is running at {rate} Hz.");

            return new Capture
            {
                Stream = stream,
                Channels = channels,
                Buffer = new byte[FramesPerChunk * channels * sizeof(float)]
            };
        }
        catch (Exception ex)
        {
            GD.PrintErr($"ActiveAudioInputPlayback:OpenDevice - {name}: {ex.Message}");
            return null;
        }
    }

    private void CloseCaptures()
    {
        var closed = new HashSet<IntPtr>();
        foreach (var capture in _captures)
        {
            if (capture.Stream == IntPtr.Zero || !closed.Add(capture.Stream))
                continue;
            try { SDL.DestroyAudioStream(capture.Stream); }
            catch { /* ignore */ }
            capture.Stream = IntPtr.Zero;
        }
        _captures.Clear();
    }

    private void FillLoop(CancellationToken token)
    {
        try
        {
            double naturalFadeOut = Math.Max(0, _component.FadeOutDuration);
            double hold = _component.Duration;
            bool finite = hold > 1e-4;

            while (!token.IsCancellationRequested && !_cleaned)
            {
                bool finished = UpdateFadeEnvelope(naturalFadeOut, hold, finite);
                if (finished)
                    break;

                if (IsPaused || DeviceStreams == null || DeviceStreams.Count == 0)
                {
                    Thread.Sleep(16);
                    continue;
                }

                int frames = ReadMonoChunk();
                if (frames <= 0)
                {
                    Thread.Sleep(4);
                    continue;
                }

                Push(frames, _envelope);
                _framesSent += frames;
            }
        }
        catch (Exception ex)
        {
            GD.PrintErr($"ActiveAudioInputPlayback:FillLoop - {ex.Message}");
        }

        if (!_cleaned)
            Callable.From(Clean).CallDeferred();
    }

    /// <summary>
    /// Updates fade-in / fade-out flags and <see cref="CurrentVolume"/> from the play clock.
    /// </summary>
    /// <returns>True when the fade-out has finished and playback should close.</returns>
    private bool UpdateFadeEnvelope(double naturalFadeOut, double hold, bool finite)
    {
        double elapsed = _clock.Elapsed.TotalSeconds;
        ElapsedSeconds = elapsed;

        float env = 1f;
        bool fadingIn = _fadeInSeconds > 1e-4 && elapsed < _fadeInSeconds;
        if (fadingIn)
            env = (float)Math.Clamp(elapsed / _fadeInSeconds, 0, 1);

        double fadeOutSeconds = _stopRequested ? _requestedFadeOut : naturalFadeOut;
        bool beginFadeOut = _stopRequested
            || (finite && elapsed >= Math.Max(0, hold - naturalFadeOut));
        if (beginFadeOut)
        {
            if (_fadeOutStartMs < 0)
                _fadeOutStartMs = _clock.ElapsedMilliseconds;
            double into = (_clock.ElapsedMilliseconds - _fadeOutStartMs) / 1000.0;
            if (fadeOutSeconds <= 1e-4)
            {
                _envelope = 0f;
                IsFadingIn = false;
                IsFadingOut = true;
                return true;
            }

            env *= (float)Math.Clamp(1.0 - (into / fadeOutSeconds), 0, 1);
            IsFadingIn = false;
            IsFadingOut = true;
            _envelope = env;
            return into >= fadeOutSeconds;
        }

        IsFadingIn = fadingIn;
        IsFadingOut = false;
        _envelope = env;
        return false;
    }

    private int ReadMonoChunk()
    {
        int frames = FramesPerChunk;
        Array.Clear(_mono, 0, frames);
        float submaster = DbToLinear(_component.InputPatch?.TrimDb ?? 0f);
        bool any = false;
        var seen = new HashSet<IntPtr>();
        foreach (var capture in _captures)
        {
            if (capture.Stream == IntPtr.Zero || capture.Channels <= 0 || !seen.Add(capture.Stream))
                continue;
            int frameBytes = capture.Channels * sizeof(float);
            int want = frames * frameBytes;
            if (capture.Buffer.Length < want)
                continue;
            int available = SDL.GetAudioStreamAvailable(capture.Stream);
            if (available < frameBytes)
                continue;
            int toRead = Math.Min(available, want);
            toRead -= toRead % frameBytes;
            int got = SDL.GetAudioStreamData(capture.Stream, capture.Buffer, toRead);
            if (got < frameBytes)
                continue;
            int gotFrames = Math.Min(got / frameBytes, frames);
            frames = Math.Min(frames, gotFrames);
            any = true;
            foreach (var view in _captures)
            {
                if (view.Stream != capture.Stream)
                    continue;
                int pick = Math.Clamp(view.PickChannel, 0, view.Channels - 1);
                for (int f = 0; f < gotFrames && f < _mono.Length; f++)
                {
                    float sample = BinaryPrimitives.ReadSingleLittleEndian(
                        capture.Buffer.AsSpan((f * view.Channels + pick) * sizeof(float), sizeof(float)));
                    _mono[f] += sample * view.Trim * submaster;
                }
            }
        }
        return any ? frames : 0;
    }

    private void Push(int frames, float envelope)
    {
        float componentVol = AudioMixMatrix.ClampComponentGainLinear((float)_component.Volume);
        float master = envelope * (_audioDevices.GetEffectiveSessionMasterLinear());
        foreach (var kv in DeviceStreams)
        {
            if (kv.Value == IntPtr.Zero)
                continue;
            if (!DeviceStreamChannels.TryGetValue(kv.Key, out int outCh) || outCh <= 0)
                continue;
            int outSamples = frames * outCh;
            if (_mix.Length < outSamples)
                _mix = new float[outSamples];
            string deviceName = _audioDevices.GetAudioDeviceByLogicalId(kv.Key)?.Name;
            AudioMixMatrix.Mix(
                _mono.AsSpan(0, frames),
                frames,
                1,
                _mix.AsSpan(0, outSamples),
                outCh,
                master,
                componentVol,
                0f,
                Routing,
                Patch,
                deviceName,
                !string.IsNullOrEmpty(DirectOutput));

            if (_audioDevices != null)
            {
                _audioDevices.GetOutputLimits(out float maxAbs, out float minAbs);
                AudioMixMatrix.ApplyOutputLimits(_mix.AsSpan(0, outSamples), maxAbs, minAbs);
            }

            int byteCount = outSamples * sizeof(float);
            if (_pcmBytes.Length < byteCount)
                _pcmBytes = new byte[byteCount];
            Buffer.BlockCopy(_mix, 0, _pcmBytes, 0, byteCount);
            SDL.PutAudioStreamData(kv.Value, _pcmBytes, byteCount);
        }
    }

    private static float DbToLinear(float db)
    {
        if (db <= -60f)
            return 0f;
        if (db > 12f)
            db = 12f;
        return MathF.Pow(10f, db / 20f);
    }
}
