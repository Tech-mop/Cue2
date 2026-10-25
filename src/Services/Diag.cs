// SPDX-FileCopyrightText: 2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System;
using Godot;

namespace Cue2.Services;

/// <summary>
/// Log output for small helpers that also run in plain C# tests. Defaults to <see cref="GD.Print(string)"/>
/// and <see cref="GD.PrintErr(string)"/>; tests swap the delegates, because calling Godot without
/// the engine crashes the test host.
/// </summary>
internal static class Diag
{
	/// <summary>Writes an information line. Defaults to <see cref="GD.Print(string)"/>.</summary>
	internal static Action<string> Info = message => GD.Print(message);

	/// <summary>Writes an error line. Defaults to <see cref="GD.PrintErr(string)"/>.</summary>
	internal static Action<string> Error = message => GD.PrintErr(message);
}
