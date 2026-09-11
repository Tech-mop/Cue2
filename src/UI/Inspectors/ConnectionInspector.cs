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

namespace Cue2.UI.Inspectors;

public partial class ConnectionInspector : Control
{
    private GlobalData _globalData;
    private GlobalSignals _globalSignals;
    private HistoryManager _historyManager;

    private PackedScene _cueLightComponentCardScene = SceneLoader.LoadPackedScene("uid://cfl3cwoqby4lo", out string _);
    private PackedScene _oscComponentCardScene = SceneLoader.LoadPackedScene("uid://cst0ttvboq673", out string _);
    private PackedScene _midiOutputCardScene = SceneLoader.LoadPackedScene(
        "res://src/UI/Inspectors/InspectorMidiOutputCard.tscn", out string _);

    private Cue _focusedCue;
    private MidiManager _midiManager;

    private Label _infoLabel;

    private FlowContainer _connectionCardContainer;
    private PanelContainer _blankConnectionCard;
    private OptionButton _availableConnectionsButton;

    /// <summary>True while rebuilding cards from model so child handlers do not re-record.</summary>
    private bool _isSyncingUi;
    
    
    public override void _Ready()
    {
        _globalData = GetNode<GlobalData>("/root/GlobalData");
        _globalSignals = GetNode<GlobalSignals>("/root/GlobalSignals");
        _historyManager = _globalData?.HistoryManager;
        _midiManager = GetNodeOrNull<MidiManager>("/root/MidiManager");

        _globalSignals.ShellFocused += ShellSelected;
        if (_historyManager != null)
            _historyManager.HistoryRestored += OnHistoryRestored;
        if (_midiManager != null)
            _midiManager.MidiStateChanged += OnMidiStateChanged;

        _infoLabel = GetNode<Label>("InfoLabel");
        _infoLabel.AddThemeColorOverride("font_color", GlobalStyles.DisabledColor);
        
        _connectionCardContainer = GetNode<FlowContainer>("%ConnectionCardContainer");
        _connectionCardContainer.Visible = false;
        
        _blankConnectionCard = GetNode<PanelContainer>("%BlankConnectionCard");
        _availableConnectionsButton = GetNode<OptionButton>("%AvailableConnectionsButton");

        VisibilityChanged += LoadConnections;
        _availableConnectionsButton.ItemSelected += OnConnectionSelected;
        
        LoadConnections();
    
        UiLocalizer.LocalizeTree(this);
        if (_globalSignals != null)
            _globalSignals.LocaleChanged += OnLocaleChanged;
}

    public override void _ExitTree()
    {
        if (_globalSignals != null)
            _globalSignals.LocaleChanged -= OnLocaleChanged;

        if (_globalSignals != null)
            _globalSignals.ShellFocused -= ShellSelected;
        if (_historyManager != null)
            _historyManager.HistoryRestored -= OnHistoryRestored;
        if (_midiManager != null)
            _midiManager.MidiStateChanged -= OnMidiStateChanged;
        if (_availableConnectionsButton != null)
            _availableConnectionsButton.ItemSelected -= OnConnectionSelected;
        base._ExitTree();
    }

    /// <summary>
    /// After cue undo/redo, rebuild connection cards from the restored model.
    /// </summary>
    private void OnHistoryRestored(int scope)
    {
        if (scope != (int)HistoryManager.HistoryScope.Cue
            && scope != (int)HistoryManager.HistoryScope.Cuelist
            && scope != (int)HistoryManager.HistoryScope.MultiCue)
            return;
        if (!Visible || _connectionCardContainer == null || !_connectionCardContainer.Visible)
            return;

        // Re-resolve focused cue (component list may have been replaced).
        int focusId = _globalData?.FocusedCue ?? -1;
        _focusedCue = focusId >= 0 ? CueList.FetchCueFromId(focusId) : null;
        LoadConnections();
    }

    /// <summary>
    /// Records a cue history step unless undo/redo restore or UI sync is in progress.
    /// </summary>
    private void RecordCueHistory(string description)
    {
        if (_isSyncingUi)
            return;
        InspectorMultiEditSupport.RecordBeforeEdit(
            _globalData,
            multiHistory: false,
            _focusedCue,
            description);
    }

