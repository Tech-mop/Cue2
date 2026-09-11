// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System;
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
/// Inspector tab for control components (GO / Pause / Stop / Resume / Start Now targeting other cues).
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
    private Button _addFadeButton;
    private Button _addSeekButton;
    private Button _addTranslateLayerButton;

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
        _addFadeButton = GetNodeOrNull<Button>("%AddFadeButton");
        _addSeekButton = GetNodeOrNull<Button>("%AddSeekButton");
        _addTranslateLayerButton = GetNodeOrNull<Button>("%AddTranslateLayerButton");

        _addGoButton.Pressed += () => AddControlComponent(ControlAction.Go);
        _addPauseButton.Pressed += () => AddControlComponent(ControlAction.Pause);
        _addStopButton.Pressed += () => AddControlComponent(ControlAction.Stop);
        _addResumeButton.Pressed += () => AddControlComponent(ControlAction.Resume);
        _addStartNowButton.Pressed += () => AddControlComponent(ControlAction.StartNow);
        if (_addFadeButton != null)
            _addFadeButton.Pressed += () => AddControlComponent(ControlAction.Fade);
        if (_addSeekButton != null)
            _addSeekButton.Pressed += () => AddControlComponent(ControlAction.Seek);
        if (_addTranslateLayerButton != null)
            _addTranslateLayerButton.Pressed += () => AddControlComponent(ControlAction.TranslateLayer);

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

        // List order = execution order among control components.
        var controls = new System.Collections.Generic.List<ControlComponent>();
        foreach (var component in _focusedCue.Components)
        {
            if (component is ControlComponent controlComp)
                controls.Add(controlComp);
        }

        for (int i = 0; i < controls.Count; i++)
        {
            var card = _controlCardScene.Instantiate<ControlComponentCard>();
            _cardContainer.AddChild(card);
            card.SetComponent(controls[i], this, orderIndex: i, orderCount: controls.Count);
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
