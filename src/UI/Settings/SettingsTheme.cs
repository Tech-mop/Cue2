// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using Cue2.Services;
using Cue2.UI.Controls;
using Cue2.UI.Utilities;
using Godot;
using static Cue2.UI.Utilities.UiLocalizer;

namespace Cue2.UI.Settings;

/// <summary>
/// Cue2 Preferences → Theme: built-in and user-added Godot themes plus high/low accent colours.
/// Values persist in <see cref="UserDataManager"/> (not the showfile).
/// </summary>
/// <remarks>
/// Changing high/low updates <see cref="GlobalStyles"/> immediately and emits
/// <see cref="GlobalSignals.ThemeColorsChanged"/>. The Theme resource
/// (<c>base_theme.tres</c>) is not rewritten. User <c>.tres</c> files are referenced
/// by path and applied live.
/// </remarks>
public partial class SettingsTheme : ScrollContainer
{
    /// <summary>Stable Settings tree key (English, persisted in user data).</summary>
    public const string MenuKey = "Theme";

    private GlobalData _globalData;
    private GlobalSignals _globalSignals;
    private GlobalStyles _globalStyles;

    private OptionButton _themeOptionButton;
    private Button _themeResetButton;
    private Button _themeRemoveButton;
    private Button _addThemeButton;
    private Label _themeMissingLabel;
    private ColourButton _highColorPicker;
    private Button _highColorResetButton;
    private ColourButton _lowColorPicker;
    private Button _lowColorResetButton;
    private FileDialog _themeFileDialog;

    /// <summary>True while pushing model → controls so handlers do not re-apply.</summary>
    private bool _isSyncingUi;

    /// <inheritdoc />
    public override void _Ready()
    {
        _globalData = GetNode<GlobalData>("/root/GlobalData");
        _globalSignals = GetNode<GlobalSignals>("/root/GlobalSignals");
        _globalStyles = GetNodeOrNull<GlobalStyles>("/root/GlobalStyles");

        _themeOptionButton = GetNode<OptionButton>("%ThemeOptionButton");
        _themeResetButton = GetNode<Button>("%ThemeResetButton");
        _themeRemoveButton = GetNodeOrNull<Button>("%ThemeRemoveButton");
        _addThemeButton = GetNodeOrNull<Button>("%AddThemeButton");
        _themeMissingLabel = GetNodeOrNull<Label>("%ThemeMissingLabel");
        _highColorPicker = GetNode<ColourButton>("%HighColorPicker");
        _highColorResetButton = GetNode<Button>("%HighColorResetButton");
        _lowColorPicker = GetNode<ColourButton>("%LowColorPicker");
        _lowColorResetButton = GetNode<Button>("%LowColorResetButton");

        ComponentDefaultsUi.SetupResetButton(this, _themeResetButton, OnThemeResetPressed);
        ComponentDefaultsUi.SetupResetButton(this, _highColorResetButton, OnHighColorResetPressed);
        ComponentDefaultsUi.SetupResetButton(this, _lowColorResetButton, OnLowColorResetPressed);
        SetupRemoveButton();

        if (_themeOptionButton != null)
            _themeOptionButton.ItemSelected += OnThemeItemSelected;
        if (_addThemeButton != null)
            _addThemeButton.Pressed += OnAddThemePressed;
        if (_highColorPicker != null)
        {
            _highColorPicker.EditAlpha = false;
            _highColorPicker.ColorChanged += OnHighColorChanged;
            _highColorPicker.PopupClosed += OnHighColorPopupClosed;
        }
        if (_lowColorPicker != null)
        {
            _lowColorPicker.EditAlpha = false;
            _lowColorPicker.ColorChanged += OnLowColorChanged;
            _lowColorPicker.PopupClosed += OnLowColorPopupClosed;
        }

        if (_globalSignals != null)
            _globalSignals.LocaleChanged += OnLocaleChanged;
        VisibilityChanged += OnVisibilityChanged;

        SyncSettings();
        LocalizeTree(this);
    }

    /// <inheritdoc />
    public override void _ExitTree()
    {
        if (_globalSignals != null)
            _globalSignals.LocaleChanged -= OnLocaleChanged;
        VisibilityChanged -= OnVisibilityChanged;
        ClearThemeFileDialog();
        base._ExitTree();
    }

