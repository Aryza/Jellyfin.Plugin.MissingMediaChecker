using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.MissingMediaChecker.Configuration;
using Jellyfin.Plugin.MissingMediaChecker.Models;
using Jellyfin.Plugin.MissingMediaChecker.ScheduledTasks;
using Jellyfin.Plugin.MissingMediaChecker.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Activity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MissingMediaChecker.Api;

[ApiController]
[Route("MissingMedia")]
[Authorize(Policy = "RequiresElevation")]
public class MissingMediaController : ControllerBase
{
    private readonly ILibraryManager    _library;
    private readonly ILoggerFactory     _loggerFactory;
    private readonly IActivityManager?  _activityManager;

    public MissingMediaController(
        ILibraryManager library,
        ILoggerFactory loggerFactory,
        IActivityManager? activityManager = null)
    {
        _library         = library;
        _loggerFactory   = loggerFactory;
        _activityManager = activityManager;
    }

    // ── GET /MissingMedia/Results ─────────────────────────────────────────────
    // Retained for back-compat: returns the whole result document. Large for
    // big libraries; prefer /Results/Summary + /Results/Groups for new callers.
    [HttpGet("Results")]
    public ActionResult<ScanResults?> GetResults()
        => Ok(ScanLibraryTask.LastResults);

    // ── GET /MissingMedia/Results/Summary (feature 26) ────────────────────────
    // Only the top-level counters and the skipped-series list. No per-group
    // episode/movie arrays. Cheap to serialise; UI boots on this.
    [HttpGet("Results/Summary")]
    public ActionResult GetResultsSummary()
    {
        var r = ScanLibraryTask.LastResults;
        if (r is null) return Ok(new { hasResults = false });

        return Ok(new
        {
            hasResults              = true,
            scanTime                = r.ScanTime,
            totalSeriesInLibrary    = r.TotalSeriesInLibrary,
            totalSeriesChecked      = r.TotalSeriesChecked,
            seriesSkippedNoId       = r.SeriesSkippedNoId,
            totalCollectionsChecked = r.TotalCollectionsChecked,
            seriesWithMissing       = r.SeriesWithMissing,
            collectionsWithMissing  = r.CollectionsWithMissing,
            totalMissingEpisodes    = r.TotalMissingEpisodes,
            totalMissingMovies      = r.TotalMissingMovies,
            newMissingEpisodes      = r.NewMissingEpisodes,
            newMissingMovies        = r.NewMissingMovies,
            incrementalCacheHits    = r.IncrementalCacheHits,
            skippedSeries           = r.SkippedSeries,
            totalGroups             = r.MissingSeries.Count + r.MissingCollections.Count
        });
    }

    // ── GET /MissingMedia/Results/Groups (feature 26) ─────────────────────────
    // Paginated + filtered + sorted group list. Query params:
    //   type=tv|movie|all, search=<substring>, sort=count-desc|count-asc|
    //   name-asc|name-desc|date-desc|date-asc|new-desc,
    //   page=1, pageSize=50.
    [HttpGet("Results/Groups")]
    public ActionResult GetResultsGroups(
        [FromQuery] string? type     = "all",
        [FromQuery] string? search   = null,
        [FromQuery] string? sort     = "count-desc",
        [FromQuery] int     page     = 1,
        [FromQuery] int     pageSize = 50)
    {
        var r = ScanLibraryTask.LastResults;
        if (r is null) return Ok(new { total = 0, page, pageSize, groups = Array.Empty<object>() });

        var typeF = (type ?? "all").ToLowerInvariant();
        var q     = (search ?? string.Empty).Trim();

        // Project each group into a uniform shape so filtering/sorting is easy.
        var all = new List<GroupEnvelope>(r.MissingSeries.Count + r.MissingCollections.Count);
        if (typeF is "all" or "tv")
        {
            foreach (var s in r.MissingSeries)
                all.Add(new GroupEnvelope(
                    "tv", s.SeriesName, s.MissingCount, s.NewMissingCount,
                    LatestDate(s.MissingEpisodes.Select(e => e.AirDate)), s, null));
        }
        if (typeF is "all" or "movie")
        {
            foreach (var c in r.MissingCollections)
                all.Add(new GroupEnvelope(
                    "movie", c.CollectionName, c.MissingCount, c.NewMissingCount,
                    LatestDate(c.MissingMovies.Select(m => m.ReleaseDate)), null, c));
        }

        if (!string.IsNullOrEmpty(q))
            all = all.Where(g => g.Name.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();

        switch ((sort ?? "count-desc").ToLowerInvariant())
        {
            case "count-asc":  all.Sort((a, b) => a.Count.CompareTo(b.Count));              break;
            case "count-desc": all.Sort((a, b) => b.Count.CompareTo(a.Count));              break;
            case "name-asc":   all.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase)); break;
            case "name-desc":  all.Sort((a, b) => string.Compare(b.Name, a.Name, StringComparison.CurrentCultureIgnoreCase)); break;
            case "date-asc":   all.Sort((a, b) => a.LatestDate.CompareTo(b.LatestDate));    break;
            case "date-desc":  all.Sort((a, b) => b.LatestDate.CompareTo(a.LatestDate));    break;
            case "new-desc":   all.Sort((a, b) => b.NewCount.CompareTo(a.NewCount));        break;
            default:           all.Sort((a, b) => b.Count.CompareTo(a.Count));              break;
        }

