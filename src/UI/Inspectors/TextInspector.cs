// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
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
using Cue2.UI.Shell;
using Cue2.UI.Controls;
using Cue2.UI.Utilities;
using Godot;
using Cue2.UI.Preview;

namespace Cue2.UI.Inspectors;

/// <summary>
/// Inspector for the text overlay component: content, target layer, duration, typography, and preview.
/// Supports multi-edit when Settings multi-edit is on and multiple cues are selected.
/// </summary>
/// <remarks>
/// Multi-edit applies only to selected cues that have a text component. Uniform field values
/// are shown; mixed values are blank. History uses a cuelist snapshot when two or more targets change.
/// Preview always reflects the primary (focused) target when present.
/// </remarks>
public partial class TextInspector : Control
{
    private GlobalData _globalData;
    private GlobalSignals _globalSignals;
    private HistoryManager _history;

    private Cue _focusedCue;
    private TextComponent _focusedText;

    /// <summary>True when multi-edit setting is on and more than one cue is selected.</summary>
    private bool _isMultiEdit;

    /// <summary>Selected cues that currently have a text component (multi-edit targets).</summary>
    private List<(Cue Cue, TextComponent Component)> _textTargets = new();

    private Label _infoLabel;
    private Control _addTextContainer;
    private Control _contentRoot;
    private Control _inspectorContent;
    private Button _addTextButton;
    private Button _deleteTextButton;

    private Control _previewContainer;
    private TextPreviewer _textPreviewer;

    private OptionButton _targetLayerOption;
    private LineEdit _durationLineEdit;
    private LineEdit _fadeInInput;
    private LineEdit _fadeOutInput;
    private TextEdit _contentTextEdit;
    private CheckBox _useBbcodeCheck;
    private OptionButton _fontOption;
    private PopupMenu _fontPopup;
    private OptionButton _hAlignOption;
    private OptionButton _vAlignOption;
    private SpinBox _fontSizeSpin;
    private ColourButton _fontColorPicker;
    private SpinBox _opacitySpin;
    private SpinBox _marginsSpin;
    private CheckBox _autowrapCheck;
    private SpinBox _outlineSizeSpin;
    private ColourButton _outlineColorPicker;
    private CheckBox _backgroundCheck;
    private ColourButton _backgroundColorPicker;

    private bool _isSyncingUi;
    private bool _alignOptionsReady;
    private bool _fontOptionsReady;
    private string[] _systemFontNames = Array.Empty<string>();

    /// <summary>Max width of the system-font dropdown (px).</summary>
    private const int FontPopupMaxWidth = 320;

    /// <summary>Max height of the system-font dropdown (px); scrolls when content is taller.</summary>
    private const int FontPopupMaxHeight = 280;

    /// <summary>Minimum dropdown width so short buttons still look usable.</summary>
    private const int FontPopupMinWidth = 200;

    /// <inheritdoc />
    public override void _Ready()
    {
        _globalData = GetNode<GlobalData>("/root/GlobalData");
        _globalSignals = GetNode<GlobalSignals>("/root/GlobalSignals");
        _history = _globalData?.HistoryManager;

        BindNodes();
        PopulateAlignOptions();
        EnsureSystemFontList();
        WireSignals();

        UiUtilities.FormatLabelsColours(this, GlobalStyles.SoftFontColor);
        _infoLabel.AddThemeColorOverride("font_color", GlobalStyles.DisabledColor);

        if (_deleteTextButton != null)
        {
            _deleteTextButton.AddThemeColorOverride("font_color", GlobalStyles.Danger);
            try
            {
                _deleteTextButton.Icon = GetThemeIcon("DeleteBin", "AtlasIcons");
                _deleteTextButton.ExpandIcon = true;
            }
            catch
            {
                // optional icon
            }
        }

        ShowNoSelection();
    
        UiLocalizer.LocalizeTree(this);
        if (_globalSignals != null)
            _globalSignals.LocaleChanged += OnLocaleChanged;
}

    /// <inheritdoc />
    public override void _ExitTree()
    {
        if (_globalSignals != null)
            _globalSignals.LocaleChanged -= OnLocaleChanged;

        if (_globalSignals != null)
        {
            _globalSignals.ShellFocused -= OnShellFocused;
            _globalSignals.SyncShellInspector -= OnSyncFromHistory;
            _globalSignals.DisplaysChanged -= OnDisplaysChanged;
        }

        if (_history != null)
            _history.HistoryRestored -= OnHistoryRestored;

        if (_fontPopup != null && IsInstanceValid(_fontPopup))
            _fontPopup.AboutToPopup -= OnFontPopupAboutToPopup;
    }

    private void BindNodes()
    {
        _infoLabel = GetNode<Label>("%InfoLabel");
        _addTextContainer = GetNode<Control>("%AddTextContainer");
        _contentRoot = GetNode<Control>("%ContentRoot");
        _inspectorContent = GetNode<Control>("%InspectorContent");
        _addTextButton = GetNode<Button>("%AddTextButton");
        _deleteTextButton = GetNode<Button>("%DeleteTextButton");
        _previewContainer = GetNodeOrNull<Control>("%PreviewContainer");
        _textPreviewer = GetNodeOrNull<TextPreviewer>("%TextPreviewer");
        _targetLayerOption = GetNode<OptionButton>("%TargetLayerOptionButton");
        _durationLineEdit = GetNode<LineEdit>("%DurationLineEdit");
        _fadeInInput = GetNodeOrNull<LineEdit>("%FadeInInput");
        _fadeOutInput = GetNodeOrNull<LineEdit>("%FadeOutInput");
        _contentTextEdit = GetNode<TextEdit>("%ContentTextEdit");
        _useBbcodeCheck = GetNode<CheckBox>("%UseBbcodeCheck");
        _fontOption = GetNodeOrNull<OptionButton>("%FontOptionButton");
        if (_fontOption != null)
        {
            // Avoid expanding to the longest font name (can be extremely wide).
            _fontOption.FitToLongestItem = false;
            _fontPopup = _fontOption.GetPopup();
        }
        _hAlignOption = GetNode<OptionButton>("%HAlignOption");
        _vAlignOption = GetNode<OptionButton>("%VAlignOption");
        _fontSizeSpin = GetNode<SpinBox>("%FontSizeSpin");
        _fontColorPicker = GetNode<ColourButton>("%FontColorPicker");
        _opacitySpin = GetNode<SpinBox>("%OpacitySpin");
        _marginsSpin = GetNode<SpinBox>("%MarginsSpin");
        _autowrapCheck = GetNode<CheckBox>("%AutowrapCheck");
        _outlineSizeSpin = GetNode<SpinBox>("%OutlineSizeSpin");
        _outlineColorPicker = GetNode<ColourButton>("%OutlineColorPicker");
        _backgroundCheck = GetNode<CheckBox>("%BackgroundCheck");
        _backgroundColorPicker = GetNode<ColourButton>("%BackgroundColorPicker");
    }

