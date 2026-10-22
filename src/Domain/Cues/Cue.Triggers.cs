// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System;
using Godot;
using System.Collections.Generic;
using System.Linq;
using Cue2.Domain.Cuelist;
using Cue2.Domain.Playback;
using Cue2.Domain.Devices;
using Cue2.Domain.ShowSettings;
using Cue2.Domain.Metadata;
using Cue2.Domain.Cues;
using Cue2.Domain.Connections;
using Cue2.Domain.Library;
using Cue2.Domain.Commands;
using Cue2.Domain.Timecode;
using Cue2.Services;
using Godot.Collections;
using Array = Godot.Collections.Array;

namespace Cue2.Domain.Cues;

/// <summary>
/// Partial: hotkey, clock, MIDI, OSC and timecode trigger settings and their save data.
/// </summary>
public partial class Cue
{
    /// <summary>
    /// Sets the cue hotkey from a key event (key + modifiers). Does not change <see cref="HotkeyEnabled"/>.
    /// </summary>
    /// <param name="source">Pressed key event to capture.</param>
    public void SetHotkey(InputEventKey source)
    {
        if (source == null)
        {
            ClearHotkey();
            return;
        }

        HotkeyKeycode = source.Keycode;
        HotkeyPhysicalKeycode = source.PhysicalKeycode;
        HotkeyCtrl = source.CtrlPressed;
        HotkeyShift = source.ShiftPressed;
        HotkeyAlt = source.AltPressed;
        HotkeyMeta = source.MetaPressed;
        HotkeyChanged?.Invoke();
    }

    /// <summary>
    /// Clears the hotkey binding back to default (no key). Does not change <see cref="HotkeyEnabled"/>.
    /// </summary>
    public void ClearHotkey()
    {
        if (!HasHotkey) return;
        HotkeyKeycode = Key.None;
        HotkeyPhysicalKeycode = Key.None;
        HotkeyCtrl = false;
        HotkeyShift = false;
        HotkeyAlt = false;
        HotkeyMeta = false;
        HotkeyChanged?.Invoke();
    }

    /// <summary>
    /// Resets hotkey to factory default: unbound and disabled.
    /// </summary>
    public void ResetHotkeyToDefault()
    {
        bool changed = HasHotkey || _hotkeyEnabled;
        HotkeyKeycode = Key.None;
        HotkeyPhysicalKeycode = Key.None;
        HotkeyCtrl = false;
        HotkeyShift = false;
        HotkeyAlt = false;
        HotkeyMeta = false;
        _hotkeyEnabled = false;
        if (changed)
            HotkeyChanged?.Invoke();
    }

    /// <summary>
    /// Returns true if <paramref name="keyEvent"/> matches this cue's bound hotkey (key + modifiers).
    /// </summary>
    /// <param name="keyEvent">Key event to test.</param>
    /// <returns><c>true</c> when bound and matching.</returns>
    public bool HotkeyMatches(InputEventKey keyEvent)
    {
        if (!HasHotkey || keyEvent == null) return false;

        // Build a synthetic event from stored fields for comparison.
        var stored = new InputEventKey
        {
            Keycode = HotkeyKeycode,
            PhysicalKeycode = HotkeyPhysicalKeycode,
            CtrlPressed = HotkeyCtrl,
            ShiftPressed = HotkeyShift,
            AltPressed = HotkeyAlt,
            MetaPressed = HotkeyMeta,
        };
        return GlobalData.KeyEventsMatch(stored, keyEvent);
    }

    /// <summary>
    /// Readable hotkey string (e.g. "Ctrl+G"), or empty when unbound.
    /// </summary>
    public string GetHotkeyDisplay()
    {
        if (!HasHotkey) return string.Empty;
        var ev = new InputEventKey
        {
            Keycode = HotkeyKeycode != Key.None ? HotkeyKeycode : HotkeyPhysicalKeycode,
            PhysicalKeycode = HotkeyPhysicalKeycode,
            CtrlPressed = HotkeyCtrl,
            ShiftPressed = HotkeyShift,
            AltPressed = HotkeyAlt,
            MetaPressed = HotkeyMeta,
        };
        return GlobalData.FormatInputEvent(ev);
    }

