// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System;
using Cue2.Domain.Cues;
using Cue2.Services;
using Godot;

using Cue2.UI.Utilities;
namespace Cue2.UI.Settings;

/// <summary>
/// Card for assigning a MIDI control to a project InputMap action.
/// Capture arms <see cref="MidiManager"/> for the next message (like cue MIDI capture).
/// </summary>
public partial class MidiInputActionCard : PanelContainer
{
    private GlobalSignals _globalSignals;
    private GlobalData _globalData;
    private MidiManager _midiManager;

    [Export] public string Action { get; set; } = "";

    private Label _actionNameLabel;
    private Button _bindingButton;
    private Button _resetButton;
    private Button _clearButton;

    private bool _isCapturing;
    private bool _captureSubscribed;

    private StyleBoxFlat _normalStyle;
    private StyleBoxFlat _flashStyle;
    private Tween _flashTween;

    /// <summary>
    /// Raised when a binding is rejected because another action already uses the pattern.
    /// </summary>
    public event Action<string, string> BindingConflict;

    /// <summary>
    /// Raised when this card starts capture so the parent can cancel other cards.
    /// </summary>
    public event Action<MidiInputActionCard> CaptureStarted;

    /// <summary>True while waiting for the next MIDI message.</summary>
    public bool IsCapturing => _isCapturing;

