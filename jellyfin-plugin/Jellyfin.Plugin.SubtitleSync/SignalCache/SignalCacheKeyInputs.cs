using System;

namespace Jellyfin.Plugin.SubtitleSync.SignalCache;

/// <summary>
/// Everything a cached speech signal depends on.
/// </summary>
/// <remarks>
/// If two analyses agree on all six of these they are bit-identical, and if any
/// one differs the cached answer is wrong. That is the whole contract. The two
/// filesystem facts are the interesting half: a user who replaces
/// <c>Movie.mkv</c> with a different encode keeps the same item id and the same
/// media source id, so without length and modification time the cache would
/// serve them a signal for a file that no longer exists, and the resulting sync
/// would be confidently wrong with nothing to point at.
/// </remarks>
public sealed record SignalCacheKeyInputs
{
    /// <summary>
    /// Gets the Jellyfin item id.
    /// </summary>
    public required string ItemId { get; init; }

    /// <summary>
    /// Gets the media source id, which distinguishes the versions of an item
    /// that share it.
    /// </summary>
    public required string MediaSourceId { get; init; }

    /// <summary>
    /// Gets the absolute index of the analysed audio stream within the
    /// container, as Jellyfin numbers it.
    /// </summary>
    public required int AudioStreamIndex { get; init; }

    /// <summary>
    /// Gets the VAD aggressiveness the browser ran at. A different setting
    /// produces a different signal from the same audio, so it is part of the
    /// identity rather than a detail.
    /// </summary>
    public required int VadAggressiveness { get; init; }

    /// <summary>
    /// Gets the media file's length in bytes.
    /// </summary>
    public required long FileLength { get; init; }

    /// <summary>
    /// Gets the media file's last write time. Normalised to UTC before hashing,
    /// so a server that changes timezone does not lose its cache.
    /// </summary>
    public required DateTime FileModifiedUtc { get; init; }
}
