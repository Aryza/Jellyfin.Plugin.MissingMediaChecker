using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.MissingMediaChecker.Configuration;
using Jellyfin.Plugin.MissingMediaChecker.Models;
using Jellyfin.Plugin.MissingMediaChecker.Services;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Activity;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.MissingMediaChecker.ScheduledTasks;

public class ScanLibraryTask : IScheduledTask
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented               = false,
        PropertyNamingPolicy        = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    // Shared state polled by the API controller.
    internal static ScanResults?     LastResults    { get; private set; }
    internal static DateTimeOffset?  LastRunAt      { get; private set; }
    internal static bool             IsScanning     { get; private set; }
    internal static double           ProgressPct    { get; private set; }
    internal static string           ProgressMsg    { get; private set; } = string.Empty;
    internal static string?          LastError      { get; private set; }

    private readonly ILibraryManager          _library;
    private readonly ILogger<ScanLibraryTask> _logger;
    private readonly IActivityManager?        _activityManager;

    // Jellyfin's DI will hand us the activity manager when registering the
    // scheduled task. The controller-side fire-and-forget path creates
    // instances without DI, so activityManager is optional.
    public ScanLibraryTask(
        ILibraryManager library,
        ILogger<ScanLibraryTask> logger,
        IActivityManager? activityManager = null)
    {
        _library         = library;
        _logger          = logger;
        _activityManager = activityManager;
        TryLoadResults();
    }

    // ── IScheduledTask ────────────────────────────────────────────────────────

    public string Name        => "Check for Missing Media";
    public string Key         => "MissingMediaCheckerScan";
    public string Description => "Scans TV series and movie collections for missing content via TMDB.";
    public string Category    => "Missing Media Checker";

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken ct)
    {
        var cfg = Plugin.Instance?.Configuration ?? new PluginConfiguration();

        if (string.IsNullOrWhiteSpace(cfg.TmdbApiKey))
        {
            _logger.LogWarning("MissingMediaChecker: TMDB API key is not set — scan skipped.");
            return;
        }

        IsScanning  = true;
        LastError   = null;
        ProgressPct = 0;
        ProgressMsg = "Starting…";

        try
        {
            // Load prior results and incremental cache for diff + smart-scan.
            var previousResults    = LastResults;
            var ignoreList         = IgnoreListStore.Load(Plugin.IgnoreListPath);
            var incrementalCache   = IncrementalCacheStore.Load(Plugin.IncrementalCachePath);

            var detector = new MissingMediaDetector(_library, _logger);
            var composite = new Progress<(double pct, string msg)>(p =>
            {
                ProgressPct = p.pct;
                ProgressMsg = p.msg;
                progress.Report(p.pct);
            });

            var results = await detector.DetectAsync(
                cfg.TmdbApiKey,
                cfg.CheckTvSeries,
                cfg.CheckMovieCollections,
                cfg.IncludeSpecials,
                cfg.SkipUnairedEpisodes,
                cfg.SkipFutureMovies,
                cfg.RecentlyAiredOnly,
                cfg.RecentlyAiredDays,
                cfg.EnableIncrementalScan,
                cfg.IncrementalMinAgeHours,
                previousResults,
                ignoreList,
                incrementalCache,
                composite,
                ct).ConfigureAwait(false);

            Commit(results);
            IncrementalCacheStore.Save(Plugin.IncrementalCachePath, incrementalCache);

            // Feature 28: fire a Jellyfin activity entry when something new
            // showed up. Suppressed when nothing changed to avoid log spam.
            if (cfg.EnableNotifications &&
                _activityManager is not null &&
                (results.NewMissingEpisodes + results.NewMissingMovies) > 0)
            {
                try
                {
                    var entry = new ActivityLog(
                        name: "Missing Media Checker",
                        type: "PluginNotification",
                        userId: Guid.Empty)
                    {
                        Overview      = $"{results.NewMissingEpisodes} new missing episode(s), {results.NewMissingMovies} new missing movie(s).",
                        ShortOverview = $"+{results.NewMissingEpisodes} ep / +{results.NewMissingMovies} mov",
                    };
                    await _activityManager.CreateAsync(entry).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "MissingMediaChecker: activity notification failed (non-fatal)");
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LastError = ex.Message;
            _logger.LogError(ex, "MissingMediaChecker scan failed");
            throw;
        }
        finally
        {
            IsScanning  = false;
            ProgressPct = 100;
        }
    }

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => new[]
    {
        new TaskTriggerInfo
        {
            Type           = TaskTriggerInfoType.WeeklyTrigger,
            DayOfWeek      = DayOfWeek.Sunday,
            TimeOfDayTicks = TimeSpan.FromHours(4).Ticks
        }
    };

    // ── persistence ───────────────────────────────────────────────────────────

    private static void Commit(ScanResults results)
    {
        // Rotate: move current results.json → previous_results.json before
        // overwriting. Lets the next scan compute the IsNew diff and lets the
        // UI show a "since last scan" view.
        try
        {
            if (File.Exists(Plugin.ResultsPath))
            {
                File.Copy(Plugin.ResultsPath, Plugin.PreviousResultsPath, overwrite: true);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"MissingMediaChecker: failed to rotate previous results — {ex.Message}");
        }

        LastResults = results;
        LastRunAt   = results.ScanTime;
        try
        {
            File.WriteAllText(Plugin.ResultsPath, JsonSerializer.Serialize(results, JsonOpts));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"MissingMediaChecker: failed to save results — {ex.Message}");
        }
    }

    private static void TryLoadResults()
    {
        try
        {
            if (!File.Exists(Plugin.ResultsPath)) return;
            var r = JsonSerializer.Deserialize<ScanResults>(
                File.ReadAllText(Plugin.ResultsPath), JsonOpts);
            if (r is not null) { LastResults = r; LastRunAt = r.ScanTime; }
        }
        catch { /* non-fatal */ }
    }
}
