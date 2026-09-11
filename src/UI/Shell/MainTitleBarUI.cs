// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using Godot;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Cue2.App;
using Cue2.UI.Shell;
using Cue2.Services;
using Cue2.UI.Windows;
using Cue2.UI.Utilities;
using SettingsWindow = Cue2.UI.Settings.SettingsWindow;
using static Cue2.UI.Utilities.UiLocalizer;

namespace Cue2.UI.Shell;

/// <summary>
/// Custom title bar: session title, application menu bar (File / Edit / Playback / View / Help),
/// Show Mode toggle, settings/about, and window chrome.
/// </summary>
/// <remarks>
/// Menus are Control-based dropdown panels (not Window/PopupMenu) so they stay open under
/// <c>embed_subwindows=false</c> without focus-stealing. Hover opens top-level menus and
/// File flyouts; themed to match PopupMenu styles.
/// </remarks>
public partial class MainTitleBarUI : Control
{
    private GlobalSignals _globalSignals;
    private GlobalData _globalData;
    private DisplaysManager _displaysManager;
    private AudioDevices _audioDevices;

    private SettingsWindow _settingsWindow;
    private PackedScene _settingsWindowPackedScene = SceneLoader.LoadPackedScene("uid://cfw3syjm11bd6", out _);
    private AboutWindow _aboutWindow;
    private PackedScene _aboutWindowPackedScene = SceneLoader.LoadPackedScene("uid://82ylja0fq6y0", out _);

    private Control _mainMenu;
    private Button _mainMenuButton;
    private bool _mainMenuActive;

    private Label _titleLabel;
    private SystemFont _unsavedTitleFont;
    private CheckButton _showModeButton;
    private bool _isSyncingShowModeUi;

    // Top-level menu bar buttons
    private Button _menuFileButton;
    private Button _menuEditButton;
    private Button _menuPlaybackButton;
    private Button _menuViewButton;
    private Button _menuHelpButton;

    // Dropdown panels (Control-based — not Window popups)
    private PanelContainer _fileDrop;
    private PanelContainer _editDrop;
    private PanelContainer _playbackDrop;
    private PanelContainer _viewDrop;
    private PanelContainer _helpDrop;
    private PanelContainer _recentFlyout;
    private PanelContainer _showFilesFlyout;

    private VBoxContainer _fileItems;
    private VBoxContainer _editItems;
    private VBoxContainer _playbackItems;
    private VBoxContainer _viewItems;
    private VBoxContainer _helpItems;
    private VBoxContainer _recentItems;
    private VBoxContainer _showFilesItems;

    private Control _openRecentRow;
    private Control _showFilesRow;

    private PanelContainer _activeDrop;
    private Button _activeDropButton;

    private Timer _menuHideTimer;

    // Dynamic Edit / View row handles
    private Button _undoButton;
    private Button _redoButton;
    private Button _cutButton;
    private Button _pasteButton;
    private Button _duplicateButton;
    private Button _deleteButton;
    private Button _groupButton;
    private Button _createCueButton;
    private Button _showModeMenuButton;
    private Button _muteMenuButton;
    private Button _blackoutMenuButton;
    private Button _closeDisplaysMenuButton;

    private readonly List<Control> _menuAreaControls = new();

    // Compact PopupMenu-matching metrics
    private static readonly Color MenuLabelColor = new(0.875f, 0.875f, 0.875f, 1f);
    private static readonly Color MenuHotkeyColor = new(0.7f, 0.7f, 0.7f, 0.8f);
    private static readonly Color MenuDisabledColor = new(0.4f, 0.4f, 0.4f, 0.8f);
    private static readonly Color MenuRowHoverBg = new(0.0039f, 0.231f, 0.251f, 0.65f);
    private static readonly Color MenuRowPressedBg = new(0.0039f, 0.231f, 0.251f, 0.9f);
    private const int MenuLabelFontSize = 9;
    private const int MenuHotkeyFontSize = 8;
    private const float MenuRowHeight = 20f;
    /// <summary>Floor width for empty/short menus; content always grows beyond this.</summary>
    private const float MenuMinWidth = 148f;
    private const float MenuRowPadLeft = 6f;
    private const float MenuRowPadRight = 8f;
    /// <summary>Minimum gap between title and hotkey columns (matches classic menu spacing).</summary>
    private const float MenuTitleHotkeyGap = 16f;
    private const float MenuCheckColWidth = 12f;
    private const float MenuCheckTitleGap = 4f;

    public override void _Ready()
    {
        _globalSignals = GetNode<GlobalSignals>("/root/GlobalSignals");
        _globalData = GetNode<GlobalData>("/root/GlobalData");
        _displaysManager = GetNodeOrNull<DisplaysManager>("/root/DisplaysManager");
        _audioDevices = GetNodeOrNull<AudioDevices>("/root/AudioDevices");

        _titleLabel = GetNode<Label>("%TitleLabel");
        UpdateTitle();

        _showModeButton = GetNodeOrNull<CheckButton>("%ShowModeButton");
        if (_showModeButton != null)
        {
            _showModeButton.Toggled += OnShowModeToggled;
            SyncShowModeButton();
        }
        _globalSignals.ShowModeChanged += OnShowModeChanged;
        _globalSignals.ToggleShowMode += OnToggleShowMode;
        _globalSignals.EnterEditMode += OnEnterEditMode;
        _globalSignals.EnterShowMode += OnEnterShowMode;

        _mainMenu = GetNode<Control>("%MainMenuContainer");
        _mainMenuButton = GetNode<Button>("%TitleMainMenu");

        _menuFileButton = GetNode<Button>("%MainMenuFile");
        _menuEditButton = GetNode<Button>("%MainMenuEdit");
        _menuPlaybackButton = GetNodeOrNull<Button>("%MainMenuPlayback");
        _menuViewButton = GetNode<Button>("%MainMenuView");
        _menuHelpButton = GetNodeOrNull<Button>("%MainMenuHelp");

        // Long enough to travel the gap between File and its flyout without dismissing
        _menuHideTimer = new Timer { OneShot = true, WaitTime = 0.35f };
        _menuHideTimer.Timeout += OnMenuHideTimeout;
        AddChild(_menuHideTimer);

        BuildMenus();
        WireMenuBar();

        GetNode<Button>("%TitleCue2Menu").Pressed += OnTitleCue2MenuPressed;
        GetNode<Button>("%TitleMainMenu").Toggled += OnTitleMainMenuToggled;
        GetNode<Button>("%WindowMinimizeButton").Pressed += OnWindowMinimizeButtonPressed;
        GetNode<Button>("%WindowExpandButton").Pressed += OnWindowExpandButtonPressed;
        GetNode<Button>("%ExitButton").Pressed += OnExitButtonPressed;

        GetNode<Button>("%SettingsButton").Toggled += OnSettingsButtonToggled;
        GetNode<Button>("%AboutButton").Toggled += OnAboutButtonPressed;
        GetNode<Button>("%AboutButton").TooltipText += Version.FullVersionString;

        _globalSignals.ToggleSettingsWindow += ToggleSettingsWindow;
        _globalSignals.OpenSettingsMenu += OnOpenSettingsMenu;

        // Reveal menu strip on hover of the hamburger
        GetNode<Button>("%TitleMainMenu").MouseEntered += () => _mainMenuButton.ButtonPressed = true;

        _globalSignals.NewSession += UpdateTitle;
        _globalSignals.OpenSelectedSession += _ => CallDeferred(nameof(UpdateTitle));
        _globalSignals.Save += UpdateTitle;
        _globalSignals.SaveAs += UpdateTitle;

        if (_globalData.HistoryManager != null)
        {
            _globalData.HistoryManager.HistoryChanged += OnHistoryChanged;
            _globalData.HistoryManager.HistoryRestored += OnHistoryRestored;
        }

        if (_globalSignals != null)
        {
            _globalSignals.VideoOutputControlChanged += OnVideoOutputControlChanged;
            _globalSignals.AudioMasterControlChanged += OnAudioMasterControlChanged;
        }

        SyncShowModeTooltip();
        SyncSettingsButtonTooltip();
        SyncHotkeyLabels();

        // Scene chrome (File/Edit/… labels, window tooltips) + menus when locale changes.
        LocalizeTree(this);
        if (_globalSignals != null)
            _globalSignals.LocaleChanged += OnLocaleChanged;

        // _Input only dismisses the hamburger menu; keep it off until the strip is open.
        SetProcessInput(false);
    }

