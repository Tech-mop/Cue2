// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Linq;
using Cue2.Domain.Cuelist;
using Cue2.Domain.Playback;
using Cue2.Domain.Devices;
using Cue2.Domain.ShowSettings;
using Cue2.Domain.Metadata;
using Cue2.Domain.Cues;
using Cue2.Domain.Connections;
using Cue2.Domain.Library;
using Cue2.Domain.Commands;
using Godot;
using Godot.Collections;

namespace Cue2.Services;

/// <summary>
/// Scoped undo/redo for document data changes (cues, cuelist structure, show settings)
/// and cuelist selection. Playback and app preferences are not tracked.
/// </summary>
/// <remarks>
/// Prefer the narrowest scope:
/// <list type="bullet">
/// <item><see cref="RecordCueChange"/> — single cue property/component edits (no display reload)</item>
/// <item><see cref="RecordCuelistChange"/> — create/delete/reorder/group (rebuilds shells only)</item>
/// <item><see cref="RecordSettingsChange"/> — settings slice only (omit Displays unless needed)</item>
/// <item><see cref="RecordSelectionChange"/> — shell selection / focus only</item>
/// </list>
/// </remarks>
public partial class HistoryManager : Node
{
	/// <summary>
	/// History entry scope controls what is captured and how restore runs.
	/// </summary>
	public enum HistoryScope
	{
		/// <summary>Single cue <see cref="Cue.GetData"/> snapshot.</summary>
		Cue = 0,
		/// <summary>Full cuelist (order + all cues), no settings.</summary>
		Cuelist = 1,
		/// <summary>Settings keys only (optionally filtered).</summary>
		Settings = 2,
		/// <summary>Selected cue ids + focused cue only (no document mutation).</summary>
		Selection = 3,
		/// <summary>
		/// Multiple cues only (id → <see cref="Cue.GetData"/>). Used for multi-edit so undo
		/// does not free/rebuild the entire cuelist UI.
		/// </summary>
		MultiCue = 4
	}

	private GlobalData _globalData;
	private GlobalSignals _globalSignals;

	private readonly List<HistoryEntry> _undoStack = new();
	private readonly List<HistoryEntry> _redoStack = new();
	private bool _isRestoring;

	/// <summary>Document-edit generation (cue / cuelist / settings). Selection is ignored.</summary>
	private int _documentRevision;

	/// <summary>Revision last written to disk (or 0 after New / Open).</summary>
	private int _savedRevision;

	/// <summary>
	/// Active continuous-edit session key (typing / drag / spin). Only merges while this is set
	/// and matches the incoming key. Discrete records (null key) or <see cref="EndCoalesceSession"/>
	/// close the session so the next edit of the same field becomes a new undo step.
	/// </summary>
	private string _activeCoalesceKey;

	/// <summary>
	/// Raised when undo/redo availability or stack contents change (including new records).
	/// </summary>
	[Signal]
	public delegate void HistoryChangedEventHandler();

	/// <summary>
	/// Raised only after a successful Undo or Redo has finished applying restored state.
	/// Argument is <see cref="HistoryScope"/> as int so listeners can refresh only when relevant
	/// (e.g. settings panels should ignore cue undos so SpinBoxes are not re-synced spuriously).
	/// </summary>
	[Signal]
	public delegate void HistoryRestoredEventHandler(int scope);

	/// <summary>
	/// Whether there is at least one undo step that can actually apply right now.
	/// In Show Mode, cue/cuelist steps are skipped (menu stays disabled if only those remain).
	/// </summary>
	public bool CanUndo => _globalData?.IsSessionLoading != true && FindApplicableIndex(_undoStack) >= 0;

	/// <summary>
	/// Whether there is at least one redo step that can actually apply right now.
	/// </summary>
	public bool CanRedo => _globalData?.IsSessionLoading != true && FindApplicableIndex(_redoStack) >= 0;

	/// <summary>
	/// Maximum undo steps, from user preferences.
	/// </summary>
	public int MaxDepth
	{
		get
		{
			var depth = _globalData?.UserDataManager?.UndoDepth ?? UserDataManager.DefaultUndoDepth;
			return Math.Clamp(depth, UserDataManager.MinUndoDepth, UserDataManager.MaxUndoDepth);
		}
	}

	/// <summary>
	/// True while a restore is in progress (suppresses nested recording).
	/// </summary>
	public bool IsRestoring => _isRestoring;

	/// <summary>
	/// True when cue, cuelist, or show-settings data has changed since the last save (or New / Open).
	/// Selection-only history does not count.
	/// </summary>
	public bool HasUnsavedDocumentChanges => _documentRevision != _savedRevision;

	/// <summary>
	/// Marks the current document revision as matching disk (after a successful Save / Save As,
	/// or after New / Open has loaded a clean session).
	/// </summary>
	public void MarkSaved()
	{
		_savedRevision = _documentRevision;
	}

