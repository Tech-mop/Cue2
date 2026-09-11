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
using Cue2.Services;
using Cue2.UI.Utilities;
using Godot;

namespace Cue2.UI.Inspectors;

/// <summary>
/// Shell context inspector: single-cue edit, or multi-edit when enabled and multiple cues are selected.
/// </summary>
/// <remarks>
/// Multi-edit (Settings → General → Multi-edit cues): fields stay blank; commits apply to every
/// selected cue. History for multi-edit uses a cuelist snapshot so all targets undo together.
/// </remarks>
public partial class ShellInspector : Control
{
	private GlobalSignals _globalSignals;
	private GlobalData _globalData;

	/// <summary>Last focused cue id. -1 = none (must not default to 0 — cue ids are 0-based).</summary>
	private int _focusedCueId = -1;

	private Cue _focusedCue;

	/// <summary>True when multi-edit is active for the current selection.</summary>
	private bool _isMultiEdit;

	private LineEdit _cueNum;
	private LineEdit _cueName;
	private Label _cueId;
	private Label _parentCueLabel;
	private LineEdit _preWaitInput;
	private LineEdit _durationValue;
	private LineEdit _postWaitInput;
	private OptionButton _followOption;
	private ColorPickerButton _colorPicker;
	private CheckBox _armedCheckBox;
	private CheckBox _skipIfDisarmedCheckBox;
	private CheckBox _onlyOneActiveCheckBox;
	private Button _deleteCueButton;

	// Hotkey trigger UI
	private CheckBox _hotkeyEnabledCheckBox;
	private Button _hotkeyBindingButton;
	private Button _hotkeyResetButton;
	private bool _isListeningForHotkey;

	// Clock trigger UI
	private CheckBox _clockEnabledCheckBox;
	private LineEdit _clockTimeInput;
	private Button _clockDaysButton;
	private Button _clockResetButton;
	private PopupPanel _clockDaysPopup;
	/// <summary>Day checkboxes in Mon–Sun display order, paired with <see cref="DayOfWeek"/>.</summary>
	private readonly (CheckBox Box, DayOfWeek Day)[] _clockDayChecks = new (CheckBox, DayOfWeek)[7];

	/// <summary>Mon–Sun display order for day pickers (not calendar Sunday-first).</summary>
	private static readonly (DayOfWeek Day, string Label)[] ClockDaysDisplayOrder =
	{
		(DayOfWeek.Monday, "Monday"),
		(DayOfWeek.Tuesday, "Tuesday"),
		(DayOfWeek.Wednesday, "Wednesday"),
		(DayOfWeek.Thursday, "Thursday"),
		(DayOfWeek.Friday, "Friday"),
		(DayOfWeek.Saturday, "Saturday"),
		(DayOfWeek.Sunday, "Sunday"),
	};

	// MIDI trigger UI
	private CheckBox _midiEnabledCheckBox;
	private Button _midiCaptureButton;
	private Button _midiResetButton;
	private OptionButton _midiTypeOption;
	private SpinBox _midiChannelSpin;
	private SpinBox _midiData1Spin;
	private CheckBox _midiMatchValueCheck;
	private SpinBox _midiData2Spin;
	private MidiManager _midiManager;
	private bool _isCapturingMidi;

	// OSC cue trigger UI
	private CheckBox _oscEnabledCheckBox;
	private LineEdit _oscAddressLineEdit;
	private Button _oscResetButton;
	private bool _oscAddressEditing;

	// Notes UI (under Triggers column)
	private TextEdit _notesTextEdit;
	private CheckBox _memoCheckBox;

	/// <summary>
	/// True while UI is being pushed from the model (undo/redo, sync). Prevents TextChanged handlers
	/// from writing back into the model / recording history.
	/// </summary>
	private bool _isRefreshingUi;

	private const string MultiNumCoalesceKey = "multi:shell:num";
	private const string MultiNameCoalesceKey = "multi:shell:name";
	private const string MultiNotesCoalesceKey = "multi:shell:notes";
	private const string MultiPlaceholder = "Multiple selected";

