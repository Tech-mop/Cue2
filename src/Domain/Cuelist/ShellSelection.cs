// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Linq;
using Cue2.Domain.Cues;
using Cue2.Services;
using Godot;

namespace Cue2.Domain.Cuelist;

public partial class ShellSelection : Node
{
    private GlobalData _globalData;
    private GlobalSignals _globalSignals;


    public static List<Cue> SelectedCues = new();

    public override void _Ready()
    {
        _globalData = GetNode<GlobalData>("/root/GlobalData");
        _globalSignals = GetNode<GlobalSignals>("/root/GlobalSignals");

        _globalSignals.SelectAllCues += OnSelectAllCues;
        _globalSignals.SelectNextCue += SelectNextCue;
        _globalSignals.SelectPreviousCue += SelectPreviousCue;
    }

    private void OnSelectAllCues() => SelectAllShells();

    /// <summary>
    /// Replaces the selection with a single cue (standard click).
    /// </summary>
    /// <param name="selectedCue">Cue to select.</param>
    /// <param name="recordHistory">
    /// When true (default), records a selection-only undo step before changing selection.
    /// Pass false for programmatic selection that is already covered by a cuelist/cue history
    /// step (create, group, playback playhead, etc.).
    /// </param>
    public void SelectIndividualShell(Cue selectedCue, bool recordHistory = true)
    {
        if (selectedCue == null) return;

        // No-op when this is already the sole selection.
        if (SelectedCues.Count == 1 && ReferenceEquals(SelectedCues[0], selectedCue))
            return;

        if (recordHistory)
            RecordSelectionHistory("Select cue");

        ClearSelectionVisuals();
        SelectedCues.Clear();
        ApplySelectCue(selectedCue);
    }

    /// <summary>
    /// Shift-click range selection from the last selected cue through <paramref name="pressedCue"/>.
    /// </summary>
    /// <param name="pressedCue">Range end (inclusive).</param>
    /// <param name="recordHistory">When true, records a selection undo step before expanding.</param>
    public void SelectThrough(Cue pressedCue, bool recordHistory = true)
    {
        var ordered = GetAllCuesInOrder();

        if (SelectedCues.Count == 0 || pressedCue == null)
            return;

        var startCue = SelectedCues.Last();
        int startIndex = ordered.IndexOf(startCue);
        int pressedIndex = ordered.IndexOf(pressedCue);
        if (startIndex < 0 || pressedIndex < 0) return;

        int start = Math.Min(startIndex, pressedIndex);
        int end = Math.Max(startIndex, pressedIndex);

        // Detect whether the range would actually add anything (or only re-focus).
        bool willAdd = false;
        for (int i = start; i <= end; i++)
        {
            Cue cue = ordered[i];
            if (cue != null && !SelectedCues.Contains(cue))
            {
                willAdd = true;
                break;
            }
        }

        bool focusChanged = _globalData != null && _globalData.FocusedCue != pressedCue.Id;
        if (!willAdd && !focusChanged)
            return;

        if (recordHistory)
            RecordSelectionHistory(willAdd ? "Select cue range" : "Focus cue");

        // Expand selection silently — only emit ShellFocused once for the pressed cue.
        // (Per-cue AddSelection would flood async audio/video inspectors mid multi-select.)
        for (int i = start; i <= end; i++)
        {
            Cue cue = ordered[i];
            if (cue == null || SelectedCues.Contains(cue))
                continue;
            cue.ShellBar?.Select();
            SelectedCues.Add(cue);
        }
        _globalSignals.EmitSignal(nameof(GlobalSignals.ShellFocused), pressedCue.Id);
    }

    private List<ShellBar> GetAllShellBarsInOrder(VBoxContainer container)
    {
        List<ShellBar> result = new();
        foreach (var child in container.GetChildren())
        {
            if (child is ShellBar sb)
            {
                result.Add(sb);
                var childContainer = sb.GetNode<VBoxContainer>("%ShellChildContainer");
                // Only recurse into child groups if they are currently expanded (visible).
                // This prevents next/previous selection from landing on cues hidden inside collapsed groups.
                if (childContainer != null && childContainer.Visible)
                {
                    result.AddRange(GetAllShellBarsInOrder(childContainer));
                }
            }
        }
        return result;
    }

