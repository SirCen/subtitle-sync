using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;

namespace Jellyfin.Plugin.SubtitleSync.MediaEncoding;

/// <summary>
/// Turns "this item, maybe this source, maybe this track" into a concrete
/// decision about what ffmpeg should read.
/// </summary>
/// <remarks>
/// Pure: it takes the media sources the library already resolved and returns a
/// <see cref="PcmStreamPlan"/>. No item lookup, no filesystem, no encoder. All of
/// the fiddly selection rules - which of several versions, which of several
/// dubs, what the index in the query string even means - are therefore covered
/// by fast tests rather than by decoding an hour of audio against a live server.
/// </remarks>
public static class PcmStreamPlanner
{
    /// <summary>
    /// Chooses the media source and audio stream to extract.
    /// </summary>
    /// <param name="mediaSources">The item's media sources, in Jellyfin's order.</param>
    /// <param name="mediaSourceId">
    /// The requested source, or null to take the first - which is the version
    /// Jellyfin itself plays by default.
    /// </param>
    /// <param name="audioStreamIndex">
    /// The requested stream's absolute container index (Jellyfin's
    /// <see cref="MediaStream.Index"/>), or null to pick the source's default
    /// audio track.
    /// </param>
    /// <returns>A usable plan, or a failure carrying the reason and a message.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="mediaSources"/> is null.</exception>
    public static PcmStreamPlan Plan(
        IReadOnlyList<MediaSourceInfo> mediaSources,
        string? mediaSourceId,
        int? audioStreamIndex)
    {
        ArgumentNullException.ThrowIfNull(mediaSources);

        var source = SelectSource(mediaSources, mediaSourceId);
        if (source is null)
        {
            return string.IsNullOrEmpty(mediaSourceId)
                ? PcmStreamPlan.Failed(
                    PcmStreamPlanFailure.NoMediaSource,
                    "The item has no media sources to extract audio from.")
                : PcmStreamPlan.Failed(
                    PcmStreamPlanFailure.UnknownMediaSource,
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "The item has no media source with id '{0}'.",
                        mediaSourceId));
        }

        if (string.IsNullOrWhiteSpace(source.Path))
        {
            return PcmStreamPlan.Failed(
                PcmStreamPlanFailure.MissingPath,
                "The selected media source has no path on disk.");
        }

        if (source.Protocol != MediaProtocol.File)
        {
            // The argument builder whitelists the file protocol only, so this
            // would fail inside ffmpeg anyway. Failing here says why.
            return PcmStreamPlan.Failed(
                PcmStreamPlanFailure.UnsupportedProtocol,
                string.Format(
                    CultureInfo.InvariantCulture,
                    "The selected media source uses the {0} protocol; only local files can be extracted.",
                    source.Protocol));
        }

        var allStreams = source.MediaStreams ?? Array.Empty<MediaStream>();

        var audioStreams = allStreams
            .Where(stream => stream.Type == MediaStreamType.Audio)
            .ToList();

        if (audioStreams.Count == 0)
        {
            return PcmStreamPlan.Failed(
                PcmStreamPlanFailure.NoAudioStream,
                "The selected media source has no audio stream.");
        }

        var audioStream = SelectAudioStream(source, audioStreams, audioStreamIndex);
        if (audioStream is null)
        {
            return PcmStreamPlan.Failed(
                PcmStreamPlanFailure.UnknownAudioStream,
                string.Format(
                    CultureInfo.InvariantCulture,
                    "The selected media source has no audio stream at index {0}.",
                    audioStreamIndex));
        }

        if (audioStream.IsExternal)
        {
            return PcmStreamPlan.Failed(
                PcmStreamPlanFailure.ExternalAudioStream,
                "The selected audio stream is an external file, which this endpoint cannot decode.");
        }

