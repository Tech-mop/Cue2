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
using SDL3;

namespace Cue2.Services;

/// <summary>
/// Manages video output devices for displaying canvas regions on monitors.
/// This is Autoloaded by project.
/// </summary>
public partial class DisplaysManager : Node
{
    /// <summary>
    /// Information about an available display.
    /// </summary>
    public struct DisplayInfo
    {
        public int Index;
        public string Name;
        public Vector2I Position;
        public Vector2I Size;
        public int Dpi;
        public float RefreshRate;
    }

    public static Canvas Canvas;
    
    private GlobalSignals _globalSignals;
    private GlobalData _globalData;
    private PackedScene _videoLayer;

    /// <summary>
    /// When true, all non-virtual output windows are forced closed/hidden without
    /// destroying the canvas/screen model. Runtime-only (not saved with the show).
    /// </summary>
    public static bool OutputDisabled { get; private set; }

    /// <summary>
    /// When true, a full-window black overlay hides all layers on every output while
    /// windows remain open. Runtime-only (not saved with the show).
    /// </summary>
    public static bool OutputBlackout { get; private set; }

    /// <summary>
    /// List of active screens (video output devices). Each screen maps a canvas region
    /// to a physical monitor, a portable Window, or Virtual Output.
    /// </summary>
    public static List<VideoOutputDevice> Outputs { get; } = new List<VideoOutputDevice>();

    /// <summary>
    /// Alias for <see cref="Outputs"/> — screens in the canvas editor model.
    /// </summary>
    public static List<VideoOutputDevice> Screens => Outputs;

    /// <summary>
    /// List of video target layers.
    /// </summary>
    public static List<VideoTargetLayer> Layers { get; } = new List<VideoTargetLayer>();

    public override void _Ready()
    {
        if (SingleInstanceGuard.IsSecondary)
            return;

        _globalSignals = GetNode<GlobalSignals>("/root/GlobalSignals");
        _globalData = GetNodeOrNull<GlobalData>("/root/GlobalData");
        Canvas = new Canvas();

        // Default layer is always needed. Skip the throwaway Screen 1 when last-show will
        // replace it immediately (LoadFromData / ClearForOpen).
        AddLayer("Default", 0);
        if (string.IsNullOrEmpty(_globalData?.StartupOpenPath))
            EnsureDefaultScreen();

        _globalSignals.EmitSignal(nameof(GlobalSignals.Log), "DisplaysManager initialized.", 0);
    }

    /// <summary>
    /// Enables or disables all house display windows (closes them without clearing topology).
    /// </summary>
    /// <param name="disabled">True to hide/close outputs; false to restore placement.</param>
    public void SetOutputDisabled(bool disabled)
    {
        if (OutputDisabled == disabled)
            return;

        OutputDisabled = disabled;
        if (disabled)
        {
            foreach (var output in Outputs.ToList())
            {
                if (output == null || !GodotObject.IsInstanceValid(output))
                    continue;
                output.ForceHideForDisable();
            }
            _globalSignals?.EmitSignal(nameof(GlobalSignals.Log),
                "DisplaysManager: Output disabled — all display windows closed.", 1);
        }
        else
        {
            // Re-place physical / window outputs; virtual stays hidden.
            foreach (var output in Outputs.ToList())
            {
                if (output == null || !GodotObject.IsInstanceValid(output))
                    continue;
                if (output.IsVirtual)
                    continue;
                if (output.IsWindow)
                    output.ClearWindowDismissed();
                output.ForceRefreshOutput();
            }
            ApplyOutputPresentationState();
            _globalSignals?.EmitSignal(nameof(GlobalSignals.Log),
                "DisplaysManager: Output re-enabled — display windows restored.", 0);
        }

        EmitOutputControlChanged();
    }

    /// <summary>
    /// Enables or disables master blackout on all outputs (windows stay open).
    /// </summary>
    /// <param name="blackout">True to black out all layers; false to reveal them.</param>
    public void SetOutputBlackout(bool blackout)
    {
        if (OutputBlackout == blackout)
            return;

        OutputBlackout = blackout;
        foreach (var output in Outputs.ToList())
        {
            if (output == null || !GodotObject.IsInstanceValid(output))
                continue;
            output.SetBlackout(blackout);
        }

        _globalSignals?.EmitSignal(nameof(GlobalSignals.Log),
            blackout
                ? "DisplaysManager: Output blackout ON."
                : "DisplaysManager: Output blackout OFF.",
            blackout ? 1 : 0);
        EmitOutputControlChanged();
    }

    /// <summary>
    /// Clears runtime disable/blackout (e.g. new session / show load). Does not change topology.
    /// </summary>
    public void ClearRuntimeOutputControls()
    {
        bool changed = OutputDisabled || OutputBlackout;
        OutputDisabled = false;
        OutputBlackout = false;
        foreach (var output in Outputs.ToList())
        {
            if (output == null || !GodotObject.IsInstanceValid(output))
                continue;
            output.SetBlackout(false);
        }

        if (changed)
            EmitOutputControlChanged();
    }

    /// <summary>
    /// Applies show background colour and vsync prefs to every output window.
    /// </summary>
    public void ApplyOutputPresentationState()
    {
        Color bg = _globalData?.Settings?.OutputBackgroundColor ?? Colors.Black;
        OutputVSyncMode vsync = _globalData?.Settings?.OutputVSyncMode
            ?? OutputVSyncMode.PreferVSync;

        foreach (var output in Outputs.ToList())
        {
            if (output == null || !GodotObject.IsInstanceValid(output))
                continue;
            output.SetOutputBackgroundColor(bg);
            output.SetBlackout(OutputBlackout);
            output.ApplyVSyncMode(vsync);
        }
    }

    /// <summary>
    /// Applies the show-scoped output background colour to all live windows.
    /// </summary>
    /// <param name="color">Background colour behind layers.</param>
    public void ApplyOutputBackgroundColor(Color color)
    {
        foreach (var output in Outputs.ToList())
        {
            if (output == null || !GodotObject.IsInstanceValid(output))
                continue;
            output.SetOutputBackgroundColor(color);
        }
        _globalSignals?.EmitSignal(nameof(GlobalSignals.OutputBackgroundColorChanged), color);
    }

