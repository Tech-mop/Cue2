// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using Cue2.Services;
using Godot;

namespace Cue2.UI.Windows;

/// <summary>
/// Scene script for the Save As file dialog. Path handling and persistence live in
/// <see cref="SaveManager"/> (this class only seeds filters if the scene is used standalone).
/// </summary>
public partial class SaveDialog : FileDialog
{
	/// <inheritdoc />
	public override void _Ready()
	{
		FileMode = FileModeEnum.SaveFile;
	}
}