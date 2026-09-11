// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using Godot;
using System;
using System.Collections;

namespace Cue2.Services;
public partial class GlobalStyles : Node
{
	private Theme _theme;

	private static StyleBoxFlat _hoverStyle = new StyleBoxFlat();
	private static StyleBoxFlat _focusedStyle = new StyleBoxFlat();
	/// <summary>Default shell-row panel (keeps content inset so borders stay visible).</summary>
	private static StyleBoxFlat _shellRowStyle = new StyleBoxFlat();
	public StyleBoxFlat NextStyle = new StyleBoxFlat();
	public StyleBoxFlat ActiveStyle = new StyleBoxFlat();
	public StyleBoxFlat DefaultStyle = new StyleBoxFlat();
	
	private static StyleBoxFlat _dangerStyle = new StyleBoxFlat();


	/// <summary>
	/// Peak warm (high) accent — brightest step of the high cascade (level 1).
	/// Change this single colour to re-theme all <see cref="HighColor1"/>–<see cref="HighColor5"/>.
	/// </summary>
	public static Color HighColor = new Color("#EB6F02");

	/// <summary>
	/// Peak cool (low) accent — brightest step of the low cascade (level 1).
	/// Change this single colour to re-theme all <see cref="LowColor1"/>–<see cref="LowColor5"/>.
	/// </summary>
	public static Color LowColor = new Color("#03838F");

	/// <summary>
	/// Darken amounts for cascade levels 1–5 (index 0 = peak / brightest, index 4 = deepest).
	/// Tuned to approximate the previous hand-picked high/low ramps.
	/// </summary>
	private static readonly float[] CascadeDarkenFactors = { 0f, 0.22f, 0.40f, 0.58f, 0.76f };

	public static Color HighColor1 => CascadeBrightness(HighColor, 1);
	public static Color HighColor2 => CascadeBrightness(HighColor, 2);
	public static Color HighColor3 => CascadeBrightness(HighColor, 3);
	public static Color HighColor4 => CascadeBrightness(HighColor, 4);
	public static Color HighColor5 => CascadeBrightness(HighColor, 5);

	public static Color LowColor1 => CascadeBrightness(LowColor, 1);
	public static Color LowColor2 => CascadeBrightness(LowColor, 2);
	public static Color LowColor3 => CascadeBrightness(LowColor, 3);
	public static Color LowColor4 => CascadeBrightness(LowColor, 4);
	public static Color LowColor5 => CascadeBrightness(LowColor, 5);

	/// <summary>
	/// Builds a cascade step from a peak colour.
	/// </summary>
	/// <param name="peak">Level-1 (brightest) colour.</param>
	/// <param name="level">Step in 1–5 (1 = peak, 5 = darkest).</param>
	/// <returns>Darkened colour for that step; alpha preserved from <paramref name="peak"/>.</returns>
	public static Color CascadeBrightness(Color peak, int level)
	{
		int index = Mathf.Clamp(level, 1, 5) - 1;
		float amount = CascadeDarkenFactors[index];
		if (amount <= 0f)
			return peak;

		// Darken RGB toward black; keep peak alpha.
		Color darkened = peak.Darkened(amount);
		darkened.A = peak.A;
		return darkened;
	}

	/// <summary>High cascade colour for the given level (1–5).</summary>
	/// <param name="level">1 = brightest, 5 = darkest.</param>
	public static Color GetHighColor(int level) => CascadeBrightness(HighColor, level);

	/// <summary>Low cascade colour for the given level (1–5).</summary>
	/// <param name="level">1 = brightest, 5 = darkest.</param>
	public static Color GetLowColor(int level) => CascadeBrightness(LowColor, level);

	public static Color Danger = new Color("#ff806f");
	public static Color Warning = new Color("#ffb45d");
	public static Color Success = new Color("#9aff92");

