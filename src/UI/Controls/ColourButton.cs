// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System;
using Cue2.Services;
using Cue2.UI.Utilities;
using Godot;

namespace Cue2.UI.Controls;

/// <summary>
/// Compact colour chip that opens a Cue2 picker: preview, HSV/RGB sliders, and preset swatches.
/// </summary>
/// <remarks>
/// Drop-in for Godot's <see cref="ColorPickerButton"/>: <see cref="Color"/>, <see cref="EditAlpha"/>,
/// <see cref="ColorChanged"/>, and <see cref="PopupClosed"/>. The popup copies the host window's
/// content scale so it matches Cue2 UI scale on high-DPI displays.
/// </remarks>
public partial class ColourButton : Button
{
    /// <summary>Raised while the user edits (slider drag or preset click).</summary>
    [Signal]
    public delegate void ColorChangedEventHandler(Color color);

    /// <summary>Raised when the picker popup closes.</summary>
    [Signal]
    public delegate void PopupClosedEventHandler();

    /// <summary>
    /// White, black, then saturated theatrical colours (S=100%, V=80%):
    /// red, amber, gold, green, turquoise, blue, purple, magenta.
    /// </summary>
    public static readonly Color[] Presets =
    {
        Colors.White,
        Colors.Black,
        Color.FromHsv(0f / 360f, 1f, 0.8f),   // red
        Color.FromHsv(25f / 360f, 1f, 0.8f),  // amber
        Color.FromHsv(51f / 360f, 1f, 0.8f),  // gold
        Color.FromHsv(120f / 360f, 1f, 0.8f), // green
        Color.FromHsv(174f / 360f, 1f, 0.8f), // turquoise
        Color.FromHsv(240f / 360f, 1f, 0.8f), // blue
        Color.FromHsv(270f / 360f, 1f, 0.8f), // purple
        Color.FromHsv(300f / 360f, 1f, 0.8f), // magenta
    };

    private const float PresetSizePx = 28f;
    private const int PresetSeparationPx = 4;
    private const int PresetColumns = 5;

    private Color _color = Colors.White;
    private bool _editAlpha;
    private bool _syncingUi;
    private float _hue01;

    private PopupPanel _popup;
    private ColorRect _preview;
    private enum ValueChannel
    {
        Hue,
        Sat,
        Val,
        Red,
        Green,
        Blue,
        Alpha
    }

    private HSlider _hueSlider;
    private HSlider _satSlider;
    private HSlider _valSlider;
    private HSlider _redSlider;
    private HSlider _greenSlider;
    private HSlider _blueSlider;
    private HSlider _alphaSlider;
    private LineEdit _hueEdit;
    private LineEdit _satEdit;
    private LineEdit _valEdit;
    private LineEdit _redEdit;
    private LineEdit _greenEdit;
    private LineEdit _blueEdit;
    private LineEdit _alphaEdit;
    private Control _alphaRow;
    private GridContainer _presetGrid;
    private GridContainer _recentGrid;
    private Control _popupVBox;
    private Button[] _presetButtons = Array.Empty<Button>();
    private Button[] _recentButtons = Array.Empty<Button>();
    private GlobalSignals _globalSignals;
    private UserDataManager _userData;

    /// <summary>Current colour. Setting this does not emit <see cref="ColorChanged"/>.</summary>
    /// <value>RGBA colour shown on the chip and in the popup.</value>
    [Export]
    public Color Color
    {
        get => _color;
        set => SetColor(value, emitChanged: false);
    }

    /// <summary>When true, the popup shows an alpha slider and preserves alpha.</summary>
    /// <value>False for opaque cue / output colours; true for text overlays.</value>
    [Export]
    public bool EditAlpha
    {
        get => _editAlpha;
        set
        {
            _editAlpha = value;
            if (_alphaRow != null)
                _alphaRow.Visible = value;
            if (!value && _color.A < 0.999f)
                SetColor(new Color(_color.R, _color.G, _color.B, 1f), emitChanged: false);
        }
    }

