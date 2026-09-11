// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using Godot;
using System;
using System.Collections.Generic;
using Cue2.Domain.Cuelist;
using Cue2.Domain.Playback;
using Cue2.Domain.Devices;
using Cue2.Domain.ShowSettings;
using Cue2.Domain.Metadata;
using Cue2.Domain.Cues;
using Cue2.Domain.Connections;
using Cue2.Domain.Library;
using Cue2.Domain.Commands;

namespace Cue2.Services;
public partial class GlobalSignals : Node
{
	[Signal]  public delegate void ShellFocusedEventHandler(int cueId);
	[Signal]  public delegate void LogEventHandler(string log, int type);
	// 0 = info, 1 = warning, 2 = error, 3 = alert
	[Signal]  public delegate void LogUpdatedEventHandler(string printout, int type);
	[Signal] public delegate void LogAlertEventHandler();
	[Signal]  public delegate void FileSelectedEventHandler(string path);
	[Signal]  public delegate void FileDroppedEventHandler(string[] files, string targetControlName);
	// CueGo removed (P2-05): live active cues are owned by CueCommandExecutor, not a parallel CueGo path.
	[Signal]  public delegate void UpdateShellBarEventHandler(int cue);
	[Signal]  public delegate void OpenSelectedSessionEventHandler(string path);
	[Signal]  public delegate void SaveFileEventHandler(string url, string showName);
	[Signal] public delegate void SyncShellInspectorEventHandler();
	
	// Signals Associated with InputActions
	[Signal] public delegate void NewSessionEventHandler();
	[Signal] public delegate void SaveEventHandler();
	[Signal] public delegate void SaveAsEventHandler();

	[Signal] public delegate void OpenSessionEventHandler();

	[Signal] public delegate void GoEventHandler();

	/// <summary>
	/// Fired when GO becomes blocked. <paramref name="reason"/> is a stable token
	/// (e.g. <see cref="GoDisableReasonSessionLoad"/>). <paramref name="durationSeconds"/>
	/// is 0 when the block is indefinite.
	/// </summary>
	[Signal] public delegate void GoDisabledEventHandler(string reason, float durationSeconds);

	/// <summary>Fired when every GO disable reason has been cleared.</summary>
	[Signal] public delegate void GoEnabledEventHandler();
	
	[Signal] public delegate void ResumeAllEventHandler();
	[Signal] public delegate void PauseAllEventHandler();
	[Signal] public delegate void StopAllEventHandler();
	
	[Signal] public delegate void CreateCueEventHandler();

	/// <summary>
	/// Fired when the total number of cues in the show changes (create, delete, load, reset, undo/redo).
	/// </summary>
	/// <remarks>Argument is the new total cue count (all cues including group children).</remarks>
	[Signal] public delegate void TotalCuesChangedEventHandler(int total);

	/// <summary>Delete currently selected cue(s) from the cuelist.</summary>
	[Signal] public delegate void DeleteSelectedCuesEventHandler();

	/// <summary>Duplicate currently selected cue(s) (and full child trees when a parent is selected).</summary>
	[Signal] public delegate void DuplicateSelectedCuesEventHandler();

	/// <summary>Cut currently selected cue(s) to the cue clipboard (copy then delete).</summary>
	[Signal] public delegate void CutSelectedCuesEventHandler();

	/// <summary>Copy currently selected cue(s) to the cue clipboard.</summary>
	[Signal] public delegate void CopySelectedCuesEventHandler();

	/// <summary>Paste cue clipboard contents below the last selected cue.</summary>
	[Signal] public delegate void PasteCuesEventHandler();
	
	[Signal] public delegate void GroupSelectedCuesEventHandler();

	/// <summary>Select all currently visible cues in the cuelist.</summary>
	[Signal] public delegate void SelectAllCuesEventHandler();

	[Signal] public delegate void SelectNextCueEventHandler();
	[Signal] public delegate void SelectPreviousCueEventHandler();
	
	[Signal] public delegate void ToggleSettingsWindowEventHandler();

	/// <summary>
	/// Open Settings on a specific tree item (stable English menu key, e.g. <c>Updates</c>).
	/// </summary>
	[Signal] public delegate void OpenSettingsMenuEventHandler(string menuKey);

