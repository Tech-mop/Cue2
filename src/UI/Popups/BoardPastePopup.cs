// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System;
using Cue2.Services;
using Cue2.UI.Utilities;
using Godot;

namespace Cue2.UI.Popups;

/// <summary>How pasted cues are placed when the target button-board cell is occupied.</summary>
public enum BoardPasteAction
{
    /// <summary>Replace the cue in the target cell; extra cues fill the next empty cells.</summary>
    Replace = 0,

    /// <summary>Insert at the target cell and shift that row to the right.</summary>
    ShuffleRow = 1,

    /// <summary>Insert at the target cell and shift that column down.</summary>
    ShuffleColumn = 2,

    /// <summary>Leave the occupied cell and fill the next empty cells.</summary>
    NextAvailable = 3
}

/// <summary>
/// Choice popup when pasting onto an occupied button-board cell.
/// Parent to <c>/root/Cue2Base</c> (never a TabContainer).
/// </summary>
public partial class BoardPastePopup : Window
{
    private GlobalData _globalData;
    private GlobalSignals _globalSignals;
    private BoardPasteAction _chosen = BoardPasteAction.Replace;
    private Button _cancelButton;
    private Button _pasteButton;
    private Label _targetLabel;

    /// <summary>Applies Linux native-window policy before enter-tree.</summary>
    public BoardPastePopup()
    {
        LinuxWindowEmbedPolicy.ApplyToAppWindow(this);
        Title = "Paste Cues";
        Borderless = true;
        Unresizable = true;
        Size = new Vector2I(460, 300);
        Exclusive = false;
    }

    /// <summary>Raised when the user confirms a paste action.</summary>
    public event Action<BoardPasteAction> Confirmed;

    /// <summary>Raised when the user cancels.</summary>
    public event Action Cancelled;

    /// <inheritdoc />
    public override void _Ready()
    {
        _globalData = GetNodeOrNull<GlobalData>("/root/GlobalData");
        _globalSignals = GetNodeOrNull<GlobalSignals>("/root/GlobalSignals");

        Theme = GlobalStyles.ActiveUiTheme ?? GD.Load<Theme>(GlobalStyles.DefaultThemeResourcePath);
        UiUtilities.RescaleWindow(this, _globalData?.BaseDisplayScale ?? 1f);
        float userScale = _globalData?.UserDataManager?.UiScale ?? UserDataManager.DefaultUiScale;
        UiUtilities.RescaleUi(this, userScale, _globalData?.BaseDisplayScale ?? 1f);

        var panel = new Panel();
        panel.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        AddChild(panel);

        var margin = new MarginContainer();
        margin.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        margin.AddThemeConstantOverride("margin_left", 12);
        margin.AddThemeConstantOverride("margin_top", 28);
        margin.AddThemeConstantOverride("margin_right", 12);
        margin.AddThemeConstantOverride("margin_bottom", 12);
        AddChild(margin);

        var root = new VBoxContainer();
        root.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        root.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        root.AddThemeConstantOverride("separation", 8);
        margin.AddChild(root);

        var title = new Label
        {
            Text = UiLocalizer.T("Paste Cues"),
            HorizontalAlignment = HorizontalAlignment.Center
        };
        title.AddThemeFontSizeOverride("font_size", 16);
        root.AddChild(title);

        _targetLabel = new Label
        {
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
        };
        root.AddChild(_targetLabel);

        root.AddChild(new HSeparator());

        var header = new Label { Text = UiLocalizer.T("This cell already has a cue:") };
        header.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        root.AddChild(header);

        var options = new VBoxContainer();
        options.AddThemeConstantOverride("separation", 2);
        options.AddChild(CreateChoice(UiLocalizer.T("Replace this cue"), BoardPasteAction.Replace, isDefault: true));
        options.AddChild(CreateChoice(UiLocalizer.T("Shuffle row (insert and shift right)"), BoardPasteAction.ShuffleRow));
        options.AddChild(CreateChoice(UiLocalizer.T("Shuffle column (insert and shift down)"), BoardPasteAction.ShuffleColumn));
        options.AddChild(CreateChoice(UiLocalizer.T("Place in the next available cell"), BoardPasteAction.NextAvailable));
        root.AddChild(options);

        var spacer = new Control();
        spacer.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        root.AddChild(spacer);

        var bar = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.End };
        _cancelButton = new Button { Text = UiLocalizer.T("Cancel") };
        _pasteButton = new Button { Text = UiLocalizer.T("Paste") };
        _cancelButton.Pressed += OnCancel;
        _pasteButton.Pressed += OnConfirm;
        bar.AddChild(_cancelButton);
        bar.AddChild(_pasteButton);
        root.AddChild(bar);

        var handlesScene = GD.Load<PackedScene>("res://src/UI/Windows/SubWindowHandles.tscn");
        if (handlesScene != null)
            AddChild(handlesScene.Instantiate());

        CloseRequested += OnCancel;
        UiLocalizer.LocalizeTree(this);
    }

    /// <inheritdoc />
    public override void _ExitTree()
    {
        if (_cancelButton != null)
            _cancelButton.Pressed -= OnCancel;
        if (_pasteButton != null)
            _pasteButton.Pressed -= OnConfirm;
        CloseRequested -= OnCancel;
        base._ExitTree();
    }

    /// <summary>Sets the drop-target summary shown above the options.</summary>
    public void Configure(int cueCount, string targetSummary)
    {
        if (_targetLabel == null)
            return;
        string countLine = cueCount == 1
            ? UiLocalizer.T("1 cue on the clipboard")
            : UiLocalizer.Tf("{0} cues on the clipboard", cueCount);
        _targetLabel.Text = string.IsNullOrWhiteSpace(targetSummary)
            ? countLine
            : $"{countLine}\n{targetSummary}";
    }

    /// <summary>Shows the popup centred over the operator window.</summary>
    public void ShowConfigured()
    {
        PopupCentered();
    }

    private CheckBox CreateChoice(string text, BoardPasteAction action, bool isDefault = false)
    {
        var cb = new CheckBox
        {
            Text = text,
            ButtonPressed = isDefault || _chosen == action,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
        };
        if (isDefault)
            _chosen = action;
        cb.Toggled += pressed =>
        {
            if (!pressed)
                return;
            _chosen = action;
            var parent = cb.GetParent();
            if (parent == null)
                return;
            foreach (Node sibling in parent.GetChildren())
            {
                if (sibling is CheckBox other && other != cb)
                    other.ButtonPressed = false;
            }
        };
        return cb;
    }

    private void OnConfirm()
    {
        Confirmed?.Invoke(_chosen);
        Hide();
    }

    private void OnCancel()
    {
        Cancelled?.Invoke();
        Hide();
    }
}