	// List zebra base colours (also used for blank space under the cuelist).
	public static Color ZebraEven = new Color(0.145f, 0.155f, 0.165f, 1f);
	public static Color ZebraOdd = new Color(0.105f, 0.112f, 0.120f, 1f);
	
	/// <summary>
	/// Main window border in Edit Mode — cool low cascade (deep teal).
	/// </summary>
	public static Color WindowBorderEditMode => LowColor4;

	/// <summary>
	/// Main window border in Show Mode — warm high cascade (deepest) for live-performance visibility.
	/// </summary>
	public static Color WindowBorderShowMode => HighColor5;
	
	// Fonts and text colors
	public static Color SoftFontColor = new Color("#45606b"); 
	public static Color DisabledColor = new Color("#1d1d1d");

	/// <summary>Shell chrome interaction state (metrics stay fixed; only colours change).</summary>
	public enum ShellChromeState
	{
		Normal,
		Hover,
		Selected
	}

	
	public override void _Ready()
	{
		_theme = GetTree().Root.GetTheme();
		
		SetProcess(false); // This class is only for statics - disable process
		
		ConfigureShellRowStyle(
			_shellRowStyle,
			borderColor: new Color(0, 0, 0, 0),
			bgColor: ZebraOdd);

		ConfigureShellRowStyle(
			_hoverStyle,
			borderColor: new Color(0.15f, 0.75f, 0.85f, 0.85f),
			bgColor: ZebraEven.Lightened(0.08f));

		ConfigureShellRowStyle(
			NextStyle,
			borderColor: LowColor3,
			bgColor: new Color(LowColor2.R, LowColor2.G, LowColor2.B, 0.25f));

		// Strong selection outline (still 1px L/R so layout does not jump).
		ConfigureShellRowStyle(
			_focusedStyle,
			borderColor: new Color(0.25f, 0.92f, 1.0f, 1f),
			bgColor: new Color(0.08f, 0.42f, 0.48f, 0.55f));

		ConfigureShellRowStyle(
			ActiveStyle,
			borderColor: HighColor3,
			bgColor: new Color(HighColor2.R, HighColor2.G, HighColor2.B, 0.55f));

		ConfigureShellRowStyle(
			_dangerStyle,
			borderColor: HighColor2,
			bgColor: new Color(HighColor5.R, HighColor5.G, HighColor5.B, 0.5f));
	}

	/// <inheritdoc />
	public override void _ExitTree()
	{
		Cue2.UI.Utilities.UiUtilities.DisposeRefCounted(_hoverStyle);
		Cue2.UI.Utilities.UiUtilities.DisposeRefCounted(_focusedStyle);
		Cue2.UI.Utilities.UiUtilities.DisposeRefCounted(_shellRowStyle);
		Cue2.UI.Utilities.UiUtilities.DisposeRefCounted(_dangerStyle);
		Cue2.UI.Utilities.UiUtilities.DisposeRefCounted(NextStyle);
		Cue2.UI.Utilities.UiUtilities.DisposeRefCounted(ActiveStyle);
		Cue2.UI.Utilities.UiUtilities.DisposeRefCounted(DefaultStyle);
		_hoverStyle = null;
		_focusedStyle = null;
		_shellRowStyle = null;
		_dangerStyle = null;
		NextStyle = null;
		ActiveStyle = null;
		DefaultStyle = null;
		base._ExitTree();
	}


	/// <summary>
	/// Fixed geometry for every shell-row state (must stay identical across hover/select).
	/// Left/right border only; top/bottom margins 0 so color strips stack flush.
	/// </summary>
	private const int ShellBorderSide = 1;
	private const int ShellContentPadX = 2;

	/// <summary>
	/// Builds a shell-row StyleBox. Metrics are locked; only colors differ per state.
	/// </summary>
	private static void ConfigureShellRowStyle(
		StyleBoxFlat style,
		Color borderColor,
		Color bgColor)
	{
		if (style == null) return;
		ApplyShellChromeMetrics(style);
		style.BorderColor = borderColor;
		style.BgColor = bgColor;
	}

