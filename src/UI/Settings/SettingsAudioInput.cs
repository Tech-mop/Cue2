// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System.Collections.Generic;
using System.Linq;
using Cue2.UI.Utilities;
using Godot;

namespace Cue2.UI.Settings;

/// <summary>
/// Settings → Audio → Audio Input. Named input patches, each a collapsible list of device channels.
/// </summary>
/// <remarks>
/// Meters run only while this page is visible. Channel meters are before trim. The header meter is after channel trim and the patch submaster.
/// </remarks>
public partial class SettingsAudioInput : ScrollContainer
{
    /// <summary>Stable Settings tree key (English, persisted in user data).</summary>
    public const string MenuKey = "Audio Input";

    private GlobalData _globalData;
    private GlobalSignals _globalSignals;
    private AudioDevices _audioDevices;
    private HistoryManager _history;
    private Button _newPatchButton;
    private Label _emptyLabel;
    private VBoxContainer _patchesList;
    private readonly List<AudioInputPatchCard> _cards = new();
    private bool _rebuilding;

    /// <inheritdoc />
    public override void _Ready()
    {
        _globalData = GetNodeOrNull<GlobalData>("/root/GlobalData");
        _globalSignals = GetNodeOrNull<GlobalSignals>("/root/GlobalSignals");
        _audioDevices = GetNodeOrNull<AudioDevices>("/root/AudioDevices");
        _history = _globalData?.HistoryManager;

        _newPatchButton = GetNodeOrNull<Button>("%NewPatchButton");
        _emptyLabel = GetNodeOrNull<Label>("%EmptyLabel");
        _patchesList = GetNodeOrNull<VBoxContainer>("%PatchesList");
        if (_patchesList != null)
            _patchesList.AddThemeConstantOverride("separation", 8);

        if (_newPatchButton != null)
            _newPatchButton.Pressed += OnNewPatchPressed;

        if (_history != null)
            _history.HistoryRestored += OnHistoryRestored;
        if (_globalSignals != null)
        {
            _globalSignals.LocaleChanged += OnLocaleChanged;
            _globalSignals.NewSession += OnNewSession;
            _globalSignals.AudioDevicesChanged += OnAudioDevicesChanged;
        }

        UiLocalizer.LocalizeTree(this);
        VisibilityChanged += OnVisibilityChanged;
        SetProcess(false);
        if (IsVisibleInTree())
            OnVisibilityChanged();
    }

    /// <inheritdoc />
    public override void _ExitTree()
    {
        VisibilityChanged -= OnVisibilityChanged;
        if (_newPatchButton != null)
            _newPatchButton.Pressed -= OnNewPatchPressed;
        if (_history != null)
            _history.HistoryRestored -= OnHistoryRestored;
        if (_globalSignals != null)
        {
            _globalSignals.LocaleChanged -= OnLocaleChanged;
            _globalSignals.NewSession -= OnNewSession;
            _globalSignals.AudioDevicesChanged -= OnAudioDevicesChanged;
        }

        _audioDevices?.StopInputMonitors();
        base._ExitTree();
    }

    /// <inheritdoc />
    public override void _Process(double delta)
    {
        if (!IsVisibleInTree())
            return;

        if (CardsStale())
        {
            Rebuild();
            SyncMonitors();
            return;
        }

        foreach (var card in _cards)
        {
            if (card != null && GodotObject.IsInstanceValid(card))
                card.TickMeters();
        }
    }

    private void OnVisibilityChanged()
    {
        bool show = IsVisibleInTree();
        SetProcess(show);
        if (!show)
        {
            _audioDevices?.StopInputMonitors();
            return;
        }

        if (CardsStale())
            Rebuild();
        else
            RefreshDeviceLists();
        SyncMonitors();
    }

    private void OnHistoryRestored(int scope)
    {
        if (!GodotObject.IsInstanceValid(this))
            return;
        if (scope != (int)HistoryManager.HistoryScope.Settings)
            return;

        if (IsVisibleInTree())
        {
            Rebuild();
            SyncMonitors();
        }
        else
        {
            DropCards();
            UpdateEmptyLabel();
            _audioDevices?.StopInputMonitors();
        }
    }

    private void OnNewSession()
    {
        if (!GodotObject.IsInstanceValid(this))
            return;
        OnHistoryRestored((int)HistoryManager.HistoryScope.Settings);
    }

