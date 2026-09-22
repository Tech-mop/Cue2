// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System;
using Cue2.App;
using Cue2.UI.Shell;
using Cue2.Services;
using Cue2.Domain.Cuelist;
using Cue2.Domain.Playback;
using Cue2.Domain.Devices;
using Cue2.Domain.ShowSettings;
using Cue2.Domain.Metadata;
using Cue2.Domain.Cues;
using Cue2.Domain.Connections;
using Cue2.Domain.Library;
using Cue2.Domain.Commands;
using Cue2.UI.Utilities;
using Godot;
using System.Linq;

namespace Cue2.UI.Inspectors;

/// <summary>
/// Inspector card for a single <see cref="ControlComponent"/> (GO / Pause / Stop / Fade / etc.).
/// </summary>
/// <remarks>
/// User may edit target by id or cue number; the other field and name label update when a match is found.
/// Hold the pick-target button and release over a shell to assign the target from the cuelist.
/// Stop/GO cards expose fade times with per-field reset buttons.
/// Fade cards pick exactly one property (volume, pan, opacity, or a routing-matrix cell).
/// Multiple controls on one cue run in list order (reorder with up/down).
/// </remarks>
public partial class ControlComponentCard : PanelContainer
{
    private Label _orderLabel;
    private Label _actionLabel;
    private Button _moveUpButton;
    private Button _moveDownButton;
    private Button _pickTargetButton;
    private Button _deleteButton;
    private LineEdit _idLineEdit;
    private LineEdit _numberLineEdit;
    private Label _nameLabel;
    private Button _targetResetButton;
    private Label _fadeCaption;
    private Control _fadeRow;
    private LineEdit _fadeLineEdit;
    private Button _fadeResetButton;

    private Label _modeCaption;
    private OptionButton _fadeModeOption;
    private Label _propertyCaption;
    private OptionButton _fadePropertyOption;
    private Label _noFadableLabel;
    private Control _noFadableSpacer;
    private Label _audioFadeCaption;
    private Control _audioFadeRow;
    private LineEdit _audioFadeLineEdit;
    private Label _opacityFadeCaption;
    private Control _opacityFadeRow;
    private LineEdit _opacityFadeLineEdit;
    private Label _panFadeCaption;
    private Control _panFadeRow;
    private HSlider _panFadeSlider;
    private LineEdit _panFadeLineEdit;
    private Control _matrixContainer;
    private Label _matrixHint;
    private GridContainer _matrixGrid;
    private Label _seekTimeCaption;
    private LineEdit _seekTimeLineEdit;
    private Label _layerCaption;
    private OptionButton _targetLayerOption;
    private Label _sizeEnableCaption;
    private Control _sizeEnableRow;
    private CheckBox _sizeEnable;
    private LineEdit _sizeXLineEdit;
    private LineEdit _sizeYLineEdit;
    private Label _posEnableCaption;
    private Control _posEnableRow;
    private CheckBox _posEnable;
    private LineEdit _posXLineEdit;
    private LineEdit _posYLineEdit;

    private ControlComponent _component;
    private ControlInspector _inspector;
    private GlobalData _globalData;
    private GlobalSignals _globalSignals;

    private int _orderIndex;
    private int _orderCount = 1;

    private bool _isSyncingUi;
    private bool _idEditing;
    private bool _numberEditing;
    private bool _fadeEditing;
    private bool _audioFadeEditing;
    private bool _opacityFadeEditing;
    private bool _panFadeEditing;
    private bool _isUpdatingPanUi;
    private bool _matrixCellEditing;
    private bool _seekTimeEditing;
    private bool _sizeEditing;
    private bool _posEditing;

    // --- Interactive pick-target tool ---
    private bool _pickActive;
    private CanvasLayer _pickLayer;
    private PanelContainer _pickBadge;
    private Label _pickBadgeLabel;
    private TextureRect _pickBadgeIcon;
    private ShellBar _pickHoverShell;
    private Color _pickHoverRestoreModulate = Colors.White;

    /// <inheritdoc />
    public override void _Ready()
    {
        _globalData = GetNodeOrNull<GlobalData>("/root/GlobalData");
        _globalSignals = GetNodeOrNull<GlobalSignals>("/root/GlobalSignals");
        SetProcess(false);
        SetProcessInput(false);

        _orderLabel = GetNodeOrNull<Label>("%OrderLabel");
        _actionLabel = GetNode<Label>("%ActionLabel");
        _moveUpButton = GetNodeOrNull<Button>("%MoveUpButton");
        _moveDownButton = GetNodeOrNull<Button>("%MoveDownButton");
        _pickTargetButton = GetNode<Button>("%PickTargetButton");
        _deleteButton = GetNode<Button>("%DeleteButton");
        _idLineEdit = GetNode<LineEdit>("%IdLineEdit");
        _numberLineEdit = GetNode<LineEdit>("%NumberLineEdit");
        _nameLabel = GetNode<Label>("%NameLabel");
        _targetResetButton = GetNode<Button>("%TargetResetButton");
        _fadeCaption = GetNode<Label>("%FadeCaption");
        _fadeRow = GetNode<Control>("%FadeRow");
        _fadeLineEdit = GetNode<LineEdit>("%FadeLineEdit");
        _fadeResetButton = GetNode<Button>("%FadeResetButton");

        _modeCaption = GetNodeOrNull<Label>("%ModeCaption");
        _fadeModeOption = GetNodeOrNull<OptionButton>("%FadeModeOption");
        _propertyCaption = GetNodeOrNull<Label>("%PropertyCaption");
        _fadePropertyOption = GetNodeOrNull<OptionButton>("%FadePropertyOption");
        _noFadableLabel = GetNodeOrNull<Label>("%NoFadableLabel");
        _noFadableSpacer = GetNodeOrNull<Control>("%NoFadableSpacer");
        _audioFadeCaption = GetNodeOrNull<Label>("%AudioFadeCaption");
        _audioFadeRow = GetNodeOrNull<Control>("%AudioFadeRow");
        _audioFadeLineEdit = GetNodeOrNull<LineEdit>("%AudioFadeLineEdit");
        _opacityFadeCaption = GetNodeOrNull<Label>("%OpacityFadeCaption");
        _opacityFadeRow = GetNodeOrNull<Control>("%OpacityFadeRow");
        _opacityFadeLineEdit = GetNodeOrNull<LineEdit>("%OpacityFadeLineEdit");
        _panFadeCaption = GetNodeOrNull<Label>("%PanFadeCaption");
        _panFadeRow = GetNodeOrNull<Control>("%PanFadeRow");
        _panFadeSlider = GetNodeOrNull<HSlider>("%PanFadeSlider");
        _panFadeLineEdit = GetNodeOrNull<LineEdit>("%PanFadeLineEdit");
        _matrixContainer = GetNodeOrNull<Control>("%MatrixContainer");
        _matrixHint = GetNodeOrNull<Label>("%MatrixHint");
        _matrixGrid = GetNodeOrNull<GridContainer>("%MatrixGrid");

        if (_fadeModeOption != null)
        {
            _fadeModeOption.Clear();
            UiLocalizer.AddTranslatedItem(_fadeModeOption, "Absolute", (int)ControlFadeMode.Absolute);
            UiLocalizer.AddTranslatedItem(_fadeModeOption, "Relative", (int)ControlFadeMode.Relative);
            _fadeModeOption.ItemSelected += OnFadeModeSelected;
        }

        if (_fadePropertyOption != null)
            _fadePropertyOption.ItemSelected += OnFadePropertySelected;

        if (_audioFadeLineEdit != null)
        {
            _audioFadeLineEdit.TextSubmitted += OnAudioFadeSubmitted;
            _audioFadeLineEdit.FocusExited += OnAudioFadeFocusExited;
            _audioFadeLineEdit.TextChanged += _ => _audioFadeEditing = true;
            // Volume fades: absolute −60…+12; relative ± with result clamped on execute.
            LineEditDbDragSlider.EnableSignedDb(
                _audioFadeLineEdit,
                minDb: UiUtilities.MinVolumeDb,
                maxDb: UiUtilities.MaxComponentGainDb);
        }

        if (_opacityFadeLineEdit != null)
        {
            _opacityFadeLineEdit.TextSubmitted += OnOpacityFadeSubmitted;
            _opacityFadeLineEdit.FocusExited += OnOpacityFadeFocusExited;
            _opacityFadeLineEdit.TextChanged += _ => _opacityFadeEditing = true;
        }

        if (_panFadeSlider != null)
        {
            _panFadeSlider.ValueChanged += OnPanFadeSliderChanged;
            _panFadeSlider.DragEnded += OnPanFadeSliderDragEnded;
        }

        if (_panFadeLineEdit != null)
        {
            _panFadeLineEdit.TextSubmitted += OnPanFadeSubmitted;
            _panFadeLineEdit.FocusExited += OnPanFadeFocusExited;
            _panFadeLineEdit.TextChanged += _ => _panFadeEditing = true;
        }

        _seekTimeCaption = GetNodeOrNull<Label>("%SeekTimeCaption");
        _seekTimeLineEdit = GetNodeOrNull<LineEdit>("%SeekTimeLineEdit");
        if (_seekTimeLineEdit != null)
        {
            _seekTimeLineEdit.TextSubmitted += OnSeekTimeSubmitted;
            _seekTimeLineEdit.FocusExited += OnSeekTimeFocusExited;
            _seekTimeLineEdit.TextChanged += _ => _seekTimeEditing = true;
        }

        _layerCaption = GetNodeOrNull<Label>("%LayerCaption");
        _targetLayerOption = GetNodeOrNull<OptionButton>("%TargetLayerOption");
        if (_targetLayerOption != null)
            _targetLayerOption.ItemSelected += OnTargetLayerSelected;

        _sizeEnableCaption = GetNodeOrNull<Label>("%SizeEnableCaption");
        _sizeEnableRow = GetNodeOrNull<Control>("%SizeEnableRow");
        _sizeEnable = GetNodeOrNull<CheckBox>("%SizeEnable");
        _sizeXLineEdit = GetNodeOrNull<LineEdit>("%SizeXLineEdit");
        _sizeYLineEdit = GetNodeOrNull<LineEdit>("%SizeYLineEdit");
        if (_sizeEnable != null)
            _sizeEnable.Toggled += OnSizeEnableToggled;
        if (_sizeXLineEdit != null)
        {
            _sizeXLineEdit.TextSubmitted += _ => OnSizeSubmitted(_sizeXLineEdit);
            _sizeXLineEdit.FocusExited += () => { if (_sizeEditing) CommitSizeFields(); };
            _sizeXLineEdit.TextChanged += _ => _sizeEditing = true;
        }
        if (_sizeYLineEdit != null)
        {
            _sizeYLineEdit.TextSubmitted += _ => OnSizeSubmitted(_sizeYLineEdit);
            _sizeYLineEdit.FocusExited += () => { if (_sizeEditing) CommitSizeFields(); };
            _sizeYLineEdit.TextChanged += _ => _sizeEditing = true;
        }

        _posEnableCaption = GetNodeOrNull<Label>("%PosEnableCaption");
        _posEnableRow = GetNodeOrNull<Control>("%PosEnableRow");
        _posEnable = GetNodeOrNull<CheckBox>("%PosEnable");
        _posXLineEdit = GetNodeOrNull<LineEdit>("%PosXLineEdit");
        _posYLineEdit = GetNodeOrNull<LineEdit>("%PosYLineEdit");
        if (_posEnable != null)
            _posEnable.Toggled += OnPosEnableToggled;
        if (_posXLineEdit != null)
        {
            _posXLineEdit.TextSubmitted += _ => OnPosSubmitted(_posXLineEdit);
            _posXLineEdit.FocusExited += () => { if (_posEditing) CommitPosFields(); };
            _posXLineEdit.TextChanged += _ => _posEditing = true;
        }
        if (_posYLineEdit != null)
        {
            _posYLineEdit.TextSubmitted += _ => OnPosSubmitted(_posYLineEdit);
            _posYLineEdit.FocusExited += () => { if (_posEditing) CommitPosFields(); };
            _posYLineEdit.TextChanged += _ => _posEditing = true;
        }

        if (_moveUpButton != null)
        {
            _moveUpButton.Icon = GetThemeIcon("Up", "AtlasIcons");
            _moveUpButton.Pressed += () => _inspector?.MoveControlComponent(_component, -1);
        }

        if (_moveDownButton != null)
        {
            _moveDownButton.Icon = GetThemeIcon("Down", "AtlasIcons");
            _moveDownButton.Pressed += () => _inspector?.MoveControlComponent(_component, +1);
        }

        // Pointing / crosshair feel for the pick tool.
        // GuiInput + AcceptEvent (not ButtonDown): mouse-up is consumed globally in _Input while
        // picking, which would leave BaseButton latched and require a second click — same fix as
        // ShellBar reorder grabber (OnDragBarGuiInput / ReleaseDragGrabber).
        _pickTargetButton.Icon = GetThemeIcon("Target", "AtlasIcons");
        _pickTargetButton.TooltipText = UiLocalizer.T("Hold and release over a cue in the list to set target");
        _pickTargetButton.KeepPressedOutside = false;
        _pickTargetButton.GuiInput += OnPickTargetButtonGuiInput;

        _deleteButton.Icon = GetThemeIcon("DeleteBin", "AtlasIcons");
        _deleteButton.Pressed += OnDeletePressed;

        _targetResetButton.Icon = GetThemeIcon("Refresh", "AtlasIcons");
        _targetResetButton.Pressed += OnTargetResetPressed;

        _fadeResetButton.Icon = GetThemeIcon("Refresh", "AtlasIcons");
        _fadeResetButton.Pressed += OnFadeResetPressed;

        _idLineEdit.TextSubmitted += OnIdSubmitted;
        _idLineEdit.FocusExited += OnIdFocusExited;
        _idLineEdit.TextChanged += _ => _idEditing = true;

        _numberLineEdit.TextSubmitted += OnNumberSubmitted;
        _numberLineEdit.FocusExited += OnNumberFocusExited;
        _numberLineEdit.TextChanged += _ => _numberEditing = true;

        _fadeLineEdit.TextSubmitted += OnFadeSubmitted;
        _fadeLineEdit.FocusExited += OnFadeFocusExited;
        _fadeLineEdit.TextChanged += _ => _fadeEditing = true;

        // Esc releases focus so show hotkeys (GO, etc.) resume. Dynamic matrix cells wired on build.
        WireAllLineEditEscapeUnfocus();
    }