    private void WireSignals()
    {
        _globalSignals.ShellFocused += OnShellFocused;
        _globalSignals.SyncShellInspector += OnSyncFromHistory;
        _globalSignals.DisplaysChanged += OnDisplaysChanged;

        if (_history != null)
            _history.HistoryRestored += OnHistoryRestored;

        _addTextButton.Pressed += OnAddTextPressed;
        _deleteTextButton.Pressed += OnDeleteTextPressed;

        _targetLayerOption.ItemSelected += OnTargetLayerSelected;
        _durationLineEdit.TextSubmitted += OnDurationSubmitted;
        _durationLineEdit.FocusExited += () => OnDurationSubmitted(_durationLineEdit.Text);

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

        _contentTextEdit.TextChanged += OnContentChanged;
        _contentTextEdit.FocusExited += OnContentFocusExited;

        _useBbcodeCheck.Toggled += OnUseBbcodeToggled;
        if (_fontOption != null)
            _fontOption.ItemSelected += OnFontSelected;
        if (_fontPopup != null)
        {
            // Cap size immediately; AboutToPopup re-applies against the live button rect.
            _fontPopup.MaxSize = new Vector2I(FontPopupMaxWidth, FontPopupMaxHeight);
            _fontPopup.AboutToPopup += OnFontPopupAboutToPopup;
        }
        _hAlignOption.ItemSelected += OnHAlignSelected;
        _vAlignOption.ItemSelected += OnVAlignSelected;

        _fontSizeSpin.ValueChanged += OnFontSizeChanged;
        WireSpinBoxFocusRelease(_fontSizeSpin, EndFontSizeCoalesce);

        _fontColorPicker.ColorChanged += OnFontColorChanged;
        _fontColorPicker.PopupClosed += EndFontColorCoalesce;

        _opacitySpin.ValueChanged += OnOpacityChanged;
        WireSpinBoxFocusRelease(_opacitySpin, EndOpacityCoalesce);

        _marginsSpin.ValueChanged += OnMarginsChanged;
        WireSpinBoxFocusRelease(_marginsSpin, EndMarginsCoalesce);
        _autowrapCheck.Toggled += OnAutowrapToggled;

        _outlineSizeSpin.ValueChanged += OnOutlineSizeChanged;
        WireSpinBoxFocusRelease(_outlineSizeSpin, EndOutlineSizeCoalesce);

        _outlineColorPicker.ColorChanged += OnOutlineColorChanged;
        _outlineColorPicker.PopupClosed += EndOutlineColorCoalesce;

        _backgroundCheck.Toggled += OnBackgroundToggled;
        _backgroundColorPicker.ColorChanged += OnBackgroundColorChanged;
        _backgroundColorPicker.PopupClosed += EndBackgroundColorCoalesce;
    }

    /// <summary>
    /// Releases focus when the user finishes typing in a SpinBox (Enter), and ends
    /// history coalescing when the embedded LineEdit loses focus.
    /// </summary>
    /// <remarks>
    /// Godot routes keyboard focus to the SpinBox's internal LineEdit, not the SpinBox
    /// itself — so SpinBox.FocusExited alone is unreliable for commit/cleanup.
    /// </remarks>
    /// <param name="spin">SpinBox to wire.</param>
    /// <param name="onFocusExited">Optional coalesce/end callback when editing finishes.</param>
    private static void WireSpinBoxFocusRelease(SpinBox spin, Action onFocusExited = null)
    {
        if (spin == null)
            return;

        spin.FocusMode = FocusModeEnum.All;
        var edit = spin.GetLineEdit();
        if (edit == null)
            return;

        edit.FocusMode = FocusModeEnum.All;
        edit.TextSubmitted += _ => ReleaseSpinBoxFocus(spin);
        if (onFocusExited != null)
            edit.FocusExited += () => onFocusExited();
    }

    /// <summary>
    /// Clears focus from a SpinBox and its embedded LineEdit after input is committed.
    /// </summary>
    private static void ReleaseSpinBoxFocus(SpinBox spin)
    {
        if (spin == null || !IsInstanceValid(spin))
            return;

        var edit = spin.GetLineEdit();
        if (edit != null && IsInstanceValid(edit) && edit.HasFocus())
            edit.ReleaseFocus();
        if (spin.HasFocus())
            spin.ReleaseFocus();
    }

    private void PopulateAlignOptions()
    {
        if (_alignOptionsReady)
            return;

        _hAlignOption.Clear();
        UiLocalizer.AddTranslatedItem(_hAlignOption, "Left", (int)HorizontalAlignment.Left);
        UiLocalizer.AddTranslatedItem(_hAlignOption, "Center", (int)HorizontalAlignment.Center);
        UiLocalizer.AddTranslatedItem(_hAlignOption, "Right", (int)HorizontalAlignment.Right);
        UiLocalizer.AddTranslatedItem(_hAlignOption, "Fill", (int)HorizontalAlignment.Fill);

        _vAlignOption.Clear();
        UiLocalizer.AddTranslatedItem(_vAlignOption, "Top", (int)VerticalAlignment.Top);
        UiLocalizer.AddTranslatedItem(_vAlignOption, "Center", (int)VerticalAlignment.Center);
        UiLocalizer.AddTranslatedItem(_vAlignOption, "Bottom", (int)VerticalAlignment.Bottom);
        // Godot VerticalAlignment has no Fill in all versions; skip if undefined.

        _alignOptionsReady = true;
    }

    /// <summary>
    /// Loads and caches sorted system font family names once per process.
    /// </summary>
    private void EnsureSystemFontList()
    {
        if (_fontOptionsReady && _systemFontNames.Length > 0)
            return;

        try
        {
            var fonts = OS.GetSystemFonts();
            if (fonts == null || fonts.Length == 0)
            {
                _systemFontNames = Array.Empty<string>();
            }
            else
            {
                var list = new System.Collections.Generic.List<string>(fonts.Length);
                foreach (string name in fonts)
                {
                    if (string.IsNullOrWhiteSpace(name))
                        continue;
                    list.Add(name.Trim());
                }
                list.Sort(StringComparer.OrdinalIgnoreCase);
                // Deduplicate case-insensitively while keeping first display form.
                var seen = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var unique = new System.Collections.Generic.List<string>(list.Count);
                foreach (string n in list)
                {
                    if (seen.Add(n))
                        unique.Add(n);
                }
                _systemFontNames = unique.ToArray();
            }
        }
        catch (Exception ex)
        {
            GD.PrintErr($"TextInspector:EnsureSystemFontList - {ex.Message}");
            _systemFontNames = Array.Empty<string>();
        }

        _fontOptionsReady = true;
    }