	public override void _Ready()
	{
		_globalData = GetNode<GlobalData>("/root/GlobalData");
		_globalSignals = GetNode<GlobalSignals>("/root/GlobalSignals");

		_globalSignals.ShellFocused += ShellSelected;

		_cueName = GetNode<LineEdit>("%ShellName");
		_cueNum = GetNode<LineEdit>("%CueNum");
		_cueId = GetNode<Label>("%CueId");
		_parentCueLabel = GetNode<Label>("%ParentCueLabel");

		_preWaitInput = GetNode<LineEdit>("%PreWaitInput");
		_durationValue = GetNode<LineEdit>("%DurationValue");
		_postWaitInput = GetNode<LineEdit>("%PostWaitInput");
		_followOption = GetNode<OptionButton>("%FollowOption");
		_colorPicker = GetNode<ColorPickerButton>("%ColourPickerButton");
		_armedCheckBox = GetNodeOrNull<CheckBox>("%ArmedCheckBox");
		_skipIfDisarmedCheckBox = GetNodeOrNull<CheckBox>("%SkipIfDisarmedCheckBox");
		_onlyOneActiveCheckBox = GetNodeOrNull<CheckBox>("%OnlyOneActiveCheckBox");
		_deleteCueButton = GetNodeOrNull<Button>("%DeleteCueButton");

		_hotkeyEnabledCheckBox = GetNodeOrNull<CheckBox>("%HotkeyEnabledCheckBox");
		_hotkeyBindingButton = GetNodeOrNull<Button>("%HotkeyBindingButton");
		_hotkeyResetButton = GetNodeOrNull<Button>("%HotkeyResetButton");

		_clockEnabledCheckBox = GetNodeOrNull<CheckBox>("%ClockEnabledCheckBox");
		_clockTimeInput = GetNodeOrNull<LineEdit>("%ClockTimeInput");
		_clockDaysButton = GetNodeOrNull<Button>("%ClockDaysButton");
		_clockResetButton = GetNodeOrNull<Button>("%ClockResetButton");
		BuildClockDaysPopup();

		_midiEnabledCheckBox = GetNodeOrNull<CheckBox>("%MidiEnabledCheckBox");
		_midiCaptureButton = GetNodeOrNull<Button>("%MidiCaptureButton");
		_midiResetButton = GetNodeOrNull<Button>("%MidiResetButton");
		_midiTypeOption = GetNodeOrNull<OptionButton>("%MidiTypeOption");
		_midiChannelSpin = GetNodeOrNull<SpinBox>("%MidiChannelSpin");
		_midiData1Spin = GetNodeOrNull<SpinBox>("%MidiData1Spin");
		_midiMatchValueCheck = GetNodeOrNull<CheckBox>("%MidiMatchValueCheck");
		_midiData2Spin = GetNodeOrNull<SpinBox>("%MidiData2Spin");
		_midiManager = GetNodeOrNull<MidiManager>("/root/MidiManager");
		EnsureMidiTypeOptions();

		_oscEnabledCheckBox = GetNodeOrNull<CheckBox>("%OscEnabledCheckBox");
		_oscAddressLineEdit = GetNodeOrNull<LineEdit>("%OscAddressLineEdit");
		_oscResetButton = GetNodeOrNull<Button>("%OscResetButton");

		_notesTextEdit = GetNodeOrNull<TextEdit>("%NotesTextEdit");
		_memoCheckBox = GetNodeOrNull<CheckBox>("%MemoCheckBox");

		UiUtilities.FormatLabelsColours(this, GlobalStyles.SoftFontColor);

		_cueNum.TextChanged += OnCueNumTextChanged;
		_cueName.TextChanged += OnCueNameTextChanged;
		// Seal continuous name/number typing so the next edit is a new undo step.
		_cueNum.TextSubmitted += _ => { _cueNum.ReleaseFocus(); };
		_cueName.TextSubmitted += _ => { _cueName.ReleaseFocus(); };
		_cueNum.FocusExited += OnCueNumFocusExited;
		_cueName.FocusExited += OnCueNameFocusExited;

		_colorPicker.PopupClosed += AssignColor;

		_preWaitInput.TextSubmitted += text => TimeFieldSubmitted(text, _preWaitInput);
		_postWaitInput.TextSubmitted += text => TimeFieldSubmitted(text, _postWaitInput);
		_preWaitInput.FocusExited += () => TimeFieldSubmitted(_preWaitInput.Text, _preWaitInput);
		_postWaitInput.FocusExited += () => TimeFieldSubmitted(_postWaitInput.Text, _postWaitInput);
		_followOption.ItemSelected += FollowOptionItemSelected;

		if (_armedCheckBox != null)
			_armedCheckBox.Toggled += OnArmedToggled;
		if (_skipIfDisarmedCheckBox != null)
			_skipIfDisarmedCheckBox.Toggled += OnSkipIfDisarmedToggled;
		if (_onlyOneActiveCheckBox != null)
			_onlyOneActiveCheckBox.Toggled += OnOnlyOneActiveToggled;

		if (_hotkeyEnabledCheckBox != null)
			_hotkeyEnabledCheckBox.Toggled += OnHotkeyEnabledToggled;
		if (_hotkeyBindingButton != null)
			_hotkeyBindingButton.Pressed += OnHotkeyBindingButtonPressed;
		if (_hotkeyResetButton != null)
		{
			_hotkeyResetButton.Pressed += OnHotkeyResetPressed;
			try
			{
				_hotkeyResetButton.Icon = GetThemeIcon("Refresh", "AtlasIcons");
				_hotkeyResetButton.ExpandIcon = true;
			}
			catch
			{
				/* icon optional */
			}
		}

		if (_clockEnabledCheckBox != null)
			_clockEnabledCheckBox.Toggled += OnClockEnabledToggled;
		if (_clockTimeInput != null)
		{
			_clockTimeInput.TextSubmitted += OnClockTimeSubmitted;
			_clockTimeInput.FocusExited += OnClockTimeFocusExited;
		}
		if (_clockDaysButton != null)
			_clockDaysButton.Pressed += OnClockDaysButtonPressed;

		if (_clockResetButton != null)
		{
			_clockResetButton.Pressed += OnClockResetPressed;
			try
			{
				_clockResetButton.Icon = GetThemeIcon("Refresh", "AtlasIcons");
				_clockResetButton.ExpandIcon = true;
			}
			catch
			{
				/* icon optional */
			}
		}

		if (_midiEnabledCheckBox != null)
			_midiEnabledCheckBox.Toggled += OnMidiEnabledToggled;
		if (_midiCaptureButton != null)
			_midiCaptureButton.Pressed += OnMidiCapturePressed;
		if (_midiResetButton != null)
		{
			_midiResetButton.Pressed += OnMidiResetPressed;
			try
			{
				_midiResetButton.Icon = GetThemeIcon("Refresh", "AtlasIcons");
				_midiResetButton.ExpandIcon = true;
			}
			catch
			{
				/* icon optional */
			}
		}
		if (_midiTypeOption != null)
			_midiTypeOption.ItemSelected += OnMidiTypeSelected;
		if (_midiChannelSpin != null)
			_midiChannelSpin.ValueChanged += OnMidiChannelChanged;
		if (_midiData1Spin != null)
			_midiData1Spin.ValueChanged += OnMidiData1Changed;
		if (_midiMatchValueCheck != null)
			_midiMatchValueCheck.Toggled += OnMidiMatchValueToggled;
		if (_midiData2Spin != null)
			_midiData2Spin.ValueChanged += OnMidiData2Changed;

		if (_oscEnabledCheckBox != null)
			_oscEnabledCheckBox.Toggled += OnOscEnabledToggled;
		if (_oscAddressLineEdit != null)
		{
			_oscAddressLineEdit.TextSubmitted += OnOscAddressSubmitted;
			_oscAddressLineEdit.FocusExited += OnOscAddressFocusExited;
		}
		if (_oscResetButton != null)
		{
			_oscResetButton.Pressed += OnOscResetPressed;
			try
			{
				_oscResetButton.Icon = GetThemeIcon("Refresh", "AtlasIcons");
				_oscResetButton.ExpandIcon = true;
			}
			catch { /* icon optional */ }
		}

		if (_notesTextEdit != null)
		{
			_notesTextEdit.TextChanged += OnNotesTextChanged;
			_notesTextEdit.FocusExited += OnNotesFocusExited;
		}
		if (_memoCheckBox != null)
			_memoCheckBox.Toggled += OnMemoToggled;

		if (_midiManager != null)
			_midiManager.MidiCaptured += OnMidiCaptured;

		if (_deleteCueButton != null)
		{
			_deleteCueButton.Pressed += OnDeleteCuePressed;
			_deleteCueButton.AddThemeColorOverride("font_color", GlobalStyles.Danger);
			try
			{
				_deleteCueButton.Icon = GetThemeIcon("DeleteBin", "AtlasIcons");
				_deleteCueButton.ExpandIcon = true;
			}
			catch
			{
				/* icon optional */
			}

			SyncDeleteHotkeyTooltip();
		}

		_globalSignals.SyncShellInspector += UpdateFields;

		Visible = false;
		UiLocalizer.LocalizeTree(this);
		if (_globalSignals != null)
			_globalSignals.LocaleChanged += OnLocaleChanged;
	}

