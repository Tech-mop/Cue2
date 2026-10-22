// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System;
using Cue2.App;
using Cue2.UI.Shell;
using Cue2.Services;
using Cue2.Domain.Cuelist;
using Cue2.Domain.Playback;
using Cue2.Domain.Devices;
using Cue2.Domain.ShowSettings;
using Cue2.Domain.Metadata;
using Cue2.Domain.Cues;
using Cue2.Domain.Connections;
using Cue2.Domain.Library;
using Cue2.Domain.Commands;
using Cue2.UI.Utilities;
using Godot;
using System.Linq;

namespace Cue2.UI.Inspectors;

/// <summary>
/// Partial: field submit handlers and commits (fade, translate, seek, property fades, GO/Stop fades).
/// </summary>
public partial class ControlComponentCard
{
    private void OnFadeSubmitted(string text)
    {
        CommitFade(text);
        _fadeLineEdit.ReleaseFocus();
    }

    private void OnFadeFocusExited()
    {
        if (_fadeEditing)
            CommitFade(_fadeLineEdit.Text);
    }

    private void CommitFade(string text)
    {
        if (_isSyncingUi || _component == null) return;
        if (_component.Action is not (ControlAction.Stop or ControlAction.Go or ControlAction.Fade
                or ControlAction.TranslateLayer))
            return;
        if (_globalData?.HistoryManager?.IsRestoring == true) return;

        if (_component.Action == ControlAction.Go)
        {
            CommitGoFade(text);
            return;
        }

        if (_component.Action == ControlAction.Fade)
        {
            CommitPropertyFadeDuration(text);
            return;
        }

        if (_component.Action == ControlAction.TranslateLayer)
        {
            CommitTranslateDuration(text);
            return;
        }

        CommitStopFade(text);
    }

    private void CommitTranslateDuration(string text)
    {
        text = (text ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(text))
        {
            if (_component.IsTranslateDurationAtDefault)
            {
                _fadeEditing = false;
                RefreshFromComponent();
                return;
            }

            RecordHistory("Edit control translate duration");
            _component.ResetTranslateDurationToDefault();
            _fadeEditing = false;
            RefreshFromComponent();
            NotifyOwnerDurationChanged();
            return;
        }

        string formatted = UiUtilities.ParseAndFormatTime(text, out double seconds, out bool isValid);
        if (!isValid || seconds < 0)
        {
            _globalSignals?.EmitSignal(nameof(GlobalSignals.Log),
                "Control card: invalid translate duration", (int)LogType.Warning);
            RefreshFromComponent();
            return;
        }

        if (Math.Abs(_component.TranslateDuration - seconds) < 1e-9)
        {
            _fadeEditing = false;
            _fadeLineEdit.Text = formatted;
            UpdateResetButtons();
            return;
        }

        RecordHistory("Edit control translate duration");
        _component.TranslateDuration = seconds;
        _fadeEditing = false;
        RefreshFromComponent();
        NotifyOwnerDurationChanged();
    }

    private void CommitPropertyFadeDuration(string text)
    {
        text = (text ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(text))
        {
            if (_component.IsPropertyFadeDurationAtDefault)
            {
                _fadeEditing = false;
                RefreshFromComponent();
                return;
            }

            RecordHistory("Edit control property fade time");
            _component.ResetPropertyFadeDurationToDefault();
            _fadeEditing = false;
            RefreshFromComponent();
            NotifyOwnerDurationChanged();
            return;
        }

        string formatted = UiUtilities.ParseAndFormatTime(text, out double seconds, out bool isValid);
        if (!isValid || seconds < 0)
        {
            _globalSignals?.EmitSignal(nameof(GlobalSignals.Log),
                "Control card: invalid property fade time", (int)LogType.Warning);
            RefreshFromComponent();
            return;
        }

        if (Math.Abs(_component.PropertyFadeDuration - seconds) < 1e-9)
        {
            _fadeEditing = false;
            _fadeLineEdit.Text = formatted;
            UpdateResetButtons();
            return;
        }

        RecordHistory("Edit control property fade time");
        _component.PropertyFadeDuration = seconds;
        _fadeEditing = false;
        RefreshFromComponent();
        NotifyOwnerDurationChanged();
    }

