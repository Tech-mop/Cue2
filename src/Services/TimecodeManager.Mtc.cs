// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using Cue2.Domain.Timecode;
using Godot;

namespace Cue2.Services;

public partial class TimecodeManager
{
    private readonly Dictionary<string, MtcAssembler> _mtcAssemblers = new(StringComparer.OrdinalIgnoreCase);

    private sealed class MtcAssembler
    {
        public readonly int[] Pieces = new int[8];
        public int ReceivedMask;
    }

    private void HookMidiSystem()
    {
        if (_midiManager == null)
            _midiManager = GetNodeOrNull<MidiManager>("/root/MidiManager");
        if (_midiManager != null)
        {
            _midiManager.MidiSystemBytes += OnMidiSystemBytes;
            _midiManager.MidiStateChanged += OnMidiStateChangedForMtc;
        }
    }

    private void UnhookMidiSystem()
    {
        if (_midiManager == null)
            return;
        _midiManager.MidiSystemBytes -= OnMidiSystemBytes;
        _midiManager.MidiStateChanged -= OnMidiStateChangedForMtc;
    }

    private void OnMidiStateChangedForMtc()
    {
        if (_timecodeEnabled)
            EnsureMtcPorts();
    }

    private void EnsureMtcPorts()
    {
        if (!_timecodeEnabled || _midiManager == null)
            return;

        foreach (var endpoint in AllEndpoints())
        {
            if (endpoint.Kind != TimecodeKind.MidiTimecode)
                continue;
            if (string.IsNullOrEmpty(endpoint.SourceName))
                continue;
            bool input = endpoint.Direction == TimecodeDirection.Input;
            _midiManager.EnsureDeviceOpen(endpoint.SourceName, asInput: input);
        }
    }

    private void OnMidiSystemBytes(string deviceName, byte[] bytes)
    {
        if (!_timecodeEnabled || bytes == null || bytes.Length == 0)
            return;

        if (bytes[0] == 0xF1 && bytes.Length >= 2)
        {
            ApplyMtcQuarterFrame(deviceName, bytes[1]);
            return;
        }

        if (bytes.Length >= 10 && bytes[0] == 0xF0 && bytes[1] == 0x7F && bytes[3] == 0x01 && bytes[4] == 0x01)
            ApplyMtcFullFrame(deviceName, bytes);
    }

    private void ApplyMtcQuarterFrame(string deviceName, byte data)
    {
        int type = (data >> 4) & 0x7;
        int nibble = data & 0x0F;
        if (!_mtcAssemblers.TryGetValue(deviceName, out var asm))
        {
            asm = new MtcAssembler();
            _mtcAssemblers[deviceName] = asm;
        }

        asm.Pieces[type] = nibble;
        asm.ReceivedMask |= 1 << type;
        if (type != 7 || asm.ReceivedMask != 0xFF)
            return;

        asm.ReceivedMask = 0;
        int frames = asm.Pieces[0] | (asm.Pieces[1] << 4);
        int seconds = asm.Pieces[2] | (asm.Pieces[3] << 4);
        int minutes = asm.Pieces[4] | (asm.Pieces[5] << 4);
        int hourNibble = asm.Pieces[6];
        int rateNibble = asm.Pieces[7];
        int hours = hourNibble | ((rateNibble & 1) << 4);
        var rate = RateFromMtc(rateNibble >> 1);
        var value = new TimecodeValue(hours, minutes, seconds, frames, rate, isRunning: true, isLocked: true);
        ApplyMtcToMatchingInputs(deviceName, value);
    }

    private void ApplyMtcFullFrame(string deviceName, byte[] bytes)
    {
        int hoursByte = bytes[5];
        int hours = hoursByte & 0x1F;
        var rate = RateFromMtc((hoursByte >> 5) & 0x3);
        var value = new TimecodeValue(hours, bytes[6], bytes[7], bytes[8], rate, isRunning: true, isLocked: true);
        ApplyMtcToMatchingInputs(deviceName, value);
    }

    private void ApplyMtcToMatchingInputs(string deviceName, TimecodeValue value)
    {
        foreach (var endpoint in _inputs)
        {
            if (endpoint.Kind != TimecodeKind.MidiTimecode)
                continue;
            if (!string.Equals(endpoint.SourceName, deviceName, StringComparison.OrdinalIgnoreCase))
                continue;
            ApplyLockedInput(endpoint, value);
        }
    }

    private static TimecodeFrameRate RateFromMtc(int code) => code switch
    {
        0 => TimecodeFrameRate.Fps24,
        1 => TimecodeFrameRate.Fps25,
        2 => TimecodeFrameRate.Fps2997Drop,
        _ => TimecodeFrameRate.Fps30
    };

    private static int MtcRateCode(TimecodeFrameRate rate) => rate switch
    {
        TimecodeFrameRate.Fps24 => 0,
        TimecodeFrameRate.Fps25 => 1,
        TimecodeFrameRate.Fps2997Drop => 2,
        _ => 3
    };

    private void SendMtcForEndpoint(TimecodeEndpoint endpoint)
    {
        if (_midiManager == null || string.IsNullOrEmpty(endpoint.SourceName))
            return;
        var value = endpoint.CurrentValue;
        if (!ShouldSendIo(endpoint.Id, value.ToDisplayString()))
            return;

        int rateCode = MtcRateCode(value.FrameRate);
        byte hours = (byte)((value.Hours & 0x1F) | (rateCode << 5));

        byte[] sysex =
        {
            0xF0, 0x7F, 0x7F, 0x01, 0x01,
            hours,
            (byte)value.Minutes,
            (byte)value.Seconds,
            (byte)value.Frames,
            0xF7
        };
        _midiManager.SendRaw(endpoint.SourceName, sysex);

        int[] pieces =
        {
            value.Frames & 0x0F,
            (value.Frames >> 4) & 0x0F,
            value.Seconds & 0x0F,
            (value.Seconds >> 4) & 0x0F,
            value.Minutes & 0x0F,
            (value.Minutes >> 4) & 0x0F,
            value.Hours & 0x0F,
            ((value.Hours >> 4) & 0x01) | (rateCode << 1)
        };
        for (int i = 0; i < 8; i++)
            _midiManager.SendRaw(endpoint.SourceName, new[] { (byte)0xF1, (byte)((i << 4) | (pieces[i] & 0x0F)) });
    }
}