    /// <summary>
    /// Re-applies show-scoped vsync preference to all live output windows.
    /// </summary>
    public void ApplyOutputVSyncPreference()
    {
        OutputVSyncMode vsync = _globalData?.Settings?.OutputVSyncMode
            ?? OutputVSyncMode.PreferVSync;
        foreach (var output in Outputs.ToList())
        {
            if (output == null || !GodotObject.IsInstanceValid(output))
                continue;
            output.ApplyVSyncMode(vsync);
        }
    }

    private void EmitOutputControlChanged()
    {
        _globalSignals?.EmitSignal(nameof(GlobalSignals.VideoOutputControlChanged),
            OutputDisabled, OutputBlackout);
    }

    /// <summary>
    /// Ensures at least one screen exists. Creates "Screen 1" as Virtual Output when empty.
    /// </summary>
    public void EnsureDefaultScreen()
    {
        if (Outputs.Count > 0)
            return;

        AddScreen("Screen 1", VideoOutputDevice.VirtualMonitorIndex);
    }

    /// <summary>
    /// Frees all output windows and layers without seeding a default screen.
    /// Used as the wipe step before <see cref="LoadFromData"/> so Open does not
    /// create a throwaway virtual window.
    /// </summary>
    /// <remarks>
    /// Does not emit <see cref="GlobalSignals.DisplaysChanged"/> — the following
    /// <see cref="LoadFromData"/> (or <see cref="ResetToDefaults"/>) notifies once.
    /// </remarks>
    public void ClearForOpen()
    {
        foreach (var output in Outputs.ToList())
        {
            if (output != null && GodotObject.IsInstanceValid(output))
            {
                RemoveChild(output);
                output.QueueFree();
            }
        }
        Outputs.Clear();
        VideoOutputDevice.SetNextOutputId(0);

        Layers.Clear();
        VideoTargetLayer.SetNextLayerId(0);

        Canvas ??= new Canvas();
        ClearRuntimeOutputControls();

        GD.Print("DisplaysManager:ClearForOpen - Screens and layers freed (no default seed).");
    }

    /// <summary>
    /// Resets canvas, layers, and screens to a clean new-show default state.
    /// </summary>
    /// <remarks>
    /// Canvas 1920×1080, one "Default" layer, one virtual "Screen 1". Emits
    /// <see cref="GlobalSignals.CanvasSizeChanged"/> and <see cref="GlobalSignals.DisplaysChanged"/>.
    /// </remarks>
    public void ResetToDefaults()
    {
        ClearForOpen();

        Canvas.SetCanvasSize(new Vector2I(1920, 1080));
        Canvas.TestPatternEnabled = false;

        AddLayer("Default", 0);
        EnsureDefaultScreen();

        UpdateCanvasTestPatterns();
        UpdateAllLayerTestPatterns();
        ApplyLayerDrawOrderToOutputs();
        ApplyOutputPresentationState();

        _globalSignals?.EmitSignal(nameof(GlobalSignals.CanvasSizeChanged), Canvas.CanvasSize);
        _globalSignals?.EmitSignal(nameof(GlobalSignals.DisplaysChanged));

        GD.Print("DisplaysManager:ResetToDefaults - Canvas/layers/screens restored to defaults.");
    }

    /// <summary>
    /// Adds a new screen with the given output assignment.
    /// </summary>
    /// <param name="name">Display name for the screen.</param>
    /// <param name="monitorIndex">Physical monitor index, <see cref="VideoOutputDevice.VirtualMonitorIndex"/>,
    /// or <see cref="VideoOutputDevice.WindowMonitorIndex"/> for a portable window.</param>
    /// <param name="canvasPosition">Optional canvas position; defaults to origin.</param>
    /// <param name="size">Optional size; defaults to canvas size.</param>
    /// <returns>The created screen (VideoOutputDevice).</returns>
    public VideoOutputDevice AddScreen(string name = null, int monitorIndex = VideoOutputDevice.VirtualMonitorIndex,
        Vector2I? canvasPosition = null, Vector2I? size = null)
    {
        var screenSize = size ?? (Canvas != null ? Canvas.CanvasSize : new Vector2I(1920, 1080));
        var screenPos = canvasPosition ?? Vector2I.Zero;
        string screenName = name ?? $"Screen {Outputs.Count + 1}";
        return AddOutput(monitorIndex, screenPos, screenSize, screenName);
    }

    /// <summary>
    /// Readable destination label for logs and UI.
    /// </summary>
    public static string GetOutputDestinationLabel(VideoOutputDevice output)
    {
        if (output == null)
            return "Unknown";
        if (output.IsVirtual)
            return "Virtual Output";
        if (output.IsWindow)
            return "Window";
        return $"monitor {output.TargetMonitor}";
    }

    /// <summary>
    /// Adds a new video output device (screen) for the specified destination.
    /// </summary>
    /// <param name="monitorIndex">Physical monitor index, Virtual Output, or Window sentinel.</param>
    /// <param name="canvasPosition">Position on the canvas.</param>
    /// <param name="size">Size of the output region.</param>
    /// <param name="name">Name of the screen.</param>
    /// <returns>The created VideoOutputDevice.</returns>
    public VideoOutputDevice AddOutput(int monitorIndex, Vector2I canvasPosition, Vector2I size, string name = null)
    {
        var output = new VideoOutputDevice();
        bool isNamedVirtualOrWindow = monitorIndex == VideoOutputDevice.VirtualMonitorIndex
            || monitorIndex == VideoOutputDevice.WindowMonitorIndex;
        output.OutputName = name ?? (isNamedVirtualOrWindow ? $"Screen {Outputs.Count + 1}" : $"Output {monitorIndex}");
        output.CanvasPosition = canvasPosition;
        output.OutputSize = size;
        output.TargetMonitor = monitorIndex;
        AddChild(output);
        
        Outputs.Add(output);
        // Virtual screens stay hidden; physical / window screens show via UpdateOutputRegion.
        // Master disable keeps house windows closed even for newly added screens.
        if (output.IsWindow)
            output.ClearWindowDismissed();
        if (!output.IsVirtual && !OutputDisabled)
            output.Show();
        output.SetCanvasReference(Canvas);
        ApplyPresentationToOutput(output);
        UpdateCanvasTestPatterns();
        UpdateAllLayerTestPatterns();

        string dest = GetOutputDestinationLabel(output);
        _globalSignals.EmitSignal(nameof(GlobalSignals.Log), $"DisplaysManager: Added screen '{output.OutputName}' ({dest}).", 0);
        _globalSignals.EmitSignal(nameof(GlobalSignals.DisplaysChanged));
        return output;
    }

