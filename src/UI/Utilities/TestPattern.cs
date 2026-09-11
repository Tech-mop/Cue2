// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using Godot;

namespace Cue2.UI.Utilities;

/// <summary>
/// Full-field alignment test pattern (border, grids, center marks, name/resolution labels).
/// </summary>
/// <remarks>
/// Use <see cref="ApplyLayout"/> for all size/position/name changes after construction so
/// Control geometry and labels stay in sync (setting <see cref="PatternSize"/> alone is not enough).
/// </remarks>
public partial class TestPattern : Control
{
    private const float BorderWidth = 10f;
    private const float FineGridStep = 10f;
    private const float CoarseGridStep = 100f;
    private const float CenterLineWidth = 4f;
    private const float CircleLineWidth = 2f;
    private const float OuterCircleLineWidth = 1f;
    private const float CenterHoleRatio = 0.2f;

    private Label _nameLabel;
    private Label _resLabel;
    private string _displayName = "";

    /// <summary>Last applied size (mirrors <see cref="Control.Size"/> after layout).</summary>
    public Vector2I PatternSize { get; private set; }

    /// <summary>Last applied position (mirrors <see cref="Control.Position"/> after layout).</summary>
    public Vector2I PatternPosition { get; private set; }

    /// <summary>Label text shown at the pattern center.</summary>
    public string PatternName
    {
        get => _displayName;
        set
        {
            _displayName = value ?? "";
            RefreshLabels();
        }
    }

