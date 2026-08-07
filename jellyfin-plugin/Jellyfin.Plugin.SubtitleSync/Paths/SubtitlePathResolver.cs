using System;
using System.Globalization;
using System.IO;

namespace Jellyfin.Plugin.SubtitleSync.Paths;

/// <summary>
/// Decides which file a synced subtitle is written to.
/// </summary>
/// <remarks>
/// <para>
/// This is the only part of the plugin that can destroy data, so it is pure: it
/// reads nothing and writes nothing. Existence and writability arrive as
/// injected predicates, which makes every branch testable without a filesystem
/// and keeps the actual write (issue #8) somewhere it can be reviewed on its
/// own.
/// </para>
/// <para>
/// The naming scheme is dictated by Jellyfin 10.11's external subtitle parser,
/// documented in <c>research/jellyfin-10.11-plugin-api.md</c> section 9. A file
/// is only picked up when it sits beside the video and its name starts with the
/// video's name followed by a dot. The remaining dot-separated segments are read
/// right to left and classified by content: flag words, then a resolvable
/// language, then whatever is left becomes the track title. So
/// <c>Movie.en.synced.srt</c> gives language <c>eng</c> and title
/// <c>synced</c>, which the picker shows as
/// <c>synced - English - SRT - External</c>.
/// </para>
/// </remarks>
public sealed class SubtitlePathResolver
{
    /// <summary>
    /// The segment that marks our output. Chosen because Jellyfin's parser reads
    /// it as none of default, forced, foreign, cc, hi or sdh, and it does not
    /// resolve as a language, so it lands in the track title.
    /// </summary>
    private const string Marker = "synced";

    private const string SubtitleExtension = ".srt";

    /// <summary>
    /// The file name limit shared by ext4, APFS, NTFS and every other filesystem
    /// a Jellyfin library realistically lives on.
    /// </summary>
    private const int MaxFileNameLength = 255;

    /// <summary>
    /// How far the collision suffix counts before giving up. A folder with 999
    /// synced copies of one track is a bug, not a use case, and an unbounded
    /// loop against a misbehaving predicate would hang the request.
    /// </summary>
    private const int MaxCollisionSuffix = 999;

    private readonly Func<string, bool> _fileExists;
    private readonly Func<string, bool> _directoryIsWritable;

    /// <summary>
    /// Initializes a new instance of the <see cref="SubtitlePathResolver"/> class.
    /// </summary>
    /// <param name="fileExists">
    /// Answers whether a full path is already taken. Called only for paths the
    /// resolver is actually considering.
    /// </param>
    /// <param name="directoryIsWritable">
    /// Answers whether the folder the output would go in can be written to.
    /// Called once, for that folder only.
    /// </param>
    public SubtitlePathResolver(Func<string, bool> fileExists, Func<string, bool> directoryIsWritable)
    {
        ArgumentNullException.ThrowIfNull(fileExists);
        ArgumentNullException.ThrowIfNull(directoryIsWritable);

        _fileExists = fileExists;
        _directoryIsWritable = directoryIsWritable;
    }

    /// <summary>
    /// Resolves the file a synced subtitle should be written to.
    /// </summary>
    /// <param name="request">The media file, source track, language and overwrite setting.</param>
    /// <returns>
    /// A resolution carrying either a path or an actionable failure. Nothing is
    /// created, and the path is only ever an existing file when
    /// <see cref="SubtitlePathResolution.OverwritesSource"/> is set.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is null.</exception>
    public SubtitlePathResolution Resolve(SubtitleOutputRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Source);

        var language = NormaliseLanguage(request.Language);

        if (!TrySplit(request.MediaPath, out var mediaFolder, out var separator, out var mediaFileName))
        {
            return SubtitlePathResolution.Failed(
                SubtitlePathFailure.InvalidMediaPath,
                FormattableString.Invariant(
                    $"'{request.MediaPath}' is not a usable media file path. A full path including the containing folder is required."));
        }

        var stem = Path.GetFileNameWithoutExtension(mediaFileName);
        if (string.IsNullOrEmpty(stem))
        {
            return SubtitlePathResolution.Failed(
                SubtitlePathFailure.InvalidMediaPath,
                FormattableString.Invariant(
                    $"'{request.MediaPath}' has no file name to derive a subtitle name from."));
        }