    /// <summary>
    /// Applies background colour, blackout, and vsync prefs to a single output.
    /// </summary>
    private void ApplyPresentationToOutput(VideoOutputDevice output)
    {
        if (output == null || !GodotObject.IsInstanceValid(output))
            return;

        Color bg = _globalData?.Settings?.OutputBackgroundColor ?? Colors.Black;
        OutputVSyncMode vsync = _globalData?.Settings?.OutputVSyncMode
            ?? OutputVSyncMode.PreferVSync;
        output.SetOutputBackgroundColor(bg);
        output.SetBlackout(OutputBlackout);
        output.ApplyVSyncMode(vsync);
        if (OutputDisabled)
            output.ForceHideForDisable();
    }

    /// <summary>
    /// Assigns a screen's output destination (physical monitor, portable Window, or Virtual Output).
    /// </summary>
    /// <param name="outputId">Screen output ID.</param>
    /// <param name="monitorIndex">Monitor index, <see cref="VideoOutputDevice.VirtualMonitorIndex"/>,
    /// or <see cref="VideoOutputDevice.WindowMonitorIndex"/>.</param>
    public void UpdateScreenTargetMonitor(int outputId, int monitorIndex)
    {
        var output = Outputs.Find(o => o.OutputId == outputId);
        if (output == null)
            return;

        output.TargetMonitor = monitorIndex;
        // Force a full region refresh when switching destinations
        if (output.IsVirtual || OutputDisabled)
        {
            output.Hide();
        }
        else if (output.IsWindow)
        {
            output.ClearWindowDismissed();
            // Leaving borderless exclusive paths: ensure OS chrome is available next place.
            output.Borderless = false;
            output.Mode = Window.ModeEnum.Windowed;
            if (!output.Visible)
                output.Show();
            output.UpdateOutputRegion();
        }
        else
        {
            // Do not set CurrentScreen here. Until the output has its own native window
            // id, Window.GetWindowId() can report the main viewport (Linux embed), and
            // CurrentScreen would move or fullscreen the operator UI.
            output.UpdateOutputRegion();
        }

        UpdateCanvasTestPatterns();
        UpdateAllLayerTestPatterns();

        string dest = GetOutputDestinationLabel(output);
        if (output.IsPhysical && !LinuxWindowEmbedPolicy.CanPlaceWindowsOnSpecificScreen)
        {
            _globalSignals.EmitSignal(nameof(GlobalSignals.Log),
                $"DisplaysManager: Screen '{output.OutputName}' assigned to {dest}. "
                + "This display server cannot place windows on a specific monitor — "
                + "drag the output window onto the house display.", 1);
        }
        else
        {
            _globalSignals.EmitSignal(nameof(GlobalSignals.Log),
                $"DisplaysManager: Screen '{output.OutputName}' assigned to {dest}.", 0);
        }
        _globalSignals.EmitSignal(nameof(GlobalSignals.DisplaysChanged));
    }

    /// <summary>
    /// Renames a screen.
    /// </summary>
    public void UpdateScreenName(int outputId, string newName)
    {
        var output = Outputs.Find(o => o.OutputId == outputId);
        if (output == null || string.IsNullOrWhiteSpace(newName))
            return;

        output.OutputName = newName.Trim();
        if (output.IsWindow)
            output.Title = output.OutputName;
        output.RefreshScreenTestPattern();
        _globalSignals.EmitSignal(nameof(GlobalSignals.Log),
            $"DisplaysManager: Renamed screen to '{output.OutputName}'.", 0);
        _globalSignals.EmitSignal(nameof(GlobalSignals.DisplaysChanged));
    }

    /// <summary>
    /// Sets the display offset for a screen (window position relative to the monitor origin).
    /// </summary>
    /// <param name="outputId">Screen output ID.</param>
    /// <param name="displayOffset">Offset in pixels from the target display home position.</param>
    public void UpdateScreenDisplayOffset(int outputId, Vector2I displayOffset)
    {
        var output = Outputs.Find(o => o.OutputId == outputId);
        if (output == null)
            return;

        output.DisplayOffset = displayOffset;
        if (output.IsWindow)
            output.ClearWindowDismissed();
        output.UpdateOutputRegion();
        _globalSignals.EmitSignal(nameof(GlobalSignals.Log),
            $"DisplaysManager: Screen '{output.OutputName}' display offset set to {displayOffset}.", 0);
        _globalSignals.EmitSignal(nameof(GlobalSignals.DisplaysChanged));
    }

    /// <summary>
    /// Sets whether a screen keeps aspect ratio when size is edited.
    /// </summary>
    public void UpdateScreenKeepAspect(int outputId, bool keepAspect)
    {
        var output = Outputs.Find(o => o.OutputId == outputId);
        if (output == null)
            return;

        output.KeepAspect = keepAspect;
        _globalSignals.EmitSignal(nameof(GlobalSignals.DisplaysChanged));
    }

    /// <summary>
    /// Default canvas size for a screen: physical display resolution when assigned,
    /// otherwise canvas size (Virtual Output and Window).
    /// </summary>
    public Vector2I GetDefaultScreenSize(VideoOutputDevice screen)
    {
        if (screen == null)
            return Canvas?.CanvasSize ?? new Vector2I(1920, 1080);

        if (screen.IsPhysical && screen.TargetMonitor < DisplayServer.GetScreenCount())
        {
            foreach (var d in GetAvailableDisplays())
            {
                if (d.Index == screen.TargetMonitor)
                    return d.Size;
            }
            return DisplayServer.ScreenGetSize(screen.TargetMonitor);
        }

        return Canvas?.CanvasSize ?? new Vector2I(1920, 1080);
    }

    /// <summary>
    /// Default layer size equals the canvas size.
    /// </summary>
    public Vector2I GetDefaultLayerSize()
    {
        return Canvas?.CanvasSize ?? new Vector2I(1920, 1080);
    }

