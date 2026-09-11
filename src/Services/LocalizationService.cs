// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Text;
using Godot;

namespace Cue2.Services;

/// <summary>
/// Application localization framework built on Godot's <see cref="TranslationServer"/>.
/// </summary>
/// <remarks>
/// Loads message catalogs from <c>res://translations/cue2.csv</c> in the editor. Exported
/// builds remap that CSV to compiled <c>cue2.*.translation</c> files, so those are loaded
/// when the source spreadsheet is not readable. Each locale is registered with
/// <see cref="TranslationServer"/> and the user's preference comes from
/// <see cref="UserDataManager"/>. Supported locales are listed in
/// <see cref="SupportedLocales"/> (extend CSV columns + this list to add languages).
/// <para/>
/// Automation: <c>python tools/i18n/update_catalog.py</c> extracts new English keys
/// and fills every locale column (mi/es/de/ru/ja/ar/hi). Use
/// <c>extract_ui_strings.py --report-unwrapped</c> to list raw UI assignments.
/// UI code uses English source strings as keys via <see cref="Cue2.UI.Utilities.UiLocalizer"/>.
/// </remarks>
public partial class LocalizationService : Node
{
	/// <summary>Path to the spreadsheet-style translation catalog.</summary>
	public const string CatalogPath = "res://translations/cue2.csv";

	/// <summary>Default and fallback locale code (English).</summary>
	public const string DefaultLocale = "en";

	/// <summary>Te reo Māori locale code (ISO 639-1).</summary>
	public const string LocaleMaori = "mi";

	/// <summary>Spanish locale code (ISO 639-1).</summary>
	public const string LocaleSpanish = "es";

	/// <summary>German locale code (ISO 639-1). Layout-stress: long compounds.</summary>
	public const string LocaleGerman = "de";

	/// <summary>Russian locale code (ISO 639-1). Cyrillic.</summary>
	public const string LocaleRussian = "ru";

	/// <summary>Japanese locale code (ISO 639-1). CJK mixed scripts, no word spaces.</summary>
	public const string LocaleJapanese = "ja";

	/// <summary>Arabic locale code (ISO 639-1). RTL + joining letters.</summary>
	public const string LocaleArabic = "ar";

	/// <summary>Hindi locale code (ISO 639-1). Devanagari combining marks.</summary>
	public const string LocaleHindi = "hi";

	/// <summary>
	/// Supported UI locales: ISO-style code, native name, and English name.
	/// The picker shows <c>Native (English)</c> so scripts stay visible and identifiable.
	/// </summary>
	/// <remarks>
	/// Cherry-picked for script / layout testing rather than market coverage:
	/// English (default), te reo Māori (macrons), Spanish (Latin accents),
	/// German (long compounds), Russian (Cyrillic), Japanese (CJK),
	/// Arabic (RTL), Hindi (Devanagari).
	/// </remarks>
	public static readonly IReadOnlyList<(string Code, string DisplayName, string EnglishName)> SupportedLocales =
		new List<(string, string, string)>
		{
			(DefaultLocale, "English", "English"),
			(LocaleMaori, "Te reo Māori", "Māori"),
			(LocaleSpanish, "Español", "Spanish"),
			(LocaleGerman, "Deutsch", "German"),
			(LocaleRussian, "Русский", "Russian"),
			(LocaleJapanese, "日本語", "Japanese"),
			(LocaleArabic, "العربية", "Arabic"),
			(LocaleHindi, "हिन्दी", "Hindi"),
		};

	private GlobalData _globalData;
	private GlobalSignals _globalSignals;
	private bool _initialized;
	private readonly HashSet<string> _registeredLocales = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

