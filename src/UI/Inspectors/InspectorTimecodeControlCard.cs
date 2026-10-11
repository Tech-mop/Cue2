// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System;
using Cue2.Domain.Cues;
using Cue2.Domain.Timecode;
using Cue2.Services;
using Cue2.UI.Timecode;
using Cue2.UI.Utilities;
using Godot;

namespace Cue2.UI.Inspectors;

/// <summary>
/// Inspector card for a <see cref="TimecodeControlComponent"/>.
/// Picks a session timecode output and the generator action to run on GO.
/// </summary>
public partial class InspectorTimecodeControlCard : PanelContainer
{
    private Label _nameLabel;
    private Button _deleteButton;
    private TimecodeClockCard _clockCard;
    private OptionButton _outputOption;
    private OptionButton _actionOption;
    private HBoxContainer _startTimeRow;
    private CheckBox _useStartTimeCheck;
    private LineEdit _timeInput;
    private HBoxContainer _offsetRow;
    private LineEdit _offsetInput;
    private HBoxContainer _durationRow;
    private LineEdit _durationInput;

    private TimecodeControlComponent _component;
    private ConnectionInspector _inspector;
    private TimecodeManager _timecodeManager;
    private GlobalData _globalData;
    private GlobalSignals _globalSignals;
    private bool _syncing;

    public override void _Ready()
    {
        _timecodeManager = GetNodeOrNull<TimecodeManager>("/root/TimecodeManager");
        _globalData = GetNodeOrNull<GlobalData>("/root/GlobalData");
        _globalSignals = GetNodeOrNull<GlobalSignals>("/root/GlobalSignals");
        _nameLabel = GetNodeOrNull<Label>("%NameLabel");
        _deleteButton = GetNodeOrNull<Button>("%DeleteButton");
        _clockCard = GetNodeOrNull<TimecodeClockCard>("%ClockCard");
        _outputOption = GetNodeOrNull<OptionButton>("%OutputOption");
        _actionOption = GetNodeOrNull<OptionButton>("%ActionOption");
        _startTimeRow = GetNodeOrNull<HBoxContainer>("%StartTimeRow");
        _useStartTimeCheck = GetNodeOrNull<CheckBox>("%UseStartTimeCheck");
        _timeInput = GetNodeOrNull<LineEdit>("%TimeInput");
        _offsetRow = GetNodeOrNull<HBoxContainer>("%OffsetRow");
        _offsetInput = GetNodeOrNull<LineEdit>("%OffsetInput");
        _durationRow = GetNodeOrNull<HBoxContainer>("%DurationRow");
        _durationInput = GetNodeOrNull<LineEdit>("%DurationInput");

        if (_clockCard != null)
            _clockCard.EnableTransport = false;

        if (_deleteButton != null)
        {
            try
            {
                _deleteButton.Icon = GetThemeIcon("DeleteBin", "AtlasIcons");
                _deleteButton.ExpandIcon = true;
            }
            catch { /* optional icon */ }
            _deleteButton.Pressed += OnDeletePressed;
        }

        if (_outputOption != null)
            _outputOption.ItemSelected += OnOutputSelected;
        if (_actionOption != null)
            _actionOption.ItemSelected += OnActionSelected;
        if (_useStartTimeCheck != null)
            _useStartTimeCheck.Toggled += OnUseStartTimeToggled;
        if (_timeInput != null)
        {
            _timeInput.TextSubmitted += OnTimeSubmitted;
            _timeInput.FocusExited += OnTimeFocusExited;
        }
        if (_offsetInput != null)
        {
            _offsetInput.TextSubmitted += OnOffsetSubmitted;
            _offsetInput.FocusExited += OnOffsetFocusExited;
        }
        if (_durationInput != null)
        {
            _durationInput.TextSubmitted += OnDurationSubmitted;
            _durationInput.FocusExited += OnDurationFocusExited;
        }

        if (_timecodeManager != null)
            _timecodeManager.TimecodeStateChanged += OnTimecodeStateChanged;
    }

