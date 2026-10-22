// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Cue2.Domain.Cuelist;
using Cue2.Domain.Cues;
using Cue2.Services;
using Cue2.UI.Popups;
using Cue2.UI.Utilities;
using Godot;
using Godot.Collections;
using Array = Godot.Collections.Array;

namespace Cue2.UI.Shell;

/// <summary>
/// Partial: grid build, layout, axis resize and cell lookup.
/// </summary>
public partial class ButtonBoard
{
    private void RefreshGridAfterMutation(int prevRows, int prevCols, params int[] cellsToBind)
    {
        if (_rows != prevRows || _cols != prevCols)
        {
            RebuildGrid();
            return;
        }

        for (int i = 0; i + 1 < cellsToBind.Length; i += 2)
            BindCellAt(cellsToBind[i], cellsToBind[i + 1]);
        LayoutCells();
    }

    private void BindCellAt(int row, int col)
    {
        int i = row * _cols + col;
        if (i < 0 || i >= _cells.Count)
            return;
        var cell = _cells[i];
        if (cell == null || !GodotObject.IsInstanceValid(cell))
            return;
        cell.Bind(this, row, col, GetCellCueId(row, col), IsShowMode());
    }

    private void RebuildGrid()
    {
        if (_contentHost == null || !GodotObject.IsInstanceValid(_contentHost))
            return;

        EnsureModel();
        bool showMode = IsShowMode();

        var unused = new System.Collections.Generic.Dictionary<(int Row, int Col), ButtonBoardCell>();
        foreach (var existing in _cells)
        {
            if (existing == null || !GodotObject.IsInstanceValid(existing))
                continue;
            unused[(existing.Row, existing.Col)] = existing;
        }

        var next = new List<ButtonBoardCell>(_rows * _cols);
        for (int r = 0; r < _rows; r++)
        {
            for (int c = 0; c < _cols; c++)
            {
                int cueId = GetCellCueId(r, c);
                if (unused.TryGetValue((r, c), out var cell))
                {
                    unused.Remove((r, c));
                    cell.Bind(this, r, c, cueId, showMode);
                }
                else
                {
                    cell = new ButtonBoardCell();
                    _contentHost.AddChild(cell);
                    cell.Bind(this, r, c, cueId, showMode);
                }

                next.Add(cell);
            }
        }

        foreach (var leftover in unused.Values)
        {
            leftover.GetParent()?.RemoveChild(leftover);
            leftover.QueueFree();
        }

        _cells.Clear();
        _cells.AddRange(next);

        SyncAxisHandles(_rowHandles, _rows, vertical: false);
        SyncAxisHandles(_colHandles, _cols, vertical: true);
        RaiseHandles();
        ApplyHandleInteractable();
        LayoutCells();
    }

    private void SyncAxisHandles(List<ButtonBoardResizeHandle> handles, int count, bool vertical)
    {
        while (handles.Count < count)
        {
            var handle = new ButtonBoardResizeHandle();
            _contentHost.AddChild(handle);
            handles.Add(handle);
        }

        while (handles.Count > count)
        {
            int last = handles.Count - 1;
            var extra = handles[last];
            handles.RemoveAt(last);
            if (extra != null && GodotObject.IsInstanceValid(extra))
            {
                extra.GetParent()?.RemoveChild(extra);
                extra.QueueFree();
            }
        }

        for (int i = 0; i < count; i++)
        {
            var handle = handles[i];
            if (handle == null || !GodotObject.IsInstanceValid(handle))
                continue;
            handle.Bind(this, vertical, i);
        }
    }

    private void RaiseHandles()
    {
        if (_contentHost == null)
            return;
        int idx = 0;
        foreach (var cell in _cells)
        {
            if (cell != null && GodotObject.IsInstanceValid(cell) && cell.GetParent() == _contentHost)
                _contentHost.MoveChild(cell, idx++);
        }

        foreach (var handle in _rowHandles)
        {
            if (handle != null && GodotObject.IsInstanceValid(handle) && handle.GetParent() == _contentHost)
                _contentHost.MoveChild(handle, idx++);
        }

        foreach (var handle in _colHandles)
        {
            if (handle != null && GodotObject.IsInstanceValid(handle) && handle.GetParent() == _contentHost)
                _contentHost.MoveChild(handle, idx++);
        }
    }

