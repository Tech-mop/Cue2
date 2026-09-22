// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using Cue2.Domain.Cuelist;
using Cue2.Domain.Playback;
using Cue2.Domain.Devices;
using Cue2.Domain.ShowSettings;
using Cue2.Domain.Metadata;
using Cue2.Domain.Cues;
using Cue2.Domain.Connections;
using Cue2.Domain.Library;
using Cue2.Domain.Commands;
using Cue2.Services;
using Cue2.Media.Audio;
using Cue2.UI.Utilities;
using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Cue2.UI.Inspectors;

/// <summary>
/// Inspector for displaying and editing cue timelines, including hierarchical children.
/// Features a fixed track sidebar, time ruler, cue-colored bars, optional waveforms,
/// playhead scrubbing, and play-from-playhead. Parent rows can collapse to hide descendants.
/// </summary>
/// <summary>
/// Partial: Action starts, bar geometry, bar drag pre-wait, shell select
/// </summary>
public partial class TimelineInspector
{
    private void CollectCues(Cue cue, List<TimelineItem> items, ref int row, int depth = 0)
    {
        if (cue == null) return;

        bool hasChildren = cue.ChildCues != null && cue.ChildCues.Count > 0;
        items.Add(new TimelineItem
        {
            Cue = cue,
            Row = row++,
            Depth = depth,
            HasChildren = hasChildren
        });

        if (hasChildren && _collapsedCueIds.Contains(cue.Id))
            return;

        if (cue.ChildCues == null) return;
        foreach (var childId in cue.ChildCues)
        {
            var child = CueList.FetchCueFromId(childId);
            if (child != null)
                CollectCues(child, items, ref row, depth + 1);
        }
    }

    /// <summary>
    /// Computes the absolute start time of a cue, including accumulated pre-waits from parents.
    /// </summary>
    /// <param name="cue">The cue to compute the start time for.</param>
    /// <returns>The absolute start time in seconds.</returns>
    private double ComputeActionStart(Cue cue)
    {
        if (cue.ParentId == -1)
            return cue.PreWait;

        var parent = CueList.FetchCueFromId(cue.ParentId);
        if (parent == null)
        {
            GD.PrintErr($"TimelineInspector:ComputeActionStart - Parent not found for cue {cue.Id}");
            return 0;
        }
        return ComputeActionStart(parent) + cue.PreWait;
    }

    /// <summary>
    /// Computes the absolute start time of the parent cue.
    /// </summary>
    /// <param name="cue">The cue whose parent start time is needed.</param>
    /// <returns>The parent's absolute start time, or 0 if no parent.</returns>
    private double ComputeParentActionStart(Cue cue)
    {
        if (cue.ParentId == -1)
            return 0;

        var parent = CueList.FetchCueFromId(cue.ParentId);
        return parent != null ? ComputeActionStart(parent) : 0;
    }

    /// <summary>
    /// Updates positions and sizes for all cue bars in the timeline.
    /// </summary>
    private void UpdateAllPositionsAndSizes()
    {
        double maxTime = 0;

        foreach (var kvp in _cueToBar)
        {
            var cue = kvp.Key;
            var bar = kvp.Value;
            if (bar == null || !IsInstanceValid(bar)) continue;

            var start = ComputeActionStart(cue);
            ApplyBarGeometry(bar, cue, start, out _, out double contentDur);
            double end = start + contentDur;
            if (IsInfiniteLoopCue(cue))
                end += IsChildDrivenInfinite(cue) ? 5.0 : 2.5; // room for loop / "Child Looping" badge
            maxTime = Math.Max(maxTime, end);
        }

        // Pre-wait ghosts can extend before action start
        foreach (var kvp in _cueToPreWaitGhost)
        {
            var cue = kvp.Key;
            var ghost = kvp.Value;
            if (ghost == null || !IsInstanceValid(ghost)) continue;
            double parentStart = ComputeParentActionStart(cue);
            double actionStart = ComputeActionStart(cue);
            maxTime = Math.Max(maxTime, actionStart);
            maxTime = Math.Max(maxTime, parentStart + Math.Max(0, cue.PreWait));
        }

        _contentMaxTime = maxTime;

        if (_cueToRow.Count == 0)
        {
            ApplyTimelineContentSize(100, RowHeight);
            UpdateDurationSummary();
            return;
        }

        float contentWidth = (float)(maxTime * _scale + 100);
        float contentHeight = _cueToRow.Values.Max() * RowHeight + RowHeight;
        ApplyTimelineContentSize(contentWidth, contentHeight);

        foreach (var bg in _rowBackgrounds)
            bg.Size = new Vector2(contentWidth, RowHeight);

        if (_timeGrid != null && IsInstanceValid(_timeGrid))
        {
            _timeGrid.Position = Vector2.Zero;
            _timeGrid.Size = new Vector2(contentWidth, contentHeight);
            _timeGrid.ZoomScale = _scale;
            _timeGrid.ContentHeight = contentHeight;
            _timeGrid.QueueRedraw();
        }

        if (_sidebarContent != null && IsInstanceValid(_sidebarContent))
        {
            float sideW = _trackSidebar?.Size.X > 1 ? _trackSidebar.Size.X : SidebarWidth;
            _sidebarContent.CustomMinimumSize = new Vector2(sideW, contentHeight);
            _sidebarContent.Size = new Vector2(sideW, contentHeight);
        }

        UpdatePlayheadLineGeometry();
        UpdateDurationSummary();
    }

