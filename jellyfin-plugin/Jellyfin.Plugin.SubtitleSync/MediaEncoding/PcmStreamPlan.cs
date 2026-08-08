namespace Jellyfin.Plugin.SubtitleSync.MediaEncoding;

/// <summary>
/// The answer to "what exactly should ffmpeg decode for this request?".
/// </summary>
/// <remarks>
/// Produced by <see cref="PcmStreamPlanner"/>, which is pure, so every rule about
/// picking a media source and an audio track is unit-testable without a library,
/// an item or a running server.
/// </remarks>
public sealed class PcmStreamPlan
{
    private PcmStreamPlan()
    {
    }

    /// <summary>
    /// Gets a value indicating whether an extraction can go ahead.
    /// </summary>
    public bool Succeeded => Failure == PcmStreamPlanFailure.None;

    /// <summary>
    /// Gets the failure reason, or <see cref="PcmStreamPlanFailure.None"/>.
    /// </summary>
    public PcmStreamPlanFailure Failure { get; private init; }

    /// <summary>
    /// Gets a message suitable for returning to the client, or null on success.
    /// </summary>
    public string? ErrorMessage { get; private init; }

    /// <summary>
    /// Gets the media file to decode, or the empty string on failure.
    /// </summary>
    public string InputPath { get; private init; } = string.Empty;

    /// <summary>
    /// Gets the id of the media source that was chosen.
    /// </summary>
    /// <remarks>
    /// Echoed back so a client that did not name one can pin its later requests
    /// - the signal cache key and the subtitle track it picks have to describe
    /// the same source this audio came from.
    /// </remarks>
    public string? MediaSourceId { get; private init; }

    /// <summary>
    /// Gets the chosen audio stream's <c>MediaStream.Index</c>, as Jellyfin
    /// numbers it.
    /// </summary>
    /// <remarks>
    /// Always resolved to a concrete stream rather than left to ffmpeg's default
    /// selection, so the track that was measured is the track that can be named
    /// in a cache key or a log line. This is the number every Jellyfin client
    /// already holds, so it is what the endpoint accepts and echoes back - but it
    /// is <b>not</b> what ffmpeg is given. See
    /// <see cref="ContainerAudioStreamIndex"/>.
    /// </remarks>
    public int AudioStreamIndex { get; private init; }

    /// <summary>
    /// Gets the index of the same stream within the media container, which is
    /// what <c>ffmpeg -map 0:N</c> needs.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>These two numbers are not always equal, and assuming they are produces
    /// silence.</b> Jellyfin numbers a media source's streams across the
    /// container <i>and</i> any external files it found beside it. The harness
    /// fixture is a two-stream mp4 - video 0, audio 1 - with a sibling
    /// <c>.en.srt</c>, and Jellyfin reports that external subtitle as index 0,
    /// video as 1 and audio as 2. Handing ffmpeg <c>-map 0:2</c> for that file
    /// fails with "Stream map '' matches no streams", because the container has
    /// no stream 2.
    /// </para>
    /// <para>
    /// The container index is therefore derived: it is the stream's position
    /// among the source's non-external streams, in index order, which is the
    /// order they were probed in.
    /// </para>
    /// </remarks>
    public int ContainerAudioStreamIndex { get; private init; }

    /// <summary>
    /// Gets an approximate size of the PCM this will produce, in bytes, or null
    /// when the runtime is unknown.
    /// </summary>
    /// <remarks>
    /// <b>Approximate, and not a <c>Content-Length</c>.</b> It is derived from the
    /// container runtime, which is not the audio stream's duration: a fixture
    /// reporting 30.000000 s decodes to 960006 bytes rather than the 960000 the
    /// arithmetic predicts, because the audio actually runs 30.000188 s. Sent as
    /// an <c>X-</c> header purely so a progress bar can move; a real
    /// <c>Content-Length</c> built from this would disagree with the body.
    /// </remarks>
    public long? EstimatedByteLength { get; private init; }

    /// <summary>
    /// Builds a usable plan.
    /// </summary>
    /// <param name="inputPath">The media file to decode.</param>
    /// <param name="mediaSourceId">The chosen source's id.</param>
    /// <param name="audioStreamIndex">The chosen audio stream's Jellyfin index.</param>
    /// <param name="containerAudioStreamIndex">The same stream's index inside the container.</param>
    /// <param name="estimatedByteLength">Approximate output size in bytes, if known.</param>
    /// <returns>The plan.</returns>
    internal static PcmStreamPlan Success(
        string inputPath,
        string? mediaSourceId,
        int audioStreamIndex,
        int containerAudioStreamIndex,
        long? estimatedByteLength)
        => new()
        {
            InputPath = inputPath,
            MediaSourceId = mediaSourceId,
            AudioStreamIndex = audioStreamIndex,
            ContainerAudioStreamIndex = containerAudioStreamIndex,
            EstimatedByteLength = estimatedByteLength,
        };

    /// <summary>
    /// Builds a failed plan.
    /// </summary>
    /// <param name="failure">Why it failed.</param>
    /// <param name="message">What the client should be told.</param>
    /// <returns>The plan.</returns>
    internal static PcmStreamPlan Failed(PcmStreamPlanFailure failure, string message)
        => new()
        {
            Failure = failure,
            ErrorMessage = message,
        };
}
