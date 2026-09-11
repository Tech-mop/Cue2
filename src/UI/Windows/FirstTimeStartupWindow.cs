// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System;
using Cue2.Services;
using Cue2.UI.Utilities;
using Godot;
using static Cue2.UI.Utilities.UiLocalizer;

namespace Cue2.UI.Windows;

/// <summary>
/// First-time startup welcome sub-window shown when Cue2 opens for a new install
/// (or every launch while testing with the force-show flag).
/// </summary>
/// <remarks>
/// Welcome message, documentation/website links, language preference, optional UI scale
/// adjustment (same controls as Cue2 Preferences), then dismiss via Get Started or window chrome.
/// Dismiss marks <see cref="UserDataManager.IsFirstTimeStartup"/> complete.
/// </remarks>
public partial class FirstTimeStartupWindow : Window
{
	private GlobalData _globalData;
	private GlobalSignals _globalSignals;
	private UserDataManager _userDataManager;
	private LocalizationService _localization;

	private LinkButton _docsLinkButton;
	private LinkButton _websiteLinkButton;
	private Button _getStartedButton;

	private Label _titleLabel;
	private Label _welcomeLabel;
	private Label _languageLabel;
	private OptionButton _languageOptionButton;
	private Label _uiScaleLabel;
	private LineEdit _uiScaleNum;
	private HSlider _uiScaleSlider;
	private Button _uiScaleResetButton;

	/// <summary>True while pushing model → controls so handlers do not re-apply.</summary>
	private bool _isSyncingUi;

	/// <summary>True while rebuilding the language option list.</summary>
	private bool _isSyncingLanguage;

	/// <summary>
	/// Applies Linux native-window policy before the scene window enters the tree.
	/// </summary>
	public FirstTimeStartupWindow()
	{
		LinuxWindowEmbedPolicy.ApplyToAppWindow(this);
	}

	/// <summary>
	/// Initializes UI, applies scale, wires dismiss, language, and UI-scale handlers.
	/// </summary>
	public override void _Ready()
	{
		_globalData = GetNode<GlobalData>("/root/GlobalData");
		_globalSignals = GetNode<GlobalSignals>("/root/GlobalSignals");
		_userDataManager = _globalData?.UserDataManager;
		_localization = _globalData?.LocalizationService;

		_docsLinkButton = GetNode<LinkButton>("%DocsLinkButton");
		_websiteLinkButton = GetNode<LinkButton>("%WebsiteLinkButton");
		_getStartedButton = GetNode<Button>("%GetStartedButton");
		_titleLabel = GetNodeOrNull<Label>("%TitleLabel");
		_welcomeLabel = GetNodeOrNull<Label>("%WelcomeLabel");
		_languageLabel = GetNodeOrNull<Label>("%LanguageLabel");
		_uiScaleLabel = GetNodeOrNull<Label>("%UiScaleLabel");

		_docsLinkButton.Uri = Version.DocsWebsite;
		_websiteLinkButton.Uri = Version.Website;

		_getStartedButton.Pressed += OnGetStartedPressed;
		CloseRequested += OnCloseRequested;

		WireLanguageControls();
		WireUiScaleControls();
		SyncLanguageControls();
		SyncUiScaleControls();
		ApplyLocalizedStrings();

		float userScale = _userDataManager?.UiScale ?? UserDataManager.DefaultUiScale;
		// Size is the scene design size (480×560). Apply after GlobalData's deferred scale refresh.
		UiUtilities.ApplyScaledDesignWindowSize(this, Size, _globalData.BaseDisplayScale);
		UiUtilities.RescaleUi(this, userScale, _globalData.BaseDisplayScale);

		_globalSignals.UiScaleChanged += ScaleUi;
		_globalSignals.LocaleChanged += OnLocaleChanged;
	}

	/// <summary>
	/// Wires the language OptionButton for first-time language selection.
	/// </summary>
	private void WireLanguageControls()
	{
		_languageOptionButton = GetNodeOrNull<OptionButton>("%LanguageOptionButton");
		if (_languageOptionButton == null)
			return;

		_languageOptionButton.ItemSelected += OnLanguageItemSelected;
	}

	/// <summary>
	/// Populates the language list from <see cref="LocalizationService"/> and selects the saved locale.
	/// </summary>
	private void SyncLanguageControls()
	{
		if (_languageOptionButton == null)
			return;

		_isSyncingLanguage = true;
		try
		{
			string locale = _userDataManager?.Locale ?? UserDataManager.DefaultLocale;
			if (_localization != null)
				_localization.PopulateLanguageOptionButton(_languageOptionButton, locale);
			else
			{
				_languageOptionButton.Clear();
				_languageOptionButton.AddItem("English", 0);
				_languageOptionButton.SetItemMetadata(0, UserDataManager.DefaultLocale);
				_languageOptionButton.Selected = 0;
			}
		}
		finally
		{
			_isSyncingLanguage = false;
		}
	}

