// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using Godot;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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
using Cue2.Media.Audio;
using Cue2.UI.Utilities;

namespace Cue2.UI.Inspectors;


/// <summary>
/// Inspector UI for managing audio components in cues. Handles file selection, playback settings,
/// and output patching. Supports multi-edit when Settings multi-edit is on and multiple cues are selected.
/// </summary>
/// <remarks>
/// Multi-edit targets are selected cues that have an audio component. Uniform values are shown;
/// mixed values are blank. Waveform and routing matrix reflect the primary (focused) target;
/// scalar edits (volume, pan, loop, times, fades, play count, output, file) apply to all targets.
/// History uses a cuelist snapshot when two or more targets change.
/// </remarks>
public partial class AudioInspector : Control
{

    private GlobalData _globalData;
    private GlobalSignals _globalSignals;
    private AudioDevices _audioDevices;
    
    private Cue _focusedCue;
    private AudioComponent _focusedAudioComponent;
    private MediaEngine _mediaEngine;

    /// <summary>True when multi-edit setting is on and more than one cue is selected.</summary>
    private bool _isMultiEdit;

    /// <summary>Selected cues that currently have an audio component.</summary>
    private List<(Cue Cue, AudioComponent Component)> _audioTargets = new();

    /// <summary>
    /// True while pushing model → UI (multi sync / restore) so handlers do not re-record.
    /// </summary>
    private bool _isSyncingUi;

    /// <summary>
    /// Bumped on every <see cref="ShellSelected"/> so overlapping async work from rapid multi-select
    /// abandons after awaits.
    /// </summary>
    private int _shellSelectGeneration;

    /// <summary>Cancels in-flight waveform generation when focus/file changes.</summary>
    private CancellationTokenSource _waveformCts;
    
    // Ui Nodes
    private Label _infoLabel;
    private HBoxContainer _selectFileContainer;
    private VBoxContainer _inspectorContent;
    private Button _buttonSelectFile;
    private LineEdit _fileUrl;
    private Label _fileMetadataLabel;
    private const string FileUrlPlaceholderKey = "URL (Can drag and drop files here)";
    private Button _routingCollapseButton;
    private VBoxContainer _routingAccordian;
    private Button _waveformCollapseButton;
    private VBoxContainer _waveformAccordian;
    
    private LineEdit _startTimeInput;
    private LineEdit _endTimeInput;
    private LineEdit _durationValue;
    private LineEdit _fileDurationValue;
    private CheckBox _loopInput;
    private LineEdit _playCountInput;
    private LineEdit _volumeInput;
    private LineEdit _fadeInInput;
    private LineEdit _fadeOutInput;
    private Label _panLabel;
    private HSlider _panSlider;
    private LineEdit _panInput;
    private OptionButton _outputOptionButton;
    private bool _isUpdatingPanUi;
    
    // Routing matrix
    private GridContainer _routingMatrixGrid;
    private VBoxContainer _routingContainer;
    /// <summary>Left-column input labels in the routing matrix (updated when pan changes).</summary>
    private readonly List<Label> _routingInputLabels = new List<Label>();
    /// <summary>Volume LineEdits in row-major order for in-place refresh (P2-08).</summary>
    private readonly List<LineEdit> _routingVolumeEdits = new List<LineEdit>();
    /// <summary>Last built matrix structure (inputs/outputs/labels); skip full rebuild when equal.</summary>
    private string _routingMatrixStructureKey;
    private int _routingMatrixBuildGeneration;
    
    // Waveform
    private PanelContainer _waveformPanel;
    private WaveformDisplay _waveformDisplay;
    private WaveformPeaks _cachedPeaks;
    private byte[] _cachedPeaksSource;
    private Button _startDragHandle;
    private Button _endDragHandle;
    private HSlider _zoomSlider;
    private HScrollBar _waveformScroll;
    private bool _isDraggingStart;
    private bool _isDraggingEnd;
    private float _viewStartNorm;
    private float _viewSpanNorm = 1f;
    
    private FileDialog _fileDialog;
    