    /// <summary>
    /// Writes hotkey fields into a serialization dictionary.
    /// </summary>
    private void WriteHotkeyToData(Dictionary dict)
    {
        dict["HotkeyEnabled"] = HotkeyEnabled;
        dict["HotkeyKeycode"] = (int)HotkeyKeycode;
        dict["HotkeyPhysicalKeycode"] = (int)HotkeyPhysicalKeycode;
        dict["HotkeyCtrl"] = HotkeyCtrl;
        dict["HotkeyShift"] = HotkeyShift;
        dict["HotkeyAlt"] = HotkeyAlt;
        dict["HotkeyMeta"] = HotkeyMeta;
    }

    /// <summary>
    /// Loads hotkey fields from saved data (constructor path; no events).
    /// </summary>
    private void LoadHotkeyFromData(Dictionary data)
    {
        _hotkeyEnabled = data.TryGetValue("HotkeyEnabled", out var en) && en.AsBool();
        HotkeyKeycode = data.TryGetValue("HotkeyKeycode", out var kc)
            ? (Key)kc.AsInt32()
            : Key.None;
        HotkeyPhysicalKeycode = data.TryGetValue("HotkeyPhysicalKeycode", out var pk)
            ? (Key)pk.AsInt32()
            : Key.None;
        HotkeyCtrl = data.TryGetValue("HotkeyCtrl", out var c) && c.AsBool();
        HotkeyShift = data.TryGetValue("HotkeyShift", out var s) && s.AsBool();
        HotkeyAlt = data.TryGetValue("HotkeyAlt", out var a) && a.AsBool();
        HotkeyMeta = data.TryGetValue("HotkeyMeta", out var m) && m.AsBool();
    }

    /// <summary>
    /// Applies hotkey fields from history/undo data and notifies listeners when changed.
    /// </summary>
    private void ApplyHotkeyFromData(Dictionary data)
    {
        bool prevEnabled = _hotkeyEnabled;
        Key prevKey = HotkeyKeycode;
        Key prevPhys = HotkeyPhysicalKeycode;
        bool prevCtrl = HotkeyCtrl;
        bool prevShift = HotkeyShift;
        bool prevAlt = HotkeyAlt;
        bool prevMeta = HotkeyMeta;

        LoadHotkeyFromData(data);

        bool changed = prevEnabled != _hotkeyEnabled ||
                       prevKey != HotkeyKeycode ||
                       prevPhys != HotkeyPhysicalKeycode ||
                       prevCtrl != HotkeyCtrl ||
                       prevShift != HotkeyShift ||
                       prevAlt != HotkeyAlt ||
                       prevMeta != HotkeyMeta;
        if (changed)
            HotkeyChanged?.Invoke();
    }

    // ── Clock trigger helpers ───────────────────────────────────────────────

    /// <summary>
    /// Sets the wall-clock target time of day. Does not change <see cref="ClockEnabled"/>.
    /// </summary>
    /// <param name="timeOfDay">Local time of day (fractional day components beyond 24h are normalized via <see cref="TimeSpan"/>).</param>
    public void SetClockTime(TimeSpan timeOfDay)
    {
        // Normalize into [0, 24h).
        double total = timeOfDay.TotalSeconds % 86400.0;
        if (total < 0) total += 86400.0;
        var normalized = TimeSpan.FromSeconds(total);

        if (HasClockTime && ClockTimeOfDay == normalized) return;

        HasClockTime = true;
        ClockTimeOfDay = normalized;
        ClockChanged?.Invoke();
    }