	/// <summary>
	/// Applies locked shell metrics (call when mutating a per-row StyleBox's colours only).
	/// </summary>
	public static void ApplyShellChromeMetrics(StyleBoxFlat style)
	{
		if (style == null) return;

		style.BorderWidthLeft = ShellBorderSide;
		style.BorderWidthRight = ShellBorderSide;
		style.BorderWidthTop = 0;
		style.BorderWidthBottom = 0;

		style.ContentMarginLeft = ShellBorderSide + ShellContentPadX;
		style.ContentMarginRight = ShellBorderSide + ShellContentPadX;
		style.ContentMarginTop = 0;
		style.ContentMarginBottom = 0;

		style.CornerRadiusTopLeft = 0;
		style.CornerRadiusTopRight = 0;
		style.CornerRadiusBottomLeft = 0;
		style.CornerRadiusBottomRight = 0;
		style.AntiAliasing = false;
	}

	/// <summary>
	/// Desaturates <paramref name="cueColor"/> toward luminance (wash for shell backgrounds).
	/// </summary>
	/// <param name="cueColor">Cue accent colour.</param>
	/// <param name="amount">0 = original, 1 = full grey.</param>
	public static Color Desaturate(Color cueColor, float amount = 0.6f)
	{
		amount = Mathf.Clamp(amount, 0f, 1f);
		float lum = cueColor.R * 0.299f + cueColor.G * 0.587f + cueColor.B * 0.114f;
		return new Color(
			Mathf.Lerp(cueColor.R, lum, amount),
			Mathf.Lerp(cueColor.G, lum, amount),
			Mathf.Lerp(cueColor.B, lum, amount),
			1f);
	}

	/// <summary>
	/// Mixes zebra base with a desaturated cue wash for the shell body.
	/// </summary>
	public static Color MixZebraAndCue(Color zebra, Color cueColor)
	{
		// Soft, darkened, desaturated cue tint over zebra.
		Color tint = Desaturate(cueColor, 0.55f).Darkened(0.28f);
		return zebra.Lerp(tint, 0.40f);
	}

	/// <summary>
	/// Final shell background for zebra index + cue colour + interaction state.
	/// </summary>
	public static Color ShellBackgroundFor(Color cueColor, bool evenZebra, ShellChromeState state)
	{
		Color zebra = evenZebra ? ZebraEven : ZebraOdd;
		Color mixed = MixZebraAndCue(zebra, cueColor);

		return state switch
		{
			// Strong cyan lift so selection reads clearly over cue wash.
			ShellChromeState.Selected => mixed
				.Lerp(new Color(0.12f, 0.62f, 0.72f), 0.52f)
				.Lightened(0.06f),
			ShellChromeState.Hover => mixed
				.Lightened(0.07f)
				.Lerp(new Color(0.08f, 0.40f, 0.45f), 0.18f),
			_ => mixed
		};
	}

	/// <summary>
	/// Shell border colour for interaction state (width stays fixed at 1px L/R).
	/// </summary>
	public static Color ShellBorderFor(ShellChromeState state)
	{
		return state switch
		{
			ShellChromeState.Selected => new Color(0.35f, 0.95f, 1.0f, 1f),
			ShellChromeState.Hover => new Color(0.15f, 0.70f, 0.80f, 0.9f),
			_ => new Color(0f, 0f, 0f, 0f)
		};
	}

	/// <summary>Default unselected shell row chrome (stable content inset).</summary>
	public static StyleBoxFlat ShellRowStyle()
	{
		return _shellRowStyle;
	}

	public static StyleBoxFlat FocusedStyle()
	{
		return _focusedStyle;
	}

	public static StyleBoxFlat HoverStyle()
	{
		return _hoverStyle;
	}

	public static StyleBoxFlat DangerStyle()
	{
		return _dangerStyle;
	}
}