    public override void _Ready()
    {
        Text = string.Empty;
        ClipText = true;
        FocusMode = FocusModeEnum.None;

        _popup = GetNode<PopupPanel>("Popup");
        _popupVBox = GetNode<Control>("Popup/Margin/VBox");
        _preview = GetNode<ColorRect>("Popup/Margin/VBox/Preview");
        _hueSlider = GetNode<HSlider>("Popup/Margin/VBox/HueRow/HueSlider");
        _satSlider = GetNode<HSlider>("Popup/Margin/VBox/SatRow/SatSlider");
        _valSlider = GetNode<HSlider>("Popup/Margin/VBox/ValRow/ValSlider");
        _redSlider = GetNode<HSlider>("Popup/Margin/VBox/RedRow/RedSlider");
        _greenSlider = GetNode<HSlider>("Popup/Margin/VBox/GreenRow/GreenSlider");
        _blueSlider = GetNode<HSlider>("Popup/Margin/VBox/BlueRow/BlueSlider");
        _alphaSlider = GetNode<HSlider>("Popup/Margin/VBox/AlphaRow/AlphaSlider");
        _hueEdit = GetNode<LineEdit>("Popup/Margin/VBox/HueRow/HueEdit");
        _satEdit = GetNode<LineEdit>("Popup/Margin/VBox/SatRow/SatEdit");
        _valEdit = GetNode<LineEdit>("Popup/Margin/VBox/ValRow/ValEdit");
        _redEdit = GetNode<LineEdit>("Popup/Margin/VBox/RedRow/RedEdit");
        _greenEdit = GetNode<LineEdit>("Popup/Margin/VBox/GreenRow/GreenEdit");
        _blueEdit = GetNode<LineEdit>("Popup/Margin/VBox/BlueRow/BlueEdit");
        _alphaEdit = GetNode<LineEdit>("Popup/Margin/VBox/AlphaRow/AlphaEdit");
        _alphaRow = GetNode<Control>("Popup/Margin/VBox/AlphaRow");
        _presetGrid = GetNode<GridContainer>("Popup/Margin/VBox/PresetGrid");
        _recentGrid = GetNode<GridContainer>("Popup/Margin/VBox/RecentGrid");

        _popup.Unresizable = true;
        _popup.Transient = true;
        _popup.PopupWindow = true;
        _popup.WrapControls = true;
        _popup.PopupHide += OnPopupHide;
        _popup.WindowInput += OnPopupWindowInput;

        _hueSlider.ValueChanged += OnHsvSliderChanged;
        _satSlider.ValueChanged += OnHsvSliderChanged;
        _valSlider.ValueChanged += OnHsvSliderChanged;
        _redSlider.ValueChanged += OnRgbSliderChanged;
        _greenSlider.ValueChanged += OnRgbSliderChanged;
        _blueSlider.ValueChanged += OnRgbSliderChanged;
        _alphaSlider.ValueChanged += OnAlphaSliderChanged;

        WireValueEdit(_hueEdit, ValueChannel.Hue);
        WireValueEdit(_satEdit, ValueChannel.Sat);
        WireValueEdit(_valEdit, ValueChannel.Val);
        WireValueEdit(_redEdit, ValueChannel.Red);
        WireValueEdit(_greenEdit, ValueChannel.Green);
        WireValueEdit(_blueEdit, ValueChannel.Blue);
        WireValueEdit(_alphaEdit, ValueChannel.Alpha);

        _alphaRow.Visible = _editAlpha;
        _userData = GetNodeOrNull<GlobalData>("/root/GlobalData")?.UserDataManager;
        BuildPresets();
        BuildRecentSlots();
        ApplySwatchStyle();
        SyncPopupFromColor();

        Pressed += OnButtonPressed;

        UiLocalizer.LocalizeTree(this);
        _globalSignals = GetNodeOrNull<GlobalSignals>("/root/GlobalSignals");
        if (_globalSignals != null)
            _globalSignals.LocaleChanged += OnLocaleChanged;
    }

    public override void _ExitTree()
    {
        if (_globalSignals != null)
            _globalSignals.LocaleChanged -= OnLocaleChanged;
        if (_popup != null && IsInstanceValid(_popup))
        {
            _popup.PopupHide -= OnPopupHide;
            _popup.WindowInput -= OnPopupWindowInput;
        }
        base._ExitTree();
    }

    private void OnLocaleChanged(string localeCode)
    {
        if (!IsInstanceValid(this))
            return;
        UiLocalizer.LocalizeTree(this);
    }

    private void OnButtonPressed()
    {
        if (_popup == null)
            return;
        if (_popup.Visible)
        {
            _popup.Hide();
            return;
        }

        ShowPopup();
    }

