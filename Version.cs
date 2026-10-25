// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

namespace Cue2; 
 
/// <summary> 
/// Provides version information for the Cue2 application. 
/// </summary> 
public static class Version 
{ 
    /// <summary> 
    /// Gets the short name of the application. 
    /// </summary> 
    /// <value>The short name string, e.g., "cue2".</value> 
    public static readonly string ShortName = "cue2"; 
 
    /// <summary> 
    /// Gets the full name of the application. 
    /// </summary> 
    /// <value>The full name string, e.g., "Cue2".</value> 
    public static readonly string Name = "Cue2"; 
 
    /// <summary> 
    /// Gets the major version number. 
    /// </summary> 
    /// <value>The major version integer.</value> 
    public static readonly int Major = 0; 
 
    /// <summary> 
    /// Gets the minor version number. 
    /// </summary> 
    /// <value>The minor version integer.</value> 
    public static readonly int Minor = 2; 
 
    /// <summary> 
    /// Gets the patch version number. 
    /// </summary> 
    /// <value>The patch version integer.</value> 
    public static readonly int Patch = 0; 

    /// <summary>
    /// Gets the SemVer prerelease label without the leading dash, e.g. "rc.1". Empty for a stable release.
    /// </summary>
    /// <value>The prerelease label. Stamped by <c>tools/export-all.py</c> from the release version.</value>
    /// <remarks>Declared before the version strings below, which read it during static initialization.</remarks>
    public static readonly string Prerelease = "";
 
    /// <summary> 
    /// Gets the version status. Empty for a public release. 
    /// </summary> 
    /// <value>The status string, e.g., "dev", or empty when shipping.</value>
    /// <remarks>
    /// Before a public release, also set <c>FeatureFlags.CueNetEnabled</c> to false until CueNet is ready to ship.
    /// </remarks>
    public static readonly string Status = ""; 
 
    /// <summary> 
    /// Gets the code name of the version. 
    /// </summary> 
    /// <value>The code name string.</value> 
    public static readonly string CodeName = "StripyHat"; 
 
    /// <summary> 
    /// Gets the module configuration. 
    /// </summary> 
    /// <value>The module config string.</value> 
    public static readonly string ModuleConfig = "";

    /// <summary>
    /// GitHub organization or user that publishes Cue2 Releases (in-app updater feed).
    /// </summary>
    public static readonly string GitHubOwner = "Tech-mop";

    /// <summary>
    /// GitHub repository name that publishes Cue2 Releases.
    /// </summary>
    public static readonly string GitHubRepo = "Cue2";
 
    /// <summary> 
    /// Gets the official website URL. 
    /// </summary> 
    /// <value>The website URL string.</value> 
    public static readonly string Website = "https://www.cue2.live/";

    /// <summary>
    /// Gets the documentation website URL.
    /// </summary>
    /// <value>The documentation website URL string.</value>
    public static readonly string DocsWebsite = "https://docs.cue2.live/";
 
    /// <summary>
    /// Gets the documentation version.
    /// </summary>
    /// <value>The docs version string, e.g., "latest".</value>
    public static readonly string Docs = "latest";

    /// <summary>
    /// Gets the full version string combining major, minor, patch, status, and code name.
    /// </summary>
    /// <value>The full version string, e.g. "v0.1.0 - StripyHat" or "v0.1.0 alpha - StripyHat".</value>
    public static readonly string FullVersionString = string.IsNullOrEmpty(Status)
        ? $"v{SemanticVersionCore()} - {CodeName}"
        : $"v{SemanticVersionCore()} {Status} - {CodeName}";

    /// <summary>
    /// Gets the semantic version string used when stamping showfiles and comparing updates
    /// (major.minor.patch, plus -prerelease for a prerelease build).
    /// </summary>
    /// <value>The semantic version, e.g. "0.1.0" or "0.2.0-rc.1".</value>
    public static readonly string SemanticVersionString = SemanticVersionCore();

    private static string SemanticVersionCore() => string.IsNullOrEmpty(Prerelease)
        ? $"{Major}.{Minor}.{Patch}"
        : $"{Major}.{Minor}.{Patch}-{Prerelease}";
}
