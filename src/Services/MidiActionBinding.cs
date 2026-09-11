// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System;
using Cue2.Domain.Cues;
using Godot;
using Godot.Collections;

namespace Cue2.Services;

/// <summary>
/// MIDI pattern bound to a project InputMap action (e.g. Go, SaveSession).
/// Default is unbound (no MIDI control).
/// </summary>
public sealed class MidiActionBinding
{
    /// <summary>True when a MIDI pattern is assigned.</summary>
    public bool HasBinding { get; set; }

    /// <summary>Message type to match.</summary>
    public MidiTriggerMessageType MessageType { get; set; } = MidiTriggerMessageType.NoteOn;

    /// <summary>Channel 1–16, or 0 for any.</summary>
    public int Channel { get; set; }

    /// <summary>Note / CC / program number (0–127).</summary>
    public int Data1 { get; set; }

    /// <summary>Velocity / CC value; used when <see cref="MatchValue"/> is true.</summary>
    public int Data2 { get; set; }

    /// <summary>When true, require exact Data2 match.</summary>
    public bool MatchValue { get; set; }

    /// <summary>Factory default: unbound.</summary>
    public static MidiActionBinding Unbound() => new();

    /// <summary>True when this differs from unbound default.</summary>
    public bool IsNonDefault => HasBinding;

    /// <summary>
    /// Readable summary (e.g. "NoteOn ch1 n60").
    /// </summary>
    public string GetDisplay()
    {
        if (!HasBinding) return string.Empty;

        string ch = Channel == 0 ? "ch*" : $"ch{Channel}";
        return MessageType switch
        {
            MidiTriggerMessageType.NoteOn =>
                MatchValue ? $"NoteOn {ch} n{Data1} v{Data2}" : $"NoteOn {ch} n{Data1}",
            MidiTriggerMessageType.NoteOff =>
                MatchValue ? $"NoteOff {ch} n{Data1} v{Data2}" : $"NoteOff {ch} n{Data1}",
            MidiTriggerMessageType.ControlChange =>
                MatchValue ? $"CC {ch} cc{Data1}={Data2}" : $"CC {ch} cc{Data1}",
            MidiTriggerMessageType.ProgramChange =>
                $"Program {ch} p{Data1}",
            _ => $"{MessageType} {ch} {Data1}"
        };
    }

    /// <summary>
    /// Returns true when <paramref name="msg"/> matches this binding pattern.
    /// </summary>
    public bool Matches(in MidiInputMessage msg)
    {
        if (!HasBinding || !msg.IsValid) return false;
        if (MessageType != msg.MessageType) return false;
        if (Channel != 0 && Channel != msg.Channel) return false;
        if (Data1 != msg.Data1) return false;
        if (MatchValue && MessageType != MidiTriggerMessageType.ProgramChange && Data2 != msg.Data2)
            return false;
        return true;
    }

    /// <summary>
    /// Sets the binding from a captured / edited MIDI message.
    /// </summary>
    public void SetFromMessage(MidiTriggerMessageType type, int channel, int data1, int data2, bool matchValue)
    {
        HasBinding = true;
        MessageType = type;
        Channel = Math.Clamp(channel, 0, 16);
        Data1 = Math.Clamp(data1, 0, 127);
        Data2 = Math.Clamp(data2, 0, 127);
        MatchValue = matchValue && type != MidiTriggerMessageType.ProgramChange;
    }

    /// <summary>Clears to unbound.</summary>
    public void Clear()
    {
        HasBinding = false;
        MessageType = MidiTriggerMessageType.NoteOn;
        Channel = 0;
        Data1 = 0;
        Data2 = 0;
        MatchValue = false;
    }

    /// <summary>Serializes for showfile / history.</summary>
    public Dictionary ToDict()
    {
        var d = new Dictionary();
        d["HasBinding"] = HasBinding;
        d["MessageType"] = (int)MessageType;
        d["Channel"] = Channel;
        d["Data1"] = Data1;
        d["Data2"] = Data2;
        d["MatchValue"] = MatchValue;
        return d;
    }

    /// <summary>Deserializes from showfile / history.</summary>
    public static MidiActionBinding FromDict(Dictionary data)
    {
        var b = new MidiActionBinding();
        if (data == null) return b;
        b.HasBinding = data.TryGetValue("HasBinding", out var h) && h.AsBool();
        b.MessageType = data.TryGetValue("MessageType", out var mt)
            ? (MidiTriggerMessageType)mt.AsInt32()
            : MidiTriggerMessageType.NoteOn;
        b.Channel = data.TryGetValue("Channel", out var ch) ? Math.Clamp(ch.AsInt32(), 0, 16) : 0;
        b.Data1 = data.TryGetValue("Data1", out var d1) ? Math.Clamp(d1.AsInt32(), 0, 127) : 0;
        b.Data2 = data.TryGetValue("Data2", out var d2) ? Math.Clamp(d2.AsInt32(), 0, 127) : 0;
        b.MatchValue = data.TryGetValue("MatchValue", out var mv) && mv.AsBool();
        if (!b.HasBinding)
            b.Clear();
        return b;
    }

    /// <summary>Deep copy.</summary>
    public MidiActionBinding Clone()
    {
        return new MidiActionBinding
        {
            HasBinding = HasBinding,
            MessageType = MessageType,
            Channel = Channel,
            Data1 = Data1,
            Data2 = Data2,
            MatchValue = MatchValue
        };
    }
}