        var overwritePath = OverwriteTarget(request);
        if (overwritePath is not null)
        {
            // The source need not live beside the media file, so the folder to
            // test for writability is the source's own.
            if (!TrySplit(overwritePath, out var sourceFolder, out var sourceSeparator, out _))
            {
                return SubtitlePathResolution.Failed(
                    SubtitlePathFailure.InvalidMediaPath,
                    FormattableString.Invariant(
                        $"'{overwritePath}' is not a usable subtitle file path."));
            }

            return CheckWritable(sourceFolder, sourceSeparator)
                ?? SubtitlePathResolution.Success(overwritePath, language, overwritesSource: true);
        }

        var notWritable = CheckWritable(mediaFolder, separator);
        if (notWritable is not null)
        {
            return notWritable;
        }

        var baseName = language is null
            ? FormattableString.Invariant($"{stem}.{Marker}")
            : FormattableString.Invariant($"{stem}.{language}.{Marker}");

        for (var attempt = 1; attempt <= MaxCollisionSuffix; attempt++)
        {
            var fileName = attempt == 1
                ? baseName + SubtitleExtension
                : FormattableString.Invariant($"{baseName}.{attempt}{SubtitleExtension}");

            if (fileName.Length > MaxFileNameLength)
            {
                return SubtitlePathResolution.Failed(
                    SubtitlePathFailure.NameTooLong,
                    FormattableString.Invariant(
                        $"The subtitle file name '{fileName}' is {fileName.Length.ToString(CultureInfo.InvariantCulture)} characters, over the {MaxFileNameLength.ToString(CultureInfo.InvariantCulture)} character limit. Shorten the media file name."));
            }

            var candidate = Join(mediaFolder, separator, fileName);

            if (!_fileExists(candidate))
            {
                return SubtitlePathResolution.Success(candidate, language, overwritesSource: false);
            }
        }

