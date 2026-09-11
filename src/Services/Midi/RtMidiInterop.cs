// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Godot;

namespace Cue2.Services;

/// <summary>
/// Official RtMidi 6.0 C API loaded via <see cref="NativeLibrary"/> (not DllImport search).
/// </summary>
/// <remarks>
/// Natives live in <c>res://bin/{platform}/</c> as <c>rtmidi.dll</c> / <c>librtmidi.dylib</c> /
/// <c>librtmidi.so</c>. Linux builds are ALSA-only (need system <c>libasound.so.2</c>).
/// </remarks>
public static class RtMidiInterop
{
    /// <summary>Client name shown to the OS MIDI stack (ALSA/CoreMIDI).</summary>
    public const string ClientName = "Cue2";

    private const uint InputQueueSize = 256;

    private static readonly object LoadLock = new();
    private static IntPtr _libraryHandle;
    private static string _loadedPath = string.Empty;

    private static Delegates.GetVersion _getVersion;
    private static Delegates.InCreate _inCreate;
    private static Delegates.InFree _inFree;
    private static Delegates.OutCreate _outCreate;
    private static Delegates.OutFree _outFree;
    private static Delegates.GetPortCount _getPortCount;
    private static Delegates.GetPortName _getPortName;
    private static Delegates.OpenPort _openPort;
    private static Delegates.ClosePort _closePort;
    private static Delegates.InSetCallback _inSetCallback;
    private static Delegates.InCancelCallback _inCancelCallback;
    private static Delegates.InIgnoreTypes _inIgnoreTypes;
    private static Delegates.OutSendMessage _outSendMessage;

    /// <summary>True after a successful <see cref="TryLoad"/>.</summary>
    public static bool IsLoaded
    {
        get
        {
            lock (LoadLock)
                return _libraryHandle != IntPtr.Zero;
        }
    }

    /// <summary>Absolute path of the loaded native library, or empty.</summary>
    public static string LoadedPath
    {
        get
        {
            lock (LoadLock)
                return _loadedPath;
        }
    }

    /// <summary>
    /// Resolves and loads the platform RtMidi shared library, then binds C API exports.
    /// </summary>
    /// <param name="path">Full path loaded, or empty on failure.</param>
    /// <param name="error">Failure reason, or empty on success.</param>
    /// <returns><c>true</c> when the library is ready to use.</returns>
    public static bool TryLoad(out string path, out string error)
    {
        lock (LoadLock)
        {
            if (_libraryHandle != IntPtr.Zero)
            {
                path = _loadedPath;
                error = string.Empty;
                return true;
            }

            path = string.Empty;
            string fileName = NativeLibPaths.GetRtMidiNativeFileName(out string platformLabel);
            if (string.IsNullOrEmpty(fileName))
            {
                error = $"RtMidi is not supported on {platformLabel}.";
                return false;
            }

            string platformDir = NativeLibPaths.GetPlatformDir(out _);
            string found = NativeLibPaths.FindLibraryFile(fileName, platformDir, out string foundDir, out var tried);
            if (string.IsNullOrEmpty(found))
            {
                error = $"{fileName} not found for {platformLabel}. Tried: {NativeLibPaths.FormatTriedDirectories(tried)}";
                return false;
            }

            if (!NativeLibrary.TryLoad(found, out IntPtr handle) || handle == IntPtr.Zero)
            {
                error = $"NativeLibrary.TryLoad failed for {found}";
                return false;
            }

            try
            {
                _getVersion = Bind<Delegates.GetVersion>(handle, "rtmidi_get_version");
                _inCreate = Bind<Delegates.InCreate>(handle, "rtmidi_in_create");
                _inFree = Bind<Delegates.InFree>(handle, "rtmidi_in_free");
                _outCreate = Bind<Delegates.OutCreate>(handle, "rtmidi_out_create");
                _outFree = Bind<Delegates.OutFree>(handle, "rtmidi_out_free");
                _getPortCount = Bind<Delegates.GetPortCount>(handle, "rtmidi_get_port_count");
                _getPortName = Bind<Delegates.GetPortName>(handle, "rtmidi_get_port_name");
                _openPort = Bind<Delegates.OpenPort>(handle, "rtmidi_open_port");
                _closePort = Bind<Delegates.ClosePort>(handle, "rtmidi_close_port");
                _inSetCallback = Bind<Delegates.InSetCallback>(handle, "rtmidi_in_set_callback");
                _inCancelCallback = Bind<Delegates.InCancelCallback>(handle, "rtmidi_in_cancel_callback");
                _inIgnoreTypes = Bind<Delegates.InIgnoreTypes>(handle, "rtmidi_in_ignore_types");
                _outSendMessage = Bind<Delegates.OutSendMessage>(handle, "rtmidi_out_send_message");
            }
            catch (Exception ex)
            {
                NativeLibrary.Free(handle);
                error = $"Missing RtMidi export in {found}: {ex.Message}";
                return false;
            }

            _libraryHandle = handle;
            _loadedPath = found;
            path = found;
            error = string.Empty;
            string version = SafeVersion();
            GD.Print($"RtMidiInterop:TryLoad - Loaded {found} [{platformLabel}] version={version}");
            return true;
        }
    }

