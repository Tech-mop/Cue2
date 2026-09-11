// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System;
using Cue2.Domain.Connections;
using Cue2.Services;
using Cue2.UI.Utilities;
using Godot;

namespace Cue2.UI.Settings;

/// <summary>
/// Card for assigning a user-defined OSC address to a project InputMap action.
/// Binding is typed in a LineEdit (not capture-from-listener).
/// </summary>
public partial class OscInputActionCard : PanelContainer
{
    private GlobalSignals _globalSignals;
    private GlobalData _globalData;
    private OscListen _oscListen;

    [Export] public string Action { get; set; } = "";

    private Label _actionNameLabel;
    private LineEdit _bindingLineEdit;
    private Button _resetButton;
    private Button _clearButton;

    private bool _isSyncingUi;
    private bool _editing;

    private StyleBoxFlat _normalStyle;
    private StyleBoxFlat _flashStyle;
    private Tween _flashTween;

    /// <summary>
    /// Raised when a binding is rejected because another action already uses the pattern.
    /// </summary>
    public event Action<string, string> BindingConflict;

    public override void _Ready()
    {
        _globalSignals = GetNodeOrNull<GlobalSignals>("/root/GlobalSignals");
        _globalData = GetNodeOrNull<GlobalData>("/root/GlobalData");
        _oscListen = GetNodeOrNull<OscListen>("/root/OscListen");

        _actionNameLabel = GetNodeOrNull<Label>("%ActionName");
        _bindingLineEdit = GetNodeOrNull<LineEdit>("%BindingLineEdit");
        _resetButton = GetNodeOrNull<Button>("%ResetButton");
        _clearButton = GetNodeOrNull<Button>("%ClearButton");

        CaptureNormalStyle();

        if (_bindingLineEdit != null)
        {
            _bindingLineEdit.TextSubmitted += OnBindingTextSubmitted;
            _bindingLineEdit.FocusEntered += OnBindingFocusEntered;
            _bindingLineEdit.FocusExited += OnBindingFocusExited;
            _bindingLineEdit.TextChanged += OnBindingTextChanged;
        }

        if (_resetButton != null)
        {
            _resetButton.Pressed += OnResetPressed;
            try
            {
                _resetButton.Icon = GetThemeIcon("Refresh", "AtlasIcons");
                _resetButton.ExpandIcon = true;
            }
            catch { /* optional */ }
        }
        if (_clearButton != null)
        {
            _clearButton.Pressed += OnClearPressed;
            try
            {
                _clearButton.Icon = GetThemeIcon("DeleteBin", "AtlasIcons");
                _clearButton.ExpandIcon = true;
            }
            catch { /* optional */ }
        }

        ApplyActionLabel();
        RefreshDisplay();
    }

    public override void _ExitTree()
    {
        if (_bindingLineEdit != null)
        {
            _bindingLineEdit.TextSubmitted -= OnBindingTextSubmitted;
            _bindingLineEdit.FocusEntered -= OnBindingFocusEntered;
            _bindingLineEdit.FocusExited -= OnBindingFocusExited;
            _bindingLineEdit.TextChanged -= OnBindingTextChanged;
        }
        if (_flashTween != null && GodotObject.IsInstanceValid(_flashTween))
        {
            _flashTween.Kill();
            _flashTween = null;
        }
        base._ExitTree();
    }

    /// <summary>
    /// Sets the InputMap action this card represents.
    /// </summary>
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

    /// <summary>
    /// Updates the LineEdit and button visibility from <see cref="OscListen"/>.
    /// </summary>
    public void RefreshDisplay()
    {
        if (_bindingLineEdit == null) return;
        if (_editing) return;

        var binding = _oscListen?.GetInputMapBinding(Action) ?? OscActionBinding.GetDefaultFor(Action);
        _isSyncingUi = true;
        try
        {
            _bindingLineEdit.Text = binding.HasBinding ? binding.Address : string.Empty;
            _bindingLineEdit.PlaceholderText = binding.HasBinding ? string.Empty : UiLocalizer.T("None");
        }
        finally
        {
            _isSyncingUi = false;
        }

        bool nonDefault = binding.IsNonDefaultFor(Action)
                          || (_oscListen?.IsInputMapBindingOverridden(Action) ?? false);
        if (_resetButton != null)
        {
            _resetButton.Visible = nonDefault;
            var factory = OscActionBinding.GetDefaultFor(Action);
            _resetButton.TooltipText = factory.HasBinding
                ? UiLocalizer.Tf("Reset to default ({0})", factory.Address)
                : UiLocalizer.T("Reset to default (no OSC)");
        }
    }