    /// <summary>
    /// Refresh when session MIDI outputs are added/removed in Settings.
    /// </summary>
    private void OnMidiStateChanged()
    {
        if (Visible && _connectionCardContainer != null && _connectionCardContainer.Visible)
            LoadConnections();
    }


    private void LoadConnections()
    {
        if (!Visible || !_connectionCardContainer.Visible) return;

        _isSyncingUi = true;
        try
        {
            // Clean out existing cards
            foreach (var child in _connectionCardContainer.GetChildren())
            {
                if (child == _blankConnectionCard) continue;
                child.QueueFree();
            }

            // Load options button in Blank Connection Card (OSC / cue lights / session MIDI outputs).
            _availableConnectionsButton.Clear();
            _availableConnectionsButton.Disabled = false;
            _availableConnectionsButton.AddItem(UiLocalizer.T("Select Connection"));

            int index = 1;
            var availableConnections = _globalData.GetAvailableConnections();
            foreach (var kvp in availableConnections)
            {
                var connectionType = (string)kvp.Value;
                var connectionObj = kvp.Key;

                if (connectionObj.Obj is CueLight cueLight)
                {
                    string displayText = $"{connectionType} - {cueLight.Name}";
                    _availableConnectionsButton.AddItem(displayText, index);
                    _availableConnectionsButton.SetItemMetadata(index, cueLight);
                    index++;
                }
                else if (connectionObj.Obj is CueOscConnection cueOscConnection)
                {
                    string displayText = $"{connectionType} - {cueOscConnection.Name}";
                    _availableConnectionsButton.AddItem(displayText, index);
                    _availableConnectionsButton.SetItemMetadata(index, cueOscConnection);
                    index++;
                }
                else
                {
                    GD.Print($"ConnectionInspector:LoadConnections - Unsupported connection type: {connectionObj.VariantType}");
                }
            }

            // Session MIDI outputs (string metadata "midi:DeviceName").
            if (_midiManager != null)
            {
                foreach (string outName in _midiManager.SessionOutputNames)
                {
                    if (string.IsNullOrEmpty(outName)) continue;
                    _availableConnectionsButton.AddItem($"MIDI Output - {outName}", index);
                    _availableConnectionsButton.SetItemMetadata(index, "midi:" + outName);
                    index++;
                }
            }

            if (index == 1)
            {
                // Only the placeholder was added.
                _availableConnectionsButton.Clear();
                _availableConnectionsButton.AddItem(UiLocalizer.T("No available connections"));
                _availableConnectionsButton.Disabled = true;
            }
            else
            {
                _availableConnectionsButton.Select(0);
            }

            // Load existing connection cards on the focused cue.
            if (_focusedCue == null) return;
            foreach (var component in _focusedCue.Components)
            {
                if (component is CueLightComponent cueLightComp)
                    LoadCueLightComponentCard(cueLightComp);
                else if (component is OscComponent oscComp)
                    LoadOscComponentCard(oscComp);
                else if (component is MidiOutputComponent midiOut)
                    LoadMidiOutputComponentCard(midiOut);
            }
        }
        finally
        {
            _isSyncingUi = false;
        }
    }

    private void LoadOscComponentCard(OscComponent component)
    {
        var oscCard = _oscComponentCardScene.Instantiate<InspectorOscConnectionCard>();
        _connectionCardContainer.AddChild(oscCard);
        oscCard.SetComponent(component, this);
            
    }

    private void LoadMidiOutputComponentCard(MidiOutputComponent component)
    {
        if (_midiOutputCardScene == null)
        {
            GD.PrintErr("ConnectionInspector:LoadMidiOutputComponentCard - card scene missing");
            return;
        }
        var card = _midiOutputCardScene.Instantiate<InspectorMidiOutputCard>();
        _connectionCardContainer.AddChild(card);
        card.SetComponent(component, this);
    }

    public void RemoveComponent(ICueComponent component)
    {
        if (_focusedCue == null || component == null) return;
        RecordCueHistory("Remove connection component");
        _focusedCue.RemoveICueComponent(component);
        // Refresh tab content indicators (dot on Connection tab).
        _globalSignals?.EmitSignal(nameof(GlobalSignals.SyncShellInspector));
    }