    /// <summary>
    /// Clears the clock time back to unset. Does not change <see cref="ClockEnabled"/>.
    /// </summary>
    public void ClearClockTime()
    {
        if (!HasClockTime) return;
        HasClockTime = false;
        ClockTimeOfDay = TimeSpan.Zero;
        ClockChanged?.Invoke();
    }

    /// <summary>
    /// Resets clock trigger to factory default: no time, disabled, every day.
    /// </summary>
    public void ResetClockToDefault()
    {
        bool changed = HasClockTime || _clockEnabled || ClockDaysMask != ClockDaysAll;
        HasClockTime = false;
        ClockTimeOfDay = TimeSpan.Zero;
        ClockDaysMask = ClockDaysAll;
        _clockEnabled = false;
        if (changed)
            ClockChanged?.Invoke();
    }

    /// <summary>
    /// Returns whether the clock trigger is enabled on the given weekday.
    /// </summary>
    /// <param name="day">Weekday to query.</param>
    public bool IsClockDayEnabled(DayOfWeek day)
    {
        int bit = (int)day;
        if (bit < 0 || bit > 6) return false;
        return (ClockDaysMask & (1 << bit)) != 0;
    }

    /// <summary>
    /// Enables or disables the clock trigger for a single weekday.
    /// </summary>
    /// <param name="day">Weekday to change.</param>
    /// <param name="enabled">Whether the cue may fire on that day.</param>
    public void SetClockDayEnabled(DayOfWeek day, bool enabled)
    {
        int bit = (int)day;
        if (bit < 0 || bit > 6) return;

        byte next = enabled
            ? (byte)(ClockDaysMask | (1 << bit))
            : (byte)(ClockDaysMask & ~(1 << bit));
        if (next == ClockDaysMask) return;

        ClockDaysMask = next;
        ClockChanged?.Invoke();
    }

    /// <summary>
    /// Replaces the full weekday mask for the clock trigger.
    /// </summary>
    /// <param name="mask">Bitmask of enabled days (bits 0–6; see <see cref="ClockDaysAll"/>).</param>
    public void SetClockDaysMask(byte mask)
    {
        byte next = (byte)(mask & ClockDaysAll);
        if (next == ClockDaysMask) return;
        ClockDaysMask = next;
        ClockChanged?.Invoke();
    }

    /// <summary>
    /// Readable 24h clock string (e.g. "14:30:00"), or empty when unset.
    /// </summary>
    public string GetClockDisplay()
    {
        if (!HasClockTime) return string.Empty;
        return FormatClockTimeOfDay(ClockTimeOfDay);
    }

    /// <summary>
    /// Formats a time-of-day as <c>HH:mm:ss</c>.
    /// </summary>
    public static string FormatClockTimeOfDay(TimeSpan timeOfDay)
    {
        int hours = (int)timeOfDay.TotalHours;
        if (hours < 0) hours = 0;
        if (hours > 23) hours %= 24;
        return $"{hours:D2}:{timeOfDay.Minutes:D2}:{timeOfDay.Seconds:D2}";
    }

    /// <summary>
    /// Returns true if local wall time crossed this cue's clock target between
    /// <paramref name="previous"/> (exclusive) and <paramref name="current"/> (inclusive)
    /// on an enabled weekday.
    /// Used so the cue fires once per selected day at the moment of reaching the time, not retroactively.
    /// </summary>
    /// <param name="previous">Previous sample of local time.</param>
    /// <param name="current">Current sample of local time.</param>
    public bool ClockCrossedBetween(DateTime previous, DateTime current)
    {
        if (!HasClockTime) return false;
        if (ClockDaysMask == 0) return false;
        if (current <= previous) return false;

        // Next candidate at the target time of day after previous.
        var candidate = previous.Date + ClockTimeOfDay;
        if (candidate <= previous)
            candidate = candidate.AddDays(1);

        // Walk forward (handles multi-day frame skips / disabled weekdays).
        for (int i = 0; i < 8 && candidate <= current; i++)
        {
            if (candidate > previous && IsClockDayEnabled(candidate.DayOfWeek))
                return true;
            candidate = candidate.AddDays(1);
        }

        return false;
    }

