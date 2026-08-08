namespace Jellyfin.Plugin.SubtitleSync.Writing;

/// <summary>
/// Why a synced subtitle was not written.
/// </summary>
/// <remarks>
/// Each value maps to one HTTP status and one actionable message. A silent
/// no-op is not in the vocabulary on purpose: the most common deployment
/// problem this endpoint hits is a read-only library mount, and an administrator
/// who is told nothing will conclude the sync itself is broken.
/// </remarks>
public enum SubtitleWriteFailure
{
    /// <summary>No failure; the file was written.</summary>
    None = 0,

    /// <summary>The item's media path was empty or named no containing folder.</summary>
    InvalidMediaPath = 1,

    /// <summary>The folder holding the media file is not on disk.</summary>
    MediaFolderMissing = 2,

    /// <summary>The media file itself is no longer on disk.</summary>
    MediaFileMissing = 3,

    /// <summary>The destination folder refused a file. Read-only mount, or wrong owner.</summary>
    NotWritable = 4,

    /// <summary>The subtitle name would be longer than the filesystem allows.</summary>
    NameTooLong = 5,

    /// <summary>Every collision-suffixed name is taken, or the name was lost to a race too many times.</summary>
    NoAvailableName = 6,

    /// <summary>The resolved path was not inside the item's own media folder. Should be unreachable.</summary>
    UnsafeTarget = 7,

    /// <summary>The write or the rename failed for a reason that is none of the above.</summary>
    WriteFailed = 8,
}