    /// <summary>
    /// Rebuilds the font OptionButton for the current component selection.
    /// </summary>
    private void PopulateFontOptions()
    {
        if (_fontOption == null)
            return;

        EnsureSystemFontList();

        _fontOption.SetBlockSignals(true);
        try
        {
            _fontOption.Clear();
            // Index 0 = theme default (empty FontName).
            UiLocalizer.AddTranslatedItem(_fontOption, "Default");
            _fontOption.SetItemMetadata(0, string.Empty);

            bool uniformFont = InspectorMultiEditSupport.TryGetUniformString(
                GetTextTargets().Select(t => t.Component.FontName?.Trim() ?? string.Empty),
                out string current);
            if (!uniformFont)
                current = string.Empty;
            int selected = 0;
            bool matched = uniformFont && string.IsNullOrEmpty(current);
            if (!uniformFont)
                matched = false; // leave Selected = -1 when mixed

            foreach (string name in _systemFontNames)
            {
                _fontOption.AddItem(name);
                int idx = _fontOption.ItemCount - 1;
                _fontOption.SetItemMetadata(idx, name);
                if (!matched && string.Equals(name, current, StringComparison.OrdinalIgnoreCase))
                {
                    selected = idx;
                    matched = true;
                }
            }

            // Keep a saved family even if missing on this machine.
            if (uniformFont && !matched && !string.IsNullOrEmpty(current))
            {
                _fontOption.AddItem($"{current} (missing)");
                int idx = _fontOption.ItemCount - 1;
                _fontOption.SetItemMetadata(idx, current);
                selected = idx;
                matched = true;
            }

            _fontOption.Selected = uniformFont ? selected : -1;
        }
        finally
        {
            _fontOption.SetBlockSignals(false);
        }

        // Keep popup bounds in sync after rebuild (item count can change max content height).
        if (_fontPopup != null && IsInstanceValid(_fontPopup))
            _fontPopup.MaxSize = new Vector2I(FontPopupMaxWidth, FontPopupMaxHeight);
    }

    /// <summary>
    /// Limits the font dropdown size and anchors it under the Font option button.
    /// </summary>
    private void OnFontPopupAboutToPopup()
    {
        if (_fontOption == null || !IsInstanceValid(_fontOption)
            || _fontPopup == null || !IsInstanceValid(_fontPopup))
            return;

        // Match button width (clamped) so the list doesn't stretch to long family names.
        float buttonW = _fontOption.Size.X;
        int width = Mathf.Clamp(Mathf.RoundToInt(buttonW), FontPopupMinWidth, FontPopupMaxWidth);

        _fontPopup.MaxSize = new Vector2I(width, FontPopupMaxHeight);
        _fontPopup.MinSize = new Vector2I(width, 0);
        // Height is content-driven up to MaxSize; force width so wrap/clip is consistent.
        int contentH = Mathf.Max(1, Mathf.RoundToInt(_fontPopup.GetContentsMinimumSize().Y));
        _fontPopup.Size = new Vector2I(width, Mathf.Min(FontPopupMaxHeight, contentH));

        int popupH = Mathf.Min(FontPopupMaxHeight, Mathf.Max(1, (int)_fontPopup.Size.Y));
        if (popupH <= 1)
            popupH = FontPopupMaxHeight;

        PlaceFontPopup(width, popupH);

        // OptionButton may reposition after this signal; re-apply once the popup is shown.
        CallDeferred(MethodName.ApplyFontPopupPlacement, width, popupH);
    }

    /// <summary>
    /// Re-asserts font popup size/position after OptionButton's own show logic.
    /// </summary>
    private void ApplyFontPopupPlacement(int width, int height)
    {
        if (_fontOption == null || !IsInstanceValid(_fontOption)
            || _fontPopup == null || !IsInstanceValid(_fontPopup)
            || !_fontPopup.Visible)
            return;

        _fontPopup.MaxSize = new Vector2I(width, FontPopupMaxHeight);
        _fontPopup.Size = new Vector2I(width, Mathf.Clamp(height, 1, FontPopupMaxHeight));
        PlaceFontPopup(width, (int)_fontPopup.Size.Y);
    }

    /// <summary>
    /// Places the font popup under the option button in native screen space or
    /// embedder-local space (Linux popup-embed policy).
    /// </summary>
    private void PlaceFontPopup(int width, int popupH)
    {
        Transform2D screenXform = _fontOption.GetScreenTransform();
        Vector2 topLeft = screenXform * Vector2.Zero;
        Vector2 bottomLeft = screenXform * new Vector2(0f, _fontOption.Size.Y);
        Vector2I pos = UiUtilities.ScreenPointToPopupPosition(_fontPopup, bottomLeft);
        Vector2I topLeftPos = UiUtilities.ScreenPointToPopupPosition(_fontPopup, topLeft);
        Rect2I usable = UiUtilities.GetPopupUsableRect(_fontPopup);

        if (pos.X + width > usable.Position.X + usable.Size.X)
            pos.X = usable.Position.X + usable.Size.X - width;
        if (pos.X < usable.Position.X)
            pos.X = usable.Position.X;

        if (pos.Y + popupH > usable.Position.Y + usable.Size.Y)
        {
            pos.Y = topLeftPos.Y - popupH;
            if (pos.Y < usable.Position.Y)
                pos.Y = usable.Position.Y;
        }

        _fontPopup.Position = pos;
    }

    private void OnShellFocused(int cueId)
    {
        if (cueId < 0)
        {
            _focusedCue = null;
            _focusedText = null;
            _isMultiEdit = false;
            _textTargets.Clear();
            ShowNoSelection();
            return;
        }

        _focusedCue = CueList.FetchCueFromId(cueId);
        RefreshFromFocusedCue();
    }

    private void OnSyncFromHistory()
    {
        CallDeferred(MethodName.RefreshFromFocusedCue);
    }

    private void OnHistoryRestored(int scope)
    {
        if (scope == (int)HistoryManager.HistoryScope.Cue
            || scope == (int)HistoryManager.HistoryScope.Cuelist
            || scope == (int)HistoryManager.HistoryScope.MultiCue)
        {
            CallDeferred(MethodName.RefreshFromFocusedCue);
        }
    }

