// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using Cue2.Services;
using Cue2.UI.Utilities;
using Godot;
using AppSettings = Cue2.Domain.ShowSettings.Settings;

namespace Cue2.UI.Settings;

/// <summary>
/// General settings panel: Go button scale, cuelist scale, stop fade-out, double-GO protection, media backup,
/// multi-edit, select-new-cues, and timeline waveforms.
/// Each setting shows a refresh button when not at its system default (same pattern as Cue2 Preferences).
/// Values are stored with the showfile via <see cref="AppSettings"/>.
/// UI scale lives in Cue2 Preferences (<see cref="UserDataManager"/>), not this panel.
/// </summary>
public partial class SettingsGeneral : ScrollContainer
{
    private GlobalSignals _globalSignals;
    private GlobalData _globalData;
    private HistoryManager _historyManager;

    private OptionButton _goScaleOptionButton;
    private Button _goScaleResetButton;

    private OptionButton _cueListScaleOptionButton;
    private Button _cueListScaleResetButton;

    private SpinBox _stopFadeSpinBox;
    private Button _stopFadeResetButton;

    private SpinBox _doubleGoSpinBox;
    private Button _doubleGoResetButton;

    private CheckBox _mediaBackupCheckBox;
    private Button _mediaBackupResetButton;

    private CheckBox _multiEditCheckBox;
    private Button _multiEditResetButton;

    private CheckBox _selectNewCuesCheckBox;
    private Button _selectNewCuesResetButton;

    private CheckBox _timelineWaveformsCheckBox;
    private Button _timelineWaveformsResetButton;

    /// <summary>True while pushing model → controls so handlers do not re-record history.</summary>
    private bool _isSyncingUi;

    /// <summary>Go scale option index → scale factor (matches OptionButton order).</summary>
    /// <remarks>0 = No Go (hide header), 0.5 = Half Go (hide notes), others scale the GO button.</remarks>
    private static readonly float[] GoScaleValues =
    {
        AppSettings.GoScaleNoGo,
        AppSettings.GoScaleHalf,
        1.0f,
        2.0f,
        4.0f,
        8.0f,
        32.0f
    };

    /// <summary>Cue list scale option index → scale factor (Small / Medium / Large).</summary>
    private static readonly float[] CueListScaleValues =
    {
        AppSettings.CueListScaleSmall,
        AppSettings.CueListScaleMedium,
        AppSettings.CueListScaleLarge
    };