    public override void _Ready()
    {
        _globalData = GetNode<GlobalData>("/root/GlobalData");
        _globalSignals = GetNode<GlobalSignals>("/root/GlobalSignals");
        _mediaEngine = GetNode<MediaEngine>("/root/MediaEngine");
        _audioDevices = GetNode<AudioDevices>("/root/AudioDevices");
        
		
        _globalSignals.ShellFocused += ShellSelected;
        // Media backup rewrites paths while a cue stays selected — refresh URL without re-select
        _globalSignals.SyncShellInspector += RefreshMediaPathDisplay;
        _globalSignals.CueMediaHealthChanged += OnCueMediaHealthChanged;
        
        
        // Ui Node setup
        _infoLabel = GetNode<Label>("%InfoLabel");
        _selectFileContainer = GetNode<HBoxContainer>("%SelectFileContainer");
        _inspectorContent = GetNode<VBoxContainer>("%InspectorContent");
        _buttonSelectFile = GetNode<Button>("%ButtonSelectFile");
        _fileUrl = GetNode<LineEdit>("%FileURL");
        _fileUrlMissingStyle = InspectorMediaUrlStyle.CreateMissingStyle();
        _fileMetadataLabel = GetNodeOrNull<Label>("%FileMetadataLabel");
        _deleteAudioComponentButton = GetNodeOrNull<Button>("%DeleteAudioComponentButton");
        if (_deleteAudioComponentButton != null)
        {
            _deleteAudioComponentButton.Pressed += OnDeleteAudioComponentPressed;
            _deleteAudioComponentButton.AddThemeColorOverride("font_color", GlobalStyles.Danger);
            try
            {
                _deleteAudioComponentButton.Icon = GetThemeIcon("DeleteBin", "AtlasIcons");
                _deleteAudioComponentButton.ExpandIcon = true;
            }
            catch { /* optional */ }
            _deleteAudioComponentButton.Visible = false;
        }
        
        _routingCollapseButton = GetNode<Button>("%RoutingCollapseButton");
        _routingCollapseButton.Icon = GetThemeIcon("Right", "AtlasIcons");
        _routingAccordian = GetNode<VBoxContainer>("%RoutingAccordian");
        _routingContainer = GetNode<VBoxContainer>("%RoutingContainer");
        _routingMatrixGrid = GetNode<GridContainer>("%RoutingMatrixGrid");
        
        _waveformCollapseButton = GetNode<Button>("%WaveformCollapseButton");
        _waveformCollapseButton.Icon = GetThemeIcon("Right", "AtlasIcons");
        _waveformAccordian = GetNode<VBoxContainer>("%WaveformAccordian");
        
        _startTimeInput = GetNode<LineEdit>("%StartTimeInput");
        _endTimeInput = GetNode<LineEdit>("%EndTimeInput");
        _durationValue = GetNode<LineEdit>("%DurationValue");
        _fileDurationValue = GetNode<LineEdit>("%FileDurationValue");
        _loopInput = GetNode<CheckBox>("%LoopInput");
        _playCountInput = GetNode<LineEdit>("%PlayCountInput");
        _volumeInput = GetNode<LineEdit>("%VolumeInput");
        _fadeInInput = GetNodeOrNull<LineEdit>("%FadeInInput");
        _fadeOutInput = GetNodeOrNull<LineEdit>("%FadeOutInput");
        _panLabel = GetNodeOrNull<Label>("%PanLabel");
        _panSlider = GetNodeOrNull<HSlider>("%PanSlider");
        _panInput = GetNodeOrNull<LineEdit>("%PanInput");
        _outputOptionButton = GetNode<OptionButton>("%OutputOptionButton");
        
        // Waveform UI setup — peak bars + zoom/scroll
        _waveformPanel = GetNode<PanelContainer>("%WaveformPanel");
        _waveformDisplay = new WaveformDisplay();
        _waveformPanel.AddChild(_waveformDisplay);
        _waveformPanel.MoveChild(_waveformDisplay, 0); // behind handles
        _waveformPanel.Resized += () => { if (_waveformAccordian.Visible) _ = DrawWaveform(); };
        _waveformPanel.GuiInput += OnWaveformPanelGuiInput;

        _startDragHandle = GetNode<Button>("%StartDragHandle");
        _endDragHandle = GetNode<Button>("%EndDragHandle");
        StyleWaveformHandles();
        _startDragHandle.GuiInput += OnStartHandleInput;
        _endDragHandle.GuiInput += OnEndHandleInput;

        _zoomSlider = GetNodeOrNull<HSlider>("%ZoomSlider");
        if (_zoomSlider != null)
        {
            _zoomSlider.MinValue = 1;
            _zoomSlider.MaxValue = 20;
            _zoomSlider.Step = 0.1;
            _zoomSlider.Value = 1;
            _zoomSlider.TooltipText = UiLocalizer.T("Zoom waveform (1× = full file)");
            _zoomSlider.ValueChanged += OnZoomChanged;
        }

        // Scroll bar under zoom (created in code so both inspectors get it)
        _waveformScroll = new HScrollBar
        {
            Name = "WaveformScroll",
            CustomMinimumSize = new Vector2(0, 14),
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            Visible = false,
            TooltipText = UiLocalizer.T("Scroll zoomed waveform")
        };
        _waveformAccordian.AddChild(_waveformScroll);
        _waveformScroll.ValueChanged += OnWaveformScrollChanged; 
        
        
        
        _startTimeInput.TextSubmitted += newText => TimeFieldSubmitted(newText, _startTimeInput);
        _endTimeInput.TextSubmitted += newText => TimeFieldSubmitted(newText, _endTimeInput);
        // Commit on blur as well (Enter is not the only way users finish an edit).
        _startTimeInput.FocusExited += () => TimeFieldSubmitted(_startTimeInput.Text, _startTimeInput);
        _endTimeInput.FocusExited += () => TimeFieldSubmitted(_endTimeInput.Text, _endTimeInput);
        _volumeInput.TextSubmitted += newText => VolumeInputSubmitted(newText, _volumeInput);
        _volumeInput.FocusExited += () => VolumeInputSubmitted(_volumeInput.Text, _volumeInput);
        LineEditDbDragSlider.EnableVolume(_volumeInput);
        if (_panSlider != null)
        {
            _panSlider.MinValue = -100;
            _panSlider.MaxValue = 100;
            _panSlider.Step = 1;
            _panSlider.ValueChanged += OnPanSliderChanged;
            _panSlider.DragEnded += OnPanSliderDragEnded;
        }
        if (_panInput != null)
        {
            _panInput.TextSubmitted += newText => PanInputSubmitted(newText);
            _panInput.FocusExited += () => PanInputSubmitted(_panInput.Text);
        }
        _loopInput.Toggled += OnLoopToggled;
        _playCountInput.TextSubmitted += OnPlayCountSubmitted;
        _playCountInput.FocusExited += () => OnPlayCountSubmitted(_playCountInput.Text);
        if (_fadeInInput != null)
        {
            _fadeInInput.TextSubmitted += text => OnFadeSubmitted(text, isIn: true);
            _fadeInInput.FocusExited += () => OnFadeSubmitted(_fadeInInput.Text, isIn: true);
        }
        if (_fadeOutInput != null)
        {
            _fadeOutInput.TextSubmitted += text => OnFadeSubmitted(text, isIn: false);
            _fadeOutInput.FocusExited += () => OnFadeSubmitted(_fadeOutInput.Text, isIn: false);
        }
        _outputOptionButton.ItemSelected += OutputOptionSelected;

        // Undo/redo and other model restores push this signal; rebind component + refresh UI.
        _globalSignals.SyncShellInspector += OnSyncFromHistory;
        
        UiUtilities.FormatLabelsColours(this, GlobalStyles.SoftFontColor);
        
        GetNode<Label>("%InfoLabel").AddThemeColorOverride("font_color", GlobalStyles.DisabledColor);
        
        // Ensure content is hidden at start up
        _inspectorContent.Visible = false;
        _selectFileContainer.Visible = false;
        _routingAccordian.Visible = false;
        _routingContainer.Visible = false;
        _waveformAccordian.Visible = false;
        
        _routingCollapseButton.Pressed += () => ToggleAccordian(_routingAccordian, _routingCollapseButton);
        _waveformCollapseButton.Pressed += () => ToggleAccordian(_waveformAccordian, _waveformCollapseButton);
        _buttonSelectFile.Pressed += OpenFileDialog;
        
    
        UiLocalizer.LocalizeTree(this);
        if (_globalSignals != null)
            _globalSignals.LocaleChanged += OnLocaleChanged;

        if (_globalData != null && _globalData.FocusedCue >= 0)
            ShellSelected(_globalData.FocusedCue);
        else
            ShowNoSelection();
    }

