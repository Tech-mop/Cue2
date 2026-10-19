// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using Cue2.Domain.Cuelist;
using Cue2.Domain.Cues;
using Cue2.Services;
using Cue2.UI.Utilities;
using Godot;
using Godot.Collections;
using Array = Godot.Collections.Array;

namespace Cue2.UI.Shell;

/// <summary>
/// Owns the workspace <see cref="TabContainer"/>: one tab per cuelist or button board
/// plus a trailing <c>+</c> tab that offers Cuelist / Button Board.
/// </summary>
public partial class CuelistWorkspace : TabContainer
{
    private const string PlusTabName = "AddCuelistTab";
    private const string DefaultListName = "Cue List";
    private const string DefaultBoardName = "Button Board";
    private const int AddTypeCuelistId = 0;
    private const int AddTypeButtonBoardId = 1;

    private GlobalData _globalData;
    private GlobalSignals _globalSignals;
    private HistoryManager _historyManager;
    private PackedScene _listScene;
    private Control _plusTab;
    private bool _syncingTabs;
    private PopupPanel _renamePopup;
    private LineEdit _renameEdit;
    private Control _renameTarget;
    private TabBar _tabBar;
    private Texture2D _closeIcon;
    private readonly System.Collections.Generic.Dictionary<CueList, List<int>> _stashedSelection = new();
    private readonly System.Collections.Generic.Dictionary<ButtonBoard, List<int>> _stashedBoardSelection = new();
    private Control _lastPage;
    private OptionButton _addTypeButton;
    private bool _addTypePicked;
    private CueMovePreview _movePreview;

    /// <summary>Shared floating card for cuelist reorder and button-board drag.</summary>
    internal CueMovePreview MovePreview => _movePreview;

    /// <summary>Live cuelist tabs in display order (excludes the + tab).</summary>
    public IReadOnlyList<CueList> Lists => CollectLists();

    /// <summary>Button board tabs in display order.</summary>
    public IReadOnlyList<ButtonBoard> Boards => CollectBoards();

    /// <summary>
    /// Index of the visible page (cuelist or button board) in tab order, or -1 when none.
    /// </summary>
    public int GetActiveListIndex()
    {
        var pages = CollectPages();
        Control current = ControlAtTab(CurrentTab);
        if (current == _plusTab)
            current = _lastPage;
        for (int i = 0; i < pages.Count; i++)
        {
            if (pages[i] == current)
                return i;
        }

        return pages.Count > 0 ? 0 : -1;
    }

    /// <summary>
    /// Cuelist whose tab contains <paramref name="globalPos"/>, or null for a miss / the + tab.
    /// </summary>
    public CueList GetListTabAtGlobalPoint(Vector2 globalPos)
    {
        return GetPageTabAtGlobalPoint(globalPos) as CueList;
    }

    /// <summary>
    /// Workspace page (cuelist or button board) whose tab contains <paramref name="globalPos"/>,
    /// or null for a miss / the + tab.
    /// </summary>
    public Control GetPageTabAtGlobalPoint(Vector2 globalPos)
    {
        if (_tabBar == null || !GodotObject.IsInstanceValid(_tabBar))
            return null;
        if (!_tabBar.GetGlobalRect().HasPoint(globalPos))
            return null;

        Vector2 local = _tabBar.GetGlobalTransformWithCanvas().AffineInverse() * globalPos;
        int idx = _tabBar.GetTabIdxAtPoint(local);
        var page = ControlAtTab(idx);
        if (page == null || page == _plusTab)
            return null;
        return page;
    }

    /// <summary>True when <paramref name="globalPos"/> is over the cuelist tab bar.</summary>
    public bool IsGlobalPointOnTabBar(Vector2 globalPos)
    {
        return _tabBar != null
               && GodotObject.IsInstanceValid(_tabBar)
               && _tabBar.GetGlobalRect().HasPoint(globalPos);
    }

    /// <summary>
    /// Shows <paramref name="list"/> during a cue reorder drag. Does not record history and
    /// keeps the dragged selection (those cues still belong to the source list until drop).
    /// </summary>
    public void HoverActivateListDuringReorder(CueList list)
    {
        HoverActivatePageDuringReorder(list);
    }

    /// <summary>
    /// Shows a cuelist or button-board tab during a reorder drag. Does not record history
    /// and keeps the dragged selection until drop.
    /// </summary>
    public void HoverActivatePageDuringReorder(Control page)
    {
        if (page == null || !GodotObject.IsInstanceValid(page) || page == _plusTab)
            return;

        bool wasSyncing = _syncingTabs;
        _syncingTabs = true;
        try
        {
            SelectPage(page);
            if (page is CueList list)
            {
                if (CueList.Live == list)
                    return;
                ActivateList(list, preserveSelection: true);
                list.SyncVirtualViewport();
            }
            else if (page is ButtonBoard board)
            {
                if (ButtonBoard.Live == board)
                    return;
                ActivateBoard(board, preserveSelection: true);
            }
        }
        finally
        {
            _syncingTabs = wasSyncing;
        }
    }

