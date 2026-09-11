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
/// Partial: Stage input: move/resize/select, drag, hit-test
/// </summary>
public partial class SettingsCanvasEditor
{
    #region Stage input (move / resize / select)

    /// <summary>
    /// Stage-local clicks, wheel zoom, and hover. Uses the pointer overlay so Linux
    /// content-scale / native Settings windows cannot miss the middle of the stage.
    /// </summary>
    /// <param name="event">GUI mouse event in overlay-local coordinates.</param>
    private void OnStageGuiInput(InputEvent @event)
    {
        if (!IsVisibleInTree() || !_stageInitialized)
            return;

        if (@event is InputEventMouse mouse)
        {
            _lastStageLocalMouse = mouse.Position;
            _hasStageLocalMouse = true;
        }

        if (@event is InputEventMouseButton mouseEvent)
        {
            if (mouseEvent.ButtonIndex == MouseButton.Middle)
            {
                _isPanning = mouseEvent.Pressed;
                UpdateStageInputProcessing();
                AcceptEvent();
                return;
            }

            if (mouseEvent.ButtonIndex == MouseButton.Left)
            {
                if (mouseEvent.Pressed)
                {
                    if (BeginCanvasInteraction())
                    {
                        UpdateStageInputProcessing();
                        AcceptEvent();
                    }
                }
                else if (_isDraggingCanvas)
                {
                    EndCanvasInteraction();
                    UpdateStageInputProcessing();
                    AcceptEvent();
                }

                return;
            }

            if (mouseEvent.ButtonIndex == MouseButton.WheelUp && Input.IsKeyPressed(Key.Ctrl))
            {
                ZoomIn();
                AcceptEvent();
            }
            else if (mouseEvent.ButtonIndex == MouseButton.WheelDown && Input.IsKeyPressed(Key.Ctrl))
            {
                ZoomOut();
                AcceptEvent();
            }
        }
        else if (@event is InputEventMouseMotion motionEvent)
        {
            if (_isPanning)
            {
                _canvasLayer.Offset += motionEvent.Relative;
                AcceptEvent();
            }
            else if (_isDraggingCanvas && _dragMode != DragMode.None)
            {
                UpdateCanvasDrag();
                AcceptEvent();
            }
            else
            {
                UpdateStageCursor();
            }
        }
    }

    /// <summary>
    /// Continues a drag/pan after the cursor leaves the overlay, and completes on button-up.
    /// Not used to start interactions — that is <see cref="OnStageGuiInput"/> only.
    /// </summary>
    public override void _Input(InputEvent @event)
    {
        if (!IsVisibleInTree() || !_stageInitialized)
            return;
        if (!_isDraggingCanvas && !_isPanning)
            return;

        if (@event is InputEventMouseButton mouseEvent && !mouseEvent.Pressed)
        {
            if (mouseEvent.ButtonIndex == MouseButton.Left && _isDraggingCanvas)
            {
                EndCanvasInteraction();
                UpdateStageInputProcessing();
                GetViewport().SetInputAsHandled();
            }
            else if (mouseEvent.ButtonIndex == MouseButton.Middle && _isPanning)
            {
                _isPanning = false;
                UpdateStageInputProcessing();
                GetViewport().SetInputAsHandled();
            }

            return;
        }

        if (@event is InputEventMouseMotion motionEvent)
        {
            RememberStageMouseFromEvent(motionEvent);
            if (_isPanning)
            {
                _canvasLayer.Offset += motionEvent.Relative;
                GetViewport().SetInputAsHandled();
            }
            else if (_isDraggingCanvas && _dragMode != DragMode.None)
            {
                UpdateCanvasDrag();
                GetViewport().SetInputAsHandled();
            }
        }
    }

    /// <summary>
    /// Enables viewport <c>_Input</c> only while a gesture can leave the overlay.
    /// </summary>
    private void UpdateStageInputProcessing()
    {
        SetProcessInput(IsVisibleInTree() && (_isDraggingCanvas || _isPanning));
    }

