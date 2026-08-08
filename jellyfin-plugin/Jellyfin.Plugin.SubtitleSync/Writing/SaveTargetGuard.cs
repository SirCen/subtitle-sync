using System;
using System.Diagnostics.CodeAnalysis;
using Jellyfin.Plugin.SubtitleSync.Paths;

namespace Jellyfin.Plugin.SubtitleSync.Writing;

/// <summary>
/// The last check before anything is created: does this path still describe a
/// file inside the item's own media folder?
/// </summary>
/// <remarks>
/// <para>
/// <see cref="SubtitlePathResolver"/> was proven safe in isolation, but it is a
/// pure function over whatever it is handed, and two of its inputs -
/// <see cref="SubtitleOutputRequest.MediaPath"/> and
/// <see cref="SubtitleSource.FilePath"/> - are strings the caller of the resolver
/// supplies. On the overwrite branch the source path is returned verbatim. This
/// re-derives the invariant from the request rather than trusting the answer, so
/// a future wiring mistake that let a request-supplied path reach the resolver
/// fails closed here instead of writing somewhere new.
/// </para>
/// <para>
/// It is pure and total: no filesystem, no symlink resolution. Symlinks are
/// handled by the write itself, which never follows one - a new file is created
/// with <c>CreateNew</c> and published with a rename, and a rename replaces a
/// symlink rather than the thing it points at.
/// </para>
/// </remarks>
public static class SaveTargetGuard
{
    private const string SubtitleExtension = ".srt";

    /// <summary>
    /// Decides whether a resolved path may be written.
    /// </summary>
    /// <param name="request">The request the resolution came from.</param>
    /// <param name="resolution">The resolver's answer.</param>
    /// <param name="reason">Why it was refused, when it was.</param>
    /// <returns>True when the path is safe to write.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> or <paramref name="resolution"/> is null.</exception>
    public static bool IsSafeTarget(
        SubtitleOutputRequest request,
        SubtitlePathResolution resolution,
        [NotNullWhen(false)] out string? reason)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(resolution);

        var output = resolution.OutputPath;

        if (string.IsNullOrWhiteSpace(output))
        {
            reason = "The resolver produced no path.";
            return false;
        }

        if (output.Contains('\0', StringComparison.Ordinal))
        {
            reason = "The resolved path contains a NUL character.";
            return false;
        }

        if (HasTraversalSegment(output))
        {
            reason = "The resolved path walks out of its folder with a '..' segment.";
            return false;
        }

        if (!output.EndsWith(SubtitleExtension, StringComparison.OrdinalIgnoreCase))
        {
            reason = "The resolved path is not a .srt file.";
            return false;
        }

        if (!MediaPathParts.TrySplit(request.MediaPath, out var mediaFolder, out _, out var mediaFileName))
        {
            reason = "The item has no usable media file path.";
            return false;
        }

        if (resolution.OverwritesSource)
        {
            // The only file an overwrite may touch is the exact source the
            // request named. Not "a file in the same folder", not "a file with
            // the same name": that one path.
            if (!string.Equals(output, request.Source?.FilePath, StringComparison.Ordinal))
            {
                reason = "An overwrite may only replace the subtitle file the sync was derived from.";
                return false;
            }

            // Independently of where it lives, it has to be named after this
            // very media file. Jellyfin's own candidate filter for external
            // subtitles requires the name to start with the video's name
            // followed by a media flag delimiter (research section 9), so this
            // holds for every path the server could legitimately have reported
            // as an external track of this item - including one in the item's
            // internal metadata folder rather than beside the video. Anything
            // else did not come from this item and is refused.
            if (!IsNamedAfter(output, mediaFileName))
            {
                reason = "An overwrite target must be named after the item's own media file.";
                return false;
            }

            reason = null;
            return true;
        }

        if (!MediaPathParts.TrySplit(output, out var outputFolder, out _, out _))
        {
            reason = "The resolved path names no containing folder.";
            return false;
        }

        // Ordinal, not a case-insensitive or normalising comparison. Both
        // strings are built from the same media path by the resolver, so on the
        // intended path they are character-for-character equal; anything else is
        // a difference worth failing on, whatever the host filesystem thinks
        // about case.
        if (!string.Equals(outputFolder, mediaFolder, StringComparison.Ordinal))
        {
            reason = "The resolved path is not in the item's own media folder.";
            return false;
        }

        if (string.Equals(output, request.MediaPath, StringComparison.Ordinal))
        {
            reason = "The resolved path is the media file itself.";
            return false;
        }

        reason = null;
        return true;
    }

    /// <summary>
    /// Tests whether a path's file name starts with the media file's name
    /// without its extension, followed by a dot.
    /// </summary>
    /// <param name="output">The resolved output path.</param>
    /// <param name="mediaFileName">The media file's name, with its extension.</param>
    /// <returns>True when the output is named after the media file.</returns>
    private static bool IsNamedAfter(string output, string mediaFileName)
    {
        var stem = System.IO.Path.GetFileNameWithoutExtension(mediaFileName);
        if (string.IsNullOrEmpty(stem))
        {
            return false;
        }

        if (!MediaPathParts.TrySplit(output, out _, out _, out var outputFileName))
        {
            return false;
        }

        return outputFileName.Length > stem.Length
            && outputFileName.StartsWith(stem, StringComparison.OrdinalIgnoreCase)
            && outputFileName[stem.Length] == '.';
    }

    /// <summary>
    /// Tests whether any separator-delimited segment of a path is <c>..</c>.
    /// </summary>
    /// <param name="path">The path.</param>
    /// <returns>True when the path walks upwards.</returns>
    private static bool HasTraversalSegment(string path)
    {
        var start = 0;

        for (var i = 0; i <= path.Length; i++)
        {
            if (i < path.Length && path[i] != '/' && path[i] != '\\')
            {
                continue;
            }

            if (i - start == 2 && path[start] == '.' && path[start + 1] == '.')
            {
                return true;
            }

            start = i + 1;
        }

        return false;
    }
}
