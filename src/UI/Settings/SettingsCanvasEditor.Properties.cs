// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
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
using Cue2.UI.Popups;

using Cue2.UI.Utilities;
namespace Cue2.UI.Settings;

/// <summary>
/// Canvas editor UI for arranging screens and target layers on the video canvas.
/// Left: Screens + Target Layers trees. Center: interactive stage (move/resize). Right: properties.
/// </summary>
/// <summary>
/// Partial: Properties panel load + defaults/reset buttons
/// </summary>
public partial class SettingsCanvasEditor
{
    #region Properties panel

    private void ShowPropertiesForSelection()
    {
        _emptyPropsLabel.Visible = false;
        _canvasProps.Visible = false;
        _outputProps.Visible = false;
        _layerProps.Visible = false;

        switch (_selectionKind)
        {
            case SelectionKind.Canvas:
                _canvasProps.Visible = true;
                LoadCanvasProps();
                break;
            case SelectionKind.Screen:
                _outputProps.Visible = true;
                LoadScreenProps();
                break;
            case SelectionKind.Layer:
                _layerProps.Visible = true;
                LoadLayerProps();
                break;
            default:
                _emptyPropsLabel.Visible = true;
                _emptyPropsLabel.Text = UiLocalizer.T("Select Canvas, a Screen, or a Target Layer.");
                break;
        }
    }

    private void LoadCanvasProps()
    {
        _isUpdatingProps = true;
        _canvasSizeXLineEdit.Text = _canvas.CanvasSize.X.ToString();
        _canvasSizeYLineEdit.Text = _canvas.CanvasSize.Y.ToString();
        _canvasTestPatternCheckBox.ButtonPressed = _canvas.TestPatternEnabled;
        UpdateCanvasResetButtons();
        _isUpdatingProps = false;
    }

    /// <summary>
    /// Shows reset affordances for canvas properties that differ from defaults.
    /// </summary>
    private void UpdateCanvasResetButtons()
    {
        if (_canvas == null)
            return;

        SetResetVisible(_canvasTestPatternResetButton, _canvas.TestPatternEnabled != DefaultTestPattern,
            "Reset to default: Off");
    }

    /// <summary>
    /// Enables or disables the full-canvas test pattern from canvas properties.
    /// </summary>
    /// <param name="toggled">New checkbox state.</param>
    private void OnCanvasTestPatternToggled(bool toggled)
    {
        if (_isUpdatingProps || _canvas == null)
            return;
        if (_canvas.TestPatternEnabled == toggled)
            return;

        RecordDisplaysHistory(toggled ? "Enable canvas test pattern" : "Disable canvas test pattern");
        _displaysManager.ToggleCanvasTestPattern(toggled);
        UpdateCanvasResetButtons();
    }

    /// <summary>
    /// Resets canvas test pattern to off (default).
    /// </summary>
    private void OnCanvasTestPatternResetPressed()
    {
        if (_canvas == null)
            return;
        RecordDisplaysHistory("Reset canvas test pattern");
        _displaysManager.ToggleCanvasTestPattern(DefaultTestPattern);
        LoadCanvasProps();
    }

