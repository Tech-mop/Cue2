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

    /// <summary>Starts a cell-to-cell drag from the reorder grabber.</summary>
    public void BeginCellDrag(ButtonBoardCell cell, Vector2 globalPos)
    {
        if (cell == null || !cell.IsOccupied || IsShowMode())
            return;

        var cue = CueList.FetchCueFromId(cell.CueId);
        if (cue != null && (ShellSelection.SelectedCues == null || !ShellSelection.SelectedCues.Contains(cue)))
            _globalData?.ShellSelection?.SelectIndividualShell(cue, recordHistory: false);

        _boxSelect?.Cancel();
        _dragging = true;
        _dragSource = cell;
        _dropTarget = null;
        _hoverTabTarget = null;
        _hoverTabElapsed = 0;
        cell.SetDragSource(true);
        (GetParent() as CuelistWorkspace)?.MovePreview?.Begin(
            Cue2.Domain.Cuelist.ShellSelection.SelectedCues);
        UpdateCellDrag(globalPos);
        SyncPointerProcessing();
    }

    private void UpdateCellDrag(Vector2 globalPos)
    {
        var workspace = GetParent() as CuelistWorkspace;
        var tabPage = workspace?.GetPageTabAtGlobalPoint(globalPos);
        var preview = (GetParent() as CuelistWorkspace)?.MovePreview;
        preview?.Follow(globalPos);

        if (tabPage is CueList tabList)
        {
            ClearLocalDropHighlight();
            ButtonBoard.Live?.ClearExternalDropTarget();
            preview?.SetLocation(UiLocalizer.Tf("Move to: {0}", tabList.ListName ?? "Cue List"));
            preview?.SetDropLine(false);
            return;
        }

        if (tabPage is ButtonBoard tabBoard && tabBoard != this)
        {
            ClearLocalDropHighlight();
            ButtonBoard.Live?.ClearExternalDropTarget();
            preview?.SetLocation(UiLocalizer.Tf("Move to: {0}", tabBoard.ListName ?? "Button Board"));
            preview?.SetDropLine(false);
            return;
        }

        if (CueList.Live != null && GodotObject.IsInstanceValid(CueList.Live) && tabPage is not ButtonBoard)
        {
            ClearLocalDropHighlight();
            ButtonBoard.Live?.ClearExternalDropTarget();
            ApplyListDropPreview(CueList.Live, globalPos, preview);
            return;
        }

        var destBoard = ResolveDragDestBoard();
        var over = destBoard?.FindCellAtGlobal(globalPos);
        if (destBoard != null && destBoard != this)
        {
            ClearLocalDropHighlight();
            destBoard.SetExternalDropTarget(over);
            ApplyBoardDropPreview(over, destBoard, preview, isSource: false);
            return;
        }

        ButtonBoard.Live?.ClearExternalDropTarget();
        if (over != _dropTarget)
        {
            _dropTarget?.SetDropHighlight(false);
            _dropTarget = over != null && over != _dragSource ? over : null;
            _dropTarget?.SetDropHighlight(true);
        }

        ApplyBoardDropPreview(over, this, preview, isSource: over == _dragSource);
    }

    private void EndCellDrag(Vector2 globalPos)
    {
        var source = _dragSource;
        FinishCellDragChrome();
        ButtonBoard.Live?.ClearExternalDropTarget();

        if (source == null || IsShowMode())
            return;

        var workspace = GetParent() as CuelistWorkspace;
        var tabPage = workspace?.GetPageTabAtGlobalPoint(globalPos);

        if (tabPage is CueList tabList)
        {
            CommitBoardDragToList(tabList, globalPos);
            return;
        }

        if (CueList.Live != null && GodotObject.IsInstanceValid(CueList.Live) && tabPage is not ButtonBoard)
        {
            CommitBoardDragToList(CueList.Live, globalPos);
            return;
        }

        var destBoard = tabPage as ButtonBoard ?? ResolveDragDestBoard();
        if (destBoard != null && destBoard != this)
        {
            destBoard.TryGetCellAt(globalPos, out int row, out int col, out _);
            CommitBoardDragToBoard(destBoard, row, col);
            return;
        }

        var dest = FindCellAtGlobal(globalPos);
        if (dest == null || dest == source)
            return;

        int fromId = GetCellCueId(source.Row, source.Col);
        int toId = GetCellCueId(dest.Row, dest.Col);
        if (fromId < 0)
            return;

        if (_historyManager?.IsRestoring != true)
            _historyManager?.RecordCuelistChange(toId >= 0 ? "Swap board cues" : "Move board cue");

        SetCellCueId(source.Row, source.Col, toId);
        SetCellCueId(dest.Row, dest.Col, fromId);
        int prevRows = _rows;
        int prevCols = _cols;
        ExpandAround(dest.Row, dest.Col);
        if (toId >= 0)
            ExpandAround(source.Row, source.Col);
        ShrinkUnusedEdges();
        RefreshGridAfterMutation(prevRows, prevCols, source.Row, source.Col, dest.Row, dest.Col);
    }

    private void CancelCellDrag()
    {
        FinishCellDragChrome();
        var workspace = GetParent() as CuelistWorkspace;
        if (workspace != null && Live != this)
            workspace.HoverActivatePageDuringReorder(this);
    }

    private void FinishCellDragChrome()
    {
        _dropTarget?.SetDropHighlight(false);
        _dragSource?.SetDragSource(false);
        _dragging = false;
        _dragSource = null;
        _dropTarget = null;
        _hoverTabTarget = null;
        _hoverTabElapsed = 0;
        (GetParent() as CuelistWorkspace)?.MovePreview?.End();
        SyncPointerProcessing();
    }

    private ButtonBoard ResolveDragDestBoard()
    {
        var live = Live;
        if (live != null && GodotObject.IsInstanceValid(live))
            return live;
        return this;
    }

    private void ClearLocalDropHighlight()
    {
        _dropTarget?.SetDropHighlight(false);
        _dropTarget = null;
    }

    private static void ApplyBoardDropPreview(ButtonBoardCell over, ButtonBoard board, CueMovePreview preview, bool isSource)
    {
        if (preview == null)
            return;
        preview.SetDropLine(false);
        if (isSource)
            preview.SetLocation(UiLocalizer.T("Release to cancel"));
        else if (over == null)
            preview.SetLocation(UiLocalizer.Tf("Move to: {0}", board?.ListName ?? "Button Board"));
        else if (over.IsOccupied)
        {
            var cue = CueList.FetchCueFromId(over.CueId);
            string name = string.IsNullOrWhiteSpace(cue?.Name) ? (cue?.CueNum ?? "?") : cue.Name;
            preview.SetLocation(UiLocalizer.Tf("Swap with: {0}", name));
        }
        else
            preview.SetLocation(UiLocalizer.T("Move to empty cell"));
    }

    private static void ApplyListDropPreview(CueList list, Vector2 globalPos, CueMovePreview preview)
    {
        if (preview == null || list == null)
            return;

        var lastShell = list.GetLastVisibleShellBar();
        var listView = list.GetCueListScrollGlobalRect();
        bool overList = listView.Size.Y > 1f && listView.Grow(8f).HasPoint(globalPos);
        var shell = list.FindVisibleShellBarAtGlobalY(globalPos.Y);

        if (lastShell == null && overList)
        {
            preview.SetLocation(UiLocalizer.T("Reorder at end (top level)"));
            var view = list.GetCueListScrollGlobalRect();
            preview.SetDropLine(true, view.Position, new Vector2(Mathf.Max(2f, view.Size.X), 2));
            return;
        }

        if (lastShell != null)
        {
            float lastBottom = lastShell.GetGlobalPosition().Y + lastShell.Size.Y;
            if (globalPos.Y > lastBottom + 8f)
            {
                preview.SetLocation(UiLocalizer.T("Reorder at end (top level)"));
                preview.SetDropLine(
                    true,
                    new Vector2(lastShell.GetGlobalPosition().X, lastBottom),
                    new Vector2(Mathf.Max(2f, lastShell.Size.X), 2));
                return;
            }
        }

        if (shell != null)
        {
            var target = CueList.FetchCueFromId(shell.CueId);
            string name = target?.Name ?? "?";
            preview.SetLocation(UiLocalizer.Tf("Reorder below: {0}", name));
            var pos = shell.GetGlobalPosition();
            preview.SetDropLine(
                true,
                new Vector2(pos.X, pos.Y + shell.Size.Y),
                new Vector2(shell.Size.X, 2));
            return;
        }

        preview.SetLocation(UiLocalizer.T("Cannot reorder here"));
        preview.SetDropLine(false);
    }

    private void TryHoverSwitchPageTab(double delta, Vector2 mouse)
    {
        var workspace = GetParent() as CuelistWorkspace;
        if (workspace == null)
            return;

        var page = workspace.GetPageTabAtGlobalPoint(mouse);
        bool alreadyVisible = page != null
            && ((page is CueList list && CueList.Live == list)
                || (page is ButtonBoard board && Live == board));
        if (page == null || alreadyVisible)
        {
            _hoverTabTarget = null;
            _hoverTabElapsed = 0;
            return;
        }

        if (!ReferenceEquals(page, _hoverTabTarget))
        {
            _hoverTabTarget = page;
            _hoverTabElapsed = 0;
            return;
        }

        _hoverTabElapsed += delta;
        if (_hoverTabElapsed < TabHoverSwitchSeconds)
            return;

        _hoverTabElapsed = 0;
        workspace.HoverActivatePageDuringReorder(page);
    }

    private void CommitBoardDragToList(CueList dest, Vector2 globalPos)
    {
        if (dest == null || !GodotObject.IsInstanceValid(dest))
            return;

        var toMove = CollectSelectedBoardRoots();
        if (toMove.Count == 0)
            return;

        if (_historyManager?.IsRestoring != true)
            _historyManager?.RecordCuelistChange("Move cues");

        ReleaseMovedCues(toMove);

        int parentId = -1;
        int insertIndex = dest.RootOrder.Count;
        var shell = dest.FindVisibleShellBarAtGlobalY(globalPos.Y);
        if (shell != null)
        {
            var target = CueList.FetchCueFromId(shell.CueId);
            if (target != null && dest.ContainsCue(target.Id))
            {
                parentId = target.ParentId;
                var siblings = dest.GetSiblingIdList(parentId) ?? dest.RootOrder;
                insertIndex = siblings.IndexOf(target.Id);
                if (insertIndex < 0)
                    insertIndex = siblings.Count;
                else
                    insertIndex++;
            }
        }

        dest.BeginVirtualRefreshSuppress();
        try
        {
            foreach (var cue in toMove)
            {
                dest.InsertCueInModel(cue, parentId, insertIndex);
                insertIndex++;
            }
        }
        finally
        {
            dest.EndVirtualRefreshSuppress();
        }

        dest.SyncVirtualViewport();
        _globalData?.ShellSelection?.SetSelection(toMove, toMove[^1], recordHistory: false, emitFocus: true);
    }

    private void CommitBoardDragToBoard(ButtonBoard dest, int row, int col)
    {
        if (dest == null || !GodotObject.IsInstanceValid(dest) || dest == this)
            return;

        var toMove = CollectSelectedBoardRoots();
        if (toMove.Count == 0)
            return;

        if (_historyManager?.IsRestoring != true)
            _historyManager?.RecordCuelistChange("Move cues");

        ReleaseMovedCues(toMove);
        dest.AcceptMovedCues(toMove, row, col, swapDestList: null);
    }

    private List<Cue> CollectSelectedBoardRoots()
    {
        var selected = GetSelectedCuesOnBoard();
        if (selected.Count == 0)
            return selected;

        var ids = new HashSet<int>(selected.Select(c => c.Id));
        var roots = new List<Cue>();
        foreach (var cue in GetOccupiedCuesInOrder())
        {
            if (!ids.Contains(cue.Id))
                continue;
            if (cue.ParentId >= 0 && ids.Contains(cue.ParentId))
                continue;
            roots.Add(cue);
        }

        return roots;
    }

    /// <summary>Edge auto-scroll of the board viewport while dragging or box-selecting.</summary>
    internal void AutoScrollBoardFromPointer(Vector2 globalMouse, double delta)
    {
        if (_scroll == null || !GodotObject.IsInstanceValid(_scroll) || delta <= 0)
            return;

        var view = _scroll.GetGlobalRect();
        if (view.Size.X < 8f || view.Size.Y < 8f)
            return;

        var grown = view.Grow(48f);
        if (!grown.HasPoint(globalMouse))
            return;

        float zone = 40f;
        float maxSpeed = 420f;
        float dx = 0f;
        float dy = 0f;
        float left = view.Position.X;
        float right = left + view.Size.X;
        float top = view.Position.Y;
        float bottom = top + view.Size.Y;

        if (globalMouse.X <= left + zone)
            dx = -maxSpeed * (1f - Mathf.Clamp((globalMouse.X - left) / zone, 0f, 1f));
        else if (globalMouse.X >= right - zone)
            dx = maxSpeed * (1f - Mathf.Clamp((right - globalMouse.X) / zone, 0f, 1f));

        if (globalMouse.Y <= top + zone)
            dy = -maxSpeed * (1f - Mathf.Clamp((globalMouse.Y - top) / zone, 0f, 1f));
        else if (globalMouse.Y >= bottom - zone)
            dy = maxSpeed * (1f - Mathf.Clamp((bottom - globalMouse.Y) / zone, 0f, 1f));

        if (!Mathf.IsZeroApprox(dx))
        {
            var bar = _scroll.GetHScrollBar();
            if (bar != null)
                bar.Value = Mathf.Clamp(bar.Value + dx * (float)delta, bar.MinValue, bar.MaxValue);
        }

        if (!Mathf.IsZeroApprox(dy))
        {
            var bar = _scroll.GetVScrollBar();
            if (bar != null)
                bar.Value = Mathf.Clamp(bar.Value + dy * (float)delta, bar.MinValue, bar.MaxValue);
        }
    }

    private void OnCopySelectedCues()
    {
        if (Live != this)
            return;
        var selected = GetSelectedCuesOnBoard();
        if (selected.Count == 0)
        {
            _globalSignals?.EmitSignal(nameof(GlobalSignals.Log), "No cues selected to copy.", (int)LogType.Info);
            return;
        }

        if (!CueList.WriteClipboardFromCues(selected))
        {
            _globalSignals?.EmitSignal(nameof(GlobalSignals.Log), "Copy produced no cue data.", (int)LogType.Warning);
            return;
        }

        _globalSignals?.EmitSignal(nameof(GlobalSignals.Log),
            selected.Count == 1
                ? $"Copied cue \"{selected[0].Name}\"."
                : $"Copied {selected.Count} cues.",
            (int)LogType.Info);
    }

    private void OnCutSelectedCues()
    {
        if (Live != this)
            return;
        if (IsShowMode())
        {
            LogShowModeBlock("cut cues");
            return;
        }

        var selected = GetSelectedCuesOnBoard();
        if (selected.Count == 0)
        {
            _globalSignals?.EmitSignal(nameof(GlobalSignals.Log), "No cues selected to cut.", (int)LogType.Info);
            return;
        }

        if (!CueList.WriteClipboardFromCues(selected))
        {
            _globalSignals?.EmitSignal(nameof(GlobalSignals.Log), "Cut produced no cue data.", (int)LogType.Warning);
            return;
        }

        RemoveCuesFromBoard(selected, "Cut cues");
        _globalSignals?.EmitSignal(nameof(GlobalSignals.Log),
            selected.Count == 1
                ? $"Cut cue \"{selected[0].Name}\"."
                : $"Cut {selected.Count} cues.",
            (int)LogType.Info);
    }

    private void OnPasteCues()
    {
        if (Live != this)
            return;
        if (IsShowMode())
        {
            LogShowModeBlock("paste cues");
            return;
        }

        if (!CueList.HasClipboard)
        {
            _globalSignals?.EmitSignal(nameof(GlobalSignals.Log), "Nothing to paste.", (int)LogType.Info);
            return;
        }

        TryGetCellAt(GetGlobalMousePosition(), out int row, out int col, out _);
        if (row < 0 || col < 0 || !IsValidCell(row, col))
        {
            row = 0;
            col = 0;
        }

        if (GetCellCueId(row, col) >= 0)
        {
            ShowPastePopup(row, col);
            return;
        }

        ApplyPaste(row, col, BoardPasteAction.NextAvailable, placeFirstInTarget: true);
    }

    private void ShowPastePopup(int row, int col)
    {
        ClosePastePopup();
        var occupying = FetchCellCue(row, col);
        string summary = occupying == null
            ? UiLocalizer.Tf("Drop Location: Row {0}, Column {1}", row + 1, col + 1)
            : UiLocalizer.Tf("Drop Location: Row {0}, Column {1} — Cue Number: {2} - Name: {3}",
                row + 1, col + 1,
                string.IsNullOrWhiteSpace(occupying.CueNum) ? occupying.Id.ToString() : occupying.CueNum,
                occupying.Name ?? string.Empty);

        _pendingPasteRow = row;
        _pendingPasteCol = col;
        _pastePopup = new BoardPastePopup();
        Node host = GetNodeOrNull("/root/Cue2Base") ?? GetTree()?.Root;
        host?.AddChild(_pastePopup);
        _pastePopup.Configure(Math.Max(1, CueList.ClipboardRootCount), summary);
        _pastePopup.Confirmed += OnPastePopupConfirmed;
        _pastePopup.Cancelled += OnPastePopupCancelled;
        _pastePopup.ShowConfigured();
    }

    private void OnPastePopupConfirmed(BoardPasteAction action)
    {
        int row = _pendingPasteRow;
        int col = _pendingPasteCol;
        ClosePastePopup();
        ApplyPaste(row, col, action, placeFirstInTarget: action != BoardPasteAction.NextAvailable);
    }

    private void OnPastePopupCancelled()
    {
        ClosePastePopup();
    }

    private void ClosePastePopup()
    {
        if (_pastePopup == null)
            return;
        _pastePopup.Confirmed -= OnPastePopupConfirmed;
        _pastePopup.Cancelled -= OnPastePopupCancelled;
        if (GodotObject.IsInstanceValid(_pastePopup))
        {
            _pastePopup.Hide();
            _pastePopup.QueueFree();
        }

        _pastePopup = null;
        _pendingPasteRow = -1;
        _pendingPasteCol = -1;
    }

    private void ApplyPaste(int row, int col, BoardPasteAction action, bool placeFirstInTarget)
    {
        var cues = CueList.CloneClipboardAsCues();
        if (cues == null || cues.Count == 0)
        {
            _globalSignals?.EmitSignal(nameof(GlobalSignals.Log), "Paste produced no cues.", (int)LogType.Warning);
            return;
        }

        foreach (var cue in cues)
            _globalData?.Cuelist?.RelinkCueComponents(cue);

        if (_historyManager?.IsRestoring != true)
            _historyManager?.RecordCuelistChange("Paste cues");

        EnsureModel();
        if (row < 0 || col < 0 || !IsValidCell(row, col))
        {
            row = 0;
            col = 0;
        }

        switch (action)
        {
            case BoardPasteAction.Replace:
                RemoveBoardCueAt(row, col);
                PlaceCuesFrom(row, col, cues, occupyTarget: true);
                break;
            case BoardPasteAction.ShuffleRow:
                ShuffleRowInsert(row, col, cues);
                break;
            case BoardPasteAction.ShuffleColumn:
                ShuffleColumnInsert(row, col, cues);
                break;
            default:
                PlaceCuesFrom(row, col, cues, occupyTarget: placeFirstInTarget && GetCellCueId(row, col) < 0);
                break;
        }

        ShrinkUnusedEdges();
        RebuildGrid();
        NotifyCueCount();
        _globalData?.ShellSelection?.SetSelection(cues, cues[^1], recordHistory: false, emitFocus: true);
        RefreshSelectionChrome();
        _globalSignals?.EmitSignal(nameof(GlobalSignals.Log),
            cues.Count == 1
                ? $"Pasted cue \"{cues[0].Name}\"."
                : $"Pasted {cues.Count} cues.",
            (int)LogType.Info);
    }

    private void PlaceCuesFrom(int row, int col, List<Cue> cues, bool occupyTarget)
    {
        int r = row;
        int c = col;
        bool first = true;
        foreach (var cue in cues)
        {
            if (first && occupyTarget && IsValidCell(r, c) && GetCellCueId(r, c) < 0)
            {
                PlaceCueInCell(r, c, cue);
                ExpandAround(r, c);
                AdvanceCell(ref r, ref c);
                first = false;
                continue;
            }

            first = false;
            EnsureEmptySlotFrom(ref r, ref c);
            PlaceCueInCell(r, c, cue);
            ExpandAround(r, c);
            AdvanceCell(ref r, ref c);
        }
    }

    private void ShuffleRowInsert(int row, int col, List<Cue> cues)
    {
        int n = cues.Count;
        if (n <= 0 || !IsValidCell(row, col))
            return;

        int oldCols = _cols;
        ResizeGrid(_rows, oldCols + n, keepCues: true);
        for (int c = oldCols - 1; c >= col; c--)
        {
            SetCellCueId(row, c + n, GetCellCueId(row, c));
            SetCellCueId(row, c, -1);
        }

        for (int i = 0; i < n; i++)
        {
            PlaceCueInCell(row, col + i, cues[i]);
            ExpandAround(row, col + i);
        }
    }

    private void ShuffleColumnInsert(int row, int col, List<Cue> cues)
    {
        int n = cues.Count;
        if (n <= 0 || !IsValidCell(row, col))
            return;

        int oldRows = _rows;
        ResizeGrid(oldRows + n, _cols, keepCues: true);
        for (int r = oldRows - 1; r >= row; r--)
        {
            SetCellCueId(r + n, col, GetCellCueId(r, col));
            SetCellCueId(r, col, -1);
        }

        for (int i = 0; i < n; i++)
        {
            PlaceCueInCell(row + i, col, cues[i]);
            ExpandAround(row + i, col);
        }
    }

    private void PlaceCueInCell(int row, int col, Cue cue)
    {
        if (cue == null || !IsValidCell(row, col))
            return;
        RegisterCueTree(cue);
        SetCellCueId(row, col, cue.Id);
    }

    private void RemoveBoardCueAt(int row, int col)
    {
        int id = GetCellCueId(row, col);
        if (id < 0)
            return;
        SetCellCueId(row, col, -1);
        UnregisterCueTree(id);
    }

    private static void UnregisterCueTree(int id)
    {
        var cue = CueList.FetchCueFromId(id);
        if (cue != null)
        {
            foreach (int childId in cue.ChildCues.ToList())
                UnregisterCueTree(childId);
        }

        CueList.CueIndex?.Remove(id);
    }

    private void RegisterCueTree(Cue cue)
    {
        if (cue == null)
            return;
        RegisterCue(cue);
        foreach (int childId in cue.ChildCues)
            RegisterCueTree(CueList.FetchCueFromId(childId));
    }

    private static List<Cue> CollectCueTree(int rootId, HashSet<int> seen)
    {
        var list = new List<Cue>();
        CollectCueTree(CueList.FetchCueFromId(rootId), seen, list, 0);
        return list;
    }

    private static void CollectCueTree(Cue cue, HashSet<int> seen, List<Cue> dest, int depth)
    {
        if (cue == null || seen == null || dest == null)
            return;
        if (depth > 32 || !seen.Add(cue.Id))
            return;
        dest.Add(cue);
        foreach (int childId in cue.ChildCues)
            CollectCueTree(CueList.FetchCueFromId(childId), seen, dest, depth + 1);
    }

    private static bool CueTreeContains(int rootId, int targetId, int depth)
    {
        if (rootId < 0 || depth > 32)
            return false;
        if (rootId == targetId)
            return true;
        var cue = CueList.FetchCueFromId(rootId);
        if (cue?.ChildCues == null)
            return false;
        foreach (int childId in cue.ChildCues)
        {
            if (CueTreeContains(childId, targetId, depth + 1))
                return true;
        }

        return false;
    }

    private List<Cue> GetSelectedCuesOnBoard()
    {
        var selected = Cue2.Domain.Cuelist.ShellSelection.SelectedCues;
        var result = new List<Cue>();
        if (selected == null)
            return result;
        foreach (var cue in selected)
        {
            if (cue != null && ContainsCue(cue.Id))
                result.Add(cue);
        }

        return result;
    }

    private void OnDeleteSelectedCues()
    {
        if (Live != this)
            return;
        if (IsShowMode())
        {
            LogShowModeBlock("delete cues");
            return;
        }

        var toRemove = GetSelectedCuesOnBoard();
        if (toRemove.Count == 0)
            return;

        RemoveCuesFromBoard(toRemove, "Delete cues");
        GD.Print($"ButtonBoard:OnDeleteSelectedCues - Removed {toRemove.Count} cue(s).");
    }

    private void RemoveCuesFromBoard(List<Cue> toRemove, string historyLabel)
    {
        if (toRemove == null || toRemove.Count == 0)
            return;

        if (_historyManager?.IsRestoring != true)
            _historyManager?.RecordCuelistChange(historyLabel);

        foreach (var cue in toRemove)
        {
            ClearCellForCue(cue.Id);
            UnregisterCueTree(cue.Id);
        }

        ShrinkUnusedEdges();
        RebuildGrid();
        Cue2.Domain.Cuelist.ShellSelection.SelectedCues.Clear();
        _globalSignals?.EmitSignal(nameof(GlobalSignals.ShellFocused), -1);
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

    private void RefreshGridAfterMutation(int prevRows, int prevCols, params int[] cellsToBind)
    {
        if (_rows != prevRows || _cols != prevCols)
        {
            RebuildGrid();
            return;
        }

        for (int i = 0; i + 1 < cellsToBind.Length; i += 2)
            BindCellAt(cellsToBind[i], cellsToBind[i + 1]);
        LayoutCells();
    }

    private void BindCellAt(int row, int col)
    {
        int i = row * _cols + col;
        if (i < 0 || i >= _cells.Count)
            return;
        var cell = _cells[i];
        if (cell == null || !GodotObject.IsInstanceValid(cell))
            return;
        cell.Bind(this, row, col, GetCellCueId(row, col), IsShowMode());
    }

    private void RebuildGrid()
    {
        if (_contentHost == null || !GodotObject.IsInstanceValid(_contentHost))
            return;

        EnsureModel();
        bool showMode = IsShowMode();

        var unused = new System.Collections.Generic.Dictionary<(int Row, int Col), ButtonBoardCell>();
        foreach (var existing in _cells)
        {
            if (existing == null || !GodotObject.IsInstanceValid(existing))
                continue;
            unused[(existing.Row, existing.Col)] = existing;
        }

        var next = new List<ButtonBoardCell>(_rows * _cols);
        for (int r = 0; r < _rows; r++)
        {
            for (int c = 0; c < _cols; c++)
            {
                int cueId = GetCellCueId(r, c);
                if (unused.TryGetValue((r, c), out var cell))
                {
                    unused.Remove((r, c));
                    cell.Bind(this, r, c, cueId, showMode);
                }
                else
                {
                    cell = new ButtonBoardCell();
                    _contentHost.AddChild(cell);
                    cell.Bind(this, r, c, cueId, showMode);
                }

                next.Add(cell);
            }
        }

        foreach (var leftover in unused.Values)
        {
            leftover.GetParent()?.RemoveChild(leftover);
            leftover.QueueFree();
        }

        _cells.Clear();
        _cells.AddRange(next);

        SyncAxisHandles(_rowHandles, _rows, vertical: false);
        SyncAxisHandles(_colHandles, _cols, vertical: true);
        RaiseHandles();
        ApplyHandleInteractable();
        LayoutCells();
    }

    private void SyncAxisHandles(List<ButtonBoardResizeHandle> handles, int count, bool vertical)
    {
        while (handles.Count < count)
        {
            var handle = new ButtonBoardResizeHandle();
            _contentHost.AddChild(handle);
            handles.Add(handle);
        }

        while (handles.Count > count)
        {
            int last = handles.Count - 1;
            var extra = handles[last];
            handles.RemoveAt(last);
            if (extra != null && GodotObject.IsInstanceValid(extra))
            {
                extra.GetParent()?.RemoveChild(extra);
                extra.QueueFree();
            }
        }

        for (int i = 0; i < count; i++)
        {
            var handle = handles[i];
            if (handle == null || !GodotObject.IsInstanceValid(handle))
                continue;
            handle.Bind(this, vertical, i);
        }
    }

    private void RaiseHandles()
    {
        if (_contentHost == null)
            return;
        int idx = 0;
        foreach (var cell in _cells)
        {
            if (cell != null && GodotObject.IsInstanceValid(cell) && cell.GetParent() == _contentHost)
                _contentHost.MoveChild(cell, idx++);
        }

        foreach (var handle in _rowHandles)
        {
            if (handle != null && GodotObject.IsInstanceValid(handle) && handle.GetParent() == _contentHost)
                _contentHost.MoveChild(handle, idx++);
        }

        foreach (var handle in _colHandles)
        {
            if (handle != null && GodotObject.IsInstanceValid(handle) && handle.GetParent() == _contentHost)
                _contentHost.MoveChild(handle, idx++);
        }
    }

    private void LayoutCells()
    {
        if (_contentHost == null || !GodotObject.IsInstanceValid(_contentHost))
            return;

        if (_colStops.Length != _cols + 1)
            _colStops = new float[_cols + 1];
        if (_rowStops.Length != _rows + 1)
            _rowStops = new float[_rows + 1];
        _colStops[0] = 0f;
        _rowStops[0] = 0f;
        for (int c = 0; c < _cols; c++)
            _colStops[c + 1] = _colStops[c] + ColWidth(c);
        for (int r = 0; r < _rows; r++)
            _rowStops[r + 1] = _rowStops[r] + RowHeight(r);

        float tableW = _colStops[_cols];
        float tableH = _rowStops[_rows];
        int expected = _rows * _cols;
        int count = Math.Min(_cells.Count, expected);
        for (int i = 0; i < count; i++)
        {
            var cell = _cells[i];
            if (cell == null || !GodotObject.IsInstanceValid(cell))
                continue;
            int r = i / _cols;
            int c = i % _cols;
            cell.Size = new Vector2(_colStops[c + 1] - _colStops[c], _rowStops[r + 1] - _rowStops[r]);
            cell.Position = new Vector2(_colStops[c], _rowStops[r]);
        }

        float hit = ButtonBoardResizeHandle.HitSize;
        float half = hit * 0.5f;
        int colHandleCount = Math.Min(_colHandles.Count, _cols);
        for (int c = 0; c < colHandleCount; c++)
        {
            var handle = _colHandles[c];
            if (handle == null || !GodotObject.IsInstanceValid(handle))
                continue;
            handle.Position = new Vector2(_colStops[c + 1] - half, 0f);
            handle.Size = new Vector2(hit, tableH);
        }

        int rowHandleCount = Math.Min(_rowHandles.Count, _rows);
        for (int r = 0; r < rowHandleCount; r++)
        {
            var handle = _rowHandles[r];
            if (handle == null || !GodotObject.IsInstanceValid(handle))
                continue;
            handle.Position = new Vector2(0f, _rowStops[r + 1] - half);
            handle.Size = new Vector2(tableW, hit);
        }

        _contentHost.CustomMinimumSize = new Vector2(
            Math.Max(MinColWidth, tableW + half),
            Math.Max(MinRowHeight, tableH + half));
    }

    /// <summary>Applies pointer motion to the active column/row resize.</summary>
    internal void UpdateAxisResizeFromHandle(Vector2 globalPos) => UpdateAxisResize(globalPos);

    private void UpdateAxisResize(Vector2 globalPos)
    {
        if (!_resizing)
            return;

        Vector2 local = ToContentLocal(globalPos);
        bool matchAll = Input.IsKeyPressed(Key.Ctrl) || Input.IsKeyPressed(Key.Meta);
        if (_resizeVertical)
        {
            if (_resizeIndex < 0 || _resizeIndex >= _colWidths.Count)
                return;
            float next = Math.Max(MinColWidth, _resizeStartSize + (local.X - _resizeStartLocal.X));
            bool axisChanged = !Mathf.IsEqualApprox(_colWidths[_resizeIndex], next);
            bool matchNeeded = matchAll && !AxisAllMatch(_colWidths, next);
            if (!axisChanged && !matchNeeded)
                return;
            if (matchAll)
                ApplyAxisSize(_colWidths, next);
            else
                _colWidths[_resizeIndex] = next;
        }
        else
        {
            if (_resizeIndex < 0 || _resizeIndex >= _rowHeights.Count)
                return;
            float next = Math.Max(MinRowHeight, _resizeStartSize + (local.Y - _resizeStartLocal.Y));
            bool axisChanged = !Mathf.IsEqualApprox(_rowHeights[_resizeIndex], next);
            bool matchNeeded = matchAll && !AxisAllMatch(_rowHeights, next);
            if (!axisChanged && !matchNeeded)
                return;
            if (matchAll)
                ApplyAxisSize(_rowHeights, next);
            else
                _rowHeights[_resizeIndex] = next;
        }

        LayoutCells();
    }

    private void EndAxisResize()
    {
        if (!_resizing)
            return;

        var finalCols = new List<float>(_colWidths);
        var finalRows = new List<float>(_rowHeights);
        bool changed = !AxisListsEqual(_colWidths, _resizeStartColWidths)
                       || !AxisListsEqual(_rowHeights, _resizeStartRowHeights);

        _resizing = false;
        _resizeHandle?.SetDragging(false);
        _resizeHandle = null;
        MouseDefaultCursorShape = CursorShape.Arrow;
        SyncPointerProcessing();

        if (!changed || _historyManager?.IsRestoring == true)
        {
            _resizeStartColWidths = null;
            _resizeStartRowHeights = null;
            return;
        }

        CopyAxisList(_colWidths, _resizeStartColWidths);
        CopyAxisList(_rowHeights, _resizeStartRowHeights);
        _historyManager?.RecordCuelistChange("Resize button board");
        CopyAxisList(_colWidths, finalCols);
        CopyAxisList(_rowHeights, finalRows);
        _resizeStartColWidths = null;
        _resizeStartRowHeights = null;
        LayoutCells();
    }

    private static bool AxisAllMatch(List<float> sizes, float value)
    {
        if (sizes == null || sizes.Count == 0)
            return true;
        for (int i = 0; i < sizes.Count; i++)
        {
            if (!Mathf.IsEqualApprox(sizes[i], value))
                return false;
        }

        return true;
    }

    private static void ApplyAxisSize(List<float> sizes, float value)
    {
        if (sizes == null)
            return;
        for (int i = 0; i < sizes.Count; i++)
            sizes[i] = value;
    }

    private static bool AxisListsEqual(List<float> a, List<float> b)
    {
        if (a == null || b == null || a.Count != b.Count)
            return false;
        for (int i = 0; i < a.Count; i++)
        {
            if (!Mathf.IsEqualApprox(a[i], b[i]))
                return false;
        }

        return true;
    }

    private static void CopyAxisList(List<float> dest, List<float> source)
    {
        if (dest == null || source == null)
            return;
        int n = Math.Min(dest.Count, source.Count);
        for (int i = 0; i < n; i++)
            dest[i] = source[i];
    }

    private void ApplyHandleInteractable()
    {
        bool edit = !IsShowMode();
        foreach (var handle in _colHandles)
        {
            if (handle != null && GodotObject.IsInstanceValid(handle))
                handle.SetInteractable(edit);
        }

        foreach (var handle in _rowHandles)
        {
            if (handle != null && GodotObject.IsInstanceValid(handle))
                handle.SetInteractable(edit);
        }
    }

    private Vector2 ToContentLocal(Vector2 globalPos)
    {
        if (_contentHost == null || !GodotObject.IsInstanceValid(_contentHost))
            return globalPos;
        return _contentHost.GetGlobalTransformWithCanvas().AffineInverse() * globalPos;
    }

    private float ColWidth(int col)
    {
        if (col < 0 || col >= _colWidths.Count)
            return DefaultColWidth;
        return Math.Max(MinColWidth, _colWidths[col]);
    }

    private float GetVisibleBoardWidth()
    {
        float w = 0f;
        if (_scroll != null && GodotObject.IsInstanceValid(_scroll))
            w = _scroll.Size.X;
        if (w < 32f)
            w = Size.X;
        if (w < 32f)
            w = 640f;
        return Math.Max(MinColWidth, w - 20f);
    }

    private int MaxColumnsForViewport()
    {
        float visible = GetVisibleBoardWidth();
        float used = 0f;
        int count = 0;
        const int hardMax = 64;
        while (count < hardMax)
        {
            float next = count < _colWidths.Count ? ColWidth(count) : DefaultColWidth;
            if (count > 0 && used + next > visible)
                break;
            used += next;
            count++;
        }

        return Math.Max(1, count);
    }

    private float RowHeight(int row)
    {
        if (row < 0 || row >= _rowHeights.Count)
            return DefaultRowHeight;
        return Math.Max(MinRowHeight, _rowHeights[row]);
    }

    /// <summary>Cell under <paramref name="globalPos"/>, or null.</summary>
    internal ButtonBoardCell FindCellAtGlobal(Vector2 globalPos)
    {
        foreach (var cell in _cells)
        {
            if (cell != null && GodotObject.IsInstanceValid(cell) && cell.GetGlobalRect().HasPoint(globalPos))
                return cell;
        }

        return null;
    }

    private void EnsureEmptySlotFrom(ref int row, ref int col)
    {
        if (TryFindNextEmpty(row, col, out int foundRow, out int foundCol))
        {
            row = foundRow;
            col = foundCol;
            return;
        }

        GrowForNextEmpty();
        if (TryFindNextEmpty(row, col, out foundRow, out foundCol))
        {
            row = foundRow;
            col = foundCol;
            return;
        }

        GrowForNextEmpty();
        if (TryFindNextEmpty(row, col, out foundRow, out foundCol))
        {
            row = foundRow;
            col = foundCol;
            return;
        }

        row = Math.Max(0, _rows - 1);
        col = Math.Max(0, _cols - 1);
    }

    private void GrowForNextEmpty()
    {
        if (_cols < MaxColumnsForViewport())
            AppendColumn();
        else
            AppendRow();
    }

    private bool TryFindNextEmpty(int startRow, int startCol, out int row, out int col)
    {
        row = -1;
        col = -1;
        if (startRow < 0)
            startRow = 0;
        if (startCol < 0)
            startCol = 0;
        for (int r = startRow; r < _rows; r++)
        {
            int c0 = r == startRow ? startCol : 0;
            for (int c = c0; c < _cols; c++)
            {
                if (GetCellCueId(r, c) < 0)
                {
                    row = r;
                    col = c;
                    return true;
                }
            }
        }

        return false;
    }

    private void AdvanceCell(ref int row, ref int col)
    {
        col++;
        if (col >= _cols)
        {
            col = 0;
            row++;
        }
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
                var meta = await mediaEngine.GetAudioFileMetadataAsync(filePath);
                audioComp.Metadata = meta;
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
