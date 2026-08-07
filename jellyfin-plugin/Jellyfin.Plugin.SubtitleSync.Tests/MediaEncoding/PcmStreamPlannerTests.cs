using System;
using System.Collections.Generic;
using Jellyfin.Plugin.SubtitleSync.MediaEncoding;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Xunit;

namespace Jellyfin.Plugin.SubtitleSync.Tests.MediaEncoding;

/// <summary>
/// Tests for <see cref="PcmStreamPlanner"/>, which is the whole of the PCM
/// endpoint's request validation and stream selection.
/// </summary>
public class PcmStreamPlannerTests
{
    /// <summary>
    /// With nothing requested, the first source is the one Jellyfin itself would
    /// play, so it is the one to measure.
    /// </summary>
    [Fact]
    public void NoRequestedSource_TakesTheFirst()
    {
        var plan = PcmStreamPlanner.Plan(
            new[]
            {
                Source("aaa", "/media/first.mkv", Audio(1)),
                Source("bbb", "/media/second.mkv", Audio(1)),
            },
            mediaSourceId: null,
            audioStreamIndex: null);

        Assert.True(plan.Succeeded);
        Assert.Equal("/media/first.mkv", plan.InputPath);
        Assert.Equal("aaa", plan.MediaSourceId);
    }

    /// <summary>
    /// A named source wins over position, whatever the casing of the id.
    /// </summary>
    [Theory]
    [InlineData("bbb")]
    [InlineData("BBB")]
    public void RequestedSource_IsSelectedCaseInsensitively(string requested)
    {
        var plan = PcmStreamPlanner.Plan(
            new[]
            {
                Source("aaa", "/media/first.mkv", Audio(1)),
                Source("bbb", "/media/second.mkv", Audio(1)),
            },
            requested,
            audioStreamIndex: null);

        Assert.True(plan.Succeeded);
        Assert.Equal("/media/second.mkv", plan.InputPath);
    }

    /// <summary>
    /// A source id that matches nothing is a 404-shaped failure, not a silent
    /// fallback to some other version of the film.
    /// </summary>
    [Fact]
    public void UnknownSource_Fails()
    {
        var plan = PcmStreamPlanner.Plan(
            new[] { Source("aaa", "/media/first.mkv", Audio(1)) },
            "does-not-exist",
            audioStreamIndex: null);

        Assert.False(plan.Succeeded);
        Assert.Equal(PcmStreamPlanFailure.UnknownMediaSource, plan.Failure);
        Assert.Contains("does-not-exist", plan.ErrorMessage, StringComparison.Ordinal);
    }

    /// <summary>
    /// An item with no sources at all is a different failure from one whose named
    /// source is missing, because the two mean different things to a client.
    /// </summary>
    [Fact]
    public void NoSources_Fails()
    {
        var plan = PcmStreamPlanner.Plan(Array.Empty<MediaSourceInfo>(), null, null);

        Assert.False(plan.Succeeded);
        Assert.Equal(PcmStreamPlanFailure.NoMediaSource, plan.Failure);
    }

    /// <summary>
    /// A source with no path cannot be decoded, and saying so beats letting
    /// ffmpeg fall over on an empty input.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void SourceWithoutPath_Fails(string? path)
    {
        var plan = PcmStreamPlanner.Plan(new[] { Source("aaa", path, Audio(1)) }, null, null);

        Assert.False(plan.Succeeded);
        Assert.Equal(PcmStreamPlanFailure.MissingPath, plan.Failure);
    }

    /// <summary>
    /// Remote sources are refused here rather than by ffmpeg, since the argument
    /// builder whitelists the file protocol only.
    /// </summary>
    [Fact]
    public void NonFileSource_Fails()
    {
        var source = Source("aaa", "http://example.invalid/stream", Audio(1));
        source.Protocol = MediaProtocol.Http;

        var plan = PcmStreamPlanner.Plan(new[] { source }, null, null);

        Assert.False(plan.Succeeded);
        Assert.Equal(PcmStreamPlanFailure.UnsupportedProtocol, plan.Failure);
    }