    private void OnStageMouseExited()
    {
        if (_isDraggingCanvas || _isPanning)
            return;
        MouseDefaultCursorShape = CursorShape.Arrow;
        if (_stagePointer != null && IsInstanceValid(_stagePointer))
            _stagePointer.MouseDefaultCursorShape = CursorShape.Arrow;
    }

    /// <summary>
    /// True when the pointer is over the center stage, in the same canvas space as
    /// <see cref="Control.GetGlobalRect"/> (not raw <see cref="Viewport.GetMousePosition"/>).
    /// </summary>
    private bool IsMouseOverStage()
    {
        if (!IsVisibleInTree())
            return false;
        Control host = StageMouseHost;
        if (host == null || !IsInstanceValid(host) || !host.IsVisibleInTree())
            return false;
        return host.GetGlobalRect().HasPoint(host.GetGlobalMousePosition());
    }

    /// <summary>Control whose local space matches gizmo / canvas-layer pixels.</summary>
    private Control StageMouseHost
    {
        get
        {
            if (_stagePointer != null && IsInstanceValid(_stagePointer))
                return _stagePointer;
            if (_subViewportContainer != null && IsInstanceValid(_subViewportContainer))
                return _subViewportContainer;
            return _scrollContainer;
        }
    }

    /// <summary>
    /// Stores overlay-local mouse from a viewport event (used while dragging outside the stage).
    /// </summary>
    /// <param name="mouse">Mouse event from <see cref="_Input"/>.</param>
    private void RememberStageMouseFromEvent(InputEventMouse mouse)
    {
        Control host = StageMouseHost;
        if (host == null || mouse == null)
            return;
        _lastStageLocalMouse = host.GetGlobalTransformWithCanvas().AffineInverse() * mouse.GlobalPosition;
        _hasStageLocalMouse = true;
    }

    /// <summary>Stage-local pixel position (overlay / SubViewportContainer space).</summary>
    private Vector2 GetStageLocalMouse()
    {
        Control host = StageMouseHost;
        if (host == null)
            return Vector2.Zero;
        if (_hasStageLocalMouse)
            return _lastStageLocalMouse;
        return host.GetLocalMousePosition();
    }

    /// <summary>
    /// Mouse position in canvas units (not zoomed).
    /// </summary>
    private Vector2 GetCanvasMousePosition()
    {
        Vector2 local = GetStageLocalMouse();
        Vector2 inLayer = local - (_canvasLayer?.Offset ?? Vector2.Zero);
        if (_zoom <= 0.0001f)
            return Vector2.Zero;
        return inLayer / _zoom;
    }

    /// <summary>
    /// Mouse position in zoomed layer pixels (matches gizmo coordinates).
    /// </summary>
    private Vector2 GetLayerMousePosition()
    {
        Vector2 local = GetStageLocalMouse();
        return local - (_canvasLayer?.Offset ?? Vector2.Zero);
    }

    private bool BeginCanvasInteraction()
    {
        Vector2 layerMouse = GetLayerMousePosition();
        Vector2 canvasMouse = GetCanvasMousePosition();

        // 1) Prefer handles / body of current selection
        if (_selectionKind == SelectionKind.Screen || _selectionKind == SelectionKind.Layer)
        {
            if (TryGetSelectedRect(out Vector2I pos, out Vector2I size))
            {
                Rect2 zoomed = new Rect2(pos.X * _zoom, pos.Y * _zoom, size.X * _zoom, size.Y * _zoom);
                var handle = HitTestHandle(zoomed, layerMouse);
                if (handle != DragMode.None)
                {
                    StartDrag(handle, canvasMouse, pos, size);
                    return true;
                }

                if (zoomed.Grow(HandleSizePx * 0.5f).HasPoint(layerMouse))
                {
                    StartDrag(DragMode.Move, canvasMouse, pos, size);
                    return true;
                }
            }
        }

        // 2) Hit-test items (layers first — typically on top conceptually — then screens)
        // Prefer topmost layer (list order) then screens when picking on stage.
        if (TryHitTestItem(canvasMouse, out SelectionKind kind, out int id))
        {
            if (kind == SelectionKind.Screen)
            {
                SelectScreenInTree(id);
                ApplySelection(SelectionKind.Screen, id, -1);
            }
            else if (kind == SelectionKind.Layer)
            {
                SelectLayerInTree(id);
                ApplySelection(SelectionKind.Layer, -1, id);
            }

            if (TryGetSelectedRect(out Vector2I pos, out Vector2I size))
            {
                StartDrag(DragMode.Move, canvasMouse, pos, size);
                return true;
            }
        }

        return false;
    }

