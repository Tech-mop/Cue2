// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Linq;
using Cue2.Services;
using Cue2.UI.Utilities;
using Godot;

namespace Cue2.UI.Settings;

/// <summary>
/// Settings panel for assigning MIDI controls to project InputMap actions.
/// Layout mirrors <see cref="SettingsInputMap"/> (categorized accordion of cards).
/// </summary>
public partial class SettingsMidiInputMap : ScrollContainer
{
    private GlobalSignals _globalSignals;
    private GlobalData _globalData;
    private HistoryManager _historyManager;
    private MidiManager _midiManager;

    private VBoxContainer _inputsContainer;
    private PackedScene _cardScene;

    private readonly Dictionary<string, MidiInputActionCard> _cardsByAction = new();
    private readonly Dictionary<string, CategorySection> _sectionByAction = new();
    private MidiInputActionCard _activeCaptureCard;

    private sealed class CategorySection
    {
        public string Title;
        public Button Header;
        public Control Content;
        public bool Expanded = true;
    }

    public override void _Ready()
    {
        _globalSignals = GetNodeOrNull<GlobalSignals>("/root/GlobalSignals");
        _globalData = GetNodeOrNull<GlobalData>("/root/GlobalData");
        _historyManager = _globalData?.HistoryManager;
        _midiManager = GetNodeOrNull<MidiManager>("/root/MidiManager");

        _inputsContainer = GetNodeOrNull<VBoxContainer>("%InputsContainer");

        _cardScene = SceneLoader.LoadPackedScene(
            "res://src/UI/Settings/MidiInputActionCard.tscn", out string err);
        if (_cardScene == null)
            GD.PrintErr($"SettingsMidiInputMap:_Ready - Failed to load MidiInputActionCard.tscn: {err}");

        VisibilityChanged += OnVisibilityChanged;
        if (_historyManager != null)
            _historyManager.HistoryRestored += OnHistoryRestored;
        if (_midiManager != null)
            _midiManager.MidiStateChanged += OnMidiStateChanged;
        if (_globalSignals != null)
            _globalSignals.LocaleChanged += OnLocaleChanged;

        if (IsVisibleInTree())
            PopulateActions();
    }

    public override void _ExitTree()
    {
        VisibilityChanged -= OnVisibilityChanged;
        if (_historyManager != null)
            _historyManager.HistoryRestored -= OnHistoryRestored;
        if (_midiManager != null)
            _midiManager.MidiStateChanged -= OnMidiStateChanged;
        if (_globalSignals != null)
            _globalSignals.LocaleChanged -= OnLocaleChanged;
        ClearCards();
        base._ExitTree();
    }

    private void OnVisibilityChanged()
    {
        if (Visible)
            PopulateActions();
        else
            ClearCards();
    }

    private void OnHistoryRestored(int scope)
    {
        if (scope != (int)HistoryManager.HistoryScope.Settings) return;
        if (!IsInstanceValid(this) || !Visible) return;

        CancelActiveCapture();
        RefreshAllCardDisplays();
    }

    private void OnMidiStateChanged()
    {
        // Bindings may have been set programmatically; refresh labels if visible.
        if (!Visible || _historyManager?.IsRestoring == true) return;
        RefreshAllCardDisplays();
    }

    private void OnLocaleChanged(string localeCode)
    {
        if (!GodotObject.IsInstanceValid(this))
            return;
        UiLocalizer.LocalizeTree(this);
        RelocalizeCategoryHeaders();
        RefreshAllCardDisplays();
    }

    private void RelocalizeCategoryHeaders()
    {
        foreach (var section in _sectionByAction.Values.Distinct())
        {
            if (section?.Header == null || !GodotObject.IsInstanceValid(section.Header))
                continue;
            section.Header.Text = section.Expanded
                ? $"▼  {UiLocalizer.T(section.Title)}"
                : $"▶  {UiLocalizer.T(section.Title)}";
        }
    }

    private void ClearCards()
    {
        CancelActiveCapture();
        _cardsByAction.Clear();
        _sectionByAction.Clear();

        if (_inputsContainer == null) return;
        foreach (Node child in _inputsContainer.GetChildren())
        {
            _inputsContainer.RemoveChild(child);
            child.QueueFree();
        }
    }

    private void CancelActiveCapture()
    {
        if (_activeCaptureCard != null && IsInstanceValid(_activeCaptureCard))
            _activeCaptureCard.CancelCapture(emitFocusExit: true);
        _activeCaptureCard = null;
    }

    private void RefreshAllCardDisplays()
    {
        foreach (var kvp in _cardsByAction)
        {
            if (kvp.Value != null && IsInstanceValid(kvp.Value))
                kvp.Value.RefreshDisplay();
        }
    }

