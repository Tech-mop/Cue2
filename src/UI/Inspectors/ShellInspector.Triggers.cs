// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

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
using Cue2.Domain.Timecode;
using Cue2.Services;
using Cue2.UI.Controls;
using Cue2.UI.Utilities;
using Godot;

namespace Cue2.UI.Inspectors;

/// <summary>
/// Partial: hotkey, clock, OSC, timecode and MIDI trigger editors.
/// </summary>
public partial class ShellInspector
{
	/// <summary>
	/// Pushes hotkey enable / binding / reset-button state from the focused cue (single-edit).
	/// Does not cancel an in-progress listen unless the cue changed via a full field refresh path.
	/// </summary>
	private void RefreshHotkeyUi()
	{
		if (_isMultiEdit) return;

		if (_hotkeyEnabledCheckBox != null)
			_hotkeyEnabledCheckBox.SetPressedNoSignal(_focusedCue?.HotkeyEnabled == true);

		if (_hotkeyBindingButton != null && !_isListeningForHotkey)
		{
			if (_focusedCue == null || !_focusedCue.HasHotkey)
				_hotkeyBindingButton.Text = UiLocalizer.T("None");
			else
				_hotkeyBindingButton.Text = _focusedCue.GetHotkeyDisplay();
		}

		if (_hotkeyResetButton != null)
		{
			bool nonDefault = _focusedCue != null && _focusedCue.IsHotkeyNonDefault;
			_hotkeyResetButton.Visible = nonDefault;
			if (nonDefault)
				_hotkeyResetButton.TooltipText = UiLocalizer.T("Reset to default (no hotkey)");
		}
	}

	/// <summary>
	/// Toggles whether the cue hotkey is active for edit target(s).
	/// </summary>
	private void OnHotkeyEnabledToggled(bool pressed)
	{
		if (_isRefreshingUi) return;
		if (_globalData?.HistoryManager?.IsRestoring == true) return;

		var targets = GetEditTargets();
		if (targets.Count == 0) return;

		if (!_isMultiEdit && targets.Count == 1 && targets[0].HotkeyEnabled == pressed)
			return;

		RecordHistoryBeforeEdit(
			pressed ? "Enable cue hotkey" : "Disable cue hotkey",
			pressed ? "Multi-edit enable cue hotkey" : "Multi-edit disable cue hotkey");

		foreach (var cue in targets)
		{
			if (cue.HotkeyEnabled == pressed) continue;
			cue.HotkeyEnabled = pressed;
		}

		if (!_isMultiEdit)
			RefreshHotkeyUi();
		else if (_hotkeyResetButton != null)
			_hotkeyResetButton.Visible = targets.Exists(c => c.IsHotkeyNonDefault);
	}

	private void OnHotkeyBindingButtonPressed()
	{
		if (_isRefreshingUi) return;
		if (_globalData?.HistoryManager?.IsRestoring == true) return;

		var targets = GetEditTargets();
		if (targets.Count == 0) return;

		if (_isListeningForHotkey)
		{
			CancelHotkeyListening();
			return;
		}

		StartHotkeyListening();
	}

	private void OnHotkeyResetPressed()
	{
		if (_isRefreshingUi) return;
		if (_globalData?.HistoryManager?.IsRestoring == true) return;

		var targets = GetEditTargets();
		if (targets.Count == 0) return;

		// Skip no-op when already default.
		if (!_isMultiEdit && targets.Count == 1 && !targets[0].IsHotkeyNonDefault)
			return;

		if (_isListeningForHotkey)
			CancelHotkeyListening();

		RecordHistoryBeforeEdit("Reset cue hotkey", "Multi-edit reset cue hotkey");
		foreach (var cue in targets)
			cue.ResetHotkeyToDefault();

		if (!_isMultiEdit)
			RefreshHotkeyUi();
		else if (_hotkeyBindingButton != null)
			_hotkeyBindingButton.Text = MultiPlaceholder;

		if (_hotkeyEnabledCheckBox != null)
			_hotkeyEnabledCheckBox.SetPressedNoSignal(false);
		if (_hotkeyResetButton != null)
			_hotkeyResetButton.Visible = false;

		GD.Print("ShellInspector:OnHotkeyResetPressed - Reset cue hotkey to default (none).");
	}

	private void StartHotkeyListening()
	{
		_isListeningForHotkey = true;
		if (_hotkeyBindingButton != null)
			_hotkeyBindingButton.Text = UiLocalizer.T("Press key... (Esc cancels)");
		// Pause global input action listener while capturing (same coordination as InputActionCard).
		_globalSignals?.EmitSignal(nameof(GlobalSignals.TextEditFocusEntered));
		GD.Print("ShellInspector:StartHotkeyListening - Listening for cue hotkey");
	}

	/// <summary>
	/// Cancels an in-progress hotkey rebind, if any.
	/// </summary>
	private void CancelHotkeyListening()
	{
		if (!_isListeningForHotkey) return;
		_isListeningForHotkey = false;
		_globalSignals?.EmitSignal(nameof(GlobalSignals.TextEditFocusExited));

		if (_isMultiEdit)
		{
			if (_hotkeyBindingButton != null)
				_hotkeyBindingButton.Text = MultiPlaceholder;
		}
		else
		{
			RefreshHotkeyUi();
		}

		GD.Print("ShellInspector:CancelHotkeyListening - Cancelled cue hotkey listen");
	}

