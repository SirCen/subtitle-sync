using Jellyfin.Plugin.SubtitleSync.Api;
using Xunit;

namespace Jellyfin.Plugin.SubtitleSync.Tests.Api;

/// <summary>
/// Covers <see cref="SubtitleCodecs"/>, the up-front decision about whether a
/// track can ever be synced.
/// </summary>
/// <remarks>
/// This classification is load bearing in two places: it is what disables a
/// track in the plugin page, and it is the guard that stops the subtitle
/// endpoint spending an ffmpeg run to discover the same thing. Getting it wrong
/// in the permissive direction means a user waits several minutes for an
/// analysis that was impossible from the start.
/// </remarks>
public class SubtitleCodecsTests
{
    // ------------------------------------------------------------------
    // Image based formats. There is no text in these at all.
    // ------------------------------------------------------------------

    /// <summary>
    /// The bitmap formats, under the names ffprobe actually reports.
    /// </summary>
    /// <param name="codec">The codec name.</param>
    [Theory]
    [InlineData("hdmv_pgs_subtitle")]  // PGS in a Blu-ray remux, the common one
    [InlineData("pgssub")]             // Jellyfin's own shorthand
    [InlineData("PGS")]
    [InlineData("sup")]                // external .sup sidecar
    [InlineData("dvd_subtitle")]       // VobSub muxed into MKV or MP4
    [InlineData("dvdsub")]
    [InlineData("vobsub")]
    [InlineData("sub")]                // external .sub, paired with an .idx
    [InlineData("idx")]
    [InlineData("dvb_subtitle")]       // broadcast recordings
    [InlineData("dvbsub")]
    [InlineData("xsub")]
    public void ImageBasedCodecsAreRejected(string codec)
    {
        Assert.Equal(SubtitleTrackSupport.ImageBased, SubtitleCodecs.Classify(codec));
    }

    /// <summary>
    /// The two spellings Jellyfin's own <c>MediaStream.IsTextFormat</c> misses.
    /// It tests for the substrings <c>dvdsub</c> and <c>dvbsub</c>, but ffprobe
    /// writes <c>dvd_subtitle</c> and <c>dvb_subtitle</c>, so the core helper
    /// calls both of them text. This is the regression that would put a bitmap
    /// track in front of a user as syncable.
    /// </summary>
    /// <param name="codec">The codec name.</param>
    [Theory]
    [InlineData("dvd_subtitle")]
    [InlineData("dvb_subtitle")]
    public void UnderscoredBitmapSpellingsAreRejectedEvenThoughCoreCallsThemText(string codec)
    {
        Assert.True(MediaBrowser.Model.Entities.MediaStream.IsTextFormat(codec));
        Assert.Equal(SubtitleTrackSupport.ImageBased, SubtitleCodecs.Classify(codec));
    }

    /// <summary>
    /// Classification is case insensitive. Container tags are not normalised
    /// anywhere between ffprobe and here.
    /// </summary>
    [Fact]
    public void CodecMatchingIgnoresCase()
    {
        Assert.Equal(SubtitleTrackSupport.ImageBased, SubtitleCodecs.Classify("HDMV_PGS_SUBTITLE"));
        Assert.Equal(SubtitleTrackSupport.Supported, SubtitleCodecs.Classify("SubRip"));
    }

    /// <summary>
    /// A muxer spelling we have not seen still trips the substring rules, so a
    /// new PGS or DVB variant fails closed rather than reaching the encoder.
    /// </summary>
    /// <param name="codec">The codec name.</param>
    [Theory]
    [InlineData("hdmv_pgs")]
    [InlineData("dvd_sub")]
    [InlineData("dvb_sub")]
    public void UnknownBitmapVariantsStillFailClosed(string codec)
    {
        Assert.Equal(SubtitleTrackSupport.ImageBased, SubtitleCodecs.Classify(codec));
    }

    // ------------------------------------------------------------------
    // Text formats
    // ------------------------------------------------------------------

    /// <summary>
    /// The text formats that turn up in real libraries.
    /// </summary>
    /// <param name="codec">The codec name.</param>
    [Theory]
    [InlineData("srt")]
    [InlineData("subrip")]
    [InlineData("ass")]
    [InlineData("ssa")]
    [InlineData("mov_text")]
    [InlineData("webvtt")]
    [InlineData("ttml")]
    [InlineData("microdvd")]
    public void TextCodecsAreSupported(string codec)
    {
        Assert.Equal(SubtitleTrackSupport.Supported, SubtitleCodecs.Classify(codec));
    }

