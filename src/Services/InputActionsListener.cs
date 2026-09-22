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
using Godot;

namespace Cue2.Services;

/// <summary>
/// Listens for project InputMap actions, cue hotkey triggers, and wall-clock cue triggers.
/// Pauses app shortcuts while text fields, rebind UIs, or OptionButton dropdowns have focus.
/// </summary>
public partial class InputActionsListener : Node
{
    private GlobalSignals _globalSignals;
    private GlobalData _globalData;
    private Timer _focusExitTimer;
    private Timer _clockPollTimer;

    private bool _listenForInput = true;

    /// <summary>Previous local time sample for wall-clock edge detection (unset until first poll).</summary>
    private DateTime? _lastClockPollLocal;

    // Data-driven mapping to avoid long chain of if statements.
    // Key = input action name, Value = (handler, useExactMatch)
    private readonly System.Collections.Generic.Dictionary<string, (System.Action handler, bool exact)> _actionMap = new();

    public override void _Ready()
    {
        _globalSignals = GetNode<GlobalSignals>("/root/GlobalSignals");
        _globalData = GetNodeOrNull<GlobalData>("/root/GlobalData");

        _focusExitTimer = new Timer { WaitTime = 0.1, OneShot = true };
        AddChild(_focusExitTimer);
        _focusExitTimer.Timeout += OnFocusExitTimerTimeout;

        // 10 Hz is plenty for wall-clock cue triggers (time-of-day GO).
        _clockPollTimer = new Timer { WaitTime = 0.1, OneShot = false, Autostart = true };
        AddChild(_clockPollTimer);
        _clockPollTimer.Timeout += PollClockTriggers;

        // Signals sent from all text edit feilds when focused. This is used to toggle input actions.
        _globalSignals.TextEditFocusEntered += SetListeningFalse;
        _globalSignals.TextEditFocusExited += OnTextEditFocusExited;

        // Seed clock poll so we do not retroactively fire cues that already passed today.
        _lastClockPollLocal = DateTime.Now;

        RegisterActions();
    }

    /// <inheritdoc />
    public override void _ExitTree()
    {
        if (_clockPollTimer != null)
            _clockPollTimer.Timeout -= PollClockTriggers;
        if (_focusExitTimer != null)
            _focusExitTimer.Timeout -= OnFocusExitTimerTimeout;
        if (_globalSignals != null)
        {
            _globalSignals.TextEditFocusEntered -= SetListeningFalse;
            _globalSignals.TextEditFocusExited -= OnTextEditFocusExited;
        }
        base._ExitTree();
    }

