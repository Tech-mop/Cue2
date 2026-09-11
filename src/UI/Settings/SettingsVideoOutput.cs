// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

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
using AppSettings = Cue2.Domain.ShowSettings.Settings;
using Cue2.UI.Utilities;

namespace Cue2.UI.Settings;

/// <summary>
/// Video / Image settings panel (Settings tree parent): live disable &amp; blackout,
/// show background colour, and show-scoped performance options (quality, preview, vsync).
/// Canvas Editor remains a child page for topology.
/// </summary>
/// <remarks>
/// Disable/blackout are runtime operator controls (not undoable, not saved).
/// Background colour and performance knobs are show-scoped with history and
/// persist in the showfile via <see cref="AppSettings"/>.
/// </remarks>
public partial class SettingsVideoOutput : ScrollContainer
{
    private GlobalSignals _globalSignals;
    private GlobalData _globalData;
    private HistoryManager _historyManager;
    private DisplaysManager _displaysManager;

    // Live output (runtime)
    private CheckBox _disableOutputCheckBox;
    private CheckBox _blackoutCheckBox;

    // Show-scoped appearance
    private ColorPickerButton _backgroundColorPicker;
    private Button _backgroundColorResetButton;

    // Show-scoped performance
    private OptionButton _qualityModeOption;
    private Button _qualityModeResetButton;
    private OptionButton _previewQualityOption;
    private Button _previewQualityResetButton;
    private OptionButton _vsyncOption;
    private Button _vsyncResetButton;

    /// <summary>True while pushing model → controls so handlers do not re-record history.</summary>
    private bool _isSyncingUi;

    public override void _Ready()
    {
        _globalSignals = GetNode<GlobalSignals>("/root/GlobalSignals");
        _globalData = GetNode<GlobalData>("/root/GlobalData");
        _historyManager = _globalData?.HistoryManager;
        _displaysManager = GetNodeOrNull<DisplaysManager>("/root/DisplaysManager");

        _disableOutputCheckBox = GetNode<CheckBox>("%DisableOutputCheckBox");
        _blackoutCheckBox = GetNode<CheckBox>("%BlackoutCheckBox");
        _backgroundColorPicker = GetNode<ColorPickerButton>("%BackgroundColorPicker");
        _backgroundColorResetButton = GetNode<Button>("%BackgroundColorResetButton");
        _qualityModeOption = GetNode<OptionButton>("%QualityModeOption");
        _qualityModeResetButton = GetNode<Button>("%QualityModeResetButton");
        _previewQualityOption = GetNode<OptionButton>("%PreviewQualityOption");
        _previewQualityResetButton = GetNode<Button>("%PreviewQualityResetButton");
        _vsyncOption = GetNode<OptionButton>("%VSyncOption");
        _vsyncResetButton = GetNode<Button>("%VSyncResetButton");

        SetupResetButton(_backgroundColorResetButton, OnBackgroundColorResetPressed);
        SetupResetButton(_qualityModeResetButton, OnQualityModeResetPressed);
        SetupResetButton(_previewQualityResetButton, OnPreviewQualityResetPressed);
        SetupResetButton(_vsyncResetButton, OnVSyncResetPressed);

        _disableOutputCheckBox.Toggled += OnDisableOutputToggled;
        _blackoutCheckBox.Toggled += OnBlackoutToggled;
        _backgroundColorPicker.PopupClosed += OnBackgroundColorPopupClosed;
        _qualityModeOption.ItemSelected += OnQualityModeSelected;
        _previewQualityOption.ItemSelected += OnPreviewQualitySelected;
        _vsyncOption.ItemSelected += OnVSyncSelected;

        if (_historyManager != null)
            _historyManager.HistoryRestored += OnHistoryRestored;
        if (_globalSignals != null)
        {
            _globalSignals.NewSession += OnNewSession;
            _globalSignals.VideoOutputControlChanged += OnVideoOutputControlChanged;
        }

        SyncSettings();
    
        UiLocalizer.LocalizeTree(this);
        if (_globalSignals != null)
            _globalSignals.LocaleChanged += OnLocaleChanged;
}

    public override void _ExitTree()
    {
        if (_globalSignals != null)
            _globalSignals.LocaleChanged -= OnLocaleChanged;

        if (_historyManager != null)
            _historyManager.HistoryRestored -= OnHistoryRestored;
        if (_globalSignals != null)
        {
            _globalSignals.NewSession -= OnNewSession;
            _globalSignals.VideoOutputControlChanged -= OnVideoOutputControlChanged;
        }
        base._ExitTree();
    }

    private static void SetupResetButton(Button button, System.Action handler)
    {
        if (button == null)
            return;
        button.Icon = button.GetThemeIcon("Refresh", "AtlasIcons");
        button.Pressed += handler;
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
        if (!GodotObject.IsInstanceValid(this))
            return;
        SyncSettings();
    }

