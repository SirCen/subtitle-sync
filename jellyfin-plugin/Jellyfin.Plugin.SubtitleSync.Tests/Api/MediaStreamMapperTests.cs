using System;
using System.Linq;
using Jellyfin.Plugin.SubtitleSync.Api;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using Xunit;

namespace Jellyfin.Plugin.SubtitleSync.Tests.Api;

/// <summary>
/// Covers <see cref="MediaStreamMapper"/>, the shaping of Jellyfin's media
/// model into the payload the plugin page consumes.
/// </summary>
/// <remarks>
/// The interesting cases are all the ones a real library produces and a happy
/// path does not: an item with no subtitles, several media versions carrying the
/// same stream indexes, a track with no language, and a track whose format can
/// never be synced.
/// </remarks>
public class MediaStreamMapperTests
{
    private const string SourceId = "f1e2d3c4b5a6978877665544332211aa";

    // ------------------------------------------------------------------
    // Subtitle track mapping
    // ------------------------------------------------------------------

    /// <summary>
    /// The straightforward case: an external SRT sidecar, the exact shape of
    /// the harness fixture.
    /// </summary>
    [Fact]
    public void MapsAnExternalSrtSidecar()
    {
        var track = MediaStreamMapper.ToSubtitleTrack(
            new MediaStream
            {
                Type = MediaStreamType.Subtitle,
                Index = 2,
                Codec = "srt",
                Language = "eng",
                IsExternal = true,
                Path = "/media/movies/Sample Clip (2020)/Sample Clip (2020).en.srt",
                IsDefault = true,
            },
            SourceId);

        Assert.Equal(2, track.Index);
        Assert.Equal(SourceId, track.MediaSourceId);
        Assert.Equal("eng", track.Language);
        Assert.Equal("srt", track.Codec);
        Assert.True(track.IsExternal);
        Assert.True(track.IsDefault);
        Assert.Equal("/media/movies/Sample Clip (2020)/Sample Clip (2020).en.srt", track.Path);
        Assert.True(track.CanSync);
        Assert.Equal(SubtitleTrackSupport.Supported, track.Support);
        Assert.False(track.StylingWillBeLost);
        Assert.Null(track.Note);
    }

    /// <summary>
    /// A track with no language is normal, not an error: plenty of muxes leave
    /// the tag off. It has to survive mapping as a null the page can render as
    /// "unknown", never as a crash or an empty string it has to test for.
    /// </summary>
    /// <param name="language">The language as reported on the stream.</param>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void ANullLanguageStaysNull(string? language)
    {
        var track = MediaStreamMapper.ToSubtitleTrack(
            new MediaStream
            {
                Type = MediaStreamType.Subtitle,
                Index = 3,
                Codec = "subrip",
                Language = language,
            },
            SourceId);

        Assert.Null(track.Language);
        Assert.True(track.CanSync);
    }

    /// <summary>
    /// An ASS track is offered, but with the warning the UI needs. Blocking it
    /// would be wrong - the timings are exactly what we can fix - and offering
    /// it silently would surprise a user who loses their typesetting.
    /// </summary>
    [Fact]
    public void AnAssTrackIsSyncableButFlaggedAsLossy()
    {
        var track = MediaStreamMapper.ToSubtitleTrack(
            new MediaStream
            {
                Type = MediaStreamType.Subtitle,
                Index = 4,
                Codec = "ass",
                Language = "jpn",
                Title = "Signs & Songs",
            },
            SourceId);

        Assert.True(track.CanSync);
        Assert.Equal(SubtitleTrackSupport.Supported, track.Support);
        Assert.True(track.StylingWillBeLost);
        Assert.NotNull(track.Note);
        Assert.Contains("SRT cannot express", track.Note, StringComparison.Ordinal);
        Assert.Equal("Signs & Songs", track.Title);
    }

    /// <summary>
    /// A PGS track is reported unsupported before anything is run, with a
    /// reason. This is the whole point of classifying up front.
    /// </summary>
    [Fact]
    public void APgsTrackIsReportedUnsupportedWithAReason()
    {
        var track = MediaStreamMapper.ToSubtitleTrack(
            new MediaStream
            {
                Type = MediaStreamType.Subtitle,
                Index = 5,
                Codec = "hdmv_pgs_subtitle",
                Language = "eng",
            },
            SourceId);

        Assert.False(track.CanSync);
        Assert.Equal(SubtitleTrackSupport.ImageBased, track.Support);
        Assert.NotNull(track.Note);
        Assert.Contains("picture-based", track.Note, StringComparison.Ordinal);
    }