    /// <summary>
    /// Switches the visible workspace tab during undo/redo. Does not record history.
    /// </summary>
    /// <param name="activeIndex">Index of the workspace page captured at record time.</param>
    public void RestoreActiveListIndex(int activeIndex)
    {
        var pages = CollectPages();
        if (pages.Count == 0 || activeIndex < 0 || activeIndex >= pages.Count)
            return;

        var page = pages[activeIndex];
        if (page == null || !GodotObject.IsInstanceValid(page))
            return;
        if (page is CueList list && CueList.Live == list)
            return;
        if (page == ControlAtTab(CurrentTab) && page is not CueList)
            return;

        bool wasSyncing = _syncingTabs;
        _syncingTabs = true;
        try
        {
            SelectPage(page);
            ActivatePage(page);
        }
        finally
        {
            _syncingTabs = wasSyncing;
        }
    }

    public override void _Ready()
    {
        _globalData = GetNodeOrNull<GlobalData>("/root/GlobalData");
        _globalSignals = GetNodeOrNull<GlobalSignals>("/root/GlobalSignals");
        _historyManager = _globalData?.HistoryManager;
        if (_globalData != null)
            _globalData.CuelistWorkspace = this;

        _listScene = SceneLoader.LoadPackedScene("res://src/UI/Shell/CueListUi.tscn", out string err);
        if (_listScene == null)
            GD.PrintErr($"CuelistWorkspace:_Ready - CueListUi scene: {err}");

        EnsurePlusTab();
        _tabBar = GetTabBar();
        if (_tabBar != null)
        {
            _tabBar.TabClosePressed += OnTabClosePressed;
            _tabBar.GuiInput += OnTabBarGuiInput;
            _tabBar.TabCloseDisplayPolicy = TabBar.CloseButtonDisplayPolicy.ShowActiveOnly;
            ApplyCloseIcon();
        }

        TabChanged += OnTabChanged;
        if (_tabBar != null)
            _tabBar.ActiveTabRearranged += OnActiveTabRearranged;

        foreach (var list in CollectLists())
        {
            if (string.IsNullOrWhiteSpace(list.ListName))
                list.ListName = DefaultListName;
        }

        if (CollectLists().Count == 0 && _listScene != null)
            CreateList(recordHistory: false, select: true);

        RefreshTabChrome();
        ActivateCurrentPage();
        _movePreview = new CueMovePreview();
        CallDeferred(nameof(AttachMovePreview));
        UiLocalizer.LocalizeTree(this);
        if (_globalSignals != null)
            _globalSignals.LocaleChanged += OnLocaleChanged;
    }

    private void AttachMovePreview()
    {
        if (_movePreview == null || !IsInsideTree())
            return;
        Node host = GetNodeOrNull("/root/Cue2Base") ?? GetTree()?.Root;
        _movePreview.Attach(host);
    }

    public override void _ExitTree()
    {
        if (_globalSignals != null)
            _globalSignals.LocaleChanged -= OnLocaleChanged;
        TabChanged -= OnTabChanged;
        if (_tabBar != null && GodotObject.IsInstanceValid(_tabBar))
        {
            _tabBar.TabClosePressed -= OnTabClosePressed;
            _tabBar.GuiInput -= OnTabBarGuiInput;
            _tabBar.ActiveTabRearranged -= OnActiveTabRearranged;
            _tabBar.RemoveThemeIconOverride("close");
        }

        TearDownAddTypeButton();

        if (_renameEdit != null)
        {
            _renameEdit.TextSubmitted -= OnRenameSubmitted;
            _renameEdit.FocusExited -= OnRenameFocusExited;
        }

        if (_closeIcon != null)
        {
            UiUtilities.DisposeRefCounted(_closeIcon);
            _closeIcon = null;
        }

        _movePreview?.DisposeResources();
        _movePreview = null;
        if (_globalData != null && _globalData.CuelistWorkspace == this)
            _globalData.CuelistWorkspace = null;
        base._ExitTree();
    }

    private void OnLocaleChanged(string locale)
    {
        RefreshTabChrome();
        if (_addTypeButton != null && GodotObject.IsInstanceValid(_addTypeButton))
            UiLocalizer.RelocalizeOptionButton(_addTypeButton);
    }

