// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System;
using System.Linq;
using Cue2.Domain.Timecode;
using Cue2.Services;
using Cue2.UI.Utilities;
using Godot;

namespace Cue2.UI.Settings;

/// <summary>
/// Editable row for a single timecode input or output (name, type, rate, source).
/// </summary>
public partial class SettingsTimecodeEndpointCard : HBoxContainer
{
    private GlobalSignals _globalSignals;
    private GlobalData _globalData;
    private HistoryManager _historyManager;
    private TimecodeManager _timecodeManager;

    private LineEdit _nameLineEdit;
    private OptionButton _kindOption;
    private OptionButton _frameRateOption;
    private OptionButton _sourceOption;
    private LineEdit _sourceLineEdit;
    private OptionButton _channelOption;
    private Label _conflictWarn;
    private CheckBox _headerCheck;
    private Label _statusLabel;
    private Button _deleteButton;

    private bool _nameEditing;
    private bool _sourceEditing;
    private bool _isSyncingUi;

    /// <summary>Bound endpoint, or <see langword="null"/> before <see cref="Bind"/>.</summary>
    public TimecodeEndpoint Endpoint { get; private set; }

    /// <summary>
    /// Raised before a user mutation so the parent panel can record settings history.
    /// </summary>
    public event Action<string> HistoryRecordRequested;

    /// <summary>
    /// Raised after a kind change that requires the parent to rebuild source lists.
    /// </summary>
    public event Action EndpointIdentityChanged;

    public override void _Ready()
    {
        _globalSignals = GetNodeOrNull<GlobalSignals>("/root/GlobalSignals");
        _globalData = GetNodeOrNull<GlobalData>("/root/GlobalData");
        _historyManager = _globalData?.HistoryManager;
        _timecodeManager = GetNodeOrNull<TimecodeManager>("/root/TimecodeManager");

        _nameLineEdit = GetNodeOrNull<LineEdit>("%NameLineEdit");
        _kindOption = GetNodeOrNull<OptionButton>("%KindOption");
        _frameRateOption = GetNodeOrNull<OptionButton>("%FrameRateOption");
        _sourceOption = GetNodeOrNull<OptionButton>("%SourceOption");
        _sourceLineEdit = GetNodeOrNull<LineEdit>("%SourceLineEdit");
        _channelOption = GetNodeOrNull<OptionButton>("%ChannelOption");
        _conflictWarn = GetNodeOrNull<Label>("%ConflictWarn");
        _headerCheck = GetNodeOrNull<CheckBox>("%HeaderCheck");
        _statusLabel = GetNodeOrNull<Label>("%StatusLabel");
        _deleteButton = GetNodeOrNull<Button>("%DeleteButton");

        if (_statusLabel != null)
            _statusLabel.SetMeta(UiLocalizer.MetaSkip, true);
        if (_conflictWarn != null)
            _conflictWarn.SetMeta(UiLocalizer.MetaSkip, true);

        if (_nameLineEdit != null)
        {
            _nameLineEdit.EditingToggled += OnNameEditingToggled;
            _nameLineEdit.TextSubmitted += OnNameTextSubmitted;
        }

        if (_kindOption != null)
            _kindOption.ItemSelected += OnKindSelected;
        if (_frameRateOption != null)
            _frameRateOption.ItemSelected += OnFrameRateSelected;
        if (_sourceOption != null)
            _sourceOption.ItemSelected += OnSourceSelected;
        if (_sourceLineEdit != null)
        {
            _sourceLineEdit.EditingToggled += OnSourceEditingToggled;
            _sourceLineEdit.TextSubmitted += OnSourceTextSubmitted;
        }
        if (_channelOption != null)
            _channelOption.ItemSelected += OnChannelSelected;
        if (_headerCheck != null)
            _headerCheck.Toggled += OnHeaderCheckToggled;

        if (_deleteButton != null)
            _deleteButton.Pressed += OnDeletePressed;

        try
        {
            if (_deleteButton != null)
                _deleteButton.Icon = GetThemeIcon("DeleteBin", "AtlasIcons");
        }
        catch
        {
            if (_deleteButton != null)
                _deleteButton.Text = "×";
        }
    }

    public override void _ExitTree()
    {
        if (_nameLineEdit != null)
        {
            _nameLineEdit.EditingToggled -= OnNameEditingToggled;
            _nameLineEdit.TextSubmitted -= OnNameTextSubmitted;
        }

        if (_kindOption != null)
            _kindOption.ItemSelected -= OnKindSelected;
        if (_frameRateOption != null)
            _frameRateOption.ItemSelected -= OnFrameRateSelected;
        if (_sourceOption != null)
            _sourceOption.ItemSelected -= OnSourceSelected;
        if (_sourceLineEdit != null)
        {
            _sourceLineEdit.EditingToggled -= OnSourceEditingToggled;
            _sourceLineEdit.TextSubmitted -= OnSourceTextSubmitted;
        }
        if (_channelOption != null)
            _channelOption.ItemSelected -= OnChannelSelected;
        if (_headerCheck != null)
            _headerCheck.Toggled -= OnHeaderCheckToggled;

        if (_deleteButton != null)
            _deleteButton.Pressed -= OnDeletePressed;

        base._ExitTree();
    }