    /// <summary>
    /// Writes clock trigger fields into a serialization dictionary.
    /// </summary>
    private void WriteClockToData(Dictionary dict)
    {
        dict["ClockEnabled"] = ClockEnabled;
        dict["HasClockTime"] = HasClockTime;
        // Total seconds from midnight (double for future sub-second if needed).
        dict["ClockSecondsOfDay"] = HasClockTime ? ClockTimeOfDay.TotalSeconds : 0.0;
        dict["ClockDaysMask"] = (int)ClockDaysMask;
    }

    /// <summary>
    /// Loads clock fields from saved data (constructor path; no events).
    /// </summary>
    private void LoadClockFromData(Dictionary data)
    {
        _clockEnabled = data.TryGetValue("ClockEnabled", out var en) && en.AsBool();
        HasClockTime = data.TryGetValue("HasClockTime", out var has) && has.AsBool();
        if (HasClockTime && data.TryGetValue("ClockSecondsOfDay", out var secs))
        {
            double total = secs.AsDouble();
            if (total < 0) total = 0;
            if (total >= 86400.0) total %= 86400.0;
            ClockTimeOfDay = TimeSpan.FromSeconds(total);
        }
        else
        {
            HasClockTime = false;
            ClockTimeOfDay = TimeSpan.Zero;
        }

        // Legacy saves without the key default to every day.
        if (data.TryGetValue("ClockDaysMask", out var days))
            ClockDaysMask = (byte)(days.AsInt32() & ClockDaysAll);
        else
            ClockDaysMask = ClockDaysAll;
    }

    /// <summary>
    /// Applies clock fields from history/undo data and notifies listeners when changed.
    /// </summary>
    private void ApplyClockFromData(Dictionary data)
    {
        bool prevEnabled = _clockEnabled;
        bool prevHas = HasClockTime;
        TimeSpan prevTime = ClockTimeOfDay;
        byte prevDays = ClockDaysMask;

        LoadClockFromData(data);

        bool changed = prevEnabled != _clockEnabled ||
                       prevHas != HasClockTime ||
                       prevTime != ClockTimeOfDay ||
                       prevDays != ClockDaysMask;
        if (changed)
            ClockChanged?.Invoke();
    }

    // ── MIDI trigger helpers ────────────────────────────────────────────────

    /// <summary>
    /// Sets the MIDI trigger pattern. Does not change <see cref="MidiTriggerEnabled"/>.
    /// </summary>
    /// <param name="type">Message type to match.</param>
    /// <param name="channel">Channel 1–16, or 0 for any.</param>
    /// <param name="data1">Note / CC / program number (0–127).</param>
    /// <param name="data2">Velocity / value (0–127); used only when <paramref name="matchValue"/> is true.</param>
    /// <param name="matchValue">When true, require exact <paramref name="data2"/>.</param>
    /// <param name="deviceFilter">Optional device name filter; empty = any device.</param>
    public void SetMidiTrigger(
        MidiTriggerMessageType type,
        int channel,
        int data1,
        int data2 = 0,
        bool matchValue = false,
        string deviceFilter = null)
    {
        channel = Math.Clamp(channel, 0, 16);
        data1 = Math.Clamp(data1, 0, 127);
        data2 = Math.Clamp(data2, 0, 127);
        deviceFilter ??= string.Empty;

        if (HasMidiTrigger &&
            MidiMessageType == type &&
            MidiChannel == channel &&
            MidiData1 == data1 &&
            MidiData2 == data2 &&
            MidiMatchValue == matchValue &&
            string.Equals(MidiDeviceFilter, deviceFilter, StringComparison.Ordinal))
        {
            return;
        }

        HasMidiTrigger = true;
        MidiMessageType = type;
        MidiChannel = channel;
        MidiData1 = data1;
        MidiData2 = data2;
        MidiMatchValue = matchValue;
        MidiDeviceFilter = deviceFilter;
        MidiTriggerChanged?.Invoke();
    }

