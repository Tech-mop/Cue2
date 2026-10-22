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
/// Fade cards pick exactly one property (volume, play rate, pitch, pan, opacity, or a routing-matrix cell).
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
    private Label _rateFadeCaption;
    private Control _rateFadeRow;
    private LineEdit _rateFadeLineEdit;
    private Label _pitchFadeCaption;
    private Control _pitchFadeRow;
    private LineEdit _pitchFadeLineEdit;
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
    private bool _rateFadeEditing;
    private bool _pitchFadeEditing;
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
        _rateFadeCaption = GetNodeOrNull<Label>("%RateFadeCaption");
        _rateFadeRow = GetNodeOrNull<Control>("%RateFadeRow");
        _rateFadeLineEdit = GetNodeOrNull<LineEdit>("%RateFadeLineEdit");
        _pitchFadeCaption = GetNodeOrNull<Label>("%PitchFadeCaption");
        _pitchFadeRow = GetNodeOrNull<Control>("%PitchFadeRow");
        _pitchFadeLineEdit = GetNodeOrNull<LineEdit>("%PitchFadeLineEdit");
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

        if (_rateFadeLineEdit != null)
        {
            _rateFadeLineEdit.TextSubmitted += OnRateFadeSubmitted;
            _rateFadeLineEdit.FocusExited += OnRateFadeFocusExited;
            _rateFadeLineEdit.TextChanged += _ => _rateFadeEditing = true;
        }

        if (_pitchFadeLineEdit != null)
        {
            _pitchFadeLineEdit.TextSubmitted += OnPitchFadeSubmitted;
            _pitchFadeLineEdit.FocusExited += OnPitchFadeFocusExited;
            _pitchFadeLineEdit.TextChanged += _ => _pitchFadeEditing = true;
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
        UiUtilities.WireLineEditEscapeReleasesFocus(_rateFadeLineEdit);
        UiUtilities.WireLineEditEscapeReleasesFocus(_pitchFadeLineEdit);
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
        if (_rateFadeCaption != null) _rateFadeCaption.Visible = false;
        if (_rateFadeRow != null) _rateFadeRow.Visible = false;
        if (_pitchFadeCaption != null) _pitchFadeCaption.Visible = false;
        if (_pitchFadeRow != null) _pitchFadeRow.Visible = false;
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

            case ControlFadeProperty.PlayRate:
                if (_rateFadeCaption != null)
                {
                    _rateFadeCaption.Visible = true;
                    _rateFadeCaption.Text = UiLocalizer.T("Play Rate:");
                }
                if (_rateFadeRow != null)
                    _rateFadeRow.Visible = true;
                SyncRateFadeLineEdit(relative);
                break;

            case ControlFadeProperty.Pitch:
                if (_pitchFadeCaption != null)
                {
                    _pitchFadeCaption.Visible = true;
                    _pitchFadeCaption.Text = UiLocalizer.T("Pitch:");
                }
                if (_pitchFadeRow != null)
                    _pitchFadeRow.Visible = true;
                SyncPitchFadeLineEdit(relative);
                break;
        }
    }

    private void SyncRateFadeLineEdit(bool relative)
    {
        if (_rateFadeLineEdit == null || _component == null) return;
        float rate = _component.FadePlayRate;
        string text = relative && rate > 0
            ? $"+{rate:0.###}"
            : rate.ToString("0.###");
        _rateFadeLineEdit.Text = text;
        _rateFadeLineEdit.PlaceholderText = relative ? "±" : "1";
        _rateFadeLineEdit.TooltipText = relative
            ? UiLocalizer.T("Relative change in play rate (result clamped to 0.1…8). Active playback only.")
            : UiLocalizer.T("Absolute play rate (0.1…8). Active playback only.");
        _rateFadeLineEdit.Editable = true;
    }

    private void SyncPitchFadeLineEdit(bool relative)
    {
        if (_pitchFadeLineEdit == null || _component == null) return;
        float cents = _component.FadePitchCents;
        string text = cents > 0 ? $"+{cents:0.#}¢" : $"{cents:0.#}¢";
        _pitchFadeLineEdit.Text = text;
        _pitchFadeLineEdit.PlaceholderText = relative ? "±¢" : "0¢";
        _pitchFadeLineEdit.TooltipText = relative
            ? UiLocalizer.T("Relative change in cents (result clamped to −2400…+2400). Active playback only.")
            : UiLocalizer.T("Absolute pitch in cents (−2400…+2400). Active playback only.");
        _pitchFadeLineEdit.Editable = true;
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