    private void LoadCueLightComponentCard(CueLightComponent cueLightComp)
    {
        //GD.Print($"ConnectionInspector:LoadConnection - CUEEEEELIGHT COMPONENTTTTTTT");
        var cueLightCard = _cueLightComponentCardScene.Instantiate<PanelContainer>();
        _connectionCardContainer.AddChild(cueLightCard);
        var position = cueLightCard.GetIndex();
        if (position > 0)
        {
            _connectionCardContainer.MoveChild(cueLightCard, position - 1);
        }
        var actionOptionButton = cueLightCard.GetNode<OptionButton>("%ConnectionActionButton");
        var countInLineEdit = cueLightCard.GetNode<LineEdit>("%CountInLineEdit");
        var nameLabel = cueLightCard.GetNode<Label>("%NameLabel");
        var deleteButton = cueLightCard.GetNode<Button>("%DeleteButton");
        nameLabel.Text = cueLightComp.CueLight.Name;
        if (cueLightComp.CountInTime > 0)
        {
            countInLineEdit.Text = UiUtilities.FormatTime((double)cueLightComp.CountInTime);
        }
        else
        {
            countInLineEdit.Text = "";
            countInLineEdit.PlaceholderText = UiLocalizer.T("(Pre-Wait)");
        }
        
        
        // Populate actionOptionButton with CueLightAction enum values 
        actionOptionButton.Clear(); 
        var actions = Enum.GetValues(typeof(CueLightAction)); 
        for (int i = 0; i < actions.Length; i++) 
        { 
            actionOptionButton.AddItem(actions.GetValue(i)?.ToString(), i); 
        } 
        actionOptionButton.Selected = (int)cueLightComp.Action; 

        // Handle action selection to update component 
        actionOptionButton.ItemSelected += (long index) => 
        { 
            try 
            {
                if (_isSyncingUi || _historyManager?.IsRestoring == true) return;
                if ((int)cueLightComp.Action == (int)index) return;
                RecordCueHistory("Edit cue light action");
                cueLightComp.Action = (CueLightAction)index; 
                _globalSignals.EmitSignal(nameof(GlobalSignals.Log), $"Updated action for CueLightComponent in Cue {_focusedCue.Id} to {cueLightComp.Action}", 0); 
            } 
            catch (Exception ex) 
            { 
                _globalSignals.EmitSignal(nameof(GlobalSignals.Log), $"Failed to update CueLightComponent action: {ex.Message}", 2); 
            } 
        };
        
        // Handle countInLineEdit changes (optional, but for completeness) 
        countInLineEdit.TextSubmitted += (string newText) =>
        {
            if (_isSyncingUi || _historyManager?.IsRestoring == true) return;
            var time = UiUtilities.ParseAndFormatTime(newText, out var seconds, out bool isValid);
            if (!isValid || string.IsNullOrEmpty(time))
            {
                countInLineEdit.Text = UiUtilities.FormatTime(cueLightComp.CountInTime);
                if (countInLineEdit.HasFocus())
                    countInLineEdit.ReleaseFocus();
                return;
            }

            if (Math.Abs(cueLightComp.CountInTime - (float)seconds) >= 1e-6f)
            {
                RecordCueHistory("Edit cue light count-in");
                cueLightComp.CountInTime = (float)seconds;
            }

            countInLineEdit.Text = time;
            if (countInLineEdit.HasFocus())
                countInLineEdit.ReleaseFocus();
        };
        countInLineEdit.FocusExited += () =>
        {
            if (_isSyncingUi || _historyManager?.IsRestoring == true) return;
            var time = UiUtilities.ParseAndFormatTime(countInLineEdit.Text, out var seconds, out bool isValid);
            if (!isValid || string.IsNullOrEmpty(time))
            {
                countInLineEdit.Text = UiUtilities.FormatTime(cueLightComp.CountInTime);
                return;
            }

            if (Math.Abs(cueLightComp.CountInTime - (float)seconds) >= 1e-6f)
            {
                RecordCueHistory("Edit cue light count-in");
                cueLightComp.CountInTime = (float)seconds;
            }

            countInLineEdit.Text = time;
        };
        
        deleteButton.Icon = GetThemeIcon("DeleteBin", "AtlasIcons"); 
        deleteButton.Pressed += () => 
        { 
            try 
            {
                if (_focusedCue == null || _isSyncingUi || _historyManager?.IsRestoring == true) return;
                RecordCueHistory("Remove cue light component");
                _focusedCue.Components.Remove(cueLightComp); 
                _globalSignals.EmitSignal(nameof(GlobalSignals.Log), $"Removed CueLightComponent from Cue {_focusedCue.Id}", 0); 
                LoadConnections();
                _globalSignals.EmitSignal(nameof(GlobalSignals.SyncShellInspector));
            } 
            catch (Exception ex) 
            { 
                _globalSignals.EmitSignal(nameof(GlobalSignals.Log), $"Failed to remove CueLightComponent: {ex.Message}", 2); 
            } 
        };
    }