	/// <summary>
	/// English fallback for the welcome body if the catalog entry is missing or failed to load.
	/// </summary>
	private const string WelcomeBodyEnglishFallback =
		"Welcome to Cue2 0.1.0 - StripyHat.\n\n" +
		"Cue2 is open-source show control software for sequenced playback of audio and video.\n\n" +
		"Explore the docs and website below for how to get started.";

	/// <summary>
	/// Applies translated chrome strings for the welcome window (title, body, language, scale, CTA).
	/// </summary>
	private void ApplyLocalizedStrings()
	{
		// Generic scene walk first (labels/tooltips). Explicit keys below overwrite so
		// managed strings (especially multiline welcome body) are never left empty/wrong.
		LocalizeTree(this);

		if (_titleLabel != null)
			_titleLabel.Text = T("FIRST_TIME_TITLE");

		ApplyWelcomeBody();

		if (_getStartedButton != null)
			_getStartedButton.Text = T("FIRST_TIME_GET_STARTED");

		if (_docsLinkButton != null)
			_docsLinkButton.Text = T("Documentation — docs.cue2.live");
		if (_websiteLinkButton != null)
			_websiteLinkButton.Text = T("Website — cue2.live");

		string languageLabel = T("FIRST_TIME_LANGUAGE");
		string languageTooltip = T("SETTINGS_LANGUAGE_TOOLTIP");
		if (_languageLabel != null)
			_languageLabel.Text = languageLabel;
		if (_languageOptionButton != null)
			_languageOptionButton.TooltipText = languageTooltip;

		if (_uiScaleLabel != null)
			_uiScaleLabel.Text = T("FIRST_TIME_UI_SCALE");

		string scaleTip = T("Scales whole UI.\nCaution: Will apply on Enter.");
		if (_uiScaleNum != null)
			_uiScaleNum.TooltipText = scaleTip;
		if (_uiScaleSlider != null)
			_uiScaleSlider.TooltipText = T("Scales whole UI.\nCaution: Will apply on mouse release.");
		if (_uiScaleResetButton != null)
			_uiScaleResetButton.TooltipText = T("RESET_TO_DEFAULT");
	}

	/// <summary>
	/// Sets the welcome body from <c>FIRST_TIME_WELCOME_BODY</c>, with a hard English fallback.
	/// </summary>
	private void ApplyWelcomeBody()
	{
		if (_welcomeLabel == null || !GodotObject.IsInstanceValid(_welcomeLabel))
		{
			GD.PrintErr("FirstTimeStartupWindow:ApplyWelcomeBody - WelcomeLabel not found.");
			return;
		}

		const string key = "FIRST_TIME_WELCOME_BODY";
		// Stable catalog key so later LocalizeTree / locale switches keep the correct msgid.
		_welcomeLabel.SetMeta(MetaText, key);
		_welcomeLabel.SetMeta(MetaSkip, false);

		string translated = T(key);
		// TranslationServer returns the key when no message exists — treat that as missing.
		if (string.IsNullOrWhiteSpace(translated) || translated == key)
		{
			GD.PrintErr("FirstTimeStartupWindow:ApplyWelcomeBody - Catalog missing FIRST_TIME_WELCOME_BODY; using English fallback.");
			translated = WelcomeBodyEnglishFallback;
		}

		_welcomeLabel.Text = translated;
		_welcomeLabel.AutowrapMode = TextServer.AutowrapMode.WordSmart;
		_welcomeLabel.Visible = true;
	}

	/// <summary>
	/// Refreshes localized strings when the application locale changes.
	/// </summary>
	/// <param name="localeCode">New locale code (unused; strings come from TranslationServer).</param>
	private void OnLocaleChanged(string localeCode)
	{
		if (!GodotObject.IsInstanceValid(this))
			return;
		ApplyLocalizedStrings();
	}

	/// <summary>
	/// Persists and applies the selected language immediately.
	/// </summary>
	/// <param name="index">Selected option index (unused; locale read from item metadata).</param>
	private void OnLanguageItemSelected(long index)
	{
		if (_isSyncingLanguage || _languageOptionButton == null)
			return;

		string locale = _localization != null
			? _localization.GetLocaleFromOptionButton(_languageOptionButton)
			: UserDataManager.DefaultLocale;

		try
		{
			if (_localization != null)
				_localization.SetUserLocale(locale);
			else if (_userDataManager != null)
				_userDataManager.Locale = locale;

			// LocaleChanged may already have refreshed chrome; apply again as a safety net.
			ApplyLocalizedStrings();
			GD.Print($"FirstTimeStartupWindow:OnLanguageItemSelected - Locale set to {locale}");
		}
		catch (Exception ex)
		{
			GD.PrintErr($"FirstTimeStartupWindow:OnLanguageItemSelected - Failed: {ex.Message}");
			_globalSignals?.EmitSignal(nameof(GlobalSignals.Log),
				$"Failed to set language: {ex.Message}", 2);
		}
	}

