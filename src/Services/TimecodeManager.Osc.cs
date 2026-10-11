// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using Cue2.Domain.Connections;
using Cue2.Domain.Timecode;
using Godot;
using Rug.Osc;

namespace Cue2.Services;

public partial class TimecodeManager
{
    private OscListen _oscListen;
    private readonly Dictionary<int, string> _lastIoSendKey = new();
    private readonly Dictionary<int, ulong> _lastIoSendMsec = new();

    private void HookOscListen()
    {
        _oscListen = GetNodeOrNull<OscListen>("/root/OscListen");
        if (_oscListen != null)
            _oscListen.MessageArrived += OnOscMessageArrived;
    }

    private void UnhookOscListen()
    {
        if (_oscListen != null)
            _oscListen.MessageArrived -= OnOscMessageArrived;
        _oscListen = null;
    }

    private void OnOscMessageArrived(OscInputMessage msg)
    {
        if (!_timecodeEnabled || !msg.IsValid)
            return;

        foreach (var endpoint in _inputs)
        {
            if (endpoint.Kind != TimecodeKind.Osc)
                continue;
            if (!OscAddressMatches(msg.Address, endpoint.SourceName))
                continue;
            if (!TryParseOscTimecode(msg, endpoint.FrameRate, out var value))
                continue;
            ApplyLockedInput(endpoint, value);
        }
    }

    private static bool OscAddressMatches(string received, string configured)
    {
        string path = string.IsNullOrWhiteSpace(configured) ? "/timecode" : configured.Trim();
        if (!path.StartsWith('/'))
            path = "/" + path;
        if (string.Equals(received, path, StringComparison.OrdinalIgnoreCase))
            return true;
        if (string.Equals(path, "/timecode", StringComparison.OrdinalIgnoreCase)
            && (string.Equals(received, "/tc", StringComparison.OrdinalIgnoreCase)
                || string.Equals(received, "/time/timecode", StringComparison.OrdinalIgnoreCase)))
            return true;
        return false;
    }

    private static bool TryParseOscTimecode(OscInputMessage msg, TimecodeFrameRate rate, out TimecodeValue value)
    {
        value = default;
        if (msg.Args != null && msg.Args.Count >= 1 && msg.Args[0] is string text
            && TimecodeValue.TryParse(text, rate, out value))
            return true;

        if (msg.Args != null && msg.Args.Count >= 4
            && TryToInt(msg.Args[0], out int h)
            && TryToInt(msg.Args[1], out int m)
            && TryToInt(msg.Args[2], out int s)
            && TryToInt(msg.Args[3], out int f))
        {
            value = new TimecodeValue(h, m, s, f, rate, isRunning: true, isLocked: true);
            return true;
        }

        return false;
    }

    private static bool TryToInt(object arg, out int value)
    {
        value = 0;
        switch (arg)
        {
            case int i:
                value = i;
                return true;
            case long l:
                value = (int)l;
                return true;
            case float fl:
                value = (int)Math.Round(fl);
                return true;
            case double d:
                value = (int)Math.Round(d);
                return true;
            case string s when int.TryParse(s, out int parsed):
                value = parsed;
                return true;
            default:
                return false;
        }
    }

    private void SendOscAndMtcOutputs()
    {
        if (!_timecodeEnabled)
            return;

        foreach (var endpoint in _outputs)
        {
            if (endpoint.Kind == TimecodeKind.Osc)
                SendOscTimecode(endpoint);
            else if (endpoint.Kind == TimecodeKind.MidiTimecode)
                SendMtcForEndpoint(endpoint);
        }
    }

    private void SendOscTimecode(TimecodeEndpoint endpoint)
    {
        var value = endpoint.CurrentValue;
        string display = value.ToDisplayString();
        if (!ShouldSendIo(endpoint.Id, display))
            return;

        string connectionName = endpoint.SourceName;
        CueOscConnection connection = null;
        var list = OscConnections.Connections;
        if (list != null)
        {
            if (!string.IsNullOrEmpty(connectionName))
            {
                foreach (var c in list)
                {
                    if (c != null && GodotObject.IsInstanceValid(c)
                        && string.Equals(c.Name, connectionName, StringComparison.OrdinalIgnoreCase))
                    {
                        connection = c;
                        break;
                    }
                }
            }

            if (connection == null && list.Count > 0)
                connection = list[0];
        }

        if (connection == null)
            return;

        string path = "/timecode";
        try
        {
            var message = new OscMessage(
                path,
                display,
                value.Hours,
                value.Minutes,
                value.Seconds,
                value.Frames,
                value.FrameRate.NominalFramesPerSecond());
            connection.SendMessage(message, monitor: false);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"TimecodeManager:SendOscTimecode - {ex.Message}");
        }
    }

    private bool ShouldSendIo(int endpointId, string displayKey)
    {
        ulong now = Time.GetTicksMsec();
        if (_lastIoSendKey.TryGetValue(endpointId, out string prev) && prev == displayKey)
        {
            if (_lastIoSendMsec.TryGetValue(endpointId, out ulong last) && now - last < 200)
                return false;
        }

        _lastIoSendKey[endpointId] = displayKey;
        _lastIoSendMsec[endpointId] = now;
        return true;
    }
}
