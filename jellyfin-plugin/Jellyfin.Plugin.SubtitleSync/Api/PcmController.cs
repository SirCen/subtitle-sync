using System;
using System.Globalization;
using System.Net.Mime;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.SubtitleSync.MediaEncoding;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SubtitleSync.Api;

/// <summary>
/// Streams a library item's audio as the raw PCM the sync algorithm consumes.
/// </summary>
/// <remarks>
/// <para>
/// <b>The wire format is headerless 16 kHz mono s16le, from byte zero.</b> No
/// RIFF, no container, nothing to skip. The browser adapter
/// (<c>jellyfin-plugin/web/src/pcmStream.ts</c>) starts decoding samples at the
/// first byte, and the Python oracle reads the same format, so any header added
/// here would be decoded as audio and would silently corrupt every sync rather
/// than fail.
/// </para>
/// <para>
/// <b>There is no <c>Content-Length</c>, on purpose.</b> The obvious formula -
/// runtime times 16000 times 2 - is wrong: container runtime and audio stream
/// duration are different numbers, and a fixture reporting 30.000000 s decodes
/// to 960006 bytes rather than 960000. A <c>Content-Length</c> that disagrees
/// with the body is worse than none, so the response is chunked and the estimate
/// travels in <see cref="EstimatedLengthHeader"/> where it can only ever affect a
/// progress bar. The client already treats a missing length as indeterminate
/// progress.
/// </para>
/// <para>
/// <b>Policy is <see cref="Policies.SubtitleManagement"/>.</b> This reads and
/// analyses; it does not write. It is the same audience Jellyfin already shows
/// "Edit subtitles" to. Only the save endpoint requires elevation.
/// </para>
/// <para>
/// Its own controller class rather than a shared one: the other endpoints have
/// nothing in common with this one's dependencies or its streaming response
/// handling.
/// </para>
/// </remarks>
[ApiController]
[Authorize(Policy = Policies.SubtitleManagement)]
[Route("SubtitleSync")]
public class PcmController : ControllerBase
{
    /// <summary>
    /// Header carrying the approximate total body size in bytes.
    /// </summary>
    /// <remarks>
    /// Advisory only, and deliberately not <c>Content-Length</c>: it is computed
    /// from the container runtime and will differ from the real body by a few
    /// hundred bytes either way. Use it to move a progress bar, never to size a
    /// buffer or to decide the stream is complete.
    /// </remarks>
    public const string EstimatedLengthHeader = "X-SubtitleSync-Estimated-Length";

    /// <summary>
    /// Header carrying the PCM sample rate in hertz.
    /// </summary>
    public const string SampleRateHeader = "X-SubtitleSync-Sample-Rate";

    /// <summary>
    /// Header carrying the PCM channel count.
    /// </summary>
    public const string ChannelsHeader = "X-SubtitleSync-Channels";

    /// <summary>
    /// Header carrying the PCM sample format, as an ffmpeg format name.
    /// </summary>
    public const string SampleFormatHeader = "X-SubtitleSync-Sample-Format";

    /// <summary>
    /// Header carrying the media source id the audio was actually taken from.
    /// </summary>
    public const string MediaSourceIdHeader = "X-SubtitleSync-Media-Source-Id";

    /// <summary>
    /// Header carrying the container index of the audio stream that was decoded.
    /// </summary>
    public const string AudioStreamIndexHeader = "X-SubtitleSync-Audio-Stream-Index";

    private static readonly Action<ILogger, Guid, Exception?> _logDisconnected =
        LoggerMessage.Define<Guid>(
            LogLevel.Debug,
            new EventId(1, nameof(GetPcm)),
            "PCM stream for item {ItemId} was abandoned by the client");

    private static readonly Action<ILogger, Guid, Exception?> _logFailed =
        LoggerMessage.Define<Guid>(
            LogLevel.Error,
            new EventId(2, nameof(GetPcm)),
            "PCM extraction failed for item {ItemId}");

    private readonly ILibraryManager _libraryManager;
    private readonly IMediaSourceManager _mediaSourceManager;
    private readonly FfmpegPcmStreamer _streamer;
    private readonly ILogger<PcmController> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="PcmController"/> class.
    /// </summary>
    /// <param name="libraryManager">Library, for the item lookup.</param>
    /// <param name="mediaSourceManager">Media sources, for paths and streams.</param>
    /// <param name="mediaEncoder">The server's encoder, for its ffmpeg path.</param>
    /// <param name="loggerFactory">Logger factory.</param>
    /// <remarks>
    /// Every parameter is a service the server itself registers, and the
    /// <see cref="FfmpegPcmStreamer"/> is built here rather than injected. That is
    /// deliberate: controllers are resolved from the DI container
    /// (<c>AddControllersAsServices</c>), so a plugin-owned dependency would need
    /// an <c>IPluginServiceRegistrator</c> entry or this endpoint would fail at
    /// request time with a resolution error rather than at startup.
    /// </remarks>
    public PcmController(
        ILibraryManager libraryManager,
        IMediaSourceManager mediaSourceManager,
        IMediaEncoder mediaEncoder,
        ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(loggerFactory);

        _libraryManager = libraryManager;
        _mediaSourceManager = mediaSourceManager;
        _streamer = new FfmpegPcmStreamer(mediaEncoder, loggerFactory.CreateLogger<FfmpegPcmStreamer>());
        _logger = loggerFactory.CreateLogger<PcmController>();
    }