	public override void _Ready()
	{
		_globalData = GetParent() as GlobalData ?? GetNode<GlobalData>("/root/GlobalData");
		_globalSignals = GetNode<GlobalSignals>("/root/GlobalSignals");

		_globalSignals.Undo += Undo;
		_globalSignals.Redo += Redo;
		// Show Mode changes which scopes are applicable — refresh Edit menu CanUndo/CanRedo.
		_globalSignals.ShowModeChanged += OnShowModeChanged;
		_globalSignals.SessionLoadStarted += OnSessionLoadStarted;
		_globalSignals.SessionLoadFinished += OnSessionLoadFinished;

		GD.Print("HistoryManager:_Ready - Initialized (scoped history).");
	}

	public override void _ExitTree()
	{
		if (_globalSignals != null)
		{
			_globalSignals.Undo -= Undo;
			_globalSignals.Redo -= Redo;
			_globalSignals.ShowModeChanged -= OnShowModeChanged;
			_globalSignals.SessionLoadStarted -= OnSessionLoadStarted;
			_globalSignals.SessionLoadFinished -= OnSessionLoadFinished;
		}
	}

	private void OnShowModeChanged(bool _)
	{
		EmitSignal(SignalName.HistoryChanged);
	}

	private void OnSessionLoadStarted(string showName)
	{
		EmitSignal(SignalName.HistoryChanged);
	}

	private void OnSessionLoadFinished()
	{
		EmitSignal(SignalName.HistoryChanged);
	}

	/// <summary>
	/// Records a single-cue checkpoint before a property or component mutation.
	/// </summary>
	/// <param name="cueId">Cue being edited.</param>
	/// <param name="description">Readable description.</param>
	/// <param name="coalesceKey">
	/// Optional key for a continuous edit session (typing, drag, spin). While the same session
	/// is open, further records with this key do not push steps. Pass null for discrete commits
	/// so each change is one undo step. Call <see cref="EndCoalesceSession"/> when the continuous
	/// interaction ends (focus exit / mouse up).
	/// </param>
	public void RecordCueChange(int cueId, string description, string coalesceKey = null)
	{
		if (_isRestoring) return;
		// Show Mode locks cue document edits — do not push history for blocked mutations.
		if (_globalData?.Settings?.IsCueEditingLocked == true) return;
		if (_globalData?.Cuelist == null) return;

		var cue = CueList.FetchCueFromId(cueId);
		if (cue == null)
		{
			GD.PrintErr($"HistoryManager:RecordCueChange - Cue {cueId} not found; skipping.");
			return;
		}

		if (ShouldCoalesce(coalesceKey))
			return;

		try
		{
			PushUndo(CaptureCueEntry(cueId, description ?? "Edit cue", coalesceKey), coalesceKey);
		}
		catch (Exception ex)
		{
			LogRecordFailure(ex);
		}
	}

	/// <summary>
	/// Records a full cuelist checkpoint before structural mutations (create/delete/reorder/group).
	/// Does not capture or restore show settings / displays.
	/// </summary>
	/// <param name="description">Readable description.</param>
	/// <param name="coalesceKey">Optional continuous-session key; usually null for structural ops.</param>
	public void RecordCuelistChange(string description, string coalesceKey = null)
	{
		if (_isRestoring) return;
		if (_globalData?.Settings?.IsCueEditingLocked == true) return;
		if (_globalData?.Cuelist == null) return;
		if (ShouldCoalesce(coalesceKey)) return;

		try
		{
			PushUndo(CaptureCuelistEntry(description ?? "Edit cuelist"), coalesceKey);
		}
		catch (Exception ex)
		{
			LogRecordFailure(ex);
		}
	}

	/// <summary>
	/// Records snapshots for a set of cues only (multi-edit). Restores in place without
	/// rebuilding the full cuelist shell tree.
	/// </summary>
	/// <param name="cueIds">Cue ids to capture (duplicates ignored).</param>
	/// <param name="description">Readable undo label.</param>
	/// <param name="coalesceKey">Optional continuous-session key.</param>
	public void RecordMultiCueChange(IEnumerable<int> cueIds, string description, string coalesceKey = null)
	{
		if (_isRestoring) return;
		if (_globalData?.Settings?.IsCueEditingLocked == true) return;
		if (_globalData?.Cuelist == null) return;
		if (cueIds == null) return;
		if (ShouldCoalesce(coalesceKey)) return;

		try
		{
			var entry = CaptureMultiCueEntry(cueIds, description ?? "Multi-edit cues", coalesceKey);
			if (entry == null)
				return;
			PushUndo(entry, coalesceKey);
		}
		catch (Exception ex)
		{
			LogRecordFailure(ex);
		}
	}

