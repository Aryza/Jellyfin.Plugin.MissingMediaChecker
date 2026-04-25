using Jellyfin.Plugin.MissingMediaChecker.HomeSections;
using Jellyfin.Plugin.MissingMediaChecker.Web;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.MissingMediaChecker;

/// <summary>
/// Plugin DI wiring. Two responsibilities:
///
///   1. Register the home-pill ASP.NET middleware + startup filter so the
///      script tag lands in index.html.
///   2. Register the three home-screen section result handlers (resolved via
///      <see cref="Microsoft.Extensions.DependencyInjection.ActivatorUtilities"/>
///      when the Home Screen Sections plugin invokes us by reflection) and the
///      entry point that registers the sections with that plugin on startup.
///
/// Home Screen Sections (IAmParadox27) is an optional runtime dependency — if
/// its assembly isn't loaded, <see cref="SectionRegistrar"/> logs and exits.
/// </summary>
public sealed class ServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection services, IServerApplicationHost applicationHost)
    {
        services.AddTransient<ScriptInjectionMiddleware>();
        services.AddTransient<IStartupFilter, MmcStartupFilter>();

        services.AddTransient<TrendingSectionHandler>();

        services.AddHostedService<SectionRegistrar>();
    }
}
