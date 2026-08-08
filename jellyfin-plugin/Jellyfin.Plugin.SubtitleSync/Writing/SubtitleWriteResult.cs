namespace Jellyfin.Plugin.SubtitleSync.Writing;

/// <summary>
/// What happened when a synced subtitle was written.
/// </summary>
public sealed class SubtitleWriteResult
{
    private SubtitleWriteResult()
    {
    }

    /// <summary>
    /// Gets a value indicating whether the file is on disk.
    /// </summary>
    public bool Succeeded => Failure == SubtitleWriteFailure.None;

    /// <summary>
    /// Gets the path actually written, or the empty string on failure.
    /// </summary>
    /// <remarks>
    /// Not necessarily the path the caller would have predicted: collision
    /// handling appends a numeric suffix, and a name lost to a concurrent save
    /// is resolved again. This is the value the response reports.
    /// </remarks>
    public string OutputPath { get; private init; } = string.Empty;

    /// <summary>
    /// Gets the normalised language segment used in the name, if any.
    /// </summary>
    public string? Language { get; private init; }

    /// <summary>
    /// Gets a value indicating whether an existing subtitle file was replaced.
    /// </summary>
    public bool OverwroteSource { get; private init; }

    /// <summary>
    /// Gets how many bytes were written.
    /// </summary>
    public long BytesWritten { get; private init; }

    /// <summary>
    /// Gets the failure reason, or <see cref="SubtitleWriteFailure.None"/>.
    /// </summary>
    public SubtitleWriteFailure Failure { get; private init; }

    /// <summary>
    /// Gets an explanation an administrator can act on, or null on success.
    /// </summary>
    public string? ErrorMessage { get; private init; }

    /// <summary>
    /// Builds a successful result.
    /// </summary>
    /// <param name="outputPath">The path written.</param>
    /// <param name="language">The language segment used.</param>
    /// <param name="overwroteSource">Whether an existing file was replaced.</param>
    /// <param name="bytesWritten">The file size.</param>
    /// <returns>The result.</returns>
    internal static SubtitleWriteResult Success(string outputPath, string? language, bool overwroteSource, long bytesWritten)
        => new()
        {
            OutputPath = outputPath,
            Language = language,
            OverwroteSource = overwroteSource,
            BytesWritten = bytesWritten,
        };

    /// <summary>
    /// Builds a failed result.
    /// </summary>
    /// <param name="failure">Why it failed.</param>
    /// <param name="message">What the administrator should do about it.</param>
    /// <returns>The result.</returns>
    internal static SubtitleWriteResult Failed(SubtitleWriteFailure failure, string message)
        => new() { Failure = failure, ErrorMessage = message };
}
