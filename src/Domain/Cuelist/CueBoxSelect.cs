// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Linq;
using Cue2.Domain.Cues;
using Godot;
using Cue2.UI.Shell;

namespace Cue2.Domain.Cuelist;

/// <summary>
/// Click-drag marquee (box) selection over the cuelist.
/// </summary>
/// <remarks>
/// Does not start from the reorder grabber (that path calls <see cref="CueList.StartReorder"/>
/// and accepts the event). Plain click without drag still selects a single cue / clears
/// empty space. Ctrl/Cmd-click toggles membership of that cue; Ctrl/Cmd-drag marquee still
/// unions hits with the selection at press time.
/// The press point is pinned to list content: when the cuelist scrolls (edge auto-scroll
/// or wheel), the origin cue stays inside the box and the marquee grows with the scroll.
/// </remarks>
internal sealed class CueBoxSelect
{
    private const float DragThresholdPx = 5f;
    private const float AutoScrollEdgePx = 32f;
    private const float AutoScrollSpeedPx = 14f;

    private readonly CueList _owner;
    private readonly VBoxContainer _cueContainer;
    private readonly ScrollContainer _scrollContainer;
    private readonly Panel _marqueePanel;
    private readonly StyleBoxFlat _marqueeStyle;

    private bool _pending;
    private bool _active;
    private Vector2 _startGlobal;
    private float _startScrollY;
    private Vector2 _currentGlobal;
    private ShellBar _originShell;
    private bool _additive;
    private List<Cue> _baselineSelection = new();
    private int _baselineFocusId = -1;
    private bool _scrollWired;

    /// <summary>True while a marquee rectangle is being dragged.</summary>
    public bool IsActive => _active;

    /// <summary>True after press while waiting to distinguish click vs drag.</summary>
    public bool IsPending => _pending;

    /// <summary>
    /// Creates the box-select controller and builds a non-interactive marquee overlay.
    /// </summary>
    /// <param name="owner">Owning cuelist.</param>
    /// <param name="cueContainer">Root shell container.</param>
    /// <param name="scrollContainer">List scroll view (auto-scroll + empty-space geometry).</param>
    /// <param name="overlayParent">Node that hosts the marquee panel (typically CueList root).</param>
    public CueBoxSelect(
        CueList owner,
        VBoxContainer cueContainer,
        ScrollContainer scrollContainer,
        Control overlayParent)
    {
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        _cueContainer = cueContainer;
        _scrollContainer = scrollContainer;

        _marqueeStyle = new StyleBoxFlat
        {
            BgColor = new Color(0.35f, 0.62f, 0.95f, 0.18f),
            BorderColor = new Color(0.55f, 0.78f, 1f, 0.9f),
            BorderWidthLeft = 1,
            BorderWidthTop = 1,
            BorderWidthRight = 1,
            BorderWidthBottom = 1,
            CornerRadiusTopLeft = 1,
            CornerRadiusTopRight = 1,
            CornerRadiusBottomRight = 1,
            CornerRadiusBottomLeft = 1
        };

        _marqueePanel = new Panel
        {
            Name = "BoxSelectMarquee",
            MouseFilter = Control.MouseFilterEnum.Ignore,
            Visible = false,
            ZIndex = 100
        };
        _marqueePanel.AddThemeStyleboxOverride("panel", _marqueeStyle);
        overlayParent?.AddChild(_marqueePanel);
    }

    /// <summary>
    /// Begins a potential box-select after a left press on a shell or empty list area.
    /// </summary>
    /// <param name="originShell">Shell under the press, or null for empty space.</param>
    /// <param name="globalPos">Press position in global coordinates.</param>
    /// <param name="additive">
    /// When true (Ctrl/Cmd): click toggles the origin cue; marquee unions hits with the
    /// pre-press selection.
    /// </param>
    public void BeginPending(ShellBar originShell, Vector2 globalPos, bool additive)
    {
        if (_active || _pending)
            Cancel();

        // Reorder owns the mouse while active; never compete with it.
        if (_owner.IsReordering)
            return;

        _pending = true;
        _active = false;
        _owner.SyncPointerInputProcessing();
        _originShell = originShell != null && GodotObject.IsInstanceValid(originShell) ? originShell : null;
        _startGlobal = globalPos;
        _startScrollY = GetScrollY();
        _currentGlobal = globalPos;
        _additive = additive;
        WireScrollWatch();

        _baselineSelection = ShellSelection.SelectedCues != null
            ? ShellSelection.SelectedCues.Where(c => c != null).ToList()
            : new List<Cue>();
        _baselineFocusId = _owner._globalData?.FocusedCue ?? -1;

        HideMarquee();
    }