    private void LayoutCells()
    {
        if (_contentHost == null || !GodotObject.IsInstanceValid(_contentHost))
            return;

        if (_colStops.Length != _cols + 1)
            _colStops = new float[_cols + 1];
        if (_rowStops.Length != _rows + 1)
            _rowStops = new float[_rows + 1];
        _colStops[0] = 0f;
        _rowStops[0] = 0f;
        for (int c = 0; c < _cols; c++)
            _colStops[c + 1] = _colStops[c] + ColWidth(c);
        for (int r = 0; r < _rows; r++)
            _rowStops[r + 1] = _rowStops[r] + RowHeight(r);

        float tableW = _colStops[_cols];
        float tableH = _rowStops[_rows];
        int expected = _rows * _cols;
        int count = Math.Min(_cells.Count, expected);
        for (int i = 0; i < count; i++)
        {
            var cell = _cells[i];
            if (cell == null || !GodotObject.IsInstanceValid(cell))
                continue;
            int r = i / _cols;
            int c = i % _cols;
            cell.Size = new Vector2(_colStops[c + 1] - _colStops[c], _rowStops[r + 1] - _rowStops[r]);
            cell.Position = new Vector2(_colStops[c], _rowStops[r]);
        }

        float hit = ButtonBoardResizeHandle.HitSize;
        float half = hit * 0.5f;
        int colHandleCount = Math.Min(_colHandles.Count, _cols);
        for (int c = 0; c < colHandleCount; c++)
        {
            var handle = _colHandles[c];
            if (handle == null || !GodotObject.IsInstanceValid(handle))
                continue;
            handle.Position = new Vector2(_colStops[c + 1] - half, 0f);
            handle.Size = new Vector2(hit, tableH);
        }

        int rowHandleCount = Math.Min(_rowHandles.Count, _rows);
        for (int r = 0; r < rowHandleCount; r++)
        {
            var handle = _rowHandles[r];
            if (handle == null || !GodotObject.IsInstanceValid(handle))
                continue;
            handle.Position = new Vector2(0f, _rowStops[r + 1] - half);
            handle.Size = new Vector2(tableW, hit);
        }

        _contentHost.CustomMinimumSize = new Vector2(
            Math.Max(MinColWidth, tableW + half),
            Math.Max(MinRowHeight, tableH + half));
    }

    /// <summary>Applies pointer motion to the active column/row resize.</summary>
    internal void UpdateAxisResizeFromHandle(Vector2 globalPos) => UpdateAxisResize(globalPos);

    private void UpdateAxisResize(Vector2 globalPos)
    {
        if (!_resizing)
            return;

        Vector2 local = ToContentLocal(globalPos);
        bool matchAll = Input.IsKeyPressed(Key.Ctrl) || Input.IsKeyPressed(Key.Meta);
        if (_resizeVertical)
        {
            if (_resizeIndex < 0 || _resizeIndex >= _colWidths.Count)
                return;
            float next = Math.Max(MinColWidth, _resizeStartSize + (local.X - _resizeStartLocal.X));
            bool axisChanged = !Mathf.IsEqualApprox(_colWidths[_resizeIndex], next);
            bool matchNeeded = matchAll && !AxisAllMatch(_colWidths, next);
            if (!axisChanged && !matchNeeded)
                return;
            if (matchAll)
                ApplyAxisSize(_colWidths, next);
            else
                _colWidths[_resizeIndex] = next;
        }
        else
        {
            if (_resizeIndex < 0 || _resizeIndex >= _rowHeights.Count)
                return;
            float next = Math.Max(MinRowHeight, _resizeStartSize + (local.Y - _resizeStartLocal.Y));
            bool axisChanged = !Mathf.IsEqualApprox(_rowHeights[_resizeIndex], next);
            bool matchNeeded = matchAll && !AxisAllMatch(_rowHeights, next);
            if (!axisChanged && !matchNeeded)
                return;
            if (matchAll)
                ApplyAxisSize(_rowHeights, next);
            else
                _rowHeights[_resizeIndex] = next;
        }

        LayoutCells();
    }

    private void EndAxisResize()
    {
        if (!_resizing)
            return;

        var finalCols = new List<float>(_colWidths);
        var finalRows = new List<float>(_rowHeights);
        bool changed = !AxisListsEqual(_colWidths, _resizeStartColWidths)
                       || !AxisListsEqual(_rowHeights, _resizeStartRowHeights);

        _resizing = false;
        _resizeHandle?.SetDragging(false);
        _resizeHandle = null;
        MouseDefaultCursorShape = CursorShape.Arrow;
        SyncPointerProcessing();

        if (!changed || _historyManager?.IsRestoring == true)
        {
            _resizeStartColWidths = null;
            _resizeStartRowHeights = null;
            return;
        }

        CopyAxisList(_colWidths, _resizeStartColWidths);
        CopyAxisList(_rowHeights, _resizeStartRowHeights);
        _historyManager?.RecordCuelistChange("Resize button board");
        CopyAxisList(_colWidths, finalCols);
        CopyAxisList(_rowHeights, finalRows);
        _resizeStartColWidths = null;
        _resizeStartRowHeights = null;
        LayoutCells();
    }