    /// <summary>
    /// Binds this card to a live endpoint and refreshes fields.
    /// </summary>
    /// <param name="endpoint">Endpoint to edit.</param>
    public void Bind(TimecodeEndpoint endpoint)
    {
        Endpoint = endpoint;
        SyncFromEndpoint();
    }

    /// <summary>
    /// Pushes endpoint state into the form controls without recording history.
    /// </summary>
    public void SyncFromEndpoint()
    {
        if (Endpoint == null) return;

        _isSyncingUi = true;
        try
        {
            if (_nameLineEdit != null && !_nameEditing)
                _nameLineEdit.Text = Endpoint.Name ?? string.Empty;

            PopulateKindOptions();
            PopulateFrameRateOptions();
            PopulateSourceControls();
            PopulateChannelOptions();
            if (_headerCheck != null)
                _headerCheck.SetPressedNoSignal(Endpoint.ShowInHeader);
            RefreshStatus();
        }
        finally
        {
            _isSyncingUi = false;
        }
    }

    /// <summary>
    /// Updates the compact status label from the live endpoint.
    /// </summary>
    public void RefreshStatus()
    {
        if (_statusLabel == null || Endpoint == null || _timecodeManager == null)
            return;

        bool enabled = _timecodeManager.TimecodeEnabled;
        string text;
        Color color;

        if (!enabled)
        {
            text = UiLocalizer.T("Off");
            color = new Color(0.6f, 0.6f, 0.6f, 1f);
        }
        else if (Endpoint.Direction == TimecodeDirection.Output
                 && Endpoint.Kind.IsControllableGenerator())
        {
            if (Endpoint.Running)
            {
                text = UiLocalizer.T("Running");
                color = new Color(0.45f, 0.85f, 0.5f, 1f);
            }
            else
            {
                text = UiLocalizer.T("Stopped");
                color = new Color(0.85f, 0.7f, 0.35f, 1f);
            }
        }
        else if (Endpoint.Direction == TimecodeDirection.Output
                 && Endpoint.Kind == TimecodeKind.SystemClock)
        {
            text = UiLocalizer.T("Running");
            color = new Color(0.45f, 0.85f, 0.5f, 1f);
        }
        else if (string.IsNullOrEmpty(Endpoint.SourceName))
        {
            text = UiLocalizer.T("No source");
            color = new Color(0.85f, 0.55f, 0.35f, 1f);
        }
        else if (Endpoint.CurrentValue.IsLocked)
        {
            text = UiLocalizer.T("Locked");
            color = new Color(0.45f, 0.85f, 0.5f, 1f);
        }
        else
        {
            text = UiLocalizer.T("Idle");
            color = new Color(0.65f, 0.65f, 0.65f, 1f);
        }

        _statusLabel.Text = text;
        _statusLabel.AddThemeColorOverride("font_color", color);
        RefreshConflictWarn();
    }

    private void RefreshConflictWarn()
    {
        if (_conflictWarn == null)
            return;
        if (Endpoint == null || _timecodeManager == null
            || !_timecodeManager.TryGetLtcDeviceConflict(Endpoint, out string message))
        {
            _conflictWarn.Visible = false;
            _conflictWarn.TooltipText = string.Empty;
            return;
        }

        _conflictWarn.Visible = true;
        _conflictWarn.Text = "⚠";
        _conflictWarn.AddThemeColorOverride("font_color", GlobalStyles.Warning);
        _conflictWarn.TooltipText = message;
    }

    private void PopulateKindOptions()
    {
        if (_kindOption == null || Endpoint == null) return;

        _kindOption.Clear();
        foreach (TimecodeKind kind in Enum.GetValues<TimecodeKind>())
        {
            if (Endpoint.Direction == TimecodeDirection.Output && !kind.AllowsOutput())
                continue;
            if (Endpoint.Direction == TimecodeDirection.Input && !kind.AllowsInput())
                continue;
            int id = (int)kind;
            UiLocalizer.AddTranslatedItem(_kindOption, kind.GetDisplayName(), id);
        }

        SelectOptionById(_kindOption, (int)Endpoint.Kind);
    }

