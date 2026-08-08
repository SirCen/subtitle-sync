using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.SubtitleSync.Paths;
using Xunit;

namespace Jellyfin.Plugin.SubtitleSync.Tests.Paths;

/// <summary>
/// Every test for the one component of this plugin that can destroy a user's
/// data. The resolver decides which file a synced subtitle is written to; a
/// wrong answer here overwrites something irreplaceable.
/// </summary>
public class SubtitlePathResolverTests
{
    private const string MediaFolder = "/media/Movies";
    private const string MoviePath = MediaFolder + "/Movie.mkv";

    // -----------------------------------------------------------------------
    // Default naming
    // -----------------------------------------------------------------------

    /// <summary>
    /// The headline case from issue #4.
    /// </summary>
    [Fact]
    public void MovieWithEnglishGetsLanguageAndSyncedMarker()
    {
        var result = Resolve(MoviePath, language: "en");

        Assert.True(result.Succeeded);
        Assert.Equal("Movie.en.synced.srt", FileName(result));
        Assert.False(result.OverwritesSource);
    }

    /// <summary>
    /// The output always sits next to the media file, because Jellyfin's stage 1
    /// candidate filter only looks in the video's containing folder.
    /// </summary>
    [Fact]
    public void OutputSitsInTheMediaFolder()
    {
        var result = Resolve(MoviePath, language: "en");

        Assert.Equal(Path.GetDirectoryName(MoviePath), Path.GetDirectoryName(result.OutputPath));
    }

    /// <summary>
    /// Separators in the input are preserved rather than rewritten to the host
    /// platform's, so a POSIX container path survives a Windows build.
    /// </summary>
    [Fact]
    public void OriginalPathSeparatorsArePreserved()
    {
        var result = Resolve(MoviePath, language: "en");

        Assert.Equal("/media/Movies/Movie.en.synced.srt", result.OutputPath);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("und")]
    public void MissingOrUndeterminedLanguageIsOmitted(string? language)
    {
        var result = Resolve(MoviePath, language);

        Assert.True(result.Succeeded);
        Assert.Equal("Movie.synced.srt", FileName(result));
        Assert.Null(result.Language);
    }

    [Theory]
    [InlineData("EN", "Movie.en.synced.srt")]
    [InlineData("  eng  ", "Movie.eng.synced.srt")]
    [InlineData("en\n", "Movie.en.synced.srt")]
    [InlineData("en\t", "Movie.en.synced.srt")]
    [InlineData("Zh-Hans", "Movie.zh-hans.synced.srt")]
    public void LanguageIsNormalisedToTrimmedLowercase(string language, string expected)
    {
        Assert.Equal(expected, FileName(Resolve(MoviePath, language)));
    }

    /// <summary>
    /// Media files with no extension still resolve; the whole name is the stem.
    /// </summary>
    [Fact]
    public void MediaFileWithoutAnExtensionUsesTheWholeName()
    {
        Assert.Equal(
            "Movie.en.synced.srt",
            FileName(Resolve(MediaFolder + "/Movie", "en")));
    }

    /// <summary>
    /// Episode names carry dots, brackets and dashes. Jellyfin matches the stem
    /// verbatim, so every one of those characters has to survive untouched.
    /// </summary>
    [Theory]
    [InlineData("Show.S01E02.1080p.WEB-DL [x265].mkv", "Show.S01E02.1080p.WEB-DL [x265].eng.synced.srt")]
    [InlineData("The Movie (2019) - [Bluray-1080p].mkv", "The Movie (2019) - [Bluray-1080p].eng.synced.srt")]
    [InlineData("Show - S01E02 - Pilot.mp4", "Show - S01E02 - Pilot.eng.synced.srt")]
    public void ComplexMediaFileNamesRoundTrip(string mediaFileName, string expected)
    {
        Assert.Equal(expected, FileName(Resolve(MediaFolder + "/" + mediaFileName, "eng")));
    }