    /// <summary>
    /// An embedded track never reports a path, even when the stream carries the
    /// container's own. Echoing that would give the save step (#8) the video
    /// file as the thing an overwrite would replace.
    /// </summary>
    [Fact]
    public void AnEmbeddedTrackNeverReportsAPath()
    {
        var track = MediaStreamMapper.ToSubtitleTrack(
            new MediaStream
            {
                Type = MediaStreamType.Subtitle,
                Index = 2,
                Codec = "subrip",
                IsExternal = false,
                Path = "/media/movies/Arrival (2016)/Arrival.mkv",
            },
            SourceId);

        Assert.False(track.IsExternal);
        Assert.Null(track.Path);
    }

    // ------------------------------------------------------------------
    // Audio track mapping
    // ------------------------------------------------------------------

    /// <summary>
    /// Audio tracks carry enough for the page to let the user pick which one
    /// the speech signal comes from.
    /// </summary>
    [Fact]
    public void MapsAnAudioStream()
    {
        var audio = MediaStreamMapper.ToAudioStream(new MediaStream
        {
            Type = MediaStreamType.Audio,
            Index = 1,
            Codec = "eac3",
            Language = "eng",
            Channels = 6,
            SampleRate = 48000,
            IsDefault = true,
        });

        Assert.Equal(1, audio.Index);
        Assert.Equal("eac3", audio.Codec);
        Assert.Equal(6, audio.Channels);
        Assert.Equal(48000, audio.SampleRate);
        Assert.True(audio.IsDefault);
    }

    // ------------------------------------------------------------------
    // Media source mapping
    // ------------------------------------------------------------------

    /// <summary>
    /// Streams are split by type, sorted by index, and video streams are
    /// dropped: the page has no use for them and listing them would invite the
    /// user to pick one.
    /// </summary>
    [Fact]
    public void SplitsStreamsByTypeAndOrdersThem()
    {
        var mapped = MediaStreamMapper.ToMediaSource(new MediaSourceInfo
        {
            Id = SourceId,
            Name = "Sample Clip",
            Path = "/media/movies/Sample Clip (2020)/Sample Clip (2020).mp4",
            Container = "mp4",
            RunTimeTicks = 300_000_000,
            DefaultAudioStreamIndex = 1,
            MediaStreams =
            [
                new MediaStream { Type = MediaStreamType.Subtitle, Index = 4, Codec = "ass" },
                new MediaStream { Type = MediaStreamType.Video, Index = 0, Codec = "h264" },
                new MediaStream { Type = MediaStreamType.Subtitle, Index = 3, Codec = "subrip" },
                new MediaStream { Type = MediaStreamType.Audio, Index = 1, Codec = "aac" },
            ],
        });

        Assert.Equal(SourceId, mapped.Id);
        Assert.Equal("mp4", mapped.Container);
        Assert.Equal(1, mapped.DefaultAudioStreamIndex);
        Assert.Equal([1], mapped.AudioStreams.Select(a => a.Index));
        Assert.Equal([3, 4], mapped.SubtitleStreams.Select(s => s.Index));
    }

    /// <summary>
    /// Every mapped track knows which source it came from, so the page can hand
    /// the pair straight back to the subtitle endpoint.
    /// </summary>
    [Fact]
    public void TracksCarryTheirSourceId()
    {
        var mapped = MediaStreamMapper.ToMediaSource(new MediaSourceInfo
        {
            Id = SourceId,
            MediaStreams = [new MediaStream { Type = MediaStreamType.Subtitle, Index = 2, Codec = "srt" }],
        });

        Assert.Equal(SourceId, Assert.Single(mapped.SubtitleStreams).MediaSourceId);
    }

    /// <summary>
    /// A source with no streams at all maps to empty lists, not nulls. A null
    /// here would be a TypeError in the page's first <c>.map()</c>.
    /// </summary>
    [Fact]
    public void ASourceWithNoStreamsMapsToEmptyLists()
    {
        var mapped = MediaStreamMapper.ToMediaSource(new MediaSourceInfo { Id = SourceId, MediaStreams = null! });

        Assert.Empty(mapped.AudioStreams);
        Assert.Empty(mapped.SubtitleStreams);
    }