	/// <summary>
	/// Loads the CSV catalog, registers translations, and applies the saved user locale.
	/// Safe to call once after <see cref="UserDataManager"/> has loaded preferences.
	/// </summary>
	public void Initialize()
	{
		if (_initialized)
			return;

		_globalData = GetNodeOrNull<GlobalData>("/root/GlobalData");
		_globalSignals = GetNodeOrNull<GlobalSignals>("/root/GlobalSignals");

		try
		{
			// Fallback locale is configured in project.godot [internationalization] locale/fallback.
			LoadAndRegisterCatalog();

			// Ensure English is always registered even if the CSV is missing or incomplete.
			EnsureLocaleRegistered(DefaultLocale);

			string preferred = _globalData?.UserDataManager?.Locale ?? DefaultLocale;
			ApplyLocale(preferred, emitSignal: false);
			// Autoload _Ready runs before the main scene exists; apply layout once the tree is up.
			CallDeferred(MethodName.ApplyUiLayoutDirection);

			_initialized = true;
			GD.Print($"LocalizationService:Initialize - Ready. Locale={TranslationServer.GetLocale()} " +
			         $"registered=[{string.Join(", ", _registeredLocales)}]");
		}
		catch (Exception ex)
		{
			GD.PrintErr($"LocalizationService:Initialize - Failed: {ex.Message}");
			_globalSignals?.EmitSignal(nameof(GlobalSignals.Log),
				$"Localization init failed: {ex.Message}", 2);
			try
			{
				EnsureLocaleRegistered(DefaultLocale);
				TranslationServer.SetLocale(DefaultLocale);
			}
			catch
			{
				// Best-effort fallback only.
			}
			_initialized = true;
		}
	}

	/// <summary>
	/// Whether <paramref name="localeCode"/> is in the supported locale list.
	/// </summary>
	/// <param name="localeCode">ISO-style locale code (e.g. <c>en</c>).</param>
	/// <returns>True if the locale is supported for the UI language picker.</returns>
	public bool IsSupported(string localeCode)
	{
		if (string.IsNullOrWhiteSpace(localeCode))
			return false;

		foreach (var (code, _, _) in SupportedLocales)
		{
			if (string.Equals(code, localeCode, StringComparison.OrdinalIgnoreCase))
				return true;
		}
		return false;
	}

	/// <summary>
	/// Resolves a locale code to a supported value, falling back to English.
	/// </summary>
	/// <param name="localeCode">Requested locale code.</param>
	/// <returns>A supported locale code.</returns>
	public string ResolveLocale(string localeCode)
	{
		if (IsSupported(localeCode))
			return localeCode.Trim().ToLowerInvariant();
		return DefaultLocale;
	}

	/// <summary>
	/// Native display name for a locale code (e.g. <c>en</c> → <c>English</c>).
	/// </summary>
	/// <param name="localeCode">ISO-style locale code.</param>
	/// <returns>Display name, or the code itself if unknown.</returns>
	public string GetDisplayName(string localeCode)
	{
		if (string.IsNullOrWhiteSpace(localeCode))
			return DefaultLocale;

		foreach (var (code, name, _) in SupportedLocales)
		{
			if (string.Equals(code, localeCode, StringComparison.OrdinalIgnoreCase))
				return name;
		}
		return localeCode;
	}

	/// <summary>
	/// Picker caption: native name, plus English in parentheses when they differ.
	/// </summary>
	/// <param name="nativeName">Name in the language itself.</param>
	/// <param name="englishName">English language name.</param>
	/// <returns>e.g. <c>日本語 (Japanese)</c>, or <c>English</c> when both match.</returns>
	public static string FormatPickerLabel(string nativeName, string englishName)
	{
		if (string.IsNullOrWhiteSpace(nativeName))
			return englishName ?? string.Empty;
		if (string.IsNullOrWhiteSpace(englishName) ||
		    string.Equals(nativeName, englishName, StringComparison.OrdinalIgnoreCase))
			return nativeName;
		return $"{nativeName} ({englishName})";
	}

