// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

namespace Cue2.Services;

/// <summary>
/// Source switches for features that ship later than the rest of Cue2.
/// </summary>
/// <remarks>
/// Flip these before a public release. <see cref="CueNetEnabled"/> is true while CueNet is
/// in development so Settings and the host plugin stay available in the editor.
/// </remarks>
public static class FeatureFlags
{
    /// <summary>
    /// CueNet host plugin, network discovery, and Settings → CueNet.
    /// </summary>
    /// <remarks>
    /// Keep true while developing CueNet. Set false before a public Cue2 release until CueNet
    /// is ready to ship. When false, <see cref="HardwarePluginLoader"/> does not load
    /// <c>CueNet.Host.dll</c>, and the Settings tree omits CueNet.
    /// </remarks>
    /// <value><see langword="true"/> when CueNet UI and plugin loading are active.</value>
    public static readonly bool CueNetEnabled = true;
}
