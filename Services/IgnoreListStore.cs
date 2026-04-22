using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.MissingMediaChecker.Services;

/// <summary>
/// Lets the user hide specific series / seasons / episodes / collections /
/// movies from the report without deleting library metadata.
///
/// Stored as JSON (small document, modified via the plugin UI) next to the
/// scan results. Keyed off TMDB IDs so the list survives library rebuilds.
/// </summary>
public sealed class IgnoreList
{
    [JsonPropertyName("seriesTmdbIds")]
    public HashSet<string> SeriesTmdbIds { get; set; } = new();

    /// <summary>Per-series season number ignores: seriesTmdbId → [seasonNumbers].</summary>
    [JsonPropertyName("seasons")]
    public Dictionary<string, HashSet<int>> Seasons { get; set; } = new();

    /// <summary>Per-series episode ignores: seriesTmdbId → ["SxEy" | "tmdbEpisodeId"].</summary>
    [JsonPropertyName("episodes")]
    public Dictionary<string, HashSet<string>> Episodes { get; set; } = new();

    [JsonPropertyName("collectionTmdbIds")]
    public HashSet<int> CollectionTmdbIds { get; set; } = new();

    [JsonPropertyName("movieTmdbIds")]
    public HashSet<int> MovieTmdbIds { get; set; } = new();

    /// <summary>
    /// Human-readable labels for ignored items so the UI can show "The Matrix"
    /// instead of raw TMDB IDs. Keys follow <see cref="LabelKey"/>; missing
    /// entries fall back to the ID in the UI.
    /// </summary>
    [JsonPropertyName("labels")]
    public Dictionary<string, string> Labels { get; set; } = new();

    /// <summary>
    /// Label key builder. Examples:
    ///   series:123, season:123:2, episode:123:S01E05, collection:7, movie:42
    /// </summary>
    public static string LabelKey(string kind, string? a = null, string? b = null)
        => b is null ? $"{kind}:{a}" : $"{kind}:{a}:{b}";

    // ── lookups (fast-path; nothing allocating) ───────────────────────────────

    public bool IsSeriesIgnored(string? tmdbId)
        => !string.IsNullOrEmpty(tmdbId) && SeriesTmdbIds.Contains(tmdbId);

    public bool IsSeasonIgnored(string? seriesTmdbId, int seasonNumber)
        => !string.IsNullOrEmpty(seriesTmdbId)
           && Seasons.TryGetValue(seriesTmdbId, out var seasons)
           && seasons.Contains(seasonNumber);

    public bool IsEpisodeIgnored(string? seriesTmdbId, int seasonNumber, int episodeNumber, int tmdbEpisodeId)
    {
        if (string.IsNullOrEmpty(seriesTmdbId)) return false;
        if (!Episodes.TryGetValue(seriesTmdbId, out var eps)) return false;
        if (eps.Contains(Key(seasonNumber, episodeNumber))) return true;
        if (tmdbEpisodeId > 0 && eps.Contains(tmdbEpisodeId.ToString())) return true;
        return false;
    }

    public bool IsCollectionIgnored(int tmdbCollectionId)
        => CollectionTmdbIds.Contains(tmdbCollectionId);

    public bool IsMovieIgnored(int tmdbMovieId)
        => MovieTmdbIds.Contains(tmdbMovieId);

    public static string Key(int season, int episode)
        => $"S{season:D2}E{episode:D2}";
}

/// <summary>
/// Persists and loads the <see cref="IgnoreList"/> to disk. File is rewritten
/// atomically on every PUT so concurrent readers always see a consistent view.
/// </summary>
public static class IgnoreListStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented               = true,
        PropertyNamingPolicy        = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private static readonly object _lock = new();
    private static IgnoreList?     _cached;

    public static IgnoreList Load(string path)
    {
        lock (_lock)
        {
            if (_cached is not null) return _cached;
            try
            {
                if (File.Exists(path))
                {
                    var json = File.ReadAllText(path);
                    _cached = JsonSerializer.Deserialize<IgnoreList>(json, JsonOpts) ?? new IgnoreList();
                }
                else
                {
                    _cached = new IgnoreList();
                }
            }
            catch
            {
                _cached = new IgnoreList();
            }
            return _cached;
        }
    }

    public static void Save(string path, IgnoreList list)
    {
        lock (_lock)
        {
            try
            {
                var tmp = path + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(list, JsonOpts));
                File.Move(tmp, path, overwrite: true);
                _cached = list;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"MissingMediaChecker: failed to save ignore list — {ex.Message}");
            }
        }
    }

    /// <summary>Force the next Load() to re-read from disk (for tests / manual edits).</summary>
    public static void Invalidate()
    {
        lock (_lock) { _cached = null; }
    }
}