    /// <summary>
    /// A source with only video and subtitles has nothing to extract.
    /// </summary>
    [Fact]
    public void SourceWithoutAudio_Fails()
    {
        var source = Source("aaa", "/media/silent.mkv");
        source.MediaStreams = new[]
        {
            new MediaStream { Index = 0, Type = MediaStreamType.Video },
            new MediaStream { Index = 1, Type = MediaStreamType.Subtitle },
        };

        var plan = PcmStreamPlanner.Plan(new[] { source }, null, null);

        Assert.False(plan.Succeeded);
        Assert.Equal(PcmStreamPlanFailure.NoAudioStream, plan.Failure);
    }

    /// <summary>
    /// The requested index is the absolute container index, matching Jellyfin's
    /// <c>MediaStream.Index</c>. Asking for 2 on a file whose streams are
    /// video 0, audio 1, audio 2 must give the second audio track, not the
    /// third stream counted among audio only.
    /// </summary>
    [Fact]
    public void RequestedIndex_IsTheContainerIndex()
    {
        var source = Source("aaa", "/media/dual.mkv");
        source.MediaStreams = new[]
        {
            new MediaStream { Index = 0, Type = MediaStreamType.Video },
            Audio(1),
            Audio(2),
        };

        var plan = PcmStreamPlanner.Plan(new[] { source }, null, audioStreamIndex: 2);

        Assert.True(plan.Succeeded);
        Assert.Equal(2, plan.AudioStreamIndex);
    }

    /// <summary>
    /// Pointing the index at a stream that exists but is not audio is a mistake
    /// worth reporting, not a track to try to decode.
    /// </summary>
    [Fact]
    public void RequestedIndexOfANonAudioStream_Fails()
    {
        var source = Source("aaa", "/media/dual.mkv");
        source.MediaStreams = new[]
        {
            new MediaStream { Index = 0, Type = MediaStreamType.Video },
            Audio(1),
        };

        var plan = PcmStreamPlanner.Plan(new[] { source }, null, audioStreamIndex: 0);

        Assert.False(plan.Succeeded);
        Assert.Equal(PcmStreamPlanFailure.UnknownAudioStream, plan.Failure);
    }

    /// <summary>
    /// An index nobody has is likewise a failure.
    /// </summary>
    [Fact]
    public void RequestedIndexOutOfRange_Fails()
    {
        var plan = PcmStreamPlanner.Plan(
            new[] { Source("aaa", "/media/first.mkv", Audio(1)) },
            null,
            audioStreamIndex: 9);

        Assert.False(plan.Succeeded);
        Assert.Equal(PcmStreamPlanFailure.UnknownAudioStream, plan.Failure);
    }

    /// <summary>
    /// With no index requested, the source's own default audio track wins - that
    /// is the dub the user is actually watching, so it is the one whose timing
    /// the subtitles must match.
    /// </summary>
    [Fact]
    public void NoRequestedIndex_PrefersTheSourceDefault()
    {
        var source = Source("aaa", "/media/dual.mkv");
        source.MediaStreams = new[] { Audio(1), Audio(2) };
        source.DefaultAudioStreamIndex = 2;

        var plan = PcmStreamPlanner.Plan(new[] { source }, null, null);

        Assert.True(plan.Succeeded);
        Assert.Equal(2, plan.AudioStreamIndex);
    }

    /// <summary>
    /// Failing that, the stream flagged default in the container.
    /// </summary>
    [Fact]
    public void NoRequestedIndex_FallsBackToTheDefaultFlag()
    {
        var source = Source("aaa", "/media/dual.mkv");
        var second = Audio(2);
        second.IsDefault = true;
        source.MediaStreams = new[] { Audio(1), second };

        var plan = PcmStreamPlanner.Plan(new[] { source }, null, null);

        Assert.True(plan.Succeeded);
        Assert.Equal(2, plan.AudioStreamIndex);
    }