    public override void _Ready()
    {
        _globalSignals = GetNodeOrNull<GlobalSignals>("/root/GlobalSignals");
        _globalData = GetNodeOrNull<GlobalData>("/root/GlobalData");
        _midiManager = GetNodeOrNull<MidiManager>("/root/MidiManager");

        _actionNameLabel = GetNodeOrNull<Label>("%ActionName");
        _bindingButton = GetNodeOrNull<Button>("%BindingButton");
        _resetButton = GetNodeOrNull<Button>("%ResetButton");
        _clearButton = GetNodeOrNull<Button>("%ClearButton");

        CaptureNormalStyle();

        if (_bindingButton != null)
            _bindingButton.Pressed += OnBindingButtonPressed;
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
        CancelCapture(emitFocusExit: true);
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
    /// Updates the binding button and reset visibility from <see cref="MidiManager"/>.
    /// </summary>
    public void RefreshDisplay()
    {
        if (_bindingButton == null) return;
        if (_isCapturing) return;

        var binding = _midiManager?.GetInputMapBinding(Action) ?? MidiActionBinding.Unbound();
        if (!binding.HasBinding)
            _bindingButton.Text = UiLocalizer.T("None");
        else
            _bindingButton.Text = binding.GetDisplay();

        if (_resetButton != null)
        {
            _resetButton.Visible = binding.IsNonDefault;
            if (binding.IsNonDefault)
                _resetButton.TooltipText = UiLocalizer.T("Reset to default (no MIDI)");
        }
    }

    private void OnBindingButtonPressed()
    {
        if (_globalData?.HistoryManager?.IsRestoring == true) return;

        if (_isCapturing)
        {
            CancelCapture(emitFocusExit: true);
            return;
        }

        if (_midiManager == null)
        {
            _globalSignals?.EmitSignal(nameof(GlobalSignals.Log),
                "MIDI Input Map: MidiManager not found.", (int)LogType.Warning);
            return;
        }

        if (!_midiManager.MidiEnabled || _midiManager.OpenInputCount == 0)
        {
            _globalSignals?.EmitSignal(nameof(GlobalSignals.Log),
                "MIDI Input Map: enable MIDI and open a session input first (Settings → MIDI).",
                (int)LogType.Warning);
            return;
        }

        CaptureStarted?.Invoke(this);
        StartCapture();
    }

    private void StartCapture()
    {
        _isCapturing = true;
        if (_bindingButton != null)
            _bindingButton.Text = UiLocalizer.T("MIDI… (Esc)");

        if (!_captureSubscribed && _midiManager != null)
        {
            _midiManager.MidiCaptured += OnMidiCaptured;
            _captureSubscribed = true;
        }

        _midiManager?.StartCapture();
        // Pause keyboard shortcuts while capturing (same coordination as key rebind).
        _globalSignals?.EmitSignal(nameof(GlobalSignals.TextEditFocusEntered));
        GD.Print($"MidiInputActionCard:StartCapture - '{Action}'");
    }

    /// <summary>
    /// Cancels capture on this card. Safe when not capturing.
    /// </summary>
    /// <param name="emitFocusExit">When false, another card is taking over capture immediately.</param>
    public void CancelCapture(bool emitFocusExit = true)
    {
        if (!_isCapturing && !_captureSubscribed) return;

        _isCapturing = false;
        if (_captureSubscribed && _midiManager != null)
        {
            _midiManager.MidiCaptured -= OnMidiCaptured;
            _captureSubscribed = false;
        }

        if (_midiManager != null && _midiManager.IsCapturing)
            _midiManager.CancelCapture();

        RefreshDisplay();
        if (emitFocusExit)
            _globalSignals?.EmitSignal(nameof(GlobalSignals.TextEditFocusExited));
        GD.Print($"MidiInputActionCard:CancelCapture - '{Action}'");
    }

    private void OnMidiCaptured(string deviceName, int messageType, int channel, int data1, int data2)
    {
        if (!_isCapturing) return;

        _isCapturing = false;
        if (_captureSubscribed && _midiManager != null)
        {
            _midiManager.MidiCaptured -= OnMidiCaptured;
            _captureSubscribed = false;
        }

        _globalSignals?.EmitSignal(nameof(GlobalSignals.TextEditFocusExited));

        var type = (MidiTriggerMessageType)messageType;
        // CC defaults to match value; notes ignore velocity for practical control maps.
        bool matchValue = type == MidiTriggerMessageType.ControlChange;

        var candidate = new MidiActionBinding();
        candidate.SetFromMessage(type, channel, data1, data2, matchValue);

        string conflict = _midiManager?.FindConflictingInputMapAction(Action, candidate);
        if (!string.IsNullOrEmpty(conflict))
        {
            string combo = candidate.GetDisplay();
            BindingConflict?.Invoke(conflict, combo);
            GD.Print($"MidiInputActionCard:OnMidiCaptured - Rejected '{combo}' for '{Action}'; used by '{conflict}'");
            RefreshDisplay();
            return;
        }

        RecordHistory("Set MIDI Input Map binding");
        _midiManager?.SetInputMapBinding(Action, candidate);
        RefreshDisplay();
        GD.Print($"MidiInputActionCard:OnMidiCaptured - '{Action}' ← {candidate.GetDisplay()}");
    }

    private void OnClearPressed()
    {
        if (_globalData?.HistoryManager?.IsRestoring == true) return;
        if (_midiManager == null) return;

        var current = _midiManager.GetInputMapBinding(Action);
        if (!current.HasBinding) return;

        if (_isCapturing)
            CancelCapture(emitFocusExit: true);

        RecordHistory("Clear MIDI Input Map binding");
        _midiManager.SetInputMapBinding(Action, MidiActionBinding.Unbound());
        RefreshDisplay();
    }

    private void OnResetPressed()
    {
        // Default is unbound — same as clear.
        OnClearPressed();
    }

    private void RecordHistory(string description)
    {
        var history = _globalData?.HistoryManager;
        if (history == null || history.IsRestoring) return;
        history.RecordSettingsChange(description, null, "MidiInputMap");
    }

    public override void _UnhandledKeyInput(InputEvent @event)
    {
        if (!_isCapturing) return;
        if (@event is not InputEventKey key || !key.Pressed) return;
        if (key.Keycode != Key.Escape) return;

        CancelCapture(emitFocusExit: true);
        GetViewport().SetInputAsHandled();
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
