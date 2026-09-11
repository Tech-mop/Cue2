// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using Godot;

namespace Cue2.Services;

/// <summary>
/// Shared GO enable/disable gate. Call <see cref="DisableGo"/> / <see cref="EnableGo"/>;
/// UI and playback subscribe to <see cref="GoDisabled"/> / <see cref="GoEnabled"/>.
/// Multiple reasons can overlap (session load + double-GO protection).
/// </summary>
public partial class GlobalSignals
{
	/// <summary>Showfile apply is in progress (models not yet GO-safe).</summary>
	public const string GoDisableReasonSessionLoad = "session_load";

	/// <summary>Post-GO cooldown from General Settings → Double Go Protection.</summary>
	public const string GoDisableReasonDoubleGo = "double_go";

	private readonly HashSet<string> _goDisableReasons = new(StringComparer.Ordinal);
	private Timer _goEnableTimer;
	private string _goTimedReason;

	/// <summary>True when no disable reason is active and GO may fire.</summary>
	public bool IsGoEnabled => _goDisableReasons.Count == 0;

	/// <summary>Duration of the current timed disable, or 0 when indefinite / none.</summary>
	public float GoDisableDurationSeconds { get; private set; }

	/// <summary>Seconds left on the timed disable timer, or 0 when none.</summary>
	public float GoDisableRemainingSeconds
	{
		get
		{
			if (_goEnableTimer == null || !GodotObject.IsInstanceValid(_goEnableTimer) || _goEnableTimer.IsStopped())
				return 0f;
			return (float)_goEnableTimer.TimeLeft;
		}
	}

	/// <summary>
	/// Blocks GO until <see cref="EnableGo"/> is called with the same <paramref name="reason"/>.
	/// When <paramref name="durationSeconds"/> is greater than 0, GO is re-enabled automatically
	/// after that many seconds (refreshing the timer if this reason is already active).
	/// </summary>
	/// <param name="reason">Stable token identifying the caller (must match <see cref="EnableGo"/>).</param>
	/// <param name="durationSeconds">Auto-enable delay, or 0 for until <see cref="EnableGo"/>.</param>
	public void DisableGo(string reason, float durationSeconds = 0f)
	{
		if (string.IsNullOrWhiteSpace(reason))
			reason = "unspecified";

		bool wasEnabled = IsGoEnabled;
		_goDisableReasons.Add(reason);

		if (durationSeconds > 0.0001f)
		{
			GoDisableDurationSeconds = durationSeconds;
			StartGoEnableTimer(reason, durationSeconds);
		}
		else
		{
			if (string.Equals(_goTimedReason, reason, StringComparison.Ordinal))
				StopGoEnableTimer();
			if (wasEnabled)
				GoDisableDurationSeconds = 0f;
		}

		EmitSignal(SignalName.GoDisabled, reason, durationSeconds);
	}

	/// <summary>
	/// Clears a previous <see cref="DisableGo"/> for <paramref name="reason"/>.
	/// Emits <see cref="GoEnabled"/> only when no reasons remain.
	/// </summary>
	/// <param name="reason">The same token passed to <see cref="DisableGo"/>.</param>
	public void EnableGo(string reason)
	{
		if (string.IsNullOrWhiteSpace(reason))
			reason = "unspecified";

		if (string.Equals(_goTimedReason, reason, StringComparison.Ordinal))
			StopGoEnableTimer();

		if (!_goDisableReasons.Remove(reason))
			return;

		if (IsGoEnabled)
		{
			GoDisableDurationSeconds = 0f;
			EmitSignal(SignalName.GoEnabled);
		}
	}

	/// <summary>
	/// True when <paramref name="reason"/> is currently holding GO disabled.
	/// </summary>
	/// <param name="reason">Token from <see cref="DisableGo"/>.</param>
	/// <returns>True if that reason is active.</returns>
	public bool IsGoDisabledBy(string reason)
	{
		if (string.IsNullOrEmpty(reason))
			return false;
		return _goDisableReasons.Contains(reason);
	}

	private void StartGoEnableTimer(string reason, float durationSeconds)
	{
		EnsureGoEnableTimer();
		_goTimedReason = reason;
		_goEnableTimer.Stop();
		_goEnableTimer.WaitTime = durationSeconds;
		_goEnableTimer.Start();
	}

	private void StopGoEnableTimer()
	{
		_goTimedReason = null;
		if (_goEnableTimer != null && GodotObject.IsInstanceValid(_goEnableTimer))
			_goEnableTimer.Stop();
	}

	private void EnsureGoEnableTimer()
	{
		if (_goEnableTimer != null && GodotObject.IsInstanceValid(_goEnableTimer))
			return;

		_goEnableTimer = new Timer
		{
			Name = "GoEnableTimer",
			OneShot = true
		};
		_goEnableTimer.Timeout += OnGoEnableTimerTimeout;
		AddChild(_goEnableTimer);
	}

	private void OnGoEnableTimerTimeout()
	{
		string reason = _goTimedReason;
		_goTimedReason = null;
		if (!string.IsNullOrEmpty(reason))
			EnableGo(reason);
	}
}
