namespace Jellyfin.Plugin.SubtitleSync.Subtitles;

/// <summary>
/// The verdict on a posted subtitle document.
/// </summary>
public sealed class SrtValidation
{
    private SrtValidation()
    {
    }

    /// <summary>
    /// Gets a value indicating whether the document is SRT the plugin will write.
    /// </summary>
    public bool IsValid => Error == SrtValidationError.None;

    /// <summary>
    /// Gets the failure reason, or <see cref="SrtValidationError.None"/>.
    /// </summary>
    public SrtValidationError Error { get; private init; }

    /// <summary>
    /// Gets an explanation an administrator can act on, or null on success.
    /// </summary>
    /// <remarks>
    /// Never quotes the rejected content. The message ends up in the server log,
    /// and the content is attacker-controlled text from a subtitle file.
    /// </remarks>
    public string? ErrorMessage { get; private init; }

    /// <summary>
    /// Gets how many cues were parsed, or zero on failure.
    /// </summary>
    public int CueCount { get; private init; }

    /// <summary>
    /// Gets the exact text that should be written to disk, or the empty string on
    /// failure. Line endings are CRLF, cue indices are renumbered from one, and
    /// the millisecond separator is a comma.
    /// </summary>
    public string NormalisedText { get; private init; } = string.Empty;

    /// <summary>
    /// Builds a successful validation.
    /// </summary>
    /// <param name="normalisedText">The text to write.</param>
    /// <param name="cueCount">How many cues it holds.</param>
    /// <returns>The validation.</returns>
    internal static SrtValidation Valid(string normalisedText, int cueCount)
        => new() { NormalisedText = normalisedText, CueCount = cueCount };

    /// <summary>
    /// Builds a refusal.
    /// </summary>
    /// <param name="error">Why it was refused.</param>
    /// <param name="message">What the administrator should do about it.</param>
    /// <returns>The validation.</returns>
    internal static SrtValidation Invalid(SrtValidationError error, string message)
        => new() { Error = error, ErrorMessage = message };
}
