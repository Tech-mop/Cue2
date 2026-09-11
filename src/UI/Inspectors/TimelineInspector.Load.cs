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
/// Partial: Load timeline, sidebar/bars, bar styling, duration helpers
/// </summary>
public partial class TimelineInspector
{
    /// <summary>
    /// Coalesces rapid LoadTimeline requests (focus + history + visibility) into one rebuild per frame.
    /// </summary>
    private void LoadTimeline()
    {
        if (_timelineLoadQueued)
            return;
        _timelineLoadQueued = true;
        Callable.From(LoadTimelineDeferred).CallDeferred();
    }

    private void LoadTimelineDeferred()
    {
        _timelineLoadQueued = false;
        LoadTimelineNow();
    }

    /// <summary>
    /// Rebuilds timeline visuals, or only refreshes geometry when the cue tree structure is unchanged (P2-08).
    /// </summary>
    private void LoadTimelineNow()
    {
        if (!Visible || _timeLineContainer == null) return;

        if (_focusedCue == null)
        {
            ShowNoSelection();
            return;
        }

        if (_infoLabel != null)
            _infoLabel.Visible = false;
        if (!_timeLineContainer.Visible)
            _timeLineContainer.Visible = true;

        var nextItems = new List<TimelineItem>();
        int row = 0;
        CollectCues(_focusedCue, nextItems, ref row);
        bool showWaveforms = _globalData?.Settings?.ShowTimelineWaveforms ?? true;
        string structureKey = BuildTimelineStructureKey(nextItems, showWaveforms);

        // Same hierarchy / collapse / waveform mode — avoid QueueFree + recreate (P2-08).
        if (structureKey == _timelineStructureKey
            && _cueToBar.Count > 0
            && _visibleItems.Count == nextItems.Count)
        {
            _visibleItems.Clear();
            _visibleItems.AddRange(nextItems);
            // Refresh row map for geometry (CollectCues may reorder rows after expand).
            _cueToRow.Clear();
            foreach (var item in _visibleItems)
                _cueToRow[item.Cue] = item.Row;

            UpdateAllPositionsAndSizes();
            UpdatePlayheadLineGeometry();
            UpdateDurationSummary();
            return;
        }

        GD.Print("TimelineInspector:LoadTimeline - Full rebuild");
        int gen = ++_timelineLoadGeneration;
        _timelineStructureKey = structureKey;

        ClearTimelineVisuals();

        _visibleItems.Clear();
        _visibleItems.AddRange(nextItems);

        // Zebra row backgrounds in timeline content
        int maxRow = row;
        for (int i = 0; i < maxRow; i++)
        {
            var bg = new ColorRect
            {
                Color = (i % 2 == 0) ? GlobalStyles.ZebraOdd : GlobalStyles.ZebraEven,
                Position = new Vector2(0, i * RowHeight),
                Size = new Vector2(100, RowHeight),
                ZIndex = -1,
                MouseFilter = MouseFilterEnum.Ignore
            };
            _timelineArea.AddChild(bg);
            _rowBackgrounds.Add(bg);
        }

        // Ensure time grid is behind everything
        if (_timeGrid != null && IsInstanceValid(_timeGrid))
        {
            if (_timeGrid.GetParent() != _timelineArea)
                _timelineArea.AddChild(_timeGrid);
            _timelineArea.MoveChild(_timeGrid, 0);
            _timeGrid.ZoomScale = _scale;
            _timeGrid.Position = Vector2.Zero;
        }

        foreach (var item in _visibleItems)
        {
            CreateSidebarRow(item);
            CreatePreWaitGhost(item);
            CreateCueBar(item, showWaveforms);
        }

        EnsurePlayheadLine();
        // Keep playhead on top
        if (_playheadLine != null && IsInstanceValid(_playheadLine) && _playheadLine.GetParent() == _timelineArea)
            _timelineArea.MoveChild(_playheadLine, _timelineArea.GetChildCount() - 1);

        UpdateAllPositionsAndSizes();
        UpdatePlayheadLineGeometry();
        UpdateDurationSummary();

        if (showWaveforms && _mediaEngine != null)
            _ = EnsureWaveformsForItemsAsync(_visibleItems.ToList(), gen);
    }