    private bool TryHitTestItem(Vector2 canvasMouse, out SelectionKind kind, out int id)
    {
        kind = SelectionKind.None;
        id = -1;

        // Layers first, top-of-stack first (list order / highest ZIndex) so the top layer wins overlaps.
        foreach (var layer in DisplaysManager.Layers)
        {
            var r = new Rect2(layer.CanvasPosition, layer.Size);
            if (!r.HasPoint(canvasMouse))
                continue;
            kind = SelectionKind.Layer;
            id = layer.LayerId;
            return true;
        }

        foreach (var screen in DisplaysManager.Screens)
        {
            var r = new Rect2(screen.CanvasPosition, screen.OutputSize);
            if (!r.HasPoint(canvasMouse))
                continue;
            kind = SelectionKind.Screen;
            id = screen.OutputId;
            return true;
        }

        return false;
    }

    private bool TryGetSelectedRect(out Vector2I pos, out Vector2I size)
    {
        pos = Vector2I.Zero;
        size = Vector2I.Zero;

        if (_selectionKind == SelectionKind.Screen)
        {
            var screen = _displaysManager.GetOutputById(_selectedScreenId);
            if (screen == null)
                return false;
            pos = screen.CanvasPosition;
            size = screen.OutputSize;
            return true;
        }

        if (_selectionKind == SelectionKind.Layer)
        {
            var layer = DisplaysManager.GetLayerById(_selectedLayerId);
            if (layer == null)
                return false;
            pos = layer.CanvasPosition;
            size = layer.Size;
            return true;
        }

        return false;
    }

    private static DragMode HitTestHandle(Rect2 zoomedRect, Vector2 layerMouse)
    {
        float hs = HandleSizePx;
        var centers = CanvasItemGizmo.GetHandleCenters(zoomedRect.Size, hs);
        var modes = new[]
        {
            DragMode.ResizeNW, DragMode.ResizeN, DragMode.ResizeNE, DragMode.ResizeE,
            DragMode.ResizeSE, DragMode.ResizeS, DragMode.ResizeSW, DragMode.ResizeW
        };

        for (int i = 0; i < centers.Length; i++)
        {
            Vector2 world = zoomedRect.Position + centers[i];
            var handleRect = new Rect2(world - new Vector2(hs, hs) * 0.5f, new Vector2(hs, hs));
            if (handleRect.Grow(HandleHitSlopPx).HasPoint(layerMouse))
                return modes[i];
        }

        return DragMode.None;
    }

    private void StartDrag(DragMode mode, Vector2 canvasMouse, Vector2I pos, Vector2I size)
    {
        _isDraggingCanvas = true;
        _dragMode = mode;
        _dragStartCanvasMouse = canvasMouse;
        _dragStartPos = pos;
        _dragStartSize = size;

        // Snapshot once at drag start; continuous move/resize coalesces into one undo step.
        string kind = _selectionKind == SelectionKind.Screen ? "screen" : "layer";
        int id = _selectionKind == SelectionKind.Screen ? _selectedScreenId : _selectedLayerId;
        _activeDragCoalesceKey = $"settings:displays:{kind}:{id}:geom";
        string desc = mode == DragMode.Move ? "Move canvas item" : "Resize canvas item";
        RecordDisplaysHistory(desc, _activeDragCoalesceKey);
    }

