namespace Jellyfin.Plugin.SubtitleSync.Paths;

/// <summary>
/// Everything <see cref="SubtitlePathResolver"/> needs to decide where a synced
/// subtitle is written.
/// </summary>
public sealed class SubtitleOutputRequest
{
    /// <summary>
    /// Gets the full path of the media file the subtitle belongs to. The output
    /// is named after this file and, unless an external source is being
    /// overwritten, written beside it.
    /// </summary>
    public required string MediaPath { get; init; }

    /// <summary>
    /// Gets the source track's language, as reported by ffprobe or by Jellyfin.
    /// Anything that is not a plain language tag is dropped.
    /// </summary>
    public string? Language { get; init; }

    /// <summary>
    /// Gets the track the synced subtitle was derived from.
    /// </summary>
    public required SubtitleSource Source { get; init; }

    /// <summary>
    /// Gets a value indicating whether an external source file should be
    /// replaced instead of a new sibling being written.
    /// </summary>
    public bool OverwriteOriginal { get; init; }
}
