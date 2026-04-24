using Jellyfin.Plugin.MissingMediaChecker.Channels;
using Jellyfin.Plugin.MissingMediaChecker.Web;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Plugins;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.MissingMediaChecker;

/// <summary>
/// Plugin-level DI registration. Jellyfin discovers IChannel implementations
/// via assembly scan, but the home-pill ASP.NET middleware has to be wired
/// in explicitly — that's what <see cref="MmcStartupFilter"/> does.
///
/// Channels are also registered as concrete <see cref="IChannel"/> entries to
/// guarantee they're constructable via DI even if Jellyfin's discovery path
/// changes between minor versions.
/// </summary>
public sealed class ServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection services, IServerApplicationHost applicationHost)
    {
        services.AddSingleton<IChannel, TrendingInLibraryChannel>();
        services.AddSingleton<IChannel, RecentMoviesChannel>();
        services.AddSingleton<IChannel, RecentEpisodesChannel>();
        services.AddSingleton<IChannel, UpcomingEpisodesChannel>();

        services.AddTransient<ScriptInjectionMiddleware>();
        services.AddTransient<IStartupFilter, MmcStartupFilter>();
    }
}