    /// <summary>
    /// Removes a video output device by ID.
    /// </summary>
    /// <param name="outputId">The output ID to remove.</param>
    public void RemoveOutput(int outputId)
    {
        var output = Outputs.Find(o => o.OutputId == outputId);
        if (output != null)
        {
            // Remove layer test patterns from this output
            foreach (var layer in Layers)
            {
                if (layer.TestPatternEnabled)
                {
                    output.RemoveLayerTestPattern(layer.LayerId);
                }
            }
            Outputs.Remove(output);
            output.QueueFree();
            _globalSignals.EmitSignal(nameof(GlobalSignals.Log), $"Removed video output '{output.OutputName}'.", 0);
            _globalSignals.EmitSignal(nameof(GlobalSignals.DisplaysChanged));
        }
    }

    /// <summary>
    /// Gets a video output device by ID.
    /// </summary>
    /// <param name="outputId">The output ID.</param>
    /// <returns>The VideoOutputDevice or null.</returns>
    public VideoOutputDevice GetOutputById(int outputId)
    {
        return Outputs.Find(o => o.OutputId == outputId);
    }

    /// <summary>
    /// Gets a video target layer by ID.
    /// </summary>
    /// <param name="layerId">The layer ID.</param>
    /// <returns>The VideoTargetLayer or null.</returns>
    public static VideoTargetLayer GetLayerById(int layerId)
    {
        return Layers.Find(l => l.LayerId == layerId);
    }

    /// <summary>
    /// Updates all output regions (called when canvas changes).
    /// </summary>
    public void UpdateAllOutputs()
    {
        foreach (var output in Outputs)
        {
            output.UpdateOutputRegion();
        }
    }

    /// <summary>
    /// Re-enumerates displays and force-refreshes every screen/output.
    /// Restores portable windows the user closed, re-places physical outputs, and hides virtual ones.
    /// </summary>
    public void RefreshAllScreens()
    {
        InvalidateDisplayCache();
        GetAvailableDisplays(); // warm cache after invalidation

        foreach (var output in Outputs.ToList())
        {
            if (output == null || !GodotObject.IsInstanceValid(output))
                continue;

            if (output.IsVirtual)
            {
                try { output.Hide(); } catch { /* ignore */ }
                continue;
            }

            // Window: clear dismissed so close-button hide is undone.
            // Physical: re-place on monitor if still available.
            output.ForceRefreshOutput();
        }

        UpdateCanvasTestPatterns();
        UpdateAllLayerTestPatterns();
        _globalSignals.EmitSignal(nameof(GlobalSignals.Log),
            "DisplaysManager: Refreshed all screens and outputs.", 0);
        _globalSignals.EmitSignal(nameof(GlobalSignals.DisplaysChanged));
    }

    public void UpdateAllLayers()
    {
        foreach (var output in Outputs)
        {
            //output.UpdateLayers();
        }
    }

    /// <summary>
    /// Updates the canvas position of a video output device.
    /// </summary>
    /// <param name="outputId">The output ID.</param>
    /// <param name="newCanvasPosition">The new canvas position.</param>
    public void UpdateOutputCanvasPosition(int outputId, Vector2I newCanvasPosition)
    {
        var output = Outputs.Find(o => o.OutputId == outputId);
        if (output != null)
        {
            output.CanvasPosition = newCanvasPosition;
            // Editor-driven changes re-show a user-closed portable window.
            if (output.IsWindow)
                output.ClearWindowDismissed();
            output.UpdateOutputRegion();
            // Screen moved on canvas — layer rects are relative to screen origin.
            output.UpdateAllLayerDisplayRects();
            UpdateCanvasTestPatterns();
            UpdateAllLayerTestPatterns();
            _globalSignals.EmitSignal(nameof(GlobalSignals.DisplaysChanged));
        }
    }

    /// <summary>
    /// Updates the size of a video output device.
    /// </summary>
    /// <param name="outputId">The output ID.</param>
    /// <param name="newSize">The new size.</param>
    public void UpdateOutputSize(int outputId, Vector2I newSize)
    {
        var output = Outputs.Find(o => o.OutputId == outputId);
        if (output != null)
        {
            output.OutputSize = newSize;
            if (output.IsWindow)
                output.ClearWindowDismissed();
            output.UpdateOutputRegion();
            output.RefreshScreenTestPattern();
            UpdateCanvasTestPatterns();
            UpdateAllLayerTestPatterns();
            _globalSignals.EmitSignal(nameof(GlobalSignals.DisplaysChanged));
        }
    }

    /// <summary>
    /// Adds a new layer to the canvas.
    /// </summary>
    /// <param name="name">Name of the layer.</param>
    /// <param name="zIndex">Z-index for ordering.</param>
    /// <returns>The created VideoTargetLayer.</returns>
    /// <remarks>
    /// Emits <see cref="GlobalSignals.DisplaysChanged"/> so open inspectors (text/video target layer
    /// OptionButtons) and video defaults refresh their layer lists.
    /// </remarks>
    public VideoTargetLayer AddLayer(string name, int zIndex)
    {
        var layer = new VideoTargetLayer(name, zIndex);
        layer.Size = Canvas.CanvasSize;
        layer.CanvasPosition = Vector2I.Zero;
        // New layers go on top of the stack (index 0).
        Layers.Insert(0, layer);
        NormalizeLayerOrder();
        ApplyLayerDrawOrderToOutputs();
        _globalSignals?.EmitSignal(nameof(GlobalSignals.Log), $"Added layer '{name}' to canvas.", 0);
        _globalSignals?.EmitSignal(nameof(GlobalSignals.DisplaysChanged));
        return layer;
    }

    /// <summary>
    /// Syncs ZIndex from list order: first entry is topmost (highest ZIndex).
    /// </summary>
    public void NormalizeLayerOrder()
    {
        for (int i = 0; i < Layers.Count; i++)
            Layers[i].ZIndex = Layers.Count - 1 - i;
    }

    /// <summary>
    /// Moves a layer one step toward the top of the stack (earlier in the list).
    /// </summary>
    /// <returns>True if the layer moved.</returns>
    public bool MoveLayerUp(int layerId)
    {
        int index = Layers.FindIndex(l => l.LayerId == layerId);
        if (index <= 0)
            return false;

        (Layers[index - 1], Layers[index]) = (Layers[index], Layers[index - 1]);
        NormalizeLayerOrder();
        ApplyLayerDrawOrderToOutputs();
        _globalSignals.EmitSignal(nameof(GlobalSignals.Log),
            $"Moved layer '{Layers[index - 1].LayerName}' up in stack.", 0);
        _globalSignals.EmitSignal(nameof(GlobalSignals.DisplaysChanged));
        return true;
    }

