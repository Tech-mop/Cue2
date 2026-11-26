// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using Godot;
using System;
using System.Globalization;

/// <summary>
/// Footer wall-clock. Updates once per second so idle skip-draw is not forced at 100 Hz.
/// </summary>
public partial class CurrentTime : Label
{
	/// <summary>Second currently shown (ticks / second), or -1 before the first paint.</summary>
	private int _shownSecond = -1;

	/// <summary>
	/// Formats local wall time for the footer clock (hours, minutes, seconds, AM/PM).
	/// </summary>
	/// <param name="now">Local time to display.</param>
	/// <returns>Display string such as <c>02:15:09 PM</c>.</returns>
	public static string FormatClock(DateTime now) => now.ToString("hh:mm:ss tt", CultureInfo.InvariantCulture);

	/// <inheritdoc />
	public override void _Process(double delta)
	{
		DateTime now = DateTime.Now;
		int second = (int)(now.Ticks / TimeSpan.TicksPerSecond);
		if (second == _shownSecond)
			return;
		_shownSecond = second;
		Text = FormatClock(now);
	}
}
