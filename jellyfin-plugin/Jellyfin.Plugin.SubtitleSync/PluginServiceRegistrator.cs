using Jellyfin.Plugin.SubtitleSync.Injection;
using Jellyfin.Plugin.SubtitleSync.SignalCache;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.SubtitleSync;

/// <summary>
/// Registers the plugin's own services with the server's container.
/// </summary>
/// <remarks>
/// <para>
/// The server finds this by scanning the plugin assembly for
/// <see cref="IPluginServiceRegistrator"/> and instantiating it with
/// <c>Activator.CreateInstance</c>
/// (<c>Emby.Server.Implementations/Plugins/PluginManager.cs</c> at v10.11.11),
/// so it needs a public parameterless constructor and there must be exactly one
/// of it in the assembly.
/// </para>
/// <para>
/// Only plugin-owned types belong here. Controllers are resolved from the
/// container because the server calls <c>AddControllersAsServices()</c>, which
/// means anything a controller constructor asks for must either be a core
/// singleton or be registered below - a missing entry fails at request time
/// with a resolution error, not at startup.
/// </para>
/// </remarks>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        // A singleton because it owns a lock that serialises writes and
        // eviction, and because it is stateless otherwise: its directory comes
        // from IApplicationPaths, which the server registers as a singleton, and
        // its size cap is read fresh from the configuration on every write so a
        // change on the configuration page takes effect without a restart.
        serviceCollection?.AddSingleton<ISignalCacheStore, SignalCacheStore>();

        // The Subtitles-menu injection (#13). Three pieces: the record of what
        // happened, the thing that makes it happen, and the startup hook that
        // runs it. Split up so the configuration page's status endpoint can read
        // the outcome without knowing anything about reflection or Harmony.
        //
        // AddHostedService rather than a scheduled task: this runs before
        // Kestrel serves its first index.html, which is the difference between
        // the menu item being there on first load and only after a refresh.
        serviceCollection?.AddSingleton<InjectionState>();
        serviceCollection?.AddSingleton<FileTransformationRegistrar>();
        serviceCollection?.AddHostedService<InjectionStartupService>();
    }
}
