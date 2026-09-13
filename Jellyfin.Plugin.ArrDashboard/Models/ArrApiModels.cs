using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.ArrDashboard.Models;

// Wire models for the Sonarr v3 / Radarr v3 REST APIs. Only the fields the
// dashboard actually renders are declared; everything else is ignored by
// System.Text.Json. Deserialization is case-insensitive, so camelCase JSON
// binds straight onto these PascalCase properties.

public class ArrSystemStatus
{
    public string? Version { get; set; }

    public string? AppName { get; set; }

    public string? InstanceName { get; set; }
}

public class ArrImage
{
    public string? CoverType { get; set; }

    public string? Url { get; set; }

    public string? RemoteUrl { get; set; }
}

public class ArrQualityName
{
    public string? Name { get; set; }
}

public class ArrQuality
{
    public ArrQualityName? Quality { get; set; }
}

public class ArrStatusMessage
{
    public string? Title { get; set; }

    public List<string>? Messages { get; set; }
}

public class ArrPagedResponse<T>
{
    public int Page { get; set; }

    public int PageSize { get; set; }

    public int TotalRecords { get; set; }

    public List<T>? Records { get; set; }
}

public class SonarrSeriesStatistics
{
    public int SeasonCount { get; set; }

    public int EpisodeFileCount { get; set; }

    public int EpisodeCount { get; set; }

    public int TotalEpisodeCount { get; set; }

    public long SizeOnDisk { get; set; }

    public double PercentOfEpisodes { get; set; }
}

public class SonarrSeries
{
    public int Id { get; set; }

    public string? Title { get; set; }

    public string? SortTitle { get; set; }

    /// <summary>continuing, ended, upcoming, deleted.</summary>
    public string? Status { get; set; }

    public bool Ended { get; set; }

    public string? Overview { get; set; }

    public int Year { get; set; }

    public bool Monitored { get; set; }

    public string? Network { get; set; }

    public string? SeriesType { get; set; }

    public int Runtime { get; set; }

    public DateTime? NextAiring { get; set; }

    public DateTime? PreviousAiring { get; set; }

    public DateTime? Added { get; set; }

    public int TvdbId { get; set; }

    public string? TitleSlug { get; set; }

    public string? Path { get; set; }

    public List<ArrImage>? Images { get; set; }

    public SonarrSeriesStatistics? Statistics { get; set; }
}

public class SonarrEpisode
{
    public int Id { get; set; }

    public int SeriesId { get; set; }

    public int EpisodeFileId { get; set; }

    public int SeasonNumber { get; set; }

    public int EpisodeNumber { get; set; }

    public int? AbsoluteEpisodeNumber { get; set; }

    public string? Title { get; set; }

    public string? Overview { get; set; }

    public DateTime? AirDateUtc { get; set; }

    public bool HasFile { get; set; }

    public bool Monitored { get; set; }

    public int? Runtime { get; set; }

    public SonarrSeries? Series { get; set; }
}

public class SonarrQueueRecord
{
    public int Id { get; set; }

    public int SeriesId { get; set; }

    public int EpisodeId { get; set; }

    public string? Title { get; set; }

    public double Size { get; set; }

    public double Sizeleft { get; set; }

    public string? Timeleft { get; set; }

    public DateTime? EstimatedCompletionTime { get; set; }

    public string? Status { get; set; }

    public string? TrackedDownloadStatus { get; set; }

    public string? TrackedDownloadState { get; set; }

    public string? DownloadClient { get; set; }

    public string? Protocol { get; set; }

    public string? Indexer { get; set; }

    public string? ErrorMessage { get; set; }

    public ArrQuality? Quality { get; set; }

    public SonarrSeries? Series { get; set; }

    public SonarrEpisode? Episode { get; set; }

    public List<ArrStatusMessage>? StatusMessages { get; set; }
}

public class SonarrHistoryRecord
{
    public int Id { get; set; }

    public int SeriesId { get; set; }

    public int EpisodeId { get; set; }

    public string? SourceTitle { get; set; }

    public DateTime Date { get; set; }

    public string? EventType { get; set; }

    public ArrQuality? Quality { get; set; }

    public SonarrSeries? Series { get; set; }

    public SonarrEpisode? Episode { get; set; }
}

public class RadarrMovieFile
{
    public long Size { get; set; }

    public ArrQuality? Quality { get; set; }

    public DateTime? DateAdded { get; set; }
}

public class RadarrMovie
{
    public int Id { get; set; }

    public string? Title { get; set; }

    public string? OriginalTitle { get; set; }

    public string? SortTitle { get; set; }

    public int Year { get; set; }

    public string? Overview { get; set; }

    /// <summary>tba, announced, inCinemas, released, deleted.</summary>
    public string? Status { get; set; }

    public bool Monitored { get; set; }

    public bool HasFile { get; set; }

    public bool IsAvailable { get; set; }

    public long SizeOnDisk { get; set; }

    public int Runtime { get; set; }

    public DateTime? Added { get; set; }

    public DateTime? InCinemas { get; set; }

    public DateTime? PhysicalRelease { get; set; }

    public DateTime? DigitalRelease { get; set; }

    public int TmdbId { get; set; }

    public string? ImdbId { get; set; }

    public string? TitleSlug { get; set; }

    public string? Studio { get; set; }

    public string? Certification { get; set; }

    public string? Path { get; set; }

    public List<ArrImage>? Images { get; set; }

    public RadarrMovieFile? MovieFile { get; set; }
}

public class RadarrQueueRecord
{
    public int Id { get; set; }

    public int MovieId { get; set; }

    public string? Title { get; set; }

    public double Size { get; set; }

    public double Sizeleft { get; set; }

    public string? Timeleft { get; set; }

    public DateTime? EstimatedCompletionTime { get; set; }

    public string? Status { get; set; }

    public string? TrackedDownloadStatus { get; set; }

    public string? TrackedDownloadState { get; set; }

    public string? DownloadClient { get; set; }

    public string? Protocol { get; set; }

    public string? Indexer { get; set; }

    public string? ErrorMessage { get; set; }

    public ArrQuality? Quality { get; set; }

    public RadarrMovie? Movie { get; set; }

    public List<ArrStatusMessage>? StatusMessages { get; set; }
}

public class RadarrHistoryRecord
{
    public int Id { get; set; }

    public int MovieId { get; set; }

    public string? SourceTitle { get; set; }

    public DateTime Date { get; set; }

    public string? EventType { get; set; }

    public ArrQuality? Quality { get; set; }

    public RadarrMovie? Movie { get; set; }
}
