// SPDX-FileCopyrightText: 2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System.Threading.Tasks;
using Cue2.Domain.Devices;
using Cue2.EngineTests.Fixtures;
using Cue2.Services;
using GdUnit4;
using Godot;
using static GdUnit4.Assertions;

namespace Cue2.EngineTests.Displays;

/// <summary>
/// Regression: undoing a target-layer rotation (spinbox, reset button, Alt+drag) must restore
/// the previous angle without losing any layers, and undo must actually revert the rotation.
/// </summary>
[TestSuite]
[RequireGodotRuntime]
public class CanvasRotationUndoTests
{
    private static DisplaysManager Displays =>
        EngineHarness.Tree.Root.GetNode<DisplaysManager>("DisplaysManager");

    private static HistoryManager History => EngineHarness.Data.HistoryManager;

    [Before]
    public async Task Boot() => await EngineHarness.BootAsync();

    [BeforeTest]
    public async Task Reset() => await EngineHarness.ResetShowAsync();

    /// <summary>Undo after a discrete rotation change reverts the angle and keeps every layer.</summary>
    [TestCase(Timeout = 60000)]
    public async Task UndoAfterRotationChangeKeepsLayers()
    {
        var layer = Displays.AddLayer("RotationUndoTest", 1);
        int countBefore = DisplaysManager.Layers.Count;
        AssertThat(countBefore).IsGreater(1);

        // Same call chain as SettingsCanvasEditor.RecordDisplaysHistory.
        History.RecordSettingsChange("Change layer rotation", null, "Displays");
        Displays.UpdateLayerRotation(layer.LayerId, 45f);
        await EngineHarness.FramesAsync(2);

        AssertThat(DisplaysManager.GetLayerById(layer.LayerId).Rotation).IsEqual(45f);

        History.Undo();
        await EngineHarness.FramesAsync(2);

        var restored = DisplaysManager.GetLayerById(layer.LayerId);
        AssertThat(restored).IsNotNull();
        AssertThat(DisplaysManager.Layers.Count)
            .OverrideFailureMessage(
                $"Undo lost layers: expected {countBefore}, got {DisplaysManager.Layers.Count}")
            .IsEqual(countBefore);
        AssertThat(restored.Rotation)
            .OverrideFailureMessage($"Undo did not revert rotation: {restored.Rotation}")
            .IsEqual(0f);
    }

    /// <summary>Undo after the rotation reset button returns to the previous angle with layers intact.</summary>
    [TestCase(Timeout = 60000)]
    public async Task UndoAfterRotationResetKeepsLayers()
    {
        var layer = Displays.AddLayer("RotationResetTest", 1);
        int countBefore = DisplaysManager.Layers.Count;

        History.RecordSettingsChange("Change layer rotation", null, "Displays");
        Displays.UpdateLayerRotation(layer.LayerId, 30f);

        // Reset-button flow: record, then apply the default.
        History.RecordSettingsChange("Reset layer rotation", null, "Displays");
        Displays.UpdateLayerRotation(layer.LayerId, 0f);
        await EngineHarness.FramesAsync(2);

        History.Undo(); // back to 30°
        await EngineHarness.FramesAsync(2);

        var restored = DisplaysManager.GetLayerById(layer.LayerId);
        AssertThat(restored).IsNotNull();
        AssertThat(DisplaysManager.Layers.Count).IsEqual(countBefore);
        AssertThat(restored.Rotation).IsEqual(30f);
    }

    /// <summary>Undo after an Alt+drag-style coalesced rotation restores the start angle and layers.</summary>
    [TestCase(Timeout = 60000)]
    public async Task UndoAfterCoalescedRotateDragKeepsLayers()
    {
        var layer = Displays.AddLayer("AltDragTest", 1);
        int countBefore = DisplaysManager.Layers.Count;
        string key = $"settings:displays:layer:{layer.LayerId}:geom";

        // Same sequence as StartRotateDrag / EndCanvasInteraction for DragMode.Rotate.
        History.RecordSettingsChange("Rotate canvas layer", key, "Displays");
        Displays.UpdateLayerRotation(layer.LayerId, 90f);
        History.EndCoalesceSession(key);
        Displays.UpdateLayerRotation(layer.LayerId, 90f); // commit call at drag end
        await EngineHarness.FramesAsync(2);

        History.Undo();
        await EngineHarness.FramesAsync(2);

        var restored = DisplaysManager.GetLayerById(layer.LayerId);
        AssertThat(restored).IsNotNull();
        AssertThat(DisplaysManager.Layers.Count).IsEqual(countBefore);
        AssertThat(restored.Rotation).IsEqual(0f);
    }

    /// <summary>Two undos step back through both rotation edits without ever emptying the layer list.</summary>
    [TestCase(Timeout = 60000)]
    public async Task TwoUndosKeepALayer()
    {
        var layer = Displays.AddLayer("TwoUndosTest", 1);
        int countBefore = DisplaysManager.Layers.Count;

        History.RecordSettingsChange("Change layer rotation", null, "Displays");
        Displays.UpdateLayerRotation(layer.LayerId, 15f);
        History.RecordSettingsChange("Change layer rotation", null, "Displays");
        Displays.UpdateLayerRotation(layer.LayerId, 45f);
        await EngineHarness.FramesAsync(2);

        History.Undo();
        await EngineHarness.FramesAsync(2);
        AssertThat(DisplaysManager.Layers.Count).IsEqual(countBefore);
        AssertThat(DisplaysManager.GetLayerById(layer.LayerId).Rotation).IsEqual(15f);

        History.Undo();
        await EngineHarness.FramesAsync(2);
        AssertThat(DisplaysManager.Layers.Count)
            .OverrideFailureMessage("Second undo emptied the layer list")
            .IsEqual(countBefore);
        AssertThat(DisplaysManager.GetLayerById(layer.LayerId).Rotation).IsEqual(0f);
    }
}