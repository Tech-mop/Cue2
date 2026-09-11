// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using Godot;

public partial class LaunchManager : Control
{
	private void _on_exit_pressed()
	{
		GetTree().Quit();
	}

	private void _on_new_pressed()
	{
		GD.Print("New Show");
	}
}
