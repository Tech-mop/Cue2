// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using Cue2.Domain.Cues;
using Cue2.Services;
using Cue2.UI.Utilities;

namespace Cue2.Domain.Playback;


/// <summary>
/// Manages the active state of a playing cue, including timers for fades/delays, UI updates, and interaction with playback engines.
/// Encapsulates playback logic to keep CueCommandExecutor clean and allow easy pause/stop/fade control.
/// Supports minimal latency by preloading and triggering on demand.
/// </summary>
public partial class ActiveCue : GodotObject
{
    private readonly Cue _cue;
    private readonly VBoxContainer _activeCueList;
    private readonly GlobalSignals _globalSignals;
    private readonly MediaEngine _mediaEngine;
    private readonly AudioDevices _audioDevices;
    private readonly Settings _settings;

    /// <summary>
    /// Chain membership from GO (null for nested child activations under a parent group).
    /// Mutable so OSC Load standby instances can be attached to a chain on subsequent GO.
    /// </summary>
    private CueChainMember _chainMember;

    private PanelContainer _activeCueBar;
    private Timer _fadeTimer; // For fade-in/out
    private Timer _updateTimer;
    private bool _isPlaying;
    private bool _inPreWait = false;
    
    private readonly object _lock = new object(); // For thread safety

    private Timer _preWaitTimer;
    private Timer _incomingWaitTimer;
    private bool _preWaitUpdateHooked;
    private bool _incomingWaitUpdateHooked;
    private bool _incomingWaitTimeoutHooked;

    /// <summary>True once content setup/trigger has been kicked off.</summary>
    private bool _contentStarted;

    /// <summary>True after UI has been built and added to the active list (may precede StartAsync).</summary>
    private bool _uiPrepared;

    /// <summary>True after chain member entry path has run (pending or head start).</summary>
    private bool _chainRunStarted;

    /// <summary>True after <see cref="ArmIncoming"/> has been called (or head started).</summary>
    private bool _incomingArmed;

    /// <summary>True while counting the post-wait lead-in after arm.</summary>
    private bool _inIncomingWait;

    /// <summary>Post-wait duration for the active incoming lead-in (from previous cue).</summary>
    private double _incomingWaitDuration;

    /// <summary>Mode of the active/pending incoming link.</summary>
    private FollowType _incomingMode;

    /// <summary>User skipped this cue's pre-wait (now or when it becomes active).</summary>
    private bool _skipPreWait;

    /// <summary>True while PreWaitComplete is subscribed to the pre-wait timer.</summary>
    private bool _preWaitTimeoutHooked;

    /// <summary>Guards against double entry into content after pre-wait.</summary>
    private bool _preWaitFinished;
    
    private readonly Dictionary<PanelContainer, ActiveAudioPlayback> _activeAudioComponents = new();
    private readonly Dictionary<PanelContainer, AudioComponent> _componentToAudio = new();
    private readonly Dictionary<PanelContainer, ActiveVideoPlayback> _activeVideoComponents = new();
    private readonly Dictionary<PanelContainer, VideoComponent> _componentToVideo = new();
    private readonly Dictionary<PanelContainer, ActiveTextPlayback> _activeTextComponents = new();
    private readonly Dictionary<PanelContainer, TextComponent> _componentToText = new();
    private readonly Dictionary<PanelContainer, CueLightComponent> _activeCueLightComponents = new();
    private readonly Dictionary<PanelContainer, OscComponent> _activeOscComponents = new();
    private readonly Dictionary<PanelContainer, MidiOutputComponent> _activeMidiOutputComponents = new();
    private readonly Dictionary<PanelContainer, ControlComponent> _activeControlComponents = new();

    /// <summary>
    /// Timed control progress (Fade / Translate / Stop fade / GO fade-in) keyed by component panel.
    /// </summary>
    private readonly Dictionary<PanelContainer, ControlTimedProgress> _controlTimedProgress = new();

    /// <summary>Wall-clock progress for a long-running control component row.</summary>
    private struct ControlTimedProgress
    {
        public double DurationSec;
        /// <summary>Engine msec when the control started; 0 = armed but not yet running.</summary>
        public ulong StartMsec;
        public bool Started;
    }

