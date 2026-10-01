// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System;
using System.Runtime.InteropServices;

namespace Cue2.Services;

/// <summary>
/// Windows recording endpoints that share a hardware clock with an output
/// (Steinberg US-2x2 and similar interfaces) refuse <c>IAudioClient::Initialize</c>
/// when the input format and the output format are different rates.
/// WASAPI still reports the mismatched input format as supported, and SDL
/// surfaces the empty <c>AUDCLNT_E_UNSUPPORTED_FORMAT</c> text.
/// </summary>
internal static class WasapiRecordingClock
{
    private const int DeviceStateActive = 0x1;
    private const int DataFlowRender = 0;
    private const int DataFlowCapture = 1;
    private const int ClsCtxAll = 23;
    private const int CoInitMultithreaded = 0;
    private const int RpcEChangedMode = unchecked((int)0x80010106);

    /// <summary>
    /// When <paramref name="captureName"/> is running at a different rate from
    /// the output that shares its hardware name, set the Windows input format
    /// to that output rate.
    /// </summary>
    /// <param name="captureName">Recording endpoint friendly name.</param>
    /// <param name="sampleRate">Rate to open after this call. 0 when unknown.</param>
    /// <param name="detail">What was found, including a change Cue2 made.</param>
    /// <returns>True when the Windows input format was changed.</returns>
    public static bool TryAlignCaptureToOutput(string captureName, out int sampleRate, out string detail)
    {
        sampleRate = 0;
        detail = string.Empty;
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(captureName))
            return false;