    private void OnDisplaysChanged()
    {
        if (GetTextTargets().Count > 0)
        {
            PopulateTargetLayerOptions();
            RefreshPreview(fullLayout: true);
        }
    }

    private void RefreshFromFocusedCue()
    {
        if (_focusedCue == null && _globalData != null && _globalData.FocusedCue >= 0)
            _focusedCue = CueList.FetchCueFromId(_globalData.FocusedCue);

        _isMultiEdit = InspectorMultiEditSupport.ShouldUseMultiEdit(_globalData);
        if (_isMultiEdit)
        {
            _textTargets = InspectorMultiEditSupport.CollectComponentTargets(c => c.GetTextComponent());
            // Primary for preview: focused cue's text if present, else last target.
            if (_focusedCue != null)
                _focusedText = _focusedCue.GetTextComponent();
            if (_focusedText == null && _textTargets.Count > 0)
            {
                _focusedCue = _textTargets[^1].Cue;
                _focusedText = _textTargets[^1].Component;
            }

            if (_textTargets.Count == 0)
            {
                _focusedText = null;
                ShowEmptyStateMulti();
                return;
            }

            ShowContentMulti();
            SyncUiFromModel();
            UpdateCcLinkedHint();
            return;
        }

        _textTargets.Clear();

        if (_focusedCue == null)
        {
            _focusedText = null;
            ShowNoSelection();
            return;
        }

        _focusedText = _focusedCue.GetTextComponent();
        if (_focusedText == null)
        {
            ShowEmptyState();
            return;
        }

        ShowContent();
        SyncUiFromModel();
        UpdateCcLinkedHint();
    }

    /// <summary>
    /// When the same cue’s video has closed captions linked, hint that live text is CC-driven.
    /// Multi-edit uses a multi placeholder when content is mixed.
    /// </summary>
    private void UpdateCcLinkedHint()
    {
        if (_contentTextEdit == null)
            return;

        if (_isMultiEdit && GetTextTargets().Count > 1
            && !InspectorMultiEditSupport.TryGetUniformString(
                GetTextTargets().Select(t => t.Component.Content ?? string.Empty), out _))
        {
            _contentTextEdit.PlaceholderText = InspectorMultiEditSupport.MultiPlaceholder;
            return;
        }

        var video = _focusedCue?.GetVideoComponent();
        bool linked = video != null && video.UseSubtitles && !video.IsImage && video.HasTextSubtitles;
        if (linked)
        {
            _contentTextEdit.PlaceholderText =
                UiLocalizer.T("Closed captions from video will show here during playback (static content is still used in the inspector / when CC is off).");
        }
        else
        {
            _contentTextEdit.PlaceholderText = UiLocalizer.T("Enter overlay text…");
        }
    }

    private void ShowNoSelection()
    {
        if (_infoLabel != null)
        {
            _infoLabel.Visible = true;
            _infoLabel.Text = UiLocalizer.T("No shell selected");
            _infoLabel.TooltipText = string.Empty;
        }
        if (_contentRoot != null)
            _contentRoot.Visible = false;
        if (_addTextContainer != null)
            _addTextContainer.Visible = false;
        if (_inspectorContent != null)
            _inspectorContent.Visible = false;
        if (_addTextButton != null)
            _addTextButton.Text = UiLocalizer.T("Add Text Component");
        ClearPreview();
    }

    private void ShowEmptyState()
    {
        if (_infoLabel != null)
        {
            _infoLabel.Visible = true;
            _infoLabel.Text = UiLocalizer.T("No Text Component");
            _infoLabel.TooltipText = string.Empty;
        }
        if (_contentRoot != null)
            _contentRoot.Visible = true;
        if (_addTextContainer != null)
            _addTextContainer.Visible = true;
        if (_inspectorContent != null)
            _inspectorContent.Visible = false;
        if (_addTextButton != null)
        {
            _addTextButton.Visible = true;
            _addTextButton.Text = UiLocalizer.T("Add Text Component");
        }
        ClearPreview();
    }

    /// <summary>
    /// Multi-selection where none of the selected cues have a text component.
    /// </summary>
    private void ShowEmptyStateMulti()
    {
        int selected = InspectorMultiEditSupport.GetSelectedCues().Count;
        if (_infoLabel != null)
        {
            _infoLabel.Visible = true;
            _infoLabel.Text = UiLocalizer.Tf("No text on {0} selected cue(s)", selected);
            _infoLabel.TooltipText =
                UiLocalizer.T("None of the selected cues have a text component. Add text to all selected cues.");
        }
        if (_contentRoot != null)
            _contentRoot.Visible = true;
        if (_addTextContainer != null)
            _addTextContainer.Visible = true;
        if (_inspectorContent != null)
            _inspectorContent.Visible = false;
        if (_addTextButton != null)
        {
            _addTextButton.Visible = true;
            _addTextButton.Text = UiLocalizer.Tf("Add Text to {0} Missing Cue(s)", selected);
        }
        ClearPreview();
    }

    private void ShowContent()
    {
        if (_infoLabel != null)
        {
            _infoLabel.Visible = false;
            _infoLabel.TooltipText = string.Empty;
        }
        if (_contentRoot != null)
            _contentRoot.Visible = true;
        if (_addTextContainer != null)
            _addTextContainer.Visible = false;
        if (_inspectorContent != null)
            _inspectorContent.Visible = true;
        if (_addTextButton != null)
            _addTextButton.Text = UiLocalizer.T("Add Text Component");
        if (_deleteTextButton != null)
            _deleteTextButton.TooltipText = UiLocalizer.T("Remove text component from this cue");
    }

    private void ShowContentMulti()
    {
        int selected = InspectorMultiEditSupport.GetSelectedCues().Count;
        int withText = _textTargets.Count;
        int missing = selected - withText;
        if (_infoLabel != null)
        {
            _infoLabel.Visible = true;
            _infoLabel.Text = InspectorMultiEditSupport.FormatComponentMultiHeader("Text", withText, selected);
            _infoLabel.TooltipText = InspectorMultiEditSupport.FormatComponentMultiTooltip(
                "text",
                _textTargets.Select(t => (t.Cue, (object)t.Component)).ToList(),
                selected);
        }
        if (_contentRoot != null)
            _contentRoot.Visible = true;
        if (_inspectorContent != null)
            _inspectorContent.Visible = true;
        if (_addTextContainer != null)
            _addTextContainer.Visible = missing > 0;
        if (_addTextButton != null)
        {
            _addTextButton.Visible = true;
            _addTextButton.Text = missing > 0
                ? UiLocalizer.Tf("Add Text to {0} Missing Cue(s)", missing)
                : UiLocalizer.T("Add Text Component");
        }

        if (_deleteTextButton != null)
            _deleteTextButton.TooltipText = UiLocalizer.Tf("Remove text component from {0} cue(s)", withText);
    }