    private void OnFadeModeSelected(long index)
    {
        if (_isSyncingUi || _component == null) return;
        if (_globalData?.HistoryManager?.IsRestoring == true) return;

        var mode = (ControlFadeMode)(int)index;

        if (_component.Action == ControlAction.Fade)
        {
            if (_component.FadeMode == mode) return;

            RecordHistory("Edit control fade mode");
            _component.FadeMode = mode;
            // Relative default delta 0 is clearer when switching modes.
            if (mode == ControlFadeMode.Relative)
            {
                _component.FadeAudioDb = 0f;
                _component.FadeOpacityPercent = 0f;
                _component.FadePan = 0f;
            }
            else
            {
                _component.FadeAudioDb = 0f;
                _component.FadeOpacityPercent = 100f;
                _component.FadePan = 0f;
            }

            RefreshFromComponent();
            return;
        }

        if (_component.Action == ControlAction.Seek)
        {
            if (_component.SeekMode == mode) return;

            RecordHistory("Edit control seek mode");
            _component.SeekMode = mode;
            _component.SeekTimeSeconds = 0;
            RefreshFromComponent();
            return;
        }

        if (_component.Action == ControlAction.TranslateLayer)
        {
            if (_component.TranslateMode == mode) return;

            RecordHistory("Edit control translate mode");
            _component.TranslateMode = mode;
            // Relative defaults to zero deltas; absolute seeds from current layer geometry.
            if (mode == ControlFadeMode.Relative)
            {
                _component.TranslateSizeX = 0;
                _component.TranslateSizeY = 0;
                _component.TranslatePosX = 0;
                _component.TranslatePosY = 0;
            }
            else
            {
                SeedTranslateFromLayer();
            }

            RefreshFromComponent();
        }
    }

    /// <summary>
    /// Absolute/Relative + size/position fields for Translate Layer.
    /// </summary>
    private void RefreshTranslateLayerUi(bool isTranslate)
    {
        if (_layerCaption != null)
            _layerCaption.Visible = isTranslate;
        if (_targetLayerOption != null)
            _targetLayerOption.Visible = isTranslate;

        if (_sizeEnableCaption != null)
            _sizeEnableCaption.Visible = isTranslate;
        if (_sizeEnableRow != null)
            _sizeEnableRow.Visible = isTranslate;
        if (_posEnableCaption != null)
            _posEnableCaption.Visible = isTranslate;
        if (_posEnableRow != null)
            _posEnableRow.Visible = isTranslate;

        if (!isTranslate)
            return;

        if (_modeCaption != null)
            _modeCaption.Visible = true;
        if (_fadeModeOption != null)
        {
            _fadeModeOption.Visible = true;
            _fadeModeOption.Select((int)_component.TranslateMode);
        }

        PopulateTargetLayerOptions();

        if (_sizeEnable != null)
            _sizeEnable.SetPressedNoSignal(_component.TranslateSizeEnabled);
        if (_posEnable != null)
            _posEnable.SetPressedNoSignal(_component.TranslatePositionEnabled);

        bool relative = _component.TranslateMode == ControlFadeMode.Relative;
        if (_sizeXLineEdit != null)
        {
            _sizeXLineEdit.Text = FormatSignedInt(_component.TranslateSizeX, relative);
            _sizeXLineEdit.Editable = _component.TranslateSizeEnabled;
            _sizeXLineEdit.PlaceholderText = relative ? "±W" : "W";
        }
        if (_sizeYLineEdit != null)
        {
            _sizeYLineEdit.Text = FormatSignedInt(_component.TranslateSizeY, relative);
            _sizeYLineEdit.Editable = _component.TranslateSizeEnabled;
            _sizeYLineEdit.PlaceholderText = relative ? "±H" : "H";
        }
        if (_posXLineEdit != null)
        {
            _posXLineEdit.Text = FormatSignedInt(_component.TranslatePosX, relative);
            _posXLineEdit.Editable = _component.TranslatePositionEnabled;
            _posXLineEdit.PlaceholderText = relative ? "±X" : "X";
        }
        if (_posYLineEdit != null)
        {
            _posYLineEdit.Text = FormatSignedInt(_component.TranslatePosY, relative);
            _posYLineEdit.Editable = _component.TranslatePositionEnabled;
            _posYLineEdit.PlaceholderText = relative ? "±Y" : "Y";
        }
    }

