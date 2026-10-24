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
/// Shared timeline node UI for the audio and video inspectors: overlay handles, click-to-add,
/// click-and-drag, numbered node list, ruler playhead, and play/pause. Bound to one
/// <see cref="MediaTimeline"/>.
/// </summary>
public partial class InspectorTimeline : Node
{
    private const double ClickDelaySec = 0.2;
    private const float NodeTimeWidth = 76f;
    private const float NodeContinueWidth = 64f;
    private const float NodeLoopWidth = 36f;
    private const float NodePlayCountWidth = 36f;
    private const float NodeDeleteWidth = 22f;

    private VBoxContainer _accordion;
    private PanelContainer _panel;
    private WaveformDisplay _display;
    private WaveformZoomBar _zoom;
    private Control _handleHost;

    private MediaTimeline _timeline;
    private int _cueId = -1;
    private bool _showAudioAutomation = true;
    private bool _showOpacityAutomation;

    private readonly List<Button> _nodeHandles = new();
    private readonly List<Button> _volumeHandles = new();
    private int _selectedNodeId = -1;
    private int _draggingNodeId = -1;
    private bool _draggingVolume;
    private bool _draggingOpacity;
    private float _pendingNorm = -1f;
    private int _pendingCueId = -1;
    private SceneTreeTimer _pendingTimer;
    private int _listRebuildGen;

    /// <summary>Records document history before a timeline mutation.</summary>
    public Action<string, string> RecordHistory { get; set; }

    /// <summary>Builds a coalescing key for a continuous edit.</summary>
    public Func<string, string> CoalesceKey { get; set; }

    /// <summary>Seals a coalesced history session.</summary>
    public Action<string> EndCoalesce { get; set; }

    /// <summary>Recalculates cue duration after region or rate edits.</summary>
    public Action DurationChanged { get; set; }

    /// <summary>True while UI sync or history restore is in progress.</summary>
    public Func<bool> IsBusy { get; set; }

    /// <summary>User-facing log (message, level).</summary>
    public Action<string, int> Log { get; set; }

    /// <summary>
    /// When false, volume and pitch columns, volume handles, and volume drag-add are omitted.
    /// Rate, interpolation, continue, and looping stay.
    /// </summary>
    public bool ShowAudioAutomation
    {
        get => _showAudioAutomation;
        set
        {
            if (_showAudioAutomation == value)
                return;
            _showAudioAutomation = value;
            ApplyDisplayFlags();
            if (_nodeHint != null)
                _nodeHint.Text = NodeHintText();
            RebuildHeader();
            Sync();
        }
    }

    /// <summary>
    /// When true, the opacity column and opacity line are shown (video timeline).
    /// Drag handles sit on the opacity line only when volume handles are hidden.
    /// </summary>
    public bool ShowOpacityAutomation
    {
        get => _showOpacityAutomation;
        set
        {
            if (_showOpacityAutomation == value)
                return;
            _showOpacityAutomation = value;
            ApplyDisplayFlags();
            RebuildHeader();
            Sync();
        }
    }

    /// <summary>
    /// Wires zoom-bar click/drag and builds the node list under <paramref name="accordion"/>.
    /// </summary>
    /// <param name="accordion">Timeline accordion that already contains the waveform panel.</param>
    /// <param name="panel">Waveform panel.</param>
    /// <param name="display">Waveform display (envelope + overlay).</param>
    /// <param name="zoom">Zoom/pan chrome attached to the panel.</param>
    /// <param name="handleHost">Parent of start/end/fade handles; node handles are added here.</param>
    public void Attach(
        VBoxContainer accordion,
        PanelContainer panel,
        WaveformDisplay display,
        WaveformZoomBar zoom,
        Control handleHost)
    {
        _accordion = accordion;
        _panel = panel;
        _display = display;
        _zoom = zoom;
        _handleHost = handleHost;
        ApplyDisplayFlags();

        if (_zoom != null)
        {
            _zoom.PanelClicked += OnPanelClicked;
            _zoom.PanelDoubleClicked += OnPanelDoubleClicked;
            _zoom.PanelDragAddStarted += OnDragAddStarted;
            _zoom.PanelDragAddMoved += OnDragAddMoved;
            _zoom.PanelDragAddEnded += OnDragAddEnded;
            WireTransport();
        }

        BuildNodeListHost();
    }

