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
/// Owns the cuelist <see cref="TabContainer"/>: one tab per list plus a trailing
/// <c>+</c> tab that creates another list.
/// </summary>
public partial class CuelistWorkspace : TabContainer
{
    private const string PlusTabName = "AddCuelistTab";
    private const string DefaultListName = "Cue List";

    private GlobalData _globalData;
    private GlobalSignals _globalSignals;
    private HistoryManager _historyManager;
    private PackedScene _listScene;
    private Control _plusTab;
    private bool _syncingTabs;
    private PopupPanel _renamePopup;
    private LineEdit _renameEdit;
    private CueList _renameTarget;
    private TabBar _tabBar;
    private Texture2D _closeIcon;
    private readonly System.Collections.Generic.Dictionary<CueList, List<int>> _stashedSelection = new();

    /// <summary>Live cuelist tabs in display order (excludes the + tab).</summary>
    public IReadOnlyList<CueList> Lists => CollectLists();

    /// <summary>
    /// Index of the visible list in <see cref="Lists"/>, or -1 when none.
    /// </summary>
    public int GetActiveListIndex()
    {
        var lists = CollectLists();
        for (int i = 0; i < lists.Count; i++)
        {
            if (lists[i] == CueList.Live)
                return i;
        }

        return lists.Count > 0 ? 0 : -1;
    }

    /// <summary>
    /// Cuelist whose tab contains <paramref name="globalPos"/>, or null for a miss / the + tab.
    /// </summary>
    public CueList GetListTabAtGlobalPoint(Vector2 globalPos)
    {
        if (_tabBar == null || !GodotObject.IsInstanceValid(_tabBar))
            return null;
        if (!_tabBar.GetGlobalRect().HasPoint(globalPos))
            return null;

        Vector2 local = _tabBar.GetGlobalTransformWithCanvas().AffineInverse() * globalPos;
        int idx = _tabBar.GetTabIdxAtPoint(local);
        return ControlAtTab(idx) as CueList;
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
        if (list == null || !GodotObject.IsInstanceValid(list) || CueList.Live == list)
            return;

        bool wasSyncing = _syncingTabs;
        _syncingTabs = true;
        try
        {
            SelectListTab(list);
            ActivateList(list, preserveSelection: true);
            list.SyncVirtualViewport();
        }
        finally
        {
            _syncingTabs = wasSyncing;
        }
    }

    /// <summary>
    /// Switches the visible cuelist tab during undo/redo. Does not record history.
    /// </summary>
    /// <param name="activeIndex">Index into <see cref="Lists"/> captured at record time.</param>
    public void RestoreActiveListIndex(int activeIndex)
    {
        var lists = CollectLists();
        if (lists.Count == 0 || activeIndex < 0 || activeIndex >= lists.Count)
            return;

        var list = lists[activeIndex];
        if (list == null || !GodotObject.IsInstanceValid(list) || CueList.Live == list)
            return;

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
        ActivateCurrentList();
        UiLocalizer.LocalizeTree(this);
        if (_globalSignals != null)
            _globalSignals.LocaleChanged += OnLocaleChanged;
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

        if (_globalData != null && _globalData.CuelistWorkspace == this)
            _globalData.CuelistWorkspace = null;
        base._ExitTree();
    }

    private void OnLocaleChanged(string locale)
    {
        RefreshTabChrome();
    }