    private static string FormatSignedInt(int value, bool relative)
    {
        if (relative && value > 0)
            return $"+{value}";
        return value.ToString();
    }

    private void PopulateTargetLayerOptions()
    {
        if (_targetLayerOption == null || _component == null) return;

        _targetLayerOption.Clear();
        UiLocalizer.AddTranslatedItem(_targetLayerOption, "(select layer)", 0);
        _targetLayerOption.SetItemMetadata(0, -1);

        int selectedIdx = 0;
        int index = 1;
        foreach (var layer in DisplaysManager.Layers.ToList())
        {
            if (layer == null) continue;
            string label = string.IsNullOrWhiteSpace(layer.LayerName)
                ? $"Layer {layer.LayerId}"
                : layer.LayerName;
            _targetLayerOption.AddItem(label, index);
            _targetLayerOption.SetItemMetadata(index, layer.LayerId);
            if (layer.LayerId == _component.TargetLayerId)
                selectedIdx = index;
            index++;
        }

        _targetLayerOption.Select(selectedIdx);
    }

    private void OnTargetLayerSelected(long index)
    {
        if (_isSyncingUi || _component == null) return;
        if (_component.Action != ControlAction.TranslateLayer) return;
        if (_globalData?.HistoryManager?.IsRestoring == true) return;
        if (_targetLayerOption == null) return;

        int layerId = _targetLayerOption.GetItemMetadata((int)index).AsInt32();
        if (layerId == _component.TargetLayerId) return;

        RecordHistory("Edit control target layer");
        _component.TargetLayerId = layerId;

        // Seed absolute values from the selected layer's current geometry.
        if (_component.TranslateMode == ControlFadeMode.Absolute)
            SeedTranslateFromLayer();

        RefreshFromComponent();
    }

    private void SeedTranslateFromLayer()
    {
        if (_component == null || _component.TargetLayerId < 0) return;
        var layer = DisplaysManager.GetLayerById(_component.TargetLayerId);
        if (layer == null) return;

        _component.TranslateSizeX = layer.Size.X;
        _component.TranslateSizeY = layer.Size.Y;
        _component.TranslatePosX = layer.CanvasPosition.X;
        _component.TranslatePosY = layer.CanvasPosition.Y;
    }

    private void OnSizeEnableToggled(bool pressed)
    {
        if (_isSyncingUi || _component == null) return;
        if (_component.Action != ControlAction.TranslateLayer) return;
        if (_globalData?.HistoryManager?.IsRestoring == true) return;
        if (_component.TranslateSizeEnabled == pressed) return;

        RecordHistory("Edit control translate size enable");
        _component.TranslateSizeEnabled = pressed;
        RefreshFromComponent();
    }

    private void OnPosEnableToggled(bool pressed)
    {
        if (_isSyncingUi || _component == null) return;
        if (_component.Action != ControlAction.TranslateLayer) return;
        if (_globalData?.HistoryManager?.IsRestoring == true) return;
        if (_component.TranslatePositionEnabled == pressed) return;

        RecordHistory("Edit control translate position enable");
        _component.TranslatePositionEnabled = pressed;
        RefreshFromComponent();
    }

    private void OnSizeSubmitted(LineEdit field)
    {
        CommitSizeFields();
        field?.ReleaseFocus();
    }

    private void OnPosSubmitted(LineEdit field)
    {
        CommitPosFields();
        field?.ReleaseFocus();
    }

