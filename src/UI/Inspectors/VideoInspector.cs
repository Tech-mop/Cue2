// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

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
using Godot;
using Cue2.UI.Preview;

namespace Cue2.UI.Inspectors;

/// <summary>
/// Inspector for video/image components. Supports multi-edit when Settings multi-edit is on
/// and multiple cues are selected (applies to cues that have a video component).
/// </summary>
public partial class VideoInspector : Control
	
{
	private GlobalData _globalData;
	private GlobalSignals _globalSignals;
	private MediaEngine _mediaEngine;
	private AudioDevices _audioDevices;
	
	private Cue _focusedCue;
	private VideoComponent _focusedVideoComponent;

	/// <summary>True when multi-edit setting is on and more than one cue is selected.</summary>
	private bool _isMultiEdit;

	/// <summary>Selected cues that currently have a video component.</summary>
	private List<(Cue Cue, VideoComponent Component)> _videoTargets = new();

	/// <summary>True while pushing model → UI so handlers do not re-record.</summary>
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
	private Button _selectFileButton;
	private LineEdit _fileUrl;
    
	private LineEdit _startTimeInput;
	private LineEdit _endTimeInput;
	private LineEdit _durationValue;
	private LineEdit _fileDurationValue;
	private Label _startTimeLabel;
	private Label _endTimeLabel;
	private Label _durationLabel;
	private Label _fileDurationLabel;
	private Control _loopPlayCountRow;
	private CheckBox _loopInput;
	private LineEdit _playCountInput;
	private LineEdit _fadeInInput;
	private LineEdit _fadeOutInput;

	private Label _fileMetadataLabel;
	private OptionButton _targetLayerOptionButton;
	private OptionButton _expandModeOptionButton;
	private OptionButton _stretchModeOptionButton;
	private LineEdit _opacityLineEdit;
	private LineEdit _scaleWidthLineEdit;
	private LineEdit _scaleHeightLineEdit;
	private LineEdit _offsetXLineEdit;
	private LineEdit _offsetYLineEdit;

	// Closed captions / subtitles → Text component link
	private HBoxContainer _subtitleRow;
	private CheckBox _useSubtitlesCheck;
	private OptionButton _subtitleTrackOption;
	private Button _addTextForCcButton;

	// Video Preview
	private Button _previewCollapseButton;
	private HBoxContainer _previewContainer;
	private VideoPreviewer _videoPreviewer;
    
	// Audio
	private Button _audioCollapseButton;
	private HBoxContainer _audioAccordian;
	private CheckButton _useAudioCheckButton;
	private Label _useAudioLabel;
	private OptionButton _outputOptionButton;
	private LineEdit _volumeInput;
	private Label _panLabel;
	private HSlider _panSlider;
	private LineEdit _panInput;
	private bool _isUpdatingPanUi;
	
	// Routing matrix
	private Button _routingCollapseButton;
	private VBoxContainer _routingAccordian;
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
	private Button _waveformCollapseButton;
	private VBoxContainer _waveformAccordian;
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
		_globalSignals= GetNode<GlobalSignals>("/root/GlobalSignals");
		_mediaEngine = GetNode<MediaEngine>("/root/MediaEngine");
		_audioDevices = GetNode<AudioDevices>("/root/AudioDevices");

		_globalSignals.ShellFocused += ShellSelected;
		// Media backup rewrites paths while a cue stays selected — refresh URL without re-select
		_globalSignals.SyncShellInspector += RefreshMediaPathDisplay;
		_globalSignals.CueMediaHealthChanged += OnCueMediaHealthChanged;
		// Layer add/remove/rename while inspector is open — refresh without failover
		_globalSignals.DisplaysChanged += OnDisplaysChangedForTargetLayers;

		AssignUiNodeParameters();
		SetupWaveformUi();
		_fileUrlMissingStyle = InspectorMediaUrlStyle.CreateMissingStyle();