	public override void _UnhandledKeyInput(InputEvent @event)
	{
		// Esc cancels MIDI capture even when not hotkey-listening.
		if (_isCapturingMidi &&
		    @event is InputEventKey escKey && escKey.Pressed && escKey.Keycode == Key.Escape)
		{
			CancelMidiCapture();
			GetViewport().SetInputAsHandled();
			return;
		}

		if (!_isListeningForHotkey) return;
		if (@event is not InputEventKey keyEvent || !keyEvent.Pressed) return;
		if (IsModifierOnlyKey(keyEvent.Keycode)) return;

		if (keyEvent.Keycode == Key.Escape)
		{
			CancelHotkeyListening();
			GetViewport().SetInputAsHandled();
			return;
		}

		GetViewport().SetInputAsHandled();

		// Reject combos already used by app InputMap actions.
		string conflict = GlobalData.FindConflictingInputAction(null, keyEvent);
		if (!string.IsNullOrEmpty(conflict))
		{
			string combo = GlobalData.FormatInputEvent(keyEvent);
			_globalSignals?.EmitSignal(nameof(GlobalSignals.Log),
				$"Hotkey '{combo}' is already used by '{conflict}'", (int)LogType.Warning);
			GD.Print($"ShellInspector:_UnhandledInput - Rejected '{combo}'; used by '{conflict}'");
			if (_hotkeyBindingButton != null)
				_hotkeyBindingButton.Text = UiLocalizer.T("Press key... (Esc cancels)");
			return;
		}

		ApplyHotkeyBinding(keyEvent);
		_isListeningForHotkey = false;
		_globalSignals?.EmitSignal(nameof(GlobalSignals.TextEditFocusExited));

		if (!_isMultiEdit)
			RefreshHotkeyUi();
		else
		{
			if (_hotkeyBindingButton != null)
				_hotkeyBindingButton.Text = GlobalData.FormatInputEvent(keyEvent);
			if (_hotkeyEnabledCheckBox != null)
				_hotkeyEnabledCheckBox.SetPressedNoSignal(true);
			if (_hotkeyResetButton != null)
				_hotkeyResetButton.Visible = true;
		}

		GD.Print($"ShellInspector:_UnhandledInput - Set cue hotkey to {GlobalData.FormatInputEvent(keyEvent)}");
	}

	/// <summary>
	/// Applies a captured key binding to edit target(s) and enables the hotkey.
	/// </summary>
	private void ApplyHotkeyBinding(InputEventKey keyEvent)
	{
		var targets = GetEditTargets();
		if (targets.Count == 0) return;
		if (_globalData?.HistoryManager?.IsRestoring == true) return;

		RecordHistoryBeforeEdit("Set cue hotkey", "Multi-edit set cue hotkey");
		foreach (var cue in targets)
		{
			cue.SetHotkey(keyEvent);
			// Binding a key implies the user wants it active.
			if (!cue.HotkeyEnabled)
				cue.HotkeyEnabled = true;
		}
	}

	private static bool IsModifierOnlyKey(Key keycode)
	{
		return keycode is Key.Ctrl or Key.Shift or Key.Alt or Key.Meta;
	}

	// ── Clock trigger UI ────────────────────────────────────────────────────

	/// <summary>
	/// Builds the weekday popup panel with checkboxes (opened from the Days button).
	/// </summary>
	private void BuildClockDaysPopup()
	{
		_clockDaysPopup = new PopupPanel
		{
			Name = "ClockDaysPopup",
			// Transparent enough that the panel style shows; content padded below.
		};
		AddChild(_clockDaysPopup);

		var margin = new MarginContainer();
		margin.AddThemeConstantOverride("margin_left", 8);
		margin.AddThemeConstantOverride("margin_top", 6);
		margin.AddThemeConstantOverride("margin_right", 8);
		margin.AddThemeConstantOverride("margin_bottom", 6);
		_clockDaysPopup.AddChild(margin);

		var vbox = new VBoxContainer();
		vbox.AddThemeConstantOverride("separation", 2);
		margin.AddChild(vbox);

		var title = new Label
		{
			Text = "Active days",
			HorizontalAlignment = HorizontalAlignment.Left,
		};
		title.AddThemeFontSizeOverride("font_size", 10);
		vbox.AddChild(title);

		for (int i = 0; i < ClockDaysDisplayOrder.Length; i++)
		{
			var (day, label) = ClockDaysDisplayOrder[i];
			var box = new CheckBox
			{
				Text = label,
				ButtonPressed = true,
				FocusMode = FocusModeEnum.None,
			};
			box.AddThemeFontSizeOverride("font_size", 11);
			var capturedDay = day;
			box.Toggled += pressed => OnClockDayToggled(capturedDay, pressed);
			vbox.AddChild(box);
			_clockDayChecks[i] = (box, day);
		}

		// Keep popup open while toggling; hide when focus leaves the popup area.
		_clockDaysPopup.PopupHide += OnClockDaysPopupHide;
	}

	/// <summary>
	/// Opens the weekday multi-select popup under the Days button.
	/// </summary>
	private void OnClockDaysButtonPressed()
	{
		if (_clockDaysPopup == null || _clockDaysButton == null) return;

		if (_clockDaysPopup.Visible)
		{
			_clockDaysPopup.Hide();
			return;
		}

		// Sync checkbox state from current edit targets before showing.
		SyncClockDayCheckboxesFromModel();
		UiUtilities.PopupScaled(_clockDaysPopup, _clockDaysButton, new Vector2(120, 10));
	}

	private void OnClockDaysPopupHide()
	{
		// Refresh summary label when the picker closes.
		if (!_isMultiEdit)
			RefreshClockDaysButtonLabel();
		else if (_clockDaysButton != null)
			_clockDaysButton.Text = MultiPlaceholder;
	}

	/// <summary>
	/// Pushes day checkbox pressed state from the focused cue (or every-day default).
	/// </summary>
	private void SyncClockDayCheckboxesFromModel()
	{
		_isRefreshingUi = true;
		try
		{
			foreach (var (box, day) in _clockDayChecks)
			{
				if (box == null) continue;
				bool on;
				if (_isMultiEdit)
				{
					// Multi: show intersection? Prefer focused/last if available, else all on.
					on = _focusedCue?.IsClockDayEnabled(day) ?? true;
				}
				else
				{
					on = _focusedCue == null || _focusedCue.IsClockDayEnabled(day);
					if (_focusedCue == null)
						on = true;
				}
				box.SetPressedNoSignal(on);
			}
		}
		finally
		{
			_isRefreshingUi = false;
		}
	}

	/// <summary>
	/// Updates the Days button caption from the focused cue's weekday mask.
	/// </summary>
	private void RefreshClockDaysButtonLabel()
	{
		if (_clockDaysButton == null) return;
		if (_isMultiEdit)
		{
			_clockDaysButton.Text = MultiPlaceholder;
			return;
		}

		byte mask = _focusedCue?.ClockDaysMask ?? Cue.ClockDaysAll;
		_clockDaysButton.Text = FormatClockDaysSummary(mask);
		_clockDaysButton.TooltipText = UiLocalizer.T("Choose which weekdays the clock trigger may fire.\nCurrent: ") +
		                               FormatClockDaysSummary(mask);
	}