    /// <summary>
    /// Ensures every LineEdit on this card unfocuses on Escape (including fields that may be
    /// hidden for the current action type).
    /// </summary>
    private void WireAllLineEditEscapeUnfocus()
    {
        UiUtilities.WireLineEditEscapeReleasesFocus(_idLineEdit);
        UiUtilities.WireLineEditEscapeReleasesFocus(_numberLineEdit);
        UiUtilities.WireLineEditEscapeReleasesFocus(_fadeLineEdit);
        UiUtilities.WireLineEditEscapeReleasesFocus(_audioFadeLineEdit);
        UiUtilities.WireLineEditEscapeReleasesFocus(_opacityFadeLineEdit);
        UiUtilities.WireLineEditEscapeReleasesFocus(_panFadeLineEdit);
        UiUtilities.WireLineEditEscapeReleasesFocus(_seekTimeLineEdit);
        UiUtilities.WireLineEditEscapeReleasesFocus(_sizeXLineEdit);
        UiUtilities.WireLineEditEscapeReleasesFocus(_sizeYLineEdit);
        UiUtilities.WireLineEditEscapeReleasesFocus(_posXLineEdit);
        UiUtilities.WireLineEditEscapeReleasesFocus(_posYLineEdit);
    }

    /// <inheritdoc />
    public override void _ExitTree()
    {
        EndPickMode(applyTarget: false);
    }

    /// <inheritdoc />
    public override void _Input(InputEvent @event)
    {
        if (!_pickActive)
            return;

        // Escape cancels without changing target.
        if (@event is InputEventKey key && key.Pressed && !key.Echo && key.Keycode == Key.Escape)
        {
            EndPickMode(applyTarget: false);
            GetViewport()?.SetInputAsHandled();
            return;
        }

        // Wheel / trackpad: the hold started on the inspector button, which captures
        // the pointer so the cuelist never sees the wheel. Forward it while over the list.
        if (TryHandlePickScrollInput(@event))
            return;

        // Release left button: assign shell under cursor (if any).
        if (@event is InputEventMouseButton mb &&
            mb.ButtonIndex == MouseButton.Left &&
            !mb.Pressed)
        {
            EndPickMode(applyTarget: true);
            GetViewport()?.SetInputAsHandled();
        }
    }

    /// <inheritdoc />
    public override void _Process(double delta)
    {
        if (!_pickActive)
            return;

        var list = _globalData?.Cuelist;
        if (list != null)
            list.AutoScrollCueListFromPointer(GetViewport().GetMousePosition(), delta);

        UpdatePickCursorAndBadge();
    }

    /// <summary>
    /// Applies wheel / trackpad pan to the cuelist while pick-target is held and the
    /// pointer is over the list. Marks the event handled so the inspector does not scroll.
    /// </summary>
    /// <param name="event">Viewport input event.</param>
    /// <returns>True when the event was consumed as a cuelist scroll.</returns>
    private bool TryHandlePickScrollInput(InputEvent @event)
    {
        var list = _globalData?.Cuelist;
        if (list == null)
            return false;

        var view = list.GetCueListScrollGlobalRect();
        if (view.Size.Y < 1f)
            return false;

        var mouse = GetViewport().GetMousePosition();
        if (!view.Grow(8f).HasPoint(mouse))
            return false;

        if (@event is InputEventMouseButton wheel &&
            wheel.Pressed &&
            (wheel.ButtonIndex == MouseButton.WheelUp || wheel.ButtonIndex == MouseButton.WheelDown))
        {
            float steps = wheel.Factor > 0.01f ? wheel.Factor : 1f;
            float dir = wheel.ButtonIndex == MouseButton.WheelUp ? -1f : 1f;
            list.ScrollCueListByWheel(dir * steps);
            GetViewport()?.SetInputAsHandled();
            return true;
        }

        if (@event is InputEventPanGesture pan && !Mathf.IsZeroApprox(pan.Delta.Y))
        {
            list.ScrollCueListByPixels(pan.Delta.Y);
            GetViewport()?.SetInputAsHandled();
            return true;
        }

        return false;
    }

    /// <summary>
    /// Binds this card to a component and parent inspector.
    /// </summary>
    /// <param name="component">Control component model.</param>
    /// <param name="inspector">Owning inspector (for remove / reorder).</param>
    /// <param name="orderIndex">0-based execution order among control components.</param>
    /// <param name="orderCount">Total control components on the cue.</param>
    public void SetComponent(
        ControlComponent component,
        ControlInspector inspector,
        int orderIndex = 0,
        int orderCount = 1)
    {
        _component = component;
        _inspector = inspector;
        _orderIndex = orderIndex;
        _orderCount = Math.Max(1, orderCount);
        RefreshFromComponent();
    }

    /// <summary>
    /// Pushes model values into the UI fields.
    /// </summary>
    public void RefreshFromComponent()
    {
        if (_component == null) return;

        _isSyncingUi = true;
        try
        {
            if (_orderLabel != null)
                _orderLabel.Text = $"#{_orderIndex + 1}";

            if (_moveUpButton != null)
                _moveUpButton.Disabled = _orderIndex <= 0;
            if (_moveDownButton != null)
                _moveDownButton.Disabled = _orderIndex >= _orderCount - 1;

            _actionLabel.Text = UiLocalizer.T(ControlComponent.GetActionDisplayName(_component.Action)).ToUpperInvariant();

            bool isTranslate = _component.Action == ControlAction.TranslateLayer;
            bool showCueTarget = !isTranslate;

            // Cue-target fields (hidden for Translate Layer).
            SetCueTargetFieldsVisible(showCueTarget);
            if (showCueTarget)
            {
                _component.ResolveTargetIfNeeded();

                _idLineEdit.Text = _component.TargetCueId >= 0
                    ? _component.TargetCueId.ToString()
                    : string.Empty;
                _numberLineEdit.Text = _component.TargetCueNum ?? string.Empty;

                string name = _component.GetTargetCueName();
                _nameLabel.Text = string.IsNullOrEmpty(name) ? "(none)" : name;
            }

            if (_pickTargetButton != null)
                _pickTargetButton.Visible = showCueTarget;

            bool showGoStopFade = _component.Action is ControlAction.Stop or ControlAction.Go;
            bool isPropertyFade = _component.Action == ControlAction.Fade;
            bool isSeek = _component.Action == ControlAction.Seek;
            // Translate uses the same Time: row as property fade duration.
            bool showTimeFade = showGoStopFade || isPropertyFade || isTranslate;

            if (_fadeCaption != null)
            {
                _fadeCaption.Visible = showTimeFade;
                _fadeCaption.Text = _component.Action switch
                {
                    ControlAction.Go => "Fade In:",
                    ControlAction.Stop => "Fade Out:",
                    ControlAction.Fade => "Time:",
                    ControlAction.TranslateLayer => "Time:",
                    _ => "Fade:"
                };
            }
            if (_fadeRow != null)
                _fadeRow.Visible = showTimeFade;

            if (_component.Action == ControlAction.Stop)
            {
                float sessionDefault = _globalData?.Settings?.StopFadeDuration ?? 0f;
                double displayFade = _component.ResolveStopFadeDuration(sessionDefault);
                _fadeLineEdit.Text = UiUtilities.FormatTime(displayFade);
                _fadeLineEdit.PlaceholderText = UiLocalizer.Tf("session ({0})", UiUtilities.FormatTime(sessionDefault));
                _fadeLineEdit.TooltipText =
                    UiLocalizer.T("Stop fade-out time. 0 = immediate. Default follows session Stop Fade.");
            }
            else if (_component.Action == ControlAction.Go)
            {
                _fadeLineEdit.Text = UiUtilities.FormatTime(_component.GoFadeInDuration);
                _fadeLineEdit.PlaceholderText = UiLocalizer.T("0 (no fade-in)");
                _fadeLineEdit.TooltipText =
                    UiLocalizer.T("GO fade-in time for the target cue. 0 = no control fade-in (default).");
            }
            else if (isPropertyFade)
            {
                _fadeLineEdit.Text = UiUtilities.FormatTime(_component.PropertyFadeDuration);
                _fadeLineEdit.PlaceholderText = UiLocalizer.T("1.000");
                _fadeLineEdit.TooltipText =
                    UiLocalizer.T("Property fade duration. 0 = snap immediately. Default 1s.");
            }
            else if (isTranslate)
            {
                _fadeLineEdit.Text = UiUtilities.FormatTime(_component.TranslateDuration);
                _fadeLineEdit.PlaceholderText = UiLocalizer.T("0 (instant)");
                _fadeLineEdit.TooltipText =
                    UiLocalizer.T("Layer translate duration. 0 = instant change (default).");
            }

            RefreshPropertyFadeUi(isPropertyFade);
            RefreshSeekUi(isSeek);
            RefreshTranslateLayerUi(isTranslate);

            UpdateResetButtons();
        }
        finally
        {
            _isSyncingUi = false;
            _idEditing = false;
            _numberEditing = false;
            _fadeEditing = false;
            _audioFadeEditing = false;
            _opacityFadeEditing = false;
            _panFadeEditing = false;
            _isUpdatingPanUi = false;
            _matrixCellEditing = false;
            _seekTimeEditing = false;
            _sizeEditing = false;
            _posEditing = false;
        }
    }