        int com = Native.CoInitializeEx(IntPtr.Zero, CoInitMultithreaded);
        bool releaseCom = com == 0 || com == 1;
        try
        {
            if (com < 0 && com != RpcEChangedMode)
            {
                detail = "Cue2 could not talk to Windows audio to check this input format.";
                return false;
            }

            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            if (!TryFind(enumerator, DataFlowCapture, captureName, out IMMDevice capture, out _))
            {
                detail = "Cue2 could not find this input in Windows audio.";
                return false;
            }

            string key = HardwareKey(captureName);
            if (!TryFindByKey(enumerator, DataFlowRender, key, captureName, out IMMDevice render, out string renderName))
            {
                sampleRate = ReadMixRate(capture);
                detail = sampleRate > 0
                    ? $"WASAPI refused the Windows input format ({sampleRate} Hz)."
                    : "WASAPI refused the Windows input format.";
                return false;
            }

            int captureRate = ReadMixRate(capture);
            int renderRate = ReadMixRate(render);
            sampleRate = captureRate;
            if (captureRate <= 0 || renderRate <= 0)
            {
                detail = "WASAPI refused the Windows input format.";
                return false;
            }

            if (captureRate == renderRate)
            {
                detail = $"WASAPI refused the Windows input format ({captureRate} Hz).";
                return false;
            }

            capture.GetId(out IntPtr idPtr);
            string deviceId = Marshal.PtrToStringUni(idPtr);
            Marshal.FreeCoTaskMem(idPtr);
            if (string.IsNullOrEmpty(deviceId))
            {
                detail = $"This input is {captureRate} Hz and '{renderName}' is {renderRate} Hz. WASAPI refused the input format.";
                return false;
            }

            var policy = (IPolicyConfig)new PolicyConfigClient();
            int hr = policy.GetDeviceFormat(deviceId, false, out IntPtr format);
            if (hr < 0 || format == IntPtr.Zero)
            {
                detail = $"This input is {captureRate} Hz and '{renderName}' is {renderRate} Hz. WASAPI refused the input format.";
                return false;
            }

            IntPtr updated = CloneAtRate(format, renderRate);
            Marshal.FreeCoTaskMem(format);
            hr = policy.SetDeviceFormat(deviceId, updated, updated);
            Marshal.FreeHGlobal(updated);
            if (hr < 0)
            {
                detail = $"This input is {captureRate} Hz and '{renderName}' is {renderRate} Hz. WASAPI refused the input format, and Cue2 could not change the Windows input format.";
                return false;
            }

            sampleRate = renderRate;
            detail = $"Windows had '{captureName}' at {captureRate} Hz. Cue2 set it to {renderRate} Hz to match '{renderName}'.";
            return true;
        }
        catch (Exception ex)
        {
            detail = ex.Message;
            return false;
        }
        finally
        {
            if (releaseCom)
                Native.CoUninitialize();
        }
    }

    private static string HardwareKey(string friendlyName)
    {
        int open = friendlyName.LastIndexOf('(');
        int close = friendlyName.LastIndexOf(')');
        if (open >= 0 && close > open)
            return friendlyName.Substring(open + 1, close - open - 1).Trim();
        return friendlyName.Trim();
    }

    private static bool TryFind(IMMDeviceEnumerator enumerator, int dataFlow, string friendlyName, out IMMDevice device, out string foundName)
    {
        device = null;
        foundName = null;
        enumerator.EnumAudioEndpoints(dataFlow, DeviceStateActive, out IMMDeviceCollection devices);
        devices.GetCount(out int count);
        for (int i = 0; i < count; i++)
        {
            devices.Item(i, out IMMDevice candidate);
            string name = FriendlyName(candidate);
            if (!string.Equals(name, friendlyName, StringComparison.Ordinal))
                continue;
            device = candidate;
            foundName = name;
            return true;
        }

        return false;
    }

    private static bool TryFindByKey(IMMDeviceEnumerator enumerator, int dataFlow, string key, string skipName, out IMMDevice device, out string foundName)
    {
        device = null;
        foundName = null;
        if (string.IsNullOrEmpty(key))
            return false;

        enumerator.EnumAudioEndpoints(dataFlow, DeviceStateActive, out IMMDeviceCollection devices);
        devices.GetCount(out int count);
        for (int i = 0; i < count; i++)
        {
            devices.Item(i, out IMMDevice candidate);
            string name = FriendlyName(candidate);
            if (string.Equals(name, skipName, StringComparison.Ordinal))
                continue;
            if (!string.Equals(HardwareKey(name), key, StringComparison.OrdinalIgnoreCase))
                continue;
            device = candidate;
            foundName = name;
            return true;
        }

        return false;
    }

    private static int ReadMixRate(IMMDevice device)
    {
        var iid = new Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2");
        int hr = device.Activate(ref iid, ClsCtxAll, IntPtr.Zero, out IntPtr punk);
        if (hr < 0 || punk == IntPtr.Zero)
            return 0;

#pragma warning disable CA1416
        var client = (IAudioClient)Marshal.GetObjectForIUnknown(punk);
        Marshal.Release(punk);
#pragma warning restore CA1416
        try
        {
            hr = client.GetMixFormat(out IntPtr format);
            if (hr < 0 || format == IntPtr.Zero)
                return 0;
            int rate = Marshal.ReadInt32(format, 4);
            Marshal.FreeCoTaskMem(format);
            return rate;
        }
        finally
        {
#pragma warning disable CA1416
            Marshal.ReleaseComObject(client);
#pragma warning restore CA1416
        }
    }

    private static string FriendlyName(IMMDevice device)
    {
        var key = new PropertyKey
        {
            fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"),
            pid = 14
        };
        device.OpenPropertyStore(0, out IPropertyStore store);
        store.GetValue(ref key, out PropVariant value);
        if (value.vt == 31 && value.pointer != IntPtr.Zero)
            return Marshal.PtrToStringUni(value.pointer) ?? string.Empty;
        return string.Empty;
    }

    private static IntPtr CloneAtRate(IntPtr source, int sampleRate)
    {
        ushort formatTag = (ushort)Marshal.ReadInt16(source, 0);
        ushort blockAlign = (ushort)Marshal.ReadInt16(source, 12);
        ushort cbSize = (ushort)Marshal.ReadInt16(source, 16);
        int bytes = 18 + (formatTag == 0xFFFE ? cbSize : 0);
        if (bytes < 18)
            bytes = 18;
        byte[] copy = new byte[bytes];
        Marshal.Copy(source, copy, 0, bytes);
        IntPtr dest = Marshal.AllocHGlobal(bytes);
        Marshal.Copy(copy, 0, dest, bytes);
        Marshal.WriteInt32(dest, 4, sampleRate);
        Marshal.WriteInt32(dest, 8, sampleRate * blockAlign);
        return dest;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey
    {
        public Guid fmtid;
        public int pid;
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropVariant
    {
        [FieldOffset(0)] public ushort vt;
        [FieldOffset(8)] public IntPtr pointer;
    }

    [ComImport]
    [Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumerator
    {
    }

    [ComImport]
    [Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")]
    private class PolicyConfigClient
    {
    }

    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
    }

    [ComImport]
    [Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceCollection
    {
        int GetCount(out int count);
        int Item(int index, out IMMDevice device);
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, out IntPtr client);
        int OpenPropertyStore(int access, out IPropertyStore store);
        int GetId(out IntPtr id);
        int GetState(out int state);
    }

    [ComImport]
    [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        int GetCount(out int count);
        int GetAt(int index, out PropertyKey key);
        int GetValue(ref PropertyKey key, out PropVariant value);
    }

    [ComImport]
    [Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioClient
    {
        [PreserveSig] int Initialize(int shareMode, int streamFlags, long bufferDuration, long periodicity, IntPtr format, IntPtr session);
        [PreserveSig] int GetBufferSize(out int frames);
        [PreserveSig] int GetStreamLatency(out long latency);
        [PreserveSig] int GetCurrentPadding(out int padding);
        [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closest);
        [PreserveSig] int GetMixFormat(out IntPtr format);
        [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(IntPtr handle);
        [PreserveSig] int GetService(ref Guid iid, out IntPtr service);
    }

    [ComImport]
    [Guid("f8679f50-850a-41cf-9c72-430f290290c8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId, out IntPtr format);
        [PreserveSig] int GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId, [MarshalAs(UnmanagedType.Bool)] bool isDefault, out IntPtr format);
        [PreserveSig] int ResetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId);
        [PreserveSig] int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr endpointFormat, IntPtr mixFormat);
    }

    private static class Native
    {
        [DllImport("ole32.dll")]
        public static extern int CoInitializeEx(IntPtr reserved, uint coInit);

        [DllImport("ole32.dll")]
        public static extern void CoUninitialize();
    }
}