    private void OnAudioDevicesChanged()
    {
        if (!GodotObject.IsInstanceValid(this) || !IsVisibleInTree())
            return;
        RefreshDeviceLists();
        SyncMonitors();
    }

    private void OnLocaleChanged(string localeCode)
    {
        if (!GodotObject.IsInstanceValid(this))
            return;
        UiLocalizer.LocalizeTree(this);
        foreach (var card in _cards)
        {
            if (card != null && GodotObject.IsInstanceValid(card))
                card.Relocalize();
        }
    }

    private void OnNewPatchPressed()
    {
        if (_globalData?.Settings == null || _history?.IsRestoring == true)
            return;

        _history?.RecordSettingsChange("Create input patch", null, AudioInputPatch.HistoryKey);
        var patch = _globalData.Settings.CreateAudioInputPatch();
        var card = AddCard(patch);
        UpdateEmptyLabel();
        SyncMonitors();
        card?.FocusName();
    }

    private void OnCardStructureChanged()
    {
        SyncMonitors();
    }

    private void OnCardDeleted(AudioInputPatchCard card)
    {
        ForgetCard(card);
        if (card != null && GodotObject.IsInstanceValid(card))
            card.QueueFree();
        UpdateEmptyLabel();
        SyncMonitors();
    }

    private void Rebuild()
    {
        if (_rebuilding || _patchesList == null || _globalData?.Settings == null)
            return;

        _rebuilding = true;
        try
        {
            DropCards();
            foreach (var patch in _globalData.Settings.GetAudioInputPatches().Values
                         .Where(p => p != null && GodotObject.IsInstanceValid(p))
                         .OrderBy(p => p.Id))
            {
                AddCard(patch);
            }

            UpdateEmptyLabel();
        }
        finally
        {
            _rebuilding = false;
        }
    }

    private AudioInputPatchCard AddCard(AudioInputPatch patch)
    {
        if (patch == null || _patchesList == null)
            return null;

        var card = new AudioInputPatchCard();
        _patchesList.AddChild(card);
        card.Bind(_globalData, _globalSignals, _audioDevices, patch);
        card.StructureChanged += OnCardStructureChanged;
        card.Deleted += OnCardDeleted;
        _cards.Add(card);
        return card;
    }

    private void DropCards()
    {
        foreach (var card in _cards.ToArray())
        {
            ForgetCard(card);
            if (card != null && GodotObject.IsInstanceValid(card))
                card.QueueFree();
        }

        _cards.Clear();
    }

    private void ForgetCard(AudioInputPatchCard card)
    {
        if (card == null)
            return;
        card.StructureChanged -= OnCardStructureChanged;
        card.Deleted -= OnCardDeleted;
        _cards.Remove(card);
    }

    private void RefreshDeviceLists()
    {
        foreach (var card in _cards)
        {
            if (card != null && GodotObject.IsInstanceValid(card))
                card.RefreshDevices();
        }
    }

    private void UpdateEmptyLabel()
    {
        if (_emptyLabel != null)
            _emptyLabel.Visible = _cards.Count == 0;
    }

    private bool CardsStale()
    {
        var patches = _globalData?.Settings?.GetAudioInputPatches();
        if (patches == null)
            return _cards.Count > 0;

        int live = 0;
        foreach (var patch in patches.Values)
        {
            if (patch != null && GodotObject.IsInstanceValid(patch))
                live++;
        }

        if (live != _cards.Count)
            return true;

        foreach (var card in _cards)
        {
            if (card == null || !GodotObject.IsInstanceValid(card))
                return true;
            if (!patches.TryGetValue(card.PatchId, out var patch) || patch == null || !GodotObject.IsInstanceValid(patch))
                return true;
            if (card.ChannelRowCount != patch.Channels.Count)
                return true;
        }

        return false;
    }

    private void SyncMonitors()
    {
        if (_audioDevices == null)
            return;
        if (!IsVisibleInTree())
        {
            _audioDevices.StopInputMonitors();
            return;
        }

        var names = new List<string>();
        var patches = _globalData?.Settings?.GetAudioInputPatches();
        if (patches != null)
        {
            foreach (var patch in patches.Values)
            {
                if (patch == null || !GodotObject.IsInstanceValid(patch))
                    continue;
                foreach (var channel in patch.Channels)
                {
                    if (!string.IsNullOrWhiteSpace(channel.DeviceName))
                        names.Add(channel.DeviceName);
                }
            }
        }

        _audioDevices.SetInputMonitorDevices(names);
    }
}