    public override void _Ready()
    {
        GD.Print("SettingsGeneral:_Ready - Settings General Init");

        _globalSignals = GetNode<GlobalSignals>("/root/GlobalSignals");
        _globalData = GetNode<GlobalData>("/root/GlobalData");
        _historyManager = _globalData?.HistoryManager;

        _goScaleOptionButton = GetNode<OptionButton>("%GoScaleOptionButton");
        _goScaleResetButton = GetNode<Button>("%GoScaleResetButton");
        _goScaleResetButton.Icon = GetThemeIcon("Refresh", "AtlasIcons");
        _goScaleResetButton.Pressed += OnGoScaleResetPressed;
        _goScaleOptionButton.ItemSelected += OnGoScaleItemSelected;

        _cueListScaleOptionButton = GetNodeOrNull<OptionButton>("%CueListScaleOptionButton");
        _cueListScaleResetButton = GetNodeOrNull<Button>("%CueListScaleResetButton");
        if (_cueListScaleResetButton != null)
        {
            _cueListScaleResetButton.Icon = GetThemeIcon("Refresh", "AtlasIcons");
            _cueListScaleResetButton.Pressed += OnCueListScaleResetPressed;
        }
        if (_cueListScaleOptionButton != null)
            _cueListScaleOptionButton.ItemSelected += OnCueListScaleItemSelected;

        _stopFadeSpinBox = GetNode<SpinBox>("%StopFadeSpinBox");
        _stopFadeResetButton = GetNode<Button>("%StopFadeResetButton");
        _stopFadeResetButton.Icon = GetThemeIcon("Refresh", "AtlasIcons");
        _stopFadeResetButton.Pressed += OnStopFadeResetPressed;
        _stopFadeSpinBox.ValueChanged += OnStopFadeChanged;
        _stopFadeSpinBox.Editable = true;
        _stopFadeSpinBox.FocusMode = FocusModeEnum.All;
        var stopFadeEdit = _stopFadeSpinBox.GetLineEdit();
        if (stopFadeEdit != null)
            stopFadeEdit.FocusMode = FocusModeEnum.All;

        _doubleGoSpinBox = GetNodeOrNull<SpinBox>("%DoubleGoProtectionSpinBox");
        _doubleGoResetButton = GetNodeOrNull<Button>("%DoubleGoProtectionResetButton");
        if (_doubleGoResetButton != null)
        {
            _doubleGoResetButton.Icon = GetThemeIcon("Refresh", "AtlasIcons");
            _doubleGoResetButton.Pressed += OnDoubleGoResetPressed;
        }
        if (_doubleGoSpinBox != null)
        {
            _doubleGoSpinBox.ValueChanged += OnDoubleGoChanged;
            _doubleGoSpinBox.Editable = true;
            _doubleGoSpinBox.FocusMode = FocusModeEnum.All;
            var doubleGoEdit = _doubleGoSpinBox.GetLineEdit();
            if (doubleGoEdit != null)
                doubleGoEdit.FocusMode = FocusModeEnum.All;
        }

        _mediaBackupCheckBox = GetNode<CheckBox>("%MediaBackupCheckBox");
        _mediaBackupCheckBox.Toggled += OnMediaBackupToggled;
        _mediaBackupResetButton = GetNode<Button>("%MediaBackupResetButton");
        _mediaBackupResetButton.Icon = GetThemeIcon("Refresh", "AtlasIcons");
        _mediaBackupResetButton.Pressed += OnMediaBackupResetPressed;

        _multiEditCheckBox = GetNodeOrNull<CheckBox>("%MultiEditCheckBox");
        if (_multiEditCheckBox != null)
            _multiEditCheckBox.Toggled += OnMultiEditToggled;
        _multiEditResetButton = GetNodeOrNull<Button>("%MultiEditResetButton");
        if (_multiEditResetButton != null)
        {
            _multiEditResetButton.Icon = GetThemeIcon("Refresh", "AtlasIcons");
            _multiEditResetButton.Pressed += OnMultiEditResetPressed;
        }

        _selectNewCuesCheckBox = GetNodeOrNull<CheckBox>("%SelectNewCuesCheckBox");
        if (_selectNewCuesCheckBox != null)
            _selectNewCuesCheckBox.Toggled += OnSelectNewCuesToggled;
        _selectNewCuesResetButton = GetNodeOrNull<Button>("%SelectNewCuesResetButton");
        if (_selectNewCuesResetButton != null)
        {
            _selectNewCuesResetButton.Icon = GetThemeIcon("Refresh", "AtlasIcons");
            _selectNewCuesResetButton.Pressed += OnSelectNewCuesResetPressed;
        }

        _timelineWaveformsCheckBox = GetNodeOrNull<CheckBox>("%TimelineWaveformsCheckBox");
        if (_timelineWaveformsCheckBox != null)
            _timelineWaveformsCheckBox.Toggled += OnTimelineWaveformsToggled;
        _timelineWaveformsResetButton = GetNodeOrNull<Button>("%TimelineWaveformsResetButton");
        if (_timelineWaveformsResetButton != null)
        {
            _timelineWaveformsResetButton.Icon = GetThemeIcon("Refresh", "AtlasIcons");
            _timelineWaveformsResetButton.Pressed += OnTimelineWaveformsResetPressed;
        }

        // Only re-sync this form when a *settings* history entry was undone/redone —
        // not on cue undos (that re-wrote the spin box and polluted history / looked like
        // fade was jumping while undoing unrelated steps).
        if (_historyManager != null)
            _historyManager.HistoryRestored += OnHistoryRestored;
        if (_globalSignals != null)
        {
            _globalSignals.NewSession += OnNewSession;
            _globalSignals.LocaleChanged += OnLocaleChanged;
        }

        SyncSettings();
        UiLocalizer.LocalizeTree(this);
    }