	/// <summary>
	/// Compact label for a weekday mask (e.g. "Every day", "Weekdays", "Mon, Fri").
	/// </summary>
	private static string FormatClockDaysSummary(byte mask)
	{
		mask = (byte)(mask & Cue.ClockDaysAll);
		if (mask == Cue.ClockDaysAll)
			return "Every day";
		if (mask == 0)
			return "No days";

		// Mon–Fri bits (DayOfWeek Mon=1 … Fri=5) => 0b0111110 = 0x3E
		const byte weekdays = (1 << (int)DayOfWeek.Monday) |
		                      (1 << (int)DayOfWeek.Tuesday) |
		                      (1 << (int)DayOfWeek.Wednesday) |
		                      (1 << (int)DayOfWeek.Thursday) |
		                      (1 << (int)DayOfWeek.Friday);
		// Sat+Sun => 0b1000001 = 0x41
		const byte weekend = (1 << (int)DayOfWeek.Saturday) | (1 << (int)DayOfWeek.Sunday);

		if (mask == weekdays)
			return "Weekdays";
		if (mask == weekend)
			return "Weekend";

		var parts = new List<string>();
		foreach (var (day, label) in ClockDaysDisplayOrder)
		{
			if ((mask & (1 << (int)day)) == 0) continue;
			// Three-letter abbreviations keep the button compact.
			parts.Add(label.Substring(0, 3));
		}
		return string.Join(", ", parts);
	}

	/// <summary>
	/// Pushes clock enable / time / days / reset-button state from the focused cue (single-edit).
	/// </summary>
	private void RefreshClockUi()
	{
		if (_isMultiEdit) return;

		if (_clockEnabledCheckBox != null)
			_clockEnabledCheckBox.SetPressedNoSignal(_focusedCue?.ClockEnabled == true);

		if (_clockTimeInput != null && !_clockTimeInput.HasFocus())
		{
			if (_focusedCue == null || !_focusedCue.HasClockTime)
				_clockTimeInput.Text = "";
			else
				_clockTimeInput.Text = _focusedCue.GetClockDisplay();
		}

		// Keep popup checkboxes in sync if open; always refresh the summary button.
		if (_clockDaysPopup != null && _clockDaysPopup.Visible)
			SyncClockDayCheckboxesFromModel();
		RefreshClockDaysButtonLabel();

		if (_clockResetButton != null)
		{
			bool nonDefault = _focusedCue != null && _focusedCue.IsClockNonDefault;
			_clockResetButton.Visible = nonDefault;
			if (nonDefault)
				_clockResetButton.TooltipText = UiLocalizer.T("Reset to default (no clock, every day)");
		}
	}

	/// <summary>
	/// Toggles one weekday for the clock trigger on edit target(s).
	/// </summary>
	private void OnClockDayToggled(DayOfWeek day, bool pressed)
	{
		if (_isRefreshingUi) return;
		if (_globalData?.HistoryManager?.IsRestoring == true) return;

		var targets = GetEditTargets();
		if (targets.Count == 0) return;

		if (!_isMultiEdit && targets.Count == 1 && targets[0].IsClockDayEnabled(day) == pressed)
			return;

		string dayName = day.ToString();
		RecordHistoryBeforeEdit(
			pressed ? $"Enable clock on {dayName}" : $"Disable clock on {dayName}",
			pressed ? $"Multi-edit enable clock on {dayName}" : $"Multi-edit disable clock on {dayName}");

		foreach (var cue in targets)
		{
			if (cue.IsClockDayEnabled(day) == pressed) continue;
			cue.SetClockDayEnabled(day, pressed);
		}

		// Live-update the button caption while the popup stays open.
		if (!_isMultiEdit)
			RefreshClockDaysButtonLabel();
		else if (_clockDaysButton != null)
			_clockDaysButton.Text = FormatClockDaysSummary(
				targets.Count > 0 ? targets[0].ClockDaysMask : Cue.ClockDaysAll);

		if (_clockResetButton != null)
		{
			if (!_isMultiEdit)
				_clockResetButton.Visible = _focusedCue != null && _focusedCue.IsClockNonDefault;
			else
				_clockResetButton.Visible = targets.Exists(c => c.IsClockNonDefault);
		}
	}

	/// <summary>
	/// Toggles whether the wall-clock trigger is active for edit target(s).
	/// </summary>
	private void OnClockEnabledToggled(bool pressed)
	{
		if (_isRefreshingUi) return;
		if (_globalData?.HistoryManager?.IsRestoring == true) return;

		var targets = GetEditTargets();
		if (targets.Count == 0) return;

		if (!_isMultiEdit && targets.Count == 1 && targets[0].ClockEnabled == pressed)
			return;

		RecordHistoryBeforeEdit(
			pressed ? "Enable cue clock" : "Disable cue clock",
			pressed ? "Multi-edit enable cue clock" : "Multi-edit disable cue clock");

		foreach (var cue in targets)
		{
			if (cue.ClockEnabled == pressed) continue;
			cue.ClockEnabled = pressed;
		}

		if (!_isMultiEdit)
			RefreshClockUi();
		else if (_clockResetButton != null)
			_clockResetButton.Visible = targets.Exists(c => c.IsClockNonDefault);
	}

	private void OnClockTimeSubmitted(string text)
	{
		CommitClockTime(text);
		_clockTimeInput?.ReleaseFocus();
	}

	private void OnClockTimeFocusExited()
	{
		if (_isRefreshingUi) return;
		if (_clockTimeInput == null) return;
		// Commit whatever is in the field when focus leaves (including empty = clear).
		CommitClockTime(_clockTimeInput.Text);
	}

