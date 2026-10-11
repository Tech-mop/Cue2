// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using Cue2.Domain.Cuelist;
using Cue2.Domain.Playback;
using Cue2.Domain.Devices;
using Cue2.Domain.ShowSettings;
using Cue2.Domain.Metadata;
using Cue2.Domain.Cues;
using Cue2.Domain.Connections;
using Cue2.Domain.Library;
using Cue2.Domain.Commands;
using Cue2.Services;
using Godot;
using Cue2.UI.Utilities;

namespace Cue2.UI.Inspectors;

/// <summary>
/// Inspector tab for control components (GO / Pause / Stop / Resume / Start Now / Devamp / Fade targeting other cues).
/// </summary>
/// <remarks>
/// Header buttons add a component of each type; cards list targets for the focused cue.
/// Pattern mirrors <see cref="ConnectionInspector"/>.
/// </remarks>
public partial class ControlInspector : Control
{
    private GlobalData _globalData;
    private GlobalSignals _globalSignals;
    private HistoryManager _historyManager;

    private PackedScene _controlCardScene =
        SceneLoader.LoadPackedScene("res://src/UI/Inspectors/ControlComponentCard.tscn", out _);

    private Cue _focusedCue;

    private Label _infoLabel;
    private Control _contentRoot;
    private FlowContainer _cardContainer;
    private Button _addGoButton;
    private Button _addPauseButton;
    private Button _addStopButton;
    private Button _addResumeButton;
    private Button _addStartNowButton;
    private Button _addDevampButton;
    private Button _addFadeButton;
    private Button _addSeekButton;
    private Button _addTranslateLayerButton;

    /// <summary>Duplicated theme StyleBoxes with a light outline; disposed on exit.</summary>
    private readonly List<StyleBoxFlat> _addButtonStyles = new();

    /// <inheritdoc />
    public override void _Ready()
    {
        _globalData = GetNode<GlobalData>("/root/GlobalData");
        _globalSignals = GetNode<GlobalSignals>("/root/GlobalSignals");
        _historyManager = _globalData?.HistoryManager;

        _infoLabel = GetNode<Label>("%InfoLabel");
        _infoLabel.AddThemeColorOverride("font_color", GlobalStyles.DisabledColor);

        _contentRoot = GetNode<Control>("%ContentRoot");
        _cardContainer = GetNode<FlowContainer>("%ControlCardContainer");
        _addGoButton = GetNode<Button>("%AddGoButton");
        _addPauseButton = GetNode<Button>("%AddPauseButton");
        _addStopButton = GetNode<Button>("%AddStopButton");
        _addResumeButton = GetNode<Button>("%AddResumeButton");
        _addStartNowButton = GetNode<Button>("%AddStartNowButton");
        _addDevampButton = GetNodeOrNull<Button>("%AddDevampButton");
        _addFadeButton = GetNodeOrNull<Button>("%AddFadeButton");
        _addSeekButton = GetNodeOrNull<Button>("%AddSeekButton");
        _addTranslateLayerButton = GetNodeOrNull<Button>("%AddTranslateLayerButton");

        _addGoButton.Pressed += () => AddControlComponent(ControlAction.Go);
        _addPauseButton.Pressed += () => AddControlComponent(ControlAction.Pause);
        _addStopButton.Pressed += () => AddControlComponent(ControlAction.Stop);
        _addResumeButton.Pressed += () => AddControlComponent(ControlAction.Resume);
        _addStartNowButton.Pressed += () => AddControlComponent(ControlAction.StartNow);
        if (_addDevampButton != null)
            _addDevampButton.Pressed += () => AddControlComponent(ControlAction.Devamp);
        if (_addFadeButton != null)
            _addFadeButton.Pressed += () => AddControlComponent(ControlAction.Fade);
        if (_addSeekButton != null)
            _addSeekButton.Pressed += () => AddControlComponent(ControlAction.Seek);
        if (_addTranslateLayerButton != null)
            _addTranslateLayerButton.Pressed += () => AddControlComponent(ControlAction.TranslateLayer);

        StyleAddControlButtons();

        _globalSignals.ShellFocused += ShellSelected;
        // Undo/redo and shell-prop syncs replace component instances — rebuild cards from live model.
        _globalSignals.SyncShellInspector += OnSyncShellInspector;
        if (_historyManager != null)
            _historyManager.HistoryRestored += OnHistoryRestored;
        VisibilityChanged += OnVisibilityChanged;

        // Start empty until a shell is selected.
        _contentRoot.Visible = false;
        _infoLabel.Visible = true;
    
        UiLocalizer.LocalizeTree(this);
        if (_globalSignals != null)
            _globalSignals.LocaleChanged += OnLocaleChanged;
}

