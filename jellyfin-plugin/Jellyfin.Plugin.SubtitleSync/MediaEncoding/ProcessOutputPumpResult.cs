namespace Jellyfin.Plugin.SubtitleSync.MediaEncoding;

/// <summary>
/// What draining a child process's two output pipes produced.
/// </summary>
public sealed record ProcessOutputPumpResult
{
    /// <summary>
    /// Gets the number of bytes copied from standard output to the destination.
    /// </summary>
    public required long BytesCopied { get; init; }

    /// <summary>
    /// Gets the tail of standard error, decoded as UTF-8.
    /// </summary>
    /// <remarks>
    /// The tail rather than the whole thing: with <c>-progress pipe:2</c> ffmpeg
    /// writes a block every second, so an hour-long extraction produces hundreds
    /// of kilobytes of stderr that nobody will ever read. The last few kilobytes
    /// are where the failure is, so that is what is kept.
    /// </remarks>
    public required string DiagnosticTail { get; init; }
}
