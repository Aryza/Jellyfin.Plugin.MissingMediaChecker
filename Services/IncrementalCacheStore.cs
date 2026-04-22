using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Jellyfin.Plugin.MissingMediaChecker.Models;

namespace Jellyfin.Plugin.MissingMediaChecker.Services;

/// <summary>
/// Per-series fingerprint stored between scans. If the fingerprint still
/// matches AND the series is "Ended" AND we've scanned it recently, we can
/// skip the TMDB round-trip and re-use the cached report wholesale.
/// </summary>
public sealed class IncrementalCacheEntry
{
    [JsonPropertyName("tmdbStatus")]        public string? TmdbStatus        { get; set; }
    [JsonPropertyName("lastMediaAddedUtc")] public DateTime? LastMediaAddedUtc { get; set; }
    [JsonPropertyName("ownedEpisodeCount")] public int     OwnedEpisodeCount { get; set; }
    [JsonPropertyName("scannedAtUtc")]      public DateTime ScannedAtUtc     { get; set; }
    [JsonPropertyName("report")]            public SeriesMissingReport? Report { get; set; }
}

public sealed class IncrementalCache
{
    /// <summary>seriesJellyfinId (as string) → fingerprint.</summary>
    [JsonPropertyName("entries")]
    public Dictionary<string, IncrementalCacheEntry> Entries { get; set; } = new();
}

public static class IncrementalCacheStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented               = false,
        PropertyNamingPolicy        = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private static readonly object _lock = new();

    public static IncrementalCache Load(string path)
    {
        lock (_lock)
        {
            try
            {
                if (!File.Exists(path)) return new IncrementalCache();
                return JsonSerializer.Deserialize<IncrementalCache>(
                    File.ReadAllText(path), JsonOpts) ?? new IncrementalCache();
            }
            catch
            {
                return new IncrementalCache();
            }
        }
    }

    public static void Save(string path, IncrementalCache cache)
    {
        lock (_lock)
        {
            try
            {
                var tmp = path + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(cache, JsonOpts));
                File.Move(tmp, path, overwrite: true);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"MissingMediaChecker: failed to save incremental cache — {ex.Message}");
            }
        }
    }
}
