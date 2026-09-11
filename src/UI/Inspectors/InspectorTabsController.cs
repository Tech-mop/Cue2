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
using Cue2.Services;
using Cue2.UI.Utilities;
using Godot;

namespace Cue2.UI.Inspectors;

/// <summary>
/// Drives inspector <see cref="TabContainer"/> UX: content indicators on tabs that have
/// components for the focused cue (or any multi-selected cue), and auto-switching to a
/// relevant tab on selection.
/// </summary>
/// <remarks>
/// Auto-switch only runs when the focused cue id changes. If the current tab already has
/// content for the newly selected cue, the tab is left unchanged. Auto-switch is also
/// suppressed while the user is on Library or Timeline (context/browsing tabs). Indicators
/// refresh on focus changes and when document history / inspector sync implies component edits.
/// During multi-edit selection, tab dots reflect the union of components across selected cues.
/// </remarks>
public partial class InspectorTabsController : TabContainer
{
	/// <summary>Preferred tab order when auto-selecting a tab with content.</summary>
	/// <remarks>
	/// Entries must match <see cref="TabContainer"/> child node names (e.g. "Visual", not component type "Video").
	/// </remarks>
	private static readonly string[] TabPriority =
	{
		"Audio",
		"Visual",
		"Text",
		"Connection",
		"Control",
		"Network"
	};

	/// <summary>
	/// Tabs that should not be interrupted by content-based auto-switch when focused.
	/// </summary>
	private static readonly HashSet<string> StickyTabs = new(StringComparer.Ordinal)
	{
		"Library",
		"Timeline"
	};

	private GlobalData _globalData;
	private GlobalSignals _globalSignals;

	/// <summary>Last cue id that triggered auto-switch logic (-2 = never, -1 = none).</summary>
	private int _lastAutoSwitchCueId = -2;

	/// <summary>English tab titles keyed by control name (catalog keys).</summary>
	private readonly Dictionary<string, string> _baseTitles = new();

	/// <summary>English tab tooltips keyed by control name (catalog keys).</summary>
	private readonly Dictionary<string, string> _baseTooltips = new();

	/// <summary>Small filled circle shown as tab icon when that tab has cue content.</summary>
	private Texture2D _contentDotIcon;

	/// <inheritdoc />
	public override void _Ready()
	{
		_globalData = GetNode<GlobalData>("/root/GlobalData");
		_globalSignals = GetNode<GlobalSignals>("/root/GlobalSignals");

		CacheBaseTitles();
		_contentDotIcon = CreateContentDotIcon();

		_globalSignals.ShellFocused += OnShellFocused;
		_globalSignals.SyncShellInspector += OnSyncIndicators;
		_globalSignals.LocaleChanged += OnLocaleChanged;

		if (_globalData.HistoryManager != null)
		{
			_globalData.HistoryManager.HistoryChanged += OnSyncIndicators;
			_globalData.HistoryManager.HistoryRestored += OnHistoryRestored;
		}

		// Initial state (no selection).
		RefreshIndicators(null);
	}

	/// <inheritdoc />
	public override void _ExitTree()
	{
		if (_globalSignals != null)
		{
			_globalSignals.ShellFocused -= OnShellFocused;
			_globalSignals.SyncShellInspector -= OnSyncIndicators;
			_globalSignals.LocaleChanged -= OnLocaleChanged;
		}

		if (_globalData?.HistoryManager != null)
		{
			_globalData.HistoryManager.HistoryChanged -= OnSyncIndicators;
			_globalData.HistoryManager.HistoryRestored -= OnHistoryRestored;
		}
	}

	private void OnHistoryRestored(int scope)
	{
		// Component add/remove can restore under Cue or Cuelist scope.
		if (scope == (int)HistoryManager.HistoryScope.Cue
		    || scope == (int)HistoryManager.HistoryScope.Cuelist
		    || scope == (int)HistoryManager.HistoryScope.MultiCue)
		{
			OnSyncIndicators();
		}
	}

	/// <summary>
	/// Re-translates tab titles and tooltips when the UI language changes.
	/// </summary>
	private void OnLocaleChanged(string localeCode)
	{
		if (!GodotObject.IsInstanceValid(this))
			return;
		RefreshIndicatorsFromFocusedCue();
	}

	private void OnShellFocused(int cueId)
	{
		var content = BuildContentFlagsForSelection(cueId);
		RefreshIndicators(content);

		// Only auto-switch when the focused cue actually changes (not undo refresh of same id).
		bool cueChanged = cueId != _lastAutoSwitchCueId;
		_lastAutoSwitchCueId = cueId;

		var cue = ResolveCue(cueId);
		if (!cueChanged || cueId < 0 || cue == null)
			return;

		TryAutoSwitchTab(content);
	}

	/// <summary>
	/// History is recorded <em>before</em> mutation; defer so indicators see the post-edit model.
	/// </summary>
	private void OnSyncIndicators()
	{
		CallDeferred(MethodName.RefreshIndicatorsFromFocusedCue);
	}

	private void RefreshIndicatorsFromFocusedCue()
	{
		RefreshIndicators(BuildContentFlagsForSelection(_globalData?.FocusedCue ?? -1));
	}

	/// <summary>
	/// Content flags for the focused cue, or the union across multi-selection when multi-edit is on.
	/// </summary>
	private HashSet<string> BuildContentFlagsForSelection(int focusedCueId)
	{
		if (InspectorMultiEditSupport.ShouldUseMultiEdit(_globalData))
		{
			var flags = new HashSet<string>(StringComparer.Ordinal);
			foreach (var cue in InspectorMultiEditSupport.GetSelectedCues())
			{
				foreach (var name in BuildContentFlags(cue))
					flags.Add(name);
			}
			return flags;
		}

		return BuildContentFlags(ResolveCue(focusedCueId));
	}

