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

public class MissingMediaDetector
{
    // With ConcurrentTmdbRequests=3 and 150ms delay per slot we get ~20 req/s,
    // well under TMDB's published limit of 40 req/10 s.
    private const    int      ConcurrentTmdbRequests = 3;
    private static readonly TimeSpan RateDelay = TimeSpan.FromMilliseconds(150);

    private readonly ILibraryManager _library;
    private readonly ILogger         _logger;

    public MissingMediaDetector(ILibraryManager library, ILogger logger)
    {
        _library = library;
        _logger  = logger;
    }

    // ── Entry point ───────────────────────────────────────────────────────────

    public async Task<ScanResults> DetectAsync(
        string   apiKey,
        bool     checkSeries,
        bool     checkCollections,
        bool     includeSpecials,
        bool     skipUnaired,
        bool     skipFutureMovies,
        IProgress<(double pct, string msg)> progress,
        CancellationToken ct)
    {
        using var tmdb = new TmdbService(apiKey, _logger);
        var results = new ScanResults { ScanTime = DateTimeOffset.UtcNow };

        bool   both = checkSeries && checkCollections;
        double half = both ? 50.0 : 100.0;

        if (checkSeries)
        {
            progress.Report((0, "Loading TV library…"));
            var (reports, total) = await ScanSeriesAsync(
                tmdb, includeSpecials, skipUnaired,
                pct => progress.Report((pct * half / 100, $"TV series: {pct:F0}%")), ct)
                .ConfigureAwait(false);

            results.TotalSeriesChecked   = total;
            results.MissingSeries        = reports.Where(r => r.MissingCount > 0)
                                                   .OrderByDescending(r => r.MissingCount)
                                                   .ToList();
            results.SeriesWithMissing    = results.MissingSeries.Count;
            results.TotalMissingEpisodes = results.MissingSeries.Sum(r => r.MissingCount);
        }

        if (checkCollections)
        {
            double baseOff = both ? 50.0 : 0.0;
            progress.Report((baseOff, "Loading movie library…"));
            var (reports, total) = await ScanCollectionsAsync(
                tmdb, skipFutureMovies,
                pct => progress.Report((baseOff + pct * half / 100, $"Collections: {pct:F0}%")), ct)
                .ConfigureAwait(false);

            results.TotalCollectionsChecked = total;
            results.MissingCollections      = reports.Where(r => r.MissingCount > 0)
                                                      .OrderByDescending(r => r.MissingCount)
                                                      .ToList();
            results.CollectionsWithMissing  = results.MissingCollections.Count;
            results.TotalMissingMovies      = results.MissingCollections.Sum(r => r.MissingCount);
        }

        progress.Report((100, "Done"));
        return results;
    }

    // ── TV series scan ────────────────────────────────────────────────────────

