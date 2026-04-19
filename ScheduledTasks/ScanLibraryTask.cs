using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MissingMediaChecker.Configuration;
using Jellyfin.Plugin.MissingMediaChecker.Models;
using Jellyfin.Plugin.MissingMediaChecker.Services;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MissingMediaChecker.ScheduledTasks;

public class ScanLibraryTask : IScheduledTask
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented        = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    // Shared state polled by the API controller.
    internal static ScanResults?     LastResults    { get; private set; }
    internal static DateTimeOffset?  LastRunAt      { get; private set; }
    internal static bool             IsScanning     { get; private set; }
    internal static double           ProgressPct    { get; private set; }
    internal static string           ProgressMsg    { get; private set; } = string.Empty;

    private readonly ILibraryManager         _library;
    private readonly ILogger<ScanLibraryTask> _logger;

    public ScanLibraryTask(ILibraryManager library, ILogger<ScanLibraryTask> logger)
    {
        _library = library;
        _logger  = logger;
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
        ProgressPct = 0;
        ProgressMsg = "Starting…";

        try
        {
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
                composite,
                ct).ConfigureAwait(false);

            Commit(results);
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