    private void PopulateFrameRateOptions()
    {
        if (_frameRateOption == null || Endpoint == null) return;

        _frameRateOption.Clear();
        foreach (TimecodeFrameRate rate in Enum.GetValues<TimecodeFrameRate>())
            UiLocalizer.AddTranslatedItem(_frameRateOption, rate.GetDisplayName(), (int)rate);

        SelectOptionById(_frameRateOption, (int)Endpoint.FrameRate);
    }

    private void PopulateSourceControls()
    {
        if (Endpoint == null) return;

        bool generator = Endpoint.Kind.IsVirtualGenerator();
        bool freeText = Endpoint.Kind.UsesFreeTextSource()
                        && Endpoint.Direction == TimecodeDirection.Input;

        if (_sourceLineEdit != null)
        {
            _sourceLineEdit.Visible = freeText;
            if (freeText && !_sourceEditing)
                _sourceLineEdit.Text = Endpoint.SourceName ?? string.Empty;
        }

        if (_sourceOption == null)
            return;

        _sourceOption.Visible = !freeText;
        _sourceOption.Clear();

        if (generator)
        {
            UiLocalizer.AddTranslatedItem(_sourceOption, "—");
            _sourceOption.SetItemDisabled(0, true);
            _sourceOption.Select(0);
            _sourceOption.Disabled = true;
            return;
        }

        _sourceOption.Disabled = false;
        var options = _timecodeManager?.ListSourceOptions(Endpoint) ?? Array.Empty<string>();
        if (options.Count == 0 && string.IsNullOrEmpty(Endpoint.SourceName))
        {
            string empty = Endpoint.Kind switch
            {
                TimecodeKind.MidiTimecode => "(No MIDI devices)",
                TimecodeKind.LinearTimecode => "(No audio devices)",
                TimecodeKind.Osc => "(No OSC connections)",
                _ => "(None found)"
            };
            _sourceOption.AddItem(UiLocalizer.T(empty));
            _sourceOption.SetItemMetadata(0, "");
            _sourceOption.SetItemDisabled(0, true);
            _sourceOption.Select(0);
            return;
        }

        int select = 0;
        for (int i = 0; i < options.Count; i++)
        {
            _sourceOption.AddItem(options[i]);
            _sourceOption.SetItemMetadata(i, options[i]);
            if (string.Equals(options[i], Endpoint.SourceName, StringComparison.OrdinalIgnoreCase))
                select = i;
        }

        if (!string.IsNullOrEmpty(Endpoint.SourceName)
            && !options.Contains(Endpoint.SourceName, StringComparer.OrdinalIgnoreCase))
        {
            int idx = _sourceOption.ItemCount;
            _sourceOption.AddItem($"{Endpoint.SourceName} (offline)");
            _sourceOption.SetItemMetadata(idx, Endpoint.SourceName);
            select = idx;
        }

        _sourceOption.Select(select);
    }

    private void PopulateChannelOptions()
    {
        if (_channelOption == null || Endpoint == null) return;

        bool show = Endpoint.Kind.UsesAudioDeviceChannel();
        _channelOption.Visible = show;
        if (!show)
            return;

        _channelOption.Clear();
        int count = _timecodeManager?.GetSourceChannelCount(Endpoint) ?? 0;
        if (count <= 0)
            count = Math.Max(1, Endpoint.SourceChannel + 1);

        int select = Math.Clamp(Endpoint.SourceChannel, 0, count - 1);
        for (int i = 0; i < count; i++)
        {
            _channelOption.AddItem((i + 1).ToString(), i);
            if (i == select)
                _channelOption.Select(i);
        }
    }

    private void OnNameEditingToggled(bool editing)
    {
        if (editing) _nameEditing = true;
        else
        {
            _nameEditing = false;
            _nameLineEdit?.ReleaseFocus();
            OnNameTextSubmitted(_nameLineEdit?.Text ?? string.Empty);
        }
    }

    private void OnNameTextSubmitted(string newText)
    {
        _nameEditing = false;
        _nameLineEdit?.ReleaseFocus();
        if (_isSyncingUi || Endpoint == null || _timecodeManager == null) return;
        if (_historyManager?.IsRestoring == true) return;

        string trimmed = newText?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(trimmed))
        {
            _globalSignals?.EmitSignal(nameof(GlobalSignals.Log),
                "Timecode: name cannot be empty.", (int)LogType.Warning);
            if (_nameLineEdit != null)
                _nameLineEdit.Text = Endpoint.Name;
            return;
        }

