namespace Jellyfin.Plugin.SubtitleSync.SignalCache;

/// <summary>
/// What the cache currently holds. Serialised straight to the configuration
/// page's readout.
/// </summary>
public sealed record SignalCacheStats
{
    /// <summary>
    /// Gets the number of stored signals.
    /// </summary>
    public required int EntryCount { get; init; }

    /// <summary>
    /// Gets the total size of those entries on disk, in bytes.
    /// </summary>
    public required long TotalBytes { get; init; }

    /// <summary>
    /// Gets the configured cap in bytes, or zero for unbounded.
    /// </summary>
    public required long SizeLimitBytes { get; init; }

    /// <summary>
    /// Gets the directory the entries live in.
    /// </summary>
    /// <remarks>
    /// Shown on the configuration page so an administrator can see for
    /// themselves that the cache is under the server's data path and not inside
    /// the plugin's install directory, which is wiped on every update.
    /// </remarks>
    public required string Directory { get; init; }
}