    private void ClearPreview()
    {
        _textPreviewer?.SetComponent(null);
    }

    private void RefreshPreview(bool fullLayout)
    {
        if (_textPreviewer == null || _focusedText == null)
            return;

        // Preview always reflects the primary target only.
        if (fullLayout)
        {
            _textPreviewer.SetComponent(_focusedText);
            _textPreviewer.SetAreasDeferred(_focusedText.TargetLayerId);
        }
        else
        {
            _textPreviewer.SetComponent(_focusedText);
            _textPreviewer.RefreshVisuals();
        }
    }

    /// <summary>
    /// Targets for the next edit: multi-edit subset, or the single focused text component.
    /// </summary>
    private List<(Cue Cue, TextComponent Component)> GetTextTargets()
    {
        if (_isMultiEdit)
            return _textTargets ?? new List<(Cue Cue, TextComponent Component)>();
        if (_focusedCue != null && _focusedText != null)
            return new List<(Cue Cue, TextComponent Component)> { (_focusedCue, _focusedText) };
        return new List<(Cue Cue, TextComponent Component)>();
    }

    private bool UseMultiHistory() => GetTextTargets().Count > 1;

    private void SyncUiFromModel()
    {
        var targets = GetTextTargets();
        if (targets.Count == 0)
            return;

        _isSyncingUi = true;
        try
        {
            PopulateTargetLayerOptions();

            // Duration
            if (InspectorMultiEditSupport.TryGetUniformDouble(targets.Select(t => t.Component.Duration), out double dur))
            {
                if (dur <= 0)
                {
                    _durationLineEdit.Text = "0 (until stopped)";
                    _durationLineEdit.TooltipText = UiLocalizer.T("0 = stay active until stopped. Enter a time to auto-end.");
                }
                else
                {
                    _durationLineEdit.Text = UiUtilities.ParseAndFormatTime(
                        dur.ToString(CultureInfo.InvariantCulture),
                        out _,
                        out string tip);
                    _durationLineEdit.TooltipText = tip + " (0 = until stopped)";
                }
                _durationLineEdit.PlaceholderText = string.Empty;
            }
            else
            {
                _durationLineEdit.Text = string.Empty;
                _durationLineEdit.PlaceholderText = InspectorMultiEditSupport.MultiPlaceholder;
                _durationLineEdit.TooltipText = InspectorMultiEditSupport.MultiPlaceholder;
            }

            // Content
            if (InspectorMultiEditSupport.TryGetUniformString(
                    targets.Select(t => t.Component.Content ?? string.Empty), out string content))
            {
                _contentTextEdit.Text = content;
            }
            else
            {
                _contentTextEdit.Text = string.Empty;
            }

            // Booleans
            if (InspectorMultiEditSupport.TryGetUniform(targets.Select(t => t.Component.UseBbcode), out bool bbcode))
                _useBbcodeCheck.SetPressedNoSignal(bbcode);
            else
                _useBbcodeCheck.SetPressedNoSignal(false);

            if (InspectorMultiEditSupport.TryGetUniform(targets.Select(t => t.Component.Autowrap), out bool wrap))
                _autowrapCheck.SetPressedNoSignal(wrap);
            else
                _autowrapCheck.SetPressedNoSignal(false);

            if (InspectorMultiEditSupport.TryGetUniform(targets.Select(t => t.Component.BackgroundEnabled), out bool bg))
                _backgroundCheck.SetPressedNoSignal(bg);
            else
                _backgroundCheck.SetPressedNoSignal(false);

            PopulateFontOptions();
            if (InspectorMultiEditSupport.TryGetUniformString(
                    targets.Select(t => t.Component.FontName ?? string.Empty), out _))
            {
                // Font option selection handled in PopulateFontOptions via primary when uniform.
            }

            if (InspectorMultiEditSupport.TryGetUniform(
                    targets.Select(t => (int)t.Component.HorizontalAlignment), out int hAlign))
                SelectOptionById(_hAlignOption, hAlign);
            else if (_hAlignOption != null)
            {
                _hAlignOption.SetBlockSignals(true);
                _hAlignOption.Selected = -1;
                _hAlignOption.SetBlockSignals(false);
            }

            if (InspectorMultiEditSupport.TryGetUniform(
                    targets.Select(t => (int)t.Component.VerticalAlignment), out int vAlign))
                SelectOptionById(_vAlignOption, vAlign);
            else if (_vAlignOption != null)
            {
                _vAlignOption.SetBlockSignals(true);
                _vAlignOption.Selected = -1;
                _vAlignOption.SetBlockSignals(false);
            }

            if (InspectorMultiEditSupport.TryGetUniform(targets.Select(t => t.Component.FontSize), out int fontSize))
                _fontSizeSpin.SetValueNoSignal(fontSize);
            else
                InspectorMultiEditSupport.ClearSpinBoxText(_fontSizeSpin);

            if (InspectorMultiEditSupport.TryGetUniformColor(targets.Select(t => t.Component.FontColor), out Color fontColor))
                _fontColorPicker.Color = fontColor;

            if (InspectorMultiEditSupport.TryGetUniformFloat(targets.Select(t => t.Component.Opacity), out float opacity))
                _opacitySpin.SetValueNoSignal(Mathf.RoundToInt(Mathf.Clamp(opacity, 0f, 1f) * 100f));
            else
                InspectorMultiEditSupport.ClearSpinBoxText(_opacitySpin);

            if (InspectorMultiEditSupport.TryGetUniform(targets.Select(t => t.Component.Margins), out int margins))
                _marginsSpin.SetValueNoSignal(margins);
            else
                InspectorMultiEditSupport.ClearSpinBoxText(_marginsSpin);

            if (InspectorMultiEditSupport.TryGetUniform(targets.Select(t => t.Component.OutlineSize), out int outlineSize))
                _outlineSizeSpin.SetValueNoSignal(outlineSize);
            else
                InspectorMultiEditSupport.ClearSpinBoxText(_outlineSizeSpin);

            if (InspectorMultiEditSupport.TryGetUniformColor(targets.Select(t => t.Component.OutlineColor), out Color outlineColor))
                _outlineColorPicker.Color = outlineColor;

            if (InspectorMultiEditSupport.TryGetUniformColor(targets.Select(t => t.Component.BackgroundColor), out Color bgColor))
                _backgroundColorPicker.Color = bgColor;

            if (_fadeInInput != null)
            {
                if (InspectorMultiEditSupport.TryGetUniformDouble(
                        targets.Select(t => t.Component.FadeInDuration), out double fadeIn))
                {
                    _fadeInInput.Text = UiUtilities.FormatTime(fadeIn);
                    _fadeInInput.PlaceholderText = string.Empty;
                }
                else
                {
                    _fadeInInput.Text = string.Empty;
                    _fadeInInput.PlaceholderText = InspectorMultiEditSupport.MultiPlaceholder;
                }
            }

            if (_fadeOutInput != null)
            {
                if (InspectorMultiEditSupport.TryGetUniformDouble(
                        targets.Select(t => t.Component.FadeOutDuration), out double fadeOut))
                {
                    _fadeOutInput.Text = UiUtilities.FormatTime(fadeOut);
                    _fadeOutInput.PlaceholderText = string.Empty;
                }
                else
                {
                    _fadeOutInput.Text = string.Empty;
                    _fadeOutInput.PlaceholderText = InspectorMultiEditSupport.MultiPlaceholder;
                }
            }
        }
        finally
        {
            _isSyncingUi = false;
        }

        RefreshPreview(fullLayout: true);
    }

