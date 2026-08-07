namespace Jellyfin.Plugin.SubtitleSync.Paths;

/// <summary>
/// Why a path could not be resolved.
/// </summary>
public enum SubtitlePathFailure
{
    /// <summary>No failure; the resolution succeeded.</summary>
    None = 0,

    /// <summary>The media path was empty, or named a file with no containing folder.</summary>
    InvalidMediaPath = 1,

    /// <summary>The folder the file would be written to cannot be written to.</summary>
    MediaFolderNotWritable = 2,

    /// <summary>The resulting file name exceeds what the filesystem allows.</summary>
    NameTooLong = 3,

    /// <summary>Every collision suffix up to the limit was already taken.</summary>
    NoAvailableName = 4,
}