    private void SetCueTargetFieldsVisible(bool visible)
    {
        // ID / No / Name captions are not unique-named; hide via line edits' parents where needed.
        if (_idLineEdit != null)
        {
            _idLineEdit.Visible = visible;
            if (_idLineEdit.GetParent() is Control idRow)
                idRow.Visible = visible;
        }
        if (_numberLineEdit != null)
            _numberLineEdit.Visible = visible;
        if (_nameLabel != null)
            _nameLabel.Visible = visible;

        // Caption labels are previous siblings in the 2-col grid — hide by walking grid children is fragile;
        // leave captions if fields empty is messy. Hide the whole first columns by finding grid.
        var grid = GetNodeOrNull<GridContainer>("MarginContainer/VBoxContainer/TargetGrid");
        if (grid == null) return;
        foreach (var child in grid.GetChildren())
        {
            if (child is not Control c) continue;
            // Hide static captions for cue targeting when not needed.
            string n = c.Name;
            if (n == "IdCaption" || n == "NumberCaption" || n == "NameCaption")
                c.Visible = visible;
        }
    }

    /// <summary>
    /// Shows Absolute/Relative mode, property picker, and the single value row for Fade controls.
    /// </summary>
    private void RefreshPropertyFadeUi(bool isPropertyFade)
    {
        // Mode option is shared with Seek / Translate Layer — only configure Fade-specific rows here.
        if (!isPropertyFade)
        {
            bool keepMode = _component?.Action is ControlAction.Seek or ControlAction.TranslateLayer;
            if (!keepMode)
            {
                if (_modeCaption != null) _modeCaption.Visible = false;
                if (_fadeModeOption != null) _fadeModeOption.Visible = false;
            }

            HideAllFadePropertyRows();
            if (_component?.Action is not (ControlAction.Seek or ControlAction.TranslateLayer))
            {
                if (_noFadableLabel != null) _noFadableLabel.Visible = false;
                if (_noFadableSpacer != null) _noFadableSpacer.Visible = false;
            }
            return;
        }

        if (_modeCaption != null)
            _modeCaption.Visible = true;
        if (_fadeModeOption != null)
        {
            _fadeModeOption.Visible = true;
            _fadeModeOption.Select((int)_component.FadeMode);
        }

        // Resolve target + available properties once a real target is set.
        Cue target = null;
        System.Collections.Generic.List<ControlFadeProperty> available = null;
        if (_component.TargetCueId >= 0)
        {
            target = CueList.FetchCueFromId(_component.TargetCueId);
            if (target != null)
                available = ControlComponent.GetAvailableFadeProperties(target);
        }

        bool hasTarget = _component.TargetCueId >= 0;
        bool showNoFadable = hasTarget && (available == null || available.Count == 0);
        bool showPropertyPicker = hasTarget && available != null && available.Count > 0;

        if (_noFadableLabel != null)
        {
            _noFadableLabel.Visible = showNoFadable;
            if (showNoFadable)
                _noFadableLabel.Text = UiLocalizer.T("No valid fadable properties");
        }
        if (_noFadableSpacer != null)
            _noFadableSpacer.Visible = showNoFadable;

        if (_propertyCaption != null)
            _propertyCaption.Visible = showPropertyPicker;
        if (_fadePropertyOption != null)
            _fadePropertyOption.Visible = showPropertyPicker;

        if (!showPropertyPicker)
        {
            HideAllFadeValueRows();
            return;
        }

        // Ensure stored property is still valid; otherwise pick the first available.
        if (!available.Contains(_component.FadeProperty))
        {
            _component.FadeProperty = available[0];
            _component.FadeAudioVolumeEnabled = _component.FadeProperty == ControlFadeProperty.Volume;
            _component.FadeVideoOpacityEnabled = _component.FadeProperty == ControlFadeProperty.Opacity;
        }

        PopulateFadePropertyOption(available, _component.FadeProperty);
        ShowFadeValueRowsFor(_component.FadeProperty, target);
    }

    private void HideAllFadePropertyRows()
    {
        if (_propertyCaption != null) _propertyCaption.Visible = false;
        if (_fadePropertyOption != null) _fadePropertyOption.Visible = false;
        HideAllFadeValueRows();
    }

    private void HideAllFadeValueRows()
    {
        if (_audioFadeCaption != null) _audioFadeCaption.Visible = false;
        if (_audioFadeRow != null) _audioFadeRow.Visible = false;
        if (_opacityFadeCaption != null) _opacityFadeCaption.Visible = false;
        if (_opacityFadeRow != null) _opacityFadeRow.Visible = false;
        if (_panFadeCaption != null) _panFadeCaption.Visible = false;
        if (_panFadeRow != null) _panFadeRow.Visible = false;
        if (_matrixContainer != null) _matrixContainer.Visible = false;
    }

    private void PopulateFadePropertyOption(
        System.Collections.Generic.List<ControlFadeProperty> available,
        ControlFadeProperty selected)
    {
        if (_fadePropertyOption == null) return;

        _fadePropertyOption.Clear();
        int selectIdx = 0;
        for (int i = 0; i < available.Count; i++)
        {
            var prop = available[i];
            UiLocalizer.AddTranslatedItem(_fadePropertyOption, ControlComponent.GetFadePropertyDisplayName(prop), i);
            _fadePropertyOption.SetItemMetadata(i, (int)prop);
            if (prop == selected)
                selectIdx = i;
        }
        _fadePropertyOption.Select(selectIdx);
    }

    private void ShowFadeValueRowsFor(ControlFadeProperty property, Cue target)
    {
        HideAllFadeValueRows();
        bool relative = _component.FadeMode == ControlFadeMode.Relative;

        switch (property)
        {
            case ControlFadeProperty.Volume:
                if (_audioFadeCaption != null)
                {
                    _audioFadeCaption.Visible = true;
                    _audioFadeCaption.Text = UiLocalizer.T("Volume:");
                    _audioFadeCaption.TooltipText = UiLocalizer.T("Overall audio volume (dB). Applies to active playback only.");
                }
                if (_audioFadeRow != null)
                    _audioFadeRow.Visible = true;
                SyncVolumeLevelLineEdit(relative);
                break;

            case ControlFadeProperty.RoutingMatrix:
                BuildFadeRoutingMatrix(target);
                break;

            case ControlFadeProperty.Opacity:
                if (_opacityFadeCaption != null)
                    _opacityFadeCaption.Visible = true;
                if (_opacityFadeRow != null)
                    _opacityFadeRow.Visible = true;
                if (_opacityFadeLineEdit != null)
                {
                    string pctText = relative && _component.FadeOpacityPercent > 0
                        ? $"+{_component.FadeOpacityPercent:0.#}%"
                        : $"{_component.FadeOpacityPercent:0.#}%";
                    _opacityFadeLineEdit.Text = pctText;
                    _opacityFadeLineEdit.PlaceholderText = relative ? "±%" : "100%";
                    _opacityFadeLineEdit.TooltipText = relative
                        ? UiLocalizer.T("Relative change in opacity % (clamped to 0…100). Active playback only.")
                        : UiLocalizer.T("Absolute opacity % (0…100). Active playback only.");
                    _opacityFadeLineEdit.Editable = true;
                }
                break;

            case ControlFadeProperty.Pan:
                if (_panFadeCaption != null)
                    _panFadeCaption.Visible = true;
                if (_panFadeRow != null)
                    _panFadeRow.Visible = true;
                SyncPanFadeUiFromComponent();
                break;
        }
    }

    private void SyncVolumeLevelLineEdit(bool relative)
    {
        if (_audioFadeLineEdit == null || _component == null) return;
        string dbText = $"{_component.FadeAudioDb:0.#}dB";
        if (_component.FadeAudioDb > 0)
            dbText = $"+{_component.FadeAudioDb:0.#}dB";
        _audioFadeLineEdit.Text = dbText;
        _audioFadeLineEdit.PlaceholderText = relative ? "±dB" : "0dB";
        _audioFadeLineEdit.TooltipText = relative
            ? UiLocalizer.T("Relative change in dB (result clamped to −60…+12). Active playback only.")
            : UiLocalizer.T("Absolute target level in dB (−60…+12 digital gain). Active playback only.");
        _audioFadeLineEdit.Editable = true;
    }

    /// <summary>
    /// Syncs pan slider + text from the control component without firing handlers.
    /// </summary>
    private void SyncPanFadeUiFromComponent()
    {
        if (_component == null) return;
        _isUpdatingPanUi = true;
        try
        {
            float pan = Mathf.Clamp(_component.FadePan, -1f, 1f);
            bool relative = _component.FadeMode == ControlFadeMode.Relative;

            if (_panFadeSlider != null)
                _panFadeSlider.SetValueNoSignal(Mathf.Round(pan * 100f));

            if (_panFadeLineEdit != null && !_panFadeLineEdit.HasFocus())
            {
                if (relative)
                {
                    if (Mathf.IsZeroApprox(pan))
                        _panFadeLineEdit.Text = "0";
                    else if (pan > 0)
                        _panFadeLineEdit.Text = $"+{UiUtilities.FormatPan(pan)}";
                    else
                        _panFadeLineEdit.Text = $"-{UiUtilities.FormatPan(Mathf.Abs(pan))}";
                }
                else
                {
                    _panFadeLineEdit.Text = UiUtilities.FormatPan(pan);
                }

                _panFadeLineEdit.PlaceholderText = relative ? "±C / ±L50" : "C";
                _panFadeLineEdit.TooltipText = relative
                    ? UiLocalizer.T("Relative pan delta (result clamped to L…R). Active playback only.")
                    : UiLocalizer.T("Absolute pan: C, L50, R100, or −100…100. Active playback only.");
            }
        }
        finally
        {
            _isUpdatingPanUi = false;
        }
    }

