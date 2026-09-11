// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using Godot;
using System;
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
using Cue2.Services;
using Cue2.UI.Popups;

namespace Cue2.UI.Settings;

/// <summary>
/// Canvas editor UI for arranging screens and target layers on the video canvas.
/// Left: Screens + Target Layers trees. Center: interactive stage (move/resize). Right: properties.
/// </summary>
/// <summary>
/// Partial: Screens/layers trees and selection
/// </summary>
public partial class SettingsCanvasEditor
{
    #region Trees

    private void RebuildTrees(bool selectCanvas = false)
    {
        if (_isDraggingCanvas)
            return;

        _isRebuildingTree = true;
        try
        {
            var prevKind = _selectionKind;
            var prevScreenId = _selectedScreenId;
            var prevLayerId = _selectedLayerId;

            RebuildScreensTree();
            RebuildLayersTree();

            if (selectCanvas)
            {
                DeselectTrees();
                ApplySelection(SelectionKind.Canvas, -1, -1);
            }
            else if (prevKind == SelectionKind.Screen && prevScreenId >= 0)
            {
                SelectScreenInTree(prevScreenId);
                ApplySelection(SelectionKind.Screen, prevScreenId, -1);
            }
            else if (prevKind == SelectionKind.Layer && prevLayerId >= 0)
            {
                SelectLayerInTree(prevLayerId);
                ApplySelection(SelectionKind.Layer, -1, prevLayerId);
            }
            else if (prevKind == SelectionKind.Canvas)
            {
                ApplySelection(SelectionKind.Canvas, -1, -1);
            }
            else
            {
                ApplySelection(SelectionKind.None, -1, -1);
            }
        }
        finally
        {
            _isRebuildingTree = false;
        }
    }

    private void RebuildScreensTree()
    {
        // Diff-update by OutputId so selection/scroll survive rename and geometry-only changes (P2-08).
        if (_screensTree == null)
            return;

        var root = _screensTree.GetRoot() ?? _screensTree.CreateItem();
        root.SetText(0, "Screens");

        var screens = DisplaysManager.Screens;
        var wantIds = new HashSet<int>();
        foreach (var screen in screens)
            wantIds.Add(screen.OutputId);

        // Map existing children by metadata id
        var existing = new Dictionary<int, TreeItem>();
        var orphan = new List<TreeItem>();
        for (var child = root.GetFirstChild(); child != null; child = child.GetNext())
        {
            int id = child.GetMetadata(0).AsInt32();
            if (wantIds.Contains(id) && !existing.ContainsKey(id))
                existing[id] = child;
            else
                orphan.Add(child);
        }

        foreach (var item in orphan)
            item.Free();

        // Update / create in screen order (reparent by recreating order via MoveBelow)
        TreeItem prev = null;
        foreach (var screen in screens)
        {
            if (!existing.TryGetValue(screen.OutputId, out var item))
            {
                item = _screensTree.CreateItem(root);
                item.SetMetadata(0, screen.OutputId);
                existing[screen.OutputId] = item;
            }

            string dest = GetScreenDestinationShortLabel(screen);
            item.SetText(0, $"{screen.OutputName}  [{dest}]");
            item.SetTooltipText(0,
                $"{screen.OutputName}\n{screen.OutputSize.X}×{screen.OutputSize.Y} @ {screen.CanvasPosition}\nOutput: {dest}");
            item.SetCustomColor(0, GetScreenTreeColor(screen));

            // Keep visual order matching DisplaysManager.Screens
            if (prev == null)
            {
                var first = root.GetFirstChild();
                if (first != null && first != item)
                    item.MoveBefore(first);
            }
            else if (prev.GetNext() != item)
            {
                item.MoveAfter(prev);
            }
            prev = item;
        }
    }

    private void RebuildLayersTree()
    {
        // Diff-update by LayerId (P2-08).
        if (_layersTree == null)
            return;

        var root = _layersTree.GetRoot() ?? _layersTree.CreateItem();
        root.SetText(0, "Layers");

        var layers = DisplaysManager.Layers;
        var wantIds = new HashSet<int>();
        foreach (var layer in layers)
            wantIds.Add(layer.LayerId);

        var existing = new Dictionary<int, TreeItem>();
        var orphan = new List<TreeItem>();
        for (var child = root.GetFirstChild(); child != null; child = child.GetNext())
        {
            int id = child.GetMetadata(0).AsInt32();
            if (wantIds.Contains(id) && !existing.ContainsKey(id))
                existing[id] = child;
            else
                orphan.Add(child);
        }

        foreach (var item in orphan)
            item.Free();

        int count = layers.Count;
        TreeItem prev = null;
        for (int i = 0; i < count; i++)
        {
            var layer = layers[i];
            if (!existing.TryGetValue(layer.LayerId, out var item))
            {
                item = _layersTree.CreateItem(root);
                item.SetMetadata(0, layer.LayerId);
                existing[layer.LayerId] = item;
            }

            string stackLabel = i == 0 ? "top" : (i == count - 1 ? "bottom" : $"#{i + 1}");
            item.SetText(0, $"{layer.LayerName}  [{stackLabel}]");
            item.SetTooltipText(0,
                $"{layer.LayerName}\nStack: {stackLabel} (first = on top)\n{layer.Size.X}×{layer.Size.Y} @ {layer.CanvasPosition}");
            item.SetCustomColor(0, new Color(0.55f, 0.75f, 1f));

            if (prev == null)
            {
                var first = root.GetFirstChild();
                if (first != null && first != item)
                    item.MoveBefore(first);
            }
            else if (prev.GetNext() != item)
            {
                item.MoveAfter(prev);
            }
            prev = item;
        }

        UpdateLayerOrderButtons();
    }