	/// <summary>
	/// Records a settings checkpoint. Pass <paramref name="keys"/> to capture only those keys
	/// (e.g. <c>StopFadeDuration</c>) so restore will not reload displays or unrelated systems.
	/// </summary>
	/// <param name="description">Readable description.</param>
	/// <param name="coalesceKey">Optional continuous-session key (e.g. spinning a value).</param>
	/// <param name="keys">
	/// Required settings key(s). Empty/null is refused. For a rare full settings
	/// memento use <see cref="Settings.HistoryFullSnapshotKey"/> (<c>"*"</c>).
	/// </param>
	public void RecordSettingsChange(string description, string coalesceKey = null, params string[] keys)
	{
		if (_isRestoring) return;
		if (_globalData?.Settings == null) return;
		if (ShouldCoalesce(coalesceKey)) return;

		if (keys == null || keys.Length == 0 || !HasAnyNonEmptyKey(keys))
		{
			GD.PrintErr(
				$"HistoryManager:RecordSettingsChange - Refused empty keys for '{description}' " +
				"(would capture full settings). Pass explicit keys or \"*\".");
			System.Diagnostics.Debug.Assert(false,
				"HistoryManager.RecordSettingsChange: keys required. Use Settings.HistoryFullSnapshotKey (\"*\") for full snapshot.");
			return;
		}

		try
		{
			var slice = _globalData.Settings.CaptureHistorySlice(keys);
			// Scalar general-settings slices are cloned without JSON (avoids empty/corrupt snapshots).
			// Full snapshot ("*") is never treated as a scalar slice.
			bool fullSnapshot = ContainsFullSnapshotKey(keys);
			var stored = !fullSnapshot && IsScalarSettingsSlice(keys)
				? CloneScalarSettingsSlice(slice)
				: DeepCloneDictionary(slice);

			if (stored == null || stored.Count == 0)
			{
				GD.PrintErr($"HistoryManager:RecordSettingsChange - Empty settings slice for '{description}'; keys={string.Join(",", keys)}");
				return;
			}

			var (selectedIds, focusedId) = CaptureSelectionState();
			var entry = new HistoryEntry(
				description ?? "Edit settings",
				coalesceKey,
				HistoryScope.Settings,
				-1,
				null,
				null,
				stored,
				selectedIds,
				focusedId);

			PushUndo(entry, coalesceKey);
		}
		catch (Exception ex)
		{
			LogRecordFailure(ex);
		}
	}

	private static bool HasAnyNonEmptyKey(string[] keys)
	{
		if (keys == null) return false;
		foreach (var k in keys)
		{
			if (!string.IsNullOrEmpty(k))
				return true;
		}
		return false;
	}

	private static bool ContainsFullSnapshotKey(string[] keys)
	{
		if (keys == null) return false;
		foreach (var k in keys)
		{
			if (k == Settings.HistoryFullSnapshotKey)
				return true;
		}
		return false;
	}

	/// <summary>
	/// After an external settings import (e.g. loading a .c2settings file).
	/// Relinks cue components when audio patches were replaced and notifies UI panels
	/// via <see cref="HistoryRestored"/> (same path as undo/redo of settings).
	/// </summary>
	/// <remarks>
	/// Call after <see cref="RecordSettingsChange"/> + <see cref="Settings.ApplyPartialFromHistory"/>.
	/// Does not push history — the caller is responsible for the pre-change memento.
	/// </remarks>
	/// <param name="keys">Settings keys that were applied (used to decide whether to relink patches).</param>
	public void NotifySettingsApplied(params string[] keys)
	{
		if (keys != null && keys.Any(k => string.Equals(k, "AudioPatch", StringComparison.Ordinal)))
			RelinkAllCueComponents();

		EmitSignal(SignalName.HistoryChanged);
		EmitSignal(SignalName.HistoryRestored, (int)HistoryScope.Settings);
		GD.Print($"HistoryManager:NotifySettingsApplied - keys=[{string.Join(", ", keys ?? System.Array.Empty<string>())}]");
	}

	/// <summary>
	/// Ends a continuous edit session so the next change (even with the same field key)
	/// becomes a new undo step. Call on text focus exit, mouse-up after a drag, etc.
	/// </summary>
	/// <param name="coalesceKey">
	/// If set, only ends the session when it matches the active key.
	/// If null, ends any active continuous session.
	/// </param>
	public void EndCoalesceSession(string coalesceKey = null)
	{
		if (string.IsNullOrEmpty(_activeCoalesceKey))
			return;
		if (coalesceKey != null && _activeCoalesceKey != coalesceKey)
			return;

		_activeCoalesceKey = null;
	}

	/// <summary>
	/// Records a selection/focus checkpoint before a pure selection change (click, range, next/prev).
	/// Does not capture cue data or settings. Call before mutating <see cref="ShellSelection"/>.
	/// </summary>
	/// <param name="description">Readable description (e.g. "Select cue").</param>
	/// <param name="coalesceKey">Optional continuous-session key; usually null for discrete clicks.</param>
	public void RecordSelectionChange(string description, string coalesceKey = null)
	{
		if (_isRestoring) return;
		if (ShouldCoalesce(coalesceKey)) return;

		try
		{
			PushUndo(CaptureSelectionEntry(description ?? "Select cues", coalesceKey), coalesceKey);
		}
		catch (Exception ex)
		{
			LogRecordFailure(ex);
		}
	}