    /// <summary>
    /// Clears the MIDI trigger pattern. Does not change <see cref="MidiTriggerEnabled"/>.
    /// </summary>
    public void ClearMidiTrigger()
    {
        if (!HasMidiTrigger) return;
        HasMidiTrigger = false;
        MidiMessageType = MidiTriggerMessageType.NoteOn;
        MidiChannel = 0;
        MidiData1 = 0;
        MidiData2 = 0;
        MidiMatchValue = false;
        MidiDeviceFilter = string.Empty;
        MidiTriggerChanged?.Invoke();
    }

    /// <summary>
    /// Resets MIDI trigger to factory default: no pattern and disabled.
    /// </summary>
    public void ResetMidiTriggerToDefault()
    {
        bool changed = HasMidiTrigger || _midiTriggerEnabled;
        HasMidiTrigger = false;
        MidiMessageType = MidiTriggerMessageType.NoteOn;
        MidiChannel = 0;
        MidiData1 = 0;
        MidiData2 = 0;
        MidiMatchValue = false;
        MidiDeviceFilter = string.Empty;
        _midiTriggerEnabled = false;
        if (changed)
            MidiTriggerChanged?.Invoke();
    }

    /// <summary>
    /// Readable summary (e.g. "NoteOn ch1 n60" or "CC ch* cc7=64").
    /// </summary>
    public string GetMidiTriggerDisplay()
    {
        if (!HasMidiTrigger) return string.Empty;

        string ch = MidiChannel == 0 ? "ch*" : $"ch{MidiChannel}";
        string typeLabel = MidiMessageType switch
        {
            MidiTriggerMessageType.NoteOn => "NoteOn",
            MidiTriggerMessageType.NoteOff => "NoteOff",
            MidiTriggerMessageType.ControlChange => "CC",
            MidiTriggerMessageType.ProgramChange => "Program",
            _ => MidiMessageType.ToString()
        };

        string core = MidiMessageType switch
        {
            MidiTriggerMessageType.NoteOn or MidiTriggerMessageType.NoteOff =>
                MidiMatchValue ? $"{typeLabel} {ch} n{MidiData1} v{MidiData2}" : $"{typeLabel} {ch} n{MidiData1}",
            MidiTriggerMessageType.ControlChange =>
                MidiMatchValue ? $"{typeLabel} {ch} cc{MidiData1}={MidiData2}" : $"{typeLabel} {ch} cc{MidiData1}",
            MidiTriggerMessageType.ProgramChange =>
                $"{typeLabel} {ch} p{MidiData1}",
            _ => $"{typeLabel} {ch} {MidiData1}"
        };

        if (!string.IsNullOrEmpty(MidiDeviceFilter))
            core += $" @{MidiDeviceFilter}";
        return core;
    }

