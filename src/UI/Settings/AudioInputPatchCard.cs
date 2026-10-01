// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using Godot;
using Cue2.UI.Utilities;

namespace Cue2.UI.Settings;

/// <summary>
/// One collapsible audio input patch: name, submaster trim, post-trim meter, and a channel list.
/// </summary>
public partial class AudioInputPatchCard : PanelContainer
{
    /// <summary>Raised when device assignments change and capture should be synced.</summary>
    public event Action StructureChanged;

    /// <summary>Raised after this patch has been deleted from settings.</summary>
    public event Action<AudioInputPatchCard> Deleted;

    private GlobalData _globalData;
    private GlobalSignals _globalSignals;
    private AudioDevices _audioDevices;
    private HistoryManager _history;
    private AudioInputPatch _patch;
    private bool _syncing;
    private bool _expanded = true;

    private Button _collapseButton;
    private LineEdit _nameEdit;
    private LineEdit _patchTrimEdit;
    private LevelMeter _postMeter;
    private Label _countLabel;
    private VBoxContainer _body;
    private readonly List<ChannelRow> _rows = new();
    private List<AudioDevices.RecordingInputDevice> _listings = new();

    /// <summary>Patch id this card edits.</summary>
    public int PatchId => _patch?.Id ?? -1;

    /// <summary>Channel rows currently shown.</summary>
    public int ChannelRowCount => _rows.Count;

    private sealed class ChannelRow
    {
        public AudioInputChannel Channel;
        public HBoxContainer Root;
        public Label Caption;
        public OptionButton DeviceOption;
        public OptionButton ChannelOption;
        public LevelMeter Meter;
        public LineEdit TrimEdit;
    }

    /// <summary>
    /// Builds the card for <paramref name="patch"/>. Call after the card is in the tree.
    /// </summary>
    /// <param name="globalData">Show data.</param>
    /// <param name="globalSignals">Log and locale signals.</param>
    /// <param name="audioDevices">Recording device list and meters.</param>
    /// <param name="patch">Patch to edit. Must stay valid for the life of the card.</param>
    public void Bind(GlobalData globalData, GlobalSignals globalSignals, AudioDevices audioDevices, AudioInputPatch patch)
    {
        _globalData = globalData;
        _globalSignals = globalSignals;
        _audioDevices = audioDevices;
        _history = globalData?.HistoryManager;
        _patch = patch;

        var style = new StyleBoxFlat
        {
            BgColor = new Color(0.11f, 0.11f, 0.11f),
            BorderColor = new Color(0.35f, 0.35f, 0.35f),
            BorderWidthLeft = 1,
            BorderWidthTop = 1,
            BorderWidthRight = 1,
            BorderWidthBottom = 1,
            ContentMarginLeft = 8,
            ContentMarginTop = 6,
            ContentMarginRight = 8,
            ContentMarginBottom = 6
        };
        AddThemeStyleboxOverride("panel", style);
        SizeFlagsHorizontal = SizeFlags.ExpandFill;

        var root = new VBoxContainer();
        root.AddThemeConstantOverride("separation", 6);
        root.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        AddChild(root);

        var header = new HBoxContainer();
        header.AddThemeConstantOverride("separation", 8);
        root.AddChild(header);

        _collapseButton = new Button
        {
            Text = "▼",
            CustomMinimumSize = new Vector2(28, 0),
            FocusMode = FocusModeEnum.None
        };
        // Arrow caption flips when collapsed; don't let localization pin one glyph.
        _collapseButton.SetMeta(UiLocalizer.MetaSkip, true);
        UiLocalizer.SetTooltip(_collapseButton, "Hide channels");
        _collapseButton.Pressed += OnCollapsePressed;
        header.AddChild(_collapseButton);

        _nameEdit = new LineEdit
        {
            Text = patch?.Name ?? string.Empty,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(120, 0)
        };
        UiLocalizer.SetPlaceholder(_nameEdit, "Patch name");
        UiLocalizer.SetTooltip(_nameEdit, "Name of this input patch");
        _nameEdit.TextSubmitted += OnNameSubmitted;
        _nameEdit.FocusExited += OnNameFocusExited;
        header.AddChild(_nameEdit);

        var trimLabel = new Label
        {
            VerticalAlignment = VerticalAlignment.Center
        };
        UiLocalizer.SetText(trimLabel, "Trim");
        header.AddChild(trimLabel);

        _patchTrimEdit = new LineEdit
        {
            Text = FormatTrim(patch?.TrimDb ?? 0f),
            CustomMinimumSize = new Vector2(84, 0),
            Alignment = HorizontalAlignment.Center
        };
        UiLocalizer.SetTooltip(_patchTrimEdit, "Submaster trim for every channel on this patch. 0 dB is unity.");
        LineEditDbDragSlider.EnableVolume(_patchTrimEdit);
        _patchTrimEdit.TextSubmitted += _ => CommitPatchTrim();
        _patchTrimEdit.FocusExited += CommitPatchTrim;
        header.AddChild(_patchTrimEdit);

        _postMeter = new LevelMeter();
        UiLocalizer.SetTooltip(_postMeter, "Peak of this patch after each channel trim and the submaster trim.");
        header.AddChild(_postMeter);

        _countLabel = new Label
        {
            VerticalAlignment = VerticalAlignment.Center
        };
        _countLabel.AddThemeColorOverride("font_color", new Color(0.65f, 0.65f, 0.65f));
        _countLabel.AddThemeFontSizeOverride("font_size", 11);
        header.AddChild(_countLabel);

        var deleteButton = new Button { FocusMode = FocusModeEnum.None };
        UiLocalizer.SetText(deleteButton, "Delete");
        UiLocalizer.SetTooltip(deleteButton, "Delete this input patch");
        deleteButton.Pressed += OnDeletePressed;
        header.AddChild(deleteButton);

        _body = new VBoxContainer();
        _body.AddThemeConstantOverride("separation", 4);
        _body.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        root.AddChild(_body);

        var addButton = new Button
        {
            FocusMode = FocusModeEnum.None,
            SizeFlagsHorizontal = SizeFlags.ShrinkBegin
        };
        UiLocalizer.SetText(addButton, "Add channel");
        UiLocalizer.SetTooltip(addButton, "Add a channel to this patch");
        addButton.Pressed += OnAddChannelPressed;
        _body.AddChild(addButton);

        ReloadListings();
        if (patch != null)
        {
            foreach (var channel in patch.Channels)
                AppendRow(channel);
        }

        UpdateCountLabel();
        Renumber();
    }