    public override void _ExitTree()
    {
        if (_historyManager != null)
            _historyManager.HistoryRestored -= OnHistoryRestored;
        if (_globalSignals != null)
        {
            _globalSignals.NewSession -= OnNewSession;
            _globalSignals.LocaleChanged -= OnLocaleChanged;
        }
        base._ExitTree();
    }

    /// <summary>
    /// Re-localizes General settings labels and tooltips when the UI language changes.
    /// </summary>
    /// <param name="localeCode">New locale code.</param>
    private void OnLocaleChanged(string localeCode)
    {
        if (!GodotObject.IsInstanceValid(this))
            return;
        UiLocalizer.LocalizeTree(this);
    }

    /// <summary>
    /// After undo/redo of a settings-scoped entry, re-read show settings into this panel.
    /// </summary>
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

    /// <summary>
    /// Pulls current <see cref="AppSettings"/> values into the form without re-firing edit handlers.
    /// </summary>
    private void SyncSettings()
    {
        if (_globalData?.Settings == null) return;

        _isSyncingUi = true;
        try
        {
            if (_goScaleOptionButton != null)
            {
                _goScaleOptionButton.SetBlockSignals(true);
                _goScaleOptionButton.Selected = GoScaleToIndex(_globalData.Settings.GoScale);
                _goScaleOptionButton.SetBlockSignals(false);
            }

            if (_cueListScaleOptionButton != null)
            {
                _cueListScaleOptionButton.SetBlockSignals(true);
                _cueListScaleOptionButton.Selected = CueListScaleToIndex(_globalData.Settings.CueListScale);
                _cueListScaleOptionButton.SetBlockSignals(false);
            }

            _stopFadeSpinBox?.SetValueNoSignal(_globalData.Settings.StopFadeDuration);
            _doubleGoSpinBox?.SetValueNoSignal(_globalData.Settings.DoubleGoProtectionSeconds);
            _mediaBackupCheckBox?.SetPressedNoSignal(_globalData.Settings.MediaBackupEnabled);
            _multiEditCheckBox?.SetPressedNoSignal(_globalData.Settings.MultiEditEnabled);
            _selectNewCuesCheckBox?.SetPressedNoSignal(_globalData.Settings.SelectNewCues);
            _timelineWaveformsCheckBox?.SetPressedNoSignal(_globalData.Settings.ShowTimelineWaveforms);

            UpdateAllResetButtons();
        }
        finally
        {
            _isSyncingUi = false;
        }
    }

    private void UpdateAllResetButtons()
    {
        UpdateGoScaleResetButton();
        UpdateCueListScaleResetButton();
        UpdateStopFadeResetButton();
        UpdateDoubleGoResetButton();
        UpdateMediaBackupResetButton();
        UpdateMultiEditResetButton();
        UpdateSelectNewCuesResetButton();
        UpdateTimelineWaveformsResetButton();
    }

    // ── Go Button Scale ───────────────────────────────────────────────────

    private void OnGoScaleItemSelected(long index)
    {
        if (_isSyncingUi || _globalData?.Settings == null) return;
        if (_historyManager?.IsRestoring == true) return;

        int i = (int)index;
        if (i < 0 || i >= GoScaleValues.Length)
            i = GoScaleToIndex(AppSettings.DefaultGoScale);

        float scale = GoScaleValues[i];
        if (Mathf.IsEqualApprox(_globalData.Settings.GoScale, scale))
        {
            UpdateGoScaleResetButton();
            return;
        }

        _historyManager?.RecordSettingsChange("Change Go scale", null, "GoScale");
        _globalData.Settings.GoScale = scale;
        _globalSignals.EmitSignal(nameof(GlobalSignals.GoScaleChanged), _globalData.Settings.GoScale);
        UpdateGoScaleResetButton();
    }

