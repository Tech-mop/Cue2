// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System;
using Godot;
using Cue2.Services;

using Cue2.UI.Utilities;
namespace Cue2.UI.Settings;

/// <summary>
/// A card displayed in the Input Map settings representing a single InputMap action.
/// Allows viewing the current binding and rebinding or clearing it.
/// Rejects rebinds that collide with another action and can flash red on conflict.
/// </summary>
public partial class InputActionCard : PanelContainer
{
    private GlobalSignals _globalSignals;
    private GlobalData _globalData;

    [Export] public string Action { get; set; } = "";

    private Label _actionNameLabel;
    private Button _bindingButton;
    private Button _resetButton;
    private Button _clearButton;

    private bool _isListeningForInput;

    private StyleBoxFlat _normalStyle;
    private StyleBoxFlat _flashStyle;
    private Tween _flashTween;

    /// <summary>
    /// Raised when a rebind is rejected because <paramref name="conflictingAction"/> already uses the combo.
    /// Second argument is a display string for the attempted key combo.
    /// </summary>
    public event Action<string, string> BindingConflict;

    /// <summary>
    /// Raised when this card begins listening for a new key (so the parent can cancel any other active rebind).
    /// </summary>
    public event Action<InputActionCard> ListeningStarted;

    /// <summary>
    /// True while this card is waiting for a key press to rebind.
    /// </summary>
    public bool IsListeningForInput => _isListeningForInput;

    public override void _Ready()
    {
        _globalSignals = GetNode<GlobalSignals>("/root/GlobalSignals");
        _globalData = GetNode<GlobalData>("/root/GlobalData");

        _actionNameLabel = GetNode<Label>("%ActionName");
        _bindingButton = GetNode<Button>("%BindingButton");
        _resetButton = GetNode<Button>("%ResetButton");
        _clearButton = GetNode<Button>("%ClearButton");

        CaptureNormalStyle();

        _bindingButton.Pressed += OnBindingButtonPressed;
        _resetButton.Pressed += OnResetButtonPressed;
        _clearButton.Pressed += OnClearButtonPressed;

        _clearButton.Icon = GetThemeIcon("DeleteBin", "AtlasIcons");
        _resetButton.Icon = GetThemeIcon("Refresh", "AtlasIcons");

        // SetAction may run before _Ready when the card is parented off-tree; apply stored Action now.
        ApplyActionLabel();
        RefreshDisplay();
    }

    /// <summary>
    /// Sets the action this card represents and refreshes its display.
    /// Safe to call before the card enters the tree (_Ready applies labels when ready).
    /// </summary>
    /// <param name="actionName">The InputMap action name (e.g. "Go", "CreateCue").</param>
    public void SetAction(string actionName)
    {
        Action = actionName;
        ApplyActionLabel();
        RefreshDisplay();
    }

    private void ApplyActionLabel()
    {
        if (_actionNameLabel == null) return;
        string label = UiLocalizer.T(PrettifyActionName(Action));
        _actionNameLabel.Text = label;
        // Full name always available on hover when the ellipsis clips the label.
        _actionNameLabel.TooltipText = label;
    }

