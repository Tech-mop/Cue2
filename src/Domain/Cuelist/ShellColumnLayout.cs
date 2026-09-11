// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System;
using Godot;

namespace Cue2.Domain.Cuelist;

/// <summary>
/// Shared column metrics for <see cref="ShellBar"/> rows and the cuelist header.
/// Fixed chrome columns (drag, expand, type, status) always reserve space so
/// optional UI (collapse chevron, issue X) never shifts the name/number columns.
/// </summary>
/// <remarks>
/// Industry-style tree table: reserved tree chrome + nested indent + flex name column.
/// Number and time column widths are user-resizable via the cuelist header.
/// <see cref="Scale"/> multiplies row heights, chrome, and fonts for the General Settings
/// "Cue list scale" option (UI-only; does not affect playback).
/// </remarks>
public static class ShellColumnLayout
{
	// ── Base (Medium / 1.0) metrics ─────────────────────────────────────────

	/// <summary>Base left color strip width at scale 1.0.</summary>
	public const float BaseColorWidth = 3f;

	/// <summary>Base nest gap between color strip and content at scale 1.0.</summary>
	public const int BaseColorNestGap = 1;

	/// <summary>Base drag handle column width at scale 1.0.</summary>
	public const float BaseDragWidth = 18f;

	/// <summary>Base expand/collapse chevron column width at scale 1.0.</summary>
	public const float BaseCollapseWidth = 18f;

	/// <summary>Base issue / status indicator column width at scale 1.0.</summary>
	public const float BaseIssueWidth = 14f;

	/// <summary>Base horizontal indent per nesting level at scale 1.0.</summary>
	public const float BaseNestIndent = 16f;

	/// <summary>Base continue / follow mode column width at scale 1.0.</summary>
	public const float BaseFollowWidth = 34f;

	/// <summary>Base height of inline controls inside a shell row at scale 1.0.</summary>
	public const float BaseRowControlHeight = 20f;

	/// <summary>Base minimum height of the cue row strip at scale 1.0.</summary>
	public const float BaseRowMinHeight = 24f;

	/// <summary>Base HBox separation between shell row columns at scale 1.0.</summary>
	public const int BaseRowSeparation = 2;

	/// <summary>Base shell field font size at scale 1.0.</summary>
	public const int BaseFontSize = 10;

	/// <summary>Base cuelist header label font size at scale 1.0.</summary>
	public const int BaseHeaderFontSize = 9;

	/// <summary>Base icon max width for drag / chrome buttons at scale 1.0.</summary>
	public const int BaseIconMaxWidth = 14;

	/// <summary>Default cue-number column width (user-resizable, not multiplied by scale).</summary>
	public const float DefaultNumberWidth = 48f;

	/// <summary>Minimum cue-number column width.</summary>
	public const float MinNumberWidth = 28f;

	/// <summary>Maximum cue-number column width.</summary>
	public const float MaxNumberWidth = 120f;

	/// <summary>Default width for pre-wait / duration / post-wait fields.</summary>
	public const float DefaultTimeWidth = 60f;

	/// <summary>Minimum time-field width.</summary>
	public const float MinTimeWidth = 44f;

	/// <summary>Maximum time-field width.</summary>
	public const float MaxTimeWidth = 140f;

	private static float _scale = 1.0f;
	private static float _numberWidth = DefaultNumberWidth;
	private static float _timeWidth = DefaultTimeWidth;

	private static float _compactStyleScale = float.NaN;
	private static StyleBoxFlat _compactNormal;
	private static StyleBoxFlat _compactFocus;

	/// <summary>
	/// Fired after a user-resizable column width or <see cref="Scale"/> changes.
	/// Listeners (shell rows, header, zebra) re-apply sizes.
	/// </summary>
	public static event Action Changed;

	/// <summary>
	/// Cuelist UI scale factor (Small ≈ 0.85, Medium = 1.0, Large ≈ 1.25).
	/// Multiplies row heights, fixed chrome, nest indent, and fonts only.
	/// </summary>
	public static float Scale
	{
		get => _scale;
		set
		{
			float clamped = Mathf.Clamp(value, 0.5f, 2.0f);
			if (Mathf.IsEqualApprox(_scale, clamped))
				return;
			_scale = clamped;
			InvalidateCompactStyles();
			Changed?.Invoke();
		}
	}

	/// <summary>Sets <see cref="Scale"/> without raising <see cref="Changed"/>.</summary>
	/// <param name="scale">Desired scale factor.</param>
	public static void SetScaleSilent(float scale)
	{
		_scale = Mathf.Clamp(scale, 0.5f, 2.0f);
		InvalidateCompactStyles();
	}

	/// <summary>
	/// Shared compact LineEdit styleboxes for shell fields (one pair per scale).
	/// </summary>
	/// <param name="field">LineEdit to style.</param>
	public static void ApplyCompactLineEditStyleBoxes(LineEdit field)
	{
		if (field == null)
			return;
		EnsureCompactStyles();
		if (_compactNormal != null)
		{
			field.AddThemeStyleboxOverride("normal", _compactNormal);
			field.AddThemeStyleboxOverride("read_only", _compactNormal);
		}
		if (_compactFocus != null)
			field.AddThemeStyleboxOverride("focus", _compactFocus);
	}

	private static void InvalidateCompactStyles()
	{
		// Drop C# refs only. LineEdits may still hold the native StyleBox until
		// ApplyCompactLineEditStyleBoxes runs; disposing here would UAF.
		_compactStyleScale = float.NaN;
		_compactNormal = null;
		_compactFocus = null;
	}