    /// <summary>
    /// Non-ASCII stems are copied through byte for byte; anything else breaks
    /// the case-insensitive prefix match in stage 1.
    /// </summary>
    [Theory]
    [InlineData("Фильм.mkv", "Фильм.ru.synced.srt")]
    [InlineData("映画.mkv", "映画.ru.synced.srt")]
    [InlineData("Cafe\u0301 Society.mkv", "Cafe\u0301 Society.ru.synced.srt")]
    public void UnicodeMediaNamesArePreserved(string mediaFileName, string expected)
    {
        Assert.Equal(expected, FileName(Resolve(MediaFolder + "/" + mediaFileName, "ru")));
    }

    // -----------------------------------------------------------------------
    // Collisions
    // -----------------------------------------------------------------------

    [Fact]
    public void ExistingTargetGetsASuffixInsteadOfBeingClobbered()
    {
        var result = Resolve(MoviePath, "en", existing: [MediaFolder + "/Movie.en.synced.srt"]);

        Assert.Equal("Movie.en.synced.2.srt", FileName(result));
    }

    [Fact]
    public void SuffixCountsUpPastEveryExistingFile()
    {
        var result = Resolve(
            MoviePath,
            "en",
            existing:
            [
                MediaFolder + "/Movie.en.synced.srt",
                MediaFolder + "/Movie.en.synced.2.srt",
            ]);

        Assert.Equal("Movie.en.synced.3.srt", FileName(result));
    }

    [Fact]
    public void UnknownLanguageCollisionsAlsoCountUp()
    {
        var result = Resolve(MoviePath, null, existing: [MediaFolder + "/Movie.synced.srt"]);

        Assert.Equal("Movie.synced.2.srt", FileName(result));
    }

    /// <summary>
    /// Re-syncing a file that was itself produced by a previous sync must not
    /// return the source path when overwrite is off.
    /// </summary>
    [Fact]
    public void ResyncingAnAlreadySyncedTrackNeverReturnsTheSourcePath()
    {
        var source = MediaFolder + "/Movie.en.synced.srt";

        var result = Resolve(MoviePath, "en", existing: [source], source: SubtitleSource.External(source));

        Assert.NotEqual(source, result.OutputPath);
        Assert.Equal("Movie.en.synced.2.srt", FileName(result));
    }

    /// <summary>
    /// A media file whose own name contains <c>.synced.</c> is not special; the
    /// marker is appended to the stem as given.
    /// </summary>
    [Fact]
    public void MediaNameContainingTheMarkerIsNotDeduplicated()
    {
        Assert.Equal(
            "Movie.synced.en.synced.srt",
            FileName(Resolve(MediaFolder + "/Movie.synced.mkv", "en")));
    }

    /// <summary>
    /// A runaway loop over an unreadable folder must stop, not spin forever.
    /// </summary>
    [Fact]
    public void ExhaustedSuffixesFailRatherThanLoopForever()
    {
        var result = new SubtitlePathResolver(_ => true, _ => true).Resolve(
            new SubtitleOutputRequest
            {
                MediaPath = MoviePath,
                Language = "en",
                Source = SubtitleSource.Embedded(),
            });

        Assert.False(result.Succeeded);
        Assert.Equal(SubtitlePathFailure.NoAvailableName, result.Failure);
        Assert.Contains("Movie.en.synced", result.ErrorMessage, StringComparison.Ordinal);
    }

    // -----------------------------------------------------------------------
    // Overwrite mode
    // -----------------------------------------------------------------------

    [Fact]
    public void OverwriteWithAnExternalSourceReturnsTheSourcePathUnchanged()
    {
        var source = MediaFolder + "/Movie.en.srt";

        var result = Resolve(
            MoviePath,
            "en",
            existing: [source],
            source: SubtitleSource.External(source),
            overwrite: true);

        Assert.True(result.Succeeded);
        Assert.Equal(source, result.OutputPath);
        Assert.True(result.OverwritesSource);
    }

    /// <summary>
    /// An embedded track has no file of its own. Overwriting it would mean
    /// rewriting the container, which this plugin never does.
    /// </summary>
    [Fact]
    public void OverwriteWithAnEmbeddedSourceStillWritesANewSibling()
    {
        var result = Resolve(MoviePath, "en", source: SubtitleSource.Embedded(), overwrite: true);

        Assert.True(result.Succeeded);
        Assert.Equal("Movie.en.synced.srt", FileName(result));
        Assert.False(result.OverwritesSource);
    }