    /// <summary>
    /// Serializes every workspace page (cuelist or button board) for the showfile / undo snapshot.
    /// </summary>
    public Dictionary GetDocumentData()
    {
        var root = new Dictionary();
        var lists = new Array();
        var collected = CollectPages();
        for (int i = 0; i < collected.Count; i++)
        {
            var data = GetPageData(collected[i]);
            if (data != null)
                lists.Add(data);
        }

        root["Cuelists"] = lists;
        int active = GetActiveListIndex();
        root["ActiveIndex"] = Math.Max(0, active);
        return root;
    }

    /// <summary>
    /// Rebuilds tabs from showfile or history data.
    /// Accepts either a <c>Cuelists</c> array document or a legacy single-list <c>Cues</c>+<c>CueOrder</c> dict.
    /// </summary>
    public void ApplyDocumentData(Dictionary data)
    {
        if (data == null)
        {
            ResetToSingleEmptyList();
            return;
        }

        var slices = new List<Dictionary>();
        if (data.TryGetValue("Cuelists", out var listsVar) && listsVar.VariantType == Variant.Type.Array)
        {
            foreach (var item in listsVar.AsGodotArray())
            {
                if (item.VariantType == Variant.Type.Dictionary)
                    slices.Add(item.AsGodotDictionary());
            }
        }
        else if (data.ContainsKey("Cues") || data.ContainsKey("CueOrder"))
        {
            slices.Add(data);
        }

        if (slices.Count == 0)
        {
            ResetToSingleEmptyList();
            return;
        }

        int activeIndex = 0;
        if (data.TryGetValue("ActiveIndex", out var activeVar))
            activeIndex = Math.Clamp(activeVar.AsInt32(), 0, slices.Count - 1);

        _syncingTabs = true;
        try
        {
            Cue.ResetIdAllocator();
            CueList.CueIndex = new System.Collections.Generic.Dictionary<int, Cue>();
            CueList.ClearClipboard();
            _stashedSelection.Clear();
            _stashedBoardSelection.Clear();

            var existing = CollectPages();
            while (existing.Count > slices.Count)
            {
                var extra = existing[^1];
                existing.RemoveAt(existing.Count - 1);
                RemoveChild(extra);
                extra.QueueFree();
            }

            for (int i = 0; i < slices.Count; i++)
            {
                existing = CollectPages();
                string kind = ReadPageKind(slices[i]);
                Control page = i < existing.Count ? existing[i] : null;
                bool wantBoard = kind == ButtonBoard.Kind;
                bool pageOk = wantBoard ? page is ButtonBoard : page is CueList;
                if (!pageOk)
                {
                    if (page != null)
                    {
                        int childIdx = page.GetIndex();
                        RemoveChild(page);
                        page.QueueFree();
                        page = wantBoard ? CreateBoardNode() : CreateListNode();
                        if (page != null)
                        {
                            AddChild(page);
                            MoveChild(page, childIdx);
                        }
                    }
                    else
                    {
                        page = wantBoard ? InstantiateBoard() : InstantiateList();
                    }
                }

                ApplyPageSlice(page, slices[i], i);
            }

            foreach (var list in CollectLists())
                list.BindLoadedViewport();

            EnsurePlusTabLast();
            RefreshTabChrome();
            var pages = CollectPages();
            if (pages.Count > 0)
            {
                SelectPage(pages[Math.Clamp(activeIndex, 0, pages.Count - 1)]);
                ActivateCurrentPage();
            }
        }
        finally
        {
            _syncingTabs = false;
        }
    }

    /// <summary>
    /// New Session / open: one empty cuelist named Cue List.
    /// </summary>
    public void ResetToSingleEmptyList()
    {
        _syncingTabs = true;
        try
        {
            var pages = CollectPages();
            CueList keep = null;
            foreach (var page in pages)
            {
                if (page is CueList list)
                {
                    keep = list;
                    break;
                }
            }

            foreach (var page in pages)
            {
                if (page == keep)
                    continue;
                RemoveChild(page);
                page.QueueFree();
            }

            CueList.CueIndex = new System.Collections.Generic.Dictionary<int, Cue>();
            Cue.ResetIdAllocator();
            CueList.ClearClipboard();
            _stashedSelection.Clear();
            _stashedBoardSelection.Clear();
            if (keep == null)
                keep = InstantiateList();
            keep.ListName = DefaultListName;
            keep.ClearListForReload();
            keep.BindLoadedViewport();
            EnsurePlusTabLast();
            RefreshTabChrome();
            SelectListTab(keep);
            ActivateList(keep);
        }
        finally
        {
            _syncingTabs = false;
        }
    }