    /// <summary>Selects the name field.</summary>
    public void FocusName()
    {
        _nameEdit?.GrabFocus();
        _nameEdit?.SelectAll();
    }

    /// <summary>Pushes the latest device-channel peaks into the meters.</summary>
    public void TickMeters()
    {
        if (_audioDevices == null)
            return;

        float patchGain = _patch != null && GodotObject.IsInstanceValid(_patch)
            ? UiUtilities.DbToLinear(_patch.TrimDb)
            : 1f;
        float postPeak = 0f;
        foreach (var row in _rows)
        {
            float peak = 0f;
            if (row.Channel != null
                && !string.IsNullOrEmpty(row.Channel.DeviceName)
                && row.Channel.DeviceChannel >= 0)
            {
                peak = _audioDevices.GetInputPeak(row.Channel.DeviceName, row.Channel.DeviceChannel);
            }

            row.Meter.Level = peak;
            float post = peak * UiUtilities.DbToLinear(row.Channel?.TrimDb ?? 0f) * patchGain;
            if (post > postPeak)
                postPeak = post;
        }

        if (_postMeter != null)
            _postMeter.Level = postPeak;
    }

    /// <summary>Reloads device lists after hardware changes.</summary>
    public void RefreshDevices()
    {
        ReloadListings();
        foreach (var row in _rows)
            FillDeviceOption(row);
    }

    /// <summary>Refreshes formatted captions after a language change.</summary>
    public void Relocalize()
    {
        UpdateCountLabel();
        Renumber();
        if (_collapseButton != null)
            UiLocalizer.SetTooltip(_collapseButton, _expanded ? "Hide channels" : "Show channels");
        RefreshDevices();
    }