    public override void _ExitTree()
    {
        if (_showModeButton != null)
            _showModeButton.Toggled -= OnShowModeToggled;
        if (_globalSignals != null)
        {
            _globalSignals.ShowModeChanged -= OnShowModeChanged;
            _globalSignals.ToggleShowMode -= OnToggleShowMode;
            _globalSignals.EnterEditMode -= OnEnterEditMode;
            _globalSignals.EnterShowMode -= OnEnterShowMode;
            _globalSignals.VideoOutputControlChanged -= OnVideoOutputControlChanged;
            _globalSignals.AudioMasterControlChanged -= OnAudioMasterControlChanged;
            _globalSignals.ToggleSettingsWindow -= ToggleSettingsWindow;
            _globalSignals.OpenSettingsMenu -= OnOpenSettingsMenu;
            _globalSignals.NewSession -= UpdateTitle;
            _globalSignals.Save -= UpdateTitle;
            _globalSignals.SaveAs -= UpdateTitle;
            _globalSignals.LocaleChanged -= OnLocaleChanged;
        }
        if (_globalData?.HistoryManager != null)
        {
            _globalData.HistoryManager.HistoryChanged -= OnHistoryChanged;
            _globalData.HistoryManager.HistoryRestored -= OnHistoryRestored;
        }
        if (_titleLabel != null)
            _titleLabel.RemoveThemeFontOverride("font");
        UiUtilities.DisposeRefCounted(_unsavedTitleFont);
        _unsavedTitleFont = null;
        base._ExitTree();
    }

    /// <summary>
    /// Re-applies translations to the title bar, menus, and chrome tooltips.
    /// </summary>
    /// <param name="localeCode">New locale (unused; strings come from TranslationServer).</param>
    private void OnLocaleChanged(string localeCode)
    {
        if (!GodotObject.IsInstanceValid(this))
            return;
        LocalizeTree(this);
        ApplyLocalizedMenuTitles();
        SyncShowModeTooltip();
        SyncSettingsButtonTooltip();
        SyncHotkeyLabels();
        // About button appends version after the base tooltip.
        var aboutBtn = GetNodeOrNull<Button>("%AboutButton");
        if (aboutBtn != null)
        {
            string baseTip = T("About Cue2");
            aboutBtn.SetMeta(MetaTooltip, baseTip);
            aboutBtn.TooltipText = baseTip + "\n" + Version.FullVersionString;
        }
    }

    /// <summary>
    /// Updates all menu row title labels from their stored English <c>base_title</c> meta.
    /// </summary>
    private void ApplyLocalizedMenuTitles()
    {
        ApplyMenuTitlesIn(_fileItems);
        ApplyMenuTitlesIn(_editItems);
        ApplyMenuTitlesIn(_playbackItems);
        ApplyMenuTitlesIn(_viewItems);
        ApplyMenuTitlesIn(_helpItems);
        ApplyMenuTitlesIn(_showFilesItems);
        ApplyMenuTitlesIn(_recentItems);
        FitAllMenusToContent();
    }

    private static void ApplyMenuTitlesIn(VBoxContainer items)
    {
        if (items == null) return;
        foreach (Node child in items.GetChildren())
        {
            if (child is not Button btn)
                continue;
            var titleLabel = GetRowTitleLabel(btn);
            if (titleLabel == null)
                continue;
            string baseTitle = btn.HasMeta("base_title")
                ? btn.GetMeta("base_title").AsString()
                : titleLabel.Text;
            if (!string.IsNullOrEmpty(baseTitle))
                titleLabel.Text = T(baseTitle);
        }
    }

    // ── Menu construction (Control panels) ──────────────────────────────────

    private void BuildMenus()
    {
        // File
        (_fileDrop, _fileItems) = CreateDropPanel("DropMenuFile");
        AddChild(_fileDrop);

        AddActionRow(_fileItems, "New Session", "NewSession", StartNewSession);
        AddActionRow(_fileItems, "Open…", "OpenSession",
            () => _globalSignals.EmitSignal(nameof(GlobalSignals.OpenSession)));
        _openRecentRow = AddFlyoutRow(_fileItems, "Open Recent", OnOpenRecentRowHover);
        AddActionRow(_fileItems, "Save", "SaveSession",
            () => _globalSignals.EmitSignal(nameof(GlobalSignals.Save)));
        AddActionRow(_fileItems, "Save As…", "SaveAsSession",
            () => _globalSignals.EmitSignal(nameof(GlobalSignals.SaveAs)));
        AddSeparator(_fileItems);
        _showFilesRow = AddFlyoutRow(_fileItems, "Show Files", OnShowFilesRowHover);
        AddSeparator(_fileItems);
        AddActionRow(_fileItems, "Quit Cue2", null, OnExitButtonPressed);

        // File flyouts
        (_recentFlyout, _recentItems) = CreateDropPanel("DropMenuRecent");
        AddChild(_recentFlyout);

        (_showFilesFlyout, _showFilesItems) = CreateDropPanel("DropMenuShowFiles");
        AddChild(_showFilesFlyout);
        AddActionRow(_showFilesItems, "Copy Media into Show Folder", null, OnShowFilesCopyMediaPressed);
        AddActionRow(_showFilesItems, "Check File Presence", null, OnShowFilesCheckPresencePressed);
        AddActionRow(_showFilesItems, "Open Show Folder", null, OnShowFilesOpenFolderPressed);

        // Edit
        (_editDrop, _editItems) = CreateDropPanel("DropMenuEdit");
        AddChild(_editDrop);

        _undoButton = AddActionRow(_editItems, "Undo", "Undo",
            () => _globalSignals.EmitSignal(nameof(GlobalSignals.Undo)));
        _redoButton = AddActionRow(_editItems, "Redo", "Redo",
            () => _globalSignals.EmitSignal(nameof(GlobalSignals.Redo)));
        AddSeparator(_editItems);
        _cutButton = AddActionRow(_editItems, "Cut", "CutSelectedCues",
            () => _globalSignals.EmitSignal(nameof(GlobalSignals.CutSelectedCues)));
        AddActionRow(_editItems, "Copy", "CopySelectedCues",
            () => _globalSignals.EmitSignal(nameof(GlobalSignals.CopySelectedCues)));
        _pasteButton = AddActionRow(_editItems, "Paste", "PasteCues",
            () => _globalSignals.EmitSignal(nameof(GlobalSignals.PasteCues)));
        AddSeparator(_editItems);
        _duplicateButton = AddActionRow(_editItems, "Duplicate", "DuplicateSelectedCues",
            () => _globalSignals.EmitSignal(nameof(GlobalSignals.DuplicateSelectedCues)));
        _deleteButton = AddActionRow(_editItems, "Delete", "DeleteCue",
            () => _globalSignals.EmitSignal(nameof(GlobalSignals.DeleteSelectedCues)));
        AddSeparator(_editItems);
        AddActionRow(_editItems, "Select All", "SelectAll",
            () => _globalSignals.EmitSignal(nameof(GlobalSignals.SelectAllCues)));
        _groupButton = AddActionRow(_editItems, "Group Selected", "GroupSelectedCues",
            () => _globalSignals.EmitSignal(nameof(GlobalSignals.GroupSelectedCues)));
        _createCueButton = AddActionRow(_editItems, "Create Cue", "CreateCue",
            () => _globalSignals.EmitSignal(nameof(GlobalSignals.CreateCue)));

        // Playback
        (_playbackDrop, _playbackItems) = CreateDropPanel("DropMenuPlayback");
        AddChild(_playbackDrop);

        AddActionRow(_playbackItems, "GO", "Go",
            () => _globalSignals.EmitSignal(nameof(GlobalSignals.Go)));
        AddActionRow(_playbackItems, "Stop All", "StopAll",
            () => _globalSignals.EmitSignal(nameof(GlobalSignals.StopAll)));
        AddActionRow(_playbackItems, "Pause All", "PauseAll",
            () => _globalSignals.EmitSignal(nameof(GlobalSignals.PauseAll)));
        AddActionRow(_playbackItems, "Resume All", "ResumeAll",
            () => _globalSignals.EmitSignal(nameof(GlobalSignals.ResumeAll)));

        // View
        (_viewDrop, _viewItems) = CreateDropPanel("DropMenuView");
        AddChild(_viewDrop);

        AddActionRow(_viewItems, "Settings…", "ToggleSettings", () => ToggleSettingsWindowOpen(true));
        AddActionRow(_viewItems, "Log…", "ToggleLog",
            () => _globalSignals.EmitSignal(nameof(GlobalSignals.ToggleLogWindow)));
        AddSeparator(_viewItems);
        AddActionRow(_viewItems, "Expand One Layer", "ExpandOneLayer",
            () => _globalSignals.EmitSignal(nameof(GlobalSignals.CuelistExpandOneLayer)));
        AddActionRow(_viewItems, "Collapse One Layer", "CollapseOneLayer",
            () => _globalSignals.EmitSignal(nameof(GlobalSignals.CuelistCollapseOneLayer)));
        AddActionRow(_viewItems, "Expand / Collapse All", "ToggleExpandAll",
            () => _globalSignals.EmitSignal(nameof(GlobalSignals.ToggleExpandAll)));
        AddSeparator(_viewItems);
        AddActionRow(_viewItems, "Fullscreen", null, OnWindowExpandButtonPressed);
        _showModeMenuButton = AddCheckRow(_viewItems, "Show Mode", "ToggleShowMode", OnShowModeMenuPressed);
        AddSeparator(_viewItems);
        _muteMenuButton = AddCheckRow(_viewItems, "Mute All Audio", null, OnMuteMenuPressed);
        _blackoutMenuButton = AddCheckRow(_viewItems, "Blackout Video", null, OnBlackoutMenuPressed);
        _closeDisplaysMenuButton = AddCheckRow(_viewItems, "Close Displays", null, OnCloseDisplaysMenuPressed);

        _muteMenuButton.TooltipText = T(
            "Runtime mute of session master audio (not saved with the show). Same as Settings → Audio.");
        _blackoutMenuButton.TooltipText = T(
            "Black out all video output layers while keeping display windows open. Runtime-only.");
        _closeDisplaysMenuButton.TooltipText = T(
            "Close/hide all house display windows without clearing canvas topology. Runtime-only.");

        // Help
        (_helpDrop, _helpItems) = CreateDropPanel("DropMenuHelp");
        AddChild(_helpDrop);

        AddActionRow(_helpItems, "About Cue2…", null, OpenAboutWindow);
        AddActionRow(_helpItems, "Check for Updates…", null, OpenUpdatesSettings);
        AddSeparator(_helpItems);
        AddActionRow(_helpItems, "Website…", null, () => OS.ShellOpen("https://www.cue2.live/"));
        AddActionRow(_helpItems, "Documentation…", null, () => OS.ShellOpen("https://docs.cue2.live/"));

        RegisterMenuArea(_fileDrop, _editDrop, _playbackDrop, _viewDrop, _helpDrop,
            _recentFlyout, _showFilesFlyout);
        RegisterMenuArea(_menuFileButton, _menuEditButton, _menuPlaybackButton,
            _menuViewButton, _menuHelpButton);

        // Size every dropdown to its widest title+hotkey row (future-proof for rebinds).
        FitAllMenusToContent();
    }