    /// <summary>
    /// Creates a new empty cuelist tab.
    /// </summary>
    public CueList CreateList(bool recordHistory = true, bool select = true)
    {
        if (_listScene == null)
            return null;
        if (recordHistory)
            _historyManager?.RecordCuelistChange("Add cuelist");

        var list = InstantiateList();
        list.ListName = NextDefaultListName();
        EnsurePlusTabLast();
        RefreshTabChrome();
        if (select)
        {
            bool wasSyncing = _syncingTabs;
            _syncingTabs = true;
            try
            {
                SelectListTab(list);
                ActivateList(list);
            }
            finally
            {
                _syncingTabs = wasSyncing;
            }
        }

        GD.Print($"CuelistWorkspace:CreateList - '{list.ListName}'");
        return list;
    }

    /// <summary>
    /// Creates a new button-board tab (placeholder UI).
    /// </summary>
    public ButtonBoard CreateButtonBoard(bool recordHistory = true, bool select = true)
    {
        if (recordHistory)
            _historyManager?.RecordCuelistChange("Add button board");

        var board = InstantiateBoard();
        board.ListName = NextDefaultBoardName();
        EnsurePlusTabLast();
        RefreshTabChrome();
        if (select)
        {
            bool wasSyncing = _syncingTabs;
            _syncingTabs = true;
            try
            {
                SelectPage(board);
                ActivateBoard(board);
            }
            finally
            {
                _syncingTabs = wasSyncing;
            }
        }

        GD.Print($"CuelistWorkspace:CreateButtonBoard - '{board.ListName}'");
        return board;
    }

    private CueList CreateListNode()
    {
        if (_listScene == null)
            return null;
        if (_listScene.Instantiate() is not CueList list)
        {
            GD.PrintErr("CuelistWorkspace:CreateListNode - CueListUi root is not CueList.");
            return null;
        }

        list.Name = $"CueList_{GetChildCount()}";
        return list;
    }

    private ButtonBoard CreateBoardNode()
    {
        return new ButtonBoard
        {
            Name = $"ButtonBoard_{GetChildCount()}",
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill
        };
    }

    private CueList InstantiateList()
    {
        var list = CreateListNode();
        if (list == null)
            return null;
        InsertPageBeforePlus(list);
        return list;
    }

    private ButtonBoard InstantiateBoard()
    {
        var board = CreateBoardNode();
        InsertPageBeforePlus(board);
        return board;
    }

    private void InsertPageBeforePlus(Control page)
    {
        int plusIndex = _plusTab != null && _plusTab.GetParent() == this
            ? _plusTab.GetIndex()
            : GetChildCount();
        AddChild(page);
        MoveChild(page, plusIndex);
    }

    private void EnsurePlusTab()
    {
        _plusTab = GetNodeOrNull<Control>(PlusTabName);
        if (_plusTab == null)
        {
            _plusTab = new Control
            {
                Name = PlusTabName,
                MouseFilter = MouseFilterEnum.Ignore
            };
            AddChild(_plusTab);
        }

        EnsurePlusTabLast();
    }

    private void EnsurePlusTabLast()
    {
        if (_plusTab == null || _plusTab.GetParent() != this)
            return;
        int last = GetChildCount() - 1;
        if (_plusTab.GetIndex() != last)
            MoveChild(_plusTab, last);
        int plusTab = TabIndexOf(_plusTab);
        if (plusTab < 0)
            return;
        SetTabTitle(plusTab, "+");
        SetTabHidden(plusTab, false);
        SetTabTooltip(plusTab, UiLocalizer.T("Add"));
    }

    private void RefreshTabChrome()
    {
        EnsurePlusTabLast();
        var pages = CollectPages();
        for (int i = 0; i < pages.Count; i++)
            SetControlTabTitle(pages[i], GetPageName(pages[i]));

        SetControlTabTitle(_plusTab, "+");
        if (_tabBar != null)
        {
            _tabBar.TabCloseDisplayPolicy = pages.Count > 1
                ? TabBar.CloseButtonDisplayPolicy.ShowActiveOnly
                : TabBar.CloseButtonDisplayPolicy.ShowNever;
        }
    }

    private List<CueList> CollectLists()
    {
        var lists = new List<CueList>();
        foreach (Node child in GetChildren())
        {
            if (child is CueList list && GodotObject.IsInstanceValid(list))
                lists.Add(list);
        }

        return lists;
    }

    private List<Control> CollectPages()
    {
        var pages = new List<Control>();
        foreach (Node child in GetChildren())
        {
            if (child is CueList list && GodotObject.IsInstanceValid(list))
                pages.Add(list);
            else if (child is ButtonBoard board && GodotObject.IsInstanceValid(board))
                pages.Add(board);
        }

        return pages;
    }

