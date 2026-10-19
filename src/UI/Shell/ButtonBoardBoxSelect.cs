// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Linq;
using Cue2.Domain.Cuelist;
using Cue2.Domain.Cues;
using Cue2.UI.Utilities;
using Godot;

namespace Cue2.UI.Shell;

/// <summary>
/// Click-drag marquee selection over the button board (same rules as the cuelist box-select).
/// </summary>
internal sealed class ButtonBoardBoxSelect
{
    private const float DragThresholdPx = 5f;

    private readonly ButtonBoard _owner;
    private readonly ScrollContainer _scroll;
    private readonly Panel _marqueePanel;
    private readonly StyleBoxFlat _marqueeStyle;

    private bool _pending;
    private bool _active;
    private Vector2 _startGlobal;
    private float _startScrollX;
    private float _startScrollY;
    private Vector2 _currentGlobal;
    private ButtonBoardCell _originCell;
    private bool _additive;
    private List<Cue> _baselineSelection = new();
    private int _baselineFocusId = -1;
    private bool _scrollWired;
    private int _lastLiveSelectionSig;

    /// <summary>True while a marquee rectangle is being dragged.</summary>
    public bool IsActive => _active;

    /// <summary>True after press while waiting to distinguish click vs drag.</summary>
    public bool IsPending => _pending;