    private void CommitSizeFields()
    {
        if (_isSyncingUi || _component == null) return;
        if (_component.Action != ControlAction.TranslateLayer) return;
        if (_globalData?.HistoryManager?.IsRestoring == true) return;

        if (!TryParseSignedInt(_sizeXLineEdit?.Text, out int sx) ||
            !TryParseSignedInt(_sizeYLineEdit?.Text, out int sy))
        {
            _globalSignals?.EmitSignal(nameof(GlobalSignals.Log),
                "Control card: invalid size values", (int)LogType.Warning);
            _sizeEditing = false;
            RefreshFromComponent();
            return;
        }

        if (_component.TranslateMode == ControlFadeMode.Absolute)
        {
            sx = Math.Max(1, sx);
            sy = Math.Max(1, sy);
        }

        if (sx == _component.TranslateSizeX && sy == _component.TranslateSizeY)
        {
            _sizeEditing = false;
            RefreshFromComponent();
            return;
        }

        RecordHistory("Edit control translate size");
        _component.TranslateSizeX = sx;
        _component.TranslateSizeY = sy;
        _sizeEditing = false;
        RefreshFromComponent();
    }

    private void CommitPosFields()
    {
        if (_isSyncingUi || _component == null) return;
        if (_component.Action != ControlAction.TranslateLayer) return;
        if (_globalData?.HistoryManager?.IsRestoring == true) return;

        if (!TryParseSignedInt(_posXLineEdit?.Text, out int px) ||
            !TryParseSignedInt(_posYLineEdit?.Text, out int py))
        {
            _globalSignals?.EmitSignal(nameof(GlobalSignals.Log),
                "Control card: invalid position values", (int)LogType.Warning);
            _posEditing = false;
            RefreshFromComponent();
            return;
        }

        if (px == _component.TranslatePosX && py == _component.TranslatePosY)
        {
            _posEditing = false;
            RefreshFromComponent();
            return;
        }

        RecordHistory("Edit control translate position");
        _component.TranslatePosX = px;
        _component.TranslatePosY = py;
        _posEditing = false;
        RefreshFromComponent();
    }

    private static bool TryParseSignedInt(string text, out int value)
    {
        value = 0;
        text = (text ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(text))
            return true; // treat blank as 0
        if (text.StartsWith('+'))
            text = text[1..];
        return int.TryParse(text, out value);
    }

    private void OnSeekTimeSubmitted(string text)
    {
        CommitSeekTime(text);
        _seekTimeLineEdit?.ReleaseFocus();
    }

    private void OnSeekTimeFocusExited()
    {
        if (_seekTimeEditing)
            CommitSeekTime(_seekTimeLineEdit?.Text ?? string.Empty);
    }

    private void CommitSeekTime(string text)
    {
        if (_isSyncingUi || _component == null) return;
        if (_component.Action != ControlAction.Seek) return;
        if (_globalData?.HistoryManager?.IsRestoring == true) return;

        text = (text ?? string.Empty).Trim();
        bool negative = text.StartsWith('-');
        if (text.StartsWith('+') || text.StartsWith('-'))
            text = text[1..].Trim();

        if (string.IsNullOrEmpty(text))
        {
            if (Math.Abs(_component.SeekTimeSeconds) < 1e-9)
            {
                _seekTimeEditing = false;
                RefreshFromComponent();
                return;
            }

            RecordHistory("Edit control seek time");
            _component.SeekTimeSeconds = 0;
            _seekTimeEditing = false;
            RefreshFromComponent();
            return;
        }

        string formatted = UiUtilities.ParseAndFormatTime(text, out double seconds, out bool isValid);
        if (!isValid || seconds < 0)
        {
            _globalSignals?.EmitSignal(nameof(GlobalSignals.Log),
                "Control card: invalid seek time", (int)LogType.Warning);
            RefreshFromComponent();
            return;
        }

        if (negative)
            seconds = -seconds;

        // Absolute seeks must be non-negative.
        if (_component.SeekMode == ControlFadeMode.Absolute && seconds < 0)
            seconds = 0;

        if (Math.Abs(_component.SeekTimeSeconds - seconds) < 1e-9)
        {
            _seekTimeEditing = false;
            RefreshFromComponent();
            return;
        }

        RecordHistory("Edit control seek time");
        _component.SeekTimeSeconds = seconds;
        _seekTimeEditing = false;
        RefreshFromComponent();
    }

