// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

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
using Godot;

namespace Cue2.UI.Shell;

/// <summary>
/// Full-area zebra stripes behind the cuelist (including blank space below the last cue).
/// Shells paint their own zebra+cue wash on top; this fills the rest of the scroll area.
/// Stripe height follows <see cref="ShellColumnLayout.RowMinHeight"/> (cuelist scale).
/// </summary>
public partial class CuelistZebraBackground : Control
{
	public override void _Ready()
	{
		MouseFilter = MouseFilterEnum.Ignore;
		// Sit behind scroll content (CueContainer + end pad) in the same MarginContainer.
		ZIndex = -1;
		SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
		ShellColumnLayout.Changed += OnShellColumnLayoutChanged;
		QueueRedraw();
	}

	public override void _ExitTree()
	{
		ShellColumnLayout.Changed -= OnShellColumnLayoutChanged;
		base._ExitTree();
	}

	private void OnShellColumnLayoutChanged()
	{
		if (IsInstanceValid(this))
			QueueRedraw();
	}

	public override void _Notification(int what)
	{
		if (what == NotificationResized || what == NotificationThemeChanged)
			QueueRedraw();
	}

	public override void _Draw()
	{
		float rowH = Mathf.Max(1f, ShellColumnLayout.RowMinHeight);
		float width = Size.X;
		float height = Size.Y;
		if (width <= 0f || height <= 0f)
			return;

		int row = 0;
		for (float y = 0f; y < height; y += rowH, row++)
		{
			Color c = (row % 2 == 0) ? GlobalStyles.ZebraEven : GlobalStyles.ZebraOdd;
			float h = Mathf.Min(rowH, height - y);
			DrawRect(new Rect2(0f, y, width, h), c, filled: true);
		}
	}
}