    private static bool AxisAllMatch(List<float> sizes, float value)
    {
        if (sizes == null || sizes.Count == 0)
            return true;
        for (int i = 0; i < sizes.Count; i++)
        {
            if (!Mathf.IsEqualApprox(sizes[i], value))
                return false;
        }

        return true;
    }

    private static void ApplyAxisSize(List<float> sizes, float value)
    {
        if (sizes == null)
            return;
        for (int i = 0; i < sizes.Count; i++)
            sizes[i] = value;
    }

    private static bool AxisListsEqual(List<float> a, List<float> b)
    {
        if (a == null || b == null || a.Count != b.Count)
            return false;
        for (int i = 0; i < a.Count; i++)
        {
            if (!Mathf.IsEqualApprox(a[i], b[i]))
                return false;
        }

        return true;
    }

    private static void CopyAxisList(List<float> dest, List<float> source)
    {
        if (dest == null || source == null)
            return;
        int n = Math.Min(dest.Count, source.Count);
        for (int i = 0; i < n; i++)
            dest[i] = source[i];
    }

    private void ApplyHandleInteractable()
    {
        bool edit = !IsShowMode();
        foreach (var handle in _colHandles)
        {
            if (handle != null && GodotObject.IsInstanceValid(handle))
                handle.SetInteractable(edit);
        }

        foreach (var handle in _rowHandles)
        {
            if (handle != null && GodotObject.IsInstanceValid(handle))
                handle.SetInteractable(edit);
        }
    }

    private Vector2 ToContentLocal(Vector2 globalPos)
    {
        if (_contentHost == null || !GodotObject.IsInstanceValid(_contentHost))
            return globalPos;
        return _contentHost.GetGlobalTransformWithCanvas().AffineInverse() * globalPos;
    }

    private float ColWidth(int col)
    {
        if (col < 0 || col >= _colWidths.Count)
            return DefaultColWidth;
        return Math.Max(MinColWidth, _colWidths[col]);
    }

    private float GetVisibleBoardWidth()
    {
        float w = 0f;
        if (_scroll != null && GodotObject.IsInstanceValid(_scroll))
            w = _scroll.Size.X;
        if (w < 32f)
            w = Size.X;
        if (w < 32f)
            w = 640f;
        return Math.Max(MinColWidth, w - 20f);
    }

    private int MaxColumnsForViewport()
    {
        float visible = GetVisibleBoardWidth();
        float used = 0f;
        int count = 0;
        const int hardMax = 64;
        while (count < hardMax)
        {
            float next = count < _colWidths.Count ? ColWidth(count) : DefaultColWidth;
            if (count > 0 && used + next > visible)
                break;
            used += next;
            count++;
        }

        return Math.Max(1, count);
    }

    private float RowHeight(int row)
    {
        if (row < 0 || row >= _rowHeights.Count)
            return DefaultRowHeight;
        return Math.Max(MinRowHeight, _rowHeights[row]);
    }

    /// <summary>Cell under <paramref name="globalPos"/>, or null.</summary>
    internal ButtonBoardCell FindCellAtGlobal(Vector2 globalPos)
    {
        foreach (var cell in _cells)
        {
            if (cell != null && GodotObject.IsInstanceValid(cell) && cell.GetGlobalRect().HasPoint(globalPos))
                return cell;
        }

        return null;
    }

    private void EnsureEmptySlotFrom(ref int row, ref int col)
    {
        if (TryFindNextEmpty(row, col, out int foundRow, out int foundCol))
        {
            row = foundRow;
            col = foundCol;
            return;
        }

        GrowForNextEmpty();
        if (TryFindNextEmpty(row, col, out foundRow, out foundCol))
        {
            row = foundRow;
            col = foundCol;
            return;
        }

        GrowForNextEmpty();
        if (TryFindNextEmpty(row, col, out foundRow, out foundCol))
        {
            row = foundRow;
            col = foundCol;
            return;
        }

        row = Math.Max(0, _rows - 1);
        col = Math.Max(0, _cols - 1);
    }

    private void GrowForNextEmpty()
    {
        if (_cols < MaxColumnsForViewport())
            AppendColumn();
        else
            AppendRow();
    }

    private bool TryFindNextEmpty(int startRow, int startCol, out int row, out int col)
    {
        row = -1;
        col = -1;
        if (startRow < 0)
            startRow = 0;
        if (startCol < 0)
            startCol = 0;
        for (int r = startRow; r < _rows; r++)
        {
            int c0 = r == startRow ? startCol : 0;
            for (int c = c0; c < _cols; c++)
            {
                if (GetCellCueId(r, c) < 0)
                {
                    row = r;
                    col = c;
                    return true;
                }
            }
        }

        return false;
    }

    private void AdvanceCell(ref int row, ref int col)
    {
        col++;
        if (col >= _cols)
        {
            col = 0;
            row++;
        }
    }
}