    /// <summary>
    /// Builds a full routing matrix like the audio/video inspector.
    /// Yellow cells are multi-selected fade targets (each with its own level).
    /// Other cells show the target cue's current levels; edit one to add it to the fade set.
    /// Clear a yellow cell (empty) to remove it from the set.
    /// </summary>
    private void BuildFadeRoutingMatrix(Cue target)
    {
        if (_matrixContainer == null || _matrixGrid == null || _component == null)
            return;

        foreach (var child in _matrixGrid.GetChildren())
            child.QueueFree();

        var routing = ControlComponent.GetPrimaryRouting(target);
        if (!ControlComponent.HasUsableRouting(routing))
        {
            _matrixContainer.Visible = false;
            return;
        }

        // Drop targets that fall outside the current matrix dimensions.
        if (_component.FadeMatrixCellTargets != null && _component.FadeMatrixCellTargets.Count > 0)
        {
            var invalid = new System.Collections.Generic.List<int>();
            foreach (var key in _component.FadeMatrixCellTargets.Keys)
            {
                ControlComponent.UnpackMatrixCellKey(key, out int inIdx, out int outIdx);
                if (inIdx < 0 || inIdx >= routing.InputChannels ||
                    outIdx < 0 || outIdx >= routing.OutputChannels)
                    invalid.Add(key);
            }
            foreach (int key in invalid)
                _component.FadeMatrixCellTargets.Remove(key);
        }

        _matrixContainer.Visible = true;
        if (_matrixHint != null)
        {
            _matrixHint.Visible = true;
            int n = _component.FadeMatrixCellTargets?.Count ?? 0;
            string modeHint = _component.FadeMode == ControlFadeMode.Relative
                ? "relative Δ dB"
                : "absolute target dB";
            _matrixHint.Text = n == 0
                ? $"Edit cells to add fade targets ({modeHint}). Multiple cells fade together. Clear a yellow cell to remove it."
                : $"{n} cell{(n == 1 ? "" : "s")} selected · {modeHint}. Edit more cells to add; clear a yellow cell to remove.";
        }

        int inCh = routing.InputChannels;
        int outCh = routing.OutputChannels;
        _matrixGrid.Columns = outCh + 1;

        // Header row
        _matrixGrid.AddChild(new Label { Text = "" });
        for (int j = 0; j < outCh; j++)
        {
            string outLabel = routing.OutputLabels != null && j < routing.OutputLabels.Count &&
                              !string.IsNullOrWhiteSpace(routing.OutputLabels[j])
                ? routing.OutputLabels[j]
                : $"Out {j + 1}";
            _matrixGrid.AddChild(new Label { Text = outLabel });
        }

        bool relative = _component.FadeMode == ControlFadeMode.Relative;

        for (int i = 0; i < inCh; i++)
        {
            string inLabel = routing.InputLabels != null && i < routing.InputLabels.Count &&
                             !string.IsNullOrWhiteSpace(routing.InputLabels[i])
                ? routing.InputLabels[i]
                : $"In {i + 1}";
            _matrixGrid.AddChild(new Label { Text = inLabel });

            for (int j = 0; j < outCh; j++)
            {
                var volumeEdit = new LineEdit();
                volumeEdit.CustomMinimumSize = new Vector2(56, 0);
                volumeEdit.Alignment = HorizontalAlignment.Center;

                bool isTargeted = _component.TryGetMatrixCellTargetDb(i, j, out float targetDb);
                if (isTargeted)
                {
                    string dbText = $"{targetDb:0.#}dB";
                    if (relative && targetDb > 0)
                        dbText = $"+{targetDb:0.#}dB";
                    volumeEdit.Text = dbText;
                    volumeEdit.TooltipText = relative
                        ? UiLocalizer.T("Fade target (relative Δ dB). Clear to remove from multi-cell set. Active playback only.")
                        : UiLocalizer.T("Fade target (absolute dB). Clear to remove from multi-cell set. Active playback only.");
                    volumeEdit.Modulate = new Color(1.15f, 1.05f, 0.75f, 1f);
                }
                else
                {
                    float linear = routing.GetVolume(i, j);
                    volumeEdit.Text = linear > 0f
                        ? $"{UiUtilities.LinearToDb(linear)}dB"
                        : string.Empty;
                    volumeEdit.TooltipText =
                        UiLocalizer.T("Cue's current level. Edit to add this cell to the multi-cell fade set.");
                    volumeEdit.Modulate = new Color(0.75f, 0.75f, 0.78f, 1f);
                }

                int row = i;
                int col = j;
                volumeEdit.TextChanged += _ =>
                {
                    if (!_isSyncingUi)
                        _matrixCellEditing = true;
                };
                volumeEdit.TextSubmitted += text =>
                {
                    _matrixCellEditing = true;
                    OnMatrixCellSubmitted(text, volumeEdit, row, col, force: true);
                };
                volumeEdit.FocusExited += () =>
                {
                    if (_isSyncingUi) return;
                    // Only commit on leave when the user actually typed (avoids focus-spam adds).
                    if (!_matrixCellEditing) return;
                    OnMatrixCellSubmitted(volumeEdit.Text, volumeEdit, row, col, force: false);
                };
                if (relative)
                    LineEditDbDragSlider.EnableSignedDb(volumeEdit);
                else
                    // Matrix fade absolute targets are unity-max (routing cells cannot boost).
                    LineEditDbDragSlider.EnableUnityVolume(volumeEdit);

                UiUtilities.WireLineEditEscapeReleasesFocus(volumeEdit);
                _matrixGrid.AddChild(volumeEdit);
            }
        }
    }

    private void OnMatrixCellSubmitted(string text, LineEdit field, int inputCh, int outputCh, bool force)
    {
        if (_isSyncingUi || _component == null || field == null) return;
        if (_component.Action != ControlAction.Fade) return;
        if (_component.FadeProperty != ControlFadeProperty.RoutingMatrix) return;
        if (_globalData?.HistoryManager?.IsRestoring == true) return;

        _matrixCellEditing = false;
        bool wasTargeted = _component.TryGetMatrixCellTargetDb(inputCh, outputCh, out float existingDb);
        string raw = (text ?? string.Empty).Trim();

        // Empty on a targeted cell → remove from multi-cell set.
        // Empty on an untargeted cell is a no-op.
        if (string.IsNullOrWhiteSpace(raw) ||
            string.Equals(raw, "dB", StringComparison.OrdinalIgnoreCase))
        {
            if (!wasTargeted)
            {
                field.ReleaseFocus();
                return;
            }

            RecordHistory("Remove control fade matrix cell");
            _component.RemoveMatrixCellTarget(inputCh, outputCh);
            RefreshFromComponent();
            field.ReleaseFocus();
            return;
        }

        string parseText = raw.Replace("dB", "", StringComparison.OrdinalIgnoreCase).Trim();
        if (parseText.StartsWith('+'))
            parseText = parseText[1..].Trim();

        if (!float.TryParse(parseText, out float db))
        {
            _globalSignals?.EmitSignal(nameof(GlobalSignals.Log),
                "Control card: invalid matrix fade level", (int)LogType.Warning);
            RefreshFromComponent();
            return;
        }

        if (_component.FadeMode == ControlFadeMode.Absolute)
            db = Mathf.Clamp(db, -60f, 0f);

        if (wasTargeted && Math.Abs(existingDb - db) < 1e-4f)
        {
            field.ReleaseFocus();
            return;
        }

        RecordHistory(wasTargeted ? "Edit control fade matrix cell" : "Add control fade matrix cell");
        _component.SetMatrixCellTarget(inputCh, outputCh, db);
        RefreshFromComponent();
        field.ReleaseFocus();
    }

    /// <summary>
    /// Shows Absolute/Relative mode and seek time for Seek controls (contextual to target).
    /// </summary>
    private void RefreshSeekUi(bool isSeek)
    {
        if (_seekTimeCaption != null)
            _seekTimeCaption.Visible = isSeek;
        if (_seekTimeLineEdit != null)
            _seekTimeLineEdit.Visible = isSeek;

        if (!isSeek)
            return;

        if (_modeCaption != null)
            _modeCaption.Visible = true;
        if (_fadeModeOption != null)
        {
            _fadeModeOption.Visible = true;
            _fadeModeOption.Select((int)_component.SeekMode);
        }

        bool hasSeekable = false;
        if (_component.TargetCueId >= 0)
        {
            var target = CueList.FetchCueFromId(_component.TargetCueId);
            hasSeekable = ControlComponent.CueHasSeekableMedia(target);
        }

        bool showNoSeekable = _component.TargetCueId >= 0 && !hasSeekable;
        if (_noFadableLabel != null)
        {
            // Reuse message label for seek-with-no-media (hidden when Fade also active — mutually exclusive).
            _noFadableLabel.Visible = showNoSeekable;
            if (showNoSeekable)
                _noFadableLabel.Text = UiLocalizer.T("No seekable media on target");
        }
        if (_noFadableSpacer != null)
            _noFadableSpacer.Visible = showNoSeekable;

        bool relative = _component.SeekMode == ControlFadeMode.Relative;
        if (_seekTimeLineEdit != null)
        {
            string formatted = UiUtilities.FormatTime(Math.Abs(_component.SeekTimeSeconds));
            if (relative && _component.SeekTimeSeconds < 0)
                _seekTimeLineEdit.Text = $"-{formatted}";
            else if (relative && _component.SeekTimeSeconds > 0)
                _seekTimeLineEdit.Text = $"+{formatted}";
            else
                _seekTimeLineEdit.Text = formatted;

            _seekTimeLineEdit.PlaceholderText = relative ? "±0:00.000" : "0:00.000";
            _seekTimeLineEdit.TooltipText = relative
                ? UiLocalizer.T("Relative seek offset from current playhead (±).")
                : UiLocalizer.T("Absolute media time to seek to.");
            _seekTimeLineEdit.Visible = _component.TargetCueId < 0 || hasSeekable;
        }
        if (_seekTimeCaption != null)
            _seekTimeCaption.Visible = _component.TargetCueId < 0 || hasSeekable;
    }

    private void UpdateResetButtons()
    {
        if (_component == null) return;

        if (_targetResetButton != null)
        {
            bool showTargetReset = _component.Action != ControlAction.TranslateLayer &&
                                   !_component.IsTargetAtDefault;
            _targetResetButton.Visible = showTargetReset;
            if (showTargetReset)
                _targetResetButton.TooltipText = UiLocalizer.T("Clear target");
        }

        if (_fadeResetButton != null)
        {
            bool showFadeReset = !_component.IsFadeAtDefault;
            _fadeResetButton.Visible = showFadeReset;
            if (showFadeReset)
            {
                if (_component.Action == ControlAction.Stop)
                {
                    float sessionDefault = _globalData?.Settings?.StopFadeDuration ?? 0f;
                    _fadeResetButton.TooltipText = UiLocalizer.Tf(
                        "Reset fade to session Stop Fade ({0})",
                        UiUtilities.FormatTime(sessionDefault));
                }
                else if (_component.Action == ControlAction.Go)
                {
                    _fadeResetButton.TooltipText = UiLocalizer.T("Reset fade-in to 0");
                }
                else if (_component.Action == ControlAction.Fade)
                {
                    _fadeResetButton.TooltipText = UiLocalizer.T("Reset fade time to 1s");
                }
                else if (_component.Action == ControlAction.TranslateLayer)
                {
                    _fadeResetButton.TooltipText = UiLocalizer.T("Reset translate time to 0 (instant)");
                }
            }
        }
    }

    private void OnTargetResetPressed()
    {
        if (_component == null || _component.IsTargetAtDefault) return;
        if (_globalData?.HistoryManager?.IsRestoring == true) return;

        RecordHistory("Reset control target");
        _component.ClearTarget();
        RefreshFromComponent();
    }