    /// <summary>
    /// Fingerprint of visible hierarchy used to skip full rebuilds when only times/geometry change.
    /// </summary>
    private string BuildTimelineStructureKey(List<TimelineItem> items, bool showWaveforms)
    {
        // focused root + ordered (id,depth,hasChildren,collapsed) + waveform flag
        var sb = new System.Text.StringBuilder(64 + items.Count * 16);
        sb.Append(_focusedCue?.Id ?? -1).Append('|').Append(showWaveforms ? 'W' : 'n').Append('|');
        foreach (var item in items)
        {
            if (item.Cue == null) continue;
            sb.Append(item.Cue.Id).Append(':')
                .Append(item.Depth).Append(':')
                .Append(item.HasChildren ? '1' : '0').Append(':')
                .Append(_collapsedCueIds.Contains(item.Cue.Id) ? 'c' : 'o')
                .Append(';');
        }
        return sb.ToString();
    }

    private void ClearTimelineVisuals()
    {
        foreach (var bg in _rowBackgrounds)
        {
            if (bg != null && IsInstanceValid(bg))
                bg.QueueFree();
        }
        _rowBackgrounds.Clear();

        foreach (var bar in _cueToBar.Values)
        {
            if (bar != null && IsInstanceValid(bar))
                bar.QueueFree();
        }
        _cueToBar.Clear();
        _cueToRow.Clear();

        foreach (var ghost in _cueToPreWaitGhost.Values)
        {
            if (ghost != null && IsInstanceValid(ghost))
                ghost.QueueFree();
        }
        _cueToPreWaitGhost.Clear();

        foreach (var label in _cueToTimeLabel.Values)
        {
            if (label != null && IsInstanceValid(label))
                label.QueueFree();
        }
        _cueToTimeLabel.Clear();

        foreach (var label in _cueToDurationLabel.Values)
        {
            if (label != null && IsInstanceValid(label))
                label.QueueFree();
        }
        _cueToDurationLabel.Clear();

        foreach (var badge in _cueToLoopBadge.Values)
        {
            if (badge != null && IsInstanceValid(badge))
                badge.QueueFree();
        }
        _cueToLoopBadge.Clear();

        foreach (var btn in _cueToCollapseButton.Values)
        {
            if (btn != null && IsInstanceValid(btn))
                btn.QueueFree();
        }
        _cueToCollapseButton.Clear();

        foreach (var row in _cueToSidebarRow.Values)
        {
            if (row != null && IsInstanceValid(row))
                row.QueueFree();
        }
        _cueToSidebarRow.Clear();

        // Keep the playhead node parented under _timelineArea (do NOT RemoveChild without free —
        // that orphaned ColorRect "PlayheadLine" and leaked a CanvasItem RID on exit).
        // Hide until the next rebuild repositions it.
        if (_playheadLine != null && IsInstanceValid(_playheadLine))
            _playheadLine.Visible = false;

        // Keep time grid; just detach references that will be recreated
        _visibleItems.Clear();
        _timelineStructureKey = null;
    }

    private void CreateSidebarRow(TimelineItem item)
    {
        if (_sidebarContent == null) return;

        var cue = item.Cue;
        var row = new Control
        {
            Name = $"SidebarRow_{cue.Id}",
            MouseFilter = MouseFilterEnum.Stop,
            CustomMinimumSize = new Vector2(SidebarWidth, RowHeight),
            Size = new Vector2(SidebarWidth, RowHeight),
            Position = new Vector2(0, item.Row * RowHeight)
        };
        row.GuiInput += e => OnSidebarRowGuiInput(e, cue);

        var bg = new ColorRect
        {
            Name = "Bg",
            Color = (item.Row % 2 == 0) ? GlobalStyles.ZebraOdd : GlobalStyles.ZebraEven,
            MouseFilter = MouseFilterEnum.Ignore,
            ZIndex = -1
        };
        row.AddChild(bg);
        bg.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);

        float x = SidebarPadX + item.Depth * 10f;