    /// <summary>
    /// Moves a layer one step toward the bottom of the stack (later in the list).
    /// </summary>
    /// <returns>True if the layer moved.</returns>
    public bool MoveLayerDown(int layerId)
    {
        int index = Layers.FindIndex(l => l.LayerId == layerId);
        if (index < 0 || index >= Layers.Count - 1)
            return false;

        (Layers[index + 1], Layers[index]) = (Layers[index], Layers[index + 1]);
        NormalizeLayerOrder();
        ApplyLayerDrawOrderToOutputs();
        _globalSignals.EmitSignal(nameof(GlobalSignals.Log),
            $"Moved layer '{Layers[index + 1].LayerName}' down in stack.", 0);
        _globalSignals.EmitSignal(nameof(GlobalSignals.DisplaysChanged));
        return true;
    }

    /// <summary>
    /// Index of a layer in the top-first stack list, or -1 if missing.
    /// </summary>
    public int GetLayerStackIndex(int layerId)
    {
        return Layers.FindIndex(l => l.LayerId == layerId);
    }

    /// <summary>
    /// Applies ZIndex / child order to all active display layer nodes on every output.
    /// </summary>
    public void ApplyLayerDrawOrderToOutputs()
    {
        foreach (var output in Outputs)
            output.ApplyLayerDrawOrder();
    }

    /// <summary>
    /// Removes a layer from the canvas.
    /// </summary>
    /// <param name="layerId">The ID of the layer to remove.</param>
    /// <remarks>
    /// Emits <see cref="GlobalSignals.DisplaysChanged"/> so target-layer pickers drop the deleted layer.
    /// </remarks>
    public void RemoveLayer(int layerId)
    {
        var layer = Layers.Find(l => l.LayerId == layerId);
        if (layer != null)
        {
            // Remove test patterns from all outputs
            foreach (var output in Outputs)
            {
                output.RemoveLayerTestPattern(layer.LayerId);
            }
            Layers.Remove(layer);
            NormalizeLayerOrder();
            ApplyLayerDrawOrderToOutputs();
            _globalSignals?.EmitSignal(nameof(GlobalSignals.Log), $"Removed layer '{layer.LayerName}'.", 0);
            _globalSignals?.EmitSignal(nameof(GlobalSignals.DisplaysChanged));
        }
    }

    /// <summary>
    /// Updates the name of a layer.
    /// </summary>
    /// <param name="layerId">The layer ID.</param>
    /// <param name="newName">The new name.</param>
    /// <remarks>
    /// Emits <see cref="GlobalSignals.DisplaysChanged"/> so OptionButton labels stay in sync.
    /// </remarks>
    public void UpdateLayerName(int layerId, string newName)
    {
        var layer = Layers.Find(l => l.LayerId == layerId);
        if (layer != null)
        {
            layer.LayerName = newName;
            if (layer.TestPatternEnabled)
                UpdateLayerTestPatterns(layerId);
            _globalSignals?.EmitSignal(nameof(GlobalSignals.Log), $"Updated layer name to '{newName}'.", 0);
            _globalSignals?.EmitSignal(nameof(GlobalSignals.DisplaysChanged));
        }
    }

    /// <summary>
    /// Updates the canvas position of a layer.
    /// </summary>
    /// <param name="layerId">The layer ID.</param>
    /// <param name="newPosition">The new canvas position.</param>
    public void UpdateLayerCanvasPosition(int layerId, Vector2I newPosition)
    {
        var layer = Layers.Find(l => l.LayerId == layerId);
        if (layer != null)
        {
            layer.CanvasPosition = newPosition;
            UpdateLayerTestPatterns(layerId);
            // Move live video TextureRects on every physical/virtual output (not only test patterns).
            UpdateLayerDisplayRectsOnOutputs(layerId);
            _globalSignals.EmitSignal(nameof(GlobalSignals.Log), $"Updated layer position to {newPosition}.", 0);
        }
    }

    /// <summary>
    /// Updates the size of a layer.
    /// </summary>
    /// <param name="layerId">The layer ID.</param>
    /// <param name="newSize">The new size.</param>
    public void UpdateLayerSize(int layerId, Vector2I newSize)
    {
        var layer = Layers.Find(l => l.LayerId == layerId);
        if (layer != null)
        {
            layer.Size = newSize;
            UpdateLayerTestPatterns(layerId);
            UpdateLayerDisplayRectsOnOutputs(layerId);
            _globalSignals.EmitSignal(nameof(GlobalSignals.Log), $"Updated layer size to {newSize}.", 0);
        }
    }

    /// <summary>
    /// Applies layer size and/or canvas position without logging (for animated control fades).
    /// Emits <see cref="GlobalSignals.LayerGeometryChanged"/> so open canvas editors can follow.
    /// </summary>
    /// <param name="layerId">Layer identity.</param>
    /// <param name="position">New canvas position, or null to leave unchanged.</param>
    /// <param name="size">New size, or null to leave unchanged.</param>
    public void ApplyLayerGeometryLive(int layerId, Vector2I? position, Vector2I? size)
    {
        var layer = Layers.Find(l => l.LayerId == layerId);
        if (layer == null) return;

        if (position.HasValue)
            layer.CanvasPosition = position.Value;
        if (size.HasValue)
        {
            var s = size.Value;
            // Keep size positive so display rects remain valid.
            layer.Size = new Vector2I(Math.Max(1, s.X), Math.Max(1, s.Y));
        }

        UpdateLayerTestPatterns(layerId);
        UpdateLayerDisplayRectsOnOutputs(layerId);
        _globalSignals?.EmitSignal(nameof(GlobalSignals.LayerGeometryChanged), layerId);
    }

    /// <summary>
    /// Pushes layer geometry to active video (and other) display rects on all outputs.
    /// </summary>
    private void UpdateLayerDisplayRectsOnOutputs(int layerId)
    {
        foreach (var output in Outputs)
            output.UpdateLayerDisplayRect(layerId);
    }

    /// <summary>
    /// Updates the transparency of a layer.
    /// </summary>
    /// <param name="layerId">The layer ID.</param>
    /// <param name="transparent">Whether the layer is transparent.</param>
    public void UpdateLayerTransparent(int layerId, bool transparent)
    {
        var layer = Layers.Find(l => l.LayerId == layerId);
        if (layer != null)
        {
            layer.Transparent = transparent;
            _globalSignals.EmitSignal(nameof(GlobalSignals.Log), $"Updated layer transparency to {transparent}.", 0);
        }
    }

