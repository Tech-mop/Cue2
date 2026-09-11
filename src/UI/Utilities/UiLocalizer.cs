// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using Godot;

namespace Cue2.UI.Utilities;

/// <summary>
/// Applies Godot <see cref="TranslationServer"/> messages to control trees.
/// </summary>
/// <remarks>
/// English source strings are the catalog keys (stored in node metadata on first pass)
/// so locale switches never lose the original msgid. Dynamic user content (LineEdit text,
/// session titles, etc.) is not overwritten — only labels, button captions, tooltips,
/// placeholders, and window titles are localized.
/// <para/>
/// Automation: extract scene strings and C# <c>T()</c>/<c>Tf()</c> literals into
/// <c>translations/cue2.csv</c> (see <c>tools/i18n/extract_ui_strings.py</c>),
/// translate columns, then call <see cref="LocalizeTree"/> from UI <c>_Ready</c>
/// and on <c>LocaleChanged</c>. Keep English source text visible in code.
/// </remarks>
public static class UiLocalizer
{
	/// <summary>Metadata key for original (English) control text.</summary>
	public const string MetaText = "tr_src_text";

	/// <summary>Metadata key for original tooltip text.</summary>
	public const string MetaTooltip = "tr_src_tooltip";

	/// <summary>Metadata key for original placeholder text.</summary>
	public const string MetaPlaceholder = "tr_src_placeholder";

	/// <summary>Metadata key for original window title.</summary>
	public const string MetaTitle = "tr_src_title";

	/// <summary>
	/// When present and true, this node (not necessarily children) is skipped.
	/// </summary>
	public const string MetaSkip = "tr_skip";

	/// <summary>
	/// Stable English menu identity for Settings tree items (display text may be translated).
	/// </summary>
	public const string MetaMenuKey = "menu_key";

	/// <summary>
	/// Parallel English captions for <see cref="OptionButton"/> items (item metadata stays free for ids).
	/// </summary>
	public const string MetaOptionKeys = "tr_src_option_keys";

	/// <summary>
	/// Parallel English tooltips for <see cref="OptionButton"/> items.
	/// </summary>
	public const string MetaOptionTooltips = "tr_src_option_tooltips";

	/// <summary>
	/// Looks up a message key in the current locale (falls back to the key / English catalog).
	/// </summary>
	/// <param name="messageKey">Catalog key (usually the English source string).</param>
	/// <returns>Translated string, or the key when missing.</returns>
	public static string T(string messageKey)
	{
		if (string.IsNullOrEmpty(messageKey))
			return messageKey ?? string.Empty;
		return TranslationServer.Translate(messageKey);
	}

	/// <summary>
	/// Formats a translated template with <see cref="string.Format(string, object[])"/>.
	/// </summary>
	/// <param name="messageKey">Catalog key containing <c>{0}</c>-style placeholders.</param>
	/// <param name="args">Format arguments.</param>
	/// <returns>Formatted translated string.</returns>
	public static string Tf(string messageKey, params object[] args)
	{
		string fmt = T(messageKey);
		if (args == null || args.Length == 0)
			return fmt;
		try
		{
			return string.Format(fmt, args);
		}
		catch (FormatException)
		{
			return fmt;
		}
	}

	/// <summary>
	/// Recursively localizes a node and its descendants.
	/// </summary>
	/// <param name="root">Root node (scene root or panel).</param>
	/// <param name="includeRoot">When true, also localize <paramref name="root"/> itself.</param>
	public static void LocalizeTree(Node root, bool includeRoot = true)
	{
		if (root == null || !GodotObject.IsInstanceValid(root))
			return;

		if (includeRoot)
			LocalizeNode(root);

		foreach (Node child in root.GetChildren())
			LocalizeTree(child, includeRoot: true);
	}

