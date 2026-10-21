// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using Godot;
using System;

public partial class CurrentTime : Label
{
	private String _time;
	// Hundredths value currently shown, so the label is only re-set when its text changes.
	private long _shownCentiseconds = -1;

	// Called when the node enters the scene tree for the first time.
	public override void _Ready()
	{


	}

	// Called every frame. 'delta' is the elapsed time since the previous frame.
	public override void _Process(double delta)
	{
		DateTime now = DateTime.Now;
		long centiseconds = now.Ticks / (TimeSpan.TicksPerMillisecond * 10);
		if (centiseconds == _shownCentiseconds)
			return;
		_shownCentiseconds = centiseconds;
		Text = now.ToString("hh:mm:ss:ff tt");
	}
}
