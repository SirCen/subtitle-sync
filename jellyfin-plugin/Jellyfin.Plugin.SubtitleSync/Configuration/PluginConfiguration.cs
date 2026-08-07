using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.SubtitleSync.Configuration;

/// <summary>
/// Persisted settings for the Subtitle Sync plugin.
/// </summary>
/// <remarks>
/// Serialised to XML by the server into
/// <c>IApplicationPaths.PluginConfigurationsPath</c>. Every property therefore
/// needs a public setter and a sensible default, because an existing config file
/// written by an older build will simply be missing any newly added element.
/// </remarks>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets a value indicating whether a synced subtitle replaces the
    /// track it was derived from, instead of being written alongside it as
    /// <c>&lt;base&gt;.&lt;lang&gt;.synced.srt</c>.
    /// </summary>
    /// <remarks>
    /// Defaults to <see langword="false"/>. Overwriting is destructive and there
    /// is no undo, so it has to be opted into.
    /// </remarks>
    public bool OverwriteOriginal { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether analysed speech signals are cached
    /// on the server so a re-sync of the same file skips the audio decode.
    /// </summary>
    /// <remarks>
    /// Placeholder for issue #9, which owns the cache implementation. Nothing
    /// reads this yet.
    /// </remarks>
    public bool EnableSignalCache { get; set; } = true;

    /// <summary>
    /// Gets or sets the upper bound, in megabytes, on the on-disk speech signal
    /// cache. Zero means unbounded.
    /// </summary>
    /// <remarks>
    /// Placeholder for issue #9. A bit-packed 100 Hz signal is roughly 45 KB per
    /// hour of runtime, so the default is generous by design.
    /// </remarks>
    public int SignalCacheSizeLimitMb { get; set; } = 512;
}
