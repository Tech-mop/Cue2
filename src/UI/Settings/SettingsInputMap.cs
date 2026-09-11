// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Linq;
using Cue2.Services;
using Godot;
using Cue2.UI.Utilities;

namespace Cue2.UI.Settings;

/// <summary>
/// Settings panel for viewing and editing the project's keyboard InputMap actions
/// (Cue2 Preferences — stored in <c>user://user_data.json</c>).
/// </summary>
/// <remarks>
/// Keyboard shortcuts are app preferences, not showfile data and not document undo (P2-14).
/// OSC/MIDI Input Map panels remain show-scoped with history. Groups actions into collapsible
/// category sections; supports rebinding with duplicate-key rejection and conflict highlighting.
/// </remarks>
public partial class SettingsInputMap : ScrollContainer
{
    private GlobalSignals _globalSignals;
    private GlobalData _globalData;

    private VBoxContainer _inputsContainer;
    private PackedScene _inputActionCardScene;

    /// <summary>Action name → card for conflict highlighting.</summary>
    private readonly Dictionary<string, InputActionCard> _cardsByAction = new();

    /// <summary>Action name → accordion section metadata (expand on conflict).</summary>
    private readonly Dictionary<string, CategorySection> _sectionByAction = new();

    /// <summary>The single card currently waiting for a key press, if any.</summary>
    private InputActionCard _activeListeningCard;

    /// <summary>Tracks one collapsible category in the input map panel.</summary>
    private sealed class CategorySection
    {
        public string Title;
        public Button Header;
        public Control Content;
        public bool Expanded = true;
    }

    public override void _Ready()
    {
        _globalSignals = GetNode<GlobalSignals>("/root/GlobalSignals");
        _globalData = GetNode<GlobalData>("/root/GlobalData");

        _inputsContainer = GetNode<VBoxContainer>("%InputsContainer");

        // Load by path for now (uid will be assigned by editor on first save of the scene).
        _inputActionCardScene = SceneLoader.LoadPackedScene(
            "res://src/UI/Settings/InputActionCard.tscn", out string err);
        if (_inputActionCardScene == null)
        {
            GD.PrintErr($"SettingsInputMap:_Ready - Failed to load InputActionCard.tscn: {err}");
        }

        VisibilityChanged += OnVisibilityChanged;

        // Initial populate if already visible (e.g. opened directly)
        if (IsVisibleInTree())
        {
            PopulateActions();
        }
    
        UiLocalizer.LocalizeTree(this);
        if (_globalSignals != null)
            _globalSignals.LocaleChanged += OnLocaleChanged;
}

    public override void _ExitTree()
    {
        if (_globalSignals != null)
            _globalSignals.LocaleChanged -= OnLocaleChanged;

        VisibilityChanged -= OnVisibilityChanged;
        base._ExitTree();
    }

    private void OnVisibilityChanged()
    {
        if (Visible)
        {
            PopulateActions();
        }
        else
        {
            ClearCards();
        }
    }

    /// <summary>
    /// Clears existing category sections and action cards from the container.
    /// </summary>
    private void ClearCards()
    {
        _activeListeningCard = null;
        _cardsByAction.Clear();
        _sectionByAction.Clear();

        if (_inputsContainer == null) return;
        foreach (Node child in _inputsContainer.GetChildren())
        {
            _inputsContainer.RemoveChild(child);
            child.QueueFree();
        }
    }

    /// <summary>
    /// Reads actions from the project InputMap and creates categorized accordion sections.
    /// </summary>
    private void PopulateActions()
    {
        if (_inputsContainer == null || _inputActionCardScene == null) return;

        ClearCards();

        var managed = GlobalData.MappableInputActions;
        var remaining = new HashSet<string>(managed.Where(a => InputMap.HasAction(a)));

        // Category sections in defined order.
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

        // Any curated actions not assigned to a category.
        if (remaining.Count > 0)
        {
            AddCategorySection("Other", remaining.OrderBy(a => a).ToList());
            remaining.Clear();
        }

        // Discover runtime non-ui_ actions missing from the curated list.
        var discovered = new List<string>();
        foreach (StringName actionName in InputMap.GetActions())
        {
            string name = actionName.ToString();
            if (name.StartsWith("ui_")) continue;
            if (managed.Contains(name)) continue;
            if (_cardsByAction.ContainsKey(name)) continue;
            discovered.Add(name);
        }

        if (discovered.Count > 0)
        {
            discovered.Sort(StringComparer.Ordinal);
            AddCategorySection("Other", discovered);
        }

        GD.Print($"SettingsInputMap:PopulateActions - Populated {_cardsByAction.Count} input action cards in categories");
    }

    /// <summary>
    /// Builds a collapsible accordion section with a header button and a flow of action cards.
    /// </summary>
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

        // Attach section to the tree first so card _Ready runs when children are added.
        sectionRoot.AddChild(header);
        sectionRoot.AddChild(content);

        // Subtle separator under each category group.
        var sep = new HSeparator();
        sep.AddThemeConstantOverride("separation", 6);
        sectionRoot.AddChild(sep);

        _inputsContainer.AddChild(sectionRoot);

        foreach (var actionName in actions)
        {
            var card = _inputActionCardScene.Instantiate<InputActionCard>();
            card.SetAction(actionName);
            content.AddChild(card);
            card.BindingConflict += OnBindingConflict;
            card.ListeningStarted += OnCardListeningStarted;
            _cardsByAction[actionName] = card;
            _sectionByAction[actionName] = section;
        }
    }

    /// <summary>
    /// Ensures only one card listens at a time: cancel any previous rebind when a new one starts.
    /// </summary>
    private void OnCardListeningStarted(InputActionCard card)
    {
        if (_activeListeningCard != null &&
            _activeListeningCard != card &&
            IsInstanceValid(_activeListeningCard))
        {
            // Another card is taking over; don't re-enable global input until the new card finishes.
            _activeListeningCard.CancelListening(emitFocusExit: false);
        }

        _activeListeningCard = card;
    }

    /// <summary>
    /// Called when a card rejects a rebind because another action already uses that key combo.
    /// Expands the owning category if needed and flashes the conflicting card red.
    /// </summary>
    private void OnBindingConflict(string conflictingAction, string attemptedCombo)
    {
        if (string.IsNullOrEmpty(conflictingAction)) return;

        // Ensure the conflicting card's category is expanded so the user can see it.
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
        {
            card.FlashConflict();
        }

        string pretty = PrettifyActionName(conflictingAction);
        string msg = string.IsNullOrEmpty(attemptedCombo)
            ? $"Hotkey already used by '{pretty}'."
            : $"Hotkey '{attemptedCombo}' is already used by '{pretty}'.";
        _globalSignals?.EmitSignal(nameof(GlobalSignals.Log), msg, (int)LogType.Warning);
        GD.Print($"SettingsInputMap:OnBindingConflict - {msg}");
    }

    private static string PrettifyActionName(string action)
    {
        if (string.IsNullOrEmpty(action)) return "";
        string result = "";
        for (int i = 0; i < action.Length; i++)
        {
            char c = action[i];
            if (i > 0 && char.IsUpper(c) && (char.IsLower(action[i - 1]) || char.IsDigit(action[i - 1])))
            {
                result += " ";
            }
            result += c;
        }
        return result;
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
        RelocalizeCategoryHeaders();
        foreach (var card in _cardsByAction.Values)
        {
            if (card != null && GodotObject.IsInstanceValid(card))
                card.RefreshDisplay();
        }
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

}