    /// <summary>
    /// Processes global mouse/keyboard while pending or active.
    /// Called from <see cref="CueList._Input"/> when reorder is not active.
    /// </summary>
    /// <param name="event">Raw input event.</param>
    public void ProcessInput(InputEvent @event)
    {
        if (!_pending && !_active)
            return;

        if (@event is InputEventKey keyEvent && keyEvent.Pressed && keyEvent.Keycode == Key.Escape)
        {
            // Restore pre-press selection and abort.
            RestoreBaselineAndAbort();
            return;
        }

        if (@event is InputEventMouseButton rmb
            && rmb.ButtonIndex == MouseButton.Right
            && rmb.Pressed
            && _active)
        {
            RestoreBaselineAndAbort();
            return;
        }

        if (@event is InputEventMouseMotion motion)
        {
            _currentGlobal = motion.GlobalPosition;

            if (_pending && !_active)
            {
                float dist = _startGlobal.DistanceTo(_currentGlobal);
                if (dist >= DragThresholdPx)
                    ActivateMarquee();
            }

            if (_active)
                RefreshMarqueeAndSelection();

            return;
        }

        if (@event is InputEventMouseButton mb
            && mb.ButtonIndex == MouseButton.Left
            && !mb.Pressed)
        {
            Commit();
        }
    }

    /// <summary>
    /// Cancels any pending or active box-select without changing selection
    /// (used when reorder starts). Does not restore baseline.
    /// </summary>
    public void Cancel()
    {
        _pending = false;
        _active = false;
        _originShell = null;
        SetOwnerProcess(false);
        _owner.SyncPointerInputProcessing();
        UnwireScrollWatch();
        HideMarquee();
    }

    /// <summary>
    /// Frees C#-created style resources. Call from the owning cuelist <c>_ExitTree</c>.
    /// </summary>
    public void DisposeResources()
    {
        Cancel();
        if (_marqueePanel != null && GodotObject.IsInstanceValid(_marqueePanel))
            _marqueePanel.RemoveThemeStyleboxOverride("panel");
        Cue2.UI.Utilities.UiUtilities.DisposeRefCounted(_marqueeStyle);
    }

    private void ActivateMarquee()
    {
        if (_active)
            return;

        _active = true;
        _pending = true; // stays true until commit so ProcessInput keeps running
        SetOwnerProcess(true);
        RefreshMarqueeAndSelection();
    }

    /// <summary>
    /// Per-frame update while the marquee is active: keep auto-scroll running when the
    /// mouse is held at an edge (no motion events) and rebuild the box after scroll.
    /// </summary>
    public void Tick()
    {
        if (!_active || _owner == null || !GodotObject.IsInstanceValid(_owner))
            return;

        _currentGlobal = _owner.GetGlobalMousePosition();
        AutoScroll(_currentGlobal);
        RefreshMarqueeAndSelection();
    }

    private void Commit()
    {
        if (!_pending && !_active)
            return;

        bool wasMarquee = _active;
        var selection = _owner._globalData?.ShellSelection;
        if (selection == null)
        {
            Cancel();
            return;
        }

        if (wasMarquee)
        {
            // Final hit-test at release (may match live state).
            var hits = CollectHits(BuildMarqueeRect());
            var final = BuildFinalSelection(hits);
            selection.SetSelection(
                final,
                focusCue: final.Count > 0 ? final[^1] : null,
                recordHistory: !SelectionEqualsBaseline(final),
                description: "Box select cues",
                emitFocus: true);
        }
        else
        {
            // Click without drag (selection still at baseline until here).
            if (_originShell != null && GodotObject.IsInstanceValid(_originShell))
            {
                var cue = CueList.FetchCueFromId(_originShell.CueId);
                if (cue != null)
                {
                    if (_additive)
                        selection.ToggleSelection(cue);
                    else
                        selection.SelectIndividualShell(cue);
                }
            }
            else if (!_additive)
            {
                // Empty-space click clears selection.
                selection.ClearSelection();
            }
        }

        _pending = false;
        _active = false;
        _originShell = null;
        SetOwnerProcess(false);
        _owner.SyncPointerInputProcessing();
        UnwireScrollWatch();
        HideMarquee();
    }

    private void RestoreBaselineAndAbort()
    {
        var selection = _owner._globalData?.ShellSelection;
        if (selection != null && _active)
        {
            selection.SetSelection(
                _baselineSelection,
                focusCue: CueList.FetchCueFromId(_baselineFocusId),
                recordHistory: false,
                description: null,
                emitFocus: true);
        }

        Cancel();
    }

    private void ApplyLiveSelection()
    {
        var selection = _owner._globalData?.ShellSelection;
        if (selection == null)
            return;

        var hits = CollectHits(BuildMarqueeRect());
        var final = BuildFinalSelection(hits);
        // Live preview: no history, no inspector flood.
        selection.SetSelection(
            final,
            focusCue: final.Count > 0 ? final[^1] : null,
            recordHistory: false,
            description: null,
            emitFocus: false);
    }

    private List<Cue> BuildFinalSelection(List<Cue> hits)
    {
        if (!_additive)
            return hits;

        // Preserve baseline order, then append new hits in visual order.
        var result = new List<Cue>(_baselineSelection.Count + hits.Count);
        var seen = new HashSet<int>();
        foreach (var c in _baselineSelection)
        {
            if (c == null || seen.Contains(c.Id))
                continue;
            result.Add(c);
            seen.Add(c.Id);
        }

        foreach (var c in hits)
        {
            if (c == null || seen.Contains(c.Id))
                continue;
            result.Add(c);
            seen.Add(c.Id);
        }

        return result;
    }