    /// <summary>Keeps handler refs so we can disconnect before freeing UI (avoids disposed-panel callbacks).</summary>
    private readonly List<(ActiveAudioPlayback Playback, ActiveAudioPlayback.CompletedEventHandler Handler)> _audioCompleteHandlers = new();
    private readonly List<(ActiveVideoPlayback Playback, ActiveVideoPlayback.CompletedEventHandler Handler)> _videoCompleteHandlers = new();
    private readonly List<(ActiveTextPlayback Playback, ActiveTextPlayback.CompletedEventHandler Handler)> _textCompleteHandlers = new();
    
    private int _activeComponentCount = 0;
    
    /// <summary>
    /// The cue this active instance is playing.
    /// </summary>
    public Cue Cue => _cue;

    /// <summary>
    /// Yields this active cue and all nested child active cues.
    /// </summary>
    /// <returns>Self and descendants currently tracked.</returns>
    public IEnumerable<ActiveCue> EnumerateSelfAndDescendants()
    {
        yield return this;
        foreach (var child in _childActiveCues.ToList())
        {
            if (child == null || !IsInstanceValid(child)) continue;
            foreach (var nested in child.EnumerateSelfAndDescendants())
                yield return nested;
        }
    }

    /// <summary>
    /// Optional fade-in override from a control GO (seconds). When set, media starts with this fade
    /// instead of (or as) the component's own fade-in for this activation.
    /// </summary>
    private double? _controlFadeInDuration;

    /// <summary>
    /// Sets a control-GO fade-in duration for this active instance (head of a control GO).
    /// </summary>
    /// <param name="seconds">Fade-in length; values ≤ 0 clear the override.</param>
    public void SetControlFadeInDuration(double seconds)
    {
        if (seconds <= 1e-9)
        {
            _controlFadeInDuration = null;
            return;
        }

        _controlFadeInDuration = seconds;
    }

    /// <summary>
    /// Seeks this cue on its body timeline and propagates to components and nested children.
    /// </summary>
    /// <param name="timeSeconds">
    /// Absolute time on this cue's playable timeline (pre-wait + content), or a relative offset
    /// when <paramref name="relative"/> is true.
    /// </param>
    /// <param name="relative">When true, offset from the current playhead.</param>
    /// <remarks>
    /// Timeline layout: <c>[0 .. PreWait)</c> pre-wait, then <c>[PreWait .. PreWait+Duration)</c> content.
    /// Nested children share the parent's content origin (child t=0 == parent content t=0).
    /// Seeking past a child's occupancy finishes that child.
    /// </remarks>
    public void RequestSeek(double timeSeconds, bool relative)
    {
        if (_isCleaned) return;

        double current = GetCueTimelineSeconds();
        double target = relative ? current + timeSeconds : timeSeconds;
        if (target < 0) target = 0;

        ApplyCueTimelineSeek(target);
    }

    /// <summary>
    /// Queues an absolute body-timeline start position for the next run (Timeline Inspector playhead).
    /// When the target lands in the content region, pre-wait wait time is skipped so playback
    /// jumps straight into content and then seeks media/children to the correct offset.
    /// </summary>
    /// <param name="bodySeconds">Seconds from this cue's body start (start of pre-wait).</param>
    public void QueueStartAtBodyTime(double bodySeconds)
    {
        if (_isCleaned) return;
        if (bodySeconds < 0) bodySeconds = 0;

        double pre = Math.Max(0.0, _cue?.PreWait ?? 0.0);
        _pendingTimelineSeekSeconds = bodySeconds;

        // Landing in (or past) content: do not sit through the pre-wait timer.
        if (bodySeconds >= pre - 1e-9)
            _skipPreWait = true;

        GD.Print(
            $"ActiveCue:QueueStartAtBodyTime - {_cue?.Name}: body={bodySeconds:F3}s pre={pre:F3}s skipPre={_skipPreWait}");
    }