	/// <summary>
	/// Releases shared compact LineEdit styleboxes (call on cuelist teardown).
	/// </summary>
	public static void DisposeCompactStyles()
	{
		Cue2.UI.Utilities.UiUtilities.DisposeRefCounted(_compactNormal);
		Cue2.UI.Utilities.UiUtilities.DisposeRefCounted(_compactFocus);
		_compactNormal = null;
		_compactFocus = null;
	}

	private static void EnsureCompactStyles()
	{
		if (_compactNormal != null && Mathf.IsEqualApprox(_compactStyleScale, _scale))
			return;

		_compactStyleScale = _scale;
		float padH = Mathf.Max(2f, 4f * _scale);
		float padV = Mathf.Max(1f, 2f * _scale);
		_compactNormal = new StyleBoxFlat
		{
			BgColor = new Color(0.12f, 0.12f, 0.12f, 0.55f),
			ContentMarginLeft = padH,
			ContentMarginRight = padH,
			ContentMarginTop = padV,
			ContentMarginBottom = padV
		};
		_compactNormal.SetCornerRadiusAll(3);
		_compactFocus = (StyleBoxFlat)_compactNormal.Duplicate();
		_compactFocus.SetBorderWidthAll(1);
		_compactFocus.BorderColor = new Color(0.02f, 0.33f, 0.36f, 0.9f);
	}

	// ── Scaled metrics ──────────────────────────────────────────────────────

	/// <summary>Left color strip width (scaled).</summary>
	public static float ColorWidth => Mathf.Max(1f, BaseColorWidth * _scale);

	/// <summary>Horizontal gap between color strip and content (scaled, at least 1).</summary>
	public static int ColorNestGap => Mathf.Max(1, Mathf.RoundToInt(BaseColorNestGap * _scale));

	/// <summary>Drag handle column (scaled).</summary>
	public static float DragWidth => Mathf.Max(12f, BaseDragWidth * _scale);

	/// <summary>Expand/collapse chevron column (scaled).</summary>
	public static float CollapseWidth => Mathf.Max(12f, BaseCollapseWidth * _scale);

	/// <summary>Issue / status indicator column (scaled).</summary>
	public static float IssueWidth => Mathf.Max(10f, BaseIssueWidth * _scale);

	/// <summary>Horizontal indent per nesting level (scaled).</summary>
	public static float NestIndent => Mathf.Max(8f, BaseNestIndent * _scale);

	/// <summary>Continue / follow mode column (scaled).</summary>
	public static float FollowWidth => Mathf.Max(24f, BaseFollowWidth * _scale);

	/// <summary>Height of inline controls inside a shell row (scaled).</summary>
	public static float RowControlHeight => Mathf.Max(14f, BaseRowControlHeight * _scale);

	/// <summary>Minimum height of the cue row strip (scaled).</summary>
	public static float RowMinHeight => Mathf.Max(16f, BaseRowMinHeight * _scale);

	/// <summary>HBox separation between shell row columns (scaled, at least 1).</summary>
	public static int RowSeparation => Mathf.Max(1, Mathf.RoundToInt(BaseRowSeparation * _scale));

	/// <summary>Shell field font size (scaled).</summary>
	public static int FontSize => Mathf.Max(8, Mathf.RoundToInt(BaseFontSize * _scale));

	/// <summary>Cuelist header label font size (scaled).</summary>
	public static int HeaderFontSize => Mathf.Max(7, Mathf.RoundToInt(BaseHeaderFontSize * _scale));

	/// <summary>Icon max width for drag / chrome buttons (scaled).</summary>
	public static int IconMaxWidth => Mathf.Max(10, Mathf.RoundToInt(BaseIconMaxWidth * _scale));

	/// <summary>User-resizable cue number column width (absolute; not multiplied by scale).</summary>
	public static float NumberWidth
	{
		get => _numberWidth;
		set
		{
			float clamped = Mathf.Clamp(value, MinNumberWidth, MaxNumberWidth);
			if (Mathf.IsEqualApprox(_numberWidth, clamped))
				return;
			_numberWidth = clamped;
			Changed?.Invoke();
		}
	}

	/// <summary>User-resizable width shared by pre-wait, duration, and post-wait columns.</summary>
	public static float TimeWidth
	{
		get => _timeWidth;
		set
		{
			float clamped = Mathf.Clamp(value, MinTimeWidth, MaxTimeWidth);
			if (Mathf.IsEqualApprox(_timeWidth, clamped))
				return;
			_timeWidth = clamped;
			Changed?.Invoke();
		}
	}

	/// <summary>
	/// Width of fixed left chrome before tree indent for a root row
	/// (one colour strip + nest gap + drag + issue + separations).
	/// Nested rows add further colour strips to the left of this chrome.
	/// </summary>
	public static float FixedLeftChromeWidth =>
		ColorWidth + ColorNestGap
		+ DragWidth + RowSeparation
		+ IssueWidth + RowSeparation;

	/// <summary>
	/// Sets widths without raising <see cref="Changed"/> (e.g. load prefs, then apply once).
	/// </summary>
	/// <param name="numberWidth">Cue number column width.</param>
	/// <param name="timeWidth">Pre/duration/post column width.</param>
	public static void SetWidthsSilent(float numberWidth, float timeWidth)
	{
		_numberWidth = Mathf.Clamp(numberWidth, MinNumberWidth, MaxNumberWidth);
		_timeWidth = Mathf.Clamp(timeWidth, MinTimeWidth, MaxTimeWidth);
	}

	/// <summary>
	/// Raises <see cref="Changed"/> so all listeners re-apply current widths and scale.
	/// </summary>
	public static void NotifyChanged()
	{
		Changed?.Invoke();
	}
}