    private void PopulateActions()
    {
        if (_inputsContainer == null || _cardScene == null) return;

        ClearCards();

        var managed = GlobalData.MappableInputActions;
        var remaining = new HashSet<string>(managed.Where(a => InputMap.HasAction(a) || a is "Undo" or "Redo"));

        foreach (var (category, actions) in GlobalData.MappableInputActionCategories)
        {
            var categoryActions = new List<string>();
            foreach (var action in actions)
            {
                if (!remaining.Contains(action)) continue;
                categoryActions.Add(action);
                remaining.Remove(action);
            }

            if (categoryActions.Count == 0) continue;
            AddCategorySection(category, categoryActions);
        }

        if (remaining.Count > 0)
            AddCategorySection("Other", remaining.OrderBy(a => a).ToList());

        GD.Print($"SettingsMidiInputMap:PopulateActions - {_cardsByAction.Count} MIDI action card(s)");
    }

    private void AddCategorySection(string categoryTitle, List<string> actions)
    {
        var sectionRoot = new VBoxContainer();
        sectionRoot.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        sectionRoot.AddThemeConstantOverride("separation", 2);

        var header = new Button();
        header.Text = $"▼  {UiLocalizer.T(categoryTitle)}";
        header.Alignment = HorizontalAlignment.Left;
        header.Flat = true;
        header.FocusMode = FocusModeEnum.None;
        header.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        header.AddThemeFontSizeOverride("font_size", 11);
        header.AddThemeColorOverride("font_color", GlobalStyles.SoftFontColor);
        header.AddThemeColorOverride("font_hover_color", Colors.White);
        header.AddThemeColorOverride("font_pressed_color", Colors.White);

        var content = new FlowContainer();
        content.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        content.AddThemeConstantOverride("h_separation", 4);
        content.AddThemeConstantOverride("v_separation", 4);

        var section = new CategorySection
        {
            Title = categoryTitle,
            Header = header,
            Content = content,
            Expanded = true,
        };

        header.Pressed += () =>
        {
            section.Expanded = !section.Expanded;
            content.Visible = section.Expanded;
            header.Text = section.Expanded
                ? $"▼  {UiLocalizer.T(section.Title)}"
                : $"▶  {UiLocalizer.T(section.Title)}";
        };

        sectionRoot.AddChild(header);
        sectionRoot.AddChild(content);

        var sep = new HSeparator();
        sep.AddThemeConstantOverride("separation", 6);
        sectionRoot.AddChild(sep);

        _inputsContainer.AddChild(sectionRoot);

        foreach (var actionName in actions)
        {
            var card = _cardScene.Instantiate<MidiInputActionCard>();
            card.SetAction(actionName);
            content.AddChild(card);
            card.BindingConflict += OnBindingConflict;
            card.CaptureStarted += OnCardCaptureStarted;
            _cardsByAction[actionName] = card;
            _sectionByAction[actionName] = section;
        }
    }

    private void OnCardCaptureStarted(MidiInputActionCard card)
    {
        if (_activeCaptureCard != null &&
            _activeCaptureCard != card &&
            IsInstanceValid(_activeCaptureCard))
        {
            _activeCaptureCard.CancelCapture(emitFocusExit: false);
        }

        _activeCaptureCard = card;
    }

    private void OnBindingConflict(string conflictingAction, string attemptedCombo)
    {
        if (string.IsNullOrEmpty(conflictingAction)) return;

        if (_sectionByAction.TryGetValue(conflictingAction, out var section) && section != null)
        {
            if (!section.Expanded && section.Content != null && section.Header != null)
            {
                section.Expanded = true;
                section.Content.Visible = true;
                section.Header.Text = $"▼  {UiLocalizer.T(section.Title)}";
            }
        }

        if (_cardsByAction.TryGetValue(conflictingAction, out var card) && IsInstanceValid(card))
            card.FlashConflict();

        string pretty = PrettifyActionName(conflictingAction);
        string msg = string.IsNullOrEmpty(attemptedCombo)
            ? $"MIDI already used by '{pretty}'."
            : $"MIDI '{attemptedCombo}' is already used by '{pretty}'.";
        _globalSignals?.EmitSignal(nameof(GlobalSignals.Log), msg, (int)LogType.Warning);
        GD.Print($"SettingsMidiInputMap:OnBindingConflict - {msg}");
    }

    private static string PrettifyActionName(string action)
    {
        if (string.IsNullOrEmpty(action)) return "";
        string result = "";
        for (int i = 0; i < action.Length; i++)
        {
            char c = action[i];
            if (i > 0 && char.IsUpper(c) && (char.IsLower(action[i - 1]) || char.IsDigit(action[i - 1])))
                result += " ";
            result += c;
        }
        return result;
    }
}
