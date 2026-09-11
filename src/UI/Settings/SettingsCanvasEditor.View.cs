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
/// Partial: Canvas size/zoom/fit, gizmos, history refresh
/// </summary>
public partial class SettingsCanvasEditor
{
    #region Canvas size / zoom / fit

    private void OnCanvasSizeSubmitted(string newText)
    {
        try
        {
            int x = int.Parse(_canvasSizeXLineEdit.Text);
            int y = int.Parse(_canvasSizeYLineEdit.Text);

            if (x == _canvas.CanvasSize.X && y == _canvas.CanvasSize.Y)
                return;

            RecordDisplaysHistory("Change canvas size");
            _canvas.SetCanvasSize(new Vector2I(x, y));

            _canvasOutlinePanel.CustomMinimumSize = new Vector2(_canvas.CanvasSize.X, _canvas.CanvasSize.Y);

            UpdateZoom();

            _canvasSizeXLineEdit.Text = _canvas.CanvasSize.X.ToString();
            _canvasSizeYLineEdit.Text = _canvas.CanvasSize.Y.ToString();

            // Canvas size changes re-clip screens — keep canvas TP geometry aligned.
            _displaysManager?.UpdateCanvasTestPatterns();

            RefreshCanvasSelectButtonText();
            UpdateCanvasGizmos();

            _globalSignals.EmitSignal(nameof(GlobalSignals.CanvasSizeChanged), _canvas.CanvasSize);
            _globalSignals.EmitSignal(nameof(GlobalSignals.Log),
                $"Canvas size submitted and updated to {x}x{y}.", 0);
        }
        catch (FormatException)
        {
            _globalSignals.EmitSignal(nameof(GlobalSignals.Log),
                "Invalid canvas size input: Must be integers.", 2);
        }
        catch (Exception ex)
        {
            _globalSignals.EmitSignal(nameof(GlobalSignals.Log),
                $"Error updating canvas size: {ex.Message}", 2);
        }
    }

    private void ZoomIn()
    {
        float increment = _zoom * 0.1f;
        _zoom = Mathf.Clamp(_zoom + increment, MinZoom, MaxZoom);
        UpdateZoom();
    }

    private void ZoomOut()
    {
        float increment = _zoom * 0.1f;
        _zoom = Mathf.Clamp(_zoom - increment, MinZoom, MaxZoom);
        UpdateZoom();
    }

    /// <summary>
    /// Fits the full canvas into the center stage view with padding and centers it.
    /// </summary>
    private void FitToView()
    {
        Vector2 viewSize = _scrollContainer.Size;
        if (viewSize.X < 8f || viewSize.Y < 8f || _canvas.CanvasSize.X <= 0 || _canvas.CanvasSize.Y <= 0)
            return;

        float availX = Mathf.Max(8f, viewSize.X - FitPadding);
        float availY = Mathf.Max(8f, viewSize.Y - FitPadding);
        float zoomX = availX / _canvas.CanvasSize.X;
        float zoomY = availY / _canvas.CanvasSize.Y;
        _zoom = Mathf.Clamp(Mathf.Min(zoomX, zoomY), MinZoom, MaxZoom);

        Vector2 zoomed = new Vector2(_canvas.CanvasSize.X * _zoom, _canvas.CanvasSize.Y * _zoom);
        _canvasLayer.Offset = (viewSize - zoomed) * 0.5f;
        UpdateZoom();
    }

    private void UpdateZoom()
    {
        if (_scrollContainer == null || _subViewportContainer == null || _viewport == null)
            return;

        Vector2 viewportSize = _scrollContainer.Size;
        // Never impose a min size on the stage host — that ratchets the center panel
        // wider when the window grows and then blocks it from shrinking.
        _subViewportContainer.CustomMinimumSize = Vector2.Zero;
        if (_stagePointer?.GetParent() is Control stageHost)
            stageHost.CustomMinimumSize = Vector2.Zero;

        if (viewportSize.X < 8f || viewportSize.Y < 8f)
            return;

        Vector2 zoomedSize = new Vector2(_canvas.CanvasSize.X * _zoom, _canvas.CanvasSize.Y * _zoom);
        _control.Size = zoomedSize;
        _control.Position = Vector2.Zero;
        // SubViewport size is owned by SubViewportContainer.Stretch — do not set it here.
        if (_backgroundRect != null)
        {
            _backgroundRect.Size = viewportSize;
            (_backgroundRect.Material as ShaderMaterial)?.SetShaderParameter("rect_size", viewportSize);
        }
        if (_canvasOutlinePanel != null)
            _canvasOutlinePanel.CustomMinimumSize = zoomedSize;
        UpdateZoomLabel();
        UpdateCanvasGizmos();
    }

