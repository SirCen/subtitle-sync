using System;
using System.Collections.Generic;
using Jellyfin.Plugin.SubtitleSync.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.SubtitleSync;

/// <summary>
/// The Subtitle Sync plugin entry point.
/// </summary>
/// <remarks>
/// Deliberately inert at this stage: it registers a configuration page and
/// nothing else. Endpoints (#6 to #9), the sync UI (#12) and the Subtitles-menu
/// injection (#13) are added on top of this shell.
/// </remarks>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">Server paths, supplied by the DI container.</param>
    /// <param name="xmlSerializer">Serialiser used for the plugin configuration file.</param>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    /// <summary>
    /// Gets the loaded plugin instance.
    /// </summary>
    /// <remarks>
    /// The server constructs exactly one <see cref="Plugin"/> through DI. This is
    /// the standard Jellyfin escape hatch for reaching
    /// <see cref="BasePlugin{TConfigurationType}.Configuration"/> from types the
    /// container does not build, such as static helpers.
    /// </remarks>
    public static Plugin? Instance { get; private set; }

    /// <summary>
    /// Gets the plugin's stable identity.
    /// </summary>
    /// <remarks>
    /// Generated once for this project. It is the key the server, the repository
    /// manifest and every installed copy agree on, so it must never change.
    /// </remarks>
    public override Guid Id => Guid.Parse("e981c765-b769-44a1-a4fe-805df5bb6d6b");

    /// <summary>
    /// Gets the name shown in Dashboard &gt; Plugins.
    /// </summary>
    public override string Name => "Subtitle Sync";

    /// <summary>
    /// Gets the one-line summary shown alongside the name.
    /// </summary>
    public override string Description => "Re-time subtitle tracks against the audio they belong to.";

    /// <summary>
    /// Gets the page name under which the browser bundle of <c>lib/</c> is served.
    /// </summary>
    /// <remarks>
    /// Registered pages are served from <c>/web/ConfigurationPage?name={Name}</c>,
    /// with the content type derived from the extension of
    /// <see cref="PluginPageInfo.EmbeddedResourcePath"/> - hence the <c>.js</c>
    /// suffix on both. This is the same trick the OpenSubtitles plugin uses to
    /// ship a script alongside its configuration page, and it is the only way to
    /// serve a static asset from a plugin in 10.11 without adding a controller.
    /// </remarks>
    public const string BundlePageName = "subtitleSync.js";

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        var prefix = GetType().Namespace;

        // EmbeddedResourcePath is the manifest resource name. For configPage.html
        // that is the MSBuild default (<RootNamespace>.<folder>.<file>); for the
        // bundle it is the LogicalName set in the csproj, because the file is
        // generated outside the project directory.
        return
        [
            new PluginPageInfo
            {
                Name = Name,
                EmbeddedResourcePath = prefix + ".Configuration.configPage.html",
            },
            new PluginPageInfo
            {
                Name = BundlePageName,
                EmbeddedResourcePath = prefix + ".Web.subtitleSync.js",
            }
        ];
    }
}