    private void ReloadListings()
    {
        _listings = _audioDevices?.GetRecordingDeviceListings() ?? new List<AudioDevices.RecordingInputDevice>();
    }

    private void AppendRow(AudioInputChannel channel)
    {
        var row = new ChannelRow { Channel = channel };
        var box = new HBoxContainer();
        box.AddThemeConstantOverride("separation", 8);
        row.Root = box;

        row.Caption = new Label
        {
            CustomMinimumSize = new Vector2(92, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        box.AddChild(row.Caption);

        var deviceLabel = new Label { VerticalAlignment = VerticalAlignment.Center };
        UiLocalizer.SetText(deviceLabel, "device:");
        box.AddChild(deviceLabel);

        row.DeviceOption = new OptionButton
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(160, 0)
        };
        UiLocalizer.SetTooltip(row.DeviceOption, "Recording device for this channel");
        row.DeviceOption.ItemSelected += _ => OnDeviceSelected(row);
        box.AddChild(row.DeviceOption);

        var channelLabel = new Label { VerticalAlignment = VerticalAlignment.Center };
        UiLocalizer.SetText(channelLabel, "ch:");
        box.AddChild(channelLabel);

        row.ChannelOption = new OptionButton { CustomMinimumSize = new Vector2(88, 0) };
        UiLocalizer.SetTooltip(row.ChannelOption, "Input on the selected device. Numbering starts at 1.");
        row.ChannelOption.ItemSelected += _ => OnChannelSelected(row);
        box.AddChild(row.ChannelOption);

        row.Meter = new LevelMeter();
        UiLocalizer.SetTooltip(row.Meter, "Live level of this device channel, before trim");
        box.AddChild(row.Meter);

        row.TrimEdit = new LineEdit
        {
            Text = FormatTrim(channel.TrimDb),
            CustomMinimumSize = new Vector2(84, 0),
            Alignment = HorizontalAlignment.Center
        };
        UiLocalizer.SetTooltip(row.TrimEdit, "Input trim. 0 dB is unity.");
        LineEditDbDragSlider.EnableVolume(row.TrimEdit);
        row.TrimEdit.TextSubmitted += _ => CommitTrim(row);
        row.TrimEdit.FocusExited += () => CommitTrim(row);
        box.AddChild(row.TrimEdit);

        var remove = new Button { FocusMode = FocusModeEnum.None };
        UiLocalizer.SetText(remove, "Remove");
        UiLocalizer.SetTooltip(remove, "Remove this channel");
        remove.Pressed += () => OnRemoveChannel(row);
        box.AddChild(remove);

        _rows.Add(row);
        // Keep Add channel as the last child.
        _body.AddChild(box);
        _body.MoveChild(box, Math.Max(0, _body.GetChildCount() - 2));

        _syncing = true;
        try
        {
            FillDeviceOption(row);
        }
        finally
        {
            _syncing = false;
        }
    }

    private void FillDeviceOption(ChannelRow row)
    {
        if (row?.DeviceOption == null || row.Channel == null)
            return;

        string current = row.Channel.DeviceName ?? string.Empty;
        var button = row.DeviceOption;
        button.SetBlockSignals(true);
        try
        {
            button.Clear();
            UiLocalizer.AddTranslatedItem(button, "None", 0);
            button.SetItemMetadata(0, string.Empty);

            int nextId = 1;
            bool found = string.IsNullOrEmpty(current);
            foreach (var device in _listings)
            {
                button.AddItem(device.Name, nextId);
                button.SetItemMetadata(button.ItemCount - 1, device.Name);
                if (string.Equals(device.Name, current, StringComparison.Ordinal))
                    found = true;
                nextId++;
            }

            if (!found)
            {
                button.AddItem(UiLocalizer.Tf("{0} (unavailable)", current), nextId);
                button.SetItemMetadata(button.ItemCount - 1, current);
            }

            int select = 0;
            for (int i = 0; i < button.ItemCount; i++)
            {
                if (button.GetItemMetadata(i).AsString() == current)
                {
                    select = i;
                    break;
                }
            }

            button.Select(select);
            FillChannelOption(row);
        }
        finally
        {
            button.SetBlockSignals(false);
        }
    }

    private void FillChannelOption(ChannelRow row)
    {
        var button = row.ChannelOption;
        if (button == null || row.Channel == null)
            return;

        string device = row.Channel.DeviceName ?? string.Empty;
        button.SetBlockSignals(true);
        try
        {
            button.Clear();
            if (string.IsNullOrEmpty(device))
            {
                button.AddItem("—", -1);
                button.Disabled = true;
                return;
            }

            button.Disabled = false;
            int count = ChannelCountFor(device);
            for (int i = 0; i < count; i++)
                button.AddItem((i + 1).ToString(), i);

            int selected = row.Channel.DeviceChannel;
            if (selected >= count && selected >= 0)
                button.AddItem(UiLocalizer.Tf("{0} (unavailable)", selected + 1), selected);

            int index = selected >= 0 ? button.GetItemIndex(selected) : -1;
            if (index >= 0)
                button.Select(index);
        }
        finally
        {
            button.SetBlockSignals(false);
        }
    }

    private int ChannelCountFor(string deviceName)
    {
        foreach (var device in _listings)
        {
            if (string.Equals(device.Name, deviceName, StringComparison.Ordinal))
                return device.ChannelCount;
        }

        return 0;
    }

    private void Renumber()
    {
        for (int i = 0; i < _rows.Count; i++)
        {
            var label = _rows[i].Caption;
            if (label == null)
                continue;
            label.SetMeta(UiLocalizer.MetaSkip, true);
            label.Text = UiLocalizer.Tf("Channel {0}", i + 1);
        }
    }

    private void UpdateCountLabel()
    {
        if (_countLabel == null)
            return;
        int count = _patch != null && GodotObject.IsInstanceValid(_patch) ? _patch.Channels.Count : 0;
        _countLabel.SetMeta(UiLocalizer.MetaSkip, true);
        _countLabel.Text = count == 1
            ? UiLocalizer.T("1 channel")
            : UiLocalizer.Tf("{0} channels", count);
    }

    private bool CanEdit()
    {
        return !_syncing
            && _patch != null
            && GodotObject.IsInstanceValid(_patch)
            && _history?.IsRestoring != true;
    }

    private void Record(string description)
    {
        _history?.RecordSettingsChange(description, null, AudioInputPatch.HistoryKey);
    }

    private void OnCollapsePressed()
    {
        _expanded = !_expanded;
        if (_body != null)
            _body.Visible = _expanded;
        if (_collapseButton == null)
            return;
        _collapseButton.Text = _expanded ? "▼" : "▶";
        UiLocalizer.SetTooltip(_collapseButton, _expanded ? "Hide channels" : "Show channels");
    }

    private void OnNameSubmitted(string text)
    {
        CommitName();
    }

    private void OnNameFocusExited()
    {
        CommitName();
    }

    private void CommitName()
    {
        if (!CanEdit() || _nameEdit == null)
            return;

        string next = _nameEdit.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(next))
        {
            _syncing = true;
            _nameEdit.Text = _patch.Name ?? string.Empty;
            _syncing = false;
            return;
        }

        if (next == _patch.Name)
            return;

        Record("Rename input patch");
        _patch.Name = next;
    }

