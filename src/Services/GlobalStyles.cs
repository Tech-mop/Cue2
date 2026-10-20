// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using Godot;
using System;
using System.Collections;
using System.Collections.Generic;

namespace Cue2.Services;
public partial class GlobalStyles : Node
{
	private Theme _theme;
	private bool _nodeAddedWired;

	private static StyleBoxFlat _hoverStyle = new StyleBoxFlat();
	private static StyleBoxFlat _focusedStyle = new StyleBoxFlat();
	/// <summary>Default shell-row panel (keeps content inset so borders stay visible).</summary>
	private static StyleBoxFlat _shellRowStyle = new StyleBoxFlat();
	public StyleBoxFlat NextStyle = new StyleBoxFlat();
	public StyleBoxFlat ActiveStyle = new StyleBoxFlat();
	public StyleBoxFlat DefaultStyle = new StyleBoxFlat();
	
	private static StyleBoxFlat _dangerStyle = new StyleBoxFlat();


	/// <summary>Stable id for the shipped Cue2 theme (<c>base_theme.tres</c>).</summary>
	public const string DefaultThemeId = "cue2";

	/// <summary>Godot Theme resource for <see cref="DefaultThemeId"/>.</summary>
	public const string DefaultThemeResourcePath = "res://src/UI/Assets/base_theme.tres";

	/// <summary>GO button Theme — never replaced when applying a UI theme.</summary>
	public const string GoThemeResourcePath = "res://src/UI/Assets/GO_theme.tres";

	/// <summary>Settings OptionButton label for the shipped theme.</summary>
	public const string DefaultThemeDisplayName = "Cue2 (Default)";

	/// <summary>Stable id for the shipped dark theme.</summary>
	public const string DarkThemeId = "dark";

	/// <summary>Godot Theme resource for <see cref="DarkThemeId"/>.</summary>
	public const string DarkThemeResourcePath = "res://src/UI/Assets/dark_theme.tres";

	/// <summary>Settings OptionButton label for the dark theme.</summary>
	public const string DarkThemeDisplayName = "Dark";

	/// <summary>Stable id for the shipped light theme.</summary>
	public const string LightThemeId = "light";

	/// <summary>Godot Theme resource for <see cref="LightThemeId"/>.</summary>
	public const string LightThemeResourcePath = "res://src/UI/Assets/light_theme.tres";

	/// <summary>Settings OptionButton label for the light theme.</summary>
	public const string LightThemeDisplayName = "Light";

	/// <summary>Stable id for the shipped neon theme.</summary>
	public const string NeonThemeId = "neon";

	/// <summary>Godot Theme resource for <see cref="NeonThemeId"/>.</summary>
	public const string NeonThemeResourcePath = "res://src/UI/Assets/neon_theme.tres";

	/// <summary>Settings OptionButton label for the neon theme.</summary>
	public const string NeonThemeDisplayName = "Neon";

	/// <summary>Factory peak warm (high) accent for the Cue2 theme.</summary>
	public static readonly Color DefaultHighColor = new Color("#EB6F02");

	/// <summary>Factory peak cool (low) accent for the Cue2 theme.</summary>
	public static readonly Color DefaultLowColor = new Color("#03838F");

	/// <summary>
	/// Built-in themes available in Cue2 Preferences. User-added <c>.tres</c> files are listed separately.
	/// </summary>
	public static readonly (string Id, string DisplayName, string ResourcePath)[] BuiltInThemes =
	{
		(DefaultThemeId, DefaultThemeDisplayName, DefaultThemeResourcePath),
		(DarkThemeId, DarkThemeDisplayName, DarkThemeResourcePath),
		(LightThemeId, LightThemeDisplayName, LightThemeResourcePath),
		(NeonThemeId, NeonThemeDisplayName, NeonThemeResourcePath)
	};

	/// <summary>Themes loaded after the user (or startup prefs) selected them. Others stay on disk.</summary>
	private static readonly Dictionary<string, Theme> _loadedBuiltInThemes = new(StringComparer.OrdinalIgnoreCase);

	/// <summary>
	/// Peak warm (high) accent — brightest step of the high cascade (level 1).
	/// Change this single colour to re-theme all <see cref="HighColor1"/>–<see cref="HighColor5"/>.
	/// </summary>
	public static Color HighColor = DefaultHighColor;