    /// <summary>
    /// Applies position/size/labels/waveform layout for a single cue bar.
    /// Content origin is 0 (sidebar is separate from timeline content).
    /// Infinite/looping cues show a single cycle block (not an arbitrary long span).
    /// </summary>
    private void ApplyBarGeometry(ColorRect bar, Cue cue, double start, out float calculatedWidth, out double contentDur)
    {
        bool infinite = IsInfiniteLoopCue(cue);
        contentDur = GetBarDisplayDurationSeconds(cue);
        bool instant = !infinite && contentDur < 1e-4;

        calculatedWidth = (float)(contentDur * _scale);

        float minW = instant ? InstantBarMinWidth : MinBarWidth;
        float displayWidth = Mathf.Max(calculatedWidth, minW);

        int row = _cueToRow.GetValueOrDefault(cue, 0);
        float barH = RowHeight - 6f;
        float barY = row * RowHeight + 3f;
        bar.Size = new Vector2(displayWidth, barH);
        bar.Position = new Vector2((float)(start * _scale), barY);

        // Instant cues get a brighter accent
        if (instant)
        {
            var accent = ResolveAccentColor(cue, bar.Color).Lightened(0.25f);
            var startLineInst = bar.GetNodeOrNull<ColorRect>("StartLine");
            if (startLineInst != null)
                startLineInst.Color = accent;
            var flagInst = bar.GetNodeOrNull<ColorRect>("Flag");
            if (flagInst != null)
                flagInst.Color = accent;
        }

        var wave = bar.GetNodeOrNull<CueBarWaveform>("Waveform");
        if (wave != null && IsInstanceValid(wave))
        {
            // Looping cues: draw one cycle only (playCount forced to 1 for display).
            if (infinite)
                wave.PlayCount = 1;

            // Waveform maps peaks across its control width. Parent bars often extend past
            // own media when children are longer — keep time accuracy by sizing the wave
            // to own-media duration only; the remainder of the bar stays empty.
            float waveW = ComputeWaveformDisplayWidth(cue, displayWidth);
            wave.Position = Vector2.Zero;
            wave.Size = new Vector2(waveW, barH);
            wave.QueueRedraw();
        }

        var endLine = bar.GetNodeOrNull<ColorRect>("EndLine");
        if (endLine != null)
        {
            endLine.Position = new Vector2(Mathf.Max(0, displayWidth - 2), 0);
            endLine.Size = new Vector2(2, bar.Size.Y);
            // Hide end accent for very short/instant markers
            endLine.Visible = !instant || displayWidth > 10f;
        }

        var startLine = bar.GetNodeOrNull<ColorRect>("StartLine");
        if (startLine != null)
            startLine.Size = new Vector2(2, bar.Size.Y);

        var flag = bar.GetNodeOrNull<ColorRect>("Flag");
        if (flag != null)
            flag.Position = new Vector2(0, Math.Max(0, bar.Size.Y - 8));

        // Pre-wait ghost: parentStart → actionStart
        if (_cueToPreWaitGhost.TryGetValue(cue, out var ghost) && ghost != null && IsInstanceValid(ghost))
        {
            double parentStart = ComputeParentActionStart(cue);
            float ghostX = (float)(parentStart * _scale);
            float ghostW = (float)(Math.Max(0, start - parentStart) * _scale);
            ghost.Position = new Vector2(ghostX, barY);
            ghost.Size = new Vector2(Mathf.Max(0, ghostW), barH);
            ghost.Visible = ghostW > 0.5f;
        }

        PositionCueLabels(cue, bar.Position, displayWidth, start);
    }

