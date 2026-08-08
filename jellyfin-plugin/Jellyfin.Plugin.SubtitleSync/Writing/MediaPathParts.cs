using System.IO;

namespace Jellyfin.Plugin.SubtitleSync.Writing;

/// <summary>
/// Splits and rejoins library paths without rewriting their separators.
/// </summary>
/// <remarks>
/// The same reasoning as <c>SubtitlePathResolver.TrySplit</c>, and deliberately
/// a copy rather than a reach into that class: the resolver is the reviewed,
/// heavily tested component that decides where data goes, and it stays pure and
/// closed. This is the write side's own, much smaller, need.
/// <para>
/// <see cref="Path.GetDirectoryName(string)"/> plus
/// <see cref="Path.Combine(string, string)"/> would turn a container's
/// <c>/media/Movies</c> into <c>\media\Movies</c> on a Windows build, which
/// would then not match the folder the resolver produced.
/// </para>
/// </remarks>
public static class MediaPathParts
{
    /// <summary>
    /// Splits a path into its folder, the separator actually used, and its file
    /// name.
    /// </summary>
    /// <param name="path">The path to split.</param>
    /// <param name="folder">The containing folder, possibly empty for a root-level file.</param>
    /// <param name="separator">The separator character found.</param>
    /// <param name="fileName">The file name.</param>
    /// <returns>False when the path is empty or names no folder.</returns>
    public static bool TrySplit(string? path, out string folder, out char separator, out string fileName)
    {
        folder = string.Empty;
        separator = Path.DirectorySeparatorChar;
        fileName = string.Empty;

        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var index = -1;
        for (var i = path.Length - 1; i >= 0; i--)
        {
            if (path[i] == Path.DirectorySeparatorChar || path[i] == Path.AltDirectorySeparatorChar)
            {
                index = i;
                break;
            }
        }

        if (index < 0 || index == path.Length - 1)
        {
            return false;
        }

        folder = path[..index];
        separator = path[index];
        fileName = path[(index + 1)..];
        return true;
    }

    /// <summary>
    /// Appends a file name to a folder, guessing the separator from the folder
    /// itself.
    /// </summary>
    /// <remarks>
    /// Used for the temporary and probe files, where only the folder is in hand.
    /// A folder that carries no separator at all is a root-level name, so the
    /// host's separator is the only sensible answer.
    /// </remarks>
    /// <param name="folder">The containing folder.</param>
    /// <param name="fileName">The file name to append.</param>
    /// <returns>The full path.</returns>
    public static string Join(string folder, string fileName)
    {
        if (string.IsNullOrEmpty(folder))
        {
            return fileName;
        }

        var separator = Path.DirectorySeparatorChar;
        for (var i = folder.Length - 1; i >= 0; i--)
        {
            if (folder[i] == Path.DirectorySeparatorChar || folder[i] == Path.AltDirectorySeparatorChar)
            {
                separator = folder[i];
                break;
            }
        }

        return folder[^1] == separator ? folder + fileName : folder + separator + fileName;
    }
}