	/// <summary>
	/// Updater UI state for Settings and the footer.
	/// <paramref name="state"/> is <see cref="UpdateUiState"/> as int; <paramref name="message"/> is status text.
	/// </summary>
	[Signal] public delegate void UpdateUiStateChangedEventHandler(int state, string message);

	[Signal] public delegate void ToggleLogWindowEventHandler();

	[Signal] public delegate void CuelistExpandOneLayerEventHandler();
	[Signal] public delegate void CuelistCollapseOneLayerEventHandler();
	[Signal] public delegate void ToggleExpandAllEventHandler();

	/// <summary>Undo the last document edit (cues / settings data).</summary>
	[Signal] public delegate void UndoEventHandler();

	/// <summary>Redo the last undone document edit.</summary>
	[Signal] public delegate void RedoEventHandler();

	/// <summary>
	/// Fired when show mode is enabled or disabled (showfile setting).
	/// </summary>
	/// <param name="enabled">True = Show Mode (cue edits locked); false = Edit Mode.</param>
	[Signal] public delegate void ShowModeChangedEventHandler(bool enabled);

	/// <summary>
	/// Toggle Show Mode / Edit Mode (Input Map / hotkey).
	/// </summary>
	[Signal] public delegate void ToggleShowModeEventHandler();

	/// <summary>Enter Edit Mode (cue editing unlocked). No-op when already in Edit Mode.</summary>
	[Signal] public delegate void EnterEditModeEventHandler();

	/// <summary>Enter Show Mode (cue editing locked). No-op when already in Show Mode.</summary>
	[Signal] public delegate void EnterShowModeEventHandler();
	
	// Text edit signal connector
	[Signal]  public delegate void TextEditFocusEnteredEventHandler();
	[Signal]  public delegate void TextEditFocusExitedEventHandler();
	
	
	// Singals assaciated with settings
	[Signal] public delegate void UiScaleChangedEventHandler(float value);
	[Signal] public delegate void GoScaleChangedEventHandler(float value);

	/// <summary>
	/// Fired when the application UI locale changes via localization preferences.
	/// </summary>
	/// <param name="localeCode">ISO-style locale code (e.g. <c>en</c>).</param>
	[Signal] public delegate void LocaleChangedEventHandler(string localeCode);

	/// <summary>Fired when the cuelist UI scale (Small / Medium / Large) changes.</summary>
	[Signal] public delegate void CueListScaleChangedEventHandler(float value);

	/// <summary>Fired when the Timeline Inspector waveform display setting changes.</summary>
	[Signal] public delegate void ShowTimelineWaveformsChangedEventHandler(bool enabled);

	// Signals associated with devices
	[Signal] public delegate void AudioDevicesChangedEventHandler();

	/// <summary>
	/// Fired when session master audio volume or runtime mute changes.
	/// </summary>
	/// <param name="linear">Session master volume linear 0–1 (ignores mute).</param>
	/// <param name="muted">True when runtime master mute is active.</param>
	[Signal] public delegate void AudioMasterControlChangedEventHandler(float linear, bool muted);

	[Signal] public delegate void DisplaysChangedEventHandler();
	[Signal] public delegate void CanvasSizeChangedEventHandler(Vector2I newSize);

	/// <summary>
	/// Fired when master video output disable or blackout runtime state changes.
	/// </summary>
	/// <param name="disabled">True when all display windows are closed/hidden.</param>
	/// <param name="blackout">True when layers are blacked out (windows still open).</param>
	[Signal] public delegate void VideoOutputControlChangedEventHandler(bool disabled, bool blackout);

	/// <summary>
	/// Fired when the show-scoped output background colour changes.
	/// </summary>
	/// <param name="color">New background colour applied to output windows.</param>
	[Signal] public delegate void OutputBackgroundColorChangedEventHandler(Color color);

	/// <summary>
	/// Fired when a single target layer's size or canvas position changes
	/// (e.g. Translate Layer control animation). Lighter than <see cref="DisplaysChanged"/>.
	/// </summary>
	/// <param name="layerId">Layer that was updated.</param>
	[Signal] public delegate void LayerGeometryChangedEventHandler(int layerId);