	/// <summary>
	/// Localizes a single node when it has user-facing static text properties.
	/// </summary>
	/// <param name="node">Node to localize.</param>
	public static void LocalizeNode(Node node)
	{
		if (node == null || !GodotObject.IsInstanceValid(node))
			return;

		if (node.HasMeta(MetaSkip) && node.GetMeta(MetaSkip).AsBool())
			return;

		// More-derived Button types first (CheckBox/OptionButton inherit Button in Godot).
		switch (node)
		{
			case Window window:
				LocalizeWindowTitle(window);
				break;
			case Label label:
				LocalizeTextControl(label, () => label.Text, v => label.Text = v);
				LocalizeTooltip(label);
				break;
			case LinkButton linkButton:
				LocalizeTextControl(linkButton, () => linkButton.Text, v => linkButton.Text = v);
				LocalizeTooltip(linkButton);
				break;
			case OptionButton optionButton:
				LocalizeTooltip(optionButton);
				RelocalizeOptionButton(optionButton);
				break;
			case CheckBox checkBox:
				LocalizeTextControl(checkBox, () => checkBox.Text, v => checkBox.Text = v);
				LocalizeTooltip(checkBox);
				break;
			case CheckButton checkButton:
				LocalizeTextControl(checkButton, () => checkButton.Text, v => checkButton.Text = v);
				LocalizeTooltip(checkButton);
				break;
			case Button button:
				// Empty Text is common for icon-only buttons; still localize tooltip.
				if (!string.IsNullOrEmpty(button.Text) || button.HasMeta(MetaText))
					LocalizeTextControl(button, () => button.Text, v => button.Text = v);
				LocalizeTooltip(button);
				break;
			case LineEdit lineEdit:
				// Do not overwrite user-entered Text — only placeholder + tooltip.
				LocalizePlaceholder(lineEdit);
				LocalizeTooltip(lineEdit);
				break;
			case TextEdit textEdit:
				LocalizePlaceholder(textEdit);
				LocalizeTooltip(textEdit);
				break;
			case SpinBox spinBox:
				LocalizeTooltip(spinBox);
				break;
			case ProgressBar progressBar:
				LocalizeTooltip(progressBar);
				break;
			case Godot.Range range:
				// Slider / other Range controls — tooltips only.
				LocalizeTooltip(range);
				break;
			case Control control:
				// Generic controls may only have tooltips (e.g. resize grips).
				LocalizeTooltip(control);
				break;
		}
	}

	/// <summary>
	/// Sets a Settings tree item's display text from a stable English menu key.
	/// </summary>
	/// <param name="item">Tree item.</param>
	/// <param name="column">Column index.</param>
	/// <param name="englishKey">Stable English label used for navigation / persistence.</param>
	public static void SetTreeItemText(TreeItem item, int column, string englishKey)
	{
		if (item == null || string.IsNullOrEmpty(englishKey))
			return;
		item.SetMeta(MetaMenuKey, englishKey);
		item.SetText(column, T(englishKey));
	}

	/// <summary>
	/// Sets a Settings tree item tooltip from an English source string.
	/// </summary>
	/// <param name="item">Tree item.</param>
	/// <param name="column">Column index.</param>
	/// <param name="englishTooltip">English tooltip (catalog key).</param>
	public static void SetTreeItemTooltip(TreeItem item, int column, string englishTooltip)
	{
		if (item == null || string.IsNullOrEmpty(englishTooltip))
			return;
		item.SetMeta(MetaTooltip + column, englishTooltip);
		item.SetTooltipText(column, T(englishTooltip));
	}

	/// <summary>
	/// Returns the stable English menu key for a tree item (falls back to column-0 text).
	/// </summary>
	/// <param name="item">Tree item.</param>
	/// <returns>English menu key.</returns>
	public static string GetTreeItemMenuKey(TreeItem item)
	{
		if (item == null)
			return string.Empty;
		if (item.HasMeta(MetaMenuKey))
			return item.GetMeta(MetaMenuKey).AsString();
		return item.GetText(0) ?? string.Empty;
	}

	/// <summary>
	/// Formats the standard "Reset to default: {value}" tooltip.
	/// </summary>
	/// <param name="defaultValue">Already-formatted default shown after the colon.</param>
	/// <returns>Translated tooltip.</returns>
	public static string ResetDefaultTip(object defaultValue)
	{
		string value = defaultValue?.ToString() ?? string.Empty;
		return Tf("Reset to default: {0}", value);
	}

	/// <summary>
	/// Translates <paramref name="englishTip"/> and appends a hotkey line when one is bound.
	/// </summary>
	/// <param name="englishTip">English tooltip body (catalog key).</param>
	/// <param name="hotkey">Display string from <c>GlobalData.ParseHotkey</c>; omitted when empty.</param>
	/// <returns>Translated tooltip, optionally with a hotkey suffix.</returns>
	public static string WithHotkey(string englishTip, string hotkey)
	{
		string body = T(englishTip);
		if (string.IsNullOrEmpty(hotkey))
			return body;
		return body + "\n" + Tf("Hotkey: {0}", hotkey);
	}

	/// <summary>
	/// Sets a control tooltip from an English source key and stores that key for later locale switches.
	/// </summary>
	/// <param name="control">Target control.</param>
	/// <param name="englishKey">English tooltip (catalog key). Empty clears the tooltip.</param>
	public static void SetTooltip(Control control, string englishKey)
	{
		if (control == null || !GodotObject.IsInstanceValid(control))
			return;

		if (string.IsNullOrEmpty(englishKey))
		{
			control.TooltipText = string.Empty;
			if (control.HasMeta(MetaTooltip))
				control.RemoveMeta(MetaTooltip);
			return;
		}

		control.SetMeta(MetaTooltip, englishKey);
		control.TooltipText = T(englishKey);
	}

