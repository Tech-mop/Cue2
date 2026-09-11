// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System;
using Cue2.Services;
using Cue2.UI.Utilities;
using Godot;

namespace Cue2.UI.Windows;

/// <summary>
/// Floating log viewer window for the <b>current session only</b>.
/// Loads the most recent page of session logs on open, with optional load-older
/// pagination within this session and clear (current session memory + current file).
/// Historical session files on disk are not shown here; they are retained/pruned
/// via <see cref="UserDataManager.LogSessionDepth"/>.
/// </summary>
public partial class LogWindow : Window
{
	private const int PageSize = 100;

	private EventLogger _eventLogger;
	private GlobalSignals _globalSignals;
	private GlobalData _globalData;

	private VBoxContainer _logListContainer;
	private Button _loadMoreButton;
	private Button _clearLogsButton;
	private Label _showingLabel;

	/// <summary>
	/// Index in EventLogger's list of the oldest log currently shown.
	/// Zero means all older history is already displayed.
	/// </summary>
	private int _oldestDisplayedIndex;

	/// <summary>
	/// Applies Linux native-window policy before the scene window enters the tree.
	/// </summary>
	public LogWindow()
	{
		LinuxWindowEmbedPolicy.ApplyToAppWindow(this);
	}

	public override void _Ready()
	{
		_eventLogger = GetNode<EventLogger>("/root/EventLogger");
		_globalData = GetNode<GlobalData>("/root/GlobalData");
		_globalSignals = GetNode<GlobalSignals>("/root/GlobalSignals");

		_logListContainer = GetNode<VBoxContainer>("%LogListContainer");
		_loadMoreButton = GetNode<Button>("%LoadMoreButton");
		_clearLogsButton = GetNode<Button>("%ClearLogsButton");
		_showingLabel = GetNode<Label>("%ShowingLabel");

		_loadMoreButton.Pressed += OnLoadMorePressed;
		_clearLogsButton.Pressed += OnClearLogsPressed;

		UiUtilities.RescaleWindow(this, _globalData.BaseDisplayScale);
		float userScale = _globalData.UserDataManager?.UiScale ?? UserDataManager.DefaultUiScale;
		UiUtilities.RescaleUi(this, userScale, _globalData.BaseDisplayScale);

		_globalSignals.UiScaleChanged += ScaleUi;
		_globalSignals.LogUpdated += OnNewLog;
		_globalSignals.LocaleChanged += OnLocaleChanged;

		SyncLogsInitialPage();
		UiLocalizer.LocalizeTree(this);
	}

	/// <summary>
	/// Re-localizes log window chrome when the UI language changes.
	/// </summary>
	/// <param name="localeCode">New locale code.</param>
	private void OnLocaleChanged(string localeCode)
	{
		if (!GodotObject.IsInstanceValid(this))
			return;
		UiLocalizer.LocalizeTree(this);
		UpdateShowingLabel();
	}

	/// <summary>
	/// Appends a newly emitted log at the top of the list (newest-first display).
	/// </summary>
	private void OnNewLog(string printout, int type)
	{
		var label = CreateLogLabel(printout, type);
		_logListContainer.AddChild(label);
		_logListContainer.MoveChild(label, 0);
		// New entries are appended to the source list; oldest index stays valid.
		UpdateShowingLabel();
	}

	/// <summary>
	/// Loads only the most recent <see cref="PageSize"/> logs into the UI.
	/// </summary>
	private void SyncLogsInitialPage()
	{
		var logList = _eventLogger.GetLogList();
		int total = logList.Count;
		_oldestDisplayedIndex = Math.Max(0, total - PageSize);

		// Source list is oldest-first; display newest-first via MoveChild(0).
		for (int i = _oldestDisplayedIndex; i < total; i++)
		{
			var label = CreateLogLabelFromText(logList[i]);
			_logListContainer.AddChild(label);
			_logListContainer.MoveChild(label, 0);
		}

		UpdatePaginationUi();
	}

	/// <summary>
	/// Loads the next older page of logs and appends them below the current entries.
	/// </summary>
	private void OnLoadMorePressed()
	{
		if (_oldestDisplayedIndex <= 0)
		{
			UpdatePaginationUi();
			return;
		}

		var logList = _eventLogger.GetLogList();
		int newStart = Math.Max(0, _oldestDisplayedIndex - PageSize);

		// Append older entries at the bottom. Walk newest-of-batch → oldest so
		// each AddChild keeps chronological order (newest above older).
		for (int i = _oldestDisplayedIndex - 1; i >= newStart; i--)
		{
			if (i < 0 || i >= logList.Count)
				continue;

			var label = CreateLogLabelFromText(logList[i]);
			_logListContainer.AddChild(label);
		}

		_oldestDisplayedIndex = newStart;
		UpdatePaginationUi();
	}