    /// <summary>
    /// Sets whether a layer keeps aspect ratio when size is edited.
    /// </summary>
    public void UpdateLayerKeepAspect(int layerId, bool keepAspect)
    {
        var layer = Layers.Find(l => l.LayerId == layerId);
        if (layer != null)
            layer.KeepAspect = keepAspect;
    }

    /// <summary>
    /// Sets whether a layer is locked against cue Translate Layer controls.
    /// </summary>
    /// <param name="layerId">The layer ID.</param>
    /// <param name="locked">When true, Translate Layer control cues are ignored for this layer.</param>
    public void UpdateLayerLocked(int layerId, bool locked)
    {
        var layer = Layers.Find(l => l.LayerId == layerId);
        if (layer != null)
        {
            layer.Locked = locked;
            _globalSignals.EmitSignal(nameof(GlobalSignals.Log),
                $"{(locked ? "Locked" : "Unlocked")} layer '{layer.LayerName}' against Translate Layer controls.", 0);
        }
    }

    /// <summary>
    /// Enables or disables the full-canvas test pattern on all screens.
    /// Each output shows the portion of the canvas pattern that intersects its region
    /// so alignment grids line up across multi-monitor setups.
    /// </summary>
    /// <param name="toggle">True to show; false to remove.</param>
    public void ToggleCanvasTestPattern(bool toggle)
    {
        if (Canvas == null)
            return;

        Canvas.TestPatternEnabled = toggle;
        if (!toggle)
        {
            foreach (var output in Outputs)
                output.RemoveCanvasTestPattern();
        }
        else
        {
            UpdateCanvasTestPatterns();
        }

        _globalSignals.EmitSignal(nameof(GlobalSignals.Log),
            $"{(toggle ? "Enabled" : "Disabled")} canvas test pattern.", 0);
    }

    /// <summary>
    /// Updates (or removes) the canvas-wide test pattern on every output from current geometry.
    /// </summary>
    public void UpdateCanvasTestPatterns()
    {
        if (Canvas == null)
            return;

        if (!Canvas.TestPatternEnabled)
        {
            foreach (var output in Outputs)
                output.RemoveCanvasTestPattern();
            return;
        }

        Rect2 canvasRect = new Rect2(0, 0, Canvas.CanvasSize.X, Canvas.CanvasSize.Y);
        foreach (var output in Outputs)
        {
            if (output == null || !GodotObject.IsInstanceValid(output))
                continue;

            Rect2 outputRect = new Rect2(output.CanvasPosition, output.OutputSize);
            Rect2 clippedRect = canvasRect.Intersection(outputRect);
            if (clippedRect.Size.X > 0 && clippedRect.Size.Y > 0)
            {
                // Canvas origin in local space of the clipped output content (same basis as layer TPs).
                Vector2 localPos = Vector2.Zero - clippedRect.Position;
                output.SetCanvasTestPattern(true, new Rect2(localPos, Canvas.CanvasSize), "Canvas");
            }
            else
            {
                output.RemoveCanvasTestPattern();
            }
        }
    }

    /// <summary>
    /// Toggles the test pattern for a layer on intersecting outputs.
    /// </summary>
    /// <param name="layerId">The layer ID.</param>
    /// <param name="toggle">Whether to enable or disable.</param>
    public void ToggleLayerTestPattern(int layerId, bool toggle)
    {
        var layer = Layers.Find(l => l.LayerId == layerId);
        if (layer == null) return;

        layer.TestPatternEnabled = toggle;
        if (!toggle)
        {
            foreach (var output in Outputs)
                output.RemoveLayerTestPattern(layerId);
        }
        else
        {
            UpdateLayerTestPatterns(layerId);
        }

        _globalSignals.EmitSignal(nameof(GlobalSignals.Log),
            $"{(toggle ? "Enabled" : "Disabled")} test pattern for layer '{layer.LayerName}'.", 0);
    }

    /// <summary>
    /// Updates (or removes) the test pattern for a layer on every output based on canvas intersection.
    /// </summary>
    /// <param name="layerId">The layer ID.</param>
    public void UpdateLayerTestPatterns(int layerId)
    {
        var layer = Layers.Find(l => l.LayerId == layerId);
        if (layer == null)
            return;

        if (!layer.TestPatternEnabled)
        {
            foreach (var output in Outputs)
                output.RemoveLayerTestPattern(layerId);
            return;
        }

        Rect2 layerRect = new Rect2(layer.CanvasPosition, layer.Size);
        Rect2 canvasRect = new Rect2(0, 0, Canvas.CanvasSize.X, Canvas.CanvasSize.Y);
        foreach (var output in Outputs)
        {
            Rect2 outputRect = new Rect2(output.CanvasPosition, output.OutputSize);
            if (layerRect.Intersection(outputRect).Size is { X: > 0, Y: > 0 })
            {
                Rect2 clippedRect = canvasRect.Intersection(outputRect);
                // Layer origin in this output's local space (full layer size; pattern may extend past clip).
                Vector2 localPos = layer.CanvasPosition - clippedRect.Position;
                output.AddLayerTestPattern(layer.LayerId, layer.LayerName, new Rect2(localPos, layer.Size));
            }
            else
            {
                // No longer overlaps this screen — drop stale pattern.
                output.RemoveLayerTestPattern(layer.LayerId);
            }
        }
    }

    /// <summary>
    /// Updates enabled layer test patterns on all outputs.
    /// </summary>
    public void UpdateAllLayerTestPatterns()
    {
        foreach (var layer in Layers)
            UpdateLayerTestPatterns(layer.LayerId);
    }

