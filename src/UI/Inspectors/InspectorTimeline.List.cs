// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using Cue2.Domain.Cues;
using Cue2.Services;
using Cue2.UI.Utilities;
using Godot;

namespace Cue2.UI.Inspectors;

/// <summary>
/// Partial: numbered node list under the waveform (delete, time, vol, opacity, rate, pitch, interp, continue, loop, n).
/// </summary>
public partial class InspectorTimeline
{
    private VBoxContainer _nodeList;
    private Label _nodeHint;
    private HBoxContainer _nodeHeader;
    private HBoxContainer _addRow;
    private Button _addButton;
    private readonly Dictionary<int, HBoxContainer> _rows = new();
    private readonly Dictionary<int, Label> _numberLabels = new();
    private readonly Dictionary<int, LineEdit> _timeEdits = new();
    private readonly Dictionary<int, LineEdit> _volumeEdits = new();
    private readonly Dictionary<int, LineEdit> _opacityEdits = new();
    private readonly Dictionary<int, LineEdit> _rateEdits = new();
    private readonly Dictionary<int, LineEdit> _pitchEdits = new();
    private readonly Dictionary<int, OptionButton> _interpOptions = new();
    private readonly Dictionary<int, CheckBox> _continueChecks = new();
    private readonly Dictionary<int, CheckBox> _loopChecks = new();
    private readonly Dictionary<int, LineEdit> _playCountEdits = new();
    private List<int> _listedIds = new();

