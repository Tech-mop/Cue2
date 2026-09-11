// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using Cue2.Domain.Cues;
using Cue2.Services;
using Cue2.UI.Utilities;
using Godot;
using AppSettings = Cue2.Domain.ShowSettings.Settings;

namespace Cue2.UI.Settings;

/// <summary>
/// Settings panel for show-scoped cue shell defaults (pre-wait, post-wait, continue mode,
/// colour, armed, skip-if-disarmed). Values match the editable fields on Shell Inspector
/// and are applied to every newly created cue.
/// </summary>
/// <remarks>
/// Each control shows a refresh button when not at its system default (same pattern as
/// General / Cue2 Preferences). Stored with the showfile via <see cref="AppSettings"/>.
/// </remarks>
public partial class SettingsCueDefaults : ScrollContainer
{
    private GlobalSignals _globalSignals;
    private GlobalData _globalData;
    private HistoryManager _historyManager;

    private LineEdit _preWaitInput;
    private Button _preWaitResetButton;

    private LineEdit _postWaitInput;
    private Button _postWaitResetButton;

    private OptionButton _followOption;
    private Button _followResetButton;

    private ColorPickerButton _colorPicker;
    private Button _colorResetButton;

    private CheckBox _armedCheckBox;
    private Button _armedResetButton;

    private CheckBox _skipIfDisarmedCheckBox;
    private Button _skipIfDisarmedResetButton;

    private CheckBox _onlyOneActiveCheckBox;
    private Button _onlyOneActiveResetButton;

    /// <summary>True while pushing model → controls so handlers do not re-record history.</summary>
    private bool _isSyncingUi;

