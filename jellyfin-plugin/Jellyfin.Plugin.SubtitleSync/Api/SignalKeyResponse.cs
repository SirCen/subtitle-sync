namespace Jellyfin.Plugin.SubtitleSync.Api;

/// <summary>
/// The identity of the cached speech signal a given analysis would produce,
/// returned by <c>GET /SubtitleSync/SignalKey/{itemId}</c>.
/// </summary>
/// <remarks>
/// <para>
/// The key is derived from six values (<see cref="SignalCache.SignalCacheKeyInputs"/>),
/// two of which - the media file's length and its last write time - are
/// filesystem facts the browser has no way of learning. So the page cannot
/// compute this itself, and without it the cache from #9 is unreachable: every
/// run would pull the full PCM stream, about 115 MB per hour of runtime, to
/// rebuild a 45 KB signal the server may already be holding.
/// </para>
/// <para>
/// Deliberately a separate call rather than a field on
/// <see cref="ItemResponse"/>. VAD aggressiveness is part of the identity and is
/// chosen by the user in the Advanced panel, so an item response would have to
/// carry the whole cross-product of audio tracks and aggressiveness levels, and
/// stat the media file on every page load to do it.
/// </para>
/// </remarks>
public sealed class SignalKeyResponse
{
    /// <summary>
    /// Gets the cache key: sixty-four lowercase hexadecimal characters, ready to
    /// be used as the path segment of the signal endpoints.
    /// </summary>
    public required string Key { get; init; }

    /// <summary>
    /// Gets the media source the key describes, resolved from the request.
    /// </summary>
    /// <remarks>
    /// Echoed because the caller may have omitted it. The subtitle track and the
    /// PCM stream must name the same source this key was derived from, or the
    /// cache would answer for the wrong version of the item.
    /// </remarks>
    public string? MediaSourceId { get; init; }

    /// <summary>
    /// Gets the audio stream the key describes, as Jellyfin numbers it. Resolved
    /// from the source's default when the caller omitted it.
    /// </summary>
    public int AudioStreamIndex { get; init; }

    /// <summary>
    /// Gets the VAD aggressiveness the key describes.
    /// </summary>
    public int VadAggressiveness { get; init; }
}