    /// <summary>
    /// Seeks this cue to an absolute body-timeline position (pre-wait + content).
    /// Used for head-bar scrub and parent→child propagation.
    /// </summary>
    /// <param name="timelineSeconds">Seconds from this cue's body start (start of pre-wait).</param>
    public void ApplyCueTimelineSeek(double timelineSeconds)
    {
        if (_isCleaned || _suppressContentCompleted) return;
        if (timelineSeconds < 0) timelineSeconds = 0;

        double pre = Math.Max(0.0, _cue?.PreWait ?? 0.0);
        double playable = GetPlayableTimelineDuration();

        // Past end of playable span → finish this cue (and nested children).
        if (playable >= 0 && timelineSeconds >= playable - 1e-4)
        {
            GD.Print(
                $"ActiveCue:ApplyCueTimelineSeek - {_cue?.Name}: {timelineSeconds:F3}s past end {playable:F3}s — finishing");
            // Not running yet: remember past-end so Start path finishes immediately without firing.
            if (!_contentStarted && !_timelineStarted)
            {
                _pendingTimelineSeekSeconds = timelineSeconds;
                _skipPreWait = true;
                return;
            }
            SetTimelineSeconds(playable);
            if (_inPreWait || _contentPlaybackActive || _childActiveCues.Count > 0 || HasOwnMediaPlayback())
                StopAll(propagateToChildren: true, fadeDurationOverride: 0);
            else
                UpdateHeadProgressUi();
            return;
        }

        // Not yet in body (still on continue/follow, or setup): queue for when body runs.
        if (!_contentStarted && !_timelineStarted)
        {
            QueueStartAtBodyTime(timelineSeconds);
            return;
        }

        if (timelineSeconds < pre - 1e-9)
        {
            SeekIntoPreWaitRegion(timelineSeconds);
            return;
        }

        // Content region on the proportional bar.
        SeekIntoContentRegion(timelineSeconds - pre, timelineSeconds);
    }

    /// <summary>
    /// True when cue-level transport is paused (content, pre-wait, or media).
    /// Used by OSC TogglePauseSelected and control tooling.
    /// </summary>
    public bool IsTransportPaused => _isPaused;

    /// <summary>
    /// Pauses transport for this active cue (media + children). Used by control components.
    /// </summary>
    public void RequestPause()
    {
        if (_isCleaned) return;

        if (_inIncomingWait && _incomingWaitTimer != null && IsInstanceValid(_incomingWaitTimer))
        {
            _incomingWaitTimer.SetPaused(true);
            if (_sequencePause != null && _activeCueBar != null)
                _sequencePause.Icon = _activeCueBar.GetThemeIcon("Play", "AtlasIcons");
        }

        if (_inPreWait)
            PreWaitPause();

        PauseAll(propagateToChildren: true);
    }

    /// <summary>
    /// Resumes transport for this active cue (media + children). Used by control components.
    /// </summary>
    public void RequestResume()
    {
        if (_isCleaned) return;

        if (_inIncomingWait && _incomingWaitTimer != null && IsInstanceValid(_incomingWaitTimer))
        {
            _incomingWaitTimer.SetPaused(false);
            if (_sequencePause != null && _activeCueBar != null)
                _sequencePause.Icon = _activeCueBar.GetThemeIcon("Pause", "AtlasIcons");
        }

        if (_inPreWait)
            PreWaitResume();

        ResumeAll(propagateToChildren: true);
    }

    /// <summary>
    /// Starts this cue's content immediately, bypassing continue/follow lead-in and pre-wait.
    /// </summary>
    /// <remarks>
    /// Used by the <see cref="ControlAction.StartNow"/> control component.
    /// No-op if content is already running (past pre-wait). Does not start a cue that
    /// is not already present in the active list (use GO for that).
    /// <para>
    /// Safe to call while a prior control GO is still in setup: sets <c>_skipPreWait</c> so
    /// the in-flight <see cref="StartPlaybackCoreAsync"/> skips pre-wait when it reaches that step.
    /// </para>
    /// </remarks>
    public void RequestStartNow()
    {
        if (_isCleaned || _suppressContentCompleted) return;

        GD.Print($"ActiveCue:RequestStartNow - {_cue?.Name}: skipping waits, starting content");

        // Cancel any continue/follow countdown or pending lead-in UI.
        if (_inIncomingWait)
        {
            HookIncomingWaitUpdate(false);
            if (_incomingWaitTimer != null && IsInstanceValid(_incomingWaitTimer))
                _incomingWaitTimer.Stop();
            FreeIncomingWaitTimer();
            _inIncomingWait = false;
        }

        // Ignore later ArmIncoming from a predecessor; we are starting independently.
        _incomingArmed = true;
        _skipPreWait = true;
        HideSequencePanel();

        // Active pre-wait → jump straight into content.
        if (_inPreWait)
        {
            _ = FinishPreWaitAndStartContent();
            return;
        }

        // Not started yet: start core without pre-wait (or race-safe with an incoming StartAsync).
        if (!_contentStarted)
        {
            if (_chainMember != null && !_chainRunStarted)
                _chainRunStarted = true;
            _ = StartPlaybackCoreAsync(includePreWait: false);
            return;
        }

        // Setup already running (e.g. loading media before PreWait): _skipPreWait is set so
        // StartPlaybackCoreAsync will skip pre-wait when it reaches that decision.
        // Already playing content → nothing to skip.
        if (_isPlaying || _preWaitFinished)
        {
            GD.Print($"ActiveCue:RequestStartNow - {_cue?.Name}: already in content, ignoring");
        }
        else
        {
            GD.Print($"ActiveCue:RequestStartNow - {_cue?.Name}: setup in progress, pre-wait will be skipped");
        }
    }