	/// <summary>
	/// Peak cool (low) accent — brightest step of the low cascade (level 1).
	/// Change this single colour to re-theme all <see cref="LowColor1"/>–<see cref="LowColor5"/>.
	/// </summary>
	public static Color LowColor = DefaultLowColor;

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

	/// <summary>Theme type for Cue2 chrome colours (<c>Cue2/colors/*</c> in the Theme resource).</summary>
	public const string Cue2ThemeType = "Cue2";

	// List zebra base colours (also used for blank space under the cuelist). From the Theme.
	public static Color ZebraEven = new Color(0.145f, 0.155f, 0.165f, 1f);
	public static Color ZebraOdd = new Color(0.105f, 0.112f, 0.120f, 1f);

	/// <summary>Title-bar strip colour from <c>Cue2/colors/title_bar</c>.</summary>
	public static Color TitleBarColor = new Color(0.0588235f, 0.0588235f, 0.0588235f, 1f);

	/// <summary>Default icon colour from <c>Cue2/colors/icon_normal</c>.</summary>
	public static Color IconNormalColor = Colors.White;

	/// <summary>Hover icon colour from <c>Cue2/colors/icon_hover</c>.</summary>
	public static Color IconHoverColor = Colors.White;
	
	/// <summary>
	/// Main window border in Edit Mode — cool low cascade (deep teal).
	/// </summary>
	public static Color WindowBorderEditMode => LowColor4;

	/// <summary>
	/// Main window border in Show Mode — warm high cascade (deepest) for live-performance visibility.
	/// </summary>
	public static Color WindowBorderShowMode => HighColor5;
	
	// Fonts and text colors
	/// <summary>Muted inspector/settings label colour from <c>Cue2/colors/soft_font</c>.</summary>
	public static Color SoftFontColor = new Color("#45606b");
	/// <summary>Disabled / empty-state text from <c>Cue2/colors/disabled</c>.</summary>
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

		var userData = GetNodeOrNull<GlobalData>("/root/GlobalData")?.UserDataManager;
		if (userData != null)
		{
			HighColor = userData.ThemeHighColor;
			LowColor = userData.ThemeLowColor;
		}
		
		ConfigureShellRowStyle(
			_shellRowStyle,
			borderColor: new Color(0, 0, 0, 0),
			bgColor: ZebraOdd);

		ConfigureShellRowStyle(
			_hoverStyle,
			borderColor: new Color(0.15f, 0.75f, 0.85f, 0.85f),
			bgColor: ZebraEven.Lightened(0.08f));

		// Strong selection outline (still 1px L/R so layout does not jump).
		ConfigureShellRowStyle(
			_focusedStyle,
			borderColor: new Color(0.25f, 0.92f, 1.0f, 1f),
			bgColor: new Color(0.08f, 0.42f, 0.48f, 0.55f));