    private List<ButtonBoard> CollectBoards()
    {
        var boards = new List<ButtonBoard>();
        foreach (Node child in GetChildren())
        {
            if (child is ButtonBoard board && GodotObject.IsInstanceValid(board))
                boards.Add(board);
        }

        return boards;
    }

    private void OnTabChanged(long tab)
    {
        if (_syncingTabs)
            return;
        int index = (int)tab;
        var child = ControlAtTab(index);
        if (child == null)
            return;
        if (child == _plusTab)
        {
            RevertFromPlusTab();
            ShowAddTypePicker();
            return;
        }

        bool switching = _lastPage != null && _lastPage != child;
        if (switching && CollectPages().Count > 1)
            _historyManager?.RecordSelectionChange("Switch cuelist");
        ActivatePage(child);
    }

    private void RevertFromPlusTab()
    {
        _syncingTabs = true;
        try
        {
            Control fallback = _lastPage;
            if (fallback == null || !GodotObject.IsInstanceValid(fallback) || fallback.GetParent() != this)
            {
                var pages = CollectPages();
                fallback = pages.Count > 0 ? pages[^1] : null;
            }

            if (fallback != null)
                SelectPage(fallback);
        }
        finally
        {
            _syncingTabs = false;
        }
    }

    private void OnActiveTabRearranged(long idxTo)
    {
        EnsurePlusTabLast();
        RefreshTabChrome();
        ActivateCurrentPage();
    }

    private void OnTabClosePressed(long tab)
    {
        var child = ControlAtTab((int)tab);
        if (child == null || child == _plusTab)
            return;

        if (child is CueList list)
        {
            if (CollectLists().Count <= 1)
                return;
            _historyManager?.RecordCuelistChange("Remove cuelist");
            bool wasVisible = child == ControlAtTab(CurrentTab);
            _stashedSelection.Remove(list);
            list.ClearListForReload();
            RemoveChild(list);
            list.QueueFree();
            AfterPageRemoved(wasVisible);
            return;
        }

        if (child is ButtonBoard board)
        {
            _historyManager?.RecordCuelistChange("Remove button board");
            bool wasVisible = child == ControlAtTab(CurrentTab);
            _stashedBoardSelection.Remove(board);
            board.ClearBoardForReload();
            RemoveChild(board);
            board.QueueFree();
            AfterPageRemoved(wasVisible);
        }
    }

    private void AfterPageRemoved(bool activateFallback)
    {
        EnsurePlusTabLast();
        RefreshTabChrome();
        if (CollectLists().Count == 0)
        {
            CreateList(recordHistory: false, select: true);
            return;
        }

        var pages = CollectPages();
        if (pages.Count == 0)
            return;

        Control current = ControlAtTab(CurrentTab);
        if (activateFallback || current == null || current == _plusTab)
        {
            SelectPage(pages[0]);
            ActivatePage(pages[0]);
        }
        else
        {
            ActivatePage(current);
        }
    }

    private void ActivateCurrentPage()
    {
        var child = ControlAtTab(CurrentTab);
        if (child != null && child != _plusTab)
            ActivatePage(child);
    }

    private void ActivatePage(Control page)
    {
        if (page is CueList list)
            ActivateList(list);
        else if (page is ButtonBoard board)
            ActivateBoard(board);
    }

    private void ActivateList(CueList list, bool preserveSelection = false)
    {
        if (list == null || !GodotObject.IsInstanceValid(list))
            return;

        var previous = CueList.Live;
        bool restoring = _historyManager?.IsRestoring == true;
        if (!preserveSelection && !restoring && previous != null && previous != list && GodotObject.IsInstanceValid(previous))
            StashSelection(previous);

        var previousBoard = ButtonBoard.Live;
        if (!preserveSelection && !restoring && previousBoard != null && GodotObject.IsInstanceValid(previousBoard))
            StashBoardSelection(previousBoard);

        CueList.Live = list;
        ButtonBoard.Live = null;
        if (_globalData != null)
            _globalData.Cuelist = list;
        _lastPage = list;

        if (!preserveSelection && !restoring && previous != list)
            RestoreSelection(list);
    }