    private void OnGoScaleResetPressed()
    {
        if (_isSyncingUi || _globalData?.Settings == null) return;
        if (Mathf.IsEqualApprox(_globalData.Settings.GoScale, AppSettings.DefaultGoScale))
        {
            SyncSettings();
            return;
        }

        _historyManager?.RecordSettingsChange("Reset Go scale", null, "GoScale");
        _globalData.Settings.GoScale = AppSettings.DefaultGoScale;
        SyncSettings();
        _globalSignals.EmitSignal(nameof(GlobalSignals.GoScaleChanged), AppSettings.DefaultGoScale);
    }

    private void UpdateGoScaleResetButton()
    {
        if (_goScaleResetButton == null || _globalData?.Settings == null) return;

        bool atDefault = Mathf.IsEqualApprox(_globalData.Settings.GoScale, AppSettings.DefaultGoScale);
        _goScaleResetButton.Visible = !atDefault;
        if (!atDefault)
            _goScaleResetButton.TooltipText = UiLocalizer.ResetDefaultTip(GoScaleLabel(AppSettings.DefaultGoScale));
    }

    private static int GoScaleToIndex(float scale)
    {
        for (int i = 0; i < GoScaleValues.Length; i++)
        {
            if (Mathf.IsEqualApprox(GoScaleValues[i], scale))
                return i;
        }
        for (int i = 0; i < GoScaleValues.Length; i++)
        {
            if (Mathf.IsEqualApprox(GoScaleValues[i], AppSettings.DefaultGoScale))
                return i;
        }
        return 1;
    }

    private static string GoScaleLabel(float scale)
    {
        return scale switch
        {
            0.0f => "No Go",
            0.5f => "Half Go",
            1.0f => "Base Scale Go",
            2.0f => "Big Go",
            4.0f => "Very Big Go",
            8.0f => "Wow, that's a big go",
            32.0f => "Nothing but Go",
            _ => scale.ToString("0.##")
        };
    }

    // ── Cue List Scale ────────────────────────────────────────────────────

    private void OnCueListScaleItemSelected(long index)
    {
        if (_isSyncingUi || _globalData?.Settings == null) return;
        if (_historyManager?.IsRestoring == true) return;

        int i = (int)index;
        if (i < 0 || i >= CueListScaleValues.Length)
            i = CueListScaleToIndex(AppSettings.DefaultCueListScale);

        float scale = CueListScaleValues[i];
        if (Mathf.IsEqualApprox(_globalData.Settings.CueListScale, scale))
        {
            UpdateCueListScaleResetButton();
            return;
        }

        _historyManager?.RecordSettingsChange("Change cue list scale", null, "CueListScale");
        _globalData.Settings.CueListScale = scale;
        _globalSignals.EmitSignal(nameof(GlobalSignals.CueListScaleChanged), _globalData.Settings.CueListScale);
        UpdateCueListScaleResetButton();
    }

    private void OnCueListScaleResetPressed()
    {
        if (_isSyncingUi || _globalData?.Settings == null) return;
        if (Mathf.IsEqualApprox(_globalData.Settings.CueListScale, AppSettings.DefaultCueListScale))
        {
            SyncSettings();
            return;
        }

        _historyManager?.RecordSettingsChange("Reset cue list scale", null, "CueListScale");
        _globalData.Settings.CueListScale = AppSettings.DefaultCueListScale;
        SyncSettings();
        _globalSignals.EmitSignal(nameof(GlobalSignals.CueListScaleChanged), AppSettings.DefaultCueListScale);
    }

    private void UpdateCueListScaleResetButton()
    {
        if (_cueListScaleResetButton == null || _globalData?.Settings == null) return;

        bool atDefault = Mathf.IsEqualApprox(_globalData.Settings.CueListScale, AppSettings.DefaultCueListScale);
        _cueListScaleResetButton.Visible = !atDefault;
        if (!atDefault)
            _cueListScaleResetButton.TooltipText = UiLocalizer.ResetDefaultTip(CueListScaleLabel(AppSettings.DefaultCueListScale));
    }

