// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Cue2.Domain.Cuelist;
using Cue2.Domain.Cues;
using Cue2.Services;
using Cue2.UI.Popups;
using Cue2.UI.Utilities;
using Godot;
using Godot.Collections;
using Array = Godot.Collections.Array;

namespace Cue2.UI.Shell;

/// <summary>
/// Workspace tab for a button board: a scrollable spreadsheet-style grid
/// with shared column widths and row heights, grown around placed cues.
/// </summary>
public partial class ButtonBoard : Control
{
    /// <summary>Showfile / history discriminator for this tab kind.</summary>
    public const string Kind = "ButtonBoard";

    private const float DefaultColWidth = 200f;
    private const float DefaultRowHeight = 140f;
    private const float MinColWidth = 88f;
    private const float MinRowHeight = 72f;

    /// <summary>The board whose tab is visible, or null when a cuelist is showing.</summary>
    internal static ButtonBoard Live { get; set; }

    /// <summary>True while a cell-to-cell reorder drag is active.</summary>
    internal bool IsReordering => _dragging;

    /// <summary>Current focused cue id from <see cref="GlobalData"/>.</summary>
    internal int FocusedCueId => _globalData?.FocusedCue ?? -1;

    /// <summary>Shared shell selection service.</summary>
    internal ShellSelection Selection => _globalData?.ShellSelection;

    /// <summary>Tab title. User data (not translated).</summary>
    public string ListName { get; set; } = "Button Board";

    private GlobalData _globalData;
    private GlobalSignals _globalSignals;
    private HistoryManager _historyManager;

    private int _rows = 1;
    private int _cols = 1;
    private int[] _cellCueIds = { -1 };
    private readonly List<float> _colWidths = new() { DefaultColWidth };
    private readonly List<float> _rowHeights = new() { DefaultRowHeight };

    private ScrollContainer _scroll;
    private Control _contentHost;
    private readonly List<ButtonBoardCell> _cells = new();
    private readonly List<ButtonBoardResizeHandle> _colHandles = new();
    private readonly List<ButtonBoardResizeHandle> _rowHandles = new();

    private bool _resizing;
    private bool _resizeVertical;
    private int _resizeIndex;
    private Vector2 _resizeStartLocal;
    private float _resizeStartSize;
    private ButtonBoardResizeHandle _resizeHandle;
    private List<float> _resizeStartColWidths;
    private List<float> _resizeStartRowHeights;
    private float[] _colStops = { 0f };
    private float[] _rowStops = { 0f };

    private bool _dragging;
    private ButtonBoardCell _dragSource;
    private ButtonBoardCell _dropTarget;
    private Control _hoverTabTarget;
    private double _hoverTabElapsed;
    private const double TabHoverSwitchSeconds = 0.35;
    private PopupMenu _contextMenu;
    private int _contextRow;
    private int _contextCol;

    private ButtonBoardBoxSelect _boxSelect;
    private BoardPastePopup _pastePopup;
    private int _pendingPasteRow = -1;
    private int _pendingPasteCol = -1;

    /// <inheritdoc />
    public override void _Ready()
    {
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Stop;
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
        SizeFlagsVertical = SizeFlags.ExpandFill;

        _globalData = GetNodeOrNull<GlobalData>("/root/GlobalData");
        _globalSignals = GetNodeOrNull<GlobalSignals>("/root/GlobalSignals");
        _historyManager = _globalData?.HistoryManager;

        _scroll = new ScrollContainer
        {
            HorizontalScrollMode = ScrollContainer.ScrollMode.Auto,
            VerticalScrollMode = ScrollContainer.ScrollMode.Auto
        };
        _scroll.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(_scroll);

        _contentHost = new Control
        {
            MouseFilter = MouseFilterEnum.Ignore,
            SizeFlagsHorizontal = SizeFlags.ShrinkBegin,
            SizeFlagsVertical = SizeFlags.ShrinkBegin
        };
        _scroll.AddChild(_contentHost);

        if (_globalSignals != null)
        {
            _globalSignals.ShowModeChanged += OnShowModeChanged;
            _globalSignals.DeleteSelectedCues += OnDeleteSelectedCues;
            _globalSignals.CopySelectedCues += OnCopySelectedCues;
            _globalSignals.CutSelectedCues += OnCutSelectedCues;
            _globalSignals.PasteCues += OnPasteCues;
            _globalSignals.CreateCue += OnCreateCue;
            _globalSignals.LocaleChanged += OnLocaleChanged;
            _globalSignals.ThemeColorsChanged += OnThemeColorsChanged;
            _globalSignals.ShellFocused += OnShellFocused;
            if (_historyManager != null)
                _historyManager.HistoryRestored += OnHistoryRestored;
        }

        EnsureModel();
        RebuildGrid();
        _boxSelect = new ButtonBoardBoxSelect(this, _scroll, this);
        _scroll.GuiInput += OnScrollGuiInput;
        UiLocalizer.LocalizeTree(this);
        SyncPointerProcessing();
    }

