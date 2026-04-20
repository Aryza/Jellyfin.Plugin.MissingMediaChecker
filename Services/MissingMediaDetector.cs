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
            var (reports, total, inLib, skipped) = await ScanSeriesAsync(
                tmdb, includeSpecials, skipUnaired,
                pct => progress.Report((pct * half / 100, $"TV series: {pct:F0}%")), ct)
                .ConfigureAwait(false);

            results.TotalSeriesInLibrary = inLib;
            results.TotalSeriesChecked   = total;
            results.SeriesSkippedNoId    = skipped;
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

    private async Task<(List<SeriesMissingReport> reports, int totalChecked, int totalInLibrary, int skippedNoId)> ScanSeriesAsync(
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

        var today      = DateTime.UtcNow.Date;
        var reportsBag = new ConcurrentBag<SeriesMissingReport>();
        int checked_   = 0;
        int skipped_   = 0;
        int done_      = 0;

        using var tmdbSem = new SemaphoreSlim(ConcurrentTmdbRequests, ConcurrentTmdbRequests);

        // Process all series concurrently. The semaphore throttles TMDB calls globally
        // so series overlap their I/O waits instead of queuing behind each other.
        var seriesTasks = allSeries.Select(async series =>
        {
            try
            {
            ct.ThrowIfCancellationRequested();

            string? tmdbId;
            if (!series.ProviderIds.TryGetValue("Tmdb", out tmdbId) || string.IsNullOrEmpty(tmdbId))
            {
                tmdbId = await ResolveTmdbIdAsync(tmdb, series.ProviderIds, tmdbSem, ct)
                    .ConfigureAwait(false);
                if (string.IsNullOrEmpty(tmdbId))
                {
                    _logger.LogDebug("Series {Name}: no resolvable TMDB ID, skipping", series.Name);
                    Interlocked.Increment(ref skipped_);
                    report((double)Interlocked.Increment(ref done_) / allSeries.Count * 100);
                    return;
                }
                _logger.LogDebug("Series {Name}: resolved TMDB ID {Id} via /find", series.Name, tmdbId);
            }

            await tmdbSem.WaitAsync(ct).ConfigureAwait(false);
            TmdbSeriesDetails? tmdbSeries;
            try
            {
                await Task.Delay(RateDelay, ct).ConfigureAwait(false);
                tmdbSeries = await tmdb.GetSeriesAsync(tmdbId, ct).ConfigureAwait(false);
            }
            finally { tmdbSem.Release(); }

            report((double)Interlocked.Increment(ref done_) / allSeries.Count * 100);

            if (tmdbSeries is null) return;
            Interlocked.Increment(ref checked_);

            // Query only this series' physical episodes via AncestorIds — more reliable
            // than grouping a global episode list by ep.SeriesId, which can mismatch.
            var ownedItems = _library.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { BaseItemKind.Episode },
                AncestorIds      = new[] { series.Id },
                IsVirtualItem    = false,
                Recursive        = true
            });

            var owned = new HashSet<(int season, int ep)>();
            foreach (var item in ownedItems)
            {
                if (item is not Episode ep) continue;
                if (!ep.ParentIndexNumber.HasValue || !ep.IndexNumber.HasValue) continue;
                int sn     = ep.ParentIndexNumber.Value;
                int eStart = ep.IndexNumber.Value;
                int eEnd   = ep.IndexNumberEnd ?? eStart;
                for (int e = eStart; e <= eEnd; e++)
                    owned.Add((sn, e));
            }

            _logger.LogDebug("Series {Name}: {Owned} physical episodes in library", series.Name, owned.Count);

            var seasonsToScan = tmdbSeries.Seasons
                .Where(s => includeSpecials || s.SeasonNumber != 0)
                .ToList();

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

            reportsBag.Add(new SeriesMissingReport
            {
                SeriesName             = series.Name,
                JellyfinId             = series.Id,
                TmdbId                 = tmdbId,
                TotalEpisodesOnTmdb    = tmdbEpCount,
                TotalEpisodesInLibrary = owned.Count,
                MissingCount           = missing.Count,
                MissingEpisodes        = missing
            });

            _logger.LogDebug("Series {Name}: {N} missing episodes", series.Name, missing.Count);
            } // end try
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Series {Name}: unexpected error, skipping", series.Name);
                Interlocked.Increment(ref done_);
            }
        });

        await Task.WhenAll(seriesTasks).ConfigureAwait(false);

        _logger.LogInformation(
            "MissingMediaChecker: series scan done — library={Lib}, checked={Checked}, skipped(noId)={Skipped}",
            allSeries.Count, checked_, skipped_);

        return (reportsBag.ToList(), checked_, allSeries.Count, skipped_);
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

        // ── Match each report to a Jellyfin box-set (best-effort, name match) ──
        // Jellyfin's TMDB plugin auto-creates BoxSet items whose names match the
        // TMDB collection name, so an exact case-insensitive lookup works in
        // most libraries.
        var boxSets = _library.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.BoxSet },
            Recursive        = true
        });

        var boxSetByName = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        foreach (var bs in boxSets)
            boxSetByName.TryAdd(bs.Name, bs.Id);

        foreach (var r in reports)
        {
            if (boxSetByName.TryGetValue(r.CollectionName, out var bsId))
                r.JellyfinCollectionId = bsId;
        }

        _logger.LogInformation(
            "MissingMediaChecker: matched {N}/{T} TMDB collections to Jellyfin box-sets",
            reports.Count(r => r.JellyfinCollectionId.HasValue), reports.Count);

        return (reports, collectionDetails.Count);
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    // Tries to resolve a TMDB series ID from TVDB or IMDB provider IDs via the /find endpoint.
    // Returns null if neither source yields a result.
    private static async Task<string?> ResolveTmdbIdAsync(
        TmdbService                     tmdb,
        IDictionary<string, string>     providerIds,
        SemaphoreSlim                   sem,
        CancellationToken               ct)
    {
        // Try TVDB first (most common for TV), then IMDB.
        var candidates = new (string key, string source)[]
        {
            ("Tvdb", "tvdb_id"),
            ("Imdb", "imdb_id"),
        };

        foreach (var (key, source) in candidates)
        {
            if (!providerIds.TryGetValue(key, out var extId) || string.IsNullOrEmpty(extId))
                continue;

            await sem.WaitAsync(ct).ConfigureAwait(false);
            TmdbFindResult? found;
            try
            {
                await Task.Delay(RateDelay, ct).ConfigureAwait(false);
                found = await tmdb.FindByExternalIdAsync(extId, source, ct).ConfigureAwait(false);
            }
            finally { sem.Release(); }

            if (found?.TvResults is { Count: > 0 } hits)
                return hits[0].Id.ToString();
        }

        return null;
    }

    // Returns true only when dateStr is present AND the date is on or before today.
    // Episodes/movies with no date are treated as unaired.
    private static bool HasAired(string? dateStr, DateTime today)
    {
        if (string.IsNullOrEmpty(dateStr)) return false;
        return DateTime.TryParse(dateStr, out var d) && d.Date <= today;
    }
}