    /// <summary>
    /// Selects every currently visible cue (respects collapsed groups).
    /// </summary>
    /// <param name="recordHistory">When true, records a selection undo step first.</param>
    public void SelectAllShells(bool recordHistory = true)
    {
        var visibleCues = GetAllCuesInOrder();
        if (visibleCues.Count == 0) return;

        // No-op when selection already matches the full visible set in order.
        if (SelectedCues.Count == visibleCues.Count &&
            SelectedCues.Zip(visibleCues, (a, b) => ReferenceEquals(a, b)).All(eq => eq))
            return;

        if (recordHistory)
            RecordSelectionHistory("Select all cues");

        ClearSelectionVisuals();
        SelectedCues.Clear();

        foreach (var cue in visibleCues)
        {
            cue.ShellBar?.Select();
            SelectedCues.Add(cue);
        }

        if (visibleCues.Count > 0)
            _globalSignals.EmitSignal(nameof(GlobalSignals.ShellFocused), visibleCues.Last().Id);
    }

    /// <summary>
    /// Adds a cue to the multi-selection (does nothing if already selected).
    /// </summary>
    /// <param name="cue">Cue to add.</param>
    /// <param name="recordHistory">When true, records a selection undo step first.</param>
    public void AddSelection(Cue cue, bool recordHistory = true)
    {
        if (cue == null) return;
        if (SelectedCues.Contains(cue))
            return;

        if (recordHistory)
            RecordSelectionHistory("Add cue to selection");

        ApplySelectCue(cue);
    }

    /// <summary>
    /// Ctrl/Cmd-click: add when not selected, remove when already selected.
    /// </summary>
    /// <param name="cue">Cue under the click.</param>
    /// <param name="recordHistory">When true, records a selection undo step first.</param>
    public void ToggleSelection(Cue cue, bool recordHistory = true)
    {
        if (cue == null) return;

        if (SelectedCues.Contains(cue))
            RemoveSelection(cue, recordHistory);
        else
            AddSelection(cue, recordHistory);
    }

    /// <summary>
    /// Removes a cue from the multi-selection (Ctrl/Cmd-click on an already selected shell).
    /// </summary>
    /// <param name="cue">Cue to deselect.</param>
    /// <param name="recordHistory">When true, records a selection undo step first.</param>
    public void RemoveSelection(Cue cue, bool recordHistory = true)
    {
        if (cue == null) return;
        if (!SelectedCues.Contains(cue))
            return;

        if (recordHistory)
            RecordSelectionHistory("Remove cue from selection");

        int previousFocusId = _globalData?.FocusedCue ?? -1;
        bool removedFocused = previousFocusId == cue.Id;

        if (cue.ShellBar != null && IsInstanceValid(cue.ShellBar))
            cue.ShellBar.Deselect();
        SelectedCues.Remove(cue);

        // Prefer keeping the existing focus when it remains selected; otherwise fall back
        // to the last remaining selected cue (or clear when the selection is empty).
        // Always emit so multi-edit inspectors re-read SelectedCues after a toggle-off.
        int focusId = -1;
        if (SelectedCues.Count > 0)
        {
            if (!removedFocused && SelectedCues.Any(c => c != null && c.Id == previousFocusId))
                focusId = previousFocusId;
            else
                focusId = SelectedCues[^1]?.Id ?? -1;
        }

        _globalSignals?.EmitSignal(nameof(GlobalSignals.ShellFocused), focusId);
    }

