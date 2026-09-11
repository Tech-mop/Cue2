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
using Cue2.UI.Utilities;
using Godot;
using AppSettings = Cue2.Domain.ShowSettings.Settings;

namespace Cue2.UI.Settings;

/// <summary>
/// Settings panel for show-scoped text component defaults (target layer, typography, alignment,
/// duration, opacity, outline/background, fades). Applied when a new text component is created.
/// </summary>
/// <remarks>
/// Content starts empty. Stored with the showfile via <see cref="AppSettings"/> under <c>TextDefaults</c>.
/// </remarks>
public partial class SettingsTextDefaults : ScrollContainer
{
    private GlobalSignals _globalSignals;
    private GlobalData _globalData;
    private HistoryManager _historyManager;

    private OptionButton _targetLayerOption;
    private Button _targetLayerResetButton;

    private LineEdit _durationInput;
    private Button _durationResetButton;

    private SpinBox _opacitySpin;
    private Button _opacityResetButton;

    private CheckBox _bbcodeCheckBox;
    private Button _bbcodeResetButton;

    private LineEdit _fontNameInput;
    private Button _fontNameResetButton;

    private SpinBox _fontSizeSpin;
    private Button _fontSizeResetButton;

    private ColorPickerButton _fontColorPicker;
    private Button _fontColorResetButton;

    private OptionButton _hAlignOption;
    private Button _hAlignResetButton;

    private OptionButton _vAlignOption;
    private Button _vAlignResetButton;

    private CheckBox _autowrapCheckBox;
    private Button _autowrapResetButton;

    private SpinBox _marginsSpin;
    private Button _marginsResetButton;

    private SpinBox _outlineSizeSpin;
    private Button _outlineSizeResetButton;

    private ColorPickerButton _outlineColorPicker;
    private Button _outlineColorResetButton;

    private CheckBox _backgroundCheckBox;
    private Button _backgroundResetButton;

    private ColorPickerButton _backgroundColorPicker;
    private Button _backgroundColorResetButton;

    private LineEdit _fadeInInput;
    private Button _fadeInResetButton;

    private LineEdit _fadeOutInput;
    private Button _fadeOutResetButton;

    private bool _isSyncingUi;