        return SubtitlePathResolution.Failed(
            SubtitlePathFailure.NoAvailableName,
            FormattableString.Invariant(
                $"Every name from '{baseName}{SubtitleExtension}' to '{baseName}.{MaxCollisionSuffix}{SubtitleExtension}' is taken in '{mediaFolder}'. Delete some of them and try again."));
    }

    /// <summary>
    /// Decides whether the request may replace its own source file.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <returns>The source path to overwrite, or null to write a new sibling.</returns>
    private string? OverwriteTarget(SubtitleOutputRequest request)
    {
        if (!request.OverwriteOriginal)
        {
            return null;
        }

        // An embedded track has no file of its own. Replacing it would mean
        // rewriting the container, which this plugin never does.
        var sourcePath = request.Source.FilePath;
        if (sourcePath is null)
        {
            return null;
        }

        // We only ever write SRT. Putting SRT bytes inside Movie.en.ass would
        // corrupt a track Jellyfin still parses by extension.
        if (!string.Equals(Path.GetExtension(sourcePath), SubtitleExtension, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        // Never conjure the "original" into existence. If it has gone, this is
        // not an overwrite and the collision-safe path applies.
        return _fileExists(sourcePath) ? sourcePath : null;
    }

    /// <summary>
    /// Runs the writability predicate and turns a false into an explanation.
    /// </summary>
    /// <param name="folder">The folder that would be written to.</param>
    /// <param name="separator">The separator character seen in the original path.</param>
    /// <returns>A failure, or null when the folder is writable.</returns>
    private SubtitlePathResolution? CheckWritable(string folder, char separator)
    {
        var probed = folder.Length == 0 ? separator.ToString() : folder;

        if (_directoryIsWritable(probed))
        {
            return null;
        }

        return SubtitlePathResolution.Failed(
            SubtitlePathFailure.MediaFolderNotWritable,
            FormattableString.Invariant(
                $"'{probed}' is read-only, so the synced subtitle cannot be saved next to the media file. Mount the library read-write in the container, or check the permissions of the account Jellyfin runs as."));
    }

    /// <summary>
    /// Splits a path into its folder, the separator character actually used, and
    /// its file name.
    /// </summary>
    /// <remarks>
    /// Deliberately not <see cref="Path.GetDirectoryName(string)"/> plus
    /// <see cref="Path.Combine(string, string)"/>: those rewrite separators to
    /// the host platform's, which would turn a container's <c>/media/...</c>
    /// path into a backslash path on a Windows build.
    /// </remarks>
    /// <param name="path">The path to split.</param>
    /// <param name="folder">The containing folder, possibly empty for a root-level file.</param>
    /// <param name="separator">The separator character found.</param>
    /// <param name="fileName">The file name.</param>
    /// <returns>False when the path is empty or names no folder.</returns>
    private static bool TrySplit(string? path, out string folder, out char separator, out string fileName)
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
            // On Linux a backslash is a legal file name character, and
            // AltDirectorySeparatorChar is '/' there, so this stays correct on
            // both platforms.
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
    /// Rejoins a folder and file name using the separator the caller's path used.
    /// </summary>
    /// <param name="folder">The containing folder.</param>
    /// <param name="separator">The separator character to use.</param>
    /// <param name="fileName">The file name.</param>
    /// <returns>The full path.</returns>
    private static string Join(string folder, char separator, string fileName)
        => folder + separator + fileName;

    /// <summary>
    /// Reduces a reported language to something safe to put in a file name, or
    /// drops it.
    /// </summary>
    /// <remarks>
    /// The language comes from container metadata, which in any library built
    /// from downloaded files is attacker-controlled, and it is about to be
    /// concatenated into a path. Only a plain BCP 47 shaped tag survives, so a
    /// separator, a dot, a traversal or a NUL cannot reach the filesystem. Tags
    /// that Jellyfin's own parser would read as a flag are dropped too: none of
    /// them is a real ISO 639 code, and keeping one would mislabel the track.
    /// </remarks>
    /// <param name="raw">The reported language.</param>
    /// <returns>The normalised tag, or null.</returns>
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Globalization",
        "CA1308:Normalize strings to uppercase",
        Justification = "The result is a file name segment, and lowercase language tags are the convention every subtitle tool and Jellyfin itself uses. Comparisons below are ordinal against lowercase literals, so the security concern behind the rule does not apply.")]
    private static string? NormaliseLanguage(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var tag = raw.Trim().ToLowerInvariant();

        if (!IsWellFormedTag(tag))
        {
            return null;
        }

        // "und" is ffprobe's own way of saying it does not know.
        if (string.Equals(tag, "und", StringComparison.Ordinal))
        {
            return null;
        }

        // MediaHearingImpairedFlags, matched by Equals. "hi" is left alone: it
        // is genuinely Hindi, and Jellyfin resolves a right-most bare ".hi" to
        // Hindi rather than to the flag.
        if (string.Equals(tag, "cc", StringComparison.Ordinal)
            || string.Equals(tag, "sdh", StringComparison.Ordinal))
        {
            return null;
        }

        // MediaDefaultFlags and MediaForcedFlags, matched by Contains, so a
        // subtag such as "en-forced" would trip them.
        if (tag.Contains("default", StringComparison.Ordinal)
            || tag.Contains("forced", StringComparison.Ordinal)
            || tag.Contains("foreign", StringComparison.Ordinal))
        {
            return null;
        }

        return tag;
    }

    /// <summary>
    /// Tests a lowercase tag against the shape of a language subtag sequence:
    /// two or three ASCII letters, then any number of one to eight character
    /// alphanumeric subtags.
    /// </summary>
    /// <param name="tag">The lowercase, trimmed tag.</param>
    /// <returns>True when the tag is well formed.</returns>
    private static bool IsWellFormedTag(string tag)
    {
        var subtagLength = 0;
        var isFirstSubtag = true;

        for (var i = 0; i <= tag.Length; i++)
        {
            if (i < tag.Length && tag[i] != '-')
            {
                var c = tag[i];
                var isLetter = c is >= 'a' and <= 'z';
                var isDigit = c is >= '0' and <= '9';

                if (!isLetter && !(isDigit && !isFirstSubtag))
                {
                    return false;
                }

                subtagLength++;
                continue;
            }

            if (isFirstSubtag)
            {
                if (subtagLength is < 2 or > 3)
                {
                    return false;
                }

                isFirstSubtag = false;
            }
            else if (subtagLength is < 1 or > 8)
            {
                return false;
            }

            subtagLength = 0;
        }

        return true;
    }
}
