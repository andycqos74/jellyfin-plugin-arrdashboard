using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ArrDashboard.Configuration;
using Jellyfin.Plugin.ArrDashboard.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ArrDashboard.Services;

/// <summary>
/// Fans requests out across every configured Sonarr/Radarr instance, maps the
/// two APIs onto one set of DTOs, and reports per-instance failures instead of
/// failing the whole request.
/// </summary>
public class ArrService
{
    private const string IsoDate = "yyyy-MM-dd";

    private readonly ArrApiClient _client;
    private readonly ILogger<ArrService> _logger;
    private readonly ResponseCache _cache = new ResponseCache();
    private object? _cachedConfig;

    public ArrService(ArrApiClient client, ILogger<ArrService> logger)
    {
        _client = client;
        _logger = logger;
    }

    /// <summary>
    /// Saving settings swaps in a new configuration instance, so a reference change
    /// means URLs or API keys may differ and everything cached is suspect.
    /// (BasePlugin.ConfigurationChanged is a settable delegate property the host can
    /// overwrite, so subscribing to it is not reliable.)
    /// </summary>
    private ResponseCache Cache
    {
        get
        {
            var current = ArrDashboardPlugin.Instance?.Configuration;
            if (!ReferenceEquals(current, Interlocked.Exchange(ref _cachedConfig, current)))
            {
                _cache.Clear();
            }

            return _cache;
        }
    }

    private static PluginConfiguration Config =>
        ArrDashboardPlugin.Instance?.Configuration ?? new PluginConfiguration();

    private static int CacheSeconds => Math.Clamp(Config.CacheSeconds, 0, 600);

    private static int PageSize => Math.Clamp(Config.MaxRecordsPerInstance, 10, 1000);

    public static IReadOnlyList<ArrInstance> SonarrInstances => Active(Config.SonarrInstances);

    public static IReadOnlyList<ArrInstance> RadarrInstances => Active(Config.RadarrInstances);

    private static IReadOnlyList<ArrInstance> Active(ArrInstance[]? instances)
        => (instances ?? Array.Empty<ArrInstance>())
            .Where(i => i.Enabled && !string.IsNullOrWhiteSpace(i.Url))
            .Select(EnsureId)
            .ToList();

    /// <summary>
    /// Instances saved before ids existed (or hand-edited XML) still need a key.
    /// </summary>
    private static ArrInstance EnsureId(ArrInstance instance)
    {
        if (string.IsNullOrWhiteSpace(instance.Id))
        {
            instance.Id = StableHash(instance.Url + "|" + instance.Name);
        }

        if (string.IsNullOrWhiteSpace(instance.Name))
        {
            instance.Name = instance.Url;
        }

        return instance;
    }

    /// <summary>
    /// FNV-1a. String.GetHashCode is randomised per process, and instance ids end
    /// up in bookmarked image URLs, so they have to survive a restart.
    /// </summary>
    private static string StableHash(string value)
    {
        unchecked
        {
            var hash = 2166136261;
            foreach (var c in value)
            {
                hash ^= c;
                hash *= 16777619;
            }

            return hash.ToString("x8", CultureInfo.InvariantCulture);
        }
    }

    public static ArrInstance? FindInstance(string kind, string instanceId)
    {
        var pool = string.Equals(kind, ServiceKind.Radarr, StringComparison.OrdinalIgnoreCase)
            ? RadarrInstances
            : SonarrInstances;

        return pool.FirstOrDefault(i => string.Equals(i.Id, instanceId, StringComparison.Ordinal));
    }

    public static ArrInstance? FindAnyInstance(string instanceId)
        => SonarrInstances.FirstOrDefault(i => string.Equals(i.Id, instanceId, StringComparison.Ordinal))
            ?? RadarrInstances.FirstOrDefault(i => string.Equals(i.Id, instanceId, StringComparison.Ordinal));

    // ---------------------------------------------------------------- status

    public async Task<List<ServiceStatusDto>> GetStatusAsync(CancellationToken cancellationToken)
    {
        var tasks = new List<Task<ServiceStatusDto>>();

        foreach (var instance in SonarrInstances)
        {
            tasks.Add(ProbeAsync(instance, ServiceKind.Sonarr, cancellationToken));
        }

        foreach (var instance in RadarrInstances)
        {
            tasks.Add(ProbeAsync(instance, ServiceKind.Radarr, cancellationToken));
        }

        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        return results.ToList();
    }

