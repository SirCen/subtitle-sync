namespace Jellyfin.Plugin.SubtitleSync.Api;

/// <summary>
/// What the save endpoint actually did.
/// </summary>
public sealed class SubtitleSaveResponse
{
    /// <summary>
    /// Gets the full path written.
    /// </summary>
    /// <remarks>
    /// Not predictable from the request. Collision handling appends a numeric
    /// suffix, and a name lost to a concurrent save is resolved again, so this
    /// is the only authoritative answer to "where did it go?".
    /// </remarks>
    public required string Path { get; init; }

    /// <summary>
    /// Gets just the file name, which is what the page shows.
    /// </summary>
    public required string FileName { get; init; }

    /// <summary>
    /// Gets the language segment used in the name, or null when the source
    /// reported nothing usable.
    /// </summary>
    public string? Language { get; init; }

    /// <summary>
    /// Gets a value indicating whether an existing subtitle file was replaced
    /// rather than a new sibling written.
    /// </summary>
    public bool OverwroteSource { get; init; }

    /// <summary>
    /// Gets the size of the file written, in bytes.
    /// </summary>
    public long Bytes { get; init; }

    /// <summary>
    /// Gets how many cues the document held.
    /// </summary>
    public int CueCount { get; init; }

    /// <summary>
    /// Gets a value indicating whether an item refresh was queued.
    /// </summary>
    /// <remarks>
    /// The refresh is queued, not awaited, so a client that wants to see the new
    /// track in the picker should poll the item rather than assume it is there
    /// the moment this response arrives.
    /// </remarks>
    public bool RefreshQueued { get; init; }
}