    private void ActivateBoard(ButtonBoard board, bool preserveSelection = false)
    {
        if (board == null || !GodotObject.IsInstanceValid(board))
            return;

        var previous = CueList.Live;
        var previousBoard = ButtonBoard.Live;
        bool restoring = _historyManager?.IsRestoring == true;
        if (!preserveSelection && !restoring && previous != null && GodotObject.IsInstanceValid(previous))
            StashSelection(previous);
        if (!preserveSelection && !restoring && previousBoard != null && previousBoard != board && GodotObject.IsInstanceValid(previousBoard))
            StashBoardSelection(previousBoard);

        CueList.Live = null;
        ButtonBoard.Live = board;
        _lastPage = board;

        if (!preserveSelection && !restoring)
        {
            if (_globalData?.Settings?.IsCueEditingLocked == true)
            {
                _globalData.ShellSelection?.ClearSelection(recordHistory: false);
                board.RefreshSelectionChrome();
            }
            else
                RestoreBoardSelection(board);
        }
    }

    private string NextDefaultListName()
    {
        int n = CollectLists().Count + 1;
        if (n <= 1)
            return DefaultListName;
        return $"{DefaultListName} {n}";
    }

    private string NextDefaultBoardName()
    {
        int n = 0;
        foreach (var page in CollectPages())
        {
            if (page is ButtonBoard)
                n++;
        }

        n++;
        if (n <= 1)
            return DefaultBoardName;
        return $"{DefaultBoardName} {n}";
    }

    private void OnTabBarGuiInput(InputEvent @event)
    {
        if (@event is not InputEventMouseButton mb || !mb.Pressed || !mb.DoubleClick)
            return;
        if (mb.ButtonIndex != MouseButton.Left)
            return;
        if (_tabBar == null)
            return;

        int tab = _tabBar.GetTabIdxAtPoint(mb.Position);
        var page = ControlAtTab(tab);
        if (page is not CueList && page is not ButtonBoard)
            return;

        BeginRename(page);
        _tabBar.AcceptEvent();
    }

    private void BeginRename(Control page)
    {
        _renameTarget = page;
        if (_renamePopup == null)
        {
            _renamePopup = new PopupPanel();
            _renameEdit = new LineEdit
            {
                CustomMinimumSize = new Vector2(160, 0)
            };
            _renameEdit.TextSubmitted += OnRenameSubmitted;
            _renameEdit.FocusExited += OnRenameFocusExited;
            var margin = new MarginContainer();
            margin.AddThemeConstantOverride("margin_left", 6);
            margin.AddThemeConstantOverride("margin_right", 6);
            margin.AddThemeConstantOverride("margin_top", 4);
            margin.AddThemeConstantOverride("margin_bottom", 4);
            margin.AddChild(_renameEdit);
            _renamePopup.AddChild(margin);
            // PopupPanel is a Window. Parenting it to TabContainer makes GetIndex()
            // diverge from tab index and SetTabTitle/SetTabHidden fail with a null child.
            Node host = GetParent() ?? this;
            host.AddChild(_renamePopup);
        }

        _renameEdit.Text = GetPageName(page);
        UiUtilities.PopupScaledAtMouse(_renamePopup, this);
        _renameEdit.GrabFocus();
        _renameEdit.SelectAll();
    }

    private void OnRenameSubmitted(string text) => CommitRename(text);

    private void OnRenameFocusExited()
    {
        if (_renameEdit != null)
            CommitRename(_renameEdit.Text);
    }

    private void CommitRename(string text)
    {
        if (_renameTarget == null || !GodotObject.IsInstanceValid(_renameTarget))
            return;
        string next = (text ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(next))
            next = _renameTarget is ButtonBoard ? DefaultBoardName : DefaultListName;
        if (string.Equals(GetPageName(_renameTarget), next, StringComparison.Ordinal))
        {
            _renamePopup?.Hide();
            _renameTarget = null;
            return;
        }

        string historyLabel = _renameTarget is ButtonBoard ? "Rename button board" : "Rename cuelist";
        _historyManager?.RecordCuelistChange(historyLabel);
        SetPageName(_renameTarget, next);
        RefreshTabChrome();
        _renamePopup?.Hide();
        _renameTarget = null;
    }

    private int TabIndexOf(Control control)
    {
        if (control == null || !GodotObject.IsInstanceValid(control) || control.GetParent() != this)
            return -1;
        return GetTabIdxFromControl(control);
    }

    private Control ControlAtTab(int tab)
    {
        if (tab < 0 || tab >= GetTabCount())
            return null;
        return GetTabControl(tab);
    }

    private void SetControlTabTitle(Control control, string title)
    {
        int idx = TabIndexOf(control);
        if (idx < 0)
            return;
        SetTabTitle(idx, title);
    }

    private void SelectListTab(CueList list) => SelectPage(list);

    private void SelectPage(Control page)
    {
        int idx = TabIndexOf(page);
        if (idx >= 0)
            CurrentTab = idx;
    }

