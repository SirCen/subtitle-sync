namespace Jellyfin.Plugin.SubtitleSync.Subtitles;

/// <summary>
/// Why a posted document was not accepted as SRT.
/// </summary>
public enum SrtValidationError
{
    /// <summary>No failure; the document is valid SRT.</summary>
    None = 0,

    /// <summary>The document held no cues at all.</summary>
    Empty = 1,

    /// <summary>A cue did not start with a well-formed timing line.</summary>
    MissingTiming = 2,

    /// <summary>A cue ended before it started.</summary>
    ReversedCue = 3,

    /// <summary>A cue had a timing line but no text under it.</summary>
    EmptyCue = 4,

    /// <summary>The text carried a control character that has no place in a subtitle.</summary>
    ControlCharacter = 5,

    /// <summary>The document held more cues than the plugin will write.</summary>
    TooManyCues = 6,
}
