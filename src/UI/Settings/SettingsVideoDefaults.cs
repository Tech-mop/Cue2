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
/// Settings panel for show-scoped video component defaults (target layer, embedded audio output,
/// layout, opacity, loop, still-image hold, fades). Applied when a new video component is created.
/// </summary>
/// <remarks>
/// Each control shows a refresh button when not at its system default.
/// Stored with the showfile via <see cref="AppSettings"/> under the <c>VideoDefaults</c> key.
/// </remarks>
public partial class SettingsVideoDefaults : ScrollContainer
{
    private GlobalSignals _globalSignals;
    private GlobalData _globalData;
    private HistoryManager _historyManager;
    private AudioDevices _audioDevices;

    private OptionButton _targetLayerOption;
    private Button _targetLayerResetButton;

    private OptionButton _audioOutputOption;
    private Button _audioOutputResetButton;

    private OptionButton _expandOption;
    private Button _expandResetButton;

    private OptionButton _stretchOption;
    private Button _stretchResetButton;

    private LineEdit _opacityInput;
    private Button _opacityResetButton;

    private CheckBox _loopCheckBox;
    private Button _loopResetButton;

    private LineEdit _playCountInput;
    private Button _playCountResetButton;

    private CheckBox _useAudioCheckBox;
    private Button _useAudioResetButton;

    private LineEdit _audioVolumeInput;
    private Button _audioVolumeResetButton;

    private HSlider _panSlider;
    private LineEdit _panInput;
    private Button _panResetButton;

    private LineEdit _imageDurationInput;
    private Button _imageDurationResetButton;

    private LineEdit _fadeInInput;
    private Button _fadeInResetButton;

    private LineEdit _fadeOutInput;
    private Button _fadeOutResetButton;

    private bool _isSyncingUi;
    private bool _isUpdatingPanUi;

