using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.SubtitleSync.Api;

/// <summary>
/// Everything the plugin sync page needs to render its track pickers, returned
/// by <c>GET /SubtitleSync/Item/{itemId}</c>.
/// </summary>
public sealed class ItemResponse
{
    /// <summary>
    /// Gets the item id, echoed back so the page can key its state on the
    /// response rather than on the URL it happened to request.
    /// </summary>
    public Guid ItemId { get; init; }

    /// <summary>
    /// Gets the item name.
    /// </summary>
    public string? Name { get; init; }

    /// <summary>
    /// Gets the concrete item type, for example <c>Movie</c> or <c>Episode</c>.
    /// </summary>
    public string ItemType { get; init; } = string.Empty;

    /// <summary>
    /// Gets the series name when the item is an episode, otherwise null.
    /// </summary>
    public string? SeriesName { get; init; }

    /// <summary>
    /// Gets the season number when the item is an episode.
    /// </summary>
    public int? ParentIndexNumber { get; init; }

    /// <summary>
    /// Gets the episode number when the item is an episode.
    /// </summary>
    public int? IndexNumber { get; init; }

    /// <summary>
    /// Gets the item runtime in ticks, when known.
    /// </summary>
    public long? RunTimeTicks { get; init; }

    /// <summary>
    /// Gets the item runtime in seconds, when known. Provided alongside ticks
    /// because every consumer of this is JavaScript working in seconds, and a
    /// tick division scattered across the front end is a rounding bug waiting to
    /// happen.
    /// </summary>
    public double? RunTimeSeconds { get; init; }

    /// <summary>
    /// Gets the item's media versions, each carrying its own streams. Empty for
    /// an item with no playable file.
    /// </summary>
    public IReadOnlyList<MediaSourceResponse> MediaSources { get; init; } = [];

    /// <summary>
    /// Gets a value indicating whether any media source has at least one track
    /// worth offering. False means "there is nothing here to sync", which the
    /// page shows as an explanation rather than an empty dropdown.
    /// </summary>
    public bool HasSyncableSubtitles { get; init; }
}