    public override void _Ready()
    {
        GD.Print("SettingsCueDefaults:_Ready - Cue Defaults panel init");

        _globalSignals = GetNode<GlobalSignals>("/root/GlobalSignals");
        _globalData = GetNode<GlobalData>("/root/GlobalData");
        _historyManager = _globalData?.HistoryManager;

        _preWaitInput = GetNode<LineEdit>("%PreWaitInput");
        _preWaitResetButton = GetNode<Button>("%PreWaitResetButton");
        _postWaitInput = GetNode<LineEdit>("%PostWaitInput");
        _postWaitResetButton = GetNode<Button>("%PostWaitResetButton");
        _followOption = GetNode<OptionButton>("%FollowOption");
        _followResetButton = GetNode<Button>("%FollowResetButton");
        _colorPicker = GetNode<ColorPickerButton>("%ColourPickerButton");
        _colorResetButton = GetNode<Button>("%ColourResetButton");
        _armedCheckBox = GetNode<CheckBox>("%ArmedCheckBox");
        _armedResetButton = GetNode<Button>("%ArmedResetButton");
        _skipIfDisarmedCheckBox = GetNode<CheckBox>("%SkipIfDisarmedCheckBox");
        _skipIfDisarmedResetButton = GetNode<Button>("%SkipIfDisarmedResetButton");
        _onlyOneActiveCheckBox = GetNodeOrNull<CheckBox>("%OnlyOneActiveCheckBox");
        _onlyOneActiveResetButton = GetNodeOrNull<Button>("%OnlyOneActiveResetButton");

        ComponentDefaultsUi.SetupResetButton(this, _preWaitResetButton, OnPreWaitResetPressed);
        ComponentDefaultsUi.SetupResetButton(this, _postWaitResetButton, OnPostWaitResetPressed);
        ComponentDefaultsUi.SetupResetButton(this, _followResetButton, OnFollowResetPressed);
        ComponentDefaultsUi.SetupResetButton(this, _colorResetButton, OnColorResetPressed);
        ComponentDefaultsUi.SetupResetButton(this, _armedResetButton, OnArmedResetPressed);
        ComponentDefaultsUi.SetupResetButton(this, _skipIfDisarmedResetButton, OnSkipIfDisarmedResetPressed);
        if (_onlyOneActiveResetButton != null)
            ComponentDefaultsUi.SetupResetButton(this, _onlyOneActiveResetButton, OnOnlyOneActiveResetPressed);

        EnsureFollowOptions();

        _preWaitInput.TextSubmitted += OnPreWaitSubmitted;
        _preWaitInput.FocusExited += OnPreWaitFocusExited;
        _postWaitInput.TextSubmitted += OnPostWaitSubmitted;
        _postWaitInput.FocusExited += OnPostWaitFocusExited;
        _followOption.ItemSelected += OnFollowItemSelected;
        _colorPicker.PopupClosed += OnColorPopupClosed;
        _armedCheckBox.Toggled += OnArmedToggled;
        _skipIfDisarmedCheckBox.Toggled += OnSkipIfDisarmedToggled;
        if (_onlyOneActiveCheckBox != null)
            _onlyOneActiveCheckBox.Toggled += OnOnlyOneActiveToggled;

        if (_historyManager != null)
            _historyManager.HistoryRestored += OnHistoryRestored;
        if (_globalSignals != null)
            _globalSignals.NewSession += OnNewSession;

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
            _globalSignals.NewSession -= OnNewSession;
        base._ExitTree();
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

    /// <summary>
    /// Pulls current cue defaults into the form without re-firing edit handlers.
    /// </summary>
    private void SyncSettings()
    {
        if (_globalData?.Settings == null) return;

        _isSyncingUi = true;
        try
        {
            var s = _globalData.Settings;
            if (_preWaitInput != null)
                _preWaitInput.Text = UiUtilities.FormatTime(s.CueDefaultPreWait);
            if (_postWaitInput != null)
                _postWaitInput.Text = UiUtilities.FormatTime(s.CueDefaultPostWait);

            EnsureFollowOptions();
            SelectFollowOption(s.CueDefaultFollow);

            if (_colorPicker != null)
                _colorPicker.Color = s.CueDefaultColor;

            _armedCheckBox?.SetPressedNoSignal(s.CueDefaultArmed);
            _skipIfDisarmedCheckBox?.SetPressedNoSignal(s.CueDefaultSkipIfDisarmed);
            _onlyOneActiveCheckBox?.SetPressedNoSignal(s.CueDefaultOnlyOneActiveInstance);

            UpdateAllResetButtons();
        }
        finally
        {
            _isSyncingUi = false;
        }
    }

    private void UpdateAllResetButtons()
    {
        UpdatePreWaitResetButton();
        UpdatePostWaitResetButton();
        UpdateFollowResetButton();
        UpdateColorResetButton();
        UpdateArmedResetButton();
        UpdateSkipIfDisarmedResetButton();
        UpdateOnlyOneActiveResetButton();
    }

    private void RecordCueDefaultsHistory(string description, string coalesceKey = null)
    {
        ComponentDefaultsUi.RecordDefaultsChange(_historyManager, description, "CueDefaults", coalesceKey);
    }

    // ── Follow option helpers ──────────────────────────────────────────────

    private void EnsureFollowOptions()
    {
        if (_followOption == null) return;
        if (_followOption.ItemCount > 0) return;

        _followOption.Clear();
        AddFollowOption(FollowType.None, "None");
        AddFollowOption(FollowType.Continue, "Auto-continue");
        AddFollowOption(FollowType.Follow, "Auto-follow");
    }

    private void AddFollowOption(FollowType type, string label)
    {
        UiLocalizer.AddTranslatedItem(_followOption, label);
        _followOption.SetItemMetadata(_followOption.ItemCount - 1, (int)type);
    }

    private void SelectFollowOption(FollowType follow)
    {
        if (_followOption == null) return;
        EnsureFollowOptions();
        _followOption.SetBlockSignals(true);
        for (int i = 0; i < _followOption.ItemCount; i++)
        {
            if (_followOption.GetItemMetadata(i).AsInt32() == (int)follow)
            {
                _followOption.Selected = i;
                _followOption.SetBlockSignals(false);
                return;
            }
        }
        _followOption.Selected = 0;
        _followOption.SetBlockSignals(false);
    }

    private static string FollowLabel(FollowType follow)
    {
        return follow switch
        {
            FollowType.Continue => "Auto-continue",
            FollowType.Follow => "Auto-follow",
            _ => "None"
        };
    }

    // ── Pre-wait ───────────────────────────────────────────────────────────

    private void OnPreWaitSubmitted(string text) => CommitPreWait(text);

    private void OnPreWaitFocusExited()
    {
        if (_isSyncingUi || _preWaitInput == null) return;
        CommitPreWait(_preWaitInput.Text);
    }

    private void CommitPreWait(string text)
    {
        if (_globalData?.Settings == null || ComponentDefaultsUi.ShouldSkipEdit(_isSyncingUi, _historyManager)) return;

        var formatted = UiUtilities.ParseAndFormatTime(text, out var seconds, out string labeled);
        if (string.IsNullOrEmpty(formatted))
        {
            _globalSignals?.EmitSignal(nameof(GlobalSignals.Log),
                $"Invalid pre-wait time: {text}", 1);
            _preWaitInput.Text = UiUtilities.FormatTime(_globalData.Settings.CueDefaultPreWait);
            return;
        }

        _preWaitInput.Text = formatted;
        _preWaitInput.TooltipText = labeled;

        if (Mathf.IsEqualApprox((float)_globalData.Settings.CueDefaultPreWait, (float)seconds))
        {
            UpdatePreWaitResetButton();
            return;
        }

        RecordCueDefaultsHistory("Change default pre-wait");
        _globalData.Settings.CueDefaultPreWait = seconds;
        UpdatePreWaitResetButton();
    }

    private void OnPreWaitResetPressed()
    {
        if (_isSyncingUi || _globalData?.Settings == null) return;
        if (Mathf.IsEqualApprox((float)_globalData.Settings.CueDefaultPreWait,
                (float)AppSettings.SystemDefaultCuePreWait))
        {
            SyncSettings();
            return;
        }

        RecordCueDefaultsHistory("Reset default pre-wait");
        _globalData.Settings.CueDefaultPreWait = AppSettings.SystemDefaultCuePreWait;
        SyncSettings();
    }

    private void UpdatePreWaitResetButton()
    {
        if (_preWaitResetButton == null || _globalData?.Settings == null) return;
        bool atDefault = Mathf.IsEqualApprox((float)_globalData.Settings.CueDefaultPreWait,
            (float)AppSettings.SystemDefaultCuePreWait);
        _preWaitResetButton.Visible = !atDefault;
        if (!atDefault)
            _preWaitResetButton.TooltipText =
                UiLocalizer.ResetDefaultTip(UiUtilities.FormatTime(AppSettings.SystemDefaultCuePreWait));
    }

    // ── Post-wait ──────────────────────────────────────────────────────────

    private void OnPostWaitSubmitted(string text) => CommitPostWait(text);

    private void OnPostWaitFocusExited()
    {
        if (_isSyncingUi || _postWaitInput == null) return;
        CommitPostWait(_postWaitInput.Text);
    }

    private void CommitPostWait(string text)
    {
        if (_globalData?.Settings == null || ComponentDefaultsUi.ShouldSkipEdit(_isSyncingUi, _historyManager)) return;

        var formatted = UiUtilities.ParseAndFormatTime(text, out var seconds, out string labeled);
        if (string.IsNullOrEmpty(formatted))
        {
            _globalSignals?.EmitSignal(nameof(GlobalSignals.Log),
                $"Invalid post-wait time: {text}", 1);
            _postWaitInput.Text = UiUtilities.FormatTime(_globalData.Settings.CueDefaultPostWait);
            return;
        }

        _postWaitInput.Text = formatted;
        _postWaitInput.TooltipText = labeled;

        if (Mathf.IsEqualApprox((float)_globalData.Settings.CueDefaultPostWait, (float)seconds))
        {
            UpdatePostWaitResetButton();
            return;
        }

        RecordCueDefaultsHistory("Change default post-wait");
        _globalData.Settings.CueDefaultPostWait = seconds;
        UpdatePostWaitResetButton();
    }

    private void OnPostWaitResetPressed()
    {
        if (_isSyncingUi || _globalData?.Settings == null) return;
        if (Mathf.IsEqualApprox((float)_globalData.Settings.CueDefaultPostWait,
                (float)AppSettings.SystemDefaultCuePostWait))
        {
            SyncSettings();
            return;
        }

        RecordCueDefaultsHistory("Reset default post-wait");
        _globalData.Settings.CueDefaultPostWait = AppSettings.SystemDefaultCuePostWait;
        SyncSettings();
    }

    private void UpdatePostWaitResetButton()
    {
        if (_postWaitResetButton == null || _globalData?.Settings == null) return;
        bool atDefault = Mathf.IsEqualApprox((float)_globalData.Settings.CueDefaultPostWait,
            (float)AppSettings.SystemDefaultCuePostWait);
        _postWaitResetButton.Visible = !atDefault;
        if (!atDefault)
            _postWaitResetButton.TooltipText =
                UiLocalizer.ResetDefaultTip(UiUtilities.FormatTime(AppSettings.SystemDefaultCuePostWait));
    }

    // ── Continue mode ──────────────────────────────────────────────────────

    private void OnFollowItemSelected(long index)
    {
        if (_globalData?.Settings == null || ComponentDefaultsUi.ShouldSkipEdit(_isSyncingUi, _historyManager)) return;

        int selectedValue = _followOption.GetItemMetadata((int)index).AsInt32();
        var follow = (FollowType)selectedValue;
        if (_globalData.Settings.CueDefaultFollow == follow)
        {
            UpdateFollowResetButton();
            return;
        }

        RecordCueDefaultsHistory("Change default continue mode");
        _globalData.Settings.CueDefaultFollow = follow;
        UpdateFollowResetButton();
    }

    private void OnFollowResetPressed()
    {
        if (_isSyncingUi || _globalData?.Settings == null) return;
        if (_globalData.Settings.CueDefaultFollow == AppSettings.SystemDefaultCueFollow)
        {
            SyncSettings();
            return;
        }

        RecordCueDefaultsHistory("Reset default continue mode");
        _globalData.Settings.CueDefaultFollow = AppSettings.SystemDefaultCueFollow;
        SyncSettings();
    }

    private void UpdateFollowResetButton()
    {
        if (_followResetButton == null || _globalData?.Settings == null) return;
        bool atDefault = _globalData.Settings.CueDefaultFollow == AppSettings.SystemDefaultCueFollow;
        _followResetButton.Visible = !atDefault;
        if (!atDefault)
            _followResetButton.TooltipText =
                UiLocalizer.ResetDefaultTip(FollowLabel(AppSettings.SystemDefaultCueFollow));
    }

    // ── Colour ─────────────────────────────────────────────────────────────

    private void OnColorPopupClosed()
    {
        if (_isSyncingUi || _globalData?.Settings == null || _colorPicker == null) return;
        if (_historyManager?.IsRestoring == true) return;

        if (_globalData.Settings.CueDefaultColor.IsEqualApprox(_colorPicker.Color))
        {
            UpdateColorResetButton();
            return;
        }

        RecordCueDefaultsHistory("Change default cue colour");
        _globalData.Settings.CueDefaultColor = _colorPicker.Color;
        UpdateColorResetButton();
    }

    private void OnColorResetPressed()
    {
        if (_isSyncingUi || _globalData?.Settings == null) return;
        if (_globalData.Settings.CueDefaultColor.IsEqualApprox(AppSettings.SystemDefaultCueColor))
        {
            SyncSettings();
            return;
        }

        RecordCueDefaultsHistory("Reset default cue colour");
        _globalData.Settings.CueDefaultColor = AppSettings.SystemDefaultCueColor;
        SyncSettings();
    }

    private void UpdateColorResetButton()
    {
        if (_colorResetButton == null || _globalData?.Settings == null) return;
        bool atDefault = _globalData.Settings.CueDefaultColor.IsEqualApprox(AppSettings.SystemDefaultCueColor);
        _colorResetButton.Visible = !atDefault;
        if (!atDefault)
            _colorResetButton.TooltipText = UiLocalizer.T("Reset to default: black");
    }

    // ── Armed ──────────────────────────────────────────────────────────────

    private void OnArmedToggled(bool pressed)
    {
        if (_globalData?.Settings == null || ComponentDefaultsUi.ShouldSkipEdit(_isSyncingUi, _historyManager)) return;

        if (_globalData.Settings.CueDefaultArmed == pressed)
        {
            UpdateArmedResetButton();
            return;
        }

        RecordCueDefaultsHistory(pressed ? "Default arm cues" : "Default disarm cues");
        _globalData.Settings.CueDefaultArmed = pressed;
        UpdateArmedResetButton();
    }

    private void OnArmedResetPressed()
    {
        if (_isSyncingUi || _globalData?.Settings == null) return;
        if (_globalData.Settings.CueDefaultArmed == AppSettings.SystemDefaultCueArmed)
        {
            SyncSettings();
            return;
        }

        RecordCueDefaultsHistory("Reset default armed");
        _globalData.Settings.CueDefaultArmed = AppSettings.SystemDefaultCueArmed;
        SyncSettings();
    }

    private void UpdateArmedResetButton()
    {
        if (_armedResetButton == null || _globalData?.Settings == null) return;
        bool atDefault = _globalData.Settings.CueDefaultArmed == AppSettings.SystemDefaultCueArmed;
        _armedResetButton.Visible = !atDefault;
        if (!atDefault)
        {
            string text = AppSettings.SystemDefaultCueArmed ? "Armed" : "Disarmed";
            _armedResetButton.TooltipText = UiLocalizer.ResetDefaultTip(text);
        }
    }

    // ── Skip if disarmed ───────────────────────────────────────────────────

    private void OnSkipIfDisarmedToggled(bool pressed)
    {
        if (_globalData?.Settings == null || ComponentDefaultsUi.ShouldSkipEdit(_isSyncingUi, _historyManager)) return;

        if (_globalData.Settings.CueDefaultSkipIfDisarmed == pressed)
        {
            UpdateSkipIfDisarmedResetButton();
            return;
        }

        RecordCueDefaultsHistory(pressed
            ? "Enable default skip if disarmed"
            : "Disable default skip if disarmed");
        _globalData.Settings.CueDefaultSkipIfDisarmed = pressed;
        UpdateSkipIfDisarmedResetButton();
    }

    private void OnSkipIfDisarmedResetPressed()
    {
        if (_isSyncingUi || _globalData?.Settings == null) return;
        if (_globalData.Settings.CueDefaultSkipIfDisarmed == AppSettings.SystemDefaultCueSkipIfDisarmed)
        {
            SyncSettings();
            return;
        }

        RecordCueDefaultsHistory("Reset default skip if disarmed");
        _globalData.Settings.CueDefaultSkipIfDisarmed = AppSettings.SystemDefaultCueSkipIfDisarmed;
        SyncSettings();
    }

    private void OnOnlyOneActiveToggled(bool pressed)
    {
        if (_isSyncingUi || _globalData?.Settings == null || _historyManager?.IsRestoring == true)
            return;
        if (_globalData.Settings.CueDefaultOnlyOneActiveInstance == pressed)
        {
            UpdateOnlyOneActiveResetButton();
            return;
        }

        RecordCueDefaultsHistory(pressed
            ? "Default only one active instance"
            : "Default allow multiple active instances");
        _globalData.Settings.CueDefaultOnlyOneActiveInstance = pressed;
        UpdateOnlyOneActiveResetButton();
    }

    private void OnOnlyOneActiveResetPressed()
    {
        if (_isSyncingUi || _globalData?.Settings == null || _historyManager?.IsRestoring == true)
            return;
        if (_globalData.Settings.CueDefaultOnlyOneActiveInstance
            == AppSettings.SystemDefaultCueOnlyOneActiveInstance)
        {
            SyncSettings();
            return;
        }

        RecordCueDefaultsHistory("Reset default only one active instance");
        _globalData.Settings.CueDefaultOnlyOneActiveInstance =
            AppSettings.SystemDefaultCueOnlyOneActiveInstance;
        SyncSettings();
    }

    private void UpdateOnlyOneActiveResetButton()
    {
        if (_onlyOneActiveResetButton == null || _globalData?.Settings == null) return;
        bool atDefault = _globalData.Settings.CueDefaultOnlyOneActiveInstance
                         == AppSettings.SystemDefaultCueOnlyOneActiveInstance;
        _onlyOneActiveResetButton.Visible = !atDefault;
        if (!atDefault)
        {
            string text = AppSettings.SystemDefaultCueOnlyOneActiveInstance ? "On" : "Off";
            _onlyOneActiveResetButton.TooltipText = UiLocalizer.ResetDefaultTip(text);
        }
    }

    private void UpdateSkipIfDisarmedResetButton()
    {
        if (_skipIfDisarmedResetButton == null || _globalData?.Settings == null) return;
        bool atDefault = _globalData.Settings.CueDefaultSkipIfDisarmed
                         == AppSettings.SystemDefaultCueSkipIfDisarmed;
        _skipIfDisarmedResetButton.Visible = !atDefault;
        if (!atDefault)
        {
            string text = AppSettings.SystemDefaultCueSkipIfDisarmed ? "On" : "Off";
            _skipIfDisarmedResetButton.TooltipText = UiLocalizer.ResetDefaultTip(text);
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
