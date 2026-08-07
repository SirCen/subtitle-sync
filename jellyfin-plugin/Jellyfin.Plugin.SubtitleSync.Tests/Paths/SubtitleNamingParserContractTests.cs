using System;
using System.IO;
using Jellyfin.Plugin.SubtitleSync.Paths;
using Xunit;

namespace Jellyfin.Plugin.SubtitleSync.Tests.Paths;

/// <summary>
/// Asserts the resolver's output against a transcription of Jellyfin 10.11's own
/// external-subtitle filename parser, rather than against our beliefs about it.
/// </summary>
/// <remarks>
/// See <c>research/jellyfin-10.11-plugin-api.md</c> section 9. The contract we
/// need: the file is recognised as belonging to the video, the language survives,
/// the marker lands in the title so the picker distinguishes it, and none of the
/// default, forced or hearing impaired flags are tripped by accident.
/// </remarks>
public class SubtitleNamingParserContractTests
{
    private const string MediaFolder = "/media/Movies";

    [Fact]
    public void TheDefaultNameParsesAsSyncedEnglish()
    {
        var parsed = ResolveAndParse("Movie.mkv", "en");

        Assert.True(parsed.IsCandidate);
        Assert.Equal("eng", parsed.Language);
        Assert.Equal("synced", parsed.Title);
        Assert.False(parsed.IsDefault);
        Assert.False(parsed.IsForced);
        Assert.False(parsed.IsHearingImpaired);
        Assert.Equal("synced - English - SRT - External", JellyfinExternalSubtitleParser.DisplayTitle(parsed));
    }

    [Fact]
    public void TheUnknownLanguageNameParsesAsSyncedUnd()
    {
        var parsed = ResolveAndParse("Movie.mkv", null);

        Assert.True(parsed.IsCandidate);
        Assert.Null(parsed.Language);
        Assert.Equal("synced", parsed.Title);
        Assert.Equal("synced - Und - SRT - External", JellyfinExternalSubtitleParser.DisplayTitle(parsed));
    }

    /// <summary>
    /// The collision suffix rides along in the title, which is exactly what
    /// makes two synced attempts distinguishable in the picker.
    /// </summary>
    [Fact]
    public void TheCollisionSuffixLandsInTheTitleAndNotTheFlags()
    {
        var resolver = new SubtitlePathResolver(
            path => path.EndsWith("Movie.en.synced.srt", StringComparison.Ordinal),
            _ => true);

        var output = resolver.Resolve(new SubtitleOutputRequest
        {
            MediaPath = MediaFolder + "/Movie.mkv",
            Language = "en",
            Source = SubtitleSource.Embedded(),
        });

        var parsed = JellyfinExternalSubtitleParser.Parse("Movie", Path.GetFileName(output.OutputPath));

        Assert.Equal("eng", parsed.Language);
        Assert.Equal("synced.2", parsed.Title);
        Assert.False(parsed.IsDefault);
        Assert.False(parsed.IsForced);
        Assert.False(parsed.IsHearingImpaired);
    }

    /// <summary>
    /// Stage 1 is a case-insensitive prefix match plus a delimiter check, so any
    /// media stem we might meet has to still yield a candidate.
    /// </summary>
    [Theory]
    [InlineData("Movie.mkv", "en")]
    [InlineData("Show.S01E02.1080p.WEB-DL [x265].mkv", "eng")]
    [InlineData("The Movie (2019) - [Bluray-1080p].mkv", null)]
    [InlineData("Фильм.mkv", "ru")]
    [InlineData("Movie.forced.mkv", "en")]
    [InlineData("Movie.default.mkv", "en")]
    [InlineData("Movie.sdh.mkv", "en")]
    [InlineData("Movie.synced.mkv", "en")]
    [InlineData("Movie.hi.mkv", "en")]
    public void EveryResolvedNameIsRecognisedAsBelongingToItsVideo(string mediaFileName, string? language)
    {
        var parsed = ResolveAndParse(mediaFileName, language);

        Assert.True(parsed.IsCandidate);
        Assert.Contains("synced", parsed.Title, StringComparison.Ordinal);
    }

    /// <summary>
    /// Flags in the media file's own stem are consumed before our segments are
    /// reached, because the parser only ever sees the suffix after the prefix.
    /// A media file called <c>Movie.forced.mkv</c> therefore does not make our
    /// output forced.
    /// </summary>
    [Fact]
    public void FlagWordsInTheMediaStemDoNotLeakIntoTheParsedFlags()
    {
        var parsed = ResolveAndParse("Movie.forced.default.sdh.mkv", "en");

        Assert.False(parsed.IsForced);
        Assert.False(parsed.IsDefault);
        Assert.False(parsed.IsHearingImpaired);
        Assert.Equal("eng", parsed.Language);
    }

    /// <summary>
    /// The marker itself must not be readable as a flag or a language, or the
    /// picker would show a mislabelled track.
    /// </summary>
    [Fact]
    public void TheMarkerIsNeverParsedAsAFlagOrALanguage()
    {
        var parsed = JellyfinExternalSubtitleParser.Parse("Movie", "Movie.synced.srt");

        Assert.Null(parsed.Language);
        Assert.Equal("synced", parsed.Title);
        Assert.False(parsed.IsDefault);
        Assert.False(parsed.IsForced);
        Assert.False(parsed.IsHearingImpaired);
    }

    /// <summary>
    /// Hindi is the collision the parser handles specially. With the marker to
    /// its right the bare <c>.hi</c> is still the right-most resolvable segment,
    /// so it reads as Hindi and not as a hearing impaired flag.
    /// </summary>
    [Fact]
    public void HindiStillParsesAsHindiWithTheMarkerPresent()
    {
        var parsed = ResolveAndParse("Movie.mkv", "hi");

        Assert.Equal("hin", parsed.Language);
        Assert.False(parsed.IsHearingImpaired);
        Assert.Equal("synced", parsed.Title);
    }

    /// <summary>
    /// The counterexample that justifies dropping flag-token language codes: had
    /// the resolver kept <c>sdh</c>, the track would claim to be hearing
    /// impaired and lose its language.
    /// </summary>
    [Fact]
    public void KeepingAFlagTokenAsALanguageWouldHaveMislabelledTheTrack()
    {
        var wouldHaveBeen = JellyfinExternalSubtitleParser.Parse("Movie", "Movie.sdh.synced.srt");

        Assert.True(wouldHaveBeen.IsHearingImpaired);
        Assert.Null(wouldHaveBeen.Language);

        var actual = ResolveAndParse("Movie.mkv", "sdh");

        Assert.False(actual.IsHearingImpaired);
    }

    private static ParsedExternalSubtitle ResolveAndParse(string mediaFileName, string? language)
    {
        var resolver = new SubtitlePathResolver(_ => false, _ => true);

        var result = resolver.Resolve(new SubtitleOutputRequest
        {
            MediaPath = MediaFolder + "/" + mediaFileName,
            Language = language,
            Source = SubtitleSource.Embedded(),
        });

        Assert.True(result.Succeeded);

        return JellyfinExternalSubtitleParser.Parse(
            Path.GetFileNameWithoutExtension(mediaFileName),
            Path.GetFileName(result.OutputPath));
    }
}