	/// <summary>
	/// Restores the previous applicable scoped state from the undo stack.
	/// In Show Mode, walks past cue/cuelist steps (left on the stack for Edit Mode) to the next
	/// settings/selection entry so Undo is never a silent no-op while the menu claims it can undo.
	/// </summary>
	public void Undo()
	{
		if (_isRestoring) return;
		if (_globalData?.IsSessionLoading == true)
		{
			_globalSignals?.EmitSignal(nameof(GlobalSignals.Log),
				"Please wait — a showfile is still loading. Cannot undo.", (int)LogType.Info);
			return;
		}

		int idx = FindApplicableIndex(_undoStack);
		if (idx < 0) return;

		HistoryEntry target = null;
		HistoryEntry redoEntry = null;
		bool popped = false;
		try
		{
			_activeCoalesceKey = null;
			target = _undoStack[idx];

			if (idx < _undoStack.Count - 1)
			{
				int skipped = _undoStack.Count - 1 - idx;
				GD.Print($"HistoryManager:Undo - Skipping {skipped} Show Mode-locked step(s) above '{target.Description}'");
			}

			// Capture redo state BEFORE popping, so a capture failure cannot discard the undo entry.
			redoEntry = CaptureCurrentForScope(target);

			_undoStack.RemoveAt(idx);
			popped = true;
			_redoStack.Add(redoEntry);

			RestoreEntry(target);
			if (IsDocumentScope(target.Scope))
				_documentRevision--;

			_globalSignals?.EmitSignal(nameof(GlobalSignals.Log),
				$"Undo: {target.Description}", (int)LogType.Info);
			GD.Print($"HistoryManager:Undo - '{target.Description}' scope={target.Scope} (undo={_undoStack.Count}, redo={_redoStack.Count})");
			EmitSignal(SignalName.HistoryChanged);
			EmitSignal(SignalName.HistoryRestored, (int)target.Scope);
		}
		catch (Exception ex)
		{
			// Restore stack integrity if we already moved the entry.
			if (popped && target != null)
			{
				if (_redoStack.Count > 0 && ReferenceEquals(_redoStack[^1], redoEntry))
					_redoStack.RemoveAt(_redoStack.Count - 1);
				// Re-insert at original index when possible.
				if (idx >= 0 && idx <= _undoStack.Count)
					_undoStack.Insert(idx, target);
				else
					_undoStack.Add(target);
			}

			GD.PrintErr($"HistoryManager:Undo - Failed: {ex.Message}\n{ex.StackTrace}");
			_globalSignals?.EmitSignal(nameof(GlobalSignals.Log),
				$"Undo failed: {ex.Message}", (int)LogType.Error);
			_isRestoring = false;
			EmitSignal(SignalName.HistoryChanged);
		}
	}

	/// <summary>
	/// Re-applies a previously undone scoped state (skips Show Mode-blocked cue/cuelist steps).
	/// </summary>
	public void Redo()
	{
		if (_isRestoring) return;
		if (_globalData?.IsSessionLoading == true)
		{
			_globalSignals?.EmitSignal(nameof(GlobalSignals.Log),
				"Please wait — a showfile is still loading. Cannot redo.", (int)LogType.Info);
			return;
		}

		int idx = FindApplicableIndex(_redoStack);
		if (idx < 0) return;

		HistoryEntry target = null;
		HistoryEntry undoEntry = null;
		bool popped = false;
		try
		{
			_activeCoalesceKey = null;
			target = _redoStack[idx];

			if (idx < _redoStack.Count - 1)
			{
				int skipped = _redoStack.Count - 1 - idx;
				GD.Print($"HistoryManager:Redo - Skipping {skipped} Show Mode-locked step(s) above '{target.Description}'");
			}

			undoEntry = CaptureCurrentForScope(target);

			_redoStack.RemoveAt(idx);
			popped = true;
			_undoStack.Add(undoEntry);
			TrimToMaxDepth();

			RestoreEntry(target);
			if (IsDocumentScope(target.Scope))
				_documentRevision++;

			_globalSignals?.EmitSignal(nameof(GlobalSignals.Log),
				$"Redo: {target.Description}", (int)LogType.Info);
			GD.Print($"HistoryManager:Redo - '{target.Description}' scope={target.Scope} (undo={_undoStack.Count}, redo={_redoStack.Count})");
			EmitSignal(SignalName.HistoryChanged);
			EmitSignal(SignalName.HistoryRestored, (int)target.Scope);
		}
		catch (Exception ex)
		{
			if (popped && target != null)
			{
				if (_undoStack.Count > 0 && ReferenceEquals(_undoStack[^1], undoEntry))
					_undoStack.RemoveAt(_undoStack.Count - 1);
				if (idx >= 0 && idx <= _redoStack.Count)
					_redoStack.Insert(idx, target);
				else
					_redoStack.Add(target);
			}

			GD.PrintErr($"HistoryManager:Redo - Failed: {ex.Message}\n{ex.StackTrace}");
			_globalSignals?.EmitSignal(nameof(GlobalSignals.Log),
				$"Redo failed: {ex.Message}", (int)LogType.Error);
			_isRestoring = false;
			EmitSignal(SignalName.HistoryChanged);
		}
	}

