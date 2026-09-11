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
    public static readonly int Minor = 1; 
 
    /// <summary> 
    /// Gets the patch version number. 
    /// </summary> 
    /// <value>The patch version integer.</value> 
    public static readonly int Patch = 0; 
 
    /// <summary> 
    /// Gets the version status. Empty for a public release. 
    /// </summary> 
    /// <value>The status string, e.g., "dev", or empty when shipping.</value> 
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
        ? $"v{Major}.{Minor}.{Patch} - {CodeName}"
        : $"v{Major}.{Minor}.{Patch} {Status} - {CodeName}";

    /// <summary>
    /// Gets the semantic version string used when stamping showfiles (major.minor.patch).
    /// </summary>
    /// <value>The semantic version, e.g. "0.1.0".</value>
    public static readonly string SemanticVersionString = $"{Major}.{Minor}.{Patch}";
}
