// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

using Godot;
using Godot.Collections;

using Cue2.Domain.Connections;
using Cue2.Domain.Cues;
using Cue2.Domain.Library;
using Cue2.UI.Shell;
using Cue2.Services;
using Cue2.UI.Popups;
using Cue2.UI.Utilities;

// This script is attached to the cuelist in main UI
// Originator
namespace Cue2.Domain.Cuelist;

/// <summary>
/// Manages the main cue list UI, including creation, removal, drag-and-drop reordering
/// (with support for nesting/grouping), box multi-select, and save/load of cue hierarchy and order.
/// </summary>
public partial class CueList : Control
{
	internal GlobalData _globalData;
	internal GlobalSignals _globalSignals;
	
	/// <summary>
	/// Global lookup of all cues by ID. Populated on creation and used for fast access.
	/// </summary>
	public static System.Collections.Generic.Dictionary<int, Cue> CueIndex; // <CueId, Cue>

	/// <summary>The workspace cuelist, or null before <c>_Ready</c> / after teardown.</summary>
	internal static CueList Live { get; private set; }

	/// <summary>
	/// Total number of cues in the show (all levels, including group children).
	/// </summary>
	/// <value>Count of entries in <see cref="CueIndex"/>.</value>
	public int TotalCueCount => CueIndex?.Count ?? 0;
	
	// Reordering is handled by a dedicated controller (see CueReorder.cs).
	private CueReorder _reorderController;

	// Box (marquee) multi-select controller (see CueBoxSelect.cs).
	private CueBoxSelect _boxSelectController;

	// Reorder constants (avoid magic numbers; actual shell min size ~26 in ShellBar.tscn)
	private const int ShellHeight = 26;
	private const int ShellMarginDiv = 4;
	
	private PackedScene _shellBarPackedScene = SceneLoader.LoadPackedScene("uid://d207a67e3ebww", out _);

	// Ui
	internal VBoxContainer _cueContainer;
	private ScrollContainer _cueListScroll;
	/// <summary>
	/// Trailing pad below the last cue so the list can scroll slightly past the end.
	/// Lives inside the scroll content (with zebra) — not a MarginContainer margin.
	/// </summary>
	private Control _scrollEndPad;
	private Button _addCueButton;
	private Button _expandAllButton;

	/// <summary>
	/// Extra scrollable space below the last cue (as a fraction of row height).
	/// Lets the final row clear the bottom of the viewport when the list is full.
	/// </summary>
	private const float ScrollEndPaddingRows = 3.0f;

	private Control _headerColorPad;
	private Control _headerIssuePad;
	private Label _numberHeaderLabel;
	private Control _numberNameResizeGrip;
	private Label _nameHeaderLabel;
	private Label _preWaitHeaderLabel;
	private Label _durationHeaderLabel;
	private Label _postWaitHeaderLabel;
	private Label _followHeaderLabel;
	private Control _headerScrollPad;
	private bool _headerScrollbarWired;

	private Control _reorderCueControl;
	private Label _reorderLocationLabel;
	private VBoxContainer _reorderListContainer;
	private Panel _reorderIndicatorPanel;

	// Expand/collapse all state
	private bool _allExpanded = false;

	/// <summary>True while the user is dragging the Number/Name column grip.</summary>
	private bool _isDraggingNumberColumn;

	/// <summary>
	/// In-app cue clipboard: ordered root original ids and a flat map of original-id → deep-cloned <see cref="Cue.GetData"/>.
	/// Survives cut (sources deleted) and supports multi-root paste with full child trees.
	/// </summary>
	private List<int> _clipboardRootIds = new();
	private Dictionary _clipboardCuesByOldId;

	/// <summary>
	/// True while a frame-sliced bulk structural operation (duplicate/paste/copy/cut/delete) is running.
	/// Concurrent cuelist mutations and cuelist-scope undo/redo are blocked while set.
	/// </summary>
	private bool _bulkOpInProgress;

	/// <summary>
	/// Whether a bulk cuelist structural operation is currently frame-slicing work.
	/// </summary>
	public bool IsBulkOpInProgress => _bulkOpInProgress;