	public override void _ExitTree()
	{
		if (_isListeningForHotkey)
			CancelHotkeyListening();
		if (_isCapturingMidi)
			CancelMidiCapture();

		if (_midiManager != null)
			_midiManager.MidiCaptured -= OnMidiCaptured;

		if (_globalSignals != null)
		{
			_globalSignals.ShellFocused -= ShellSelected;
			_globalSignals.SyncShellInspector -= UpdateFields;
			_globalSignals.LocaleChanged -= OnLocaleChanged;
		}

		DetachFocusedCueEvents();
		base._ExitTree();
	}

	/// <summary>
	/// Re-localizes shell inspector labels and tooltips when the UI language changes.
	/// </summary>
	/// <param name="localeCode">New locale code.</param>
	private void OnLocaleChanged(string localeCode)
	{
		if (!GodotObject.IsInstanceValid(this))
			return;
		UiLocalizer.LocalizeTree(this);
		SyncDeleteHotkeyTooltip();
	}

	private void SyncDeleteHotkeyTooltip()
	{
		if (_deleteCueButton == null) return;
		string hotkey = GlobalData.ParseHotkey("DeleteCue");
		string tip = _isMultiEdit
			? UiLocalizer.T("Delete all selected cues (and any child cues).")
			: UiLocalizer.T("Delete this cue (and any child cues).");
		if (!string.IsNullOrEmpty(hotkey))
			tip += "\n" + UiLocalizer.Tf("Hotkey: {0}", hotkey);
		_deleteCueButton.TooltipText = tip;
	}

	/// <summary>
	/// Deletes the focused / selected cue(s) via the cuelist (same path as Delete key).
	/// </summary>
	private void OnDeleteCuePressed()
	{
		if (!_isMultiEdit)
		{
			if (_focusedCue == null)
				return;

			// Ensure this cue is selected so DeleteSelectedCues targets it.
			// Do not record a separate selection step — delete records cuelist history with selection.
			if (!ShellSelection.SelectedCues.Contains(_focusedCue))
				_globalData?.ShellSelection?.SelectIndividualShell(_focusedCue, recordHistory: false);
		}
		else if (ShellSelection.SelectedCues == null || ShellSelection.SelectedCues.Count == 0)
		{
			return;
		}

		_globalSignals.EmitSignal(nameof(GlobalSignals.DeleteSelectedCues));

		DetachFocusedCueEvents();
		_focusedCue = null;
		_focusedCueId = -1;
		_isMultiEdit = false;
		Visible = false;
	}

	private void ShellSelected(int cueId)
	{
		// cueId < 0 = selection cleared (e.g. after delete)
		if (cueId < 0)
		{
			if (_isListeningForHotkey)
				CancelHotkeyListening();
			if (_isCapturingMidi)
				CancelMidiCapture();
			if (_clockDaysPopup != null && _clockDaysPopup.Visible)
				_clockDaysPopup.Hide();
			DetachFocusedCueEvents();
			_focusedCue = null;
			_focusedCueId = -1;
			_isMultiEdit = false;
			Visible = false;
			return;
		}

		Visible = true;

		bool multiEditWanted = ShouldUseMultiEdit();
		if (multiEditWanted)
		{
			EnterMultiEditMode();
			return;
		}

		// Single-cue path (or multi-edit disabled with multiple selected).
		if (_focusedCueId == cueId && _focusedCue != null && !_isMultiEdit)
			return;

		DetachFocusedCueEvents();
		_isMultiEdit = false;

		_focusedCue = CueList.FetchCueFromId(cueId);
		if (_focusedCue == null)
		{
			_focusedCueId = -1;
			Visible = false;
			GD.Print($"ShellInspector:ShellSelected - Cue id {cueId} not found (cleared).");
			return;
		}

		_focusedCueId = cueId;
		_focusedCue.NameChanged += OnNameChanged;
		_focusedCue.CueNumChanged += OnCueNumChanged;
		_focusedCue.NotesChanged += OnNotesChangedFromModel;
		_focusedCue.FollowChanged += OnFollowChanged;
		EnsureFollowOptions();
		ClearMultiEditPlaceholders();
		UpdateFields();
	}