    private void OnFadePropertySelected(long index)
    {
        if (_isSyncingUi || _component == null) return;
        if (_component.Action != ControlAction.Fade) return;
        if (_globalData?.HistoryManager?.IsRestoring == true) return;
        if (_fadePropertyOption == null) return;

        var property = (ControlFadeProperty)_fadePropertyOption.GetItemMetadata((int)index).AsInt32();
        if (_component.FadeProperty == property) return;

        RecordHistory("Edit control fade property");
        _component.FadeProperty = property;
        _component.FadeAudioVolumeEnabled = property == ControlFadeProperty.Volume;
        _component.FadeVideoOpacityEnabled = property == ControlFadeProperty.Opacity;

        // Sensible defaults when switching property under absolute mode.
        if (_component.FadeMode == ControlFadeMode.Absolute)
        {
            switch (property)
            {
                case ControlFadeProperty.Volume:
                case ControlFadeProperty.RoutingMatrix:
                    // Keep current dB if already set; otherwise full / unity.
                    break;
                case ControlFadeProperty.Opacity:
                    if (Math.Abs(_component.FadeOpacityPercent) < 1e-4f)
                        _component.FadeOpacityPercent = 100f;
                    break;
                case ControlFadeProperty.Pan:
                    // Leave pan as-is (0 = center is a fine absolute default).
                    break;
                case ControlFadeProperty.PlayRate:
                    if (Math.Abs(_component.FadePlayRate) < 1e-4f)
                        _component.FadePlayRate = 1f;
                    break;
                case ControlFadeProperty.Pitch:
                    break;
            }
        }

        RefreshFromComponent();
    }

    private void OnPanFadeSliderChanged(double value)
    {
        if (_isUpdatingPanUi || _isSyncingUi || _component == null) return;
        if (_component.Action != ControlAction.Fade) return;
        if (_component.FadeProperty != ControlFadeProperty.Pan) return;
        if (_globalData?.HistoryManager?.IsRestoring == true) return;

        float pan = Mathf.Clamp((float)value / 100f, -1f, 1f);
        if (Math.Abs(_component.FadePan - pan) < 1e-6f) return;

        // Coalesce continuous drag into one undo step.
        int cueId = GetOwnerCueId();
        if (cueId >= 0)
        {
            string key = $"cue:{cueId}:control-fade-pan";
            InspectorMultiEditSupport.RecordBeforeEditById(
                _globalData, multiHistory: false, cueId, "Edit control fade pan", coalesceKey: key);
        }

        _component.FadePan = pan;
        _isUpdatingPanUi = true;
        try
        {
            if (_panFadeLineEdit != null && !_panFadeLineEdit.HasFocus())
            {
                bool relative = _component.FadeMode == ControlFadeMode.Relative;
                if (relative)
                {
                    if (Mathf.IsZeroApprox(pan))
                        _panFadeLineEdit.Text = "0";
                    else if (pan > 0)
                        _panFadeLineEdit.Text = $"+{UiUtilities.FormatPan(pan)}";
                    else
                        _panFadeLineEdit.Text = $"-{UiUtilities.FormatPan(Mathf.Abs(pan))}";
                }
                else
                {
                    _panFadeLineEdit.Text = UiUtilities.FormatPan(pan);
                }
            }
        }
        finally
        {
            _isUpdatingPanUi = false;
        }
    }

    private void OnPanFadeSliderDragEnded(bool valueChanged)
    {
        if (_component == null) return;
        int cueId = GetOwnerCueId();
        if (cueId >= 0)
        {
            string key = $"cue:{cueId}:control-fade-pan";
            InspectorMultiEditSupport.EndCoalesce(_globalData, multiHistory: false, key, key);
        }
        if (valueChanged)
            SyncPanFadeUiFromComponent();
    }

    private void OnAudioFadeSubmitted(string text)
    {
        CommitAudioFade(text);
        _audioFadeLineEdit?.ReleaseFocus();
    }

