// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System;
using Cue2.Domain.Cues;
using Cue2.Services;
using Cue2.UI.Utilities;
using Godot;

namespace Cue2.UI.Inspectors;

/// <summary>
/// Inspector card for a <see cref="MidiOutputComponent"/> on a cue.
/// Lets the user pick a session MIDI output device and configure the message to send on GO.
/// </summary>
public partial class InspectorMidiOutputCard : PanelContainer
{
    private Label _nameLabel;
    private OptionButton _deviceOption;
    private OptionButton _typeOption;
    private SpinBox _channelSpin;
    private SpinBox _data1Spin;
    private SpinBox _data2Spin;
    private SpinBox _durationSpin;
    private Button _testSendButton;
    private Button _deleteButton;

    private MidiOutputComponent _component;
    private ConnectionInspector _inspector;
    private MidiManager _midiManager;
    private bool _syncing;

    public override void _Ready()
    {
        _nameLabel = GetNodeOrNull<Label>("%NameLabel");
        _deviceOption = GetNodeOrNull<OptionButton>("%DeviceOption");
        _typeOption = GetNodeOrNull<OptionButton>("%TypeOption");
        _channelSpin = GetNodeOrNull<SpinBox>("%ChannelSpin");
        _data1Spin = GetNodeOrNull<SpinBox>("%Data1Spin");
        _data2Spin = GetNodeOrNull<SpinBox>("%Data2Spin");
        _durationSpin = GetNodeOrNull<SpinBox>("%DurationSpin");
        _testSendButton = GetNodeOrNull<Button>("%TestSendButton");
        _deleteButton = GetNodeOrNull<Button>("%DeleteButton");
        _midiManager = GetNodeOrNull<MidiManager>("/root/MidiManager");

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

        EnsureTypeOptions();
        if (_deviceOption != null)
            _deviceOption.ItemSelected += OnDeviceSelected;
        if (_typeOption != null)
            _typeOption.ItemSelected += OnTypeSelected;
        if (_channelSpin != null)
            _channelSpin.ValueChanged += OnChannelChanged;
        if (_data1Spin != null)
            _data1Spin.ValueChanged += OnData1Changed;
        if (_data2Spin != null)
            _data2Spin.ValueChanged += OnData2Changed;
        if (_durationSpin != null)
            _durationSpin.ValueChanged += OnDurationChanged;
        if (_testSendButton != null)
            _testSendButton.Pressed += OnTestSendPressed;
    }

    /// <summary>
    /// Binds this card to a component and parent inspector.
    /// </summary>
    /// <param name="component">The MIDI output component to edit.</param>
    /// <param name="inspector">Parent connection inspector (for remove).</param>
    public void SetComponent(MidiOutputComponent component, ConnectionInspector inspector)
    {
        _component = component;
        _inspector = inspector;
        SyncFromComponent();
    }

    private void EnsureTypeOptions()
    {
        if (_typeOption == null || _typeOption.ItemCount > 0) return;
        _typeOption.Clear();
        AddType(MidiTriggerMessageType.NoteOn, "Note On");
        AddType(MidiTriggerMessageType.NoteOff, "Note Off");
        AddType(MidiTriggerMessageType.ControlChange, "CC");
        AddType(MidiTriggerMessageType.ProgramChange, "Program");
    }

    private void AddType(MidiTriggerMessageType type, string label)
    {
        int i = _typeOption.ItemCount;
        _typeOption.AddItem(label);
        _typeOption.SetItemMetadata(i, (int)type);
    }

    private void PopulateDeviceOptions()
    {
        if (_deviceOption == null) return;

        _deviceOption.Clear();
        var session = _midiManager?.SessionOutputNames;
        string current = _component?.OutputDeviceName ?? string.Empty;
        bool foundCurrent = false;

        if (session != null)
        {
            foreach (string name in session)
            {
                if (string.IsNullOrEmpty(name)) continue;
                int idx = _deviceOption.ItemCount;
                string label = name;
                if (_midiManager != null && !_midiManager.IsOutputOpen(name))
                    label = $"{name} (offline)";
                _deviceOption.AddItem(label);
                _deviceOption.SetItemMetadata(idx, name);
                if (string.Equals(name, current, StringComparison.OrdinalIgnoreCase))
                {
                    _deviceOption.Select(idx);
                    foundCurrent = true;
                }
            }
        }

        // Keep a stored device name even if it is no longer in the session list.
        if (!foundCurrent && !string.IsNullOrEmpty(current))
        {
            int idx = _deviceOption.ItemCount;
            _deviceOption.AddItem($"{current} (missing)");
            _deviceOption.SetItemMetadata(idx, current);
            _deviceOption.Select(idx);
            foundCurrent = true;
        }

        if (!foundCurrent)
        {
            _deviceOption.AddItem(UiLocalizer.T("(no device)"));
            _deviceOption.SetItemMetadata(0, string.Empty);
            _deviceOption.Select(0);
        }
    }