        if (Endpoint.Name == trimmed) return;
        RequestHistory($"Rename timecode to '{trimmed}'");
        _timecodeManager.SetName(Endpoint.Id, trimmed);
        if (_nameLineEdit != null)
            _nameLineEdit.Text = Endpoint.Name;
        EndpointIdentityChanged?.Invoke();
    }

    private void OnKindSelected(long index)
    {
        if (_isSyncingUi || Endpoint == null || _kindOption == null || _timecodeManager == null)
            return;
        if (_historyManager?.IsRestoring == true) return;
        if (index < 0 || index >= _kindOption.ItemCount) return;

        var next = (TimecodeKind)_kindOption.GetItemId((int)index);
        if (Endpoint.Kind == next) return;

        RequestHistory($"Set timecode type to {next.GetDisplayName()}");
        _timecodeManager.SetKind(Endpoint.Id, next);
        SyncFromEndpoint();
        EndpointIdentityChanged?.Invoke();
    }

    private void OnFrameRateSelected(long index)
    {
        if (_isSyncingUi || Endpoint == null || _frameRateOption == null || _timecodeManager == null)
            return;
        if (_historyManager?.IsRestoring == true) return;
        if (index < 0 || index >= _frameRateOption.ItemCount) return;

        var next = (TimecodeFrameRate)_frameRateOption.GetItemId((int)index);
        if (Endpoint.FrameRate == next) return;

        RequestHistory($"Set timecode rate to {next.GetDisplayName()}");
        _timecodeManager.SetFrameRate(Endpoint.Id, next);
    }

    private void OnSourceSelected(long index)
    {
        if (_isSyncingUi || Endpoint == null || _sourceOption == null || _timecodeManager == null)
            return;
        if (_historyManager?.IsRestoring == true) return;
        if (index < 0 || index >= _sourceOption.ItemCount) return;
        if (_sourceOption.IsItemDisabled((int)index)) return;

        string next = _sourceOption.GetItemMetadata((int)index).AsString();
        if (string.Equals(Endpoint.SourceName, next, StringComparison.Ordinal))
            return;

        RequestHistory($"Set timecode source to '{next}'");
        _timecodeManager.SetSourceName(Endpoint.Id, next);
        PopulateChannelOptions();
        RefreshStatus();
    }

    private void OnChannelSelected(long index)
    {
        if (_isSyncingUi || Endpoint == null || _channelOption == null || _timecodeManager == null)
            return;
        if (_historyManager?.IsRestoring == true) return;
        if (index < 0 || index >= _channelOption.ItemCount) return;

        int channel = _channelOption.GetItemId((int)index);
        if (Endpoint.SourceChannel == channel) return;

        RequestHistory($"Set timecode channel to {channel + 1}");
        _timecodeManager.SetSourceChannel(Endpoint.Id, channel);
        RefreshStatus();
    }

    private void OnHeaderCheckToggled(bool pressed)
    {
        if (_isSyncingUi || Endpoint == null || _timecodeManager == null) return;
        if (_historyManager?.IsRestoring == true) return;
        if (Endpoint.ShowInHeader == pressed) return;

        RequestHistory(pressed ? "Show timecode in GO header" : "Hide timecode from GO header");
        _timecodeManager.SetShowInHeader(Endpoint.Id, pressed);
    }

    private void OnSourceEditingToggled(bool editing)
    {
        if (editing) _sourceEditing = true;
        else
        {
            _sourceEditing = false;
            _sourceLineEdit?.ReleaseFocus();
            OnSourceTextSubmitted(_sourceLineEdit?.Text ?? string.Empty);
        }
    }

    private void OnSourceTextSubmitted(string newText)
    {
        _sourceEditing = false;
        _sourceLineEdit?.ReleaseFocus();
        if (_isSyncingUi || Endpoint == null || _timecodeManager == null) return;
        if (_historyManager?.IsRestoring == true) return;

        string trimmed = newText?.Trim() ?? string.Empty;
        if (string.Equals(Endpoint.SourceName, trimmed, StringComparison.Ordinal))
            return;

        RequestHistory($"Set timecode source to '{trimmed}'");
        _timecodeManager.SetSourceName(Endpoint.Id, trimmed);
        RefreshStatus();
    }

    private void OnDeletePressed()
    {
        if (Endpoint == null || _timecodeManager == null) return;
        if (_historyManager?.IsRestoring == true) return;

        RequestHistory($"Remove timecode '{Endpoint.Name}'");
        _timecodeManager.RemoveEndpoint(Endpoint.Id);
    }

    private void RequestHistory(string description)
    {
        if (_historyManager?.IsRestoring == true) return;
        HistoryRecordRequested?.Invoke(description);
    }

    private static void SelectOptionById(OptionButton button, int id)
    {
        if (button == null) return;
        for (int i = 0; i < button.ItemCount; i++)
        {
            if (button.GetItemId(i) == id)
            {
                button.Select(i);
                return;
            }
        }

        if (button.ItemCount > 0)
            button.Select(0);
    }
}