    /// <summary>
    /// An unrecognised codec is neither blocked nor silently trusted. ffmpeg
    /// reads more text formats than any list here will hold, so blocking would
    /// cost working tracks; the flag is what lets the page warn instead.
    /// </summary>
    /// <param name="codec">The codec name.</param>
    [Theory]
    [InlineData("some_new_text_format")]
    [InlineData("dvb_teletext")]
    public void UnrecognisedCodecsAreFlaggedButNotBlocked(string codec)
    {
        var support = SubtitleCodecs.Classify(codec);

        Assert.Equal(SubtitleTrackSupport.UnknownFormat, support);
        Assert.NotEqual(SubtitleTrackSupport.ImageBased, support);
    }

    /// <summary>
    /// A missing codec is unknown, not assumed good. An embedded stream with no
    /// codec is a probe that failed, and pretending otherwise would present a
    /// track that cannot be read.
    /// </summary>
    /// <param name="codec">The codec name.</param>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AbsentCodecIsUnknown(string? codec)
    {
        Assert.Equal(SubtitleTrackSupport.UnknownFormat, SubtitleCodecs.Classify(codec));
    }

    // ------------------------------------------------------------------
    // Styling loss
    // ------------------------------------------------------------------

    /// <summary>
    /// ASS and SSA carry positioning, fonts and effects that SRT has no syntax
    /// for. The track still syncs; the user needs to know what they get back.
    /// </summary>
    /// <param name="codec">The codec name.</param>
    [Theory]
    [InlineData("ass")]
    [InlineData("ssa")]
    [InlineData("ASS")]
    public void StyledCodecsAreFlaggedAsLossy(string codec)
    {
        Assert.True(SubtitleCodecs.StylingIsLostInSrt(codec));
        Assert.Equal(SubtitleTrackSupport.Supported, SubtitleCodecs.Classify(codec));
    }

    /// <summary>
    /// Nothing else is. SRT to SRT loses nothing, so an unconditional warning
    /// would train users to ignore it.
    /// </summary>
    /// <param name="codec">The codec name.</param>
    [Theory]
    [InlineData("srt")]
    [InlineData("subrip")]
    [InlineData("mov_text")]
    [InlineData(null)]
    public void OtherCodecsAreNotFlaggedAsLossy(string? codec)
    {
        Assert.False(SubtitleCodecs.StylingIsLostInSrt(codec));
    }

    // ------------------------------------------------------------------
    // The message the user reads
    // ------------------------------------------------------------------

    /// <summary>
    /// A supported, non-lossy track has nothing to say. A null note is what
    /// lets the page render no annotation at all.
    /// </summary>
    [Fact]
    public void SupportedTracksHaveNoNote()
    {
        Assert.Null(SubtitleCodecs.DescribeSupport(SubtitleTrackSupport.Supported, "subrip", stylingIsLost: false));
    }

    /// <summary>
    /// Every state that disables or degrades a track explains itself, and names
    /// the codec so the user can tell which track is meant.
    /// </summary>
    /// <param name="support">The support level.</param>
    /// <param name="codec">The codec name.</param>
    /// <param name="stylingIsLost">Whether styling is dropped.</param>
    [Theory]
    [InlineData(SubtitleTrackSupport.ImageBased, "hdmv_pgs_subtitle", false)]
    [InlineData(SubtitleTrackSupport.UnknownFormat, "dvb_teletext", false)]
    [InlineData(SubtitleTrackSupport.Supported, "ass", true)]
    public void FlaggedTracksExplainThemselves(SubtitleTrackSupport support, string codec, bool stylingIsLost)
    {
        var note = SubtitleCodecs.DescribeSupport(support, codec, stylingIsLost);

        Assert.NotNull(note);
        Assert.Contains(codec, note, System.StringComparison.Ordinal);
    }

    /// <summary>
    /// The unsupported message survives a missing codec rather than rendering
    /// an empty pair of quotes.
    /// </summary>
    [Fact]
    public void MissingCodecStillProducesAReadableNote()
    {
        var note = SubtitleCodecs.DescribeSupport(SubtitleTrackSupport.UnknownFormat, null, stylingIsLost: false);

        Assert.NotNull(note);
        Assert.Contains("unknown", note, System.StringComparison.Ordinal);
    }
}
