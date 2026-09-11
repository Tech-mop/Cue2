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

namespace Cue2.UI.Settings;

/// <summary>
/// Canvas editor UI for arranging screens and target layers on the video canvas.
/// Left: Screens + Target Layers trees. Center: interactive stage (move/resize). Right: properties.
/// </summary>
/// <summary>
/// Partial: Screen property handlers and create/delete
/// </summary>
public partial class SettingsCanvasEditor
{
    #region Screen property handlers

    private VideoOutputDevice GetSelectedScreen()
    {
        if (_selectionKind != SelectionKind.Screen)
            return null;
        return _displaysManager.GetOutputById(_selectedScreenId);
    }

    private void OnScreenNameSubmitted(string text)
    {
        if (_isUpdatingProps)
        {
            _screenNameLineEdit.ReleaseFocus();
            return;
        }

        var screen = GetSelectedScreen();
        if (screen == null)
        {
            _screenNameLineEdit.ReleaseFocus();
            return;
        }

        string next = (text ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(next) || next == screen.OutputName)
        {
            _screenNameLineEdit.Text = screen.OutputName;
            _screenNameLineEdit.ReleaseFocus();
            return;
        }

        RecordDisplaysHistory("Rename screen");
        _displaysManager.UpdateScreenName(screen.OutputId, next);
        RebuildTrees();
        UpdateCanvasGizmos();
        _screenNameLineEdit.ReleaseFocus();
    }

    private void OnScreenOutputSelected(long index)
    {
        if (_isUpdatingProps)
            return;

        var screen = GetSelectedScreen();
        if (screen == null)
            return;

        int i = (int)index;
        if (i < 0 || i >= _outputOptionMonitorMap.Count)
            return;

        int monitor = _outputOptionMonitorMap[i];
        if (screen.TargetMonitor == monitor)
            return;

        RecordDisplaysHistory("Change screen output");
        _displaysManager.UpdateScreenTargetMonitor(screen.OutputId, monitor);
        RebuildTrees();
        if (_selectionKind == SelectionKind.Screen)
            LoadScreenProps();
        UpdateCanvasGizmos();
    }

    private void OnScreenSizeXSubmitted(string text)
    {
        var screen = GetSelectedScreen();
        if (_isUpdatingProps || screen == null)
        {
            _outputSizeXLineEdit.ReleaseFocus();
            return;
        }

        try
        {
            int val = int.Parse(text);
            Vector2I size = screen.KeepAspect
                ? SizeWithKeepAspect(screen.OutputSize, val, null)
                : new Vector2I(val, screen.OutputSize.Y);
            if (size == screen.OutputSize)
            {
                _outputSizeXLineEdit.ReleaseFocus();
                return;
            }
            RecordDisplaysHistory("Change screen size");
            _displaysManager.UpdateOutputSize(screen.OutputId, size);
            LoadScreenProps();
            UpdateCanvasGizmos();
        }
        catch (FormatException)
        {
            _outputSizeXLineEdit.Text = screen.OutputSize.X.ToString();
        }

        _outputSizeXLineEdit.ReleaseFocus();
    }

    private void OnScreenSizeYSubmitted(string text)
    {
        var screen = GetSelectedScreen();
        if (_isUpdatingProps || screen == null)
        {
            _outputSizeYLineEdit.ReleaseFocus();
            return;
        }

        try
        {
            int val = int.Parse(text);
            Vector2I size = screen.KeepAspect
                ? SizeWithKeepAspect(screen.OutputSize, null, val)
                : new Vector2I(screen.OutputSize.X, val);
            if (size == screen.OutputSize)
            {
                _outputSizeYLineEdit.ReleaseFocus();
                return;
            }
            RecordDisplaysHistory("Change screen size");
            _displaysManager.UpdateOutputSize(screen.OutputId, size);
            LoadScreenProps();
            UpdateCanvasGizmos();
        }
        catch (FormatException)
        {
            _outputSizeYLineEdit.Text = screen.OutputSize.Y.ToString();
        }

        _outputSizeYLineEdit.ReleaseFocus();
    }

    private void OnScreenPosXSubmitted(string text)
    {
        var screen = GetSelectedScreen();
        if (_isUpdatingProps || screen == null)
        {
            _outputPosXLineEdit.ReleaseFocus();
            return;
        }

        try
        {
            int val = int.Parse(text);
            if (val == screen.CanvasPosition.X)
            {
                _outputPosXLineEdit.ReleaseFocus();
                return;
            }
            RecordDisplaysHistory("Change screen position");
            _displaysManager.UpdateOutputCanvasPosition(screen.OutputId, new Vector2I(val, screen.CanvasPosition.Y));
            LoadScreenProps();
            UpdateCanvasGizmos();
        }
        catch (FormatException)
        {
            _outputPosXLineEdit.Text = screen.CanvasPosition.X.ToString();
        }

        _outputPosXLineEdit.ReleaseFocus();
    }

