// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System;
using Cue2.Services;
using Godot;

namespace Cue2.UI.Utilities;

/// <summary>
/// Click-and-hold vertical drag slider for <see cref="LineEdit"/> fields that display dB values.
/// </summary>
/// <remarks>
/// <para>
/// Enable on any volume/dB field with a single call:
/// <code>
/// LineEditDbDragSlider.Enable(volumeLineEdit);
/// </code>
/// </para>
/// <para>
/// Behavior: press and hold (or press and drag vertically) on the field to show a vertical
/// fader beside it. While the button is held, vertical mouse movement scrubs the dB value
/// and updates the LineEdit text. On release the fader hides and the value is committed
/// (default: emits <see cref="LineEdit.TextSubmitted"/> so existing handlers run once).
/// Horizontal drag is left alone for normal text selection.
/// </para>
/// <para>
/// The behavior node is parented to the LineEdit and frees with it. Re-calling
/// <see cref="Enable"/> updates configuration on an existing attachment.
/// </para>
/// </remarks>
public partial class LineEditDbDragSlider : Node
{
    /// <summary>Child node name used for idempotent <see cref="Enable"/>.</summary>
    public const string ChildName = "DbDragSlider";

    /// <summary>Default practical floor (−60 dB).</summary>
    public const float DefaultMinDb = -60f;

    /// <summary>
    /// Default ceiling for cue component volume fields (digital gain up to +12 dB).
    /// </summary>
    public const float DefaultMaxDb = 12f;

    /// <summary>Unity ceiling (0 dBFS) for master / routing matrix fields that must not boost.</summary>
    public const float UnityMaxDb = 0f;

    private const float DefaultHoldDelaySec = 0.18f;
    private const float DefaultActivateDragPx = 5f;
    private const float DefaultPixelsPerDb = 1.375f;
    private const float DefaultSliderHeight = 160f;
    private const float DefaultSliderWidth = 28f;
    /// <summary>Fixed overlay width (fits longest dB strings like −60.0dB without growing).</summary>
    private const float OverlayFixedWidth = 48f;
    private const float CancelHorizontalPx = 10f;

    private LineEdit _field;
    private Config _config = new();

    private bool _buttonDown;
    private bool _dragging;
    private double _holdElapsed;
    private Vector2 _pressGlobal;
    private float _lastMouseY;
    private float _startDb;
    private float _currentDb;
    private Control.CursorShape _savedCursor;

    private PanelContainer _overlay;
    private VSlider _slider;
    private Label _valueLabel;

    /// <summary>
    /// Optional configuration for range, formatting, and commit behavior.
    /// </summary>
    public sealed class Config
    {
        /// <summary>Minimum dB (bottom of fader). Default −60.</summary>
        public float MinDb { get; set; } = DefaultMinDb;

        /// <summary>Maximum dB (top of fader). Default 0.</summary>
        public float MaxDb { get; set; } = DefaultMaxDb;

        /// <summary>Quantization step while scrubbing. Default 0.1 dB.</summary>
        public float StepDb { get; set; } = 0.1f;

        /// <summary>Hold time before the fader appears without movement (seconds).</summary>
        public float HoldDelaySec { get; set; } = DefaultHoldDelaySec;

        /// <summary>Vertical pixels of movement that starts the fader immediately.</summary>
        public float ActivateDragPixels { get; set; } = DefaultActivateDragPx;

        /// <summary>Mouse pixels per 1 dB of change (higher = less sensitive).</summary>
        public float PixelsPerDb { get; set; } = DefaultPixelsPerDb;

        /// <summary>Overlay fader height in pixels.</summary>
        public float SliderHeight { get; set; } = DefaultSliderHeight;

        /// <summary>
        /// When true, positive values format with a leading '+' (relative fades).
        /// </summary>
        public bool FormatSigned { get; set; }

        /// <summary>
        /// When true (default), release emits <see cref="LineEdit.TextSubmitted"/> so host
        /// handlers commit once. Set false if you only use <see cref="ValueCommitted"/>.
        /// </summary>
        public bool EmitTextSubmittedOnCommit { get; set; } = true;

