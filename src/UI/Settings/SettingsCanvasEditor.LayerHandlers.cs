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
/// Partial: Layer property handlers and create/delete
/// </summary>
public partial class SettingsCanvasEditor
{
    #region Layer property handlers

    private void OnLayerNameSubmitted(string text)
    {
        if (_isUpdatingProps || _selectionKind != SelectionKind.Layer)
        {
            _layerNameLineEdit.ReleaseFocus();
            return;
        }

        var layer = DisplaysManager.GetLayerById(_selectedLayerId);
        string next = text ?? string.Empty;
        if (layer == null || next == layer.LayerName)
        {
            _layerNameLineEdit.ReleaseFocus();
            return;
        }

        RecordDisplaysHistory("Rename layer");
        _displaysManager.UpdateLayerName(_selectedLayerId, next);
        RebuildTrees();
        UpdateCanvasGizmos();
        _layerNameLineEdit.ReleaseFocus();
    }

    private void OnLayerSizeXSubmitted(string text)
    {
        if (_isUpdatingProps)
        {
            _layerSizeXLineEdit.ReleaseFocus();
            return;
        }

        var layer = DisplaysManager.GetLayerById(_selectedLayerId);
        if (layer == null)
        {
            _layerSizeXLineEdit.ReleaseFocus();
            return;
        }

        try
        {
            int val = int.Parse(text);
            Vector2I size = layer.KeepAspect
                ? SizeWithKeepAspect(layer.Size, val, null)
                : new Vector2I(val, layer.Size.Y);
            if (size == layer.Size)
            {
                _layerSizeXLineEdit.ReleaseFocus();
                return;
            }
            RecordDisplaysHistory("Change layer size");
            _displaysManager.UpdateLayerSize(_selectedLayerId, size);
            LoadLayerProps();
            UpdateCanvasGizmos();
        }
        catch (FormatException)
        {
            _layerSizeXLineEdit.Text = layer.Size.X.ToString();
        }

        _layerSizeXLineEdit.ReleaseFocus();
    }

    private void OnLayerSizeYSubmitted(string text)
    {
        if (_isUpdatingProps)
        {
            _layerSizeYLineEdit.ReleaseFocus();
            return;
        }

        var layer = DisplaysManager.GetLayerById(_selectedLayerId);
        if (layer == null)
        {
            _layerSizeYLineEdit.ReleaseFocus();
            return;
        }

        try
        {
            int val = int.Parse(text);
            Vector2I size = layer.KeepAspect
                ? SizeWithKeepAspect(layer.Size, null, val)
                : new Vector2I(layer.Size.X, val);
            if (size == layer.Size)
            {
                _layerSizeYLineEdit.ReleaseFocus();
                return;
            }
            RecordDisplaysHistory("Change layer size");
            _displaysManager.UpdateLayerSize(_selectedLayerId, size);
            LoadLayerProps();
            UpdateCanvasGizmos();
        }
        catch (FormatException)
        {
            _layerSizeYLineEdit.Text = layer.Size.Y.ToString();
        }

        _layerSizeYLineEdit.ReleaseFocus();
    }

    private void OnLayerPosXSubmitted(string text)
    {
        if (_isUpdatingProps)
        {
            _layerPosXLineEdit.ReleaseFocus();
            return;
        }

        var layer = DisplaysManager.GetLayerById(_selectedLayerId);
        if (layer == null)
        {
            _layerPosXLineEdit.ReleaseFocus();
            return;
        }

        try
        {
            int val = int.Parse(text);
            if (val == layer.CanvasPosition.X)
            {
                _layerPosXLineEdit.ReleaseFocus();
                return;
            }
            RecordDisplaysHistory("Change layer position");
            _displaysManager.UpdateLayerCanvasPosition(_selectedLayerId, new Vector2I(val, layer.CanvasPosition.Y));
            LoadLayerProps();
            UpdateCanvasGizmos();
        }
        catch (FormatException)
        {
            _layerPosXLineEdit.Text = layer.CanvasPosition.X.ToString();
        }

        _layerPosXLineEdit.ReleaseFocus();
    }

    private void OnLayerPosYSubmitted(string text)
    {
        if (_isUpdatingProps)
        {
            _layerPosYLineEdit.ReleaseFocus();
            return;
        }

        var layer = DisplaysManager.GetLayerById(_selectedLayerId);
        if (layer == null)
        {
            _layerPosYLineEdit.ReleaseFocus();
            return;
        }

        try
        {
            int val = int.Parse(text);
            if (val == layer.CanvasPosition.Y)
            {
                _layerPosYLineEdit.ReleaseFocus();
                return;
            }
            RecordDisplaysHistory("Change layer position");
            _displaysManager.UpdateLayerCanvasPosition(_selectedLayerId, new Vector2I(layer.CanvasPosition.X, val));
            LoadLayerProps();
            UpdateCanvasGizmos();
        }
        catch (FormatException)
        {
            _layerPosYLineEdit.Text = layer.CanvasPosition.Y.ToString();
        }

        _layerPosYLineEdit.ReleaseFocus();
    }

    private void OnLayerKeepAspectToggled(bool toggled)
    {
        if (_isUpdatingProps || _selectionKind != SelectionKind.Layer)
            return;

        RecordDisplaysHistory(toggled ? "Enable layer keep-aspect" : "Disable layer keep-aspect");
        _displaysManager.UpdateLayerKeepAspect(_selectedLayerId, toggled);
        var layer = DisplaysManager.GetLayerById(_selectedLayerId);
        if (layer != null)
            UpdateLayerResetButtons(layer);
    }