    private void UpdateZoomLabel()
    {
        _zoomPercentLineEdit.Text = $"{_zoom * 100:F0}";
    }

    private void OnZoomPercentSubmitted(string newText)
    {
        try
        {
            float percent = float.Parse(newText);
            _zoom = Mathf.Clamp(percent / 100f, MinZoom, MaxZoom);
            UpdateZoom();
        }
        catch
        {
            UpdateZoomLabel();
        }

        _zoomPercentLineEdit.ReleaseFocus();
    }

    #endregion

    #region Gizmos / refresh

    /// <summary>
    /// Rebuilds stage gizmos for all screens and layers, highlighting the selection with handles.
    /// </summary>
    private void UpdateCanvasGizmos()
    {
        foreach (var g in _gizmos)
        {
            if (IsInstanceValid(g))
            {
                _canvasLayer.RemoveChild(g);
                g.QueueFree();
            }
        }

        _gizmos.Clear();

        // Screens under layers so layers draw on top
        foreach (var screen in DisplaysManager.Screens)
        {
            bool selected = _selectionKind == SelectionKind.Screen && screen.OutputId == _selectedScreenId;
            Color border;
            Color fill;
            if (screen.IsVirtual)
            {
                border = new Color(1f, 0.5f, 0.2f, 0.85f);
                fill = new Color(1f, 0.45f, 0.15f, 0.1f);
            }
            else if (screen.IsWindow)
            {
                border = new Color(0.35f, 0.85f, 0.45f, 0.9f);
                fill = new Color(0.25f, 0.75f, 0.35f, 0.1f);
            }
            else
            {
                border = new Color(1f, 0.2f, 0.15f, 0.9f);
                fill = new Color(1f, 0.15f, 0.1f, 0.12f);
            }

            var gizmo = new CanvasItemGizmo
            {
                IsScreen = true,
                ItemId = screen.OutputId,
                LabelText = screen.OutputName,
                BorderColor = border,
                FillColor = fill,
                OffsetDash = false,
                Selected = selected,
                MouseFilter = MouseFilterEnum.Ignore
            };
            gizmo.Position = new Vector2(screen.CanvasPosition.X * _zoom, screen.CanvasPosition.Y * _zoom);
            gizmo.Size = new Vector2(
                Mathf.Max(1f, screen.OutputSize.X * _zoom),
                Mathf.Max(1f, screen.OutputSize.Y * _zoom));
            _canvasLayer.AddChild(gizmo);
            gizmo.QueueRedraw();
            _gizmos.Add(gizmo);
        }

        // Draw bottom-of-stack first so top layer gizmos appear above.
        for (int i = DisplaysManager.Layers.Count - 1; i >= 0; i--)
        {
            var layer = DisplaysManager.Layers[i];
            bool selected = _selectionKind == SelectionKind.Layer && layer.LayerId == _selectedLayerId;
            var gizmo = new CanvasItemGizmo
            {
                IsScreen = false,
                ItemId = layer.LayerId,
                LabelText = layer.LayerName,
                BorderColor = new Color(0.25f, 0.55f, 1f, 0.9f),
                FillColor = new Color(0.2f, 0.45f, 1f, 0.1f),
                OffsetDash = true,
                Selected = selected,
                MouseFilter = MouseFilterEnum.Ignore,
                ZIndex = layer.ZIndex
            };
            gizmo.Position = new Vector2(layer.CanvasPosition.X * _zoom, layer.CanvasPosition.Y * _zoom);
            gizmo.Size = new Vector2(
                Mathf.Max(1f, layer.Size.X * _zoom),
                Mathf.Max(1f, layer.Size.Y * _zoom));
            _canvasLayer.AddChild(gizmo);
            gizmo.QueueRedraw();
            _gizmos.Add(gizmo);
        }

        ForceStageRedraw();
    }