    private void ShowPopup()
    {
        SyncPopupFromColor();
        RefreshRecentSwatches();

        var host = GetWindow();
        float scale = host != null ? Mathf.Max(host.ContentScaleFactor, 0.01f) : 1f;
        _popup.ContentScaleMode = Window.ContentScaleModeEnum.CanvasItems;
        _popup.ContentScaleAspect = Window.ContentScaleAspectEnum.Expand;
        _popup.ContentScaleFactor = scale;

        _popup.ResetSize();
        Vector2 content = _popup.GetContentsMinimumSize();
        int width = Math.Max(Mathf.CeilToInt(content.X * scale), 1);
        int height = Math.Max(Mathf.CeilToInt(content.Y * scale), 1);

        var screenXform = GetScreenTransform();
        Vector2 topLeft = screenXform.Origin;
        Vector2 bottomLeft = screenXform * new Vector2(0f, Size.Y);
        Vector2I pos = UiUtilities.ScreenPointToPopupPosition(
            _popup,
            new Vector2(topLeft.X, bottomLeft.Y + 2f));

        Rect2I usable = UiUtilities.GetPopupUsableRect(_popup);
        if (pos.X + width > usable.Position.X + usable.Size.X)
            pos.X = usable.Position.X + usable.Size.X - width;
        if (pos.X < usable.Position.X)
            pos.X = usable.Position.X;
        if (pos.Y + height > usable.Position.Y + usable.Size.Y)
        {
            Vector2I above = UiUtilities.ScreenPointToPopupPosition(_popup, topLeft);
            pos.Y = above.Y - height;
            if (pos.Y < usable.Position.Y)
                pos.Y = usable.Position.Y;
        }

        _popup.Popup(new Rect2I(pos, new Vector2I(width, height)));
        _popup.GrabFocus();
    }

    private void OnPopupWindowInput(InputEvent @event)
    {
        if (@event is not InputEventKey key || !key.Pressed || key.Echo)
            return;

        bool enter = key.Keycode == Key.Enter || key.Keycode == Key.KpEnter;
        bool esc = key.Keycode == Key.Escape
                   || key.PhysicalKeycode == Key.Escape
                   || key.IsAction("ui_cancel");

        if (TryGetFocusedValueEdit(out LineEdit edit, out ValueChannel channel))
        {
            if (enter)
            {
                ApplyValueEdit(edit, channel);
                edit.ReleaseFocus();
                _popup.SetInputAsHandled();
            }
            else if (esc)
            {
                WriteValueEdit(edit, channel);
                edit.ReleaseFocus();
                _popup.SetInputAsHandled();
            }

            return;
        }

        if (enter)
        {
            CommitAndClose();
            _popup.SetInputAsHandled();
        }
    }

    /// <summary>
    /// Hides the picker so <see cref="PopupClosed"/> commits the current colour.
    /// </summary>
    private void CommitAndClose()
    {
        if (_popup != null && _popup.Visible)
            _popup.Hide();
    }

    private void OnPopupHide()
    {
        RememberCommittedColour();
        EmitSignal(SignalName.PopupClosed);
    }

    /// <summary>
    /// Stores the committed colour in user prefs when it is not one of the fixed presets.
    /// </summary>
    private void RememberCommittedColour()
    {
        if (_userData == null || IsPresetColour(_color))
            return;
        _userData.RememberCustomColour(_color);
    }

    /// <summary>
    /// True when <paramref name="color"/> matches a built-in preset (RGB only).
    /// </summary>
    public static bool IsPresetColour(Color color)
    {
        foreach (Color preset in Presets)
        {
            if (ColorsEqualRgb(color, preset))
                return true;
        }
        return false;
    }

    private void OnHsvSliderChanged(double _)
    {
        if (_syncingUi)
            return;

        _hue01 = (float)(_hueSlider.Value / 360.0);
        float s = (float)(_satSlider.Value / 100.0);
        float v = (float)(_valSlider.Value / 100.0);
        float a = CurrentAlpha();
        SetColor(Color.FromHsv(_hue01, s, v, a), emitChanged: true);
    }

    private void OnRgbSliderChanged(double _)
    {
        if (_syncingUi)
            return;

        float r = (float)(_redSlider.Value / 255.0);
        float g = (float)(_greenSlider.Value / 255.0);
        float b = (float)(_blueSlider.Value / 255.0);
        SetColor(new Color(r, g, b, CurrentAlpha()), emitChanged: true);
    }