    /// <summary>
    /// Live geometry pass: refresh screen + layer test patterns after canvas-editor drag without
    /// committing OS window placement (that happens on drag end via <see cref="UpdateOutputRegion"/>).
    /// </summary>
    /// <param name="outputId">Screen being dragged/resized, or -1 for layer-only updates.</param>
    /// <param name="layerId">Layer being dragged/resized, or -1 for screen-wide refresh.</param>
    public void RefreshTestPatternsLive(int outputId = -1, int layerId = -1)
    {
        if (outputId >= 0)
        {
            var output = Outputs.Find(o => o.OutputId == outputId);
            output?.RefreshScreenTestPattern();
            // Screen move/resize changes local origins for canvas + layer patterns on that output.
            UpdateCanvasTestPatterns();
            UpdateAllLayerTestPatterns();
            return;
        }

        if (layerId >= 0)
        {
            UpdateLayerTestPatterns(layerId);
            return;
        }

        foreach (var output in Outputs)
            output.RefreshScreenTestPattern();
        UpdateCanvasTestPatterns();
        UpdateAllLayerTestPatterns();
    }

    /// <summary>
    /// Short-lived cache for <see cref="GetAvailableDisplays"/> — SDL enumeration is relatively
    /// expensive and was being called repeatedly from the canvas editor on the main thread,
    /// which can stall video presentation (also main-thread).
    /// </summary>
    private List<DisplayInfo> _cachedDisplays;
    private ulong _cachedDisplaysMs;
    private const ulong DisplayCacheTtlMs = 2000;

    /// <summary>
    /// Invalidates the display list cache (e.g. after a monitor change).
    /// </summary>
    public void InvalidateDisplayCache()
    {
        _cachedDisplays = null;
        _cachedDisplaysMs = 0;
    }

    /// <summary>
    /// Gets a list of available displays with their information.
    /// Results are cached briefly to avoid repeated SDL queries on the main thread.
    /// </summary>
    /// <returns>List of DisplayInfo for each detected display.</returns>
    public List<DisplayInfo> GetAvailableDisplays()
    {
        ulong now = Time.GetTicksMsec();
        if (_cachedDisplays != null && now - _cachedDisplaysMs < DisplayCacheTtlMs)
            return _cachedDisplays;

        var displays = new List<DisplayInfo>();
        try
        {
            // Calculate display position offset
            var gPrimI = DisplayServer.GetPrimaryScreen();
            var gPrimPos = DisplayServer.ScreenGetPosition(gPrimI);
            var sPrimI = SDL.GetPrimaryDisplay();
            SDL.GetDisplayBounds(sPrimI, out SDL.Rect sPrimRect);

            var offsetX = gPrimPos.X - sPrimRect.X;
            var offsetY = gPrimPos.Y - sPrimRect.Y;

            int screenCount = DisplayServer.GetScreenCount();

            var displayIDs = SDL.GetDisplays(out var sdlCount);
            if (sdlCount != screenCount)
            {
                _globalSignals.EmitSignal(nameof(GlobalSignals.Log),
                    $"Mismatch in display counts: Godot {screenCount}, SDL {sdlCount}. Using Godot count.", 1);
                GD.Print($"{SDL.GetError()}");
            }

            // Get SDL display data
            var sdlDisplays = new List<(uint ID, Vector2I Position, Vector2I Size)>();
            for (int j = 0; j < sdlCount; j++)
            {
                var id = displayIDs[j];
                if (SDL.GetDisplayBounds(id, out SDL.Rect bounds) != true)
                {
                    _globalSignals.EmitSignal(nameof(GlobalSignals.Log),
                        $"SDL_GetDisplayBounds failed for SDL index {j}: {SDL.GetError()}", 2);
                    continue;
                }

                Vector2I pos = new Vector2I(bounds.X, bounds.Y);
                Vector2I size = new Vector2I(bounds.W, bounds.H);

                sdlDisplays.Add((id, pos, size));
            }

            // Compare SDL displays to Godot and name
            for (int i = 0; i < screenCount; i++)
            {
                Vector2I gPos = DisplayServer.ScreenGetPosition(i);
                Vector2I gSize = DisplayServer.ScreenGetSize(i);
                int gDpi = DisplayServer.ScreenGetDpi(i);
                float gRefresh = DisplayServer.ScreenGetRefreshRate(i);

                // Find matching SDL display
                uint matchedID = 0;
                bool found = false;
                for (int k = 0; k < sdlDisplays.Count; k++)
                {
                    var sdl = sdlDisplays[k];
                    if (sdl.Position.X == (gPos.X - offsetX) && sdl.Position.Y == (gPos.Y - offsetY) && sdl.Size.X == gSize.X && sdl.Size.Y == gSize.Y)
                    {
                        matchedID = sdl.ID;
                        found = true;
                        // Remove to avoid duplicate matches
                        sdlDisplays.RemoveAt(k);
                        break;
                    }
                }

                // Try to get actual name via SDL
                string displayName = $"Display {i}";
                if (found)
                {
                    var namePtr = SDL.GetDisplayName(matchedID);
                    if (namePtr != null)
                    {
                        displayName = namePtr;
                    }
                    else
                    {
                        _globalSignals.EmitSignal(nameof(GlobalSignals.Log),
                            $"SDL_GetDisplayName failed for display {i}: {SDL.GetError()}", 1);
                    }
                }

                displays.Add(new DisplayInfo
                {
                    Index = i,
                    Name = displayName,
                    Position = gPos,
                    Size = gSize,
                    Dpi = gDpi,
                    RefreshRate = gRefresh
                });
            }
        }
        catch (Exception ex)
        {
            _globalSignals.EmitSignal(nameof(GlobalSignals.Log),
                $"Error getting available displays: {ex.Message}", 2);
        }

        _cachedDisplays = displays;
        _cachedDisplaysMs = now;
        return displays;
    }

    /// <summary>
    /// Returns the configured video outputs mapped to whether their destination is currently available.
    /// Physical: target monitor index is valid. Virtual / Window: always available.
    /// Used by the footer to display combined device status.
    /// </summary>
    /// <returns>Dictionary of "Name (destination)" → isConnected (true = green/available).</returns>
    public Dictionary<string, bool> GetVideoOutputStatuses()
    {
        var result = new Dictionary<string, bool>();
        if (Outputs == null || Outputs.Count == 0)
            return result;

        int screenCount = DisplayServer.GetScreenCount();

        foreach (var output in Outputs)
        {
            bool isConnected;
            string key;
            if (output.IsVirtual)
            {
                isConnected = true;
                key = $"{output.OutputName} (Virtual)";
            }
            else if (output.IsWindow)
            {
                isConnected = true;
                key = $"{output.OutputName} (Window)";
            }
            else
            {
                // Connected if the target monitor index is currently valid.
                isConnected = output.TargetMonitor >= 0 && output.TargetMonitor < screenCount;
                key = $"{output.OutputName} (Monitor {output.TargetMonitor})";
            }

            result[key] = isConnected;
        }

        return result;
    }