	/// <summary>
	/// Clears the current session logs from memory and the current session file,
	/// empties the UI, then records a single confirmation entry.
	/// Historical rotated session files on disk are not deleted.
	/// </summary>
	private void OnClearLogsPressed()
	{
		_eventLogger.ClearLogs();

		// Remove immediately so the confirmation log is not mixed with deferred frees.
		while (_logListContainer.GetChildCount() > 0)
		{
			var child = _logListContainer.GetChild(0);
			_logListContainer.RemoveChild(child);
			child.QueueFree();
		}

		_oldestDisplayedIndex = 0;
		UpdatePaginationUi();

		// One new entry so the footer session count and this window stay consistent.
		_globalSignals.EmitSignal(nameof(GlobalSignals.Log), "Current session logs cleared.", (int)LogType.Info);
	}

	/// <summary>
	/// Refreshes the load-more button state and the showing X/Y logs label.
	/// </summary>
	private void UpdatePaginationUi()
	{
		UpdateLoadMoreButton();
		UpdateShowingLabel();
	}

	private void UpdateLoadMoreButton()
	{
		int remaining = _oldestDisplayedIndex;
		if (remaining <= 0)
		{
			_loadMoreButton.Disabled = true;
			_loadMoreButton.Text = UiLocalizer.T("No older logs");
			return;
		}

		_loadMoreButton.Disabled = false;
		int nextBatch = Math.Min(PageSize, remaining);
		_loadMoreButton.Text = nextBatch == PageSize
			? UiLocalizer.T("Load next 100")
			: UiLocalizer.Tf("Load next {0}", nextBatch);
	}

	/// <summary>
	/// Updates the centered status label to "Showing ###/### Logs".
	/// Displayed count is the contiguous range from the oldest loaded index through the list end
	/// (including live entries prepended while the window is open).
	/// </summary>
	private void UpdateShowingLabel()
	{
		if (_showingLabel == null || _eventLogger == null)
			return;

		int total = _eventLogger.GetTotalLogCount();
		int showing = Math.Max(0, total - _oldestDisplayedIndex);
		_showingLabel.Text = UiLocalizer.Tf("Showing {0}/{1} Logs", showing, total);
	}

	private static Label CreateLogLabel(string printout, int type)
	{
		var label = new Label
		{
			Text = printout,
			AutowrapMode = TextServer.AutowrapMode.WordSmart,
			SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
		};

		if (type == (int)LogType.Error || type == (int)LogType.Alert)
			label.AddThemeColorOverride("font_color", GlobalStyles.Danger);
		else if (type == (int)LogType.Warning)
			label.AddThemeColorOverride("font_color", GlobalStyles.Warning);

		return label;
	}

	/// <summary>
	/// Creates a label from a stored log line (type inferred from text prefix).
	/// </summary>
	private static Label CreateLogLabelFromText(string log)
	{
		var label = new Label
		{
			Text = log,
			AutowrapMode = TextServer.AutowrapMode.WordSmart,
			SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
		};

		// Historical lines use type name prefixes (e.g. "Error  :  ...").
		if (log.StartsWith("Error", StringComparison.Ordinal) || log.StartsWith("Alert", StringComparison.Ordinal))
			label.AddThemeColorOverride("font_color", GlobalStyles.Danger);
		else if (log.StartsWith("Warning", StringComparison.Ordinal))
			label.AddThemeColorOverride("font_color", GlobalStyles.Warning);

		return label;
	}

	private void ScaleUi(float value)
	{
		try
		{
			float effectiveScale = value * _globalData.BaseDisplayScale;
			WrapControls = true;
			ContentScaleFactor = effectiveScale;
			ChildControlsChanged();
			GD.Print($"LogWindow:ScaleUi - Applied effective UI scale: {effectiveScale} (user: {value} * base: {_globalData.BaseDisplayScale})");
		}
		catch (Exception ex)
		{
			_globalSignals.EmitSignal(nameof(GlobalSignals.Log), $"Error applying UI scale: {ex.Message}", (int)LogType.Error);
			GetWindow().ContentScaleFactor = value;
		}
	}

	public override void _ExitTree()
	{
		if (_loadMoreButton != null)
			_loadMoreButton.Pressed -= OnLoadMorePressed;
		if (_clearLogsButton != null)
			_clearLogsButton.Pressed -= OnClearLogsPressed;

		if (_globalSignals != null)
		{
			_globalSignals.UiScaleChanged -= ScaleUi;
			_globalSignals.LogUpdated -= OnNewLog;
			_globalSignals.LocaleChanged -= OnLocaleChanged;
		}
	}
}
