using System;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Linq;
using System.Net.Mime;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SubtitleSync.Api;

/// <summary>
/// Read-only endpoints backing the plugin's sync page: what tracks does this
/// item have, and give me one of them as SRT.
/// </summary>
/// <remarks>
/// <para>
/// Controllers are auto-discovered - the server adds every plugin assembly as
/// an MVC application part - so nothing here needs registering. It is resolved
/// from the DI container, and every service it takes is a core singleton
/// (<c>Emby.Server.Implementations/ApplicationHost.cs</c> at v10.11.11), so no
/// <c>IPluginServiceRegistrator</c> entry is required either.
/// </para>
/// <para>
/// The policy is <see cref="Policies.SubtitleManagement"/>, the same permission
/// Jellyfin gates its own "Edit subtitles" affordance on. Not
/// <c>RequiresElevation</c>: these two endpoints only read, and the users who
/// can already manage subtitles are exactly the set the injected menu item is
/// shown to. The write path (#8) is gated harder, separately.
/// </para>
/// </remarks>
[ApiController]
[Authorize(Policy = Policies.SubtitleManagement)]
[Route("SubtitleSync")]
public partial class SubtitleSyncItemController : ControllerBase
{
    /// <summary>
    /// The claim the server puts the authenticated user's id in.
    /// <c>Jellyfin.Api.Constants.InternalClaimTypes.UserId</c> at v10.11.11.
    /// Spelled out because <c>Jellyfin.Api</c> is not a package a plugin can
    /// reference, so neither the constant nor <c>User.GetUserId()</c> is
    /// reachable from here.
    /// </summary>
    private const string UserIdClaim = "Jellyfin-UserId";

    /// <summary>
    /// The output format asked of <see cref="ISubtitleEncoder"/>.
    /// <c>MediaBrowser.Model.MediaInfo.SubtitleFormat.SRT</c>.
    /// </summary>
    private const string SrtFormat = "srt";

    /// <summary>
    /// SRT has no registered media type. <c>text/plain</c> keeps it readable in
    /// a browser and in curl, and the page reads it with <c>response.text()</c>
    /// either way. The charset is explicit because the encoder always writes
    /// UTF-8, whatever the source file's encoding was.
    /// </summary>
    private const string SrtContentType = "text/plain; charset=utf-8";

    private readonly ILibraryManager _libraryManager;
    private readonly IMediaSourceManager _mediaSourceManager;
    private readonly ISubtitleEncoder _subtitleEncoder;
    private readonly ILogger<SubtitleSyncItemController> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="SubtitleSyncItemController"/> class.
    /// </summary>
    /// <param name="libraryManager">Resolves an item id, honouring what the user may see.</param>
    /// <param name="mediaSourceManager">Lists an item's versions and their streams.</param>
    /// <param name="subtitleEncoder">Converts a track to SRT, external or embedded.</param>
    /// <param name="logger">Logger.</param>
    public SubtitleSyncItemController(
        ILibraryManager libraryManager,
        IMediaSourceManager mediaSourceManager,
        ISubtitleEncoder subtitleEncoder,
        ILogger<SubtitleSyncItemController> logger)
    {
        _libraryManager = libraryManager;
        _mediaSourceManager = mediaSourceManager;
        _subtitleEncoder = subtitleEncoder;
        _logger = logger;
    }