    private void StashSelection(CueList list)
    {
        if (list == null || !GodotObject.IsInstanceValid(list))
            return;
        var ids = new List<int>();
        var selected = ShellSelection.SelectedCues;
        if (selected != null)
        {
            foreach (var cue in selected)
            {
                if (cue != null && list.ContainsCue(cue.Id))
                    ids.Add(cue.Id);
            }
        }

        _stashedSelection[list] = ids;
    }

    private void RestoreSelection(CueList list)
    {
        if (list == null || !GodotObject.IsInstanceValid(list))
            return;

        var cues = new List<Cue>();
        if (_stashedSelection.TryGetValue(list, out var ids) && ids != null)
        {
            foreach (int id in ids)
            {
                var cue = CueList.FetchCueFromId(id);
                if (cue != null && list.ContainsCue(id))
                    cues.Add(cue);
            }
        }

        _globalData?.ShellSelection?.SetSelection(cues, recordHistory: false, emitFocus: true);
    }

    private void StashBoardSelection(ButtonBoard board)
    {
        if (board == null || !GodotObject.IsInstanceValid(board))
            return;
        var ids = new List<int>();
        var selected = ShellSelection.SelectedCues;
        if (selected != null)
        {
            foreach (var cue in selected)
            {
                if (cue != null && board.ContainsCue(cue.Id))
                    ids.Add(cue.Id);
            }
        }

        _stashedBoardSelection[board] = ids;
    }

    private void RestoreBoardSelection(ButtonBoard board)
    {
        if (board == null || !GodotObject.IsInstanceValid(board))
            return;

        var cues = new List<Cue>();
        if (_stashedBoardSelection.TryGetValue(board, out var ids) && ids != null)
        {
            foreach (int id in ids)
            {
                var cue = CueList.FetchCueFromId(id);
                if (cue != null && board.ContainsCue(id))
                    cues.Add(cue);
            }
        }

        _globalData?.ShellSelection?.SetSelection(cues, recordHistory: false, emitFocus: true);
    }

    private void ShowAddTypePicker()
    {
        EnsureAddTypeButton();
        if (_addTypeButton == null)
            return;

        _addTypePicked = false;
        _addTypeButton.Select(-1);
        PlaceAddTypeButtonOnPlusTab();
        _addTypeButton.Visible = true;
        _addTypeButton.ShowPopup();
    }

    private void EnsureAddTypeButton()
    {
        if (_addTypeButton != null && GodotObject.IsInstanceValid(_addTypeButton))
            return;

        Control host = GetNodeOrNull<Control>("/root/Cue2Base");
        if (host == null)
        {
            GD.PrintErr("CuelistWorkspace:EnsureAddTypeButton - Cue2Base host missing; picker skipped.");
            return;
        }

        _addTypeButton = new OptionButton
        {
            Name = "CuelistAddTypeButton",
            Flat = true,
            FocusMode = FocusModeEnum.None,
            MouseFilter = MouseFilterEnum.Ignore,
            AllowReselect = true,
            Visible = false,
            Modulate = Colors.Transparent
        };
        UiLocalizer.AddTranslatedItem(_addTypeButton, "Cuelist", AddTypeCuelistId);
        UiLocalizer.AddTranslatedItem(_addTypeButton, "Button Board", AddTypeButtonBoardId);
        _addTypeButton.Select(-1);
        _addTypeButton.ItemSelected += OnAddTypePicked;
        PopupMenu popup = _addTypeButton.GetPopup();
        if (popup != null)
            popup.PopupHide += OnAddTypePopupHide;
        host.AddChild(_addTypeButton);
    }

    private void PlaceAddTypeButtonOnPlusTab()
    {
        if (_addTypeButton == null || _tabBar == null || !GodotObject.IsInstanceValid(_tabBar))
            return;
        int plusTab = TabIndexOf(_plusTab);
        if (plusTab < 0)
            return;

        Rect2 tabRect = _tabBar.GetTabRect(plusTab);
        Vector2 global = _tabBar.GetGlobalTransformWithCanvas() * tabRect.Position;
        if (_addTypeButton.GetParent() is not Control host)
            return;

        Vector2 local = host.GetGlobalTransformWithCanvas().AffineInverse() * global;
        _addTypeButton.Position = local;
        _addTypeButton.Size = tabRect.Size;
    }

    private void OnAddTypePicked(long index)
    {
        _addTypePicked = true;
        int id = _addTypeButton != null
            ? _addTypeButton.GetItemId((int)index)
            : AddTypeCuelistId;
        HideAddTypePicker();
        if (id == AddTypeButtonBoardId)
            CreateButtonBoard(recordHistory: true, select: true);
        else
            CreateList(recordHistory: true, select: true);
    }

    private void OnAddTypePopupHide()
    {
        if (_addTypePicked)
            return;
        HideAddTypePicker();
    }

