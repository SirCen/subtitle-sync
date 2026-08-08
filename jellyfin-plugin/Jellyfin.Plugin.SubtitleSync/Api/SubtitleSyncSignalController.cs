using System;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Net.Mime;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.SubtitleSync.SignalCache;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SubtitleSync.Api;

/// <summary>
/// The speech signal cache: fetch an analysed signal, or contribute one.
/// </summary>
/// <remarks>
/// <para>
/// This is the endpoint that decides whether a sync costs 45 KB or 115 MB per
/// hour of runtime. The VAD runs in the browser, so the server cannot produce a
/// signal on its own: on a miss the client streams raw PCM, analyses it, and
/// POSTs the result back here for everyone else.
/// </para>
/// <para>
/// The policy on both verbs is <see cref="Policies.SubtitleManagement"/>, the
/// permission Jellyfin gates its own "Edit subtitles" affordance on, matching
/// the rest of the plugin's read path. Not <c>RequiresElevation</c>: a user who
/// is allowed to run a sync has to be able to both read and fill the cache, and
/// a cache only they could read would not be a shared cache. The two
/// maintenance endpoints are separate, and are elevated, because they belong to
/// the Dashboard rather than to the sync page.
/// </para>
/// <para>
/// <b>The cache is trusted-writer, not verified, and that is a deliberate
/// trade.</b> <see cref="SpeechSignalCodec.Validate"/> proves an envelope is
/// well-formed - magic, length, zeroed padding, CRC - and nothing more. A
/// syntactically perfect signal of arbitrary bits passes, so any account with
/// <see cref="Policies.SubtitleManagement"/> can read the key for an item it can
/// see from <c>GET /SubtitleSync/SignalKey/{itemId}</c> and then replace what is
/// stored under it. The next administrator to sync that item gets a cache hit,
/// analyses against the planted signal, and is shown a confident offset that has
/// nothing to do with the audio - which, as
/// <see cref="SpeechSignalCodec"/> says of bad signals generally, is a worse
/// failure than an error. Accepted because the alternative is a cache only its
/// own writer can read, which is not a cache; because the write into the library
/// still needs elevation; and because the damage is one mis-timed sibling file
/// that is obvious on playback. Do not read the CRC as an integrity guarantee
/// against the people filling this cache: it is not one.
/// </para>
/// <para>
/// Controllers are auto-discovered - the server adds every plugin assembly as
/// an MVC application part - but they are resolved from the DI container, so
/// <see cref="ISignalCacheStore"/> has to be registered. That happens in
/// <see cref="PluginServiceRegistrator"/>.
/// </para>
/// </remarks>
[ApiController]
[Route("SubtitleSync")]
public partial class SubtitleSyncSignalController : ControllerBase
{
    /// <summary>
    /// The media type of an envelope in both directions. It is opaque binary:
    /// the format is documented on <see cref="SpeechSignalCodec"/>, not in a
    /// content type.
    /// </summary>
    private const string SignalContentType = MediaTypeNames.Application.Octet;

    private readonly ISignalCacheStore _store;
    private readonly ILogger<SubtitleSyncSignalController> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="SubtitleSyncSignalController"/> class.
    /// </summary>
    /// <param name="store">The cache store.</param>
    /// <param name="logger">Logger.</param>
    public SubtitleSyncSignalController(ISignalCacheStore store, ILogger<SubtitleSyncSignalController> logger)
    {
        _store = store;
        _logger = logger;
    }