    /// <inheritdoc />
    public override void _ExitTree()
    {
        SetProcessInput(false);
        SetProcess(false);
        if (_scroll != null)
            _scroll.GuiInput -= OnScrollGuiInput;
        if (_globalSignals != null)
        {
            _globalSignals.ShowModeChanged -= OnShowModeChanged;
            _globalSignals.DeleteSelectedCues -= OnDeleteSelectedCues;
            _globalSignals.CopySelectedCues -= OnCopySelectedCues;
            _globalSignals.CutSelectedCues -= OnCutSelectedCues;
            _globalSignals.PasteCues -= OnPasteCues;
            _globalSignals.CreateCue -= OnCreateCue;
            _globalSignals.LocaleChanged -= OnLocaleChanged;
            _globalSignals.ThemeColorsChanged -= OnThemeColorsChanged;
            _globalSignals.ShellFocused -= OnShellFocused;
            if (_historyManager != null)
                _historyManager.HistoryRestored -= OnHistoryRestored;
        }

        if (_dragging)
            CancelCellDrag();
        if (_resizing)
            EndAxisResize();
        _boxSelect?.DisposeResources();
        _boxSelect = null;
        ClosePastePopup();
        if (_contextMenu != null && GodotObject.IsInstanceValid(_contextMenu))
        {
            _contextMenu.IdPressed -= OnContextMenuIdPressed;
            _contextMenu.QueueFree();
        }

        _contextMenu = null;
        if (Live == this)
            Live = null;
        base._ExitTree();
    }

    /// <inheritdoc />
    public override void _Input(InputEvent @event)
    {
        if (_dragging)
        {
            if (@event is InputEventMouseButton mb)
            {
                if (mb.ButtonIndex == MouseButton.Left && !mb.Pressed)
                {
                    EndCellDrag(mb.GlobalPosition);
                    GetViewport()?.SetInputAsHandled();
                }
                else if (mb.Pressed && mb.ButtonIndex == MouseButton.Right)
                {
                    CancelCellDrag();
                    GetViewport()?.SetInputAsHandled();
                }
            }
            else if (@event is InputEventKey key && key.Pressed && key.Keycode == Key.Escape)
            {
                CancelCellDrag();
                GetViewport()?.SetInputAsHandled();
            }

            return;
        }

        if (_resizing)
        {
            if (@event is InputEventMouseButton mb && mb.ButtonIndex == MouseButton.Left && !mb.Pressed)
            {
                EndAxisResize();
                GetViewport()?.SetInputAsHandled();
            }
            return;
        }

        if (_boxSelect != null && (_boxSelect.IsPending || _boxSelect.IsActive))
            _boxSelect.ProcessInput(@event);
    }

    /// <inheritdoc />
    public override void _Process(double delta)
    {
        if (_resizing)
        {
            UpdateAxisResize(GetGlobalMousePosition());
            return;
        }

        if (_dragging)
        {
            Vector2 mouse = GetGlobalMousePosition();
            TryHoverSwitchPageTab(delta, mouse);
            var destBoard = ButtonBoard.Live != null && GodotObject.IsInstanceValid(ButtonBoard.Live)
                ? ButtonBoard.Live
                : this;
            destBoard.AutoScrollBoardFromPointer(mouse, delta);
            UpdateCellDrag(mouse);
            return;
        }

        if (_boxSelect != null && _boxSelect.IsActive)
            _boxSelect.Tick(delta);
    }

    /// <summary>Serializes this board for the showfile / undo snapshot.</summary>
    public Dictionary GetData()
    {
        var cells = new Array();
        var cues = new Dictionary();
        var seen = new HashSet<int>();
        for (int r = 0; r < _rows; r++)
        {
            for (int c = 0; c < _cols; c++)
            {
                int id = GetCellCueId(r, c);
                if (id < 0)
                    continue;
                cells.Add(new Dictionary
                {
                    ["Row"] = r,
                    ["Col"] = c,
                    ["Id"] = id
                });
                foreach (var cue in CollectCueTree(id, seen))
                    cues[cue.Id.ToString()] = cue.GetData();
            }
        }

        var colWidths = new Array();
        foreach (float w in _colWidths)
            colWidths.Add(w);
        var rowHeights = new Array();
        foreach (float h in _rowHeights)
            rowHeights.Add(h);

        return new Dictionary
        {
            ["Kind"] = Kind,
            ["Name"] = ListName ?? "Button Board",
            ["Rows"] = _rows,
            ["Columns"] = _cols,
            ["ColumnWidths"] = colWidths,
            ["RowHeights"] = rowHeights,
            ["Cells"] = cells,
            ["Cues"] = cues
        };
    }

    /// <summary>Applies a saved board dictionary (name, grid, and cues).</summary>
    public void ApplyFromData(Dictionary data)
    {
        if (data == null)
            return;

        UnregisterCuesFromIndex();

        if (data.TryGetValue("Name", out var nameVar))
        {
            string name = nameVar.AsString();
            if (!string.IsNullOrWhiteSpace(name))
                ListName = name;
        }

        int rows = 1;
        int cols = 1;
        if (data.TryGetValue("Rows", out var rowsVar))
            rows = Math.Max(1, rowsVar.AsInt32());
        if (data.TryGetValue("Columns", out var colsVar))
            cols = Math.Max(1, colsVar.AsInt32());

        ResizeGrid(rows, cols, keepCues: false);

        ReadFloatList(data, "ColumnWidths", _colWidths, cols, MinColWidth, DefaultColWidth);
        ReadFloatList(data, "RowHeights", _rowHeights, rows, MinRowHeight, DefaultRowHeight);

        if (data.TryGetValue("Cues", out var cuesVar) && cuesVar.VariantType == Variant.Type.Dictionary)
        {
            foreach (var kv in cuesVar.AsGodotDictionary())
            {
                if (kv.Value.VariantType != Variant.Type.Dictionary)
                    continue;
                var cue = new Cue(kv.Value.AsGodotDictionary());
                RegisterCue(cue);
                _globalData?.Cuelist?.RelinkCueComponents(cue);
            }
        }

        if (data.TryGetValue("Cells", out var cellsVar) && cellsVar.VariantType == Variant.Type.Array)
        {
            foreach (var item in cellsVar.AsGodotArray())
            {
                if (item.VariantType != Variant.Type.Dictionary)
                    continue;
                var cell = item.AsGodotDictionary();
                int r = cell.TryGetValue("Row", out var rVar) ? rVar.AsInt32() : -1;
                int c = cell.TryGetValue("Col", out var cVar) ? cVar.AsInt32() : -1;
                int id = cell.TryGetValue("Id", out var idVar) ? idVar.AsInt32() : -1;
                if (r < 0 || c < 0 || r >= _rows || c >= _cols || id < 0)
                    continue;
                if (CueList.FetchCueFromId(id) == null)
                    continue;
                SetCellCueId(r, c, id);
            }
        }

        if (IsInsideTree())
            RebuildGrid();
        NotifyCueCount();
    }

