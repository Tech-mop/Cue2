// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using Godot;
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
using Cue2.UI.Utilities;

// This script is attached to instanced shell bars in the cue list, it handles
// -UI of itself
// -Emitting signals of interactions attached with it's relevant info
namespace Cue2.UI.Shell;

/// <summary>
/// Partial: Inline cue num/name/memo/pre-post wait commits and history
/// </summary>
public partial class ShellBar
{
	private bool IsCueEditingLocked() =>
		_globalData?.Settings?.IsCueEditingLocked == true;

	/// <summary>
	/// Applies or clears inline-edit lock when show mode changes.
	/// </summary>
	private void OnShowModeChanged(bool enabled)
	{
		ApplyShowModeEditLock(enabled);
	}

	/// <summary>
	/// Disables shell-row editing chrome in Show Mode (keeps selection / collapse / playback usable).
	/// </summary>
	/// <param name="locked">True when Show Mode is active.</param>
	private void ApplyShowModeEditLock(bool locked)
	{
		if (locked)
			CancelInlineEdits();

		// Pre/post wait are normally always editable; lock them in show mode.
		ConfigureTimeFieldEditability(_preWaitLineEdit, !locked);
		ConfigureTimeFieldEditability(_postWaitLineEdit, !locked);

		if (_followButton != null)
			_followButton.Disabled = locked;

		if (_dragButton != null)
		{
			_dragButton.Disabled = locked;
			_dragButton.MouseFilter = locked ? MouseFilterEnum.Ignore : MouseFilterEnum.Stop;
			_dragButton.MouseDefaultCursorShape = locked
				? CursorShape.Arrow
				: CursorShape.Drag;
		}
	}

	/// <summary>
	/// Applies focus/editable defaults so pre/post wait fields accept single-click typing.
	/// </summary>
	/// <param name="field">Pre-wait or post-wait LineEdit.</param>
	/// <param name="editable">Whether the field should accept text input.</param>
	private static void ConfigureTimeFieldEditability(LineEdit field, bool editable)
	{
		if (field == null) return;
		field.Editable = editable;
		// Always keep Click/All so a single click focuses and enters edit mode.
		field.FocusMode = editable ? FocusModeEnum.All : FocusModeEnum.None;
		field.MouseFilter = MouseFilterEnum.Stop;
		field.SelectAllOnFocus = true;
	}

	/// <summary>
	/// Wires pre/post wait for focus, submit, and context-menu input.
	/// </summary>
	/// <param name="field">Pre-wait or post-wait LineEdit.</param>
	/// <param name="isPreWait">True for pre-wait; false for post-wait.</param>
	private void WireTimeField(LineEdit field, bool isPreWait)
	{
		if (field == null) return;

		ConfigureTimeFieldEditability(field, editable: true);
		field.GuiInput += OnTimeFieldGuiInput;
		if (isPreWait)
		{
			field.FocusEntered += OnPreWaitFocusEntered;
			field.FocusExited += OnPreWaitFocusExited;
			field.TextSubmitted += OnPreWaitTextSubmitted;
			field.EditingToggled += OnPreWaitEditToggled;
		}
		else
		{
			field.FocusEntered += OnPostWaitFocusEntered;
			field.FocusExited += OnPostWaitFocusExited;
			field.TextSubmitted += OnPostWaitTextSubmitted;
			field.EditingToggled += OnPostWaitEditToggled;
		}
	}

	/// <summary>
	/// Aborts any in-progress double-click inline edit without committing.
	/// </summary>
	private void CancelInlineEdits()
	{
		if (_isEditingCueNum && _cueNumLineEdit != null)
		{
			_cueNumLineEdit.Text = _cue?.CueNum ?? string.Empty;
			_cueNumLineEdit.Editable = false;
			_cueNumLineEdit.FocusMode = FocusModeEnum.None;
			if (_cueNumLineEdit.HasFocus())
				_cueNumLineEdit.ReleaseFocus();
			_isEditingCueNum = false;
		}
		if (_isEditingName && _cueNameLineEdit != null)
		{
			_cueNameLineEdit.Text = _cue?.Name ?? string.Empty;
			_cueNameLineEdit.Editable = false;
			_cueNameLineEdit.FocusMode = FocusModeEnum.None;
			if (_cueNameLineEdit.HasFocus())
				_cueNameLineEdit.ReleaseFocus();
			_isEditingName = false;
		}
		if (_isEditingMemo && _memoLineEdit != null)
		{
			_memoLineEdit.Text = FlattenNotesForShell(_cue?.Notes);
			_memoLineEdit.Editable = false;
			_memoLineEdit.FocusMode = FocusModeEnum.None;
			if (_memoLineEdit.HasFocus())
				_memoLineEdit.ReleaseFocus();
			_isEditingMemo = false;
		}
		if (_isEditingPreWait && _preWaitLineEdit != null && _cue != null)
		{
			_preWaitLineEdit.Text = FormatDurationField(_cue.PreWait);
			if (_preWaitLineEdit.HasFocus())
				_preWaitLineEdit.ReleaseFocus();
			_isEditingPreWait = false;
		}
		if (_isEditingPostWait && _postWaitLineEdit != null && _cue != null)
		{
			_postWaitLineEdit.Text = FormatDurationField(_cue.PostWait);
			if (_postWaitLineEdit.HasFocus())
				_postWaitLineEdit.ReleaseFocus();
			_isEditingPostWait = false;
		}
	}

