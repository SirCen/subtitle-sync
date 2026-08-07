namespace Jellyfin.Plugin.SubtitleSync.Api;

/// <summary>
/// One subtitle track, as the plugin page needs to see it.
/// </summary>
public sealed class SubtitleTrackResponse
{
    /// <summary>
    /// Gets the stream index within its media source. This plus
    /// <see cref="MediaSourceId"/> is what identifies the track to
    /// <c>GET /SubtitleSync/Subtitle/{itemId}</c>.
    /// </summary>
    public int Index { get; init; }

    /// <summary>
    /// Gets the media source the index belongs to. Indexes are only unique
    /// within a source, so an item with several versions has several track 2s.
    /// </summary>
    public string MediaSourceId { get; init; } = string.Empty;

    /// <summary>
    /// Gets the track language, or null when the container or file name did not
    /// say. Null is common and is not an error.
    /// </summary>
    public string? Language { get; init; }

    /// <summary>
    /// Gets the codec name as reported by ffprobe, for example <c>subrip</c>,
    /// <c>ass</c> or <c>hdmv_pgs_subtitle</c>.
    /// </summary>
    public string? Codec { get; init; }

    /// <summary>
    /// Gets the track title, when the container or file name carried one.
    /// </summary>
    public string? Title { get; init; }

    /// <summary>
    /// Gets Jellyfin's own rendering of the track, the same string its player
    /// shows in the track picker, so our list reads identically to the one the
    /// user just came from.
    /// </summary>
    public string? DisplayTitle { get; init; }

    /// <summary>
    /// Gets a value indicating whether the track is a sidecar file rather than a
    /// stream inside the container.
    /// </summary>
    public bool IsExternal { get; init; }

    /// <summary>
    /// Gets the sidecar file path when <see cref="IsExternal"/> is set,
    /// otherwise null. Lets the page say which file a sync would replace.
    /// </summary>
    public string? Path { get; init; }

    /// <summary>
    /// Gets a value indicating whether the track is flagged default.
    /// </summary>
    public bool IsDefault { get; init; }

    /// <summary>
    /// Gets a value indicating whether the track is flagged forced.
    /// </summary>
    public bool IsForced { get; init; }

    /// <summary>
    /// Gets a value indicating whether the track is flagged for the hearing
    /// impaired.
    /// </summary>
    public bool IsHearingImpaired { get; init; }

    /// <summary>
    /// Gets the support level. Serialised as its name, for example
    /// <c>"ImageBased"</c>, so the page can branch on a stable token rather than
    /// on the message text.
    /// </summary>
    public SubtitleTrackSupport Support { get; init; }

    /// <summary>
    /// Gets a value indicating whether the page should offer to sync this track.
    /// False only for image-based formats, which can never work.
    /// </summary>
    public bool CanSync { get; init; }

    /// <summary>
    /// Gets a value indicating whether converting this track to SRT throws away
    /// styling. True for ASS and SSA.
    /// </summary>
    public bool StylingWillBeLost { get; init; }

    /// <summary>
    /// Gets the sentence to show beside the track, explaining why it is
    /// disabled or what a sync will cost. Null when there is nothing to say.
    /// </summary>
    public string? Note { get; init; }
}