    /// <summary>
    /// Binds the host to a component timeline. Pass null to clear.
    /// </summary>
    /// <param name="timeline">Shared media timeline, or null.</param>
    /// <param name="cueId">Owning cue id for pending-click guards.</param>
    public void Bind(MediaTimeline timeline, int cueId)
    {
        if (!ReferenceEquals(_timeline, timeline) || _cueId != cueId)
        {
            CancelPendingClick();
            _selectedNodeId = -1;
            _draggingNodeId = -1;
            _draggingVolume = false;
            _draggingOpacity = false;
            _timeline = timeline;
            _cueId = cueId;
            ResetParkedPlayhead();
        }
        else
        {
            _timeline = timeline;
            _cueId = cueId;
        }

        ApplyDisplayFlags();
        Sync();
    }

    /// <summary>
    /// Disconnects zoom signals and frees overlay handles. Call from the inspector <c>_ExitTree</c>.
    /// </summary>
    public void Detach()
    {
        CancelPendingClick();
        ClearHandles();
        if (_zoom != null)
        {
            _zoom.PanelClicked -= OnPanelClicked;
            _zoom.PanelDoubleClicked -= OnPanelDoubleClicked;
            _zoom.PanelDragAddStarted -= OnDragAddStarted;
            _zoom.PanelDragAddMoved -= OnDragAddMoved;
            _zoom.PanelDragAddEnded -= OnDragAddEnded;
            UnwireTransport();
        }

        _timeline = null;
        _display = null;
        _zoom = null;
        _panel = null;
        _accordion = null;
        _handleHost = null;
    }

    /// <summary>
    /// Relocalizes list chrome after a language change.
    /// </summary>
    public void Localize()
    {
        foreach (var handle in _nodeHandles)
            WaveformDisplay.StyleNodeHandle(handle);
        RefreshListChrome();
        Sync();
    }

    /// <summary>
    /// Draws node slices, places drag handles, and refreshes the list under the waveform.
    /// </summary>
    public void Sync()
    {
        _timeline?.EnsureFileEndNode();
        var ordered = _timeline?.GetNodesInTimeOrder() ?? new List<AudioTimelineNode>();
        double duration = ResolvedFileDuration();
        if (duration <= 0)
            duration = 1;

        var draw = BuildDrawList(ordered, duration);
        int selectedNumber = -1;
        for (int i = 0; i < ordered.Count; i++)
        {
            if (ordered[i].Id == _selectedNodeId)
            {
                selectedNumber = i + 1;
                break;
            }
        }

        _display?.SetNodes(draw, selectedNumber);
        SyncHandles(ordered, duration);
        SyncNodeList(ordered);
    }

    /// <summary>File duration in seconds supplied by the bound component.</summary>
    public Func<double> GetFileDuration { get; set; }

    private double ResolvedFileDuration()
    {
        double duration = GetFileDuration?.Invoke() ?? 0.0;
        if (duration <= 0)
            duration = 0;
        return duration;
    }

    private bool Busy => IsBusy?.Invoke() == true;

    private void ApplyDisplayFlags()
    {
        if (_display == null)
            return;
        _display.ShowEnvelope = _showAudioAutomation;
        _display.ShowVolumeAutomation = _showAudioAutomation;
        _display.ShowPitchAutomation = _showAudioAutomation;
        _display.ShowOpacityAutomation = _showOpacityAutomation;
    }

    private void Record(string description, string coalesceKey = null)
    {
        RecordHistory?.Invoke(description, coalesceKey);
    }

    private List<WaveformDisplay.TimelineNodeDraw> BuildDrawList(
        List<AudioTimelineNode> ordered, double duration)
    {
        var draw = new List<WaveformDisplay.TimelineNodeDraw>(ordered.Count);
        for (int i = 0; i < ordered.Count; i++)
        {
            var node = ordered[i];
            float norm = (float)Math.Clamp(node.TimeSeconds / duration, 0, 1);
            draw.Add(new WaveformDisplay.TimelineNodeDraw
            {
                Number = i + 1,
                FileNorm = norm,
                VolumeLinear = node.VolumeLinear,
                RateScale = node.RateScale,
                PitchCents = node.PitchCents,
                OpacityScale = node.OpacityScale,
                Loop = node.Loop,
                PlayCount = node.PlayCount,
                ContinueRegion = node.ContinueRegion,
                IsFileEnd = node.IsFileEnd,
                Interpolation = node.VolumeInterpolation
            });
        }

        return draw;
    }