	/// <summary>
	/// Short label for the in-flight bulk op (e.g. <c>duplicate cues</c>), or empty when idle.
	/// </summary>
	public string BulkOpLabel { get; private set; } = string.Empty;

	/// <summary>
	/// Nested suppress depth for <see cref="NotifyTotalCuesChanged"/> during bulk shell create/delete.
	/// When >0, count/zebra updates are deferred until the outer bulk ends.
	/// </summary>
	private int _bulkNotifySuppressDepth;

	/// <summary>
	/// Ops that touch this many cues (including group children) run async with footer progress;
	/// smaller ops stay synchronous for snappy single-cue editing.
	/// </summary>
	private const int BulkAsyncThreshold = 20;

	/// <summary>Shell creates / clipboard clones processed per frame during async bulk ops.</summary>
	private const int BulkItemsPerFrame = 12;
	
	public CueList()
	{
		CueIndex = new System.Collections.Generic.Dictionary<int, Cue>();
	}

	public override void _Ready()
	{
		_globalData = GetNode<GlobalData>("/root/GlobalData");
		_globalData.Cuelist = this;
		Live = this;
		_globalSignals = GetNode<GlobalSignals>("/root/GlobalSignals");

		// Ui
		_cueContainer = GetNode<VBoxContainer>("%CueContainer");
		_cueListScroll = GetNodeOrNull<ScrollContainer>("VBoxContainer/Cue List");
		_scrollEndPad = GetNodeOrNull<Control>("%ScrollEndPad");
		_addCueButton = GetNode<Button>("%AddCueButton");
		_expandAllButton = GetNode<Button>("%ExpandAllButton");

		_headerColorPad = GetNodeOrNull<Control>("%HeaderColorPad");
		_headerIssuePad = GetNodeOrNull<Control>("%HeaderIssuePad");
		_numberHeaderLabel = GetNodeOrNull<Label>("%NumberHeaderLabel");
		_numberNameResizeGrip = GetNodeOrNull<Control>("%NumberNameResizeGrip");
		_nameHeaderLabel = GetNodeOrNull<Label>("%NameHeaderLabel");
		_preWaitHeaderLabel = GetNodeOrNull<Label>("%PreWaitHeaderLabel");
		_durationHeaderLabel = GetNodeOrNull<Label>("%DurationHeaderLabel");
		_postWaitHeaderLabel = GetNodeOrNull<Label>("%PostWaitHeaderLabel");
		_followHeaderLabel = GetNodeOrNull<Label>("%FollowHeaderLabel");
		_headerScrollPad = GetNodeOrNull<Control>("%HeaderScrollPad");
		
		_reorderCueControl = GetNode<Control>("%ReorderCueControl");
		_reorderLocationLabel = GetNode<Label>("%ReorderLocationLabel");
		_reorderListContainer = GetNode<VBoxContainer>("%ReorderListContainer");
		_reorderIndicatorPanel = GetNode<Panel>("%ReorderIndicatorPanel");

		_addCueButton.Icon = GetThemeIcon("PlusCircled", "AtlasIcons");
		_expandAllButton.Icon = GetThemeIcon("Right", "AtlasIcons");

		_reorderCueControl.Visible = false;

		LoadShellColumnPrefs();
		// Apply show-scoped cuelist density before wiring header (so chrome sizes match).
		ApplyCueListScale(_globalData?.Settings?.CueListScale ?? Settings.DefaultCueListScale, silent: true);
		SetupShellColumnHeader();
		ShellColumnLayout.Changed += OnShellColumnLayoutChanged;

		_syncHotkeys();

		_reorderController = new CueReorder(this, _reorderCueControl, _reorderLocationLabel, _reorderListContainer, _reorderIndicatorPanel, _cueContainer);
		_boxSelectController = new CueBoxSelect(this, _cueContainer, _cueListScroll, this);

		// Empty space in the list (below last cue / gutters / end pad) starts box-select / clear.
		if (_cueContainer != null)
			_cueContainer.GuiInput += OnCueContainerGuiInput;
		if (_scrollEndPad != null)
			_scrollEndPad.GuiInput += OnCueContainerGuiInput;

		_globalSignals.CreateCue += CreateCue;
		_globalSignals.DeleteSelectedCues += DeleteSelectedCues;
		_globalSignals.DuplicateSelectedCues += DuplicateSelectedCues;
		_globalSignals.CutSelectedCues += CutSelectedCues;
		_globalSignals.CopySelectedCues += CopySelectedCues;
		_globalSignals.PasteCues += PasteCues;
		_globalSignals.GroupSelectedCues += GroupSelectedCues;
		_globalSignals.CuelistExpandOneLayer += ExpandOneLayer;
		_globalSignals.CuelistCollapseOneLayer += CollapseOneLayer;
		_globalSignals.ToggleExpandAll += OnExpandAllPressed;
		_addCueButton.Pressed += CreateCue;
		_expandAllButton.Pressed += OnExpandAllPressed;

		_globalSignals.ShowModeChanged += OnShowModeChanged;
		_globalSignals.CueListScaleChanged += OnCueListScaleChanged;
		_globalSignals.NewSession += OnNewSession;
		_globalSignals.LocaleChanged += OnLocaleChanged;
		ApplyShowModeUi(_globalData?.Settings?.IsCueEditingLocked == true);
		UiLocalizer.LocalizeTree(this);

		// _Input/_Process exist for reorder + marquee only. Leaving them always-on wraps every
		// mouse motion in C# and leaks InputEventMouseMotion on quit (Godot Mono unsafe refs).
		SetProcessInput(false);
		SetProcess(false);
	}