	/// <summary>
	/// Applies a locale to <see cref="TranslationServer"/> and optionally notifies listeners.
	/// Does not persist the preference; use <see cref="SetUserLocale"/> for that.
	/// </summary>
	/// <param name="localeCode">Requested locale code.</param>
	/// <param name="emitSignal">When true, emits <see cref="GlobalSignals.LocaleChanged"/>.</param>
	/// <returns>The resolved locale that was applied.</returns>
	public string ApplyLocale(string localeCode, bool emitSignal = true)
	{
		string resolved = ResolveLocale(localeCode);
		EnsureLocaleRegistered(resolved);

		string previous = TranslationServer.GetLocale();
		TranslationServer.SetLocale(resolved);
		ApplyUiLayoutDirection();

		GD.Print($"LocalizationService:ApplyLocale - {previous} → {resolved}");

		if (emitSignal && !string.Equals(previous, resolved, StringComparison.OrdinalIgnoreCase))
			_globalSignals?.EmitSignal(nameof(GlobalSignals.LocaleChanged), resolved);

		return resolved;
	}

	/// <summary>
	/// Persists the locale preference and applies it to the translation server.
	/// </summary>
	/// <param name="localeCode">Requested locale code.</param>
	/// <returns>The resolved locale that was stored and applied.</returns>
	public string SetUserLocale(string localeCode)
	{
		string resolved = ResolveLocale(localeCode);

		if (_globalData?.UserDataManager != null)
		{
			// Property setter saves when the value changes.
			_globalData.UserDataManager.Locale = resolved;
		}

		// Always apply so TranslationServer stays in sync even if the value was already saved.
		string previous = TranslationServer.GetLocale();
		TranslationServer.SetLocale(resolved);
		EnsureLocaleRegistered(resolved);
		ApplyUiLayoutDirection();

		if (!string.Equals(previous, resolved, StringComparison.OrdinalIgnoreCase))
		{
			GD.Print($"LocalizationService:SetUserLocale - {previous} → {resolved}");
			_globalSignals?.EmitSignal(nameof(GlobalSignals.LocaleChanged), resolved);
		}
		else
		{
			GD.Print($"LocalizationService:SetUserLocale - Locale remains {resolved}");
		}

		return resolved;
	}

	/// <summary>
	/// Fills an <see cref="OptionButton"/> with supported languages and selects the given locale.
	/// Item metadata stores the locale code string.
	/// </summary>
	/// <param name="button">Target option button (cleared and rebuilt).</param>
	/// <param name="selectedLocale">Locale code to select; falls back to English if unsupported.</param>
	public void PopulateLanguageOptionButton(OptionButton button, string selectedLocale)
	{
		if (button == null || !GodotObject.IsInstanceValid(button))
			return;

		string resolved = ResolveLocale(selectedLocale);
		button.Clear();

		int selectedIndex = 0;
		for (int i = 0; i < SupportedLocales.Count; i++)
		{
			var (code, displayName, englishName) = SupportedLocales[i];
			button.AddItem(FormatPickerLabel(displayName, englishName), i);
			button.SetItemMetadata(i, code);
			if (string.Equals(code, resolved, StringComparison.OrdinalIgnoreCase))
				selectedIndex = i;
		}

		button.Selected = selectedIndex;
	}

	/// <summary>
	/// Reads the locale code stored as metadata on the selected option-button item.
	/// </summary>
	/// <param name="button">Language option button previously filled by <see cref="PopulateLanguageOptionButton"/>.</param>
	/// <returns>Locale code, or <see cref="DefaultLocale"/> if unavailable.</returns>
	public string GetLocaleFromOptionButton(OptionButton button)
	{
		if (button == null || !GodotObject.IsInstanceValid(button) || button.ItemCount == 0)
			return DefaultLocale;

		int index = button.Selected;
		if (index < 0 || index >= button.ItemCount)
			return DefaultLocale;

		Variant meta = button.GetItemMetadata(index);
		string code = meta.AsString();
		return ResolveLocale(code);
	}