    private void LoadScreenProps()
    {
        _isUpdatingProps = true;
        try
        {
            var screen = _displaysManager.GetOutputById(_selectedScreenId);
            if (screen == null)
            {
                _outputProps.Visible = false;
                _emptyPropsLabel.Visible = true;
                _emptyPropsLabel.Text = UiLocalizer.T("Screen not found.");
                return;
            }

            _outputPropsTitle.Text = UiLocalizer.T("Screen");
            _screenNameLineEdit.Text = screen.OutputName;
            _outputPosXLineEdit.Text = screen.CanvasPosition.X.ToString();
            _outputPosYLineEdit.Text = screen.CanvasPosition.Y.ToString();
            _outputSizeXLineEdit.Text = screen.OutputSize.X.ToString();
            _outputSizeYLineEdit.Text = screen.OutputSize.Y.ToString();
            _displayOffsetXLineEdit.Text = screen.DisplayOffset.X.ToString();
            _displayOffsetYLineEdit.Text = screen.DisplayOffset.Y.ToString();
            _screenKeepAspectCheckBox.ButtonPressed = screen.KeepAspect;
            _outputTransparentCheckBox.ButtonPressed = screen.OutputTransparent;
            _outputTestPatternCheckBox.ButtonPressed = screen.TestPatternStatus();

            PopulateOutputOption(screen.TargetMonitor);
            UpdateScreenResetButtons(screen);
            UpdateDisplayOffsetLabel(screen);

            if (screen.IsVirtual)
            {
                _outputResolutionLabel.Text = UiLocalizer.T("Virtual Output — not shown on a physical display");
            }
            else if (screen.IsWindow)
            {
                if (screen.IsWindowDismissed)
                {
                    _outputResolutionLabel.Text =
                        "Window closed — change size/position or reselect Window to show again";
                }
                else
                {
                    _outputResolutionLabel.Text =
                        $"Portable Window  ·  {screen.OutputSize.X}×{screen.OutputSize.Y}  (OS title bar + controls)";
                }
            }
            else
            {
                var displays = _displaysManager.GetAvailableDisplays();
                string res = "Physical output";
                foreach (var d in displays)
                {
                    if (d.Index == screen.TargetMonitor)
                    {
                        res = $"{d.Name}  ·  {d.Size.X}×{d.Size.Y}";
                        break;
                    }
                }

                if (screen.TargetMonitor >= DisplayServer.GetScreenCount())
                    res = $"Monitor {screen.TargetMonitor} (not connected)";

                _outputResolutionLabel.Text = res;
            }

            _deleteScreenButton.Disabled = DisplaysManager.Screens.Count <= 1;
        }
        finally
        {
            _isUpdatingProps = false;
        }
    }

    private void PopulateOutputOption(int selectedMonitor)
    {
        _screenOutputOption.Clear();
        _outputOptionMonitorMap.Clear();

        // Destination options: Virtual, Window, then physical displays.
        UiLocalizer.AddTranslatedItem(_screenOutputOption, "Virtual Output");
        _outputOptionMonitorMap.Add(VideoOutputDevice.VirtualMonitorIndex);

        UiLocalizer.AddTranslatedItem(_screenOutputOption, "Window");
        _outputOptionMonitorMap.Add(VideoOutputDevice.WindowMonitorIndex);

        var displays = _displaysManager.GetAvailableDisplays();
        int selectIndex = 0;
        if (selectedMonitor == VideoOutputDevice.WindowMonitorIndex)
            selectIndex = 1;

        // Physical displays start after Virtual (0) and Window (1).
        const int physicalStartIndex = 2;
        for (int i = 0; i < displays.Count; i++)
        {
            var d = displays[i];
            _screenOutputOption.AddItem($"{d.Name}  ({d.Size.X}×{d.Size.Y})");
            _outputOptionMonitorMap.Add(d.Index);
            if (d.Index == selectedMonitor)
                selectIndex = physicalStartIndex + i;
        }

        if (selectedMonitor >= 0 && selectIndex == 0)
        {
            _screenOutputOption.AddItem($"Monitor {selectedMonitor} (missing)");
            _outputOptionMonitorMap.Add(selectedMonitor);
            selectIndex = _outputOptionMonitorMap.Count - 1;
        }

        _screenOutputOption.Select(selectIndex);
    }

    /// <summary>
    /// Display Offset means monitor-relative offset for physical screens, absolute desktop position for Window.
    /// </summary>
    private void UpdateDisplayOffsetLabel(VideoOutputDevice screen)
    {
        var offsetLabel = GetNodeOrNull<Label>("%DisplayOffsetLabel");
        if (offsetLabel == null)
            return;

        if (screen != null && screen.IsWindow)
        {
            offsetLabel.Text = UiLocalizer.T("Window Position");
            if (_displayOffsetXLineEdit != null)
                _displayOffsetXLineEdit.TooltipText = UiLocalizer.T("Desktop X position of the portable window");
            if (_displayOffsetYLineEdit != null)
                _displayOffsetYLineEdit.TooltipText = UiLocalizer.T("Desktop Y position of the portable window");
        }
        else
        {
            offsetLabel.Text = UiLocalizer.T("Display Offset");
            if (_displayOffsetXLineEdit != null)
                _displayOffsetXLineEdit.TooltipText = UiLocalizer.T("Offset from the target display origin (X)");
            if (_displayOffsetYLineEdit != null)
                _displayOffsetYLineEdit.TooltipText = UiLocalizer.T("Offset from the target display origin (Y)");
        }
    }

