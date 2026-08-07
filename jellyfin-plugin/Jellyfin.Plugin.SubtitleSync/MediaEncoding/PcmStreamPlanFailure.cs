namespace Jellyfin.Plugin.SubtitleSync.MediaEncoding;

/// <summary>
/// Why a PCM extraction could not be planned.
/// </summary>
/// <remarks>
/// Split by what the caller should do about it, which for an HTTP endpoint means
/// split by status code: <see cref="NoMediaSource"/> and
/// <see cref="UnknownMediaSource"/> are "you asked about something that is not
/// there", the rest are "that request does not make sense".
/// </remarks>
public enum PcmStreamPlanFailure
{
    /// <summary>No failure; the plan is usable.</summary>
    None = 0,

    /// <summary>The item exists but has no media sources at all.</summary>
    NoMediaSource = 1,

    /// <summary>A media source id was given and no source has it.</summary>
    UnknownMediaSource = 2,

    /// <summary>The chosen source has no path on disk to decode.</summary>
    MissingPath = 3,

    /// <summary>
    /// The chosen source is not a local file, so ffmpeg would have to open a
    /// network protocol the argument builder deliberately refuses to whitelist.
    /// </summary>
    UnsupportedProtocol = 4,

    /// <summary>The chosen source has no audio stream to extract.</summary>
    NoAudioStream = 5,

    /// <summary>
    /// An audio stream index was given and the source has no audio stream at
    /// that index.
    /// </summary>
    UnknownAudioStream = 6,

    /// <summary>
    /// The chosen audio stream lives in a separate file rather than in the
    /// media container, which this endpoint does not decode.
    /// </summary>
    ExternalAudioStream = 7,
}