    private void HideAddTypePicker()
    {
        if (_addTypeButton == null || !GodotObject.IsInstanceValid(_addTypeButton))
            return;
        _addTypeButton.Visible = false;
        _addTypeButton.Select(-1);
    }

    private void TearDownAddTypeButton()
    {
        if (_addTypeButton == null)
            return;
        if (GodotObject.IsInstanceValid(_addTypeButton))
        {
            _addTypeButton.ItemSelected -= OnAddTypePicked;
            PopupMenu popup = _addTypeButton.GetPopup();
            if (popup != null && GodotObject.IsInstanceValid(popup))
                popup.PopupHide -= OnAddTypePopupHide;
            _addTypeButton.QueueFree();
        }

        _addTypeButton = null;
    }

    private static string ReadPageKind(Dictionary data)
    {
        if (data != null && data.TryGetValue("Kind", out var kindVar))
        {
            string kind = kindVar.AsString();
            if (kind == ButtonBoard.Kind)
                return ButtonBoard.Kind;
        }

        return CueList.Kind;
    }

    private static Dictionary GetPageData(Control page)
    {
        if (page is CueList list)
        {
            var data = list.GetData();
            data["Name"] = list.ListName ?? DefaultListName;
            return data;
        }

        if (page is ButtonBoard board)
            return board.GetData();
        return null;
    }

    private static void ApplyPageSlice(Control page, Dictionary slice, int index)
    {
        if (page is CueList list)
        {
            if (slice.TryGetValue("Name", out var nameVar))
            {
                string name = nameVar.AsString();
                if (!string.IsNullOrWhiteSpace(name))
                    list.ListName = name;
            }
            else if (string.IsNullOrWhiteSpace(list.ListName))
                list.ListName = index == 0 ? DefaultListName : $"{DefaultListName} {index + 1}";

            list.ClearListForReload();
            list.LoadCueModels(slice);
            return;
        }

        if (page is ButtonBoard board)
        {
            board.ApplyFromData(slice);
            if (string.IsNullOrWhiteSpace(board.ListName))
                board.ListName = index == 0 ? DefaultBoardName : $"{DefaultBoardName} {index + 1}";
        }
    }

    private static string GetPageName(Control page)
    {
        if (page is CueList list)
            return string.IsNullOrWhiteSpace(list.ListName) ? DefaultListName : list.ListName;
        if (page is ButtonBoard board)
            return string.IsNullOrWhiteSpace(board.ListName) ? DefaultBoardName : board.ListName;
        return string.Empty;
    }

    private static void SetPageName(Control page, string name)
    {
        if (page is CueList list)
            list.ListName = name;
        else if (page is ButtonBoard board)
            board.ListName = name;
    }

    /// <summary>
    /// AtlasIcons Stop is a 32px X. TabBar draws the close texture at native size
    /// (no <c>icon_max_width</c>), so it must be resized to content pixels.
    /// </summary>
    private void ApplyCloseIcon()
    {
        if (_tabBar == null)
            return;

        const int size = 9;
        var tex = CreateScaledCloseIcon(size);
        if (_closeIcon != null && _closeIcon != tex)
            UiUtilities.DisposeRefCounted(_closeIcon);
        _closeIcon = tex;
        _tabBar.AddThemeIconOverride("close", _closeIcon);
        _tabBar.AddThemeConstantOverride("icon_max_width", size);
    }

    private Texture2D CreateScaledCloseIcon(int size)
    {
        var src = GetThemeIcon("Stop", "AtlasIcons");
        Image img = null;
        try
        {
            img = src?.GetImage();
        }
        catch (Exception ex)
        {
            GD.PrintErr($"CuelistWorkspace:CreateScaledCloseIcon - GetImage failed: {ex.Message}");
        }

        if (img != null && img.GetWidth() > 0 && img.GetHeight() > 0 && img.GetWidth() <= 48 && img.GetHeight() <= 48)
        {
            if (img.GetWidth() != size || img.GetHeight() != size)
                img.Resize(size, size, Image.Interpolation.Lanczos);
            return ImageTexture.CreateFromImage(img);
        }

        img = Image.CreateEmpty(size, size, false, Image.Format.Rgba8);
        img.Fill(Colors.Transparent);
        var color = Colors.White;
        int last = size - 1;
        for (int i = 0; i < size; i++)
        {
            img.SetPixel(i, i, color);
            img.SetPixel(last - i, i, color);
            if (i + 1 < size)
            {
                img.SetPixel(i + 1, i, color);
                int j = last - i - 1;
                if (j >= 0)
                    img.SetPixel(j, i, color);
            }
        }

        return ImageTexture.CreateFromImage(img);
    }
}