	// Media backup (show-local file copies)
	/// <summary>
	/// Fired when media backup progress changes.
	/// Args: percent (0–100), busy, statusText (e.g. "Copying 45%"), originPath, destPath, completedCount, totalCount.
	/// </summary>
	[Signal] public delegate void MediaBackupProgressEventHandler(
		float percent, bool busy, string statusText, string originPath, string destPath, int completedCount, int totalCount);
	/// <summary>Fired when the media backup queue becomes idle.</summary>
	[Signal] public delegate void MediaBackupCompletedEventHandler();

	/// <summary>
	/// Generic footer background-process progress (cuelist bulk ops, etc.).
	/// Args: percent (0–100), busy, statusText (shown on the bar), detail (tooltip secondary line),
	/// completedCount, totalCount.
	/// </summary>
	[Signal] public delegate void BackgroundProcessProgressEventHandler(
		float percent, bool busy, string statusText, string detail, int completedCount, int totalCount);

	/// <summary>Fired when a generic background process finishes (footer may hide after a short delay).</summary>
	[Signal] public delegate void BackgroundProcessCompletedEventHandler();

	/// <summary>
	/// Fired when a showfile apply starts (startup last-show or File → Open after the version gate).
	/// </summary>
	/// <param name="showName">Show file name without extension (may be empty).</param>
	[Signal] public delegate void SessionLoadStartedEventHandler(string showName);

	/// <summary>
	/// Determinate progress while a showfile is applied to the live session.
	/// </summary>
	/// <param name="percent">0–100.</param>
	/// <param name="statusText">English source status key (e.g. <c>Loading cues…</c>).</param>
	/// <param name="detail">Secondary line (e.g. <c>120/840 cues</c>).</param>
	/// <param name="completed">Completed units for the current stage.</param>
	/// <param name="total">Total units for the current stage (0 when unknown).</param>
	[Signal] public delegate void SessionLoadProgressEventHandler(
		float percent, string statusText, string detail, int completed, int total);

	/// <summary>Fired when showfile apply finishes (success or fail). Overlay hides; GO is unblocked.</summary>
	[Signal] public delegate void SessionLoadFinishedEventHandler();

	// Media health (missing files, etc.)
	/// <summary>
	/// Fired when a cue's media health state changes.
	/// Args: cueId, hasIssue, message (tooltip text when hasIssue is true).
	/// </summary>
	[Signal] public delegate void CueMediaHealthChangedEventHandler(int cueId, bool hasIssue, string message);

	/// <summary>
	/// When true, <see cref="OnNodeAdded"/> does not wire LineEdit/OptionButton keyboard policy.
	/// Showfile first-bind suppresses this, then calls <see cref="ScanForUiKeyboardPolicy"/> once.
	/// </summary>
	public bool SuppressUiKeyboardScan { get; set; }

	/// <summary>Text fields wired for focus-gate + Esc/submit unfocus.</summary>
	private readonly HashSet<Node> _connectedTextFields = new();

	/// <summary>Per-LineEdit hooks so they can be disconnected cleanly on free.</summary>
	private readonly Dictionary<LineEdit, (Control.GuiInputEventHandler Gui, LineEdit.TextSubmittedEventHandler Submit)> _lineEditHooks = new();

	/// <summary>Per-TextEdit Esc hooks.</summary>
	private readonly Dictionary<TextEdit, Control.GuiInputEventHandler> _textEditHooks = new();

	/// <summary>
	/// OptionButtons that swallow Space so they do not open/close via ui_accept Space
	/// (Space is reserved for the Go InputMap action).
	/// </summary>
	private readonly Dictionary<OptionButton, Control.GuiInputEventHandler> _optionButtonHooks = new();

	/// <summary>
	/// Scans the tree for LineEdit/TextEdit and wires focus + Esc (and Enter unfocus for LineEdit).
	/// Focus signals drive <see cref="InputActionsListener"/> so typing does not fire hotkeys.
	/// Also wires OptionButtons so Space does not open the dropdown (Go uses Space).
	/// </summary>
	public override void _Ready()
	{
		// Linux: embed OptionButton/Popup lists inside the main viewport. Do not ForceNative /root.
		LinuxWindowEmbedPolicy.EnablePopupEmbedding(GetTree().Root);

		// Scan for existing text fields / OptionButtons at startup
		ScanForUiKeyboardPolicy(GetTree().Root);

		// Listen for new nodes added dynamically (inspectors, settings cards, SpinBox embeds, etc.)
		GetTree().NodeAdded += OnNodeAdded;
		GetTree().NodeRemoved += OnNodeRemoved;
	}