    /// <summary>
    /// Returns true when <paramref name="type"/>/<paramref name="channel"/>/<paramref name="data1"/>/<paramref name="data2"/>
    /// from <paramref name="deviceName"/> matches this cue's MIDI trigger pattern.
    /// </summary>
    public bool MidiTriggerMatches(
        MidiTriggerMessageType type,
        int channel,
        int data1,
        int data2,
        string deviceName = null)
    {
        if (!HasMidiTrigger) return false;

        if (MidiMessageType != type) return false;

        // Channel: 0 = any; stored/UI is 1–16, incoming channel should also be 1–16.
        if (MidiChannel != 0 && MidiChannel != channel) return false;

        if (MidiData1 != data1) return false;

        if (MidiMatchValue && MidiMessageType != MidiTriggerMessageType.ProgramChange)
        {
            if (MidiData2 != data2) return false;
        }

        if (!string.IsNullOrEmpty(MidiDeviceFilter))
        {
            if (string.IsNullOrEmpty(deviceName) ||
                !string.Equals(MidiDeviceFilter, deviceName, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    private void WriteMidiTriggerToData(Dictionary dict)
    {
        dict["MidiTriggerEnabled"] = MidiTriggerEnabled;
        dict["HasMidiTrigger"] = HasMidiTrigger;
        dict["MidiMessageType"] = (int)MidiMessageType;
        dict["MidiChannel"] = MidiChannel;
        dict["MidiData1"] = MidiData1;
        dict["MidiData2"] = MidiData2;
        dict["MidiMatchValue"] = MidiMatchValue;
        dict["MidiDeviceFilter"] = MidiDeviceFilter ?? string.Empty;
    }

    private void WriteOscTriggerToData(Dictionary dict)
    {
        dict["OscTriggerEnabled"] = OscTriggerEnabled;
        dict["OscTriggerAddress"] = OscTriggerAddress ?? string.Empty;
    }

    private void LoadOscTriggerFromData(Dictionary data)
    {
        if (data == null) return;
        _oscTriggerEnabled = data.TryGetValue("OscTriggerEnabled", out var en) && en.AsBool();
        _oscTriggerAddress = data.TryGetValue("OscTriggerAddress", out var addr)
            ? addr.AsString() ?? string.Empty
            : string.Empty;
    }

    private void ApplyOscTriggerFromData(Dictionary data)
    {
        bool prevEn = _oscTriggerEnabled;
        string prevAddr = _oscTriggerAddress;
        LoadOscTriggerFromData(data);
        if (prevEn != _oscTriggerEnabled
            || !string.Equals(prevAddr, _oscTriggerAddress, StringComparison.Ordinal))
            OscTriggerChanged?.Invoke();
    }

    // ── Timecode trigger helpers ────────────────────────────────────────────

    /// <summary>
    /// Sets the watched clock. Does not change <see cref="TimecodeTriggerEnabled"/>.
    /// </summary>
    /// <param name="endpointId">Session input or output id, or <c>-1</c> to clear.</param>
    public void SetTimecodeTriggerEndpoint(int endpointId)
    {
        int next = endpointId < 0 ? -1 : endpointId;
        if (TimecodeTriggerEndpointId == next) return;
        TimecodeTriggerEndpointId = next;
        TimecodeTriggerChanged?.Invoke();
    }

    /// <summary>
    /// Sets the trigger time. Does not change <see cref="TimecodeTriggerEnabled"/>.
    /// </summary>
    /// <param name="hours">Hours 0–23.</param>
    /// <param name="minutes">Minutes 0–59.</param>
    /// <param name="seconds">Seconds 0–59.</param>
    /// <param name="frames">Frames (clamped 0–29).</param>
    public void SetTimecodeTriggerTime(int hours, int minutes, int seconds, int frames)
    {
        hours = ((hours % 24) + 24) % 24;
        minutes = Math.Clamp(minutes, 0, 59);
        seconds = Math.Clamp(seconds, 0, 59);
        frames = Math.Clamp(frames, 0, 29);

        if (HasTimecodeTrigger
            && TimecodeTriggerHours == hours
            && TimecodeTriggerMinutes == minutes
            && TimecodeTriggerSeconds == seconds
            && TimecodeTriggerFrames == frames)
            return;

        HasTimecodeTrigger = true;
        TimecodeTriggerHours = hours;
        TimecodeTriggerMinutes = minutes;
        TimecodeTriggerSeconds = seconds;
        TimecodeTriggerFrames = frames;
        TimecodeTriggerChanged?.Invoke();
    }

    /// <summary>
    /// Clears the trigger time. Does not change enable or the selected clock.
    /// </summary>
    public void ClearTimecodeTriggerTime()
    {
        if (!HasTimecodeTrigger) return;
        HasTimecodeTrigger = false;
        TimecodeTriggerHours = 0;
        TimecodeTriggerMinutes = 0;
        TimecodeTriggerSeconds = 0;
        TimecodeTriggerFrames = 0;
        TimecodeTriggerChanged?.Invoke();
    }

    /// <summary>
    /// Resets the timecode trigger to factory default: disabled, no clock, no time.
    /// </summary>
    public void ResetTimecodeTriggerToDefault()
    {
        bool changed = IsTimecodeTriggerNonDefault;
        _timecodeTriggerEnabled = false;
        HasTimecodeTrigger = false;
        TimecodeTriggerEndpointId = -1;
        TimecodeTriggerHours = 0;
        TimecodeTriggerMinutes = 0;
        TimecodeTriggerSeconds = 0;
        TimecodeTriggerFrames = 0;
        if (changed)
            TimecodeTriggerChanged?.Invoke();
    }

    /// <summary>
    /// Readable <c>HH:MM:SS:FF</c>, or empty when unset.
    /// </summary>
    public string GetTimecodeTriggerDisplay()
    {
        if (!HasTimecodeTrigger) return string.Empty;
        return $"{TimecodeTriggerHours:00}:{TimecodeTriggerMinutes:00}:{TimecodeTriggerSeconds:00}:{TimecodeTriggerFrames:00}";
    }

    /// <summary>
    /// Target elapsed seconds of this trigger at <paramref name="frameRate"/>.
    /// </summary>
    /// <param name="frameRate">Rate of the watched clock.</param>
    /// <returns>Elapsed seconds, or <c>-1</c> when unset.</returns>
    public double GetTimecodeTriggerElapsedSeconds(TimecodeFrameRate frameRate)
    {
        if (!HasTimecodeTrigger) return -1;
        return new TimecodeValue(
            TimecodeTriggerHours,
            TimecodeTriggerMinutes,
            TimecodeTriggerSeconds,
            TimecodeTriggerFrames,
            frameRate).ToElapsedSeconds();
    }

    private void WriteTimecodeTriggerToData(Dictionary dict)
    {
        dict["TimecodeTriggerEnabled"] = TimecodeTriggerEnabled;
        dict["HasTimecodeTrigger"] = HasTimecodeTrigger;
        dict["TimecodeTriggerEndpointId"] = TimecodeTriggerEndpointId;
        dict["TimecodeTriggerHours"] = TimecodeTriggerHours;
        dict["TimecodeTriggerMinutes"] = TimecodeTriggerMinutes;
        dict["TimecodeTriggerSeconds"] = TimecodeTriggerSeconds;
        dict["TimecodeTriggerFrames"] = TimecodeTriggerFrames;
    }

    private void LoadTimecodeTriggerFromData(Dictionary data)
    {
        if (data == null) return;
        _timecodeTriggerEnabled = data.TryGetValue("TimecodeTriggerEnabled", out var en) && en.AsBool();
        HasTimecodeTrigger = data.TryGetValue("HasTimecodeTrigger", out var has) && has.AsBool();
        TimecodeTriggerEndpointId = data.TryGetValue("TimecodeTriggerEndpointId", out var id)
            ? id.AsInt32()
            : -1;
        if (TimecodeTriggerEndpointId < 0)
            TimecodeTriggerEndpointId = -1;
        TimecodeTriggerHours = data.TryGetValue("TimecodeTriggerHours", out var h)
            ? Math.Clamp(h.AsInt32(), 0, 23)
            : 0;
        TimecodeTriggerMinutes = data.TryGetValue("TimecodeTriggerMinutes", out var m)
            ? Math.Clamp(m.AsInt32(), 0, 59)
            : 0;
        TimecodeTriggerSeconds = data.TryGetValue("TimecodeTriggerSeconds", out var s)
            ? Math.Clamp(s.AsInt32(), 0, 59)
            : 0;
        TimecodeTriggerFrames = data.TryGetValue("TimecodeTriggerFrames", out var f)
            ? Math.Clamp(f.AsInt32(), 0, 29)
            : 0;
        if (!HasTimecodeTrigger)
        {
            TimecodeTriggerHours = 0;
            TimecodeTriggerMinutes = 0;
            TimecodeTriggerSeconds = 0;
            TimecodeTriggerFrames = 0;
        }
    }

    private void ApplyTimecodeTriggerFromData(Dictionary data)
    {
        bool prevEn = _timecodeTriggerEnabled;
        bool prevHas = HasTimecodeTrigger;
        int prevId = TimecodeTriggerEndpointId;
        int prevH = TimecodeTriggerHours;
        int prevM = TimecodeTriggerMinutes;
        int prevS = TimecodeTriggerSeconds;
        int prevF = TimecodeTriggerFrames;

        LoadTimecodeTriggerFromData(data);

        bool changed = prevEn != _timecodeTriggerEnabled
                       || prevHas != HasTimecodeTrigger
                       || prevId != TimecodeTriggerEndpointId
                       || prevH != TimecodeTriggerHours
                       || prevM != TimecodeTriggerMinutes
                       || prevS != TimecodeTriggerSeconds
                       || prevF != TimecodeTriggerFrames;
        if (changed)
            TimecodeTriggerChanged?.Invoke();
    }

    private void LoadMidiTriggerFromData(Dictionary data)
    {
        _midiTriggerEnabled = data.TryGetValue("MidiTriggerEnabled", out var en) && en.AsBool();
        HasMidiTrigger = data.TryGetValue("HasMidiTrigger", out var has) && has.AsBool();
        MidiMessageType = data.TryGetValue("MidiMessageType", out var mt)
            ? (MidiTriggerMessageType)mt.AsInt32()
            : MidiTriggerMessageType.NoteOn;
        MidiChannel = data.TryGetValue("MidiChannel", out var ch) ? Math.Clamp(ch.AsInt32(), 0, 16) : 0;
        MidiData1 = data.TryGetValue("MidiData1", out var d1) ? Math.Clamp(d1.AsInt32(), 0, 127) : 0;
        MidiData2 = data.TryGetValue("MidiData2", out var d2) ? Math.Clamp(d2.AsInt32(), 0, 127) : 0;
        MidiMatchValue = data.TryGetValue("MidiMatchValue", out var mv) && mv.AsBool();
        MidiDeviceFilter = data.TryGetValue("MidiDeviceFilter", out var df) ? df.AsString() : string.Empty;
        if (!HasMidiTrigger)
        {
            MidiMessageType = MidiTriggerMessageType.NoteOn;
            MidiChannel = 0;
            MidiData1 = 0;
            MidiData2 = 0;
            MidiMatchValue = false;
            MidiDeviceFilter = string.Empty;
        }
    }

    private void ApplyMidiTriggerFromData(Dictionary data)
    {
        bool prevEnabled = _midiTriggerEnabled;
        bool prevHas = HasMidiTrigger;
        var prevType = MidiMessageType;
        int prevCh = MidiChannel;
        int prevD1 = MidiData1;
        int prevD2 = MidiData2;
        bool prevMatch = MidiMatchValue;
        string prevDev = MidiDeviceFilter;

        LoadMidiTriggerFromData(data);

        bool changed = prevEnabled != _midiTriggerEnabled ||
                       prevHas != HasMidiTrigger ||
                       prevType != MidiMessageType ||
                       prevCh != MidiChannel ||
                       prevD1 != MidiData1 ||
                       prevD2 != MidiData2 ||
                       prevMatch != MidiMatchValue ||
                       !string.Equals(prevDev, MidiDeviceFilter, StringComparison.Ordinal);
        if (changed)
            MidiTriggerChanged?.Invoke();
    }
}