    /// <summary>
    /// True when <paramref name="cueId"/> is a cell root on this board or a hidden descendant
    /// of one of those roots (child sequence).
    /// </summary>
    public bool ContainsCue(int cueId)
    {
        if (cueId < 0 || _cellCueIds == null)
            return false;
        for (int i = 0; i < _cellCueIds.Length; i++)
        {
            int rootId = _cellCueIds[i];
            if (rootId < 0)
                continue;
            if (rootId == cueId || CueTreeContains(rootId, cueId, 0))
                return true;
        }

        return false;
    }

    /// <summary>Removes this board's cues from <see cref="CueList.CueIndex"/> and resets to a 1×1 empty grid.</summary>
    public void ClearBoardForReload()
    {
        UnregisterCuesFromIndex();
        ResizeGrid(1, 1, keepCues: false);
        if (IsInsideTree())
            RebuildGrid();
        NotifyCueCount();
    }

    /// <summary>The board that owns <paramref name="cueId"/>, or null.</summary>
    public static ButtonBoard FindOwningBoard(int cueId)
    {
        if (Live != null && Live.ContainsCue(cueId))
            return Live;

        var workspace = Live?.GetParent() as CuelistWorkspace
                        ?? CueList.Live?.GetParent() as CuelistWorkspace;
        if (workspace == null)
            return null;
        foreach (var board in workspace.Boards)
        {
            if (board != null && GodotObject.IsInstanceValid(board) && board.ContainsCue(cueId))
                return board;
        }

        return null;
    }

    /// <summary>
    /// Resolves the cell under <paramref name="globalPos"/>. Returns false when the pointer is not on a cell.
    /// </summary>
    /// <param name="globalPos">Pointer position in global coordinates.</param>
    /// <param name="row">Cell row, or -1 when missed.</param>
    /// <param name="col">Cell column, or -1 when missed.</param>
    /// <param name="cueId">Cue id in that cell, or -1 when empty or missed.</param>
    /// <returns>True when a cell was hit.</returns>
    public bool TryGetCellAt(Vector2 globalPos, out int row, out int col, out int cueId)
    {
        row = -1;
        col = -1;
        cueId = -1;
        var cell = FindCellAtGlobal(globalPos);
        if (cell == null)
            return false;
        row = cell.Row;
        col = cell.Col;
        cueId = GetCellCueId(row, col);
        return true;
    }

    /// <summary>
    /// Imports dropped media onto the board: replace one occupied cell, or fill empty cells
    /// in row-major order starting at the drop cell.
    /// </summary>
    /// <param name="files">Valid media file paths.</param>
    /// <param name="startRow">Drop cell row, or -1 to start at the first empty cell.</param>
    /// <param name="startCol">Drop cell column, or -1 to start at the first empty cell.</param>
    /// <param name="action">Replace occupied media, or fill empty cells.</param>
    public void ImportDroppedFiles(string[] files, int startRow, int startCol, BoardDropAction action)
    {
        if (IsShowMode())
        {
            LogShowModeBlock("import dropped files");
            return;
        }

        if (files == null || files.Length == 0)
            return;

        EnsureModel();
        if (startRow < 0 || startCol < 0 || !IsValidCell(startRow, startCol))
        {
            startRow = 0;
            startCol = 0;
        }

        bool replace = action == BoardDropAction.Replace && files.Length == 1
                       && GetCellCueId(startRow, startCol) >= 0;

        if (_historyManager?.IsRestoring != true)
            _historyManager?.RecordCuelistChange(replace ? "Replace board cue media" : "Import media cues");

        if (replace)
        {
            var existing = FetchCellCue(startRow, startCol);
            if (existing != null)
            {
                ReplaceCueMedia(existing, files[0]);
                RebuildGrid();
                NotifyCueCount();
                _globalData?.ShellSelection?.SelectIndividualShell(existing, recordHistory: false);
                GD.Print($"ButtonBoard:ImportDroppedFiles - Replaced cue {existing.Id} at ({startRow},{startCol})");
                return;
            }
        }

        int row = startRow;
        int col = startCol;
        Cue firstCreated = null;
        int created = 0;
        foreach (string filePath in files)
        {
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
                continue;
            EnsureEmptySlotFrom(ref row, ref col);
            var cue = CreateMediaCueInCell(row, col, filePath);
            if (cue == null)
                continue;
            firstCreated ??= cue;
            created++;
            ExpandAround(row, col);
            AdvanceCell(ref row, ref col);
        }

        if (created == 0)
        {
            _globalSignals?.EmitSignal(nameof(GlobalSignals.Log),
                "ButtonBoard: No cues were created from the dropped files.", (int)LogType.Warning);
            return;
        }

        RebuildGrid();
        NotifyCueCount();
        MaybeSelectNewCue(firstCreated);
        _globalSignals?.EmitSignal(nameof(GlobalSignals.Log),
            $"ButtonBoard: Created {created} cue(s) from drop.", (int)LogType.Info);
        GD.Print($"ButtonBoard:ImportDroppedFiles - Created {created} cue(s) from ({startRow},{startCol})");
    }