	/// <summary>
	/// Clears both undo and redo stacks (e.g. on New Session or Open Session).
	/// </summary>
	public void Clear()
	{
		_activeCoalesceKey = null;
		_undoStack.Clear();
		_redoStack.Clear();
		_documentRevision = 0;
		_savedRevision = 0;
		EmitSignal(SignalName.HistoryChanged);
		GD.Print("HistoryManager:Clear - History cleared.");
	}

	/// <summary>
	/// Index of the topmost stack entry that Undo/Redo may apply now, or -1 if none.
	/// Skips Show Mode-blocked cue/cuelist steps and bulk-op-busy scopes.
	/// </summary>
	private int FindApplicableIndex(List<HistoryEntry> stack)
	{
		if (stack == null || stack.Count == 0)
			return -1;
		for (int i = stack.Count - 1; i >= 0; i--)
		{
			var scope = stack[i].Scope;
			if (IsCueScopeBlockedInShowMode(scope))
				continue;
			if (IsCuelistBulkBusy(scope))
				continue;
			return i;
		}
		return -1;
	}

	/// <summary>
	/// Trims the undo stack to the current <see cref="MaxDepth"/> and clears redo when shrinking.
	/// </summary>
	public void TrimToMaxDepth()
	{
		int max = MaxDepth;
		bool changed = false;

		while (_undoStack.Count > max)
		{
			_undoStack.RemoveAt(0);
			changed = true;
		}

		if (changed && _redoStack.Count > 0)
		{
			_redoStack.Clear();
			changed = true;
		}

		if (changed)
			EmitSignal(SignalName.HistoryChanged);
	}

	/// <summary>
	/// True only while a continuous session is open for this key (typing/drag/spin).
	/// Discrete records pass null and never coalesce.
	/// </summary>
	private bool ShouldCoalesce(string coalesceKey)
	{
		return !string.IsNullOrEmpty(coalesceKey)
		       && !string.IsNullOrEmpty(_activeCoalesceKey)
		       && _activeCoalesceKey == coalesceKey;
	}

	/// <summary>
	/// Whether undoing/redoing this scope would change cues or cuelist structure while Show Mode is on.
	/// </summary>
	private bool IsCueScopeBlockedInShowMode(HistoryScope scope)
	{
		if (_globalData?.Settings?.IsCueEditingLocked != true)
			return false;
		return scope is HistoryScope.Cue or HistoryScope.Cuelist or HistoryScope.MultiCue;
	}

	/// <summary>
	/// True when a frame-sliced bulk cuelist mutation is running and restore would race shell creation.
	/// Blocks Cue / Cuelist / MultiCue scopes (all touch shells); settings undos remain allowed.
	/// </summary>
	private bool IsCuelistBulkBusy(HistoryScope scope)
	{
		if (scope is not (HistoryScope.Cue or HistoryScope.Cuelist or HistoryScope.MultiCue))
			return false;
		return _globalData?.Cuelist?.IsBulkOpInProgress == true;
	}

	private void PushUndo(HistoryEntry entry, string coalesceKey)
	{
		_undoStack.Add(entry);
		_redoStack.Clear();
		// Open or close continuous session: null key = discrete commit, seals any prior session.
		_activeCoalesceKey = string.IsNullOrEmpty(coalesceKey) ? null : coalesceKey;
		if (IsDocumentScope(entry.Scope))
			_documentRevision++;
		TrimToMaxDepth();
		EmitSignal(SignalName.HistoryChanged);
		GD.Print($"HistoryManager:Push - '{entry.Description}' scope={entry.Scope} (undo={_undoStack.Count})");
	}

	private static bool IsDocumentScope(HistoryScope scope) =>
		scope is HistoryScope.Cue or HistoryScope.Cuelist or HistoryScope.Settings or HistoryScope.MultiCue;

	private void LogRecordFailure(Exception ex)
	{
		GD.PrintErr($"HistoryManager:Record - Failed: {ex.Message}");
		_globalSignals?.EmitSignal(nameof(GlobalSignals.Log),
			$"HistoryManager: Failed to record history: {ex.Message}", (int)LogType.Error);
	}

	/// <summary>
	/// Captures live state matching the scope of an existing history entry (for the opposite stack).
	/// </summary>
	private HistoryEntry CaptureCurrentForScope(HistoryEntry template)
	{
		return template.Scope switch
		{
			HistoryScope.Cue => CaptureCueEntry(template.CueId, template.Description, null),
			HistoryScope.Cuelist => CaptureCuelistEntry(template.Description),
			HistoryScope.MultiCue => CaptureMultiCueEntry(
				EnumerateMultiCueIds(template.CuesData),
				template.Description,
				null) ?? throw new InvalidOperationException(
					"Cannot capture multi-cue history: no matching live cues."),
			HistoryScope.Settings => CaptureSettingsEntry(
				template.Description,
				// Capture the same key set that was stored on the template (never full GetData).
				CaptureSettingsMatchingKeys(template.SettingsData)),
			HistoryScope.Selection => CaptureSelectionEntry(template.Description, null),
			_ => throw new InvalidOperationException($"Unknown history scope: {template.Scope}")
		};
	}

