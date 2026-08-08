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
    public override Guid Id => Guid.Parse("96d55013-3cf0-465e-9036-7fb73dd47f71");

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

    /// <summary>
    /// Gets the page name under which the sync page's own UI bundle is served.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="BundlePageName"/> so the shared algorithm and
    /// the user interface are two downloads: the first is 47 KB of lib/ plus
    /// inline libfvad and changes rarely, the second is the page and changes
    /// often. Loaded in that order by the bootstrap in <c>syncPage.html</c>,
    /// because the page reads <c>window.SubtitleSync</c> at init time.
    /// </remarks>
    public const string PageBundlePageName = "subtitleSyncPage.js";

    /// <summary>
    /// Gets the page name of the sync UI itself.
    /// </summary>
    /// <remarks>
    /// Reached at <c>/web/#/configurationpage?name=SubtitleSyncPage</c>, with an
    /// optional <c>&amp;itemId=</c> for the injected Subtitles-menu item (#13).
    /// Without one the page shows a library picker, which is the primary route:
    /// the injection depends on a third-party plugin and cannot be relied on.
    /// Deliberately not the plugin's display name - that name belongs to the
    /// configuration page the Dashboard links to.
    /// </remarks>
    public const string SyncPageName = "SubtitleSyncPage";

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        var prefix = GetType().Namespace;

        // EmbeddedResourcePath is the manifest resource name. For the two HTML
        // pages that is the MSBuild default (<RootNamespace>.<folder>.<file>);
        // for the bundles it is the LogicalName set in the csproj, because those
        // files are generated outside the project directory.
        return
        [
            new PluginPageInfo
            {
                Name = Name,
                EmbeddedResourcePath = prefix + ".Configuration.configPage.html",
            },
            new PluginPageInfo
            {
                Name = SyncPageName,
                EmbeddedResourcePath = prefix + ".Configuration.syncPage.html",
            },
            new PluginPageInfo
            {
                Name = BundlePageName,
                EmbeddedResourcePath = prefix + ".Web.subtitleSync.js",
            },
            new PluginPageInfo
            {
                Name = PageBundlePageName,
                EmbeddedResourcePath = prefix + ".Web.subtitleSyncPage.js",
            }
        ];
    }
}