    /// <summary>
    /// Restores the File URL placeholder used when no path is set (matches video inspector).
    /// </summary>
    private void RestoreFileUrlPlaceholder()
    {
        if (_fileUrl == null)
            return;
        _fileUrl.PlaceholderText = UiLocalizer.T(FileUrlPlaceholderKey);
    }

    /// <summary>
    /// Hides the compact audio file-details line under the File URL.
    /// </summary>
    private void ClearFileMetadataLabel()
    {
        if (_fileMetadataLabel == null)
            return;
        _fileMetadataLabel.Text = "";
        _fileMetadataLabel.TooltipText = "";
        _fileMetadataLabel.Visible = false;
    }

    /// <summary>
    /// Fills the compact file-details label from the focused audio component metadata.
    /// </summary>
    private void UpdateFileMetadataLabel()
    {
        if (_fileMetadataLabel == null)
            return;

        var meta = _focusedAudioComponent?.Metadata;
        if (meta == null || (meta.Channels <= 0 && string.IsNullOrEmpty(meta.Codec)))
        {
            ClearFileMetadataLabel();
            return;
        }

        var parts = new List<string>();
        if (meta.Channels > 0)
            parts.Add($"{meta.Channels}ch");
        if (meta.SampleRate > 0)
            parts.Add(FormatAudioSampleRate(meta.SampleRate));
        if (meta.BitDepth > 0)
            parts.Add($"{meta.BitDepth}-bit");
        if (!string.IsNullOrWhiteSpace(meta.Codec))
            parts.Add(meta.Codec);

        string compact = string.Join(" · ", parts);
        if (string.IsNullOrWhiteSpace(compact))
        {
            ClearFileMetadataLabel();
            return;
        }

        string tooltip =
            UiLocalizer.Tf("Duration: {0}", UiUtilities.FormatTime(meta.Duration)) + "\n" +
            UiLocalizer.Tf("Channels: {0}", meta.Channels) + "\n" +
            UiLocalizer.Tf("Sample Rate: {0} Hz", meta.SampleRate) + "\n" +
            UiLocalizer.Tf("Bit Depth: {0}", meta.BitDepth) + "\n" +
            UiLocalizer.Tf("Codec: {0}", meta.Codec) + "\n" +
            UiLocalizer.Tf("Format: {0}", meta.Format);

        _fileMetadataLabel.Text = compact;
        _fileMetadataLabel.TooltipText = tooltip;
        _fileMetadataLabel.Visible = true;
        if (_fileUrl != null && !_fileUrlMissing)
            _fileUrl.TooltipText = tooltip;
    }