	private HistoryEntry CaptureCueEntry(int cueId, string description, string coalesceKey)
	{
		var cue = CueList.FetchCueFromId(cueId);
		if (cue == null)
			throw new InvalidOperationException($"Cannot capture history for missing cue {cueId}");

		// Strip large regenerable payloads BEFORE clone — never JSON-copy waveform peaks.
		var data = cue.GetData();
		StripWaveformPayloads(data);
		data = DeepCloneDictionary(data);

		var (selectedIds, focusedId) = CaptureSelectionState();
		return new HistoryEntry(
			description,
			coalesceKey,
			HistoryScope.Cue,
			cueId,
			data,
			null,
			null,
			selectedIds,
			focusedId);
	}

	private HistoryEntry CaptureCuelistEntry(string description)
	{
		// Build live snapshot, strip waveforms on each cue, then structural-clone (P2-09).
		var live = _globalData.Cuelist.GetData();
		if (live.TryGetValue("Cues", out var cuesVariant) && cuesVariant.VariantType == Variant.Type.Dictionary)
		{
			foreach (var kv in (Dictionary)cuesVariant)
			{
				if (kv.Value.VariantType == Variant.Type.Dictionary)
					StripWaveformPayloads(kv.Value.AsGodotDictionary());
			}
		}

		var cues = DeepCloneDictionary(live);

		var (selectedIds, focusedId) = CaptureSelectionState();
		return new HistoryEntry(
			description,
			null,
			HistoryScope.Cuelist,
			-1,
			null,
			cues,
			null,
			selectedIds,
			focusedId);
	}

	/// <summary>
	/// Captures only the listed cues (multi-edit). Returns null when no valid cues were found.
	/// </summary>
	private HistoryEntry CaptureMultiCueEntry(IEnumerable<int> cueIds, string description, string coalesceKey)
	{
		if (cueIds == null)
			return null;

		var map = new Dictionary();
		var seen = new HashSet<int>();
		foreach (int cueId in cueIds)
		{
			if (cueId < 0 || !seen.Add(cueId))
				continue;
			var cue = CueList.FetchCueFromId(cueId);
			if (cue == null)
				continue;
			var data = cue.GetData();
			StripWaveformPayloads(data);
			map[cueId.ToString()] = DeepCloneDictionary(data);
		}

		if (map.Count == 0)
		{
			GD.PrintErr("HistoryManager:CaptureMultiCueEntry - No valid cues; skipping.");
			return null;
		}

		var (selectedIds, focusedId) = CaptureSelectionState();
		return new HistoryEntry(
			description,
			coalesceKey,
			HistoryScope.MultiCue,
			-1,
			null,
			map,
			null,
			selectedIds,
			focusedId);
	}

	private static IEnumerable<int> EnumerateMultiCueIds(Dictionary multiMap)
	{
		if (multiMap == null)
			yield break;
		foreach (var key in multiMap.Keys)
		{
			string s = key.AsString();
			if (int.TryParse(s, out int id))
				yield return id;
		}
	}

	private HistoryEntry CaptureSettingsEntry(string description, Dictionary settingsData)
	{
		var (selectedIds, focusedId) = CaptureSelectionState();
		return new HistoryEntry(
			description,
			null,
			HistoryScope.Settings,
			-1,
			null,
			null,
			settingsData,
			selectedIds,
			focusedId);
	}

	private HistoryEntry CaptureSelectionEntry(string description, string coalesceKey)
	{
		var (selectedIds, focusedId) = CaptureSelectionState();
		return new HistoryEntry(
			description,
			coalesceKey,
			HistoryScope.Selection,
			-1,
			null,
			null,
			null,
			selectedIds,
			focusedId);
	}

	/// <summary>
	/// Snapshots current shell multi-selection (ordered) and focused cue id for history restore.
	/// </summary>
	private (int[] SelectedCueIds, int FocusedCueId) CaptureSelectionState()
	{
		int focusedId = _globalData?.FocusedCue ?? -1;
		var selected = ShellSelection.SelectedCues;
		if (selected == null || selected.Count == 0)
			return (System.Array.Empty<int>(), focusedId);

		// Preserve list order — last entry is "most recently selected" (paste/duplicate anchor).
		var ids = new int[selected.Count];
		int n = 0;
		foreach (var cue in selected)
		{
			if (cue == null) continue;
			ids[n++] = cue.Id;
		}
		if (n == ids.Length)
			return (ids, focusedId);
		if (n == 0)
			return (System.Array.Empty<int>(), focusedId);
		var trimmed = new int[n];
		System.Array.Copy(ids, trimmed, n);
		return (trimmed, focusedId);
	}