        /// <summary>
        /// Optional live callback while scrubbing (does not replace commit).
        /// Use for preview without writing history every frame.
        /// </summary>
        public Action<float> ValueChanged { get; set; }

        /// <summary>
        /// Optional commit callback on mouse release (after text is formatted).
        /// </summary>
        public Action<float> ValueCommitted { get; set; }

        /// <summary>
        /// Custom formatter. Default: one decimal + "dB" (with optional '+').
        /// </summary>
        public Func<float, string> Format { get; set; }

        /// <summary>
        /// Custom parser from LineEdit text. Return null to fall back to MinDb.
        /// </summary>
        public Func<string, float?> Parse { get; set; }
    }

    /// <summary>
    /// Attaches click-hold dB scrubbing to <paramref name="field"/> (idempotent).
    /// </summary>
    /// <param name="field">Target LineEdit (volume / matrix / master, etc.).</param>
    /// <param name="config">Optional range and commit overrides.</param>
    /// <returns>The behavior node, or null if <paramref name="field"/> is invalid.</returns>
    public static LineEditDbDragSlider Enable(LineEdit field, Config config = null)
    {
        if (field == null || !GodotObject.IsInstanceValid(field))
            return null;

        var existing = field.GetNodeOrNull<LineEditDbDragSlider>(ChildName);
        if (existing != null)
        {
            existing.ApplyConfig(config ?? new Config());
            return existing;
        }

        var node = new LineEditDbDragSlider
        {
            Name = ChildName
        };
        node.ApplyConfig(config ?? new Config());
        field.AddChild(node);
        return node;
    }

    /// <summary>
    /// Convenience: cue component / embedded-audio volume range (−60…+12 dB) with digital gain.
    /// Positive values format with a leading '+'.
    /// </summary>
    /// <param name="field">Target LineEdit.</param>
    /// <returns>The behavior node, or null if invalid.</returns>
    public static LineEditDbDragSlider EnableVolume(LineEdit field)
    {
        return Enable(field, new Config
        {
            MinDb = DefaultMinDb,
            MaxDb = DefaultMaxDb,
            FormatSigned = true
        });
    }

    /// <summary>
    /// Convenience: unity-gain-only absolute volume (−60…0 dB) for master and routing matrices.
    /// </summary>
    /// <param name="field">Target LineEdit.</param>
    /// <returns>The behavior node, or null if invalid.</returns>
    public static LineEditDbDragSlider EnableUnityVolume(LineEdit field)
    {
        return Enable(field, new Config
        {
            MinDb = DefaultMinDb,
            MaxDb = UnityMaxDb,
            FormatSigned = false
        });
    }

    /// <summary>
    /// Convenience: relative / signed dB range (e.g. control fade levels).
    /// </summary>
    /// <param name="field">Target LineEdit.</param>
    /// <param name="minDb">Floor (default −60).</param>
    /// <param name="maxDb">Ceiling (default +24).</param>
    /// <returns>The behavior node, or null if invalid.</returns>
    public static LineEditDbDragSlider EnableSignedDb(LineEdit field, float minDb = -60f, float maxDb = 24f)
    {
        return Enable(field, new Config
        {
            MinDb = minDb,
            MaxDb = maxDb,
            FormatSigned = true
        });
    }

    /// <summary>
    /// Updates range / callbacks on an already-enabled field.
    /// </summary>
    /// <param name="config">New configuration (null resets to defaults).</param>
    public void ApplyConfig(Config config)
    {
        _config = config ?? new Config();
        if (_config.MinDb > _config.MaxDb)
            (_config.MinDb, _config.MaxDb) = (_config.MaxDb, _config.MinDb);
        if (_config.StepDb <= 0f)
            _config.StepDb = 0.1f;
        if (_config.PixelsPerDb < 0.5f)
            _config.PixelsPerDb = 0.5f;
        if (_slider != null && GodotObject.IsInstanceValid(_slider))
        {
            _slider.MinValue = _config.MinDb;
            _slider.MaxValue = _config.MaxDb;
            _slider.Step = _config.StepDb;
        }
    }