    private void OnAudioFadeFocusExited()
    {
        if (_audioFadeEditing)
            CommitAudioFade(_audioFadeLineEdit?.Text ?? string.Empty);
    }

    private void OnOpacityFadeSubmitted(string text)
    {
        CommitOpacityFade(text);
        _opacityFadeLineEdit?.ReleaseFocus();
    }

    private void OnOpacityFadeFocusExited()
    {
        if (_opacityFadeEditing)
            CommitOpacityFade(_opacityFadeLineEdit?.Text ?? string.Empty);
    }

    private void OnPanFadeSubmitted(string text)
    {
        CommitPanFade(text);
        _panFadeLineEdit?.ReleaseFocus();
    }

    private void OnPanFadeFocusExited()
    {
        if (_panFadeEditing)
            CommitPanFade(_panFadeLineEdit?.Text ?? string.Empty);
    }

    private void OnRateFadeSubmitted(string text)
    {
        CommitRateFade(text);
        _rateFadeLineEdit?.ReleaseFocus();
    }

    private void OnRateFadeFocusExited()
    {
        if (_rateFadeEditing)
            CommitRateFade(_rateFadeLineEdit?.Text ?? string.Empty);
    }

    private void OnPitchFadeSubmitted(string text)
    {
        CommitPitchFade(text);
        _pitchFadeLineEdit?.ReleaseFocus();
    }

    private void OnPitchFadeFocusExited()
    {
        if (_pitchFadeEditing)
            CommitPitchFade(_pitchFadeLineEdit?.Text ?? string.Empty);
    }

    private void CommitRateFade(string text)
    {
        if (_isSyncingUi || _component == null) return;
        if (_component.Action != ControlAction.Fade) return;
        if (_component.FadeProperty != ControlFadeProperty.PlayRate) return;
        if (_globalData?.HistoryManager?.IsRestoring == true) return;

        text = (text ?? string.Empty).Trim();
        if (text.StartsWith('+'))
            text = text[1..].Trim();
        if (!float.TryParse(text, out float rate) || (_component.FadeMode == ControlFadeMode.Absolute && rate <= 0))
        {
            _globalSignals?.EmitSignal(nameof(GlobalSignals.Log),
                "Control card: invalid play rate", (int)LogType.Warning);
            RefreshFromComponent();
            return;
        }

        if (_component.FadeMode == ControlFadeMode.Absolute)
            rate = (float)AudioComponent.ClampPlayRate(rate);

        if (Math.Abs(_component.FadePlayRate - rate) < 1e-4f)
        {
            _rateFadeEditing = false;
            RefreshFromComponent();
            return;
        }

        RecordHistory("Edit control fade play rate");
        _component.FadePlayRate = rate;
        _rateFadeEditing = false;
        RefreshFromComponent();
    }

    private void CommitPitchFade(string text)
    {
        if (_isSyncingUi || _component == null) return;
        if (_component.Action != ControlAction.Fade) return;
        if (_component.FadeProperty != ControlFadeProperty.Pitch) return;
        if (_globalData?.HistoryManager?.IsRestoring == true) return;

        text = (text ?? string.Empty).Replace("¢", "", StringComparison.Ordinal)
            .Replace("cents", "", StringComparison.OrdinalIgnoreCase).Trim();
        if (text.StartsWith('+'))
            text = text[1..].Trim();
        if (!float.TryParse(text, out float cents))
        {
            _globalSignals?.EmitSignal(nameof(GlobalSignals.Log),
                "Control card: invalid pitch cents", (int)LogType.Warning);
            RefreshFromComponent();
            return;
        }

        if (_component.FadeMode == ControlFadeMode.Absolute)
            cents = AudioComponent.ClampPitchCents(cents);

        if (Math.Abs(_component.FadePitchCents - cents) < 1e-3f)
        {
            _pitchFadeEditing = false;
            RefreshFromComponent();
            return;
        }

        RecordHistory("Edit control fade pitch");
        _component.FadePitchCents = cents;
        _pitchFadeEditing = false;
        RefreshFromComponent();
    }