    private void OnConnectionSelected(long selectedIndex)
    {
        if (selectedIndex < 0 || _availableConnectionsButton == null) return;
        if (_isSyncingUi || _historyManager?.IsRestoring == true) return;
        if (_focusedCue == null) return;

        var selectedMetadata = _availableConnectionsButton.GetItemMetadata((int)selectedIndex);

        // MIDI outputs use string metadata "midi:DeviceName".
        if (selectedMetadata.VariantType == Variant.Type.String)
        {
            string meta = selectedMetadata.AsString();
            if (meta != null && meta.StartsWith("midi:", StringComparison.Ordinal))
            {
                string deviceName = meta.Substring("midi:".Length);
                if (!string.IsNullOrEmpty(deviceName))
                {
                    RecordCueHistory("Add MIDI output component");
                    var midiComp = new MidiOutputComponent
                    {
                        OutputDeviceName = deviceName,
                        MessageType = MidiTriggerMessageType.NoteOn,
                        Channel = 1,
                        Data1 = 60,
                        Data2 = 100,
                        NoteDurationSeconds = 0.5
                    };
                    _focusedCue.AddICueComponent(midiComp);
                    _globalSignals.EmitSignal(nameof(GlobalSignals.Log),
                        $"Selected connection: MIDI Output - {deviceName}", 0);
                    LoadConnections();
                    _globalSignals.EmitSignal(nameof(GlobalSignals.SyncShellInspector));
                }
                return;
            }
        }

        var selectedObj = selectedMetadata.Obj;
        if (selectedObj is CueLight selectedCueLight)
        {
            _globalSignals.EmitSignal(nameof(GlobalSignals.Log), $"Selected connection: Cue Light - {selectedCueLight.Name}", 0);
            RecordCueHistory("Add cue light component");
            var cueLightComponent = new CueLightComponent { CueLight = selectedCueLight, CueLightId = selectedCueLight.Id };
            _focusedCue.AddICueComponent(cueLightComponent);
            LoadConnections();
            _globalSignals.EmitSignal(nameof(GlobalSignals.SyncShellInspector));
        }
        else if (selectedObj is CueOscConnection selectedOscConnection)
        {
            _globalSignals.EmitSignal(nameof(GlobalSignals.Log), $"Selected connection: OSC Connection - {selectedOscConnection.Name}", 0);
            RecordCueHistory("Add OSC component");
            var oscComponent = new OscComponent { OscConnection = selectedOscConnection, OscConnectionId = selectedOscConnection.Id };
            _focusedCue.AddICueComponent(oscComponent);
            LoadConnections();
            _globalSignals.EmitSignal(nameof(GlobalSignals.SyncShellInspector));
        }
        else
        {
            _globalSignals.EmitSignal(nameof(GlobalSignals.Log), "Failed to retrieve selected connection object.", 2);
        }
    }

    private void ShellSelected(int cueId)
    {
        _focusedCue = CueList.FetchCueFromId(cueId);

        if (_focusedCue == null)
        {
            GD.Print($"ConnectionInspector:ShellSelected - No Shell selected");
            _infoLabel.Visible = true;
            _connectionCardContainer.Visible = false;
            return;
        }
        
        _infoLabel.Visible = false;
        _connectionCardContainer.Visible = true;
        
        LoadConnections();
        
        
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