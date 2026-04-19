using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.MissingMediaChecker.Models;

// ── TV ────────────────────────────────────────────────────────────────────────

public class MissingEpisode
{
    public int     SeasonNumber  { get; set; }
    public int     EpisodeNumber { get; set; }
    public string? EpisodeName   { get; set; }
    public string? AirDate       { get; set; }
    public string? Overview      { get; set; }
    public int     TmdbId        { get; set; }
}

public class SeriesMissingReport
{
    public string  SeriesName              { get; set; } = string.Empty;
    public Guid    JellyfinId              { get; set; }
    public string? TmdbId                  { get; set; }
    public int     TotalEpisodesOnTmdb     { get; set; }
    public int     TotalEpisodesInLibrary  { get; set; }
    public int     MissingCount            { get; set; }
    public List<MissingEpisode> MissingEpisodes { get; set; } = new();
}

// ── Movies / Collections ─────────────────────────────────────────────────────

public class MissingMovie
{
    public string  Title        { get; set; } = string.Empty;
    public string? ReleaseDate  { get; set; }
    public int?    Year         { get; set; }
    public string? Overview     { get; set; }
    public string? PosterPath   { get; set; }
    public int     TmdbId       { get; set; }
    public double  VoteAverage  { get; set; }
}

public class CollectionMissingReport
{
    public string CollectionName         { get; set; } = string.Empty;
    public int    TmdbCollectionId       { get; set; }
    public int    TotalMoviesInCollection { get; set; }
    public int    MoviesInLibrary        { get; set; }
    public int    MissingCount           { get; set; }
    public List<MissingMovie> MissingMovies { get; set; } = new();
}

// ── Top-level scan result ─────────────────────────────────────────────────────

public class ScanResults
{
    public DateTimeOffset ScanTime                { get; set; }
    public int            TotalSeriesChecked      { get; set; }
    public int            TotalCollectionsChecked { get; set; }
    public int            SeriesWithMissing       { get; set; }
    public int            CollectionsWithMissing  { get; set; }
    public int            TotalMissingEpisodes    { get; set; }
    public int            TotalMissingMovies      { get; set; }
    public List<SeriesMissingReport>    MissingSeries      { get; set; } = new();
    public List<CollectionMissingReport> MissingCollections { get; set; } = new();
}