    private void OnAlphaSliderChanged(double _)
    {
        if (_syncingUi || !_editAlpha)
            return;

        var next = _color;
        next.A = (float)(_alphaSlider.Value / 100.0);
        SetColor(next, emitChanged: true);
    }

    private float CurrentAlpha()
    {
        if (!_editAlpha || _alphaSlider == null)
            return 1f;
        return (float)(_alphaSlider.Value / 100.0);
    }

    private void SetColor(Color color, bool emitChanged)
    {
        if (!_editAlpha)
            color.A = 1f;

        bool same = _color.IsEqualApprox(color);
        _color = color;
        ApplySwatchStyle();
        if (_preview != null)
            _preview.Color = color;
        SyncPopupFromColor();
        RefreshPresetSelection();
        RefreshRecentSwatches();

        if (emitChanged && !same)
            EmitSignal(SignalName.ColorChanged, color);
    }

    private void SyncPopupFromColor()
    {
        if (_hueSlider == null)
            return;

        _syncingUi = true;
        try
        {
            _color.ToHsv(out float h, out float s, out float v);
            if (s > 0.001f)
                _hue01 = h;
            _hueSlider.SetValueNoSignal(_hue01 * 360.0);
            _satSlider.SetValueNoSignal(s * 100.0);
            _valSlider.SetValueNoSignal(v * 100.0);
            if (_redSlider != null)
                _redSlider.SetValueNoSignal(Mathf.Round(_color.R * 255.0f));
            if (_greenSlider != null)
                _greenSlider.SetValueNoSignal(Mathf.Round(_color.G * 255.0f));
            if (_blueSlider != null)
                _blueSlider.SetValueNoSignal(Mathf.Round(_color.B * 255.0f));
            if (_alphaSlider != null)
                _alphaSlider.SetValueNoSignal(_color.A * 100.0);
            if (_preview != null)
                _preview.Color = _color;

            WriteValueEditIfUnfocused(_hueEdit, ValueChannel.Hue);
            WriteValueEditIfUnfocused(_satEdit, ValueChannel.Sat);
            WriteValueEditIfUnfocused(_valEdit, ValueChannel.Val);
            WriteValueEditIfUnfocused(_redEdit, ValueChannel.Red);
            WriteValueEditIfUnfocused(_greenEdit, ValueChannel.Green);
            WriteValueEditIfUnfocused(_blueEdit, ValueChannel.Blue);
            WriteValueEditIfUnfocused(_alphaEdit, ValueChannel.Alpha);
        }
        finally
        {
            _syncingUi = false;
        }
    }

    private void WireValueEdit(LineEdit edit, ValueChannel channel)
    {
        if (edit == null)
            return;

        edit.TextSubmitted += _ =>
        {
            ApplyValueEdit(edit, channel);
            edit.ReleaseFocus();
        };
        edit.FocusExited += () => ApplyValueEdit(edit, channel);
    }

    private bool TryGetFocusedValueEdit(out LineEdit edit, out ValueChannel channel)
    {
        if (IsEditFocused(_hueEdit)) { edit = _hueEdit; channel = ValueChannel.Hue; return true; }
        if (IsEditFocused(_satEdit)) { edit = _satEdit; channel = ValueChannel.Sat; return true; }
        if (IsEditFocused(_valEdit)) { edit = _valEdit; channel = ValueChannel.Val; return true; }
        if (IsEditFocused(_redEdit)) { edit = _redEdit; channel = ValueChannel.Red; return true; }
        if (IsEditFocused(_greenEdit)) { edit = _greenEdit; channel = ValueChannel.Green; return true; }
        if (IsEditFocused(_blueEdit)) { edit = _blueEdit; channel = ValueChannel.Blue; return true; }
        if (IsEditFocused(_alphaEdit)) { edit = _alphaEdit; channel = ValueChannel.Alpha; return true; }
        edit = null;
        channel = ValueChannel.Hue;
        return false;
    }

    private static bool IsEditFocused(LineEdit edit)
    {
        return edit != null && GodotObject.IsInstanceValid(edit) && edit.HasFocus();
    }

    private void WriteValueEditIfUnfocused(LineEdit edit, ValueChannel channel)
    {
        if (edit == null || edit.HasFocus())
            return;
        WriteValueEdit(edit, channel);
    }