    public override void _ExitTree()
    {
        if (_timecodeManager != null)
            _timecodeManager.TimecodeStateChanged -= OnTimecodeStateChanged;
        if (_deleteButton != null)
            _deleteButton.Pressed -= OnDeletePressed;
        if (_outputOption != null)
            _outputOption.ItemSelected -= OnOutputSelected;
        if (_actionOption != null)
            _actionOption.ItemSelected -= OnActionSelected;
        if (_useStartTimeCheck != null)
            _useStartTimeCheck.Toggled -= OnUseStartTimeToggled;
        if (_timeInput != null)
        {
            _timeInput.TextSubmitted -= OnTimeSubmitted;
            _timeInput.FocusExited -= OnTimeFocusExited;
        }
        if (_offsetInput != null)
        {
            _offsetInput.TextSubmitted -= OnOffsetSubmitted;
            _offsetInput.FocusExited -= OnOffsetFocusExited;
        }
        if (_durationInput != null)
        {
            _durationInput.TextSubmitted -= OnDurationSubmitted;
            _durationInput.FocusExited -= OnDurationFocusExited;
        }
        base._ExitTree();
    }

    /// <summary>
    /// Binds this card to a component and parent inspector.
    /// </summary>
    /// <param name="component">Timecode control component.</param>
    /// <param name="inspector">Parent connection inspector.</param>
    public void SetComponent(TimecodeControlComponent component, ConnectionInspector inspector)
    {
        _component = component;
        _inspector = inspector;
        PopulateActionOptions();
        SyncFromComponent();
    }

    private void OnTimecodeStateChanged()
    {
        if (_component != null)
            SyncFromComponent();
    }

    private void SyncFromComponent()
    {
        if (_component == null) return;
        _syncing = true;
        try
        {
            PopulateOutputOptions();
            SelectAction(_component.Action);
            if (_useStartTimeCheck != null)
                _useStartTimeCheck.SetPressedNoSignal(_component.UseStartTime);
            WriteTimeFieldsFromComponent();
            RefreshFieldVisibility();
            RefreshClock();
        }
        finally
        {
            _syncing = false;
        }
    }

    private void PopulateActionOptions()
    {
        if (_actionOption == null) return;
        _actionOption.Clear();
        foreach (TimecodeControlAction action in Enum.GetValues<TimecodeControlAction>())
            UiLocalizer.AddTranslatedItem(_actionOption, TimecodeControlComponent.GetActionDisplayName(action), (int)action);
    }

    private void PopulateOutputOptions()
    {
        if (_outputOption == null) return;
        _outputOption.Clear();

        var outputs = _timecodeManager?.Outputs;
        if (outputs == null || outputs.Count == 0)
        {
            UiLocalizer.AddTranslatedItem(_outputOption, "(No timecode outputs)");
            _outputOption.SetItemDisabled(0, true);
            _outputOption.Select(0);
            return;
        }

        int select = 0;
        for (int i = 0; i < outputs.Count; i++)
        {
            var endpoint = outputs[i];
            string label = $"{endpoint.Name} ({endpoint.Kind.GetShortName()})";
            _outputOption.AddItem(label);
            _outputOption.SetItemMetadata(i, endpoint.Id);
            if (endpoint.Id == _component.EndpointId)
                select = i;
        }

        if (_component.EndpointId < 0 && outputs.Count > 0)
        {
            _component.EndpointId = outputs[0].Id;
            select = 0;
        }

        _outputOption.Select(select);
    }

    private void SelectAction(TimecodeControlAction action)
    {
        if (_actionOption == null) return;
        for (int i = 0; i < _actionOption.ItemCount; i++)
        {
            if (_actionOption.GetItemId(i) == (int)action)
            {
                _actionOption.Select(i);
                return;
            }
        }
    }