		_deleteVideoComponentButton = GetNodeOrNull<Button>("%DeleteVideoComponentButton");
		if (_deleteVideoComponentButton != null)
		{
			_deleteVideoComponentButton.Pressed += OnDeleteVideoComponentPressed;
			_deleteVideoComponentButton.AddThemeColorOverride("font_color", GlobalStyles.Danger);
			try
			{
				_deleteVideoComponentButton.Icon = GetThemeIcon("DeleteBin", "AtlasIcons");
				_deleteVideoComponentButton.ExpandIcon = true;
			}
			catch { /* optional */ }
			_deleteVideoComponentButton.Visible = false;
		}

		_startTimeInput.TextSubmitted += newText => TimeFieldSubmitted(newText, _startTimeInput);
		_endTimeInput.TextSubmitted += newText => TimeFieldSubmitted(newText, _endTimeInput);
		_startTimeInput.FocusExited += () => TimeFieldSubmitted(_startTimeInput.Text, _startTimeInput);
		_endTimeInput.FocusExited += () => TimeFieldSubmitted(_endTimeInput.Text, _endTimeInput);
		_durationValue.TextSubmitted += OnImageDurationSubmitted;
		_durationValue.FocusExited += () => OnImageDurationSubmitted(_durationValue.Text);
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
		_scaleWidthLineEdit.TextSubmitted += newText => OnScaleWidthSubmitted(newText);
		_scaleHeightLineEdit.TextSubmitted += newText => OnScaleHeightSubmitted(newText);
		_offsetXLineEdit.TextSubmitted += newText => OnOffsetXSubmitted(newText);
		_offsetYLineEdit.TextSubmitted += newText => OnOffsetYSubmitted(newText);
		_scaleWidthLineEdit.FocusExited += () => OnScaleWidthSubmitted(_scaleWidthLineEdit.Text);
		_scaleHeightLineEdit.FocusExited += () => OnScaleHeightSubmitted(_scaleHeightLineEdit.Text);
		_offsetXLineEdit.FocusExited += () => OnOffsetXSubmitted(_offsetXLineEdit.Text);
		_offsetYLineEdit.FocusExited += () => OnOffsetYSubmitted(_offsetYLineEdit.Text);
		_useAudioCheckButton.Toggled += OnUseAudioToggled;
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
			_panInput.TextSubmitted += _ => PanInputSubmitted(_panInput.Text);
			_panInput.FocusExited += () => PanInputSubmitted(_panInput.Text);
		}
		_outputOptionButton.ItemSelected += OutputOptionSelected;
		_targetLayerOptionButton.ItemSelected += TargetLayerSelected;
		_expandModeOptionButton.ItemSelected += ExpandModeSelected;
		_stretchModeOptionButton.ItemSelected += StretchModeSelected;
		_opacityLineEdit.TextSubmitted += OnOpacitySubmitted;
		_opacityLineEdit.FocusExited += () => OnOpacitySubmitted(_opacityLineEdit.Text);

		_globalSignals.SyncShellInspector += OnSyncFromHistory;
		
		UiUtilities.FormatLabelsColours(this, GlobalStyles.SoftFontColor);
        
		GetNode<Label>("%InfoLabel").AddThemeColorOverride("font_color", GlobalStyles.DisabledColor);
        
		// Ensure content is hidden at start up
		_inspectorContent.Visible = false;
		_selectFileContainer.Visible = false;
		_previewContainer.Visible = false;
		_audioAccordian.Visible = false;
		_routingAccordian.Visible = false;
		_waveformAccordian.Visible = false;
		
		_previewCollapseButton.Icon = GetThemeIcon("Right", "AtlasIcons");
		_audioCollapseButton.Icon = GetThemeIcon("Right", "AtlasIcons");
		_routingCollapseButton.Icon = GetThemeIcon("Right", "AtlasIcons");
		_waveformCollapseButton.Icon = GetThemeIcon("Right", "AtlasIcons");
		
		// Connect Ui input methods.
		_selectFileButton.Pressed += OpenFileDialog;

		// Accordion connections
		_previewCollapseButton.Pressed += () => ToggleAccordian(_previewContainer, _previewCollapseButton);
		_audioCollapseButton.Pressed += () => ToggleAccordian(_audioAccordian, _audioCollapseButton);
		_routingCollapseButton.Pressed += () => ToggleAccordian(_routingAccordian, _routingCollapseButton);
		_waveformCollapseButton.Pressed += () => ToggleAccordian(_waveformAccordian, _waveformCollapseButton);
		_previewCollapseButton.Pressed += PreviewToggled;
	
		UiLocalizer.LocalizeTree(this);
		if (_globalSignals != null)
			_globalSignals.LocaleChanged += OnLocaleChanged;

		if (_globalData != null && _globalData.FocusedCue >= 0)
			ShellSelected(_globalData.FocusedCue);
		else
			ShowNoSelection();
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

		try
		{
			if (_videoPreviewer != null && IsInstanceValid(_videoPreviewer))
				_videoPreviewer.ClearDecoder();
		}
		catch
		{
			/* best-effort during exit */
		}

		if (_globalSignals != null)
		{
			_globalSignals.ShellFocused -= ShellSelected;
			_globalSignals.SyncShellInspector -= RefreshMediaPathDisplay;
			_globalSignals.SyncShellInspector -= OnSyncFromHistory;
			_globalSignals.CueMediaHealthChanged -= OnCueMediaHealthChanged;
			_globalSignals.DisplaysChanged -= OnDisplaysChangedForTargetLayers;
		}

		_focusedCue = null;
		_focusedVideoComponent = null;
		_videoTargets.Clear();

		base._ExitTree();
	}

	private void AssignUiNodeParameters()
	{
		// Ui Node setup
		_infoLabel = GetNode<Label>("%InfoLabel");
		_selectFileContainer = GetNode<HBoxContainer>("%SelectFileContainer");
		_inspectorContent = GetNode<VBoxContainer>("%InspectorContent");
		_selectFileButton =  GetNode<Button>("%ButtonSelectFile");
		_fileUrl = GetNode<LineEdit>("%FileUrl");
		
		_startTimeInput = GetNode<LineEdit>("%StartTimeInput");
		_endTimeInput = GetNode<LineEdit>("%EndTimeInput");
		_durationValue = GetNode<LineEdit>("%DurationValue");
		_fileDurationValue = GetNode<LineEdit>("%FileDurationValue");
		// Labels share the timing row; hide for still images.
		var timingRow = _startTimeInput.GetParent();
		_startTimeLabel = timingRow?.GetNodeOrNull<Label>("StartTimeLabel");
		_endTimeLabel = timingRow?.GetNodeOrNull<Label>("EndTimeLabel");
		_durationLabel = timingRow?.GetNodeOrNull<Label>("DurationLabel");
		_fileDurationLabel = timingRow?.GetNodeOrNull<Label>("FileDurationLabel");
		_loopInput = GetNode<CheckBox>("%LoopInput");
		_playCountInput  = GetNode<LineEdit>("%PlayCountInput");
		_loopPlayCountRow = _loopInput?.GetParent() as Control;
		_fadeInInput = GetNodeOrNull<LineEdit>("%FadeInInput");
		_fadeOutInput = GetNodeOrNull<LineEdit>("%FadeOutInput");
		
		_fileMetadataLabel = GetNode<Label>("%FileMetadataLabel");
		_targetLayerOptionButton = GetNode<OptionButton>("%TargetLayerOptionButton");
		_expandModeOptionButton = GetNode<OptionButton>("%ExpandModeOptionButton");
		_stretchModeOptionButton = GetNode<OptionButton>("%StretchModeOptionButton");
		_opacityLineEdit = GetNode<LineEdit>("%OpacityLineEdit");
		_scaleWidthLineEdit  = GetNode<LineEdit>("%ScaleWidthLineEdit");
		_scaleHeightLineEdit  = GetNode<LineEdit>("%ScaleHeightLineEdit");
		_offsetXLineEdit  = GetNode<LineEdit>("%OffsetXLineEdit");
		_offsetYLineEdit  = GetNode<LineEdit>("%OffsetYLineEdit");

		PopulateTextureLayoutOptions();
		BuildSubtitleUi();
		
		// Video Previewer
		_previewCollapseButton = GetNode<Button>("%PreviewCollapseButton");
		_previewContainer = GetNode<HBoxContainer>("%PreviewContainer");
		_videoPreviewer = GetNode<VideoPreviewer>("%VideoPreviewer");
	    
		// Audio
		_audioCollapseButton  = GetNode<Button>("%AudioCollapseButton");
		_audioAccordian = GetNode<HBoxContainer>("%AudioAccordian");
		_useAudioCheckButton = GetNode<CheckButton>("%UseAudioCheckButton");
		_useAudioLabel = GetNode<Label>("%UseAudioLabel");
		_outputOptionButton = GetNode<OptionButton>("%OutputOptionButton");
		_routingCollapseButton = GetNode<Button>("%RoutingCollapseButton");
		_routingAccordian =  GetNode<VBoxContainer>("%RoutingAccordian");
		_routingMatrixGrid = GetNode<GridContainer>("%RoutingMatrixGrid");
		_routingContainer = GetNode<VBoxContainer>("%RoutingContainer");
		_waveformCollapseButton  = GetNode<Button>("%WaveformCollapseButton");
		_waveformAccordian =   GetNode<VBoxContainer>("%WaveformAccordian");
		_volumeInput = GetNode<LineEdit>("%VolumeInput");
		_panLabel = GetNodeOrNull<Label>("%PanLabel");
		_panSlider = GetNodeOrNull<HSlider>("%PanSlider");
		_panInput = GetNodeOrNull<LineEdit>("%PanInput");
	}

	/// <summary>
	/// Builds the closed-caption row (link to Text component + track picker) under Target Layer.
	/// </summary>
	private void BuildSubtitleUi()
	{
		if (_inspectorContent == null || _targetLayerOptionButton == null)
			return;

		var targetRow = _targetLayerOptionButton.GetParent() as Control;
		if (targetRow == null)
			return;

		_subtitleRow = new HBoxContainer
		{
			Name = "SubtitleRow",
			Visible = false
		};
		_subtitleRow.AddThemeConstantOverride("separation", 8);

		var ccLabel = new Label { Text = "Captions:" };
		_subtitleRow.AddChild(ccLabel);

		_useSubtitlesCheck = new CheckBox
		{
			Text = "Link to Text",
			TooltipText =
				UiLocalizer.T("When enabled, the cue’s Text component shows this file’s closed captions during playback. ") +
				"Requires a Text component on the same cue (use + Text if missing)."
		};
		_useSubtitlesCheck.Toggled += OnUseSubtitlesToggled;
		_subtitleRow.AddChild(_useSubtitlesCheck);

		_subtitleTrackOption = new OptionButton
		{
			CustomMinimumSize = new Vector2(180, 0),
			SizeFlagsHorizontal = SizeFlags.ExpandFill,
			TooltipText = UiLocalizer.T("Subtitle / closed-caption track to display."),
			FitToLongestItem = false,
			ClipText = true
		};
		_subtitleTrackOption.ItemSelected += OnSubtitleTrackSelected;
		_subtitleRow.AddChild(_subtitleTrackOption);

		_addTextForCcButton = new Button
		{
			Text = UiLocalizer.T("+ Text"),
			TooltipText = UiLocalizer.T("Add a Text component to this cue for closed captions."),
			Visible = false
		};
		_addTextForCcButton.Pressed += OnAddTextForCcPressed;
		_subtitleRow.AddChild(_addTextForCcButton);

		_inspectorContent.AddChild(_subtitleRow);
		_inspectorContent.MoveChild(_subtitleRow, targetRow.GetIndex() + 1);
	}

	/// <summary>
	/// Fills Expand / Stretch option buttons with user-facing labels and tooltips.
	/// </summary>
	private void PopulateTextureLayoutOptions()
	{
		if (_expandModeOptionButton != null)
		{
			_expandModeOptionButton.Clear();
			AddTextureOption(_expandModeOptionButton, "Keep Size", (int)TextureRect.ExpandModeEnum.KeepSize,
				"Show the video at its original pixel size. It will not grow to fill the layer.");
			AddTextureOption(_expandModeOptionButton, "Ignore Size", (int)TextureRect.ExpandModeEnum.IgnoreSize,
				"Fill the full layer area. Use Stretch to control how the picture fits (recommended default).");
			AddTextureOption(_expandModeOptionButton, "Fit Width Proportional", (int)TextureRect.ExpandModeEnum.FitWidthProportional,
				"Match the layer width; height scales automatically to keep the video’s aspect ratio.");
			AddTextureOption(_expandModeOptionButton, "Fit Height Proportional", (int)TextureRect.ExpandModeEnum.FitHeightProportional,
				"Match the layer height; width scales automatically to keep the video’s aspect ratio.");
		}

		if (_stretchModeOptionButton != null)
		{
			_stretchModeOptionButton.Clear();
			AddTextureOption(_stretchModeOptionButton, "Scale", (int)TextureRect.StretchModeEnum.Scale,
				"Stretch the picture to fill the area. Aspect ratio may change (distort).");
			AddTextureOption(_stretchModeOptionButton, "Tile", (int)TextureRect.StretchModeEnum.Tile,
				"Repeat the picture like tiles to fill the area.");
			AddTextureOption(_stretchModeOptionButton, "Keep", (int)TextureRect.StretchModeEnum.Keep,
				"Show at original size, top-left aligned. May crop or leave empty space.");
			AddTextureOption(_stretchModeOptionButton, "Keep Centered", (int)TextureRect.StretchModeEnum.KeepCentered,
				"Show at original size, centered. May crop or leave empty space.");
			AddTextureOption(_stretchModeOptionButton, "Keep Aspect", (int)TextureRect.StretchModeEnum.KeepAspect,
				"Scale to fit inside without cropping; keeps aspect ratio. Top-left aligned.");
			AddTextureOption(_stretchModeOptionButton, "Keep Aspect Centered", (int)TextureRect.StretchModeEnum.KeepAspectCentered,
				"Scale to fit inside without cropping; keeps aspect ratio and centers the picture (letterbox / pillarbox).");
			AddTextureOption(_stretchModeOptionButton, "Keep Aspect Covered", (int)TextureRect.StretchModeEnum.KeepAspectCovered,
				"Scale to cover the whole area while keeping aspect ratio. Edges may be cropped.");
		}
	}

	private static void AddTextureOption(OptionButton button, string label, int id, string tooltip)
	{
		UiLocalizer.AddTranslatedItem(button, label, id, tooltip);
	}

	private void SetupWaveformUi()
	{
		_waveformPanel = GetNode<PanelContainer>("%WaveformPanel");
		_waveformDisplay = new WaveformDisplay();
		_waveformPanel.AddChild(_waveformDisplay);
		_waveformPanel.MoveChild(_waveformDisplay, 0);
		_waveformPanel.Resized += () => { if (_waveformAccordian.Visible) _ = DrawWaveform(); };
		_waveformPanel.GuiInput += OnWaveformPanelGuiInput;

		_startDragHandle = GetNode<Button>("%StartDragHandle");
		_endDragHandle = GetNode<Button>("%EndDragHandle");
		_startDragHandle.CustomMinimumSize = new Vector2(10, 0);
		_endDragHandle.CustomMinimumSize = new Vector2(10, 0);
		_startDragHandle.Modulate = GlobalStyles.LowColor1;
		_endDragHandle.Modulate = GlobalStyles.HighColor1;
		UiLocalizer.SetTooltip(_startDragHandle, "Start time (drag)");
		UiLocalizer.SetTooltip(_endDragHandle, "End time (drag)");
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
	}

	private void OnZoomChanged(double value)
	{
		float zoom = Mathf.Max(1f, (float)value);
		float oldSpan = _viewSpanNorm;
		float center = _viewStartNorm + oldSpan * 0.5f;
		_viewSpanNorm = 1f / zoom;
		_viewStartNorm = Mathf.Clamp(center - _viewSpanNorm * 0.5f, 0f, 1f - _viewSpanNorm);
		SyncWaveformScrollBar();
		_ = DrawWaveform();
	}

	private void OnWaveformScrollChanged(double value)
	{
		float maxStart = Math.Max(0f, 1f - _viewSpanNorm);
		_viewStartNorm = maxStart <= 0 ? 0 : Mathf.Clamp((float)value, 0f, maxStart);
		_ = DrawWaveform();
	}

	private void SyncWaveformScrollBar()
	{
		if (_waveformScroll == null) return;
		bool zoomed = _viewSpanNorm < 0.999f;
		_waveformScroll.Visible = zoomed;
		if (!zoomed)
		{
			_viewStartNorm = 0f;
			return;
		}
		float maxStart = Math.Max(0.0001f, 1f - _viewSpanNorm);
		_waveformScroll.MinValue = 0;
		_waveformScroll.MaxValue = maxStart;
		_waveformScroll.Page = Math.Max(0.01, _viewSpanNorm * maxStart);
		_waveformScroll.Step = maxStart / 200.0;
		_waveformScroll.SetValueNoSignal(Mathf.Clamp(_viewStartNorm, 0f, maxStart));
	}

	private void OnWaveformPanelGuiInput(InputEvent @event)
	{
		if (@event is InputEventMouseButton mb && mb.Pressed &&
		    (mb.ButtonIndex == MouseButton.WheelUp || mb.ButtonIndex == MouseButton.WheelDown))
		{
			if (mb.CtrlPressed && _zoomSlider != null)
			{
				double z = _zoomSlider.Value;
				z += mb.ButtonIndex == MouseButton.WheelUp ? 0.5 : -0.5;
				_zoomSlider.Value = Mathf.Clamp((float)z, (float)_zoomSlider.MinValue, (float)_zoomSlider.MaxValue);
				AcceptEvent();
			}
			else if (_viewSpanNorm < 0.999f)
			{
				float delta = _viewSpanNorm * 0.15f * (mb.ButtonIndex == MouseButton.WheelUp ? -1f : 1f);
				float maxStart = 1f - _viewSpanNorm;
				_viewStartNorm = Mathf.Clamp(_viewStartNorm + delta, 0f, maxStart);
				SyncWaveformScrollBar();
				_ = DrawWaveform();
				AcceptEvent();
			}
		}
	}

	/// <summary>
	/// Called when a cue shell is selected. Updates UI based on presence of AudioComponent.
	/// </summary>
	/// <param name="cueId">The ID of the selected cue.</param>
	private StyleBoxFlat _fileUrlMissingStyle;
	private Button _deleteVideoComponentButton;

	/// <summary>
	/// Refreshes the file URL field when media paths are rewritten (e.g. after show-local backup).
	/// </summary>

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
		if (_addTextForCcButton != null)
		{
			UiLocalizer.SetText(_addTextForCcButton, "+ Text");
			UiLocalizer.SetTooltip(_addTextForCcButton, "Add a Text component to this cue for closed captions.");
		}
	}

}