    public async Task<ServiceStatusDto> ProbeAsync(
        ArrInstance instance,
        string kind,
        CancellationToken cancellationToken)
    {
        var dto = new ServiceStatusDto
        {
            InstanceId = instance.Id,
            Name = instance.Name,
            Kind = kind,
            Url = instance.Url
        };

        var stopwatch = Stopwatch.StartNew();
        try
        {
            var status = await _client
                .GetAsync<ArrSystemStatus>(instance, "system/status", cancellationToken)
                .ConfigureAwait(false);

            dto.Online = true;
            dto.Version = status.Version;

            if (!string.IsNullOrWhiteSpace(status.InstanceName))
            {
                dto.Name = instance.Name;
            }
        }
        catch (Exception ex) when (ex is ArrRequestException or OperationCanceledException)
        {
            dto.Online = false;
            dto.Error = ex.Message;
        }

        dto.ResponseMs = stopwatch.ElapsedMilliseconds;
        return dto;
    }

    // -------------------------------------------------------------- calendar

    public Task<AggregateResult<CalendarItemDto>> GetCalendarAsync(
        DateTime start,
        DateTime end,
        CancellationToken cancellationToken)
    {
        var key = "calendar:" + start.ToString("O", CultureInfo.InvariantCulture)
            + ":" + end.ToString("O", CultureInfo.InvariantCulture);

        return Cache.GetOrAddAsync(key, CacheSeconds, () => BuildCalendarAsync(start, end, cancellationToken));
    }

