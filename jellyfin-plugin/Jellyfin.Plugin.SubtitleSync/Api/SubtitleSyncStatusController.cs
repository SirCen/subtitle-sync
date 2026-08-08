using System.Net.Mime;
using Jellyfin.Plugin.SubtitleSync.Injection;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.SubtitleSync.Api;

/// <summary>
/// Reports whether the Subtitles-menu injection is live.
/// </summary>
/// <remarks>
/// <para>
/// Elevated, because the only consumer is the plugin's configuration page and
/// the answer describes the server's plugin inventory. Nothing a non-admin can
/// do with it, and no reason to tell them what else is installed.
/// </para>
/// <para>
/// This exists so the configuration page can show the install note without
/// guessing. A page that inferred "File Transformation is missing" from the
/// absence of a menu item would be wrong in exactly the case that matters -
/// when the plugin is installed but its API has moved.
/// </para>
/// </remarks>
[ApiController]
[Authorize(Policy = Policies.RequiresElevation)]
[Route("SubtitleSync")]
public class SubtitleSyncStatusController : ControllerBase
{
    private readonly InjectionState _state;

    /// <summary>
    /// Initializes a new instance of the <see cref="SubtitleSyncStatusController"/> class.
    /// </summary>
    /// <param name="state">Written once at startup by <see cref="InjectionStartupService"/>.</param>
    public SubtitleSyncStatusController(InjectionState state)
    {
        _state = state;
    }

    /// <summary>
    /// Gets the state of the Subtitles-menu injection.
    /// </summary>
    /// <response code="200">The current state.</response>
    /// <returns>The injection status.</returns>
    [HttpGet("Status")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [Produces(MediaTypeNames.Application.Json)]
    public ActionResult<InjectionStatusResponse> GetStatus() =>
        new InjectionStatusResponse
        {
            Availability = _state.Availability.ToString(),
            MenuItemActive = _state.Availability == InjectionAvailability.Registered,
            FileTransformationVersion = _state.FileTransformationVersion,
            Detail = _state.Detail,
        };
}