    private void OnScreenPosYSubmitted(string text)
    {
        var screen = GetSelectedScreen();
        if (_isUpdatingProps || screen == null)
        {
            _outputPosYLineEdit.ReleaseFocus();
            return;
        }

        try
        {
            int val = int.Parse(text);
            if (val == screen.CanvasPosition.Y)
            {
                _outputPosYLineEdit.ReleaseFocus();
                return;
            }
            RecordDisplaysHistory("Change screen position");
            _displaysManager.UpdateOutputCanvasPosition(screen.OutputId, new Vector2I(screen.CanvasPosition.X, val));
            LoadScreenProps();
            UpdateCanvasGizmos();
        }
        catch (FormatException)
        {
            _outputPosYLineEdit.Text = screen.CanvasPosition.Y.ToString();
        }

        _outputPosYLineEdit.ReleaseFocus();
    }

    private void OnDisplayOffsetXSubmitted(string text)
    {
        var screen = GetSelectedScreen();
        if (_isUpdatingProps || screen == null)
        {
            _displayOffsetXLineEdit.ReleaseFocus();
            return;
        }

        try
        {
            int val = int.Parse(text);
            if (val == screen.DisplayOffset.X)
            {
                _displayOffsetXLineEdit.ReleaseFocus();
                return;
            }
            RecordDisplaysHistory("Change screen display offset");
            _displaysManager.UpdateScreenDisplayOffset(screen.OutputId, new Vector2I(val, screen.DisplayOffset.Y));
            UpdateScreenResetButtons(screen);
        }
        catch (FormatException)
        {
            _displayOffsetXLineEdit.Text = screen.DisplayOffset.X.ToString();
        }

        _displayOffsetXLineEdit.ReleaseFocus();
    }

    private void OnDisplayOffsetYSubmitted(string text)
    {
        var screen = GetSelectedScreen();
        if (_isUpdatingProps || screen == null)
        {
            _displayOffsetYLineEdit.ReleaseFocus();
            return;
        }

        try
        {
            int val = int.Parse(text);
            if (val == screen.DisplayOffset.Y)
            {
                _displayOffsetYLineEdit.ReleaseFocus();
                return;
            }
            RecordDisplaysHistory("Change screen display offset");
            _displaysManager.UpdateScreenDisplayOffset(screen.OutputId, new Vector2I(screen.DisplayOffset.X, val));
            UpdateScreenResetButtons(screen);
        }
        catch (FormatException)
        {
            _displayOffsetYLineEdit.Text = screen.DisplayOffset.Y.ToString();
        }

        _displayOffsetYLineEdit.ReleaseFocus();
    }

    private void OnScreenKeepAspectToggled(bool toggled)
    {
        if (_isUpdatingProps)
            return;

        var screen = GetSelectedScreen();
        if (screen == null)
            return;

        RecordDisplaysHistory(toggled ? "Enable screen keep-aspect" : "Disable screen keep-aspect");
        _displaysManager.UpdateScreenKeepAspect(screen.OutputId, toggled);
        UpdateScreenResetButtons(screen);
    }

    private void OnScreenTransparentToggled(bool toggled)
    {
        if (_isUpdatingProps)
            return;

        var screen = GetSelectedScreen();
        if (screen == null)
            return;
        RecordDisplaysHistory(toggled ? "Enable screen transparency" : "Disable screen transparency");
        screen.SetTransparent(toggled);
        UpdateScreenResetButtons(screen);
    }

    private void OnScreenTestPatternToggled(bool toggled)
    {
        if (_isUpdatingProps)
            return;

        var screen = GetSelectedScreen();
        if (screen == null)
            return;
        RecordDisplaysHistory(toggled ? "Enable screen test pattern" : "Disable screen test pattern");
        screen.ToggleTestPattern(toggled);
        UpdateScreenResetButtons(screen);
    }

    private void OnDeleteScreenPressed()
    {
        if (_selectionKind != SelectionKind.Screen)
            return;

        if (DisplaysManager.Screens.Count <= 1)
        {
            _globalSignals.EmitSignal(nameof(GlobalSignals.Log),
                "Cannot delete the last screen.", 1);
            return;
        }

        RecordDisplaysHistory("Delete screen");
        _displaysManager.RemoveOutput(_selectedScreenId);
        RebuildTrees(selectCanvas: true);
        UpdateCanvasGizmos();
    }

    private void OnNewScreenPressed()
    {
        RecordDisplaysHistory("Create screen");
        string name = $"Screen {DisplaysManager.Screens.Count + 1}";
        var screen = _displaysManager.AddScreen(name, VideoOutputDevice.VirtualMonitorIndex);
        RebuildTrees();
        SelectScreenInTree(screen.OutputId);
        ApplySelection(SelectionKind.Screen, screen.OutputId, -1);
        UpdateCanvasGizmos();
    }

    /// <summary>
    /// Re-checks all screens/outputs: restores closed portable windows, re-places physical
    /// outputs, and refreshes the available-display list.
    /// </summary>
    private void OnRefreshScreensPressed()
    {
        _displaysManager.RefreshAllScreens();
        RebuildTrees();
        if (_selectionKind == SelectionKind.Screen && _selectedScreenId >= 0)
        {
            SelectScreenInTree(_selectedScreenId);
            LoadScreenProps();
        }
        else if (_selectionKind == SelectionKind.Layer && _selectedLayerId >= 0)
        {
            SelectLayerInTree(_selectedLayerId);
            LoadLayerProps();
        }

        UpdateCanvasGizmos();
        _globalSignals.EmitSignal(nameof(GlobalSignals.Log),
            "Canvas Editor: Screens refreshed.", 0);
    }

    #endregion

}
