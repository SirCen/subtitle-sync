using System.Collections.Generic;

namespace Jellyfin.Plugin.SubtitleSync.Api;

/// <summary>
/// One version of an item's media, with the streams that belong to it.
/// </summary>
/// <remarks>
/// Streams are nested inside the source rather than flattened onto the item
/// because a stream index only means anything relative to its source. An item
/// with a 1080p and a 4K version has two stream 2s, and flattening them would
/// silently sync the wrong track.
/// </remarks>
public sealed class MediaSourceResponse
{
    /// <summary>
    /// Gets the media source id, to be passed back to the subtitle and PCM
    /// endpoints. For a plain file item this is the item id in <c>"N"</c> form.
    /// </summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>
    /// Gets the version name Jellyfin shows in its version picker.
    /// </summary>
    public string? Name { get; init; }

    /// <summary>
    /// Gets the media file path on the server.
    /// </summary>
    public string? Path { get; init; }

    /// <summary>
    /// Gets the container format, for example <c>mkv</c>.
    /// </summary>
    public string? Container { get; init; }

    /// <summary>
    /// Gets the runtime in ticks for this specific version, when known. May
    /// differ from the item's runtime for a differently cut version.
    /// </summary>
    public long? RunTimeTicks { get; init; }

    /// <summary>
    /// Gets the index of the audio stream Jellyfin would play by default, when
    /// it has an opinion.
    /// </summary>
    public int? DefaultAudioStreamIndex { get; init; }

    /// <summary>
    /// Gets the audio tracks, in stream order.
    /// </summary>
    public IReadOnlyList<AudioStreamResponse> AudioStreams { get; init; } = [];

    /// <summary>
    /// Gets the subtitle tracks, in stream order, each already classified as
    /// syncable or not.
    /// </summary>
    public IReadOnlyList<SubtitleTrackResponse> SubtitleStreams { get; init; } = [];
}