    /// <summary>
    /// Pulls current theme id and accent colours into the form.
    /// </summary>
    private void SyncSettings()
    {
        var udm = _globalData?.UserDataManager;
        if (udm == null)
            return;

        _isSyncingUi = true;
        try
        {
            PopulateThemeOptions(udm.ThemeId);
            if (_highColorPicker != null)
                _highColorPicker.Color = udm.ThemeHighColor;
            if (_lowColorPicker != null)
                _lowColorPicker.Color = udm.ThemeLowColor;
            UpdateAllResetButtons();
            UpdateThemeMissingUi();
            UpdateThemeRemoveButton();
        }
        finally
        {
            _isSyncingUi = false;
        }
    }

    /// <summary>
    /// Fills the theme OptionButton from built-in themes plus user-added <c>.tres</c> files.
    /// </summary>
    /// <param name="selectedId">Theme id to select.</param>
    private void PopulateThemeOptions(string selectedId)
    {
        if (_themeOptionButton == null)
            return;

        var udm = _globalData?.UserDataManager;
        string id = udm != null ? udm.ResolveThemeId(selectedId) : GlobalStyles.DefaultThemeId;
        _themeOptionButton.Clear();
        int selected = 0;
        int index = 0;
        for (int i = 0; i < GlobalStyles.BuiltInThemes.Length; i++)
        {
            var theme = GlobalStyles.BuiltInThemes[i];
            _themeOptionButton.AddItem(T(theme.DisplayName), index);
            _themeOptionButton.SetItemMetadata(index, theme.Id);
            if (string.Equals(theme.Id, id, System.StringComparison.OrdinalIgnoreCase))
                selected = index;
            index++;
        }

        if (udm != null)
        {
            foreach (var userTheme in udm.UserThemes)
            {
                if (userTheme == null)
                    continue;
                string label = userTheme.DisplayName;
                if (UserDataManager.IsUserThemeMissing(userTheme))
                    label = Tf("{0} (missing)", userTheme.DisplayName);
                _themeOptionButton.AddItem(label, index);
                _themeOptionButton.SetItemMetadata(index, userTheme.Id);
                _themeOptionButton.SetItemTooltip(index, userTheme.Path);
                if (string.Equals(userTheme.Id, id, System.StringComparison.OrdinalIgnoreCase))
                    selected = index;
                index++;
            }
        }

        _themeOptionButton.Selected = selected;
    }

    private void OnVisibilityChanged()
    {
        if (IsVisibleInTree())
            SyncSettings();
    }

    private void OnLocaleChanged(string localeCode)
    {
        if (!GodotObject.IsInstanceValid(this))
            return;
        LocalizeTree(this);
        string currentId = ReadSelectedThemeId();
        PopulateThemeOptions(currentId);
        UpdateAllResetButtons();
        UpdateThemeMissingUi();
        UpdateThemeRemoveButton();
    }

    private string ReadSelectedThemeId()
    {
        if (_themeOptionButton == null || _themeOptionButton.Selected < 0)
            return GlobalStyles.DefaultThemeId;
        string raw = _themeOptionButton.GetItemMetadata(_themeOptionButton.Selected).AsString();
        return _globalData?.UserDataManager?.ResolveThemeId(raw) ?? GlobalStyles.DefaultThemeId;
    }

    private void OnThemeItemSelected(long index)
    {
        if (_isSyncingUi || _globalData?.UserDataManager == null)
            return;

        string raw = index >= 0 && index < _themeOptionButton.ItemCount
            ? _themeOptionButton.GetItemMetadata((int)index).AsString()
            : GlobalStyles.DefaultThemeId;
        string id = _globalData.UserDataManager.ResolveThemeId(raw);
        _globalData.UserDataManager.ThemeId = id;
        UpdateThemeResetButton();
        UpdateThemeMissingUi();
        UpdateThemeRemoveButton();
    }

    private void OnThemeResetPressed()
    {
        if (_isSyncingUi || _globalData?.UserDataManager == null)
            return;
        _globalData.UserDataManager.ThemeId = GlobalStyles.DefaultThemeId;
        SyncSettings();
    }