    /// <summary>
    /// Describes an item's media versions, audio tracks and subtitle tracks.
    /// </summary>
    /// <param name="itemId">The item to describe.</param>
    /// <returns>The item description.</returns>
    /// <response code="200">The item was found and described.</response>
    /// <response code="400">The item is not a playable video, for example a series or a season.</response>
    /// <response code="404">No such item, or the user cannot see it.</response>
    [HttpGet("Item/{itemId}")]
    [Produces(MediaTypeNames.Application.Json)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<ItemResponse> GetItem([FromRoute, Required] Guid itemId)
    {
        var item = FindItem(itemId);
        if (item is null)
        {
            return NotFoundItem(itemId);
        }

        if (item is not Video video)
        {
            // A series or a season has no audio to analyse and no streams of its
            // own. Saying so beats returning an empty track list, which reads
            // like "this episode has no subtitles".
            return Problem(
                detail: string.Create(
                    CultureInfo.InvariantCulture,
                    $"'{item.Name}' is a {item.GetType().Name}, which has no media of its own. Open a specific episode or movie."),
                statusCode: StatusCodes.Status400BadRequest,
                title: "Not a playable item");
        }

        // enablePathSubstitution false: we want the server's real paths, since
        // the save step writes beside them.
        var sources = _mediaSourceManager.GetStaticMediaSources(video, false);

        var episode = item as MediaBrowser.Controller.Entities.TV.Episode;

        var response = MediaStreamMapper.ToItem(
            video.Id,
            video.Name,
            video.GetType().Name,
            video.RunTimeTicks,
            sources,
            episode?.SeriesName,
            video.ParentIndexNumber,
            video.IndexNumber);

        LogDescribedItem(
            itemId,
            response.MediaSources.Count,
            response.MediaSources.Sum(s => s.SubtitleStreams.Count));

        return response;
    }

    /// <summary>
    /// Returns one subtitle track converted to SRT.
    /// </summary>
    /// <remarks>
    /// Calls <see cref="ISubtitleEncoder"/> in process rather than proxying
    /// Jellyfin's own <c>Videos/.../Stream.srt</c> endpoint. That endpoint
    /// carries no <c>[Authorize]</c> attribute at all in 10.11, and proxying it
    /// would mean a self-request with a token round-trip for something the DI
    /// container already offers as a singleton. The encoder handles external
    /// files and embedded streams the same way, extracting with ffmpeg when it
    /// has to.
    /// </remarks>
    /// <param name="itemId">The item owning the track.</param>
    /// <param name="index">The subtitle stream index, relative to its media source.</param>
    /// <param name="mediaSourceId">
    /// The media source the index belongs to. Optional, but send it: for an item
    /// with several versions, omitting it takes the first source carrying that
    /// index, which may not be the one the page is showing.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The track as SRT text.</returns>
    /// <response code="200">The track was converted.</response>
    /// <response code="400">The item is not a video, or the track is an image-based format that has no text.</response>
    /// <response code="404">No such item or no such subtitle stream.</response>
    [HttpGet("Subtitle/{itemId}")]
    [Produces(SrtContentType)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> GetSubtitle(
        [FromRoute, Required] Guid itemId,
        [FromQuery, Required] int index,
        [FromQuery] string? mediaSourceId,
        CancellationToken cancellationToken)
    {
        var item = FindItem(itemId);
        if (item is null)
        {
            return NotFoundItem(itemId);
        }

        if (item is not Video video)
        {
            return Problem(
                detail: string.Create(
                    CultureInfo.InvariantCulture,
                    $"'{item.Name}' is a {item.GetType().Name}, which has no media of its own."),
                statusCode: StatusCodes.Status400BadRequest,
                title: "Not a playable item");
        }

        var sources = _mediaSourceManager.GetStaticMediaSources(video, false);

        if (!MediaStreamMapper.TryFindSubtitleStream(sources, mediaSourceId, index, out var source, out var stream)
            || source is null
            || stream is null)
        {
            return Problem(
                detail: string.Create(
                    CultureInfo.InvariantCulture,
                    $"No subtitle stream with index {index} on {MediaStreamMapper.DescribeSource(mediaSourceId)} of '{item.Name}'."),
                statusCode: StatusCodes.Status404NotFound,
                title: "Subtitle stream not found");
        }

        // The same classification the item response already advertised. Checked
        // again because a client is free to ask for anything, and because
        // handing a PGS stream to the encoder wastes an ffmpeg run to arrive at
        // a worse error.
        var support = SubtitleCodecs.Classify(stream.Codec);
        if (support == SubtitleTrackSupport.ImageBased)
        {
            return Problem(
                detail: SubtitleCodecs.DescribeSupport(support, stream.Codec, stylingIsLost: false),
                statusCode: StatusCodes.Status400BadRequest,
                title: "Unsupported subtitle format");
        }

        LogConvertingStream(index, stream.Codec, stream.IsExternal, itemId);

        // startTimeTicks 0 with endTimeTicks 0 means "the whole track": the
        // encoder only filters events when an end time is given.
        var srt = await _subtitleEncoder.GetSubtitles(
                video,
                source.Id,
                index,
                SrtFormat,
                startTimeTicks: 0,
                endTimeTicks: 0,
                preserveOriginalTimestamps: true,
                cancellationToken)
            .ConfigureAwait(false);

        return File(srt, SrtContentType);
    }

    /// <summary>
    /// Resolves an item id against what the caller is allowed to see.
    /// </summary>
    /// <remarks>
    /// The user-scoped overload is what the core controllers use, and it is the
    /// difference between "you cannot see it" answering 404 and this endpoint
    /// becoming a way to enumerate a parentally-restricted library. An API key
    /// has no user, so it falls back to the unscoped lookup.
    /// </remarks>
    /// <param name="itemId">The item id.</param>
    /// <returns>The item, or null.</returns>
    private BaseItem? FindItem(Guid itemId)
    {
        if (itemId.Equals(Guid.Empty))
        {
            return null;
        }

        var userId = CurrentUserId();

        try
        {
            return userId.Equals(Guid.Empty)
                ? _libraryManager.GetItemById<BaseItem>(itemId)
                : _libraryManager.GetItemById<BaseItem>(itemId, userId);
        }
        catch (InvalidOperationException ex)
        {
            // Observed against 10.11.11: a row whose stored type the server can
            // no longer resolve - left behind by an uninstalled plugin, or a
            // internal row such as the all-zeros-but-one id - throws "Cannot
            // deserialize unknown type" out of BaseItemRepository rather than
            // returning null. Jellyfin's own /Items/{id} answers 500 for the
            // same id. From the caller's point of view the item is not there,
            // so say that instead of leaking a stack trace.
            LogItemNotDeserializable(itemId, ex);
            return null;
        }
    }

    /// <summary>
    /// Reads the authenticated user id out of the request claims.
    /// </summary>
    /// <returns>The user id, or <see cref="Guid.Empty"/> for an API key.</returns>
    private Guid CurrentUserId()
    {
        var value = User?.FindFirst(UserIdClaim)?.Value;

        return Guid.TryParse(value, out var userId) ? userId : Guid.Empty;
    }

    /// <summary>
    /// The 404 for an item that does not exist or is not visible. Deliberately
    /// the same answer for both.
    /// </summary>
    /// <param name="itemId">The requested id.</param>
    /// <returns>A 404 result.</returns>
    private ObjectResult NotFoundItem(Guid itemId)
        => Problem(
            detail: string.Create(CultureInfo.InvariantCulture, $"No item with id {itemId:N} is available to you."),
            statusCode: StatusCodes.Status404NotFound,
            title: "Item not found");

    /// <summary>
    /// Logs the outcome of an item description.
    /// </summary>
    /// <remarks>
    /// Source-generated rather than a plain <c>LogDebug</c> call because the
    /// project builds with <c>AnalysisMode=AllEnabledByDefault</c>, and CA1848
    /// wants the allocation-free delegate form.
    /// </remarks>
    /// <param name="itemId">The item.</param>
    /// <param name="sourceCount">How many media versions it has.</param>
    /// <param name="trackCount">How many subtitle tracks across all of them.</param>
    [LoggerMessage(
        EventId = 7001,
        Level = LogLevel.Debug,
        Message = "Described item {ItemId}: {SourceCount} media source(s), {TrackCount} subtitle track(s)")]
    private partial void LogDescribedItem(Guid itemId, int sourceCount, int trackCount);

    /// <summary>
    /// Logs a conversion about to be handed to the subtitle encoder.
    /// </summary>
    /// <param name="index">The stream index.</param>
    /// <param name="codec">The source codec.</param>
    /// <param name="isExternal">Whether the track is a sidecar file.</param>
    /// <param name="itemId">The item.</param>
    [LoggerMessage(
        EventId = 7002,
        Level = LogLevel.Debug,
        Message = "Converting subtitle stream {Index} ({Codec}, external={IsExternal}) of item {ItemId} to SRT")]
    private partial void LogConvertingStream(int index, string? codec, bool isExternal, Guid itemId);

    /// <summary>
    /// Logs a library row the server could not turn back into an item.
    /// </summary>
    /// <param name="itemId">The item.</param>
    /// <param name="exception">The repository failure.</param>
    [LoggerMessage(
        EventId = 7003,
        Level = LogLevel.Warning,
        Message = "Item {ItemId} exists in the library database but could not be deserialised; answering as not found")]
    private partial void LogItemNotDeserializable(Guid itemId, Exception exception);
}