    /// <summary>
    /// Overwrite mode must never create the source file it thinks it is
    /// replacing. If the path has gone, fall back to a fresh sibling.
    /// </summary>
    [Fact]
    public void OverwriteFallsBackWhenTheSourceFileNoLongerExists()
    {
        var result = Resolve(
            MoviePath,
            "en",
            existing: [],
            source: SubtitleSource.External(MediaFolder + "/Movie.en.srt"),
            overwrite: true);

        Assert.Equal("Movie.en.synced.srt", FileName(result));
        Assert.False(result.OverwritesSource);
    }

    /// <summary>
    /// We only ever write SRT bytes. Writing them over a .ass or .sub file
    /// would silently corrupt a track Jellyfin still parses by extension.
    /// </summary>
    [Theory]
    [InlineData("Movie.en.ass")]
    [InlineData("Movie.en.sub")]
    [InlineData("Movie.en.vtt")]
    public void OverwriteFallsBackWhenTheSourceIsNotSrt(string sourceFileName)
    {
        var source = MediaFolder + "/" + sourceFileName;

        var result = Resolve(
            MoviePath,
            "en",
            existing: [source],
            source: SubtitleSource.External(source),
            overwrite: true);

        Assert.Equal("Movie.en.synced.srt", FileName(result));
        Assert.False(result.OverwritesSource);
    }

    [Fact]
    public void OverwriteIsCaseInsensitiveAboutTheSrtExtension()
    {
        var source = MediaFolder + "/Movie.en.SRT";

        var result = Resolve(
            MoviePath,
            "en",
            existing: [source],
            source: SubtitleSource.External(source),
            overwrite: true);

        Assert.Equal(source, result.OutputPath);
        Assert.True(result.OverwritesSource);
    }

    /// <summary>
    /// Overwrite mode is off by default, so an external source is left alone.
    /// </summary>
    [Fact]
    public void WithoutOverwriteAnExternalSourceIsLeftAlone()
    {
        var source = MediaFolder + "/Movie.en.srt";

        var result = Resolve(MoviePath, "en", existing: [source], source: SubtitleSource.External(source));

        Assert.Equal("Movie.en.synced.srt", FileName(result));
        Assert.False(result.OverwritesSource);
    }

    // -----------------------------------------------------------------------
    // Hostile language codes
    // -----------------------------------------------------------------------