    private void RegisterActions()
    {
        Register("NewSession", nameof(GlobalSignals.NewSession), "New Session", true);
        Register("OpenSession", nameof(GlobalSignals.OpenSession), "Open Session", true);
        Register("SaveSession", nameof(GlobalSignals.Save), "Save", true);
        Register("SaveAsSession", nameof(GlobalSignals.SaveAs), "Save As", true);
        Register("Go", nameof(GlobalSignals.Go), "Go", false);
        Register("StopAll", nameof(GlobalSignals.StopAll), "Stop All", false);
        Register("CreateCue", nameof(GlobalSignals.CreateCue), "Create Cue", true);
        Register("DeleteCue", nameof(GlobalSignals.DeleteSelectedCues), "Delete Selected Cues", true);
        Register("DuplicateSelectedCues", nameof(GlobalSignals.DuplicateSelectedCues), "Duplicate Selected Cues", true);
        Register("CutSelectedCues", nameof(GlobalSignals.CutSelectedCues), "Cut Selected Cues", true);
        Register("CopySelectedCues", nameof(GlobalSignals.CopySelectedCues), "Copy Selected Cues", true);
        Register("PasteCues", nameof(GlobalSignals.PasteCues), "Paste Cues", true);
        Register("GroupSelectedCues", nameof(GlobalSignals.GroupSelectedCues), "Group Selected Cues", true);
        Register("SelectAll", nameof(GlobalSignals.SelectAllCues), "Select All Cues", true);
        Register("SelectNext", nameof(GlobalSignals.SelectNextCue), "Select Next Cue", true);
        Register("SelectPrevious", nameof(GlobalSignals.SelectPreviousCue), "Select Previous Cue", true);
        Register("PauseAll", nameof(GlobalSignals.PauseAll), "Pause All Cues", true);
        Register("ResumeAll", nameof(GlobalSignals.ResumeAll), "Resume All Cues", true);
        Register("ToggleSettings", nameof(GlobalSignals.ToggleSettingsWindow), "Toggle Settings Window", true);
        Register("ToggleLog", nameof(GlobalSignals.ToggleLogWindow), "Toggle Log Window", true);
        Register("ToggleShowMode", nameof(GlobalSignals.ToggleShowMode), "Toggle Show Mode", true);
        Register("EditMode", nameof(GlobalSignals.EnterEditMode), "Edit Mode", true);
        Register("ShowMode", nameof(GlobalSignals.EnterShowMode), "Show Mode", true);
        Register("ExpandOneLayer", nameof(GlobalSignals.CuelistExpandOneLayer), "Expand One Group Layer", true);
        Register("CollapseOneLayer", nameof(GlobalSignals.CuelistCollapseOneLayer), "Collapse One Group Layer", true);
        Register("ToggleExpandAll", nameof(GlobalSignals.ToggleExpandAll), "Toggle Expand/Collapse Groups", true);
        // Undo/Redo are handled separately so they still work while a LineEdit/SpinBox has focus.
    }

    private void Register(string action, string signalName, string logName, bool exact)
    {
        _actionMap[action] = (() =>
        {
            GD.Print($"InputActionsListener:Actions - Input Action: {logName}");
            _globalSignals.EmitSignal(signalName);
        }, exact);
    }

    /// <summary>
    /// Invokes the same handler as the keyboard InputMap for <paramref name="actionName"/>
    /// (used by MIDI / OSC Input Map bindings). Undo/Redo always fire even when typing-focused.
    /// Other actions respect the text-field listen gate unless <paramref name="ignoreFocusGate"/> is true
    /// (OSC show control should always work).
    /// </summary>
    /// <param name="actionName">Project InputMap action name (e.g. "Go").</param>
    /// <param name="ignoreFocusGate">When true, fire even if a LineEdit has focus (OSC).</param>
    /// <returns><c>true</c> when a handler was invoked.</returns>
    public bool TryTriggerAction(string actionName, bool ignoreFocusGate = false)
    {
        if (string.IsNullOrEmpty(actionName)) return false;

        // Undo/Redo always available (same as keyboard path).
        if (actionName == "Undo")
        {
            GD.Print("InputActionsListener:TryTriggerAction - External Action: Undo");
            _globalSignals.EmitSignal(nameof(GlobalSignals.Undo));
            return true;
        }
        if (actionName == "Redo")
        {
            GD.Print("InputActionsListener:TryTriggerAction - External Action: Redo");
            _globalSignals.EmitSignal(nameof(GlobalSignals.Redo));
            return true;
        }

        if (!ignoreFocusGate && !_listenForInput) return false;

        if (_actionMap.TryGetValue(actionName, out var entry))
        {
            entry.handler();
            return true;
        }

        return false;
    }

    public override void _Process(double delta)
    {
        // Undo/Redo must poll globally: LineEdit consumes Ctrl+Z so _UnhandledKeyInput never sees it.
        if (!Input.IsAnythingPressed())
            return;

        if (Input.IsActionJustPressed("Undo", true))
        {
            GD.Print("InputActionsListener:Actions - Input Action: Undo");
            _globalSignals.EmitSignal(nameof(GlobalSignals.Undo));
            return;
        }
        if (Input.IsActionJustPressed("Redo", true))
        {
            GD.Print("InputActionsListener:Actions - Input Action: Redo");
            _globalSignals.EmitSignal(nameof(GlobalSignals.Redo));
        }
    }