    /// <summary>
    /// Replaces the multi-selection with <paramref name="cues"/> (document/visual order).
    /// Used by box-select live preview and commit.
    /// </summary>
    /// <param name="cues">Cues to select; null or empty clears selection.</param>
    /// <param name="focusCue">Cue to focus (defaults to last in list).</param>
    /// <param name="recordHistory">When true, records a selection undo step if the set changes.</param>
    /// <param name="description">History description when recording.</param>
    /// <param name="emitFocus">When true, emits <c>ShellFocused</c> once after apply.</param>
    public void SetSelection(
        IReadOnlyList<Cue> cues,
        Cue focusCue = null,
        bool recordHistory = true,
        string description = "Select cues",
        bool emitFocus = true)
    {
        cues ??= Array.Empty<Cue>();
        var next = new List<Cue>(cues.Count);
        var seen = new HashSet<int>();
        foreach (var cue in cues)
        {
            if (cue == null || seen.Contains(cue.Id))
                continue;
            next.Add(cue);
            seen.Add(cue.Id);
        }

        // No-op when selection already matches in order.
        if (SelectedCues.Count == next.Count &&
            SelectedCues.Zip(next, (a, b) => ReferenceEquals(a, b)).All(eq => eq))
        {
            if (emitFocus && focusCue != null && _globalData != null && _globalData.FocusedCue != focusCue.Id)
                _globalSignals?.EmitSignal(nameof(GlobalSignals.ShellFocused), focusCue.Id);
            return;
        }

        if (recordHistory)
            RecordSelectionHistory(string.IsNullOrEmpty(description) ? "Select cues" : description);

        ClearSelectionVisuals();
        SelectedCues.Clear();

        foreach (var cue in next)
        {
            cue.ShellBar?.Select();
            SelectedCues.Add(cue);
        }

        if (!emitFocus)
            return;

        Cue focus = focusCue;
        if (focus == null && SelectedCues.Count > 0)
            focus = SelectedCues[^1];

        int focusId = focus?.Id ?? -1;
        _globalSignals?.EmitSignal(nameof(GlobalSignals.ShellFocused), focusId);
    }

    /// <summary>
    /// Clears all selected shells and focus (empty-space click).
    /// </summary>
    /// <param name="recordHistory">When true, records a selection undo step if anything was selected.</param>
    public void ClearSelection(bool recordHistory = true)
    {
        if (SelectedCues.Count == 0 && (_globalData == null || _globalData.FocusedCue < 0))
            return;

        if (recordHistory)
            RecordSelectionHistory("Clear selection");

        ClearSelectionVisuals();
        SelectedCues.Clear();
        _globalSignals?.EmitSignal(nameof(GlobalSignals.ShellFocused), -1);
    }

    /// <summary>
    /// Returns the ordered list of currently visible cues for navigation/selection.
    /// Respects group expansion state (cues inside collapsed groups are excluded).
    /// </summary>
    public List<Cue> GetAllCuesInOrder()
    {
        return _globalData?.Cuelist?.GetVisibleCues() ?? new List<Cue>();
    }

    public void SelectNextCue()
    {
        var ordered = GetAllCuesInOrder();
        if (ordered.Count == 0) return;

        Cue target;
        if (SelectedCues.Count == 0)
        {
            target = ordered[0];
        }
        else
        {
            var current = SelectedCues.Last();
            int idx = ordered.IndexOf(current);
            if (idx < 0)
            {
                // Current selection is hidden (e.g. in a collapsed group). Start from beginning.
                target = ordered[0];
            }
            else
            {
                int next = (idx + 1) % ordered.Count;
                target = ordered[next];
            }
        }
        SelectIndividualShell(target);
    }

    public void SelectPreviousCue()
    {
        var ordered = GetAllCuesInOrder();
        if (ordered.Count == 0) return;

        Cue target;
        if (SelectedCues.Count == 0)
        {
            target = ordered[ordered.Count - 1];
        }
        else
        {
            var current = SelectedCues.Last();
            int idx = ordered.IndexOf(current);
            if (idx < 0)
            {
                // Current selection is hidden (e.g. in a collapsed group). Start from end.
                target = ordered[ordered.Count - 1];
            }
            else
            {
                int prev = (idx - 1 + ordered.Count) % ordered.Count;
                target = ordered[prev];
            }
        }
        SelectIndividualShell(target);
    }

    /// <summary>
    /// Records a selection-only history checkpoint when not mid-restore.
    /// </summary>
    private void RecordSelectionHistory(string description)
    {
        var history = _globalData?.HistoryManager;
        if (history == null || history.IsRestoring) return;
        history.RecordSelectionChange(description);
    }

    private void ClearSelectionVisuals()
    {
        foreach (var cue in SelectedCues.ToList())
        {
            if (cue?.ShellBar != null && IsInstanceValid(cue.ShellBar))
                cue.ShellBar.Deselect();
        }
    }

    /// <summary>
    /// Marks <paramref name="cue"/> selected and emits focus (no history).
    /// </summary>
    private void ApplySelectCue(Cue cue)
    {
        cue.ShellBar?.Select();
        SelectedCues.Add(cue);
        _globalSignals.EmitSignal(nameof(GlobalSignals.ShellFocused), cue.Id);
    }
}
