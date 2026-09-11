// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System;
using System.IO;
using Cue2.Services;
using Godot;

// This is a resource attached to:
// -OpenDialog: FileDialog (Found in Cue2Base scene)

namespace Cue2.UI.Windows;

/// <summary>
/// File dialog for opening Cue2 session (.c2) showfiles.
/// </summary>
public partial class OpenDialog : FileDialog
{
	private GlobalSignals _globalSignals;

	/// <inheritdoc />
	public override void _Ready()
	{
		_globalSignals = GetNode<GlobalSignals>("/root/GlobalSignals");

		// Prefer lowercase .c2 (matches save) but also accept .C2 from older files / OS case.
		ClearFilters();
		AddFilter("*.c2,*.C2 ; Cue2 Session");

		FileSelected += OnFileSelected;
	}

	/// <summary>
	/// Validates the selected path and requests session open.
	/// </summary>
	/// <param name="path">Absolute filesystem path chosen in the dialog.</param>
	private void OnFileSelected(string path)
	{
		string extension = Path.GetExtension(path ?? string.Empty);
		if (!string.Equals(extension, ".c2", StringComparison.OrdinalIgnoreCase))
		{
			_globalSignals.EmitSignal(nameof(GlobalSignals.Log),
				$"Not a valid session extension: {extension}", (int)LogType.Warning);
			return;
		}

		_globalSignals.EmitSignal(nameof(GlobalSignals.OpenSelectedSession), path);
	}
}