    /// <summary>
    /// Re-applies expand/stretch/opacity on live video TextureRects for a video component.
    /// </summary>
    public void RefreshVideoVisuals(VideoComponent component)
    {
        if (component == null || _isCleaned)
            return;

        foreach (var playback in _activeVideoComponents.Values)
        {
            if (playback != null && playback.UsesVideoComponent(component))
                playback.RefreshVisualProperties();
        }
    }

    /// <summary>
    /// Enumerates live audio playback instances on this active cue (not descendants).
    /// </summary>
    public IEnumerable<ActiveAudioPlayback> EnumerateAudioPlaybacks()
    {
        foreach (var playback in _activeAudioComponents.Values)
        {
            if (playback != null && IsInstanceValid(playback))
                yield return playback;
        }
    }

    /// <summary>
    /// Enumerates live video playback instances on this active cue (not descendants).
    /// </summary>
    public IEnumerable<ActiveVideoPlayback> EnumerateVideoPlaybacks()
    {
        foreach (var playback in _activeVideoComponents.Values)
        {
            if (playback != null && IsInstanceValid(playback))
                yield return playback;
        }
    }

    /// <summary>
    /// Re-applies text content and style on live RichTextLabels for a text component.
    /// </summary>
    /// <param name="component">Text component whose active playback should refresh.</param>
    public void RefreshTextVisuals(TextComponent component)
    {
        if (component == null || _isCleaned)
            return;

        foreach (var playback in _activeTextComponents.Values)
        {
            if (playback != null && playback.UsesTextComponent(component))
                playback.RefreshVisualProperties();
        }
    }

    /// <summary>
    /// Event raised when the cue playback is completed (cleanup finished).
    /// </summary>
    [Signal]
    public delegate void CompletedEventHandler();

    /// <summary>
    /// Raised once when cue content finishes naturally (components + child cues done).
    /// Not raised on stop/panic. Used for auto-follow arming (real completion, seek-aware).
    /// </summary>
    public event Action ContentCompleted;

    /// <summary>
    /// Raised when the content phase begins (after this cue's pre-wait / lead-in).
    /// Used for auto-continue arming.
    /// </summary>
    public event Action ContentPhaseStarted;

    /// <summary>
    /// Next cue in a pre-spawned continue/follow chain (cancelled if this cue is stopped early).
    /// </summary>
    public ActiveCue NextInChain { get; set; }

    /// <summary>
    /// True after natural content completion has been reported (or suppressed by stop).
    /// </summary>
    private bool _contentCompletedRaised;

    /// <summary>
    /// True after content-phase-started has been reported.
    /// </summary>
    private bool _contentPhaseStartedRaised;

    /// <summary>
    /// When true, stop paths must not report natural content completion or arm the chain.
    /// </summary>
    private bool _suppressContentCompleted;
    
    
    // UI
    private ProgressBar _headProgressBar;
    private Label _headLabelName;
    private Label _headLabelTimeLeft;
    private Label _headLabelTimeRight;

    private Button _headPause;
    private Button _headStop;
    
    private VBoxContainer _componentContainer;
        
    private Label _preWaitTimerLabel;
    private ProgressBar _preWaitProgress;
    private PanelContainer _preWaitPanel;
    private Button _preWaitPause;
    private Button _preWaitSkip;

