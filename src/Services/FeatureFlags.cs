// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

namespace Cue2.Services;

/// <summary>
/// Source switches for features that ship later than the rest of Cue2.
/// </summary>
/// <remarks>
/// <see cref="CueNetEnabled"/> follows the build: on in the editor and debug exports,
/// off in release exports, so a public release never loads CueNet.
/// </remarks>
public static class FeatureFlags
{
    /// <summary>
    /// CueNet host plugin, network discovery, and Settings → CueNet.
    /// </summary>
    /// <remarks>
    /// True in the editor (Debug, which also defines TOOLS) and in debug exports (ExportDebug);
    /// false in release exports (ExportRelease, no DEBUG). When false,
    /// <see cref="HardwarePluginLoader"/> does not load <c>CueNet.Host.dll</c>, and the
    /// Settings tree omits CueNet. Change this line when CueNet is ready to ship.
    /// </remarks>
    /// <value><see langword="true"/> when CueNet UI and plugin loading are active.</value>
#if DEBUG
    public static readonly bool CueNetEnabled = true;
#else
    public static readonly bool CueNetEnabled = false;
#endif
}