    private void CommitAudioFade(string text)
    {
        if (_isSyncingUi || _component == null) return;
        if (_component.Action != ControlAction.Fade) return;
        if (_component.FadeProperty is not (ControlFadeProperty.Volume or ControlFadeProperty.RoutingMatrix))
            return;
        if (_globalData?.HistoryManager?.IsRestoring == true) return;

        text = (text ?? string.Empty).Replace("dB", "", StringComparison.OrdinalIgnoreCase).Trim();
        if (text.StartsWith('+'))
            text = text[1..].Trim();
        if (!float.TryParse(text, out float db))
        {
            _globalSignals?.EmitSignal(nameof(GlobalSignals.Log),
                "Control card: invalid fade level dB", (int)LogType.Warning);
            RefreshFromComponent();
            return;
        }

        if (_component.FadeMode == ControlFadeMode.Absolute)
        {
            // Component volume fades allow digital gain; routing-matrix targets stay unity-max.
            if (_component.FadeProperty == ControlFadeProperty.RoutingMatrix)
                db = Mathf.Clamp(db, UiUtilities.MinVolumeDb, 0f);
            else
                db = Mathf.Clamp(db, UiUtilities.MinVolumeDb, UiUtilities.MaxComponentGainDb);
        }

        if (Math.Abs(_component.FadeAudioDb - db) < 1e-4f)
        {
            _audioFadeEditing = false;
            RefreshFromComponent();
            return;
        }

        RecordHistory(_component.FadeProperty == ControlFadeProperty.RoutingMatrix
            ? "Edit control fade matrix level"
            : "Edit control fade volume");
        _component.FadeAudioDb = db;
        _audioFadeEditing = false;
        RefreshFromComponent();
    }

    private void CommitOpacityFade(string text)
    {
        if (_isSyncingUi || _component == null) return;
        if (_component.Action != ControlAction.Fade) return;
        if (_component.FadeProperty != ControlFadeProperty.Opacity) return;
        if (_globalData?.HistoryManager?.IsRestoring == true) return;

        text = (text ?? string.Empty).Replace("%", "", StringComparison.Ordinal).Trim();
        if (text.StartsWith('+'))
            text = text[1..].Trim();
        if (!float.TryParse(text, out float pct))
        {
            _globalSignals?.EmitSignal(nameof(GlobalSignals.Log),
                "Control card: invalid opacity fade %", (int)LogType.Warning);
            RefreshFromComponent();
            return;
        }

        if (_component.FadeMode == ControlFadeMode.Absolute)
            pct = Mathf.Clamp(pct, 0f, 100f);

        if (Math.Abs(_component.FadeOpacityPercent - pct) < 1e-4f)
        {
            _opacityFadeEditing = false;
            RefreshFromComponent();
            return;
        }

        RecordHistory("Edit control fade opacity");
        _component.FadeOpacityPercent = pct;
        _opacityFadeEditing = false;
        RefreshFromComponent();
    }

    private void CommitPanFade(string text)
    {
        if (_isSyncingUi || _component == null) return;
        if (_component.Action != ControlAction.Fade) return;
        if (_component.FadeProperty != ControlFadeProperty.Pan) return;
        if (_globalData?.HistoryManager?.IsRestoring == true) return;

        text = (text ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(text))
        {
            if (Math.Abs(_component.FadePan) < 1e-6f)
            {
                _panFadeEditing = false;
                RefreshFromComponent();
                return;
            }

            RecordHistory("Edit control fade pan");
            _component.FadePan = 0f;
            _panFadeEditing = false;
            RefreshFromComponent();
            return;
        }

        // Allow relative signed values: +L50 / -R25 / +0.5
        bool negative = text.StartsWith('-');
        if (text.StartsWith('+') || text.StartsWith('-'))
            text = text[1..].Trim();

        if (!UiUtilities.TryParsePan(text, out float pan) &&
            !float.TryParse(text, out pan))
        {
            // Numeric −100…100 without L/R prefix
            _globalSignals?.EmitSignal(nameof(GlobalSignals.Log),
                "Control card: invalid pan value", (int)LogType.Warning);
            RefreshFromComponent();
            return;
        }

        // If user typed a bare number outside −1…1, treat as percent (−100…100).
        if (Math.Abs(pan) > 1f + 1e-4f)
            pan = Mathf.Clamp(pan, -100f, 100f) / 100f;

        if (negative)
            pan = -Mathf.Abs(pan);

        if (_component.FadeMode == ControlFadeMode.Absolute)
            pan = Mathf.Clamp(pan, -1f, 1f);

        if (Math.Abs(_component.FadePan - pan) < 1e-6f)
        {
            _panFadeEditing = false;
            RefreshFromComponent();
            return;
        }

        RecordHistory("Edit control fade pan");
        _component.FadePan = pan;
        _panFadeEditing = false;
        RefreshFromComponent();
    }