    /// <inheritdoc />
    public override void _Ready()
    {
        _field = GetParentOrNull<LineEdit>();
        if (_field == null)
        {
            GD.PrintErr("LineEditDbDragSlider:_Ready - Parent must be a LineEdit; freeing.");
            QueueFree();
            return;
        }

        _field.GuiInput += OnFieldGuiInput;
        _field.TreeExiting += OnFieldTreeExiting;
        SetProcess(false);
        SetProcessInput(false);
    }

    /// <inheritdoc />
    public override void _ExitTree()
    {
        if (_field != null && GodotObject.IsInstanceValid(_field))
        {
            _field.GuiInput -= OnFieldGuiInput;
            _field.TreeExiting -= OnFieldTreeExiting;
        }

        DestroyOverlay();
        base._ExitTree();
    }

    /// <inheritdoc />
    public override void _Process(double delta)
    {
        if (!_buttonDown || _dragging)
            return;

        if (_field == null || !GodotObject.IsInstanceValid(_field) || !_field.Editable)
        {
            CancelPending();
            return;
        }

        _holdElapsed += delta;
        if (_holdElapsed >= _config.HoldDelaySec)
            BeginDrag();
    }

    /// <inheritdoc />
    public override void _Input(InputEvent @event)
    {
        if (!_buttonDown)
            return;

        if (@event is InputEventMouseMotion motion)
        {
            if (!_dragging)
            {
                float dy = Mathf.Abs(motion.GlobalPosition.Y - _pressGlobal.Y);
                float dx = Mathf.Abs(motion.GlobalPosition.X - _pressGlobal.X);

                // Prefer vertical scrub over text selection once the user moves enough.
                if (dy >= _config.ActivateDragPixels && dy >= dx)
                {
                    BeginDrag();
                }
                else if (dx >= CancelHorizontalPx && dx > dy)
                {
                    // Horizontal intent: allow normal LineEdit selection.
                    CancelPending();
                    return;
                }
            }

            if (_dragging)
            {
                float deltaY = motion.GlobalPosition.Y - _lastMouseY;
                _lastMouseY = motion.GlobalPosition.Y;
                // Mouse up → louder (higher dB).
                float next = _currentDb - (deltaY / _config.PixelsPerDb);
                ApplyDb(next, notifyLive: true);
                GetViewport()?.SetInputAsHandled();
            }
        }
        else if (@event is InputEventMouseButton button
                 && button.ButtonIndex == MouseButton.Left
                 && !button.Pressed)
        {
            if (_dragging)
                EndDrag(commit: true);
            else
                CancelPending();
        }
        else if (@event is InputEventKey key && key.Pressed && key.Keycode == Key.Escape && _dragging)
        {
            // Escape cancels scrub and restores the value at press.
            ApplyDb(_startDb, notifyLive: false);
            EndDrag(commit: false);
            GetViewport()?.SetInputAsHandled();
        }
    }

    private void OnFieldGuiInput(InputEvent @event)
    {
        if (_field == null || !GodotObject.IsInstanceValid(_field) || !_field.Editable)
            return;

        if (@event is not InputEventMouseButton button || button.ButtonIndex != MouseButton.Left)
            return;

        if (button.Pressed)
        {
            // Fresh press — arm hold / drag (do not steal click for caret placement yet).
            _buttonDown = true;
            _dragging = false;
            _holdElapsed = 0;
            _pressGlobal = button.GlobalPosition;
            _lastMouseY = button.GlobalPosition.Y;
            SetProcess(true);
            SetProcessInput(true);
        }
        else if (_dragging)
        {
            // Release over the field while scrubbing (also handled in _Input).
            EndDrag(commit: true);
            _field.AcceptEvent();
        }
    }

    private void OnFieldTreeExiting()
    {
        CancelPending();
        DestroyOverlay();
    }

    private void BeginDrag()
    {
        if (_dragging || _field == null || !GodotObject.IsInstanceValid(_field))
            return;

        _dragging = true;
        _holdElapsed = 0;
        SetProcess(false);

        _startDb = ParseFieldDb();
        _currentDb = _startDb;
        _lastMouseY = _field.GetGlobalMousePosition().Y;

        try
        {
            _field.Deselect();
        }
        catch
        {
            // Older bindings / edge cases — ignore.
        }

        _savedCursor = _field.MouseDefaultCursorShape;
        _field.MouseDefaultCursorShape = Control.CursorShape.Vsize;

        EnsureOverlay();
        PositionOverlay();
        ApplyDb(_currentDb, notifyLive: false);
        if (_overlay != null)
            _overlay.Visible = true;
    }