    /// <inheritdoc />
    public override void _ExitTree()
    {
        if (_globalSignals != null)
            _globalSignals.LocaleChanged -= OnLocaleChanged;

        if (_globalSignals != null)
        {
            _globalSignals.ShellFocused -= ShellSelected;
            _globalSignals.SyncShellInspector -= OnSyncShellInspector;
        }
        if (_historyManager != null)
            _historyManager.HistoryRestored -= OnHistoryRestored;
        VisibilityChanged -= OnVisibilityChanged;

        foreach (var style in _addButtonStyles)
            UiUtilities.DisposeRefCounted(style);
        _addButtonStyles.Clear();
    }

    /// <summary>
    /// Adds a 1px light border to the header add-action buttons so they read as buttons
    /// (theme normal style draws no fill and no outline).
    /// </summary>
    private void StyleAddControlButtons()
    {
        Button[] buttons =
        {
            _addGoButton,
            _addPauseButton,
            _addStopButton,
            _addResumeButton,
            _addStartNowButton,
            _addDevampButton,
            _addFadeButton,
            _addSeekButton,
            _addTranslateLayerButton
        };

        foreach (var button in buttons)
            ApplyGentleButtonOutline(button);
    }

    /// <summary>
    /// Duplicates theme button styles and adds a soft 1px outline (stronger on hover/press).
    /// </summary>
    private void ApplyGentleButtonOutline(Button button)
    {
        if (button == null)
            return;

        ApplyOutlinedStyle(button, "normal", new Color(0.78f, 0.84f, 0.88f, 0.28f));
        ApplyOutlinedStyle(button, "hover", new Color(0.85f, 0.90f, 0.94f, 0.45f));
        ApplyOutlinedStyle(button, "pressed", new Color(0.90f, 0.94f, 0.97f, 0.55f));
        ApplyOutlinedStyle(button, "focus", new Color(0.78f, 0.84f, 0.88f, 0.40f));
        ApplyOutlinedStyle(button, "disabled", new Color(0.55f, 0.58f, 0.62f, 0.18f));
    }

    private void ApplyOutlinedStyle(Button button, string styleName, Color border)
    {
        var existing = button.GetThemeStylebox(styleName);
        StyleBoxFlat flat;
        if (existing is StyleBoxFlat source)
            flat = (StyleBoxFlat)source.Duplicate();
        else
        {
            flat = new StyleBoxFlat
            {
                BgColor = new Color(0.1f, 0.1f, 0.1f, 0.35f),
                ContentMarginLeft = 4,
                ContentMarginTop = 4,
                ContentMarginRight = 4,
                ContentMarginBottom = 4
            };
        }

        flat.SetBorderWidthAll(1);
        flat.BorderColor = border;
        flat.SetCornerRadiusAll(3);
        button.AddThemeStyleboxOverride(styleName, flat);
        _addButtonStyles.Add(flat);
    }

    private void OnVisibilityChanged()
    {
        if (Visible)
            RefreshFromLiveModel();
    }

    /// <summary>
    /// After cue undo/redo, rebuild control cards from the restored model (drop orphan refs).
    /// </summary>
    private void OnHistoryRestored(int scope)
    {
        if (scope != (int)HistoryManager.HistoryScope.Cue
            && scope != (int)HistoryManager.HistoryScope.Cuelist
            && scope != (int)HistoryManager.HistoryScope.MultiCue)
            return;
        if (!Visible)
            return;
        RefreshFromLiveModel();
    }

    /// <summary>
    /// External model edits (including history restore side-paths) that emit SyncShellInspector.
    /// </summary>
    private void OnSyncShellInspector()
    {
        if (!IsInsideTree() || !Visible)
            return;
        RefreshFromLiveModel();
    }

    /// <summary>
    /// Re-resolves the focused cue and rebuilds cards from current components.
    /// </summary>
    private void RefreshFromLiveModel()
    {
        int focusId = _globalData?.FocusedCue ?? _focusedCue?.Id ?? -1;
        _focusedCue = focusId >= 0 ? CueList.FetchCueFromId(focusId) : null;

        if (_focusedCue == null)
        {
            if (_infoLabel != null) _infoLabel.Visible = true;
            if (_contentRoot != null) _contentRoot.Visible = false;
            ClearCards();
            return;
        }

        if (_infoLabel != null) _infoLabel.Visible = false;
        if (_contentRoot != null) _contentRoot.Visible = true;
        LoadCards();
    }

    private void ShellSelected(int cueId)
    {
        _focusedCue = CueList.FetchCueFromId(cueId);

        if (_focusedCue == null)
        {
            _infoLabel.Visible = true;
            _contentRoot.Visible = false;
            ClearCards();
            return;
        }

        _infoLabel.Visible = false;
        _contentRoot.Visible = true;
        LoadCards();
    }