    private void PopulateTargetLayerOptions()
    {
        if (_targetLayerOption == null)
            return;

        var targets = GetTextTargets();
        if (targets.Count == 0)
            return;

        _targetLayerOption.SetBlockSignals(true);
        try
        {
            _targetLayerOption.Clear();
            _targetLayerOption.AddItem(UiLocalizer.T("No Output"));
            _targetLayerOption.SetItemMetadata(0, -1);

            bool uniformLayer = InspectorMultiEditSupport.TryGetUniform(
                targets.Select(t => t.Component.TargetLayerId), out int targetId);
            int selectedIndex = 0;
            bool matched = !uniformLayer || targetId < 0;

            if (DisplaysManager.Layers != null)
            {
                foreach (var layer in DisplaysManager.Layers)
                {
                    if (layer == null) continue;
                    _targetLayerOption.AddItem(layer.LayerName);
                    int idx = _targetLayerOption.ItemCount - 1;
                    _targetLayerOption.SetItemMetadata(idx, layer.LayerId);
                    if (uniformLayer && layer.LayerId == targetId)
                    {
                        selectedIndex = idx;
                        matched = true;
                    }
                }
            }

            if (uniformLayer && !matched && targetId >= 0)
            {
                _targetLayerOption.AddItem($"!!! Missing layer {targetId}");
                int idx = _targetLayerOption.ItemCount - 1;
                _targetLayerOption.SetItemMetadata(idx, targetId);
                selectedIndex = idx;
            }

            _targetLayerOption.Selected = uniformLayer ? selectedIndex : -1;
        }
        finally
        {
            _targetLayerOption.SetBlockSignals(false);
        }
    }

    private static void SelectOptionById(OptionButton button, int id)
    {
        if (button == null) return;
        for (int i = 0; i < button.ItemCount; i++)
        {
            if (button.GetItemId(i) == id)
            {
                button.Selected = i;
                return;
            }
        }
    }

    private bool CanEdit()
    {
        return !_isSyncingUi
               && GetTextTargets().Count > 0
               && _history?.IsRestoring != true;
    }

    private void Record(string description, string coalesceKey = null)
    {
        var targets = GetTextTargets();
        if (targets.Count == 0)
            return;
        InspectorMultiEditSupport.RecordBeforeEdit(
            _globalData,
            UseMultiHistory(),
            targets[^1].Cue,
            description,
            multiDescription: null,
            coalesceKey);
    }

    private void EndCoalesce(string key)
    {
        if (string.IsNullOrEmpty(key))
            return;
        InspectorMultiEditSupport.EndCoalesce(_globalData, UseMultiHistory(), key, key);
    }

    private string CoalesceKey(string field)
    {
        if (UseMultiHistory())
            return $"multi:text:{field}";
        return _focusedCue != null ? $"cue:{_focusedCue.Id}:text:{field}" : null;
    }

    private void NotifyLiveVisuals()
    {
        foreach (var (_, text) in GetTextTargets())
            _globalData?.CueCommandExecutor?.RefreshPlayingTextVisuals(text);
        RefreshPreview(fullLayout: false);
    }

    private void RecalcDuration()
    {
        foreach (var (cue, text) in GetTextTargets())
        {
            text?.RecalculateDuration();
            cue?.CalculateTotalDuration();
            if (cue != null)
                _globalSignals?.EmitSignal(nameof(GlobalSignals.UpdateShellBar), cue.Id);
        }
    }

    private void OnAddTextPressed()
    {
        if (_history?.IsRestoring == true)
            return;

        if (_isMultiEdit)
        {
            var selected = InspectorMultiEditSupport.GetSelectedCues();
            var missing = selected.Where(c => c.GetTextComponent() == null).ToList();
            if (missing.Count == 0)
            {
                RefreshFromFocusedCue();
                return;
            }

            InspectorMultiEditSupport.RecordBeforeEdit(
                _globalData,
                missing.Count > 1,
                missing[^1],
                "Add text component",
                "Multi-add text components");
            foreach (var cue in missing)
                cue.AddTextComponent();
            foreach (var cue in missing)
            {
                cue.CalculateTotalDuration();
                _globalSignals?.EmitSignal(nameof(GlobalSignals.UpdateShellBar), cue.Id);
            }

            _globalSignals.EmitSignal(nameof(GlobalSignals.SyncShellInspector));
            RefreshFromFocusedCue();
            return;
        }

        if (_focusedCue == null)
            return;

        if (_focusedCue.GetTextComponent() != null)
        {
            RefreshFromFocusedCue();
            return;
        }

        Record("Add text component");
        _focusedText = _focusedCue.AddTextComponent();
        _focusedCue.CalculateTotalDuration();
        _globalSignals?.EmitSignal(nameof(GlobalSignals.UpdateShellBar), _focusedCue.Id);
        _globalSignals.EmitSignal(nameof(GlobalSignals.SyncShellInspector));
        RefreshFromFocusedCue();
    }

