using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.MissingMediaChecker.Models;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MissingMediaChecker.Services;

public class MissingMediaDetector
{
    // TMDB removed their published rate limit in late 2023. We cap concurrency
    // mainly to leave threadpool/SQLite headroom for the UI during a scan.
    // 16 was too aggressive — Jellyfin web became unresponsive during scans
    // because every parallel worker also fired DB queries.
    private const int ConcurrentTmdbRequests = 8;

    private static readonly List<Episode> EmptyEpisodes = new(0);

    private readonly ILibraryManager _library;
    private readonly ILogger         _logger;

    public MissingMediaDetector(ILibraryManager library, ILogger logger)
    {
        _library = library;
        _logger  = logger;
    }

    // ── Diff-scan context ─────────────────────────────────────────────────────
    // Bundles the previous-scan lookup tables so we only build them once per
    // DetectAsync call and pass them by reference to inner helpers.
    private sealed class DiffContext
    {
        public HashSet<(string series, int season, int episode)> PrevEpisodes = new();
        public HashSet<(int collection, int movie)>              PrevMovies   = new();
        public bool HasPrevious;
    }

    // ── Entry point ───────────────────────────────────────────────────────────

    public async Task<ScanResults> DetectAsync(
        string           apiKey,
        bool             checkSeries,
        bool             checkCollections,
        bool             includeSpecials,
        bool             skipUnaired,
        bool             skipFutureMovies,
        bool             recentlyAiredOnly,
        int              recentlyAiredDays,
        bool             enableIncrementalScan,
        int              incrementalMinAgeHours,
        ScanResults?     previousResults,
        IgnoreList       ignoreList,
        IncrementalCache incrementalCache,
        IProgress<(double pct, string msg)> progress,
        CancellationToken ct)
    {
        using var tmdb = new TmdbService(apiKey, _logger);
        var results = new ScanResults { ScanTime = DateTimeOffset.UtcNow };

        var diff = BuildDiffContext(previousResults);

        DateTime? recentCutoff = recentlyAiredOnly
            ? DateTime.UtcNow.Date.AddDays(-Math.Max(0, recentlyAiredDays))
            : null;

        bool   both = checkSeries && checkCollections;
        double half = both ? 50.0 : 100.0;

        if (checkSeries)
        {
            progress.Report((0, "Loading TV library…"));
            var (reports, total, inLib, skippedList, cacheHits) = await ScanSeriesAsync(
                tmdb, includeSpecials, skipUnaired, recentCutoff,
                enableIncrementalScan, incrementalMinAgeHours,
                diff, ignoreList, incrementalCache,
                pct => progress.Report((pct * half / 100, $"TV series: {pct:F0}%")), ct)
                .ConfigureAwait(false);

            results.TotalSeriesInLibrary = inLib;
            results.TotalSeriesChecked   = total;
            results.SeriesSkippedNoId    = skippedList.Count;
            results.SkippedSeries        = skippedList;
            results.IncrementalCacheHits = cacheHits;
            results.MissingSeries        = reports.Where(r => r.MissingCount > 0)
                                                   .OrderByDescending(r => r.MissingCount)
                                                   .ToList();
            results.SeriesWithMissing    = results.MissingSeries.Count;
            results.TotalMissingEpisodes = results.MissingSeries.Sum(r => r.MissingCount);
            results.NewMissingEpisodes   = results.MissingSeries.Sum(r => r.NewMissingCount);
        }

        if (checkCollections)
        {
            double baseOff = both ? 50.0 : 0.0;
            progress.Report((baseOff, "Loading movie library…"));
            var (reports, total) = await ScanCollectionsAsync(
                tmdb, skipFutureMovies, recentCutoff, diff, ignoreList,
                pct => progress.Report((baseOff + pct * half / 100, $"Collections: {pct:F0}%")), ct)
                .ConfigureAwait(false);

            results.TotalCollectionsChecked = total;
            results.MissingCollections      = reports.Where(r => r.MissingCount > 0)
                                                      .OrderByDescending(r => r.MissingCount)
                                                      .ToList();
            results.CollectionsWithMissing  = results.MissingCollections.Count;
            results.TotalMissingMovies      = results.MissingCollections.Sum(r => r.MissingCount);
            results.NewMissingMovies        = results.MissingCollections.Sum(r => r.NewMissingCount);
        }

        progress.Report((100, "Done"));
        return results;
    }