    private static int CueListScaleToIndex(float scale)
    {
        for (int i = 0; i < CueListScaleValues.Length; i++)
        {
            if (Mathf.IsEqualApprox(CueListScaleValues[i], scale))
                return i;
        }
        for (int i = 0; i < CueListScaleValues.Length; i++)
        {
            if (Mathf.IsEqualApprox(CueListScaleValues[i], AppSettings.DefaultCueListScale))
                return i;
        }
        return 1;
    }

    private static string CueListScaleLabel(float scale)
    {
        if (Mathf.IsEqualApprox(scale, AppSettings.CueListScaleSmall))
            return "Small";
        if (Mathf.IsEqualApprox(scale, AppSettings.CueListScaleMedium))
            return "Medium";
        if (Mathf.IsEqualApprox(scale, AppSettings.CueListScaleLarge))
            return "Large";
        return scale.ToString("0.##");
    }

    // ── Stop Fade Out ─────────────────────────────────────────────────────

    private void OnStopFadeChanged(double value)
    {
        if (_isSyncingUi || _globalData?.Settings == null) return;
        if (_historyManager?.IsRestoring == true) return;

        float fade = (float)Mathf.Clamp(value, 0.0, 30.0);
        if (Mathf.IsEqualApprox(_globalData.Settings.StopFadeDuration, fade))
        {
            UpdateStopFadeResetButton();
            return;
        }

        // Simple discrete step: each committed value change is one undo entry (no coalesce).
        _historyManager?.RecordSettingsChange("Change stop fade", null, "StopFadeDuration");
        _globalData.Settings.StopFadeDuration = fade;
        UpdateStopFadeResetButton();
    }

    private void OnStopFadeResetPressed()
    {
        if (_isSyncingUi || _globalData?.Settings == null) return;
        if (Mathf.IsEqualApprox(_globalData.Settings.StopFadeDuration, AppSettings.DefaultStopFadeDuration))
        {
            SyncSettings();
            return;
        }

        _historyManager?.RecordSettingsChange("Reset stop fade", null, "StopFadeDuration");
        _globalData.Settings.StopFadeDuration = AppSettings.DefaultStopFadeDuration;
        SyncSettings();
    }

    private void UpdateStopFadeResetButton()
    {
        if (_stopFadeResetButton == null || _globalData?.Settings == null) return;

        bool atDefault = Mathf.IsEqualApprox(_globalData.Settings.StopFadeDuration, AppSettings.DefaultStopFadeDuration);
        _stopFadeResetButton.Visible = !atDefault;
        if (!atDefault)
            _stopFadeResetButton.TooltipText = UiLocalizer.ResetDefaultTip($"{AppSettings.DefaultStopFadeDuration:0.#}s");
    }

    // ── Double Go Protection ──────────────────────────────────────────────

    private void OnDoubleGoChanged(double value)
    {
        if (_isSyncingUi || _globalData?.Settings == null) return;
        if (_historyManager?.IsRestoring == true) return;

        float seconds = (float)Mathf.Clamp(value, 0.0, AppSettings.MaxDoubleGoProtectionSeconds);
        if (Mathf.IsEqualApprox(_globalData.Settings.DoubleGoProtectionSeconds, seconds))
        {
            UpdateDoubleGoResetButton();
            return;
        }

        _historyManager?.RecordSettingsChange("Change double GO protection", null, "DoubleGoProtection");
        _globalData.Settings.DoubleGoProtectionSeconds = seconds;
        UpdateDoubleGoResetButton();
    }

    private void OnDoubleGoResetPressed()
    {
        if (_isSyncingUi || _globalData?.Settings == null) return;
        if (Mathf.IsEqualApprox(_globalData.Settings.DoubleGoProtectionSeconds,
                AppSettings.DefaultDoubleGoProtectionSeconds))
        {
            SyncSettings();
            return;
        }

        _historyManager?.RecordSettingsChange("Reset double GO protection", null, "DoubleGoProtection");
        _globalData.Settings.DoubleGoProtectionSeconds = AppSettings.DefaultDoubleGoProtectionSeconds;
        SyncSettings();
    }

