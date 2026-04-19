using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MissingMediaChecker.Configuration;
using Jellyfin.Plugin.MissingMediaChecker.Models;
using Jellyfin.Plugin.MissingMediaChecker.ScheduledTasks;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MissingMediaChecker.Api;

[ApiController]
[Route("MissingMedia")]
[Authorize(Policy = "RequiresElevation")]
public class MissingMediaController : ControllerBase
{
    private readonly ILibraryManager   _library;
    private readonly ILoggerFactory    _loggerFactory;

    public MissingMediaController(
        ILibraryManager library,
        ILoggerFactory loggerFactory)
    {
        _library       = library;
        _loggerFactory = loggerFactory;
    }

    // ── GET /MissingMedia/Results ─────────────────────────────────────────────

    [HttpGet("Results")]
    public ActionResult<ScanResults?> GetResults()
        => Ok(ScanLibraryTask.LastResults);

    // ── GET /MissingMedia/Status ──────────────────────────────────────────────

    [HttpGet("Status")]
    public ActionResult GetStatus()
        => Ok(new
        {
            isScanning      = ScanLibraryTask.IsScanning,
            percentComplete = ScanLibraryTask.ProgressPct,
            message         = ScanLibraryTask.ProgressMsg,
            lastRunAt       = ScanLibraryTask.LastRunAt?.ToString("O")
        });

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
        _ = Task.Run(async () =>
        {
            var task = new ScanLibraryTask(_library, taskLogger);
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