	/// <summary>
	/// Restores shell selection and focus from a history memento after model apply.
	/// Missing cue ids (deleted / not yet recreated) are skipped.
	/// </summary>
	private void RestoreSelection(HistoryEntry entry)
	{
		if (entry == null) return;

		// Drop current selection chrome (shells may be freed after cuelist rebuild).
		foreach (var cue in ShellSelection.SelectedCues.ToList())
		{
			if (cue?.ShellBar != null && IsInstanceValid(cue.ShellBar))
				cue.ShellBar.Deselect();
		}
		ShellSelection.SelectedCues.Clear();

		if (entry.SelectedCueIds != null)
		{
			foreach (int id in entry.SelectedCueIds)
			{
				var cue = CueList.FetchCueFromId(id);
				if (cue?.ShellBar == null || !IsInstanceValid(cue.ShellBar))
					continue;
				cue.ShellBar.Select();
				ShellSelection.SelectedCues.Add(cue);
			}
		}

		int focusId = entry.FocusedCueId;
		if (focusId >= 0 && CueList.FetchCueFromId(focusId) == null)
			focusId = -1;
		if (focusId < 0 && ShellSelection.SelectedCues.Count > 0)
			focusId = ShellSelection.SelectedCues[^1].Id;

		// Always publish so FocusedCue / inspectors match selection (including empty).
		_globalSignals?.EmitSignal(nameof(GlobalSignals.ShellFocused), focusId);
	}

	/// <summary>
	/// Clears embedded WaveformData from a cue snapshot dictionary (in place).
	/// Waveform peaks regenerate on demand after restore — keep them out of history mementos.
	/// </summary>
	/// <remarks>
	/// Call <b>before</b> <see cref="DeepCloneDictionary"/> so large buffers are never copied.
	/// Mutates only the snapshot dictionary from <see cref="Cue.GetData"/>, not live component fields
	/// (GetData boxes a new dictionary; replacing the entry does not clear the component's array).
	/// </remarks>
	private static void StripWaveformPayloads(Dictionary cueData)
	{
		if (cueData == null || !cueData.ContainsKey("Components")) return;
		var comps = cueData["Components"].AsGodotArray();
		foreach (var compVar in comps)
		{
			if (compVar.VariantType != Variant.Type.Dictionary) continue;
			var comp = compVar.AsGodotDictionary();
			if (comp.ContainsKey("WaveformData"))
				comp["WaveformData"] = System.Array.Empty<byte>();
		}
	}

	/// <summary>
	/// Captures current settings values for exactly the keys present in a previous settings snapshot.
	/// Never falls back to full GetData() (that can throw / hang and used to discard undo entries).
	/// </summary>
	private Dictionary CaptureSettingsMatchingKeys(Dictionary previousSlice)
	{
		if (previousSlice == null || previousSlice.Count == 0)
			return new Dictionary();

		var keys = previousSlice.Keys.Select(k => k.AsString()).Where(k => !string.IsNullOrEmpty(k)).ToArray();
		if (keys.Length == 0)
			return new Dictionary();

		var slice = _globalData.Settings.CaptureHistorySlice(keys);
		return IsScalarSettingsSlice(keys)
			? CloneScalarSettingsSlice(slice)
			: DeepCloneDictionary(slice);
	}

	private static readonly HashSet<string> ScalarSettingsKeys = new(StringComparer.Ordinal)
	{
		"GoScale",
		"CueListScale",
		"WaveformResolution",
		"StopFadeDuration",
		"MediaBackupEnabled",
		"MultiEditEnabled",
		"SelectNewCues",
		"ShowMode",
		"ShowTimelineWaveforms",
		"OutputBackgroundColor",
		"VideoQualityMode",
		"VideoPreviewQuality",
		"OutputVSyncMode",
		"AudioLatencyMode",
		"AudioDeclickMs",
		"AudioMasterVolume"
	};

	private static bool IsScalarSettingsSlice(string[] keys)
	{
		if (keys == null || keys.Length == 0) return false;
		foreach (var key in keys)
		{
			if (string.IsNullOrEmpty(key) || !ScalarSettingsKeys.Contains(key))
				return false;
		}
		return true;
	}

	private static bool NeedsDeepSettingsClone(Dictionary settingsData)
	{
		if (settingsData == null) return false;
		foreach (var k in settingsData.Keys)
		{
			string key = k.AsString();
			if (string.IsNullOrEmpty(key) || !ScalarSettingsKeys.Contains(key))
				return true;
		}
		return false;
	}

	/// <summary>
	/// Copies primitive settings values without JSON (stable for floats/bools/ints).
	/// </summary>
	private static Dictionary CloneScalarSettingsSlice(Dictionary source)
	{
		var clone = new Dictionary();
		if (source == null) return clone;
		foreach (var kvp in source)
		{
			string key = kvp.Key.AsString();
			if (string.IsNullOrEmpty(key)) continue;
			// Re-box as plain managed values so later restore does not depend on Variant lifetime.
			var v = kvp.Value;
			clone[key] = v.VariantType switch
			{
				Variant.Type.Float => v.AsSingle(),
				Variant.Type.Int => v.AsInt32(),
				Variant.Type.Bool => v.AsBool(),
				Variant.Type.String => v.AsString(),
				_ => v
			};
		}
		return clone;
	}