        return PcmStreamPlan.Success(
            source.Path,
            string.IsNullOrEmpty(source.Id) ? mediaSourceId : source.Id,
            audioStream.Index,
            ToContainerIndex(allStreams, audioStream),
            EstimateByteLength(source.RunTimeTicks));
    }

    /// <summary>
    /// Translates a Jellyfin stream index into the index of the same stream
    /// inside the media container.
    /// </summary>
    /// <param name="allStreams">Every stream on the media source.</param>
    /// <param name="target">The stream to locate. Must not be external.</param>
    /// <returns>The container index, suitable for <c>ffmpeg -map 0:N</c>.</returns>
    /// <remarks>
    /// Jellyfin numbers external streams alongside the container's own, so the
    /// two numberings diverge as soon as a sibling subtitle file exists. See
    /// <see cref="PcmStreamPlan.ContainerAudioStreamIndex"/> for the fixture that
    /// makes this concrete. Ordering by index reproduces the probe order, which
    /// is the container order.
    /// </remarks>
    private static int ToContainerIndex(IReadOnlyList<MediaStream> allStreams, MediaStream target)
    {
        var containerIndex = 0;

        foreach (var stream in allStreams.Where(s => !s.IsExternal).OrderBy(s => s.Index))
        {
            if (stream.Index == target.Index)
            {
                return containerIndex;
            }

            containerIndex++;
        }

        // Unreachable for a non-external stream taken from this same list, but
        // falling back to the Jellyfin index is the behaviour that is right when
        // there are no external streams at all.
        return target.Index;
    }

    /// <summary>
    /// Estimates how many PCM bytes a runtime decodes to.
    /// </summary>
    /// <param name="runTimeTicks">The runtime in ticks, or null if unknown.</param>
    /// <returns>
    /// The approximate byte count, rounded down to a whole sample, or null when
    /// the runtime is unknown or not positive.
    /// </returns>
    /// <remarks>
    /// Only ever an estimate. See <see cref="PcmStreamPlan.EstimatedByteLength"/>
    /// for why it must not become a <c>Content-Length</c>. Rounded to a whole
    /// sample so a consumer dividing by two never sees a stray half-sample.
    /// </remarks>
    public static long? EstimateByteLength(long? runTimeTicks)
    {
        if (runTimeTicks is not > 0)
        {
            return null;
        }

        var seconds = runTimeTicks.Value / (double)TimeSpan.TicksPerSecond;
        var bytes = seconds * FfmpegArguments.SampleRate * FfmpegArguments.Channels * FfmpegArguments.BytesPerSample;

        if (bytes >= long.MaxValue)
        {
            return null;
        }

        var whole = (long)bytes;
        return whole - (whole % FfmpegArguments.BytesPerSample);
    }

    private static MediaSourceInfo? SelectSource(IReadOnlyList<MediaSourceInfo> mediaSources, string? mediaSourceId)
    {
        if (string.IsNullOrEmpty(mediaSourceId))
        {
            return mediaSources.Count > 0 ? mediaSources[0] : null;
        }

        // Jellyfin renders source ids as a 32-character "N" format GUID, and
        // clients round-trip them as they were given, but casing has bitten
        // enough Jellyfin plugins to be worth not caring about.
        return mediaSources.FirstOrDefault(
            source => string.Equals(source.Id, mediaSourceId, StringComparison.OrdinalIgnoreCase));
    }

    private static MediaStream? SelectAudioStream(
        MediaSourceInfo source,
        List<MediaStream> audioStreams,
        int? audioStreamIndex)
    {
        if (audioStreamIndex is int requested)
        {
            // Absolute container index, so a caller passing "1" for "the second
            // audio track" of a file whose stream 1 is audio and stream 0 is
            // video gets stream 1. That matches MediaStream.Index, which is what
            // every Jellyfin client already holds.
            return audioStreams.FirstOrDefault(stream => stream.Index == requested);
        }

        if (source.DefaultAudioStreamIndex is int preferred)
        {
            var match = audioStreams.FirstOrDefault(stream => stream.Index == preferred);
            if (match is not null)
            {
                return match;
            }
        }

        return audioStreams.FirstOrDefault(stream => stream.IsDefault) ?? audioStreams[0];
    }
}
