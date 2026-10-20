// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using Cue2.Domain.Cuelist;
using Cue2.Domain.Cues;
using Cue2.Services;
using Cue2.UI.Utilities;
using Godot;

namespace Cue2.UI.Shell;

/// <summary>
/// One button-board cell: an edit-mode + slot, or a cue button with number / name / notes.
/// </summary>
public partial class ButtonBoardCell : PanelContainer
{
    private const int ColorStripWidth = 4;
    private const int ChromeButtonSize = 18;

    private ButtonBoard _board;
    private int _row;
    private int _col;
    private int _cueId = -1;
    private Cue _cue;

    private StyleBoxFlat _panelStyle;
    private ColorRect _colorStrip;
    private VBoxContainer _occupiedBox;
    private HBoxContainer _topBar;
    private Button _dragButton;
    private Label _numberLabel;
    private Button _playButton;
    private Label _nameLabel;
    private Control _nameClip;
    private Label _notesLabel;
    private Control _notesClip;
    private Label _childSeqLabel;
    private Font _childSeqFont;
    private Button _plusButton;

    private GlobalSignals _globalSignals;
    private bool _hovered;
    private bool _showMode;

    /// <summary>Grid row of this cell.</summary>
    public int Row => _row;

    /// <summary>Grid column of this cell.</summary>
    public int Col => _col;

    /// <summary>Cue id in this cell, or -1 when empty.</summary>
    public int CueId => _cueId;

    /// <summary>True when this cell holds a cue.</summary>
    public bool IsOccupied => _cueId >= 0 && _cue != null;

    /// <inheritdoc />
    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Stop;
        FocusMode = FocusModeEnum.None;
        ClipContents = true;
        SizeFlagsHorizontal = SizeFlags.ShrinkBegin;
        SizeFlagsVertical = SizeFlags.ShrinkBegin;

        _panelStyle = new StyleBoxFlat();
        _panelStyle.SetCornerRadiusAll(4);
        _panelStyle.ContentMarginLeft = 4;
        _panelStyle.ContentMarginRight = 4;
        _panelStyle.ContentMarginTop = 4;
        _panelStyle.ContentMarginBottom = 4;
        AddThemeStyleboxOverride("panel", _panelStyle);

        var body = new HBoxContainer
        {
            MouseFilter = MouseFilterEnum.Ignore
        };
        body.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        body.AddThemeConstantOverride("separation", 4);
        AddChild(body);

        _colorStrip = new ColorRect
        {
            CustomMinimumSize = new Vector2(ColorStripWidth, 0),
            SizeFlagsVertical = SizeFlags.ExpandFill,
            MouseFilter = MouseFilterEnum.Ignore,
            Visible = false
        };
        body.AddChild(_colorStrip);

