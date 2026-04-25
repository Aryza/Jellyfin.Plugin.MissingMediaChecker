using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.MissingMediaChecker.Models;

/// <summary>
/// One upcoming-release row shown on the Release Calendar tab.
/// "kind" selects rendering: "episode" → next episode of an owned series;
/// "movie"   → unreleased film in a box-set the user owns at least one part of.
/// </summary>
public class CalendarItem
{
    [JsonPropertyName("kind")]         public string  Kind         { get; set; } = string.Empty;
    [JsonPropertyName("date")]         public string  Date         { get; set; } = string.Empty; // ISO yyyy-MM-dd
    [JsonPropertyName("title")]        public string  Title        { get; set; } = string.Empty;
    [JsonPropertyName("subtitle")]     public string? Subtitle     { get; set; }
    [JsonPropertyName("overview")]     public string? Overview     { get; set; }
    [JsonPropertyName("posterPath")]   public string? PosterPath   { get; set; }
    [JsonPropertyName("tmdbParentId")] public int     TmdbParentId { get; set; }
    [JsonPropertyName("tmdbItemId")]   public int     TmdbItemId   { get; set; }
    [JsonPropertyName("seasonNumber")] public int?    SeasonNumber { get; set; }
    [JsonPropertyName("episodeNumber")] public int?   EpisodeNumber { get; set; }
    [JsonPropertyName("jellyfinId")]   public Guid?   JellyfinId   { get; set; }
}

public class CalendarResponse
{
    [JsonPropertyName("generatedAt")] public DateTimeOffset    GeneratedAt { get; set; }
    [JsonPropertyName("lookaheadDays")] public int             LookaheadDays { get; set; }
    [JsonPropertyName("seriesScanned")] public int             SeriesScanned { get; set; }
    [JsonPropertyName("collectionsScanned")] public int        CollectionsScanned { get; set; }
    [JsonPropertyName("items")]       public List<CalendarItem> Items       { get; set; } = new();
}
