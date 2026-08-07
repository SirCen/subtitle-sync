using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.SubtitleSync.Api;

/// <summary>
/// Turns Jellyfin's media model into the response models the plugin page reads.
/// </summary>
/// <remarks>
/// <para>
/// Kept pure and separate from the controller because this is where the real
/// decisions live - which tracks are offered, which are disabled and why - and
/// they are worth testing without a running server. <see cref="MediaStream"/>
/// and <see cref="MediaSourceInfo"/> are plain model types, so a test can build
/// exactly the awkward stream it wants.
/// </para>
/// <para>
/// It also stops Jellyfin's model leaking into the wire format. The page is
/// versioned with the plugin; the server's DTOs are not ours to promise.
/// </para>
/// </remarks>
public static class MediaStreamMapper
{
    /// <summary>
    /// Maps one subtitle stream, classifying it on the way.
    /// </summary>
    /// <param name="stream">The subtitle stream.</param>
    /// <param name="mediaSourceId">The id of the source the index is relative to.</param>
    /// <returns>The response model.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is null.</exception>
    public static SubtitleTrackResponse ToSubtitleTrack(MediaStream stream, string mediaSourceId)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var support = SubtitleCodecs.Classify(stream.Codec);
        var stylingIsLost = SubtitleCodecs.StylingIsLostInSrt(stream.Codec);