    private PanelContainer _sequencePanel;
    private ProgressBar _sequenceProgress;
    private Label _sequenceLabel;
    private Label _sequenceNameLabel;
    private Label _sequenceTimerLabel;
    private Button _sequencePause;
    private Button _sequenceSkip;

    private PanelContainer _postWaitPanel;
    private ProgressBar _postWaitProgress;
    private Label _postWaitLabel;
    private Label _postWaitNameLabel;
    private Label _postWaitTimerLabel;
    private Button _postWaitPause;
    private Button _postWaitSkip;
    
    // Main cue progress scene
    private PackedScene _activeCueBarScene = SceneLoader.LoadPackedScene("uid://dt7rlfag7yr2c", out string error); 
    // Component progress scene
    private PackedScene _componentProgressBarScene = SceneLoader.LoadPackedScene("uid://cb7g4xgryo2dg", out string error);
    
    private bool _isPaused = false;
    private bool _isCleaned = false;
    private bool _isFinished = false;
    /// <summary>True after the first Stop while a stop-fade is in progress (second Stop = hard stop).</summary>
    private bool _isStopFading = false;

    private readonly List<ActiveCue> _childActiveCues = new List<ActiveCue>();

    // --- Body timeline (head progress / parent↔child seek) ---
    // Layout: [0, PreWait) pre-wait, [PreWait, PreWait+Duration) content (Duration includes nested children).

    /// <summary>Frozen body-timeline position (seconds) when the clock is not running.</summary>
    private double _timelineBase;

    /// <summary>Engine msec when the running body clock was started/resumed.</summary>
    private ulong _timelineClockStartMsec;

    /// <summary>True while the body wall clock advances.</summary>
    private bool _timelineClockRunning;

    /// <summary>True after pre-wait or content has started (body clock origin established).</summary>
    private bool _timelineStarted;

    /// <summary>How much pre-wait was honored when content began (0 if skipped).</summary>
    private double _preWaitSecondsHonored;

    /// <summary>True while content playback is active (media and/or children running).</summary>
    private bool _contentPlaybackActive;

    /// <summary>True after <see cref="TriggerComponents"/> has been called at least once.</summary>
    private bool _componentsTriggered;

    /// <summary>
    /// True after <see cref="SetupComponents"/> has finished (including early preload for pending follow).
    /// </summary>
    private bool _componentsSetup;

    /// <summary>
    /// True when this instance was created/prepared by OSC Load (or equivalent) and has not
    /// entered content yet. GO reuses standby instances so preload work is not discarded.
    /// </summary>
    private bool _standbyPreload;

    /// <summary>Preload task for pending chain members (follow/continue) so arm can trigger immediately.</summary>
    private Task _pendingPreloadTask;

    /// <summary>
    /// True when media components are set up but content has not started (safe to reuse for GO).
    /// </summary>
    public bool IsStandbyPreloaded =>
        _standbyPreload && !_contentStarted && !_isCleaned && _uiPrepared;

    /// <summary>True while the user is scrubbing the head progress bar.</summary>
    private bool _headIsSeeking;

    /// <summary>Preview body-timeline time while scrubbing the head bar.</summary>
    private double _pendingHeadSeekSeconds;

    /// <summary>
    /// Absolute body-timeline seek to apply when body/content becomes ready.
    /// </summary>
    private double? _pendingTimelineSeekSeconds;

    /// <summary>
    /// True while rewinding content for a scrub into pre-wait (ignore child Completed → parent finish).
    /// </summary>
    private bool _isRewindingContent;

    /// <summary>
    /// Child cue ids that have already finished (or been finished by seek) during this activation.
    /// Cleared when rewinding to pre-wait or content t≈0 so a scrub-to-start can re-fire children.
    /// Mid-timeline scrub-back does not resurrect finished children.
    /// </summary>
    private readonly HashSet<int> _finishedChildCueIds = new();

    /// <summary>
    /// Initializes a new instance of the ActiveCue class for Godot serialization.
    /// </summary>
    public ActiveCue()
    {
        // Blank constructor for Godot
    }
    
