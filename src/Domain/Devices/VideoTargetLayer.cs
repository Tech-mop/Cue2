// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using Godot;
using System;

namespace Cue2.Domain.Devices;

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

    public VideoTargetLayer()
    {
        LayerId = _nextLayerId++;
    }

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
        KeepAspect = data.ContainsKey("KeepAspect") && (bool)data["KeepAspect"];
        TestPatternEnabled = data.ContainsKey("TestPatternEnabled") && (bool)data["TestPatternEnabled"];
        Locked = data.ContainsKey("Locked") && (bool)data["Locked"];
    }
    
}