	/// <summary>
	/// True when multi-edit setting is on and more than one cue is selected.
	/// </summary>
	private bool ShouldUseMultiEdit()
	{
		if (_globalData?.Settings == null || !_globalData.Settings.MultiEditEnabled)
			return false;
		return ShellSelection.SelectedCues != null && ShellSelection.SelectedCues.Count > 1;
	}

	/// <summary>
	/// Activates multi-edit: shows ID list, leaves fields blank, does not bind a single cue for sync.
	/// </summary>
	private void EnterMultiEditMode()
	{
		DetachFocusedCueEvents();
		_isMultiEdit = true;

		// Keep last focused id for reference / delete path, but do not load its values.
		var selected = GetSelectedCuesSnapshot();
		if (selected.Count > 0)
		{
			_focusedCue = selected[^1];
			_focusedCueId = _focusedCue.Id;
		}

		EnsureFollowOptions();
		ShowMultiEditBlankFields(selected);
		SyncDeleteHotkeyTooltip();
	}

	private void DetachFocusedCueEvents()
	{
		if (_focusedCue == null) return;
		_focusedCue.NameChanged -= OnNameChanged;
		_focusedCue.CueNumChanged -= OnCueNumChanged;
		_focusedCue.NotesChanged -= OnNotesChangedFromModel;
		_focusedCue.FollowChanged -= OnFollowChanged;
	}

	/// <summary>
	/// Snapshot of currently selected cues (non-null only).
	/// </summary>
	private static List<Cue> GetSelectedCuesSnapshot()
	{
		if (ShellSelection.SelectedCues == null || ShellSelection.SelectedCues.Count == 0)
			return new List<Cue>();
		return ShellSelection.SelectedCues.Where(c => c != null).ToList();
	}

	/// <summary>
	/// Target cues for an edit: multi-selection when multi-editing, else the focused cue.
	/// </summary>
	private List<Cue> GetEditTargets()
	{
		if (_isMultiEdit)
			return GetSelectedCuesSnapshot();
		if (_focusedCue != null)
			return new List<Cue> { _focusedCue };
		return new List<Cue>();
	}

	/// <summary>
	/// Clears editable fields and shows multi-edit header / placeholders (no model values).
	/// </summary>
	private void ShowMultiEditBlankFields(List<Cue> selected)
	{
		_isRefreshingUi = true;
		try
		{
			if (_cueId != null)
			{
				_cueId.Text = UiLocalizer.T("MULTI-EDITING");
				_cueId.TooltipText = FormatMultiEditIdTooltip(selected);
			}

			if (_parentCueLabel != null)
				_parentCueLabel.Text = "";

			if (_cueNum != null)
			{
				_cueNum.Text = "";
				_cueNum.PlaceholderText = MultiPlaceholder;
			}

			if (_cueName != null)
			{
				_cueName.Text = "";
				_cueName.PlaceholderText = MultiPlaceholder;
			}

			if (_preWaitInput != null)
			{
				_preWaitInput.Text = "";
				_preWaitInput.PlaceholderText = MultiPlaceholder;
			}

			if (_postWaitInput != null)
			{
				_postWaitInput.Text = "";
				_postWaitInput.PlaceholderText = MultiPlaceholder;
			}

			if (_durationValue != null)
			{
				_durationValue.Text = "";
				_durationValue.PlaceholderText = UiLocalizer.T("—");
			}

			if (_followOption != null)
			{
				_followOption.SetBlockSignals(true);
				_followOption.Selected = -1;
				_followOption.SetBlockSignals(false);
			}

			// Leave colour picker as-is (no model load). User must open picker to commit a colour.

			if (_armedCheckBox != null)
				_armedCheckBox.SetPressedNoSignal(false);

			if (_skipIfDisarmedCheckBox != null)
				_skipIfDisarmedCheckBox.SetPressedNoSignal(false);

			if (_onlyOneActiveCheckBox != null)
				_onlyOneActiveCheckBox.SetPressedNoSignal(false);

			if (_isListeningForHotkey)
				CancelHotkeyListening();

			if (_hotkeyEnabledCheckBox != null)
				_hotkeyEnabledCheckBox.SetPressedNoSignal(false);

			if (_hotkeyBindingButton != null)
				_hotkeyBindingButton.Text = MultiPlaceholder;

			if (_hotkeyResetButton != null)
				_hotkeyResetButton.Visible = false;

			if (_clockEnabledCheckBox != null)
				_clockEnabledCheckBox.SetPressedNoSignal(false);

			if (_clockTimeInput != null)
			{
				_clockTimeInput.Text = "";
				_clockTimeInput.PlaceholderText = MultiPlaceholder;
			}

			if (_clockDaysButton != null)
				_clockDaysButton.Text = MultiPlaceholder;

			if (_clockDaysPopup != null && _clockDaysPopup.Visible)
				_clockDaysPopup.Hide();

			if (_clockResetButton != null)
				_clockResetButton.Visible = false;

			if (_isCapturingMidi)
				CancelMidiCapture();

			if (_midiEnabledCheckBox != null)
				_midiEnabledCheckBox.SetPressedNoSignal(false);
			if (_midiCaptureButton != null)
				_midiCaptureButton.Text = UiLocalizer.T("Capture");
			if (_midiResetButton != null)
				_midiResetButton.Visible = false;
			if (_midiTypeOption != null)
			{
				_midiTypeOption.SetBlockSignals(true);
				_midiTypeOption.Selected = -1;
				_midiTypeOption.SetBlockSignals(false);
			}
			if (_midiChannelSpin != null)
				_midiChannelSpin.SetValueNoSignal(0);
			if (_midiData1Spin != null)
				_midiData1Spin.SetValueNoSignal(0);
			if (_midiMatchValueCheck != null)
				_midiMatchValueCheck.SetPressedNoSignal(false);
			if (_midiData2Spin != null)
				_midiData2Spin.SetValueNoSignal(0);

			if (_notesTextEdit != null)
			{
				_notesTextEdit.Text = "";
				_notesTextEdit.PlaceholderText = MultiPlaceholder;
			}

			if (_memoCheckBox != null)
				_memoCheckBox.SetPressedNoSignal(false);
		}
		finally
		{
			_isRefreshingUi = false;
		}
	}