    /// <summary>
    /// Places start/pre on the first line, length on the second line below, and loop badge after the bar.
    /// </summary>
    private void PositionCueLabels(Cue cue, Vector2 barPosition, float barDisplayWidth, double startTimeSeconds)
    {
        float labelX = barPosition.X + LabelStartOffsetX;
        float topY = barPosition.Y + 1f;

        if (_cueToTimeLabel.TryGetValue(cue, out var timeLabel) && timeLabel != null && IsInstanceValid(timeLabel))
        {
            timeLabel.Text = FormatBarStartPreLabel(cue, startTimeSeconds);
            timeLabel.Position = new Vector2(labelX, topY);
            timeLabel.ResetSize();
        }

        if (_cueToDurationLabel.TryGetValue(cue, out var durationLabel)
            && durationLabel != null && IsInstanceValid(durationLabel))
        {
            durationLabel.Text = FormatBarLengthLabel(cue);
            // Second line: length sits below pre-wait / start line
            durationLabel.Position = new Vector2(labelX, topY + 13f);
            durationLabel.ResetSize();
        }

        if (_cueToLoopBadge.TryGetValue(cue, out var loopBadge) && loopBadge != null && IsInstanceValid(loopBadge))
        {
            bool childLoop = IsChildDrivenInfinite(cue);
            loopBadge.Text = FormatLoopBadgeText(cue);
            loopBadge.TooltipText = childLoop
                ? UiLocalizer.T("A nested child cue loops indefinitely")
                : UiLocalizer.T("This cue's media loops indefinitely");
            // Child-loop badge is longer — keep a bit more room after the bar.
            loopBadge.Position = new Vector2(barPosition.X + barDisplayWidth + 6f, topY + 4f);
            loopBadge.ResetSize();
            loopBadge.Visible = true;
        }
    }

    /// <summary>
    /// Sets TimelineArea minimum size with right/bottom padding so content stays clear of scrollbars.
    /// Drawing (rows, grid, bars) uses the unpadded content size; padding is empty space.
    /// </summary>
    private void ApplyTimelineContentSize(float contentWidth, float contentHeight)
    {
        if (_timelineArea == null || !IsInstanceValid(_timelineArea)) return;
        contentWidth = Math.Max(1f, contentWidth);
        contentHeight = Math.Max(1f, contentHeight);
        _timelineArea.CustomMinimumSize = new Vector2(
            contentWidth + ScrollbarPadRight,
            contentHeight + ScrollbarPadBottom);
    }