	/// <summary>
	/// Recursively wires keyboard policy for text fields and OptionButtons under <paramref name="node"/>.
	/// </summary>
	/// <param name="node">Root of the subtree to scan. Ignored when null or invalid.</param>
	public void ScanForUiKeyboardPolicy(Node node)
	{
		if (node == null || !GodotObject.IsInstanceValid(node))
			return;

		if (node is LineEdit or TextEdit)
			ConnectFocusSignals(node);
		else if (node is OptionButton optionButton)
			ConnectOptionButtonSpaceBlock(optionButton);

		foreach (Node child in node.GetChildren())
			ScanForUiKeyboardPolicy(child);
	}

	private void OnNodeAdded(Node node)
	{
		// FileDialog / AcceptDialog are created without our constructors; apply while still hidden.
		if (node is Window window)
			LinuxWindowEmbedPolicy.ApplyToAppWindow(window);

		if (SuppressUiKeyboardScan)
			return;
		if (node is LineEdit or TextEdit)
			ConnectFocusSignals(node);
		else if (node is OptionButton optionButton)
			ConnectOptionButtonSpaceBlock(optionButton);
	}

	private void OnNodeRemoved(Node node)
	{
		if (node is LineEdit or TextEdit)
			DisconnectFocusSignals(node);
		else if (node is OptionButton optionButton)
			DisconnectOptionButtonSpaceBlock(optionButton);
	}

	/// <summary>
	/// Wires focus tracking (hotkey gate) and keyboard unfocus helpers for a text field.
	/// </summary>
	private void ConnectFocusSignals(Node node)
	{
		if (node is not Control textField || _connectedTextFields.Contains(node))
			return;

		textField.FocusEntered += FocusEntered;
		textField.FocusExited += FocusExited;

		if (node is LineEdit lineEdit)
		{
			// Esc → leave field; Enter/submit → leave field (consistent across the app).
			Control.GuiInputEventHandler gui = @event => OnLineEditGuiInput(lineEdit, @event);
			LineEdit.TextSubmittedEventHandler submit = _ => ReleaseLineEditFocus(lineEdit);
			lineEdit.GuiInput += gui;
			lineEdit.TextSubmitted += submit;
			_lineEditHooks[lineEdit] = (gui, submit);
		}
		else if (node is TextEdit textEdit)
		{
			// Multi-line: Esc only (Enter inserts a newline).
			Control.GuiInputEventHandler gui = @event => OnTextEditGuiInput(textEdit, @event);
			textEdit.GuiInput += gui;
			_textEditHooks[textEdit] = gui;
		}

		_connectedTextFields.Add(node);
	}

	/// <summary>
	/// Unhooks focus and keyboard helpers when a text field leaves the tree.
	/// </summary>
	private void DisconnectFocusSignals(Node node)
	{
		if (!_connectedTextFields.Contains(node) || node is not Control textField)
			return;

		textField.FocusEntered -= FocusEntered;
		textField.FocusExited -= FocusExited;

		if (node is LineEdit lineEdit && _lineEditHooks.TryGetValue(lineEdit, out var lineHooks))
		{
			lineEdit.GuiInput -= lineHooks.Gui;
			lineEdit.TextSubmitted -= lineHooks.Submit;
			_lineEditHooks.Remove(lineEdit);
		}
		else if (node is TextEdit textEdit && _textEditHooks.TryGetValue(textEdit, out var textGui))
		{
			textEdit.GuiInput -= textGui;
			_textEditHooks.Remove(textEdit);
		}

		_connectedTextFields.Remove(node);
	}

	private void OnLineEditGuiInput(LineEdit lineEdit, InputEvent @event)
	{
		if (!IsEscapePressed(@event) || !GodotObject.IsInstanceValid(lineEdit) || !lineEdit.HasFocus())
			return;

		ReleaseLineEditFocus(lineEdit);
		lineEdit.GetViewport()?.SetInputAsHandled();
	}