    /// <summary>
    /// Fetches a cached speech signal.
    /// </summary>
    /// <param name="key">The cache key, sixty-four lowercase hex characters.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The signal envelope.</returns>
    /// <response code="200">The signal was cached and is returned.</response>
    /// <response code="400">The key is not a well-formed cache key.</response>
    /// <response code="404">Nothing is cached under that key.</response>
    // No [Produces]: it constrains content negotiation for the whole action, so
    // declaring octet-stream here turns the JSON error body of a rejected key
    // into a 406 instead of the 400 it is. The success path sets its own content
    // type on the FileResult, which is the only response that has a body worth
    // negotiating.
    [HttpGet("Signal/{key}")]
    [Authorize(Policy = Policies.SubtitleManagement)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(FileResult))]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> GetSignal([FromRoute, Required] string key, CancellationToken cancellationToken)
    {
        if (!SignalCacheKey.IsValid(key))
        {
            return BadRequestKey();
        }

        var envelope = await _store.ReadAsync(key, cancellationToken).ConfigureAwait(false);
        if (envelope is null)
        {
            LogMiss(key);
            return NotFound();
        }

        LogHit(key, envelope.Length);
        return File(envelope, SignalContentType);
    }

    /// <summary>
    /// Stores a speech signal the browser has just analysed.
    /// </summary>
    /// <param name="key">The cache key, sixty-four lowercase hex characters.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>No content.</returns>
    /// <response code="204">The signal was stored.</response>
    /// <response code="400">The key or the payload was rejected.</response>
    /// <response code="413">The payload is longer than the maximum envelope.</response>
    /// <remarks>
    /// The body is read as raw bytes rather than bound to a model. It is up to
    /// a megabyte of packed bits with no structure a serialiser could help
    /// with, and reading it by hand is what makes the length cap enforceable
    /// before the allocation rather than after it.
    /// </remarks>
    [HttpPost("Signal/{key}")]
    [Authorize(Policy = Policies.SubtitleManagement)]
    [Consumes(SignalContentType)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status413PayloadTooLarge)]
    public async Task<ActionResult> PostSignal([FromRoute, Required] string key, CancellationToken cancellationToken)
    {
        if (!SignalCacheKey.IsValid(key))
        {
            return BadRequestKey();
        }

        var max = SpeechSignalCodec.MaxEnvelopeLength;

        // Trust the declared length only to reject early. The actual guard is
        // the bounded read below, which does not depend on the client being
        // honest about Content-Length or sending one at all.
        if (Request.ContentLength > max)
        {
            return StatusCode(StatusCodes.Status413PayloadTooLarge);
        }

        var body = await ReadBodyAsync(max, cancellationToken).ConfigureAwait(false);
        if (body is null)
        {
            return StatusCode(StatusCodes.Status413PayloadTooLarge);
        }

        var error = SpeechSignalCodec.Validate(body);
        if (error != SignalPayloadError.None)
        {
            LogRejectedPayload(key, error);
            return BadRequest(new { Error = "InvalidSignalPayload", Reason = error.ToString() });
        }

        await _store.WriteAsync(key, body, cancellationToken).ConfigureAwait(false);
        LogAccepted(key, SpeechSignalCodec.ReadSampleCount(body));
        return NoContent();
    }

    /// <summary>
    /// Reports what the cache is holding, for the configuration page's readout.
    /// </summary>
    /// <returns>The cache statistics.</returns>
    /// <response code="200">The statistics.</response>
    [HttpGet("Cache/Stats")]
    [Authorize(Policy = Policies.RequiresElevation)]
    [Produces(MediaTypeNames.Application.Json)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<SignalCacheStats> GetCacheStats()
    {
        return _store.GetStats();
    }

    /// <summary>
    /// Empties the cache.
    /// </summary>
    /// <returns>What was left afterwards, so the page can refresh its readout
    /// from the same response.</returns>
    /// <response code="200">The cache was cleared.</response>
    /// <remarks>
    /// Elevated, unlike the signal endpoints. Clearing costs every other user a
    /// re-analysis, and the only thing that offers it is the Dashboard
    /// configuration page, which is admin-only already.
    /// </remarks>
    [HttpPost("Cache/Clear")]
    [Authorize(Policy = Policies.RequiresElevation)]
    [Produces(MediaTypeNames.Application.Json)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<SignalCacheStats> ClearCache()
    {
        _store.Clear();
        return _store.GetStats();
    }

    /// <summary>
    /// Reads the request body, refusing to hold more than the cap.
    /// </summary>
    /// <param name="max">The largest acceptable body.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The bytes, or <see langword="null"/> if the body was too long.</returns>
    private async Task<byte[]?> ReadBodyAsync(int max, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream(capacity: 64 * 1024);
        var chunk = new byte[64 * 1024];
        int read;
        while ((read = await Request.Body.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > max)
            {
                return null;
            }

            buffer.Write(chunk.AsSpan(0, read));
        }

        return buffer.ToArray();
    }

    private BadRequestObjectResult BadRequestKey()
    {
        // The rejected value is deliberately not echoed: it is attacker
        // controlled text that would end up in a log an administrator reads.
        LogRejectedKey();
        return BadRequest(new
        {
            Error = "InvalidCacheKey",
            Reason = "A cache key must be exactly " + SignalCacheKey.Length + " lowercase hexadecimal characters.",
        });
    }

    /// <summary>
    /// Logs a cache hit.
    /// </summary>
    /// <param name="key">The cache key.</param>
    /// <param name="bytes">The envelope size.</param>
    [LoggerMessage(
        EventId = 9101,
        Level = LogLevel.Debug,
        Message = "Speech signal cache hit for {Key} ({Bytes} bytes)")]
    private partial void LogHit(string key, int bytes);

    /// <summary>
    /// Logs a cache miss, which is what sends the client to the PCM endpoint.
    /// </summary>
    /// <param name="key">The cache key.</param>
    [LoggerMessage(
        EventId = 9102,
        Level = LogLevel.Debug,
        Message = "Speech signal cache miss for {Key}")]
    private partial void LogMiss(string key);

    /// <summary>
    /// Logs a stored signal.
    /// </summary>
    /// <param name="key">The cache key.</param>
    /// <param name="sampleCount">How many 10 ms samples it carries.</param>
    [LoggerMessage(
        EventId = 9103,
        Level = LogLevel.Information,
        Message = "Stored speech signal {Key} ({SampleCount} samples)")]
    private partial void LogAccepted(string key, int sampleCount);

    /// <summary>
    /// Logs a payload that failed validation.
    /// </summary>
    /// <param name="key">The cache key.</param>
    /// <param name="error">Why it was refused.</param>
    [LoggerMessage(
        EventId = 9104,
        Level = LogLevel.Warning,
        Message = "Rejected a speech signal payload for {Key}: {Error}")]
    private partial void LogRejectedPayload(string key, SignalPayloadError error);

    /// <summary>
    /// Logs a malformed key. The value is not included on purpose.
    /// </summary>
    [LoggerMessage(
        EventId = 9105,
        Level = LogLevel.Warning,
        Message = "Rejected a malformed speech signal cache key")]
    private partial void LogRejectedKey();
}