    private void UpdateCanvasDrag()
    {
        if (!_isDraggingCanvas || _dragMode == DragMode.None)
            return;

        Vector2 canvasMouse = GetCanvasMousePosition();
        Vector2 delta = canvasMouse - _dragStartCanvasMouse;
        Vector2I d = new Vector2I(Mathf.RoundToInt(delta.X), Mathf.RoundToInt(delta.Y));

        Vector2I newPos = _dragStartPos;
        Vector2I newSize = _dragStartSize;

        switch (_dragMode)
        {
            case DragMode.Move:
                newPos = _dragStartPos + d;
                break;
            case DragMode.ResizeE:
                newSize = new Vector2I(Mathf.Max((int)MinItemSize, _dragStartSize.X + d.X), _dragStartSize.Y);
                break;
            case DragMode.ResizeS:
                newSize = new Vector2I(_dragStartSize.X, Mathf.Max((int)MinItemSize, _dragStartSize.Y + d.Y));
                break;
            case DragMode.ResizeSE:
                newSize = new Vector2I(
                    Mathf.Max((int)MinItemSize, _dragStartSize.X + d.X),
                    Mathf.Max((int)MinItemSize, _dragStartSize.Y + d.Y));
                break;
            case DragMode.ResizeW:
            {
                int right = _dragStartPos.X + _dragStartSize.X;
                newPos = new Vector2I(Mathf.Min(right - (int)MinItemSize, _dragStartPos.X + d.X), _dragStartPos.Y);
                newSize = new Vector2I(right - newPos.X, _dragStartSize.Y);
                break;
            }
            case DragMode.ResizeN:
            {
                int bottom = _dragStartPos.Y + _dragStartSize.Y;
                newPos = new Vector2I(_dragStartPos.X, Mathf.Min(bottom - (int)MinItemSize, _dragStartPos.Y + d.Y));
                newSize = new Vector2I(_dragStartSize.X, bottom - newPos.Y);
                break;
            }
            case DragMode.ResizeNW:
            {
                int right = _dragStartPos.X + _dragStartSize.X;
                int bottom = _dragStartPos.Y + _dragStartSize.Y;
                newPos = new Vector2I(
                    Mathf.Min(right - (int)MinItemSize, _dragStartPos.X + d.X),
                    Mathf.Min(bottom - (int)MinItemSize, _dragStartPos.Y + d.Y));
                newSize = new Vector2I(right - newPos.X, bottom - newPos.Y);
                break;
            }
            case DragMode.ResizeNE:
            {
                int bottom = _dragStartPos.Y + _dragStartSize.Y;
                newPos = new Vector2I(_dragStartPos.X, Mathf.Min(bottom - (int)MinItemSize, _dragStartPos.Y + d.Y));
                newSize = new Vector2I(
                    Mathf.Max((int)MinItemSize, _dragStartSize.X + d.X),
                    bottom - newPos.Y);
                break;
            }
            case DragMode.ResizeSW:
            {
                int right = _dragStartPos.X + _dragStartSize.X;
                newPos = new Vector2I(Mathf.Min(right - (int)MinItemSize, _dragStartPos.X + d.X), _dragStartPos.Y);
                newSize = new Vector2I(right - newPos.X, Mathf.Max((int)MinItemSize, _dragStartSize.Y + d.Y));
                break;
            }
        }

        if (_dragMode != DragMode.Move && IsSelectedKeepAspect())
            ApplyKeepAspectToDrag(ref newPos, ref newSize);

        ApplyLiveRect(newPos, newSize);
    }

    private bool IsSelectedKeepAspect()
    {
        if (_selectionKind == SelectionKind.Screen)
            return GetSelectedScreen()?.KeepAspect ?? false;
        if (_selectionKind == SelectionKind.Layer)
            return DisplaysManager.GetLayerById(_selectedLayerId)?.KeepAspect ?? false;
        return false;
    }