    /// <summary>Creates a new cue in the empty cell at <paramref name="row"/>,<paramref name="col"/>.</summary>
    public void CreateCueAt(int row, int col)
    {
        if (IsShowMode())
        {
            LogShowModeBlock("create cues");
            return;
        }

        if (!IsValidCell(row, col) || GetCellCueId(row, col) >= 0)
            return;

        if (_historyManager?.IsRestoring != true)
            _historyManager?.RecordCuelistChange("Create cue");

        var cue = new Cue();
        _globalData?.Settings?.ApplyShellDefaults(cue);
        RegisterCueTree(cue);
        SetCellCueId(row, col, cue.Id);
        int prevRows = _rows;
        int prevCols = _cols;
        ExpandAround(row, col);
        RefreshGridAfterMutation(prevRows, prevCols, row, col);
        NotifyCueCount();
        MaybeSelectNewCue(cue);
        GD.Print($"ButtonBoard:CreateCueAt - cue {cue.Id} at ({row},{col})");
    }

    /// <summary>
    /// Occupied cues in row-major order (used for shift-range, select-all, next/previous).
    /// </summary>
    public List<Cue> GetOccupiedCuesInOrder()
    {
        var list = new List<Cue>();
        for (int r = 0; r < _rows; r++)
        {
            for (int c = 0; c < _cols; c++)
            {
                var cue = FetchCellCue(r, c);
                if (cue != null)
                    list.Add(cue);
            }
        }

        return list;
    }

    /// <summary>Occupied cues whose cells intersect <paramref name="globalRect"/>, in row-major order.</summary>
    internal List<Cue> CollectOccupiedCuesInRect(Rect2 globalRect)
    {
        var hits = new List<Cue>();
        foreach (var cell in _cells)
        {
            if (cell == null || !GodotObject.IsInstanceValid(cell) || !cell.IsOccupied)
                continue;
            if (!globalRect.Intersects(cell.GetGlobalRect()))
                continue;
            var cue = CueList.FetchCueFromId(cell.CueId);
            if (cue != null)
                hits.Add(cue);
        }

        return hits;
    }

    /// <summary>Edit-mode context menu for a board cell (cut / copy / paste / delete / create).</summary>
    public void ShowCellContextMenu(ButtonBoardCell cell)
    {
        if (cell == null || IsShowMode())
            return;

        _contextRow = cell.Row;
        _contextCol = cell.Col;
        if (cell.IsOccupied)
        {
            var cue = FetchCellCue(cell.Row, cell.Col);
            if (cue != null)
            {
                var selected = Cue2.Domain.Cuelist.ShellSelection.SelectedCues;
                if (selected == null || !selected.Contains(cue))
                    _globalData?.ShellSelection?.SelectIndividualShell(cue);
            }
        }

        EnsureContextMenu();
        _contextMenu.Clear();
        bool occupied = cell.IsOccupied;
        AddContextMenuItem("Cut", "CutSelectedCues", 0, occupied);
        AddContextMenuItem("Copy", "CopySelectedCues", 1, occupied);
        AddContextMenuItem("Paste", "PasteCues", 2, enabled: true);
        _contextMenu.AddSeparator();
        AddContextMenuItem("Delete", "DeleteCue", 3, occupied);
        _contextMenu.AddSeparator();
        AddContextMenuItem("Create Cue", "CreateCue", 4, enabled: true);
        UiUtilities.PopupScaledAtMouse(_contextMenu, this);
    }

    private void EnsureContextMenu()
    {
        if (_contextMenu != null && GodotObject.IsInstanceValid(_contextMenu))
            return;

        _contextMenu = new PopupMenu
        {
            Name = "BoardContextMenu",
            HideOnItemSelection = true
        };
        Node host = GetNodeOrNull("/root/Cue2Base") ?? this;
        host.AddChild(_contextMenu);
        _contextMenu.IdPressed += OnContextMenuIdPressed;
    }

    private void AddContextMenuItem(string title, string inputAction, int id, bool enabled)
    {
        _contextMenu.AddItem(UiLocalizer.T(title), id);
        int index = _contextMenu.ItemCount - 1;
        _contextMenu.SetItemDisabled(index, !enabled);
        if (string.IsNullOrEmpty(inputAction) || !InputMap.HasAction(inputAction))
            return;

        var shortcut = new Shortcut();
        var events = new Array();
        foreach (InputEvent ev in InputMap.ActionGetEvents(inputAction))
        {
            if (ev == null)
                continue;
            events.Add((InputEvent)ev.Duplicate());
        }

        shortcut.Events = events;
        _contextMenu.SetItemShortcut(index, shortcut, global: false);
        _contextMenu.SetItemShortcutDisabled(index, true);
    }

    private void OnContextMenuIdPressed(long id)
    {
        switch (id)
        {
            case 0:
                OnCutSelectedCues();
                break;
            case 1:
                OnCopySelectedCues();
                break;
            case 2:
                OnPasteCues();
                break;
            case 3:
                OnDeleteSelectedCues();
                break;
            case 4:
                CreateCueNear(_contextRow, _contextCol);
                break;
        }
    }

    private void OnCreateCue()
    {
        if (Live != this || IsShowMode())
            return;

        int row = 0;
        int col = 0;
        int focusId = _globalData?.FocusedCue ?? -1;
        if (focusId >= 0)
        {
            for (int r = 0; r < _rows; r++)
            {
                for (int c = 0; c < _cols; c++)
                {
                    if (GetCellCueId(r, c) != focusId)
                        continue;
                    row = r;
                    col = c;
                    break;
                }
            }
        }

        CreateCueNear(row, col);
    }