    /// <summary>
    /// Initializes a new instance of the ActiveCue class with the specified cue and dependencies.
    /// </summary>
    /// <param name="cue">The cue to activate.</param>
    /// <param name="activeCueList">The UI container for active cues.</param>
    /// <param name="mediaEngine">The media engine for audio processing.</param>
    /// <param name="audioDevices">The audio devices manager.</param>
    /// <param name="globalSignals">The global signals for event communication.</param>
    /// <param name="chainMember">
    /// Optional chain membership from GO. Null for nested child cues under a parent group.
    /// </param>
    public ActiveCue(
        Cue cue,
        VBoxContainer activeCueList,
        MediaEngine mediaEngine,
        AudioDevices audioDevices,
        GlobalSignals globalSignals,
        CueChainMember chainMember = null)
    {
        _cue = cue ?? throw new ArgumentNullException(nameof(cue));
        _activeCueList = activeCueList;
        _mediaEngine = mediaEngine ?? throw new ArgumentNullException(nameof(mediaEngine));
        _audioDevices = audioDevices ?? throw new ArgumentNullException(nameof(audioDevices));
        _globalSignals = globalSignals ?? throw new ArgumentNullException(nameof(globalSignals));
        _settings = _activeCueList.GetNode<GlobalData>("/root/GlobalData").Settings;
        _chainMember = chainMember;
        _incomingMode = chainMember?.IncomingMode ?? FollowType.None;
        _incomingWaitDuration = chainMember?.IncomingPostWait ?? 0.0;
    }

    /// <summary>
    /// Builds the active-cue row and inserts it into the list immediately (synchronous).
    /// Call in chain order so the active list matches sequence occurrence order.
    /// </summary>
    public void PrepareUiInOrder()
    {
        if (_uiPrepared || _isCleaned) return;
        SetupUi();
        SetupSignals();
        SetupTimers();
        _uiPrepared = true;
    }

    private void SetupUi()
    {
        // Parent cue UI 
        _activeCueBar = _activeCueBarScene.Instantiate<PanelContainer>();
        _activeCueList.AddChild(_activeCueBar);
        
        _componentContainer = _activeCueBar.GetNode<VBoxContainer>("%ComponentList");
        _headProgressBar = _activeCueBar.GetNode<ProgressBar>("%ProgressBar"); 
        _headLabelName = _activeCueBar.GetNode<Label>("%LabelName"); 
        _headLabelTimeLeft = _activeCueBar.GetNode<Label>("%LabelTimeLeft"); 
        _headLabelTimeRight = _activeCueBar.GetNode<Label>("%LabelTimeRight");
        _headPause = _activeCueBar.GetNode<Button>("%HeadPause");
        _headStop = _activeCueBar.GetNode<Button>("%HeadStop");
        
        _preWaitPanel = _activeCueBar.GetNode<PanelContainer>("%PreWaitBar");
        _preWaitTimerLabel = _preWaitPanel.GetNode<Label>("%PreWaitTimer");
        _preWaitProgress = _preWaitPanel.GetNode<ProgressBar>("%PreWaitProgress");
        _preWaitPause = _preWaitPanel.GetNode<Button>("%PreWaitPause");
        _preWaitSkip = _preWaitPanel.GetNode<Button>("%PreWaitSkip");

        _sequencePanel = _activeCueBar.GetNodeOrNull<PanelContainer>("%SequenceBar");
        if (_sequencePanel != null)
        {
            _sequenceProgress = _sequencePanel.GetNodeOrNull<ProgressBar>("%SequenceProgress");
            _sequenceLabel = _sequencePanel.GetNodeOrNull<Label>("%SequenceLabel");
            _sequenceNameLabel = _sequencePanel.GetNodeOrNull<Label>("%SequenceNameLabel");
            _sequenceTimerLabel = _sequencePanel.GetNodeOrNull<Label>("%SequenceTimer");
            _sequencePause = _sequencePanel.GetNodeOrNull<Button>("%SequencePause");
            _sequenceSkip = _sequencePanel.GetNodeOrNull<Button>("%SequenceSkip");
        }

        _postWaitPanel = _activeCueBar.GetNodeOrNull<PanelContainer>("%PostWaitBar");
        if (_postWaitPanel != null)
        {
            _postWaitProgress = _postWaitPanel.GetNodeOrNull<ProgressBar>("%PostWaitProgress");
            _postWaitLabel = _postWaitPanel.GetNodeOrNull<Label>("%PostWaitLabel");
            _postWaitNameLabel = _postWaitPanel.GetNodeOrNull<Label>("%PostWaitNameLabel");
            _postWaitTimerLabel = _postWaitPanel.GetNodeOrNull<Label>("%PostWaitTimer");
            _postWaitPause = _postWaitPanel.GetNodeOrNull<Button>("%PostWaitPause");
            _postWaitSkip = _postWaitPanel.GetNodeOrNull<Button>("%PostWaitSkip");
        }

        // Continue/follow completes before pre-wait — keep that bar above pre-wait.
        EnsureSequenceBarAbovePreWait();

        //GD.Print($"ActiveCue:SetupUi - Setting everything the same colour why? {_cue.Name}");
        var cueColor = _cue.Color;
        var colorBar = _activeCueBar.GetNode<Panel>("%ColorBar");
        var style = colorBar.GetThemeStylebox("panel").Duplicate() as StyleBoxFlat;
        style.BgColor = cueColor;
        colorBar.AddThemeStyleboxOverride("panel", style);
        
        var borderStyle = _activeCueBar.GetThemeStylebox("panel").Duplicate() as StyleBoxFlat;
        borderStyle.BorderColor = cueColor;
        _activeCueBar.AddThemeStyleboxOverride("panel", borderStyle);

        // Wait / follow fills use the cool (low) cascade; content uses the warm (high) cascade.
        ApplyProgressFill(_sequenceProgress, GlobalStyles.LowColor2);
        ApplyProgressFill(_preWaitProgress, GlobalStyles.LowColor1);
        ApplyProgressFill(_postWaitProgress, GlobalStyles.LowColor3);
        ApplyProgressFill(_headProgressBar, GlobalStyles.HighColor1);
        var fadeBar = _activeCueBar.GetNodeOrNull<ProgressBar>("%FadeProgressBar");
        ApplyProgressFill(fadeBar, GlobalStyles.HighColor5, fadeOverlay: true);
        
        _headPause.Icon = _activeCueBar.GetThemeIcon("Pause", "AtlasIcons");
        _headStop.Icon = _activeCueBar.GetThemeIcon("Stop", "AtlasIcons");
        
        _headProgressBar.Value = 0;
        _headLabelName.Text = _cue.Name;

        // Ensure duration includes nested children before first paint.
        try { _cue.CalculateTotalDuration(); } catch { /* best-effort */ }

        double playable = GetPlayableTimelineDuration();
        _headLabelTimeLeft.Text = UiUtilities.FormatTime(0);
        _headLabelTimeRight.Text = playable < 0
            ? "∞"
            : $"-({UiUtilities.FormatTime(playable)})";

        // Head bar scrub seeks full body timeline (pre-wait + content + nested children).
        WireHeadProgressSeek();
    }