    /// <summary>
    /// Removes a control component from the focused cue (history recorded).
    /// </summary>
    /// <param name="component">Component instance to remove.</param>
    public void RemoveComponent(ICueComponent component)
    {
        if (_focusedCue == null || component == null) return;
        if (_globalData?.HistoryManager?.IsRestoring == true) return;

        InspectorMultiEditSupport.RecordBeforeEdit(
            _globalData, multiHistory: false, _focusedCue, "Remove control component");
        _focusedCue.RemoveICueComponent(component);
        try { _focusedCue.CalculateTotalDuration(); } catch { /* best-effort */ }
        _globalSignals?.EmitSignal(nameof(GlobalSignals.UpdateShellBar), _focusedCue.Id);
        _globalSignals?.EmitSignal(nameof(GlobalSignals.Log),
            $"Removed control component from cue {_focusedCue.Id}", (int)LogType.Info);
        LoadCards();
    }

    /// <summary>
    /// Moves a control component earlier or later in the cue's component list (execution order).
    /// </summary>
    /// <param name="component">Control to move.</param>
    /// <param name="delta">-1 = earlier, +1 = later among control components.</param>
    public void MoveControlComponent(ControlComponent component, int delta)
    {
        if (_focusedCue == null || component == null || delta == 0) return;
        if (_globalData?.HistoryManager?.IsRestoring == true) return;

        var controls = new System.Collections.Generic.List<ControlComponent>();
        foreach (var c in _focusedCue.Components)
        {
            if (c is ControlComponent cc)
                controls.Add(cc);
        }

        int idx = controls.IndexOf(component);
        int swapIdx = idx + delta;
        if (idx < 0 || swapIdx < 0 || swapIdx >= controls.Count)
            return;

        int listA = _focusedCue.Components.IndexOf(controls[idx]);
        int listB = _focusedCue.Components.IndexOf(controls[swapIdx]);
        if (listA < 0 || listB < 0) return;

        InspectorMultiEditSupport.RecordBeforeEdit(
            _globalData, multiHistory: false, _focusedCue, "Reorder control components");
        (_focusedCue.Components[listA], _focusedCue.Components[listB]) =
            (_focusedCue.Components[listB], _focusedCue.Components[listA]);

        LoadCards();
    }

    private void AddControlComponent(ControlAction action)
    {
        if (_focusedCue == null)
        {
            _globalSignals?.EmitSignal(nameof(GlobalSignals.Log),
                "ControlInspector: No cue selected", (int)LogType.Warning);
            return;
        }

        if (_globalData?.HistoryManager?.IsRestoring == true) return;

        InspectorMultiEditSupport.RecordBeforeEdit(
            _globalData, multiHistory: false, _focusedCue, $"Add control {action}");
        var component = new ControlComponent { Action = action };
        // Append — runs after existing controls in list order.
        _focusedCue.AddICueComponent(component);
        // Default Fade is 1s — include control times in shell duration.
        try { _focusedCue.CalculateTotalDuration(); } catch { /* best-effort */ }
        _globalSignals?.EmitSignal(nameof(GlobalSignals.UpdateShellBar), _focusedCue.Id);
        _globalSignals?.EmitSignal(nameof(GlobalSignals.Log),
            $"Added {action} control to cue {_focusedCue.Id}", (int)LogType.Info);
        LoadCards();
    }

    private void LoadCards()
    {
        ClearCards();
        if (_focusedCue == null || _controlCardScene == null) return;

        var controls = new System.Collections.Generic.List<ControlComponent>();
        foreach (var component in _focusedCue.Components)
        {
            if (component is ControlComponent controlComp)
                controls.Add(controlComp);
        }

        int controlIndex = 0;
        foreach (var component in _focusedCue.Components)
        {
            if (component is ControlComponent controlComp)
            {
                var card = _controlCardScene.Instantiate<ControlComponentCard>();
                _cardContainer.AddChild(card);
                card.SetComponent(controlComp, this, orderIndex: controlIndex, orderCount: controls.Count);
                controlIndex++;
            }
        }
    }

    private void ClearCards()
    {
        if (_cardContainer == null) return;
        foreach (var child in _cardContainer.GetChildren())
        {
            child.QueueFree();
        }
    }

    /// <summary>
    /// Re-localizes panel chrome when the UI language changes.
    /// </summary>
    /// <param name="localeCode">New locale code.</param>
    private void OnLocaleChanged(string localeCode)
    {
        if (!GodotObject.IsInstanceValid(this))
            return;
        UiLocalizer.LocalizeTree(this);
    }

}