        var stack = new Control
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
            MouseFilter = MouseFilterEnum.Ignore,
            ClipContents = true
        };
        body.AddChild(stack);

        _occupiedBox = new VBoxContainer
        {
            MouseFilter = MouseFilterEnum.Ignore,
            ClipContents = true
        };
        _occupiedBox.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        _occupiedBox.AddThemeConstantOverride("separation", 2);
        stack.AddChild(_occupiedBox);

        _topBar = new HBoxContainer
        {
            MouseFilter = MouseFilterEnum.Ignore,
            SizeFlagsHorizontal = SizeFlags.ExpandFill
        };
        _topBar.AddThemeConstantOverride("separation", 4);
        _occupiedBox.AddChild(_topBar);

        _dragButton = new Button
        {
            Flat = true,
            FocusMode = FocusModeEnum.None,
            CustomMinimumSize = new Vector2(ChromeButtonSize, ChromeButtonSize),
            SizeFlagsVertical = SizeFlags.ShrinkCenter,
            MouseDefaultCursorShape = CursorShape.Drag,
            TooltipText = UiLocalizer.T("Reorder")
        };
        _dragButton.SetMeta(UiLocalizer.MetaTooltip, "Reorder");
        _dragButton.AddThemeConstantOverride("icon_max_width", 14);
        _dragButton.ExpandIcon = true;
        _dragButton.IconAlignment = HorizontalAlignment.Center;
        _dragButton.GuiInput += OnDragGuiInput;
        _topBar.AddChild(_dragButton);

        _numberLabel = new Label
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            ClipText = true,
            MouseFilter = MouseFilterEnum.Ignore
        };
        _numberLabel.AddThemeFontSizeOverride("font_size", 12);
        _topBar.AddChild(_numberLabel);

        _playButton = new Button
        {
            Flat = true,
            FocusMode = FocusModeEnum.None,
            CustomMinimumSize = new Vector2(ChromeButtonSize, ChromeButtonSize),
            SizeFlagsVertical = SizeFlags.ShrinkCenter,
            TooltipText = UiLocalizer.T("Play")
        };
        _playButton.SetMeta(UiLocalizer.MetaTooltip, "Play");
        _playButton.AddThemeConstantOverride("icon_max_width", 14);
        _playButton.ExpandIcon = true;
        _playButton.IconAlignment = HorizontalAlignment.Center;
        _playButton.Pressed += OnPlayPressed;
        _topBar.AddChild(_playButton);

        _nameClip = new Control
        {
            MouseFilter = MouseFilterEnum.Ignore,
            ClipContents = true,
            CustomMinimumSize = new Vector2(0, 16),
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
            SizeFlagsStretchRatio = 1f
        };
        _occupiedBox.AddChild(_nameClip);

        _nameLabel = new Label
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            ClipText = false,
            TextOverrunBehavior = TextServer.OverrunBehavior.NoTrimming,
            MouseFilter = MouseFilterEnum.Ignore
        };
        _nameLabel.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        _nameLabel.AddThemeFontSizeOverride("font_size", 13);
        _nameClip.AddChild(_nameLabel);

        _notesClip = new Control
        {
            MouseFilter = MouseFilterEnum.Ignore,
            ClipContents = true,
            CustomMinimumSize = new Vector2(0, 14),
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
            SizeFlagsStretchRatio = 1f
        };
        _occupiedBox.AddChild(_notesClip);

        _notesLabel = new Label
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            ClipText = false,
            TextOverrunBehavior = TextServer.OverrunBehavior.NoTrimming,
            MouseFilter = MouseFilterEnum.Ignore
        };
        _notesLabel.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        _notesLabel.AddThemeFontSizeOverride("font_size", 11);
        _notesClip.AddChild(_notesLabel);

        _childSeqLabel = new Label
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ShrinkCenter,
            ClipText = true,
            Visible = false,
            MouseFilter = MouseFilterEnum.Ignore
        };
        _childSeqLabel.AddThemeFontSizeOverride("font_size", 10);
        _childSeqLabel.AddThemeColorOverride("font_color", new Color(0.70f, 0.74f, 0.78f, 0.92f));
        _childSeqFont = new SystemFont
        {
            FontNames = new[]
            {
                "Open Sans", "Segoe UI", "Helvetica Neue", "Arial", "Noto Sans",
                "DejaVu Sans", "Liberation Sans", "FreeSans", "Sans"
            },
            FontItalic = true
        };
        _childSeqLabel.AddThemeFontOverride("font", _childSeqFont);
        _occupiedBox.AddChild(_childSeqLabel);

        _plusButton = new Button
        {
            Text = "+",
            Flat = true,
            FocusMode = FocusModeEnum.None,
            Visible = false,
            MouseFilter = MouseFilterEnum.Ignore,
            TooltipText = UiLocalizer.T("Add cue")
        };
        _plusButton.SetMeta(UiLocalizer.MetaTooltip, "Add cue");
        _plusButton.SetMeta(UiLocalizer.MetaText, "+");
        _plusButton.AddThemeFontSizeOverride("font_size", 28);
        _plusButton.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        stack.AddChild(_plusButton);

        GuiInput += OnCellGuiInput;
        MouseEntered += OnMouseEntered;
        MouseExited += OnMouseExited;

        _globalSignals = GetNodeOrNull<GlobalSignals>("/root/GlobalSignals");
        if (_globalSignals != null)
            _globalSignals.UpdateShellBar += OnUpdateShellBar;

        ApplyDragPlayIcons();
        Refresh();
    }

    /// <inheritdoc />
    public override void _ExitTree()
    {
        UnbindCue();
        if (_dragButton != null)
            _dragButton.GuiInput -= OnDragGuiInput;
        if (_playButton != null)
            _playButton.Pressed -= OnPlayPressed;
        GuiInput -= OnCellGuiInput;
        MouseEntered -= OnMouseEntered;
        MouseExited -= OnMouseExited;
        if (_globalSignals != null)
            _globalSignals.UpdateShellBar -= OnUpdateShellBar;

        if (_childSeqLabel != null)
            _childSeqLabel.RemoveThemeFontOverride("font");
        if (_childSeqFont != null)
        {
            UiUtilities.DisposeRefCounted(_childSeqFont);
            _childSeqFont = null;
        }

        if (_panelStyle != null)
        {
            RemoveThemeStyleboxOverride("panel");
            UiUtilities.DisposeRefCounted(_panelStyle);
            _panelStyle = null;
        }

        base._ExitTree();
    }

    /// <summary>
    /// Places this widget on the board at <paramref name="row"/>,<paramref name="col"/> with optional cue.
    /// </summary>
    public void Bind(ButtonBoard board, int row, int col, int cueId, bool showMode)
    {
        Cue live = cueId >= 0 ? CueList.FetchCueFromId(cueId) : null;
        bool sameCue = _cueId == cueId && ReferenceEquals(_cue, live);
        bool showChanged = _showMode != showMode;
        _board = board;
        _row = row;
        _col = col;
        _showMode = showMode;
        if (!sameCue)
            BindCue(cueId);
        if (!sameCue || showChanged)
            Refresh();
        else
            RefreshChrome();
    }

    /// <summary>Applies show-mode chrome without rebinding the cue.</summary>
    public void SetShowMode(bool showMode)
    {
        if (_showMode == showMode)
            return;
        _showMode = showMode;
        Refresh();
    }

    private bool _dropHighlight;

    /// <summary>Highlights this cell as a drag drop target.</summary>
    public void SetDropHighlight(bool on)
    {
        _dropHighlight = on;
        RefreshChrome();
    }

    /// <summary>Repaints selection/hover chrome from the current <see cref="ShellSelection"/>.</summary>
    public void RefreshSelectionChrome()
    {
        RefreshChrome();
    }

    /// <summary>Dims this cell while it is the drag source.</summary>
    public void SetDragSource(bool on)
    {
        Modulate = on ? new Color(1f, 1f, 1f, 0.45f) : Colors.White;
    }

    private void BindCue(int cueId)
    {
        UnbindCue();
        _cueId = cueId;
        _cue = cueId >= 0 ? CueList.FetchCueFromId(cueId) : null;
        if (_cue == null)
        {
            _cueId = -1;
            return;
        }

        _cue.NameChanged += OnCueNameChanged;
        _cue.CueNumChanged += OnCueNumChanged;
        _cue.NotesChanged += OnCueNotesChanged;
        _cue.MemoChanged += OnCueMemoChanged;
        _cue.ColorChanged += OnCueColorChanged;
    }

    private void UnbindCue()
    {
        if (_cue == null)
            return;
        _cue.NameChanged -= OnCueNameChanged;
        _cue.CueNumChanged -= OnCueNumChanged;
        _cue.NotesChanged -= OnCueNotesChanged;
        _cue.MemoChanged -= OnCueMemoChanged;
        _cue.ColorChanged -= OnCueColorChanged;
        _cue = null;
        _cueId = -1;
    }

    /// <summary>Rebuilds occupied/empty chrome from the live cue.</summary>
    public void Refresh()
    {
        bool occupied = IsOccupied;
        bool edit = !_showMode;
        bool memo = occupied && _cue.Memo;

        if (_plusButton != null)
            _plusButton.Visible = !occupied && edit;
        if (_occupiedBox != null)
            _occupiedBox.Visible = occupied;
        if (_colorStrip != null)
            _colorStrip.Visible = occupied;

        if (_dragButton != null)
            _dragButton.Visible = occupied && edit;
        if (_playButton != null)
            _playButton.Visible = occupied && edit;
        if (_numberLabel != null)
            _numberLabel.Visible = occupied && !memo;
        if (_nameLabel != null)
            _nameLabel.Visible = occupied && !memo;
        if (_nameClip != null)
            _nameClip.Visible = occupied && !memo;

        if (occupied)
        {
            if (_numberLabel != null)
                _numberLabel.Text = _cue.CueNum ?? string.Empty;
            if (_nameLabel != null)
                _nameLabel.Text = _cue.Name ?? string.Empty;
            if (_notesLabel != null)
            {
                _notesLabel.Text = _cue.Notes ?? string.Empty;
                _notesLabel.Visible = true;
            }

            if (_notesClip != null)
            {
                _notesClip.Visible = true;
                _notesClip.SizeFlagsStretchRatio = memo ? 2f : 1f;
            }

            if (_colorStrip != null)
                _colorStrip.Color = _cue.Color.A > 0.01f ? _cue.Color : GlobalStyles.LowColor3;

            RefreshChildSequenceLabel();
        }
        else
        {
            if (_notesLabel != null)
                _notesLabel.Visible = false;
            if (_notesClip != null)
                _notesClip.Visible = false;
            if (_childSeqLabel != null)
                _childSeqLabel.Visible = false;
        }

        RefreshChrome();
    }

    private void RefreshChildSequenceLabel()
    {
        if (_childSeqLabel == null)
            return;
        int count = CountChildSequence(_cue);
        if (count <= 0)
        {
            _childSeqLabel.Visible = false;
            return;
        }

        _childSeqLabel.Visible = true;
        _childSeqLabel.Text = count == 1
            ? UiLocalizer.T("(1 child sequence)")
            : UiLocalizer.Tf("({0} child sequence)", count);
    }

    private static int CountChildSequence(Cue cue)
    {
        if (cue?.ChildCues == null || cue.ChildCues.Count == 0)
            return 0;
        return CountDescendants(cue, 0);
    }

    private static int CountDescendants(Cue cue, int depth)
    {
        if (cue?.ChildCues == null || cue.ChildCues.Count == 0)
            return 0;
        if (depth > 32)
            return cue.ChildCues.Count;

        int total = 0;
        foreach (int childId in cue.ChildCues)
        {
            total++;
            var child = CueList.FetchCueFromId(childId);
            if (child != null)
                total += CountDescendants(child, depth + 1);
        }

        return total;
    }

    /// <summary>
    /// Re-applies fallback strip colour and redraws after high/low accents change.
    /// </summary>
    internal void RefreshAccentColors()
    {
        if (_cue != null && _colorStrip != null)
            _colorStrip.Color = _cue.Color.A > 0.01f ? _cue.Color : GlobalStyles.LowColor3;
        ApplyDragPlayIcons();
        QueueRedraw();
    }

    private void RefreshChrome()
    {
        if (_panelStyle == null)
            return;

        bool occupied = IsOccupied;
        bool selected = occupied && !_showMode && IsCueSelected();
        Color bg = Colors.Transparent;
        if (occupied && _cue != null && _cue.Color.A > 0.01f)
            bg = new Color(_cue.Color.R, _cue.Color.G, _cue.Color.B, 0.16f);
        if (selected)
        {
            Color sel = new Color(GlobalStyles.LowColor1.R, GlobalStyles.LowColor1.G, GlobalStyles.LowColor1.B, 0.20f);
            bg = bg.A > 0.01f ? bg.Lerp(sel, 0.55f) : sel;
        }
        else if (_hovered && !(_showMode && !occupied))
        {
            bg = bg.A > 0.01f
                ? bg.Lightened(0.08f)
                : new Color(1f, 1f, 1f, 0.06f);
        }

        if (_dropHighlight)
        {
            Color drop = new Color(GlobalStyles.HighColor1.R, GlobalStyles.HighColor1.G, GlobalStyles.HighColor1.B, 0.14f);
            bg = bg.A > 0.01f ? bg.Lerp(drop, 0.5f) : drop;
        }

        Color border = Colors.Transparent;
        int borderW = 0;
        if (_dropHighlight)
        {
            border = GlobalStyles.HighColor1;
            borderW = 2;
        }
        else if (selected)
        {
            border = GlobalStyles.LowColor1;
            borderW = 2;
        }
        else if (occupied)
        {
            border = new Color(0.22f, 0.26f, 0.28f, 0.55f);
            borderW = 1;
        }

        _panelStyle.BgColor = bg;
        _panelStyle.SetBorderWidthAll(borderW);
        _panelStyle.BorderColor = border;
    }

    private bool IsCueSelected()
    {
        if (_cue == null)
            return false;
        var selected = ShellSelection.SelectedCues;
        return selected != null && selected.Contains(_cue);
    }

    private void ApplyDragPlayIcons()
    {
        if (_dragButton != null)
        {
            var icon = GetThemeIcon("Rearrange", "AtlasIcons");
            if (icon != null)
                _dragButton.Icon = icon;
            _dragButton.AddThemeColorOverride("icon_normal_color", GlobalStyles.IconNormalColor);
            _dragButton.AddThemeColorOverride("icon_hover_color", GlobalStyles.IconHoverColor);
        }

        if (_playButton != null)
        {
            var icon = GetThemeIcon("Play", "AtlasIcons");
            if (icon != null)
                _playButton.Icon = icon;
        }
    }

    private void OnPlayPressed()
    {
        _board?.PlayCueAt(_row, _col);
    }

    private void OnDragGuiInput(InputEvent @event)
    {
        if (@event is not InputEventMouseButton mb || !mb.Pressed)
            return;
        if (mb.ButtonIndex != MouseButton.Left)
            return;
        _board?.BeginCellDrag(this, mb.GlobalPosition);
        AcceptEvent();
    }

    private void OnCellGuiInput(InputEvent @event)
    {
        if (@event is not InputEventMouseButton mb || !mb.Pressed)
            return;

        if (mb.ButtonIndex == MouseButton.Right)
        {
            if (!_showMode)
            {
                _board?.ShowCellContextMenu(this);
                AcceptEvent();
            }

            return;
        }

        if (mb.ButtonIndex != MouseButton.Left)
            return;

        if (_showMode)
        {
            if (IsOccupied)
            {
                _board?.PlayCueAt(_row, _col);
                AcceptEvent();
            }

            return;
        }

        if (mb.ShiftPressed && IsOccupied)
        {
            _board?.SelectCueAt(_row, _col, mb);
            AcceptEvent();
            return;
        }

        bool additive = mb.CtrlPressed || mb.MetaPressed;
        _board?.BeginPotentialBoxSelect(this, mb.GlobalPosition, additive);
    }

    private void OnMouseEntered() => SetHovered(true);

    private void OnMouseExited() => SetHovered(false);

    private void SetHovered(bool hovered)
    {
        if (_hovered == hovered)
            return;
        _hovered = hovered;
        RefreshChrome();
    }

    private void OnUpdateShellBar(int cueId)
    {
        if (_cueId == cueId)
            Refresh();
    }

    private void OnCueNameChanged(string value)
    {
        if (_nameLabel != null)
            _nameLabel.Text = value ?? string.Empty;
    }

    private void OnCueNumChanged(string value)
    {
        if (_numberLabel != null)
            _numberLabel.Text = value ?? string.Empty;
    }

    private void OnCueNotesChanged(string value)
    {
        if (_notesLabel != null)
            _notesLabel.Text = value ?? string.Empty;
    }

    private void OnCueMemoChanged(bool memo)
    {
        Refresh();
    }

    private void OnCueColorChanged(Color color)
    {
        if (_colorStrip != null)
            _colorStrip.Color = color.A > 0.01f ? color : GlobalStyles.LowColor3;
        RefreshChrome();
    }
}
