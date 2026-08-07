using System;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.IO;
using System.Net.Mime;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.SubtitleSync.Paths;
using Jellyfin.Plugin.SubtitleSync.Subtitles;
using Jellyfin.Plugin.SubtitleSync.Writing;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.MediaInfo;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SubtitleSync.Api;

/// <summary>
/// The one endpoint in this plugin that writes into a user's media library.
/// </summary>
/// <remarks>
/// <para>
/// <b>The policy is <see cref="Policies.RequiresElevation"/>, and that is not an
/// oversight.</b> Every other endpoint uses
/// <see cref="Policies.SubtitleManagement"/>, matching the permission Jellyfin
/// gates its own "Edit subtitles" affordance on. This one creates a file inside
/// a library folder, which is a different kind of act from reading one, so it is
/// admin-only. The split is deliberate; see issue #8.
/// </para>
/// <para>
/// <b>Nothing about the destination comes from the request.</b> The caller names
/// an item, a media source and a subtitle stream index, and nothing else. The
/// media path, the source subtitle path and the language are all read from the
/// server's own view of that item, so there is no string in the request that
/// could become part of a path. The overwrite setting comes from the plugin
/// configuration, not the body: a client cannot ask to replace a user's file.
/// </para>
/// <para>
/// <b>The body is raw SRT, not JSON.</b> A model-bound property would be
/// buffered and deserialised before any of this code ran, which would put the
/// size cap after the allocation rather than before it. Reading the stream by
/// hand is what makes <see cref="SrtValidator.MaxBytes"/> enforceable. The same
/// reasoning as the signal cache endpoint.
/// </para>
/// </remarks>
[ApiController]
[Authorize(Policy = Policies.RequiresElevation)]
[Route("SubtitleSync")]
public partial class SubtitleSyncSaveController : ControllerBase
{
    /// <summary>
    /// The claim the server puts the authenticated user's id in.
    /// <c>Jellyfin.Api.Constants.InternalClaimTypes.UserId</c> at v10.11.11.
    /// Spelled out because <c>Jellyfin.Api</c> is not a package a plugin can
    /// reference.
    /// </summary>
    private const string UserIdClaim = "Jellyfin-UserId";

    /// <summary>
    /// SRT has no registered media type, and the page posts
    /// <c>text/plain;charset=utf-8</c>.
    /// </summary>
    private const string SrtContentType = "text/plain";

    /// <summary>
    /// UTF-8 that refuses malformed input rather than substituting U+FFFD.
    /// </summary>
    /// <remarks>
    /// A replacement character silently written into a subtitle is a corruption
    /// the user only finds months later. Better to refuse the request and say
    /// the encoding was wrong.
    /// </remarks>
    private static readonly UTF8Encoding _strictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private readonly ILibraryManager _libraryManager;
    private readonly IMediaSourceManager _mediaSourceManager;
    private readonly IProviderManager _providerManager;
    private readonly IFileSystem _fileSystem;
    private readonly ILogger<SubtitleSyncSaveController> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="SubtitleSyncSaveController"/> class.
    /// </summary>
    /// <param name="libraryManager">Resolves the item id.</param>
    /// <param name="mediaSourceManager">Supplies the item's real file paths and streams.</param>
    /// <param name="providerManager">Queues the refresh that makes the new file appear.</param>
    /// <param name="fileSystem">Backs the <see cref="DirectoryService"/> the refresh needs.</param>
    /// <param name="logger">Logger.</param>
    /// <remarks>
    /// Every dependency is a core singleton, so no
    /// <c>IPluginServiceRegistrator</c> entry is needed for this controller.
    /// </remarks>
    public SubtitleSyncSaveController(
        ILibraryManager libraryManager,
        IMediaSourceManager mediaSourceManager,
        IProviderManager providerManager,
        IFileSystem fileSystem,
        ILogger<SubtitleSyncSaveController> logger)
    {
        _libraryManager = libraryManager;
        _mediaSourceManager = mediaSourceManager;
        _providerManager = providerManager;
        _fileSystem = fileSystem;
        _logger = logger;
    }

