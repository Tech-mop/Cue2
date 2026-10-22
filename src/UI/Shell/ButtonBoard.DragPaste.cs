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
/// Partial: cell drag and drop, copy/cut/paste and delete.
/// </summary>
public partial class ButtonBoard
{
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
}
