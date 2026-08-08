using System;
using System.Collections.Frozen;
using System.Collections.Generic;

namespace Jellyfin.Plugin.SubtitleSync.Api;

/// <summary>
/// Classifies a subtitle codec name into something we can sync, something we
/// never can, and something we are not sure about.
/// </summary>
/// <remarks>
/// <para>
/// Pure and static on purpose: this is the decision the plugin page renders as
/// an enabled or disabled track, and it has to be identical whether it is
/// reached through the item endpoint or re-checked before a conversion.
/// </para>
/// <para>
/// Deliberately <b>not</b> <c>MediaStream.IsTextSubtitleStream</c>. Jellyfin's
/// <c>IsTextFormat</c> tests for the substrings <c>pgs</c>, <c>dvdsub</c> and
/// <c>dvbsub</c>, but ffprobe reports VobSub as <c>dvd_subtitle</c> and DVB
/// subtitles as <c>dvb_subtitle</c> - neither of which contains those
/// substrings. Both would come back as "text" and then fail during conversion,
/// which is exactly the mid-run failure this classification exists to prevent.
/// Verified against
/// <c>MediaBrowser.Model/Entities/MediaStream.cs</c> at v10.11.11.
/// </para>
/// </remarks>
public static class SubtitleCodecs
{
    /// <summary>
    /// Codec names ffprobe and Jellyfin use for bitmap subtitle formats, plus
    /// the sidecar extensions Jellyfin turns into a codec name for an external
    /// bitmap track (<c>.sup</c> for PGS, <c>.sub</c>/<c>.idx</c> for VobSub).
    /// </summary>
    private static readonly FrozenSet<string> ImageBasedCodecs = new[]
    {
        "hdmv_pgs_subtitle",
        "pgssub",
        "pgs",
        "sup",
        "dvd_subtitle",
        "dvdsub",
        "vobsub",
        "sub",
        "idx",
        "dvb_subtitle",
        "dvbsub",
        "xsub",
        "dvbsub_teletext",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Substrings that identify a bitmap format whatever the surrounding name.
    /// Catches muxer-specific spellings we have not seen, such as
    /// <c>hdmv_pgs</c> variants or <c>dvd_sub</c>.
    /// </summary>
    private static readonly string[] ImageBasedFragments =
    [
        "pgs",
        "dvdsub",
        "dvd_sub",
        "dvbsub",
        "dvb_sub",
        "vobsub",
        "xsub",
    ];

    /// <summary>
    /// Text codec names ffmpeg can write out as SRT. Anything here is
    /// <see cref="SubtitleTrackSupport.Supported"/> without further thought.
    /// </summary>
    private static readonly FrozenSet<string> TextCodecs = new[]
    {
        "srt",
        "subrip",
        "ass",
        "ssa",
        "mov_text",
        "text",
        "webvtt",
        "vtt",
        "ttml",
        "dfxp",
        "smi",
        "sami",
        "subviewer",
        "subviewer1",
        "microdvd",
        "mpl2",
        "pjs",
        "realtext",
        "stl",
        "vplayer",
        "jacosub",
        "sbv",
        "eia_608",
        "cc_dec",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Codecs whose styling, positioning and karaoke timing do not survive a
    /// conversion to SRT. The track still syncs correctly; the user just gets
    /// plain text back.
    /// </summary>
    private static readonly FrozenSet<string> StyledCodecs = new[]
    {
        "ass",
        "ssa",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Classifies a codec name.
    /// </summary>
    /// <param name="codec">
    /// The codec as reported on the media stream. Null, empty or whitespace is
    /// treated as unknown rather than assumed good.
    /// </param>
    /// <returns>The support level.</returns>
    public static SubtitleTrackSupport Classify(string? codec)
    {
        if (string.IsNullOrWhiteSpace(codec))
        {
            return SubtitleTrackSupport.UnknownFormat;
        }

        var trimmed = codec.Trim();

        if (ImageBasedCodecs.Contains(trimmed))
        {
            return SubtitleTrackSupport.ImageBased;
        }

        foreach (var fragment in ImageBasedFragments)
        {
            if (trimmed.Contains(fragment, StringComparison.OrdinalIgnoreCase))
            {
                return SubtitleTrackSupport.ImageBased;
            }
        }

        return TextCodecs.Contains(trimmed)
            ? SubtitleTrackSupport.Supported
            : SubtitleTrackSupport.UnknownFormat;
    }

    /// <summary>
    /// Tests whether converting this codec to SRT throws away styling.
    /// </summary>
    /// <param name="codec">The codec as reported on the media stream.</param>
    /// <returns>True for ASS and SSA.</returns>
    public static bool StylingIsLostInSrt(string? codec)
        => !string.IsNullOrWhiteSpace(codec) && StyledCodecs.Contains(codec.Trim());

    /// <summary>
    /// Builds the sentence the plugin page shows next to a track.
    /// </summary>
    /// <remarks>
    /// Written here rather than in the front end so the wording is the same
    /// wherever the classification is surfaced, and so a new codec category
    /// cannot ship an explanation the UI has no case for.
    /// </remarks>
    /// <param name="support">The support level from <see cref="Classify"/>.</param>
    /// <param name="codec">The codec name, used verbatim in the message.</param>
    /// <param name="stylingIsLost">Whether ASS or SSA styling will be dropped.</param>
    /// <returns>A note for the user, or null when there is nothing to say.</returns>
    public static string? DescribeSupport(SubtitleTrackSupport support, string? codec, bool stylingIsLost)
    {
        var name = string.IsNullOrWhiteSpace(codec) ? "unknown" : codec.Trim();

        switch (support)
        {
            case SubtitleTrackSupport.ImageBased:
                return FormattableString.Invariant(
                    $"'{name}' is a picture-based subtitle format, so it holds no text to line up against the audio. Syncing it is not possible without OCR, which this plugin does not do.");

            case SubtitleTrackSupport.UnknownFormat:
                return FormattableString.Invariant(
                    $"'{name}' is not a subtitle format this plugin recognises. Syncing will be attempted, but the conversion to SRT may fail.");

            case SubtitleTrackSupport.Supported when stylingIsLost:
                return FormattableString.Invariant(
                    $"'{name}' carries styling, positioning and effects that SRT cannot express. The synced output keeps the text and the new timings only.");

            default:
                return null;
        }
    }

    /// <summary>
    /// Gets every codec name treated as image based, for tests and diagnostics.
    /// </summary>
    /// <returns>The codec names.</returns>
    public static IReadOnlyCollection<string> ImageBasedCodecNames() => ImageBasedCodecs;
}