    private bool _cleanedUp;

    /// <summary>
    /// Disconnects process-lifetime signals and window listeners so Settings can free on close/exit.
    /// Idempotent — called from TreeExiting and <see cref="_ExitTree"/>.
    /// </summary>
    private void Cleanup()
    {
        if (_cleanedUp)
            return;
        _cleanedUp = true;

        TreeExiting -= Cleanup;

        var hostWindow = GetWindow();
        if (hostWindow != null && GodotObject.IsInstanceValid(hostWindow))
            hostWindow.SizeChanged -= OnWindowSizeChanged;

        VisibilityChanged -= OnEditorVisibilityChanged;
        if (_scrollContainer != null && IsInstanceValid(_scrollContainer))
            _scrollContainer.Resized -= OnStageResized;
        if (_stagePointer != null && IsInstanceValid(_stagePointer))
        {
            _stagePointer.GuiInput -= OnStageGuiInput;
            _stagePointer.MouseExited -= OnStageMouseExited;
        }
        if (_bodyHSplit != null && IsInstanceValid(_bodyHSplit))
            _bodyHSplit.Resized -= OnBodyHSplitResized;
        if (_historyManager != null)
            _historyManager.HistoryRestored -= OnHistoryRestored;

        if (_globalSignals != null && GodotObject.IsInstanceValid(_globalSignals))
        {
            if (_displaysChangedCallable.Target != null
                && _globalSignals.IsConnected(nameof(GlobalSignals.DisplaysChanged), _displaysChangedCallable))
            {
                _globalSignals.Disconnect(nameof(GlobalSignals.DisplaysChanged), _displaysChangedCallable);
            }

            if (_canvasSizeChangedCallable.Target != null
                && _globalSignals.IsConnected(nameof(GlobalSignals.CanvasSizeChanged), _canvasSizeChangedCallable))
            {
                _globalSignals.Disconnect(nameof(GlobalSignals.CanvasSizeChanged), _canvasSizeChangedCallable);
            }

            if (_layerGeometryChangedCallable.Target != null
                && _globalSignals.IsConnected(nameof(GlobalSignals.LayerGeometryChanged), _layerGeometryChangedCallable))
            {
                _globalSignals.Disconnect(nameof(GlobalSignals.LayerGeometryChanged), _layerGeometryChangedCallable);
            }
        }

        // Drop any open layer-delete popup so it does not outlive Settings.
        if (_activeLayerDeleteDialog != null && GodotObject.IsInstanceValid(_activeLayerDeleteDialog))
        {
            _activeLayerDeleteDialog.QueueFree();
            _activeLayerDeleteDialog = null;
        }
    }

    /// <summary>
    /// Records a full Displays snapshot (canvas + screens + layers) before a user mutation.
    /// </summary>
    private void RecordDisplaysHistory(string description, string coalesceKey = null)
    {
        if (_historyManager == null || _historyManager.IsRestoring)
            return;
        _historyManager.RecordSettingsChange(description, coalesceKey, "Displays");
    }

    /// <summary>
    /// After settings undo/redo that reloads Displays, rebuild trees/gizmos/props from the new model.
    /// Output windows are recreated by <see cref="DisplaysManager.LoadFromData"/> — never keep
    /// stale screen references in selection beyond IDs.
    /// </summary>
    private void OnHistoryRestored(int scope)
    {
        if (scope != (int)HistoryManager.HistoryScope.Settings)
            return;
        if (!IsInstanceValid(this))
            return;

        // Drop any in-progress drag against a model that was just replaced.
        _isDraggingCanvas = false;
        _dragMode = DragMode.None;
        if (!string.IsNullOrEmpty(_activeDragCoalesceKey))
        {
            _historyManager?.EndCoalesceSession(_activeDragCoalesceKey);
            _activeDragCoalesceKey = null;
        }

        // Canvas instance is stable; size may have changed.
        _canvas = DisplaysManager.Canvas;

        if (!_stageInitialized || !IsVisibleInTree())
        {
            // Stage not ready / not shown — refresh when the user opens this panel again.
            _needsHistoryRefresh = true;
            return;
        }

        _needsHistoryRefresh = false;
        RefreshAfterHistoryRestore();
    }