    /// <summary>
    /// Handles input events for cue bars, including dragging to adjust pre-wait times
    /// and double-click to set playhead at action start.
    /// </summary>
    /// <param name="event">The input event.</param>
    /// <param name="cue">The associated cue.</param>
    /// <param name="bar">The visual bar representation.</param>
    /// <remarks>
    /// History is recorded on the first real pre-wait change during a drag, not on mouse-down,
    /// so a click without drag does not push an empty undo step (P1-20).
    /// </remarks>
    private void HandleBarInput(InputEvent @event, Cue cue, ColorRect bar)
    {
        if (@event is InputEventMouseButton mouseButton)
        {
            if (mouseButton.ButtonIndex == MouseButton.Left)
            {
                if (mouseButton.Pressed)
                {
                    // Double-click: set playhead to action start
                    double now = Time.GetTicksMsec() / 1000.0;
                    if (cue.Id == _lastClickCueId && now - _lastClickTime < 0.35)
                    {
                        _followLivePlayhead = false;
                        SetPlayheadSeconds(ComputeActionStart(cue));
                        EnsurePlayheadVisible();
                        _lastClickCueId = -1;
                        _dragging = false;
                        _preWaitDragHistoryRecorded = false;
                        GrabFocusSafe();
                        GetViewport()?.SetInputAsHandled();
                        return;
                    }
                    _lastClickTime = now;
                    _lastClickCueId = cue.Id;

                    _dragging = true;
                    _initialBarPos = bar.Position;
                    _initialMousePos = GetViewport().GetMousePosition();
                    _draggedCue = cue;
                    // Do not RecordCueChange here — click without drag would create a no-op undo step.
                    _preWaitDragHistoryRecorded = false;
                    GrabFocusSafe();
                }
                else
                {
                    if (_draggedCue != null && _preWaitDragHistoryRecorded)
                    {
                        string key = $"cue:{_draggedCue.Id}:timeline-prewait";
                        InspectorMultiEditSupport.EndCoalesce(_globalData, multiHistory: false, key, key);
                    }
                    _dragging = false;
                    _draggedCue = null;
                    _preWaitDragHistoryRecorded = false;
                    _globalSignals.EmitSignal(nameof(GlobalSignals.SyncShellInspector));
                }
            }
        }
        else if (@event is InputEventMouseMotion && _dragging && _draggedCue == cue)
        {
            var currentMousePos = GetViewport().GetMousePosition();
            var delta = currentMousePos - _initialMousePos;

            // Earliest legal action start = parent action start (pre-wait 0). Never snap back
            // to drag-start position — that felt mouse-speed dependent and wrong for children.
            double parentStart = ComputeParentActionStart(cue);
            float minX = (float)(Math.Max(0.0, parentStart) * _scale);

            float newX = _initialBarPos.X + delta.X;
            newX = Mathf.Max(minX, newX);

            double newStart = newX / Math.Max(0.001f, _scale);
            double newPreWait = Math.Max(0.0, newStart - parentStart);

            // Skip no-op updates (still re-apply geometry so the bar stays clamped at minX).
            if (Math.Abs(cue.PreWait - newPreWait) < 1e-9)
            {
                if (_cueToBar.TryGetValue(cue, out var liveBar) && liveBar != null && IsInstanceValid(liveBar))
                    ApplyBarGeometry(liveBar, cue, ComputeActionStart(cue), out _, out _);
                return;
            }

            // First real change in this drag: capture pre-change memento (coalesced for the drag).
            if (!_preWaitDragHistoryRecorded)
            {
                InspectorMultiEditSupport.RecordBeforeEditById(
                    _globalData,
                    multiHistory: false,
                    cue.Id,
                    "Edit pre-wait (timeline)",
                    coalesceKey: $"cue:{cue.Id}:timeline-prewait");
                _preWaitDragHistoryRecorded = true;
            }

            cue.PreWait = newPreWait;
            RecalcDurationsUp(cue);
            UpdateSubtreePositions(cue);

            if (cue.ParentId != -1)
            {
                var parent = CueList.FetchCueFromId(cue.ParentId);
                UpdateAncestorSizes(parent);
            }

            UpdateTimelineSize();
        }
    }

    /// <summary>
    /// Recalculates total durations for a cue and its ancestors.
    /// </summary>
    /// <param name="cue">The cue to start recalculating from.</param>
    private void RecalcDurationsUp(Cue cue)
    {
        cue.CalculateTotalDuration();
        if (cue.ParentId != -1)
        {
            var parent = CueList.FetchCueFromId(cue.ParentId);
            if (parent != null)
                RecalcDurationsUp(parent);
        }
    }

    /// <summary>
    /// Updates positions and sizes for a cue and its child subtree.
    /// </summary>
    /// <param name="cue">The root cue of the subtree.</param>
    private void UpdateSubtreePositions(Cue cue)
    {
        if (!_cueToBar.TryGetValue(cue, out var bar) || bar == null || !IsInstanceValid(bar)) return;

        var start = ComputeActionStart(cue);
        ApplyBarGeometry(bar, cue, start, out _, out _);

        if (cue.ChildCues == null) return;
        foreach (var childId in cue.ChildCues)
        {
            var child = CueList.FetchCueFromId(childId);
            if (child != null)
                UpdateSubtreePositions(child);
        }
    }

    /// <summary>
    /// Updates sizes for a cue and its ancestors without repositioning children only.
    /// </summary>
    /// <param name="cue">The cue to update.</param>
    private void UpdateAncestorSizes(Cue cue)
    {
        if (cue == null) return;

        if (_cueToBar.TryGetValue(cue, out var bar) && bar != null && IsInstanceValid(bar))
        {
            var start = ComputeActionStart(cue);
            ApplyBarGeometry(bar, cue, start, out _, out _);
        }

        if (cue.ParentId != -1)
        {
            var parent = CueList.FetchCueFromId(cue.ParentId);
            UpdateAncestorSizes(parent);
        }
    }

