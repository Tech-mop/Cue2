// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using Godot;
using System;

public partial class CurrentTime : Label
{
	private String _time;

	// Called when the node enters the scene tree for the first time.
	public override void _Ready()
	{


	}

	// Called every frame. 'delta' is the elapsed time since the previous frame.
	public override void _Process(double delta)
	{
		Text = DateTime.Now.ToString("hh:mm:ss:ff tt");
	}
}