    private void OnFadeResetPressed()
    {
        if (_component == null || _component.IsFadeAtDefault) return;
        if (_globalData?.HistoryManager?.IsRestoring == true) return;

        if (_component.Action == ControlAction.Stop)
        {
            RecordHistory("Reset control stop fade");
            _component.ResetStopFadeToSessionDefault();
        }
        else if (_component.Action == ControlAction.Go)
        {
            RecordHistory("Reset control go fade-in");
            _component.ResetGoFadeInToDefault();
        }
        else if (_component.Action == ControlAction.Fade)
        {
            RecordHistory("Reset control property fade time");
            _component.ResetPropertyFadeDurationToDefault();
        }
        else if (_component.Action == ControlAction.TranslateLayer)
        {
            RecordHistory("Reset control translate duration");
            _component.ResetTranslateDurationToDefault();
        }
        else
        {
            return;
        }

        RefreshFromComponent();
        NotifyOwnerDurationChanged();
    }

    private void OnFadeSubmitted(string text)
    {
        CommitFade(text);
        _fadeLineEdit.ReleaseFocus();
    }

    private void OnFadeFocusExited()
    {
        if (_fadeEditing)
            CommitFade(_fadeLineEdit.Text);
    }

    private void CommitFade(string text)
    {
        if (_isSyncingUi || _component == null) return;
        if (_component.Action is not (ControlAction.Stop or ControlAction.Go or ControlAction.Fade
                or ControlAction.TranslateLayer))
            return;
        if (_globalData?.HistoryManager?.IsRestoring == true) return;

        if (_component.Action == ControlAction.Go)
        {
            CommitGoFade(text);
            return;
        }

        if (_component.Action == ControlAction.Fade)
        {
            CommitPropertyFadeDuration(text);
            return;
        }

        if (_component.Action == ControlAction.TranslateLayer)
        {
            CommitTranslateDuration(text);
            return;
        }

        CommitStopFade(text);
    }

    private void CommitTranslateDuration(string text)
    {
        text = (text ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(text))
        {
            if (_component.IsTranslateDurationAtDefault)
            {
                _fadeEditing = false;
                RefreshFromComponent();
                return;
            }

            RecordHistory("Edit control translate duration");
            _component.ResetTranslateDurationToDefault();
            _fadeEditing = false;
            RefreshFromComponent();
            NotifyOwnerDurationChanged();
            return;
        }

        string formatted = UiUtilities.ParseAndFormatTime(text, out double seconds, out bool isValid);
        if (!isValid || seconds < 0)
        {
            _globalSignals?.EmitSignal(nameof(GlobalSignals.Log),
                "Control card: invalid translate duration", (int)LogType.Warning);
            RefreshFromComponent();
            return;
        }

        if (Math.Abs(_component.TranslateDuration - seconds) < 1e-9)
        {
            _fadeEditing = false;
            _fadeLineEdit.Text = formatted;
            UpdateResetButtons();
            return;
        }

        RecordHistory("Edit control translate duration");
        _component.TranslateDuration = seconds;
        _fadeEditing = false;
        RefreshFromComponent();
        NotifyOwnerDurationChanged();
    }

    private void CommitPropertyFadeDuration(string text)
    {
        text = (text ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(text))
        {
            if (_component.IsPropertyFadeDurationAtDefault)
            {
                _fadeEditing = false;
                RefreshFromComponent();
                return;
            }

            RecordHistory("Edit control property fade time");
            _component.ResetPropertyFadeDurationToDefault();
            _fadeEditing = false;
            RefreshFromComponent();
            NotifyOwnerDurationChanged();
            return;
        }

        string formatted = UiUtilities.ParseAndFormatTime(text, out double seconds, out bool isValid);
        if (!isValid || seconds < 0)
        {
            _globalSignals?.EmitSignal(nameof(GlobalSignals.Log),
                "Control card: invalid property fade time", (int)LogType.Warning);
            RefreshFromComponent();
            return;
        }

        if (Math.Abs(_component.PropertyFadeDuration - seconds) < 1e-9)
        {
            _fadeEditing = false;
            _fadeLineEdit.Text = formatted;
            UpdateResetButtons();
            return;
        }

        RecordHistory("Edit control property fade time");
        _component.PropertyFadeDuration = seconds;
        _fadeEditing = false;
        RefreshFromComponent();
        NotifyOwnerDurationChanged();
    }

    private void OnFadeModeSelected(long index)
    {
        if (_isSyncingUi || _component == null) return;
        if (_globalData?.HistoryManager?.IsRestoring == true) return;

        var mode = (ControlFadeMode)(int)index;

        if (_component.Action == ControlAction.Fade)
        {
            if (_component.FadeMode == mode) return;

            RecordHistory("Edit control fade mode");
            _component.FadeMode = mode;
            // Relative default delta 0 is clearer when switching modes.
            if (mode == ControlFadeMode.Relative)
            {
                _component.FadeAudioDb = 0f;
                _component.FadeOpacityPercent = 0f;
                _component.FadePan = 0f;
            }
            else
            {
                _component.FadeAudioDb = 0f;
                _component.FadeOpacityPercent = 100f;
                _component.FadePan = 0f;
            }

            RefreshFromComponent();
            return;
        }

        if (_component.Action == ControlAction.Seek)
        {
            if (_component.SeekMode == mode) return;

            RecordHistory("Edit control seek mode");
            _component.SeekMode = mode;
            _component.SeekTimeSeconds = 0;
            RefreshFromComponent();
            return;
        }

        if (_component.Action == ControlAction.TranslateLayer)
        {
            if (_component.TranslateMode == mode) return;

            RecordHistory("Edit control translate mode");
            _component.TranslateMode = mode;
            // Relative defaults to zero deltas; absolute seeds from current layer geometry.
            if (mode == ControlFadeMode.Relative)
            {
                _component.TranslateSizeX = 0;
                _component.TranslateSizeY = 0;
                _component.TranslatePosX = 0;
                _component.TranslatePosY = 0;
            }
            else
            {
                SeedTranslateFromLayer();
            }

            RefreshFromComponent();
        }
    }

    /// <summary>
    /// Absolute/Relative + size/position fields for Translate Layer.
    /// </summary>
    private void RefreshTranslateLayerUi(bool isTranslate)
    {
        if (_layerCaption != null)
            _layerCaption.Visible = isTranslate;
        if (_targetLayerOption != null)
            _targetLayerOption.Visible = isTranslate;

        if (_sizeEnableCaption != null)
            _sizeEnableCaption.Visible = isTranslate;
        if (_sizeEnableRow != null)
            _sizeEnableRow.Visible = isTranslate;
        if (_posEnableCaption != null)
            _posEnableCaption.Visible = isTranslate;
        if (_posEnableRow != null)
            _posEnableRow.Visible = isTranslate;

        if (!isTranslate)
            return;

        if (_modeCaption != null)
            _modeCaption.Visible = true;
        if (_fadeModeOption != null)
        {
            _fadeModeOption.Visible = true;
            _fadeModeOption.Select((int)_component.TranslateMode);
        }

        PopulateTargetLayerOptions();

        if (_sizeEnable != null)
            _sizeEnable.SetPressedNoSignal(_component.TranslateSizeEnabled);
        if (_posEnable != null)
            _posEnable.SetPressedNoSignal(_component.TranslatePositionEnabled);

        bool relative = _component.TranslateMode == ControlFadeMode.Relative;
        if (_sizeXLineEdit != null)
        {
            _sizeXLineEdit.Text = FormatSignedInt(_component.TranslateSizeX, relative);
            _sizeXLineEdit.Editable = _component.TranslateSizeEnabled;
            _sizeXLineEdit.PlaceholderText = relative ? "±W" : "W";
        }
        if (_sizeYLineEdit != null)
        {
            _sizeYLineEdit.Text = FormatSignedInt(_component.TranslateSizeY, relative);
            _sizeYLineEdit.Editable = _component.TranslateSizeEnabled;
            _sizeYLineEdit.PlaceholderText = relative ? "±H" : "H";
        }
        if (_posXLineEdit != null)
        {
            _posXLineEdit.Text = FormatSignedInt(_component.TranslatePosX, relative);
            _posXLineEdit.Editable = _component.TranslatePositionEnabled;
            _posXLineEdit.PlaceholderText = relative ? "±X" : "X";
        }
        if (_posYLineEdit != null)
        {
            _posYLineEdit.Text = FormatSignedInt(_component.TranslatePosY, relative);
            _posYLineEdit.Editable = _component.TranslatePositionEnabled;
            _posYLineEdit.PlaceholderText = relative ? "±Y" : "Y";
        }
    }

    private static string FormatSignedInt(int value, bool relative)
    {
        if (relative && value > 0)
            return $"+{value}";
        return value.ToString();
    }

    private void PopulateTargetLayerOptions()
    {
        if (_targetLayerOption == null || _component == null) return;

        _targetLayerOption.Clear();
        UiLocalizer.AddTranslatedItem(_targetLayerOption, "(select layer)", 0);
        _targetLayerOption.SetItemMetadata(0, -1);

        int selectedIdx = 0;
        int index = 1;
        foreach (var layer in DisplaysManager.Layers.ToList())
        {
            if (layer == null) continue;
            string label = string.IsNullOrWhiteSpace(layer.LayerName)
                ? $"Layer {layer.LayerId}"
                : layer.LayerName;
            _targetLayerOption.AddItem(label, index);
            _targetLayerOption.SetItemMetadata(index, layer.LayerId);
            if (layer.LayerId == _component.TargetLayerId)
                selectedIdx = index;
            index++;
        }

        _targetLayerOption.Select(selectedIdx);
    }

    private void OnTargetLayerSelected(long index)
    {
        if (_isSyncingUi || _component == null) return;
        if (_component.Action != ControlAction.TranslateLayer) return;
        if (_globalData?.HistoryManager?.IsRestoring == true) return;
        if (_targetLayerOption == null) return;

        int layerId = _targetLayerOption.GetItemMetadata((int)index).AsInt32();
        if (layerId == _component.TargetLayerId) return;

        RecordHistory("Edit control target layer");
        _component.TargetLayerId = layerId;

        // Seed absolute values from the selected layer's current geometry.
        if (_component.TranslateMode == ControlFadeMode.Absolute)
            SeedTranslateFromLayer();

        RefreshFromComponent();
    }

    private void SeedTranslateFromLayer()
    {
        if (_component == null || _component.TargetLayerId < 0) return;
        var layer = DisplaysManager.GetLayerById(_component.TargetLayerId);
        if (layer == null) return;

        _component.TranslateSizeX = layer.Size.X;
        _component.TranslateSizeY = layer.Size.Y;
        _component.TranslatePosX = layer.CanvasPosition.X;
        _component.TranslatePosY = layer.CanvasPosition.Y;
    }

    private void OnSizeEnableToggled(bool pressed)
    {
        if (_isSyncingUi || _component == null) return;
        if (_component.Action != ControlAction.TranslateLayer) return;
        if (_globalData?.HistoryManager?.IsRestoring == true) return;
        if (_component.TranslateSizeEnabled == pressed) return;

        RecordHistory("Edit control translate size enable");
        _component.TranslateSizeEnabled = pressed;
        RefreshFromComponent();
    }