    /// <summary>
    /// Serializes the displays manager data.
    /// </summary>
    /// <returns>Dictionary containing layers and outputs data.</returns>
    public Godot.Collections.Dictionary GetData()
    {
        var data = new Godot.Collections.Dictionary();
        var canvasData = Canvas.GetData();
        data.Add("Canvas", canvasData);
        
        var layersData = new Godot.Collections.Array();
        foreach (var layer in Layers)
        {
            layersData.Add(layer.GetData());
        }
        data.Add("Layers", layersData);

        var outputsData = new Godot.Collections.Array();
        foreach (var output in Outputs)
        {
            outputsData.Add(output.GetData());
        }
        data.Add("Outputs", outputsData);


        return data;
    }

    /// <summary>
    /// Loads the displays manager data from a dictionary.
    /// </summary>
    /// <param name="data">Dictionary containing layers and outputs data.</param>
    /// <remarks>
    /// Safe for JSON-round-tripped history snapshots (Variant-wrapped nested dicts/arrays).
    /// Replaces all screens (Window nodes) and layer models. Emits
    /// <see cref="GlobalSignals.DisplaysChanged"/> and <see cref="GlobalSignals.CanvasSizeChanged"/>
    /// so the canvas editor and footer can rebuild.
    /// </remarks>
    public void LoadFromData(Godot.Collections.Dictionary data)
    {
        if (data == null)
            return;

        // Track screen test-pattern flags while loading — overlays need a parented window.
        var pendingScreenTestPatterns = new System.Collections.Generic.List<(int outputId, bool enabled)>();

        if (data.ContainsKey("Canvas"))
        {
            var canvasData = data["Canvas"].AsGodotDictionary();
            Canvas.LoadFromData(canvasData);
        }

        Layers.Clear();
        // Reset id allocator before reloading so LoadFromData can advance it cleanly.
        VideoTargetLayer.SetNextLayerId(0);

        if (data.ContainsKey("Layers"))
        {
            var layersData = data["Layers"].AsGodotArray();
            // Array order is authoritative (top-first). Do not re-sort by ZIndex — equal/stale
            // Z values produced unstable multi-layer order and broke cue TargetLayerId mapping
            // when combined with the old LayerId++ load bug.
            foreach (var layerVar in layersData)
            {
                if (layerVar.VariantType != Variant.Type.Dictionary)
                    continue;
                var layerData = layerVar.AsGodotDictionary();
                var layer = new VideoTargetLayer();
                layer.LoadFromData(layerData);
                Layers.Add(layer);
            }
        }

        if (Layers.Count == 0)
        {
            AddLayer("Default", 0);
        }
        else
        {
            NormalizeLayerOrder();
            int maxLayerId = 0;
            foreach (var layer in Layers)
            {
                if (layer.LayerId > maxLayerId)
                    maxLayerId = layer.LayerId;
            }
            VideoTargetLayer.SetNextLayerId(maxLayerId + 1);
        }

        foreach (var output in Outputs.ToList())
        {
            if (output != null && GodotObject.IsInstanceValid(output))
            {
                RemoveChild(output);
                output.QueueFree();
            }
        }
        Outputs.Clear();

        if (data.ContainsKey("Outputs"))
        {
            var outputsData = data["Outputs"].AsGodotArray();
            foreach (var outputVar in outputsData)
            {
                if (outputVar.VariantType != Variant.Type.Dictionary)
                    continue;
                var outputData = outputVar.AsGodotDictionary();
                var output = new VideoOutputDevice();
                output.LoadFromData(outputData);
                AddChild(output);
                Outputs.Add(output);
                // Virtual / missing monitors stay hidden; Window and available physical monitors show.
                // Respect master disable so undo/load does not force house screens open while disabled.
                if (!OutputDisabled)
                {
                    if (output.IsWindow)
                    {
                        output.ClearWindowDismissed();
                        output.Show();
                    }
                    else if (output.IsPhysical && output.TargetMonitor < DisplayServer.GetScreenCount())
                    {
                        output.Show();
                    }
                }
                output.SetCanvasReference(Canvas);
                output.SetTransparent(output.OutputTransparent);
                ApplyPresentationToOutput(output);

                bool testPattern = outputData.ContainsKey("TestPatternEnabled")
                    && (bool)outputData["TestPatternEnabled"];
                pendingScreenTestPatterns.Add((output.OutputId, testPattern));
            }
            // Update _nextOutputId to avoid ID conflicts
            if (Outputs.Count > 0)
            {
                int maxId = Outputs.Max(o => o.OutputId);
                VideoOutputDevice.SetNextOutputId(maxId + 1);
            }
        }

        // Always keep at least one screen after load
        EnsureDefaultScreen();

        // Re-apply screen test patterns after windows exist in the tree.
        foreach (var (outputId, enabled) in pendingScreenTestPatterns)
        {
            var output = GetOutputById(outputId);
            if (output != null && GodotObject.IsInstanceValid(output))
                output.ToggleTestPattern(enabled);
        }

        // Canvas flag was loaded with Canvas.LoadFromData — rebuild overlays on live windows.
        UpdateCanvasTestPatterns();
        UpdateAllLayerTestPatterns();
        ApplyLayerDrawOrderToOutputs();
        ApplyOutputPresentationState();

        _globalSignals?.EmitSignal(nameof(GlobalSignals.CanvasSizeChanged), Canvas.CanvasSize);
        _globalSignals?.EmitSignal(nameof(GlobalSignals.DisplaysChanged));

        // Avoid stealing focus during undo/redo restores (history IsRestoring path).
        var history = GetNodeOrNull<GlobalData>("/root/GlobalData")?.HistoryManager;
        if (history == null || !history.IsRestoring)
        {
            try
            {
                DisplayServer.WindowMoveToForeground(GetWindow().GetWindowId());
            }
            catch
            {
                // Window may not be ready during early session load.
            }
        }
    }

    public override void _ExitTree()
    {
        // Layers are children of Canvas, which is freed by GlobalData, so no need to clean them here
        foreach (var output in Outputs)
        {
            RemoveChild(output);
            output.QueueFree();
        }
        Outputs.Clear();
    }
}