    private void CreateCueNear(int row, int col)
    {
        if (IsShowMode())
            return;
        EnsureModel();
        if (!IsValidCell(row, col))
        {
            row = 0;
            col = 0;
        }

        if (GetCellCueId(row, col) < 0)
        {
            CreateCueAt(row, col);
            return;
        }

        AdvanceCell(ref row, ref col);
        EnsureEmptySlotFrom(ref row, ref col);
        CreateCueAt(row, col);
    }

    /// <summary>Starts pending box-select (click vs marquee) from a cell or empty board area.</summary>
    public void BeginPotentialBoxSelect(ButtonBoardCell origin, Vector2 globalPos, bool additive)
    {
        if (_dragging || _resizing)
            return;
        _boxSelect?.BeginPending(origin, globalPos, additive);
    }

    /// <summary>Creates a cue on an empty-cell click that did not become a marquee.</summary>
    internal void TryCreateCueFromEmptyClick(int row, int col)
    {
        if (IsShowMode())
        {
            _globalData?.ShellSelection?.ClearSelection();
            RefreshSelectionChrome();
            return;
        }

        CreateCueAt(row, col);
    }

    /// <summary>Repaints cell selection chrome after a selection change that does not emit focus.</summary>
    internal void RefreshSelectionChrome()
    {
        foreach (var cell in _cells)
        {
            if (cell != null && GodotObject.IsInstanceValid(cell))
                cell.RefreshSelectionChrome();
        }
    }

    /// <summary>Enables <c>_Input</c>/<c>_Process</c> while reorder, resize, or box-select needs the pointer.</summary>
    internal void SyncPointerProcessing()
    {
        bool boxTracking = _boxSelect != null && (_boxSelect.IsPending || _boxSelect.IsActive);
        bool boxActive = _boxSelect != null && _boxSelect.IsActive;
        SetProcessInput(_dragging || _resizing || boxTracking);
        SetProcess(_dragging || _resizing || boxActive);
    }

    private void OnScrollGuiInput(InputEvent @event)
    {
        if (@event is not InputEventMouseButton mb || !mb.Pressed)
            return;
        if (mb.ButtonIndex != MouseButton.Left)
            return;
        if (_dragging || _resizing)
            return;
        if (FindCellAtGlobal(mb.GlobalPosition) != null)
            return;
        if (Input.IsKeyPressed(Key.Shift))
            return;

        bool additive = mb.CtrlPressed || mb.MetaPressed;
        BeginPotentialBoxSelect(null, mb.GlobalPosition, additive);
    }

    /// <summary>Selects the cue in this cell for inspector editing (edit mode).</summary>
    public void SelectCueAt(int row, int col, InputEventMouseButton mb)
    {
        var cue = FetchCellCue(row, col);
        if (cue == null)
            return;

        bool shift = mb != null && mb.ShiftPressed;
        bool ctrl = mb != null && (mb.CtrlPressed || mb.MetaPressed);
        if (shift)
        {
            var last = Cue2.Domain.Cuelist.ShellSelection.SelectedCues != null
                       && Cue2.Domain.Cuelist.ShellSelection.SelectedCues.Count > 0
                ? Cue2.Domain.Cuelist.ShellSelection.SelectedCues[^1]
                : null;
            if (last != null && ContainsCue(last.Id))
                _globalData?.ShellSelection?.SelectThrough(cue);
            else
                _globalData?.ShellSelection?.SelectIndividualShell(cue);
            RefreshSelectionChrome();
            return;
        }

        if (ctrl)
            _globalData?.ShellSelection?.ToggleSelection(cue);
        else
            _globalData?.ShellSelection?.SelectIndividualShell(cue);
        RefreshSelectionChrome();
    }

    /// <summary>GOs the cue in this cell.</summary>
    public void PlayCueAt(int row, int col)
    {
        var cue = FetchCellCue(row, col);
        if (cue == null)
            return;
        if (!cue.Armed)
        {
            GD.Print($"ButtonBoard:PlayCueAt - Skipping disarmed cue {cue.Name} (id={cue.Id})");
            return;
        }

        _globalData?.CueCommandExecutor?.ActivateSequenceFrom(cue);
    }

    /// <summary>
    /// Starts a spreadsheet-style column or row resize from a shared divider.
    /// </summary>
    /// <param name="handle">Divider being dragged.</param>
    /// <param name="globalPos">Pointer position at press.</param>
    public void BeginAxisResize(ButtonBoardResizeHandle handle, Vector2 globalPos)
    {
        if (handle == null || IsShowMode() || _contentHost == null)
            return;
        if (!GodotObject.IsInstanceValid(handle))
            return;

        _resizing = true;
        _resizeHandle = handle;
        _resizeVertical = handle.Vertical;
        _resizeIndex = handle.Index;
        _resizeStartLocal = ToContentLocal(globalPos);
        _resizeStartSize = handle.Vertical ? ColWidth(handle.Index) : RowHeight(handle.Index);
        _resizeStartColWidths = new List<float>(_colWidths);
        _resizeStartRowHeights = new List<float>(_rowHeights);
        MouseDefaultCursorShape = handle.Vertical ? CursorShape.Hsize : CursorShape.Vsize;
        handle.SetDragging(true);
        _boxSelect?.Cancel();
        SyncPointerProcessing();
        UpdateAxisResize(globalPos);
    }

    /// <summary>Highlights a cell as an external (cuelist reorder) drop target.</summary>
    internal void SetExternalDropTarget(ButtonBoardCell cell)
    {
        if (_dropTarget == cell)
            return;
        _dropTarget?.SetDropHighlight(false);
        _dropTarget = cell != null && GodotObject.IsInstanceValid(cell) ? cell : null;
        _dropTarget?.SetDropHighlight(true);
    }