    private int SelectedNumber()
    {
        if (_timeline == null || _selectedNodeId < 0)
            return -1;
        var ordered = _timeline.GetNodesInTimeOrder();
        for (int i = 0; i < ordered.Count; i++)
        {
            if (ordered[i].Id == _selectedNodeId)
                return i + 1;
        }

        return -1;
    }

    private void SyncHandles(List<AudioTimelineNode> ordered, double duration)
    {
        if (_handleHost == null)
            return;

        float width = _panel != null ? _panel.Size.X : 0f;
        var keep = new HashSet<int>();
        foreach (var node in ordered)
            keep.Add(node.Id);

        PruneHandles(_nodeHandles, keep, OnNodeHandleInput);
        PruneHandles(_volumeHandles, keep, OnVolumeHandleInput);

        var squares = IndexHandles(_nodeHandles);
        var circles = IndexHandles(_volumeHandles);
        int flagIndex = 0;
        if (_handleHost.GetChildCount() > 0)
            flagIndex = Math.Max(0, _handleHost.GetChildCount() - 1);

        foreach (var node in ordered)
        {
            float norm = (float)Math.Clamp(node.TimeSeconds / duration, 0, 1);
            if (!squares.TryGetValue(node.Id, out var square))
            {
                square = WaveformDisplay.CreateNodeHandle(node.Id);
                square.GuiInput += OnNodeHandleInput;
                _handleHost.AddChild(square);
                _handleHost.MoveChild(square, 0);
                _nodeHandles.Add(square);
            }

            if (node.IsFileEnd)
            {
                square.Visible = false;
                square.MouseFilter = Control.MouseFilterEnum.Ignore;
            }
            else
            {
                square.MouseFilter = Control.MouseFilterEnum.Stop;
                _display?.PlaceNodeHandle(square, norm, width);
            }

            bool showYHandle = _showAudioAutomation || _showOpacityAutomation;
            if (!showYHandle)
            {
                if (circles.TryGetValue(node.Id, out var hidden))
                    hidden.Visible = false;
                continue;
            }

            if (!circles.TryGetValue(node.Id, out var circle))
            {
                circle = WaveformDisplay.CreateVolumeHandle(node.Id);
                circle.GuiInput += OnVolumeHandleInput;
                _handleHost.AddChild(circle);
                _handleHost.MoveChild(circle, flagIndex);
                _volumeHandles.Add(circle);
                flagIndex = circle.GetIndex() + 1;
            }

            circle.Visible = true;
            if (_showAudioAutomation)
            {
                if (node.IsFileEnd)
                    WaveformDisplay.StyleFileEndVolumeHandle(circle);
                else
                    WaveformDisplay.StyleVolumeHandle(circle);
                _display?.PlaceVolumeHandle(circle, norm, node.VolumeLinear, width);
            }
            else
            {
                if (node.IsFileEnd)
                    WaveformDisplay.StyleFileEndOpacityHandle(circle);
                else
                    WaveformDisplay.StyleOpacityHandle(circle);
                _display?.PlaceOpacityHandle(circle, norm, node.OpacityScale, width);
            }
        }
    }

    private static void PruneHandles(
        List<Button> handles, HashSet<int> keep, Control.GuiInputEventHandler handler)
    {
        for (int i = handles.Count - 1; i >= 0; i--)
        {
            var handle = handles[i];
            int id = handle != null && GodotObject.IsInstanceValid(handle) && handle.HasMeta("node_id")
                ? handle.GetMeta("node_id").AsInt32()
                : -1;
            if (keep.Contains(id))
                continue;
            if (handle != null && GodotObject.IsInstanceValid(handle))
            {
                handle.GuiInput -= handler;
                handle.QueueFree();
            }

            handles.RemoveAt(i);
        }
    }

    private static Dictionary<int, Button> IndexHandles(List<Button> handles)
    {
        var byId = new Dictionary<int, Button>();
        foreach (var handle in handles)
        {
            if (handle == null || !GodotObject.IsInstanceValid(handle) || !handle.HasMeta("node_id"))
                continue;
            byId[handle.GetMeta("node_id").AsInt32()] = handle;
        }

        return byId;
    }