	/// <summary>
	/// Parses and applies wall-clock time to edit target(s). Empty input clears the clock time.
	/// </summary>
	private void CommitClockTime(string text)
	{
		if (_isRefreshingUi) return;
		if (_globalData?.HistoryManager?.IsRestoring == true) return;

		var targets = GetEditTargets();
		if (targets.Count == 0) return;

		if (!UiUtilities.TryParseClockTime(text, out TimeSpan timeOfDay, out string display))
		{
			_globalSignals?.EmitSignal(nameof(GlobalSignals.Log),
				$"Invalid clock time: \"{text}\". Use HH:mm or HH:mm:ss (optional AM/PM).", (int)LogType.Warning);
			// Restore previous display without mutating model.
			if (!_isMultiEdit)
				RefreshClockUi();
			return;
		}

		bool clearing = string.IsNullOrEmpty(display);

		// Skip no-op for single edit.
		if (!_isMultiEdit && targets.Count == 1)
		{
			var cue = targets[0];
			if (clearing && !cue.HasClockTime)
			{
				if (_clockTimeInput != null) _clockTimeInput.Text = "";
				return;
			}
			if (!clearing && cue.HasClockTime && cue.ClockTimeOfDay == timeOfDay)
			{
				if (_clockTimeInput != null) _clockTimeInput.Text = display;
				return;
			}
		}

		if (clearing)
		{
			RecordHistoryBeforeEdit("Clear cue clock time", "Multi-edit clear cue clock time");
			foreach (var cue in targets)
			{
				if (!cue.HasClockTime) continue;
				cue.ClearClockTime();
			}
			if (_clockTimeInput != null) _clockTimeInput.Text = "";
		}
		else
		{
			RecordHistoryBeforeEdit("Set cue clock time", "Multi-edit set cue clock time");
			foreach (var cue in targets)
			{
				cue.SetClockTime(timeOfDay);
				// Setting a time implies the user wants it active.
				if (!cue.ClockEnabled)
					cue.ClockEnabled = true;
			}
			if (_clockTimeInput != null) _clockTimeInput.Text = display;
			if (_clockEnabledCheckBox != null)
				_clockEnabledCheckBox.SetPressedNoSignal(true);
		}

		if (!_isMultiEdit)
			RefreshClockUi();
		else if (_clockResetButton != null)
			_clockResetButton.Visible = targets.Exists(c => c.IsClockNonDefault);

		GD.Print(clearing
			? "ShellInspector:CommitClockTime - Cleared cue clock time"
			: $"ShellInspector:CommitClockTime - Set cue clock to {display}");
	}

	private void OnClockResetPressed()
	{
		if (_isRefreshingUi) return;
		if (_globalData?.HistoryManager?.IsRestoring == true) return;

		var targets = GetEditTargets();
		if (targets.Count == 0) return;

		if (!_isMultiEdit && targets.Count == 1 && !targets[0].IsClockNonDefault)
			return;

		if (_clockDaysPopup != null && _clockDaysPopup.Visible)
			_clockDaysPopup.Hide();

		RecordHistoryBeforeEdit("Reset cue clock", "Multi-edit reset cue clock");
		foreach (var cue in targets)
			cue.ResetClockToDefault();

		if (_clockEnabledCheckBox != null)
			_clockEnabledCheckBox.SetPressedNoSignal(false);
		if (_clockTimeInput != null)
			_clockTimeInput.Text = "";
		if (_clockDaysButton != null)
			_clockDaysButton.Text = UiLocalizer.T("Every day");
		if (_clockResetButton != null)
			_clockResetButton.Visible = false;

		if (!_isMultiEdit)
			RefreshClockUi();

		GD.Print("ShellInspector:OnClockResetPressed - Reset cue clock to default (none, every day).");
	}

	// ── MIDI trigger UI ─────────────────────────────────────────────────────

	/// <summary>
	/// Ensures the MIDI message-type OptionButton has Note On / Off / CC / Program entries.
	/// </summary>
	private void EnsureMidiTypeOptions()
	{
		if (_midiTypeOption == null) return;
		if (_midiTypeOption.ItemCount > 0) return;

		_midiTypeOption.Clear();
		AddMidiTypeOption(MidiTriggerMessageType.NoteOn, "Note On");
		AddMidiTypeOption(MidiTriggerMessageType.NoteOff, "Note Off");
		AddMidiTypeOption(MidiTriggerMessageType.ControlChange, "CC");
		AddMidiTypeOption(MidiTriggerMessageType.ProgramChange, "Program");
	}

	private void AddMidiTypeOption(MidiTriggerMessageType type, string label)
	{
		int index = _midiTypeOption.ItemCount;
		_midiTypeOption.AddItem(label);
		_midiTypeOption.SetItemMetadata(index, (int)type);
	}

	/// <summary>
	/// Pushes MIDI enable / fields / capture / reset state from the focused cue (single-edit).
	/// </summary>
	private void RefreshMidiUi()
	{
		if (_isMultiEdit) return;

		EnsureMidiTypeOptions();

		if (_midiEnabledCheckBox != null)
			_midiEnabledCheckBox.SetPressedNoSignal(_focusedCue?.MidiTriggerEnabled == true);

		if (_midiTypeOption != null && _focusedCue != null)
		{
			int typeVal = (int)_focusedCue.MidiMessageType;
			for (int i = 0; i < _midiTypeOption.ItemCount; i++)
			{
				if (_midiTypeOption.GetItemMetadata(i).AsInt32() == typeVal)
				{
					_midiTypeOption.SetBlockSignals(true);
					_midiTypeOption.Selected = i;
					_midiTypeOption.SetBlockSignals(false);
					break;
				}
			}
		}

		if (_midiChannelSpin != null)
			_midiChannelSpin.SetValueNoSignal(_focusedCue?.MidiChannel ?? 0);
		if (_midiData1Spin != null)
			_midiData1Spin.SetValueNoSignal(_focusedCue?.MidiData1 ?? 0);
		if (_midiMatchValueCheck != null)
			_midiMatchValueCheck.SetPressedNoSignal(_focusedCue?.MidiMatchValue == true);
		if (_midiData2Spin != null)
		{
			_midiData2Spin.SetValueNoSignal(_focusedCue?.MidiData2 ?? 0);
			_midiData2Spin.Editable = _focusedCue?.MidiMatchValue == true;
		}

		UpdateMidiData1Prefix();

		if (_midiCaptureButton != null && !_isCapturingMidi)
			_midiCaptureButton.Text = UiLocalizer.T("Capture");

		if (_midiResetButton != null)
		{
			bool nonDefault = _focusedCue != null && _focusedCue.IsMidiTriggerNonDefault;
			_midiResetButton.Visible = nonDefault;
			if (nonDefault)
				_midiResetButton.TooltipText = UiLocalizer.T("Reset to default (no MIDI trigger)");
		}
	}