    private void OnLayerTransparentToggled(bool toggled)
    {
        if (_isUpdatingProps || _selectionKind != SelectionKind.Layer)
            return;

        RecordDisplaysHistory(toggled ? "Enable layer transparency" : "Disable layer transparency");
        _displaysManager.UpdateLayerTransparent(_selectedLayerId, toggled);
        var layer = DisplaysManager.GetLayerById(_selectedLayerId);
        if (layer != null)
            UpdateLayerResetButtons(layer);
    }

    private void OnLayerTestPatternToggled(bool toggled)
    {
        if (_isUpdatingProps || _selectionKind != SelectionKind.Layer)
            return;

        RecordDisplaysHistory(toggled ? "Enable layer test pattern" : "Disable layer test pattern");
        _displaysManager.ToggleLayerTestPattern(_selectedLayerId, toggled);
        var layer = DisplaysManager.GetLayerById(_selectedLayerId);
        if (layer != null)
            UpdateLayerResetButtons(layer);
    }

    private void OnLayerLockToggled(bool toggled)
    {
        if (_isUpdatingProps || _selectionKind != SelectionKind.Layer)
            return;

        RecordDisplaysHistory(toggled ? "Lock layer" : "Unlock layer");
        _displaysManager.UpdateLayerLocked(_selectedLayerId, toggled);
        var layer = DisplaysManager.GetLayerById(_selectedLayerId);
        if (layer != null)
            UpdateLayerResetButtons(layer);
    }

    private void OnDeleteLayerPressed()
    {
        if (_selectionKind != SelectionKind.Layer)
            return;
        if (_historyManager?.IsRestoring == true)
            return;
        if (_activeLayerDeleteDialog != null && GodotObject.IsInstanceValid(_activeLayerDeleteDialog))
            return;

        int layerId = _selectedLayerId;
        var layer = DisplaysManager.GetLayerById(layerId);
        if (layer == null)
            return;

        string layerName = layer.LayerName ?? $"Layer {layerId}";
        var usage = CueResourceUsage.FindCuesUsingTargetLayer(layerId);

        if (usage.Count == 0)
        {
            PerformLayerDelete(layerId, reassign: null);
            return;
        }

        var alternatives = DisplaysManager.Layers
            .Where(l => l != null && l.LayerId != layerId)
            .OrderBy(l => l.LayerName ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .Select(l => (l.LayerId, l.LayerName ?? $"Layer {l.LayerId}"))
            .ToList();

        // Same flow as FileDropPopup: Create → Configure → AddChild → ShowConfigured
        var dialog = ResourceInUseDeleteDialog.Create(out string loadErr);
        if (dialog == null)
        {
            _globalSignals?.EmitSignal(nameof(GlobalSignals.Log),
                $"Failed to open delete dialog: {loadErr}", 2);
            return;
        }

        _activeLayerDeleteDialog = dialog;
        dialog.Configure("target layer", layerName, usage.Cues, alternatives);
        dialog.Confirmed += result => OnLayerDeleteDialogConfirmed(layerId, result);
        dialog.Cancelled += () =>
        {
            if (_activeLayerDeleteDialog == dialog) _activeLayerDeleteDialog = null;
        };
        dialog.TreeExiting += () =>
        {
            if (_activeLayerDeleteDialog == dialog) _activeLayerDeleteDialog = null;
        };

        GetTree()?.Root?.AddChild(dialog);
        dialog.ShowConfigured();
    }

    private void OnLayerDeleteDialogConfirmed(int layerId, ResourceInUseDeleteResult result)
    {
        if (_activeLayerDeleteDialog != null)
            _activeLayerDeleteDialog = null;

        if (result == null || result.Action == ResourceInUseDeleteAction.Cancel)
            return;

        var usingCues = CueResourceUsage.FindCuesUsingTargetLayer(layerId).Cues;
        Action reassign = null;

        if (result.Action == ResourceInUseDeleteAction.Unassign)
        {
            reassign = () => CueResourceUsage.UnassignTargetLayer(usingCues, layerId);
        }
        else if (result.Action == ResourceInUseDeleteAction.Replace)
        {
            if (DisplaysManager.GetLayerById(result.ReplaceWithId) == null)
            {
                _globalSignals?.EmitSignal(nameof(GlobalSignals.Log),
                    $"Cannot replace layer: target id {result.ReplaceWithId} not found.", 2);
                return;
            }
            reassign = () => CueResourceUsage.ReplaceTargetLayer(usingCues, layerId, result.ReplaceWithId);
        }

        PerformLayerDelete(layerId, reassign);
    }

    /// <summary>
    /// Records history, optionally reassigns cues, removes the layer, and refreshes the canvas UI.
    /// </summary>
    private void PerformLayerDelete(int layerId, Action reassign)
    {
        RecordDisplaysHistory("Delete layer");
        if (reassign != null)
        {
            _historyManager?.RecordCuelistChange("Reassign cues after layer delete");
            reassign.Invoke();
        }

        _displaysManager.RemoveLayer(layerId);
        GetNodeOrNull<MediaHealthService>("/root/MediaHealthService")?.RecheckAllQuiet();
        _globalSignals?.EmitSignal(nameof(GlobalSignals.SyncShellInspector));
        RebuildTrees(selectCanvas: true);
        UpdateCanvasGizmos();
    }

    private void OnNewTargetLayerPressed()
    {
        RecordDisplaysHistory("Create target layer");
        string name = $"Layer {DisplaysManager.Layers.Count + 1}";
        int zIndex = DisplaysManager.Layers.Count;
        var layer = _displaysManager.AddLayer(name, zIndex);
        RebuildTrees();
        SelectLayerInTree(layer.LayerId);
        ApplySelection(SelectionKind.Layer, -1, layer.LayerId);
        UpdateCanvasGizmos();
        _globalSignals.EmitSignal(nameof(GlobalSignals.Log), $"Added new target layer '{name}'.", 0);
    }

    #endregion

}
