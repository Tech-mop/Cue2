// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System;
using Cue2.Domain.Devices;
using Cue2.Services;
using Cue2.UI.Utilities;
using Godot;

namespace Cue2.UI.Settings.PatchMatrix;

/// <summary>
/// Custom-drawn routing grid: one cell per bus × visible device output.
/// Click toggles, drag paints, collapsed groups expand on click.
/// </summary>
public partial class PatchMatrixCells : Control
{
    /// <summary>Raised when a cell is painted to <paramref name="routed"/>.</summary>
    public event Action<int, int, bool> CellPainted;

    /// <summary>Raised when the user clicks a collapsed-group placeholder.</summary>
    public event Action<string, int> GroupExpandRequested;

    /// <summary>Raised when the hovered cell changes (row/col, or -1/-1 on exit).</summary>
    public event Action<int, int> HoverChanged;

    /// <summary>Raised when a paint gesture starts (mouse down on a live cell).</summary>
    public event Action PaintStarted;

    /// <summary>Raised when a paint gesture ends (mouse up / leave while dragging).</summary>
    public event Action PaintEnded;

    private PatchMatrixView _view;
    private AudioOutputPatch _patch;
    private int _hoverRow = -1;
    private int _hoverCol = -1;
    private bool _painting;
    private bool _paintValue;
    private Font _font;

    /// <summary>Current layout. Assign and call <see cref="ApplyView"/>.</summary>
    public PatchMatrixView View => _view;

    /// <summary>Row under the pointer, or -1.</summary>
    public int HoverRow => _hoverRow;

    /// <summary>Column under the pointer, or -1.</summary>
    public int HoverCol => _hoverCol;

    /// <summary>
    /// Binds patch + layout and sizes this control to the grid.
    /// </summary>
    /// <param name="patch">Patch whose routing is shown.</param>
    /// <param name="view">Layout snapshot.</param>
    public void ApplyView(AudioOutputPatch patch, PatchMatrixView view)
    {
        _patch = patch;
        _view = view;
        float w = view != null ? Math.Max(view.TotalWidth, 1f) : 1f;
        float h = view != null ? Math.Max(view.BodyHeight, 1f) : 1f;
        CustomMinimumSize = new Vector2(w, h);
        QueueRedraw();
    }

    public override void _Ready()
    {
        FocusMode = FocusModeEnum.All;
        MouseFilter = MouseFilterEnum.Stop;
        MouseDefaultCursorShape = CursorShape.Arrow;
        _font = ThemeDB.FallbackFont;
    }

    public override void _Notification(int what)
    {
        if (what == NotificationMouseExit)
            SetHover(-1, -1);
    }

    public override void _GuiInput(InputEvent @event)
    {
        if (_view == null)
            return;

        switch (@event)
        {
            case InputEventMouseMotion motion:
                HandleMotion(motion);
                break;
            case InputEventMouseButton button:
                HandleButton(button);
                break;
            case InputEventKey key when key.Pressed && !key.Echo && HasFocus():
                HandleKey(key);
                break;
        }
    }

    public override string _GetTooltip(Vector2 atPosition)
    {
        if (_view == null)
            return string.Empty;
        HitTest(atPosition, out int row, out int col);
        if (row < 0 || col < 0 || col >= _view.Columns.Count || row >= _view.BusNames.Count)
            return string.Empty;

        var column = _view.Columns[col];
        string bus = _view.BusNames[row];
        if (column.IsGroupPlaceholder)
        {
            int last = column.DeviceChannel + column.GroupOutputCount;
            return UiLocalizer.Tf("{0} → {1} {2}–{3} (click to expand)",
                bus, column.DeviceName, column.DeviceChannel + 1, last);
        }

        return UiLocalizer.Tf("{0} → {1} {2}", bus, column.DeviceName, column.DeviceChannel + 1);
    }