    private void WriteValueEdit(LineEdit edit, ValueChannel channel)
    {
        if (edit == null)
            return;
        edit.Text = FormatChannelValue(channel);
    }

    private string FormatChannelValue(ValueChannel channel)
    {
        return channel switch
        {
            ValueChannel.Hue => Mathf.RoundToInt(_hue01 * 360f).ToString(),
            ValueChannel.Sat => Mathf.RoundToInt(ChannelSat() * 100f).ToString(),
            ValueChannel.Val => Mathf.RoundToInt(ChannelVal() * 100f).ToString(),
            ValueChannel.Red => Mathf.RoundToInt(_color.R * 255f).ToString(),
            ValueChannel.Green => Mathf.RoundToInt(_color.G * 255f).ToString(),
            ValueChannel.Blue => Mathf.RoundToInt(_color.B * 255f).ToString(),
            ValueChannel.Alpha => Mathf.RoundToInt(_color.A * 100f).ToString(),
            _ => "0"
        };
    }

    private float ChannelSat()
    {
        _color.ToHsv(out _, out float s, out _);
        return s;
    }

    private float ChannelVal()
    {
        _color.ToHsv(out _, out _, out float v);
        return v;
    }

    private void ApplyValueEdit(LineEdit edit, ValueChannel channel)
    {
        if (_syncingUi || edit == null)
            return;

        if (!int.TryParse(edit.Text.Trim(), out int parsed))
        {
            WriteValueEdit(edit, channel);
            return;
        }

        int max = channel switch
        {
            ValueChannel.Hue => 360,
            ValueChannel.Red or ValueChannel.Green or ValueChannel.Blue => 255,
            _ => 100
        };
        parsed = Mathf.Clamp(parsed, 0, max);

        switch (channel)
        {
            case ValueChannel.Hue:
                _hue01 = parsed / 360f;
                SetColor(Color.FromHsv(_hue01, ChannelSat(), ChannelVal(), CurrentAlpha()), emitChanged: true);
                break;
            case ValueChannel.Sat:
                SetColor(Color.FromHsv(_hue01, parsed / 100f, ChannelVal(), CurrentAlpha()), emitChanged: true);
                break;
            case ValueChannel.Val:
                SetColor(Color.FromHsv(_hue01, ChannelSat(), parsed / 100f, CurrentAlpha()), emitChanged: true);
                break;
            case ValueChannel.Red:
                SetColor(new Color(parsed / 255f, _color.G, _color.B, CurrentAlpha()), emitChanged: true);
                break;
            case ValueChannel.Green:
                SetColor(new Color(_color.R, parsed / 255f, _color.B, CurrentAlpha()), emitChanged: true);
                break;
            case ValueChannel.Blue:
                SetColor(new Color(_color.R, _color.G, parsed / 255f, CurrentAlpha()), emitChanged: true);
                break;
            case ValueChannel.Alpha:
                if (!_editAlpha)
                    break;
                var next = _color;
                next.A = parsed / 100f;
                SetColor(next, emitChanged: true);
                break;
        }
    }

    private void BuildPresets()
    {
        if (_presetGrid == null)
            return;

        foreach (Node child in _presetGrid.GetChildren())
            child.QueueFree();

        _presetButtons = new Button[Presets.Length];
        for (int i = 0; i < Presets.Length; i++)
        {
            Color preset = Presets[i];
            var btn = new Button
            {
                CustomMinimumSize = new Vector2(PresetSizePx, PresetSizePx),
                FocusMode = FocusModeEnum.None,
                Flat = false,
            };
            ApplyColourToButton(btn, preset, selected: false);
            int index = i;
            btn.Pressed += () =>
            {
                Color next = Presets[index];
                if (_editAlpha)
                    next.A = _color.A;
                SetColor(next, emitChanged: true);
                CommitAndClose();
            };
            _presetGrid.AddChild(btn);
            _presetButtons[i] = btn;
        }

        float gridWidth = PresetColumns * PresetSizePx + (PresetColumns - 1) * PresetSeparationPx;
        _presetGrid.CustomMinimumSize = new Vector2(gridWidth, 0);
        if (_popupVBox != null)
            _popupVBox.CustomMinimumSize = new Vector2(gridWidth, 0);

        RefreshPresetSelection();
    }