    private void ClearHandles()
    {
        foreach (var handle in _nodeHandles)
        {
            if (handle == null || !GodotObject.IsInstanceValid(handle))
                continue;
            handle.GuiInput -= OnNodeHandleInput;
            handle.QueueFree();
        }

        _nodeHandles.Clear();
        foreach (var handle in _volumeHandles)
        {
            if (handle == null || !GodotObject.IsInstanceValid(handle))
                continue;
            handle.GuiInput -= OnVolumeHandleInput;
            handle.QueueFree();
        }

        _volumeHandles.Clear();
    }

    private void OnPanelClicked(float fileNorm)
    {
        if (Busy || _timeline == null)
            return;

        CancelPendingClick();
        _pendingNorm = fileNorm;
        _pendingCueId = _cueId;
        if (_display != null)
            _display.PendingNodeNorm = fileNorm;
        var tree = GetTree();
        if (tree == null)
            return;
        _pendingTimer = tree.CreateTimer(ClickDelaySec);
        _pendingTimer.Timeout += OnPendingClickTimeout;
    }

    private void OnPanelDoubleClicked()
    {
        CancelPendingClick();
    }

    private void OnPendingClickTimeout()
    {
        float norm = _pendingNorm;
        int cueId = _pendingCueId;
        if (_pendingTimer != null)
        {
            _pendingTimer.Timeout -= OnPendingClickTimeout;
            _pendingTimer = null;
        }

        _pendingNorm = -1f;
        _pendingCueId = -1;
        if (_cueId != cueId)
        {
            if (_display != null)
                _display.PendingNodeNorm = -1f;
            return;
        }

        AddNodeAtFileNorm(norm);
    }

    private void CancelPendingClick()
    {
        if (_pendingTimer != null)
        {
            _pendingTimer.Timeout -= OnPendingClickTimeout;
            _pendingTimer = null;
        }

        _pendingNorm = -1f;
        _pendingCueId = -1;
        if (_display != null)
            _display.PendingNodeNorm = -1f;
    }

    private void AddNodeAtFileNorm(
        float fileNorm, float? volumeLinear = null, float? opacityScale = null, bool beginValueDrag = false)
    {
        if (_timeline == null || Busy)
            return;

        double duration = ResolvedFileDuration();
        if (duration <= 0)
            return;

        if (_timeline.CountUserNodes() >= MediaTimeline.MaxUserNodes)
        {
            Log?.Invoke($"Timeline: maximum of {MediaTimeline.MaxUserNodes} timeline nodes.", (int)LogType.Warning);
            return;
        }

        double time = _timeline.ClampNodeTime(fileNorm * duration);
        foreach (var existing in _timeline.Nodes)
        {
            if (existing != null && Math.Abs(existing.TimeSeconds - time) < 0.001)
                return;
        }

        Record("Add timeline node");
        var node = _timeline.AddNode(time, volumeLinear, opacityScale);
        if (node == null)
            return;
        _selectedNodeId = node.Id;
        if (beginValueDrag)
        {
            _draggingNodeId = node.Id;
            _draggingVolume = _showAudioAutomation;
            _draggingOpacity = !_showAudioAutomation && _showOpacityAutomation;
        }

        if (_display != null)
            _display.PendingNodeNorm = -1f;
        DurationChanged?.Invoke();
        Sync();
    }

    private void OnDragAddStarted(float fileNorm)
    {
        if (Busy)
            return;
        CancelPendingClick();
        if (_timeline == null || _display == null || _panel == null)
            return;

        if (_showAudioAutomation)
        {
            float volume = _display.YToVolumeLinear(_panel.GetLocalMousePosition().Y);
            AddNodeAtFileNorm(fileNorm, volume, beginValueDrag: true);
        }
        else if (_showOpacityAutomation)
        {
            float opacity = _display.YToOpacityScale(_panel.GetLocalMousePosition().Y);
            AddNodeAtFileNorm(fileNorm, opacityScale: opacity, beginValueDrag: true);
        }
        else
        {
            AddNodeAtFileNorm(fileNorm, beginValueDrag: true);
        }
    }

    private void OnDragAddMoved()
    {
        if (_draggingNodeId < 0)
            return;
        MoveDraggingNode();
    }