	/// <summary>
	/// Restores single-edit placeholders after leaving multi-edit.
	/// </summary>
	private void ClearMultiEditPlaceholders()
	{
		if (_cueNum != null)
			_cueNum.PlaceholderText = UiLocalizer.T("No Selection");
		if (_cueName != null)
			_cueName.PlaceholderText = UiLocalizer.T("No Selection");
		if (_preWaitInput != null)
			_preWaitInput.PlaceholderText = "";
		if (_postWaitInput != null)
			_postWaitInput.PlaceholderText = "";
		if (_durationValue != null)
			_durationValue.PlaceholderText = "";
		if (_clockTimeInput != null)
			_clockTimeInput.PlaceholderText = UiLocalizer.T("HH:mm:ss");
		if (_notesTextEdit != null)
			_notesTextEdit.PlaceholderText = UiLocalizer.T("Cue notes…");
		SyncDeleteHotkeyTooltip();
	}

	/// <summary>
	/// Builds a tooltip listing selected cue IDs (label itself stays short: "MULTI-EDITING").
	/// </summary>
	private static string FormatMultiEditIdTooltip(List<Cue> selected)
	{
		if (selected == null || selected.Count == 0)
			return "No cues selected.";

		string ids = string.Join(", ", selected.Select(c => c.Id));
		return $"Editing {selected.Count} cue(s).\nIDs: {ids}";
	}

	/// <summary>
	/// Keeps the follow OptionButton in sync when the mode is edited from the shell bar (single-edit only).
	/// </summary>
	private void OnFollowChanged(FollowType follow)
	{
		if (_isMultiEdit || _isRefreshingUi || _followOption == null) return;
		_isRefreshingUi = true;
		try
		{
			SelectFollowOption(follow);
		}
		finally
		{
			_isRefreshingUi = false;
		}
	}

	/// <summary>
	/// Selects the OptionButton item matching <paramref name="follow"/>.
	/// </summary>
	private void SelectFollowOption(FollowType follow)
	{
		if (_followOption == null) return;
		EnsureFollowOptions();
		for (int i = 0; i < _followOption.ItemCount; i++)
		{
			if (_followOption.GetItemMetadata(i).AsInt32() == (int)follow)
			{
				_followOption.Selected = i;
				return;
			}
		}
	}

	/// <summary>
	/// Ensures the follow OptionButton is populated with continue-mode labels.
	/// </summary>
	private void EnsureFollowOptions()
	{
		if (_followOption == null) return;
		if (_followOption.ItemCount > 0) return;

		_followOption.Clear();
		AddFollowOption(FollowType.None, "None");
		AddFollowOption(FollowType.Continue, "Auto-continue");
		AddFollowOption(FollowType.Follow, "Auto-follow");
	}

	/// <summary>
	/// Adds one continue-mode entry to the follow OptionButton.
	/// </summary>
	private void AddFollowOption(FollowType type, string label)
	{
		UiLocalizer.AddTranslatedItem(_followOption, label);
		_followOption.SetItemMetadata(_followOption.ItemCount - 1, (int)type);
	}