    public override void _Draw()
    {
        var view = _view;
        int rows = view?.BusIds.Count ?? 0;
        int cols = view?.Columns.Count ?? 0;
        var size = Size;
        DrawRect(new Rect2(Vector2.Zero, size), new Color(0.10f, 0.10f, 0.10f));

        if (view == null || cols == 0)
        {
            string empty = UiLocalizer.T("Add a device to route this patch.");
            var font = _font ?? ThemeDB.FallbackFont;
            if (font != null)
            {
                DrawString(font, new Vector2(8, 22), empty, HorizontalAlignment.Left, -1, 13,
                    new Color(0.65f, 0.65f, 0.65f));
            }
            return;
        }

        float cellH = PatchMatrixMetrics.CellSize;
        var grid = new Color(0.22f, 0.22f, 0.22f);
        var gridStrong = new Color(0.08f, 0.35f, 0.38f, 0.7f);
        var routedFill = GlobalStyles.LowColor2;
        var collapsedColumn = new Color(GlobalStyles.LowColor3.R, GlobalStyles.LowColor3.G, GlobalStyles.LowColor3.B, 0.10f);
        var pip = GlobalStyles.LowColor1;
        var hover = new Color(1f, 1f, 1f, 0.07f);
        var missing = new Color(0.55f, 0.12f, 0.12f, 0.28f);

        if (_hoverRow >= 0 && _hoverRow < rows)
            DrawRect(new Rect2(0, _hoverRow * cellH, view.TotalWidth, cellH), hover);

        float x = 0f;
        for (int c = 0; c < cols; c++)
        {
            var col = view.Columns[c];
            if (col.IsGroupPlaceholder)
                DrawRect(new Rect2(x, 0, col.Width, rows * cellH), collapsedColumn);
            if (_hoverCol == c)
                DrawRect(new Rect2(x, 0, col.Width, rows * cellH), hover);

            for (int r = 0; r < rows; r++)
            {
                var cell = new Rect2(x + 1, r * cellH + 1, col.Width - 2, cellH - 2);
                if (col.IsGroupPlaceholder)
                {
                    if (GroupHasRoute(col, view.BusIds[r]))
                    {
                        float pipSize = 6f;
                        DrawRect(new Rect2(
                            x + (col.Width - pipSize) * 0.5f,
                            r * cellH + (cellH - pipSize) * 0.5f,
                            pipSize, pipSize), pip);
                    }
                    DrawRect(cell, grid, false, 1);
                }
                else
                {
                    if (IsRouted(col, view.BusIds[r]))
                        DrawRect(cell, routedFill);
                    DrawRect(cell, grid, false, 1);
                }

                if (col.DeviceMissing)
                    DrawRect(cell, missing);
            }

            x += col.Width;
        }

        // Device / group separators on top of cells.
        foreach (var device in view.Devices)
        {
            DrawLine(new Vector2(device.X, 0), new Vector2(device.X, rows * cellH), gridStrong, 1.5f);
            if (view.ShowGroupRow)
            {
                foreach (var g in device.Groups)
                {
                    if (g.X <= device.X + 0.5f) continue;
                    DrawLine(new Vector2(g.X, 0), new Vector2(g.X, rows * cellH),
                        new Color(0.3f, 0.3f, 0.3f, 0.8f), 1);
                }
            }
        }

        if (HasFocus() && _hoverRow >= 0 && _hoverCol >= 0 && _hoverCol < cols)
        {
            float fx = ColumnX(_hoverCol);
            var focus = new Rect2(fx + 0.5f, _hoverRow * cellH + 0.5f,
                view.Columns[_hoverCol].Width - 1, cellH - 1);
            DrawRect(focus, GlobalStyles.HighColor2, false, 1.5f);
        }
    }

    private void HandleMotion(InputEventMouseMotion motion)
    {
        HitTest(motion.Position, out int row, out int col);
        SetHover(row, col);
        MouseDefaultCursorShape = col >= 0 && _view.Columns[col].IsGroupPlaceholder
            ? CursorShape.PointingHand
            : CursorShape.Arrow;

        if (_painting && row >= 0 && col >= 0 && !_view.Columns[col].IsGroupPlaceholder)
            PaintCell(row, col, _paintValue);

        AcceptEvent();
    }