    private void OnDeletePressed()
    {
        if (!CanEdit())
            return;

        Record("Delete input patch");
        int id = _patch.Id;
        _patch = null;
        _globalData?.Settings?.DeleteAudioInputPatch(id);
        Deleted?.Invoke(this);
    }

    private void OnAddChannelPressed()
    {
        if (!CanEdit())
            return;
        if (_patch.Channels.Count >= AudioInputPatch.MaxChannels)
        {
            _globalSignals?.EmitSignal(nameof(GlobalSignals.Log),
                $"Input patch '{_patch.Name}' is limited to {AudioInputPatch.MaxChannels} channels.",
                (int)LogType.Warning);
            return;
        }

        Record("Add input channel");
        var channel = _patch.AddChannel();
        if (channel == null)
            return;

        AppendRow(channel);
        Renumber();
        UpdateCountLabel();
        StructureChanged?.Invoke();
    }

    private void OnRemoveChannel(ChannelRow row)
    {
        if (!CanEdit() || row?.Channel == null)
            return;
        if (_patch.FindChannel(row.Channel.Id) == null)
            return;

        Record("Remove input channel");
        _patch.RemoveChannel(row.Channel.Id);
        _rows.Remove(row);
        row.Root?.QueueFree();
        Renumber();
        UpdateCountLabel();
        StructureChanged?.Invoke();
    }

