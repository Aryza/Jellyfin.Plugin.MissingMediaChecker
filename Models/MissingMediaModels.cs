using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.MissingMediaChecker.Models;

// ── TV ────────────────────────────────────────────────────────────────────────

public class MissingEpisode
{
    [JsonPropertyName("seasonNumber")]  public int     SeasonNumber  { get; set; }
    [JsonPropertyName("episodeNumber")] public int     EpisodeNumber { get; set; }
    [JsonPropertyName("episodeName")]   public string? EpisodeName   { get; set; }
    [JsonPropertyName("airDate")]       public string? AirDate       { get; set; }
    [JsonPropertyName("overview")]      public string? Overview      { get; set; }
    [JsonPropertyName("tmdbId")]        public int     TmdbId        { get; set; }
}

public class SeriesMissingReport
{
    [JsonPropertyName("seriesName")]             public string  SeriesName             { get; set; } = string.Empty;
    [JsonPropertyName("jellyfinId")]             public Guid    JellyfinId             { get; set; }
    [JsonPropertyName("tmdbId")]                 public string? TmdbId                 { get; set; }
    [JsonPropertyName("totalEpisodesOnTmdb")]    public int     TotalEpisodesOnTmdb    { get; set; }
    [JsonPropertyName("totalEpisodesInLibrary")] public int     TotalEpisodesInLibrary { get; set; }
    [JsonPropertyName("missingCount")]           public int     MissingCount           { get; set; }
    /// <summary>Season numbers where every eligible episode is missing.</summary>
    [JsonPropertyName("missingSeasons")]         public List<int> MissingSeasons       { get; set; } = new();
    [JsonPropertyName("missingEpisodes")]        public List<MissingEpisode> MissingEpisodes { get; set; } = new();
}

// ── Skipped series ────────────────────────────────────────────────────────────

public class SkippedSeries
{
    [JsonPropertyName("seriesName")]   public string SeriesName   { get; set; } = string.Empty;
    /// <summary>Human-readable summary of which provider IDs were tried (e.g. "Tvdb:12345").</summary>
    [JsonPropertyName("availableIds")] public string AvailableIds { get; set; } = string.Empty;
}

// ── Movies / Collections ─────────────────────────────────────────────────────

public class MissingMovie
{
    [JsonPropertyName("title")]       public string  Title       { get; set; } = string.Empty;
    [JsonPropertyName("releaseDate")] public string? ReleaseDate { get; set; }
    [JsonPropertyName("year")]        public int?    Year        { get; set; }
    [JsonPropertyName("overview")]    public string? Overview    { get; set; }
    [JsonPropertyName("posterPath")]  public string? PosterPath  { get; set; }
    [JsonPropertyName("tmdbId")]      public int     TmdbId      { get; set; }
    [JsonPropertyName("voteAverage")] public double  VoteAverage { get; set; }
}

public class CollectionMissingReport
{
    [JsonPropertyName("collectionName")]          public string  CollectionName          { get; set; } = string.Empty;
    [JsonPropertyName("tmdbCollectionId")]         public int     TmdbCollectionId        { get; set; }
    [JsonPropertyName("jellyfinCollectionId")]     public Guid?   JellyfinCollectionId    { get; set; }
    [JsonPropertyName("totalMoviesInCollection")]  public int     TotalMoviesInCollection { get; set; }
    [JsonPropertyName("moviesInLibrary")]          public int     MoviesInLibrary         { get; set; }
    [JsonPropertyName("missingCount")]             public int     MissingCount            { get; set; }
    [JsonPropertyName("missingMovies")]            public List<MissingMovie> MissingMovies { get; set; } = new();
}

// ── Top-level scan result ─────────────────────────────────────────────────────

public class ScanResults
{
    [JsonPropertyName("scanTime")]                public DateTimeOffset ScanTime                { get; set; }
    [JsonPropertyName("totalSeriesInLibrary")]     public int            TotalSeriesInLibrary    { get; set; }
    [JsonPropertyName("totalSeriesChecked")]       public int            TotalSeriesChecked      { get; set; }
    [JsonPropertyName("seriesSkippedNoId")]        public int            SeriesSkippedNoId       { get; set; }
    [JsonPropertyName("totalCollectionsChecked")]  public int            TotalCollectionsChecked { get; set; }
    [JsonPropertyName("seriesWithMissing")]        public int            SeriesWithMissing       { get; set; }
    [JsonPropertyName("collectionsWithMissing")]   public int            CollectionsWithMissing  { get; set; }
    [JsonPropertyName("totalMissingEpisodes")]     public int            TotalMissingEpisodes    { get; set; }
    [JsonPropertyName("totalMissingMovies")]       public int            TotalMissingMovies      { get; set; }
    [JsonPropertyName("missingSeries")]            public List<SeriesMissingReport>     MissingSeries      { get; set; } = new();
    [JsonPropertyName("missingCollections")]       public List<CollectionMissingReport> MissingCollections { get; set; } = new();
    [JsonPropertyName("skippedSeries")]            public List<SkippedSeries>           SkippedSeries      { get; set; } = new();
}