    private void OnDeleteTextPressed()
    {
        if (!CanEdit())
            return;

        var targets = GetTextTargets();
        Record("Remove text component");
        foreach (var (cue, text) in targets)
        {
            cue.RemoveICueComponent(text);
            cue.CalculateTotalDuration();
            _globalSignals?.EmitSignal(nameof(GlobalSignals.UpdateShellBar), cue.Id);
        }

        _focusedText = null;
        _textTargets.Clear();
        _globalSignals.EmitSignal(nameof(GlobalSignals.SyncShellInspector));
        RefreshFromFocusedCue();
    }

    private void OnTargetLayerSelected(long index)
    {
        if (!CanEdit() || _targetLayerOption == null)
            return;

        string item = _targetLayerOption.GetItemText((int)index);
        if (item != null && item.StartsWith("!!! Missing", StringComparison.Ordinal))
            return;

        int layerId = (int)_targetLayerOption.GetItemMetadata((int)index);
        var targets = GetTextTargets();
        if (targets.All(t => t.Component.TargetLayerId == layerId))
            return;

        Record("Edit text target layer");
        foreach (var (_, text) in targets)
            text.TargetLayerId = layerId;
        // Target layer change requires re-GO to re-host on outputs; preview updates immediately.
        RefreshPreview(fullLayout: true);
    }

    private void OnDurationSubmitted(string text)
    {
        if (!CanEdit())
        {
            if (_durationLineEdit != null && _durationLineEdit.HasFocus())
                _durationLineEdit.ReleaseFocus();
            return;
        }

        string raw = (text ?? string.Empty).Trim();
        if (raw.StartsWith("0 (", StringComparison.Ordinal) || raw == "0")
        {
            var targets = GetTextTargets();
            if (targets.Any(t => t.Component.Duration != 0))
            {
                Record("Edit text duration");
                foreach (var (_, comp) in targets)
                    comp.Duration = 0;
                RecalcDuration();
            }

            SyncDurationFieldOnly();
            if (_durationLineEdit != null && _durationLineEdit.HasFocus())
                _durationLineEdit.ReleaseFocus();
            return;
        }

        UiUtilities.ParseAndFormatTime(raw, out double seconds, out bool isValid);
        if (!isValid)
        {
            SyncDurationFieldOnly();
            if (_durationLineEdit != null && _durationLineEdit.HasFocus())
                _durationLineEdit.ReleaseFocus();
            return;
        }

        seconds = Math.Max(0, seconds);
        var targets2 = GetTextTargets();
        if (targets2.Any(t => Math.Abs(t.Component.Duration - seconds) >= 1e-9))
        {
            Record("Edit text duration");
            foreach (var (_, comp) in targets2)
                comp.Duration = seconds;
            RecalcDuration();
        }

        SyncDurationFieldOnly();
        if (_durationLineEdit != null && _durationLineEdit.HasFocus())
            _durationLineEdit.ReleaseFocus();
    }

    /// <summary>
    /// Commits fade-in or fade-out duration from a time LineEdit.
    /// </summary>
    /// <param name="text">User-entered time string.</param>
    /// <param name="isIn">True for fade-in; false for fade-out.</param>
    private void OnFadeSubmitted(string text, bool isIn)
    {
        var field = isIn ? _fadeInInput : _fadeOutInput;
        if (field == null) return;

        if (!CanEdit())
        {
            if (field.HasFocus()) field.ReleaseFocus();
            return;
        }

        var formatted = UiUtilities.ParseAndFormatTime(text, out var seconds, out string labeled);
        if (string.IsNullOrEmpty(formatted))
        {
            _globalSignals?.EmitSignal(nameof(GlobalSignals.Log),
                $"Invalid text fade time: {text}", 1);
            SyncUiFromModel();
            if (field.HasFocus()) field.ReleaseFocus();
            return;
        }

        seconds = Math.Max(0.0, seconds);
        field.Text = formatted;
        field.TooltipText = labeled + (isIn
            ? " (fade-in at play start)"
            : " (fade-out on stop)");

        var targets = GetTextTargets();
        bool anyChange = targets.Any(t =>
        {
            double existing = isIn ? t.Component.FadeInDuration : t.Component.FadeOutDuration;
            return !Mathf.IsEqualApprox((float)existing, (float)seconds);
        });
        if (!anyChange)
        {
            if (field.HasFocus()) field.ReleaseFocus();
            return;
        }

        Record(isIn ? "Edit text fade-in" : "Edit text fade-out");
        foreach (var (_, comp) in targets)
        {
            if (isIn)
                comp.FadeInDuration = seconds;
            else
                comp.FadeOutDuration = seconds;
        }

        if (field.HasFocus()) field.ReleaseFocus();
    }

    private void SyncDurationFieldOnly()
    {
        if (_durationLineEdit == null)
            return;

        var targets = GetTextTargets();
        if (targets.Count == 0)
            return;

        _isSyncingUi = true;
        try
        {
            if (InspectorMultiEditSupport.TryGetUniformDouble(targets.Select(t => t.Component.Duration), out double dur))
            {
                if (dur <= 0)
                {
                    _durationLineEdit.Text = "0 (until stopped)";
                    _durationLineEdit.TooltipText = UiLocalizer.T("0 = stay active until stopped. Enter a time to auto-end.");
                }
                else
                {
                    _durationLineEdit.Text = UiUtilities.ParseAndFormatTime(
                        dur.ToString(CultureInfo.InvariantCulture),
                        out _,
                        out string tip);
                    _durationLineEdit.TooltipText = tip + " (0 = until stopped)";
                }
                _durationLineEdit.PlaceholderText = string.Empty;
            }
            else
            {
                _durationLineEdit.Text = string.Empty;
                _durationLineEdit.PlaceholderText = InspectorMultiEditSupport.MultiPlaceholder;
            }
        }
        finally
        {
            _isSyncingUi = false;
        }
    }

    private void OnContentChanged()
    {
        if (!CanEdit())
            return;

        string content = _contentTextEdit.Text ?? string.Empty;
        var targets = GetTextTargets();
        if (targets.All(t => (t.Component.Content ?? string.Empty) == content))
            return;

        Record("Edit text content", CoalesceKey("content"));
        foreach (var (_, comp) in targets)
            comp.Content = content;
        NotifyLiveVisuals();
    }

    private void OnContentFocusExited()
    {
        EndCoalesce(CoalesceKey("content"));
    }

    private void OnUseBbcodeToggled(bool pressed)
    {
        if (!CanEdit())
            return;
        var targets = GetTextTargets();
        if (targets.All(t => t.Component.UseBbcode == pressed))
            return;

        Record("Edit text BBCode mode");
        foreach (var (_, comp) in targets)
            comp.UseBbcode = pressed;
        NotifyLiveVisuals();
    }