	/// <summary>
	/// Wires the UI scale LineEdit / HSlider / reset button to match Cue2 Preferences.
	/// </summary>
	private void WireUiScaleControls()
	{
		_uiScaleNum = GetNode<LineEdit>("%UiScaleNum");
		_uiScaleSlider = GetNode<HSlider>("%UiScaleSlider");
		_uiScaleResetButton = GetNodeOrNull<Button>("%UiScaleResetButton");

		if (_uiScaleResetButton != null)
		{
			_uiScaleResetButton.Icon = GetThemeIcon("Refresh", "AtlasIcons");
			_uiScaleResetButton.Pressed += OnUiScaleResetPressed;
		}

		// Ensure the percentage field can receive focus (scene default may be FOCUS_NONE).
		_uiScaleNum.FocusMode = Control.FocusModeEnum.All;
		_uiScaleNum.Editable = true;

		if (_uiScaleSlider != null)
		{
			_uiScaleSlider.MinValue = UserDataManager.MinUiScalePercent;
			_uiScaleSlider.MaxValue = UserDataManager.MaxUiScalePercent;
			_uiScaleSlider.ValueChanged += OnUiScaleSliderValueChanged;
			_uiScaleSlider.DragEnded += OnUiScaleSliderDragEnded;
		}
		// Commit typed scale on Enter and when focus leaves the field.
		UiUtilities.BindLineEditCommit(_uiScaleNum, OnUiScaleTextSubmitted);
	}

	/// <summary>
	/// Pulls current user UI scale into the form without re-firing edit handlers.
	/// </summary>
	private void SyncUiScaleControls()
	{
		if (_userDataManager == null)
			return;

		_isSyncingUi = true;
		try
		{
			float uiPct = _userDataManager.UiScale * 100f;
			if (_uiScaleNum != null)
				_uiScaleNum.Text = uiPct + "%";
			_uiScaleSlider?.SetValueNoSignal(uiPct);
			UpdateUiScaleResetButton();
		}
		finally
		{
			_isSyncingUi = false;
		}
	}

	// ── UI Scale (mirrors SettingsCue2Prefs) ──────────────────────────────

	/// <summary>
	/// Live-updates the percentage field while dragging; applies on mouse release.
	/// </summary>
	/// <param name="value">Slider value in percent (25–400).</param>
	private void OnUiScaleSliderValueChanged(double value)
	{
		if (_isSyncingUi)
			return;
		if (_uiScaleNum != null)
			_uiScaleNum.Text = value + "%";
	}

	/// <summary>
	/// Commits UI scale when the slider drag ends.
	/// </summary>
	/// <param name="_">Unused; Godot drag-ended signal argument.</param>
	private void OnUiScaleSliderDragEnded(bool _)
	{
		if (_isSyncingUi || _uiScaleSlider == null)
			return;
		ApplyUiScale((float)(_uiScaleSlider.Value / 100.0));
	}

	/// <summary>
	/// Commits UI scale when Enter is pressed in the percentage field.
	/// </summary>
	/// <param name="input">Raw LineEdit text (may include %).</param>
	private void OnUiScaleTextSubmitted(string input)
	{
		if (_isSyncingUi)
			return;
		CommitUiScaleFromText(input);
	}

	/// <summary>
	/// Parses and clamps typed percent, then applies UI scale.
	/// </summary>
	/// <param name="input">Raw text from the LineEdit.</param>
	private void CommitUiScaleFromText(string input)
	{
		if (_userDataManager == null || _uiScaleNum == null)
			return;

		string cleaned = (input ?? string.Empty).Replace("%", "").Trim();
		if (!float.TryParse(cleaned, out float value))
		{
			_globalSignals?.EmitSignal(nameof(GlobalSignals.Log), "Invalid value for UI Scale entered", 1);
			_uiScaleNum.Text = _userDataManager.UiScale * 100f + "%";
			return;
		}

		value = Mathf.Clamp(value, UserDataManager.MinUiScalePercent, UserDataManager.MaxUiScalePercent);
		_uiScaleNum.Text = value + "%";
		_uiScaleSlider?.SetValueNoSignal(value);
		ApplyUiScale(value / 100f);
		if (_uiScaleNum.HasFocus())
			_uiScaleNum.ReleaseFocus();
	}