    /// <summary>
    /// And failing that, the first audio stream. Never ffmpeg's own default
    /// selection: the endpoint reports which track it measured, so it has to be
    /// the one choosing.
    /// </summary>
    [Fact]
    public void NoRequestedIndexAndNoDefault_TakesTheFirstAudioStream()
    {
        var source = Source("aaa", "/media/dual.mkv");
        source.MediaStreams = new[]
        {
            new MediaStream { Index = 0, Type = MediaStreamType.Video },
            Audio(3),
            Audio(4),
        };

        var plan = PcmStreamPlanner.Plan(new[] { source }, null, null);

        Assert.True(plan.Succeeded);
        Assert.Equal(3, plan.AudioStreamIndex);
    }

    /// <summary>
    /// A default index pointing at a stream that is not audio is ignored rather
    /// than obeyed.
    /// </summary>
    [Fact]
    public void SourceDefaultPointingAtVideo_IsIgnored()
    {
        var source = Source("aaa", "/media/dual.mkv");
        source.MediaStreams = new[]
        {
            new MediaStream { Index = 0, Type = MediaStreamType.Video },
            Audio(1),
        };
        source.DefaultAudioStreamIndex = 0;

        var plan = PcmStreamPlanner.Plan(new[] { source }, null, null);

        Assert.True(plan.Succeeded);
        Assert.Equal(1, plan.AudioStreamIndex);
    }

    /// <summary>
    /// The harness fixture, exactly as the live server reports it: a two-stream
    /// mp4 (video 0, audio 1) with a sibling <c>.en.srt</c>, which Jellyfin
    /// numbers subtitle 0, video 1, audio 2. Handing ffmpeg the Jellyfin index
    /// gives <c>-map 0:2</c> against a container with two streams, which fails
    /// with "Stream map '' matches no streams" - observed against Jellyfin
    /// 10.11.11 before this translation existed.
    /// </summary>
    [Fact]
    public void ExternalSubtitle_ShiftsJellyfinIndexesOffTheContainer()
    {
        var external = new MediaStream { Index = 0, Type = MediaStreamType.Subtitle, IsExternal = true };
        var video = new MediaStream { Index = 1, Type = MediaStreamType.Video };
        var source = Source("aaa", "/media/Sample Clip (2020).mp4");
        source.MediaStreams = new[] { external, video, Audio(2) };
        source.DefaultAudioStreamIndex = 2;

        var plan = PcmStreamPlanner.Plan(new[] { source }, null, null);

        Assert.True(plan.Succeeded);

        // What the client speaks.
        Assert.Equal(2, plan.AudioStreamIndex);

        // What ffmpeg needs.
        Assert.Equal(1, plan.ContainerAudioStreamIndex);
    }

    /// <summary>
    /// With no external streams the two numberings coincide, and the translation
    /// must not perturb them.
    /// </summary>
    [Fact]
    public void WithoutExternalStreams_TheIndexesCoincide()
    {
        var source = Source("aaa", "/media/plain.mkv");
        source.MediaStreams = new[]
        {
            new MediaStream { Index = 0, Type = MediaStreamType.Video },
            Audio(1),
            Audio(2),
        };

        var plan = PcmStreamPlanner.Plan(new[] { source }, null, audioStreamIndex: 2);

        Assert.True(plan.Succeeded);
        Assert.Equal(2, plan.AudioStreamIndex);
        Assert.Equal(2, plan.ContainerAudioStreamIndex);
    }