    /// <summary>
    /// Constrains drag resize to the aspect ratio at drag start.
    /// </summary>
    private void ApplyKeepAspectToDrag(ref Vector2I newPos, ref Vector2I newSize)
    {
        float aspect = _dragStartSize.X / (float)Mathf.Max(1, _dragStartSize.Y);

        switch (_dragMode)
        {
            case DragMode.ResizeE:
            case DragMode.ResizeW:
                newSize = new Vector2I(newSize.X, Mathf.Max((int)MinItemSize, Mathf.RoundToInt(newSize.X / aspect)));
                break;
            case DragMode.ResizeN:
            case DragMode.ResizeS:
                newSize = new Vector2I(Mathf.Max((int)MinItemSize, Mathf.RoundToInt(newSize.Y * aspect)), newSize.Y);
                break;
            case DragMode.ResizeSE:
                newSize = new Vector2I(newSize.X, Mathf.Max((int)MinItemSize, Mathf.RoundToInt(newSize.X / aspect)));
                break;
            case DragMode.ResizeNE:
            {
                int bottom = _dragStartPos.Y + _dragStartSize.Y;
                newSize = new Vector2I(newSize.X, Mathf.Max((int)MinItemSize, Mathf.RoundToInt(newSize.X / aspect)));
                newPos = new Vector2I(newPos.X, bottom - newSize.Y);
                break;
            }
            case DragMode.ResizeSW:
            {
                int right = _dragStartPos.X + _dragStartSize.X;
                newSize = new Vector2I(newSize.X, Mathf.Max((int)MinItemSize, Mathf.RoundToInt(newSize.X / aspect)));
                newPos = new Vector2I(right - newSize.X, newPos.Y);
                break;
            }
            case DragMode.ResizeNW:
            {
                int right = _dragStartPos.X + _dragStartSize.X;
                int bottom = _dragStartPos.Y + _dragStartSize.Y;
                newSize = new Vector2I(newSize.X, Mathf.Max((int)MinItemSize, Mathf.RoundToInt(newSize.X / aspect)));
                newPos = new Vector2I(right - newSize.X, bottom - newSize.Y);
                break;
            }
        }
    }

    /// <summary>
    /// Returns a new size keeping aspect of <paramref name="reference"/> when changing width or height.
    /// </summary>
    private static Vector2I SizeWithKeepAspect(Vector2I reference, int? newWidth, int? newHeight)
    {
        if (reference.X <= 0 || reference.Y <= 0)
        {
            return new Vector2I(
                newWidth ?? Mathf.Max(1, reference.X),
                newHeight ?? Mathf.Max(1, reference.Y));
        }

        float aspect = reference.X / (float)reference.Y;
        if (newWidth.HasValue)
        {
            int w = Mathf.Max(1, newWidth.Value);
            return new Vector2I(w, Mathf.Max(1, Mathf.RoundToInt(w / aspect)));
        }

        if (newHeight.HasValue)
        {
            int h = Mathf.Max(1, newHeight.Value);
            return new Vector2I(Mathf.Max(1, Mathf.RoundToInt(h * aspect)), h);
        }

        return reference;
    }

    private void ApplyLiveRect(Vector2I pos, Vector2I size)
    {
        if (_selectionKind == SelectionKind.Screen)
        {
            var screen = _displaysManager.GetOutputById(_selectedScreenId);
            if (screen == null)
                return;
            screen.CanvasPosition = pos;
            screen.OutputSize = size;
            // Defer OS window resize until drag ends, but keep live video + test patterns in sync.
            screen.UpdateAllLayerDisplayRects();
            _displaysManager.RefreshTestPatternsLive(outputId: _selectedScreenId);
        }
        else if (_selectionKind == SelectionKind.Layer)
        {
            var layer = DisplaysManager.GetLayerById(_selectedLayerId);
            if (layer == null)
                return;
            layer.CanvasPosition = pos;
            layer.Size = size;
            // Push geometry to playing video TextureRects and layer test patterns on every output.
            foreach (var output in DisplaysManager.Outputs)
                output.UpdateLayerDisplayRect(_selectedLayerId);
            _displaysManager.RefreshTestPatternsLive(layerId: _selectedLayerId);
        }
        else
        {
            return;
        }

        SyncPropsFromSelectionLive(pos, size);
        UpdateCanvasGizmos();
    }