    private void BuildNodeListHost()
    {
        if (_accordion == null)
            return;

        var margin = new MarginContainer { Name = "TimelineNodeListMargin" };
        margin.AddThemeConstantOverride("margin_left", 20);
        margin.AddThemeConstantOverride("margin_right", 20);
        margin.AddThemeConstantOverride("margin_top", 4);
        _accordion.AddChild(margin);

        _nodeList = new VBoxContainer { Name = "TimelineNodeList" };
        _nodeList.AddThemeConstantOverride("separation", 2);
        margin.AddChild(_nodeList);

        _nodeHint = new Label
        {
            Name = "TimelineNodeHint",
            Text = NodeHintText(),
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        _nodeHint.AddThemeFontSizeOverride("font_size", 10);
        _nodeHint.AddThemeColorOverride("font_color", GlobalStyles.SoftFontColor);
        _nodeList.AddChild(_nodeHint);

        _nodeHeader = CreateHeader();
        _nodeList.AddChild(_nodeHeader);

        _addRow = new HBoxContainer { Name = "TimelineAddRow" };
        _addRow.AddThemeConstantOverride("separation", 8);

        _addButton = new Button
        {
            Name = "TimelineAddNodeButton",
            Text = UiLocalizer.T("Add node"),
            FocusMode = Control.FocusModeEnum.None,
            SizeFlagsHorizontal = Control.SizeFlags.ShrinkBegin
        };
        _addButton.AddThemeFontSizeOverride("font_size", 10);
        _addButton.TooltipText = UiLocalizer.T("Add a node at the midpoint of the longest gap");
        _addButton.Pressed += OnAddNodePressed;
        _addRow.AddChild(_addButton);
        _nodeList.AddChild(_addRow);
    }

    private HBoxContainer CreateHeader()
    {
        var header = new HBoxContainer
        {
            Name = "TimelineNodeHeader",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            Visible = false
        };
        header.AddThemeConstantOverride("separation", 6);
        header.AddChild(new Control { CustomMinimumSize = new Vector2(NodeDeleteWidth, 0) });
        header.AddChild(MakeHeaderLabel("#", 28, HorizontalAlignment.Right));
        header.AddChild(MakeHeaderLabel("Time", NodeTimeWidth));
        var vol = MakeHeaderLabel("Vol", 56, HorizontalAlignment.Center);
        vol.Name = "VolHeader";
        vol.Visible = _showAudioAutomation;
        header.AddChild(vol);
        var opacity = MakeHeaderLabel("Op", 48, HorizontalAlignment.Center);
        opacity.Name = "OpacityHeader";
        opacity.Visible = _showOpacityAutomation;
        opacity.TooltipText = UiLocalizer.T("Opacity relative to the component");
        header.AddChild(opacity);
        header.AddChild(MakeHeaderLabel("Rate", 48, HorizontalAlignment.Center));
        var pitch = MakeHeaderLabel("Pitch", 48, HorizontalAlignment.Center);
        pitch.Name = "PitchHeader";
        pitch.Visible = _showAudioAutomation;
        header.AddChild(pitch);
        header.AddChild(MakeHeaderLabel("Interp", 78, HorizontalAlignment.Center));
        header.AddChild(MakeHeaderLabel("Continue", NodeContinueWidth, HorizontalAlignment.Center));
        header.AddChild(MakeHeaderLabel("Loop", NodeLoopWidth, HorizontalAlignment.Center));
        header.AddChild(MakeHeaderLabel("n", NodePlayCountWidth, HorizontalAlignment.Center));
        return header;
    }

    private void RebuildHeader()
    {
        if (_nodeHeader == null || !GodotObject.IsInstanceValid(_nodeHeader))
            return;
        int insert = _nodeHeader.GetIndex();
        _nodeHeader.QueueFree();
        _nodeHeader = CreateHeader();
        _nodeList.AddChild(_nodeHeader);
        _nodeList.MoveChild(_nodeHeader, insert);
        foreach (var id in new List<int>(_rows.Keys))
            ForgetRow(id);
        _listedIds.Clear();
    }

    private static Label MakeHeaderLabel(string text, float minWidth, HorizontalAlignment align = HorizontalAlignment.Left)
    {
        var label = new Label
        {
            Text = UiLocalizer.T(text),
            CustomMinimumSize = new Vector2(minWidth, 0),
            HorizontalAlignment = align,
            VerticalAlignment = VerticalAlignment.Center
        };
        label.AddThemeFontSizeOverride("font_size", 10);
        label.AddThemeColorOverride("font_color", GlobalStyles.SoftFontColor);
        return label;
    }

    private Control MakeDeleteControl(int nodeId, bool isFileEnd)
    {
        if (isFileEnd)
        {
            return new Control { CustomMinimumSize = new Vector2(NodeDeleteWidth, 0) };
        }

        var button = new Button
        {
            CustomMinimumSize = new Vector2(NodeDeleteWidth, 0),
            FocusMode = Control.FocusModeEnum.None,
            Flat = true,
            TooltipText = UiLocalizer.T("Delete node")
        };
        try
        {
            button.Icon = _accordion?.GetThemeIcon("DeleteBin", "AtlasIcons");
            button.ExpandIcon = true;
            button.AddThemeConstantOverride("icon_max_width", 12);
        }
        catch
        {
            button.Text = "×";
        }

        button.Pressed += () => RemoveNodeAt(nodeId);
        return button;
    }

    private static void FillInterpOption(OptionButton button, TimelineVolumeInterpolation selected)
    {
        button.Clear();
        button.AddItem(UiLocalizer.T("Linear"), (int)TimelineVolumeInterpolation.Linear);
        button.AddItem(UiLocalizer.T("Snap"), (int)TimelineVolumeInterpolation.Snap);
        button.AddItem(UiLocalizer.T("Bezier"), (int)TimelineVolumeInterpolation.Bezier);
        int idx = button.GetItemIndex((int)selected);
        button.Selected = idx >= 0 ? idx : 0;
    }

    private void SyncAddRowState()
    {
        if (_addButton == null)
            return;
        int user = _timeline?.CountUserNodes() ?? 0;
        _addButton.Disabled = _timeline == null || user >= MediaTimeline.MaxUserNodes;
    }

    private string NodeHintText() =>
        _showAudioAutomation
            ? UiLocalizer.T("Click the ruler to set the playhead. Click the waveform to add a node.")
            : UiLocalizer.T("Click the ruler to set the playhead. Click the timeline to add a node.");

    private void RefreshListChrome()
    {
        if (_nodeHint != null)
            _nodeHint.Text = NodeHintText();
        if (_addButton != null)
        {
            _addButton.Text = UiLocalizer.T("Add node");
            _addButton.TooltipText = UiLocalizer.T("Add a node at the midpoint of the longest gap");
        }

        RebuildHeader();
    }

    private void SyncNodeList(List<AudioTimelineNode> ordered)
    {
        var ids = new List<int>(ordered.Count);
        foreach (var node in ordered)
            ids.Add(node.Id);

        bool structureChanged = ids.Count != _listedIds.Count;
        if (!structureChanged)
        {
            for (int i = 0; i < ids.Count; i++)
            {
                if (ids[i] != _listedIds[i])
                {
                    structureChanged = true;
                    break;
                }
            }
        }

        if (structureChanged && _draggingNodeId < 0)
        {
            if (_nodeHint != null)
                _nodeHint.Visible = (_timeline?.CountUserNodes() ?? 0) == 0;
            SyncAddRowState();
            int gen = ++_listRebuildGen;
            CallDeferred(nameof(DeferredRebuildList), gen);
        }
        else
            RefreshListTimes(ordered);
    }

    private void DeferredRebuildList(int gen)
    {
        if (gen != _listRebuildGen || !IsInsideTree())
            return;
        var ordered = _timeline?.GetNodesInTimeOrder() ?? new List<AudioTimelineNode>();
        RebuildList(ordered);
    }

    private void RebuildList(List<AudioTimelineNode> ordered)
    {
        if (_nodeList == null)
            return;

        var keep = new HashSet<int>(ordered.Count);
        foreach (var node in ordered)
            keep.Add(node.Id);

        if (_rows.Count > 0)
        {
            var remove = new List<int>();
            foreach (var id in _rows.Keys)
            {
                if (!keep.Contains(id))
                    remove.Add(id);
            }

            foreach (int id in remove)
                ForgetRow(id);
        }

        _listedIds = new List<int>(ordered.Count);
        int insertAt = _nodeHeader != null && GodotObject.IsInstanceValid(_nodeHeader)
            ? _nodeHeader.GetIndex() + 1
            : (_nodeHint != null && GodotObject.IsInstanceValid(_nodeHint)
                ? _nodeHint.GetIndex() + 1
                : 0);

        for (int i = 0; i < ordered.Count; i++)
        {
            var node = ordered[i];
            _listedIds.Add(node.Id);
            if (!_rows.TryGetValue(node.Id, out var row)
                || row == null || !GodotObject.IsInstanceValid(row))
            {
                row = CreateRow(node, i + 1);
                _nodeList.AddChild(row);
                _rows[node.Id] = row;
            }
            else if (_numberLabels.TryGetValue(node.Id, out var num)
                     && num != null && GodotObject.IsInstanceValid(num))
            {
                num.Text = node.IsFileEnd ? UiLocalizer.T("EOF") : (i + 1).ToString();
            }

            int desired = insertAt + i;
            if (row.GetIndex() != desired)
                _nodeList.MoveChild(row, desired);
        }

        if (_nodeHint != null)
        {
            int userCount = _timeline?.CountUserNodes() ?? 0;
            _nodeHint.Visible = userCount == 0;
            _nodeHint.Text = NodeHintText();
        }

        if (_nodeHeader != null)
            _nodeHeader.Visible = ordered.Count > 0;
        SyncAddRowState();

        if (_addRow != null && GodotObject.IsInstanceValid(_addRow))
            _nodeList.MoveChild(_addRow, _nodeList.GetChildCount() - 1);

        RefreshListTimes(ordered);
    }

    private void ForgetRow(int id)
    {
        if (_rows.TryGetValue(id, out var row))
        {
            if (row != null && GodotObject.IsInstanceValid(row))
                row.QueueFree();
            _rows.Remove(id);
        }

        _numberLabels.Remove(id);
        _timeEdits.Remove(id);
        _volumeEdits.Remove(id);
        _opacityEdits.Remove(id);
        _rateEdits.Remove(id);
        _pitchEdits.Remove(id);
        _interpOptions.Remove(id);
        _continueChecks.Remove(id);
        _loopChecks.Remove(id);
        _playCountEdits.Remove(id);
    }

    private HBoxContainer CreateRow(AudioTimelineNode node, int number)
    {
        int capturedId = node.Id;
        var row = new HBoxContainer
        {
            Name = $"TimelineNodeRow_{capturedId}",
            SizeFlagsHorizontal = Control.SizeFlags.Fill
        };
        row.AddThemeConstantOverride("separation", 6);
        row.AddChild(MakeDeleteControl(capturedId, node.IsFileEnd));

        var num = new Label
        {
            Text = node.IsFileEnd ? UiLocalizer.T("EOF") : number.ToString(),
            CustomMinimumSize = new Vector2(28, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            MouseFilter = Control.MouseFilterEnum.Stop
        };
        num.AddThemeFontSizeOverride("font_size", 10);
        num.AddThemeColorOverride("font_color", GlobalStyles.SoftFontColor);
        num.GuiInput += @event =>
        {
            if (@event is InputEventMouseButton mb && mb.Pressed && mb.ButtonIndex == MouseButton.Left)
            {
                _selectedNodeId = capturedId;
                Sync();
                num.AcceptEvent();
            }
        };
        _numberLabels[capturedId] = num;
        row.AddChild(num);

        var time = new LineEdit
        {
            Text = UiUtilities.FormatTime(node.TimeSeconds),
            CustomMinimumSize = new Vector2(NodeTimeWidth, 0),
            SizeFlagsHorizontal = Control.SizeFlags.ShrinkBegin,
            Alignment = HorizontalAlignment.Center
        };
        time.AddThemeFontSizeOverride("font_size", 10);
        time.Editable = !node.IsFileEnd;
        time.TooltipText = node.IsFileEnd
            ? UiLocalizer.T("End of file")
            : UiLocalizer.T("End of this node's region");
        if (!node.IsFileEnd)
        {
            time.TextSubmitted += text => OnTimeSubmitted(capturedId, text, time);
            time.FocusExited += () =>
            {
                if (GodotObject.IsInstanceValid(time))
                    OnTimeSubmitted(capturedId, time.Text, time);
            };
        }

        _timeEdits[capturedId] = time;
        row.AddChild(time);

        var vol = new LineEdit
        {
            Text = UiUtilities.FormatComponentVolumeDb(node.VolumeLinear),
            CustomMinimumSize = new Vector2(56, 0),
            Alignment = HorizontalAlignment.Center,
            Visible = _showAudioAutomation
        };
        vol.AddThemeFontSizeOverride("font_size", 10);
        vol.TooltipText = UiLocalizer.T("Volume relative to the component");
        vol.TextSubmitted += text => OnVolumeSubmitted(capturedId, text, vol);
        vol.FocusExited += () =>
        {
            if (GodotObject.IsInstanceValid(vol))
                OnVolumeSubmitted(capturedId, vol.Text, vol);
        };
        LineEditDbDragSlider.Enable(vol, new LineEditDbDragSlider.Config
        {
            MinDb = LineEditDbDragSlider.DefaultMinDb,
            MaxDb = LineEditDbDragSlider.DefaultMaxDb,
            FormatSigned = true,
            ValueChanged = db => OnVolumeScrubbed(capturedId, db)
        });
        _volumeEdits[capturedId] = vol;
        row.AddChild(vol);

        var opacity = new LineEdit
        {
            Text = FormatOpacityPercent(node.OpacityScale),
            CustomMinimumSize = new Vector2(48, 0),
            Alignment = HorizontalAlignment.Center,
            Visible = _showOpacityAutomation
        };
        opacity.AddThemeFontSizeOverride("font_size", 10);
        opacity.TooltipText = UiLocalizer.T("Opacity relative to the component");
        opacity.TextSubmitted += text => OnOpacitySubmitted(capturedId, text, opacity);
        opacity.FocusExited += () =>
        {
            if (GodotObject.IsInstanceValid(opacity))
                OnOpacitySubmitted(capturedId, opacity.Text, opacity);
        };
        _opacityEdits[capturedId] = opacity;
        row.AddChild(opacity);

        var rate = new LineEdit
        {
            Text = InspectorRatePitch.FormatRate(node.RateScale),
            CustomMinimumSize = new Vector2(48, 0),
            Alignment = HorizontalAlignment.Center
        };
        rate.AddThemeFontSizeOverride("font_size", 10);
        rate.TooltipText = UiLocalizer.T("Play rate relative to the component");
        rate.TextSubmitted += text => OnRateSubmitted(capturedId, text, rate);
        rate.FocusExited += () =>
        {
            if (GodotObject.IsInstanceValid(rate))
                OnRateSubmitted(capturedId, rate.Text, rate);
        };
        _rateEdits[capturedId] = rate;
        row.AddChild(rate);

        var pitch = new LineEdit
        {
            Text = InspectorRatePitch.FormatCents(node.PitchCents),
            CustomMinimumSize = new Vector2(48, 0),
            Alignment = HorizontalAlignment.Center,
            Visible = _showAudioAutomation
        };
        pitch.AddThemeFontSizeOverride("font_size", 10);
        pitch.TooltipText = UiLocalizer.T("Pitch offset in cents relative to the component");
        pitch.TextSubmitted += text => OnPitchSubmitted(capturedId, text, pitch);
        pitch.FocusExited += () =>
        {
            if (GodotObject.IsInstanceValid(pitch))
                OnPitchSubmitted(capturedId, pitch.Text, pitch);
        };
        _pitchEdits[capturedId] = pitch;
        row.AddChild(pitch);

        var interp = new OptionButton
        {
            FocusMode = Control.FocusModeEnum.None,
            CustomMinimumSize = new Vector2(78, 0),
            SizeFlagsHorizontal = Control.SizeFlags.ShrinkBegin,
            FitToLongestItem = false
        };
        interp.AddThemeFontSizeOverride("font_size", 10);
        FillInterpOption(interp, node.VolumeInterpolation);
        interp.ItemSelected += index => OnInterpSelected(capturedId, index);
        _interpOptions[capturedId] = interp;
        row.AddChild(interp);

        var continueBox = new CheckBox
        {
            ButtonPressed = node.ContinueRegion,
            FocusMode = Control.FocusModeEnum.None,
            CustomMinimumSize = new Vector2(NodeContinueWidth, 0)
        };
        continueBox.TooltipText = UiLocalizer.T(
            "Continue through this node without using it as a loop bound");
        continueBox.Toggled += state => OnContinueToggled(capturedId, state);
        _continueChecks[capturedId] = continueBox;
        row.AddChild(continueBox);

        var loop = new CheckBox
        {
            ButtonPressed = node.Loop,
            FocusMode = Control.FocusModeEnum.None,
            CustomMinimumSize = new Vector2(NodeLoopWidth, 0),
            Visible = !node.ContinueRegion
        };
        loop.TooltipText = UiLocalizer.T("Loop the region before this node");
        loop.Toggled += state => OnLoopToggled(capturedId, state);
        _loopChecks[capturedId] = loop;
        row.AddChild(loop);

        var count = new LineEdit
        {
            Text = node.PlayCount.ToString(),
            CustomMinimumSize = new Vector2(NodePlayCountWidth, 0),
            Alignment = HorizontalAlignment.Center,
            Editable = !node.Loop,
            Visible = !node.ContinueRegion
        };
        count.AddThemeFontSizeOverride("font_size", 10);
        count.TooltipText = UiLocalizer.T("Plays of the region before this node");
        count.TextSubmitted += text => OnPlayCountSubmitted(capturedId, text, count);
        count.FocusExited += () =>
        {
            if (GodotObject.IsInstanceValid(count))
                OnPlayCountSubmitted(capturedId, count.Text, count);
        };
        _playCountEdits[capturedId] = count;
        row.AddChild(count);

        return row;
    }

    private void RefreshListTimes(List<AudioTimelineNode> ordered)
    {
        if (_nodeHint != null)
            _nodeHint.Visible = (_timeline?.CountUserNodes() ?? 0) == 0;
        if (_nodeHeader != null)
            _nodeHeader.Visible = ordered.Count > 0;
        SyncAddRowState();

        foreach (var node in ordered)
        {
            if (_timeEdits.TryGetValue(node.Id, out var time)
                && time != null && GodotObject.IsInstanceValid(time) && !time.HasFocus())
                time.Text = UiUtilities.FormatTime(node.TimeSeconds);

            if (_volumeEdits.TryGetValue(node.Id, out var vol)
                && vol != null && GodotObject.IsInstanceValid(vol))
            {
                vol.Visible = _showAudioAutomation;
                if (!vol.HasFocus())
                    vol.Text = UiUtilities.FormatComponentVolumeDb(node.VolumeLinear);
            }

            if (_opacityEdits.TryGetValue(node.Id, out var opacity)
                && opacity != null && GodotObject.IsInstanceValid(opacity))
            {
                opacity.Visible = _showOpacityAutomation;
                if (!opacity.HasFocus())
                    opacity.Text = FormatOpacityPercent(node.OpacityScale);
            }

            if (_rateEdits.TryGetValue(node.Id, out var rate)
                && rate != null && GodotObject.IsInstanceValid(rate) && !rate.HasFocus())
                rate.Text = InspectorRatePitch.FormatRate(node.RateScale);

            if (_pitchEdits.TryGetValue(node.Id, out var pitch)
                && pitch != null && GodotObject.IsInstanceValid(pitch))
            {
                pitch.Visible = _showAudioAutomation;
                if (!pitch.HasFocus())
                    pitch.Text = InspectorRatePitch.FormatCents(node.PitchCents);
            }

            if (_interpOptions.TryGetValue(node.Id, out var interp)
                && interp != null && GodotObject.IsInstanceValid(interp))
            {
                int idx = interp.GetItemIndex((int)node.VolumeInterpolation);
                if (idx >= 0 && interp.Selected != idx)
                {
                    interp.SetBlockSignals(true);
                    interp.Selected = idx;
                    interp.SetBlockSignals(false);
                }
            }

            if (_continueChecks.TryGetValue(node.Id, out var continueBox)
                && continueBox != null && GodotObject.IsInstanceValid(continueBox)
                && continueBox.ButtonPressed != node.ContinueRegion)
                continueBox.SetPressedNoSignal(node.ContinueRegion);

            if (_loopChecks.TryGetValue(node.Id, out var loop)
                && loop != null && GodotObject.IsInstanceValid(loop))
            {
                loop.Visible = !node.ContinueRegion;
                if (loop.ButtonPressed != node.Loop)
                    loop.SetPressedNoSignal(node.Loop);
            }

            if (_playCountEdits.TryGetValue(node.Id, out var count)
                && count != null && GodotObject.IsInstanceValid(count))
            {
                count.Visible = !node.ContinueRegion;
                count.Editable = !node.Loop;
                if (!count.HasFocus())
                    count.Text = node.PlayCount.ToString();
            }
        }
    }

    private void OnVolumeScrubbed(int nodeId, float db)
    {
        if (_timeline == null || Busy)
            return;
        var node = _timeline.FindNode(nodeId);
        if (node == null)
            return;

        float linear = UiUtilities.DbToLinear(db);
        if (Math.Abs(node.VolumeLinear - linear) < 1e-6f)
            return;

        Record("Edit timeline node volume", CoalesceKey?.Invoke($"node-vol:{nodeId}"));
        node.VolumeLinear = linear;
        _display?.SetNodes(BuildDrawList(_timeline.GetNodesInTimeOrder(), Math.Max(1, ResolvedFileDuration())),
            SelectedNumber());
    }

    private void OnVolumeSubmitted(int nodeId, string text, LineEdit field)
    {
        if (_timeline == null || field == null || Busy)
            return;
        var node = _timeline.FindNode(nodeId);
        if (node == null)
            return;

        if (!float.TryParse(text.Replace("dB", "").Trim(), out var dbValue))
        {
            field.Text = UiUtilities.FormatComponentVolumeDb(node.VolumeLinear);
            return;
        }

        dbValue = Mathf.Clamp(dbValue, UiUtilities.MinVolumeDb, UiUtilities.MaxComponentGainDb);
        float linear = UiUtilities.DbToLinear(dbValue);
        field.Text = UiUtilities.FormatComponentVolumeDb(linear);
        var key = CoalesceKey?.Invoke($"node-vol:{nodeId}");
        if (Math.Abs(node.VolumeLinear - linear) >= 1e-6f)
        {
            Record("Edit timeline node volume", key);
            node.VolumeLinear = linear;
        }

        if (!string.IsNullOrEmpty(key))
            EndCoalesce?.Invoke(key);
        Sync();
    }

    private static string FormatOpacityPercent(float scale)
    {
        scale = AudioTimelineNode.ClampOpacityScale(scale);
        return $"{scale * 100f:0.#}%";
    }

    private void OnOpacitySubmitted(int nodeId, string text, LineEdit field)
    {
        if (_timeline == null || field == null || Busy)
            return;
        var node = _timeline.FindNode(nodeId);
        if (node == null)
            return;

        string cleaned = (text ?? string.Empty).Replace("%", "").Trim();
        if (!float.TryParse(cleaned, out float pct))
        {
            field.Text = FormatOpacityPercent(node.OpacityScale);
            return;
        }

        pct = Mathf.Clamp(pct, 0f, 100f);
        float scale = AudioTimelineNode.ClampOpacityScale(pct / 100f);
        field.Text = FormatOpacityPercent(scale);
        if (Math.Abs(node.OpacityScale - scale) < 1e-4f)
            return;

        Record("Edit timeline node opacity");
        node.OpacityScale = scale;
        Sync();
    }

    private void OnLoopToggled(int nodeId, bool state)
    {
        if (_timeline == null || Busy)
            return;
        var node = _timeline.FindNode(nodeId);
        if (node == null || node.Loop == state)
            return;

        Record("Edit timeline node loop");
        node.Loop = state;
        DurationChanged?.Invoke();
        Sync();
    }

    private void OnPlayCountSubmitted(int nodeId, string text, LineEdit field)
    {
        if (_timeline == null || field == null || Busy)
            return;
        var node = _timeline.FindNode(nodeId);
        if (node == null)
            return;

        if (!int.TryParse(text, out var playCount) || playCount < 1)
        {
            field.Text = node.PlayCount.ToString();
            return;
        }

        if (node.PlayCount == playCount)
        {
            field.Text = playCount.ToString();
            return;
        }

        Record("Edit timeline node play count");
        node.PlayCount = playCount;
        field.Text = node.PlayCount.ToString();
        DurationChanged?.Invoke();
        Sync();
    }

    private void OnInterpSelected(int nodeId, long index)
    {
        if (_timeline == null || Busy)
            return;
        var node = _timeline.FindNode(nodeId);
        if (node == null)
            return;
        if (!_interpOptions.TryGetValue(nodeId, out var button)
            || button == null || !GodotObject.IsInstanceValid(button))
            return;

        var mode = TimelineVolume.FromInt(button.GetItemId((int)index));
        if (node.VolumeInterpolation == mode)
            return;

        Record("Edit timeline node interpolation");
        node.VolumeInterpolation = mode;
        Sync();
    }

    private void OnContinueToggled(int nodeId, bool state)
    {
        if (_timeline == null || Busy)
            return;
        var node = _timeline.FindNode(nodeId);
        if (node == null || node.ContinueRegion == state)
            return;

        Record("Edit timeline node continue");
        node.ContinueRegion = state;
        DurationChanged?.Invoke();
        Sync();
    }

    private void OnRateSubmitted(int nodeId, string text, LineEdit field)
    {
        if (_timeline == null || field == null || Busy)
            return;
        var node = _timeline.FindNode(nodeId);
        if (node == null)
            return;

        if (!InspectorRatePitch.TryParseRate(text, out double rate))
        {
            field.Text = InspectorRatePitch.FormatRate(node.RateScale);
            return;
        }

        field.Text = InspectorRatePitch.FormatRate(rate);
        if (Math.Abs(node.RateScale - rate) < 1e-6)
            return;

        Record("Edit timeline node rate");
        node.RateScale = (float)rate;
        DurationChanged?.Invoke();
        Sync();
    }

    private void OnPitchSubmitted(int nodeId, string text, LineEdit field)
    {
        if (_timeline == null || field == null || Busy)
            return;
        var node = _timeline.FindNode(nodeId);
        if (node == null)
            return;

        if (!InspectorRatePitch.TryParseCents(text, out float cents))
        {
            field.Text = InspectorRatePitch.FormatCents(node.PitchCents);
            return;
        }

        field.Text = InspectorRatePitch.FormatCents(cents);
        if (Math.Abs(node.PitchCents - cents) < 0.5f)
            return;

        Record("Edit timeline node pitch");
        node.PitchCents = cents;
        Sync();
    }

    private void OnTimeSubmitted(int nodeId, string text, LineEdit field)
    {
        if (_timeline == null || field == null || Busy)
            return;
        var node = _timeline.FindNode(nodeId);
        if (node == null || node.IsFileEnd)
            return;

        var formatted = UiUtilities.ParseAndFormatTime(text, out var seconds, out _, out bool isValid);
        if (!isValid || string.IsNullOrEmpty(formatted))
        {
            field.Text = UiUtilities.FormatTime(node.TimeSeconds);
            return;
        }

        double clamped = _timeline.ClampNodeTime(seconds);
        if (Math.Abs(node.TimeSeconds - clamped) < 1e-9)
        {
            field.Text = UiUtilities.FormatTime(clamped);
            return;
        }

        Record("Edit timeline node");
        node.TimeSeconds = clamped;
        field.Text = UiUtilities.FormatTime(clamped);
        DurationChanged?.Invoke();
        Sync();
    }
}
