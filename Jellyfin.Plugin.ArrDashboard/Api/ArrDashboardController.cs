using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ArrDashboard.Configuration;
using Jellyfin.Plugin.ArrDashboard.Models;
using Jellyfin.Plugin.ArrDashboard.Services;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ArrDashboard.Api;

/// <summary>
/// Server-side proxy for the dashboard UI. Every Sonarr/Radarr call goes through
/// here so API keys stay on the server and browsers never need direct access to
/// the *arr hosts.
/// </summary>
[ApiController]
[Route("ArrDashboard")]
[Produces("application/json")]
[Authorize]
public class ArrDashboardController : ControllerBase
{
    private readonly ArrService _service;
    private readonly ILogger<ArrDashboardController> _logger;

    public ArrDashboardController(ArrService service, ILogger<ArrDashboardController> logger)
    {
        _service = service;
        _logger = logger;
    }

    private static PluginConfiguration Config =>
        ArrDashboardPlugin.Instance?.Configuration ?? new PluginConfiguration();

    // ------------------------------------------------------------------ read

    /// <summary>
    /// Connectivity and version of every configured instance.
    /// </summary>
    [HttpGet("Status")]
    public async Task<ActionResult<List<ServiceStatusDto>>> GetStatus(CancellationToken cancellationToken)
    {
        var denied = CheckReadAccess();
        if (denied is not null)
        {
            return denied;
        }

        return await _service.GetStatusAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Library-wide counters for the header strip.
    /// </summary>
    [HttpGet("Summary")]
    public async Task<ActionResult<SummaryDto>> GetSummary(CancellationToken cancellationToken)
    {
        var denied = CheckReadAccess();
        if (denied is not null)
        {
            return denied;
        }

        return await _service.GetSummaryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Upcoming and recent releases across Sonarr and Radarr.
    /// </summary>
    /// <param name="daysPast">Days before today. Defaults to the configured value.</param>
    /// <param name="daysFuture">Days after today. Defaults to the configured value.</param>
    /// <param name="start">Explicit start date, overrides daysPast.</param>
    /// <param name="end">Explicit end date, overrides daysFuture.</param>
    [HttpGet("Calendar")]
    public async Task<ActionResult<AggregateResult<CalendarItemDto>>> GetCalendar(
        [FromQuery] int? daysPast,
        [FromQuery] int? daysFuture,
        [FromQuery] DateTime? start,
        [FromQuery] DateTime? end,
        CancellationToken cancellationToken)
    {
        var denied = CheckReadAccess();
        if (denied is not null)
        {
            return denied;
        }

        var config = Config;
        var today = DateTime.UtcNow.Date;

        var from = start?.Date ?? today.AddDays(-Math.Clamp(daysPast ?? config.CalendarDaysPast, 0, 365));
        var to = end?.Date ?? today.AddDays(Math.Clamp(daysFuture ?? config.CalendarDaysFuture, 1, 365));

        if (to < from)
        {
            return BadRequest("end must not be before start.");
        }

        if ((to - from).TotalDays > 400)
        {
            return BadRequest("Date range is limited to 400 days.");
        }

        // Sonarr/Radarr treat end as exclusive-ish; include the whole final day.
        return await _service
            .GetCalendarAsync(from, to.AddDays(1).AddSeconds(-1), cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Everything Sonarr and Radarr are currently downloading or importing.
    /// </summary>
    [HttpGet("Queue")]
    public async Task<ActionResult<AggregateResult<QueueItemDto>>> GetQueue(CancellationToken cancellationToken)
    {
        var denied = CheckReadAccess();
        if (denied is not null)
        {
            return denied;
        }

        return await _service.GetQueueAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// All series tracked by Sonarr, with completion statistics.
    /// </summary>
    [HttpGet("Series")]
    public async Task<ActionResult<AggregateResult<SeriesDto>>> GetSeries(CancellationToken cancellationToken)
    {
        var denied = CheckReadAccess();
        if (denied is not null)
        {
            return denied;
        }

        return await _service.GetSeriesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// All movies tracked by Radarr.
    /// </summary>
    [HttpGet("Movies")]
    public async Task<ActionResult<AggregateResult<MovieDto>>> GetMovies(CancellationToken cancellationToken)
    {
        var denied = CheckReadAccess();
        if (denied is not null)
        {
            return denied;
        }

        return await _service.GetMoviesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Monitored episodes and movies with no file.
    /// </summary>
    [HttpGet("Missing")]
    public async Task<ActionResult<AggregateResult<MissingItemDto>>> GetMissing(
        [FromQuery] int limit,
        CancellationToken cancellationToken)
    {
        var denied = CheckReadAccess();
        if (denied is not null)
        {
            return denied;
        }

        return await _service
            .GetMissingAsync(limit <= 0 ? 100 : limit, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Recent grabs, imports and failures.
    /// </summary>
    [HttpGet("History")]
    public async Task<ActionResult<AggregateResult<HistoryItemDto>>> GetHistory(
        [FromQuery] int limit,
        CancellationToken cancellationToken)
    {
        var denied = CheckReadAccess();
        if (denied is not null)
        {
            return denied;
        }

        return await _service
            .GetHistoryAsync(limit <= 0 ? 50 : limit, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Proxies a poster from an instance so the browser needs neither network
    /// access to the *arr host nor its API key.
    /// </summary>
    [HttpGet("Image")]
    [Produces("image/jpeg")]
    public async Task<ActionResult> GetImage(
        [FromQuery, Required] string instanceId,
        [FromQuery, Required] string path,
        CancellationToken cancellationToken)
    {
        var denied = CheckReadAccess();
        if (denied is not null)
        {
            return denied;
        }

        var instance = ArrService.FindAnyInstance(instanceId);
        if (instance is null)
        {
            return NotFound("Unknown instance.");
        }

        // Only relative paths on the instance itself - never an arbitrary URL.
        if (string.IsNullOrWhiteSpace(path)
            || !path.StartsWith('/')
            || path.StartsWith("//", StringComparison.Ordinal)
            || path.Contains("..", StringComparison.Ordinal)
            || path.Contains("://", StringComparison.Ordinal))
        {
            return BadRequest("Invalid image path.");
        }

        try
        {
            var asset = await _service.GetImageAsync(instance, path, cancellationToken).ConfigureAwait(false);
            Response.Headers.CacheControl = "private, max-age=86400";
            return File(asset.Data, asset.ContentType);
        }
        catch (ArrRequestException ex)
        {
            _logger.LogDebug("[ArrDashboard] Poster fetch failed: {Message}", ex.Message);
            return NotFound();
        }
    }

    // ----------------------------------------------------------------- write

    /// <summary>
    /// Drops cached responses so the next read hits the backends.
    /// </summary>
    [HttpPost("Refresh")]
    public ActionResult Refresh()
    {
        var denied = CheckReadAccess();
        if (denied is not null)
        {
            return denied;
        }

        _service.InvalidateCache();
        return NoContent();
    }

    /// <summary>
    /// Triggers a Sonarr search for every monitored missing episode of a series.
    /// </summary>
    [HttpPost("Search/Series/{instanceId}/{seriesId:int}")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public async Task<ActionResult> SearchSeries(
        [FromRoute] string instanceId,
        [FromRoute] int seriesId,
        CancellationToken cancellationToken)
    {
        var instance = ArrService.FindInstance(ServiceKind.Sonarr, instanceId);
        if (instance is null)
        {
            return NotFound("Unknown Sonarr instance.");
        }

        return await RunCommandAsync(
            () => _service.SearchSeriesAsync(instance, seriesId, cancellationToken)).ConfigureAwait(false);
    }

    /// <summary>
    /// Triggers a Sonarr search for specific episodes.
    /// </summary>
    [HttpPost("Search/Episodes/{instanceId}")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public async Task<ActionResult> SearchEpisodes(
        [FromRoute] string instanceId,
        [FromBody] int[] episodeIds,
        CancellationToken cancellationToken)
    {
        if (episodeIds is null || episodeIds.Length == 0)
        {
            return BadRequest("No episode ids supplied.");
        }

        var instance = ArrService.FindInstance(ServiceKind.Sonarr, instanceId);
        if (instance is null)
        {
            return NotFound("Unknown Sonarr instance.");
        }

        return await RunCommandAsync(
            () => _service.SearchEpisodesAsync(instance, episodeIds, cancellationToken)).ConfigureAwait(false);
    }

    /// <summary>
    /// Triggers a Radarr search for a movie.
    /// </summary>
    [HttpPost("Search/Movie/{instanceId}/{movieId:int}")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public async Task<ActionResult> SearchMovie(
        [FromRoute] string instanceId,
        [FromRoute] int movieId,
        CancellationToken cancellationToken)
    {
        var instance = ArrService.FindInstance(ServiceKind.Radarr, instanceId);
        if (instance is null)
        {
            return NotFound("Unknown Radarr instance.");
        }

        return await RunCommandAsync(
            () => _service.SearchMoviesAsync(instance, new[] { movieId }, cancellationToken)).ConfigureAwait(false);
    }

    /// <summary>
    /// Removes a queue item, optionally telling the download client to drop it too.
    /// </summary>
    [HttpDelete("Queue/{kind}/{instanceId}/{queueId:int}")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public async Task<ActionResult> RemoveFromQueue(
        [FromRoute] string kind,
        [FromRoute] string instanceId,
        [FromRoute] int queueId,
        [FromQuery] bool removeFromClient,
        [FromQuery] bool blocklist,
        CancellationToken cancellationToken)
    {
        var instance = ArrService.FindInstance(kind, instanceId);
        if (instance is null)
        {
            return NotFound("Unknown instance.");
        }

        return await RunCommandAsync(
            () => _service.RemoveFromQueueAsync(instance, queueId, removeFromClient, blocklist, cancellationToken))
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Validates a URL and API key from the configuration page before saving.
    /// </summary>
    [HttpPost("TestConnection")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public async Task<ActionResult<ServiceStatusDto>> TestConnection(
        [FromBody] TestConnectionRequest request,
        CancellationToken cancellationToken)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Url))
        {
            return BadRequest("A URL is required.");
        }

        var probe = new ArrInstance
        {
            Id = "test",
            Name = string.IsNullOrWhiteSpace(request.Name) ? "Instance" : request.Name,
            Url = request.Url,
            ApiKey = request.ApiKey ?? string.Empty,
            Enabled = true
        };

        var kind = string.Equals(request.Kind, ServiceKind.Radarr, StringComparison.OrdinalIgnoreCase)
            ? ServiceKind.Radarr
            : ServiceKind.Sonarr;

        return await _service.ProbeAsync(probe, kind, cancellationToken).ConfigureAwait(false);
    }

    // --------------------------------------------------------------- helpers

    private async Task<ActionResult> RunCommandAsync(Func<Task> action)
    {
        try
        {
            await action().ConfigureAwait(false);
            return NoContent();
        }
        catch (ArrRequestException ex)
        {
            return StatusCode(StatusCodes.Status502BadGateway, ex.Message);
        }
    }

    /// <summary>
    /// Returns a Forbid result when the dashboard is restricted to administrators
    /// and the caller is not one; null when access is allowed.
    /// </summary>
    private ActionResult? CheckReadAccess()
    {
        if (Config.AllowNonAdminAccess)
        {
            return null;
        }

        return User.IsInRole("Administrator") ? null : Forbid();
    }

    /// <summary>
    /// Body of a connection test from the configuration page.
    /// </summary>
    public class TestConnectionRequest
    {
        public string? Kind { get; set; }

        public string? Name { get; set; }

        public string? Url { get; set; }

        public string? ApiKey { get; set; }
    }
}