	public override void _ExitTree()
	{
		// Recycled shells live off the scroll container; free them before this node dies.
		ClearVirtualState();
		ShellColumnLayout.Changed -= OnShellColumnLayoutChanged;
		if (_numberNameResizeGrip != null)
			_numberNameResizeGrip.GuiInput -= OnNumberNameGripGuiInput;
		if (_durationHeaderLabel != null)
			_durationHeaderLabel.GuiInput -= OnTimeHeaderGuiInput;
		if (_cueContainer != null)
			_cueContainer.GuiInput -= OnCueContainerGuiInput;
		if (_scrollEndPad != null)
			_scrollEndPad.GuiInput -= OnCueContainerGuiInput;
		if (_globalSignals != null)
		{
			_globalSignals.ShowModeChanged -= OnShowModeChanged;
			_globalSignals.CueListScaleChanged -= OnCueListScaleChanged;
			_globalSignals.NewSession -= OnNewSession;
			_globalSignals.LocaleChanged -= OnLocaleChanged;
		}
		if (_virtualScrollWired && _cueListScroll != null && IsInstanceValid(_cueListScroll))
		{
			_cueListScroll.Resized -= OnVirtualScrollChanged;
			var vBar = _cueListScroll.GetVScrollBar();
			if (vBar != null)
				vBar.ValueChanged -= OnVirtualScrollValueChanged;
			_virtualScrollWired = false;
		}
		UnwireHeaderScrollbarPad();
		_boxSelectController?.DisposeResources();
		ShellColumnLayout.DisposeCompactStyles();
		if (Live == this)
			Live = null;
		base._ExitTree();
	}

	/// <summary>
	/// Re-localizes cuelist header labels and tooltips when the UI language changes.
	/// </summary>
	/// <param name="localeCode">New locale code.</param>
	private void OnLocaleChanged(string localeCode)
	{
		if (!GodotObject.IsInstanceValid(this))
			return;
		UiLocalizer.LocalizeTree(this);
		_syncHotkeys();
	}

	/// <summary>
	/// Applies cuelist UI scale from General Settings (row height / chrome / fonts only).
	/// </summary>
	private void OnCueListScaleChanged(float scale)
	{
		ApplyCueListScale(scale, silent: false);
	}

	/// <summary>
	/// Re-applies scale after New Session / show reset.
	/// </summary>
	private void OnNewSession()
	{
		ApplyCueListScale(_globalData?.Settings?.CueListScale ?? Settings.DefaultCueListScale, silent: false);
	}

