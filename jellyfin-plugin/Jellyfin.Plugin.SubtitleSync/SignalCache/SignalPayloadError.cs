namespace Jellyfin.Plugin.SubtitleSync.SignalCache;

/// <summary>
/// Why a speech-signal payload was refused.
/// </summary>
/// <remarks>
/// Every value here is reachable from two directions: a browser POSTing a
/// malformed body, and a cache file that has been truncated or corrupted on
/// disk. Keeping the reason specific is what lets the API answer a bad request
/// usefully while the store quietly treats the same condition as a miss.
/// </remarks>
public enum SignalPayloadError
{
    /// <summary>
    /// The payload is well formed.
    /// </summary>
    None = 0,

    /// <summary>
    /// Shorter than the fixed header, so not even the sample count can be read.
    /// </summary>
    TooShort,

    /// <summary>
    /// The leading magic is not <c>SSC1</c>. This is not one of ours.
    /// </summary>
    BadMagic,

    /// <summary>
    /// The declared sample count is negative or beyond
    /// <see cref="SpeechSignalCodec.MaxSampleCount"/>. Checked from the header
    /// alone, before anything is allocated on its say-so.
    /// </summary>
    SampleCountOutOfRange,

    /// <summary>
    /// The payload is not exactly the length the declared sample count implies.
    /// A truncated upload and a half-written file both land here.
    /// </summary>
    LengthMismatch,

    /// <summary>
    /// The unused bits of the final packed byte are not zero, so the encoding is
    /// not canonical.
    /// </summary>
    PaddingNotZero,

    /// <summary>
    /// The body does not match the checksum in the header. The bytes have been
    /// altered since they were packed.
    /// </summary>
    ChecksumMismatch,
}
