using System;

namespace Jellyfin.Plugin.SubtitleSync.Paths;

/// <summary>
/// Where the subtitle text being synced came from.
/// </summary>
/// <remarks>
/// Modelled as two explicit constructions rather than a nullable path plus a
/// boolean, because "embedded" and "external with an unknown path" must never be
/// confusable: the overwrite setting reads this to decide whether a user's file
/// is about to be replaced.
/// </remarks>
public sealed class SubtitleSource
{
    private SubtitleSource(string? filePath)
    {
        FilePath = filePath;
    }

    /// <summary>
    /// Gets the full path of the external subtitle file, or <see langword="null"/>
    /// when the track is embedded in the media container.
    /// </summary>
    public string? FilePath { get; }

    /// <summary>
    /// Gets a value indicating whether the track is a file of its own.
    /// </summary>
    public bool IsExternal => FilePath is not null;

    /// <summary>
    /// A track carried inside the media container. It has no file of its own and
    /// can never be overwritten.
    /// </summary>
    /// <returns>An embedded source.</returns>
    public static SubtitleSource Embedded() => new(null);

    /// <summary>
    /// A sidecar subtitle file.
    /// </summary>
    /// <param name="filePath">Full path to the existing subtitle file.</param>
    /// <returns>An external source.</returns>
    /// <exception cref="ArgumentException">The path is null, empty or whitespace.</exception>
    public static SubtitleSource External(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ArgumentException(
                "An external subtitle source needs the full path of its file.",
                nameof(filePath));
        }

        return new SubtitleSource(filePath);
    }
}