    /// <summary>
    /// Fires enabled wall-clock cue triggers when local time crosses their target time of day.
    /// Each cue GO's at most once per crossing (typically once per day). Does not move playhead.
    /// </summary>
    private void PollClockTriggers()
    {
        var now = DateTime.Now;
        if (_lastClockPollLocal is not DateTime previous)
        {
            _lastClockPollLocal = now;
            return;
        }

        // Avoid zero-length or reverse samples (clock adjustments / same tick).
        if (now <= previous)
        {
            if (now < previous)
                _lastClockPollLocal = now; // clock went backwards — resync without firing
            return;
        }

        try
        {
            if (CueList.CueIndex == null || CueList.CueIndex.Count == 0)
                return;

            var executor = _globalData?.CueCommandExecutor;
            if (executor == null)
                return;

            foreach (Cue cue in CueList.CueIndex.Values)
            {
                if (cue == null || !cue.CanFireClock) continue;
                if (!cue.ClockCrossedBetween(previous, now)) continue;

                string display = cue.GetClockDisplay();
                GD.Print($"InputActionsListener:PollClockTriggers - Clock GO: \"{cue.Name}\" (id={cue.Id}) at {display}");
                _globalSignals?.EmitSignal(nameof(GlobalSignals.Log),
                    $"Clock trigger: \"{cue.Name}\" at {display}", (int)LogType.Info);
                executor.ActivateSequenceFrom(cue);
            }
        }
        finally
        {
            _lastClockPollLocal = now;
        }
    }

    /// <summary>
    /// Fires InputMap shortcuts on the key-press frame without a per-frame action scan.
    /// </summary>
    private void TryHandleInputMapActions()
    {
        foreach (var kvp in _actionMap)
        {
            if (Input.IsActionJustPressed(kvp.Key, kvp.Value.exact))
                kvp.Value.handler();
        }
    }

    /// <summary>
    /// Fires enabled cue hotkeys that match a newly pressed key combo.
    /// Multiple cues may share a hotkey; each armed+enabled match is GO'd without moving playhead.
    /// </summary>
    public override void _UnhandledKeyInput(InputEvent @event)
    {
        if (@event is not InputEventKey keyEvent || !keyEvent.Pressed || keyEvent.Echo) return;

        if (_listenForInput)
            TryHandleInputMapActions();

        if (!_listenForInput) return;
        if (IsModifierOnlyKey(keyEvent.Keycode)) return;

        if (CueList.CueIndex == null || CueList.CueIndex.Count == 0) return;

        var executor = _globalData?.CueCommandExecutor;
        if (executor == null) return;

        bool any = false;
        foreach (Cue cue in CueList.CueIndex.Values)
        {
            if (cue == null || !cue.CanFireHotkey) continue;
            if (!cue.HotkeyMatches(keyEvent)) continue;

            GD.Print($"InputActionsListener:_UnhandledInput - Cue hotkey GO: \"{cue.Name}\" (id={cue.Id})");
            executor.ActivateSequenceFrom(cue);
            any = true;
        }

        if (any)
            GetViewport().SetInputAsHandled();
    }

    private static bool IsModifierOnlyKey(Key keycode)
    {
        return keycode is Key.Ctrl or Key.Shift or Key.Alt or Key.Meta;
    }

    private void SetListening(bool listening) => _listenForInput = listening;

    private void OnTextEditFocusExited()
    {
        _focusExitTimer.Start();
    }

    private void OnFocusExitTimerTimeout()
    {
        // A LineEdit blur can start this timer while an OptionButton list is still open,
        // or a popup search field can miss focus_exited. Keep waiting until both are clear.
        if (_globalSignals != null && _globalSignals.IsInputMapPaused())
        {
            _focusExitTimer.Start();
            return;
        }

        SetListening(true);
    }

    private void SetListeningFalse()
    { 
        _focusExitTimer.Stop(); // Stop the timer if focus entered again
        SetListening(false);
    }
}

