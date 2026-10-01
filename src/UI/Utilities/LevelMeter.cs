// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using Godot;

namespace Cue2.UI.Utilities;

/// <summary>
/// Horizontal peak meter for input or output levels. <see cref="Level"/> is linear amplitude, 0…1.
/// </summary>
public partial class LevelMeter : Control
{
    private float _level;

    /// <summary>Linear peak, 0…1. Redraws when the value moves.</summary>
    public float Level
    {
        get => _level;
        set
        {
            float next = Mathf.Clamp(value, 0f, 1f);
            if (Mathf.Abs(next - _level) < 0.004f)
                return;
            _level = next;
            QueueRedraw();
        }
    }

    /// <summary>Creates a meter with a fixed track size.</summary>
    public LevelMeter()
    {
        CustomMinimumSize = new Vector2(110, 16);
        MouseFilter = MouseFilterEnum.Ignore;
        SizeFlagsVertical = SizeFlags.ShrinkCenter;
    }

    /// <inheritdoc />
    public override void _Notification(int what)
    {
        if (what == NotificationResized)
            QueueRedraw();
    }

    /// <inheritdoc />
    public override void _Draw()
    {
        var rect = new Rect2(Vector2.Zero, Size);
        if (rect.Size.X < 1f || rect.Size.Y < 1f)
            return;

        DrawRect(rect, new Color(0.08f, 0.08f, 0.08f));
        float width = rect.Size.X * _level;
        if (width < 0.5f)
            return;

        Color color = _level >= 0.9f
            ? new Color(0.9f, 0.25f, 0.2f)
            : _level >= 0.7f
                ? new Color(0.9f, 0.75f, 0.2f)
                : new Color(0.25f, 0.75f, 0.4f);
        DrawRect(new Rect2(0f, 0f, width, rect.Size.Y), color);
    }
}