	/// <summary>
	/// True when <paramref name="localeCode"/> is a right-to-left UI locale (currently Arabic).
	/// </summary>
	/// <param name="localeCode">ISO-style locale code.</param>
	/// <returns>True for RTL locales.</returns>
	public bool IsRtlLocale(string localeCode)
	{
		string resolved = ResolveLocale(localeCode);
		return string.Equals(resolved, LocaleArabic, StringComparison.OrdinalIgnoreCase);
	}

	/// <summary>
	/// Sets <see cref="Control.LayoutDirection"/> to follow the application locale
	/// on each window's first control so Arabic flips chrome without per-widget wiring.
	/// Children inherit unless they override.
	/// </summary>
	public void ApplyUiLayoutDirection()
	{
		SceneTree tree = GetTree();
		if (tree?.Root == null)
			return;

		ApplyLayoutToTree(tree.Root);
	}

	/// <summary>
	/// Walks a node tree and assigns <see cref="Control.LayoutDirectionEnum.ApplicationLocale"/>
	/// on the first control under each viewport/window (children inherit).
	/// </summary>
	/// <param name="node">Root to walk.</param>
	private static void ApplyLayoutToTree(Node node)
	{
		if (node == null || !GodotObject.IsInstanceValid(node))
			return;

		if (node is Control control)
		{
			control.LayoutDirection = Control.LayoutDirectionEnum.ApplicationLocale;
			return;
		}

		foreach (Node child in node.GetChildren())
			ApplyLayoutToTree(child);
	}

	/// <summary>
	/// Translates a message key using the current locale (wrapper for <see cref="TranslationServer.Translate"/>).
	/// </summary>
	/// <param name="messageKey">Catalog key (e.g. <c>SETTINGS_LANGUAGE</c>).</param>
	/// <returns>Translated string, or the key if no message exists.</returns>
	public string Translate(string messageKey)
	{
		if (string.IsNullOrEmpty(messageKey))
			return string.Empty;
		return TranslationServer.Translate(messageKey);
	}

	// ── Catalog loading ───────────────────────────────────────────────────

	/// <summary>
	/// Path pattern for Godot-compiled catalogs produced from <see cref="CatalogPath"/>.
	/// </summary>
	/// <param name="localeCode">ISO-style locale code (e.g. <c>es</c>).</param>
	/// <returns>Resource path of the compiled <see cref="Translation"/>.</returns>
	public static string GetCompiledCatalogPath(string localeCode) =>
		$"res://translations/cue2.{localeCode}.translation";

	/// <summary>
	/// Parses the CSV catalog when it is a real file (editor), otherwise loads the
	/// compiled <c>.translation</c> resources that Godot packs into exported builds.
	/// </summary>
	private void LoadAndRegisterCatalog()
	{
		// Project.godot auto-loads compiled cue2.*.translation at engine start.
		// Those files stay stale until Godot reimports the CSV, so they would win
		// over a newer spreadsheet (new keys stay English). Own the catalog instead.
		TranslationServer.Clear();
		_registeredLocales.Clear();

		if (TryLoadCatalogFromCsv())
			return;

		if (TryLoadCatalogFromCompiledTranslations())
			return;

		GD.PrintErr($"LocalizationService:LoadAndRegisterCatalog - No catalog available " +
		            $"(CSV missing in export is expected; compiled .translation files also missing).");
		_globalSignals?.EmitSignal(nameof(GlobalSignals.Log),
			$"Translation catalog missing: {CatalogPath}", 1);
	}