	/// <summary>
	/// Updates <see cref="ShellColumnLayout.Scale"/> and refreshes header chrome.
	/// Shell rows re-layout via <see cref="ShellColumnLayout.Changed"/>.
	/// </summary>
	/// <param name="scale">Scale factor (Small / Medium / Large).</param>
	/// <param name="silent">When true, set scale without raising Changed (caller will apply once).</param>
	private void ApplyCueListScale(float scale, bool silent)
	{
		if (silent)
			ShellColumnLayout.SetScaleSilent(scale);
		else
			ShellColumnLayout.Scale = scale;

		// Header button sizes use scaled chrome; re-apply even after silent set.
		ApplyHeaderChromeSizes();
		ApplyHeaderColumnLayout();
		UpdateScrollEndPadding();
	}

	/// <summary>
	/// Sizes the trailing scroll pad so the list can scroll slightly past the last cue.
	/// Pad is a sibling of <see cref="_cueContainer"/> inside the zebra-covered content,
	/// so stripes continue through the blank space.
	/// </summary>
	private void UpdateScrollEndPadding()
	{
		if (_scrollEndPad == null || !IsInstanceValid(_scrollEndPad))
			return;

		float padPx = Mathf.Max(24f, ShellColumnLayout.RowMinHeight * ScrollEndPaddingRows);
		_scrollEndPad.CustomMinimumSize = new Vector2(0f, padPx);
		QueueSyncHeaderScrollbarPad();
	}

	/// <summary>
	/// After GO advances the playhead: if the newly selected cue sits at or past the bottom
	/// of the visible cuelist (or outside the viewport), scroll so that cue is centered.
	/// </summary>
	/// <param name="cue">Playhead cue to keep in view.</param>
	public void EnsurePlayheadVisibleAfterGo(Cue cue)
	{
		if (cue == null || _cueListScroll == null)
			return;
		// Defer one frame so selection chrome / layout settle after SelectIndividualShell.
		CallDeferred(nameof(EnsurePlayheadVisibleAfterGoDeferred), cue.Id);
	}

	/// <summary>
	/// Deferred body for <see cref="EnsurePlayheadVisibleAfterGo"/>.
	/// </summary>
	/// <param name="cueId">Id of the playhead cue.</param>
	private void EnsurePlayheadVisibleAfterGoDeferred(int cueId)
	{
		if (_cueListScroll == null || !IsInstanceValid(_cueListScroll))
			return;

		var cue = FetchCueFromId(cueId);
		if (cue == null)
			return;

		int vis = GetVisibleRowIndex(cueId);
		if (vis < 0)
		{
			ScrollToCueId(cueId);
			return;
		}

		float rowH = VirtualRowHeight;
		float viewH = _cueListScroll.Size.Y;
		float scrollY = _cueListScroll.ScrollVertical;
		float rowTop = vis * rowH;
		float rowBottom = rowTop + rowH;
		float viewTop = scrollY;
		float viewBottom = scrollY + viewH;
		float comfortBottom = viewBottom - Mathf.Max(1f, rowH * 0.5f);
		bool outsideAbove = rowBottom <= viewTop;
		bool outsideBelow = rowTop >= viewBottom;
		bool atOrPastBottom = rowBottom > comfortBottom;
		if (!outsideAbove && !outsideBelow && !atOrPastBottom)
			return;

		ScrollToCueId(cueId);
	}

	/// <summary>
	/// Scrolls the cuelist so <paramref name="control"/> is vertically centered in the viewport.
	/// </summary>
	/// <param name="control">Control to center (typically a shell row).</param>
	private void ScrollControlToVerticalCenter(Control control)
	{
		if (_cueListScroll == null || control == null || !IsInstanceValid(control))
			return;

		var scrollRect = _cueListScroll.GetGlobalRect();
		var controlRect = control.GetGlobalRect();
		float viewCenterY = scrollRect.Position.Y + scrollRect.Size.Y * 0.5f;
		float controlCenterY = controlRect.Position.Y + controlRect.Size.Y * 0.5f;
		float delta = controlCenterY - viewCenterY;

		int next = _cueListScroll.ScrollVertical + Mathf.RoundToInt(delta);
		var vBar = _cueListScroll.GetVScrollBar();
		if (vBar != null)
			next = (int)Mathf.Clamp(next, vBar.MinValue, vBar.MaxValue);
		else
			next = Mathf.Max(0, next);

		_cueListScroll.ScrollVertical = next;
	}