    /// <inheritdoc />
    public override void _Ready()
    {
        GD.Print("SettingsTextDefaults:_Ready - Text Defaults panel init");

        _globalSignals = GetNode<GlobalSignals>("/root/GlobalSignals");
        _globalData = GetNode<GlobalData>("/root/GlobalData");
        _historyManager = _globalData?.HistoryManager;

        _targetLayerOption = GetNodeOrNull<OptionButton>("%TargetLayerOption");
        _targetLayerResetButton = GetNodeOrNull<Button>("%TargetLayerResetButton");
        _durationInput = GetNode<LineEdit>("%DurationInput");
        _durationResetButton = GetNode<Button>("%DurationResetButton");
        _opacitySpin = GetNode<SpinBox>("%OpacitySpin");
        _opacityResetButton = GetNode<Button>("%OpacityResetButton");
        _bbcodeCheckBox = GetNode<CheckBox>("%BbcodeCheckBox");
        _bbcodeResetButton = GetNode<Button>("%BbcodeResetButton");
        _fontNameInput = GetNode<LineEdit>("%FontNameInput");
        _fontNameResetButton = GetNode<Button>("%FontNameResetButton");
        _fontSizeSpin = GetNode<SpinBox>("%FontSizeSpin");
        _fontSizeResetButton = GetNode<Button>("%FontSizeResetButton");
        _fontColorPicker = GetNode<ColorPickerButton>("%FontColorPicker");
        _fontColorResetButton = GetNode<Button>("%FontColorResetButton");
        _hAlignOption = GetNode<OptionButton>("%HAlignOption");
        _hAlignResetButton = GetNode<Button>("%HAlignResetButton");
        _vAlignOption = GetNode<OptionButton>("%VAlignOption");
        _vAlignResetButton = GetNode<Button>("%VAlignResetButton");
        _autowrapCheckBox = GetNode<CheckBox>("%AutowrapCheckBox");
        _autowrapResetButton = GetNode<Button>("%AutowrapResetButton");
        _marginsSpin = GetNode<SpinBox>("%MarginsSpin");
        _marginsResetButton = GetNode<Button>("%MarginsResetButton");
        _outlineSizeSpin = GetNode<SpinBox>("%OutlineSizeSpin");
        _outlineSizeResetButton = GetNode<Button>("%OutlineSizeResetButton");
        _outlineColorPicker = GetNode<ColorPickerButton>("%OutlineColorPicker");
        _outlineColorResetButton = GetNode<Button>("%OutlineColorResetButton");
        _backgroundCheckBox = GetNode<CheckBox>("%BackgroundCheckBox");
        _backgroundResetButton = GetNode<Button>("%BackgroundResetButton");
        _backgroundColorPicker = GetNode<ColorPickerButton>("%BackgroundColorPicker");
        _backgroundColorResetButton = GetNode<Button>("%BackgroundColorResetButton");
        _fadeInInput = GetNode<LineEdit>("%FadeInInput");
        _fadeInResetButton = GetNode<Button>("%FadeInResetButton");
        _fadeOutInput = GetNode<LineEdit>("%FadeOutInput");
        _fadeOutResetButton = GetNode<Button>("%FadeOutResetButton");

        ComponentDefaultsUi.SetupResetButton(this, _targetLayerResetButton, OnTargetLayerResetPressed);
        ComponentDefaultsUi.SetupResetButton(this, _durationResetButton, OnDurationResetPressed);
        ComponentDefaultsUi.SetupResetButton(this, _opacityResetButton, OnOpacityResetPressed);
        ComponentDefaultsUi.SetupResetButton(this, _bbcodeResetButton, OnBbcodeResetPressed);
        ComponentDefaultsUi.SetupResetButton(this, _fontNameResetButton, OnFontNameResetPressed);
        ComponentDefaultsUi.SetupResetButton(this, _fontSizeResetButton, OnFontSizeResetPressed);
        ComponentDefaultsUi.SetupResetButton(this, _fontColorResetButton, OnFontColorResetPressed);
        ComponentDefaultsUi.SetupResetButton(this, _hAlignResetButton, OnHAlignResetPressed);
        ComponentDefaultsUi.SetupResetButton(this, _vAlignResetButton, OnVAlignResetPressed);
        ComponentDefaultsUi.SetupResetButton(this, _autowrapResetButton, OnAutowrapResetPressed);
        ComponentDefaultsUi.SetupResetButton(this, _marginsResetButton, OnMarginsResetPressed);
        ComponentDefaultsUi.SetupResetButton(this, _outlineSizeResetButton, OnOutlineSizeResetPressed);
        ComponentDefaultsUi.SetupResetButton(this, _outlineColorResetButton, OnOutlineColorResetPressed);
        ComponentDefaultsUi.SetupResetButton(this, _backgroundResetButton, OnBackgroundResetPressed);
        ComponentDefaultsUi.SetupResetButton(this, _backgroundColorResetButton, OnBackgroundColorResetPressed);
        ComponentDefaultsUi.SetupResetButton(this, _fadeInResetButton, OnFadeInResetPressed);
        ComponentDefaultsUi.SetupResetButton(this, _fadeOutResetButton, OnFadeOutResetPressed);

        EnsureAlignOptions();

        if (_targetLayerOption != null)
            _targetLayerOption.ItemSelected += OnTargetLayerSelected;
        _durationInput.TextSubmitted += OnDurationSubmitted;
        _durationInput.FocusExited += OnDurationFocusExited;
        _opacitySpin.ValueChanged += OnOpacityChanged;
        _bbcodeCheckBox.Toggled += OnBbcodeToggled;
        _fontNameInput.TextSubmitted += OnFontNameSubmitted;
        _fontNameInput.FocusExited += OnFontNameFocusExited;
        _fontSizeSpin.ValueChanged += OnFontSizeChanged;
        _fontColorPicker.PopupClosed += OnFontColorPopupClosed;
        _hAlignOption.ItemSelected += OnHAlignSelected;
        _vAlignOption.ItemSelected += OnVAlignSelected;
        _autowrapCheckBox.Toggled += OnAutowrapToggled;
        _marginsSpin.ValueChanged += OnMarginsChanged;
        _outlineSizeSpin.ValueChanged += OnOutlineSizeChanged;
        _outlineColorPicker.PopupClosed += OnOutlineColorPopupClosed;
        _backgroundCheckBox.Toggled += OnBackgroundToggled;
        _backgroundColorPicker.PopupClosed += OnBackgroundColorPopupClosed;
        _fadeInInput.TextSubmitted += t => CommitFade(t, isIn: true);
        _fadeInInput.FocusExited += () =>
        {
            if (!_isSyncingUi && _fadeInInput != null)
                CommitFade(_fadeInInput.Text, isIn: true);
        };
        _fadeOutInput.TextSubmitted += t => CommitFade(t, isIn: false);
        _fadeOutInput.FocusExited += () =>
        {
            if (!_isSyncingUi && _fadeOutInput != null)
                CommitFade(_fadeOutInput.Text, isIn: false);
        };

        if (_historyManager != null)
            _historyManager.HistoryRestored += OnHistoryRestored;
        if (_globalSignals != null)
        {
            _globalSignals.NewSession += OnNewSession;
            _globalSignals.DisplaysChanged += OnDisplaysChanged;
        }

        SyncSettings();
    
        UiLocalizer.LocalizeTree(this);
        if (_globalSignals != null)
            _globalSignals.LocaleChanged += OnLocaleChanged;
}

    /// <inheritdoc />
    public override void _ExitTree()
    {
        if (_globalSignals != null)
            _globalSignals.LocaleChanged -= OnLocaleChanged;

        if (_historyManager != null)
            _historyManager.HistoryRestored -= OnHistoryRestored;
        if (_globalSignals != null)
        {
            _globalSignals.NewSession -= OnNewSession;
            _globalSignals.DisplaysChanged -= OnDisplaysChanged;
        }
        base._ExitTree();
    }

    private void OnDisplaysChanged()
    {
        if (!GodotObject.IsInstanceValid(this) || _globalData?.Settings == null)
            return;
        SyncSettings();
    }


    private void OnHistoryRestored(int scope)
    {
        if (!GodotObject.IsInstanceValid(this) || _globalData?.Settings == null)
            return;
        if (scope != (int)HistoryManager.HistoryScope.Settings)
            return;
        SyncSettings();
    }

    private void OnNewSession()
    {
        if (!GodotObject.IsInstanceValid(this) || _globalData?.Settings == null)
            return;
        SyncSettings();
    }