	/// <summary>
	/// Sets label text from an English source key and stores that key for later locale switches.
	/// </summary>
	/// <param name="label">Target label.</param>
	/// <param name="englishKey">English caption (catalog key).</param>
	public static void SetText(Label label, string englishKey)
	{
		if (label == null || !GodotObject.IsInstanceValid(label))
			return;
		if (string.IsNullOrEmpty(englishKey))
		{
			label.Text = string.Empty;
			return;
		}

		label.SetMeta(MetaText, englishKey);
		label.Text = T(englishKey);
	}

	/// <summary>
	/// Sets button caption from an English source key and stores that key for later locale switches.
	/// </summary>
	/// <param name="button">Target button.</param>
	/// <param name="englishKey">English caption (catalog key).</param>
	public static void SetText(Button button, string englishKey)
	{
		if (button == null || !GodotObject.IsInstanceValid(button))
			return;
		if (string.IsNullOrEmpty(englishKey))
		{
			button.Text = string.Empty;
			return;
		}

		button.SetMeta(MetaText, englishKey);
		button.Text = T(englishKey);
	}

	/// <summary>
	/// Sets a LineEdit placeholder from an English source key.
	/// </summary>
	/// <param name="edit">Target field.</param>
	/// <param name="englishKey">English placeholder (catalog key).</param>
	public static void SetPlaceholder(LineEdit edit, string englishKey)
	{
		if (edit == null || !GodotObject.IsInstanceValid(edit))
			return;
		if (string.IsNullOrEmpty(englishKey))
		{
			edit.PlaceholderText = string.Empty;
			if (edit.HasMeta(MetaPlaceholder))
				edit.RemoveMeta(MetaPlaceholder);
			return;
		}

		edit.SetMeta(MetaPlaceholder, englishKey);
		edit.PlaceholderText = T(englishKey);
	}

	/// <summary>
	/// Sets a TextEdit placeholder from an English source key.
	/// </summary>
	/// <param name="edit">Target field.</param>
	/// <param name="englishKey">English placeholder (catalog key).</param>
	public static void SetPlaceholder(TextEdit edit, string englishKey)
	{
		if (edit == null || !GodotObject.IsInstanceValid(edit))
			return;
		if (string.IsNullOrEmpty(englishKey))
		{
			edit.PlaceholderText = string.Empty;
			if (edit.HasMeta(MetaPlaceholder))
				edit.RemoveMeta(MetaPlaceholder);
			return;
		}

		edit.SetMeta(MetaPlaceholder, englishKey);
		edit.PlaceholderText = T(englishKey);
	}

	/// <summary>
	/// Adds an OptionButton item whose caption (and optional tooltip) are translated
	/// from English source keys stored on the button — item metadata stays free for ids.
	/// </summary>
	/// <param name="button">Target option button.</param>
	/// <param name="englishKey">English item caption (catalog key).</param>
	/// <param name="id">Optional item id passed to <see cref="OptionButton.AddItem(string, int)"/>.</param>
	/// <param name="englishTooltip">Optional English item tooltip (catalog key).</param>
	public static void AddTranslatedItem(OptionButton button, string englishKey, int id = -1, string englishTooltip = null)
	{
		if (button == null || !GodotObject.IsInstanceValid(button) || string.IsNullOrEmpty(englishKey))
			return;

		int index = button.ItemCount;
		if (index == 0)
		{
			if (button.HasMeta(MetaOptionKeys))
				button.RemoveMeta(MetaOptionKeys);
			if (button.HasMeta(MetaOptionTooltips))
				button.RemoveMeta(MetaOptionTooltips);
		}

		if (id >= 0)
			button.AddItem(T(englishKey), id);
		else
			button.AddItem(T(englishKey));

		StoreIndexedMeta(button, MetaOptionKeys, index, englishKey);
		StoreIndexedMeta(button, MetaOptionTooltips, index, englishTooltip ?? string.Empty);
		if (!string.IsNullOrEmpty(englishTooltip))
			button.SetItemTooltip(index, T(englishTooltip));
	}

	/// <summary>
	/// Re-translates OptionButton items that were added with <see cref="AddTranslatedItem"/>.
	/// Items without stored English keys (dynamic user data) are left unchanged.
	/// </summary>
	/// <param name="button">Target option button.</param>
	public static void RelocalizeOptionButton(OptionButton button)
	{
		if (button == null || !GodotObject.IsInstanceValid(button))
			return;

		List<string> keys = ReadIndexedMeta(button, MetaOptionKeys);
		List<string> tips = ReadIndexedMeta(button, MetaOptionTooltips);
		int count = button.ItemCount;
		for (int i = 0; i < count; i++)
		{
			if (i < keys.Count && !string.IsNullOrEmpty(keys[i]))
				button.SetItemText(i, T(keys[i]));
			if (i < tips.Count && !string.IsNullOrEmpty(tips[i]))
				button.SetItemTooltip(i, T(tips[i]));
		}
	}