        if (item.HasChildren)
        {
            bool collapsed = _collapsedCueIds.Contains(cue.Id);
            var btn = new Button
            {
                Name = $"Collapse_{cue.Id}",
                FocusMode = FocusModeEnum.None,
                Flat = true,
                CustomMinimumSize = new Vector2(CollapseBtnSize, CollapseBtnSize),
                Size = new Vector2(CollapseBtnSize, CollapseBtnSize),
                Position = new Vector2(x, (RowHeight - CollapseBtnSize) * 0.5f),
                TooltipText = collapsed ? "Expand children" : "Collapse children",
                MouseDefaultCursorShape = CursorShape.PointingHand,
                ZIndex = 2
            };
            try
            {
                btn.ThemeTypeVariation = "AtlasIcons";
                btn.Icon = GetThemeIcon(collapsed ? "Right" : "Down", "AtlasIcons");
                btn.ExpandIcon = true;
                btn.AddThemeConstantOverride("icon_max_width", 12);
            }
            catch
            {
                btn.Text = collapsed ? "▶" : "▼";
            }

            int cueId = cue.Id;
            btn.Pressed += () => OnCollapseToggled(cueId);
            row.AddChild(btn);
            _cueToCollapseButton[cue] = btn;
            x += CollapseBtnSize + 2f;
        }
        else
        {
            x += 4f; // slight indent alignment with chevron-less rows
        }

        // Color swatch
        Color swatchColor = cue.Color;
        if (IsNearBlack(swatchColor))
            swatchColor = GlobalStyles.LowColor2;
        var swatch = new ColorRect
        {
            Name = "Swatch",
            Color = swatchColor,
            Size = new Vector2(SwatchSize, SwatchSize),
            Position = new Vector2(x, (RowHeight - SwatchSize) * 0.5f),
            MouseFilter = MouseFilterEnum.Ignore
        };
        row.AddChild(swatch);
        x += SwatchSize + 4f;

        // Cue number
        var numLabel = new Label
        {
            Name = "CueNum",
            Text = cue.CueNum ?? string.Empty,
            Position = new Vector2(x, 4f),
            MouseFilter = MouseFilterEnum.Ignore,
            ClipText = true
        };
        numLabel.AddThemeFontSizeOverride("font_size", 11);
        numLabel.AddThemeColorOverride("font_color", new Color(0.7f, 0.75f, 0.78f, 1f));
        row.AddChild(numLabel);

        // Name (truncated)
        var nameLabel = new Label
        {
            Name = "CueName",
            Text = cue.Name ?? string.Empty,
            Position = new Vector2(x, 20f),
            MouseFilter = MouseFilterEnum.Ignore,
            ClipText = true,
            TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis
        };
        nameLabel.AddThemeFontSizeOverride("font_size", 11);
        nameLabel.AddThemeColorOverride("font_color", new Color(0.9f, 0.92f, 0.94f, 1f));
        row.AddChild(nameLabel);

        // Size labels to remaining width
        float remaining = Math.Max(20f, SidebarWidth - x - SidebarPadX);
        numLabel.Size = new Vector2(remaining, 16f);
        nameLabel.Size = new Vector2(remaining, 16f);

        row.TooltipText = $"{cue.CueNum} — {cue.Name}";

