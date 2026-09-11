// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using Cue2.Services;
using Godot;

namespace Cue2.UI.Utilities;

public partial class ButtonStyles : Button
{
	private StyleBoxFlat _hoverStyle = GlobalStyles.HoverStyle();
	private void _onMouseEntered()
	{
		//this.AddThemeStyleboxOverride("panel", _hoverStyle);
	}
	private void _onMouseExited()
	{
		//this.RemoveThemeStyleboxOverride("panel");
	}
}