    private void SyncFromComponent()
    {
        if (_component == null) return;
        _syncing = true;
        try
        {
            if (_nameLabel != null)
                _nameLabel.Text = UiLocalizer.T("MIDI Output");

            PopulateDeviceOptions();
            EnsureTypeOptions();

            if (_typeOption != null)
            {
                int want = (int)_component.MessageType;
                for (int i = 0; i < _typeOption.ItemCount; i++)
                {
                    if (_typeOption.GetItemMetadata(i).AsInt32() == want)
                    {
                        _typeOption.Selected = i;
                        break;
                    }
                }
            }

            _channelSpin?.SetValueNoSignal(_component.Channel);
            _data1Spin?.SetValueNoSignal(_component.Data1);
            _data2Spin?.SetValueNoSignal(_component.Data2);
            _durationSpin?.SetValueNoSignal(_component.NoteDurationSeconds);
            UpdateFieldPrefixes();
        }
        finally
        {
            _syncing = false;
        }
    }

    private void UpdateFieldPrefixes()
    {
        if (_component == null) return;
        if (_data1Spin != null)
        {
            _data1Spin.Prefix = _component.MessageType switch
            {
                MidiTriggerMessageType.ControlChange => "cc",
                MidiTriggerMessageType.ProgramChange => "p",
                _ => "n"
            };
        }
        if (_data2Spin != null)
        {
            bool useVel = _component.MessageType is MidiTriggerMessageType.NoteOn
                or MidiTriggerMessageType.NoteOff
                or MidiTriggerMessageType.ControlChange;
            _data2Spin.Editable = useVel;
            _data2Spin.Prefix = _component.MessageType == MidiTriggerMessageType.ControlChange ? "val" : "v";
        }
        if (_durationSpin != null)
            _durationSpin.Editable = _component.MessageType == MidiTriggerMessageType.NoteOn;
    }

    private void RecordHistory(string description)
    {
        var gd = GetNodeOrNull<GlobalData>("/root/GlobalData");
        if (gd == null) return;
        InspectorMultiEditSupport.RecordBeforeEditById(
            gd, multiHistory: false, gd.FocusedCue, description);
    }

    private void OnDeviceSelected(long index)
    {
        if (_syncing || _component == null || _deviceOption == null) return;
        if (index < 0 || index >= _deviceOption.ItemCount) return;

        string name = _deviceOption.GetItemMetadata((int)index).AsString() ?? string.Empty;
        if (string.Equals(_component.OutputDeviceName, name, StringComparison.OrdinalIgnoreCase))
            return;

        RecordHistory("Edit MIDI output device");
        _component.OutputDeviceName = name;
    }

    private void OnTypeSelected(long index)
    {
        if (_syncing || _component == null || _typeOption == null) return;
        var type = (MidiTriggerMessageType)_typeOption.GetItemMetadata((int)index).AsInt32();
        if (_component.MessageType == type) return;
        RecordHistory("Edit MIDI output type");
        _component.MessageType = type;
        UpdateFieldPrefixes();
    }

    private void OnChannelChanged(double value)
    {
        if (_syncing || _component == null) return;
        int ch = Math.Clamp((int)value, 1, 16);
        if (_component.Channel == ch) return;
        RecordHistory("Edit MIDI output channel");
        _component.Channel = ch;
    }

    private void OnData1Changed(double value)
    {
        if (_syncing || _component == null) return;
        int d = Math.Clamp((int)value, 0, 127);
        if (_component.Data1 == d) return;
        RecordHistory("Edit MIDI output data");
        _component.Data1 = d;
    }

    private void OnData2Changed(double value)
    {
        if (_syncing || _component == null) return;
        int d = Math.Clamp((int)value, 0, 127);
        if (_component.Data2 == d) return;
        RecordHistory("Edit MIDI output value");
        _component.Data2 = d;
    }

    private void OnDurationChanged(double value)
    {
        if (_syncing || _component == null) return;
        double d = Math.Max(0, value);
        if (Math.Abs(_component.NoteDurationSeconds - d) < 1e-9) return;
        RecordHistory("Edit MIDI note duration");
        _component.NoteDurationSeconds = d;
    }

    private void OnTestSendPressed()
    {
        if (_component == null) return;

        if (string.IsNullOrWhiteSpace(_component.OutputDeviceName))
        {
            var gs = GetNodeOrNull<GlobalSignals>("/root/GlobalSignals");
            gs?.EmitSignal(nameof(GlobalSignals.Log),
                "MIDI Test Send: no output device selected.", (int)LogType.Warning);
            return;
        }

        // Fire immediately without waiting on the async GO path.
        _ = _component.Execute();
    }

    private void OnDeletePressed()
    {
        if (_component == null || _inspector == null) return;
        _inspector.RemoveComponent(_component);
        QueueFree();
    }
}
