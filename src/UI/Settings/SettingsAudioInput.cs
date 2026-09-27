// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using Cue2.Services;
using Cue2.UI.Utilities;
using Godot;

namespace Cue2.UI.Settings;

/// <summary>
/// Settings panel for show-scoped audio capture devices (Settings → Audio → Audio Input).
/// </summary>
public partial class SettingsAudioInput : ScrollContainer
{
    /// <summary>Stable Settings tree key (English, persisted in user data).</summary>
    public const string MenuKey = "Audio Input";

    private GlobalSignals _globalSignals;

    /// <inheritdoc />
    public override void _Ready()
    {
        _globalSignals = GetNodeOrNull<GlobalSignals>("/root/GlobalSignals");
        UiLocalizer.LocalizeTree(this);
        if (_globalSignals != null)
            _globalSignals.LocaleChanged += OnLocaleChanged;
    }

    /// <inheritdoc />
    public override void _ExitTree()
    {
        if (_globalSignals != null)
            _globalSignals.LocaleChanged -= OnLocaleChanged;
        base._ExitTree();
    }

    /// <summary>
    /// Re-localizes panel chrome when the UI language changes.
    /// </summary>
    /// <param name="localeCode">New locale code.</param>
    private void OnLocaleChanged(string localeCode)
    {
        if (!GodotObject.IsInstanceValid(this))
            return;
        UiLocalizer.LocalizeTree(this);
    }
}