    private bool SelectionEqualsBaseline(List<Cue> final)
    {
        if (final.Count != _baselineSelection.Count)
            return false;
        for (int i = 0; i < final.Count; i++)
        {
            if (!ReferenceEquals(final[i], _baselineSelection[i]))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Press point in current global space: Y follows list scroll so the origin cue
    /// stays inside the box as the cuelist moves.
    /// </summary>
    private Vector2 GetContentPinnedStartGlobal()
    {
        float dy = GetScrollY() - _startScrollY;
        return new Vector2(_startGlobal.X, _startGlobal.Y - dy);
    }

    private float GetScrollY()
    {
        if (_scrollContainer == null || !GodotObject.IsInstanceValid(_scrollContainer))
            return 0f;
        return _scrollContainer.ScrollVertical;
    }

    private Rect2 BuildMarqueeRect()
    {
        var start = GetContentPinnedStartGlobal();
        float x = Mathf.Min(start.X, _currentGlobal.X);
        float y = Mathf.Min(start.Y, _currentGlobal.Y);
        float w = Mathf.Abs(_currentGlobal.X - start.X);
        float h = Mathf.Abs(_currentGlobal.Y - start.Y);
        return new Rect2(x, y, w, h);
    }

    private List<Cue> CollectHits(Rect2 marqueeGlobal)
    {
        var hits = new List<Cue>();
        var list = _owner;
        if (list == null)
            return hits;

        for (int i = 0; i < list.VisibleRowIds.Count; i++)
        {
            var rect = list.GetVisibleRowGlobalRect(i);
            if (!marqueeGlobal.Intersects(rect))
                continue;
            var cue = CueList.FetchCueFromId(list.VisibleRowIds[i]);
            if (cue != null)
                hits.Add(cue);
        }

        return hits;
    }

    private void RefreshMarqueeAndSelection()
    {
        UpdateMarqueeVisual();
        ApplyLiveSelection();
    }

    private void UpdateMarqueeVisual()
    {
        if (_marqueePanel == null || !GodotObject.IsInstanceValid(_marqueePanel))
            return;

        var rect = BuildMarqueeRect();
        // Clip the overlay to the list viewport so it does not paint over the header.
        if (_scrollContainer != null && GodotObject.IsInstanceValid(_scrollContainer))
            rect = rect.Intersection(_scrollContainer.GetGlobalRect());

        // Marquee is parented to CueList; convert global → local.
        var parent = _marqueePanel.GetParentOrNull<Control>();
        Vector2 localPos = parent != null
            ? parent.GetGlobalTransformWithCanvas().AffineInverse() * rect.Position
            : rect.Position;

        _marqueePanel.Position = localPos;
        _marqueePanel.Size = rect.Size;
        _marqueePanel.Visible = rect.Size.X > 0.5f || rect.Size.Y > 0.5f;
    }

    private void WireScrollWatch()
    {
        if (_scrollWired || _scrollContainer == null || !GodotObject.IsInstanceValid(_scrollContainer))
            return;
        var vBar = _scrollContainer.GetVScrollBar();
        if (vBar == null)
            return;
        vBar.ValueChanged += OnScrollChanged;
        _scrollWired = true;
    }

    private void UnwireScrollWatch()
    {
        if (!_scrollWired || _scrollContainer == null || !GodotObject.IsInstanceValid(_scrollContainer))
        {
            _scrollWired = false;
            return;
        }

        var vBar = _scrollContainer.GetVScrollBar();
        if (vBar != null)
            vBar.ValueChanged -= OnScrollChanged;
        _scrollWired = false;
    }

    private void OnScrollChanged(double value)
    {
        if (!_active)
            return;
        RefreshMarqueeAndSelection();
    }

    private void SetOwnerProcess(bool enabled)
    {
        if (_owner != null && GodotObject.IsInstanceValid(_owner))
            _owner.SetProcess(enabled);
    }

    private void HideMarquee()
    {
        if (_marqueePanel != null && GodotObject.IsInstanceValid(_marqueePanel))
            _marqueePanel.Visible = false;
    }

    private void AutoScroll(Vector2 globalMouse)
    {
        if (_scrollContainer == null || !GodotObject.IsInstanceValid(_scrollContainer))
            return;

        var view = _scrollContainer.GetGlobalRect();
        if (globalMouse.Y < view.Position.Y + AutoScrollEdgePx)
        {
            _scrollContainer.ScrollVertical = (int)Mathf.Max(
                0,
                _scrollContainer.ScrollVertical - AutoScrollSpeedPx);
        }
        else if (globalMouse.Y > view.Position.Y + view.Size.Y - AutoScrollEdgePx)
        {
            _scrollContainer.ScrollVertical = (int)(_scrollContainer.ScrollVertical + AutoScrollSpeedPx);
        }
    }
}