    /// <summary>
    /// Applies a GlobalStyles high/low fill to an active-cue progress bar.
    /// </summary>
    /// <param name="bar">Progress bar to style; ignored when null.</param>
    /// <param name="fill">Fill colour (typically <see cref="GlobalStyles.HighColor1"/>–5 or LowColor1–5).</param>
    /// <param name="fadeOverlay">When true, also tints the bottom border to the same cascade family.</param>
    private static void ApplyProgressFill(ProgressBar bar, Color fill, bool fadeOverlay = false)
    {
        if (bar == null || !GodotObject.IsInstanceValid(bar))
            return;

        var style = bar.GetThemeStylebox("fill")?.Duplicate() as StyleBoxFlat ?? new StyleBoxFlat();
        style.BgColor = fill;
        if (fadeOverlay)
        {
            style.BorderWidthBottom = Math.Max(style.BorderWidthBottom, 1);
            style.BorderColor = GlobalStyles.HighColor3;
        }
        bar.AddThemeStyleboxOverride("fill", style);
    }

    /// <summary>
    /// Instantiates a component progress row styled with the high (content) cascade.
    /// </summary>
    private PanelContainer CreateStyledComponentProgressBar()
    {
        var panel = _componentProgressBarScene.Instantiate<PanelContainer>();
        ApplyProgressFill(panel.GetNodeOrNull<ProgressBar>("ComponentProgress"), GlobalStyles.HighColor2);
        ApplyProgressFill(
            panel.GetNodeOrNull<ProgressBar>("%ComponentFadeProgress"),
            GlobalStyles.HighColor5,
            fadeOverlay: true);
        return panel;
    }