	/// <summary>
	/// Switches to a content tab unless the current tab already has content for this cue,
	/// or the current tab is sticky (Library / Timeline).
	/// </summary>
	private void TryAutoSwitchTab(HashSet<string> contentTabs)
	{
		if (contentTabs == null || contentTabs.Count == 0)
			return;

		int current = CurrentTab;
		if (current >= 0 && current < GetTabCount())
		{
			string currentName = GetTabControl(current)?.Name;
			if (!string.IsNullOrEmpty(currentName))
			{
				// Stay on browsing / timeline tabs even when the focused cue has other content.
				if (StickyTabs.Contains(currentName))
					return;

				// Stay — current tab already relevant for this cue.
				if (contentTabs.Contains(currentName))
					return;
			}
		}

		// Pick first priority tab that has content (respects drag-rearranged order via name lookup).
		foreach (string preferred in TabPriority)
		{
			if (!contentTabs.Contains(preferred))
				continue;

			int idx = FindTabIndexByName(preferred);
			if (idx >= 0)
			{
				CurrentTab = idx;
				return;
			}
		}
	}

	/// <summary>
	/// Updates tab titles/icons to show which tabs have components for the focused cue.
	/// </summary>
	private void RefreshIndicators(HashSet<string> contentTabs)
	{
		contentTabs ??= new HashSet<string>();

		for (int i = 0; i < GetTabCount(); i++)
		{
			var control = GetTabControl(i);
			if (control == null)
				continue;

			string name = control.Name;
			if (!_baseTitles.TryGetValue(name, out string baseTitle))
			{
				baseTitle = name;
				_baseTitles[name] = baseTitle;
			}

			bool hasContent = contentTabs.Contains(name);
			// Title stays clean; icon marks presence. English key is cached; display is translated.
			SetTabTitle(i, UiLocalizer.T(baseTitle));
			if (_baseTooltips.TryGetValue(name, out string tipKey) && !string.IsNullOrEmpty(tipKey))
				SetTabTooltip(i, UiLocalizer.T(tipKey));
			SetTabIcon(i, hasContent ? _contentDotIcon : null);
		}
	}

	/// <summary>
	/// Maps cue components to inspector tab names that own that content.
	/// </summary>
	private static HashSet<string> BuildContentFlags(Cue cue)
	{
		var flags = new HashSet<string>(StringComparer.Ordinal);
		if (cue?.Components == null)
			return flags;

		foreach (var component in cue.Components)
		{
			if (component == null)
				continue;

			switch (component.Type)
			{
				case "Audio":
					flags.Add("Audio");
					break;
				case "Video":
					// Component type remains "Video"; inspector tab node was renamed to "Visual".
					flags.Add("Visual");
					break;
				case "Text":
					flags.Add("Text");
					break;
				case "Network":
					flags.Add("Network");
					break;
				case "Control":
					flags.Add("Control");
					break;
				case "CueLight":
				case "OscComponent":
				case "MidiOutput":
					flags.Add("Connection");
					break;
			}
		}

		return flags;
	}

	private static Cue ResolveCue(int cueId)
	{
		if (cueId < 0)
			return null;
		return CueList.FetchCueFromId(cueId);
	}

	private void CacheBaseTitles()
	{
		_baseTitles.Clear();
		_baseTooltips.Clear();
		for (int i = 0; i < GetTabCount(); i++)
		{
			var control = GetTabControl(i);
			if (control == null)
				continue;
			// Prefer existing title (scene may customize); fall back to node name.
			// Capture English before/after LocalizeTree via MetaTooltip on the page root.
			string title = GetTabTitle(i);
			if (string.IsNullOrEmpty(title))
				title = control.Name;
			_baseTitles[control.Name] = title;

			string tip = string.Empty;
			if (control.HasMeta(UiLocalizer.MetaTooltip))
				tip = control.GetMeta(UiLocalizer.MetaTooltip).AsString();
			if (string.IsNullOrEmpty(tip))
				tip = GetTabTooltip(i);
			if (string.IsNullOrEmpty(tip))
				tip = control.TooltipText;
			if (!string.IsNullOrEmpty(tip))
				_baseTooltips[control.Name] = tip;
		}
	}

	private int FindTabIndexByName(string tabName)
	{
		for (int i = 0; i < GetTabCount(); i++)
		{
			var control = GetTabControl(i);
			if (control != null && control.Name == tabName)
				return i;
		}
		return -1;
	}

	/// <summary>
	/// Builds a small accent-coloured circle used as the "has content" tab icon.
	/// </summary>
	private static Texture2D CreateContentDotIcon()
	{
		const int size = 10;
		var img = Image.CreateEmpty(size, size, false, Image.Format.Rgba8);
		img.Fill(Colors.Transparent);

		// Warm high accent so content dots read clearly against tab chrome.
		var color = GlobalStyles.HighColor3;
		float center = (size - 1) * 0.5f;
		float radius = size * 0.5f - 0.75f;
		float radiusSq = radius * radius;

		for (int y = 0; y < size; y++)
		{
			for (int x = 0; x < size; x++)
			{
				float dx = x - center;
				float dy = y - center;
				if (dx * dx + dy * dy <= radiusSq)
					img.SetPixel(x, y, color);
			}
		}

		return ImageTexture.CreateFromImage(img);
	}
}
