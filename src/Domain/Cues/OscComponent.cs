// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System;
using System.Threading.Tasks;
using Cue2.Domain.Connections;
using Godot;
using Godot.Collections;
using Rug.Osc;

namespace Cue2.Domain.Cues;

/// <summary>
/// Cue component that sends an OSC message on a named <see cref="CueOscConnection"/>.
/// Supports multi-typed arguments via <see cref="ArgsText"/>.
/// </summary>
public class OscComponent : ICueComponent
{
    public string Type => "OscComponent";
    public int OscConnectionId;
    /// <summary>OSC address path (must start with /).</summary>
    public string OscMessage = "/";
    /// <summary>
    /// Optional arguments text (e.g. <c>1 0.5 "hello" true</c>). Parsed at send time.
    /// </summary>
    public string ArgsText = string.Empty;
    public CueOscConnection OscConnection;

    public async Task Execute()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(OscMessage))
            {
                GD.Print("OscComponent:Execute - Invalid OSC message path (empty)");
                return;
            }
            if (OscConnection == null)
            {
                GD.PrintErr("OscComponent:Execute - OscConnection is null");
                return;
            }

            // Accept QLab-style combined lines ("/jump 2") via SplitPathAndArgs inside BuildMessage.
            if (!OscMessageUtil.SplitPathAndArgs(OscMessage, ArgsText, out string path, out string args)
                || string.IsNullOrEmpty(path) || path == "/")
            {
                GD.Print($"OscComponent:Execute - Invalid OSC message path: '{OscMessage}'");
                return;
            }

            OscMessage oscMes = string.IsNullOrWhiteSpace(args)
                ? new OscMessage(path)
                : OscMessageUtil.BuildMessage(path, args);

            OscConnection.SendMessage(oscMes);
        }
        catch (Exception ex)
        {
            GD.Print($"OscComponent:Execute - Failed: {ex.Message}");
        }
        await Task.Delay(1);
    }

    public Dictionary GetData()
    {
        return new Dictionary()
        {
            { "Command", OscMessage ?? string.Empty },
            { "ArgsText", ArgsText ?? string.Empty },
            { "OscConnectionId", OscConnectionId },
        };
    }

    public void LoadFromData(Dictionary data)
    {
        OscMessage = data.TryGetValue("Command", out var value) ? (string)value : OscMessage;
        ArgsText = data.TryGetValue("ArgsText", out value) ? (string)value : (ArgsText ?? string.Empty);
        OscConnectionId = data.TryGetValue("OscConnectionId", out value) ? (int)value : OscConnectionId;
    }
}