    private void OnPosEnableToggled(bool pressed)
    {
        if (_isSyncingUi || _component == null) return;
        if (_component.Action != ControlAction.TranslateLayer) return;
        if (_globalData?.HistoryManager?.IsRestoring == true) return;
        if (_component.TranslatePositionEnabled == pressed) return;

        RecordHistory("Edit control translate position enable");
        _component.TranslatePositionEnabled = pressed;
        RefreshFromComponent();
    }

    private void OnSizeSubmitted(LineEdit field)
    {
        CommitSizeFields();
        field?.ReleaseFocus();
    }

    private void OnPosSubmitted(LineEdit field)
    {
        CommitPosFields();
        field?.ReleaseFocus();
    }

    private void CommitSizeFields()
    {
        if (_isSyncingUi || _component == null) return;
        if (_component.Action != ControlAction.TranslateLayer) return;
        if (_globalData?.HistoryManager?.IsRestoring == true) return;

        if (!TryParseSignedInt(_sizeXLineEdit?.Text, out int sx) ||
            !TryParseSignedInt(_sizeYLineEdit?.Text, out int sy))
        {
            _globalSignals?.EmitSignal(nameof(GlobalSignals.Log),
                "Control card: invalid size values", (int)LogType.Warning);
            _sizeEditing = false;
            RefreshFromComponent();
            return;
        }

        if (_component.TranslateMode == ControlFadeMode.Absolute)
        {
            sx = Math.Max(1, sx);
            sy = Math.Max(1, sy);
        }

        if (sx == _component.TranslateSizeX && sy == _component.TranslateSizeY)
        {
            _sizeEditing = false;
            RefreshFromComponent();
            return;
        }

        RecordHistory("Edit control translate size");
        _component.TranslateSizeX = sx;
        _component.TranslateSizeY = sy;
        _sizeEditing = false;
        RefreshFromComponent();
    }

    private void CommitPosFields()
    {
        if (_isSyncingUi || _component == null) return;
        if (_component.Action != ControlAction.TranslateLayer) return;
        if (_globalData?.HistoryManager?.IsRestoring == true) return;

        if (!TryParseSignedInt(_posXLineEdit?.Text, out int px) ||
            !TryParseSignedInt(_posYLineEdit?.Text, out int py))
        {
            _globalSignals?.EmitSignal(nameof(GlobalSignals.Log),
                "Control card: invalid position values", (int)LogType.Warning);
            _posEditing = false;
            RefreshFromComponent();
            return;
        }

        if (px == _component.TranslatePosX && py == _component.TranslatePosY)
        {
            _posEditing = false;
            RefreshFromComponent();
            return;
        }

        RecordHistory("Edit control translate position");
        _component.TranslatePosX = px;
        _component.TranslatePosY = py;
        _posEditing = false;
        RefreshFromComponent();
    }

    private static bool TryParseSignedInt(string text, out int value)
    {
        value = 0;
        text = (text ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(text))
            return true; // treat blank as 0
        if (text.StartsWith('+'))
            text = text[1..];
        return int.TryParse(text, out value);
    }

    private void OnSeekTimeSubmitted(string text)
    {
        CommitSeekTime(text);
        _seekTimeLineEdit?.ReleaseFocus();
    }

    private void OnSeekTimeFocusExited()
    {
        if (_seekTimeEditing)
            CommitSeekTime(_seekTimeLineEdit?.Text ?? string.Empty);
    }

    private void CommitSeekTime(string text)
    {
        if (_isSyncingUi || _component == null) return;
        if (_component.Action != ControlAction.Seek) return;
        if (_globalData?.HistoryManager?.IsRestoring == true) return;

        text = (text ?? string.Empty).Trim();
        bool negative = text.StartsWith('-');
        if (text.StartsWith('+') || text.StartsWith('-'))
            text = text[1..].Trim();

        if (string.IsNullOrEmpty(text))
        {
            if (Math.Abs(_component.SeekTimeSeconds) < 1e-9)
            {
                _seekTimeEditing = false;
                RefreshFromComponent();
                return;
            }

            RecordHistory("Edit control seek time");
            _component.SeekTimeSeconds = 0;
            _seekTimeEditing = false;
            RefreshFromComponent();
            return;
        }

        string formatted = UiUtilities.ParseAndFormatTime(text, out double seconds, out bool isValid);
        if (!isValid || seconds < 0)
        {
            _globalSignals?.EmitSignal(nameof(GlobalSignals.Log),
                "Control card: invalid seek time", (int)LogType.Warning);
            RefreshFromComponent();
            return;
        }

        if (negative)
            seconds = -seconds;

        // Absolute seeks must be non-negative.
        if (_component.SeekMode == ControlFadeMode.Absolute && seconds < 0)
            seconds = 0;

        if (Math.Abs(_component.SeekTimeSeconds - seconds) < 1e-9)
        {
            _seekTimeEditing = false;
            RefreshFromComponent();
            return;
        }

        RecordHistory("Edit control seek time");
        _component.SeekTimeSeconds = seconds;
        _seekTimeEditing = false;
        RefreshFromComponent();
    }

    private void OnFadePropertySelected(long index)
    {
        if (_isSyncingUi || _component == null) return;
        if (_component.Action != ControlAction.Fade) return;
        if (_globalData?.HistoryManager?.IsRestoring == true) return;
        if (_fadePropertyOption == null) return;

        var property = (ControlFadeProperty)_fadePropertyOption.GetItemMetadata((int)index).AsInt32();
        if (_component.FadeProperty == property) return;

        RecordHistory("Edit control fade property");
        _component.FadeProperty = property;
        _component.FadeAudioVolumeEnabled = property == ControlFadeProperty.Volume;
        _component.FadeVideoOpacityEnabled = property == ControlFadeProperty.Opacity;

        // Sensible defaults when switching property under absolute mode.
        if (_component.FadeMode == ControlFadeMode.Absolute)
        {
            switch (property)
            {
                case ControlFadeProperty.Volume:
                case ControlFadeProperty.RoutingMatrix:
                    // Keep current dB if already set; otherwise full / unity.
                    break;
                case ControlFadeProperty.Opacity:
                    if (Math.Abs(_component.FadeOpacityPercent) < 1e-4f)
                        _component.FadeOpacityPercent = 100f;
                    break;
                case ControlFadeProperty.Pan:
                    // Leave pan as-is (0 = center is a fine absolute default).
                    break;
            }
        }

        RefreshFromComponent();
    }

    private void OnPanFadeSliderChanged(double value)
    {
        if (_isUpdatingPanUi || _isSyncingUi || _component == null) return;
        if (_component.Action != ControlAction.Fade) return;
        if (_component.FadeProperty != ControlFadeProperty.Pan) return;
        if (_globalData?.HistoryManager?.IsRestoring == true) return;

        float pan = Mathf.Clamp((float)value / 100f, -1f, 1f);
        if (Math.Abs(_component.FadePan - pan) < 1e-6f) return;

        // Coalesce continuous drag into one undo step.
        int cueId = GetOwnerCueId();
        if (cueId >= 0)
        {
            string key = $"cue:{cueId}:control-fade-pan";
            InspectorMultiEditSupport.RecordBeforeEditById(
                _globalData, multiHistory: false, cueId, "Edit control fade pan", coalesceKey: key);
        }

        _component.FadePan = pan;
        _isUpdatingPanUi = true;
        try
        {
            if (_panFadeLineEdit != null && !_panFadeLineEdit.HasFocus())
            {
                bool relative = _component.FadeMode == ControlFadeMode.Relative;
                if (relative)
                {
                    if (Mathf.IsZeroApprox(pan))
                        _panFadeLineEdit.Text = "0";
                    else if (pan > 0)
                        _panFadeLineEdit.Text = $"+{UiUtilities.FormatPan(pan)}";
                    else
                        _panFadeLineEdit.Text = $"-{UiUtilities.FormatPan(Mathf.Abs(pan))}";
                }
                else
                {
                    _panFadeLineEdit.Text = UiUtilities.FormatPan(pan);
                }
            }
        }
        finally
        {
            _isUpdatingPanUi = false;
        }
    }

    private void OnPanFadeSliderDragEnded(bool valueChanged)
    {
        if (_component == null) return;
        int cueId = GetOwnerCueId();
        if (cueId >= 0)
        {
            string key = $"cue:{cueId}:control-fade-pan";
            InspectorMultiEditSupport.EndCoalesce(_globalData, multiHistory: false, key, key);
        }
        if (valueChanged)
            SyncPanFadeUiFromComponent();
    }

    private void OnAudioFadeSubmitted(string text)
    {
        CommitAudioFade(text);
        _audioFadeLineEdit?.ReleaseFocus();
    }

    private void OnAudioFadeFocusExited()
    {
        if (_audioFadeEditing)
            CommitAudioFade(_audioFadeLineEdit?.Text ?? string.Empty);
    }

    private void OnOpacityFadeSubmitted(string text)
    {
        CommitOpacityFade(text);
        _opacityFadeLineEdit?.ReleaseFocus();
    }

    private void OnOpacityFadeFocusExited()
    {
        if (_opacityFadeEditing)
            CommitOpacityFade(_opacityFadeLineEdit?.Text ?? string.Empty);
    }

    private void OnPanFadeSubmitted(string text)
    {
        CommitPanFade(text);
        _panFadeLineEdit?.ReleaseFocus();
    }

    private void OnPanFadeFocusExited()
    {
        if (_panFadeEditing)
            CommitPanFade(_panFadeLineEdit?.Text ?? string.Empty);
    }

    private void CommitAudioFade(string text)
    {
        if (_isSyncingUi || _component == null) return;
        if (_component.Action != ControlAction.Fade) return;
        if (_component.FadeProperty is not (ControlFadeProperty.Volume or ControlFadeProperty.RoutingMatrix))
            return;
        if (_globalData?.HistoryManager?.IsRestoring == true) return;

        text = (text ?? string.Empty).Replace("dB", "", StringComparison.OrdinalIgnoreCase).Trim();
        if (text.StartsWith('+'))
            text = text[1..].Trim();
        if (!float.TryParse(text, out float db))
        {
            _globalSignals?.EmitSignal(nameof(GlobalSignals.Log),
                "Control card: invalid fade level dB", (int)LogType.Warning);
            RefreshFromComponent();
            return;
        }

        if (_component.FadeMode == ControlFadeMode.Absolute)
        {
            // Component volume fades allow digital gain; routing-matrix targets stay unity-max.
            if (_component.FadeProperty == ControlFadeProperty.RoutingMatrix)
                db = Mathf.Clamp(db, UiUtilities.MinVolumeDb, 0f);
            else
                db = Mathf.Clamp(db, UiUtilities.MinVolumeDb, UiUtilities.MaxComponentGainDb);
        }

        if (Math.Abs(_component.FadeAudioDb - db) < 1e-4f)
        {
            _audioFadeEditing = false;
            RefreshFromComponent();
            return;
        }

        RecordHistory(_component.FadeProperty == ControlFadeProperty.RoutingMatrix
            ? "Edit control fade matrix level"
            : "Edit control fade volume");
        _component.FadeAudioDb = db;
        _audioFadeEditing = false;
        RefreshFromComponent();
    }