	/// <summary>
	/// Loads and registers translations from the source CSV when FileAccess can read it.
	/// </summary>
	/// <returns>True when at least one locale catalog was registered from the CSV.</returns>
	private bool TryLoadCatalogFromCsv()
	{
		if (!Godot.FileAccess.FileExists(CatalogPath))
			return false;

		using var file = Godot.FileAccess.Open(CatalogPath, Godot.FileAccess.ModeFlags.Read);
		if (file == null)
		{
			Error err = Godot.FileAccess.GetOpenError();
			GD.Print($"LocalizationService:TryLoadCatalogFromCsv - Cannot open {CatalogPath}: {err}");
			return false;
		}

		string text = file.GetAsText();
		if (string.IsNullOrWhiteSpace(text))
		{
			GD.Print("LocalizationService:TryLoadCatalogFromCsv - Catalog is empty.");
			return false;
		}

		// Strip UTF-8 BOM if present. Keep real newlines — records may be multiline (quoted fields).
		if (text.Length > 0 && text[0] == '\uFEFF')
			text = text.Substring(1);

		List<List<string>> records = ParseCsvRecords(text);
		if (records.Count == 0)
			return false;

		List<string> header = records[0];
		string headerKey = header.Count > 0 ? (header[0] ?? string.Empty).Trim() : string.Empty;
		if (header.Count < 2 || !string.Equals(headerKey, "keys", StringComparison.OrdinalIgnoreCase))
		{
			GD.Print("LocalizationService:TryLoadCatalogFromCsv - Not a translation spreadsheet " +
			         "(export remaps this path); falling back to compiled catalogs.");
			return false;
		}

		// Column 0 is keys; remaining columns are locale codes (skip comment columns starting with _).
		var localeColumns = new List<(int Index, string Locale)>();
		for (int c = 1; c < header.Count; c++)
		{
			string col = (header[c] ?? string.Empty).Trim();
			if (string.IsNullOrEmpty(col) || col.StartsWith('_') || col.StartsWith('?'))
				continue;
			localeColumns.Add((c, col.ToLowerInvariant()));
		}

		if (localeColumns.Count == 0)
		{
			GD.PrintErr("LocalizationService:TryLoadCatalogFromCsv - No locale columns found.");
			return false;
		}

		var translations = new Dictionary<string, Translation>(StringComparer.OrdinalIgnoreCase);
		foreach (var (_, locale) in localeColumns)
		{
			if (!translations.ContainsKey(locale))
			{
				var t = new Translation { Locale = locale };
				translations[locale] = t;
			}
		}

		int messageCount = 0;
		for (int recordIndex = 1; recordIndex < records.Count; recordIndex++)
		{
			List<string> fields = records[recordIndex];
			if (fields.Count == 0)
				continue;

			string key = fields[0]?.Trim() ?? string.Empty;
			if (string.IsNullOrEmpty(key) || key.StartsWith('#'))
				continue;

			foreach (var (colIndex, locale) in localeColumns)
			{
				if (colIndex >= fields.Count)
					continue;
				string message = fields[colIndex] ?? string.Empty;
				if (string.IsNullOrEmpty(message))
					continue;

				translations[locale].AddMessage(key, message);
				messageCount++;
			}
		}

		if (translations.Count == 0 || messageCount == 0)
			return false;

		foreach (var kvp in translations)
		{
			// Replace any previous registration for this locale from earlier init attempts.
			TranslationServer.AddTranslation(kvp.Value);
			_registeredLocales.Add(kvp.Key);
		}

		GD.Print($"LocalizationService:TryLoadCatalogFromCsv - Registered {translations.Count} locale(s), " +
		         $"{messageCount} message cell(s) from {records.Count - 1} row(s).");
		return true;
	}

	/// <summary>
	/// Loads Godot-imported <c>cue2.&lt;locale&gt;.translation</c> resources.
	/// These are what exported PCK files actually contain (the CSV source is remapped away).
	/// </summary>
	/// <returns>True when at least one compiled catalog was registered.</returns>
	private bool TryLoadCatalogFromCompiledTranslations()
	{
		int loaded = 0;
		foreach (var (code, _, _) in SupportedLocales)
		{
			string path = GetCompiledCatalogPath(code);
			if (!ResourceLoader.Exists(path))
			{
				GD.Print($"LocalizationService:TryLoadCatalogFromCompiledTranslations - Missing {path}");
				continue;
			}

			var translation = ResourceLoader.Load<Translation>(path);
			if (translation == null)
			{
				GD.PrintErr($"LocalizationService:TryLoadCatalogFromCompiledTranslations - Failed to load {path}");
				continue;
			}

			if (string.IsNullOrEmpty(translation.Locale))
				translation.Locale = code;

			TranslationServer.AddTranslation(translation);
			_registeredLocales.Add(code);
			loaded++;
		}

		if (loaded == 0)
			return false;

		GD.Print($"LocalizationService:TryLoadCatalogFromCompiledTranslations - Registered {loaded} compiled locale(s).");
		return true;
	}