	private void OnTextEditGuiInput(TextEdit textEdit, InputEvent @event)
	{
		if (!IsEscapePressed(@event) || !GodotObject.IsInstanceValid(textEdit) || !textEdit.HasFocus())
			return;

		textEdit.ReleaseFocus();
		textEdit.GetViewport()?.SetInputAsHandled();
	}

	/// <summary>
	/// True when Escape was just pressed (ignores key-repeat).
	/// </summary>
	private static bool IsEscapePressed(InputEvent @event)
	{
		if (@event is not InputEventKey key || !key.Pressed || key.Echo)
			return false;

		return key.Keycode == Key.Escape
			|| key.PhysicalKeycode == Key.Escape
			|| key.IsAction("ui_cancel");
	}

	/// <summary>
	/// Ends edit mode and clears focus so InputMap listening can resume.
	/// Deferred so Godot cannot re-grab focus on the same frame as TextSubmitted.
	/// </summary>
	/// <param name="lineEdit">LineEdit to unfocus.</param>
	private static void ReleaseLineEditFocus(LineEdit lineEdit)
	{
		if (lineEdit == null || !GodotObject.IsInstanceValid(lineEdit))
			return;
		
		if (lineEdit.HasMethod("unedit") && lineEdit.IsEditing())
			lineEdit.Unedit();

		if (lineEdit.HasFocus())
			lineEdit.ReleaseFocus();

		// Belt-and-braces: submit can race with engine re-focus on the same frame.
		lineEdit.CallDeferred(Control.MethodName.ReleaseFocus);
	}

	/// <summary>
	/// Prevents OptionButton from treating Space as ui_accept (open/close dropdown).
	/// Mouse click and Enter still work. Space remains available for the Go action.
	/// </summary>
	/// <remarks>
	/// Godot emits <see cref="Control.GuiInput"/> before BaseButton's virtual
	/// <c>_gui_input</c>; marking the event handled here stops the built-in activation.
	/// <see cref="Input.IsActionJustPressed"/> still sees Space for InputMap.
	/// </remarks>
	private void ConnectOptionButtonSpaceBlock(OptionButton optionButton)
	{
		if (optionButton == null || !GodotObject.IsInstanceValid(optionButton))
			return;
		if (_optionButtonHooks.ContainsKey(optionButton))
			return;

		Control.GuiInputEventHandler gui = @event => OnOptionButtonGuiInput(optionButton, @event);
		optionButton.GuiInput += gui;
		_optionButtonHooks[optionButton] = gui;
	}

	/// <summary>
	/// Removes Space-block handler when an OptionButton leaves the tree.
	/// </summary>
	private void DisconnectOptionButtonSpaceBlock(OptionButton optionButton)
	{
		if (optionButton == null)
			return;
		if (!_optionButtonHooks.TryGetValue(optionButton, out var gui))
			return;

		if (GodotObject.IsInstanceValid(optionButton))
			optionButton.GuiInput -= gui;
		_optionButtonHooks.Remove(optionButton);
	}

	/// <summary>
	/// Swallows Space on a focused OptionButton so the popup does not toggle.
	/// </summary>
	private static void OnOptionButtonGuiInput(OptionButton optionButton, InputEvent @event)
	{
		if (optionButton == null || !GodotObject.IsInstanceValid(optionButton))
			return;
		if (!IsSpaceKeyPressed(@event))
			return;

		// Do not open/close the list on Space. Leave the key for app Go (and do not activate).
		optionButton.AcceptEvent();
	}

	/// <summary>
	/// True when Space was just pressed (ignores key-repeat and modifiers are allowed).
	/// </summary>
	private static bool IsSpaceKeyPressed(InputEvent @event)
	{
		if (@event is not InputEventKey key || !key.Pressed || key.Echo)
			return false;

		// Prefer physical/key codes; unicode 32 covers some layouts.
		return key.Keycode == Key.Space
			|| key.PhysicalKeycode == Key.Space
			|| key.KeyLabel == Key.Space
			|| key.Unicode == 32;
	}

	private void FocusEntered()
	{
		EmitSignal(SignalName.TextEditFocusEntered);
	}

	private void FocusExited()
	{
		EmitSignal(SignalName.TextEditFocusExited);
	}
}
