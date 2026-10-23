// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using Cue2.Domain.Cues;
using Cue2.Services;
using Cue2.UI.Shell;
using Cue2.UI.Utilities;

namespace Cue2.Domain.Cuelist;

/// <summary>
/// Encapsulates the state machine, mouse tracking, drop target calculation,
/// and commit/cancel logic for reordering cues (including nesting support).
/// Edge auto-scroll and wheel-forward reuse <see cref="CueList.AutoScrollCueListFromPointer"/>
/// / <see cref="CueList.ScrollCueListByWheel"/> (same helpers as control pick-target).
/// </summary>
internal sealed class CueReorder(
    CueList owner,
    Control reorderCueControl,
    Label reorderLocationLabel,
    VBoxContainer reorderListContainer,
    Panel reorderIndicatorPanel,
    VBoxContainer cueContainer)
{
    public bool IsActive { get; private set; }
    public ShellBar MouseOverShellBar { get; private set; }
    public bool InsertAbove { get; private set; }
    public bool InsertBelow { get; private set; }
    public bool InsertMakeChild { get; private set; }
    public bool DropAtEndAsTopLevel { get; private set; }
    public int DraggedCueId { get; private set; } = -1;

    private const double TabHoverSwitchSeconds = 0.35;

    private Control _hoverTabTarget;
    private double _hoverTabElapsed;
    private Node _overlayHome;
    private ButtonBoard _boardDropTarget;

    private CueList DropList
    {
        get
        {
            var live = CueList.Live;
            if (live != null && GodotObject.IsInstanceValid(live))
                return live;
            return owner;
        }
    }

    private CuelistWorkspace Workspace => owner.GetParent() as CuelistWorkspace;

    private CueMovePreview Preview => Workspace?.MovePreview;

    private void SetPreview(
        string location,
        bool lineVisible = false,
        Vector2 linePos = default,
        Vector2 lineSize = default,
        bool outlineVisible = false,
        Vector2 outlinePos = default,
        Vector2 outlineSize = default)
    {
        var preview = Preview;
        if (preview != null)
        {
            preview.SetLocation(location);
            if (outlineVisible)
                preview.SetDropOutline(true, outlinePos, outlineSize);
            else
                preview.SetDropLine(lineVisible, linePos, lineSize);
            return;
        }

        if (reorderLocationLabel != null)
            reorderLocationLabel.Text = location ?? string.Empty;
        if (reorderIndicatorPanel == null)
            return;
        bool showIndicator = outlineVisible || lineVisible;
        reorderIndicatorPanel.Visible = showIndicator;
        if (!showIndicator)
            return;
        if (outlineVisible)
        {
            reorderIndicatorPanel.GlobalPosition = outlinePos;
            reorderIndicatorPanel.Size = new Vector2(Mathf.Max(2f, outlineSize.X), Mathf.Max(2f, outlineSize.Y));
            return;
        }

        reorderIndicatorPanel.GlobalPosition = linePos;
        reorderIndicatorPanel.Size = new Vector2(Mathf.Max(2f, lineSize.X), Mathf.Max(1f, lineSize.Y));
    }

    public void Start(ShellBar shellbar)
    {
        if (IsActive) return;
        if (shellbar == null || !GodotObject.IsInstanceValid(shellbar)) return;

        if (!shellbar.Selected)
        {
            owner.SelectIndividualForReorder(shellbar.CueId);
        }

        if (reorderListContainer.GetChildCount() > 0)
        {
            foreach (var child in reorderListContainer.GetChildren())
            {
                child.QueueFree();
            }
        }

        reorderCueControl.Visible = false;
        Workspace?.MovePreview?.Begin(ShellSelection.SelectedCues);
        IsActive = true;
        DraggedCueId = shellbar.CueId;
        ResetDropFlags();
        MouseOverShellBar = shellbar;
        _hoverTabTarget = null;
        _hoverTabElapsed = 0;

        // Initial hover is the dragged item itself
    }

    public void ProcessInput(InputEvent @event)
    {
        if (!IsActive) return;

        if (@event is InputEventMouseMotion eventMouseMotion)
        {
            Workspace?.MovePreview?.Follow(eventMouseMotion.GlobalPosition);
            RefreshHoverAndDrop(eventMouseMotion.GlobalPosition);
            return;
        }

        // Wheel / trackpad: the drag started on the grabber Button, which captures the
        // pointer so the ScrollContainer often never sees the wheel. Forward it here.
        if (TryHandleScrollInput(@event))
            return;

        // Left release = commit.
        if (@event is InputEventMouseButton mb && mb.ButtonIndex == MouseButton.Left && !mb.Pressed)
        {
            Commit();
            return;
        }

        // Cancel support (ESC or right-click)
        if (@event is InputEventKey keyEvent && keyEvent.Pressed && keyEvent.Keycode == Key.Escape)
        {
            Cancel();
            return;
        }
        if (@event is InputEventMouseButton rmb && rmb.ButtonIndex == MouseButton.Right && rmb.Pressed)
        {
            Cancel();
        }
    }

    /// <summary>
    /// Per-frame while dragging: edge auto-scroll (same helper as pick-target) and keep the
    /// drop indicator aligned as the list slides under a still pointer.
    /// </summary>
    /// <param name="delta">Frame delta in seconds.</param>
    public void Tick(double delta)
    {
        if (!IsActive)
            return;

        var mouse = owner.GetGlobalMousePosition();
        TryHoverSwitchPageTab(delta, mouse);
        var workspace = Workspace;
        if (workspace == null || !workspace.IsGlobalPointOnTabBar(mouse))
        {
            if (ButtonBoard.Live != null)
                ButtonBoard.Live.AutoScrollBoardFromPointer(mouse, delta);
            else
                DropList.AutoScrollCueListFromPointer(mouse, delta);
        }
        Workspace?.MovePreview?.Follow(mouse);
        RefreshHoverAndDrop(mouse);
    }

    /// <summary>
    /// Applies wheel / trackpad pan to the cuelist while a reorder drag is held.
    /// </summary>
    /// <param name="event">Viewport input event.</param>
    /// <returns>True when the event was consumed as a cuelist scroll.</returns>
    private bool TryHandleScrollInput(InputEvent @event)
    {
        if (@event is InputEventMouseButton wheel &&
            wheel.Pressed &&
            (wheel.ButtonIndex == MouseButton.WheelUp || wheel.ButtonIndex == MouseButton.WheelDown))
        {
            float steps = wheel.Factor > 0.01f ? wheel.Factor : 1f;
            float dir = wheel.ButtonIndex == MouseButton.WheelUp ? -1f : 1f;
            if (ButtonBoard.Live == null)
                DropList.ScrollCueListByWheel(dir * steps);
            owner.GetViewport()?.SetInputAsHandled();
            RefreshHoverAndDrop(owner.GetGlobalMousePosition());
            return true;
        }

        if (@event is InputEventPanGesture pan && !Mathf.IsZeroApprox(pan.Delta.Y))
        {
            if (ButtonBoard.Live == null)
                DropList.ScrollCueListByPixels(pan.Delta.Y);
            owner.GetViewport()?.SetInputAsHandled();
            RefreshHoverAndDrop(owner.GetGlobalMousePosition());
            return true;
        }

        return false;
    }

    /// <summary>
    /// Re-resolves the shell under the pointer (needed after auto-scroll / wheel with no motion)
    /// and updates the drop indicator.
    /// </summary>
    private void RefreshHoverAndDrop(Vector2 globalMouse)
    {
        var workspace = Workspace;
        if (workspace != null)
        {
            var tabPage = workspace.GetPageTabAtGlobalPoint(globalMouse);
            if (tabPage is CueList tabList)
            {
                ClearBoardDropHighlight();
                SetPreview(UiLocalizer.Tf("Move to: {0}", tabList.ListName ?? "Cue List"));
                MouseOverShellBar = null;
                ResetDropFlags();
                DropAtEndAsTopLevel = true;
                return;
            }

            if (tabPage is ButtonBoard tabBoard)
            {
                ClearBoardDropHighlight();
                SetPreview(UiLocalizer.Tf("Move to: {0}", tabBoard.ListName ?? "Button Board"));
                MouseOverShellBar = null;
                ResetDropFlags();
                DropAtEndAsTopLevel = true;
                return;
            }

            if (workspace.IsGlobalPointOnTabBar(globalMouse))
            {
                ClearBoardDropHighlight();
                SetPreview(UiLocalizer.T("Cannot reorder here"));
                MouseOverShellBar = null;
                ResetDropFlags();
                return;
            }
        }

        if (ButtonBoard.Live != null && GodotObject.IsInstanceValid(ButtonBoard.Live))
        {
            RefreshBoardDrop(ButtonBoard.Live, globalMouse);
            return;
        }

        ClearBoardDropHighlight();
        var dropList = DropList;
        MouseOverShellBar = dropList.FindVisibleShellBarAtGlobalY(globalMouse.Y);
        UpdateDropTarget(dropList, globalMouse);
    }

    private void TryHoverSwitchPageTab(double delta, Vector2 mouse)
    {
        var workspace = Workspace;
        if (workspace == null)
            return;

        var page = workspace.GetPageTabAtGlobalPoint(mouse);
        bool alreadyVisible = page != null
            && ((page is CueList list && CueList.Live == list)
                || (page is ButtonBoard board && ButtonBoard.Live == board));
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
        MouseOverShellBar = null;
        ResetDropFlags();
        ClearBoardDropHighlight();
    }

    private void RefreshBoardDrop(ButtonBoard board, Vector2 globalMouse)
    {
        MouseOverShellBar = null;
        ResetDropFlags();
        var cell = board.FindCellAtGlobal(globalMouse);
        board.SetExternalDropTarget(cell);
        _boardDropTarget = board;
        if (cell == null)
            SetPreview(UiLocalizer.Tf("Move to: {0}", board.ListName ?? "Button Board"));
        else if (cell.IsOccupied)
        {
            var cue = CueList.FetchCueFromId(cell.CueId);
            string name = string.IsNullOrWhiteSpace(cue?.Name) ? (cue?.CueNum ?? "?") : cue.Name;
            SetPreview(UiLocalizer.Tf("Swap with: {0}", name));
        }
        else
            SetPreview(UiLocalizer.T("Move to empty cell"));
    }

    private void ClearBoardDropHighlight()
    {
        _boardDropTarget?.ClearExternalDropTarget();
        _boardDropTarget = null;
    }

    private void UpdateDropTarget(CueList dropList, Vector2 globalMouse)
    {
        float mouseY = globalMouse.Y;
        bool check = IsValidDropTarget();
        bool handledAsEnd = false;

        // Detect blank space below the *entire* list (after the visually last cue).
        // This should always result in a top-level append, even if the last cue is nested.
        var lastShell = dropList.GetLastVisibleShellBar();
        var dropContainer = dropList._cueContainer ?? cueContainer;
        var listView = dropList.GetCueListScrollGlobalRect();
        bool overList = listView.Size.Y > 1f && listView.Grow(8f).HasPoint(globalMouse);

        if (lastShell == null && overList)
        {
            InsertAbove = false;
            InsertBelow = true;
            InsertMakeChild = false;
            DropAtEndAsTopLevel = true;
            SetPreview(
                UiLocalizer.T("Reorder at end (top level)"),
                true,
                dropContainer.GetGlobalPosition(),
                new Vector2(Mathf.Max(2f, dropContainer.Size.X), 2));
            handledAsEnd = true;
            check = true;
        }
        else if (lastShell != null)
        {
            float lastBottom = lastShell.GetGlobalPosition().Y + lastShell.Size.Y;
            if (mouseY > lastBottom + 8)  // tolerance for "blank area below everything"
            {
                InsertAbove = false;
                InsertBelow = true;
                InsertMakeChild = false;
                DropAtEndAsTopLevel = true;

                SetPreview(
                    UiLocalizer.T("Reorder at end (top level)"),
                    true,
                    new Vector2(dropContainer.GetGlobalPosition().X, lastBottom),
                    new Vector2(Mathf.Max(2f, dropContainer.Size.X), 2));

                handledAsEnd = true;
                check = true;
            }
        }

        if (!handledAsEnd)
        {
            DropAtEndAsTopLevel = false;

            if (check && MouseOverShellBar != null)
            {
                var targetCueId = MouseOverShellBar.CueId;
                var shellPosY = MouseOverShellBar.GetGlobalPosition().Y;
                var shellSizeY = 26; // ShellHeight
                var margin = shellSizeY / 4;

                InsertAbove = mouseY < shellPosY + margin;
                InsertBelow = mouseY > shellPosY + margin * 3;
                var targetCue = CueList.FetchCueFromId(targetCueId);
                InsertMakeChild = targetCue != null && targetCue.ParentId != -1;

                string targetName = targetCue?.Name ?? "?";
                string parentName = "";
                if (InsertMakeChild && targetCue != null)
                {
                    var p = CueList.FetchCueFromId(targetCue.ParentId);
                    parentName = p?.Name ?? "?";
                }

                if (InsertBelow)
                {
                    string text = InsertMakeChild
                        ? $"Reorder below: {targetName} and child of: {parentName}"
                        : $"Reorder below: {targetName}";
                    SetPreview(
                        text,
                        true,
                        new Vector2(MouseOverShellBar.GetGlobalPosition().X, MouseOverShellBar.GetGlobalPosition().Y + MouseOverShellBar.Size.Y),
                        new Vector2(MouseOverShellBar.Size.X, 2));
                }
                else if (InsertAbove)
                {
                    string text = InsertMakeChild
                        ? $"Reorder above: {targetName} and child of: {parentName}"
                        : $"Reorder above: {targetName}";
                    SetPreview(text, true, MouseOverShellBar.GetGlobalPosition(), new Vector2(MouseOverShellBar.Size.X, 2));
                }
                else
                {
                    InsertMakeChild = true;
                    var shellPos = MouseOverShellBar.GetGlobalPosition();
                    SetPreview(
                        UiLocalizer.Tf("Make child of: {0}", targetName),
                        outlineVisible: true,
                        outlinePos: shellPos,
                        outlineSize: MouseOverShellBar.Size);
                }
            }
            else
            {
                SetPreview(UiLocalizer.T("Cannot reorder here"));
            }
        }
    }

    private bool IsValidDropTarget()
    {
        if (MouseOverShellBar == null) return false;
        var targetCue = CueList.FetchCueFromId(MouseOverShellBar.CueId);
        if (targetCue == null) return false;
        return !ShellSelection.SelectedCues.Contains(targetCue);
    }

    public void Commit()
    {
        if (!IsActive) return;

        var mouse = owner.GetGlobalMousePosition();
        var workspace = Workspace;
        var tabPage = workspace?.GetPageTabAtGlobalPoint(mouse);
        if (tabPage is ButtonBoard tabBoard)
        {
            CommitToBoard(tabBoard, mouse, tabDrop: true);
            return;
        }

        if (ButtonBoard.Live != null && GodotObject.IsInstanceValid(ButtonBoard.Live)
            && tabPage is not CueList)
        {
            CommitToBoard(ButtonBoard.Live, mouse, tabDrop: false);
            return;
        }

        var tabList = tabPage as CueList;
        var dest = tabList ?? DropList;
        bool tabDrop = tabList != null;
        bool isEndDrop = DropAtEndAsTopLevel || tabDrop;

        if (!isEndDrop && !IsValidDropTarget())
        {
            Cancel();
            return;
        }

        var targetCue = isEndDrop ? null : CueList.FetchCueFromId(MouseOverShellBar?.CueId ?? -1);
        if (!isEndDrop && (targetCue == null || !dest.ContainsCue(targetCue.Id)))
        {
            Cancel();
            return;
        }

        // Check for cycles for any of the moved items (skip for end-of-list top level drop)
        if (!isEndDrop)
        {
            foreach (var sc in ShellSelection.SelectedCues)
            {
                int prospective = (!InsertAbove && !InsertBelow) ? targetCue.Id : targetCue.ParentId;
                if (WouldCreateCycle(sc, prospective))
                {
                    owner.EmitLog($"CueList:EndReorder - Cycle would be created; aborting reorder for {sc.Name}", (int)LogType.Warning);
                    Cancel();
                    return;
                }
            }
        }

        bool crossList = dest != owner;
        owner.RecordHistory(crossList ? "Move cues" : "Reorder cues");

        var selectedIds = new HashSet<int>(ShellSelection.SelectedCues.Select(c => c.Id));
        var visual = owner.GetModelVisualOrder(includeCollapsed: true);
        var toMove = new List<Cue>();
        foreach (int id in visual)
        {
            if (!selectedIds.Contains(id))
                continue;
            var cue = CueList.FetchCueFromId(id);
            if (cue == null)
                continue;
            if (cue.ParentId != -1 && selectedIds.Contains(cue.ParentId))
                continue;
            toMove.Add(cue);
        }

        if (toMove.Count == 0)
        {
            Cancel();
            return;
        }

        int newParentId;
        int insertIndex;
        if (isEndDrop || targetCue == null)
        {
            newParentId = -1;
            insertIndex = dest.RootOrder.Count;
        }
        else if (!InsertAbove && !InsertBelow)
        {
            newParentId = targetCue.Id;
            insertIndex = targetCue.ChildCues.Count;
            targetCue.Expanded = true;
        }
        else
        {
            newParentId = targetCue.ParentId;
            var siblings = dest.GetSiblingIdList(newParentId) ?? dest.RootOrder;
            insertIndex = siblings.IndexOf(targetCue.Id);
            if (insertIndex < 0)
                insertIndex = siblings.Count;
            if (InsertBelow)
                insertIndex++;
        }

        owner.BeginVirtualRefreshSuppress();
        if (crossList)
            dest.BeginVirtualRefreshSuppress();
        try
        {
            foreach (var cue in toMove)
            {
                if (!crossList)
                {
                    var list = owner.GetSiblingIdList(cue.ParentId) ?? owner.RootOrder;
                    int oldIdx = list.IndexOf(cue.Id);
                    if (cue.ParentId == newParentId && oldIdx >= 0 && oldIdx < insertIndex)
                        insertIndex--;
                    owner.InsertCueInModel(cue, newParentId, insertIndex);
                }
                else
                {
                    owner.DetachCueFromModel(cue);
                    dest.InsertCueInModel(cue, newParentId, insertIndex);
                }

                insertIndex++;
            }
        }
        finally
        {
            owner.EndVirtualRefreshSuppress();
            if (crossList)
                dest.EndVirtualRefreshSuppress();
        }

        Cleanup(keepChanges: true);
    }

    private void CommitToBoard(ButtonBoard dest, Vector2 mouse, bool tabDrop)
    {
        if (dest == null || !GodotObject.IsInstanceValid(dest))
        {
            Cancel();
            return;
        }

        var toMove = CollectMovedRoots();
        if (toMove.Count == 0)
        {
            Cancel();
            return;
        }

        int row = -1;
        int col = -1;
        if (!tabDrop)
            dest.TryGetCellAt(mouse, out row, out col, out _);

        owner.RecordHistory("Move cues");
        foreach (var cue in toMove)
            owner.DetachCueFromModel(cue);

        dest.AcceptMovedCues(toMove, row, col, owner);
        Cleanup(keepChanges: true);
    }

    private List<Cue> CollectMovedRoots()
    {
        var selectedIds = new HashSet<int>(ShellSelection.SelectedCues.Select(c => c.Id));
        var visual = owner.GetModelVisualOrder(includeCollapsed: true);
        var toMove = new List<Cue>();
        foreach (int id in visual)
        {
            if (!selectedIds.Contains(id))
                continue;
            var cue = CueList.FetchCueFromId(id);
            if (cue == null)
                continue;
            if (cue.ParentId != -1 && selectedIds.Contains(cue.ParentId))
                continue;
            toMove.Add(cue);
        }

        return toMove;
    }

    public void Cancel()
    {
        Cleanup(keepChanges: false);
    }

    private void Cleanup(bool keepChanges)
    {
        foreach (var child in reorderListContainer.GetChildren())
        {
            child.QueueFree();
        }

        int draggedId = DraggedCueId;

        IsActive = false;
        RestoreFloatingOverlay();
        Preview?.End();
        reorderCueControl.Visible = false;
        MouseOverShellBar = null;
        ResetDropFlags();
        DraggedCueId = -1;
        _hoverTabTarget = null;
        _hoverTabElapsed = 0;
        owner.SyncPointerInputProcessing();

        if (!keepChanges)
        {
            var workspace = Workspace;
            if (workspace != null && CueList.Live != owner && GodotObject.IsInstanceValid(owner))
                workspace.HoverActivatePageDuringReorder(owner);
        }

        ClearBoardDropHighlight();

        // Drop any hover wash that thrash-accumulated during the drag.
        owner.ClearAllShellHoverChrome();

        // Unstick grabber(s): mouse-up landed on the floating reorder UI, not DragBar
        // (keep_pressed_outside leaves BaseButton pressed until a real ButtonUp).
        ReleaseStuckDragGrabbers(draggedId);
        // One more frame after Godot finishes this input path.
        owner.CallDeferred(nameof(CueList.DeferredReleaseReorderGrabbers), draggedId);

        if (!keepChanges)
        {
            // Shells were already detached in Commit if we got that far; 
            // for pure cancel, they should still be in original positions.
        }
    }

    /// <summary>
    /// Forces reorder grabber buttons out of the pressed state after a drag session.
    /// </summary>
    /// <param name="draggedCueId">Primary dragged cue id, or -1.</param>
    private void ReleaseStuckDragGrabbers(int draggedCueId)
    {
        if (draggedCueId >= 0)
        {
            var dragged = CueList.FetchCueFromId(draggedCueId);
            dragged?.ShellBar?.ReleaseDragGrabber();
        }

        if (ShellSelection.SelectedCues == null)
            return;

        foreach (var cue in ShellSelection.SelectedCues)
            cue?.ShellBar?.ReleaseDragGrabber();
    }

    public void ResetDropFlags()
    {
        InsertAbove = false;
        InsertBelow = false;
        InsertMakeChild = false;
        DropAtEndAsTopLevel = false;
    }

    public void SetMouseOver(ShellBar shellbar)
    {
        MouseOverShellBar = shellbar;
    }

    private void PrepareReorderPreviewLabels()
    {
        foreach (var selectedCue in ShellSelection.SelectedCues)
        {
            var label = new Label();
            label.Text = selectedCue.Name;
            label.AddThemeFontSizeOverride("font_size", 9);
            label.AddThemeColorOverride("font_color", new Color(0.7f, 0.7f, 0.7f, 0.5f));
            reorderListContainer.AddChild(label);
        }
    }

    private (VBoxContainer targetContainer, int insertIndex, int newParentId, bool isMakeChild) DetermineReorderTarget()
    {
        if (DropAtEndAsTopLevel)
        {
            return (cueContainer, cueContainer.GetChildCount(), -1, false);
        }

        var targetShell = MouseOverShellBar;
        if (targetShell == null)
            return (cueContainer, 0, -1, false);

        VBoxContainer container = cueContainer;
        int newPid = -1;
        bool makeChild = false;

        if (!InsertAbove && !InsertBelow)
        {
            container = targetShell.ShellChildContainer ?? cueContainer;
            newPid = targetShell.CueId;
            makeChild = true;
        }
        else if (CueList.FetchCueFromId(targetShell.CueId)?.ParentId != -1)
        {
            var targetParent = CueList.FetchCueFromId(CueList.FetchCueFromId(targetShell.CueId).ParentId);
            container = targetParent?.ShellBar?.ShellChildContainer ?? cueContainer;
            newPid = targetParent?.Id ?? -1;
        }

        // Index is finalized in ResolveInsertIndexAfterDetach (above vs below).
        int idx = targetShell.GetIndex();
        if (InsertBelow)
            idx += 1;
        return (container, idx, newPid, makeChild);
    }

    /// <summary>
    /// Computes the child index for the drop after moved shells have been removed from the tree.
    /// Must run post-detach so removing an earlier sibling does not leave "above" off-by-one.
    /// </summary>
    private int ResolveInsertIndexAfterDetach(VBoxContainer targetContainer, ShellBar targetShell, bool isMakeChild)
    {
        if (targetContainer == null)
            return 0;

        if (DropAtEndAsTopLevel || isMakeChild)
            return targetContainer.GetChildCount();

        // Target still lives in the destination container for above/below drops.
        if (targetShell != null
            && GodotObject.IsInstanceValid(targetShell)
            && targetShell.GetParent() == targetContainer)
        {
            int targetIdx = targetShell.GetIndex();
            int idx = InsertBelow ? targetIdx + 1 : targetIdx;
            return Math.Clamp(idx, 0, targetContainer.GetChildCount());
        }

        // Fallback if the hover target was also moved (should not happen for valid drops).
        int fallback = targetContainer.GetChildCount();
        return Math.Clamp(fallback, 0, targetContainer.GetChildCount());
    }

    private bool WouldCreateCycle(Cue movingCue, int prospectiveParentId)
    {
        if (movingCue == null || prospectiveParentId == -1) return false;
        if (movingCue.Id == prospectiveParentId) return true;

        var current = CueList.FetchCueFromId(prospectiveParentId);
        while (current != null)
        {
            if (current.Id == movingCue.Id) return true;
            if (current.ParentId == -1) break;
            current = CueList.FetchCueFromId(current.ParentId);
        }
        return false;
    }

    /// <summary>
    /// Reparents the floating preview off the source CueList so it stays visible when
    /// TabContainer hides that tab during a hover-switch.
    /// </summary>
    private void AttachFloatingOverlay()
    {
        if (!GodotObject.IsInstanceValid(reorderCueControl))
            return;
        _overlayHome = reorderCueControl.GetParent();
        // Cue2Base is a Control (not a split/container). Do not reparent onto the
        // TabContainer or its HSplit — extra children become tabs / split panes.
        Node host = owner.GetNodeOrNull("/root/Cue2Base") ?? owner.GetTree()?.Root;
        if (host == null || host == _overlayHome)
            return;
        reorderCueControl.Reparent(host, keepGlobalTransform: true);
    }

    private void RestoreFloatingOverlay()
    {
        if (!GodotObject.IsInstanceValid(reorderCueControl))
            return;
        if (_overlayHome != null
            && GodotObject.IsInstanceValid(_overlayHome)
            && reorderCueControl.GetParent() != _overlayHome)
        {
            reorderCueControl.Reparent(_overlayHome, keepGlobalTransform: true);
        }

        _overlayHome = null;
    }

    private void SyncChildListsFromContainers()
    {
        foreach (var cueEntry in CueList.CueIndex)
        {
            var cue = cueEntry.Value;
            if (cue.ChildCues.Count == 0 && cue.ShellBar?.ShellChildContainer?.GetChildCount() == 0)
                continue;

            var container = cue.ShellBar?.ShellChildContainer;
            if (container == null) continue;

            var ordered = new List<int>();
            foreach (var child in container.GetChildren())
            {
                if (child is ShellBar sb)
                {
                    int id = sb.CueId;
                    if (id >= 0) ordered.Add(id);
                }
            }
            cue.ChildCues = ordered;
        }
    }
}