    /// <summary>
    /// Creates the marquee overlay parented to <paramref name="overlayParent"/>.
    /// </summary>
    public ButtonBoardBoxSelect(ButtonBoard owner, ScrollContainer scroll, Control overlayParent)
    {
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        _scroll = scroll;

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
    /// Begins a potential box-select after a left press on a cell or empty board area.
    /// </summary>
    public void BeginPending(ButtonBoardCell originCell, Vector2 globalPos, bool additive)
    {
        if (_active || _pending)
            Cancel();

        if (_owner.IsReordering)
            return;

        _pending = true;
        _active = false;
        _originCell = originCell != null && GodotObject.IsInstanceValid(originCell) ? originCell : null;
        _startGlobal = globalPos;
        _startScrollX = GetScrollX();
        _startScrollY = GetScrollY();
        _currentGlobal = globalPos;
        _additive = additive;
        _lastLiveSelectionSig = 0;
        WireScrollWatch();

        _baselineSelection = ShellSelection.SelectedCues != null
            ? ShellSelection.SelectedCues.Where(c => c != null).ToList()
            : new List<Cue>();
        _baselineFocusId = _owner.FocusedCueId;

        HideMarquee();
        _owner.SyncPointerProcessing();
    }

    /// <summary>Processes global mouse/keyboard while pending or active.</summary>
    public void ProcessInput(InputEvent @event)
    {
        if (!_pending && !_active)
            return;

        if (@event is InputEventKey keyEvent && keyEvent.Pressed && keyEvent.Keycode == Key.Escape)
        {
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
            if (_pending && !_active && _startGlobal.DistanceTo(_currentGlobal) >= DragThresholdPx)
                ActivateMarquee();
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
    /// Cancels pending/active marquee without restoring baseline (used when a cell drag starts).
    /// </summary>
    public void Cancel()
    {
        _pending = false;
        _active = false;
        _originCell = null;
        _lastLiveSelectionSig = 0;
        UnwireScrollWatch();
        HideMarquee();
        _owner.SyncPointerProcessing();
    }

    /// <summary>Frees the marquee style. Call from the board <c>_ExitTree</c>.</summary>
    public void DisposeResources()
    {
        Cancel();
        if (_marqueePanel != null && GodotObject.IsInstanceValid(_marqueePanel))
            _marqueePanel.RemoveThemeStyleboxOverride("panel");
        UiUtilities.DisposeRefCounted(_marqueeStyle);
        if (_marqueePanel != null && GodotObject.IsInstanceValid(_marqueePanel))
        {
            _marqueePanel.GetParent()?.RemoveChild(_marqueePanel);
            _marqueePanel.QueueFree();
        }
    }

    /// <summary>Per-frame while the marquee is held: edge auto-scroll and live hit-test.</summary>
    public void Tick(double delta)
    {
        if (!_active)
            return;
        _currentGlobal = _owner.GetGlobalMousePosition();
        _owner.AutoScrollBoardFromPointer(_currentGlobal, delta);
        RefreshMarqueeAndSelection();
    }

    private void ActivateMarquee()
    {
        if (_active)
            return;
        _active = true;
        _pending = true;
        _owner.SyncPointerProcessing();
        RefreshMarqueeAndSelection();
    }

    private void Commit()
    {
        if (!_pending && !_active)
            return;

        bool wasMarquee = _active;
        var selection = _owner.Selection;
        if (selection == null)
        {
            Cancel();
            return;
        }

        if (wasMarquee)
        {
            var hits = _owner.CollectOccupiedCuesInRect(BuildMarqueeRect());
            var final = BuildFinalSelection(hits);
            selection.SetSelection(
                final,
                focusCue: final.Count > 0 ? final[^1] : null,
                recordHistory: !SelectionEqualsBaseline(final),
                description: "Box select cues",
                emitFocus: true);
            _owner.RefreshSelectionChrome();
        }
        else
        {
            CommitClick(selection);
        }

        _pending = false;
        _active = false;
        _originCell = null;
        UnwireScrollWatch();
        HideMarquee();
        _owner.SyncPointerProcessing();
    }

    private void CommitClick(ShellSelection selection)
    {
        bool originValid = _originCell != null && GodotObject.IsInstanceValid(_originCell);
        if (originValid && _originCell.IsOccupied)
        {
            var cue = CueList.FetchCueFromId(_originCell.CueId);
            if (cue == null)
                return;
            if (_additive)
                selection.ToggleSelection(cue);
            else
                selection.SelectIndividualShell(cue);
            _owner.RefreshSelectionChrome();
            return;
        }

        if (originValid && !_originCell.IsOccupied)
        {
            if (!_additive)
                _owner.TryCreateCueFromEmptyClick(_originCell.Row, _originCell.Col);
            return;
        }

        if (!_additive)
        {
            selection.ClearSelection();
            _owner.RefreshSelectionChrome();
        }
    }

    private void RestoreBaselineAndAbort()
    {
        var selection = _owner.Selection;
        if (selection != null && _active)
        {
            selection.SetSelection(
                _baselineSelection,
                focusCue: CueList.FetchCueFromId(_baselineFocusId),
                recordHistory: false,
                description: null,
                emitFocus: true);
            _owner.RefreshSelectionChrome();
        }

        Cancel();
    }

    private void ApplyLiveSelection()
    {
        var selection = _owner.Selection;
        if (selection == null)
            return;

        var hits = _owner.CollectOccupiedCuesInRect(BuildMarqueeRect());
        var final = BuildFinalSelection(hits);
        int sig = SelectionSignature(final);
        if (sig == _lastLiveSelectionSig)
            return;
        _lastLiveSelectionSig = sig;
        selection.SetSelection(
            final,
            focusCue: final.Count > 0 ? final[^1] : null,
            recordHistory: false,
            description: null,
            emitFocus: false);
        _owner.RefreshSelectionChrome();
    }

    private static int SelectionSignature(List<Cue> cues)
    {
        int hash = cues.Count;
        foreach (var cue in cues)
            hash = HashCode.Combine(hash, cue?.Id ?? -1);
        return hash;
    }

    private List<Cue> BuildFinalSelection(List<Cue> hits)
    {
        if (!_additive)
            return hits;

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

    private Vector2 GetContentPinnedStartGlobal()
    {
        float dx = GetScrollX() - _startScrollX;
        float dy = GetScrollY() - _startScrollY;
        return new Vector2(_startGlobal.X - dx, _startGlobal.Y - dy);
    }

    private float GetScrollX()
    {
        if (_scroll == null || !GodotObject.IsInstanceValid(_scroll))
            return 0f;
        return _scroll.ScrollHorizontal;
    }

    private float GetScrollY()
    {
        if (_scroll == null || !GodotObject.IsInstanceValid(_scroll))
            return 0f;
        return _scroll.ScrollVertical;
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
        if (_scroll != null && GodotObject.IsInstanceValid(_scroll))
            rect = rect.Intersection(_scroll.GetGlobalRect());

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
        if (_scrollWired || _scroll == null || !GodotObject.IsInstanceValid(_scroll))
            return;
        var vBar = _scroll.GetVScrollBar();
        var hBar = _scroll.GetHScrollBar();
        if (vBar != null)
            vBar.ValueChanged += OnScrollChanged;
        if (hBar != null)
            hBar.ValueChanged += OnScrollChanged;
        _scrollWired = true;
    }

    private void UnwireScrollWatch()
    {
        if (!_scrollWired || _scroll == null || !GodotObject.IsInstanceValid(_scroll))
        {
            _scrollWired = false;
            return;
        }

        var vBar = _scroll.GetVScrollBar();
        var hBar = _scroll.GetHScrollBar();
        if (vBar != null)
            vBar.ValueChanged -= OnScrollChanged;
        if (hBar != null)
            hBar.ValueChanged -= OnScrollChanged;
        _scrollWired = false;
    }

    private void OnScrollChanged(double value)
    {
        if (!_active)
            return;
        RefreshMarqueeAndSelection();
    }

    private void HideMarquee()
    {
        if (_marqueePanel != null && GodotObject.IsInstanceValid(_marqueePanel))
            _marqueePanel.Visible = false;
    }
}