    /// <summary>
    /// Creates an empty pattern. Call <see cref="ApplyLayout"/> before or after adding to the tree.
    /// </summary>
    public TestPattern()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        ClipContents = true;
    }

    /// <summary>
    /// Creates a pattern with initial geometry and name.
    /// </summary>
    /// <param name="patternSize">Pattern size in pixels.</param>
    /// <param name="patternPosition">Top-left position in the parent.</param>
    /// <param name="name">Display name (layer or screen).</param>
    public TestPattern(Vector2I patternSize, Vector2I patternPosition, string name = "")
        : this()
    {
        ApplyLayout(patternSize, patternPosition, name);
    }

    /// <inheritdoc />
    public override void _Ready()
    {
        EnsureLabels();
        RefreshLabels();
        QueueRedraw();
    }

    /// <summary>
    /// Updates size, position, and optional name, then redraws.
    /// Safe to call before or after the node enters the tree.
    /// </summary>
    /// <param name="size">New size in pixels (clamped to at least 1×1).</param>
    /// <param name="position">New top-left position in parent space.</param>
    /// <param name="name">When non-null, replaces the display name.</param>
    public void ApplyLayout(Vector2I size, Vector2I position, string name = null)
    {
        if (name != null)
            _displayName = name;

        PatternSize = new Vector2I(Mathf.Max(1, size.X), Mathf.Max(1, size.Y));
        PatternPosition = position;

        // Control.Size/Position drive _Draw and layout; Pattern* alone is not enough.
        Size = PatternSize;
        Position = PatternPosition;

        EnsureLabels();
        RefreshLabels();
        QueueRedraw();
    }

    /// <inheritdoc />
    public override void _Draw()
    {
        Vector2 size = Size;
        if (size.X < 1f || size.Y < 1f)
            return;

        Vector2 center = size * 0.5f;
        float minDim = Mathf.Min(size.X, size.Y);
        float holeRadius = CenterHoleRatio * minDim;

        // Outer border
        DrawRect(new Rect2(Vector2.Zero, size), Colors.Red, filled: false, width: BorderWidth);

        // Fine grid (10px), coarse grid (100px), both skip the center hole
        DrawGrid(FineGridStep, new Color(0.5f, 0.5f, 0.5f, 0.5f), 1f, center, holeRadius, size);
        DrawGrid(CoarseGridStep, Colors.White, 1f, center, holeRadius, size);

        // Center cross (clipped by hole) + diagonals
        DrawVerticalLine(center.X, Colors.Blue, CenterLineWidth, center, holeRadius, size);
        DrawHorizontalLine(center.Y, Colors.Blue, CenterLineWidth, center, holeRadius, size);
        DrawClippedLine(Vector2.Zero, size, Colors.Blue, CenterLineWidth, center, holeRadius);
        DrawClippedLine(new Vector2(size.X, 0), new Vector2(0, size.Y), Colors.Blue, CenterLineWidth, center, holeRadius);

        // Circles
        DrawArc(center, holeRadius, 0, Mathf.Tau, 64, Colors.Blue, CircleLineWidth, antialiased: true);
        DrawArc(center, minDim * 0.5f, 0, Mathf.Tau, 64, Colors.Red, OuterCircleLineWidth, antialiased: true);
    }

    private void EnsureLabels()
    {
        if (_nameLabel != null && IsInstanceValid(_nameLabel)
            && _resLabel != null && IsInstanceValid(_resLabel))
            return;

        // Rebuild if freed or first call
        if (_nameLabel != null && IsInstanceValid(_nameLabel))
            _nameLabel.QueueFree();
        if (_resLabel != null && IsInstanceValid(_resLabel))
            _resLabel.QueueFree();

        _nameLabel = CreateCenteredLabel();
        _resLabel = CreateCenteredLabel();
        AddChild(_nameLabel);
        AddChild(_resLabel);
    }

    private static Label CreateCenteredLabel()
    {
        return new Label
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            MouseFilter = MouseFilterEnum.Ignore
        };
    }

    private void RefreshLabels()
    {
        if (_nameLabel == null || !IsInstanceValid(_nameLabel)
            || _resLabel == null || !IsInstanceValid(_resLabel))
            return;

        float w = Mathf.Max(1f, Size.X);
        float h = Mathf.Max(1f, Size.Y);
        float midY = h * 0.5f;

        _nameLabel.Text = _displayName;
        _nameLabel.Position = new Vector2(0, midY - 30);
        _nameLabel.Size = new Vector2(w, 20);

        _resLabel.Text = $"{Mathf.RoundToInt(w)}x{Mathf.RoundToInt(h)}";
        _resLabel.Position = new Vector2(0, midY + 10);
        _resLabel.Size = new Vector2(w, 20);
    }

    private void DrawGrid(float step, Color color, float width, Vector2 center, float radius, Vector2 size)
    {
        if (step <= 0f)
            return;

        int maxI = Mathf.CeilToInt(Mathf.Max(size.X, size.Y) / step) + 1;
        for (int i = 1; i <= maxI; i++)
        {
            float xPos = center.X + i * step;
            if (xPos < size.X)
                DrawVerticalLine(xPos, color, width, center, radius, size);
            float xNeg = center.X - i * step;
            if (xNeg >= 0)
                DrawVerticalLine(xNeg, color, width, center, radius, size);

            float yPos = center.Y + i * step;
            if (yPos < size.Y)
                DrawHorizontalLine(yPos, color, width, center, radius, size);
            float yNeg = center.Y - i * step;
            if (yNeg >= 0)
                DrawHorizontalLine(yNeg, color, width, center, radius, size);
        }
    }

    private void DrawVerticalLine(float x, Color color, float width, Vector2 center, float radius, Vector2 size)
    {
        float dx = x - center.X;
        if (Mathf.Abs(dx) > radius)
        {
            DrawLine(new Vector2(x, 0), new Vector2(x, size.Y), color, width);
            return;
        }

        float dy = Mathf.Sqrt(radius * radius - dx * dx);
        float y1 = center.Y - dy;
        float y2 = center.Y + dy;
        if (y1 > 0)
            DrawLine(new Vector2(x, 0), new Vector2(x, y1), color, width);
        if (y2 < size.Y)
            DrawLine(new Vector2(x, y2), new Vector2(x, size.Y), color, width);
    }

    private void DrawHorizontalLine(float y, Color color, float width, Vector2 center, float radius, Vector2 size)
    {
        float dy = y - center.Y;
        if (Mathf.Abs(dy) > radius)
        {
            DrawLine(new Vector2(0, y), new Vector2(size.X, y), color, width);
            return;
        }

        float dx = Mathf.Sqrt(radius * radius - dy * dy);
        float x1 = center.X - dx;
        float x2 = center.X + dx;
        if (x1 > 0)
            DrawLine(new Vector2(0, y), new Vector2(x1, y), color, width);
        if (x2 < size.X)
            DrawLine(new Vector2(x2, y), new Vector2(size.X, y), color, width);
    }

    private void DrawClippedLine(Vector2 a, Vector2 b, Color color, float width, Vector2 center, float radius)
    {
        Vector2 d = b - a;
        Vector2 c = a - center;
        float aa = d.Dot(d);
        if (aa < 1e-6f)
            return;

        float bb = 2f * c.Dot(d);
        float cc = c.Dot(c) - radius * radius;
        float discriminant = bb * bb - 4f * aa * cc;
        if (discriminant < 0)
        {
            DrawLine(a, b, color, width, antialiased: true);
            return;
        }

        float sqrtD = Mathf.Sqrt(discriminant);
        float s1 = (-bb - sqrtD) / (2f * aa);
        float s2 = (-bb + sqrtD) / (2f * aa);
        if (s1 > s2)
            (s1, s2) = (s2, s1);

        if (s1 > 0)
            DrawLine(a, a + s1 * d, color, width);
        if (s2 < 1)
            DrawLine(a + s2 * d, b, color, width);
    }
}