    private void RefreshFieldVisibility()
    {
        if (_component == null) return;
        var action = _component.Action;
        bool showTime = action == TimecodeControlAction.SetTime
                        || action == TimecodeControlAction.Play
                        || action == TimecodeControlAction.PlayForDuration;
        bool showUseStart = action == TimecodeControlAction.Play
                            || action == TimecodeControlAction.PlayForDuration;
        bool timeEnabled = action == TimecodeControlAction.SetTime || _component.UseStartTime;

        if (_startTimeRow != null)
            _startTimeRow.Visible = showTime;
        if (_useStartTimeCheck != null)
            _useStartTimeCheck.Visible = showUseStart;
        SetLineEditable(_timeInput, timeEnabled);
        if (_offsetRow != null)
            _offsetRow.Visible = action == TimecodeControlAction.OffsetTime;
        if (_durationRow != null)
            _durationRow.Visible = action == TimecodeControlAction.PlayForDuration;
    }

    private static void SetLineEditable(LineEdit edit, bool editable)
    {
        if (edit == null) return;
        edit.Editable = editable;
        edit.Modulate = editable ? Colors.White : new Color(1, 1, 1, 0.45f);
    }

    private TimecodeFrameRate BoundFrameRate()
    {
        var endpoint = _timecodeManager?.FindEndpoint(_component?.EndpointId ?? -1);
        return endpoint?.FrameRate ?? TimecodeFrameRate.Fps30;
    }

    private void WriteTimeFieldsFromComponent()
    {
        if (_component == null) return;
        var rate = BoundFrameRate();
        if (_timeInput != null && !_timeInput.HasFocus())
        {
            var value = new TimecodeValue(
                _component.SetHours,
                _component.SetMinutes,
                _component.SetSeconds,
                _component.SetFrames,
                rate);
            _timeInput.Text = value.ToDisplayString();
        }

        if (_offsetInput != null && !_offsetInput.HasFocus())
            _offsetInput.Text = TimecodeValue.FormatElapsed(_component.OffsetSeconds, rate, allowNegative: true);

        if (_durationInput != null && !_durationInput.HasFocus())
            _durationInput.Text = TimecodeValue.FormatElapsed(_component.PlayDurationSeconds, rate);
    }

    private void RefreshClock()
    {
        if (_clockCard == null || _component == null) return;
        _clockCard.EnableTransport = false;
        if (_component.EndpointId >= 0)
            _clockCard.Bind(_component.EndpointId);
    }

    private void Record(string description)
    {
        if (_inspector == null || _component == null) return;
        if (_globalData?.HistoryManager?.IsRestoring == true) return;
        InspectorMultiEditSupport.RecordBeforeEdit(
            _globalData, multiHistory: false, _inspector.FocusedCueForHistory, description);
    }

    private void OnDeletePressed()
    {
        _inspector?.RemoveComponent(_component);
    }

    private void OnOutputSelected(long index)
    {
        if (_syncing || _component == null || _outputOption == null) return;
        if (index < 0 || index >= _outputOption.ItemCount) return;
        int id = (int)_outputOption.GetItemMetadata((int)index);
        if (_component.EndpointId == id) return;
        Record("Set timecode output");
        _component.EndpointId = id;
        RefreshClock();
        WriteTimeFieldsFromComponent();
    }

    private void OnActionSelected(long index)
    {
        if (_syncing || _component == null || _actionOption == null) return;
        if (index < 0 || index >= _actionOption.ItemCount) return;
        var next = (TimecodeControlAction)_actionOption.GetItemId((int)index);
        if (_component.Action == next) return;
        Record($"Set timecode action {TimecodeControlComponent.GetActionDisplayName(next)}");
        _component.Action = next;
        RefreshFieldVisibility();
        WriteTimeFieldsFromComponent();
        _inspector?.NotifyCueDurationMaybeChanged();
    }

    private void OnUseStartTimeToggled(bool pressed)
    {
        if (_syncing || _component == null) return;
        if (_component.UseStartTime == pressed) return;
        Record(pressed ? "Enable timecode start time" : "Disable timecode start time");
        _component.UseStartTime = pressed;
        RefreshFieldVisibility();
    }