    private void BuildRecentSlots()
    {
        if (_recentGrid == null)
            return;

        foreach (Node child in _recentGrid.GetChildren())
            child.QueueFree();

        int slots = UserDataManager.MaxRecentCustomColours;
        _recentButtons = new Button[slots];
        for (int i = 0; i < slots; i++)
        {
            var btn = new Button
            {
                CustomMinimumSize = new Vector2(PresetSizePx, PresetSizePx),
                FocusMode = FocusModeEnum.None,
                Flat = false,
            };
            int index = i;
            btn.Pressed += () => OnRecentPressed(index);
            _recentGrid.AddChild(btn);
            _recentButtons[i] = btn;
        }

        RefreshRecentSwatches();
    }

    private void OnRecentPressed(int index)
    {
        var recents = _userData?.GetRecentCustomColours();
        if (recents == null || index < 0 || index >= recents.Count)
            return;

        Color next = recents[index];
        if (_editAlpha)
            next.A = _color.A;
        SetColor(next, emitChanged: true);
        CommitAndClose();
    }

    private void RefreshRecentSwatches()
    {
        if (_recentButtons.Length == 0)
            return;

        var recents = _userData?.GetRecentCustomColours();
        int count = recents?.Count ?? 0;
        for (int i = 0; i < _recentButtons.Length; i++)
        {
            var btn = _recentButtons[i];
            if (btn == null || !IsInstanceValid(btn))
                continue;

            if (i < count)
            {
                Color fill = recents[i];
                bool selected = ColorsEqualRgb(_color, fill);
                ApplyColourToButton(btn, fill, selected);
                btn.Disabled = false;
                btn.MouseFilter = MouseFilterEnum.Stop;
                btn.TooltipText = UiLocalizer.T("Recent colour");
            }
            else
            {
                ApplyEmptyRecentStyle(btn);
                btn.Disabled = true;
                btn.MouseFilter = MouseFilterEnum.Ignore;
                btn.TooltipText = string.Empty;
            }
        }
    }

    private static void ApplyEmptyRecentStyle(Button button)
    {
        var style = new StyleBoxFlat
        {
            BgColor = new Color(0.12f, 0.12f, 0.12f, 1f),
            BorderColor = new Color(1f, 1f, 1f, 0.12f),
        };
        style.SetBorderWidthAll(1);
        style.SetCornerRadiusAll(3);
        style.SetContentMarginAll(0);
        foreach (string state in new[] { "normal", "hover", "pressed", "disabled", "focus" })
            button.AddThemeStyleboxOverride(state, style);
    }

    private void RefreshPresetSelection()
    {
        if (_presetButtons.Length == 0)
            return;
        for (int i = 0; i < _presetButtons.Length; i++)
        {
            var btn = _presetButtons[i];
            if (btn == null || !IsInstanceValid(btn))
                continue;
            Color preset = Presets[i];
            Color compare = _editAlpha
                ? new Color(preset.R, preset.G, preset.B, _color.A)
                : preset;
            bool selected = _color.IsEqualApprox(compare)
                            || (!_editAlpha && ColorsEqualRgb(_color, preset));
            ApplyColourToButton(btn, preset, selected);
        }
    }

    private static bool ColorsEqualRgb(Color a, Color b)
    {
        return Mathf.IsEqualApprox(a.R, b.R)
               && Mathf.IsEqualApprox(a.G, b.G)
               && Mathf.IsEqualApprox(a.B, b.B);
    }

    private void ApplySwatchStyle()
    {
        ApplyColourToButton(this, _color, selected: true);
    }

    /// <summary>
    /// Fills a button with <paramref name="fill"/> and a light border (stronger when selected).
    /// </summary>
    private static void ApplyColourToButton(Button button, Color fill, bool selected)
    {
        if (button == null)
            return;

        float lum = fill.R * 0.299f + fill.G * 0.587f + fill.B * 0.114f;
        Color border = lum > 0.55f
            ? new Color(0f, 0f, 0f, selected ? 0.85f : 0.35f)
            : new Color(1f, 1f, 1f, selected ? 0.85f : 0.28f);

        foreach (string state in new[] { "normal", "hover", "pressed", "disabled", "focus" })
        {
            var style = new StyleBoxFlat
            {
                BgColor = fill,
                BorderColor = border,
            };
            int w = selected ? 2 : 1;
            style.SetBorderWidthAll(w);
            style.SetCornerRadiusAll(3);
            style.SetContentMarginAll(0);
            button.AddThemeStyleboxOverride(state, style);
        }
    }
}