    /// <summary>
    /// Streams an item's audio as headerless 16 kHz mono s16le PCM.
    /// </summary>
    /// <param name="itemId">The library item.</param>
    /// <param name="mediaSourceId">
    /// Which version to decode. Omit for the first, which is the one Jellyfin
    /// plays by default.
    /// </param>
    /// <param name="audioStreamIndex">
    /// The absolute container index of the audio track (Jellyfin's
    /// <c>MediaStream.Index</c>, not an audio-only ordinal). Omit for the
    /// source's default track.
    /// </param>
    /// <param name="cancellationToken">
    /// Bound by MVC to <see cref="HttpContext.RequestAborted"/>, so a client that
    /// disconnects kills the ffmpeg process rather than leaving it decoding to
    /// nowhere.
    /// </param>
    /// <returns>The PCM body, or a problem response if nothing can be decoded.</returns>
    /// <response code="200">Raw s16le PCM, chunked.</response>
    /// <response code="400">The requested source or audio stream is unusable.</response>
    /// <response code="404">No such item, or it has nothing to decode.</response>
    [HttpGet("Pcm/{itemId}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]

    // Deliberately no [Produces]: constraining the action to octet-stream also
    // constrains its error responses, and MVC cannot format a message string as
    // octet-stream, so every 400 and 404 came back as an empty 406 instead.
    // Observed against 10.11.11. The success path sets Content-Type itself.
    public async Task<ActionResult> GetPcm(
        [FromRoute] Guid itemId,
        [FromQuery] string? mediaSourceId,
        [FromQuery] int? audioStreamIndex,
        CancellationToken cancellationToken)
    {
        var item = _libraryManager.GetItemById(itemId);
        if (item is null)
        {
            return NotFound(string.Format(CultureInfo.InvariantCulture, "No item with id {0}.", itemId));
        }

        var plan = PcmStreamPlanner.Plan(
            _mediaSourceManager.GetStaticMediaSources(item, false),
            mediaSourceId,
            audioStreamIndex);

        if (!plan.Succeeded)
        {
            return plan.Failure switch
            {
                PcmStreamPlanFailure.NoMediaSource or PcmStreamPlanFailure.UnknownMediaSource
                    => NotFound(plan.ErrorMessage),
                _ => BadRequest(plan.ErrorMessage),
            };
        }

        var request = new FfmpegPcmRequest
        {
            InputPath = plan.InputPath,
            // The container index, not the Jellyfin one: an external subtitle
            // beside the file shifts Jellyfin's numbering off the container's.
            AudioStreamIndex = plan.ContainerAudioStreamIndex,
        };

        // Everything that can fail cheaply has failed by now, so it is safe to
        // commit to a 200 and start writing.
        Response.ContentType = MediaTypeNames.Application.Octet;
        WriteFormatHeaders(Response, plan);

        try
        {
            await Response.StartAsync(cancellationToken).ConfigureAwait(false);
            await _streamer.StreamAsync(request, Response.Body, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The normal end of an abandoned request. ffmpeg is already dead;
            // there is nobody left to tell.
            _logDisconnected(_logger, itemId, null);
        }
        catch (FfmpegExecutionException ex)
        {
            // The response is already committed, so this cannot become a 500. The
            // client sees a reset connection mid-body, which is at least
            // distinguishable from a short but complete stream.
            _logFailed(_logger, itemId, ex);
            HttpContext.Abort();
        }

        return new EmptyResult();
    }

    /// <summary>
    /// Writes the advisory headers describing the body that is about to follow.
    /// </summary>
    /// <remarks>
    /// The format headers are constants, not negotiation: they let the client
    /// assert the contract it decodes against instead of assuming it.
    /// </remarks>
    private static void WriteFormatHeaders(HttpResponse response, PcmStreamPlan plan)
    {
        var headers = response.Headers;

        headers[SampleRateHeader] = FfmpegArguments.SampleRate.ToString(CultureInfo.InvariantCulture);
        headers[ChannelsHeader] = FfmpegArguments.Channels.ToString(CultureInfo.InvariantCulture);
        headers[SampleFormatHeader] = FfmpegArguments.SampleFormat;
        headers[AudioStreamIndexHeader] = plan.AudioStreamIndex.ToString(CultureInfo.InvariantCulture);

        if (!string.IsNullOrEmpty(plan.MediaSourceId))
        {
            headers[MediaSourceIdHeader] = plan.MediaSourceId;
        }

        if (plan.EstimatedByteLength is long estimated)
        {
            headers[EstimatedLengthHeader] = estimated.ToString(CultureInfo.InvariantCulture);
        }
    }
}
