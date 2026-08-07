using System;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.SubtitleSync.SignalCache;

/// <summary>
/// The server-side store of analysed speech signals.
/// </summary>
/// <remarks>
/// Deals in whole envelopes (see <see cref="SpeechSignalCodec"/>) rather than
/// decoded signals, because the server never looks inside one: it stores what
/// the browser produced and hands the same bytes back. Validation still happens
/// on both sides of the boundary, but as a check, not as a transformation.
/// </remarks>
public interface ISignalCacheStore
{
    /// <summary>
    /// Gets the directory entries are stored in.
    /// </summary>
    string CacheDirectory { get; }

    /// <summary>
    /// Fetches a stored signal.
    /// </summary>
    /// <param name="key">The cache key. Validated before it touches a file API.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// The envelope, or <see langword="null"/> for a miss. A damaged or
    /// truncated entry is a miss too: it is dropped and reported as absent
    /// rather than returned in part.
    /// </returns>
    /// <exception cref="ArgumentException">The key is not well formed.</exception>
    Task<byte[]?> ReadAsync(string key, CancellationToken cancellationToken);

    /// <summary>
    /// Stores a signal, replacing any existing entry for the key.
    /// </summary>
    /// <param name="key">The cache key. Validated before it touches a file API.</param>
    /// <param name="envelope">The envelope to store.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes once the entry is durable and visible.</returns>
    /// <exception cref="ArgumentException">The key is not well formed.</exception>
    /// <exception cref="System.IO.InvalidDataException">The payload is not a valid envelope.</exception>
    Task WriteAsync(string key, ReadOnlyMemory<byte> envelope, CancellationToken cancellationToken);

    /// <summary>
    /// Measures what is currently stored.
    /// </summary>
    /// <returns>The entry count, the bytes used and the configured cap.</returns>
    SignalCacheStats GetStats();

    /// <summary>
    /// Deletes every entry.
    /// </summary>
    /// <returns>How many entries were removed.</returns>
    int Clear();
}