    private void CommitGoFade(string text)
    {
        text = (text ?? string.Empty).Trim();

        // Blank → default 0.
        if (string.IsNullOrEmpty(text))
        {
            if (_component.IsGoFadeAtDefault)
            {
                _fadeEditing = false;
                RefreshFromComponent();
                return;
            }

            RecordHistory("Edit control go fade-in");
            _component.ResetGoFadeInToDefault();
            _fadeEditing = false;
            RefreshFromComponent();
            NotifyOwnerDurationChanged();
            return;
        }

        string formatted = UiUtilities.ParseAndFormatTime(text, out double seconds, out bool isValid);
        if (!isValid || seconds < 0)
        {
            _globalSignals?.EmitSignal(nameof(GlobalSignals.Log),
                "Control card: invalid go fade-in time", (int)LogType.Warning);
            RefreshFromComponent();
            return;
        }

        if (Math.Abs(_component.GoFadeInDuration - seconds) < 1e-9)
        {
            _fadeEditing = false;
            _fadeLineEdit.Text = formatted;
            UpdateResetButtons();
            return;
        }

        RecordHistory("Edit control go fade-in");
        _component.GoFadeInDuration = seconds;
        _fadeEditing = false;
        RefreshFromComponent();
        NotifyOwnerDurationChanged();
    }

    private void CommitStopFade(string text)
    {
        text = (text ?? string.Empty).Trim();
        float sessionDefault = _globalData?.Settings?.StopFadeDuration ?? 0f;

        // Blank → back to session default.
        if (string.IsNullOrEmpty(text))
        {
            if (_component.IsStopFadeAtDefault)
            {
                _fadeEditing = false;
                RefreshFromComponent();
                return;
            }

            RecordHistory("Edit control stop fade");
            _component.ResetStopFadeToSessionDefault();
            _fadeEditing = false;
            RefreshFromComponent();
            NotifyOwnerDurationChanged();
            return;
        }

        string formatted = UiUtilities.ParseAndFormatTime(text, out double seconds, out bool isValid);
        if (!isValid || seconds < 0)
        {
            _globalSignals?.EmitSignal(nameof(GlobalSignals.Log),
                "Control card: invalid stop fade time", (int)LogType.Warning);
            RefreshFromComponent();
            return;
        }

        // Match session default → stay on / return to session-default mode.
        if (Math.Abs(seconds - sessionDefault) < 1e-6)
        {
            if (_component.IsStopFadeAtDefault)
            {
                _fadeEditing = false;
                RefreshFromComponent();
                return;
            }

            RecordHistory("Edit control stop fade");
            _component.ResetStopFadeToSessionDefault();
            _fadeEditing = false;
            RefreshFromComponent();
            NotifyOwnerDurationChanged();
            return;
        }

        if (!_component.StopFadeUsesSessionDefault &&
            Math.Abs(_component.StopFadeDuration - seconds) < 1e-9)
        {
            _fadeEditing = false;
            _fadeLineEdit.Text = formatted;
            return;
        }

        RecordHistory("Edit control stop fade");
        _component.StopFadeUsesSessionDefault = false;
        _component.StopFadeDuration = seconds;
        _fadeEditing = false;
        RefreshFromComponent();
        NotifyOwnerDurationChanged();
    }
}