    private void UpdateDoubleGoResetButton()
    {
        if (_doubleGoResetButton == null || _globalData?.Settings == null) return;

        bool atDefault = Mathf.IsEqualApprox(_globalData.Settings.DoubleGoProtectionSeconds,
            AppSettings.DefaultDoubleGoProtectionSeconds);
        _doubleGoResetButton.Visible = !atDefault;
        if (!atDefault)
            _doubleGoResetButton.TooltipText =
                $"Reset to default: {AppSettings.DefaultDoubleGoProtectionSeconds:0.#}s";
    }

    // ── Media Backup ──────────────────────────────────────────────────────

    private void OnMediaBackupToggled(bool enabled)
    {
        if (_isSyncingUi || _globalData?.Settings == null) return;
        if (_historyManager?.IsRestoring == true) return;

        if (_globalData.Settings.MediaBackupEnabled == enabled)
        {
            UpdateMediaBackupResetButton();
            return;
        }

        _historyManager?.RecordSettingsChange("Change media backup setting", null, "MediaBackupEnabled");
        _globalData.Settings.MediaBackupEnabled = enabled;
        UpdateMediaBackupResetButton();
    }

    private void OnMediaBackupResetPressed()
    {
        if (_isSyncingUi || _globalData?.Settings == null) return;
        if (_globalData.Settings.MediaBackupEnabled == AppSettings.DefaultMediaBackupEnabled)
        {
            SyncSettings();
            return;
        }

        _historyManager?.RecordSettingsChange("Reset media backup setting", null, "MediaBackupEnabled");
        _globalData.Settings.MediaBackupEnabled = AppSettings.DefaultMediaBackupEnabled;
        SyncSettings();
    }

    private void UpdateMediaBackupResetButton()
    {
        if (_mediaBackupResetButton == null || _globalData?.Settings == null) return;

        bool atDefault = _globalData.Settings.MediaBackupEnabled == AppSettings.DefaultMediaBackupEnabled;
        _mediaBackupResetButton.Visible = !atDefault;
        if (!atDefault)
        {
            string defaultText = AppSettings.DefaultMediaBackupEnabled ? "Enabled" : "Disabled";
            _mediaBackupResetButton.TooltipText = UiLocalizer.ResetDefaultTip(defaultText);
        }
    }

    // ── Multi-edit ────────────────────────────────────────────────────────

    private void OnMultiEditToggled(bool enabled)
    {
        if (_isSyncingUi || _globalData?.Settings == null) return;
        if (_historyManager?.IsRestoring == true) return;

        if (_globalData.Settings.MultiEditEnabled == enabled)
        {
            UpdateMultiEditResetButton();
            return;
        }

        _historyManager?.RecordSettingsChange("Change multi-edit setting", null, "MultiEditEnabled");
        _globalData.Settings.MultiEditEnabled = enabled;
        // Refresh shell inspector if open so multi/single mode tracks the toggle immediately.
        _globalSignals?.EmitSignal(nameof(GlobalSignals.SyncShellInspector));
        UpdateMultiEditResetButton();
    }

    private void OnMultiEditResetPressed()
    {
        if (_isSyncingUi || _globalData?.Settings == null) return;
        if (_globalData.Settings.MultiEditEnabled == AppSettings.DefaultMultiEditEnabled)
        {
            SyncSettings();
            return;
        }

        _historyManager?.RecordSettingsChange("Reset multi-edit setting", null, "MultiEditEnabled");
        _globalData.Settings.MultiEditEnabled = AppSettings.DefaultMultiEditEnabled;
        SyncSettings();
        _globalSignals?.EmitSignal(nameof(GlobalSignals.SyncShellInspector));
    }

    private void UpdateMultiEditResetButton()
    {
        if (_multiEditResetButton == null || _globalData?.Settings == null) return;

        bool atDefault = _globalData.Settings.MultiEditEnabled == AppSettings.DefaultMultiEditEnabled;
        _multiEditResetButton.Visible = !atDefault;
        if (!atDefault)
        {
            string defaultText = AppSettings.DefaultMultiEditEnabled ? "Enabled" : "Disabled";
            _multiEditResetButton.TooltipText = UiLocalizer.ResetDefaultTip(defaultText);
        }
    }