    /// <summary>Clears an external drop-target highlight.</summary>
    internal void ClearExternalDropTarget()
    {
        _dropTarget?.SetDropHighlight(false);
        _dropTarget = null;
    }

    /// <summary>
    /// Places cues already in <see cref="CueList.CueIndex"/> onto this board (move from a cuelist).
    /// Occupied drop cell swaps that cue onto <paramref name="swapDestList"/> when provided.
    /// </summary>
    internal void AcceptMovedCues(IReadOnlyList<Cue> cues, int row, int col, CueList swapDestList)
    {
        if (cues == null || cues.Count == 0)
            return;

        EnsureModel();
        if (row < 0 || col < 0 || !IsValidCell(row, col))
        {
            row = 0;
            col = 0;
        }

        int occupyingId = GetCellCueId(row, col);
        if (occupyingId >= 0 && swapDestList != null)
        {
            var occupying = FetchCellCue(row, col);
            SetCellCueId(row, col, -1);
            if (occupying != null)
                swapDestList.InsertCueInModel(occupying, -1, swapDestList.RootOrder.Count);
        }

        var list = cues as List<Cue> ?? new List<Cue>(cues);
        PlaceCuesFrom(row, col, list, occupyTarget: true);
        ShrinkUnusedEdges();
        RebuildGrid();
        NotifyCueCount();
        _globalData?.ShellSelection?.SetSelection(list, list[^1], recordHistory: false, emitFocus: true);
        RefreshSelectionChrome();
    }

    /// <summary>
    /// Removes cues from board cells without unregistering them from <see cref="CueList.CueIndex"/>.
    /// </summary>
    internal void ReleaseMovedCues(IReadOnlyList<Cue> cues)
    {
        if (cues == null || cues.Count == 0)
            return;
        foreach (var cue in cues)
        {
            if (cue != null)
                ClearCellForCue(cue.Id);
        }

        ShrinkUnusedEdges();
        RebuildGrid();
        NotifyCueCount();
    }

    private void OnShowModeChanged(bool enabled)
    {
        foreach (var cell in _cells)
            cell.SetShowMode(enabled);
        if (enabled && _dragging)
            CancelCellDrag();
        if (enabled && _resizing)
            EndAxisResize();
        if (enabled)
        {
            _boxSelect?.Cancel();
            if (Live == this)
            {
                _globalData?.ShellSelection?.ClearSelection(recordHistory: false);
                RefreshSelectionChrome();
            }
        }
        ApplyHandleInteractable();
    }

    private void OnLocaleChanged(string locale)
    {
        UiLocalizer.LocalizeTree(this);
    }

    private void OnThemeColorsChanged()
    {
        foreach (var cell in _cells)
            cell?.RefreshAccentColors();
        foreach (var handle in _colHandles)
            handle?.QueueRedraw();
        foreach (var handle in _rowHandles)
            handle?.QueueRedraw();
    }

    private void OnHistoryRestored(int scope)
    {
        RefreshSelectionChrome();
    }

    private void OnShellFocused(int cueId)
    {
        RefreshSelectionChrome();
    }

    private Cue CreateMediaCueInCell(int row, int col, string filePath)
    {
        if (!IsValidCell(row, col) || GetCellCueId(row, col) >= 0)
            return null;

        var cue = new Cue();
        _globalData?.Settings?.ApplyShellDefaults(cue);
        string baseName = Path.GetFileNameWithoutExtension(filePath);
        cue.Name = string.IsNullOrWhiteSpace(baseName) ? $"Cue {cue.Id}" : baseName;
        cue.CueNum = cue.Id.ToString();
        RegisterCueTree(cue);
        if (!AttachMediaToCue(cue, filePath))
        {
            UnregisterCueTree(cue.Id);
            return null;
        }

        SetCellCueId(row, col, cue.Id);
        return cue;
    }