    /// <summary>
    /// Writes a synced subtitle beside an item's media file and asks Jellyfin to
    /// index it.
    /// </summary>
    /// <param name="itemId">The item the subtitle belongs to.</param>
    /// <param name="index">
    /// The index of the subtitle stream the sync was derived from. Used for the
    /// output's language and, when the overwrite setting is on, to decide which
    /// file may be replaced.
    /// </param>
    /// <param name="mediaSourceId">
    /// The media source the index belongs to. Optional, but send it: for an item
    /// with several versions, omitting it takes the first source carrying that
    /// index, which may not be the one the page is showing.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Where the subtitle was written.</returns>
    /// <response code="200">The file is on disk and a refresh was queued.</response>
    /// <response code="400">The body is not valid SRT, or the item is not a playable local file.</response>
    /// <response code="404">No such item, or no such subtitle stream on it.</response>
    /// <response code="409">The library folder would not accept the file. Read-only mount, permissions, or a missing path.</response>
    /// <response code="413">The body is larger than the maximum accepted subtitle.</response>
    [HttpPost("Save/{itemId}")]
    [Consumes(SrtContentType)]
    [Produces(MediaTypeNames.Application.Json)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status413PayloadTooLarge)]
    public async Task<ActionResult<SubtitleSaveResponse>> Save(
        [FromRoute, Required] Guid itemId,
        [FromQuery, Required] int index,
        [FromQuery] string? mediaSourceId,
        CancellationToken cancellationToken)
    {
        var item = FindItem(itemId);
        if (item is null)
        {
            return Problem(
                detail: string.Create(CultureInfo.InvariantCulture, $"No item with id {itemId:N} is available to you."),
                statusCode: StatusCodes.Status404NotFound,
                title: "Item not found");
        }

        if (item is not Video video)
        {
            return Problem(
                detail: string.Create(
                    CultureInfo.InvariantCulture,
                    $"'{item.Name}' is a {item.GetType().Name}, which has no media of its own. Open a specific episode or movie."),
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

        // A stream, a DVD folder or anything else that is not a plain file on
        // this server has no folder to write a sibling into. Checked before the
        // body is read so a bad request is refused cheaply.
        if (source.Protocol != MediaProtocol.File || string.IsNullOrWhiteSpace(source.Path))
        {
            return Problem(
                detail: string.Create(
                    CultureInfo.InvariantCulture,
                    $"'{item.Name}' is not a local file ({source.Protocol}), so there is no folder to write a subtitle into."),
                statusCode: StatusCodes.Status400BadRequest,
                title: "Not a local media file");
        }

        var body = await ReadBodyAsync(cancellationToken).ConfigureAwait(false);
        if (body is null)
        {
            LogRejectedOversizeBody(itemId);
            return Problem(
                detail: string.Create(
                    CultureInfo.InvariantCulture,
                    $"A subtitle may be at most {SrtValidator.MaxBytes} bytes."),
                statusCode: StatusCodes.Status413PayloadTooLarge,
                title: "Subtitle too large");
        }

        string text;
        try
        {
            text = _strictUtf8.GetString(body);
        }
        catch (DecoderFallbackException)
        {
            return Problem(
                detail: "The body is not valid UTF-8. Convert the subtitle to UTF-8 before saving it.",
                statusCode: StatusCodes.Status400BadRequest,
                title: "Not UTF-8");
        }

        var validation = SrtValidator.Validate(text);
        if (!validation.IsValid)
        {
            LogRejectedContent(itemId, validation.Error);
            return Problem(
                detail: validation.ErrorMessage,
                statusCode: StatusCodes.Status400BadRequest,
                title: "Not a valid SRT file");
        }

        // The only inputs to the destination decision, all of them the server's
        // own. stream.Path is used only when Jellyfin itself says the track is
        // external; an embedded stream can carry the container's path, and
        // treating that as a subtitle file would offer to overwrite the video.
        var request = new SubtitleOutputRequest
        {
            MediaPath = source.Path,
            Language = stream.Language,
            Source = stream.IsExternal && !string.IsNullOrWhiteSpace(stream.Path)
                ? SubtitleSource.External(stream.Path)
                : SubtitleSource.Embedded(),
            OverwriteOriginal = Plugin.Instance?.Configuration.OverwriteOriginal ?? false,
        };

        var written = await SyncedSubtitleWriter.WriteAsync(request, validation.NormalisedText, cancellationToken)
            .ConfigureAwait(false);

        if (!written.Succeeded)
        {
            LogWriteFailed(itemId, written.Failure, written.ErrorMessage);
            return Problem(
                detail: written.ErrorMessage,
                statusCode: StatusFor(written.Failure),
                title: TitleFor(written.Failure));
        }

        LogSaved(itemId, written.OutputPath, written.BytesWritten, written.OverwroteSource);

        var refreshQueued = QueueRefresh(video);

        return new SubtitleSaveResponse
        {
            Path = written.OutputPath,
            FileName = Path.GetFileName(written.OutputPath),
            Language = written.Language,
            OverwroteSource = written.OverwroteSource,
            Bytes = written.BytesWritten,
            CueCount = validation.CueCount,
            RefreshQueued = refreshQueued,
        };
    }

    /// <summary>
    /// Asks the server to re-probe the item so the new sibling becomes a track.
    /// </summary>
    /// <remarks>
    /// The sanctioned pattern, copied from what Jellyfin itself does after
    /// downloading a remote subtitle (<c>SubtitleController.DownloadRemoteSubtitles</c>
    /// at v10.11.11). <c>ProbeProvider.HasChanged</c> compares the item's known
    /// subtitle files against what the resolver finds on disk now, so a brand new
    /// <c>DirectoryService</c> is required: it caches directory listings, and a
    /// shared one would answer from before the file existed.
    /// </remarks>
    /// <param name="video">The item to refresh.</param>
    /// <returns>True when the refresh was queued.</returns>
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "The file is already on disk and correct at this point. Whatever the provider subsystem does, this request succeeded, and turning a queueing problem into a 500 would tell the administrator the save failed when it did not.")]
    private bool QueueRefresh(Video video)
    {
        try
        {
            _providerManager.QueueRefresh(
                video.Id,
                new MetadataRefreshOptions(new DirectoryService(_fileSystem)),
                RefreshPriority.High);

            return true;
        }
        catch (Exception ex)
        {
            LogRefreshFailed(video.Id, ex);
            return false;
        }
    }

    /// <summary>
    /// Reads the request body, refusing to hold more than the cap.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The bytes, or null when the body was too long.</returns>
    private async Task<byte[]?> ReadBodyAsync(CancellationToken cancellationToken)
    {
        // Trust the declared length only to reject early. The bounded read below
        // is the actual guard, and it does not depend on the client being honest
        // about Content-Length or sending one at all.
        if (Request.ContentLength > SrtValidator.MaxBytes)
        {
            return null;
        }

        using var buffer = new MemoryStream(capacity: 64 * 1024);
        var chunk = new byte[64 * 1024];
        int read;

        while ((read = await Request.Body.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > SrtValidator.MaxBytes)
            {
                return null;
            }

            buffer.Write(chunk.AsSpan(0, read));
        }

        return buffer.ToArray();
    }

    /// <summary>
    /// Maps a write failure to a status code.
    /// </summary>
    /// <remarks>
    /// The environmental failures are 409 rather than 500. The request was
    /// well-formed and the server is healthy; something about the library's
    /// state on disk conflicts with it, and that is a distinction a client should
    /// be able to act on without reading prose. The two that should be
    /// unreachable are 500, because reaching them is a bug in this plugin.
    /// </remarks>
    /// <param name="failure">The failure.</param>
    /// <returns>The status code.</returns>
    private static int StatusFor(SubtitleWriteFailure failure)
        => failure switch
        {
            SubtitleWriteFailure.UnsafeTarget => StatusCodes.Status500InternalServerError,
            SubtitleWriteFailure.WriteFailed => StatusCodes.Status500InternalServerError,
            _ => StatusCodes.Status409Conflict,
        };

    /// <summary>
    /// Gives a write failure a title an administrator can scan.
    /// </summary>
    /// <param name="failure">The failure.</param>
    /// <returns>The title.</returns>
    private static string TitleFor(SubtitleWriteFailure failure)
        => failure switch
        {
            SubtitleWriteFailure.InvalidMediaPath => "Unusable media path",
            SubtitleWriteFailure.MediaFolderMissing => "Media folder not on disk",
            SubtitleWriteFailure.MediaFileMissing => "Media file not on disk",
            SubtitleWriteFailure.NotWritable => "Library folder is not writable",
            SubtitleWriteFailure.NameTooLong => "Subtitle name too long",
            SubtitleWriteFailure.NoAvailableName => "No free subtitle name",
            SubtitleWriteFailure.UnsafeTarget => "Refused an unsafe destination",
            _ => "Could not write the subtitle",
        };

    /// <summary>
    /// Resolves an item id against what the caller is allowed to see.
    /// </summary>
    /// <param name="itemId">The item id.</param>
    /// <returns>The item, or null.</returns>
    private BaseItem? FindItem(Guid itemId)
    {
        if (itemId.Equals(Guid.Empty))
        {
            return null;
        }

        var value = User?.FindFirst(UserIdClaim)?.Value;
        var userId = Guid.TryParse(value, out var parsed) ? parsed : Guid.Empty;

        try
        {
            return userId.Equals(Guid.Empty)
                ? _libraryManager.GetItemById<BaseItem>(itemId)
                : _libraryManager.GetItemById<BaseItem>(itemId, userId);
        }
        catch (InvalidOperationException ex)
        {
            // Same behaviour as the read endpoints: a row the server can no
            // longer deserialise throws rather than returning null, and from the
            // caller's point of view the item is simply not there.
            LogItemNotDeserializable(itemId, ex);
            return null;
        }
    }

    /// <summary>
    /// Logs a completed save. Information level: this is the audit trail for the
    /// only endpoint that changes a library.
    /// </summary>
    /// <param name="itemId">The item.</param>
    /// <param name="path">The path written.</param>
    /// <param name="bytes">The file size.</param>
    /// <param name="overwrote">Whether an existing file was replaced.</param>
    [LoggerMessage(
        EventId = 8001,
        Level = LogLevel.Information,
        Message = "Saved synced subtitle for item {ItemId} to {Path} ({Bytes} bytes, replaced existing: {Overwrote})")]
    private partial void LogSaved(Guid itemId, string path, long bytes, bool overwrote);

    /// <summary>
    /// Logs a refused document. The content is deliberately not included.
    /// </summary>
    /// <param name="itemId">The item.</param>
    /// <param name="error">Why it was refused.</param>
    [LoggerMessage(
        EventId = 8002,
        Level = LogLevel.Warning,
        Message = "Refused a subtitle for item {ItemId}: {Error}")]
    private partial void LogRejectedContent(Guid itemId, SrtValidationError error);

    /// <summary>
    /// Logs a body that exceeded the cap.
    /// </summary>
    /// <param name="itemId">The item.</param>
    [LoggerMessage(
        EventId = 8003,
        Level = LogLevel.Warning,
        Message = "Refused an oversized subtitle body for item {ItemId}")]
    private partial void LogRejectedOversizeBody(Guid itemId);

    /// <summary>
    /// Logs a write that did not happen.
    /// </summary>
    /// <param name="itemId">The item.</param>
    /// <param name="failure">Why.</param>
    /// <param name="detail">The message shown to the caller.</param>
    [LoggerMessage(
        EventId = 8004,
        Level = LogLevel.Error,
        Message = "Could not save a synced subtitle for item {ItemId} ({Failure}): {Detail}")]
    private partial void LogWriteFailed(Guid itemId, SubtitleWriteFailure failure, string? detail);

    /// <summary>
    /// Logs a refresh that could not be queued after a successful write.
    /// </summary>
    /// <param name="itemId">The item.</param>
    /// <param name="exception">What went wrong.</param>
    [LoggerMessage(
        EventId = 8005,
        Level = LogLevel.Warning,
        Message = "Wrote a synced subtitle for item {ItemId} but could not queue a refresh; it will appear at the next library scan")]
    private partial void LogRefreshFailed(Guid itemId, Exception exception);

    /// <summary>
    /// Logs a library row the server could not turn back into an item.
    /// </summary>
    /// <param name="itemId">The item.</param>
    /// <param name="exception">The repository failure.</param>
    [LoggerMessage(
        EventId = 8006,
        Level = LogLevel.Warning,
        Message = "Item {ItemId} exists in the library database but could not be deserialised; answering as not found")]
    private partial void LogItemNotDeserializable(Guid itemId, Exception exception);
}