        _sidebarContent.AddChild(row);
        _cueToSidebarRow[cue] = row;
        _cueToRow[cue] = item.Row;
    }

    private void OnSidebarRowGuiInput(InputEvent @event, Cue cue)
    {
        if (@event is InputEventMouseButton mb
            && mb.ButtonIndex == MouseButton.Left
            && mb.Pressed
            && !mb.DoubleClick)
        {
            // Ignore if click is on collapse button region — button handles that itself.
            _followLivePlayhead = false;
            SetPlayheadSeconds(ComputeActionStart(cue));
            EnsurePlayheadVisible();
            GrabFocusSafe();
            GetViewport()?.SetInputAsHandled();
        }
    }

    private void OnCollapseToggled(int cueId)
    {
        if (!_collapsedCueIds.Add(cueId))
            _collapsedCueIds.Remove(cueId);
        LoadTimeline();
    }

    private void CreatePreWaitGhost(TimelineItem item)
    {
        var cue = item.Cue;
        if (cue.PreWait <= 1e-4) return;

        var ghost = new ColorRect
        {
            Name = $"PreWaitGhost_{cue.Id}",
            Color = new Color(0.5f, 0.55f, 0.6f, 0.12f),
            MouseFilter = MouseFilterEnum.Ignore,
            ZIndex = 0
        };
        _timelineArea.AddChild(ghost);
        _cueToPreWaitGhost[cue] = ghost;
    }

    private void CreateCueBar(TimelineItem item, bool showWaveforms)
    {
        var cue = item.Cue;
        var barColor = ResolveBarColor(cue);
        var accentColor = ResolveAccentColor(cue, barColor);

        var bar = new ColorRect
        {
            Color = barColor,
            MouseFilter = MouseFilterEnum.Stop,
            ClipContents = true,
            MouseDefaultCursorShape = CursorShape.Move,
            ZIndex = 2
        };
        bar.GuiInput += e => HandleBarInput(e, cue, bar);
        _timelineArea.AddChild(bar);

        if (showWaveforms && TryGetCueWaveformSource(cue, out var peaks, out float startNorm, out float endNorm, out int playCount))
            AttachWaveformLayer(bar, peaks, startNorm, endNorm, playCount);

        var startLine = new ColorRect
        {
            Name = "StartLine",
            Color = accentColor,
            Size = new Vector2(2, RowHeight - 6),
            Position = new Vector2(0, 0),
            MouseFilter = MouseFilterEnum.Ignore
        };
        bar.AddChild(startLine);

        var endLine = new ColorRect
        {
            Name = "EndLine",
            Color = accentColor.Darkened(0.15f),
            Size = new Vector2(2, RowHeight - 6),
            Position = new Vector2(0, 0),
            MouseFilter = MouseFilterEnum.Ignore
        };
        bar.AddChild(endLine);

        var flag = new ColorRect
        {
            Name = "Flag",
            Color = accentColor,
            Size = new Vector2(8, 8),
            Position = new Vector2(0, RowHeight - 16),
            MouseFilter = MouseFilterEnum.Stop,
            MouseDefaultCursorShape = CursorShape.Move
        };
        flag.GuiInput += e => HandleBarInput(e, cue, bar);
        bar.AddChild(flag);

        // Free-floating timing: line 1 = start + pre-wait, line 2 = length (below).
        double actionStart = ComputeActionStart(cue);
        var timeLabel = new Label
        {
            Name = $"TimeLabel_{cue.Id}",
            Text = FormatBarStartPreLabel(cue, actionStart),
            MouseFilter = MouseFilterEnum.Ignore,
            ZIndex = 5
        };
        StyleBarTextLabel(timeLabel, new Color(0.92f, 0.94f, 0.96f, 0.95f));
        _timelineArea.AddChild(timeLabel);

        var durationLabel = new Label
        {
            Name = $"DurationLabel_{cue.Id}",
            Text = FormatBarLengthLabel(cue),
            MouseFilter = MouseFilterEnum.Ignore,
            ZIndex = 5
        };
        StyleBarTextLabel(durationLabel, new Color(0.78f, 0.82f, 0.86f, 0.95f));
        _timelineArea.AddChild(durationLabel);

        // Infinite: own media loop vs child-driven infinite (different badge wording).
        if (IsInfiniteLoopCue(cue))
        {
            bool childLoop = IsChildDrivenInfinite(cue);
            var loopBadge = new Label
            {
                Name = $"LoopBadge_{cue.Id}",
                Text = FormatLoopBadgeText(cue),
                MouseFilter = MouseFilterEnum.Ignore,
                ZIndex = 6,
                TooltipText = childLoop
                    ? "A nested child cue loops indefinitely"
                    : "This cue's media loops indefinitely"
            };
            StyleBarTextLabel(loopBadge, GlobalStyles.HighColor1.Lightened(0.15f));
            _timelineArea.AddChild(loopBadge);
            _cueToLoopBadge[cue] = loopBadge;
        }

        _cueToBar[cue] = bar;
        _cueToRow[cue] = item.Row;
        _cueToTimeLabel[cue] = timeLabel;
        _cueToDurationLabel[cue] = durationLabel;
    }

    private static void StyleBarTextLabel(Label label, Color fontColor)
    {
        if (label == null) return;
        label.AddThemeFontSizeOverride("font_size", 10);
        label.AddThemeColorOverride("font_color", fontColor);
        label.AddThemeColorOverride("font_shadow_color", new Color(0, 0, 0, 0.85f));
        label.AddThemeConstantOverride("shadow_offset_x", 1);
        label.AddThemeConstantOverride("shadow_offset_y", 1);
    }

    private static Color ResolveBarColor(Cue cue)
    {
        if (cue == null) return GlobalStyles.LowColor5;
        Color c = cue.Color;
        if (IsNearBlack(c))
            return GlobalStyles.LowColor5;

        // Blend cue color with LowColor palette for a professional muted bar
        return GlobalStyles.LowColor4.Lerp(c, 0.55f).Darkened(0.1f) with { A = 0.92f };
    }

    private static Color ResolveAccentColor(Cue cue, Color barColor)
    {
        if (cue != null && !IsNearBlack(cue.Color))
            return cue.Color.Lightened(0.15f);
        return GlobalStyles.HighColor3;
    }

    private static bool IsNearBlack(Color c)
    {
        return c.R < 0.04f && c.G < 0.04f && c.B < 0.04f;
    }

    /// <summary>Line 1: absolute start and pre-wait.</summary>
    private static string FormatBarStartPreLabel(Cue cue, double actionStart)
    {
        if (cue == null) return string.Empty;
        string startStr = UiUtilities.FormatTime(actionStart);
        string preStr = UiUtilities.FormatTime(Math.Max(0, cue.PreWait));
        return $"{startStr}  (pre {preStr})";
    }

    /// <summary>Line 2: content length (or loop / child-loop notation).</summary>
    private static string FormatBarLengthLabel(Cue cue)
    {
        if (cue == null) return string.Empty;
        if (IsChildDrivenInfinite(cue))
            return "∞  Child Looping";

        if (HasSelfInfiniteContent(cue))
        {
            double cycle = GetSingleCycleDurationSeconds(cue);
            return cycle > 1e-4 ? $"len {UiUtilities.FormatTime(cycle)}  ↻" : "len ∞";
        }

        // Duration < 0 but we couldn't classify — still show infinite.
        if (IsInfiniteLoopCue(cue))
            return "len ∞";

        double len = Math.Max(0, cue.Duration);
        if (len < 1e-4) return "len 0s";
        if (len < 60) return $"len {len:0.##}s";
        return $"len {UiUtilities.FormatTime(len)}";
    }

    /// <summary>Badge text after the bar for infinite cues.</summary>
    private static string FormatLoopBadgeText(Cue cue)
    {
        if (IsChildDrivenInfinite(cue))
            return "∞ Child Looping";
        return "↻ LOOP";
    }

    /// <summary>True when cue content duration is infinite (own loop or child loop).</summary>
    private static bool IsInfiniteLoopCue(Cue cue) => cue != null && cue.Duration < 0;

    /// <summary>
    /// True when this cue itself has looping / infinite media (not only via a child).
    /// </summary>
    private static bool HasSelfInfiniteContent(Cue cue)
    {
        if (cue == null) return false;

        var audio = cue.GetAudioComponent();
        if (audio != null && audio.Loop)
            return true;

        var video = cue.GetVideoComponent();
        if (video != null)
        {
            if (video.Loop)
                return true;
            // Still image held until stopped is infinite on this cue.
            if (video.IsImage && video.Duration <= 0)
                return true;
        }

        var text = cue.GetTextComponent();
        if (text != null)
        {
            // Duration 0 / TotalDuration < 0 = hold until stopped.
            if (text.TotalDuration < 0 || text.Duration <= 0)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Infinite shell duration driven by a nested child loop (this cue has no self-infinite media).
    /// </summary>
    private static bool IsChildDrivenInfinite(Cue cue) =>
        IsInfiniteLoopCue(cue) && !HasSelfInfiniteContent(cue);

    /// <summary>
    /// One playback segment length for display (not × playcount / not infinite span).
    /// For looping cues this is a single cycle; for finite cues the full content duration
    /// (includes nested children when they extend the parent).
    /// </summary>
    private static double GetBarDisplayDurationSeconds(Cue cue)
    {
        if (cue == null) return 0;
        if (!IsInfiniteLoopCue(cue))
            return Math.Max(0, cue.Duration);
        return GetSingleCycleDurationSeconds(cue);
    }

    /// <summary>
    /// Duration of this cue's own audio/video content only (excludes nested children).
    /// Used to size the waveform so child-extended parent bars do not stretch peaks.
    /// </summary>
    /// <returns>
    /// Linear seconds of own media (including play-count for finite audio/video),
    /// <c>-1</c> if own media loops indefinitely, or <c>0</c> if no media.
    /// </returns>
    private static double GetCueOwnMediaDurationSeconds(Cue cue)
    {
        if (cue == null) return 0;

        double contents = 0;
        bool hasMedia = false;

        var audio = cue.GetAudioComponent();
        if (audio != null)
        {
            hasMedia = true;
            if (audio.Loop)
                return -1;
            // Prefer TotalDuration (segment × play count); fall back to Duration × PlayCount.
            double audioDur = audio.TotalDuration;
            if (audioDur <= 1e-9 && audio.Duration > 0)
                audioDur = audio.Duration * Math.Max(1, audio.PlayCount);
            if (audioDur > contents)
                contents = audioDur;
        }

        var video = cue.GetVideoComponent();
        if (video != null)
        {
            hasMedia = true;
            if (video.Loop || video.TotalDuration < 0)
                return -1;
            double videoDur = video.TotalDuration;
            if (videoDur <= 1e-9 && video.Duration > 0)
                videoDur = video.Duration * Math.Max(1, video.PlayCount);
            if (videoDur > contents)
                contents = videoDur;
        }

        if (!hasMedia)
            return 0;
        return Math.Max(0, contents);
    }

    /// <summary>
    /// Pixel width for the in-bar waveform control at the current zoom.
    /// Matches own-media time scale; clamped to the bar so it never overflows.
    /// </summary>
    /// <param name="cue">Cue that owns the media waveform.</param>
    /// <param name="barDisplayWidth">Full bar width in pixels (may include child extension).</param>
    /// <returns>Width in pixels for the waveform layer (0 if no drawable media span).</returns>
    private float ComputeWaveformDisplayWidth(Cue cue, float barDisplayWidth)
    {
        if (cue == null || barDisplayWidth <= 0.5f || _scale <= 1e-6f)
            return 0f;

        double mediaDur = GetCueOwnMediaDurationSeconds(cue);
        // Own infinite loop: one display cycle (matches forced PlayCount = 1 on the wave).
        if (mediaDur < 0)
            mediaDur = GetSingleCycleDurationSeconds(cue);

        if (mediaDur <= 1e-9)
            return 0f;

        float waveW = (float)(mediaDur * _scale);
        return Mathf.Clamp(waveW, 0f, barDisplayWidth);
    }

    /// <summary>
    /// Best-effort single media/content cycle length for looping cues.
    /// </summary>
    private static double GetSingleCycleDurationSeconds(Cue cue)
    {
        if (cue == null) return InfiniteLoopDisplaySeconds;

        double cycle = 0;
        var audio = cue.GetAudioComponent();
        if (audio != null && audio.Duration > 0)
            cycle = Math.Max(cycle, audio.Duration);

        var video = cue.GetVideoComponent();
        if (video != null && video.Duration > 0)
            cycle = Math.Max(cycle, video.Duration);

        var text = cue.GetTextComponent();
        if (text != null && text.Duration > 0)
            cycle = Math.Max(cycle, text.Duration);

        // Nested groups: longest finite child cycle as a stand-in
        if (cycle <= 1e-9 && cue.ChildCues != null)
        {
            foreach (var childId in cue.ChildCues)
            {
                var child = CueList.FetchCueFromId(childId);
                if (child == null) continue;
                double childCycle = child.Duration >= 0
                    ? child.Duration
                    : GetSingleCycleDurationSeconds(child);
                cycle = Math.Max(cycle, childCycle);
            }
        }

        return cycle > 1e-9 ? cycle : InfiniteLoopDisplaySeconds;
    }

}