    private void CommitOpacityFade(string text)
    {
        if (_isSyncingUi || _component == null) return;
        if (_component.Action != ControlAction.Fade) return;
        if (_component.FadeProperty != ControlFadeProperty.Opacity) return;
        if (_globalData?.HistoryManager?.IsRestoring == true) return;

        text = (text ?? string.Empty).Replace("%", "", StringComparison.Ordinal).Trim();
        if (text.StartsWith('+'))
            text = text[1..].Trim();
        if (!float.TryParse(text, out float pct))
        {
            _globalSignals?.EmitSignal(nameof(GlobalSignals.Log),
                "Control card: invalid opacity fade %", (int)LogType.Warning);
            RefreshFromComponent();
            return;
        }

        if (_component.FadeMode == ControlFadeMode.Absolute)
            pct = Mathf.Clamp(pct, 0f, 100f);

        if (Math.Abs(_component.FadeOpacityPercent - pct) < 1e-4f)
        {
            _opacityFadeEditing = false;
            RefreshFromComponent();
            return;
        }

        RecordHistory("Edit control fade opacity");
        _component.FadeOpacityPercent = pct;
        _opacityFadeEditing = false;
        RefreshFromComponent();
    }

    private void CommitPanFade(string text)
    {
        if (_isSyncingUi || _component == null) return;
        if (_component.Action != ControlAction.Fade) return;
        if (_component.FadeProperty != ControlFadeProperty.Pan) return;
        if (_globalData?.HistoryManager?.IsRestoring == true) return;

        text = (text ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(text))
        {
            if (Math.Abs(_component.FadePan) < 1e-6f)
            {
                _panFadeEditing = false;
                RefreshFromComponent();
                return;
            }

            RecordHistory("Edit control fade pan");
            _component.FadePan = 0f;
            _panFadeEditing = false;
            RefreshFromComponent();
            return;
        }

        // Allow relative signed values: +L50 / -R25 / +0.5
        bool negative = text.StartsWith('-');
        if (text.StartsWith('+') || text.StartsWith('-'))
            text = text[1..].Trim();

        if (!UiUtilities.TryParsePan(text, out float pan) &&
            !float.TryParse(text, out pan))
        {
            // Numeric −100…100 without L/R prefix
            _globalSignals?.EmitSignal(nameof(GlobalSignals.Log),
                "Control card: invalid pan value", (int)LogType.Warning);
            RefreshFromComponent();
            return;
        }

        // If user typed a bare number outside −1…1, treat as percent (−100…100).
        if (Math.Abs(pan) > 1f + 1e-4f)
            pan = Mathf.Clamp(pan, -100f, 100f) / 100f;

        if (negative)
            pan = -Mathf.Abs(pan);

        if (_component.FadeMode == ControlFadeMode.Absolute)
            pan = Mathf.Clamp(pan, -1f, 1f);

        if (Math.Abs(_component.FadePan - pan) < 1e-6f)
        {
            _panFadeEditing = false;
            RefreshFromComponent();
            return;
        }

        RecordHistory("Edit control fade pan");
        _component.FadePan = pan;
        _panFadeEditing = false;
        RefreshFromComponent();
    }

    private void CommitGoFade(string text)
    {
        text = (text ?? string.Empty).Trim();

        // Blank → default 0.
        if (string.IsNullOrEmpty(text))
        {
            if (_component.IsGoFadeAtDefault)
            {
                _fadeEditing = false;
                RefreshFromComponent();
                return;
            }

            RecordHistory("Edit control go fade-in");
            _component.ResetGoFadeInToDefault();
            _fadeEditing = false;
            RefreshFromComponent();
            NotifyOwnerDurationChanged();
            return;
        }

        string formatted = UiUtilities.ParseAndFormatTime(text, out double seconds, out bool isValid);
        if (!isValid || seconds < 0)
        {
            _globalSignals?.EmitSignal(nameof(GlobalSignals.Log),
                "Control card: invalid go fade-in time", (int)LogType.Warning);
            RefreshFromComponent();
            return;
        }

        if (Math.Abs(_component.GoFadeInDuration - seconds) < 1e-9)
        {
            _fadeEditing = false;
            _fadeLineEdit.Text = formatted;
            UpdateResetButtons();
            return;
        }

        RecordHistory("Edit control go fade-in");
        _component.GoFadeInDuration = seconds;
        _fadeEditing = false;
        RefreshFromComponent();
        NotifyOwnerDurationChanged();
    }

    private void CommitStopFade(string text)
    {
        text = (text ?? string.Empty).Trim();
        float sessionDefault = _globalData?.Settings?.StopFadeDuration ?? 0f;

        // Blank → back to session default.
        if (string.IsNullOrEmpty(text))
        {
            if (_component.IsStopFadeAtDefault)
            {
                _fadeEditing = false;
                RefreshFromComponent();
                return;
            }

            RecordHistory("Edit control stop fade");
            _component.ResetStopFadeToSessionDefault();
            _fadeEditing = false;
            RefreshFromComponent();
            NotifyOwnerDurationChanged();
            return;
        }

        string formatted = UiUtilities.ParseAndFormatTime(text, out double seconds, out bool isValid);
        if (!isValid || seconds < 0)
        {
            _globalSignals?.EmitSignal(nameof(GlobalSignals.Log),
                "Control card: invalid stop fade time", (int)LogType.Warning);
            RefreshFromComponent();
            return;
        }

        // Match session default → stay on / return to session-default mode.
        if (Math.Abs(seconds - sessionDefault) < 1e-6)
        {
            if (_component.IsStopFadeAtDefault)
            {
                _fadeEditing = false;
                RefreshFromComponent();
                return;
            }

            RecordHistory("Edit control stop fade");
            _component.ResetStopFadeToSessionDefault();
            _fadeEditing = false;
            RefreshFromComponent();
            NotifyOwnerDurationChanged();
            return;
        }

        if (!_component.StopFadeUsesSessionDefault &&
            Math.Abs(_component.StopFadeDuration - seconds) < 1e-9)
        {
            _fadeEditing = false;
            _fadeLineEdit.Text = formatted;
            return;
        }

        RecordHistory("Edit control stop fade");
        _component.StopFadeUsesSessionDefault = false;
        _component.StopFadeDuration = seconds;
        _fadeEditing = false;
        RefreshFromComponent();
        NotifyOwnerDurationChanged();
    }

    private void OnDeletePressed()
    {
        EndPickMode(applyTarget: false);
        _inspector?.RemoveComponent(_component);
        QueueFree();
    }

    private void OnIdSubmitted(string text)
    {
        CommitId(text);
        _idLineEdit.ReleaseFocus();
    }

    private void OnIdFocusExited()
    {
        if (_idEditing)
            CommitId(_idLineEdit.Text);
    }

    private void OnNumberSubmitted(string text)
    {
        CommitNumber(text);
        _numberLineEdit.ReleaseFocus();
    }

    private void OnNumberFocusExited()
    {
        if (_numberEditing)
            CommitNumber(_numberLineEdit.Text);
    }

    private void CommitId(string text)
    {
        if (_isSyncingUi || _component == null) return;
        if (_globalData?.HistoryManager?.IsRestoring == true) return;

        text = (text ?? string.Empty).Trim();
        int newId = -1;
        if (!string.IsNullOrEmpty(text))
        {
            if (!int.TryParse(text, out newId) || newId < 0)
            {
                _globalSignals?.EmitSignal(nameof(GlobalSignals.Log),
                    "Control card: invalid cue id", (int)LogType.Warning);
                RefreshFromComponent();
                return;
            }
        }

        if (newId == _component.TargetCueId &&
            (newId >= 0 || string.IsNullOrEmpty(_component.TargetCueNum)))
        {
            _idEditing = false;
            return;
        }

        // Self-target blocked for transport controls; Fade may target self (level changes).
        if (newId >= 0 && IsSelfTarget(newId) && _component.Action != ControlAction.Fade)
        {
            RejectSelfTarget();
            _idEditing = false;
            RefreshFromComponent();
            return;
        }

        RecordHistory("Edit control target id");

        if (newId < 0)
        {
            _component.TargetCueId = -1;
            _component.TargetCueNum = string.Empty;
        }
        else
        {
            var cue = CueList.FetchCueFromId(newId);
            if (cue == null)
            {
                _component.TargetCueId = newId;
                // Keep number only if it still matches; otherwise clear for honesty.
                if (!string.IsNullOrEmpty(_component.TargetCueNum))
                {
                    var byNum = CueList.FetchCueFromCueNum(_component.TargetCueNum);
                    if (byNum == null || byNum.Id != newId)
                        _component.TargetCueNum = string.Empty;
                }
                _globalSignals?.EmitSignal(nameof(GlobalSignals.Log),
                    $"Control card: no cue with id {newId}", (int)LogType.Warning);
            }
            else
            {
                _component.TargetCueId = cue.Id;
                _component.TargetCueNum = cue.CueNum ?? string.Empty;
                TryAutoRenameOwner();
            }
        }

        _idEditing = false;
        RefreshFromComponent();
        UpdateResetButtons();
    }

    private void CommitNumber(string text)
    {
        if (_isSyncingUi || _component == null) return;
        if (_globalData?.HistoryManager?.IsRestoring == true) return;

        text = (text ?? string.Empty).Trim();

        if (string.Equals(_component.TargetCueNum ?? string.Empty, text, StringComparison.Ordinal) &&
            (string.IsNullOrEmpty(text) || _component.TargetCueId >= 0))
        {
            // Still resolve in case id was stale.
            if (!string.IsNullOrEmpty(text))
            {
                var existing = CueList.FetchCueFromCueNum(text);
                if (existing != null && existing.Id == _component.TargetCueId)
                {
                    _numberEditing = false;
                    return;
                }
            }
            else if (_component.TargetCueId < 0)
            {
                _numberEditing = false;
                return;
            }
        }

        if (!string.IsNullOrEmpty(text))
        {
            var resolved = CueList.FetchCueFromCueNum(text);
            if (resolved != null && IsSelfTarget(resolved.Id) &&
                _component.Action != ControlAction.Fade)
            {
                RejectSelfTarget();
                _numberEditing = false;
                RefreshFromComponent();
                return;
            }
        }

        RecordHistory("Edit control target number");

        if (string.IsNullOrEmpty(text))
        {
            _component.TargetCueId = -1;
            _component.TargetCueNum = string.Empty;
        }
        else
        {
            var cue = CueList.FetchCueFromCueNum(text);
            if (cue == null)
            {
                _component.TargetCueNum = text;
                _component.TargetCueId = -1;
                _globalSignals?.EmitSignal(nameof(GlobalSignals.Log),
                    $"Control card: no cue with number \"{text}\"", (int)LogType.Warning);
            }
            else
            {
                _component.TargetCueId = cue.Id;
                _component.TargetCueNum = cue.CueNum ?? text;
                TryAutoRenameOwner();
            }
        }

        _numberEditing = false;
        RefreshFromComponent();
        UpdateResetButtons();
    }

    // =========================
    // Pick-target interaction
    // =========================

    /// <summary>
    /// Starts pick on left-press. Accepts the event so BaseButton does not latch pressed;
    /// mouse-up is owned by <see cref="_Input"/> during pick mode.
    /// </summary>
    private void OnPickTargetButtonGuiInput(InputEvent @event)
    {
        if (@event is not InputEventMouseButton mb)
            return;
        if (mb.ButtonIndex != MouseButton.Left || !mb.Pressed)
            return;

        // Prevent BaseButton internal press-attempt (would stick if mouse-up is handled globally).
        AcceptEvent();
        ReleasePickButton();

        if (_component == null) return;
        if (_globalData?.HistoryManager?.IsRestoring == true) return;
        if (_pickActive) return;

        StartPickMode();
    }

