// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System;
using System.IO;
using Cue2.Services;
using Godot;

namespace Cue2.UI.Windows;

public partial class FileDialogue : FileDialog
{

	private GlobalSignals _globalSignals;
	public override void _Ready()
	{
		_globalSignals = GetNode<GlobalSignals>("/root/GlobalSignals");
	}

	private void _on_file_selected(String @path)
	{
		string extention = Path.GetExtension(@path);

		if (String.IsNullOrEmpty(extention))
		{
			_globalSignals.EmitSignal(nameof(GlobalSignals.Log), "File select failed: " + extention + " Invalid File Type");
			return;
		}
		else
		{
			_globalSignals.EmitSignal(nameof(GlobalSignals.FileSelected), @path);
		}

	}
}