    /// <summary>
    /// A language code arrives from file metadata, which is attacker-controlled
    /// in any library built from downloaded files. Anything that is not a plain
    /// BCP 47 style tag is dropped rather than pasted into a path.
    /// </summary>
    [Theory]
    [InlineData("../../../etc")]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("../en")]
    [InlineData("..\\..\\Windows")]
    [InlineData("en/us")]
    [InlineData("en\\us")]
    [InlineData("en.us")]
    [InlineData("/en")]
    [InlineData("C:")]
    [InlineData("C:/Windows/System32")]
    [InlineData("\\\\server\\share")]
    [InlineData("en\0")]
    [InlineData("en:us")]
    [InlineData("en*")]
    [InlineData("en?")]
    [InlineData("~")]
    [InlineData("e")]
    [InlineData("engl")]
    [InlineData("русский")]
    [InlineData("$(rm -rf)")]
    [InlineData("en-")]
    [InlineData("-en")]
    [InlineData("en--us")]
    public void HostileLanguageCodesAreDroppedNotPasted(string language)
    {
        var result = Resolve(MoviePath, language);

        Assert.True(result.Succeeded);
        Assert.Equal("Movie.synced.srt", FileName(result));
        Assert.Null(result.Language);
    }

    /// <summary>
    /// The belt-and-braces version of the above: whatever the language, the
    /// resolved path must stay inside the media folder.
    /// </summary>
    [Theory]
    [InlineData("../../../etc")]
    [InlineData("en/us")]
    [InlineData("C:/Windows/System32")]
    [InlineData("..")]
    public void HostileLanguageCodesCannotEscapeTheMediaFolder(string language)
    {
        var result = Resolve(MoviePath, language);

        Assert.Equal(Path.GetDirectoryName(MoviePath), Path.GetDirectoryName(result.OutputPath));
        Assert.Equal(
            Path.GetFullPath(MediaFolder),
            Path.GetDirectoryName(Path.GetFullPath(result.OutputPath)));
    }

    /// <summary>
    /// Jellyfin's segment parser reads <c>cc</c> and <c>sdh</c> as hearing
    /// impaired flags and anything containing <c>forced</c>, <c>foreign</c> or
    /// <c>default</c> as a flag, by substring. None of those are real ISO 639
    /// codes, so they are dropped rather than mislabelling the track.
    /// </summary>
    [Theory]
    [InlineData("cc")]
    [InlineData("CC")]
    [InlineData("sdh")]
    [InlineData("SDH")]
    [InlineData("en-forced")]
    [InlineData("en-default")]
    [InlineData("en-foreign")]
    [InlineData("forced")]
    [InlineData("default")]
    public void LanguageCodesThatCollideWithJellyfinFlagTokensAreDropped(string language)
    {
        var result = Resolve(MoviePath, language);

        Assert.Equal("Movie.synced.srt", FileName(result));
        Assert.Null(result.Language);
    }

    /// <summary>
    /// <c>hi</c> is the one genuine collision: it is both Hindi and a hearing
    /// impaired flag. Jellyfin resolves a right-most bare <c>.hi</c> to Hindi,
    /// and our marker sits to its right anyway, so it is kept.
    /// </summary>
    [Fact]
    public void HindiIsKeptDespiteCollidingWithTheHearingImpairedFlag()
    {
        var result = Resolve(MoviePath, "hi");

        Assert.Equal("Movie.hi.synced.srt", FileName(result));
        Assert.Equal("hi", result.Language);
    }

    // -----------------------------------------------------------------------
    // Failure modes
    // -----------------------------------------------------------------------

    /// <summary>
    /// A read-only media mount is the common Docker misconfiguration. It has to
    /// say so, naming the folder, rather than failing at write time or not at
    /// all.
    /// </summary>
    [Fact]
    public void ReadOnlyMediaFolderFailsWithAnActionableMessage()
    {
        var resolver = new SubtitlePathResolver(_ => false, _ => false);

        var result = resolver.Resolve(new SubtitleOutputRequest
        {
            MediaPath = MoviePath,
            Language = "en",
            Source = SubtitleSource.Embedded(),
        });

        Assert.False(result.Succeeded);
        Assert.Equal(SubtitlePathFailure.MediaFolderNotWritable, result.Failure);
        Assert.Contains(MediaFolder, result.ErrorMessage, StringComparison.Ordinal);
        Assert.Contains("read-only", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(string.Empty, result.OutputPath);
    }

    /// <summary>
    /// Overwrite mode writes into the source file's folder, which is not
    /// necessarily the media folder, so that is the folder to test.
    /// </summary>
    [Fact]
    public void OverwriteChecksWritabilityOfTheFolderItWillActuallyWriteTo()
    {
        var probed = new List<string>();
        var source = "/subs/Movie.en.srt";

        var resolver = new SubtitlePathResolver(
            path => string.Equals(path, source, StringComparison.Ordinal),
            path =>
            {
                probed.Add(path);
                return true;
            });

        resolver.Resolve(new SubtitleOutputRequest
        {
            MediaPath = MoviePath,
            Language = "en",
            Source = SubtitleSource.External(source),
            OverwriteOriginal = true,
        });

        Assert.Equal(["/subs"], probed);
    }

    [Theory]
    [InlineData("Movie.mkv")]
    [InlineData("")]
    [InlineData("   ")]
    public void AMediaPathWithNoFolderIsRejected(string mediaPath)
    {
        var result = Resolve(mediaPath, "en");

        Assert.False(result.Succeeded);
        Assert.Equal(SubtitlePathFailure.InvalidMediaPath, result.Failure);
        Assert.Equal(string.Empty, result.OutputPath);
    }

    /// <summary>
    /// A stem close to the 255 byte limit most filesystems impose would produce
    /// a name that cannot be created. Say so instead of silently truncating,
    /// which would land the output on top of a different file.
    /// </summary>
    [Fact]
    public void AFileNameOverTheFilesystemLimitFails()
    {
        var stem = new string('a', 250);

        var result = Resolve(MediaFolder + "/" + stem + ".mkv", "en");

        Assert.False(result.Succeeded);
        Assert.Equal(SubtitlePathFailure.NameTooLong, result.Failure);
    }

    /// <summary>
    /// Just under the limit still works, and is not shortened.
    /// </summary>
    [Fact]
    public void AFileNameJustUnderTheLimitIsReturnedIntact()
    {
        // stem + ".en.synced.srt" is 14 characters.
        var stem = new string('a', 241);

        var result = Resolve(MediaFolder + "/" + stem + ".mkv", "en");

        Assert.True(result.Succeeded);
        Assert.Equal(255, FileName(result).Length);
        Assert.StartsWith(stem, FileName(result), StringComparison.Ordinal);
    }

    /// <summary>
    /// A path longer than the classic Windows MAX_PATH is a write-time concern,
    /// not a naming one. The resolver must not quietly mangle it.
    /// </summary>
    [Fact]
    public void ADeeplyNestedPathIsNotTruncated()
    {
        var deep = "/media/" + string.Join('/', Enumerable.Repeat("subfolder", 40));

        var result = Resolve(deep + "/Movie.mkv", "en");

        Assert.True(result.Succeeded);
        Assert.True(result.OutputPath.Length > 260);
        Assert.Equal(deep + "/Movie.en.synced.srt", result.OutputPath);
    }

    [Fact]
    public void ANullRequestIsRejected()
    {
        var resolver = new SubtitlePathResolver(_ => false, _ => true);

        Assert.Throws<ArgumentNullException>(() => resolver.Resolve(null!));
    }

    [Fact]
    public void ANullExternalSourcePathIsRejected()
    {
        Assert.Throws<ArgumentException>(() => SubtitleSource.External("  "));
    }

    // -----------------------------------------------------------------------
    // Purity
    // -----------------------------------------------------------------------

    /// <summary>
    /// The resolver decides, it does not act. Nothing it produces may exist on
    /// disk as a side effect, and it must only ever ask about paths it is
    /// actually considering.
    /// </summary>
    [Fact]
    public void OnlyCandidatePathsAreProbedForExistence()
    {
        var probed = new List<string>();

        var resolver = new SubtitlePathResolver(
            path =>
            {
                probed.Add(path);
                return probed.Count <= 2;
            },
            _ => true);

        var result = resolver.Resolve(new SubtitleOutputRequest
        {
            MediaPath = MoviePath,
            Language = "en",
            Source = SubtitleSource.Embedded(),
        });

        Assert.Equal(
            [
                "/media/Movies/Movie.en.synced.srt",
                "/media/Movies/Movie.en.synced.2.srt",
                "/media/Movies/Movie.en.synced.3.srt",
            ],
            probed);
        Assert.Equal("/media/Movies/Movie.en.synced.3.srt", result.OutputPath);
    }

    /// <summary>
    /// The same request twice gives the same answer.
    /// </summary>
    [Fact]
    public void ResolutionIsDeterministic()
    {
        var resolver = new SubtitlePathResolver(_ => false, _ => true);
        var request = new SubtitleOutputRequest
        {
            MediaPath = MoviePath,
            Language = "en",
            Source = SubtitleSource.Embedded(),
        };

        Assert.Equal(resolver.Resolve(request).OutputPath, resolver.Resolve(request).OutputPath);
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static string FileName(SubtitlePathResolution result)
        => Path.GetFileName(result.OutputPath);

    private static SubtitlePathResolution Resolve(
        string mediaPath,
        string? language,
        IReadOnlyCollection<string>? existing = null,
        SubtitleSource? source = null,
        bool overwrite = false)
    {
        var files = existing ?? [];

        var resolver = new SubtitlePathResolver(
            path => files.Contains(path, StringComparer.Ordinal),
            _ => true);

        return resolver.Resolve(new SubtitleOutputRequest
        {
            MediaPath = mediaPath,
            Language = language,
            Source = source ?? SubtitleSource.Embedded(),
            OverwriteOriginal = overwrite,
        });
    }
}
