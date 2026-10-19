// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using Cue2.Services;
using Cue2.UI.Utilities;
using Godot;

namespace Cue2.UI.Shell;

/// <summary>
/// Full-table column or row divider for the button board. Dragging resizes every cell on that axis.
/// </summary>
public partial class ButtonBoardResizeHandle : Control
{
    /// <summary>Pointer hit thickness in pixels, centred on the cell edge.</summary>
    public const float HitSize = 8f;

    private const float LineWidth = 1f;
    private const float ActiveLineWidth = 2f;

    private ButtonBoard _board;
    private bool _hovered;
    private bool _dragging;

    /// <summary>True for a column divider (vertical line); false for a row divider.</summary>
    public bool Vertical { get; private set; }

    /// <summary>Column index (right edge) or row index (bottom edge) this handle resizes.</summary>
    public int Index { get; private set; }

    /// <inheritdoc />
    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Stop;
        FocusMode = FocusModeEnum.None;
        GuiInput += OnGuiInput;
        MouseEntered += OnMouseEntered;
        MouseExited += OnMouseExited;
    }

    /// <inheritdoc />
    public override void _ExitTree()
    {
        GuiInput -= OnGuiInput;
        MouseEntered -= OnMouseEntered;
        MouseExited -= OnMouseExited;
        _board = null;
        base._ExitTree();
    }

    /// <summary>
    /// Attaches this handle to <paramref name="board"/> as a column or row divider.
    /// </summary>
    /// <param name="board">Owning button board.</param>
    /// <param name="vertical">True for a column divider.</param>
    /// <param name="index">Column or row being resized.</param>
    public void Bind(ButtonBoard board, bool vertical, int index)
    {
        _board = board;
        Vertical = vertical;
        Index = index;
        MouseDefaultCursorShape = vertical ? CursorShape.Hsize : CursorShape.Vsize;
        string key = vertical
            ? "Resize column (Ctrl: all columns)"
            : "Resize row (Ctrl: all rows)";
        TooltipText = UiLocalizer.T(key);
        SetMeta(UiLocalizer.MetaTooltip, key);
        QueueRedraw();
    }

    /// <summary>Enables or disables pointer interaction (off in Show Mode).</summary>
    public void SetInteractable(bool on)
    {
        Visible = on;
        MouseFilter = on ? MouseFilterEnum.Stop : MouseFilterEnum.Ignore;
        if (!on)
            _hovered = false;
    }

    /// <summary>Keeps the divider highlighted while a drag is in progress.</summary>
    public void SetDragging(bool on)
    {
        if (_dragging == on)
            return;
        _dragging = on;
        QueueRedraw();
    }

    /// <inheritdoc />
    public override void _Draw()
    {
        bool active = _dragging || _hovered;
        Color color = active
            ? GlobalStyles.HighColor1
            : new Color(0.32f, 0.36f, 0.38f, 0.55f);
        float thickness = active ? ActiveLineWidth : LineWidth;
        if (Vertical)
        {
            float x = (Size.X - thickness) * 0.5f;
            DrawRect(new Rect2(x, 0f, thickness, Size.Y), color);
        }
        else
        {
            float y = (Size.Y - thickness) * 0.5f;
            DrawRect(new Rect2(0f, y, Size.X, thickness), color);
        }
    }

    private void OnGuiInput(InputEvent @event)
    {
        if (@event is InputEventMouseButton mb)
        {
            if (mb.ButtonIndex != MouseButton.Left || !mb.Pressed)
                return;
            _board?.BeginAxisResize(this, mb.GlobalPosition);
            AcceptEvent();
            return;
        }

        if (@event is InputEventMouseMotion motion && _dragging)
        {
            _board?.UpdateAxisResizeFromHandle(motion.GlobalPosition);
            AcceptEvent();
        }
    }

    private void OnMouseEntered()
    {
        if (MouseFilter == MouseFilterEnum.Ignore)
            return;
        _hovered = true;
        QueueRedraw();
    }

    private void OnMouseExited()
    {
        _hovered = false;
        QueueRedraw();
    }
}
