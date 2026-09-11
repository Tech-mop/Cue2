// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using Godot.Collections;

namespace Cue2.Domain.Cues;

/// <summary>
/// Contract for a single playback or control unit attached to a <see cref="Cue"/>.
/// </summary>
/// <remarks>
/// A cue shell holds zero or more components (audio, video, text, OSC, MIDI, control, cue light).
/// Components own their own serializable state; the parent cue stores them in its component list
/// and reconstructs them from type + <see cref="LoadFromData"/> when loading a showfile or applying history.
/// </remarks>
public interface ICueComponent
{
    /// <summary>
    /// Stable component type id used when serializing and when reconstructing components from save data.
    /// </summary>
    /// <value>
    /// A fixed string unique to the concrete component kind (for example
    /// <c>"Audio"</c>, <c>"Video"</c>, <c>"Text"</c>, <c>"Control"</c>,
    /// <c>"CueLight"</c>, <c>"OscComponent"</c>, <c>"MidiOutput"</c>).
    /// Must match the switch arms in <see cref="Cue"/> load paths.
    /// </value>
    string Type { get; }

    /// <summary>
    /// Serializes this component's state into a Godot dictionary for the showfile, library, or history snapshot.
    /// </summary>
    /// <returns>
    /// A dictionary of component fields. Callers typically also store <see cref="Type"/> on the same
    /// dictionary (or a wrapper) so load can instantiate the correct concrete class before calling
    /// <see cref="LoadFromData"/>.
    /// </returns>
    /// <remarks>
    /// Prefer plain value types and nested dictionaries so data round-trips through JSON showfiles.
    /// Avoid embedding non-serializable runtime handles (open devices, textures, decoders).
    /// </remarks>
    Dictionary GetData();

    /// <summary>
    /// Restores this component's state from a dictionary previously produced by <see cref="GetData"/>
    /// (or an equivalent save shape).
    /// </summary>
    /// <param name="data">
    /// Component field dictionary. Missing keys should keep safe defaults where practical;
    /// required media paths or ids may log and leave the component incomplete.
    /// </param>
    /// <remarks>
    /// Called after construction of the concrete type selected via <see cref="Type"/>.
    /// Does not attach the component to a cue — the parent <see cref="Cue"/> owns the component list.
    /// Relinking of live objects (patches, OSC connections, layers) may be completed by higher-level
    /// load / relink helpers after all components are restored.
    /// </remarks>
    void LoadFromData(Dictionary data);
}