    private void EndDrag(bool commit)
    {
        if (!_buttonDown && !_dragging)
            return;

        bool wasDragging = _dragging;
        float committed = _currentDb;

        _buttonDown = false;
        _dragging = false;
        _holdElapsed = 0;
        SetProcess(false);
        SetProcessInput(false);

        if (_field != null && GodotObject.IsInstanceValid(_field))
            _field.MouseDefaultCursorShape = _savedCursor;

        if (_overlay != null && GodotObject.IsInstanceValid(_overlay))
            _overlay.Visible = false;

        if (!wasDragging || _field == null || !GodotObject.IsInstanceValid(_field))
            return;

        string text = FormatDb(committed);
        _field.Text = text;

        if (commit)
        {
            try
            {
                _config.ValueCommitted?.Invoke(committed);
            }
            catch (Exception ex)
            {
                GD.PrintErr($"LineEditDbDragSlider:EndDrag - ValueCommitted error: {ex.Message}");
            }

            if (_config.EmitTextSubmittedOnCommit)
            {
                // Single commit through existing TextSubmitted / FocusExited-style handlers.
                _field.EmitSignal(LineEdit.SignalName.TextSubmitted, text);
            }
        }
    }

    private void CancelPending()
    {
        _buttonDown = false;
        _dragging = false;
        _holdElapsed = 0;
        SetProcess(false);
        SetProcessInput(false);

        if (_field != null && GodotObject.IsInstanceValid(_field))
            _field.MouseDefaultCursorShape = _savedCursor;

        if (_overlay != null && GodotObject.IsInstanceValid(_overlay))
            _overlay.Visible = false;
    }

    private void ApplyDb(float db, bool notifyLive)
    {
        float stepped = Snap(db);
        _currentDb = stepped;

        if (_field != null && GodotObject.IsInstanceValid(_field))
            _field.Text = FormatDb(stepped);

        if (_slider != null && GodotObject.IsInstanceValid(_slider))
            _slider.SetValueNoSignal(stepped);

        if (_valueLabel != null && GodotObject.IsInstanceValid(_valueLabel))
            _valueLabel.Text = FormatDb(stepped);

        if (notifyLive)
        {
            try
            {
                _config.ValueChanged?.Invoke(stepped);
            }
            catch (Exception ex)
            {
                GD.PrintErr($"LineEditDbDragSlider:ApplyDb - ValueChanged error: {ex.Message}");
            }
        }
    }

    private float Snap(float db)
    {
        float min = _config.MinDb;
        float max = _config.MaxDb;
        float step = _config.StepDb;
        db = Mathf.Clamp(db, min, max);
        if (step > 0f)
        {
            float steps = MathF.Round((db - min) / step);
            db = min + steps * step;
            db = Mathf.Clamp(db, min, max);
            // Avoid float dust like -0.0001
            db = MathF.Round(db / step) * step;
        }

        return db;
    }

    private float ParseFieldDb()
    {
        string text = _field?.Text ?? string.Empty;
        if (_config.Parse != null)
        {
            float? custom = _config.Parse(text);
            if (custom.HasValue)
                return Snap(custom.Value);
        }

        float? parsed = DefaultParseDb(text);
        return Snap(parsed ?? _config.MinDb);
    }

    private string FormatDb(float db)
    {
        if (_config.Format != null)
            return _config.Format(db);

        db = Snap(db);
        if (_config.FormatSigned && db > 0f)
            return $"+{db:0.0}dB";
        return $"{db:0.0}dB";
    }