    /// <summary>
    /// Several external streams shift the numbering by more than one, and the
    /// translation counts internal streams rather than subtracting a guess.
    /// </summary>
    [Fact]
    public void MultipleExternalStreams_AreAllSkipped()
    {
        var source = Source("aaa", "/media/many.mkv");
        source.MediaStreams = new[]
        {
            new MediaStream { Index = 0, Type = MediaStreamType.Subtitle, IsExternal = true },
            new MediaStream { Index = 1, Type = MediaStreamType.Subtitle, IsExternal = true },
            new MediaStream { Index = 2, Type = MediaStreamType.Video },
            Audio(3),
            Audio(4),
        };

        var plan = PcmStreamPlanner.Plan(new[] { source }, null, audioStreamIndex: 4);

        Assert.True(plan.Succeeded);
        Assert.Equal(4, plan.AudioStreamIndex);
        Assert.Equal(2, plan.ContainerAudioStreamIndex);
    }

    /// <summary>
    /// An external audio track lives in its own file, so mapping it out of the
    /// media container would decode the wrong thing. Refused rather than
    /// silently mis-decoded.
    /// </summary>
    [Fact]
    public void ExternalAudioStream_Fails()
    {
        var source = Source("aaa", "/media/film.mkv");
        var external = Audio(1);
        external.IsExternal = true;
        source.MediaStreams = new[] { external };

        var plan = PcmStreamPlanner.Plan(new[] { source }, null, null);

        Assert.False(plan.Succeeded);
        Assert.Equal(PcmStreamPlanFailure.ExternalAudioStream, plan.Failure);
    }

    /// <summary>
    /// The estimate is the naive runtime arithmetic, and only ever an estimate.
    /// It exists to move a progress bar, which is why it may not become a
    /// <c>Content-Length</c>: 30 s of container runtime here predicts 960000
    /// bytes, and the real fixture decodes to 960006.
    /// </summary>
    [Fact]
    public void Estimate_IsRuntimeTimesSampleRateTimesBytesPerSample()
    {
        Assert.Equal(960_000, PcmStreamPlanner.EstimateByteLength(30 * TimeSpan.TicksPerSecond));
    }

    /// <summary>
    /// An unknown or nonsensical runtime yields no estimate at all, so the client
    /// shows indeterminate progress rather than a bar against a made-up total.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData(0L)]
    [InlineData(-1L)]
    public void Estimate_IsAbsentWhenTheRuntimeIsUnusable(long? ticks)
    {
        Assert.Null(PcmStreamPlanner.EstimateByteLength(ticks));
    }

    /// <summary>
    /// The estimate is always a whole number of samples, so a consumer dividing
    /// by two never sees a stray half-sample.
    /// </summary>
    [Fact]
    public void Estimate_IsAlignedToWholeSamples()
    {
        var estimate = PcmStreamPlanner.EstimateByteLength(1234567);

        Assert.NotNull(estimate);
        Assert.Equal(0, estimate!.Value % FfmpegArguments.BytesPerSample);
    }

    /// <summary>
    /// The estimate rides on the successful plan, taken from the chosen source's
    /// runtime rather than the item's.
    /// </summary>
    [Fact]
    public void Plan_CarriesTheEstimateOfTheChosenSource()
    {
        var first = Source("aaa", "/media/first.mkv", Audio(1));
        first.RunTimeTicks = 10 * TimeSpan.TicksPerSecond;
        var second = Source("bbb", "/media/second.mkv", Audio(1));
        second.RunTimeTicks = 20 * TimeSpan.TicksPerSecond;

        var plan = PcmStreamPlanner.Plan(new[] { first, second }, "bbb", null);

        Assert.Equal(20 * 16000 * 2, plan.EstimatedByteLength);
    }

    /// <summary>
    /// Null sources are a programming error, not a 404.
    /// </summary>
    [Fact]
    public void NullSources_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => PcmStreamPlanner.Plan(null!, null, null));
    }

    private static MediaStream Audio(int index)
        => new() { Index = index, Type = MediaStreamType.Audio };

    private static MediaSourceInfo Source(string id, string? path, params MediaStream[] streams)
        => new()
        {
            Id = id,
            Path = path!,
            Protocol = MediaProtocol.File,
            MediaStreams = streams.Length == 0 ? new List<MediaStream>() : streams,
        };
}