    private void OnVideoOutputControlChanged(bool disabled, bool blackout)
    {
        if (!GodotObject.IsInstanceValid(this) || _isSyncingUi)
            return;
        _isSyncingUi = true;
        try
        {
            _disableOutputCheckBox?.SetPressedNoSignal(disabled);
            _blackoutCheckBox?.SetPressedNoSignal(blackout);
            // Blackout is meaningless while displays are closed.
            if (_blackoutCheckBox != null)
                _blackoutCheckBox.Disabled = disabled;
        }
        finally
        {
            _isSyncingUi = false;
        }
    }

    /// <summary>
    /// Pulls current model values into the form without re-firing edit handlers.
    /// </summary>
    private void SyncSettings()
    {
        if (_globalData?.Settings == null)
            return;

        _isSyncingUi = true;
        try
        {
            bool disabled = DisplaysManager.OutputDisabled;
            bool blackout = DisplaysManager.OutputBlackout;
            _disableOutputCheckBox?.SetPressedNoSignal(disabled);
            _blackoutCheckBox?.SetPressedNoSignal(blackout);
            if (_blackoutCheckBox != null)
                _blackoutCheckBox.Disabled = disabled;

            if (_backgroundColorPicker != null)
                _backgroundColorPicker.Color = _globalData.Settings.OutputBackgroundColor;

            SyncShowPerformanceOptions(blockSignals: true);
            UpdateAllResetButtons();
        }
        finally
        {
            _isSyncingUi = false;
        }
    }

    private void SyncShowPerformanceOptions(bool blockSignals = false)
    {
        var settings = _globalData?.Settings;
        if (settings == null)
            return;

        if (_qualityModeOption != null)
        {
            if (blockSignals) _qualityModeOption.SetBlockSignals(true);
            _qualityModeOption.Selected = (int)settings.VideoQualityMode;
            if (blockSignals) _qualityModeOption.SetBlockSignals(false);
        }
        if (_previewQualityOption != null)
        {
            if (blockSignals) _previewQualityOption.SetBlockSignals(true);
            _previewQualityOption.Selected = (int)settings.VideoPreviewQuality;
            if (blockSignals) _previewQualityOption.SetBlockSignals(false);
        }
        if (_vsyncOption != null)
        {
            if (blockSignals) _vsyncOption.SetBlockSignals(true);
            _vsyncOption.Selected = (int)settings.OutputVSyncMode;
            if (blockSignals) _vsyncOption.SetBlockSignals(false);
        }

        UpdatePerformanceResetButtons();
    }

    private void UpdateAllResetButtons()
    {
        UpdateBackgroundColorResetButton();
        UpdatePerformanceResetButtons();
    }

    private void UpdatePerformanceResetButtons()
    {
        UpdateQualityModeResetButton();
        UpdatePreviewQualityResetButton();
        UpdateVSyncResetButton();
    }

    // ── Live output controls ────────────────────────────────────────────────

    private void OnDisableOutputToggled(bool pressed)
    {
        if (_isSyncingUi || _displaysManager == null)
            return;
        _displaysManager.SetOutputDisabled(pressed);
        if (_blackoutCheckBox != null)
            _blackoutCheckBox.Disabled = pressed;
    }

    private void OnBlackoutToggled(bool pressed)
    {
        if (_isSyncingUi || _displaysManager == null)
            return;
        if (DisplaysManager.OutputDisabled)
            return;
        _displaysManager.SetOutputBlackout(pressed);
    }

    // ── Show background colour ──────────────────────────────────────────────

    private void OnBackgroundColorPopupClosed()
    {
        if (_isSyncingUi || _globalData?.Settings == null || _historyManager?.IsRestoring == true)
            return;
        if (_backgroundColorPicker == null)
            return;

        Color next = _backgroundColorPicker.Color;
        if (_globalData.Settings.OutputBackgroundColor.IsEqualApprox(next))
        {
            UpdateBackgroundColorResetButton();
            return;
        }

        _historyManager?.RecordSettingsChange("Output background colour", null, "OutputBackgroundColor");
        _globalData.Settings.OutputBackgroundColor = next;
        _displaysManager?.ApplyOutputBackgroundColor(next);
        UpdateBackgroundColorResetButton();
    }

    private void OnBackgroundColorResetPressed()
    {
        if (_isSyncingUi || _globalData?.Settings == null || _historyManager?.IsRestoring == true)
            return;
        if (_globalData.Settings.OutputBackgroundColor.IsEqualApprox(AppSettings.DefaultOutputBackgroundColor))
            return;

        _historyManager?.RecordSettingsChange("Reset output background colour", null, "OutputBackgroundColor");
        _globalData.Settings.OutputBackgroundColor = AppSettings.DefaultOutputBackgroundColor;
        if (_backgroundColorPicker != null)
            _backgroundColorPicker.Color = AppSettings.DefaultOutputBackgroundColor;
        _displaysManager?.ApplyOutputBackgroundColor(AppSettings.DefaultOutputBackgroundColor);
        UpdateBackgroundColorResetButton();
    }

    private void UpdateBackgroundColorResetButton()
    {
        if (_backgroundColorResetButton == null || _globalData?.Settings == null)
            return;
        bool atDefault = _globalData.Settings.OutputBackgroundColor
            .IsEqualApprox(AppSettings.DefaultOutputBackgroundColor);
        _backgroundColorResetButton.Visible = !atDefault;
        if (!atDefault)
            _backgroundColorResetButton.TooltipText = UiLocalizer.T("Reset to default: black");
    }

