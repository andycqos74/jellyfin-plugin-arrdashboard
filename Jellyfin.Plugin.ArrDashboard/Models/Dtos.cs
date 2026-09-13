using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.ArrDashboard.Models;

/// <summary>
/// Which kind of backend an item came from.
/// </summary>
public static class ServiceKind
{
    public const string Sonarr = "sonarr";

    public const string Radarr = "radarr";
}

/// <summary>
/// Reachability of one configured instance.
/// </summary>
public class ServiceStatusDto
{
    public string InstanceId { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Kind { get; set; } = string.Empty;

    public string Url { get; set; } = string.Empty;

    public bool Online { get; set; }

    public string? Version { get; set; }

    public string? Error { get; set; }

    public long ResponseMs { get; set; }
}

/// <summary>
/// Envelope returned by every aggregating endpoint: partial results plus the
/// instances that failed, so one dead server never blanks the whole page.
/// </summary>
/// <typeparam name="T">Item type.</typeparam>
public class AggregateResult<T>
{
    public List<T> Items { get; set; } = new List<T>();

    public List<InstanceError> Errors { get; set; } = new List<InstanceError>();
}

public class InstanceError
{
    public string InstanceId { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Kind { get; set; } = string.Empty;

    public string Message { get; set; } = string.Empty;
}

/// <summary>
/// One dated release: an episode airing, or a movie cinema/digital/physical date.
/// </summary>
public class CalendarItemDto
{
    public string InstanceId { get; set; } = string.Empty;

    public string ServiceName { get; set; } = string.Empty;

    public string Kind { get; set; } = string.Empty;

    public int Id { get; set; }

    public int ParentId { get; set; }

    public string Title { get; set; } = string.Empty;

    public string? SubTitle { get; set; }

    public string? EpisodeCode { get; set; }

    public DateTime AirDateUtc { get; set; }

    /// <summary>airing, cinemas, digital or physical.</summary>
    public string EventType { get; set; } = "airing";

    public bool HasFile { get; set; }

    public bool Monitored { get; set; }

    public bool Downloading { get; set; }

    public string? Network { get; set; }

    public int Runtime { get; set; }

    public string? Overview { get; set; }

    public string? PosterUrl { get; set; }

    public string? DetailUrl { get; set; }

    public bool SeasonPremiere { get; set; }
}

/// <summary>
/// A row in the download queue, as reported by Sonarr/Radarr.
/// </summary>
public class QueueItemDto
{
    public string InstanceId { get; set; } = string.Empty;

    public string ServiceName { get; set; } = string.Empty;

    public string Kind { get; set; } = string.Empty;

    /// <summary>Queue record id - the handle used to remove or blocklist the item.</summary>
    public int Id { get; set; }

    /// <summary>Series id (Sonarr) or movie id (Radarr).</summary>
    public int ParentId { get; set; }

    /// <summary>Episode id for Sonarr items, 0 for Radarr.</summary>
    public int EpisodeId { get; set; }

    public string Title { get; set; } = string.Empty;

    public string? SubTitle { get; set; }

    public string? ReleaseTitle { get; set; }

    public double Size { get; set; }

    public double SizeLeft { get; set; }

    public double Progress { get; set; }

    public string? TimeLeft { get; set; }

    public DateTime? EstimatedCompletionTime { get; set; }

    public string? Status { get; set; }

    public string? TrackedDownloadStatus { get; set; }

    public string? TrackedDownloadState { get; set; }

    public string? Protocol { get; set; }

    public string? DownloadClient { get; set; }

    public string? Indexer { get; set; }

    public string? Quality { get; set; }

    public string? ErrorMessage { get; set; }

    public List<string> Warnings { get; set; } = new List<string>();

    public string? PosterUrl { get; set; }
}

/// <summary>
/// Series status card.
/// </summary>
public class SeriesDto
{
    public string InstanceId { get; set; } = string.Empty;

    public string ServiceName { get; set; } = string.Empty;