	/// <summary>
	/// Updates the Data1 spin prefix based on message type (n / cc / p).
	/// </summary>
	private void UpdateMidiData1Prefix()
	{
		if (_midiData1Spin == null) return;
		var type = GetSelectedMidiType();
		_midiData1Spin.Prefix = type switch
		{
			MidiTriggerMessageType.ControlChange => "cc",
			MidiTriggerMessageType.ProgramChange => "p",
			_ => "n"
		};
	}

	private MidiTriggerMessageType GetSelectedMidiType()
	{
		if (_midiTypeOption == null || _midiTypeOption.Selected < 0)
			return MidiTriggerMessageType.NoteOn;
		return (MidiTriggerMessageType)_midiTypeOption.GetItemMetadata(_midiTypeOption.Selected).AsInt32();
	}

	// ── OSC cue trigger UI ──────────────────────────────────────────────────

	private void RefreshOscUi()
	{
		if (_isMultiEdit) return;

		if (_oscEnabledCheckBox != null)
			_oscEnabledCheckBox.SetPressedNoSignal(_focusedCue?.OscTriggerEnabled == true);

		if (_oscAddressLineEdit != null && !_oscAddressEditing)
		{
			_oscAddressLineEdit.Text = _focusedCue?.OscTriggerAddress ?? string.Empty;
		}

		if (_oscResetButton != null)
		{
			bool nonDefault = _focusedCue != null && _focusedCue.IsOscTriggerNonDefault;
			_oscResetButton.Visible = nonDefault;
		}
	}

	private void OnOscEnabledToggled(bool pressed)
	{
		if (_isRefreshingUi) return;
		if (_globalData?.HistoryManager?.IsRestoring == true) return;

		var targets = GetEditTargets();
		if (targets.Count == 0) return;
		if (!_isMultiEdit && targets.Count == 1 && targets[0].OscTriggerEnabled == pressed)
			return;

		RecordHistoryBeforeEdit(
			pressed ? "Enable cue OSC trigger" : "Disable cue OSC trigger",
			pressed ? "Multi-edit enable cue OSC" : "Multi-edit disable cue OSC");

		foreach (var cue in targets)
		{
			if (cue.OscTriggerEnabled == pressed) continue;
			cue.OscTriggerEnabled = pressed;
		}

		if (!_isMultiEdit)
			RefreshOscUi();
	}

	private void OnOscAddressSubmitted(string text)
	{
		_oscAddressEditing = false;
		_oscAddressLineEdit?.ReleaseFocus();
		CommitOscAddress(text);
	}

	private void OnOscAddressFocusExited()
	{
		if (_oscAddressLineEdit == null) return;
		_oscAddressEditing = false;
		CommitOscAddress(_oscAddressLineEdit.Text);
	}

	private void CommitOscAddress(string text)
	{
		if (_isRefreshingUi) return;
		if (_globalData?.HistoryManager?.IsRestoring == true) return;

		var targets = GetEditTargets();
		if (targets.Count == 0) return;

		string path = (text ?? string.Empty).Trim();
		if (!string.IsNullOrEmpty(path) && !path.StartsWith("/"))
			path = "/" + path;

		if (!_isMultiEdit && targets.Count == 1
		    && string.Equals(targets[0].OscTriggerAddress, path, System.StringComparison.Ordinal))
			return;

		RecordHistoryBeforeEdit("Edit cue OSC trigger path", "Multi-edit cue OSC path");
		foreach (var cue in targets)
			cue.OscTriggerAddress = path;

		if (!_isMultiEdit)
			RefreshOscUi();
	}

	private void OnOscResetPressed()
	{
		if (_isRefreshingUi) return;
		if (_globalData?.HistoryManager?.IsRestoring == true) return;

		var targets = GetEditTargets();
		if (targets.Count == 0) return;

		RecordHistoryBeforeEdit("Reset cue OSC trigger", "Multi-edit reset cue OSC");
		foreach (var cue in targets)
		{
			cue.OscTriggerEnabled = false;
			cue.OscTriggerAddress = string.Empty;
		}
		if (!_isMultiEdit)
			RefreshOscUi();
	}

	// ── Timecode cue trigger UI ─────────────────────────────────────────────

	private void OnTimecodeStateChanged()
	{
		if (!GodotObject.IsInstanceValid(this))
			return;
		if (_isMultiEdit)
		{
			PopulateTimecodeSourceOptions(-1);
			return;
		}

		RefreshTimecodeTriggerUi();
	}

	private void RefreshTimecodeTriggerUi()
	{
		if (_isMultiEdit) return;

		int selectedId = _focusedCue?.TimecodeTriggerEndpointId ?? -1;
		PopulateTimecodeSourceOptions(selectedId);

		if (_timecodeEnabledCheckBox != null)
			_timecodeEnabledCheckBox.SetPressedNoSignal(_focusedCue?.TimecodeTriggerEnabled == true);

		if (_timecodeTimeInput != null && !_timecodeTimeInput.HasFocus())
		{
			_timecodeTimeInput.Text = _focusedCue != null && _focusedCue.HasTimecodeTrigger
				? FormatTimecodeTriggerField(_focusedCue)
				: "";
		}

		if (_timecodeResetButton != null)
		{
			bool nonDefault = _focusedCue != null && _focusedCue.IsTimecodeTriggerNonDefault;
			_timecodeResetButton.Visible = nonDefault;
			if (nonDefault)
				_timecodeResetButton.TooltipText = UiLocalizer.T("Reset to default (no timecode trigger)");
		}
	}

	private string FormatTimecodeTriggerField(Cue cue)
	{
		if (cue == null || !cue.HasTimecodeTrigger)
			return string.Empty;
		var rate = _timecodeManager?.FindEndpoint(cue.TimecodeTriggerEndpointId)?.FrameRate
		           ?? TimecodeFrameRate.Fps30;
		return new TimecodeValue(
			cue.TimecodeTriggerHours,
			cue.TimecodeTriggerMinutes,
			cue.TimecodeTriggerSeconds,
			cue.TimecodeTriggerFrames,
			rate).ToDisplayString();
	}