    private void ReplaceCueMedia(Cue cue, string filePath)
    {
        if (cue == null || string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
            return;

        string ext = Path.GetExtension(filePath).ToLowerInvariant();
        bool isAudio = IsAudioExtension(ext);
        bool isVideoOrImage = IsVideoOrImageExtension(ext);
        if (!isAudio && !isVideoOrImage)
            return;

        if (isAudio)
        {
            var video = cue.GetVideoComponent();
            if (video != null)
                cue.RemoveICueComponent(video);
            var input = cue.GetAudioInputComponent();
            if (input != null)
                cue.RemoveICueComponent(input);
        }
        else
        {
            var audio = cue.GetAudioComponent();
            if (audio != null)
                cue.RemoveICueComponent(audio);
            var input = cue.GetAudioInputComponent();
            if (input != null)
                cue.RemoveICueComponent(input);
        }

        AttachMediaToCue(cue, filePath);
        string baseName = Path.GetFileNameWithoutExtension(filePath);
        if (!string.IsNullOrWhiteSpace(baseName))
            cue.Name = baseName;
        cue.CalculateTotalDuration();
        _globalSignals?.EmitSignal(nameof(GlobalSignals.UpdateShellBar), cue.Id);
        _globalSignals?.EmitSignal(nameof(GlobalSignals.SyncShellInspector));
        GetNodeOrNull<MediaHealthService>("/root/MediaHealthService")?.CheckCue(cue.Id);
    }

    private bool AttachMediaToCue(Cue cue, string filePath)
    {
        if (cue == null || string.IsNullOrEmpty(filePath))
            return false;

        string ext = Path.GetExtension(filePath).ToLowerInvariant();
        bool isAudio = IsAudioExtension(ext);
        bool isVideoOrImage = IsVideoOrImageExtension(ext);
        if (!isAudio && !isVideoOrImage)
            return false;

        string pathToStore = ResolveMediaPathForNewCue(filePath, isAudio);
        if (isAudio)
        {
            var existing = cue.GetAudioComponent();
            if (existing != null)
            {
                bool pathChanged = !string.Equals(existing.AudioFile, pathToStore, StringComparison.OrdinalIgnoreCase);
                existing.AudioFile = pathToStore;
                if (pathChanged)
                {
                    existing.WaveformData = null;
                    existing.Metadata = null;
                    existing.ClearTimelineNodes();
                }

                existing.StartTime = 0.0;
                existing.EndTime = -1.0;
                existing.RecalculateDuration();
            }
            else
            {
                cue.AddAudioComponent(pathToStore);
            }
        }
        else
        {
            var existing = cue.GetVideoComponent();
            if (existing != null)
            {
                bool pathChanged = !string.Equals(existing.VideoFile, pathToStore, StringComparison.OrdinalIgnoreCase);
                existing.VideoFile = pathToStore;
                existing.RefreshIsImageFromPath();
                if (pathChanged)
                {
                    existing.WaveformData = null;
                    existing.Metadata = null;
                }

                existing.StartTime = 0.0;
                existing.EndTime = -1.0;
            }
            else
            {
                cue.AddVideoComponent(pathToStore);
            }
        }

        _globalData?.Cuelist?.RelinkCueComponents(cue);
        var mediaEngine = GetNodeOrNull<MediaEngine>("/root/MediaEngine");
        _ = ApplyMetadataToNewCueAsync(cue, filePath, mediaEngine);
        GetNodeOrNull<MediaHealthService>("/root/MediaHealthService")?.CheckCue(cue.Id);
        return true;
    }

    private string ResolveMediaPathForNewCue(string absolutePath, bool isAudio)
    {
        try
        {
            var backup = GetNodeOrNull<MediaBackupManager>("/root/MediaBackupManager");
            if (backup == null)
                return absolutePath;

            var kind = isAudio
                ? MediaBackupKind.Audio
                : MediaBackupManager.DetectKindFromPath(absolutePath);

            string relative = backup.EnsureMediaBackedUp(absolutePath, kind);
            return string.IsNullOrEmpty(relative) ? absolutePath : relative;
        }
        catch (Exception ex)
        {
            GD.PrintErr($"ButtonBoard:ResolveMediaPathForNewCue - {ex.Message}");
            return absolutePath;
        }
    }

    private async Task ApplyMetadataToNewCueAsync(Cue cue, string filePath, MediaEngine mediaEngine)
    {
        if (cue == null || string.IsNullOrEmpty(filePath) || mediaEngine == null)
            return;

        try
        {
            var audioComp = cue.GetAudioComponent();
            var videoComp = cue.GetVideoComponent();

            if (audioComp != null)
            {
                var probe = await mediaEngine.ProbeAudioFileMetadataWithRetryAsync(filePath);
                if (probe.Metadata != null)
                {
                    audioComp.Metadata = probe.Metadata;
                    audioComp.RecalculateDuration();
                    try
                    {
                        audioComp.WaveformData = await mediaEngine.GenerateWaveformAsync(filePath);
                    }
                    catch
                    {
                        /* non-fatal */
                    }
                }
                else
                {
                    // Leave Metadata empty so selecting or playing the cue reads the file again.
                    _globalSignals?.EmitSignal(nameof(GlobalSignals.Log),
                        $"Could not read {Path.GetFileName(filePath)} for cue '{cue.Name}': {probe.Error}. The cue will retry when selected or played.",
                        (int)LogType.Warning);
                }
            }
            else if (videoComp != null)
            {
                videoComp.RefreshIsImageFromPath();
                var meta = await mediaEngine.GetVideoFileMetadataAsync(filePath);
                videoComp.Metadata = meta;
                if (videoComp.IsImage)
                {
                    videoComp.HasAudio = false;
                    videoComp.UseAudio = false;
                    videoComp.StartTime = 0;
                    videoComp.EndTime = -1;
                }
                else
                {
                    videoComp.HasAudio = meta.AudioChannels > 0;
                    videoComp.UseAudio = videoComp.HasAudio;
                }

                videoComp.RecalculateDuration();
            }

            cue.CalculateTotalDuration();
            _globalSignals?.EmitSignal(nameof(GlobalSignals.UpdateShellBar), cue.Id);
            if (_globalData != null && _globalData.FocusedCue == cue.Id)
            {
                _globalSignals?.EmitSignal(nameof(GlobalSignals.ShellFocused), cue.Id);
                _globalSignals?.EmitSignal(nameof(GlobalSignals.SyncShellInspector));
            }
        }
        catch (Exception ex)
        {
            _globalSignals?.EmitSignal(nameof(GlobalSignals.Log),
                $"ButtonBoard: Failed to load metadata for dropped file '{Path.GetFileName(filePath)}': {ex.Message}",
                (int)LogType.Warning);
        }
    }

    private static bool IsAudioExtension(string ext)
    {
        return GlobalData.AudioFileFilters.Any(e => e.TrimStart('*').Equals(ext, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsVideoOrImageExtension(string ext)
    {
        return GlobalData.VideoFileFilters.Any(e => e.TrimStart('*').Equals(ext, StringComparison.OrdinalIgnoreCase))
               || GlobalData.ImageFileFilters.Any(e => e.TrimStart('*').Equals(ext, StringComparison.OrdinalIgnoreCase));
    }

    private void ExpandAround(int row, int col)
    {
        if (row + 1 >= _rows)
            AppendRow();
        if (col + 1 >= _cols && _cols < MaxColumnsForViewport())
            AppendColumn();
    }

    private void ShrinkUnusedEdges()
    {
        if (!HasAnyCue())
        {
            ResizeGrid(1, 1, keepCues: false);
            return;
        }

        int maxR = 0;
        int maxC = 0;
        for (int r = 0; r < _rows; r++)
        {
            for (int c = 0; c < _cols; c++)
            {
                if (GetCellCueId(r, c) < 0)
                    continue;
                maxR = Math.Max(maxR, r);
                maxC = Math.Max(maxC, c);
            }
        }

        int maxFit = MaxColumnsForViewport();
        int needRows = Math.Max(1, maxR + 2);
        int needCols = Math.Max(1, maxC + 1);
        if (needCols < maxFit)
            needCols = Math.Min(maxFit, maxC + 2);
        if (needRows < _rows || needCols < _cols)
            ResizeGrid(needRows, needCols, keepCues: true);
    }

    private void AppendRow()
    {
        ResizeGrid(_rows + 1, _cols, keepCues: true);
    }

    private void AppendColumn()
    {
        ResizeGrid(_rows, _cols + 1, keepCues: true);
    }

    private void ResizeGrid(int rows, int cols, bool keepCues)
    {
        rows = Math.Max(1, rows);
        cols = Math.Max(1, cols);
        var next = new int[rows * cols];
        for (int i = 0; i < next.Length; i++)
            next[i] = -1;
        if (keepCues && _cellCueIds != null)
        {
            int copyR = Math.Min(rows, _rows);
            int copyC = Math.Min(cols, _cols);
            for (int r = 0; r < copyR; r++)
            {
                for (int c = 0; c < copyC; c++)
                    next[r * cols + c] = GetCellCueId(r, c);
            }
        }

        _rows = rows;
        _cols = cols;
        _cellCueIds = next;
        while (_colWidths.Count < _cols)
            _colWidths.Add(DefaultColWidth);
        while (_colWidths.Count > _cols)
            _colWidths.RemoveAt(_colWidths.Count - 1);
        while (_rowHeights.Count < _rows)
            _rowHeights.Add(DefaultRowHeight);
        while (_rowHeights.Count > _rows)
            _rowHeights.RemoveAt(_rowHeights.Count - 1);
    }

    private void EnsureModel()
    {
        if (_cellCueIds != null && _cellCueIds.Length == _rows * _cols && _rows > 0 && _cols > 0)
            return;
        ResizeGrid(1, 1, keepCues: false);
    }

    private int GetCellCueId(int row, int col)
    {
        if (!IsValidCell(row, col))
            return -1;
        return _cellCueIds[row * _cols + col];
    }

    private void SetCellCueId(int row, int col, int cueId)
    {
        if (!IsValidCell(row, col))
            return;
        _cellCueIds[row * _cols + col] = cueId;
    }

    private bool IsValidCell(int row, int col)
    {
        return row >= 0 && col >= 0 && row < _rows && col < _cols && _cellCueIds != null;
    }

    private Cue FetchCellCue(int row, int col)
    {
        return CueList.FetchCueFromId(GetCellCueId(row, col));
    }

    private bool HasAnyCue()
    {
        if (_cellCueIds == null)
            return false;
        for (int i = 0; i < _cellCueIds.Length; i++)
        {
            if (_cellCueIds[i] >= 0)
                return true;
        }

        return false;
    }

    private void ClearCellForCue(int cueId)
    {
        if (_cellCueIds == null)
            return;
        for (int i = 0; i < _cellCueIds.Length; i++)
        {
            if (_cellCueIds[i] == cueId)
                _cellCueIds[i] = -1;
        }
    }

    private void RegisterCue(Cue cue)
    {
        if (cue == null)
            return;
        CueList.CueIndex ??= new System.Collections.Generic.Dictionary<int, Cue>();
        CueList.CueIndex[cue.Id] = cue;
    }

    private void UnregisterCuesFromIndex()
    {
        if (_cellCueIds == null)
            return;
        for (int i = 0; i < _cellCueIds.Length; i++)
        {
            int id = _cellCueIds[i];
            if (id >= 0)
                UnregisterCueTree(id);
            _cellCueIds[i] = -1;
        }
    }

    private void MaybeSelectNewCue(Cue cue)
    {
        if (cue == null)
            return;
        if (_globalData?.Settings?.SelectNewCues != true)
            return;
        _globalData.ShellSelection?.SelectIndividualShell(cue, recordHistory: false);
    }

    private void NotifyCueCount()
    {
        int total = CueList.CueIndex?.Count ?? 0;
        if (_globalData != null)
            _globalData.CueTotal = total;
        _globalSignals?.EmitSignal(nameof(GlobalSignals.TotalCuesChanged), total);
    }

    private bool IsShowMode() => _globalData?.Settings?.IsCueEditingLocked == true;

    private void LogShowModeBlock(string actionLabel)
    {
        _globalSignals?.EmitSignal(nameof(GlobalSignals.Log),
            $"Show Mode: cannot {actionLabel}. Turn off Show Mode to edit cues.", (int)LogType.Info);
    }

    private static void ReadFloatList(
        Dictionary data,
        string key,
        List<float> dest,
        int count,
        float min,
        float fallback)
    {
        dest.Clear();
        if (data.TryGetValue(key, out var varList) && varList.VariantType == Variant.Type.Array)
        {
            foreach (var item in varList.AsGodotArray())
                dest.Add(Math.Max(min, item.AsSingle()));
        }

        while (dest.Count < count)
            dest.Add(fallback);
        while (dest.Count > count)
            dest.RemoveAt(dest.Count - 1);
    }
}
