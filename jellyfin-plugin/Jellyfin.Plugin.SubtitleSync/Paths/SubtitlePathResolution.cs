namespace Jellyfin.Plugin.SubtitleSync.Paths;

/// <summary>
/// The answer to "where does this synced subtitle go?".
/// </summary>
public sealed class SubtitlePathResolution
{
    private SubtitlePathResolution()
    {
    }

    /// <summary>
    /// Gets a value indicating whether a path was produced.
    /// </summary>
    public bool Succeeded => Failure == SubtitlePathFailure.None;

    /// <summary>
    /// Gets the path to write to, or the empty string on failure.
    /// </summary>
    public string OutputPath { get; private init; } = string.Empty;

    /// <summary>
    /// Gets a value indicating whether <see cref="OutputPath"/> is an existing
    /// file that will be replaced. Only ever true for an external source with
    /// the overwrite setting on; a caller that refuses to destroy data can stop
    /// here.
    /// </summary>
    public bool OverwritesSource { get; private init; }

    /// <summary>
    /// Gets the normalised language actually used in the name, or
    /// <see langword="null"/> when none was usable.
    /// </summary>
    public string? Language { get; private init; }

    /// <summary>
    /// Gets the failure reason, or <see cref="SubtitlePathFailure.None"/>.
    /// </summary>
    public SubtitlePathFailure Failure { get; private init; }

    /// <summary>
    /// Gets a message suitable for showing to an administrator, or null on
    /// success.
    /// </summary>
    public string? ErrorMessage { get; private init; }

    /// <summary>
    /// Builds a successful resolution.
    /// </summary>
    /// <param name="outputPath">The path to write to.</param>
    /// <param name="language">The normalised language used, if any.</param>
    /// <param name="overwritesSource">Whether the path is the source file itself.</param>
    /// <returns>The resolution.</returns>
    internal static SubtitlePathResolution Success(string outputPath, string? language, bool overwritesSource)
        => new()
        {
            OutputPath = outputPath,
            Language = language,
            OverwritesSource = overwritesSource,
        };

    /// <summary>
    /// Builds a failed resolution.
    /// </summary>
    /// <param name="failure">Why it failed.</param>
    /// <param name="message">What the administrator should do about it.</param>
    /// <returns>The resolution.</returns>
    internal static SubtitlePathResolution Failed(SubtitlePathFailure failure, string message)
        => new()
        {
            Failure = failure,
            ErrorMessage = message,
        };
}
