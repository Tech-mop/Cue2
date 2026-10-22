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
/// Partial: pick-target mode (drag from the pick button onto a cue).
/// </summary>
public partial class ControlComponentCard
{
    /// <summary>
    /// Starts pick on left-press. Accepts the event so BaseButton does not latch pressed;
    /// mouse-up is owned by <see cref="_Input"/> during pick mode.
    /// </summary>
    private void OnPickTargetButtonGuiInput(InputEvent @event)
    {
        if (@event is not InputEventMouseButton mb)
            return;
        if (mb.ButtonIndex != MouseButton.Left || !mb.Pressed)
            return;

        // Prevent BaseButton internal press-attempt (would stick if mouse-up is handled globally).
        AcceptEvent();
        ReleasePickButton();

        if (_component == null) return;
        if (_globalData?.HistoryManager?.IsRestoring == true) return;
        if (_pickActive) return;

        StartPickMode();
    }

    /// <summary>
    /// Clears residual pressed/focus state on the pick button after a pick session
    /// (mirrors <see cref="ShellBar.ReleaseDragGrabber"/>).
    /// </summary>
    private void ReleasePickButton()
    {
        if (_pickTargetButton == null || !IsInstanceValid(_pickTargetButton))
            return;

        _pickTargetButton.KeepPressedOutside = false;
        _pickTargetButton.SetPressedNoSignal(false);
        _pickTargetButton.ButtonPressed = false;
        if (_pickTargetButton.HasFocus())
            _pickTargetButton.ReleaseFocus();
    }

    private void StartPickMode()
    {
        if (_pickActive) return;

        _pickActive = true;
        SetProcessInput(true);
        SetProcess(true);

        EnsurePickBadge();
        if (_pickLayer != null)
            _pickLayer.Visible = true;

        // Crosshair / target cursor for the hold duration.
        try
        {
            DisplayServer.CursorSetShape(DisplayServer.CursorShape.Cross);
        }
        catch (Exception ex)
        {
            GD.Print($"ControlComponentCard:StartPickMode - Cursor shape: {ex.Message}");
        }

        UpdatePickCursorAndBadge();
        _globalSignals?.EmitSignal(nameof(GlobalSignals.Log),
            "Control: pick a cue — release over a shell (Esc to cancel)", (int)LogType.Info);
    }

    /// <summary>
    /// Ends pick mode. When <paramref name="applyTarget"/> is true, assigns the shell under the mouse.
    /// </summary>
    private void EndPickMode(bool applyTarget)
    {
        if (!_pickActive && _pickLayer == null && _pickHoverShell == null)
            return;

        ShellBar targetShell = null;
        if (applyTarget && _pickActive)
            targetShell = FindShellBarUnderMouse();

        ClearPickHoverHighlight();

        _pickActive = false;
        SetProcessInput(false);
        SetProcess(false);

        // Restore default cursor.
        try
        {
            DisplayServer.CursorSetShape(DisplayServer.CursorShape.Arrow);
        }
        catch
        {
            // Best-effort; some platforms ignore cursor shape.
        }

        // Critical: clear latched BaseButton state (mouse-up never reached the button).
        ReleasePickButton();

        if (_pickLayer != null && IsInstanceValid(_pickLayer))
        {
            _pickLayer.QueueFree();
            _pickLayer = null;
            _pickBadge = null;
            _pickBadgeLabel = null;
            _pickBadgeIcon = null;
        }

        if (applyTarget && targetShell != null)
            ApplyTargetFromShell(targetShell);
    }

    private void ApplyTargetFromShell(ShellBar shell)
    {
        if (shell == null || _component == null) return;

        int cueId = shell.CueId;
        if (IsSelfTarget(cueId) && _component.Action != ControlAction.Fade)
        {
            RejectSelfTarget();
            return;
        }

        var cue = CueList.FetchCueFromId(cueId);
        if (cue == null)
        {
            _globalSignals?.EmitSignal(nameof(GlobalSignals.Log),
                $"Control pick: cue id {cueId} not found", (int)LogType.Warning);
            return;
        }

        if (cue.Id == _component.TargetCueId &&
            string.Equals(_component.TargetCueNum ?? string.Empty, cue.CueNum ?? string.Empty, StringComparison.Ordinal))
        {
            RefreshFromComponent();
            return;
        }

        RecordHistory("Pick control target");
        _component.TargetCueId = cue.Id;
        _component.TargetCueNum = cue.CueNum ?? string.Empty;
        TryAutoRenameOwner();
        RefreshFromComponent();

        _globalSignals?.EmitSignal(nameof(GlobalSignals.Log),
            $"Control pick: target → #{_component.TargetCueNum} \"{cue.Name}\"", (int)LogType.Info);
    }