    public int Id { get; set; }

    public string Title { get; set; } = string.Empty;

    public string? SortTitle { get; set; }

    public int Year { get; set; }

    public string? Status { get; set; }

    public bool Monitored { get; set; }

    public string? Network { get; set; }

    public string? Overview { get; set; }

    public int SeasonCount { get; set; }

    public int EpisodeFileCount { get; set; }

    public int EpisodeCount { get; set; }

    public int TotalEpisodeCount { get; set; }

    public int MissingEpisodeCount { get; set; }

    public double PercentOfEpisodes { get; set; }

    public long SizeOnDisk { get; set; }

    public DateTime? NextAiring { get; set; }

    public DateTime? PreviousAiring { get; set; }

    public DateTime? Added { get; set; }

    public string? PosterUrl { get; set; }

    public string? DetailUrl { get; set; }
}

/// <summary>
/// Movie status card.
/// </summary>
public class MovieDto
{
    public string InstanceId { get; set; } = string.Empty;

    public string ServiceName { get; set; } = string.Empty;

    public int Id { get; set; }

    public string Title { get; set; } = string.Empty;

    public string? SortTitle { get; set; }

    public int Year { get; set; }

    public string? Status { get; set; }

    public bool Monitored { get; set; }

    public bool HasFile { get; set; }

    public bool IsAvailable { get; set; }

    public string? Overview { get; set; }

    public string? Studio { get; set; }

    public string? Certification { get; set; }

    public int Runtime { get; set; }

    public long SizeOnDisk { get; set; }

    public string? Quality { get; set; }

    public DateTime? InCinemas { get; set; }

    public DateTime? DigitalRelease { get; set; }

    public DateTime? PhysicalRelease { get; set; }

    public DateTime? Added { get; set; }

    public string? PosterUrl { get; set; }

    public string? DetailUrl { get; set; }
}

/// <summary>
/// A monitored item with no file yet.
/// </summary>
public class MissingItemDto
{
    public string InstanceId { get; set; } = string.Empty;

    public string ServiceName { get; set; } = string.Empty;

    public string Kind { get; set; } = string.Empty;

    public int Id { get; set; }

    public int ParentId { get; set; }

    public string Title { get; set; } = string.Empty;

    public string? SubTitle { get; set; }

    public string? EpisodeCode { get; set; }

    public DateTime? ReleaseDate { get; set; }

    public string? PosterUrl { get; set; }

    public string? DetailUrl { get; set; }
}

/// <summary>
/// Recent grab, import or failure event.
/// </summary>
public class HistoryItemDto
{
    public string InstanceId { get; set; } = string.Empty;

    public string ServiceName { get; set; } = string.Empty;

    public string Kind { get; set; } = string.Empty;

    public int Id { get; set; }

    public string Title { get; set; } = string.Empty;

    public string? SubTitle { get; set; }

    public string? SourceTitle { get; set; }

    public string? EventType { get; set; }

    public string? Quality { get; set; }

    public DateTime Date { get; set; }

    public string? PosterUrl { get; set; }
}

/// <summary>
/// Counters for the header strip.
/// </summary>
public class SummaryDto
{
    public int SeriesTotal { get; set; }

    public int SeriesMonitored { get; set; }

    public int SeriesContinuing { get; set; }

    public int EpisodeFiles { get; set; }

    public int EpisodesMissing { get; set; }

    public long SeriesSizeOnDisk { get; set; }

    public int MoviesTotal { get; set; }

    public int MoviesMonitored { get; set; }

    public int MoviesWithFile { get; set; }

    public int MoviesMissing { get; set; }

    public long MoviesSizeOnDisk { get; set; }

    public int QueueCount { get; set; }

    public int QueueWarnings { get; set; }

    public int InstancesOnline { get; set; }

    public int InstancesTotal { get; set; }

    public List<InstanceError> Errors { get; set; } = new List<InstanceError>();
}