	/// <summary>
	/// Global rectangle of the cuelist scroll viewport (used by pick-target / overlay tools).
	/// </summary>
	/// <returns>Viewport bounds in global coordinates, or an empty rect if unavailable.</returns>
	public Rect2 GetCueListScrollGlobalRect()
	{
		if (_cueListScroll == null || !IsInstanceValid(_cueListScroll))
			return new Rect2();
		return _cueListScroll.GetGlobalRect();
	}

	/// <summary>
	/// Applies a mouse-wheel step to the cuelist. Positive <paramref name="steps"/> scrolls down.
	/// </summary>
	/// <param name="steps">Wheel notches (typically ±1, or <see cref="InputEventMouseButton.Factor"/>).</param>
	public void ScrollCueListByWheel(float steps)
	{
		if (_cueListScroll == null || !IsInstanceValid(_cueListScroll) || Mathf.IsZeroApprox(steps))
			return;
		float rowH = VirtualRowHeight;
		ScrollCueListByPixels(steps * rowH * 3f);
	}

	/// <summary>
	/// Adds <paramref name="deltaPx"/> to the cuelist vertical scroll (positive = down).
	/// </summary>
	/// <param name="deltaPx">Pixels to add.</param>
	public void ScrollCueListByPixels(float deltaPx)
	{
		ApplyCueListScrollDelta(deltaPx);
	}

	/// <summary>
	/// Scrolls the cuelist when the pointer is in the top or bottom edge band of the
	/// scroll viewport. Speed scales from 0 at the inner edge of the band to a maximum
	/// at the viewport edge (and just outside it).
	/// </summary>
	/// <param name="globalMouse">Pointer position in global coordinates.</param>
	/// <param name="delta">Frame delta in seconds.</param>
	public void AutoScrollCueListFromPointer(Vector2 globalMouse, double delta)
	{
		if (_cueListScroll == null || !IsInstanceValid(_cueListScroll) || delta <= 0)
			return;

		var view = _cueListScroll.GetGlobalRect();
		if (view.Size.Y < 8f)
			return;

		// Must be over the list horizontally (small slop for the scrollbar).
		if (globalMouse.X < view.Position.X - 8f ||
		    globalMouse.X > view.Position.X + view.Size.X + 8f)
			return;

		float zone = Mathf.Max(36f, VirtualRowHeight * 2.5f);
		zone = Mathf.Min(zone, view.Size.Y * 0.35f);
		float maxSpeed = Mathf.Max(220f, VirtualRowHeight * 22f);

		float top = view.Position.Y;
		float bottom = top + view.Size.Y;
		float dy = 0f;

		if (globalMouse.Y <= top + zone && globalMouse.Y >= top - zone)
		{
			float closeness = 1f - Mathf.Clamp((globalMouse.Y - top) / zone, 0f, 1f);
			dy = -maxSpeed * closeness;
		}
		else if (globalMouse.Y >= bottom - zone && globalMouse.Y <= bottom + zone)
		{
			float closeness = 1f - Mathf.Clamp((bottom - globalMouse.Y) / zone, 0f, 1f);
			dy = maxSpeed * closeness;
		}

		if (Mathf.IsZeroApprox(dy))
			return;
		ApplyCueListScrollDelta(dy * (float)delta);
	}

	/// <summary>
	/// Adds <paramref name="deltaPx"/> to the cuelist vertical scroll, clamped to the bar range.
	/// </summary>
	/// <param name="deltaPx">Pixels to add (positive = down).</param>
	private void ApplyCueListScrollDelta(float deltaPx)
	{
		if (_cueListScroll == null || !IsInstanceValid(_cueListScroll))
			return;

		int next = _cueListScroll.ScrollVertical + Mathf.RoundToInt(deltaPx);
		var vBar = _cueListScroll.GetVScrollBar();
		if (vBar != null)
			next = (int)Mathf.Clamp(next, vBar.MinValue, vBar.MaxValue);
		else
			next = Mathf.Max(0, next);
		_cueListScroll.ScrollVertical = next;
	}

	/// <summary>
	/// True when Show Mode is locking cue/cuelist document edits.
	/// </summary>
	private bool IsCueEditingLocked() =>
		_globalData?.Settings?.IsCueEditingLocked == true;

