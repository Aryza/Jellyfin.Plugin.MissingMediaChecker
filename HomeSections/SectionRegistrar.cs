using System;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;

namespace Jellyfin.Plugin.MissingMediaChecker.HomeSections;

/// <summary>
/// Registers our three home-screen section handlers with IAmParadox27's
/// Home Screen Sections (HSS) plugin on server startup.
///
/// HSS can't be referenced at compile time because Jellyfin loads plugins into
/// separate AssemblyLoadContexts — a direct reference would resolve against a
/// different instance of the assembly than the one that's actually loaded.
/// Instead we probe every load context for the HSS assembly, find its static
/// <c>PluginInterface.RegisterSection</c>, and invoke it with a JObject
/// payload. If HSS isn't installed we log once and no-op; the rest of the
/// plugin (scanner, pill) still works.
///
/// Runs as an <see cref="IHostedService"/> so registration happens after
/// Jellyfin has built the host and loaded every plugin's assembly — earlier
/// hooks (e.g. plugin ctor) fire before HSS is available.
/// </summary>
public sealed class SectionRegistrar : IHostedService
{
    private readonly ILogger<SectionRegistrar> _logger;

    public SectionRegistrar(ILogger<SectionRegistrar> logger)
    {
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        try { Register(); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MissingMediaChecker: home-section registration failed");
        }
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private void Register()
    {
        var hss = AssemblyLoadContext.All
            .SelectMany(c => c.Assemblies)
            .FirstOrDefault(a => a.FullName?.Contains(".HomeScreenSections", StringComparison.Ordinal) == true);
        if (hss is null)
        {
            _logger.LogInformation("MissingMediaChecker: Home Screen Sections plugin not installed — sections skipped.");
            return;
        }

        var pluginInterface = hss.GetType("Jellyfin.Plugin.HomeScreenSections.PluginInterface");
        var registerMethod  = pluginInterface?.GetMethod("RegisterSection");
        if (registerMethod is null)
        {
            _logger.LogWarning("MissingMediaChecker: HSS PluginInterface.RegisterSection not found — version mismatch?");
            return;
        }

        var cfg = Plugin.Instance?.Configuration;
        if (cfg is null) return;

        var selfAssembly = GetType().Assembly.FullName;

        if (cfg.EnableTrendingChannel)
            Invoke(registerMethod, selfAssembly, "mmc-trending",      cfg.TrendingChannelName,       typeof(TrendingSectionHandler).FullName!);
        if (cfg.EnableRecentMoviesChannel)
            Invoke(registerMethod, selfAssembly, "mmc-recent-movies", cfg.RecentMoviesChannelName,   typeof(RecentMoviesSectionHandler).FullName!);
        if (cfg.EnableRecentEpisodesChannel)
            Invoke(registerMethod, selfAssembly, "mmc-recent-eps",    cfg.RecentEpisodesChannelName, typeof(RecentEpisodesSectionHandler).FullName!);
    }

    private void Invoke(MethodInfo register, string? selfAssembly, string id, string title, string handlerClass)
    {
        var payload = new JObject
        {
            ["id"]              = id,
            ["displayText"]     = title,
            ["limit"]           = 1,
            ["route"]           = null,
            ["additionalData"]  = null,
            ["resultsAssembly"] = selfAssembly,
            ["resultsClass"]    = handlerClass,
            ["resultsMethod"]   = "GetResults"
        };
        register.Invoke(null, new object?[] { payload });
        _logger.LogInformation("MissingMediaChecker: registered HSS section {Id} → {Title}", id, title);
    }
}