	/// <summary>
	/// Re-applies translations to a tree item that was configured with <see cref="SetTreeItemText"/>.
	/// </summary>
	/// <param name="item">Root item to walk (depth-first).</param>
	/// <param name="column">Column index.</param>
	public static void RelocalizeTreeItems(TreeItem item, int column = 0)
	{
		if (item == null)
			return;

		if (item.HasMeta(MetaMenuKey))
		{
			string key = item.GetMeta(MetaMenuKey).AsString();
			item.SetText(column, T(key));
		}

		string tipMeta = MetaTooltip + column;
		if (item.HasMeta(tipMeta))
		{
			string tipKey = item.GetMeta(tipMeta).AsString();
			item.SetTooltipText(column, T(tipKey));
		}

		TreeItem child = item.GetFirstChild();
		while (child != null)
		{
			RelocalizeTreeItems(child, column);
			child = child.GetNext();
		}
	}

	private static void LocalizeWindowTitle(Window window)
	{
		string src = CaptureOrGet(window, MetaTitle, window.Title);
		if (!string.IsNullOrEmpty(src))
			window.Title = T(src);
	}

	private static void LocalizeTextControl(Control control, Func<string> getText, Action<string> setText)
	{
		string current = getText() ?? string.Empty;
		// Icon-only or intentionally blank captions: only keep meta if already set.
		if (string.IsNullOrEmpty(current) && !control.HasMeta(MetaText))
			return;

		string src = CaptureOrGet(control, MetaText, current);
		if (!string.IsNullOrEmpty(src))
			setText(T(src));
	}

	private static void LocalizeTooltip(Control control)
	{
		string current = control.TooltipText ?? string.Empty;
		if (string.IsNullOrEmpty(current) && !control.HasMeta(MetaTooltip))
			return;

		string src = CaptureOrGet(control, MetaTooltip, current);
		if (!string.IsNullOrEmpty(src))
			control.TooltipText = T(src);
	}

	private static void LocalizePlaceholder(LineEdit lineEdit)
	{
		string current = lineEdit.PlaceholderText ?? string.Empty;
		if (string.IsNullOrEmpty(current) && !lineEdit.HasMeta(MetaPlaceholder))
			return;
		string src = CaptureOrGet(lineEdit, MetaPlaceholder, current);
		if (!string.IsNullOrEmpty(src))
			lineEdit.PlaceholderText = T(src);
	}

	private static void LocalizePlaceholder(TextEdit textEdit)
	{
		string current = textEdit.PlaceholderText ?? string.Empty;
		if (string.IsNullOrEmpty(current) && !textEdit.HasMeta(MetaPlaceholder))
			return;
		string src = CaptureOrGet(textEdit, MetaPlaceholder, current);
		if (!string.IsNullOrEmpty(src))
			textEdit.PlaceholderText = T(src);
	}

	/// <summary>
	/// Returns existing source metadata, or stores <paramref name="current"/> as the source on first use.
	/// </summary>
	private static string CaptureOrGet(GodotObject obj, string metaKey, string current)
	{
		if (obj.HasMeta(metaKey))
		{
			string existing = obj.GetMeta(metaKey).AsString();
			if (!string.IsNullOrEmpty(existing))
				return existing;
		}

		if (string.IsNullOrEmpty(current))
			return string.Empty;

		obj.SetMeta(metaKey, current);
		return current;
	}

	/// <summary>
	/// Stores a string at <paramref name="index"/> in a Godot Array kept as node metadata.
	/// </summary>
	private static void StoreIndexedMeta(GodotObject obj, string metaKey, int index, string value)
	{
		var arr = obj.HasMeta(metaKey)
			? obj.GetMeta(metaKey).AsGodotArray()
			: new Godot.Collections.Array();
		while (arr.Count <= index)
			arr.Add(string.Empty);
		arr[index] = value ?? string.Empty;
		obj.SetMeta(metaKey, arr);
	}

	/// <summary>
	/// Reads a string list stored by <see cref="StoreIndexedMeta"/>.
	/// </summary>
	private static List<string> ReadIndexedMeta(GodotObject obj, string metaKey)
	{
		var result = new List<string>();
		if (obj == null || !obj.HasMeta(metaKey))
			return result;

		var arr = obj.GetMeta(metaKey).AsGodotArray();
		foreach (Variant v in arr)
			result.Add(v.AsString());
		return result;
	}
}
