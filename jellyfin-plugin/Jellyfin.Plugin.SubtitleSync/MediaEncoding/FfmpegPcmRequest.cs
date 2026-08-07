namespace Jellyfin.Plugin.SubtitleSync.MediaEncoding;

/// <summary>
/// What to extract: everything about a PCM extraction that varies per call.
/// </summary>
/// <remarks>
/// The output format is deliberately absent. 16 kHz mono s16le is a contract
/// shared with the browser adapter and the Python oracle, not a setting, so it
/// lives as constants on <see cref="FfmpegArguments"/> where nothing can vary
/// it per request.
/// </remarks>
public sealed record FfmpegPcmRequest
{
    /// <summary>
    /// Gets the path of the media file to read, exactly as the library holds it.
    /// </summary>
    /// <remarks>
    /// Pass the raw path. Do not pre-quote it, do not prefix a protocol and do
    /// not escape anything: <see cref="FfmpegArguments"/> owns the protocol
    /// prefix, and a caller that adds its own defeats the check rather than
    /// reinforcing it.
    /// </remarks>
    public required string InputPath { get; init; }

    /// <summary>
    /// Gets the absolute index within the container of the audio stream to
    /// decode, or <see langword="null"/> to let ffmpeg pick the default track.
    /// </summary>
    /// <remarks>
    /// This is the stream's position within the container, counting every
    /// stream in the file rather than just the audio ones, and it is what
    /// <c>-map 0:N</c> expects.
    /// <para>
    /// It is NOT Jellyfin's <c>MediaStream.Index</c>. Jellyfin numbers external
    /// sidecar subtitles into the same sequence, so a two-stream mp4 with a
    /// sibling <c>.en.srt</c> is numbered subtitle 0, video 1, audio 2, and
    /// passing that 2 straight through makes ffmpeg fail with
    /// "Stream map '' matches no streams". Use
    /// <see cref="PcmStreamPlanner"/>, which derives this value as the stream's
    /// rank among non-external streams.
    /// </para>
    /// </remarks>
    public int? AudioStreamIndex { get; init; }

    /// <summary>
    /// Gets a value indicating whether ffmpeg should emit machine-readable
    /// progress blocks on stderr. Defaults to <see langword="true"/>.
    /// </summary>
    public bool ReportProgress { get; init; } = true;
}