    // ── Select new cues ───────────────────────────────────────────────────

    private void OnSelectNewCuesToggled(bool enabled)
    {
        if (_isSyncingUi || _globalData?.Settings == null) return;
        if (_historyManager?.IsRestoring == true) return;

        if (_globalData.Settings.SelectNewCues == enabled)
        {
            UpdateSelectNewCuesResetButton();
            return;
        }

        _historyManager?.RecordSettingsChange("Change select new cues setting", null, "SelectNewCues");
        _globalData.Settings.SelectNewCues = enabled;
        UpdateSelectNewCuesResetButton();
    }

    private void OnSelectNewCuesResetPressed()
    {
        if (_isSyncingUi || _globalData?.Settings == null) return;
        if (_globalData.Settings.SelectNewCues == AppSettings.DefaultSelectNewCues)
        {
            SyncSettings();
            return;
        }

        _historyManager?.RecordSettingsChange("Reset select new cues setting", null, "SelectNewCues");
        _globalData.Settings.SelectNewCues = AppSettings.DefaultSelectNewCues;
        SyncSettings();
    }

    private void UpdateSelectNewCuesResetButton()
    {
        if (_selectNewCuesResetButton == null || _globalData?.Settings == null) return;

        bool atDefault = _globalData.Settings.SelectNewCues == AppSettings.DefaultSelectNewCues;
        _selectNewCuesResetButton.Visible = !atDefault;
        if (!atDefault)
        {
            string defaultText = AppSettings.DefaultSelectNewCues ? "Enabled" : "Disabled";
            _selectNewCuesResetButton.TooltipText = UiLocalizer.ResetDefaultTip(defaultText);
        }
    }

    // ── Timeline waveforms ────────────────────────────────────────────────

    private void OnTimelineWaveformsToggled(bool enabled)
    {
        if (_isSyncingUi || _globalData?.Settings == null) return;
        if (_historyManager?.IsRestoring == true) return;

        if (_globalData.Settings.ShowTimelineWaveforms == enabled)
        {
            UpdateTimelineWaveformsResetButton();
            return;
        }

        _historyManager?.RecordSettingsChange("Change timeline waveforms setting", null, "ShowTimelineWaveforms");
        _globalData.Settings.ShowTimelineWaveforms = enabled;
        _globalSignals?.EmitSignal(nameof(GlobalSignals.ShowTimelineWaveformsChanged), enabled);
        UpdateTimelineWaveformsResetButton();
    }

    private void OnTimelineWaveformsResetPressed()
    {
        if (_isSyncingUi || _globalData?.Settings == null) return;
        if (_globalData.Settings.ShowTimelineWaveforms == AppSettings.DefaultShowTimelineWaveforms)
        {
            SyncSettings();
            return;
        }

        _historyManager?.RecordSettingsChange("Reset timeline waveforms setting", null, "ShowTimelineWaveforms");
        _globalData.Settings.ShowTimelineWaveforms = AppSettings.DefaultShowTimelineWaveforms;
        SyncSettings();
        _globalSignals?.EmitSignal(nameof(GlobalSignals.ShowTimelineWaveformsChanged),
            AppSettings.DefaultShowTimelineWaveforms);
    }

    private void UpdateTimelineWaveformsResetButton()
    {
        if (_timelineWaveformsResetButton == null || _globalData?.Settings == null) return;

        bool atDefault = _globalData.Settings.ShowTimelineWaveforms == AppSettings.DefaultShowTimelineWaveforms;
        _timelineWaveformsResetButton.Visible = !atDefault;
        if (!atDefault)
        {
            string defaultText = AppSettings.DefaultShowTimelineWaveforms ? "Enabled" : "Disabled";
            _timelineWaveformsResetButton.TooltipText = UiLocalizer.ResetDefaultTip(defaultText);
        }
    }
}