	/// <summary>
	/// Full refresh of shell inspector fields from the focused cue model (single-edit),
	/// or re-applies blank multi-edit UI when multi-editing.
	/// Wired to <see cref="GlobalSignals.SyncShellInspector"/>.
	/// </summary>
	public void UpdateFields()
	{
		if (!GodotObject.IsInstanceValid(this))
			return;

		// Setting toggle or selection may have changed while inspector stayed open.
		if (ShouldUseMultiEdit())
		{
			// Enter or re-enter when selection set changes. Do not wipe fields on every
			// SyncShellInspector (would clear text the user is mid-typing).
			if (!_isMultiEdit || NeedsMultiHeaderRefresh())
				EnterMultiEditMode();
			return;
		}

		// Left multi-edit (selection shrunk or setting off) — fall back to single if possible.
		if (_isMultiEdit)
		{
			_isMultiEdit = false;
			ClearMultiEditPlaceholders();
			int id = _focusedCueId >= 0
				? _focusedCueId
				: (ShellSelection.SelectedCues?.LastOrDefault()?.Id ?? -1);
			// Clear focus markers so ShellSelected reloads fields even if the id is unchanged.
			DetachFocusedCueEvents();
			_focusedCue = null;
			_focusedCueId = -1;
			if (id >= 0)
			{
				ShellSelected(id);
				return;
			}

			Visible = false;
			return;
		}

		if (_focusedCue == null)
			return;
		if (_preWaitInput == null || _postWaitInput == null || _durationValue == null)
			return;

		_isRefreshingUi = true;
		try
		{
			if (_cueNum != null)
				_cueNum.Text = _focusedCue.CueNum ?? string.Empty;
			if (_cueName != null)
				_cueName.Text = _focusedCue.Name ?? string.Empty;

			if (_cueId != null)
			{
				_cueId.Text = $"ID: {_focusedCue.Id}";
				_cueId.TooltipText = "";
			}

			if (_parentCueLabel != null)
			{
				if (_focusedCue.ParentId != -1)
				{
					var parent = CueList.FetchCueFromId(_focusedCue.ParentId);
					_parentCueLabel.Text = parent != null ? ("Parent: " + parent.Name) : "";
				}
				else
				{
					_parentCueLabel.Text = "";
				}
			}

			EnsureFollowOptions();
			SelectFollowOption(_focusedCue.Follow);

			_preWaitInput.Text = UiUtilities.FormatTime(_focusedCue.PreWait);
			_postWaitInput.Text = UiUtilities.FormatTime(_focusedCue.PostWait);
			var duration = _focusedCue.TotalDuration;
			if (duration < 0)
				_durationValue.Text = UiLocalizer.T("Until Stopped");
			else
				_durationValue.Text = UiUtilities.FormatTime(_focusedCue.TotalDuration);

			if (_colorPicker != null)
				_colorPicker.Color = _focusedCue.Color;

			if (_armedCheckBox != null)
				_armedCheckBox.SetPressedNoSignal(_focusedCue.Armed);
			if (_skipIfDisarmedCheckBox != null)
				_skipIfDisarmedCheckBox.SetPressedNoSignal(_focusedCue.SkipIfDisarmed);
			if (_onlyOneActiveCheckBox != null)
				_onlyOneActiveCheckBox.SetPressedNoSignal(_focusedCue.OnlyOneActiveInstance);

			RefreshHotkeyUi();
			RefreshClockUi();
			RefreshMidiUi();
			RefreshOscUi();

			if (_notesTextEdit != null)
			{
				// Avoid stomping caret while the operator is mid-type, but always
				// apply model state during undo/redo restore.
				bool restoring = _globalData?.HistoryManager?.IsRestoring == true;
				if (restoring || !_notesTextEdit.HasFocus())
					_notesTextEdit.Text = _focusedCue.Notes ?? string.Empty;
			}

			if (_memoCheckBox != null)
				_memoCheckBox.SetPressedNoSignal(_focusedCue.Memo);
		}
		finally
		{
			_isRefreshingUi = false;
		}
	}

	/// <summary>
	/// True when multi header should refresh because selection IDs changed.
	/// </summary>
	private bool NeedsMultiHeaderRefresh()
	{
		if (_cueId == null) return true;
		string expected = FormatMultiEditIdTooltip(GetSelectedCuesSnapshot());
		return _cueId.TooltipText != expected;
	}

	private void OnNameChanged(string name)
	{
		if (_isMultiEdit || _isRefreshingUi || _cueName == null) return;
		// Skip while the inspector field itself is being typed (TextChanged already owns that path).
		if (_cueName.HasFocus())
			return;

		_isRefreshingUi = true;
		try
		{
			_cueName.Text = name ?? string.Empty;
		}
		finally
		{
			_isRefreshingUi = false;
		}
	}

	/// <summary>
	/// Updates the inspector number field when the model changes from shell-bar (or other) edits.
	/// </summary>
	/// <param name="cueNum">New cue number string.</param>
	private void OnCueNumChanged(string cueNum)
	{
		if (_isMultiEdit || _isRefreshingUi || _cueNum == null) return;
		if (_cueNum.HasFocus())
			return;

		_isRefreshingUi = true;
		try
		{
			_cueNum.Text = cueNum ?? string.Empty;
		}
		finally
		{
			_isRefreshingUi = false;
		}
	}

	/// <summary>
	/// Updates the inspector notes field when the model changes from shell-bar memo edits.
	/// </summary>
	/// <param name="notes">New notes text.</param>
	private void OnNotesChangedFromModel(string notes)
	{
		if (_isMultiEdit || _isRefreshingUi || _notesTextEdit == null) return;
		if (_notesTextEdit.HasFocus())
			return;

		_isRefreshingUi = true;
		try
		{
			_notesTextEdit.Text = notes ?? string.Empty;
		}
		finally
		{
			_isRefreshingUi = false;
		}
	}

	/// <summary>
	/// Records history for the upcoming mutation (single cue or full cuelist for multi-edit).
	/// </summary>
	private void RecordHistoryBeforeEdit(string singleDescription, string multiDescription, string coalesceKey = null)
	{
		InspectorMultiEditSupport.RecordBeforeEdit(
			_globalData,
			_isMultiEdit,
			_focusedCue,
			singleDescription,
			multiDescription,
			coalesceKey);
	}

	private void EndCoalesceForCurrentEdit(string singleKeySuffix, string multiKey)
	{
		string singleKey = _focusedCueId >= 0 ? $"cue:{_focusedCueId}:{singleKeySuffix}" : null;
		InspectorMultiEditSupport.EndCoalesce(_globalData, _isMultiEdit, multiKey, singleKey);
	}

	private void OnCueNumFocusExited() => EndCoalesceForCurrentEdit("num", MultiNumCoalesceKey);

	private void OnCueNameFocusExited() => EndCoalesceForCurrentEdit("name", MultiNameCoalesceKey);