	private void PopulateTimecodeSourceOptions(int selectedEndpointId)
	{
		if (_timecodeSourceOption == null)
			return;

		_timecodeSourceOption.SetBlockSignals(true);
		_timecodeSourceOption.Clear();

		var inputs = _timecodeManager?.Inputs;
		var outputs = _timecodeManager?.Outputs;
		bool any = (inputs != null && inputs.Count > 0) || (outputs != null && outputs.Count > 0);
		if (!any)
		{
			UiLocalizer.AddTranslatedItem(_timecodeSourceOption, "(No timecode I/O)");
			_timecodeSourceOption.SetItemDisabled(0, true);
			_timecodeSourceOption.SetItemMetadata(0, -1);
			_timecodeSourceOption.Select(0);
			_timecodeSourceOption.SetBlockSignals(false);
			return;
		}

		int select = -1;
		void AddGroup(string header, IReadOnlyList<TimecodeEndpoint> list, string prefix)
		{
			if (list == null || list.Count == 0)
				return;
			int headerIndex = _timecodeSourceOption.ItemCount;
			_timecodeSourceOption.AddItem(UiLocalizer.T(header));
			_timecodeSourceOption.SetItemDisabled(headerIndex, true);
			_timecodeSourceOption.SetItemMetadata(headerIndex, -1);
			for (int i = 0; i < list.Count; i++)
			{
				var endpoint = list[i];
				int index = _timecodeSourceOption.ItemCount;
				_timecodeSourceOption.AddItem($"{prefix} {endpoint.Name} ({endpoint.Kind.GetShortName()})");
				_timecodeSourceOption.SetItemMetadata(index, endpoint.Id);
				if (endpoint.Id == selectedEndpointId)
					select = index;
			}
		}

		AddGroup("Inputs", inputs, "In");
		AddGroup("Outputs", outputs, "Out");

		if (select < 0 && selectedEndpointId >= 0)
		{
			select = _timecodeSourceOption.ItemCount;
			_timecodeSourceOption.AddItem($"Missing #{selectedEndpointId}");
			_timecodeSourceOption.SetItemMetadata(select, selectedEndpointId);
		}

		_timecodeSourceOption.Select(select);
		_timecodeSourceOption.SetBlockSignals(false);
	}

	private void OnTimecodeEnabledToggled(bool pressed)
	{
		if (_isRefreshingUi) return;
		if (_globalData?.HistoryManager?.IsRestoring == true) return;

		var targets = GetEditTargets();
		if (targets.Count == 0) return;
		if (!_isMultiEdit && targets.Count == 1 && targets[0].TimecodeTriggerEnabled == pressed)
			return;

		RecordHistoryBeforeEdit(
			pressed ? "Enable cue timecode trigger" : "Disable cue timecode trigger",
			pressed ? "Multi-edit enable cue timecode" : "Multi-edit disable cue timecode");

		foreach (var cue in targets)
		{
			if (cue.TimecodeTriggerEnabled == pressed) continue;
			cue.TimecodeTriggerEnabled = pressed;
		}

		if (!_isMultiEdit)
			RefreshTimecodeTriggerUi();
		else if (_timecodeResetButton != null)
			_timecodeResetButton.Visible = targets.Exists(c => c.IsTimecodeTriggerNonDefault);
	}

	private void OnTimecodeSourceSelected(long index)
	{
		if (_isRefreshingUi) return;
		if (_globalData?.HistoryManager?.IsRestoring == true) return;
		if (_timecodeSourceOption == null) return;
		if (index < 0 || index >= _timecodeSourceOption.ItemCount) return;
		if (_timecodeSourceOption.IsItemDisabled((int)index))
			return;

		int id = (int)_timecodeSourceOption.GetItemMetadata((int)index);
		var targets = GetEditTargets();
		if (targets.Count == 0) return;
		if (!_isMultiEdit && targets.Count == 1 && targets[0].TimecodeTriggerEndpointId == id)
			return;

		RecordHistoryBeforeEdit("Set cue timecode trigger clock", "Multi-edit cue timecode clock");
		foreach (var cue in targets)
		{
			if (cue.TimecodeTriggerEndpointId == id) continue;
			cue.SetTimecodeTriggerEndpoint(id);
			if (id >= 0 && !cue.TimecodeTriggerEnabled && cue.HasTimecodeTrigger)
				cue.TimecodeTriggerEnabled = true;
		}

		if (!_isMultiEdit)
			RefreshTimecodeTriggerUi();
		else if (_timecodeResetButton != null)
			_timecodeResetButton.Visible = targets.Exists(c => c.IsTimecodeTriggerNonDefault);
	}

	private void OnTimecodeTimeSubmitted(string text)
	{
		CommitTimecodeTriggerTime(text);
		_timecodeTimeInput?.ReleaseFocus();
	}

	private void OnTimecodeTimeFocusExited()
	{
		if (_timecodeTimeInput == null) return;
		if (_isRefreshingUi) return;
		CommitTimecodeTriggerTime(_timecodeTimeInput.Text);
	}

	private void CommitTimecodeTriggerTime(string text)
	{
		if (_isRefreshingUi) return;
		if (_globalData?.HistoryManager?.IsRestoring == true) return;

		var targets = GetEditTargets();
		if (targets.Count == 0) return;

		string trimmed = (text ?? string.Empty).Trim();
		if (string.IsNullOrEmpty(trimmed))
		{
			if (!_isMultiEdit && targets.Count == 1 && !targets[0].HasTimecodeTrigger)
			{
				if (_timecodeTimeInput != null) _timecodeTimeInput.Text = "";
				return;
			}

			RecordHistoryBeforeEdit("Clear cue timecode trigger time", "Multi-edit clear cue timecode time");
			foreach (var cue in targets)
			{
				if (!cue.HasTimecodeTrigger) continue;
				cue.ClearTimecodeTriggerTime();
			}

			if (_timecodeTimeInput != null) _timecodeTimeInput.Text = "";
			if (!_isMultiEdit)
				RefreshTimecodeTriggerUi();
			else if (_timecodeResetButton != null)
				_timecodeResetButton.Visible = targets.Exists(c => c.IsTimecodeTriggerNonDefault);
			return;
		}

		var rate = ResolveTimecodeTriggerRate(targets);
		if (!TimecodeValue.TryParseUserInput(trimmed, rate, out var value))
		{
			_globalSignals?.EmitSignal(nameof(GlobalSignals.Log),
				$"Invalid timecode: \"{text}\". Use HH:MM:SS:FF, MM:SS:FF, SS:FF, or seconds.", (int)LogType.Warning);
			if (!_isMultiEdit)
				RefreshTimecodeTriggerUi();
			return;
		}

		if (!_isMultiEdit && targets.Count == 1)
		{
			var cue = targets[0];
			if (cue.HasTimecodeTrigger
			    && cue.TimecodeTriggerHours == value.Hours
			    && cue.TimecodeTriggerMinutes == value.Minutes
			    && cue.TimecodeTriggerSeconds == value.Seconds
			    && cue.TimecodeTriggerFrames == value.Frames)
			{
				if (_timecodeTimeInput != null)
					_timecodeTimeInput.Text = value.ToDisplayString();
				return;
			}
		}

		RecordHistoryBeforeEdit("Set cue timecode trigger time", "Multi-edit set cue timecode time");
		foreach (var cue in targets)
		{
			cue.SetTimecodeTriggerTime(value.Hours, value.Minutes, value.Seconds, value.Frames);
			if (!cue.TimecodeTriggerEnabled)
				cue.TimecodeTriggerEnabled = true;
		}

		if (_timecodeTimeInput != null)
			_timecodeTimeInput.Text = value.ToDisplayString();
		if (_timecodeEnabledCheckBox != null)
			_timecodeEnabledCheckBox.SetPressedNoSignal(true);

		if (!_isMultiEdit)
			RefreshTimecodeTriggerUi();
		else if (_timecodeResetButton != null)
			_timecodeResetButton.Visible = targets.Exists(c => c.IsTimecodeTriggerNonDefault);
	}