    // ── Diff helper ───────────────────────────────────────────────────────────

    private static DiffContext BuildDiffContext(ScanResults? prev)
    {
        var ctx = new DiffContext { HasPrevious = prev is not null };
        if (prev is null) return ctx;

        foreach (var s in prev.MissingSeries)
        {
            if (string.IsNullOrEmpty(s.TmdbId)) continue;
            foreach (var ep in s.MissingEpisodes)
                ctx.PrevEpisodes.Add((s.TmdbId, ep.SeasonNumber, ep.EpisodeNumber));
        }
        foreach (var c in prev.MissingCollections)
        {
            foreach (var m in c.MissingMovies)
                ctx.PrevMovies.Add((c.TmdbCollectionId, m.TmdbId));
        }
        return ctx;
    }

    // ── TV series scan ────────────────────────────────────────────────────────

    private async Task<(List<SeriesMissingReport> reports, int totalChecked, int totalInLibrary, List<SkippedSeries> skipped, int cacheHits)> ScanSeriesAsync(
        TmdbService      tmdb,
        bool             includeSpecials,
        bool             skipUnaired,
        DateTime?        recentCutoff,
        bool             enableIncrementalScan,
        int              incrementalMinAgeHours,
        DiffContext      diff,
        IgnoreList       ignoreList,
        IncrementalCache incrementalCache,
        Action<double>   report,
        CancellationToken ct)
    {
        var allSeries = _library.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.Series },
            Recursive        = true
        });

        _logger.LogInformation("MissingMediaChecker: {N} series in library", allSeries.Count);

        // Bulk-load every episode once and group by SeriesId. Replaces N
        // per-series AncestorIds queries that previously fired in parallel —
        // those were the dominant cause of UI starvation during a scan.
        var allEpisodeItems = _library.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.Episode },
            Recursive        = true,
            IsVirtualItem    = false
        });

        var episodesBySeries = new Dictionary<Guid, List<Episode>>(allSeries.Count);
        foreach (var item in allEpisodeItems)
        {
            if (item is not Episode ep) continue;
            var sid = ep.SeriesId;
            if (sid == Guid.Empty) continue;
            if (!episodesBySeries.TryGetValue(sid, out var list))
                episodesBySeries[sid] = list = new List<Episode>();
            list.Add(ep);
        }
        _logger.LogInformation(
            "MissingMediaChecker: indexed {E} episodes across {S} series in one query",
            allEpisodeItems.Count, episodesBySeries.Count);

        var today      = DateTime.UtcNow.Date;
        var reportsBag = new ConcurrentBag<SeriesMissingReport>();
        var skippedBag = new ConcurrentBag<SkippedSeries>();
        int checked_   = 0;
        int done_      = 0;
        int cacheHits_ = 0;

        using var tmdbSem = new SemaphoreSlim(ConcurrentTmdbRequests, ConcurrentTmdbRequests);

        var parallelOpts = new ParallelOptions
        {
            MaxDegreeOfParallelism = ConcurrentTmdbRequests,
            CancellationToken      = ct
        };

        var minAge = TimeSpan.FromHours(Math.Max(1, incrementalMinAgeHours));

        await Parallel.ForEachAsync(allSeries, parallelOpts, async (series, innerCt) =>
        {
            try
            {
                innerCt.ThrowIfCancellationRequested();

                // ── 1. Resolve TMDB ID ────────────────────────────────────────
                string? tmdbId;
                if (!series.ProviderIds.TryGetValue("Tmdb", out tmdbId) || string.IsNullOrEmpty(tmdbId))
                {
                    tmdbId = await ResolveTmdbIdAsync(tmdb, series.ProviderIds, tmdbSem, innerCt)
                        .ConfigureAwait(false);
                    if (string.IsNullOrEmpty(tmdbId))
                    {
                        _logger.LogDebug("Series {Name}: no resolvable TMDB ID, skipping", series.Name);
                        var availIds = string.Join(", ", series.ProviderIds
                            .Where(kv => !string.IsNullOrEmpty(kv.Value))
                            .Select(kv => $"{kv.Key}:{kv.Value}"));
                        skippedBag.Add(new SkippedSeries
                        {
                            SeriesName   = series.Name,
                            AvailableIds = string.IsNullOrEmpty(availIds) ? "none" : availIds
                        });
                        report((double)Interlocked.Increment(ref done_) / allSeries.Count * 100);
                        return;
                    }
                }

                // ── 2. Ignore-list fast skip ─────────────────────────────────
                if (ignoreList.IsSeriesIgnored(tmdbId))
                {
                    report((double)Interlocked.Increment(ref done_) / allSeries.Count * 100);
                    return;
                }

                // ── 3. Owned episodes (resolved from the bulk index above) ──
                var ownedEpisodes = episodesBySeries.TryGetValue(series.Id, out var eps)
                    ? eps : EmptyEpisodes;

                // ── 4. Incremental cache: if fingerprint matches an "Ended"
                // series within the min-age window, reuse the cached report
                // wholesale. Avoids the TMDB round-trip entirely.
                string cacheKey = series.Id.ToString();
                IncrementalCacheEntry? cached = null;
                if (enableIncrementalScan &&
                    incrementalCache.Entries.TryGetValue(cacheKey, out cached) &&
                    cached?.Report is not null &&
                    IsEndedStatus(cached.TmdbStatus) &&
                    (DateTime.UtcNow - cached.ScannedAtUtc) < minAge)
                {
                    if (ownedEpisodes.Count == cached.OwnedEpisodeCount)
                    {
                        // Cache hit: reuse, but clear IsNew flags (by definition
                        // nothing is new relative to the previous scan because
                        // we didn't observe any change).
                        var reused = CloneReport(cached.Report);
                        reused.NewMissingCount = 0;
                        foreach (var ep in reused.MissingEpisodes) ep.IsNew = false;
                        // Re-apply ignore list post-hoc (list may have changed
                        // since the cached scan).
                        ApplyEpisodeIgnores(reused, ignoreList);
                        reportsBag.Add(reused);
                        Interlocked.Increment(ref checked_);
                        Interlocked.Increment(ref cacheHits_);
                        report((double)Interlocked.Increment(ref done_) / allSeries.Count * 100);
                        return;
                    }
                }

                // ── 5. Full scan: one combined /tv/{id} + append_to_response call
                await tmdbSem.WaitAsync(innerCt).ConfigureAwait(false);
                TmdbSeriesDetails? tmdbSeries;
                Dictionary<int, TmdbSeasonDetails> seasonDetailsByNumber;
                try
                {
                    var bundle = await tmdb.GetSeriesWithSeasonsAsync(tmdbId, includeSpecials, innerCt)
                        .ConfigureAwait(false);
                    tmdbSeries            = bundle.details;
                    seasonDetailsByNumber = bundle.seasons;
                }
                finally { tmdbSem.Release(); }

                report((double)Interlocked.Increment(ref done_) / allSeries.Count * 100);

                if (tmdbSeries is null) return;
                Interlocked.Increment(ref checked_);

                var owned = new HashSet<(int season, int ep)>(ownedEpisodes.Count);
                foreach (var ep in ownedEpisodes)
                {
                    if (!ep.ParentIndexNumber.HasValue || !ep.IndexNumber.HasValue) continue;
                    int sn     = ep.ParentIndexNumber.Value;
                    int eStart = ep.IndexNumber.Value;
                    int eEnd   = ep.IndexNumberEnd ?? eStart;
                    for (int e = eStart; e <= eEnd; e++)
                        owned.Add((sn, e));
                }

                var missing        = new List<MissingEpisode>();
                var missingSeasons = new List<int>();
                int tmdbEpCount    = 0;
                int newMissingCnt  = 0;

                foreach (var seasonSummary in tmdbSeries.Seasons)
                {
                    if (!includeSpecials && seasonSummary.SeasonNumber == 0) continue;
                    if (!seasonDetailsByNumber.TryGetValue(seasonSummary.SeasonNumber, out var sd))
                        continue;

                    // Ignore-list: whole-season suppression
                    if (ignoreList.IsSeasonIgnored(tmdbId, sd.SeasonNumber))
                        continue;

                    int eligibleCount   = 0;
                    int missingInSeason = 0;

                    foreach (var ep in sd.Episodes)
                    {
                        tmdbEpCount++;
                        if (skipUnaired && !HasAired(ep.AirDate, today)) continue;

                        // Recently-aired-only filter
                        if (recentCutoff.HasValue && !AirDateWithin(ep.AirDate, recentCutoff.Value, today))
                            continue;

                        // Ignore-list: single episode
                        if (ignoreList.IsEpisodeIgnored(tmdbId, ep.SeasonNumber, ep.EpisodeNumber, ep.Id))
                            continue;

                        eligibleCount++;
                        if (!owned.Contains((ep.SeasonNumber, ep.EpisodeNumber)))
                        {
                            bool isNew = diff.HasPrevious &&
                                !diff.PrevEpisodes.Contains((tmdbId, ep.SeasonNumber, ep.EpisodeNumber));
                            if (isNew) newMissingCnt++;

                            missing.Add(new MissingEpisode
                            {
                                SeasonNumber  = ep.SeasonNumber,
                                EpisodeNumber = ep.EpisodeNumber,
                                EpisodeName   = ep.Name,
                                AirDate       = ep.AirDate,
                                Overview      = ep.Overview,
                                TmdbId        = ep.Id,
                                IsNew         = isNew
                            });
                            missingInSeason++;
                        }
                    }

                    if (eligibleCount > 0 && eligibleCount == missingInSeason)
                        missingSeasons.Add(sd.SeasonNumber);
                }

                missing.Sort(static (a, b) =>
                    a.SeasonNumber != b.SeasonNumber
                        ? a.SeasonNumber.CompareTo(b.SeasonNumber)
                        : a.EpisodeNumber.CompareTo(b.EpisodeNumber));

                var reportObj = new SeriesMissingReport
                {
                    SeriesName             = series.Name,
                    JellyfinId             = series.Id,
                    TmdbId                 = tmdbId,
                    PosterPath             = tmdbSeries.PosterPath,
                    TotalEpisodesOnTmdb    = tmdbEpCount,
                    TotalEpisodesInLibrary = owned.Count,
                    MissingCount           = missing.Count,
                    MissingSeasons         = missingSeasons,
                    MissingEpisodes        = missing,
                    NewMissingCount        = newMissingCnt
                };

                reportsBag.Add(reportObj);

                // Update incremental cache for next time
                incrementalCache.Entries[cacheKey] = new IncrementalCacheEntry
                {
                    TmdbStatus        = tmdbSeries.Status,
                    OwnedEpisodeCount = ownedEpisodes.Count,
                    ScannedAtUtc      = DateTime.UtcNow,
                    Report            = reportObj
                };

                _logger.LogDebug("Series {Name}: {N} missing episodes ({New} new)",
                    series.Name, missing.Count, newMissingCnt);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Series {Name}: unexpected error, skipping", series.Name);
                Interlocked.Increment(ref done_);
            }
        }).ConfigureAwait(false);

        var skippedList = skippedBag.ToList();
        _logger.LogInformation(
            "MissingMediaChecker: series scan done — library={Lib}, checked={Checked}, skipped(noId)={Skipped}, cacheHits={Hits}",
            allSeries.Count, checked_, skippedList.Count, cacheHits_);

        return (reportsBag.ToList(), checked_, allSeries.Count, skippedList, cacheHits_);
    }

    private static bool IsEndedStatus(string? s)
    {
        if (string.IsNullOrEmpty(s)) return false;
        return s.Equals("Ended",    StringComparison.OrdinalIgnoreCase)
            || s.Equals("Canceled", StringComparison.OrdinalIgnoreCase)
            || s.Equals("Cancelled", StringComparison.OrdinalIgnoreCase);
    }

    private static SeriesMissingReport CloneReport(SeriesMissingReport src)
    {
        var eps = new List<MissingEpisode>(src.MissingEpisodes.Count);
        foreach (var e in src.MissingEpisodes)
        {
            eps.Add(new MissingEpisode
            {
                SeasonNumber  = e.SeasonNumber,
                EpisodeNumber = e.EpisodeNumber,
                EpisodeName   = e.EpisodeName,
                AirDate       = e.AirDate,
                Overview      = e.Overview,
                TmdbId        = e.TmdbId,
                IsNew         = e.IsNew
            });
        }
        return new SeriesMissingReport
        {
            SeriesName             = src.SeriesName,
            JellyfinId             = src.JellyfinId,
            TmdbId                 = src.TmdbId,
            PosterPath             = src.PosterPath,
            TotalEpisodesOnTmdb    = src.TotalEpisodesOnTmdb,
            TotalEpisodesInLibrary = src.TotalEpisodesInLibrary,
            MissingCount           = src.MissingCount,
            MissingSeasons         = new List<int>(src.MissingSeasons),
            MissingEpisodes        = eps,
            NewMissingCount        = src.NewMissingCount
        };
    }

    // Post-hoc prune: drop ignored episodes/seasons from a cached report so
    // edits to the ignore list take effect immediately even on cache hits.
    private static void ApplyEpisodeIgnores(SeriesMissingReport r, IgnoreList ignoreList)
    {
        if (r.MissingEpisodes.Count == 0 ||
            (ignoreList.Seasons.Count == 0 && ignoreList.Episodes.Count == 0))
            return;

        r.MissingEpisodes = r.MissingEpisodes
            .Where(ep => !ignoreList.IsSeasonIgnored(r.TmdbId, ep.SeasonNumber)
                      && !ignoreList.IsEpisodeIgnored(r.TmdbId, ep.SeasonNumber, ep.EpisodeNumber, ep.TmdbId))
            .ToList();
        r.MissingCount   = r.MissingEpisodes.Count;
        r.MissingSeasons = r.MissingSeasons
            .Where(s => !ignoreList.IsSeasonIgnored(r.TmdbId, s))
            .ToList();
    }

    // ── Collection / movie scan ───────────────────────────────────────────────

    private async Task<(List<CollectionMissingReport> reports, int totalChecked)> ScanCollectionsAsync(
        TmdbService    tmdb,
        bool           skipFutureMovies,
        DateTime?      recentCutoff,
        DiffContext    diff,
        IgnoreList     ignoreList,
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

        var collectionOwned = new ConcurrentDictionary<int, ConcurrentBag<int>>();

        var parallelOpts = new ParallelOptions
        {
            MaxDegreeOfParallelism = ConcurrentTmdbRequests,
            CancellationToken      = ct
        };

        var movieIdToCollection = BuildMovieToBoxSetCollectionMap();
        if (movieIdToCollection.Count > 0)
        {
            _logger.LogInformation(
                "MissingMediaChecker: resolved {N} movies to TMDB collections via Jellyfin BoxSets",
                movieIdToCollection.Count);
        }

        // Phase 1a (sync): movies already mapped via Jellyfin BoxSets — no TMDB call.
        // Phase 1b (parallel HTTP): only stand-alone movies need a /movie/{id}
        // round-trip to discover their BelongsToCollection. Pre-filtering here
        // shrinks the parallel workload to the bare minimum.
        var unmapped = new List<BaseItem>();
        foreach (var movie in allMovies)
        {
            if (!movie.ProviderIds.TryGetValue("Tmdb", out var tmdbId) || string.IsNullOrEmpty(tmdbId))
                continue;
            if (!int.TryParse(tmdbId, out var tmdbIntId)) continue;

            if (movieIdToCollection.TryGetValue(movie.Id, out var knownColId))
            {
                collectionOwned.GetOrAdd(knownColId, static _ => new ConcurrentBag<int>()).Add(tmdbIntId);
                continue;
            }
            unmapped.Add(movie);
        }

        int done1  = 0;
        int total1 = unmapped.Count;

        await Parallel.ForEachAsync(unmapped, parallelOpts, async (movie, innerCt) =>
        {
            try
            {
                var tmdbId = movie.ProviderIds["Tmdb"];
                int.TryParse(tmdbId, out var tmdbIntId);

                await tmdbSem.WaitAsync(innerCt).ConfigureAwait(false);
                try
                {
                    var details = await tmdb.GetMovieAsync(tmdbId, innerCt).ConfigureAwait(false);
                    if (details?.BelongsToCollection is null) return;

                    int colId = details.BelongsToCollection.Id;
                    collectionOwned.GetOrAdd(colId, static _ => new ConcurrentBag<int>()).Add(tmdbIntId);
                }
                finally { tmdbSem.Release(); }
            }
            finally
            {
                int d = Interlocked.Increment(ref done1);
                report(total1 == 0 ? 70 : (double)d / total1 * 70);
            }
        }).ConfigureAwait(false);

        // Phase 2 (70–100%): fetch full details for each discovered collection.
        // Skip ignored collections entirely.
        int done2  = 0;
        int total2 = collectionOwned.Count;

        var collectionDetails = new ConcurrentDictionary<int, TmdbCollectionDetails>();

        await Parallel.ForEachAsync(collectionOwned.Keys, parallelOpts, async (colId, innerCt) =>
        {
            try
            {
                if (ignoreList.IsCollectionIgnored(colId)) return;

                await tmdbSem.WaitAsync(innerCt).ConfigureAwait(false);
                try
                {
                    var col = await tmdb.GetCollectionAsync(colId, innerCt).ConfigureAwait(false);
                    if (col is not null) collectionDetails[colId] = col;
                }
                finally { tmdbSem.Release(); }
            }
            finally
            {
                int d = Interlocked.Increment(ref done2);
                report(70 + (double)d / Math.Max(total2, 1) * 30);
            }
        }).ConfigureAwait(false);

        // Build reports
        var reports = new List<CollectionMissingReport>(collectionDetails.Count);

        foreach (var (colId, col) in collectionDetails)
        {
            var owned = collectionOwned.TryGetValue(colId, out var bag)
                ? new HashSet<int>(bag)
                : new HashSet<int>();

            var missing       = new List<MissingMovie>();
            int newMissingCnt = 0;
            foreach (var part in col.Parts)
            {
                if (owned.Contains(part.Id)) continue;
                if (skipFutureMovies && !HasAired(part.ReleaseDate, today)) continue;
                if (recentCutoff.HasValue && !AirDateWithin(part.ReleaseDate, recentCutoff.Value, today)) continue;
                if (ignoreList.IsMovieIgnored(part.Id)) continue;

                int? year = null;
                if (!string.IsNullOrEmpty(part.ReleaseDate) &&
                    DateTime.TryParse(part.ReleaseDate, out var rd))
                    year = rd.Year;

                bool isNew = diff.HasPrevious && !diff.PrevMovies.Contains((colId, part.Id));
                if (isNew) newMissingCnt++;

                missing.Add(new MissingMovie
                {
                    Title       = part.Title,
                    ReleaseDate = part.ReleaseDate,
                    Year        = year,
                    Overview    = part.Overview,
                    PosterPath  = part.PosterPath,
                    TmdbId      = part.Id,
                    VoteAverage = part.VoteAverage,
                    IsNew       = isNew
                });
            }

            missing.Sort((a, b) =>
                string.Compare(a.ReleaseDate, b.ReleaseDate, StringComparison.Ordinal));

            reports.Add(new CollectionMissingReport
            {
                CollectionName          = col.Name,
                TmdbCollectionId        = col.Id,
                PosterPath              = col.PosterPath,
                TotalMoviesInCollection = col.Parts.Count,
                MoviesInLibrary         = owned.Count,
                MissingCount            = missing.Count,
                MissingMovies           = missing,
                NewMissingCount         = newMissingCnt
            });
        }

        // Match each report to a Jellyfin box-set by TMDB ID, then fall back to name.
        var boxSets = _library.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.BoxSet },
            Recursive        = true
        });

        var boxSetByTmdbId = new Dictionary<int, Guid>(boxSets.Count);
        var boxSetByName   = new Dictionary<string, Guid>(boxSets.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var bs in boxSets)
        {
            if (bs.ProviderIds.TryGetValue("Tmdb", out var idStr) &&
                !string.IsNullOrEmpty(idStr) &&
                int.TryParse(idStr, out var tmdbId))
            {
                boxSetByTmdbId.TryAdd(tmdbId, bs.Id);
            }
            boxSetByName.TryAdd(bs.Name, bs.Id);
        }

        int matchedById = 0, matchedByName = 0;
        foreach (var r in reports)
        {
            if (boxSetByTmdbId.TryGetValue(r.TmdbCollectionId, out var idById))
            {
                r.JellyfinCollectionId = idById;
                matchedById++;
            }
            else if (boxSetByName.TryGetValue(r.CollectionName, out var idByName))
            {
                r.JellyfinCollectionId = idByName;
                matchedByName++;
            }
        }
        _logger.LogDebug(
            "MissingMediaChecker: BoxSet matches — by TMDB ID: {ById}, by name fallback: {ByName}",
            matchedById, matchedByName);

        _logger.LogInformation(
            "MissingMediaChecker: matched {N}/{T} TMDB collections to Jellyfin box-sets",
            reports.Count(r => r.JellyfinCollectionId.HasValue), reports.Count);

        return (reports, collectionDetails.Count);
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private Dictionary<Guid, int> BuildMovieToBoxSetCollectionMap()
    {
        var map = new Dictionary<Guid, int>();

        var boxSets = _library.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.BoxSet },
            Recursive        = true
        });

        foreach (var bs in boxSets)
        {
            if (!bs.ProviderIds.TryGetValue("Tmdb", out var idStr) ||
                string.IsNullOrEmpty(idStr) ||
                !int.TryParse(idStr, out var tmdbColId))
                continue;

            if (bs is not Folder folder) continue;

            foreach (var child in folder.GetLinkedChildren())
            {
                if (child is Movie)
                    map[child.Id] = tmdbColId;
            }
        }

        return map;
    }

    private static async Task<string?> ResolveTmdbIdAsync(
        TmdbService                     tmdb,
        IDictionary<string, string>     providerIds,
        SemaphoreSlim                   sem,
        CancellationToken               ct)
    {
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
                found = await tmdb.FindByExternalIdAsync(extId, source, ct).ConfigureAwait(false);
            }
            finally { sem.Release(); }

            if (found?.TvResults is { Count: > 0 } hits)
                return hits[0].Id.ToString();
        }

        return null;
    }

    private static bool HasAired(string? dateStr, DateTime today)
    {
        if (string.IsNullOrEmpty(dateStr)) return false;
        return DateTime.TryParse(dateStr, out var d) && d.Date <= today;
    }

    // Returns true if the air date is present AND between [cutoff, today] inclusive.
    // Used by the "recently aired only" filter; items with missing dates are
    // excluded because we can't place them in the window.
    private static bool AirDateWithin(string? dateStr, DateTime cutoff, DateTime today)
    {
        if (string.IsNullOrEmpty(dateStr)) return false;
        if (!DateTime.TryParse(dateStr, out var d)) return false;
        return d.Date >= cutoff && d.Date <= today;
    }
}