	private void OnCueNumGuiInput(InputEvent @event)
	{
		OnInput(@event);
		if (@event is InputEventMouseButton mb && mb.ButtonIndex == MouseButton.Left && mb.DoubleClick)
		{
			if (IsCueEditingLocked()) return;
			if (_isEditingCueNum) return;
			BeginInlineCueNumEdit();
		}
	}

	/// <summary>
	/// Enters double-click inline edit for the cue number field.
	/// </summary>
	private void BeginInlineCueNumEdit()
	{
		if (_cueNumLineEdit == null || _cue == null) return;
		_isEditingCueNum = true;
		_cueNumLineEdit.Editable = true;
		_cueNumLineEdit.FocusMode = FocusModeEnum.All;
		_cueNumLineEdit.GrabFocus();
		if (_cueNumLineEdit.HasMethod("edit") && !_cueNumLineEdit.IsEditing())
			_cueNumLineEdit.Edit();
		_cueNumLineEdit.SelectAll();
	}

	private void OnCueNumEditToggled(bool editing)
	{
		if (_cue == null) return;
		if (IsCueEditingLocked())
		{
			if (editing)
				CancelInlineEdits();
			return;
		}

		if (editing)
		{
			_isEditingCueNum = true;
			return;
		}

		CommitCueNumEdit(releaseFocus: false);
	}

	private void OnCueNumFocusExited()
	{
		CommitCueNumEdit(releaseFocus: false);
	}

	private void OnCueNumTextSubmitted(string _)
	{
		CommitCueNumEdit(releaseFocus: true);
	}

	/// <summary>
	/// Commits double-click cue-number edit to the model and refreshes inspectors.
	/// </summary>
	/// <param name="releaseFocus">When true, clear focus after commit (Enter path).</param>
	private void CommitCueNumEdit(bool releaseFocus)
	{
		if (!_isEditingCueNum || _cueNumLineEdit == null || _cue == null)
			return;

		_isEditingCueNum = false;
		_cueNumLineEdit.Editable = false;
		_cueNumLineEdit.FocusMode = FocusModeEnum.None;

		if (IsCueEditingLocked() || _globalData?.HistoryManager?.IsRestoring == true)
		{
			_cueNumLineEdit.Text = _cue.CueNum ?? string.Empty;
			if (releaseFocus && _cueNumLineEdit.HasFocus())
				_cueNumLineEdit.CallDeferred(Control.MethodName.ReleaseFocus);
			return;
		}

		string newNum = _cueNumLineEdit.Text ?? string.Empty;
		if (!string.Equals(_cue.CueNum ?? string.Empty, newNum, System.StringComparison.Ordinal))
		{
			_globalData?.HistoryManager?.RecordCueChange(_cue.Id, "Edit cue number");
			_cue.CueNum = newNum;
			NotifyInspectorsOfCueEdit();
		}

		if (releaseFocus && _cueNumLineEdit.HasFocus())
			_cueNumLineEdit.CallDeferred(Control.MethodName.ReleaseFocus);
	}

	private void OnNameGuiInput(InputEvent @event)
	{
		OnInput(@event);
		if (@event is InputEventMouseButton mb && mb.ButtonIndex == MouseButton.Left && mb.DoubleClick)
		{
			if (IsCueEditingLocked()) return;
			if (_isEditingName) return;
			BeginInlineNameEdit();
		}
	}