	/// <summary>
	/// Writes UI scale to user preferences (persists + emits <see cref="GlobalSignals.UiScaleChanged"/>).
	/// </summary>
	/// <param name="scaleFactor">Scale factor in the range 0.25–4.0.</param>
	private void ApplyUiScale(float scaleFactor)
	{
		if (_isSyncingUi || _userDataManager == null)
			return;

		scaleFactor = Mathf.Clamp(scaleFactor, UserDataManager.MinUiScale, UserDataManager.MaxUiScale);
		if (Mathf.IsEqualApprox(_userDataManager.UiScale, scaleFactor))
		{
			UpdateUiScaleResetButton();
			return;
		}

		_userDataManager.UiScale = scaleFactor;
		UpdateUiScaleResetButton();
	}

	/// <summary>
	/// Resets UI scale to the user default and syncs controls.
	/// </summary>
	private void OnUiScaleResetPressed()
	{
		if (_isSyncingUi || _userDataManager == null)
			return;
		if (Mathf.IsEqualApprox(_userDataManager.UiScale, UserDataManager.DefaultUiScale))
		{
			SyncUiScaleControls();
			return;
		}

		_userDataManager.UiScale = UserDataManager.DefaultUiScale;
		SyncUiScaleControls();
	}

	/// <summary>
	/// Shows the reset button only when scale is not at the system default.
	/// </summary>
	private void UpdateUiScaleResetButton()
	{
		if (_uiScaleResetButton == null || _userDataManager == null)
			return;

		bool atDefault = Mathf.IsEqualApprox(_userDataManager.UiScale, UserDataManager.DefaultUiScale);
		_uiScaleResetButton.Visible = !atDefault;
		if (!atDefault)
			_uiScaleResetButton.TooltipText = ResetDefaultTip($"{UserDataManager.DefaultUiScale * 100f:0}%");
	}

	// ── Dismiss ───────────────────────────────────────────────────────────

	/// <summary>
	/// Marks first-time startup complete and closes the window.
	/// </summary>
	private void OnGetStartedPressed()
	{
		MarkFirstTimeComplete();
		QueueFree();
	}

	/// <summary>
	/// Handles OS/window close request (title bar / chrome).
	/// </summary>
	private void OnCloseRequested()
	{
		MarkFirstTimeComplete();
		QueueFree();
	}

	/// <summary>
	/// Persists the first-time flag. Safe to call more than once.
	/// </summary>
	/// <remarks>
	/// Also invoked from <see cref="_ExitTree"/> so the SubWindowHandles exit button
	/// (which QueueFrees without emitting CloseRequested) still completes the flow.
	/// </remarks>
	private void MarkFirstTimeComplete()
	{
		try
		{
			_userDataManager?.MarkFirstTimeStartupComplete();
		}
		catch (Exception ex)
		{
			GD.PrintErr($"FirstTimeStartupWindow:MarkFirstTimeComplete - Failed to mark complete: {ex.Message}");
			_globalSignals?.EmitSignal(nameof(GlobalSignals.Log),
				$"Failed to save first-time startup preference: {ex.Message}", 2);
		}
	}

	/// <summary>
	/// Applies runtime UI scale changes to this borderless sub-window.
	/// </summary>
	/// <param name="value">User UI scale multiplier.</param>
	private void ScaleUi(float value)
	{
		try
		{
			// Keep controls in sync if scale was changed elsewhere (main Settings, undo, etc.).
			if (!_isSyncingUi)
				SyncUiScaleControls();

			float effectiveScale = value * _globalData.BaseDisplayScale;
			WrapControls = true;
			ContentScaleFactor = effectiveScale;
			ChildControlsChanged();
			GD.Print($"FirstTimeStartupWindow:ScaleUi - Applied effective UI scale: {effectiveScale}");
		}
		catch (Exception ex)
		{
			_globalSignals?.EmitSignal(nameof(GlobalSignals.Log), $"Error applying UI scale: {ex.Message}", 2);
			GetWindow().ContentScaleFactor = value;
		}
	}

	/// <inheritdoc />
	public override void _ExitTree()
	{
		// Cover dismiss via SubWindowHandles exit button (QueueFree without CloseRequested).
		MarkFirstTimeComplete();

		if (_getStartedButton != null)
			_getStartedButton.Pressed -= OnGetStartedPressed;

		CloseRequested -= OnCloseRequested;

		if (_languageOptionButton != null)
			_languageOptionButton.ItemSelected -= OnLanguageItemSelected;

		if (_uiScaleSlider != null)
		{
			_uiScaleSlider.ValueChanged -= OnUiScaleSliderValueChanged;
			_uiScaleSlider.DragEnded -= OnUiScaleSliderDragEnded;
		}
		// UI scale LineEdit is wired with BindLineEditCommit; hooks die with the node.
		if (_uiScaleResetButton != null)
			_uiScaleResetButton.Pressed -= OnUiScaleResetPressed;

		if (_globalSignals != null)
		{
			_globalSignals.UiScaleChanged -= ScaleUi;
			_globalSignals.LocaleChanged -= OnLocaleChanged;
		}
	}
}