    private void EnsureAlignOptions()
    {
        if (_hAlignOption != null && _hAlignOption.ItemCount == 0)
        {
            ComponentDefaultsUi.AddOptionItem(_hAlignOption, "Left", (int)HorizontalAlignment.Left);
            ComponentDefaultsUi.AddOptionItem(_hAlignOption, "Center", (int)HorizontalAlignment.Center);
            ComponentDefaultsUi.AddOptionItem(_hAlignOption, "Right", (int)HorizontalAlignment.Right);
            ComponentDefaultsUi.AddOptionItem(_hAlignOption, "Fill", (int)HorizontalAlignment.Fill);
        }

        if (_vAlignOption != null && _vAlignOption.ItemCount == 0)
        {
            ComponentDefaultsUi.AddOptionItem(_vAlignOption, "Top", (int)VerticalAlignment.Top);
            ComponentDefaultsUi.AddOptionItem(_vAlignOption, "Center", (int)VerticalAlignment.Center);
            ComponentDefaultsUi.AddOptionItem(_vAlignOption, "Bottom", (int)VerticalAlignment.Bottom);
            ComponentDefaultsUi.AddOptionItem(_vAlignOption, "Fill", (int)VerticalAlignment.Fill);
        }
    }


    private void SyncSettings()
    {
        if (_globalData?.Settings == null) return;

        _isSyncingUi = true;
        try
        {
            var s = _globalData.Settings;
            ComponentDefaultsUi.PopulateTargetLayerOption(
                _targetLayerOption, s.TextDefaultTargetLayerMode, s.TextDefaultTargetLayerId);
            EnsureAlignOptions();

            if (_durationInput != null)
            {
                if (s.TextDefaultDuration <= 0)
                    _durationInput.Text = UiLocalizer.T("Until stopped");
                else
                    _durationInput.Text = UiUtilities.FormatTime(s.TextDefaultDuration);
            }

            if (_opacitySpin != null)
                _opacitySpin.SetValueNoSignal(Mathf.Clamp(s.TextDefaultOpacity, 0f, 1f) * 100.0);

            _bbcodeCheckBox?.SetPressedNoSignal(s.TextDefaultUseBbcode);

            if (_fontNameInput != null)
                _fontNameInput.Text = s.TextDefaultFontName ?? string.Empty;

            if (_fontSizeSpin != null)
                _fontSizeSpin.SetValueNoSignal(Math.Max(1, s.TextDefaultFontSize));

            if (_fontColorPicker != null)
                _fontColorPicker.Color = s.TextDefaultFontColor;

            ComponentDefaultsUi.SelectOptionByMetadata(_hAlignOption, (int)s.TextDefaultHAlign);
            ComponentDefaultsUi.SelectOptionByMetadata(_vAlignOption, (int)s.TextDefaultVAlign);

            _autowrapCheckBox?.SetPressedNoSignal(s.TextDefaultAutowrap);

            if (_marginsSpin != null)
                _marginsSpin.SetValueNoSignal(Math.Max(0, s.TextDefaultMargins));

            if (_outlineSizeSpin != null)
                _outlineSizeSpin.SetValueNoSignal(Math.Max(0, s.TextDefaultOutlineSize));

            if (_outlineColorPicker != null)
                _outlineColorPicker.Color = s.TextDefaultOutlineColor;

            _backgroundCheckBox?.SetPressedNoSignal(s.TextDefaultBackgroundEnabled);

            if (_backgroundColorPicker != null)
                _backgroundColorPicker.Color = s.TextDefaultBackgroundColor;

            if (_fadeInInput != null)
                _fadeInInput.Text = UiUtilities.FormatTime(s.TextDefaultFadeIn);
            if (_fadeOutInput != null)
                _fadeOutInput.Text = UiUtilities.FormatTime(s.TextDefaultFadeOut);

            UpdateAllResetButtons();
        }
        finally
        {
            _isSyncingUi = false;
        }
    }

    private void UpdateAllResetButtons()
    {
        UpdateTargetLayerResetButton();
        UpdateDurationResetButton();
        UpdateOpacityResetButton();
        UpdateBbcodeResetButton();
        UpdateFontNameResetButton();
        UpdateFontSizeResetButton();
        UpdateFontColorResetButton();
        UpdateHAlignResetButton();
        UpdateVAlignResetButton();
        UpdateAutowrapResetButton();
        UpdateMarginsResetButton();
        UpdateOutlineSizeResetButton();
        UpdateOutlineColorResetButton();
        UpdateBackgroundResetButton();
        UpdateBackgroundColorResetButton();
        UpdateFadeInResetButton();
        UpdateFadeOutResetButton();
    }

    private void RecordHistory(string description, string coalesceKey = null)
    {
        ComponentDefaultsUi.RecordDefaultsChange(_historyManager, description, "TextDefaults", coalesceKey);
    }

    // ── Target layer ───────────────────────────────────────────────────────

    private void OnTargetLayerSelected(long index)
    {
        if (_isSyncingUi || _globalData?.Settings == null || _targetLayerOption == null) return;
        if (_historyManager?.IsRestoring == true) return;

        ComponentDefaultsUi.ReadTargetLayerSelection(
            _targetLayerOption, out var mode, out int layerId);
        var s = _globalData.Settings;
        if (s.TextDefaultTargetLayerMode == mode && s.TextDefaultTargetLayerId == layerId)
        {
            UpdateTargetLayerResetButton();
            return;
        }

        RecordHistory("Change default text target layer");
        s.TextDefaultTargetLayerMode = mode;
        s.TextDefaultTargetLayerId = layerId;
        UpdateTargetLayerResetButton();
    }