	private TimecodeFrameRate ResolveTimecodeTriggerRate(List<Cue> targets)
	{
		int endpointId = -1;
		if (_timecodeSourceOption != null && _timecodeSourceOption.Selected >= 0)
			endpointId = (int)_timecodeSourceOption.GetItemMetadata(_timecodeSourceOption.Selected);
		else if (targets != null && targets.Count == 1)
			endpointId = targets[0].TimecodeTriggerEndpointId;

		var endpoint = _timecodeManager?.FindEndpoint(endpointId);
		return endpoint?.FrameRate ?? TimecodeFrameRate.Fps30;
	}

	private void OnTimecodeResetPressed()
	{
		if (_isRefreshingUi) return;
		if (_globalData?.HistoryManager?.IsRestoring == true) return;

		var targets = GetEditTargets();
		if (targets.Count == 0) return;
		if (!_isMultiEdit && targets.Count == 1 && !targets[0].IsTimecodeTriggerNonDefault)
			return;

		RecordHistoryBeforeEdit("Reset cue timecode trigger", "Multi-edit reset cue timecode");
		foreach (var cue in targets)
			cue.ResetTimecodeTriggerToDefault();

		if (_timecodeEnabledCheckBox != null)
			_timecodeEnabledCheckBox.SetPressedNoSignal(false);
		if (_timecodeTimeInput != null)
			_timecodeTimeInput.Text = "";
		if (_timecodeResetButton != null)
			_timecodeResetButton.Visible = false;

		if (!_isMultiEdit)
			RefreshTimecodeTriggerUi();
		else
			PopulateTimecodeSourceOptions(-1);
	}

	private void OnMidiEnabledToggled(bool pressed)
	{
		if (_isRefreshingUi) return;
		if (_globalData?.HistoryManager?.IsRestoring == true) return;

		var targets = GetEditTargets();
		if (targets.Count == 0) return;

		if (!_isMultiEdit && targets.Count == 1 && targets[0].MidiTriggerEnabled == pressed)
			return;

		RecordHistoryBeforeEdit(
			pressed ? "Enable cue MIDI trigger" : "Disable cue MIDI trigger",
			pressed ? "Multi-edit enable cue MIDI" : "Multi-edit disable cue MIDI");

		foreach (var cue in targets)
		{
			if (cue.MidiTriggerEnabled == pressed) continue;
			cue.MidiTriggerEnabled = pressed;
		}

		if (!_isMultiEdit)
			RefreshMidiUi();
		else if (_midiResetButton != null)
			_midiResetButton.Visible = targets.Exists(c => c.IsMidiTriggerNonDefault);
	}

	private void OnMidiCapturePressed()
	{
		if (_isRefreshingUi) return;
		if (_globalData?.HistoryManager?.IsRestoring == true) return;

		var targets = GetEditTargets();
		if (targets.Count == 0) return;

		if (_isCapturingMidi)
		{
			CancelMidiCapture();
			return;
		}

		if (_midiManager == null)
		{
			_globalSignals?.EmitSignal(nameof(GlobalSignals.Log),
				"MIDI capture unavailable: MidiManager not found.", (int)LogType.Warning);
			return;
		}

		if (!_midiManager.MidiEnabled || _midiManager.OpenInputCount == 0)
		{
			_globalSignals?.EmitSignal(nameof(GlobalSignals.Log),
				"MIDI capture: enable MIDI and open at least one session input in Settings → MIDI.",
				(int)LogType.Warning);
			return;
		}

		_isCapturingMidi = true;
		if (_midiCaptureButton != null)
			_midiCaptureButton.Text = UiLocalizer.T("Waiting…");
		_midiManager.StartCapture();
		GD.Print("ShellInspector:OnMidiCapturePressed - Capture armed");
	}

	/// <summary>
	/// Cancels MIDI capture mode (local flag + MidiManager).
	/// </summary>
	private void CancelMidiCapture()
	{
		if (!_isCapturingMidi && _midiManager?.IsCapturing != true) return;
		_isCapturingMidi = false;
		_midiManager?.CancelCapture();
		if (_midiCaptureButton != null)
			_midiCaptureButton.Text = UiLocalizer.T("Capture");
		GD.Print("ShellInspector:CancelMidiCapture - Cancelled");
	}

