using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.MissingMediaChecker.Models;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MissingMediaChecker.Services;

/// <summary>
/// Builds the Release Calendar: upcoming next-episode-to-air dates for every
/// owned series plus future-release movies that belong to a Jellyfin BoxSet
/// the user already owns at least one part of.
///
/// Each call hits TMDB once per series and once per box-set collection. Result
/// is cached via <see cref="SectionContentCache"/> so consecutive UI refreshes
/// don't re-scan the library or re-hit TMDB.
/// </summary>
public sealed class CalendarService
{
    private const int ConcurrentTmdbRequests = 8;
    private const string CacheKey = "calendar.v1";

    private readonly ILibraryManager _library;
    private readonly ILogger         _logger;

    public CalendarService(ILibraryManager library, ILogger logger)
    {
        _library = library;
        _logger  = logger;
    }

    public Task<CalendarResponse?> GetAsync(
        string apiKey,
        int lookaheadDays,
        TimeSpan cacheTtl,
        bool forceRefresh,
        CancellationToken ct)
    {
        if (forceRefresh) SectionContentCache.Invalidate(CacheKey);

        return SectionContentCache.GetOrLoadAsync<CalendarResponse>(
            CacheKey, cacheTtl,
            innerCt => BuildAsync(apiKey, lookaheadDays, innerCt),
            ct);
    }

    private async Task<CalendarResponse?> BuildAsync(string apiKey, int lookaheadDays, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(apiKey)) return null;

        using var tmdb = new TmdbService(apiKey, _logger);

        var today  = DateTime.UtcNow.Date;
        var cutoff = today.AddDays(Math.Max(1, lookaheadDays));

        using var sem = new SemaphoreSlim(ConcurrentTmdbRequests, ConcurrentTmdbRequests);
        var parallelOpts = new ParallelOptions
        {
            MaxDegreeOfParallelism = ConcurrentTmdbRequests,
            CancellationToken      = ct
        };

        var items = new ConcurrentBag<CalendarItem>();

        // ── 1) TV: next_episode_to_air for owned series ──────────────────────
        var seriesList = LibraryBridge.SeriesWithTmdb(_library).ToList();
        await Parallel.ForEachAsync(seriesList, parallelOpts, async (entry, innerCt) =>
        {
            var (series, tmdbId) = entry;
            await sem.WaitAsync(innerCt).ConfigureAwait(false);
            try
            {
                var details = await tmdb.GetSeriesAsync(tmdbId.ToString(), innerCt).ConfigureAwait(false);
                var next    = details?.NextEpisodeToAir;
                if (next is null || string.IsNullOrEmpty(next.AirDate)) return;
                if (!DateTime.TryParse(next.AirDate, out var airDate)) return;
                if (airDate.Date < today || airDate.Date > cutoff) return;

                items.Add(new CalendarItem
                {
                    Kind          = "episode",
                    Date          = airDate.ToString("yyyy-MM-dd"),
                    Title         = series.Name,
                    Subtitle      = $"S{next.SeasonNumber:00}E{next.EpisodeNumber:00}" +
                                    (string.IsNullOrEmpty(next.Name) ? string.Empty : $" · {next.Name}"),
                    Overview      = next.Overview,
                    PosterPath    = details?.PosterPath,
                    TmdbParentId  = tmdbId,
                    TmdbItemId    = next.Id,
                    SeasonNumber  = next.SeasonNumber,
                    EpisodeNumber = next.EpisodeNumber,
                    JellyfinId    = series.Id
                });
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "Calendar: TV fetch failed for {Series}", series.Name);
            }
            finally { sem.Release(); }
        }).ConfigureAwait(false);

        // ── 2) Movies: future-release parts of owned box-set collections ─────
        var boxSets = _library.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.BoxSet },
            Recursive        = true
        });

        var collectionIds = new Dictionary<int, Guid>();
        foreach (var bs in boxSets)
        {
            if (!bs.ProviderIds.TryGetValue("Tmdb", out var idStr)) continue;
            if (!int.TryParse(idStr, out var colId) || colId <= 0) continue;
            collectionIds.TryAdd(colId, bs.Id);
        }

        await Parallel.ForEachAsync(collectionIds, parallelOpts, async (kv, innerCt) =>
        {
            var (colId, jfBoxSetId) = (kv.Key, kv.Value);
            await sem.WaitAsync(innerCt).ConfigureAwait(false);
            try
            {
                var col = await tmdb.GetCollectionAsync(colId, innerCt).ConfigureAwait(false);
                if (col is null) return;

                foreach (var part in col.Parts)
                {
                    if (string.IsNullOrEmpty(part.ReleaseDate)) continue;
                    if (!DateTime.TryParse(part.ReleaseDate, out var rd)) continue;
                    if (rd.Date < today || rd.Date > cutoff) continue;

                    items.Add(new CalendarItem
                    {
                        Kind         = "movie",
                        Date         = rd.ToString("yyyy-MM-dd"),
                        Title        = part.Title,
                        Subtitle     = col.Name,
                        Overview     = part.Overview,
                        PosterPath   = part.PosterPath ?? col.PosterPath,
                        TmdbParentId = colId,
                        TmdbItemId   = part.Id,
                        JellyfinId   = jfBoxSetId
                    });
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "Calendar: collection fetch failed for {Id}", colId);
            }
            finally { sem.Release(); }
        }).ConfigureAwait(false);

        var sorted = items
            .OrderBy(i => i.Date, StringComparer.Ordinal)
            .ThenBy(i => i.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new CalendarResponse
        {
            GeneratedAt        = DateTimeOffset.UtcNow,
            LookaheadDays      = lookaheadDays,
            SeriesScanned      = seriesList.Count,
            CollectionsScanned = collectionIds.Count,
            Items              = sorted
        };
    }
}