	/// <summary>
	/// Handles submission of time fields. Parses input and applies to edit target(s).
	/// </summary>
	private void TimeFieldSubmitted(string text, LineEdit textField)
	{
		var targets = GetEditTargets();
		if (targets.Count == 0 || textField == null) return;
		if (_isRefreshingUi || _globalData?.HistoryManager?.IsRestoring == true)
			return;

		try
		{
			// Blank pre/post wait → 0 (clear the wait). Other invalid input still reverts.
			string time;
			double timeSecs;
			string labeledTime;
			bool isValid;
			if (string.IsNullOrWhiteSpace(text)
			    && (textField == _preWaitInput || textField == _postWaitInput))
			{
				timeSecs = 0;
				time = UiUtilities.FormatTime(0);
				UiUtilities.ParseAndFormatTime(time, out _, out labeledTime, out _);
				isValid = true;
			}
			else
			{
				time = UiUtilities.ParseAndFormatTime(text, out timeSecs, out labeledTime, out isValid);
			}

			if (!isValid || string.IsNullOrEmpty(time))
			{
				_globalSignals.EmitSignal(nameof(GlobalSignals.Log),
					$"Invalid time format in {textField.Name}: {text}", 1);
				// Always re-sanitize so invalid text cannot stick in the LineEdit.
				RestoreShellTimeFieldDisplay(textField);
				if (textField.HasFocus())
					textField.ReleaseFocus();
				return;
			}

			textField.Text = time;
			textField.TooltipText = labeledTime;

			if (textField == _preWaitInput)
			{
				if (targets.All(c => Math.Abs(c.PreWait - timeSecs) < 1e-9))
				{
					if (textField.HasFocus())
						textField.ReleaseFocus();
					return;
				}
				RecordHistoryBeforeEdit("Edit pre-wait", "Multi-edit pre-wait");
				foreach (var cue in targets)
					cue.PreWait = timeSecs;
			}
			else if (textField == _postWaitInput)
			{
				if (targets.All(c => Math.Abs(c.PostWait - timeSecs) < 1e-9))
				{
					if (textField.HasFocus())
						textField.ReleaseFocus();
					return;
				}
				RecordHistoryBeforeEdit("Edit post-wait", "Multi-edit post-wait");
				foreach (var cue in targets)
					cue.PostWait = timeSecs;
			}

			if (!_isMultiEdit && _focusedCue != null && _durationValue != null)
			{
				var durationSecs = _focusedCue.CalculateTotalDuration();
				_durationValue.Text =
					UiUtilities.ParseAndFormatTime(durationSecs.ToString(), out var _, out string durLabeledTime);
				_durationValue.TooltipText = durLabeledTime;
			}

			foreach (var cue in targets)
				_globalSignals?.EmitSignal(nameof(GlobalSignals.UpdateShellBar), cue.Id);

			if (textField.HasFocus())
				textField.ReleaseFocus();
		}
		catch (Exception ex)
		{
			GD.Print($"ShellInspector:TimeFieldSubmitted - Error parsing time: {ex.Message}");
			_globalSignals.EmitSignal(nameof(GlobalSignals.Log), $"Error parsing time: {ex.Message}", 2);
			RestoreShellTimeFieldDisplay(textField);
			if (textField.HasFocus())
				textField.ReleaseFocus();
		}
	}

	/// <summary>
	/// Writes the current model pre/post wait back into a time LineEdit (after invalid input).
	/// </summary>
	private void RestoreShellTimeFieldDisplay(LineEdit textField)
	{
		if (textField == null || _focusedCue == null)
			return;

		double value = textField == _postWaitInput ? _focusedCue.PostWait : _focusedCue.PreWait;
		string formatted = UiUtilities.FormatTime(value);
		textField.Text = formatted;
		UiUtilities.ParseAndFormatTime(formatted, out _, out string labeled, out _);
		textField.TooltipText = labeled;
	}

	private void FollowOptionItemSelected(long index)
	{
		if (_isRefreshingUi) return;
		if (_globalData?.HistoryManager?.IsRestoring == true) return;

		var targets = GetEditTargets();
		if (targets.Count == 0) return;

		int selectedValue = _followOption.GetItemMetadata((int)index).AsInt32();
		var follow = (FollowType)selectedValue;

		// Skip no-op when single-edit and already set.
		if (!_isMultiEdit && targets.Count == 1 && targets[0].Follow == follow)
			return;

		RecordHistoryBeforeEdit("Edit continue mode", "Multi-edit continue mode");
		foreach (var cue in targets)
		{
			if (cue.Follow == follow) continue;
			cue.Follow = follow;
			_globalSignals?.EmitSignal(nameof(GlobalSignals.UpdateShellBar), cue.Id);
		}
	}

	private void AssignColor()
	{
		if (_isRefreshingUi) return;

		var targets = GetEditTargets();
		if (targets.Count == 0 || _colorPicker == null) return;

		Color color = _colorPicker.Color;
		GD.Print($"ShellInspector:AssignColor - Assigning color. {color.R}, {color.G}, {color.B}");

		if (!_isMultiEdit && targets.Count == 1 && targets[0].Color.IsEqualApprox(color))
			return;

		RecordHistoryBeforeEdit("Edit cue color", "Multi-edit cue colour");
		foreach (var cue in targets)
		{
			if (cue.Color.IsEqualApprox(color)) continue;
			cue.Color = color;
			_globalSignals?.EmitSignal(nameof(GlobalSignals.UpdateShellBar), cue.Id);
		}
	}

	/// <summary>
	/// Toggles armed state for edit target(s).
	/// </summary>
	private void OnArmedToggled(bool pressed)
	{
		if (_isRefreshingUi) return;
		if (_globalData?.HistoryManager?.IsRestoring == true) return;

		var targets = GetEditTargets();
		if (targets.Count == 0) return;

		if (!_isMultiEdit && targets.Count == 1 && targets[0].Armed == pressed)
			return;

		RecordHistoryBeforeEdit(
			pressed ? "Arm cue" : "Disarm cue",
			pressed ? "Multi-edit arm cues" : "Multi-edit disarm cues");

		foreach (var cue in targets)
		{
			if (cue.Armed == pressed) continue;
			cue.Armed = pressed;
			_globalSignals?.EmitSignal(nameof(GlobalSignals.UpdateShellBar), cue.Id);
		}
	}