    /// <summary>
    /// Serializes every cuelist for the showfile / undo snapshot.
    /// </summary>
    public Dictionary GetDocumentData()
    {
        var root = new Dictionary();
        var lists = new Array();
        var collected = CollectLists();
        for (int i = 0; i < collected.Count; i++)
        {
            var data = collected[i].GetData();
            data["Name"] = collected[i].ListName ?? DefaultListName;
            lists.Add(data);
        }

        root["Cuelists"] = lists;
        root["ActiveIndex"] = Math.Max(0, collected.IndexOf(CueList.Live));
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

            var existing = CollectLists();
            while (existing.Count > slices.Count)
            {
                var extra = existing[^1];
                existing.RemoveAt(existing.Count - 1);
                RemoveChild(extra);
                extra.QueueFree();
            }

            existing = CollectLists();
            for (int i = 0; i < slices.Count; i++)
            {
                CueList list;
                if (i < existing.Count)
                    list = existing[i];
                else
                    list = InstantiateList();

                if (slices[i].TryGetValue("Name", out var nameVar))
                {
                    string name = nameVar.AsString();
                    if (!string.IsNullOrWhiteSpace(name))
                        list.ListName = name;
                }
                else if (string.IsNullOrWhiteSpace(list.ListName))
                    list.ListName = i == 0 ? DefaultListName : $"{DefaultListName} {i + 1}";

                list.ClearListForReload();
                list.LoadCueModels(slices[i]);
            }

            foreach (var list in CollectLists())
                list.BindLoadedViewport();

            EnsurePlusTabLast();
            RefreshTabChrome();
            var lists = CollectLists();
            SelectListTab(lists[Math.Clamp(activeIndex, 0, lists.Count - 1)]);
            ActivateCurrentList();
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
            var existing = CollectLists();
            CueList keep = existing.Count > 0 ? existing[0] : null;
            for (int i = 1; i < existing.Count; i++)
            {
                RemoveChild(existing[i]);
                existing[i].QueueFree();
            }

            CueList.CueIndex = new System.Collections.Generic.Dictionary<int, Cue>();
            Cue.ResetIdAllocator();
            CueList.ClearClipboard();
            _stashedSelection.Clear();
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
    /// Creates a new empty cuelist tab (the + control).
    /// </summary>
    public CueList CreateList(bool recordHistory = true, bool select = true)
    {
        if (_listScene == null)
            return null;
        if (recordHistory)
            _historyManager?.RecordCuelistChange("Add cuelist");

        var list = InstantiateList();
        list.ListName = NextDefaultName();
        EnsurePlusTabLast();
        RefreshTabChrome();
        if (select)
        {
            SelectListTab(list);
            ActivateList(list);
        }

        GD.Print($"CuelistWorkspace:CreateList - '{list.ListName}'");
        return list;
    }

    private CueList InstantiateList()
    {
        if (_listScene.Instantiate() is not CueList list)
        {
            GD.PrintErr("CuelistWorkspace:InstantiateList - CueListUi root is not CueList.");
            return null;
        }

        list.Name = $"CueList_{GetChildCount()}";
        int plusIndex = _plusTab != null && _plusTab.GetParent() == this
            ? _plusTab.GetIndex()
            : GetChildCount();
        AddChild(list);
        MoveChild(list, plusIndex);
        return list;
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
        SetTabTooltip(plusTab, UiLocalizer.T("Add cuelist"));
    }

    private void RefreshTabChrome()
    {
        EnsurePlusTabLast();
        var lists = CollectLists();
        for (int i = 0; i < lists.Count; i++)
        {
            string title = string.IsNullOrWhiteSpace(lists[i].ListName)
                ? DefaultListName
                : lists[i].ListName;
            SetControlTabTitle(lists[i], title);
        }

        SetControlTabTitle(_plusTab, "+");
        if (_tabBar != null)
        {
            _tabBar.TabCloseDisplayPolicy = lists.Count > 1
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
            _syncingTabs = true;
            var created = CreateList(recordHistory: true, select: true);
            _syncingTabs = false;
            if (created == null)
            {
                var lists = CollectLists();
                if (lists.Count > 0)
                    SelectListTab(lists[^1]);
            }

            return;
        }

        if (child is CueList list)
        {
            if (CueList.Live != null && CueList.Live != list && CollectLists().Count > 1)
                _historyManager?.RecordSelectionChange("Switch cuelist");
            ActivateList(list);
        }
    }

    private void OnActiveTabRearranged(long idxTo)
    {
        EnsurePlusTabLast();
        RefreshTabChrome();
        ActivateCurrentList();
    }

    private void OnTabClosePressed(long tab)
    {
        var child = ControlAtTab((int)tab);
        if (child == null || child == _plusTab)
            return;
        if (child is not CueList list)
            return;
        var lists = CollectLists();
        if (lists.Count <= 1)
            return;

        _historyManager?.RecordCuelistChange("Remove cuelist");
        bool wasActive = CueList.Live == list;
        _stashedSelection.Remove(list);
        list.ClearListForReload();
        RemoveChild(list);
        list.QueueFree();
        AfterListRemoved(wasActive);
    }

    private void AfterListRemoved(bool activateFallback)
    {
        EnsurePlusTabLast();
        RefreshTabChrome();
        var lists = CollectLists();
        if (lists.Count == 0)
        {
            CreateList(recordHistory: false, select: true);
            return;
        }

        if (activateFallback || CueList.Live == null || !GodotObject.IsInstanceValid(CueList.Live))
        {
            SelectListTab(lists[0]);
            ActivateList(lists[0]);
        }
    }

    private void ActivateCurrentList()
    {
        if (ControlAtTab(CurrentTab) is CueList list)
            ActivateList(list);
    }

    private void ActivateList(CueList list, bool preserveSelection = false)
    {
        if (list == null || !GodotObject.IsInstanceValid(list))
            return;

        var previous = CueList.Live;
        bool restoring = _historyManager?.IsRestoring == true;
        if (!preserveSelection && !restoring && previous != null && previous != list && GodotObject.IsInstanceValid(previous))
            StashSelection(previous);

        CueList.Live = list;
        if (_globalData != null)
            _globalData.Cuelist = list;

        if (!preserveSelection && !restoring && previous != list)
            RestoreSelection(list);
    }

    private string NextDefaultName()
    {
        int n = CollectLists().Count + 1;
        if (n <= 1)
            return DefaultListName;
        return $"{DefaultListName} {n}";
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
        if (ControlAtTab(tab) is not CueList list)
            return;

        BeginRename(list);
        _tabBar.AcceptEvent();
    }

    private void BeginRename(CueList list)
    {
        _renameTarget = list;
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

        _renameEdit.Text = list.ListName ?? DefaultListName;
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
            next = DefaultListName;
        if (string.Equals(_renameTarget.ListName, next, StringComparison.Ordinal))
        {
            _renamePopup?.Hide();
            _renameTarget = null;
            return;
        }

        _historyManager?.RecordCuelistChange("Rename cuelist");
        _renameTarget.ListName = next;
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

    private void SelectListTab(CueList list)
    {
        int idx = TabIndexOf(list);
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