    private void LoadLayerProps()
    {
        _isUpdatingProps = true;
        try
        {
            var layer = DisplaysManager.GetLayerById(_selectedLayerId);
            if (layer == null)
            {
                _layerProps.Visible = false;
                _emptyPropsLabel.Visible = true;
                _emptyPropsLabel.Text = UiLocalizer.T("Layer not found.");
                return;
            }

            _layerNameLineEdit.Text = layer.LayerName;
            _layerPosXLineEdit.Text = layer.CanvasPosition.X.ToString();
            _layerPosYLineEdit.Text = layer.CanvasPosition.Y.ToString();
            _layerSizeXLineEdit.Text = layer.Size.X.ToString();
            _layerSizeYLineEdit.Text = layer.Size.Y.ToString();
            _layerKeepAspectCheckBox.ButtonPressed = layer.KeepAspect;
            _layerTransparentCheckBox.ButtonPressed = layer.Transparent;
            _layerTestPatternCheckBox.ButtonPressed = layer.TestPatternEnabled;
            _layerLockCheckBox.ButtonPressed = layer.Locked;
            UpdateLayerResetButtons(layer);
        }
        finally
        {
            _isUpdatingProps = false;
        }
    }

    #endregion

    #region Defaults / reset buttons

    private static readonly Vector2I DefaultCanvasPosition = Vector2I.Zero;
    private static readonly Vector2I DefaultDisplayOffset = Vector2I.Zero;
    private const bool DefaultKeepAspect = false;
    private const bool DefaultTransparent = false;
    private const bool DefaultTestPattern = false;
    private const bool DefaultLocked = false;
    private const int DefaultOutputMonitor = VideoOutputDevice.VirtualMonitorIndex;

    private void UpdateScreenResetButtons(VideoOutputDevice screen)
    {
        if (screen == null)
            return;

        Vector2I defaultSize = _displaysManager.GetDefaultScreenSize(screen);

        SetResetVisible(_screenOutputResetButton, screen.TargetMonitor != DefaultOutputMonitor,
            "Reset to default: Virtual Output");
        SetResetVisible(_screenSizeResetButton, screen.OutputSize != defaultSize,
            $"Reset to default: {defaultSize.X}×{defaultSize.Y}");
        SetResetVisible(_screenKeepAspectResetButton, screen.KeepAspect != DefaultKeepAspect,
            "Reset to default: Off");
        SetResetVisible(_screenPosResetButton, screen.CanvasPosition != DefaultCanvasPosition,
            "Reset to default: 0×0");
        SetResetVisible(_screenDisplayOffsetResetButton, screen.DisplayOffset != DefaultDisplayOffset,
            "Reset to default: 0×0");
        SetResetVisible(_screenTransparentResetButton, screen.OutputTransparent != DefaultTransparent,
            "Reset to default: Off");
        SetResetVisible(_screenTestPatternResetButton, screen.TestPatternStatus() != DefaultTestPattern,
            "Reset to default: Off");
    }

    private void UpdateLayerResetButtons(VideoTargetLayer layer)
    {
        if (layer == null)
            return;

        Vector2I defaultSize = _displaysManager.GetDefaultLayerSize();

        SetResetVisible(_layerSizeResetButton, layer.Size != defaultSize,
            $"Reset to default: {defaultSize.X}×{defaultSize.Y}");
        SetResetVisible(_layerKeepAspectResetButton, layer.KeepAspect != DefaultKeepAspect,
            "Reset to default: Off");
        SetResetVisible(_layerPosResetButton, layer.CanvasPosition != DefaultCanvasPosition,
            "Reset to default: 0×0");
        SetResetVisible(_layerTransparentResetButton, layer.Transparent != DefaultTransparent,
            "Reset to default: Off");
        SetResetVisible(_layerTestPatternResetButton, layer.TestPatternEnabled != DefaultTestPattern,
            "Reset to default: Off");
        SetResetVisible(_layerLockResetButton, layer.Locked != DefaultLocked,
            "Reset to default: Off");
    }

    private static void SetResetVisible(Button button, bool show, string tooltip)
    {
        if (button == null)
            return;
        button.Visible = show;
        if (show)
            button.TooltipText = tooltip;
    }

