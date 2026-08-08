using System;

namespace Jellyfin.Plugin.SubtitleSync.Injection;

/// <summary>
/// What we know about the third-party File Transformation plugin, in one place.
/// </summary>
/// <remarks>
/// <para>
/// The Subtitles-menu item (#13) exists only because Jellyfin 10.11 has no
/// supported way to add UI to the item detail page. File Transformation is how
/// we get a script into the web client, and it works by Harmony-patching
/// <c>Jellyfin.Server.Startup.Configure</c> and reading private fields by
/// reflection. It can therefore break on any Jellyfin point release, not only a
/// web client update.
/// </para>
/// <para>
/// It also cannot be depended on: Jellyfin 10.11 has no plugin dependency
/// mechanism at all, and a <c>dependencies</c> array in a repository manifest is
/// silently discarded. Runtime detection and a note on the configuration page is
/// the whole of what is available to us.
/// </para>
/// <para>
/// See <c>research/jellyfin-10.11-plugin-api.md</c> section 11.
/// </para>
/// </remarks>
public static class FileTransformationFacts
{
    /// <summary>
    /// The File Transformation plugin's own id, from its
    /// <c>FileTransformationPlugin.cs</c>. Used to tell "not installed" apart
    /// from "installed but its API has moved", which look identical otherwise.
    /// </summary>
    public static readonly Guid PluginId = new("5e87cc92-571a-4d8d-8d98-d2d4147f9f90");

    /// <summary>
    /// The plugin's display name, for the message on our configuration page.
    /// </summary>
    public const string PluginName = "File Transformation";

    /// <summary>
    /// The repository the administrator has to add under Dashboard &gt; Plugins
    /// &gt; Repositories before they can install it.
    /// </summary>
    public const string RepositoryUrl = "https://www.iamparadox.dev/jellyfin/plugins/manifest.json";

    /// <summary>
    /// Where the plugin's source and its own install instructions live.
    /// </summary>
    public const string ProjectUrl = "https://github.com/IAmParadox27/jellyfin-plugin-file-transformation";

    /// <summary>
    /// Marker used to find the plugin's assembly. Jellyfin loads every plugin
    /// into its own <see cref="System.Runtime.Loader.AssemblyLoadContext"/>, so
    /// a project reference would not give us the same type identity the running
    /// server has - reflection over the loaded assembly is the only route.
    /// </summary>
    public const string AssemblyMarker = ".FileTransformation";

    /// <summary>
    /// The static entry point we call. It takes a
    /// <c>Newtonsoft.Json.Linq.JObject</c>, which is why this project has a
    /// Newtonsoft.Json package reference at all.
    /// </summary>
    public const string PluginInterfaceTypeName = "Jellyfin.Plugin.FileTransformation.PluginInterface";

    /// <summary>
    /// The method name on <see cref="PluginInterfaceTypeName"/>.
    /// </summary>
    public const string RegisterMethodName = "RegisterTransformation";
}
