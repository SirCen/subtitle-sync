using System;
using Jellyfin.Plugin.SubtitleSync.Injection;

namespace Jellyfin.Plugin.SubtitleSync.Api;

/// <summary>
/// What the configuration page needs to know about the Subtitles-menu item.
/// </summary>
/// <remarks>
/// Serialised by the server's <c>System.Text.Json</c> options, which use PascalCase.
/// </remarks>
public sealed class InjectionStatusResponse
{
    /// <summary>
    /// Gets or sets the outcome, as a string so a future value added here does
    /// not deserialise as a meaningless number in an older page.
    /// </summary>
    public string Availability { get; set; } = nameof(InjectionAvailability.Unknown);

    /// <summary>
    /// Gets or sets a value indicating whether the menu item should appear for
    /// administrators. The single question the page actually acts on.
    /// </summary>
    public bool MenuItemActive { get; set; }

    /// <summary>
    /// Gets or sets the loaded File Transformation version, or null.
    /// </summary>
    public string? FileTransformationVersion { get; set; }

    /// <summary>
    /// Gets or sets a one-line explanation when something is wrong.
    /// </summary>
    public string? Detail { get; set; }

    /// <summary>
    /// Gets or sets the repository URL an administrator has to add to install
    /// File Transformation. Served rather than hard-coded into the page so the
    /// C# constant stays the single source of truth.
    /// </summary>
    public Uri RepositoryUrl { get; set; } = new(FileTransformationFacts.RepositoryUrl);

    /// <summary>
    /// Gets or sets the plugin's project page.
    /// </summary>
    public Uri ProjectUrl { get; set; } = new(FileTransformationFacts.ProjectUrl);

    /// <summary>
    /// Gets or sets the plugin's display name, as it appears in the Jellyfin
    /// plugin catalogue.
    /// </summary>
    public string PluginName { get; set; } = FileTransformationFacts.PluginName;
}
