// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using Godot;

namespace Cue2.Domain.Devices;

/// <summary>
/// Logical video canvas: pixel size and canvas-wide presentation flags (e.g. test pattern).
/// </summary>
public class Canvas
{
    /// <summary>
    /// The dimensions of the canvas in pixels (width, height).
    /// </summary>
    public Vector2I CanvasSize { get; private set; } = new Vector2I(1920, 1080); // Default size

    /// <summary>
    /// When true, a full-canvas alignment test pattern is drawn across all screens
    /// (each output shows its intersecting portion so grids align between monitors).
    /// </summary>
    /// <value>Default is <c>false</c> (off).</value>
    public bool TestPatternEnabled { get; set; }

    /// <summary>
    /// Sets the canvas size and resizes the viewport accordingly.
    /// </summary>
    /// <param name="newSize">New canvas dimensions.</param>
    public void SetCanvasSize(Vector2I newSize)
    {
        if (newSize.X <= 0 || newSize.Y <= 0)
        {
            return;
        }
        CanvasSize = newSize;
    }

    /// <summary>
    /// Serializes the canvas state to a dictionary.
    /// </summary>
    /// <returns>Dictionary containing canvas data.</returns>
    public Godot.Collections.Dictionary GetData()
    {
        var data = new Godot.Collections.Dictionary();
        data.Add("CanvasSizeX", CanvasSize.X);
        data.Add("CanvasSizeY", CanvasSize.Y);
        data.Add("TestPatternEnabled", TestPatternEnabled);
        return data;
    }

    /// <summary>
    /// Loads the canvas state from a dictionary.
    /// </summary>
    /// <param name="data">Dictionary containing canvas data.</param>
    public void LoadFromData(Godot.Collections.Dictionary data)
    {
        var size = new Vector2I(1920, 1080);
        size.X = data.ContainsKey("CanvasSizeX") ? (int)data["CanvasSizeX"] : 1920;
        size.Y = data.ContainsKey("CanvasSizeY") ? (int)data["CanvasSizeY"] : 1080;
        SetCanvasSize(size);
        TestPatternEnabled = data.ContainsKey("TestPatternEnabled") && (bool)data["TestPatternEnabled"];
    }
}