    private void SetupRemoveButton()
    {
        if (_themeRemoveButton == null)
            return;
        try
        {
            _themeRemoveButton.Icon = GetThemeIcon("DeleteBin", "AtlasIcons");
        }
        catch
        {
            // Icon optional
        }

        _themeRemoveButton.Pressed += OnThemeRemovePressed;
    }

    private void OnThemeRemovePressed()
    {
        if (_isSyncingUi || _globalData?.UserDataManager == null)
            return;

        string id = ReadSelectedThemeId();
        if (UserDataManager.IsBuiltInThemeId(id))
            return;

        _globalData.UserDataManager.RemoveUserTheme(id);
        SyncSettings();
    }

    private void OnAddThemePressed()
    {
        if (_isSyncingUi)
            return;
        OpenThemeFileDialog();
    }

    /// <summary>
    /// Native filesystem picker for a Godot Theme <c>.tres</c>.
    /// </summary>
    private void OpenThemeFileDialog()
    {
        ClearThemeFileDialog();
        _themeFileDialog = new FileDialog
        {
            FileMode = FileDialog.FileModeEnum.OpenFile,
            Access = FileDialog.AccessEnum.Filesystem,
            Title = T("Add Theme"),
            UseNativeDialog = true
        };
        _themeFileDialog.AddFilter("*.tres ; Godot Theme");

        string startDir = _globalData?.UserDataManager?.LastThemeBrowseDirectory;
        if (string.IsNullOrEmpty(startDir) || !DirAccess.DirExistsAbsolute(startDir))
        {
            try
            {
                startDir = ProjectSettings.GlobalizePath("res://");
            }
            catch
            {
                startDir = string.Empty;
            }
        }

        if (!string.IsNullOrEmpty(startDir) && DirAccess.DirExistsAbsolute(startDir))
            _themeFileDialog.CurrentDir = startDir;

        AddChild(_themeFileDialog);
        _themeFileDialog.FileSelected += OnThemeFileSelected;
        _themeFileDialog.Canceled += ClearThemeFileDialog;
        _themeFileDialog.PopupCenteredClamped(new Vector2I(900, 600));
    }

    private void OnThemeFileSelected(string path)
    {
        ClearThemeFileDialog();
        var udm = _globalData?.UserDataManager;
        if (udm == null)
            return;

        if (!udm.TryAddUserTheme(path, out _, out string error))
        {
            _globalSignals?.EmitSignal(nameof(GlobalSignals.Log),
                string.IsNullOrWhiteSpace(error)
                    ? T("Could not add theme.")
                    : error,
                (int)LogType.Warning);
            return;
        }

        SyncSettings();
    }

    private void ClearThemeFileDialog()
    {
        if (_themeFileDialog == null)
            return;
        if (GodotObject.IsInstanceValid(_themeFileDialog))
            _themeFileDialog.QueueFree();
        _themeFileDialog = null;
    }

    private void UpdateThemeRemoveButton()
    {
        if (_themeRemoveButton == null)
            return;
        string id = _globalData?.UserDataManager?.ThemeId ?? GlobalStyles.DefaultThemeId;
        bool userTheme = !UserDataManager.IsBuiltInThemeId(id);
        _themeRemoveButton.Visible = userTheme;
        if (userTheme)
            _themeRemoveButton.TooltipText = T("Remove this theme from the list");
    }

    private void UpdateThemeMissingUi()
    {
        if (_themeMissingLabel == null)
            return;

        var udm = _globalData?.UserDataManager;
        var entry = udm?.FindUserTheme(udm.ThemeId);
        bool missing = entry != null && UserDataManager.IsUserThemeMissing(entry);
        _themeMissingLabel.Visible = missing;
        if (missing)
        {
            _themeMissingLabel.AddThemeColorOverride("font_color", GlobalStyles.Danger);
            _themeMissingLabel.Text = Tf("Theme file is missing: {0}", entry.Path);
        }
        else
        {
            _themeMissingLabel.RemoveThemeColorOverride("font_color");
        }
    }