    private void OnScreenOutputResetPressed()
    {
        var screen = GetSelectedScreen();
        if (screen == null)
            return;
        RecordDisplaysHistory("Reset screen output");
        _displaysManager.UpdateScreenTargetMonitor(screen.OutputId, DefaultOutputMonitor);
        RebuildTrees();
        LoadScreenProps();
        UpdateCanvasGizmos();
    }

    private void OnScreenSizeResetPressed()
    {
        var screen = GetSelectedScreen();
        if (screen == null)
            return;
        RecordDisplaysHistory("Reset screen size");
        var def = _displaysManager.GetDefaultScreenSize(screen);
        _displaysManager.UpdateOutputSize(screen.OutputId, def);
        LoadScreenProps();
        UpdateCanvasGizmos();
    }

    private void OnScreenKeepAspectResetPressed()
    {
        var screen = GetSelectedScreen();
        if (screen == null)
            return;
        RecordDisplaysHistory("Reset screen keep-aspect");
        _displaysManager.UpdateScreenKeepAspect(screen.OutputId, DefaultKeepAspect);
        LoadScreenProps();
    }

    private void OnScreenPosResetPressed()
    {
        var screen = GetSelectedScreen();
        if (screen == null)
            return;
        RecordDisplaysHistory("Reset screen position");
        _displaysManager.UpdateOutputCanvasPosition(screen.OutputId, DefaultCanvasPosition);
        LoadScreenProps();
        UpdateCanvasGizmos();
    }

    private void OnScreenDisplayOffsetResetPressed()
    {
        var screen = GetSelectedScreen();
        if (screen == null)
            return;
        RecordDisplaysHistory("Reset screen display offset");
        _displaysManager.UpdateScreenDisplayOffset(screen.OutputId, DefaultDisplayOffset);
        LoadScreenProps();
    }

    private void OnScreenTransparentResetPressed()
    {
        var screen = GetSelectedScreen();
        if (screen == null)
            return;
        RecordDisplaysHistory("Reset screen transparency");
        screen.SetTransparent(DefaultTransparent);
        LoadScreenProps();
    }

    private void OnScreenTestPatternResetPressed()
    {
        var screen = GetSelectedScreen();
        if (screen == null)
            return;
        RecordDisplaysHistory("Reset screen test pattern");
        screen.ToggleTestPattern(DefaultTestPattern);
        LoadScreenProps();
    }

    private void OnLayerSizeResetPressed()
    {
        if (_selectionKind != SelectionKind.Layer)
            return;
        RecordDisplaysHistory("Reset layer size");
        var def = _displaysManager.GetDefaultLayerSize();
        _displaysManager.UpdateLayerSize(_selectedLayerId, def);
        LoadLayerProps();
        UpdateCanvasGizmos();
    }

    private void OnLayerKeepAspectResetPressed()
    {
        if (_selectionKind != SelectionKind.Layer)
            return;
        RecordDisplaysHistory("Reset layer keep-aspect");
        _displaysManager.UpdateLayerKeepAspect(_selectedLayerId, DefaultKeepAspect);
        LoadLayerProps();
    }

    private void OnLayerPosResetPressed()
    {
        if (_selectionKind != SelectionKind.Layer)
            return;
        RecordDisplaysHistory("Reset layer position");
        _displaysManager.UpdateLayerCanvasPosition(_selectedLayerId, DefaultCanvasPosition);
        LoadLayerProps();
        UpdateCanvasGizmos();
    }

    private void OnLayerTransparentResetPressed()
    {
        if (_selectionKind != SelectionKind.Layer)
            return;
        RecordDisplaysHistory("Reset layer transparency");
        _displaysManager.UpdateLayerTransparent(_selectedLayerId, DefaultTransparent);
        LoadLayerProps();
    }

    private void OnLayerTestPatternResetPressed()
    {
        if (_selectionKind != SelectionKind.Layer)
            return;
        RecordDisplaysHistory("Reset layer test pattern");
        _displaysManager.ToggleLayerTestPattern(_selectedLayerId, DefaultTestPattern);
        LoadLayerProps();
    }

    private void OnLayerLockResetPressed()
    {
        if (_selectionKind != SelectionKind.Layer)
            return;
        RecordDisplaysHistory("Reset layer lock");
        _displaysManager.UpdateLayerLocked(_selectedLayerId, DefaultLocked);
        LoadLayerProps();
    }

    #endregion

}