    /// <summary>RtMidi version string, or empty if unavailable.</summary>
    public static string SafeVersion()
    {
        try
        {
            if (_getVersion == null)
                return string.Empty;
            IntPtr p = _getVersion();
            return p == IntPtr.Zero ? string.Empty : Marshal.PtrToStringUTF8(p) ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>System MIDI input port names (unique, sorted, case-insensitive).</summary>
    public static List<string> ListInputNames()
    {
        return ListNames(input: true);
    }

    /// <summary>System MIDI output port names (unique, sorted, case-insensitive).</summary>
    public static List<string> ListOutputNames()
    {
        return ListNames(input: false);
    }

    private static List<string> ListNames(bool input)
    {
        var names = new List<string>();
        if (!IsLoaded)
            return names;

        IntPtr wrapper = IntPtr.Zero;
        try
        {
            wrapper = input
                ? _inCreate(RtMidiApi.Unspecified, ClientName, InputQueueSize)
                : _outCreate(RtMidiApi.Unspecified, ClientName);
            if (wrapper == IntPtr.Zero || !ReadOk(wrapper))
                return names;

            uint count = _getPortCount(wrapper);
            for (uint i = 0; i < count; i++)
            {
                string name = ReadPortName(wrapper, i);
                if (string.IsNullOrWhiteSpace(name))
                    continue;
                if (!names.Exists(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase)))
                    names.Add(name);
            }

            names.Sort(StringComparer.OrdinalIgnoreCase);
            return names;
        }
        catch (Exception ex)
        {
            GD.PrintErr($"RtMidiInterop:ListNames - {(input ? "input" : "output")}: {ex.Message}");
            return names;
        }
        finally
        {
            FreeWrapper(wrapper, input);
        }
    }

    /// <summary>
    /// Opens a named input port and starts the unmanaged receive callback.
    /// </summary>
    public static bool TryOpenInput(string portName, Action<byte[]> onMessage, Action<string> onError, out InputPort port, out string error)
    {
        port = null;
        error = string.Empty;
        if (!IsLoaded)
        {
            error = "RtMidi is not loaded.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(portName))
        {
            error = "Empty port name.";
            return false;
        }

        IntPtr wrapper = IntPtr.Zero;
        try
        {
            wrapper = _inCreate(RtMidiApi.Unspecified, ClientName, InputQueueSize);
            if (wrapper == IntPtr.Zero)
            {
                error = "rtmidi_in_create returned null.";
                return false;
            }

            if (!ReadOk(wrapper))
            {
                error = ReadMsg(wrapper) ?? "rtmidi_in_create failed.";
                FreeWrapper(wrapper, input: true);
                return false;
            }

            if (!TryFindPortIndex(wrapper, portName, out uint index))
            {
                error = $"Input port '{portName}' is not available.";
                FreeWrapper(wrapper, input: true);
                return false;
            }

            _openPort(wrapper, index, ClientName);
            if (!ReadOk(wrapper))
            {
                error = ReadMsg(wrapper) ?? $"Failed to open input '{portName}'.";
                FreeWrapper(wrapper, input: true);
                return false;
            }

            // Channel voice only — drop clock/active-sensing/sysex (monitor still sees unknown shorts).
            _inIgnoreTypes(wrapper, midiSysex: true, midiTime: true, midiSense: true);

            port = new InputPort(wrapper, portName, onMessage, onError);
            _inSetCallback(wrapper, port.NativeCallback, IntPtr.Zero);
            if (!ReadOk(wrapper))
            {
                error = ReadMsg(wrapper) ?? "rtmidi_in_set_callback failed.";
                port.Dispose();
                port = null;
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            if (port != null)
            {
                port.Dispose();
                port = null;
            }
            else
            {
                FreeWrapper(wrapper, input: true);
            }

            return false;
        }
    }

    /// <summary>
    /// Opens a named output port for sending channel messages.
    /// </summary>
    public static bool TryOpenOutput(string portName, out OutputPort port, out string error)
    {
        port = null;
        error = string.Empty;
        if (!IsLoaded)
        {
            error = "RtMidi is not loaded.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(portName))
        {
            error = "Empty port name.";
            return false;
        }

        IntPtr wrapper = IntPtr.Zero;
        try
        {
            wrapper = _outCreate(RtMidiApi.Unspecified, ClientName);
            if (wrapper == IntPtr.Zero)
            {
                error = "rtmidi_out_create returned null.";
                return false;
            }

            if (!ReadOk(wrapper))
            {
                error = ReadMsg(wrapper) ?? "rtmidi_out_create failed.";
                FreeWrapper(wrapper, input: false);
                return false;
            }

            if (!TryFindPortIndex(wrapper, portName, out uint index))
            {
                error = $"Output port '{portName}' is not available.";
                FreeWrapper(wrapper, input: false);
                return false;
            }

            _openPort(wrapper, index, ClientName);
            if (!ReadOk(wrapper))
            {
                error = ReadMsg(wrapper) ?? $"Failed to open output '{portName}'.";
                FreeWrapper(wrapper, input: false);
                return false;
            }

            port = new OutputPort(wrapper, portName);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            FreeWrapper(wrapper, input: false);
            return false;
        }
    }

    private static bool TryFindPortIndex(IntPtr wrapper, string portName, out uint index)
    {
        index = 0;
        uint count = _getPortCount(wrapper);
        for (uint i = 0; i < count; i++)
        {
            string name = ReadPortName(wrapper, i);
            if (string.Equals(name, portName, StringComparison.OrdinalIgnoreCase))
            {
                index = i;
                return true;
            }
        }

        return false;
    }

    private static string ReadPortName(IntPtr wrapper, uint index)
    {
        int len = 0;
        int rc = _getPortName(wrapper, index, IntPtr.Zero, ref len);
        if (rc < 0 || len <= 1)
            return string.Empty;

        IntPtr buf = Marshal.AllocHGlobal(len);
        try
        {
            int bufLen = len;
            _getPortName(wrapper, index, buf, ref bufLen);
            return Marshal.PtrToStringUTF8(buf)?.TrimEnd('\0') ?? string.Empty;
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }

    private static void FreeWrapper(IntPtr wrapper, bool input)
    {
        if (wrapper == IntPtr.Zero)
            return;
        try
        {
            if (input)
                _inFree?.Invoke(wrapper);
            else
                _outFree?.Invoke(wrapper);
        }
        catch
        {
            // already freed / native tore down
        }
    }

    /// <summary>RtMidi 6.0 <c>RtMidiWrapper</c> (64-bit).</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct Wrapper
    {
        public IntPtr Ptr;
        public IntPtr Data;
        public byte Ok;
        public byte Pad1;
        public byte Pad2;
        public byte Pad3;
        public int PadToPtr;
        public IntPtr Msg;
    }

    private static bool ReadOk(IntPtr wrapper)
    {
        if (wrapper == IntPtr.Zero)
            return false;
        var w = Marshal.PtrToStructure<Wrapper>(wrapper);
        return w.Ok != 0;
    }

    private static string ReadMsg(IntPtr wrapper)
    {
        if (wrapper == IntPtr.Zero)
            return null;
        var w = Marshal.PtrToStructure<Wrapper>(wrapper);
        if (w.Msg == IntPtr.Zero)
            return null;
        return Marshal.PtrToStringUTF8(w.Msg) ?? Marshal.PtrToStringAnsi(w.Msg);
    }

    private static T Bind<T>(IntPtr handle, string exportName) where T : Delegate
    {
        IntPtr symbol = NativeLibrary.GetExport(handle, exportName);
        return Marshal.GetDelegateForFunctionPointer<T>(symbol);
    }

    private enum RtMidiApi
    {
        Unspecified = 0
    }

    private static class Delegates
    {
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate IntPtr GetVersion();

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate IntPtr InCreate(RtMidiApi api, [MarshalAs(UnmanagedType.LPUTF8Str)] string clientName, uint queueSizeLimit);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void InFree(IntPtr device);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate IntPtr OutCreate(RtMidiApi api, [MarshalAs(UnmanagedType.LPUTF8Str)] string clientName);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void OutFree(IntPtr device);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate uint GetPortCount(IntPtr device);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate int GetPortName(IntPtr device, uint portNumber, IntPtr bufOut, ref int bufLen);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void OpenPort(IntPtr device, uint portNumber, [MarshalAs(UnmanagedType.LPUTF8Str)] string portName);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void ClosePort(IntPtr device);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void InSetCallback(IntPtr device, MidiCallback callback, IntPtr userData);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void InCancelCallback(IntPtr device);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void InIgnoreTypes(IntPtr device, [MarshalAs(UnmanagedType.I1)] bool midiSysex, [MarshalAs(UnmanagedType.I1)] bool midiTime, [MarshalAs(UnmanagedType.I1)] bool midiSense);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate int OutSendMessage(IntPtr device, byte[] message, int length);
    }

    /// <summary>RtMidi C callback: timestamp, message pointer, size, user data.</summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void MidiCallback(double timeStamp, IntPtr message, UIntPtr messageSize, IntPtr userData);

    /// <summary>Open RtMidi input port with a live receive callback.</summary>
    public sealed class InputPort : IDisposable
    {
        private readonly object _gate = new();
        private IntPtr _wrapper;
        private readonly Action<byte[]> _onMessage;
        private readonly Action<string> _onError;
        private bool _disposed;

        /// <summary>Keeps the unmanaged callback from being collected.</summary>
        internal readonly MidiCallback NativeCallback;

        /// <summary>Port name used to open this handle.</summary>
        public string Name { get; }

        /// <summary>True until <see cref="Dispose"/>.</summary>
        public bool IsOpen
        {
            get
            {
                lock (_gate)
                    return !_disposed && _wrapper != IntPtr.Zero;
            }
        }

        internal InputPort(IntPtr wrapper, string name, Action<byte[]> onMessage, Action<string> onError)
        {
            _wrapper = wrapper;
            Name = name;
            _onMessage = onMessage;
            _onError = onError;
            NativeCallback = OnNativeMessage;
        }

        private void OnNativeMessage(double timeStamp, IntPtr message, UIntPtr messageSize, IntPtr userData)
        {
            try
            {
                int len = (int)messageSize;
                if (len <= 0 || message == IntPtr.Zero)
                    return;
                var bytes = new byte[len];
                Marshal.Copy(message, bytes, 0, len);
                _onMessage?.Invoke(bytes);
            }
            catch (Exception ex)
            {
                try { _onError?.Invoke(ex.Message); }
                catch { /* swallow callback errors */ }
            }
        }

        /// <inheritdoc />
        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed)
                    return;
                _disposed = true;
                IntPtr wrapper = _wrapper;
                _wrapper = IntPtr.Zero;
                if (wrapper == IntPtr.Zero)
                    return;
                try { _inCancelCallback?.Invoke(wrapper); }
                catch { /* ignore */ }
                try { _closePort?.Invoke(wrapper); }
                catch { /* ignore */ }
                FreeWrapper(wrapper, input: true);
            }
        }
    }

    /// <summary>Open RtMidi output port.</summary>
    public sealed class OutputPort : IDisposable
    {
        private readonly object _gate = new();
        private IntPtr _wrapper;
        private bool _disposed;

        /// <summary>Port name used to open this handle.</summary>
        public string Name { get; }

        /// <summary>True until <see cref="Dispose"/>.</summary>
        public bool IsOpen
        {
            get
            {
                lock (_gate)
                    return !_disposed && _wrapper != IntPtr.Zero && ReadOk(_wrapper);
            }
        }

        internal OutputPort(IntPtr wrapper, string name)
        {
            _wrapper = wrapper;
            Name = name;
        }

        /// <summary>
        /// Sends a raw MIDI message. Returns <c>false</c> on native failure.
        /// </summary>
        public bool TrySend(byte[] message, out string error)
        {
            error = string.Empty;
            if (message == null || message.Length == 0)
            {
                error = "Empty MIDI message.";
                return false;
            }

            lock (_gate)
            {
                if (_disposed || _wrapper == IntPtr.Zero)
                {
                    error = "Output port is closed.";
                    return false;
                }

                int rc = _outSendMessage(_wrapper, message, message.Length);
                if (rc != 0 || !ReadOk(_wrapper))
                {
                    error = ReadMsg(_wrapper) ?? "rtmidi_out_send_message failed.";
                    return false;
                }

                return true;
            }
        }

        /// <inheritdoc />
        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed)
                    return;
                _disposed = true;
                IntPtr wrapper = _wrapper;
                _wrapper = IntPtr.Zero;
                if (wrapper == IntPtr.Zero)
                    return;
                try { _closePort?.Invoke(wrapper); }
                catch { /* ignore */ }
                FreeWrapper(wrapper, input: false);
            }
        }
    }
}