	/// <summary>
	/// Applies a captured MIDI message from <see cref="MidiManager.MidiCaptured"/>.
	/// </summary>
	private void OnMidiCaptured(string deviceName, int messageType, int channel, int data1, int data2)
	{
		if (!_isCapturingMidi) return;
		_isCapturingMidi = false;
		if (_midiCaptureButton != null)
			_midiCaptureButton.Text = UiLocalizer.T("Capture");

		var targets = GetEditTargets();
		if (targets.Count == 0) return;
		if (_globalData?.HistoryManager?.IsRestoring == true) return;

		var type = (MidiTriggerMessageType)messageType;
		// Capture matches type+channel+number; velocity optional (off by default — more useful for notes).
		bool matchValue = type == MidiTriggerMessageType.ControlChange;

		RecordHistoryBeforeEdit("Capture cue MIDI trigger", "Multi-edit capture cue MIDI");
		foreach (var cue in targets)
		{
			cue.SetMidiTrigger(type, channel, data1, data2, matchValue, deviceFilter: null);
			if (!cue.MidiTriggerEnabled)
				cue.MidiTriggerEnabled = true;
		}

		if (!_isMultiEdit)
			RefreshMidiUi();
		else
		{
			if (_midiEnabledCheckBox != null)
				_midiEnabledCheckBox.SetPressedNoSignal(true);
			if (_midiResetButton != null)
				_midiResetButton.Visible = true;
			// Best-effort field push for multi-edit after capture.
			_isRefreshingUi = true;
			try
			{
				EnsureMidiTypeOptions();
				if (_midiTypeOption != null)
				{
					for (int i = 0; i < _midiTypeOption.ItemCount; i++)
					{
						if (_midiTypeOption.GetItemMetadata(i).AsInt32() == messageType)
						{
							_midiTypeOption.Selected = i;
							break;
						}
					}
				}
				_midiChannelSpin?.SetValueNoSignal(channel);
				_midiData1Spin?.SetValueNoSignal(data1);
				_midiMatchValueCheck?.SetPressedNoSignal(matchValue);
				_midiData2Spin?.SetValueNoSignal(data2);
				if (_midiData2Spin != null)
					_midiData2Spin.Editable = matchValue;
				UpdateMidiData1Prefix();
			}
			finally
			{
				_isRefreshingUi = false;
			}
		}

		_globalSignals?.EmitSignal(nameof(GlobalSignals.Log),
			$"MIDI capture: {type} ch={channel} d1={data1} d2={data2}" +
			(string.IsNullOrEmpty(deviceName) ? "" : $" from {deviceName}"),
			(int)LogType.Info);
	}

	private void OnMidiResetPressed()
	{
		if (_isRefreshingUi) return;
		if (_globalData?.HistoryManager?.IsRestoring == true) return;

		var targets = GetEditTargets();
		if (targets.Count == 0) return;

		if (!_isMultiEdit && targets.Count == 1 && !targets[0].IsMidiTriggerNonDefault)
			return;

		if (_isCapturingMidi)
			CancelMidiCapture();

		RecordHistoryBeforeEdit("Reset cue MIDI trigger", "Multi-edit reset cue MIDI");
		foreach (var cue in targets)
			cue.ResetMidiTriggerToDefault();

		if (_midiEnabledCheckBox != null)
			_midiEnabledCheckBox.SetPressedNoSignal(false);
		if (_midiResetButton != null)
			_midiResetButton.Visible = false;

		if (!_isMultiEdit)
			RefreshMidiUi();
		else
		{
			_midiChannelSpin?.SetValueNoSignal(0);
			_midiData1Spin?.SetValueNoSignal(0);
			_midiData2Spin?.SetValueNoSignal(0);
			_midiMatchValueCheck?.SetPressedNoSignal(false);
		}

		GD.Print("ShellInspector:OnMidiResetPressed - Reset cue MIDI trigger to default.");
	}

	/// <summary>
	/// Commits current MIDI field values from the inspector into the model.
	/// </summary>
	private void CommitMidiFieldsFromUi()
	{
		if (_isRefreshingUi) return;
		if (_globalData?.HistoryManager?.IsRestoring == true) return;

		var targets = GetEditTargets();
		if (targets.Count == 0) return;

		var type = GetSelectedMidiType();
		int channel = _midiChannelSpin != null ? (int)_midiChannelSpin.Value : 0;
		int data1 = _midiData1Spin != null ? (int)_midiData1Spin.Value : 0;
		int data2 = _midiData2Spin != null ? (int)_midiData2Spin.Value : 0;
		bool matchValue = _midiMatchValueCheck != null && _midiMatchValueCheck.ButtonPressed;

		// Skip no-op single edit.
		if (!_isMultiEdit && targets.Count == 1)
		{
			var cue = targets[0];
			if (cue.HasMidiTrigger &&
			    cue.MidiMessageType == type &&
			    cue.MidiChannel == channel &&
			    cue.MidiData1 == data1 &&
			    cue.MidiData2 == data2 &&
			    cue.MidiMatchValue == matchValue)
			{
				return;
			}
		}

		RecordHistoryBeforeEdit("Edit cue MIDI trigger", "Multi-edit cue MIDI trigger");
		foreach (var cue in targets)
		{
			cue.SetMidiTrigger(type, channel, data1, data2, matchValue, cue.MidiDeviceFilter);
			// Editing fields implies the user wants a pattern present; do not auto-enable
			// (enable is explicit via checkbox / capture).
		}

		if (!_isMultiEdit)
			RefreshMidiUi();
		else if (_midiResetButton != null)
			_midiResetButton.Visible = targets.Exists(c => c.IsMidiTriggerNonDefault);
	}

	private void OnMidiTypeSelected(long index)
	{
		if (_isRefreshingUi) return;
		UpdateMidiData1Prefix();
		// Program change has no value match.
		if (GetSelectedMidiType() == MidiTriggerMessageType.ProgramChange)
		{
			if (_midiMatchValueCheck != null)
				_midiMatchValueCheck.SetPressedNoSignal(false);
			if (_midiData2Spin != null)
				_midiData2Spin.Editable = false;
		}
		CommitMidiFieldsFromUi();
	}

	private void OnMidiChannelChanged(double value)
	{
		if (_isRefreshingUi) return;
		CommitMidiFieldsFromUi();
	}

	private void OnMidiData1Changed(double value)
	{
		if (_isRefreshingUi) return;
		CommitMidiFieldsFromUi();
	}

	private void OnMidiData2Changed(double value)
	{
		if (_isRefreshingUi) return;
		CommitMidiFieldsFromUi();
	}

	private void OnMidiMatchValueToggled(bool pressed)
	{
		if (_isRefreshingUi) return;
		if (_midiData2Spin != null)
			_midiData2Spin.Editable = pressed;
		CommitMidiFieldsFromUi();
	}
}