    private void OnDragAddEnded()
    {
        EndNodeDrag();
    }

    private void OnAddNodePressed()
    {
        if (_timeline == null || Busy)
            return;

        double time = _timeline.PickNewNodeTime();
        if (time < 0)
            return;
        double duration = ResolvedFileDuration();
        if (duration <= 0)
            return;
        AddNodeAtFileNorm((float)Math.Clamp(time / duration, 0, 1));
    }

    private void OnNodeHandleInput(InputEvent @event)
    {
        HandleNodeDrag(@event, volumePoint: false);
    }

    private void OnVolumeHandleInput(InputEvent @event)
    {
        HandleNodeDrag(@event, volumePoint: true);
    }

    private void HandleNodeDrag(InputEvent @event, bool volumePoint)
    {
        if (@event is InputEventMouseButton mouseButton && mouseButton.ButtonIndex == MouseButton.Left)
        {
            if (mouseButton.Pressed)
            {
                int id = HitTestHandles(volumePoint ? _volumeHandles : _nodeHandles);
                if (id < 0)
                    return;
                _selectedNodeId = id;
                Record("Move timeline node", CoalesceKey?.Invoke($"node-drag:{id}"));
                _draggingNodeId = id;
                _draggingVolume = volumePoint && _showAudioAutomation;
                _draggingOpacity = volumePoint && !_showAudioAutomation && _showOpacityAutomation;
                var handles = volumePoint ? _volumeHandles : _nodeHandles;
                foreach (var handle in handles)
                {
                    if (handle != null && GodotObject.IsInstanceValid(handle)
                        && handle.HasMeta("node_id") && handle.GetMeta("node_id").AsInt32() == id)
                    {
                        handle.AcceptEvent();
                        break;
                    }
                }

                Sync();
            }
            else
                EndNodeDrag();
        }
        else if (@event is InputEventMouseMotion && _draggingNodeId >= 0)
        {
            MoveDraggingNode();
        }
    }

    private static int HitTestHandles(List<Button> handles)
    {
        foreach (var handle in handles)
        {
            if (handle == null || !GodotObject.IsInstanceValid(handle) || !handle.Visible)
                continue;
            var local = handle.GetLocalMousePosition();
            if (local.X >= 0 && local.Y >= 0 && local.X <= handle.Size.X && local.Y <= handle.Size.Y)
                return handle.GetMeta("node_id").AsInt32();
        }

        return -1;
    }

    private void MoveDraggingNode()
    {
        if (_timeline == null || _display == null || _panel == null)
            return;
        var node = _timeline.FindNode(_draggingNodeId);
        if (node == null)
            return;

        double duration = ResolvedFileDuration();
        if (duration <= 0)
            return;

        var local = _panel.GetLocalMousePosition();
        if (!node.IsFileEnd)
        {
            float norm = _display.XToFileNorm(local.X);
            node.TimeSeconds = _timeline.ClampNodeTime(norm * duration);
        }

        if (_draggingVolume && _showAudioAutomation)
            node.VolumeLinear = _display.YToVolumeLinear(local.Y);
        else if (_draggingOpacity && _showOpacityAutomation)
            node.OpacityScale = _display.YToOpacityScale(local.Y);
        Sync();
    }

    private void EndNodeDrag()
    {
        if (_draggingNodeId < 0)
            return;
        var key = CoalesceKey?.Invoke($"node-drag:{_draggingNodeId}");
        _draggingNodeId = -1;
        _draggingVolume = false;
        _draggingOpacity = false;
        if (!string.IsNullOrEmpty(key))
            EndCoalesce?.Invoke(key);
        DurationChanged?.Invoke();
        Sync();
    }

    private void RemoveNodeAt(int nodeId)
    {
        if (_timeline == null || Busy)
            return;
        var existing = _timeline.FindNode(nodeId);
        if (existing == null || existing.IsFileEnd)
            return;

        Record("Remove timeline node");
        _timeline.RemoveNode(nodeId);
        if (_selectedNodeId == nodeId)
            _selectedNodeId = -1;
        if (_draggingNodeId == nodeId)
        {
            _draggingNodeId = -1;
            _draggingVolume = false;
            _draggingOpacity = false;
        }

        DurationChanged?.Invoke();
        Sync();
    }
}