    /// <summary>
    /// Enables ↑/↓ based on the selected layer's place in the top-first stack.
    /// </summary>
    private void UpdateLayerOrderButtons()
    {
        if (_moveLayerUpButton == null || _moveLayerDownButton == null)
            return;

        if (_selectionKind != SelectionKind.Layer || _selectedLayerId < 0)
        {
            _moveLayerUpButton.Disabled = true;
            _moveLayerDownButton.Disabled = true;
            return;
        }

        int index = _displaysManager.GetLayerStackIndex(_selectedLayerId);
        int count = DisplaysManager.Layers.Count;
        _moveLayerUpButton.Disabled = index <= 0;
        _moveLayerDownButton.Disabled = index < 0 || index >= count - 1;
    }

    private void OnMoveLayerUpPressed()
    {
        if (_selectionKind != SelectionKind.Layer)
            return;
        if (_historyManager != null && _historyManager.IsRestoring)
            return;
        // Only snapshot when the layer can actually move (avoid no-op undo steps).
        if (_displaysManager.GetLayerStackIndex(_selectedLayerId) <= 0)
            return;
        RecordDisplaysHistory("Move layer up");
        if (_displaysManager.MoveLayerUp(_selectedLayerId))
        {
            RebuildTrees();
            UpdateCanvasGizmos();
        }
    }

    private void OnMoveLayerDownPressed()
    {
        if (_selectionKind != SelectionKind.Layer)
            return;
        if (_historyManager != null && _historyManager.IsRestoring)
            return;
        int index = _displaysManager.GetLayerStackIndex(_selectedLayerId);
        int count = DisplaysManager.Layers.Count;
        if (index < 0 || index >= count - 1)
            return;
        RecordDisplaysHistory("Move layer down");
        if (_displaysManager.MoveLayerDown(_selectedLayerId))
        {
            RebuildTrees();
            UpdateCanvasGizmos();
        }
    }

    private string GetMonitorLabel(int monitorIndex)
    {
        if (monitorIndex == VideoOutputDevice.VirtualMonitorIndex)
            return "Virtual";
        if (monitorIndex == VideoOutputDevice.WindowMonitorIndex)
            return "Window";

        var displays = _displaysManager.GetAvailableDisplays();
        foreach (var d in displays)
        {
            if (d.Index == monitorIndex)
                return d.Name;
        }

        return $"Monitor {monitorIndex} (missing)";
    }

    /// <summary>
    /// Short destination label for screen tree rows and tooltips.
    /// </summary>
    private string GetScreenDestinationShortLabel(VideoOutputDevice screen)
    {
        if (screen == null)
            return "Unknown";
        if (screen.IsVirtual)
            return "Virtual";
        if (screen.IsWindow)
            return "Window";
        return GetMonitorLabel(screen.TargetMonitor);
    }

    private static Color GetScreenTreeColor(VideoOutputDevice screen)
    {
        if (screen == null)
            return new Color(1f, 0.55f, 0.45f);
        if (screen.IsVirtual)
            return new Color(0.75f, 0.55f, 0.45f);
        if (screen.IsWindow)
            return new Color(0.55f, 0.8f, 0.55f);
        return new Color(1f, 0.55f, 0.45f);
    }

    private void DeselectTrees()
    {
        _screensTree.DeselectAll();
        _layersTree.DeselectAll();
    }

    private void SelectScreenInTree(int screenId)
    {
        _layersTree.DeselectAll();
        var root = _screensTree.GetRoot();
        if (root == null)
            return;

        var child = root.GetFirstChild();
        while (child != null)
        {
            if (child.GetMetadata(0).AsInt32() == screenId)
            {
                child.Select(0);
                return;
            }
            child = child.GetNext();
        }
    }

    private void SelectLayerInTree(int layerId)
    {
        _screensTree.DeselectAll();
        var root = _layersTree.GetRoot();
        if (root == null)
            return;

        var child = root.GetFirstChild();
        while (child != null)
        {
            if (child.GetMetadata(0).AsInt32() == layerId)
            {
                child.Select(0);
                return;
            }
            child = child.GetNext();
        }
    }

    private void OnCanvasSelectPressed()
    {
        DeselectTrees();
        ApplySelection(SelectionKind.Canvas, -1, -1);
    }

    private void OnScreensTreeItemSelected()
    {
        if (_isRebuildingTree)
            return;

        var item = _screensTree.GetSelected();
        if (item == null || item == _screensTree.GetRoot())
            return;

        _layersTree.DeselectAll();
        int screenId = item.GetMetadata(0).AsInt32();
        ApplySelection(SelectionKind.Screen, screenId, -1);
    }

    private void OnLayersTreeItemSelected()
    {
        if (_isRebuildingTree)
            return;

        var item = _layersTree.GetSelected();
        if (item == null || item == _layersTree.GetRoot())
            return;

        _screensTree.DeselectAll();
        int layerId = item.GetMetadata(0).AsInt32();
        ApplySelection(SelectionKind.Layer, -1, layerId);
    }

    private void ApplySelection(SelectionKind kind, int screenId, int layerId)
    {
        _selectionKind = kind;
        _selectedScreenId = screenId;
        _selectedLayerId = layerId;
        ShowPropertiesForSelection();
        UpdateLayerOrderButtons();
        UpdateCanvasGizmos();
    }

    #endregion

}