    private static string PrettifyActionName(string action)
    {
        if (string.IsNullOrEmpty(action)) return "";
        // Insert spaces before capitals and treat common patterns
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
    /// Updates the binding button text from the current state of InputMap for this action.
    /// Also updates visibility and tooltip of the reset-to-default button.
    /// </summary>
    public void RefreshDisplay()
    {
        if (_bindingButton == null) return;
        if (string.IsNullOrEmpty(Action) || !InputMap.HasAction(Action))
        {
            _bindingButton.Text = UiLocalizer.T("Unbound");
            UpdateResetButton();
            return;
        }

        var events = InputMap.ActionGetEvents(Action);
        if (events.Count == 0)
        {
            _bindingButton.Text = UiLocalizer.T("Unbound");
            UpdateResetButton();
            return;
        }

        // Build a compact representation. For v1 show up to first two bindings.
        var parts = new System.Collections.Generic.List<string>();
        int shown = 0;
        foreach (InputEvent ev in events)
        {
            if (shown >= 2) break;
            string s = GlobalData.FormatInputEvent(ev);
            if (!string.IsNullOrEmpty(s))
            {
                parts.Add(s);
                shown++;
            }
        }
        _bindingButton.Text = string.Join(" / ", parts);
        if (events.Count > 2)
        {
            _bindingButton.Text += " …";
        }

        UpdateResetButton();
    }

    private void UpdateResetButton()
    {
        if (_resetButton == null) return;

        bool atDefault = _globalData != null && _globalData.IsInputActionAtDefault(Action);
        _resetButton.Visible = !atDefault;

        if (!atDefault)
        {
            string defaultText = _globalData?.GetDefaultBindingDisplay(Action) ?? "default";
            _resetButton.TooltipText = UiLocalizer.ResetDefaultTip(defaultText);
        }
    }

    private void OnBindingButtonPressed()
    {
        if (_isListeningForInput)
        {
            CancelListening();
            return;
        }

        StartListening();
    }

    private void OnClearButtonPressed()
    {
        if (string.IsNullOrEmpty(Action) || !InputMap.HasAction(Action)) return;

        // Keyboard Input Map is app preference only (user://) — not document undo (P2-14).
        InputMap.ActionEraseEvents(Action);
        _globalData?.UserDataManager?.PersistLiveInputMap();
        GD.Print($"InputActionCard:OnClearButtonPressed - Cleared events for action '{Action}'");
        RefreshDisplay();
    }

    private void OnResetButtonPressed()
    {
        if (string.IsNullOrEmpty(Action) || _globalData == null) return;

        // Block reset if any default event collides with another action's current binding.
        var defaults = _globalData.GetDefaultInputEvents(Action);
        foreach (InputEvent ev in defaults)
        {
            if (ev is not InputEventKey key) continue;
            string conflict = GlobalData.FindConflictingInputAction(Action, key);
            if (!string.IsNullOrEmpty(conflict))
            {
                string combo = GlobalData.FormatInputEvent(key);
                BindingConflict?.Invoke(conflict, combo);
                GD.Print($"InputActionCard:OnResetButtonPressed - Reset blocked for '{Action}'; default '{combo}' used by '{conflict}'");
                return;
            }
        }

        // Keyboard Input Map is app preference only (user://) — not document undo (P2-14).
        _globalData.ResetInputActionToDefault(Action);
        _globalData.UserDataManager?.PersistLiveInputMap();
        GD.Print($"InputActionCard:OnResetButtonPressed - Reset '{Action}' to default via refresh button.");
        RefreshDisplay();
    }

    private void StartListening()
    {
        // Let the parent cancel any other card first (exclusive rebind).
        ListeningStarted?.Invoke(this);

        _isListeningForInput = true;
        _bindingButton.Text = UiLocalizer.T("Press key... (Esc cancels)");
        // Pause global input action listener (reuses existing focus signals as a coordination mechanism)
        _globalSignals.EmitSignal(nameof(GlobalSignals.TextEditFocusEntered));
        GD.Print($"InputActionCard:StartListening - Listening for new binding for '{Action}'");
    }

    /// <summary>
    /// Cancels an in-progress rebind, if any. Safe to call when not listening.
    /// </summary>
    /// <param name="emitFocusExit">
    /// When false, skips the TextEditFocusExited signal (used when another card is immediately taking over listening).
    /// </param>
    public void CancelListening(bool emitFocusExit = true)
    {
        if (!_isListeningForInput) return;

        _isListeningForInput = false;
        RefreshDisplay();
        if (emitFocusExit)
            _globalSignals.EmitSignal(nameof(GlobalSignals.TextEditFocusExited));
        GD.Print($"InputActionCard:CancelListening - Cancelled listening for '{Action}'");
    }

    public override void _UnhandledKeyInput(InputEvent @event)
    {
        if (!_isListeningForInput) return;
        if (@event is not InputEventKey keyEvent || !keyEvent.Pressed) return;
        // Ignore pure modifier presses (user is still holding Ctrl etc.)
        if (IsModifierOnlyKey(keyEvent.Keycode)) return;

        if (keyEvent.Keycode == Key.Escape)
        {
            CancelListening();
            GetViewport().SetInputAsHandled();
            return;
        }

        GetViewport().SetInputAsHandled();

        // Reject if another action already uses this combo.
        string conflict = GlobalData.FindConflictingInputAction(Action, keyEvent);
        if (!string.IsNullOrEmpty(conflict))
        {
            string combo = GlobalData.FormatInputEvent(keyEvent);
            BindingConflict?.Invoke(conflict, combo);
            GD.Print($"InputActionCard:_UnhandledInput - Rejected '{combo}' for '{Action}'; used by '{conflict}'");
            // Stay listening so the user can try another key, but restore button hint.
            _bindingButton.Text = UiLocalizer.T("Press key... (Esc cancels)");
            return;
        }

        // Apply the captured key as the (sole) binding for this action.
        ApplyNewBinding(keyEvent);
        _isListeningForInput = false;
        RefreshDisplay();
        _globalSignals.EmitSignal(nameof(GlobalSignals.TextEditFocusExited));
        GD.Print($"InputActionCard:_UnhandledInput - Set binding for '{Action}' to {GlobalData.FormatInputEvent(keyEvent)}");
    }

    private static bool IsModifierOnlyKey(Key keycode)
    {
        // Ignore pure modifier presses while listening (user is composing a combo).
        return keycode is Key.Ctrl or Key.Shift or Key.Alt or Key.Meta;
    }

    private void ApplyNewBinding(InputEventKey source)
    {
        if (string.IsNullOrEmpty(Action) || !InputMap.HasAction(Action)) return;

        // For v1: replace all existing bindings with this single key event.
        // Keyboard shortcuts are Cue2 Preferences (user://user_data.json), not showfile
        // and not document undo — persist immediately (P2-14).
        InputMap.ActionEraseEvents(Action);

        var newEvent = new InputEventKey
        {
            Keycode = source.Keycode,
            PhysicalKeycode = source.PhysicalKeycode,
            CtrlPressed = source.CtrlPressed,
            ShiftPressed = source.ShiftPressed,
            AltPressed = source.AltPressed,
            MetaPressed = source.MetaPressed,
        };

        InputMap.ActionAddEvent(Action, newEvent);
        _globalData?.UserDataManager?.PersistLiveInputMap();
    }

    /// <summary>
    /// Briefly highlights this card in red (border + tint), then fades back to normal.
    /// Used when another rebind attempt collides with this card's binding.
    /// </summary>
    public void FlashConflict()
    {
        CaptureNormalStyle();
        EnsureFlashStyle();

        if (_flashTween != null && GodotObject.IsInstanceValid(_flashTween))
        {
            _flashTween.Kill();
            _flashTween = null;
        }

        // Instant red state.
        _flashStyle.BorderColor = GlobalStyles.Danger;
        _flashStyle.BgColor = new Color(0.32f, 0.08f, 0.08f, 1f);
        AddThemeStyleboxOverride("panel", _flashStyle);

        // Hold red briefly, then fade border/bg back toward normal and restore style.
        _flashTween = CreateTween();
        _flashTween.SetParallel(true);
        _flashTween.TweenProperty(_flashStyle, "border_color", _normalStyle.BorderColor, 0.85f)
            .SetDelay(0.45f)
            .SetTrans(Tween.TransitionType.Sine)
            .SetEase(Tween.EaseType.InOut);
        _flashTween.TweenProperty(_flashStyle, "bg_color", _normalStyle.BgColor, 0.85f)
            .SetDelay(0.45f)
            .SetTrans(Tween.TransitionType.Sine)
            .SetEase(Tween.EaseType.InOut);
        _flashTween.SetParallel(false);
        _flashTween.TweenCallback(Callable.From(RestoreNormalStyle));
    }

    private void CaptureNormalStyle()
    {
        if (_normalStyle != null) return;

        var existing = GetThemeStylebox("panel");
        if (existing is StyleBoxFlat flat)
        {
            _normalStyle = (StyleBoxFlat)flat.Duplicate();
        }
        else
        {
            _normalStyle = new StyleBoxFlat
            {
                BgColor = new Color(0.12f, 0.12f, 0.12f, 1f),
                BorderColor = new Color(0.35f, 0.35f, 0.35f, 1f),
            };
            _normalStyle.SetBorderWidthAll(1);
            _normalStyle.SetCornerRadiusAll(4);
        }
    }

    private void EnsureFlashStyle()
    {
        if (_flashStyle != null) return;
        CaptureNormalStyle();
        _flashStyle = (StyleBoxFlat)_normalStyle.Duplicate();
        _flashStyle.SetBorderWidthAll(Math.Max(2, _normalStyle.GetBorderWidth(Side.Left)));
    }

    private void RestoreNormalStyle()
    {
        if (_normalStyle != null)
            AddThemeStyleboxOverride("panel", _normalStyle);
        else
            RemoveThemeStyleboxOverride("panel");
    }

    public override void _ExitTree()
    {
        if (_flashTween != null && GodotObject.IsInstanceValid(_flashTween))
        {
            _flashTween.Kill();
            _flashTween = null;
        }

        if (_isListeningForInput)
        {
            // Ensure we re-enable global listener if card is removed while listening
            _globalSignals.EmitSignal(nameof(GlobalSignals.TextEditFocusExited));
        }
    }
}
