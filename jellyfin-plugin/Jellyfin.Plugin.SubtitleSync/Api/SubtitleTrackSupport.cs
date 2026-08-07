namespace Jellyfin.Plugin.SubtitleSync.Api;

/// <summary>
/// Whether a subtitle track is something this plugin can re-time.
/// </summary>
/// <remarks>
/// Reported up front on every track in the item response so the plugin page can
/// disable the ones that can never work, with a reason, instead of letting the
/// user start a several-minute analysis that was doomed before it began.
/// </remarks>
public enum SubtitleTrackSupport
{
    /// <summary>
    /// A text format we recognise. Convertible to SRT and safe to sync.
    /// </summary>
    Supported = 0,

    /// <summary>
    /// A bitmap format such as PGS, VobSub or DVB subtitles. The track is a
    /// sequence of pictures, so there is no text to correlate against speech and
    /// no amount of conversion will produce any. Permanently unsyncable.
    /// </summary>
    ImageBased = 1,

    /// <summary>
    /// A codec that is neither in our text list nor known to be image based.
    /// Allowed through, because the list of obscure text formats ffmpeg reads is
    /// long and a false negative here would block a track that works fine, but
    /// flagged so the UI can warn that conversion may fail.
    /// </summary>
    UnknownFormat = 2,
}