    /// <summary>
    /// Default parser: strips "dB", optional leading '+', returns null if empty/invalid.
    /// </summary>
    public static float? DefaultParseDb(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        string cleaned = text.Trim();
        cleaned = cleaned.Replace("dB", "", StringComparison.OrdinalIgnoreCase)
            .Replace("db", "", StringComparison.OrdinalIgnoreCase)
            .Trim();
        if (cleaned.StartsWith('+'))
            cleaned = cleaned[1..].Trim();

        if (!float.TryParse(cleaned, out float db))
            return null;
        return db;
    }

    private void EnsureOverlay()
    {
        if (_overlay != null && GodotObject.IsInstanceValid(_overlay))
            return;

        _overlay = new PanelContainer
        {
            Name = "DbDragOverlay",
            Visible = false,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            // Draw in viewport space so the fader is not clipped by ScrollContainers.
            TopLevel = true,
            ZIndex = 100,
            ClipContents = true,
            CustomMinimumSize = new Vector2(OverlayFixedWidth, _config.SliderHeight)
        };

        var panelStyle = new StyleBoxFlat
        {
            BgColor = new Color(0.08f, 0.09f, 0.1f, 0.94f),
            BorderColor = GlobalStyles.LowColor2,
            BorderWidthLeft = 1,
            BorderWidthTop = 1,
            BorderWidthRight = 1,
            BorderWidthBottom = 1,
            CornerRadiusTopLeft = 4,
            CornerRadiusTopRight = 4,
            CornerRadiusBottomRight = 4,
            CornerRadiusBottomLeft = 4,
            ContentMarginLeft = 3,
            ContentMarginTop = 6,
            ContentMarginRight = 3,
            ContentMarginBottom = 6
        };
        _overlay.AddThemeStyleboxOverride("panel", panelStyle);

        var vbox = new VBoxContainer
        {
            Alignment = BoxContainer.AlignmentMode.Center,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            SizeFlagsHorizontal = Control.SizeFlags.Fill,
            SizeFlagsVertical = Control.SizeFlags.Fill
        };
        vbox.AddThemeConstantOverride("separation", 3);

        // Value readout lives in a fixed-size slot so digit count never widens the overlay
        // (Label min-size tracks full text; a plain Control parent does not).
        float labelSlotW = OverlayFixedWidth - 10f;
        var valueSlot = new Control
        {
            CustomMinimumSize = new Vector2(labelSlotW, 14f),
            SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            ClipContents = true
        };
        _valueLabel = new Label
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            Text = "0.0dB",
            ClipText = true,
            TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis
        };
        _valueLabel.AddThemeFontSizeOverride("font_size", 9);
        _valueLabel.AddThemeColorOverride("font_color", new Color(0.85f, 0.9f, 0.92f));
        valueSlot.AddChild(_valueLabel);
        _valueLabel.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);

        _slider = new VSlider
        {
            MinValue = _config.MinDb,
            MaxValue = _config.MaxDb,
            Step = _config.StepDb,
            Value = _config.MinDb,
            Editable = false,
            Scrollable = false,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(DefaultSliderWidth - 8f, _config.SliderHeight - 40f)
        };

        // Accent the grabber so the live level is obvious.
        try
        {
            var grabber = new StyleBoxFlat
            {
                BgColor = GlobalStyles.LowColor1,
                CornerRadiusTopLeft = 3,
                CornerRadiusTopRight = 3,
                CornerRadiusBottomRight = 3,
                CornerRadiusBottomLeft = 3,
                ContentMarginLeft = 2,
                ContentMarginTop = 2,
                ContentMarginRight = 2,
                ContentMarginBottom = 2
            };
            _slider.AddThemeStyleboxOverride("slider", new StyleBoxFlat
            {
                BgColor = new Color(0.15f, 0.17f, 0.18f, 1f),
                CornerRadiusTopLeft = 2,
                CornerRadiusTopRight = 2,
                CornerRadiusBottomRight = 2,
                CornerRadiusBottomLeft = 2,
                ContentMarginLeft = 4,
                ContentMarginRight = 4
            });
            _slider.AddThemeStyleboxOverride("grabber_area", new StyleBoxFlat
            {
                BgColor = new Color(GlobalStyles.LowColor3.R, GlobalStyles.LowColor3.G, GlobalStyles.LowColor3.B, 0.85f),
                CornerRadiusTopLeft = 2,
                CornerRadiusTopRight = 2,
                CornerRadiusBottomRight = 2,
                CornerRadiusBottomLeft = 2
            });
            _slider.AddThemeStyleboxOverride("grabber_area_highlight", new StyleBoxFlat
            {
                BgColor = new Color(GlobalStyles.LowColor2.R, GlobalStyles.LowColor2.G, GlobalStyles.LowColor2.B, 0.9f),
                CornerRadiusTopLeft = 2,
                CornerRadiusTopRight = 2,
                CornerRadiusBottomRight = 2,
                CornerRadiusBottomLeft = 2
            });
            _slider.AddThemeStyleboxOverride("grabber", grabber);
            _slider.AddThemeStyleboxOverride("grabber_highlight", grabber);
        }
        catch
        {
            // Theme overrides are cosmetic.
        }

        var maxSlot = new Control
        {
            CustomMinimumSize = new Vector2(labelSlotW, 11f),
            SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            ClipContents = true
        };
        var maxHint = new Label
        {
            Text = FormatBoundLabel(_config.MaxDb),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            ClipText = true
        };
        maxHint.AddThemeFontSizeOverride("font_size", 8);
        maxHint.AddThemeColorOverride("font_color", GlobalStyles.SoftFontColor);
        maxSlot.AddChild(maxHint);
        maxHint.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);

        var minSlot = new Control
        {
            CustomMinimumSize = new Vector2(labelSlotW, 11f),
            SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            ClipContents = true
        };
        var minHint = new Label
        {
            Text = FormatBoundLabel(_config.MinDb),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            ClipText = true
        };
        minHint.AddThemeFontSizeOverride("font_size", 8);
        minHint.AddThemeColorOverride("font_color", GlobalStyles.SoftFontColor);
        minSlot.AddChild(minHint);
        minHint.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);

        vbox.AddChild(maxSlot);
        vbox.AddChild(valueSlot);
        vbox.AddChild(_slider);
        vbox.AddChild(minSlot);
        _overlay.AddChild(vbox);

        // Parent to the field so lifetime tracks the control; TopLevel draws unclipped.
        _field.AddChild(_overlay);
        _overlay.Size = new Vector2(OverlayFixedWidth, _config.SliderHeight);
    }

    private static string FormatBoundLabel(float db)
    {
        if (Mathf.IsEqualApprox(db, 0f))
            return "0";
        if (db > 0f)
            return $"+{db:0.#}";
        return $"{db:0.#}";
    }

    private void PositionOverlay()
    {
        if (_overlay == null || !GodotObject.IsInstanceValid(_overlay) || _field == null)
            return;

        float height = Mathf.Max(120f, _config.SliderHeight);
        const float width = OverlayFixedWidth;
        _overlay.CustomMinimumSize = new Vector2(width, height);
        _overlay.Size = new Vector2(width, height);

        Vector2 fieldGlobal = _field.GlobalPosition;
        Vector2 fieldSize = _field.Size;
        float centerY = fieldGlobal.Y + fieldSize.Y * 0.5f;
        float y = centerY - height * 0.5f;

        // Prefer left of the field; fall back to right if near the left screen edge.
        const float gap = 6f;
        float leftX = fieldGlobal.X - width - gap;
        float rightX = fieldGlobal.X + fieldSize.X + gap;
        float x = leftX >= 4f ? leftX : rightX;

        // Clamp vertically into the visible viewport.
        var viewport = _field.GetViewport();
        if (viewport != null)
        {
            var vr = viewport.GetVisibleRect();
            y = Mathf.Clamp(y, vr.Position.Y + 4f, vr.Position.Y + vr.Size.Y - height - 4f);
            if (x + width > vr.Position.X + vr.Size.X - 4f)
                x = fieldGlobal.X - width - gap;
            if (x < vr.Position.X + 4f)
                x = rightX;
        }

        _overlay.GlobalPosition = new Vector2(x, y);
    }

    private void DestroyOverlay()
    {
        if (_overlay != null && GodotObject.IsInstanceValid(_overlay))
            _overlay.QueueFree();
        _overlay = null;
        _slider = null;
        _valueLabel = null;
    }
}