    private void OnHighColorChanged(Color color)
    {
        if (_isSyncingUi)
            return;
        ApplyLiveAccents();
    }

    private void OnLowColorChanged(Color color)
    {
        if (_isSyncingUi)
            return;
        ApplyLiveAccents();
    }

    private void OnHighColorPopupClosed()
    {
        PersistAccents();
    }

    private void OnLowColorPopupClosed()
    {
        PersistAccents();
    }

    private void OnHighColorResetPressed()
    {
        if (_isSyncingUi || _globalData?.UserDataManager == null)
            return;

        _isSyncingUi = true;
        try
        {
            if (_highColorPicker != null)
                _highColorPicker.Color = GlobalStyles.DefaultHighColor;
        }
        finally
        {
            _isSyncingUi = false;
        }

        ApplyLiveAccents();
        PersistAccents();
    }

    private void OnLowColorResetPressed()
    {
        if (_isSyncingUi || _globalData?.UserDataManager == null)
            return;

        _isSyncingUi = true;
        try
        {
            if (_lowColorPicker != null)
                _lowColorPicker.Color = GlobalStyles.DefaultLowColor;
        }
        finally
        {
            _isSyncingUi = false;
        }

        ApplyLiveAccents();
        PersistAccents();
    }

    /// <summary>
    /// Pushes picker colours into <see cref="GlobalStyles"/> so UI updates immediately.
    /// Disk persist happens on popup close / reset.
    /// </summary>
    private void ApplyLiveAccents()
    {
        Color high = _highColorPicker != null ? _highColorPicker.Color : GlobalStyles.HighColor;
        Color low = _lowColorPicker != null ? _lowColorPicker.Color : GlobalStyles.LowColor;
        high.A = 1f;
        low.A = 1f;
        _globalStyles?.ApplyAccentColors(high, low);
        UpdateHighColorResetButton();
        UpdateLowColorResetButton();
    }

    /// <summary>
    /// Writes current picker colours to user preferences.
    /// </summary>
    private void PersistAccents()
    {
        if (_globalData?.UserDataManager == null)
            return;

        Color high = _highColorPicker != null ? _highColorPicker.Color : GlobalStyles.DefaultHighColor;
        Color low = _lowColorPicker != null ? _lowColorPicker.Color : GlobalStyles.DefaultLowColor;
        high.A = 1f;
        low.A = 1f;
        _globalData.UserDataManager.ThemeHighColor = high;
        _globalData.UserDataManager.ThemeLowColor = low;
        UpdateHighColorResetButton();
        UpdateLowColorResetButton();
    }

    private void UpdateAllResetButtons()
    {
        UpdateThemeResetButton();
        UpdateHighColorResetButton();
        UpdateLowColorResetButton();
    }

    private void UpdateThemeResetButton()
    {
        if (_themeResetButton == null || _globalData?.UserDataManager == null)
            return;

        bool atDefault = string.Equals(
            _globalData.UserDataManager.ThemeId,
            GlobalStyles.DefaultThemeId,
            System.StringComparison.OrdinalIgnoreCase);
        _themeResetButton.Visible = !atDefault;
        if (!atDefault)
            _themeResetButton.TooltipText = ResetDefaultTip(GlobalStyles.DefaultThemeDisplayName);
    }

    private void UpdateHighColorResetButton()
    {
        if (_highColorResetButton == null)
            return;
        Color current = _highColorPicker != null ? _highColorPicker.Color : GlobalStyles.HighColor;
        bool atDefault = current.IsEqualApprox(GlobalStyles.DefaultHighColor);
        _highColorResetButton.Visible = !atDefault;
        if (!atDefault)
            _highColorResetButton.TooltipText = T("Reset to default");
    }

    private void UpdateLowColorResetButton()
    {
        if (_lowColorResetButton == null)
            return;
        Color current = _lowColorPicker != null ? _lowColorPicker.Color : GlobalStyles.LowColor;
        bool atDefault = current.IsEqualApprox(GlobalStyles.DefaultLowColor);
        _lowColorResetButton.Visible = !atDefault;
        if (!atDefault)
            _lowColorResetButton.TooltipText = T("Reset to default");
    }
}