    /// <summary>
    /// Full UI sync after Displays history restore (or when stage becomes visible after a restore).
    /// </summary>
    private void RefreshAfterHistoryRestore()
    {
        if (!IsInstanceValid(this) || _canvas == null)
            return;

        // Drop selection if the screen/layer no longer exists after restore.
        if (_selectionKind == SelectionKind.Screen
            && _displaysManager.GetOutputById(_selectedScreenId) == null)
        {
            _selectionKind = SelectionKind.Canvas;
            _selectedScreenId = -1;
            _selectedLayerId = -1;
        }
        else if (_selectionKind == SelectionKind.Layer
                 && DisplaysManager.GetLayerById(_selectedLayerId) == null)
        {
            _selectionKind = SelectionKind.Canvas;
            _selectedScreenId = -1;
            _selectedLayerId = -1;
        }

        Vector2I size = _canvas.CanvasSize;
        if (_canvasOutlinePanel != null && IsInstanceValid(_canvasOutlinePanel))
            _canvasOutlinePanel.CustomMinimumSize = new Vector2(size.X, size.Y);
        if (_canvasSelectButton != null && IsInstanceValid(_canvasSelectButton))
            RefreshCanvasSelectButtonText();

        _isUpdatingProps = true;
        try
        {
            if (_canvasSizeXLineEdit != null)
                _canvasSizeXLineEdit.Text = size.X.ToString();
            if (_canvasSizeYLineEdit != null)
                _canvasSizeYLineEdit.Text = size.Y.ToString();
        }
        finally
        {
            _isUpdatingProps = false;
        }

        RebuildTrees();
        UpdateCanvasGizmos();
        ShowPropertiesForSelection();
        CallDeferred(nameof(RefreshStageView));
    }

    private void OnDisplaysChanged()
    {
        // Skip mid-history restore — OnHistoryRestored performs a coordinated full refresh.
        if (_historyManager != null && _historyManager.IsRestoring)
            return;

        // Canvas instance is stable; rebind size after ResetToDefaults / load.
        _canvas = DisplaysManager.Canvas;

        // Skip UI work while canvas editor is not shown — mark dirty for next open.
        if (!_stageInitialized || !IsVisibleInTree())
        {
            _needsHistoryRefresh = true;
            return;
        }

        if (_isDraggingCanvas)
        {
            UpdateCanvasGizmos();
            return;
        }

        if (!_isRebuildingTree)
            RebuildTrees();
        UpdateCanvasGizmos();
        // Keep canvas size labels in sync (New Session / load)
        if (_canvas != null)
            OnCanvasSizeChanged(_canvas.CanvasSize);
    }

    /// <summary>
    /// Lightweight follow of live layer geometry (e.g. Translate Layer control while this editor is open).
    /// Avoids a full tree rebuild every animation frame.
    /// </summary>
    /// <param name="layerId">Layer that changed size and/or canvas position.</param>
    private void OnLayerGeometryChanged(int layerId)
    {
        if (_historyManager != null && _historyManager.IsRestoring)
            return;

        // Not shown — no stage work; next open rebuilds from model via DisplaysChanged / dirty flag.
        if (!_stageInitialized || !IsVisibleInTree())
            return;

        // User is dragging on stage — don't fight the gizmo with external updates mid-gesture.
        if (_isDraggingCanvas)
            return;

        UpdateCanvasGizmos();

        // Keep the right-hand property fields live when this layer is selected.
        if (_selectionKind == SelectionKind.Layer && _selectedLayerId == layerId)
            LoadLayerProps();
    }

    private void OnCanvasSizeChanged(Vector2I newSize)
    {
        if (_historyManager != null && _historyManager.IsRestoring)
            return;

        if (_canvasSizeXLineEdit != null)
            _canvasSizeXLineEdit.Text = newSize.X.ToString();
        if (_canvasSizeYLineEdit != null)
            _canvasSizeYLineEdit.Text = newSize.Y.ToString();
        if (_canvasSelectButton != null)
            RefreshCanvasSelectButtonText();
    }

    #endregion
}