	/// <summary>
	/// Toggles skip-if-disarmed for edit target(s).
	/// </summary>
	private void OnSkipIfDisarmedToggled(bool pressed)
	{
		if (_isRefreshingUi) return;
		if (_globalData?.HistoryManager?.IsRestoring == true) return;

		var targets = GetEditTargets();
		if (targets.Count == 0) return;

		if (!_isMultiEdit && targets.Count == 1 && targets[0].SkipIfDisarmed == pressed)
			return;

		RecordHistoryBeforeEdit(
			pressed ? "Enable skip if disarmed" : "Disable skip if disarmed",
			pressed ? "Multi-edit enable skip if disarmed" : "Multi-edit disable skip if disarmed");

		foreach (var cue in targets)
		{
			if (cue.SkipIfDisarmed == pressed) continue;
			cue.SkipIfDisarmed = pressed;
			_globalSignals?.EmitSignal(nameof(GlobalSignals.UpdateShellBar), cue.Id);
		}
	}

	/// <summary>
	/// Toggles only-one-active-instance for edit target(s).
	/// </summary>
	private void OnOnlyOneActiveToggled(bool pressed)
	{
		if (_isRefreshingUi) return;
		if (_globalData?.HistoryManager?.IsRestoring == true) return;

		var targets = GetEditTargets();
		if (targets.Count == 0) return;

		if (!_isMultiEdit && targets.Count == 1 && targets[0].OnlyOneActiveInstance == pressed)
			return;

		RecordHistoryBeforeEdit(
			pressed ? "Enable only one active instance" : "Allow multiple active instances",
			pressed ? "Multi-edit only one active instance" : "Multi-edit allow multiple active instances");

		foreach (var cue in targets)
		{
			if (cue.OnlyOneActiveInstance == pressed) continue;
			cue.OnlyOneActiveInstance = pressed;
		}
	}

	private void OnCueNumTextChanged(string data)
	{
		if (_isRefreshingUi) return;

		var targets = GetEditTargets();
		if (targets.Count == 0) return;

		string coalesce = _isMultiEdit ? MultiNumCoalesceKey : $"cue:{_focusedCueId}:num";
		RecordHistoryBeforeEdit("Edit cue number", "Multi-edit cue number", coalesce);

		foreach (var cue in targets)
			cue.CueNum = data;
	}

	private void OnCueNameTextChanged(string data)
	{
		if (_isRefreshingUi) return;

		var targets = GetEditTargets();
		if (targets.Count == 0) return;

		string coalesce = _isMultiEdit ? MultiNameCoalesceKey : $"cue:{_focusedCueId}:name";
		RecordHistoryBeforeEdit("Edit cue name", "Multi-edit cue name", coalesce);

		foreach (var cue in targets)
			cue.Name = data;
	}

	private void OnNotesTextChanged()
	{
		if (_isRefreshingUi || _notesTextEdit == null) return;

		var targets = GetEditTargets();
		if (targets.Count == 0) return;

		string text = _notesTextEdit.Text ?? string.Empty;

		// Skip no-op when single-edit and unchanged.
		if (!_isMultiEdit && targets.Count == 1 &&
		    string.Equals(targets[0].Notes ?? string.Empty, text, StringComparison.Ordinal))
			return;

		string coalesce = _isMultiEdit ? MultiNotesCoalesceKey : $"cue:{_focusedCueId}:notes";
		RecordHistoryBeforeEdit("Edit cue notes", "Multi-edit cue notes", coalesce);

		foreach (var cue in targets)
			cue.Notes = text;
	}

	private void OnNotesFocusExited() => EndCoalesceForCurrentEdit("notes", MultiNotesCoalesceKey);

	/// <summary>
	/// Toggles memo shell layout for edit target(s): notes replace number/name/times on the shell bar.
	/// </summary>
	private void OnMemoToggled(bool pressed)
	{
		if (_isRefreshingUi) return;
		if (_globalData?.HistoryManager?.IsRestoring == true) return;

		var targets = GetEditTargets();
		if (targets.Count == 0) return;

		if (!_isMultiEdit && targets.Count == 1 && targets[0].Memo == pressed)
			return;

		RecordHistoryBeforeEdit(
			pressed ? "Enable memo mode" : "Disable memo mode",
			pressed ? "Multi-edit enable memo mode" : "Multi-edit disable memo mode");

		foreach (var cue in targets)
		{
			if (cue.Memo == pressed) continue;
			cue.Memo = pressed;
			_globalSignals?.EmitSignal(nameof(GlobalSignals.UpdateShellBar), cue.Id);
		}
	}

	// ── Hotkey trigger UI ───────────────────────────────────────────────────

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

		// Measure content, then place under the button. Popup() uses screen coords when native
		// and embedder-local coords when Linux popup-embed is active.
		_clockDaysPopup.ResetSize();
		var size = _clockDaysPopup.GetContentsMinimumSize();
		int width = Math.Max((int)Math.Ceiling(size.X), 120);
		int height = Math.Max((int)Math.Ceiling(size.Y), 10);

		var screenXform = _clockDaysButton.GetScreenTransform();
		Vector2 buttonTopLeft = screenXform.Origin;
		Vector2 buttonBottomLeft = screenXform * new Vector2(0f, _clockDaysButton.Size.Y);
		Vector2I popupPos = UiUtilities.ScreenPointToPopupPosition(
			_clockDaysPopup,
			new Vector2(buttonTopLeft.X, buttonBottomLeft.Y + 2f));

		_clockDaysPopup.Popup(new Rect2I(popupPos, new Vector2I(width, height)));
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

