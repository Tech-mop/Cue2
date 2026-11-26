// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using Godot;
using System;

namespace Cue2.Domain.Devices;

/// <summary>
/// A named rectangle on the video canvas that video and text cues can target.
/// Geometry (position, size, rotation) is an output mapping: content is laid out
/// into the unrotated layer, then the layer is placed and rotated on the canvas.
/// </summary>
public class VideoTargetLayer
{
    private static int _nextLayerId = 0;

    /// <summary>
    /// Unique ID for the layer.
    /// </summary>
    public int LayerId { get; private set; }

    /// <summary>
    /// Name of the layer for identification.
    /// </summary>
    public string LayerName { get; set; } = "Unnamed Layer";

    /// <summary>
    /// Draw-stack index. Higher values render on top.
    /// List order in <see cref="Cue2.Services.DisplaysManager.Layers"/> is top-first:
    /// first layer in the list has the highest ZIndex.
    /// </summary>
    public int ZIndex { get; set; } = 0;

    /// <summary>
    /// Position on the canvas (top-left corner).
    /// </summary>
    public Vector2I CanvasPosition { get; set; } = Vector2I.Zero;

    /// <summary>
    /// Size of the layer on the canvas.
    /// </summary>
    public Vector2I Size { get; set; } = new Vector2I(1920, 1080);

    /// <summary>
    /// Whether the layer is transparent.
    /// </summary>
    public bool Transparent { get; set; } = false;

    /// <summary>
    /// Output rotation of the layer on the canvas, in degrees (clockwise, Godot convention).
    /// Rotates the layer about its center on every screen output. This only affects the
    /// output mapping: the input content (expand/stretch layout of the source texture)
    /// is treated as normal and is not rotated before mapping.
    /// </summary>
    public float Rotation { get; set; } = 0f;

    /// <summary>
    /// Whether the test pattern is enabled for this layer.
    /// </summary>
    public bool TestPatternEnabled { get; set; } = false;

    /// <summary>
    /// When true, changing one size dimension updates the other to preserve aspect ratio.
    /// </summary>
    public bool KeepAspect { get; set; } = false;

    /// <summary>
    /// When true, control cues cannot apply Translate Layer geometry changes to this layer.
    /// Manual edits in the canvas editor remain allowed.
    /// </summary>
    public bool Locked { get; set; } = false;

    /// <summary>
    /// Wraps a canvas-layer rotation into the editor range <c>[-360, 360)</c>.
    /// </summary>
    /// <param name="degrees">Clockwise rotation in degrees.</param>
    /// <returns>Equivalent angle inside <c>[-360, 360)</c>.</returns>
    public static float NormalizeRotation(float degrees)
    {
        return Mathf.Wrap(degrees, -360f, 360f);
    }

    /// <summary>Allocates the next layer id and constructs an unrotated default-size layer.</summary>
    public VideoTargetLayer()
    {
        LayerId = _nextLayerId++;
    }

    /// <summary>
    /// Allocates the next layer id and constructs a named layer.
    /// </summary>
    /// <param name="name">Display name.</param>
    /// <param name="zIndex">Draw-stack index (higher renders on top).</param>
    public VideoTargetLayer(string name, int zIndex) : this()
    {
        LayerName = name;
        ZIndex = zIndex;
    }

    /// <summary>
    /// Sets the next auto-assigned layer id (used after loading a session).
    /// </summary>
    /// <param name="id">Next id to allocate for new layers.</param>
    public static void SetNextLayerId(int id)
    {
        _nextLayerId = Math.Max(0, id);
    }

    /// <summary>
    /// Serializes the layer data.
    /// </summary>
    /// <returns>Dictionary containing layer data.</returns>
    public Godot.Collections.Dictionary GetData()
    {
        var data = new Godot.Collections.Dictionary();
        data.Add("LayerId", LayerId);
        data.Add("LayerName", LayerName);
        data.Add("ZIndex", ZIndex);
        data.Add("CanvasPositionX", CanvasPosition.X);
        data.Add("CanvasPositionY", CanvasPosition.Y);
        data.Add("SizeX", Size.X);
        data.Add("SizeY", Size.Y);
        data.Add("Transparent", Transparent);
        data.Add("Rotation", Rotation);
        data.Add("KeepAspect", KeepAspect);
        data.Add("TestPatternEnabled", TestPatternEnabled);
        data.Add("Locked", Locked);
        return data;
    }

    /// <summary>
    /// Loads the layer data from a dictionary.
    /// </summary>
    /// <param name="data">Dictionary containing layer data.</param>
    /// <remarks>
    /// Must not mutate <see cref="LayerId"/> after assignment. The previous
    /// <c>_nextLayerId = LayerId++</c> post-increment rewrote loaded ids and broke
    /// multi-layer sessions / cue TargetLayerId references.
    /// </remarks>
    public void LoadFromData(Godot.Collections.Dictionary data)
    {
        LayerId = (int)data["LayerId"];
        // Advance allocator past this id without changing LayerId itself.
        if (LayerId >= _nextLayerId)
            _nextLayerId = LayerId + 1;

        LayerName = (string)data["LayerName"];
        ZIndex = data.ContainsKey("ZIndex") ? (int)data["ZIndex"] : 0;
        
        var outSizeX = data.ContainsKey("SizeX") ? (int)data["SizeX"] : 1920;
        var outSizeY = data.ContainsKey("SizeY") ? (int)data["SizeY"] : 1080;
        Size = new Vector2I(outSizeX, outSizeY);
        
        var canvPosX = data.ContainsKey("CanvasPositionX") ? (int)data["CanvasPositionX"] : 0;
        var canvPosY = data.ContainsKey("CanvasPositionY") ? (int)data["CanvasPositionY"] : 0;
        CanvasPosition = new Vector2I(canvPosX, canvPosY);

        Transparent = data.ContainsKey("Transparent") && (bool)data["Transparent"];
        // Older showfiles have no Rotation: default to 0 (unrotated) so they load unchanged.
        // Variant.AsSingle (not Convert.ToSingle): Godot Variant does not implement
        // IConvertible, so Convert throws and would break undo/show loads.
        Rotation = data.ContainsKey("Rotation")
            ? NormalizeRotation(data["Rotation"].AsSingle())
            : 0f;
        KeepAspect = data.ContainsKey("KeepAspect") && (bool)data["KeepAspect"];
        TestPatternEnabled = data.ContainsKey("TestPatternEnabled") && (bool)data["TestPatternEnabled"];
        Locked = data.ContainsKey("Locked") && (bool)data["Locked"];
    }
    
}