	/// <summary>
	/// Logs and returns true when a mutating cue operation should be blocked.
	/// </summary>
	/// <param name="actionLabel">Short action name for the log message.</param>
	/// <returns>True if the caller should abort.</returns>
	private bool BlockIfShowMode(string actionLabel)
	{
		if (!IsCueEditingLocked())
			return false;
		_globalSignals?.EmitSignal(nameof(GlobalSignals.Log),
			$"Show Mode: cannot {actionLabel}. Turn off Show Mode to edit cues.", (int)LogType.Info);
		return true;
	}

	/// <summary>
	/// Logs and returns true when another bulk cuelist operation is already running.
	/// </summary>
	/// <param name="actionLabel">Short action name for the log message.</param>
	/// <returns>True if the caller should abort.</returns>
	private bool BlockIfBulkBusy(string actionLabel)
	{
		if (_globalData?.IsSessionLoading == true)
		{
			_globalSignals?.EmitSignal(nameof(GlobalSignals.Log),
				$"Please wait — a showfile is still loading. Cannot {actionLabel}.", (int)LogType.Info);
			return true;
		}
		if (!_bulkOpInProgress)
			return false;
		_globalSignals?.EmitSignal(nameof(GlobalSignals.Log),
			$"Please wait — a cuelist operation is in progress. Cannot {actionLabel}.", (int)LogType.Info);
		return true;
	}

	private void OnShowModeChanged(bool enabled)
	{
		ApplyShowModeUi(enabled);
	}

	/// <summary>
	/// Disables Add Cue (and similar chrome) while Show Mode is active.
	/// </summary>
	private void ApplyShowModeUi(bool showMode)
	{
		if (_addCueButton != null)
			_addCueButton.Disabled = showMode;
	}

	/// <summary>
	/// Wires the cuelist header to mirror shell column chrome and user-resizable widths.
	/// Order matches rows: Color | Drag(Add) | Issue pad | Collapse(Expand) | Number | grip | Name | times.
	/// </summary>
	private void SetupShellColumnHeader()
	{
		ApplyHeaderChromeSizes();
		WireHeaderScrollbarPad();

		// Dedicated grip is more reliable than HSplit for this header (Godot 4.6 multi-split quirks).
		if (_numberNameResizeGrip != null)
		{
			_numberNameResizeGrip.MouseDefaultCursorShape = Control.CursorShape.Hsize;
			_numberNameResizeGrip.GuiInput += OnNumberNameGripGuiInput;
		}

		// Drag on duration header to resize all three time columns together.
		if (_durationHeaderLabel != null)
			_durationHeaderLabel.GuiInput += OnTimeHeaderGuiInput;

		ApplyHeaderColumnLayout();
	}

	/// <summary>
	/// Sizes header chrome (Add / Expand / pads) from current <see cref="ShellColumnLayout"/> scale.
	/// </summary>
	private void ApplyHeaderChromeSizes()
	{
		float headerBtnH = Mathf.Max(14f, 18f * ShellColumnLayout.Scale);
		float padH = Mathf.Max(12f, 15f * ShellColumnLayout.Scale);

		if (_addCueButton != null)
		{
			_addCueButton.CustomMinimumSize = new Vector2(ShellColumnLayout.DragWidth, headerBtnH);
			_addCueButton.AddThemeConstantOverride("icon_max_width", ShellColumnLayout.IconMaxWidth);
		}
		if (_expandAllButton != null)
		{
			_expandAllButton.CustomMinimumSize = new Vector2(ShellColumnLayout.CollapseWidth, headerBtnH);
			_expandAllButton.AddThemeConstantOverride("icon_max_width", ShellColumnLayout.IconMaxWidth);
		}

		// Color strip + nest gap so Number columns line up with shell rows.
		if (_headerColorPad != null)
			_headerColorPad.CustomMinimumSize = new Vector2(
				ShellColumnLayout.ColorWidth + ShellColumnLayout.ColorNestGap, padH);
		if (_headerIssuePad != null)
			_headerIssuePad.CustomMinimumSize = new Vector2(ShellColumnLayout.IssueWidth, padH);
	}