        return new SubtitleTrackResponse
        {
            Index = stream.Index,
            MediaSourceId = mediaSourceId ?? string.Empty,
            Language = NullIfBlank(stream.Language),
            Codec = NullIfBlank(stream.Codec),
            Title = NullIfBlank(stream.Title),
            DisplayTitle = NullIfBlank(stream.DisplayTitle),
            IsExternal = stream.IsExternal,

            // Only ever surfaced for an external track. An embedded stream can
            // carry the container's own path here, and echoing that as though it
            // were a subtitle file would have the save step (#8) offering to
            // overwrite the video.
            Path = stream.IsExternal ? NullIfBlank(stream.Path) : null,
            IsDefault = stream.IsDefault,
            IsForced = stream.IsForced,
            IsHearingImpaired = stream.IsHearingImpaired,
            Support = support,
            CanSync = support != SubtitleTrackSupport.ImageBased,
            StylingWillBeLost = stylingIsLost,
            Note = SubtitleCodecs.DescribeSupport(support, stream.Codec, stylingIsLost),
        };
    }

    /// <summary>
    /// Maps one audio stream.
    /// </summary>
    /// <param name="stream">The audio stream.</param>
    /// <returns>The response model.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is null.</exception>
    public static AudioStreamResponse ToAudioStream(MediaStream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        return new AudioStreamResponse
        {
            Index = stream.Index,
            Language = NullIfBlank(stream.Language),
            Codec = NullIfBlank(stream.Codec),
            Title = NullIfBlank(stream.Title),
            DisplayTitle = NullIfBlank(stream.DisplayTitle),
            Channels = stream.Channels,
            SampleRate = stream.SampleRate,
            IsDefault = stream.IsDefault,
        };
    }

    /// <summary>
    /// Maps one media source and the streams hanging off it.
    /// </summary>
    /// <param name="source">The media source.</param>
    /// <returns>The response model.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> is null.</exception>
    public static MediaSourceResponse ToMediaSource(MediaSourceInfo source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var id = source.Id ?? string.Empty;
        var streams = source.MediaStreams ?? [];

        return new MediaSourceResponse
        {
            Id = id,
            Name = NullIfBlank(source.Name),
            Path = NullIfBlank(source.Path),
            Container = NullIfBlank(source.Container),
            RunTimeTicks = source.RunTimeTicks,
            DefaultAudioStreamIndex = source.DefaultAudioStreamIndex,
            AudioStreams = streams
                .Where(s => s.Type == MediaStreamType.Audio)
                .OrderBy(s => s.Index)
                .Select(ToAudioStream)
                .ToArray(),
            SubtitleStreams = streams
                .Where(s => s.Type == MediaStreamType.Subtitle)
                .OrderBy(s => s.Index)
                .Select(s => ToSubtitleTrack(s, id))
                .ToArray(),
        };
    }

    /// <summary>
    /// Assembles the full item response from already-resolved parts.
    /// </summary>
    /// <remarks>
    /// Takes the item's descriptive fields as arguments rather than a
    /// <see cref="MediaBrowser.Controller.Entities.BaseItem"/> so the shape of
    /// the response can be tested without the server's static wiring. The
    /// controller does the resolving; this does the shaping.
    /// </remarks>
    /// <param name="itemId">The item id.</param>
    /// <param name="name">The item name.</param>
    /// <param name="itemType">The concrete item type name.</param>
    /// <param name="runTimeTicks">The item runtime in ticks, when known.</param>
    /// <param name="sources">The item's media sources.</param>
    /// <param name="seriesName">The series name, for an episode.</param>
    /// <param name="parentIndexNumber">The season number, for an episode.</param>
    /// <param name="indexNumber">The episode number, for an episode.</param>
    /// <returns>The response model.</returns>
    public static ItemResponse ToItem(
        Guid itemId,
        string? name,
        string itemType,
        long? runTimeTicks,
        IEnumerable<MediaSourceInfo> sources,
        string? seriesName = null,
        int? parentIndexNumber = null,
        int? indexNumber = null)
    {
        var mapped = (sources ?? []).Select(ToMediaSource).ToArray();

        return new ItemResponse
        {
            ItemId = itemId,
            Name = NullIfBlank(name),
            ItemType = itemType ?? string.Empty,
            SeriesName = NullIfBlank(seriesName),
            ParentIndexNumber = parentIndexNumber,
            IndexNumber = indexNumber,
            RunTimeTicks = runTimeTicks,
            RunTimeSeconds = runTimeTicks.HasValue
                ? runTimeTicks.Value / (double)TimeSpan.TicksPerSecond
                : null,
            MediaSources = mapped,
            HasSyncableSubtitles = mapped.Any(s => s.SubtitleStreams.Any(t => t.CanSync)),
        };
    }

    /// <summary>
    /// Finds one subtitle track across an item's sources.
    /// </summary>
    /// <remarks>
    /// The page always sends a media source id, but an older page, a hand-rolled
    /// request or a single-version item makes it worth defaulting: with no id
    /// given, the first source that actually has that index wins.
    /// </remarks>
    /// <param name="sources">The item's media sources.</param>
    /// <param name="mediaSourceId">The requested source id, or null for any.</param>
    /// <param name="index">The requested stream index.</param>
    /// <param name="source">The source the track was found in.</param>
    /// <param name="stream">The subtitle stream.</param>
    /// <returns>True when a matching subtitle stream exists.</returns>
    public static bool TryFindSubtitleStream(
        IEnumerable<MediaSourceInfo> sources,
        string? mediaSourceId,
        int index,
        out MediaSourceInfo? source,
        out MediaStream? stream)
    {
        source = null;
        stream = null;

        foreach (var candidate in sources ?? [])
        {
            if (!string.IsNullOrEmpty(mediaSourceId)
                && !string.Equals(candidate.Id, mediaSourceId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var match = (candidate.MediaStreams ?? [])
                .FirstOrDefault(s => s.Type == MediaStreamType.Subtitle && s.Index == index);

            if (match is not null)
            {
                source = candidate;
                stream = match;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Formats a media source id for a log or error message.
    /// </summary>
    /// <param name="mediaSourceId">The id, possibly absent.</param>
    /// <returns>A printable description.</returns>
    internal static string DescribeSource(string? mediaSourceId)
        => string.IsNullOrEmpty(mediaSourceId)
            ? "any media source"
            : string.Create(CultureInfo.InvariantCulture, $"media source '{mediaSourceId}'");

    /// <summary>
    /// Collapses empty and whitespace-only strings to null.
    /// </summary>
    /// <remarks>
    /// ffprobe reports an absent tag as an empty string about as often as it
    /// omits it. Without this the page would need to test for both.
    /// </remarks>
    /// <param name="value">The value.</param>
    /// <returns>The trimmed value, or null.</returns>
    private static string? NullIfBlank(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value;
}
