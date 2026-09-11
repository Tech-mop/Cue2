// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System;
using Godot;
using Godot.Collections;

namespace Cue2.Services;

/// <summary>
/// OSC pattern bound to a project InputMap action (e.g. Go, SaveSession).
/// Factory defaults for common playback actions are <c>/Go</c>, <c>/StopAll</c>, etc.
/// </summary>
public sealed class OscActionBinding
{
    /// <summary>True when an OSC address pattern is assigned.</summary>
    public bool HasBinding { get; set; }

    /// <summary>OSC address path to match (e.g. "/Go", "/cue/1/start"). Case-sensitive.</summary>
    public string Address { get; set; } = string.Empty;

    /// <summary>
    /// When true, also require <see cref="ArgsDisplay"/> to match the received argument string.
    /// When false, only the address is matched (any args).
    /// </summary>
    public bool MatchArgs { get; set; }

    /// <summary>
    /// Serialized argument list used when <see cref="MatchArgs"/> is true
    /// (same format as the monitor log, e.g. "1" or "1, \"hello\"").
    /// </summary>
    public string ArgsDisplay { get; set; } = string.Empty;

    /// <summary>Factory default: unbound.</summary>
    public static OscActionBinding Unbound() => new();

    /// <summary>
    /// Factory default OSC path for a project InputMap action, or unbound when none.
    /// These replace the former global built-in paths for the same actions.
    /// </summary>
    public static OscActionBinding GetDefaultFor(string actionName)
    {
        string path = actionName switch
        {
            "Go" => "/Go",
            "StopAll" => "/StopAll",
            "PauseAll" => "/PauseAll",
            "ResumeAll" => "/ResumeAll",
            "SelectAll" => "/SelectAll",
            "SelectNext" => "/SelectNext",
            "SelectPrevious" => "/SelectPrevious",
            "SaveSession" => "/Save",
            "Undo" => "/Undo",
            "Redo" => "/Redo",
            "EditMode" => "/EditMode",
            "ShowMode" => "/ShowMode",
            _ => null
        };
        return path == null ? Unbound() : FromAddress(path);
    }

    /// <summary>Creates an address-only binding (any arguments accepted).</summary>
    public static OscActionBinding FromAddress(string address)
    {
        var b = new OscActionBinding();
        b.SetFromAddress(address);
        return b;
    }

    /// <summary>
    /// True when this binding differs from the factory default for <paramref name="actionName"/>.
    /// </summary>
    public bool IsNonDefaultFor(string actionName)
    {
        return !EqualsBinding(GetDefaultFor(actionName));
    }

    /// <summary>
    /// Readable summary (e.g. "/Go" or "/level 0.5").
    /// </summary>
    public string GetDisplay()
    {
        if (!HasBinding || string.IsNullOrEmpty(Address)) return string.Empty;
        if (MatchArgs && !string.IsNullOrEmpty(ArgsDisplay))
            return $"{Address} {ArgsDisplay}";
        return Address;
    }

    /// <summary>
    /// Returns true when the received message matches this binding pattern.
    /// </summary>
    public bool Matches(string address, string argsDisplay)
    {
        if (!HasBinding || string.IsNullOrEmpty(Address)) return false;
        if (!string.Equals(Address, address, StringComparison.Ordinal)) return false;
        if (!MatchArgs) return true;
        return string.Equals(
            ArgsDisplay ?? string.Empty,
            argsDisplay ?? string.Empty,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Sets an address-only binding from user-typed path text.
    /// </summary>
    /// <param name="address">OSC address path (should start with /).</param>
    /// <returns>False when the address is empty or invalid.</returns>
    public bool SetFromAddress(string address)
    {
        string trimmed = (address ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            Clear();
            return false;
        }

        // Allow typing without leading slash — normalise.
        if (!trimmed.StartsWith('/'))
            trimmed = "/" + trimmed;

        // Basic validation: no whitespace in path.
        if (trimmed.IndexOfAny(new[] { ' ', '\t', '\r', '\n' }) >= 0)
            return false;

        HasBinding = true;
        Address = trimmed;
        MatchArgs = false;
        ArgsDisplay = string.Empty;
        return true;
    }

    /// <summary>
    /// Sets the binding from a captured OSC message (optional; Input Map uses typed paths).
    /// </summary>
    public void SetFromMessage(string address, string argsDisplay, bool matchArgs)
    {
        HasBinding = !string.IsNullOrWhiteSpace(address);
        Address = address?.Trim() ?? string.Empty;
        ArgsDisplay = argsDisplay ?? string.Empty;
        MatchArgs = matchArgs && !string.IsNullOrEmpty(ArgsDisplay);
    }

    /// <summary>Clears to unbound.</summary>
    public void Clear()
    {
        HasBinding = false;
        Address = string.Empty;
        MatchArgs = false;
        ArgsDisplay = string.Empty;
    }

    /// <summary>Structural equality for defaults / conflict checks.</summary>
    public bool EqualsBinding(OscActionBinding other)
    {
        if (other == null) return !HasBinding;
        if (HasBinding != other.HasBinding) return false;
        if (!HasBinding) return true;
        return string.Equals(Address ?? string.Empty, other.Address ?? string.Empty, StringComparison.Ordinal)
               && MatchArgs == other.MatchArgs
               && string.Equals(ArgsDisplay ?? string.Empty, other.ArgsDisplay ?? string.Empty, StringComparison.Ordinal);
    }

    /// <summary>Serializes for showfile / history.</summary>
    public Dictionary ToDict()
    {
        var d = new Dictionary();
        d["HasBinding"] = HasBinding;
        d["Address"] = Address ?? string.Empty;
        d["MatchArgs"] = MatchArgs;
        d["ArgsDisplay"] = ArgsDisplay ?? string.Empty;
        return d;
    }

    /// <summary>Deserializes from showfile / history (including explicit unbound overrides).</summary>
    public static OscActionBinding FromDict(Dictionary data)
    {
        var b = new OscActionBinding();
        if (data == null) return b;
        b.HasBinding = data.TryGetValue("HasBinding", out var h) && h.AsBool();
        b.Address = data.TryGetValue("Address", out var a) ? a.AsString() : string.Empty;
        b.MatchArgs = data.TryGetValue("MatchArgs", out var ma) && ma.AsBool();
        b.ArgsDisplay = data.TryGetValue("ArgsDisplay", out var ad) ? ad.AsString() : string.Empty;
        // Explicit unbound: HasBinding false with empty address.
        if (!b.HasBinding)
        {
            b.Clear();
            return b;
        }
        if (string.IsNullOrEmpty(b.Address))
            b.Clear();
        return b;
    }

    /// <summary>Deep copy.</summary>
    public OscActionBinding Clone()
    {
        return new OscActionBinding
        {
            HasBinding = HasBinding,
            Address = Address,
            MatchArgs = MatchArgs,
            ArgsDisplay = ArgsDisplay
        };
    }
}