	private void RestoreEntry(HistoryEntry entry)
	{
		_isRestoring = true;
		try
		{
			switch (entry.Scope)
			{
				case HistoryScope.Cue:
					// Stack mementos are exclusive copies; do not deep-clone again on restore.
					_globalData.Cuelist.ApplyCueHistorySnapshot(entry.CueId, entry.CueData);
					break;

				case HistoryScope.Cuelist:
					_globalData.Cuelist.ApplyCuelistHistorySnapshot(entry.CuesData);
					break;

				case HistoryScope.MultiCue:
					_globalData.Cuelist.ApplyMultiCueHistorySnapshot(entry.CuesData);
					break;

				case HistoryScope.Settings:
					// Scalar slices are already plain dictionaries; deep-clone nested snapshots (patches, OSC/MIDI maps…).
					// Settings apply mutates nested structures in place for some keys — clone when nested.
					var settingsData = entry.SettingsData;
					if (settingsData != null && NeedsDeepSettingsClone(settingsData))
						settingsData = DeepCloneDictionary(settingsData);
					else if (settingsData != null)
						settingsData = CloneScalarSettingsSlice(settingsData);

					_globalData.Settings.ApplyPartialFromHistory(settingsData);
					// Patches are freed and recreated — re-link cue components and refresh inspectors
					// that list patch names / hold live AudioOutputPatch references.
					if (entry.SettingsData != null && entry.SettingsData.ContainsKey("AudioPatch"))
						RelinkAllCueComponents();
					// Displays / AudioPatch inspector refresh happens via RestoreSelection → ShellFocused.
					break;

				case HistoryScope.Selection:
					// Selection-only memento — no document apply.
					break;
			}

			// Always re-apply selection last so cuelist rebuilds have live shells to select,
			// and so property undos put the user back on the cues they had selected.
			// Selection-scope undos only run this step.
			RestoreSelection(entry);
		}
		finally
		{
			_isRestoring = false;
		}
	}

	private void RelinkAllCueComponents()
	{
		if (_globalData?.Cuelist == null) return;
		foreach (var cue in CueList.CueIndex.Values)
			_globalData.Cuelist.RelinkCueComponents(cue);
	}

	/// <summary>
	/// Deep-clones a Godot dictionary so history never aliases live models.
	/// </summary>
	/// <remarks>
	/// Uses a structural walk (not JSON stringify/parse) for speed on large cuelist snapshots.
	/// Nested dictionaries/arrays are copied; packed byte arrays are block-copied; scalars and
	/// strings are stored as new Variants (value semantics). Prefer stripping regenerable blobs
	/// (waveforms) before calling this.
	/// </remarks>
	private static Dictionary DeepCloneDictionary(Dictionary source)
	{
		if (source == null) return new Dictionary();

		var result = new Dictionary();
		foreach (var kv in source)
			result[kv.Key] = DeepCloneVariant(kv.Value);
		return result;
	}

	/// <summary>
	/// Recursively clones a Godot <see cref="Variant"/> for history mementos.
	/// </summary>
	private static Variant DeepCloneVariant(Variant value)
	{
		switch (value.VariantType)
		{
			case Variant.Type.Nil:
				return default;

			case Variant.Type.Dictionary:
				return DeepCloneDictionary(value.AsGodotDictionary());

			case Variant.Type.Array:
			{
				var src = value.AsGodotArray();
				var dst = new Godot.Collections.Array();
				int n = src.Count;
				dst.Resize(n);
				for (int i = 0; i < n; i++)
					dst[i] = DeepCloneVariant(src[i]);
				return dst;
			}

			case Variant.Type.PackedByteArray:
			{
				byte[] bytes = value.AsByteArray();
				if (bytes == null || bytes.Length == 0)
					return System.Array.Empty<byte>();
				var copy = new byte[bytes.Length];
				Buffer.BlockCopy(bytes, 0, copy, 0, bytes.Length);
				return copy;
			}

			default:
				// Numbers, bool, string, Color, Vector2, etc. — safe to share by Variant value.
				return value;
		}
	}

	/// <summary>
	/// One scoped memento entry on the undo/redo stack.
	/// </summary>
	private sealed class HistoryEntry
	{
		public string Description { get; }
		public string CoalesceKey { get; }
		public HistoryScope Scope { get; }
		public int CueId { get; }
		public Dictionary CueData { get; }
		public Dictionary CuesData { get; }
		public Dictionary SettingsData { get; }

		/// <summary>Ordered selected cue ids at capture time (last = most recent selection).</summary>
		public int[] SelectedCueIds { get; }

		/// <summary>Focused cue id at capture time, or -1.</summary>
		public int FocusedCueId { get; }

		public HistoryEntry(
			string description,
			string coalesceKey,
			HistoryScope scope,
			int cueId,
			Dictionary cueData,
			Dictionary cuesData,
			Dictionary settingsData,
			int[] selectedCueIds,
			int focusedCueId)
		{
			Description = description ?? "Edit";
			CoalesceKey = coalesceKey;
			Scope = scope;
			CueId = cueId;
			CueData = cueData;
			CuesData = cuesData;
			SettingsData = settingsData;
			SelectedCueIds = selectedCueIds ?? System.Array.Empty<int>();
			FocusedCueId = focusedCueId;
		}
	}
}