    /// <summary>
    /// Clears residual pressed/focus state on the pick button after a pick session
    /// (mirrors <see cref="ShellBar.ReleaseDragGrabber"/>).
    /// </summary>
    private void ReleasePickButton()
    {
        if (_pickTargetButton == null || !IsInstanceValid(_pickTargetButton))
            return;

        _pickTargetButton.KeepPressedOutside = false;
        _pickTargetButton.SetPressedNoSignal(false);
        _pickTargetButton.ButtonPressed = false;
        if (_pickTargetButton.HasFocus())
            _pickTargetButton.ReleaseFocus();
    }

    private void StartPickMode()
    {
        if (_pickActive) return;

        _pickActive = true;
        SetProcessInput(true);
        SetProcess(true);

        EnsurePickBadge();
        if (_pickLayer != null)
            _pickLayer.Visible = true;

        // Crosshair / target cursor for the hold duration.
        try
        {
            DisplayServer.CursorSetShape(DisplayServer.CursorShape.Cross);
        }
        catch (Exception ex)
        {
            GD.Print($"ControlComponentCard:StartPickMode - Cursor shape: {ex.Message}");
        }

        UpdatePickCursorAndBadge();
        _globalSignals?.EmitSignal(nameof(GlobalSignals.Log),
            "Control: pick a cue — release over a shell (Esc to cancel)", (int)LogType.Info);
    }

    /// <summary>
    /// Ends pick mode. When <paramref name="applyTarget"/> is true, assigns the shell under the mouse.
    /// </summary>
    private void EndPickMode(bool applyTarget)
    {
        if (!_pickActive && _pickLayer == null && _pickHoverShell == null)
            return;

        ShellBar targetShell = null;
        if (applyTarget && _pickActive)
            targetShell = FindShellBarUnderMouse();

        ClearPickHoverHighlight();

        _pickActive = false;
        SetProcessInput(false);
        SetProcess(false);

        // Restore default cursor.
        try
        {
            DisplayServer.CursorSetShape(DisplayServer.CursorShape.Arrow);
        }
        catch
        {
            // Best-effort; some platforms ignore cursor shape.
        }

        // Critical: clear latched BaseButton state (mouse-up never reached the button).
        ReleasePickButton();

        if (_pickLayer != null && IsInstanceValid(_pickLayer))
        {
            _pickLayer.QueueFree();
            _pickLayer = null;
            _pickBadge = null;
            _pickBadgeLabel = null;
            _pickBadgeIcon = null;
        }

        if (applyTarget && targetShell != null)
            ApplyTargetFromShell(targetShell);
    }

    private void ApplyTargetFromShell(ShellBar shell)
    {
        if (shell == null || _component == null) return;

        int cueId = shell.CueId;
        if (IsSelfTarget(cueId) && _component.Action != ControlAction.Fade)
        {
            RejectSelfTarget();
            return;
        }

        var cue = CueList.FetchCueFromId(cueId);
        if (cue == null)
        {
            _globalSignals?.EmitSignal(nameof(GlobalSignals.Log),
                $"Control pick: cue id {cueId} not found", (int)LogType.Warning);
            return;
        }

        if (cue.Id == _component.TargetCueId &&
            string.Equals(_component.TargetCueNum ?? string.Empty, cue.CueNum ?? string.Empty, StringComparison.Ordinal))
        {
            RefreshFromComponent();
            return;
        }

        RecordHistory("Pick control target");
        _component.TargetCueId = cue.Id;
        _component.TargetCueNum = cue.CueNum ?? string.Empty;
        TryAutoRenameOwner();
        RefreshFromComponent();

        _globalSignals?.EmitSignal(nameof(GlobalSignals.Log),
            $"Control pick: target → #{_component.TargetCueNum} \"{cue.Name}\"", (int)LogType.Info);
    }

    private void EnsurePickBadge()
    {
        if (_pickLayer != null && IsInstanceValid(_pickLayer))
            return;

        _pickLayer = new CanvasLayer
        {
            Name = "ControlPickTargetLayer",
            Layer = 100
        };

        _pickBadge = new PanelContainer
        {
            MouseFilter = MouseFilterEnum.Ignore,
            Name = "PickBadge"
        };

        var style = new StyleBoxFlat
        {
            BgColor = new Color(0.08f, 0.08f, 0.1f, 0.92f),
            BorderColor = new Color(0.92f, 0.44f, 0.01f, 1f),
            BorderWidthLeft = 1,
            BorderWidthTop = 1,
            BorderWidthRight = 1,
            BorderWidthBottom = 1,
            CornerRadiusTopLeft = 4,
            CornerRadiusTopRight = 4,
            CornerRadiusBottomRight = 4,
            CornerRadiusBottomLeft = 4,
            ContentMarginLeft = 6,
            ContentMarginTop = 4,
            ContentMarginRight = 8,
            ContentMarginBottom = 4
        };
        _pickBadge.AddThemeStyleboxOverride("panel", style);

        var row = new HBoxContainer
        {
            MouseFilter = MouseFilterEnum.Ignore,
            // Small gap between cursor icon and label.
        };
        row.AddThemeConstantOverride("separation", 6);

        _pickBadgeIcon = new TextureRect
        {
            MouseFilter = MouseFilterEnum.Ignore,
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            CustomMinimumSize = new Vector2(16, 16)
        };
        var icon = GetThemeIcon("Target", "AtlasIcons");
        if (icon != null)
            _pickBadgeIcon.Texture = icon;
        row.AddChild(_pickBadgeIcon);

        _pickBadgeLabel = new Label
        {
            MouseFilter = MouseFilterEnum.Ignore,
            Text = "Pick cue…"
        };
        _pickBadgeLabel.AddThemeColorOverride("font_color", new Color(0.95f, 0.95f, 0.95f, 1f));
        _pickBadgeLabel.AddThemeFontSizeOverride("font_size", 12);
        row.AddChild(_pickBadgeLabel);

        _pickBadge.AddChild(row);
        _pickLayer.AddChild(_pickBadge);
        GetTree().Root.AddChild(_pickLayer);
    }

    private void UpdatePickCursorAndBadge()
    {
        // Keep crosshair sticky while over other controls that set their own cursor.
        try
        {
            DisplayServer.CursorSetShape(DisplayServer.CursorShape.Cross);
        }
        catch
        {
            // ignore
        }

        var shell = FindShellBarUnderMouse();
        SetPickHoverHighlight(shell);

        if (_pickBadge == null || !IsInstanceValid(_pickBadge))
            return;

        Vector2 mouse = GetViewport().GetMousePosition();
        // Offset so the badge sits just below-right of the cursor tip.
        _pickBadge.Position = mouse + new Vector2(18, 18);

        if (shell != null)
        {
            if (IsSelfTarget(shell.CueId) && _component?.Action != ControlAction.Fade)
            {
                _pickBadgeLabel.Text = UiLocalizer.T("Cannot target self");
            }
            else
            {
                var cue = CueList.FetchCueFromId(shell.CueId);
                string num = cue?.CueNum ?? shell.CueId.ToString();
                string name = cue?.Name ?? "(unknown)";
                string action = _component != null
                    ? ControlComponent.GetActionDisplayName(_component.Action)
                    : "Target";
                _pickBadgeLabel.Text = $"{action} → #{num}  {name}";
            }
        }
        else
        {
            _pickBadgeLabel.Text = UiLocalizer.T("Pick cue…");
        }
    }

    private void SetPickHoverHighlight(ShellBar shell)
    {
        if (_pickHoverShell == shell)
            return;

        ClearPickHoverHighlight();

        if (shell == null || !IsInstanceValid(shell))
            return;

        _pickHoverShell = shell;
        _pickHoverRestoreModulate = shell.Modulate;
        // Warm tint so the drop target is obvious under the crosshair.
        shell.Modulate = new Color(1.15f, 1.05f, 0.85f, 1f);
    }

    private void ClearPickHoverHighlight()
    {
        if (_pickHoverShell != null && IsInstanceValid(_pickHoverShell))
            _pickHoverShell.Modulate = _pickHoverRestoreModulate;
        _pickHoverShell = null;
        _pickHoverRestoreModulate = Colors.White;
    }

    /// <summary>
    /// Walks from the control under the mouse up the parent chain to find a <see cref="ShellBar"/>.
    /// </summary>
    private ShellBar FindShellBarUnderMouse()
    {
        var vp = GetViewport();
        if (vp == null) return null;

        Control hovered = vp.GuiGetHoveredControl();
        while (hovered != null)
        {
            if (hovered is ShellBar shell)
                return shell;
            hovered = hovered.GetParent() as Control;
        }

        return null;
    }

    /// <summary>
    /// Id of the cue currently being edited (the control component's owner).
    /// </summary>
    private int GetOwnerCueId() => _globalData?.FocusedCue ?? -1;

    /// <summary>
    /// True when <paramref name="cueId"/> is the owning control cue (self-target).
    /// </summary>
    private bool IsSelfTarget(int cueId)
    {
        int ownerId = GetOwnerCueId();
        return ownerId >= 0 && cueId == ownerId;
    }

    /// <summary>
    /// Logs a user-facing warning that self-targeting is not allowed.
    /// </summary>
    private void RejectSelfTarget()
    {
        _globalSignals?.EmitSignal(nameof(GlobalSignals.Log),
            "Control: a cue cannot target itself", (int)LogType.Warning);
    }

    /// <summary>
    /// Renames the focused cue when it still has a placeholder name and the target resolved.
    /// History was already recorded for the target edit, so the rename is included in that step.
    /// </summary>
    private void TryAutoRenameOwner()
    {
        if (_component == null) return;

        int cueId = GetOwnerCueId();
        var owner = cueId >= 0 ? CueList.FetchCueFromId(cueId) : null;
        if (owner == null) return;

        if (_component.TryAutoRenameOwnerCue(owner))
        {
            // Keep shell inspector / any listeners that don't bind NameChanged in sync.
            _globalSignals?.EmitSignal(nameof(GlobalSignals.SyncShellInspector));
            _globalSignals?.EmitSignal(nameof(GlobalSignals.Log),
                $"Control: renamed cue to \"{owner.Name}\"", (int)LogType.Info);
        }
    }

    private void RecordHistory(string description)
    {
        int cueId = GetOwnerCueId();
        if (cueId < 0) return;
        InspectorMultiEditSupport.RecordBeforeEditById(
            _globalData, multiHistory: false, cueId, description);
    }

    /// <summary>
    /// Recalculates the owning cue's duration after a timed control field changes
    /// (property fade, stop fade, GO fade-in, translate duration).
    /// </summary>
    private void NotifyOwnerDurationChanged()
    {
        int cueId = GetOwnerCueId();
        var owner = cueId >= 0 ? CueList.FetchCueFromId(cueId) : null;
        if (owner == null) return;
        try
        {
            owner.CalculateTotalDuration();
        }
        catch
        {
            /* best-effort */
        }
        _globalSignals?.EmitSignal(nameof(GlobalSignals.UpdateShellBar), cueId);
        _globalSignals?.EmitSignal(nameof(GlobalSignals.SyncShellInspector));
    }
}