    private void OnFontSelected(long index)
    {
        if (!CanEdit() || _fontOption == null)
            return;

        string fontName = _fontOption.GetItemMetadata((int)index).AsString() ?? string.Empty;
        var targets = GetTextTargets();
        if (targets.All(t => string.Equals(t.Component.FontName ?? string.Empty, fontName, StringComparison.Ordinal)))
            return;

        Record("Edit text font");
        foreach (var (_, comp) in targets)
        {
            comp.FontName = fontName;
            // Clear file override so system family selection is effective.
            if (!string.IsNullOrEmpty(fontName))
                comp.FontPath = string.Empty;
        }
        NotifyLiveVisuals();
    }

    private void OnHAlignSelected(long index)
    {
        if (!CanEdit())
            return;

        int id = _hAlignOption.GetItemId((int)index);
        var align = (HorizontalAlignment)id;
        var targets = GetTextTargets();
        if (targets.All(t => t.Component.HorizontalAlignment == align))
            return;

        Record("Edit text horizontal alignment");
        foreach (var (_, comp) in targets)
            comp.HorizontalAlignment = align;
        NotifyLiveVisuals();
    }

    private void OnVAlignSelected(long index)
    {
        if (!CanEdit())
            return;

        int id = _vAlignOption.GetItemId((int)index);
        var align = (VerticalAlignment)id;
        var targets = GetTextTargets();
        if (targets.All(t => t.Component.VerticalAlignment == align))
            return;

        Record("Edit text vertical alignment");
        foreach (var (_, comp) in targets)
            comp.VerticalAlignment = align;
        NotifyLiveVisuals();
    }

    private void OnFontSizeChanged(double value)
    {
        if (!CanEdit())
            return;

        int size = Mathf.Max(1, (int)Math.Round(value));
        var targets = GetTextTargets();
        if (targets.All(t => t.Component.FontSize == size))
            return;

        Record("Edit text font size", CoalesceKey("fontsize"));
        foreach (var (_, comp) in targets)
            comp.FontSize = size;
        NotifyLiveVisuals();
    }

    private void EndFontSizeCoalesce() => EndCoalesce(CoalesceKey("fontsize"));

    private void OnFontColorChanged(Color color)
    {
        if (!CanEdit())
            return;
        var targets = GetTextTargets();
        if (targets.All(t => t.Component.FontColor.IsEqualApprox(color)))
            return;

        Record("Edit text font colour", CoalesceKey("fontcolor"));
        foreach (var (_, comp) in targets)
            comp.FontColor = color;
        NotifyLiveVisuals();
    }

    private void EndFontColorCoalesce() => EndCoalesce(CoalesceKey("fontcolor"));

    private void OnOpacityChanged(double value)
    {
        if (!CanEdit())
            return;

        float opacity = Mathf.Clamp((float)value / 100f, 0f, 1f);
        var targets = GetTextTargets();
        if (targets.All(t => Mathf.IsEqualApprox(t.Component.Opacity, opacity)))
            return;

        Record("Edit text opacity", CoalesceKey("opacity"));
        foreach (var (_, comp) in targets)
            comp.Opacity = opacity;
        NotifyLiveVisuals();
    }

    private void EndOpacityCoalesce() => EndCoalesce(CoalesceKey("opacity"));

    private void OnMarginsChanged(double value)
    {
        if (!CanEdit())
            return;

        int margins = Mathf.Max(0, (int)Math.Round(value));
        var targets = GetTextTargets();
        if (targets.All(t => t.Component.Margins == margins))
            return;

        Record("Edit text margins", CoalesceKey("margins"));
        foreach (var (_, comp) in targets)
            comp.Margins = margins;
        NotifyLiveVisuals();
    }

    private void EndMarginsCoalesce() => EndCoalesce(CoalesceKey("margins"));

    private void OnAutowrapToggled(bool pressed)
    {
        if (!CanEdit())
            return;
        var targets = GetTextTargets();
        if (targets.All(t => t.Component.Autowrap == pressed))
            return;

        Record("Edit text wrap");
        foreach (var (_, comp) in targets)
            comp.Autowrap = pressed;
        NotifyLiveVisuals();
    }

    private void OnOutlineSizeChanged(double value)
    {
        if (!CanEdit())
            return;

        int size = Mathf.Max(0, (int)Math.Round(value));
        var targets = GetTextTargets();
        if (targets.All(t => t.Component.OutlineSize == size))
            return;

        Record("Edit text outline size", CoalesceKey("outlinesize"));
        foreach (var (_, comp) in targets)
            comp.OutlineSize = size;
        NotifyLiveVisuals();
    }

    private void EndOutlineSizeCoalesce() => EndCoalesce(CoalesceKey("outlinesize"));

    private void OnOutlineColorChanged(Color color)
    {
        if (!CanEdit())
            return;
        var targets = GetTextTargets();
        if (targets.All(t => t.Component.OutlineColor.IsEqualApprox(color)))
            return;

        Record("Edit text outline colour", CoalesceKey("outlinecolor"));
        foreach (var (_, comp) in targets)
            comp.OutlineColor = color;
        NotifyLiveVisuals();
    }

    private void EndOutlineColorCoalesce() => EndCoalesce(CoalesceKey("outlinecolor"));

    private void OnBackgroundToggled(bool pressed)
    {
        if (!CanEdit())
            return;
        var targets = GetTextTargets();
        if (targets.All(t => t.Component.BackgroundEnabled == pressed))
            return;

        Record("Edit text background");
        foreach (var (_, comp) in targets)
            comp.BackgroundEnabled = pressed;
        NotifyLiveVisuals();
    }

    private void OnBackgroundColorChanged(Color color)
    {
        if (!CanEdit())
            return;
        var targets = GetTextTargets();
        if (targets.All(t => t.Component.BackgroundColor.IsEqualApprox(color)))
            return;

        Record("Edit text background colour", CoalesceKey("bgcolor"));
        foreach (var (_, comp) in targets)
            comp.BackgroundColor = color;
        NotifyLiveVisuals();
    }

    private void EndBackgroundColorCoalesce() => EndCoalesce(CoalesceKey("bgcolor"));

    /// <summary>
    /// Re-localizes panel chrome when the UI language changes.
    /// </summary>
    /// <param name="localeCode">New locale code.</param>
    private void OnLocaleChanged(string localeCode)
    {
        if (!GodotObject.IsInstanceValid(this))
            return;
        UiLocalizer.LocalizeTree(this);
        RefreshFromFocusedCue();
    }

}