    // ------------------------------------------------------------------
    // Item assembly
    // ------------------------------------------------------------------

    /// <summary>
    /// Runtime is offered in both ticks and seconds. Every consumer of this is
    /// JavaScript working in seconds, and a tick division repeated across the
    /// front end is a rounding bug waiting to be written.
    /// </summary>
    [Fact]
    public void ReportsRuntimeInTicksAndSeconds()
    {
        var item = MediaStreamMapper.ToItem(Guid.NewGuid(), "Arrival", "Movie", 71_400_000_000, []);

        Assert.Equal(71_400_000_000, item.RunTimeTicks);
        Assert.Equal(7140d, item.RunTimeSeconds);
    }

    /// <summary>
    /// An unknown runtime stays unknown rather than becoming zero, which the
    /// page would render as a zero-length timeline.
    /// </summary>
    [Fact]
    public void AnUnknownRuntimeStaysNull()
    {
        var item = MediaStreamMapper.ToItem(Guid.NewGuid(), "Arrival", "Movie", null, []);

        Assert.Null(item.RunTimeTicks);
        Assert.Null(item.RunTimeSeconds);
    }

    /// <summary>
    /// An item with no subtitle tracks anywhere says so explicitly, so the page
    /// can explain itself instead of rendering an empty dropdown that looks
    /// broken.
    /// </summary>
    [Fact]
    public void AnItemWithNoSubtitlesSaysSo()
    {
        var item = MediaStreamMapper.ToItem(
            Guid.NewGuid(),
            "Arrival",
            "Movie",
            71_400_000_000,
            [
                new MediaSourceInfo
                {
                    Id = SourceId,
                    MediaStreams = [new MediaStream { Type = MediaStreamType.Audio, Index = 1, Codec = "aac" }],
                }
            ]);

        Assert.False(item.HasSyncableSubtitles);
        Assert.Empty(Assert.Single(item.MediaSources).SubtitleStreams);
    }

    /// <summary>
    /// An item whose only subtitle track is a bitmap is also "nothing to sync".
    /// Counting it would put the page into a state where every option is
    /// disabled and nothing explains why the button does not work.
    /// </summary>
    [Fact]
    public void AnItemWithOnlyImageBasedSubtitlesHasNothingSyncable()
    {
        var item = MediaStreamMapper.ToItem(
            Guid.NewGuid(),
            "Blu-ray Remux",
            "Movie",
            null,
            [
                new MediaSourceInfo
                {
                    Id = SourceId,
                    MediaStreams = [new MediaStream { Type = MediaStreamType.Subtitle, Index = 2, Codec = "hdmv_pgs_subtitle" }],
                }
            ]);

        Assert.False(item.HasSyncableSubtitles);
        Assert.False(Assert.Single(Assert.Single(item.MediaSources).SubtitleStreams).CanSync);
    }

    /// <summary>
    /// Several versions of the same film keep their streams apart. Both sources
    /// have a stream 2, and flattening them would sync the 4K remux's track
    /// while the page thinks it is showing the 1080p one.
    /// </summary>
    [Fact]
    public void MultipleMediaSourcesKeepTheirOwnStreams()
    {
        var item = MediaStreamMapper.ToItem(
            Guid.NewGuid(),
            "Arrival",
            "Movie",
            null,
            [
                new MediaSourceInfo
                {
                    Id = "aaaa",
                    Name = "1080p",
                    MediaStreams = [new MediaStream { Type = MediaStreamType.Subtitle, Index = 2, Codec = "subrip" }],
                },
                new MediaSourceInfo
                {
                    Id = "bbbb",
                    Name = "4K Remux",
                    MediaStreams = [new MediaStream { Type = MediaStreamType.Subtitle, Index = 2, Codec = "hdmv_pgs_subtitle" }],
                }
            ]);

        Assert.Equal(2, item.MediaSources.Count);
        Assert.True(item.HasSyncableSubtitles);

        var hd = item.MediaSources.Single(s => s.Id == "aaaa");
        var uhd = item.MediaSources.Single(s => s.Id == "bbbb");

        Assert.True(Assert.Single(hd.SubtitleStreams).CanSync);
        Assert.False(Assert.Single(uhd.SubtitleStreams).CanSync);
        Assert.Equal("aaaa", Assert.Single(hd.SubtitleStreams).MediaSourceId);
        Assert.Equal("bbbb", Assert.Single(uhd.SubtitleStreams).MediaSourceId);
    }