    // ── Show performance options ────────────────────────────────────────────

    private void OnQualityModeSelected(long index)
    {
        if (_isSyncingUi || _globalData?.Settings == null || _historyManager?.IsRestoring == true)
            return;

        var next = (VideoQualityMode)(int)index;
        if (_globalData.Settings.VideoQualityMode == next)
        {
            UpdateQualityModeResetButton();
            return;
        }

        _historyManager?.RecordSettingsChange("Change video quality mode", null, "VideoQualityMode");
        _globalData.Settings.VideoQualityMode = next;
        UpdateQualityModeResetButton();
    }

    private void OnQualityModeResetPressed()
    {
        if (_isSyncingUi || _globalData?.Settings == null || _historyManager?.IsRestoring == true)
            return;
        if (_globalData.Settings.VideoQualityMode == AppSettings.DefaultVideoQualityMode)
            return;

        _historyManager?.RecordSettingsChange("Reset video quality mode", null, "VideoQualityMode");
        _globalData.Settings.VideoQualityMode = AppSettings.DefaultVideoQualityMode;
        SyncShowPerformanceOptions(blockSignals: true);
    }

    private void UpdateQualityModeResetButton()
    {
        if (_qualityModeResetButton == null || _globalData?.Settings == null)
            return;
        bool atDefault = _globalData.Settings.VideoQualityMode == AppSettings.DefaultVideoQualityMode;
        _qualityModeResetButton.Visible = !atDefault;
        if (!atDefault)
            _qualityModeResetButton.TooltipText = UiLocalizer.T("Reset to default: Balanced");
    }

    private void OnPreviewQualitySelected(long index)
    {
        if (_isSyncingUi || _globalData?.Settings == null || _historyManager?.IsRestoring == true)
            return;

        var next = (VideoPreviewQuality)(int)index;
        if (_globalData.Settings.VideoPreviewQuality == next)
        {
            UpdatePreviewQualityResetButton();
            return;
        }

        _historyManager?.RecordSettingsChange("Change video preview quality", null, "VideoPreviewQuality");
        _globalData.Settings.VideoPreviewQuality = next;
        UpdatePreviewQualityResetButton();
    }

    private void OnPreviewQualityResetPressed()
    {
        if (_isSyncingUi || _globalData?.Settings == null || _historyManager?.IsRestoring == true)
            return;
        if (_globalData.Settings.VideoPreviewQuality == AppSettings.DefaultVideoPreviewQuality)
            return;

        _historyManager?.RecordSettingsChange("Reset video preview quality", null, "VideoPreviewQuality");
        _globalData.Settings.VideoPreviewQuality = AppSettings.DefaultVideoPreviewQuality;
        SyncShowPerformanceOptions(blockSignals: true);
    }

    private void UpdatePreviewQualityResetButton()
    {
        if (_previewQualityResetButton == null || _globalData?.Settings == null)
            return;
        bool atDefault = _globalData.Settings.VideoPreviewQuality == AppSettings.DefaultVideoPreviewQuality;
        _previewQualityResetButton.Visible = !atDefault;
        if (!atDefault)
            _previewQualityResetButton.TooltipText = UiLocalizer.T("Reset to default: Full");
    }

    private void OnVSyncSelected(long index)
    {
        if (_isSyncingUi || _globalData?.Settings == null || _historyManager?.IsRestoring == true)
            return;

        var next = (OutputVSyncMode)(int)index;
        if (_globalData.Settings.OutputVSyncMode == next)
        {
            UpdateVSyncResetButton();
            return;
        }

        _historyManager?.RecordSettingsChange("Change output vsync mode", null, "OutputVSyncMode");
        _globalData.Settings.OutputVSyncMode = next;
        _displaysManager?.ApplyOutputVSyncPreference();
        UpdateVSyncResetButton();
    }

    private void OnVSyncResetPressed()
    {
        if (_isSyncingUi || _globalData?.Settings == null || _historyManager?.IsRestoring == true)
            return;
        if (_globalData.Settings.OutputVSyncMode == AppSettings.DefaultOutputVSyncMode)
            return;

        _historyManager?.RecordSettingsChange("Reset output vsync mode", null, "OutputVSyncMode");
        _globalData.Settings.OutputVSyncMode = AppSettings.DefaultOutputVSyncMode;
        _displaysManager?.ApplyOutputVSyncPreference();
        SyncShowPerformanceOptions(blockSignals: true);
    }

    private void UpdateVSyncResetButton()
    {
        if (_vsyncResetButton == null || _globalData?.Settings == null)
            return;
        bool atDefault = _globalData.Settings.OutputVSyncMode == AppSettings.DefaultOutputVSyncMode;
        _vsyncResetButton.Visible = !atDefault;
        if (!atDefault)
            _vsyncResetButton.TooltipText = UiLocalizer.T("Reset to default: Prefer VSync");
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