	private void OnShellColumnLayoutChanged()
	{
		if (!IsInstanceValid(this))
			return;
		// ShellBar instances subscribe to ShellColumnLayout.Changed themselves.
		// Scale changes also fire Changed — refresh header chrome + column labels.
		ApplyHeaderChromeSizes();
		ApplyHeaderColumnLayout();
		SyncVirtualViewport();
		// Only persist when user-resized widths changed (scale changes do not write prefs).
		// Persist is cheap and keeps number/time widths saved after drag end via grip handlers;
		// calling here on scale is harmless (same values).
		PersistShellColumnPrefs();
	}

	/// <summary>
	/// Applies current <see cref="ShellColumnLayout"/> widths and font scale to header labels.
	/// </summary>
	private void ApplyHeaderColumnLayout()
	{
		float numW = ShellColumnLayout.NumberWidth;
		float timeW = ShellColumnLayout.TimeWidth;
		float followW = ShellColumnLayout.FollowWidth;
		int headerFont = ShellColumnLayout.HeaderFontSize;

		if (_numberHeaderLabel != null)
		{
			_numberHeaderLabel.CustomMinimumSize = new Vector2(numW, 0);
			_numberHeaderLabel.AddThemeFontSizeOverride("font_size", headerFont);
		}
		if (_nameHeaderLabel != null)
			_nameHeaderLabel.AddThemeFontSizeOverride("font_size", headerFont);
		if (_preWaitHeaderLabel != null)
		{
			_preWaitHeaderLabel.CustomMinimumSize = new Vector2(timeW, 0);
			_preWaitHeaderLabel.AddThemeFontSizeOverride("font_size", headerFont);
		}
		if (_durationHeaderLabel != null)
		{
			_durationHeaderLabel.CustomMinimumSize = new Vector2(timeW, 0);
			_durationHeaderLabel.TooltipText = UiLocalizer.T("Drag horizontally to resize Pre-Wait / Duration / Post-Wait columns.");
			_durationHeaderLabel.MouseDefaultCursorShape = Control.CursorShape.Hsize;
			_durationHeaderLabel.AddThemeFontSizeOverride("font_size", headerFont);
		}
		if (_postWaitHeaderLabel != null)
		{
			_postWaitHeaderLabel.CustomMinimumSize = new Vector2(timeW, 0);
			_postWaitHeaderLabel.AddThemeFontSizeOverride("font_size", headerFont);
		}
		if (_followHeaderLabel != null)
		{
			_followHeaderLabel.CustomMinimumSize = new Vector2(followW, 0);
			_followHeaderLabel.AddThemeFontSizeOverride("font_size", headerFont);
			_followHeaderLabel.Text = "↳";
		}
		SyncHeaderScrollbarPad();
	}

	/// <summary>
	/// Subscribes to cuelist scrollbar size / visibility so the header can reserve matching width.
	/// </summary>
	private void WireHeaderScrollbarPad()
	{
		if (_headerScrollbarWired || _cueListScroll == null || !IsInstanceValid(_cueListScroll))
			return;

		_headerScrollbarWired = true;
		_cueListScroll.Resized += OnHeaderScrollbarLayoutChanged;
		var vBar = _cueListScroll.GetVScrollBar();
		if (vBar != null)
		{
			vBar.VisibilityChanged += OnHeaderScrollbarLayoutChanged;
			vBar.Resized += OnHeaderScrollbarLayoutChanged;
		}
		QueueSyncHeaderScrollbarPad();
	}

	/// <summary>
	/// Unsubscribes header scrollbar-pad listeners.
	/// </summary>
	private void UnwireHeaderScrollbarPad()
	{
		if (!_headerScrollbarWired || _cueListScroll == null || !IsInstanceValid(_cueListScroll))
		{
			_headerScrollbarWired = false;
			return;
		}

		_cueListScroll.Resized -= OnHeaderScrollbarLayoutChanged;
		var vBar = _cueListScroll.GetVScrollBar();
		if (vBar != null)
		{
			vBar.VisibilityChanged -= OnHeaderScrollbarLayoutChanged;
			vBar.Resized -= OnHeaderScrollbarLayoutChanged;
		}
		_headerScrollbarWired = false;
	}

	private void OnHeaderScrollbarLayoutChanged()
	{
		SyncHeaderScrollbarPad();
	}