        page     = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 500);
        int start = (page - 1) * pageSize;

        var slice = all.Skip(start).Take(pageSize).Select(g => new
        {
            type       = g.Type,
            name       = g.Name,
            count      = g.Count,
            newCount   = g.NewCount,
            latestDate = g.LatestDate,
            series     = g.Series,
            collection = g.Collection
        }).ToList();

        return Ok(new
        {
            total    = all.Count,
            page,
            pageSize,
            groups   = slice
        });
    }

    private record GroupEnvelope(
        string Type,
        string Name,
        int Count,
        int NewCount,
        DateTime LatestDate,
        SeriesMissingReport? Series,
        CollectionMissingReport? Collection);

    private static DateTime LatestDate(IEnumerable<string?> dates)
    {
        DateTime best = DateTime.MinValue;
        foreach (var s in dates)
        {
            if (string.IsNullOrEmpty(s)) continue;
            if (DateTime.TryParse(s, out var d) && d > best) best = d;
        }
        return best;
    }

    // ── Ignore list endpoints (feature 14) ────────────────────────────────────

    [HttpGet("Ignores")]
    public ActionResult<IgnoreList> GetIgnores()
        => Ok(IgnoreListStore.Load(Plugin.IgnoreListPath));

    [HttpPut("Ignores")]
    public ActionResult PutIgnores([FromBody] IgnoreList list)
    {
        if (list is null) return BadRequest();
        IgnoreListStore.Save(Plugin.IgnoreListPath, list);
        return Ok(list);
    }

    public record AddIgnoreRequest(string Kind, string? TmdbSeriesId, int? SeasonNumber,
                                   int? EpisodeNumber, int? TmdbEpisodeId,
                                   int? TmdbCollectionId, int? TmdbMovieId,
                                   string? Name);

    [HttpPost("Ignores/Add")]
    public ActionResult AddIgnore([FromBody] AddIgnoreRequest req)
    {
        if (req is null) return BadRequest();
        var list = IgnoreListStore.Load(Plugin.IgnoreListPath);
        var kind = req.Kind?.ToLowerInvariant();

        switch (kind)
        {
            case "series":
                if (string.IsNullOrEmpty(req.TmdbSeriesId)) return BadRequest("tmdbSeriesId required");
                list.SeriesTmdbIds.Add(req.TmdbSeriesId);
                if (!string.IsNullOrWhiteSpace(req.Name))
                    list.Labels[IgnoreList.LabelKey("series", req.TmdbSeriesId)] = req.Name!;
                break;
            case "season":
                if (string.IsNullOrEmpty(req.TmdbSeriesId) || req.SeasonNumber is null)
                    return BadRequest("tmdbSeriesId + seasonNumber required");
                if (!list.Seasons.TryGetValue(req.TmdbSeriesId, out var seasons))
                    list.Seasons[req.TmdbSeriesId] = seasons = new HashSet<int>();
                seasons.Add(req.SeasonNumber.Value);
                if (!string.IsNullOrWhiteSpace(req.Name))
                    list.Labels[IgnoreList.LabelKey("season", req.TmdbSeriesId, req.SeasonNumber.Value.ToString())] = req.Name!;
                break;
            case "episode":
                if (string.IsNullOrEmpty(req.TmdbSeriesId) ||
                    req.SeasonNumber is null || req.EpisodeNumber is null)
                    return BadRequest("tmdbSeriesId + seasonNumber + episodeNumber required");
                if (!list.Episodes.TryGetValue(req.TmdbSeriesId, out var eps))
                    list.Episodes[req.TmdbSeriesId] = eps = new HashSet<string>();
                var epKey = IgnoreList.Key(req.SeasonNumber.Value, req.EpisodeNumber.Value);
                eps.Add(epKey);
                if (!string.IsNullOrWhiteSpace(req.Name))
                    list.Labels[IgnoreList.LabelKey("episode", req.TmdbSeriesId, epKey)] = req.Name!;
                break;
            case "collection":
                if (req.TmdbCollectionId is null) return BadRequest("tmdbCollectionId required");
                list.CollectionTmdbIds.Add(req.TmdbCollectionId.Value);
                if (!string.IsNullOrWhiteSpace(req.Name))
                    list.Labels[IgnoreList.LabelKey("collection", req.TmdbCollectionId.Value.ToString())] = req.Name!;
                break;
            case "movie":
                if (req.TmdbMovieId is null) return BadRequest("tmdbMovieId required");
                list.MovieTmdbIds.Add(req.TmdbMovieId.Value);
                if (!string.IsNullOrWhiteSpace(req.Name))
                    list.Labels[IgnoreList.LabelKey("movie", req.TmdbMovieId.Value.ToString())] = req.Name!;
                break;
            default:
                return BadRequest("kind must be series|season|episode|collection|movie");
        }

        IgnoreListStore.Save(Plugin.IgnoreListPath, list);
        return Ok(list);
    }

    [HttpPost("Ignores/Remove")]
    public ActionResult RemoveIgnore([FromBody] AddIgnoreRequest req)
    {
        if (req is null) return BadRequest();
        var list = IgnoreListStore.Load(Plugin.IgnoreListPath);

        switch (req.Kind?.ToLowerInvariant())
        {
            case "series":
                if (!string.IsNullOrEmpty(req.TmdbSeriesId))
                {
                    list.SeriesTmdbIds.Remove(req.TmdbSeriesId);
                    list.Labels.Remove(IgnoreList.LabelKey("series", req.TmdbSeriesId));
                }
                break;
            case "season":
                if (!string.IsNullOrEmpty(req.TmdbSeriesId) && req.SeasonNumber is not null
                    && list.Seasons.TryGetValue(req.TmdbSeriesId, out var seasons))
                {
                    seasons.Remove(req.SeasonNumber.Value);
                    list.Labels.Remove(IgnoreList.LabelKey("season", req.TmdbSeriesId, req.SeasonNumber.Value.ToString()));
                }
                break;
            case "episode":
                if (!string.IsNullOrEmpty(req.TmdbSeriesId)
                    && req.SeasonNumber is not null && req.EpisodeNumber is not null
                    && list.Episodes.TryGetValue(req.TmdbSeriesId, out var eps))
                {
                    var epKey = IgnoreList.Key(req.SeasonNumber.Value, req.EpisodeNumber.Value);
                    eps.Remove(epKey);
                    list.Labels.Remove(IgnoreList.LabelKey("episode", req.TmdbSeriesId, epKey));
                }
                break;
            case "collection":
                if (req.TmdbCollectionId is not null)
                {
                    list.CollectionTmdbIds.Remove(req.TmdbCollectionId.Value);
                    list.Labels.Remove(IgnoreList.LabelKey("collection", req.TmdbCollectionId.Value.ToString()));
                }
                break;
            case "movie":
                if (req.TmdbMovieId is not null)
                {
                    list.MovieTmdbIds.Remove(req.TmdbMovieId.Value);
                    list.Labels.Remove(IgnoreList.LabelKey("movie", req.TmdbMovieId.Value.ToString()));
                }
                break;
        }

        IgnoreListStore.Save(Plugin.IgnoreListPath, list);
        return Ok(list);
    }

    // ── GET /MissingMedia/Status ──────────────────────────────────────────────

    [HttpGet("Status")]
    public ActionResult GetStatus()
        => Ok(new
        {
            isScanning      = ScanLibraryTask.IsScanning,
            percentComplete = ScanLibraryTask.ProgressPct,
            message         = ScanLibraryTask.ProgressMsg,
            lastRunAt       = ScanLibraryTask.LastRunAt?.ToString("O"),
            lastError       = ScanLibraryTask.LastError
        });

    // ── GET /MissingMedia/Debug ───────────────────────────────────────────────

    [HttpGet("Debug")]
    public ActionResult GetDebug()
    {
        var cfg = Plugin.Instance?.Configuration ?? new PluginConfiguration();

        var seriesCount = _library.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.Series },
            Recursive        = true
        }).Count;

        var movieCount = _library.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.Movie },
            Recursive        = true,
            IsVirtualItem    = false
        }).Count;

        var episodeCount = _library.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.Episode },
            Recursive        = true,
            IsVirtualItem    = false
        }).Count;

        return Ok(new
        {
            pluginInstanceLoaded    = Plugin.Instance is not null,
            apiKeyConfigured        = !string.IsNullOrWhiteSpace(cfg.TmdbApiKey),
            checkTvSeries           = cfg.CheckTvSeries,
            checkMovieCollections   = cfg.CheckMovieCollections,
            seriesInLibrary         = seriesCount,
            moviesInLibrary         = movieCount,
            physicalEpisodesInLibrary = episodeCount,
            lastScanTime            = ScanLibraryTask.LastRunAt?.ToString("O"),
            lastError               = ScanLibraryTask.LastError
        });
    }

    // ── GET /MissingMedia/Calendar ────────────────────────────────────────────
    // Upcoming next-episode-to-air for owned series + future-release movies in
    // owned BoxSet collections. Result is cached (CalendarCacheMinutes) so the
    // typical UI refresh is free; pass refresh=true to force a TMDB rescan.
    [HttpGet("Calendar")]
    public async Task<ActionResult> GetCalendar(
        [FromQuery] int? days = null,
        [FromQuery] bool refresh = false,
        CancellationToken ct = default)
    {
        var cfg = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        if (string.IsNullOrWhiteSpace(cfg.TmdbApiKey))
            return BadRequest(new { message = "TMDB API key is not configured. Set it in plugin settings first." });

        var lookahead = Math.Clamp(days ?? cfg.CalendarLookaheadDays, 1, 730);
        var ttl       = TimeSpan.FromMinutes(Math.Max(1, cfg.CalendarCacheMinutes));

        var svc = new CalendarService(_library, _loggerFactory.CreateLogger<CalendarService>());
        var result = await svc.GetAsync(cfg.TmdbApiKey, lookahead, ttl, refresh, ct).ConfigureAwait(false);
        return Ok(result ?? new CalendarResponse { LookaheadDays = lookahead });
    }

    // ── POST /MissingMedia/Scan ───────────────────────────────────────────────

    [HttpPost("Scan")]
    public ActionResult TriggerScan()
    {
        if (ScanLibraryTask.IsScanning)
            return Ok(new { message = "A scan is already in progress." });

        var cfg = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        if (string.IsNullOrWhiteSpace(cfg.TmdbApiKey))
            return BadRequest(new { message = "TMDB API key is not configured. Set it in plugin settings first." });

        var taskLogger = _loggerFactory.CreateLogger<ScanLibraryTask>();
        var am         = _activityManager;
        _ = Task.Run(async () =>
        {
            var task = new ScanLibraryTask(_library, taskLogger, am);
            try
            {
                await task.ExecuteAsync(new Progress<double>(), CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                taskLogger.LogError(ex, "MissingMediaChecker background scan failed");
            }
        });

        return Ok(new { message = "Scan started." });
    }
}
