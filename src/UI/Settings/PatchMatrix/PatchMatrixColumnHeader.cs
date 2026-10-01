// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System;
using Cue2.Services;
using Cue2.UI.Utilities;
using Godot;

namespace Cue2.UI.Settings.PatchMatrix;

/// <summary>
/// Pinned column header for the patch matrix: device name, group-of-8 chips, hardware numbers.
/// </summary>
public partial class PatchMatrixColumnHeader : Control
{
    /// <summary>Raised when the user clicks the remove control on a device span.</summary>
    public event Action<string> RemoveDeviceRequested;

    /// <summary>Raised when a group chip is clicked (expand or collapse).</summary>
    public event Action<string, int> ToggleGroupRequested;

    private PatchMatrixView _view;
    private int _hoverCol = -1;
    private Font _font;

    /// <summary>Column under the pointer in the cell grid, for a vertical highlight.</summary>
    public int HoverCol
    {
        get => _hoverCol;
        set
        {
            if (_hoverCol == value) return;
            _hoverCol = value;
            QueueRedraw();
        }
    }

    /// <summary>
    /// Applies layout and sizes this control to the header strip.
    /// </summary>
    /// <param name="view">Layout snapshot.</param>
    public void ApplyView(PatchMatrixView view)
    {
        _view = view;
        float w = view != null ? Math.Max(view.TotalWidth, 1f) : 1f;
        float h = view != null ? Math.Max(view.HeaderHeight, 1f) : PatchMatrixMetrics.HeaderHeight(false);
        CustomMinimumSize = new Vector2(w, h);
        QueueRedraw();
    }

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Stop;
        _font = ThemeDB.FallbackFont;
    }

    public override string _GetTooltip(Vector2 atPosition)
    {
        if (_view == null)
            return string.Empty;

        foreach (var device in _view.Devices)
        {
            if (atPosition.X < device.X || atPosition.X >= device.X + device.Width)
                continue;

            if (atPosition.Y < PatchMatrixMetrics.DeviceNameRow)
            {
                if (RemoveRect(device).HasPoint(atPosition))
                    return UiLocalizer.T("Remove this device from the patch");
                return device.Missing
                    ? UiLocalizer.Tf("{0}: used in this patch but currently unavailable.", device.Name)
                    : device.Name;
            }

            if (_view.ShowGroupRow)
            {
                foreach (var group in device.Groups)
                {
                    if (atPosition.X >= group.X && atPosition.X < group.X + group.Width)
                    {
                        string range = $"{group.FirstHw + 1}–{group.LastHw + 1}";
                        return group.Collapsed
                            ? UiLocalizer.Tf("Expand channels {0}", range)
                            : UiLocalizer.Tf("Collapse channels {0}", range);
                    }
                }
            }
        }

        return string.Empty;
    }

    public override void _GuiInput(InputEvent @event)
    {
        if (_view == null || @event is not InputEventMouseButton button)
            return;
        if (button.ButtonIndex != MouseButton.Left || !button.Pressed)
            return;

        float y = button.Position.Y;
        float x = button.Position.X;
        float nameBottom = PatchMatrixMetrics.DeviceNameRow;
        float groupBottom = nameBottom + (_view.ShowGroupRow ? PatchMatrixMetrics.GroupRow : 0f);

        foreach (var device in _view.Devices)
        {
            if (x < device.X || x >= device.X + device.Width)
                continue;

            if (y < nameBottom)
            {
                var remove = RemoveRect(device);
                if (remove.HasPoint(button.Position))
                    RemoveDeviceRequested?.Invoke(device.Name);
                AcceptEvent();
                return;
            }

            if (_view.ShowGroupRow && y < groupBottom)
            {
                foreach (var group in device.Groups)
                {
                    if (x >= group.X && x < group.X + group.Width)
                    {
                        ToggleGroupRequested?.Invoke(device.Name, group.GroupIndex);
                        AcceptEvent();
                        return;
                    }
                }
            }
        }
    }

    public override void _Draw()
    {
        var view = _view;
        var size = Size;
        DrawRect(new Rect2(Vector2.Zero, size), new Color(0.12f, 0.12f, 0.13f));
        var font = _font ?? ThemeDB.FallbackFont;
        if (view == null)
            return;

        if (view.Devices.Count == 0)
        {
            if (font != null)
            {
                DrawString(font, new Vector2(8, 18),
                    UiLocalizer.T("Add a device to this patch."),
                    HorizontalAlignment.Left, -1, 12,
                    new Color(0.65f, 0.65f, 0.65f));
            }
            return;
        }

        float nameH = PatchMatrixMetrics.DeviceNameRow;
        float groupH = view.ShowGroupRow ? PatchMatrixMetrics.GroupRow : 0f;
        float numberTop = nameH + groupH;
        var hoverTint = new Color(1f, 1f, 1f, 0.06f);
        var missingBg = new Color(0.45f, 0.10f, 0.10f, 0.45f);
        var chipBg = new Color(0.18f, 0.22f, 0.23f);
        var chipCollapsed = new Color(0.16f, 0.16f, 0.18f);
        var border = new Color(0.08f, 0.35f, 0.38f, 0.85f);
        var text = new Color(0.86f, 0.88f, 0.88f);
        var muted = new Color(0.7f, 0.72f, 0.72f);

        if (_hoverCol >= 0 && _hoverCol < view.Columns.Count)
        {
            float hx = 0f;
            for (int i = 0; i < _hoverCol; i++)
                hx += view.Columns[i].Width;
            DrawRect(new Rect2(hx, 0, view.Columns[_hoverCol].Width, size.Y), hoverTint);
        }

        foreach (var device in view.Devices)
        {
            var span = new Rect2(device.X, 0, device.Width, size.Y);
            if (device.Missing)
                DrawRect(span, missingBg);
            DrawRect(span, border, false, 1);

            var remove = RemoveRect(device);
            float nameWidth = Math.Max(8f, device.Width - 20f);
            if (font != null)
            {
                DrawString(font, new Vector2(device.X + 4, 16),
                    Truncate(font, device.Name, nameWidth, 12),
                    HorizontalAlignment.Left, nameWidth, 12, text);
                DrawString(font, remove.Position + new Vector2(4, 13), "×",
                    HorizontalAlignment.Left, -1, 14, new Color(0.85f, 0.45f, 0.4f));
            }

            if (view.ShowGroupRow)
            {
                foreach (var group in device.Groups)
                {
                    var chip = new Rect2(group.X + 1, nameH + 1, group.Width - 2, groupH - 2);
                    DrawRect(chip, group.Collapsed ? chipCollapsed : chipBg);
                    DrawRect(chip, border, false, 1);
                    string label = $"{group.FirstHw + 1}–{group.LastHw + 1}";
                    if (group.Collapsed)
                        label = "▸ " + label;
                    else
                        label = "▾ " + label;
                    if (font != null)
                    {
                        DrawString(font, new Vector2(group.X + 3, nameH + 14),
                            Truncate(font, label, group.Width - 6, 10),
                            HorizontalAlignment.Left, group.Width - 6, 10, muted);
                    }
                }
            }

            float cx = device.X;
            foreach (var group in device.Groups)
            {
                if (group.Collapsed)
                {
                    if (font != null)
                    {
                        DrawString(font, new Vector2(cx + 2, numberTop + 13),
                            $"{group.FirstHw + 1}–{group.LastHw + 1}",
                            HorizontalAlignment.Left, group.Width - 4, 9, muted);
                    }
                    cx += group.Width;
                    continue;
                }

                for (int i = 0; i < group.OutputCount; i++)
                {
                    int hw = group.FirstHw + i;
                    if (font != null)
                    {
                        string n = (hw + 1).ToString();
                        DrawString(font, new Vector2(cx, numberTop + 13), n,
                            HorizontalAlignment.Center, PatchMatrixMetrics.CellSize, 10, text);
                    }
                    cx += PatchMatrixMetrics.CellSize;
                }
            }
        }
    }

    private static Rect2 RemoveRect(PatchMatrixDeviceSpan device)
    {
        float w = Math.Min(18f, device.Width);
        return new Rect2(device.X + device.Width - w, 2, w, PatchMatrixMetrics.DeviceNameRow - 4);
    }

    private static string Truncate(Font font, string text, float width, int fontSize)
    {
        if (string.IsNullOrEmpty(text) || font == null || width <= 8)
            return text ?? string.Empty;
        if (font.GetStringSize(text, HorizontalAlignment.Left, -1, fontSize).X <= width)
            return text;
        string ellipsis = "…";
        for (int len = text.Length - 1; len > 0; len--)
        {
            string candidate = text.Substring(0, len) + ellipsis;
            if (font.GetStringSize(candidate, HorizontalAlignment.Left, -1, fontSize).X <= width)
                return candidate;
        }
        return ellipsis;
    }
}