    /// <inheritdoc />
    public override void _Ready()
    {
        GD.Print("SettingsVideoDefaults:_Ready - Video Defaults panel init");

        _globalSignals = GetNode<GlobalSignals>("/root/GlobalSignals");
        _globalData = GetNode<GlobalData>("/root/GlobalData");
        _historyManager = _globalData?.HistoryManager;
        _audioDevices = GetNodeOrNull<AudioDevices>("/root/AudioDevices");

        _targetLayerOption = GetNodeOrNull<OptionButton>("%TargetLayerOption");
        _targetLayerResetButton = GetNodeOrNull<Button>("%TargetLayerResetButton");
        _audioOutputOption = GetNodeOrNull<OptionButton>("%AudioOutputOption");
        _audioOutputResetButton = GetNodeOrNull<Button>("%AudioOutputResetButton");
        _expandOption = GetNode<OptionButton>("%ExpandOption");
        _expandResetButton = GetNode<Button>("%ExpandResetButton");
        _stretchOption = GetNode<OptionButton>("%StretchOption");
        _stretchResetButton = GetNode<Button>("%StretchResetButton");
        _opacityInput = GetNode<LineEdit>("%OpacityInput");
        _opacityResetButton = GetNode<Button>("%OpacityResetButton");
        _loopCheckBox = GetNode<CheckBox>("%LoopCheckBox");
        _loopResetButton = GetNode<Button>("%LoopResetButton");
        _playCountInput = GetNode<LineEdit>("%PlayCountInput");
        _playCountResetButton = GetNode<Button>("%PlayCountResetButton");
        _useAudioCheckBox = GetNode<CheckBox>("%UseAudioCheckBox");
        _useAudioResetButton = GetNode<Button>("%UseAudioResetButton");
        _audioVolumeInput = GetNode<LineEdit>("%AudioVolumeInput");
        _audioVolumeResetButton = GetNode<Button>("%AudioVolumeResetButton");
        _panSlider = GetNode<HSlider>("%PanSlider");
        _panInput = GetNode<LineEdit>("%PanInput");
        _panResetButton = GetNode<Button>("%PanResetButton");
        _imageDurationInput = GetNode<LineEdit>("%ImageDurationInput");
        _imageDurationResetButton = GetNode<Button>("%ImageDurationResetButton");
        _fadeInInput = GetNode<LineEdit>("%FadeInInput");
        _fadeInResetButton = GetNode<Button>("%FadeInResetButton");
        _fadeOutInput = GetNode<LineEdit>("%FadeOutInput");
        _fadeOutResetButton = GetNode<Button>("%FadeOutResetButton");

        ComponentDefaultsUi.SetupResetButton(this, _targetLayerResetButton, OnTargetLayerResetPressed);
        ComponentDefaultsUi.SetupResetButton(this, _audioOutputResetButton, OnAudioOutputResetPressed);
        ComponentDefaultsUi.SetupResetButton(this, _expandResetButton, OnExpandResetPressed);
        ComponentDefaultsUi.SetupResetButton(this, _stretchResetButton, OnStretchResetPressed);
        ComponentDefaultsUi.SetupResetButton(this, _opacityResetButton, OnOpacityResetPressed);
        ComponentDefaultsUi.SetupResetButton(this, _loopResetButton, OnLoopResetPressed);
        ComponentDefaultsUi.SetupResetButton(this, _playCountResetButton, OnPlayCountResetPressed);
        ComponentDefaultsUi.SetupResetButton(this, _useAudioResetButton, OnUseAudioResetPressed);
        ComponentDefaultsUi.SetupResetButton(this, _audioVolumeResetButton, OnAudioVolumeResetPressed);
        ComponentDefaultsUi.SetupResetButton(this, _panResetButton, OnPanResetPressed);
        ComponentDefaultsUi.SetupResetButton(this, _imageDurationResetButton, OnImageDurationResetPressed);
        ComponentDefaultsUi.SetupResetButton(this, _fadeInResetButton, OnFadeInResetPressed);
        ComponentDefaultsUi.SetupResetButton(this, _fadeOutResetButton, OnFadeOutResetPressed);

        EnsureTextureOptions();

        if (_targetLayerOption != null)
            _targetLayerOption.ItemSelected += OnTargetLayerSelected;
        if (_audioOutputOption != null)
            _audioOutputOption.ItemSelected += OnAudioOutputSelected;
        _expandOption.ItemSelected += OnExpandSelected;
        _stretchOption.ItemSelected += OnStretchSelected;
        _opacityInput.TextSubmitted += OnOpacitySubmitted;
        _opacityInput.FocusExited += OnOpacityFocusExited;
        _loopCheckBox.Toggled += OnLoopToggled;
        _playCountInput.TextSubmitted += OnPlayCountSubmitted;
        _playCountInput.FocusExited += OnPlayCountFocusExited;
        _useAudioCheckBox.Toggled += OnUseAudioToggled;
        _audioVolumeInput.TextSubmitted += OnAudioVolumeSubmitted;
        _audioVolumeInput.FocusExited += OnAudioVolumeFocusExited;
        LineEditDbDragSlider.EnableVolume(_audioVolumeInput);
        _panSlider.ValueChanged += OnPanSliderChanged;
        _panSlider.DragEnded += OnPanDragEnded;
        _panInput.TextSubmitted += OnPanSubmitted;
        _panInput.FocusExited += OnPanFocusExited;
        _imageDurationInput.TextSubmitted += OnImageDurationSubmitted;
        _imageDurationInput.FocusExited += OnImageDurationFocusExited;
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
        // Rebuild layer list only (keep other fields via SyncSettings).
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

    private void EnsureTextureOptions()
    {
        if (_expandOption != null && _expandOption.ItemCount == 0)
        {
            ComponentDefaultsUi.AddOptionItem(_expandOption, "Keep Size", (int)TextureRect.ExpandModeEnum.KeepSize);
            ComponentDefaultsUi.AddOptionItem(_expandOption, "Ignore Size", (int)TextureRect.ExpandModeEnum.IgnoreSize);
            ComponentDefaultsUi.AddOptionItem(_expandOption, "Fit Width Proportional",
                (int)TextureRect.ExpandModeEnum.FitWidthProportional);
            ComponentDefaultsUi.AddOptionItem(_expandOption, "Fit Height Proportional",
                (int)TextureRect.ExpandModeEnum.FitHeightProportional);
        }

        if (_stretchOption != null && _stretchOption.ItemCount == 0)
        {
            ComponentDefaultsUi.AddOptionItem(_stretchOption, "Scale", (int)TextureRect.StretchModeEnum.Scale);
            ComponentDefaultsUi.AddOptionItem(_stretchOption, "Tile", (int)TextureRect.StretchModeEnum.Tile);
            ComponentDefaultsUi.AddOptionItem(_stretchOption, "Keep", (int)TextureRect.StretchModeEnum.Keep);
            ComponentDefaultsUi.AddOptionItem(_stretchOption, "Keep Centered", (int)TextureRect.StretchModeEnum.KeepCentered);
            ComponentDefaultsUi.AddOptionItem(_stretchOption, "Keep Aspect", (int)TextureRect.StretchModeEnum.KeepAspect);
            ComponentDefaultsUi.AddOptionItem(_stretchOption, "Keep Aspect Centered",
                (int)TextureRect.StretchModeEnum.KeepAspectCentered);
            ComponentDefaultsUi.AddOptionItem(_stretchOption, "Keep Aspect Covered",
                (int)TextureRect.StretchModeEnum.KeepAspectCovered);
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
                _targetLayerOption, s.VideoDefaultTargetLayerMode, s.VideoDefaultTargetLayerId);
            ComponentDefaultsUi.PopulateAudioOutputOption(
                _audioOutputOption, s, _audioDevices,
                s.VideoDefaultOutputMode, s.VideoDefaultPatchId, s.VideoDefaultDirectOutput);

            EnsureTextureOptions();
            ComponentDefaultsUi.SelectOptionByMetadata(_expandOption, (int)s.VideoDefaultExpandMode);
            ComponentDefaultsUi.SelectOptionByMetadata(_stretchOption, (int)s.VideoDefaultStretchMode);

            if (_opacityInput != null)
            {
                float pct = Mathf.Clamp(s.VideoDefaultOpacity, 0f, 1f) * 100f;
                _opacityInput.Text = pct.ToString("0.#");
            }

            _loopCheckBox?.SetPressedNoSignal(s.VideoDefaultLoop);
            if (_playCountInput != null)
                _playCountInput.Text = Math.Max(1, s.VideoDefaultPlayCount).ToString();
            _useAudioCheckBox?.SetPressedNoSignal(s.VideoDefaultUseAudio);

            if (_audioVolumeInput != null)
                _audioVolumeInput.Text = UiUtilities.FormatComponentVolumeDb(s.VideoDefaultAudioVolume);

            _isUpdatingPanUi = true;
            try
            {
                float pan = Mathf.Clamp(s.VideoDefaultPan, -1f, 1f);
                _panSlider?.SetValueNoSignal(Mathf.Round(pan * 100f));
                if (_panInput != null)
                    _panInput.Text = UiUtilities.FormatPan(pan);
            }
            finally
            {
                _isUpdatingPanUi = false;
            }

            if (_imageDurationInput != null)
            {
                if (s.VideoDefaultImageDuration <= 0)
                    _imageDurationInput.Text = UiLocalizer.T("Until stopped");
                else
                    _imageDurationInput.Text = UiUtilities.FormatTime(s.VideoDefaultImageDuration);
            }

            if (_fadeInInput != null)
                _fadeInInput.Text = UiUtilities.FormatTime(s.VideoDefaultFadeIn);
            if (_fadeOutInput != null)
                _fadeOutInput.Text = UiUtilities.FormatTime(s.VideoDefaultFadeOut);

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
        UpdateAudioOutputResetButton();
        UpdateExpandResetButton();
        UpdateStretchResetButton();
        UpdateOpacityResetButton();
        UpdateLoopResetButton();
        UpdatePlayCountResetButton();
        UpdateUseAudioResetButton();
        UpdateAudioVolumeResetButton();
        UpdatePanResetButton();
        UpdateImageDurationResetButton();
        UpdateFadeInResetButton();
        UpdateFadeOutResetButton();
    }

    private void RecordHistory(string description, string coalesceKey = null)
    {
        ComponentDefaultsUi.RecordDefaultsChange(_historyManager, description, "VideoDefaults", coalesceKey);
    }

    // ── Target layer / audio output ────────────────────────────────────────

    private void OnTargetLayerSelected(long index)
    {
        if (_isSyncingUi || _globalData?.Settings == null || _targetLayerOption == null) return;
        if (_historyManager?.IsRestoring == true) return;

        ComponentDefaultsUi.ReadTargetLayerSelection(
            _targetLayerOption, out var mode, out int layerId);
        var s = _globalData.Settings;
        if (s.VideoDefaultTargetLayerMode == mode && s.VideoDefaultTargetLayerId == layerId)
        {
            UpdateTargetLayerResetButton();
            return;
        }

        RecordHistory("Change default video target layer");
        s.VideoDefaultTargetLayerMode = mode;
        s.VideoDefaultTargetLayerId = layerId;
        UpdateTargetLayerResetButton();
    }

    private void OnTargetLayerResetPressed()
    {
        if (_isSyncingUi || _globalData?.Settings == null) return;
        var s = _globalData.Settings;
        if (ComponentDefaultsUi.IsTargetLayerAtSystem(
                s.VideoDefaultTargetLayerMode, s.VideoDefaultTargetLayerId))
        {
            SyncSettings();
            return;
        }

        RecordHistory("Reset default video target layer");
        s.VideoDefaultTargetLayerMode = AppSettings.SystemDefaultVideoTargetLayerMode;
        s.VideoDefaultTargetLayerId = -1;
        SyncSettings();
    }

    private void UpdateTargetLayerResetButton()
    {
        if (_targetLayerResetButton == null || _globalData?.Settings == null) return;
        var s = _globalData.Settings;
        bool atDefault = ComponentDefaultsUi.IsTargetLayerAtSystem(
            s.VideoDefaultTargetLayerMode, s.VideoDefaultTargetLayerId);
        _targetLayerResetButton.Visible = !atDefault;
        if (!atDefault)
            _targetLayerResetButton.TooltipText = UiLocalizer.T("Reset to default: First available layer");
    }

    private void OnAudioOutputSelected(long index)
    {
        if (_isSyncingUi || _globalData?.Settings == null || _audioOutputOption == null) return;
        if (_historyManager?.IsRestoring == true) return;

        ComponentDefaultsUi.ReadAudioOutputSelection(
            _audioOutputOption, out var mode, out int patchId, out string direct);
        var s = _globalData.Settings;
        if (s.VideoDefaultOutputMode == mode
            && s.VideoDefaultPatchId == patchId
            && string.Equals(s.VideoDefaultDirectOutput ?? string.Empty, direct ?? string.Empty,
                StringComparison.Ordinal))
        {
            UpdateAudioOutputResetButton();
            return;
        }

        RecordHistory("Change default video audio output");
        s.VideoDefaultOutputMode = mode;
        s.VideoDefaultPatchId = patchId;
        s.VideoDefaultDirectOutput = direct ?? string.Empty;
        UpdateAudioOutputResetButton();
    }

    private void OnAudioOutputResetPressed()
    {
        if (_isSyncingUi || _globalData?.Settings == null) return;
        var s = _globalData.Settings;
        if (ComponentDefaultsUi.IsAudioOutputAtSystem(
                s.VideoDefaultOutputMode, s.VideoDefaultPatchId, s.VideoDefaultDirectOutput))
        {
            SyncSettings();
            return;
        }

        RecordHistory("Reset default video audio output");
        s.VideoDefaultOutputMode = AppSettings.SystemDefaultVideoOutputMode;
        s.VideoDefaultPatchId = -1;
        s.VideoDefaultDirectOutput = string.Empty;
        SyncSettings();
    }

    private void UpdateAudioOutputResetButton()
    {
        if (_audioOutputResetButton == null || _globalData?.Settings == null) return;
        var s = _globalData.Settings;
        bool atDefault = ComponentDefaultsUi.IsAudioOutputAtSystem(
            s.VideoDefaultOutputMode, s.VideoDefaultPatchId, s.VideoDefaultDirectOutput);
        _audioOutputResetButton.Visible = !atDefault;
        if (!atDefault)
            _audioOutputResetButton.TooltipText = UiLocalizer.T("Reset to default: Preferred (Default Patch)");
    }

    // ── Expand / Stretch ───────────────────────────────────────────────────

    private void OnExpandSelected(long index)
    {
        if (_globalData?.Settings == null || ComponentDefaultsUi.ShouldSkipEdit(_isSyncingUi, _historyManager)) return;

        int mode = _expandOption.GetItemMetadata((int)index).AsInt32();
        var expand = (TextureRect.ExpandModeEnum)mode;
        if (_globalData.Settings.VideoDefaultExpandMode == expand)
        {
            UpdateExpandResetButton();
            return;
        }

        RecordHistory("Change default video expand mode");
        _globalData.Settings.VideoDefaultExpandMode = expand;
        UpdateExpandResetButton();
    }

    private void OnExpandResetPressed()
    {
        if (_isSyncingUi || _globalData?.Settings == null) return;
        if (_globalData.Settings.VideoDefaultExpandMode == AppSettings.SystemDefaultVideoExpandMode)
        {
            SyncSettings();
            return;
        }

        RecordHistory("Reset default video expand mode");
        _globalData.Settings.VideoDefaultExpandMode = AppSettings.SystemDefaultVideoExpandMode;
        SyncSettings();
    }

    private void UpdateExpandResetButton()
    {
        if (_expandResetButton == null || _globalData?.Settings == null) return;
        bool atDefault = _globalData.Settings.VideoDefaultExpandMode
                         == AppSettings.SystemDefaultVideoExpandMode;
        _expandResetButton.Visible = !atDefault;
        if (!atDefault)
            _expandResetButton.TooltipText = UiLocalizer.T("Reset to default: Ignore Size");
    }

    private void OnStretchSelected(long index)
    {
        if (_globalData?.Settings == null || ComponentDefaultsUi.ShouldSkipEdit(_isSyncingUi, _historyManager)) return;

        int mode = _stretchOption.GetItemMetadata((int)index).AsInt32();
        var stretch = (TextureRect.StretchModeEnum)mode;
        if (_globalData.Settings.VideoDefaultStretchMode == stretch)
        {
            UpdateStretchResetButton();
            return;
        }

        RecordHistory("Change default video stretch mode");
        _globalData.Settings.VideoDefaultStretchMode = stretch;
        UpdateStretchResetButton();
    }

    private void OnStretchResetPressed()
    {
        if (_isSyncingUi || _globalData?.Settings == null) return;
        if (_globalData.Settings.VideoDefaultStretchMode == AppSettings.SystemDefaultVideoStretchMode)
        {
            SyncSettings();
            return;
        }

        RecordHistory("Reset default video stretch mode");
        _globalData.Settings.VideoDefaultStretchMode = AppSettings.SystemDefaultVideoStretchMode;
        SyncSettings();
    }

    private void UpdateStretchResetButton()
    {
        if (_stretchResetButton == null || _globalData?.Settings == null) return;
        bool atDefault = _globalData.Settings.VideoDefaultStretchMode
                         == AppSettings.SystemDefaultVideoStretchMode;
        _stretchResetButton.Visible = !atDefault;
        if (!atDefault)
            _stretchResetButton.TooltipText = UiLocalizer.T("Reset to default: Keep Aspect Centered");
    }

    // ── Opacity ────────────────────────────────────────────────────────────

    private void OnOpacitySubmitted(string text) => CommitOpacity(text);

    private void OnOpacityFocusExited()
    {
        if (_isSyncingUi || _opacityInput == null) return;
        CommitOpacity(_opacityInput.Text);
    }

    private void CommitOpacity(string text)
    {
        if (_globalData?.Settings == null || ComponentDefaultsUi.ShouldSkipEdit(_isSyncingUi, _historyManager)) return;

        string cleaned = (text ?? string.Empty).Replace("%", "").Trim();
        if (!float.TryParse(cleaned, out float pct))
        {
            _globalSignals?.EmitSignal(nameof(GlobalSignals.Log),
                $"Invalid default opacity: {text}", 1);
            float cur = Mathf.Clamp(_globalData.Settings.VideoDefaultOpacity, 0f, 1f) * 100f;
            _opacityInput.Text = cur.ToString("0.#");
            return;
        }

        pct = Mathf.Clamp(pct, 0f, 100f);
        float opacity = pct / 100f;
        _opacityInput.Text = pct.ToString("0.#");

        if (Math.Abs(_globalData.Settings.VideoDefaultOpacity - opacity) < 1e-4f)
        {
            UpdateOpacityResetButton();
            return;
        }

        RecordHistory("Change default video opacity");
        _globalData.Settings.VideoDefaultOpacity = opacity;
        UpdateOpacityResetButton();
    }

    private void OnOpacityResetPressed()
    {
        if (_isSyncingUi || _globalData?.Settings == null) return;
        if (Math.Abs(_globalData.Settings.VideoDefaultOpacity
                      - AppSettings.SystemDefaultVideoOpacity) < 1e-4f)
        {
            SyncSettings();
            return;
        }

        RecordHistory("Reset default video opacity");
        _globalData.Settings.VideoDefaultOpacity = AppSettings.SystemDefaultVideoOpacity;
        SyncSettings();
    }

    private void UpdateOpacityResetButton()
    {
        if (_opacityResetButton == null || _globalData?.Settings == null) return;
        bool atDefault = Math.Abs(_globalData.Settings.VideoDefaultOpacity
                                  - AppSettings.SystemDefaultVideoOpacity) < 1e-4f;
        _opacityResetButton.Visible = !atDefault;
        if (!atDefault)
            _opacityResetButton.TooltipText = UiLocalizer.T("Reset to default: 100%");
    }

    // ── Loop / play count ──────────────────────────────────────────────────

    private void OnLoopToggled(bool pressed)
    {
        if (_globalData?.Settings == null || ComponentDefaultsUi.ShouldSkipEdit(_isSyncingUi, _historyManager)) return;
        if (_globalData.Settings.VideoDefaultLoop == pressed)
        {
            UpdateLoopResetButton();
            return;
        }

        RecordHistory(pressed ? "Enable default video loop" : "Disable default video loop");
        _globalData.Settings.VideoDefaultLoop = pressed;
        UpdateLoopResetButton();
    }

    private void OnLoopResetPressed()
    {
        if (_isSyncingUi || _globalData?.Settings == null) return;
        if (_globalData.Settings.VideoDefaultLoop == AppSettings.SystemDefaultVideoLoop)
        {
            SyncSettings();
            return;
        }

        RecordHistory("Reset default video loop");
        _globalData.Settings.VideoDefaultLoop = AppSettings.SystemDefaultVideoLoop;
        SyncSettings();
    }

    private void UpdateLoopResetButton()
    {
        if (_loopResetButton == null || _globalData?.Settings == null) return;
        bool atDefault = _globalData.Settings.VideoDefaultLoop == AppSettings.SystemDefaultVideoLoop;
        _loopResetButton.Visible = !atDefault;
        if (!atDefault)
            _loopResetButton.TooltipText =
                UiLocalizer.ResetDefaultTip((AppSettings.SystemDefaultVideoLoop ? "On" : "Off"));
    }

    private void OnPlayCountSubmitted(string text) => CommitPlayCount(text);

    private void OnPlayCountFocusExited()
    {
        if (_isSyncingUi || _playCountInput == null) return;
        CommitPlayCount(_playCountInput.Text);
    }

    private void CommitPlayCount(string text)
    {
        if (_globalData?.Settings == null || ComponentDefaultsUi.ShouldSkipEdit(_isSyncingUi, _historyManager)) return;

        if (!int.TryParse(text.Trim(), out int count) || count < 1)
        {
            _globalSignals?.EmitSignal(nameof(GlobalSignals.Log),
                $"Invalid default play count: {text}", 1);
            _playCountInput.Text = Math.Max(1, _globalData.Settings.VideoDefaultPlayCount).ToString();
            return;
        }

        _playCountInput.Text = count.ToString();
        if (_globalData.Settings.VideoDefaultPlayCount == count)
        {
            UpdatePlayCountResetButton();
            return;
        }

        RecordHistory("Change default video play count");
        _globalData.Settings.VideoDefaultPlayCount = count;
        UpdatePlayCountResetButton();
    }

    private void OnPlayCountResetPressed()
    {
        if (_isSyncingUi || _globalData?.Settings == null) return;
        if (_globalData.Settings.VideoDefaultPlayCount == AppSettings.SystemDefaultVideoPlayCount)
        {
            SyncSettings();
            return;
        }

        RecordHistory("Reset default video play count");
        _globalData.Settings.VideoDefaultPlayCount = AppSettings.SystemDefaultVideoPlayCount;
        SyncSettings();
    }

    private void UpdatePlayCountResetButton()
    {
        if (_playCountResetButton == null || _globalData?.Settings == null) return;
        bool atDefault = _globalData.Settings.VideoDefaultPlayCount
                         == AppSettings.SystemDefaultVideoPlayCount;
        _playCountResetButton.Visible = !atDefault;
        if (!atDefault)
            _playCountResetButton.TooltipText =
                UiLocalizer.ResetDefaultTip(AppSettings.SystemDefaultVideoPlayCount);
    }

    // ── Use audio / volume / pan ───────────────────────────────────────────

    private void OnUseAudioToggled(bool pressed)
    {
        if (_globalData?.Settings == null || ComponentDefaultsUi.ShouldSkipEdit(_isSyncingUi, _historyManager)) return;
        if (_globalData.Settings.VideoDefaultUseAudio == pressed)
        {
            UpdateUseAudioResetButton();
            return;
        }

        RecordHistory(pressed ? "Enable default video audio" : "Disable default video audio");
        _globalData.Settings.VideoDefaultUseAudio = pressed;
        UpdateUseAudioResetButton();
    }

    private void OnUseAudioResetPressed()
    {
        if (_isSyncingUi || _globalData?.Settings == null) return;
        if (_globalData.Settings.VideoDefaultUseAudio == AppSettings.SystemDefaultVideoUseAudio)
        {
            SyncSettings();
            return;
        }

        RecordHistory("Reset default video use-audio");
        _globalData.Settings.VideoDefaultUseAudio = AppSettings.SystemDefaultVideoUseAudio;
        SyncSettings();
    }

    private void UpdateUseAudioResetButton()
    {
        if (_useAudioResetButton == null || _globalData?.Settings == null) return;
        bool atDefault = _globalData.Settings.VideoDefaultUseAudio
                         == AppSettings.SystemDefaultVideoUseAudio;
        _useAudioResetButton.Visible = !atDefault;
        if (!atDefault)
            _useAudioResetButton.TooltipText =
                UiLocalizer.ResetDefaultTip((AppSettings.SystemDefaultVideoUseAudio ? "On" : "Off"));
    }

    private void OnAudioVolumeSubmitted(string text) => CommitAudioVolume(text);

    private void OnAudioVolumeFocusExited()
    {
        if (_isSyncingUi || _audioVolumeInput == null) return;
        CommitAudioVolume(_audioVolumeInput.Text);
    }

    private void CommitAudioVolume(string text)
    {
        if (_globalData?.Settings == null || ComponentDefaultsUi.ShouldSkipEdit(_isSyncingUi, _historyManager)) return;

        try
        {
            if (!float.TryParse(text.Replace("dB", "").Trim(), out var dbValue))
            {
                _globalSignals?.EmitSignal(nameof(GlobalSignals.Log),
                    $"Invalid default audio volume: {text}", 1);
                _audioVolumeInput.Text =
                    UiUtilities.FormatComponentVolumeDb(_globalData.Settings.VideoDefaultAudioVolume);
                return;
            }

            // Digital gain allowed (−60…+12 dB).
            dbValue = Mathf.Clamp(dbValue, UiUtilities.MinVolumeDb, UiUtilities.MaxComponentGainDb);
            float volume = UiUtilities.DbToLinear(dbValue);
            _audioVolumeInput.Text = UiUtilities.FormatComponentVolumeDb(volume);

            if (Math.Abs(_globalData.Settings.VideoDefaultAudioVolume - volume) < 1e-6f)
            {
                UpdateAudioVolumeResetButton();
                return;
            }

            RecordHistory("Change default video audio volume");
            _globalData.Settings.VideoDefaultAudioVolume = volume;
            UpdateAudioVolumeResetButton();
        }
        catch (Exception ex)
        {
            _globalSignals?.EmitSignal(nameof(GlobalSignals.Log),
                $"Error parsing default audio volume: {ex.Message}", 2);
        }
    }

    private void OnAudioVolumeResetPressed()
    {
        if (_isSyncingUi || _globalData?.Settings == null) return;
        if (Math.Abs(_globalData.Settings.VideoDefaultAudioVolume
                      - AppSettings.SystemDefaultVideoAudioVolume) < 1e-6f)
        {
            SyncSettings();
            return;
        }

        RecordHistory("Reset default video audio volume");
        _globalData.Settings.VideoDefaultAudioVolume = AppSettings.SystemDefaultVideoAudioVolume;
        SyncSettings();
    }

    private void UpdateAudioVolumeResetButton()
    {
        if (_audioVolumeResetButton == null || _globalData?.Settings == null) return;
        bool atDefault = Math.Abs(_globalData.Settings.VideoDefaultAudioVolume
                                  - AppSettings.SystemDefaultVideoAudioVolume) < 1e-6f;
        _audioVolumeResetButton.Visible = !atDefault;
        if (!atDefault)
            _audioVolumeResetButton.TooltipText =
                $"Reset to default: {UiUtilities.LinearToDb(AppSettings.SystemDefaultVideoAudioVolume)}dB";
    }

    private void OnPanSliderChanged(double value)
    {
        if (_isUpdatingPanUi || _globalData?.Settings == null || ComponentDefaultsUi.ShouldSkipEdit(_isSyncingUi, _historyManager)) return;

        float pan = Mathf.Clamp((float)value / 100f, -1f, 1f);
        if (Math.Abs(_globalData.Settings.VideoDefaultPan - pan) < 1e-6f)
            return;

        RecordHistory("Change default video pan", "settings:video-defaults:pan");
        _globalData.Settings.VideoDefaultPan = pan;

        _isUpdatingPanUi = true;
        try
        {
            if (_panInput != null)
                _panInput.Text = UiUtilities.FormatPan(pan);
        }
        finally
        {
            _isUpdatingPanUi = false;
        }

        UpdatePanResetButton();
    }

    private void OnPanDragEnded(bool valueChanged)
    {
        if (!valueChanged) return;
        ComponentDefaultsUi.EndDefaultsCoalesce(_historyManager, "settings:video-defaults:pan");
    }

    private void OnPanSubmitted(string text) => CommitPan(text);

    private void OnPanFocusExited()
    {
        if (_isSyncingUi || _panInput == null) return;
        CommitPan(_panInput.Text);
    }

    private void CommitPan(string text)
    {
        if (_globalData?.Settings == null || ComponentDefaultsUi.ShouldSkipEdit(_isSyncingUi, _historyManager)) return;

        if (!UiUtilities.TryParsePan(text, out float pan))
        {
            _globalSignals?.EmitSignal(nameof(GlobalSignals.Log),
                $"Invalid default pan: {text}", 1);
            _panInput.Text = UiUtilities.FormatPan(_globalData.Settings.VideoDefaultPan);
            return;
        }

        pan = Mathf.Clamp(pan, -1f, 1f);
        _panInput.Text = UiUtilities.FormatPan(pan);
        _isUpdatingPanUi = true;
        try
        {
            _panSlider?.SetValueNoSignal(Mathf.Round(pan * 100f));
        }
        finally
        {
            _isUpdatingPanUi = false;
        }

        if (Math.Abs(_globalData.Settings.VideoDefaultPan - pan) < 1e-6f)
        {
            UpdatePanResetButton();
            return;
        }

        RecordHistory("Change default video pan");
        _globalData.Settings.VideoDefaultPan = pan;
        UpdatePanResetButton();
    }

    private void OnPanResetPressed()
    {
        if (_isSyncingUi || _globalData?.Settings == null) return;
        if (Math.Abs(_globalData.Settings.VideoDefaultPan - AppSettings.SystemDefaultVideoPan) < 1e-6f)
        {
            SyncSettings();
            return;
        }

        RecordHistory("Reset default video pan");
        _globalData.Settings.VideoDefaultPan = AppSettings.SystemDefaultVideoPan;
        SyncSettings();
    }

    private void UpdatePanResetButton()
    {
        if (_panResetButton == null || _globalData?.Settings == null) return;
        bool atDefault = Math.Abs(_globalData.Settings.VideoDefaultPan
                                  - AppSettings.SystemDefaultVideoPan) < 1e-6f;
        _panResetButton.Visible = !atDefault;
        if (!atDefault)
            _panResetButton.TooltipText =
                UiLocalizer.ResetDefaultTip(UiUtilities.FormatPan(AppSettings.SystemDefaultVideoPan));
    }

    // ── Image duration ─────────────────────────────────────────────────────

    private void OnImageDurationSubmitted(string text) => CommitImageDuration(text);

    private void OnImageDurationFocusExited()
    {
        if (_isSyncingUi || _imageDurationInput == null) return;
        CommitImageDuration(_imageDurationInput.Text);
    }

    private void CommitImageDuration(string text)
    {
        if (_globalData?.Settings == null || ComponentDefaultsUi.ShouldSkipEdit(_isSyncingUi, _historyManager)) return;

        string trimmed = (text ?? string.Empty).Trim();
        double seconds;
        if (string.IsNullOrEmpty(trimmed)
            || trimmed.Equals("until stopped", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("0", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("inf", StringComparison.OrdinalIgnoreCase))
        {
            seconds = 0;
            _imageDurationInput.Text = UiLocalizer.T("Until stopped");
        }
        else
        {
            var formatted = UiUtilities.ParseAndFormatTime(trimmed, out seconds, out string labeled);
            if (string.IsNullOrEmpty(formatted))
            {
                _globalSignals?.EmitSignal(nameof(GlobalSignals.Log),
                    $"Invalid default image duration: {text}", 1);
                double cur = _globalData.Settings.VideoDefaultImageDuration;
                _imageDurationInput.Text = cur <= 0
                    ? UiLocalizer.T("Until stopped")
                    : UiUtilities.FormatTime(cur);
                return;
            }

            seconds = Math.Max(0.0, seconds);
            if (seconds <= 0)
                _imageDurationInput.Text = UiLocalizer.T("Until stopped");
            else
            {
                _imageDurationInput.Text = formatted;
                _imageDurationInput.TooltipText = labeled;
            }
        }

        if (Mathf.IsEqualApprox((float)_globalData.Settings.VideoDefaultImageDuration, (float)seconds))
        {
            UpdateImageDurationResetButton();
            return;
        }

        RecordHistory("Change default image duration");
        _globalData.Settings.VideoDefaultImageDuration = seconds;
        UpdateImageDurationResetButton();
    }

    private void OnImageDurationResetPressed()
    {
        if (_isSyncingUi || _globalData?.Settings == null) return;
        if (Mathf.IsEqualApprox((float)_globalData.Settings.VideoDefaultImageDuration,
                (float)AppSettings.SystemDefaultVideoImageDuration))
        {
            SyncSettings();
            return;
        }

        RecordHistory("Reset default image duration");
        _globalData.Settings.VideoDefaultImageDuration = AppSettings.SystemDefaultVideoImageDuration;
        SyncSettings();
    }

    private void UpdateImageDurationResetButton()
    {
        if (_imageDurationResetButton == null || _globalData?.Settings == null) return;
        bool atDefault = Mathf.IsEqualApprox((float)_globalData.Settings.VideoDefaultImageDuration,
            (float)AppSettings.SystemDefaultVideoImageDuration);
        _imageDurationResetButton.Visible = !atDefault;
        if (!atDefault)
            _imageDurationResetButton.TooltipText = UiLocalizer.T("Reset to default: Until stopped (images only)");
    }

    // ── Fades ──────────────────────────────────────────────────────────────

    private void CommitFade(string text, bool isIn)
    {
        if (_globalData?.Settings == null || ComponentDefaultsUi.ShouldSkipEdit(_isSyncingUi, _historyManager))
            return;

        var field = isIn ? _fadeInInput : _fadeOutInput;
        double existing = isIn
            ? _globalData.Settings.VideoDefaultFadeIn
            : _globalData.Settings.VideoDefaultFadeOut;
        if (!ComponentDefaultsUi.TryParseTimeDefault(
                field, text, existing, _globalSignals, $"Invalid default fade time: {text}", out double seconds))
        {
            if (isIn) UpdateFadeInResetButton();
            else UpdateFadeOutResetButton();
            return;
        }

        RecordHistory(isIn ? "Change default video fade-in" : "Change default video fade-out");
        if (isIn)
            _globalData.Settings.VideoDefaultFadeIn = seconds;
        else
            _globalData.Settings.VideoDefaultFadeOut = seconds;

        if (isIn) UpdateFadeInResetButton();
        else UpdateFadeOutResetButton();
    }

    private void OnFadeInResetPressed()
    {
        if (_globalData?.Settings == null) return;
        ComponentDefaultsUi.TryResetField(
            _isSyncingUi, _historyManager, "VideoDefaults", "Reset default video fade-in",
            ComponentDefaultsUi.NearlyEqual(_globalData.Settings.VideoDefaultFadeIn, AppSettings.SystemDefaultVideoFadeIn),
            () => _globalData.Settings.VideoDefaultFadeIn = AppSettings.SystemDefaultVideoFadeIn,
            SyncSettings);
    }

    private void OnFadeOutResetPressed()
    {
        if (_globalData?.Settings == null) return;
        ComponentDefaultsUi.TryResetField(
            _isSyncingUi, _historyManager, "VideoDefaults", "Reset default video fade-out",
            ComponentDefaultsUi.NearlyEqual(_globalData.Settings.VideoDefaultFadeOut, AppSettings.SystemDefaultVideoFadeOut),
            () => _globalData.Settings.VideoDefaultFadeOut = AppSettings.SystemDefaultVideoFadeOut,
            SyncSettings);
    }

    private void UpdateFadeInResetButton()
    {
        if (_globalData?.Settings == null) return;
        bool atDefault = ComponentDefaultsUi.NearlyEqual(
            _globalData.Settings.VideoDefaultFadeIn, AppSettings.SystemDefaultVideoFadeIn);
        ComponentDefaultsUi.UpdateResetButton(
            _fadeInResetButton, atDefault,
            UiLocalizer.ResetDefaultTip(UiUtilities.FormatTime(AppSettings.SystemDefaultVideoFadeIn)));
    }

    private void UpdateFadeOutResetButton()
    {
        if (_globalData?.Settings == null) return;
        bool atDefault = ComponentDefaultsUi.NearlyEqual(
            _globalData.Settings.VideoDefaultFadeOut, AppSettings.SystemDefaultVideoFadeOut);
        ComponentDefaultsUi.UpdateResetButton(
            _fadeOutResetButton, atDefault,
            UiLocalizer.ResetDefaultTip(UiUtilities.FormatTime(AppSettings.SystemDefaultVideoFadeOut)));
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