    private void OnBindingFocusEntered()
    {
        _editing = true;
        _globalSignals?.EmitSignal(nameof(GlobalSignals.TextEditFocusEntered));
    }

    private void OnBindingFocusExited()
    {
        if (!_editing) return;
        _editing = false;
        CommitBindingText(_bindingLineEdit?.Text ?? string.Empty);
        _globalSignals?.EmitSignal(nameof(GlobalSignals.TextEditFocusExited));
    }

    private void OnBindingTextSubmitted(string text)
    {
        CommitBindingText(text);
        _bindingLineEdit?.ReleaseFocus();
    }

    private void OnBindingTextChanged(string _)
    {
        // Live typing only; commit on submit / focus exit.
    }

    private void CommitBindingText(string text)
    {
        if (_isSyncingUi || _globalData?.HistoryManager?.IsRestoring == true) return;
        if (_oscListen == null) return;

        string trimmed = (text ?? string.Empty).Trim();
        var current = _oscListen.GetInputMapBinding(Action);

        // Empty → clear (unbound override if there was a default).
        if (string.IsNullOrEmpty(trimmed))
        {
            if (!current.HasBinding && !_oscListen.IsInputMapBindingOverridden(Action))
            {
                RefreshDisplay();
                return;
            }
            RecordHistory("Clear OSC Input Map binding");
            _oscListen.SetInputMapBinding(Action, OscActionBinding.Unbound());
            RefreshDisplay();
            return;
        }

        var candidate = new OscActionBinding();
        if (!candidate.SetFromAddress(trimmed))
        {
            _globalSignals?.EmitSignal(nameof(GlobalSignals.Log),
                "OSC Input Map: path must start with / and contain no spaces.",
                (int)LogType.Warning);
            RefreshDisplay();
            return;
        }

        if (current.EqualsBinding(candidate))
        {
            // Normalise display (e.g. missing leading slash).
            RefreshDisplay();
            return;
        }

        string conflict = _oscListen.FindConflictingInputMapAction(Action, candidate);
        if (!string.IsNullOrEmpty(conflict))
        {
            BindingConflict?.Invoke(conflict, candidate.GetDisplay());
            GD.Print($"OscInputActionCard:Commit - Rejected '{candidate.GetDisplay()}' for '{Action}'; used by '{conflict}'");
            RefreshDisplay();
            return;
        }

        RecordHistory("Set OSC Input Map binding");
        _oscListen.SetInputMapBinding(Action, candidate);
        RefreshDisplay();
        GD.Print($"OscInputActionCard:Commit - '{Action}' ← {candidate.GetDisplay()}");
    }

    private void OnClearPressed()
    {
        if (_globalData?.HistoryManager?.IsRestoring == true) return;
        if (_oscListen == null) return;

        var current = _oscListen.GetInputMapBinding(Action);
        if (!current.HasBinding && _oscListen.IsInputMapBindingOverridden(Action))
            return; // already cleared
        if (!current.HasBinding && !_oscListen.IsInputMapBindingOverridden(Action))
            return; // already default unbound

        _editing = false;
        RecordHistory("Clear OSC Input Map binding");
        _oscListen.SetInputMapBinding(Action, OscActionBinding.Unbound());
        RefreshDisplay();
    }

    private void OnResetPressed()
    {
        if (_globalData?.HistoryManager?.IsRestoring == true) return;
        if (_oscListen == null) return;
        if (!_oscListen.IsInputMapBindingOverridden(Action)
            && !_oscListen.GetInputMapBinding(Action).IsNonDefaultFor(Action))
            return;

        _editing = false;
        RecordHistory("Reset OSC Input Map binding");
        _oscListen.ResetInputMapBinding(Action);
        RefreshDisplay();
    }

    private void RecordHistory(string description)
    {
        var history = _globalData?.HistoryManager;
        if (history == null || history.IsRestoring) return;
        history.RecordSettingsChange(description, null, "OscInputMap");
    }

    /// <summary>
    /// Briefly highlights this card in red when another rebind collides with it.
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

        _flashStyle.BorderColor = GlobalStyles.Danger;
        _flashStyle.BgColor = new Color(0.32f, 0.08f, 0.08f, 1f);
        AddThemeStyleboxOverride("panel", _flashStyle);

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
            _normalStyle = (StyleBoxFlat)flat.Duplicate();
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
