// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using Cue2.Domain.Cues;
using Cue2.Services;
using Cue2.UI.Utilities;

namespace Cue2.Domain.Cuelist;

/// <summary>
/// Encapsulates the state machine, mouse tracking, drop target calculation,
/// and commit/cancel logic for reordering cues (including nesting support).
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

        PrepareReorderPreviewLabels();

        reorderCueControl.Visible = true;
        // Floating preview must not steal mouse from list/grabbers (Ignore = 2 in scene;
        // enforce here in case the packed scene was edited).
        reorderCueControl.MouseFilter = Control.MouseFilterEnum.Ignore;
        IsActive = true;
        DraggedCueId = shellbar.CueId;
        ResetDropFlags();
        MouseOverShellBar = shellbar;

        // Initial hover is the dragged item itself
    }

    public void ProcessInput(InputEvent @event)
    {
        if (!IsActive) return;

        if (@event is InputEventMouseMotion eventMouseMotion)
        {
            var reorderControl = reorderCueControl;
            reorderControl.GlobalPosition = new Vector2(eventMouseMotion.Position.X, eventMouseMotion.Position.Y);
            UpdateDropTarget(eventMouseMotion.GlobalPosition.Y);
        }

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

    private void UpdateDropTarget(float mouseY)
    {
        bool check = IsValidDropTarget();
        bool handledAsEnd = false;

        // Detect blank space below the *entire* list (after the visually last cue).
        // This should always result in a top-level append, even if the last cue is nested.
        var lastShell = owner.GetLastVisibleShellBar();
        if (lastShell != null)
        {
            float lastBottom = lastShell.GetGlobalPosition().Y + lastShell.Size.Y;
            if (mouseY > lastBottom + 8)  // tolerance for "blank area below everything"
            {
                InsertAbove = false;
                InsertBelow = true;
                InsertMakeChild = false;
                DropAtEndAsTopLevel = true;

                reorderLocationLabel.Text = UiLocalizer.T("Reorder at end (top level)");
                var indicator = reorderIndicatorPanel;
                indicator.GlobalPosition = new Vector2(cueContainer.GetGlobalPosition().X, lastBottom);
                indicator.Size = new Vector2(cueContainer.Size.X, 2);
                indicator.Visible = true;

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

                var label = reorderLocationLabel;
                var indicator = reorderIndicatorPanel;

                if (InsertBelow)
                {
                    label.Text = InsertMakeChild
                        ? $"Reorder below: {targetName} and child of: {parentName}"
                        : $"Reorder below: {targetName}";
                    indicator.GlobalPosition = new Vector2(MouseOverShellBar.GetGlobalPosition().X, MouseOverShellBar.GetGlobalPosition().Y + MouseOverShellBar.Size.Y);
                    indicator.Size = new Vector2(MouseOverShellBar.Size.X, 1);
                    indicator.Visible = true;
                }
                else if (InsertAbove)
                {
                    label.Text = InsertMakeChild
                        ? $"Reorder above: {targetName} and child of: {parentName}"
                        : $"Reorder above: {targetName}";
                    indicator.GlobalPosition = MouseOverShellBar.GetGlobalPosition();
                    indicator.Size = new Vector2(MouseOverShellBar.Size.X, 1);
                    indicator.Visible = true;
                }
                else
                {
                    label.Text = UiLocalizer.Tf("Make child of: {0}", targetName);
                    indicator.GlobalPosition = MouseOverShellBar.GetGlobalPosition();
                    indicator.Size = MouseOverShellBar.Size;
                    indicator.Visible = true;
                }
            }
            else
            {
                reorderLocationLabel.Text = UiLocalizer.T("Cannot reorder here");
                reorderIndicatorPanel.Visible = false;
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

        // Validate location. Blank end-of-list is always allowed.
        bool isEndDrop = DropAtEndAsTopLevel;
        if (!isEndDrop && !IsValidDropTarget())
        {
            Cancel();
            return;
        }

        var targetCue = isEndDrop ? null : CueList.FetchCueFromId(MouseOverShellBar?.CueId ?? -1);
        if (!isEndDrop && targetCue == null)
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

        owner.RecordHistory("Reorder cues");

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
        if (DropAtEndAsTopLevel || targetCue == null)
        {
            newParentId = -1;
            insertIndex = owner.RootOrder.Count;
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
            var siblings = owner.GetSiblingIdList(newParentId) ?? owner.RootOrder;
            insertIndex = siblings.IndexOf(targetCue.Id);
            if (insertIndex < 0)
                insertIndex = siblings.Count;
            if (InsertBelow)
                insertIndex++;
        }

        owner.BeginVirtualRefreshSuppress();
        try
        {
            foreach (var cue in toMove)
            {
                var list = owner.GetSiblingIdList(cue.ParentId) ?? owner.RootOrder;
                int oldIdx = list.IndexOf(cue.Id);
                if (cue.ParentId == newParentId && oldIdx >= 0 && oldIdx < insertIndex)
                    insertIndex--;
                owner.InsertCueInModel(cue, newParentId, insertIndex);
                insertIndex++;
            }
        }
        finally
        {
            owner.EndVirtualRefreshSuppress();
        }

        Cleanup(keepChanges: true);
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
        reorderCueControl.Visible = false;
        MouseOverShellBar = null;
        ResetDropFlags();
        DraggedCueId = -1;
        owner.SyncPointerInputProcessing();

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
