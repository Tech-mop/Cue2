// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System;
using System.Threading.Tasks;
using Cue2.Services;
using Godot;
using Godot.Collections;

namespace Cue2.Domain.Cues;

/// <summary>
/// Cue component that sends a MIDI channel message to a session MIDI output device when the cue fires.
/// </summary>
public class MidiOutputComponent : ICueComponent
{
    /// <inheritdoc />
    public string Type => "MidiOutput";

    /// <summary>Session output device name (must be in <see cref="MidiManager.SessionOutputNames"/>).</summary>
    public string OutputDeviceName { get; set; } = string.Empty;

    /// <summary>Message type to send.</summary>
    public MidiTriggerMessageType MessageType { get; set; } = MidiTriggerMessageType.NoteOn;

    /// <summary>MIDI channel 1–16.</summary>
    public int Channel { get; set; } = 1;

    /// <summary>Note / CC / program number (0–127).</summary>
    public int Data1 { get; set; }

    /// <summary>Velocity / CC value (0–127). Ignored for Program Change.</summary>
    public int Data2 { get; set; } = 100;

    /// <summary>
    /// When &gt; 0 and message is Note On, schedules a Note Off after this many seconds.
    /// </summary>
    public double NoteDurationSeconds { get; set; }

    /// <summary>
    /// Short summary for active-cue bar / logs.
    /// </summary>
    public string GetDisplaySummary()
    {
        string ch = $"ch{Math.Clamp(Channel, 1, 16)}";
        string core = MessageType switch
        {
            MidiTriggerMessageType.NoteOn =>
                NoteDurationSeconds > 1e-6
                    ? $"NoteOn {ch} n{Data1} v{Data2} ({NoteDurationSeconds:0.###}s)"
                    : $"NoteOn {ch} n{Data1} v{Data2}",
            MidiTriggerMessageType.NoteOff => $"NoteOff {ch} n{Data1}",
            MidiTriggerMessageType.ControlChange => $"CC {ch} cc{Data1}={Data2}",
            MidiTriggerMessageType.ProgramChange => $"Program {ch} p{Data1}",
            _ => $"{MessageType} {ch} {Data1}"
        };
        return string.IsNullOrEmpty(OutputDeviceName) ? core : $"{OutputDeviceName}: {core}";
    }

    /// <summary>
    /// Sends the configured MIDI message via <see cref="MidiManager"/>.
    /// </summary>
    public async Task Execute()
    {
        try
        {
            if (Engine.GetMainLoop() is not SceneTree tree)
                return;

            var midi = tree.Root.GetNodeOrNull<MidiManager>("/root/MidiManager");
            if (midi == null)
            {
                GD.PrintErr("MidiOutputComponent:Execute - MidiManager not found");
                return;
            }

            if (string.IsNullOrWhiteSpace(OutputDeviceName))
            {
                GD.Print("MidiOutputComponent:Execute - No output device set");
                return;
            }

            midi.SendMessage(
                OutputDeviceName,
                MessageType,
                Channel,
                Data1,
                Data2,
                MessageType == MidiTriggerMessageType.NoteOn ? NoteDurationSeconds : 0);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"MidiOutputComponent:Execute - {ex.Message}");
        }

        // Brief yield so active-bar UI can show the component row.
        await Task.Delay(1);
    }

    /// <inheritdoc />
    public Dictionary GetData()
    {
        return new Dictionary
        {
            { "OutputDeviceName", OutputDeviceName ?? string.Empty },
            { "MessageType", (int)MessageType },
            { "Channel", Channel },
            { "Data1", Data1 },
            { "Data2", Data2 },
            { "NoteDurationSeconds", NoteDurationSeconds },
        };
    }

    /// <inheritdoc />
    public void LoadFromData(Dictionary data)
    {
        if (data == null) return;
        OutputDeviceName = data.TryGetValue("OutputDeviceName", out var v) ? v.AsString() : OutputDeviceName;
        MessageType = data.TryGetValue("MessageType", out v)
            ? (MidiTriggerMessageType)v.AsInt32()
            : MessageType;
        Channel = data.TryGetValue("Channel", out v) ? Math.Clamp(v.AsInt32(), 1, 16) : Channel;
        Data1 = data.TryGetValue("Data1", out v) ? Math.Clamp(v.AsInt32(), 0, 127) : Data1;
        Data2 = data.TryGetValue("Data2", out v) ? Math.Clamp(v.AsInt32(), 0, 127) : Data2;
        NoteDurationSeconds = data.TryGetValue("NoteDurationSeconds", out v)
            ? Math.Max(0, v.AsDouble())
            : NoteDurationSeconds;
    }
}