    private void OnTargetLayerResetPressed()
    {
        if (_isSyncingUi || _globalData?.Settings == null) return;
        var s = _globalData.Settings;
        if (ComponentDefaultsUi.IsTargetLayerAtSystem(
                s.TextDefaultTargetLayerMode, s.TextDefaultTargetLayerId))
        {
            SyncSettings();
            return;
        }

        RecordHistory("Reset default text target layer");
        s.TextDefaultTargetLayerMode = AppSettings.SystemDefaultTextTargetLayerMode;
        s.TextDefaultTargetLayerId = -1;
        SyncSettings();
    }

    private void UpdateTargetLayerResetButton()
    {
        if (_targetLayerResetButton == null || _globalData?.Settings == null) return;
        var s = _globalData.Settings;
        bool atDefault = ComponentDefaultsUi.IsTargetLayerAtSystem(
            s.TextDefaultTargetLayerMode, s.TextDefaultTargetLayerId);
        _targetLayerResetButton.Visible = !atDefault;
        if (!atDefault)
            _targetLayerResetButton.TooltipText = UiLocalizer.T("Reset to default: First available layer");
    }

    // ── Duration ───────────────────────────────────────────────────────────

    private void OnDurationSubmitted(string text) => CommitDuration(text);

    private void OnDurationFocusExited()
    {
        if (_isSyncingUi || _durationInput == null) return;
        CommitDuration(_durationInput.Text);
    }

