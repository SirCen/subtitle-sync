namespace Jellyfin.Plugin.SubtitleSync.Api;

/// <summary>
/// One audio track. The page shows these so the user can pick which one the
/// speech signal is extracted from.
/// </summary>
public sealed class AudioStreamResponse
{
    /// <summary>
    /// Gets the stream index within its media source.
    /// </summary>
    public int Index { get; init; }

    /// <summary>
    /// Gets the track language, or null when the container did not say.
    /// </summary>
    public string? Language { get; init; }

    /// <summary>
    /// Gets the codec name as reported by ffprobe.
    /// </summary>
    public string? Codec { get; init; }

    /// <summary>
    /// Gets the track title, when the container carried one.
    /// </summary>
    public string? Title { get; init; }

    /// <summary>
    /// Gets Jellyfin's own rendering of the track.
    /// </summary>
    public string? DisplayTitle { get; init; }

    /// <summary>
    /// Gets the channel count, when known.
    /// </summary>
    public int? Channels { get; init; }

    /// <summary>
    /// Gets the sample rate in hertz, when known. Informational only: the PCM
    /// endpoint always resamples to 16 kHz mono.
    /// </summary>
    public int? SampleRate { get; init; }

    /// <summary>
    /// Gets a value indicating whether the track is flagged default. This is the
    /// one the page should preselect.
    /// </summary>
    public bool IsDefault { get; init; }
}