    private async Task<(List<SeriesMissingReport> reports, int totalChecked)> ScanSeriesAsync(
        TmdbService    tmdb,
        bool           includeSpecials,
        bool           skipUnaired,
        Action<double> report,
        CancellationToken ct)
    {
        var allSeries = _library.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.Series },
            Recursive        = true
        });

        _logger.LogInformation("MissingMediaChecker: {N} series in library", allSeries.Count);

        // Pre-load every PHYSICAL episode in one DB round-trip and group by SeriesId.
        // IsVirtualItem = false is critical: Jellyfin's metadata plugins create virtual
        // placeholder episodes for every missing entry they detect. Without this filter,
        // virtual placeholders are counted as owned, masking missing episodes entirely.
        var allEpisodes = _library.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.Episode },
            Recursive        = true,
            IsVirtualItem    = false
        });

        _logger.LogInformation("MissingMediaChecker: {N} episodes pre-loaded", allEpisodes.Count);

        var ownedBySeries = new Dictionary<Guid, HashSet<(int season, int ep)>>();
        foreach (var item in allEpisodes)
        {
            if (item is not Episode ep) continue;
            if (!ep.ParentIndexNumber.HasValue || !ep.IndexNumber.HasValue) continue;

            if (!ownedBySeries.TryGetValue(ep.SeriesId, out var set))
                ownedBySeries[ep.SeriesId] = set = new HashSet<(int, int)>();

            int s      = ep.ParentIndexNumber.Value;
            int eStart = ep.IndexNumber.Value;
            int eEnd   = ep.IndexNumberEnd ?? eStart;
            for (int e = eStart; e <= eEnd; e++)
                set.Add((s, e));
        }

        var today   = DateTime.UtcNow.Date;
        var reports = new List<SeriesMissingReport>();
        int checked_ = 0;

        using var tmdbSem = new SemaphoreSlim(ConcurrentTmdbRequests, ConcurrentTmdbRequests);

        for (int i = 0; i < allSeries.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            report((double)i / allSeries.Count * 100);

            var series = allSeries[i];
            if (!series.ProviderIds.TryGetValue("Tmdb", out var tmdbId) || string.IsNullOrEmpty(tmdbId))
                continue;

            // Fetch series header (season list + overall episode count).
            await tmdbSem.WaitAsync(ct).ConfigureAwait(false);
            TmdbSeriesDetails? tmdbSeries;
            try
            {
                await Task.Delay(RateDelay, ct).ConfigureAwait(false);
                tmdbSeries = await tmdb.GetSeriesAsync(tmdbId, ct).ConfigureAwait(false);
            }
            finally { tmdbSem.Release(); }

            if (tmdbSeries is null) continue;
            checked_++;

            var owned = ownedBySeries.TryGetValue(series.Id, out var s2)
                ? s2
                : new HashSet<(int, int)>();

            // Filter seasons we care about before spawning any tasks.
            var seasonsToScan = tmdbSeries.Seasons
                .Where(s => includeSpecials || s.SeasonNumber != 0)
                .ToList();

            // Fetch all seasons for this series in parallel (semaphore-limited).
            // A 5-season show goes from 5 × 150 ms = 750 ms → ceil(5/3) × 150 ms = 300 ms.
            var seasonTasks = seasonsToScan.Select(async seasonSummary =>
            {
                await tmdbSem.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    await Task.Delay(RateDelay, ct).ConfigureAwait(false);
                    return await tmdb.GetSeasonAsync(tmdbId, seasonSummary.SeasonNumber, ct)
                        .ConfigureAwait(false);
                }
                finally { tmdbSem.Release(); }
            });

            var seasonDetails = await Task.WhenAll(seasonTasks).ConfigureAwait(false);

            var missing     = new List<MissingEpisode>();
            int tmdbEpCount = 0;

            foreach (var seasonDetail in seasonDetails)
            {
                if (seasonDetail is null) continue;

                foreach (var ep in seasonDetail.Episodes)
                {
                    tmdbEpCount++;

                    // Bug fix: only skip when skipUnaired is true. When false,
                    // include all episodes regardless of whether they've aired.
                    if (skipUnaired && !HasAired(ep.AirDate, today)) continue;

                    if (!owned.Contains((ep.SeasonNumber, ep.EpisodeNumber)))
                    {
                        missing.Add(new MissingEpisode
                        {
                            SeasonNumber  = ep.SeasonNumber,
                            EpisodeNumber = ep.EpisodeNumber,
                            EpisodeName   = ep.Name,
                            AirDate       = ep.AirDate,
                            Overview      = ep.Overview,
                            TmdbId        = ep.Id
                        });
                    }
                }
            }

            missing.Sort((a, b) =>
                a.SeasonNumber != b.SeasonNumber
                    ? a.SeasonNumber.CompareTo(b.SeasonNumber)
                    : a.EpisodeNumber.CompareTo(b.EpisodeNumber));

            reports.Add(new SeriesMissingReport
            {
                SeriesName  = series.Name,
                JellyfinId  = series.Id,
                TmdbId      = tmdbId,
                // Bug fix: use the episode count from the seasons we actually scanned,
                // not tmdbSeries.NumberOfEpisodes which always includes specials.
                TotalEpisodesOnTmdb    = tmdbEpCount,
                TotalEpisodesInLibrary = owned.Count,
                MissingCount           = missing.Count,
                MissingEpisodes        = missing
            });

            _logger.LogDebug("Series {Name}: {N} missing episodes", series.Name, missing.Count);
        }

        return (reports, checked_);
    }

    // ── Collection / movie scan ───────────────────────────────────────────────

    private async Task<(List<CollectionMissingReport> reports, int totalChecked)> ScanCollectionsAsync(
        TmdbService    tmdb,
        bool           skipFutureMovies,
        Action<double> report,
        CancellationToken ct)
    {
        var allMovies = _library.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.Movie },
            Recursive        = true,
            IsVirtualItem    = false
        });

        _logger.LogInformation("MissingMediaChecker: {N} movies in library", allMovies.Count);

        var today = DateTime.UtcNow.Date;

        using var tmdbSem = new SemaphoreSlim(ConcurrentTmdbRequests, ConcurrentTmdbRequests);

        // collectionId → bag of owned TMDB movie IDs (ConcurrentBag is append-safe across threads)
        var collectionOwned = new ConcurrentDictionary<int, ConcurrentBag<int>>();

        // ── Phase 1 (0–70%): fetch each movie's TMDB details in parallel ─────
        int done1  = 0;
        int total1 = allMovies.Count;

        var movieTasks = allMovies.Select(async movie =>
        {
            if (!movie.ProviderIds.TryGetValue("Tmdb", out var tmdbId) || string.IsNullOrEmpty(tmdbId))
                return;
            if (!int.TryParse(tmdbId, out var tmdbIntId)) return;

            await tmdbSem.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await Task.Delay(RateDelay, ct).ConfigureAwait(false);
                var details = await tmdb.GetMovieAsync(tmdbId, ct).ConfigureAwait(false);
                if (details?.BelongsToCollection is null) return;

                int colId = details.BelongsToCollection.Id;
                collectionOwned.GetOrAdd(colId, _ => new ConcurrentBag<int>()).Add(tmdbIntId);
            }
            finally
            {
                tmdbSem.Release();
                // Thread-safe progress tick (Interlocked avoids a lock)
                int d = Interlocked.Increment(ref done1);
                report((double)d / Math.Max(total1, 1) * 70);
            }
        });

        await Task.WhenAll(movieTasks).ConfigureAwait(false);

        // ── Phase 2 (70–100%): fetch full details for each discovered collection ──
        int done2  = 0;
        int total2 = collectionOwned.Count;

        var collectionDetails = new ConcurrentDictionary<int, TmdbCollectionDetails>();

        var colTasks = collectionOwned.Keys.Select(async colId =>
        {
            await tmdbSem.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await Task.Delay(RateDelay, ct).ConfigureAwait(false);
                var col = await tmdb.GetCollectionAsync(colId, ct).ConfigureAwait(false);
                if (col is not null) collectionDetails[colId] = col;
            }
            finally
            {
                tmdbSem.Release();
                int d = Interlocked.Increment(ref done2);
                report(70 + (double)d / Math.Max(total2, 1) * 30);
            }
        });

        await Task.WhenAll(colTasks).ConfigureAwait(false);

        // ── Build reports ─────────────────────────────────────────────────────
        var reports = new List<CollectionMissingReport>();

        foreach (var (colId, col) in collectionDetails)
        {
            var owned = collectionOwned.TryGetValue(colId, out var bag)
                ? new HashSet<int>(bag)
                : new HashSet<int>();

            var missing = new List<MissingMovie>();
            foreach (var part in col.Parts)
            {
                if (owned.Contains(part.Id)) continue;
                if (skipFutureMovies && !HasAired(part.ReleaseDate, today)) continue;

                int? year = null;
                if (!string.IsNullOrEmpty(part.ReleaseDate) &&
                    DateTime.TryParse(part.ReleaseDate, out var rd))
                    year = rd.Year;

                missing.Add(new MissingMovie
                {
                    Title       = part.Title,
                    ReleaseDate = part.ReleaseDate,
                    Year        = year,
                    Overview    = part.Overview,
                    PosterPath  = part.PosterPath,
                    TmdbId      = part.Id,
                    VoteAverage = part.VoteAverage
                });
            }

            missing.Sort((a, b) =>
                string.Compare(a.ReleaseDate, b.ReleaseDate, StringComparison.Ordinal));

            reports.Add(new CollectionMissingReport
            {
                CollectionName          = col.Name,
                TmdbCollectionId        = col.Id,
                TotalMoviesInCollection = col.Parts.Count,
                MoviesInLibrary         = owned.Count,
                MissingCount            = missing.Count,
                MissingMovies           = missing
            });
        }

        return (reports, collectionDetails.Count);
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    // Returns true only when dateStr is present AND the date is on or before today.
    // Episodes/movies with no date are treated as unaired.
    private static bool HasAired(string? dateStr, DateTime today)
    {
        if (string.IsNullOrEmpty(dateStr)) return false;
        return DateTime.TryParse(dateStr, out var d) && d.Date <= today;
    }
}