    /// <summary>
    /// Episode fields ride along when they are there, so the page can title
    /// itself "Show - S01E02 - Name" rather than just the episode name.
    /// </summary>
    [Fact]
    public void CarriesEpisodeIdentity()
    {
        var item = MediaStreamMapper.ToItem(
            Guid.NewGuid(),
            "The One With The Subtitles",
            "Episode",
            null,
            [],
            seriesName: "Friends",
            parentIndexNumber: 1,
            indexNumber: 2);

        Assert.Equal("Friends", item.SeriesName);
        Assert.Equal(1, item.ParentIndexNumber);
        Assert.Equal(2, item.IndexNumber);
        Assert.Equal("Episode", item.ItemType);
    }

    // ------------------------------------------------------------------
    // Locating a requested track
    // ------------------------------------------------------------------

    /// <summary>
    /// With a source id given, only that source is searched. The same index in
    /// another version must not answer.
    /// </summary>
    [Fact]
    public void FindsTheTrackInTheNamedSourceOnly()
    {
        MediaSourceInfo[] sources =
        [
            new() { Id = "aaaa", MediaStreams = [Subtitle(2, "subrip")] },
            new() { Id = "bbbb", MediaStreams = [Subtitle(2, "ass")] },
        ];

        Assert.True(MediaStreamMapper.TryFindSubtitleStream(sources, "bbbb", 2, out var source, out var stream));
        Assert.Equal("bbbb", source!.Id);
        Assert.Equal("ass", stream!.Codec);
    }

    /// <summary>
    /// With no source id, the first source carrying the index answers. A
    /// single-version item is the overwhelmingly common case and should not
    /// need the extra parameter.
    /// </summary>
    [Fact]
    public void FallsBackToTheFirstSourceCarryingTheIndex()
    {
        MediaSourceInfo[] sources =
        [
            new() { Id = "aaaa", MediaStreams = [Subtitle(2, "subrip")] },
            new() { Id = "bbbb", MediaStreams = [Subtitle(2, "ass")] },
        ];

        Assert.True(MediaStreamMapper.TryFindSubtitleStream(sources, null, 2, out var source, out _));
        Assert.Equal("aaaa", source!.Id);
    }

    /// <summary>
    /// An index that exists as audio is not a subtitle track. Answering with it
    /// would hand the encoder an audio stream.
    /// </summary>
    [Fact]
    public void DoesNotMatchANonSubtitleStreamAtTheSameIndex()
    {
        MediaSourceInfo[] sources =
        [
            new()
            {
                Id = "aaaa",
                MediaStreams = [new MediaStream { Type = MediaStreamType.Audio, Index = 1, Codec = "aac" }],
            },
        ];

        Assert.False(MediaStreamMapper.TryFindSubtitleStream(sources, "aaaa", 1, out var source, out var stream));
        Assert.Null(source);
        Assert.Null(stream);
    }

    /// <summary>
    /// An unknown source id or index finds nothing, which is the controller's
    /// cue for a 404.
    /// </summary>
    /// <param name="mediaSourceId">The requested source.</param>
    /// <param name="index">The requested index.</param>
    [Theory]
    [InlineData("nope", 2)]
    [InlineData("aaaa", 99)]
    public void MissingTrackIsNotFound(string mediaSourceId, int index)
    {
        MediaSourceInfo[] sources = [new() { Id = "aaaa", MediaStreams = [Subtitle(2, "subrip")] }];

        Assert.False(MediaStreamMapper.TryFindSubtitleStream(sources, mediaSourceId, index, out _, out _));
    }

    /// <summary>
    /// An item with no media sources at all - a stub entry, or one whose file
    /// has gone - finds nothing rather than throwing.
    /// </summary>
    [Fact]
    public void NoSourcesFindsNothing()
    {
        Assert.False(MediaStreamMapper.TryFindSubtitleStream([], null, 2, out _, out _));
    }

    private static MediaStream Subtitle(int index, string codec)
        => new() { Type = MediaStreamType.Subtitle, Index = index, Codec = codec };
}
