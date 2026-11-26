// SPDX-FileCopyrightText: 2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System;
using Cue2.Domain.Devices;
using GdUnit4;
using Godot;
using static GdUnit4.Assertions;

namespace Cue2.EngineTests.Displays;

/// <summary>
/// Serialization round-trips for canvas target layers. Rotation is an output-mapping
/// property: it must survive save/load and default to 0 (unrotated) for legacy showfiles.
/// Needs the Godot runtime for Godot.Collections.Dictionary (not plain-suite safe).
/// </summary>
[TestSuite]
[RequireGodotRuntime]
public class VideoTargetLayerTests
{
    /// <summary>All layer properties, including output rotation, survive a GetData/LoadFromData round trip.</summary>
    [TestCase]
    public void LayerRoundTripsThroughGetData()
    {
        var layer = new VideoTargetLayer("Stage Left", 2)
        {
            CanvasPosition = new Vector2I(100, 200),
            Size = new Vector2I(640, 480),
            Rotation = 37.5f
        };

        Godot.Collections.Dictionary data = layer.GetData();

        var restored = new VideoTargetLayer("Restored", 0);
        restored.LoadFromData(data);

        AssertThat(restored.LayerId).IsEqual(layer.LayerId);
        AssertThat(restored.LayerName).IsEqual("Stage Left");
        AssertThat(restored.ZIndex).IsEqual(2);
        AssertThat(restored.CanvasPosition).IsEqual(new Vector2I(100, 200));
        AssertThat(restored.Size).IsEqual(new Vector2I(640, 480));
        AssertThat(Mathf.Abs(restored.Rotation - 37.5f) < 0.0001f)
            .OverrideFailureMessage($"Rotation did not round-trip: {restored.Rotation}")
            .IsTrue();
    }

    /// <summary>Showfiles saved before rotation existed load unrotated (no Rotation key defaults to 0).</summary>
    [TestCase]
    public void LegacyLayerWithoutRotationLoadsUnrotated()
    {
        var data = new Godot.Collections.Dictionary
        {
            { "LayerId", 7 },
            { "LayerName", "Legacy" },
            { "ZIndex", 1 },
            { "CanvasPositionX", 10 },
            { "CanvasPositionY", 20 },
            { "SizeX", 1920 },
            { "SizeY", 1080 }
        };

        var layer = new VideoTargetLayer();
        layer.LoadFromData(data);

        AssertThat(layer.LayerId).IsEqual(7);
        AssertThat(layer.LayerName).IsEqual("Legacy");
        AssertThat(layer.CanvasPosition).IsEqual(new Vector2I(10, 20));
        AssertThat(layer.Size).IsEqual(new Vector2I(1920, 1080));
        AssertThat(layer.Rotation).IsEqual(0f);
    }

    /// <summary>A fresh layer serializes Rotation = 0 so every saved show carries the key.</summary>
    [TestCase]
    public void NewLayerSerializesZeroRotation()
    {
        var layer = new VideoTargetLayer("Default", 0);
        Godot.Collections.Dictionary data = layer.GetData();

        AssertThat(data.ContainsKey("Rotation")).IsTrue();
        AssertThat(data["Rotation"].AsSingle()).IsEqual(0f);
    }

    /// <summary>Angles outside [-360, 360) are wrapped on load so showfiles stay in range.</summary>
    [TestCase]
    public void LoadedRotationIsNormalized()
    {
        var data = new Godot.Collections.Dictionary
        {
            { "LayerId", 3 },
            { "LayerName", "Wrap" },
            { "Rotation", 370f }
        };

        var layer = new VideoTargetLayer();
        layer.LoadFromData(data);

        AssertThat(Mathf.Abs(layer.Rotation - (-350f)) < 0.0001f)
            .OverrideFailureMessage($"Rotation was not wrapped: {layer.Rotation}")
            .IsTrue();
    }
}