    private void OnTimeSubmitted(string text)
    {
        CommitStartTime(text);
        _timeInput?.ReleaseFocus();
    }

    private void OnTimeFocusExited()
    {
        if (_syncing || _timeInput == null) return;
        CommitStartTime(_timeInput.Text);
    }

    private void OnOffsetSubmitted(string text)
    {
        CommitOffset(text);
        _offsetInput?.ReleaseFocus();
    }

    private void OnOffsetFocusExited()
    {
        if (_syncing || _offsetInput == null) return;
        CommitOffset(_offsetInput.Text);
    }

    private void OnDurationSubmitted(string text)
    {
        CommitDuration(text);
        _durationInput?.ReleaseFocus();
    }

    private void OnDurationFocusExited()
    {
        if (_syncing || _durationInput == null) return;
        CommitDuration(_durationInput.Text);
    }

    private void CommitStartTime(string text)
    {
        if (_syncing || _component == null) return;
        var rate = BoundFrameRate();
        if (!TimecodeValue.TryParseUserInput(text, rate, out var value))
        {
            WarnInvalidTimecode(text);
            WriteTimeFieldsFromComponent();
            return;
        }

        if (_component.SetHours == value.Hours && _component.SetMinutes == value.Minutes
            && _component.SetSeconds == value.Seconds && _component.SetFrames == value.Frames)
        {
            if (_timeInput != null)
                _timeInput.Text = value.ToDisplayString();
            return;
        }

        Record("Set timecode start time");
        _component.SetHours = value.Hours;
        _component.SetMinutes = value.Minutes;
        _component.SetSeconds = value.Seconds;
        _component.SetFrames = value.Frames;
        if (_timeInput != null)
            _timeInput.Text = value.ToDisplayString();
    }

    private void CommitOffset(string text)
    {
        if (_syncing || _component == null) return;
        var rate = BoundFrameRate();
        if (!TimecodeValue.TryParseUserElapsed(text, rate, out double magnitude, out int sign))
        {
            WarnInvalidTimecode(text);
            WriteTimeFieldsFromComponent();
            return;
        }

        double seconds = sign * magnitude;
        if (Math.Abs(_component.OffsetSeconds - seconds) < 1e-9)
        {
            if (_offsetInput != null)
                _offsetInput.Text = TimecodeValue.FormatElapsed(seconds, rate, allowNegative: true);
            return;
        }

        Record("Set timecode offset");
        _component.OffsetSeconds = seconds;
        if (_offsetInput != null)
            _offsetInput.Text = TimecodeValue.FormatElapsed(seconds, rate, allowNegative: true);
    }

    private void CommitDuration(string text)
    {
        if (_syncing || _component == null) return;
        var rate = BoundFrameRate();
        if (!TimecodeValue.TryParseUserElapsed(text, rate, out double magnitude, out int sign) || sign < 0)
        {
            WarnInvalidTimecode(text);
            WriteTimeFieldsFromComponent();
            return;
        }

        double seconds = Math.Max(0, magnitude);
        if (Math.Abs(_component.PlayDurationSeconds - seconds) < 1e-9)
        {
            if (_durationInput != null)
                _durationInput.Text = TimecodeValue.FormatElapsed(seconds, rate);
            return;
        }

        Record("Set timecode play duration");
        _component.PlayDurationSeconds = seconds;
        if (_durationInput != null)
            _durationInput.Text = TimecodeValue.FormatElapsed(seconds, rate);
        _inspector?.NotifyCueDurationMaybeChanged();
    }

    private void WarnInvalidTimecode(string text)
    {
        _globalSignals?.EmitSignal(
            nameof(GlobalSignals.Log),
            $"Invalid timecode: \"{text}\". Use HH:MM:SS:FF, MM:SS:FF, SS:FF, or seconds (optional leading -).",
            (int)LogType.Warning);
        GD.Print($"InspectorTimecodeControlCard:WarnInvalidTimecode - '{text}'");
    }
}