    private void EnsurePickBadge()
    {
        if (_pickLayer != null && IsInstanceValid(_pickLayer))
            return;

        _pickLayer = new CanvasLayer
        {
            Name = "ControlPickTargetLayer",
            Layer = 100
        };

        _pickBadge = new PanelContainer
        {
            MouseFilter = MouseFilterEnum.Ignore,
            Name = "PickBadge"
        };

        var style = new StyleBoxFlat
        {
            BgColor = new Color(0.08f, 0.08f, 0.1f, 0.92f),
            BorderColor = new Color(0.92f, 0.44f, 0.01f, 1f),
            BorderWidthLeft = 1,
            BorderWidthTop = 1,
            BorderWidthRight = 1,
            BorderWidthBottom = 1,
            CornerRadiusTopLeft = 4,
            CornerRadiusTopRight = 4,
            CornerRadiusBottomRight = 4,
            CornerRadiusBottomLeft = 4,
            ContentMarginLeft = 6,
            ContentMarginTop = 4,
            ContentMarginRight = 8,
            ContentMarginBottom = 4
        };
        _pickBadge.AddThemeStyleboxOverride("panel", style);

        var row = new HBoxContainer
        {
            MouseFilter = MouseFilterEnum.Ignore,
            // Small gap between cursor icon and label.
        };
        row.AddThemeConstantOverride("separation", 6);

        _pickBadgeIcon = new TextureRect
        {
            MouseFilter = MouseFilterEnum.Ignore,
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            CustomMinimumSize = new Vector2(16, 16)
        };
        var icon = GetThemeIcon("Target", "AtlasIcons");
        if (icon != null)
            _pickBadgeIcon.Texture = icon;
        row.AddChild(_pickBadgeIcon);

        _pickBadgeLabel = new Label
        {
            MouseFilter = MouseFilterEnum.Ignore,
            Text = "Pick cue…"
        };
        _pickBadgeLabel.AddThemeColorOverride("font_color", new Color(0.95f, 0.95f, 0.95f, 1f));
        _pickBadgeLabel.AddThemeFontSizeOverride("font_size", 12);
        row.AddChild(_pickBadgeLabel);

        _pickBadge.AddChild(row);
        _pickLayer.AddChild(_pickBadge);
        GetTree().Root.AddChild(_pickLayer);
    }

    private void UpdatePickCursorAndBadge()
    {
        // Keep crosshair sticky while over other controls that set their own cursor.
        try
        {
            DisplayServer.CursorSetShape(DisplayServer.CursorShape.Cross);
        }
        catch
        {
            // ignore
        }

        var shell = FindShellBarUnderMouse();
        SetPickHoverHighlight(shell);

        if (_pickBadge == null || !IsInstanceValid(_pickBadge))
            return;

        Vector2 mouse = GetViewport().GetMousePosition();
        // Offset so the badge sits just below-right of the cursor tip.
        _pickBadge.Position = mouse + new Vector2(18, 18);

        if (shell != null)
        {
            if (IsSelfTarget(shell.CueId) && _component?.Action != ControlAction.Fade)
            {
                _pickBadgeLabel.Text = UiLocalizer.T("Cannot target self");
            }
            else
            {
                var cue = CueList.FetchCueFromId(shell.CueId);
                string num = cue?.CueNum ?? shell.CueId.ToString();
                string name = cue?.Name ?? "(unknown)";
                string action = _component != null
                    ? ControlComponent.GetActionDisplayName(_component.Action)
                    : "Target";
                _pickBadgeLabel.Text = $"{action} → #{num}  {name}";
            }
        }
        else
        {
            _pickBadgeLabel.Text = UiLocalizer.T("Pick cue…");
        }
    }

    private void SetPickHoverHighlight(ShellBar shell)
    {
        if (_pickHoverShell == shell)
            return;

        ClearPickHoverHighlight();

        if (shell == null || !IsInstanceValid(shell))
            return;

        _pickHoverShell = shell;
        _pickHoverRestoreModulate = shell.Modulate;
        // Warm tint so the drop target is obvious under the crosshair.
        shell.Modulate = new Color(1.15f, 1.05f, 0.85f, 1f);
    }

    private void ClearPickHoverHighlight()
    {
        if (_pickHoverShell != null && IsInstanceValid(_pickHoverShell))
            _pickHoverShell.Modulate = _pickHoverRestoreModulate;
        _pickHoverShell = null;
        _pickHoverRestoreModulate = Colors.White;
    }

    /// <summary>
    /// Walks from the control under the mouse up the parent chain to find a <see cref="ShellBar"/>.
    /// </summary>
    private ShellBar FindShellBarUnderMouse()
    {
        var vp = GetViewport();
        if (vp == null) return null;

        Control hovered = vp.GuiGetHoveredControl();
        while (hovered != null)
        {
            if (hovered is ShellBar shell)
                return shell;
            hovered = hovered.GetParent() as Control;
        }

        return null;
    }
}