    private void RegisterMenuArea(params Control[] controls)
    {
        foreach (var c in controls)
        {
            if (c == null) continue;
            _menuAreaControls.Add(c);
            c.MouseEntered += CancelMenuHide;
            c.MouseExited += ScheduleMenuHide;
        }
    }

    private (PanelContainer panel, VBoxContainer items) CreateDropPanel(string name)
    {
        var panel = new PanelContainer
        {
            Name = name,
            Visible = false,
            MouseFilter = Control.MouseFilterEnum.Stop
        };

        // Match PopupMenu panel look from the project theme (compact content margins)
        var style = GetThemeStylebox("panel", "PopupMenu");
        if (style != null)
        {
            var dup = (StyleBox)style.Duplicate();
            if (dup is StyleBoxFlat flat)
            {
                flat.ContentMarginLeft = 2;
                flat.ContentMarginTop = 2;
                flat.ContentMarginRight = 2;
                flat.ContentMarginBottom = 2;
            }
            panel.AddThemeStyleboxOverride("panel", dup);
        }
        else
        {
            panel.AddThemeStyleboxOverride("panel", new StyleBoxFlat
            {
                BgColor = new Color(0.118f, 0.118f, 0.118f, 1f),
                ContentMarginLeft = 2,
                ContentMarginTop = 2,
                ContentMarginRight = 2,
                ContentMarginBottom = 2
            });
        }

        var margin = new MarginContainer
        {
            MouseFilter = Control.MouseFilterEnum.Stop
        };
        margin.AddThemeConstantOverride("margin_left", 1);
        margin.AddThemeConstantOverride("margin_top", 1);
        margin.AddThemeConstantOverride("margin_right", 1);
        margin.AddThemeConstantOverride("margin_bottom", 1);

        var vbox = new VBoxContainer
        {
            Name = "Items",
            MouseFilter = Control.MouseFilterEnum.Stop
        };
        vbox.AddThemeConstantOverride("separation", 0);

        margin.AddChild(vbox);
        panel.AddChild(margin);
        return (panel, vbox);
    }

    private Button AddActionRow(VBoxContainer parent, string title, string inputAction, Action onPressed)
    {
        var btn = CreateMenuRow(title, inputAction, isCheck: false, isFlyout: false);
        btn.Pressed += () =>
        {
            CloseMenuBarAfterAction();
            onPressed?.Invoke();
        };
        // Dismiss File flyouts when hovering a top-level plain row — but never when the
        // row itself lives inside a flyout (Show Files items used to call HideFlyouts
        // and immediately closed the submenu under the pointer).
        btn.MouseEntered += OnPlainMenuRowHoverMaybeCloseFlyouts;
        parent.AddChild(btn);
        return btn;
    }

    /// <summary>
    /// Closes Open Recent / Show Files flyouts when hovering a main-menu action row.
    /// Skips when the pointer is already over a flyout (flyout content rows).
    /// </summary>
    private void OnPlainMenuRowHoverMaybeCloseFlyouts()
    {
        if (IsPointerOverAnyFlyout())
            return;
        HideFlyouts();
    }

    private bool IsPointerOverAnyFlyout()
    {
        var mouse = GetViewport().GetMousePosition();
        if (_recentFlyout != null && GodotObject.IsInstanceValid(_recentFlyout) &&
            _recentFlyout.Visible && _recentFlyout.GetGlobalRect().Grow(2f).HasPoint(mouse))
            return true;
        if (_showFilesFlyout != null && GodotObject.IsInstanceValid(_showFilesFlyout) &&
            _showFilesFlyout.Visible && _showFilesFlyout.GetGlobalRect().Grow(2f).HasPoint(mouse))
            return true;
        return false;
    }

    private Control AddFlyoutRow(VBoxContainer parent, string title, Action onHover)
    {
        // Chevron lives in the right-hand (hotkey) column, matching PopupMenu submenu affordance
        var btn = CreateMenuRow(title, inputAction: null, isCheck: false, isFlyout: true);
        btn.Pressed += () => onHover?.Invoke();
        btn.MouseEntered += () =>
        {
            CancelMenuHide();
            onHover?.Invoke();
        };
        parent.AddChild(btn);
        return btn;
    }