    /// <summary>
    /// Formats a sample rate for the compact file-details line (e.g. 48000 → 48 kHz).
    /// </summary>
    /// <param name="hz">Sample rate in Hertz.</param>
    /// <returns>Short display string.</returns>
    private static string FormatAudioSampleRate(int hz)
    {
        if (hz <= 0)
            return string.Empty;
        if (hz % 1000 == 0)
            return $"{hz / 1000} kHz";
        if (hz % 100 == 0)
            return $"{hz / 1000.0:0.0} kHz";
        return $"{hz} Hz";
    }

    /// <inheritdoc />
    public override void _ExitTree()
    {
        if (_globalSignals != null)
            _globalSignals.LocaleChanged -= OnLocaleChanged;

        // Invalidate in-flight ShellSelected / waveform work so callbacks no-op after free.
        _shellSelectGeneration++;
        CancelWaveformWork();
        ClearFileDialog();

        if (_globalSignals != null)
        {
            _globalSignals.ShellFocused -= ShellSelected;
            _globalSignals.SyncShellInspector -= RefreshMediaPathDisplay;
            _globalSignals.SyncShellInspector -= OnSyncFromHistory;
            _globalSignals.CueMediaHealthChanged -= OnCueMediaHealthChanged;
        }

        _focusedCue = null;
        _focusedAudioComponent = null;
        _audioTargets.Clear();

        base._ExitTree();
    }
    
    /// <summary>
    /// Handles submission of time fields (start/end). Parses input, updates component, and recalculates duration.
    /// Blank or -1 input sets time to undefined (EndTime=-1, StartTime=0).
    /// Start times are clamped to [0, file duration]. End times at or beyond file duration become full (EndTime=-1).
    /// </summary>
    /// <param name="text">The submitted text.</param>
    /// <param name="textField">The LineEdit field.</param>

    /// <summary>
    /// Re-localizes panel chrome when the UI language changes.
    /// </summary>
    /// <param name="localeCode">New locale code.</param>
    private void OnLocaleChanged(string localeCode)
    {
        if (!GodotObject.IsInstanceValid(this))
            return;
        UiLocalizer.LocalizeTree(this);
        if (_focusedCue == null)
            ShowNoSelection();
    }

}