    private async Task<AggregateResult<CalendarItemDto>> BuildCalendarAsync(
        DateTime start,
        DateTime end,
        CancellationToken cancellationToken)
    {
        var result = new AggregateResult<CalendarItemDto>();
        var unmonitored = Config.IncludeUnmonitored ? "true" : "false";

        var query = new Dictionary<string, string?>
        {
            ["start"] = start.ToString(IsoDate, CultureInfo.InvariantCulture),
            ["end"] = end.ToString(IsoDate, CultureInfo.InvariantCulture),
            ["unmonitored"] = unmonitored
        };

        // Queue lookup lets the calendar show "downloading" without a second round trip in the UI.
        var queue = await GetQueueAsync(cancellationToken).ConfigureAwait(false);
        var downloadingEpisodes = queue.Items
            .Where(q => string.Equals(q.Kind, ServiceKind.Sonarr, StringComparison.Ordinal))
            .Select(q => q.InstanceId + ":" + q.EpisodeId.ToString(CultureInfo.InvariantCulture))
            .ToHashSet(StringComparer.Ordinal);
        var downloadingMovies = queue.Items
            .Where(q => string.Equals(q.Kind, ServiceKind.Radarr, StringComparison.Ordinal))
            .Select(q => q.InstanceId + ":" + q.ParentId.ToString(CultureInfo.InvariantCulture))
            .ToHashSet(StringComparer.Ordinal);

        var sonarrTasks = SonarrInstances.Select(instance => RunAsync(
            instance,
            ServiceKind.Sonarr,
            result,
            async () =>
            {
                var episodeQuery = new Dictionary<string, string?>(query)
                {
                    ["includeSeries"] = "true"
                };

                var episodes = await _client
                    .GetAsync<List<SonarrEpisode>>(instance, "calendar", episodeQuery, cancellationToken)
                    .ConfigureAwait(false);

                var items = new List<CalendarItemDto>();
                foreach (var episode in episodes)
                {
                    if (episode.AirDateUtc is null)
                    {
                        continue;
                    }

                    var series = episode.Series;
                    items.Add(new CalendarItemDto
                    {
                        InstanceId = instance.Id,
                        ServiceName = instance.Name,
                        Kind = ServiceKind.Sonarr,
                        Id = episode.Id,
                        ParentId = episode.SeriesId,
                        Title = series?.Title ?? "Unknown series",
                        SubTitle = episode.Title,
                        EpisodeCode = FormatEpisodeCode(episode.SeasonNumber, episode.EpisodeNumber),
                        AirDateUtc = episode.AirDateUtc.Value,
                        EventType = "airing",
                        HasFile = episode.HasFile,
                        Monitored = episode.Monitored,
                        Downloading = downloadingEpisodes.Contains(
                            instance.Id + ":" + episode.Id.ToString(CultureInfo.InvariantCulture)),
                        Network = series?.Network,
                        Runtime = episode.Runtime ?? series?.Runtime ?? 0,
                        Overview = episode.Overview,
                        PosterUrl = PosterFor(instance, series?.Images),
                        DetailUrl = SeriesDetailUrl(instance, series),
                        SeasonPremiere = episode.EpisodeNumber == 1
                    });
                }

                return items;
            }));

        var radarrTasks = RadarrInstances.Select(instance => RunAsync(
            instance,
            ServiceKind.Radarr,
            result,
            async () =>
            {
                var movies = await _client
                    .GetAsync<List<RadarrMovie>>(instance, "calendar", query, cancellationToken)
                    .ConfigureAwait(false);

                var items = new List<CalendarItemDto>();
                foreach (var movie in movies)
                {
                    var downloading = downloadingMovies.Contains(
                        instance.Id + ":" + movie.Id.ToString(CultureInfo.InvariantCulture));

                    // Radarr returns the movie once even when several of its release
                    // dates land in the window; emit one entry per date in range.
                    foreach (var release in MovieReleaseDates(movie))
                    {
                        if (release.Date < start || release.Date > end)
                        {
                            continue;
                        }

                        items.Add(new CalendarItemDto
                        {
                            InstanceId = instance.Id,
                            ServiceName = instance.Name,
                            Kind = ServiceKind.Radarr,
                            Id = movie.Id,
                            ParentId = movie.Id,
                            Title = movie.Title ?? "Unknown movie",
                            SubTitle = movie.Year > 0
                                ? movie.Year.ToString(CultureInfo.InvariantCulture)
                                : null,
                            AirDateUtc = release.Date,
                            EventType = release.Type,
                            HasFile = movie.HasFile,
                            Monitored = movie.Monitored,
                            Downloading = downloading,
                            Network = movie.Studio,
                            Runtime = movie.Runtime,
                            Overview = movie.Overview,
                            PosterUrl = PosterFor(instance, movie.Images),
                            DetailUrl = MovieDetailUrl(instance, movie)
                        });
                    }
                }

                return items;
            }));

        await Task.WhenAll(sonarrTasks.Concat(radarrTasks)).ConfigureAwait(false);

        result.Items = result.Items
            .OrderBy(i => i.AirDateUtc)
            .ThenBy(i => i.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return result;
    }

    private static IEnumerable<(DateTime Date, string Type)> MovieReleaseDates(RadarrMovie movie)
    {
        if (movie.InCinemas.HasValue)
        {
            yield return (movie.InCinemas.Value, "cinemas");
        }

        if (movie.DigitalRelease.HasValue)
        {
            yield return (movie.DigitalRelease.Value, "digital");
        }

        if (movie.PhysicalRelease.HasValue)
        {
            yield return (movie.PhysicalRelease.Value, "physical");
        }
    }

    // ----------------------------------------------------------------- queue

    public Task<AggregateResult<QueueItemDto>> GetQueueAsync(CancellationToken cancellationToken)
        => Cache.GetOrAddAsync("queue", Math.Min(CacheSeconds, 15), () => BuildQueueAsync(cancellationToken));

    private async Task<AggregateResult<QueueItemDto>> BuildQueueAsync(CancellationToken cancellationToken)
    {
        var result = new AggregateResult<QueueItemDto>();

        var query = new Dictionary<string, string?>
        {
            ["page"] = "1",
            ["pageSize"] = PageSize.ToString(CultureInfo.InvariantCulture),
            ["includeUnknownSeriesItems"] = "true",
            ["includeUnknownMovieItems"] = "true"
        };

        var sonarrTasks = SonarrInstances.Select(instance => RunAsync(
            instance,
            ServiceKind.Sonarr,
            result,
            async () =>
            {
                var sonarrQuery = new Dictionary<string, string?>(query)
                {
                    ["includeSeries"] = "true",
                    ["includeEpisode"] = "true"
                };

                var page = await _client
                    .GetAsync<ArrPagedResponse<SonarrQueueRecord>>(instance, "queue", sonarrQuery, cancellationToken)
                    .ConfigureAwait(false);

                return (page.Records ?? new List<SonarrQueueRecord>()).Select(record => new QueueItemDto
                {
                    InstanceId = instance.Id,
                    ServiceName = instance.Name,
                    Kind = ServiceKind.Sonarr,
                    Id = record.Id,
                    ParentId = record.SeriesId,
                    EpisodeId = record.EpisodeId,
                    Title = record.Series?.Title ?? record.Title ?? "Unknown",
                    SubTitle = BuildEpisodeSubtitle(record.Episode),
                    ReleaseTitle = record.Title,
                    Size = record.Size,
                    SizeLeft = record.Sizeleft,
                    Progress = Percent(record.Size, record.Sizeleft),
                    TimeLeft = record.Timeleft,
                    EstimatedCompletionTime = record.EstimatedCompletionTime,
                    Status = record.Status,
                    TrackedDownloadStatus = record.TrackedDownloadStatus,
                    TrackedDownloadState = record.TrackedDownloadState,
                    Protocol = record.Protocol,
                    DownloadClient = record.DownloadClient,
                    Indexer = record.Indexer,
                    Quality = record.Quality?.Quality?.Name,
                    ErrorMessage = record.ErrorMessage,
                    Warnings = FlattenStatusMessages(record.StatusMessages),
                    PosterUrl = PosterFor(instance, record.Series?.Images)
                }).ToList();
            }));

        var radarrTasks = RadarrInstances.Select(instance => RunAsync(
            instance,
            ServiceKind.Radarr,
            result,
            async () =>
            {
                var radarrQuery = new Dictionary<string, string?>(query)
                {
                    ["includeMovie"] = "true"
                };

                var page = await _client
                    .GetAsync<ArrPagedResponse<RadarrQueueRecord>>(instance, "queue", radarrQuery, cancellationToken)
                    .ConfigureAwait(false);

                return (page.Records ?? new List<RadarrQueueRecord>()).Select(record => new QueueItemDto
                {
                    InstanceId = instance.Id,
                    ServiceName = instance.Name,
                    Kind = ServiceKind.Radarr,
                    Id = record.Id,
                    ParentId = record.MovieId,
                    Title = record.Movie?.Title ?? record.Title ?? "Unknown",
                    SubTitle = record.Movie is { Year: > 0 } queuedMovie
                        ? queuedMovie.Year.ToString(CultureInfo.InvariantCulture)
                        : null,
                    ReleaseTitle = record.Title,
                    Size = record.Size,
                    SizeLeft = record.Sizeleft,
                    Progress = Percent(record.Size, record.Sizeleft),
                    TimeLeft = record.Timeleft,
                    EstimatedCompletionTime = record.EstimatedCompletionTime,
                    Status = record.Status,
                    TrackedDownloadStatus = record.TrackedDownloadStatus,
                    TrackedDownloadState = record.TrackedDownloadState,
                    Protocol = record.Protocol,
                    DownloadClient = record.DownloadClient,
                    Indexer = record.Indexer,
                    Quality = record.Quality?.Quality?.Name,
                    ErrorMessage = record.ErrorMessage,
                    Warnings = FlattenStatusMessages(record.StatusMessages),
                    PosterUrl = PosterFor(instance, record.Movie?.Images)
                }).ToList();
            }));

        await Task.WhenAll(sonarrTasks.Concat(radarrTasks)).ConfigureAwait(false);

        result.Items = result.Items
            .OrderByDescending(i => i.Progress)
            .ThenBy(i => i.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return result;
    }

    // ---------------------------------------------------------------- series

    public Task<AggregateResult<SeriesDto>> GetSeriesAsync(CancellationToken cancellationToken)
        => Cache.GetOrAddAsync("series", CacheSeconds, () => BuildSeriesAsync(cancellationToken));

    private async Task<AggregateResult<SeriesDto>> BuildSeriesAsync(CancellationToken cancellationToken)
    {
        var result = new AggregateResult<SeriesDto>();

        var tasks = SonarrInstances.Select(instance => RunAsync(
            instance,
            ServiceKind.Sonarr,
            result,
            async () =>
            {
                var series = await _client
                    .GetAsync<List<SonarrSeries>>(instance, "series", cancellationToken)
                    .ConfigureAwait(false);

                return series.Select(s =>
                {
                    var stats = s.Statistics;
                    var episodeCount = stats?.EpisodeCount ?? 0;
                    var fileCount = stats?.EpisodeFileCount ?? 0;

                    return new SeriesDto
                    {
                        InstanceId = instance.Id,
                        ServiceName = instance.Name,
                        Id = s.Id,
                        Title = s.Title ?? "Unknown series",
                        SortTitle = s.SortTitle,
                        Year = s.Year,
                        Status = s.Status,
                        Monitored = s.Monitored,
                        Network = s.Network,
                        Overview = s.Overview,
                        SeasonCount = stats?.SeasonCount ?? 0,
                        EpisodeFileCount = fileCount,
                        EpisodeCount = episodeCount,
                        TotalEpisodeCount = stats?.TotalEpisodeCount ?? 0,
                        MissingEpisodeCount = Math.Max(0, episodeCount - fileCount),
                        PercentOfEpisodes = stats?.PercentOfEpisodes ?? 0,
                        SizeOnDisk = stats?.SizeOnDisk ?? 0,
                        NextAiring = s.NextAiring,
                        PreviousAiring = s.PreviousAiring,
                        Added = s.Added,
                        PosterUrl = PosterFor(instance, s.Images),
                        DetailUrl = SeriesDetailUrl(instance, s)
                    };
                }).ToList();
            }));

        await Task.WhenAll(tasks).ConfigureAwait(false);

        result.Items = result.Items
            .OrderBy(s => s.SortTitle ?? s.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return result;
    }

    // ---------------------------------------------------------------- movies

    public Task<AggregateResult<MovieDto>> GetMoviesAsync(CancellationToken cancellationToken)
        => Cache.GetOrAddAsync("movies", CacheSeconds, () => BuildMoviesAsync(cancellationToken));

    private async Task<AggregateResult<MovieDto>> BuildMoviesAsync(CancellationToken cancellationToken)
    {
        var result = new AggregateResult<MovieDto>();

        var tasks = RadarrInstances.Select(instance => RunAsync(
            instance,
            ServiceKind.Radarr,
            result,
            async () =>
            {
                var movies = await _client
                    .GetAsync<List<RadarrMovie>>(instance, "movie", cancellationToken)
                    .ConfigureAwait(false);

                return movies.Select(m => new MovieDto
                {
                    InstanceId = instance.Id,
                    ServiceName = instance.Name,
                    Id = m.Id,
                    Title = m.Title ?? "Unknown movie",
                    SortTitle = m.SortTitle,
                    Year = m.Year,
                    Status = m.Status,
                    Monitored = m.Monitored,
                    HasFile = m.HasFile,
                    IsAvailable = m.IsAvailable,
                    Overview = m.Overview,
                    Studio = m.Studio,
                    Certification = m.Certification,
                    Runtime = m.Runtime,
                    SizeOnDisk = m.SizeOnDisk != 0 ? m.SizeOnDisk : m.MovieFile?.Size ?? 0,
                    Quality = m.MovieFile?.Quality?.Quality?.Name,
                    InCinemas = m.InCinemas,
                    DigitalRelease = m.DigitalRelease,
                    PhysicalRelease = m.PhysicalRelease,
                    Added = m.Added,
                    PosterUrl = PosterFor(instance, m.Images),
                    DetailUrl = MovieDetailUrl(instance, m)
                }).ToList();
            }));

        await Task.WhenAll(tasks).ConfigureAwait(false);

        result.Items = result.Items
            .OrderBy(m => m.SortTitle ?? m.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return result;
    }

    // --------------------------------------------------------------- missing

    public Task<AggregateResult<MissingItemDto>> GetMissingAsync(int limit, CancellationToken cancellationToken)
        => Cache.GetOrAddAsync(
            "missing:" + limit.ToString(CultureInfo.InvariantCulture),
            CacheSeconds,
            () => BuildMissingAsync(limit, cancellationToken));

    private async Task<AggregateResult<MissingItemDto>> BuildMissingAsync(
        int limit,
        CancellationToken cancellationToken)
    {
        var result = new AggregateResult<MissingItemDto>();
        var pageSize = Math.Clamp(limit, 1, 500).ToString(CultureInfo.InvariantCulture);

        var sonarrTasks = SonarrInstances.Select(instance => RunAsync(
            instance,
            ServiceKind.Sonarr,
            result,
            async () =>
            {
                var query = new Dictionary<string, string?>
                {
                    ["page"] = "1",
                    ["pageSize"] = pageSize,
                    ["sortKey"] = "episodes.airDateUtc",
                    ["sortDirection"] = "descending",
                    ["includeSeries"] = "true",
                    ["monitored"] = "true"
                };

                var page = await _client
                    .GetAsync<ArrPagedResponse<SonarrEpisode>>(instance, "wanted/missing", query, cancellationToken)
                    .ConfigureAwait(false);

                return (page.Records ?? new List<SonarrEpisode>()).Select(e => new MissingItemDto
                {
                    InstanceId = instance.Id,
                    ServiceName = instance.Name,
                    Kind = ServiceKind.Sonarr,
                    Id = e.Id,
                    ParentId = e.SeriesId,
                    Title = e.Series?.Title ?? "Unknown series",
                    SubTitle = e.Title,
                    EpisodeCode = FormatEpisodeCode(e.SeasonNumber, e.EpisodeNumber),
                    ReleaseDate = e.AirDateUtc,
                    PosterUrl = PosterFor(instance, e.Series?.Images),
                    DetailUrl = SeriesDetailUrl(instance, e.Series)
                }).ToList();
            }));

        var radarrTasks = RadarrInstances.Select(instance => RunAsync(
            instance,
            ServiceKind.Radarr,
            result,
            async () =>
            {
                var query = new Dictionary<string, string?>
                {
                    ["page"] = "1",
                    ["pageSize"] = pageSize,
                    ["sortKey"] = "digitalRelease",
                    ["sortDirection"] = "descending",
                    ["monitored"] = "true"
                };

                var page = await _client
                    .GetAsync<ArrPagedResponse<RadarrMovie>>(instance, "wanted/missing", query, cancellationToken)
                    .ConfigureAwait(false);

                return (page.Records ?? new List<RadarrMovie>()).Select(m => new MissingItemDto
                {
                    InstanceId = instance.Id,
                    ServiceName = instance.Name,
                    Kind = ServiceKind.Radarr,
                    Id = m.Id,
                    ParentId = m.Id,
                    Title = m.Title ?? "Unknown movie",
                    SubTitle = m.Year > 0 ? m.Year.ToString(CultureInfo.InvariantCulture) : null,
                    ReleaseDate = m.DigitalRelease ?? m.PhysicalRelease ?? m.InCinemas,
                    PosterUrl = PosterFor(instance, m.Images),
                    DetailUrl = MovieDetailUrl(instance, m)
                }).ToList();
            }));

        await Task.WhenAll(sonarrTasks.Concat(radarrTasks)).ConfigureAwait(false);

        result.Items = result.Items
            .OrderByDescending(i => i.ReleaseDate ?? DateTime.MinValue)
            .ToList();

        return result;
    }

    // --------------------------------------------------------------- history

    public Task<AggregateResult<HistoryItemDto>> GetHistoryAsync(int limit, CancellationToken cancellationToken)
        => Cache.GetOrAddAsync(
            "history:" + limit.ToString(CultureInfo.InvariantCulture),
            CacheSeconds,
            () => BuildHistoryAsync(limit, cancellationToken));

    private async Task<AggregateResult<HistoryItemDto>> BuildHistoryAsync(
        int limit,
        CancellationToken cancellationToken)
    {
        var result = new AggregateResult<HistoryItemDto>();
        var pageSize = Math.Clamp(limit, 1, 500).ToString(CultureInfo.InvariantCulture);

        var query = new Dictionary<string, string?>
        {
            ["page"] = "1",
            ["pageSize"] = pageSize,
            ["sortKey"] = "date",
            ["sortDirection"] = "descending"
        };

        var sonarrTasks = SonarrInstances.Select(instance => RunAsync(
            instance,
            ServiceKind.Sonarr,
            result,
            async () =>
            {
                var sonarrQuery = new Dictionary<string, string?>(query)
                {
                    ["includeSeries"] = "true",
                    ["includeEpisode"] = "true"
                };

                var page = await _client
                    .GetAsync<ArrPagedResponse<SonarrHistoryRecord>>(instance, "history", sonarrQuery, cancellationToken)
                    .ConfigureAwait(false);

                return (page.Records ?? new List<SonarrHistoryRecord>()).Select(h => new HistoryItemDto
                {
                    InstanceId = instance.Id,
                    ServiceName = instance.Name,
                    Kind = ServiceKind.Sonarr,
                    Id = h.Id,
                    Title = h.Series?.Title ?? "Unknown series",
                    SubTitle = BuildEpisodeSubtitle(h.Episode),
                    SourceTitle = h.SourceTitle,
                    EventType = h.EventType,
                    Quality = h.Quality?.Quality?.Name,
                    Date = h.Date,
                    PosterUrl = PosterFor(instance, h.Series?.Images)
                }).ToList();
            }));

        var radarrTasks = RadarrInstances.Select(instance => RunAsync(
            instance,
            ServiceKind.Radarr,
            result,
            async () =>
            {
                var page = await _client
                    .GetAsync<ArrPagedResponse<RadarrHistoryRecord>>(instance, "history", query, cancellationToken)
                    .ConfigureAwait(false);

                return (page.Records ?? new List<RadarrHistoryRecord>()).Select(h => new HistoryItemDto
                {
                    InstanceId = instance.Id,
                    ServiceName = instance.Name,
                    Kind = ServiceKind.Radarr,
                    Id = h.Id,
                    Title = h.Movie?.Title ?? "Unknown movie",
                    SubTitle = h.Movie is { Year: > 0 } historyMovie
                        ? historyMovie.Year.ToString(CultureInfo.InvariantCulture)
                        : null,
                    SourceTitle = h.SourceTitle,
                    EventType = h.EventType,
                    Quality = h.Quality?.Quality?.Name,
                    Date = h.Date,
                    PosterUrl = PosterFor(instance, h.Movie?.Images)
                }).ToList();
            }));

        await Task.WhenAll(sonarrTasks.Concat(radarrTasks)).ConfigureAwait(false);

        result.Items = result.Items
            .OrderByDescending(i => i.Date)
            .Take(Math.Clamp(limit, 1, 500))
            .ToList();

        return result;
    }

    // --------------------------------------------------------------- summary

    public async Task<SummaryDto> GetSummaryAsync(CancellationToken cancellationToken)
    {
        var statusTask = GetStatusAsync(cancellationToken);
        var seriesTask = GetSeriesAsync(cancellationToken);
        var moviesTask = GetMoviesAsync(cancellationToken);
        var queueTask = GetQueueAsync(cancellationToken);

        await Task.WhenAll(statusTask, seriesTask, moviesTask, queueTask).ConfigureAwait(false);

        var status = await statusTask.ConfigureAwait(false);
        var series = await seriesTask.ConfigureAwait(false);
        var movies = await moviesTask.ConfigureAwait(false);
        var queue = await queueTask.ConfigureAwait(false);

        var summary = new SummaryDto
        {
            SeriesTotal = series.Items.Count,
            SeriesMonitored = series.Items.Count(s => s.Monitored),
            SeriesContinuing = series.Items.Count(s =>
                string.Equals(s.Status, "continuing", StringComparison.OrdinalIgnoreCase)),
            EpisodeFiles = series.Items.Sum(s => s.EpisodeFileCount),
            EpisodesMissing = series.Items.Where(s => s.Monitored).Sum(s => s.MissingEpisodeCount),
            SeriesSizeOnDisk = series.Items.Sum(s => s.SizeOnDisk),
            MoviesTotal = movies.Items.Count,
            MoviesMonitored = movies.Items.Count(m => m.Monitored),
            MoviesWithFile = movies.Items.Count(m => m.HasFile),
            MoviesMissing = movies.Items.Count(m => m.Monitored && !m.HasFile && m.IsAvailable),
            MoviesSizeOnDisk = movies.Items.Sum(m => m.SizeOnDisk),
            QueueCount = queue.Items.Count,
            QueueWarnings = queue.Items.Count(q =>
                !string.IsNullOrEmpty(q.ErrorMessage)
                || q.Warnings.Count > 0
                || string.Equals(q.TrackedDownloadStatus, "warning", StringComparison.OrdinalIgnoreCase)
                || string.Equals(q.TrackedDownloadStatus, "error", StringComparison.OrdinalIgnoreCase)),
            InstancesOnline = status.Count(s => s.Online),
            InstancesTotal = status.Count
        };

        summary.Errors = series.Errors
            .Concat(movies.Errors)
            .Concat(queue.Errors)
            .GroupBy(e => e.InstanceId, StringComparer.Ordinal)
            .Select(g => g.First())
            .ToList();

        return summary;
    }

    // -------------------------------------------------------------- commands

    public async Task SearchSeriesAsync(ArrInstance instance, int seriesId, CancellationToken cancellationToken)
    {
        await _client.PostCommandAsync(
            instance,
            new { name = "SeriesSearch", seriesId },
            cancellationToken).ConfigureAwait(false);

        _cache.Clear();
    }

    public async Task SearchEpisodesAsync(ArrInstance instance, int[] episodeIds, CancellationToken cancellationToken)
    {
        await _client.PostCommandAsync(
            instance,
            new { name = "EpisodeSearch", episodeIds },
            cancellationToken).ConfigureAwait(false);

        _cache.Clear();
    }

    public async Task SearchMoviesAsync(ArrInstance instance, int[] movieIds, CancellationToken cancellationToken)
    {
        await _client.PostCommandAsync(
            instance,
            new { name = "MoviesSearch", movieIds },
            cancellationToken).ConfigureAwait(false);

        _cache.Clear();
    }

    public async Task RemoveFromQueueAsync(
        ArrInstance instance,
        int queueId,
        bool removeFromClient,
        bool blocklist,
        CancellationToken cancellationToken)
    {
        var query = new Dictionary<string, string?>
        {
            ["removeFromClient"] = removeFromClient ? "true" : "false",
            ["blocklist"] = blocklist ? "true" : "false"
        };

        await _client.DeleteAsync(
            instance,
            "queue/" + queueId.ToString(CultureInfo.InvariantCulture),
            query,
            cancellationToken).ConfigureAwait(false);

        _cache.Clear();
    }

    public Task<ArrApiClient.AssetResult> GetImageAsync(
        ArrInstance instance,
        string path,
        CancellationToken cancellationToken)
        => _client.GetAssetAsync(instance, path, cancellationToken);

    public void InvalidateCache() => _cache.Clear();

    // --------------------------------------------------------------- helpers

    /// <summary>
    /// Runs one instance fetch, folding either its items or its failure into the shared result.
    /// </summary>
    private async Task RunAsync<T>(
        ArrInstance instance,
        string kind,
        AggregateResult<T> result,
        Func<Task<List<T>>> fetch)
    {
        try
        {
            var items = await fetch().ConfigureAwait(false);
            lock (result)
            {
                result.Items.AddRange(items);
            }
        }
        catch (Exception ex) when (ex is ArrRequestException or OperationCanceledException)
        {
            _logger.LogWarning("[ArrDashboard] {Kind} instance {Name} failed: {Message}", kind, instance.Name, ex.Message);
            lock (result)
            {
                result.Errors.Add(new InstanceError
                {
                    InstanceId = instance.Id,
                    Name = instance.Name,
                    Kind = kind,
                    Message = ex.Message
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[ArrDashboard] Unexpected error from {Kind} instance {Name}", kind, instance.Name);
            lock (result)
            {
                result.Errors.Add(new InstanceError
                {
                    InstanceId = instance.Id,
                    Name = instance.Name,
                    Kind = kind,
                    Message = "Unexpected error: " + ex.Message
                });
            }
        }
    }

    private static double Percent(double size, double sizeLeft)
    {
        if (size <= 0)
        {
            return 0;
        }

        var done = (size - sizeLeft) / size * 100d;
        return Math.Clamp(Math.Round(done, 1), 0, 100);
    }

    private static List<string> FlattenStatusMessages(List<ArrStatusMessage>? messages)
    {
        var flattened = new List<string>();
        if (messages is null)
        {
            return flattened;
        }

        foreach (var message in messages)
        {
            if (message.Messages is { Count: > 0 })
            {
                flattened.AddRange(message.Messages.Where(m => !string.IsNullOrWhiteSpace(m)));
            }
            else if (!string.IsNullOrWhiteSpace(message.Title))
            {
                flattened.Add(message.Title);
            }
        }

        return flattened;
    }

    private static string FormatEpisodeCode(int season, int episode)
        => "S" + season.ToString("00", CultureInfo.InvariantCulture)
            + "E" + episode.ToString("00", CultureInfo.InvariantCulture);

    private static string? BuildEpisodeSubtitle(SonarrEpisode? episode)
    {
        if (episode is null)
        {
            return null;
        }

        var code = FormatEpisodeCode(episode.SeasonNumber, episode.EpisodeNumber);
        return string.IsNullOrWhiteSpace(episode.Title) ? code : code + " - " + episode.Title;
    }

    /// <summary>
    /// Returns the plugin-relative proxy URL for an instance poster, or null when
    /// the instance exposes no usable local cover.
    /// </summary>
    private static string? PosterFor(ArrInstance instance, List<ArrImage>? images)
    {
        if (images is null || images.Count == 0)
        {
            return null;
        }

        var poster = images.FirstOrDefault(i =>
                string.Equals(i.CoverType, "poster", StringComparison.OrdinalIgnoreCase))
            ?? images[0];

        var path = poster.Url;
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        path = StripUrlBase(instance, path);

        return "ArrDashboard/Image?instanceId=" + Uri.EscapeDataString(instance.Id)
            + "&path=" + Uri.EscapeDataString(path);
    }

    /// <summary>
    /// When Sonarr/Radarr run behind a URL base, image paths already include it,
    /// and the configured base URL does too. Drop one copy.
    /// </summary>
    private static string StripUrlBase(ArrInstance instance, string path)
    {
        if (!Uri.TryCreate(instance.Url, UriKind.Absolute, out var baseUri))
        {
            return path;
        }

        var basePath = baseUri.AbsolutePath.TrimEnd('/');
        if (basePath.Length > 1 && path.StartsWith(basePath + "/", StringComparison.OrdinalIgnoreCase))
        {
            return path.Substring(basePath.Length);
        }

        return path;
    }

    private static string? SeriesDetailUrl(ArrInstance instance, SonarrSeries? series)
    {
        if (series is null || string.IsNullOrWhiteSpace(series.TitleSlug))
        {
            return null;
        }

        return instance.Url.TrimEnd('/') + "/series/" + series.TitleSlug;
    }

    private static string? MovieDetailUrl(ArrInstance instance, RadarrMovie? movie)
    {
        if (movie is null || string.IsNullOrWhiteSpace(movie.TitleSlug))
        {
            return null;
        }

        return instance.Url.TrimEnd('/') + "/movie/" + movie.TitleSlug;
    }
}