    private void HandleButton(InputEventMouseButton button)
    {
        if (button.ButtonIndex != MouseButton.Left)
            return;

        HitTest(button.Position, out int row, out int col);
        if (button.Pressed)
        {
            GrabFocus();
            SetHover(row, col);
            if (col < 0 || row < 0)
                return;

            var column = _view.Columns[col];
            if (column.IsGroupPlaceholder)
            {
                GroupExpandRequested?.Invoke(column.DeviceName, column.GroupIndex);
                AcceptEvent();
                return;
            }

            bool next = !IsRouted(column, _view.BusIds[row]);
            _painting = true;
            _paintValue = next;
            PaintStarted?.Invoke();
            PaintCell(row, col, next);
            AcceptEvent();
        }
        else if (_painting)
        {
            _painting = false;
            PaintEnded?.Invoke();
            AcceptEvent();
        }
    }

    private void HandleKey(InputEventKey key)
    {
        int rows = _view.BusIds.Count;
        int cols = _view.Columns.Count;
        if (rows == 0 || cols == 0)
            return;

        int row = _hoverRow < 0 ? 0 : _hoverRow;
        int col = _hoverCol < 0 ? 0 : _hoverCol;
        bool handled = true;
        switch (key.Keycode)
        {
            case Key.Left:
                col = Math.Max(0, col - 1);
                break;
            case Key.Right:
                col = Math.Min(cols - 1, col + 1);
                break;
            case Key.Up:
                row = Math.Max(0, row - 1);
                break;
            case Key.Down:
                row = Math.Min(rows - 1, row + 1);
                break;
            case Key.Space:
            case Key.Enter:
            case Key.KpEnter:
                if (!_view.Columns[col].IsGroupPlaceholder)
                {
                    PaintStarted?.Invoke();
                    PaintCell(row, col, !IsRouted(_view.Columns[col], _view.BusIds[row]));
                    PaintEnded?.Invoke();
                }
                else
                {
                    GroupExpandRequested?.Invoke(_view.Columns[col].DeviceName, _view.Columns[col].GroupIndex);
                }
                break;
            default:
                handled = false;
                break;
        }

        if (!handled)
            return;

        SetHover(row, col);
        AcceptEvent();
    }

    private void PaintCell(int row, int col, bool routed)
    {
        if (_view == null || col < 0 || col >= _view.Columns.Count || row < 0 || row >= _view.BusIds.Count)
            return;
        var column = _view.Columns[col];
        if (column.IsGroupPlaceholder)
            return;
        CellPainted?.Invoke(row, col, routed);
        QueueRedraw();
    }

    private bool IsRouted(PatchMatrixColumn column, int busId)
    {
        if (_patch == null || column.OutputIndex < 0)
            return false;
        return _patch.IsChannelRouted(column.DeviceName, column.OutputIndex, busId);
    }

    private bool GroupHasRoute(PatchMatrixColumn column, int busId)
    {
        if (_patch == null)
            return false;
        int end = column.GroupOutputStart + column.GroupOutputCount;
        for (int i = column.GroupOutputStart; i < end; i++)
        {
            if (_patch.IsChannelRouted(column.DeviceName, i, busId))
                return true;
        }
        return false;
    }

    private void HitTest(Vector2 local, out int row, out int col)
    {
        row = -1;
        col = -1;
        if (_view == null || _view.Columns.Count == 0)
            return;

        float cellH = PatchMatrixMetrics.CellSize;
        if (local.Y >= 0 && _view.BusIds.Count > 0)
        {
            int r = (int)(local.Y / cellH);
            if (r >= 0 && r < _view.BusIds.Count)
                row = r;
        }

        float x = 0f;
        for (int c = 0; c < _view.Columns.Count; c++)
        {
            float w = _view.Columns[c].Width;
            if (local.X >= x && local.X < x + w)
            {
                col = c;
                break;
            }
            x += w;
        }
    }

    private float ColumnX(int col)
    {
        float x = 0f;
        for (int i = 0; i < col && i < _view.Columns.Count; i++)
            x += _view.Columns[i].Width;
        return x;
    }

    private void SetHover(int row, int col)
    {
        if (row == _hoverRow && col == _hoverCol)
            return;
        _hoverRow = row;
        _hoverCol = col;
        HoverChanged?.Invoke(row, col);
        QueueRedraw();
    }
}