	/// <summary>
	/// Defers pad sync one frame so ScrollContainer can show/hide the bar after content changes.
	/// </summary>
	private void QueueSyncHeaderScrollbarPad()
	{
		if (!IsInsideTree())
			return;
		CallDeferred(MethodName.SyncHeaderScrollbarPad);
	}

	/// <summary>
	/// Reserves header width matching the visible vertical scrollbar so Pre/Dur/Post/Follow
	/// line up with shell rows. Hidden when the bar is not taking layout space.
	/// </summary>
	private void SyncHeaderScrollbarPad()
	{
		if (_headerScrollPad == null || !IsInstanceValid(_headerScrollPad) || _cueListScroll == null)
			return;

		float barW = 0f;
		var vBar = _cueListScroll.GetVScrollBar();
		// Visible can stay true when the bar is unused; require a real scroll range.
		bool barNeeded = vBar != null && IsInstanceValid(vBar)
		                 && vBar.Visible
		                 && vBar.MaxValue > vBar.MinValue + vBar.Page + 0.5;
		if (barNeeded)
		{
			barW = vBar.Size.X;
			if (barW < 1f)
				barW = vBar.GetCombinedMinimumSize().X;
		}

		// Subtract the header HBox separation so TimeColumns + gap + pad == content + scrollbar.
		var header = _headerScrollPad.GetParent() as BoxContainer;
		int sep = header != null ? header.GetThemeConstant("separation") : 0;
		float pad = barW > 0.5f ? Mathf.Max(0f, barW - sep) : 0f;

		_headerScrollPad.Visible = pad > 0.5f;
		_headerScrollPad.CustomMinimumSize = new Vector2(pad, 0);
	}

	/// <summary>
	/// Drags the Number/Name boundary grip to resize the number column.
	/// </summary>
	private void OnNumberNameGripGuiInput(InputEvent @event)
	{
		if (@event is InputEventMouseButton mb && mb.ButtonIndex == MouseButton.Left)
		{
			_isDraggingNumberColumn = mb.Pressed;
			if (!mb.Pressed)
				PersistShellColumnPrefs();
			GetViewport().SetInputAsHandled();
			return;
		}

		if (@event is InputEventMouseMotion motion
		    && _isDraggingNumberColumn
		    && (motion.ButtonMask & MouseButtonMask.Left) != 0)
		{
			ShellColumnLayout.NumberWidth = ShellColumnLayout.NumberWidth + motion.Relative.X;
			GetViewport().SetInputAsHandled();
		}
	}

	/// <summary>
	/// Horizontal drag on the Duration header resizes the three timing columns together.
	/// </summary>
	private void OnTimeHeaderGuiInput(InputEvent @event)
	{
		if (@event is InputEventMouseMotion motion
		    && (motion.ButtonMask & MouseButtonMask.Left) != 0)
		{
			ShellColumnLayout.TimeWidth = ShellColumnLayout.TimeWidth + motion.Relative.X;
			GetViewport().SetInputAsHandled();
		}
	}

	/// <summary>
	/// Pushes shared column widths to every shell row in the hierarchy.
	/// </summary>
	private void ApplyColumnLayoutToAllShells()
	{
		foreach (int id in VisibleRowIds)
			FetchCueFromId(id)?.ShellBar?.ApplyColumnLayout();
	}

	/// <summary>
	/// Loads persisted shell column widths from user preferences when available.
	/// </summary>
	private void LoadShellColumnPrefs()
	{
		var udm = _globalData?.UserDataManager;
		if (udm == null)
			return;

		float number = udm.ShellNumberColumnWidth;
		float time = udm.ShellTimeColumnWidth;
		if (number > 0 || time > 0)
		{
			ShellColumnLayout.SetWidthsSilent(
				number > 0 ? number : ShellColumnLayout.DefaultNumberWidth,
				time > 0 ? time : ShellColumnLayout.DefaultTimeWidth);
		}
	}

	/// <summary>
	/// Saves current shell column widths into user preferences.
	/// </summary>
	private void PersistShellColumnPrefs()
	{
		var udm = _globalData?.UserDataManager;
		if (udm == null)
			return;
		udm.SetShellColumnWidths(ShellColumnLayout.NumberWidth, ShellColumnLayout.TimeWidth);
	}
}