    /// <summary>
    /// Wires click/drag scrubbing on the cue head progress bar to the body timeline.
    /// </summary>
    private void WireHeadProgressSeek()
    {
        if (_headProgressBar == null) return;

        _headProgressBar.GuiInput += OnHeadProgressGuiInput;
    }

    private void OnHeadProgressGuiInput(InputEvent @event)
    {
        if (_isCleaned || _headProgressBar == null || !IsInstanceValid(_headProgressBar))
            return;

        double playable = GetPlayableTimelineDuration();
        // Infinite / unknown / empty: no meaningful scrub target.
        if (playable < 0 || playable <= 1e-9)
            return;

        if (@event is InputEventMouseButton mb && mb.ButtonIndex == MouseButton.Left)
        {
            if (mb.Pressed)
            {
                _headIsSeeking = true;
                double percent = Math.Clamp(mb.Position.X / Math.Max(1.0, _headProgressBar.Size.X), 0.0, 1.0);
                _pendingHeadSeekSeconds = percent * playable;
                _headProgressBar.Value = percent * 100.0;
                UpdateHeadTimeLabels(_pendingHeadSeekSeconds, playable);
            }
            else if (_headIsSeeking)
            {
                _headIsSeeking = false;
                ApplyCueTimelineSeek(_pendingHeadSeekSeconds);
                GD.Print($"ActiveCue:OnHeadProgressGuiInput - {_cue?.Name}: seek to {_pendingHeadSeekSeconds:F3}s / {playable:F3}s");
            }
        }
        else if (@event is InputEventMouseMotion mm && _headIsSeeking)
        {
            double percent = Math.Clamp(mm.Position.X / Math.Max(1.0, _headProgressBar.Size.X), 0.0, 1.0);
            _pendingHeadSeekSeconds = percent * playable;
            _headProgressBar.Value = percent * 100.0;
            UpdateHeadTimeLabels(_pendingHeadSeekSeconds, playable);
        }
    }

    /// <summary>
    /// Moves the continue/follow strip above pre-wait (sequence lead-in runs first).
    /// </summary>
    private void EnsureSequenceBarAbovePreWait()
    {
        if (_sequencePanel == null || _preWaitPanel == null) return;
        if (!IsInstanceValid(_sequencePanel) || !IsInstanceValid(_preWaitPanel)) return;

        var parent = _sequencePanel.GetParent();
        if (parent == null || _preWaitPanel.GetParent() != parent) return;

        int preIdx = _preWaitPanel.GetIndex();
        int seqIdx = _sequencePanel.GetIndex();
        if (seqIdx > preIdx)
            parent.MoveChild(_sequencePanel, preIdx);
    }

    private void SetupSignals()
    {
        _globalSignals.StopAll += GlobalStopAll;
        // Use named handlers so Cleanup can disconnect them (lambdas cannot be unsubscribed).
        _globalSignals.PauseAll += GlobalPauseAll;
        _globalSignals.ResumeAll += GlobalResumeAll;

        _headPause.Pressed += TogglePauseAll;
        _headStop.Pressed += OnHeadStopPressed;

        _preWaitPause.Pressed += TogglePreWaitPause;
        _preWaitSkip.Pressed += OnPreWaitSkipPressed;

        if (_sequencePause != null)
            _sequencePause.Pressed += ToggleSchedulePause;
        if (_sequenceSkip != null)
            _sequenceSkip.Pressed += OnSequenceSkipPressed;
        if (_postWaitPause != null)
            _postWaitPause.Pressed += ToggleSchedulePause;
        if (_postWaitSkip != null)
            _postWaitSkip.Pressed += OnPostWaitSkipPressed;
    }

    private void OnHeadStopPressed()
    {
        StopAll();
    }

    private void SetupTimers()
    {
        _updateTimer = new Timer { WaitTime = 0.1, OneShot = false }; // 10Hz UI update
        _activeCueBar.AddChild(_updateTimer);
        _updateTimer.Timeout += UpdateUi;
        _updateTimer.Start();
        
        _fadeTimer = new Timer { OneShot = true }; // For fades, set dynamically
        _activeCueBar.AddChild(_fadeTimer);
        
        if (_cue.PreWait > 0)
        {
            _preWaitTimer = new Timer { WaitTime = _cue.PreWait, OneShot = true, IgnoreTimeScale = true};
            _activeCueBar.AddChild(_preWaitTimer);
        }
    }
    
}