	/// <summary>
	/// Enters double-click inline edit for the cue name field.
	/// </summary>
	private void BeginInlineNameEdit()
	{
		if (_cueNameLineEdit == null || _cue == null) return;
		_isEditingName = true;
		_cueNameLineEdit.Editable = true;
		_cueNameLineEdit.FocusMode = FocusModeEnum.All;
		_cueNameLineEdit.GrabFocus();
		if (_cueNameLineEdit.HasMethod("edit") && !_cueNameLineEdit.IsEditing())
			_cueNameLineEdit.Edit();
		_cueNameLineEdit.SelectAll();
	}

	private void OnNameEditToggled(bool editing)
	{
		if (_cue == null) return;
		if (IsCueEditingLocked())
		{
			if (editing)
				CancelInlineEdits();
			return;
		}

		if (editing)
		{
			_isEditingName = true;
			return;
		}

		CommitNameEdit(releaseFocus: false);
	}

	private void OnNameFocusExited()
	{
		CommitNameEdit(releaseFocus: false);
	}

	private void OnNameTextSubmitted(string _)
	{
		CommitNameEdit(releaseFocus: true);
	}

	/// <summary>
	/// Commits double-click cue-name edit to the model and refreshes inspectors.
	/// </summary>
	/// <param name="releaseFocus">When true, clear focus after commit (Enter path).</param>
	private void CommitNameEdit(bool releaseFocus)
	{
		if (!_isEditingName || _cueNameLineEdit == null || _cue == null)
			return;

		_isEditingName = false;
		_cueNameLineEdit.Editable = false;
		_cueNameLineEdit.FocusMode = FocusModeEnum.None;

		if (IsCueEditingLocked() || _globalData?.HistoryManager?.IsRestoring == true)
		{
			_cueNameLineEdit.Text = _cue.Name ?? string.Empty;
			if (releaseFocus && _cueNameLineEdit.HasFocus())
				_cueNameLineEdit.CallDeferred(Control.MethodName.ReleaseFocus);
			return;
		}

		string newName = _cueNameLineEdit.Text ?? string.Empty;
		if (!string.Equals(_cue.Name ?? string.Empty, newName, System.StringComparison.Ordinal))
		{
			_globalData?.HistoryManager?.RecordCueChange(_cue.Id, "Edit cue name");
			_cue.Name = newName;
			NotifyInspectorsOfCueEdit();
		}

		if (releaseFocus && _cueNameLineEdit.HasFocus())
			_cueNameLineEdit.CallDeferred(Control.MethodName.ReleaseFocus);
	}

	private void OnMemoGuiInput(InputEvent @event)
	{
		OnInput(@event);
		if (_memoLineEdit == null || _cue?.Memo != true) return;
		if (@event is InputEventMouseButton mb && mb.ButtonIndex == MouseButton.Left && mb.DoubleClick)
		{
			if (IsCueEditingLocked()) return;
			if (_isEditingMemo) return;
			BeginInlineMemoEdit();
		}
	}

	/// <summary>
	/// Enters double-click inline edit for the memo (notes) field.
	/// </summary>
	private void BeginInlineMemoEdit()
	{
		if (_memoLineEdit == null || _cue == null) return;
		_isEditingMemo = true;
		_memoLineEdit.Editable = true;
		_memoLineEdit.FocusMode = FocusModeEnum.All;
		_memoLineEdit.GrabFocus();
		if (_memoLineEdit.HasMethod("edit") && !_memoLineEdit.IsEditing())
			_memoLineEdit.Edit();
		_memoLineEdit.SelectAll();
	}

	private void OnMemoEditToggled(bool editing)
	{
		if (_cue == null || _memoLineEdit == null) return;
		// Mirror name/number: block edit entry in Show Mode; cancel any in-progress session.
		if (IsCueEditingLocked())
		{
			if (editing)
				CancelInlineEdits();
			return;
		}

		if (editing)
		{
			_isEditingMemo = true;
			return;
		}

		CommitMemoEdit(releaseFocus: false);
	}

	private void OnMemoFocusExited()
	{
		CommitMemoEdit(releaseFocus: false);
	}

	private void OnMemoTextSubmitted(string _)
	{
		CommitMemoEdit(releaseFocus: true);
	}