    private Button AddCheckRow(VBoxContainer parent, string title, string inputAction, Action onPressed)
    {
        var btn = CreateMenuRow(title, inputAction, isCheck: true, isFlyout: false);
        btn.ToggleMode = true;
        btn.Pressed += () =>
        {
            // Keep View menu open for check toggles
            CancelMenuHide();
            onPressed?.Invoke();
        };
        btn.MouseEntered += HideFlyouts;
        parent.AddChild(btn);
        return btn;
    }

    /// <summary>
    /// Compact menu row: left title, right hotkey/chevron — PopupMenu layout.
    /// Title and hotkey both size to their text; the menu panel grows to fit the widest row
    /// so labels are never clipped when shortcuts are long or rebound.
    /// Hover/press feedback via a ColorRect (more reliable than Button theme styles alone).
    /// </summary>
    private Button CreateMenuRow(string title, string inputAction, bool isCheck, bool isFlyout)
    {
        var btn = new Button
        {
            Text = string.Empty, // labels drawn by child HBox (title | hotkey)
            FocusMode = Control.FocusModeEnum.None,
            MouseFilter = Control.MouseFilterEnum.Stop,
            Flat = true,
            ClipText = false,
            CustomMinimumSize = new Vector2(MenuMinWidth, MenuRowHeight)
        };

        ApplyCompactMenuButtonStyles(btn);

        // Full-rect highlight behind labels (theme hover can fail with empty-text flat buttons)
        var highlight = new ColorRect
        {
            Name = "HoverHighlight",
            Color = Colors.Transparent,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            ShowBehindParent = false
        };
        highlight.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        btn.AddChild(highlight);

        var hbox = new HBoxContainer
        {
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        hbox.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        hbox.OffsetLeft = MenuRowPadLeft;
        hbox.OffsetRight = -MenuRowPadRight;
        hbox.OffsetTop = 1;
        hbox.OffsetBottom = -1;
        // Gap between check and title only; title↔hotkey gap is the expand spacer.
        hbox.AddThemeConstantOverride("separation", (int)MenuCheckTitleGap);

        // Optional check mark column (fixed width so labels stay aligned)
        Label checkLabel = null;
        if (isCheck)
        {
            checkLabel = new Label
            {
                Text = " ",
                MouseFilter = Control.MouseFilterEnum.Ignore,
                CustomMinimumSize = new Vector2(MenuCheckColWidth, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            checkLabel.AddThemeFontSizeOverride("font_size", MenuLabelFontSize);
            checkLabel.AddThemeColorOverride("font_color", MenuLabelColor);
            hbox.AddChild(checkLabel);
        }

        // Title: natural width, never clip. Menu grows via FitMenuItemsToContent.
        // English title is the catalog key; display is translated.
        var titleLabel = new Label
        {
            Text = T(title),
            MouseFilter = Control.MouseFilterEnum.Ignore,
            SizeFlagsHorizontal = Control.SizeFlags.ShrinkBegin,
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Left,
            ClipText = false,
            TextOverrunBehavior = TextServer.OverrunBehavior.NoTrimming
        };
        titleLabel.AddThemeFontSizeOverride("font_size", MenuLabelFontSize);
        titleLabel.AddThemeColorOverride("font_color", MenuLabelColor);

        // Flexible middle column keeps the hotkey right-aligned without stealing title width.
        var spacer = new Control
        {
            Name = "RowSpacer",
            MouseFilter = Control.MouseFilterEnum.Ignore,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(MenuTitleHotkeyGap, 0)
        };

        var hotkeyLabel = new Label
        {
            Text = isFlyout ? "›" : string.Empty,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            SizeFlagsHorizontal = Control.SizeFlags.ShrinkEnd,
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
            ClipText = false,
            TextOverrunBehavior = TextServer.OverrunBehavior.NoTrimming
        };
        hotkeyLabel.AddThemeFontSizeOverride("font_size", MenuHotkeyFontSize);
        hotkeyLabel.AddThemeColorOverride("font_color", MenuHotkeyColor);

        if (!string.IsNullOrEmpty(inputAction) && !isFlyout)
        {
            btn.SetMeta("input_action", inputAction);
            string hk = GlobalData.ParseHotkey(inputAction);
            if (!string.IsNullOrEmpty(hk))
                hotkeyLabel.Text = hk;
        }

        btn.SetMeta("base_title", title);
        if (isCheck)
            btn.SetMeta("check_title", title);

        hbox.AddChild(titleLabel);
        hbox.AddChild(spacer);
        hbox.AddChild(hotkeyLabel);
        btn.AddChild(hbox);

        // Keep refs for enable/check/hotkey sync
        btn.SetMeta("title_label", titleLabel);
        btn.SetMeta("hotkey_label", hotkeyLabel);
        btn.SetMeta("highlight", highlight);
        btn.SetMeta("has_check", isCheck);
        if (checkLabel != null)
            btn.SetMeta("check_label", checkLabel);

        WireRowHighlight(btn, highlight);

        // Flyout parent rows / flyout items: keep File menu alive while the pointer is here
        btn.MouseEntered += CancelMenuHide;

        return btn;
    }

    /// <summary>
    /// Sizes every menu panel to the widest row so titles and hotkeys never clip.
    /// Safe after rebinding InputMap (call from <see cref="SyncHotkeyLabels"/>).
    /// </summary>
    private void FitAllMenusToContent()
    {
        FitMenuItemsToContent(_fileItems);
        FitMenuItemsToContent(_editItems);
        FitMenuItemsToContent(_playbackItems);
        FitMenuItemsToContent(_viewItems);
        FitMenuItemsToContent(_helpItems);
        FitMenuItemsToContent(_showFilesItems);
        // Recent is rebuilt on hover; FitMenuItemsToContent is called from PopulateRecentFlyout.
    }

    /// <summary>
    /// Sets each row's min width to the max content width in this menu (classic OS menu behavior).
    /// </summary>
    private static void FitMenuItemsToContent(VBoxContainer items)
    {
        if (items == null || !GodotObject.IsInstanceValid(items))
            return;

        float maxW = MenuMinWidth;
        foreach (Node child in items.GetChildren())
        {
            if (child is Button btn)
                maxW = Math.Max(maxW, MeasureMenuRowContentWidth(btn));
        }

        foreach (Node child in items.GetChildren())
        {
            if (child is Button btn)
                btn.CustomMinimumSize = new Vector2(maxW, MenuRowHeight);
        }

        // Panel grows with its children; ResetSize on open picks this up.
        if (items.GetParent()?.GetParent() is PanelContainer panel)
            panel.CustomMinimumSize = new Vector2(maxW, 0);
    }

    /// <summary>
    /// Natural pixel width needed for a row: padding + optional check + title + gap + hotkey.
    /// </summary>
    private static float MeasureMenuRowContentWidth(Button btn)
    {
        if (btn == null)
            return MenuMinWidth;

        float w = MenuRowPadLeft + MenuRowPadRight;

        bool hasCheck = btn.HasMeta("has_check") && btn.GetMeta("has_check").AsBool();
        if (hasCheck)
            w += MenuCheckColWidth + MenuCheckTitleGap;

        var title = GetRowTitleLabel(btn);
        var hotkey = GetRowHotkeyLabel(btn);
        float titleW = MeasureLabelTextWidth(title);
        float hotkeyW = MeasureLabelTextWidth(hotkey);

        w += titleW;
        // Always reserve the title↔hotkey gap so columns stay aligned when some rows lack shortcuts.
        w += MenuTitleHotkeyGap;
        w += hotkeyW;

        // Small safety margin for font hinting / subpixel rounding.
        return w + 2f;
    }

    /// <summary>
    /// Measures rendered text width for a themed Label (0 when empty/null).
    /// </summary>
    private static float MeasureLabelTextWidth(Label label)
    {
        if (label == null || string.IsNullOrEmpty(label.Text))
            return 0f;

        var font = label.GetThemeFont("font") ?? ThemeDB.FallbackFont;
        int fontSize = label.HasThemeFontSizeOverride("font_size")
            ? label.GetThemeFontSize("font_size")
            : label.GetThemeDefaultFontSize();
        if (font == null)
            return label.Text.Length * fontSize * 0.5f;

        return font.GetStringSize(
            label.Text,
            HorizontalAlignment.Left,
            -1,
            fontSize).X;
    }

    /// <summary>
    /// Explicit hover + press row highlight (ColorRect).
    /// </summary>
    private static void WireRowHighlight(Button btn, ColorRect highlight)
    {
        if (btn == null || highlight == null) return;

        btn.MouseEntered += () =>
        {
            if (btn.Disabled) return;
            if (btn.ButtonPressed && !btn.ToggleMode)
                highlight.Color = MenuRowPressedBg;
            else
                highlight.Color = MenuRowHoverBg;
        };
        btn.MouseExited += () =>
        {
            // Toggle rows keep a subtle pressed look when checked
            if (btn.ToggleMode && btn.ButtonPressed && !btn.Disabled)
                highlight.Color = MenuRowHoverBg * new Color(1, 1, 1, 0.45f);
            else
                highlight.Color = Colors.Transparent;
        };
        btn.ButtonDown += () =>
        {
            if (btn.Disabled) return;
            highlight.Color = MenuRowPressedBg;
        };
        btn.ButtonUp += () =>
        {
            if (btn.Disabled)
            {
                highlight.Color = Colors.Transparent;
                return;
            }
            // Still over the row after click?
            if (btn.GetGlobalRect().HasPoint(btn.GetViewport().GetMousePosition()))
                highlight.Color = MenuRowHoverBg;
            else if (btn.ToggleMode && btn.ButtonPressed)
                highlight.Color = MenuRowHoverBg * new Color(1, 1, 1, 0.45f);
            else
                highlight.Color = Colors.Transparent;
        };
    }

    /// <summary>
    /// Flat button styles with equal padding so layout does not shift on hover.
    /// Background is handled by the row ColorRect highlight.
    /// </summary>
    private static void ApplyCompactMenuButtonStyles(Button btn)
    {
        var empty = new StyleBoxEmpty();
        empty.ContentMarginLeft = 0;
        empty.ContentMarginTop = 0;
        empty.ContentMarginRight = 0;
        empty.ContentMarginBottom = 0;

        btn.AddThemeStyleboxOverride("normal", empty);
        btn.AddThemeStyleboxOverride("hover", empty);
        btn.AddThemeStyleboxOverride("pressed", empty);
        btn.AddThemeStyleboxOverride("hover_pressed", empty);
        btn.AddThemeStyleboxOverride("focus", empty);
        btn.AddThemeStyleboxOverride("disabled", empty);
    }

    private static void AddSeparator(VBoxContainer parent)
    {
        var sep = new HSeparator
        {
            MouseFilter = Control.MouseFilterEnum.Ignore,
            CustomMinimumSize = new Vector2(0, 6)
        };
        parent.AddChild(sep);
    }

    private static Label GetRowTitleLabel(Button btn)
    {
        if (btn == null || !btn.HasMeta("title_label")) return null;
        return btn.GetMeta("title_label").AsGodotObject() as Label;
    }

    private static Label GetRowHotkeyLabel(Button btn)
    {
        if (btn == null || !btn.HasMeta("hotkey_label")) return null;
        return btn.GetMeta("hotkey_label").AsGodotObject() as Label;
    }

    private static Label GetRowCheckLabel(Button btn)
    {
        if (btn == null || !btn.HasMeta("check_label")) return null;
        return btn.GetMeta("check_label").AsGodotObject() as Label;
    }

    private void WireMenuBar()
    {
        WireMenuButton(_menuFileButton, _fileDrop);
        WireMenuButton(_menuEditButton, _editDrop);
        WireMenuButton(_menuPlaybackButton, _playbackDrop);
        WireMenuButton(_menuViewButton, _viewDrop);
        WireMenuButton(_menuHelpButton, _helpDrop);
    }

    private void WireMenuButton(Button button, PanelContainer drop)
    {
        if (button == null || drop == null) return;

        // Hover opens when the menu strip is visible (no click required)
        button.MouseEntered += () =>
        {
            if (!_mainMenuActive) return;
            CancelMenuHide();
            OpenDrop(button, drop);
        };
        button.Pressed += () =>
        {
            if (!_mainMenuActive)
                _mainMenuButton.ButtonPressed = true;
            CancelMenuHide();
            OpenDrop(button, drop);
        };
    }

    /// <summary>
    /// Shows <paramref name="drop"/> under <paramref name="button"/>; hides other top-level drops.
    /// </summary>
    private void OpenDrop(Button button, PanelContainer drop)
    {
        if (button == null || drop == null) return;

        // Already open under this button — keep it (avoid flicker / hide-on-reenter)
        if (_activeDrop == drop && drop.Visible && _activeDropButton == button)
        {
            CancelMenuHide();
            return;
        }

        HideAllDropsExcept(drop);
        if (drop != _fileDrop)
            HideFlyouts();

        RefreshDropState(drop);

        // Position in title-bar local space (Control panels, not screen Windows)
        drop.Show();
        drop.ResetSize();
        var btnRect = button.GetGlobalRect();
        var selfRect = GetGlobalRect();
        float localX = btnRect.Position.X - selfRect.Position.X;
        float localY = btnRect.Position.Y + btnRect.Size.Y - selfRect.Position.Y;
        drop.Position = new Vector2(localX, localY);
        drop.MoveToFront();

        _activeDrop = drop;
        _activeDropButton = button;
        CancelMenuHide();
    }

    private void HideAllDropsExcept(PanelContainer keep)
    {
        void HideIfOther(PanelContainer p)
        {
            if (p != null && p != keep)
                p.Hide();
        }

        HideIfOther(_fileDrop);
        HideIfOther(_editDrop);
        HideIfOther(_playbackDrop);
        HideIfOther(_viewDrop);
        HideIfOther(_helpDrop);
    }

    private void HideAllDrops()
    {
        _fileDrop?.Hide();
        _editDrop?.Hide();
        _playbackDrop?.Hide();
        _viewDrop?.Hide();
        _helpDrop?.Hide();
        HideFlyouts();
        _activeDrop = null;
        _activeDropButton = null;
    }

    private void HideFlyouts()
    {
        _recentFlyout?.Hide();
        _showFilesFlyout?.Hide();
    }

    private void OnOpenRecentRowHover()
    {
        if (_fileDrop == null || !_fileDrop.Visible) return;
        CancelMenuHide();
        _showFilesFlyout?.Hide();
        PopulateRecentFlyout();
        PositionFlyout(_recentFlyout, _openRecentRow);
        _recentFlyout.Show();
        _recentFlyout.MoveToFront();
        // Keep File menu above the main window content but under the flyout
        _fileDrop.MoveToFront();
        _recentFlyout.MoveToFront();
    }

    private void OnShowFilesRowHover()
    {
        if (_fileDrop == null || !_fileDrop.Visible) return;
        CancelMenuHide();
        _recentFlyout?.Hide();
        PositionFlyout(_showFilesFlyout, _showFilesRow);
        _showFilesFlyout.Show();
        _showFilesFlyout.MoveToFront();
        _fileDrop.MoveToFront();
        _showFilesFlyout.MoveToFront();
    }

    private void PositionFlyout(PanelContainer flyout, Control anchorRow)
    {
        if (flyout == null || _fileDrop == null || anchorRow == null) return;

        flyout.ResetSize();
        // Overlap parent by a few px so the pointer never leaves both menus while traveling
        float x = _fileDrop.Position.X + _fileDrop.Size.X - 4f;
        var rowGlobal = anchorRow.GetGlobalRect();
        var selfGlobal = GetGlobalRect();
        float y = rowGlobal.Position.Y - selfGlobal.Position.Y;
        flyout.Position = new Vector2(x, y);
    }

    private void PopulateRecentFlyout()
    {
        if (_recentItems == null) return;

        // Free immediately so a second hover in the same frame does not stack rows
        while (_recentItems.GetChildCount() > 0)
        {
            var child = _recentItems.GetChild(0);
            _recentItems.RemoveChild(child);
            child.Free();
        }

        var recents = _globalData?.UserDataManager?.GetRecentShowFiles();
        if (recents == null || recents.Count == 0)
        {
            var empty = new Label
            {
                Text = "(No recent files)",
                HorizontalAlignment = HorizontalAlignment.Left
            };
            empty.AddThemeColorOverride("font_color", new Color(0.55f, 0.55f, 0.55f));
            empty.AddThemeFontSizeOverride("font_size", 11);
            _recentItems.AddChild(empty);
            return;
        }

        const int maxToShow = 10;
        int n = 0;
        foreach (var path in recents)
        {
            if (n >= maxToShow) break;
            string displayName = Path.GetFileName(path);
            if (string.IsNullOrEmpty(displayName)) displayName = path;
            if (displayName.Length > 40)
                displayName = displayName.Substring(0, 37) + "…";

            string captured = path;
            var btn = CreateMenuRow(displayName, null, isCheck: false, isFlyout: false);
            btn.TooltipText = path;
            btn.CustomMinimumSize = new Vector2(200, MenuRowHeight);
            btn.Pressed += () =>
            {
                CloseMenuBarAfterAction();
                _globalSignals.EmitSignal(nameof(GlobalSignals.OpenSelectedSession), captured);
            };
            _recentItems.AddChild(btn);
            n++;
        }

        AddSeparator(_recentItems);
        var clearBtn = CreateMenuRow("Clear Recent", null, isCheck: false, isFlyout: false);
        clearBtn.Pressed += () =>
        {
            _globalData?.UserDataManager?.ClearRecentShowFiles();
            PopulateRecentFlyout();
        };
        _recentItems.AddChild(clearBtn);

        // Grow flyout to longest recent path label + Clear row (no clipping).
        FitMenuItemsToContent(_recentItems);
    }

    private void RefreshDropState(PanelContainer drop)
    {
        if (drop == _editDrop)
            RefreshEditMenuState();
        else if (drop == _viewDrop)
            RefreshViewMenuState();
        else if (drop == _fileDrop)
            PopulateRecentFlyout(); // pre-build so first hover is instant
    }

    private void RefreshEditMenuState()
    {
        var history = _globalData?.HistoryManager;
        bool locked = _globalData?.Settings?.IsCueEditingLocked == true;

        SetMenuRowDisabled(_undoButton, history == null || !history.CanUndo);
        SetMenuRowDisabled(_redoButton, history == null || !history.CanRedo);
        SetMenuRowDisabled(_cutButton, locked);
        SetMenuRowDisabled(_pasteButton, locked);
        SetMenuRowDisabled(_duplicateButton, locked);
        SetMenuRowDisabled(_deleteButton, locked);
        SetMenuRowDisabled(_groupButton, locked);
        SetMenuRowDisabled(_createCueButton, locked);
    }

    /// <summary>
    /// Disables a row and dims its title/hotkey labels (flat buttons don't grey children alone).
    /// </summary>
    private static void SetMenuRowDisabled(Button btn, bool disabled)
    {
        if (btn == null) return;
        btn.Disabled = disabled;
        var color = disabled ? MenuDisabledColor : MenuLabelColor;
        var hotkeyColor = disabled ? MenuDisabledColor : MenuHotkeyColor;
        var title = GetRowTitleLabel(btn);
        var hotkey = GetRowHotkeyLabel(btn);
        var check = GetRowCheckLabel(btn);
        if (title != null) title.AddThemeColorOverride("font_color", color);
        if (hotkey != null) hotkey.AddThemeColorOverride("font_color", hotkeyColor);
        if (check != null) check.AddThemeColorOverride("font_color", color);

        if (disabled && btn.HasMeta("highlight") &&
            btn.GetMeta("highlight").AsGodotObject() is ColorRect hl)
            hl.Color = Colors.Transparent;
    }

    private void RefreshViewMenuState()
    {
        bool showMode = _globalData?.Settings?.ShowMode == true;
        bool muted = _audioDevices?.SessionMasterMuted ?? false;
        bool blackout = DisplaysManager.OutputBlackout;
        bool closed = DisplaysManager.OutputDisabled;

        SetCheckButtonState(_showModeMenuButton, showMode);
        SetCheckButtonState(_muteMenuButton, muted);
        SetCheckButtonState(_blackoutMenuButton, blackout);
        SetCheckButtonState(_closeDisplaysMenuButton, closed);

        SetMenuRowDisabled(_blackoutMenuButton, closed);
    }

    private static void SetCheckButtonState(Button btn, bool pressed)
    {
        if (btn == null) return;
        btn.SetPressedNoSignal(pressed);

        var checkLabel = GetRowCheckLabel(btn);
        if (checkLabel != null)
            checkLabel.Text = pressed ? "✓" : " ";

        // Keep title pure (hotkey stays in its own column)
        var titleLabel = GetRowTitleLabel(btn);
        if (titleLabel != null && btn.HasMeta("check_title"))
            titleLabel.Text = btn.GetMeta("check_title").AsString();
    }

    private void OnShowModeMenuPressed()
    {
        var settings = _globalData?.Settings;
        if (settings == null) return;
        settings.SetShowMode(!settings.ShowMode);
        RefreshViewMenuState();
    }

    private void OnMuteMenuPressed()
    {
        if (_audioDevices == null) return;
        _audioDevices.SetSessionMasterMuted(!_audioDevices.SessionMasterMuted);
        RefreshViewMenuState();
    }

    private void OnBlackoutMenuPressed()
    {
        if (_displaysManager == null || DisplaysManager.OutputDisabled) return;
        _displaysManager.SetOutputBlackout(!DisplaysManager.OutputBlackout);
        RefreshViewMenuState();
    }

    private void OnCloseDisplaysMenuPressed()
    {
        if (_displaysManager == null) return;
        _displaysManager.SetOutputDisabled(!DisplaysManager.OutputDisabled);
        RefreshViewMenuState();
    }

    // ── Menu hide timing ────────────────────────────────────────────────────

    private void ScheduleMenuHide()
    {
        // While a File flyout is open, don't arm hide if the pointer is still near File/flyout
        if (IsMouseOverMenuArea())
            return;
        _menuHideTimer?.Stop();
        _menuHideTimer?.Start();
    }

    private void CancelMenuHide()
    {
        _menuHideTimer?.Stop();
    }

    private void OnMenuHideTimeout()
    {
        if (IsMouseOverMenuArea())
            return;
        HideAllDrops();
    }

    private bool IsMouseOverMenuArea()
    {
        var mouse = GetViewport().GetMousePosition();
        foreach (var c in _menuAreaControls)
        {
            if (c == null || !GodotObject.IsInstanceValid(c) || !c.Visible)
                continue;
            if (c.GetGlobalRect().Grow(2f).HasPoint(mouse))
                return true;
        }

        // Gap between File dropdown and an open flyout (Open Recent / Show Files)
        if (IsMouseInFileFlyoutBridge(mouse))
            return true;

        return false;
    }

    /// <summary>
    /// True while the cursor is in the strip between the File menu and an open flyout.
    /// Prevents the File menu from closing while moving into Open Recent / Show Files.
    /// </summary>
    private bool IsMouseInFileFlyoutBridge(Vector2 mouse)
    {
        if (_fileDrop == null || !_fileDrop.Visible)
            return false;

        PanelContainer flyout = null;
        if (_recentFlyout != null && _recentFlyout.Visible)
            flyout = _recentFlyout;
        else if (_showFilesFlyout != null && _showFilesFlyout.Visible)
            flyout = _showFilesFlyout;

        if (flyout == null)
            return false;

        var fileR = _fileDrop.GetGlobalRect().Grow(2f);
        var flyR = flyout.GetGlobalRect().Grow(2f);

        // Horizontal corridor from File's right edge to flyout's left edge
        float left = Math.Min(fileR.Position.X + fileR.Size.X, flyR.Position.X) - 6f;
        float right = Math.Max(fileR.Position.X + fileR.Size.X, flyR.Position.X) + 6f;
        float top = Math.Min(fileR.Position.Y, flyR.Position.Y) - 4f;
        float bottom = Math.Max(fileR.Position.Y + fileR.Size.Y, flyR.Position.Y + flyR.Size.Y) + 4f;

        return mouse.X >= left && mouse.X <= right && mouse.Y >= top && mouse.Y <= bottom;
    }

    private void CloseMenuBarAfterAction()
    {
        HideAllDrops();
        if (_mainMenuButton != null)
            _mainMenuButton.ButtonPressed = false;
    }

    // ── Show Files ──────────────────────────────────────────────────────────

    private void OnShowFilesCopyMediaPressed()
    {
        if (string.IsNullOrEmpty(_globalData?.SessionDir) || string.IsNullOrEmpty(_globalData.SessionPath))
        {
            _globalSignals.EmitSignal(nameof(GlobalSignals.Log),
                "Cannot copy media: save the show first so a show folder exists.", 1);
            return;
        }

        var backup = GetNodeOrNull<MediaBackupManager>("/root/MediaBackupManager");
        if (backup == null)
        {
            _globalSignals.EmitSignal(nameof(GlobalSignals.Log), "Media backup service unavailable.", 2);
            return;
        }

        _globalSignals.EmitSignal(nameof(GlobalSignals.Log), "Copying media files into show folder…", 0);
        backup.EnqueueShowMediaBackup(force: true);
    }

    private void OnShowFilesCheckPresencePressed()
    {
        var health = GetNodeOrNull<MediaHealthService>("/root/MediaHealthService");
        if (health == null)
        {
            _globalSignals.EmitSignal(nameof(GlobalSignals.Log), "Media health service unavailable.", 2);
            return;
        }

        _globalSignals.EmitSignal(nameof(GlobalSignals.Log), "Checking file presence…", 0);
        health.CheckAllMediaNow();
    }

    private void OnShowFilesOpenFolderPressed()
    {
        string dir = _globalData?.SessionDir;
        if (string.IsNullOrEmpty(dir) && !string.IsNullOrEmpty(_globalData?.SessionPath))
            dir = _globalData.SessionPath.GetBaseDir();

        if (string.IsNullOrEmpty(dir))
        {
            _globalSignals.EmitSignal(nameof(GlobalSignals.Log),
                "No show folder open. Save the show first.", 1);
            return;
        }

        try
        {
            string full = Path.GetFullPath(dir);
            if (!Directory.Exists(full))
            {
                _globalSignals.EmitSignal(nameof(GlobalSignals.Log),
                    $"Show folder does not exist: {full}", 2);
                return;
            }

            Error err = OS.ShellOpen(full);
            if (err != Error.Ok)
            {
                _globalSignals.EmitSignal(nameof(GlobalSignals.Log),
                    $"Failed to open show folder ({err}): {full}", 2);
                return;
            }

            _globalSignals.EmitSignal(nameof(GlobalSignals.Log), $"Opened show folder: {full}", 0);
        }
        catch (Exception ex)
        {
            _globalSignals.EmitSignal(nameof(GlobalSignals.Log),
                $"Failed to open show folder: {ex.Message}", 2);
            GD.PrintErr($"MainTitleBarUI:OnShowFilesOpenFolderPressed - {ex.Message}");
        }
    }

    // ── Hotkeys / state sync ────────────────────────────────────────────────

    private void SyncHotkeyLabels()
    {
        SyncRowsHotkeys(_fileItems);
        SyncRowsHotkeys(_editItems);
        SyncRowsHotkeys(_playbackItems);
        SyncRowsHotkeys(_viewItems);
        RefreshViewMenuState();
        // Rebinding InputMap can lengthen shortcuts (e.g. multi-modifier combos) —
        // grow menus so titles are never clipped by the hotkey column.
        FitAllMenusToContent();
    }

    private static void SyncRowsHotkeys(VBoxContainer items)
    {
        if (items == null) return;
        foreach (Node child in items.GetChildren())
        {
            if (child is not Button btn || !btn.HasMeta("input_action"))
                continue;

            string action = btn.GetMeta("input_action").AsString();
            string hk = GlobalData.ParseHotkey(action);
            var hotkeyLabel = GetRowHotkeyLabel(btn);
            if (hotkeyLabel != null)
                hotkeyLabel.Text = hk ?? string.Empty;

            var titleLabel = GetRowTitleLabel(btn);
            if (titleLabel != null)
            {
                string baseTitle = btn.HasMeta("check_title")
                    ? btn.GetMeta("check_title").AsString()
                    : btn.HasMeta("base_title")
                        ? btn.GetMeta("base_title").AsString()
                        : titleLabel.Text;
                titleLabel.Text = T(baseTitle);
            }
        }
    }

    private void OnHistoryChanged()
    {
        UpdateTitle();
        if (_editDrop != null && _editDrop.Visible)
            RefreshEditMenuState();
    }

    private void OnHistoryRestored(int scope)
    {
        if (scope != (int)HistoryManager.HistoryScope.Settings) return;
        SyncHotkeyLabels();
        SyncShowModeTooltip();
        SyncSettingsButtonTooltip();
        if (_editDrop != null && _editDrop.Visible)
            RefreshEditMenuState();
        if (_viewDrop != null && _viewDrop.Visible)
            RefreshViewMenuState();
    }

    private void OnVideoOutputControlChanged(bool disabled, bool blackout)
    {
        if (_viewDrop != null && _viewDrop.Visible)
            RefreshViewMenuState();
    }

    private void OnAudioMasterControlChanged(float linear, bool muted)
    {
        if (_viewDrop != null && _viewDrop.Visible)
            RefreshViewMenuState();
    }

    // ── Show mode / title / chrome ──────────────────────────────────────────

    private void OnShowModeToggled(bool enabled)
    {
        if (_isSyncingShowModeUi) return;
        _globalData?.Settings?.SetShowMode(enabled);
    }

    private void OnToggleShowMode()
    {
        var settings = _globalData?.Settings;
        if (settings == null) return;
        settings.SetShowMode(!settings.ShowMode);
    }

    private void OnEnterEditMode()
    {
        _globalData?.Settings?.SetShowMode(false);
    }

    private void OnEnterShowMode()
    {
        _globalData?.Settings?.SetShowMode(true);
    }

    private void OnShowModeChanged(bool enabled)
    {
        SyncShowModeButton();
        SyncShowModeTooltip();
        if (_viewDrop != null && _viewDrop.Visible)
            RefreshViewMenuState();
        if (_editDrop != null && _editDrop.Visible)
            RefreshEditMenuState();
    }

    private void SyncShowModeButton()
    {
        if (_showModeButton == null) return;
        bool showMode = _globalData?.Settings?.ShowMode == true;
        if (_showModeButton.ButtonPressed == showMode) return;
        _isSyncingShowModeUi = true;
        _showModeButton.SetPressedNoSignal(showMode);
        _isSyncingShowModeUi = false;
    }

    private void SyncShowModeTooltip()
    {
        if (_showModeButton == null) return;
        string hotkey = GlobalData.ParseHotkey("ToggleShowMode");
        string tip = T(
            "Show Mode locks cue editing for live performance.\n" +
            "Off = Edit Mode (default): full cue editing.\n" +
            "On = Show Mode: inspectors hidden, shell edits and cue structure locked.\n" +
            "Saved with the showfile.");
        if (!string.IsNullOrEmpty(hotkey))
            tip += "\n" + Tf("Hotkey: {0}", hotkey);
        _showModeButton.TooltipText = tip;
    }

    private void SyncSettingsButtonTooltip()
    {
        var settingsBtn = GetNodeOrNull<Button>("%SettingsButton");
        if (settingsBtn == null) return;
        string settingsHotkey = GlobalData.ParseHotkey("ToggleSettings");
        settingsBtn.TooltipText = T("Settings") +
            (!string.IsNullOrEmpty(settingsHotkey) ? "\n" + Tf("Hotkey: {0}", settingsHotkey) : "");
    }

    /// <summary>
    /// Sets the header session title. Dirty documents show the name in italic with a trailing *.
    /// </summary>
    public void UpdateTitle()
    {
        if (_titleLabel == null || _globalData == null) return;

        bool dirty = _globalData.HistoryManager?.HasUnsavedDocumentChanges == true;
        if (!string.IsNullOrEmpty(_globalData.SessionName))
        {
            string name = _globalData.SessionName;
            _titleLabel.Text = dirty ? $"Cue2 - {name}*" : $"Cue2 - {name}";
        }
        else
        {
            _titleLabel.Text = dirty ? "Cue2*" : "Cue2";
        }

        ApplyUnsavedTitleStyle(dirty);
    }

    /// <summary>
    /// Applies or clears italic typeface on the session title.
    /// </summary>
    /// <param name="dirty">True when the document has unsaved changes.</param>
    private void ApplyUnsavedTitleStyle(bool dirty)
    {
        if (_titleLabel == null)
            return;

        if (!dirty)
        {
            _titleLabel.RemoveThemeFontOverride("font");
            return;
        }

        if (_unsavedTitleFont == null || !GodotObject.IsInstanceValid(_unsavedTitleFont))
        {
            _unsavedTitleFont = new SystemFont
            {
                FontNames = new[]
                {
                    "Open Sans", "Segoe UI", "Helvetica Neue", "Arial", "Noto Sans",
                    "DejaVu Sans", "Liberation Sans", "FreeSans", "Sans"
                },
                FontWeight = 600,
                FontItalic = true
            };
        }

        _titleLabel.AddThemeFontOverride("font", _unsavedTitleFont);
    }

    public override void _Input(InputEvent @event)
    {
        if (@event is InputEventMouseButton mouseEvent &&
            mouseEvent.ButtonIndex == MouseButton.Left &&
            mouseEvent.Pressed &&
            _mainMenuActive)
        {
            if (!IsMouseOverMenuArea() &&
                (_mainMenuButton == null || !_mainMenuButton.GetGlobalRect().HasPoint(mouseEvent.GlobalPosition)))
            {
                // Click outside menu area — close strip and drops
                if (_activeDrop != null || IsAnyDropVisible())
                    HideAllDrops();
                // Only collapse strip if not clicking the hamburger itself
                if (_mainMenuButton != null &&
                    !_mainMenuButton.GetGlobalRect().HasPoint(GetViewport().GetMousePosition()))
                {
                    // Don't force-close strip on every outside click of content — only drops
                }
            }
        }
    }

    private bool IsAnyDropVisible()
    {
        return (_fileDrop?.Visible == true) ||
               (_editDrop?.Visible == true) ||
               (_playbackDrop?.Visible == true) ||
               (_viewDrop?.Visible == true) ||
               (_helpDrop?.Visible == true);
    }

    private void OnTitleCue2MenuPressed()
    {
        OpenAboutWindow();
    }

    private void OnTitleMainMenuToggled(bool toggle)
    {
        if (toggle)
        {
            _mainMenu.Visible = true;
            _mainMenuActive = true;
            SetProcessInput(true);
        }
        else
        {
            _mainMenu.Visible = false;
            _mainMenuActive = false;
            SetProcessInput(false);
            _menuHideTimer?.Stop();
            HideAllDrops();
        }
    }

    private void StartNewSession()
    {
        GetNodeOrNull<SaveManager>("/root/SaveManager")?.RequestNewSession();
    }

    private void OpenAboutWindow()
    {
        var aboutBtn = GetNodeOrNull<Button>("%AboutButton");
        if (aboutBtn != null)
        {
            aboutBtn.ButtonPressed = true;
            return;
        }

        if (_aboutWindow == null || !GodotObject.IsInstanceValid(_aboutWindow))
        {
            _aboutWindow = _aboutWindowPackedScene.Instantiate<AboutWindow>();
            _aboutWindow.TreeExiting += OnAboutWindowExiting;
            AddChild(_aboutWindow);
        }
        else
        {
            _aboutWindow.Show();
        }
    }

    private void OnAboutButtonPressed(bool toggle)
    {
        if (toggle)
        {
            if (_aboutWindow == null || !GodotObject.IsInstanceValid(_aboutWindow))
            {
                _aboutWindow = _aboutWindowPackedScene.Instantiate<AboutWindow>();
                _aboutWindow.TreeExiting += OnAboutWindowExiting;
                AddChild(_aboutWindow);
            }
            else
            {
                _aboutWindow.Show();
            }
        }
        else
        {
            _aboutWindow?.QueueFree();
        }
    }

    private void OnAboutWindowExiting()
    {
        _aboutWindow = null;
        var aboutBtn = GetNodeOrNull<Button>("%AboutButton");
        if (aboutBtn != null)
            aboutBtn.ButtonPressed = false;
    }

    private void OnSettingsButtonToggled(bool toggle)
    {
        ToggleSettingsWindowOpen(toggle);
    }

    private void ToggleSettingsWindowOpen(bool open)
    {
        var settingsBtn = GetNodeOrNull<Button>("%SettingsButton");

        if (open)
        {
            if (_settingsWindow == null || !GodotObject.IsInstanceValid(_settingsWindow))
            {
                GD.Print("MainTitleBarUI:ToggleSettingsWindowOpen - Loading settings window scene");
                _settingsWindow = _settingsWindowPackedScene.Instantiate<SettingsWindow>();
                _settingsWindow.Visible = false;
                _settingsWindow.TreeExiting += OnSettingsWindowClose;
                _settingsWindow.CloseRequested += OnSettingsCloseRequested;
                AddChild(_settingsWindow);
            }
            else
            {
                _settingsWindow.Show();
                _settingsWindow.GrabFocus();
            }
            if (settingsBtn != null)
                settingsBtn.SetPressedNoSignal(true);
        }
        else
        {
            if (_settingsWindow != null && GodotObject.IsInstanceValid(_settingsWindow))
                _settingsWindow.Hide();
            if (settingsBtn != null)
                settingsBtn.SetPressedNoSignal(false);
        }
    }

    private void OnSettingsCloseRequested()
    {
        if (_settingsWindow != null && GodotObject.IsInstanceValid(_settingsWindow))
            _settingsWindow.Hide();
        var settingsBtn = GetNodeOrNull<Button>("%SettingsButton");
        if (settingsBtn != null)
            settingsBtn.ButtonPressed = false;
    }

    private void OnSettingsWindowClose()
    {
        _settingsWindow = null;
        var settingsBtn = GetNodeOrNull<Button>("%SettingsButton");
        if (settingsBtn != null)
            settingsBtn.ButtonPressed = false;
    }

    private void OpenUpdatesSettings()
    {
        OnOpenSettingsMenu("Updates");
    }

    /// <summary>
    /// Opens Settings on the given stable English menu key (e.g. Updates).
    /// </summary>
    /// <param name="menuKey">Settings tree key.</param>
    private void OnOpenSettingsMenu(string menuKey)
    {
        ToggleSettingsWindowOpen(true);
        if (_settingsWindow != null && GodotObject.IsInstanceValid(_settingsWindow))
            _settingsWindow.CallDeferred(SettingsWindow.MethodName.OpenMenu, menuKey ?? "Updates");
    }

    private void ToggleSettingsWindow()
    {
        var btn = GetNodeOrNull<Button>("%SettingsButton");
        if (btn != null)
            btn.ButtonPressed = !btn.ButtonPressed;
        else
            ToggleSettingsWindowOpen(_settingsWindow == null ||
                                     !GodotObject.IsInstanceValid(_settingsWindow) ||
                                     !_settingsWindow.Visible);
    }

    private void OnWindowMinimizeButtonPressed()
    {
        var window = GetWindow();
        if (window != null && GodotObject.IsInstanceValid(window))
            window.Mode = Window.ModeEnum.Minimized;
    }

    private void OnWindowExpandButtonPressed()
    {
        if (GetParent() is MainWindowHandles handles)
        {
            handles.ToggleFullscreenFromChrome();
            return;
        }

        var window = GetWindow();
        if (window == null || !GodotObject.IsInstanceValid(window))
            return;

        UiUtilities.ToggleFullscreen(window);
    }

    private void OnExitButtonPressed()
    {
        GetNodeOrNull<SaveManager>("/root/SaveManager")?.RequestQuit();
    }
}
