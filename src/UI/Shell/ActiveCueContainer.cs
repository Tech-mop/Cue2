// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using Cue2.Services;
using Cue2.UI.Utilities;
using Godot;

namespace Cue2.UI.Shell;

/// <summary>
/// Host panel for the active-cues list chrome (header + transport buttons).
/// </summary>
/// <remarks>
/// Live active-cue rows are owned by <see cref="Cue2.Domain.Commands.CueCommandExecutor"/>,
/// which parents bars into <c>%ActiveCueList</c>. This script only wires Resume/Pause/Stop All
/// and localization — it does not track or free playback instances.
/// </remarks>
public partial class ActiveCueContainer : PanelContainer
{
	private GlobalSignals _globalSignals;

	/// <inheritdoc />
	public override void _Ready()
	{
		_globalSignals = GetNodeOrNull<GlobalSignals>("/root/GlobalSignals");

		GetNode<Button>("%ResumeAllButton").Pressed += () =>
			_globalSignals?.EmitSignal(nameof(GlobalSignals.ResumeAll));
		GetNode<Button>("%PauseAllButton").Pressed += () =>
			_globalSignals?.EmitSignal(nameof(GlobalSignals.PauseAll));
		GetNode<Button>("%StopAllButton").Pressed += () =>
			_globalSignals?.EmitSignal(nameof(GlobalSignals.StopAll));

		UiLocalizer.LocalizeTree(this);
		if (_globalSignals != null)
			_globalSignals.LocaleChanged += OnLocaleChanged;
	}

	/// <inheritdoc />
	public override void _ExitTree()
	{
		if (_globalSignals != null)
			_globalSignals.LocaleChanged -= OnLocaleChanged;
		base._ExitTree();
	}

	/// <summary>
	/// Re-localizes active-cue panel chrome when the UI language changes.
	/// </summary>
	/// <param name="localeCode">New locale code.</param>
	private void OnLocaleChanged(string localeCode)
	{
		if (!GodotObject.IsInstanceValid(this))
			return;
		UiLocalizer.LocalizeTree(this);
	}
}