	/// <summary>
	/// Commits double-click memo-notes edit to the model (same guards as name/number).
	/// </summary>
	/// <param name="releaseFocus">When true, clear focus after commit (Enter path).</param>
	private void CommitMemoEdit(bool releaseFocus)
	{
		if (!_isEditingMemo || _memoLineEdit == null || _cue == null)
			return;

		_isEditingMemo = false;
		_memoLineEdit.Editable = false;
		_memoLineEdit.FocusMode = FocusModeEnum.None;

		string flatCurrent = FlattenNotesForShell(_cue.Notes);
		if (IsCueEditingLocked() || _globalData?.HistoryManager?.IsRestoring == true)
		{
			// Restore display from model; do not record during undo/redo or Show Mode.
			_memoLineEdit.Text = flatCurrent;
			_memoLineEdit.TooltipText = string.IsNullOrEmpty(_cue.Notes)
				? UiLocalizer.T("Memo cue — double-click to edit notes.")
				: _cue.Notes;
			if (releaseFocus && _memoLineEdit.HasFocus())
				_memoLineEdit.CallDeferred(Control.MethodName.ReleaseFocus);
			return;
		}

		string newNotes = _memoLineEdit.Text ?? string.Empty;
		// Compare against flattened form so multi-line notes are not wiped by an unchanged display.
		if (!string.Equals(flatCurrent, newNotes, System.StringComparison.Ordinal))
		{
			_globalData?.HistoryManager?.RecordCueChange(_cue.Id, "Edit cue notes");
			_cue.Notes = newNotes;
			NotifyInspectorsOfCueEdit();
		}
		else
		{
			_memoLineEdit.Text = flatCurrent;
		}

		_memoLineEdit.TooltipText = string.IsNullOrEmpty(_cue.Notes)
			? UiLocalizer.T("Memo cue — double-click to edit notes.")
			: _cue.Notes;

		if (releaseFocus && _memoLineEdit.HasFocus())
			_memoLineEdit.CallDeferred(Control.MethodName.ReleaseFocus);
	}

	private void OnPreWaitFocusEntered()
	{
		if (_cue == null || _preWaitLineEdit == null) return;
		if (IsCueEditingLocked())
		{
			_preWaitLineEdit.Text = FormatDurationField(_cue.PreWait);
			_preWaitLineEdit.CallDeferred(Control.MethodName.ReleaseFocus);
			return;
		}
		_isEditingPreWait = true;
		// Godot 4.4+: keyboard focus alone does not always enter edit mode — force it.
		if (_preWaitLineEdit.HasMethod("edit") && !_preWaitLineEdit.IsEditing())
			_preWaitLineEdit.Edit();
	}

	private void OnPostWaitFocusEntered()
	{
		if (_cue == null || _postWaitLineEdit == null) return;
		if (IsCueEditingLocked())
		{
			_postWaitLineEdit.Text = FormatDurationField(_cue.PostWait);
			_postWaitLineEdit.CallDeferred(Control.MethodName.ReleaseFocus);
			return;
		}
		_isEditingPostWait = true;
		if (_postWaitLineEdit.HasMethod("edit") && !_postWaitLineEdit.IsEditing())
			_postWaitLineEdit.Edit();
	}

	private void OnPreWaitTextSubmitted(string _)
	{
		CommitPreWaitEdit(releaseFocus: true);
	}

	private void OnPostWaitTextSubmitted(string _)
	{
		CommitPostWaitEdit(releaseFocus: true);
	}

	private void OnPreWaitFocusExited()
	{
		// Commit when leaving the field (click away / tab). EditingToggled may also fire —
		// Commit* is idempotent via the editing flag.
		CommitPreWaitEdit(releaseFocus: false);
	}

	private void OnPostWaitFocusExited()
	{
		CommitPostWaitEdit(releaseFocus: false);
	}

	private void OnPreWaitEditToggled(bool editing)
	{
		if (_cue == null) return;
		if (IsCueEditingLocked())
		{
			if (editing && _preWaitLineEdit != null)
			{
				_preWaitLineEdit.Text = FormatDurationField(_cue.PreWait);
				_preWaitLineEdit.CallDeferred(Control.MethodName.ReleaseFocus);
			}
			_isEditingPreWait = false;
			return;
		}

		if (editing)
		{
			_isEditingPreWait = true;
			return;
		}

		// Edit mode ended (Enter / Unedit / click away). Commit once.
		CommitPreWaitEdit(releaseFocus: false);
	}

	private void OnPostWaitEditToggled(bool editing)
	{
		if (_cue == null) return;
		if (IsCueEditingLocked())
		{
			if (editing && _postWaitLineEdit != null)
			{
				_postWaitLineEdit.Text = FormatDurationField(_cue.PostWait);
				_postWaitLineEdit.CallDeferred(Control.MethodName.ReleaseFocus);
			}
			_isEditingPostWait = false;
			return;
		}

		if (editing)
		{
			_isEditingPostWait = true;
			return;
		}

		CommitPostWaitEdit(releaseFocus: false);
	}

