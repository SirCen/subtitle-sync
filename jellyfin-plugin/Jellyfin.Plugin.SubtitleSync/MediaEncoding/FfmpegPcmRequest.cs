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
    /// This is Jellyfin's <c>MediaStream.Index</c>, which counts every stream in
    /// the file, not just the audio ones.
    /// </remarks>
    public int? AudioStreamIndex { get; init; }

    /// <summary>
    /// Gets a value indicating whether ffmpeg should emit machine-readable
    /// progress blocks on stderr. Defaults to <see langword="true"/>.
    /// </summary>
    public bool ReportProgress { get; init; } = true;
}