    private void CommitDuration(string text)
    {
        if (_globalData?.Settings == null || ComponentDefaultsUi.ShouldSkipEdit(_isSyncingUi, _historyManager)) return;

        string trimmed = (text ?? string.Empty).Trim();
        double seconds;
        if (string.IsNullOrEmpty(trimmed)
            || trimmed.Equals("until stopped", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("0", StringComparison.OrdinalIgnoreCase))
        {
            seconds = 0;
            _durationInput.Text = UiLocalizer.T("Until stopped");
        }
        else
        {
            var formatted = UiUtilities.ParseAndFormatTime(trimmed, out seconds, out string labeled);
            if (string.IsNullOrEmpty(formatted))
            {
                _globalSignals?.EmitSignal(nameof(GlobalSignals.Log),
                    $"Invalid default text duration: {text}", 1);
                double cur = _globalData.Settings.TextDefaultDuration;
                _durationInput.Text = cur <= 0 ? UiLocalizer.T("Until stopped") : UiUtilities.FormatTime(cur);
                return;
            }

            seconds = Math.Max(0.0, seconds);
            if (seconds <= 0)
                _durationInput.Text = UiLocalizer.T("Until stopped");
            else
            {
                _durationInput.Text = formatted;
                _durationInput.TooltipText = labeled;
            }
        }

        if (Mathf.IsEqualApprox((float)_globalData.Settings.TextDefaultDuration, (float)seconds))
        {
            UpdateDurationResetButton();
            return;
        }

        RecordHistory("Change default text duration");
        _globalData.Settings.TextDefaultDuration = seconds;
        UpdateDurationResetButton();
    }

    private void OnDurationResetPressed()
    {
        if (_isSyncingUi || _globalData?.Settings == null) return;
        if (Mathf.IsEqualApprox((float)_globalData.Settings.TextDefaultDuration,
                (float)AppSettings.SystemDefaultTextDuration))
        {
            SyncSettings();
            return;
        }

        RecordHistory("Reset default text duration");
        _globalData.Settings.TextDefaultDuration = AppSettings.SystemDefaultTextDuration;
        SyncSettings();
    }

    private void UpdateDurationResetButton()
    {
        if (_durationResetButton == null || _globalData?.Settings == null) return;
        bool atDefault = Mathf.IsEqualApprox((float)_globalData.Settings.TextDefaultDuration,
            (float)AppSettings.SystemDefaultTextDuration);
        _durationResetButton.Visible = !atDefault;
        if (!atDefault)
            _durationResetButton.TooltipText = UiLocalizer.T("Reset to default: Until stopped");
    }

    // ── Opacity ────────────────────────────────────────────────────────────

    private void OnOpacityChanged(double value)
    {
        if (_globalData?.Settings == null || ComponentDefaultsUi.ShouldSkipEdit(_isSyncingUi, _historyManager)) return;

        float opacity = Mathf.Clamp((float)value / 100f, 0f, 1f);
        if (Math.Abs(_globalData.Settings.TextDefaultOpacity - opacity) < 1e-4f)
        {
            UpdateOpacityResetButton();
            return;
        }

        RecordHistory("Change default text opacity");
        _globalData.Settings.TextDefaultOpacity = opacity;
        UpdateOpacityResetButton();
    }

    private void OnOpacityResetPressed()
    {
        if (_isSyncingUi || _globalData?.Settings == null) return;
        if (Math.Abs(_globalData.Settings.TextDefaultOpacity
                      - AppSettings.SystemDefaultTextOpacity) < 1e-4f)
        {
            SyncSettings();
            return;
        }

        RecordHistory("Reset default text opacity");
        _globalData.Settings.TextDefaultOpacity = AppSettings.SystemDefaultTextOpacity;
        SyncSettings();
    }

    private void UpdateOpacityResetButton()
    {
        if (_opacityResetButton == null || _globalData?.Settings == null) return;
        bool atDefault = Math.Abs(_globalData.Settings.TextDefaultOpacity
                                  - AppSettings.SystemDefaultTextOpacity) < 1e-4f;
        _opacityResetButton.Visible = !atDefault;
        if (!atDefault)
            _opacityResetButton.TooltipText = UiLocalizer.T("Reset to default: 100%");
    }

    // ── BBCode ─────────────────────────────────────────────────────────────

    private void OnBbcodeToggled(bool pressed)
    {
        if (_globalData?.Settings == null || ComponentDefaultsUi.ShouldSkipEdit(_isSyncingUi, _historyManager)) return;
        if (_globalData.Settings.TextDefaultUseBbcode == pressed)
        {
            UpdateBbcodeResetButton();
            return;
        }

        RecordHistory(pressed ? "Enable default text BBCode" : "Disable default text BBCode");
        _globalData.Settings.TextDefaultUseBbcode = pressed;
        UpdateBbcodeResetButton();
    }

    private void OnBbcodeResetPressed()
    {
        if (_isSyncingUi || _globalData?.Settings == null) return;
        if (_globalData.Settings.TextDefaultUseBbcode == AppSettings.SystemDefaultTextUseBbcode)
        {
            SyncSettings();
            return;
        }

        RecordHistory("Reset default text BBCode");
        _globalData.Settings.TextDefaultUseBbcode = AppSettings.SystemDefaultTextUseBbcode;
        SyncSettings();
    }

    private void UpdateBbcodeResetButton()
    {
        if (_bbcodeResetButton == null || _globalData?.Settings == null) return;
        bool atDefault = _globalData.Settings.TextDefaultUseBbcode
                         == AppSettings.SystemDefaultTextUseBbcode;
        _bbcodeResetButton.Visible = !atDefault;
        if (!atDefault)
            _bbcodeResetButton.TooltipText =
                UiLocalizer.ResetDefaultTip((AppSettings.SystemDefaultTextUseBbcode ? "On" : "Off"));
    }

    // ── Font name / size / colour ──────────────────────────────────────────

    private void OnFontNameSubmitted(string text) => CommitFontName(text);

    private void OnFontNameFocusExited()
    {
        if (_isSyncingUi || _fontNameInput == null) return;
        CommitFontName(_fontNameInput.Text);
    }

    private void CommitFontName(string text)
    {
        if (_globalData?.Settings == null || ComponentDefaultsUi.ShouldSkipEdit(_isSyncingUi, _historyManager)) return;

        string name = (text ?? string.Empty).Trim();
        _fontNameInput.Text = name;
        if (string.Equals(_globalData.Settings.TextDefaultFontName ?? string.Empty, name,
                StringComparison.Ordinal))
        {
            UpdateFontNameResetButton();
            return;
        }

        RecordHistory("Change default text font name");
        _globalData.Settings.TextDefaultFontName = name;
        UpdateFontNameResetButton();
    }

    private void OnFontNameResetPressed()
    {
        if (_isSyncingUi || _globalData?.Settings == null) return;
        if (string.Equals(_globalData.Settings.TextDefaultFontName ?? string.Empty,
                AppSettings.SystemDefaultTextFontName, StringComparison.Ordinal))
        {
            SyncSettings();
            return;
        }

        RecordHistory("Reset default text font name");
        _globalData.Settings.TextDefaultFontName = AppSettings.SystemDefaultTextFontName;
        SyncSettings();
    }

    private void UpdateFontNameResetButton()
    {
        if (_fontNameResetButton == null || _globalData?.Settings == null) return;
        bool atDefault = string.Equals(_globalData.Settings.TextDefaultFontName ?? string.Empty,
            AppSettings.SystemDefaultTextFontName, StringComparison.Ordinal);
        _fontNameResetButton.Visible = !atDefault;
        if (!atDefault)
            _fontNameResetButton.TooltipText = UiLocalizer.T("Reset to default: theme font");
    }

    private void OnFontSizeChanged(double value)
    {
        if (_globalData?.Settings == null || ComponentDefaultsUi.ShouldSkipEdit(_isSyncingUi, _historyManager)) return;

        int size = Math.Max(1, (int)Math.Round(value));
        if (_globalData.Settings.TextDefaultFontSize == size)
        {
            UpdateFontSizeResetButton();
            return;
        }

        RecordHistory("Change default text font size");
        _globalData.Settings.TextDefaultFontSize = size;
        UpdateFontSizeResetButton();
    }

    private void OnFontSizeResetPressed()
    {
        if (_isSyncingUi || _globalData?.Settings == null) return;
        if (_globalData.Settings.TextDefaultFontSize == AppSettings.SystemDefaultTextFontSize)
        {
            SyncSettings();
            return;
        }

        RecordHistory("Reset default text font size");
        _globalData.Settings.TextDefaultFontSize = AppSettings.SystemDefaultTextFontSize;
        SyncSettings();
    }

    private void UpdateFontSizeResetButton()
    {
        if (_fontSizeResetButton == null || _globalData?.Settings == null) return;
        bool atDefault = _globalData.Settings.TextDefaultFontSize
                         == AppSettings.SystemDefaultTextFontSize;
        _fontSizeResetButton.Visible = !atDefault;
        if (!atDefault)
            _fontSizeResetButton.TooltipText =
                UiLocalizer.ResetDefaultTip(AppSettings.SystemDefaultTextFontSize);
    }

    private void OnFontColorPopupClosed()
    {
        if (_isSyncingUi || _globalData?.Settings == null || _fontColorPicker == null) return;
        if (_historyManager?.IsRestoring == true) return;

        if (_globalData.Settings.TextDefaultFontColor.IsEqualApprox(_fontColorPicker.Color))
        {
            UpdateFontColorResetButton();
            return;
        }

        RecordHistory("Change default text font colour");
        _globalData.Settings.TextDefaultFontColor = _fontColorPicker.Color;
        UpdateFontColorResetButton();
    }

    private void OnFontColorResetPressed()
    {
        if (_isSyncingUi || _globalData?.Settings == null) return;
        if (_globalData.Settings.TextDefaultFontColor.IsEqualApprox(AppSettings.SystemDefaultTextFontColor))
        {
            SyncSettings();
            return;
        }

        RecordHistory("Reset default text font colour");
        _globalData.Settings.TextDefaultFontColor = AppSettings.SystemDefaultTextFontColor;
        SyncSettings();
    }

    private void UpdateFontColorResetButton()
    {
        if (_fontColorResetButton == null || _globalData?.Settings == null) return;
        bool atDefault = _globalData.Settings.TextDefaultFontColor
            .IsEqualApprox(AppSettings.SystemDefaultTextFontColor);
        _fontColorResetButton.Visible = !atDefault;
        if (!atDefault)
            _fontColorResetButton.TooltipText = UiLocalizer.T("Reset to default: white");
    }

    // ── Alignment ──────────────────────────────────────────────────────────

    private void OnHAlignSelected(long index)
    {
        if (_globalData?.Settings == null || ComponentDefaultsUi.ShouldSkipEdit(_isSyncingUi, _historyManager)) return;

        var align = (HorizontalAlignment)_hAlignOption.GetItemMetadata((int)index).AsInt32();
        if (_globalData.Settings.TextDefaultHAlign == align)
        {
            UpdateHAlignResetButton();
            return;
        }

        RecordHistory("Change default text horizontal alignment");
        _globalData.Settings.TextDefaultHAlign = align;
        UpdateHAlignResetButton();
    }

    private void OnHAlignResetPressed()
    {
        if (_isSyncingUi || _globalData?.Settings == null) return;
        if (_globalData.Settings.TextDefaultHAlign == AppSettings.SystemDefaultTextHAlign)
        {
            SyncSettings();
            return;
        }

        RecordHistory("Reset default text horizontal alignment");
        _globalData.Settings.TextDefaultHAlign = AppSettings.SystemDefaultTextHAlign;
        SyncSettings();
    }

    private void UpdateHAlignResetButton()
    {
        if (_hAlignResetButton == null || _globalData?.Settings == null) return;
        bool atDefault = _globalData.Settings.TextDefaultHAlign == AppSettings.SystemDefaultTextHAlign;
        _hAlignResetButton.Visible = !atDefault;
        if (!atDefault)
            _hAlignResetButton.TooltipText = UiLocalizer.T("Reset to default: Center");
    }

    private void OnVAlignSelected(long index)
    {
        if (_globalData?.Settings == null || ComponentDefaultsUi.ShouldSkipEdit(_isSyncingUi, _historyManager)) return;

        var align = (VerticalAlignment)_vAlignOption.GetItemMetadata((int)index).AsInt32();
        if (_globalData.Settings.TextDefaultVAlign == align)
        {
            UpdateVAlignResetButton();
            return;
        }

        RecordHistory("Change default text vertical alignment");
        _globalData.Settings.TextDefaultVAlign = align;
        UpdateVAlignResetButton();
    }

    private void OnVAlignResetPressed()
    {
        if (_isSyncingUi || _globalData?.Settings == null) return;
        if (_globalData.Settings.TextDefaultVAlign == AppSettings.SystemDefaultTextVAlign)
        {
            SyncSettings();
            return;
        }

        RecordHistory("Reset default text vertical alignment");
        _globalData.Settings.TextDefaultVAlign = AppSettings.SystemDefaultTextVAlign;
        SyncSettings();
    }

    private void UpdateVAlignResetButton()
    {
        if (_vAlignResetButton == null || _globalData?.Settings == null) return;
        bool atDefault = _globalData.Settings.TextDefaultVAlign == AppSettings.SystemDefaultTextVAlign;
        _vAlignResetButton.Visible = !atDefault;
        if (!atDefault)
            _vAlignResetButton.TooltipText = UiLocalizer.T("Reset to default: Center");
    }

    // ── Autowrap / margins ─────────────────────────────────────────────────

    private void OnAutowrapToggled(bool pressed)
    {
        if (_globalData?.Settings == null || ComponentDefaultsUi.ShouldSkipEdit(_isSyncingUi, _historyManager)) return;
        if (_globalData.Settings.TextDefaultAutowrap == pressed)
        {
            UpdateAutowrapResetButton();
            return;
        }

        RecordHistory(pressed ? "Enable default text autowrap" : "Disable default text autowrap");
        _globalData.Settings.TextDefaultAutowrap = pressed;
        UpdateAutowrapResetButton();
    }

    private void OnAutowrapResetPressed()
    {
        if (_isSyncingUi || _globalData?.Settings == null) return;
        if (_globalData.Settings.TextDefaultAutowrap == AppSettings.SystemDefaultTextAutowrap)
        {
            SyncSettings();
            return;
        }

        RecordHistory("Reset default text autowrap");
        _globalData.Settings.TextDefaultAutowrap = AppSettings.SystemDefaultTextAutowrap;
        SyncSettings();
    }

    private void UpdateAutowrapResetButton()
    {
        if (_autowrapResetButton == null || _globalData?.Settings == null) return;
        bool atDefault = _globalData.Settings.TextDefaultAutowrap
                         == AppSettings.SystemDefaultTextAutowrap;
        _autowrapResetButton.Visible = !atDefault;
        if (!atDefault)
            _autowrapResetButton.TooltipText =
                UiLocalizer.ResetDefaultTip((AppSettings.SystemDefaultTextAutowrap ? "On" : "Off"));
    }

    private void OnMarginsChanged(double value)
    {
        if (_globalData?.Settings == null || ComponentDefaultsUi.ShouldSkipEdit(_isSyncingUi, _historyManager)) return;

        int margins = Math.Max(0, (int)Math.Round(value));
        if (_globalData.Settings.TextDefaultMargins == margins)
        {
            UpdateMarginsResetButton();
            return;
        }

        RecordHistory("Change default text margins");
        _globalData.Settings.TextDefaultMargins = margins;
        UpdateMarginsResetButton();
    }

    private void OnMarginsResetPressed()
    {
        if (_isSyncingUi || _globalData?.Settings == null) return;
        if (_globalData.Settings.TextDefaultMargins == AppSettings.SystemDefaultTextMargins)
        {
            SyncSettings();
            return;
        }

        RecordHistory("Reset default text margins");
        _globalData.Settings.TextDefaultMargins = AppSettings.SystemDefaultTextMargins;
        SyncSettings();
    }

    private void UpdateMarginsResetButton()
    {
        if (_marginsResetButton == null || _globalData?.Settings == null) return;
        bool atDefault = _globalData.Settings.TextDefaultMargins
                         == AppSettings.SystemDefaultTextMargins;
        _marginsResetButton.Visible = !atDefault;
        if (!atDefault)
            _marginsResetButton.TooltipText =
                UiLocalizer.ResetDefaultTip(AppSettings.SystemDefaultTextMargins);
    }

    // ── Outline ────────────────────────────────────────────────────────────

    private void OnOutlineSizeChanged(double value)
    {
        if (_globalData?.Settings == null || ComponentDefaultsUi.ShouldSkipEdit(_isSyncingUi, _historyManager)) return;

        int size = Math.Max(0, (int)Math.Round(value));
        if (_globalData.Settings.TextDefaultOutlineSize == size)
        {
            UpdateOutlineSizeResetButton();
            return;
        }

        RecordHistory("Change default text outline size");
        _globalData.Settings.TextDefaultOutlineSize = size;
        UpdateOutlineSizeResetButton();
    }

    private void OnOutlineSizeResetPressed()
    {
        if (_isSyncingUi || _globalData?.Settings == null) return;
        if (_globalData.Settings.TextDefaultOutlineSize == AppSettings.SystemDefaultTextOutlineSize)
        {
            SyncSettings();
            return;
        }

        RecordHistory("Reset default text outline size");
        _globalData.Settings.TextDefaultOutlineSize = AppSettings.SystemDefaultTextOutlineSize;
        SyncSettings();
    }

    private void UpdateOutlineSizeResetButton()
    {
        if (_outlineSizeResetButton == null || _globalData?.Settings == null) return;
        bool atDefault = _globalData.Settings.TextDefaultOutlineSize
                         == AppSettings.SystemDefaultTextOutlineSize;
        _outlineSizeResetButton.Visible = !atDefault;
        if (!atDefault)
            _outlineSizeResetButton.TooltipText =
                UiLocalizer.ResetDefaultTip(AppSettings.SystemDefaultTextOutlineSize);
    }

    private void OnOutlineColorPopupClosed()
    {
        if (_isSyncingUi || _globalData?.Settings == null || _outlineColorPicker == null) return;
        if (_historyManager?.IsRestoring == true) return;

        if (_globalData.Settings.TextDefaultOutlineColor.IsEqualApprox(_outlineColorPicker.Color))
        {
            UpdateOutlineColorResetButton();
            return;
        }

        RecordHistory("Change default text outline colour");
        _globalData.Settings.TextDefaultOutlineColor = _outlineColorPicker.Color;
        UpdateOutlineColorResetButton();
    }

    private void OnOutlineColorResetPressed()
    {
        if (_isSyncingUi || _globalData?.Settings == null) return;
        if (_globalData.Settings.TextDefaultOutlineColor
            .IsEqualApprox(AppSettings.SystemDefaultTextOutlineColor))
        {
            SyncSettings();
            return;
        }

        RecordHistory("Reset default text outline colour");
        _globalData.Settings.TextDefaultOutlineColor = AppSettings.SystemDefaultTextOutlineColor;
        SyncSettings();
    }

    private void UpdateOutlineColorResetButton()
    {
        if (_outlineColorResetButton == null || _globalData?.Settings == null) return;
        bool atDefault = _globalData.Settings.TextDefaultOutlineColor
            .IsEqualApprox(AppSettings.SystemDefaultTextOutlineColor);
        _outlineColorResetButton.Visible = !atDefault;
        if (!atDefault)
            _outlineColorResetButton.TooltipText = UiLocalizer.T("Reset to default: black");
    }

    // ── Background ─────────────────────────────────────────────────────────

    private void OnBackgroundToggled(bool pressed)
    {
        if (_globalData?.Settings == null || ComponentDefaultsUi.ShouldSkipEdit(_isSyncingUi, _historyManager)) return;
        if (_globalData.Settings.TextDefaultBackgroundEnabled == pressed)
        {
            UpdateBackgroundResetButton();
            return;
        }

        RecordHistory(pressed ? "Enable default text background" : "Disable default text background");
        _globalData.Settings.TextDefaultBackgroundEnabled = pressed;
        UpdateBackgroundResetButton();
    }

    private void OnBackgroundResetPressed()
    {
        if (_isSyncingUi || _globalData?.Settings == null) return;
        if (_globalData.Settings.TextDefaultBackgroundEnabled
            == AppSettings.SystemDefaultTextBackgroundEnabled)
        {
            SyncSettings();
            return;
        }

        RecordHistory("Reset default text background");
        _globalData.Settings.TextDefaultBackgroundEnabled =
            AppSettings.SystemDefaultTextBackgroundEnabled;
        SyncSettings();
    }

    private void UpdateBackgroundResetButton()
    {
        if (_backgroundResetButton == null || _globalData?.Settings == null) return;
        bool atDefault = _globalData.Settings.TextDefaultBackgroundEnabled
                         == AppSettings.SystemDefaultTextBackgroundEnabled;
        _backgroundResetButton.Visible = !atDefault;
        if (!atDefault)
            _backgroundResetButton.TooltipText =
                UiLocalizer.ResetDefaultTip((AppSettings.SystemDefaultTextBackgroundEnabled ? "On" : "Off"));
    }

    private void OnBackgroundColorPopupClosed()
    {
        if (_isSyncingUi || _globalData?.Settings == null || _backgroundColorPicker == null) return;
        if (_historyManager?.IsRestoring == true) return;

        if (_globalData.Settings.TextDefaultBackgroundColor.IsEqualApprox(_backgroundColorPicker.Color))
        {
            UpdateBackgroundColorResetButton();
            return;
        }

        RecordHistory("Change default text background colour");
        _globalData.Settings.TextDefaultBackgroundColor = _backgroundColorPicker.Color;
        UpdateBackgroundColorResetButton();
    }

    private void OnBackgroundColorResetPressed()
    {
        if (_isSyncingUi || _globalData?.Settings == null) return;
        if (_globalData.Settings.TextDefaultBackgroundColor
            .IsEqualApprox(AppSettings.SystemDefaultTextBackgroundColor))
        {
            SyncSettings();
            return;
        }

        RecordHistory("Reset default text background colour");
        _globalData.Settings.TextDefaultBackgroundColor =
            AppSettings.SystemDefaultTextBackgroundColor;
        SyncSettings();
    }

    private void UpdateBackgroundColorResetButton()
    {
        if (_backgroundColorResetButton == null || _globalData?.Settings == null) return;
        bool atDefault = _globalData.Settings.TextDefaultBackgroundColor
            .IsEqualApprox(AppSettings.SystemDefaultTextBackgroundColor);
        _backgroundColorResetButton.Visible = !atDefault;
        if (!atDefault)
            _backgroundColorResetButton.TooltipText = UiLocalizer.T("Reset to default background colour");
    }

    // ── Fades ──────────────────────────────────────────────────────────────

    private void CommitFade(string text, bool isIn)
    {
        if (_globalData?.Settings == null || ComponentDefaultsUi.ShouldSkipEdit(_isSyncingUi, _historyManager))
            return;

        var field = isIn ? _fadeInInput : _fadeOutInput;
        double existing = isIn
            ? _globalData.Settings.TextDefaultFadeIn
            : _globalData.Settings.TextDefaultFadeOut;
        if (!ComponentDefaultsUi.TryParseTimeDefault(
                field, text, existing, _globalSignals, $"Invalid default fade time: {text}", out double seconds))
        {
            if (isIn) UpdateFadeInResetButton();
            else UpdateFadeOutResetButton();
            return;
        }

        RecordHistory(isIn ? "Change default text fade-in" : "Change default text fade-out");
        if (isIn)
            _globalData.Settings.TextDefaultFadeIn = seconds;
        else
            _globalData.Settings.TextDefaultFadeOut = seconds;

        if (isIn) UpdateFadeInResetButton();
        else UpdateFadeOutResetButton();
    }

    private void OnFadeInResetPressed()
    {
        if (_globalData?.Settings == null) return;
        ComponentDefaultsUi.TryResetField(
            _isSyncingUi, _historyManager, "TextDefaults", "Reset default text fade-in",
            ComponentDefaultsUi.NearlyEqual(_globalData.Settings.TextDefaultFadeIn, AppSettings.SystemDefaultTextFadeIn),
            () => _globalData.Settings.TextDefaultFadeIn = AppSettings.SystemDefaultTextFadeIn,
            SyncSettings);
    }

    private void OnFadeOutResetPressed()
    {
        if (_globalData?.Settings == null) return;
        ComponentDefaultsUi.TryResetField(
            _isSyncingUi, _historyManager, "TextDefaults", "Reset default text fade-out",
            ComponentDefaultsUi.NearlyEqual(_globalData.Settings.TextDefaultFadeOut, AppSettings.SystemDefaultTextFadeOut),
            () => _globalData.Settings.TextDefaultFadeOut = AppSettings.SystemDefaultTextFadeOut,
            SyncSettings);
    }

    private void UpdateFadeInResetButton()
    {
        if (_globalData?.Settings == null) return;
        bool atDefault = ComponentDefaultsUi.NearlyEqual(
            _globalData.Settings.TextDefaultFadeIn, AppSettings.SystemDefaultTextFadeIn);
        ComponentDefaultsUi.UpdateResetButton(
            _fadeInResetButton, atDefault,
            UiLocalizer.ResetDefaultTip(UiUtilities.FormatTime(AppSettings.SystemDefaultTextFadeIn)));
    }

    private void UpdateFadeOutResetButton()
    {
        if (_globalData?.Settings == null) return;
        bool atDefault = ComponentDefaultsUi.NearlyEqual(
            _globalData.Settings.TextDefaultFadeOut, AppSettings.SystemDefaultTextFadeOut);
        ComponentDefaultsUi.UpdateResetButton(
            _fadeOutResetButton, atDefault,
            UiLocalizer.ResetDefaultTip(UiUtilities.FormatTime(AppSettings.SystemDefaultTextFadeOut)));
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
