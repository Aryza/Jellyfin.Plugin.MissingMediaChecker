using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.MissingMediaChecker.Models;

// ── TV ────────────────────────────────────────────────────────────────────────

public class TmdbSeriesDetails
{
    [JsonPropertyName("id")]               public int    Id              { get; set; }
    [JsonPropertyName("name")]             public string Name            { get; set; } = string.Empty;
    [JsonPropertyName("status")]           public string? Status         { get; set; }
    [JsonPropertyName("poster_path")]      public string? PosterPath     { get; set; }
    [JsonPropertyName("number_of_seasons")]  public int  NumberOfSeasons  { get; set; }
    [JsonPropertyName("number_of_episodes")] public int  NumberOfEpisodes { get; set; }
    [JsonPropertyName("seasons")]          public List<TmdbSeasonSummary> Seasons { get; set; } = new();
}

public class TmdbSeasonSummary
{
    [JsonPropertyName("id")]             public int    Id            { get; set; }
    [JsonPropertyName("season_number")]  public int    SeasonNumber  { get; set; }
    [JsonPropertyName("episode_count")]  public int    EpisodeCount  { get; set; }
    [JsonPropertyName("name")]           public string? Name         { get; set; }
    [JsonPropertyName("air_date")]       public string? AirDate      { get; set; }
}

public class TmdbSeasonDetails
{
    [JsonPropertyName("id")]            public int    Id           { get; set; }
    [JsonPropertyName("season_number")] public int    SeasonNumber { get; set; }
    [JsonPropertyName("name")]          public string? Name        { get; set; }
    [JsonPropertyName("episodes")]      public List<TmdbEpisode> Episodes { get; set; } = new();
}

/// <summary>
/// A /tv/{id} response with an append_to_response clause. Season detail blocks
/// arrive as dynamic top-level keys named "season/0", "season/1", … and are
/// captured by JsonExtensionData so we don't need a property per season number.
/// </summary>
public class TmdbSeriesWithExtras : TmdbSeriesDetails
{
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extras { get; set; }
}

public class TmdbEpisode
{
    [JsonPropertyName("id")]             public int    Id            { get; set; }
    [JsonPropertyName("episode_number")] public int    EpisodeNumber { get; set; }
    [JsonPropertyName("season_number")]  public int    SeasonNumber  { get; set; }
    [JsonPropertyName("name")]           public string? Name         { get; set; }
    [JsonPropertyName("air_date")]       public string? AirDate      { get; set; }
    [JsonPropertyName("overview")]       public string? Overview     { get; set; }
}

// ── Movies / Collections ─────────────────────────────────────────────────────

public class TmdbMovieDetails
{
    [JsonPropertyName("id")]                    public int    Id                   { get; set; }
    [JsonPropertyName("title")]                 public string Title                { get; set; } = string.Empty;
    [JsonPropertyName("release_date")]          public string? ReleaseDate         { get; set; }
    [JsonPropertyName("belongs_to_collection")] public TmdbCollectionRef? BelongsToCollection { get; set; }
}

public class TmdbCollectionRef
{
    [JsonPropertyName("id")]   public int    Id   { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
}

public class TmdbCollectionDetails
{
    [JsonPropertyName("id")]          public int    Id          { get; set; }
    [JsonPropertyName("name")]        public string Name        { get; set; } = string.Empty;
    [JsonPropertyName("overview")]    public string? Overview    { get; set; }
    [JsonPropertyName("poster_path")] public string? PosterPath  { get; set; }
    [JsonPropertyName("parts")]       public List<TmdbMoviePart> Parts { get; set; } = new();
}

public class TmdbMoviePart
{
    [JsonPropertyName("id")]           public int    Id           { get; set; }
    [JsonPropertyName("title")]        public string Title        { get; set; } = string.Empty;
    [JsonPropertyName("release_date")] public string? ReleaseDate { get; set; }
    [JsonPropertyName("overview")]     public string? Overview    { get; set; }
    [JsonPropertyName("poster_path")]  public string? PosterPath  { get; set; }
    [JsonPropertyName("vote_average")] public double  VoteAverage { get; set; }
}

// ── /find endpoint ────────────────────────────────────────────────────────────

public class TmdbFindResult
{
    [JsonPropertyName("tv_results")]    public List<TmdbFindEntry> TvResults    { get; set; } = new();
    [JsonPropertyName("movie_results")] public List<TmdbFindEntry> MovieResults { get; set; } = new();
}

public class TmdbFindEntry
{
    [JsonPropertyName("id")] public int Id { get; set; }
}