    private void SyncPropsFromSelectionLive(Vector2I pos, Vector2I size)
    {
        _isUpdatingProps = true;
        try
        {
            if (_selectionKind == SelectionKind.Screen && _outputProps.Visible)
            {
                _outputPosXLineEdit.Text = pos.X.ToString();
                _outputPosYLineEdit.Text = pos.Y.ToString();
                _outputSizeXLineEdit.Text = size.X.ToString();
                _outputSizeYLineEdit.Text = size.Y.ToString();
            }
            else if (_selectionKind == SelectionKind.Layer && _layerProps.Visible)
            {
                _layerPosXLineEdit.Text = pos.X.ToString();
                _layerPosYLineEdit.Text = pos.Y.ToString();
                _layerSizeXLineEdit.Text = size.X.ToString();
                _layerSizeYLineEdit.Text = size.Y.ToString();
            }
        }
        finally
        {
            _isUpdatingProps = false;
        }
    }

    private void EndCanvasInteraction()
    {
        if (!_isDraggingCanvas)
            return;

        _isDraggingCanvas = false;
        var mode = _dragMode;
        _dragMode = DragMode.None;

        // Seal the drag session so the next move/resize is a new undo step.
        if (!string.IsNullOrEmpty(_activeDragCoalesceKey))
        {
            _historyManager?.EndCoalesceSession(_activeDragCoalesceKey);
            _activeDragCoalesceKey = null;
        }

        if (mode == DragMode.None)
            return;

        if (!TryGetSelectedRect(out Vector2I pos, out Vector2I size))
            return;

        // Commit through DisplaysManager so outputs / test patterns update
        // (geometry was already applied live; history was captured at StartDrag).
        if (_selectionKind == SelectionKind.Screen)
        {
            _displaysManager.UpdateOutputCanvasPosition(_selectedScreenId, pos);
            _displaysManager.UpdateOutputSize(_selectedScreenId, size);
            LoadScreenProps();
        }
        else if (_selectionKind == SelectionKind.Layer)
        {
            _displaysManager.UpdateLayerCanvasPosition(_selectedLayerId, pos);
            _displaysManager.UpdateLayerSize(_selectedLayerId, size);
            LoadLayerProps();
        }

        UpdateCanvasGizmos();
    }

    private void UpdateStageCursor()
    {
        CursorShape shape = CursorShape.Arrow;
        if (TryGetSelectedRect(out Vector2I pos, out Vector2I size))
        {
            Rect2 zoomed = new Rect2(pos.X * _zoom, pos.Y * _zoom, size.X * _zoom, size.Y * _zoom);
            var handle = HitTestHandle(zoomed, GetLayerMousePosition());
            shape = handle switch
            {
                DragMode.ResizeN or DragMode.ResizeS => CursorShape.Vsize,
                DragMode.ResizeE or DragMode.ResizeW => CursorShape.Hsize,
                DragMode.ResizeNE or DragMode.ResizeSW => CursorShape.Bdiagsize,
                DragMode.ResizeNW or DragMode.ResizeSE => CursorShape.Fdiagsize,
                DragMode.None when zoomed.HasPoint(GetLayerMousePosition()) => CursorShape.Move,
                _ => CursorShape.Arrow
            };
        }

        MouseDefaultCursorShape = shape;
        if (_stagePointer != null && IsInstanceValid(_stagePointer))
            _stagePointer.MouseDefaultCursorShape = shape;
    }

    #endregion

}