	/// <summary>
	/// Registers an empty (or display-name-only) translation for a locale if none was loaded from CSV.
	/// </summary>
	/// <param name="localeCode">Locale to ensure.</param>
	private void EnsureLocaleRegistered(string localeCode)
	{
		string locale = ResolveLocale(localeCode);
		if (_registeredLocales.Contains(locale))
			return;

		string compiledPath = GetCompiledCatalogPath(locale);
		if (ResourceLoader.Exists(compiledPath))
		{
			var compiled = ResourceLoader.Load<Translation>(compiledPath);
			if (compiled != null)
			{
				if (string.IsNullOrEmpty(compiled.Locale))
					compiled.Locale = locale;
				TranslationServer.AddTranslation(compiled);
				_registeredLocales.Add(locale);
				GD.Print($"LocalizationService:EnsureLocaleRegistered - Loaded compiled catalog for '{locale}'.");
				return;
			}
		}

		var translation = new Translation { Locale = locale };
		// Seed the language display name so Tr("LOCALE_NAME_en") works even without CSV.
		translation.AddMessage($"LOCALE_NAME_{locale}", GetDisplayName(locale));
		TranslationServer.AddTranslation(translation);
		_registeredLocales.Add(locale);
		GD.Print($"LocalizationService:EnsureLocaleRegistered - Registered fallback catalog for '{locale}'.");
	}

	/// <summary>
	/// Parses a full CSV document into records, supporting multiline quoted fields and <c>""</c> escapes.
	/// </summary>
	/// <param name="text">Entire CSV file contents.</param>
	/// <returns>List of field lists (never null).</returns>
	/// <remarks>
	/// Line-splitting first is incorrect for Godot-style translation spreadsheets that embed
	/// newlines inside quoted cells (e.g. welcome body). This parser walks the full text so
	/// those records stay intact.
	/// </remarks>
	private static List<List<string>> ParseCsvRecords(string text)
	{
		var records = new List<List<string>>();
		if (string.IsNullOrEmpty(text))
			return records;

		var fields = new List<string>();
		var current = new StringBuilder();
		bool inQuotes = false;

		void EndField()
		{
			fields.Add(current.ToString());
			current.Clear();
		}

		void EndRecord()
		{
			EndField();
			// Skip completely empty rows (e.g. trailing newline).
			bool any = false;
			foreach (string f in fields)
			{
				if (!string.IsNullOrWhiteSpace(f))
				{
					any = true;
					break;
				}
			}
			if (any)
				records.Add(fields);
			fields = new List<string>();
		}

		for (int i = 0; i < text.Length; i++)
		{
			char ch = text[i];
			if (inQuotes)
			{
				if (ch == '"')
				{
					// Escaped quote ""
					if (i + 1 < text.Length && text[i + 1] == '"')
					{
						current.Append('"');
						i++;
					}
					else
					{
						inQuotes = false;
					}
				}
				else
				{
					// Preserve newlines inside quoted fields.
					current.Append(ch);
				}
			}
			else
			{
				if (ch == '"')
				{
					inQuotes = true;
				}
				else if (ch == ',')
				{
					EndField();
				}
				else if (ch == '\n')
				{
					EndRecord();
				}
				else if (ch == '\r')
				{
					// Normalize CRLF / bare CR as record separators outside quotes.
					if (i + 1 < text.Length && text[i + 1] == '\n')
						i++;
					EndRecord();
				}
				else
				{
					current.Append(ch);
				}
			}
		}

		// Final field/record when file does not end with a newline.
		if (current.Length > 0 || fields.Count > 0)
			EndRecord();

		return records;
	}
}