    /// <summary>
    /// Recalculates and sets the minimum size of the timeline area based on content.
    /// </summary>
    private void UpdateTimelineSize()
    {
        double maxTime = 0;
        foreach (var kvp in _cueToBar)
        {
            var cue = kvp.Key;
            var start = ComputeActionStart(cue);
            // Looping cues only show one cycle (+ badge padding).
            double end = start + GetBarDisplayDurationSeconds(cue);
            if (IsInfiniteLoopCue(cue))
                end += IsChildDrivenInfinite(cue) ? 5.0 : 2.5;
            maxTime = Math.Max(maxTime, end);
        }
        _contentMaxTime = maxTime;
        float contentWidth = (float)(maxTime * _scale + 100);
        float contentHeight = Math.Max(RowHeight, _timelineArea.CustomMinimumSize.Y - ScrollbarPadBottom);
        ApplyTimelineContentSize(contentWidth, contentHeight);

        foreach (var bg in _rowBackgrounds)
            bg.Size = new Vector2(contentWidth, RowHeight);

        if (_timeGrid != null && IsInstanceValid(_timeGrid))
        {
            _timeGrid.Size = new Vector2(contentWidth, contentHeight);
            _timeGrid.QueueRedraw();
        }

        UpdatePlayheadLineGeometry();
        UpdateDurationSummary();
    }

    /// <summary>
    /// Handles selection of a new cue shell.
    /// </summary>
    /// <param name="cueId">The ID of the selected cue.</param>
    private void ShellSelected(int cueId)
    {
        _focusedCue = cueId >= 0 ? CueList.FetchCueFromId(cueId) : null;

        if (_focusedCue == null)
        {
            GD.Print("TimelineInspector:ShellSelected - No cue selected");
            ShowNoSelection();
            return;
        }

        if (_infoLabel != null)
            _infoLabel.Visible = false;
        if (_timeLineContainer != null)
            _timeLineContainer.Visible = true;

        PruneCollapseStateToFocusedTree();
        LoadTimeline();
        UpdateIdleProcess();
    }

    /// <summary>
    /// Hides the timeline canvas and shows the same greyed empty-state as other inspectors.
    /// </summary>
    private void ShowNoSelection()
    {
        _focusedCue = null;
        if (_infoLabel != null)
        {
            _infoLabel.Visible = true;
            _infoLabel.Text = UiLocalizer.T("No shell selected");
        }
        if (_timeLineContainer != null)
            _timeLineContainer.Visible = false;
        if (_sidebarSeparator != null)
            _sidebarSeparator.Visible = false;
        ClearTimelineVisuals();
        _timelineStructureKey = null;
        UpdateIdleProcess();
    }

    /// <summary>
    /// Removes collapse entries for cues not under the current focused root.
    /// </summary>
    private void PruneCollapseStateToFocusedTree()
    {
        if (_focusedCue == null || _collapsedCueIds.Count == 0)
            return;

        var live = new HashSet<int>();
        CollectAllDescendantIds(_focusedCue, live);
        _collapsedCueIds.RemoveWhere(id => !live.Contains(id));
    }

    private static void CollectAllDescendantIds(Cue cue, HashSet<int> ids)
    {
        if (cue == null || !ids.Add(cue.Id)) return;
        if (cue.ChildCues == null) return;
        foreach (var childId in cue.ChildCues)
        {
            var child = CueList.FetchCueFromId(childId);
            if (child != null)
                CollectAllDescendantIds(child, ids);
        }
    }

    /// <summary>
    /// Struct representing a cue and its row in the timeline.
    /// </summary>
    private struct TimelineItem
    {
        public Cue Cue;
        public int Row;
        public int Depth;
        public bool HasChildren;
    }

    /// <summary>
    /// Compact peak waveform drawn inside a timeline cue bar.
    /// Maps the bar width to the play region (start–end of file) tiled by <see cref="PlayCount"/>.
    /// All plays use a consistent colour; vertical dividers mark playcount boundaries.
    /// </summary>
}