	/// <summary>
	/// Parses and applies the pre-wait field. Safe to call multiple times for the same edit session.
	/// </summary>
	/// <param name="releaseFocus">When true, unfocus after commit (Enter / TextSubmitted path).</param>
	private void CommitPreWaitEdit(bool releaseFocus)
	{
		if (!_isEditingPreWait || _preWaitLineEdit == null || _cue == null)
			return;

		// Clear flag first so nested Unedit/FocusExited/UpdateShellBar cannot re-enter.
		_isEditingPreWait = false;

		if (IsCueEditingLocked() || _globalData?.HistoryManager?.IsRestoring == true)
		{
			_preWaitLineEdit.Text = FormatDurationField(_cue.PreWait);
			if (releaseFocus && _preWaitLineEdit.HasFocus())
				_preWaitLineEdit.CallDeferred(Control.MethodName.ReleaseFocus);
			return;
		}

		// Blank submit → 0 (clear the wait).
		string raw = _preWaitLineEdit.Text ?? string.Empty;
		string ret;
		double time;
		bool isValid;
		if (string.IsNullOrWhiteSpace(raw))
		{
			time = 0;
			ret = FormatDurationField(0);
			isValid = true;
		}
		else
		{
			ret = UiUtilities.ParseAndFormatTime(raw, out time, out isValid);
		}

		if (string.IsNullOrEmpty(ret) || !isValid)
		{
			_preWaitLineEdit.Text = FormatDurationField(_cue.PreWait);
		}
		else if (System.Math.Abs(_cue.PreWait - time) >= 1e-9)
		{
			_globalData?.HistoryManager?.RecordCueChange(_cue.Id, "Edit pre-wait");
			_cue.PreWait = time;
			_cue.CalculateTotalDuration();
			_preWaitLineEdit.Text = ret;
			NotifyInspectorsOfCueEdit();
		}
		else
		{
			_preWaitLineEdit.Text = ret;
		}

		if (releaseFocus && _preWaitLineEdit.HasFocus())
			_preWaitLineEdit.CallDeferred(Control.MethodName.ReleaseFocus);
	}

	/// <summary>
	/// Parses and applies the post-wait field. Safe to call multiple times for the same edit session.
	/// </summary>
	/// <param name="releaseFocus">When true, unfocus after commit (Enter / TextSubmitted path).</param>
	private void CommitPostWaitEdit(bool releaseFocus)
	{
		if (!_isEditingPostWait || _postWaitLineEdit == null || _cue == null)
			return;

		_isEditingPostWait = false;

		if (IsCueEditingLocked() || _globalData?.HistoryManager?.IsRestoring == true)
		{
			_postWaitLineEdit.Text = FormatDurationField(_cue.PostWait);
			if (releaseFocus && _postWaitLineEdit.HasFocus())
				_postWaitLineEdit.CallDeferred(Control.MethodName.ReleaseFocus);
			return;
		}

		// Blank submit → 0 (clear the wait).
		string raw = _postWaitLineEdit.Text ?? string.Empty;
		string ret;
		double time;
		bool isValid;
		if (string.IsNullOrWhiteSpace(raw))
		{
			time = 0;
			ret = FormatDurationField(0);
			isValid = true;
		}
		else
		{
			ret = UiUtilities.ParseAndFormatTime(raw, out time, out isValid);
		}

		if (string.IsNullOrEmpty(ret) || !isValid)
		{
			_postWaitLineEdit.Text = FormatDurationField(_cue.PostWait);
		}
		else if (System.Math.Abs(_cue.PostWait - time) >= 1e-9)
		{
			_globalData?.HistoryManager?.RecordCueChange(_cue.Id, "Edit post-wait");
			_cue.PostWait = time;
			_cue.CalculateTotalDuration();
			_postWaitLineEdit.Text = ret;
			NotifyInspectorsOfCueEdit();
		}
		else
		{
			_postWaitLineEdit.Text = ret;
		}

		if (releaseFocus && _postWaitLineEdit.HasFocus())
			_postWaitLineEdit.CallDeferred(Control.MethodName.ReleaseFocus);
	}

	/// <summary>
	/// Continue-mode cycle on the shell row: None → Auto-continue → Auto-follow → None.
	/// </summary>
}