		RefreshAccentStyleBoxes();
		CallDeferred(nameof(ApplyFromUserDataDeferred));
	}

	/// <summary>
	/// Applies the persisted Theme after autoloads and the main scene are in the tree.
	/// </summary>
	private void ApplyFromUserDataDeferred()
	{
		var userData = GetNodeOrNull<GlobalData>("/root/GlobalData")?.UserDataManager;
		ApplyFromUserData(userData);
		EnsureNodeAddedWired();
	}

	/// <summary>
	/// Sets peak high/low accents, refreshes cached StyleBoxes that use the cascades,
	/// and optionally emits <see cref="GlobalSignals.ThemeColorsChanged"/>.
	/// </summary>
	/// <param name="high">Peak warm accent (level 1).</param>
	/// <param name="low">Peak cool accent (level 1).</param>
	/// <param name="notify">When true, broadcast so cached chrome can refresh.</param>
	public void ApplyAccentColors(Color high, Color low, bool notify = true)
	{
		bool changed = !HighColor.IsEqualApprox(high) || !LowColor.IsEqualApprox(low);
		HighColor = high;
		LowColor = low;
		if (changed)
			RefreshAccentStyleBoxes();

		if (notify && changed)
		{
			var signals = GetNodeOrNull<GlobalSignals>("/root/GlobalSignals");
			signals?.EmitSignal(nameof(GlobalSignals.ThemeColorsChanged));
		}
	}

	/// <summary>
	/// Rebuilds StyleBoxes that sample the high/low cascades. Does not mutate
	/// <c>base_theme.tres</c> or other shared Theme resources.
	/// </summary>
	private void RefreshAccentStyleBoxes()
	{
		ConfigureShellRowStyle(
			NextStyle,
			borderColor: LowColor3,
			bgColor: new Color(LowColor2.R, LowColor2.G, LowColor2.B, 0.25f));

		ConfigureShellRowStyle(
			ActiveStyle,
			borderColor: HighColor3,
			bgColor: new Color(HighColor2.R, HighColor2.G, HighColor2.B, 0.55f));

		ConfigureShellRowStyle(
			_dangerStyle,
			borderColor: HighColor2,
			bgColor: new Color(HighColor5.R, HighColor5.G, HighColor5.B, 0.5f));
	}

	/// <summary>
	/// Tints the active-tab highlight (selected / focus top border) to
	/// <see cref="HighColor1"/>. Duplicates StyleBoxes so <c>base_theme.tres</c> is not mutated.
	/// </summary>
	/// <param name="tabs">Inspector or cuelist <see cref="TabContainer"/>.</param>
	public static void ApplyTabContainerAccent(TabContainer tabs)
	{
		if (tabs == null || !GodotObject.IsInstanceValid(tabs))
			return;

		StyleBoxFlat selected = EnsureTabStyleOverride(tabs, "tab_selected");
		StyleBoxFlat focus = EnsureTabStyleOverride(tabs, "tab_focus");
		if (selected != null)
			selected.BorderColor = HighColor1;
		if (focus != null)
			focus.BorderColor = HighColor1;

		TabBar bar = tabs.GetTabBar();
		if (bar == null || !GodotObject.IsInstanceValid(bar))
			return;
		if (selected != null)
			bar.AddThemeStyleboxOverride("tab_selected", selected);
		if (focus != null)
			bar.AddThemeStyleboxOverride("tab_focus", focus);
	}

	/// <summary>
	/// Returns a per-control duplicate of a TabContainer StyleBox, creating the override once.
	/// </summary>
	private static StyleBoxFlat EnsureTabStyleOverride(Control control, string styleName)
	{
		if (control.HasThemeStyleboxOverride(styleName))
			control.RemoveThemeStyleboxOverride(styleName);

		var style = control.GetThemeStylebox(styleName)?.Duplicate() as StyleBoxFlat;
		if (style == null)
			return null;
		control.AddThemeStyleboxOverride(styleName, style);
		return style;
	}

	/// <summary>
	/// Theme resource currently applied to Cue2 windows (default or user-added).
	/// </summary>
	/// <summary>Theme resource currently applied to Cue2 windows (default, dark, light, or user-added).</summary>
	public static Theme ActiveUiTheme { get; private set; }

	/// <summary>
	/// Loads and applies the Theme for the current <see cref="UserDataManager.ThemeId"/>.
	/// Missing or invalid user themes fall back to Cue2 (Default) visually.
	/// </summary>
	/// <param name="userData">User preferences; ignored when null.</param>
	public void ApplyFromUserData(UserDataManager userData)
	{
		Theme theme = LoadThemeForUserData(userData);
		ApplyUiTheme(theme);
	}

	/// <summary>
	/// Loads a Godot Theme from a filesystem or <c>res://</c> path.
	/// </summary>
	/// <param name="path">Absolute or resource path to a Theme <c>.tres</c>.</param>
	/// <param name="theme">Loaded Theme on success.</param>
	/// <param name="error">User-facing error on failure.</param>
	/// <returns>True when <paramref name="theme"/> is a valid Theme.</returns>
	public bool TryLoadThemeResource(string path, out Theme theme, out string error)
	{
		theme = null;
		error = null;
		if (string.IsNullOrWhiteSpace(path))
		{
			error = "No theme path.";
			return false;
		}

		string loadPath = path.Trim();
		try
		{
			string localized = ProjectSettings.LocalizePath(loadPath);
			if (!string.IsNullOrEmpty(localized) &&
			    localized.StartsWith("res://", StringComparison.OrdinalIgnoreCase))
				loadPath = localized;
		}
		catch
		{
			// Keep the original path.
		}

		try
		{
			var loaded = ResourceLoader.Load(loadPath, "Theme", ResourceLoader.CacheMode.Replace);
			theme = loaded as Theme;
			if (theme == null)
			{
				error = "File is not a Godot Theme resource.";
				return false;
			}

			return true;
		}
		catch (Exception ex)
		{
			error = ex.Message;
			theme = null;
			return false;
		}
	}

	/// <summary>
	/// Assigns <paramref name="theme"/> to the root, all Window nodes, and Controls that
	/// currently use the app Theme. Leaves the GO button's <c>GO_theme.tres</c> alone.
	/// </summary>
	/// <param name="theme">Theme to apply; ignored when null.</param>
	public void ApplyUiTheme(Theme theme)
	{
		if (theme == null)
			return;

		ActiveUiTheme = theme;
		PullChromeFromTheme(theme);
		RefreshAccentStyleBoxes();
		var tree = GetTree();
		if (tree?.Root != null)
		{
			tree.Root.Theme = theme;
			ApplyThemeToNodeRecursive(tree.Root);
		}

		EnsureNodeAddedWired();

		var signals = GetNodeOrNull<GlobalSignals>("/root/GlobalSignals");
		signals?.EmitSignal(nameof(GlobalSignals.ThemeColorsChanged));
	}

	/// <summary>
	/// Copies Cue2 chrome colours from the Theme resource into the statics shells/inspectors read.
	/// Missing keys keep the previous value.
	/// </summary>
	/// <param name="theme">Active UI Theme.</param>
	private static void PullChromeFromTheme(Theme theme)
	{
		if (theme == null)
			return;

		ZebraEven = ReadCue2Color(theme, "zebra_even", ZebraEven);
		ZebraOdd = ReadCue2Color(theme, "zebra_odd", ZebraOdd);
		SoftFontColor = ReadCue2Color(theme, "soft_font", SoftFontColor);
		DisabledColor = ReadCue2Color(theme, "disabled", DisabledColor);
		TitleBarColor = ReadCue2Color(theme, "title_bar", TitleBarColor);
		IconNormalColor = ReadCue2Color(theme, "icon_normal", IconNormalColor);
		IconHoverColor = ReadCue2Color(theme, "icon_hover", IconHoverColor);
	}

	/// <summary>
	/// Reads a colour from the Cue2 theme type, or <paramref name="fallback"/> when absent.
	/// </summary>
	public static Color ReadCue2Color(Theme theme, string colorName, Color fallback)
	{
		if (theme != null && theme.HasColor(colorName, Cue2ThemeType))
			return theme.GetColor(colorName, Cue2ThemeType);
		return fallback;
	}

	private Theme LoadThemeForUserData(UserDataManager userData)
	{
		if (userData == null)
			return LoadDefaultUiTheme();

		if (UserDataManager.IsBuiltInThemeId(userData.ThemeId))
			return LoadBuiltInTheme(userData.ThemeId);

		var entry = userData.FindUserTheme(userData.ThemeId);
		if (entry == null)
			return LoadDefaultUiTheme();

		if (UserDataManager.IsUserThemeMissing(entry) ||
		    !TryLoadThemeResource(entry.Path, out Theme loaded, out _))
		{
			GD.Print($"GlobalStyles:LoadThemeForUserData - Theme missing or invalid, using Cue2 (Default): {entry.Path}");
			var signals = GetNodeOrNull<GlobalSignals>("/root/GlobalSignals");
			signals?.EmitSignal(nameof(GlobalSignals.Log),
				$"Theme file is missing: {entry.Path}", (int)LogType.Warning);
			return LoadDefaultUiTheme();
		}

		return loaded;
	}

	/// <summary>
	/// Loads a shipped Theme by id. Unknown ids fall back to Cue2 (Default).
	/// </summary>
	private Theme LoadBuiltInTheme(string themeId)
	{
		if (string.IsNullOrWhiteSpace(themeId))
			return LoadDefaultUiTheme();

		if (_loadedBuiltInThemes.TryGetValue(themeId, out Theme cached)
		    && GodotObject.IsInstanceValid(cached))
			return cached;

		foreach (var theme in BuiltInThemes)
		{
			if (!string.Equals(theme.Id, themeId, StringComparison.OrdinalIgnoreCase))
				continue;
			GD.Print($"GlobalStyles:LoadBuiltInTheme - Loading {theme.ResourcePath}");
			var loaded = ResourceLoader.Load<Theme>(theme.ResourcePath, "", ResourceLoader.CacheMode.Replace);
			if (loaded != null)
			{
				_loadedBuiltInThemes[theme.Id] = loaded;
				return loaded;
			}
		}

		return LoadDefaultUiTheme();
	}

	private Theme LoadDefaultUiTheme()
	{
		if (_loadedBuiltInThemes.TryGetValue(DefaultThemeId, out Theme cached)
		    && GodotObject.IsInstanceValid(cached))
			return cached;

		GD.Print($"GlobalStyles:LoadDefaultUiTheme - Loading {DefaultThemeResourcePath}");
		var loaded = ResourceLoader.Load<Theme>(DefaultThemeResourcePath, "", ResourceLoader.CacheMode.Reuse);
		if (loaded != null)
			_loadedBuiltInThemes[DefaultThemeId] = loaded;
		return loaded ?? _theme;
	}

	private void EnsureNodeAddedWired()
	{
		if (_nodeAddedWired)
			return;
		var tree = GetTree();
		if (tree == null)
			return;
		tree.NodeAdded += OnThemeNodeAdded;
		_nodeAddedWired = true;
	}

	private void OnThemeNodeAdded(Node node)
	{
		if (ActiveUiTheme == null || node == null || !GodotObject.IsInstanceValid(node))
			return;
		ApplyThemeToNode(node);
	}

	private void ApplyThemeToNodeRecursive(Node node)
	{
		if (node == null || !GodotObject.IsInstanceValid(node))
			return;
		ApplyThemeToNode(node);
		foreach (Node child in node.GetChildren())
			ApplyThemeToNodeRecursive(child);
	}

	private void ApplyThemeToNode(Node node)
	{
		if (ActiveUiTheme == null || node == null || !GodotObject.IsInstanceValid(node))
			return;

		if (node is Window window)
		{
			window.Theme = ActiveUiTheme;
			return;
		}

		if (node is Control control && control.Theme != null)
		{
			if (IsGoTheme(control.Theme))
				return;
			control.Theme = ActiveUiTheme;
		}
	}

	/// <summary>
	/// True when <paramref name="theme"/> is the GO button Theme (must not be replaced).
	/// </summary>
	public static bool IsGoTheme(Theme theme)
	{
		if (theme == null)
			return false;
		string path = theme.ResourcePath ?? string.Empty;
		return path.EndsWith("GO_theme.tres", StringComparison.OrdinalIgnoreCase)
			|| string.Equals(path, GoThemeResourcePath, StringComparison.OrdinalIgnoreCase);
	}

	/// <inheritdoc />
	public override void _ExitTree()
	{
		if (_nodeAddedWired)
		{
			var tree = GetTree();
			if (tree != null)
				tree.NodeAdded -= OnThemeNodeAdded;
			_nodeAddedWired = false;
		}

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
		float lum = zebra.R * 0.299f + zebra.G * 0.587f + zebra.B * 0.114f;
		if (lum >= 0.55f)
		{
			// Light zebra: keep rows white; cue wash is a hint only.
			Color tint = Desaturate(cueColor, 0.55f).Lightened(0.78f);
			return zebra.Lerp(tint, 0.07f);
		}

		Color darkTint = Desaturate(cueColor, 0.55f).Darkened(0.28f);
		return zebra.Lerp(darkTint, 0.40f);
	}

	/// <summary>
	/// Final shell background for zebra index + cue colour + interaction state.
	/// </summary>
	public static Color ShellBackgroundFor(Color cueColor, bool evenZebra, ShellChromeState state)
	{
		Color zebra = evenZebra ? ZebraEven : ZebraOdd;
		Color mixed = MixZebraAndCue(zebra, cueColor);

		float lum = zebra.R * 0.299f + zebra.G * 0.587f + zebra.B * 0.114f;
		if (lum >= 0.55f)
		{
			return state switch
			{
				ShellChromeState.Selected => mixed.Lerp(new Color(0.82f, 0.93f, 0.96f), 0.28f),
				ShellChromeState.Hover => mixed.Lerp(new Color(0.92f, 0.94f, 0.96f), 0.18f),
				_ => mixed
			};
		}

		return state switch
		{
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