    private void OnDeviceSelected(ChannelRow row)
    {
        if (!CanEdit() || row?.Channel == null || row.DeviceOption == null)
            return;

        int index = row.DeviceOption.Selected;
        string device = index >= 0 ? row.DeviceOption.GetItemMetadata(index).AsString() : string.Empty;
        if (device == null)
            device = string.Empty;
        if (device == (row.Channel.DeviceName ?? string.Empty))
            return;

        Record("Assign input device");
        row.Channel.DeviceName = device;
        int count = ChannelCountFor(device);
        if (string.IsNullOrEmpty(device))
            row.Channel.DeviceChannel = -1;
        else if (row.Channel.DeviceChannel < 0 || row.Channel.DeviceChannel >= count)
            row.Channel.DeviceChannel = count > 0 ? 0 : -1;

        FillChannelOption(row);
        StructureChanged?.Invoke();
    }

    private void OnChannelSelected(ChannelRow row)
    {
        if (!CanEdit() || row?.Channel == null || row.ChannelOption == null)
            return;

        int selected = row.ChannelOption.GetSelectedId();
        if (selected == row.Channel.DeviceChannel)
            return;

        Record("Assign input channel");
        row.Channel.DeviceChannel = selected;
        StructureChanged?.Invoke();
    }

    private void CommitTrim(ChannelRow row)
    {
        if (!CanEdit() || row?.Channel == null || row.TrimEdit == null)
            return;

        if (!TryParseTrim(row.TrimEdit.Text, out float db))
        {
            _globalSignals?.EmitSignal(nameof(GlobalSignals.Log),
                $"Invalid input trim: {row.TrimEdit.Text}", (int)LogType.Warning);
            _syncing = true;
            row.TrimEdit.Text = FormatTrim(row.Channel.TrimDb);
            _syncing = false;
            return;
        }

        db = Mathf.Clamp(db, AudioInputPatch.MinTrimDb, AudioInputPatch.MaxTrimDb);
        string formatted = FormatTrim(db);
        if (row.TrimEdit.Text != formatted)
        {
            _syncing = true;
            row.TrimEdit.Text = formatted;
            _syncing = false;
        }

        if (Mathf.IsEqualApprox(db, row.Channel.TrimDb))
            return;

        Record("Change input trim");
        row.Channel.TrimDb = db;
    }

    private void CommitPatchTrim()
    {
        if (!CanEdit() || _patchTrimEdit == null)
            return;

        if (!TryParseTrim(_patchTrimEdit.Text, out float db))
        {
            _globalSignals?.EmitSignal(nameof(GlobalSignals.Log),
                $"Invalid input patch trim: {_patchTrimEdit.Text}", (int)LogType.Warning);
            _syncing = true;
            _patchTrimEdit.Text = FormatTrim(_patch.TrimDb);
            _syncing = false;
            return;
        }

        db = Mathf.Clamp(db, AudioInputPatch.MinTrimDb, AudioInputPatch.MaxTrimDb);
        string formatted = FormatTrim(db);
        if (_patchTrimEdit.Text != formatted)
        {
            _syncing = true;
            _patchTrimEdit.Text = formatted;
            _syncing = false;
        }

        if (Mathf.IsEqualApprox(db, _patch.TrimDb))
            return;

        Record("Change input patch trim");
        _patch.TrimDb = db;
    }

    private static bool TryParseTrim(string text, out float db)
    {
        db = 0f;
        if (string.IsNullOrWhiteSpace(text))
            return false;
        string cleaned = text.Replace("dB", "", StringComparison.OrdinalIgnoreCase)
            .Replace("+", "", StringComparison.Ordinal)
            .Trim();
        return float.TryParse(cleaned, out db);
    }

    private static string FormatTrim(float db)
    {
        if (db > 0.049f)
            return $"+{db:0.0}dB";
        return $"{db:0.0}dB";
    }
}
